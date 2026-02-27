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
                notif_id,notif_type,sender_name,sender_id,sender_image,message
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
                    SenderName  = r.GetString(14),
                    SenderId    = r.GetString(15),
                    SenderImage = r.GetString(16),
                    Message     = r.GetString(17),
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
                     notif_id,notif_type,sender_name,sender_id,sender_image,message)
                VALUES
                    ($id,$type,$ts,$wid,$wn,$wt,$loc,
                     $pp,$pu,$uid,$un,$ui,
                     $nid,$nt,$sn,$si,$sim,$msg)";
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
            cmd.Parameters.AddWithValue("$nid",  ev.NotifId);
            cmd.Parameters.AddWithValue("$nt",   ev.NotifType);
            cmd.Parameters.AddWithValue("$sn",   ev.SenderName);
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
                    world_name==$wn, world_thumb=$wt, user_image=$ui,
                    photo_url=$pu, message=$msg
                WHERE id=$id";
            cmd.Parameters.AddWithValue("$wn",  ev.WorldName);
            cmd.Parameters.AddWithValue("$wt",  ev.WorldThumb);
            cmd.Parameters.AddWithValue("$ui",  ev.UserImage);
            cmd.Parameters.AddWithValue("$pu",  ev.PhotoUrl);
            cmd.Parameters.AddWithValue("$msg", ev.Message);
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

    // Disposal

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _db.Close(); } catch { }
        _db.Dispose();
    }
}
