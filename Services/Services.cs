using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VRCNext.Services;

// Webhook Service - posts files to Discord, deletes messages
public class WebhookService
{
    private readonly HttpClient _http = new();

    public class PostResult
    {
        public bool Success { get; set; }
        public string? MessageId { get; set; }
        public string? Error { get; set; }
    }

    public class PostRecord
    {
        public string MessageId { get; set; } = "";
        public string WebhookUrl { get; set; } = "";
        public string WebhookName { get; set; } = "";
        public string FileName { get; set; } = "";
        public double SizeMB { get; set; }
        public DateTime PostedAt { get; set; } = DateTime.Now;
    }

    public async Task<PostResult> PostFileAsync(string url, string path, string? name = null, string? avatar = null)
    {
        try
        {
            if (!File.Exists(path)) return new() { Error = "File not found" };
            using var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(await File.ReadAllBytesAsync(path)), "file", Path.GetFileName(path));
            if (!string.IsNullOrEmpty(name)) content.Add(new StringContent(name), "username");
            if (!string.IsNullOrEmpty(avatar)) content.Add(new StringContent(avatar), "avatar_url");
            var resp = await _http.PostAsync(url.TrimEnd('/') + "?wait=true", content);
            if (resp.IsSuccessStatusCode)
            {
                var data = JObject.Parse(await resp.Content.ReadAsStringAsync());
                return new() { Success = true, MessageId = data["id"]?.ToString() };
            }
            return new() { Error = $"HTTP {(int)resp.StatusCode}" };
        }
        catch (Exception ex) { return new() { Error = ex.Message }; }
    }

    public async Task<bool> DeleteAsync(string url, string msgId)
    {
        try
        {
            var resp = await _http.DeleteAsync($"{url.TrimEnd('/')}/messages/{msgId}");
            return resp.StatusCode == System.Net.HttpStatusCode.NoContent;
        }
        catch { return false; }
    }
}

// File Watcher - monitors folders for new media files
public class FileWatcherService : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly HashSet<string> _recent = new();
    private readonly object _lock = new();

    public static readonly HashSet<string> ImgExt = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };
    public static readonly HashSet<string> VidExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".avi", ".mkv", ".webm", ".flv", ".wmv" };

    public event EventHandler<FileArg>? NewFile;

    public class FileArg : EventArgs
    {
        public string FilePath { get; set; } = "";
        public string FileName { get; set; } = "";
        public string FileType { get; set; } = "";
        public double SizeMB { get; set; }
    }

    public void Start(IEnumerable<string> folders)
    {
        Stop();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            var w = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime
            };
            w.Created += (s, e) => Handle(e.FullPath);
            w.Renamed += (s, e) => Handle(e.FullPath);
            _watchers.Add(w);
        }
    }

    public void Stop()
    {
        foreach (var w in _watchers) { w.EnableRaisingEvents = false; w.Dispose(); }
        _watchers.Clear();
        lock (_lock) _recent.Clear();
    }

    private void Handle(string p)
    {
        var ext = Path.GetExtension(p);
        bool img = ImgExt.Contains(ext), vid = VidExt.Contains(ext);
        if (!img && !vid) return;

        lock (_lock)
        {
            if (_recent.Contains(p)) return;
            _recent.Add(p);
            if (_recent.Count > 200) { _recent.Clear(); _recent.Add(p); }
        }

        Task.Run(async () =>
        {
            await Task.Delay(1500);
            if (!await WaitReady(p, vid ? 120 : 10)) return;
            try
            {
                var info = new FileInfo(p);
                var mb = info.Length / 1048576.0;
                if (mb > 25) return;
                NewFile?.Invoke(this, new()
                {
                    FilePath = p,
                    FileName = info.Name,
                    FileType = img ? "image" : "video",
                    SizeMB = mb
                });
            }
            catch { }
        });
    }

    private static async Task<bool> WaitReady(string path, int seconds)
    {
        long lastSize = -1;
        int stable = 0;
        for (int i = 0; i < seconds; i++)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return false;
                if (fi.Length == lastSize && fi.Length > 0)
                {
                    stable++;
                    if (stable >= 3)
                    {
                        try { using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read); return true; }
                        catch { stable = 0; }
                    }
                }
                else stable = 0;
                lastSize = fi.Length;
            }
            catch { return false; }
            await Task.Delay(1000);
        }
        return false;
    }

    public void Dispose() => Stop();
}

// App Settings - persisted to JSON in %AppData%
public class AppSettings
{
    public string BotName { get; set; } = "VRCNext";
    public string BotAvatarUrl { get; set; } = "";
    public List<WebhookSlot> Webhooks { get; set; } = new()
    {
        new() { Name = "Channel 1" },
        new() { Name = "Channel 2" },
        new() { Name = "Channel 3" },
        new() { Name = "Channel 4" },
    };
    public List<string> WatchFolders { get; set; } = new();
    public List<string> Favorites { get; set; } = new();
    public string VrcPath { get; set; } = "";
    public List<string> ExtraExe { get; set; } = new();
    public bool AutoStart { get; set; }
    public bool PostAll { get; set; }
    public int SelectedChannel { get; set; }
    public bool Notifications { get; set; } = true;
    public bool NotifySound { get; set; }
    public bool MinimizeToTray { get; set; }
    public string Theme { get; set; } = "midnight";
    public string DashBgPath { get; set; } = "";
    public int DashOpacity { get; set; } = 40;
    public bool RandomDashBg { get; set; } = false;
    public string VrcUsername { get; set; } = "";
    public string VrcPassword { get; set; } = ""; // stored locally only
    public string VrcAuthCookie { get; set; } = ""; // session cookie
    public string VrcTwoFactorCookie { get; set; } = ""; // 2FA cookie

    // Custom Chatbox settings
    public bool CbShowTime { get; set; } = true;
    public bool CbShowMedia { get; set; } = true;
    public bool CbShowPlaytime { get; set; } = true;
    public bool CbShowCustomText { get; set; } = true;
    public bool CbShowSystemStats { get; set; }
    public bool CbShowAfk { get; set; }
    public string CbAfkMessage { get; set; } = "Currently AFK";
    public bool CbSuppressSound { get; set; } = true;
    public string CbTimeFormat { get; set; } = "hh:mm tt";
    public string CbSeparator { get; set; } = " | ";
    public int CbIntervalMs { get; set; } = 5000;
    public List<string> CbCustomLines { get; set; } = new();

    // Space Flight settings
    public float SfMultiplier { get; set; } = 1f;
    public bool SfLockX { get; set; }
    public bool SfLockY { get; set; }
    public bool SfLockZ { get; set; }
    public bool SfLeftHand { get; set; }
    public bool SfRightHand { get; set; } = true;
    public bool SfUseGrip { get; set; } = true;

    // Auto-start flags
    public bool ChatboxAutoStart { get; set; }
    public bool SfAutoStart { get; set; }

    public bool SetupComplete { get; set; }

    public class WebhookSlot
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public bool Enabled { get; set; }
    }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "settings.json");

    [JsonIgnore] public static string? LastLoadError { get; private set; }
    [JsonIgnore] public static string? LoadDebugInfo { get; private set; }
    [JsonIgnore] public string? LastSaveError { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonConvert.DeserializeObject<AppSettings>(json,
                    new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace }) ?? new();
                // Ensure exactly 4 webhook slots
                if (s.Webhooks == null) s.Webhooks = new();
                if (s.Webhooks.Count > 4) s.Webhooks = s.Webhooks.Take(4).ToList();
                while (s.Webhooks.Count < 4) s.Webhooks.Add(new() { Name = $"Channel {s.Webhooks.Count + 1}" });
                return s;
            }
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
        catch { }
    }
}

/// <summary>
/// Tracks time spent with users (same instance) and last-seen timestamps.
/// Persisted in the shared timeline.db (SQLite), user_tracking table.
/// Automatically migrates from legacy user_tracking.json on first run.
/// </summary>
public class UserTimeTracker : IDisposable
{
    public class UserRecord
    {
        public long TotalSeconds { get; set; }
        public string LastSeen { get; set; } = "";
        public string LastSeenLocation { get; set; } = "";
    }

    // In-memory cache, same access pattern as before
    public Dictionary<string, UserRecord> Users { get; } = new();

    private readonly SqliteConnection _db;
    private bool _disposed;
    private string _myCurrentLocation = "";

    private static readonly string LegacyFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "user_tracking.json");

    private UserTimeTracker(SqliteConnection db) { _db = db; }

    public static UserTimeTracker Load()
    {
        var conn = Database.OpenConnection();
        var tracker = new UserTimeTracker(conn);
        tracker.InitSchema();
        tracker.MigrateFromJson();
        tracker.LoadFromDb();
        return tracker;
    }

    private void InitSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS user_tracking (
                user_id            TEXT PRIMARY KEY,
                total_seconds      INTEGER NOT NULL DEFAULT 0,
                last_seen          TEXT    NOT NULL DEFAULT '',
                last_seen_location TEXT    NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_ut_lastseen ON user_tracking(last_seen DESC);
        ";
        cmd.ExecuteNonQuery();
    }

    private void MigrateFromJson()
    {
        if (!File.Exists(LegacyFilePath)) return;
        try
        {
            var json = File.ReadAllText(LegacyFilePath);
            var legacy = JsonConvert.DeserializeObject<UserTimeTracker_Legacy>(json);
            if (legacy?.Users == null) { File.Delete(LegacyFilePath); return; }

            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT OR IGNORE INTO user_tracking
                (user_id,total_seconds,last_seen,last_seen_location)
                VALUES($uid,$ts,$ls,$lsl)";
            var pUid = cmd.Parameters.Add("$uid", SqliteType.Text);
            var pTs  = cmd.Parameters.Add("$ts",  SqliteType.Integer);
            var pLs  = cmd.Parameters.Add("$ls",  SqliteType.Text);
            var pLsl = cmd.Parameters.Add("$lsl", SqliteType.Text);
            foreach (var (userId, rec) in legacy.Users)
            {
                pUid.Value = userId;
                pTs.Value  = rec.TotalSeconds;
                pLs.Value  = rec.LastSeen ?? "";
                pLsl.Value = rec.LastSeenLocation ?? "";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            File.Delete(LegacyFilePath);
        }
        catch { }
    }

    private void LoadFromDb()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT user_id,total_seconds,last_seen,last_seen_location FROM user_tracking";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            Users[r.GetString(0)] = new UserRecord
            {
                TotalSeconds     = r.GetInt64(1),
                LastSeen         = r.GetString(2),
                LastSeenLocation = r.GetString(3),
            };
    }

    public void SetMyLocation(string location) => _myCurrentLocation = location ?? "";

    /// <summary>
    /// Called every poll tick. Updates in-memory cache and persists changed records to SQLite.
    /// </summary>
    public void Tick(IEnumerable<(string userId, string location, string presence)> onlineFriends, int elapsedSeconds = 45)
    {
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
                rec.LastSeen = DateTime.UtcNow.ToString("o");
                if (!string.IsNullOrEmpty(location) && location != "offline" && location != "private")
                    rec.LastSeenLocation = location;
            }

            if (!string.IsNullOrEmpty(_myCurrentLocation)
                && _myCurrentLocation != "offline"
                && _myCurrentLocation != "private"
                && _myCurrentLocation != "traveling"
                && location == _myCurrentLocation)
            {
                rec.TotalSeconds += elapsedSeconds;
            }

            changed.Add((userId, rec));
        }

        if (changed.Count == 0) return;
        try
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO user_tracking(user_id,total_seconds,last_seen,last_seen_location)
                VALUES($uid,$ts,$ls,$lsl)
                ON CONFLICT(user_id) DO UPDATE SET
                    total_seconds=excluded.total_seconds,
                    last_seen=excluded.last_seen,
                    last_seen_location=excluded.last_seen_location";
            var pUid = cmd.Parameters.Add("$uid", SqliteType.Text);
            var pTs  = cmd.Parameters.Add("$ts",  SqliteType.Integer);
            var pLs  = cmd.Parameters.Add("$ls",  SqliteType.Text);
            var pLsl = cmd.Parameters.Add("$lsl", SqliteType.Text);
            foreach (var (userId, rec) in changed)
            {
                pUid.Value = userId;
                pTs.Value  = rec.TotalSeconds;
                pLs.Value  = rec.LastSeen;
                pLsl.Value = rec.LastSeenLocation;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch { }
    }

    public (long totalSeconds, string lastSeen) GetUserStats(string userId)
    {
        if (Users.TryGetValue(userId, out var rec))
            return (rec.TotalSeconds, rec.LastSeen);
        return (0, "");
    }

    /// <summary>No-op. Writes happen in Tick(). Kept for API compatibility.</summary>
    public void Save() { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _db.Close(); } catch { }
        _db.Dispose();
    }

    private class UserTimeTracker_Legacy
    {
        public Dictionary<string, UserRecord>? Users { get; set; }
    }
}

/// <summary>
/// Tracks total time spent in each VRChat world.
/// Persisted in the shared timeline.db (SQLite), world_tracking table.
/// Automatically migrates from legacy world_tracking.json on first run.
/// </summary>
public class WorldTimeTracker : IDisposable
{
    public class WorldRecord
    {
        public long TotalSeconds { get; set; }
        public string LastVisited { get; set; } = "";
        public int VisitCount { get; set; }
    }

    // In-memory cache, same access pattern as before
    public Dictionary<string, WorldRecord> Worlds { get; } = new();

    private readonly SqliteConnection _db;
    private bool _disposed;
    private string _currentWorldId = "";
    private DateTime _lastTick = DateTime.UtcNow;

    private static readonly string LegacyFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "world_tracking.json");

    private WorldTimeTracker(SqliteConnection db) { _db = db; }

    public static WorldTimeTracker Load()
    {
        var conn = Database.OpenConnection();
        var tracker = new WorldTimeTracker(conn);
        tracker.InitSchema();
        tracker.MigrateFromJson();
        tracker.LoadFromDb();
        return tracker;
    }

    private void InitSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS world_tracking (
                world_id      TEXT PRIMARY KEY,
                total_seconds INTEGER NOT NULL DEFAULT 0,
                visit_count   INTEGER NOT NULL DEFAULT 0,
                last_visited  TEXT    NOT NULL DEFAULT ''
            );
        ";
        cmd.ExecuteNonQuery();
    }

    private void MigrateFromJson()
    {
        if (!File.Exists(LegacyFilePath)) return;
        try
        {
            var json = File.ReadAllText(LegacyFilePath);
            var legacy = JsonConvert.DeserializeObject<WorldTimeTracker_Legacy>(json);
            if (legacy?.Worlds == null) { File.Delete(LegacyFilePath); return; }

            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT OR IGNORE INTO world_tracking
                (world_id,total_seconds,visit_count,last_visited)
                VALUES($wid,$ts,$vc,$lv)";
            var pWid = cmd.Parameters.Add("$wid", SqliteType.Text);
            var pTs  = cmd.Parameters.Add("$ts",  SqliteType.Integer);
            var pVc  = cmd.Parameters.Add("$vc",  SqliteType.Integer);
            var pLv  = cmd.Parameters.Add("$lv",  SqliteType.Text);
            foreach (var (worldId, rec) in legacy.Worlds)
            {
                pWid.Value = worldId;
                pTs.Value  = rec.TotalSeconds;
                pVc.Value  = rec.VisitCount;
                pLv.Value  = rec.LastVisited ?? "";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            File.Delete(LegacyFilePath);
        }
        catch { }
    }

    private void LoadFromDb()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT world_id,total_seconds,visit_count,last_visited FROM world_tracking";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            Worlds[r.GetString(0)] = new WorldRecord
            {
                TotalSeconds = r.GetInt64(1),
                VisitCount   = r.GetInt32(2),
                LastVisited  = r.GetString(3),
            };
    }

    public void SetCurrentWorld(string worldId)
    {
        FlushCurrentWorld();
        _currentWorldId = worldId ?? "";
        _lastTick = DateTime.UtcNow;

        if (string.IsNullOrEmpty(_currentWorldId) || !_currentWorldId.StartsWith("wrld_")) return;

        if (!Worlds.TryGetValue(_currentWorldId, out var rec))
        {
            rec = new WorldRecord();
            Worlds[_currentWorldId] = rec;
        }
        rec.VisitCount++;
        rec.LastVisited = DateTime.UtcNow.ToString("o");
        UpsertWorld(_currentWorldId, rec);
    }

    /// <summary>
    /// Resume tracking a world after app restart. Does NOT increment visit count.
    /// </summary>
    public void ResumeWorld(string worldId)
    {
        _currentWorldId = worldId ?? "";
        _lastTick = DateTime.UtcNow;
    }

    public void Tick(int elapsedSeconds = 45)
    {
        if (string.IsNullOrEmpty(_currentWorldId) || !_currentWorldId.StartsWith("wrld_"))
            return;
        if (!Worlds.TryGetValue(_currentWorldId, out var rec))
        {
            rec = new WorldRecord();
            Worlds[_currentWorldId] = rec;
        }
        rec.TotalSeconds += elapsedSeconds;
        rec.LastVisited = DateTime.UtcNow.ToString("o");
        _lastTick = DateTime.UtcNow;
        UpsertWorld(_currentWorldId, rec);
    }

    private void FlushCurrentWorld()
    {
        if (string.IsNullOrEmpty(_currentWorldId) || !_currentWorldId.StartsWith("wrld_"))
            return;
        var elapsed = (int)(DateTime.UtcNow - _lastTick).TotalSeconds;
        if (elapsed > 0 && elapsed < 120 && Worlds.TryGetValue(_currentWorldId, out var rec))
        {
            rec.TotalSeconds += elapsed;
            rec.LastVisited = DateTime.UtcNow.ToString("o");
            UpsertWorld(_currentWorldId, rec);
        }
    }

    private void UpsertWorld(string worldId, WorldRecord rec)
    {
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"INSERT INTO world_tracking(world_id,total_seconds,visit_count,last_visited)
                VALUES($wid,$ts,$vc,$lv)
                ON CONFLICT(world_id) DO UPDATE SET
                    total_seconds=excluded.total_seconds,
                    visit_count=excluded.visit_count,
                    last_visited=excluded.last_visited";
            cmd.Parameters.AddWithValue("$wid", worldId);
            cmd.Parameters.AddWithValue("$ts",  rec.TotalSeconds);
            cmd.Parameters.AddWithValue("$vc",  rec.VisitCount);
            cmd.Parameters.AddWithValue("$lv",  rec.LastVisited);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    public (long totalSeconds, int visitCount, string lastVisited) GetWorldStats(string worldId)
    {
        if (Worlds.TryGetValue(worldId, out var rec))
            return (rec.TotalSeconds, rec.VisitCount, rec.LastVisited);
        return (0, 0, "");
    }

    /// <summary>No-op. Writes happen in SetCurrentWorld/Tick. Kept for API compatibility.</summary>
    public void Save() { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FlushCurrentWorld();
        try { _db.Close(); } catch { }
        _db.Dispose();
    }

    private class WorldTimeTracker_Legacy
    {
        public Dictionary<string, WorldRecord>? Worlds { get; set; }
    }

    /// <summary>
    /// Extract world ID from a VRChat PNG file's tEXt metadata chunks.
    /// VRChat stores world info in various formats: direct tEXt keys,
    /// JSON in Description, etc.
    /// </summary>
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

                    // Any chunk containing wrld_: extract the world ID
                    if (text.Contains("wrld_"))
                    {
                        var idx = text.IndexOf("wrld_");
                        // World IDs are alphanumeric + hyphens + underscores
                        var end = idx;
                        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_' || text[end] == '-'))
                            end++;
                        var worldId = text.Substring(idx, end - idx);
                        if (worldId.Length > 10) return worldId;
                    }

                    fs.Seek(4, SeekOrigin.Current); // CRC
                    continue;
                }

                // Skip chunk data + CRC
                fs.Seek(chunkLen + 4, SeekOrigin.Current);
            }
        }
        catch { }
        return null;
    }
}

/// <summary>
/// Stores which players were in the instance when a photo was taken.
/// Persisted to the shared timeline.db (SQLite), photo_records + photo_record_players tables.
/// Automatically migrates from the legacy photo_players.json on first run.
/// </summary>
public class PhotoPlayersStore : IDisposable
{
    public class PhotoPlayerInfo
    {
        public string UserId      { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Image       { get; set; } = "";
    }

    public class PhotoRecord
    {
        public List<PhotoPlayerInfo> Players { get; set; } = new();
        public string WorldId { get; set; } = "";
    }

    // In-memory cache, same access pattern as before
    public Dictionary<string, PhotoRecord> Photos { get; } = new();

    private readonly SqliteConnection _db;
    private bool _disposed;

    private static readonly string LegacyFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "photo_players.json");

    private PhotoPlayersStore(SqliteConnection db) { _db = db; }

    public static PhotoPlayersStore Load()
    {
        var conn = Database.OpenConnection();
        var store = new PhotoPlayersStore(conn);
        store.InitSchema();
        store.MigrateFromJson();
        store.LoadFromDb();
        return store;
    }

    private void InitSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS photo_records (
                file_name TEXT PRIMARY KEY,
                world_id  TEXT DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS photo_record_players (
                file_name    TEXT NOT NULL,
                user_id      TEXT DEFAULT '',
                display_name TEXT DEFAULT '',
                image        TEXT DEFAULT '',
                PRIMARY KEY (file_name, user_id)
            );
        ";
        cmd.ExecuteNonQuery();
    }

    private void MigrateFromJson()
    {
        if (!File.Exists(LegacyFilePath)) return;
        try
        {
            var json = File.ReadAllText(LegacyFilePath);
            // Legacy format: { "Photos": { "fileName": { "WorldId": "", "Players": [...] } } }
            var legacy = JsonConvert.DeserializeObject<PhotoPlayersStore_Legacy>(json);
            if (legacy?.Photos == null) { File.Delete(LegacyFilePath); return; }

            using var tx = _db.BeginTransaction();
            using var recCmd = _db.CreateCommand();
            recCmd.Transaction = tx;
            recCmd.CommandText = "INSERT OR IGNORE INTO photo_records(file_name,world_id) VALUES($fn,$wid)";
            var pfn  = recCmd.Parameters.Add("$fn",  SqliteType.Text);
            var pwid = recCmd.Parameters.Add("$wid", SqliteType.Text);

            using var plCmd = _db.CreateCommand();
            plCmd.Transaction = tx;
            plCmd.CommandText = @"INSERT OR IGNORE INTO photo_record_players
                (file_name,user_id,display_name,image) VALUES($fn,$uid,$dn,$img)";
            var ppfn  = plCmd.Parameters.Add("$fn",  SqliteType.Text);
            var ppuid = plCmd.Parameters.Add("$uid", SqliteType.Text);
            var ppdn  = plCmd.Parameters.Add("$dn",  SqliteType.Text);
            var ppimg = plCmd.Parameters.Add("$img", SqliteType.Text);

            foreach (var (fileName, rec) in legacy.Photos)
            {
                pfn.Value  = fileName;
                pwid.Value = rec.WorldId ?? "";
                recCmd.ExecuteNonQuery();

                ppfn.Value = fileName;
                foreach (var p in rec.Players ?? new())
                {
                    ppuid.Value = p.UserId ?? "";
                    ppdn.Value  = p.DisplayName ?? "";
                    ppimg.Value = p.Image ?? "";
                    plCmd.ExecuteNonQuery();
                }
            }
            tx.Commit();
            File.Delete(LegacyFilePath);
        }
        catch { }
    }

    private void LoadFromDb()
    {
        var playerMap = new Dictionary<string, List<PhotoPlayerInfo>>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT file_name,user_id,display_name,image FROM photo_record_players";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var fn = r.GetString(0);
                if (!playerMap.TryGetValue(fn, out var list))
                    playerMap[fn] = list = new();
                list.Add(new PhotoPlayerInfo { UserId = r.GetString(1), DisplayName = r.GetString(2), Image = r.GetString(3) });
            }
        }
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT file_name,world_id FROM photo_records";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var fn = r.GetString(0);
                Photos[fn] = new PhotoRecord
                {
                    WorldId = r.GetString(1),
                    Players = playerMap.TryGetValue(fn, out var pl) ? pl : new(),
                };
            }
        }
    }

    // Public API

    public void RecordPhoto(string fileName, IEnumerable<(string userId, string displayName, string image)> players, string worldId)
    {
        var rec = new PhotoRecord
        {
            WorldId = worldId,
            Players = players.Select(p => new PhotoPlayerInfo { UserId = p.userId, DisplayName = p.displayName, Image = p.image }).ToList()
        };
        Photos[fileName] = rec;

        try
        {
            using var tx = _db.BeginTransaction();

            using var recCmd = _db.CreateCommand();
            recCmd.Transaction = tx;
            recCmd.CommandText = "INSERT OR REPLACE INTO photo_records(file_name,world_id) VALUES($fn,$wid)";
            recCmd.Parameters.AddWithValue("$fn",  fileName);
            recCmd.Parameters.AddWithValue("$wid", worldId);
            recCmd.ExecuteNonQuery();

            using var delCmd = _db.CreateCommand();
            delCmd.Transaction = tx;
            delCmd.CommandText = "DELETE FROM photo_record_players WHERE file_name=$fn";
            delCmd.Parameters.AddWithValue("$fn", fileName);
            delCmd.ExecuteNonQuery();

            using var plCmd = _db.CreateCommand();
            plCmd.Transaction = tx;
            plCmd.CommandText = @"INSERT INTO photo_record_players
                (file_name,user_id,display_name,image) VALUES($fn,$uid,$dn,$img)";
            var pfn  = plCmd.Parameters.Add("$fn",  SqliteType.Text);
            var puid = plCmd.Parameters.Add("$uid", SqliteType.Text);
            var pdn  = plCmd.Parameters.Add("$dn",  SqliteType.Text);
            var pimg = plCmd.Parameters.Add("$img", SqliteType.Text);
            pfn.Value = fileName;
            foreach (var p in rec.Players)
            {
                puid.Value = p.UserId;
                pdn.Value  = p.DisplayName;
                pimg.Value = p.Image;
                plCmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch { }
    }

    public PhotoRecord? GetPhotoRecord(string fileName)
        => Photos.TryGetValue(fileName, out var rec) ? rec : null;

    /// <summary>No-op. Writes are immediate in SQLite. Kept for API compatibility.</summary>
    public void Save() { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _db.Close(); } catch { }
        _db.Dispose();
    }

    // Used only during JSON migration
    private class PhotoPlayersStore_Legacy
    {
        public Dictionary<string, PhotoRecord>? Photos { get; set; }
    }
}
