#if !WINDOWS
using System.Diagnostics;
using System.Security.Cryptography;
using SkiaSharp;

namespace VRCNext.Services.Helpers;

public static class LinuxDesktopHelper
{
    public static bool IsKde => Desktop.Contains("kde", StringComparison.Ordinal);

    private static bool IsWayland =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    private static string Desktop =>
        (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").ToLowerInvariant();

    public static bool CopyImageToClipboard(string path, out string? error)
    {
        error = null;
        var mime = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif"            => "image/gif",
            ".webp"           => "image/webp",
            ".bmp"            => "image/bmp",
            _                 => "image/png",
        };

        if (IsWayland && TryPipeFile("wl-copy", new[] { "-t", mime }, path, out error))
            return true;
        if (TryRun("xclip", new[] { "-selection", "clipboard", "-t", mime, "-i", path }, out error))
            return true;
        error ??= "No clipboard tool found. Install wl-clipboard (Wayland) or xclip (X11).";
        return false;
    }

    public static bool SetWallpaper(string path, out string? error)
    {
        error = null;
        var uri = new Uri(path).AbsoluteUri;
        var d = Desktop;

        if (d.Contains("kde"))
        {
            if (TryRun("plasma-apply-wallpaperimage", new[] { path }, out error))
                return true;
            var script =
                "var allDesktops = desktops();" +
                "for (i = 0; i < allDesktops.length; i++) {" +
                "  d = allDesktops[i];" +
                "  d.wallpaperPlugin = 'org.kde.image';" +
                "  d.currentConfigGroup = Array('Wallpaper', 'org.kde.image', 'General');" +
                "  d.writeConfig('Image', '" + uri.Replace("'", "%27") + "');" +
                "}";
            foreach (var qdbus in new[] { "qdbus6", "qdbus", "qdbus-qt5" })
                if (TryRun(qdbus, new[] { "org.kde.plasmashell", "/PlasmaShell", "org.kde.PlasmaShell.evaluateScript", script }, out error))
                    return true;
        }

        if (d.Contains("gnome") || d.Contains("unity") || d.Contains("budgie") || d.Contains("pantheon"))
        {
            var ok = TryRun("gsettings", new[] { "set", "org.gnome.desktop.background", "picture-uri", uri }, out error);
            TryRun("gsettings", new[] { "set", "org.gnome.desktop.background", "picture-uri-dark", uri }, out _);
            if (ok) return true;
        }

        if (d.Contains("cinnamon") &&
            TryRun("gsettings", new[] { "set", "org.cinnamon.desktop.background", "picture-uri", uri }, out error))
            return true;

        if (d.Contains("mate") &&
            TryRun("gsettings", new[] { "set", "org.mate.background", "picture-filename", path }, out error))
            return true;

        if (d.Contains("xfce"))
        {
            var props = RunCapture("xfconf-query", new[] { "-c", "xfce4-desktop", "-l" });
            bool any = false;
            foreach (var prop in (props ?? "").Split('\n'))
            {
                var p = prop.Trim();
                if (p.EndsWith("last-image") || p.EndsWith("image-path"))
                    if (TryRun("xfconf-query", new[] { "-c", "xfce4-desktop", "-p", p, "-s", path }, out _))
                        any = true;
            }
            if (any) return true;
        }

        if (TryRun("gsettings", new[] { "set", "org.gnome.desktop.background", "picture-uri", uri }, out error))
        {
            TryRun("gsettings", new[] { "set", "org.gnome.desktop.background", "picture-uri-dark", uri }, out _);
            return true;
        }

        error ??= $"Could not set wallpaper on this desktop environment ({Desktop}).";
        return false;
    }

    public static bool RevealInFileManager(string path)
    {
        var uri = new Uri(path).AbsoluteUri;
        if (TryRun("dbus-send", new[]
            {
                "--session", "--print-reply",
                "--dest=org.freedesktop.FileManager1",
                "/org/freedesktop/FileManager1",
                "org.freedesktop.FileManager1.ShowItems",
                $"array:string:{uri}", "string:",
            }, out _))
            return true;

        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return false;
        return TryRun("xdg-open", new[] { dir }, out _, waitForExit: false);
    }

    public static bool PlayNotificationSound(string path, double volume, Action<string> log, string? eventId = null, bool preview = false)
    {
        var canberraPlayer = FindExecutable("canberra-gtk-play");
        var player = canberraPlayer ?? FindExecutable("gst-play-1.0");
        if (player == null)
        {
            log("[Sound] No Linux sound player found. Install libcanberra-gtk3 or GStreamer tools to enable notification sounds.");
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo(player)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            if (canberraPlayer != null)
            {
                var normalizedVolume = Math.Clamp(volume, 0, 1);
                var volumeDb = normalizedVolume == 0 ? -100 : 20 * Math.Log10(normalizedVolume);
                startInfo.ArgumentList.Add("--name=VRCNext");
                startInfo.ArgumentList.Add($"--volume={volumeDb.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
                startInfo.ArgumentList.Add($"--file={path}");
            }
            else
            {
                startInfo.ArgumentList.Add("--quiet");
                startInfo.ArgumentList.Add("--no-interactive");
                startInfo.ArgumentList.Add($"--volume={Math.Clamp(volume, 0, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                startInfo.ArgumentList.Add(path);
            }

            var process = Process.Start(startInfo);
            if (process == null)
            {
                log($"[Sound] Could not start {Path.GetFileName(player)}.");
                return false;
            }

            _ = ObserveSoundPlaybackAsync(process, log);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            log($"[Sound] Could not start notification sound: {ex.Message}");
            return false;
        }
    }

    private static async Task ObserveSoundPlaybackAsync(Process process, Action<string> log)
    {
        using (process)
        {
            try
            {
                var errorTask = process.StandardError.ReadToEndAsync();
                var outputTask = process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                var error = (await errorTask).Trim();
                var output = (await outputTask).Trim();
                if (process.ExitCode != 0)
                    log($"[Sound] {Path.GetFileName(process.StartInfo.FileName)} playback failed: {(error.Length > 0 ? error : output)}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                log($"[Sound] Error while playing notification sound: {ex.Message}");
            }
        }
    }

    private static string? FindExecutable(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static bool TryRun(string file, string[] args, out string? error, bool waitForExit = true, int timeoutMs = 15000)
    {
        error = null;
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardError = waitForExit,
                RedirectStandardOutput = waitForExit,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc == null) { error = $"{file} could not be started"; return false; }
            if (!waitForExit) return true;
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                error = $"{file} timed out";
                return false;
            }
            var err = errorTask.GetAwaiter().GetResult().Trim();
            _ = outputTask.GetAwaiter().GetResult();
            if (proc.ExitCode != 0)
            {
                error = err.Length > 0 ? err : $"{file} exited with code {proc.ExitCode}";
                return false;
            }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private static string? RunCapture(string file, string[] args, int timeoutMs = 10000)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            _ = errorTask.GetAwaiter().GetResult();
            var output = outputTask.GetAwaiter().GetResult();
            return proc.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }

    private static bool TryPipeFile(string file, string[] args, string inputPath, out string? error)
    {
        try
        {
            using var fs = File.OpenRead(inputPath);
            return TryPipeStream(file, args, fs, out error);
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private static bool TryPipeStream(string file, string[] args, Stream input, out string? error)
    {
        error = null;
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc == null) { error = $"{file} could not be started"; return false; }
            var errorTask = proc.StandardError.ReadToEndAsync();
            input.CopyTo(proc.StandardInput.BaseStream);
            proc.StandardInput.Close();
            if (!proc.WaitForExit(15000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                error = $"{file} timed out";
                return false;
            }
            var err = errorTask.GetAwaiter().GetResult().Trim();
            if (proc.ExitCode != 0)
            {
                error = err.Length > 0 ? err : $"{file} exited with code {proc.ExitCode}";
                return false;
            }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
}

public static class LinuxNotificationHelper
{
    public const string VrchatNotificationEvent = "vrchat-notification";
    public const string FriendOnlineEvent = "friend-online";
    public const string ActionFlowEvent = "action-flow";
    public const string NotificationSoundEvent = "notification-sound";
    public const string FriendOnlineSoundEvent = "friend-online-sound";
    public const string MessageSoundEvent = "message-sound";
    public const string MediaRelaySoundEvent = "media-relay-sound";

    private static readonly HashSet<string> KnownEventIds = new(StringComparer.Ordinal)
    {
        VrchatNotificationEvent,
        FriendOnlineEvent,
        ActionFlowEvent,
        NotificationSoundEvent,
        FriendOnlineSoundEvent,
        MessageSoundEvent,
        MediaRelaySoundEvent,
        "notification-friend-request-sound",
        "notification-invite-sound",
        "notification-invite-request-sound",
        "notification-invite-response-sound",
        "notification-invite-request-response-sound",
        "notification-vote-to-kick-sound",
        "notification-group-invite-sound",
        "notification-group-join-request-sound",
        "notification-group-announcement-sound",
        "notification-instance-announcement-sound",
        "friend-request",
        "invite",
        "invite-request",
        "invite-response",
        "invite-request-response",
        "vote-to-kick",
        "group-invite",
        "group-join-request",
        "group-announcement",
        "instance-announcement",
    };

    private const int MaxImageBytes = 5 * 1024 * 1024;
    private const int MaxImageDimension = 256;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> RecentNotifications = new();
    private const int MaxDecodedPixels = 16_000_000;

    public static async Task<bool> TrySendAsync(
        string title,
        string body,
        string? imageUrl,
        HttpClient http,
        Action<string> log,
        string? eventId = null)
    {
        var notifySend = FindExecutable("notify-send");
        if (notifySend == null) return false;

        // Linux forwarding can replay the same frontend event through both the
        // main window and helper window. KDE coalesces by event id, while other
        // desktops need a short local guard as well.
        if (!string.IsNullOrEmpty(eventId))
        {
            var now = DateTime.UtcNow;
            if (RecentNotifications.TryGetValue(eventId, out var previous) && (now - previous).TotalSeconds < 5)
                return true;
            RecentNotifications[eventId] = now;
            foreach (var item in RecentNotifications.Where(x => (now - x.Value).TotalSeconds > 30).ToList())
                RecentNotifications.TryRemove(item.Key, out _);
        }

        string? imagePath = null;
        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            try
            {
                imagePath = await GetImagePathAsync(imageUrl, http);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
            {
                log($"[Notifications] Could not prepare notification image: {ex.Message}");
            }
        }

        try
        {
            var startInfo = new ProcessStartInfo(notifySend) { UseShellExecute = false };
            startInfo.ArgumentList.Add("--app-name=VRCNext");
            startInfo.ArgumentList.Add("--hint=string:desktop-entry:vrcnext");
            startInfo.ArgumentList.Add("--hint=string:x-kde-appname:vrcnext");
            if (eventId != null && KnownEventIds.Contains(eventId))
                startInfo.ArgumentList.Add($"--hint=string:x-kde-eventId:{eventId}");
            if (imagePath != null)
            {
                startInfo.ArgumentList.Add($"--icon={imagePath}");
                var imageUri = new Uri(imagePath).AbsoluteUri;
                startInfo.ArgumentList.Add($"--hint=string:image-path:{imageUri}");
                startInfo.ArgumentList.Add($"--hint=string:image_path:{imageUri}");
            }
            startInfo.ArgumentList.Add(title);
            startInfo.ArgumentList.Add(body);

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                log("[Notifications] Could not start notify-send.");
                return false;
            }

            await process.WaitForExitAsync();
            if (process.ExitCode == 0) return true;

            log($"[Notifications] notify-send exited with code {process.ExitCode}.");
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            log($"[Notifications] Could not send desktop notification: {ex.Message}");
            return false;
        }
    }

    private static async Task<string?> GetImagePathAsync(string imageUrl, HttpClient http)
    {
        var cacheDir = Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CACHE_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
            "VRCNext", "NotificationImages");
        Directory.CreateDirectory(cacheDir);

        var cacheKey = imageUrl;
        if (Uri.TryCreate(imageUrl, UriKind.Absolute, out var fileUri) && fileUri.IsFile)
            imageUrl = fileUri.LocalPath;

        var cachePath = Path.Combine(cacheDir, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cacheKey))) + ".png");
        if (File.Exists(cachePath)) return cachePath;

        byte[] imageBytes;
        if (Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxImageBytes)
                throw new IOException("Image exceeds the 5 MB notification image limit.");
            if (response.Content.Headers.ContentType?.MediaType is { } mediaType
                && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                throw new IOException($"URL returned non-image content ({mediaType}).");

            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            while (true)
            {
                var read = await source.ReadAsync(chunk, timeout.Token);
                if (read == 0) break;
                if (buffer.Length + read > MaxImageBytes)
                    throw new IOException("Image exceeds the 5 MB notification image limit.");
                await buffer.WriteAsync(chunk.AsMemory(0, read), timeout.Token);
            }
            imageBytes = buffer.ToArray();
        }
        else
        {
            if (!File.Exists(imageUrl)) return null;
            var fileInfo = new FileInfo(imageUrl);
            if (fileInfo.Length > MaxImageBytes)
                throw new IOException("Image exceeds the 5 MB notification image limit.");
            imageBytes = await File.ReadAllBytesAsync(imageUrl);
        }

        var pngBytes = CreatePngThumbnail(imageBytes);
        var temporaryPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, pngBytes);
            File.Move(temporaryPath, cachePath, overwrite: true);
            return cachePath;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static byte[] CreatePngThumbnail(byte[] imageBytes)
    {
        using var data = SKData.CreateCopy(imageBytes);
        using var codec = SKCodec.Create(data) ?? throw new IOException("The notification image format is not supported.");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxDecodedPixels)
            throw new IOException("The notification image dimensions are invalid or too large.");

        using var source = SKBitmap.Decode(codec) ?? throw new IOException("The notification image could not be decoded.");
        var scale = Math.Min(1d, (double)MaxImageDimension / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var thumbnail = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        if (!source.ScalePixels(thumbnail, new SKSamplingOptions(SKFilterMode.Linear)))
            throw new IOException("The notification image could not be resized.");

        using var outputImage = SKImage.FromBitmap(thumbnail);
        using var outputData = outputImage.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new IOException("The notification image could not be encoded.");
        return outputData.ToArray();
    }

    private static string? FindExecutable(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
#endif
