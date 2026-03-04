using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VRCNext.Services;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace VRCNext;

public class MainForm : Form
{
    // P/Invoke for borderless window drag / native window management
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("Gdi32.dll")]  private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    [DllImport("user32.dll")] private static extern int  GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int  SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private const int  GWL_STYLE        = -16;
    private const int  WS_CAPTION       = 0x00C00000;
    private const int  WS_THICKFRAME    = 0x00040000;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_NOMOVE       = 0x0002;
    private const uint SWP_NOSIZE       = 0x0001;
    private const uint SWP_NOZORDER     = 0x0004;

    private const int WM_NCCALCSIZE      = 0x0083;
    private const int WM_NCLBUTTONDBLCLK = 0x00A3;
    private const int HTCAPTION          = 2;

    private WebView2 _webView = null!;
    private readonly AppSettings _settings;
    private readonly WebhookService _webhook = new();
    private readonly FileWatcherService _fileWatcher = new();
    private readonly VRChatApiService _vrcApi = new();
    private readonly VRChatLogWatcher _logWatcher = new();
    private ChatboxService? _chatbox;
    private SteamVRService? _steamVR;
    private OscService? _osc;
    // Cache fetched player profile images: userId -> (imageUrl, fetchedAt)
    private readonly Dictionary<string, (string image, DateTime fetched)> _playerImageCache = new();
    // Favorite friends: userId -> fvrt_xxx id
    private readonly Dictionary<string, string> _favoriteFriends = new();
    private ImageCacheService? _imgCache;
    private readonly CacheHandler _cache = new();
    private static readonly System.Text.RegularExpressions.Regex _vrcImgUrlRegex = new(
        @"""(https://(?:api\.vrchat\.cloud|assets\.vrchat\.com|files\.vrchat\.cloud)[^""]+)""",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    private readonly List<WebhookService.PostRecord> _postHistory = new();
    private bool _relayRunning;
    private DateTime _relayStart;
    private double _totalSizeMB;
    private int _fileCount;
    private System.Windows.Forms.Timer _uptimeTimer = null!;
    private VRChatWebSocketService? _wsService;
    private Process? _vcProcess;
    private readonly UserTimeTracker _timeTracker;
    private readonly WorldTimeTracker _worldTimeTracker;
    private readonly PhotoPlayersStore _photoPlayersStore;
    private readonly TimelineService _timeline;
    private readonly UpdateService _updateService = new();
    private string _lastTrackedWorldId = "";
    private FileSystemWatcher? _vrcPhotoWatcher;

    // User detail cache: serves profiles instantly on repeated opens
    private readonly Dictionary<string, (object payload, DateTime cachedAt)> _userDetailCache = new();

    // Library file cache: quick-scan result for pagination
    private record LibFileEntry(FileInfo Fi, string Host, string Folder);
    private List<LibFileEntry> _libFileCache = new();
    private int _libFileCacheTotal = 0;

    // Timeline: cumulative instance player tracking
    private readonly Dictionary<string, (string displayName, string image)> _cumulativeInstancePlayers = new();
    private readonly HashSet<string>   _meetAgainThisInstance = new();
    private string?                    _pendingInstanceEventId;
    private System.Threading.Timer?    _instanceSnapshotTimer;
    private bool                       _logWatcherBootstrapped;
    private string                     _currentVrcUserId = "";

    // Live friend store — seeded by REST on startup, kept fresh by WebSocket events
    private readonly Dictionary<string, JObject>             _friendStore      = new(); // userId -> latest user JObject

    // Friends Timeline: per-friend state for change detection
    private readonly Dictionary<string, string>              _friendLastLoc        = new(); // userId -> location
    private readonly Dictionary<string, string>              _friendLastStatus     = new(); // userId -> status
    private readonly Dictionary<string, string>              _friendLastStatusDesc = new(); // userId -> statusDescription
    private readonly Dictionary<string, string>              _friendLastBio        = new(); // userId -> bio
    private readonly Dictionary<string, (string name, string image)> _friendNameImg = new(); // userId -> name+image
    private bool                                             _friendStateSeeded = false;

    public MainForm()
    {
        _settings = AppSettings.Load();
        _timeTracker = UserTimeTracker.Load();
        _worldTimeTracker = WorldTimeTracker.Load();
        _photoPlayersStore = PhotoPlayersStore.Load();
        _timeline = TimelineService.Load();

        Text = "VRCNext";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        Size = new Size(1100, 700);
        MinimumSize = new Size(900, 540);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(8, 12, 21);
        FormBorderStyle = FormBorderStyle.Sizable;
        DoubleBuffered = true;

        _webView = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_webView);

        _uptimeTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _uptimeTimer.Tick += (s, e) =>
        {
            if (_relayRunning)
                SendToJS("uptimeTick", (DateTime.Now - _relayStart).ToString(@"hh\:mm\:ss"));
        };

        _fileWatcher.NewFile += OnNewFile;
        Load += async (s, e) => await InitWebView();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Enable Windows 11 native rounded corners (DWMWCP_ROUND = 2)
        var pref = 2;
        DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, sizeof(int));
    }

    protected override void WndProc(ref Message m)
    {
        // Extend client area to cover the native title bar so it is invisible,
        // while keeping WS_THICKFRAME + WS_CAPTION for Aero Snap and edge-tiling.
        if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            m.Result = IntPtr.Zero;
            return;
        }

        // With FormBorderStyle.Sizable, Windows routes title-bar double-click here
        // as a non-client message (WM_NCLBUTTONDBLCLK) rather than through WebView2.
        if (m.Msg == WM_NCLBUTTONDBLCLK && m.WParam.ToInt32() == HTCAPTION)
        {
            WindowState = WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal
                : FormWindowState.Maximized;
            SendToJS("windowMaxState", WindowState == FormWindowState.Maximized);
            return;
        }

        base.WndProc(ref m);
    }



    private async Task InitWebView()
    {
        var wv2DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VRCNext", "WebView2");
        Directory.CreateDirectory(wv2DataDir);
        var env = await CoreWebView2Environment.CreateAsync(null, wv2DataDir);
        await _webView.EnsureCoreWebView2Async(env);

        // Image cache (avatars, world thumbnails, group icons)
        var imgCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VRCNext", "ImageCache");
        Directory.CreateDirectory(imgCacheDir);
        _imgCache = new ImageCacheService(imgCacheDir, _vrcApi.GetHttpClient())
        {
            Enabled    = _settings.ImgCacheEnabled,
            LimitBytes = (long)_settings.ImgCacheLimitGb * 1024 * 1024 * 1024,
        };
        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            ImageCacheService.VirtualHost, imgCacheDir, CoreWebView2HostResourceAccessKind.Allow);

        // Map watch folders as virtual hosts so images/videos can be loaded
        UpdateVirtualHostMappings();

        // Load setup wizard or main UI based on whether setup is complete
        var wwwroot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
        var startPage = _settings.SetupComplete
            ? Path.Combine(wwwroot, "index.html")
            : Path.Combine(wwwroot, "setup", "setup.html");
        // Fallback to index.html if setup page doesn't exist
        if (!File.Exists(startPage))
            startPage = Path.Combine(wwwroot, "index.html");
        _webView.CoreWebView2.Navigate(new Uri(startPage).AbsoluteUri);

        // Listen for messages from JS
        _webView.CoreWebView2.WebMessageReceived += OnWebMessage;

        // Disable context menu and devtools in release
        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
    }

    private readonly List<string> _mappedHosts = new();

    private void UpdateVirtualHostMappings()
    {
        if (_webView.CoreWebView2 == null) return;

        foreach (var host in _mappedHosts)
        {
            try { _webView.CoreWebView2.ClearVirtualHostNameToFolderMapping(host); } catch { }
        }
        _mappedHosts.Clear();

        for (int i = 0; i < _settings.WatchFolders.Count; i++)
        {
            var folder = _settings.WatchFolders[i];
            if (!Directory.Exists(folder)) continue;
            var host = $"localmedia{i}.vrcnext.local";
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                host, folder, CoreWebView2HostResourceAccessKind.Allow);
            _mappedHosts.Add(host);
        }
    }

    // JS to C# message handler
    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var msg = JObject.Parse(e.WebMessageAsJson);
            var action = msg["action"]?.ToString() ?? "";

            switch (action)
            {
                case "ready":
                    // Debug: show what Load() did
                    if (AppSettings.LastLoadError != null)
                        SendToJS("log", new { msg = $"[LOAD ERROR] {AppSettings.LastLoadError}", color = "err" });
                    SendToJS("log", new { msg = $"[LOAD] {AppSettings.LoadDebugInfo}", color = "sec" });
                    SendToJS("log", new { msg = $"[STARTUP] Webhooks: {string.Join(", ", _settings.Webhooks.Select((w,i) => $"#{i+1} \"{w.Name}\" url={w.Url?.Length ?? 0}ch {(w.Enabled?"ON":"off")}"))}", color = "sec" });
                    SendToJS("loadSettings", _settings);
                    SendToJS("favoritesLoaded", _settings.Favorites);
                    if (_settings.AutoStart) StartRelay();
                    _ = VrcTryResumeAsync();
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(3000); // wait for UI to settle
                        var version = await _updateService.CheckAsync();
                        if (version != null)
                            Invoke(() => SendToJS("updateAvailable", new { version }));
                    });
                    break;

                // Setup Wizard
                case "setupReady":
                    // Auto-detect VRChat path if not already set
                    var detectedPath = _settings.VrcPath;
                    if (string.IsNullOrWhiteSpace(detectedPath) || !File.Exists(detectedPath))
                        detectedPath = DetectVrcLaunchExe();
                    if (!string.IsNullOrEmpty(detectedPath) && detectedPath != _settings.VrcPath)
                    {
                        _settings.VrcPath = detectedPath;
                        _settings.Save();
                    }

                    // Pre-fill photo dir with first WatchFolder, or default VRChat path
                    var photoDir = _settings.WatchFolders.FirstOrDefault() ?? "";
                    if (string.IsNullOrEmpty(photoDir))
                    {
                        var defPhoto = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VRChat");
                        if (Directory.Exists(defPhoto)) photoDir = defPhoto;
                    }

                    SendToJS("setupState", new
                    {
                        vrcPath = detectedPath ?? "",
                        photoDir,
                        loggedIn = _vrcApi.IsLoggedIn,
                        displayName = _vrcApi.IsLoggedIn ? (_vrcApi.CurrentUserRaw?["displayName"]?.ToString() ?? "") : "",
                    });
                    _ = VrcTryResumeAsync();
                    break;

                case "setupDone":
                    _settings.SetupComplete = true;
                    _settings.Save();
                    Invoke(() =>
                    {
                        var mainHtml = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "index.html");
                        _webView.CoreWebView2.Navigate(new Uri(mainHtml).AbsoluteUri);
                    });
                    break;

                case "resetSetup":
                    _settings.SetupComplete = false;
                    _settings.Save();
                    Invoke(() =>
                    {
                        var setupHtml = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "setup", "setup.html");
                        if (File.Exists(setupHtml))
                            _webView.CoreWebView2.Navigate(new Uri(setupHtml).AbsoluteUri);
                    });
                    break;

                case "clearImgCache":
                    _ = Task.Run(() =>
                    {
                        _imgCache?.ClearAll();
                        Invoke(() => SendToJS("log", new { msg = "🗑 Image cache cleared.", color = "sec" }));
                    });
                    break;

                case "clearFfcCache":
                    _ = Task.Run(() =>
                    {
                        _cache.ClearAll();
                        _userDetailCache.Clear();
                        Invoke(() => SendToJS("log", new { msg = "🗑 FFC cache cleared.", color = "sec" }));
                    });
                    break;

                case "forceFfcAll":
                    _ = Task.Run(ForceFfcAllAsync);
                    break;

                case "setupSaveVrcPath":
                    _settings.VrcPath = msg["path"]?.ToString() ?? "";
                    _settings.Save();
                    break;

                case "setupSavePhotoDir":
                    var setupPhotoDir = msg["path"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(setupPhotoDir)
                        && Directory.Exists(setupPhotoDir)
                        && !_settings.WatchFolders.Contains(setupPhotoDir, StringComparer.OrdinalIgnoreCase))
                    {
                        _settings.WatchFolders.Add(setupPhotoDir);
                        _settings.Save();
                    }
                    break;

                case "setupBrowsePhotoDir":
                    Invoke(() =>
                    {
                        using var dlg = new FolderBrowserDialog();
                        dlg.Description = "Select your VRChat Photos folder";
                        var defPath = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VRChat");
                        if (Directory.Exists(defPath)) dlg.SelectedPath = defPath;
                        if (dlg.ShowDialog() == DialogResult.OK)
                            SendToJS("setupPhotoDirResult", dlg.SelectedPath);
                    });
                    break;

                // Window chrome (borderless)
                case "windowMinimize":
                    WindowState = FormWindowState.Minimized;
                    break;
                case "windowMaximize":
                    if (WindowState == FormWindowState.Maximized)
                        WindowState = FormWindowState.Normal;
                    else
                        WindowState = FormWindowState.Maximized;
                    SendToJS("windowMaxState", WindowState == FormWindowState.Maximized);
                    break;
                case "windowClose":
                    Close();
                    break;
                case "windowDragStart":
                    // SC_MOVE on a maximized window: Windows natively restores+repositions on drag.
                    // Do NOT manually restore here — that would break double-click restore.
                    ReleaseCapture();
                    SendMessage(Handle, 0x0112, 0xF012, 0); // WM_SYSCOMMAND SC_MOVE
                    break;

                case "windowResizeStart":
                    if (WindowState != FormWindowState.Maximized)
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
                        if (htCode != 0) { ReleaseCapture(); SendMessage(Handle, 0x00A1, htCode, 0); }
                    }
                    break;

                case "startRelay":
                    StartRelay();
                    break;

                case "stopRelay":
                    StopRelay();
                    break;

                case "saveSettings":
                    var data = msg["data"];
                    if (data != null) ApplySettings(data);
                    break;

                case "addFolder":
                    Invoke(() =>
                    {
                        using var dlg = new FolderBrowserDialog();
                        if (dlg.ShowDialog() == DialogResult.OK)
                            SendToJS("folderAdded", dlg.SelectedPath);
                    });
                    break;

                case "deletePost":
                    var msgId = msg["messageId"]?.ToString();
                    var whUrl = msg["webhookUrl"]?.ToString();
                    if (msgId != null && whUrl != null)
                    {
                        var ok = await _webhook.DeleteAsync(whUrl, msgId);
                        SendToJS("deleteResult", new { messageId = msgId, success = ok });
                        if (ok) _postHistory.RemoveAll(p => p.MessageId == msgId);
                    }
                    break;

                case "manualPost":
                    var filePath = msg["filePath"]?.ToString();
                    if (filePath != null) await PostFile(filePath, true);
                    break;

                case "dropFiles":
                    var files = msg["files"]?.ToObject<string[]>();
                    if (files != null)
                    {
                        foreach (var f in files)
                        {
                            var ext = Path.GetExtension(f).ToLower();
                            if (FileWatcherService.ImgExt.Contains(ext) || FileWatcherService.VidExt.Contains(ext))
                                await PostFile(f, true);
                        }
                    }
                    break;

                case "scanLibrary":
                    ScanLibraryFolders();
                    break;

                case "loadLibraryPage":
                    var libOffset = msg["offset"]?.Value<int>() ?? 0;
                    _ = Task.Run(() =>
                    {
                        var items = BuildLibraryItems(libOffset, 100);
                        Invoke(() => SendToJS("libraryPageData", new
                        {
                            files = items,
                            total = _libFileCacheTotal,
                            offset = libOffset,
                            hasMore = libOffset + items.Count < _libFileCacheTotal,
                        }));
                    });
                    break;

                case "deleteLibraryFile":
                    var delPath = msg["path"]?.ToString();
                    if (!string.IsNullOrEmpty(delPath))
                    {
                        try
                        {
                            var fullDelPath = Path.GetFullPath(delPath);
                            bool inAllowedFolder = _settings.WatchFolders.Any(f =>
                                !string.IsNullOrEmpty(f) &&
                                fullDelPath.StartsWith(
                                    Path.GetFullPath(f).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                                    StringComparison.OrdinalIgnoreCase));
                            if (!inAllowedFolder)
                            {
                                SendToJS("log", new { msg = "Delete blocked: path outside watch folders.", color = "err" });
                                break;
                            }
                            if (File.Exists(fullDelPath))
                            {
                                File.Delete(fullDelPath);
                                _settings.Favorites.Remove(delPath);
                                _settings.Save();
                                SendToJS("log", new { msg = $"Deleted: {Path.GetFileName(fullDelPath)}", color = "ok" });
                                SendToJS("libraryFileDeleted", new { path = delPath });
                            }
                            else
                            {
                                SendToJS("log", new { msg = "File not found", color = "err" });
                            }
                        }
                        catch (Exception ex)
                        {
                            SendToJS("log", new { msg = $"Delete error: {ex.Message}", color = "err" });
                        }
                    }
                    break;

                case "addFavorite":
                    var favPath = msg["path"]?.ToString();
                    if (!string.IsNullOrEmpty(favPath) && !_settings.Favorites.Contains(favPath))
                    {
                        _settings.Favorites.Add(favPath);
                        _settings.Save();
                    }
                    break;

                case "removeFavorite":
                    var unfavPath = msg["path"]?.ToString();
                    if (!string.IsNullOrEmpty(unfavPath))
                    {
                        _settings.Favorites.Remove(unfavPath);
                        _settings.Save();
                    }
                    break;

                case "browseExe":
                    var target = msg["target"]?.ToString() ?? "extra";
                    Invoke(() =>
                    {
                        using var dlg = new OpenFileDialog
                        {
                            Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
                            Title = target == "vrchat" ? "Select VRChat executable" : "Select application"
                        };
                        if (dlg.ShowDialog() == DialogResult.OK)
                        {
                            SendToJS("exeAdded", new { target, path = dlg.FileName });
                            // Persist VRC path immediately (works for both setup and main UI)
                            if (target == "vrchat")
                            {
                                _settings.VrcPath = dlg.FileName;
                                _settings.Save();
                            }
                        }
                    });
                    break;

                // Dashboard background image picker
                case "browseDashBg":
                    Invoke(() =>
                    {
                        using var dlg = new OpenFileDialog
                        {
                            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif|All files (*.*)|*.*",
                            Title = "Choose Dashboard Background"
                        };
                        if (dlg.ShowDialog() == DialogResult.OK)
                        {
                            try
                            {
                                var bytes = File.ReadAllBytes(dlg.FileName);
                                var ext2 = Path.GetExtension(dlg.FileName).ToLower();
                                var mime = ext2 switch
                                {
                                    ".png" => "image/png",
                                    ".jpg" or ".jpeg" => "image/jpeg",
                                    ".gif" => "image/gif",
                                    ".bmp" => "image/bmp",
                                    ".webp" => "image/webp",
                                    _ => "image/png"
                                };
                                var dataUri = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
                                SendToJS("dashBgSelected", new { path = dlg.FileName, dataUri });
                            }
                            catch (Exception ex)
                            {
                                SendToJS("log", new { msg = $"Background image error: {ex.Message}", color = "err" });
                            }
                        }
                    });
                    break;

                // Load dashboard bg on startup (convert saved path to base64)
                case "vrcLoadDashBg":
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var bgPath = msg["path"]?.ToString();
                            if (!string.IsNullOrEmpty(bgPath) && File.Exists(bgPath))
                            {
                                var bytes = File.ReadAllBytes(bgPath);
                                var ext3 = Path.GetExtension(bgPath).ToLower();
                                var mime = ext3 switch
                                {
                                    ".png" => "image/png",
                                    ".jpg" or ".jpeg" => "image/jpeg",
                                    ".gif" => "image/gif",
                                    ".bmp" => "image/bmp",
                                    ".webp" => "image/webp",
                                    _ => "image/png"
                                };
                                var dataUri = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
                                Invoke(() => SendToJS("dashBgSelected", new { path = bgPath, dataUri }));
                            }
                        }
                        catch (Exception ex)
                        {
                            Invoke(() => SendToJS("log", new { msg = $"Load background error: {ex.Message}", color = "err" }));
                        }
                    });
                    break;

                // Pick random image from watch folders
                case "vrcRandomDashBg":
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var imgExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                                { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };
                            var allImages = new List<string>();

                            foreach (var folder in _settings.WatchFolders.Where(Directory.Exists))
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
                                Invoke(() => SendToJS("log", new { msg = "Random background: no images found in watch folders", color = "warn" }));
                                return;
                            }

                            var rng = new Random();
                            var picked = allImages[rng.Next(allImages.Count)];
                            var bytes = File.ReadAllBytes(picked);
                            var imgExt = Path.GetExtension(picked).ToLower();
                            var mime = imgExt switch
                            {
                                ".png" => "image/png",
                                ".jpg" or ".jpeg" => "image/jpeg",
                                ".gif" => "image/gif",
                                ".bmp" => "image/bmp",
                                ".webp" => "image/webp",
                                _ => "image/png"
                            };
                            var dataUri = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
                            Invoke(() =>
                            {
                                SendToJS("dashBgSelected", new { path = picked, dataUri });
                                SendToJS("log", new { msg = $"Random background: {Path.GetFileName(picked)}", color = "ok" });
                            });
                        }
                        catch (Exception ex)
                        {
                            Invoke(() => SendToJS("log", new { msg = $"Random background error: {ex.Message}", color = "err" }));
                        }
                    });
                    break;

                // Resolve world IDs to names/thumbnails for dashboard
                case "vrcResolveWorlds":
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var worldIds = msg["worldIds"]?.ToObject<List<string>>() ?? new();

                            foreach (var wid in worldIds)
                            {
                                try
                                {
                                    var world = await _vrcApi.GetWorldAsync(wid);
                                    if (world != null)
                                    {
                                        var single = new Dictionary<string, object>
                                        {
                                            [wid] = new
                                            {
                                                name = world["name"]?.ToString() ?? "",
                                                thumbnailImageUrl = world["thumbnailImageUrl"]?.ToString() ?? "",
                                                imageUrl = world["imageUrl"]?.ToString() ?? ""
                                            }
                                        };
                                        Invoke(() => SendToJS("vrcWorldsResolved", single));
                                    }
                                    await Task.Delay(250);
                                }
                                catch { /* skip failed worlds */ }
                            }
                        }
                        catch (Exception ex)
                        {
                            Invoke(() => SendToJS("log", new { msg = $"World resolve error: {ex.Message}", color = "err" }));
                        }
                    });
                    break;

                case "fetchDiscoveryFeed":
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var url = msg["url"]?.ToString() ?? "";
                            using var http = new System.Net.Http.HttpClient();
                            http.DefaultRequestHeaders.Add("User-Agent", "VRCNext");
                            var resp = await http.GetStringAsync(url);
                            Invoke(() => SendToJS("discoveryFeed", new { json = resp }));
                        }
                        catch (Exception ex)
                        {
                            Invoke(() => SendToJS("log", new { msg = $"Discovery fetch error: {ex.Message}", color = "err" }));
                        }
                    });
                    break;

                case "playVRChat":
                    if (IsVrcRunning())
                        SendToJS("log", new { msg = "VRChat is already running.", color = "ok" });
                    else
                        SendToJS("vrcLaunchNeeded", new { location = "", steamVr = IsSteamVrRunning() });
                    break;

                case "vrcLogin":
                    var vrcUser = msg["username"]?.ToString() ?? "";
                    var vrcPass = msg["password"]?.ToString() ?? "";
                    await VrcLoginAsync(vrcUser, vrcPass);
                    break;

                case "vrc2FA":
                    var code2fa = msg["code"]?.ToString() ?? "";
                    var type2fa = msg["type"]?.ToString() ?? "totp";
                    await VrcVerify2FAAsync(code2fa, type2fa);
                    break;

                case "vrcLogout":
                    _wsService?.Stop();
                    await _vrcApi.LogoutAsync();
                    // Clear saved session cookies
                    _settings.VrcAuthCookie = "";
                    _settings.VrcTwoFactorCookie = "";
                    _settings.Save();
                    SendToJS("vrcLoggedOut", null);
                    SendToJS("log", new { msg = "VRChat: Logged out", color = "sec" });
                    break;

                case "vrcRefreshFriends":
                    await VrcRefreshFriendsAsync();
                    break;

                // Update own status
                case "vrcUpdateStatus":
                    var newStatus = msg["status"]?.ToString() ?? "active";
                    var newDesc = msg["statusDescription"]?.ToString() ?? "";
                    await VrcUpdateStatusAsync(newStatus, newDesc);
                    break;

                // Update own profile (bio, pronouns, links, languages)
                case "vrcUpdateProfile":
                    var upBio = msg["bio"] != null ? msg["bio"]!.ToString() : (string?)null;
                    var upPronouns = msg["pronouns"] != null ? msg["pronouns"]!.ToString() : (string?)null;
                    var upBioLinks = msg["bioLinks"]?.ToObject<List<string>>();
                    var upTags = msg["tags"]?.ToObject<List<string>>();
                    _ = Task.Run(async () =>
                    {
                        var updUser = await _vrcApi.UpdateProfileAsync(upBio, upPronouns, upBioLinks, upTags);
                        Invoke(() =>
                        {
                            if (updUser != null)
                            {
                                SendVrcUserData(updUser);
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

                // Multi-Invite: invite mehrere Freunde zur eigenen Instanz
                case "vrcBatchInvite":
                    var batchIds = msg["userIds"]?.ToObject<List<string>>() ?? new List<string>();
                    if (batchIds.Count > 0)
                    {
                        _ = Task.Run(async () =>
                        {
                            int bDone = 0, bSuccess = 0, bFail = 0;
                            int bTotal = batchIds.Count;
                            foreach (var bUid in batchIds)
                            {
                                var bOk = await _vrcApi.InviteFriendAsync(bUid);
                                bDone++;
                                if (bOk) bSuccess++; else bFail++;
                                Invoke(() => SendToJS("vrcBatchInviteProgress", new { done = bDone, total = bTotal, success = bSuccess, fail = bFail }));
                                if (bDone < bTotal)
                                    await Task.Delay(1500); // 1.5s Abstand (Rate Limit)
                            }
                        });
                    }
                    break;

                // Get friend detail
                case "vrcGetFriendDetail":
                    var friendId = msg["userId"]?.ToString();
                    if (!string.IsNullOrEmpty(friendId))
                        await VrcGetFriendDetailAsync(friendId);
                    break;

                // Join friend (self-invite)
                case "vrcJoinFriend":
                    var joinLoc = msg["location"]?.ToString();
                    if (!string.IsNullOrEmpty(joinLoc))
                    {
                        if (IsVrcRunning())
                        {
                            // VRC running — try API self-invite first
                            var ok2 = await _vrcApi.InviteSelfAsync(joinLoc);
                            if (ok2)
                            {
                                SendToJS("vrcActionResult", new { action = "join", success = true,
                                    message = "Self-invite sent! Check VRChat." });
                            }
                            else
                            {
                                // Fallback: launch via vrchat:// protocol URI
                                try
                                {
                                    var launchUri = VRChatApiService.BuildLaunchUri(joinLoc);
                                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                    {
                                        FileName = launchUri,
                                        UseShellExecute = true
                                    });
                                    SendToJS("vrcActionResult", new { action = "join", success = true,
                                        message = "Launching VRChat to join world..." });
                                    SendToJS("log", new { msg = $"Launched via vrchat:// protocol", color = "ok" });
                                }
                                catch (Exception ex)
                                {
                                    SendToJS("vrcActionResult", new { action = "join", success = false,
                                        message = "Failed to join. Is VRChat running?" });
                                    SendToJS("log", new { msg = $"Launch fallback failed: {ex.Message}", color = "err" });
                                }
                            }
                        }
                        else
                        {
                            // VRC not running — ask VR or Desktop
                            Invoke(() => SendToJS("vrcLaunchNeeded", new { location = joinLoc, steamVr = IsSteamVrRunning() }));
                        }
                    }
                    break;

                // Invite friend
                case "vrcInviteFriend":
                    var invUserId = msg["userId"]?.ToString();
                    var invMsgSlot = msg["messageSlot"]?.Value<int?>();
                    if (!string.IsNullOrEmpty(invUserId))
                    {
                        var ok3 = await _vrcApi.InviteFriendAsync(invUserId, invMsgSlot);
                        SendToJS("vrcActionResult", new
                        {
                            action = "invite",
                            success = ok3,
                            message = ok3 ? "Invite sent!" : "Failed to send invite. Make sure you are in a valid instance."
                        });
                    }
                    break;

                case "vrcGetInviteMessages":
                    var gimUserId = msg["userId"]?.ToString() ?? _vrcApi.CurrentUserId;
                    if (!string.IsNullOrEmpty(gimUserId))
                    {
                        var msgs = await _vrcApi.GetInviteMessagesAsync(gimUserId);
                        SendToJS("vrcInviteMessages", msgs ?? new Newtonsoft.Json.Linq.JArray());
                    }
                    break;

                case "vrcUpdateInviteMessage":
                    var uimUserId = msg["userId"]?.ToString() ?? _vrcApi.CurrentUserId;
                    var uimSlot   = msg["slot"]?.Value<int>() ?? -1;
                    var uimText   = msg["message"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(uimUserId) && uimSlot >= 0 && !string.IsNullOrEmpty(uimText))
                    {
                        var (uimOk, uimArr, uimCooldown) = await _vrcApi.UpdateInviteMessageAsync(uimUserId, uimSlot, uimText);
                        if (uimOk && uimArr != null)
                            SendToJS("vrcInviteMessages", uimArr);
                        else
                            SendToJS("vrcInviteMessageUpdateFailed", new { slot = uimSlot, cooldown = uimCooldown });
                    }
                    break;

                // Request invite from friend
                case "vrcRequestInvite":
                    var reqUserId = msg["userId"]?.ToString();
                    if (!string.IsNullOrEmpty(reqUserId))
                    {
                        var ok4 = await _vrcApi.RequestInviteAsync(reqUserId);
                        SendToJS("vrcActionResult", new
                        {
                            action = "requestInvite",
                            success = ok4,
                            message = ok4 ? "Invite request sent!" : "Failed to request invite."
                        });
                    }
                    break;

                case "vrcCreateInstance":
                    var ciWorldId = msg["worldId"]?.ToString() ?? "";
                    var ciType = msg["type"]?.ToString() ?? "public";
                    var ciRegion = msg["region"]?.ToString() ?? "eu";
                    if (!string.IsNullOrEmpty(ciWorldId))
                    {
                        _ = Task.Run(async () =>
                        {
                            var location = _vrcApi.BuildInstanceLocation(ciWorldId, ciType, ciRegion);
                            var ok = await _vrcApi.InviteSelfAsync(location);
                            Invoke(() =>
                            {
                                SendToJS("vrcActionResult", new
                                {
                                    action = "createInstance",
                                    success = ok,
                                    message = ok ? "Instance created! Self-invite sent." : "Failed to create instance.",
                                    location
                                });
                            });
                        });
                    }
                    break;

                // User Notes
                case "vrcUpdateNote":
                    var noteUserId = msg["userId"]?.ToString() ?? "";
                    var noteText = msg["note"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(noteUserId))
                    {
                        _ = Task.Run(async () =>
                        {
                            var ok = await _vrcApi.UpdateUserNoteAsync(noteUserId, noteText);
                            Invoke(() => SendToJS("vrcNoteUpdated", new
                            {
                                success = ok,
                                userId = noteUserId,
                                note = noteText
                            }));
                        });
                    }
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
                        _ = Task.Run(FetchAndCacheAvatarsAsync);
                    }
                    else
                    {
                        _ = Task.Run(FetchAndCacheFavAvatarsAsync);
                    }
                    break;

                case "vrcSelectAvatar":
                    var selAvatarId = msg["avatarId"]?.ToString();
                    if (!string.IsNullOrEmpty(selAvatarId))
                    {
                        _ = Task.Run(async () =>
                        {
                            var ok5 = await _vrcApi.SelectAvatarAsync(selAvatarId);
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
                    if (!string.IsNullOrWhiteSpace(avSearchQuery))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                const int avLimit = 20;
                                var raw = await _vrcApi.SearchAvatarsAsync(avSearchQuery, avLimit, avSearchPage);
                                var list = raw.Cast<JObject>().Select(a => new
                                {
                                    id               = a["vrc_id"]?.ToString() ?? a["id"]?.ToString() ?? "",
                                    name             = a["name"]?.ToString() ?? "",
                                    thumbnailImageUrl = a["image_url"]?.ToString() ?? a["thumbnailImageUrl"]?.ToString() ?? "",
                                    imageUrl         = a["image_url"]?.ToString() ?? a["imageUrl"]?.ToString() ?? "",
                                    authorName       = a["author"]?["name"]?.ToString() ?? a["authorName"]?.ToString() ?? "",
                                    releaseStatus    = "public",
                                    description      = a["description"]?.ToString() ?? "",
                                    unityPackages    = a["unityPackages"] as JArray ?? new JArray(),
                                    compatibility    = a["compatibility"] as JArray ?? new JArray(),
                                }).ToList();
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

                // Search - users, worlds, groups
                case "vrcSearchUsers":
                    var uQ = msg["query"]?.ToString() ?? "";
                    var uOff = msg["offset"]?.Value<int>() ?? 0;
                    _ = Task.Run(async () =>
                    {
                        var res = await _vrcApi.SearchUsersAsync(uQ, 20, uOff);
                        var list = res.Cast<JObject>().Select(u => new {
                            id = u["id"]?.ToString() ?? "", displayName = u["displayName"]?.ToString() ?? "",
                            image = VRChatApiService.GetUserImage(u), status = u["status"]?.ToString() ?? "offline",
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
                    _ = Task.Run(async () =>
                    {
                        var res = await _vrcApi.SearchWorldsAsync(wQ, 20, wOff);
                        var list = res.Cast<JObject>().Select(w => new {
                            id = w["id"]?.ToString() ?? "", name = w["name"]?.ToString() ?? "",
                            imageUrl = w["imageUrl"]?.ToString() ?? "", thumbnailImageUrl = w["thumbnailImageUrl"]?.ToString() ?? "",
                            authorName = w["authorName"]?.ToString() ?? "", occupants = w["occupants"]?.Value<int>() ?? 0,
                            capacity = w["capacity"]?.Value<int>() ?? 0, favorites = w["favorites"]?.Value<int>() ?? 0,
                            visits = w["visits"]?.Value<int>() ?? 0, description = w["description"]?.ToString() ?? "",
                            tags = w["tags"]?.ToObject<List<string>>() ?? new(),
                            worldTimeSeconds = _worldTimeTracker.GetWorldStats(w["id"]?.ToString() ?? "").totalSeconds,
                        }).ToList();
                        Invoke(() => SendToJS("vrcSearchResults", new { type = "worlds", results = list, offset = wOff, hasMore = list.Count >= 20 }));
                    });
                    break;

                case "vrcSearchGroups":
                    var gQ = msg["query"]?.ToString() ?? "";
                    var gOff = msg["offset"]?.Value<int>() ?? 0;
                    _ = Task.Run(async () =>
                    {
                        var res = await _vrcApi.SearchGroupsAsync(gQ, 20, gOff);
                        var list = res.Cast<JObject>().Select(g => new {
                            id = g["id"]?.ToString() ?? "", name = g["name"]?.ToString() ?? "",
                            shortCode = g["shortCode"]?.ToString() ?? "", description = g["description"]?.ToString() ?? "",
                            iconUrl = g["iconUrl"]?.ToString() ?? "", bannerUrl = g["bannerUrl"]?.ToString() ?? "",
                            memberCount = g["memberCount"]?.Value<int>() ?? 0, privacy = g["privacy"]?.ToString() ?? "",
                        }).ToList();
                        Invoke(() => SendToJS("vrcSearchResults", new { type = "groups", results = list, offset = gOff, hasMore = list.Count >= 20 }));
                    });
                    break;

                case "vrcGetWorldDetail":
                    var wdId = msg["worldId"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(wdId))
                    {
                        _ = Task.Run(async () =>
                        {
                            static string StripNonce(string l) =>
                                System.Text.RegularExpressions.Regex.Replace(l ?? "", @"~nonce\([^)]*\)", "");

                            var world = await _vrcApi.GetWorldAsync(wdId);
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
                            var rawInstances = new List<(string instanceId, int users, string type, string region, string location, string ownerId)>();
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
                                        var (_, _, instType) = VRChatApiService.ParseLocation($"{wdId}:{instId}");
                                        var regionMatch = System.Text.RegularExpressions.Regex.Match(instId, @"region\(([^)]+)\)");
                                        var region = regionMatch.Success ? regionMatch.Groups[1].Value : "us";
                                        var loc = $"{wdId}:{instId}";
                                        rawInstances.Add((instId, users, instType, region, loc, ParseOwnerId(instId)));
                                        knownLocations.Add(loc);
                                    }
                                }
                            }
                            // Find friend locations in this world not covered by the world API instances
                            List<string> friendLocs;
                            lock (_friendStore)
                            {
                                friendLocs = _friendStore.Values
                                    .Select(f => f["location"]?.ToString() ?? "")
                                    .Where(loc => loc.StartsWith(wdId + ":"))
                                    .Distinct()
                                    .Where(loc => !knownLocations.Contains(StripNonce(loc)))
                                    .ToList();
                            }
                            // Fetch real user counts for friend-inferred instances in parallel
                            if (friendLocs.Count > 0)
                            {
                                var instTasks = friendLocs.Select(loc => _vrcApi.GetInstanceAsync(loc)).ToArray();
                                var instResults = await Task.WhenAll(instTasks);
                                for (int i = 0; i < friendLocs.Count; i++)
                                {
                                    var loc = friendLocs[i];
                                    var instData = instResults[i];
                                    var nUsers = instData?["n_users"]?.Value<int>() ?? instData?["userCount"]?.Value<int>() ?? 0;
                                    var (_, instId2, instType2) = VRChatApiService.ParseLocation(loc);
                                    var regionMatch2 = System.Text.RegularExpressions.Regex.Match(instId2, @"region\(([^)]+)\)");
                                    var region2 = regionMatch2.Success ? regionMatch2.Groups[1].Value : "us";
                                    rawInstances.Add((instId2, nUsers, instType2, region2, loc, ParseOwnerId(instId2)));
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
                                var gTasks = uniqueGroupIds.ToDictionary(id => id, id => _vrcApi.GetGroupAsync(id));
                                try { await Task.WhenAll(gTasks.Values); } catch { }
                                foreach (var kv in gTasks)
                                    if (!kv.Value.IsFaulted && kv.Value.Result != null)
                                        groupInfoMap[kv.Key] = (
                                            kv.Value.Result["name"]?.ToString() ?? "",
                                            kv.Value.Result["shortCode"]?.ToString() ?? "");
                            }
                            var instances = rawInstances.Select(r => {
                                var ownerName = "";
                                var ownerGroup = "";
                                if (r.ownerId.StartsWith("usr_"))
                                    lock (_friendStore) { _friendStore.TryGetValue(r.ownerId, out var f); ownerName = f?["displayName"]?.ToString() ?? ""; }
                                else if (r.ownerId.StartsWith("grp_") && groupInfoMap.TryGetValue(r.ownerId, out var info))
                                    (ownerName, ownerGroup) = info;
                                return new { instanceId = r.instanceId, users = r.users, type = r.type, region = r.region, location = r.location, ownerName, ownerGroup, ownerId = r.ownerId };
                            }).ToList<object>();
                            var tags = world["tags"]?.ToObject<List<string>>() ?? new();
                            var (wTimeSeconds, wVisitCount, wLastVisited) = _worldTimeTracker.GetWorldStats(world["id"]?.ToString() ?? "");
                            Invoke(() => SendToJS("vrcWorldDetail", new
                            {
                                id = world["id"]?.ToString() ?? "",
                                name = world["name"]?.ToString() ?? "",
                                description = world["description"]?.ToString() ?? "",
                                imageUrl = world["imageUrl"]?.ToString() ?? "",
                                thumbnailImageUrl = world["thumbnailImageUrl"]?.ToString() ?? "",
                                authorName = world["authorName"]?.ToString() ?? "",
                                authorId = world["authorId"]?.ToString() ?? "",
                                occupants = world["occupants"]?.Value<int>() ?? 0,
                                publicOccupants = world["publicOccupants"]?.Value<int>() ?? 0,
                                privateOccupants = world["privateOccupants"]?.Value<int>() ?? 0,
                                capacity = world["capacity"]?.Value<int>() ?? 0,
                                recommendedCapacity = world["recommendedCapacity"]?.Value<int>() ?? 0,
                                favorites = world["favorites"]?.Value<int>() ?? 0,
                                visits = world["visits"]?.Value<int>() ?? 0,
                                tags,
                                instances,
                                worldTimeSeconds = wTimeSeconds,
                                worldVisitCount = wVisitCount,
                            }));
                        });
                    }
                    break;

                // Favorite Friends
                case "vrcGetFavoriteFriends":
                    _ = LoadFavoriteFriendsAsync();
                    break;

                case "vrcAddFavoriteFriend":
                {
                    var uid = msg["userId"]?.ToString() ?? "";
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var result = await _vrcApi.AddFavoriteFriendAsync(uid);
                            if (result == null) return;
                            var fvrtId = result["id"]?.ToString() ?? "";
                            if (string.IsNullOrEmpty(fvrtId)) return;
                            lock (_favoriteFriends) _favoriteFriends[uid] = fvrtId;
                            Invoke(() => SendToJS("vrcFavoriteFriendToggled", new { userId = uid, fvrtId, isFavorited = true }));
                        }
                        catch { }
                    });
                    break;
                }

                case "vrcRemoveFavoriteFriend":
                {
                    var uid    = msg["userId"]?.ToString() ?? "";
                    var fvrtId = msg["fvrtId"]?.ToString() ?? "";
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var ok = await _vrcApi.RemoveFavoriteFriendAsync(fvrtId);
                            if (!ok) return;
                            lock (_favoriteFriends) _favoriteFriends.Remove(uid);
                            Invoke(() => SendToJS("vrcFavoriteFriendToggled", new { userId = uid, fvrtId = "", isFavorited = false }));
                        }
                        catch { }
                    });
                    break;
                }

                // Groups - my groups, join, leave
                case "vrcGetFavoriteWorlds":
                    _ = Task.Run(async () =>
                    {
                        if (_settings.FfcEnabled)
                        {
                            var cachedFavWorlds = _cache.LoadRaw(CacheHandler.KeyFavWorlds);
                            if (cachedFavWorlds != null) Invoke(() => SendToJS("vrcFavoriteWorlds", cachedFavWorlds));
                        }
                        await FetchAndCacheFavWorldsAsync();
                    });
                    break;

                case "vrcUpdateFavoriteGroup":
                    _ = Task.Run(async () =>
                    {
                        var groupType = msg["groupType"]?.ToString() ?? "world";
                        var groupName = msg["groupName"]?.ToString() ?? "";
                        var displayName = msg["displayName"]?.ToString() ?? "";
                        var ok = await _vrcApi.UpdateFavoriteGroupAsync(groupType, groupName, displayName);
                        Invoke(() => SendToJS("vrcFavoriteGroupUpdated", new { ok, groupName, displayName }));
                    });
                    break;

                case "vrcGetWorldFavGroups":
                    _ = Task.Run(async () =>
                    {
                        var groups = await _vrcApi.GetFavoriteGroupsAsync();
                        var worldTypes = new HashSet<string> { "world", "vrcPlusWorld" };
                        var groupList = groups
                            .Where(g => worldTypes.Contains(g["type"]?.ToString() ?? ""))
                            .Select(g => new WFavGroup {
                                name        = g["name"]?.ToString() ?? "",
                                displayName = g["displayName"]?.ToString() ?? "",
                                type        = g["type"]?.ToString() ?? "world"
                            })
                            .Where(g => !string.IsNullOrEmpty(g.name))
                            .ToList();
                        groupList = FillMissingWorldSlots(groupList);
                        Invoke(() => SendToJS("vrcWorldFavGroups", groupList));
                    });
                    break;

                case "vrcAddWorldFavorite":
                    _ = Task.Run(async () =>
                    {
                        var worldId   = msg["worldId"]?.ToString() ?? "";
                        var groupName = msg["groupName"]?.ToString() ?? "";
                        var groupType = msg["groupType"]?.ToString() ?? "world";
                        var oldFvrtId = msg["oldFvrtId"]?.ToString();
                        var (ok, resultData) = await _vrcApi.AddWorldFavoriteAsync(worldId, groupName, groupType, oldFvrtId);
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
                        var ok = await _vrcApi.RemoveFavoriteFriendAsync(fvrtId);
                        Invoke(() => SendToJS("vrcWorldUnfavoriteResult", new { ok, worldId }));
                    });
                    break;
                }

                case "vrcGetAvatarFavGroups":
                    _ = Task.Run(async () =>
                    {
                        var groups = await _vrcApi.GetFavoriteGroupsAsync();
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
                        var (avOk, avResult) = await _vrcApi.AddAvatarFavoriteAsync(avId, avGroup, avType, avOldFvrt);
                        Invoke(() => SendToJS("vrcAvatarFavoriteResult", new { ok = avOk, avatarId = avId, groupName = avGroup, newFvrtId = avOk ? avResult : "", error = avOk ? "" : avResult }));
                    });
                    break;

                case "vrcRemoveAvatarFavorite":
                {
                    var avRmId   = msg["avatarId"]?.ToString() ?? "";
                    var avFvrtId = msg["fvrtId"]?.ToString() ?? "";
                    _ = Task.Run(async () =>
                    {
                        var ok = await _vrcApi.RemoveFavoriteFriendAsync(avFvrtId);
                        Invoke(() => SendToJS("vrcAvatarUnfavoriteResult", new { ok, avatarId = avRmId }));
                    });
                    break;
                }

                case "vrcGetMyGroups":
                    {
                        if (_settings.FfcEnabled)
                        {
                            var cachedGrps = _cache.LoadRaw(CacheHandler.KeyGroups);
                            if (cachedGrps != null) Invoke(() => SendToJS("vrcMyGroups", cachedGrps));
                        }
                        _ = Task.Run(FetchAndCacheGroupsAsync);
                    }
                    break;

                case "vrcGetGroup":
                    var ggId = msg["groupId"]?.ToString();
                    if (!string.IsNullOrEmpty(ggId))
                    {
                        _ = Task.Run(async () =>
                        {
                            var g = await _vrcApi.GetGroupAsync(ggId);
                            if (g != null)
                            {
                                // Fetch additional data in parallel
                                var postsTask = _vrcApi.GetGroupPostsAsync(ggId);
                                var instancesTask = _vrcApi.GetGroupInstancesAsync(ggId);
                                var membersTask = _vrcApi.GetGroupMembersAsync(ggId);
                                var eventsTask = _vrcApi.GetGroupEventsAsync(ggId);

                                await Task.WhenAll(postsTask, instancesTask, membersTask, eventsTask);

                                var posts = postsTask.Result;
                                var instances = instancesTask.Result;
                                var members = membersTask.Result;
                                var events = eventsTask.Result;

                                // Fetch gallery images for all galleries
                                var galleries = g["galleries"] as JArray ?? new JArray();
                                var galleryImages = new List<object>();
                                foreach (var gal in galleries)
                                {
                                    var galId = gal["id"]?.ToString();
                                    var galName = gal["name"]?.ToString() ?? "";
                                    if (!string.IsNullOrEmpty(galId))
                                    {
                                        var imgs = await _vrcApi.GetGroupGalleryImagesAsync(ggId, galId);
                                        foreach (var img in imgs)
                                        {
                                            galleryImages.Add(new {
                                                imageUrl = img["imageUrl"]?.ToString() ?? "",
                                                galleryName = galName,
                                                createdAt = img["createdAt"]?.ToString() ?? "",
                                            });
                                        }
                                    }
                                }

                                var myMember = g["myMember"] as JObject;
                                var myPerms = myMember?["permissions"] as JArray ?? new JArray();
                                var canPost = myPerms.Any(p => p.ToString() == "*" || p.ToString() == "group-posts-manage");

                                Invoke(() => SendToJS("vrcGroupDetail", new {
                                    id = g["id"]?.ToString() ?? "", name = g["name"]?.ToString() ?? "",
                                    shortCode = g["shortCode"]?.ToString() ?? "", description = g["description"]?.ToString() ?? "",
                                    iconUrl = g["iconUrl"]?.ToString() ?? "", bannerUrl = g["bannerUrl"]?.ToString() ?? "",
                                    memberCount = g["memberCount"]?.Value<int>() ?? 0, privacy = g["privacy"]?.ToString() ?? "",
                                    rules = g["rules"]?.ToString() ?? "",
                                    isJoined = g["myMember"] != null && g["myMember"].Type != JTokenType.Null,
                                    canPost,
                                    posts = posts.Select(p => new {
                                        id = p["id"]?.ToString() ?? "",
                                        title = p["title"]?.ToString() ?? "",
                                        text = p["text"]?.ToString() ?? "",
                                        imageUrl = p["imageUrl"]?.ToString() ?? "",
                                        createdAt = p["createdAt"]?.ToString() ?? "",
                                        authorId = p["authorId"]?.ToString() ?? "",
                                        visibility = p["visibility"]?.ToString() ?? "",
                                    }),
                                    groupEvents = events.Select(e => new {
                                        title = e["title"]?.ToString() ?? "",
                                        description = e["description"]?.ToString() ?? "",
                                        startDate = e["startsAt"]?.ToString() ?? e["startDate"]?.ToString() ?? "",
                                        endDate = e["endsAt"]?.ToString() ?? e["endDate"]?.ToString() ?? "",
                                        imageUrl = e["imageUrl"]?.ToString() ?? "",
                                        accessType = e["accessType"]?.ToString() ?? "",
                                    }),
                                    groupInstances = instances.Select(i => new {
                                        instanceId = i["instanceId"]?.ToString() ?? "",
                                        location = i["location"]?.ToString() ?? "",
                                        worldName = i["world"]?["name"]?.ToString() ?? "",
                                        worldThumb = i["world"]?["thumbnailImageUrl"]?.ToString() ?? i["world"]?["imageUrl"]?.ToString() ?? "",
                                        userCount = i["userCount"]?.Value<int>() ?? i["n_users"]?.Value<int>() ?? 0,
                                        capacity = i["world"]?["capacity"]?.Value<int>() ?? 0,
                                    }),
                                    galleryImages,
                                    groupMembers = members.Select(m => new {
                                        id = m["userId"]?.ToString() ?? "",
                                        displayName = m["user"]?["displayName"]?.ToString() ?? m["displayName"]?.ToString() ?? "",
                                        image = m["user"] is JObject gmu
                                            ? (VRChatApiService.GetUserImage(gmu) is var gi && gi.Length > 0 ? gi : gmu["thumbnailUrl"]?.ToString() ?? "")
                                            : "",
                                        status = m["user"]?["status"]?.ToString() ?? "",
                                        statusDescription = m["user"]?["statusDescription"]?.ToString() ?? "",
                                        role = m["roleIds"]?.FirstOrDefault()?.ToString() ?? "",
                                        joinedAt = m["joinedAt"]?.ToString() ?? "",
                                    }),
                                }));
                            }
                            else
                            {
                                Invoke(() => SendToJS("vrcGroupDetailError", new { error = $"Could not load group {ggId}" }));
                            }
                        });
                    }
                    break;

                case "vrcJoinGroup":
                    var jgId = msg["groupId"]?.ToString();
                    if (!string.IsNullOrEmpty(jgId))
                    {
                        _ = Task.Run(async () => {
                            var ok = await _vrcApi.JoinGroupAsync(jgId);
                            Invoke(() => SendToJS("vrcActionResult", new { action = "joinGroup", success = ok,
                                message = ok ? "Group join request sent!" : "Failed to join group" }));
                        });
                    }
                    break;

                case "vrcGetGroupMembers":
                    var gmId = msg["groupId"]?.ToString();
                    var gmOffset = msg["offset"]?.Value<int>() ?? 0;
                    if (!string.IsNullOrEmpty(gmId))
                    {
                        _ = Task.Run(async () => {
                            var members = await _vrcApi.GetGroupMembersAsync(gmId, 50, gmOffset);
                            var list = members.Select(m => new {
                                id = m["userId"]?.ToString() ?? "",
                                displayName = m["user"]?["displayName"]?.ToString() ?? m["displayName"]?.ToString() ?? "",
                                image = m["user"] is JObject gmu2
                                    ? (VRChatApiService.GetUserImage(gmu2) is var gi2 && gi2.Length > 0 ? gi2 : gmu2["thumbnailUrl"]?.ToString() ?? "")
                                    : "",
                                status = m["user"]?["status"]?.ToString() ?? "",
                                statusDescription = m["user"]?["statusDescription"]?.ToString() ?? "",
                                role = m["roleIds"]?.FirstOrDefault()?.ToString() ?? "",
                                joinedAt = m["joinedAt"]?.ToString() ?? "",
                            }).ToList();
                            Invoke(() => SendToJS("vrcGroupMembersPage", new {
                                groupId = gmId, offset = gmOffset, members = list,
                                hasMore = members.Count >= 50,
                            }));
                        });
                    }
                    break;

                case "vrcLeaveGroup":
                    var lgId = msg["groupId"]?.ToString();
                    if (!string.IsNullOrEmpty(lgId))
                    {
                        _ = Task.Run(async () => {
                            var ok = await _vrcApi.LeaveGroupAsync(lgId);
                            Invoke(() => SendToJS("vrcActionResult", new { action = "leaveGroup", success = ok,
                                message = ok ? "Left group" : "Failed to leave group" }));
                        });
                    }
                    break;

                case "vrcCreateGroupPost":
                {
                    var cpGroupId = msg["groupId"]?.ToString() ?? "";
                    var cpTitle = msg["title"]?.ToString() ?? "";
                    var cpText = msg["text"]?.ToString() ?? "";
                    var cpVisibility = msg["visibility"]?.ToString() ?? "group";
                    var cpNotify = msg["sendNotification"]?.Value<bool>() ?? false;
                    var cpImageBase64 = msg["imageBase64"]?.ToString();
                    var cpImageFileId = msg["imageFileId"]?.ToString(); // direct file_xxx from library picker
                    if (!string.IsNullOrEmpty(cpGroupId) && !string.IsNullOrEmpty(cpTitle))
                    {
                        _ = Task.Run(async () =>
                        {
                            string? imageId = null;
                            if (!string.IsNullOrEmpty(cpImageFileId))
                            {
                                // Using an existing gallery photo — no upload needed
                                imageId = cpImageFileId;
                                Invoke(() => SendToJS("log", new { msg = $"[GroupPost] Using library image: {imageId}", color = "sec" }));
                            }
                            else if (!string.IsNullOrEmpty(cpImageBase64))
                            {
                                try
                                {
                                    // Extract MIME type and strip data URL prefix (e.g. "data:image/jpeg;base64,...")
                                    var b64 = cpImageBase64;
                                    string imgMime = "image/png";
                                    string imgExt = ".png";
                                    if (b64.StartsWith("data:"))
                                    {
                                        var semi = b64.IndexOf(';');
                                        if (semi > 5) imgMime = b64[5..semi];
                                        imgExt = imgMime switch
                                        {
                                            "image/jpeg" => ".jpg",
                                            "image/gif"  => ".gif",
                                            "image/webp" => ".webp",
                                            _            => ".png"
                                        };
                                    }
                                    var commaIdx = b64.IndexOf(',');
                                    if (commaIdx >= 0) b64 = b64[(commaIdx + 1)..];
                                    var imgBytes = Convert.FromBase64String(b64);
                                    Invoke(() => SendToJS("log", new { msg = $"[GroupPost] Uploading image {imgMime} {imgBytes.Length / 1024} KB", color = "sec" }));
                                    imageId = await _vrcApi.UploadImageAsync(imgBytes, imgMime, imgExt);
                                    if (imageId == null)
                                        Invoke(() => SendToJS("log", new { msg = "[GroupPost] Image upload failed, posting without image", color = "warn" }));
                                    else
                                        Invoke(() => SendToJS("log", new { msg = $"[GroupPost] Image uploaded: {imageId}", color = "sec" }));
                                }
                                catch (Exception ex)
                                {
                                    Invoke(() => SendToJS("log", new { msg = $"[GroupPost] Image parse error: {ex.Message}", color = "err" }));
                                }
                            }
                            var ok = await _vrcApi.CreateGroupPostAsync(cpGroupId, cpTitle, cpText, cpVisibility, cpNotify, imageId);
                            Invoke(() => SendToJS("vrcActionResult", new
                            {
                                action = "createGroupPost",
                                success = ok,
                                message = ok ? "Post created!" : "Failed to create post"
                            }));
                        });
                    }
                    break;
                }

                case "vrcDeleteGroupPost":
                {
                    var dgpGroupId = msg["groupId"]?.ToString() ?? "";
                    var dgpPostId  = msg["postId"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(dgpGroupId) && !string.IsNullOrEmpty(dgpPostId))
                    {
                        _ = Task.Run(async () =>
                        {
                            var ok = await _vrcApi.DeleteGroupPostAsync(dgpGroupId, dgpPostId);
                            Invoke(() => SendToJS("vrcActionResult", new { action = "deleteGroupPost", success = ok, postId = dgpPostId }));
                        });
                    }
                    break;
                }

                case "vrcGetMutualsForNetwork":
                {
                    var mnUid = msg["userId"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(mnUid))
                    {
                        _ = Task.Run(async () =>
                        {
                            var (arr, optedOut) = await _vrcApi.GetUserMutualsAsync(mnUid);
                            var ids = optedOut ? Array.Empty<string>()
                                               : arr.Select(m => m["id"]?.ToString() ?? "").Where(s => s != "").ToArray();
                            Invoke(() => SendToJS("vrcMutualsForNetwork", new { userId = mnUid, mutualIds = ids, optedOut }));
                        });
                    }
                    break;
                }

                case "vrcSaveMutualCache":
                {
                    var mcJson = msg["cache"]?.ToString() ?? "{}";
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCNext");
                            Directory.CreateDirectory(dir);
                            File.WriteAllText(Path.Combine(dir, "mutual_cache.json"), mcJson, System.Text.Encoding.UTF8);
                        }
                        catch { /* non-critical */ }
                    });
                    break;
                }

                case "vrcLoadMutualCache":
                {
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCNext", "mutual_cache.json");
                            var json = File.Exists(path) ? File.ReadAllText(path, System.Text.Encoding.UTF8) : "{}";
                            Invoke(() => SendToJS("vrcMutualCacheLoaded", new { json }));
                        }
                        catch
                        {
                            Invoke(() => SendToJS("vrcMutualCacheLoaded", new { json = "{}" }));
                        }
                    });
                    break;
                }

                case "vrcClearMutualCache":
                {
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCNext", "mutual_cache.json");
                            if (File.Exists(path)) File.Delete(path);
                        }
                        catch { /* non-critical */ }
                    });
                    break;
                }

                case "vrcGetTimeSpent":
                {
                    var tsMyId = _vrcApi.CurrentUserId ?? "";
                    _ = Task.Run(() =>
                    {
                        var stats = _timeline.GetTimeSpentStats(tsMyId);

                        // Build name/image/meets lookup from timeline (covers historical data)
                        var tlPersons = stats.Persons.ToDictionary(p => p.UserId);
                        var tlWorlds  = stats.Worlds.ToDictionary(w => w.WorldId);

                        // PERSONS: start from ALL UserTimeTracker users so nobody is missed.
                        // liveElapsed: time since last Tick not yet counted for co-present users
                        var logPlayerIds = new HashSet<string>(
                            _logWatcher.GetCurrentPlayers()
                                .Where(p => !string.IsNullOrEmpty(p.UserId))
                                .Select(p => p.UserId));
                        var rawLiveElapsed = (long)(DateTime.UtcNow - _timeTracker.LastTick).TotalSeconds;
                        var liveElapsed = rawLiveElapsed > 0 && rawLiveElapsed <= 3600 ? rawLiveElapsed : 0;

                        var personList = _timeTracker.Users
                            .Where(kv => kv.Key != tsMyId)
                            .Select(kv =>
                            {
                                var isCoPresent = logPlayerIds.Contains(kv.Key);
                                // Effective seconds = stored + live pending (if currently in same instance)
                                var effectiveSec = kv.Value.TotalSeconds + (isCoPresent ? liveElapsed : 0);
                                if (effectiveSec <= 0) return default; // skip zero-time entries

                                tlPersons.TryGetValue(kv.Key, out var tl);
                                // Priority: live caches (correct) → UserRecord → timeline → friendStore
                                var name  = !string.IsNullOrEmpty(kv.Value.DisplayName) ? kv.Value.DisplayName
                                          : tl?.DisplayName ?? "";
                                var image = ResolvePlayerImage(kv.Key, null);
                                if (string.IsNullOrEmpty(image))
                                {
                                    image = !string.IsNullOrEmpty(kv.Value.Image) ? kv.Value.Image
                                          : tl?.Image ?? "";
                                }
                                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(image))
                                {
                                    lock (_friendStore)
                                    {
                                        if (_friendStore.TryGetValue(kv.Key, out var fj))
                                        {
                                            if (string.IsNullOrEmpty(name))  name  = fj["displayName"]?.ToString() ?? "";
                                            if (string.IsNullOrEmpty(image)) image = VRChatApiService.GetUserImage(fj);
                                        }
                                    }
                                }
                                if (string.IsNullOrEmpty(name)) return default; // truly unknown, skip
                                return (UserId: kv.Key, DisplayName: name, Image: image,
                                        Seconds: effectiveSec, Meets: tl?.Meets ?? 0);
                            })
                            .Where(p => p.UserId != null)
                            .OrderByDescending(p => p.Seconds)
                            .Take(200)
                            .ToList();

                        // WORLDS: flush pending time before reading so the displayed value is current.
                        _worldTimeTracker.Tick();
                        _worldTimeTracker.Save();

                        // Start from ALL WorldTimeTracker worlds.
                        // Same issue — timeline top-200 could miss recently visited worlds.
                        var worldList = _worldTimeTracker.Worlds
                            .Select(kv =>
                            {
                                tlWorlds.TryGetValue(kv.Key, out var tl);
                                // WorldTimeTracker now stores name/thumb (updated after 15s API call)
                                // Fall back to timeline lookup for older entries
                                var name  = !string.IsNullOrEmpty(kv.Value.WorldName)  ? kv.Value.WorldName  : (tl?.WorldName  ?? "");
                                var thumb = !string.IsNullOrEmpty(kv.Value.WorldThumb) ? kv.Value.WorldThumb : (tl?.WorldThumb ?? "");
                                var visits = kv.Value.VisitCount > 0 ? kv.Value.VisitCount : (tl?.Visits ?? 0);
                                return (WorldId: kv.Key, WorldName: name, WorldThumb: thumb,
                                        Seconds: kv.Value.TotalSeconds, Visits: visits);
                            })
                            .Where(w => !string.IsNullOrEmpty(w.WorldName)) // skip worlds with no name yet
                            .OrderByDescending(w => w.Seconds)
                            .Take(200)
                            .ToList();

                        Invoke(() => SendToJS("vrcTimeSpentData", new
                        {
                            totalSeconds = stats.TotalSeconds,
                            worlds = worldList.Select(w => new
                            {
                                worldId    = w.WorldId,
                                worldName  = w.WorldName,
                                worldThumb = w.WorldThumb,
                                seconds    = w.Seconds,
                                visits     = w.Visits,
                            }),
                            persons = personList.Select(p => new
                            {
                                userId      = p.UserId,
                                displayName = p.DisplayName,
                                image       = p.Image,
                                seconds     = p.Seconds,
                                meets       = p.Meets,
                            }),
                        }));
                    });
                    break;
                }

                case "vrcCreateGroupInstance":
                {
                    var cgiWorldId = msg["worldId"]?.ToString() ?? "";
                    var cgiGroupId = msg["groupId"]?.ToString() ?? "";
                    var cgiAccessType = msg["groupAccessType"]?.ToString() ?? "members";
                    var cgiRegion = msg["region"]?.ToString() ?? "eu";
                    if (!string.IsNullOrEmpty(cgiWorldId) && !string.IsNullOrEmpty(cgiGroupId))
                    {
                        _ = Task.Run(async () =>
                        {
                            var location = await _vrcApi.CreateGroupInstanceAsync(cgiWorldId, cgiGroupId, cgiAccessType, cgiRegion);
                            if (!string.IsNullOrEmpty(location))
                            {
                                var ok = await _vrcApi.InviteSelfAsync(location);
                                Invoke(() => SendToJS("vrcActionResult", new
                                {
                                    action = "createInstance",
                                    success = ok,
                                    message = ok ? "Group instance created! Self-invite sent." : "Instance created but invite failed.",
                                    location
                                }));
                            }
                            else
                            {
                                Invoke(() => SendToJS("vrcActionResult", new
                                {
                                    action = "createInstance",
                                    success = false,
                                    message = "Failed to create group instance."
                                }));
                            }
                        });
                    }
                    break;
                }

                // Custom Chatbox OSC
                case "chatboxConfig":
                    {
                        _chatbox ??= new ChatboxService(s => Invoke(() => SendToJS("log", new { msg = s, color = "sec" })));
                        _chatbox.SetUpdateCallback(data => {
                            try { Invoke(() => SendToJS("chatboxUpdate", data)); } catch { }
                        });

                        var enabled = msg["enabled"]?.Value<bool>() ?? false;
                        var showTime = msg["showTime"]?.Value<bool>() ?? true;
                        var showMedia = msg["showMedia"]?.Value<bool>() ?? true;
                        var showPlaytime = msg["showPlaytime"]?.Value<bool>() ?? true;
                        var showCustomText = msg["showCustomText"]?.Value<bool>() ?? true;
                        var showSystemStats = msg["showSystemStats"]?.Value<bool>() ?? false;
                        var showAfk = msg["showAfk"]?.Value<bool>() ?? false;
                        var afkMessage = msg["afkMessage"]?.ToString() ?? "Currently AFK";
                        var suppressSound = msg["suppressSound"]?.Value<bool>() ?? true;
                        var timeFormat = msg["timeFormat"]?.ToString() ?? "hh:mm tt";
                        var separator = msg["separator"]?.ToString() ?? " | ";
                        var intervalMs = msg["intervalMs"]?.Value<int>() ?? 5000;
                        var customLines = msg["customLines"]?.ToObject<List<string>>() ?? new();

                        _chatbox.ApplyConfig(enabled, showTime, showMedia, showPlaytime,
                            showCustomText, showSystemStats, showAfk, afkMessage,
                            suppressSound, timeFormat, separator, intervalMs, customLines);

                        // Persist chatbox settings
                        _settings.CbShowTime = showTime;
                        _settings.CbShowMedia = showMedia;
                        _settings.CbShowPlaytime = showPlaytime;
                        _settings.CbShowCustomText = showCustomText;
                        _settings.CbShowSystemStats = showSystemStats;
                        _settings.CbShowAfk = showAfk;
                        _settings.CbAfkMessage = afkMessage;
                        _settings.CbSuppressSound = suppressSound;
                        _settings.CbTimeFormat = timeFormat;
                        _settings.CbSeparator = separator;
                        _settings.CbIntervalMs = intervalMs;
                        _settings.CbCustomLines = customLines;
                        _settings.Save();
                    }
                    break;

                case "chatboxStop":
                    _chatbox?.Stop();
                    break;

                // Space Flight
                case "sfConnect":
                    {
                        _steamVR ??= new SteamVRService(s => Invoke(() => SendToJS("log", new { msg = s, color = "sec" })));
                        _steamVR.SetUpdateCallback(data => {
                            try { Invoke(() => SendToJS("sfUpdate", data)); } catch { }
                        });
                        if (_steamVR.Connect())
                        {
                            _steamVR.ApplyConfig(_settings.SfMultiplier, _settings.SfLockX, _settings.SfLockY, _settings.SfLockZ,
                                _settings.SfLeftHand, _settings.SfRightHand, _settings.SfUseGrip);
                            _steamVR.StartPolling();
                        }
                    }
                    break;
                case "sfDisconnect":
                    _steamVR?.Disconnect();
                    SendToJS("sfUpdate", new { connected = false, dragging = false, offsetX = 0, offsetY = 0, offsetZ = 0,
                        leftController = false, rightController = false, error = (string?)null });
                    break;
                case "sfReset":
                    _steamVR?.ResetOffset();
                    break;
                case "sfConfig":
                    {
                        var mult = msg["dragMultiplier"]?.Value<float>() ?? 1f;
                        var lx = msg["lockX"]?.Value<bool>() ?? false;
                        var ly = msg["lockY"]?.Value<bool>() ?? false;
                        var lz = msg["lockZ"]?.Value<bool>() ?? false;
                        var lh = msg["leftHand"]?.Value<bool>() ?? false;
                        var rh = msg["rightHand"]?.Value<bool>() ?? true;
                        var grip = msg["useGrip"]?.Value<bool>() ?? true;
                        _steamVR?.ApplyConfig(mult, lx, ly, lz, lh, rh, grip);
                    }
                    break;

                // OSC Tool
                case "oscConnect":
                    {
                        _osc ??= new OscService(s => Invoke(() => SendToJS("log", new { msg = s, color = "sec" })));
                        _osc.SetParamCallback((name, val, type) => {
                            try { Invoke(() => SendToJS("oscParam", new { name, value = val, type })); } catch { }
                        });
                        _osc.SetAvatarChangeCallback((avatarId, paramDefs) => {
                            try
                            {
                                var paramList = paramDefs.Select(p => new { p.Name, p.Type, p.HasInput, p.HasOutput }).ToList();
                                Invoke(() => SendToJS("oscAvatarParams", new { avatarId, paramList }));
                            }
                            catch { }
                        });
                        bool oscOk = _osc.Start();
                        SendToJS("oscState", new { connected = oscOk });
                        if (oscOk)
                        {
                            _ = Task.Run(async () =>
                            {
                                // Try OSCQuery first; gets all live values instantly (VRChat v2023.3.1+)
                                bool gotLive = await _osc.TryOscQueryAsync((name, val, type) =>
                                {
                                    try { Invoke(() => SendToJS("oscParam", new { name, value = val, type })); } catch { }
                                });
                                // Fallback: load config file as pending params so the full list is visible
                                if (!gotLive)
                                {
                                    var (avatarId, paramDefs) = _osc.LoadMostRecentAvatarConfig();
                                    if (paramDefs.Count > 0)
                                    {
                                        var paramList = paramDefs.Select(p => new { p.Name, p.Type, p.HasInput, p.HasOutput }).ToList();
                                        Invoke(() => SendToJS("oscAvatarParams", new { avatarId, paramList }));
                                    }
                                }
                            });
                        }
                    }
                    break;
                case "oscDisconnect":
                    _osc?.Stop();
                    SendToJS("oscState", new { connected = false });
                    break;
                case "oscSend":
                    {
                        var pName = msg["name"]?.ToString() ?? "";
                        var pType = msg["type"]?.ToString() ?? "";
                        if (_osc?.IsConnected != true)
                        {
                            SendToJS("log", new { msg = $"[OSC] Send skipped — not connected (osc={_osc != null}, running={_osc?.IsConnected})", color = "err" });
                        }
                        else if (!string.IsNullOrEmpty(pName))
                        {
                            if (pType == "bool") _osc.SendBool(pName, msg["value"]?.Value<bool>() ?? false);
                            else if (pType == "float") _osc.SendFloat(pName, msg["value"]?.Value<float>() ?? 0f);
                            else if (pType == "int") _osc.SendInt(pName, msg["value"]?.Value<int>() ?? 0);
                        }
                    }
                    break;
                case "oscEnableOutputs":
                    {
                        int filesUpdated = _osc != null ? _osc.EnableAllOutputs()
                            : new OscService(s => { }).EnableAllOutputs();
                        SendToJS("oscOutputsEnabled", new { filesUpdated });
                    }
                    break;

                // VRCVideoCacher
                case "vcCheck":
                    SendToJS("vcState", GetVcState());
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
                case "vcSend":
                    break;

                // Friend request
                case "vrcSendFriendRequest":
                    var frUid = msg["userId"]?.ToString();
                    if (!string.IsNullOrEmpty(frUid))
                    {
                        _ = Task.Run(async () => {
                            var ok = await _vrcApi.SendFriendRequestAsync(frUid);
                            Invoke(() => SendToJS("vrcActionResult", new { action = "friendRequest", success = ok,
                                message = ok ? "Friend request sent!" : "Failed to send request" }));
                        });
                    }
                    break;

                case "vrcUnfriend":
                    var ufUid = msg["userId"]?.ToString();
                    if (!string.IsNullOrEmpty(ufUid))
                    {
                        _ = Task.Run(async () => {
                            var ok = await _vrcApi.UnfriendAsync(ufUid);
                            Invoke(() => {
                                SendToJS("vrcActionResult", new { action = "unfriend", success = ok,
                                    message = ok ? "Unfriended" : "Failed to unfriend" });
                                if (ok) SendToJS("vrcUnfriendDone", new { userId = ufUid });
                            });
                        });
                    }
                    break;

                case "vrcGetBlocked":
                    _ = Task.Run(async () => {
                        var arr = await _vrcApi.GetPlayerModerationsAsync("block");
                        await EnrichModerationsWithImagesAsync(arr);
                        Invoke(() => SendToJS("vrcBlockedList", arr));
                    });
                    break;

                case "vrcGetMuted":
                    _ = Task.Run(async () => {
                        var arr = await _vrcApi.GetPlayerModerationsAsync("mute");
                        await EnrichModerationsWithImagesAsync(arr);
                        Invoke(() => SendToJS("vrcMutedList", arr));
                    });
                    break;

                case "vrcBlock":
                    var blUid = msg["userId"]?.ToString();
                    if (!string.IsNullOrEmpty(blUid))
                    {
                        _ = Task.Run(async () => {
                            var ok = await _vrcApi.ModerateUserAsync(blUid, "block");
                            Invoke(() => {
                                SendToJS("vrcActionResult", new { action = "block", success = ok,
                                    message = ok ? "Blocked" : "Failed to block" });
                                if (ok) SendToJS("vrcModDone", new { userId = blUid, type = "block", active = true });
                            });
                        });
                    }
                    break;

                case "vrcMute":
                    var muteUid = msg["userId"]?.ToString();
                    if (!string.IsNullOrEmpty(muteUid))
                    {
                        _ = Task.Run(async () => {
                            var ok = await _vrcApi.ModerateUserAsync(muteUid, "mute");
                            Invoke(() => {
                                SendToJS("vrcActionResult", new { action = "mute", success = ok,
                                    message = ok ? "Muted" : "Failed to mute" });
                                if (ok) SendToJS("vrcModDone", new { userId = muteUid, type = "mute", active = true });
                            });
                        });
                    }
                    break;

                case "vrcUnblock":
                    var ubUid = msg["userId"]?.ToString();
                    if (!string.IsNullOrEmpty(ubUid))
                    {
                        _ = Task.Run(async () => {
                            var ok = await _vrcApi.UnmoderateUserAsync(ubUid, "block");
                            Invoke(() => {
                                SendToJS("vrcActionResult", new { action = "unblock", success = ok,
                                    message = ok ? "Unblocked" : "Failed to unblock" });
                                if (ok) SendToJS("vrcModDone", new { userId = ubUid, type = "block", active = false });
                            });
                        });
                    }
                    break;

                case "vrcUnmute":
                    var umUid = msg["userId"]?.ToString();
                    if (!string.IsNullOrEmpty(umUid))
                    {
                        _ = Task.Run(async () => {
                            var ok = await _vrcApi.UnmoderateUserAsync(umUid, "mute");
                            Invoke(() => {
                                SendToJS("vrcActionResult", new { action = "unmute", success = ok,
                                    message = ok ? "Unmuted" : "Failed to unmute" });
                                if (ok) SendToJS("vrcModDone", new { userId = umUid, type = "mute", active = false });
                            });
                        });
                    }
                    break;

                case "vrcBoop":
                    var boopUid = msg["userId"]?.ToString();
                    if (!string.IsNullOrEmpty(boopUid))
                    {
                        _ = Task.Run(async () => {
                            var ok = await _vrcApi.SendBoopAsync(boopUid);
                            Invoke(() => SendToJS("vrcActionResult", new { action = "boop", success = ok,
                                message = ok ? "Booped!" : "Failed to boop" }));
                        });
                    }
                    break;

                // Notifications
                case "vrcGetNotifications":
                    _ = VrcGetNotificationsAsync();
                    break;

                // App updates
                case "checkUpdate":
                    _ = Task.Run(async () =>
                    {
                        var version = await _updateService.CheckAsync();
                        if (version != null)
                            Invoke(() => SendToJS("updateAvailable", new { version }));
                    });
                    break;

                case "installUpdate":
                    _ = Task.Run(async () =>
                    {
                        await _updateService.DownloadAsync(p =>
                            Invoke(() => SendToJS("updateProgress", p)));
                        Invoke(() => SendToJS("updateReady", null));
                        await Task.Delay(800);
                        Invoke(() => _updateService.ApplyAndRestart());
                    });
                    break;

                case "vrcAcceptNotification":
                {
                    var anId   = msg["notifId"]?.ToString();
                    var anType = msg["type"]?.ToString();
                    var anIsV2 = msg["_v2"]?.Value<bool>() ?? false;

                    // details: nested JObject or JSON-encoded string (v1)
                    JObject? anDet = null;
                    { var rawDet = msg["details"];
                      if (rawDet is JObject d1) anDet = d1;
                      else if (rawDet?.Type == JTokenType.String) try { anDet = JObject.Parse(rawDet.ToString()); } catch { } }

                    // _data: v2 group-specific payload (groupId, requestUserId, etc.)
                    JObject? anData = null;
                    { var rawData = msg["_data"];
                      if (rawData is JObject d2) anData = d2;
                      else if (rawData?.Type == JTokenType.String) try { anData = JObject.Parse(rawData.ToString()); } catch { } }

                    var anLink = msg["_link"]?.ToString();
                    SendToJS("log", new { msg = $"AcceptNotif: type={anType} v2={anIsV2} det={anDet?.ToString(Newtonsoft.Json.Formatting.None)??"null"} data={anData?.ToString(Newtonsoft.Json.Formatting.None)??"null"}", color = "ok" });

                    if (!string.IsNullOrEmpty(anId))
                    {
                        if (anType == "invite")
                        {
                            // World invite: join the instance
                            var invLoc = anDet?["worldId"]?.ToString();
                            if (!string.IsNullOrEmpty(invLoc) && invLoc.Contains(":"))
                            {
                                if (IsVrcRunning())
                                {
                                    _ = Task.Run(async () => {
                                        var ok = await _vrcApi.InviteSelfAsync(invLoc);
                                        await _vrcApi.AcceptNotificationAsync(anId);
                                        Invoke(() => SendToJS("vrcActionResult", new { action = "acceptNotif", success = ok,
                                            message = ok ? "Joining world... Check VRChat." : "Failed to join." }));
                                    });
                                }
                                else
                                {
                                    Invoke(() => SendToJS("vrcLaunchNeeded", new { location = invLoc, steamVr = IsSteamVrRunning() }));
                                }
                                break;
                            }
                        }
                        else if (anType == "group.invite")
                        {
                            // Group invite: join via Groups API (not notification accept endpoint)
                            var groupId = anDet?["groupId"]?.ToString()
                                       ?? anData?["groupId"]?.ToString()
                                       ?? ExtractGroupIdFromLink(anLink);
                            if (!string.IsNullOrEmpty(groupId))
                            {
                                _ = Task.Run(async () => {
                                    var ok = await _vrcApi.JoinGroupAsync(groupId);
                                    await _vrcApi.HideNotificationAsync(anId, anIsV2);
                                    Invoke(() => SendToJS("vrcActionResult", new { action = "acceptNotif", success = ok,
                                        message = ok ? "Group joined!" : "Failed to join group.", groupJoined = ok }));
                                    if (ok) _ = VrcGetNotificationsAsync();
                                });
                                break;
                            }
                        }
                        else if (anType == "group.joinRequest")
                        {
                            // Someone wants to join your group — approve via Groups API
                            var groupId       = anDet?["groupId"]?.ToString()
                                             ?? anData?["groupId"]?.ToString()
                                             ?? ExtractGroupIdFromLink(anLink);
                            var groupShortCode = anData?["groupName"]?.ToString() ?? anDet?["groupName"]?.ToString();
                            var requestUser   = anDet?["requestUserId"]?.ToString()
                                             ?? anData?["requestUserId"]?.ToString()
                                             ?? anDet?["userId"]?.ToString()
                                             ?? anData?["userId"]?.ToString()
                                             ?? msg["senderId"]?.ToString();
                            _ = Task.Run(async () => {
                                // Resolve groupId via shortCode lookup if not directly in payload
                                var resolvedGroupId = groupId;
                                if (string.IsNullOrEmpty(resolvedGroupId) && !string.IsNullOrEmpty(groupShortCode))
                                    resolvedGroupId = await _vrcApi.FindGroupIdByShortCodeAsync(groupShortCode);
                                if (!string.IsNullOrEmpty(resolvedGroupId) && !string.IsNullOrEmpty(requestUser))
                                {
                                    var ok = await _vrcApi.RespondGroupJoinRequestAsync(resolvedGroupId, requestUser, "accept");
                                    await _vrcApi.HideNotificationAsync(anId, anIsV2);
                                    Invoke(() => SendToJS("vrcActionResult", new { action = "acceptNotif", success = ok,
                                        message = ok ? "Join request approved!" : "Failed to approve." }));
                                    if (ok) _ = VrcGetNotificationsAsync();
                                }
                                else
                                {
                                    Invoke(() => SendToJS("log", new { msg = $"group.joinRequest: could not resolve groupId (shortCode={groupShortCode}) or requestUser", color = "warn" }));
                                }
                            });
                            break;
                        }
                        else if (anType == "friendRequest")
                        {
                            // Friend request: v1 notification accept endpoint
                            _ = Task.Run(async () => {
                                var ok = await _vrcApi.AcceptNotificationAsync(anId);
                                Invoke(() => SendToJS("vrcActionResult", new { action = "acceptNotif", success = ok,
                                    message = ok ? "Friend request accepted!" : "Failed." }));
                                if (ok) _ = VrcGetNotificationsAsync();
                            });
                            break;
                        }
                        else if (anType == "requestInvite")
                        {
                            // Someone asked for an invite — send them to our current world via POST /invite/{userId}
                            var requesterId = msg["senderId"]?.ToString();
                            _ = Task.Run(async () => {
                                bool ok = false;
                                if (!string.IsNullOrEmpty(requesterId))
                                    ok = await _vrcApi.InviteFriendAsync(requesterId);
                                // fallback: try notification accept endpoint
                                if (!ok)
                                    ok = await _vrcApi.AcceptNotificationAsync(anId);
                                else
                                    await _vrcApi.HideNotificationAsync(anId, anIsV2);
                                Invoke(() => SendToJS("vrcActionResult", new { action = "acceptNotif", success = ok,
                                    message = ok ? "Invite sent!" : "Failed. Are you in a world?" }));
                                if (ok) _ = VrcGetNotificationsAsync();
                            });
                            break;
                        }

                        // Fallback for any other acceptable type
                        _ = Task.Run(async () => {
                            var ok = await _vrcApi.AcceptNotificationAsync(anId);
                            Invoke(() => SendToJS("vrcActionResult", new { action = "acceptNotif", success = ok,
                                message = ok ? "Accepted!" : "Failed." }));
                        });
                    }
                    break;
                }

                case "vrcLaunchAndJoin":
                    var llLoc = msg["location"]?.ToString() ?? "";
                    var llVr  = msg["vr"]?.Value<bool>() ?? false;
                    {
                        var vrcExe = _settings.VrcPath;
                        if (!string.IsNullOrWhiteSpace(vrcExe) && File.Exists(vrcExe))
                        {
                            string llArgs;
                            if (!string.IsNullOrEmpty(llLoc))
                            {
                                var joinUri = VRChatApiService.BuildLaunchUri(llLoc);
                                llArgs = llVr ? $"\"{joinUri}\"" : $"--no-vr \"{joinUri}\"";
                            }
                            else
                            {
                                llArgs = llVr ? "" : "--no-vr";
                            }
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = vrcExe, Arguments = llArgs,
                                WorkingDirectory = Path.GetDirectoryName(vrcExe) ?? "",
                                UseShellExecute = false
                            });
                        }
                        else if (!string.IsNullOrEmpty(llLoc))
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = VRChatApiService.BuildLaunchUri(llLoc), UseShellExecute = true
                            });
                        }
                        else
                        {
                            SendToJS("log", new { msg = "VRChat path not configured. Set it in Settings.", color = "err" });
                            break;
                        }
                        foreach (var exe in _settings.ExtraExe)
                        {
                            try
                            {
                                if (File.Exists(exe))
                                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                    {
                                        FileName = exe,
                                        WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                                        UseShellExecute = true
                                    });
                            }
                            catch { }
                        }
                        var modeLabel = llVr ? "VR" : "Desktop";
                        var locLabel  = !string.IsNullOrEmpty(llLoc) ? $" → {llLoc}" : "";
                        SendToJS("vrcActionResult", new { action = "join", success = true, message = $"Launching VRChat ({modeLabel})..." });
                        SendToJS("log", new { msg = $"Launched VRChat [{modeLabel}]{locLabel}", color = "ok" });
                    }
                    break;

                case "vrcMarkNotifRead":
                    var mnId = msg["notifId"]?.ToString();
                    if (!string.IsNullOrEmpty(mnId))
                        _ = Task.Run(async () => await _vrcApi.MarkNotificationReadAsync(mnId));
                    break;

                case "vrcHideNotification":
                {
                    var hnId   = msg["notifId"]?.ToString();
                    var hnType = msg["type"]?.ToString();
                    var hnV2   = msg["_v2"]?.Value<bool>() ?? false;

                    JObject? hnDet = null;
                    { var r = msg["details"];
                      if (r is JObject d1) hnDet = d1;
                      else if (r?.Type == JTokenType.String) try { hnDet = JObject.Parse(r.ToString()); } catch { } }

                    JObject? hnData = null;
                    { var r = msg["_data"];
                      if (r is JObject d2) hnData = d2;
                      else if (r?.Type == JTokenType.String) try { hnData = JObject.Parse(r.ToString()); } catch { } }

                    var hnLink = msg["_link"]?.ToString();

                    if (!string.IsNullOrEmpty(hnId))
                    {
                        _ = Task.Run(async () =>
                        {
                            bool ok;
                            if (hnType == "group.joinRequest")
                            {
                                // Reject via Groups API — also hides the notification on VRChat's side
                                var groupId       = hnDet?["groupId"]?.ToString() ?? hnData?["groupId"]?.ToString()
                                                 ?? ExtractGroupIdFromLink(hnLink);
                                var groupShortCode = hnData?["groupName"]?.ToString() ?? hnDet?["groupName"]?.ToString();
                                var requestUser   = hnDet?["requestUserId"]?.ToString() ?? hnData?["requestUserId"]?.ToString()
                                                 ?? hnDet?["userId"]?.ToString()         ?? hnData?["userId"]?.ToString()
                                                 ?? msg["senderId"]?.ToString();
                                // Resolve groupId via shortCode lookup if not directly in payload
                                if (string.IsNullOrEmpty(groupId) && !string.IsNullOrEmpty(groupShortCode))
                                    groupId = await _vrcApi.FindGroupIdByShortCodeAsync(groupShortCode);
                                if (!string.IsNullOrEmpty(groupId) && !string.IsNullOrEmpty(requestUser))
                                    ok = await _vrcApi.RespondGroupJoinRequestAsync(groupId, requestUser, "reject");
                                else
                                    ok = await _vrcApi.HideNotificationAsync(hnId, hnV2);
                            }
                            else if (hnType == "group.invite")
                            {
                                // Decline invite: hide the notification (just dismiss it)
                                ok = await _vrcApi.HideNotificationAsync(hnId, hnV2);
                            }
                            else
                            {
                                ok = await _vrcApi.HideNotificationAsync(hnId, hnV2);
                            }
                            // Don't show "Failed" toast — notification is already removed locally
                            if (ok) Invoke(() => SendToJS("vrcActionResult", new { action = "hideNotif", success = true, message = "Declined" }));
                        });
                    }
                    break;
                }

                // Current instance
                case "vrcGetCurrentInstance":
                    _ = VrcGetCurrentInstanceAsync();
                    break;

                // User detail (for non-friend profile viewing)
                case "vrcGetUser":
                    var guId = msg["userId"]?.ToString();
                    if (!string.IsNullOrEmpty(guId))
                    {
                        _ = Task.Run(async () => {
                            var u = await _vrcApi.GetUserAsync(guId);
                            if (u != null) Invoke(() => SendToJS("vrcUserDetail", new {
                                id = u["id"]?.ToString() ?? "", displayName = u["displayName"]?.ToString() ?? "",
                                image = VRChatApiService.GetUserImage(u), status = u["status"]?.ToString() ?? "offline",
                                statusDescription = u["statusDescription"]?.ToString() ?? "",
                                bio = u["bio"]?.ToString() ?? "", location = u["location"]?.ToString() ?? "",
                                isFriend = u["isFriend"]?.Value<bool>() ?? false,
                                currentAvatarImageUrl = u["currentAvatarImageUrl"]?.ToString() ?? "",
                            }));
                        });
                    }
                    break;

                // Timeline
                case "getTimeline":
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Import any existing photos from PhotoPlayersStore not yet in timeline
                            await BootstrapPhotoTimeline();

                            var (events, hasMore) = _timeline.GetEventsPaged(100, 0);
                            var payload = events.Select(e => BuildTimelinePayload(e)).ToList();
                            Invoke(() => SendToJS("timelineData", new { events = payload, hasMore, offset = 0 }));

                            if (!_vrcApi.IsLoggedIn) return;
                            bool anyResolved = false;

                            // 1) Resolve missing world names (first page only)
                            var unknownWorlds = events
                                .Where(e => !string.IsNullOrEmpty(e.WorldId) && string.IsNullOrEmpty(e.WorldName))
                                .Select(e => e.WorldId).Distinct().Take(20).ToList();

                            foreach (var wid in unknownWorlds)
                            {
                                try
                                {
                                    var w = await _vrcApi.GetWorldAsync(wid);
                                    if (w != null)
                                    {
                                        var wName  = w["name"]?.ToString()              ?? "";
                                        var wThumb = w["thumbnailImageUrl"]?.ToString() ?? "";
                                        foreach (var ev in events
                                            .Where(e => e.WorldId == wid && string.IsNullOrEmpty(e.WorldName)))
                                        {
                                            _timeline.UpdateEvent(ev.Id, e => { e.WorldName = wName; e.WorldThumb = wThumb; });
                                            ev.WorldName  = wName;
                                            ev.WorldThumb = wThumb;
                                            anyResolved = true;
                                        }
                                    }
                                }
                                catch { }
                            }

                            // 2) Resolve missing user / player images (first page only)
                            var fetchedImgs   = new Dictionary<string, string>(); // userId -> imageUrl
                            var playerRefs    = new List<(string evId, string userId)>();
                            var userEventRefs = new List<(string evId, string userId)>();

                            foreach (var ev in events)
                            {
                                if (ev.Type == "instance_join")
                                {
                                    foreach (var p in ev.Players.Where(p =>
                                        string.IsNullOrEmpty(p.Image) && !string.IsNullOrEmpty(p.UserId)))
                                    {
                                        if (!fetchedImgs.ContainsKey(p.UserId)) fetchedImgs[p.UserId] = "";
                                        playerRefs.Add((ev.Id, p.UserId));
                                    }
                                }
                                else if (ev.Type is "first_meet" or "meet_again")
                                {
                                    if (string.IsNullOrEmpty(ev.UserImage) && !string.IsNullOrEmpty(ev.UserId))
                                    {
                                        if (!fetchedImgs.ContainsKey(ev.UserId)) fetchedImgs[ev.UserId] = "";
                                        userEventRefs.Add((ev.Id, ev.UserId));
                                    }
                                }
                            }

                            if (fetchedImgs.Count > 0)
                            {
                                // Batch-fetch with rate limiting (max 60 unique users, 3 concurrent)
                                var toFetch  = fetchedImgs.Keys.Take(60).ToList();
                                var sem      = new SemaphoreSlim(3);
                                var imgTasks = toFetch.Select(async uid =>
                                {
                                    await sem.WaitAsync();
                                    try
                                    {
                                        if (_playerImageCache.TryGetValue(uid, out var c) && !string.IsNullOrEmpty(c.image))
                                        {
                                            fetchedImgs[uid] = c.image;
                                            return;
                                        }
                                        var profile = await _vrcApi.GetUserAsync(uid);
                                        if (profile != null)
                                        {
                                            var img = VRChatApiService.GetUserImage(profile);
                                            if (!string.IsNullOrEmpty(img))
                                            {
                                                fetchedImgs[uid] = img;
                                                lock (_playerImageCache) _playerImageCache[uid] = (img, DateTime.Now);
                                            }
                                        }
                                        await Task.Delay(250);
                                    }
                                    finally { sem.Release(); }
                                });
                                await Task.WhenAll(imgTasks);

                                foreach (var (evId, uid) in playerRefs)
                                {
                                    if (!fetchedImgs.TryGetValue(uid, out var img) || string.IsNullOrEmpty(img)) continue;
                                    var localImg = img; var localUid = uid;
                                    _timeline.UpdateEvent(evId, ev =>
                                    {
                                        var p = ev.Players.FirstOrDefault(x => x.UserId == localUid);
                                        if (p != null && string.IsNullOrEmpty(p.Image)) p.Image = localImg;
                                    });
                                    var localEv = events.FirstOrDefault(e => e.Id == evId);
                                    if (localEv != null)
                                    {
                                        var p = localEv.Players.FirstOrDefault(x => x.UserId == uid);
                                        if (p != null && string.IsNullOrEmpty(p.Image)) p.Image = img;
                                    }
                                    anyResolved = true;
                                }
                                foreach (var (evId, uid) in userEventRefs)
                                {
                                    if (!fetchedImgs.TryGetValue(uid, out var img) || string.IsNullOrEmpty(img)) continue;
                                    var localImg = img;
                                    _timeline.UpdateEvent(evId, ev =>
                                    {
                                        if (string.IsNullOrEmpty(ev.UserImage)) ev.UserImage = localImg;
                                    });
                                    var localEv = events.FirstOrDefault(e => e.Id == evId);
                                    if (localEv != null && string.IsNullOrEmpty(localEv.UserImage)) localEv.UserImage = img;
                                    anyResolved = true;
                                }
                            }

                            if (anyResolved)
                            {
                                var updated = events.Select(e => BuildTimelinePayload(e)).ToList();
                                Invoke(() => SendToJS("timelineData", new { events = updated, hasMore, offset = 0 }));
                            }
                        }
                        catch (Exception ex)
                        {
                            Invoke(() => SendToJS("log", new { msg = $"[TIMELINE] Load error: {ex.Message}", color = "err" }));
                        }
                    });
                    break;

                case "getTimelinePage":
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var pageOffset = msg["offset"]?.Value<int>() ?? 0;
                            var (events, hasMore) = _timeline.GetEventsPaged(100, pageOffset);
                            var payload = events.Select(e => BuildTimelinePayload(e)).ToList();
                            Invoke(() => SendToJS("timelineData", new { events = payload, hasMore, offset = pageOffset }));
                        }
                        catch { }
                    });
                    break;

                case "searchTimeline":
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var srchQuery = msg["query"]?.ToString() ?? "";
                            var srchDate  = msg["date"]?.ToString() ?? "";
                            var events    = _timeline.SearchEvents(srchQuery, "", srchDate);
                            var payload   = events.Select(e => BuildTimelinePayload(e)).ToList();
                            Invoke(() => SendToJS("timelineSearchResults", new { events = payload, query = srchQuery, date = srchDate }));
                        }
                        catch { }
                    });
                    break;

                case "searchFriendTimeline":
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var srchQuery = msg["query"]?.ToString() ?? "";
                            var srchDate  = msg["date"]?.ToString() ?? "";
                            var events    = _timeline.SearchFriendEvents(srchQuery, srchDate);
                            var payload   = events.Select(e => BuildFriendTimelinePayload(e)).ToList();
                            Invoke(() => SendToJS("friendTimelineSearchResults", new { events = payload, query = srchQuery, date = srchDate }));
                        }
                        catch { }
                    });
                    break;

                case "getFriendTimeline":
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var typeFilter = msg["type"]?.ToString() ?? "";
                            var (fevents, hasMore) = _timeline.GetFriendEventsPaged(100, 0, typeFilter);
                            var fpayload = fevents.Select(e => BuildFriendTimelinePayload(e)).ToList();
                            Invoke(() => SendToJS("friendTimelineData", new { events = fpayload, hasMore, offset = 0 }));

                            if (!_vrcApi.IsLoggedIn) return;

                            // Resolve world names for GPS events that have worldId but no worldName (first page only)
                            var unknownGpsWorlds = fevents
                                .Where(e => e.Type == "friend_gps" && !string.IsNullOrEmpty(e.WorldId) && string.IsNullOrEmpty(e.WorldName))
                                .Select(e => e.WorldId).Distinct().Take(20).ToList();

                            foreach (var wid in unknownGpsWorlds)
                            {
                                try
                                {
                                    var w = await _vrcApi.GetWorldAsync(wid);
                                    if (w == null) continue;
                                    var wName  = w["name"]?.ToString()              ?? "";
                                    var wThumb = w["thumbnailImageUrl"]?.ToString() ?? "";
                                    foreach (var ev in fevents.Where(e => e.WorldId == wid && string.IsNullOrEmpty(e.WorldName)))
                                    {
                                        _timeline.UpdateFriendEventWorld(ev.Id, wName, wThumb);
                                        ev.WorldName  = wName;
                                        ev.WorldThumb = wThumb;
                                        var evCopy = ev;
                                        Invoke(() => SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(evCopy)));
                                    }
                                }
                                catch { }
                            }
                        }
                        catch (Exception ex)
                        {
                            Invoke(() => SendToJS("log", new { msg = $"[FRIEND TIMELINE] Load error: {ex.Message}", color = "err" }));
                        }
                    });
                    break;

                case "getFriendTimelinePage":
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var pageOffset = msg["offset"]?.Value<int>() ?? 0;
                            var typeFilter = msg["type"]?.ToString() ?? "";
                            var (fevents, hasMore) = _timeline.GetFriendEventsPaged(100, pageOffset, typeFilter);
                            var fpayload = fevents.Select(e => BuildFriendTimelinePayload(e)).ToList();
                            Invoke(() => SendToJS("friendTimelineData", new { events = fpayload, hasMore, offset = pageOffset }));
                        }
                        catch { }
                    });
                    break;

                case "getTimelineByDate":
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var dateStr = msg["date"]?.ToString() ?? "";
                            if (!DateTime.TryParse(dateStr, out var localDate)) return;
                            localDate = DateTime.SpecifyKind(localDate, DateTimeKind.Local);
                            var events  = _timeline.GetEventsByDate(localDate);
                            var payload = events.Select(e => BuildTimelinePayload(e)).ToList();
                            Invoke(() => SendToJS("timelineData", new { events = payload, hasMore = false, offset = 0 }));
                        }
                        catch { }
                    });
                    break;

                case "getFriendTimelineByDate":
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var dateStr    = msg["date"]?.ToString() ?? "";
                            var typeFilter = msg["type"]?.ToString() ?? "";
                            if (!DateTime.TryParse(dateStr, out var localDate)) return;
                            localDate = DateTime.SpecifyKind(localDate, DateTimeKind.Local);
                            var fevents  = _timeline.GetFriendEventsByDate(localDate, typeFilter);
                            var fpayload = fevents.Select(e => BuildFriendTimelinePayload(e)).ToList();
                            Invoke(() => SendToJS("friendTimelineData", new { events = fpayload, hasMore = false, offset = 0 }));
                        }
                        catch { }
                    });
                    break;

                // Inventory

                case "invGetFiles":
                {
                    var invTag = msg["tag"]?.ToString() ?? "gallery";
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var files = await _vrcApi.GetInventoryFilesAsync(invTag);
                            // Also fetch emojianimated when tag=emoji
                            if (invTag == "emoji")
                            {
                                var animated = await _vrcApi.GetInventoryFilesAsync("emojianimated");
                                foreach (var a in animated)
                                    files.Add(a);
                            }
                            var list = files.OfType<Newtonsoft.Json.Linq.JObject>().Select(f =>
                            {
                                var versions = (f["versions"] as Newtonsoft.Json.Linq.JArray) ?? new Newtonsoft.Json.Linq.JArray();
                                var latest = versions.OfType<Newtonsoft.Json.Linq.JObject>()
                                    .LastOrDefault(v => v["status"]?.ToString() == "complete")
                                    ?? versions.OfType<Newtonsoft.Json.Linq.JObject>().LastOrDefault();
                                var fileUrl = latest?["file"]?["url"]?.ToString() ?? "";
                                var versionId = latest?["version"]?.Value<int>() ?? 1;
                                var sizeBytes = latest?["file"]?["sizeInBytes"]?.Value<long>() ?? 0;
                                var createdAt = IsoDate(latest?["created_at"] ?? f["created_at"]);
                                return new
                                {
                                    id = f["id"]?.ToString() ?? "",
                                    name = f["name"]?.ToString() ?? "",
                                    tags = (f["tags"] as Newtonsoft.Json.Linq.JArray)?.ToObject<List<string>>() ?? new List<string>(),
                                    animationStyle = f["animationStyle"]?.ToString() ?? "",
                                    maskTag = f["maskTag"]?.ToString() ?? "",
                                    fileUrl,
                                    versionId,
                                    sizeBytes,
                                    createdAt,
                                };
                            }).OrderByDescending(f => f.createdAt).ToList();
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
                    Invoke(() =>
                    {
                        using var dlg = new OpenFileDialog
                        {
                            Filter = "PNG Images (*.png)|*.png",
                            Title = $"Upload {uploadTag} image"
                        };
                        if (dlg.ShowDialog() == DialogResult.OK)
                        {
                            var path = dlg.FileName;
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var bytes = System.IO.File.ReadAllBytes(path);
                                    var (ok, file, error) = await _vrcApi.UploadInventoryImageAsync(bytes, uploadTag);
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
                                        Invoke(() => SendToJS("invUploadResult", new { success = true, tag = uploadTag, file = newFile }));
                                    }
                                    else
                                    {
                                        Invoke(() => SendToJS("invUploadResult", new { success = false, tag = uploadTag, error }));
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Invoke(() => SendToJS("invUploadResult", new { success = false, tag = uploadTag, error = ex.Message }));
                                }
                            });
                        }
                    });
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

                            var (ok2, file2, error2) = await _vrcApi.UploadInventoryImageAsync(bytes2, uploadTag2, animStyle, maskTagVal);
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
                            var ok = await _vrcApi.DeleteInventoryFileAsync(delFileId);
                            Invoke(() => SendToJS("invDeleteResult", new { success = ok, fileId = delFileId }));
                        });
                    }
                    break;
                }

                case "invGetPrints":
                {
                    var printUserId = _vrcApi.CurrentUserId;
                    if (!string.IsNullOrEmpty(printUserId))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var prints = await _vrcApi.GetUserPrintsAsync(printUserId);
                                var list = prints.OfType<Newtonsoft.Json.Linq.JObject>().Select(p =>
                                {
                                    // Try to get image URL from files object
                                    var filesObj = p["files"] as Newtonsoft.Json.Linq.JObject;
                                    var imageUrl = filesObj?["image"]?.ToString()
                                        ?? p["imageUrl"]?.ToString()
                                        ?? p["thumbnailImageUrl"]?.ToString()
                                        ?? "";
                                    return new
                                    {
                                        id = p["id"]?.ToString() ?? "",
                                        authorId = p["authorId"]?.ToString() ?? "",
                                        authorName = p["authorName"]?.ToString() ?? "",
                                        worldId = p["worldId"]?.ToString() ?? "",
                                        worldName = p["worldName"]?.ToString() ?? "",
                                        note = p["note"]?.ToString() ?? "",
                                        createdAt = IsoDate(p["createdAt"] ?? p["timestamp"]),
                                        imageUrl,
                                    };
                                }).OrderByDescending(p => p.createdAt).ToList();
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
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var (items, total) = await _vrcApi.GetInventoryItemsAsync();
                            var list = items.OfType<Newtonsoft.Json.Linq.JObject>().Select(item => new
                            {
                                id          = item["id"]?.ToString() ?? "",
                                name        = item["name"]?.ToString() ?? "Item",
                                description = item["description"]?.ToString() ?? "",
                                itemType    = item["itemType"]?.ToString() ?? "",
                                imageUrl    = item["imageUrl"]?.ToString()
                                              ?? item["metadata"]?["imageUrl"]?.ToString() ?? "",
                                isArchived  = item["isArchived"]?.Value<bool>() ?? false,
                                createdAt   = IsoDate(item["created_at"]),
                            }).ToList();
                            Invoke(() => SendToJS("invInventory", new { items = list, totalCount = total }));
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
                            var ok = await _vrcApi.DeletePrintAsync(delPrintId);
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
                        Invoke(() =>
                        {
                            using var saveDlg = new SaveFileDialog
                            {
                                FileName = System.IO.Path.GetFileName(dlFileName),
                                Filter = "PNG Image (*.png)|*.png|All Files (*.*)|*.*",
                                Title = "Save image"
                            };
                            if (saveDlg.ShowDialog() == DialogResult.OK)
                            {
                                var savePath = saveDlg.FileName;
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        var resp = await _vrcApi.GetHttpClient().GetAsync(dlUrl);
                                        if (resp.IsSuccessStatusCode)
                                        {
                                            var bytes = await resp.Content.ReadAsByteArrayAsync();
                                            System.IO.File.WriteAllBytes(savePath, bytes);
                                            Invoke(() => SendToJS("log", new { msg = $"Saved: {savePath}", color = "ok" }));
                                        }
                                        else
                                        {
                                            Invoke(() => SendToJS("log", new { msg = $"Download failed: HTTP {(int)resp.StatusCode}", color = "err" }));
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Invoke(() => SendToJS("log", new { msg = $"Download error: {ex.Message}", color = "err" }));
                                    }
                                });
                            }
                        });
                    }
                    break;
                }

                case "openUrl":
                    var openUrlTarget = msg["url"]?.ToString();
                    if (!string.IsNullOrEmpty(openUrlTarget) &&
                        (openUrlTarget.StartsWith("https://") || openUrlTarget.StartsWith("http://")))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = openUrlTarget,
                            UseShellExecute = true
                        });
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            SendToJS("log", new { msg = $"Error: {ex.Message}", color = "err" });
        }
    }

    private class WFavGroup
    {
        public string name        { get; set; } = "";
        public string displayName { get; set; } = "";
        public string type        { get; set; } = "";
        public int    capacity    { get; set; } = 25;
    }

    /// <summary>
    /// VRChat API only returns world favorite groups that have at least one world in them.
    /// This fills in the standard empty slots so all 8 (or 4) groups are always visible.
    /// </summary>
    private static List<WFavGroup> FillMissingWorldSlots(List<WFavGroup> groupList)
    {
        var existing = new HashSet<string>(groupList.Select(g => g.name));

        var regularSlots = new[] {
            ("worlds1", "Worlds 1", "world"), ("worlds2", "Worlds 2", "world"),
            ("worlds3", "Worlds 3", "world"), ("worlds4", "Worlds 4", "world")
        };
        foreach (var (sName, sDisplay, sType) in regularSlots)
            if (!existing.Contains(sName))
                groupList.Add(new WFavGroup { name = sName, displayName = sDisplay, type = sType });

        bool hasVrcPlus = groupList.Any(g => g.type == "vrcPlusWorld");
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

    // ── Cache fetch helpers ───────────────────────────────────────────────────

    private async Task FetchAndCacheFavWorldsAsync()
    {
        try
        {
            var groups = await _vrcApi.GetFavoriteGroupsAsync();
            var worldTypes = new HashSet<string> { "world", "vrcPlusWorld" };
            var groupList = groups
                .Where(g => worldTypes.Contains(g["type"]?.ToString() ?? ""))
                .Select(g => new WFavGroup {
                    name        = g["name"]?.ToString() ?? "",
                    displayName = g["displayName"]?.ToString() ?? "",
                    type        = g["type"]?.ToString() ?? "world"
                })
                .Where(g => !string.IsNullOrEmpty(g.name))
                .ToList();
            groupList = FillMissingWorldSlots(groupList);

            var sem = new SemaphoreSlim(4, 4);
            var perGroup = new System.Collections.Concurrent.ConcurrentDictionary<string, List<JObject>>();
            await Task.WhenAll(groupList.Select(async g =>
            {
                await sem.WaitAsync();
                try { perGroup[g.name] = await _vrcApi.GetFavoriteWorldsByGroupAsync(g.name, 100); }
                finally { sem.Release(); }
            }));

            var allWorlds = new List<object>();
            foreach (var g in groupList)
            {
                if (!perGroup.TryGetValue(g.name, out var groupWorlds)) continue;
                foreach (var w in groupWorlds)
                {
                    var wid = w["id"]?.ToString() ?? "";
                    var stats = _worldTimeTracker.GetWorldStats(wid);
                    allWorlds.Add(new
                    {
                        id                = wid,
                        name              = w["name"]?.ToString() ?? "",
                        imageUrl          = w["imageUrl"]?.ToString() ?? "",
                        thumbnailImageUrl = w["thumbnailImageUrl"]?.ToString() ?? "",
                        authorName        = w["authorName"]?.ToString() ?? "",
                        occupants         = w["occupants"]?.Value<int>()  ?? 0,
                        capacity          = w["capacity"]?.Value<int>()   ?? 0,
                        favorites         = w["favorites"]?.Value<int>()  ?? 0,
                        visits            = w["visits"]?.Value<int>()     ?? 0,
                        tags              = w["tags"]?.ToObject<List<string>>() ?? new List<string>(),
                        favoriteGroup     = g.name,
                        favoriteId        = w["favoriteId"]?.ToString() ?? "",
                        worldTimeSeconds  = stats.totalSeconds,
                        worldVisitCount   = stats.visitCount,
                    });
                }
            }

            var payload = new { worlds = allWorlds, groups = groupList };
            if (_settings.FfcEnabled) _cache.Save(CacheHandler.KeyFavWorlds, payload);
            Invoke(() => SendToJS("vrcFavoriteWorlds", payload));
        }
        catch (Exception ex)
        {
            Invoke(() => SendToJS("log", new { msg = $"Favorite worlds error: {ex.Message}", color = "err" }));
        }
    }

    private static List<WFavGroup> FillMissingAvatarSlots(List<WFavGroup> groupList)
    {
        var existing = new HashSet<string>(groupList.Select(g => g.name));

        // VRChat has 6 avatar favorite groups (avatars1–avatars6), all with type "avatar".
        // avatars1 is free; avatars2–6 require VRC+.
        // The API only returns groups that have been renamed or contain items,
        // so we fill in any missing slots so empty groups are still visible.
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

    private async Task FetchAndCacheFavAvatarsAsync()
    {
        try
        {
            var groups = await _vrcApi.GetFavoriteGroupsAsync();
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
            int avCap = _vrcApi.HasVrcPlus ? 50 : 25;
            foreach (var g in groupList) g.capacity = avCap;

            var sem = new SemaphoreSlim(4, 4);
            var perGroup = new System.Collections.Concurrent.ConcurrentDictionary<string, List<JObject>>();
            await Task.WhenAll(groupList.Select(async g =>
            {
                await sem.WaitAsync();
                try { perGroup[g.name] = await _vrcApi.GetFavoriteAvatarsByGroupAsync(g.name, 100); }
                finally { sem.Release(); }
            }));

            var allAvatars = new List<object>();
            foreach (var g in groupList)
            {
                if (!perGroup.TryGetValue(g.name, out var groupAvatars)) continue;
                foreach (var a in groupAvatars)
                {
                    allAvatars.Add(new
                    {
                        id                = a["id"]?.ToString() ?? "",
                        name              = a["name"]?.ToString() ?? "",
                        imageUrl          = a["imageUrl"]?.ToString() ?? "",
                        thumbnailImageUrl = a["thumbnailImageUrl"]?.ToString() ?? "",
                        authorName        = a["authorName"]?.ToString() ?? "",
                        releaseStatus     = a["releaseStatus"]?.ToString() ?? "private",
                        favoriteGroup     = g.name,
                        favoriteId        = a["favoriteId"]?.ToString() ?? "",
                        unityPackages     = a["unityPackages"] as JArray ?? new JArray(),
                    });
                }
            }

            var payload = new { avatars = allAvatars, groups = groupList };
            Invoke(() => SendToJS("vrcFavoriteAvatars", payload));
        }
        catch (Exception ex)
        {
            Invoke(() => SendToJS("log", new { msg = $"Favorite avatars error: {ex.Message}", color = "err" }));
        }
    }

    private async Task FetchAndCacheAvatarsAsync()
    {
        try
        {
            var avatars = await _vrcApi.GetOwnAvatarsAsync();
var list = avatars.Select(a => new
            {
                id                = a["id"]?.ToString() ?? "",
                name              = a["name"]?.ToString() ?? "",
                imageUrl          = a["imageUrl"]?.ToString() ?? "",
                thumbnailImageUrl = a["thumbnailImageUrl"]?.ToString() ?? "",
                authorName        = a["authorName"]?.ToString() ?? "",
                releaseStatus     = a["releaseStatus"]?.ToString() ?? "private",
                description       = a["description"]?.ToString() ?? "",
                unityPackages     = a["unityPackages"] as JArray ?? new JArray(),
            }).ToList();
            var payload = new { filter = "own", avatars = list, currentAvatarId = _vrcApi.CurrentAvatarId ?? "" };
            if (_settings.FfcEnabled) _cache.Save(CacheHandler.KeyAvatars, payload);
            Invoke(() => SendToJS("vrcAvatars", payload));
        }
        catch (Exception ex)
        {
            Invoke(() => SendToJS("log", new { msg = $"Avatar load error: {ex.Message}", color = "err" }));
        }
    }

    private async Task FetchAndCacheGroupsAsync()
    {
        try
        {
            var groups = await _vrcApi.GetUserGroupsAsync();
            var ids = groups.Cast<JObject>()
                .Select(g => g["groupId"]?.ToString() ?? g["id"]?.ToString() ?? "")
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .ToList();

            var fullGroups = await Task.WhenAll(ids.Select(id => _vrcApi.GetGroupAsync(id)));

            var enriched = new List<object>();
            for (int i = 0; i < ids.Count; i++)
            {
                var full = fullGroups[i];
                if (full == null) continue;

                var myMember = full["myMember"] as JObject;
                var perms = myMember?["permissions"]?.ToObject<List<string>>();
                var name = full["name"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(name)) continue;

                var canCreate = perms == null
                    || perms.Contains("*")
                    || perms.Contains("group-instance-open-create")
                    || perms.Contains("group-instance-plus-create")
                    || perms.Contains("group-instance-public-create")
                    || perms.Contains("group-instance-restricted-create");

                var canPost = perms != null
                    && (perms.Contains("*") || perms.Contains("group-posts-manage"));

                enriched.Add(new {
                    id = full["id"]?.ToString() ?? ids[i],
                    name,
                    shortCode    = full["shortCode"]?.ToString() ?? "",
                    description  = full["description"]?.ToString() ?? "",
                    iconUrl      = full["iconUrl"]?.ToString() ?? "",
                    bannerUrl    = full["bannerUrl"]?.ToString() ?? "",
                    memberCount  = full["memberCount"]?.Value<int>() ?? 0,
                    privacy      = full["privacy"]?.ToString() ?? "",
                    canCreateInstance = canCreate,
                    canPost,
                });
            }
            if (_settings.FfcEnabled) _cache.Save(CacheHandler.KeyGroups, enriched);
            Invoke(() => {
                SendToJS("log", new { msg = $"[GROUPS] {enriched.Count} loaded", color = "sec" });
                SendToJS("vrcMyGroups", enriched);
            });
        }
        catch (Exception ex)
        {
            Invoke(() => SendToJS("log", new { msg = $"Groups load error: {ex.Message}", color = "err" }));
        }
    }

    /// <summary>
    /// Sends all available disk-cached data to JS immediately (called on startup before API refresh).
    /// </summary>
    private void SendAllCachedData()
    {
        if (!_settings.FfcEnabled) return;

        // Friends are NOT sent from cache — status/location must always be live.
        // VrcRefreshFriendsAsync() fills the friends list with fresh data on startup.

        var avatars = _cache.LoadRaw(CacheHandler.KeyAvatars);
        if (avatars != null) SendToJS("vrcAvatars", avatars);

        var groups = _cache.LoadRaw(CacheHandler.KeyGroups);
        if (groups != null) SendToJS("vrcMyGroups", groups);

        var favWorlds = _cache.LoadRaw(CacheHandler.KeyFavWorlds);
        if (favWorlds != null) SendToJS("vrcFavoriteWorlds", favWorlds);
    }

    /// <summary>
    /// Kicks off background refresh of avatars, groups, and favorite worlds after login.
    /// Friends are already refreshed by VrcRefreshFriendsAsync.
    /// </summary>
    private async Task TriggerStartupBackgroundRefreshAsync()
    {
        if (!_vrcApi.IsLoggedIn) return;
        _ = Task.Run(FetchAndCacheAvatarsAsync);
        _ = Task.Run(FetchAndCacheGroupsAsync);
        _ = Task.Run(FetchAndCacheFavWorldsAsync);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Bulk-caches all friend profiles, avatars, groups, and favorite worlds.
    /// Sends ffcProgress updates to the UI. Always saves to cache regardless of FfcEnabled.
    /// </summary>
    private async Task ForceFfcAllAsync()
    {
        if (!_vrcApi.IsLoggedIn) return;

        void Progress(int current, int total, string label) =>
            Invoke(() => SendToJS("ffcProgress", new {
                progress = total > 0 ? (int)((double)current / total * 100) : 0,
                label,
                done = false
            }));

        try
        {
            var friendIds = _friendNameImg.Keys.ToList();
            int total = friendIds.Count + 3; // +3 for avatars, groups, worlds
            int completed = 0;

            Progress(completed, total, "Caching avatars...");
            await FetchAndCacheAvatarsAsync();
            Progress(++completed, total, "Caching groups...");
            await FetchAndCacheGroupsAsync();
            Progress(++completed, total, "Caching worlds...");
            await FetchAndCacheFavWorldsAsync();

            // Cache friend profiles 4 at a time.
            var semaphore = new SemaphoreSlim(4, 4);
            var tasks = friendIds.Select(async uid =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var payload = await BuildUserDetailPayloadAsync(uid);
                    if (payload != null)
                    {
                        _userDetailCache[uid] = (payload, DateTime.UtcNow);
                        _cache.Save(CacheHandler.KeyUserProfile(uid), payload);
                    }
                    await Task.Delay(250); // rate-limit gap before next profile
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
                SendToJS("ffcProgress", new { progress = 100, label = $"Done! {friendIds.Count} profiles cached.", done = true });
                SendToJS("log", new { msg = $"FFC: {friendIds.Count} profiles + avatars + groups + worlds cached.", color = "ok" });
            });
        }
        catch (Exception ex)
        {
            Invoke(() => SendToJS("ffcProgress", new { progress = 0, label = "Error: " + ex.Message, done = true }));
        }
    }

    // C# to JS messaging
    private void SendToJS(string type, object? payload = null)
    {
        if (_webView.CoreWebView2 == null) return;
        var msg = JsonConvert.SerializeObject(new { type, payload });
        // Replace VRChat image URLs with locally-cached versions for instant loading
        if (_imgCache != null)
            msg = _vrcImgUrlRegex.Replace(msg, m => $"\"{_imgCache.Get(m.Groups[1].Value)}\"");
        try { _webView.CoreWebView2.PostWebMessageAsJson(msg); } catch { }
    }

    // Relay Control
    private void StartRelay()
    {
        var folders = _settings.WatchFolders.Where(Directory.Exists).ToList();
        if (folders.Count == 0)
        {
            SendToJS("log", new { msg = "No valid watch folders configured!", color = "err" });
            return;
        }
        var whs = _settings.Webhooks.Where(w => w.Enabled && !string.IsNullOrWhiteSpace(w.Url)).ToList();
        if (whs.Count == 0)
        {
            SendToJS("log", new { msg = "No webhooks active!", color = "err" });
            return;
        }

        _fileWatcher.Start(folders);
        _relayRunning = true;
        _relayStart = DateTime.Now;
        _uptimeTimer.Start();

        SendToJS("relayState", new { running = true, streams = whs.Count });
        SendToJS("log", new { msg = "Relay started successfully", color = "ok" });
        foreach (var f in folders)
            SendToJS("log", new { msg = $"  Watching: {f}", color = "sec" });
        foreach (var w in whs)
            SendToJS("log", new { msg = $"  Webhook: {w.Name}", color = "accent" });
    }

    private void StopRelay()
    {
        _fileWatcher.Stop();
        _relayRunning = false;
        _uptimeTimer.Stop();

        SendToJS("relayState", new { running = false, streams = 0 });
        SendToJS("log", new { msg = "Relay stopped", color = "warn" });
    }

    // VRCVideoCacher
    private static readonly string VcExePath = Path.Combine(
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
                try { Invoke(() => SendToJS("vcState", GetVcState())); } catch { }
            };
            SendToJS("vcState", GetVcState());
        }
        catch { }
    }

    private void StopVcProcess()
    {
        try { _vcProcess?.Kill(entireProcessTree: true); } catch { }
        _vcProcess = null;
        SendToJS("vcState", GetVcState());
    }

    private async Task InstallVcAsync()
    {
        try
        {
            Invoke(() => SendToJS("vcState", new { installed = false, running = false, downloading = true, progress = 0 }));

            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "VRCNext");

            var apiResp = await http.GetAsync("https://api.github.com/repos/EllyVR/VRCVideoCacher/releases/latest");
            if (!apiResp.IsSuccessStatusCode)
            {
                Invoke(() => SendToJS("vcState", new { installed = false, running = false, error = $"GitHub API: HTTP {(int)apiResp.StatusCode}" }));
                return;
            }

            var json     = JObject.Parse(await apiResp.Content.ReadAsStringAsync());
            var version  = json["tag_name"]?.ToString() ?? "?";
            var assets   = json["assets"] as JArray;
            var exeAsset = assets?.FirstOrDefault(a => a["name"]?.ToString().EndsWith(".exe") == true);
            var dlUrl    = exeAsset?["browser_download_url"]?.ToString();

            if (string.IsNullOrEmpty(dlUrl))
            {
                Invoke(() => SendToJS("vcState", new { installed = false, running = false, error = "No .exe asset in latest release" }));
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
                    try { Invoke(() => SendToJS("vcState", new { installed = false, running = false, downloading = true, progress = pct })); } catch { }
                }
            }

            Invoke(() =>
            {
                SendToJS("vcLog",   new { msg = $"VRCVideoCacher {version} installed", color = "ok" });
                SendToJS("vcState", GetVcState());
            });
        }
        catch (Exception ex)
        {
            try { Invoke(() => SendToJS("vcState", new { installed = false, running = false, error = ex.Message })); } catch { }
        }
    }

    // Launch VRChat + extra apps
    private void LaunchVRChat()
    {
        try
        {
            var vrcPath = _settings.VrcPath;
            if (string.IsNullOrWhiteSpace(vrcPath) || !File.Exists(vrcPath))
            {
                SendToJS("log", new { msg = "VRChat path not set or invalid. Configure in Settings.", color = "err" });
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = vrcPath,
                WorkingDirectory = Path.GetDirectoryName(vrcPath) ?? "",
                UseShellExecute = true
            });
            SendToJS("log", new { msg = "Launched VRChat", color = "ok" });

            foreach (var exe in _settings.ExtraExe)
            {
                try
                {
                    if (!File.Exists(exe))
                    {
                        SendToJS("log", new { msg = $"Not found: {Path.GetFileName(exe)}", color = "warn" });
                        continue;
                    }
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = exe,
                        WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                        UseShellExecute = true
                    });
                    SendToJS("log", new { msg = $"Launched: {Path.GetFileName(exe)}", color = "ok" });
                }
                catch (Exception ex)
                {
                    SendToJS("log", new { msg = $"Failed to launch {Path.GetFileName(exe)}: {ex.Message}", color = "err" });
                }
            }
        }
        catch (Exception ex)
        {
            SendToJS("log", new { msg = $"Launch error: {ex.Message}", color = "err" });
        }
    }

    private static bool IsVrcRunning() =>
        Process.GetProcessesByName("VRChat").Any(p => { try { return !p.HasExited; } catch { return false; } });

    private static bool IsSteamVrRunning() =>
        Process.GetProcessesByName("vrserver").Any(p => { try { return !p.HasExited; } catch { return false; } });

    // VRChat API
    private string _pending2faType = "totp";
    private bool _vrcDebugSetup;

    private void SetupVrcDebugLog()
    {
        if (_vrcDebugSetup) return;
        _vrcDebugSetup = true;
        _vrcApi.DebugLog += msg =>
        {
            try { Invoke(() => SendToJS("log", new { msg = $"[VRC] {msg}", color = "sec" })); } catch { }
        };
        _logWatcher.DebugLog += msg =>
        {
            try { Invoke(() => SendToJS("log", new { msg = $"[LOG] {msg}", color = "sec" })); } catch { }
        };
        _logWatcher.WorldChanged += (wId, loc) =>
        {
            try { Invoke(() => HandleWorldChangedOnUiThread(wId, loc)); } catch { }
        };
        _logWatcher.PlayerJoined += (uid, name) =>
        {
            try { Invoke(() => HandlePlayerJoinedOnUiThread(uid, name)); } catch { }
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
                SendVrcUserData(result.User);
                SendToJS("log", new { msg = $"VRChat: Reconnected as {result.User["displayName"]}", color = "ok" });
                SendAllCachedData();
                StartWebSocket();
                await VrcRefreshFriendsAsync();
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

            SendVrcUserData(result.User);
            SendToJS("log", new { msg = $"VRChat: Logged in as {result.User["displayName"]}", color = "ok" });
            StartWebSocket();
            await VrcRefreshFriendsAsync();
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

            SendVrcUserData(result.User);
            SendToJS("log", new { msg = $"VRChat: Logged in as {result.User["displayName"]}", color = "ok" });
            StartWebSocket();
            await VrcRefreshFriendsAsync();
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

    // Extracted API actions (called from switch-case and WebSocket events)

    // --- Notification helpers ---

    private static string? ExtractGroupIdFromLink(string? link)
    {
        if (string.IsNullOrEmpty(link)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(link, @"grp_[0-9a-f\-]+");
        return m.Success ? m.Value : null;
    }

    private static dynamic NormalizeNotifV1(JObject n) => (dynamic)new {
        id             = n["id"]?.ToString() ?? "",
        type           = n["type"]?.ToString() ?? "",
        senderUserId   = n["senderUserId"]?.ToString() ?? "",
        senderUsername = n["senderUsername"]?.ToString() ?? "",
        message        = n["message"]?.ToString() ?? "",
        created_at     = n["created_at"]?.Type == JTokenType.Date
                           ? n["created_at"]!.Value<DateTime>().ToString("o")
                           : n["created_at"]?.ToString() ?? DateTime.UtcNow.ToString("o"),
        seen           = n["seen"]?.Value<bool>() ?? false,
        details        = n["details"],
        _v2            = false,
        _title         = (string?)null,
        _link          = (string?)null,
    };

    private static dynamic NormalizeNotifV2(JObject n) => (dynamic)new {
        id             = n["id"]?.ToString() ?? "",
        type           = n["type"]?.ToString() ?? "",
        senderUserId   = n["senderUserId"]?.ToString() ?? "",
        // v2 uses senderDisplayName; fall back to senderUsername for safety
        senderUsername = n["senderDisplayName"]?.ToString()
                      ?? n["senderUsername"]?.ToString()
                      ?? "",
        message        = n["message"]?.ToString() ?? "",
        created_at     = n["createdAt"]?.Type == JTokenType.Date
                           ? n["createdAt"]!.Value<DateTime>().ToString("o")
                           : n["createdAt"]?.ToString() ?? DateTime.UtcNow.ToString("o"),
        seen           = n["seen"]?.Value<bool>() ?? false,
        details        = (object?)null,
        _v2            = true,
        _title         = n["title"]?.ToString(),
        _link          = n["link"]?.ToString(),
        _data          = n["data"],  // group-specific data: groupId, requestUserId, etc.
    };

    /// <summary>
    /// Process a single notification: add to timeline, send to JS.
    /// If prependToJs is true, sends vrcNotificationPrepend (WS path).
    /// Returns the timeline payload so the caller can batch if needed.
    /// </summary>
    private object? ProcessSingleNotif(dynamic n, bool prependToJs)
    {
        if (_timeline.IsLoggedNotif((string)n.id)) return null;
        _timeline.AddLoggedNotif((string)n.id);

        var senderImg    = "";
        var senderUserId = (string?)n.senderUserId;
        if (!string.IsNullOrEmpty(senderUserId))
        {
            lock (_playerImageCache)
                if (_playerImageCache.TryGetValue(senderUserId, out var cached))
                    senderImg = cached.image;
        }

        var notifEv = new TimelineService.TimelineEvent
        {
            Type        = "notification",
            Timestamp   = n.created_at,
            NotifId     = n.id,
            NotifType   = n.type,
            SenderName  = n.senderUsername,
            SenderId    = n.senderUserId,
            SenderImage = senderImg,
            Message     = n.message,
        };
        _timeline.AddEvent(notifEv);

        if (prependToJs)
            Invoke(() => {
                SendToJS("vrcNotificationPrepend", n);
                SendToJS("timelineEvent", BuildTimelinePayload(notifEv));
            });

        // Async image fetch if not cached
        if (string.IsNullOrEmpty(senderImg) && !string.IsNullOrEmpty(senderUserId) && _vrcApi.IsLoggedIn)
        {
            var evId = notifEv.Id;
            var uid  = senderUserId;
            _ = Task.Run(async () =>
            {
                try
                {
                    var profile = await _vrcApi.GetUserAsync(uid);
                    if (profile == null) return;
                    var img = VRChatApiService.GetUserImage(profile);
                    if (string.IsNullOrEmpty(img)) return;
                    lock (_playerImageCache) _playerImageCache[uid] = (img, DateTime.Now);
                    _timeline.UpdateEvent(evId, ev => ev.SenderImage = img);
                    var updated = _timeline.GetEvents().FirstOrDefault(e => e.Id == evId);
                    if (updated != null) Invoke(() => SendToJS("timelineEvent", BuildTimelinePayload(updated)));
                }
                catch { }
            });
        }

        return BuildTimelinePayload(notifEv);
    }

    /// <summary>REST fetch of v1+v2 — used on login and reconnect to catch missed notifications.</summary>
    private Task VrcGetNotificationsAsync() => Task.Run(async () =>
    {
        var t1 = _vrcApi.GetNotificationsAsync();
        var t2 = _vrcApi.GetNotificationsV2Async();
        await Task.WhenAll(t1, t2);

        var list = t1.Result.Cast<JObject>().Select(NormalizeNotifV1).ToList();
        Invoke(() => SendToJS("log", new { msg = $"[Notif REST] v1={t1.Result.Count} types=[{string.Join(",", t1.Result.Cast<JObject>().Select(n => n["type"]?.ToString()))}]", color = "sec" }));

        var v2Ids = new HashSet<string>(list.Select(n => (string)n.id));
        foreach (JObject n in t2.Result.Cast<JObject>())
        {
            var id = n["id"]?.ToString() ?? "";
            if (v2Ids.Contains(id)) continue;
            list.Add(NormalizeNotifV2(n));
        }
        Invoke(() => SendToJS("log", new { msg = $"[Notif REST] v2={t2.Result.Count} types=[{string.Join(",", t2.Result.Cast<JObject>().Select(n => n["type"]?.ToString()))}]", color = "sec" }));

        list = list.OrderByDescending(n => (string)n.created_at).ToList();

        var newTimeline = new List<object>();
        foreach (var n in list)
        {
            var ev = ProcessSingleNotif(n, prependToJs: false);
            if (ev != null) newTimeline.Add(ev);
        }

        Invoke(() =>
        {
            SendToJS("vrcNotifications", list);
            foreach (var ev in newTimeline)
                SendToJS("timelineEvent", ev);
        });
    });

    private Task VrcGetCurrentInstanceAsync() => Task.Run(async () =>
    {
        try
        {
            // Step 1: Get live location via /users/{id} (not /auth/user which returns null)
            var loc = await _vrcApi.GetMyLocationAsync();
            if (string.IsNullOrEmpty(loc) || loc == "offline" || loc == "private" || loc == "traveling")
            {
                Invoke(() => SendToJS("vrcCurrentInstance", new { empty = true }));
                return;
            }

            var parsed = VRChatApiService.ParseLocation(loc);

            // Step 2: Fetch instance details (for world info + n_users)
            var inst = await _vrcApi.GetInstanceAsync(loc);

            // Step 3: Get world info
            var worldName     = inst?["world"]?["name"]?.ToString() ?? "";
            var worldThumb    = inst?["world"]?["thumbnailImageUrl"]?.ToString() ?? "";
            var worldCapacity = inst?["world"]?["capacity"]?.Value<int>() ?? 0;

            if (string.IsNullOrEmpty(worldName) && !string.IsNullOrEmpty(parsed.worldId))
            {
                var world = await _vrcApi.GetWorldAsync(parsed.worldId);
                if (world != null)
                {
                    worldName     = world["name"]?.ToString() ?? "";
                    worldThumb    = world["thumbnailImageUrl"]?.ToString() ?? "";
                    worldCapacity = world["capacity"]?.Value<int>() ?? 0;
                }
            }
            if (string.IsNullOrEmpty(worldName)) worldName = parsed.worldId;

            // Step 4: Build player list. Prefer LogWatcher (reads VRChat logs),
            // fall back to API users array
            var users = new List<object>();
            string playerSource = "none";

            Invoke(() => SendToJS("log", new { msg = $"[LOG] {_logWatcher.GetDiagnostics()}", color = "sec" }));

            // Source A: VRChat log file (most complete, shows ALL players)
            var logPlayers = _logWatcher.GetCurrentPlayers();
            if (logPlayers.Count > 0)
            {
                playerSource = "logfile";

                var playersWithId = logPlayers.Where(p => !string.IsNullOrEmpty(p.UserId)).ToList();
                var userProfiles  = new Dictionary<string, JObject>();

                var needFetch = playersWithId.Where(p =>
                    !_playerImageCache.TryGetValue(p.UserId, out var c) ||
                    (DateTime.Now - c.fetched).TotalMinutes > 10
                ).ToList();

                if (needFetch.Count > 0)
                {
                    var semaphore = new SemaphoreSlim(5);
                    var tasks = needFetch.Select(async p =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            var profile = await _vrcApi.GetUserAsync(p.UserId);
                            if (profile != null)
                            {
                                var img = VRChatApiService.GetUserImage(profile);
                                lock (_playerImageCache)
                                    _playerImageCache[p.UserId] = (img, DateTime.Now);
                                lock (userProfiles)
                                    userProfiles[p.UserId] = profile;
                            }
                        }
                        finally { semaphore.Release(); }
                    });
                    await Task.WhenAll(tasks);

                    Invoke(() =>
                    {
                        foreach (var uid in _cumulativeInstancePlayers.Keys.ToList())
                        {
                            if (_playerImageCache.TryGetValue(uid, out var cached) && !string.IsNullOrEmpty(cached.image))
                            {
                                var existing = _cumulativeInstancePlayers[uid];
                                _cumulativeInstancePlayers[uid] = (existing.displayName, cached.image);
                            }
                        }
                    });
                }

                Invoke(() => SendToJS("log", new { msg = $"[LOG] Profiles: {needFetch.Count} fetched, {playersWithId.Count - needFetch.Count} cached", color = "sec" }));

                foreach (var p in logPlayers)
                {
                    var img = "";
                    var status = "";
                    if (!string.IsNullOrEmpty(p.UserId))
                    {
                        if (userProfiles.TryGetValue(p.UserId, out var prof))
                        {
                            img    = VRChatApiService.GetUserImage(prof);
                            status = prof["status"]?.ToString() ?? "";
                        }
                        else if (_playerImageCache.TryGetValue(p.UserId, out var cached))
                        {
                            img = cached.image;
                        }
                    }
                    users.Add(new { id = p.UserId, displayName = p.DisplayName, image = img, status });
                }
            }

            // Source B: API users array (sometimes populated for public instances)
            if (users.Count == 0 && inst?["users"]?.Type == JTokenType.Array)
            {
                playerSource = "api";
                foreach (var u in inst["users"]!)
                {
                    users.Add(new {
                        id          = u["id"]?.ToString() ?? "",
                        displayName = u["displayName"]?.ToString() ?? "",
                        image       = VRChatApiService.GetUserImage((JObject)u),
                        status      = u["status"]?.ToString() ?? "",
                    });
                }
            }

            var nUsers = inst?["n_users"]?.Value<int>()
                ?? inst?["userCount"]?.Value<int>()
                ?? users.Count;
            if (worldCapacity == 0) worldCapacity = inst?["capacity"]?.Value<int>() ?? 0;

            Invoke(() =>
            {
                SendToJS("log", new { msg = $"Instance: {worldName} — {nUsers} total, {users.Count} tracked ({playerSource})", color = "ok" });
                SendToJS("vrcCurrentInstance", new {
                    location = loc, worldId = parsed.worldId,
                    worldName, worldThumb,
                    instanceType = parsed.instanceType,
                    nUsers, capacity = worldCapacity, users, playerSource,
                });
            });
        }
        catch (Exception ex)
        {
            Invoke(() =>
            {
                SendToJS("log", new { msg = $"❌ Instance error: {ex.Message}", color = "err" });
                SendToJS("vrcCurrentInstance", new { error = ex.Message });
            });
        }
    });

    // WebSocket helpers

    private System.Threading.Timer? _wsFallbackTimer;

    private void StartWebSocket()
    {
        var (auth, tfa) = _vrcApi.GetCookies();
        if (string.IsNullOrEmpty(auth)) return;

        _wsService?.Dispose();
        _wsService = new VRChatWebSocketService();

        // friend-active: state changed but we have no user object — just push store as-is
        _wsService.FriendsChanged += (_, _) =>
        {
            if (_vrcApi.IsLoggedIn && _friendStateSeeded)
                PushFriendsFromStore();
        };

        // friend-add / friend-delete: list membership changed — need authoritative REST data
        _wsService.FriendListChanged += (_, _) =>
        {
            if (_vrcApi.IsLoggedIn)
                _ = VrcRefreshFriendsAsync(true);
        };

        _wsService.NotificationArrived += (_, args) =>
        {
            if (!_vrcApi.IsLoggedIn) return;
            // update/delete or missing payload → full REST refresh
            if (args.WsType is "notification-v2-update" or "notification-v2-delete" || args.Data == null)
            {
                _ = VrcGetNotificationsAsync();
                return;
            }
            // notification/notification-v2: process the WS payload directly — no REST needed
            _ = Task.Run(() =>
            {
                try
                {
                    var n = args.WsType == "notification-v2"
                        ? NormalizeNotifV2(args.Data)
                        : NormalizeNotifV1(args.Data);
                    ProcessSingleNotif(n, prependToJs: true);
                }
                catch (Exception ex)
                {
                    Invoke(() => SendToJS("log", new { msg = $"[WS Notif] parse error: {ex.Message}", color = "err" }));
                    _ = VrcGetNotificationsAsync();
                }
            });
        };

        // Small delay so the VRC API reflects the new location before we query it
        _wsService.OwnLocationChanged += (_, _) =>
        {
            if (_vrcApi.IsLoggedIn)
                _ = Task.Delay(3000).ContinueWith(_ => VrcGetCurrentInstanceAsync());
        };

        // All log calls must use Invoke(); these fire on the WebSocket background thread
        _wsService.Connected += (_, _) =>
        {
            Invoke(() =>
            {
                SendToJS("wsStatus", new { connected = true });
                SendToJS("log", new { msg = "[WS] Connected to pipeline.vrchat.cloud", color = "ok" });
            });
            // Refresh on reconnect; may have missed events during the disconnect window
            if (_vrcApi.IsLoggedIn)
            {
                _ = VrcRefreshFriendsAsync(true);
                _ = VrcGetNotificationsAsync();
            }
        };

        _wsService.Disconnected += (_, _) =>
            Invoke(() =>
            {
                SendToJS("wsStatus", new { connected = false });
                SendToJS("log", new { msg = "[WS] Disconnected — reconnecting...", color = "warn" });
            });

        _wsService.ConnectError += (_, err) =>
            Invoke(() => SendToJS("log", new { msg = $"[WS] Error: {err}", color = "err" }));

        // Friends Timeline: typed WebSocket events
        _wsService.FriendLocationChanged += OnWsFriendLocation;
        _wsService.FriendWentOffline     += OnWsFriendOffline;
        _wsService.FriendWentOnline      += OnWsFriendOnline;
        _wsService.FriendUpdated         += OnWsFriendUpdated;

        // Pass a delegate so the service fetches fresh cookies on every internal reconnect
        _wsService.Start(auth, tfa ?? "", () =>
        {
            var (a, t) = _vrcApi.GetCookies();
            return (a ?? "", t ?? "");
        });

        // Fallback: safety-net refresh every 5 min in case a WebSocket event was missed.
        // WS events now update the live store directly, so REST is only a last resort.
        _wsFallbackTimer?.Dispose();
        var jitter = TimeSpan.FromSeconds(Random.Shared.Next(0, 30));
        _wsFallbackTimer = new System.Threading.Timer(_ =>
        {
            if (!_vrcApi.IsLoggedIn) return;
            _ = VrcRefreshFriendsAsync(true);
            _ = VrcGetNotificationsAsync();
        }, null, TimeSpan.FromMinutes(5) + jitter, TimeSpan.FromMinutes(5));
    }

    // Favorite Friends

    private async Task LoadFavoriteFriendsAsync()
    {
        try
        {
            var favs = await _vrcApi.GetFavoriteFriendsAsync();
            lock (_favoriteFriends)
            {
                _favoriteFriends.Clear();
                foreach (var fav in favs)
                {
                    var uid   = fav["favoriteId"]?.ToString() ?? "";
                    var fvrtId = fav["id"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(fvrtId))
                        _favoriteFriends[uid] = fvrtId;
                }
            }
            // Send the list to JS so the People Favorites tab can render
            var list = favs.Select(f => new
            {
                fvrtId     = f["id"]?.ToString() ?? "",
                favoriteId = f["favoriteId"]?.ToString() ?? "",
            }).Where(f => !string.IsNullOrEmpty(f.favoriteId)).ToList();
            Invoke(() => SendToJS("vrcFavoriteFriends", list));
        }
        catch { }
    }

    private void SendVrcUserData(JObject user)
    {
        _currentVrcUserId = user["id"]?.ToString() ?? "";

        // Start log watcher on successful login (idempotent, safe to call multiple times)
        _logWatcher.Start();
        StartVrcPhotoWatcher();
        _ = LoadFavoriteFriendsAsync();

        // Bootstrap once per app session: if VRChat was already running when the app started,
        // the catch-up read populated CurrentWorldId without firing WorldChanged.
        if (!_logWatcherBootstrapped)
        {
            _logWatcherBootstrapped = true;
            if (!string.IsNullOrEmpty(_logWatcher.CurrentWorldId) && _pendingInstanceEventId == null)
            {
                var loc = _logWatcher.CurrentLocation ?? _logWatcher.CurrentWorldId;
                // Avoid creating a duplicate if we already logged this exact instance on a previous run
                var lastJoin = _timeline.GetEvents().FirstOrDefault(e => e.Type == "instance_join");
                if (lastJoin != null && lastJoin.Location == loc)
                {
                    // Reuse the existing event so players continue to accumulate into it
                    _pendingInstanceEventId = lastJoin.Id;
                    // Pre-populate from previously persisted players so they aren't lost across restarts
                    if (lastJoin.Players != null)
                    {
                        foreach (var p in lastJoin.Players)
                        {
                            if (!string.IsNullOrEmpty(p.UserId))
                                _cumulativeInstancePlayers[p.UserId] = (p.DisplayName, p.Image ?? "");
                        }
                    }
                    // Resume world tracking without counting an extra visit (app restart in same world)
                    _worldTimeTracker.ResumeWorld(_logWatcher.CurrentWorldId);
                    _lastTrackedWorldId = _logWatcher.CurrentWorldId;
                }
                else
                {
                    HandleWorldChangedOnUiThread(_logWatcher.CurrentWorldId, loc);
                }
                // Populate cumulative players from the log watcher's already-parsed list
                foreach (var p in _logWatcher.GetCurrentPlayers())
                {
                    if (!string.IsNullOrEmpty(p.UserId) && !_cumulativeInstancePlayers.ContainsKey(p.UserId))
                        _cumulativeInstancePlayers[p.UserId] = (p.DisplayName, "");
                    // Also store name in UserTimeTracker for players already in instance on startup
                    if (!string.IsNullOrEmpty(p.UserId) && !string.IsNullOrEmpty(p.DisplayName))
                        _timeTracker.UpdateUserInfo(p.UserId, p.DisplayName, "");
                }
            }
        }

        SendToJS("vrcUser", new
        {
            id = user["id"]?.ToString() ?? "",
            displayName = user["displayName"]?.ToString() ?? "",
            image = VRChatApiService.GetUserImage(user),
            status = user["status"]?.ToString() ?? "offline",
            statusDescription = user["statusDescription"]?.ToString() ?? "",
            currentAvatar = user["currentAvatar"]?.ToString() ?? "",
            bio = user["bio"]?.ToString() ?? "",
            pronouns = user["pronouns"]?.ToString() ?? "",
            bioLinks = user["bioLinks"]?.ToObject<List<string>>() ?? new List<string>(),
            tags = user["tags"]?.ToObject<List<string>>() ?? new List<string>(),
            profilePicOverride    = _imgCache?.Get(user["profilePicOverride"]?.ToString() ?? "") ?? user["profilePicOverride"]?.ToString() ?? "",
            currentAvatarImageUrl = _imgCache?.Get(user["currentAvatarImageUrl"]?.ToString() ?? "") ?? user["currentAvatarImageUrl"]?.ToString() ?? "",
        });
    }

    private async Task VrcRefreshFriendsAsync(bool silent = false)
    {
        if (!_vrcApi.IsLoggedIn) return;
        try
        {
            var online = await _vrcApi.GetOnlineFriendsAsync();
            var offline = await _vrcApi.GetOfflineFriendsAsync();

            // Seed live friend store from authoritative REST data.
            // onlineIds dedup: some web-active friends appear in BOTH online and offline REST lists.
            // Without dedup the offline loop overwrites the correct online entry with location="offline".
            lock (_friendStore)
            {
                var onlineIds = new HashSet<string>(
                    online.Select(f => f["id"]?.ToString() ?? "").Where(id => !string.IsNullOrEmpty(id)));
                foreach (var f in online)
                {
                    var uid = f["id"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(uid)) _friendStore[uid] = f;
                }
                foreach (var f in offline)
                {
                    var uid = f["id"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(uid) || onlineIds.Contains(uid)) continue;
                    var copy = (JObject)f.DeepClone();
                    copy["location"] = "offline";
                    copy["status"]   = "offline";
                    _friendStore[uid] = copy;
                }
            }

            // Track IDs already seen from online list to avoid duplicates
            var seenIds = new HashSet<string>();

            var onlineList = online.Select(f =>
            {
                var id = f["id"]?.ToString() ?? "";
                seenIds.Add(id);
                var location = f["location"]?.ToString() ?? "";
                var platform = f["platform"]?.ToString() ?? f["last_platform"]?.ToString() ?? "";
                // "private" / "traveling" = in-game with hidden or transitioning instance.
                // The only reliable web indicator is platform == "web".
                bool isWebPlatform = platform.Equals("web", StringComparison.OrdinalIgnoreCase);
                bool isInGame = !string.IsNullOrEmpty(location)
                    && location != "offline"
                    && location != ""
                    && !isWebPlatform;
                bool isWebActive = !isInGame;
                return new
                {
                    id,
                    displayName = f["displayName"]?.ToString() ?? "",
                    image = VRChatApiService.GetUserImage(f),
                    status = f["status"]?.ToString() ?? "offline",
                    statusDescription = f["statusDescription"]?.ToString() ?? "",
                    location = location,
                    platform = platform,
                    presence = isInGame ? "game" : "web",
                    tags = f["tags"]?.ToObject<List<string>>() ?? new(),
                };
            }).ToList();

            var offlineList = offline
                .Where(f => !seenIds.Contains(f["id"]?.ToString() ?? ""))
                .Select(f => new
                {
                    id = f["id"]?.ToString() ?? "",
                    displayName = f["displayName"]?.ToString() ?? "",
                    image = VRChatApiService.GetUserImage(f),
                    status = "offline",
                    statusDescription = f["statusDescription"]?.ToString() ?? "",
                    location = "offline",
                    platform = f["last_platform"]?.ToString() ?? "",
                    presence = "offline",
                    tags = f["tags"]?.ToObject<List<string>>() ?? new(),
                }).ToList();

            // Sort: in-game first (by status), then web-active (by status), then offline
            var friendList = onlineList
                .OrderBy(f => f.presence == "game" ? 0 : 1)
                .ThenBy(f => f.status switch
                {
                    "join me" => 0,
                    "active" => 1,
                    "ask me" => 2,
                    "busy" => 3,
                    _ => 4
                })
                .Cast<object>()
                .Concat(offlineList.OrderBy(f => f.displayName).Cast<object>())
                .ToList();

            var counts = new
            {
                game = onlineList.Count(f => f.presence == "game"),
                web = onlineList.Count(f => f.presence == "web"),
                offline = offlineList.Count
            };

            // Timeline: seed known users on first run so we don't get false first-meet alerts
            if (!_timeline.KnownUsersSeeded)
            {
                var allIds = online.Select(f => f["id"]?.ToString())
                    .Concat(offline.Select(f => f["id"]?.ToString()))
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Cast<string>()
                    .ToList();
                _timeline.SeedKnownUsers(allIds);
            }

            // Friends Timeline: seed per-friend state on first load to avoid false positives
            if (!_friendStateSeeded)
            {
                foreach (var f in online)
                {
                    var uid = f["id"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(uid)) continue;
                    _friendLastLoc[uid]        = f["location"]?.ToString() ?? "";
                    _friendLastStatus[uid]     = f["status"]?.ToString() ?? "";
                    _friendLastStatusDesc[uid] = (f["statusDescription"]?.ToString() ?? "").Trim();
                    _friendLastBio[uid]        = (f["bio"]?.ToString() ?? "").Trim();
                    _friendNameImg[uid]        = (f["displayName"]?.ToString() ?? "", VRChatApiService.GetUserImage(f));
                }
                foreach (var f in offline)
                {
                    var uid = f["id"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(uid)) continue;
                    _friendLastLoc[uid]        = "offline";
                    _friendLastStatus[uid]     = "offline";
                    _friendLastStatusDesc[uid] = (f["statusDescription"]?.ToString() ?? "").Trim();
                    _friendLastBio[uid]        = (f["bio"]?.ToString() ?? "").Trim();
                    _friendNameImg[uid]        = (f["displayName"]?.ToString() ?? "", VRChatApiService.GetUserImage(f));
                }
                _friendStateSeeded = true;
            }
            else
            {
                // On subsequent refreshes, update name/image cache for all friends
                foreach (var f in online.Concat(offline))
                {
                    var uid = f["id"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(uid)) continue;
                    var img = VRChatApiService.GetUserImage(f);
                    if (img.Length > 0)
                        _friendNameImg[uid] = (f["displayName"]?.ToString() ?? _friendNameImg.GetValueOrDefault(uid).name ?? "", img);
                }
            }

            if (_settings.FfcEnabled) _cache.Save(CacheHandler.KeyFriends, new { friends = friendList, counts });
            Invoke(() =>
            {
                SendToJS("vrcFriends", new { friends = friendList, counts });
                if (!silent)
                    SendToJS("log", new { msg = $"VRChat: {counts.game} in-game, {counts.web} web, {counts.offline} offline", color = "ok" });
            });

            // Time tracking: update my location and tick tracker
            try
            {
                // Prefer log-derived location (no extra API call, immune to rate limits).
                // Fall back to API only if log watcher hasn't seen a room join yet.
                var myLoc = _logWatcher.CurrentLocation;
                if (string.IsNullOrEmpty(myLoc) || myLoc == "offline" || myLoc == "private" || myLoc == "traveling")
                    myLoc = await _vrcApi.GetMyLocationAsync();

                _timeTracker.SetMyLocation(myLoc ?? "");
                var trackData = onlineList.Select(f => (
                    userId: f.id,
                    location: f.location,
                    presence: f.presence
                )).ToList();

                if (!string.IsNullOrEmpty(myLoc) && myLoc != "offline" && myLoc != "private" && myLoc != "traveling")
                {
                    var logPlayers = _logWatcher.GetCurrentPlayers();
                    var logPlayerIds = new HashSet<string>(
                        logPlayers.Where(p => !string.IsNullOrEmpty(p.UserId)).Select(p => p.UserId));

                    // Fix: friends whose API location is "private" but confirmed in same instance via log
                    for (int i = 0; i < trackData.Count; i++)
                    {
                        var t = trackData[i];
                        if (t.location == "private" && logPlayerIds.Contains(t.userId))
                            trackData[i] = (t.userId, myLoc, t.presence);
                    }

                    // Track non-friends from LogWatcher
                    var trackedIds = new HashSet<string>(trackData.Select(t => t.userId));
                    foreach (var p in logPlayers)
                    {
                        if (!string.IsNullOrEmpty(p.UserId) && !trackedIds.Contains(p.UserId))
                            trackData.Add((userId: p.UserId, location: myLoc, presence: "game"));
                    }
                }

                _timeTracker.Tick(trackData);
                _timeTracker.Save();

                // World time tracking – visit detection is handled by the log watcher (HandleWorldChangedOnUiThread).
                // Here we only accumulate elapsed time for the current world.
                var (myWorldId, _, _) = VRChatApiService.ParseLocation(myLoc);
                if (!string.IsNullOrEmpty(myWorldId) && myWorldId.StartsWith("wrld_"))
                {
                    _worldTimeTracker.Tick();
                    _worldTimeTracker.Save();
                }
                else if (!string.IsNullOrEmpty(_lastTrackedWorldId))
                {
                    _lastTrackedWorldId = "";
                }
            }
            catch { /* don't break refresh if tracking fails */ }
        }
        catch (Exception ex)
        {
            if (!silent)
                Invoke(() => SendToJS("log", new { msg = $"VRChat: Friends error — {ex.Message}", color = "err" }));
        }
    }

    private async Task VrcUpdateStatusAsync(string status, string statusDescription)
    {
        if (!_vrcApi.IsLoggedIn) return;
        var user = await _vrcApi.UpdateStatusAsync(status, statusDescription);
        if (user != null)
        {
            SendVrcUserData(user);
            SendToJS("log", new { msg = $"VRChat: Status updated to {status}", color = "ok" });
        }
        else
        {
            SendToJS("log", new { msg = "VRChat: Failed to update status", color = "err" });
        }
    }

    private async Task EnrichModerationsWithImagesAsync(JArray entries)
    {
        var tasks = entries.OfType<JObject>().Select(async entry =>
        {
            var uid = entry["targetUserId"]?.ToString();
            if (string.IsNullOrEmpty(uid)) return;
            var user = await _vrcApi.GetUserAsync(uid);
            if (user != null)
                entry["image"] = VRChatApiService.GetUserImage(user);
        });
        await Task.WhenAll(tasks);
    }

    // ── Live Friend Store helpers ─────────────────────────────────────────────

    /// <summary>
    /// Merges a WebSocket user object (and optional location/platform) into the live friend store.
    /// Call from WS event handlers instead of triggering a REST friends refresh.
    /// </summary>
    private void MergeFriendStore(string userId, JObject? userObj,
                                   string? location = null, string? platform = null,
                                   bool wentOffline = false)
    {
        if (string.IsNullOrEmpty(userId)) return;
        lock (_friendStore)
        {
            if (!_friendStore.TryGetValue(userId, out var entry))
            { entry = new JObject(); _friendStore[userId] = entry; }
            if (userObj != null)
            {
                foreach (var prop in userObj.Properties()) entry[prop.Name] = prop.Value;
                var img = VRChatApiService.GetUserImage(userObj);
                if (!string.IsNullOrEmpty(img))
                    _friendNameImg[userId] = (userObj["displayName"]?.ToString() ?? _friendNameImg.GetValueOrDefault(userId).name ?? "", img);
            }
            if (location != null)  entry["location"]      = location;
            if (platform != null)  entry["last_platform"] = platform;
            if (wentOffline)
            {
                entry["location"] = "offline";
                entry["status"]   = "offline";
            }
        }
    }

    /// <summary>
    /// Builds the vrcFriends payload from the live store and pushes it to JS.
    /// Same shape as VrcRefreshFriendsAsync — no REST calls.
    /// </summary>
    private void PushFriendsFromStore()
    {
        List<JObject> snapshot;
        lock (_friendStore) snapshot = _friendStore.Values.ToList();

        var list = snapshot.Select(f =>
        {
            var location  = f["location"]?.ToString() ?? "";
            var platform  = f["last_platform"]?.ToString() ?? f["platform"]?.ToString() ?? "";
            bool isWebPlatform = platform.Equals("web", StringComparison.OrdinalIgnoreCase);
            bool isInGame = !string.IsNullOrEmpty(location)
                && location != "offline"
                && location != ""
                && !isWebPlatform;
            var status = f["status"]?.ToString() ?? "offline";
            var presence = (location == "offline" && status == "offline") ? "offline"
                         : isInGame ? "game" : "web";
            return new
            {
                id                = f["id"]?.ToString() ?? "",
                displayName       = f["displayName"]?.ToString() ?? "",
                image             = VRChatApiService.GetUserImage(f),
                status,
                statusDescription = f["statusDescription"]?.ToString() ?? "",
                location,
                platform,
                presence,
                tags              = f["tags"]?.ToObject<List<string>>() ?? new List<string>(),
            };
        })
        .OrderBy(f => f.presence switch { "game" => 0, "web" => 1, _ => 2 })
        .ThenBy(f => f.status switch
        {
            "join me" => 0, "active" => 1, "ask me" => 2, "busy" => 3, _ => 4
        })
        .ThenBy(f => f.displayName)
        .ToList();

        var counts = new
        {
            game    = list.Count(f => f.presence == "game"),
            web     = list.Count(f => f.presence == "web"),
            offline = list.Count(f => f.presence == "offline"),
        };

        Invoke(() => SendToJS("vrcFriends", new { friends = list, counts }));
    }

    // ─────────────────────────────────────────────────────────────────────────

    private async Task VrcGetFriendDetailAsync(string userId)
    {
        if (!_vrcApi.IsLoggedIn) return;

        // Helper: send a payload, then silently refresh & re-cache in background
        void ServeAndRefresh(object immediatePayload)
        {
            SendToJS("vrcFriendDetail", immediatePayload);
            _ = Task.Run(async () =>
            {
                try
                {
                    var fresh = await BuildUserDetailPayloadAsync(userId);
                    if (fresh == null) return;
                    Invoke(() =>
                    {
                        _userDetailCache[userId] = (fresh, DateTime.UtcNow);
                        if (_settings.FfcEnabled) _cache.Save(CacheHandler.KeyUserProfile(userId), fresh);
                        SendToJS("vrcFriendDetail", fresh);
                    });
                }
                catch { }
            });
        }

        // 1. In-memory cache → instant (only when FFC enabled)
        if (_settings.FfcEnabled && _userDetailCache.TryGetValue(userId, out var cached))
        {
            ServeAndRefresh(cached.payload);
            return;
        }

        // 2. Disk cache → instant (also populates in-memory for this session)
        var diskCached = _settings.FfcEnabled ? _cache.LoadRaw(CacheHandler.KeyUserProfile(userId)) : null;
        if (diskCached is JObject diskProfile)
        {
            // Overlay live fields from the friend store (kept fresh by WebSocket).
            // Falls back to safe defaults if the store has no entry yet.
            JObject? live;
            lock (_friendStore) _friendStore.TryGetValue(userId, out live);
            diskProfile["status"]              = live?["status"]?.ToString() ?? "offline";
            diskProfile["statusDescription"]   = live?["statusDescription"]?.ToString() ?? "";
            diskProfile["location"]            = live?["location"]?.ToString() ?? "";
            diskProfile["worldName"]           = "";
            diskProfile["worldThumb"]          = "";
            diskProfile["instanceType"]        = "";
            diskProfile["userCount"]           = 0;
            diskProfile["worldCapacity"]       = 0;
            diskProfile["canJoin"]             = false;
            diskProfile["canRequestInvite"]    = false;
            diskProfile["inSameInstance"]      = false;
            diskProfile["travelingToLocation"] = "";
            diskProfile["state"]               = "";
            _userDetailCache[userId] = (diskProfile, DateTime.UtcNow);
            ServeAndRefresh(diskProfile);
            return;
        }

        // 3. Cold fetch → block once, then every future open is instant
        try
        {
            var payload = await BuildUserDetailPayloadAsync(userId);
            if (payload == null)
            {
                SendToJS("vrcFriendDetailError", new { error = "Could not load user profile" });
                return;
            }
            _userDetailCache[userId] = (payload, DateTime.UtcNow);
            if (_settings.FfcEnabled) _cache.Save(CacheHandler.KeyUserProfile(userId), payload);
            SendToJS("vrcFriendDetail", payload);
        }
        catch (Exception ex)
        {
            SendToJS("vrcFriendDetailError", new { error = ex.Message });
            SendToJS("log", new { msg = $"VRChat: Error loading profile — {ex.Message}", color = "err" });
        }
    }

    private async Task<object?> BuildUserDetailPayloadAsync(string userId)
    {
        // Use live store data if available for location/status (kept fresh by WebSocket events).
        // Always fetch the full user object via REST when store data lacks badges, since the
        // friends-list endpoints (/auth/user/friends) do not include the badges array.
        JObject? user;
        lock (_friendStore) _friendStore.TryGetValue(userId, out user);
        if (user == null || user["badges"] == null)
        {
            var fresh = await _vrcApi.GetUserAsync(userId);
            if (fresh != null) user = fresh;
            else if (user == null) return null;
        }

        var location = user["location"]?.ToString() ?? "private";
        var (worldId, instanceId, instanceType) = VRChatApiService.ParseLocation(location);
        bool hasWorld = !string.IsNullOrEmpty(worldId) && worldId.StartsWith("wrld_");

        // Launch all secondary fetches in parallel after GetUser completes
        var worldTask   = hasWorld ? _vrcApi.GetWorldAsync(worldId)    : Task.FromResult<JObject?>(null);
        var instTask    = hasWorld ? _vrcApi.GetInstanceAsync(location) : Task.FromResult<JObject?>(null);
        var noteTask    = _vrcApi.GetUserNoteAsync(userId);
        var repGrpTask  = _vrcApi.GetUserRepresentedGroupAsync(userId);
        var grpsTask    = _vrcApi.GetUserGroupsByIdAsync(userId);
        var worldsTask  = _vrcApi.GetUserWorldsAsync(userId);
        var mutualsTask = _vrcApi.GetUserMutualsAsync(userId);

        // Wait for all; ContinueWith swallows individual task exceptions
        await Task.WhenAll(new Task[] { worldTask, instTask, noteTask, repGrpTask, grpsTask, worldsTask, mutualsTask }
            .Select(t => t.ContinueWith(_ => { })));

        var world    = worldTask.IsCompletedSuccessfully   ? worldTask.Result   : null;
        var inst     = instTask.IsCompletedSuccessfully    ? instTask.Result    : null;
        var noteObj  = noteTask.IsCompletedSuccessfully    ? noteTask.Result    : null;
        var repGroup = repGrpTask.IsCompletedSuccessfully  ? repGrpTask.Result  : null;
        var groups   = grpsTask.IsCompletedSuccessfully    ? grpsTask.Result    : new JArray();
        var worlds   = worldsTask.IsCompletedSuccessfully  ? worldsTask.Result  : new JArray();
        var (mutualsArr, mutualsOptedOut) = mutualsTask.IsCompletedSuccessfully
            ? mutualsTask.Result : (new JArray(), false);
        // Badges come from the full user object via GET /users/{userId} (ensured above)
        var badgesArr = user["badges"] as JArray ?? new JArray();

        string worldName     = world?["name"]?.ToString() ?? "";
        string worldThumb    = world?["thumbnailImageUrl"]?.ToString() ?? "";
        int    worldCapacity = world?["capacity"]?.Value<int>() ?? 0;
        int    userCount     = inst?["n_users"]?.Value<int>() ?? inst?["userCount"]?.Value<int>() ?? 0;
        string userNote      = noteObj?["note"]?.ToString() ?? "";

        bool canJoin = instanceType == "public" || instanceType == "friends" || instanceType == "friends+"
                    || instanceType == "hidden"
                    || instanceType == "group-public" || instanceType == "group-plus"
                    || instanceType == "group-members" || instanceType == "group";
        bool canRequestInvite = instanceType == "private";
        bool isInWorld = !string.IsNullOrEmpty(worldId) && location != "private" && location != "offline" && location != "traveling";

        object? representedGroup = null;
        if (repGroup != null && !string.IsNullOrEmpty(repGroup["id"]?.ToString()))
        {
            representedGroup = new
            {
                id            = repGroup["id"]?.ToString() ?? "",
                name          = repGroup["name"]?.ToString() ?? "",
                shortCode     = repGroup["shortCode"]?.ToString() ?? "",
                discriminator = repGroup["discriminator"]?.ToString() ?? "",
                iconUrl       = repGroup["iconUrl"]?.ToString() ?? "",
                bannerUrl     = repGroup["bannerUrl"]?.ToString() ?? "",
                memberCount   = repGroup["memberCount"]?.Value<int>() ?? 0,
            };
        }

        List<object> userGroups = new();
        foreach (var g in groups)
        {
            var gid = g["groupId"]?.ToString() ?? g["id"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(gid)) continue;
            userGroups.Add(new
            {
                id            = gid,
                name          = g["name"]?.ToString() ?? "",
                shortCode     = g["shortCode"]?.ToString() ?? "",
                discriminator = g["discriminator"]?.ToString() ?? "",
                iconUrl       = g["iconUrl"]?.ToString() ?? g["iconId"]?.ToString() ?? "",
                bannerUrl     = g["bannerUrl"]?.ToString() ?? "",
                memberCount   = g["memberCount"]?.Value<int>() ?? 0,
                isRepresenting = g["isRepresenting"]?.Value<bool>() ?? false,
            });
        }

        List<object> userWorlds = new();
        foreach (var w in worlds)
        {
            var wObj = w as JObject;
            if (wObj == null) continue;
            userWorlds.Add(new
            {
                id                = wObj["id"]?.ToString() ?? "",
                name              = wObj["name"]?.ToString() ?? "",
                thumbnailImageUrl = wObj["thumbnailImageUrl"]?.ToString() ?? "",
                occupants         = wObj["occupants"]?.Value<int>() ?? 0,
                favorites         = wObj["favorites"]?.Value<int>() ?? 0,
                visits            = wObj["visits"]?.Value<int>() ?? 0,
            });
        }

        List<object> mutualsList = new();
        foreach (var mu in mutualsArr)
        {
            var muObj = mu as JObject;
            if (muObj == null) continue;
            var muId       = muObj["id"]?.ToString() ?? "";
            var muImage    = (_friendNameImg.TryGetValue(muId, out var muFi) && !string.IsNullOrEmpty(muFi.image))
                                ? muFi.image
                                : VRChatApiService.GetUserImage(muObj);
            var muLocation = muObj["location"]?.ToString() ?? "";
            var muStatus   = muObj["status"]?.ToString() ?? "offline";
            bool muIsInGame  = !string.IsNullOrEmpty(muLocation)
                && muLocation != "offline" && muLocation != "private" && muLocation != "traveling";
            bool muIsOffline = muStatus == "offline" || muLocation == "offline";
            mutualsList.Add(new
            {
                id                = muObj["id"]?.ToString() ?? "",
                displayName       = muObj["displayName"]?.ToString() ?? "",
                image             = muImage,
                status            = muStatus,
                statusDescription = muObj["statusDescription"]?.ToString() ?? "",
                presence          = muIsOffline ? "offline" : muIsInGame ? "game" : "web",
            });
        }

        List<object> badges = new();
        foreach (var b in badgesArr)
        {
            var bObj = b as JObject;
            if (bObj == null) continue;
            var imageUrl = bObj["badgeImageUrl"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(imageUrl)) continue;
            badges.Add(new
            {
                id          = bObj["badgeId"]?.ToString() ?? "",
                name        = bObj["badgeName"]?.ToString() ?? "",
                description = bObj["badgeDescription"]?.ToString() ?? "",
                imageUrl,
                showcased   = bObj["showcased"]?.Value<bool>() ?? false,
            });
        }

        // Check via log watcher (reliable) rather than API location (often "private")
        var isCoPresent = _logWatcher.GetCurrentPlayers().Any(p => p.UserId == userId);
        var (totalSeconds, lastSeenLocal) = _timeTracker.GetUserStats(userId, isCoPresent);
        var lastLogin = user["last_login"]?.ToString() ?? "";

        return new
        {
            id = user["id"]?.ToString() ?? "",
            displayName = user["displayName"]?.ToString() ?? "",
            image = VRChatApiService.GetUserImage(user),
            status = user["status"]?.ToString() ?? "offline",
            statusDescription = user["statusDescription"]?.ToString() ?? "",
            bio = user["bio"]?.ToString() ?? "",
            lastLogin,
            dateJoined = user["date_joined"]?.ToString() ?? "",
            location,
            worldName,
            worldThumb,
            instanceType,
            userCount,
            worldCapacity,
            isFriend = user["isFriend"]?.Value<bool>() ?? !string.IsNullOrEmpty(user["friendKey"]?.ToString()),
            canJoin = isInWorld && canJoin,
            canRequestInvite = canRequestInvite,
            canInvite = true,
            currentAvatarImageUrl = _imgCache?.Get(user["currentAvatarImageUrl"]?.ToString() ?? "") ?? user["currentAvatarImageUrl"]?.ToString() ?? "",
            profilePicOverride    = _imgCache?.Get(user["profilePicOverride"]?.ToString() ?? "") ?? user["profilePicOverride"]?.ToString() ?? "",
            tags = user["tags"]?.ToObject<List<string>>() ?? new(),
            note = user["note"]?.ToString() ?? "",
            friendKey = user["friendKey"]?.ToString() ?? "",
            travelingToLocation = user["travelingToLocation"]?.ToString() ?? "",
            state = user["state"]?.ToString() ?? "",
            lastPlatform = user["last_platform"]?.ToString() ?? "",
            platform = user["platform"]?.ToString() ?? "",
            userNote,
            totalTimeSeconds = totalSeconds,
            inSameInstance = _logWatcher.GetCurrentPlayers().Any(p => p.UserId == userId),
            lastSeenTracked = lastSeenLocal,
            pronouns = user["pronouns"]?.ToString() ?? "",
            ageVerificationStatus = user["ageVerificationStatus"]?.ToString() ?? "",
            ageVerified = user["ageVerified"]?.Value<bool>() ?? false,
            representedGroup,
            userGroups,
            mutuals = mutualsList,
            mutualsOptedOut,
            userWorlds,
            bioLinks = user["bioLinks"]?.ToObject<List<string>>() ?? new List<string>(),
            isFavorited = _favoriteFriends.ContainsKey(userId),
            favFriendId = _favoriteFriends.GetValueOrDefault(userId, ""),
            badges,
        };
    }

    // Timeline - LogWatcher event handlers (run on UI thread)

    private void HandleWorldChangedOnUiThread(string worldId, string location)
    {
        // Finalise previous instance event: refresh images from cache, save, push to JS
        if (_pendingInstanceEventId != null)
        {
            // Fill any missing images from the player cache
            foreach (var uid in _cumulativeInstancePlayers.Keys.ToList())
            {
                if (_playerImageCache.TryGetValue(uid, out var cached) && !string.IsNullOrEmpty(cached.image))
                {
                    var existing = _cumulativeInstancePlayers[uid];
                    _cumulativeInstancePlayers[uid] = (existing.displayName, cached.image);
                }
            }

            var finalPlayers = _cumulativeInstancePlayers.Select(kv => new TimelineService.PlayerSnap
            {
                UserId      = kv.Key,
                DisplayName = kv.Value.displayName,
                Image       = ResolvePlayerImage(kv.Key, kv.Value.image)
            }).ToList();
            var prevId = _pendingInstanceEventId;
            _timeline.UpdateEvent(prevId, ev => ev.Players = finalPlayers);
            var finalEv = _timeline.GetEvents().FirstOrDefault(e => e.Id == prevId);
            if (finalEv != null) SendToJS("timelineEvent", BuildTimelinePayload(finalEv));
        }

        _cumulativeInstancePlayers.Clear();
        _meetAgainThisInstance.Clear();
        _instanceSnapshotTimer?.Dispose();
        _instanceSnapshotTimer = null;

        // Create new instance_join timeline event (world name resolved asynchronously)
        var evId  = Guid.NewGuid().ToString("N")[..8];
        _pendingInstanceEventId = evId;

        var instEv = new TimelineService.TimelineEvent
        {
            Id        = evId,
            Type      = "instance_join",
            Timestamp = DateTime.UtcNow.ToString("o"),
            WorldId   = worldId,
            Location  = location
        };
        _timeline.AddEvent(instEv);
        SendToJS("timelineEvent", BuildTimelinePayload(instEv));
        SendToJS("log", new { msg = $"[TIMELINE] Instance join: {worldId}", color = "sec" });

        // Track world visit immediately (log watcher fires on every actual world change)
        _worldTimeTracker.SetCurrentWorld(worldId);
        _lastTrackedWorldId = worldId;

        // Immediately refresh instance panel so sidebar doesn't wait for the 60s poll
        SendToJS("vrcWorldJoined", new { worldId });

        // After 15 s: snapshot players + resolve world name
        _instanceSnapshotTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                Invoke(async () =>
                {
                    try
                    {
                        // Refresh any images that have since been fetched (e.g. via requestInstanceInfo)
                        foreach (var uid in _cumulativeInstancePlayers.Keys.ToList())
                        {
                            if (_playerImageCache.TryGetValue(uid, out var cached) && !string.IsNullOrEmpty(cached.image))
                            {
                                var existing = _cumulativeInstancePlayers[uid];
                                _cumulativeInstancePlayers[uid] = (existing.displayName, cached.image);
                            }
                        }

                        var snap = _cumulativeInstancePlayers.Select(kv => new TimelineService.PlayerSnap
                        {
                            UserId      = kv.Key,
                            DisplayName = kv.Value.displayName,
                            Image       = ResolvePlayerImage(kv.Key, kv.Value.image)
                        }).ToList();

                        string wName = "", wThumb = "";
                        if (worldId.StartsWith("wrld_") && _vrcApi.IsLoggedIn)
                        {
                            var world = await _vrcApi.GetWorldAsync(worldId);
                            if (world != null)
                            {
                                wName  = world["name"]?.ToString()                ?? "";
                                wThumb = world["thumbnailImageUrl"]?.ToString()   ?? "";
                            }
                        }

                        _timeline.UpdateEvent(evId, ev =>
                        {
                            ev.WorldName  = wName;
                            ev.WorldThumb = wThumb;
                            ev.Players    = snap;
                        });

                        // Store name/thumb in WorldTimeTracker so Time Spent tab can show it
                        // even for worlds that aren't in the timeline top-200
                        if (!string.IsNullOrEmpty(wName))
                            _worldTimeTracker.UpdateWorldInfo(worldId, wName, wThumb);

                        var updated = _timeline.GetEvents().FirstOrDefault(e => e.Id == evId);
                        if (updated != null) SendToJS("timelineEvent", BuildTimelinePayload(updated));
                    }
                    catch { }
                });
            }
            catch { }
        }, null, 15_000, System.Threading.Timeout.Infinite);
    }

    private void HandlePlayerJoinedOnUiThread(string userId, string displayName)
    {
        // Skip events for the local player; VRChat logs OnPlayerJoined for self too
        if (!string.IsNullOrEmpty(_currentVrcUserId) && userId == _currentVrcUserId) return;

        // Accumulate into instance player history
        if (!string.IsNullOrEmpty(userId) && !_cumulativeInstancePlayers.ContainsKey(userId))
        {
            var img = _playerImageCache.TryGetValue(userId, out var c) ? c.image : "";
            _cumulativeInstancePlayers[userId] = (displayName, img);
            // Store name in UserTimeTracker so this player appears in Time Spent list
            // even when they are not a friend and not in the timeline top-200
            _timeTracker.UpdateUserInfo(userId, displayName, img);

            // Live-update the instance_join timeline event so the UI shows players immediately
            if (_pendingInstanceEventId != null)
            {
                var evId = _pendingInstanceEventId;
                var snap = _cumulativeInstancePlayers.Select(kv => new TimelineService.PlayerSnap
                {
                    UserId      = kv.Key,
                    DisplayName = kv.Value.displayName,
                    Image       = ResolvePlayerImage(kv.Key, kv.Value.image)
                }).ToList();
                _timeline.UpdateEvent(evId, ev => ev.Players = snap);
                var updated = _timeline.GetEvents().FirstOrDefault(e => e.Id == evId);
                if (updated != null) SendToJS("timelineEvent", BuildTimelinePayload(updated));
            }
        }

        // First-meet detection, only after known-users set is seeded
        if (!string.IsNullOrEmpty(userId) && _timeline.KnownUsersSeeded && !_timeline.IsKnownUser(userId))
        {
            _timeline.AddKnownUser(userId);
            var img = _playerImageCache.TryGetValue(userId, out var ci) ? ci.image : "";
            var meetEv = new TimelineService.TimelineEvent
            {
                Type      = "first_meet",
                Timestamp = DateTime.UtcNow.ToString("o"),
                UserId    = userId,
                UserName  = displayName,
                UserImage = img,
                WorldId   = _logWatcher.CurrentWorldId ?? "",
                Location  = _logWatcher.CurrentLocation ?? ""
            };
            _timeline.AddEvent(meetEv);
            SendToJS("timelineEvent", BuildTimelinePayload(meetEv));
            SendToJS("log", new { msg = $"[TIMELINE] First meet: {displayName}", color = "sec" });

            // If no image yet, fetch async and update the event
            if (string.IsNullOrEmpty(img) && _vrcApi.IsLoggedIn)
            {
                var evId = meetEv.Id;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var profile = await _vrcApi.GetUserAsync(userId);
                        if (profile == null) return;
                        var fetchedImg = VRChatApiService.GetUserImage(profile);
                        if (string.IsNullOrEmpty(fetchedImg)) return;
                        lock (_playerImageCache) _playerImageCache[userId] = (fetchedImg, DateTime.Now);
                        _timeline.UpdateEvent(evId, ev => ev.UserImage = fetchedImg);
                        var updated = _timeline.GetEvents().FirstOrDefault(e => e.Id == evId);
                        if (updated != null) Invoke(() => SendToJS("timelineEvent", BuildTimelinePayload(updated)));
                    }
                    catch { }
                });
            }
        }
        else if (!string.IsNullOrEmpty(userId))
        {
            _timeline.AddKnownUser(userId);

            // Meet Again: known user, not yet seen in this instance
            if (_timeline.KnownUsersSeeded && !_meetAgainThisInstance.Contains(userId))
            {
                _meetAgainThisInstance.Add(userId);
                var img = _playerImageCache.TryGetValue(userId, out var cImg) ? cImg.image : "";
                var meetAgainEv = new TimelineService.TimelineEvent
                {
                    Type      = "meet_again",
                    Timestamp = DateTime.UtcNow.ToString("o"),
                    UserId    = userId,
                    UserName  = displayName,
                    UserImage = img,
                    WorldId   = _logWatcher.CurrentWorldId ?? "",
                    Location  = _logWatcher.CurrentLocation ?? ""
                };
                _timeline.AddEvent(meetAgainEv);
                SendToJS("timelineEvent", BuildTimelinePayload(meetAgainEv));

                // Async-fetch image if missing
                if (string.IsNullOrEmpty(img) && _vrcApi.IsLoggedIn)
                {
                    var maEvId = meetAgainEv.Id;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var profile = await _vrcApi.GetUserAsync(userId);
                            if (profile == null) return;
                            var fetchedImg = VRChatApiService.GetUserImage(profile);
                            if (string.IsNullOrEmpty(fetchedImg)) return;
                            lock (_playerImageCache) _playerImageCache[userId] = (fetchedImg, DateTime.Now);
                            _timeline.UpdateEvent(maEvId, ev => ev.UserImage = fetchedImg);
                            var updated = _timeline.GetEvents().FirstOrDefault(e => e.Id == maEvId);
                            if (updated != null) Invoke(() => SendToJS("timelineEvent", BuildTimelinePayload(updated)));
                        }
                        catch { }
                    });
                }
            }
        }
    }

    // Friends Timeline - WebSocket event handlers

    private void OnWsFriendLocation(object? sender, FriendEventArgs e)
    {
        if (string.IsNullOrEmpty(e.UserId) || !_friendStateSeeded) return;

        // Update live store and push to JS — no REST call needed
        MergeFriendStore(e.UserId, e.User, location: e.Location,
            platform: string.IsNullOrEmpty(e.Platform) ? null : e.Platform);
        PushFriendsFromStore();

        // Update name/image cache
        if (e.User != null)
            _friendNameImg[e.UserId] = (
                e.User["displayName"]?.ToString() ?? _friendNameImg.GetValueOrDefault(e.UserId).name ?? "",
                VRChatApiService.GetUserImage(e.User).Length > 0
                    ? VRChatApiService.GetUserImage(e.User)
                    : _friendNameImg.GetValueOrDefault(e.UserId).image ?? ""
            );

        var newLoc = e.Location;

        // Derive worldId exclusively from the location string itself.
        // VRChat can send friend-location with location="traveling" but worldId="wrld_xxx"
        // (destination announced before the user has actually loaded in).
        // Using e.WorldId would cause a GPS entry during traveling — we only want one
        // entry after the friend has truly arrived.
        var worldId = newLoc.Contains(':') ? newLoc.Split(':')[0] : newLoc;

        // Only log when the location string itself confirms a real world
        if (!worldId.StartsWith("wrld_")) { _friendLastLoc[e.UserId] = newLoc; return; }

        var oldLoc     = _friendLastLoc.GetValueOrDefault(e.UserId, "");
        var oldWorldId = oldLoc.Contains(':') ? oldLoc.Split(':')[0] : oldLoc;

        // Skip if no world change — VRChat fires a second friend-location event once
        // the instance is fully loaded (same wrld_ but with full instance params appended).
        if (oldLoc == newLoc || oldWorldId == worldId) { _friendLastLoc[e.UserId] = newLoc; return; }

        _friendLastLoc[e.UserId] = newLoc;

        var (fname, fimg) = _friendNameImg.GetValueOrDefault(e.UserId, ("", ""));
        var fev = new TimelineService.FriendTimelineEvent
        {
            Type        = "friend_gps",
            FriendId    = e.UserId,
            FriendName  = fname,
            FriendImage = fimg,
            WorldId     = worldId,
            Location    = newLoc,
        };
        _timeline.AddFriendEvent(fev);

        var fevPayload = BuildFriendTimelinePayload(fev);
        Invoke(() => SendToJS("friendTimelineEvent", fevPayload));

        // Async-resolve world name + thumb
        var evId = fev.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                var world = await _vrcApi.GetWorldAsync(worldId);
                if (world == null) return;
                var wname  = world["name"]?.ToString() ?? "";
                var wthumb = world["thumbnailImageUrl"]?.ToString() ?? "";
                _timeline.UpdateFriendEventWorld(evId, wname, wthumb);
                var updated = _timeline.GetFriendEvents().FirstOrDefault(x => x.Id == evId);
                if (updated != null)
                    Invoke(() => SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(updated)));
            }
            catch { }
        });
    }

    private void OnWsFriendOffline(object? sender, FriendEventArgs e)
    {
        if (string.IsNullOrEmpty(e.UserId) || !_friendStateSeeded) return;

        // Mark offline in store and push — friend-offline has no user object
        MergeFriendStore(e.UserId, null, wentOffline: true);
        PushFriendsFromStore();

        var (fname, fimg) = _friendNameImg.GetValueOrDefault(e.UserId, ("", ""));
        if (e.User != null)
        {
            fname = e.User["displayName"]?.ToString() ?? fname;
            var img = VRChatApiService.GetUserImage(e.User);
            if (img.Length > 0) fimg = img;
            _friendNameImg[e.UserId] = (fname, fimg);
        }

        _friendLastLoc[e.UserId] = "offline";

        var fev = new TimelineService.FriendTimelineEvent
        {
            Type        = "friend_offline",
            FriendId    = e.UserId,
            FriendName  = fname,
            FriendImage = fimg,
        };
        _timeline.AddFriendEvent(fev);
        Invoke(() => SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev)));
    }

    private void OnWsFriendOnline(object? sender, FriendEventArgs e)
    {
        if (string.IsNullOrEmpty(e.UserId) || !_friendStateSeeded) return;

        // Update store with online user data and push — no REST call needed
        // Pass "" (not null) when no location: clears any previous "offline" location in the store
        MergeFriendStore(e.UserId, e.User,
            location: string.IsNullOrEmpty(e.Location) ? "" : e.Location,
            platform: string.IsNullOrEmpty(e.Platform) ? null : e.Platform);
        PushFriendsFromStore();

        var fname = "";
        var fimg  = "";
        if (e.User != null)
        {
            fname = e.User["displayName"]?.ToString() ?? "";
            fimg  = VRChatApiService.GetUserImage(e.User);
            _friendNameImg[e.UserId] = (fname, fimg);
        }
        else
        {
            (fname, fimg) = _friendNameImg.GetValueOrDefault(e.UserId, ("", ""));
        }

        // Guard: don't let an empty/non-world online event overwrite a world location
        // that a friend-location event may have already written.
        var onlineLoc = e.Location ?? "";
        var curLoc    = _friendLastLoc.GetValueOrDefault(e.UserId, "");
        _friendLastLoc[e.UserId] = (string.IsNullOrEmpty(onlineLoc) && curLoc.StartsWith("wrld_"))
            ? curLoc : onlineLoc;

        var fev = new TimelineService.FriendTimelineEvent
        {
            Type        = "friend_online",
            FriendId    = e.UserId,
            FriendName  = fname,
            FriendImage = fimg,
        };
        _timeline.AddFriendEvent(fev);
        Invoke(() => SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev)));
    }

    private void OnWsFriendUpdated(object? sender, FriendEventArgs e)
    {
        if (e.User == null || string.IsNullOrEmpty(e.UserId) || !_friendStateSeeded) return;

        // Update store with fresh user data and push — no REST call needed
        MergeFriendStore(e.UserId, e.User);
        PushFriendsFromStore();

        var fname  = e.User["displayName"]?.ToString() ?? _friendNameImg.GetValueOrDefault(e.UserId).name ?? "";
        var fimg   = VRChatApiService.GetUserImage(e.User);
        if (fimg.Length == 0) fimg = _friendNameImg.GetValueOrDefault(e.UserId).image ?? "";
        _friendNameImg[e.UserId] = (fname, fimg);

        var newStatus     = e.User["status"]?.ToString() ?? "";
        var newStatusDesc = (e.User["statusDescription"]?.ToString() ?? "").Trim();
        var newBio        = (e.User["bio"]?.ToString() ?? "").Trim();

        // Status change
        if (!string.IsNullOrEmpty(newStatus))
        {
            var oldStatus = _friendLastStatus.GetValueOrDefault(e.UserId, "");
            if (oldStatus != newStatus && !string.IsNullOrEmpty(oldStatus))
            {
                var fev = new TimelineService.FriendTimelineEvent
                {
                    Type        = "friend_status",
                    FriendId    = e.UserId,
                    FriendName  = fname,
                    FriendImage = fimg,
                    OldValue    = oldStatus,
                    NewValue    = newStatus,
                };
                _timeline.AddFriendEvent(fev);
                Invoke(() => SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev)));
            }
            _friendLastStatus[e.UserId] = newStatus;
        }

        // Status text change
        var oldStatusDesc = _friendLastStatusDesc.GetValueOrDefault(e.UserId, "");
        if (oldStatusDesc != newStatusDesc && !string.IsNullOrEmpty(oldStatusDesc))
        {
            var fev = new TimelineService.FriendTimelineEvent
            {
                Type        = "friend_statusdesc",
                FriendId    = e.UserId,
                FriendName  = fname,
                FriendImage = fimg,
                OldValue    = oldStatusDesc,
                NewValue    = newStatusDesc,
            };
            _timeline.AddFriendEvent(fev);
            Invoke(() => SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev)));
        }
        _friendLastStatusDesc[e.UserId] = newStatusDesc;

        // Bio change
        var oldBio = _friendLastBio.GetValueOrDefault(e.UserId, "");
        if (!string.IsNullOrEmpty(newBio) && oldBio != newBio && !string.IsNullOrEmpty(oldBio))
        {
            var fev = new TimelineService.FriendTimelineEvent
            {
                Type        = "friend_bio",
                FriendId    = e.UserId,
                FriendName  = fname,
                FriendImage = fimg,
                OldValue    = oldBio.Length > 500 ? oldBio[..500] : oldBio,
                NewValue    = newBio.Length > 500 ? newBio[..500] : newBio,
            };
            _timeline.AddFriendEvent(fev);
            Invoke(() => SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev)));
        }
        if (!string.IsNullOrEmpty(newBio))
            _friendLastBio[e.UserId] = newBio;
    }

    // Timeline - helpers

    private object BuildTimelinePayload(TimelineService.TimelineEvent ev) => new
    {
        id          = ev.Id,
        type        = ev.Type,
        timestamp   = ev.Timestamp,
        worldId     = ev.WorldId,
        worldName   = ev.WorldName,
        worldThumb  = ev.WorldThumb,
        location    = ev.Location,
        players     = ev.Players.Select(p => new { userId = p.UserId, displayName = p.DisplayName, image = ResolvePlayerImage(p.UserId, p.Image) }).ToList(),
        photoPath   = ev.PhotoPath,
        photoUrl    = ev.PhotoUrl,
        userId      = ev.UserId,
        userName    = ev.UserName,
        userImage   = ResolvePlayerImage(ev.UserId, ev.UserImage),
        notifId     = ev.NotifId,
        notifType   = ev.NotifType,
        senderName  = ev.SenderName,
        senderId    = ev.SenderId,
        senderImage = ResolvePlayerImage(ev.SenderId, ev.SenderImage),
        message     = ev.Message,
    };

    private object BuildFriendTimelinePayload(TimelineService.FriendTimelineEvent ev) => new
    {
        id          = ev.Id,
        type        = ev.Type,
        timestamp   = ev.Timestamp,
        friendId    = ev.FriendId,
        friendName  = ev.FriendName,
        friendImage = ResolvePlayerImage(ev.FriendId, ev.FriendImage),
        worldId     = ev.WorldId,
        worldName   = ev.WorldName,
        worldThumb  = ev.WorldThumb,
        location    = ev.Location,
        oldValue    = ev.OldValue,
        newValue    = ev.NewValue,
    };

    /// <summary>
    /// Returns the best available profile image for a user: prefers the live friend/player
    /// image caches (which use the corrected GetUserImage chain) over anything stored in the DB.
    /// </summary>
    private string ResolvePlayerImage(string? userId, string? storedImage)
    {
        if (!string.IsNullOrEmpty(userId))
        {
            if (_friendNameImg.TryGetValue(userId, out var fi) && !string.IsNullOrEmpty(fi.image))
                return fi.image;
            if (_playerImageCache.TryGetValue(userId, out var pi) && !string.IsNullOrEmpty(pi.image))
                return pi.image;
        }
        return storedImage ?? "";
    }

    /// <summary>Converts a local file path to a virtual-host URL usable in WebView2.</summary>
    private string GetVirtualMediaUrl(string filePath)
    {
        // Check watch-folder virtual hosts first
        for (int i = 0; i < _settings.WatchFolders.Count; i++)
        {
            var folder = _settings.WatchFolders[i];
            if (!Directory.Exists(folder)) continue;
            if (filePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            {
                var rel = filePath.Substring(folder.Length).TrimStart('\\', '/').Replace('\\', '/');
                return $"http://localmedia{i}.vrcnext.local/{rel}";
            }
        }
        // Fallback: VRChat screenshot virtual host
        var vrcPhotoDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VRChat");
        if (filePath.StartsWith(vrcPhotoDir, StringComparison.OrdinalIgnoreCase))
        {
            var rel = filePath.Substring(vrcPhotoDir.Length).TrimStart('\\', '/').Replace('\\', '/');
            return $"http://vrcphotos.vrcnext.local/{rel}";
        }
        return "";
    }

    // Timeline - photo bootstrap (import existing photos)

    /// <summary>
    /// Imports existing photo_players.json entries into the timeline (one-time bootstrap).
    /// Only creates events for photos not already tracked.
    /// </summary>
    private async Task BootstrapPhotoTimeline()
    {
        try
        {
            // Build set of filenames already in timeline
            var existingFiles = new HashSet<string>(
                _timeline.GetEvents()
                    .Where(e => e.Type == "photo" && !string.IsNullOrEmpty(e.PhotoPath))
                    .Select(e => Path.GetFileName(e.PhotoPath)),
                StringComparer.OrdinalIgnoreCase);

            if (_photoPlayersStore.Photos.Count == 0) return;

            // Build list of search roots (VRChat photo dir + watch folders)
            var searchRoots = new List<string>();
            var vrcPhotoDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VRChat");
            if (Directory.Exists(vrcPhotoDir)) searchRoots.Add(vrcPhotoDir);
            foreach (var folder in _settings.WatchFolders.Where(Directory.Exists))
            {
                if (!searchRoots.Any(r => r.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                    searchRoots.Add(folder);
            }

            int added = 0;
            foreach (var (fileName, rec) in _photoPlayersStore.Photos)
            {
                if (existingFiles.Contains(fileName)) continue;

                // Find the actual file on disk
                string? filePath = null;
                foreach (var root in searchRoots)
                {
                    try
                    {
                        var found = Directory.GetFiles(root, fileName, SearchOption.AllDirectories)
                                             .FirstOrDefault();
                        if (found != null) { filePath = found; break; }
                    }
                    catch { }
                }
                if (filePath == null) continue;

                var photoUrl = GetVirtualMediaUrl(filePath);
                if (string.IsNullOrEmpty(photoUrl)) continue;

                // Parse timestamp from VRChat filename (VRChat_YYYY-MM-DD_HH-mm-ss.fff_...)
                DateTime ts;
                try
                {
                    var m = System.Text.RegularExpressions.Regex.Match(fileName,
                        @"VRChat_(\d{4}-\d{2}-\d{2})_(\d{2}-\d{2}-\d{2})");
                    ts = m.Success
                        ? DateTime.ParseExact($"{m.Groups[1].Value} {m.Groups[2].Value}",
                            "yyyy-MM-dd HH-mm-ss",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeLocal).ToUniversalTime()
                        : new FileInfo(filePath).LastWriteTimeUtc;
                }
                catch { ts = new FileInfo(filePath).LastWriteTimeUtc; }

                var ev = new TimelineService.TimelineEvent
                {
                    Type      = "photo",
                    Timestamp = ts.ToString("o"),
                    WorldId   = rec.WorldId,
                    PhotoPath = filePath,
                    PhotoUrl  = photoUrl,
                    Players   = rec.Players.Select(p => new TimelineService.PlayerSnap
                    {
                        UserId      = p.UserId,
                        DisplayName = p.DisplayName,
                        Image       = ResolvePlayerImage(p.UserId, p.Image)
                    }).ToList()
                };
                _timeline.AddEvent(ev);
                existingFiles.Add(fileName);
                added++;
            }

            if (added > 0)
                Invoke(() => SendToJS("log", new { msg = $"[TIMELINE] Imported {added} existing photo(s)", color = "sec" }));
        }
        catch (Exception ex)
        {
            try { Invoke(() => SendToJS("log", new { msg = $"[TIMELINE] Bootstrap error: {ex.Message}", color = "err" })); } catch { }
        }
    }

    // File Watcher - Post to Discord
    private async void OnNewFile(object? sender, FileWatcherService.FileArg e)
    {
        if (InvokeRequired) { Invoke(() => OnNewFile(sender, e)); return; }

        // Snapshot players for VRChat screenshots
        SnapshotPhotoPlayers(e.FilePath);

        await PostFile(e.FilePath, false, e.SizeMB);
    }

    /// <summary>
    /// Start a dedicated watcher for VRChat photo folder, independent of relay.
    /// </summary>
    private void StartVrcPhotoWatcher()
    {
        if (_vrcPhotoWatcher != null) return; // already running

        var vrcPhotoDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "VRChat");
        if (!Directory.Exists(vrcPhotoDir))
        {
            try { Directory.CreateDirectory(vrcPhotoDir); }
            catch { return; }
        }

        // Register virtual host so WebView2 can load VRChat screenshots
        try
        {
            _webView.CoreWebView2?.SetVirtualHostNameToFolderMapping(
                "vrcphotos.vrcnext.local", vrcPhotoDir, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
        }
        catch { }

        _vrcPhotoWatcher = new FileSystemWatcher(vrcPhotoDir)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
            Filter = "VRChat_*.png"
        };
        _vrcPhotoWatcher.Created += (s, e) =>
        {
            // Small delay so file is fully written
            Task.Run(async () =>
            {
                await Task.Delay(2000);
                try { Invoke(() => SnapshotPhotoPlayers(e.FullPath)); }
                catch { }
            });
        };

        // Also snapshot any VRChat photos from watch folders
        foreach (var folder in _settings.WatchFolders.Where(Directory.Exists))
        {
            if (folder.Equals(vrcPhotoDir, StringComparison.OrdinalIgnoreCase)) continue;
            // The relay FileWatcher already handles these via OnNewFile
        }
    }

    private static readonly HashSet<string> _imgExts =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

    /// <summary>
    /// Snapshot current instance + players for any image file from any watched folder.
    /// </summary>
    private void SnapshotPhotoPlayers(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (!_imgExts.Contains(Path.GetExtension(filePath)))
            return;
        if (_photoPlayersStore.GetPhotoRecord(fileName) != null) return; // already recorded

        try
        {
            var logPlayers = _logWatcher.GetCurrentPlayers();
            var wid = _logWatcher.CurrentWorldId ?? "";

            var players = new List<(string userId, string displayName, string image)>();

            foreach (var p in logPlayers)
            {
                var img = "";
                if (!string.IsNullOrEmpty(p.UserId))
                {
                    // Try player image cache first (from instance info fetches)
                    if (_playerImageCache.TryGetValue(p.UserId, out var cached))
                        img = cached.image;
                }
                players.Add((p.UserId, p.DisplayName, img));
            }

            // Don't create an empty record; it would poison the cache and prevent
            // re-snapshot on subsequent library loads when VRChat data becomes available.
            if (string.IsNullOrEmpty(wid) && players.Count == 0) return;

            _photoPlayersStore.RecordPhoto(fileName, players, wid);
            _photoPlayersStore.Save();

            // Async: fetch missing images and update record
            _ = Task.Run(async () =>
            {
                var needFetch = players.Where(p => string.IsNullOrEmpty(p.image) && !string.IsNullOrEmpty(p.userId)).ToList();
                if (needFetch.Count == 0) return;

                var semaphore = new SemaphoreSlim(5);
                var updated = false;
                var tasks = needFetch.Select(async p =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var profile = await _vrcApi.GetUserAsync(p.userId);
                        if (profile != null)
                        {
                            var img = VRChatApiService.GetUserImage(profile);
                            lock (_playerImageCache)
                                _playerImageCache[p.userId] = (img, DateTime.Now);

                            var rec = _photoPlayersStore.GetPhotoRecord(fileName);
                            if (rec != null)
                            {
                                var pi = rec.Players.FirstOrDefault(x => x.UserId == p.userId);
                                if (pi != null) { pi.Image = img; updated = true; }
                            }
                        }
                    }
                    finally { semaphore.Release(); }
                });
                await Task.WhenAll(tasks);
                if (updated) _photoPlayersStore.Save();
            });

            SendToJS("log", new { msg = $"📸 Captured {players.Count} players for {fileName}", color = "sec" });

            // Timeline: log photo event
            var photoUrl = GetVirtualMediaUrl(filePath);
            var photoEv = new TimelineService.TimelineEvent
            {
                Type      = "photo",
                Timestamp = DateTime.UtcNow.ToString("o"),
                WorldId   = wid,
                PhotoPath = filePath,
                PhotoUrl  = photoUrl,
                Players   = players.Select(p => new TimelineService.PlayerSnap
                {
                    UserId      = p.userId,
                    DisplayName = p.displayName,
                    Image       = p.image
                }).ToList()
            };
            _timeline.AddEvent(photoEv);
            SendToJS("timelineEvent", BuildTimelinePayload(photoEv));
        }
        catch { }
    }

    private async Task PostFile(string filePath, bool manual, double sizeMB = 0)
    {
        var fileName = Path.GetFileName(filePath);
        if (sizeMB == 0)
        {
            try { sizeMB = new FileInfo(filePath).Length / 1048576.0; } catch { return; }
        }

        var typeStr = FileWatcherService.ImgExt.Contains(Path.GetExtension(filePath)) ? "image" : "video";
        var prefix = manual ? "Manual post" : "New file";
        SendToJS("log", new { msg = $"{prefix}: {fileName} ({sizeMB:F1} MB)", color = "default" });

        var whs = _settings.Webhooks.Where(w => w.Enabled && !string.IsNullOrWhiteSpace(w.Url)).ToList();
        if (!_settings.PostAll && _settings.SelectedChannel < whs.Count)
            whs = new() { whs[_settings.SelectedChannel] };

        foreach (var wh in whs)
        {
            var result = await _webhook.PostFileAsync(wh.Url, filePath, _settings.BotName, _settings.BotAvatarUrl);
            if (result.Success)
            {
                SendToJS("log", new { msg = $"  Posted to '{wh.Name}'", color = "ok" });
                _fileCount++;
                _totalSizeMB += sizeMB;

                var record = new WebhookService.PostRecord
                {
                    MessageId = result.MessageId ?? "",
                    WebhookUrl = wh.Url,
                    WebhookName = wh.Name,
                    FileName = fileName,
                    SizeMB = sizeMB,
                };
                _postHistory.Add(record);

                SendToJS("stats", new { files = _fileCount, size = $"{_totalSizeMB:F1} MB" });
                SendToJS("filePosted", new
                {
                    name = fileName,
                    channel = wh.Name,
                    size = $"{sizeMB:F1} MB",
                    time = record.PostedAt.ToString("HH:mm:ss"),
                    messageId = record.MessageId,
                    webhookUrl = wh.Url,
                });
            }
            else
            {
                SendToJS("log", new { msg = $"  Error '{wh.Name}': {result.Error}", color = "err" });
            }
        }
    }

    // Media Library - scan watch folders for media files
    private void ScanLibraryFolders()
    {
        UpdateVirtualHostMappings();

        Task.Run(() =>
        {
            try
            {
                var allExts = FileWatcherService.ImgExt.Concat(FileWatcherService.VidExt).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var entries = new List<LibFileEntry>();

                for (int fi = 0; fi < _settings.WatchFolders.Count; fi++)
                {
                    var folder = _settings.WatchFolders[fi];
                    if (!Directory.Exists(folder)) continue;
                    var host = $"localmedia{fi}.vrcnext.local";
                    try
                    {
                        new DirectoryInfo(folder)
                            .EnumerateFiles("*.*", SearchOption.AllDirectories)
                            .Where(f => allExts.Contains(f.Extension))
                            .ToList()
                            .ForEach(f => entries.Add(new LibFileEntry(f, host, folder)));
                    }
                    catch { }
                }

                // Sort all by newest first, store cache
                var sorted = entries.OrderByDescending(e => e.Fi.LastWriteTime).ToList();
                _libFileCache = sorted;
                _libFileCacheTotal = sorted.Count;

                var firstPage = BuildLibraryItems(0, 100);
                Invoke(() => SendToJS("libraryData", new
                {
                    files = firstPage,
                    total = _libFileCacheTotal,
                    offset = 0,
                    hasMore = _libFileCacheTotal > 100,
                }));
            }
            catch (Exception ex)
            {
                Invoke(() => SendToJS("log", new { msg = $"Library scan error: {ex.Message}", color = "err" }));
            }
        });
    }

    private List<object> BuildLibraryItems(int offset, int count)
    {
        var result = new List<object>();
        var slice = _libFileCache.Skip(offset).Take(count);
        foreach (var e in slice)
        {
            var f = e.Fi;
            var isImg = FileWatcherService.ImgExt.Contains(f.Extension);
            var sizeMB = f.Length / 1048576.0;
            var relPath = Path.GetRelativePath(e.Folder, f.FullName).Replace('\\', '/');
            var virtualUrl = $"https://{e.Host}/{Uri.EscapeDataString(relPath).Replace("%2F", "/")}";

            string? photoWorldId = null;
            List<object>? photoPlayers = null;
            if (isImg)
            {
                if (f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                    try { photoWorldId = WorldTimeTracker.ExtractWorldIdFromPng(f.FullName); } catch { }

                var rec = _photoPlayersStore.GetPhotoRecord(f.Name);
                if (rec == null && (DateTime.Now - f.LastWriteTime).TotalHours < 24)
                {
                    try { Invoke(() => SnapshotPhotoPlayers(f.FullName)); rec = _photoPlayersStore.GetPhotoRecord(f.Name); }
                    catch { }
                }
                if (rec != null)
                {
                    if (string.IsNullOrEmpty(photoWorldId) && !string.IsNullOrEmpty(rec.WorldId))
                        photoWorldId = rec.WorldId;
                    photoPlayers = rec.Players.Select(p => (object)new { userId = p.UserId, displayName = p.DisplayName, image = ResolvePlayerImage(p.UserId, p.Image) }).ToList();
                }
            }

            result.Add(new
            {
                name = f.Name,
                path = f.FullName,
                folder = e.Folder,
                type = isImg ? "image" : "video",
                size = sizeMB < 1 ? $"{f.Length / 1024.0:F0} KB" : $"{sizeMB:F1} MB",
                modified = f.LastWriteTime.ToString("o"),
                time = f.LastWriteTime.ToString("HH:mm"),
                url = virtualUrl,
                worldId = photoWorldId ?? "",
                players = photoPlayers ?? new List<object>(),
            });
        }
        return result;
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
            var root = drive.RootDirectory.FullName; // e.g. "C:\"
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
                // Match "path" entries like:  "path"		"D:\\SteamLibrary"
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try { _vcProcess?.Kill(entireProcessTree: true); } catch { }
        _wsService?.Dispose();
        _wsFallbackTimer?.Dispose();
        _fileWatcher.Dispose();
        _uptimeTimer.Dispose();
        _steamVR?.Dispose();
        _osc?.Dispose();
        _timeline.Dispose();
        _photoPlayersStore.Dispose();
        _timeTracker.Dispose();
        _worldTimeTracker.Dispose();
        base.OnFormClosing(e);
    }

    // Helper: always returns ISO 8601 date string from a JToken
    private static string IsoDate(JToken? t)
    {
        if (t == null) return "";
        if (t.Type == JTokenType.Date)
            return t.Value<DateTime>().ToUniversalTime().ToString("o");
        return t.ToString();
    }

}

// Intercepts WebView2 mouse messages so edge-drag resize works on a borderless form
