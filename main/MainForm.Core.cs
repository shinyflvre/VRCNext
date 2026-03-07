using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using VRCNext.Services;

namespace VRCNext;

public partial class MainForm : Form
{
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
        if (Environment.GetCommandLineArgs().Contains("--minimized"))
            WindowState = FormWindowState.Minimized;
        BackColor = Color.FromArgb(8, 12, 21);
        FormBorderStyle = FormBorderStyle.Sizable;
        DoubleBuffered = true;

        _webView = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_webView);

        _uptimeTimer = new System.Windows.Forms.Timer { Interval = 100 };
        int _uptimeTick = 0;
        _uptimeTimer.Tick += (s, e) =>
        {
            if (_voiceFight?.IsRunning == true)
                SendToJS("vfMeter", new { level = _voiceFight.MeterLevel });
            _uptimeTick++;
            if (_uptimeTick >= 10)
            {
                _uptimeTick = 0;
                if (_relayRunning)
                    SendToJS("uptimeTick", (DateTime.Now - _relayStart).ToString(@"hh\:mm\:ss"));
            }
        };

        _fileWatcher.NewFile += OnNewFile;
        Load += async (s, e) => await InitWebView();
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try { _vcProcess?.Kill(entireProcessTree: true); } catch { }
        _wsService?.Dispose();
        _wsFallbackTimer?.Dispose();
        _fileWatcher.Dispose();
        _uptimeTimer.Dispose();
        _steamVR?.Dispose();
        _voiceFight?.Dispose();
        _osc?.Dispose();
        _timeline.Dispose();
        _photoPlayersStore.Dispose();
        _timeTracker.Dispose();
        _worldTimeTracker.Dispose();
        _vrcPhotoWatcher?.Dispose();
        _friendsRefreshLock.Dispose();
        _webhook.Dispose();
        _logWatcher.Dispose();
        _webView?.Dispose();
        base.OnFormClosing(e);
    }
}
