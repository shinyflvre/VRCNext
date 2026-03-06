using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Disk-based image cache for VRChat images (avatars, worlds, groups).
/// Cached images are served via a WebView2 virtual host for instant local access.
/// Cache TTL: 7 days. Background download on cache miss; returns original URL while downloading.
/// </summary>
public class ImageCacheService
{
    private readonly string _dir;
    private readonly HttpClient _http;
    private readonly HashSet<string> _inFlight = new();
    private readonly HashSet<string> _permanentFail = new();
    private static readonly TimeSpan TTL = TimeSpan.FromDays(7);

    // Amount freed per trim pass (~2 GB)
    private const long TrimPassBytes = 2L * 1024 * 1024 * 1024;

    public const string VirtualHost = "imgcache.vrcnext.local";

    /// <summary>When false, Get() always returns the original URL and no files are written.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Maximum cache size in bytes. 0 = unlimited. Trim runs after each download.</summary>
    public long LimitBytes { get; set; } = 5L * 1024 * 1024 * 1024;

    public ImageCacheService(string cacheDir, HttpClient http)
    {
        _dir = cacheDir;
        Directory.CreateDirectory(_dir);
        _http = http;
    }

    /// <summary>
    /// Returns a local virtual-host URL if the image is already cached,
    /// otherwise returns the original URL and starts a background download.
    /// When caching is disabled, always returns the original URL.
    /// </summary>
    public string Get(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http")) return url ?? "";
        if (!Enabled) return url;

        var fileName = GetFileName(url);
        var filePath = Path.Combine(_dir, fileName);

        if (File.Exists(filePath) && DateTime.UtcNow - File.GetCreationTimeUtc(filePath) < TTL)
            return $"https://{VirtualHost}/{fileName}";

        // Skip URLs that permanently failed (403, 404, etc.)
        lock (_permanentFail)
            if (_permanentFail.Contains(url)) return url;

        // Start background download; caller gets original URL this time
        _ = DownloadAsync(url, filePath);
        return url;
    }

    /// <summary>
    /// Returns the current total size of the cache directory in bytes.
    /// </summary>
    public long GetCacheSizeBytes()
    {
        if (!Directory.Exists(_dir)) return 0;
        return new DirectoryInfo(_dir)
            .GetFiles("*", SearchOption.TopDirectoryOnly)
            .Where(f => !f.Name.EndsWith(".tmp"))
            .Sum(f => f.Length);
    }

    /// <summary>
    /// Deletes the oldest cached files until at least <see cref="TrimPassBytes"/> have been freed,
    /// or until the total size is below <paramref name="limitBytes"/>.
    /// Called automatically after each successful download when a limit is set.
    /// </summary>
    public void TrimIfNeeded(long limitBytes)
    {
        if (limitBytes <= 0 || !Directory.Exists(_dir)) return;
        try
        {
            var files = new DirectoryInfo(_dir)
                .GetFiles("*", SearchOption.TopDirectoryOnly)
                .Where(f => !f.Name.EndsWith(".tmp"))
                .OrderBy(f => f.LastWriteTimeUtc)   // oldest first
                .ToList();

            var total = files.Sum(f => f.Length);
            if (total <= limitBytes) return;

            // Free TrimPassBytes worth of old files to create breathing room
            long freed = 0;
            foreach (var f in files)
            {
                if (freed >= TrimPassBytes) break;
                try { freed += f.Length; f.Delete(); } catch { }
            }
        }
        catch { }
    }

    /// <summary>Deletes all cached image files.</summary>
    public void ClearAll()
    {
        if (!Directory.Exists(_dir)) return;
        try
        {
            foreach (var f in new DirectoryInfo(_dir).GetFiles("*", SearchOption.TopDirectoryOnly))
                try { f.Delete(); } catch { }
        }
        catch { }
    }

    private async Task DownloadAsync(string url, string filePath)
    {
        lock (_inFlight)
        {
            if (_inFlight.Contains(url)) return;
            _inFlight.Add(url);
        }
        try
        {
            var tmp = filePath + ".tmp";
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead);
            if (!resp.IsSuccessStatusCode)
            {
                var status = (int)resp.StatusCode;
                // 403/404 won't be fixed by retrying — permanently blacklist
                if (status == 403 || status == 404)
                    lock (_permanentFail) _permanentFail.Add(url);
                return;
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, filePath, overwrite: true);

            // Enforce cache limit in background after each write
            if (LimitBytes > 0)
                _ = Task.Run(() => TrimIfNeeded(LimitBytes));
        }
        catch { }
        finally
        {
            lock (_inFlight) _inFlight.Remove(url);
        }
    }

    private static string GetFileName(string url)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(url));
        var ext  = Path.GetExtension(url.Split('?')[0]);
        if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".jpg";
        return Convert.ToHexString(hash).ToLower() + ext;
    }
}
