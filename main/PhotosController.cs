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
    public string VrcPhotoDir => _vrcPhotoDir;
    public FileSystemWatcher? VrcPhotoWatcher => _vrcPhotoWatcher;

    // Constructor

    public PhotosController(CoreLibrary core, FriendsController friends, InstanceController instance)
    {
        _core = core;
        _friends = friends;
        _instance = instance;
        _favorites = FavoritedImagesStore.Load();
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
        var vrcPhotoDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VRChat");
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
            var vrcPhotoDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VRChat");
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

        var vrcPhotoDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "VRChat");
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
            Filter = "VRChat_*.png"
        };
        _vrcPhotoWatcher.Created += (s, e) =>
        {
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
                            FavoritedImagesStore.Save(_favorites);
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
                        try
                        {
                            // Use PowerShell to copy image to clipboard natively
                            var escaped = clipPath.Replace("'", "''");
                            var psi = new System.Diagnostics.ProcessStartInfo("powershell",
                                $"-NonInteractive -WindowStyle Hidden -Command \"Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Clipboard]::SetImage([System.Drawing.Image]::FromFile('{escaped}'))\"")
                            { CreateNoWindow = true, UseShellExecute = false };
                            System.Diagnostics.Process.Start(psi);
                            _core.SendToJS("toast", new { ok = true, msg = "Image copied to clipboard" });
                        }
                        catch (Exception ex)
                        {
                            _core.SendToJS("toast", new { ok = false, msg = $"Clipboard failed: {ex.Message}" });
                        }
                    }
                }
                break;

            case "addFavorite":
                var favPath = msg["path"]?.ToString();
                if (!string.IsNullOrEmpty(favPath) && !_favorites.Contains(favPath))
                {
                    _favorites.Add(favPath);
                    FavoritedImagesStore.Save(_favorites);
                }
                break;

            case "removeFavorite":
                var unfavPath = msg["path"]?.ToString();
                if (!string.IsNullOrEmpty(unfavPath))
                {
                    _favorites.Remove(unfavPath);
                    FavoritedImagesStore.Save(_favorites);
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
#if WINDOWS
                var wallPath = msg["path"]?.ToString();
                if (!string.IsNullOrEmpty(wallPath) && File.Exists(wallPath))
                {
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
                }
#else
                var lWallPath = msg["path"]?.ToString();
                if (!string.IsNullOrEmpty(lWallPath) && File.Exists(lWallPath))
                {
                    bool wok = SetLinuxWallpaper(lWallPath);
                    _core.SendToJS("toast", new { ok = wok, msg = wok
                        ? "Desktop background updated"
                        : "Could not set wallpaper (unsupported desktop environment)" });
                }
#endif
                break;
        }
    }

    // Private Methods

#if !WINDOWS
    private static bool SetLinuxWallpaper(string path)
    {
        var uri = "file://" + path;
        if (TryRun("plasma-apply-wallpaperimage", path)) return true;
        bool gnome = TryRun("gsettings", "set", "org.gnome.desktop.background", "picture-uri", uri);
        TryRun("gsettings", "set", "org.gnome.desktop.background", "picture-uri-dark", uri);
        if (gnome) return true;
        if (TryRun("gsettings", "set", "org.cinnamon.desktop.background", "picture-uri", uri)) return true;
        if (TryRun("xfconf-query", "-c", "xfce4-desktop", "-p", "/backdrop/screen0/monitor0/workspace0/last-image", "-s", path)) return true;
        if (TryRun("feh", "--bg-fill", path)) return true;
        return false;
    }

    private static bool TryRun(string file, params string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = file,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(8000)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
#endif

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

            string? worldId = null;
            List<object>? players = null;
            string authorName = "", authorId = "";
            if (isImg)
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
        if (!_imgExts.Contains(Path.GetExtension(filePath)))
            return;
        if (_core.PhotoPlayersStore.GetPhotoRecord(fileName) != null) return; // already recorded

        try
        {
            var logPlayers = _core.LogWatcher.GetCurrentPlayers();
            var wid = _core.LogWatcher.CurrentWorldId ?? "";

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

            // Timeline: log photo event
            var photoUrl = GetVirtualMediaUrl(filePath);
            var phWorldName  = _instance.CachedInstWorldName;
            var phWorldThumb = _instance.CachedInstWorldThumb;
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
            return;
        }

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

                var all = BuildLibraryItemsFast();
                _core.SendToJS("libraryData", new { files = all, total = all.Count, hasMore = false });

                // Background pass: read PNG world IDs without blocking the UI
                EnrichLibraryWorldIds();
            }
            catch (Exception ex)
            {
                _core.SendToJS("log", new { msg = $"Library scan error: {ex.Message}", color = "err" });
            }
        });
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

            string? worldId = null;
            List<object>? players = null;
            if (isImg)
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

            var (imgW, imgH) = isImg ? ReadImageDimensions(f.FullName) : (0, 0);

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

            try
            {
                var (an, aid) = UnifiedTimeEngine.ExtractPhotoAuthorFromPng(f.FullName);
                if (!string.IsNullOrEmpty(an) || !string.IsNullOrEmpty(aid))
                    authorBatch[f.FullName] = new { name = an ?? "", id = aid ?? "" };
            }
            catch { }

            var rec = _core.PhotoPlayersStore.GetPhotoRecord(f.Name);
            if (rec == null || string.IsNullOrEmpty(rec.WorldId))
            {
                string? worldId = null;
                try { worldId = UnifiedTimeEngine.ExtractWorldIdFromPng(f.FullName); } catch { }
                if (!string.IsNullOrEmpty(worldId)) batch[f.FullName] = worldId;
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

    // Keep old paginated builder for loadLibraryPage compatibility
    private List<object> BuildLibraryItems(int offset, int count)
        => BuildLibraryItemsFast().Skip(offset).Take(count).ToList();

    // Reads image dimensions from PNG/JPEG file headers without decoding pixels.
    // PNG: IHDR chunk at bytes 16-23. JPEG: scan for SOF0-SOF3 marker.
    private static (int W, int H) ReadImageDimensions(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 512);
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
