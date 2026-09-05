using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NativeFileDialogSharp;
using VRCNext.Services;
using VRCNext.Services.Helpers;
using System.Diagnostics;

namespace VRCNext;

public partial class AppShell
{
    // Persisted cache: contentId -> { name, rawImageUrl }
    private Dictionary<string, (string name, string rawImageUrl)>? _sharedContentCache;
    private readonly object _sharedContentCacheLock = new();

    // Lazy cache of own avatars — GET /api/1/avatars/{id} returns 403 for own private avatars.
    // Loaded once via GetOwnAvatarsAsync (uses releaseStatus=all).
    private Dictionary<string, (string name, string thumb)>? _ownAvatarCache;
    private readonly SemaphoreSlim _ownAvatarCacheLock = new(1, 1);
    private async Task EnsureOwnAvatarCacheAsync()
    {
        if (_ownAvatarCache != null) return;
        await _ownAvatarCacheLock.WaitAsync();
        try
        {
            if (_ownAvatarCache != null) return;
            _ownAvatarCache = new();
            var avatars = await _core.Avatars.GetOwnAvatarsAsync();
            foreach (var a in avatars)
            {
                var id    = a["id"]?.ToString() ?? "";
                var name2 = a["name"]?.ToString() ?? "";
                var thumb = a["thumbnailImageUrl"]?.ToString() ?? a["imageUrl"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(id))
                    _ownAvatarCache[id] = (name2, thumb);
            }
        }
        finally { _ownAvatarCacheLock.Release(); }
    }

    private Dictionary<string, (string name, string rawImageUrl)> GetSharedContentCache()
    {
        if (_sharedContentCache != null) return _sharedContentCache;
        lock (_sharedContentCacheLock)
        {
            if (_sharedContentCache != null) return _sharedContentCache;
            _sharedContentCache = new();
            if (_cache.LoadRaw(CacheHandler.KeySharedContent) is Newtonsoft.Json.Linq.JObject obj)
            {
                foreach (var prop in obj.Properties())
                    _sharedContentCache[prop.Name] = (
                        prop.Value["name"]?.ToString()        ?? "",
                        prop.Value["rawImageUrl"]?.ToString() ?? "");
            }
            return _sharedContentCache;
        }
    }

    // Called at startup to pre-populate JS _msgrContentCache so first chat open shows images instantly.
    public async Task PrefetchSharedContentAsync()
    {
        var scc = GetSharedContentCache();
        List<(string id, string name, string rawImageUrl)> entries;
        lock (_sharedContentCacheLock)
            entries = scc.Where(kv => !string.IsNullOrEmpty(kv.Value.rawImageUrl))
                         .Select(kv => (kv.Key, kv.Value.name, kv.Value.rawImageUrl))
                         .ToList();

        foreach (var (id, name, rawImageUrl) in entries)
        {
            var image = id.StartsWith("avtr_") ? ImageCacheHelper.GetAvatarUrl(id, rawImageUrl)
                      : id.StartsWith("wrld_") ? ImageCacheHelper.GetWorldUrl(id, rawImageUrl)
                      : id.StartsWith("usr_")  ? ImageCacheHelper.GetUserUrl(id, rawImageUrl)
                      : id.StartsWith("grp_")  ? ImageCacheHelper.GetGroupUrl(id, rawImageUrl)
                      : rawImageUrl;
            if (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(image))
                Invoke(() => SendToJS("vrcSharedContentInfo", new { contentId = id, name, image }));
        }
    }

    // Re-fetches the favorites list for a kind so local group changes (create/delete) refresh the UI.
    private void RefreshLocalFavKind(string kind)
    {
        if (kind == "avatar") _ = Task.Run(_authCtrl.FetchAndCacheFavAvatarsAsync);
        else if (kind == "friend") _ = Task.Run(_friends.FetchAndCacheFavFriendsAsync);
        else _ = Task.Run(_authCtrl.FetchAndCacheFavWorldsAsync);
    }

    private void SaveSharedContentCache()
    {
        var dict = _sharedContentCache;
        if (dict == null) return;
        lock (_sharedContentCacheLock)
        {
            var obj = new Newtonsoft.Json.Linq.JObject();
            foreach (var kv in dict)
                obj[kv.Key] = new Newtonsoft.Json.Linq.JObject {
                    ["name"]        = kv.Value.name,
                    ["rawImageUrl"] = kv.Value.rawImageUrl,
                };
            _cache.Save(CacheHandler.KeySharedContent, obj);
        }
    }

    private readonly HashSet<string> _checkedAvatarIds = new();
    private readonly HashSet<string> _deletedAvatarIds = new();
    private readonly HashSet<string> _reportedToAvtrdb = new();
    private readonly List<string> _avtrdbReportQueue = new();
    private System.Threading.Timer? _avtrdbReportTimer;
    private readonly List<string> _avtrdbSubmitQueue = new();
    private readonly HashSet<string> _avtrdbSubmittedIds = new();
    private System.Threading.Timer? _avtrdbSubmitTimer;

    private readonly HashSet<string> _reportedToAvtrIcu = new();
    private readonly List<string> _avtrIcuReportQueue = new();
    private System.Threading.Timer? _avtrIcuReportTimer;
    private readonly List<string> _avtrIcuSubmitQueue = new();
    private readonly HashSet<string> _avtrIcuSubmittedIds = new();
    private System.Threading.Timer? _avtrIcuSubmitTimer;

    private const string VrcndbBase = "https://db.vrcnext.com/api";
    private readonly List<string> _vrcndbSubmitQueue = new();
    private readonly HashSet<string> _vrcndbSubmittedIds = new();
    private System.Threading.Timer? _vrcndbSubmitTimer;
    private DateTime? _vrcndbFirstQueuedAt;
    private readonly System.Threading.SemaphoreSlim _vrcndbFlushGate = new(1, 1);
    private const int VrcndbBatchSize  = 40;
    private const int VrcndbQuietMs    = 30_000;
    private const int VrcndbMaxWaitSec = 60;
    private readonly List<string> _vrcndbRecheckQueue = new();
    private readonly HashSet<string> _vrcndbRecheckedIds = new();
    private System.Threading.Timer? _vrcndbRecheckTimer;

    private void LoadDeletedAvatarsCache()
    {
        foreach (var id in AvtrdbCacheHelper.LoadAllDeletedIds())
        {
            _deletedAvatarIds.Add(id);
            _checkedAvatarIds.Add(id);
        }
    }

    private void QueueAvtrdbReport(List<string> ids)
    {
        int added = 0;
        lock (_avtrdbReportQueue)
        {
            foreach (var id in ids)
                if (_reportedToAvtrdb.Add(id)) { _avtrdbReportQueue.Add(id); added++; }
        }
        if (added > 0)
            Invoke(() => SendToJS("avtrdbCollecting", new { count = added }));
        // Debounce: wait 60s for more IDs to accumulate, then send in one batch
        _avtrdbReportTimer?.Dispose();
        _avtrdbReportTimer = new System.Threading.Timer(_ => _ = Task.Run(FlushAvtrdbReportQueue), null, 60_000, Timeout.Infinite);
    }

    private async Task FlushAvtrdbReportQueue()
    {
        List<string> batch;
        lock (_avtrdbReportQueue)
        {
            if (_avtrdbReportQueue.Count == 0) return;
            batch = new List<string>(_avtrdbReportQueue);
            _avtrdbReportQueue.Clear();
        }
        await SendToAvtrdb(batch, "deletion");
    }

    private void QueueAvtrdbSubmit(string avatarId)
    {
        if (!_settings.AvtrdbSubmitAvatars) return;
        lock (_avtrdbSubmitQueue)
        {
            if (!_avtrdbSubmittedIds.Add(avatarId)) return;
            _avtrdbSubmitQueue.Add(avatarId);
        }
        // Check if avatar already exists in avtrdb before submitting
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _core.Avatars.SearchAvatarsAsync(avatarId, 1);
                foreach (var a in result.Cast<JObject>())
                {
                    var vid = a["vrc_id"]?.ToString() ?? a["id"]?.ToString() ?? "";
                    if (vid.StartsWith("avtr_")) QueueVrcndbSubmit(vid);
                }
                bool exists = result.Count > 0 && result.Any(a =>
                    (a["vrc_id"]?.ToString() ?? a["id"]?.ToString() ?? "") == avatarId);
                if (exists)
                {
                    lock (_avtrdbSubmitQueue) _avtrdbSubmitQueue.Remove(avatarId);
                    return;
                }
                // Avatar not in avtrdb — keep in queue, debounce submit
                Invoke(() => SendToJS("avtrdbCollecting", new { count = 0, submit = 1 }));
                _avtrdbSubmitTimer?.Dispose();
                _avtrdbSubmitTimer = new System.Threading.Timer(_ => _ = Task.Run(FlushAvtrdbSubmitQueue), null, 60_000, Timeout.Infinite);
            }
            catch (Exception ex)
            {
                lock (_avtrdbSubmitQueue)
                {
                    _avtrdbSubmittedIds.Remove(avatarId);
                    _avtrdbSubmitQueue.Remove(avatarId);
                }
                CrashHandler.WriteEntry("QueueAvtrdbSubmit", ex);
            }
        });
    }

    private async Task FlushAvtrdbSubmitQueue()
    {
        List<string> batch;
        lock (_avtrdbSubmitQueue)
        {
            if (_avtrdbSubmitQueue.Count == 0) return;
            batch = new List<string>(_avtrdbSubmitQueue);
            _avtrdbSubmitQueue.Clear();
        }
        await SendToAvtrdb(batch, "submit");
    }

    private async Task SendToAvtrdb(List<string> avatarIds, string reportType = "deletion")
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestVersion = System.Net.HttpVersion.Version20;
            client.DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionExact;
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent);
            var userId = _vrcApi.CurrentUserId;
            var payload = new { avatar_ids = avatarIds, attribution = string.IsNullOrEmpty(userId) ? null : userId };
            var json = JsonConvert.SerializeObject(payload);
            SendToJS("log", new { msg = $"[AVTRDB] SUB {reportType} x{avatarIds.Count}", color = "sec" });
            var resp = await client.PostAsync("https://api.avtrdb.com/v3/avatar/ingest",
                new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();

            if (resp.IsSuccessStatusCode)
            {
                var r = JObject.Parse(body);
                var enqueued = r["avatars_enqueued"]?.Value<int>() ?? 0;
                var invalid = r["invalid_ids"]?.Value<int>() ?? 0;
                var ticket = r["ticket"]?.ToString() ?? "";
                Invoke(() =>
                {
                    var label = reportType == "submit" ? "Submitted" : "Reported";
                    SendToJS("log", new { msg = $"[avtrdb] {label} {avatarIds.Count} avatar(s) — {enqueued} enqueued, {invalid} invalid", color = "ok" });
                    SendToJS("avtrdbReport", new { count = avatarIds.Count, enqueued, invalid, ticket, type = reportType });
                });
            }
            else
                Invoke(() => SendToJS("log", new { msg = $"[avtrdb] Failed to report: {(int)resp.StatusCode} {body[..Math.Min(200, body.Length)]}", color = "err" }));
        }
        catch (Exception ex)
        {
            Invoke(() => SendToJS("log", new { msg = $"[avtrdb] Error: {ex.Message}", color = "err" }));
        }
    }

    private void QueueAvtrIcuReport(List<string> ids)
    {
        if (!_settings.AvtrIcuReportDeleted) return;
        int added = 0;
        lock (_avtrIcuReportQueue)
        {
            foreach (var id in ids)
                if (_reportedToAvtrIcu.Add(id)) { _avtrIcuReportQueue.Add(id); added++; }
        }
        if (added > 0)
        {
            _avtrIcuReportTimer?.Dispose();
            _avtrIcuReportTimer = new System.Threading.Timer(_ => _ = Task.Run(FlushAvtrIcuReportQueue), null, 60_000, Timeout.Infinite);
        }
    }

    private async Task FlushAvtrIcuReportQueue()
    {
        List<string> batch;
        lock (_avtrIcuReportQueue)
        {
            if (_avtrIcuReportQueue.Count == 0) return;
            batch = new List<string>(_avtrIcuReportQueue);
            _avtrIcuReportQueue.Clear();
        }
        await SendToAvtrIcu(batch, "deletion");
    }

    private void QueueAvtrIcuSubmit(string avatarId)
    {
        if (!_settings.AvtrIcuSubmitAvatars) return;
        lock (_avtrIcuSubmitQueue)
        {
            if (!_avtrIcuSubmittedIds.Add(avatarId)) return;
            _avtrIcuSubmitQueue.Add(avatarId);
        }
        _avtrIcuSubmitTimer?.Dispose();
        _avtrIcuSubmitTimer = new System.Threading.Timer(_ => _ = Task.Run(FlushAvtrIcuSubmitQueue), null, 60_000, Timeout.Infinite);
    }

    private async Task FlushAvtrIcuSubmitQueue()
    {
        List<string> batch;
        lock (_avtrIcuSubmitQueue)
        {
            if (_avtrIcuSubmitQueue.Count == 0) return;
            batch = new List<string>(_avtrIcuSubmitQueue);
            _avtrIcuSubmitQueue.Clear();
        }
        await SendToAvtrIcu(batch, "submit");
    }

    private async Task SendToAvtrIcu(List<string> avatarIds, string reportType = "deletion")
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestVersion = System.Net.HttpVersion.Version20;
            client.DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower;
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent);
            var payload = avatarIds.Select(id => new { id }).ToArray();
            var json = JsonConvert.SerializeObject(payload);
            var resp = await client.PostAsync("https://avtr.icu/upload-bulk",
                new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
                Invoke(() =>
                {
                    var label = reportType == "submit" ? "Submitted" : "Reported";
                    SendToJS("log", new { msg = $"[avtr.icu] {label} {avatarIds.Count} avatar(s)", color = "ok" });
                    SendToJS("avtrIcuReport", new { count = avatarIds.Count, type = reportType });
                });
            else
                Invoke(() => SendToJS("log", new { msg = $"[avtr.icu] Failed: {(int)resp.StatusCode} {body[..Math.Min(200, body.Length)]}", color = "err" }));
        }
        catch (Exception ex)
        {
            Invoke(() => SendToJS("log", new { msg = $"[avtr.icu] Error: {ex.Message}", color = "err" }));
        }
    }

    private static string VrcndbThumbUrl(JObject a)
    {
        var t = a["thumbnail"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(t)) return "";
        return t.StartsWith("http") ? t : "https://db.vrcnext.com" + t;
    }

    private static string[] VrcndbCompat(JObject a)
        => (a["platforms"] as JArray ?? new JArray())
            .Select(p => p.ToString() == "quest" ? "android" : p.ToString())
            .ToArray();

    private void QueueVrcndbSubmit(string avatarId)
    {
        if (!_settings.VrcndbSubmitAvatars) return;
        bool flushNow;
        lock (_vrcndbSubmitQueue)
        {
            if (!_vrcndbSubmittedIds.Add(avatarId)) return;
            _vrcndbSubmitQueue.Add(avatarId);
            _vrcndbFirstQueuedAt ??= DateTime.UtcNow;
            flushNow = _vrcndbSubmitQueue.Count >= VrcndbBatchSize
                    || (DateTime.UtcNow - _vrcndbFirstQueuedAt.Value).TotalSeconds >= VrcndbMaxWaitSec;
        }
        _vrcndbSubmitTimer?.Dispose();
        _vrcndbSubmitTimer = new System.Threading.Timer(_ => _ = Task.Run(FlushVrcndbSubmitQueue), null, VrcndbQuietMs, Timeout.Infinite);
        if (flushNow) _ = Task.Run(FlushVrcndbSubmitQueue);
    }

    private async Task FlushVrcndbSubmitQueue()
    {
        if (!await _vrcndbFlushGate.WaitAsync(0)) return;
        try
        {
            while (true)
            {
                List<string> batch;
                lock (_vrcndbSubmitQueue)
                {
                    if (_vrcndbSubmitQueue.Count == 0) { _vrcndbFirstQueuedAt = null; return; }
                    var take = Math.Min(100, _vrcndbSubmitQueue.Count);
                    batch = _vrcndbSubmitQueue.GetRange(0, take);
                    _vrcndbSubmitQueue.RemoveRange(0, take);
                    _vrcndbFirstQueuedAt = _vrcndbSubmitQueue.Count > 0 ? DateTime.UtcNow : null;
                }
                await PostToVrcndb("ingest.php", batch, "submit");
                await Task.Delay(500);
            }
        }
        finally { _vrcndbFlushGate.Release(); }
    }

    private void QueueVrcndbRecheck(IEnumerable<string> ids)
    {
        if (!_settings.VrcndbReportDeleted) return;
        lock (_vrcndbRecheckQueue)
        {
            foreach (var id in ids)
                if (_vrcndbRecheckedIds.Add(id)) _vrcndbRecheckQueue.Add(id);
            if (_vrcndbRecheckQueue.Count == 0) return;
        }
        _vrcndbRecheckTimer?.Dispose();
        _vrcndbRecheckTimer = new System.Threading.Timer(_ => _ = Task.Run(FlushVrcndbRecheckQueue), null, 30_000, Timeout.Infinite);
    }

    private async Task FlushVrcndbRecheckQueue()
    {
        List<string> batch;
        lock (_vrcndbRecheckQueue)
        {
            if (_vrcndbRecheckQueue.Count == 0) return;
            batch = new List<string>(_vrcndbRecheckQueue);
            _vrcndbRecheckQueue.Clear();
        }
        await PostToVrcndb("recheck.php", batch, "deletion");
    }

    private async Task PostToVrcndb(string endpoint, List<string> avatarIds, string reportType)
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestVersion = System.Net.HttpVersion.Version20;
            client.DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower;
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent);
            var userId = _vrcApi.CurrentUserId;
            var payload = new { avatar_ids = avatarIds, attribution = string.IsNullOrEmpty(userId) ? null : userId };
            var json = JsonConvert.SerializeObject(payload);
            var resp = await client.PostAsync($"{VrcndbBase}/{endpoint}",
                new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();

            if (resp.IsSuccessStatusCode)
            {
                var r = JObject.Parse(body);
                var enqueued = r["enqueued"]?.Value<int>() ?? r["rechecking"]?.Value<int>() ?? 0;
                var duplicates = r["duplicates"]?.Value<int>() ?? 0;
                Invoke(() =>
                {
                    var label = reportType == "submit" ? "Submitted" : "Reported";
                    SendToJS("log", new { msg = $"[VRCNDb] {label} {avatarIds.Count} avatar(s) — {enqueued} new, {duplicates} dupes", color = "ok" });
                    SendToJS("vrcndbReport", new { count = avatarIds.Count, enqueued, duplicates, type = reportType });
                });
            }
            else
                Invoke(() => SendToJS("log", new { msg = $"[VRCNDb] Failed: {(int)resp.StatusCode} {body[..Math.Min(200, body.Length)]}", color = "err" }));
        }
        catch (Exception ex)
        {
            Invoke(() => SendToJS("log", new { msg = $"[VRCNDb] Error: {ex.Message}", color = "err" }));
        }
    }

    // JS to C# message handler
    private async Task OnWebMessage(string rawMessage)
    {
        try
        {
            JObject msg;
            using (var _jr = new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(rawMessage)) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })
                msg = JObject.Load(_jr);
            var action = msg["action"]?.ToString() ?? "";
            CrashHandler.AddBreadcrumb($"JS→C# action={action}");

#if !WINDOWS
            if (IsWindowsOnlyAction(action)) return;
#endif

            switch (action)
            {
                case "ready":
                    // Signal platform to JS (hides Windows-only tabs on Linux)
                    SendToJS("setPlatform", new { isLinux = !OperatingSystem.IsWindows() });
                    _windowCtrl.InstallChrome();
                    _windowCtrl.RestoreMaximizedState();
                    // Debug: show what Load() did
                    if (AppSettings.LastLoadError != null)
                        SendToJS("log", new { msg = $"[LOAD ERROR] {AppSettings.LastLoadError}", color = "err" });
                    SendToJS("log", new { msg = $"[LOAD] {AppSettings.LoadDebugInfo}", color = "sec" });
                    SendToJS("log", new { msg = $"[STARTUP] Webhooks: {string.Join(", ", _settings.Webhooks.Select((w,i) => $"#{i+1} \"{w.Name}\" url={w.Url?.Length ?? 0}ch {(w.Enabled?"ON":"off")}"))}", color = "sec" });
                    if (OperatingSystem.IsWindows())
                    {
                        try
                        {
                            using var wid = System.Security.Principal.WindowsIdentity.GetCurrent();
                            bool elevated = new System.Security.Principal.WindowsPrincipal(wid)
                                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                            SendToJS("log", new { msg = $"[STARTUP] Elevated: {(elevated ? "yes" : "no")}", color = "sec" });
                        }
                        catch { }
                    }
                    _authCtrl.HandleReady();
                    _sfCtrl.ResendState();
                    _stCtrl.ResendState();
                    _fsCtrl.ResendState();
                    FlushPendingDeepLink();
                    // Check for crash report from previous session — show modal after UI is ready
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1200);
                        CheckAndShowPendingCrash();
                    });
                    break;

                case "getGameLog":
                    _authCtrl.HandleGetGameLog();
                    break;

                case "setupReady":
                    _windowCtrl.InstallChrome();
                    await _authCtrl.HandleMessage(action, msg);
                    break;

                // Setup / Auth / Settings — delegated to AuthController
                case "setupDone":
                case "forceTrim":
                case "resetSetup":
                case "getImgCacheSize":
                case "optimizeImgCache":
                case "clearFfcCache":
                case "dbAnalyze":
                case "dbMemoryUsage":
                case "dbOptimize":
                case "dbBackup":
                case "regBackup":
                case "forceFfcAll":
                case "setupSaveLanguage":
                case "setupSaveStartWithWindows":
                case "setupSaveVrcPath":
                case "setupSavePhotoDir":
                case "setupSavePrefs":
                case "setupBrowsePhotoDir":
                    await _authCtrl.HandleMessage(action, msg);
                    break;

                // Window chrome (borderless)
                case "windowMinimize":
                case "windowMaximize":
                case "windowClose":
                case "windowDragStart":
                case "windowResizeStart":
                case "setGuiZoom":
                    _windowCtrl.HandleMessage(action, msg);
                    break;

                case "startRelay":
                case "stopRelay":
                    _relayCtrl.HandleMessage(action, msg);
                    break;

                case "getTtsDevices":
                case "ttsTest":
                case "ttsPreview":
                case "getSystemFonts":
                case "getCursorFiles":
                case "getCustomThemes":
                    _windowCtrl.HandleMessage(action, msg);
                    break;

                case "saveSettings":
                case "saveVrcndbConsent":
                case "loadTranslation":
                    await _authCtrl.HandleMessage(action, msg);
                    break;

                case "screenPickColor":
#if WINDOWS
                    ScreenColorPickerService.Start(
                        hex => SendToJS("screenPickResult", new { hex }),
                        () => SendToJS("screenPickResult", new { cancelled = true }));
#else
                    SendToJS("screenPickResult", new { unsupported = true });
#endif
                    break;

                case "vrcnPlusCheckEntitlement":
                case "vrcnPlusGetTheme":
                case "vrcnPlusSaveTheme":
                    await _vrcnPlusCtrl.HandleMessage(action, msg);
                    break;

                case "saveCustomColors":
                    var themesArr = msg["themes"] as JArray;
                    if (themesArr != null)
                        _cache.Save(CacheHandler.KeyCustomColors, new { themes = themesArr });
                    break;

                case "addFolder":
                    {
                        var r = Dialog.FolderPicker();
                        if (r.IsOk) SendToJS("folderAdded", r.Path);
                    }
                    break;

                case "pickFolder":
                    {
                        var target = msg["target"]?.ToString() ?? "";
                        var r = Dialog.FolderPicker();
                        if (r.IsOk) SendToJS("folderPicked", new { target, path = r.Path });
                    }
                    break;

                case "importVrcxSelect":
                case "importVrcxStart":
                    await _timelineCtrl.HandleMessage(action, msg);
                    break;

                // Photo/Library actions delegated to PhotosController
                case "deletePost":
                case "manualPost":
                case "scanLibrary":
                case "scanLibraryForce":
                case "loadLibraryPage":
                case "deleteLibraryFile":
                case "addFavorite":
                case "removeFavorite":
                case "setDesktopBackground":
                case "getPhotoRating":
                case "setPhotoRating":
                case "scanLibraryRatings":
                case "getMediaTags":
                case "setMediaTags":
                case "setMediaUserTag":
                case "removeMediaUserTag":
                    await _photos.HandleMessage(action, msg);
                    break;

                case "copyImageToClipboard":
                {
                    var clipUrl = msg["url"]?.ToString();
                    if (!string.IsNullOrEmpty(clipUrl) && string.IsNullOrEmpty(msg["path"]?.ToString()))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var resp = await _vrcApi.GetHttpClient().GetAsync(clipUrl);
                                if (resp.IsSuccessStatusCode)
                                {
                                    var bytes = await resp.Content.ReadAsByteArrayAsync();
                                    var ext = (resp.Content.Headers.ContentType?.MediaType ?? "").Contains("jpeg") ? "jpg" : "png";
                                    var tempPath = Path.Combine(Path.GetTempPath(), $"vrcn_clip_{Guid.NewGuid():N}.{ext}");
                                    File.WriteAllBytes(tempPath, bytes);
                                    await _photos.HandleMessage("copyImageToClipboard", new JObject { ["path"] = tempPath });
                                }
                                else SendToJS("toast", new { ok = false, msg = "Copy failed" });
                            }
                            catch (Exception ex) { SendToJS("toast", new { ok = false, msg = $"Copy failed: {ex.Message}" }); }
                        });
                    }
                    else
                    {
                        await _photos.HandleMessage(action, msg);
                    }
                    break;
                }

                case "browseExe":
                case "browseDashBg":
                case "vrcLoadDashBg":
                case "vrcRandomDashBg":
                    await _authCtrl.HandleMessage(action, msg);
                    break;

                // Resolve world IDs to names/thumbnails for dashboard
                case "vrcResolveWorlds":
                case "vrcResolveGroups":
                    await _instance.HandleMessage(action, msg);
                    break;

                case "vrcGetRecentWorlds":
                    _ = Task.Run(async () =>
                    {
                        var worlds = await _core.World.GetRecentWorldsAsync();
                        foreach (JObject w in worlds.OfType<JObject>())
                        {
                            var url = ImageCacheHelper.GetWorldUrl(w["id"]?.ToString(), w["imageUrl"]?.ToString() ?? w["thumbnailImageUrl"]?.ToString());
                            w["imageUrl"] = url; w["thumbnailImageUrl"] = url;
                        }
                        Invoke(() => SendToJS("recentWorlds", new { worlds }));
                    });
                    break;

                case "vrcGetVisitedWorlds":
                    _ = Task.Run(async () =>
                    {
                        var ids = _core.Timeline.GetRecentVisitedWorldIds(100);
                        var worlds = await _core.World.GetWorldsByIdsAsync(ids);
                        foreach (JObject w in worlds.OfType<JObject>())
                        {
                            var wid = w["id"]?.ToString() ?? "";
                            var url = ImageCacheHelper.GetWorldUrl(wid, w["imageUrl"]?.ToString() ?? w["thumbnailImageUrl"]?.ToString());
                            w["imageUrl"] = url; w["thumbnailImageUrl"] = url;
                            EnrichWorldDatesFromCache(_core.TimeEngine, w, wid);
                            w["created_at"] = DateTimeHelper.Iso(w["created_at"]);
                            w["updated_at"] = DateTimeHelper.Iso(w["updated_at"]);
                            var stats = _core.TimeEngine.GetWorldStats(wid);
                            w["worldTimeSeconds"]  = stats.totalSeconds;
                            w["worldVisitCount"]   = stats.visitCount;
                            w["worldLastVisited"]  = stats.lastVisited;
                        }
                        Invoke(() => SendToJS("visitedWorlds", new { worlds }));
                    });
                    break;

                case "vrcGetRecentSeen":
                    _ = Task.Run(() =>
                    {
                        var players = _core.Timeline.GetRecentSeenPlayers(100, _core.CurrentVrcUserId);
                        foreach (JObject p in players)
                        {
                            var pid = p["id"]?.ToString() ?? "";
                            _friends.EnrichFromProfileCache(p, pid, false);
                            p["image"] = ImageCacheHelper.GetUserUrl(pid, p["image"]?.ToString());
                        }
                        Invoke(() => SendToJS("recentSeenPlayers", new { players }));
                    });
                    break;

                case "vrcGetRecentAvatars":
                    _ = Task.Run(async () =>
                    {
                        var minimal = _core.Timeline.GetRecentUsedAvatars(100);
                        var resolved = await Task.WhenAll(minimal.Select(async m =>
                        {
                            var id = m["id"]?.ToString() ?? "";
                            JObject a;
                            if (_core.TimeEngine.GetAvatarDetail(id) != null)
                            {
                                a = m;
                            }
                            else
                            {
                                JObject? full = null;
                                try { full = await _core.Avatars.GetAvatarAsync(id); } catch { }
                                if (full != null) CacheAvatarDetailFrom(full);
                                a = full ?? m;
                            }
                            EnrichAvatarFromCache(a, id);
                            var img = a["thumbnailImageUrl"]?.ToString() ?? a["imageUrl"]?.ToString() ?? m["thumbnailImageUrl"]?.ToString();
                            a["thumbnailImageUrl"] = ImageCacheHelper.GetAvatarUrl(id, img);
                            return a;
                        }));
                        var avatars = new JArray();
                        foreach (var a in resolved) avatars.Add(a);
                        Invoke(() => SendToJS("recentAvatars", new { avatars }));
                    });
                    break;

                case "vrcGetPopularWorlds":
                    _ = Task.Run(async () =>
                    {
                        var worlds = await _core.World.GetPopularWorldsAsync();
                        foreach (JObject w in worlds.OfType<JObject>())
                        {
                            var url = ImageCacheHelper.GetWorldUrl(w["id"]?.ToString(), w["imageUrl"]?.ToString() ?? w["thumbnailImageUrl"]?.ToString());
                            w["imageUrl"] = url; w["thumbnailImageUrl"] = url;
                        }
                        Invoke(() => SendToJS("popularWorlds", new { worlds }));
                    });
                    break;

                case "vrcGetActiveWorlds":
                    _ = Task.Run(async () =>
                    {
                        var worlds = await _core.World.GetActiveWorldsAsync();
                        foreach (JObject w in worlds.OfType<JObject>())
                        {
                            var url = ImageCacheHelper.GetWorldUrl(w["id"]?.ToString(), w["imageUrl"]?.ToString() ?? w["thumbnailImageUrl"]?.ToString());
                            w["imageUrl"] = url; w["thumbnailImageUrl"] = url;
                        }
                        Invoke(() => SendToJS("activeWorlds", new { worlds }));
                    });
                    break;


                case "playVRChat":
                    _relayCtrl.HandleMessage(action, msg);
                    break;

                case "vrcLogin":
                case "vrc2FA":
                case "vrcLogout":
                // Multi-Account actions.
                case "listAccounts":
                case "addAccount":
                case "addAccount2FA":
                case "addAccountCancel":
                case "switchAccount":
                case "removeAccount":
                case "logoutAccount":
                    await _authCtrl.HandleMessage(action, msg);
                    break;

                case "vrcRefreshFriends":
                    await _friends.RefreshFriendsAsync();
                    break;

                case "vrcFriendFetchState":
                case "vrcFriendFetchStart":
                    await _friendFetch.HandleMessage(action, msg);
                    break;

                // Update own status
                case "vrcUpdateStatus":
                    var newStatus = msg["status"]?.ToString() ?? "active";
                    var newDesc = msg["statusDescription"]?.ToString() ?? "";
                    await _friends.UpdateStatusAsync(newStatus, newDesc);
                    break;

                // Update own profile (bio, pronouns, links, languages, icon, banner)
                case "vrcSaveProfileTheme":
                {
                    var thId   = msg["themeId"]?.ToString() ?? "";
                    var thName = msg["name"]?.ToString() ?? "";
                    var thBtn  = msg["buttonColor"]?.ToString() ?? "";
                    var thIcon = msg["iconColor"]?.ToString() ?? "";
                    var thSub  = msg["subtextColor"]?.ToString() ?? "";
                    _ = Task.Run(async () =>
                    {
                        var theme = string.IsNullOrEmpty(thId)
                            ? await _core.Users.CreateProfileThemeAsync(thName, thBtn, thIcon, thSub)
                            : await _core.Users.UpdateProfileThemeAsync(thId, thName, thBtn, thIcon, thSub);
                        Invoke(() => SendToJS("vrcProfileThemeSaved", new { success = theme != null, theme, created = string.IsNullOrEmpty(thId) }));
                    });
                    break;
                }

                case "vrcDeleteProfileTheme":
                {
                    var delId = msg["themeId"]?.ToString() ?? "";
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.Users.DeleteProfileThemeAsync(delId);
                        Invoke(() => SendToJS("vrcProfileThemeDeleted", new { success = ok, themeId = delId }));
                    });
                    break;
                }

                case "vrcSetActiveProfileTheme":
                {
                    var actId = msg["themeId"]?.ToString() ?? "";
                    var actSelf = _core.VrcApi.CurrentUserId;
                    _ = Task.Run(async () =>
                    {
                        var res = await _core.Users.UpdateProfileAppearanceAsync(actSelf, new JObject { ["themeId"] = actId });
                        Invoke(() => SendToJS("vrcActiveProfileThemeSet", new { success = res != null, themeId = actId }));
                    });
                    break;
                }

                case "vrcUpdateProfileBackground":
                {
                    var bgKind = msg["backgroundType"]?.ToString() ?? "default";
                    var bgBody = new JObject { ["backgroundType"] = bgKind };
                    if (bgKind == "texture")
                        bgBody["backgroundTextureId"] = msg["backgroundTextureId"]?.ToString() ?? "";
                    else if (bgKind == "gradient")
                    {
                        // VRChat expects bare hex, the picker hands us "#rrggbb".
                        bgBody["backgroundGradientTop"]    = (msg["backgroundGradientTop"]?.ToString()    ?? "").TrimStart('#');
                        bgBody["backgroundGradientBottom"] = (msg["backgroundGradientBottom"]?.ToString() ?? "").TrimStart('#');
                    }

                    var bgSelfId = _core.VrcApi.CurrentUserId;
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.Users.UpdateProfileAppearanceAsync(bgSelfId, bgBody);
                        Invoke(() =>
                        {
                            SendToJS("vrcProfileBackgroundUpdated", new
                            {
                                success                  = ok != null,
                                backgroundType           = bgKind,
                                backgroundTextureId      = bgBody["backgroundTextureId"]?.ToString() ?? "",
                                backgroundTextureUrl     = ProfileBackgroundHelper.UrlFor(bgBody["backgroundTextureId"]?.ToString()),
                                backgroundGradientTop    = bgBody["backgroundGradientTop"]?.ToString() ?? "",
                                backgroundGradientBottom = bgBody["backgroundGradientBottom"]?.ToString() ?? "",
                            });
                            SendToJS("log", new { msg = ok != null ? "VRChat: Profile background updated" : "VRChat: Failed to update profile background", color = ok != null ? "ok" : "err" });
                        });
                    });
                    break;
                }

                case "vrcUpdateProfileBanner":
                {
                    var bnUrl    = msg["bannerCustomUrl"]?.ToString() ?? "";
                    var bnSelfId = _core.VrcApi.CurrentUserId ?? "";
                    _ = Task.Run(async () =>
                    {
                        var ok = string.IsNullOrEmpty(bnUrl) || string.IsNullOrEmpty(bnSelfId)
                            ? null
                            : await _core.Users.SetProfileBannerAsync(bnSelfId, bnUrl);
                        Invoke(() =>
                        {
                            SendToJS("vrcProfileBannerUpdated", new
                            {
                                success   = ok != null,
                                bannerUrl = ok != null ? ImageCacheHelper.GetUserBannerUrl(bnSelfId, bnUrl) : "",
                            });
                            SendToJS("log", new { msg = ok != null ? "VRChat: Profile banner updated" : "VRChat: Failed to update profile banner", color = ok != null ? "ok" : "err" });
                        });
                    });
                    break;
                }

                case "vrcUpdateProfile":
                    var upBio = msg["bio"] != null ? msg["bio"]!.ToString() : (string?)null;
                    var upPronouns = msg["pronouns"] != null ? msg["pronouns"]!.ToString() : (string?)null;
                    var upBioLinks = msg["bioLinks"]?.ToObject<List<string>>();
                    var upTags = msg["tags"]?.ToObject<List<string>>();
                    var upUserIcon = msg["userIcon"]           != null ? msg["userIcon"]!.ToString()           : (string?)null;
                    var upBanner   = msg["profilePicOverride"] != null ? msg["profilePicOverride"]!.ToString() : (string?)null;
                    _ = Task.Run(async () =>
                    {
                        var updUser = await _core.Users.UpdateProfileAsync(upBio, upPronouns, upBioLinks, upTags, upUserIcon, upBanner);
                        Invoke(() =>
                        {
                            if (updUser != null)
                            {
                                _authCtrl.SendVrcUserData(updUser);
                                SendToJS("vrcProfileUpdated", new { success = true });
                                SendToJS("log", new { msg = "VRChat: Profile updated", color = "ok" });
                            }
                            else
                            {
                                SendToJS("vrcProfileUpdated", new { success = false, error = "Update failed" });
                                SendToJS("log", new { msg = "VRChat: Profile update failed", color = "err" });
                            }
                        });
                    });
                    break;

                // Toggle badge visibility
                case "vrcUpdateBadge":
                    var badgeId = msg["badgeId"]?.ToString() ?? "";
                    var badgeShowcased = msg["showcased"]?.Value<bool>() ?? false;
                    if (!string.IsNullOrEmpty(badgeId))
                    {
                        _ = Task.Run(async () =>
                        {
                            var ok = await _core.Users.UpdateBadgeAsync(badgeId, badgeShowcased);
                            Invoke(() =>
                            {
                                SendToJS("vrcBadgeUpdated", new { badgeId, showcased = badgeShowcased, success = ok });
                                if (ok) SendToJS("log", new { msg = $"Badge {(badgeShowcased ? "shown" : "hidden")}", color = "ok" });
                                else SendToJS("log", new { msg = "Badge update failed", color = "err" });
                            });
                        });
                    }
                    break;

                // Multi-Invite delegated to FriendsController
                case "vrcBatchInvite":
                    await _friends.HandleMessage(action, msg);
                    break;

                case "vrcGetMyInstances":
                    await _instance.HandleMessage(action, msg);
                    break;

                case "vrcGetInstanceDetail":
                    await _instance.HandleMessage(action, msg);
                    break;

                case "vrcGetWorldInstancesDetail":
                    await _instance.HandleMessage(action, msg);
                    break;

                case "vrcRemoveMyInstance":
                    await _instance.HandleMessage(action, msg);
                    break;

                case "consoleCommand":
                {
                    var cmd = msg["cmd"]?.ToString() ?? "";
                    var result = ConsoleHelper.Execute(cmd);
                    if (!string.IsNullOrEmpty(result.Text))
                        SendToJS("consoleOutput", new { text = result.Text, color = result.Color });
                    if (result.Extra == "forceTrim")
                        _core.MemTrim.TrimNow();
                    else if (result.Extra == "fixNps")
                        VRCNext.Services.WindowsFixes.ForceFix();
                    else if (result.Extra == "forceTrimAll")
                        _core.TrimCaches(force: true);
                    else if (result.Extra == "vrcMsgList" && result.ExtraPayload != null)
                    {
                        var msgType = JObject.FromObject(result.ExtraPayload)["msgType"]?.ToString() ?? "message";
                        _ = Task.Run(async () =>
                        {
                            var uid = _core.VrcApi.CurrentUserId;
                            if (string.IsNullOrEmpty(uid))
                            {
                                SendToJS("consoleOutput", new { text = "/msg: not logged in", color = "err" });
                                return;
                            }
                            var arr = await _core.Invite.GetInviteMessagesAsync(uid, msgType);
                            if (arr == null)
                            {
                                SendToJS("consoleOutput", new { text = $"/msg {(msgType == "requestResponse" ? "request" : "invite")}: API call failed", color = "err" });
                                return;
                            }
                            var label = msgType == "requestResponse" ? "Invite Request Responses" : "Invite Messages";
                            var lines = new List<string> { $"{label} ({arr.Count}):" };
                            foreach (JObject m in arr.Cast<JObject>().OrderBy(x => x["slot"]?.Value<int>() ?? 0))
                            {
                                var slot     = m["slot"]?.Value<int>() ?? -1;
                                var text     = m["message"]?.ToString() ?? "";
                                var cd       = m["remainingCooldownMinutes"]?.Value<int>() ?? 0;
                                var cdNote   = cd > 0 ? $" [cooldown {cd}m]" : "";
                                lines.Add($"  Slot {slot}: \"{text}\"{cdNote}");
                            }
                            SendToJS("consoleOutput", new { text = string.Join("\n", lines), color = "info" });
                        });
                    }
                    else if (result.Extra == "vrcnPlusAdmin" && result.ExtraPayload != null)
                    {
                        var payload  = JObject.FromObject(result.ExtraPayload);
                        var sub      = payload["sub"]?.ToString() ?? "";
                        var targetId = payload["targetId"]?.ToString() ?? "";
                        _ = Task.Run(async () =>
                        {
                            var caller = _core.VrcApi.CurrentUserId;
                            if (string.IsNullOrEmpty(caller))
                            {
                                SendToJS("consoleOutput", new { text = "[VRCN+] not logged in", color = "err" });
                                return;
                            }
                            var (cmdOk, text) = await _vrcnPlusCtrl.RunAdminCommandAsync(sub, caller, targetId);
                            SendToJS("consoleOutput", new { text, color = cmdOk ? "ok" : "err" });
                        });
                    }
                    else if (result.Extra != null && result.ExtraPayload != null)
                        SendToJS(result.Extra, result.ExtraPayload);
                    break;
                }

                case "openLogFile":
                    if (!string.IsNullOrEmpty(_activityLogPath) && File.Exists(_activityLogPath))
                        Process.Start(new ProcessStartInfo(_activityLogPath) { UseShellExecute = true });
                    break;

                case "openLogFolder":
                    if (!string.IsNullOrEmpty(_activityLogDir) && Directory.Exists(_activityLogDir))
                        Process.Start(new ProcessStartInfo(_activityLogDir) { UseShellExecute = true });
                    break;


                // Get friend detail / preview
                case "vrcGetFriendDetail":
                    var friendId = msg["userId"]?.ToString();
                    if (!string.IsNullOrEmpty(friendId))
                    {
                        if (msg["force"]?.Value<bool>() == true)
                            VRCNext.Services.Helpers.ModalCacheHelper.Invalidate(friendId);
                        await _friends.GetFriendDetailAsync(friendId);
                    }
                    break;

                case "vrcGetFriendPreview":
                case "vrcGetUserBasic":
                case "commentsApi":
                    await _friends.HandleMessage(action, msg);
                    break;

                case "vrcLookupAvatarByFileId":
                case "vrcGetAvatarInfo":
                case "vrcGetInstanceAvatars":
                    await _friends.HandleMessage(action, msg);
                    break;

                // Friend actions delegated to FriendsController
                case "vrcJoinFriend":
                case "vrcInviteFriend":
                case "vrcInviteFriendWithPhoto":
                case "vrcGetInviteMessages":
                case "vrcUpdateInviteMessage":
                case "vrcRequestInvite":
                case "vrcGetUserAvatars":
                case "vrcGetUserFavWorlds":
                    await _friends.HandleMessage(action, msg);
                    break;

                case "vrcCreateInstance":
                case "vrcSelfInvite":
                case "vrcOpenInGame":
                    await _instance.HandleMessage(action, msg);
                    break;

                // User Notes
                case "vrcUpdateNote":
                case "setUserMemo":
                    await _friends.HandleMessage(action, msg);
                    break;

                // Avatars - list and switch
                case "vrcGetAvatars":
                    var avatarFilterType = msg["filter"]?.ToString() ?? "own";
                    if (avatarFilterType == "own")
                    {
                        if (_settings.FfcEnabled)
                        {
                            var cachedAvt = _cache.LoadRaw(CacheHandler.KeyAvatars);
                            if (cachedAvt != null) Invoke(() => SendToJS("vrcAvatars", cachedAvt));
                        }
                        _ = Task.Run(_authCtrl.FetchAndCacheAvatarsAsync);
                    }
                    else
                    {
                        if (_settings.FfcEnabled)
                        {
                            var cachedFav = _cache.LoadRaw(CacheHandler.KeyFavAvatars);
                            if (cachedFav != null) Invoke(() => SendToJS("vrcFavoriteAvatars", cachedFav));
                        }
                        _ = Task.Run(_authCtrl.FetchAndCacheFavAvatarsAsync);
                    }
                    break;

                case "vrcSelectAvatar":
                    var selAvatarId = msg["avatarId"]?.ToString();
                    if (!string.IsNullOrEmpty(selAvatarId))
                    {
                        _ = Task.Run(async () =>
                        {
                            var ok5 = await _core.Avatars.SelectAvatarAsync(selAvatarId);
                            if (ok5 && _settings.VrcndbSyncWears) PopularityReporter.Report(selAvatarId, "client", "wear");
                            Invoke(() =>
                            {
                                SendToJS("vrcAvatarSelected", new { avatarId = ok5 ? selAvatarId : "" });
                                SendToJS("log", new
                                {
                                    msg = ok5 ? "Avatar changed!" : "Failed to change avatar",
                                    color = ok5 ? "ok" : "err"
                                });
                            });
                        });
                    }
                    break;

                case "vrcSearchAvatars":
                    var avSearchQuery = msg["query"]?.ToString() ?? "";
                    var avSearchPage  = msg["page"]?.Value<int>() ?? 0;
                    var avSearchDb    = msg["db"]?.ToString() ?? "avtrdb";
                    var avVrcnPlatform = msg["platform"]?.ToString() ?? "";
                    var avVrcnPerf     = msg["perf"]?.ToString() ?? "";
                    var avVrcnContent  = msg["content"]?.ToString() ?? "";
                    var avVrcnFt       = msg["ft"]?.Value<bool>() ?? false;
                    if (!string.IsNullOrWhiteSpace(avSearchQuery))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                int avLimit;
                                List<object> list;
                                var vrcndbIds = new List<string>();

                                if (avSearchDb == "avtricu")
                                {
                                    avLimit = 100;
                                    var similarMatch = System.Text.RegularExpressions.Regex.Match(avSearchQuery, @"^similar:\s*(avtr_[\w-]+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                    var raw = similarMatch.Success
                                        ? await _core.Avatars.SearchSimilarAvatarsAvtrIcuAsync(similarMatch.Groups[1].Value, avLimit)
                                        : await _core.Avatars.SearchAvatarsAvtrIcuAsync(avSearchQuery, avLimit, avSearchPage * avLimit);

                                    list = raw.Cast<JObject>().Select(a => (object)new
                                    {
                                        id                = a["id"]?.ToString() ?? "",
                                        name              = a["name"]?.ToString() ?? "",
                                        thumbnailImageUrl = ImageCacheHelper.GetAvatarUrl(a["id"]?.ToString(), a["thumbnailImageUrl"]?.ToString() ?? a["imageUrl"]?.ToString()),
                                        imageUrl          = ImageCacheHelper.GetAvatarUrl(a["id"]?.ToString(), a["imageUrl"]?.ToString() ?? a["thumbnailImageUrl"]?.ToString()),
                                        authorName        = a["authorName"]?.ToString() ?? "",
                                        releaseStatus     = "public",
                                        description       = a["description"]?.ToString() ?? "",
                                        unityPackages     = Array.Empty<object>(),
                                        compatibility     = (a["platforms"] as JArray ?? new JArray()).Select(p => p.ToString()).ToArray(),
                                        performance       = AvtrIcuPerf(a),
                                        sources           = new[] { "avtricu" },
                                    }).ToList();
                                    vrcndbIds.AddRange(raw.Cast<JObject>().Select(a => a["id"]?.ToString() ?? ""));
                                }
                                else if (avSearchDb == "vrcn")
                                {
                                    avLimit = 20;
                                    var raw = await _core.Avatars.SearchAvatarsVrcnAsync(avSearchQuery, avLimit, avSearchPage, avVrcnPlatform, avVrcnPerf, avVrcnContent, avVrcnFt);
                                    list = raw.Cast<JObject>().Select(a => (object)new
                                    {
                                        id                = a["id"]?.ToString() ?? "",
                                        name              = a["name"]?.ToString() ?? "",
                                        thumbnailImageUrl = ImageCacheHelper.GetAvatarUrl(a["id"]?.ToString(), VrcndbThumbUrl(a)),
                                        imageUrl          = ImageCacheHelper.GetAvatarUrl(a["id"]?.ToString(), VrcndbThumbUrl(a)),
                                        authorName        = a["author_name"]?.ToString() ?? "",
                                        releaseStatus     = "public",
                                        description       = a["description"]?.ToString() ?? "",
                                        unityPackages     = Array.Empty<object>(),
                                        compatibility     = VrcndbCompat(a),
                                        performance       = a["performance"],
                                        sources           = new[] { "vrcn" },
                                    }).ToList();
                                }
                                else if (avSearchDb == "all")
                                {
                                    avLimit = 20;
                                    var similarMatch = System.Text.RegularExpressions.Regex.Match(avSearchQuery, @"^similar:\s*(avtr_[\w-]+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                                    Task<JArray> avtrdbTask, avtrIcuTask;
                                    if (similarMatch.Success)
                                    {
                                        var sid = similarMatch.Groups[1].Value;
                                        avtrdbTask  = _core.Avatars.SearchAvatarsAsync(avSearchQuery, avLimit, avSearchPage);
                                        avtrIcuTask = _core.Avatars.SearchSimilarAvatarsAvtrIcuAsync(sid, avLimit);
                                    }
                                    else
                                    {
                                        avtrdbTask  = _core.Avatars.SearchAvatarsAsync(avSearchQuery, avLimit, avSearchPage);
                                        avtrIcuTask = _core.Avatars.SearchAvatarsAvtrIcuAsync(avSearchQuery, avLimit, avSearchPage * avLimit);
                                    }
                                    var vrcnTask = _core.Avatars.SearchAvatarsVrcnAsync(avSearchQuery, avLimit, avSearchPage);
                                    await Task.WhenAll(avtrdbTask, avtrIcuTask, vrcnTask);

                                    var dbEntries = avtrdbTask.Result.Cast<JObject>()
                                        .Select(a => new {
                                            id                = a["vrc_id"]?.ToString() ?? a["id"]?.ToString() ?? "",
                                            name              = a["name"]?.ToString() ?? "",
                                            thumbnailImageUrl = ImageCacheHelper.GetAvatarUrl(a["vrc_id"]?.ToString() ?? a["id"]?.ToString(), a["image_url"]?.ToString() ?? a["thumbnailImageUrl"]?.ToString() ?? a["imageUrl"]?.ToString()),
                                            imageUrl          = ImageCacheHelper.GetAvatarUrl(a["vrc_id"]?.ToString() ?? a["id"]?.ToString(), a["image_url"]?.ToString() ?? a["imageUrl"]?.ToString()),
                                            authorName        = a["author"]?["name"]?.ToString() ?? a["authorName"]?.ToString() ?? "",
                                            description       = a["description"]?.ToString() ?? "",
                                            unityPackages     = Array.Empty<object>(),
                                            performance       = AvtrdbPerf(a),
                                            compatibility     = (a["compatibility"] as JArray ?? new JArray()).Select(p => p.ToString()).ToArray(),
                                            tags              = (a["tags"]?["author_tags"] as JArray ?? new JArray()).Select(x => "author_tag_" + x.ToString()).ToArray(),
                                            created_at        = DateTimeHelper.Iso(a["created_at"]),
                                            updated_at        = DateTimeHelper.Iso(a["updated_at"]),
                                        })
                                        .Where(x => !string.IsNullOrEmpty(x.id))
                                        .ToList();

                                    var icuEntries = avtrIcuTask.Result.Cast<JObject>()
                                        .Select(a => new {
                                            id                = a["id"]?.ToString() ?? "",
                                            name              = a["name"]?.ToString() ?? "",
                                            thumbnailImageUrl = ImageCacheHelper.GetAvatarUrl(a["id"]?.ToString(), a["thumbnailImageUrl"]?.ToString() ?? a["imageUrl"]?.ToString()),
                                            imageUrl          = ImageCacheHelper.GetAvatarUrl(a["id"]?.ToString(), a["imageUrl"]?.ToString() ?? a["thumbnailImageUrl"]?.ToString()),
                                            authorName        = a["authorName"]?.ToString() ?? "",
                                            description       = a["description"]?.ToString() ?? "",
                                            compatibility     = (a["platforms"] as JArray ?? new JArray()).Select(p => p.ToString()).ToArray(),
                                            performance       = AvtrIcuPerf(a),
                                            tags              = (a["tags"] as JArray ?? new JArray()).Select(x => x.ToString()).ToArray(),
                                            created_at        = DateTimeHelper.Iso(a["created_at"]),
                                            updated_at        = DateTimeHelper.Iso(a["updated_at"]),
                                        })
                                        .Where(x => !string.IsNullOrEmpty(x.id))
                                        .ToList();

                                    var vrcnEntries = vrcnTask.Result.Cast<JObject>()
                                        .Select(a => new {
                                            id                = a["id"]?.ToString() ?? "",
                                            name              = a["name"]?.ToString() ?? "",
                                            thumbnailImageUrl = ImageCacheHelper.GetAvatarUrl(a["id"]?.ToString(), VrcndbThumbUrl(a)),
                                            imageUrl          = ImageCacheHelper.GetAvatarUrl(a["id"]?.ToString(), VrcndbThumbUrl(a)),
                                            authorName        = a["author_name"]?.ToString() ?? "",
                                            description       = a["description"]?.ToString() ?? "",
                                            compatibility     = VrcndbCompat(a),
                                        performance       = a["performance"],
                                            tags              = (a["tags"] as JArray ?? new JArray()).Select(x => x.ToString()).ToArray(),
                                            created_at        = DateTimeHelper.Iso(a["created_at"]),
                                            updated_at        = DateTimeHelper.Iso(a["updated_at"]),
                                        })
                                        .Where(x => !string.IsNullOrEmpty(x.id))
                                        .ToList();

                                    var dbIds   = new HashSet<string>(dbEntries.Select(x => x.id));
                                    var icuIds  = new HashSet<string>(icuEntries.Select(x => x.id));
                                    var vrcnIds = new HashSet<string>(vrcnEntries.Select(x => x.id));

                                    string[] Srcs(string id, string first)
                                    {
                                        var s = new List<string> { first };
                                        if (first != "avtrdb"  && dbIds.Contains(id))   s.Add("avtrdb");
                                        if (first != "avtricu" && icuIds.Contains(id))  s.Add("avtricu");
                                        if (first != "vrcn"    && vrcnIds.Contains(id)) s.Add("vrcn");
                                        return s.ToArray();
                                    }

                                    list = new List<object>();
                                    foreach (var a in dbEntries)
                                        list.Add(new { a.id, a.name, a.thumbnailImageUrl, a.imageUrl, a.authorName, releaseStatus = "public", a.description, a.unityPackages, a.performance, a.compatibility, a.tags, a.created_at, a.updated_at, sources = Srcs(a.id, "avtrdb") });
                                    foreach (var a in icuEntries)
                                    {
                                        if (!dbIds.Contains(a.id))
                                            list.Add(new { a.id, a.name, a.thumbnailImageUrl, a.imageUrl, a.authorName, releaseStatus = "public", a.description, unityPackages = Array.Empty<object>(), a.compatibility, a.performance, a.tags, a.created_at, a.updated_at, sources = Srcs(a.id, "avtricu") });
                                    }
                                    foreach (var a in vrcnEntries)
                                    {
                                        if (!dbIds.Contains(a.id) && !icuIds.Contains(a.id))
                                            list.Add(new { a.id, a.name, a.thumbnailImageUrl, a.imageUrl, a.authorName, releaseStatus = "public", a.description, unityPackages = Array.Empty<object>(), a.compatibility, a.performance, a.tags, a.created_at, a.updated_at, sources = new[] { "vrcn" } });
                                    }
                                    vrcndbIds.AddRange(dbIds);
                                    vrcndbIds.AddRange(icuIds);
                                }
                                else
                                {
                                    avLimit = 20;
                                    var raw = await _core.Avatars.SearchAvatarsAsync(avSearchQuery, avLimit, avSearchPage);
                                    list = raw.Cast<JObject>().Select(a => (object)new
                                    {
                                        id                = a["vrc_id"]?.ToString() ?? a["id"]?.ToString() ?? "",
                                        name              = a["name"]?.ToString() ?? "",
                                        thumbnailImageUrl = ImageCacheHelper.GetAvatarUrl(a["vrc_id"]?.ToString() ?? a["id"]?.ToString(), a["image_url"]?.ToString() ?? a["thumbnailImageUrl"]?.ToString() ?? a["imageUrl"]?.ToString()),
                                        imageUrl          = ImageCacheHelper.GetAvatarUrl(a["vrc_id"]?.ToString() ?? a["id"]?.ToString(), a["image_url"]?.ToString() ?? a["imageUrl"]?.ToString()),
                                        authorName        = a["author"]?["name"]?.ToString() ?? a["authorName"]?.ToString() ?? "",
                                        releaseStatus     = "public",
                                        description       = a["description"]?.ToString() ?? "",
                                        unityPackages     = Array.Empty<object>(),
                                        performance       = AvtrdbPerf(a),
                                        compatibility     = (a["compatibility"] as JArray ?? new JArray()).Select(p => p.ToString()).ToArray(),
                                        tags              = (a["tags"]?["author_tags"] as JArray ?? new JArray()).Select(x => "author_tag_" + x.ToString()).ToArray(),
                                        created_at        = DateTimeHelper.Iso(a["created_at"]),
                                        updated_at        = DateTimeHelper.Iso(a["updated_at"]),
                                        sources           = new[] { "avtrdb" },
                                    }).ToList();
                                    vrcndbIds.AddRange(raw.Cast<JObject>().Select(a => a["vrc_id"]?.ToString() ?? a["id"]?.ToString() ?? ""));
                                }

                                foreach (var vid in vrcndbIds)
                                    if (vid.StartsWith("avtr_")) QueueVrcndbSubmit(vid);

                                var enriched = list.Select(o =>
                                {
                                    var jo = JObject.FromObject(o);
                                    CacheSearchAvatar(jo);
                                    EnrichAvatarFromCache(jo, jo["id"]?.ToString() ?? "");
                                    return jo;
                                }).ToList();

                                Invoke(() => SendToJS("vrcAvatarSearchResults", new
                                {
                                    results = enriched,
                                    page    = avSearchPage,
                                    hasMore = enriched.Count >= avLimit,
                                }));
                            }
                            catch (Exception ex)
                            {
                                Invoke(() => SendToJS("log", new { msg = $"Avatar search error: {ex.Message}", color = "err" }));
                            }
                        });
                    }
                    break;

                case "vrcCacheAvatarBatch":
                    if (msg["avatars"] is JArray batchArr)
                    {
                        var mapping = new Dictionary<string, string>();
                        var details = new Dictionary<string, object>();
                        foreach (var item in batchArr.OfType<JObject>())
                        {
                            var bid  = item["id"]?.ToString();
                            var bUrl = item["imageUrl"]?.ToString();
                            if (string.IsNullOrEmpty(bid)) continue;
                            mapping[bid] = ImageCacheHelper.GetAvatarUrl(bid, bUrl);
                            try
                            {
                                var d = _core.TimeEngine.GetAvatarDetail(bid);
                                if (d != null)
                                    details[bid] = new
                                    {
                                        authorName = d.AuthorName,
                                        releaseStatus = d.ReleaseStatus,
                                        performance = new { pc = d.PcPerf, quest = d.QuestPerf, ios = d.IosPerf },
                                    };
                            }
                            catch { }
                        }
                        SendToJS("vrcAvatarBatchCached", mapping);
                        if (details.Count > 0) SendToJS("vrcAvatarBatchDetails", details);
                    }
                    break;

                case "vrcCheckAvatars":
                {
                    // Without a VRChat session every lookup fails, which would mark
                    // the whole batch as deleted and report that upstream.
                    if (!_vrcApi.IsLoggedIn) break;
                    var ids = msg["ids"]?.ToObject<string[]>();
                    if (ids is { Length: > 0 })
                    {
                        // Return cached deleted IDs immediately
                        List<string> cachedDeleted;
                        lock (_deletedAvatarIds) cachedDeleted = ids.Where(id => _deletedAvatarIds.Contains(id)).ToList();
                        if (cachedDeleted.Count > 0)
                        {
                            Invoke(() => SendToJS("vrcAvatarsDeleted", new { ids = cachedDeleted }));

                            // Queue cached deleted IDs for batched report to avtrdb
                            if (_settings.AvtrdbReportDeleted)
                                QueueAvtrdbReport(cachedDeleted);
                            if (_settings.AvtrIcuReportDeleted)
                                _ = Task.Run(() => QueueAvtrIcuReport(cachedDeleted));
                            if (_settings.VrcndbReportDeleted)
                                QueueVrcndbRecheck(cachedDeleted);
                        }

                        // Skip IDs already cached in Avatar_Deletion or Avatar_User_Content (30-day TTL).
                        // Also de-duplicate concurrent checks within the same session.
                        string[] toCheck;
                        lock (_checkedAvatarIds)
                        {
                            toCheck = ids.Where(id =>
                                !AvtrdbCacheHelper.IsDeletedCached(id) &&
                                !AvtrdbCacheHelper.IsUserContentCached(id) &&
                                _checkedAvatarIds.Add(id)).ToArray();
                        }

                        if (toCheck.Length > 0)
                        {
                            _ = Task.Run(async () =>
                            {
                                var deleted = new List<string>();
                                var exists  = new List<string>();
                                var detailBatch = new Dictionary<string, object>();
                                void FlushDetails()
                                {
                                    if (detailBatch.Count == 0) return;
                                    var snapshot = new Dictionary<string, object>(detailBatch);
                                    detailBatch.Clear();
                                    Invoke(() => SendToJS("vrcAvatarDetailsBatch", snapshot));
                                }
                                foreach (var id in toCheck)
                                {
                                    if (!_vrcApi.IsLoggedIn) break;
                                    try
                                    {
                                        var av = await _core.Avatars.GetAvatarAsync(id);
                                        if (av == null) { deleted.Add(id); lock (_deletedAvatarIds) _deletedAvatarIds.Add(id); }
                                        else
                                        {
                                            exists.Add(id);
                                            CacheAvatarDetailFrom(av);
                                            var d = AvatarDetailPayload(av);
                                            if (d != null) detailBatch[id] = d;
                                            if (detailBatch.Count >= 4) FlushDetails();
                                        }
                                    }
                                    catch { deleted.Add(id); lock (_deletedAvatarIds) _deletedAvatarIds.Add(id); }
                                    await Task.Delay(250);
                                }
                                FlushDetails();
                                if (exists.Count > 0)
                                    AvtrdbCacheHelper.MarkUserContentBatch("", exists, "avtrdb");
                                if (deleted.Count > 0)
                                {
                                    AvtrdbCacheHelper.MarkDeletedBatch(deleted, "avtrdb");
                                    Invoke(() => SendToJS("vrcAvatarsDeleted", new { ids = deleted }));

                                    if (_settings.AvtrdbReportDeleted)
                                        QueueAvtrdbReport(deleted);
                                    if (_settings.AvtrIcuReportDeleted)
                                        _ = Task.Run(() => QueueAvtrIcuReport(deleted));
                                    if (_settings.VrcndbReportDeleted)
                                        QueueVrcndbRecheck(deleted);
                                }
                            });
                        }
                    }
                    break;
                }

                // Search - users, worlds, groups
                case "vrcSearchUsers":
                    var uQ = msg["query"]?.ToString() ?? "";
                    var uOff = msg["offset"]?.Value<int>() ?? 0;
                    _ = Task.Run(async () =>
                    {
                        var res = await _core.Users.SearchUsersAsync(uQ, 20, uOff);
                        foreach (var did in res.OfType<JObject>()
                                     .SelectMany(u => new[] { u["iconFrame"]?.ToString(), u["nameplateEffect"]?.ToString(), u["profileEffect"]?.ToString() })
                                     .Where(x => !string.IsNullOrEmpty(x)).Distinct())
                            await _core.Inventory.ResolveDecorationAsync(did!);
                        var list = res.Cast<JObject>().Select(u => {
                        var uEnrichId = u["id"]?.ToString() ?? "";
                        var uObj = JObject.FromObject(new {
                            id = u["id"]?.ToString() ?? "", displayName = u["displayName"]?.ToString() ?? "",
                            image = ImageCacheHelper.GetUserUrl(u["id"]?.ToString(), VRChatApiService.GetUserImage(u)), status = u["status"]?.ToString() ?? "offline",
                            statusDescription = u["statusDescription"]?.ToString() ?? "", bio = u["bio"]?.ToString() ?? "",
                            isFriend = u["isFriend"]?.Value<bool>() ?? false,
                            bioLinks = u["bioLinks"]?.ToObject<List<string>>() ?? new(),
                            pronouns = u["pronouns"]?.ToString() ?? "",
                            platform = u["last_platform"]?.ToString() ?? "",
                            location = u["location"]?.ToString() ?? "",
                            iconFrame = u["iconFrame"]?.ToString() ?? "",
                            iconFrameUrl = IconFrameHelper.UrlFor(u["iconFrame"]?.ToString(), _core.Inventory),
                            nameplateEffect = u["nameplateEffect"]?.ToString() ?? "",
                            nameplateUrl = IconFrameHelper.UrlFor(u["nameplateEffect"]?.ToString(), _core.Inventory),
                            profileEffect = u["profileEffect"]?.ToString() ?? "",
                            profileEffectUrl = IconFrameHelper.UrlFor(u["profileEffect"]?.ToString(), _core.Inventory),
                        });
                        _friends.EnrichFromProfileCache(uObj, uEnrichId, true);
                        return uObj;
                        }).ToList();
                        Invoke(() => SendToJS("vrcSearchResults", new { type = "users", results = list, offset = uOff, hasMore = list.Count >= 20 }));
                    });
                    break;

                case "vrcSearchWorlds":
                    var wQ = msg["query"]?.ToString() ?? "";
                    var wOff = msg["offset"]?.Value<int>() ?? 0;
                    var wSort = msg["sort"]?.ToString() ?? "relevance";
                    _ = Task.Run(async () =>
                    {
                        var res = await _core.World.SearchWorldsAsync(wQ, 20, wOff, wSort);
                        var list = res.Cast<JObject>().Select(w => {
                            var wid2 = w["id"]?.ToString() ?? "";
                            var wurl = ImageCacheHelper.GetWorldUrl(wid2, w["imageUrl"]?.ToString() ?? w["thumbnailImageUrl"]?.ToString());
                            var wStats = _core.TimeEngine.GetWorldStats(wid2);
                            EnrichWorldDatesFromCache(_core.TimeEngine, w, wid2);
                            return new {
                            id = wid2, name = w["name"]?.ToString() ?? "",
                            imageUrl = wurl, thumbnailImageUrl = wurl,
                            authorName = w["authorName"]?.ToString() ?? "", occupants = w["occupants"]?.Value<int>() ?? 0,
                            capacity = w["capacity"]?.Value<int>() ?? 0, favorites = w["favorites"]?.Value<int>() ?? 0,
                            visits = w["visits"]?.Value<int>() ?? 0, description = w["description"]?.ToString() ?? "",
                            tags = w["tags"]?.ToObject<List<string>>() ?? new(),
                            created_at = DateTimeHelper.Iso(w["created_at"]),
                            updated_at = DateTimeHelper.Iso(w["updated_at"]),
                            worldTimeSeconds = wStats.totalSeconds,
                            worldVisitCount  = wStats.visitCount,
                            worldLastVisited = wStats.lastVisited,
                            };
                        }).ToList();
                        Invoke(() => SendToJS("vrcSearchResults", new { type = "worlds", results = list, offset = wOff, hasMore = list.Count >= 20 }));
                    });
                    break;

                case "vrcSearchGroups":
                    await _groups.HandleMessage(action, msg);
                    break;

                case "vrcGetWorldDetail":
                    var wdId = msg["worldId"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(wdId))
                    {
                        // Serve from DB cache immediately if available
                        var wdCached = _core.TimeEngine.GetWorldDetail(wdId);
                        if (wdCached != null)
                        {
                            Invoke(() => SendToJS("vrcWorldDetail", new
                            {
                                id                  = wdId,
                                name                = wdCached.WorldName,
                                description         = wdCached.Description,
                                imageUrl            = ImageCacheHelper.GetWorldUrl(wdId, wdCached.ImageUrl),
                                thumbnailImageUrl   = ImageCacheHelper.GetWorldUrl(wdId, wdCached.WorldThumb),
                                authorName          = wdCached.AuthorName,
                                authorId            = wdCached.AuthorId,
                                occupants           = 0,
                                publicOccupants     = wdCached.PublicOccupants,
                                privateOccupants    = wdCached.PrivateOccupants,
                                heat                = wdCached.Heat,
                                popularity          = wdCached.Popularity,
                                version             = wdCached.Version,
                                capacity            = wdCached.Capacity,
                                recommendedCapacity = wdCached.RecommendedCapacity,
                                favorites           = wdCached.Favorites,
                                visits              = wdCached.Visits,
                                createdAt           = wdCached.Published,
                                updatedAt           = wdCached.Updated,
                                pcSize              = wdCached.PcSize,
                                androidSize         = wdCached.AndroidSize,
                                iosSize             = wdCached.IosSize,
                                tags                = wdCached.Tags,
                                instances           = new List<object>(),
                                worldTimeSeconds    = wdCached.TotalSeconds,
                                worldVisitCount     = wdCached.VisitCount,
                                fromCache           = true,
                            }));
                        }

                        _ = Task.Run(async () =>
                        {
                            static string StripNonce(string l) =>
                                System.Text.RegularExpressions.Regex.Replace(l ?? "", @"~nonce\([^)]*\)", "");

                            var world = await _core.World.GetWorldFreshAsync(wdId);
                            if (world == null)
                            {
                                Invoke(() => SendToJS("vrcWorldDetailError", new { error = "Could not load world" }));
                                return;
                            }
                            // Helper: parse owner ID (usr_xxx or grp_xxx) from instance ID string
                            static string ParseOwnerId(string instId) {
                                var m = System.Text.RegularExpressions.Regex.Match(instId, @"~(?:friends|hidden|private|group)\(([^)]+)\)");
                                return m.Success ? m.Groups[1].Value : "";
                            }

                            // Phase 1 — build raw list with ownerIds
                            var rawInstances = new List<(string instanceId, int users, string type, string region, string location, string ownerId, bool ageGate, Dictionary<string,double> languageRatio)>();
                            var knownLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            var instArr = world["instances"] as JArray;
                            if (instArr != null)
                            {
                                foreach (var inst in instArr)
                                {
                                    if (inst is JArray pair && pair.Count >= 2)
                                    {
                                        var instId = pair[0]?.ToString() ?? "";
                                        var users = pair[1]?.Value<int>() ?? 0;
                                        var langRatio = pair.Count >= 3 && pair[2] is JObject lr
                                            ? lr.ToObject<Dictionary<string, double>>() ?? new()
                                            : new Dictionary<string, double>();
                                        var (_, _, instType) = VRChatApiService.ParseLocation($"{wdId}:{instId}");
                                        // ~canRequestInvite in raw instance IDs (from world's instances array) is the instance type flag
                                        if (instType == "private" && instId.Contains("~canRequestInvite")) instType = "invite_plus";
                                        var regionMatch = System.Text.RegularExpressions.Regex.Match(instId, @"region\(([^)]+)\)");
                                        var region = regionMatch.Success ? regionMatch.Groups[1].Value : "us";
                                        var loc = $"{wdId}:{instId}";
                                        rawInstances.Add((instId, users, instType, region, loc, ParseOwnerId(instId), instId.Contains("~ageGate"), langRatio));
                                        knownLocations.Add(loc);
                                    }
                                }
                            }
                            // Find friend locations in this world not covered by the world API instances
                            var storeSnapshot = _friends.GetStoreSnapshot();
                            var friendLocs = storeSnapshot
                                .Select(f => f["location"]?.ToString() ?? "")
                                .Where(loc => loc.StartsWith(wdId + ":"))
                                .Distinct()
                                .Where(loc => !knownLocations.Contains(StripNonce(loc)))
                                .ToList();
                            // Fetch real user counts for friend-inferred instances in parallel
                            if (friendLocs.Count > 0)
                            {
                                var instTasks = friendLocs.Select(loc => _core.Instances.GetInstanceAsync(loc)).ToArray();
                                var instResults = await Task.WhenAll(instTasks);
                                for (int i = 0; i < friendLocs.Count; i++)
                                {
                                    var loc = friendLocs[i];
                                    var instData = instResults[i];
                                    var nUsers = instData?["n_users"]?.Value<int>() ?? instData?["userCount"]?.Value<int>() ?? 0;
                                    var (_, instId2, instType2) = VRChatApiService.ParseLocation(loc);
                                    // Use instance API canRequestInvite to distinguish Invite from Invite+
                                    var instType2Final = instType2 == "private" && instData?["canRequestInvite"]?.Value<bool>() == true ? "invite_plus" : instType2;
                                    var regionMatch2 = System.Text.RegularExpressions.Regex.Match(instId2, @"region\(([^)]+)\)");
                                    var region2 = regionMatch2.Success ? regionMatch2.Groups[1].Value : "us";
                                    rawInstances.Add((instId2, nUsers, instType2Final, region2, loc, ParseOwnerId(instId2), instId2.Contains("~ageGate"), new Dictionary<string, double>()));
                                }
                            }

                            // Phase 2 — resolve owner names
                            // Batch-fetch group names for any grp_ owners
                            var uniqueGroupIds = rawInstances
                                .Where(r => r.ownerId.StartsWith("grp_"))
                                .Select(r => r.ownerId).Distinct().ToList();
                            var groupInfoMap = new Dictionary<string, (string name, string shortCode)>();
                            if (uniqueGroupIds.Count > 0)
                            {
                                // Check DB cache first — populated when group was opened manually or on first world-modal load
                                var uncachedGroupIds = new List<string>();
                                foreach (var gid in uniqueGroupIds)
                                {
                                    var cached = _core.TimeEngine.GetGroupDetail(gid);
                                    if (cached != null && !string.IsNullOrEmpty(cached.Name))
                                        groupInfoMap[gid] = (cached.Name, cached.ShortCode);
                                    else
                                        uncachedGroupIds.Add(gid);
                                }
                                // Only fetch from API for groups not yet in DB
                                if (uncachedGroupIds.Count > 0)
                                {
                                    var gTasks = uncachedGroupIds.ToDictionary(id => id, id => _core.Groups.GetGroupAsync(id));
                                    try { await Task.WhenAll(gTasks.Values); } catch { }
                                    foreach (var kv in gTasks)
                                    {
                                        if (kv.Value.IsFaulted || kv.Value.Result == null) continue;
                                        var g = kv.Value.Result;
                                        var gName  = g["name"]?.ToString()      ?? "";
                                        var gShort = g["shortCode"]?.ToString() ?? "";
                                        groupInfoMap[kv.Key] = (gName, gShort);
                                        // Persist to DB so future world-modal opens skip the API call
                                        _core.TimeEngine.SaveGroupDetail(kv.Key, gName, gShort,
                                            g["description"]?.ToString()                       ?? "",
                                            g["iconUrl"]?.ToString()                           ?? "",
                                            g["bannerUrl"]?.ToString()                         ?? "",
                                            g["memberCount"]?.Value<int>()                     ?? 0,
                                            g["privacy"]?.ToString()                           ?? "",
                                            g["joinState"]?.ToString()                         ?? "",
                                            g["ownerId"]?.ToString()                           ?? "",
                                            g["ownerDisplayName"]?.ToString()                  ?? "",
                                            g["rules"]?.ToString()                             ?? "",
                                            g["languages"]?.ToObject<List<string>>()           ?? new(),
                                            g["links"]?.ToObject<List<string>>()               ?? new());
                                    }
                                }
                            }
                            var instances = rawInstances.Select(r => {
                                var ownerName = "";
                                var ownerGroup = "";
                                if (r.ownerId.StartsWith("usr_"))
                                    { var f = _friends.GetStoreValue(r.ownerId); ownerName = f?["displayName"]?.ToString() ?? ""; }
                                else if (r.ownerId.StartsWith("grp_") && groupInfoMap.TryGetValue(r.ownerId, out var info))
                                    (ownerName, ownerGroup) = info;
                                return new { instanceId = r.instanceId, users = r.users, type = r.type, region = r.region, location = r.location, ownerName, ownerGroup, ownerId = r.ownerId, ageGate = r.ageGate, languageRatio = r.languageRatio };
                            }).ToList<object>();
                            var tags = world["tags"]?.ToObject<List<string>>() ?? new();
                            var (wTimeSeconds, wVisitCount, wLastVisited) = _core.TimeEngine.GetWorldStats(world["id"]?.ToString() ?? "");

                            // Extract PC and Android download sizes via /file/{fileId} API.
                            // assetUrl pattern: .../file/{fileId}/{version}/file
                            static (string fileId, int version) ParseAssetUrl(string url)
                            {
                                var m = System.Text.RegularExpressions.Regex.Match(url, @"/file/(file_[^/]+)/(\d+)/");
                                return m.Success ? (m.Groups[1].Value, int.Parse(m.Groups[2].Value)) : ("", 0);
                            }
                            static long ExtractSizeFromFile(JObject? fileObj, int version)
                            {
                                if (fileObj == null) return 0;
                                var versions = fileObj["versions"] as JArray;
                                if (versions == null) return 0;
                                foreach (var v in versions)
                                {
                                    if (v["version"]?.Value<int>() == version)
                                        return v["file"]?["sizeInBytes"]?.Value<long>() ?? 0;
                                }
                                return 0;
                            }

                            string pcAssetUrl = "", androidAssetUrl = "", iosAssetUrl = "";
                            var unityPkgs = world["unityPackages"] as JArray ?? new JArray();
                            foreach (var pkg in unityPkgs)
                            {
                                var platform = pkg["platform"]?.ToString() ?? "";
                                var url = pkg["assetUrl"]?.ToString() ?? "";
                                if (string.IsNullOrEmpty(url)) continue;
                                if (platform == "standalonewindows" && string.IsNullOrEmpty(pcAssetUrl)) pcAssetUrl = url;
                                else if (platform == "android" && string.IsNullOrEmpty(androidAssetUrl)) androidAssetUrl = url;
                                else if (platform == "ios" && string.IsNullOrEmpty(iosAssetUrl)) iosAssetUrl = url;
                            }

                            var (pcFileId, pcVer) = ParseAssetUrl(pcAssetUrl);
                            var (andFileId, andVer) = ParseAssetUrl(androidAssetUrl);
                            var (iosFileId, iosVer) = ParseAssetUrl(iosAssetUrl);

                            // Use cached sizes when world version hasn't changed — skips file API calls.
                            var newVersion = world["version"]?.Value<int>() ?? 0;
                            var sizeCache  = _core.TimeEngine.GetWorldDetail(world["id"]?.ToString() ?? "");
                            long pcSize, androidSize, iosSize;
                            if (sizeCache != null && sizeCache.Version == newVersion
                                && (sizeCache.PcSize > 0 || sizeCache.AndroidSize > 0 || sizeCache.IosSize > 0))
                            {
                                pcSize      = sizeCache.PcSize;
                                androidSize = sizeCache.AndroidSize;
                                iosSize     = sizeCache.IosSize;
                            }
                            else
                            {
                                JObject? pcFileObj = null, andFileObj = null, iosFileObj = null;
                                var fileTasks = new List<Task>();
                                if (!string.IsNullOrEmpty(pcFileId))
                                    fileTasks.Add(Task.Run(async () => pcFileObj = await _core.Files.GetFileAsync(pcFileId)));
                                if (!string.IsNullOrEmpty(andFileId) && andFileId != pcFileId)
                                    fileTasks.Add(Task.Run(async () => andFileObj = await _core.Files.GetFileAsync(andFileId)));
                                else if (andFileId == pcFileId)
                                    fileTasks.Add(Task.Run(() => { andFileObj = pcFileObj; return Task.CompletedTask; }));
                                if (!string.IsNullOrEmpty(iosFileId) && iosFileId != pcFileId && iosFileId != andFileId)
                                    fileTasks.Add(Task.Run(async () => iosFileObj = await _core.Files.GetFileAsync(iosFileId)));
                                else if (iosFileId == pcFileId)
                                    fileTasks.Add(Task.Run(() => { iosFileObj = pcFileObj; return Task.CompletedTask; }));
                                else if (iosFileId == andFileId)
                                    fileTasks.Add(Task.Run(() => { iosFileObj = andFileObj; return Task.CompletedTask; }));
                                if (fileTasks.Count > 0) await Task.WhenAll(fileTasks);
                                pcSize      = ExtractSizeFromFile(pcFileObj, pcVer);
                                androidSize = ExtractSizeFromFile(andFileObj, andVer);
                                iosSize     = ExtractSizeFromFile(iosFileObj, iosVer);
                            }
                            static string ToIso(JToken? t)
                            {
                                var s = t?.ToString();
                                if (string.IsNullOrEmpty(s)) return "";
                                if (DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                                    return dt.ToUniversalTime().ToString("yyyy-MM-dd");
                                return "";
                            }
                            _core.TimeEngine.SaveWorldDetail(
                                worldId:             world["id"]?.ToString() ?? "",
                                name:                world["name"]?.ToString() ?? "",
                                thumb:               world["thumbnailImageUrl"]?.ToString() ?? "",
                                description:         world["description"]?.ToString() ?? "",
                                imageUrl:            world["imageUrl"]?.ToString() ?? "",
                                authorName:          world["authorName"]?.ToString() ?? "",
                                authorId:            world["authorId"]?.ToString() ?? "",
                                published:           ToIso(world["created_at"]),
                                updated:             ToIso(world["updated_at"]),
                                capacity:            world["capacity"]?.Value<int>() ?? 0,
                                recommendedCapacity: world["recommendedCapacity"]?.Value<int>() ?? 0,
                                tags:                tags,
                                favorites:           world["favorites"]?.Value<int>() ?? 0,
                                visits:              world["visits"]?.Value<int>() ?? 0,
                                pcSize:              pcSize,
                                androidSize:         androidSize,
                                iosSize:             iosSize,
                                heat:                world["heat"]?.Value<int>() ?? 0,
                                popularity:          world["popularity"]?.Value<int>() ?? 0,
                                publicOccupants:     world["publicOccupants"]?.Value<int>() ?? 0,
                                privateOccupants:    world["privateOccupants"]?.Value<int>() ?? 0,
                                version:             world["version"]?.Value<int>() ?? 0
                            );
                            Invoke(() => SendToJS("vrcWorldDetail", new
                            {
                                id = world["id"]?.ToString() ?? "",
                                name = world["name"]?.ToString() ?? "",
                                description = world["description"]?.ToString() ?? "",
                                imageUrl = ImageCacheHelper.GetWorldUrl(world["id"]?.ToString(), world["imageUrl"]?.ToString()),
                                thumbnailImageUrl = world["thumbnailImageUrl"]?.ToString() ?? "",
                                authorName = world["authorName"]?.ToString() ?? "",
                                authorId = world["authorId"]?.ToString() ?? "",
                                occupants = world["occupants"]?.Value<int>() ?? 0,
                                publicOccupants = world["publicOccupants"]?.Value<int>() ?? 0,
                                privateOccupants = world["privateOccupants"]?.Value<int>() ?? 0,
                                heat = world["heat"]?.Value<int>() ?? 0,
                                popularity = world["popularity"]?.Value<int>() ?? 0,
                                version = world["version"]?.Value<int>() ?? 0,
                                capacity = world["capacity"]?.Value<int>() ?? 0,
                                recommendedCapacity = world["recommendedCapacity"]?.Value<int>() ?? 0,
                                favorites = world["favorites"]?.Value<int>() ?? 0,
                                visits = world["visits"]?.Value<int>() ?? 0,
                                createdAt = ToIso(world["created_at"]),
                                updatedAt = ToIso(world["updated_at"]),
                                pcSize,
                                androidSize,
                                iosSize,
                                tags,
                                instances,
                                worldTimeSeconds = wTimeSeconds,
                                worldVisitCount = wVisitCount,
                                rawJson = world,
                            }));
                        });
                    }
                    break;

                case "vrcGetOnlineCount":
                    await _instance.HandleMessage(action, msg);
                    break;

                case "vrcUpdateAvatar":
                {
                    var avId     = msg["avatarId"]?.ToString()             ?? "";
                    var avName   = msg["name"]?.ToString()                 ?? "";
                    var avDesc   = msg["description"]?.ToString()          ?? "";
                    var avStatus = msg["releaseStatus"]?.ToString()        ?? "private";
                    var avTags   = msg["tags"]?.ToObject<List<string>>()   ?? new();
                    if (!string.IsNullOrEmpty(avId))
                        _ = Task.Run(async () =>
                        {
                            var (ok, error) = await _core.Avatars.UpdateAvatarAsync(avId, avName, avDesc, avStatus, avTags);
                            if (ok)
                            {
                                var ex = _core.TimeEngine.GetAvatarDetail(avId);
                                if (ex != null)
                                {
                                    _core.TimeEngine.SaveAvatarDetail(
                                        avId, avName, ex.AuthorName, ex.AuthorId,
                                        ex.ThumbnailImageUrl, ex.ImageUrl,
                                        avStatus, ex.Version,
                                        DateTimeHelper.Iso(ex.CreatedAt), DateTimeHelper.Iso(ex.UpdatedAt),
                                        avDesc, avTags,
                                        ex.HasPC, ex.HasQuest, ex.HasImpostor,
                                        ex.PcPerf, ex.QuestPerf);
                                    ModalCacheHelper.Invalidate(avId);
                                }
                            }
                            Invoke(() => SendToJS("vrcAvatarUpdateResult", new
                            {
                                ok,
                                error,
                                name          = ok ? avName   : (string?)null,
                                description   = ok ? avDesc   : (string?)null,
                                releaseStatus = ok ? avStatus : (string?)null,
                                tags          = ok ? avTags   : (List<string>?)null,
                            }));
                        });
                    break;
                }

                case "vrcGetAvatarDetail":
                {
                    var avdId = msg["avatarId"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(avdId))
                    {
                        if (msg["force"]?.Value<bool>() == true) ModalCacheHelper.Invalidate(avdId);
                        var avdCached = _core.TimeEngine.GetAvatarDetail(avdId);
                        if (avdCached != null)
                            Invoke(() => SendToJS("vrcAvatarDetail", new {
                                id = avdId, name = avdCached.Name, authorName = avdCached.AuthorName,
                                authorId = avdCached.AuthorId, thumbnailImageUrl = ImageCacheHelper.GetAvatarUrl(avdId, avdCached.ThumbnailImageUrl),
                                imageUrl = ImageCacheHelper.GetAvatarUrl(avdId, avdCached.ImageUrl), releaseStatus = avdCached.ReleaseStatus,
                                version = avdCached.Version, created_at = DateTimeHelper.Iso(avdCached.CreatedAt),
                                updated_at = DateTimeHelper.Iso(avdCached.UpdatedAt), description = avdCached.Description,
                                tags = avdCached.Tags, hasPC = avdCached.HasPC, hasQuest = avdCached.HasQuest,
                                hasIos = avdCached.HasIos,
                                hasImpostor = avdCached.HasImpostor,
                                pcPerf = ValidPerf(avdCached.PcPerf), questPerf = ValidPerf(avdCached.QuestPerf),
                                iosPerf = ValidPerf(avdCached.IosPerf),
                            }));
                        SendCachedAvatarAnalysis(avdId);
                        if (ModalCacheHelper.IsCached(avdId)) break;
                        ModalCacheHelper.Mark(avdId);
                        _ = Task.Run(async () =>
                        {
                            var avatar = await _core.Avatars.GetAvatarAsync(avdId);
                            if (avatar == null)
                            {
                                Invoke(() => SendToJS("vrcAvatarDetailError", new { error = "Could not load avatar" }));
                                return;
                            }
                            var packages = avatar["unityPackages"] as JArray ?? new JArray();
                            var realPkgs = packages.Where(p => p["variant"]?.ToString() != "impostor").ToList();
                            var hasPC    = realPkgs.Any(p => p["platform"]?.ToString() == "standalonewindows");
                            var hasQuest = realPkgs.Any(p => p["platform"]?.ToString() == "android");
                            var hasIos   = realPkgs.Any(p => p["platform"]?.ToString() == "ios");
                            var hasImpostor = packages.Any(p => p["variant"]?.ToString() == "impostor");
                            var (pcPerf, questPerf, iosPerf) = ResolveAvatarPerf(avatar);
                            // Save immediately so future opens are instant from DB
                            var avtSaveId = avatar["id"]?.ToString() ?? avdId;
                            _core.TimeEngine.SaveAvatarDetail(
                                avtSaveId,
                                avatar["name"]?.ToString() ?? "",
                                avatar["authorName"]?.ToString() ?? "",
                                avatar["authorId"]?.ToString() ?? "",
                                avatar["thumbnailImageUrl"]?.ToString() ?? "",
                                avatar["imageUrl"]?.ToString() ?? "",
                                avatar["releaseStatus"]?.ToString() ?? "",
                                avatar["version"]?.Value<int>() ?? 0,
                                DateTimeHelper.Iso(avatar["created_at"]),
                                DateTimeHelper.Iso(avatar["updated_at"]),
                                avatar["description"]?.ToString() ?? "",
                                avatar["tags"]?.ToObject<List<string>>() ?? new(),
                                hasPC, hasQuest, hasImpostor, pcPerf, questPerf, hasIos, iosPerf);
                            Invoke(() => SendToJS("vrcAvatarDetail", new
                            {
                                id               = avatar["id"]?.ToString()                  ?? "",
                                name             = avatar["name"]?.ToString()                ?? "",
                                authorName       = avatar["authorName"]?.ToString()          ?? "",
                                authorId         = avatar["authorId"]?.ToString()            ?? "",
                                thumbnailImageUrl = ImageCacheHelper.GetAvatarUrl(avatar["id"]?.ToString(), avatar["thumbnailImageUrl"]?.ToString() ?? avatar["imageUrl"]?.ToString()),
                                imageUrl         = ImageCacheHelper.GetAvatarUrl(avatar["id"]?.ToString(), avatar["imageUrl"]?.ToString() ?? avatar["thumbnailImageUrl"]?.ToString()),
                                releaseStatus    = avatar["releaseStatus"]?.ToString()       ?? "",
                                version          = avatar["version"]?.Value<int>()           ?? 0,
                                created_at       = DateTimeHelper.Iso(avatar["created_at"]),
                                updated_at       = DateTimeHelper.Iso(avatar["updated_at"]),
                                description      = avatar["description"]?.ToString()         ?? "",
                                tags             = avatar["tags"]?.ToObject<List<string>>()  ?? new(),
                                hasPC,
                                hasQuest,
                                hasIos,
                                hasImpostor,
                                pcPerf,
                                questPerf,
                                iosPerf,
                                rawJson = avatar,
                            }));
                            await FetchAvatarAnalysisAsync(avatar);
                        });
                    }
                    break;
                }

                case "vrcGetAvatarGallery":
                {
                    var galAvId = msg["avatarId"]?.ToString() ?? "";
                    _ = Task.Run(async () =>
                    {
                        var gallery = await _core.Avatars.GetAvatarGalleryAsync(galAvId);
                        var images = gallery.OfType<JObject>().Select(f =>
                        {
                            var vers = f["versions"] as JArray ?? new JArray();
                            var latest = vers.OfType<JObject>().LastOrDefault(v => v["status"]?.ToString() == "complete")
                                ?? vers.OfType<JObject>().LastOrDefault();
                            return new {
                                id = f["id"]?.ToString() ?? "",
                                name = f["name"]?.ToString() ?? "",
                                url = latest?["file"]?["url"]?.ToString() ?? "",
                                sizeBytes = latest?["file"]?["sizeInBytes"]?.Value<long>() ?? 0L,
                                createdAt = f["created_at"]?.ToString() ?? ""
                            };
                        }).Where(x => !string.IsNullOrEmpty(x.url)).ToList();
                        Invoke(() => SendToJS("vrcAvatarGallery", new { avatarId = galAvId, images }));
                    });
                    break;
                }

                case "vrcUploadAvatarGallery":
                {
                    var galUpAvId = msg["avatarId"]?.ToString() ?? "";
                    var galDataB64 = msg["data"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(galDataB64))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var galRaw = galDataB64.Contains(",") ? galDataB64.Split(',')[1] : galDataB64;
                                var bytes = Convert.FromBase64String(galRaw);
                                var (ok, error) = await _core.Avatars.UploadAvatarGalleryImageAsync(galUpAvId, bytes);
                                if (ok)
                                {
                                    var gallery = await _core.Avatars.GetAvatarGalleryAsync(galUpAvId);
                                    var images = gallery.OfType<JObject>().Select(f =>
                                    {
                                        var vers = f["versions"] as JArray ?? new JArray();
                                        var latest = vers.OfType<JObject>().LastOrDefault(v => v["status"]?.ToString() == "complete")
                                            ?? vers.OfType<JObject>().LastOrDefault();
                                        return new {
                                            id = f["id"]?.ToString() ?? "",
                                            name = f["name"]?.ToString() ?? "",
                                            url = latest?["file"]?["url"]?.ToString() ?? "",
                                            sizeBytes = latest?["file"]?["sizeInBytes"]?.Value<long>() ?? 0L,
                                            createdAt = f["created_at"]?.ToString() ?? ""
                                        };
                                    }).Where(x => !string.IsNullOrEmpty(x.url)).ToList();
                                    Invoke(() => SendToJS("vrcAvatarGalleryResult", new { ok = true, avatarId = galUpAvId, images }));
                                }
                                else
                                {
                                    Invoke(() => SendToJS("vrcAvatarGalleryResult", new { ok = false, avatarId = galUpAvId, error }));
                                }
                            }
                            catch (Exception ex)
                            {
                                Invoke(() => SendToJS("vrcAvatarGalleryResult", new { ok = false, avatarId = galUpAvId, error = ex.Message }));
                            }
                        });
                    }
                    break;
                }

                case "vrcDeleteAvatar":
                {
                    var delAvId = msg["avatarId"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(delAvId))
                        _ = Task.Run(async () =>
                        {
                            var (ok, error) = await _core.Avatars.DeleteAvatarAsync(delAvId);
                            if (ok)
                            {
                                ModalCacheHelper.Invalidate(delAvId);
                                RemoveFromCachedList(CacheHandler.KeyAvatars, "avatars", delAvId);
                                await _authCtrl.FetchAndCacheAvatarsAsync();
                            }
                            Invoke(() => SendToJS("vrcAvatarDeleteResult", new { ok, error, avatarId = delAvId }));
                        });
                    break;
                }

                case "vrcDeleteWorld":
                {
                    var delWId = msg["worldId"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(delWId))
                        _ = Task.Run(async () =>
                        {
                            var (ok, error) = await _core.World.DeleteWorldAsync(delWId);
                            if (ok) ModalCacheHelper.Invalidate(delWId);
                            Invoke(() => SendToJS("vrcWorldDeleteResult", new { ok, error, worldId = delWId }));
                        });
                    break;
                }

                case "vrcDeleteGroup":
                {
                    var delGId = msg["groupId"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(delGId))
                        _ = Task.Run(async () =>
                        {
                            var (ok, error) = await _core.Groups.DeleteGroupAsync(delGId);
                            if (ok)
                            {
                                ModalCacheHelper.Invalidate(delGId);
                                _groups.MarkDeleted(delGId);
                            }
                            Invoke(() => SendToJS("vrcGroupDeleteResult", new { ok, error, groupId = delGId }));
                        });
                    break;
                }

                case "vrcUpdateWorld":
                {
                    var wuId   = msg["worldId"]?.ToString()               ?? "";
                    var wuName = msg["name"]?.ToString()                  ?? "";
                    var wuDesc = msg["description"]?.ToString()           ?? "";
                    var wuTags = msg["tags"]?.ToObject<List<string>>()    ?? new();
                    if (!string.IsNullOrEmpty(wuId))
                        _ = Task.Run(async () =>
                        {
                            var (ok, error) = await _core.World.UpdateWorldAsync(wuId, wuName, wuDesc, wuTags);
                            if (ok)
                            {
                                var ex = _core.TimeEngine.GetWorldDetail(wuId);
                                if (ex != null)
                                {
                                    _core.TimeEngine.SaveWorldDetail(
                                        worldId:             wuId,
                                        name:                wuName,
                                        thumb:               ex.WorldThumb,
                                        description:         wuDesc,
                                        imageUrl:            ex.ImageUrl,
                                        authorName:          ex.AuthorName,
                                        authorId:            ex.AuthorId,
                                        published:           ex.Published,
                                        updated:             ex.Updated,
                                        capacity:            ex.Capacity,
                                        recommendedCapacity: ex.RecommendedCapacity,
                                        tags:                wuTags,
                                        favorites:           ex.Favorites,
                                        visits:              ex.Visits,
                                        pcSize:              ex.PcSize,
                                        androidSize:         ex.AndroidSize,
                                        iosSize:             ex.IosSize,
                                        heat:                ex.Heat,
                                        popularity:          ex.Popularity,
                                        publicOccupants:     ex.PublicOccupants,
                                        privateOccupants:    ex.PrivateOccupants,
                                        version:             ex.Version);
                                }
                                ModalCacheHelper.Invalidate(wuId);
                            }
                            Invoke(() => SendToJS("vrcWorldUpdateResult", new
                            {
                                ok,
                                error,
                                worldId     = wuId,
                                name        = ok ? wuName : (string?)null,
                                description = ok ? wuDesc : (string?)null,
                                tags        = ok ? wuTags : (List<string>?)null,
                            }));
                        });
                    break;
                }

                case "vrcUploadWorldImage":
                {
                    var wImgId     = msg["worldId"]?.ToString() ?? "";
                    var wImgDataB64 = msg["data"]?.ToString()   ?? "";
                    if (!string.IsNullOrEmpty(wImgId) && !string.IsNullOrEmpty(wImgDataB64))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var rawWorld = await _core.World.GetWorldFreshAsync(wImgId);
                                var rawImageUrl = rawWorld?["imageUrl"]?.ToString()
                                    ?? rawWorld?["thumbnailImageUrl"]?.ToString() ?? "";
                                if (string.IsNullOrEmpty(rawImageUrl))
                                {
                                    Invoke(() => SendToJS("vrcWorldImageResult", new { ok = false, worldId = wImgId, imageUrl = "", error = "Could not retrieve world image URL" }));
                                    return;
                                }
                                var imgRaw = wImgDataB64.Contains(",") ? wImgDataB64.Split(',')[1] : wImgDataB64;
                                var bytes = Convert.FromBase64String(imgRaw);
                                var (ok, imageUrl, error) = await _core.World.UploadWorldMainImageAsync(wImgId, rawImageUrl, bytes);
                                if (ok) ModalCacheHelper.Invalidate(wImgId);
                                Invoke(() => SendToJS("vrcWorldImageResult", new { ok, worldId = wImgId, imageUrl, error }));
                            }
                            catch (Exception ex)
                            {
                                Invoke(() => SendToJS("vrcWorldImageResult", new { ok = false, worldId = wImgId, imageUrl = "", error = ex.Message }));
                            }
                        });
                    }
                    break;
                }

                case "vrcUploadAvatarImage":
                {
                    var imgAvId = msg["avatarId"]?.ToString() ?? "";
                    var imgDataB64 = msg["data"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(imgDataB64))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                // Fetch raw avatar to get the real VRChat imageUrl (not the local cache URL)
                                var rawAvatar = await _core.Avatars.GetAvatarAsync(imgAvId);
                                var rawImageUrl = rawAvatar?["imageUrl"]?.ToString()
                                    ?? rawAvatar?["thumbnailImageUrl"]?.ToString() ?? "";
                                if (string.IsNullOrEmpty(rawImageUrl))
                                {
                                    Invoke(() => SendToJS("vrcAvatarImageResult", new { ok = false, avatarId = imgAvId, imageUrl = "", error = "Could not retrieve avatar image URL" }));
                                    return;
                                }
                                var imgRaw = imgDataB64.Contains(",") ? imgDataB64.Split(',')[1] : imgDataB64;
                                var bytes = Convert.FromBase64String(imgRaw);
                                var (ok, imageUrl, error) = await _core.Avatars.UploadAvatarMainImageAsync(imgAvId, rawImageUrl, bytes);
                                Invoke(() => SendToJS("vrcAvatarImageResult", new { ok, avatarId = imgAvId, imageUrl, error }));
                            }
                            catch (Exception ex)
                            {
                                Invoke(() => SendToJS("vrcAvatarImageResult", new { ok = false, avatarId = imgAvId, imageUrl = "", error = ex.Message }));
                            }
                        });
                    }
                    break;
                }

                // Shared Content Info (for Messenger content cards)
                case "vrcGetSharedContentInfo":
                {
                    var scId   = msg["contentId"]?.ToString() ?? "";
                    var scType = msg["contentType"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(scId))
                    {
                        _ = Task.Run(async () =>
                        {
                            var scc = GetSharedContentCache();

                            string name = "", rawImage = "";
                            bool fromCache = false;

                            // Check disk cache — skip API call only if we already have the image URL
                            lock (_sharedContentCacheLock)
                            {
                                if (scc.TryGetValue(scId, out var cached) &&
                                    !string.IsNullOrEmpty(cached.rawImageUrl))
                                {
                                    name      = cached.name;
                                    rawImage  = cached.rawImageUrl;
                                    fromCache = true;
                                }
                            }

                            if (!fromCache)
                            {
                                // Use Value<string>() not ToString() — JSON null tokens return ""
                                // from ToString() which breaks the ?? fallback chain.
                                if (scType == "wrld")
                                {
                                    var w = await _core.World.GetWorldFreshAsync(scId);
                                    name     = w?["name"]?.Value<string>() ?? "";
                                    rawImage = w?["imageUrl"]?.Value<string>()
                                            ?? w?["thumbnailImageUrl"]?.Value<string>() ?? "";
                                }
                                else if (scType == "avtr")
                                {
                                    var a = await _core.Avatars.GetAvatarAsync(scId);
                                    name     = a?["name"]?.Value<string>() ?? "";
                                    rawImage = a?["thumbnailImageUrl"]?.Value<string>()
                                            ?? a?["imageUrl"]?.Value<string>() ?? "";
                                    // GET /api/1/avatars/{id} returns 403 for own private avatars.
                                    // Fall back to own avatar list (releaseStatus=all) which includes private avatars.
                                    if (string.IsNullOrEmpty(rawImage) || string.IsNullOrEmpty(name))
                                    {
                                        await EnsureOwnAvatarCacheAsync();
                                        if (_ownAvatarCache!.TryGetValue(scId, out var own))
                                        {
                                            if (string.IsNullOrEmpty(name))     name     = own.name;
                                            if (string.IsNullOrEmpty(rawImage)) rawImage = own.thumb;
                                        }
                                    }
                                }
                                else if (scType == "grp")
                                {
                                    var g = await _core.Groups.GetGroupAsync(scId);
                                    name     = g?["name"]?.Value<string>() ?? "";
                                    rawImage = g?["iconUrl"]?.Value<string>()
                                            ?? g?["bannerUrl"]?.Value<string>() ?? "";
                                }
                                else if (scType == "usr")
                                {
                                    var u = await _core.Users.GetUserAsync(scId);
                                    name     = u?["displayName"]?.Value<string>() ?? "";
                                    rawImage = VRChatApiService.GetUserImage(u) ?? "";
                                }

                                // Persist to disk cache — only save if we got a usable image URL
                                // so we don't permanently cache "no image" for content that has one
                                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(rawImage))
                                {
                                    lock (_sharedContentCacheLock)
                                        scc[scId] = (name, rawImage);
                                    SaveSharedContentCache();
                                }
                            }

                            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(rawImage)) return;

                            string imageToSend = rawImage;
                            if (!string.IsNullOrEmpty(rawImage))
                            {
                                var fp = scType == "avtr" ? await ImageCacheHelper.CacheAvatarAsync(scId, rawImage)
                                       : scType == "wrld" ? await ImageCacheHelper.CacheWorldAsync(scId, rawImage)
                                       : scType == "grp"  ? await ImageCacheHelper.CacheGroupAsync(scId, rawImage)
                                       : scType == "usr"  ? await ImageCacheHelper.CacheUserAsync(scId, rawImage)
                                       : null;
                                if (fp != null) imageToSend = ImageCacheHelper.ToLocalUrl(fp);
                            }

                            Invoke(() => SendToJS("vrcSharedContentInfo", new { contentId = scId, contentType = scType, name, image = imageToSend }));
                        });
                    }
                    break;
                }

                // Favorite Friends
                case "vrcGetFavoriteFriends":
                case "vrcAddFavoriteFriend":
                case "vrcRemoveFavoriteFriend":
                case "vrcAddFavoriteFriendToGroup":
                    await _friends.HandleMessage(action, msg);
                    break;

                case "vrcGetMyWorlds":
                    _ = Task.Run(async () =>
                    {
                        var worlds = await _core.World.GetMyWorldsAsync();
                        foreach (JObject w in worlds.OfType<JObject>())
                        {
                            var wid = w["id"]?.ToString() ?? "";
                            var url = ImageCacheHelper.GetWorldUrl(wid, w["imageUrl"]?.ToString() ?? w["thumbnailImageUrl"]?.ToString());
                            w["imageUrl"] = url; w["thumbnailImageUrl"] = url;
                            EnrichWorldDatesFromCache(_core.TimeEngine, w, wid);
                            w["created_at"] = DateTimeHelper.Iso(w["created_at"]);
                            w["updated_at"] = DateTimeHelper.Iso(w["updated_at"]);
                            var stats = _core.TimeEngine.GetWorldStats(wid);
                            w["worldTimeSeconds"] = stats.totalSeconds;
                            w["worldVisitCount"]  = stats.visitCount;
                            w["worldLastVisited"] = stats.lastVisited;
                        }
                        Invoke(() => SendToJS("vrcMyWorlds", worlds));
                    });
                    break;

                case "exportList":
                {
                    var exType = msg["type"]?.ToString() ?? "";
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var categories = new List<object>();

                            void AddLocalCats(string kind, Func<LocalFavoritesStore.LocalItem, string> nameOf)
                            {
                                var byGroup = _core.LocalFavorites.GetItems(kind)
                                    .GroupBy(it => it.GroupName)
                                    .ToDictionary(gr => gr.Key, gr => gr.ToList());
                                foreach (var lg in _core.LocalFavorites.GetGroups(kind))
                                {
                                    var its  = byGroup.TryGetValue(lg.Name, out var l) ? l : new List<LocalFavoritesStore.LocalItem>();
                                    var rows = its.Select(it => new { id = it.EntityId, name = nameOf(it) })
                                        .Where(x => x.id.Length > 0).ToList();
                                    categories.Add(new { key = (string?)null, title = string.IsNullOrEmpty(lg.DisplayName) ? lg.Name : lg.DisplayName, local = true, items = rows });
                                }
                            }

                            if (exType == "worlds")
                            {
                                var mine = await _core.World.GetMyWorldsAsync();
                                categories.Add(new { key = (string?)"my_worlds", title = (string?)null, local = false, items = mine.OfType<JObject>()
                                    .Select(w => new { id = w["id"]?.ToString() ?? "", name = w["name"]?.ToString() ?? "" })
                                    .Where(x => x.id.Length > 0).ToList() });

                                var favGroups = await _core.Favorites.GetFavoriteGroupsAsync();
                                var worldTypes = new HashSet<string> { "world", "vrcPlusWorld" };
                                foreach (var g in favGroups.Where(g => worldTypes.Contains(g["type"]?.ToString() ?? "")))
                                {
                                    var gtag = g["name"]?.ToString() ?? "";
                                    if (gtag.Length == 0) continue;
                                    var gtitle = g["displayName"]?.ToString();
                                    if (string.IsNullOrEmpty(gtitle)) gtitle = gtag;
                                    var items = (await _core.Favorites.GetFavoriteWorldsByGroupAsync(gtag, 100))
                                        .Select(w => new { id = w["id"]?.ToString() ?? "", name = w["name"]?.ToString() ?? "" })
                                        .Where(x => x.id.Length > 0).ToList();
                                    categories.Add(new { key = (string?)null, title = gtitle, local = false, items });
                                }
                                AddLocalCats("world", it => it.Snapshot["name"]?.ToString() ?? "");
                            }
                            else if (exType == "avatars")
                            {
                                var mine = await _core.Avatars.GetOwnAvatarsAsync();
                                categories.Add(new { key = (string?)"my_avatars", title = (string?)null, local = false, items = mine
                                    .Select(a => new { id = a["id"]?.ToString() ?? "", name = a["name"]?.ToString() ?? "" })
                                    .Where(x => x.id.Length > 0).ToList() });

                                var favGroups = await _core.Favorites.GetFavoriteGroupsAsync();
                                foreach (var g in favGroups.Where(g => (g["type"]?.ToString() ?? "") == "avatar"))
                                {
                                    var gtag = g["name"]?.ToString() ?? "";
                                    if (gtag.Length == 0) continue;
                                    var gtitle = g["displayName"]?.ToString();
                                    if (string.IsNullOrEmpty(gtitle)) gtitle = gtag;
                                    var items = (await _core.Favorites.GetFavoriteAvatarsByGroupAsync(gtag, 100))
                                        .Select(a => new { id = a["id"]?.ToString() ?? "", name = a["name"]?.ToString() ?? "" })
                                        .Where(x => x.id.Length > 0).ToList();
                                    categories.Add(new { key = (string?)null, title = gtitle, local = false, items });
                                }
                                AddLocalCats("avatar", it => it.Snapshot["name"]?.ToString() ?? "");
                            }
                            else if (exType == "groups")
                            {
                                var groups = await _core.Groups.GetUserGroupsAsync();
                                categories.Add(new { key = (string?)"my_groups", title = (string?)null, local = false, items = groups.OfType<JObject>()
                                    .Select(g => new { id = g["groupId"]?.ToString() ?? g["id"]?.ToString() ?? "", name = g["name"]?.ToString() ?? "" })
                                    .Where(x => x.id.Length > 0).ToList() });
                            }
                            else if (exType == "friends")
                            {
                                var store = _friends.GetStoreSnapshot();
                                var nameById = new Dictionary<string, string>();
                                foreach (var f in store)
                                {
                                    var id = f["id"]?.ToString() ?? "";
                                    if (id.Length > 0) nameById[id] = f["displayName"]?.ToString() ?? "";
                                }

                                categories.Add(new { key = (string?)"friends", title = (string?)null, local = false, items = store
                                    .Select(f => new { id = f["id"]?.ToString() ?? "", name = f["displayName"]?.ToString() ?? "" })
                                    .Where(x => x.id.Length > 0)
                                    .OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase).ToList() });

                                var favGroups = await _core.Favorites.GetFavoriteGroupsAsync();
                                var favs = await _core.Favorites.GetFavoriteFriendsAsync();
                                var byTag = favs.OfType<JObject>()
                                    .Select(e => new { uid = e["favoriteId"]?.ToString() ?? "", tag = (e["tags"] as JArray)?.FirstOrDefault()?.ToString() ?? "group_0" })
                                    .Where(x => x.uid.Length > 0)
                                    .GroupBy(x => x.tag)
                                    .ToDictionary(gr => gr.Key, gr => gr.Select(x => x.uid).ToList());
                                foreach (var g in favGroups.Where(g => (g["type"]?.ToString() ?? "") == "friend"))
                                {
                                    var gtag = g["name"]?.ToString() ?? "";
                                    if (gtag.Length == 0) continue;
                                    var gtitle = g["displayName"]?.ToString();
                                    if (string.IsNullOrEmpty(gtitle)) gtitle = gtag;
                                    var uids = byTag.TryGetValue(gtag, out var l) ? l : new List<string>();
                                    var items = uids.Select(uid => new { id = uid, name = nameById.TryGetValue(uid, out var nm) && nm.Length > 0 ? nm : uid })
                                        .OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase).ToList();
                                    categories.Add(new { key = (string?)null, title = gtitle, local = false, items });
                                }
                                AddLocalCats("friend", it => it.Snapshot["name"]?.ToString() ?? it.Snapshot["displayName"]?.ToString() ?? (nameById.TryGetValue(it.EntityId, out var n) ? n : ""));
                            }
                            else return;

                            Invoke(() => SendToJS("exportList", new { type = exType, categories }));
                        }
                        catch (Exception ex)
                        {
                            Invoke(() => SendToJS("log", new { msg = $"Export error: {ex.Message}", color = "err" }));
                        }
                    });
                    break;
                }

                case "exportSaveCsv":
                {
                    var csvText = msg["text"]?.ToString() ?? "";
                    var rs = Dialog.FileSave("csv");
                    if (rs.IsOk)
                    {
                        var path = rs.Path;
                        if (!path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) path += ".csv";
                        try
                        {
                            System.IO.File.WriteAllText(path, csvText);
                            SendToJS("log", new { msg = $"Saved: {path}", color = "ok" });
                        }
                        catch (Exception ex)
                        {
                            SendToJS("log", new { msg = $"Export save failed: {ex.Message}", color = "err" });
                        }
                    }
                    break;
                }

                case "exportDebugKit":
                {
                    var dkPick = Dialog.FolderPicker();
                    if (!dkPick.IsOk || string.IsNullOrEmpty(dkPick.Path)) break;
                    var dkDir = dkPick.Path;
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var vrcnLogs = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                "VRCNext", "Logs");
                            var crashDir = Path.Combine(vrcnLogs, "Crashes");
                            var vrcDir   = VRCNext.Services.Helpers.VrcPathsHelper.AppDataDir();

                            IEnumerable<string> Latest(string dir, string pattern, int count) =>
                                Directory.Exists(dir)
                                    ? Directory.GetFiles(dir, pattern)
                                        .Select(f => new FileInfo(f))
                                        .OrderByDescending(f => f.LastWriteTimeUtc)
                                        .Take(count)
                                        .Select(f => f.FullName)
                                    : Enumerable.Empty<string>();

                            var zipPath = Path.Combine(dkDir, $"vrcn-log-{DateTime.Now:dd-MM-yyyy}.zip");
                            using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
                            {
                                void Add(string folder, string file)
                                {
                                    var entry = zip.CreateEntry(folder + "/" + Path.GetFileName(file), System.IO.Compression.CompressionLevel.Optimal);
                                    using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                                    using var dst = entry.Open();
                                    src.CopyTo(dst);
                                }
                                foreach (var f in Latest(crashDir, "crash_*.txt", 5))    Add("crashes", f);
                                foreach (var f in Latest(vrcnLogs, "vrcn-log-*.txt", 5)) Add("vrcn", f);
                                foreach (var f in Latest(vrcDir, "output_log_*.txt", 2)) Add("vrchat", f);
                            }
                            Invoke(() => SendToJS("debugKitExported", new { ok = true, path = zipPath }));
                        }
                        catch (Exception ex)
                        {
                            Invoke(() => SendToJS("debugKitExported", new { ok = false, error = ex.Message }));
                        }
                    });
                    break;
                }

                case "importPickFile":
                {
                    var imType = msg["type"]?.ToString() ?? "";
                    if (imType != "worlds" && imType != "avatars") break;
                    var ro = Dialog.FileOpen("csv,json,txt");
                    if (!ro.IsOk) break;
                    string imText;
                    try { imText = System.IO.File.ReadAllText(ro.Path); }
                    catch (Exception ex)
                    {
                        SendToJS("log", new { msg = $"Import read failed: {ex.Message}", color = "err" });
                        SendToJS("toast", new { ok = false, msg = $"Import failed: {ex.Message}" });
                        break;
                    }
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var favGroups = await _core.Favorites.GetFavoriteGroupsAsync();
                            List<AuthController.WFavGroup> groupList;
                            if (imType == "avatars")
                            {
                                groupList = favGroups
                                    .Where(g => (g["type"]?.ToString() ?? "") == "avatar")
                                    .Select(g => new AuthController.WFavGroup {
                                        name        = g["name"]?.ToString() ?? "",
                                        displayName = g["displayName"]?.ToString() ?? "",
                                        type        = g["type"]?.ToString() ?? "avatar"
                                    })
                                    .Where(g => !string.IsNullOrEmpty(g.name))
                                    .ToList();
                                groupList = AuthController.FillMissingAvatarSlots(groupList);
                                int imCap = _vrcApi.HasVrcPlus ? 50 : 25;
                                foreach (var g in groupList) g.capacity = imCap;
                                groupList.AddRange(AuthController.BuildLocalGroups(_core.LocalFavorites.GetGroups("avatar"), "localAvatar"));
                            }
                            else
                            {
                                var imWorldTypes = new HashSet<string> { "world", "vrcPlusWorld" };
                                groupList = favGroups
                                    .Where(g => imWorldTypes.Contains(g["type"]?.ToString() ?? ""))
                                    .Select(g => new AuthController.WFavGroup {
                                        name        = g["name"]?.ToString() ?? "",
                                        displayName = g["displayName"]?.ToString() ?? "",
                                        type        = g["type"]?.ToString() ?? "world"
                                    })
                                    .Where(g => !string.IsNullOrEmpty(g.name))
                                    .ToList();
                                groupList = AuthController.FillMissingWorldSlots(groupList, _vrcApi.HasVrcPlus);
                                groupList.AddRange(AuthController.BuildLocalGroups(_core.LocalFavorites.GetGroups("world"), "localWorld"));
                            }
                            Invoke(() => SendToJS("importFile", new { type = imType, text = imText, groups = groupList }));
                        }
                        catch (Exception ex)
                        {
                            Invoke(() => SendToJS("log", new { msg = $"Import error: {ex.Message}", color = "err" }));
                        }
                    });
                    break;
                }

                case "importFavorites":
                {
                    var impType    = msg["type"]?.ToString() ?? "";
                    var impEntries = (msg["entries"] as JArray) ?? new JArray();
                    if (impType != "worlds" && impType != "avatars") break;
                    _ = Task.Run(async () =>
                    {
                        var impKind = impType == "avatars" ? "avatar" : "world";
                        var impSelfId = _vrcApi.CurrentUserId ?? "";
                        int total = impEntries.Count, done = 0, added = 0, failed = 0, skipped = 0;
                        foreach (var entry in impEntries.OfType<JObject>())
                        {
                            var entId   = entry["id"]?.ToString() ?? "";
                            var entGrp  = entry["groupName"]?.ToString() ?? "";
                            var entType = entry["groupType"]?.ToString() ?? "";
                            done++;
                            if (entId.Length == 0 || entGrp.Length == 0) { failed++; continue; }

                            // Deleted or private entries would otherwise be favorited as
                            // unusable "Unnamed / Private" placeholders.
                            JObject? entSnap = null;
                            try
                            {
                                entSnap = impKind == "avatar"
                                    ? await _core.Avatars.GetAvatarAsync(entId)
                                    : await _core.World.GetWorldAsync(entId);
                            }
                            catch (Exception ex)
                            {
                                Invoke(() => SendToJS("log", new { msg = $"Import [{entId}] lookup failed: {ex.Message}", color = "err" }));
                            }

                            string entRelease = entSnap?["releaseStatus"]?.ToString() ?? "";
                            string entAuthor  = entSnap?["authorId"]?.ToString() ?? "";
                            bool entMine      = entAuthor.Length > 0 && entAuthor == impSelfId;
                            bool entUnusable  = entSnap == null
                                || (entRelease.Length > 0
                                    && !entRelease.Equals("public", StringComparison.OrdinalIgnoreCase)
                                    && !entMine);

                            if (entUnusable)
                            {
                                skipped++;
                                var why = entSnap == null ? "not found (deleted or private)" : $"not public ({entRelease})";
                                Invoke(() => SendToJS("log", new { msg = $"Import [{entId}] skipped: {why}", color = "sec" }));
                                int sDone = done, sOk = added, sFailed = failed, sSkip = skipped;
                                Invoke(() => SendToJS("importProgress", new { type = impType, done = sDone, total, ok = sOk, failed = sFailed, skipped = sSkip }));
                                await Task.Delay(500);
                                continue;
                            }

                            bool entLocal = entType.StartsWith("local") || _core.LocalFavorites.IsLocalGroup(entGrp);
                            bool entOk = false;
                            try
                            {
                                if (entLocal)
                                {
                                    var (lok, lerr, _) = _core.LocalFavorites.AddItem(entGrp, impKind, entId, entSnap ?? new JObject());
                                    entOk = lok;
                                    if (!lok) Invoke(() => SendToJS("log", new { msg = $"Import [{entId}]: {lerr}", color = "err" }));
                                }
                                else if (impKind == "avatar")
                                {
                                    var (aok, ares) = await _core.Avatars.AddAvatarFavoriteAsync(entId, entGrp, entType.Length > 0 ? entType : "avatar");
                                    entOk = aok;
                                    if (!aok) Invoke(() => SendToJS("log", new { msg = $"Import [{entId}]: {ares}", color = "err" }));
                                }
                                else
                                {
                                    var (wok, wres) = await _core.Favorites.AddWorldFavoriteAsync(entId, entGrp, entType.Length > 0 ? entType : "world");
                                    entOk = wok;
                                    if (!wok) Invoke(() => SendToJS("log", new { msg = $"Import [{entId}]: {wres}", color = "err" }));
                                }
                            }
                            catch (Exception ex)
                            {
                                Invoke(() => SendToJS("log", new { msg = $"Import [{entId}] exception: {ex.Message}", color = "err" }));
                            }

                            if (entOk) added++; else failed++;
                            int pDone = done, pOk = added, pFailed = failed, pSkip = skipped;
                            Invoke(() => SendToJS("importProgress", new { type = impType, done = pDone, total, ok = pOk, failed = pFailed, skipped = pSkip }));
                            await Task.Delay(500);
                        }
                        try
                        {
                            if (impKind == "avatar") _cache.Delete(CacheHandler.KeyFavAvatars);
                            RefreshLocalFavKind(impKind);
                        }
                        catch (Exception ex)
                        {
                            Invoke(() => SendToJS("log", new { msg = $"Import refresh failed: {ex.Message}", color = "err" }));
                        }
                        Invoke(() => SendToJS("importDone", new { type = impType, total, ok = added, failed, skipped }));
                    });
                    break;
                }

                case "getWorldInsights":
                    _ = Task.Run(() =>
                    {
                        var worldId = msg["worldId"]?.ToString() ?? "";
                        var from    = msg["from"]?.ToString() ?? "";
                        var to      = msg["to"]?.ToString() ?? "";
                        if (string.IsNullOrEmpty(worldId) || string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) return;
                        var stats = _core.Timeline.GetWorldStats(worldId, from, to);
                        Invoke(() => SendToJS("worldInsights", new { worldId, from, to, stats }));
                    });
                    break;

                case "refreshWorldInsights":
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var worldId = msg["worldId"]?.ToString() ?? "";
                            var from    = msg["from"]?.ToString() ?? "";
                            var to      = msg["to"]?.ToString() ?? "";

                            var worlds = await _core.World.GetMyWorldsAsync();
                            foreach (var w in worlds)
                            {
                                var id = w["id"]?.ToString();
                                if (string.IsNullOrEmpty(id)) continue;
                                var active    = w["occupants"]?.Value<int>() ?? 0;
                                var favorites = w["favorites"]?.Value<int>() ?? 0;
                                var visits    = _core.Timeline.GetTodaysVisits(id);
                                if (visits <= 0)
                                    visits = (await _core.World.GetWorldFreshAsync(id))?["visits"]?.Value<int>() ?? 0;
                                _core.Timeline.InsertWorldStats(id, active, favorites, visits);
                            }

                            if (!string.IsNullOrEmpty(worldId) && !string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to))
                            {
                                var stats = _core.Timeline.GetWorldStats(worldId, from, to);
                                Invoke(() => SendToJS("worldInsights", new { worldId, from, to, stats }));
                            }
                        }
                        catch (Exception ex) { CrashHandler.WriteEntry("refreshWorldInsights", ex); }
                    });
                    break;

                // Groups - my groups, join, leave
                case "vrcGetFavoriteWorlds":
                    _ = Task.Run(async () =>
                    {
                        if (_settings.FfcEnabled)
                        {
                            var cachedFavWorlds = _cache.LoadRaw(CacheHandler.KeyFavWorlds);
                            if (cachedFavWorlds != null)
                            {
                                if (cachedFavWorlds is JObject cfw)
                                    foreach (var cw in cfw["worlds"] as JArray ?? new JArray())
                                        if (cw is JObject cwo)
                                            EnrichWorldDatesFromCache(_core.TimeEngine, cwo, cwo["id"]?.ToString() ?? "");
                                Invoke(() => SendToJS("vrcFavoriteWorlds", cachedFavWorlds));
                            }
                        }
                        _authCtrl.ClearFavGroupsCache(); // ensure fresh visibility state from API
                        await _authCtrl.FetchAndCacheFavWorldsAsync();
                    });
                    break;

                case "vrcUpdateFavoriteGroup":
                    _ = Task.Run(async () =>
                    {
                        var groupType   = msg["groupType"]?.ToString() ?? "world";
                        var groupName   = msg["groupName"]?.ToString() ?? "";
                        var displayName = msg["displayName"]?.ToString() ?? "";
                        var visibility  = msg["visibility"]?.ToString(); // null = don't change
                        bool ok;
                        if (_core.LocalFavorites.IsLocalGroup(groupName))
                            ok = string.IsNullOrEmpty(displayName) || _core.LocalFavorites.RenameGroup(groupName, displayName);
                        else
                        {
                            ok = await _core.Favorites.UpdateFavoriteGroupAsync(groupType, groupName, displayName, visibility);
                            if (ok) _authCtrl.ClearFavGroupsCache();
                        }
                        Invoke(() => SendToJS("vrcFavoriteGroupUpdated", new { ok, groupName, displayName, visibility }));
                    });
                    break;

                case "vrcGetWorldFavGroups":
                    _ = Task.Run(async () =>
                    {
                        var groups = await _core.Favorites.GetFavoriteGroupsAsync();
                        var worldTypes = new HashSet<string> { "world", "vrcPlusWorld" };
                        var groupList = groups
                            .Where(g => worldTypes.Contains(g["type"]?.ToString() ?? ""))
                            .Select(g => new AuthController.WFavGroup {
                                name        = g["name"]?.ToString() ?? "",
                                displayName = g["displayName"]?.ToString() ?? "",
                                type        = g["type"]?.ToString() ?? "world"
                            })
                            .Where(g => !string.IsNullOrEmpty(g.name))
                            .ToList();
                        groupList = AuthController.FillMissingWorldSlots(groupList, _vrcApi.HasVrcPlus);
                        groupList.AddRange(AuthController.BuildLocalGroups(_core.LocalFavorites.GetGroups("world"), "localWorld"));
                        Invoke(() => SendToJS("vrcWorldFavGroups", groupList));
                    });
                    break;

                case "vrcGetFriendFavGroups":
                    _ = Task.Run(async () =>
                    {
                        var groups = await _core.Favorites.GetFavoriteGroupsAsync();
                        var groupList = groups
                            .Where(g => g["type"]?.ToString() == "friend")
                            .Select(g => new AuthController.WFavGroup {
                                name        = g["name"]?.ToString() ?? "",
                                displayName = g["displayName"]?.ToString() ?? "",
                                type        = "friend",
                                capacity    = g["capacity"]?.Value<int>() ?? 150,
                                visibility  = g["visibility"]?.ToString() ?? "private",
                            })
                            .Where(g => !string.IsNullOrEmpty(g.name))
                            .ToList();
                        groupList = AuthController.FillMissingFriendSlots(groupList);
                        groupList.AddRange(AuthController.BuildLocalGroups(_core.LocalFavorites.GetGroups("friend"), "localFriend"));
                        Invoke(() => SendToJS("vrcFriendFavGroups", groupList));
                    });
                    break;

                case "vrcUpdateFavoriteFriendGroup":
                    _ = Task.Run(async () =>
                    {
                        var groupName   = msg["groupName"]?.ToString() ?? "";
                        var displayName = msg["displayName"]?.ToString() ?? "";
                        var visibility  = msg["visibility"]?.ToString();
                        var ok = await _core.Favorites.UpdateFavoriteGroupAsync("friend", groupName, displayName, visibility);
                        Invoke(() => SendToJS("vrcFriendFavoriteGroupUpdated", new { ok, groupName, displayName, visibility }));
                    });
                    break;

                case "vrcSetHomeWorld":
                    var homeWid = msg["worldId"]?.ToString();
                    if (!string.IsNullOrEmpty(homeWid))
                    {
                        _ = Task.Run(async () =>
                        {
                            var ok = await _core.Users.SetHomeWorldAsync(homeWid);
                            Invoke(() => SendToJS("vrcActionResult", new { action = "setHomeWorld", success = ok,
                                message = ok ? "Home world updated!" : "Failed to set home world" }));
                        });
                    }
                    break;

                case "vrcAddWorldFavorite":
                    _ = Task.Run(async () =>
                    {
                        var worldId   = msg["worldId"]?.ToString() ?? "";
                        var groupName = msg["groupName"]?.ToString() ?? "";
                        var groupType = msg["groupType"]?.ToString() ?? "world";
                        var oldFvrtId = msg["oldFvrtId"]?.ToString();
                        bool targetLocal = groupType.StartsWith("local") || _core.LocalFavorites.IsLocalGroup(groupName);
                        if (LocalFavoritesStore.IsLocalId(oldFvrtId)) { _core.LocalFavorites.RemoveItem(oldFvrtId!); oldFvrtId = null; }
                        else if (targetLocal && !string.IsNullOrEmpty(oldFvrtId)) { await _core.Favorites.RemoveFavoriteFriendAsync(oldFvrtId); oldFvrtId = null; }
                        if (targetLocal)
                        {
                            var snap = await _core.World.GetWorldAsync(worldId) ?? new JObject();
                            var (lok, lerr, lid) = _core.LocalFavorites.AddItem(groupName, "world", worldId, snap);
                            Invoke(() => SendToJS("vrcWorldFavoriteResult", new { ok = lok, worldId, groupName, newFvrtId = lok ? lid : "", error = lok ? "" : lerr }));
                            return;
                        }
                        var (ok, resultData) = await _core.Favorites.AddWorldFavoriteAsync(worldId, groupName, groupType, oldFvrtId);
                        // resultData = new fvrt ID on success, error message on failure
                        Invoke(() => SendToJS("vrcWorldFavoriteResult", new { ok, worldId, groupName, newFvrtId = ok ? resultData : "", error = ok ? "" : resultData }));
                    });
                    break;

                case "vrcRemoveWorldFavorite":
                {
                    var worldId = msg["worldId"]?.ToString() ?? "";
                    var fvrtId  = msg["fvrtId"]?.ToString() ?? "";
                    _ = Task.Run(async () =>
                    {
                        var ok = LocalFavoritesStore.IsLocalId(fvrtId)
                            ? _core.LocalFavorites.RemoveItem(fvrtId)
                            : await _core.Favorites.RemoveFavoriteFriendAsync(fvrtId);
                        Invoke(() => SendToJS("vrcWorldUnfavoriteResult", new { ok, worldId }));
                    });
                    break;
                }

                case "vrcGetAvatarFavGroups":
                    _ = Task.Run(async () =>
                    {
                        var groups = await _core.Favorites.GetFavoriteGroupsAsync();
                        var avatarTypes = new HashSet<string> { "avatar" };
                        var groupList = groups
                            .Where(g => avatarTypes.Contains(g["type"]?.ToString() ?? ""))
                            .Select(g => new AuthController.WFavGroup {
                                name        = g["name"]?.ToString() ?? "",
                                displayName = g["displayName"]?.ToString() ?? "",
                                type        = g["type"]?.ToString() ?? "avatar"
                            })
                            .Where(g => !string.IsNullOrEmpty(g.name))
                            .ToList();
                        groupList = AuthController.FillMissingAvatarSlots(groupList);
                        int avCap = _vrcApi.HasVrcPlus ? 50 : 25;
                        foreach (var g in groupList) g.capacity = avCap;
                        groupList.AddRange(AuthController.BuildLocalGroups(_core.LocalFavorites.GetGroups("avatar"), "localAvatar"));
                        Invoke(() => SendToJS("vrcAvatarFavGroups", groupList));
                    });
                    break;

                case "vrcAddAvatarFavorite":
                    _ = Task.Run(async () =>
                    {
                        var avId      = msg["avatarId"]?.ToString() ?? "";
                        var avGroup   = msg["groupName"]?.ToString() ?? "";
                        var avType    = msg["groupType"]?.ToString() ?? "avatar";
                        var avOldFvrt = msg["oldFvrtId"]?.ToString();
                        bool avTargetLocal = avType.StartsWith("local") || _core.LocalFavorites.IsLocalGroup(avGroup);
                        if (LocalFavoritesStore.IsLocalId(avOldFvrt)) { _core.LocalFavorites.RemoveItem(avOldFvrt!); avOldFvrt = null; }
                        else if (avTargetLocal && !string.IsNullOrEmpty(avOldFvrt)) { await _core.Favorites.RemoveFavoriteFriendAsync(avOldFvrt); avOldFvrt = null; }
                        if (avTargetLocal)
                        {
                            var snap = await _core.Avatars.GetAvatarAsync(avId) ?? new JObject();
                            var (lok, lerr, lid) = _core.LocalFavorites.AddItem(avGroup, "avatar", avId, snap);
                            if (lok) _cache.Delete(CacheHandler.KeyFavAvatars);
                            if (lok && _settings.VrcndbSyncLikes) PopularityReporter.SyncFavoriteLikes(new[] { avId });
                            Invoke(() => SendToJS("vrcAvatarFavoriteResult", new { ok = lok, avatarId = avId, groupName = avGroup, newFvrtId = lok ? lid : "", error = lok ? "" : lerr }));
                            return;
                        }
                        var (avOk, avResult) = await _core.Avatars.AddAvatarFavoriteAsync(avId, avGroup, avType, avOldFvrt);
                        if (avOk) _cache.Delete(CacheHandler.KeyFavAvatars);
                        if (avOk && _settings.VrcndbSyncLikes) PopularityReporter.SyncFavoriteLikes(new[] { avId });
                        Invoke(() => SendToJS("vrcAvatarFavoriteResult", new { ok = avOk, avatarId = avId, groupName = avGroup, newFvrtId = avOk ? avResult : "", error = avOk ? "" : avResult }));
                    });
                    break;

                case "vrcRemoveAvatarFavorite":
                {
                    var avRmId   = msg["avatarId"]?.ToString() ?? "";
                    var avFvrtId = msg["fvrtId"]?.ToString() ?? "";
                    _ = Task.Run(async () =>
                    {
                        var ok = LocalFavoritesStore.IsLocalId(avFvrtId)
                            ? _core.LocalFavorites.RemoveItem(avFvrtId)
                            : await _core.Favorites.RemoveFavoriteFriendAsync(avFvrtId);
                        if (ok) _cache.Delete(CacheHandler.KeyFavAvatars);
                        Invoke(() => SendToJS("vrcAvatarUnfavoriteResult", new { ok, avatarId = avRmId }));
                    });
                    break;
                }

                case "vrcCreateLocalGroup":
                    _ = Task.Run(() =>
                    {
                        var kind = msg["kind"]?.ToString() ?? "world";
                        var displayName = msg["displayName"]?.ToString() ?? "";
                        var (ok, err, grp) = _core.LocalFavorites.CreateGroup(kind, displayName);
                        Invoke(() => SendToJS("vrcLocalGroupResult", new { ok, kind, action = "create", error = ok ? "" : err, groupName = grp?.Name ?? "", displayName = grp?.DisplayName ?? "" }));
                        if (ok) RefreshLocalFavKind(kind);
                    });
                    break;

                case "vrcDeleteLocalGroup":
                    _ = Task.Run(() =>
                    {
                        var kind = msg["kind"]?.ToString() ?? "world";
                        var groupName = msg["groupName"]?.ToString() ?? "";
                        var ok = _core.LocalFavorites.DeleteGroup(groupName);
                        Invoke(() => SendToJS("vrcLocalGroupResult", new { ok, kind, action = "delete", error = ok ? "" : "delete_failed", groupName }));
                        if (ok) RefreshLocalFavKind(kind);
                    });
                    break;

                case "vrcGetMyGroups":
                    await _groups.HandleMessage(action, msg);
                    break;

                case "vrcGetRepresentedGroup":
                    await _groups.HandleMessage(action, msg);
                    break;

                case "vrcGetGroup":
                    await _groups.HandleMessage(action, msg);
                    break;

                case "vrcJoinGroup":
                case "vrcGetGroupMembers":
                case "vrcSearchGroupMembers":
                case "vrcGetGroupRoleMembers":
                case "vrcLeaveGroup":
                case "vrcRepresentGroup":
                case "vrcSetGroupVisibility":
                    await _groups.HandleMessage(action, msg);
                    break;

                case "vrcCreateGroup":
                case "vrcCreateGroupPost":
                case "vrcUpdateGroupPost":
                case "vrcDeleteGroupPost":
                case "vrcDeleteGroupEvent":
                    await _groups.HandleMessage(action, msg);
                    break;

                case "vrcUpdateGroup":
                case "vrcKickGroupMember":
                case "vrcBanGroupMember":
                case "vrcGetGroupBans":
                case "vrcGetGroupLogs":
                case "vrcUnbanGroupMember":
                case "vrcCreateGroupRole":
                case "vrcUpdateGroupRole":
                case "vrcDeleteGroupRole":
                case "vrcAddGroupMemberRole":
                case "vrcRemoveGroupMemberRole":
                case "vrcCreateGroupEvent":
                case "vrcInviteToGroup":
                case "vrcGetDashGroupInstances":
                case "vrcGetMutualsForNetwork":
                case "vrcSaveMutualCache":
                case "vrcLoadMutualCache":
                case "vrcClearMutualCache":
                case "vrcGetGroupsForNetwork":
                case "vrcGetNetworkSessions":
                case "vrcSaveNetworkCache":
                case "vrcLoadNetworkCache":
                case "vrcClearNetworkCache":
                    await _groups.HandleMessage(action, msg);
                    break;

                case "vrcGetTimeSpent":
                case "vrcGetPeopleStats":
                    await _instance.HandleMessage(action, msg);
                    break;

                case "vrcCreateGroupInstance":
                    await _groups.HandleMessage(action, msg);
                    break;

                // Custom Chatbox OSC
                case "chatboxConfig":
                case "chatboxDirectSend":
                    _chatboxCtrl.HandleMessage(action, msg);
                    break;

                // Space Flight
                case "sfConnect":
                case "sfDisconnect":
                case "sfReset":
                case "sfConfig":
                    _sfCtrl.HandleMessage(action, msg);
                    break;

                // Space Turn
                case "stConnect":
                case "stDisconnect":
                case "stReset":
                case "stConfig":
                    _stCtrl.HandleMessage(action, msg);
                    break;

                // FrameShot (in-VR photo)
                case "fsConnect":
                case "fsDisconnect":
                case "fsConfig":
                case "fsGetDevices":
                case "fsSetOutput":
                case "fsGetAudioDevices":
                case "fsGetFfmpegState":
                case "fsInstallFfmpeg":
                    _fsCtrl.HandleMessage(action, msg);
                    break;

                // Voice Fight
                case "vfGetDevices":
                case "vfGetItems":
                case "vfStart":
                case "vfStop":
                case "vfAddSound":
                case "vfAddSoundToItem":
                case "vfDeleteItem":
                case "vfDeleteSound":
                case "vfPlaySound":
                case "vfSetStopWord":
                case "vfStopSound":
                case "vfGetBlockList":
                case "vfSetBlockList":
                case "vfSetWord":
                case "vfSetVolume":
                case "vfSetInputDevice":
                case "vfSetOutputDevice":
                    _vfCtrl.HandleMessage(action, msg);
                    break;

                // Kikitan XD
                case "kxdGetDevices":
                case "kxdSaveSettings":
                case "kxdTranslateProfileText":
                case "kxdLocalGetState":
                case "kxdLocalDownload":
                case "kxdLocalCancel":
                case "kxdLocalUninstall":
                case "kxdLocalSaveSelection":
                    _kxdCtrl.HandleMessage(action, msg);
                    break;

                case "kxdStart":
                case "kxdStop":
                    _kxdCtrl.HandleMessage(action, msg);
                    _vroCtrl.UpdateToolStates();
                    break;

                // Event Snipe
                case "vrcStartSnipe":
                case "vrcStopSnipe":
                case "vrcSnipeStatus":
                    await _snipeCtrl.HandleMessage(action, msg);
                    break;

                // Action Flow
                case "afLoadFlows":
                case "afSaveFlows":
                case "afSaveConditions":
                case "afTrayNotify":
                case "afGetGameRunning":
                case "afSendChatMessage":
                case "afInstanceWebhook":
                case "afTextWebhook":
                    _afCtrl.HandleMessage(action, msg);
                    break;

                // Status Schedule
                case "ssLoadRules":
                case "ssSaveRules":
                case "ssSetEnabled":
                    await _ssCtrl.HandleMessage(action, msg);
                    break;

                // Avatar Scaling
                case "asConnect":
                case "asDisconnect":
                case "asSaveSettings":
                case "asSetScale":
                    _asCtrl.HandleMessage(action, msg);
                    break;

                // OSC Tool
                case "oscConnect":
                case "oscDisconnect":
                case "oscSend":
                case "oscSendRaw":
                case "hypeRateGetState":
                case "weatherGetState":
                case "oscSetTabVisible":
                case "oscEnableOutputs":
                    _chatboxCtrl.HandleMessage(action, msg);
                    break;

                // VRCVideoCacher
                case "vcCheck":
                case "vcInstall":
                case "vcStart":
                case "vcStop":
                    _relayCtrl.HandleMessage(action, msg);
                    break;

                // Friend actions delegated to FriendsController
                case "vrcSendFriendRequest":
                case "vrcUnfriend":
                case "vrcGetBlocked":
                case "vrcGetMuted":
                case "vrcBlock":
                case "vrcMute":
                case "vrcUnblock":
                case "vrcUnmute":
                case "vrcHideAvatar":
                case "vrcShowAvatar":
                case "vrcInteractOff":
                case "vrcInteractOn":
                case "vrcMuteChat":
                case "vrcUnmuteChat":
                case "vrcGetAllModerations":
                case "vrcBoop":
                case "vrcSendChatMessage":
                case "vrcGetChatHistory":
                case "vrcSetFriendAlert":
                case "vrcGetFriendAlert":
                    await _friends.HandleMessage(action, msg);
                    break;

                // Calendar
                case "vrcGetCalendarEvents":
                    var calFilter = msg["filter"]?.ToString() ?? "all";
                    var calYear   = msg["year"]?.Value<int>()  ?? 0;
                    var calMonth  = msg["month"]?.Value<int>() ?? 0;
                    _ = Task.Run(async () => {
                        var evts = await _core.Calendar.GetCalendarEventsAsync(calFilter, calYear, calMonth);
                        if (calYear > 0 && calMonth > 0)
                        {
                            var nextYear  = calMonth == 12 ? calYear + 1 : calYear;
                            var nextMonth = calMonth == 12 ? 1 : calMonth + 1;
                            var seen = new HashSet<string>(evts.OfType<JObject>()
                                .Select(e => e["id"]?.ToString() ?? "").Where(s => s.Length > 0));
                            foreach (var e in await _core.Calendar.GetCalendarEventsAsync(calFilter, nextYear, nextMonth))
                                if (e is JObject eo && seen.Add(eo["id"]?.ToString() ?? "")) evts.Add(eo);
                        }
                        // Block event series and show 1 event instead of multiple ones lol.
                        var seriesIds = new HashSet<string>(
                            evts.OfType<JObject>()
                                .Select(e => e["seriesId"]?.ToString())
                                .Where(s => !string.IsNullOrEmpty(s))!);
                        var deduped = new JArray(
                            evts.OfType<JObject>()
                                .Where(e => !seriesIds.Contains(e["id"]?.ToString() ?? "")));
                        foreach (var ce in deduped.OfType<JObject>())
                            ce["imageUrl"] = ImageCacheHelper.GetEventUrl(ce["id"]?.ToString(), ce["imageUrl"]?.ToString());
                        Invoke(() => SendToJS("vrcCalendarEvents", new { events = deduped, filter = calFilter }));
                    });
                    break;

                case "vrcGetCalendarEvent":
                    var calGrpId = msg["groupId"]?.ToString();
                    var calEvtId = msg["calendarId"]?.ToString();
                    if (!string.IsNullOrEmpty(calGrpId) && !string.IsNullOrEmpty(calEvtId))
                    {
                        var calCached = _core.TimeEngine.GetEventDetail(calEvtId);
                        if (calCached != null)
                            Invoke(() => SendToJS("vrcCalendarEvent", new JObject {
                                ["id"]          = calEvtId,
                                ["groupId"]     = calCached.GroupId,
                                ["ownerId"]     = calCached.OwnerId,
                                ["title"]       = calCached.Title,
                                ["description"] = calCached.Description,
                                ["startsAt"]    = calCached.StartsAt,
                                ["endsAt"]      = calCached.EndsAt,
                                ["imageUrl"]    = ImageCacheHelper.GetEventUrl(calEvtId, calCached.ImageUrl),
                                ["accessType"]  = calCached.AccessType,
                                ["isFollowing"] = calCached.IsFollowing,
                                ["tags"]        = new JArray(calCached.Tags),
                            }));
                        _ = Task.Run(async () => {
                            var ev = await _core.Calendar.GetCalendarEventAsync(calGrpId, calEvtId);
                            if (ev != null)
                            {
                                _core.TimeEngine.SaveEventDetail(
                                    ev["id"]?.ToString() ?? calEvtId,
                                    ev["groupId"]?.ToString() ?? calGrpId,
                                    ev["title"]?.ToString() ?? "",
                                    ev["description"]?.ToString() ?? "",
                                    ev["startsAt"]?.ToString() ?? "",
                                    ev["endsAt"]?.ToString() ?? "",
                                    ev["imageUrl"]?.ToString() ?? "",
                                    ev["accessType"]?.ToString() ?? "",
                                    ev["tags"]?.ToObject<List<string>>() ?? new(),
                                    ev["ownerId"]?.ToString() ?? "",
                                    ev["isFollowing"]?.Value<bool>() ?? false);
                            }
                            if (ev != null)
                                ev["imageUrl"] = ImageCacheHelper.GetEventUrl(ev["id"]?.ToString() ?? calEvtId, ev["imageUrl"]?.ToString());
                            Invoke(() => SendToJS("vrcCalendarEvent", ev ?? new JObject()));
                        });
                    }
                    break;

                case "vrcFollowEvent":
                    var fevGrpId = msg["groupId"]?.ToString();
                    var fevEvtId = msg["calendarId"]?.ToString();
                    var doFollow = msg["follow"]?.Value<bool>() ?? true;
                    if (!string.IsNullOrEmpty(fevGrpId) && !string.IsNullOrEmpty(fevEvtId))
                    {
                        _ = Task.Run(async () => {
                            var ok = await _core.Calendar.FollowEventAsync(fevGrpId, fevEvtId, doFollow);
                            Invoke(() => SendToJS("vrcActionResult", new { action = doFollow ? "followEvent" : "unfollowEvent",
                                success = ok, message = ok ? (doFollow ? "Following event" : "Unfollowed") : "Failed" }));
                        });
                    }
                    break;

                // Notifications delegated to NotificationsController
                case "vrcGetNotifications":
                case "vrcGetHiddenNotifications":
                case "vrcClearNotifications":
                case "vrcAcceptNotification":
                case "vrcHideNotification":
                case "vrcGetRespondMessages":
                case "vrcUpdateRespondMessage":
                    await _notifications.HandleMessage(action, msg);
                    break;

                case "vrcGetLogFiles":
                {
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var dir = VRCNext.Services.Helpers.VrcPathsHelper.AppDataDir();
                            var files = Directory.Exists(dir)
                                ? Directory.GetFiles(dir, "output_log_*.txt")
                                    .Select(f => new FileInfo(f))
                                    .OrderByDescending(f => f.LastWriteTimeUtc)
                                    .Take(50)
                                    .Select(f => new
                                    {
                                        name = f.Name,
                                        sizeBytes = f.Length,
                                        modified = f.LastWriteTimeUtc.ToString("o"),
                                    })
                                    .ToList<object>()
                                : new List<object>();
                            Invoke(() => SendToJS("vrcLogFiles", new { dir, files }));
                        }
                        catch (Exception ex)
                        {
                            Invoke(() => SendToJS("vrcLogFiles", new { dir = "", files = new object[0], error = ex.Message }));
                        }
                    });
                    break;
                }

                case "vrcReadLogFile":
                {
                    var lvName   = msg["file"]?.ToString() ?? "";
                    var lvQuery  = msg["query"]?.ToString() ?? "";
                    var lvLevels = msg["levels"]?.ToObject<List<string>>() ?? new List<string>();
                    var lvCats   = msg["categories"]?.ToObject<List<string>>() ?? new List<string>();
                    var lvMax    = Math.Clamp(msg["max"]?.Value<int>() ?? 2000, 100, 20000);
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var dir = VRCNext.Services.Helpers.VrcPathsHelper.AppDataDir();
                            var safe = Path.GetFileName(lvName);
                            if (string.IsNullOrEmpty(safe)
                                || !safe.StartsWith("output_log_", StringComparison.OrdinalIgnoreCase)
                                || !safe.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                            {
                                Invoke(() => SendToJS("vrcLogLines", new { file = lvName, entries = new object[0], error = "Invalid log file" }));
                                return;
                            }
                            var path = Path.Combine(dir, safe);
                            if (!File.Exists(path))
                            {
                                Invoke(() => SendToJS("vrcLogLines", new { file = safe, entries = new object[0], error = "File not found" }));
                                return;
                            }

                            var all = new List<VrcLogEntry>();
                            VrcLogEntry? current = null;
                            var lineNo = 0;
                            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            using (var sr = new StreamReader(fs))
                            {
                                string? line;
                                while ((line = sr.ReadLine()) != null)
                                {
                                    lineNo++;
                                    var header = ParseVrcLogHeader(line);
                                    if (header != null)
                                    {
                                        if (current != null) all.Add(current);
                                        current = new VrcLogEntry
                                        {
                                            timestamp = header.Value.timestamp,
                                            level     = header.Value.level,
                                            message   = StripVrcRichText(header.Value.message),
                                            category  = ExtractVrcLogCategory(header.Value.message),
                                            raw       = line,
                                            lineNumber = lineNo,
                                        };
                                        continue;
                                    }
                                    if (current != null) current.continuation.Add(line);
                                }
                            }
                            if (current != null) all.Add(current);

                            var categories = all.Select(e => e.category)
                                .Where(c => !string.IsNullOrEmpty(c))
                                .Distinct(StringComparer.Ordinal)
                                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                                .ToList();

                            IEnumerable<VrcLogEntry> filtered = all;
                            if (lvLevels.Count > 0)
                                filtered = filtered.Where(e => lvLevels.Contains(e.level, StringComparer.OrdinalIgnoreCase));
                            if (lvCats.Count > 0)
                                filtered = filtered.Where(e => lvCats.Contains(e.category, StringComparer.Ordinal));
                            if (!string.IsNullOrWhiteSpace(lvQuery))
                                filtered = filtered.Where(e =>
                                    e.message.Contains(lvQuery, StringComparison.OrdinalIgnoreCase)
                                    || e.category.Contains(lvQuery, StringComparison.OrdinalIgnoreCase)
                                    || e.continuation.Any(c => c.Contains(lvQuery, StringComparison.OrdinalIgnoreCase)));

                            var list = filtered.ToList();
                            var matched = list.Count;
                            var truncated = matched > lvMax;
                            var shown = truncated ? list.Skip(matched - lvMax).ToList() : list;

                            Invoke(() => SendToJS("vrcLogLines", new
                            {
                                file = safe,
                                entries = shown.Select(e => new
                                {
                                    e.timestamp,
                                    e.level,
                                    e.category,
                                    e.message,
                                    e.raw,
                                    e.lineNumber,
                                    contLines = e.continuation,
                                }),
                                categories,
                                matched,
                                total = all.Count,
                                truncated,
                                error = "",
                            }));
                        }
                        catch (Exception ex)
                        {
                            Invoke(() => SendToJS("vrcLogLines", new { file = lvName, entries = new object[0], error = ex.Message }));
                        }
                    });
                    break;
                }

                case "vrcGetMessageTemplates":
                case "vrcUpdateMessageTemplate":
                case "vrcRespondToNotification":
                case "vrcRespondToNotificationWithPhoto":
                    await _notifications.HandleMessage(action, msg);
                    break;

                // App updates
                case "checkUpdate":
                case "installUpdate":
                case "getChangelog":
                    await _authCtrl.HandleMessage(action, msg);
                    break;

                case "vrcLaunchAndJoin":
                    _relayCtrl.HandleMessage(action, msg);
                    break;

                // Current instance
                case "vrcGetCurrentInstance":
                    await _instance.HandleMessage(action, msg);
                    break;

                // Timeline — all timeline + import message cases delegated to TimelineController
                case "getTimeline":
                case "getTimelinePage":
                case "searchTimeline":
                case "searchFriendTimeline":
                case "getFriendTimeline":
                case "getFriendTimelinePage":
                case "getFtAlsoWasHere":
                case "getTimelineByDate":
                case "getFriendTimelineByDate":
                case "getTimelineForUser":
                case "getTimelineForWorld":
                case "getProfileInsights":
                case "getUserOnlineHeatmap":
                case "getUserStatusTime":
                case "getTimelineMonthActivity":
                case "getInstanceChart":
                case "deleteTimelineEvent":
                case "deleteFriendTimelineEvent":
                case "deleteTimelineEvents":
                case "deleteFriendTimelineEvents":
                case "deleteTimelineByType":
                case "deleteFriendTimelineByType":
                case "getMeetNetwork":
                case "getMeetNetworkWorlds":
                case "getRewind":
                case "checkRewind":
                case "rewindSeen":
                    await _timelineCtrl.HandleMessage(action, msg);
                    break;

                case "getFriendActivityForUser":
                    _timelineCtrl.HandleGetFriendActivityForUser(msg);
                    break;

                // Inventory

                case "invGetFiles":
                {
                    var invTag = msg["tag"]?.ToString() ?? "gallery";
                    var invFilesForce = msg["force"]?.Value<bool>() ?? false;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Serve from cache if fresh (unless an explicit refresh forced a reload)
                            if (!invFilesForce && _settings.FfcEnabled && _cache.IsFresh(CacheHandler.KeyInventory, TimeSpan.FromHours(12)))
                            {
                                var cached = _cache.LoadRaw(CacheHandler.KeyInventory) as JObject;
                                var cachedSection = cached?["files"]?[invTag] as JArray;
                                if (cachedSection != null)
                                {
                                    Invoke(() => SendToJS("invFiles", new { tag = invTag, files = cachedSection }));
                                    return;
                                }
                            }

                            var files = await _core.Files.GetInventoryFilesAsync(invTag);
                            // Also fetch emojianimated when tag=emoji
                            if (invTag == "emoji")
                            {
                                var animated = await _core.Files.GetInventoryFilesAsync("emojianimated");
                                foreach (var a in animated)
                                    files.Add(a);
                            }
                            var list = files.OfType<JObject>().Select(f =>
                            {
                                var versions = (f["versions"] as JArray) ?? new JArray();
                                var latest = versions.OfType<JObject>()
                                    .LastOrDefault(v => v["status"]?.ToString() == "complete")
                                    ?? versions.OfType<JObject>().LastOrDefault();
                                var rawFileUrl = latest?["file"]?["url"]?.ToString() ?? "";
                                var fileUrl = rawFileUrl;
                                var versionId = latest?["version"]?.Value<int>() ?? 1;
                                var sizeBytes = latest?["file"]?["sizeInBytes"]?.Value<long>() ?? 0;
                                var createdAt = IsoDate(latest?["created_at"] ?? f["created_at"]);
                                return new
                                {
                                    id = f["id"]?.ToString() ?? "",
                                    name = f["name"]?.ToString() ?? "",
                                    tags = (f["tags"] as JArray)?.ToObject<List<string>>() ?? new List<string>(),
                                    animationStyle = f["animationStyle"]?.ToString() ?? "",
                                    maskTag = f["maskTag"]?.ToString() ?? "",
                                    fileUrl,
                                    versionId,
                                    sizeBytes,
                                    createdAt,
                                };
                            }).OrderByDescending(f => f.createdAt).ToList();

                            if (_settings.FfcEnabled) InvCacheSaveSection("files." + invTag, list);
                            Invoke(() => SendToJS("invFiles", new { tag = invTag, files = list }));
                        }
                        catch (Exception ex)
                        {
                            Invoke(() => SendToJS("log", new { msg = $"Inventory load error: {ex.Message}", color = "err" }));
                            Invoke(() => SendToJS("invFiles", new { tag = invTag, files = new object[0], error = ex.Message }));
                        }
                    });
                    break;
                }

                case "invBrowseUpload":
                {
                    var uploadTag = msg["tag"]?.ToString() ?? "gallery";
                    var r = Dialog.FileOpen("png");
                    if (r.IsOk)
                    {
                        var path = r.Path;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var bytes = System.IO.File.ReadAllBytes(path);
                                var (ok, file, error) = await _core.Files.UploadInventoryImageAsync(bytes, uploadTag);
                                if (ok) _cache.Delete(CacheHandler.KeyInventory);
                                if (ok && file != null)
                                {
                                    var versions = (file["versions"] as Newtonsoft.Json.Linq.JArray) ?? new Newtonsoft.Json.Linq.JArray();
                                    var latest = versions.OfType<Newtonsoft.Json.Linq.JObject>()
                                        .LastOrDefault(v => v["status"]?.ToString() == "complete")
                                        ?? versions.OfType<Newtonsoft.Json.Linq.JObject>().LastOrDefault();
                                    var fileUrl = latest?["file"]?["url"]?.ToString() ?? "";
                                    var versionId = latest?["version"]?.Value<int>() ?? 1;
                                    var newFile = new
                                    {
                                        id = file["id"]?.ToString() ?? "",
                                        name = file["name"]?.ToString() ?? "",
                                        tags = (file["tags"] as Newtonsoft.Json.Linq.JArray)?.ToObject<List<string>>() ?? new List<string>(),
                                        animationStyle = file["animationStyle"]?.ToString() ?? "",
                                        maskTag = file["maskTag"]?.ToString() ?? "",
                                        fileUrl,
                                        versionId,
                                        sizeBytes = latest?["file"]?["sizeInBytes"]?.Value<long>() ?? (long)bytes.Length,
                                        createdAt = DateTime.UtcNow.ToString("o"),
                                    };
                                    SendToJS("invUploadResult", new { success = true, tag = uploadTag, file = newFile });
                                }
                                else
                                {
                                    SendToJS("invUploadResult", new { success = false, tag = uploadTag, error });
                                }
                            }
                            catch (Exception ex)
                            {
                                SendToJS("invUploadResult", new { success = false, tag = uploadTag, error = ex.Message });
                            }
                        });
                    }
                    break;
                }

                case "invUploadFromData":
                {
                    var uploadTag2  = msg["tag"]?.ToString() ?? "gallery";
                    var dataB64     = msg["data"]?.ToString() ?? "";
                    var animStyle   = msg["animationStyle"]?.ToString() ?? "";
                    var maskTagVal  = msg["maskTag"]?.ToString() ?? "";

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Strip data-URL prefix (data:image/png;base64,...)
                            var raw = dataB64.Contains(",") ? dataB64.Split(',')[1] : dataB64;
                            var bytes2 = Convert.FromBase64String(raw);

                            var (ok2, file2, error2) = await _core.Files.UploadInventoryImageAsync(bytes2, uploadTag2, animStyle, maskTagVal);
                            if (ok2) _cache.Delete(CacheHandler.KeyInventory);
                            if (ok2 && file2 != null)
                            {
                                var versions2 = (file2["versions"] as Newtonsoft.Json.Linq.JArray) ?? new Newtonsoft.Json.Linq.JArray();
                                var latest2   = versions2.OfType<Newtonsoft.Json.Linq.JObject>()
                                    .LastOrDefault(v => v["status"]?.ToString() == "complete")
                                    ?? versions2.OfType<Newtonsoft.Json.Linq.JObject>().LastOrDefault();
                                var fileUrl2    = latest2?["file"]?["url"]?.ToString() ?? "";
                                var versionId2  = latest2?["version"]?.Value<int>() ?? 1;
                                var newFile2 = new
                                {
                                    id            = file2["id"]?.ToString() ?? "",
                                    name          = file2["name"]?.ToString() ?? "",
                                    tags          = (file2["tags"] as Newtonsoft.Json.Linq.JArray)?.ToObject<List<string>>() ?? new List<string>(),
                                    animationStyle = file2["animationStyle"]?.ToString() ?? "",
                                    maskTag       = file2["maskTag"]?.ToString() ?? "",
                                    fileUrl       = fileUrl2,
                                    versionId     = versionId2,
                                    sizeBytes     = latest2?["file"]?["sizeInBytes"]?.Value<long>() ?? (long)bytes2.Length,
                                    createdAt     = DateTime.UtcNow.ToString("o"),
                                };
                                Invoke(() => SendToJS("invUploadResult", new { success = true, tag = uploadTag2, file = newFile2 }));
                            }
                            else
                            {
                                Invoke(() => SendToJS("invUploadResult", new { success = false, tag = uploadTag2, error = error2 }));
                            }
                        }
                        catch (Exception ex)
                        {
                            Invoke(() => SendToJS("invUploadResult", new { success = false, tag = uploadTag2, error = ex.Message }));
                        }
                    });
                    break;
                }

                case "invDeleteFile":
                {
                    var delFileId = msg["fileId"]?.ToString();
                    if (!string.IsNullOrEmpty(delFileId))
                    {
                        _ = Task.Run(async () =>
                        {
                            var ok = await _core.Files.DeleteInventoryFileAsync(delFileId);
                            if (ok) _cache.Delete(CacheHandler.KeyInventory);
                            Invoke(() => SendToJS("invDeleteResult", new { success = ok, fileId = delFileId }));
                        });
                    }
                    break;
                }

                case "invUploadPrint":
                {
                    var printData      = msg["data"]?.ToString()      ?? "";
                    var printNote      = msg["note"]?.ToString()      ?? "";
                    var printWorldId   = msg["worldId"]?.ToString()   ?? "";
                    var printWorldName = msg["worldName"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(printData))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var raw = printData.Contains(",") ? printData.Split(',')[1] : printData;
                                var bytes = Convert.FromBase64String(raw);
                                var (ok, print, error) = await _core.Inventory.UploadPrintAsync(
                                    bytes, printNote, DateTime.UtcNow, printWorldId, printWorldName);
                                if (ok) _cache.Delete(CacheHandler.KeyInventory);
                                Invoke(() => SendToJS("invPrintUploadResult", new
                                {
                                    success = ok,
                                    error,
                                    printId = print?["id"]?.ToString() ?? "",
                                }));
                            }
                            catch (Exception ex)
                            {
                                Invoke(() => SendToJS("invPrintUploadResult", new { success = false, error = ex.Message, printId = "" }));
                            }
                        });
                    }
                    break;
                }

                case "invGetPrints":
                {
                    var printUserId = _vrcApi.CurrentUserId;
                    var invPrintsForce = msg["force"]?.Value<bool>() ?? false;
                    if (!string.IsNullOrEmpty(printUserId))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                // Serve from cache if fresh (unless an explicit refresh forced a reload)
                                if (!invPrintsForce && _settings.FfcEnabled && _cache.IsFresh(CacheHandler.KeyInventory, TimeSpan.FromHours(12)))
                                {
                                    var cached = _cache.LoadRaw(CacheHandler.KeyInventory) as JObject;
                                    var cachedSection = cached?["prints"] as JArray;
                                    if (cachedSection != null)
                                    {
                                        Invoke(() => SendToJS("invPrints", new { prints = cachedSection }));
                                        return;
                                    }
                                }

                                var prints = await _core.Inventory.GetUserPrintsAsync(printUserId);
                                var list = prints.OfType<JObject>().Select(p =>
                                {
                                    var filesObj = p["files"] as JObject;
                                    var rawPrintUrl = filesObj?["image"]?.ToString()
                                        ?? p["imageUrl"]?.ToString()
                                        ?? p["thumbnailImageUrl"]?.ToString()
                                        ?? "";
                                    var imageUrl = rawPrintUrl;
                                    return new
                                    {
                                        id         = p["id"]?.ToString() ?? "",
                                        authorId   = p["authorId"]?.ToString() ?? "",
                                        authorName = p["authorName"]?.ToString() ?? "",
                                        worldId    = p["worldId"]?.ToString() ?? "",
                                        worldName  = p["worldName"]?.ToString() ?? "",
                                        note       = p["note"]?.ToString() ?? "",
                                        createdAt  = IsoDate(p["createdAt"] ?? p["timestamp"]),
                                        imageUrl,
                                    };
                                }).OrderByDescending(p => p.createdAt).ToList();

                                if (_settings.FfcEnabled) InvCacheSaveSection("prints", list);
                                Invoke(() => SendToJS("invPrints", new { prints = list }));
                            }
                            catch (Exception ex)
                            {
                                Invoke(() => SendToJS("log", new { msg = $"Prints load error: {ex.Message}", color = "err" }));
                                Invoke(() => SendToJS("invPrints", new { prints = new object[0], error = ex.Message }));
                            }
                        });
                    }
                    else
                    {
                        Invoke(() => SendToJS("invPrints", new { prints = new object[0] }));
                    }
                    break;
                }

                case "invGetInventory":
                {
                    var invItemsForce = msg["force"]?.Value<bool>() ?? false;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Serve from cache if fresh (unless an explicit refresh forced a reload)
                            if (!invItemsForce && _settings.FfcEnabled && _cache.IsFresh(CacheHandler.KeyInventory, TimeSpan.FromHours(12)))
                            {
                                var cached = _cache.LoadRaw(CacheHandler.KeyInventory) as JObject;
                                var cachedSection = cached?["inventory"] as JObject;
                                if (cachedSection != null)
                                {
                                    Invoke(() => SendToJS("invInventory", cachedSection));
                                    return;
                                }
                            }

                            var (items, total) = await _core.Inventory.GetInventoryItemsAsync();
                            var list = items.OfType<JObject>().Select(item => new
                            {
                                id          = item["id"]?.ToString() ?? "",
                                name        = item["name"]?.ToString() ?? "Item",
                                description = item["description"]?.ToString() ?? "",
                                itemType    = item["itemType"]?.ToString() ?? "",
                                imageUrl    = item["imageUrl"]?.ToString() ?? item["metadata"]?["imageUrl"]?.ToString() ?? "",
                                isArchived  = item["isArchived"]?.Value<bool>() ?? false,
                                createdAt   = IsoDate(item["created_at"]),
                            }).ToList();
                            var payload = new { items = list, totalCount = total };
                            if (_settings.FfcEnabled) InvCacheSaveSection("inventory", payload);
                            Invoke(() => SendToJS("invInventory", payload));
                        }
                        catch (Exception ex)
                        {
                            Invoke(() => SendToJS("invInventory", new { items = new object[0], error = ex.Message }));
                        }
                    });
                    break;
                }

                case "invDeletePrint":
                {
                    var delPrintId = msg["printId"]?.ToString();
                    if (!string.IsNullOrEmpty(delPrintId))
                    {
                        _ = Task.Run(async () =>
                        {
                            var ok = await _core.Inventory.DeletePrintAsync(delPrintId);
                            if (ok) _cache.Delete(CacheHandler.KeyInventory);
                            Invoke(() => SendToJS("invPrintDeleteResult", new { success = ok, printId = delPrintId }));
                        });
                    }
                    break;
                }

                case "vrcGetProfileDecorations":
                    _ = Task.Run(async () =>
                    {
                        var decos = await _core.Inventory.GetOwnDecorationsAsync();
                        var list = decos.Select(it =>
                        {
                            var tpl = it["templateId"]?.ToString() ?? "";
                            _ = _core.Inventory.ResolveDecorationAsync(tpl);
                            return new
                            {
                                slot       = it["__slot"]?.ToString() ?? "",
                                templateId = tpl,
                                name       = it["name"]?.ToString() ?? "",
                                imageUrl   = it["imageUrl"]?.ToString() ?? it["metadata"]?["imageUrl"]?.ToString() ?? "",
                            };
                        }).Where(x => !string.IsNullOrEmpty(x.templateId)).ToList();
                        Invoke(() => SendToJS("vrcProfileDecorations", new { decorations = list }));
                    });
                    break;

                case "vrcSetProfileDecoration":
                    _ = Task.Run(async () =>
                    {
                        var field = msg["field"]?.ToString() ?? "";
                        var value = msg["value"]?.ToString() ?? "";
                        var ok = await _core.Inventory.SetProfileDecorationAsync(field, value);
                        var url = "";
                        if (ok && !string.IsNullOrEmpty(value))
                        {
                            await _core.Inventory.ResolveDecorationAsync(value);
                            url = ImageCacheHelper.GetVrcPlusUrlIfCached(value);
                        }
                        Invoke(() => SendToJS("vrcSetProfileDecorationResult", new { ok, field, value, url }));
                    });
                    break;

                case "invDownload":
                {
                    var dlUrl = msg["url"]?.ToString();
                    var dlFileName = msg["fileName"]?.ToString() ?? "download.png";
                    if (!string.IsNullOrEmpty(dlUrl))
                    {
                        var rs = Dialog.FileSave("png");
                        if (rs.IsOk)
                        {
                            var savePath = rs.Path;
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var resp = await _vrcApi.GetHttpClient().GetAsync(dlUrl);
                                    if (resp.IsSuccessStatusCode)
                                    {
                                        var bytes = await resp.Content.ReadAsByteArrayAsync();
                                        System.IO.File.WriteAllBytes(savePath, bytes);
                                        SendToJS("log", new { msg = $"Saved: {savePath}", color = "ok" });
                                    }
                                    else
                                    {
                                        SendToJS("log", new { msg = $"Download failed: HTTP {(int)resp.StatusCode}", color = "err" });
                                    }
                                }
                                catch (Exception ex)
                                {
                                    SendToJS("log", new { msg = $"Download error: {ex.Message}", color = "err" });
                                }
                            });
                        }
                    }
                    break;
                }

                case "openUrl":
                case "restartApp":
                    await _authCtrl.HandleMessage(action, msg);
                    break;

                case "getApiHealth":
                    _ = Task.Run(() => FetchApiHealthAsync());
                    break;

                case "getApiHealthDetail":
                    _ = Task.Run(() => SendApiHealthDetailAsync());
                    break;

                case "revealInExplorer":
                {
                    var filePath = msg["path"]?.ToString();
                    if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                    {
#if WINDOWS
                        Process.Start("explorer.exe", $"/select,\"{filePath}\"");
#else
                        _ = Task.Run(() => VRCNext.Services.Helpers.LinuxDesktopHelper.RevealInFileManager(filePath));
#endif
                    }
                    break;
                }

                case "openShortcutFolder":
                {
                    var folder = msg["folder"]?.ToString();
                    string? dir = folder switch
                    {
                        "vrchat_data"  => VrcPathsHelper.AppDataDir(),
                        "vrchat_crash" => VrcPathsHelper.CrashDumpDir(),
                        "vrcn_data"    => Path.Combine(
                                              Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                              "VRCNext"),
                        _ => null
                    };
                    if (!string.IsNullOrEmpty(dir))
                        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                    break;
                }

                // VRChat Tools — config.json + cache management + launch options
                case "vrcConfigGet":
                {
                    _ = Task.Run(() =>
                    {
                        var (cfgJson, cacheBytes) = VrcConfigHelper.ReadConfigAndCacheSize();
                        var prints = new
                        {
                            enabled = _core.Settings.SaveInstancePrints,
                            path = _core.Settings.InstancePrintsPath ?? "",
                            defaultPath = Path.Combine(VrcPathsHelper.PhotoDir(), "Prints"),
                            flagSet = (_core.Settings.VrcLaunchArgs ?? "").Contains("--enable-sdk-log-levels", StringComparison.OrdinalIgnoreCase),
                            logOk = VrcConfigHelper.LogHasApiRequests(),
                        };
                        var inGame = new { cameraRes = VrcConfigHelper.ReadInGameCameraResolution() };
                        Invoke(() => SendToJS("vrcConfigData", new { config = cfgJson, cacheBytes, prints, inGame }));
                    });
                    break;
                }
                case "vrcCacheRefresh":
                {
                    _ = Task.Run(() =>
                    {
                        var bytes = VrcConfigHelper.GetCacheSize();
                        Invoke(() => SendToJS("vrcConfigData", new { cacheBytes = bytes }));
                    });
                    break;
                }
                case "vrcCacheDeleteAll":
                {
                    _ = Task.Run(() =>
                    {
                        bool ok = VrcConfigHelper.DeleteAllCache(out var err);
                        var bytes = VrcConfigHelper.GetCacheSize();
                        Invoke(() =>
                        {
                            SendToJS("vrcConfigData", new { cacheBytes = bytes });
                            SendToJS("toast", new { ok, msg = ok ? "VRChat cache deleted" : ("Cache delete failed: " + err) });
                        });
                    });
                    break;
                }
                case "vrcCacheSweep":
                {
                    _ = Task.Run(() =>
                    {
                        var removed = VrcConfigHelper.SweepCache(out var err);
                        var bytes = VrcConfigHelper.GetCacheSize();
                        Invoke(() =>
                        {
                            SendToJS("vrcConfigData", new { cacheBytes = bytes });
                            SendToJS("toast", new { ok = err == null, msg = err ?? $"Swept {removed} cache entries" });
                        });
                    });
                    break;
                }
                case "vrcConfigSave":
                {
                    if (msg["prints"] is JObject pr)
                    {
                        _core.Settings.SaveInstancePrints = pr["enabled"]?.Value<bool>() ?? false;
                        _core.Settings.InstancePrintsPath = (pr["path"]?.ToString() ?? "").Trim();
                        _core.Settings.Save();
                    }
                    var cfg = msg["config"] as JObject;
                    if (cfg != null)
                    {
                        _ = Task.Run(() =>
                        {
                            bool ok = VrcConfigHelper.WriteConfig(cfg, out var err);
                            Invoke(() => SendToJS("toast", new { ok, msg = ok ? "VRChat config saved" : ("Config save failed: " + err) }));
                        });
                    }
                    break;
                }
                case "vrcAddSdkLogFlag":
                {
                    const string flag = "--enable-sdk-log-levels";
                    var cur = (_core.Settings.VrcLaunchArgs ?? "").Trim();
                    if (!cur.Contains(flag, StringComparison.OrdinalIgnoreCase))
                    {
                        _core.Settings.VrcLaunchArgs = (cur.Length > 0 ? cur + " " : "") + flag;
                        _core.Settings.Save();
                    }
                    Invoke(() => SendToJS("toast", new { ok = true, msg = "Launch option set: " + flag }));
                    break;
                }
                case "vrcLaunchOptionsGet":
                {
                    Invoke(() => SendToJS("vrcLaunchOptionsData", new
                    {
                        path = _core.Settings.VrcPath ?? "",
                        args = _core.Settings.VrcLaunchArgs ?? ""
                    }));
                    break;
                }
                case "vrcLaunchOptionsSave":
                {
                    var newPath = msg["path"]?.ToString() ?? "";
                    var newArgs = (msg["args"]?.ToString() ?? "").Trim();
                    // Validate path override the way VRCX-0 does (must end in launch.exe if .exe).
                    if (!string.IsNullOrEmpty(newPath) && newPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        && !newPath.EndsWith("launch.exe", StringComparison.OrdinalIgnoreCase)
                        && !newPath.EndsWith("VRChat.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        SendToJS("toast", new { ok = false, msg = "Invalid VRChat path (must end with launch.exe or VRChat.exe)" });
                        break;
                    }
                    _core.Settings.VrcPath = newPath;
                    _core.Settings.VrcLaunchArgs = newArgs;
                    try { _core.Settings.Save(); } catch { }
                    SendToJS("toast", new { ok = true, msg = "Launch options saved" });
                    SendToJS("vrcLaunchOptionsData", new { path = newPath, args = newArgs });
                    break;
                }

                // Discord Rich Presence
                case "dpStart":
                case "dpStop":
                    _discordCtrl.HandleMessage(action, msg);
                    break;

                // Permini — permanent auto-invite list
                case "perminiGet":
                {
                    var rawObj = _cache.LoadRaw(CacheHandler.KeyPermini);
                    var raw = rawObj is Newtonsoft.Json.Linq.JArray ja ? ja : new Newtonsoft.Json.Linq.JArray();
                    // Rebuild in-memory lookup
                    _core.PerminiList.Clear();
                    foreach (var item in raw.OfType<Newtonsoft.Json.Linq.JObject>())
                    {
                        var uid = item["userId"]?.ToString();
                        if (!string.IsNullOrEmpty(uid))
                            _core.PerminiList[uid] = ParsePerminiEntry(item);
                    }
                    Invoke(() => SendToJS("perminiData", raw));
                    break;
                }

                case "perminiSave":
                {
                    var list = msg["list"] as Newtonsoft.Json.Linq.JArray;
                    if (list != null)
                    {
                        _cache.Save(CacheHandler.KeyPermini, list);
                        // Rebuild in-memory lookup
                        _core.PerminiList.Clear();
                        foreach (var item in list.OfType<Newtonsoft.Json.Linq.JObject>())
                        {
                            var uid = item["userId"]?.ToString();
                            if (!string.IsNullOrEmpty(uid))
                                _core.PerminiList[uid] = ParsePerminiEntry(item);
                        }
                        _core.SendToJS("toast", new { ok = true, msg = "Saved" });
                    }
                    break;
                }

                case "overlayThemeColors":
                    await _vroCtrl.HandleMessage(action, msg);
                    break;

                // Crash report modal
                case "sendCrashReport":
                    _ = SendPendingCrashReportAsync();
                    break;
                case "dismissCrashReport":
                    Services.CrashHandler.ClearPendingCrash();
                    break;

                case "trayNotification":
#if WINDOWS
                    if (_settings.MinimizeToTray && _settings.TrayNotificationsEnabled)
                    {
                        var tnTitle  = msg["title"]?.ToString()    ?? "";
                        var tnSub   = msg["subtitle"]?.ToString()  ?? "";
                        var tnAccent = msg["accentKey"]?.ToString() ?? "accent";
                        var tnImage  = msg["imageUrl"]?.ToString()  ?? "";
                        _trayService?.ShowNotification(tnTitle, tnSub, tnImage, tnAccent);
                    }
#endif
                    break;

                // VR Wrist Overlay
                case "vroConnect":
                case "vroDisconnect":
                case "vroShow":
                case "vroHide":
                case "vroConfig":
                case "vroAutoSave":
                case "vroToastConfig":
                case "vroWaterConfig":
                case "vroRecordKeybind":
                case "vroCancelRecording":
                case "vroScaleConfig":
                case "vroRecordScaleKeybind":
                case "vroCancelScaleRecording":
                    await _vroCtrl.HandleMessage(action, msg);
                    break;

                case "vrcGetNews":
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var http = new System.Net.Http.HttpClient();
                            http.DefaultRequestHeaders.Add("User-Agent", AppInfo.UserAgent);
                            http.DefaultRequestHeaders.Add("Accept", "application/json");
                            var json = await http.GetStringAsync("https://ask.vrchat.com/c/official/31.json");
                            var data = JObject.Parse(json);
                            var topics = data["topic_list"]?["topics"] as JArray ?? new JArray();
                            var items = topics.Take(6).Select(t => new
                            {
                                id      = t["id"]?.ToString() ?? "",
                                title   = t["title"]?.ToString() ?? "",
                                link    = $"https://ask.vrchat.com/t/{t["slug"]}/{t["id"]}",
                                pubDate = NewsIsoDate(t["created_at"]),
                                img     = t["image_url"]?.ToString() ?? "",
                                excerpt = t["excerpt"]?.ToString() ?? "",
                            }).ToArray();
                            Invoke(() => SendToJS("vrcNews", new { items }));
                        }
                        catch (Exception ex)
                        {
                            Invoke(() => SendToJS("log", new { msg = $"[News] fetch failed: {ex.Message}", color = "warn" }));
                        }
                    });
                    break;

                case "vrcGetNewsArticle":
                {
                    var newsTopicId = msg["id"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(newsTopicId)) break;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var http = new System.Net.Http.HttpClient();
                            http.DefaultRequestHeaders.Add("User-Agent", AppInfo.UserAgent);
                            http.DefaultRequestHeaders.Add("Accept", "application/json");
                            var json = await http.GetStringAsync($"https://ask.vrchat.com/t/{newsTopicId}.json");
                            var data = JObject.Parse(json);
                            var posts = data["post_stream"]?["posts"] as JArray ?? new JArray();
                            var html = (posts.FirstOrDefault() as JObject)?["cooked"]?.ToString() ?? "";
                            var title = data["title"]?.ToString() ?? "";
                            var slug = data["slug"]?.ToString() ?? "";
                            var link = $"https://ask.vrchat.com/t/{slug}/{newsTopicId}";
                            Invoke(() => SendToJS("vrcNewsArticle", new { id = newsTopicId, title, html, link, port = _core.HttpPort }));
                        }
                        catch (Exception ex)
                        {
                            Invoke(() => SendToJS("vrcNewsArticle", new { id = newsTopicId, error = ex.Message }));
                        }
                    });
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            SendToJS("log", new { msg = $"Error: {ex.Message}", color = "err" });
        }
    }

#if !WINDOWS
    private static readonly string[] _windowsOnlyActionPrefixes =
        { "vf", "kxd", "chatbox", "osc", "sf", "st", "fs", "dp", "vc", "vro", "as" };

    private static bool IsWindowsOnlyAction(string action)
    {
        if (action == "startRelay" || action == "stopRelay") return true;
        foreach (var prefix in _windowsOnlyActionPrefixes)
        {
            if (action.Length > prefix.Length
                && action.StartsWith(prefix, StringComparison.Ordinal)
                && char.IsUpper(action[prefix.Length]))
                return true;
        }
        return false;
    }
#endif

    private static string NewsIsoDate(JToken? token)
    {
        if (token == null) return "";
        try { return token.Value<DateTime>().ToUniversalTime().ToString("o"); }
        catch { return token.ToString(); }
    }

    private void EnrichAvatarFromCache(JObject a, string avatarId)
        => EnrichAvatarFromCache(_core.TimeEngine, a, avatarId);

    internal static void EnrichAvatarFromCache(UnifiedTimeEngine engine, JObject a, string avatarId)
    {
        if (a == null || string.IsNullOrEmpty(avatarId)) return;
        UnifiedTimeEngine.AvatarDetailCache? c;
        try { c = engine.GetAvatarDetail(avatarId); }
        catch { return; }
        if (c == null) return;

        void Str(string key, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (!string.IsNullOrEmpty(a[key]?.ToString())) return;
            a[key] = value;
        }

        Str("name", c.Name);
        Str("authorName", c.AuthorName);
        Str("authorId", c.AuthorId);
        Str("releaseStatus", c.ReleaseStatus);
        Str("description", c.Description);
        Str("created_at", DateTimeHelper.Iso(c.CreatedAt));
        Str("updated_at", DateTimeHelper.Iso(c.UpdatedAt));
        if ((a["tags"] as JArray)?.Count is null or 0 && c.Tags.Count > 0) a["tags"] = JArray.FromObject(c.Tags);

        var (livePc, liveQuest, liveIos) = ResolveAvatarPerf(a);
        var pcPerf    = livePc.Length    > 0 ? livePc    : ValidPerf(c.PcPerf);
        var questPerf = liveQuest.Length > 0 ? liveQuest : ValidPerf(c.QuestPerf);
        var iosPerf   = liveIos.Length   > 0 ? liveIos   : ValidPerf(c.IosPerf);

        if (pcPerf.Length > 0 || questPerf.Length > 0 || iosPerf.Length > 0)
            a["performance"] = new JObject { ["pc"] = pcPerf, ["quest"] = questPerf, ["ios"] = iosPerf };

        if (a["compatibility"] == null && a["unityPackages"] == null && (c.HasPC || c.HasQuest || c.HasIos))
        {
            var compat = new JArray();
            if (c.HasPC)    compat.Add("pc");
            if (c.HasQuest) compat.Add("android");
            if (c.HasIos)   compat.Add("ios");
            a["compatibility"] = compat;
        }
    }

    private void RemoveFromCachedList(string cacheKey, string arrayProp, string entityId)
    {
        try
        {
            if (!_settings.FfcEnabled) return;
            if (_cache.LoadRaw(cacheKey) is not JObject root) return;
            if (root[arrayProp] is not JArray arr) return;
            var kept = new JArray();
            bool removed = false;
            foreach (var item in arr.OfType<JObject>())
            {
                if (item["id"]?.ToString() == entityId) { removed = true; continue; }
                kept.Add(item);
            }
            if (!removed) return;
            root[arrayProp] = kept;
            _cache.Save(cacheKey, root);
        }
        catch { }
    }


    private sealed class VrcLogEntry
    {
        public string timestamp { get; set; } = "";
        public string level     { get; set; } = "";
        public string category  { get; set; } = "";
        public string message   { get; set; } = "";
        public string raw       { get; set; } = "";
        public int    lineNumber { get; set; }
        public List<string> continuation { get; } = new();
    }

    private static readonly string[] VrcLogLevels = { "Debug", "Warning", "Error" };

    private static (string timestamp, string level, string message)? ParseVrcLogHeader(string line)
    {
        if (line.Length < 20) return null;
        var stamp = line[..19];
        if (!DateTime.TryParseExact(stamp, "yyyy.MM.dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _))
            return null;

        var rest = line[19..].TrimStart();
        foreach (var level in VrcLogLevels)
        {
            if (!rest.StartsWith(level, StringComparison.Ordinal)) continue;
            var afterLevel = rest[level.Length..].TrimStart();
            if (!afterLevel.StartsWith('-')) return null;
            return (stamp, level, afterLevel[1..].TrimStart());
        }
        return null;
    }

    private static string ExtractVrcLogCategory(string message)
    {
        var trimmed = message.TrimStart();
        if (!trimmed.StartsWith('[')) return "";
        var close = trimmed.IndexOf(']');
        if (close < 0) return "";
        return StripVrcRichText(trimmed[1..close]).Trim();
    }

    private static readonly System.Text.RegularExpressions.Regex VrcRichTextRe = new(
        @"</?(?:color|b|i|u|s|size|material|quad|align|alpha|cspace|font|indent|line-height|link|lowercase|uppercase|smallcaps|margin|mark|mspace|noparse|nobr|page|pos|space|sprite|strikethrough|style|sub|sup|voffset|width)(?:=[^>]*)?>",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string StripVrcRichText(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('<') < 0) return text;
        return VrcRichTextRe.Replace(text, "");
    }

    internal static void EnrichWorldDatesFromCache(UnifiedTimeEngine engine, JObject w, string worldId)
    {
        if (w == null || string.IsNullOrEmpty(worldId)) return;
        UnifiedTimeEngine.WorldDetailCache? c;
        try { c = engine.GetWorldDetail(worldId); }
        catch { return; }
        if (c == null) return;
        if (string.IsNullOrEmpty(w["created_at"]?.ToString()) && !string.IsNullOrEmpty(c.Published)) w["created_at"] = c.Published;
        if (string.IsNullOrEmpty(w["updated_at"]?.ToString()) && !string.IsNullOrEmpty(c.Updated)) w["updated_at"] = c.Updated;
    }

    private void CacheAvatarDetailFrom(JObject avatar)
        => CacheAvatarDetailFrom(_core.TimeEngine, avatar);

    internal static void CacheAvatarDetailFrom(UnifiedTimeEngine engine, JObject avatar,
        bool? platformPC = null, bool? platformQuest = null, bool? platformIos = null)
    {
        try
        {
            var id = avatar?["id"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(id)) return;

            UnifiedTimeEngine.AvatarDetailCache? old = null;
            try { old = engine.GetAvatarDetail(id); } catch { }

            var packages = (avatar!["unityPackages"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            var realPkgs = packages.Where(p => p["variant"]?.ToString() != "impostor").ToList();
            var (pcPerf, questPerf, iosPerf) = ResolveAvatarPerf(avatar);

            var hasPC       = platformPC    ?? (realPkgs.Any(p => p["platform"]?.ToString() == "standalonewindows") || pcPerf.Length > 0);
            var hasQuest    = platformQuest ?? (realPkgs.Any(p => p["platform"]?.ToString() == "android")           || questPerf.Length > 0);
            var hasIos      = platformIos   ?? (realPkgs.Any(p => p["platform"]?.ToString() == "ios")               || iosPerf.Length > 0);
            var hasImpostor = packages.Any(p => p["variant"]?.ToString() == "impostor");

            if (old != null)
            {
                if (packages.Count == 0)
                {
                    hasPC       = hasPC       || old.HasPC;
                    hasQuest    = hasQuest    || old.HasQuest;
                    hasIos      = hasIos      || old.HasIos;
                    hasImpostor = hasImpostor || old.HasImpostor;
                }
                if (pcPerf.Length == 0)    pcPerf    = old.PcPerf;
                if (questPerf.Length == 0) questPerf = old.QuestPerf;
                if (iosPerf.Length == 0)   iosPerf   = old.IosPerf;
            }

            string Pick(string key, string? fallback)
            {
                var v = avatar[key]?.ToString() ?? "";
                return string.IsNullOrEmpty(v) ? (fallback ?? "") : v;
            }

            var tags = avatar["tags"]?.ToObject<List<string>>() ?? new List<string>();
            if (tags.Count == 0 && old != null) tags = old.Tags;

            var createdAt = DateTimeHelper.Iso(Pick("created_at", old?.CreatedAt));
            var updatedAt = DateTimeHelper.Iso(Pick("updated_at", old?.UpdatedAt));

            var hasMeta = createdAt.Length > 0 || updatedAt.Length > 0 || tags.Count > 0;
            var hasPerf = pcPerf.Length > 0 || questPerf.Length > 0 || iosPerf.Length > 0;
            if (!hasMeta && !hasPerf) return;

            var version = avatar["version"]?.Value<int>() ?? 0;
            if (version == 0 && old != null) version = old.Version;

            engine.SaveAvatarDetail(
                id,
                Pick("name", old?.Name),
                Pick("authorName", old?.AuthorName),
                Pick("authorId", old?.AuthorId),
                Pick("thumbnailImageUrl", old?.ThumbnailImageUrl),
                Pick("imageUrl", old?.ImageUrl),
                Pick("releaseStatus", old?.ReleaseStatus),
                version,
                createdAt,
                updatedAt,
                Pick("description", old?.Description),
                tags,
                hasPC, hasQuest, hasImpostor, pcPerf, questPerf, hasIos, iosPerf);
        }
        catch { }
    }

    private static object? AvatarDetailPayload(JObject avatar)
    {
        try
        {
            var (pc, quest, ios) = ResolveAvatarPerf(avatar);
            return new
            {
                name = avatar["name"]?.ToString() ?? "",
                authorName = avatar["authorName"]?.ToString() ?? "",
                releaseStatus = avatar["releaseStatus"]?.ToString() ?? "",
                created_at = DateTimeHelper.Iso(avatar["created_at"]),
                updated_at = DateTimeHelper.Iso(avatar["updated_at"]),
                tags = (avatar["tags"] as JArray)?.Select(x => x.ToString()).ToArray() ?? Array.Empty<string>(),
                performance = new { pc, quest, ios },
            };
        }
        catch { return null; }
    }

    private static object AvtrdbPerf(JObject a)
    {
        var p = a["performance"];
        return new
        {
            pc    = p?["pc_rating"]?.ToString() ?? "",
            quest = p?["android_rating"]?.ToString() ?? "",
            ios   = p?["ios_rating"]?.ToString() ?? "",
        };
    }

    private static object AvtrIcuPerf(JObject a)
    {
        var p = a["performanceRating"] ?? a["performance"];
        return new
        {
            pc    = p?["standalonewindows"]?.ToString() ?? p?["pc"]?.ToString() ?? "",
            quest = p?["android"]?.ToString() ?? p?["quest"]?.ToString() ?? "",
            ios   = p?["ios"]?.ToString() ?? "",
        };
    }

    private static CoreLibrary.PerminiEntry ParsePerminiEntry(JObject item) => new()
    {
        AllowActive     = item["allowActive"]?.Value<bool>() ?? false,
        AllowAskMe      = item["allowAskMe"]?.Value<bool>()  ?? false,
        AllowDnD        = item["allowDnD"]?.Value<bool>()    ?? false,
        ScheduleEnabled = item["scheduleEnabled"]?.Value<bool>() ?? false,
        Start           = item["start"]?.ToString() ?? "09:00",
        End             = item["end"]?.ToString()   ?? "17:00",
        Days            = (item["days"] as JArray)?.Select(d => d.Value<int>()).ToList() ?? new(),
    };

    private static readonly string[] PerfOrder = { "excellent", "good", "medium", "poor", "verypoor" };

    private static string ValidPerf(string? v)
    {
        var k = new string((v ?? "").ToLowerInvariant().Where(char.IsLetter).ToArray());
        return Array.IndexOf(PerfOrder, k) >= 0 ? v! : "";
    }

    private static int PerfRank(string v)
    {
        var k = new string((v ?? "").ToLowerInvariant().Where(char.IsLetter).ToArray());
        var i = Array.IndexOf(PerfOrder, k);
        return i < 0 ? 99 : i;
    }

    private static string BestPkgPerf(List<JObject> pkgs, string platform) => pkgs
        .Where(p => p["platform"]?.ToString() == platform)
        .Select(p => ValidPerf(p["performanceRating"]?.ToString()))
        .Where(x => x.Length > 0)
        .OrderBy(PerfRank)
        .FirstOrDefault() ?? "";

    private static readonly System.Text.RegularExpressions.Regex _bundleUrlRe =
        new(@"/file/(file_[0-9a-fA-F-]+)/(\d+)/", System.Text.RegularExpressions.RegexOptions.Compiled);

    private void SendCachedAvatarAnalysis(string avatarId)
    {
        var rows = _core.TimeEngine.GetAvatarAnalysis(avatarId);
        if (rows.Count == 0) return;
        var platforms = new JObject();
        foreach (var r in rows)
        {
            if (string.IsNullOrEmpty(r.Json)) continue;
            try { platforms[r.Platform] = JObject.Parse(r.Json); } catch { }
        }
        if (!platforms.HasValues) return;
        Invoke(() => SendToJS("vrcAvatarAnalysis", new { avatarId, platforms, pending = false, cached = true }));
    }

    private async Task FetchAvatarAnalysisAsync(JObject avatar)
    {
        var avatarId = avatar["id"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(avatarId)) return;
        var me = _core.VrcApi.CurrentUserId;
        if (string.IsNullOrEmpty(me) || avatar["authorId"]?.ToString() != me) return;

        var targets = new Dictionary<string, (string fileId, int version)>();
        foreach (var p in avatar["unityPackages"] as JArray ?? new JArray())
        {
            var variant = p["variant"]?.ToString() ?? "standard";
            if (variant != "standard" && variant != "security") continue;
            var platform = p["platform"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(platform)) continue;
            var m = _bundleUrlRe.Match(p["assetUrl"]?.ToString() ?? "");
            if (!m.Success || !int.TryParse(m.Groups[2].Value, out var version)) continue;
            if (targets.ContainsKey(platform) && variant != "security") continue;
            targets[platform] = (m.Groups[1].Value, version);
        }
        if (targets.Count == 0) return;

        var platforms = new JObject();
        var pending = false;
        foreach (var (platform, t) in targets)
        {
            var (status, data) = await _core.Avatars.GetFileAnalysisAsync(t.fileId, t.version, "security");
            if (status == 202 || (data != null && data["success"]?.Value<bool>() != true)) { pending = true; continue; }
            if (data == null) continue;
            data.Remove("encryptionKey");
            _core.TimeEngine.SaveAvatarAnalysis(avatarId, platform, t.fileId, t.version, data.ToString(Newtonsoft.Json.Formatting.None));
            platforms[platform] = data;
        }
        if (!platforms.HasValues && !pending) return;
        Invoke(() => SendToJS("vrcAvatarAnalysis", new { avatarId, platforms, pending, cached = false }));
    }

    private static (string pc, string quest, string ios) ResolveAvatarPerf(JObject avatar)
    {
        var perf = avatar["performance"] as JObject;
        string Obj(params string[] keys)
        {
            foreach (var k in keys)
            {
                var v = ValidPerf(perf?[k]?.ToString());
                if (v.Length > 0) return v;
            }
            return "";
        }
        var pkgs = (avatar["unityPackages"] as JArray)?.OfType<JObject>()
            .Where(p => p["variant"]?.ToString() != "impostor").ToList() ?? new List<JObject>();

        var pc    = Obj("pc", "standalonewindows"); if (pc.Length == 0)    pc    = BestPkgPerf(pkgs, "standalonewindows");
        var quest = Obj("quest", "android");        if (quest.Length == 0) quest = BestPkgPerf(pkgs, "android");
        var ios   = Obj("ios");                     if (ios.Length == 0)   ios   = BestPkgPerf(pkgs, "ios");
        return (pc, quest, ios);
    }

    private void CacheSearchAvatar(JObject a)
    {
        try
        {
            var id = a?["id"]?.ToString() ?? "";
            if (!id.StartsWith("avtr_", StringComparison.Ordinal)) return;

            var compat = (a!["compatibility"] as JArray)?.Select(x => x.ToString()).ToList() ?? new List<string>();
            if (compat.Count == 0)
            {
                CacheAvatarDetailFrom(_core.TimeEngine, a);
                return;
            }

            var (pc, quest, ios) = ResolveAvatarPerf(a);
            CacheAvatarDetailFrom(_core.TimeEngine, a,
                platformPC:    pc.Length > 0    || compat.Contains("pc") || compat.Contains("standalonewindows"),
                platformQuest: quest.Length > 0 || compat.Contains("android") || compat.Contains("quest"),
                platformIos:   ios.Length > 0   || compat.Contains("ios"));
        }
        catch { }
    }
}
