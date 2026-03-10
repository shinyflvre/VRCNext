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
        _thumbCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VRCNext", "ThumbCache");
        Directory.CreateDirectory(_thumbCacheDir);
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
        _discordPresence?.Dispose();
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
        var path    = ctx.Request.Url?.AbsolutePath ?? "/";
        var isThumb = ctx.Request.Url?.Query?.Contains("thumb=1") == true;
        try
        {
            if (path.StartsWith("/imgcache/"))
                ServeFile(ctx, Path.Combine(_imgCacheDir, Uri.UnescapeDataString(path["/imgcache/".Length..])));
            else if (path.StartsWith("/vrcphotos/"))
            {
                if (!string.IsNullOrEmpty(_vrcPhotoDir))
                {
                    var file = Path.Combine(_vrcPhotoDir, Uri.UnescapeDataString(path["/vrcphotos/".Length..]));
                    if (isThumb) ServeThumb(ctx, file); else ServeFile(ctx, file);
                }
                else ctx.Response.StatusCode = 404;
            }
            else if (path.StartsWith("/media"))
            {
                var rest  = path["/media".Length..];
                var slash = rest.IndexOf('/');
                if (slash > 0 && int.TryParse(rest[..slash], out var idx)
                    && idx < _settings.WatchFolders.Count)
                {
                    var file = Path.Combine(_settings.WatchFolders[idx], Uri.UnescapeDataString(rest[(slash + 1)..]));
                    if (isThumb) ServeThumb(ctx, file); else ServeFile(ctx, file);
                }
                else ctx.Response.StatusCode = 404;
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
        // Stream directly — NEVER load full file into RAM (videos can be multiple gigabytes)
        ctx.Response.ContentLength64 = new FileInfo(file).Length;
        using var fs = File.OpenRead(file);
        fs.CopyTo(ctx.Response.OutputStream);
    }

    private static readonly SemaphoreSlim _thumbSem = new(2, 2);
    private static int _thumbGenCount = 0;

    private void ServeThumb(System.Net.HttpListenerContext ctx, string file)
    {
        if (!File.Exists(file)) { ctx.Response.StatusCode = 404; return; }
        var ext = Path.GetExtension(file).ToLower();
        // Only thumbnail images; serve other types (video etc.) as-is
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp"))
        {
            ServeFile(ctx, file);
            return;
        }

        // Cache key: MD5 of path, invalidate if source is newer
        var keyBytes  = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(file));
        var thumbPath = Path.Combine(_thumbCacheDir, Convert.ToHexString(keyBytes) + ".jpg");

        if (!File.Exists(thumbPath) || File.GetLastWriteTimeUtc(thumbPath) < File.GetLastWriteTimeUtc(file))
        {
            _thumbSem.Wait();
            try
            {
                // Double-check after acquiring semaphore (another thread may have generated it)
                if (!File.Exists(thumbPath) || File.GetLastWriteTimeUtc(thumbPath) < File.GetLastWriteTimeUtc(file))
                {
                    var tmpPath = thumbPath + ".tmp";
                    // FromStream instead of FromFile — releases file handle immediately after read
                    var rawBytes = File.ReadAllBytes(file);
                    using var ms  = new MemoryStream(rawBytes);
                    using var src = System.Drawing.Image.FromStream(ms, false, false);
                    const int maxSize = 400;
                    var scale = Math.Min(1.0, Math.Min(maxSize / (double)src.Width, maxSize / (double)src.Height));
                    var w = Math.Max(1, (int)(src.Width  * scale));
                    var h = Math.Max(1, (int)(src.Height * scale));
                    using var bmp = new System.Drawing.Bitmap(w, h);
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                        g.DrawImage(src, 0, 0, w, h);
                    }
                    var jpegCodec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                        .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                    var encParams = new System.Drawing.Imaging.EncoderParameters(1);
                    encParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                        System.Drawing.Imaging.Encoder.Quality, 72L);
                    // Atomic write: temp file then rename to prevent serving half-written files
                    bmp.Save(tmpPath, jpegCodec, encParams);
                    File.Move(tmpPath, thumbPath, overwrite: true);

                    // Periodic GC to prompt libgdiplus to release native memory
                    if (Interlocked.Increment(ref _thumbGenCount) % 10 == 0)
                        GC.Collect(1, GCCollectionMode.Optimized, false);
                }
            }
            finally { _thumbSem.Release(); }
        }

        ctx.Response.ContentType = "image/jpeg";
        ctx.Response.StatusCode  = 200;
        var thumbBytes = File.ReadAllBytes(thumbPath);
        ctx.Response.ContentLength64 = thumbBytes.Length;
        ctx.Response.OutputStream.Write(thumbBytes);
    }
}
