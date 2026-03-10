using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace VRCNext.Services;

/// <summary>
/// Persists timeline events to AppData\Roaming\VRCNext\VRCNData.db (SQLite).
/// Keeps in-memory caches for fast lookups; writes are incremental (no full-file rewrite).
/// Automatically migrates from the legacy JSON files on first run.
/// </summary>
public class TimelineService : IDisposable
{
    // Public data classes

    public class FriendTimelineEvent
    {
        public string Id          { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Type        { get; set; } = ""; // friend_gps | friend_status | friend_offline | friend_online | friend_bio
        public string Timestamp   { get; set; } = DateTime.UtcNow.ToString("o");
        public string FriendId    { get; set; } = "";
        public string FriendName  { get; set; } = "";
        public string FriendImage { get; set; } = "";
        public string WorldId     { get; set; } = "";
        public string WorldName   { get; set; } = "";
        public string WorldThumb  { get; set; } = "";
        public string Location    { get; set; } = "";
        public string OldValue    { get; set; } = ""; // old status (status events) or old bio (bio events)
        public string NewValue    { get; set; } = ""; // new status or new bio
    }

    public class PlayerSnap
    {
        public string UserId      { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Image       { get; set; } = "";
    }

    public class TimelineEvent
    {
        public string Id        { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Type      { get; set; } = "";
        public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");

        // World context
        public string WorldId    { get; set; } = "";
        public string WorldName  { get; set; } = "";
        public string WorldThumb { get; set; } = "";
        public string Location   { get; set; } = "";

        // Players present (instance_join, photo)
        public List<PlayerSnap> Players { get; set; } = new();

        // Photo event fields
        public string PhotoPath { get; set; } = "";
        public string PhotoUrl  { get; set; } = "";

        // First meet / user event fields
        public string UserId    { get; set; } = "";
        public string UserName  { get; set; } = "";
        public string UserImage { get; set; } = "";

        // Notification event fields
        public string NotifId      { get; set; } = "";
        public string NotifType    { get; set; } = "";
        public string NotifTitle   { get; set; } = "";
        public string SenderName   { get; set; } = "";
        public string SenderId     { get; set; } = "";
        public string SenderImage  { get; set; } = "";
        public string Message      { get; set; } = "";
    }

    // In-memory caches

    private readonly List<TimelineEvent>       _events       = new();
    private readonly List<FriendTimelineEvent> _friendEvents = new();
    private readonly HashSet<string>           _knownUserIds = new();
    private readonly HashSet<string>           _loggedNotifs = new();
    private readonly object                    _lock         = new();
    private bool                               _knownUsersSeeded;
    private bool                               _disposed;

    // Database

    private readonly SqliteConnection _db;

    // Legacy JSON paths (migration only)
    private static readonly string LegacyEventsJson = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "timeline_events.json");
    private static readonly string LegacyKnownUsersJson = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "timeline_known_users.json");

    // Public properties

    public bool KnownUsersSeeded => _knownUsersSeeded;

    public HashSet<string> GetKnownUserIds() { lock (_lock) return new HashSet<string>(_knownUserIds); }

    // Constructor / factory

    private TimelineService(SqliteConnection db) { _db = db; }

    public static TimelineService Load()
    {
        var conn = Database.OpenConnection();

        var svc = new TimelineService(conn);
        svc.InitSchema();
        svc.MigrateFromJson();
        svc.LoadFromDb();
        return svc;
    }

    // Schema

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
        // Column migration (safe — SQLite ignores ADD COLUMN if caught)
        try { using var mc = _db.CreateCommand(); mc.CommandText = "ALTER TABLE events ADD COLUMN notif_title TEXT NOT NULL DEFAULT ''"; mc.ExecuteNonQuery(); } catch { }
    }

    // JSON to SQLite migration

    private void MigrateFromJson()
    {
        // timeline_events.json
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
            catch { /* Leave JSON intact if migration fails */ }
        }

        // timeline_known_users.json
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

    // Load from DB into memory

    private void LoadFromDb()
    {
        // Load all events + their players
        var playerMap = new Dictionary<string, List<PlayerSnap>>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT event_id, user_id, display_name, image FROM event_players";
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
                });
            }
        }

        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = @"SELECT id,type,timestamp,world_id,world_name,world_thumb,
                location,photo_path,photo_url,user_id,user_name,user_image,
                notif_id,notif_type,notif_title,sender_name,sender_id,sender_image,message
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
                    Players     = playerMap.TryGetValue(id, out var pl) ? pl : new(),
                };
                _events.Add(ev);
                if (ev.Type == "notification" && !string.IsNullOrEmpty(ev.NotifId))
                    _loggedNotifs.Add(ev.NotifId);
            }
        }

        // Load known users
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT user_id FROM known_users";
            using var r = cmd.ExecuteReader();
            while (r.Read()) _knownUserIds.Add(r.GetString(0));
        }
        if (_knownUserIds.Count > 0) _knownUsersSeeded = true;

        // Load logged notif IDs (beyond what's in events)
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT notif_id FROM logged_notifs";
            using var r = cmd.ExecuteReader();
            while (r.Read()) _loggedNotifs.Add(r.GetString(0));
        }

        // Load friend timeline events
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = @"SELECT id,type,timestamp,friend_id,friend_name,friend_image,
                world_id,world_name,world_thumb,location,old_value,new_value
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
                });
            }
        }
    }

    // Public API

    public void AddEvent(TimelineEvent ev)
    {
        lock (_lock)
        {
            _events.Add(ev);
            DbInsertEvent(ev, null);
        }
    }

    /// <summary>Bulk-imports events (e.g. from VRCX). Skips duplicates via INSERT OR IGNORE on id.</summary>
    public void BulkImportEvents(IEnumerable<TimelineEvent> events)
    {
        lock (_lock)
        {
            try
            {
                using var tx = _db.BeginTransaction();
                foreach (var ev in events)
                {
                    DbInsertIgnoreEvent(ev, tx);
                    if (!_events.Any(e => e.Id == ev.Id)) _events.Add(ev);
                }
                tx.Commit();
            }
            catch { }
        }
    }

    /// <summary>Bulk-imports friend events (e.g. from VRCX). Skips duplicates via INSERT OR IGNORE on id.</summary>
    public void BulkImportFriendEvents(IEnumerable<FriendTimelineEvent> events)
    {
        lock (_lock)
        {
            try
            {
                using var tx = _db.BeginTransaction();
                foreach (var ev in events)
                {
                    DbInsertIgnoreFriendEvent(ev, tx);
                    if (!_friendEvents.Any(e => e.Id == ev.Id)) _friendEvents.Add(ev);
                }
                tx.Commit();
            }
            catch { }
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

    /// <summary>Returns the total count of personal timeline events, optionally filtered by type.</summary>
    /// <summary>Returns the total number of meet_again events for a specific userId.</summary>
    public long GetMeetAgainCount(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return 0;
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM events WHERE type = 'meet_again' AND user_id = $uid";
            cmd.Parameters.AddWithValue("$uid", userId);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }
        catch { return 0; }
    }

    public long GetEventCount(string typeFilter = "")
    {
        try
        {
            using var cmd = _db.CreateCommand();
            var typeClause = string.IsNullOrEmpty(typeFilter) ? "" : "WHERE type = $type";
            cmd.CommandText = $"SELECT COUNT(*) FROM events {typeClause}";
            if (!string.IsNullOrEmpty(typeFilter)) cmd.Parameters.AddWithValue("$type", typeFilter);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }
        catch { return 0; }
    }

    /// <summary>Returns the total count of friend timeline events, optionally filtered by type.</summary>
    public long GetFriendEventCount(string typeFilter = "")
    {
        try
        {
            using var cmd = _db.CreateCommand();
            var hasType = !string.IsNullOrEmpty(typeFilter) && typeFilter != "all";
            var typeClause = hasType ? "WHERE type = $type" : "";
            cmd.CommandText = $"SELECT COUNT(*) FROM friend_events {typeClause}";
            if (hasType) cmd.Parameters.AddWithValue("$type", typeFilter);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }
        catch { return 0; }
    }

    /// <summary>Returns the exact total count of events matching a search query (same WHERE as SearchEvents).</summary>
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
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }
        catch { return 0; }
    }

    /// <summary>Returns the exact total count of friend events matching a search query (same WHERE as SearchFriendEvents).</summary>
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
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }
        catch { return 0; }
    }

    /// <summary>Returns a page of personal timeline events directly from DB (newest first). HasMore=true if more exist beyond this page.</summary>
    public (List<TimelineEvent> Events, bool HasMore) GetEventsPaged(int limit, int offset, string typeFilter = "")
    {
        var ids = new List<string>();
        try
        {
            using var cmd = _db.CreateCommand();
            var typeClause = string.IsNullOrEmpty(typeFilter) ? "" : "WHERE type = $type";
            cmd.CommandText = $"SELECT id FROM events {typeClause} ORDER BY timestamp DESC LIMIT $limit OFFSET $offset";
            cmd.Parameters.AddWithValue("$limit",  limit + 1);
            cmd.Parameters.AddWithValue("$offset", offset);
            if (!string.IsNullOrEmpty(typeFilter)) cmd.Parameters.AddWithValue("$type", typeFilter);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetString(0));
        }
        catch { return (new List<TimelineEvent>(), false); }

        var hasMore = ids.Count > limit;
        if (hasMore) ids.RemoveAt(ids.Count - 1);
        if (ids.Count == 0) return (new List<TimelineEvent>(), hasMore);

        var playerMap = new Dictionary<string, List<PlayerSnap>>();
        try
        {
            var inP = string.Join(",", ids.Select((_, i) => $"$p{i}"));
            using var pcmd = _db.CreateCommand();
            pcmd.CommandText = $"SELECT event_id,user_id,display_name,image FROM event_players WHERE event_id IN ({inP})";
            for (int i = 0; i < ids.Count; i++) pcmd.Parameters.AddWithValue($"$p{i}", ids[i]);
            using var pr = pcmd.ExecuteReader();
            while (pr.Read())
            {
                var eid = pr.GetString(0);
                if (!playerMap.TryGetValue(eid, out var list)) playerMap[eid] = list = new();
                list.Add(new PlayerSnap { UserId = pr.GetString(1), DisplayName = pr.GetString(2), Image = pr.GetString(3) });
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
                notif_id,notif_type,notif_title,sender_name,sender_id,sender_image,message
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
                    Players     = playerMap.TryGetValue(id, out var pl) ? pl : new(),
                });
            }
        }
        catch { }
        return (result, hasMore);
    }

    /// <summary>
    /// Full-DB search: returns a page of events whose user_name, world_name, message,
    /// or any player display_name matches the query. Optionally filtered by type.
    /// </summary>
    public (List<TimelineEvent> Events, bool HasMore) SearchEvents(string query, string typeFilter = "", string date = "", int offset = 0)
    {
        if (string.IsNullOrWhiteSpace(query)) return (new List<TimelineEvent>(), false);
        var like = "%" + query.Replace("%", "\\%").Replace("_", "\\_") + "%";

        // Optional date range (local calendar day → UTC window)
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
            cmd.Parameters.AddWithValue("$limit",  101);
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

        var hasMore = ids.Count > 100;
        if (hasMore) ids.RemoveAt(ids.Count - 1);
        if (ids.Count == 0) return (new List<TimelineEvent>(), hasMore);

        // Load players for matched events
        var playerMap = new Dictionary<string, List<PlayerSnap>>();
        try
        {
            var inP = string.Join(",", ids.Select((_, i) => $"$p{i}"));
            using var pcmd = _db.CreateCommand();
            pcmd.CommandText = $"SELECT event_id,user_id,display_name,image FROM event_players WHERE event_id IN ({inP})";
            for (int i = 0; i < ids.Count; i++) pcmd.Parameters.AddWithValue($"$p{i}", ids[i]);
            using var pr = pcmd.ExecuteReader();
            while (pr.Read())
            {
                var eid = pr.GetString(0);
                if (!playerMap.TryGetValue(eid, out var list)) playerMap[eid] = list = new();
                list.Add(new PlayerSnap { UserId = pr.GetString(1), DisplayName = pr.GetString(2), Image = pr.GetString(3) });
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
                notif_id,notif_type,notif_title,sender_name,sender_id,sender_image,message
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
                    Players     = playerMap.TryGetValue(id, out var pl) ? pl : new(),
                });
            }
        }
        catch { }
        return (result, hasMore);
    }

    /// <summary>Returns all personal timeline events for a specific local calendar date.</summary>
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
            pcmd.CommandText = $"SELECT event_id,user_id,display_name,image FROM event_players WHERE event_id IN ({inP})";
            for (int i = 0; i < ids.Count; i++) pcmd.Parameters.AddWithValue($"$p{i}", ids[i]);
            using var pr = pcmd.ExecuteReader();
            while (pr.Read())
            {
                var eid = pr.GetString(0);
                if (!playerMap.TryGetValue(eid, out var list)) playerMap[eid] = list = new();
                list.Add(new PlayerSnap { UserId = pr.GetString(1), DisplayName = pr.GetString(2), Image = pr.GetString(3) });
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
                notif_id,notif_type,notif_title,sender_name,sender_id,sender_image,message
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
                    Players     = playerMap.TryGetValue(id, out var pl) ? pl : new(),
                });
            }
        }
        catch { }
        return result;
    }

    // Friend timeline events

    public void AddFriendEvent(FriendTimelineEvent ev)
    {
        lock (_lock)
        {
            _friendEvents.Add(ev);
            DbInsertFriendEvent(ev);
        }
    }

    public List<FriendTimelineEvent> GetFriendEvents()
    {
        lock (_lock)
            return _friendEvents.OrderByDescending(e => e.Timestamp).ToList();
    }

    /// <summary>Returns a page of friend events directly from DB (newest first). HasMore=true if more exist beyond this page.</summary>
    public (List<FriendTimelineEvent> Events, bool HasMore) GetFriendEventsPaged(
        int limit, int offset, string? type = null)
    {
        var result = new List<FriendTimelineEvent>();
        try
        {
            using var cmd = _db.CreateCommand();
            var hasType = !string.IsNullOrEmpty(type) && type != "all";
            cmd.CommandText = hasType
                ? @"SELECT id,type,timestamp,friend_id,friend_name,friend_image,
                       world_id,world_name,world_thumb,location,old_value,new_value
                       FROM friend_events WHERE type=$type
                       ORDER BY timestamp DESC LIMIT $limit OFFSET $offset"
                : @"SELECT id,type,timestamp,friend_id,friend_name,friend_image,
                       world_id,world_name,world_thumb,location,old_value,new_value
                       FROM friend_events
                       ORDER BY timestamp DESC LIMIT $limit OFFSET $offset";
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
                });
        }
        catch { }
        var hasMore = result.Count > limit;
        if (hasMore) result.RemoveAt(result.Count - 1);
        return (result, hasMore);
    }

    /// <summary>Returns all friend timeline events for a specific local calendar date, with optional type filter.</summary>
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
                       world_id,world_name,world_thumb,location,old_value,new_value
                       FROM friend_events WHERE type=$type AND timestamp >= $s AND timestamp < $e
                       ORDER BY timestamp DESC"
                : @"SELECT id,type,timestamp,friend_id,friend_name,friend_image,
                       world_id,world_name,world_thumb,location,old_value,new_value
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
                });
        }
        catch { }
        return result;
    }

    public (List<FriendTimelineEvent> Events, bool HasMore) SearchFriendEvents(string query, string date = "", int offset = 0, string typeFilter = "")
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
                       world_id,world_name,world_thumb,location,old_value,new_value
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
            cmd.Parameters.AddWithValue("$limit",  101);
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
                });
        }
        catch { }
        var hasMore = result.Count > 100;
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

    /// <summary>Returns other friend_gps events at the same location (base, nonce stripped), excluding <paramref name="excludeId"/>.</summary>
    public List<FriendTimelineEvent> GetFriendGpsColocated(string location, string excludeId)
    {
        // Match on the base location (before first ~) so nonce differences don't block matching
        var colon = location.IndexOf('~');
        var locBase = colon > 0 ? location[..colon] : location;
        if (string.IsNullOrEmpty(locBase)) return new();
        var result = new List<FriendTimelineEvent>();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"
                SELECT DISTINCT friend_id, friend_name, friend_image
                FROM friend_events
                WHERE type='friend_gps' AND id != $excl
                  AND (location = $loc OR location LIKE $locPrefix)
                ORDER BY timestamp DESC LIMIT 50";
            cmd.Parameters.AddWithValue("$excl",      excludeId);
            cmd.Parameters.AddWithValue("$loc",       locBase);
            cmd.Parameters.AddWithValue("$locPrefix", locBase + "~%");
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(new FriendTimelineEvent
                {
                    FriendId    = r.GetString(0),
                    FriendName  = r.GetString(1),
                    FriendImage = r.GetString(2),
                });
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

    // Known users

    public bool IsKnownUser(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return true;
        lock (_lock) return _knownUserIds.Contains(userId);
    }

    public void SeedKnownUsers(IEnumerable<string> userIds)
    {
        var toAdd = userIds.Where(x => !string.IsNullOrEmpty(x)).ToList();
        lock (_lock)
        {
            foreach (var id in toAdd) _knownUserIds.Add(id);
            _knownUsersSeeded = true;
        }
        try
        {
            using var tx  = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO known_users(user_id) VALUES($id)";
            var p = cmd.Parameters.Add("$id", SqliteType.Text);
            foreach (var id in toAdd) { p.Value = id; cmd.ExecuteNonQuery(); }
            tx.Commit();
        }
        catch { }
    }

    public void AddKnownUser(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return;
        lock (_lock) _knownUserIds.Add(userId);
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO known_users(user_id) VALUES($id)";
            cmd.Parameters.AddWithValue("$id", userId);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    // Notification dedup

    public bool IsLoggedNotif(string notifId)
    {
        if (string.IsNullOrEmpty(notifId)) return true;
        lock (_lock) return _loggedNotifs.Contains(notifId);
    }

    public void AddLoggedNotif(string notifId)
    {
        if (string.IsNullOrEmpty(notifId)) return;
        lock (_lock) _loggedNotifs.Add(notifId);
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
                     notif_id,notif_type,notif_title,sender_name,sender_id,sender_image,message)
                VALUES
                    ($id,$type,$ts,$wid,$wn,$wt,$loc,
                     $pp,$pu,$uid,$un,$ui,
                     $nid,$nt,$ntitle,$sn,$si,$sim,$msg)";
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
            cmd.ExecuteNonQuery();

            // Insert players
            if (ev.Players.Count > 0)
            {
                using var pcmd = _db.CreateCommand();
                if (tx != null) pcmd.Transaction = tx;
                pcmd.CommandText = @"INSERT OR REPLACE INTO event_players
                    (event_id,user_id,display_name,image) VALUES($eid,$uid,$dn,$img)";
                var pEid = pcmd.Parameters.Add("$eid", SqliteType.Text);
                var pUid = pcmd.Parameters.Add("$uid", SqliteType.Text);
                var pDn  = pcmd.Parameters.Add("$dn",  SqliteType.Text);
                var pImg = pcmd.Parameters.Add("$img", SqliteType.Text);
                pEid.Value = ev.Id;
                foreach (var p in ev.Players)
                {
                    pUid.Value = p.UserId;
                    pDn.Value  = p.DisplayName;
                    pImg.Value = p.Image;
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

            // Replace all players for this event
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
                    (event_id,user_id,display_name,image) VALUES($eid,$uid,$dn,$img)";
                var pEid = pcmd.Parameters.Add("$eid", SqliteType.Text);
                var pUid = pcmd.Parameters.Add("$uid", SqliteType.Text);
                var pDn  = pcmd.Parameters.Add("$dn",  SqliteType.Text);
                var pImg = pcmd.Parameters.Add("$img", SqliteType.Text);
                pEid.Value = ev.Id;
                foreach (var p in ev.Players)
                {
                    pUid.Value = p.UserId;
                    pDn.Value  = p.DisplayName;
                    pImg.Value = p.Image;
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

    private void DbInsertIgnoreEvent(TimelineEvent ev, SqliteTransaction tx)
    {
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT OR IGNORE INTO events
                (id,type,timestamp,world_id,world_name,world_thumb,location,
                 photo_path,photo_url,user_id,user_name,user_image,
                 notif_id,notif_type,notif_title,sender_name,sender_id,sender_image,message)
                VALUES
                ($id,$type,$ts,$wid,$wn,$wt,$loc,
                 $pp,$pu,$uid,$un,$ui,
                 $nid,$nt,$ntitle,$sn,$si,$sim,$msg)";
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
            cmd.ExecuteNonQuery();

            if (ev.Players.Count > 0)
            {
                using var pcmd = _db.CreateCommand();
                pcmd.Transaction = tx;
                pcmd.CommandText = "INSERT OR IGNORE INTO event_players (event_id,user_id,display_name,image) VALUES($eid,$uid,$dn,$img)";
                var pEid = pcmd.Parameters.Add("$eid", SqliteType.Text);
                var pUid = pcmd.Parameters.Add("$uid", SqliteType.Text);
                var pDn  = pcmd.Parameters.Add("$dn",  SqliteType.Text);
                var pImg = pcmd.Parameters.Add("$img", SqliteType.Text);
                pEid.Value = ev.Id;
                foreach (var p in ev.Players)
                {
                    pUid.Value = p.UserId;
                    pDn.Value  = p.DisplayName;
                    pImg.Value = p.Image;
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

    // Time Spent statistics (computed from instance_join events)

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

    /// <summary>
    /// Calculates time spent per world and per person from instance_join events.
    /// Duration per session = time until next join, capped at 8 hours.
    /// The current user (selfId) is excluded from the persons list.
    /// </summary>
    public TimeSpentStats GetTimeSpentStats(string selfId = "")
    {
        const long MAX_SESSION = 8L * 3600;

        // Fetch all instance_join events in chronological order
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

        // Fetch players for all those events
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

            // Estimate session duration
            long sec;
            if (i + 1 < joins.Count)
                sec = (long)(joins[i + 1].Timestamp - ev.Timestamp).TotalSeconds;
            else
                sec = (long)(DateTime.UtcNow - ev.Timestamp).TotalSeconds; // ongoing session
            if (sec < 0)  sec = 0;
            if (sec > MAX_SESSION) sec = MAX_SESSION;

            totalSec += sec;

            // World
            if (!string.IsNullOrEmpty(ev.WorldId))
            {
                worldStats.TryGetValue(ev.WorldId, out var ws);
                // Keep the most recent (non-empty) world name/thumb
                var wName  = string.IsNullOrEmpty(ev.WorldName)  ? ws.Name  : ev.WorldName;
                var wThumb = string.IsNullOrEmpty(ev.WorldThumb) ? ws.Thumb : ev.WorldThumb;
                worldStats[ev.WorldId] = (wName, wThumb, ws.Sec + sec, ws.Visits + 1);
            }

            // Persons
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
                .Take(200)
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
                .Take(200)
                .ToList(),
        };
    }

    // Disposal

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _db.Close(); } catch { }
        _db.Dispose();
    }
}
