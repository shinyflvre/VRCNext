using Photino.NET;
using Newtonsoft.Json;
using System.Diagnostics;
using VRCNext.Services;

namespace VRCNext;

public partial class MainForm
{
    public MainForm(string[] args)
    {
        _settings = AppSettings.Load();
        if (_settings.MemoryTrimEnabled) _memTrim.SetEnabled(true);
        _timeTracker = UserTimeTracker.Load();
        _worldTimeTracker = WorldTimeTracker.Load();
        _photoPlayersStore = PhotoPlayersStore.Load();
        _timeline = TimelineService.Load();
        _minimized = args.Contains("--minimized");
        _fileWatcher.NewFile += OnNewFile;
    }

    public void Run()
    {
        StartHttpListener();

        _imgCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VRCNext", "ImageCache");
        Directory.CreateDirectory(_imgCacheDir);
        _imgCache = new ImageCacheService(_imgCacheDir, _vrcApi.GetHttpClient())
        {
            Enabled    = _settings.ImgCacheEnabled,
            LimitBytes = (long)_settings.ImgCacheLimitGb * 1024 * 1024 * 1024,
            Port       = _httpPort,
        };

        var wwwroot   = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
        var startPage = _settings.SetupComplete
            ? Path.Combine(wwwroot, "index.html")
            : Path.Combine(wwwroot, "setup", "setup.html");
        if (!File.Exists(startPage)) startPage = Path.Combine(wwwroot, "index.html");

        int uptimeTick = 0;
        _uptimeTimer2 = new System.Threading.Timer(_ =>
        {
            if (_voiceFight?.IsRunning == true)
                SendToJS("vfMeter", new { level = _voiceFight.MeterLevel });
            if (Interlocked.Increment(ref uptimeTick) % 10 == 0 && _relayRunning)
                SendToJS("uptimeTick", (DateTime.Now - _relayStart).ToString(@"hh\:mm\:ss"));
        }, null, 100, 100);

        // Chromeless on Windows requires explicit location (Center() sets a flag, not coordinates)
        var (startX, startY) = GetCenteredLocation(1100, 700);

#if !WINDOWS
        // Auto-install missing GStreamer plugins required by WebKit2GTK (blank window without them)
        EnsureLinuxGstreamer();

        // WebKit2GTK on systems without proper GPU (VMs, Hyper-V, missing Vulkan):
        // Force Mesa software rendering so EGL/OpenGL never fails in the WebKit child process.
        // These must be set before PhotinoWindow so the child process inherits them.
        void SetIfUnset(string key, string val)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                Environment.SetEnvironmentVariable(key, val);
        }
        SetIfUnset("WEBKIT_DISABLE_DMABUF_RENDERER",    "1");
        SetIfUnset("WEBKIT_DISABLE_COMPOSITING_MODE",   "1");
        SetIfUnset("LIBGL_ALWAYS_SOFTWARE",             "1");
        SetIfUnset("GALLIUM_DRIVER",                    "llvmpipe");
#endif

        var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "logo.png");
        var windowBuilder = new PhotinoWindow()
            .SetTitle("VRCNext")
            .SetUseOsDefaultSize(false)
            .SetSize(1100, 700)
            .SetMinSize(900, 540)
            .SetChromeless(OperatingSystem.IsWindows())
            .SetResizable(true)
            .SetUseOsDefaultLocation(false)
            .SetLeft(startX)
            .SetTop(startY)
            .RegisterWebMessageReceivedHandler((_, message) => { _ = OnWebMessage(message); });
        if (File.Exists(iconPath)) windowBuilder.SetIconFile(iconPath);
        _window = windowBuilder.Load(startPage);

        if (_minimized) _window.SetMinimized(true);
        _window.WaitForClose();
        OnClose();
    }

#if !WINDOWS
    private static void EnsureLinuxGstreamer()
    {
        // Check if autoaudiosink is available — its absence crashes WebKitWebProcess (blank window)
        try
        {
            var check = Process.Start(new ProcessStartInfo("gst-inspect-1.0")
            {
                Arguments = "autoaudiosink",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            });
            check?.WaitForExit(3000);
            if (check?.ExitCode == 0) return; // already installed
        }
        catch { return; } // gst-inspect-1.0 not on PATH — nothing we can do

        // Determine install command for the running distro
        string pkgs = "";
        if (File.Exists("/usr/bin/pacman") || File.Exists("/bin/pacman"))
            pkgs = "gst-plugins-base gst-plugins-good gst-libav";
        else if (File.Exists("/usr/bin/apt-get"))
            pkgs = "gstreamer1.0-plugins-base gstreamer1.0-plugins-good gstreamer1.0-libav";
        else if (File.Exists("/usr/bin/dnf"))
            pkgs = "gstreamer1-plugins-base gstreamer1-plugins-good";
        if (string.IsNullOrEmpty(pkgs)) return;

        string pm  = File.Exists("/usr/bin/pacman") || File.Exists("/bin/pacman") ? "pacman -S --noconfirm"
                   : File.Exists("/usr/bin/apt-get")                               ? "apt-get install -y"
                                                                                   : "dnf install -y";
        string cmd = $"{pm} {pkgs}";

        // Use pkexec — opens a GUI polkit dialog (no terminal needed)
        try
        {
            var proc = Process.Start(new ProcessStartInfo("pkexec")
            {
                Arguments      = $"/bin/bash -c \"{cmd}\"",
                UseShellExecute = false,
            });
            proc?.WaitForExit();
        }
        catch { }
    }
#endif

    private void OnClose()
    {
        try { _vcProcess?.Kill(entireProcessTree: true); } catch { }
        _wsService?.Dispose();
        _wsFallbackTimer?.Dispose();
        _fileWatcher.Dispose();
        _uptimeTimer2?.Dispose();
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
        _memTrim.Dispose();
        _httpListener?.Stop();
    }

    private void SendToJS(string type, object? payload = null)
    {
        var msg = JsonConvert.SerializeObject(new { type, payload });
        if (_imgCache != null)
            msg = _vrcImgUrlRegex.Replace(msg, m => $"\"{_imgCache.Get(m.Groups[1].Value)}\"");
        try { _window.SendWebMessage(msg); } catch { }
    }

    // ── HttpListener (replaces WebView2 virtual hosts) ────────────────────────

    private readonly List<string> _mappedHosts = new();

    private void StartHttpListener()
    {
        // Try the saved port first, then scan for a free one
        var candidates = new List<int>();
        if (_settings.LocalHttpPort > 0) candidates.Add(_settings.LocalHttpPort);
        var rng = new Random();
        for (int i = 0; i < 200; i++) candidates.Add(rng.Next(49152, 65534));

        foreach (var port in candidates)
        {
            _httpListener = new System.Net.HttpListener();
            _httpListener.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                _httpListener.Start();
                _httpPort = port;
                if (_settings.LocalHttpPort != port)
                {
                    _settings.LocalHttpPort = port;
                    _settings.Save();
                }
                break;
            }
            catch (System.Net.HttpListenerException) { _httpListener.Close(); }
        }
        _ = Task.Run(ServeHttpAsync);
    }

    private void UpdateVirtualHostMappings()
    {
        // No-op with Photino — watch folders served via /media{i}/ routes in HttpListener
    }

    private async Task ServeHttpAsync()
    {
        while (_httpListener?.IsListening == true)
        {
            try
            {
                var ctx = await _httpListener.GetContextAsync();
                _ = Task.Run(() => HandleHttp(ctx));
            }
            catch { break; }
        }
    }

    private void HandleHttp(System.Net.HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        try
        {
            if (path.StartsWith("/imgcache/"))
                ServeFile(ctx, Path.Combine(_imgCacheDir, Uri.UnescapeDataString(path["/imgcache/".Length..])));
            else if (path.StartsWith("/vrcphotos/"))
            {
                if (!string.IsNullOrEmpty(_vrcPhotoDir))
                    ServeFile(ctx, Path.Combine(_vrcPhotoDir, Uri.UnescapeDataString(path["/vrcphotos/".Length..])));
                else
                    ctx.Response.StatusCode = 404;
            }
            else if (path.StartsWith("/media"))
            {
                var rest  = path["/media".Length..];
                var slash = rest.IndexOf('/');
                if (slash > 0 && int.TryParse(rest[..slash], out var idx)
                    && idx < _settings.WatchFolders.Count)
                    ServeFile(ctx, Path.Combine(_settings.WatchFolders[idx], Uri.UnescapeDataString(rest[(slash + 1)..])));
                else
                    ctx.Response.StatusCode = 404;
            }
            else ctx.Response.StatusCode = 404;
        }
        catch { ctx.Response.StatusCode = 500; }
        finally { try { ctx.Response.Close(); } catch { } }
    }

    private static void ServeFile(System.Net.HttpListenerContext ctx, string file)
    {
        if (!File.Exists(file)) { ctx.Response.StatusCode = 404; return; }
        ctx.Response.ContentType = Path.GetExtension(file).ToLower() switch {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png"  => "image/png",
            ".gif"  => "image/gif",
            ".webp" => "image/webp",
            ".mp4"  => "video/mp4",
            ".webm" => "video/webm",
            _       => "application/octet-stream"
        };
        ctx.Response.StatusCode = 200;
        using var fs = File.OpenRead(file);
        fs.CopyTo(ctx.Response.OutputStream);
    }
}
