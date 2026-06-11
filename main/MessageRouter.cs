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
            catch { }
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
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent);
            var userId = _vrcApi.CurrentUserId;
            var payload = new { avatar_ids = avatarIds, attribution = string.IsNullOrEmpty(userId) ? null : userId };
            var json = JsonConvert.SerializeObject(payload);
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

            switch (action)
            {
                case "ready":
                    // Signal platform to JS (hides Windows-only tabs on Linux)
                    SendToJS("setPlatform", new { isLinux = !OperatingSystem.IsWindows() });
                    _windowCtrl.InstallChrome();
                    // Debug: show what Load() did
                    if (AppSettings.LastLoadError != null)
                        SendToJS("log", new { msg = $"[LOAD ERROR] {AppSettings.LastLoadError}", color = "err" });
                    SendToJS("log", new { msg = $"[LOAD] {AppSettings.LoadDebugInfo}", color = "sec" });
                    SendToJS("log", new { msg = $"[STARTUP] Webhooks: {string.Join(", ", _settings.Webhooks.Select((w,i) => $"#{i+1} \"{w.Name}\" url={w.Url?.Length ?? 0}ch {(w.Enabled?"ON":"off")}"))}", color = "sec" });
                    _authCtrl.HandleReady();
                    // Check for crash report from previous session — show modal after UI is ready
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1200);
                        CheckAndShowPendingCrash();
                    });
                    break;

                // Setup / Auth / Settings — delegated to AuthController
                case "setupReady":
                case "setupDone":
                case "forceTrim":
                case "resetSetup":
                case "clearImgCache":
                case "getImgCacheSize":
                case "optimizeImgCache":
                case "clearFfcCache":
                case "dbAnalyze":
                case "dbOptimize":
                case "dbBackup":
                case "regBackup":
                case "forceFfcAll":
                case "setupSaveLanguage":
                case "setupSaveStartWithWindows":
                case "setupSaveVrcPath":
                case "setupSavePhotoDir":
                case "setupBrowsePhotoDir":
                    await _authCtrl.HandleMessage(action, msg);
                    break;

                // Window chrome (borderless)
                case "windowMinimize":
                case "windowMaximize":
                case "windowClose":
                case "windowDragStart":
                case "windowResizeStart":
                    _windowCtrl.HandleMessage(action, msg);
                    break;

                case "startRelay":
                case "stopRelay":
                    _relayCtrl.HandleMessage(action, msg);
                    break;

                case "getCursorFiles":
                case "getCustomThemes":
                    _windowCtrl.HandleMessage(action, msg);
                    break;

                case "saveSettings":
                case "loadTranslation":
                    await _authCtrl.HandleMessage(action, msg);
                    break;

                case "vrcnPlusCheckEntitlement":
                case "vrcnPlusGetTheme":
                case "vrcnPlusSaveTheme":
                case "vrcnPlusDeleteTheme":
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

                case "importVrcxSelect":
                case "importVrcxStart":
                    await _timelineCtrl.HandleMessage(action, msg);
                    break;

                // Photo/Library actions delegated to PhotosController
                case "deletePost":
                case "manualPost":
                case "dropFiles":
                case "scanLibrary":
                case "scanLibraryForce":
                case "loadLibraryPage":
                case "deleteLibraryFile":
                case "copyImageToClipboard":
                case "addFavorite":
                case "removeFavorite":
                case "setDesktopBackground":
                    await _photos.HandleMessage(action, msg);
                    break;

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

                // Update own status
                case "vrcUpdateStatus":
                    var newStatus = msg["status"]?.ToString() ?? "active";
                    var newDesc = msg["statusDescription"]?.ToString() ?? "";
                    await _friends.UpdateStatusAsync(newStatus, newDesc);
                    break;

                // Update own profile (bio, pronouns, links, languages, icon, banner)
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
                        await _friends.GetFriendDetailAsync(friendId);
                    break;

                case "vrcGetFriendPreview":
                case "vrcGetUserBasic":
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
                    await _instance.HandleMessage(action, msg);
                    break;

                // User Notes
                case "vrcUpdateNote":
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
                    if (!string.IsNullOrWhiteSpace(avSearchQuery))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                int avLimit;
                                List<object> list;

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
                                        sources           = new[] { "avtricu" },
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
                                    await Task.WhenAll(avtrdbTask, avtrIcuTask);

                                    var dbEntries = avtrdbTask.Result.Cast<JObject>()
                                        .Select(a => new {
                                            id                = a["vrc_id"]?.ToString() ?? a["id"]?.ToString() ?? "",
                                            name              = a["name"]?.ToString() ?? "",
                                            thumbnailImageUrl = ImageCacheHelper.GetAvatarUrl(a["vrc_id"]?.ToString() ?? a["id"]?.ToString(), a["image_url"]?.ToString() ?? a["thumbnailImageUrl"]?.ToString() ?? a["imageUrl"]?.ToString()),
                                            imageUrl          = ImageCacheHelper.GetAvatarUrl(a["vrc_id"]?.ToString() ?? a["id"]?.ToString(), a["image_url"]?.ToString() ?? a["imageUrl"]?.ToString()),
                                            authorName        = a["author"]?["name"]?.ToString() ?? a["authorName"]?.ToString() ?? "",
                                            description       = a["description"]?.ToString() ?? "",
                                            unityPackages     = (a["unityPackages"] as JArray ?? new JArray()).Select(p => new { platform = p["platform"]?.ToString() ?? "", variant = p["variant"]?.ToString() ?? "" }).ToArray(),
                                            compatibility     = (a["compatibility"] as JArray ?? new JArray()).Select(p => p.ToString()).ToArray(),
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
                                        })
                                        .Where(x => !string.IsNullOrEmpty(x.id))
                                        .ToList();

                                    var dbIds  = new HashSet<string>(dbEntries.Select(x => x.id));
                                    var icuIds = new HashSet<string>(icuEntries.Select(x => x.id));

                                    list = new List<object>();
                                    foreach (var a in dbEntries)
                                    {
                                        var srcs = icuIds.Contains(a.id) ? new[] { "avtrdb", "avtricu" } : new[] { "avtrdb" };
                                        list.Add(new { a.id, a.name, a.thumbnailImageUrl, a.imageUrl, a.authorName, releaseStatus = "public", a.description, a.unityPackages, a.compatibility, sources = srcs });
                                    }
                                    foreach (var a in icuEntries)
                                    {
                                        if (!dbIds.Contains(a.id))
                                            list.Add(new { a.id, a.name, a.thumbnailImageUrl, a.imageUrl, a.authorName, releaseStatus = "public", a.description, unityPackages = Array.Empty<object>(), a.compatibility, sources = new[] { "avtricu" } });
                                    }
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
                                        unityPackages     = (a["unityPackages"] as JArray ?? new JArray())
                                            .Select(p => new { platform = p["platform"]?.ToString() ?? "", variant = p["variant"]?.ToString() ?? "" })
                                            .ToArray(),
                                        compatibility     = (a["compatibility"] as JArray ?? new JArray()).Select(p => p.ToString()).ToArray(),
                                        sources           = new[] { "avtrdb" },
                                    }).ToList();
                                }

                                Invoke(() => SendToJS("vrcAvatarSearchResults", new
                                {
                                    results = list,
                                    page    = avSearchPage,
                                    hasMore = list.Count >= avLimit,
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
                        foreach (var item in batchArr.OfType<JObject>())
                        {
                            var bid  = item["id"]?.ToString();
                            var bUrl = item["imageUrl"]?.ToString();
                            if (!string.IsNullOrEmpty(bid))
                                mapping[bid] = ImageCacheHelper.GetAvatarUrl(bid, bUrl);
                        }
                        SendToJS("vrcAvatarBatchCached", mapping);
                    }
                    break;

                case "vrcCheckAvatars":
                {
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
                                foreach (var id in toCheck)
                                {
                                    try
                                    {
                                        var av = await _core.Avatars.GetAvatarAsync(id);
                                        if (av == null) { deleted.Add(id); lock (_deletedAvatarIds) _deletedAvatarIds.Add(id); }
                                        else exists.Add(id);
                                    }
                                    catch { deleted.Add(id); lock (_deletedAvatarIds) _deletedAvatarIds.Add(id); }
                                    await Task.Delay(250);
                                }
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
                        var list = res.Cast<JObject>().Select(u => new {
                            id = u["id"]?.ToString() ?? "", displayName = u["displayName"]?.ToString() ?? "",
                            image = ImageCacheHelper.GetUserUrl(u["id"]?.ToString(), VRChatApiService.GetUserImage(u)), status = u["status"]?.ToString() ?? "offline",
                            statusDescription = u["statusDescription"]?.ToString() ?? "", bio = u["bio"]?.ToString() ?? "",
                            isFriend = u["isFriend"]?.Value<bool>() ?? false,
                            location = u["location"]?.ToString() ?? "",
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
                            return new {
                            id = wid2, name = w["name"]?.ToString() ?? "",
                            imageUrl = wurl, thumbnailImageUrl = wurl,
                            authorName = w["authorName"]?.ToString() ?? "", occupants = w["occupants"]?.Value<int>() ?? 0,
                            capacity = w["capacity"]?.Value<int>() ?? 0, favorites = w["favorites"]?.Value<int>() ?? 0,
                            visits = w["visits"]?.Value<int>() ?? 0, description = w["description"]?.ToString() ?? "",
                            tags = w["tags"]?.ToObject<List<string>>() ?? new(),
                            worldTimeSeconds = _core.TimeEngine.GetWorldStats(wid2).totalSeconds,
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
                                        ex.CreatedAt, ex.UpdatedAt,
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
                        var avdCached = _core.TimeEngine.GetAvatarDetail(avdId);
                        if (avdCached != null)
                            Invoke(() => SendToJS("vrcAvatarDetail", new {
                                id = avdId, name = avdCached.Name, authorName = avdCached.AuthorName,
                                authorId = avdCached.AuthorId, thumbnailImageUrl = ImageCacheHelper.GetAvatarUrl(avdId, avdCached.ThumbnailImageUrl),
                                imageUrl = ImageCacheHelper.GetAvatarUrl(avdId, avdCached.ImageUrl), releaseStatus = avdCached.ReleaseStatus,
                                version = avdCached.Version, created_at = avdCached.CreatedAt,
                                updated_at = avdCached.UpdatedAt, description = avdCached.Description,
                                tags = avdCached.Tags, hasPC = avdCached.HasPC, hasQuest = avdCached.HasQuest,
                                hasImpostor = avdCached.HasImpostor, pcPerf = avdCached.PcPerf, questPerf = avdCached.QuestPerf,
                            }));
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
                            var hasImpostor = packages.Any(p => p["variant"]?.ToString() == "impostor");
                            var pcPerf    = realPkgs.FirstOrDefault(p => p["platform"]?.ToString() == "standalonewindows")?["performanceRating"]?.ToString() ?? "";
                            var questPerf = realPkgs.FirstOrDefault(p => p["platform"]?.ToString() == "android")?["performanceRating"]?.ToString() ?? "";
                            var perf = avatar["performance"] as JObject;
                            if (string.IsNullOrEmpty(pcPerf))    pcPerf    = perf?["standalonewindows"]?.ToString() ?? "";
                            if (string.IsNullOrEmpty(questPerf)) questPerf = perf?["android"]?.ToString() ?? "";
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
                                avatar["created_at"]?.ToString() ?? "",
                                avatar["updated_at"]?.ToString() ?? "",
                                avatar["description"]?.ToString() ?? "",
                                avatar["tags"]?.ToObject<List<string>>() ?? new(),
                                hasPC, hasQuest, hasImpostor, pcPerf, questPerf);
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
                                created_at       = avatar["created_at"]?.ToString()          ?? "",
                                updated_at       = avatar["updated_at"]?.ToString()          ?? "",
                                description      = avatar["description"]?.ToString()         ?? "",
                                tags             = avatar["tags"]?.ToObject<List<string>>()  ?? new(),
                                hasPC,
                                hasQuest,
                                hasImpostor,
                                pcPerf,
                                questPerf,
                                rawJson = avatar,
                            }));
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
                            var url = ImageCacheHelper.GetWorldUrl(w["id"]?.ToString(), w["imageUrl"]?.ToString() ?? w["thumbnailImageUrl"]?.ToString());
                            w["imageUrl"] = url; w["thumbnailImageUrl"] = url;
                        }
                        Invoke(() => SendToJS("vrcMyWorlds", worlds));
                    });
                    break;

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
                        catch { }
                    });
                    break;

                // Groups - my groups, join, leave
                case "vrcGetFavoriteWorlds":
                    _ = Task.Run(async () =>
                    {
                        if (_settings.FfcEnabled)
                        {
                            var cachedFavWorlds = _cache.LoadRaw(CacheHandler.KeyFavWorlds);
                            if (cachedFavWorlds != null) Invoke(() => SendToJS("vrcFavoriteWorlds", cachedFavWorlds));
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
                        var ok = await _core.Favorites.UpdateFavoriteGroupAsync(groupType, groupName, displayName, visibility);
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
                        groupList = AuthController.FillMissingWorldSlots(groupList);
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
                        var ok = await _core.Favorites.RemoveFavoriteFriendAsync(fvrtId);
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
                        var (avOk, avResult) = await _core.Avatars.AddAvatarFavoriteAsync(avId, avGroup, avType, avOldFvrt);
                        if (avOk) _cache.Delete(CacheHandler.KeyFavAvatars);
                        Invoke(() => SendToJS("vrcAvatarFavoriteResult", new { ok = avOk, avatarId = avId, groupName = avGroup, newFvrtId = avOk ? avResult : "", error = avOk ? "" : avResult }));
                    });
                    break;

                case "vrcRemoveAvatarFavorite":
                {
                    var avRmId   = msg["avatarId"]?.ToString() ?? "";
                    var avFvrtId = msg["fvrtId"]?.ToString() ?? "";
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.Favorites.RemoveFavoriteFriendAsync(avFvrtId);
                        if (ok) _cache.Delete(CacheHandler.KeyFavAvatars);
                        Invoke(() => SendToJS("vrcAvatarUnfavoriteResult", new { ok, avatarId = avRmId }));
                    });
                    break;
                }

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
                    await _groups.HandleMessage(action, msg);
                    break;

                case "vrcGetTimeSpent":
                    await _instance.HandleMessage(action, msg);
                    break;

                case "vrcCreateGroupInstance":
                    await _groups.HandleMessage(action, msg);
                    break;

                // Custom Chatbox OSC
                case "chatboxConfig":
                case "chatboxStop":
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
                    _afCtrl.HandleMessage(action, msg);
                    break;

                // Avatar Scaling
                case "asConnect":
                case "asDisconnect":
                case "asSaveSettings":
                case "asSetScale":
                case "asRecordKey":
                    _asCtrl.HandleMessage(action, msg);
                    break;

                // OSC Tool
                case "oscConnect":
                case "oscDisconnect":
                case "oscSend":
                case "oscSendRaw":
                case "oscEnableOutputs":
                    _chatboxCtrl.HandleMessage(action, msg);
                    break;

                // VRCVideoCacher
                case "vcCheck":
                case "vcInstall":
                case "vcStart":
                case "vcStop":
                case "vcSend":
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
                case "vrcGetAllNotifications":
                case "vrcAcceptNotification":
                case "vrcMarkNotifRead":
                case "vrcHideNotification":
                case "vrcGetRespondMessages":
                case "vrcUpdateRespondMessage":
                case "vrcRespondToNotification":
                case "vrcRespondToNotificationWithPhoto":
                    await _notifications.HandleMessage(action, msg);
                    break;

                // App updates
                case "checkUpdate":
                case "installUpdate":
                    await _authCtrl.HandleMessage(action, msg);
                    break;

                case "vrcLaunchAndJoin":
                    _relayCtrl.HandleMessage(action, msg);
                    break;

                // Current instance
                case "vrcGetCurrentInstance":
                    await _instance.HandleMessage(action, msg);
                    break;

                // User detail (for non-friend profile viewing)
                case "vrcGetUser":
                    var guId = msg["userId"]?.ToString();
                    if (!string.IsNullOrEmpty(guId))
                    {
                        var guCached = _core.TimeEngine.GetUserDetail(guId);
                        if (guCached != null)
                            Invoke(() => SendToJS("vrcUserDetail", new {
                                id = guId, displayName = guCached.DisplayName, image = guCached.Image,
                                status = guCached.Status, statusDescription = guCached.StatusDescription,
                                bio = guCached.Bio, location = guCached.Location, isFriend = guCached.IsFriend,
                                currentAvatarImageUrl = ImageCacheHelper.GetAvatarUrl(null, guCached.CurrentAvatarImg),
                            }));
                        _ = Task.Run(async () => {
                            var u = await _core.Users.GetUserAsync(guId);
                            if (u != null)
                            {
                                var guImg = VRChatApiService.GetUserImage(u);
                                _core.TimeEngine.SaveUserDetail(
                                    u["id"]?.ToString() ?? guId,
                                    u["displayName"]?.ToString() ?? "",
                                    guImg,
                                    u["status"]?.ToString() ?? "offline",
                                    u["statusDescription"]?.ToString() ?? "",
                                    u["bio"]?.ToString() ?? "",
                                    u["location"]?.ToString() ?? "",
                                    u["isFriend"]?.Value<bool>() ?? false,
                                    u["currentAvatarImageUrl"]?.ToString() ?? "");
                                Invoke(() => SendToJS("vrcUserDetail", new {
                                    id = u["id"]?.ToString() ?? "", displayName = u["displayName"]?.ToString() ?? "",
                                    image = ImageCacheHelper.GetUserUrl(u["id"]?.ToString() ?? guId, guImg), status = u["status"]?.ToString() ?? "offline",
                                    statusDescription = u["statusDescription"]?.ToString() ?? "",
                                    bio = u["bio"]?.ToString() ?? "", location = u["location"]?.ToString() ?? "",
                                    isFriend = u["isFriend"]?.Value<bool>() ?? false,
                                    currentAvatarImageUrl = ImageCacheHelper.GetAvatarUrl(u["currentAvatar"]?.ToString(), u["currentAvatarImageUrl"]?.ToString()),
                                }));
                            }
                        });
                    }
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
                case "getTimelineMonthActivity":
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

                case "revealInExplorer":
                {
                    var filePath = msg["path"]?.ToString();
                    if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                        Process.Start("explorer.exe", $"/select,\"{filePath}\"");
                    break;
                }

                case "openShortcutFolder":
                {
                    var folder = msg["folder"]?.ToString();
                    string? dir = folder switch
                    {
                        "vrchat_data"  => Path.GetFullPath(Path.Combine(
                                              Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                              "..", "LocalLow", "VRChat", "VRChat")),
                        "vrchat_crash" => Path.Combine(
                                              Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                              "Temp", "VRChat", "VRChat", "Crashes"),
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
                        Invoke(() => SendToJS("vrcConfigData", new { config = cfgJson, cacheBytes }));
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
                case "dpRefresh":
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
                            _core.PerminiList[uid] = (
                                item["allowActive"]?.Value<bool>() ?? false,
                                item["allowAskMe"]?.Value<bool>()  ?? false,
                                item["allowDnD"]?.Value<bool>()    ?? false);
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
                                _core.PerminiList[uid] = (
                                    item["allowActive"]?.Value<bool>() ?? false,
                                    item["allowAskMe"]?.Value<bool>()  ?? false,
                                    item["allowDnD"]?.Value<bool>()    ?? false);
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
                case "vroToggle":
                case "vroConfig":
                case "vroAutoSave":
                case "vroToastConfig":
                case "vroWaterConfig":
                case "vroRecordKeybind":
                case "vroCancelRecording":
                case "vroSetTab":
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
                            var items = topics.Take(3).Select(t => new
                            {
                                title   = t["title"]?.ToString() ?? "",
                                link    = $"https://ask.vrchat.com/t/{t["slug"]}/{t["id"]}",
                                pubDate = t["created_at"]?.ToString() ?? "",
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

                // Local Favorites
                case "localFavGetGroups":
                {
                    var favType = msg["favType"]?.ToString() ?? "";
                    var groups = _core.TimeEngine.GetLocalFavGroups(favType);
                    Invoke(() => SendToJS("localFavGroups", new { favType, groups }));
                    break;
                }

                case "localFavCreateGroup":
                {
                    var name = msg["name"]?.ToString() ?? "";
                    var favType = msg["favType"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(favType))
                    {
                        var id = _core.TimeEngine.CreateLocalFavGroup(name, favType);
                        var groups = _core.TimeEngine.GetLocalFavGroups(favType);
                        Invoke(() => SendToJS("localFavGroups", new { favType, groups }));
                        Invoke(() => SendToJS("localFavGroupCreated", new { ok = id > 0, groupId = id, name, favType }));
                    }
                    break;
                }

                case "localFavDeleteGroup":
                {
                    var groupId = msg["groupId"]?.Value<int>() ?? 0;
                    var favType = msg["favType"]?.ToString() ?? "";
                    if (groupId > 0 && !string.IsNullOrEmpty(favType))
                    {
                        var ok = _core.TimeEngine.DeleteLocalFavGroup(groupId);
                        var groups = _core.TimeEngine.GetLocalFavGroups(favType);
                        Invoke(() => SendToJS("localFavGroups", new { favType, groups }));
                        Invoke(() => SendToJS("localFavGroupDeleted", new { ok, groupId }));
                    }
                    break;
                }

                case "localFavRenameGroup":
                {
                    var groupId = msg["groupId"]?.Value<int>() ?? 0;
                    var newName = msg["newName"]?.ToString() ?? "";
                    var favType = msg["favType"]?.ToString() ?? "";
                    if (groupId > 0 && !string.IsNullOrEmpty(newName) && !string.IsNullOrEmpty(favType))
                    {
                        var ok = _core.TimeEngine.RenameLocalFavGroup(groupId, newName);
                        var groups = _core.TimeEngine.GetLocalFavGroups(favType);
                        Invoke(() => SendToJS("localFavGroups", new { favType, groups }));
                        Invoke(() => SendToJS("localFavGroupRenamed", new { ok, groupId, newName }));
                    }
                    break;
                }

                case "localFavGetItems":
                {
                    var favType = msg["favType"]?.ToString() ?? "";
                    var groupFilter = msg["groupFilter"]?.ToString();
                    var entries = _core.TimeEngine.GetLocalFavEntries(favType, groupFilter);
                    Invoke(() => SendToJS("localFavItems", new { favType, groupFilter = groupFilter ?? "", entries }));
                    break;
                }

                case "localFavAddItem":
                {
                    var groupId = msg["groupId"]?.Value<int>() ?? 0;
                    var itemId = msg["itemId"]?.ToString() ?? "";
                    var itemType = msg["itemType"]?.ToString() ?? "";
                    if (groupId > 0 && !string.IsNullOrEmpty(itemId) && !string.IsNullOrEmpty(itemType))
                    {
                        var ok = _core.TimeEngine.AddLocalFavItem(groupId, itemId, itemType);
                        Invoke(() => SendToJS("localFavItemAdded", new { ok, groupId, itemId, itemType }));
                    }
                    break;
                }

                case "localFavRemoveItem":
                {
                    var groupId = msg["groupId"]?.Value<int>() ?? 0;
                    var itemId = msg["itemId"]?.ToString() ?? "";
                    var itemType = msg["itemType"]?.ToString() ?? "";
                    if (groupId > 0 && !string.IsNullOrEmpty(itemId) && !string.IsNullOrEmpty(itemType))
                    {
                        var ok = _core.TimeEngine.RemoveLocalFavItem(groupId, itemId, itemType);
                        Invoke(() => SendToJS("localFavItemRemoved", new { ok, groupId, itemId, itemType }));
                    }
                    break;
                }

                case "localFavGetItemGroups":
                {
                    var itemId = msg["itemId"]?.ToString() ?? "";
                    var itemType = msg["itemType"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(itemId) && !string.IsNullOrEmpty(itemType))
                    {
                        var groupIds = _core.TimeEngine.GetGroupsForItem(itemId, itemType);
                        Invoke(() => SendToJS("localFavItemGroups", new { itemId, itemType, groupIds }));
                    }
                    break;
                }

                case "localFavRemoveItemFromAll":
                {
                    var itemId = msg["itemId"]?.ToString() ?? "";
                    var itemType = msg["itemType"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(itemId) && !string.IsNullOrEmpty(itemType))
                    {
                        var ok = _core.TimeEngine.RemoveItemFromAllGroups(itemId, itemType);
                        Invoke(() => SendToJS("localFavItemRemovedFromAll", new { ok, itemId, itemType }));
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            SendToJS("log", new { msg = $"Error: {ex.Message}", color = "err" });
        }
    }

}
