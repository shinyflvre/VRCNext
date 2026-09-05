using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Security.Cryptography;

namespace VRCNext.Services;

// Webhook Service - posts files to Discord, deletes messages
public class WebhookService : IDisposable
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

    public async Task<PostResult> PostJsonAsync(string url, string json)
    {
        try
        {
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(url, content);
            if (resp.IsSuccessStatusCode) return new() { Success = true };
            return new() { Error = $"HTTP {(int)resp.StatusCode}" };
        }
        catch (Exception ex) { return new() { Error = ex.Message }; }
    }

    public async Task<PostResult> PostEmbedWithFilesAsync(string url, string json, IEnumerable<(string Path, string Name)> files)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(json, System.Text.Encoding.UTF8, "application/json"), "payload_json");
            int i = 0;
            foreach (var (path, name) in files)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                content.Add(new ByteArrayContent(await File.ReadAllBytesAsync(path)), $"files[{i}]", name);
                i++;
            }
            var resp = await _http.PostAsync(url, content);
            if (resp.IsSuccessStatusCode) return new() { Success = true };
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

    public void Dispose() => _http.Dispose();
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

// One VRChat account entry persisted in AppSettings.Accounts.
public class VrcAccount
{
    public string AccountId { get; set; } = ""; 
    public string UserId { get; set; } = "";     
    public string DisplayName { get; set; } = "";
    public string Username { get; set; } = "";
    public string AvatarImageUrl { get; set; } = "";
    public bool IsPrimary { get; set; } = false;
    public int ProfileIndex { get; set; } = 0;

    // Encrypted on disk via DPAPI, read decrypted values from Password/AuthCookie/TwoFactorCookie at runtime.
    public string PasswordEnc { get; set; } = "";
    public string AuthCookieEnc { get; set; } = "";
    public string TwoFactorCookieEnc { get; set; } = "";

    [JsonIgnore] public string Password { get; set; } = "";
    [JsonIgnore] public string AuthCookie { get; set; } = "";
    [JsonIgnore] public string TwoFactorCookie { get; set; } = "";
}

[JsonConverter(typeof(CbCustomLineConverter))]
public class CbCustomLine
{
    public string Text { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public class CbCustomLineConverter : JsonConverter<CbCustomLine>
{
    public override CbCustomLine ReadJson(JsonReader reader, Type objectType, CbCustomLine? existingValue,
        bool hasExistingValue, JsonSerializer serializer)
    {
        var token = JToken.Load(reader);
        if (token.Type == JTokenType.String)
            return new CbCustomLine { Text = token.ToString(), Enabled = true };
        if (token is JObject o)
            return new CbCustomLine
            {
                Text    = o["Text"]?.ToString() ?? o["text"]?.ToString() ?? "",
                Enabled = o["Enabled"]?.Value<bool>() ?? o["enabled"]?.Value<bool>() ?? true,
            };
        return new CbCustomLine();
    }

    public override void WriteJson(JsonWriter writer, CbCustomLine? value, JsonSerializer serializer)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("Text");
        writer.WriteValue(value?.Text ?? "");
        writer.WritePropertyName("Enabled");
        writer.WriteValue(value?.Enabled ?? true);
        writer.WriteEndObject();
    }
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
    public int LocalHttpPort { get; set; } = 0;
    public List<string> WatchFolders { get; set; } = new();
    public List<string>? RelayEnabledFolders { get; set; } = null; // null = all enabled (default)
    public List<string> MyInstances { get; set; } = new();
    public List<string> Favorites { get; set; } = new();
    public string VrcPath { get; set; } = "";
    public string VrcLaunchArgs { get; set; } = "";
    public List<string> ExtraExe { get; set; } = new(); // legacy — kept for JSON compat / migration
    public List<string> ExtraExeDesktop { get; set; } = new();
    public List<string> ExtraExeVR { get; set; } = new();
    public bool CloseWithVrc { get; set; } = false;
    public bool StartAlwaysWithVrc { get; set; } = true;
    public bool AutoStart { get; set; }
    public bool StartWithWindows { get; set; }
    public bool PostAll { get; set; }
    public int SelectedChannel { get; set; }
    public bool Notifications { get; set; } = true;
    public bool NotifySound { get; set; } // legacy — kept for JSON compat
    public bool NotifySoundEnabled { get; set; }
    public bool MessageSoundEnabled { get; set; }
    public bool MediaRelaySoundEnabled { get; set; }
    public bool SteamOverlaySoundEnabled { get; set; } = true;
    public string NotifySoundFile { get; set; } = "";
    public string MessageSoundFile { get; set; } = "";
    public string MediaRelaySoundFile { get; set; } = "";
    public string SteamOverlaySoundFile { get; set; } = "";
    public int NotifySoundVolume { get; set; } = 50;
    public int MessageSoundVolume { get; set; } = 50;
    public int MediaRelaySoundVolume { get; set; } = 50;
    public int SteamOverlaySoundVolume { get; set; } = 50;
    public bool FriendOnlineToastEnabled { get; set; }
    public bool FriendOnlineToastFavOnly { get; set; }
    public bool FriendsSidebarLocationOnly { get; set; } = true;
    public bool FriendsSidebarPreviewCollapsed { get; set; } = true;
    public bool FriendsSidebarPreviewOpen { get; set; } = false;
    public bool SeparateFavoriteFriends { get; set; } = false;
    public bool PeopleAlwaysStats { get; set; } = false;
    public bool ModernFolderLayout { get; set; } = true;
    public bool NavSidebarHoverText { get; set; } = true;
    public bool VrcPlusOptimizeEnabled { get; set; } = true;
    public bool EnableVrcPlusDecorations { get; set; } = false;
    public bool EnableProfileIconFrames { get; set; } = true;
    public bool SquareIconFrames { get; set; } = true;
    public bool EnableNameplateDecoration { get; set; } = true;
    public bool EnableProfileEffects { get; set; } = true;
    public bool EnableProfileBackgrounds { get; set; } = true;
    public bool EnableProfileThemes { get; set; } = true;
    public bool ProfileThemeContrast { get; set; } = true;
    public bool TransparentProfileCards { get; set; } = false;
    public bool ShowDecorationsOnDashboard { get; set; } = true;
    public bool? EnableProfileIconFramesOthers { get; set; }
    public bool? SquareIconFramesOthers { get; set; }
    public bool? EnableNameplateDecorationOthers { get; set; }
    public bool? EnableProfileEffectsOthers { get; set; }
    public bool? EnableProfileBackgroundsOthers { get; set; }
    public bool? EnableProfileThemesOthers { get; set; }
    public bool? ProfileThemeContrastOthers { get; set; }
    public bool? TransparentProfileCardsOthers { get; set; }
    public bool? ShowDecorationsOnDashboardOthers { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool TrayNotificationsEnabled { get; set; }
    public string Language { get; set; } = "en";
    public string LastChangelogVersion { get; set; } = "";
    public string Theme { get; set; } = "vrcn";
    public string SpecialTheme { get; set; } = "";
    public int AutoColorAccuracy { get; set; } = 50;
    public string PlayBtnTheme { get; set; } = "";
    public string CursorTheme { get; set; } = "";
    public string AppFont { get; set; } = "google-sans";
    public string CustomFont { get; set; } = "";
    public int FontSizeOffset { get; set; } = 0;
    public int TaskbarHeight { get; set; } = 42;
    public List<string> ActiveCustomThemes { get; set; } = ["VRCNext v2 Preview"];
    public int GuiZoom { get; set; } = 100;
    public string DashBgPath { get; set; } = "";
    public int DashOpacity { get; set; } = 40;
    public bool RandomDashBg { get; set; } = false;
    public bool ClockEnabled { get; set; } = false;
    public bool DateEnabled { get; set; } = false;
    public bool ShowVrcPlus { get; set; } = true;
    public bool ShowVrcCredits { get; set; } = true;
    public bool ShowApiHealth { get; set; } = true;
    // List of configured VRChat accounts and the currently active local AccountId (not UserId).
    public List<VrcAccount> Accounts { get; set; } = new();
    public string ActiveAccountId { get; set; } = "";

    // Legacy single-account fields migrated into Accounts on Load and cleared on Save.
    public string VrcUsername { get; set; } = "";
    public string VrcPasswordEnc { get; set; } = "";
    public string VrcAuthCookieEnc { get; set; } = "";
    public string VrcTwoFactorCookieEnc { get; set; } = "";

    [JsonIgnore] public string VrcPassword { get; set; } = "";
    [JsonIgnore] public string VrcAuthCookie { get; set; } = "";
    [JsonIgnore] public string VrcTwoFactorCookie { get; set; } = "";

    // Resolves the currently active account by ActiveAccountId, falling back to the primary or first entry.
    [JsonIgnore]
    public VrcAccount? ActiveAccount
    {
        get
        {
            if (Accounts.Count == 0) return null;
            if (!string.IsNullOrEmpty(ActiveAccountId))
            {
                var byId = Accounts.FirstOrDefault(a => a.AccountId == ActiveAccountId);
                if (byId != null) return byId;
            }
            return Accounts.FirstOrDefault(a => a.IsPrimary) ?? Accounts.FirstOrDefault();
        }
    }

    [JsonIgnore]
    public VrcAccount? PrimaryAccount => Accounts.FirstOrDefault(a => a.IsPrimary);

    // Ensures a primary account exists for fresh installs or corrupted settings and returns it.
    public VrcAccount EnsurePrimaryAccount()
    {
        var p = PrimaryAccount;
        if (p != null) return p;
        var primary = new VrcAccount
        {
            AccountId = Guid.NewGuid().ToString("N"),
            IsPrimary = true,
        };
        Accounts.Add(primary);
        if (string.IsNullOrEmpty(ActiveAccountId)) ActiveAccountId = primary.AccountId;
        return primary;
    }

    public int EnsureProfileIndex(VrcAccount acc)
    {
        if (acc.IsPrimary) return 0;
        if (acc.ProfileIndex > 0) return acc.ProfileIndex;
        var used = Accounts.Where(a => a.ProfileIndex > 0).Select(a => a.ProfileIndex).ToHashSet();
        var next = 1;
        while (used.Contains(next)) next++;
        acc.ProfileIndex = next;
        return next;
    }

    private static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        try
        {
#if WINDOWS
            var enc = ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(enc);
#else
            return VRCNext.Services.Helpers.SecretProtector.Protect(plain);
#endif
        }
        catch { return ""; }
    }

    private static string Unprotect(string cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        try
        {
#if WINDOWS
            var dec = ProtectedData.Unprotect(
                Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(dec);
#else
            return VRCNext.Services.Helpers.SecretProtector.Unprotect(cipher);
#endif
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
    public int CbAfkMouseSeconds { get; set; } = 10;
    public int CbAfkKeyboardSeconds { get; set; } = 10;
    public bool CbSuppressSound { get; set; } = true;
    public string CbTimeFormat { get; set; } = "hh:mm tt";
    public string CbSeparator { get; set; } = " | ";
    public string CbCustomTemplate { get; set; } = "";
    public int CbIntervalMs { get; set; } = 5000;
    public List<CbCustomLine> CbCustomLines { get; set; } = new();
    public bool CbHideBackground { get; set; } = false;
    public bool CbShowAfkTime { get; set; } = true;
    public List<string> CbLineOrder { get; set; } = new() { "time", "media", "stats", "pulse", "weather", "window", "custom" };
    public bool CbShowPulse { get; set; }
    public string CbHypeRateId { get; set; } = "";
    public bool CbAfHeartRate { get; set; }
    public string CbPulseFormat { get; set; } = "\U0001F49A {bpm} BPM";
    public bool CbShowWindow { get; set; }
    public string CbWindowFormat { get; set; } = "\U0001FA9F On desktop \"{app}\"";
    public bool CbShowWeather { get; set; }
    public string CbWeatherCity { get; set; } = "";
    public string CbWeatherUnit { get; set; } = "celsius";
    public string CbWeatherFormat { get; set; } = "{icon} {temp}";
    public bool CbStatCpu { get; set; } = true;
    public bool CbStatRam { get; set; } = true;
    public bool CbStatGpu { get; set; }
    public bool CbStatVram { get; set; }

    // Space Flight settings
    public float SfMultiplier { get; set; } = 1f;
    public bool  SfLockX { get; set; }
    public bool  SfLockY { get; set; }
    public bool  SfLockZ { get; set; }
    // Legacy fields kept for JSON compatibility — superseded by per-hand button assignments
    public bool  SfLeftHand   { get; set; }
    public bool  SfRightHand  { get; set; } = true;
    public bool  SfUseGrip    { get; set; } = true;
    // Per-hand button assignments. 0 = "None".
    // Defaults: Left Thumbstick = Reset, Right Thumbstick = Drag.
    public uint  SfLeftResetButton  { get; set; } = 32; // Axis0 / Thumbstick
    public uint  SfRightResetButton { get; set; } = 0;
    public uint  SfLeftDragButton   { get; set; } = 0;
    public uint  SfRightDragButton  { get; set; } = 32; // Axis0 / Thumbstick
    public uint  SfLeftGravityButton  { get; set; } = 0;
    public uint  SfRightGravityButton { get; set; } = 0;
    public float SfGravity { get; set; } = 9.8f;
    public uint  SfIdxLeftResetButton    { get; set; } = 0;
    public uint  SfIdxRightResetButton   { get; set; } = 0;
    public uint  SfIdxLeftDragButton     { get; set; } = 0;
    public uint  SfIdxRightDragButton    { get; set; } = 0;
    public uint  SfIdxLeftGravityButton  { get; set; } = 0;
    public uint  SfIdxRightGravityButton { get; set; } = 0;

    // Space Turn settings
    public float StMultiplier   { get; set; } = 1f;
    public float StSnapDegrees  { get; set; } = 0f;
    public bool  StInvert       { get; set; }
    public float StSmoothing    { get; set; } = 0f;
    public bool  StAutoStartVR  { get; set; }
    public uint  StLeftTurnButton     { get; set; } = 2; // Grip
    public uint  StRightTurnButton    { get; set; } = 0;
    public uint  StLeftResetButton    { get; set; } = 0;
    public uint  StRightResetButton   { get; set; } = 0;
    public uint  StIdxLeftTurnButton  { get; set; } = 0;
    public uint  StIdxRightTurnButton { get; set; } = 0;
    public uint  StIdxLeftResetButton { get; set; } = 0;
    public uint  StIdxRightResetButton{ get; set; } = 0;

    // FrameShot settings
    public uint   FsLeftButton       { get; set; } = 2;  // EVRButtonId.k_EButton_Grip
    public uint   FsRightButton      { get; set; } = 2;  // EVRButtonId.k_EButton_Grip
    public bool   FsAutoStartVR      { get; set; }
    public string FsOutputDevice     { get; set; } = "";
    public string FsOutputDeviceId   { get; set; } = "";
    public int    FsActivationRadius { get; set; } = 15; // cm, 5–30
    public uint   FsLeftRecordButton  { get; set; } = 0; // 0 = none
    public uint   FsRightRecordButton { get; set; } = 0; // 0 = none
    public int    FsGifMaxResolution  { get; set; } = 512;
    public int    FsGifMaxFps         { get; set; } = 10;
    public bool   FsUseHmdRotations   { get; set; } = false;
    public uint   FsLeftVideoButton   { get; set; } = 0;
    public uint   FsRightVideoButton  { get; set; } = 0;
    public uint   FsLeftAcceptButton  { get; set; } = 0;
    public uint   FsRightAcceptButton { get; set; } = 0;
    public string FsVideoDeviceA      { get; set; } = "";
    public string FsVideoDeviceB      { get; set; } = "";
    public int    FsVideoFps          { get; set; } = 30;
    public string FsVideoQuality      { get; set; } = "1080p";
    public string FsVideoBitrateQuality { get; set; } = "medium";
    public uint   FsIdxLeftButton        { get; set; } = 0;
    public uint   FsIdxRightButton       { get; set; } = 0;
    public uint   FsIdxLeftRecordButton  { get; set; } = 0;
    public uint   FsIdxRightRecordButton { get; set; } = 0;
    public uint   FsIdxLeftVideoButton   { get; set; } = 0;
    public uint   FsIdxRightVideoButton  { get; set; } = 0;
    public uint   FsIdxLeftAcceptButton  { get; set; } = 0;
    public uint   FsIdxRightAcceptButton { get; set; } = 0;
    public int    FsAudioKbps         { get; set; } = 256;

    // Auto-start flags (legacy — kept for JSON compat, no longer acted on)
    public bool ChatboxAutoStart { get; set; }
    public bool SfAutoStart { get; set; }
    public bool DiscordPresenceAutoStart { get; set; }
    public bool VroAutoStart { get; set; }

    // Auto-start split: VR vs Desktop (triggered when VRChat is launched from VRCNext)
    public bool ChatboxAutoStartVR       { get; set; }
    public bool ChatboxAutoStartDesktop  { get; set; }
    public bool SfAutoStartVR            { get; set; }
    public bool RelayAutoStartVR         { get; set; }
    public bool RelayAutoStartDesktop    { get; set; }
    public bool YtAutoStartVR            { get; set; }
    public bool YtAutoStartDesktop       { get; set; }
    public bool VfAutoStartVR            { get; set; }
    public bool VfAutoStartDesktop       { get; set; }
    public bool DpAutoStartVR            { get; set; }
    public bool DpAutoStartDesktop       { get; set; }
    public bool VroAutoStartVR           { get; set; }

    // VR Wrist Overlay settings
    public bool    VroAttachLeft  { get; set; } = true;
    public bool    VroAttachHand  { get; set; } = true;
    public float   VroPosX        { get; set; } = -0.10f;
    public float   VroPosY        { get; set; } = -0.03f;
    public float   VroPosZ        { get; set; } = 0.11f;
    public float   VroRotX        { get; set; } = -180f;
    public float   VroRotY        { get; set; } = 46f;
    public float   VroRotZ        { get; set; } = 85f;
    public float   VroWidth       { get; set; } = 0.16f;
    public List<uint> VroKeybind       { get; set; } = new();
    public int        VroKeybindHand   { get; set; } = 0; // 0=any, 1=left, 2=right
    public int        VroKeybindMode   { get; set; } = 0; // 0=combo(hold), 1=doubletap
    public List<uint> VroKeybindDt     { get; set; } = new();
    public int        VroKeybindDtHand { get; set; } = 0; // 0=any, 1=left, 2=right for doubletap slot
    public int        VroControlRadius { get; set; } = 16; // cm, 3–28; 16 = default
    public bool       VroDynVis        { get; set; } = false;
    public int        VroFocusRadius   { get; set; } = 35; // cm, 20–60; 35 = default
    public bool       VroSeamless      { get; set; } = false;
    public List<uint> VroIdxKeybind       { get; set; } = new();
    public int        VroIdxKeybindHand   { get; set; } = 0;
    public List<uint> VroIdxKeybindDt     { get; set; } = new();
    public int        VroIdxKeybindDtHand { get; set; } = 0;

    // 0 = legacy OpenVR input, 1 = SteamVR Input (Valve Index)
    public int        VrInputMode      { get; set; } = 0;

    // VR Toast Notifications (HMD-attached)
    public bool       VroToastEnabled      { get; set; } = true;
    public bool       VroToastFavOnly      { get; set; }
    public int        VroToastSize         { get; set; } = 50; // 0–100, default 50%
    public float      VroToastOffsetX      { get; set; } = 0f;
    public float      VroToastOffsetY      { get; set; } = -0.12f;
    public bool       VroToastOnline       { get; set; } = true;
    public bool       VroToastOffline      { get; set; } = true;
    public bool       VroToastWebOnline    { get; set; } = true;
    public bool       VroToastWebOffline   { get; set; } = true;
    public bool       VroToastGps          { get; set; } = true;
    public bool       VroToastStatus       { get; set; } = true;
    public bool       VroToastStatusDesc   { get; set; } = true;
    public bool       VroToastBio          { get; set; } = true;
    public int        VroToastDuration     { get; set; } = 8;   // seconds, 2–10
    public int        VroToastStack        { get; set; } = 2;   // 1–4, max simultaneous toasts
    public bool       VroToastFriendReq    { get; set; } = true;
    public bool       VroToastInvite       { get; set; } = true;
    public bool       VroToastGroupInv     { get; set; } = true;
    public bool       VroToastJoined       { get; set; } = true;
    public bool       VroToastLeft         { get; set; } = true;
    public bool       VroToastReqInvite    { get; set; } = true;
    public bool       VroToastTtsOnline     { get; set; } = false;
    public bool       VroToastTtsOffline    { get; set; } = false;
    public bool       VroToastTtsGps        { get; set; } = false;
    public bool       VroToastTtsStatus     { get; set; } = false;
    public bool       VroToastTtsStatusDesc { get; set; } = false;
    public bool       VroToastTtsBio        { get; set; } = false;
    public bool       VroToastTtsFriendReq  { get; set; } = false;
    public bool       VroToastTtsInvite     { get; set; } = false;
    public bool       VroToastTtsGroupInv   { get; set; } = false;
    public bool       VroToastTtsJoined     { get; set; } = false;
    public bool       VroToastTtsLeft       { get; set; } = false;
    public bool       VroToastTtsReqInvite  { get; set; } = false;
    public int        VroTtsDevice         { get; set; } = -1;
    public string     VroTtsDeviceName     { get; set; } = "";
    public string     VroTtsDeviceId       { get; set; } = "";
    public string     VroTtsVoice          { get; set; } = "";
    public string     VroTtsEngine         { get; set; } = "sapi";
    public string     VroTtsLang           { get; set; } = "";
    public string     VroTtsGender         { get; set; } = "";

    // VR Dashboard — Water Reminder
    public bool VroWaterEnabled { get; set; } = false;
    public int  VroWaterHours   { get; set; } = 1;
    public int  VroWaterMinutes { get; set; } = 0;

    // Avtrdb Support — report deleted avatars to help clean the database
    public bool AvtrdbReportDeleted { get; set; } = true;
    public bool AvtrdbSubmitAvatars { get; set; }

    // Avtr.icu Support
    public bool AvtrIcuReportDeleted { get; set; } = true;
    public bool AvtrIcuSubmitAvatars { get; set; }

    // VRCNDb (db.vrcnext.com) — default off
    public bool VrcndbSubmitAvatars { get; set; }
    public bool VrcndbReportDeleted { get; set; }
    public bool VrcndbConsentShown { get; set; }
    public bool VrcndbSyncLikes { get; set; } = true;
    public bool VrcndbSyncWears { get; set; } = true;
    public bool CommentsOnWorldsEnabled { get; set; } = true;

    // Discord Rich Presence — privacy per status
    public bool DpHideInstIdJoinMe  { get; set; }
    public bool DpHideInstIdOnline  { get; set; }
    public bool DpHideInstIdAskMe   { get; set; } = true;
    public bool DpHideInstIdBusy    { get; set; } = true;
    public bool DpHideLocJoinMe     { get; set; }
    public bool DpHideLocOnline     { get; set; }
    public bool DpHideLocAskMe      { get; set; } = true;
    public bool DpHideLocBusy       { get; set; } = true;
    public bool DpHidePlayersJoinMe { get; set; }
    public bool DpHidePlayersOnline { get; set; }
    public bool DpHidePlayersAskMe  { get; set; } = true;
    public bool DpHidePlayersBusy   { get; set; } = true;
    public bool DpHideJoinBtnJoinMe { get; set; }
    public bool DpHideJoinBtnOnline { get; set; }
    public bool DpHideJoinBtnAskMe  { get; set; } = true;
    public bool DpHideJoinBtnBusy   { get; set; } = true;

    // Image cache settings
    public int  ImgCacheLimitGb         { get; set; } = 5;
    public bool ImgCacheOptimizeEnabled { get; set; } = true;
    public bool ImgMemoryOptimizeEnabled { get; set; } = true;

    // Notification V2 endpoint support (set false if account gets 404, persists across sessions)
    public bool NotifV2Supported { get; set; } = true;

    // Fast Fetch Cache
    public bool FfcEnabled { get; set; } = true;

    // Memory Trim
    public bool MemoryTrimEnabled { get; set; } = true;

    // Instance prints — download prints other players drop in the instance
    public bool   SaveInstancePrints { get; set; } = false;
    public string InstancePrintsPath { get; set; } = "";

    // Instance stickers — download stickers other players spawn in the instance
    public bool   SaveInstanceStickers { get; set; } = false;
    public string InstanceStickersPath { get; set; } = "";

    // Windows Fixes
    public bool MediaFixEnabled { get; set; } = true;

    public bool MultiTaskMode { get; set; } = false;

    public bool TilingManager { get; set; } = true;

    // Database optimization — load limited entries into RAM at startup
    public bool DbOptimize           { get; set; } = true;
    public int  DbOptimizeMaxEntries { get; set; } = 500;

    // One-time migration: backfill first_meet_date + meet_again_count into user_tracking
    public bool UserTrackingCountsMigrated { get; set; } = false;

    // One-time migration: convert event_players.joined_at/left_at from single timestamp to JSON array of sessions
    public bool EventPlayerSessionsMigrated { get; set; } = false;

    public bool DuplicateFriendRemovedCleaned { get; set; } = false;

    public bool ExtraExeAutoStartMigrated { get; set; } = false;

    public int RewindShownYear { get; set; } = 0;

    // Auto-Update on startup
    public bool AutoUpdate { get; set; } = true;

    // Crash Reporting, send anonymous stack traces to the developer via Discord webhook
    public bool SendCrashData { get; set; } = false;
    // Restart after crash. We do ignore task manager kills here!
    public bool RestartAfterCrash { get; set; } = true;

    // Text Tools (debug — makes all text selectable)
    public bool TextToolsEnabled { get; set; } = false;

    // Window Behavior
    public bool RememberWindowSize     { get; set; } = false;
    public bool RememberWindowPosition { get; set; } = false;
    public int  SavedWindowWidth       { get; set; } = 1100;
    public int  SavedWindowHeight      { get; set; } = 700;
    public int  SavedWindowX           { get; set; } = -1;
    public int  SavedWindowY           { get; set; } = -1;
    public bool SavedWindowMaximized   { get; set; } = false;

    // Auto-Backups
    public bool     RegBackupEnabled    { get; set; } = true;
    public int      RegBackupDays       { get; set; } = 30;
    public bool     DbAutoBackupEnabled { get; set; } = true;
    public int      DbAutoBackupDays    { get; set; } = 60;
    public DateTime LastRegBackup       { get; set; } = DateTime.MinValue;
    public DateTime LastDbAutoBackup    { get; set; } = DateTime.MinValue;

    // Performance — WebView2/Chromium flags (all require restart)
    public bool GpuAcceleration     { get; set; } = true;
    public bool LinuxGpuAcceleration { get; set; } = false;
    public bool GpuShaderCache      { get; set; } = false;
    public bool V8Heap128           { get; set; } = false;
    public bool TwoRenderProcesses  { get; set; } = false;
    public bool EfficiencyMode      { get; set; } = false;
    public bool AnimationsEnabled   { get; set; } = true;
    public bool BlurEnabled         { get; set; } = true;
    public int  SearchDebounceMs    { get; set; } = 500;

    // Avatar Scaling
    public bool  AsAutoStartVR            { get; set; }
    public bool  AsAutoStartDesktop       { get; set; }
    public bool  AsUseSafetySettings      { get; set; }
    public float AsScale                  { get; set; } = 1.0f;
    public float AsScaleMin               { get; set; } = 0.5f;
    public float AsScaleMax               { get; set; } = 3.0f;
    public bool  AsSaveScaleBetweenWorlds { get; set; }
    public int   AsKeyUp                  { get; set; }
    public int   AsKeyDown                { get; set; }
    public float AsSmoothing              { get; set; } = 30f;

    // VR Overlay — Avatar Scale Tab
    public bool        VroScaleEnabled      { get; set; } = true;
    public bool        VroScaleLeftThumb    { get; set; } = false;
    public bool        VroScaleRightThumb   { get; set; } = true;
    public List<uint>  VroScaleKeybind           { get; set; } = new();
    public int         VroScaleKeybindHand       { get; set; } = 0;
    public List<uint>  VroIdxScaleKeybind        { get; set; } = new();
    public int         VroIdxScaleKeybindHand    { get; set; } = 0;
    public int         VroScaleScrollSensitivity { get; set; } = 25;

    // Dashboard layout customization
    public List<string>? DashSectionOrder  { get; set; } = null;
    public List<string>? DashSectionHidden { get; set; } = null;
    public List<string>? DashRows          { get; set; } = null;
    public List<string>? DashHero          { get; set; } = null;
    public int DashLayoutVersion           { get; set; } = 0;

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
    private static readonly string BackupPath = FilePath + ".bak";
    private static readonly object _saveLock = new();

    [JsonIgnore] public static string? LastLoadError { get; private set; }
    [JsonIgnore] public static string? LoadDebugInfo { get; private set; }
    [JsonIgnore] public string? LastSaveError { get; set; }

    private static AppSettings? TryReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonConvert.DeserializeObject<AppSettings>(json,
                new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace });
        }
        catch (Exception ex) { LastLoadError = ex.Message; return null; }
    }

    public static AppSettings Load()
    {
        try
        {
            var s = TryReadFile(FilePath) ?? TryReadFile(BackupPath);
            if (s != null)
            {
                // Ensure exactly 4 webhook slots
                if (s.Webhooks == null) s.Webhooks = new();
                if (s.Webhooks.Count > 4) s.Webhooks = s.Webhooks.Take(4).ToList();
                while (s.Webhooks.Count < 4) s.Webhooks.Add(new() { Name = $"Channel {s.Webhooks.Count + 1}" });

                // One-time reset so the new dashboard default layout applies to existing users.
                if (s.DashLayoutVersion < 2)
                {
                    s.DashSectionOrder  = null;
                    s.DashSectionHidden = null;
                    s.DashRows          = null;
                    s.DashHero          = null;
                    s.DashLayoutVersion = 2;
                }

                // One-time migration of legacy single-account fields into Accounts[0] as primary.
                if (s.Accounts == null) s.Accounts = new();
                if (s.Accounts.Count == 0 &&
                    (!string.IsNullOrEmpty(s.VrcUsername) ||
                     !string.IsNullOrEmpty(s.VrcPasswordEnc) ||
                     !string.IsNullOrEmpty(s.VrcAuthCookieEnc) ||
                     !string.IsNullOrEmpty(s.VrcTwoFactorCookieEnc)))
                {
                    var primary = new VrcAccount
                    {
                        AccountId          = Guid.NewGuid().ToString("N"),
                        UserId             = "", // Filled on the first successful resume or login.
                        DisplayName        = s.VrcUsername,
                        Username           = s.VrcUsername,
                        IsPrimary          = true,
                        PasswordEnc        = s.VrcPasswordEnc,
                        AuthCookieEnc      = s.VrcAuthCookieEnc,
                        TwoFactorCookieEnc = s.VrcTwoFactorCookieEnc,
                    };
                    s.Accounts.Add(primary);
                    s.ActiveAccountId = primary.AccountId;
                }

                // Per-account decrypt of stored credentials.
                foreach (var acc in s.Accounts)
                {
                    acc.Password        = Unprotect(acc.PasswordEnc);
                    acc.AuthCookie      = Unprotect(acc.AuthCookieEnc);
                    acc.TwoFactorCookie = Unprotect(acc.TwoFactorCookieEnc);
                }

                // Legacy decrypt kept for backwards-read but should be empty after migration.
                s.VrcPassword        = Unprotect(s.VrcPasswordEnc);
                s.VrcAuthCookie      = Unprotect(s.VrcAuthCookieEnc);
                s.VrcTwoFactorCookie = Unprotect(s.VrcTwoFactorCookieEnc);

                return s;
            }
        }
        catch { }
        return new() { DashLayoutVersion = 2 };
    }

    public void Save()
    {
        try
        {
            // Per-account encrypt of credentials before writing to disk.
            foreach (var acc in Accounts)
            {
                acc.PasswordEnc        = Protect(acc.Password);
                acc.AuthCookieEnc      = Protect(acc.AuthCookie);
                acc.TwoFactorCookieEnc = Protect(acc.TwoFactorCookie);
            }
            // Wipe legacy fields so they are removed from JSON after migration.
            VrcUsername           = "";
            VrcPasswordEnc        = "";
            VrcAuthCookieEnc      = "";
            VrcTwoFactorCookieEnc = "";
            VrcPassword           = "";
            VrcAuthCookie         = "";
            VrcTwoFactorCookie    = "";
            var dir = Path.GetDirectoryName(FilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            lock (_saveLock)
            {
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                VRCNext.Services.Helpers.SecretProtector.RestrictToOwner(tmp);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, BackupPath);
                else File.Move(tmp, FilePath);
            }
            LastSaveError = null;
        }
        catch (Exception ex) { LastSaveError = ex.Message; }
    }
}

// Voice Fight settings - persisted separately from main settings
public class VoiceFightSettings
{
    public int InputDeviceIndex { get; set; }
    public string InputDeviceName { get; set; } = "";
    public string InputDeviceId { get; set; } = "";
    public int OutputDeviceIndex { get; set; } = -1;
    public string OutputDeviceName { get; set; } = "";
    public string OutputDeviceId { get; set; } = "";
    public string StopWord { get; set; } = "";
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


// stores which players were in the instance when a photo was taken. persisted in SQLite.
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

    public void UpdateWorldId(string fileName, string worldId)
    {
        if (Photos.TryGetValue(fileName, out var rec))
            rec.WorldId = worldId;
        else
            Photos[fileName] = new PhotoRecord { WorldId = worldId };

        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO photo_records(file_name,world_id) VALUES($fn,$wid)";
            cmd.Parameters.AddWithValue("$fn",  fileName);
            cmd.Parameters.AddWithValue("$wid", worldId);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    public PhotoRecord? GetPhotoRecord(string fileName)
        => Photos.TryGetValue(fileName, out var rec) ? rec : null;

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