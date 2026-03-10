using VRCNext.Services;

namespace VRCNext;

public partial class MainForm
{
    /// <summary>Converts a local file path to an HttpListener URL.</summary>
    private string GetVirtualMediaUrl(string filePath)
    {
        // Check watch-folder routes first
        for (int i = 0; i < _settings.WatchFolders.Count; i++)
        {
            var folder = _settings.WatchFolders[i];
            if (!Directory.Exists(folder)) continue;
            if (filePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            {
                var rel = filePath.Substring(folder.Length).TrimStart('\\', '/').Replace('\\', '/');
                return $"http://localhost:{_httpPort}/media{i}/{Uri.EscapeDataString(rel)}";
            }
        }
        // Fallback: VRChat screenshot folder
        var vrcPhotoDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VRChat");
        if (filePath.StartsWith(vrcPhotoDir, StringComparison.OrdinalIgnoreCase))
        {
            var rel = filePath.Substring(vrcPhotoDir.Length).TrimStart('\\', '/').Replace('\\', '/');
            return $"http://localhost:{_httpPort}/vrcphotos/{Uri.EscapeDataString(rel)}";
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
                    if (_friendNameImg.TryGetValue(p.UserId, out var fi) && !string.IsNullOrEmpty(fi.image))
                        img = fi.image;
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
            var virtualUrl = $"http://localhost:{_httpPort}/media{e.FolderIndex}/{Uri.EscapeDataString(relPath).Replace("%2F", "/")}";

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
}
