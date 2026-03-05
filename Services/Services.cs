using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;

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

    // Encrypted on disk via DPAPI — use VrcPassword/VrcAuthCookie/VrcTwoFactorCookie at runtime
    public string VrcPasswordEnc { get; set; } = "";
    public string VrcAuthCookieEnc { get; set; } = "";
    public string VrcTwoFactorCookieEnc { get; set; } = "";

    [JsonIgnore] public string VrcPassword { get; set; } = "";
    [JsonIgnore] public string VrcAuthCookie { get; set; } = "";
    [JsonIgnore] public string VrcTwoFactorCookie { get; set; } = "";

    private static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        try
        {
            var enc = ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(enc);
        }
        catch { return ""; }
    }

    private static string Unprotect(string cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        try
        {
            var dec = ProtectedData.Unprotect(
                Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(dec);
        }
        catch { return ""; }
    }

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

    // Image cache settings
    public bool ImgCacheEnabled { get; set; } = true;
    public int ImgCacheLimitGb { get; set; } = 5;

    // Fast Fetch Cache
    public bool FfcEnabled { get; set; } = true;

    public bool SetupComplete { get; set; }

    public List<string> InviteMessages { get; set; } = new()
    {
        "Come join us!",
        "We're here, join!",
        "You should check this out!",
        "Join me?"
    };

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
                // Decrypt credentials
                s.VrcPassword        = Unprotect(s.VrcPasswordEnc);
                s.VrcAuthCookie      = Unprotect(s.VrcAuthCookieEnc);
                s.VrcTwoFactorCookie = Unprotect(s.VrcTwoFactorCookieEnc);
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
            // Encrypt credentials before writing to disk
            VrcPasswordEnc        = Protect(VrcPassword);
            VrcAuthCookieEnc      = Protect(VrcAuthCookie);
            VrcTwoFactorCookieEnc = Protect(VrcTwoFactorCookie);
            var dir = Path.GetDirectoryName(FilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
        catch { }
    }
}

// Voice Fight settings - persisted separately from main settings
public class VoiceFightSettings
{
    public int InputDeviceIndex { get; set; }
    public string StopWord { get; set; } = "";
    public bool MuteTalk { get; set; } = false;
    public List<VfSoundItem> Items { get; set; } = new();

    public class VfSoundItem
    {
        public string Word { get; set; } = "";
        public List<VfSoundFile> Files { get; set; } = new();

        // Legacy single-file fields from pre-v2 saves; migrated to Files on Load.
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? FilePath { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public float? VolumePercent { get; set; }

        public class VfSoundFile
        {
            public string FilePath { get; set; } = "";
            public float VolumePercent { get; set; } = 100f;
        }
    }

    private static string SavePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "voicefight_settings.json");

    public static VoiceFightSettings Load()
    {
        try
        {
            if (File.Exists(SavePath))
            {
                var json = File.ReadAllText(SavePath);
                var settings = JsonConvert.DeserializeObject<VoiceFightSettings>(json) ?? new();

                // Migrate legacy single-file items
                bool migrated = false;
                foreach (var item in settings.Items)
                {
                    if (item.Files.Count == 0 && !string.IsNullOrWhiteSpace(item.FilePath))
                    {
                        item.Files.Add(new VfSoundItem.VfSoundFile
                        {
                            FilePath = item.FilePath,
                            VolumePercent = item.VolumePercent ?? 100f
                        });
                        item.FilePath = null;
                        item.VolumePercent = null;
                        migrated = true;
                    }
                }
                if (migrated) settings.Save();
                return settings;
            }
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SavePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(SavePath, JsonConvert.SerializeObject(this, Formatting.Indented));
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
        public string DisplayName { get; set; } = "";
        public string Image { get; set; } = "";
    }

    // In-memory cache, same access pattern as before
    public Dictionary<string, UserRecord> Users { get; } = new();
    public DateTime LastTick => _lastTick;

    private readonly SqliteConnection _db;
    private bool _disposed;
    private string _myCurrentLocation = "";
    private DateTime _lastTick = DateTime.UtcNow;
    private HashSet<string> _lastCoPresentIds = new();

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

        foreach (var col in new[] { "display_name TEXT NOT NULL DEFAULT ''", "image TEXT NOT NULL DEFAULT ''" })
        {
            try
            {
                using var ac = _db.CreateCommand();
                ac.CommandText = $"ALTER TABLE user_tracking ADD COLUMN {col}";
                ac.ExecuteNonQuery();
            }
            catch { }
        }
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

    /// <summary>Stores display name and image for a user so they appear in the Time Spent list
    /// even when they are not friends and not in the timeline top-200.</summary>
    public void UpdateUserInfo(string userId, string displayName, string image)
    {
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

    public void SetMyLocation(string location) => _myCurrentLocation = location ?? "";

    /// <summary>
    /// Called every poll tick. Updates in-memory cache and persists changed records to SQLite.
    /// </summary>
    public void Tick(IEnumerable<(string userId, string location, string presence)> onlineFriends)
    {
        var now = DateTime.UtcNow;
        var elapsed = (long)(now - _lastTick).TotalSeconds;
        _lastTick = now;

        var changed = new List<(string userId, UserRecord rec)>();
        var newCoPresentIds = new HashSet<string>();

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

            if (elapsed > 0 && elapsed <= 3600 // cap at 1h to handle sleep/pause
                && !string.IsNullOrEmpty(_myCurrentLocation)
                && _myCurrentLocation != "offline"
                && _myCurrentLocation != "private"
                && _myCurrentLocation != "traveling"
                && location == _myCurrentLocation)
            {
                rec.TotalSeconds += elapsed;
                newCoPresentIds.Add(userId);
            }

            changed.Add((userId, rec));
        }

        // Only update the co-present set when elapsed was valid.
        // If elapsed was 0 or >cap (e.g. rapid WS-triggered tick), the set would be empty
        // and the previous co-present state is still the correct one for flush-on-close.
        if (elapsed > 0 && elapsed <= 3600)
            _lastCoPresentIds = newCoPresentIds;

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
                pUid.Value = userId;
                pTs.Value  = rec.TotalSeconds;
                pLs.Value  = rec.LastSeen;
                pLsl.Value = rec.LastSeenLocation;
                pDn.Value  = rec.DisplayName;
                pImg.Value = rec.Image;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch { }
    }

    public (long totalSeconds, string lastSeen) GetUserStats(string userId, bool isCoPresent = false)
    {
        if (!Users.TryGetValue(userId, out var rec))
            return (0, "");

        var total = rec.TotalSeconds;

        // Add live pending time if currently co-present (time since last Tick not yet counted).
        // isCoPresent is determined by the log watcher (reliable), not the VRC API location
        // (which often returns "private" even for players in the same instance).
        if (isCoPresent)
        {
            var liveElapsed = (long)(DateTime.UtcNow - _lastTick).TotalSeconds;
            if (liveElapsed > 0 && liveElapsed <= 3600)
                total += liveElapsed;
        }

        return (total, rec.LastSeen);
    }

    /// <summary>No-op. Writes happen in Tick(). Kept for API compatibility.</summary>
    public void Save() { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FlushCoPresentUsers();
        try { _db.Close(); } catch { }
        _db.Dispose();
    }

    /// <summary>
    /// Saves pending elapsed time (since last Tick) for all co-present users.
    /// Called on app close so the current session is not lost.
    /// </summary>
    private void FlushCoPresentUsers()
    {
        if (_lastCoPresentIds.Count == 0) return;
        var elapsed = (long)(DateTime.UtcNow - _lastTick).TotalSeconds;
        if (elapsed <= 0 || elapsed > 3600) return;

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

            var now = DateTime.UtcNow.ToString("o");
            foreach (var userId in _lastCoPresentIds)
            {
                if (!Users.TryGetValue(userId, out var rec)) continue;
                rec.TotalSeconds += elapsed;
                pUid.Value = userId;
                pTs.Value  = rec.TotalSeconds;
                pLs.Value  = now;
                pLsl.Value = rec.LastSeenLocation;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            _lastTick = DateTime.UtcNow; // prevent double-flush
        }
        catch { }
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
        public string WorldName  { get; set; } = "";
        public string WorldThumb { get; set; } = "";
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
        // Create table (no-op if already exists)
        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"CREATE TABLE IF NOT EXISTS world_tracking (
            world_id      TEXT    PRIMARY KEY,
            total_seconds INTEGER NOT NULL DEFAULT 0,
            visit_count   INTEGER NOT NULL DEFAULT 0,
            last_visited  TEXT    NOT NULL DEFAULT '',
            world_name    TEXT    NOT NULL DEFAULT '',
            world_thumb   TEXT    NOT NULL DEFAULT ''
        )";
        cmd.ExecuteNonQuery();

        // Add new columns if upgrading from older schema (ALTER TABLE ADD COLUMN
        // throws if column already exists — that's fine, just ignore)
        foreach (var col in new[] { "world_name TEXT NOT NULL DEFAULT ''", "world_thumb TEXT NOT NULL DEFAULT ''" })
        {
            try
            {
                using var ac = _db.CreateCommand();
                ac.CommandText = $"ALTER TABLE world_tracking ADD COLUMN {col}";
                ac.ExecuteNonQuery();
            }
            catch { }
        }
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

    public void Tick()
    {
        if (string.IsNullOrEmpty(_currentWorldId) || !_currentWorldId.StartsWith("wrld_"))
            return;
        var now = DateTime.UtcNow;
        var elapsed = (long)(now - _lastTick).TotalSeconds;
        _lastTick = now;
        if (elapsed <= 0 || elapsed > 3600) return; // cap at 1h to handle sleep/pause
        if (!Worlds.TryGetValue(_currentWorldId, out var rec))
        {
            rec = new WorldRecord();
            Worlds[_currentWorldId] = rec;
        }
        rec.TotalSeconds += elapsed;
        rec.LastVisited = now.ToString("o");
        UpsertWorld(_currentWorldId, rec);
    }

    private void FlushCurrentWorld()
    {
        if (string.IsNullOrEmpty(_currentWorldId) || !_currentWorldId.StartsWith("wrld_"))
            return;
        var now = DateTime.UtcNow;
        var elapsed = (long)(now - _lastTick).TotalSeconds;
        _lastTick = now;
        if (elapsed <= 0 || elapsed > 3600) return; // same cap as Tick
        if (!Worlds.TryGetValue(_currentWorldId, out var rec)) return;
        rec.TotalSeconds += elapsed;
        rec.LastVisited = now.ToString("o");
        UpsertWorld(_currentWorldId, rec);
    }

    private void UpsertWorld(string worldId, WorldRecord rec)
    {
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"INSERT INTO world_tracking(world_id,total_seconds,visit_count,last_visited,world_name,world_thumb)
                VALUES($wid,$ts,$vc,$lv,$wn,$wt)
                ON CONFLICT(world_id) DO UPDATE SET
                    total_seconds=excluded.total_seconds,
                    visit_count=excluded.visit_count,
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

    /// <summary>Updates world name and thumbnail when resolved from the API.</summary>
    public void UpdateWorldInfo(string worldId, string name, string thumb)
    {
        if (string.IsNullOrEmpty(worldId) || string.IsNullOrEmpty(name)) return;
        if (!Worlds.TryGetValue(worldId, out var rec)) return;
        if (rec.WorldName == name && rec.WorldThumb == thumb) return; // no change
        rec.WorldName  = name;
        rec.WorldThumb = thumb;
        UpsertWorld(worldId, rec);
    }

    public (long totalSeconds, int visitCount, string lastVisited) GetWorldStats(string worldId)
    {
        if (!Worlds.TryGetValue(worldId, out var rec))
            return (0, 0, "");

        var total = rec.TotalSeconds;

        // Add live pending time if this is the current world (time since last Tick not yet counted)
        if (worldId == _currentWorldId)
        {
            var liveElapsed = (long)(DateTime.UtcNow - _lastTick).TotalSeconds;
            if (liveElapsed > 0 && liveElapsed <= 3600)
                total += liveElapsed;
        }

        return (total, rec.VisitCount, rec.LastVisited);
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
