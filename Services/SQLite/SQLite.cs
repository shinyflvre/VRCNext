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
    private string _currentGroupId = "";

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
            _currentGroupId = ParseGroupId(_currentLocation);

            if (!string.IsNullOrEmpty(_currentGroupId))
                BumpGroupJoinLocked(_currentGroupId, now);

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
            _currentGroupId = ParseGroupId(_currentLocation);
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

    // fix time spent and get from user_tracking and world_tracking

    public class TimeSpentWorldRow
    {
        public string WorldId    { get; set; } = "";
        public string WorldName  { get; set; } = "";
        public string WorldThumb { get; set; } = "";
        public long   Seconds    { get; set; }
        public int    Visits     { get; set; }
        public long   Rank       { get; set; }
    }

    public class TimeSpentPersonRow
    {
        public string UserId      { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Image       { get; set; } = "";
        public long   Seconds     { get; set; }
        public long   Meets       { get; set; }
        public long   Rank        { get; set; }
    }

    public class TimeSpentGroupRow
    {
        public string GroupId   { get; set; } = "";
        public string GroupName { get; set; } = "";
        public string IconUrl   { get; set; } = "";
        public string ShortCode { get; set; } = "";
        public long   Seconds   { get; set; }
        public long   Joins     { get; set; }
        public long   Rank      { get; set; }
    }

    public class TimeSpentGroupPage
    {
        public List<TimeSpentGroupRow> Rows { get; } = new();
        public int    TotalFiltered { get; set; }
        public int    TotalAll      { get; set; }
        public long   TotalSeconds  { get; set; }
        public long   TotalJoins    { get; set; }
        public long   MaxSeconds    { get; set; }
        public string TopGroupName  { get; set; } = "";
    }

    public class TimeSpentWorldPage
    {
        public List<TimeSpentWorldRow> Rows { get; } = new();
        public int    TotalFiltered { get; set; }
        public int    TotalAll      { get; set; }
        public long   TotalSeconds  { get; set; }
        public long   TotalVisits   { get; set; }
        public long   MaxSeconds    { get; set; }
        public string TopWorldName  { get; set; } = "";
    }

    public class TimeSpentPersonPage
    {
        public List<TimeSpentPersonRow> Rows { get; } = new();
        public int  TotalFiltered { get; set; }
        public int  TotalAll      { get; set; }
        public long TotalSeconds  { get; set; }
        public long MaxSeconds    { get; set; }
    }

    private const string TsWorldWhere  = "total_seconds > 0 AND world_name <> ''";
    private const string TsPersonWhere = "total_seconds > 0 AND display_name <> '' AND user_id <> $self";
    private const string TsMeetsExpr   = "meet_again_count + CASE WHEN first_meet_date <> '' THEN 1 ELSE 0 END";

    private const string TsGroupWhere = "(time_total_seconds > 0 OR time_join_count > 0)";

    public TimeSpentGroupPage GetTimeSpentGroupPage(string query, int page, int pageSize)
    {
        var result = new TimeSpentGroupPage();
        var filter = string.IsNullOrEmpty(query)
            ? ""
            : " AND (instr(lower(name), $q) > 0 OR instr(lower(short_code), $q) > 0)";

        lock (_lock)
        {
            if (_disposed) return result;
            try
            {
                using (var cmd = _db.CreateCommand())
                {
                    cmd.CommandText = $@"SELECT COUNT(*), COALESCE(SUM(time_total_seconds),0),
                                                COALESCE(SUM(time_join_count),0), COALESCE(MAX(time_total_seconds),0)
                        FROM group_tracking WHERE {TsGroupWhere}";
                    using var r = cmd.ExecuteReader();
                    if (r.Read())
                    {
                        result.TotalAll     = r.GetInt32(0);
                        result.TotalSeconds = r.GetInt64(1);
                        result.TotalJoins   = r.GetInt64(2);
                        result.MaxSeconds   = r.GetInt64(3);
                    }
                }

                using (var cmd = _db.CreateCommand())
                {
                    cmd.CommandText = $"SELECT name FROM group_tracking WHERE {TsGroupWhere} ORDER BY time_total_seconds DESC LIMIT 1";
                    result.TopGroupName = cmd.ExecuteScalar() as string ?? "";
                }

                if (string.IsNullOrEmpty(query))
                    result.TotalFiltered = result.TotalAll;
                else
                {
                    using var cmd = _db.CreateCommand();
                    cmd.CommandText = $"SELECT COUNT(*) FROM group_tracking WHERE {TsGroupWhere}{filter}";
                    cmd.Parameters.AddWithValue("$q", query);
                    result.TotalFiltered = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
                }

                var skip = Math.Max(0, page) * (long)pageSize;
                using (var cmd = _db.CreateCommand())
                {
                    cmd.CommandText = string.IsNullOrEmpty(query)
                        ? $@"SELECT group_id, name, icon_url, short_code, time_total_seconds, time_join_count, 0
                            FROM group_tracking WHERE {TsGroupWhere}
                            ORDER BY time_total_seconds DESC, group_id ASC LIMIT $take OFFSET $skip"
                        : $@"SELECT group_id, name, icon_url, short_code, time_total_seconds, time_join_count, rnk FROM (
                                SELECT group_id, name, icon_url, short_code, time_total_seconds, time_join_count,
                                       ROW_NUMBER() OVER (ORDER BY time_total_seconds DESC, group_id ASC) AS rnk
                                FROM group_tracking WHERE {TsGroupWhere}
                             ) WHERE instr(lower(name), $q) > 0 OR instr(lower(short_code), $q) > 0
                            ORDER BY rnk LIMIT $take OFFSET $skip";
                    if (!string.IsNullOrEmpty(query)) cmd.Parameters.AddWithValue("$q", query);
                    cmd.Parameters.AddWithValue("$take", pageSize);
                    cmd.Parameters.AddWithValue("$skip", skip);
                    using var r = cmd.ExecuteReader();
                    long fallbackRank = skip;
                    while (r.Read())
                    {
                        fallbackRank++;
                        result.Rows.Add(new TimeSpentGroupRow
                        {
                            GroupId   = r.GetString(0),
                            GroupName = r.GetString(1),
                            IconUrl   = r.GetString(2),
                            ShortCode = r.GetString(3),
                            Seconds   = r.GetInt64(4),
                            Joins     = r.GetInt64(5),
                            Rank      = r.GetInt64(6) > 0 ? r.GetInt64(6) : fallbackRank,
                        });
                    }
                }
            }
            catch { }
        }
        return result;
    }

    public TimeSpentWorldPage GetTimeSpentWorldPage(string query, int page, int pageSize)
    {
        var result = new TimeSpentWorldPage();
        var filter = string.IsNullOrEmpty(query) ? "" : " AND instr(lower(world_name), $q) > 0";

        lock (_lock)
        {
            if (_disposed) return result;
            try
            {
                using (var cmd = _db.CreateCommand())
                {
                    cmd.CommandText = $@"SELECT COUNT(*), COALESCE(SUM(total_seconds),0), COALESCE(SUM(visit_count),0),
                                                COALESCE(MAX(total_seconds),0)
                        FROM world_tracking WHERE {TsWorldWhere}";
                    using var r = cmd.ExecuteReader();
                    if (r.Read())
                    {
                        result.TotalAll     = r.GetInt32(0);
                        result.TotalSeconds = r.GetInt64(1);
                        result.TotalVisits  = r.GetInt64(2);
                        result.MaxSeconds   = r.GetInt64(3);
                    }
                }

                using (var cmd = _db.CreateCommand())
                {
                    cmd.CommandText = $"SELECT world_name FROM world_tracking WHERE {TsWorldWhere} ORDER BY total_seconds DESC LIMIT 1";
                    result.TopWorldName = cmd.ExecuteScalar() as string ?? "";
                }

                if (string.IsNullOrEmpty(query))
                    result.TotalFiltered = result.TotalAll;
                else
                {
                    using var cmd = _db.CreateCommand();
                    cmd.CommandText = $"SELECT COUNT(*) FROM world_tracking WHERE {TsWorldWhere}{filter}";
                    cmd.Parameters.AddWithValue("$q", query);
                    result.TotalFiltered = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
                }

                var skip = Math.Max(0, page) * (long)pageSize;
                using (var cmd = _db.CreateCommand())
                {
                    cmd.CommandText = string.IsNullOrEmpty(query)
                        ? $@"SELECT world_id, world_name, world_thumb, total_seconds, visit_count, 0
                            FROM world_tracking WHERE {TsWorldWhere}
                            ORDER BY total_seconds DESC, world_id ASC LIMIT $take OFFSET $skip"
                        : $@"SELECT world_id, world_name, world_thumb, total_seconds, visit_count, rnk FROM (
                                SELECT world_id, world_name, world_thumb, total_seconds, visit_count,
                                       ROW_NUMBER() OVER (ORDER BY total_seconds DESC, world_id ASC) AS rnk
                                FROM world_tracking WHERE {TsWorldWhere}
                             ) WHERE instr(lower(world_name), $q) > 0
                            ORDER BY rnk LIMIT $take OFFSET $skip";
                    if (!string.IsNullOrEmpty(query)) cmd.Parameters.AddWithValue("$q", query);
                    cmd.Parameters.AddWithValue("$take", pageSize);
                    cmd.Parameters.AddWithValue("$skip", skip);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        result.Rows.Add(new TimeSpentWorldRow
                        {
                            WorldId    = r.GetString(0),
                            WorldName  = r.GetString(1),
                            WorldThumb = r.GetString(2),
                            Seconds    = r.GetInt64(3),
                            Visits     = r.GetInt32(4),
                            Rank       = r.GetInt64(5),
                        });
                }
                if (string.IsNullOrEmpty(query))
                    for (int i = 0; i < result.Rows.Count; i++) result.Rows[i].Rank = skip + i + 1;
            }
            catch { }
        }
        return result;
    }

    public TimeSpentPersonPage GetTimeSpentPersonPage(string selfId, string query, int page, int pageSize)
    {
        var result = new TimeSpentPersonPage();
        var filter = string.IsNullOrEmpty(query) ? "" : " AND instr(lower(display_name), $q) > 0";

        lock (_lock)
        {
            if (_disposed) return result;
            try
            {
                using (var cmd = _db.CreateCommand())
                {
                    cmd.CommandText = $@"SELECT COUNT(*), COALESCE(SUM(total_seconds),0), COALESCE(MAX(total_seconds),0)
                        FROM user_tracking WHERE {TsPersonWhere}";
                    cmd.Parameters.AddWithValue("$self", selfId);
                    using var r = cmd.ExecuteReader();
                    if (r.Read())
                    {
                        result.TotalAll     = r.GetInt32(0);
                        result.TotalSeconds = r.GetInt64(1);
                        result.MaxSeconds   = r.GetInt64(2);
                    }
                }

                if (string.IsNullOrEmpty(query))
                    result.TotalFiltered = result.TotalAll;
                else
                {
                    using var cmd = _db.CreateCommand();
                    cmd.CommandText = $"SELECT COUNT(*) FROM user_tracking WHERE {TsPersonWhere}{filter}";
                    cmd.Parameters.AddWithValue("$self", selfId);
                    cmd.Parameters.AddWithValue("$q", query);
                    result.TotalFiltered = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
                }

                var skip = Math.Max(0, page) * (long)pageSize;
                using (var cmd = _db.CreateCommand())
                {
                    cmd.CommandText = string.IsNullOrEmpty(query)
                        ? $@"SELECT user_id, display_name, image, total_seconds, {TsMeetsExpr}, 0
                            FROM user_tracking WHERE {TsPersonWhere}
                            ORDER BY total_seconds DESC, user_id ASC LIMIT $take OFFSET $skip"
                        : $@"SELECT user_id, display_name, image, total_seconds, meets, rnk FROM (
                                SELECT user_id, display_name, image, total_seconds, {TsMeetsExpr} AS meets,
                                       ROW_NUMBER() OVER (ORDER BY total_seconds DESC, user_id ASC) AS rnk
                                FROM user_tracking WHERE {TsPersonWhere}
                             ) WHERE instr(lower(display_name), $q) > 0
                            ORDER BY rnk LIMIT $take OFFSET $skip";
                    cmd.Parameters.AddWithValue("$self", selfId);
                    if (!string.IsNullOrEmpty(query)) cmd.Parameters.AddWithValue("$q", query);
                    cmd.Parameters.AddWithValue("$take", pageSize);
                    cmd.Parameters.AddWithValue("$skip", skip);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        result.Rows.Add(new TimeSpentPersonRow
                        {
                            UserId      = r.GetString(0),
                            DisplayName = r.GetString(1),
                            Image       = r.GetString(2),
                            Seconds     = r.GetInt64(3),
                            Meets       = r.GetInt64(4),
                            Rank        = r.GetInt64(5),
                        });
                }
                if (string.IsNullOrEmpty(query))
                    for (int i = 0; i < result.Rows.Count; i++) result.Rows[i].Rank = skip + i + 1;
            }
            catch { }
        }
        return result;
    }

    public List<TimeSpentPersonRow> GetAllTimeSpentPersonStats(string selfId)
    {
        var rows = new List<TimeSpentPersonRow>();

        lock (_lock)
        {
            if (_disposed) return rows;
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = $@"SELECT user_id, total_seconds, {TsMeetsExpr}
                    FROM user_tracking WHERE total_seconds > 0 AND user_id <> $self";
                cmd.Parameters.AddWithValue("$self", selfId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    rows.Add(new TimeSpentPersonRow
                    {
                        UserId  = r.GetString(0),
                        Seconds = r.GetInt64(1),
                        Meets   = r.GetInt64(2),
                    });
            }
            catch { }
        }
        return rows;
    }

    // Counts how many of the given users appear in the Time Spent person list.
    public int CountTimeSpentPersons(string selfId, ICollection<string> userIds)
    {
        if (userIds.Count == 0) return 0;
        var total = 0;

        lock (_lock)
        {
            if (_disposed) return 0;
            try
            {
                foreach (var chunk in userIds.Chunk(400))
                {
                    using var cmd = _db.CreateCommand();
                    var inP = string.Join(",", chunk.Select((_, i) => $"$u{i}"));
                    cmd.CommandText = $"SELECT COUNT(*) FROM user_tracking WHERE {TsPersonWhere} AND user_id IN ({inP})";
                    cmd.Parameters.AddWithValue("$self", selfId);
                    for (int i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue($"$u{i}", chunk[i]);
                    total += Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
                }
            }
            catch { return total; }
        }
        return total;
    }

    public (TimeSpentPersonRow? Friend, TimeSpentPersonRow? Stranger) GetTopTimeSpentPersons(
        string selfId, ICollection<string> friendIds)
    {
        TimeSpentPersonRow? friend = null, stranger = null;

        lock (_lock)
        {
            if (_disposed) return (null, null);
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = $@"SELECT user_id, display_name, total_seconds
                    FROM user_tracking WHERE {TsPersonWhere}
                    ORDER BY total_seconds DESC, user_id ASC";
                cmd.Parameters.AddWithValue("$self", selfId);
                using var r = cmd.ExecuteReader();
                while (r.Read() && (friend == null || stranger == null))
                {
                    var row = new TimeSpentPersonRow
                    {
                        UserId      = r.GetString(0),
                        DisplayName = r.GetString(1),
                        Seconds     = r.GetInt64(2),
                    };
                    if (friendIds.Contains(row.UserId)) friend ??= row;
                    else                                stranger ??= row;
                }
            }
            catch { }
        }
        return (friend, stranger);
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
        public bool   HasIos            { get; set; }
        public bool   HasImpostor       { get; set; }
        public string PcPerf            { get; set; } = "";
        public string QuestPerf         { get; set; } = "";
        public string IosPerf           { get; set; } = "";
    }

    private static readonly string[] _perfNames = { "excellent", "good", "medium", "poor", "verypoor" };

    private static string NormalizePerf(string value)
    {
        var key = new string((value ?? "").ToLowerInvariant().Where(char.IsLetter).ToArray());
        return Array.IndexOf(_perfNames, key) >= 0 ? value : "";
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
                    has_pc,has_quest,has_impostor,pc_perf,quest_perf,detail_cached_at,has_ios,ios_perf
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
                    PcPerf            = NormalizePerf(r.GetString(14)),
                    QuestPerf         = NormalizePerf(r.GetString(15)),
                    HasIos            = r.GetInt32(17) != 0,
                    IosPerf           = NormalizePerf(r.GetString(18)),
                };
            }
            catch { return null; }
        }
    }

    public void SaveAvatarDetail(string avatarId, string name, string authorName, string authorId,
        string thumbnailImageUrl, string imageUrl, string releaseStatus, int version,
        string createdAt, string updatedAt, string description, List<string> tags,
        bool hasPC, bool hasQuest, bool hasImpostor, string pcPerf, string questPerf,
        bool hasIos = false, string iosPerf = "")
    {
        if (string.IsNullOrEmpty(avatarId)) return;
        if (avatarId == "avtr_c38a1615-5bf5-42b4-84eb-a8b6c37cbd11") return;
        var now = DateTime.UtcNow.ToString("o");
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"INSERT INTO avatar_tracking(avatar_id,name,author_name,author_id,thumbnail_image_url,
                    image_url,release_status,version,created_at,updated_at,description,tags,
                    has_pc,has_quest,has_impostor,pc_perf,quest_perf,detail_cached_at,has_ios,ios_perf)
                    VALUES($id,$n,$an,$ai,$ti,$img,$rs,$ver,$ca,$ua,$desc,$tags,$hpc,$hq,$hi,$pcp,$qp,$cat,$his,$iosp)
                    ON CONFLICT(avatar_id) DO UPDATE SET
                        name=excluded.name, author_name=excluded.author_name, author_id=excluded.author_id,
                        thumbnail_image_url=excluded.thumbnail_image_url, image_url=excluded.image_url,
                        release_status=excluded.release_status, version=excluded.version,
                        created_at=excluded.created_at, updated_at=excluded.updated_at,
                        description=excluded.description, tags=excluded.tags,
                        has_pc=excluded.has_pc, has_quest=excluded.has_quest, has_impostor=excluded.has_impostor,
                        pc_perf=excluded.pc_perf, quest_perf=excluded.quest_perf, detail_cached_at=excluded.detail_cached_at,
                        has_ios=excluded.has_ios, ios_perf=excluded.ios_perf";
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
                cmd.Parameters.AddWithValue("$his",  hasIos ? 1 : 0);
                cmd.Parameters.AddWithValue("$iosp", iosPerf);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AVT-SAVE-ERR] {ex.Message}"); }
        }
    }

    // Avatar performance analysis cache

    public class AvatarAnalysisRow
    {
        public string Platform { get; set; } = "";
        public string FileId   { get; set; } = "";
        public int    Version  { get; set; }
        public string Json     { get; set; } = "";
        public string CachedAt { get; set; } = "";
    }

    public List<AvatarAnalysisRow> GetAvatarAnalysis(string avatarId)
    {
        var list = new List<AvatarAnalysisRow>();
        if (string.IsNullOrEmpty(avatarId)) return list;
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT platform,file_id,version,json,cached_at FROM avatar_analysis WHERE avatar_id=$id";
                cmd.Parameters.AddWithValue("$id", avatarId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    list.Add(new AvatarAnalysisRow
                    {
                        Platform = r.GetString(0), FileId = r.GetString(1), Version = r.GetInt32(2),
                        Json = r.GetString(3), CachedAt = r.GetString(4),
                    });
                }
            }
            catch { }
        }
        return list;
    }

    public void SaveAvatarAnalysis(string avatarId, string platform, string fileId, int version, string json)
    {
        if (string.IsNullOrEmpty(avatarId) || string.IsNullOrEmpty(platform)) return;
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"INSERT OR REPLACE INTO avatar_analysis(avatar_id,platform,file_id,version,json,cached_at)
                    VALUES($id,$p,$f,$v,$j,$t)";
                cmd.Parameters.AddWithValue("$id", avatarId);
                cmd.Parameters.AddWithValue("$p", platform);
                cmd.Parameters.AddWithValue("$f", fileId ?? "");
                cmd.Parameters.AddWithValue("$v", version);
                cmd.Parameters.AddWithValue("$j", json ?? "");
                cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            catch { }
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
        public string ProfileBannerUrl        { get; set; } = "";
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
        public string ProfileIconFrame        { get; set; } = "";
        public string ProfileNameplate        { get; set; } = "";
        public string ProfileEffect           { get; set; } = "";
        public string ProfileBgType           { get; set; } = "";
        public string ProfileBgTexture        { get; set; } = "";
        public string ProfileBgGradTop        { get; set; } = "";
        public string ProfileBgGradBottom     { get; set; } = "";
        public string ProfileThemeButton      { get; set; } = "";
        public string ProfileThemeIcon        { get; set; } = "";
        public string ProfileThemeSubtext     { get; set; } = "";
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
                    profile_current_avatar_id, profile_avatar_file_id, profile_pic_override, profile_banner_url,
                    profile_tags, profile_note, profile_friend_key, profile_traveling_to, profile_state,
                    profile_last_platform, profile_platform, profile_user_note, profile_in_same_instance,
                    profile_pronouns, profile_age_verification, profile_age_verified,
                    profile_bio_links, profile_is_favorited, profile_fav_friend_id, profile_badges,
                    profile_represented_group,
                    groups, groups_cached_at, content, content_cached_at,
                    mutuals, mutuals_cached_at, mutual_groups, mutual_groups_cached_at,
                    profile_current_avatar, profile_icon_frame, profile_nameplate, profile_effect,
                    profile_bg_type, profile_bg_texture, profile_bg_grad_top, profile_bg_grad_bottom,
                    profile_theme_button, profile_theme_icon, profile_theme_subtext
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
                    ProfileBannerUrl       = S("profile_banner_url"),
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
                    ProfileIconFrame       = S("profile_icon_frame"),
                    ProfileNameplate       = S("profile_nameplate"),
                    ProfileEffect          = S("profile_effect"),
                    ProfileBgType          = S("profile_bg_type"),
                    ProfileBgTexture       = S("profile_bg_texture"),
                    ProfileBgGradTop       = S("profile_bg_grad_top"),
                    ProfileBgGradBottom    = S("profile_bg_grad_bottom"),
                    ProfileThemeButton     = S("profile_theme_button"),
                    ProfileThemeIcon       = S("profile_theme_icon"),
                    ProfileThemeSubtext    = S("profile_theme_subtext"),
                };
                return string.IsNullOrEmpty(c.ProfileCachedAt) ? null : c;
            }
            catch { return null; }
        }
    }

    public Dictionary<string, (string dateJoined, string pronouns, int mutualFriends, int mutualGroups)>? GetUserFactsBatch(IReadOnlyList<string> userIds)
    {
        var result = new Dictionary<string, (string, string, int, int)>();
        if (userIds.Count == 0) return result;
        lock (_lock)
        {
            if (_disposed) return result;
            try
            {
                const int chunk = 400;
                for (int off = 0; off < userIds.Count; off += chunk)
                {
                    var slice = userIds.Skip(off).Take(chunk).ToList();
                    using var cmd = _db.CreateCommand();
                    var ps = string.Join(",", slice.Select((_, i) => $"$u{i}"));
                    cmd.CommandText = $@"SELECT user_id, profile_date_joined, profile_pronouns,
                        CASE WHEN json_valid(mutuals) AND COALESCE(json_extract(mutuals,'$.optedOut'),0) != 1
                             THEN COALESCE(json_array_length(mutuals,'$.mutuals'),0) ELSE 0 END,
                        CASE WHEN json_valid(mutual_groups) THEN COALESCE(json_array_length(mutual_groups),0) ELSE 0 END
                        FROM user_tracking
                        WHERE COALESCE(profile_cached_at,'') != '' AND user_id IN ({ps})";
                    for (int i = 0; i < slice.Count; i++) cmd.Parameters.AddWithValue($"$u{i}", slice[i]);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        result[r.GetString(0)] = (
                            r.IsDBNull(1) ? "" : r.GetString(1),
                            r.IsDBNull(2) ? "" : r.GetString(2),
                            r.IsDBNull(3) ? 0 : (int)r.GetInt64(3),
                            r.IsDBNull(4) ? 0 : (int)r.GetInt64(4));
                    }
                }
            }
            catch { return null; }
        }
        return result;
    }

    private static string Pick(JObject o, params string[] keys)
    {
        foreach (var k in keys)
        {
            var v = o[k]?.ToString();
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return "";
    }

    public void SaveUserProfileCache(string userId, string payloadJson)
    {
        if (string.IsNullOrEmpty(userId)) return;
        JObject p;
        try
        {
            using var sr = new System.IO.StringReader(payloadJson);
            using var jr = new JsonTextReader(sr) { DateParseHandling = DateParseHandling.None };
            p = JObject.Load(jr);
        }
        catch (Exception ex) { CrashHandler.WriteEntry("SaveUserProfileCache.Parse", ex); return; }
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
                    profile_last_login    = CASE WHEN $ll  <> '' THEN $ll  ELSE profile_last_login    END,
                    profile_last_activity = CASE WHEN $la  <> '' THEN $la  ELSE profile_last_activity END,
                    profile_date_joined   = CASE WHEN $dj  <> '' THEN $dj  ELSE profile_date_joined   END,
                    profile_world_name=$wn, profile_world_thumb=$wt, profile_instance_type=$it,
                    profile_user_count=$uc, profile_world_capacity=$wc, profile_can_join=$cj,
                    profile_can_request_invite=$cri, profile_can_invite=$ci,
                    profile_current_avatar_id=$caid, profile_avatar_file_id=$afid, profile_pic_override=$po,
                    profile_banner_url=$bnu,
                    profile_tags=$tags, profile_note=$note, profile_friend_key=$fk, profile_traveling_to=$tt,
                    profile_state=$state, profile_last_platform=$lp, profile_platform=$pl, profile_user_note=$un,
                    profile_in_same_instance=$isi,
                    profile_pronouns      = CASE WHEN $pro <> '' THEN $pro ELSE profile_pronouns      END,
                    profile_age_verification=$av,
                    profile_age_verified=$avd, profile_bio_links=$bl, profile_is_favorited=$ifav,
                    profile_fav_friend_id=$ffid, profile_badges=$badges,
                    profile_represented_group=$rg,
                    profile_icon_frame=$icf, profile_nameplate=$npl, profile_effect=$pfx,
                    profile_bg_type=$bgt, profile_bg_texture=$bgx, profile_bg_grad_top=$bgu, profile_bg_grad_bottom=$bgd,
                    profile_theme_button=$thb, profile_theme_icon=$thi, profile_theme_subtext=$ths
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
                cmd.Parameters.AddWithValue("$ll",    Pick(p, "lastLogin", "last_login"));
                cmd.Parameters.AddWithValue("$la",    Pick(p, "lastActivity", "last_activity"));
                cmd.Parameters.AddWithValue("$dj",    Pick(p, "dateJoined", "date_joined"));
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
                cmd.Parameters.AddWithValue("$bnu",   p["bannerUrl"]?.ToString() ?? "");
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
                cmd.Parameters.AddWithValue("$icf",    p["iconFrame"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$npl",    p["nameplateEffect"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$pfx",    p["profileEffect"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$bgt",    p["backgroundType"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$bgx",    p["backgroundTextureId"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$bgu",    p["backgroundGradientTop"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$bgd",    p["backgroundGradientBottom"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$thb",    p["themeButtonColor"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$thi",    p["themeIconColor"]?.ToString() ?? "");
                cmd.Parameters.AddWithValue("$ths",    p["themeSubtextColor"]?.ToString() ?? "");
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { CrashHandler.WriteEntry("SaveUserProfileCache.Write", ex); }
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
            catch (Exception ex) { CrashHandler.WriteEntry("SaveUserGroupsCache", ex); }
        }
    }

    public void SetAvatarInfoCache(string userId, string fileId, string avatarId, string name, string authorName, string imageUrl = "")
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(avatarId)) return;
        if (avatarId == "avtr_c38a1615-5bf5-42b4-84eb-a8b6c37cbd11") return;
        var json = JsonConvert.SerializeObject(new { fileId, avatarId, name, authorName, imageUrl });
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
            catch (Exception ex) { CrashHandler.WriteEntry("SetAvatarInfoCache", ex); }
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
            catch (Exception ex) { CrashHandler.WriteEntry("SaveUserContentCache", ex); }
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
            catch (Exception ex) { CrashHandler.WriteEntry("SaveUserMutualsCache", ex); }
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
            catch (Exception ex) { CrashHandler.WriteEntry("SaveUserMutualGroupsCache", ex); }
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
            catch (Exception ex) { CrashHandler.WriteEntry("CleanMutualCaches", ex); }
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
            catch (Exception ex) { CrashHandler.WriteEntry("SaveEventDetail", ex); }
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
            catch (Exception ex) { CrashHandler.WriteEntry("UpdateUserInfo", ex); }
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
            catch (Exception ex) { CrashHandler.WriteEntry("UpdateFriendTracking", ex); }
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
                            _currentGroupId = ParseGroupId(currentLocation);
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
                if (!string.IsNullOrEmpty(_currentGroupId))
                {
                    var (gName, gTotal) = AddGroupSecondsLocked(_currentGroupId, delta, now);
                    if (gName.Length == 0) gName = _currentGroupId;
                    _logger?.Invoke($"[TIMER] Group Time saved: +{delta}s — overall time in \"{gName}\": {FormatDuration(gTotal)}");
                }
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
        _currentGroupId = "";
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
        var elapsed = (long)(now - _worldSessionStart.Value).TotalSeconds;
        if (!string.IsNullOrEmpty(_currentWorldId) && _currentWorldId.StartsWith("wrld_"))
        {
            if (elapsed > 0 && elapsed <= 86400)
            {
                if (!Worlds.TryGetValue(_currentWorldId, out var rec))
                {
                    rec = new WorldRecord();
                    Worlds[_currentWorldId] = rec;
                }
                rec.TotalSeconds += elapsed;
                rec.LastVisited = now.ToString("o");
                UpsertWorldLocked(_currentWorldId, rec);
            }
        }
        _worldSessionStart = null;
    }

    internal static string ParseGroupId(string? location)
    {
        if (string.IsNullOrEmpty(location)) return "";
        var m = System.Text.RegularExpressions.Regex.Match(location, @"~group\((grp_[0-9A-Za-z-]+)\)");
        return m.Success ? m.Groups[1].Value : "";
    }

    public void SaveGroupTimeIdentity(string groupId, string name, string shortCode, string iconUrl)
    {
        if (string.IsNullOrEmpty(groupId)) return;
        lock (_lock)
        {
            if (_disposed) return;
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"INSERT INTO group_tracking(group_id,name,short_code,icon_url)
                    VALUES($gid,$n,$sc,$ic)
                    ON CONFLICT(group_id) DO UPDATE SET
                        name       = CASE WHEN excluded.name       <> '' THEN excluded.name       ELSE group_tracking.name       END,
                        short_code = CASE WHEN excluded.short_code <> '' THEN excluded.short_code ELSE group_tracking.short_code END,
                        icon_url   = CASE WHEN excluded.icon_url   <> '' THEN excluded.icon_url   ELSE group_tracking.icon_url   END";
                cmd.Parameters.AddWithValue("$gid", groupId);
                cmd.Parameters.AddWithValue("$n",  name ?? "");
                cmd.Parameters.AddWithValue("$sc", shortCode ?? "");
                cmd.Parameters.AddWithValue("$ic", iconUrl ?? "");
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }

    private void BumpGroupJoinLocked(string groupId, DateTime now)
    {
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"INSERT INTO group_tracking(group_id,time_join_count,time_last_visited)
                VALUES($gid,1,$lv)
                ON CONFLICT(group_id) DO UPDATE SET
                    time_join_count = time_join_count + 1,
                    time_last_visited = excluded.time_last_visited";
            cmd.Parameters.AddWithValue("$gid", groupId);
            cmd.Parameters.AddWithValue("$lv", now.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    private (string name, long total) AddGroupSecondsLocked(string groupId, long seconds, DateTime now)
    {
        try
        {
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = @"INSERT INTO group_tracking(group_id,time_total_seconds,time_last_visited)
                    VALUES($gid,$sec,$lv)
                    ON CONFLICT(group_id) DO UPDATE SET
                        time_total_seconds = time_total_seconds + excluded.time_total_seconds,
                        time_last_visited = excluded.time_last_visited";
                cmd.Parameters.AddWithValue("$gid", groupId);
                cmd.Parameters.AddWithValue("$sec", seconds);
                cmd.Parameters.AddWithValue("$lv", now.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            using (var q = _db.CreateCommand())
            {
                q.CommandText = "SELECT name, time_total_seconds FROM group_tracking WHERE group_id=$gid";
                q.Parameters.AddWithValue("$gid", groupId);
                using var r = q.ExecuteReader();
                if (r.Read()) return (r.GetString(0), r.GetInt64(1));
            }
        }
        catch { }
        return ("", 0);
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

        using var idxSec = _db.CreateCommand();
        idxSec.CommandText = "CREATE INDEX IF NOT EXISTS idx_ut_seconds ON user_tracking(total_seconds DESC)";
        try { idxSec.ExecuteNonQuery(); } catch { }

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
            "profile_banner_url          TEXT    NOT NULL DEFAULT ''",
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
            "last_status                 TEXT    NOT NULL DEFAULT ''",
            "last_status_at              TEXT    NOT NULL DEFAULT ''",
            "profile_icon_frame          TEXT    NOT NULL DEFAULT ''",
            "profile_nameplate           TEXT    NOT NULL DEFAULT ''",
            "profile_effect              TEXT    NOT NULL DEFAULT ''",
            "profile_bg_type             TEXT    NOT NULL DEFAULT ''",
            "profile_bg_texture          TEXT    NOT NULL DEFAULT ''",
            "profile_bg_grad_top         TEXT    NOT NULL DEFAULT ''",
            "profile_bg_grad_bottom      TEXT    NOT NULL DEFAULT ''",
            "profile_theme_button        TEXT    NOT NULL DEFAULT ''",
            "profile_theme_icon          TEXT    NOT NULL DEFAULT ''",
            "profile_theme_subtext       TEXT    NOT NULL DEFAULT ''",
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
            "time_total_seconds INTEGER NOT NULL DEFAULT 0",
            "time_join_count    INTEGER NOT NULL DEFAULT 0",
            "time_last_visited  TEXT NOT NULL DEFAULT ''",
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
            "has_ios             INTEGER NOT NULL DEFAULT 0",
            "ios_perf            TEXT NOT NULL DEFAULT ''",
        })
        {
            try { using var mc = _db.CreateCommand(); mc.CommandText = $"ALTER TABLE avatar_tracking ADD COLUMN {col}"; mc.ExecuteNonQuery(); } catch { }
        }

        // avatar_analysis
        try
        {
            using var aac = _db.CreateCommand();
            aac.CommandText = @"CREATE TABLE IF NOT EXISTS avatar_analysis (
                avatar_id  TEXT NOT NULL,
                platform   TEXT NOT NULL,
                file_id    TEXT NOT NULL DEFAULT '',
                version    INTEGER NOT NULL DEFAULT 0,
                json       TEXT NOT NULL DEFAULT '',
                cached_at  TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (avatar_id, platform)
            )";
            aac.ExecuteNonQuery();
        }
        catch { }

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

        using var widx = _db.CreateCommand();
        widx.CommandText = "CREATE INDEX IF NOT EXISTS idx_wt_seconds ON world_tracking(total_seconds DESC)";
        try { widx.ExecuteNonQuery(); } catch { }

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

    private const int PngHeadBytes = 16384;
    private const int PngTailBytes = 16384;
    private const int PngMaxTextChunk = 131072;

    private static void FillBuffer(FileStream fs, byte[] buf, int count)
    {
        int done = 0;
        while (done < count)
        {
            int n = fs.Read(buf, done, count - done);
            if (n <= 0) break;
            done += n;
        }
    }

    private static void ScanChunksInBuffer(byte[] buf, int length, List<string> texts)
    {
        int p = 0;
        while (p + 8 <= length)
        {
            int chunkLen = (buf[p] << 24) | (buf[p + 1] << 16) | (buf[p + 2] << 8) | buf[p + 3];
            if (chunkLen < 0) break;
            var type = System.Text.Encoding.ASCII.GetString(buf, p + 4, 4);
            if (type == "IEND") break;
            int dataStart = p + 8;
            if (type is "tEXt" or "iTXt" or "zTXt" && chunkLen > 0 && chunkLen < PngMaxTextChunk)
            {
                if (dataStart + chunkLen > length) break;
                texts.Add(System.Text.Encoding.UTF8.GetString(buf, dataStart, chunkLen));
            }
            long next = (long)dataStart + chunkLen + 4;
            if (next <= p || next > length) break;
            p = (int)next;
        }
    }

    private static void ScanTailForTextChunks(byte[] buf, int length, List<string> texts)
    {
        for (int i = 4; i + 8 <= length; i++)
        {
            if (buf[i] != (byte)'t' && buf[i] != (byte)'i' && buf[i] != (byte)'z') continue;
            var type = System.Text.Encoding.ASCII.GetString(buf, i, 4);
            if (type is not ("tEXt" or "iTXt" or "zTXt")) continue;
            int chunkLen = (buf[i - 4] << 24) | (buf[i - 3] << 16) | (buf[i - 2] << 8) | buf[i - 1];
            if (chunkLen <= 0 || chunkLen >= PngMaxTextChunk) continue;
            int dataStart = i + 4;
            if (dataStart + chunkLen + 4 > length) continue;
            texts.Add(System.Text.Encoding.UTF8.GetString(buf, dataStart, chunkLen));
            i = dataStart + chunkLen + 3;
        }
    }

    // Reads every PNG text chunk in one head read plus, when the file is larger, one tail read.
    // Metadata written before the image data is always covered by the head; trailing metadata by the tail.
    private static List<string> ReadPngTextChunks(string filePath)
    {
        var texts = new List<string>();
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                          PngHeadBytes, FileOptions.SequentialScan);
            var sig = new byte[8];
            if (fs.Read(sig, 0, 8) != 8) return texts;
            if (sig[0] != 137 || sig[1] != 80 || sig[2] != 78 || sig[3] != 71) return texts;

            long len = fs.Length;
            int headLen = (int)Math.Min(len - 8, PngHeadBytes);
            if (headLen <= 0) return texts;
            var head = new byte[headLen];
            FillBuffer(fs, head, headLen);
            ScanChunksInBuffer(head, headLen, texts);

            long covered = 8L + headLen;
            if (covered < len)
            {
                int tailLen = (int)Math.Min(len - covered, PngTailBytes);
                if (tailLen > 8)
                {
                    fs.Seek(len - tailLen, SeekOrigin.Begin);
                    var tail = new byte[tailLen];
                    FillBuffer(fs, tail, tailLen);
                    ScanTailForTextChunks(tail, tailLen, texts);
                }
            }
        }
        catch { }
        return texts;
    }

    private static string? WorldIdFromTexts(List<string> texts)
    {
        foreach (var text in texts)
        {
            var idx = text.IndexOf("wrld_", StringComparison.Ordinal);
            if (idx < 0) continue;
            var end = idx;
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_' || text[end] == '-'))
                end++;
            var worldId = text.Substring(idx, end - idx);
            if (worldId.Length > 10) return worldId;
        }
        return null;
    }

    private static (string? name, string? id) AuthorFromTexts(List<string> texts)
    {
        string? name = null, id = null;
        foreach (var text in texts)
        {
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
            if (name != null && id != null) break;
        }
        return (name, id);
    }

    // Single-pass variant: world id and author from one file read instead of two.
    public static (string? worldId, string? authorName, string? authorId) ExtractPhotoMetaFromPng(string filePath)
    {
        var texts = ReadPngTextChunks(filePath);
        if (texts.Count == 0) return (null, null, null);
        var (an, aid) = AuthorFromTexts(texts);
        return (WorldIdFromTexts(texts), an, aid);
    }

    public static string? ExtractWorldIdFromPng(string filePath)
        => WorldIdFromTexts(ReadPngTextChunks(filePath));

    public static (string? name, string? id) ExtractPhotoAuthorFromPng(string filePath)
        => AuthorFromTexts(ReadPngTextChunks(filePath));
}