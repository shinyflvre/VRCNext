using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NativeFileDialogSharp;
using VRCNext.Services;
using VRCNext.Services.Helpers;
using System.Diagnostics;
using System.Net;

namespace VRCNext;

// Owns all auth, login, settings, setup, cache-send, and startup orchestration.

public class AuthController
{
    private readonly CoreLibrary _core;
    private readonly FriendsController _friends;
    private readonly InstanceController _instance;
    private readonly PhotosController _photos;
    private readonly RelayController _relayCtrl;
    private readonly GroupsController _groups;
    private readonly DiscordController _discordCtrl;

    // Auth State
    private string _pending2faType = "totp";
    private bool _vrcDebugSetup;
    private string _lastAvatarName = "";
    private string _lastVideoUrl = "";
    private DateTime _lastVideoUrlTime = DateTime.MinValue;
    private DateTime _readyAt = DateTime.MaxValue;
    private CancellationTokenSource? _refreshCts;
    private volatile bool _sessionExpiryHandled;

    // In-flight guards — prevent duplicate startup fetches when JS triggers same requests
    private int _favWorldsInFlight = 0;
    private int _favAvatarsInFlight = 0;
    private List<JObject>? _cachedFavGroups;
    public void ClearFavGroupsCache() => _cachedFavGroups = null;

    private readonly VRCNext.Services.Tools.InstancePrintSaver _printSaver;
    private readonly VRCNext.Services.Tools.InstanceStickerSaver _stickerSaver;

    // Constructor

    public AuthController(
        CoreLibrary core,
        FriendsController friends,
        InstanceController instance,
        PhotosController photos,
        RelayController relayCtrl,
        GroupsController groups,
        DiscordController discordCtrl)
    {
        _core = core;
        _friends = friends;
        _instance = instance;
        _photos = photos;
        _relayCtrl = relayCtrl;
        _groups = groups;
        _discordCtrl = discordCtrl;
        _printSaver = new VRCNext.Services.Tools.InstancePrintSaver(core);
        _stickerSaver = new VRCNext.Services.Tools.InstanceStickerSaver(core);

        // Allow RelayController to trigger a session resume....
        _relayCtrl.OnWakeResumeRequested = VrcTryResumeAsync;

        _relayCtrl.OnAuthExpired = HandleWsAuthExpired;

        // CoreLibrary uses these callbacks to stop and restart account-scoped background tasks.
        _core.StopAccountScopedTasks = () =>
        {
            _refreshCts?.Cancel();
            _relayCtrl.StopWebSocket();
            return Task.CompletedTask;
        };
        _core.StartAccountScopedTasks = () =>
        {
            StartPeriodicRefresh();
            return Task.CompletedTask;
        };
    }

    // Invoke shim (Photino is thread-safe)
    private static void Invoke(Action action) => action();

    // Message Handler

    public async Task HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "vrcLogin":
                // Reject login while an account switch is in progress.
                if (_core.IsSwitchInProgress)
                {
                    _core.SendToJS("accountSwitchInProgress", new { rejectedAction = "vrcLogin" });
                    break;
                }
                var vrcUser = msg["username"]?.ToString() ?? "";
                var vrcPass = msg["password"]?.ToString() ?? "";
                await VrcLoginAsync(vrcUser, vrcPass);
                break;

            case "vrc2FA":
                if (_core.IsSwitchInProgress)
                {
                    _core.SendToJS("accountSwitchInProgress", new { rejectedAction = "vrc2FA" });
                    break;
                }
                var code2fa = msg["code"]?.ToString() ?? "";
                var type2fa = msg["type"]?.ToString() ?? "totp";
                await VrcVerify2FAAsync(code2fa, type2fa);
                break;

            case "vrcLogout":
                _refreshCts?.Cancel();
                _relayCtrl.StopWebSocket();
                await _core.Auth.LogoutAsync();
                await _core.AccountMutationLock.WaitAsync();
                try
                {
                    var acc = _core.Settings.ActiveAccount;
                    if (acc != null)
                    {
                        acc.AuthCookie = "";
                        acc.TwoFactorCookie = "";
                    }
                    _core.Settings.Save();
                }
                finally { _core.AccountMutationLock.Release(); }
                _core.SendToJS("vrcLoggedOut", null);
                _core.SendToJS("log", new { msg = "VRChat: Logged out", color = "sec" });
                break;

            // Multi-Account handlers.

            case "listAccounts":
                SendAccountsList();
                break;

            case "addAccount":
                if (_core.IsSwitchInProgress)
                {
                    _core.SendToJS("accountSwitchInProgress", new { rejectedAction = "addAccount" });
                    break;
                }
                {
                    var addUser = msg["username"]?.ToString() ?? "";
                    var addPass = msg["password"]?.ToString() ?? "";
                    await AddAccountAsync(addUser, addPass);
                }
                break;

            case "addAccount2FA":
                if (_core.IsSwitchInProgress)
                {
                    _core.SendToJS("accountSwitchInProgress", new { rejectedAction = "addAccount2FA" });
                    break;
                }
                {
                    var addCode = msg["code"]?.ToString() ?? "";
                    var addType = msg["type"]?.ToString() ?? "totp";
                    await AddAccountVerify2FAAsync(addCode, addType);
                }
                break;

            case "addAccountCancel":
                AddAccountCancel();
                break;

            case "switchAccount":
                {
                    var switchId = msg["accountId"]?.ToString() ?? "";
                    _ = SwitchAccountAsync(switchId);
                }
                break;

            case "removeAccount":
                if (_core.IsSwitchInProgress)
                {
                    _core.SendToJS("accountSwitchInProgress", new { rejectedAction = "removeAccount" });
                    break;
                }
                {
                    var removeId = msg["accountId"]?.ToString() ?? "";
                    await RemoveAccountAsync(removeId);
                }
                break;

            case "logoutAccount":
                if (_core.IsSwitchInProgress)
                {
                    _core.SendToJS("accountSwitchInProgress", new { rejectedAction = "logoutAccount" });
                    break;
                }
                {
                    var logoutId = msg["accountId"]?.ToString() ?? "";
                    await LogoutAccountAsync(logoutId);
                }
                break;

            case "saveSettings":
                var data = msg["data"];
                if (data != null) ApplySettings(data);
                break;

            case "saveVrcndbConsent":
                _core.Settings.VrcndbSubmitAvatars = msg["submit"]?.Value<bool>() ?? true;
                _core.Settings.VrcndbReportDeleted = msg["report"]?.Value<bool>() ?? true;
                _core.Settings.VrcndbSyncLikes     = msg["syncLikes"]?.Value<bool>() ?? _core.Settings.VrcndbSyncLikes;
                _core.Settings.CommentsOnWorldsEnabled = msg["comments"]?.Value<bool>() ?? _core.Settings.CommentsOnWorldsEnabled;
                _core.Settings.VrcndbConsentShown  = true;
                _core.Settings.Save();
                break;

            case "loadTranslation":
                SendTranslation(msg["language"]?.ToString());
                break;

            case "setupReady":
                _core.SendToJS("setPlatform", new { isLinux = !OperatingSystem.IsWindows() });
                var detectedPath = _core.Settings.VrcPath;
                if (string.IsNullOrWhiteSpace(detectedPath) || !File.Exists(detectedPath))
                    detectedPath = DetectVrcLaunchExe();
                if (!string.IsNullOrEmpty(detectedPath) && detectedPath != _core.Settings.VrcPath)
                {
                    _core.Settings.VrcPath = detectedPath;
                    _core.Settings.Save();
                }
                var photoDir = _core.Settings.WatchFolders.FirstOrDefault() ?? "";
                if (string.IsNullOrEmpty(photoDir))
                    photoDir = DetectVrcPhotoDir();
                _core.SendToJS("setupState", new
                {
                    vrcPath = detectedPath ?? "",
                    photoDir,
                    loggedIn = _core.VrcApi.IsLoggedIn,
                    displayName = _core.VrcApi.IsLoggedIn ? (_core.VrcApi.CurrentUserRaw?["displayName"]?.ToString() ?? "") : "",
                    platform = OperatingSystem.IsWindows() ? "windows" : "linux",
                    language = _core.Settings.Language ?? "en",
                    startWithSystem = _core.Settings.StartWithWindows,
                    prefs = new
                    {
                        enableProfileIconFrames        = _core.Settings.EnableProfileIconFrames,
                        enableVrcPlusDecorations       = _core.Settings.EnableVrcPlusDecorations,
                        friendsSidebarLocationOnly     = _core.Settings.FriendsSidebarLocationOnly,
                        friendsSidebarPreviewCollapsed = _core.Settings.FriendsSidebarPreviewCollapsed,
                    },
                });
                _ = VrcTryResumeAsync();
                break;

            case "setupDone":
                _core.Settings.SetupComplete = true;
                _core.Settings.Save();
                _core.LoadPage?.Invoke(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "frontend", "index.html"));
                break;

            case "resetSetup":
                _core.Settings.SetupComplete = false;
                _core.Settings.Save();
                var setupHtml = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "frontend", "setup", "setup.html");
                if (File.Exists(setupHtml)) _core.LoadPage?.Invoke(setupHtml);
                break;

            case "forceTrim":
                _core.MemTrim.TrimNow();
                break;

            case "getImgCacheSize":
                _ = Task.Run(() =>
                {
                    var bytes = ImageCacheHelper.GetCacheSizeBytes();
                    Invoke(() => _core.SendToJS("imgCacheSize", new { bytes }));
                });
                break;

            case "optimizeImgCache":
                _ = Task.Run(async () =>
                {
                    Invoke(() => _core.SendToJS("imgCacheOptimizeProgress", new { done = 0, total = -1 }));
                    await ImageCacheHelper.OptimizeAsync((done, total) =>
                        Invoke(() => _core.SendToJS("imgCacheOptimizeProgress", new { done, total })));
                    var bytes = ImageCacheHelper.GetCacheSizeBytes();
                    Invoke(() =>
                    {
                        _core.SendToJS("imgCacheOptimizeProgress", new { done = -1, total = 0 });
                        _core.SendToJS("imgCacheSize", new { bytes });
                    });
                });
                break;

            case "clearFfcCache":
                _ = Task.Run(() =>
                {
                    _core.Cache.ClearAll();
                    Invoke(() => _core.SendToJS("log", new { msg = "\ud83d\uddd1 FFC cache cleared.", color = "sec" }));
                });
                break;

            case "dbAnalyze":
                _ = Task.Run(() =>
                {
                    Invoke(() => _core.SendToJS("dbAnalyzeProgress", new { running = true }));
                    try
                    {
                        var result = SQLiteOptimizing.Analyze();
                        Invoke(() => _core.SendToJS("dbAnalyzeResult", new
                        {
                            totalRows          = result.TotalRows,
                            friendRows         = result.FriendRows,
                            cleanableRows      = result.CleanableRows,
                            counts             = result.Counts.Select(c => new { label = c.Label, count = c.Count }).ToArray(),
                            friendOnlineCount   = result.FriendOnlineCount,
                            friendOfflineCount  = result.FriendOfflineCount,
                            friendStatusCount   = result.FriendStatusCount,
                            friendStatusDescCount = result.FriendStatusDescCount,
                            friendBioCount        = result.FriendBioCount,
                            friendAvatarCount     = result.FriendAvatarCount,
                            notificationCount      = result.NotificationCount,
                            videoUrlCount          = result.VideoUrlCount,
                            avatarSwitchCount      = result.AvatarSwitchCount,
                            instancePlayersCount   = result.InstancePlayersCount,
                        }));
                    }
                    catch (Exception ex)
                    {
                        Invoke(() => _core.SendToJS("dbAnalyzeResult", new { error = ex.Message }));
                    }
                });
                break;

            case "dbMemoryUsage":
                _ = Task.Run(() =>
                {
                    try
                    {
                        var tables = SQLiteOptimizing.MemoryUsage();
                        var (fileBytes, freeBytes) = SQLiteOptimizing.DbFileStats();
                        Invoke(() => _core.SendToJS("dbMemoryResult", new
                        {
                            tables = tables.Select(t => new { table = t.Table, label = t.Label, bytes = t.Bytes, rows = t.Rows }).ToArray(),
                            liveBytes = tables.Sum(t => t.Bytes),
                            fileBytes,
                            freeBytes,
                        }));
                    }
                    catch (Exception ex)
                    {
                        Invoke(() => _core.SendToJS("dbMemoryResult", new { error = ex.Message }));
                    }
                });
                break;

            case "dbOptimize":
                _ = Task.Run(() =>
                {
                    Invoke(() => _core.SendToJS("dbOptimizeProgress", new { phase = "optimize" }));
                    try
                    {
                        var (userCleaned, feCleaned, notifCleaned, epCleaned) = SQLiteOptimizing.Optimize();
                        Invoke(() => _core.SendToJS("dbOptimizeProgress", new { phase = "vacuum" }));
                        SQLiteOptimizing.Vacuum();
                        Invoke(() => _core.SendToJS("dbOptimizeDone", new { userCleaned, feCleaned, notifCleaned, epCleaned }));
                    }
                    catch (Exception ex)
                    {
                        Invoke(() => _core.SendToJS("dbOptimizeDone", new { error = ex.Message }));
                    }
                });
                break;

            case "dbBackup":
                _ = Task.Run(() =>
                {
                    try
                    {
                        var path = SQLiteOptimizing.CreateBackup();
                        Invoke(() => _core.SendToJS("dbBackupDone", new { path }));
                    }
                    catch (Exception ex)
                    {
                        Invoke(() => _core.SendToJS("dbBackupDone", new { error = ex.Message }));
                    }
                });
                break;

            case "regBackup":
                _ = Task.Run(() =>
                {
                    try
                    {
                        var path = SQLiteOptimizing.CreateRegistryBackup();
                        Invoke(() => _core.SendToJS("regBackupDone", new { path }));
                    }
                    catch (Exception ex)
                    {
                        Invoke(() => _core.SendToJS("regBackupDone", new { error = ex.Message }));
                    }
                });
                break;

            case "forceFfcAll":
                _ = Task.Run(ForceFfcAllAsync);
                break;

            case "setupSaveLanguage":
                _core.Settings.Language = NormalizeLanguage(msg["language"]?.ToString());
                _core.Settings.Save();
                break;

            case "setupSaveStartWithWindows":
                _core.Settings.StartWithWindows = msg["enabled"]?.Value<bool>() ?? false;
                ApplyStartWithWindows(_core.Settings.StartWithWindows);
                _core.Settings.Save();
                break;

            case "setupSaveVrcPath":
                _core.Settings.VrcPath = msg["path"]?.ToString() ?? "";
                _core.Settings.Save();
                break;

            case "setupSavePhotoDir":
                var setupPhotoDir = msg["path"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(setupPhotoDir)
                    && Directory.Exists(setupPhotoDir)
                    && !_core.Settings.WatchFolders.Contains(setupPhotoDir, StringComparer.OrdinalIgnoreCase))
                {
                    _core.Settings.WatchFolders.Add(setupPhotoDir);
                    _core.Settings.Save();
                }
                break;

            case "setupSavePrefs":
                {
                    var prefs = msg["prefs"] as JObject;
                    if (prefs != null) ApplySetupPrefs(prefs);
                }
                break;

            case "setupBrowsePhotoDir":
                {
                    var defaultDir = VrcPathsHelper.PhotoDir();
                    if (!Directory.Exists(defaultDir))
                        defaultDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    var r = Dialog.FolderPicker(defaultDir);
                    if (r.IsOk) _core.SendToJS("setupPhotoDirResult", r.Path);
                }
                break;

            case "checkUpdate":
                _ = Task.Run(async () =>
                {
                    var version = await _core.UpdateService.CheckAsync();
                    if (version != null)
                        Invoke(() => _core.SendToJS("updateAvailable", new { version }));
                });
                break;

            case "installUpdate":
                _ = Task.Run(async () =>
                {
                    await _core.UpdateService.DownloadAsync(p =>
                        Invoke(() => _core.SendToJS("updateProgress", p)));
                    Invoke(() => _core.SendToJS("updateReady", null));
                    await Task.Delay(800);
                    Invoke(() => _core.UpdateService.ApplyAndRestart());
                });
                break;

            case "getChangelog":
                _ = Task.Run(async () =>
                {
                    var payload = await BuildChangelogPayloadAsync(auto: false);
                    Invoke(() => _core.SendToJS("showChangelog", payload));
                });
                break;

            case "restartApp":
                var exe = AppInfo.SelfExecutable;
                if (!string.IsNullOrEmpty(exe))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = $"--waitpid {Environment.ProcessId}",
                        UseShellExecute = OperatingSystem.IsWindows()
                    });
                    WindowController.AllowNextClose();
                    try { _core.Window?.Close(); } catch { Environment.Exit(0); }
                }
                break;

            case "openUrl":
                var openUrlTarget = msg["url"]?.ToString();
                if (!string.IsNullOrEmpty(openUrlTarget) &&
                    (openUrlTarget.StartsWith("https://") || openUrlTarget.StartsWith("http://")))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = openUrlTarget,
                        UseShellExecute = true
                    });
                }
                break;

            case "browseExe":
                {
                    var target = msg["target"]?.ToString() ?? "extra";
                    // VRChat path picker stays exe-only; startup-app pickers also accept
                    // shortcuts (.lnk) and launch scripts (.bat/.cmd).
#if WINDOWS
                    var r = target == "vrchat" ? Dialog.FileOpen("exe") : Dialog.FileOpen("exe,lnk,bat,cmd");
#else
                    var r = Dialog.FileOpen();
#endif
                    if (r.IsOk)
                    {
                        _core.SendToJS("exeAdded", new { target, path = r.Path });
                        if (target == "vrchat")
                        {
                            _core.Settings.VrcPath = r.Path;
                            _core.Settings.Save();
                        }
                    }
                }
                break;

            case "browseDashBg":
                {
                    var r = Dialog.FileOpen("png,jpg,jpeg,bmp,webp,gif,mp4");
                    if (r.IsOk)
                    {
                        try
                        {
                            if (r.Path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                            {
                                var info = new FileInfo(r.Path);
                                if (info.Length > 60L * 1024 * 1024)
                                {
                                    _core.SendToJS("toast", new { ok = false, msg = "Video must be under 60 MB" });
                                    break;
                                }
                            }
                            var url = _core.DashBgUrl(r.Path);
                            _core.SendToJS("dashBgSelected", new { path = r.Path, url, sample = DashBgSample(r.Path) });
                        }
                        catch (Exception ex)
                        {
                            _core.SendToJS("log", new { msg = $"Background image error: {ex.Message}", color = "err" });
                        }
                    }
                }
                break;

            case "vrcLoadDashBg":
                _ = Task.Run(() =>
                {
                    try
                    {
                        var bgPath = msg["path"]?.ToString();
                        if (!string.IsNullOrEmpty(bgPath) && File.Exists(bgPath))
                        {
                            var url = _core.DashBgUrl(bgPath);
                            var sample = DashBgSample(bgPath);
                            Invoke(() => _core.SendToJS("dashBgSelected", new { path = bgPath, url, sample }));
                        }
                    }
                    catch (Exception ex)
                    {
                        Invoke(() => _core.SendToJS("log", new { msg = $"Load background error: {ex.Message}", color = "err" }));
                    }
                });
                break;

            case "vrcRandomDashBg":
                _ = Task.Run(() =>
                {
                    try
                    {
                        var imgExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };
                        var allImages = new List<string>();

                        foreach (var folder in _core.Settings.WatchFolders.Where(Directory.Exists))
                        {
                            try
                            {
                                allImages.AddRange(
                                    Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                                        .Where(f => imgExts.Contains(Path.GetExtension(f)))
                                );
                            }
                            catch { }
                        }

                        if (allImages.Count == 0)
                        {
                            Invoke(() => _core.SendToJS("log", new { msg = "Random background: no images found in watch folders", color = "warn" }));
                            return;
                        }

                        var rng = new Random();
                        var picked = allImages[rng.Next(allImages.Count)];
                        var url = _core.DashBgUrl(picked);
                        var sample = DashBgSample(picked);
                        Invoke(() =>
                        {
                            _core.SendToJS("dashBgSelected", new { path = picked, url, sample });
                            _core.SendToJS("log", new { msg = $"Random background: {Path.GetFileName(picked)}", color = "ok" });
                        });
                    }
                    catch (Exception ex)
                    {
                        Invoke(() => _core.SendToJS("log", new { msg = $"Random background error: {ex.Message}", color = "err" }));
                    }
                });
                break;
        }
    }

    // Ready handler (called from MainForm "ready" case)

    public void HandleReady()
    {
        _readyAt = DateTime.UtcNow;
        SendTranslation(_core.Settings.Language);
        _core.SendToJS("loadSettings", _core.Settings);
        _core.SendToJS("dateTimeFormat", new
        {
            shortDatePattern = VRCNext.Services.Helpers.DateTimeHelper.ShortDatePattern,
            is24Hour = VRCNext.Services.Helpers.DateTimeHelper.Is24Hour,
        });
        _core.SendToJS("favoritesLoaded", _photos.Favorites);
#if WINDOWS
        _core.SendToJS("libraryRatings", _photos.Ratings);
#endif
        var customColors = _core.Cache.LoadRaw(CacheHandler.KeyCustomColors);
        if (customColors != null) _core.SendToJS("customColors", customColors);
#if WINDOWS
        if (_core.Settings.MinimizeToTray)
            _core.OnTraySettingChanged?.Invoke(true, true); // autoHide on startup
#endif
        _ = VrcTryResumeAsync();
        if (_core.Settings.LastChangelogVersion != AppInfo.Version)
        {
            _core.Settings.LastChangelogVersion = AppInfo.Version;
            _core.Settings.Save();
            _ = Task.Run(async () =>
            {
                await Task.Delay(4000);
                var payload = await BuildChangelogPayloadAsync(auto: true);
                Invoke(() => _core.SendToJS("showChangelog", payload));
            });
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            var version = await _core.UpdateService.CheckAsync();
            if (version != null)
                Invoke(() => _core.SendToJS("updateAvailable", new { version }));
        });
    }

    private const string ChangelogGroupId = "grp_36c812a1-0146-4eb8-ab73-4df11c7fc0e3";

    private async Task<object> BuildChangelogPayloadAsync(bool auto)
    {
        var notes = await FetchReleaseNotesAsync();
        JObject? g = null;
        try { g = await _core.Groups.GetGroupAsync(ChangelogGroupId); } catch { }
        return new
        {
            version = AppInfo.Version,
            notes,
            auto,
            groupName = g?["name"]?.ToString() ?? "VRCN",
            groupIcon = ImageCacheHelper.GetGroupUrl(ChangelogGroupId, g?["iconUrl"]?.ToString(), authoritative: g != null),
            groupBanner = ImageCacheHelper.GetGroupBannerUrl(ChangelogGroupId, g?["bannerUrl"]?.ToString(), authoritative: g != null),
            groupMembers = g?["memberCount"]?.Value<int>() ?? 0,
            groupJoined = g?["myMember"] != null && g["myMember"]!.Type != JTokenType.Null,
        };
    }

    private static string _cachedReleaseNotes = "";

    private static async Task<string> FetchReleaseNotesAsync()
    {
        if (!string.IsNullOrEmpty(_cachedReleaseNotes)) return _cachedReleaseNotes;
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestVersion = System.Net.HttpVersion.Version20;
            client.DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower;
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent);
            var resp = await client.GetAsync($"https://api.github.com/repos/shinyflvre/VRCNext/releases/tags/v{AppInfo.Version}");
            if (!resp.IsSuccessStatusCode)
                resp = await client.GetAsync("https://api.github.com/repos/shinyflvre/VRCNext/releases/latest");
            if (!resp.IsSuccessStatusCode) return "";
            var body = await resp.Content.ReadAsStringAsync();
            var notes = JObject.Parse(body)["body"]?.ToString() ?? "";
            if (!string.IsNullOrEmpty(notes)) _cachedReleaseNotes = notes;
            return notes;
        }
        catch { return ""; }
    }

    // VRC Debug Log Setup

    private static string DashBgSample(string path)
    {
        try
        {
            if (path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return "";
            using var codec = SkiaSharp.SKCodec.Create(new MemoryStream(File.ReadAllBytes(path)));
            if (codec == null) return "";
            using var src = SkiaSharp.SKBitmap.Decode(codec);
            if (src == null) return "";
            const int size = 80;
            using var dst = new SkiaSharp.SKBitmap(size, size, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
            src.ScalePixels(dst, SkiaSharp.SKFilterQuality.Medium);
            using var img  = SkiaSharp.SKImage.FromBitmap(dst);
            using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
            if (data == null) return "";
            return "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
        }
        catch { return ""; }
    }

    private void SetupVrcDebugLog()
    {
        if (_vrcDebugSetup) return;
        _vrcDebugSetup = true;
        _core.VrcApi.DebugLog += msg =>
        {
            try { _core.SendToJS("log", new { msg = $"[VRC] {msg}", color = "sec" }); } catch { }
        };
        _core.LogWatcher.DebugLog += msg =>
        {
            try { _core.SendToJS("log", new { msg = $"[LOG] {msg}", color = "sec" }); } catch { }
        };
        _core.World.OnCacheLog += msg =>
        {
            try { _core.SendToJS("log", new { msg = $"[CACH] {msg}", color = "sec" }); } catch { }
        };
        _core.LogWatcher.WorldChanged += (wId, loc) =>
        {
            try { _instance.HandleWorldChangedOnUiThread(wId, loc); } catch (Exception ex) { CrashHandler.WriteEntry("LogWatcher.WorldChanged", ex); }
        };
        _core.LogWatcher.PlayerJoined += (uid, name) =>
        {
            try { _instance.HandlePlayerJoinedOnUiThread(uid, name); } catch (Exception ex) { CrashHandler.WriteEntry("LogWatcher.PlayerJoined", ex); }
        };
        _core.LogWatcher.PlayerLeft += (uid, name) =>
        {
            try
            {
                // End the player's time session immediately on leave
                if (!string.IsNullOrEmpty(uid)) _core.TimeEngine.OnPlayerLeft(uid);
                if (!string.IsNullOrEmpty(uid)) _instance.HandlePlayerLeftOnUiThread(uid, name);
                _instance.PushCurrentInstanceFromCache();
            }
            catch (Exception ex) { CrashHandler.WriteEntry("LogWatcher.PlayerLeft", ex); }
        };
        _core.LogWatcher.InstanceClosed += loc =>
        {
            try
            {
                _instance.RecentlyClosedLocs.Add(loc);
                if (_core.Settings.MyInstances.Remove(loc))
                {
                    _core.Settings.Save();
                    _ = Task.Run(() => _core.DispatchMessage?.Invoke("""{"type":"vrcGetMyInstances"}"""));
                }
            }
            catch (Exception ex) { CrashHandler.WriteEntry("LogWatcher.InstanceClosed", ex); }
        };
        _core.LogWatcher.AvatarChanged += (displayName, avatarName) =>
        {
            try
            {
                var myName = _core.VrcApi.CurrentUserRaw?["displayName"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(myName) || displayName != myName) return;
                if (avatarName == _lastAvatarName) return;
                _lastAvatarName = avatarName;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(2000);
                        await _core.Auth.GetCurrentUserLocationAsync();
                        var avatarId = _core.VrcApi.CurrentAvatarId ?? "";
                        string avatarThumb = "";
                        JObject? av = null;
                        if (!string.IsNullOrEmpty(avatarId))
                        {
                            av = await _core.Avatars.GetAvatarAsync(avatarId);
                            avatarThumb = av?["thumbnailImageUrl"]?.ToString() ?? av?["imageUrl"]?.ToString() ?? "";
                        }
                        var ev = new TimelineService.TimelineEvent
                        {
                            Type      = "avatar_switch",
                            Timestamp = DateTime.UtcNow.ToString("o"),
                            UserId    = avatarId,
                            UserName  = avatarName,
                            UserImage = avatarThumb,
                        };
                        _core.Timeline.AddEvent(ev);
                        _core.SendToJS("timelineEvent", _instance.BuildTimelinePayload(ev));

                        if (!string.IsNullOrEmpty(avatarId))
                            _core.SendToJS("vrcAvatarSelected", new { avatarId });

                        // Submit public avatar to avtrdb if enabled
                        if (!string.IsNullOrEmpty(avatarId) && av?["releaseStatus"]?.ToString() == "public")
                            _core.AvtrdbSubmit?.Invoke(avatarId);
                        if (!string.IsNullOrEmpty(avatarId))
                            _core.VrcndbSubmit?.Invoke(avatarId);
                    }
                    catch (Exception ex) { CrashHandler.WriteEntry("LogWatcher.AvatarChanged.Async", ex); }
                });
            }
            catch (Exception ex) { CrashHandler.WriteEntry("LogWatcher.AvatarChanged", ex); }
        };
        _core.LogWatcher.AvatarSeen += id =>
        {
            try { _core.VrcndbSubmit?.Invoke(id); } catch (Exception ex) { CrashHandler.WriteEntry("LogWatcher.AvatarSeen", ex); }
        };
        _core.LogWatcher.PrintSeen += printId =>
        {
            try { _printSaver.OnPrintSeen(printId); } catch (Exception ex) { CrashHandler.WriteEntry("LogWatcher.PrintSeen", ex); }
        };
        _core.LogWatcher.StickerSeen += (userId, displayName, inventoryId) =>
        {
            try { _stickerSaver.OnStickerSeen(userId, displayName, inventoryId); } catch (Exception ex) { CrashHandler.WriteEntry("LogWatcher.StickerSeen", ex); }
        };
        _core.LogWatcher.VideoUrl += url =>
        {
            try
            {
                var now = DateTime.UtcNow;
                if (_lastVideoUrl == url && (now - _lastVideoUrlTime).TotalSeconds < 30) return;
                _lastVideoUrl     = url;
                _lastVideoUrlTime = now;

                var ev = new TimelineService.TimelineEvent
                {
                    Type      = "video_url",
                    Timestamp = now.ToString("o"),
                    WorldId   = _core.LogWatcher.CurrentWorldId ?? "",
                    WorldName = _instance.CachedInstWorldName,
                    Message   = url,
                };
                _core.Timeline.AddEvent(ev);
                _core.SendToJS("timelineEvent", _instance.BuildTimelinePayload(ev));
            }
            catch (Exception ex) { CrashHandler.WriteEntry("LogWatcher.VideoUrl", ex); }
        };
        _core.LogWatcher.PlayerModerated += (name, modType, active) =>
        {
            try
            {
                var uid = "";
                try
                {
                    var pl = _core.LogWatcher.GetCurrentPlayers().FirstOrDefault(p => p.DisplayName == name);
                    if (pl != null) uid = pl.UserId ?? "";
                }
                catch { }
                _friends.LogModeration(uid, name, "", modType, active);
            }
            catch { }
        };
        _core.LogWatcher.GameLogEntry += gle =>
        {
            try
            {
                _core.SendToJS("gameLogEvent", new
                {
                    type      = gle.Type,
                    timestamp = gle.Timestamp,
                    message   = gle.Message,
                    detail    = gle.Detail,
                });
            }
            catch { }
        };
    }

    public void HandleGetGameLog()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var entries = VRChatLogWatcher.BuildGameLogHistory(1000)
                    .Select(g => new { type = g.Type, timestamp = g.Timestamp, message = g.Message, detail = g.Detail })
                    .ToList();
                _core.SendToJS("gameLogHistory", new { entries });
            }
            catch { }
        });
    }

    // Session Resume

    public async Task VrcTryResumeAsync()
    {
        try { await VrcTryResumeInternalAsync(); }
        catch (Exception ex) { _core.SendToJS("log", new { msg = $"VRChat: Resume error — {ex.Message}", color = "warn" }); }
    }

    private async Task VrcTryResumeInternalAsync()
    {
        SetupVrcDebugLog();

        var active = _core.Settings.ActiveAccount;
        if (active != null && !string.IsNullOrEmpty(active.AuthCookie))
        {
            _core.SendToJS("log", new { msg = "VRChat: Resuming session...", color = "sec" });
            _core.VrcApi.RestoreCookies(active.AuthCookie, active.TwoFactorCookie);

            var result = await _core.Auth.TryResumeSessionAsync();
            if (result.Success && result.User != null)
            {
                SendVrcUserData(result.User, loginFlow: true);
                _core.SendToJS("log", new { msg = $"VRChat: Reconnected as {result.User["displayName"]}", color = "ok" });
                SendAllCachedData();
                await _friends.RefreshFriendsAsync();
                _relayCtrl.StartWebSocket();
                _ = TriggerStartupBackgroundRefreshAsync();
                StartPeriodicRefresh();
                return;
            }

            if (result.NetworkError)
            {
                // Network not ready (post-sleep, no internet) — keep cookies, retry later
                _core.SendToJS("log", new { msg = $"VRChat: Network unavailable ({result.Error}) — cookies preserved, will retry", color = "warn" });
                _relayCtrl.ScheduleResumeRetry();
                return;
            }

            // auth failure w 401 or 403 or 2FA required.. clear invalid cookies on the active account
            await _core.AccountMutationLock.WaitAsync();
            try
            {
                active.AuthCookie = "";
                active.TwoFactorCookie = "";
                _core.Settings.Save();
            }
            finally { _core.AccountMutationLock.Release(); }
            _core.SendToJS("log", new { msg = "VRChat: Session expired, please log in again", color = "warn" });
        }

        if (active != null && !string.IsNullOrEmpty(active.Username))
        {
            _core.SendToJS("vrcPrefillLogin", new
            {
                username = active.Username,
                password = active.Password
            });
        }
    }

    // Login writes credentials into the active account while holding the mutation lock for the full flow.
    private async Task VrcLoginAsync(string username, string password)
    {
        SetupVrcDebugLog();
        _core.SendToJS("log", new { msg = "VRChat: Logging in...", color = "sec" });
        _core.VrcApi.ResetSession();
        _sessionExpiryHandled = false;
        var result = await _core.Auth.LoginAsync(username, password);
        if (result.Requires2FA)
        {
            _pending2faType = result.TwoFactorType;
            _core.SendToJS("vrcNeeds2FA", new { type = result.TwoFactorType });
            _core.SendToJS("log", new { msg = $"VRChat: 2FA required ({result.TwoFactorType})", color = "warn" });
        }
        else if (result.Success && result.User != null)
        {
            await _core.AccountMutationLock.WaitAsync();
            try
            {
                var acc = _core.Settings.ActiveAccount ?? _core.Settings.EnsurePrimaryAccount();
                acc.Username = username;
                acc.Password = password;
                SaveVrcCookiesUnlocked();
                _core.Settings.Save();
                SendVrcUserDataUnlocked(result.User, loginFlow: true);
            }
            finally { _core.AccountMutationLock.Release(); }

            _core.SendToJS("log", new { msg = $"VRChat: Logged in as {result.User["displayName"]}", color = "ok" });
            await _friends.RefreshFriendsAsync();
            _relayCtrl.StartWebSocket();
            _ = TriggerStartupBackgroundRefreshAsync();
            StartPeriodicRefresh();
        }
        else
        {
            _core.SendToJS("vrcLoginError", new { error = result.Error ?? "Login failed" });
            _core.SendToJS("log", new { msg = $"VRChat: {result.Error}", color = "err" });
        }
    }

    // 2FA continues the login flow and persists cookies into the active account.
    private async Task VrcVerify2FAAsync(string code, string type)
    {
        var result = await _core.Auth.Verify2FAAsync(code, type);
        if (result.Success && result.User != null)
        {
            await _core.AccountMutationLock.WaitAsync();
            try
            {
                _ = _core.Settings.ActiveAccount ?? _core.Settings.EnsurePrimaryAccount();
                SaveVrcCookiesUnlocked();
                _core.Settings.Save();
                SendVrcUserDataUnlocked(result.User, loginFlow: true);
            }
            finally { _core.AccountMutationLock.Release(); }

            _core.SendToJS("log", new { msg = $"VRChat: Logged in as {result.User["displayName"]}", color = "ok" });
            await _friends.RefreshFriendsAsync();
            _relayCtrl.StartWebSocket();
            _ = TriggerStartupBackgroundRefreshAsync();
            StartPeriodicRefresh();
        }
        else
        {
            _core.SendToJS("vrcLoginError", new { error = result.Error ?? "2FA failed" });
            _core.SendToJS("log", new { msg = $"VRChat: 2FA error \u2014 {result.Error}", color = "err" });
        }
    }

    // Periodic auth/user refresh

    private void StartPeriodicRefresh()
    {
        _sessionExpiryHandled = false;
        _refreshCts?.Cancel();
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(10), ct);
                    if (!_core.VrcApi.IsLoggedIn) break;
                    var result = await _core.Auth.TryResumeSessionAsync();
                    if (result.Success && result.User != null)
                    {
                        Invoke(() => SendVrcUserData(result.User, loginFlow: false));
                    }
                    else if (!result.NetworkError)
                    {
                        Invoke(() => HandleSessionExpired("session refresh unauthorized"));
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private void HandleWsAuthExpired()
    {
        if (!_core.VrcApi.IsLoggedIn || _sessionExpiryHandled) return;
        _ = Task.Run(async () =>
        {
            var result = await _core.Auth.TryResumeSessionAsync();
            if (result.Success || result.NetworkError) return;
            Invoke(() => HandleSessionExpired("WebSocket authentication failed"));
        });
    }

    private void HandleSessionExpired(string reason)
    {
        if (_sessionExpiryHandled) return;
        _sessionExpiryHandled = true;

        _refreshCts?.Cancel();
        _relayCtrl.StopWebSocket();
        _core.VrcApi.ResetSession();
        _core.CurrentVrcUserId = "";

        string username = "", password = "";
        _core.AccountMutationLock.Wait();
        try
        {
            var acc = _core.Settings.ActiveAccount;
            if (acc != null)
            {
                acc.AuthCookie = "";
                acc.TwoFactorCookie = "";
                username = acc.Username ?? "";
                password = acc.Password ?? "";
            }
            _core.Settings.Save();
        }
        finally { _core.AccountMutationLock.Release(); }

        _core.SendToJS("log", new { msg = $"VRChat: Session expired ({reason}) — please log in again", color = "warn" });
        _core.SendToJS("vrcLoggedOut", (object?)null);
#if WINDOWS
        _core.OnTrayLoggedOut?.Invoke();
#endif
        if (!string.IsNullOrEmpty(username))
            _core.SendToJS("vrcPrefillLogin", new { username, password });
    }

    private void TryCleanMutualCaches(JObject user)
    {
        HashSet<string>? friendIds = null;
        if (user["friends"] is JArray fr)
            friendIds = new HashSet<string>(fr.Select(t => t?.ToString() ?? "").Where(s => s.Length > 0));

        HashSet<string>? groupIds = null;
        if ((user["presence"] as JObject)?["groups"] is JArray gr)
            groupIds = new HashSet<string>(gr.Select(t => t?.ToString() ?? "").Where(s => s.Length > 0));

        if (friendIds == null && groupIds == null) return;
        _ = Task.Run(() => { try { _core.TimeEngine.CleanMutualCaches(friendIds, groupIds); } catch { } });
    }

    // Writes current session cookies into the active account, caller must already hold the mutation lock.
    private void SaveVrcCookiesUnlocked()
    {
        var (auth, tfa) = _core.VrcApi.GetCookies();
        var acc = _core.Settings.ActiveAccount ?? _core.Settings.EnsurePrimaryAccount();
        acc.AuthCookie = auth ?? "";
        acc.TwoFactorCookie = tfa ?? "";
    }

    // Public wrapper that acquires the mutation lock itself.
    // Public wrapper for SendVrcUserData that acquires the mutation lock synchronously for callback sites.
    public void SendVrcUserData(JObject user, bool loginFlow = false)
    {
        _core.AccountMutationLock.Wait();
        try { SendVrcUserDataUnlocked(user, loginFlow); }
        finally { _core.AccountMutationLock.Release(); }
    }

    private string _selfLastStatus     = "";
    private string _selfLastStatusDesc = "";
    private string _selfLastBio        = "";
    private bool   _selfProfileSeeded;

    public void LogSelfProfileEvent(string subKind, string oldValue, string newValue)
    {
        try
        {
            var uid  = _core.CurrentVrcUserId ?? "";
            var raw  = _core.VrcApi.CurrentUserRaw;
            var name = raw?["displayName"]?.ToString() ?? _core.Settings.ActiveAccount?.DisplayName ?? "";
            var img  = raw != null ? VRChatApiService.GetUserImage(raw) : (_core.Settings.ActiveAccount?.AvatarImageUrl ?? "");
            var ev = new TimelineService.TimelineEvent
            {
                Type       = "profile",
                UserId     = uid,
                UserName   = name,
                UserImage  = img,
                NotifType  = subKind,
                NotifTitle = oldValue.Length > 500 ? oldValue[..500] : oldValue,
                Message    = newValue.Length > 500 ? newValue[..500] : newValue,
            };
            _core.Timeline.AddEvent(ev);
            _core.SendToJS("timelineEvent", _instance.BuildTimelinePayload(ev));
        }
        catch { }
    }

    private void DetectSelfProfileChanges(JObject user, bool loginFlow)
    {
        var newStatus = user["status"]?.ToString() ?? "";
        var newDesc   = (user["statusDescription"]?.ToString() ?? "").Trim();
        var newBio    = (user["bio"]?.ToString() ?? "").Trim();

        if (!_selfProfileSeeded || loginFlow)
        {
            _selfLastStatus     = newStatus;
            _selfLastStatusDesc = newDesc;
            _selfLastBio        = newBio;
            _selfProfileSeeded  = true;
            return;
        }

        if (!string.IsNullOrEmpty(newStatus) && newStatus != _selfLastStatus && !string.IsNullOrEmpty(_selfLastStatus))
            LogSelfProfileEvent("status", _selfLastStatus, newStatus);
        if (!string.IsNullOrEmpty(newStatus)) _selfLastStatus = newStatus;

        if (newDesc != _selfLastStatusDesc && !string.IsNullOrEmpty(_selfLastStatusDesc))
            LogSelfProfileEvent("statusdesc", _selfLastStatusDesc, newDesc);
        _selfLastStatusDesc = newDesc;

        if (!string.IsNullOrEmpty(newBio) && newBio != _selfLastBio && !string.IsNullOrEmpty(_selfLastBio))
            LogSelfProfileEvent("bio", _selfLastBio, newBio);
        if (!string.IsNullOrEmpty(newBio)) _selfLastBio = newBio;
    }

    public void SendVrcUserDataUnlocked(JObject user, bool loginFlow = false)
    {
        _core.CurrentVrcUserId = user["id"]?.ToString() ?? "";

        DetectSelfProfileChanges(user, loginFlow);

        TryCleanMutualCaches(user);

        // Backwrite UserId to the active account if empty and always refresh display name and avatar.
        var acc = _core.Settings.ActiveAccount;
        if (acc != null)
        {
            var uid = user["id"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(acc.UserId) && Database.IsValidVrcUserId(uid))
            {
                acc.UserId = uid;
            }
            var dn = user["displayName"]?.ToString();
            if (!string.IsNullOrEmpty(dn)) acc.DisplayName = dn;
            // Use the user icon or pic ovverride
            var profileImg = VRChatApiService.GetUserImage(user);
            if (!string.IsNullOrEmpty(profileImg)) acc.AvatarImageUrl = profileImg;
            // Backfill username if it was never captured before.
            if (string.IsNullOrEmpty(acc.Username))
            {
                var un = user["username"]?.ToString();
                if (!string.IsNullOrEmpty(un)) acc.Username = un;
            }
        }

        if (loginFlow)
        {
            _core.LogWatcher.Start();
            _photos.StartVrcPhotoWatcher();
            _ = Task.Run(_friends.FetchAndCacheFavFriendsAsync);
        }

        if (!_instance.LogWatcherBootstrapped)
        {
            _instance.LogWatcherBootstrapped = true;
            var vrcRunning = _core.IsVrcRunning?.Invoke() ?? false;
            if (vrcRunning && !string.IsNullOrEmpty(_core.LogWatcher.CurrentWorldId) && _instance.PendingInstanceEventId == null)
            {
                var loc = _core.LogWatcher.CurrentLocation ?? _core.LogWatcher.CurrentWorldId;
                var lastJoin = _core.Timeline.GetEvents().FirstOrDefault(e => e.Type == "instance_join");
                var lastJoinFinalised = lastJoin != null && !string.IsNullOrEmpty(lastJoin.LeftAt);
                if (lastJoin != null && lastJoin.Location == loc && !lastJoinFinalised)
                {
                    _instance.PendingInstanceEventId = lastJoin.Id;
                    if (lastJoin.Players != null)
                    {
                        foreach (var p in lastJoin.Players)
                        {
                            if (!string.IsNullOrEmpty(p.UserId))
                            {
                                _instance.CumulativeInstancePlayers[p.UserId] = (p.DisplayName, p.Image ?? "");
                                if (p.JoinedAts != null && p.JoinedAts.Count > 0)
                                    _instance.PlayerJoinTimes[p.UserId] = new List<string>(p.JoinedAts);
                                if (p.LeftAts != null && p.LeftAts.Count > 0)
                                    _instance.PlayerLeftTimes[p.UserId] = new List<string>(p.LeftAts);
                                if (p.UserId != (_core.CurrentVrcUserId ?? ""))
                                    _instance.MeetAgainThisInstance.Add(p.UserId);
                            }
                        }
                    }
                    ReconcilePlayerSessionsFromLog(lastJoin);
                    _core.TimeEngine.OnWorldResumed(_core.LogWatcher.CurrentWorldId, loc);
                    _instance.LastTrackedWorldId = _core.LogWatcher.CurrentWorldId;
                }
                else if (lastJoin != null && lastJoin.Location == loc && lastJoinFinalised)
                {

                }
                else
                {
                    // Close any open tracked instance_join event from a previous session
                    if (lastJoin != null && lastJoin.Tracked == 1 && string.IsNullOrEmpty(lastJoin.LeftAt))
                    {
                        var nowStr = DateTime.UtcNow.ToString("o");
                        _core.Timeline.UpdateEvent(lastJoin.Id, ev =>
                        {
                            if (ev.Players == null) return;
                            foreach (var p in ev.Players.Where(p => p.LeftAts.Count < p.JoinedAts.Count))
                                p.LeftAts.Add(nowStr);
                        });
                        _core.Timeline.SetInstanceEventLeftAt(lastJoin.Id, nowStr);
                    }
                    _instance.HandleWorldChangedOnUiThread(_core.LogWatcher.CurrentWorldId, loc);
                }
                var currentPlayers = _core.LogWatcher.GetCurrentPlayers();
                foreach (var p in currentPlayers)
                {
                    if (string.IsNullOrEmpty(p.UserId)) continue;
                    if (!string.IsNullOrEmpty(_core.CurrentVrcUserId) && p.UserId == _core.CurrentVrcUserId) continue;
                    if (!_instance.CumulativeInstancePlayers.ContainsKey(p.UserId))
                        _instance.CumulativeInstancePlayers[p.UserId] = (p.DisplayName, "");
                    if (!_instance.PlayerJoinTimes.ContainsKey(p.UserId))
                        _instance.PlayerJoinTimes[p.UserId] = new List<string> { p.JoinedAt.ToUniversalTime().ToString("o") };
                    if (!string.IsNullOrEmpty(p.DisplayName))
                        _core.TimeEngine.UpdateUserInfo(p.UserId, p.DisplayName, "");
                    // Register catch-up players in the engine with their real log timestamp.
                    // Without this, players already present when VRCNext starts would have
                    // no active session → Time Together shows 0 instead of the real duration.
                    _core.TimeEngine.OnPlayerJoined(p.UserId, p.JoinedAt.ToUniversalTime());
                }
                // Restore active session from DB — adds gap time for players still present
                var currentPlayerIds = new HashSet<string>(
                    currentPlayers.Where(p => !string.IsNullOrEmpty(p.UserId)).Select(p => p.UserId));
                currentPlayerIds.Remove(_core.CurrentVrcUserId ?? "");
                _core.TimeEngine.RestoreActiveSession(loc, currentPlayerIds);
            }

            var resumedId = _instance.PendingInstanceEventId;
            foreach (var openEv in _core.Timeline.GetOpenInstanceEvents())
            {
                if (openEv.Id == resumedId) continue;
                var nowStr = DateTime.UtcNow.ToString("o");
                _core.Timeline.UpdateEvent(openEv.Id, ev =>
                {
                    if (ev.Players == null) return;
                    foreach (var p in ev.Players.Where(p => p.LeftAts.Count < p.JoinedAts.Count))
                        p.LeftAts.Add(nowStr);
                });
                _core.Timeline.SetInstanceEventLeftAt(openEv.Id, nowStr);
                var closed = _core.Timeline.GetEvents().FirstOrDefault(e => e.Id == openEv.Id);
                if (closed != null) _core.SendToJS("timelineEvent", _instance.BuildTimelinePayload(closed));
            }
        }

        var rawStatus = user["status"]?.ToString() ?? "";
        if (!string.IsNullOrEmpty(rawStatus)) _core.MyVrcStatus = rawStatus;
        _discordCtrl.PushPresence();

        var userImage = VRChatApiService.GetUserImage(user);
        var userDisplayName = user["displayName"]?.ToString() ?? "";
        var userStatus = user["status"]?.ToString() ?? "offline";
        var userStatusDesc = user["statusDescription"]?.ToString() ?? "";

        _core.SendToJS("vrcUser", new
        {
            id = user["id"]?.ToString() ?? "",
            displayName = userDisplayName,
            image = userImage,
            iconFrame = user["iconFrame"]?.ToString() ?? "",
            iconFrameUrl = IconFrameHelper.UrlFor(user["iconFrame"]?.ToString(), _core.Inventory),
            nameplateEffect = user["nameplateEffect"]?.ToString() ?? "",
            nameplateUrl = IconFrameHelper.UrlFor(user["nameplateEffect"]?.ToString(), _core.Inventory),
            profileEffect = user["profileEffect"]?.ToString() ?? "",
            profileEffectUrl = IconFrameHelper.UrlFor(user["profileEffect"]?.ToString(), _core.Inventory),
            status = userStatus,
            statusDescription = userStatusDesc,
            statusHistory = user["statusHistory"]?.ToObject<List<string>>() ?? new List<string>(),
            currentAvatar = user["currentAvatar"]?.ToString() ?? "",
            bio = user["bio"]?.ToString() ?? "",
            pronouns = user["pronouns"]?.ToString() ?? "",
            bioLinks = user["bioLinks"]?.ToObject<List<string>>() ?? new List<string>(),
            tags = user["tags"]?.ToObject<List<string>>() ?? new List<string>(),
            bannerUrl             = ImageCacheHelper.GetUserBannerUrl(user["id"]?.ToString(), user["bannerUrl"]?.ToString()),
            bannerType            = user["bannerType"]?.ToString() ?? "",
            profilePicOverride    = ImageCacheHelper.GetUserPicOverrideUrl(user["id"]?.ToString(), user["profilePicOverride"]?.ToString()),
            currentAvatarImageUrl = ImageCacheHelper.GetAvatarUrl(user["currentAvatar"]?.ToString(), user["currentAvatarImageUrl"]?.ToString()),
            dateJoined        = user["date_joined"]?.ToString() ?? "",
            lastLogin         = user["last_login"]?.ToString() ?? "",
            lastPlatform      = user["last_platform"]?.ToString() ?? "",
            platform          = user["platform"]?.ToString() ?? "",
            ageVerified       = user["ageVerified"]?.Value<bool>() ?? false,
            isEconomyCreator  = user["isEconomyCreator"]?.Value<bool>() ?? false,
            ageVerificationStatus = user["ageVerificationStatus"]?.ToString() ?? "",
            vrcRunning        = _core.IsVrcRunning?.Invoke() ?? false,
            allowAvatarCopying = user["allowAvatarCopying"]?.Value<bool>() ?? false,
            isBoopingEnabled  = user["isBoopingEnabled"]?.Value<bool>() ?? false,
            homeLocation      = user["homeLocation"]?.ToString() ?? "",
            rawJson = user,
        });

#if WINDOWS
        _core.OnTrayUserUpdate?.Invoke(userDisplayName, userStatus, userStatusDesc, userImage);
#endif

        // Fetch own badges (auth endpoint may not include badges, so fetch full user profile)
        var userId = user["id"]?.ToString() ?? "";
        if (!string.IsNullOrEmpty(userId))
        {
            _ = Task.Run(async () =>
            {
                JArray? badgesArr = user["badges"] as JArray;
                if (badgesArr == null || badgesArr.Count == 0)
                {
                    var fullUser = await _core.Users.GetUserAsync(userId);
                    badgesArr = fullUser?["badges"] as JArray ?? new JArray();
                }
                var badges = new List<object>();
                foreach (var b in badgesArr)
                {
                    if (b is not JObject bObj) continue;
                    var imageUrl = bObj["badgeImageUrl"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(imageUrl)) continue;
                    badges.Add(new
                    {
                        id = bObj["badgeId"]?.ToString() ?? "",
                        name = bObj["badgeName"]?.ToString() ?? "",
                        description = bObj["badgeDescription"]?.ToString() ?? "",
                        imageUrl, showcased = bObj["showcased"]?.Value<bool>() ?? false,
                    });
                }
                Invoke(() => _core.SendToJS("vrcMyBadges", new { badges }));
            });
        }

        if (loginFlow)
        {
            _ = Task.Run(async () =>
            {
                var balance = await _core.Economy.GetBalanceAsync();
                if (balance >= 0)
                    Invoke(() => _core.SendToJS("vrcCredits", new { balance }));
            });
        }

        // The profile background sits on its own endpoint and this method is sync, so it
        // is fetched afterwards and patched onto the already-sent user payload.
        var selfId = user["id"]?.ToString();
        if (!string.IsNullOrEmpty(selfId))
        {
            _ = Task.Run(async () =>
            {
                var appearance = await _core.Users.GetProfileAppearanceAsync(selfId, asSelf: true);
                if (appearance == null) return;
                var texId = appearance["backgroundTextureId"]?.ToString() ?? "";
                Invoke(() => _core.SendToJS("vrcSelfAppearance", new
                {
                    themeId                  = appearance["themeId"]?.ToString() ?? "",
                    themes                   = appearance["themes"] as JArray ?? new JArray(),
                    backgroundType           = appearance["backgroundType"]?.ToString() ?? "",
                    backgroundTextureId      = texId,
                    backgroundTextureUrl     = ProfileBackgroundHelper.UrlFor(texId),
                    backgroundGradientTop    = appearance["backgroundGradientTop"]?.ToString() ?? "",
                    backgroundGradientBottom = appearance["backgroundGradientBottom"]?.ToString() ?? "",
                }));
            });
        }
    }

    private void ReconcilePlayerSessionsFromLog(TimelineService.TimelineEvent lastJoin)
    {
        if (lastJoin == null) return;
        const long ToleranceMs = 5000;

        long ToMs(string iso)
        {
            return DateTimeOffset.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var o)
                ? o.ToUnixTimeMilliseconds() : 0L;
        }
        bool HasNearby(List<long> existing, long target)
        {
            foreach (var e in existing) if (Math.Abs(e - target) <= ToleranceMs) return true;
            return false;
        }

        var selfId  = _core.CurrentVrcUserId ?? "";
        var logEvents = _core.LogWatcher.GetPlayerEventLog();
        var perUserName = new Dictionary<string, string>();
        var groupedLog  = new Dictionary<string, List<(string type, DateTime ts)>>();
        foreach (var ev in logEvents)
        {
            if (string.IsNullOrEmpty(ev.UserId) || ev.UserId == selfId) continue;
            perUserName[ev.UserId] = ev.DisplayName;
            if (!groupedLog.TryGetValue(ev.UserId, out var list))
                groupedLog[ev.UserId] = list = new List<(string, DateTime)>();
            list.Add((ev.Type, ev.Timestamp));
        }

        var changed = false;
        // Phase 1: merge log events into DB arrays for every user
        foreach (var (uid, events) in groupedLog)
        {
            if (!_instance.PlayerJoinTimes.TryGetValue(uid, out var dbJoins))
                _instance.PlayerJoinTimes[uid] = dbJoins = new List<string>();
            if (!_instance.PlayerLeftTimes.TryGetValue(uid, out var dbLefts))
                _instance.PlayerLeftTimes[uid] = dbLefts = new List<string>();

            var joinMs = dbJoins.Select(ToMs).ToList();
            var leftMs = dbLefts.Select(ToMs).ToList();

            foreach (var (type, ts) in events.OrderBy(e => e.ts))
            {
                var utc = ts.ToUniversalTime();
                var ms  = new DateTimeOffset(utc).ToUnixTimeMilliseconds();
                var iso = utc.ToString("o");
                if (type == "join")
                {
                    if (HasNearby(joinMs, ms)) continue;
                    dbJoins.Add(iso); joinMs.Add(ms); changed = true;
                }
                else
                {
                    if (HasNearby(leftMs, ms) || dbLefts.Count >= dbJoins.Count) continue;
                    dbLefts.Add(iso); leftMs.Add(ms); changed = true;
                }
            }

            dbJoins.Sort(StringComparer.Ordinal);
            dbLefts.Sort(StringComparer.Ordinal);

            if (!_instance.CumulativeInstancePlayers.ContainsKey(uid))
            {
                var img = _friends.TryGetNameImage(uid, out var fi) ? fi.image : "";
                _instance.CumulativeInstancePlayers[uid] = (perUserName[uid], img);
            }
            _instance.MeetAgainThisInstance.Add(uid);
        }

        foreach (var uid in _instance.PlayerJoinTimes.Keys.ToList())
        {
            if (uid == selfId) continue;
            if (!_instance.PlayerLeftTimes.TryGetValue(uid, out var storedLefts) || storedLefts.Count < 2) continue;
            var deduped = TimelineService.PlayerSnap.DedupeLefts(_instance.PlayerJoinTimes[uid], storedLefts);
            if (deduped.Count == storedLefts.Count) continue;
            _instance.PlayerLeftTimes[uid] = deduped;
            changed = true;
        }

        var currentlyPresent = new HashSet<string>(
            _core.LogWatcher.GetCurrentPlayers()
                .Where(p => !string.IsNullOrEmpty(p.UserId))
                .Select(p => p.UserId));
        var logHasEvidence = logEvents.Count > 0;
        foreach (var uid in _instance.PlayerJoinTimes.Keys.ToList())
        {
            if (uid == selfId) continue;
            if (logHasEvidence && currentlyPresent.Contains(uid)) continue;
            var joins = _instance.PlayerJoinTimes[uid];
            if (!_instance.PlayerLeftTimes.TryGetValue(uid, out var lefts))
                _instance.PlayerLeftTimes[uid] = lefts = new List<string>();
            if (lefts.Count >= joins.Count) continue;
            if (!logHasEvidence)
            {
                while (lefts.Count < joins.Count) lefts.Add(joins[joins.Count - 1]);
            }
            else
            {
                var logLeft = _core.LogWatcher.GetLastLeftTime(uid);
                var fallback = logLeft?.ToUniversalTime().ToString("o") ?? joins[joins.Count - 1];
                while (lefts.Count < joins.Count) lefts.Add(fallback);
            }
            changed = true;
        }

        if (!changed) return;

        _core.Timeline.UpdateEvent(lastJoin.Id, ev =>
        {
            ev.Players ??= new List<TimelineService.PlayerSnap>();
            foreach (var p in ev.Players)
            {
                if (_instance.PlayerJoinTimes.TryGetValue(p.UserId, out var nj))
                    p.JoinedAts = new List<string>(nj);
                if (_instance.PlayerLeftTimes.TryGetValue(p.UserId, out var nl))
                    p.LeftAts = new List<string>(nl);
            }
            foreach (var (uid, _) in groupedLog)
            {
                if (ev.Players.Any(p => p.UserId == uid)) continue;
                var img = _friends.TryGetNameImage(uid, out var fi) ? fi.image : "";
                ev.Players.Add(new TimelineService.PlayerSnap
                {
                    UserId      = uid,
                    DisplayName = perUserName[uid],
                    Image       = img,
                    JoinedAts   = new List<string>(_instance.PlayerJoinTimes[uid]),
                    LeftAts     = new List<string>(_instance.PlayerLeftTimes.TryGetValue(uid, out var l) ? l : new List<string>()),
                });
            }
        });
        var refreshed = _core.Timeline.GetEvents().FirstOrDefault(e => e.Id == lastJoin.Id);
        if (refreshed != null) _core.SendToJS("timelineEvent", _instance.BuildTimelinePayload(refreshed));
    }

    // Multi-Account state, list, add, switch, remove and logout flows.

    public enum SwitchOutcome { Success, RequiresLogin, Error }

    private class PendingAddAccount
    {
        public HttpClient Http = null!;
        public CookieContainer Cookies = null!;
        public string Username = "";
        public string Password = "";
    }
    private PendingAddAccount? _pendingAddAccount;

    // Sends a sanitized account list to the frontend without any passwords or cookies.
    public void SendAccountsList()
    {
        var payload = new
        {
            activeAccountId = _core.Settings.ActiveAccountId,
            accounts = _core.Settings.Accounts.Select(a => new
            {
                accountId      = a.AccountId,
                userId         = a.UserId,
                displayName    = a.DisplayName,
                username       = a.Username,
                avatarImageUrl = a.AvatarImageUrl,
                isPrimary      = a.IsPrimary,
                isActive       = a.AccountId == _core.Settings.ActiveAccountId,
                hasSession     = !string.IsNullOrEmpty(a.AuthCookie),
                profileIndex   = a.IsPrimary ? 0 : a.ProfileIndex,
            }).ToList()
        };
        _core.SendToJS("accountsList", payload);
    }

    // Cleans up the in-flight add-account session on success, cancel or any terminal error.
    private void DisposePendingAdd()
    {
        try { _pendingAddAccount?.Http?.Dispose(); } catch { }
        _pendingAddAccount = null;
    }

    // Adds an account through an isolated session, locking only around the final save block.
    private async Task AddAccountAsync(string username, string password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            _core.SendToJS("accountAddError", new { error = "Username and password required" });
            return;
        }
        // Discard any previous in-flight add-account session.
        if (_pendingAddAccount != null) DisposePendingAdd();

        // Build an isolated HttpClient that has no effect on _core.VrcApi.
        var cookies = new CookieContainer();
        var handler = new HttpClientHandler { CookieContainer = cookies, UseCookies = true };
        var http = new HttpClient(handler);
        http.DefaultRequestVersion = System.Net.HttpVersion.Version20;
        http.DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower;
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent);
        _pendingAddAccount = new PendingAddAccount { Http = http, Cookies = cookies, Username = username, Password = password };

        var result = await _core.Auth.LoginIsolatedAsync(username, password, http, cookies);

        if (result.Requires2FA)
        {
            _core.SendToJS("accountAddNeeds2FA", new { twoFactorType = result.TwoFactorType });
            return; // HttpClient stays open until AddAccountVerify2FAAsync runs.
        }

        if (result.Success && result.User != null)
        {
            await FinalizeAddAccountAsync(result.User);
            return;
        }

        _core.SendToJS("accountAddError", new { error = result.Error ?? "Login failed" });
        DisposePendingAdd();
    }

    private async Task AddAccountVerify2FAAsync(string code, string type)
    {
        if (_pendingAddAccount == null)
        {
            _core.SendToJS("accountAddError", new { error = "No pending add-account flow" });
            return;
        }
        var pending = _pendingAddAccount;
        var result = await _core.Auth.Verify2FAIsolatedAsync(code, type, pending.Http, pending.Cookies);
        if (result.Success && result.User != null)
        {
            await FinalizeAddAccountAsync(result.User);
            return;
        }
        // Keep the HttpClient open on a wrong 2FA code so the user can retry.
        _core.SendToJS("accountAddError", new { error = result.Error ?? "2FA failed" });
    }

    private void AddAccountCancel()
    {
        DisposePendingAdd();
        _core.SendToJS("accountAddError", new { error = "Cancelled", cancelled = true });
    }

    private async Task FinalizeAddAccountAsync(JObject user)
    {
        var pending = _pendingAddAccount!;
        var newUserId = user["id"]?.ToString() ?? "";
        if (!Database.IsValidVrcUserId(newUserId))
        {
            _core.SendToJS("accountAddError", new { error = "Invalid VRChat user ID returned by API" });
            DisposePendingAdd();
            return;
        }

        await _core.AccountMutationLock.WaitAsync();
        string newAccountId;
        try
        {
            if (_core.Settings.Accounts.Any(a => a.UserId == newUserId))
            {
                _core.SendToJS("accountAddError", new { error = "Account already added" });
                DisposePendingAdd();
                return;
            }

            string? auth = null, twoFA = null;
            foreach (Cookie c in pending.Cookies.GetCookies(new Uri("https://api.vrchat.cloud")))
            {
                if (c.Name == "auth") auth = c.Value;
                if (c.Name == "twoFactorAuth") twoFA = c.Value;
            }

            var newAcc = new VrcAccount
            {
                AccountId      = Guid.NewGuid().ToString("N"),
                UserId         = newUserId,
                Username       = string.IsNullOrEmpty(pending.Username)
                                    ? (user["username"]?.ToString() ?? "")
                                    : pending.Username,
                DisplayName    = user["displayName"]?.ToString() ?? pending.Username,
                AvatarImageUrl = VRChatApiService.GetUserImage(user),
                IsPrimary      = false,
                Password       = pending.Password,
                AuthCookie     = auth ?? "",
                TwoFactorCookie= twoFA ?? "",
            };
            newAccountId = newAcc.AccountId;
            _core.Settings.Accounts.Add(newAcc);
            _core.Settings.EnsureProfileIndex(newAcc);
            _core.Settings.Save();
        }
        finally { _core.AccountMutationLock.Release(); }

        DisposePendingAdd();
        _core.SendToJS("accountAddSuccess", new { accountId = newAccountId });
        SendAccountsList();
    }

    // Switch — Lock für die gesamte Transaktion (HR4).
    public async Task<SwitchOutcome> SwitchAccountAsync(string accountId)
    {
        if (!_core.TryBeginAccountSwitch())
        {
            _core.SendToJS("accountSwitchError", new { reason = "switch_in_progress" });
            return SwitchOutcome.Error;
        }
        await _core.AccountMutationLock.WaitAsync();
        try { return await SwitchAccountInternalAsync(accountId); }
        finally
        {
            _core.AccountMutationLock.Release();
            _core.EndAccountSwitch();
        }
    }

    // Restart-based switch that persists state and relaunches the process so resume and DB-load happen on next start.
    private async Task<SwitchOutcome> SwitchAccountInternalAsync(string accountId)
    {
        var target = _core.Settings.Accounts.FirstOrDefault(a => a.AccountId == accountId);
        if (target == null)
        {
            _core.SendToJS("accountSwitchError", new { reason = "not_found" });
            return SwitchOutcome.Error;
        }
        if (_core.Settings.ActiveAccountId == accountId)
        {
            // Already active so no restart is needed.
            return SwitchOutcome.Success;
        }
        if (!target.IsPrimary && !Database.IsValidVrcUserId(target.UserId))
        {
            _core.SendToJS("accountSwitchError", new { reason = "invalid_user_id" });
            return SwitchOutcome.Error;
        }

        // Tell the frontend to show the restart overlay.
        _core.SendToJS("accountSwitchStarting", new
        {
            targetAccountId = accountId,
            displayName     = target.DisplayName,
        });

        // Save current cookies so the prior account can resume without re-login when switched back.
        var (curAuth, curTwoFA) = _core.VrcApi.GetCookies();
        var cur = _core.Settings.ActiveAccount;
        if (cur != null)
        {
            cur.AuthCookie = curAuth ?? "";
            cur.TwoFactorCookie = curTwoFA ?? "";
        }

        // Switch the active account and persist before restarting.
        _core.Settings.ActiveAccountId = target.AccountId;
        _core.Settings.Save();

        // Brief delay so the frontend can render the restart overlay before exit.
        await Task.Delay(400);

        // Launch a new instance and terminate the current one.
        try
        {
            var exe = AppInfo.SelfExecutable;
            if (string.IsNullOrEmpty(exe))
                exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (!string.IsNullOrEmpty(exe))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"--waitpid {Environment.ProcessId}",
                    UseShellExecute = OperatingSystem.IsWindows()
                });
            }
        }
        catch { }

        try { _core.Window?.Close(); } catch { }
        Environment.Exit(0);
        return SwitchOutcome.Success; // unreachable
    }

    // Remove blocks the primary account and switches an active secondary to primary first.
    private async Task RemoveAccountAsync(string accountId)
    {
        var target = _core.Settings.Accounts.FirstOrDefault(a => a.AccountId == accountId);
        if (target == null)
        {
            _core.SendToJS("accountSwitchError", new { reason = "not_found", action = "remove" });
            return;
        }
        if (target.IsPrimary)
        {
            _core.SendToJS("accountSwitchError", new { reason = "primary_immutable", action = "remove" });
            return;
        }

        // Switch to the primary first without holding the mutation lock since SwitchAccountAsync acquires it.
        if (target.AccountId == _core.Settings.ActiveAccountId)
        {
            var primary = _core.Settings.PrimaryAccount;
            if (primary == null)
            {
                _core.SendToJS("accountSwitchError", new { reason = "no_primary", action = "remove" });
                return;
            }
            var outcome = await SwitchAccountAsync(primary.AccountId);
            if (outcome != SwitchOutcome.Success)
            {
                _core.SendToJS("accountSwitchError", new { reason = "pre_remove_switch_failed", action = "remove" });
                return;
            }
        }

        await _core.AccountMutationLock.WaitAsync();
        try
        {
            _core.Settings.Accounts.RemoveAll(a => a.AccountId == accountId);
            _core.Settings.Save();
        }
        finally { _core.AccountMutationLock.Release(); }

        // The {userId}_VRCNData.db file is intentionally left on disk.
        SendAccountsList();
    }

    // Logout clears the account session and cookies without changing the active DB.
    private async Task LogoutAccountAsync(string accountId)
    {
        await _core.AccountMutationLock.WaitAsync();
        try
        {
            var target = _core.Settings.Accounts.FirstOrDefault(a => a.AccountId == accountId);
            if (target == null)
            {
                _core.SendToJS("accountSwitchError", new { reason = "not_found", action = "logout" });
                return;
            }
            if (target.AccountId == _core.Settings.ActiveAccountId)
            {
                // Active account is being logged out so tear down the current session in place.
                _refreshCts?.Cancel();
                _relayCtrl.StopWebSocket();
                await _core.StopAccountScopedTasksAsync();
                _core.VrcApi.ResetSession();
                _core.CurrentVrcUserId = "";
            }
            target.AuthCookie = "";
            target.TwoFactorCookie = "";
            _core.Settings.Save();
        }
        finally { _core.AccountMutationLock.Release(); }

        SendAccountsList();
        _core.SendToJS("vrcLoggedOut", null);
    }

    // Settings

    private void ApplySetupPrefs(JObject prefs)
    {
        void Flag(string key, Action<bool> set)
        {
            var v = prefs[key];
            if (v != null && v.Type != JTokenType.Null) set(v.Value<bool>());
        }
        Flag("enableVrcPlusDecorations",       v => _core.Settings.EnableVrcPlusDecorations = v);
        Flag("enableProfileIconFrames",        v => _core.Settings.EnableProfileIconFrames = v);
        Flag("squareIconFrames",               v => _core.Settings.SquareIconFrames = v);
        Flag("enableNameplateDecoration",      v => _core.Settings.EnableNameplateDecoration = v);
        Flag("enableProfileEffects",           v => _core.Settings.EnableProfileEffects = v);
        Flag("enableProfileBackgrounds",       v => _core.Settings.EnableProfileBackgrounds = v);
        Flag("enableProfileThemes",            v => _core.Settings.EnableProfileThemes = v);
        Flag("profileThemeContrast",           v => _core.Settings.ProfileThemeContrast = v);
        Flag("transparentProfileCards",        v => _core.Settings.TransparentProfileCards = v);
        Flag("showDecorationsOnDashboard",     v => _core.Settings.ShowDecorationsOnDashboard = v);
        Flag("enableProfileIconFramesOthers", v => _core.Settings.EnableProfileIconFramesOthers = v);
        Flag("squareIconFramesOthers", v => _core.Settings.SquareIconFramesOthers = v);
        Flag("enableNameplateDecorationOthers", v => _core.Settings.EnableNameplateDecorationOthers = v);
        Flag("enableProfileEffectsOthers", v => _core.Settings.EnableProfileEffectsOthers = v);
        Flag("enableProfileBackgroundsOthers", v => _core.Settings.EnableProfileBackgroundsOthers = v);
        Flag("enableProfileThemesOthers", v => _core.Settings.EnableProfileThemesOthers = v);
        Flag("profileThemeContrastOthers", v => _core.Settings.ProfileThemeContrastOthers = v);
        Flag("transparentProfileCardsOthers", v => _core.Settings.TransparentProfileCardsOthers = v);
        Flag("showDecorationsOnDashboardOthers", v => _core.Settings.ShowDecorationsOnDashboardOthers = v);
        Flag("friendsSidebarLocationOnly",     v => _core.Settings.FriendsSidebarLocationOnly = v);
        Flag("friendsSidebarPreviewCollapsed", v => _core.Settings.FriendsSidebarPreviewCollapsed = v);

        _core.Settings.Save();
    }

    private void ApplySettings(JToken data)
    {
        try
        {
            _core.Settings.BotName = data["botName"]?.ToString() ?? "VRCNext";
            _core.Settings.BotAvatarUrl = data["botAvatar"]?.ToString() ?? "";
            _core.Settings.VrcPath = data["vrcPath"]?.ToString() ?? "";
            _core.Settings.AutoStart = data["autoStart"]?.Value<bool>() ?? false;
            _core.Settings.StartWithWindows = data["startWithWindows"]?.Value<bool>() ?? false;
            ApplyStartWithWindows(_core.Settings.StartWithWindows);
            _core.Settings.PostAll = data["postAll"]?.Value<bool>() ?? false;
            _core.Settings.Notifications = data["notifications"]?.Value<bool>() ?? true;
            _core.Settings.NotifySound = data["notifySound"]?.Value<bool>() ?? false; // legacy
            _core.Settings.NotifySoundEnabled = data["notifySoundEnabled"]?.Value<bool>() ?? false;
            _core.Settings.MessageSoundEnabled = data["messageSoundEnabled"]?.Value<bool>() ?? false;
            _core.Settings.MediaRelaySoundEnabled = data["mediaRelaySoundEnabled"]?.Value<bool>() ?? false;
            _core.Settings.SteamOverlaySoundEnabled = data["steamOverlaySoundEnabled"]?.Value<bool>() ?? true;
            _core.Settings.NotifySoundFile = data["notifySoundFile"]?.ToString() ?? "";
            _core.Settings.MessageSoundFile = data["messageSoundFile"]?.ToString() ?? "";
            _core.Settings.MediaRelaySoundFile = data["mediaRelaySoundFile"]?.ToString() ?? "";
            _core.Settings.SteamOverlaySoundFile = data["steamOverlaySoundFile"]?.ToString() ?? "";
            _core.Settings.NotifySoundVolume = data["notifySoundVolume"]?.Value<int>() ?? 50;
            _core.Settings.MessageSoundVolume = data["messageSoundVolume"]?.Value<int>() ?? 50;
            _core.Settings.MediaRelaySoundVolume = data["mediaRelaySoundVolume"]?.Value<int>() ?? 50;
            _core.Settings.SteamOverlaySoundVolume = data["steamOverlaySoundVolume"]?.Value<int>() ?? 50;
            _core.Settings.FriendOnlineToastEnabled = data["friendOnlineToastEnabled"]?.Value<bool>() ?? false;
            _core.Settings.FriendOnlineToastFavOnly = data["friendOnlineToastFavOnly"]?.Value<bool>() ?? false;
            _core.Settings.FriendsSidebarLocationOnly = data["friendsSidebarLocationOnly"]?.Value<bool>() ?? true;
            _core.Settings.FriendsSidebarPreviewCollapsed = data["friendsSidebarPreviewCollapsed"]?.Value<bool>() ?? true;
            _core.Settings.FriendsSidebarPreviewOpen = data["friendsSidebarPreviewOpen"]?.Value<bool>() ?? false;
            _core.Settings.SeparateFavoriteFriends = data["separateFavoriteFriends"]?.Value<bool>() ?? false;
            _core.Settings.PeopleAlwaysStats = data["peopleAlwaysStats"]?.Value<bool>() ?? false;
            _core.Settings.CommentsOnWorldsEnabled = data["commentsOnWorldsEnabled"]?.Value<bool>() ?? true;
            _core.Settings.ModernFolderLayout = data["modernFolderLayout"]?.Value<bool>() ?? true;
            _core.Settings.NavSidebarHoverText = data["navSidebarHoverText"]?.Value<bool>() ?? true;
            _core.Settings.EnableVrcPlusDecorations = data["enableVrcPlusDecorations"]?.Value<bool>() ?? false;
            _core.Settings.EnableProfileIconFrames = data["enableProfileIconFrames"]?.Value<bool>() ?? false;
            _core.Settings.SquareIconFrames = data["squareIconFrames"]?.Value<bool>() ?? false;
            _core.Settings.EnableNameplateDecoration = data["enableNameplateDecoration"]?.Value<bool>() ?? false;
            _core.Settings.EnableProfileEffects = data["enableProfileEffects"]?.Value<bool>() ?? false;
            _core.Settings.EnableProfileBackgrounds = data["enableProfileBackgrounds"]?.Value<bool>() ?? false;
            _core.Settings.EnableProfileThemes = data["enableProfileThemes"]?.Value<bool>() ?? false;
            _core.Settings.ProfileThemeContrast = data["profileThemeContrast"]?.Value<bool>() ?? true;
            _core.Settings.TransparentProfileCards = data["transparentProfileCards"]?.Value<bool>() ?? false;
            _core.Settings.ShowDecorationsOnDashboard = data["showDecorationsOnDashboard"]?.Value<bool>() ?? false;
            _core.Settings.EnableProfileIconFramesOthers = data["enableProfileIconFramesOthers"]?.Value<bool>() ?? _core.Settings.EnableProfileIconFramesOthers;
            _core.Settings.SquareIconFramesOthers = data["squareIconFramesOthers"]?.Value<bool>() ?? _core.Settings.SquareIconFramesOthers;
            _core.Settings.EnableNameplateDecorationOthers = data["enableNameplateDecorationOthers"]?.Value<bool>() ?? _core.Settings.EnableNameplateDecorationOthers;
            _core.Settings.EnableProfileEffectsOthers = data["enableProfileEffectsOthers"]?.Value<bool>() ?? _core.Settings.EnableProfileEffectsOthers;
            _core.Settings.EnableProfileBackgroundsOthers = data["enableProfileBackgroundsOthers"]?.Value<bool>() ?? _core.Settings.EnableProfileBackgroundsOthers;
            _core.Settings.EnableProfileThemesOthers = data["enableProfileThemesOthers"]?.Value<bool>() ?? _core.Settings.EnableProfileThemesOthers;
            _core.Settings.ProfileThemeContrastOthers = data["profileThemeContrastOthers"]?.Value<bool>() ?? _core.Settings.ProfileThemeContrastOthers;
            _core.Settings.TransparentProfileCardsOthers = data["transparentProfileCardsOthers"]?.Value<bool>() ?? _core.Settings.TransparentProfileCardsOthers;
            _core.Settings.ShowDecorationsOnDashboardOthers = data["showDecorationsOnDashboardOthers"]?.Value<bool>() ?? _core.Settings.ShowDecorationsOnDashboardOthers;
            _core.Settings.MinimizeToTray = data["minimizeToTray"]?.Value<bool>() ?? false;
            _core.Settings.TrayNotificationsEnabled = data["trayNotificationsEnabled"]?.Value<bool>() ?? false;
#if WINDOWS
            _core.OnTraySettingChanged?.Invoke(_core.Settings.MinimizeToTray, false);
#endif
            _core.Settings.Language = NormalizeLanguage(data["language"]?.ToString());
            _core.Settings.Theme = data["theme"]?.ToString() ?? "vrcn";
            _core.Settings.SpecialTheme = data["specialTheme"]?.ToString() ?? "";
#if WINDOWS
            // Theme colors are always pushed from JS via overlayThemeColors
            // JS THEMES and would overwrite the correct colors that JS just sent.
#endif
            _core.Settings.AutoColorAccuracy = data["autoColorAccuracy"]?.Value<int>() ?? 50;
            _core.Settings.PlayBtnTheme = data["playBtnTheme"]?.ToString() ?? "";
            _core.Settings.CursorTheme = data["cursorTheme"]?.ToString() ?? "";
            _core.Settings.AppFont = data["appFont"]?.ToString() ?? "google-sans";
            _core.Settings.CustomFont = data["customFont"]?.ToString() ?? "";
            _core.Settings.VroToastTtsOnline = data["vroToastTtsOnline"]?.Value<bool>() ?? false;
            _core.Settings.VroToastTtsOffline = data["vroToastTtsOffline"]?.Value<bool>() ?? false;
            _core.Settings.VroToastTtsGps = data["vroToastTtsGps"]?.Value<bool>() ?? false;
            _core.Settings.VroToastTtsStatus = data["vroToastTtsStatus"]?.Value<bool>() ?? false;
            _core.Settings.VroToastTtsStatusDesc = data["vroToastTtsStatusDesc"]?.Value<bool>() ?? false;
            _core.Settings.VroToastTtsBio = data["vroToastTtsBio"]?.Value<bool>() ?? false;
            _core.Settings.VroToastTtsFriendReq = data["vroToastTtsFriendReq"]?.Value<bool>() ?? false;
            _core.Settings.VroToastTtsInvite = data["vroToastTtsInvite"]?.Value<bool>() ?? false;
            _core.Settings.VroToastTtsGroupInv = data["vroToastTtsGroupInv"]?.Value<bool>() ?? false;
            _core.Settings.VroToastTtsJoined = data["vroToastTtsJoined"]?.Value<bool>() ?? false;
            _core.Settings.VroToastTtsLeft = data["vroToastTtsLeft"]?.Value<bool>() ?? false;
            _core.Settings.VroToastTtsReqInvite = data["vroToastTtsReqInvite"]?.Value<bool>() ?? false;
            _core.Settings.VroToastJoined = data["vroToastJoined"]?.Value<bool>() ?? true;
            _core.Settings.VroToastLeft = data["vroToastLeft"]?.Value<bool>() ?? true;
            _core.Settings.VroToastReqInvite = data["vroToastReqInvite"]?.Value<bool>() ?? true;
            if (VRCNext.Services.Helpers.AudioDeviceManager.TryReadSelectionFromMessage(data["vroTtsDeviceId"], data["vroTtsDeviceName"]?.ToString(), false, _core.Settings.VroTtsDeviceName, out var vroTtsId, out var vroTtsName))
            {
                _core.Settings.VroTtsDeviceId = vroTtsId;
                _core.Settings.VroTtsDeviceName = vroTtsName;
            }
            _core.Settings.VroTtsVoice  = data["vroTtsVoice"]?.ToString() ?? "";
            _core.Settings.VroTtsEngine = data["vroTtsEngine"]?.ToString() ?? "sapi";
            _core.Settings.VroTtsLang   = data["vroTtsLang"]?.ToString() ?? "";
            _core.Settings.VroTtsGender = data["vroTtsGender"]?.ToString() ?? "";
            _core.Settings.FontSizeOffset = Math.Clamp(data["fontSizeOffset"]?.Value<int>() ?? 0, -5, 5);
            _core.Settings.TaskbarHeight = Math.Clamp(data["taskbarHeight"]?.Value<int>() ?? 42, 36, 48);
            var activeCustomThemes = data["activeCustomThemes"]?.ToObject<List<string>>();
            if (activeCustomThemes != null) _core.Settings.ActiveCustomThemes = activeCustomThemes;
            _core.Settings.GuiZoom = Math.Clamp(data["guiZoom"]?.Value<int>() ?? 100, 50, 200);

            var dashBg = data["dashBgPath"]?.ToString();
            if (dashBg != null) _core.Settings.DashBgPath = dashBg;
            _core.Settings.DashOpacity = data["dashOpacity"]?.Value<int>() ?? 40;
            _core.Settings.RandomDashBg = data["randomDashBg"]?.Value<bool>() ?? false;
            _core.Settings.ClockEnabled  = data["clockEnabled"]?.Value<bool>()  ?? false;
            _core.Settings.DateEnabled   = data["dateEnabled"]?.Value<bool>()   ?? false;
            _core.Settings.ShowVrcPlus    = data["showVrcPlus"]?.Value<bool>()    ?? true;
            _core.Settings.ShowVrcCredits = data["showVrcCredits"]?.Value<bool>() ?? true;
            _core.Settings.ShowApiHealth  = data["showApiHealth"]?.Value<bool>()  ?? true;

            // Webhooks: explicit parsing to handle any casing
            if (data["webhooks"] is JArray whArr && whArr.Count > 0)
            {
                _core.Settings.Webhooks.Clear();
                for (int i = 0; i < Math.Min(whArr.Count, 4); i++)
                {
                    var item = whArr[i];
                    _core.Settings.Webhooks.Add(new AppSettings.WebhookSlot {
                        Name = (item["Name"] ?? item["name"])?.ToString() ?? "",
                        Url = (item["Url"] ?? item["url"])?.ToString() ?? "",
                        Enabled = (item["Enabled"] ?? item["enabled"])?.Value<bool>() ?? false,
                    });
                }
                while (_core.Settings.Webhooks.Count < 4)
                    _core.Settings.Webhooks.Add(new AppSettings.WebhookSlot { Name = $"Channel {_core.Settings.Webhooks.Count + 1}" });
            }

            var folders = data["folders"]?.ToObject<List<string>>();
            if (folders != null) _core.Settings.WatchFolders = folders;

            var relayEnabledFoldersToken = data["relayEnabledFolders"];
            if (relayEnabledFoldersToken != null)
                _core.Settings.RelayEnabledFolders = relayEnabledFoldersToken.ToObject<List<string>>();

            var extraExe = data["extraExe"]?.ToObject<List<string>>();
            if (extraExe != null) _core.Settings.ExtraExe = extraExe;

            var extraExeDesktop = data["extraExeDesktop"]?.ToObject<List<string>>();
            if (extraExeDesktop != null)
            {
                bool desktopChanged = !_core.Settings.ExtraExeDesktop.SequenceEqual(extraExeDesktop);
                _core.Settings.ExtraExeDesktop = extraExeDesktop;
                if (desktopChanged) AutoStartShortcutHelper.Sync(extraExeDesktop, vr: false);
            }

            var extraExeVR = data["extraExeVR"]?.ToObject<List<string>>();
            if (extraExeVR != null)
            {
                bool vrChanged = !_core.Settings.ExtraExeVR.SequenceEqual(extraExeVR);
                _core.Settings.ExtraExeVR = extraExeVR;
                if (vrChanged) AutoStartShortcutHelper.Sync(extraExeVR, vr: true);
            }

            _core.Settings.CloseWithVrc = data["closeWithVrc"]?.Value<bool>() ?? false;
            _core.Settings.StartAlwaysWithVrc = data["startAlwaysWithVrc"]?.Value<bool>() ?? true;

            // Credentials are no longer accepted through saveSettings, login runs through the Accounts tab.

            // Space Flight settings
            _core.Settings.SfMultiplier = data["sfMultiplier"]?.Value<float>() ?? 1f;
            _core.Settings.SfLockX = data["sfLockX"]?.Value<bool>() ?? false;
            _core.Settings.SfLockY = data["sfLockY"]?.Value<bool>() ?? false;
            _core.Settings.SfLockZ = data["sfLockZ"]?.Value<bool>() ?? false;
            _core.Settings.SfLeftHand = data["sfLeftHand"]?.Value<bool>() ?? false;
            _core.Settings.SfRightHand = data["sfRightHand"]?.Value<bool>() ?? true;
            _core.Settings.SfUseGrip = data["sfUseGrip"]?.Value<bool>() ?? true;
            int vrModeIn = data["vrInputMode"]?.Value<int>() ?? _core.Settings.VrInputMode;
            if (vrModeIn != _core.Settings.VrInputMode)
            {
                _core.Settings.VrInputMode = vrModeIn;
#if WINDOWS
                _core.VrOverlay?.ApplyInputMode(vrModeIn);
#endif
            }
            bool vrIdx = _core.Settings.VrInputMode == 1;

            uint SfIn(string key, uint current)
            {
                var v = data[key]?.Value<int>();
                return v.HasValue ? (uint)v.Value : current;
            }

            if (vrIdx)
            {
                _core.Settings.SfIdxLeftResetButton    = SfIn("sfLeftResetBtn",     _core.Settings.SfIdxLeftResetButton);
                _core.Settings.SfIdxRightResetButton   = SfIn("sfRightResetBtn",    _core.Settings.SfIdxRightResetButton);
                _core.Settings.SfIdxLeftDragButton     = SfIn("sfLeftDragBtn",      _core.Settings.SfIdxLeftDragButton);
                _core.Settings.SfIdxRightDragButton    = SfIn("sfRightDragBtn",     _core.Settings.SfIdxRightDragButton);
                _core.Settings.SfIdxLeftGravityButton  = SfIn("sfLeftGravityBtn",   _core.Settings.SfIdxLeftGravityButton);
                _core.Settings.SfIdxRightGravityButton = SfIn("sfRightGravityBtn",  _core.Settings.SfIdxRightGravityButton);
            }
            else
            {
                _core.Settings.SfLeftResetButton    = SfIn("sfLeftResetBtn",    _core.Settings.SfLeftResetButton);
                _core.Settings.SfRightResetButton   = SfIn("sfRightResetBtn",   _core.Settings.SfRightResetButton);
                _core.Settings.SfLeftDragButton     = SfIn("sfLeftDragBtn",     _core.Settings.SfLeftDragButton);
                _core.Settings.SfRightDragButton    = SfIn("sfRightDragBtn",    _core.Settings.SfRightDragButton);
                _core.Settings.SfLeftGravityButton  = SfIn("sfLeftGravityBtn",  _core.Settings.SfLeftGravityButton);
                _core.Settings.SfRightGravityButton = SfIn("sfRightGravityBtn", _core.Settings.SfRightGravityButton);
            }
            _core.Settings.SfGravity = data["sfGravity"]?.Value<float>() ?? 9.8f;

            // Space Turn settings
            _core.Settings.StMultiplier  = data["stMultiplier"]?.Value<float>()  ?? _core.Settings.StMultiplier;
            _core.Settings.StSnapDegrees = data["stSnapDegrees"]?.Value<float>() ?? _core.Settings.StSnapDegrees;
            _core.Settings.StInvert      = data["stInvert"]?.Value<bool>()       ?? _core.Settings.StInvert;
            _core.Settings.StSmoothing   = data["stSmoothing"]?.Value<float>()   ?? _core.Settings.StSmoothing;
            _core.Settings.StAutoStartVR = data["stAutoStartVR"]?.Value<bool>()  ?? _core.Settings.StAutoStartVR;
            if (vrIdx)
            {
                _core.Settings.StIdxLeftTurnButton   = SfIn("stLeftTurnBtn",   _core.Settings.StIdxLeftTurnButton);
                _core.Settings.StIdxRightTurnButton  = SfIn("stRightTurnBtn",  _core.Settings.StIdxRightTurnButton);
                _core.Settings.StIdxLeftResetButton  = SfIn("stLeftResetBtn",  _core.Settings.StIdxLeftResetButton);
                _core.Settings.StIdxRightResetButton = SfIn("stRightResetBtn", _core.Settings.StIdxRightResetButton);
            }
            else
            {
                _core.Settings.StLeftTurnButton   = SfIn("stLeftTurnBtn",   _core.Settings.StLeftTurnButton);
                _core.Settings.StRightTurnButton  = SfIn("stRightTurnBtn",  _core.Settings.StRightTurnButton);
                _core.Settings.StLeftResetButton  = SfIn("stLeftResetBtn",  _core.Settings.StLeftResetButton);
                _core.Settings.StRightResetButton = SfIn("stRightResetBtn", _core.Settings.StRightResetButton);
            }
            _core.Settings.ChatboxAutoStart = data["chatboxAutoStart"]?.Value<bool>() ?? false;
            _core.Settings.SfAutoStart = data["sfAutoStart"]?.Value<bool>() ?? false;
            _core.Settings.DiscordPresenceAutoStart = data["discordPresenceAutoStart"]?.Value<bool>() ?? false;
            // VR / Desktop split auto-starts
            _core.Settings.ChatboxAutoStartVR      = data["chatboxAutoStartVR"]?.Value<bool>()      ?? false;
            _core.Settings.ChatboxAutoStartDesktop = data["chatboxAutoStartDesktop"]?.Value<bool>() ?? false;
            _core.Settings.SfAutoStartVR           = data["sfAutoStartVR"]?.Value<bool>()           ?? false;
            _core.Settings.FsAutoStartVR           = data["fsAutoStartVR"]?.Value<bool>()           ?? false;
            if (vrIdx)
            {
                _core.Settings.FsIdxLeftButton  = SfIn("fsLeftButton",  _core.Settings.FsIdxLeftButton);
                _core.Settings.FsIdxRightButton = SfIn("fsRightButton", _core.Settings.FsIdxRightButton);
            }
            else
            {
                _core.Settings.FsLeftButton  = SfIn("fsLeftButton",  _core.Settings.FsLeftButton);
                _core.Settings.FsRightButton = SfIn("fsRightButton", _core.Settings.FsRightButton);
            }
            if (VRCNext.Services.Helpers.AudioDeviceManager.TryReadSelectionFromMessage(data["fsOutputDeviceId"], data["fsOutputDeviceName"]?.ToString(), false, _core.Settings.FsOutputDevice, out var fsOutId, out var fsOutName))
            {
                _core.Settings.FsOutputDeviceId = fsOutId;
                _core.Settings.FsOutputDevice = fsOutName;
            }
            _core.Settings.FsActivationRadius      = data["fsActivationRadius"]?.Value<int>()       ?? 15;
            if (vrIdx)
            {
                _core.Settings.FsIdxLeftRecordButton  = SfIn("fsLeftRecordButton",  _core.Settings.FsIdxLeftRecordButton);
                _core.Settings.FsIdxRightRecordButton = SfIn("fsRightRecordButton", _core.Settings.FsIdxRightRecordButton);
            }
            else
            {
                _core.Settings.FsLeftRecordButton  = SfIn("fsLeftRecordButton",  _core.Settings.FsLeftRecordButton);
                _core.Settings.FsRightRecordButton = SfIn("fsRightRecordButton", _core.Settings.FsRightRecordButton);
            }
            _core.Settings.FsGifMaxResolution      = data["fsGifMaxResolution"]?.Value<int>()         ?? 512;
            _core.Settings.FsGifMaxFps             = data["fsGifMaxFps"]?.Value<int>()                ?? 10;
            _core.Settings.FsUseHmdRotations       = data["fsUseHmdRotations"]?.Value<bool>()         ?? false;
            if (vrIdx)
            {
                _core.Settings.FsIdxLeftVideoButton  = SfIn("fsLeftVideoButton",  _core.Settings.FsIdxLeftVideoButton);
                _core.Settings.FsIdxRightVideoButton = SfIn("fsRightVideoButton", _core.Settings.FsIdxRightVideoButton);
            }
            else
            {
                _core.Settings.FsLeftVideoButton  = SfIn("fsLeftVideoButton",  _core.Settings.FsLeftVideoButton);
                _core.Settings.FsRightVideoButton = SfIn("fsRightVideoButton", _core.Settings.FsRightVideoButton);
            }
            if (vrIdx)
            {
                _core.Settings.FsIdxLeftAcceptButton  = SfIn("fsLeftAcceptButton",  _core.Settings.FsIdxLeftAcceptButton);
                _core.Settings.FsIdxRightAcceptButton = SfIn("fsRightAcceptButton", _core.Settings.FsIdxRightAcceptButton);
            }
            else
            {
                _core.Settings.FsLeftAcceptButton  = SfIn("fsLeftAcceptButton",  _core.Settings.FsLeftAcceptButton);
                _core.Settings.FsRightAcceptButton = SfIn("fsRightAcceptButton", _core.Settings.FsRightAcceptButton);
            }
            _core.Settings.FsVideoDeviceA          = data["fsVideoDeviceA"]?.Value<string>()          ?? "";
            _core.Settings.FsVideoDeviceB          = data["fsVideoDeviceB"]?.Value<string>()          ?? "";
            _core.Settings.FsVideoFps              = data["fsVideoFps"]?.Value<int>()                 ?? 30;
            _core.Settings.FsVideoQuality          = data["fsVideoQuality"]?.Value<string>()          ?? "1080p";
            _core.Settings.FsVideoBitrateQuality   = data["fsVideoBitrateQuality"]?.Value<string>()   ?? "medium";
            _core.Settings.FsAudioKbps             = data["fsAudioKbps"]?.Value<int>()                ?? 256;
            _core.Settings.RelayAutoStartVR        = data["relayAutoStartVR"]?.Value<bool>()        ?? false;
            _core.Settings.RelayAutoStartDesktop   = data["relayAutoStartDesktop"]?.Value<bool>()   ?? false;
            _core.Settings.YtAutoStartVR           = data["ytAutoStartVR"]?.Value<bool>()           ?? false;
            _core.Settings.YtAutoStartDesktop      = data["ytAutoStartDesktop"]?.Value<bool>()      ?? false;
            _core.Settings.VfAutoStartVR           = data["vfAutoStartVR"]?.Value<bool>()           ?? false;
            _core.Settings.VfAutoStartDesktop      = data["vfAutoStartDesktop"]?.Value<bool>()      ?? false;
            _core.Settings.DpAutoStartVR           = data["dpAutoStartVR"]?.Value<bool>()           ?? false;
            _core.Settings.DpAutoStartDesktop      = data["dpAutoStartDesktop"]?.Value<bool>()      ?? false;
            _core.Settings.VroAutoStartVR          = data["vroAutoStartVR"]?.Value<bool>()          ?? false;
            // Avatar Scaling
            _core.Settings.AsAutoStartVR            = data["asAutoStartVR"]?.Value<bool>()            ?? false;
            _core.Settings.AsAutoStartDesktop       = data["asAutoStartDesktop"]?.Value<bool>()       ?? false;
            _core.Settings.AsUseSafetySettings      = data["asUseSafety"]?.Value<bool>()              ?? false;
            _core.Settings.AsScale                  = data["asScale"]?.Value<float>()                 ?? 1.0f;
            _core.Settings.AsScaleMin               = data["asScaleMin"]?.Value<float>()              ?? 0.5f;
            _core.Settings.AsScaleMax               = data["asScaleMax"]?.Value<float>()              ?? 3.0f;
            _core.Settings.AsSaveScaleBetweenWorlds = data["asSaveScale"]?.Value<bool>()              ?? false;
            _core.Settings.AsKeyUp                  = data["asKeyUp"]?.Value<int>()                   ?? 0;
            _core.Settings.AsKeyDown                = data["asKeyDown"]?.Value<int>()                 ?? 0;
            _core.Settings.AsSmoothing              = data["asSmoothing"]?.Value<float>()             ?? 30f;
            _core.Settings.VroWaterEnabled = data["vroWaterEnabled"]?.Value<bool>() ?? false;
            _core.Settings.VroWaterHours   = data["vroWaterHours"]?.Value<int>()    ?? 1;
            _core.Settings.VroWaterMinutes = data["vroWaterMinutes"]?.Value<int>()  ?? 0;
            _core.Settings.DpHideInstIdJoinMe  = data["dpHideInstIdJoinMe"]?.Value<bool>()  ?? false;
            _core.Settings.DpHideInstIdOnline  = data["dpHideInstIdOnline"]?.Value<bool>()  ?? false;
            _core.Settings.DpHideInstIdAskMe   = data["dpHideInstIdAskMe"]?.Value<bool>()   ?? true;
            _core.Settings.DpHideInstIdBusy    = data["dpHideInstIdBusy"]?.Value<bool>()    ?? true;
            _core.Settings.DpHideLocJoinMe     = data["dpHideLocJoinMe"]?.Value<bool>()     ?? false;
            _core.Settings.DpHideLocOnline     = data["dpHideLocOnline"]?.Value<bool>()     ?? false;
            _core.Settings.DpHideLocAskMe      = data["dpHideLocAskMe"]?.Value<bool>()      ?? true;
            _core.Settings.DpHideLocBusy       = data["dpHideLocBusy"]?.Value<bool>()       ?? true;
            _core.Settings.DpHidePlayersJoinMe = data["dpHidePlayersJoinMe"]?.Value<bool>() ?? false;
            _core.Settings.DpHidePlayersOnline = data["dpHidePlayersOnline"]?.Value<bool>() ?? false;
            _core.Settings.DpHidePlayersAskMe  = data["dpHidePlayersAskMe"]?.Value<bool>()  ?? true;
            _core.Settings.DpHidePlayersBusy   = data["dpHidePlayersBusy"]?.Value<bool>()   ?? true;
            _core.Settings.DpHideJoinBtnJoinMe = data["dpHideJoinBtnJoinMe"]?.Value<bool>() ?? false;
            _core.Settings.DpHideJoinBtnOnline = data["dpHideJoinBtnOnline"]?.Value<bool>() ?? false;
            _core.Settings.DpHideJoinBtnAskMe  = data["dpHideJoinBtnAskMe"]?.Value<bool>()  ?? true;
            _core.Settings.DpHideJoinBtnBusy   = data["dpHideJoinBtnBusy"]?.Value<bool>()   ?? true;

            // Image cache
            _core.Settings.ImgCacheLimitGb         = Math.Clamp(data["imgCacheLimitGb"]?.Value<int>() ?? 5, 5, 30);
            _core.Settings.ImgCacheOptimizeEnabled = data["imgCacheOptimizeEnabled"]?.Value<bool>() ?? true;
            _core.Settings.ImgMemoryOptimizeEnabled = data["imgMemoryOptimizeEnabled"]?.Value<bool>() ?? true;
            _core.Settings.VrcPlusOptimizeEnabled   = data["vrcPlusOptimizeEnabled"]?.Value<bool>() ?? true;
            ImageCacheHelper.LimitGb         = _core.Settings.ImgCacheLimitGb;
            ImageCacheHelper.OptimizeEnabled = _core.Settings.ImgCacheOptimizeEnabled;

            // Fast Fetch Cache
            _core.Settings.FfcEnabled = data["ffcEnabled"]?.Value<bool>() ?? true;

            // Avtrdb Support
            _core.Settings.AvtrdbReportDeleted = data["avtrdbReportDeleted"]?.Value<bool>() ?? true;
            _core.Settings.AvtrdbSubmitAvatars = data["avtrdbSubmitAvatars"]?.Value<bool>() ?? false;

            // Avtr.icu Support
            _core.Settings.AvtrIcuReportDeleted = data["avtrIcuReportDeleted"]?.Value<bool>() ?? true;
            _core.Settings.AvtrIcuSubmitAvatars = data["avtrIcuSubmitAvatars"]?.Value<bool>() ?? false;

            // VRCNDb
            _core.Settings.VrcndbSubmitAvatars = data["vrcndbSubmitAvatars"]?.Value<bool>() ?? false;
            _core.Settings.VrcndbReportDeleted = data["vrcndbReportDeleted"]?.Value<bool>() ?? false;
            _core.Settings.VrcndbSyncLikes     = data["vrcndbSyncLikes"]?.Value<bool>() ?? true;
            _core.Settings.VrcndbSyncWears     = data["vrcndbSyncWears"]?.Value<bool>() ?? true;
            _core.Settings.VrcndbConsentShown  = data["vrcndbConsentShown"]?.Value<bool>() ?? _core.Settings.VrcndbConsentShown;

            // Memory Trim
            _core.Settings.MemoryTrimEnabled = data["memoryTrimEnabled"]?.Value<bool>() ?? true;
            _core.MemTrim.SetEnabled(_core.Settings.MemoryTrimEnabled);

            // Windows Fixes
            _core.Settings.MediaFixEnabled = data["mediaFixEnabled"]?.Value<bool>() ?? true;
            VRCNext.Services.WindowsFixes.SetEnabled(_core.Settings.MediaFixEnabled);

            _core.Settings.MultiTaskMode = data["multiTaskMode"]?.Value<bool>() ?? false;
            _core.Settings.TilingManager = data["tilingManager"]?.Value<bool>() ?? true;

            // Database optimization (requires restart to take effect)
            _core.Settings.DbOptimize           = data["dbOptimize"]?.Value<bool>() ?? true;
            _core.Settings.DbOptimizeMaxEntries = Math.Clamp(data["dbOptimizeMaxEntries"]?.Value<int>() ?? 500, 500, 250000);

            // Auto-Update
            _core.Settings.AutoUpdate = data["autoUpdate"]?.Value<bool>() ?? true;

            // Crash Reporting
            _core.Settings.SendCrashData       = data["sendCrashData"]?.Value<bool>()       ?? true;
            _core.Settings.RestartAfterCrash   = data["restartAfterCrash"]?.Value<bool>()   ?? true;

            // Text Tools
            _core.Settings.TextToolsEnabled = data["textToolsEnabled"]?.Value<bool>() ?? false;

            // Window Behavior
            _core.Settings.RememberWindowSize     = data["rememberWindowSize"]?.Value<bool>()     ?? false;
            _core.Settings.RememberWindowPosition = data["rememberWindowPosition"]?.Value<bool>() ?? false;

            // Auto-Backups
            _core.Settings.RegBackupEnabled    = data["regBackupEnabled"]?.Value<bool>()    ?? true;
            _core.Settings.RegBackupDays       = data["regBackupDays"]?.Value<int>()        ?? 30;
            _core.Settings.DbAutoBackupEnabled = data["dbAutoBackupEnabled"]?.Value<bool>() ?? true;
            _core.Settings.DbAutoBackupDays    = data["dbAutoBackupDays"]?.Value<int>()     ?? 60;

            // Performance flags (require restart)
            _core.Settings.GpuAcceleration    = data["gpuAcceleration"]?.Value<bool>()    ?? _core.Settings.GpuAcceleration;
            _core.Settings.LinuxGpuAcceleration = data["linuxGpuAcceleration"]?.Value<bool>() ?? _core.Settings.LinuxGpuAcceleration;
            _core.Settings.GpuShaderCache     = data["gpuShaderCache"]?.Value<bool>()     ?? false;
            _core.Settings.V8Heap128          = data["v8Heap128"]?.Value<bool>()          ?? false;
            _core.Settings.TwoRenderProcesses = data["twoRenderProcesses"]?.Value<bool>() ?? false;
            var _newEffMode = data["efficiencyMode"]?.Value<bool>() ?? _core.Settings.EfficiencyMode;
            if (_newEffMode != _core.Settings.EfficiencyMode || _newEffMode)
            {
                _core.Settings.EfficiencyMode = _newEffMode;
                VRCNext.Services.EfficiencyModeService.Apply(_newEffMode);
            }
            _core.Settings.AnimationsEnabled  = data["animationsEnabled"]?.Value<bool>()  ?? true;
            _core.Settings.BlurEnabled        = data["blurEnabled"]?.Value<bool>()        ?? true;
            _core.Settings.SearchDebounceMs   = Math.Clamp(data["searchDebounceMs"]?.Value<int>() ?? 500, 15, 900);

            // Dashboard layout
            var dashOrder  = data["dashSectionOrder"]?.ToObject<List<string>>();
            var dashHidden = data["dashSectionHidden"]?.ToObject<List<string>>();
            var dashRows   = data["dashRows"]?.ToObject<List<string>>();
            var dashHero   = data["dashHero"]?.ToObject<List<string>>();
            if (dashOrder  != null) _core.Settings.DashSectionOrder  = dashOrder;
            if (dashHidden != null) _core.Settings.DashSectionHidden = dashHidden;
            if (dashRows   != null) _core.Settings.DashRows          = dashRows;
            if (dashHero   != null) _core.Settings.DashHero          = dashHero;

            _core.Settings.Save();
            if (_core.Settings.LastSaveError != null)
            {
                _core.SendToJS("log", new { msg = $"❌ Save failed: {_core.Settings.LastSaveError}", color = "err" });
                _core.SendToJS("toast", new { ok = false, msg = "Failed to save this setting, please report this error" });
            }
            else if (DateTime.UtcNow - _readyAt > TimeSpan.FromSeconds(3))
            {
                _core.SendToJS("toast", new { ok = true, msg = "Saved" });
            }

            _core.PushDiscordPresence?.Invoke();

            // No-op with Photino — watch folders served via /media{i}/ routes
        }
        catch (Exception ex)
        {
            _core.SendToJS("log", new { msg = $"Save error: {ex.Message}", color = "err" });
            _core.SendToJS("toast", new { ok = false, msg = "Failed to save this setting, please report this error" });
        }
    }

    private static string NormalizeLanguage(string? language)
    {
        return (language ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "de" => "de",
            "es" => "es",
            "fr" => "fr",
            "ja" => "ja",
            "zh-cn" => "zh-CN",
            "zh_cn" => "zh-CN",
            "zh-tw" => "zh-TW",
            "zh_tw" => "zh-TW",
            "ru"    => "ru",
            "ko"  => "ko",
            _ => "en"
        };
    }

    private void SendTranslation(string? requestedLanguage)
    {
        try
        {
            var language = NormalizeLanguage(requestedLanguage);
            var i18nDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "frontend", "i18n");
            var path = Path.Combine(i18nDir, $"{language}.json");
            var fallbackPath = Path.Combine(i18nDir, "en.json");
            var json = File.Exists(path)
                ? File.ReadAllText(path)
                : File.Exists(fallbackPath)
                    ? File.ReadAllText(fallbackPath)
                    : "{}";
            var translations = JObject.Parse(json);
            _core.SendToJS("translationData", new { language, translations });
        }
        catch (Exception ex)
        {
            _core.SendToJS("log", new { msg = $"Translation load failed: {ex.Message}", color = "err" });
            _core.SendToJS("translationData", new { language = "en", translations = new JObject() });
        }
    }

    // Start With Windows

    internal static void ApplyStartWithWindows(bool enable)
    {
#if WINDOWS
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
        if (key == null) return;
        var exe = Environment.ProcessPath ?? "";
        if (enable)
            key.SetValue("VRCNext", $"\"{exe}\" --minimized");
        else
            key.DeleteValue("VRCNext", throwOnMissingValue: false);
#else
        var dir  = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "autostart");
        var file = Path.Combine(dir, "VRCNext.desktop");
        if (enable)
        {
            Directory.CreateDirectory(dir);
            var exe = AppInfo.SelfExecutable;
            if (string.IsNullOrEmpty(exe)) exe = "VRCNext";
            File.WriteAllText(file,
                "[Desktop Entry]\n" +
                "Type=Application\n" +
                "Name=VRCNext\n" +
                $"Exec=\"{exe}\" --minimized\n" +
                "Hidden=false\n" +
                "NoDisplay=false\n" +
                "X-GNOME-Autostart-enabled=true\n" +
                "StartupNotify=false\n");
        }
        else if (File.Exists(file)) File.Delete(file);
#endif
    }

    // VRC Path Detection

    internal static string? DetectVrcLaunchExe()
    {
        var candidates = new List<string>();

#if WINDOWS
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed) continue;
            var root = drive.RootDirectory.FullName;
            candidates.Add(Path.Combine(root, "Program Files (x86)", "Steam", "steamapps", "common", "VRChat", "launch.exe"));
            candidates.Add(Path.Combine(root, "Program Files", "Steam", "steamapps", "common", "VRChat", "launch.exe"));
            candidates.Add(Path.Combine(root, "Steam", "steamapps", "common", "VRChat", "launch.exe"));
            candidates.Add(Path.Combine(root, "SteamLibrary", "steamapps", "common", "VRChat", "launch.exe"));
            candidates.Add(Path.Combine(root, "Games", "Steam", "steamapps", "common", "VRChat", "launch.exe"));
            candidates.Add(Path.Combine(root, "Games", "SteamLibrary", "steamapps", "common", "VRChat", "launch.exe"));
        }

        var steamVdfWin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam", "steamapps", "libraryfolders.vdf");
        AddVdfLibraries(steamVdfWin, candidates, "launch.exe");
#else
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var linuxSteamRoots = new[]
        {
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".steam", "root"),
        };
        foreach (var sr in linuxSteamRoots)
        {
            candidates.Add(Path.Combine(sr, "steamapps", "common", "VRChat", "VRChat.exe"));
            candidates.Add(Path.Combine(sr, "steamapps", "common", "VRChat", "launch.exe"));
            AddVdfLibraries(Path.Combine(sr, "steamapps", "libraryfolders.vdf"), candidates, "VRChat.exe");
        }
#endif
        return candidates.FirstOrDefault(File.Exists);
    }

    private static void AddVdfLibraries(string vdfPath, List<string> candidates, string exe)
    {
        try
        {
            if (!File.Exists(vdfPath)) return;
            var vdf = File.ReadAllText(vdfPath);
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\""))
            {
                var libPath = m.Groups[1].Value.Replace("\\\\", "\\");
                candidates.Add(Path.Combine(libPath, "steamapps", "common", "VRChat", exe));
            }
        }
        catch { }
    }

    internal static string DetectVrcPhotoDir()
    {
        var path = VrcPathsHelper.PhotoDir();
        return Directory.Exists(path) ? path : "";
    }

    // Cache Send / Startup Refresh

    internal class WFavGroup
    {
        public string name        { get; set; } = "";
        public string displayName { get; set; } = "";
        public string type        { get; set; } = "";
        public int    capacity    { get; set; } = 25;
        public string visibility  { get; set; } = "private";
        public bool   local       { get; set; } = false;
    }

    internal static List<WFavGroup> FillMissingWorldSlots(List<WFavGroup> groupList, bool hasVrcPlus = false)
    {
        var existing = new HashSet<string>(groupList.Select(g => g.name));

        var regularSlots = new[] {
            ("worlds1", "Worlds 1", "world"), ("worlds2", "Worlds 2", "world"),
            ("worlds3", "Worlds 3", "world"), ("worlds4", "Worlds 4", "world")
        };
        foreach (var (sName, sDisplay, sType) in regularSlots)
            if (!existing.Contains(sName))
                groupList.Add(new WFavGroup { name = sName, displayName = sDisplay, type = sType });

        // The VRChat+ world groups only come back from /favorite/groups once they have
        // been materialized (first favorite added). Fall back to the account's VRChat+
        // status so all 8 slots show up right away for VRChat+ users.
        hasVrcPlus = hasVrcPlus || groupList.Any(g => g.type == "vrcPlusWorld");
        if (hasVrcPlus)
        {
            var vrcPlusSlots = new[] {
                ("vrcPlusWorlds1", "VRC+ Worlds 1", "vrcPlusWorld"), ("vrcPlusWorlds2", "VRC+ Worlds 2", "vrcPlusWorld"),
                ("vrcPlusWorlds3", "VRC+ Worlds 3", "vrcPlusWorld"), ("vrcPlusWorlds4", "VRC+ Worlds 4", "vrcPlusWorld")
            };
            foreach (var (sName, sDisplay, sType) in vrcPlusSlots)
                if (!existing.Contains(sName))
                    groupList.Add(new WFavGroup { name = sName, displayName = sDisplay, type = sType });
        }

        return groupList
            .OrderBy(g => g.type == "vrcPlusWorld" ? 1 : 0)
            .ThenBy(g => g.name)
            .ToList();
    }

    internal static List<WFavGroup> FillMissingAvatarSlots(List<WFavGroup> groupList)
    {
        var existing = new HashSet<string>(groupList.Select(g => g.name));

        var slots = new[] {
            ("avatars1", "Avatars 1", "avatar"),
            ("avatars2", "Avatars 2", "avatar"),
            ("avatars3", "Avatars 3", "avatar"),
            ("avatars4", "Avatars 4", "avatar"),
            ("avatars5", "Avatars 5", "avatar"),
            ("avatars6", "Avatars 6", "avatar"),
        };
        foreach (var (sName, sDisplay, sType) in slots)
            if (!existing.Contains(sName))
                groupList.Add(new WFavGroup { name = sName, displayName = sDisplay, type = sType });

        return groupList
            .OrderBy(g => g.name)
            .ToList();
    }

    internal static List<WFavGroup> FillMissingFriendSlots(List<WFavGroup> groupList)
    {
        var existing = new HashSet<string>(groupList.Select(g => g.name));
        var slots = new[] {
            ("group_0", "Group 1", "friend"),
            ("group_1", "Group 2", "friend"),
            ("group_2", "Group 3", "friend"),
        };
        foreach (var (sName, sDisplay, sType) in slots)
            if (!existing.Contains(sName))
                groupList.Add(new WFavGroup { name = sName, displayName = sDisplay, type = sType, capacity = 150 });
        foreach (var g in groupList) if (g.capacity < 100) g.capacity = 150;
        return groupList.OrderBy(g => g.name).ToList();
    }

    // Maps local SQLite favorite groups to WFavGroup, tagged with a local type so the UI shows the Local badge.
    internal static List<WFavGroup> BuildLocalGroups(IEnumerable<LocalFavoritesStore.LocalGroup> locals, string localType) =>
        locals.Select(g => new WFavGroup
        {
            name        = g.Name,
            displayName = g.DisplayName,
            type        = localType,
            capacity    = LocalFavoritesStore.MaxItems,
            local       = true,
        }).ToList();

    public async Task FetchAndCacheFavWorldsAsync()
    {
        if (Interlocked.CompareExchange(ref _favWorldsInFlight, 1, 0) != 0) return; // already running
        try
        {
            var groups = _cachedFavGroups ?? await _core.Favorites.GetFavoriteGroupsAsync();
            _cachedFavGroups = groups;
            var worldTypes = new HashSet<string> { "world", "vrcPlusWorld" };
            var groupList = groups
                .Where(g => worldTypes.Contains(g["type"]?.ToString() ?? ""))
                .Select(g => new WFavGroup {
                    name        = g["name"]?.ToString() ?? "",
                    displayName = g["displayName"]?.ToString() ?? "",
                    type        = g["type"]?.ToString() ?? "world",
                    visibility  = g["visibility"]?.ToString() ?? "private",
                })
                .Where(g => !string.IsNullOrEmpty(g.name))
                .ToList();
            groupList = FillMissingWorldSlots(groupList, _core.VrcApi.HasVrcPlus);

            var sem = new SemaphoreSlim(4, 4);
            var perGroup = new System.Collections.Concurrent.ConcurrentDictionary<string, List<JObject>>();
            await Task.WhenAll(groupList.Select(async g =>
            {
                await sem.WaitAsync();
                try { perGroup[g.name] = await _core.Favorites.GetFavoriteWorldsByGroupAsync(g.name, 100); }
                finally { sem.Release(); }
            }));

            var allWorlds = new List<object>();
            foreach (var g in groupList)
            {
                if (!perGroup.TryGetValue(g.name, out var groupWorlds)) continue;
                foreach (var w in groupWorlds)
                {
                    var wid = w["id"]?.ToString() ?? "";
                    var stats = _core.TimeEngine.GetWorldStats(wid);
                    AppShell.EnrichWorldDatesFromCache(_core.TimeEngine, w, wid);
                    var rawWorldImg = ImageCacheHelper.GetWorldUrl(wid, w["imageUrl"]?.ToString() ?? w["thumbnailImageUrl"]?.ToString());
                    allWorlds.Add(new
                    {
                        id                = wid,
                        name              = w["name"]?.ToString() ?? "",
                        imageUrl          = rawWorldImg,
                        thumbnailImageUrl = rawWorldImg,
                        authorName        = w["authorName"]?.ToString() ?? "",
                        occupants         = w["occupants"]?.Value<int>()  ?? 0,
                        capacity          = w["capacity"]?.Value<int>()   ?? 0,
                        favorites         = w["favorites"]?.Value<int>()  ?? 0,
                        visits            = w["visits"]?.Value<int>()     ?? 0,
                        tags              = w["tags"]?.ToObject<List<string>>() ?? new List<string>(),
                        created_at        = DateTimeHelper.Iso(w["created_at"]),
                        updated_at        = DateTimeHelper.Iso(w["updated_at"]),
                        favoriteGroup     = g.name,
                        favoriteId        = w["favoriteId"]?.ToString() ?? "",
                        worldTimeSeconds  = stats.totalSeconds,
                        worldVisitCount   = stats.visitCount,
                        worldLastVisited  = stats.lastVisited,
                    });
                }
            }

            foreach (var lg in BuildLocalGroups(_core.LocalFavorites.GetGroups("world"), "localWorld")) groupList.Add(lg);
            foreach (var it in _core.LocalFavorites.GetItems("world"))
            {
                var w = it.Snapshot;
                var wid = it.EntityId;
                var stats = _core.TimeEngine.GetWorldStats(wid);
                AppShell.EnrichWorldDatesFromCache(_core.TimeEngine, w, wid);
                var rawWorldImg = ImageCacheHelper.GetWorldUrl(wid, w["imageUrl"]?.ToString() ?? w["thumbnailImageUrl"]?.ToString());
                allWorlds.Add(new
                {
                    id                = wid,
                    name              = w["name"]?.ToString() ?? "",
                    imageUrl          = rawWorldImg,
                    thumbnailImageUrl = rawWorldImg,
                    authorName        = w["authorName"]?.ToString() ?? "",
                    occupants         = w["occupants"]?.Value<int>()  ?? 0,
                    capacity          = w["capacity"]?.Value<int>()   ?? 0,
                    favorites         = w["favorites"]?.Value<int>()  ?? 0,
                    visits            = w["visits"]?.Value<int>()     ?? 0,
                    tags              = w["tags"]?.ToObject<List<string>>() ?? new List<string>(),
                    created_at        = DateTimeHelper.Iso(w["created_at"]),
                    updated_at        = DateTimeHelper.Iso(w["updated_at"]),
                    favoriteGroup     = it.GroupName,
                    favoriteId        = it.Id,
                    worldTimeSeconds  = stats.totalSeconds,
                    worldVisitCount   = stats.visitCount,
                    worldLastVisited  = stats.lastVisited,
                });
            }

            var payload = new { worlds = allWorlds, groups = groupList };
            if (_core.Settings.FfcEnabled) _core.Cache.Save(CacheHandler.KeyFavWorlds, payload);
            Invoke(() => _core.SendToJS("vrcFavoriteWorlds", payload));
        }
        catch (Exception ex)
        {
            Invoke(() => _core.SendToJS("log", new { msg = $"Favorite worlds error: {ex.Message}", color = "err" }));
        }
        finally { Interlocked.Exchange(ref _favWorldsInFlight, 0); }
    }

    public async Task FetchAndCacheFavAvatarsAsync()
    {
        if (Interlocked.CompareExchange(ref _favAvatarsInFlight, 1, 0) != 0) return; // already running
        try
        {
            var groups = _cachedFavGroups ?? await _core.Favorites.GetFavoriteGroupsAsync();
            _cachedFavGroups = groups;
            var avatarTypes = new HashSet<string> { "avatar" };
            var groupList = groups
                .Where(g => avatarTypes.Contains(g["type"]?.ToString() ?? ""))
                .Select(g => new WFavGroup {
                    name        = g["name"]?.ToString() ?? "",
                    displayName = g["displayName"]?.ToString() ?? "",
                    type        = g["type"]?.ToString() ?? "avatar"
                })
                .Where(g => !string.IsNullOrEmpty(g.name))
                .ToList();
            groupList = FillMissingAvatarSlots(groupList);
            int avCap = _core.VrcApi.HasVrcPlus ? 50 : 25;
            foreach (var g in groupList) g.capacity = avCap;

            var sem = new SemaphoreSlim(4, 4);
            var perGroup = new System.Collections.Concurrent.ConcurrentDictionary<string, List<JObject>>();
            await Task.WhenAll(groupList.Select(async g =>
            {
                await sem.WaitAsync();
                try { perGroup[g.name] = await _core.Favorites.GetFavoriteAvatarsByGroupAsync(g.name, 100); }
                finally { sem.Release(); }
            }));

            var allAvatarsRaw = new List<object>();
            var allAvatarsJs  = new List<object>();
            var favLikeIds    = new List<string>();
            foreach (var g in groupList)
            {
                if (!perGroup.TryGetValue(g.name, out var groupAvatars)) continue;
                foreach (var a in groupAvatars)
                {
                    AppShell.CacheAvatarDetailFrom(_core.TimeEngine, a);
                    AppShell.EnrichAvatarFromCache(_core.TimeEngine, a, a["id"]?.ToString() ?? "");
                    var rawUrl    = a["imageUrl"]?.ToString() ?? a["thumbnailImageUrl"]?.ToString() ?? "";
                    var img       = ImageCacheHelper.GetAvatarUrl(a["id"]?.ToString(), rawUrl);
                    var id        = a["id"]?.ToString() ?? "";
                    var name      = a["name"]?.ToString() ?? "";
                    var author    = a["authorName"]?.ToString() ?? "";
                    var release   = a["releaseStatus"]?.ToString() ?? "private";
                    var fvrtId    = a["favoriteId"]?.ToString() ?? "";
                    var pkgs      = (a["unityPackages"] as JArray ?? new JArray())
                        .Select(p => new { platform = p["platform"]?.ToString() ?? "", variant = p["variant"]?.ToString() ?? "", performanceRating = p["performanceRating"]?.ToString() ?? "" })
                        .ToArray();
                    var created   = DateTimeHelper.Iso(a["created_at"]);
                    var updated   = DateTimeHelper.Iso(a["updated_at"]);
                    var tags      = (a["tags"] as JArray ?? new JArray()).Select(x => x?.ToString() ?? "").ToArray();
                    allAvatarsRaw.Add(new { id, name, imageUrl = rawUrl, thumbnailImageUrl = rawUrl, authorName = author, releaseStatus = release, favoriteGroup = g.name, favoriteId = fvrtId, created_at = created, updated_at = updated, tags, unityPackages = pkgs });
                    allAvatarsJs.Add(new  { id, name, imageUrl = img,    thumbnailImageUrl = img,    authorName = author, releaseStatus = release, favoriteGroup = g.name, favoriteId = fvrtId, created_at = created, updated_at = updated, tags, unityPackages = pkgs });
                    favLikeIds.Add(id);
                }
            }

            foreach (var lg in BuildLocalGroups(_core.LocalFavorites.GetGroups("avatar"), "localAvatar")) groupList.Add(lg);
            foreach (var it in _core.LocalFavorites.GetItems("avatar"))
            {
                var a         = it.Snapshot;
                AppShell.EnrichAvatarFromCache(_core.TimeEngine, a, it.EntityId);
                var rawUrl    = a["imageUrl"]?.ToString() ?? a["thumbnailImageUrl"]?.ToString() ?? "";
                var img       = ImageCacheHelper.GetAvatarUrl(it.EntityId, rawUrl);
                var id        = it.EntityId;
                var name      = a["name"]?.ToString() ?? "";
                var author    = a["authorName"]?.ToString() ?? "";
                var release   = a["releaseStatus"]?.ToString() ?? "private";
                var pkgs      = (a["unityPackages"] as JArray ?? new JArray())
                    .Select(p => new { platform = p["platform"]?.ToString() ?? "", variant = p["variant"]?.ToString() ?? "", performanceRating = p["performanceRating"]?.ToString() ?? "" })
                    .ToArray();
                var created   = DateTimeHelper.Iso(a["created_at"]);
                var updated   = DateTimeHelper.Iso(a["updated_at"]);
                var tags      = (a["tags"] as JArray ?? new JArray()).Select(x => x?.ToString() ?? "").ToArray();
                allAvatarsRaw.Add(new { id, name, imageUrl = rawUrl, thumbnailImageUrl = rawUrl, authorName = author, releaseStatus = release, favoriteGroup = it.GroupName, favoriteId = it.Id, created_at = created, updated_at = updated, tags, unityPackages = pkgs });
                allAvatarsJs.Add(new  { id, name, imageUrl = img,    thumbnailImageUrl = img,    authorName = author, releaseStatus = release, favoriteGroup = it.GroupName, favoriteId = it.Id, created_at = created, updated_at = updated, tags, unityPackages = pkgs });
                favLikeIds.Add(id);
            }

            if (_core.Settings.FfcEnabled) _core.Cache.Save(CacheHandler.KeyFavAvatars, new { avatars = allAvatarsRaw, groups = groupList });
            Invoke(() => _core.SendToJS("vrcFavoriteAvatars", new { avatars = allAvatarsJs, groups = groupList }));
            if (_core.Settings.VrcndbSyncLikes) PopularityReporter.SyncFavoriteLikes(favLikeIds);
        }
        catch (Exception ex)
        {
            Invoke(() => _core.SendToJS("log", new { msg = $"Favorite avatars error: {ex.Message}", color = "err" }));
        }
        finally { Interlocked.Exchange(ref _favAvatarsInFlight, 0); }
    }

    public async Task FetchAndCacheAvatarsAsync()
    {
        try
        {
            var avatars = await _core.Avatars.GetOwnAvatarsAsync();
            foreach (var a in avatars)
            {
                AppShell.CacheAvatarDetailFrom(_core.TimeEngine, a);
                AppShell.EnrichAvatarFromCache(_core.TimeEngine, a, a["id"]?.ToString() ?? "");
            }
            // Build with raw CDN URLs so FFC can detect image changes on next load
            var rawList = avatars.Select(a => new
            {
                id                = a["id"]?.ToString() ?? "",
                name              = a["name"]?.ToString() ?? "",
                imageUrl          = a["imageUrl"]?.ToString() ?? a["thumbnailImageUrl"]?.ToString() ?? "",
                thumbnailImageUrl = a["thumbnailImageUrl"]?.ToString() ?? a["imageUrl"]?.ToString() ?? "",
                authorName        = a["authorName"]?.ToString() ?? "",
                releaseStatus     = a["releaseStatus"]?.ToString() ?? "private",
                description       = a["description"]?.ToString() ?? "",
                created_at        = DateTimeHelper.Iso(a["created_at"]),
                updated_at        = DateTimeHelper.Iso(a["updated_at"]),
                tags              = (a["tags"] as JArray ?? new JArray()).Select(x => x?.ToString() ?? "").ToArray(),
                unityPackages     = (a["unityPackages"] as JArray ?? new JArray())
                    .Select(p => new { platform = p["platform"]?.ToString() ?? "", variant = p["variant"]?.ToString() ?? "", performanceRating = p["performanceRating"]?.ToString() ?? "" })
                    .ToArray(),
            }).ToList();
            if (_core.Settings.FfcEnabled)
                _core.Cache.Save(CacheHandler.KeyAvatars, new { filter = "own", avatars = rawList, currentAvatarId = _core.VrcApi.CurrentAvatarId ?? "" });
            // Send to JS with processed image URLs (disk cache or CDN)
            var jsList = rawList.Select(a => { var img = ImageCacheHelper.GetAvatarUrl(a.id, a.imageUrl); return new { a.id, a.name, imageUrl = img, thumbnailImageUrl = img, a.authorName, a.releaseStatus, a.description, a.created_at, a.updated_at, a.tags, a.unityPackages }; }).ToList();
            Invoke(() => _core.SendToJS("vrcAvatars", new { filter = "own", avatars = jsList, currentAvatarId = _core.VrcApi.CurrentAvatarId ?? "" }));
        }
        catch (Exception ex)
        {
            Invoke(() => _core.SendToJS("log", new { msg = $"Avatar load error: {ex.Message}", color = "err" }));
        }
    }

    private void SendAllCachedData()
    {
        var customColors = _core.Cache.LoadRaw(CacheHandler.KeyCustomColors);
        if (customColors != null) _core.SendToJS("customColors", customColors);

        if (!_core.Settings.FfcEnabled) return;

        // Re-process image URLs before sending — FFC stores raw CDN URLs that bypass ImageCache
        if (_core.Cache.LoadRaw(CacheHandler.KeyAvatars) is JObject avatarsObj)
        {
            foreach (var a in avatarsObj["avatars"] as JArray ?? new JArray())
                if (a is JObject ao)
                {
                    ao["imageUrl"] = ImageCacheHelper.GetAvatarUrl(ao["id"]?.ToString(), ao["imageUrl"]?.ToString() ?? ao["thumbnailImageUrl"]?.ToString());
                    ao["thumbnailImageUrl"] = ao["imageUrl"];
                    AppShell.EnrichAvatarFromCache(_core.TimeEngine, ao, ao["id"]?.ToString() ?? "");
                }
            _core.SendToJS("vrcAvatars", avatarsObj);
        }

        if (_core.Cache.LoadRaw(CacheHandler.KeyFavWorlds) is JObject favWorldsObj)
        {
            foreach (var grp in favWorldsObj["worlds"] as JArray ?? new JArray())
                if (grp is JObject wo)
                {
                    wo["imageUrl"] = ImageCacheHelper.GetWorldUrl(wo["id"]?.ToString(), wo["imageUrl"]?.ToString() ?? wo["thumbnailImageUrl"]?.ToString());
                    wo["thumbnailImageUrl"] = wo["imageUrl"];
                    AppShell.EnrichWorldDatesFromCache(_core.TimeEngine, wo, wo["id"]?.ToString() ?? "");
                }
            _core.SendToJS("vrcFavoriteWorlds", favWorldsObj);
        }

        if (_core.Cache.LoadRaw(CacheHandler.KeyFavAvatars) is JObject favAvatarsObj)
        {
            var favLikeIds = new List<string>();
            foreach (var a in favAvatarsObj["avatars"] as JArray ?? new JArray())
                if (a is JObject ao)
                {
                    ao["imageUrl"] = ImageCacheHelper.GetAvatarUrl(ao["id"]?.ToString(), ao["imageUrl"]?.ToString() ?? ao["thumbnailImageUrl"]?.ToString());
                    ao["thumbnailImageUrl"] = ao["imageUrl"];
                    AppShell.EnrichAvatarFromCache(_core.TimeEngine, ao, ao["id"]?.ToString() ?? "");
                    var fid = ao["id"]?.ToString() ?? "";
                    if (fid.Length > 0) favLikeIds.Add(fid);
                }
            _core.SendToJS("vrcFavoriteAvatars", favAvatarsObj);
            if (_core.Settings.VrcndbSyncLikes) PopularityReporter.SyncFavoriteLikes(favLikeIds);
        }

        if (_core.Cache.LoadRaw(CacheHandler.KeyFavFriends) is JObject favFriendsObj)
            _core.SendToJS("vrcFavoriteFriends", favFriendsObj);
    }

    private static readonly TimeSpan StartupCacheTtl = TimeSpan.FromDays(1);

    private async Task TriggerStartupBackgroundRefreshAsync()
    {
        if (!_core.VrcApi.IsLoggedIn) return;
        _ = Task.Run(_groups.FetchRepresentedGroupAsync);
        if (!_core.Cache.IsFresh(CacheHandler.KeyAvatars,    StartupCacheTtl)) _ = Task.Run(FetchAndCacheAvatarsAsync);
        if (!_core.Cache.IsFresh(CacheHandler.KeyGroups,    StartupCacheTtl))  _ = Task.Run(_groups.FetchAndCacheAsync);
        if (!_core.Cache.IsFresh(CacheHandler.KeyFavWorlds, StartupCacheTtl)) _ = Task.Run(FetchAndCacheFavWorldsAsync);
        if (!_core.Cache.IsFresh(CacheHandler.KeyFavAvatars, StartupCacheTtl)) _ = Task.Run(FetchAndCacheFavAvatarsAsync);
        if (!_core.Cache.IsFresh(CacheHandler.KeyFavFriends, StartupCacheTtl)) _ = Task.Run(_friends.FetchAndCacheFavFriendsAsync);
        if (_core.PrefetchSharedContent != null) _ = Task.Run(_core.PrefetchSharedContent);
        _ = Task.Run(CollectWorldStatsIfMissingAsync);
        if (_core.Settings.AutoUpdate) _ = Task.Run(AutoUpdateAsync);
        await Task.CompletedTask;
    }

    private async Task AutoUpdateAsync()
    {
        try
        {
            var version = await _core.UpdateService.CheckAsync();
            if (version == null) return;

            Invoke(() => _core.SendToJS("updateAvailable", new { version }));

            await _core.UpdateService.DownloadAsync(p =>
                Invoke(() => _core.SendToJS("updateProgress", p)));

            Invoke(() => _core.SendToJS("updateReady", null));
            await Task.Delay(800);
            Invoke(() => _core.UpdateService.ApplyAndRestart());
        }
        catch { }
    }

    private async Task CollectWorldStatsIfMissingAsync()
    {
        try
        {
            if (!_core.VrcApi.IsLoggedIn) return;
            if (_core.Timeline.HasWorldStatsForCurrentHour()) return;
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
        }
        catch { }
    }

    public async Task ForceFfcAllAsync()
    {
        if (!_core.VrcApi.IsLoggedIn) return;

        void Progress(int current, int total, string label) =>
            Invoke(() => _core.SendToJS("ffcProgress", new {
                progress = total > 0 ? (int)((double)current / total * 100) : 0,
                label,
                done = false
            }));

        try
        {
            var friendIds = _friends.GetTrackedUserIds();
            int total = friendIds.Count + 4;
            int completed = 0;

            Progress(completed, total, "Caching avatars...");
            await FetchAndCacheAvatarsAsync();
            Progress(++completed, total, "Caching favorite avatars...");
            await FetchAndCacheFavAvatarsAsync();
            Progress(++completed, total, "Caching groups...");
            await _groups.FetchAndCacheAsync();
            Progress(++completed, total, "Caching worlds...");
            await FetchAndCacheFavWorldsAsync();

            var semaphore = new SemaphoreSlim(4, 4);
            var tasks = friendIds.Select(async uid =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var payload = await _friends.BuildUserDetailPayloadAsync(uid);
                    if (payload != null)
                    {
                        _core.TimeEngine.SaveUserProfileCache(uid, Newtonsoft.Json.JsonConvert.SerializeObject(payload));
                    }
                    await Task.Delay(250);
                }
                catch { }
                finally
                {
                    semaphore.Release();
                    int c = Interlocked.Increment(ref completed);
                    Progress(c, total, $"Caching profiles... ({c - 3}/{friendIds.Count})");
                }
            });

            await Task.WhenAll(tasks);

            Invoke(() =>
            {
                _core.SendToJS("ffcProgress", new { progress = 100, label = $"Done! {friendIds.Count} profiles cached.", done = true });
                _core.SendToJS("log", new { msg = $"FFC: {friendIds.Count} profiles + avatars + groups + worlds cached.", color = "ok" });
            });
        }
        catch (Exception ex)
        {
            Invoke(() => _core.SendToJS("ffcProgress", new { progress = 0, label = "Error: " + ex.Message, done = true }));
        }
    }
}
