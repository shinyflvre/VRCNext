using System.Diagnostics;
using Newtonsoft.Json.Linq;
using VRCNext.Services;
using VRCNext.Services.Helpers;

namespace VRCNext;

// Owns all Relay, VRCVideoCacher, VRChat launch, and WebSocket lifecycle state + logic.

public class RelayController : IDisposable
{
    private readonly CoreLibrary _core;
    private readonly FriendsController _friends;
    private readonly InstanceController _instance;
    private readonly NotificationsController _notifications;
    private readonly VROverlayController _vroCtrl;

    // Fields (moved from MainForm.Fields.cs)
    private bool _relayRunning;
    private DateTime _relayStart;
    private Process? _vcProcess;
    private VRChatWebSocketService? _wsService;

    // VRChat process monitor + startup-app tracking (Close / Start always with VRChat)
    private System.Threading.Timer? _vrcMonitorTimer;
    private int      _vrcSessionPid;
    private DateTime _vrcSessionProcStart = DateTime.MinValue;
    private DateTime _vrcSessionFirstSeen;
    private bool     _vrcSessionByUs;
    private bool     _vrcSessionVr;
    private bool     _vrcSessionModeResolved;
    private bool     _vrcSessionHandled;
    private (bool vr, DateTime at, int priorPid)? _pendingLaunch;
    private int _vrcCheckBusy;
    private readonly Dictionary<string, DateTime> _launchedExeNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _launchedLock = new();

    // Sleep/wake detection + resume retry
    private System.Threading.Timer? _wakeTimer;
    private DateTime _lastWakeTick = DateTime.UtcNow;
    private int _resumeRetryCount;
    private const int WakeThresholdSec = 20;
    private const int MaxResumeRetries = 5;

    // Callback set by AuthController so RelayController can trigger re-auth without circular dep
    public Func<Task>? OnWakeResumeRequested { get; set; }

    public Action? OnAuthExpired { get; set; }

    // Public Accessors
    public bool IsRunning => _relayRunning;
    public DateTime RelayStart => _relayStart;
    public bool IsVcRunning => _vcProcess != null && !_vcProcess.HasExited;

    // Cross-domain callback (set by MainForm)
    public Action<JObject>? OnOwnUserUpdated { get; set; }

    public RelayController(
        CoreLibrary core,
        FriendsController friends,
        InstanceController instance,
        NotificationsController notifications,
        VROverlayController vroCtrl)
    {
        _core = core;
        _friends = friends;
        _instance = instance;
        _notifications = notifications;
        _vroCtrl = vroCtrl;

        // Timer fires every 10s; if actual elapsed > WakeThresholdSec the PC just woke from sleep
        _wakeTimer = new System.Threading.Timer(_ => CheckForWake(), null,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        // Monitor VRChat process to drive "Close with VRChat" / "Start always with VRChat"
        var (seedPid, seedStart) = GetVrcProcInfo();
        if (seedPid != 0)
        {
            _vrcSessionPid          = seedPid;
            _vrcSessionProcStart    = seedStart;
            _vrcSessionFirstSeen    = DateTime.UtcNow;
            _vrcSessionVr           = IsSteamVrRunning();
            _vrcSessionModeResolved = true;
            _vrcSessionHandled      = true;
        }
        _vrcMonitorTimer = new System.Threading.Timer(_ => CheckVrcState(), null,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    private void CheckForWake()
    {
        var now  = DateTime.UtcNow;
        var gap  = (now - _lastWakeTick).TotalSeconds;
        _lastWakeTick = now;

        if (gap < WakeThresholdSec) return;

        _core.SendToJS("log", new { msg = $"[Wake] PC resumed from sleep (~{(int)gap}s gap) — reconnecting...", color = "sec" });

        // Restart WebSocket unconditionally — existing connection is dead
        if (_core.VrcApi.IsLoggedIn)
        {
            StartWebSocket();
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000); // give network a moment
                if (!_core.VrcApi.IsLoggedIn) return;
                await _friends.RefreshFriendsAsync(true);
            });
        }
        else if (!string.IsNullOrEmpty(_core.Settings.ActiveAccount?.AuthCookie))
        {
            // Not logged in yet (startup resume failed) — retry now that network is up
            _resumeRetryCount = 0;
            _ = Task.Run(async () => { await Task.Delay(3000); await TryResumeWithRetryAsync(); });
        }
    }

    public void ScheduleResumeRetry()
    {
        _resumeRetryCount = 0;
        _ = Task.Run(async () => { await Task.Delay(8000); await TryResumeWithRetryAsync(); });
    }

    private async Task TryResumeWithRetryAsync()
    {
        if (_resumeRetryCount >= MaxResumeRetries) return;
        if (OnWakeResumeRequested == null) return;

        _resumeRetryCount++;
        _core.SendToJS("log", new { msg = $"[Wake] Resume attempt {_resumeRetryCount}/{MaxResumeRetries}...", color = "sec" });

        try { await OnWakeResumeRequested(); }
        catch { }
    }

    // Message Handler

    public void HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "startRelay":
                StartRelay();
                break;
            case "stopRelay":
                StopRelay();
                break;
            case "playVRChat":
            {
                var pvRunning = IsVrcRunning();
                var pvLoc = pvRunning ? (_core.LogWatcher?.CurrentLocation ?? "") : "";
                _core.SendToJS("vrcLaunchNeeded", new { location = pvLoc, steamVr = IsSteamVrRunning() });
                break;
            }
            case "vcCheck":
                _core.SendToJS("vcState", GetVcState());
                break;
            case "vcInstall":
                _ = InstallVcAsync();
                break;
            case "vcStart":
                StartVcProcess();
                break;
            case "vcStop":
                StopVcProcess();
                break;
            case "vrcLaunchAndJoin":
                {
                    var llLoc = msg["location"]?.ToString() ?? "";
                    var llVr  = msg["vr"]?.Value<bool>() ?? false;
#if WINDOWS
                    // Build args: --no-vr for Desktop mode, optional join URI, plus user-configured launch args
                    var joinUri2 = !string.IsNullOrEmpty(llLoc) ? VRChatApiService.BuildLaunchUri(llLoc) : "";
                    var noVrFlag = llVr ? "" : "--no-vr";
                    var llExtraArgs = (_core.Settings.VrcLaunchArgs ?? "").Trim();
                    var llProfileArg = GetVrcProfileArg();

                    // Always prefer steam.exe -applaunch so --no-vr is passed correctly.
                    // steam:// URL does NOT forward args to VRChat, causing SteamVR to open in Desktop mode.
                    var steamExe = FindSteamExe();
                    bool llLaunched = false;
                    if (steamExe != null)
                    {
                        var applaunchArgs = string.IsNullOrEmpty(joinUri2)
                            ? $"-applaunch 438100 {noVrFlag} {llProfileArg} {llExtraArgs}".Trim()
                            : $"-applaunch 438100 {noVrFlag} {llProfileArg} {llExtraArgs} \"{joinUri2}\"".Trim();
                        applaunchArgs = System.Text.RegularExpressions.Regex.Replace(applaunchArgs, "\\s+", " ");
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = steamExe, Arguments = applaunchArgs,
                                UseShellExecute = false
                            });
                            llLaunched = true;
                        }
                        catch { }
                    }
                    if (!llLaunched)
                    {
                        // Fallback: direct exe path (user-configured)
                        var vrcExe = _core.Settings.VrcPath;
                        if (!string.IsNullOrWhiteSpace(vrcExe) && File.Exists(vrcExe))
                        {
                            var llArgs = string.IsNullOrEmpty(joinUri2)
                                ? $"{noVrFlag} {llProfileArg} {llExtraArgs}".Trim()
                                : $"{noVrFlag} {llProfileArg} {llExtraArgs} \"{joinUri2}\"".Trim();
                            llArgs = System.Text.RegularExpressions.Regex.Replace(llArgs, "\\s+", " ");
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = vrcExe, Arguments = llArgs,
                                WorkingDirectory = Path.GetDirectoryName(vrcExe) ?? "",
                                UseShellExecute = false
                            });
                        }
                        else
                        {
                            _core.SendToJS("vrcActionResult", new { action = "join", success = false,
                                message = "Could not launch VRChat: Steam not found and no exe path configured." });
                            _core.SendToJS("log", new { msg = "Launch failed: steam.exe not found and VrcPath not set.", color = "err" });
                            break;
                        }
                    }
#else
                    // On Linux, launch via Steam so Proton is applied automatically
                    var joinUriLnx   = !string.IsNullOrEmpty(llLoc) ? VRChatApiService.BuildLaunchUri(llLoc) : "";
                    var noVrFlagLnx  = llVr ? "" : "--no-vr";
                    var extraArgsLnx = (_core.Settings.VrcLaunchArgs ?? "").Trim();
                    var profileLnx   = GetVrcProfileArg();
                    var applaunchLnx = System.Text.RegularExpressions.Regex.Replace(
                        string.IsNullOrEmpty(joinUriLnx)
                            ? $"-applaunch 438100 {noVrFlagLnx} {profileLnx} {extraArgsLnx}"
                            : $"-applaunch 438100 {noVrFlagLnx} {profileLnx} {extraArgsLnx} \"{joinUriLnx}\"",
                        "\\s+", " ").Trim();
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "steam",
                            Arguments = applaunchLnx,
                            UseShellExecute = false
                        });
                    }
                    catch
                    {
                        var steamUrl = string.IsNullOrEmpty(joinUriLnx)
                            ? "steam://rungameid/438100"
                            : $"steam://rungameid/438100//{Uri.EscapeDataString(joinUriLnx)}";
                        Process.Start(new ProcessStartInfo { FileName = steamUrl, UseShellExecute = true });
                    }
#endif
                    int plPriorPid = 0;
                    try { plPriorPid = GetVrcProcInfo().pid; } catch { }
                    _pendingLaunch = (llVr, DateTime.UtcNow, plPriorPid);

                    var modeLabel = llVr ? "VR" : "Desktop";
                    var locLabel  = !string.IsNullOrEmpty(llLoc) ? $" → {llLoc}" : "";
                    _core.SendToJS("vrcActionResult", new { action = "join", success = true, message = $"Launching VRChat ({modeLabel})..." });
                    _core.SendToJS("log", new { msg = $"Launched VRChat [{modeLabel}]{locLabel}", color = "ok" });
                    _core.SendToJS("vrcLaunched", new { vr = llVr });
                }
                break;
        }
    }

    // Relay Control

    private void StartRelay()
    {
        var allFolders = _core.Settings.WatchFolders.Where(Directory.Exists).ToList();
        var enabledFilter = _core.Settings.RelayEnabledFolders;
        var folders = enabledFilter == null
            ? allFolders
            : allFolders.Where(f => enabledFilter.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
        if (folders.Count == 0)
        {
            _core.SendToJS("log", new { msg = "No valid watch folders configured!", color = "err" });
            return;
        }
        var whs = _core.Settings.Webhooks.Where(w => w.Enabled && !string.IsNullOrWhiteSpace(w.Url)).ToList();
        if (whs.Count == 0)
        {
            _core.SendToJS("log", new { msg = "No webhooks active!", color = "err" });
            return;
        }

        _core.FileWatcher.Start(folders);
        _relayRunning = true;
        _relayStart = DateTime.Now;
        _vroCtrl.UpdateToolStates();

        _core.SendToJS("relayState", new { running = true, streams = whs.Count });
        _core.SendToJS("log", new { msg = "Relay started successfully", color = "ok" });
        foreach (var f in folders)
            _core.SendToJS("log", new { msg = $"  Watching: {f}", color = "sec" });
        foreach (var w in whs)
            _core.SendToJS("log", new { msg = $"  Webhook: {w.Name}", color = "accent" });
    }

    private void StopRelay()
    {
        _core.FileWatcher.Stop();
        _relayRunning = false;
        _vroCtrl.UpdateToolStates();

        _core.SendToJS("relayState", new { running = false, streams = 0 });
        _core.SendToJS("log", new { msg = "Relay stopped", color = "warn" });
    }

    // Toggle (called from VR overlay)

    public void ToggleRelay()
    {
        if (_relayRunning) StopRelay();
        else               StartRelay();
    }

    // VRCVideoCacher

    public static readonly string VcExePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "Tools", "VRCVideoCacher", "VRCVideoCacher.exe");

    private object GetVcState()
    {
        bool installed = File.Exists(VcExePath);
        bool running   = _vcProcess != null && !_vcProcess.HasExited;
        return new { installed, running };
    }

    private void StartVcProcess()
    {
        if (!File.Exists(VcExePath)) return;
        if (_vcProcess != null && !_vcProcess.HasExited) return;

        try
        {
            _vcProcess = Process.Start(new ProcessStartInfo
            {
                FileName         = VcExePath,
                WorkingDirectory = Path.GetDirectoryName(VcExePath)!,
                UseShellExecute  = false,
                CreateNoWindow   = false,
            })!;
            _vcProcess.EnableRaisingEvents = true;
            _vcProcess.Exited += (_, _) =>
            {
                _vcProcess = null;
                try { Invoke(() => { _core.SendToJS("vcState", GetVcState()); _vroCtrl.UpdateToolStates(); }); } catch { }
            };
            _core.SendToJS("vcState", GetVcState());
            _vroCtrl.UpdateToolStates();
        }
        catch { }
    }

    private void StopVcProcess()
    {
        try { _vcProcess?.Kill(entireProcessTree: true); } catch { }
        _vcProcess = null;
        _core.SendToJS("vcState", GetVcState());
        _vroCtrl.UpdateToolStates();
    }

    private async Task InstallVcAsync()
    {
        try
        {
            Invoke(() => _core.SendToJS("vcState", new { installed = false, running = false, downloading = true, progress = 0 }));

            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent);

            var apiResp = await http.GetAsync("https://api.github.com/repos/EllyVR/VRCVideoCacher/releases/latest");
            if (!apiResp.IsSuccessStatusCode)
            {
                Invoke(() => _core.SendToJS("vcState", new { installed = false, running = false, error = $"GitHub API: HTTP {(int)apiResp.StatusCode}" }));
                return;
            }

            var json     = JObject.Parse(await apiResp.Content.ReadAsStringAsync());
            var version  = json["tag_name"]?.ToString() ?? "?";
            var assets   = json["assets"] as JArray;
            var exeAsset = assets?.FirstOrDefault(a => a["name"]?.ToString().EndsWith(".exe") == true);
            var dlUrl    = exeAsset?["browser_download_url"]?.ToString();

            if (string.IsNullOrEmpty(dlUrl))
            {
                Invoke(() => _core.SendToJS("vcState", new { installed = false, running = false, error = "No .exe asset in latest release" }));
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(VcExePath)!);

            using var dlResp = await http.GetAsync(dlUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            var total = dlResp.Content.Headers.ContentLength ?? -1L;
            await using var stream = await dlResp.Content.ReadAsStreamAsync();
            await using var fs     = File.Create(VcExePath);

            var buf        = new byte[65536];
            long downloaded = 0;
            int  read;
            while ((read = await stream.ReadAsync(buf)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, read));
                downloaded += read;
                if (total > 0)
                {
                    int pct = (int)(downloaded * 100 / total);
                    try { Invoke(() => _core.SendToJS("vcState", new { installed = false, running = false, downloading = true, progress = pct })); } catch { }
                }
            }

            Invoke(() =>
            {
                _core.SendToJS("vcState", GetVcState());
            });
        }
        catch (Exception ex)
        {
            try { Invoke(() => _core.SendToJS("vcState", new { installed = false, running = false, error = ex.Message })); } catch { }
        }
    }

    // Launch VRChat + extra apps

    private string GetVrcProfileArg()
    {
        var acc = _core.Settings.ActiveAccount;
        if (acc == null || acc.IsPrimary) return "";
        var extra = _core.Settings.VrcLaunchArgs ?? "";
        if (extra.IndexOf("--profile", StringComparison.OrdinalIgnoreCase) >= 0) return "";
        var before = acc.ProfileIndex;
        var idx = _core.Settings.EnsureProfileIndex(acc);
        if (idx != before) _core.Settings.Save();
        return $"--profile={idx}";
    }

    // Launches the configured startup apps and remembers them so they can be
    // closed again when "Close with VRChat" is enabled. Handles .exe, .lnk
    // shortcuts and .bat/.cmd scripts.
    //
    // Apps are launched through explorer.exe so they run detached, exactly like
    // a user double-click. Launching an app as a direct child process makes
    // Electron/Chromium apps (e.g. SlimeVR) exit immediately, while native apps
    // survive — which is why a direct Process.Start started Voicemeeter but not
    // SlimeVR.
    private void LaunchExtraApps(IEnumerable<string> apps, bool log, bool vr)
    {
        foreach (var raw in apps)
        {
            var path = (raw ?? "").Trim().Trim('"');
            if (string.IsNullOrEmpty(path)) continue;

            if (!File.Exists(path))
            {
                _core.SendToJS("log", new { msg = $"Startup app not found: {path}", color = "warn" });
                continue;
            }

            try
            {
#if WINDOWS
                var launchPath = path;
                if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var lnk = AutoStartShortcutHelper.EnsureShortcut(path, vr);
                    if (!string.IsNullOrEmpty(lnk) && File.Exists(lnk)) launchPath = lnk;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{launchPath}\"",
                    UseShellExecute = false
                });
#else
                bool isExecutable = false;
                try { isExecutable = (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0; } catch { }
                if (isExecutable)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                        UseShellExecute = false
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true
                    });
                }
#endif
                var exeName = ResolveTargetExeName(path);
                if (!string.IsNullOrEmpty(exeName))
                    lock (_launchedLock)
                    {
                        if (!_launchedExeNames.ContainsKey(exeName))
                            _launchedExeNames[exeName] = DateTime.Now;
                    }

                if (log) _core.SendToJS("log", new { msg = $"Launched: {Path.GetFileName(path)}", color = "ok" });
            }
            catch (Exception ex)
            {
                _core.SendToJS("log", new { msg = $"Failed to launch {Path.GetFileName(path)}: {ex.Message}", color = "err" });
            }
        }
    }

    // Maps a configured startup-app path to the process name that will be
    // running (so Close-with-VRChat can find and stop it). Resolves .lnk
    // shortcuts to their target executable.
    private static string ResolveTargetExeName(string path)
    {
        try
        {
#if WINDOWS
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".exe")
                return Path.GetFileNameWithoutExtension(path);
            if (ext == ".lnk")
            {
                var target = ResolveShortcutTarget(path);
                if (!string.IsNullOrEmpty(target) && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return Path.GetFileNameWithoutExtension(target);
            }
#else
            return Path.GetFileNameWithoutExtension(path);
#endif
        }
        catch { }
        return "";
    }

#if WINDOWS
    private static string ResolveShortcutTarget(string lnkPath)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return "";
            dynamic? shell = Activator.CreateInstance(t);
            if (shell == null) return "";
            dynamic sc = shell.CreateShortcut(lnkPath);
            string target = sc.TargetPath as string ?? "";
            try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sc); } catch { }
            try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); } catch { }
            return target;
        }
        catch { return ""; }
    }
#endif

    // VRChat process monitor (Close / Start always with VRChat)

    private const int VrDetectGraceSec = 15;

    private void CheckVrcState()
    {
        if (System.Threading.Interlocked.Exchange(ref _vrcCheckBusy, 1) == 1) return;
        try
        {
            int pid; DateTime procStart;
            try { (pid, procStart) = GetVrcProcInfo(); }
            catch { return; }

            bool changed = pid != _vrcSessionPid
                || (pid != 0 && _vrcSessionProcStart != DateTime.MinValue
                    && procStart != DateTime.MinValue && procStart != _vrcSessionProcStart);

            if (changed)
            {
                if (_vrcSessionPid != 0) EndVrcSession();
                if (pid != 0) BeginVrcSession(pid, procStart);
            }

            if (_vrcSessionPid != 0 && !_vrcSessionHandled)
                TryHandleVrcSessionStart();

            var startUtc = _vrcSessionProcStart != DateTime.MinValue
                ? _vrcSessionProcStart.ToUniversalTime()
                : _vrcSessionFirstSeen;
            _core.VrcSessions.Tick(_vrcSessionPid, startUtc);
        }
        finally { System.Threading.Interlocked.Exchange(ref _vrcCheckBusy, 0); }
    }

    private void BeginVrcSession(int pid, DateTime procStart)
    {
        _vrcSessionPid          = pid;
        _vrcSessionProcStart    = procStart;
        _vrcSessionFirstSeen    = DateTime.UtcNow;
        _vrcSessionHandled      = false;
        _vrcSessionModeResolved = false;
        _vrcSessionByUs         = false;
        _vrcSessionVr           = false;

        if (_pendingLaunch is { } pl)
        {
            bool fresh = (DateTime.UtcNow - pl.at).TotalSeconds < 180;
            if (fresh && pl.priorPid != pid)
            {
                _vrcSessionByUs         = true;
                _vrcSessionVr           = pl.vr;
                _vrcSessionModeResolved = true;
            }
            _pendingLaunch = null;
        }

        Invoke(() => _core.SendToJS("vrcRunningChanged", new { running = true }));
    }

    private void EndVrcSession()
    {
        _vrcSessionPid          = 0;
        _vrcSessionProcStart    = DateTime.MinValue;
        _vrcSessionHandled      = false;
        _vrcSessionModeResolved = false;

        _core.VrcSessions.End(DateTime.UtcNow);
        Invoke(() => _core.SendToJS("vrcRunningChanged", new { running = false }));

        if (_core.Settings.CloseWithVrc)
        {
            CloseTrackedExtraApps();
            Invoke(() => _core.SendToJS("vrcClosed", new { }));
        }
    }

    private void TryHandleVrcSessionStart()
    {
        if (!_vrcSessionModeResolved)
        {
            if (IsSteamVrRunning())
            {
                _vrcSessionVr = true;
                _vrcSessionModeResolved = true;
            }
            else if ((DateTime.UtcNow - _vrcSessionFirstSeen).TotalSeconds >= VrDetectGraceSec)
            {
                _vrcSessionVr = false;
                _vrcSessionModeResolved = true;
            }
            else return;
        }

        _vrcSessionHandled = true;
        bool vr    = _vrcSessionVr;
        bool byUs  = _vrcSessionByUs;
        bool launchApps = byUs || _core.Settings.StartAlwaysWithVrc;
        var apps = vr ? _core.Settings.ExtraExeVR : _core.Settings.ExtraExeDesktop;

        Invoke(() =>
        {
            _core.SendToJS("log", new { msg = $"VRChat session detected ({(vr ? "VR" : "Desktop")}{(byUs ? ", launched by VRCNext" : "")}) — running auto-start...", color = "sec" });
            if (launchApps) LaunchExtraApps(apps, log: true, vr: vr);
            _core.SendToJS("vrcLaunched", new { vr });
        });
    }

    private void CloseTrackedExtraApps()
    {
        List<KeyValuePair<string, DateTime>> tracked;
        lock (_launchedLock)
        {
            tracked = _launchedExeNames.ToList();
            _launchedExeNames.Clear();
        }
        int closed = 0;
        foreach (var kv in tracked)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(kv.Key))
                {
                    try
                    {
                        if (p.HasExited) continue;
                        bool ours = true;
                        try { ours = p.StartTime >= kv.Value.AddSeconds(-5); } catch { }
                        if (!ours) continue;
                        p.Kill(entireProcessTree: true);
                        closed++;
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
        }
        if (closed > 0)
            Invoke(() => _core.SendToJS("log", new { msg = $"VRChat closed — closed {closed} startup app process(es).", color = "sec" }));
    }

    // Find steam.exe via registry (Windows only)

#if WINDOWS
    private static string? FindSteamExe()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                         ?? Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
            var installPath = key?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(installPath))
            {
                var exe = Path.Combine(installPath, "steam.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch { }
        // Fallback: common default paths
        foreach (var candidate in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steam.exe"),
            @"C:\Program Files\Steam\steam.exe"
        })
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
#endif

    // Static process checks

    public static bool IsVrcRunning()
    {
        try
        {
            bool Alive(Process p) { try { return !p.HasExited; } catch { return false; } }
            if (Process.GetProcessesByName("VRChat").Any(Alive)) return true;
#if !WINDOWS
            if (Process.GetProcessesByName("VRChat.exe").Any(Alive)) return true;
#endif
            return false;
        }
        catch { return false; }
    }

    public static bool IsSteamVrRunning()
    {
        try { return Process.GetProcessesByName("vrserver").Any(p => { try { return !p.HasExited; } catch { return false; } }); }
        catch { return false; }
    }

    private static (int pid, DateTime started) GetVrcProcInfo()
    {
        try
        {
            int bestPid = 0;
            var bestStart = DateTime.MinValue;
            void Scan(string name)
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (p.HasExited) continue;
                        DateTime st;
                        try { st = p.StartTime; } catch { st = DateTime.MinValue; }
                        if (bestPid == 0 || (st != DateTime.MinValue && (bestStart == DateTime.MinValue || st < bestStart)))
                        {
                            bestPid = p.Id;
                            bestStart = st;
                        }
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            Scan("VRChat");
#if !WINDOWS
            Scan("VRChat.exe");
#endif
            return (bestPid, bestStart);
        }
        catch { return (0, DateTime.MinValue); }
    }

    // WebSocket lifecycle

    public void StopWebSocket()
    {
        _wsService?.Stop();
    }

    public void StartWebSocket()
    {
        var (auth, tfa) = _core.VrcApi.GetCookies();
        if (string.IsNullOrEmpty(auth)) return;

        _wsService?.Dispose();
        _wsService = new VRChatWebSocketService();

        // Wire all friend-related WebSocket events to FriendsController
        _friends.WireWebSocket(_wsService);

        // Wire all notification-related WebSocket events to NotificationsController
        _notifications.WireWebSocket(_wsService);

        _wsService.AuthExpiredSuspected += (_, _) => Invoke(() => OnAuthExpired?.Invoke());

        // Small delay so the VRC API reflects the new location before we query it
        _wsService.OwnLocationChanged += (_, _) =>
        {
            if (_core.VrcApi.IsLoggedIn)
                _ = Task.Delay(3000).ContinueWith(_ => _instance.GetCurrentInstanceAsync());
        };

        // user-update: own profile changed in-game (status, bio, statusDescription, tags, icon…)
        // The WS payload is partial (no pronouns/bioLinks), so fetch the full user from REST
        // then push everything to JS in one shot — sidebar, modal, Discord presence all update.
        _wsService.OwnUserUpdated += (_, _) =>
        {
            if (!_core.VrcApi.IsLoggedIn) return;
            _ = Task.Run(async () =>
            {
                var prevAvatar = _core.VrcApi.CurrentAvatarId ?? "";
                var full = await _core.Auth.RefreshCurrentUserAsync();
                if (full != null)
                {
                    var newAvatar = full["currentAvatar"]?.ToString() ?? "";
                    if (_core.Settings.VrcndbSyncWears && !string.IsNullOrEmpty(prevAvatar)
                        && !string.IsNullOrEmpty(newAvatar) && newAvatar != prevAvatar)
                        PopularityReporter.Report(newAvatar, "client", "wear");
                    Invoke(() => OnOwnUserUpdated?.Invoke(full));
                }
            });
        };

        // All log calls must use Invoke(); these fire on the WebSocket background thread
        _wsService.Connected += (_, _) =>
        {
            Invoke(() =>
            {
                _core.SendToJS("wsStatus", new { connected = true });
                _core.SendToJS("log", new { msg = "[WS] Connected to pipeline.vrchat.cloud", color = "ok" });
            });
        };

        _wsService.Disconnected += (_, _) =>
        {
            Invoke(() =>
            {
                _core.SendToJS("wsStatus", new { connected = false });
                _core.SendToJS("log", new { msg = "[WS] Disconnected — reconnecting...", color = "warn" });
            });
        };

        // Real connection failure (not watchdog idle-timeout "No data for Xs") → one REST fallback.
        // Watchdog aborts the socket after 75s of silence and reconnects automatically — no REST needed.
        _wsService.ConnectError += (_, err) =>
        {
            Invoke(() => _core.SendToJS("log", new { msg = $"[WS] Error: {err}", color = "err" }));
            if (!err.StartsWith("No data for") && _core.VrcApi.IsLoggedIn)
            {
                _ = _friends.RefreshFriendsAsync(true);
                _ = _notifications.GetNotificationsAsync();
            }
        };

        // Pass a delegate so the service fetches fresh cookies on every internal reconnect
        _wsService.Start(auth, tfa ?? "", () =>
        {
            var (a, t) = _core.VrcApi.GetCookies();
            return (a ?? "", t ?? "");
        });
    }

    // Disposal

    public void Dispose()
    {
        _wakeTimer?.Dispose();
        _wakeTimer = null;
        _vrcMonitorTimer?.Dispose();
        _vrcMonitorTimer = null;
        try { _vcProcess?.Kill(entireProcessTree: true); } catch { }
        _wsService?.Dispose();
    }

    // Photino compatibility shim
    private static void Invoke(Action action) => action();
}
