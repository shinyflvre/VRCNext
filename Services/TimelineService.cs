using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VRCNext.Services;

// persists timeline events to SQLite. in-memory caches for fast lookups, incremental writes, auto-migrates from legacy JSON.
public class TimelineService : IDisposable
{
    public class FriendTimelineEvent
    {
        public string Id          { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Type        { get; set; } = "";
        public string Timestamp   { get; set; } = DateTime.UtcNow.ToString("o");
        public string FriendId    { get; set; } = "";
        public string FriendName  { get; set; } = "";
        public string FriendImage { get; set; } = "";
        public string WorldId     { get; set; } = "";
        public string WorldName   { get; set; } = "";
        public string WorldThumb  { get; set; } = "";
        public string Location    { get; set; } = "";
        public string OldValue    { get; set; } = "";
        public string NewValue    { get; set; } = "";
        public string LeftAt      { get; set; } = "";
        public int    Tracked     { get; set; } = 0;
    }

    public class PlayerSnap
    {
        public string UserId      { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Image       { get; set; } = "";
        public List<string> JoinedAts { get; set; } = new();
        public List<string> LeftAts   { get; set; } = new();

        public static List<string> ParseSessions(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return new();
            var trimmed = raw.TrimStart();
            if (trimmed.StartsWith("["))
            {
                try { return JsonConvert.DeserializeObject<List<string>>(raw) ?? new(); }
                catch { return new(); }
            }
            return new() { raw };
        }

        public static string SerializeSessions(List<string>? sessions)
        {
            if (sessions == null || sessions.Count == 0) return "";
            return JsonConvert.SerializeObject(sessions);
        }
    }

    public class TimelineEvent
    {
        public string Id        { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Type      { get; set; } = "";
        public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");

        public string WorldId    { get; set; } = "";
        public string WorldName  { get; set; } = "";
        public string WorldThumb { get; set; } = "";
        public string Location   { get; set; } = "";
        public List<PlayerSnap> Players { get; set; } = new();
        public string PhotoPath { get; set; } = "";
        public string PhotoUrl  { get; set; } = "";
        public string UserId    { get; set; } = "";
        public string UserName  { get; set; } = "";
        public string UserImage { get; set; } = "";
        public string NotifId      { get; set; } = "";
        public string NotifType    { get; set; } = "";
        public string NotifTitle   { get; set; } = "";
        public string SenderName   { get; set; } = "";
        public string SenderId     { get; set; } = "";
        public string SenderImage  { get; set; } = "";
        public string Message      { get; set; } = "";
        public string LeftAt       { get; set; } = "";
        public int    Tracked      { get; set; } = 0;
    }

    private readonly List<TimelineEvent>       _events       = new();
    private readonly List<FriendTimelineEvent> _friendEvents = new();
    private readonly HashSet<string>           _loggedNotifs = new();
    private readonly object                    _lock         = new();
    private bool                               _knownUsersSeeded;
    private bool                               _disposed;
    private bool                               _optimizeMode;
    private int                                _maxN;

    private readonly SqliteConnection _db;

    private static readonly string LegacyEventsJson = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "timeline_events.json");
    private static readonly string LegacyKnownUsersJson = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "timeline_known_users.json");

    public bool KnownUsersSeeded => _knownUsersSeeded;

    public HashSet<string> GetKnownUserIds()
    {
        try
        {
            var result = new HashSet<string>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT user_id FROM known_users";
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(r.GetString(0));
            return result;
        }
        catch { return new HashSet<string>(); }
    }

    private TimelineService(SqliteConnection db) { _db = db; }

    public static TimelineService Load(AppSettings settings)
    {
        var conn          = Database.OpenConnection();
        var svc           = new TimelineService(conn);
        svc._optimizeMode = settings.DbOptimize;
        svc._maxN         = Math.Clamp(settings.DbOptimizeMaxEntries, 500, 250000);
        svc.InitSchema();
        svc.MigrateFromJson();
        svc.LoadFromDb();
        return svc;
    }

    private void InitSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS events (
                id           TEXT PRIMARY KEY,
                type         TEXT NOT NULL DEFAULT '',
                timestamp    TEXT NOT NULL DEFAULT '',
                world_id     TEXT DEFAULT '',
                world_name   TEXT DEFAULT '',
                world_thumb  TEXT DEFAULT '',
                location     TEXT DEFAULT '',
                photo_path   TEXT DEFAULT '',
                photo_url    TEXT DEFAULT '',
                user_id      TEXT DEFAULT '',
                user_name    TEXT DEFAULT '',
                user_image   TEXT DEFAULT '',
                notif_id     TEXT DEFAULT '',
                notif_type   TEXT DEFAULT '',
                sender_name  TEXT DEFAULT '',
                sender_id    TEXT DEFAULT '',
                sender_image TEXT DEFAULT '',
                message      TEXT DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS event_players (
                event_id     TEXT NOT NULL,
                user_id      TEXT NOT NULL,
                display_name TEXT DEFAULT '',
                image        TEXT DEFAULT '',
                joined_at    TEXT DEFAULT '',
                left_at      TEXT DEFAULT '',
                PRIMARY KEY (event_id, user_id)
            );
            CREATE TABLE IF NOT EXISTS known_users (
                user_id TEXT PRIMARY KEY
            );
            CREATE TABLE IF NOT EXISTS logged_notifs (
                notif_id TEXT PRIMARY KEY
            );
            CREATE INDEX IF NOT EXISTS idx_events_ts   ON events(timestamp DESC);
            CREATE INDEX IF NOT EXISTS idx_events_type ON events(type);
            CREATE INDEX IF NOT EXISTS idx_ep_user     ON event_players(user_id);

            CREATE TABLE IF NOT EXISTS friend_events (
                id           TEXT PRIMARY KEY,
                type         TEXT NOT NULL DEFAULT '',
                timestamp    TEXT NOT NULL DEFAULT '',
                friend_id    TEXT DEFAULT '',
                friend_name  TEXT DEFAULT '',
                friend_image TEXT DEFAULT '',
                world_id     TEXT DEFAULT '',
                world_name   TEXT DEFAULT '',
                world_thumb  TEXT DEFAULT '',
                location     TEXT DEFAULT '',
                old_value    TEXT DEFAULT '',
                new_value    TEXT DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_fe_ts     ON friend_events(timestamp DESC);
            CREATE INDEX IF NOT EXISTS idx_fe_type   ON friend_events(type);
            CREATE INDEX IF NOT EXISTS idx_fe_friend ON friend_events(friend_id);
        ";
        cmd.ExecuteNonQuery();
        // Column migration — SQLite ADD COLUMN is idempotent with catch
        try { using var mc = _db.CreateCommand(); mc.CommandText = "ALTER TABLE events ADD COLUMN notif_title TEXT NOT NULL DEFAULT ''"; mc.ExecuteNonQuery(); } catch { }
        try { using var mc = _db.CreateCommand(); mc.CommandText = "ALTER TABLE events ADD COLUMN left_at  TEXT    DEFAULT NULL"; mc.ExecuteNonQuery(); } catch { }
        try { using var mc = _db.CreateCommand(); mc.CommandText = "ALTER TABLE events ADD COLUMN tracked  INTEGER NOT NULL DEFAULT 0"; mc.ExecuteNonQuery(); } catch { }
        try { using var mc = _db.CreateCommand(); mc.CommandText = "ALTER TABLE friend_events ADD COLUMN left_at  TEXT    DEFAULT NULL"; mc.ExecuteNonQuery(); } catch { }
        try { using var mc = _db.CreateCommand(); mc.CommandText = "ALTER TABLE friend_events ADD COLUMN tracked  INTEGER NOT NULL DEFAULT 0"; mc.ExecuteNonQuery(); } catch { }
        try { using var mc = _db.CreateCommand(); mc.CommandText = "ALTER TABLE event_players ADD COLUMN joined_at TEXT DEFAULT ''"; mc.ExecuteNonQuery(); } catch { }
        try { using var mc = _db.CreateCommand(); mc.CommandText = "ALTER TABLE event_players ADD COLUMN left_at   TEXT DEFAULT ''"; mc.ExecuteNonQuery(); } catch { }
        // Backfill event_players.left_at for rows that are empty but whose parent event is already closed
        try
        {
            using var mc = _db.CreateCommand();
            mc.CommandText = @"
                UPDATE event_players
                SET left_at = (SELECT left_at FROM events WHERE events.id = event_players.event_id)
                WHERE (left_at IS NULL OR left_at = '')
                  AND event_id IN (SELECT id FROM events WHERE left_at IS NOT NULL AND left_at != '')";
            mc.ExecuteNonQuery();
        }
        catch { }
        // Colocated friends per GPS event
        try
        {
            using var cc = _db.CreateCommand();
            cc.CommandText = @"
                CREATE TABLE IF NOT EXISTS friend_event_colocated (
                    event_id     TEXT NOT NULL,
                    friend_id    TEXT NOT NULL,
                    friend_name  TEXT NOT NULL DEFAULT '',
                    friend_image TEXT NOT NULL DEFAULT '',
                    PRIMARY KEY (event_id, friend_id)
                )";
            cc.ExecuteNonQuery();
        }
        catch { }
        // World Insights stats table
        try
        {
            using var ws = _db.CreateCommand();
            ws.CommandText = @"
                CREATE TABLE IF NOT EXISTS world_stats (
                    id        INTEGER PRIMARY KEY AUTOINCREMENT,
                    world_id  TEXT NOT NULL,
                    timestamp TEXT NOT NULL,
                    active    INTEGER NOT NULL DEFAULT 0,
                    favorites INTEGER NOT NULL DEFAULT 0,
                    visits    INTEGER NOT NULL DEFAULT 0
                );
                DROP INDEX IF EXISTS idx_ws_world_ts;
                CREATE UNIQUE INDEX IF NOT EXISTS idx_ws_world_ts ON world_stats(world_id, timestamp);
            ";
            ws.ExecuteNonQuery();
        }
        catch { }
        try
        {
            using var um = _db.CreateCommand();
            um.CommandText = @"
                CREATE TABLE IF NOT EXISTS user_memos (
                    user_id    TEXT PRIMARY KEY,
                    memo       TEXT NOT NULL DEFAULT '',
                    updated_at TEXT NOT NULL DEFAULT ''
                )";
            um.ExecuteNonQuery();
        }
        catch { }
        // Dedupe photo events caused by concurrent BootstrapPhotoTimeline runs.
        try
        {
            using var tx = _db.BeginTransaction();
            using var ddCmd = _db.CreateCommand();
            ddCmd.Transaction = tx;
            ddCmd.CommandText = @"
                DELETE FROM events
                WHERE type = 'photo'
                  AND photo_path != ''
                  AND rowid NOT IN (
                    SELECT MIN(rowid) FROM events
                    WHERE type = 'photo' AND photo_path != ''
                    GROUP BY photo_path
                  )";
            ddCmd.ExecuteNonQuery();

            using var orphanCmd = _db.CreateCommand();
            orphanCmd.Transaction = tx;
            orphanCmd.CommandText = "DELETE FROM event_players WHERE event_id NOT IN (SELECT id FROM events)";
            orphanCmd.ExecuteNonQuery();

            tx.Commit();
        }
        catch { }
    }

    private void MigrateFromJson()
    {
        if (File.Exists(LegacyEventsJson))
        {
            try
            {
                var json   = File.ReadAllText(LegacyEventsJson);
                var events = JsonConvert.DeserializeObject<List<TimelineEvent>>(json) ?? new();
                if (events.Count > 0)
                {
                    using var tx = _db.BeginTransaction();
                    foreach (var ev in events)
                        DbInsertEvent(ev, tx);
                    tx.Commit();
                }
                File.Delete(LegacyEventsJson);
            }
            catch { }
        }

        if (File.Exists(LegacyKnownUsersJson))
        {
            try
            {
                var json = File.ReadAllText(LegacyKnownUsersJson);
                var ids  = JsonConvert.DeserializeObject<List<string>>(json) ?? new();
                if (ids.Count > 0)
                {
                    using var tx  = _db.BeginTransaction();
                    using var cmd = _db.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT OR IGNORE INTO known_users(user_id) VALUES($id)";
                    var p = cmd.Parameters.Add("$id", SqliteType.Text);
                    foreach (var id in ids.Where(x => !string.IsNullOrEmpty(x)))
                    { p.Value = id; cmd.ExecuteNonQuery(); }
                    tx.Commit();
                }
                File.Delete(LegacyKnownUsersJson);
            }
            catch { }
        }
    }

    private void LoadFromDb()
    {
        if (_optimizeMode)
        {
            // Players only for the N most recent events (subquery avoids SQLite param-count limits)
            var optPlayerMap = new Dictionary<string, List<PlayerSnap>>();
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = @"SELECT ep.event_id,ep.user_id,ep.display_name,ep.image,ep.joined_at,ep.left_at
                    FROM event_players ep
                    WHERE ep.event_id IN (SELECT id FROM events ORDER BY timestamp DESC LIMIT $n)";
                cmd.Parameters.AddWithValue("$n", _maxN);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var eid = r.GetString(0);
                    if (!optPlayerMap.TryGetValue(eid, out var list)) optPlayerMap[eid] = list = new();
                    list.Add(new PlayerSnap {
                        UserId      = r.GetString(1),
                        DisplayName = r.GetString(2),
                        Image       = r.GetString(3),
                        JoinedAts   = PlayerSnap.ParseSessions(r.IsDBNull(4) ? "" : r.GetString(4)),
                        LeftAts     = PlayerSnap.ParseSessions(r.IsDBNull(5) ? "" : r.GetString(5)),
                    });
                }
            }

            // Latest N events — newest-first (DESC)
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = @"SELECT id,type,timestamp,world_id,world_name,world_thumb,
                    location,photo_path,photo_url,user_id,user_name,user_image,
                    notif_id,notif_type,notif_title,sender_name,sender_id,sender_image,message,
                    left_at,tracked
                    FROM events
                    WHERE id IN (SELECT id FROM events ORDER BY timestamp DESC LIMIT $n)
                    ORDER BY timestamp DESC";
                cmd.Parameters.AddWithValue("$n", _maxN);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var id = r.GetString(0);
                    var ev = new TimelineEvent
                    {
                        Id          = id,
                        Type        = r.GetString(1),
                        Timestamp   = r.GetString(2),
                        WorldId     = r.GetString(3),
                        WorldName   = r.GetString(4),
                        WorldThumb  = r.GetString(5),
                        Location    = r.GetString(6),
                        PhotoPath   = r.GetString(7),
                        PhotoUrl    = r.GetString(8),
                        UserId      = r.GetString(9),
                        UserName    = r.GetString(10),
                        UserImage   = r.GetString(11),
                        NotifId     = r.GetString(12),
                        NotifType   = r.GetString(13),
                        NotifTitle  = r.GetString(14),
                        SenderName  = r.GetString(15),
                        SenderId    = r.GetString(16),
                        SenderImage = r.GetString(17),
                        Message     = r.GetString(18),
                        LeftAt      = r.IsDBNull(19) ? "" : r.GetString(19),
                        Tracked     = r.GetInt32(20),
                        Players     = optPlayerMap.TryGetValue(id, out var pl) ? pl : new(),
                    };
                    _events.Add(ev);
                    if (ev.Type == "notification" && !string.IsNullOrEmpty(ev.NotifId))
                        _loggedNotifs.Add(ev.NotifId);
                }
            }

            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(1) FROM known_users LIMIT 1";
                _knownUsersSeeded = (long)(cmd.ExecuteScalar() ?? 0L) > 0;
            }

            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT notif_id FROM logged_notifs ORDER BY rowid DESC LIMIT 2000";
                using var r = cmd.ExecuteReader();
                while (r.Read()) _loggedNotifs.Add(r.GetString(0));
            }

            // Latest N friend events — newest-first (DESC)
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = @"SELECT id,type,timestamp,friend_id,friend_name,friend_image,
                    world_id,world_name,world_thumb,location,old_value,new_value,left_at,tracked
                    FROM friend_events
                    WHERE id IN (SELECT id FROM friend_events ORDER BY timestamp DESC LIMIT $n)
                    ORDER BY timestamp DESC";
                cmd.Parameters.AddWithValue("$n", _maxN);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    _friendEvents.Add(new FriendTimelineEvent
                    {
                        Id          = r.GetString(0),
                        Type        = r.GetString(1),
                        Timestamp   = r.GetString(2),
                        FriendId    = r.GetString(3),
                        FriendName  = r.GetString(4),
                        FriendImage = r.GetString(5),
                        WorldId     = r.GetString(6),
                        WorldName   = r.GetString(7),
                        WorldThumb  = r.GetString(8),
                        Location    = r.GetString(9),
                        OldValue    = r.GetString(10),
                        NewValue    = r.GetString(11),
                        LeftAt      = r.IsDBNull(12) ? "" : r.GetString(12),
                        Tracked     = r.GetInt32(13),
                    });
            }

            return; // skip full load
        }

        var playerMap = new Dictionary<string, List<PlayerSnap>>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT event_id, user_id, display_name, image, joined_at, left_at FROM event_players";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var eid = r.GetString(0);
                if (!playerMap.TryGetValue(eid, out var list))
                    playerMap[eid] = list = new();
                list.Add(new PlayerSnap
                {
                    UserId      = r.GetString(1),
                    DisplayName = r.GetString(2),
                    Image       = r.GetString(3),
                    JoinedAts   = PlayerSnap.ParseSessions(r.IsDBNull(4) ? "" : r.GetString(4)),
                    LeftAts     = PlayerSnap.ParseSessions(r.IsDBNull(5) ? "" : r.GetString(5)),
                });
            }
        }

        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = @"SELECT id,type,timestamp,world_id,world_name,world_thumb,
                location,photo_path,photo_url,user_id,user_name,user_image,
                notif_id,notif_type,notif_title,sender_name,sender_id,sender_image,message,
                left_at,tracked
                FROM events ORDER BY timestamp ASC";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetString(0);
                var ev = new TimelineEvent
                {
                    Id          = id,
                    Type        = r.GetString(1),
                    Timestamp   = r.GetString(2),
                    WorldId     = r.GetString(3),
                    WorldName   = r.GetString(4),
                    WorldThumb  = r.GetString(5),
                    Location    = r.GetString(6),
                    PhotoPath   = r.GetString(7),
                    PhotoUrl    = r.GetString(8),
                    UserId      = r.GetString(9),
                    UserName    = r.GetString(10),
                    UserImage   = r.GetString(11),
                    NotifId     = r.GetString(12),
                    NotifType   = r.GetString(13),
                    NotifTitle  = r.GetString(14),
                    SenderName  = r.GetString(15),
                    SenderId    = r.GetString(16),
                    SenderImage = r.GetString(17),
                    Message     = r.GetString(18),
                    LeftAt      = r.IsDBNull(19) ? "" : r.GetString(19),
                    Tracked     = r.GetInt32(20),
                    Players     = playerMap.TryGetValue(id, out var pl) ? pl : new(),
                };
                _events.Add(ev);
                if (ev.Type == "notification" && !string.IsNullOrEmpty(ev.NotifId))
                    _loggedNotifs.Add(ev.NotifId);
            }
        }

        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(1) FROM known_users LIMIT 1";
            _knownUsersSeeded = (long)(cmd.ExecuteScalar() ?? 0L) > 0;
        }

        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT notif_id FROM logged_notifs ORDER BY rowid DESC LIMIT 2000";
            using var r = cmd.ExecuteReader();
            while (r.Read()) _loggedNotifs.Add(r.GetString(0));
        }

        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = @"SELECT id,type,timestamp,friend_id,friend_name,friend_image,
                world_id,world_name,world_thumb,location,old_value,new_value,left_at,tracked
                FROM friend_events ORDER BY timestamp ASC";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                _friendEvents.Add(new FriendTimelineEvent
                {
                    Id          = r.GetString(0),
                    Type        = r.GetString(1),
                    Timestamp   = r.GetString(2),
                    FriendId    = r.GetString(3),
                    FriendName  = r.GetString(4),
                    FriendImage = r.GetString(5),
                    WorldId     = r.GetString(6),
                    WorldName   = r.GetString(7),
                    WorldThumb  = r.GetString(8),
                    Location    = r.GetString(9),
                    OldValue    = r.GetString(10),
                    NewValue    = r.GetString(11),
                    LeftAt      = r.IsDBNull(12) ? "" : r.GetString(12),
                    Tracked     = r.GetInt32(13),
                });
            }
        }
    }

    public void AddEvent(TimelineEvent ev)
    {
        lock (_lock)
        {
            if (_optimizeMode)
            {
                _events.Insert(0, ev);
                if (_events.Count > _maxN) _events.RemoveAt(_events.Count - 1);
            }
            else
                _events.Add(ev);
            DbInsertEvent(ev, null);
        }

        if (ev.Type == "first_meet" && !string.IsNullOrEmpty(ev.UserId))
            DbSetFirstMeetDate(ev.UserId, ev.Timestamp);
        else if (ev.Type == "meet_again" && !string.IsNullOrEmpty(ev.UserId))
            DbIncrementMeetAgain(ev.UserId);
    }

    public void BulkImportEvents(IEnumerable<TimelineEvent> events)
    {
        lock (_lock)
        {
            try
            {
                var have = new HashSet<string>(StringComparer.Ordinal);
                foreach (var e in _events) have.Add(e.Id);

                var newEvents = new List<TimelineEvent>();
                using var tx = _db.BeginTransaction();
                foreach (var ev in events)
                {
                    DbInsertIgnoreEvent(ev, tx);
                    if (have.Add(ev.Id)) newEvents.Add(ev);
                }
                tx.Commit();
                _events.AddRange(newEvents);
            }
            catch (Exception ex) { CrashHandler.WriteEntry("BulkImportEvents", ex); }

            if (_optimizeMode)
            {
                _events.Sort((a, b) => string.Compare(b.Timestamp, a.Timestamp, StringComparison.Ordinal));
                if (_events.Count > _maxN) _events.RemoveRange(_maxN, _events.Count - _maxN);
            }
        }
    }

    public void BulkImportFriendEvents(IEnumerable<FriendTimelineEvent> events)
    {
        lock (_lock)
        {
            try
            {
                var have = new HashSet<string>(StringComparer.Ordinal);
                foreach (var e in _friendEvents) have.Add(e.Id);

                var newEvents = new List<FriendTimelineEvent>();
                using var tx = _db.BeginTransaction();
                foreach (var ev in events)
                {
                    DbInsertIgnoreFriendEvent(ev, tx);
                    if (have.Add(ev.Id)) newEvents.Add(ev);
                }
                tx.Commit();
                _friendEvents.AddRange(newEvents);
            }
            catch (Exception ex) { CrashHandler.WriteEntry("BulkImportFriendEvents", ex); }

            if (_optimizeMode)
            {
                _friendEvents.Sort((a, b) => string.Compare(b.Timestamp, a.Timestamp, StringComparison.Ordinal));
                if (_friendEvents.Count > _maxN) _friendEvents.RemoveRange(_maxN, _friendEvents.Count - _maxN);
            }
        }
    }

    public void UpdateEvent(string id, Action<TimelineEvent> update)
    {
        TimelineEvent? ev;
        lock (_lock) ev = _events.FirstOrDefault(e => e.Id == id);
        if (ev == null) return;
        lock (_lock)
        {
            update(ev);
            DbUpdateEvent(ev);
        }
    }

    public List<TimelineEvent> GetEvents()
    {
        lock (_lock)
            return _events.OrderByDescending(e => e.Timestamp).ToList();
    }

    public bool DeleteEvent(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        lock (_lock)
        {
            int n;
            try
            {
                using var tx = _db.BeginTransaction();
                using (var c = _db.CreateCommand())
                {
                    c.Transaction = tx;
                    c.CommandText = "DELETE FROM event_players WHERE event_id=$id";
                    c.Parameters.AddWithValue("$id", id);
                    c.ExecuteNonQuery();
                }
                using (var c = _db.CreateCommand())
                {
                    c.Transaction = tx;
                    c.CommandText = "DELETE FROM events WHERE id=$id";
                    c.Parameters.AddWithValue("$id", id);
                    n = c.ExecuteNonQuery();
                }
                tx.Commit();
            }
            catch { return false; }
            _events.RemoveAll(e => e.Id == id);
            return n > 0;
        }
    }

    public int DeleteEvents(IEnumerable<string> ids)
    {
        var list = ids?.Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList() ?? new();
        if (list.Count == 0) return 0;
        int total = 0;
        lock (_lock)
        {
            try
            {
                using var tx = _db.BeginTransaction();
                using var delP = _db.CreateCommand();
                using var delE = _db.CreateCommand();
                delP.Transaction = tx; delE.Transaction = tx;
                delP.CommandText = "DELETE FROM event_players WHERE event_id=$id";
                delE.CommandText = "DELETE FROM events WHERE id=$id";
                var pP = delP.Parameters.Add("$id", SqliteType.Text);
                var pE = delE.Parameters.Add("$id", SqliteType.Text);
                foreach (var id in list)
                {
                    pP.Value = id; delP.ExecuteNonQuery();
                    pE.Value = id; total += delE.ExecuteNonQuery();
                }
                tx.Commit();
            }
            catch { return 0; }
            var set = new HashSet<string>(list);
            _events.RemoveAll(e => set.Contains(e.Id));
        }
        return total;
    }

    public int DeleteEventsByType(string type, int limit)
    {
        lock (_lock)
        {
            var ids = new List<string>();
            try
            {
                using (var sel = _db.CreateCommand())
                {
                    var typeClause = string.IsNullOrEmpty(type) ? "" : "WHERE type=$type";
                    sel.CommandText = limit > 0
                        ? $"SELECT id FROM events {typeClause} ORDER BY timestamp DESC LIMIT $n"
                        : $"SELECT id FROM events {typeClause}";
                    if (!string.IsNullOrEmpty(type)) sel.Parameters.AddWithValue("$type", type);
                    if (limit > 0) sel.Parameters.AddWithValue("$n", limit);
                    using var r = sel.ExecuteReader();
                    while (r.Read()) ids.Add(r.GetString(0));
                }
                if (ids.Count == 0) return 0;

                using var tx = _db.BeginTransaction();
                using var delP = _db.CreateCommand();
                using var delE = _db.CreateCommand();
                delP.Transaction = tx; delE.Transaction = tx;
                delP.CommandText = "DELETE FROM event_players WHERE event_id=$id";
                delE.CommandText = "DELETE FROM events WHERE id=$id";
                var pP = delP.Parameters.Add("$id", SqliteType.Text);
                var pE = delE.Parameters.Add("$id", SqliteType.Text);
                foreach (var id in ids)
                {
                    pP.Value = id; delP.ExecuteNonQuery();
                    pE.Value = id; delE.ExecuteNonQuery();
                }
                tx.Commit();
            }
            catch { return 0; }
            var set = new HashSet<string>(ids);
            _events.RemoveAll(e => set.Contains(e.Id));
            return ids.Count;
        }
    }

    public List<string> PruneOrphanedPhotos(Action<int>? onProgress = null)
    {
        var deleted = new List<string>();
        List<(string Id, string Path)> photos;
        lock (_lock)
        {
            photos = _events
                .Where(e => e.Type == "photo" && !string.IsNullOrEmpty(e.PhotoPath))
                .Select(e => (e.Id, e.PhotoPath))
                .ToList();
        }
        if (photos.Count == 0) return deleted;

        int total = photos.Count, done = 0;
        var orphanPaths = new List<string>();
        var orphanIds   = new List<string>();

        foreach (var (id, path) in photos)
        {
            if (!File.Exists(path)) { orphanPaths.Add(path); orphanIds.Add(id); }
            done++;
            if (done % 20 == 0 || done == total)
                onProgress?.Invoke(5 + (int)(done * 85.0 / total));
        }

        if (orphanPaths.Count == 0) return deleted;

        lock (_lock)
        {
            try
            {
                using var tx  = _db.BeginTransaction();
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM events WHERE photo_path = $p";
                var p = cmd.Parameters.Add("$p", Microsoft.Data.Sqlite.SqliteType.Text);
                foreach (var path in orphanPaths) { p.Value = path; cmd.ExecuteNonQuery(); }
                tx.Commit();
                var set = new HashSet<string>(orphanIds, StringComparer.Ordinal);
                _events.RemoveAll(e => set.Contains(e.Id));
                deleted.AddRange(orphanIds);
            }
            catch { }
        }
        return deleted;
    }

    public HashSet<string> GetPhotoFilePaths()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT photo_path FROM events WHERE type = 'photo' AND photo_path != ''";
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(Path.GetFileName(r.GetString(0)));
        }
        catch { }
        return result;
    }

    public long GetMeetAgainCount(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return 0;
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"SELECT meet_again_count + CASE WHEN first_meet_date != '' THEN 1 ELSE 0 END
                FROM user_tracking WHERE user_id = $uid";
            cmd.Parameters.AddWithValue("$uid", userId);
            var val = cmd.ExecuteScalar();
            return val is null or DBNull ? 0L : Convert.ToInt64(val);
        }
        catch { return 0; }
    }

    public string GetFirstMeetDate(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return "";
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT first_meet_date FROM user_tracking WHERE user_id = $uid";
            cmd.Parameters.AddWithValue("$uid", userId);
            var val = cmd.ExecuteScalar();
            return val is string s ? s : "";
        }
        catch { return ""; }
    }

    private void DbSetFirstMeetDate(string userId, string timestamp)
    {
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"INSERT INTO user_tracking (user_id, total_seconds, last_seen, last_seen_location, display_name, image, first_meet_date, meet_again_count)
                VALUES ($uid, 0, '', '', '', '', $ts, 0)
                ON CONFLICT(user_id) DO UPDATE SET
                    first_meet_date = CASE WHEN first_meet_date = '' THEN excluded.first_meet_date ELSE first_meet_date END";
            cmd.Parameters.AddWithValue("$uid", userId);
            cmd.Parameters.AddWithValue("$ts",  timestamp);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    private void DbIncrementMeetAgain(string userId)
    {
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"INSERT INTO user_tracking (user_id, total_seconds, last_seen, last_seen_location, display_name, image, first_meet_date, meet_again_count)
                VALUES ($uid, 0, '', '', '', '', '', 1)
                ON CONFLICT(user_id) DO UPDATE SET meet_again_count = meet_again_count + 1";
            cmd.Parameters.AddWithValue("$uid", userId);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    // Returns ISO timestamp of the last time we were in the same instance as userId
    // (most recent meet_again or first_meet event)
    public string GetLastSeenTimestamp(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return "";
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT MAX(timestamp) FROM events WHERE user_id = $uid AND type IN ('meet_again', 'first_meet')";
            cmd.Parameters.AddWithValue("$uid", userId);
            var val = cmd.ExecuteScalar();
            return val is string s ? s : "";
        }
        catch { return ""; }
    }

    public long GetEventCount(string typeFilter = "")
    {
        if (_optimizeMode && string.IsNullOrEmpty(typeFilter))
        {
            lock (_lock)
            {
                return _events.Count;
            }
        }

        try
        {
            using var cmd = _db.CreateCommand();
            var typeClause = string.IsNullOrEmpty(typeFilter) ? "" : "WHERE type = $type";
            cmd.CommandText = $"SELECT COUNT(*) FROM events {typeClause}";
            if (!string.IsNullOrEmpty(typeFilter)) cmd.Parameters.AddWithValue("$type", typeFilter);
            var count = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            return (_optimizeMode && count > _maxN) ? (long)_maxN : count;
        }
        catch { return 0; }
    }

    public long GetFriendEventCount(string typeFilter = "")
    {
        var hasTypeCount = !string.IsNullOrEmpty(typeFilter) && typeFilter != "all";
        if (_optimizeMode && !hasTypeCount)
        {
            lock (_lock)
            {
                return _friendEvents.Count;
            }
        }

        try
        {
            using var cmd = _db.CreateCommand();
            var typeClause = hasTypeCount ? "WHERE type = $type" : "";
            cmd.CommandText = $"SELECT COUNT(*) FROM friend_events {typeClause}";
            if (hasTypeCount) cmd.Parameters.AddWithValue("$type", typeFilter);
            var count = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            return (_optimizeMode && count > _maxN) ? (long)_maxN : count;
        }
        catch { return 0; }
    }

    public long SearchEventsCount(string query, string typeFilter = "", string date = "")
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;
        var like = "%" + query.Replace("%", "\\%").Replace("_", "\\_") + "%";
        string utcStart = "", utcEnd = "";
        if (!string.IsNullOrEmpty(date) && DateTime.TryParse(date, out var ld))
        {
            ld = DateTime.SpecifyKind(ld, DateTimeKind.Local);
            utcStart = ld.ToUniversalTime().ToString("o");
            utcEnd   = ld.AddDays(1).ToUniversalTime().ToString("o");
        }
        try
        {
            using var cmd = _db.CreateCommand();
            var typeClause = string.IsNullOrEmpty(typeFilter) ? "" : "AND e.type = $type";
            var dateClause = string.IsNullOrEmpty(utcStart)   ? "" : "AND e.timestamp >= $ds AND e.timestamp < $de";
            cmd.CommandText = $@"
                SELECT COUNT(DISTINCT e.id)
                FROM events e
                LEFT JOIN event_players ep ON e.id = ep.event_id
                WHERE 1=1
                  {typeClause}
                  {dateClause}
                  AND (
                    e.user_name        LIKE $q ESCAPE '\'
                    OR e.world_name    LIKE $q ESCAPE '\'
                    OR e.sender_name   LIKE $q ESCAPE '\'
                    OR e.message       LIKE $q ESCAPE '\'
                    OR ep.display_name LIKE $q ESCAPE '\'
                  )";
            cmd.Parameters.AddWithValue("$q", like);
            if (!string.IsNullOrEmpty(typeFilter)) cmd.Parameters.AddWithValue("$type", typeFilter);
            if (!string.IsNullOrEmpty(utcStart))
            {
                cmd.Parameters.AddWithValue("$ds", utcStart);
                cmd.Parameters.AddWithValue("$de", utcEnd);
            }
            var count = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            return (_optimizeMode && count > _maxN) ? (long)_maxN : count;
        }
        catch { return 0; }
    }

    public long SearchFriendEventsCount(string query, string date = "", string typeFilter = "")
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;
        var like = "%" + query.Replace("%", "\\%").Replace("_", "\\_") + "%";
        string utcStart = "", utcEnd = "";
        if (!string.IsNullOrEmpty(date) && DateTime.TryParse(date, out var ld))
        {
            ld = DateTime.SpecifyKind(ld, DateTimeKind.Local);
            utcStart = ld.ToUniversalTime().ToString("o");
            utcEnd   = ld.AddDays(1).ToUniversalTime().ToString("o");
        }
        try
        {
            using var cmd = _db.CreateCommand();
            var dateClause = string.IsNullOrEmpty(utcStart) ? "" : "AND timestamp >= $ds AND timestamp < $de";
            var typeClause = string.IsNullOrEmpty(typeFilter) ? "" : "AND type = $type";
            cmd.CommandText = $@"
                SELECT COUNT(*)
                FROM friend_events
                WHERE 1=1
                  {dateClause}
                  {typeClause}
                  AND (
                    friend_name LIKE $q ESCAPE '\'
                    OR world_name LIKE $q ESCAPE '\'
                    OR location   LIKE $q ESCAPE '\'
                    OR old_value  LIKE $q ESCAPE '\'
                    OR new_value  LIKE $q ESCAPE '\'
                  )";
            cmd.Parameters.AddWithValue("$q", like);
            if (!string.IsNullOrEmpty(typeFilter)) cmd.Parameters.AddWithValue("$type", typeFilter);
            if (!string.IsNullOrEmpty(utcStart))
            {
                cmd.Parameters.AddWithValue("$ds", utcStart);
                cmd.Parameters.AddWithValue("$de", utcEnd);
            }
            var count = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            return (_optimizeMode && count > _maxN) ? (long)_maxN : count;
        }
        catch { return 0; }
    }

    /// Returns the most recent personal timeline events that involve a specific userId
    /// (appears in event_players, or as the met user, or as notification sender).
    public List<TimelineEvent> GetEventsForUser(string userId, int limit = 10)
    {
        var ids = new List<string>();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"
                SELECT DISTINCT e.id FROM events e
                LEFT JOIN event_players ep ON e.id = ep.event_id
                WHERE ep.user_id = $uid OR e.user_id = $uid OR e.sender_id = $uid
                ORDER BY e.timestamp DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$uid",   userId);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetString(0));
        }
        catch { return new List<TimelineEvent>(); }

        if (ids.Count == 0) return new List<TimelineEvent>();

        var playerMap = new Dictionary<string, List<PlayerSnap>>();
        try
        {
            var inP = string.Join(",", ids.Select((_, i) => $"$p{i}"));
            using var pcmd = _db.CreateCommand();
            pcmd.CommandText = $"SELECT event_id,user_id,display_name,image,joined_at,left_at FROM event_players WHERE event_id IN ({inP})";
            for (int i = 0; i < ids.Count; i++) pcmd.Parameters.AddWithValue($"$p{i}", ids[i]);
            using var pr = pcmd.ExecuteReader();
            while (pr.Read())
            {
                var eid = pr.GetString(0);
                if (!playerMap.TryGetValue(eid, out var list)) playerMap[eid] = list = new();
                list.Add(new PlayerSnap {
                    UserId      = pr.GetString(1),
                    DisplayName = pr.GetString(2),
                    Image       = pr.GetString(3),
                    JoinedAts   = PlayerSnap.ParseSessions(pr.IsDBNull(4) ? "" : pr.GetString(4)),
                    LeftAts     = PlayerSnap.ParseSessions(pr.IsDBNull(5) ? "" : pr.GetString(5)),
                });
            }
        }
        catch { }

        var result = new List<TimelineEvent>();
        try
        {
            var inE = string.Join(",", ids.Select((_, i) => $"$e{i}"));
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $@"SELECT id,type,timestamp,world_id,world_name,world_thumb,
                location,photo_path,photo_url,user_id,user_name,user_image,
                notif_id,notif_type,notif_title,sender_name,sender_id,sender_image,message,
                left_at,tracked
                FROM events WHERE id IN ({inE}) ORDER BY timestamp DESC";
            for (int i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue($"$e{i}", ids[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetString(0);
                result.Add(new TimelineEvent
                {
                    Id          = id,
                    Type        = r.GetString(1),
                    Timestamp   = r.GetString(2),
                    WorldId     = r.GetString(3),
                    WorldName   = r.GetString(4),
                    WorldThumb  = r.GetString(5),
                    Location    = r.GetString(6),
                    PhotoPath   = r.GetString(7),
                    PhotoUrl    = r.GetString(8),
                    UserId      = r.GetString(9),
                    UserName    = r.GetString(10),
                    UserImage   = r.GetString(11),
                    NotifId     = r.GetString(12),
                    NotifType   = r.GetString(13),
                    NotifTitle  = r.GetString(14),
                    SenderName  = r.GetString(15),
                    SenderId    = r.GetString(16),
                    SenderImage = r.GetString(17),
                    Message     = r.GetString(18),
                    LeftAt      = r.IsDBNull(19) ? "" : r.GetString(19),
                    Tracked     = r.GetInt32(20),
                    Players     = playerMap.TryGetValue(id, out var pl) ? pl : new(),
                });
            }
        }
        catch { }
        return result;
    }

    private static readonly HashSet<string> _eventSortCols =
        new(StringComparer.OrdinalIgnoreCase) { "timestamp", "type", "user_name", "message", "world_name" };

    private static string EventOrderBy(string? sortBy, string? sortDir)
    {
        var col = !string.IsNullOrEmpty(sortBy) && _eventSortCols.Contains(sortBy) ? sortBy : "timestamp";
        var dir = string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
        return col == "timestamp"
            ? $"ORDER BY timestamp {dir}"
            : $"ORDER BY {col} {dir}, timestamp DESC";
    }

    public (List<TimelineEvent> Events, bool HasMore) GetEventsPaged(
        int limit, int offset, string typeFilter = "", string? sortBy = null, string? sortDir = null)
    {
        var defaultSort = string.IsNullOrEmpty(sortBy)
            || (string.Equals(sortBy, "timestamp", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase));

        if (_optimizeMode && string.IsNullOrEmpty(typeFilter) && defaultSort)
        {
            lock (_lock)
            {
                var filtered = _events.ToList();
                return (filtered.Skip(offset).Take(limit).ToList(), offset + limit < filtered.Count);
            }
        }

        var ids = new List<string>();
        try
        {
            using var cmd = _db.CreateCommand();
            var typeClause = string.IsNullOrEmpty(typeFilter) ? "" : "WHERE type = $type";
            cmd.CommandText = $"SELECT id FROM events {typeClause} {EventOrderBy(sortBy, sortDir)} LIMIT $limit OFFSET $offset";
            cmd.Parameters.AddWithValue("$limit",  limit + 1);
            cmd.Parameters.AddWithValue("$offset", offset);
            if (!string.IsNullOrEmpty(typeFilter)) cmd.Parameters.AddWithValue("$type", typeFilter);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetString(0));
        }
        catch { return (new List<TimelineEvent>(), false); }

        if (_optimizeMode)
        {
            int remaining = _maxN - offset;
            if (remaining <= 0) return (new List<TimelineEvent>(), false);
            if (ids.Count > remaining) ids = ids.Take(remaining).ToList();
        }

        var hasMore = ids.Count > limit;
        if (hasMore) ids.RemoveAt(ids.Count - 1);
        if (ids.Count == 0) return (new List<TimelineEvent>(), hasMore);

        var playerMap = new Dictionary<string, List<PlayerSnap>>();
        try
        {
            var inP = string.Join(",", ids.Select((_, i) => $"$p{i}"));
            using var pcmd = _db.CreateCommand();
            pcmd.CommandText = $"SELECT event_id,user_id,display_name,image,joined_at,left_at FROM event_players WHERE event_id IN ({inP})";
            for (int i = 0; i < ids.Count; i++) pcmd.Parameters.AddWithValue($"$p{i}", ids[i]);
            using var pr = pcmd.ExecuteReader();
            while (pr.Read())
            {
                var eid = pr.GetString(0);
                if (!playerMap.TryGetValue(eid, out var list)) playerMap[eid] = list = new();
                list.Add(new PlayerSnap {
                    UserId      = pr.GetString(1),
                    DisplayName = pr.GetString(2),
                    Image       = pr.GetString(3),
                    JoinedAts   = PlayerSnap.ParseSessions(pr.IsDBNull(4) ? "" : pr.GetString(4)),
                    LeftAts     = PlayerSnap.ParseSessions(pr.IsDBNull(5) ? "" : pr.GetString(5)),
                });
            }
        }
        catch { }

        var result = new List<TimelineEvent>();
        try
        {
            var inE = string.Join(",", ids.Select((_, i) => $"$e{i}"));
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $@"SELECT id,type,timestamp,world_id,world_name,world_thumb,
                location,photo_path,photo_url,user_id,user_name,user_image,
                notif_id,notif_type,notif_title,sender_name,sender_id,sender_image,message,
                left_at,tracked
                FROM events WHERE id IN ({inE}) ORDER BY timestamp DESC";
            for (int i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue($"$e{i}", ids[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetString(0);
                result.Add(new TimelineEvent
                {
                    Id          = id,
                    Type        = r.GetString(1),
                    Timestamp   = r.GetString(2),
                    WorldId     = r.GetString(3),
                    WorldName   = r.GetString(4),
                    WorldThumb  = r.GetString(5),
                    Location    = r.GetString(6),
                    PhotoPath   = r.GetString(7),
                    PhotoUrl    = r.GetString(8),
                    UserId      = r.GetString(9),
                    UserName    = r.GetString(10),
                    UserImage   = r.GetString(11),
                    NotifId     = r.GetString(12),
                    NotifType   = r.GetString(13),
                    NotifTitle  = r.GetString(14),
                    SenderName  = r.GetString(15),
                    SenderId    = r.GetString(16),
                    SenderImage = r.GetString(17),
                    Message     = r.GetString(18),
                    LeftAt      = r.IsDBNull(19) ? "" : r.GetString(19),
                    Tracked     = r.GetInt32(20),
                    Players     = playerMap.TryGetValue(id, out var pl) ? pl : new(),
                });
            }
        }
        catch { }

        var rank = new Dictionary<string, int>(ids.Count);
        for (int i = 0; i < ids.Count; i++) rank[ids[i]] = i;
        result = result.OrderBy(e => rank.TryGetValue(e.Id, out var i) ? i : int.MaxValue).ToList();

        return (result, hasMore);
    }

    public (List<TimelineEvent> Events, bool HasMore) SearchEvents(string query, string typeFilter = "", string date = "", int offset = 0, int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(query)) return (new List<TimelineEvent>(), false);
        var like = "%" + query.Replace("%", "\\%").Replace("_", "\\_") + "%";

        string utcStart = "", utcEnd = "";
        if (!string.IsNullOrEmpty(date) && DateTime.TryParse(date, out var localDate))
        {
            localDate  = DateTime.SpecifyKind(localDate, DateTimeKind.Local);
            utcStart   = localDate.ToUniversalTime().ToString("o");
            utcEnd     = localDate.AddDays(1).ToUniversalTime().ToString("o");
        }

        var ids = new List<string>();
        try
        {
            using var cmd = _db.CreateCommand();
            var typeClause = string.IsNullOrEmpty(typeFilter) ? "" : "AND e.type = $type";
            var dateClause = string.IsNullOrEmpty(utcStart)   ? "" : "AND e.timestamp >= $ds AND e.timestamp < $de";
            cmd.CommandText = $@"
                SELECT DISTINCT e.id
                FROM events e
                LEFT JOIN event_players ep ON e.id = ep.event_id
                WHERE 1=1
                  {typeClause}
                  {dateClause}
                  AND (
                    e.user_name        LIKE $q ESCAPE '\'
                    OR e.world_name    LIKE $q ESCAPE '\'
                    OR e.sender_name   LIKE $q ESCAPE '\'
                    OR e.message       LIKE $q ESCAPE '\'
                    OR ep.display_name LIKE $q ESCAPE '\'
                  )
                ORDER BY e.timestamp DESC
                LIMIT $limit OFFSET $offset";
            cmd.Parameters.AddWithValue("$q",      like);
            cmd.Parameters.AddWithValue("$limit",  limit + 1);
            cmd.Parameters.AddWithValue("$offset", offset);
            if (!string.IsNullOrEmpty(typeFilter))
                cmd.Parameters.AddWithValue("$type", typeFilter);
            if (!string.IsNullOrEmpty(utcStart))
            {
                cmd.Parameters.AddWithValue("$ds", utcStart);
                cmd.Parameters.AddWithValue("$de", utcEnd);
            }
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetString(0));
        }
        catch { return (new List<TimelineEvent>(), false); }

        if (_optimizeMode)
        {
            int remaining = _maxN - offset;
            if (remaining <= 0) return (new List<TimelineEvent>(), false);
            if (ids.Count > remaining) ids = ids.Take(remaining).ToList();
        }

        var hasMore = ids.Count > limit;
        if (hasMore) ids.RemoveAt(ids.Count - 1);
        if (ids.Count == 0) return (new List<TimelineEvent>(), hasMore);

        var playerMap = new Dictionary<string, List<PlayerSnap>>();
        try
        {
            var inP = string.Join(",", ids.Select((_, i) => $"$p{i}"));
            using var pcmd = _db.CreateCommand();
            pcmd.CommandText = $"SELECT event_id,user_id,display_name,image,joined_at,left_at FROM event_players WHERE event_id IN ({inP})";
            for (int i = 0; i < ids.Count; i++) pcmd.Parameters.AddWithValue($"$p{i}", ids[i]);
            using var pr = pcmd.ExecuteReader();
            while (pr.Read())
            {
                var eid = pr.GetString(0);
                if (!playerMap.TryGetValue(eid, out var list)) playerMap[eid] = list = new();
                list.Add(new PlayerSnap {
                    UserId      = pr.GetString(1),
                    DisplayName = pr.GetString(2),
                    Image       = pr.GetString(3),
                    JoinedAts   = PlayerSnap.ParseSessions(pr.IsDBNull(4) ? "" : pr.GetString(4)),
                    LeftAts     = PlayerSnap.ParseSessions(pr.IsDBNull(5) ? "" : pr.GetString(5)),
                });
            }
        }
        catch { }

        var result = new List<TimelineEvent>();
        try
        {
            var inE = string.Join(",", ids.Select((_, i) => $"$e{i}"));
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $@"SELECT id,type,timestamp,world_id,world_name,world_thumb,
                location,photo_path,photo_url,user_id,user_name,user_image,
                notif_id,notif_type,notif_title,sender_name,sender_id,sender_image,message,
                left_at,tracked
                FROM events WHERE id IN ({inE}) ORDER BY timestamp DESC";
            for (int i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue($"$e{i}", ids[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetString(0);
                result.Add(new TimelineEvent
                {
                    Id          = id,
                    Type        = r.GetString(1),
                    Timestamp   = r.GetString(2),
                    WorldId     = r.GetString(3),
                    WorldName   = r.GetString(4),
                    WorldThumb  = r.GetString(5),
                    Location    = r.GetString(6),
                    PhotoPath   = r.GetString(7),
                    PhotoUrl    = r.GetString(8),
                    UserId      = r.GetString(9),
                    UserName    = r.GetString(10),
                    UserImage   = r.GetString(11),
                    NotifId     = r.GetString(12),
                    NotifType   = r.GetString(13),
                    NotifTitle  = r.GetString(14),
                    SenderName  = r.GetString(15),
                    SenderId    = r.GetString(16),
                    SenderImage = r.GetString(17),
                    Message     = r.GetString(18),
                    LeftAt      = r.IsDBNull(19) ? "" : r.GetString(19),
                    Tracked     = r.GetInt32(20),
                    Players     = playerMap.TryGetValue(id, out var pl) ? pl : new(),
                });
            }
        }
        catch { }
        return (result, hasMore);
    }

    public List<TimelineEvent> GetEventsByDate(DateTime localDate)
    {
        var utcStart = localDate.ToUniversalTime().ToString("o");
        var utcEnd   = localDate.AddDays(1).ToUniversalTime().ToString("o");

        var ids = new List<string>();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id FROM events WHERE timestamp >= $s AND timestamp < $e ORDER BY timestamp DESC";
            cmd.Parameters.AddWithValue("$s", utcStart);
            cmd.Parameters.AddWithValue("$e", utcEnd);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetString(0));
        }
        catch { return new List<TimelineEvent>(); }

        if (ids.Count == 0) return new List<TimelineEvent>();

        var playerMap = new Dictionary<string, List<PlayerSnap>>();
        try
        {
            var inP = string.Join(",", ids.Select((_, i) => $"$p{i}"));
            using var pcmd = _db.CreateCommand();
            pcmd.CommandText = $"SELECT event_id,user_id,display_name,image,joined_at,left_at FROM event_players WHERE event_id IN ({inP})";
            for (int i = 0; i < ids.Count; i++) pcmd.Parameters.AddWithValue($"$p{i}", ids[i]);
            using var pr = pcmd.ExecuteReader();
            while (pr.Read())
            {
                var eid = pr.GetString(0);
                if (!playerMap.TryGetValue(eid, out var list)) playerMap[eid] = list = new();
                list.Add(new PlayerSnap {
                    UserId      = pr.GetString(1),
                    DisplayName = pr.GetString(2),
                    Image       = pr.GetString(3),
                    JoinedAts   = PlayerSnap.ParseSessions(pr.IsDBNull(4) ? "" : pr.GetString(4)),
                    LeftAts     = PlayerSnap.ParseSessions(pr.IsDBNull(5) ? "" : pr.GetString(5)),
                });
            }
        }
        catch { }

        var result = new List<TimelineEvent>();
        try
        {
            var inE = string.Join(",", ids.Select((_, i) => $"$e{i}"));
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $@"SELECT id,type,timestamp,world_id,world_name,world_thumb,
                location,photo_path,photo_url,user_id,user_name,user_image,
                notif_id,notif_type,notif_title,sender_name,sender_id,sender_image,message,
                left_at,tracked
                FROM events WHERE id IN ({inE}) ORDER BY timestamp DESC";
            for (int i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue($"$e{i}", ids[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetString(0);
                result.Add(new TimelineEvent
                {
                    Id          = id,
                    Type        = r.GetString(1),
                    Timestamp   = r.GetString(2),
                    WorldId     = r.GetString(3),
                    WorldName   = r.GetString(4),
                    WorldThumb  = r.GetString(5),
                    Location    = r.GetString(6),
                    PhotoPath   = r.GetString(7),
                    PhotoUrl    = r.GetString(8),
                    UserId      = r.GetString(9),
                    UserName    = r.GetString(10),
                    UserImage   = r.GetString(11),
                    NotifId     = r.GetString(12),
                    NotifType   = r.GetString(13),
                    NotifTitle  = r.GetString(14),
                    SenderName  = r.GetString(15),
                    SenderId    = r.GetString(16),
                    SenderImage = r.GetString(17),
                    Message     = r.GetString(18),
                    LeftAt      = r.IsDBNull(19) ? "" : r.GetString(19),
                    Tracked     = r.GetInt32(20),
                    Players     = playerMap.TryGetValue(id, out var pl) ? pl : new(),
                });
            }
        }
        catch { }
        return result;
    }

    public List<TimelineEvent> GetInstanceVisitsForDay(DateTime localDate)
    {
        var utcStart = localDate.ToUniversalTime().ToString("o");
        var utcEnd   = localDate.AddDays(1).ToUniversalTime().ToString("o");

        var ids = new List<string>();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"SELECT id FROM events
                WHERE type='instance_join' AND timestamp < $e AND (left_at = '' OR left_at >= $s)
                ORDER BY timestamp ASC";
            cmd.Parameters.AddWithValue("$s", utcStart);
            cmd.Parameters.AddWithValue("$e", utcEnd);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetString(0));
        }
        catch { return new List<TimelineEvent>(); }

        if (ids.Count == 0) return new List<TimelineEvent>();

        var playerMap = new Dictionary<string, List<PlayerSnap>>();
        try
        {
            var inP = string.Join(",", ids.Select((_, i) => $"$p{i}"));
            using var pcmd = _db.CreateCommand();
            pcmd.CommandText = $"SELECT event_id,user_id,display_name,image,joined_at,left_at FROM event_players WHERE event_id IN ({inP})";
            for (int i = 0; i < ids.Count; i++) pcmd.Parameters.AddWithValue($"$p{i}", ids[i]);
            using var pr = pcmd.ExecuteReader();
            while (pr.Read())
            {
                var eid = pr.GetString(0);
                if (!playerMap.TryGetValue(eid, out var list)) playerMap[eid] = list = new();
                list.Add(new PlayerSnap {
                    UserId      = pr.GetString(1),
                    DisplayName = pr.GetString(2),
                    Image       = pr.GetString(3),
                    JoinedAts   = PlayerSnap.ParseSessions(pr.IsDBNull(4) ? "" : pr.GetString(4)),
                    LeftAts     = PlayerSnap.ParseSessions(pr.IsDBNull(5) ? "" : pr.GetString(5)),
                });
            }
        }
        catch { }

        var result = new List<TimelineEvent>();
        try
        {
            var inE = string.Join(",", ids.Select((_, i) => $"$e{i}"));
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $@"SELECT id,type,timestamp,world_id,world_name,world_thumb,
                location,photo_path,photo_url,user_id,user_name,user_image,
                notif_id,notif_type,notif_title,sender_name,sender_id,sender_image,message,
                left_at,tracked
                FROM events WHERE id IN ({inE}) ORDER BY timestamp ASC";
            for (int i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue($"$e{i}", ids[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetString(0);
                result.Add(new TimelineEvent
                {
                    Id          = id,
                    Type        = r.GetString(1),
                    Timestamp   = r.GetString(2),
                    WorldId     = r.GetString(3),
                    WorldName   = r.GetString(4),
                    WorldThumb  = r.GetString(5),
                    Location    = r.GetString(6),
                    PhotoPath   = r.GetString(7),
                    PhotoUrl    = r.GetString(8),
                    UserId      = r.GetString(9),
                    UserName    = r.GetString(10),
                    UserImage   = r.GetString(11),
                    NotifId     = r.GetString(12),
                    NotifType   = r.GetString(13),
                    NotifTitle  = r.GetString(14),
                    SenderName  = r.GetString(15),
                    SenderId    = r.GetString(16),
                    SenderImage = r.GetString(17),
                    Message     = r.GetString(18),
                    LeftAt      = r.IsDBNull(19) ? "" : r.GetString(19),
                    Tracked     = r.GetInt32(20),
                    Players     = playerMap.TryGetValue(id, out var pl) ? pl : new(),
                });
            }
        }
        catch { }
        return result;
    }

    // Distinct world IDs ordered by most recent visit (instance_join events).
    public Dictionary<string, string> GetLastSeenTogetherMap()
    {
        var map = new Dictionary<string, string>();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"SELECT user_id, MAX(ts) AS last_seen FROM (
                    SELECT ep.user_id AS user_id, j.value AS ts
                      FROM event_players ep, json_each(ep.left_at) j
                     WHERE ep.user_id <> '' AND ep.left_at LIKE '[%'
                    UNION ALL
                    SELECT ep.user_id, j.value
                      FROM event_players ep, json_each(ep.joined_at) j
                     WHERE ep.user_id <> '' AND ep.joined_at LIKE '[%'
                ) GROUP BY user_id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var uid = r.IsDBNull(0) ? "" : r.GetString(0);
                if (uid.Length == 0) continue;
                map[uid] = r.IsDBNull(1) ? "" : r.GetString(1);
            }
        }
        catch { }
        return map;
    }

    public List<string> GetRecentVisitedWorldIds(int limit = 32)
    {
        var ids = new List<string>();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"SELECT world_id, MAX(timestamp) AS ts FROM events
                WHERE type='instance_join' AND world_id LIKE 'wrld_%'
                GROUP BY world_id ORDER BY ts DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetString(0));
        }
        catch { }
        return ids;
    }

    // Distinct players ordered by most recently seen (from instance_join event_players).
    public List<JObject> GetRecentSeenPlayers(int limit = 64, string excludeUserId = "")
    {
        var result = new List<JObject>();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"SELECT ep.user_id, ep.display_name, ep.image, MAX(e.timestamp) AS ts
                FROM event_players ep JOIN events e ON e.id = ep.event_id
                WHERE e.type='instance_join' AND ep.user_id != '' AND ep.user_id != $self
                GROUP BY ep.user_id ORDER BY ts DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$self", excludeUserId ?? "");
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var uid = r.IsDBNull(0) ? "" : r.GetString(0);
                if (string.IsNullOrEmpty(uid)) continue;
                result.Add(new JObject {
                    ["id"]          = uid,
                    ["displayName"] = r.IsDBNull(1) ? "" : r.GetString(1),
                    ["image"]       = r.IsDBNull(2) ? "" : r.GetString(2),
                });
            }
        }
        catch { }
        return result;
    }

    // Distinct avatars ordered by most recently worn (avatar_switch events).
    public List<JObject> GetRecentUsedAvatars(int limit = 32)
    {
        var result = new List<JObject>();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"SELECT user_id, user_name, user_image, MAX(timestamp) AS ts
                FROM events WHERE type='avatar_switch' AND user_id != ''
                GROUP BY user_id ORDER BY ts DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var aid = r.IsDBNull(0) ? "" : r.GetString(0);
                if (string.IsNullOrEmpty(aid)) continue;
                result.Add(new JObject {
                    ["id"]                = aid,
                    ["name"]              = r.IsDBNull(1) ? "" : r.GetString(1),
                    ["thumbnailImageUrl"] = r.IsDBNull(2) ? "" : r.GetString(2),
                });
            }
        }
        catch { }
        return result;
    }

    public List<TimelineEvent> GetInstanceVisitsForWorld(string worldId, int limit = 10)
    {
        if (string.IsNullOrEmpty(worldId)) return new List<TimelineEvent>();

        var ids = new List<string>();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"SELECT id FROM events
                WHERE type='instance_join' AND world_id=$wid
                ORDER BY timestamp DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$wid",   worldId);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetString(0));
        }
        catch { return new List<TimelineEvent>(); }

        if (ids.Count == 0) return new List<TimelineEvent>();

        var playerMap = new Dictionary<string, List<PlayerSnap>>();
        try
        {
            var inP = string.Join(",", ids.Select((_, i) => $"$p{i}"));
            using var pcmd = _db.CreateCommand();
            pcmd.CommandText = $"SELECT event_id,user_id,display_name,image,joined_at,left_at FROM event_players WHERE event_id IN ({inP})";
            for (int i = 0; i < ids.Count; i++) pcmd.Parameters.AddWithValue($"$p{i}", ids[i]);
            using var pr = pcmd.ExecuteReader();
            while (pr.Read())
            {
                var eid = pr.GetString(0);
                if (!playerMap.TryGetValue(eid, out var list)) playerMap[eid] = list = new();
                list.Add(new PlayerSnap {
                    UserId      = pr.GetString(1),
                    DisplayName = pr.GetString(2),
                    Image       = pr.GetString(3),
                    JoinedAts   = PlayerSnap.ParseSessions(pr.IsDBNull(4) ? "" : pr.GetString(4)),
                    LeftAts     = PlayerSnap.ParseSessions(pr.IsDBNull(5) ? "" : pr.GetString(5)),
                });
            }
        }
        catch { }

        var result = new List<TimelineEvent>();
        try
        {
            var inE = string.Join(",", ids.Select((_, i) => $"$e{i}"));
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $@"SELECT id,type,timestamp,world_id,world_name,world_thumb,
                location,photo_path,photo_url,user_id,user_name,user_image,
                notif_id,notif_type,notif_title,sender_name,sender_id,sender_image,message,
                left_at,tracked
                FROM events WHERE id IN ({inE}) ORDER BY timestamp DESC";
            for (int i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue($"$e{i}", ids[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetString(0);
                result.Add(new TimelineEvent
                {
                    Id          = id,
                    Type        = r.GetString(1),
                    Timestamp   = r.GetString(2),
                    WorldId     = r.GetString(3),
                    WorldName   = r.GetString(4),
                    WorldThumb  = r.GetString(5),
                    Location    = r.GetString(6),
                    PhotoPath   = r.GetString(7),
                    PhotoUrl    = r.GetString(8),
                    UserId      = r.GetString(9),
                    UserName    = r.GetString(10),
                    UserImage   = r.GetString(11),
                    NotifId     = r.GetString(12),
                    NotifType   = r.GetString(13),
                    NotifTitle  = r.GetString(14),
                    SenderName  = r.GetString(15),
                    SenderId    = r.GetString(16),
                    SenderImage = r.GetString(17),
                    Message     = r.GetString(18),
                    LeftAt      = r.IsDBNull(19) ? "" : r.GetString(19),
                    Tracked     = r.GetInt32(20),
                    Players     = playerMap.TryGetValue(id, out var pl) ? pl : new(),
                });
            }
        }
        catch { }
        return result;
    }

    // Friend timeline

    public void AddFriendEvent(FriendTimelineEvent ev)
    {
        lock (_lock)
        {
            if (_optimizeMode)
            {
                _friendEvents.Insert(0, ev);
                if (_friendEvents.Count > _maxN) _friendEvents.RemoveAt(_friendEvents.Count - 1);
            }
            else
                _friendEvents.Add(ev);
            DbInsertFriendEvent(ev);
        }
    }

    public List<FriendTimelineEvent> GetFriendEvents()
    {
        lock (_lock)
            return _friendEvents.OrderByDescending(e => e.Timestamp).ToList();
    }

    public bool DeleteFriendEvent(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        lock (_lock)
        {
            int n;
            try
            {
                using var tx = _db.BeginTransaction();
                using (var c = _db.CreateCommand())
                {
                    c.Transaction = tx;
                    c.CommandText = "DELETE FROM friend_event_colocated WHERE event_id=$id";
                    c.Parameters.AddWithValue("$id", id);
                    c.ExecuteNonQuery();
                }
                using (var c = _db.CreateCommand())
                {
                    c.Transaction = tx;
                    c.CommandText = "DELETE FROM friend_events WHERE id=$id";
                    c.Parameters.AddWithValue("$id", id);
                    n = c.ExecuteNonQuery();
                }
                tx.Commit();
            }
            catch { return false; }
            _friendEvents.RemoveAll(e => e.Id == id);
            return n > 0;
        }
    }

    public int DeleteFriendEvents(IEnumerable<string> ids)
    {
        var list = ids?.Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList() ?? new();
        if (list.Count == 0) return 0;
        int total = 0;
        lock (_lock)
        {
            try
            {
                using var tx = _db.BeginTransaction();
                using var delC = _db.CreateCommand();
                using var delE = _db.CreateCommand();
                delC.Transaction = tx; delE.Transaction = tx;
                delC.CommandText = "DELETE FROM friend_event_colocated WHERE event_id=$id";
                delE.CommandText = "DELETE FROM friend_events WHERE id=$id";
                var pC = delC.Parameters.Add("$id", SqliteType.Text);
                var pE = delE.Parameters.Add("$id", SqliteType.Text);
                foreach (var id in list)
                {
                    pC.Value = id; delC.ExecuteNonQuery();
                    pE.Value = id; total += delE.ExecuteNonQuery();
                }
                tx.Commit();
            }
            catch { return 0; }
            var set = new HashSet<string>(list);
            _friendEvents.RemoveAll(e => set.Contains(e.Id));
        }
        return total;
    }

    public int DeleteFriendEventsByType(string type, int limit)
    {
        var hasType = !string.IsNullOrEmpty(type) && type != "all";
        lock (_lock)
        {
            var ids = new List<string>();
            try
            {
                using (var sel = _db.CreateCommand())
                {
                    var typeClause = hasType ? "WHERE type=$type" : "";
                    sel.CommandText = limit > 0
                        ? $"SELECT id FROM friend_events {typeClause} ORDER BY timestamp DESC LIMIT $n"
                        : $"SELECT id FROM friend_events {typeClause}";
                    if (hasType) sel.Parameters.AddWithValue("$type", type);
                    if (limit > 0) sel.Parameters.AddWithValue("$n", limit);
                    using var r = sel.ExecuteReader();
                    while (r.Read()) ids.Add(r.GetString(0));
                }
                if (ids.Count == 0) return 0;

                using var tx = _db.BeginTransaction();
                using var delC = _db.CreateCommand();
                using var delE = _db.CreateCommand();
                delC.Transaction = tx; delE.Transaction = tx;
                delC.CommandText = "DELETE FROM friend_event_colocated WHERE event_id=$id";
                delE.CommandText = "DELETE FROM friend_events WHERE id=$id";
                var pC = delC.Parameters.Add("$id", SqliteType.Text);
                var pE = delE.Parameters.Add("$id", SqliteType.Text);
                foreach (var id in ids)
                {
                    pC.Value = id; delC.ExecuteNonQuery();
                    pE.Value = id; delE.ExecuteNonQuery();
                }
                tx.Commit();
            }
            catch { return 0; }
            var set = new HashSet<string>(ids);
            _friendEvents.RemoveAll(e => set.Contains(e.Id));
            return ids.Count;
        }
    }

    private static readonly HashSet<string> _friendSortCols =
        new(StringComparer.OrdinalIgnoreCase) { "timestamp", "type", "friend_name", "world_name" };

    private static string FriendOrderBy(string? sortBy, string? sortDir)
    {
        var col = !string.IsNullOrEmpty(sortBy) && _friendSortCols.Contains(sortBy) ? sortBy : "timestamp";
        var dir = string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
        return col == "timestamp"
            ? $"ORDER BY timestamp {dir}"
            : $"ORDER BY {col} {dir}, timestamp DESC";
    }

    public (List<FriendTimelineEvent> Events, bool HasMore) GetFriendEventsPaged(
        int limit, int offset, string? type = null, string? sortBy = null, string? sortDir = null)
    {
        var hasType = !string.IsNullOrEmpty(type) && type != "all";
        var defaultSort = string.IsNullOrEmpty(sortBy)
            || (string.Equals(sortBy, "timestamp", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase));

        if (_optimizeMode && !hasType && defaultSort)
        {
            lock (_lock)
            {
                var filtered = _friendEvents.ToList();
                return (filtered.Skip(offset).Take(limit).ToList(), offset + limit < filtered.Count);
            }
        }

        var result = new List<FriendTimelineEvent>();
        try
        {
            var orderBy = FriendOrderBy(sortBy, sortDir);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = hasType
                ? $@"SELECT id,type,timestamp,friend_id,friend_name,friend_image,
                       world_id,world_name,world_thumb,location,old_value,new_value,left_at,tracked
                       FROM friend_events WHERE type=$type
                       {orderBy} LIMIT $limit OFFSET $offset"
                : $@"SELECT id,type,timestamp,friend_id,friend_name,friend_image,
                       world_id,world_name,world_thumb,location,old_value,new_value,left_at,tracked
                       FROM friend_events
                       {orderBy} LIMIT $limit OFFSET $offset";
            cmd.Parameters.AddWithValue("$limit",  limit + 1);
            cmd.Parameters.AddWithValue("$offset", offset);
            if (hasType) cmd.Parameters.AddWithValue("$type", type);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(new FriendTimelineEvent
                {
                    Id          = r.GetString(0),
                    Type        = r.GetString(1),
                    Timestamp   = r.GetString(2),
                    FriendId    = r.GetString(3),
                    FriendName  = r.GetString(4),
                    FriendImage = r.GetString(5),
                    WorldId     = r.GetString(6),
                    WorldName   = r.GetString(7),
                    WorldThumb  = r.GetString(8),
                    Location    = r.GetString(9),
                    OldValue    = r.GetString(10),
                    NewValue    = r.GetString(11),
                    LeftAt      = r.IsDBNull(12) ? "" : r.GetString(12),
                    Tracked     = r.GetInt32(13),
                });
        }
        catch { }
        if (_optimizeMode)
        {
            int remaining = _maxN - offset;
            if (remaining <= 0) return (new List<FriendTimelineEvent>(), false);
            if (result.Count > remaining) result = result.Take(remaining).ToList();
        }
        var hasMore = result.Count > limit;
        if (hasMore) result.RemoveAt(result.Count - 1);
        return (result, hasMore);
    }

    public string GetUserMemo(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return "";
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT memo FROM user_memos WHERE user_id=$id";
            cmd.Parameters.AddWithValue("$id", userId);
            return cmd.ExecuteScalar() as string ?? "";
        }
        catch { return ""; }
    }

    public void SetUserMemo(string userId, string memo)
    {
        if (string.IsNullOrEmpty(userId)) return;
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"INSERT INTO user_memos(user_id, memo, updated_at) VALUES($id, $m, $ts)
                ON CONFLICT(user_id) DO UPDATE SET memo=$m, updated_at=$ts";
            cmd.Parameters.AddWithValue("$id", userId);
            cmd.Parameters.AddWithValue("$m", memo ?? "");
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    public string GetLastKnownFriendName(string friendId)
    {
        if (string.IsNullOrEmpty(friendId)) return "";
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"SELECT friend_name FROM friend_events
                WHERE friend_id = $fid AND friend_name <> ''
                ORDER BY timestamp DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$fid", friendId);
            return cmd.ExecuteScalar() as string ?? "";
        }
        catch { return ""; }
    }

    public List<FriendTimelineEvent> GetFriendEventsForUser(string friendId, int limit = 10)
    {
        var result = new List<FriendTimelineEvent>();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"SELECT id,type,timestamp,friend_id,friend_name,friend_image,
                world_id,world_name,world_thumb,location,old_value,new_value
                FROM friend_events WHERE friend_id=$fid
                ORDER BY timestamp DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$fid",   friendId);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(new FriendTimelineEvent
                {
                    Id          = r.GetString(0),
                    Type        = r.GetString(1),
                    Timestamp   = r.GetString(2),
                    FriendId    = r.GetString(3),
                    FriendName  = r.GetString(4),
                    FriendImage = r.GetString(5),
                    WorldId     = r.GetString(6),
                    WorldName   = r.GetString(7),
                    WorldThumb  = r.GetString(8),
                    Location    = r.GetString(9),
                    OldValue    = r.GetString(10),
                    NewValue    = r.GetString(11),
                });
        }
        catch { }
        return result;
    }

    public List<FriendTimelineEvent> GetFriendEventsByDate(DateTime localDate, string? type = null)
    {
        var utcStart = localDate.ToUniversalTime().ToString("o");
        var utcEnd   = localDate.AddDays(1).ToUniversalTime().ToString("o");
        var result   = new List<FriendTimelineEvent>();
        try
        {
            using var cmd = _db.CreateCommand();
            var hasType = !string.IsNullOrEmpty(type) && type != "all";
            cmd.CommandText = hasType
                ? @"SELECT id,type,timestamp,friend_id,friend_name,friend_image,
                       world_id,world_name,world_thumb,location,old_value,new_value,left_at,tracked
                       FROM friend_events WHERE type=$type AND timestamp >= $s AND timestamp < $e
                       ORDER BY timestamp DESC"
                : @"SELECT id,type,timestamp,friend_id,friend_name,friend_image,
                       world_id,world_name,world_thumb,location,old_value,new_value,left_at,tracked
                       FROM friend_events WHERE timestamp >= $s AND timestamp < $e
                       ORDER BY timestamp DESC";
            cmd.Parameters.AddWithValue("$s", utcStart);
            cmd.Parameters.AddWithValue("$e", utcEnd);
            if (hasType) cmd.Parameters.AddWithValue("$type", type);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(new FriendTimelineEvent
                {
                    Id          = r.GetString(0),
                    Type        = r.GetString(1),
                    Timestamp   = r.GetString(2),
                    FriendId    = r.GetString(3),
                    FriendName  = r.GetString(4),
                    FriendImage = r.GetString(5),
                    WorldId     = r.GetString(6),
                    WorldName   = r.GetString(7),
                    WorldThumb  = r.GetString(8),
                    Location    = r.GetString(9),
                    OldValue    = r.GetString(10),
                    NewValue    = r.GetString(11),
                    LeftAt      = r.IsDBNull(12) ? "" : r.GetString(12),
                    Tracked     = r.GetInt32(13),
                });
        }
        catch { }
        return result;
    }

    public (List<FriendTimelineEvent> Events, bool HasMore) SearchFriendEvents(string query, string date = "", int offset = 0, string typeFilter = "", int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(query)) return (new List<FriendTimelineEvent>(), false);
        var like = "%" + query.Replace("%", "\\%").Replace("_", "\\_") + "%";

        string utcStart = "", utcEnd = "";
        if (!string.IsNullOrEmpty(date) && DateTime.TryParse(date, out var localDate))
        {
            localDate = DateTime.SpecifyKind(localDate, DateTimeKind.Local);
            utcStart  = localDate.ToUniversalTime().ToString("o");
            utcEnd    = localDate.AddDays(1).ToUniversalTime().ToString("o");
        }

        var result = new List<FriendTimelineEvent>();
        try
        {
            using var cmd = _db.CreateCommand();
            var dateClause = string.IsNullOrEmpty(utcStart) ? "" : "AND timestamp >= $ds AND timestamp < $de";
            var typeClause = string.IsNullOrEmpty(typeFilter) ? "" : "AND type = $type";
            cmd.CommandText = $@"
                SELECT id,type,timestamp,friend_id,friend_name,friend_image,
                       world_id,world_name,world_thumb,location,old_value,new_value,left_at,tracked
                FROM friend_events
                WHERE 1=1
                  {dateClause}
                  {typeClause}
                  AND (
                    friend_name LIKE $q ESCAPE '\'
                    OR world_name LIKE $q ESCAPE '\'
                    OR location   LIKE $q ESCAPE '\'
                    OR old_value  LIKE $q ESCAPE '\'
                    OR new_value  LIKE $q ESCAPE '\'
                  )
                ORDER BY timestamp DESC
                LIMIT $limit OFFSET $offset";
            cmd.Parameters.AddWithValue("$q",      like);
            cmd.Parameters.AddWithValue("$limit",  limit + 1);
            cmd.Parameters.AddWithValue("$offset", offset);
            if (!string.IsNullOrEmpty(typeFilter)) cmd.Parameters.AddWithValue("$type", typeFilter);
            if (!string.IsNullOrEmpty(utcStart))
            {
                cmd.Parameters.AddWithValue("$ds", utcStart);
                cmd.Parameters.AddWithValue("$de", utcEnd);
            }
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(new FriendTimelineEvent
                {
                    Id          = r.GetString(0),
                    Type        = r.GetString(1),
                    Timestamp   = r.GetString(2),
                    FriendId    = r.GetString(3),
                    FriendName  = r.GetString(4),
                    FriendImage = r.GetString(5),
                    WorldId     = r.GetString(6),
                    WorldName   = r.GetString(7),
                    WorldThumb  = r.GetString(8),
                    Location    = r.GetString(9),
                    OldValue    = r.GetString(10),
                    NewValue    = r.GetString(11),
                    LeftAt      = r.IsDBNull(12) ? "" : r.GetString(12),
                    Tracked     = r.GetInt32(13),
                });
        }
        catch { }
        if (_optimizeMode)
        {
            int remaining = _maxN - offset;
            if (remaining <= 0) return (new List<FriendTimelineEvent>(), false);
            if (result.Count > remaining) result.RemoveRange(remaining, result.Count - remaining);
        }
        var hasMore = result.Count > limit;
        if (hasMore) result.RemoveAt(result.Count - 1);
        return (result, hasMore);
    }

    public void UpdateFriendEventImage(string id, string friendImage)
    {
        FriendTimelineEvent? ev;
        lock (_lock) ev = _friendEvents.FirstOrDefault(e => e.Id == id);
        if (ev == null) return;
        lock (_lock) ev.FriendImage = friendImage;
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE friend_events SET friend_image=$fi WHERE id=$id";
            cmd.Parameters.AddWithValue("$fi",  friendImage);
            cmd.Parameters.AddWithValue("$id",  id);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    public void SetFriendEventLeftAt(string id, string leftAt)
    {
        lock (_lock)
        {
            var ev = _friendEvents.FirstOrDefault(e => e.Id == id);
            if (ev != null) ev.LeftAt = leftAt;
        }
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE friend_events SET left_at=$la WHERE id=$id";
            cmd.Parameters.AddWithValue("$la", leftAt);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    public void SetInstanceEventLeftAt(string id, string leftAt)
    {
        lock (_lock)
        {
            var ev = _events.FirstOrDefault(e => e.Id == id);
            if (ev != null) ev.LeftAt = leftAt;
        }
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE events SET left_at=$la WHERE id=$id";
            cmd.Parameters.AddWithValue("$la", leftAt);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    public void AddFriendEventColocated(string eventId, string friendId, string friendName, string friendImage)
    {
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"
                INSERT OR IGNORE INTO friend_event_colocated (event_id, friend_id, friend_name, friend_image)
                VALUES ($eid, $fid, $fn, $fi)";
            cmd.Parameters.AddWithValue("$eid", eventId);
            cmd.Parameters.AddWithValue("$fid", friendId);
            cmd.Parameters.AddWithValue("$fn",  friendName);
            cmd.Parameters.AddWithValue("$fi",  friendImage);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    public List<TimelineEvent> GetOpenInstanceEvents()
    {
        var result = new List<TimelineEvent>();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"SELECT id, location FROM events
                WHERE type='instance_join' AND tracked=1 AND (left_at IS NULL OR left_at='')";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(new TimelineEvent { Id = r.GetString(0), Location = r.IsDBNull(1) ? "" : r.GetString(1) });
        }
        catch { }
        return result;
    }

    public List<FriendTimelineEvent> GetOpenTrackedGpsEvents()
    {
        var result = new List<FriendTimelineEvent>();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"
                SELECT id, friend_id, location
                FROM friend_events
                WHERE type='friend_gps' AND tracked=1 AND left_at IS NULL";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(new FriendTimelineEvent
                {
                    Id       = r.GetString(0),
                    FriendId = r.GetString(1),
                    Location = r.GetString(2),
                });
        }
        catch { }
        return result;
    }

    public List<FriendTimelineEvent> GetFriendGpsColocated(string location, string excludeId)
    {
        var colon = location.IndexOf('~');
        var locBase = colon > 0 ? location[..colon] : location;
        if (string.IsNullOrEmpty(locBase)) return new();
        var result = new List<FriendTimelineEvent>();
        try
        {
            // tracked = 1 → new system, tracked = 0 → legacy
            bool isTracked = false;
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT tracked FROM friend_events WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", excludeId);
                using var r = cmd.ExecuteReader();
                if (r.Read()) isTracked = r.GetInt32(0) == 1;
            }

            if (isTracked)
            {
                // New system: read only from friend_event_colocated, never fall back
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"
                    SELECT friend_id, friend_name, friend_image
                    FROM friend_event_colocated
                    WHERE event_id = $eid";
                cmd.Parameters.AddWithValue("$eid", excludeId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    result.Add(new FriendTimelineEvent
                    {
                        FriendId    = r.GetString(0),
                        FriendName  = r.GetString(1),
                        FriendImage = r.GetString(2),
                    });
            }
            else
            {
                // Legacy: old query across all history
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"
                    SELECT friend_id, friend_name, friend_image
                    FROM friend_events
                    WHERE type='friend_gps' AND id != $excl
                      AND (location = $loc OR location LIKE $locPrefix)
                    ORDER BY timestamp DESC";
                cmd.Parameters.AddWithValue("$excl",      excludeId);
                cmd.Parameters.AddWithValue("$loc",       locBase);
                cmd.Parameters.AddWithValue("$locPrefix", locBase + "~%");
                var seen = new HashSet<string>();
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var friendId = r.GetString(0);
                    if (!seen.Add(friendId)) continue;
                    result.Add(new FriendTimelineEvent
                    {
                        FriendId    = friendId,
                        FriendName  = r.GetString(1),
                        FriendImage = r.GetString(2),
                    });
                    if (result.Count >= 50) break;
                }
            }
        }
        catch { }
        return result;
    }

    public void UpdateFriendEventWorld(string id, string worldName, string worldThumb)
    {
        FriendTimelineEvent? ev;
        lock (_lock) ev = _friendEvents.FirstOrDefault(e => e.Id == id);
        if (ev == null) return;
        lock (_lock)
        {
            ev.WorldName  = worldName;
            ev.WorldThumb = worldThumb;
        }
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE friend_events SET world_name=$wn, world_thumb=$wt WHERE id=$id";
            cmd.Parameters.AddWithValue("$wn",  worldName);
            cmd.Parameters.AddWithValue("$wt",  worldThumb);
            cmd.Parameters.AddWithValue("$id",  id);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    // Known users tracking

    public bool IsKnownUser(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return true;
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(1) FROM known_users WHERE user_id = $id";
            cmd.Parameters.AddWithValue("$id", userId);
            return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
        }
        catch { return false; }
    }

    public void SeedKnownUsers(IEnumerable<string> userIds)
    {
        var toAdd = userIds.Where(x => !string.IsNullOrEmpty(x)).ToList();
        try
        {
            using var tx  = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO known_users(user_id) VALUES($id)";
            var p = cmd.Parameters.Add("$id", SqliteType.Text);
            foreach (var id in toAdd) { p.Value = id; cmd.ExecuteNonQuery(); }
            tx.Commit();
            _knownUsersSeeded = true;
        }
        catch { }
    }

    public void AddKnownUser(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return;
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO known_users(user_id) VALUES($id)";
            cmd.Parameters.AddWithValue("$id", userId);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    // Notification deduplication

    public bool IsLoggedNotif(string notifId)
    {
        if (string.IsNullOrEmpty(notifId)) return true;
        lock (_lock) return _loggedNotifs.Contains(notifId);
    }

    public void AddLoggedNotif(string notifId)
    {
        if (string.IsNullOrEmpty(notifId)) return;
        lock (_lock) { if (_loggedNotifs.Count < 2000) _loggedNotifs.Add(notifId); }
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO logged_notifs(notif_id) VALUES($id)";
            cmd.Parameters.AddWithValue("$id", notifId);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    // DB helpers

    private void DbInsertEvent(TimelineEvent ev, SqliteTransaction? tx)
    {
        try
        {
            using var cmd = _db.CreateCommand();
            if (tx != null) cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT OR REPLACE INTO events
                    (id,type,timestamp,world_id,world_name,world_thumb,location,
                     photo_path,photo_url,user_id,user_name,user_image,
                     notif_id,notif_type,notif_title,sender_name,sender_id,sender_image,message,
                     left_at,tracked)
                VALUES
                    ($id,$type,$ts,$wid,$wn,$wt,$loc,
                     $pp,$pu,$uid,$un,$ui,
                     $nid,$nt,$ntitle,$sn,$si,$sim,$msg,
                     $la,$tr)";
            cmd.Parameters.AddWithValue("$id",   ev.Id);
            cmd.Parameters.AddWithValue("$type", ev.Type);
            cmd.Parameters.AddWithValue("$ts",   ev.Timestamp);
            cmd.Parameters.AddWithValue("$wid",  ev.WorldId);
            cmd.Parameters.AddWithValue("$wn",   ev.WorldName);
            cmd.Parameters.AddWithValue("$wt",   ev.WorldThumb);
            cmd.Parameters.AddWithValue("$loc",  ev.Location);
            cmd.Parameters.AddWithValue("$pp",   ev.PhotoPath);
            cmd.Parameters.AddWithValue("$pu",   ev.PhotoUrl);
            cmd.Parameters.AddWithValue("$uid",  ev.UserId);
            cmd.Parameters.AddWithValue("$un",   ev.UserName);
            cmd.Parameters.AddWithValue("$ui",   ev.UserImage);
            cmd.Parameters.AddWithValue("$nid",    ev.NotifId);
            cmd.Parameters.AddWithValue("$nt",     ev.NotifType);
            cmd.Parameters.AddWithValue("$ntitle", ev.NotifTitle);
            cmd.Parameters.AddWithValue("$sn",     ev.SenderName);
            cmd.Parameters.AddWithValue("$si",   ev.SenderId);
            cmd.Parameters.AddWithValue("$sim",  ev.SenderImage);
            cmd.Parameters.AddWithValue("$msg",  ev.Message);
            cmd.Parameters.AddWithValue("$la",   string.IsNullOrEmpty(ev.LeftAt) ? (object)DBNull.Value : ev.LeftAt);
            cmd.Parameters.AddWithValue("$tr",   ev.Tracked);
            cmd.ExecuteNonQuery();

            if (ev.Players.Count > 0)
            {
                using var pcmd = _db.CreateCommand();
                if (tx != null) pcmd.Transaction = tx;
                pcmd.CommandText = @"INSERT OR REPLACE INTO event_players
                    (event_id,user_id,display_name,image,joined_at,left_at) VALUES($eid,$uid,$dn,$img,$ja,$la)";
                var pEid = pcmd.Parameters.Add("$eid", SqliteType.Text);
                var pUid = pcmd.Parameters.Add("$uid", SqliteType.Text);
                var pDn  = pcmd.Parameters.Add("$dn",  SqliteType.Text);
                var pImg = pcmd.Parameters.Add("$img", SqliteType.Text);
                var pJa  = pcmd.Parameters.Add("$ja",  SqliteType.Text);
                var pLa  = pcmd.Parameters.Add("$la",  SqliteType.Text);
                pEid.Value = ev.Id;
                foreach (var p in ev.Players)
                {
                    pUid.Value = p.UserId;
                    pDn.Value  = p.DisplayName;
                    pImg.Value = p.Image;
                    pJa.Value  = PlayerSnap.SerializeSessions(p.JoinedAts);
                    pLa.Value  = PlayerSnap.SerializeSessions(p.LeftAts);
                    pcmd.ExecuteNonQuery();
                }
            }
        }
        catch { }
    }

    private void DbUpdateEvent(TimelineEvent ev)
    {
        try
        {
            using var tx  = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                UPDATE events SET
                    world_name=$wn, world_thumb=$wt, user_image=$ui,
                    photo_url=$pu, message=$msg,
                    sender_name=$sn, sender_image=$sim
                WHERE id=$id";
            cmd.Parameters.AddWithValue("$wn",  ev.WorldName);
            cmd.Parameters.AddWithValue("$wt",  ev.WorldThumb);
            cmd.Parameters.AddWithValue("$ui",  ev.UserImage);
            cmd.Parameters.AddWithValue("$pu",  ev.PhotoUrl);
            cmd.Parameters.AddWithValue("$msg", ev.Message);
            cmd.Parameters.AddWithValue("$sn",  ev.SenderName);
            cmd.Parameters.AddWithValue("$sim", ev.SenderImage);
            cmd.Parameters.AddWithValue("$id",  ev.Id);
            cmd.ExecuteNonQuery();

            // Preserve existing sessions if the new PlayerSnap has empty values.
            var savedJoinTimes = new Dictionary<string, List<string>>();
            var savedLeftTimes = new Dictionary<string, List<string>>();
            using (var readCmd = _db.CreateCommand())
            {
                readCmd.Transaction = tx;
                readCmd.CommandText = "SELECT user_id, joined_at, left_at FROM event_players WHERE event_id=$eid AND (joined_at != '' OR left_at != '')";
                readCmd.Parameters.AddWithValue("$eid", ev.Id);
                using var rdr = readCmd.ExecuteReader();
                while (rdr.Read())
                {
                    var uid = rdr.GetString(0);
                    var ja  = PlayerSnap.ParseSessions(rdr.IsDBNull(1) ? "" : rdr.GetString(1));
                    var la  = PlayerSnap.ParseSessions(rdr.IsDBNull(2) ? "" : rdr.GetString(2));
                    if (ja.Count > 0) savedJoinTimes[uid] = ja;
                    if (la.Count > 0) savedLeftTimes[uid] = la;
                }
            }

            using var del = _db.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM event_players WHERE event_id=$eid";
            del.Parameters.AddWithValue("$eid", ev.Id);
            del.ExecuteNonQuery();

            if (ev.Players.Count > 0)
            {
                using var pcmd = _db.CreateCommand();
                pcmd.Transaction = tx;
                pcmd.CommandText = @"INSERT INTO event_players
                    (event_id,user_id,display_name,image,joined_at,left_at) VALUES($eid,$uid,$dn,$img,$ja,$la)";
                var pEid = pcmd.Parameters.Add("$eid", SqliteType.Text);
                var pUid = pcmd.Parameters.Add("$uid", SqliteType.Text);
                var pDn  = pcmd.Parameters.Add("$dn",  SqliteType.Text);
                var pImg = pcmd.Parameters.Add("$img", SqliteType.Text);
                var pJa  = pcmd.Parameters.Add("$ja",  SqliteType.Text);
                var pLa  = pcmd.Parameters.Add("$la",  SqliteType.Text);
                pEid.Value = ev.Id;
                foreach (var p in ev.Players)
                {
                    pUid.Value = p.UserId;
                    pDn.Value  = p.DisplayName;
                    pImg.Value = p.Image;
                    var ja = (p.JoinedAts == null || p.JoinedAts.Count == 0) && savedJoinTimes.TryGetValue(p.UserId, out var savedJa) ? savedJa : p.JoinedAts;
                    var la = (p.LeftAts   == null || p.LeftAts.Count == 0)   && savedLeftTimes.TryGetValue(p.UserId, out var savedLa) ? savedLa : p.LeftAts;
                    pJa.Value = PlayerSnap.SerializeSessions(ja);
                    pLa.Value = PlayerSnap.SerializeSessions(la);
                    pcmd.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
        catch { }
    }

    private void DbInsertFriendEvent(FriendTimelineEvent ev)
    {
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO friend_events
                    (id,type,timestamp,friend_id,friend_name,friend_image,
                     world_id,world_name,world_thumb,location,old_value,new_value,left_at,tracked)
                VALUES
                    ($id,$type,$ts,$fid,$fn,$fi,$wid,$wn,$wt,$loc,$ov,$nv,$la,$tr)";
            cmd.Parameters.AddWithValue("$id",   ev.Id);
            cmd.Parameters.AddWithValue("$type", ev.Type);
            cmd.Parameters.AddWithValue("$ts",   ev.Timestamp);
            cmd.Parameters.AddWithValue("$fid",  ev.FriendId);
            cmd.Parameters.AddWithValue("$fn",   ev.FriendName);
            cmd.Parameters.AddWithValue("$fi",   ev.FriendImage);
            cmd.Parameters.AddWithValue("$wid",  ev.WorldId);
            cmd.Parameters.AddWithValue("$wn",   ev.WorldName);
            cmd.Parameters.AddWithValue("$wt",   ev.WorldThumb);
            cmd.Parameters.AddWithValue("$loc",  ev.Location);
            cmd.Parameters.AddWithValue("$ov",   ev.OldValue);
            cmd.Parameters.AddWithValue("$nv",   ev.NewValue);
            cmd.Parameters.AddWithValue("$la",   string.IsNullOrEmpty(ev.LeftAt) ? (object)DBNull.Value : ev.LeftAt);
            cmd.Parameters.AddWithValue("$tr",   ev.Tracked);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    private void DbInsertIgnoreEvent(TimelineEvent ev, SqliteTransaction tx)
    {
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT OR IGNORE INTO events
                (id,type,timestamp,world_id,world_name,world_thumb,location,
                 photo_path,photo_url,user_id,user_name,user_image,
                 notif_id,notif_type,notif_title,sender_name,sender_id,sender_image,message,
                 left_at,tracked)
                VALUES
                ($id,$type,$ts,$wid,$wn,$wt,$loc,
                 $pp,$pu,$uid,$un,$ui,
                 $nid,$nt,$ntitle,$sn,$si,$sim,$msg,
                 $la,$tr)";
            cmd.Parameters.AddWithValue("$id",     ev.Id);
            cmd.Parameters.AddWithValue("$type",   ev.Type);
            cmd.Parameters.AddWithValue("$ts",     ev.Timestamp);
            cmd.Parameters.AddWithValue("$wid",    ev.WorldId);
            cmd.Parameters.AddWithValue("$wn",     ev.WorldName);
            cmd.Parameters.AddWithValue("$wt",     ev.WorldThumb);
            cmd.Parameters.AddWithValue("$loc",    ev.Location);
            cmd.Parameters.AddWithValue("$pp",     ev.PhotoPath);
            cmd.Parameters.AddWithValue("$pu",     ev.PhotoUrl);
            cmd.Parameters.AddWithValue("$uid",    ev.UserId);
            cmd.Parameters.AddWithValue("$un",     ev.UserName);
            cmd.Parameters.AddWithValue("$ui",     ev.UserImage);
            cmd.Parameters.AddWithValue("$nid",    ev.NotifId);
            cmd.Parameters.AddWithValue("$nt",     ev.NotifType);
            cmd.Parameters.AddWithValue("$ntitle", ev.NotifTitle);
            cmd.Parameters.AddWithValue("$sn",     ev.SenderName);
            cmd.Parameters.AddWithValue("$si",     ev.SenderId);
            cmd.Parameters.AddWithValue("$sim",    ev.SenderImage);
            cmd.Parameters.AddWithValue("$msg",    ev.Message);
            cmd.Parameters.AddWithValue("$la",     string.IsNullOrEmpty(ev.LeftAt) ? (object)DBNull.Value : ev.LeftAt);
            cmd.Parameters.AddWithValue("$tr",     ev.Tracked);
            cmd.ExecuteNonQuery();

            if (ev.Players.Count > 0)
            {
                using var pcmd = _db.CreateCommand();
                pcmd.Transaction = tx;
                pcmd.CommandText = "INSERT OR IGNORE INTO event_players (event_id,user_id,display_name,image,joined_at,left_at) VALUES($eid,$uid,$dn,$img,$ja,$la)";
                var pEid = pcmd.Parameters.Add("$eid", SqliteType.Text);
                var pUid = pcmd.Parameters.Add("$uid", SqliteType.Text);
                var pDn  = pcmd.Parameters.Add("$dn",  SqliteType.Text);
                var pImg = pcmd.Parameters.Add("$img", SqliteType.Text);
                var pJa  = pcmd.Parameters.Add("$ja",  SqliteType.Text);
                var pLa  = pcmd.Parameters.Add("$la",  SqliteType.Text);
                pEid.Value = ev.Id;
                foreach (var p in ev.Players)
                {
                    pUid.Value = p.UserId;
                    pDn.Value  = p.DisplayName;
                    pImg.Value = p.Image;
                    pJa.Value  = PlayerSnap.SerializeSessions(p.JoinedAts);
                    pLa.Value  = PlayerSnap.SerializeSessions(p.LeftAts);
                    pcmd.ExecuteNonQuery();
                }
            }
        }
        catch { }
    }

    private void DbInsertIgnoreFriendEvent(FriendTimelineEvent ev, SqliteTransaction tx)
    {
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT OR IGNORE INTO friend_events
                (id,type,timestamp,friend_id,friend_name,friend_image,
                 world_id,world_name,world_thumb,location,old_value,new_value)
                VALUES
                ($id,$type,$ts,$fid,$fn,$fi,$wid,$wn,$wt,$loc,$ov,$nv)";
            cmd.Parameters.AddWithValue("$id",   ev.Id);
            cmd.Parameters.AddWithValue("$type", ev.Type);
            cmd.Parameters.AddWithValue("$ts",   ev.Timestamp);
            cmd.Parameters.AddWithValue("$fid",  ev.FriendId);
            cmd.Parameters.AddWithValue("$fn",   ev.FriendName);
            cmd.Parameters.AddWithValue("$fi",   ev.FriendImage);
            cmd.Parameters.AddWithValue("$wid",  ev.WorldId);
            cmd.Parameters.AddWithValue("$wn",   ev.WorldName);
            cmd.Parameters.AddWithValue("$wt",   ev.WorldThumb);
            cmd.Parameters.AddWithValue("$loc",  ev.Location);
            cmd.Parameters.AddWithValue("$ov",   ev.OldValue);
            cmd.Parameters.AddWithValue("$nv",   ev.NewValue);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    // Time Spent statistics

    public class WorldTimeEntry
    {
        public string WorldId    { get; set; } = "";
        public string WorldName  { get; set; } = "";
        public string WorldThumb { get; set; } = "";
        public long   Seconds    { get; set; }
        public int    Visits     { get; set; }
    }

    public class PersonTimeEntry
    {
        public string UserId      { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Image       { get; set; } = "";
        public long   Seconds     { get; set; }
        public int    Meets       { get; set; }
    }

    public class TimeSpentStats
    {
        public List<WorldTimeEntry>  Worlds  { get; set; } = new();
        public List<PersonTimeEntry> Persons { get; set; } = new();
        public long TotalSeconds { get; set; }
    }

    public TimeSpentStats GetTimeSpentStats(string selfId = "")
    {
        const long MAX_SESSION = 8L * 3600;

        var joins = new List<(string Id, DateTime Timestamp, string WorldId, string WorldName, string WorldThumb)>();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"SELECT id, timestamp, world_id, world_name, world_thumb
                FROM events WHERE type='instance_join' ORDER BY timestamp ASC";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (DateTime.TryParse(r.GetString(1), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                    joins.Add((r.GetString(0), dt, r.GetString(2), r.GetString(3), r.GetString(4)));
            }
        }
        catch { }

        if (joins.Count == 0)
            return new TimeSpentStats();

        var playerMap = new Dictionary<string, List<(string UserId, string Name, string Image)>>();
        try
        {
            var inP = string.Join(",", joins.Select((_, i) => $"$p{i}"));
            using var pcmd = _db.CreateCommand();
            pcmd.CommandText = $"SELECT event_id, user_id, display_name, image FROM event_players WHERE event_id IN ({inP})";
            for (int i = 0; i < joins.Count; i++) pcmd.Parameters.AddWithValue($"$p{i}", joins[i].Id);
            using var pr = pcmd.ExecuteReader();
            while (pr.Read())
            {
                var eid = pr.GetString(0);
                if (!playerMap.TryGetValue(eid, out var list)) playerMap[eid] = list = new();
                list.Add((pr.GetString(1), pr.GetString(2), pr.GetString(3)));
            }
        }
        catch { }

        var worldStats  = new Dictionary<string, (string Name, string Thumb, long Sec, int Visits)>();
        var personStats = new Dictionary<string, (string Name, string Image, long Sec, int Meets)>();
        long totalSec   = 0;

        for (int i = 0; i < joins.Count; i++)
        {
            var ev = joins[i];

            long sec;
            if (i + 1 < joins.Count)
                sec = (long)(joins[i + 1].Timestamp - ev.Timestamp).TotalSeconds;
            else
                sec = (long)(DateTime.UtcNow - ev.Timestamp).TotalSeconds; // ongoing session
            if (sec < 0)  sec = 0;
            if (sec > MAX_SESSION) sec = MAX_SESSION;

            totalSec += sec;

            if (!string.IsNullOrEmpty(ev.WorldId))
            {
                worldStats.TryGetValue(ev.WorldId, out var ws);
                var wName  = string.IsNullOrEmpty(ev.WorldName)  ? ws.Name  : ev.WorldName;
                var wThumb = string.IsNullOrEmpty(ev.WorldThumb) ? ws.Thumb : ev.WorldThumb;
                worldStats[ev.WorldId] = (wName, wThumb, ws.Sec + sec, ws.Visits + 1);
            }

            if (playerMap.TryGetValue(ev.Id, out var players))
            {
                foreach (var p in players)
                {
                    if (string.IsNullOrEmpty(p.UserId)) continue;
                    if (!string.IsNullOrEmpty(selfId) && p.UserId == selfId) continue;
                    personStats.TryGetValue(p.UserId, out var ps);
                    var pName  = string.IsNullOrEmpty(p.Name)  ? ps.Name  : p.Name;
                    var pImage = string.IsNullOrEmpty(p.Image) ? ps.Image : p.Image;
                    personStats[p.UserId] = (pName, pImage, ps.Sec + sec, ps.Meets + 1);
                }
            }
        }

        return new TimeSpentStats
        {
            TotalSeconds = totalSec,
            Worlds = worldStats
                .Select(kv => new WorldTimeEntry
                {
                    WorldId    = kv.Key,
                    WorldName  = kv.Value.Name,
                    WorldThumb = kv.Value.Thumb,
                    Seconds    = kv.Value.Sec,
                    Visits     = kv.Value.Visits,
                })
                .OrderByDescending(w => w.Seconds)
                .ToList(),
            Persons = personStats
                .Select(kv => new PersonTimeEntry
                {
                    UserId      = kv.Key,
                    DisplayName = kv.Value.Name,
                    Image       = kv.Value.Image,
                    Seconds     = kv.Value.Sec,
                    Meets       = kv.Value.Meets,
                })
                .OrderByDescending(p => p.Seconds)
                .ToList(),
        };
    }

    public class ProfileInsights
    {
        public List<WorldTimeEntry>  Worlds  { get; set; } = new();
        public List<PersonTimeEntry> Persons { get; set; } = new();
    }

    public ProfileInsights GetUserProfileInsights(string userId, string selfId = "", int limit = 10)
    {
        var result = new ProfileInsights();
        if (string.IsNullOrEmpty(userId)) return result;

        var worldStats  = new Dictionary<string, (string Name, string Thumb, int Visits)>();
        var personStats = new Dictionary<string, (string Name, string Image, int Meets)>();

        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"SELECT world_id, world_name, world_thumb FROM friend_events
                WHERE type='friend_gps' AND friend_id=$uid AND world_id != ''";
            cmd.Parameters.AddWithValue("$uid", userId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var wid = r.GetString(0);
                if (string.IsNullOrEmpty(wid)) continue;
                worldStats.TryGetValue(wid, out var ws);
                var name  = string.IsNullOrEmpty(r.GetString(1)) ? ws.Name  : r.GetString(1);
                var thumb = string.IsNullOrEmpty(r.GetString(2)) ? ws.Thumb : r.GetString(2);
                worldStats[wid] = (name, thumb, ws.Visits + 1);
            }
        }
        catch { }

        var eventIds = new List<string>();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"SELECT DISTINCT e.id FROM events e
                JOIN event_players ep ON e.id = ep.event_id
                WHERE e.type='instance_join' AND ep.user_id=$uid";
            cmd.Parameters.AddWithValue("$uid", userId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) eventIds.Add(r.GetString(0));
        }
        catch { }

        if (eventIds.Count > 0)
        {
            var worldByEvent = new Dictionary<string, (string WorldId, string Name, string Thumb)>();
            try
            {
                var inE = string.Join(",", eventIds.Select((_, i) => $"$e{i}"));
                using var cmd = _db.CreateCommand();
                cmd.CommandText = $"SELECT id, world_id, world_name, world_thumb FROM events WHERE id IN ({inE})";
                for (int i = 0; i < eventIds.Count; i++) cmd.Parameters.AddWithValue($"$e{i}", eventIds[i]);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    worldByEvent[r.GetString(0)] = (r.GetString(1), r.GetString(2), r.GetString(3));
            }
            catch { }

            var playersByEvent = new Dictionary<string, List<(string UserId, string Name, string Image)>>();
            try
            {
                var inE = string.Join(",", eventIds.Select((_, i) => $"$e{i}"));
                using var cmd = _db.CreateCommand();
                cmd.CommandText = $"SELECT event_id,user_id,display_name,image FROM event_players WHERE event_id IN ({inE})";
                for (int i = 0; i < eventIds.Count; i++) cmd.Parameters.AddWithValue($"$e{i}", eventIds[i]);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var eid = r.GetString(0);
                    if (!playersByEvent.TryGetValue(eid, out var list)) playersByEvent[eid] = list = new();
                    list.Add((r.GetString(1), r.GetString(2), r.GetString(3)));
                }
            }
            catch { }

            string selfName = "", selfImage = "";
            int selfMeets = 0;

            foreach (var eid in eventIds)
            {
                if (!playersByEvent.TryGetValue(eid, out var players)) continue;
                if (!players.Any(p => p.UserId == userId)) continue;

                if (worldByEvent.TryGetValue(eid, out var w) && !string.IsNullOrEmpty(w.WorldId))
                {
                    worldStats.TryGetValue(w.WorldId, out var ws);
                    var wName  = string.IsNullOrEmpty(w.Name)  ? ws.Name  : w.Name;
                    var wThumb = string.IsNullOrEmpty(w.Thumb) ? ws.Thumb : w.Thumb;
                    worldStats[w.WorldId] = (wName, wThumb, ws.Visits + 1);
                }

                if (!string.IsNullOrEmpty(selfId) && selfId != userId) selfMeets++;

                foreach (var p in players)
                {
                    if (string.IsNullOrEmpty(p.UserId) || p.UserId == userId) continue;
                    if (p.UserId == selfId)
                    {
                        if (string.IsNullOrEmpty(selfName)  && !string.IsNullOrEmpty(p.Name))  selfName  = p.Name;
                        if (string.IsNullOrEmpty(selfImage) && !string.IsNullOrEmpty(p.Image)) selfImage = p.Image;
                        continue;
                    }
                    personStats.TryGetValue(p.UserId, out var ps);
                    var pName  = string.IsNullOrEmpty(p.Name)  ? ps.Name  : p.Name;
                    var pImage = string.IsNullOrEmpty(p.Image) ? ps.Image : p.Image;
                    personStats[p.UserId] = (pName, pImage, ps.Meets + 1);
                }
            }

            if (selfMeets > 0)
            {
                personStats.TryGetValue(selfId, out var sps);
                personStats[selfId] = (
                    string.IsNullOrEmpty(selfName)  ? sps.Name  : selfName,
                    string.IsNullOrEmpty(selfImage) ? sps.Image : selfImage,
                    sps.Meets + selfMeets);
            }
        }

        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"SELECT c.friend_id, c.friend_name, c.friend_image
                FROM friend_events e
                JOIN friend_event_colocated c ON c.event_id = e.id
                WHERE e.type='friend_gps' AND e.friend_id=$uid";
            cmd.Parameters.AddWithValue("$uid", userId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var fid = r.GetString(0);
                if (string.IsNullOrEmpty(fid) || fid == userId) continue;
                personStats.TryGetValue(fid, out var ps);
                var name  = string.IsNullOrEmpty(r.GetString(1)) ? ps.Name  : r.GetString(1);
                var image = string.IsNullOrEmpty(r.GetString(2)) ? ps.Image : r.GetString(2);
                personStats[fid] = (name, image, ps.Meets + 1);
            }
        }
        catch { }

        result.Worlds = worldStats
            .Select(kv => new WorldTimeEntry
            {
                WorldId    = kv.Key,
                WorldName  = kv.Value.Name,
                WorldThumb = kv.Value.Thumb,
                Visits     = kv.Value.Visits,
            })
            .OrderByDescending(w => w.Visits)
            .Take(limit)
            .ToList();

        result.Persons = personStats
            .Select(kv => new PersonTimeEntry
            {
                UserId      = kv.Key,
                DisplayName = kv.Value.Name,
                Image       = kv.Value.Image,
                Meets       = kv.Value.Meets,
            })
            .OrderByDescending(p => p.Meets)
            .Take(limit)
            .ToList();

        return result;
    }

    public class OnlineHeatmap
    {
        public double[] Buckets      { get; set; } = new double[7 * 24];
        public double   TotalMinutes { get; set; }
        public int      Sessions     { get; set; }
    }

    private List<(DateTime Start, DateTime End)> BuildMergedOnlineSessions(string userId)
    {
        var result = new List<(DateTime Start, DateTime End)>();
        if (string.IsNullOrEmpty(userId)) return result;

        var events = new List<(DateTime Ts, bool IsOnline)>();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"SELECT timestamp, type FROM friend_events
                WHERE friend_id=$uid AND (type='friend_online' OR type='friend_offline')
                ORDER BY timestamp ASC";
            cmd.Parameters.AddWithValue("$uid", userId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!DateTime.TryParse(r.GetString(0), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt)) continue;
                events.Add((dt.ToUniversalTime(), r.GetString(1) == "friend_online"));
            }
        }
        catch { return result; }

        return MergeOnlineEvents(events);
    }

    private List<(DateTime Start, DateTime End)> BuildSelfOnlineSessions(string userId)
    {
        var result = new List<(DateTime Start, DateTime End)>();
        if (string.IsNullOrEmpty(userId)) return result;

        var events = new List<(DateTime Ts, bool IsOnline)>();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"SELECT timestamp, message FROM events
                WHERE type='profile' AND notif_type='launch' AND user_id=$uid
                ORDER BY timestamp ASC";
            cmd.Parameters.AddWithValue("$uid", userId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!DateTime.TryParse(r.GetString(0), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt)) continue;
                events.Add((dt.ToUniversalTime(), (r.IsDBNull(1) ? "" : r.GetString(1)) == "start"));
            }
        }
        catch { return result; }

        return MergeOnlineEvents(events);
    }

    private static List<(DateTime Start, DateTime End)> MergeOnlineEvents(List<(DateTime Ts, bool IsOnline)> events)
    {
        const double MAX_SESSION_MIN = 8 * 60;
        const double MERGE_GAP_MIN   = 5;

        var result = new List<(DateTime Start, DateTime End)>();
        if (events.Count == 0) return result;

        var now = DateTime.UtcNow;
        var sessions = new List<(DateTime Start, DateTime End)>();
        DateTime? curStart = null;
        foreach (var (ts, isOnline) in events)
        {
            if (isOnline)
            {
                if (curStart != null) sessions.Add((curStart.Value, ts));
                curStart = ts;
            }
            else if (curStart != null)
            {
                sessions.Add((curStart.Value, ts));
                curStart = null;
            }
        }
        if (curStart != null) sessions.Add((curStart.Value, now));

        sessions.Sort((a, b) => a.Start.CompareTo(b.Start));
        foreach (var s in sessions)
        {
            if (result.Count > 0 && (s.Start - result[^1].End).TotalMinutes <= MERGE_GAP_MIN)
            {
                if (s.End > result[^1].End) result[^1] = (result[^1].Start, s.End);
            }
            else result.Add(s);
        }

        for (int i = 0; i < result.Count; i++)
        {
            var cap = result[i].Start.AddMinutes(MAX_SESSION_MIN);
            if (result[i].End > cap) result[i] = (result[i].Start, cap);
        }

        return result;
    }

    public OnlineHeatmap GetUserOnlineHeatmap(string userId, int days = 30)
    {
        if (string.IsNullOrEmpty(userId)) return new OnlineHeatmap();
        return BuildHeatmap(BuildMergedOnlineSessions(userId), days);
    }

    public OnlineHeatmap GetSelfOnlineHeatmap(string userId, int days = 30)
    {
        if (string.IsNullOrEmpty(userId)) return new OnlineHeatmap();
        return BuildHeatmap(BuildSelfOnlineSessions(userId), days);
    }

    private static OnlineHeatmap BuildHeatmap(List<(DateTime Start, DateTime End)> merged, int days)
    {
        var hm = new OnlineHeatmap();
        if (merged.Count == 0) return hm;

        var now = DateTime.UtcNow;
        var windowStart = days > 0 ? now.AddDays(-days) : new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        foreach (var s in merged)
        {
            var start = s.Start > windowStart ? s.Start : windowStart;
            var end   = s.End   < now         ? s.End   : now;
            if (end <= start) continue;

            hm.Sessions++;
            hm.TotalMinutes += AddIntervalMinutes(hm.Buckets, start, end);
        }

        return hm;
    }

    private static double AddIntervalMinutes(double[] buckets, DateTime start, DateTime end)
    {
        double total = 0;
        var cursor = start;
        while (cursor < end)
        {
            var local = cursor.ToLocalTime();
            int dow  = ((int)local.DayOfWeek + 6) % 7;
            int hour = local.Hour;
            var nextBoundaryUtc = local.Date.AddHours(hour + 1).ToUniversalTime();
            var segEnd = nextBoundaryUtc < end ? nextBoundaryUtc : end;
            var mins = (segEnd - cursor).TotalMinutes;
            buckets[dow * 24 + hour] += mins;
            total += mins;
            cursor = segEnd;
        }
        return total;
    }

    public class StatusBreakdown
    {
        public Dictionary<string, double[]> Buckets { get; set; } = new();
        public Dictionary<string, double>   Seconds { get; set; } = new();
        public double TotalSeconds { get; set; }
    }

    public void UpdateUserLastStatus(string userId, string status)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(status) || status == "offline") return;
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"INSERT INTO user_tracking (user_id, last_status, last_status_at)
                    VALUES ($uid, $st, $ts)
                    ON CONFLICT(user_id) DO UPDATE SET last_status = excluded.last_status, last_status_at = excluded.last_status_at";
                cmd.Parameters.AddWithValue("$uid", userId);
                cmd.Parameters.AddWithValue("$st",  status);
                cmd.Parameters.AddWithValue("$ts",  DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }

    private string GetUserSeedStatus(string userId)
    {
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT last_status, profile_status FROM user_tracking WHERE user_id=$uid";
            cmd.Parameters.AddWithValue("$uid", userId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                var ls = r.IsDBNull(0) ? "" : r.GetString(0);
                if (!string.IsNullOrEmpty(ls) && ls != "offline") return ls;
                var ps = r.IsDBNull(1) ? "" : r.GetString(1);
                if (!string.IsNullOrEmpty(ps) && ps != "offline") return ps;
            }
        }
        catch { }
        return "";
    }

    public StatusBreakdown GetUserStatusBreakdown(string userId, int days = 30)
    {
        if (string.IsNullOrEmpty(userId)) return new StatusBreakdown();
        var transitions = ReadStatusTransitions(
            @"SELECT timestamp, old_value, new_value FROM friend_events
              WHERE type='friend_status' AND friend_id=$uid ORDER BY timestamp ASC",
            userId, out var initial);
        return BuildStatusBreakdown(BuildMergedOnlineSessions(userId), transitions, initial, userId, days);
    }

    public StatusBreakdown GetSelfStatusBreakdown(string userId, int days = 30)
    {
        if (string.IsNullOrEmpty(userId)) return new StatusBreakdown();
        var transitions = ReadStatusTransitions(
            @"SELECT timestamp, notif_title, message FROM events
              WHERE type='profile' AND notif_type='status' AND user_id=$uid ORDER BY timestamp ASC",
            userId, out var initial);
        return BuildStatusBreakdown(BuildSelfOnlineSessions(userId), transitions, initial, userId, days);
    }

    private List<(DateTime Ts, string Status)> ReadStatusTransitions(string sql, string userId, out string initialStatus)
    {
        var transitions = new List<(DateTime Ts, string Status)>();
        initialStatus = "";
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$uid", userId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!DateTime.TryParse(r.GetString(0), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt)) continue;
                if (transitions.Count == 0) initialStatus = r.IsDBNull(1) ? "" : r.GetString(1);
                transitions.Add((dt.ToUniversalTime(), r.IsDBNull(2) ? "" : r.GetString(2)));
            }
        }
        catch { }
        return transitions;
    }

    private StatusBreakdown BuildStatusBreakdown(
        List<(DateTime Start, DateTime End)> sessions,
        List<(DateTime Ts, string Status)> transitions,
        string initialStatus, string userId, int days)
    {
        var bd = new StatusBreakdown();
        if (sessions.Count == 0) return bd;

        if (string.IsNullOrEmpty(initialStatus))
        {
            var seed = GetUserSeedStatus(userId);
            initialStatus = string.IsNullOrEmpty(seed) ? "active" : seed;
        }

        var now = DateTime.UtcNow;
        var windowStart = days > 0 ? now.AddDays(-days) : new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        foreach (var s in sessions)
        {
            var start = s.Start > windowStart ? s.Start : windowStart;
            var end   = s.End   < now         ? s.End   : now;
            if (end <= start) continue;

            var curStatus = StatusAt(start, transitions, initialStatus);
            var cursor = start;
            foreach (var tr in transitions)
            {
                if (tr.Ts <= cursor) continue;
                if (tr.Ts >= end) break;
                AddStatusSegment(bd, curStatus, cursor, tr.Ts);
                curStatus = tr.Status;
                cursor = tr.Ts;
            }
            AddStatusSegment(bd, curStatus, cursor, end);
        }

        return bd;
    }

    private static string StatusAt(DateTime time, List<(DateTime Ts, string Status)> transitions, string initialStatus)
    {
        var status = initialStatus;
        foreach (var tr in transitions)
        {
            if (tr.Ts > time) break;
            status = tr.Status;
        }
        return status;
    }

    private static void AddStatusSegment(StatusBreakdown bd, string status, DateTime start, DateTime end)
    {
        var seconds = (end - start).TotalSeconds;
        if (seconds <= 0) return;
        if (string.IsNullOrEmpty(status) || status == "offline") status = "active";
        if (!bd.Buckets.TryGetValue(status, out var buckets)) bd.Buckets[status] = buckets = new double[7 * 24];
        AddIntervalMinutes(buckets, start, end);
        bd.Seconds.TryGetValue(status, out var cur);
        bd.Seconds[status] = cur + seconds;
        bd.TotalSeconds += seconds;
    }

    // World Insights.

    public class WorldStatPoint
    {
        public string Timestamp { get; set; } = "";
        public int Active    { get; set; }
        public int Favorites { get; set; }
        public int Visits    { get; set; }
    }

    public void InsertWorldStats(string worldId, int active, int favorites, int visits)
    {
        lock (_lock)
        {
            var hourBucket = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH':00:00Z'");
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"INSERT INTO world_stats (world_id, timestamp, active, favorites, visits)
                VALUES (@wid, @ts, @a, @f, @v)
                ON CONFLICT(world_id, timestamp) DO UPDATE SET
                    active = excluded.active, favorites = excluded.favorites, visits = excluded.visits";
            cmd.Parameters.AddWithValue("@wid", worldId);
            cmd.Parameters.AddWithValue("@ts", hourBucket);
            cmd.Parameters.AddWithValue("@a", active);
            cmd.Parameters.AddWithValue("@f", favorites);
            cmd.Parameters.AddWithValue("@v", visits);
            cmd.ExecuteNonQuery();
        }
    }

    public int GetTodaysVisits(string worldId)
    {
        lock (_lock)
        {
            var dayStart = DateTime.UtcNow.ToString("yyyy-MM-dd'T'00:00:00Z");
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT visits FROM world_stats WHERE world_id = @wid AND timestamp >= @day AND visits > 0 ORDER BY timestamp DESC LIMIT 1";
            cmd.Parameters.AddWithValue("@wid", worldId);
            cmd.Parameters.AddWithValue("@day", dayStart);
            var result = cmd.ExecuteScalar();
            return result != null && result != DBNull.Value ? Convert.ToInt32(result) : 0;
        }
    }

    public List<WorldStatPoint> GetWorldStats(string worldId, string fromIso, string toIso)
    {
        lock (_lock)
        {
            var list = new List<WorldStatPoint>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT timestamp, active, favorites, visits FROM world_stats WHERE world_id = @wid AND timestamp >= @from AND timestamp <= @to ORDER BY timestamp ASC";
            cmd.Parameters.AddWithValue("@wid", worldId);
            cmd.Parameters.AddWithValue("@from", fromIso);
            cmd.Parameters.AddWithValue("@to", toIso);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new WorldStatPoint
                {
                    Timestamp = r.GetString(0),
                    Active    = r.GetInt32(1),
                    Favorites = r.GetInt32(2),
                    Visits    = r.GetInt32(3),
                });
            }
            return list;
        }
    }

    public bool HasWorldStatsForCurrentHour()
    {
        lock (_lock)
        {
            var hourBucket = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH':00:00Z'");
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM world_stats WHERE timestamp = @ts";
            cmd.Parameters.AddWithValue("@ts", hourBucket);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }
    }

    // Month activity for calendar dot debug

    public (Dictionary<string, int> Personal, Dictionary<string, int> Friends)
        GetMonthActivity(int year, int month)
    {
        var localStart = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Local);
        var localEnd   = localStart.AddMonths(1);
        var utcStart   = localStart.ToUniversalTime().ToString("o");
        var utcEnd     = localEnd.ToUniversalTime().ToString("o");

        var personal = new Dictionary<string, int>();
        var friends  = new Dictionary<string, int>();

        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT timestamp FROM events WHERE timestamp >= $s AND timestamp < $e";
            cmd.Parameters.AddWithValue("$s", utcStart);
            cmd.Parameters.AddWithValue("$e", utcEnd);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var ts = r.GetString(0);
                if (DateTime.TryParse(ts, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                {
                    var key = dt.ToLocalTime().ToString("yyyy-MM-dd");
                    personal[key] = personal.TryGetValue(key, out var c) ? c + 1 : 1;
                }
            }
        }
        catch { }

        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT timestamp FROM friend_events WHERE timestamp >= $s AND timestamp < $e";
            cmd.Parameters.AddWithValue("$s", utcStart);
            cmd.Parameters.AddWithValue("$e", utcEnd);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var ts = r.GetString(0);
                if (DateTime.TryParse(ts, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                {
                    var key = dt.ToLocalTime().ToString("yyyy-MM-dd");
                    friends[key] = friends.TryGetValue(key, out var c) ? c + 1 : 1;
                }
            }
        }
        catch { }

        return (personal, friends);
    }

    public List<int[]> GetSharedSessionWeights(List<string> ids, int days = 180, double halfLifeDays = 60)
    {
        var result = new List<int[]>();
        if (ids == null || ids.Count < 2) return result;

        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < ids.Count; i++)
        {
            if (!string.IsNullOrEmpty(ids[i]) && !index.ContainsKey(ids[i])) index[ids[i]] = i;
        }

        var now    = DateTime.UtcNow;
        var cutoff = now.AddDays(-days).ToString("o");
        long n     = ids.Count;

        var seen = new List<(long Key, long Ticks)>();

        void Collect(List<int> group, DateTime when)
        {
            if (group.Count < 2 || group.Count > 64) return;
            group.Sort();
            for (int a = 0; a < group.Count; a++)
            {
                for (int b = a + 1; b < group.Count; b++)
                {
                    if (group[a] == group[b]) continue;
                    seen.Add((group[a] * n + group[b], when.Ticks));
                }
            }
        }

        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"SELECT p.event_id, e.timestamp, p.user_id
                FROM event_players p
                JOIN events e ON e.id = p.event_id
                WHERE e.type='instance_join' AND e.timestamp >= $cut
                ORDER BY e.timestamp ASC, p.event_id ASC";
            cmd.Parameters.AddWithValue("$cut", cutoff);
            using var r = cmd.ExecuteReader();

            var curId    = "";
            var curWhen  = DateTime.MinValue;
            var curGroup = new List<int>();
            while (r.Read())
            {
                var eid = r.GetString(0);
                if (eid != curId)
                {
                    Collect(curGroup, curWhen);
                    curGroup = new List<int>();
                    curId    = eid;
                    DateTime.TryParse(r.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind, out curWhen);
                }
                if (index.TryGetValue(r.GetString(2), out var ix)) curGroup.Add(ix);
            }
            Collect(curGroup, curWhen);
        }
        catch { }

        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"SELECT e.id, e.timestamp, e.friend_id, c.friend_id
                FROM friend_events e
                JOIN friend_event_colocated c ON c.event_id = e.id
                WHERE e.type='friend_gps' AND e.timestamp >= $cut
                ORDER BY e.timestamp ASC, e.id ASC";
            cmd.Parameters.AddWithValue("$cut", cutoff);
            using var r = cmd.ExecuteReader();

            var curId    = "";
            var curWhen  = DateTime.MinValue;
            var curGroup = new List<int>();
            while (r.Read())
            {
                var eid = r.GetString(0);
                if (eid != curId)
                {
                    Collect(curGroup, curWhen);
                    curGroup = new List<int>();
                    curId    = eid;
                    DateTime.TryParse(r.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind, out curWhen);
                    if (index.TryGetValue(r.GetString(2), out var self)) curGroup.Add(self);
                }
                if (index.TryGetValue(r.GetString(3), out var ix)) curGroup.Add(ix);
            }
            Collect(curGroup, curWhen);
        }
        catch { }

        if (seen.Count == 0) return result;

        seen.Sort((x, y) => x.Ticks != y.Ticks ? x.Ticks.CompareTo(y.Ticks) : x.Key.CompareTo(y.Key));

        var sessionGap = TimeSpan.FromMinutes(20).Ticks;
        var lastSeen   = new Dictionary<long, long>();
        var weights    = new Dictionary<long, double>();

        foreach (var (key, ticks) in seen)
        {
            if (lastSeen.TryGetValue(key, out var prev) && ticks - prev < sessionGap) continue;
            lastSeen[key] = ticks;

            var age = (now - new DateTime(ticks, DateTimeKind.Utc)).TotalDays;
            if (age < 0) age = 0;
            var mult = Math.Pow(0.5, age / halfLifeDays);
            weights[key] = weights.TryGetValue(key, out var w) ? w + mult : mult;
        }

        foreach (var kv in weights)
        {
            var a = (int)(kv.Key / n);
            var b = (int)(kv.Key % n);
            var w = (int)Math.Round(kv.Value * 1000);
            if (w > 0) result.Add(new[] { a, b, w });
        }
        return result;
    }

    // Disposal

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _db?.Dispose(); } catch { }
    }
}
