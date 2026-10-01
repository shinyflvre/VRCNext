using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace VRCNext.Services.Helpers;

// Unified image cache. One entity ID → one file → reused everywhere.
// Directory: %AppData%\VRCNext\Caches\ImageCache\{subdir}\{entityId}.{ext}
public static class ImageCacheHelper
{
    private static string _baseDir = "";
    private static HttpClient? _http;
    private static SqliteConnection? _db;
    private static readonly object _dbLock = new();

    public static int  Port            { get; set; } = 49152;
    public static int  LimitGb         { get; set; } = 5;
    public static bool OptimizeEnabled { get; set; } = true;

    // Toggle ImageCache Debugging
    public static bool DebugMode { get; set; } = false;

    /// <summary>Set at startup to route download logs to the activity log. Args: (message, color).</summary>
    public static Action<string, string>? Log { get; set; }
    public static Action<string, string>? OnImageRefreshed { get; set; }
    private static readonly ConcurrentDictionary<string, Task<string?>> _downloads = new();
    // Session-scoped path memo: "" = checked, not found; non-empty = full path
    private static readonly ConcurrentDictionary<string, string> _pathCache = new();
    // Last downloaded URL per entity — loaded from SQLite on startup
    private static readonly ConcurrentDictionary<string, string> _urls = new();
    private static readonly ConcurrentDictionary<string, string> _authFileIds = new();
    private static readonly ConcurrentDictionary<string, DateTime> _revalidated = new();

    private static readonly string[] _imageExtensions = [".jpg", ".png", ".webp", ".gif"];

    private static readonly SemaphoreSlim _downloadSem = new(6, 6);

    public static void Initialize(HttpClient http)
    {
        _baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VRCNext", "Caches", "ImageCache");
        _http = http;

        Directory.CreateDirectory(Path.Combine(_baseDir, "Worlds"));
        Directory.CreateDirectory(Path.Combine(_baseDir, "Groups"));
        Directory.CreateDirectory(Path.Combine(_baseDir, "Avatars"));
        Directory.CreateDirectory(Path.Combine(_baseDir, "Users"));
        Directory.CreateDirectory(Path.Combine(_baseDir, "Badges"));
        Directory.CreateDirectory(Path.Combine(_baseDir, "Events"));
        Directory.CreateDirectory(Path.Combine(_baseDir, "VRCPlus"));

        _InitDb();
    }

    private static void _InitDb()
    {
        _db = new SqliteConnection($"Data Source={Database.DbPath}");
        _db.Open();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "PRAGMA cache_size=-1024; CREATE TABLE IF NOT EXISTS image_versions (key TEXT PRIMARY KEY, url TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
    }


    private static string? GetStoredUrl(string subdir, string entityId)
    {
        var key = $"{subdir}/{entityId}";
        if (_urls.TryGetValue(key, out var cached)) return cached;
        try
        {
            lock (_dbLock)
            {
                if (_db == null) return null;
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT url FROM image_versions WHERE key = $key";
                cmd.Parameters.AddWithValue("$key", key);
                var result = cmd.ExecuteScalar() as string;
                if (result != null) _urls[key] = result;
                return result;
            }
        }
        catch { return null; }
    }

    private static void SaveUrl(string subdir, string entityId, string url)
    {
        var key = $"{subdir}/{entityId}";
        _urls[key] = url;
        _ = Task.Run(() =>
        {
            lock (_dbLock)
            {
                if (_db == null) return;
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO image_versions (key, url) VALUES ($key, $url)
                    ON CONFLICT(key) DO UPDATE SET url = $url";
                cmd.Parameters.AddWithValue("$key", key);
                cmd.Parameters.AddWithValue("$url", url);
                cmd.ExecuteNonQuery();
            }
        });
    }

    public static string ToLocalUrl(string localPath)
    {
        var rel = Path.GetRelativePath(_baseDir, localPath).Replace('\\', '/');
        long v = 0;
        try { v = File.GetLastWriteTimeUtc(localPath).Ticks; } catch { }
        var url = $"http://localhost:{Port}/imgcache/{rel}?v={v}";
        return DebugMode ? url + "&src=disk" : url;
    }

// World
    public static string? GetWorldCached(string? worldId)
        => FindCachedFile("Worlds", worldId);
    public static Task<string?> CacheWorldAsync(string? worldId, string? imageUrl, bool forceRefresh = false)
        => CacheAsync("Worlds", worldId, imageUrl, forceRefresh);
    public static string? CacheWorldBackground(string? worldId, string? imageUrl)
    {
        var cached = GetWorldCached(worldId);
        if (cached != null) return cached;
        if (!string.IsNullOrWhiteSpace(worldId) && !string.IsNullOrWhiteSpace(imageUrl))
            _ = CacheWorldAsync(worldId, imageUrl);
        return null;
    }

    public static string GetWorldUrl(string? worldId, string? imageUrl)
    {
        imageUrl = StripLocalhostUrl(imageUrl);
        var cached = GetWorldCached(worldId);
        if (cached != null)
        {
            if (!string.IsNullOrWhiteSpace(imageUrl) && !string.IsNullOrWhiteSpace(worldId))
            {
                var normalized = NormalizeTo512(imageUrl);
                var storedUrl  = GetStoredUrl("Worlds", worldId);
                if (storedUrl == normalized) return ToLocalUrl(cached);
                if (!ShouldRefresh($"Worlds/{worldId}", normalized, storedUrl)) return ToLocalUrl(cached);
                _ = CacheAsync("Worlds", worldId, imageUrl, forceRefresh: true);
                return normalized;
            }
            return ToLocalUrl(cached);
        }
        CacheWorldBackground(worldId, imageUrl);
        return RawOrEmpty(imageUrl);
    }

// Groups

    public static string? GetGroupCached(string? groupId)
        => FindCachedFile("Groups", groupId);

    public static Task<string?> CacheGroupAsync(string? groupId, string? iconUrl, bool forceRefresh = false)
        => CacheAsync("Groups", groupId, iconUrl, forceRefresh);

    public static string? CacheGroupBackground(string? groupId, string? iconUrl)
    {
        var cached = GetGroupCached(groupId);
        if (cached != null) return cached;
        if (!string.IsNullOrWhiteSpace(groupId) && !string.IsNullOrWhiteSpace(iconUrl))
            _ = CacheGroupAsync(groupId, iconUrl);
        return null;
    }

    public static string GetGroupUrl(string? groupId, string? iconUrl, bool authoritative = false)
    {
        iconUrl = StripLocalhostUrl(iconUrl);
        if (authoritative && !string.IsNullOrWhiteSpace(groupId))
            RecordAuthoritative($"Groups/{groupId}", iconUrl);
        var cached = GetGroupCached(groupId);
        if (cached != null)
        {
            if (!string.IsNullOrWhiteSpace(iconUrl) && !string.IsNullOrWhiteSpace(groupId))
            {
                var normalized = NormalizeTo512(iconUrl);
                var storedUrl  = GetStoredUrl("Groups", groupId);
                if (storedUrl == normalized)
                {
                    RevalidateInBackground("Groups", groupId, iconUrl, cached);
                    return ToLocalUrl(cached);
                }
                if (!ShouldRefresh($"Groups/{groupId}", normalized, storedUrl, authoritative)) return ToLocalUrl(cached);
                _ = CacheAsync("Groups", groupId, iconUrl, forceRefresh: true);
                return normalized;
            }
            return ToLocalUrl(cached);
        }
        CacheGroupBackground(groupId, iconUrl);
        return RawOrEmpty(iconUrl);
    }

    public static string GetGroupBannerUrl(string? groupId, string? bannerUrl, bool authoritative = false)
    {
        bannerUrl = StripLocalhostUrl(bannerUrl);
        var bannerId = string.IsNullOrWhiteSpace(groupId) ? null : groupId + "_banner";
        if (authoritative && bannerId != null)
            RecordAuthoritative($"Groups/{bannerId}", bannerUrl);
        var cached   = FindCachedFile("Groups", bannerId);
        if (cached != null)
        {
            if (!string.IsNullOrWhiteSpace(bannerUrl) && bannerId != null)
            {
                var normalized = NormalizeTo512(bannerUrl);
                var storedUrl  = GetStoredUrl("Groups", bannerId);
                if (storedUrl == normalized)
                {
                    RevalidateInBackground("Groups", bannerId, bannerUrl, cached);
                    return ToLocalUrl(cached);
                }
                if (!ShouldRefresh($"Groups/{bannerId}", normalized, storedUrl, authoritative)) return ToLocalUrl(cached);
                _ = CacheAsync("Groups", bannerId, bannerUrl, forceRefresh: true);
                return normalized;
            }
            return ToLocalUrl(cached);
        }
        if (!string.IsNullOrWhiteSpace(bannerId) && !string.IsNullOrWhiteSpace(bannerUrl))
            _ = CacheAsync("Groups", bannerId, bannerUrl, false);
        return RawOrEmpty(bannerUrl);
    }

// Users

    public static string? GetUserCached(string? userId)
        => FindCachedFile("Users", userId);

    public static string? GetUserBannerCached(string? userId)
        => FindCachedFile("Users", userId == null ? null : userId + "_banner");

    public static string? GetUserPicOverrideCached(string? userId)
        => FindCachedFile("Users", userId == null ? null : userId + "_pfp");

    public static Task<string?> CacheUserAsync(string? userId, string? iconUrl, bool forceRefresh = false)
        => CacheAsync("Users", userId, iconUrl, forceRefresh);

    public static string GetUserUrl(string? userId, string? iconUrl)
    {
        iconUrl = StripLocalhostUrl(iconUrl);
        var cached = GetUserCached(userId);
        if (cached != null)
        {
            if (!string.IsNullOrWhiteSpace(iconUrl) && !string.IsNullOrWhiteSpace(userId))
            {
                var normalized = NormalizeTo512(iconUrl);
                var storedUrl  = GetStoredUrl("Users", userId);
                if (storedUrl == normalized) return ToLocalUrl(cached);
                if (!ShouldRefresh($"Users/{userId}", normalized, storedUrl)) return ToLocalUrl(cached);
                _ = CacheAsync("Users", userId, iconUrl, forceRefresh: true);
                return normalized;
            }
            return ToLocalUrl(cached);
        }
        if (!string.IsNullOrWhiteSpace(userId) && !string.IsNullOrWhiteSpace(iconUrl))
            _ = CacheAsync("Users", userId, iconUrl, false);
        return RawOrEmpty(iconUrl);
    }

    public static string GetUserBannerUrl(string? userId, string? bannerUrl)
    {
        bannerUrl = StripLocalhostUrl(bannerUrl);
        var bannerId = userId == null ? null : userId + "_banner";
        var cached = GetUserBannerCached(userId);
        if (cached != null)
        {
            if (!string.IsNullOrWhiteSpace(bannerUrl) && bannerId != null)
            {
                var normalized = NormalizeTo512(bannerUrl);
                var storedUrl  = GetStoredUrl("Users", bannerId);
                if (storedUrl == normalized)
                {
                    Log?.Invoke($"[BANNER] → Cache Hit {userId}", "ok");
                    return ToLocalUrl(cached);
                }
                if (!ShouldRefresh($"Users/{bannerId}", normalized, storedUrl)) return ToLocalUrl(cached);
                _ = CacheAsync("Users", bannerId, bannerUrl, forceRefresh: true);
                return normalized;
            }
            return ToLocalUrl(cached);
        }
        if (!string.IsNullOrWhiteSpace(bannerId) && !string.IsNullOrWhiteSpace(bannerUrl))
            _ = CacheAsync("Users", bannerId, bannerUrl, false);
        return RawOrEmpty(bannerUrl);
    }

    public static string GetUserPicOverrideUrl(string? userId, string? picUrl)
    {
        picUrl = StripLocalhostUrl(picUrl);
        var picId = userId == null ? null : userId + "_pfp";
        var cached = GetUserPicOverrideCached(userId);
        if (cached != null)
        {
            if (!string.IsNullOrWhiteSpace(picUrl) && picId != null)
            {
                var normalized = NormalizeTo512(picUrl);
                var storedUrl  = GetStoredUrl("Users", picId);
                if (storedUrl == normalized) return ToLocalUrl(cached);
                if (!ShouldRefresh($"Users/{picId}", normalized, storedUrl)) return ToLocalUrl(cached);
                _ = CacheAsync("Users", picId, picUrl, forceRefresh: true);
                return normalized;
            }
            return ToLocalUrl(cached);
        }
        if (!string.IsNullOrWhiteSpace(picId) && !string.IsNullOrWhiteSpace(picUrl))
            _ = CacheAsync("Users", picId, picUrl, false);
        return RawOrEmpty(picUrl);
    }

// Badges

    public static string GetBadgeUrl(string? badgeId, string? imageUrl)
    {
        var cached = FindCachedFile("Badges", badgeId);
        if (cached != null) return ToLocalUrl(cached);
        imageUrl = StripLocalhostUrl(imageUrl);
        if (!string.IsNullOrWhiteSpace(badgeId) && !string.IsNullOrWhiteSpace(imageUrl))
            _ = CacheAsync("Badges", badgeId, imageUrl, false);
        return RawOrEmpty(imageUrl);
    }

// Events (Group Events, Calendar Events)

    public static string? GetEventCached(string? eventId)
        => FindCachedFile("Events", eventId);

    public static string GetEventUrl(string? eventId, string? imageUrl)
    {
        imageUrl = StripLocalhostUrl(imageUrl);
        var cached = GetEventCached(eventId);
        if (cached != null)
        {
            if (!string.IsNullOrWhiteSpace(imageUrl) && !string.IsNullOrWhiteSpace(eventId))
            {
                var normalized = NormalizeTo512(imageUrl);
                var storedUrl  = GetStoredUrl("Events", eventId);
                if (storedUrl == normalized) return ToLocalUrl(cached);
                if (!ShouldRefresh($"Events/{eventId}", normalized, storedUrl)) return ToLocalUrl(cached);
                _ = CacheAsync("Events", eventId, imageUrl, forceRefresh: true);
                return normalized;
            }
            return ToLocalUrl(cached);
        }
        if (!string.IsNullOrWhiteSpace(eventId) && !string.IsNullOrWhiteSpace(imageUrl))
            _ = CacheAsync("Events", eventId, imageUrl, false);
        return RawOrEmpty(imageUrl);
    }

    public static string? GetVrcPlusCached(string? entityId)
        => FindCachedFile("VRCPlus", entityId);

    public static Task<string?> CacheVrcPlusAsync(string? entityId, string? assetUrl, bool forceRefresh = false)
        => CacheAsync("VRCPlus", entityId, assetUrl, forceRefresh, normalize: false);

    public static string GetVrcPlusUrlIfCached(string? entityId)
    {
        var cached = GetVrcPlusCached(entityId);
        return cached != null ? ToLocalUrl(cached) : "";
    }

    public static bool IsVrcPlusFresh(string? entityId, TimeSpan ttl)
    {
        var cached = GetVrcPlusCached(entityId);
        if (cached == null) return false;
        try { return DateTime.UtcNow - File.GetLastWriteTimeUtc(cached) < ttl; }
        catch { return false; }
    }

// Avatars

    public static string? GetAvatarCached(string? avatarId)
        => FindCachedFile("Avatars", avatarId);

    public static Task<string?> CacheAvatarAsync(string? avatarId, string? imageUrl, bool forceRefresh = false)
        => CacheAsync("Avatars", avatarId, imageUrl, forceRefresh);

    public static string? CacheAvatarBackground(string? avatarId, string? imageUrl)
    {
        var cached = GetAvatarCached(avatarId);
        if (cached != null) return cached;
        if (!string.IsNullOrWhiteSpace(avatarId) && !string.IsNullOrWhiteSpace(imageUrl))
            _ = CacheAvatarAsync(avatarId, imageUrl);
        return null;
    }

    private static bool IsPlaceholderImage(string? url) =>
        url != null
        && (url.Contains(VRCNext.Services.AvtrdbResolver.HiddenAvatarFileId, StringComparison.OrdinalIgnoreCase)
         || url.Contains(VRCNext.Services.AvtrdbResolver.LoadingAvatarFileId, StringComparison.OrdinalIgnoreCase));

    public static string GetAvatarUrlPreferCached(string? avatarId, string? imageUrl)
    {
        if (IsPlaceholderImage(imageUrl)) imageUrl = null;
        var cached = GetAvatarCached(avatarId);
        return cached != null ? ToLocalUrl(cached) : GetAvatarUrl(avatarId, imageUrl);
    }

    public static string GetAvatarUrl(string? avatarId, string? imageUrl)
    {
        imageUrl = StripLocalhostUrl(imageUrl);
        if (IsPlaceholderImage(imageUrl)) imageUrl = null;
        var cached = GetAvatarCached(avatarId);
        if (cached != null)
        {
            if (!string.IsNullOrWhiteSpace(imageUrl) && !string.IsNullOrWhiteSpace(avatarId))
            {
                var normalized = NormalizeTo512(imageUrl);
                var storedUrl  = GetStoredUrl("Avatars", avatarId);
                if (storedUrl == normalized) return ToLocalUrl(cached);
                if (!ShouldRefresh($"Avatars/{avatarId}", normalized, storedUrl)) return ToLocalUrl(cached);
                _ = CacheAsync("Avatars", avatarId, imageUrl, forceRefresh: true);
                return normalized;
            }
            return ToLocalUrl(cached);
        }
        CacheAvatarBackground(avatarId, imageUrl);
        return RawOrEmpty(imageUrl);
    }

// Core

    // Strip stale localhost URLs — we can't re-download from localhost.
    private static string? StripLocalhostUrl(string? url) =>
        url != null && url.StartsWith("http://localhost:") ? null : url;

    // Returns the normalized URL for the frontend, or "" if it has permanently
    // failed (403/404). Prevents the browser from re-requesting dead images.
    private static string RawOrEmpty(string? url)
    {
        var norm = NormalizeTo512(url ?? "");
        if (string.IsNullOrEmpty(norm)) return "";
        return PermafailHelper.IsPermafailed(norm, "Image") ? "" : norm;
    }

    // Extract VRChat file ID from a normalized URL (e.g. .../image/file_xxx/2/512 → file_xxx)
    private static string ExtractFileId(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        var marker = "/image/";
        var i = url.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return "";
        var parts = url[(i + marker.Length)..].Split('/');
        return parts.Length >= 1 ? parts[0] : "";
    }

    // Extract VRChat file version number from a normalized URL (e.g. .../image/file_xxx/2/512 → 2)
    private static int ExtractVersion(string? url)
    {
        if (string.IsNullOrEmpty(url)) return 0;
        var marker = "/image/";
        var i = url.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return 0;
        var parts = url[(i + marker.Length)..].Split('/');
        // parts[0] = file_xxx, parts[1] = version, parts[2] = size
        if (parts.Length >= 2 && int.TryParse(parts[1], out var v)) return v;
        return 0;
    }

    private static bool ShouldRefresh(string key, string incomingUrl, string? storedUrl, bool authoritative = false)
    {
        if (!IsNewerOrUnknown(incomingUrl, storedUrl)) return false;
        if (authoritative) return true;
        var fid = ExtractFileId(incomingUrl);
        if (fid.Length > 0 && _authFileIds.TryGetValue(key, out var authFid) && authFid != fid) return false;
        return true;
    }

    private static readonly ConcurrentDictionary<string, DateTime> _forcePrefix = new();

    public static void ResetRevalidation(string subdirPrefix)
    {
        _forcePrefix[subdirPrefix] = DateTime.UtcNow;
        foreach (var k in _revalidated.Keys)
            if (k.StartsWith(subdirPrefix + "/", StringComparison.Ordinal)) _revalidated.TryRemove(k, out _);
    }

    private static void RevalidateInBackground(string subdir, string entityId, string imageUrl, string cachedPath)
    {
        var key = $"{subdir}/{entityId}";
        var now = DateTime.UtcNow;
        var forced = _forcePrefix.TryGetValue(subdir, out var ft) && now - ft < TimeSpan.FromMinutes(2);
        if (!forced) return;
        if (_revalidated.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(2)) return;
        _revalidated[key] = now;
        _ = Task.Run(async () =>
        {
            try
            {
                if (_http == null) return;
                var url = NormalizeTo512(imageUrl);
                long remoteLen = -1;
                await _downloadSem.WaitAsync();
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation(BackoffHandler.NoBackoffHeader, "1");
                    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                    if (!resp.IsSuccessStatusCode) return;
                    remoteLen = resp.Content.Headers.ContentLength ?? -1;
                }
                finally { _downloadSem.Release(); }
                if (remoteLen <= 0) return;
                long localLen;
                try { localLen = new FileInfo(cachedPath).Length; } catch { return; }
                if (remoteLen == localLen) return;
                Log?.Invoke($"CDN CHANGED - {key} - {localLen} -> {remoteLen} bytes, re-downloading", "warn");
                var result = await CacheAsync(subdir, entityId, imageUrl, forceRefresh: true);
                if (result != null) OnImageRefreshed?.Invoke(subdir, entityId);
            }
            catch { }
        });
    }

    private static void RecordAuthoritative(string key, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var norm = NormalizeTo512(url);
        var fid = ExtractFileId(norm);
        if (fid.Length > 0) _authFileIds[key] = fid;
        if (PermafailHelper.IsPermafailed(norm, "Image")) PermafailHelper.Remove(norm, "Image");
    }

    // Returns true if incomingUrl should replace the stored cached image.
    // Different file IDs always refresh (user switched to a different file entirely).
    // Same file ID: only refresh if incoming version is newer or equal.
    private static bool IsNewerOrUnknown(string incomingUrl, string? storedUrl)
    {
        if (string.IsNullOrEmpty(storedUrl)) return true;
        var incomingFileId = ExtractFileId(incomingUrl);
        var storedFileId   = ExtractFileId(storedUrl);
        if (!string.IsNullOrEmpty(incomingFileId) && !string.IsNullOrEmpty(storedFileId) && incomingFileId != storedFileId)
            return true;
        var incomingVer = ExtractVersion(incomingUrl);
        var storedVer   = ExtractVersion(storedUrl);
        if (incomingVer == 0 || storedVer == 0) return true;
        return incomingVer >= storedVer;
    }

    private static Task<string?> CacheAsync(string subdir, string? entityId, string? imageUrl, bool forceRefresh, bool normalize = true)
    {
        imageUrl = StripLocalhostUrl(imageUrl);
        if (string.IsNullOrWhiteSpace(entityId) || string.IsNullOrWhiteSpace(imageUrl) || _http == null)
            return Task.FromResult<string?>(null);
        var permaKey = normalize ? NormalizeTo512(imageUrl) : imageUrl;
        if (PermafailHelper.IsPermafailed(permaKey, "Image"))
            return Task.FromResult<string?>(null);

        if (!forceRefresh)
        {
            var existing = FindCachedFile(subdir, entityId);
            if (existing != null) return Task.FromResult<string?>(existing);
        }

        var key = $"{subdir}/{entityId}";

        return _downloads.GetOrAdd(key, _key =>
        {
            var task = DownloadAsync(subdir, entityId, imageUrl, forceRefresh, normalize);
            return task.ContinueWith(t =>
            {
                _downloads.TryRemove(key, out _);
                return t.Status == TaskStatus.RanToCompletion ? t.Result : null;
            }, TaskContinuationOptions.ExecuteSynchronously);
        });
    }

    private static async Task<string?> DownloadAsync(string subdir, string entityId, string imageUrl, bool forceRefresh, bool normalize = true)
    {
        var dir      = Path.Combine(_baseDir, subdir);
        var tmpPath  = Path.Combine(dir, entityId + ".tmp");
        var fetchUrl = normalize ? NormalizeTo512(imageUrl) : imageUrl;

        Log?.Invoke($"CDN - {subdir} - {fetchUrl}", "sec");

        await _downloadSem.WaitAsync();
        try
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, fetchUrl);
                req.Headers.TryAddWithoutValidation(BackoffHandler.NoBackoffHeader, "1");
                using var resp = await _http!.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                var code = (int)resp.StatusCode;
                if (!resp.IsSuccessStatusCode)
                {
                    var color = code == 429 ? "warn" : "err";
                    Log?.Invoke($"CDN {code} - {subdir} - {fetchUrl}", color);
                    if (code == 403 || code == 404) PermafailHelper.Add(fetchUrl, "Image", code);
                    return null;
                }

                using (var stream = await resp.Content.ReadAsStreamAsync())
                using (var fs    = File.Create(tmpPath))
                    await stream.CopyToAsync(fs);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"CDN ERR - {subdir} - {fetchUrl} ({ex.Message})", "err");
                TryDelete(tmpPath);
                return null;
            }

            var ext = DetectExtension(tmpPath);
            if (ext == null)
            {
                Log?.Invoke($"CDN SKIP - {subdir} - {fetchUrl} (not an image)", "warn");
                TryDelete(tmpPath);
                return null;
            }

            if (forceRefresh)
            {
                _pathCache.TryRemove($"{subdir}/{entityId}", out _);
                foreach (var old in _imageExtensions)
                    TryDelete(Path.Combine(dir, entityId + old));
            }

            var finalPath = Path.Combine(dir, entityId + ext);
            try
            {
                File.Move(tmpPath, finalPath, overwrite: true);
            }
            catch
            {
                TryDelete(tmpPath);
                return null;
            }

            _pathCache[$"{subdir}/{entityId}"] = finalPath;
            SaveUrl(subdir, entityId, fetchUrl);
            Log?.Invoke($"CDN 200 - {subdir} - {fetchUrl}", "ok");
            ScheduleTrim();
            return finalPath;
        }
        finally { _downloadSem.Release(); }
    }

// Helpers
    private static string? FindCachedFile(string subdir, string? entityId)
    {
        if (string.IsNullOrWhiteSpace(entityId)) return null;
        var key = $"{subdir}/{entityId}";
        if (_pathCache.TryGetValue(key, out var memo))
            return memo.Length > 0 ? memo : null;
        var dir = Path.Combine(_baseDir, subdir);
        foreach (var ext in _imageExtensions)
        {
            var path = Path.Combine(dir, entityId + ext);
            if (File.Exists(path))
            {
                _pathCache[key] = path;
                return path;
            }
        }
        _pathCache[key] = "";
        return null;
    }

    private static string? DetectExtension(string path)
    {
        try
        {
            Span<byte> hdr = stackalloc byte[12];
            using var f = File.OpenRead(path);
            f.ReadAtLeast(hdr, hdr.Length, throwOnEndOfStream: false);

            if (hdr[0] == 0xFF && hdr[1] == 0xD8 && hdr[2] == 0xFF)
                return ".jpg";
            if (hdr[0] == 0x89 && hdr[1] == 0x50 && hdr[2] == 0x4E && hdr[3] == 0x47)
                return ".png";
            if (hdr[0] == 0x52 && hdr[1] == 0x49 && hdr[2] == 0x46 && hdr[3] == 0x46 &&
                hdr[8] == 0x57 && hdr[9] == 0x45 && hdr[10] == 0x42 && hdr[11] == 0x50)
                return ".webp";
            if (hdr[0] == 0x47 && hdr[1] == 0x49 && hdr[2] == 0x46)
                return ".gif";
        }
        catch { }
        return null;
    }

    // Cache Manager

    private const int TrimDelayMs = 5000;
    private static int _trimScheduled;
    private static readonly object _trimLock = new();

    private static readonly EnumerationOptions _cacheEnumOptions = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip      = 0,
        IgnoreInaccessible    = false,
    };

    private static bool IsCacheFile(ref System.IO.Enumeration.FileSystemEntry e) =>
        !e.IsDirectory && !e.FileName.EndsWith(".tmp", StringComparison.Ordinal);

    private static void ScheduleTrim()
    {
        if (Interlocked.Exchange(ref _trimScheduled, 1) == 1) return;
        _ = Task.Delay(TrimDelayMs).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _trimScheduled, 0);
            TrimIfNeeded();
        });
    }

    public static long GetCacheSizeBytes()
    {
        if (!Directory.Exists(_baseDir)) return 0;
        long total = 0;
        var sizes = new System.IO.Enumeration.FileSystemEnumerable<long>(
            _baseDir, (ref System.IO.Enumeration.FileSystemEntry e) => e.Length, _cacheEnumOptions)
        {
            ShouldIncludePredicate = IsCacheFile,
        };
        foreach (var len in sizes) total += len;
        return total;
    }

    public static void TrimIfNeeded(bool force = false)
    {
        var limitBytes = (long)LimitGb * 1024 * 1024 * 1024;
        if (limitBytes <= 0 || !Directory.Exists(_baseDir)) return;
        lock (_trimLock)
        try
        {
            var total = GetCacheSizeBytes();

            int deletedFiles = 0;
            long deletedBytes = 0;

            if (force || total > limitBytes)
            {
                var files = new System.IO.Enumeration.FileSystemEnumerable<(string Path, long Length, DateTime LastWriteUtc)>(
                    _baseDir,
                    (ref System.IO.Enumeration.FileSystemEntry e) => (e.ToFullPath(), e.Length, e.LastWriteTimeUtc.UtcDateTime),
                    _cacheEnumOptions)
                {
                    ShouldIncludePredicate = IsCacheFile,
                }.OrderBy(f => f.LastWriteUtc).ToList();
                total = files.Sum(f => f.Length);

                var target = (long)(limitBytes * 0.8);
                foreach (var f in files)
                {
                    if (total <= target) break;
                    try
                    {
                        total -= f.Length;
                        deletedBytes += f.Length;
                        File.Delete(f.Path);
                        deletedFiles++;
                        var rel = Path.GetRelativePath(_baseDir, f.Path);
                        var key = Path.ChangeExtension(rel, null).Replace('\\', '/');
                        _pathCache.TryRemove(key, out _);
                        _urls.TryRemove(key, out _);
                    }
                    catch { }
                }
            }

            int clearedKeys;
            if (force)
            {
                // Clear all in-memory caches — next lookup re-scans disk / re-queries DB
                clearedKeys = _pathCache.Count + _urls.Count;
                _pathCache.Clear();
                _urls.Clear();
            }
            else
            {
                // Remove stale "not found" markers — safe to evict, next lookup rechecks disk
                var staleKeys = _pathCache.Where(kv => kv.Value.Length == 0).Select(kv => kv.Key).ToList();
                foreach (var key in staleKeys)
                    _pathCache.TryRemove(key, out _);
                clearedKeys = staleKeys.Count;
            }

            if (deletedFiles > 0)
                Log?.Invoke($"[GC] - ImageCache - Trimmed {deletedFiles} files ({deletedBytes / 1024 / 1024} MB freed). {clearedKeys} entries cleared.", "sec");
            else if (clearedKeys > 0)
                Log?.Invoke($"[GC] - ImageCache - No disk trim needed. {clearedKeys} entries cleared.", "sec");
            else
                Log?.Invoke($"[GC] - ImageCache - No trim needed. {_pathCache.Count} path entries, {_urls.Count} url entries.", "sec");
        }
        catch { }
    }

    public static async Task OptimizeAsync(Action<int, int>? onProgress = null)
    {
        if (!Directory.Exists(_baseDir)) return;
        const long threshold = (long)(1.5 * 1024 * 1024);
        var pngFiles = new DirectoryInfo(_baseDir)
            .GetFiles("*.png", SearchOption.AllDirectories)
            .Where(f => f.Length > threshold)
            .Select(f => f.FullName)
            .ToList();
        int total = pngFiles.Count, done = 0;
        onProgress?.Invoke(done, total);
        foreach (var pngPath in pngFiles)
        {
            var jpgPath = pngPath[..^4] + ".jpg";
            try
            {
                using var bmp = SkiaSharp.SKBitmap.Decode(pngPath);
                if (bmp == null) continue;
                using var img  = SkiaSharp.SKImage.FromBitmap(bmp);
                using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 80);
                if (data != null)
                {
                    using var fs = File.Create(jpgPath);
                    data.SaveTo(fs);
                    try { File.Delete(pngPath); } catch { }
                }
            }
            catch { }
            done++;
            onProgress?.Invoke(done, total);
            await Task.Yield();
        }
    }

    // Converts VRC Url to CDN 800 Endpoints
    public static string NormalizeTo512(string url)
    {
        const string filePrefix = "/api/1/file/";
        var fi = url.IndexOf(filePrefix, StringComparison.Ordinal);
        if (fi >= 0)
        {
            var rest  = url[(fi + filePrefix.Length)..];
            var parts = rest.Split('/');
            if (parts.Length >= 2 && parts[0].StartsWith("file_", StringComparison.OrdinalIgnoreCase))
                return $"https://api.vrchat.cloud/api/1/image/{parts[0]}/{parts[1]}/800";
        }
        if (url.Contains("/api/1/image/", StringComparison.Ordinal))
        {
            if (url.EndsWith("/256", StringComparison.Ordinal)) return url[..^3] + "800";
            if (url.EndsWith("/512", StringComparison.Ordinal)) return url[..^3] + "800";
        }
        return url;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
