using Newtonsoft.Json.Linq;
using VRCNext.Services;
using VRCNext.Services.Helpers;
#if WINDOWS
using System.Runtime.InteropServices;
#endif

namespace VRCNext;

// Owns all photo/library related state, logic, and message handling.

public class PhotosController
{
    private readonly CoreLibrary _core;
    private readonly FriendsController _friends;
    private readonly InstanceController _instance;

    // Photo State
    private List<string> _favorites;
    private string _vrcPhotoDir = "";
    private FileSystemWatcher? _vrcPhotoWatcher;
    private List<LibFileEntry> _libFileCache = new();
    private int _libFileCacheTotal = 0;
    private bool _libCacheReady = false;
    private int _libScanRunning;
    private readonly MediaLibraryStore _media;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int W, int H)> _dimCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _ratingCache;
    private bool _ratingsScanned = false;
    private readonly List<WebhookService.PostRecord> _postHistory = new();
    private int _fileCount;
    private double _totalSizeMB;
    private readonly SemaphoreSlim _photoBootstrapLock = new(1, 1);

    // Library file cache entry
    public record LibFileEntry(FileInfo Fi, int FolderIndex, string Folder);

    private static readonly HashSet<string> _imgExts =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

#if WINDOWS
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);
    private const int SPI_SETDESKWALLPAPER = 0x0014;
    private const int SPIF_UPDATEINIFILE   = 0x01;
    private const int SPIF_SENDCHANGE      = 0x02;
#endif

    // Public Accessors (for other domains)
    public List<string> Favorites => _favorites;
    public Dictionary<string, int> Ratings => _ratingCache.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
    public string VrcPhotoDir => _vrcPhotoDir;
    public FileSystemWatcher? VrcPhotoWatcher => _vrcPhotoWatcher;

    // Constructor

    public PhotosController(CoreLibrary core, FriendsController friends, InstanceController instance)
    {
        _core = core;
        _friends = friends;
        _instance = instance;
        _media       = MediaLibraryStore.Load();
        _favorites   = _media.GetFavorites();
        _ratingCache = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(_media.GetRatings());

        if (_media.NeedsLegacyMigration())
        {
            _ = Task.Run(() =>
            {
                if (!_media.MigrateLegacyJson()) return;
                _favorites = _media.GetFavorites();
                foreach (var kv in _media.GetRatings()) _ratingCache[kv.Key] = kv.Value;
                _core.SendToJS("favoritesLoaded", _favorites);
                _core.SendToJS("libraryRatings", Ratings);
            });
        }
    }

    // Public Methods

    public string GetVirtualMediaUrl(string filePath)
    {
        // Check watch-folder routes first
        for (int i = 0; i < _core.Settings.WatchFolders.Count; i++)
        {
            var folder = _core.Settings.WatchFolders[i];
            if (!Directory.Exists(folder)) continue;
            if (filePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            {
                var rel = filePath.Substring(folder.Length).TrimStart('\\', '/').Replace('\\', '/');
                return $"http://localhost:{_core.HttpPort}/media{i}/{Uri.EscapeDataString(rel)}";
            }
        }
        // Fallback: VRChat screenshot folder
        var vrcPhotoDir = VrcPathsHelper.PhotoDir();
        if (filePath.StartsWith(vrcPhotoDir, StringComparison.OrdinalIgnoreCase))
        {
            var rel = filePath.Substring(vrcPhotoDir.Length).TrimStart('\\', '/').Replace('\\', '/');
            return $"http://localhost:{_core.HttpPort}/vrcphotos/{Uri.EscapeDataString(rel)}";
        }
        return "";
    }

    // Timeline - photo bootstrap (import existing photos)

    // Imports existing photo_players.json entries not yet in timeline
    public async Task BootstrapPhotoTimeline()
    {
        // Serialize concurrent calls so the existingFiles snapshot stays consistent
        await _photoBootstrapLock.WaitAsync();
        try
        {
            // Build set of filenames already in timeline
            var existingFiles = _core.Timeline.GetPhotoFilePaths();

            if (_core.PhotoPlayersStore.Photos.Count == 0) return;

            // Build list of search roots (VRChat photo dir + watch folders)
            var searchRoots = new List<string>();
            var vrcPhotoDir = VrcPathsHelper.PhotoDir();
            if (Directory.Exists(vrcPhotoDir)) searchRoots.Add(vrcPhotoDir);
            foreach (var folder in _core.Settings.WatchFolders.Where(Directory.Exists))
            {
                if (!searchRoots.Any(r => r.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                    searchRoots.Add(folder);
            }

            int added = 0;
            foreach (var (fileName, rec) in _core.PhotoPlayersStore.Photos)
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
                if (!UnifiedTimeEngine.IsOwnPhoto(filePath, rec.Players.Select(p => p.UserId), _core.CurrentVrcUserId)) continue;

                var photoUrl = GetVirtualMediaUrl(filePath);
                if (string.IsNullOrEmpty(photoUrl)) continue;

                var ts = VrcPathsHelper.TryParseVrcPhotoTime(fileName, out var shotLocal)
                    ? shotLocal.ToUniversalTime()
                    : new FileInfo(filePath).LastWriteTimeUtc;

                var bsWorldName  = "";
                var bsWorldThumb = "";
                if (!string.IsNullOrEmpty(rec.WorldId) && _core.TimeEngine.Worlds.TryGetValue(rec.WorldId, out var bsWRec) && !string.IsNullOrEmpty(bsWRec.WorldName))
                { bsWorldName = bsWRec.WorldName; bsWorldThumb = bsWRec.WorldThumb; }
                var ev = new TimelineService.TimelineEvent
                {
                    Type       = "photo",
                    Timestamp  = ts.ToString("o"),
                    WorldId    = rec.WorldId,
                    WorldName  = bsWorldName,
                    WorldThumb = bsWorldThumb,
                    PhotoPath  = filePath,
                    PhotoUrl   = photoUrl,
                    Players    = rec.Players.Select(p => new TimelineService.PlayerSnap
                    {
                        UserId      = p.UserId,
                        DisplayName = p.DisplayName,
                        Image       = _friends.ResolveWithDiskFallback(p.UserId, p.Image)
                    }).ToList()
                };
                _core.Timeline.AddEvent(ev);
                existingFiles.Add(fileName);
                added++;
            }

            if (added > 0)
                _core.SendToJS("log", new { msg = $"[TIMELINE] Imported {added} existing photo(s)", color = "sec" });
        }
        catch (Exception ex)
        {
            try { _core.SendToJS("log", new { msg = $"[TIMELINE] Bootstrap error: {ex.Message}", color = "err" }); } catch { }
        }
        finally
        {
            _photoBootstrapLock.Release();
        }
    }

    public void HandleExternalSave(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;
            SnapshotPhotoPlayers(filePath);
            AddFileToLibrary(filePath);
        }
        catch (Exception ex)
        {
            CrashHandler.WriteEntry("PhotosController.HandleExternalSave", ex);
        }
    }

    // File Watcher - Post to Discord
    public async void OnNewFile(object? sender, FileWatcherService.FileArg e)
    {
        try
        {
            // Snapshot players for VRChat screenshots
            SnapshotPhotoPlayers(e.FilePath);

            // Inject into library without rescanning
            AddFileToLibrary(e.FilePath);

            await PostFile(e.FilePath, false, e.SizeMB);
        }
        catch (Exception ex)
        {
            CrashHandler.WriteEntry("PhotosController.OnNewFile", ex);
        }
    }

    public void StartVrcPhotoWatcher()
    {
        if (_vrcPhotoWatcher != null) return; // already running

        var vrcPhotoDir = VrcPathsHelper.PhotoDir();
        if (!Directory.Exists(vrcPhotoDir))
        {
            try { Directory.CreateDirectory(vrcPhotoDir); }
            catch { return; }
        }

        // Store for HttpListener /vrcphotos/ route
        _vrcPhotoDir = vrcPhotoDir;

        _vrcPhotoWatcher = new FileSystemWatcher(vrcPhotoDir)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
            Filter = "VRChat_*"
        };
        _vrcPhotoWatcher.Created += (s, e) =>
        {
            var ext = Path.GetExtension(e.FullPath);
            if (!_imgExts.Contains(ext) && !FileWatcherService.VidExt.Contains(ext)) return;
            // Small delay so file is fully written
            Task.Run(async () =>
            {
                await Task.Delay(2000);
                try { SnapshotPhotoPlayers(e.FullPath); AddFileToLibrary(e.FullPath); }
                catch { }
            });
        };

        // Also snapshot any VRChat photos from watch folders
        foreach (var folder in _core.Settings.WatchFolders.Where(Directory.Exists))
        {
            if (folder.Equals(vrcPhotoDir, StringComparison.OrdinalIgnoreCase)) continue;
            // The relay FileWatcher already handles these via OnNewFile
        }
    }

    public async Task PostFile(string filePath, bool manual, double sizeMB = 0)
    {
        var fileName = Path.GetFileName(filePath);
        if (sizeMB == 0)
        {
            try { sizeMB = new FileInfo(filePath).Length / 1048576.0; } catch { return; }
        }

        var typeStr = FileWatcherService.ImgExt.Contains(Path.GetExtension(filePath)) ? "image" : "video";
        var prefix = manual ? "Manual post" : "New file";
        _core.SendToJS("log", new { msg = $"{prefix}: {fileName} ({sizeMB:F1} MB)", color = "default" });

        var whs = _core.Settings.Webhooks.Where(w => w.Enabled && !string.IsNullOrWhiteSpace(w.Url)).ToList();

        foreach (var wh in whs)
        {
            var result = await _core.Webhook.PostFileAsync(wh.Url, filePath, _core.Settings.BotName, _core.Settings.BotAvatarUrl);
            if (result.Success)
            {
                _core.SendToJS("log", new { msg = $"  Posted to '{wh.Name}'", color = "ok" });
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

                _core.SendToJS("stats", new { files = _fileCount, size = $"{_totalSizeMB:F1} MB" });
                _core.SendToJS("filePosted", new
                {
                    name = fileName,
                    channel = wh.Name,
                    size = $"{sizeMB:F1} MB",
                    time = VRCNext.Services.Helpers.DateTimeHelper.FormatTimeWithSeconds(record.PostedAt),
                    messageId = record.MessageId,
                    webhookUrl = wh.Url,
                });
            }
            else
            {
                _core.SendToJS("log", new { msg = $"  Error '{wh.Name}': {result.Error}", color = "err" });
            }
        }
    }

    // Message Handler

    public async Task HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "scanLibrary":
                ScanLibraryFolders(false);
                break;

            case "scanLibraryForce":
                _libCacheReady = false;
                _ratingsScanned = false;
                ScanLibraryFolders(true);
                break;

            case "loadLibraryPage":
                var libOffset = msg["offset"]?.Value<int>() ?? 0;
                _ = Task.Run(() =>
                {
                    var items = BuildLibraryItems(libOffset, 100);
                    _core.SendToJS("libraryPageData", new
                    {
                        files = items,
                        total = _libFileCacheTotal,
                        offset = libOffset,
                        hasMore = libOffset + items.Count < _libFileCacheTotal,
                    });
                });
                break;

            case "deleteLibraryFile":
                var delPath = msg["path"]?.ToString();
                if (!string.IsNullOrEmpty(delPath))
                {
                    try
                    {
                        var fullDelPath = Path.GetFullPath(delPath);
                        bool inAllowedFolder = _core.Settings.WatchFolders.Any(f =>
                            !string.IsNullOrEmpty(f) &&
                            fullDelPath.StartsWith(
                                Path.GetFullPath(f).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase));
                        if (!inAllowedFolder)
                        {
                            _core.SendToJS("log", new { msg = "Delete blocked: path outside watch folders.", color = "err" });
                            break;
                        }
                        if (File.Exists(fullDelPath))
                        {
                            File.Delete(fullDelPath);
                            _favorites.Remove(delPath);
                            _ratingCache.TryRemove(delPath, out _);
                            _media.Delete(delPath);
                            _core.SendToJS("log", new { msg = $"Deleted: {Path.GetFileName(fullDelPath)}", color = "ok" });
                            _core.SendToJS("libraryFileDeleted", new { path = delPath });
                        }
                        else
                        {
                            _core.SendToJS("log", new { msg = "File not found", color = "err" });
                        }
                    }
                    catch (Exception ex)
                    {
                        _core.SendToJS("log", new { msg = $"Delete error: {ex.Message}", color = "err" });
                    }
                }
                break;

            case "copyImageToClipboard":
                {
                    var clipPath = msg["path"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(clipPath) && File.Exists(clipPath))
                    {
#if WINDOWS
                        try
                        {
                            var escaped = clipPath.Replace("'", "''");
                            var psCommand = $"Add-Type -AssemblyName System.Windows.Forms; $col = [System.Collections.Specialized.StringCollection]::new(); $col.Add('{escaped}'); [System.Windows.Forms.Clipboard]::SetFileDropList($col)";
                            var psi = new System.Diagnostics.ProcessStartInfo("powershell",
                                $"-NonInteractive -WindowStyle Hidden -Command \"{psCommand}\"")
                            { CreateNoWindow = true, UseShellExecute = false };
                            System.Diagnostics.Process.Start(psi);
                            _core.SendToJS("toast", new { ok = true, msg = "Copied to clipboard" });
                        }
                        catch (Exception ex)
                        {
                            _core.SendToJS("toast", new { ok = false, msg = $"Clipboard failed: {ex.Message}" });
                        }
#else
                        await Task.Run(() =>
                        {
                            if (LinuxDesktopHelper.CopyImageToClipboard(clipPath, out var clipErr))
                                _core.SendToJS("toast", new { ok = true, msg = "Copied to clipboard" });
                            else
                                _core.SendToJS("toast", new { ok = false, msg = $"Clipboard failed: {clipErr}" });
                        });
#endif
                    }
                }
                break;

            case "getMediaTags":
                SendMediaTags();
                break;

            case "setMediaTags":
            {
                var tagPath = msg["path"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(tagPath)) break;
                var tagList = (msg["tags"] as JArray)?
                    .Select(x => x?.ToString() ?? "")
                    .Where(x => MediaTagCatalog.Contains(x))
                    .Distinct()
                    .ToList() ?? new List<string>();
                _media.SetTags(tagPath, tagList);
                _core.SendToJS("mediaTagsUpdated", new { path = tagPath, tags = tagList });
                break;
            }

            case "setMediaUserTag":
            {
                var utPath = msg["path"]?.ToString() ?? "";
                var utUser = msg["userId"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(utPath) || string.IsNullOrEmpty(utUser)) break;
                var utName = msg["displayName"]?.ToString() ?? "";
                var utX    = Math.Clamp(msg["x"]?.Value<double>() ?? 0, 0, 1);
                var utY    = Math.Clamp(msg["y"]?.Value<double>() ?? 0, 0, 1);
                _media.SetUserTag(utPath, utUser, utName, utX, utY);
                SendUserTagsFor(utPath);
                break;
            }

            case "removeMediaUserTag":
            {
                var rmPath = msg["path"]?.ToString() ?? "";
                var rmUser = msg["userId"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(rmPath) || string.IsNullOrEmpty(rmUser)) break;
                _media.RemoveUserTag(rmPath, rmUser);
                SendUserTagsFor(rmPath);
                break;
            }

            case "addFavorite":
                var favPath = msg["path"]?.ToString();
                if (!string.IsNullOrEmpty(favPath) && !_favorites.Contains(favPath))
                {
                    _favorites.Add(favPath);
                    _media.SetFavorite(favPath, true);
                }
                break;

            case "removeFavorite":
                var unfavPath = msg["path"]?.ToString();
                if (!string.IsNullOrEmpty(unfavPath))
                {
                    _favorites.Remove(unfavPath);
                    _media.SetFavorite(unfavPath, false);
                }
                break;

#if WINDOWS
            case "getPhotoRating":
                {
                    var ratPath = msg["path"]?.ToString();
                    if (string.IsNullOrEmpty(ratPath)) break;
                    if (_ratingCache.TryGetValue(ratPath, out var cachedStars))
                    {
                        _core.SendToJS("photoRating", new { path = ratPath, stars = cachedStars });
                        break;
                    }
                    _ = Task.Run(async () =>
                    {
                        var stars = await PhotoRatingHelper.GetRatingAsync(ratPath);
                        _ratingCache[ratPath] = stars;
                        _core.SendToJS("photoRating", new { path = ratPath, stars });
                    });
                }
                break;

            case "setPhotoRating":
                {
                    var ratPath = msg["path"]?.ToString();
                    var stars = msg["stars"]?.Value<int>() ?? 0;
                    if (string.IsNullOrEmpty(ratPath)) break;
                    _ = Task.Run(async () =>
                    {
                        var ok = await PhotoRatingHelper.SetRatingAsync(ratPath, stars);
                        if (ok)
                        {
                            _ratingCache[ratPath] = stars;
                            _media.SetRating(ratPath, stars);
                        }
                        _core.SendToJS("photoRatingSet", new { path = ratPath, stars, ok });
                    });
                }
                break;

            case "scanLibraryRatings":
                if (!_ratingsScanned)
                {
                    _ratingsScanned = true;
                    _ = Task.Run(() => EnrichLibraryRatings());
                }
                break;
#endif

            case "manualPost":
                var filePath = msg["filePath"]?.ToString();
                if (filePath != null) await PostFile(filePath, true);
                break;

            case "deletePost":
                var msgId = msg["messageId"]?.ToString();
                var whUrl = msg["webhookUrl"]?.ToString();
                if (msgId != null && whUrl != null)
                {
                    var ok = await _core.Webhook.DeleteAsync(whUrl, msgId);
                    _core.SendToJS("deleteResult", new { messageId = msgId, success = ok });
                    if (ok) _postHistory.RemoveAll(p => p.MessageId == msgId);
                }
                break;

            case "setDesktopBackground":
            {
                var wallPath = msg["path"]?.ToString();
                if (!string.IsNullOrEmpty(wallPath) && File.Exists(wallPath))
                {
#if WINDOWS
                    try
                    {
                        using var regKey = Microsoft.Win32.Registry.CurrentUser
                            .OpenSubKey(@"Control Panel\Desktop", writable: true);
                        if (regKey != null)
                        {
                            regKey.SetValue("Wallpaper",       wallPath);
                            regKey.SetValue("WallpaperStyle",  "10"); // Fill
                            regKey.SetValue("TileWallpaper",   "0");
                        }
                        SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, wallPath, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
                        _core.SendToJS("toast", new { ok = true, msg = "Desktop background updated" });
                    }
                    catch (Exception ex)
                    {
                        _core.SendToJS("toast", new { ok = false, msg = $"Wallpaper error: {ex.Message}" });
                    }
#else
                    await Task.Run(() =>
                    {
                        if (LinuxDesktopHelper.SetWallpaper(wallPath, out var wallErr))
                            _core.SendToJS("toast", new { ok = true, msg = "Desktop background updated" });
                        else
                            _core.SendToJS("toast", new { ok = false, msg = $"Wallpaper error: {wallErr}" });
                    });
#endif
                }
                break;
            }
        }
    }

    // Private Methods

    // Add a single new file to the library cache and push it to JS immediately.
    // No-op if the cache isn't ready yet (the next scan will pick it up).
    private void AddFileToLibrary(string filePath)
    {
        if (!_libCacheReady) return;
        try
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists) return;

            // Find which watch folder contains this file
            int folderIdx = -1;
            string folder = "";
            for (int i = 0; i < _core.Settings.WatchFolders.Count; i++)
            {
                if (filePath.StartsWith(_core.Settings.WatchFolders[i], StringComparison.OrdinalIgnoreCase))
                { folderIdx = i; folder = _core.Settings.WatchFolders[i]; break; }
            }
            if (folderIdx < 0) return;

            // Deduplicate
            if (_libFileCache.Any(e => e.Fi.FullName.Equals(filePath, StringComparison.OrdinalIgnoreCase))) return;

            var entry = new LibFileEntry(fi, folderIdx, folder);
            _libFileCache.Insert(0, entry);
            _libFileCacheTotal = _libFileCache.Count;

            var isImg  = FileWatcherService.ImgExt.Contains(fi.Extension);
            var isGif  = fi.Extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
            var sizeMB = fi.Length / 1048576.0;
            var rel    = Path.GetRelativePath(folder, filePath).Replace('\\', '/');
            var url    = $"http://localhost:{_core.HttpPort}/media{folderIdx}/{Uri.EscapeDataString(rel).Replace("%2F", "/")}";

            var isVid  = FileWatcherService.VidExt.Contains(fi.Extension);
            string? worldId = null;
            List<object>? players = null;
            string authorName = "", authorId = "";
            if (isImg || isVid)
            {
                var rec = _core.PhotoPlayersStore.GetPhotoRecord(fi.Name);
                if (rec != null)
                {
                    worldId = rec.WorldId;
                    players = rec.Players.Select(p => (object)new
                    {
                        userId = p.UserId, displayName = p.DisplayName,
                        image  = _friends.ResolveWithDiskFallback(p.UserId, p.Image)
                    }).ToList();
                }
                if (fi.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var (an, aid) = UnifiedTimeEngine.ExtractPhotoAuthorFromPng(fi.FullName);
                        authorName = an ?? "";
                        authorId   = aid ?? "";
                    }
                    catch { }
                }
            }

            _core.SendToJS("libraryNewFile", new
            {
                name     = fi.Name,
                path     = fi.FullName,
                folder,
                type     = isGif ? "gif" : isImg ? "image" : "video",
                size     = sizeMB < 1 ? $"{fi.Length / 1024.0:F0} KB" : $"{sizeMB:F1} MB",
                modified = fi.CreationTime.ToString("o"),
                time     = VRCNext.Services.Helpers.DateTimeHelper.FormatTime(fi.CreationTime),
                url,
                worldId  = worldId ?? "",
                players  = players ?? new List<object>(),
                authorName,
                authorId,
            });
        }
        catch { }
    }

    private void SnapshotPhotoPlayers(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var ext = Path.GetExtension(filePath);
        var isImage = _imgExts.Contains(ext);
        if (!isImage && !FileWatcherService.VidExt.Contains(ext))
            return;
        if (_core.PhotoPlayersStore.GetPhotoRecord(fileName) != null) return; // already recorded

        try
        {
            var logPlayers = _core.LogWatcher.GetCurrentPlayers();
            var wid = _core.LogWatcher.CurrentWorldId ?? "";
            if (Path.GetExtension(filePath).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var pngWid = UnifiedTimeEngine.ExtractWorldIdFromPng(filePath);
                    if (!string.IsNullOrEmpty(pngWid)) wid = pngWid;
                }
                catch { }
            }

            var players = new List<(string userId, string displayName, string image)>();

            var selfRaw  = _core.VrcApi.CurrentUserRaw;
            var selfId   = _core.VrcApi.CurrentUserId ?? "";
            var selfName = selfRaw?["displayName"]?.ToString() ?? "";
            var selfImg  = selfRaw != null ? ImageCacheHelper.GetUserUrl(selfId, VRChatApiService.GetUserImage(selfRaw)) : "";
            if (!string.IsNullOrEmpty(selfId) && !string.IsNullOrEmpty(selfName))
                players.Add((selfId, selfName, selfImg));

            foreach (var p in logPlayers)
            {
                if (p.UserId == selfId) continue;
                var img = "";
                if (!string.IsNullOrEmpty(p.UserId))
                {
                    if (_friends.TryGetNameImage(p.UserId, out var fi) && !string.IsNullOrEmpty(fi.image))
                        img = fi.image;
                }
                players.Add((p.UserId, p.DisplayName, img));
            }

            // Don't create an empty record; it would poison the cache and prevent
            // re-snapshot on subsequent library loads when VRChat data becomes available.
            if (string.IsNullOrEmpty(wid) && players.Count == 0) return;

            _core.PhotoPlayersStore.RecordPhoto(fileName, players, wid);
            _core.PhotoPlayersStore.Save();

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
                        var profile = await _core.Users.GetUserAsync(p.userId);
                        if (profile != null)
                        {
                            var img = VRChatApiService.GetUserImage(profile);
                            var rec = _core.PhotoPlayersStore.GetPhotoRecord(fileName);
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
                if (updated) _core.PhotoPlayersStore.Save();
            });

            _core.SendToJS("log", new { msg = $"\U0001f4f8 Captured {players.Count} players for {fileName}", color = "sec" });

            if (!isImage) return;

            // Timeline: log photo event
            var photoUrl = GetVirtualMediaUrl(filePath);
            var phWorldName  = "";
            var phWorldThumb = "";
            if (wid == (_core.LogWatcher.CurrentWorldId ?? ""))
            { phWorldName = _instance.CachedInstWorldName; phWorldThumb = _instance.CachedInstWorldThumb; }
            if (string.IsNullOrEmpty(phWorldName) && _core.TimeEngine.Worlds.TryGetValue(wid, out var phWRec) && !string.IsNullOrEmpty(phWRec.WorldName))
            { phWorldName = phWRec.WorldName; phWorldThumb = phWRec.WorldThumb; }
            var photoEv = new TimelineService.TimelineEvent
            {
                Type       = "photo",
                Timestamp  = DateTime.UtcNow.ToString("o"),
                WorldId    = wid,
                WorldName  = phWorldName,
                WorldThumb = phWorldThumb,
                PhotoPath  = filePath,
                PhotoUrl   = photoUrl,
                Players    = players.Select(p => new TimelineService.PlayerSnap
                {
                    UserId      = p.userId,
                    DisplayName = p.displayName,
                    Image       = p.image
                }).ToList()
            };
            _core.Timeline.AddEvent(photoEv);
            _core.SendToJS("timelineEvent", _instance.BuildTimelinePayload(photoEv));
        }
        catch { }
    }

    // Media Library -- enumerate files and send all metadata to JS in one shot.
    // force=false: serve from in-memory cache instantly if already scanned (tab re-open).
    // force=true : rescan filesystem (Refresh button).
    private void ScanLibraryFolders(bool force = false)
    {
        // Cache hit -- serve instantly without touching disk, then enrich in background
        if (!force && _libCacheReady && _libFileCache.Count > 0)
        {
            var all = BuildLibraryItemsFast();
            _core.SendToJS("libraryData", new { files = all, total = all.Count, hasMore = false });
            _ = Task.Run(() => EnrichLibraryWorldIds());
#if WINDOWS
            if (!_ratingsScanned) { _ratingsScanned = true; _ = Task.Run(EnrichLibraryRatings); }
#endif
            return;
        }

        if (Interlocked.Exchange(ref _libScanRunning, 1) == 1) return;
        _libCacheReady = false;
        Task.Run(() =>
        {
            try
            {
                var allExts = FileWatcherService.ImgExt.Concat(FileWatcherService.VidExt)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var entries = new List<LibFileEntry>();

                for (int fi = 0; fi < _core.Settings.WatchFolders.Count; fi++)
                {
                    var folder = _core.Settings.WatchFolders[fi];
                    if (!Directory.Exists(folder)) continue;
                    try
                    {
                        new DirectoryInfo(folder)
                            .EnumerateFiles("*.*", SearchOption.AllDirectories)
                            .Where(f => allExts.Contains(f.Extension))
                            .ToList()
                            .ForEach(f => entries.Add(new LibFileEntry(f, fi, folder)));
                    }
                    catch { }
                }

                _libFileCache      = entries.OrderByDescending(e => e.Fi.CreationTime).ToList();
                _libFileCacheTotal = _libFileCache.Count;
                _libCacheReady     = true;

                SyncLibraryDb();

                var all = BuildLibraryItemsFast();
                _core.SendToJS("libraryData", new { files = all, total = all.Count, hasMore = false });

                // Background pass: read PNG world IDs without blocking the UI
                EnrichLibraryWorldIds();
#if WINDOWS
                if (!_ratingsScanned) { _ratingsScanned = true; _ = Task.Run(EnrichLibraryRatings); }
#endif
            }
            catch (Exception ex)
            {
                _core.SendToJS("log", new { msg = $"Library scan error: {ex.Message}", color = "err" });
            }
            finally { Interlocked.Exchange(ref _libScanRunning, 0); }
        });
    }

    // Mirrors the scanned filesystem state into user_photos. Image dimensions are
    // only read from disk for files the DB has not seen before or whose size or
    // creation time changed, so a rescan costs no per-file reads on a warm DB.
    private void SyncLibraryDb()
    {
        try
        {
            var known = _media.GetFileState();
            var rows  = new List<MediaLibraryStore.PhotoRow>(_libFileCache.Count);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var e in _libFileCache)
            {
                var f       = e.Fi;
                var path    = f.FullName;
                var isImg   = FileWatcherService.ImgExt.Contains(f.Extension);
                var isGif   = f.Extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
                var created = f.CreationTime.ToString("o");
                paths.Add(path);

                int w = 0, h = 0;
                if (known.TryGetValue(path, out var st) && st.SizeBytes == f.Length && st.CreatedAt == created)
                {
                    w = st.ImgW; h = st.ImgH;
                }
                else if (isImg)
                {
                    (w, h) = ReadImageDimensions(path);
                }

                _dimCache[path] = (w, h);

                rows.Add(new MediaLibraryStore.PhotoRow
                {
                    Path      = path,
                    Name      = f.Name,
                    Folder    = e.Folder,
                    Type      = isGif ? "gif" : isImg ? "image" : "video",
                    SizeBytes = f.Length,
                    CreatedAt = created,
                    ImgW      = w,
                    ImgH      = h,
                });
            }

            _media.UpsertFiles(rows);
            _media.PruneMissing(paths);
        }
        catch (Exception ex) { CrashHandler.WriteEntry("SyncLibraryDb", ex); }
    }

    // Builds the full item list using only in-memory data -- zero file reads.
    // WorldId comes from the player-record store (already in RAM).
    // ExtractWorldIdFromPng is intentionally skipped here to keep this fast;
    // it is still called live by SnapshotPhotoPlayers when a photo is first taken.
    private List<object> BuildLibraryItemsFast()
    {
        var result = new List<object>(_libFileCache.Count);
        foreach (var e in _libFileCache)
        {
            var f      = e.Fi;
            var isImg  = FileWatcherService.ImgExt.Contains(f.Extension);
            var isGif  = f.Extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
            var sizeMB = f.Length / 1048576.0;
            var rel    = Path.GetRelativePath(e.Folder, f.FullName).Replace('\\', '/');
            var url    = $"http://localhost:{_core.HttpPort}/media{e.FolderIndex}/{Uri.EscapeDataString(rel).Replace("%2F", "/")}";

            var isVid  = FileWatcherService.VidExt.Contains(f.Extension);
            string? worldId = null;
            List<object>? players = null;
            if (isImg || isVid)
            {
                var rec = _core.PhotoPlayersStore.GetPhotoRecord(f.Name); // O(1) dict lookup
                if (rec != null)
                {
                    worldId = rec.WorldId;
                    players = rec.Players.Select(p => (object)new
                    {
                        userId = p.UserId, displayName = p.DisplayName,
                        image  = _friends.ResolveWithDiskFallback(p.UserId, p.Image)
                    }).ToList();
                }
            }

            var (imgW, imgH) = _dimCache.TryGetValue(f.FullName, out var dim)
                ? dim
                : (isImg ? ReadImageDimensions(f.FullName) : (0, 0));

            result.Add(new
            {
                name     = f.Name,
                path     = f.FullName,
                folder   = e.Folder,
                type     = isGif ? "gif" : isImg ? "image" : "video",
                size     = sizeMB < 1 ? $"{f.Length / 1024.0:F0} KB" : $"{sizeMB:F1} MB",
                modified = f.CreationTime.ToString("o"),
                time     = VRCNext.Services.Helpers.DateTimeHelper.FormatTime(f.CreationTime),
                url,
                worldId  = worldId ?? "",
                players  = players ?? new List<object>(),
                imgW,
                imgH,
            });
        }
        return result;
    }
    
    private void EnrichLibraryWorldIds()
    {
        var batch       = new Dictionary<string, string>();
        var authorBatch = new Dictionary<string, object>();
        foreach (var e in _libFileCache)
        {
            var f = e.Fi;
            if (!f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase)) continue;

            string? worldId = null;
            try
            {
                var (wid, _, an, aid) = UnifiedTimeEngine.ExtractPhotoMetaFromPng(f.FullName);
                worldId = wid;
                if (!string.IsNullOrEmpty(an) || !string.IsNullOrEmpty(aid))
                {
                    authorBatch[f.FullName] = new { name = an ?? "", id = aid ?? "" };
                    _media.SetAuthor(f.FullName, an ?? "", aid ?? "");
                }
            }
            catch { }

            var rec = _core.PhotoPlayersStore.GetPhotoRecord(f.Name);
            if (!string.IsNullOrEmpty(worldId) && (rec == null || rec.WorldId != worldId))
            {
                _core.PhotoPlayersStore.UpdateWorldId(f.Name, worldId);
                _media.SetWorldId(f.FullName, worldId);
                batch[f.FullName] = worldId;
            }

            if (batch.Count >= 50 || authorBatch.Count >= 50)
            {
                if (batch.Count > 0)
                {
                    _core.SendToJS("libraryWorldIds", new Dictionary<string, string>(batch));
                    batch.Clear();
                }
                if (authorBatch.Count > 0)
                {
                    _core.SendToJS("libraryAuthors", new Dictionary<string, object>(authorBatch));
                    authorBatch.Clear();
                }
                Thread.Sleep(20); // yield -- keep enrichment low-priority
            }
        }
        if (batch.Count > 0)
            _core.SendToJS("libraryWorldIds", batch);
        if (authorBatch.Count > 0)
            _core.SendToJS("libraryAuthors", authorBatch);
    }

#if WINDOWS
    // Background pass: re-reads the OS "Rating" property so ratings changed
    // outside VRCNext (Explorer, other tools) get picked up. Only differences
    // against the stored value are pushed to JS and written back to the DB.
    private async Task EnrichLibraryRatings()
    {
        var batch   = new Dictionary<string, int>();
        var pending = new List<KeyValuePair<string, int>>();
        int scanned = 0;

        foreach (var e in _libFileCache)
        {
            var path  = e.Fi.FullName;
            var stars = await PhotoRatingHelper.GetRatingAsync(path);

            if (!_ratingCache.TryGetValue(path, out var known) || known != stars)
            {
                _ratingCache[path] = stars;
                batch[path] = stars;
                pending.Add(new KeyValuePair<string, int>(path, stars));
            }

            if (batch.Count >= 50)
            {
                _core.SendToJS("libraryRatings", new Dictionary<string, int>(batch));
                batch.Clear();
            }
            if (pending.Count >= 200) { _media.SetRatings(pending); pending.Clear(); }
            if (++scanned % 50 == 0) await Task.Delay(20);
        }

        if (batch.Count > 0)   _core.SendToJS("libraryRatings", batch);
        if (pending.Count > 0) _media.SetRatings(pending);
    }
#endif

    // Keep old paginated builder for loadLibraryPage compatibility
    public static readonly string[] MediaTagCatalog =
    {
        "funny", "romantic", "lovely", "sad", "extreme", "meme", "funky", "dancing",
        "friends", "group", "relationship", "sports", "activities", "games", "sleeping", "misc",
    };

    private void SendMediaTags()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var tags = _media.GetAllTags()
                    .ToDictionary(kv => kv.Key, kv => kv.Value.Where(MediaTagCatalog.Contains).ToList());
                var userTags = _media.GetAllUserTags()
                    .GroupBy(r => r.Path)
                    .ToDictionary(g => g.Key, g => g.Select(r => (object)new
                    {
                        userId      = r.UserId,
                        displayName = r.DisplayName,
                        x           = r.X,
                        y           = r.Y,
                    }).ToList());
                _core.SendToJS("mediaTagsData", new { tags, userTags, catalog = MediaTagCatalog });
            }
            catch (Exception ex) { CrashHandler.WriteEntry("PhotosController.SendMediaTags", ex); }
        });
    }

    private void SendUserTagsFor(string path)
    {
        var list = _media.GetAllUserTags()
            .Where(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase))
            .Select(r => (object)new { userId = r.UserId, displayName = r.DisplayName, x = r.X, y = r.Y })
            .ToList();
        _core.SendToJS("mediaUserTagsUpdated", new { path, userTags = list });
    }

    private List<object> BuildLibraryItems(int offset, int count)
        => BuildLibraryItemsFast().Skip(offset).Take(count).ToList();

    // Reads image dimensions from PNG/JPEG file headers without decoding pixels.
    // PNG: IHDR chunk at bytes 16-23. JPEG: scan for SOF0-SOF3 marker.
    private static (int W, int H) ReadImageDimensions(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                          bufferSize: 8192, FileOptions.SequentialScan);
            if (ext == ".png")
            {
                Span<byte> buf = stackalloc byte[24];
                if (fs.Read(buf) < 24) return (0, 0);
                int w = (buf[16] << 24) | (buf[17] << 16) | (buf[18] << 8) | buf[19];
                int h = (buf[20] << 24) | (buf[21] << 16) | (buf[22] << 8) | buf[23];
                return (w, h);
            }
            if (ext == ".jpg" || ext == ".jpeg")
            {
                Span<byte> buf = stackalloc byte[4096];
                int read = fs.Read(buf);
                for (int i = 0; i < read - 9; i++)
                {
                    if (buf[i] != 0xFF) continue;
                    byte m = buf[i + 1];
                    if (m >= 0xC0 && m <= 0xC3)
                    {
                        int h = (buf[i + 5] << 8) | buf[i + 6];
                        int w = (buf[i + 7] << 8) | buf[i + 8];
                        return (w, h);
                    }
                }
            }
        }
        catch { }
        return (0, 0);
    }
}
