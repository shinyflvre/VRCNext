using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;

namespace VRCNext.Services;

public class UnifiedTimeEngine : IDisposable
{

    public class UserRecord
    {
        public long TotalSeconds { get; set; }
        public string LastSeen { get; set; } = "";
        public string LastSeenLocation { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Image { get; set; } = "";
    }

    public class WorldRecord
    {
        public long TotalSeconds { get; set; }
        public string LastVisited { get; set; } = "";
        public int VisitCount { get; set; }
        public string WorldName  { get; set; } = "";
        public string WorldThumb { get; set; } = "";
    }

    public Dictionary<string, UserRecord> Users { get; } = new();
    public Dictionary<string, WorldRecord> Worlds { get; } = new();
    private readonly Dictionary<string, DateTime> _playerSessions = new();
    private DateTime? _worldSessionStart;
    private string _currentWorldId = "";
    private string _currentLocation = "";

    private readonly SqliteConnection _db;
    private readonly object _lock = new();
    private System.Threading.Timer? _watchdogTimer;  
    private Func<bool>? _isVrcRunning;
    private bool _disposed;
    private bool _vrcWasRunning; 
    public Action? OnVrcClosed;
    private Process? _monitoredVrcProcess; 
    private DateTime _lastVrcAliveUtc; 
    private DateTime _lastFlushUtc = DateTime.MinValue; 
    private Action<string>? _logger; 

    private static readonly string UserLegacyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "user_tracking.json");
    private static readonly string WorldLegacyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "world_tracking.json");

    private UnifiedTimeEngine(SqliteConnection db) { _db = db; }

    public static UnifiedTimeEngine Load(Func<bool>? isVrcRunning = null, Action<string>? logger = null)
    {
        var conn = Database.OpenConnection();
        var engine = new UnifiedTimeEngine(conn);
        engine._isVrcRunning = isVrcRunning;
        engine._logger = logger;
        engine.InitSchema();
        engine.MigrateUsersFromJson();
        engine.MigrateWorldsFromJson();
        engine.LoadUsersFromDb();
        engine.LoadWorldsFromDb();
        engine._watchdogTimer = new System.Threading.Timer(
            engine.WatchdogTick, null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        return engine;
    }

    // Core event methods
    public void OnWorldJoined(string worldId, string location)
    {
        lock (_lock)
        {
            if (_disposed) return;
            var now = DateTime.UtcNow;

            EndAllPlayerSessionsLocked(now);
            EndWorldSessionLocked(now);

            _currentWorldId = worldId ?? "";
            _currentLocation = location ?? "";

            if (!string.IsNullOrEmpty(_currentWorldId) && _currentWorldId.StartsWith("wrld_"))
            {
                _worldSessionStart = now;
                if (!Worlds.TryGetValue(_currentWorldId, out var rec))
                {
                    rec = new WorldRecord();
                    Worlds[_currentWorldId] = rec;
                }
                rec.VisitCount++;
                rec.LastVisited = now.ToString("o");
                UpsertWorldLocked(_currentWorldId, rec);
            }

            PersistActiveSessionLocked();
        }
    }

    public void OnWorldResumed(string worldId, string location)
    {
        lock (_lock)
        {
            _currentWorldId = worldId ?? "";
            _currentLocation = location ?? "";
            if (!_worldSessionStart.HasValue)
                _worldSessionStart = DateTime.UtcNow;
            PersistActiveSessionLocked();
        }
    }

    public void OnPlayerJoined(string userId, DateTime joinedAtUtc)
    {
        lock (_lock)
        {
            if (_disposed || string.IsNullOrEmpty(userId)) return;
            _playerSessions[userId] = joinedAtUtc;
            if (!Users.TryGetValue(userId, out _))
                Users[userId] = new UserRecord();
            PersistActiveSessionLocked();
        }
    }

    public void OnPlayerLeft(string userId)
    {
        lock (_lock)
        {
            if (_disposed || string.IsNullOrEmpty(userId)) return;
            if (!_playerSessions.TryGetValue(userId, out var sessionStart)) return;

            var now = DateTime.UtcNow;
            var delta = (long)(now - sessionStart).TotalSeconds;
            if (delta > 0 && delta <= 86400) // cap at 24h sanity check
            {
                if (Users.TryGetValue(userId, out var rec))
                {
                    rec.TotalSeconds += delta;
                    rec.LastSeen = now.ToString("o");
                    if (!string.IsNullOrEmpty(_currentLocation))
                        rec.LastSeenLocation = _currentLocation;
                }
            }
            _playerSessions.Remove(userId);
            PersistUserLocked(userId, now);
            PersistActiveSessionLocked();
        }
    }


    // Query methods
    public (long totalSeconds, string lastSeen) GetUserStats(string userId, bool isCoPresent = false)
    {
        lock (_lock)
        {
            if (!Users.TryGetValue(userId, out var rec))
                return (0, "");

            var total = rec.TotalSeconds;

            if (isCoPresent && _isVrcRunning?.Invoke() == true
                && _playerSessions.TryGetValue(userId, out var sessionStart))
            {
                var live = (long)(DateTime.UtcNow - sessionStart).TotalSeconds;
                if (live > 0 && live <= 86400)
                    total += live;
            }

            return (total, rec.LastSeen);
        }
    }
    public (long totalSeconds, int visitCount, string lastVisited) GetWorldStats(string worldId)
    {
        lock (_lock)
        {
            if (!Worlds.TryGetValue(worldId, out var rec))
                return (0, 0, "");

            var total = rec.TotalSeconds;

            if (worldId == _currentWorldId && _worldSessionStart.HasValue
                && _isVrcRunning?.Invoke() == true)
            {
                var live = (long)(DateTime.UtcNow - _worldSessionStart.Value).TotalSeconds;
                if (live > 0 && live <= 86400)
                    total += live;
            }

            return (total, rec.VisitCount, rec.LastVisited);
        }
    }

    // World detail cache
    public class WorldDetailCache
    {
        public string WorldName             { get; set; } = "";
        public string WorldThumb            { get; set; } = "";
        public string Description           { get; set; } = "";
        public string ImageUrl              { get; set; } = "";
        public string AuthorName            { get; set; } = "";
        public string AuthorId              { get; set; } = "";
        public string Published             { get; set; } = "";
        public string Updated               { get; set; } = "";
        public int    Capacity              { get; set; }
        public int    RecommendedCapacity   { get; set; }
        public List<string> Tags            { get; set; } = new();
        public int    Favorites             { get; set; }
        public int    Visits                { get; set; }
        public long   PcSize               { get; set; }
        public long   AndroidSize           { get; set; }
        public long   IosSize               { get; set; }
        public int    Heat                  { get; set; }
        public int    Popularity            { get; set; }
        public int    PublicOccupants       { get; set; }
        public int    PrivateOccupants      { get; set; }
        public int    Version               { get; set; }
        public long   TotalSeconds          { get; set; }
        public int    VisitCount            { get; set; }
        public string LastVisited           { get; set; } = "";
    }

    public WorldDetailCache? GetWorldDetail(string worldId)
    {
        if (string.IsNullOrEmpty(worldId)) return null;
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"SELECT world_name,world_thumb,world_description,world_image_url,
                    world_author_name,world_author_id,world_published,world_updated,
                    world_capacity,world_recommended_capacity,world_tags,
                    world_favorites,world_visits,world_pc_size,world_android_size,world_ios_size,
                    world_heat,world_popularity,world_public_occupants,world_private_occupants,world_version,
                    total_seconds,visit_count,last_visited,detail_cached_at
                    FROM world_tracking WHERE world_id=$wid";
                cmd.Parameters.AddWithValue("$wid", worldId);
                using var r = cmd.ExecuteReader();
                if (!r.Read() || string.IsNullOrEmpty(r.GetString(24))) return null;
                return new WorldDetailCache
                {
                    WorldName           = r.GetString(0),
                    WorldThumb          = r.GetString(1),
                    Description         = r.GetString(2),
                    ImageUrl            = r.GetString(3),
                    AuthorName          = r.GetString(4),
                    AuthorId            = r.GetString(5),
                    Published           = r.GetString(6),
                    Updated             = r.GetString(7),
                    Capacity            = r.GetInt32(8),
                    RecommendedCapacity = r.GetInt32(9),
                    Tags                = JsonConvert.DeserializeObject<List<string>>(r.GetString(10)) ?? new(),
                    Favorites           = r.GetInt32(11),
                    Visits              = r.GetInt32(12),
                    PcSize              = r.GetInt64(13),
                    AndroidSize         = r.GetInt64(14),
                    IosSize             = r.GetInt64(15),
                    Heat                = r.GetInt32(16),
                    Popularity          = r.GetInt32(17),
                    PublicOccupants     = r.GetInt32(18),
                    PrivateOccupants    = r.GetInt32(19),
                    Version             = r.GetInt32(20),
                    TotalSeconds        = r.GetInt64(21),
                    VisitCount          = r.GetInt32(22),
                    LastVisited         = r.GetString(23),
                };
            }
            catch { return null; }
        }
    }

    public void SaveWorldDetail(string worldId, string name, string thumb, string description,
        string imageUrl, string authorName, string authorId, string published, string updated,
        int capacity, int recommendedCapacity, List<string> tags,
        int favorites, int visits, long pcSize, long androidSize, long iosSize,
        int heat, int popularity, int publicOccupants, int privateOccupants, int version)
    {
        if (string.IsNullOrEmpty(worldId)) return;
        var tagsJson = JsonConvert.SerializeObject(tags);
        var now      = DateTime.UtcNow.ToString("o");
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"INSERT INTO world_tracking(world_id,world_name,world_thumb,world_description,world_image_url,
                    world_author_name,world_author_id,world_published,world_updated,world_capacity,
                    world_recommended_capacity,world_tags,world_favorites,world_visits,world_pc_size,world_android_size,world_ios_size,
                    world_heat,world_popularity,world_public_occupants,world_private_occupants,world_version,detail_cached_at)
                    VALUES($wid,$wn,$wt,$desc,$img,$an,$ai,$pub,$upd,$cap,$rcap,$tags,$fav,$vis,$pcs,$ands,$ioss,$heat,$pop,$pubocc,$privocc,$ver,$cat)
                    ON CONFLICT(world_id) DO UPDATE SET
                        world_name=excluded.world_name, world_thumb=excluded.world_thumb,
                        world_description=excluded.world_description, world_image_url=excluded.world_image_url,
                        world_author_name=excluded.world_author_name, world_author_id=excluded.world_author_id,
                        world_published=excluded.world_published, world_updated=excluded.world_updated,
                        world_capacity=excluded.world_capacity, world_recommended_capacity=excluded.world_recommended_capacity,
                        world_tags=excluded.world_tags, world_favorites=excluded.world_favorites,
                        world_visits=excluded.world_visits, world_pc_size=excluded.world_pc_size,
                        world_android_size=excluded.world_android_size,
                        world_ios_size=excluded.world_ios_size,
                        world_heat=excluded.world_heat, world_popularity=excluded.world_popularity,
                        world_public_occupants=excluded.world_public_occupants,
                        world_private_occupants=excluded.world_private_occupants,
                        world_version=excluded.world_version,
                        detail_cached_at=excluded.detail_cached_at";
                cmd.Parameters.AddWithValue("$wid",     worldId);
                cmd.Parameters.AddWithValue("$wn",      name);
                cmd.Parameters.AddWithValue("$wt",      thumb);
                cmd.Parameters.AddWithValue("$desc",    description);
                cmd.Parameters.AddWithValue("$img",     imageUrl);
                cmd.Parameters.AddWithValue("$an",      authorName);
                cmd.Parameters.AddWithValue("$ai",      authorId);
                cmd.Parameters.AddWithValue("$pub",     published);
                cmd.Parameters.AddWithValue("$upd",     updated);
                cmd.Parameters.AddWithValue("$cap",     capacity);
                cmd.Parameters.AddWithValue("$rcap",    recommendedCapacity);
                cmd.Parameters.AddWithValue("$tags",    tagsJson);
                cmd.Parameters.AddWithValue("$fav",     favorites);
                cmd.Parameters.AddWithValue("$vis",     visits);
                cmd.Parameters.AddWithValue("$pcs",     pcSize);
                cmd.Parameters.AddWithValue("$ands",    androidSize);
                cmd.Parameters.AddWithValue("$ioss",    iosSize);
                cmd.Parameters.AddWithValue("$heat",    heat);
                cmd.Parameters.AddWithValue("$pop",     popularity);
                cmd.Parameters.AddWithValue("$pubocc",  publicOccupants);
                cmd.Parameters.AddWithValue("$privocc", privateOccupants);
                cmd.Parameters.AddWithValue("$ver",     version);
                cmd.Parameters.AddWithValue("$cat",     now);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }

    // Group detail cache

    public class GroupDetailCache
    {
        public string Name            { get; set; } = "";
        public string ShortCode       { get; set; } = "";
        public string Description     { get; set; } = "";
        public string IconUrl         { get; set; } = "";
        public string BannerUrl       { get; set; } = "";
        public int    MemberCount     { get; set; }
        public string Privacy         { get; set; } = "";
        public string JoinState       { get; set; } = "";
        public string OwnerId         { get; set; } = "";
        public string OwnerName       { get; set; } = "";
        public string Rules           { get; set; } = "";
        public List<string> Languages { get; set; } = new();
        public List<string> Links     { get; set; } = new();
        public string CreatedAt       { get; set; } = "";
        public bool   IsVerified      { get; set; }
        public string JoinedAt        { get; set; } = "";
        public bool   IsRepresenting  { get; set; }
        public string LastPostJson    { get; set; } = "";
        public string LastEventJson   { get; set; } = "";
    }

    public string GetGroupDetailStatus(string groupId)
    {
        if (string.IsNullOrEmpty(groupId)) return "empty_id";
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT detail_cached_at FROM group_tracking WHERE group_id=$id";
                cmd.Parameters.AddWithValue("$id", groupId);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return "no_row";
                var cachedAt = r.IsDBNull(0) ? "" : r.GetString(0);
                return string.IsNullOrEmpty(cachedAt) ? "empty_cached_at" : $"ok:{cachedAt}";
            }
            catch (Exception ex) { return $"ex:{ex.Message}"; }
        }
    }

    public GroupDetailCache? GetGroupDetail(string groupId)
    {
        if (string.IsNullOrEmpty(groupId)) return null;
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"SELECT name,short_code,description,icon_url,banner_url,member_count,
                    privacy,join_state,owner_id,owner_display_name,rules,languages,links,detail_cached_at,
                    created_at,is_verified,joined_at,is_representing,last_post_json,last_event_json
                    FROM group_tracking WHERE group_id=$id";
                cmd.Parameters.AddWithValue("$id", groupId);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) { System.Diagnostics.Debug.WriteLine($"[GRP-CACHE] no row for {groupId}"); return null; }
                var cachedAt = r.IsDBNull(13) ? "" : r.GetString(13);
                if (string.IsNullOrEmpty(cachedAt)) { System.Diagnostics.Debug.WriteLine($"[GRP-CACHE] empty detail_cached_at for {groupId}"); return null; }
                return new GroupDetailCache
                {
                    Name          = r.IsDBNull(0)  ? "" : r.GetString(0),
                    ShortCode     = r.IsDBNull(1)  ? "" : r.GetString(1),
                    Description   = r.IsDBNull(2)  ? "" : r.GetString(2),
                    IconUrl       = r.IsDBNull(3)  ? "" : r.GetString(3),
                    BannerUrl     = r.IsDBNull(4)  ? "" : r.GetString(4),
                    MemberCount   = r.IsDBNull(5)  ? 0  : r.GetInt32(5),
                    Privacy       = r.IsDBNull(6)  ? "" : r.GetString(6),
                    JoinState     = r.IsDBNull(7)  ? "" : r.GetString(7),
                    OwnerId       = r.IsDBNull(8)  ? "" : r.GetString(8),
                    OwnerName     = r.IsDBNull(9)  ? "" : r.GetString(9),
                    Rules         = r.IsDBNull(10) ? "" : r.GetString(10),
                    Languages     = r.IsDBNull(11) ? new() : JsonConvert.DeserializeObject<List<string>>(r.GetString(11)) ?? new(),
                    Links         = r.IsDBNull(12) ? new() : JsonConvert.DeserializeObject<List<string>>(r.GetString(12)) ?? new(),
                    CreatedAt     = r.IsDBNull(14) ? "" : r.GetString(14),
                    IsVerified    = !r.IsDBNull(15) && r.GetInt32(15) != 0,
                    JoinedAt      = r.IsDBNull(16) ? "" : r.GetString(16),
                    IsRepresenting = !r.IsDBNull(17) && r.GetInt32(17) != 0,
                    LastPostJson  = r.IsDBNull(18) ? "" : r.GetString(18),
                    LastEventJson = r.IsDBNull(19) ? "" : r.GetString(19),
                };
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[GRP-CACHE] exception: {ex.Message}"); return null; }
        }
    }

    public void SaveGroupDetail(string groupId, string name, string shortCode, string description,
        string iconUrl, string bannerUrl, int memberCount, string privacy, string joinState,
        string ownerId, string ownerDisplayName, string rules, List<string> languages, List<string> links,
        string createdAt = "", bool isVerified = false, string joinedAt = "", bool isRepresenting = false,
        string lastPostJson = "", string lastEventJson = "")
    {
        if (string.IsNullOrEmpty(groupId)) return;
        var now = DateTime.UtcNow.ToString("o");
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"INSERT INTO group_tracking(group_id,name,short_code,description,icon_url,banner_url,
                    member_count,privacy,join_state,owner_id,owner_display_name,rules,languages,links,detail_cached_at,
                    created_at,is_verified,joined_at,is_representing,last_post_json,last_event_json)
                    VALUES($id,$n,$sc,$desc,$ic,$bn,$mc,$pr,$js,$oid,$odn,$rul,$lng,$lnk,$cat,$ca,$iv,$ja,$ir,$lpj,$lej)
                    ON CONFLICT(group_id) DO UPDATE SET
                        name=excluded.name, short_code=excluded.short_code, description=excluded.description,
                        icon_url=excluded.icon_url, banner_url=excluded.banner_url, member_count=excluded.member_count,
                        privacy=excluded.privacy, join_state=excluded.join_state, owner_id=excluded.owner_id,
                        owner_display_name=excluded.owner_display_name, rules=excluded.rules,
                        languages=excluded.languages, links=excluded.links, detail_cached_at=excluded.detail_cached_at,
                        created_at=excluded.created_at, is_verified=excluded.is_verified,
                        joined_at=excluded.joined_at, is_representing=excluded.is_representing,
                        last_post_json=CASE WHEN excluded.last_post_json='' THEN last_post_json ELSE excluded.last_post_json END,
                        last_event_json=CASE WHEN excluded.last_event_json='' THEN last_event_json ELSE excluded.last_event_json END";
                cmd.Parameters.AddWithValue("$id",  groupId);
                cmd.Parameters.AddWithValue("$n",   name);
                cmd.Parameters.AddWithValue("$sc",  shortCode);
                cmd.Parameters.AddWithValue("$desc", description);
                cmd.Parameters.AddWithValue("$ic",  iconUrl);
                cmd.Parameters.AddWithValue("$bn",  bannerUrl);
                cmd.Parameters.AddWithValue("$mc",  memberCount);
                cmd.Parameters.AddWithValue("$pr",  privacy);
                cmd.Parameters.AddWithValue("$js",  joinState);
                cmd.Parameters.AddWithValue("$oid", ownerId);
                cmd.Parameters.AddWithValue("$odn", ownerDisplayName);
                cmd.Parameters.AddWithValue("$rul", rules);
                cmd.Parameters.AddWithValue("$lng", JsonConvert.SerializeObject(languages));
                cmd.Parameters.AddWithValue("$lnk", JsonConvert.SerializeObject(links));
                cmd.Parameters.AddWithValue("$cat", now);
                cmd.Parameters.AddWithValue("$ca",  createdAt);
                cmd.Parameters.AddWithValue("$iv",  isVerified ? 1 : 0);
                cmd.Parameters.AddWithValue("$ja",  joinedAt);
                cmd.Parameters.AddWithValue("$ir",  isRepresenting ? 1 : 0);
                cmd.Parameters.AddWithValue("$lpj", lastPostJson);
                cmd.Parameters.AddWithValue("$lej", lastEventJson);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[GRP-SAVE-ERR] {ex.Message}"); _lastGroupSaveError = ex.Message; }
        }
    }
    internal string _lastGroupSaveError = "";

    // Avatar detail cache

    public class AvatarDetailCache
    {
        public string Name              { get; set; } = "";
        public string AuthorName        { get; set; } = "";
        public string AuthorId          { get; set; } = "";
        public string ThumbnailImageUrl { get; set; } = "";
        public string ImageUrl          { get; set; } = "";
        public string ReleaseStatus     { get; set; } = "";
        public int    Version           { get; set; }
        public string CreatedAt         { get; set; } = "";
        public string UpdatedAt         { get; set; } = "";
        public string Description       { get; set; } = "";
        public List<string> Tags        { get; set; } = new();
        public bool   HasPC             { get; set; }
        public bool   HasQuest          { get; set; }
        public bool   HasImpostor       { get; set; }
        public string PcPerf            { get; set; } = "";
        public string QuestPerf         { get; set; } = "";
    }

    public string GetAvatarDetailStatus(string avatarId)
    {
        if (string.IsNullOrEmpty(avatarId)) return "empty_id";
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT detail_cached_at FROM avatar_tracking WHERE avatar_id=$id";
                cmd.Parameters.AddWithValue("$id", avatarId);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return "no_row";
                var cachedAt = r.IsDBNull(0) ? "" : r.GetString(0);
                return string.IsNullOrEmpty(cachedAt) ? "empty_cached_at" : $"ok:{cachedAt}";
            }
            catch (Exception ex) { return $"ex:{ex.Message}"; }
        }
    }

    public AvatarDetailCache? GetAvatarDetail(string avatarId)
    {
        if (string.IsNullOrEmpty(avatarId)) return null;
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"SELECT name,author_name,author_id,thumbnail_image_url,image_url,
                    release_status,version,created_at,updated_at,description,tags,
                    has_pc,has_quest,has_impostor,pc_perf,quest_perf,detail_cached_at
                    FROM avatar_tracking WHERE avatar_id=$id";
                cmd.Parameters.AddWithValue("$id", avatarId);
                using var r = cmd.ExecuteReader();
                if (!r.Read() || string.IsNullOrEmpty(r.GetString(16))) return null;
                return new AvatarDetailCache
                {
                    Name              = r.GetString(0),  AuthorName        = r.GetString(1),
                    AuthorId          = r.GetString(2),  ThumbnailImageUrl = r.GetString(3),
                    ImageUrl          = r.GetString(4),  ReleaseStatus     = r.GetString(5),
                    Version           = r.GetInt32(6),   CreatedAt         = r.GetString(7),
                    UpdatedAt         = r.GetString(8),  Description       = r.GetString(9),
                    Tags              = JsonConvert.DeserializeObject<List<string>>(r.GetString(10)) ?? new(),
                    HasPC             = r.GetInt32(11) != 0,
                    HasQuest          = r.GetInt32(12) != 0,
                    HasImpostor       = r.GetInt32(13) != 0,
                    PcPerf            = r.GetString(14),
                    QuestPerf         = r.GetString(15),
                };
            }
            catch { return null; }
        }
    }

    public void SaveAvatarDetail(string avatarId, string name, string authorName, string authorId,
        string thumbnailImageUrl, string imageUrl, string releaseStatus, int version,
        string createdAt, string updatedAt, string description, List<string> tags,
        bool hasPC, bool hasQuest, bool hasImpostor, string pcPerf, string questPerf)
    {
        if (string.IsNullOrEmpty(avatarId)) return;
        var now = DateTime.UtcNow.ToString("o");
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"INSERT INTO avatar_tracking(avatar_id,name,author_name,author_id,thumbnail_image_url,
                    image_url,release_status,version,created_at,updated_at,description,tags,
                    has_pc,has_quest,has_impostor,pc_perf,quest_perf,detail_cached_at)
                    VALUES($id,$n,$an,$ai,$ti,$img,$rs,$ver,$ca,$ua,$desc,$tags,$hpc,$hq,$hi,$pcp,$qp,$cat)
                    ON CONFLICT(avatar_id) DO UPDATE SET
                        name=excluded.name, author_name=excluded.author_name, author_id=excluded.author_id,
                        thumbnail_image_url=excluded.thumbnail_image_url, image_url=excluded.image_url,
                        release_status=excluded.release_status, version=excluded.version,
                        created_at=excluded.created_at, updated_at=excluded.updated_at,
                        description=excluded.description, tags=excluded.tags,
                        has_pc=excluded.has_pc, has_quest=excluded.has_quest, has_impostor=excluded.has_impostor,
                        pc_perf=excluded.pc_perf, quest_perf=excluded.quest_perf, detail_cached_at=excluded.detail_cached_at";
                cmd.Parameters.AddWithValue("$id",   avatarId);
                cmd.Parameters.AddWithValue("$n",    name);
                cmd.Parameters.AddWithValue("$an",   authorName);
                cmd.Parameters.AddWithValue("$ai",   authorId);
                cmd.Parameters.AddWithValue("$ti",   thumbnailImageUrl);
                cmd.Parameters.AddWithValue("$img",  imageUrl);
                cmd.Parameters.AddWithValue("$rs",   releaseStatus);
                cmd.Parameters.AddWithValue("$ver",  version);
                cmd.Parameters.AddWithValue("$ca",   createdAt);
                cmd.Parameters.AddWithValue("$ua",   updatedAt);
                cmd.Parameters.AddWithValue("$desc", description);
                cmd.Parameters.AddWithValue("$tags", JsonConvert.SerializeObject(tags));
                cmd.Parameters.AddWithValue("$hpc",  hasPC ? 1 : 0);
                cmd.Parameters.AddWithValue("$hq",   hasQuest ? 1 : 0);
                cmd.Parameters.AddWithValue("$hi",   hasImpostor ? 1 : 0);
                cmd.Parameters.AddWithValue("$pcp",  pcPerf);
                cmd.Parameters.AddWithValue("$qp",   questPerf);
                cmd.Parameters.AddWithValue("$cat",  now);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AVT-SAVE-ERR] {ex.Message}"); }
        }
    }

    // User detail cache

    public class UserDetailCache
    {
        public string DisplayName       { get; set; } = "";
        public string Image             { get; set; } = "";
        public string Status            { get; set; } = "";
        public string StatusDescription { get; set; } = "";
        public string Bio               { get; set; } = "";
        public string Location          { get; set; } = "";
        public bool   IsFriend          { get; set; }
        public string CurrentAvatarImg  { get; set; } = "";
    }

    public UserDetailCache? GetUserDetail(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return null;
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"SELECT display_name,image,profile_status,profile_status_desc,
                    profile_bio,profile_location,profile_is_friend,profile_avatar_img,profile_cached_at
                    FROM user_tracking WHERE user_id=$id";
                cmd.Parameters.AddWithValue("$id", userId);
                using var r = cmd.ExecuteReader();
                if (!r.Read() || string.IsNullOrEmpty(r.GetString(8))) return null;
                return new UserDetailCache
                {
                    DisplayName       = r.GetString(0), Image             = r.GetString(1),
                    Status            = r.GetString(2), StatusDescription = r.GetString(3),
                    Bio               = r.GetString(4), Location          = r.GetString(5),
                    IsFriend          = r.GetInt32(6) != 0,
                    CurrentAvatarImg  = r.GetString(7),
                };
            }
            catch { return null; }
        }
    }

    public void SaveUserDetail(string userId, string displayName, string image, string status,
        string statusDescription, string bio, string location, bool isFriend, string currentAvatarImg)
    {
        if (string.IsNullOrEmpty(userId)) return;
        var now = DateTime.UtcNow.ToString("o");
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"INSERT INTO user_tracking(user_id,display_name,image,profile_status,profile_status_desc,
                    profile_bio,profile_location,profile_is_friend,profile_avatar_img,profile_cached_at)
                    VALUES($id,$dn,$img,$st,$sd,$bio,$loc,$fr,$ai,$cat)
                    ON CONFLICT(user_id) DO UPDATE SET
                        display_name=excluded.display_name, image=excluded.image,
                        profile_status=excluded.profile_status, profile_status_desc=excluded.profile_status_desc,
                        profile_bio=excluded.profile_bio, profile_location=excluded.profile_location,
                        profile_is_friend=excluded.profile_is_friend, profile_avatar_img=excluded.profile_avatar_img,
                        profile_cached_at=excluded.profile_cached_at";
                cmd.Parameters.AddWithValue("$id",  userId);
                cmd.Parameters.AddWithValue("$dn",  displayName);
                cmd.Parameters.AddWithValue("$img", image);
                cmd.Parameters.AddWithValue("$st",  status);
                cmd.Parameters.AddWithValue("$sd",  statusDescription);
                cmd.Parameters.AddWithValue("$bio", bio);
                cmd.Parameters.AddWithValue("$loc", location);
                cmd.Parameters.AddWithValue("$fr",  isFriend ? 1 : 0);
                cmd.Parameters.AddWithValue("$ai",  currentAvatarImg);
                cmd.Parameters.AddWithValue("$cat", now);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }

    // User profile cache

    public class UserProfileCache
    {
        public string DisplayName             { get; set; } = "";
        public string Image                   { get; set; } = "";
        public string ProfileStatus           { get; set; } = "";
        public string ProfileStatusDesc       { get; set; } = "";
        public string ProfileBio              { get; set; } = "";
        public string ProfileLocation         { get; set; } = "";
        public int    ProfileIsFriend         { get; set; }
        public string ProfileAvatarImg        { get; set; } = "";
        public string ProfileCachedAt         { get; set; } = "";
        public string ProfileLastLogin        { get; set; } = "";
        public string ProfileLastActivity     { get; set; } = "";
        public string ProfileDateJoined       { get; set; } = "";
        public string ProfileWorldName        { get; set; } = "";
        public string ProfileWorldThumb       { get; set; } = "";
        public string ProfileInstanceType     { get; set; } = "";
        public int    ProfileUserCount        { get; set; }
        public int    ProfileWorldCapacity    { get; set; }
        public int    ProfileCanJoin          { get; set; }
        public int    ProfileCanRequestInvite { get; set; }
        public int    ProfileCanInvite        { get; set; }
        public string ProfileCurrentAvatarId  { get; set; } = "";
        public string ProfileAvatarFileId     { get; set; } = "";
        public string ProfilePicOverride      { get; set; } = "";
        public string ProfileTags             { get; set; } = "[]";
        public string ProfileNote             { get; set; } = "";
        public string ProfileFriendKey        { get; set; } = "";
        public string ProfileTravelingTo      { get; set; } = "";
        public string ProfileState            { get; set; } = "";
        public string ProfileLastPlatform     { get; set; } = "";
        public string ProfilePlatform         { get; set; } = "";
        public string ProfileUserNote         { get; set; } = "";
        public int    ProfileInSameInstance   { get; set; }
        public string ProfilePronouns         { get; set; } = "";
        public string ProfileAgeVerification  { get; set; } = "";
        public int    ProfileAgeVerified      { get; set; }
        public string ProfileBioLinks         { get; set; } = "[]";
        public int    ProfileIsFavorited      { get; set; }
        public string ProfileFavFriendId      { get; set; } = "";
        public string ProfileBadges           { get; set; } = "[]";
        public string ProfileRepresentedGroup { get; set; } = "";
        public string GroupsJson              { get; set; } = "[]";
        public string GroupsCachedAt          { get; set; } = "";
        public string ContentJson             { get; set; } = "{}";
        public string ContentCachedAt         { get; set; } = "";
        public string MutualsJson             { get; set; } = "{}";
        public string MutualsCachedAt         { get; set; } = "";
        public string MutualGroupsJson        { get; set; } = "[]";
        public string MutualGroupsCachedAt    { get; set; } = "";
        public string ProfileCurrentAvatar    { get; set; } = "";
    }

    public UserProfileCache? GetUserProfileCache(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return null;
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"SELECT
                    display_name, image, profile_status, profile_status_desc, profile_bio, profile_location,
                    profile_is_friend, profile_avatar_img, profile_cached_at,
                    profile_last_login, profile_last_activity, profile_date_joined,
                    profile_world_name, profile_world_thumb, profile_instance_type,
                    profile_user_count, profile_world_capacity, profile_can_join, profile_can_request_invite, profile_can_invite,
                    profile_current_avatar_id, profile_avatar_file_id, profile_pic_override,
                    profile_tags, profile_note, profile_friend_key, profile_traveling_to, profile_state,
                    profile_last_platform, profile_platform, profile_user_note, profile_in_same_instance,
                    profile_pronouns, profile_age_verification, profile_age_verified,
                    profile_bio_links, profile_is_favorited, profile_fav_friend_id, profile_badges,
                    profile_represented_group,
                    groups, groups_cached_at, content, content_cached_at,
                    mutuals, mutuals_cached_at, mutual_groups, mutual_groups_cached_at,
                    profile_current_avatar
                    FROM user_tracking WHERE user_id=$id";
                cmd.Parameters.AddWithValue("$id", userId);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;
                string S(string col) { var o = r.GetOrdinal(col); return r.IsDBNull(o) ? "" : r.GetString(o); }
                int    I(string col) { var o = r.GetOrdinal(col); return r.IsDBNull(o) ? 0 : (int)r.GetInt64(o); }
                string SA(string col, string def) { var v = S(col); return v.Length > 0 ? v : def; }
                var c = new UserProfileCache
                {
                    DisplayName            = S("display_name"),
                    Image                  = S("image"),
                    ProfileStatus          = S("profile_status"),
                    ProfileStatusDesc      = S("profile_status_desc"),
                    ProfileBio             = S("profile_bio"),
                    ProfileLocation        = S("profile_location"),
                    ProfileIsFriend        = I("profile_is_friend"),
                    ProfileAvatarImg       = S("profile_avatar_img"),
                    ProfileCachedAt        = S("profile_cached_at"),
                    ProfileLastLogin       = S("profile_last_login"),
                    ProfileLastActivity    = S("profile_last_activity"),
                    ProfileDateJoined      = S("profile_date_joined"),
                    ProfileWorldName       = S("profile_world_name"),
                    ProfileWorldThumb      = S("profile_world_thumb"),
                    ProfileInstanceType    = S("profile_instance_type"),
                    ProfileUserCount       = I("profile_user_count"),
                    ProfileWorldCapacity   = I("profile_world_capacity"),
                    ProfileCanJoin         = I("profile_can_join"),
                    ProfileCanRequestInvite = I("profile_can_request_invite"),
                    ProfileCanInvite       = I("profile_can_invite"),
                    ProfileCurrentAvatarId = S("profile_current_avatar_id"),
                    ProfileAvatarFileId    = S("profile_avatar_file_id"),
                    ProfilePicOverride     = S("profile_pic_override"),
                    ProfileTags            = SA("profile_tags", "[]"),
                    ProfileNote            = S("profile_note"),
                    ProfileFriendKey       = S("profile_friend_key"),
                    ProfileTravelingTo     = S("profile_traveling_to"),
                    ProfileState           = S("profile_state"),
                    ProfileLastPlatform    = S("profile_last_platform"),
                    ProfilePlatform        = S("profile_platform"),
                    ProfileUserNote        = S("profile_user_note"),
                    ProfileInSameInstance  = I("profile_in_same_instance"),
                    ProfilePronouns        = S("profile_pronouns"),
                    ProfileAgeVerification = S("profile_age_verification"),
                    ProfileAgeVerified     = I("profile_age_verified"),
                    ProfileBioLinks        = SA("profile_bio_links", "[]"),
                    ProfileIsFavorited     = I("profile_is_favorited"),
                    ProfileFavFriendId     = S("profile_fav_friend_id"),
                    ProfileBadges          = SA("profile_badges", "[]"),
                    ProfileRepresentedGroup = S("profile_represented_group"),
                    GroupsJson             = SA("groups", "[]"),
                    GroupsCachedAt         = S("groups_cached_at"),
                    ContentJson            = SA("content", "{}"),
                    ContentCachedAt        = S("content_cached_at"),
                    MutualsJson            = SA("mutuals", "{}"),
                    MutualsCachedAt        = S("mutuals_cached_at"),
                    MutualGroupsJson       = SA("mutual_groups", "[]"),
                    MutualGroupsCachedAt   = S("mutual_groups_cached_at"),
                    ProfileCurrentAvatar   = S("profile_current_avatar"),
                };
                return string.IsNullOrEmpty(c.ProfileCachedAt) ? null : c;
            }
            catch { return null; }
        }
    }

    public void SaveUserProfileCache(string userId, string payloadJson)
    {
        if (string.IsNullOrEmpty(userId)) return;
        JObject p;
        try { p = JObject.Parse(payloadJson); } catch { return; }
        var now = DateTime.UtcNow.ToString("o");
        lock (_lock)
        {
            try
            {
                using var ins = _db.CreateCommand();
                ins.CommandText = "INSERT OR IGNORE INTO user_tracking(user_id) VALUES($id)";
                ins.Parameters.AddWithValue("$id", userId);
                ins.ExecuteNonQuery();
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"UPDATE user_tracking SET
                    display_name=$dn, image=$img,
                    profile_status=$st, profile_status_desc=$sd, profile_bio=$bio, profile_location=$loc,
                    profile_is_friend=$fr, profile_avatar_img=$ai, profile_cached_at=$cat,
                    profile_last_login=$ll, profile_last_activity=$la, profile_date_joined=$dj,
                    profile_world_name=$wn, profile_world_thumb=$wt, profile_instance_type=$it,
                    profile_user_count=$uc, profile_world_capacity=$wc, profile_can_join=$cj,
                    profile_can_request_invite=$cri, profile_can_invite=$ci,
                    profile_current_avatar_id=$caid, profile_avatar_file_id=$afid, profile_pic_override=$po,
                    profile_tags=$tags, profile_note=$note, profile_friend_key=$fk, profile_traveling_to=$tt,
                    profile_state=$state, profile_last_platform=$lp, profile_platform=$pl, profile_user_note=$un,
                    profile_in_same_instance=$isi, profile_pronouns=$pro, profile_age_verification=$av,
                    profile_age_verified=$avd, profile_bio_links=$bl, profile_is_favorited=$ifav,
                    profile_fav_friend_id=$ffid, profile_badges=$badges,
                    profile_represented_group=$rg
                    WHERE user_id=$id";
                cmd.Parameters.AddWithValue("$id",    userId);
                cmd.Parameters.AddWithValue("$dn",    p["displayName"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$img",   p["image"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$st",    p["status"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$sd",    p["statusDescription"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$bio",   p["bio"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$loc",   p["location"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$fr",    p["isFriend"]?.Value<bool>() == true ? 1 : 0);
                cmd.Parameters.AddWithValue("$ai",    p["currentAvatarImageUrl"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$cat",   now);
                cmd.Parameters.AddWithValue("$ll",    p["lastLogin"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$la",    p["lastActivity"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$dj",    p["dateJoined"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$wn",    p["worldName"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$wt",    p["worldThumb"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$it",    p["instanceType"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$uc",    p["userCount"]?.Value<int>() ?? 0);
                cmd.Parameters.AddWithValue("$wc",    p["worldCapacity"]?.Value<int>() ?? 0);
                cmd.Parameters.AddWithValue("$cj",    p["canJoin"]?.Value<bool>() == true ? 1 : 0);
                cmd.Parameters.AddWithValue("$cri",   p["canRequestInvite"]?.Value<bool>() == true ? 1 : 0);
                cmd.Parameters.AddWithValue("$ci",    p["canInvite"]?.Value<bool>() == true ? 1 : 0);
                cmd.Parameters.AddWithValue("$caid",  p["currentAvatarId"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$afid",  p["avatarFileId"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$po",    p["profilePicOverride"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$tags",  p["tags"]?.ToString() ?? "[]");
                cmd.Parameters.AddWithValue("$note",  p["note"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$fk",    p["friendKey"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$tt",    p["travelingToLocation"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$state", p["state"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$lp",    p["lastPlatform"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$pl",    p["platform"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$un",    p["userNote"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$isi",   p["inSameInstance"]?.Value<bool>() == true ? 1 : 0);
                cmd.Parameters.AddWithValue("$pro",   p["pronouns"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$av",    p["ageVerificationStatus"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$avd",   p["ageVerified"]?.Value<bool>() == true ? 1 : 0);
                cmd.Parameters.AddWithValue("$bl",    p["bioLinks"]?.ToString() ?? "[]");
                cmd.Parameters.AddWithValue("$ifav",  p["isFavorited"]?.Value<bool>() == true ? 1 : 0);
                cmd.Parameters.AddWithValue("$ffid",  p["favFriendId"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$badges", p["badges"]?.ToString() ?? "[]");
                cmd.Parameters.AddWithValue("$rg",     p["representedGroup"]?.Type == JTokenType.Object ? p["representedGroup"]!.ToString() : "");
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }

    public void SaveUserGroupsCache(string userId, string groupsJson)
    {
        if (string.IsNullOrEmpty(userId)) return;
        var now = DateTime.UtcNow.ToString("o");
        lock (_lock)
        {
            try
            {
                using var ins = _db.CreateCommand();
                ins.CommandText = "INSERT OR IGNORE INTO user_tracking(user_id) VALUES($id)";
                ins.Parameters.AddWithValue("$id", userId);
                ins.ExecuteNonQuery();
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "UPDATE user_tracking SET groups=$gj, groups_cached_at=$cat WHERE user_id=$id";
                cmd.Parameters.AddWithValue("$id", userId); cmd.Parameters.AddWithValue("$gj", groupsJson); cmd.Parameters.AddWithValue("$cat", now);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }

    public void SetAvatarInfoCache(string userId, string fileId, string avatarId, string name, string authorName)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(avatarId)) return;
        var json = JsonConvert.SerializeObject(new { fileId, avatarId, name, authorName });
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "UPDATE user_tracking SET profile_current_avatar=$ca WHERE user_id=$id";
                cmd.Parameters.AddWithValue("$id", userId);
                cmd.Parameters.AddWithValue("$ca", json);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }

    public void SaveUserContentCache(string userId, string contentJson)
    {
        if (string.IsNullOrEmpty(userId)) return;
        var now = DateTime.UtcNow.ToString("o");
        lock (_lock)
        {
            try
            {
                using var ins = _db.CreateCommand();
                ins.CommandText = "INSERT OR IGNORE INTO user_tracking(user_id) VALUES($id)";
                ins.Parameters.AddWithValue("$id", userId);
                ins.ExecuteNonQuery();
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "UPDATE user_tracking SET content=$cj, content_cached_at=$cat WHERE user_id=$id";
                cmd.Parameters.AddWithValue("$id", userId); cmd.Parameters.AddWithValue("$cj", contentJson); cmd.Parameters.AddWithValue("$cat", now);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }

    public void SaveUserMutualsCache(string userId, string mutualsJson)
    {
        if (string.IsNullOrEmpty(userId)) return;
        var now = DateTime.UtcNow.ToString("o");
        lock (_lock)
        {
            try
            {
                using var ins = _db.CreateCommand();
                ins.CommandText = "INSERT OR IGNORE INTO user_tracking(user_id) VALUES($id)";
                ins.Parameters.AddWithValue("$id", userId);
                ins.ExecuteNonQuery();
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "UPDATE user_tracking SET mutuals=$mj, mutuals_cached_at=$cat WHERE user_id=$id";
                cmd.Parameters.AddWithValue("$id", userId); cmd.Parameters.AddWithValue("$mj", mutualsJson); cmd.Parameters.AddWithValue("$cat", now);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }

    public void SaveUserMutualGroupsCache(string userId, string mutualGroupsJson)
    {
        if (string.IsNullOrEmpty(userId)) return;
        var now = DateTime.UtcNow.ToString("o");
        lock (_lock)
        {
            try
            {
                using var ins = _db.CreateCommand();
                ins.CommandText = "INSERT OR IGNORE INTO user_tracking(user_id) VALUES($id)";
                ins.Parameters.AddWithValue("$id", userId);
                ins.ExecuteNonQuery();
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "UPDATE user_tracking SET mutual_groups=$mgj, mutual_groups_cached_at=$cat WHERE user_id=$id";
                cmd.Parameters.AddWithValue("$id", userId); cmd.Parameters.AddWithValue("$mgj", mutualGroupsJson); cmd.Parameters.AddWithValue("$cat", now);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }

    public void CleanMutualCaches(HashSet<string>? validFriendIds, HashSet<string>? validGroupIds)
    {
        if (validFriendIds == null && validGroupIds == null) return;
        lock (_lock)
        {
            try
            {
                var rows = new List<(string id, string mutuals, string mutualGroups)>();
                using (var sel = _db.CreateCommand())
                {
                    sel.CommandText = @"SELECT user_id, mutuals, mutual_groups FROM user_tracking
                        WHERE (mutuals IS NOT NULL AND mutuals != '' AND mutuals != '{}')
                           OR (mutual_groups IS NOT NULL AND mutual_groups != '' AND mutual_groups != '[]')";
                    using var r = sel.ExecuteReader();
                    while (r.Read())
                        rows.Add((
                            r.IsDBNull(0) ? "" : r.GetString(0),
                            r.IsDBNull(1) ? "" : r.GetString(1),
                            r.IsDBNull(2) ? "" : r.GetString(2)));
                }

                foreach (var (uid, mutualsJson, mutualGroupsJson) in rows)
                {
                    if (string.IsNullOrEmpty(uid)) continue;
                    string? newMutuals = null;
                    string? newMutualGroups = null;

                    if (validFriendIds != null && !string.IsNullOrEmpty(mutualsJson) && mutualsJson != "{}")
                    {
                        try
                        {
                            var obj = JObject.Parse(mutualsJson);
                            if (obj["mutuals"] is JArray arr)
                            {
                                var filtered = new JArray(arr.Where(m =>
                                {
                                    var id = m?["id"]?.ToString() ?? "";
                                    return id.Length == 0 || validFriendIds.Contains(id);
                                }));
                                if (filtered.Count != arr.Count)
                                {
                                    obj["mutuals"] = filtered;
                                    newMutuals = obj.ToString(Formatting.None);
                                }
                            }
                        }
                        catch { }
                    }

                    if (validGroupIds != null && !string.IsNullOrEmpty(mutualGroupsJson) && mutualGroupsJson != "[]")
                    {
                        try
                        {
                            var arr = JArray.Parse(mutualGroupsJson);
                            var filtered = new JArray(arr.Where(g =>
                            {
                                var id = g?["groupId"]?.ToString() ?? g?["id"]?.ToString() ?? "";
                                return id.Length == 0 || validGroupIds.Contains(id);
                            }));
                            if (filtered.Count != arr.Count)
                                newMutualGroups = filtered.ToString(Formatting.None);
                        }
                        catch { }
                    }

                    if (newMutuals == null && newMutualGroups == null) continue;

                    using var upd = _db.CreateCommand();
                    if (newMutuals != null && newMutualGroups != null)
                    {
                        upd.CommandText = "UPDATE user_tracking SET mutuals=$m, mutual_groups=$g WHERE user_id=$id";
                        upd.Parameters.AddWithValue("$m", newMutuals);
                        upd.Parameters.AddWithValue("$g", newMutualGroups);
                    }
                    else if (newMutuals != null)
                    {
                        upd.CommandText = "UPDATE user_tracking SET mutuals=$m WHERE user_id=$id";
                        upd.Parameters.AddWithValue("$m", newMutuals);
                    }
                    else
                    {
                        upd.CommandText = "UPDATE user_tracking SET mutual_groups=$g WHERE user_id=$id";
                        upd.Parameters.AddWithValue("$g", newMutualGroups);
                    }
                    upd.Parameters.AddWithValue("$id", uid);
                    upd.ExecuteNonQuery();
                }
            }
            catch { }
        }
    }

    // Event detail cache

    public class EventDetailCache
    {
        public string GroupId     { get; set; } = "";
        public string Title       { get; set; } = "";
        public string Description { get; set; } = "";
        public string StartsAt    { get; set; } = "";
        public string EndsAt      { get; set; } = "";
        public string ImageUrl    { get; set; } = "";
        public string AccessType  { get; set; } = "";
        public List<string> Tags  { get; set; } = new();
        public string OwnerId     { get; set; } = "";
        public bool   IsFollowing { get; set; }
    }

    public EventDetailCache? GetEventDetail(string eventId)
    {
        if (string.IsNullOrEmpty(eventId)) return null;
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"SELECT group_id,title,description,starts_at,ends_at,image_url,
                    access_type,tags,owner_id,is_following,detail_cached_at
                    FROM event_tracking WHERE event_id=$id";
                cmd.Parameters.AddWithValue("$id", eventId);
                using var r = cmd.ExecuteReader();
                if (!r.Read() || string.IsNullOrEmpty(r.GetString(10))) return null;
                return new EventDetailCache
                {
                    GroupId     = r.GetString(0), Title       = r.GetString(1),
                    Description = r.GetString(2), StartsAt    = r.GetString(3),
                    EndsAt      = r.GetString(4), ImageUrl    = r.GetString(5),
                    AccessType  = r.GetString(6),
                    Tags        = JsonConvert.DeserializeObject<List<string>>(r.GetString(7)) ?? new(),
                    OwnerId     = r.GetString(8),
                    IsFollowing = r.GetInt32(9) != 0,
                };
            }
            catch { return null; }
        }
    }

    public void SaveEventDetail(string eventId, string groupId, string title, string description,
        string startsAt, string endsAt, string imageUrl, string accessType, List<string> tags,
        string ownerId, bool isFollowing)
    {
        if (string.IsNullOrEmpty(eventId)) return;
        var now = DateTime.UtcNow.ToString("o");
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"INSERT INTO event_tracking(event_id,group_id,title,description,starts_at,ends_at,
                    image_url,access_type,tags,owner_id,is_following,detail_cached_at)
                    VALUES($id,$gid,$ti,$desc,$sa,$ea,$img,$at,$tags,$oid,$flw,$cat)
                    ON CONFLICT(event_id) DO UPDATE SET
                        group_id=excluded.group_id, title=excluded.title, description=excluded.description,
                        starts_at=excluded.starts_at, ends_at=excluded.ends_at, image_url=excluded.image_url,
                        access_type=excluded.access_type, tags=excluded.tags, owner_id=excluded.owner_id,
                        is_following=excluded.is_following, detail_cached_at=excluded.detail_cached_at";
                cmd.Parameters.AddWithValue("$id",   eventId);
                cmd.Parameters.AddWithValue("$gid",  groupId);
                cmd.Parameters.AddWithValue("$ti",   title);
                cmd.Parameters.AddWithValue("$desc", description);
                cmd.Parameters.AddWithValue("$sa",   startsAt);
                cmd.Parameters.AddWithValue("$ea",   endsAt);
                cmd.Parameters.AddWithValue("$img",  imageUrl);
                cmd.Parameters.AddWithValue("$at",   accessType);
                cmd.Parameters.AddWithValue("$tags", JsonConvert.SerializeObject(tags));
                cmd.Parameters.AddWithValue("$oid",  ownerId);
                cmd.Parameters.AddWithValue("$flw",  isFollowing ? 1 : 0);
                cmd.Parameters.AddWithValue("$cat",  now);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }

    // User info & friend tracking

    public void UpdateUserInfo(string userId, string displayName, string image)
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(displayName)) return;
            if (!Users.TryGetValue(userId, out var rec))
            {
                rec = new UserRecord();
                Users[userId] = rec;
            }
            if (rec.DisplayName == displayName && rec.Image == image) return;
            rec.DisplayName = displayName;
            if (!string.IsNullOrEmpty(image)) rec.Image = image;
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"INSERT INTO user_tracking(user_id,total_seconds,last_seen,last_seen_location,display_name,image)
                    VALUES($uid,0,'','', $dn,$img)
                    ON CONFLICT(user_id) DO UPDATE SET
                        display_name=CASE WHEN excluded.display_name!='' THEN excluded.display_name ELSE user_tracking.display_name END,
                        image=CASE WHEN excluded.image!='' THEN excluded.image ELSE user_tracking.image END";
                cmd.Parameters.AddWithValue("$uid", userId);
                cmd.Parameters.AddWithValue("$dn",  rec.DisplayName);
                cmd.Parameters.AddWithValue("$img", rec.Image);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }

    /// <summary>Updates LastSeen/LastSeenLocation for online friends. No time accumulation.</summary>
    public void UpdateFriendTracking(IEnumerable<(string userId, string location, string presence)> onlineFriends)
    {
        lock (_lock)
        {
            if (_disposed) return;
            var now = DateTime.UtcNow;
            var changed = new List<(string userId, UserRecord rec)>();

            foreach (var (userId, location, presence) in onlineFriends)
            {
                if (string.IsNullOrEmpty(userId)) continue;
                if (!Users.TryGetValue(userId, out var rec))
                {
                    rec = new UserRecord();
                    Users[userId] = rec;
                }
                if (presence != "offline")
                {
                    rec.LastSeen = now.ToString("o");
                    if (!string.IsNullOrEmpty(location) && location != "offline" && location != "private")
                        rec.LastSeenLocation = location;
                }
                changed.Add((userId, rec));
            }

            if (changed.Count == 0) return;
            try
            {
                using var tx = _db.BeginTransaction();
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO user_tracking(user_id,total_seconds,last_seen,last_seen_location,display_name,image)
                    VALUES($uid,$ts,$ls,$lsl,$dn,$img)
                    ON CONFLICT(user_id) DO UPDATE SET
                        total_seconds=excluded.total_seconds,
                        last_seen=excluded.last_seen,
                        last_seen_location=excluded.last_seen_location,
                        display_name=CASE WHEN excluded.display_name!='' THEN excluded.display_name ELSE user_tracking.display_name END,
                        image=CASE WHEN excluded.image!='' THEN excluded.image ELSE user_tracking.image END";
                var pUid = cmd.Parameters.Add("$uid", SqliteType.Text);
                var pTs  = cmd.Parameters.Add("$ts",  SqliteType.Integer);
                var pLs  = cmd.Parameters.Add("$ls",  SqliteType.Text);
                var pLsl = cmd.Parameters.Add("$lsl", SqliteType.Text);
                var pDn  = cmd.Parameters.Add("$dn",  SqliteType.Text);
                var pImg = cmd.Parameters.Add("$img", SqliteType.Text);
                foreach (var (userId, rec) in changed)
                {
                    pUid.Value = userId; pTs.Value = rec.TotalSeconds;
                    pLs.Value = rec.LastSeen; pLsl.Value = rec.LastSeenLocation;
                    pDn.Value = rec.DisplayName; pImg.Value = rec.Image;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            catch { }
        }
    }

    public void UpdateWorldInfo(string worldId, string name, string thumb)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(worldId) || string.IsNullOrEmpty(name)) return;
            if (!Worlds.TryGetValue(worldId, out var rec)) return;
            if (rec.WorldName == name && rec.WorldThumb == thumb) return;
            rec.WorldName  = name;
            rec.WorldThumb = thumb;
            UpsertWorldLocked(worldId, rec);
        }
    }

    // Crash recovery

    public void RestoreActiveSession(string currentLocation, HashSet<string> currentPlayerIds)
    {
        lock (_lock)
        {
            if (_disposed) return;
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT location,co_present_ids,last_flush_utc FROM active_session WHERE id=1";
                using var r = cmd.ExecuteReader();
                if (!r.Read())
                {
                    // No active_session row — clear any stale sessions pre-populated from catch-up
                    _playerSessions.Clear();
                    _worldSessionStart = null;
                    return;
                }

                var location = r.GetString(0);
                var sessionsJson = r.GetString(1);
                var worldStartStr = r.GetString(2);
                r.Close();

                if (string.IsNullOrEmpty(location) || location != currentLocation)
                {
                    _playerSessions.Clear();
                    _worldSessionStart = null;
                    ClearActiveSessionLocked();
                    return;
                }

                if (_isVrcRunning?.Invoke() != true)
                {
                    _playerSessions.Clear();
                    _worldSessionStart = null;
                    ClearActiveSessionLocked();
                    return;
                }

                var now = DateTime.UtcNow;

                Dictionary<string, string>? savedSessions = null;
                try { savedSessions = JsonConvert.DeserializeObject<Dictionary<string, string>>(sessionsJson); }
                catch { }
                if (savedSessions == null)
                {
                    ClearActiveSessionLocked();
                    return;
                }

                foreach (var (userId, startStr) in savedSessions)
                {
                    if (!currentPlayerIds.Contains(userId)) continue;
                    if (!DateTime.TryParse(startStr, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var sessionStart))
                        continue;
                    var age = (now - sessionStart).TotalSeconds;
                    if (age < 0 || age > 86400) continue;

                    _playerSessions[userId] = now; // start from NOW — DB already has flushed time
                    if (!Users.ContainsKey(userId))
                        Users[userId] = new UserRecord();
                }

                if (DateTime.TryParse(worldStartStr, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var wStart))
                {
                    var wAge = (now - wStart).TotalSeconds;
                    if (wAge >= 0 && wAge <= 86400)
                    {
                        var colon = currentLocation.IndexOf(':');
                        var worldId = colon >= 0 ? currentLocation.Substring(0, colon) : currentLocation;
                        if (!string.IsNullOrEmpty(worldId) && worldId.StartsWith("wrld_"))
                        {
                            _currentWorldId = worldId;
                            _currentLocation = currentLocation;
                            _worldSessionStart = now; // start from NOW — DB already has flushed time
                        }
                    }
                }

                PersistActiveSessionLocked();
            }
            catch
            {
                _playerSessions.Clear();
                _worldSessionStart = null;
                ClearActiveSessionLocked();
            }
        }
    }

    // Bulk import (VRCX migration)

    public void BulkMergeUsers(IEnumerable<(string userId, string displayName, long seconds, string lastSeen)> entries)
    {
        lock (_lock)
        {
            if (_disposed) return;
            try
            {
                using var tx  = _db.BeginTransaction();
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO user_tracking(user_id,total_seconds,last_seen,last_seen_location,display_name,image)
                    VALUES($uid,$ts,$ls,'',$dn,'')
                    ON CONFLICT(user_id) DO UPDATE SET
                        total_seconds = user_tracking.total_seconds + excluded.total_seconds,
                        last_seen = CASE WHEN excluded.last_seen > user_tracking.last_seen THEN excluded.last_seen ELSE user_tracking.last_seen END,
                        display_name = CASE WHEN excluded.display_name != '' AND user_tracking.display_name = '' THEN excluded.display_name ELSE user_tracking.display_name END";
                var pUid = cmd.Parameters.Add("$uid", SqliteType.Text);
                var pTs  = cmd.Parameters.Add("$ts",  SqliteType.Integer);
                var pLs  = cmd.Parameters.Add("$ls",  SqliteType.Text);
                var pDn  = cmd.Parameters.Add("$dn",  SqliteType.Text);
                foreach (var (userId, displayName, seconds, lastSeen) in entries)
                {
                    pUid.Value = userId; pTs.Value = seconds;
                    pLs.Value  = lastSeen; pDn.Value = displayName;
                    cmd.ExecuteNonQuery();
                    if (!Users.TryGetValue(userId, out var rec)) { rec = new UserRecord(); Users[userId] = rec; }
                    rec.TotalSeconds += seconds;
                    if (string.IsNullOrEmpty(rec.DisplayName) && !string.IsNullOrEmpty(displayName)) rec.DisplayName = displayName;
                    if (string.Compare(lastSeen, rec.LastSeen, StringComparison.Ordinal) > 0) rec.LastSeen = lastSeen;
                }
                tx.Commit();
            }
            catch { }
        }
    }

    public void BulkMergeWorlds(IEnumerable<(string worldId, string worldName, long seconds, int visitCount, string lastVisited)> entries)
    {
        lock (_lock)
        {
            if (_disposed) return;
            try
            {
                using var tx  = _db.BeginTransaction();
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO world_tracking(world_id,total_seconds,visit_count,last_visited,world_name,world_thumb)
                    VALUES($wid,$ts,$vc,$lv,$wn,'')
                    ON CONFLICT(world_id) DO UPDATE SET
                        total_seconds = world_tracking.total_seconds + excluded.total_seconds,
                        visit_count   = world_tracking.visit_count   + excluded.visit_count,
                        last_visited  = CASE WHEN excluded.last_visited > world_tracking.last_visited THEN excluded.last_visited ELSE world_tracking.last_visited END,
                        world_name    = CASE WHEN excluded.world_name != '' AND world_tracking.world_name = '' THEN excluded.world_name ELSE world_tracking.world_name END";
                var pWid = cmd.Parameters.Add("$wid", SqliteType.Text);
                var pTs  = cmd.Parameters.Add("$ts",  SqliteType.Integer);
                var pVc  = cmd.Parameters.Add("$vc",  SqliteType.Integer);
                var pLv  = cmd.Parameters.Add("$lv",  SqliteType.Text);
                var pWn  = cmd.Parameters.Add("$wn",  SqliteType.Text);
                foreach (var (worldId, worldName, seconds, visitCount, lastVisited) in entries)
                {
                    pWid.Value = worldId; pTs.Value = seconds;
                    pVc.Value  = visitCount; pLv.Value = lastVisited; pWn.Value = worldName;
                    cmd.ExecuteNonQuery();
                    if (!Worlds.TryGetValue(worldId, out var rec)) { rec = new WorldRecord(); Worlds[worldId] = rec; }
                    rec.TotalSeconds += seconds; rec.VisitCount += visitCount;
                    if (string.IsNullOrEmpty(rec.WorldName) && !string.IsNullOrEmpty(worldName)) rec.WorldName = worldName;
                    if (string.Compare(lastVisited, rec.LastVisited, StringComparison.Ordinal) > 0) rec.LastVisited = lastVisited;
                }
                tx.Commit();
            }
            catch { }
        }
    }

    public void Save() { } // persistence handled by event methods and watchdog

    // Watchdog — polls VRChat.exe every 2s, flushes sessions every 30s

    private void WatchdogTick(object? state)
    {
        lock (_lock)
        {
            if (_disposed) return;

            var vrcRunning = _isVrcRunning?.Invoke() ?? false;

            if (_vrcWasRunning && !vrcRunning)
            {
                HandleVrcClosedLocked();
            }

            if (vrcRunning)
            {
                _lastVrcAliveUtc = DateTime.UtcNow;
                AttachProcessExitedLocked();
            }

            _vrcWasRunning = vrcRunning;

            if (vrcRunning && (_playerSessions.Count > 0 || _worldSessionStart.HasValue))
            {
                var now = DateTime.UtcNow;
                if ((now - _lastFlushUtc).TotalSeconds >= 30)
                {
                    FlushSessionsToDbLocked(now);
                    _lastFlushUtc = now;
                }
                PersistActiveSessionLocked();
            }
        }
    }

    private void AttachProcessExitedLocked()
    {
        if (_monitoredVrcProcess != null)
        {
            try { if (!_monitoredVrcProcess.HasExited) return; }
            catch { }
            try { _monitoredVrcProcess.Dispose(); } catch { }
            _monitoredVrcProcess = null;
        }
        try
        {
            var procs = Process.GetProcessesByName("VRChat");
            foreach (var p in procs)
            {
                try
                {
                    if (p.HasExited) { p.Dispose(); continue; }
                    p.EnableRaisingEvents = true;
                    p.Exited += OnVrcProcessExited;
                    _monitoredVrcProcess = p;
                    return; // attached to first live process
                }
                catch { p.Dispose(); }
            }
        }
        catch { }
    }

    private void FlushSessionsToDbLocked(DateTime now)
    {
        var userIds = _playerSessions.Keys.ToList();
        foreach (var userId in userIds)
        {
            var delta = (long)(now - _playerSessions[userId]).TotalSeconds;
            if (delta <= 0 || delta > 86400) continue;
            if (Users.TryGetValue(userId, out var rec))
            {
                rec.TotalSeconds += delta;
                rec.LastSeen = now.ToString("o");
                _logger?.Invoke($"[TIMER] Spend Time saved: {rec.DisplayName} +{delta}s — overall time: {FormatDuration(rec.TotalSeconds)}");
            }
            _playerSessions[userId] = now;
        }
        if (userIds.Count > 0)
            PersistAllUsersLocked(userIds, now);

        if (_worldSessionStart.HasValue && !string.IsNullOrEmpty(_currentWorldId) && _currentWorldId.StartsWith("wrld_"))
        {
            var delta = (long)(now - _worldSessionStart.Value).TotalSeconds;
            if (delta > 0 && delta <= 86400)
            {
                if (!Worlds.TryGetValue(_currentWorldId, out var rec))
                {
                    rec = new WorldRecord();
                    Worlds[_currentWorldId] = rec;
                }
                rec.TotalSeconds += delta;
                rec.LastVisited = now.ToString("o");
                UpsertWorldLocked(_currentWorldId, rec);
                var wName = rec.WorldName.Length > 0 ? rec.WorldName : _currentWorldId;
                _logger?.Invoke($"[TIMER] World Time saved: +{delta}s — overall time in \"{wName}\": {FormatDuration(rec.TotalSeconds)}");
            }
            _worldSessionStart = now;
        }
    }

    private static string FormatDuration(long totalSeconds)
    {
        var d = totalSeconds / 86400;
        var h = (totalSeconds % 86400) / 3600;
        var m = (totalSeconds % 3600) / 60;
        var s = totalSeconds % 60;
        if (d > 0) return $"{d}d {h}h {m}m {s}s";
        if (h > 0) return $"{h}h {m}m {s}s";
        if (m > 0) return $"{m}m {s}s";
        return $"{s}s";
    }

    private void OnVrcProcessExited(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            if (_disposed) return;
            var stillRunning = _isVrcRunning?.Invoke() ?? false;
            if (stillRunning) return;
            HandleVrcClosedLocked();
            _vrcWasRunning = false;
        }
    }

    private void HandleVrcClosedLocked()
    {
        if (_playerSessions.Count == 0 && !_worldSessionStart.HasValue) return;
        // Use midpoint between last-confirmed-alive and now to minimize overcount (~1s avg error).
        var now = DateTime.UtcNow;
        var endTime = _lastVrcAliveUtc > DateTime.MinValue
            ? _lastVrcAliveUtc + TimeSpan.FromTicks((now - _lastVrcAliveUtc).Ticks / 2)
            : now;
        if (endTime > now) endTime = now;
        if ((now - endTime).TotalSeconds > 10) endTime = now;

        EndAllPlayerSessionsLocked(endTime);
        EndWorldSessionLocked(endTime);
        _currentWorldId = "";
        _currentLocation = "";
        ClearActiveSessionLocked();
        try { _monitoredVrcProcess?.Dispose(); } catch { }
        _monitoredVrcProcess = null;
        try { OnVrcClosed?.Invoke(); } catch { }
    }

    // Session end helpers

    private void EndAllPlayerSessionsLocked(DateTime now)
    {
        if (_playerSessions.Count == 0) return;
        var userIds = _playerSessions.Keys.ToList();
        foreach (var userId in userIds)
        {
            var sessionStart = _playerSessions[userId];
            var delta = (long)(now - sessionStart).TotalSeconds;
            if (delta > 0 && delta <= 86400)
            {
                if (Users.TryGetValue(userId, out var rec))
                {
                    rec.TotalSeconds += delta;
                    rec.LastSeen = now.ToString("o");
                    if (!string.IsNullOrEmpty(_currentLocation))
                        rec.LastSeenLocation = _currentLocation;
                }
            }
        }
        PersistAllUsersLocked(userIds, now);
        _playerSessions.Clear();
    }

    private void EndWorldSessionLocked(DateTime now)
    {
        if (!_worldSessionStart.HasValue) return;
        if (!string.IsNullOrEmpty(_currentWorldId) && _currentWorldId.StartsWith("wrld_"))
        {
            var delta = (long)(now - _worldSessionStart.Value).TotalSeconds;
            if (delta > 0 && delta <= 86400)
            {
                if (!Worlds.TryGetValue(_currentWorldId, out var rec))
                {
                    rec = new WorldRecord();
                    Worlds[_currentWorldId] = rec;
                }
                rec.TotalSeconds += delta;
                rec.LastVisited = now.ToString("o");
                UpsertWorldLocked(_currentWorldId, rec);
            }
        }
        _worldSessionStart = null;
    }

    // DB persistence

    private void InitSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"CREATE TABLE IF NOT EXISTS user_tracking (
            user_id            TEXT    PRIMARY KEY,
            total_seconds      INTEGER NOT NULL DEFAULT 0,
            last_seen          TEXT    NOT NULL DEFAULT '',
            last_seen_location TEXT    NOT NULL DEFAULT '',
            display_name       TEXT    NOT NULL DEFAULT '',
            image              TEXT    NOT NULL DEFAULT ''
        )";
        cmd.ExecuteNonQuery();

        using var idx = _db.CreateCommand();
        idx.CommandText = "CREATE INDEX IF NOT EXISTS idx_ut_lastseen ON user_tracking(last_seen DESC)";
        try { idx.ExecuteNonQuery(); } catch { }

        foreach (var col in new[]
        {
            "display_name                TEXT    NOT NULL DEFAULT ''",
            "image                       TEXT    NOT NULL DEFAULT ''",
            "profile_status              TEXT    NOT NULL DEFAULT ''",
            "profile_status_desc         TEXT    NOT NULL DEFAULT ''",
            "profile_bio                 TEXT    NOT NULL DEFAULT ''",
            "profile_location            TEXT    NOT NULL DEFAULT ''",
            "profile_is_friend           INTEGER NOT NULL DEFAULT 0",
            "profile_avatar_img          TEXT    NOT NULL DEFAULT ''",
            "profile_cached_at           TEXT    NOT NULL DEFAULT ''",
            "first_meet_date             TEXT    NOT NULL DEFAULT ''",
            "meet_again_count            INTEGER NOT NULL DEFAULT 0",
            "profile_last_login          TEXT    NOT NULL DEFAULT ''",
            "profile_last_activity       TEXT    NOT NULL DEFAULT ''",
            "profile_date_joined         TEXT    NOT NULL DEFAULT ''",
            "profile_world_name          TEXT    NOT NULL DEFAULT ''",
            "profile_world_thumb         TEXT    NOT NULL DEFAULT ''",
            "profile_instance_type       TEXT    NOT NULL DEFAULT ''",
            "profile_user_count          INTEGER NOT NULL DEFAULT 0",
            "profile_world_capacity      INTEGER NOT NULL DEFAULT 0",
            "profile_can_join            INTEGER NOT NULL DEFAULT 0",
            "profile_can_request_invite  INTEGER NOT NULL DEFAULT 0",
            "profile_can_invite          INTEGER NOT NULL DEFAULT 0",
            "profile_current_avatar_id   TEXT    NOT NULL DEFAULT ''",
            "profile_avatar_file_id      TEXT    NOT NULL DEFAULT ''",
            "profile_pic_override        TEXT    NOT NULL DEFAULT ''",
            "profile_tags                TEXT    NOT NULL DEFAULT '[]'",
            "profile_note                TEXT    NOT NULL DEFAULT ''",
            "profile_friend_key          TEXT    NOT NULL DEFAULT ''",
            "profile_traveling_to        TEXT    NOT NULL DEFAULT ''",
            "profile_state               TEXT    NOT NULL DEFAULT ''",
            "profile_last_platform       TEXT    NOT NULL DEFAULT ''",
            "profile_platform            TEXT    NOT NULL DEFAULT ''",
            "profile_user_note           TEXT    NOT NULL DEFAULT ''",
            "profile_in_same_instance    INTEGER NOT NULL DEFAULT 0",
            "profile_pronouns            TEXT    NOT NULL DEFAULT ''",
            "profile_age_verification    TEXT    NOT NULL DEFAULT ''",
            "profile_age_verified        INTEGER NOT NULL DEFAULT 0",
            "profile_bio_links           TEXT    NOT NULL DEFAULT '[]'",
            "profile_is_favorited        INTEGER NOT NULL DEFAULT 0",
            "profile_fav_friend_id       TEXT    NOT NULL DEFAULT ''",
            "profile_badges              TEXT    NOT NULL DEFAULT '[]'",
            "profile_represented_group   TEXT    NOT NULL DEFAULT ''",
            "groups                      TEXT    NOT NULL DEFAULT ''",
            "groups_cached_at            TEXT    NOT NULL DEFAULT ''",
            "content                     TEXT    NOT NULL DEFAULT ''",
            "content_cached_at           TEXT    NOT NULL DEFAULT ''",
            "mutuals                     TEXT    NOT NULL DEFAULT ''",
            "mutuals_cached_at           TEXT    NOT NULL DEFAULT ''",
            "mutual_groups               TEXT    NOT NULL DEFAULT ''",
            "mutual_groups_cached_at     TEXT    NOT NULL DEFAULT ''",
            "profile_current_avatar      TEXT    NOT NULL DEFAULT ''",
            "friend_alert                INTEGER NOT NULL DEFAULT 0",
        })
        {
            try
            {
                using var ac = _db.CreateCommand();
                ac.CommandText = $"ALTER TABLE user_tracking ADD COLUMN {col}";
                ac.ExecuteNonQuery();
            }
            catch { }
        }

        // group_tracking
        try
        {
            using var gc = _db.CreateCommand();
            gc.CommandText = @"CREATE TABLE IF NOT EXISTS group_tracking (
                group_id           TEXT PRIMARY KEY,
                name               TEXT NOT NULL DEFAULT '',
                short_code         TEXT NOT NULL DEFAULT '',
                description        TEXT NOT NULL DEFAULT '',
                icon_url           TEXT NOT NULL DEFAULT '',
                banner_url         TEXT NOT NULL DEFAULT '',
                member_count       INTEGER NOT NULL DEFAULT 0,
                privacy            TEXT NOT NULL DEFAULT '',
                join_state         TEXT NOT NULL DEFAULT '',
                owner_id           TEXT NOT NULL DEFAULT '',
                owner_display_name TEXT NOT NULL DEFAULT '',
                rules              TEXT NOT NULL DEFAULT '',
                languages          TEXT NOT NULL DEFAULT '',
                links              TEXT NOT NULL DEFAULT '',
                detail_cached_at   TEXT NOT NULL DEFAULT '',
                created_at         TEXT NOT NULL DEFAULT '',
                is_verified        INTEGER NOT NULL DEFAULT 0,
                joined_at          TEXT NOT NULL DEFAULT '',
                is_representing    INTEGER NOT NULL DEFAULT 0,
                last_post_json     TEXT NOT NULL DEFAULT '',
                last_event_json    TEXT NOT NULL DEFAULT ''
            )";
            gc.ExecuteNonQuery();
        }
        catch { }

        foreach (var col in new[]
        {
            "name               TEXT NOT NULL DEFAULT ''",
            "short_code         TEXT NOT NULL DEFAULT ''",
            "description        TEXT NOT NULL DEFAULT ''",
            "icon_url           TEXT NOT NULL DEFAULT ''",
            "banner_url         TEXT NOT NULL DEFAULT ''",
            "member_count       INTEGER NOT NULL DEFAULT 0",
            "privacy            TEXT NOT NULL DEFAULT ''",
            "join_state         TEXT NOT NULL DEFAULT ''",
            "owner_id           TEXT NOT NULL DEFAULT ''",
            "owner_display_name TEXT NOT NULL DEFAULT ''",
            "rules              TEXT NOT NULL DEFAULT ''",
            "languages          TEXT NOT NULL DEFAULT ''",
            "links              TEXT NOT NULL DEFAULT ''",
            "detail_cached_at   TEXT NOT NULL DEFAULT ''",
            "created_at         TEXT NOT NULL DEFAULT ''",
            "is_verified        INTEGER NOT NULL DEFAULT 0",
            "joined_at          TEXT NOT NULL DEFAULT ''",
            "is_representing    INTEGER NOT NULL DEFAULT 0",
            "last_post_json     TEXT NOT NULL DEFAULT ''",
            "last_event_json    TEXT NOT NULL DEFAULT ''",
        })
        {
            try { using var mc = _db.CreateCommand(); mc.CommandText = $"ALTER TABLE group_tracking ADD COLUMN {col}"; mc.ExecuteNonQuery(); } catch { }
        }

        // avatar_tracking
        try
        {
            using var ac2 = _db.CreateCommand();
            ac2.CommandText = @"CREATE TABLE IF NOT EXISTS avatar_tracking (
                avatar_id           TEXT PRIMARY KEY,
                name                TEXT NOT NULL DEFAULT '',
                author_name         TEXT NOT NULL DEFAULT '',
                author_id           TEXT NOT NULL DEFAULT '',
                thumbnail_image_url TEXT NOT NULL DEFAULT '',
                image_url           TEXT NOT NULL DEFAULT '',
                release_status      TEXT NOT NULL DEFAULT '',
                version             INTEGER NOT NULL DEFAULT 0,
                created_at          TEXT NOT NULL DEFAULT '',
                updated_at          TEXT NOT NULL DEFAULT '',
                description         TEXT NOT NULL DEFAULT '',
                tags                TEXT NOT NULL DEFAULT '',
                has_pc              INTEGER NOT NULL DEFAULT 0,
                has_quest           INTEGER NOT NULL DEFAULT 0,
                has_impostor        INTEGER NOT NULL DEFAULT 0,
                pc_perf             TEXT NOT NULL DEFAULT '',
                quest_perf          TEXT NOT NULL DEFAULT '',
                detail_cached_at    TEXT NOT NULL DEFAULT ''
            )";
            ac2.ExecuteNonQuery();
        }
        catch { }

        foreach (var col in new[]
        {
            "name                TEXT NOT NULL DEFAULT ''",
            "author_name         TEXT NOT NULL DEFAULT ''",
            "author_id           TEXT NOT NULL DEFAULT ''",
            "thumbnail_image_url TEXT NOT NULL DEFAULT ''",
            "image_url           TEXT NOT NULL DEFAULT ''",
            "release_status      TEXT NOT NULL DEFAULT ''",
            "version             INTEGER NOT NULL DEFAULT 0",
            "created_at          TEXT NOT NULL DEFAULT ''",
            "updated_at          TEXT NOT NULL DEFAULT ''",
            "description         TEXT NOT NULL DEFAULT ''",
            "tags                TEXT NOT NULL DEFAULT ''",
            "has_pc              INTEGER NOT NULL DEFAULT 0",
            "has_quest           INTEGER NOT NULL DEFAULT 0",
            "has_impostor        INTEGER NOT NULL DEFAULT 0",
            "pc_perf             TEXT NOT NULL DEFAULT ''",
            "quest_perf          TEXT NOT NULL DEFAULT ''",
            "detail_cached_at    TEXT NOT NULL DEFAULT ''",
        })
        {
            try { using var mc = _db.CreateCommand(); mc.CommandText = $"ALTER TABLE avatar_tracking ADD COLUMN {col}"; mc.ExecuteNonQuery(); } catch { }
        }

        // event_tracking
        try
        {
            using var ec = _db.CreateCommand();
            ec.CommandText = @"CREATE TABLE IF NOT EXISTS event_tracking (
                event_id         TEXT PRIMARY KEY,
                group_id         TEXT NOT NULL DEFAULT '',
                title            TEXT NOT NULL DEFAULT '',
                description      TEXT NOT NULL DEFAULT '',
                starts_at        TEXT NOT NULL DEFAULT '',
                ends_at          TEXT NOT NULL DEFAULT '',
                image_url        TEXT NOT NULL DEFAULT '',
                access_type      TEXT NOT NULL DEFAULT '',
                tags             TEXT NOT NULL DEFAULT '',
                owner_id         TEXT NOT NULL DEFAULT '',
                is_following     INTEGER NOT NULL DEFAULT 0,
                detail_cached_at TEXT NOT NULL DEFAULT ''
            )";
            ec.ExecuteNonQuery();
        }
        catch { }

        foreach (var col in new[]
        {
            "group_id         TEXT NOT NULL DEFAULT ''",
            "title            TEXT NOT NULL DEFAULT ''",
            "description      TEXT NOT NULL DEFAULT ''",
            "starts_at        TEXT NOT NULL DEFAULT ''",
            "ends_at          TEXT NOT NULL DEFAULT ''",
            "image_url        TEXT NOT NULL DEFAULT ''",
            "access_type      TEXT NOT NULL DEFAULT ''",
            "tags             TEXT NOT NULL DEFAULT ''",
            "owner_id         TEXT NOT NULL DEFAULT ''",
            "is_following     INTEGER NOT NULL DEFAULT 0",
            "detail_cached_at TEXT NOT NULL DEFAULT ''",
        })
        {
            try { using var mc = _db.CreateCommand(); mc.CommandText = $"ALTER TABLE event_tracking ADD COLUMN {col}"; mc.ExecuteNonQuery(); } catch { }
        }

        using var wcmd = _db.CreateCommand();
        wcmd.CommandText = @"CREATE TABLE IF NOT EXISTS world_tracking (
            world_id      TEXT    PRIMARY KEY,
            total_seconds INTEGER NOT NULL DEFAULT 0,
            visit_count   INTEGER NOT NULL DEFAULT 0,
            last_visited  TEXT    NOT NULL DEFAULT '',
            world_name    TEXT    NOT NULL DEFAULT '',
            world_thumb   TEXT    NOT NULL DEFAULT ''
        )";
        wcmd.ExecuteNonQuery();

        foreach (var col in new[]
        {
            "world_name                TEXT    NOT NULL DEFAULT ''",
            "world_thumb               TEXT    NOT NULL DEFAULT ''",
            "world_description         TEXT    NOT NULL DEFAULT ''",
            "world_image_url           TEXT    NOT NULL DEFAULT ''",
            "world_author_name         TEXT    NOT NULL DEFAULT ''",
            "world_author_id           TEXT    NOT NULL DEFAULT ''",
            "world_published           TEXT    NOT NULL DEFAULT ''",
            "world_updated             TEXT    NOT NULL DEFAULT ''",
            "world_capacity            INTEGER NOT NULL DEFAULT 0",
            "world_recommended_capacity INTEGER NOT NULL DEFAULT 0",
            "world_tags                TEXT    NOT NULL DEFAULT ''",
            "world_favorites           INTEGER NOT NULL DEFAULT 0",
            "world_visits              INTEGER NOT NULL DEFAULT 0",
            "world_pc_size             INTEGER NOT NULL DEFAULT 0",
            "world_android_size        INTEGER NOT NULL DEFAULT 0",
            "world_ios_size            INTEGER NOT NULL DEFAULT 0",
            "world_heat                INTEGER NOT NULL DEFAULT 0",
            "world_popularity          INTEGER NOT NULL DEFAULT 0",
            "world_public_occupants    INTEGER NOT NULL DEFAULT 0",
            "world_private_occupants   INTEGER NOT NULL DEFAULT 0",
            "world_version             INTEGER NOT NULL DEFAULT 0",
            "detail_cached_at          TEXT    NOT NULL DEFAULT ''",
        })
        {
            try
            {
                using var ac = _db.CreateCommand();
                ac.CommandText = $"ALTER TABLE world_tracking ADD COLUMN {col}";
                ac.ExecuteNonQuery();
            }
            catch { }
        }

        using var as_cmd = _db.CreateCommand();
        as_cmd.CommandText = @"CREATE TABLE IF NOT EXISTS active_session (
            id             INTEGER PRIMARY KEY CHECK(id = 1),
            location       TEXT    NOT NULL DEFAULT '',
            co_present_ids TEXT    NOT NULL DEFAULT '',
            last_flush_utc TEXT    NOT NULL DEFAULT ''
        )";
        as_cmd.ExecuteNonQuery();

        // local_favorite_groups
        try
        {
            using var lfg = _db.CreateCommand();
            lfg.CommandText = @"CREATE TABLE IF NOT EXISTS local_favorite_groups (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                name       TEXT NOT NULL DEFAULT '',
                fav_type   TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL DEFAULT ''
            )";
            lfg.ExecuteNonQuery();
        }
        catch { }

        foreach (var col in new[]
        {
            "name       TEXT NOT NULL DEFAULT ''",
            "fav_type   TEXT NOT NULL DEFAULT ''",
            "created_at TEXT NOT NULL DEFAULT ''",
        })
        {
            try { using var mc = _db.CreateCommand(); mc.CommandText = $"ALTER TABLE local_favorite_groups ADD COLUMN {col}"; mc.ExecuteNonQuery(); } catch { }
        }

        // local_favorite_entries
        try
        {
            using var lfe = _db.CreateCommand();
            lfe.CommandText = @"CREATE TABLE IF NOT EXISTS local_favorite_entries (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                group_id   INTEGER NOT NULL DEFAULT 0,
                item_id    TEXT NOT NULL DEFAULT '',
                item_type  TEXT NOT NULL DEFAULT '',
                added_at   TEXT NOT NULL DEFAULT ''
            )";
            lfe.ExecuteNonQuery();
        }
        catch { }

        foreach (var col in new[]
        {
            "group_id  INTEGER NOT NULL DEFAULT 0",
            "item_id   TEXT NOT NULL DEFAULT ''",
            "item_type TEXT NOT NULL DEFAULT ''",
            "added_at  TEXT NOT NULL DEFAULT ''",
        })
        {
            try { using var mc = _db.CreateCommand(); mc.CommandText = $"ALTER TABLE local_favorite_entries ADD COLUMN {col}"; mc.ExecuteNonQuery(); } catch { }
        }

        try { using var idx1 = _db.CreateCommand(); idx1.CommandText = "CREATE INDEX IF NOT EXISTS idx_lfe_group ON local_favorite_entries(group_id)"; idx1.ExecuteNonQuery(); } catch { }
        try { using var idx2 = _db.CreateCommand(); idx2.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_lfe_unique ON local_favorite_entries(group_id, item_id, item_type)"; idx2.ExecuteNonQuery(); } catch { }

        foreach (var col in new[]
        {
            "profile           TEXT NOT NULL DEFAULT ''",
            "groups            TEXT NOT NULL DEFAULT '[]'",
            "groups_cached_at  TEXT NOT NULL DEFAULT ''",
            "content           TEXT NOT NULL DEFAULT '{}'",
            "content_cached_at TEXT NOT NULL DEFAULT ''",
            "mutuals           TEXT NOT NULL DEFAULT '{}'",
            "mutuals_cached_at TEXT NOT NULL DEFAULT ''",
            "mutual_groups           TEXT NOT NULL DEFAULT '[]'",
            "mutual_groups_cached_at TEXT NOT NULL DEFAULT ''",
        })
        {
            try { using var mc = _db.CreateCommand(); mc.CommandText = $"ALTER TABLE user_tracking ADD COLUMN {col}"; mc.ExecuteNonQuery(); } catch { }
        }
    }

    private void MigrateUsersFromJson()
    {
        if (!File.Exists(UserLegacyPath)) return;
        try
        {
            var json = File.ReadAllText(UserLegacyPath);
            var legacy = JsonConvert.DeserializeObject<UserLegacy>(json);
            if (legacy?.Users == null) { File.Delete(UserLegacyPath); return; }
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT OR IGNORE INTO user_tracking(user_id,total_seconds,last_seen,last_seen_location)
                VALUES($uid,$ts,$ls,$lsl)";
            var pUid = cmd.Parameters.Add("$uid", SqliteType.Text);
            var pTs  = cmd.Parameters.Add("$ts",  SqliteType.Integer);
            var pLs  = cmd.Parameters.Add("$ls",  SqliteType.Text);
            var pLsl = cmd.Parameters.Add("$lsl", SqliteType.Text);
            foreach (var (userId, rec) in legacy.Users)
            {
                pUid.Value = userId; pTs.Value = rec.TotalSeconds;
                pLs.Value = rec.LastSeen ?? ""; pLsl.Value = rec.LastSeenLocation ?? "";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            File.Delete(UserLegacyPath);
        }
        catch { }
    }

    private void MigrateWorldsFromJson()
    {
        if (!File.Exists(WorldLegacyPath)) return;
        try
        {
            var json = File.ReadAllText(WorldLegacyPath);
            var legacy = JsonConvert.DeserializeObject<WorldLegacy>(json);
            if (legacy?.Worlds == null) { File.Delete(WorldLegacyPath); return; }
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT OR IGNORE INTO world_tracking(world_id,total_seconds,visit_count,last_visited)
                VALUES($wid,$ts,$vc,$lv)";
            var pWid = cmd.Parameters.Add("$wid", SqliteType.Text);
            var pTs  = cmd.Parameters.Add("$ts",  SqliteType.Integer);
            var pVc  = cmd.Parameters.Add("$vc",  SqliteType.Integer);
            var pLv  = cmd.Parameters.Add("$lv",  SqliteType.Text);
            foreach (var (worldId, rec) in legacy.Worlds)
            {
                pWid.Value = worldId; pTs.Value = rec.TotalSeconds;
                pVc.Value = rec.VisitCount; pLv.Value = rec.LastVisited ?? "";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            File.Delete(WorldLegacyPath);
        }
        catch { }
    }

    private void LoadUsersFromDb()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT user_id,total_seconds,last_seen,last_seen_location,display_name,image FROM user_tracking";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            Users[r.GetString(0)] = new UserRecord
            {
                TotalSeconds     = r.GetInt64(1),
                LastSeen         = r.GetString(2),
                LastSeenLocation = r.GetString(3),
                DisplayName      = r.GetString(4),
                Image            = r.GetString(5),
            };
    }

    private void LoadWorldsFromDb()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT world_id,total_seconds,visit_count,last_visited,world_name,world_thumb FROM world_tracking";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            Worlds[r.GetString(0)] = new WorldRecord
            {
                TotalSeconds = r.GetInt64(1),
                VisitCount   = r.GetInt32(2),
                LastVisited  = r.GetString(3),
                WorldName    = r.GetString(4),
                WorldThumb   = r.GetString(5),
            };
    }

    private void PersistUserLocked(string userId, DateTime now)
    {
        if (!Users.TryGetValue(userId, out var rec)) return;
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"INSERT INTO user_tracking(user_id,total_seconds,last_seen,last_seen_location,display_name,image)
                VALUES($uid,$ts,$ls,$lsl,$dn,$img)
                ON CONFLICT(user_id) DO UPDATE SET
                    total_seconds=excluded.total_seconds, last_seen=excluded.last_seen,
                    last_seen_location=excluded.last_seen_location,
                    display_name=CASE WHEN excluded.display_name!='' THEN excluded.display_name ELSE user_tracking.display_name END,
                    image=CASE WHEN excluded.image!='' THEN excluded.image ELSE user_tracking.image END";
            cmd.Parameters.AddWithValue("$uid", userId);
            cmd.Parameters.AddWithValue("$ts",  rec.TotalSeconds);
            cmd.Parameters.AddWithValue("$ls",  now.ToString("o"));
            cmd.Parameters.AddWithValue("$lsl", rec.LastSeenLocation);
            cmd.Parameters.AddWithValue("$dn",  rec.DisplayName);
            cmd.Parameters.AddWithValue("$img", rec.Image);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    private void PersistAllUsersLocked(IEnumerable<string> userIds, DateTime now)
    {
        try
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO user_tracking(user_id,total_seconds,last_seen,last_seen_location,display_name,image)
                VALUES($uid,$ts,$ls,$lsl,$dn,$img)
                ON CONFLICT(user_id) DO UPDATE SET
                    total_seconds=excluded.total_seconds, last_seen=excluded.last_seen,
                    last_seen_location=excluded.last_seen_location,
                    display_name=CASE WHEN excluded.display_name!='' THEN excluded.display_name ELSE user_tracking.display_name END,
                    image=CASE WHEN excluded.image!='' THEN excluded.image ELSE user_tracking.image END";
            var pUid = cmd.Parameters.Add("$uid", SqliteType.Text);
            var pTs  = cmd.Parameters.Add("$ts",  SqliteType.Integer);
            var pLs  = cmd.Parameters.Add("$ls",  SqliteType.Text);
            var pLsl = cmd.Parameters.Add("$lsl", SqliteType.Text);
            var pDn  = cmd.Parameters.Add("$dn",  SqliteType.Text);
            var pImg = cmd.Parameters.Add("$img", SqliteType.Text);
            var nowStr = now.ToString("o");
            foreach (var userId in userIds)
            {
                if (!Users.TryGetValue(userId, out var rec)) continue;
                pUid.Value = userId; pTs.Value = rec.TotalSeconds;
                pLs.Value = nowStr;  pLsl.Value = rec.LastSeenLocation;
                pDn.Value = rec.DisplayName; pImg.Value = rec.Image;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch { }
    }

    private void UpsertWorldLocked(string worldId, WorldRecord rec)
    {
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"INSERT INTO world_tracking(world_id,total_seconds,visit_count,last_visited,world_name,world_thumb)
                VALUES($wid,$ts,$vc,$lv,$wn,$wt)
                ON CONFLICT(world_id) DO UPDATE SET
                    total_seconds=excluded.total_seconds, visit_count=excluded.visit_count,
                    last_visited=excluded.last_visited,
                    world_name=CASE WHEN excluded.world_name!='' THEN excluded.world_name ELSE world_tracking.world_name END,
                    world_thumb=CASE WHEN excluded.world_thumb!='' THEN excluded.world_thumb ELSE world_tracking.world_thumb END";
            cmd.Parameters.AddWithValue("$wid", worldId);
            cmd.Parameters.AddWithValue("$ts",  rec.TotalSeconds);
            cmd.Parameters.AddWithValue("$vc",  rec.VisitCount);
            cmd.Parameters.AddWithValue("$lv",  rec.LastVisited);
            cmd.Parameters.AddWithValue("$wn",  rec.WorldName);
            cmd.Parameters.AddWithValue("$wt",  rec.WorldThumb);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    /// <summary>
    /// Persists active session state to DB for crash recovery.
    /// co_present_ids = JSON { "userId": "session_start_utc_iso", ... }
    /// last_flush_utc = world session start UTC ISO (for world time recovery)
    /// </summary>
    private void PersistActiveSessionLocked()
    {
        if (_playerSessions.Count == 0 && !_worldSessionStart.HasValue)
        {
            ClearActiveSessionLocked();
            return;
        }
        try
        {
            // Serialize per-player session starts as JSON
            var sessionsDict = _playerSessions.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ToString("o"));
            var sessionsJson = JsonConvert.SerializeObject(sessionsDict);

            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"INSERT INTO active_session(id,location,co_present_ids,last_flush_utc)
                VALUES(1,$loc,$ids,$ts)
                ON CONFLICT(id) DO UPDATE SET
                    location=excluded.location,
                    co_present_ids=excluded.co_present_ids,
                    last_flush_utc=excluded.last_flush_utc";
            cmd.Parameters.AddWithValue("$loc", _currentLocation);
            cmd.Parameters.AddWithValue("$ids", sessionsJson);
            cmd.Parameters.AddWithValue("$ts", _worldSessionStart?.ToString("o") ?? "");
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    private void ClearActiveSessionLocked()
    {
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM active_session WHERE id=1";
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    // per friend online alert level: 0=default, 1=always notify, -1=never notify
    public void SetFriendAlert(string userId, int level)
    {
        if (string.IsNullOrEmpty(userId)) return;
        lock (_lock)
        {
            try
            {
                using var ins = _db.CreateCommand();
                ins.CommandText = "INSERT OR IGNORE INTO user_tracking(user_id) VALUES($id)";
                ins.Parameters.AddWithValue("$id", userId);
                ins.ExecuteNonQuery();
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "UPDATE user_tracking SET friend_alert=$fa WHERE user_id=$id";
                cmd.Parameters.AddWithValue("$id", userId);
                cmd.Parameters.AddWithValue("$fa", level);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }

    public int GetFriendAlert(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return 0;
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT COALESCE(friend_alert,0) FROM user_tracking WHERE user_id=$id";
                cmd.Parameters.AddWithValue("$id", userId);
                var result = cmd.ExecuteScalar();
                return result is long l ? (int)l : 0;
            }
            catch { return 0; }
        }
    }

    // Local Favorite Groups

    public class LocalFavGroup
    {
        [JsonProperty("id")]
        public int Id { get; set; }
        [JsonProperty("name")]
        public string Name { get; set; } = "";
        [JsonProperty("favType")]
        public string FavType { get; set; } = "";
        [JsonProperty("createdAt")]
        public string CreatedAt { get; set; } = "";
    }

    public class LocalFavEntry
    {
        [JsonProperty("id")]
        public int Id { get; set; }
        [JsonProperty("groupId")]
        public int GroupId { get; set; }
        [JsonProperty("itemId")]
        public string ItemId { get; set; } = "";
        [JsonProperty("itemType")]
        public string ItemType { get; set; } = "";
        [JsonProperty("addedAt")]
        public string AddedAt { get; set; } = "";
    }

    public List<LocalFavGroup> GetLocalFavGroups(string favType)
    {
        lock (_lock)
        {
            var result = new List<LocalFavGroup>();
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT id,name,fav_type,created_at FROM local_favorite_groups WHERE fav_type=$ft ORDER BY name ASC";
                cmd.Parameters.AddWithValue("$ft", favType);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    result.Add(new LocalFavGroup { Id = r.GetInt32(0), Name = r.GetString(1), FavType = r.GetString(2), CreatedAt = r.GetString(3) });
            }
            catch { }
            return result;
        }
    }

    public int CreateLocalFavGroup(string name, string favType)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(favType)) return -1;
        lock (_lock)
        {
            try
            {
                var now = DateTime.UtcNow.ToString("o");
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "INSERT INTO local_favorite_groups(name,fav_type,created_at) VALUES($n,$ft,$ca); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$ft", favType);
                cmd.Parameters.AddWithValue("$ca", now);
                var result = cmd.ExecuteScalar();
                return result is long l ? (int)l : -1;
            }
            catch { return -1; }
        }
    }

    public bool DeleteLocalFavGroup(int groupId)
    {
        lock (_lock)
        {
            try
            {
                using var tx = _db.BeginTransaction();
                using var del1 = _db.CreateCommand();
                del1.Transaction = tx;
                del1.CommandText = "DELETE FROM local_favorite_entries WHERE group_id=$gid";
                del1.Parameters.AddWithValue("$gid", groupId);
                del1.ExecuteNonQuery();
                using var del2 = _db.CreateCommand();
                del2.Transaction = tx;
                del2.CommandText = "DELETE FROM local_favorite_groups WHERE id=$gid";
                del2.Parameters.AddWithValue("$gid", groupId);
                del2.ExecuteNonQuery();
                tx.Commit();
                return true;
            }
            catch { return false; }
        }
    }

    public bool RenameLocalFavGroup(int groupId, string newName)
    {
        if (string.IsNullOrEmpty(newName)) return false;
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "UPDATE local_favorite_groups SET name=$n WHERE id=$gid";
                cmd.Parameters.AddWithValue("$n", newName);
                cmd.Parameters.AddWithValue("$gid", groupId);
                cmd.ExecuteNonQuery();
                return true;
            }
            catch { return false; }
        }
    }

    public List<LocalFavEntry> GetLocalFavEntries(string favType, string? groupFilter = null)
    {
        lock (_lock)
        {
            var result = new List<LocalFavEntry>();
            try
            {
                using var cmd = _db.CreateCommand();
                if (string.IsNullOrEmpty(groupFilter))
                {
                    cmd.CommandText = @"SELECT e.id,e.group_id,e.item_id,e.item_type,e.added_at
                        FROM local_favorite_entries e
                        INNER JOIN local_favorite_groups g ON e.group_id=g.id
                        WHERE g.fav_type=$ft
                        ORDER BY e.added_at DESC";
                    cmd.Parameters.AddWithValue("$ft", favType);
                }
                else
                {
                    cmd.CommandText = "SELECT id,group_id,item_id,item_type,added_at FROM local_favorite_entries WHERE group_id=(SELECT id FROM local_favorite_groups WHERE name=$gn AND fav_type=$ft)";
                    cmd.Parameters.AddWithValue("$gn", groupFilter);
                    cmd.Parameters.AddWithValue("$ft", favType);
                }
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    result.Add(new LocalFavEntry { Id = r.GetInt32(0), GroupId = r.GetInt32(1), ItemId = r.GetString(2), ItemType = r.GetString(3), AddedAt = r.GetString(4) });
            }
            catch { }
            return result;
        }
    }

    public List<int> GetGroupsForItem(string itemId, string itemType)
    {
        lock (_lock)
        {
            var result = new List<int>();
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT group_id FROM local_favorite_entries WHERE item_id=$i AND item_type=$it";
                cmd.Parameters.AddWithValue("$i", itemId);
                cmd.Parameters.AddWithValue("$it", itemType);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    result.Add(r.GetInt32(0));
            }
            catch { }
            return result;
        }
    }

    public bool AddLocalFavItem(int groupId, string itemId, string itemType)
    {
        lock (_lock)
        {
            try
            {
                var now = DateTime.UtcNow.ToString("o");
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "INSERT OR IGNORE INTO local_favorite_entries(group_id,item_id,item_type,added_at) VALUES($gid,$i,$it,$ca)";
                cmd.Parameters.AddWithValue("$gid", groupId);
                cmd.Parameters.AddWithValue("$i", itemId);
                cmd.Parameters.AddWithValue("$it", itemType);
                cmd.Parameters.AddWithValue("$ca", now);
                cmd.ExecuteNonQuery();
                return true;
            }
            catch { return false; }
        }
    }

    public bool RemoveLocalFavItem(int groupId, string itemId, string itemType)
    {
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "DELETE FROM local_favorite_entries WHERE group_id=$gid AND item_id=$i AND item_type=$it";
                cmd.Parameters.AddWithValue("$gid", groupId);
                cmd.Parameters.AddWithValue("$i", itemId);
                cmd.Parameters.AddWithValue("$it", itemType);
                cmd.ExecuteNonQuery();
                return true;
            }
            catch { return false; }
        }
    }

    public bool RemoveItemFromAllGroups(string itemId, string itemType)
    {
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "DELETE FROM local_favorite_entries WHERE item_id=$i AND item_type=$it";
                cmd.Parameters.AddWithValue("$i", itemId);
                cmd.Parameters.AddWithValue("$it", itemType);
                cmd.ExecuteNonQuery();
                return true;
            }
            catch { return false; }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watchdogTimer?.Dispose();
        _watchdogTimer = null;
        lock (_lock)
        {
            var vrcRunning = _isVrcRunning?.Invoke() ?? false;
            if (vrcRunning && (_playerSessions.Count > 0 || _worldSessionStart.HasValue))
            {
                // VRC still running → VRCNext restart. Preserve active_session for RestoreActiveSession.
                PersistActiveSessionLocked();
            }
            else
            {
                var now = DateTime.UtcNow;
                EndAllPlayerSessionsLocked(now);
                EndWorldSessionLocked(now);
                ClearActiveSessionLocked();
            }
        }
        try { _monitoredVrcProcess?.Dispose(); } catch { }
        _monitoredVrcProcess = null;
        try { _db.Close(); } catch { }
        _db.Dispose();
    }

    private class UserLegacy { public Dictionary<string, UserRecord>? Users { get; set; } }
    private class WorldLegacy { public Dictionary<string, WorldRecord>? Worlds { get; set; } }

    public static string? ExtractWorldIdFromPng(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var sig = new byte[8];
            if (fs.Read(sig, 0, 8) != 8) return null;
            if (sig[0] != 137 || sig[1] != 80 || sig[2] != 78 || sig[3] != 71) return null;

            while (fs.Position < fs.Length - 8)
            {
                var lenBuf = new byte[4];
                if (fs.Read(lenBuf, 0, 4) != 4) break;
                int chunkLen = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];

                var typeBuf = new byte[4];
                if (fs.Read(typeBuf, 0, 4) != 4) break;
                var chunkType = System.Text.Encoding.ASCII.GetString(typeBuf);

                if (chunkType == "IEND") break;

                if ((chunkType == "tEXt" || chunkType == "iTXt" || chunkType == "zTXt") && chunkLen > 0 && chunkLen < 131072)
                {
                    var data = new byte[chunkLen];
                    if (fs.Read(data, 0, chunkLen) != chunkLen) break;

                    var text = System.Text.Encoding.UTF8.GetString(data);

                    if (text.Contains("wrld_"))
                    {
                        var idx = text.IndexOf("wrld_");
                        var end = idx;
                        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_' || text[end] == '-'))
                            end++;
                        var worldId = text.Substring(idx, end - idx);
                        if (worldId.Length > 10) return worldId;
                    }

                    fs.Seek(4, SeekOrigin.Current);
                    continue;
                }

                fs.Seek(chunkLen + 4, SeekOrigin.Current);
            }
        }
        catch { }
        return null;
    }

    public static (string? name, string? id) ExtractPhotoAuthorFromPng(string filePath)
    {
        string? name = null, id = null;
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var sig = new byte[8];
            if (fs.Read(sig, 0, 8) != 8) return (null, null);
            if (sig[0] != 137 || sig[1] != 80 || sig[2] != 78 || sig[3] != 71) return (null, null);

            while (fs.Position < fs.Length - 8)
            {
                var lenBuf = new byte[4];
                if (fs.Read(lenBuf, 0, 4) != 4) break;
                int chunkLen = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];

                var typeBuf = new byte[4];
                if (fs.Read(typeBuf, 0, 4) != 4) break;
                var chunkType = System.Text.Encoding.ASCII.GetString(typeBuf);

                if (chunkType == "IEND") break;

                if ((chunkType == "tEXt" || chunkType == "iTXt" || chunkType == "zTXt") && chunkLen > 0 && chunkLen < 131072)
                {
                    var data = new byte[chunkLen];
                    if (fs.Read(data, 0, chunkLen) != chunkLen) break;

                    var text = System.Text.Encoding.UTF8.GetString(data);

                    if (name == null)
                    {
                        var nm = System.Text.RegularExpressions.Regex.Match(text, @"<[A-Za-z]*:?Author>\s*([^<]+?)\s*</");
                        if (nm.Success)
                        {
                            var v = nm.Groups[1].Value.Trim();
                            if (v.Length > 0) name = v;
                        }
                    }

                    if (id == null)
                    {
                        var im = System.Text.RegularExpressions.Regex.Match(text, @"AuthorID>\s*(usr_[0-9a-fA-F\-]+)");
                        if (im.Success) id = im.Groups[1].Value;
                    }

                    fs.Seek(4, SeekOrigin.Current);
                    if (name != null && id != null) break;
                    continue;
                }

                fs.Seek(chunkLen + 4, SeekOrigin.Current);
            }
        }
        catch { }
        return (name, id);
    }
}