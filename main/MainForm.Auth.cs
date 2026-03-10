using Newtonsoft.Json.Linq;
using VRCNext.Services;

namespace VRCNext;

public partial class MainForm
{
    // VRChat API
    private string _pending2faType = "totp";
    private bool _vrcDebugSetup;

    private void SetupVrcDebugLog()
    {
        if (_vrcDebugSetup) return;
        _vrcDebugSetup = true;
        _vrcApi.DebugLog += msg =>
        {
            try { SendToJS("log", new { msg = $"[VRC] {msg}", color = "sec" }); } catch { }
        };
        _logWatcher.DebugLog += msg =>
        {
            try { SendToJS("log", new { msg = $"[LOG] {msg}", color = "sec" }); } catch { }
        };
        _logWatcher.WorldChanged += (wId, loc) =>
        {
            try { HandleWorldChangedOnUiThread(wId, loc); } catch { }
        };
        _logWatcher.PlayerJoined += (uid, name) =>
        {
            try { HandlePlayerJoinedOnUiThread(uid, name); } catch { }
        };
        _logWatcher.PlayerLeft += (uid, name) =>
        {
            try { PushCurrentInstanceFromCache(); } catch { }
        };
        _logWatcher.InstanceClosed += loc =>
        {
            try
            {
                _recentlyClosedLocs.Add(loc);
                if (_settings.MyInstances.Remove(loc))
                {
                    _settings.Save();
                    _ = Task.Run(() => OnWebMessage("""{"type":"vrcGetMyInstances"}"""));
                }
            }
            catch { }
        };
        _logWatcher.AvatarChanged += (displayName, avatarName) =>
        {
            try
            {
                var myName = _vrcApi.CurrentUserRaw?["displayName"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(myName) || displayName != myName) return;
                // Dedup: VRChat re-logs avatar on every instance join; skip if name unchanged
                if (avatarName == _lastAvatarName) return;
                _lastAvatarName = avatarName;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Wait a moment then refresh user to get new currentAvatar ID
                        await Task.Delay(2000);
                        await _vrcApi.GetCurrentUserLocationAsync(); // updates CurrentUserRaw
                        var avatarId = _vrcApi.CurrentAvatarId ?? "";
                        string avatarThumb = "";
                        if (!string.IsNullOrEmpty(avatarId))
                        {
                            var av = await _vrcApi.GetAvatarAsync(avatarId);
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
                        _timeline.AddEvent(ev);
                        SendToJS("timelineEvent", BuildTimelinePayload(ev));
                    }
                    catch { }
                });
            }
            catch { }
        };
        _logWatcher.VideoUrl += url =>
        {
            try
            {
                // Deduplicate: skip if same URL was logged within the last 30 seconds
                var now = DateTime.UtcNow;
                if (_lastVideoUrl == url && (now - _lastVideoUrlTime).TotalSeconds < 30) return;
                _lastVideoUrl     = url;
                _lastVideoUrlTime = now;

                var ev = new TimelineService.TimelineEvent
                {
                    Type      = "video_url",
                    Timestamp = now.ToString("o"),
                    WorldId   = _logWatcher.CurrentWorldId ?? "",
                    WorldName = _cachedInstWorldName,
                    Message   = url,
                };
                _timeline.AddEvent(ev);
                SendToJS("timelineEvent", BuildTimelinePayload(ev));
            }
            catch { }
        };
    }

    /// <summary>
    /// Try to resume session from saved cookies. No 2FA prompt on startup.
    /// Falls back to showing the login prompt if session is expired.
    /// </summary>
    private async Task VrcTryResumeAsync()
    {
        SetupVrcDebugLog();

        // If we have saved auth cookies, try to resume
        if (!string.IsNullOrEmpty(_settings.VrcAuthCookie))
        {
            SendToJS("log", new { msg = "VRChat: Resuming session...", color = "sec" });
            _vrcApi.RestoreCookies(_settings.VrcAuthCookie, _settings.VrcTwoFactorCookie);

            var result = await _vrcApi.TryResumeSessionAsync();
            if (result.Success && result.User != null)
            {
                SendVrcUserData(result.User, loginFlow: true);
                SendToJS("log", new { msg = $"VRChat: Reconnected as {result.User["displayName"]}", color = "ok" });
                SendAllCachedData();
                await VrcRefreshFriendsAsync();
                _wsDisconnectedAt = DateTime.UtcNow;
                StartWebSocket();
                _ = TriggerStartupBackgroundRefreshAsync();
                return;
            }

            // Session expired; clear cookies
            _settings.VrcAuthCookie = "";
            _settings.VrcTwoFactorCookie = "";
            _settings.Save();
            SendToJS("log", new { msg = "VRChat: Session expired, please log in again", color = "warn" });
        }

        // Pre-fill login form if credentials are saved (but don't auto-login, no 2FA nag)
        if (!string.IsNullOrEmpty(_settings.VrcUsername))
        {
            SendToJS("vrcPrefillLogin", new
            {
                username = _settings.VrcUsername,
                password = _settings.VrcPassword
            });
        }
    }

    private async Task VrcLoginAsync(string username, string password)
    {
        SetupVrcDebugLog();
        SendToJS("log", new { msg = "VRChat: Logging in...", color = "sec" });
        var result = await _vrcApi.LoginAsync(username, password);
        if (result.Requires2FA)
        {
            _pending2faType = result.TwoFactorType;
            SendToJS("vrcNeeds2FA", new { type = result.TwoFactorType });
            SendToJS("log", new { msg = $"VRChat: 2FA required ({result.TwoFactorType})", color = "warn" });
        }
        else if (result.Success && result.User != null)
        {
            // Save credentials AND session cookies
            _settings.VrcUsername = username;
            _settings.VrcPassword = password;
            SaveVrcCookies();
            _settings.Save();

            SendVrcUserData(result.User, loginFlow: true);
            SendToJS("log", new { msg = $"VRChat: Logged in as {result.User["displayName"]}", color = "ok" });
            await VrcRefreshFriendsAsync();
            _wsDisconnectedAt = DateTime.UtcNow;
            StartWebSocket();
            _ = TriggerStartupBackgroundRefreshAsync();
        }
        else
        {
            SendToJS("vrcLoginError", new { error = result.Error ?? "Login failed" });
            SendToJS("log", new { msg = $"VRChat: {result.Error}", color = "err" });
        }
    }

    private async Task VrcVerify2FAAsync(string code, string type)
    {
        var result = await _vrcApi.Verify2FAAsync(code, type);
        if (result.Success && result.User != null)
        {
            // Save session cookies after successful 2FA
            SaveVrcCookies();
            _settings.Save();

            SendVrcUserData(result.User, loginFlow: true);
            SendToJS("log", new { msg = $"VRChat: Logged in as {result.User["displayName"]}", color = "ok" });
            await VrcRefreshFriendsAsync();
            _wsDisconnectedAt = DateTime.UtcNow;
            StartWebSocket();
            _ = TriggerStartupBackgroundRefreshAsync();
        }
        else
        {
            SendToJS("vrcLoginError", new { error = result.Error ?? "2FA failed" });
            SendToJS("log", new { msg = $"VRChat: 2FA error — {result.Error}", color = "err" });
        }
    }

    private void SaveVrcCookies()
    {
        var (auth, tfa) = _vrcApi.GetCookies();
        _settings.VrcAuthCookie = auth ?? "";
        _settings.VrcTwoFactorCookie = tfa ?? "";
    }

    private static void ApplyStartWithWindows(bool enable)
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
            File.WriteAllText(file,
                $"[Desktop Entry]\nType=Application\nName=VRCNext\n" +
                $"Exec=\"{Environment.ProcessPath ?? "VRCNext"}\" --minimized\nHidden=false\n");
        }
        else if (File.Exists(file)) File.Delete(file);
#endif
    }

    // Settings
    private void ApplySettings(JToken data)
    {
        try
        {
            _settings.BotName = data["botName"]?.ToString() ?? "VRCNext";
            _settings.BotAvatarUrl = data["botAvatar"]?.ToString() ?? "";
            _settings.VrcPath = data["vrcPath"]?.ToString() ?? "";
            _settings.AutoStart = data["autoStart"]?.Value<bool>() ?? false;
            _settings.StartWithWindows = data["startWithWindows"]?.Value<bool>() ?? false;
            ApplyStartWithWindows(_settings.StartWithWindows);
            _settings.PostAll = data["postAll"]?.Value<bool>() ?? false;
            _settings.Notifications = data["notifications"]?.Value<bool>() ?? true;
            _settings.NotifySound = data["notifySound"]?.Value<bool>() ?? false;
            _settings.MinimizeToTray = data["minimizeToTray"]?.Value<bool>() ?? false;
            _settings.Theme = data["theme"]?.ToString() ?? "midnight";

            var dashBg = data["dashBgPath"]?.ToString();
            if (dashBg != null) _settings.DashBgPath = dashBg;
            _settings.DashOpacity = data["dashOpacity"]?.Value<int>() ?? 40;
            _settings.RandomDashBg = data["randomDashBg"]?.Value<bool>() ?? false;

            // Webhooks: explicit parsing to handle any casing
            if (data["webhooks"] is JArray whArr && whArr.Count > 0)
            {
                _settings.Webhooks.Clear();
                for (int i = 0; i < Math.Min(whArr.Count, 4); i++)
                {
                    var item = whArr[i];
                    _settings.Webhooks.Add(new AppSettings.WebhookSlot {
                        Name = (item["Name"] ?? item["name"])?.ToString() ?? "",
                        Url = (item["Url"] ?? item["url"])?.ToString() ?? "",
                        Enabled = (item["Enabled"] ?? item["enabled"])?.Value<bool>() ?? false,
                    });
                }
                while (_settings.Webhooks.Count < 4)
                    _settings.Webhooks.Add(new AppSettings.WebhookSlot { Name = $"Channel {_settings.Webhooks.Count + 1}" });
            }

            var folders = data["folders"]?.ToObject<List<string>>();
            if (folders != null) _settings.WatchFolders = folders;

            var extraExe = data["extraExe"]?.ToObject<List<string>>();
            if (extraExe != null) _settings.ExtraExe = extraExe;

            var vrcU = data["vrcUsername"]?.ToString();
            var vrcP = data["vrcPassword"]?.ToString();
            if (vrcU != null) _settings.VrcUsername = vrcU;
            if (vrcP != null) _settings.VrcPassword = vrcP;

            // Space Flight settings
            _settings.SfMultiplier = data["sfMultiplier"]?.Value<float>() ?? 1f;
            _settings.SfLockX = data["sfLockX"]?.Value<bool>() ?? false;
            _settings.SfLockY = data["sfLockY"]?.Value<bool>() ?? false;
            _settings.SfLockZ = data["sfLockZ"]?.Value<bool>() ?? false;
            _settings.SfLeftHand = data["sfLeftHand"]?.Value<bool>() ?? false;
            _settings.SfRightHand = data["sfRightHand"]?.Value<bool>() ?? true;
            _settings.SfUseGrip = data["sfUseGrip"]?.Value<bool>() ?? true;
            _settings.ChatboxAutoStart = data["chatboxAutoStart"]?.Value<bool>() ?? false;
            _settings.SfAutoStart = data["sfAutoStart"]?.Value<bool>() ?? false;
            _settings.DiscordPresenceAutoStart = data["discordPresenceAutoStart"]?.Value<bool>() ?? false;
            _settings.DpHideInstIdJoinMe  = data["dpHideInstIdJoinMe"]?.Value<bool>()  ?? false;
            _settings.DpHideInstIdOnline  = data["dpHideInstIdOnline"]?.Value<bool>()  ?? false;
            _settings.DpHideInstIdAskMe   = data["dpHideInstIdAskMe"]?.Value<bool>()   ?? true;
            _settings.DpHideInstIdBusy    = data["dpHideInstIdBusy"]?.Value<bool>()    ?? true;
            _settings.DpHideLocJoinMe     = data["dpHideLocJoinMe"]?.Value<bool>()     ?? false;
            _settings.DpHideLocOnline     = data["dpHideLocOnline"]?.Value<bool>()     ?? false;
            _settings.DpHideLocAskMe      = data["dpHideLocAskMe"]?.Value<bool>()      ?? true;
            _settings.DpHideLocBusy       = data["dpHideLocBusy"]?.Value<bool>()       ?? true;
            _settings.DpHidePlayersJoinMe = data["dpHidePlayersJoinMe"]?.Value<bool>() ?? false;
            _settings.DpHidePlayersOnline = data["dpHidePlayersOnline"]?.Value<bool>() ?? false;
            _settings.DpHidePlayersAskMe  = data["dpHidePlayersAskMe"]?.Value<bool>()  ?? true;
            _settings.DpHidePlayersBusy   = data["dpHidePlayersBusy"]?.Value<bool>()   ?? true;
            _settings.DpHideJoinBtnJoinMe = data["dpHideJoinBtnJoinMe"]?.Value<bool>() ?? false;
            _settings.DpHideJoinBtnOnline = data["dpHideJoinBtnOnline"]?.Value<bool>() ?? false;
            _settings.DpHideJoinBtnAskMe  = data["dpHideJoinBtnAskMe"]?.Value<bool>()  ?? true;
            _settings.DpHideJoinBtnBusy   = data["dpHideJoinBtnBusy"]?.Value<bool>()   ?? true;

            // Image cache settings
            _settings.ImgCacheEnabled  = data["imgCacheEnabled"]?.Value<bool>() ?? true;
            _settings.ImgCacheLimitGb  = data["imgCacheLimitGb"]?.Value<int>()  ?? 5;
            if (_imgCache != null)
            {
                _imgCache.Enabled    = _settings.ImgCacheEnabled;
                _imgCache.LimitBytes = (long)_settings.ImgCacheLimitGb * 1024 * 1024 * 1024;
                // Apply limit immediately — don't wait for the next download
                if (_settings.ImgCacheEnabled && _imgCache.LimitBytes > 0)
                    _ = Task.Run(() => _imgCache.TrimIfNeeded(_imgCache.LimitBytes));
            }

            // Fast Fetch Cache
            _settings.FfcEnabled = data["ffcEnabled"]?.Value<bool>() ?? true;

            // Memory Trim
            _settings.MemoryTrimEnabled = data["memoryTrimEnabled"]?.Value<bool>() ?? false;
            _memTrim.SetEnabled(_settings.MemoryTrimEnabled);

            _settings.Save();
            if (_settings.LastSaveError != null)
                SendToJS("log", new { msg = $"❌ Save failed: {_settings.LastSaveError}", color = "err" });

            UpdateVirtualHostMappings();
        }
        catch (Exception ex)
        {
            SendToJS("log", new { msg = $"Save error: {ex.Message}", color = "err" });
        }
    }

    private static string? DetectVrcLaunchExe()
    {
        // Check common Steam library locations across all drives
        var candidates = new List<string>();

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

        // Also try reading Steam's libraryfolders.vdf for custom library paths
        try
        {
            var steamDefault = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Steam", "steamapps", "libraryfolders.vdf");
            if (File.Exists(steamDefault))
            {
                var vdf = File.ReadAllText(steamDefault);
                foreach (System.Text.RegularExpressions.Match m in
                    System.Text.RegularExpressions.Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\""))
                {
                    var libPath = m.Groups[1].Value.Replace("\\\\", "\\");
                    candidates.Add(Path.Combine(libPath, "steamapps", "common", "VRChat", "launch.exe"));
                }
            }
        }
        catch { }

        return candidates.FirstOrDefault(File.Exists);
    }
}
