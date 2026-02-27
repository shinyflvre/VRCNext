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
    private static readonly TimeSpan TTL = TimeSpan.FromDays(7);

    public const string VirtualHost = "imgcache.vrcnext.local";

    public ImageCacheService(string cacheDir, HttpClient http)
    {
        _dir = cacheDir;
        Directory.CreateDirectory(_dir);
        _http = http;
    }

    /// <summary>
    /// Returns a local virtual-host URL if the image is already cached,
    /// otherwise returns the original URL and starts a background download.
    /// </summary>
    public string Get(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http")) return url ?? "";

        var fileName = GetFileName(url);
        var filePath = Path.Combine(_dir, fileName);

        if (File.Exists(filePath) && DateTime.UtcNow - File.GetCreationTimeUtc(filePath) < TTL)
            return $"https://{VirtualHost}/{fileName}";

        // Start background download; caller gets original URL this time
        _ = DownloadAsync(url, filePath);
        return url;
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
            if (!resp.IsSuccessStatusCode) return;
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, filePath, overwrite: true);
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
