#if WINDOWS
using System.Runtime.InteropServices;
#endif
using Newtonsoft.Json.Linq;

namespace VRCNext;

// Owns all window chrome (borderless frame), P/Invoke subclassing, and window action messages.

public class WindowController
{
    private readonly CoreLibrary _core;

    public WindowController(CoreLibrary core)
    {
        _core = core;
    }

#if WINDOWS
    // P/Invoke declarations
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern int SendMessage(nint hWnd, int Msg, int wParam, int lParam);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
    // comctl32 proper subclassing API — safe under nested message loops and window teardown
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hWnd, SUBCLASSPROC pfn, nuint uId, nuint dwRefData);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT lpwndpl);

    private const int SW_HIDE           = 0;
    private const int SW_SHOW           = 5;
    private const int SW_RESTORE        = 9;
    private const int SW_SHOWMAXIMIZED  = 3;

    /// <summary>
    /// When true, minimize actions hide the window to the system tray instead.
    /// No window style changes — animations and taskbar behaviour stay native.
    /// </summary>
    private static volatile bool _minimizeToTray;
    private static volatile bool _forceClose;

    public static void AllowNextClose() => _forceClose = true;
#else
    public static void AllowNextClose() { }
#endif
#if WINDOWS

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int leftWidth, rightWidth, topHeight, bottomHeight; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int   length;
        public int   flags;
        public int   showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT  rcNormalPosition;
    }

    // Set by SubclassWndProc at WM_CLOSE (before the native window is destroyed).
    // AppShell.OnClose() reads this instead of _window.Width/Height/Left/Top which return 0 after WaitForClose().
    internal static (int Left, int Top, int Width, int Height, bool WasMaximized) LastWindowPlacement;

    private delegate nint SUBCLASSPROC(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);
    private static SUBCLASSPROC? _subclassProc; // must stay rooted — prevents GC collection of the delegate
    private static volatile bool _ncDestroyed;  // set on WM_NCDESTROY; guards re-entrant calls and post-destroy messages
    private const nuint SubclassId = 1;
    internal static volatile Action? OnMinimized; // set from AppShell; called on any minimize/hide-to-tray

    private static nint SubclassWndProc(nint hWnd, uint msg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
    {
        const uint WM_CLOSE      = 0x0010;
        const uint WM_DESTROY    = 0x0002;
        const uint WM_SIZE       = 0x0005;
        const uint WM_NCDESTROY  = 0x0082;
        const uint WM_NCCALCSIZE = 0x0083;
        const uint WM_NCHITTEST  = 0x0084;
        const uint WM_SYSCOMMAND = 0x0112;
        const int  SC_MINIMIZE   = 0xF020;

        if (msg == WM_NCDESTROY)
        {
            _subclassProc = null;
            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        if (_ncDestroyed)
            return 0;

        if (msg == WM_CLOSE)
        {
            if (_forceClose) { _forceClose = false; }
            else if (_minimizeToTray)
            {
                ShowWindow(hWnd, SW_HIDE);
                OnMinimized?.Invoke();
                return 0;
            }
            // Window will actually close — capture geometry now while the HWND is still valid.
            // _window.Width/Height/Left/Top return 0 after WaitForClose() because the native window is gone.
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (GetWindowPlacement(hWnd, ref wp))
            {
                LastWindowPlacement = (
                    wp.rcNormalPosition.Left,
                    wp.rcNormalPosition.Top,
                    wp.rcNormalPosition.Right  - wp.rcNormalPosition.Left,
                    wp.rcNormalPosition.Bottom - wp.rcNormalPosition.Top,
                    wp.showCmd == SW_SHOWMAXIMIZED
                );
            }
        }

        if (msg == WM_DESTROY)
        {
            _ncDestroyed = true;
            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        if (msg == WM_SIZE && wParam == 1 /*SIZE_MINIMIZED*/)
        {
            OnMinimized?.Invoke();
            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        if (msg == WM_NCCALCSIZE && wParam == 1)
            return 0;

        if (msg == WM_SYSCOMMAND && _minimizeToTray)
        {
            var sc = wParam.ToInt32() & 0xFFF0;
            if (sc == SC_MINIMIZE || sc == 0xF060 /*SC_CLOSE*/)
            {
                ShowWindow(hWnd, SW_HIDE);
                OnMinimized?.Invoke();
                return 0;
            }
        }

        if (msg == WM_NCHITTEST)
        {
            var hit = DefSubclassProc(hWnd, msg, wParam, lParam);
            if (hit == 1 /*HTCLIENT*/)
            {
                const int border = 8;
                int x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
                int y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
                GetWindowRect(hWnd, out var rc);
                bool l = x < rc.Left   + border, r = x > rc.Right  - border;
                bool t = y < rc.Top    + border, b = y > rc.Bottom - border;
                if (t && l) return 13; if (t && r) return 14;
                if (b && l) return 16; if (b && r) return 17;
                if (t) return 12; if (b) return 15;
                if (l) return 10; if (r) return 11;
            }
            return hit;
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void InstallWndProcSubclass(nint hWnd)
    {
        if (_subclassProc != null) return; // already installed
        _ncDestroyed = false;
        _subclassProc = SubclassWndProc;
        SetWindowSubclass(hWnd, _subclassProc, SubclassId, 0);
    }

    /// <summary>
    /// Enables or disables "minimize to tray" mode.
    /// Does NOT change any window styles — just sets a flag.
    /// When active, minimize actions (JS button, Win+D, etc.) hide the window via SW_HIDE.
    /// </summary>
    public void SetHideFromTaskbar(bool hide)
    {
        _minimizeToTray = hide;
    }

    /// <summary>
    /// Called by the Photino WindowClosingHandler. If minimize-to-tray is active,
    /// hides the window and returns true (cancel close). Otherwise returns false (allow close).
    /// </summary>
    /// <summary>
    /// Unconditionally hides the window (SW_HIDE). Used on startup to auto-hide to tray.
    /// </summary>
    public void HideWindow()
    {
        var window = _core.Window;
        if (window == null) return;
        ShowWindow(window.WindowHandle, SW_HIDE);
    }

    /// <summary>
    /// Toggles the Photino window visibility (used by tray icon left-click).
    /// </summary>
    public void ToggleWindowVisibility()
    {
        var window = _core.Window;
        if (window == null) return;
        var hWnd = window.WindowHandle;
        if (IsIconic(hWnd))
        {
            ShowWindow(hWnd, SW_RESTORE);
            SetForegroundWindow(hWnd);
        }
        else if (!IsWindowVisible(hWnd))
        {
            ShowWindow(hWnd, SW_SHOW);
            SetForegroundWindow(hWnd);
        }
        else
        {
            ShowWindow(hWnd, SW_HIDE);
        }
    }

    public void BringToFront()
    {
        var window = _core.Window;
        if (window == null) return;
        var hWnd = window.WindowHandle;
        if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
        else if (!IsWindowVisible(hWnd)) ShowWindow(hWnd, SW_SHOW);
        SetForegroundWindow(hWnd);
    }
#endif

    /// <summary>
    /// Sets the native title bar background color via DWM (Windows 11+).
    /// hex must be in #RRGGBB format. No-op on unsupported OS versions.
    /// </summary>
    // Install Chrome (called from "ready" message)

    public void InstallChrome()
    {
#if WINDOWS
        var window = _core.Window;
        if (window == null) return;
        var hWnd = window.WindowHandle;
        // Subclass FIRST — WM_NCCALCSIZE handler must be active before SWP_FRAMECHANGED
        // so the title bar is never rendered even for a single frame
        InstallWndProcSubclass(hWnd);
        // Full OVERLAPPEDWINDOW styles → snap/tile/Win11 snap layouts all work
        const int GWL_STYLE      = -16;
        const int WS_THICKFRAME  = 0x00040000;
        const int WS_CAPTION     = 0x00C00000;
        const int WS_SYSMENU     = 0x00080000;
        const int WS_MINIMIZEBOX = 0x00020000;
        const int WS_MAXIMIZEBOX = 0x00010000;
        SetWindowLong(hWnd, GWL_STYLE,
            GetWindowLong(hWnd, GWL_STYLE) | WS_THICKFRAME | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        // SWP_FRAMECHANGED triggers WM_NCCALCSIZE — subclass returns 0 → no NC area drawn
        SetWindowPos(hWnd, 0, 0, 0, 0, 0, 0x0020 | 0x0001 | 0x0002 | 0x0004);
        // Win11 rounded corners
        int cornerPref = 2;
        _ = DwmSetWindowAttribute(hWnd, 33, ref cornerPref, 4);
#endif
    }

    /// <summary>Re-applies the maximized window state saved on the previous shutdown.</summary>
    public void RestoreMaximizedState()
    {
        if (_maximizedRestored) return;
        _maximizedRestored = true;
        var window = _core.Window;
        if (window == null) return;
        if (!_core.Settings.RememberWindowSize || !_core.Settings.SavedWindowMaximized) return;
        try
        {
            if (!window.Maximized) window.SetMaximized(true);
            _core.SendToJS("windowMaxState", true);
        }
        catch { }
    }

    private bool _maximizedRestored;

    // GetCenteredLocation (used by Run())

    public static (int x, int y) GetCenteredLocation(int w, int h)
    {
#if WINDOWS
        return (Math.Max(0, (GetSystemMetrics(0) - w) / 2), Math.Max(0, (GetSystemMetrics(1) - h) / 2));
#else
        return (100, 50);
#endif
    }

#if WINDOWS
    private readonly Dictionary<int, Photino.NET.PhotinoSurface> _wmSurfaces = new();

    private void HandleSurfaceMessage(string action, JObject msg, Photino.NET.PhotinoWindow window)
    {
        int wmId = (int?)msg["wmId"] ?? 0;
        if (action == "wmSurfaceOpen")
        {
            if (!window.CompositionHosting)
            {
                _core.SendToJS("wmSurfaceCreated", new { wmId, surfaceId = 0 });
                return;
            }
            try
            {
                int width  = Math.Clamp((int?)msg["width"]  ?? 760, 560, 2400);
                int height = Math.Clamp((int?)msg["height"] ?? 600, 360, 1600);
                var mainPos = window.Location;
                int offset = 60 + (_wmSurfaces.Count % 6) * 28;
                int nearWmId = (int?)msg["nearWmId"] ?? 0;
                if (nearWmId != 0 && _wmSurfaces.TryGetValue(nearWmId, out var near) && !near.IsClosed)
                {
                    mainPos = near.Location;
                    offset = 40;
                }
                var surface = window.CreateSurface(new Photino.NET.PhotinoSurfaceOptions
                {
                    Title = msg["title"]?.ToString() ?? "VRCNext",
                    Width = width,
                    Height = height,
                    MinWidth = 560,
                    MinHeight = 360,
                    Location = new System.Drawing.Point(mainPos.X + offset, mainPos.Y + offset),
                    Chromeless = true,
                    Resizable = true,
                    Owned = false,
                    SharedFocus = true,
                    FollowOwnerVisibility = true,
                    Visible = false,
                });
                _wmSurfaces[wmId] = surface;
                surface.Closed += (_, _) =>
                {
                    _wmSurfaces.Remove(wmId);
                    _core.SendToJS("wmSurfaceClosed", new { wmId });
                };
                _core.SendToJS("wmSurfaceCreated", new { wmId, surfaceId = surface.Id });
            }
            catch (Exception ex)
            {
                VRCNext.Services.CrashHandler.WriteEntry("wmSurfaceOpen", ex);
                _core.SendToJS("wmSurfaceCreated", new { wmId, surfaceId = 0 });
            }
            return;
        }

        if (!_wmSurfaces.TryGetValue(wmId, out var s) || s.IsClosed) return;
        switch (action)
        {
            case "wmSurfaceClose":
                s.Close();
                break;
            case "wmSurfaceVisible":
                s.Visible = msg["visible"]?.Value<bool>() ?? true;
                if (s.Visible) s.Activate();
                break;
            case "wmSurfaceDrag":
                ReleaseCapture();
                SendMessage(s.Handle, 0x00A1, 2, 0);
                break;
            case "wmSurfaceTitle":
                s.Title = msg["title"]?.ToString() ?? s.Title;
                break;
            case "wmSurfaceActivate":
                s.Activate();
                break;
        }
    }
#endif

    // Message Handler

    public void HandleMessage(string action, JObject msg)
    {
        var window = _core.Window;
        if (window == null) return;

        switch (action)
        {
            case "windowMinimize":
#if WINDOWS
                if (_minimizeToTray)
                {
                    ShowWindow(window.WindowHandle, SW_HIDE);
                    OnMinimized?.Invoke(); // SW_HIDE does not send WM_SIZE
                }
                else
#endif
                    window.SetMinimized(true); // → WM_SIZE SIZE_MINIMIZED → OnMinimized via subclass
                break;
            case "windowMaximize":
                var nowMax = window.Maximized;
                window.SetMaximized(!nowMax);
                _core.SendToJS("windowMaxState", !nowMax);
                break;
            case "setGuiZoom":
            {
                var pct = (int?)msg["zoom"] ?? 100;
                window.Zoom = Math.Clamp(pct, 50, 200);
                break;
            }
            case "windowClose":
#if WINDOWS
                if (_minimizeToTray)
                {
                    ShowWindow(window.WindowHandle, SW_HIDE);
                    OnMinimized?.Invoke();
                }
                else
#endif
                    window.Close();
                break;
            case "wmSurfaceOpen":
            case "wmSurfaceClose":
            case "wmSurfaceVisible":
            case "wmSurfaceDrag":
            case "wmSurfaceTitle":
            case "wmSurfaceActivate":
#if WINDOWS
                HandleSurfaceMessage(action, msg, window);
#else
                if (action == "wmSurfaceOpen") _core.SendToJS("wmSurfaceCreated", new { wmId = (int?)msg["wmId"] ?? 0, surfaceId = 0 });
#endif
                break;
            case "windowBackground":
#if WINDOWS
                try
                {
                    var hex = (msg["color"]?.ToString() ?? "").Trim();
                    if (hex.StartsWith("#") && (hex.Length == 7 || hex.Length == 4))
                    {
                        if (hex.Length == 4) hex = "#" + hex[1] + hex[1] + hex[2] + hex[2] + hex[3] + hex[3];
                        var c = System.Drawing.Color.FromArgb(255, Convert.ToInt32(hex.Substring(1, 2), 16), Convert.ToInt32(hex.Substring(3, 2), 16), Convert.ToInt32(hex.Substring(5, 2), 16));
                        window.SetBackgroundColor(c);
                    }
                }
                catch { }
#endif
                break;
            case "windowDragStart":
#if WINDOWS
                // SC_MOVE on a maximized window: Windows natively restores+repositions on drag.
                // Do NOT manually restore here — that would break double-click restore.
                ReleaseCapture();
                SendMessage(window.WindowHandle, 0x0112, 0xF012, 0); // WM_SYSCOMMAND SC_MOVE
#endif
                break;
            case "windowResizeStart":
#if WINDOWS
                if (!window.Maximized)
                {
                    var htCode = msg["direction"]?.ToString() switch {
                        "w"  => 10, // HTLEFT
                        "e"  => 11, // HTRIGHT
                        "n"  => 12, // HTTOP
                        "nw" => 13, // HTTOPLEFT
                        "ne" => 14, // HTTOPRIGHT
                        "s"  => 15, // HTBOTTOM
                        "sw" => 16, // HTBOTTOMLEFT
                        "se" => 17, // HTBOTTOMRIGHT
                        _    => 0
                    };
                    if (htCode != 0) { ReleaseCapture(); SendMessage(window.WindowHandle, 0x00A1, htCode, 0); }
                }
#endif
                break;
            case "getTtsDevices":
            {
                var target = msg["target"]?.ToString() ?? "";
                var engine = msg["engine"]?.ToString() ?? VRCNext.Services.Helpers.TtsService.EngineSapi;
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    var voices = await VRCNext.Services.Helpers.TtsService.GetVoicesAsync(engine);
                    _core.SendToJS("ttsDevices", new
                    {
                        target,
                        engine,
                        devices = VRCNext.Services.Helpers.AudioDeviceManager.ListOutputs().Select(d => new { id = d.Id, name = d.Name }).ToArray(),
                        voices,
                    });
                });
                break;
            }
            case "ttsTest":
            {
                VRCNext.Services.Helpers.TtsService.Speak(
                    msg["text"]?.ToString() ?? "VRCNext text to speech is working.",
                    msg["engine"]?.ToString() ?? VRCNext.Services.Helpers.TtsService.EngineSapi,
                    msg["voice"]?.ToString() ?? "",
                    VRCNext.Services.Helpers.AudioSelection.From(msg["deviceId"]?.ToString(), msg["deviceName"]?.ToString()),
                    msg["volume"]?.Value<int?>() ?? 100,
                    msg["rate"]?.Value<int?>() ?? 0);
                break;
            }
            case "ttsPreview":
            {
                VRCNext.Services.Helpers.TtsService.Preview(
                    msg["text"]?.ToString() ?? "Hello",
                    msg["engine"]?.ToString() ?? VRCNext.Services.Helpers.TtsService.EngineSapi,
                    msg["voice"]?.ToString() ?? "",
                    VRCNext.Services.Helpers.AudioSelection.From(msg["deviceId"]?.ToString(), msg["deviceName"]?.ToString()),
                    msg["rate"]?.Value<int?>() ?? 0);
                break;
            }
            case "getSystemFonts":
            {
                var fonts = new List<string>();
#if WINDOWS
                try
                {
                    using var installed = new System.Drawing.Text.InstalledFontCollection();
                    fonts = installed.Families
                        .Select(f => f.Name)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                        .ToList();
                }
                catch { }
#else
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("fc-list")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    psi.ArgumentList.Add(":");
                    psi.ArgumentList.Add("family");
                    using var proc = System.Diagnostics.Process.Start(psi);
                    if (proc != null)
                    {
                        var output = proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit(10000);
                        fonts = output.Split('\n')
                            .Select(l => l.Split(',')[0].Trim())
                            .Where(n => !string.IsNullOrWhiteSpace(n))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                            .ToList();
                    }
                }
                catch { }
#endif
                _core.SendToJS("systemFonts", new { fonts });
                break;
            }
            case "getCursorFiles":
            {
                var cursorDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "frontend", "cursor");
                var cursorFiles = Directory.Exists(cursorDir)
                    ? Directory.GetFiles(cursorDir, "*", SearchOption.TopDirectoryOnly)
                        .Where(f => { var ext = Path.GetExtension(f).ToLower(); return ext == ".cur" || ext == ".png"; })
                        .Select(Path.GetFileName)
                        .OrderBy(f => f)
                        .ToList()
                    : new List<string?>();
                _core.SendToJS("cursorFiles", new { files = cursorFiles, port = _core.HttpPort });
                break;
            }
            case "getCustomThemes":
            {
                var themes = new List<object>();

                static (List<string?> css, List<string?> js, string? author, string? version) ScanThemeDir(string dir)
                {
                    var all = Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly);
                    var css = all.Where(f => Path.GetExtension(f).Equals(".css", StringComparison.OrdinalIgnoreCase))
                                 .Select(Path.GetFileName).OrderBy(f => f).ToList();
                    var js  = all.Where(f => Path.GetExtension(f).Equals(".js",  StringComparison.OrdinalIgnoreCase))
                                 .Select(Path.GetFileName).OrderBy(f => f).ToList();
                    string? author = null, version = null;
                    var infoPath = Path.Combine(dir, "info.json");
                    if (File.Exists(infoPath))
                        try {
                            var info = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(infoPath));
                            author  = info["author"]?.ToString();
                            version = info["version"]?.ToString();
                        } catch { }
                    return (css, js, author, version);
                }

                // Built-in themes — served from install dir, always up-to-date
                var builtInSrc = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "frontend", "custom-themes");
                if (Directory.Exists(builtInSrc))
                {
                    foreach (var dir in Directory.GetDirectories(builtInSrc).OrderBy(d => d))
                    {
                        var name = Path.GetFileName(dir);
                        var (css, js, author, version) = ScanThemeDir(dir);
                        if (css.Count == 0 && js.Count == 0) continue;
                        themes.Add(new { id = name, name, cssFiles = css, jsFiles = js, author, version, builtIn = true });
                    }
                }

                // User custom themes — served from AppData
                var customDir = _core.CustomThemesDir;
                if (Directory.Exists(customDir))
                {
                    foreach (var dir in Directory.GetDirectories(customDir).OrderBy(d => d))
                    {
                        var name = Path.GetFileName(dir);
                        var (css, js, author, version) = ScanThemeDir(dir);
                        if (css.Count == 0 && js.Count == 0) continue;
                        themes.Add(new { id = name, name, cssFiles = css, jsFiles = js, author, version, builtIn = false });
                    }
                }

                _core.SendToJS("customThemes", new { themes, port = _core.HttpPort });
                break;
            }
        }
    }
}
