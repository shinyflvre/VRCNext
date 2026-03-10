using Newtonsoft.Json.Linq;
using VRCNext.Services;

namespace VRCNext;

public partial class MainForm
{
    private Task VrcGetCurrentInstanceAsync() => Task.Run(async () =>
    {
        try
        {
            // Step 1: Location from log watcher — no API call. If VRChat not running, treat as offline.
            var loc = IsVrcRunning() ? _logWatcher.CurrentLocation : null;
            if (string.IsNullOrEmpty(loc) || loc == "offline" || loc == "private" || loc == "traveling")
            {
                Invoke(() => SendToJS("vrcCurrentInstance", new { empty = true }));
                return;
            }

            var parsed = VRChatApiService.ParseLocation(loc);

            // Step 2: Fetch instance details (for world info + n_users)
            var inst = await _vrcApi.GetInstanceAsync(loc);

            // Step 3: Get world info
            var worldName     = inst?["world"]?["name"]?.ToString() ?? "";
            var worldThumb    = inst?["world"]?["thumbnailImageUrl"]?.ToString() ?? "";
            var worldCapacity = inst?["world"]?["capacity"]?.Value<int>() ?? 0;

            if (string.IsNullOrEmpty(worldName) && !string.IsNullOrEmpty(parsed.worldId))
            {
                var world = await _vrcApi.GetWorldAsync(parsed.worldId);
                if (world != null)
                {
                    worldName     = world["name"]?.ToString() ?? "";
                    worldThumb    = world["thumbnailImageUrl"]?.ToString() ?? "";
                    worldCapacity = world["capacity"]?.Value<int>() ?? 0;
                }
            }
            if (string.IsNullOrEmpty(worldName)) worldName = parsed.worldId;

            // Step 4: Build player list. Prefer LogWatcher (reads VRChat logs),
            // fall back to API users array
            var users = new List<object>();
            string playerSource = "none";

            Invoke(() => SendToJS("log", new { msg = $"[LOG] {_logWatcher.GetDiagnostics()}", color = "sec" }));

            // Source A: VRChat log file (most complete, shows ALL players)
            var logPlayers = _logWatcher.GetCurrentPlayers();
            if (logPlayers.Count > 0)
            {
                playerSource = "logfile";

                var playersWithId = logPlayers.Where(p => !string.IsNullOrEmpty(p.UserId)).ToList();
                var userProfiles  = new Dictionary<string, JObject>();

                var needFetch = playersWithId.Where(p =>
                    !_friendNameImg.ContainsKey(p.UserId) &&
                    !_tlPlayerImageCache.ContainsKey(p.UserId)
                ).ToList();

                if (needFetch.Count > 0)
                {
                    var semaphore = new SemaphoreSlim(5);
                    var tasks = needFetch.Select(async p =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            var profile = await _vrcApi.GetUserAsync(p.UserId);
                            if (profile != null)
                            {
                                var img = VRChatApiService.GetUserImage(profile);
                                if (!string.IsNullOrEmpty(img))
                                    _tlPlayerImageCache[p.UserId] = img;
                                lock (userProfiles)
                                    userProfiles[p.UserId] = profile;
                            }
                        }
                        finally { semaphore.Release(); }
                    });
                    await Task.WhenAll(tasks);

                }

                Invoke(() => SendToJS("log", new { msg = $"[LOG] Profiles: {needFetch.Count} fetched, {playersWithId.Count - needFetch.Count} cached", color = "sec" }));

                foreach (var p in logPlayers)
                {
                    var img = "";
                    var status = "";
                    if (!string.IsNullOrEmpty(p.UserId))
                    {
                        if (userProfiles.TryGetValue(p.UserId, out var prof))
                        {
                            img    = VRChatApiService.GetUserImage(prof);
                            status = prof["status"]?.ToString() ?? "";
                        }
                        else if (_friendNameImg.TryGetValue(p.UserId, out var fi) && !string.IsNullOrEmpty(fi.image))
                        {
                            img = fi.image;
                        }
                        else if (_tlPlayerImageCache.TryGetValue(p.UserId, out var ci) && !string.IsNullOrEmpty(ci))
                        {
                            img = ci;
                        }
                    }
                    users.Add(new { id = p.UserId, displayName = p.DisplayName, image = img, status });
                }
            }

            // Source B: API users array (sometimes populated for public instances)
            if (users.Count == 0 && inst?["users"]?.Type == JTokenType.Array)
            {
                playerSource = "api";
                foreach (var u in inst["users"]!)
                {
                    users.Add(new {
                        id          = u["id"]?.ToString() ?? "",
                        displayName = u["displayName"]?.ToString() ?? "",
                        image       = VRChatApiService.GetUserImage((JObject)u),
                        status      = u["status"]?.ToString() ?? "",
                    });
                }
            }

            var nUsers = inst?["n_users"]?.Value<int>()
                ?? inst?["userCount"]?.Value<int>()
                ?? users.Count;
            if (worldCapacity == 0) worldCapacity = inst?["capacity"]?.Value<int>() ?? 0;

            _cachedInstLocation   = loc;
            _cachedInstWorldName  = worldName;
            _cachedInstWorldThumb = worldThumb;
            _cachedInstCapacity   = worldCapacity;
            _cachedInstType       = parsed.instanceType;

            Invoke(() =>
            {
                PushDiscordPresence();
                SendToJS("log", new { msg = $"Instance: {worldName} — {nUsers} total, {users.Count} tracked ({playerSource})", color = "ok" });
                SendToJS("vrcCurrentInstance", new {
                    location = loc, worldId = parsed.worldId,
                    worldName, worldThumb,
                    instanceType = parsed.instanceType,
                    nUsers, capacity = worldCapacity, users, playerSource,
                });
            });
        }
        catch (Exception ex)
        {
            Invoke(() =>
            {
                SendToJS("log", new { msg = $"❌ Instance error: {ex.Message}", color = "err" });
                SendToJS("vrcCurrentInstance", new { error = ex.Message });
            });
        }
    });

    /// <summary>
    /// Push an updated vrcCurrentInstance to JS using only cached REST data + live LogWatcher list.
    /// No REST calls — used for instant player join/leave updates.
    /// </summary>
    private void PushCurrentInstanceFromCache()
    {
        if (string.IsNullOrEmpty(_cachedInstLocation)) return;
        var parsed = VRChatApiService.ParseLocation(_cachedInstLocation);
        var logPlayers = _logWatcher.GetCurrentPlayers();
        var users = logPlayers.Select(p =>
        {
            string img;
            if (_friendNameImg.TryGetValue(p.UserId ?? "", out var fi) && !string.IsNullOrEmpty(fi.image))
                img = fi.image;
            else if (_tlPlayerImageCache.TryGetValue(p.UserId ?? "", out var ci) && !string.IsNullOrEmpty(ci))
                img = ci;
            else
                img = "";
            return (object)new { id = p.UserId, displayName = p.DisplayName, image = img, status = "" };
        }).ToList();

        SendToJS("vrcCurrentInstance", new {
            location     = _cachedInstLocation,
            worldId      = parsed.worldId,
            worldName    = _cachedInstWorldName,
            worldThumb   = _cachedInstWorldThumb,
            instanceType = _cachedInstType,
            nUsers       = logPlayers.Count,
            capacity     = _cachedInstCapacity,
            users,
            playerSource = "logfile",
        });
    }

    // Timeline - LogWatcher event handlers (run on UI thread)

    private void HandleWorldChangedOnUiThread(string worldId, string location)
    {
        // Finalise previous instance event
        if (_pendingInstanceEventId != null)
        {
            var finalPlayers = _cumulativeInstancePlayers.Select(kv => new TimelineService.PlayerSnap
            {
                UserId      = kv.Key,
                DisplayName = kv.Value.displayName,
                Image       = ResolvePlayerImage(kv.Key, kv.Value.image)
            }).ToList();
            var prevId = _pendingInstanceEventId;
            _timeline.UpdateEvent(prevId, ev => ev.Players = finalPlayers);
            var finalEv = _timeline.GetEvents().FirstOrDefault(e => e.Id == prevId);
            if (finalEv != null) SendToJS("timelineEvent", BuildTimelinePayload(finalEv));
        }

        _cumulativeInstancePlayers.Clear();
        _meetAgainThisInstance.Clear();
        _instanceSnapshotTimer?.Dispose();
        _instanceSnapshotTimer = null;

        // Create new instance_join timeline event (world name resolved asynchronously)
        var evId  = Guid.NewGuid().ToString("N")[..8];
        _pendingInstanceEventId = evId;

        var instEv = new TimelineService.TimelineEvent
        {
            Id        = evId,
            Type      = "instance_join",
            Timestamp = DateTime.UtcNow.ToString("o"),
            WorldId   = worldId,
            Location  = location
        };
        _timeline.AddEvent(instEv);
        SendToJS("timelineEvent", BuildTimelinePayload(instEv));
        SendToJS("log", new { msg = $"[TIMELINE] Instance join: {worldId}", color = "sec" });

        // Reset Discord join timer for the new instance
        _discordJoinedAt = DateTime.Now;

        // Track world visit immediately (log watcher fires on every actual world change)
        _worldTimeTracker.SetCurrentWorld(worldId);
        _lastTrackedWorldId = worldId;

        // Immediately refresh instance panel so sidebar doesn't wait for the 60s poll
        SendToJS("vrcWorldJoined", new { worldId });

        // After 15 s: snapshot players + resolve world name
        _instanceSnapshotTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                Invoke(async () =>
                {
                    try
                    {
                        // Refresh any images that have since been fetched (e.g. via requestInstanceInfo)
                        var snap = _cumulativeInstancePlayers.Select(kv => new TimelineService.PlayerSnap
                        {
                            UserId      = kv.Key,
                            DisplayName = kv.Value.displayName,
                            Image       = ResolvePlayerImage(kv.Key, kv.Value.image)
                        }).ToList();

                        string wName = "", wThumb = "";
                        if (worldId.StartsWith("wrld_") && _vrcApi.IsLoggedIn)
                        {
                            var world = await _vrcApi.GetWorldAsync(worldId);
                            if (world != null)
                            {
                                wName  = world["name"]?.ToString() ?? "";
                                wThumb = world["thumbnailImageUrl"]?.ToString() ?? "";
                            }
                        }

                        _timeline.UpdateEvent(evId, ev =>
                        {
                            ev.WorldName  = wName;
                            ev.WorldThumb = wThumb;
                            ev.Players    = snap;
                        });

                        // Store name/thumb in WorldTimeTracker so Time Spent tab can show it
                        // even for worlds that aren't in the timeline top-200
                        if (!string.IsNullOrEmpty(wName))
                            _worldTimeTracker.UpdateWorldInfo(worldId, wName, wThumb);

                        var updated = _timeline.GetEvents().FirstOrDefault(e => e.Id == evId);
                        if (updated != null) SendToJS("timelineEvent", BuildTimelinePayload(updated));
                    }
                    catch { }
                });
            }
            catch { }
        }, null, 15_000, System.Threading.Timeout.Infinite);
    }

    private void HandlePlayerJoinedOnUiThread(string userId, string displayName)
    {
        // Skip events for the local player; VRChat logs OnPlayerJoined for self too
        if (!string.IsNullOrEmpty(_currentVrcUserId) && userId == _currentVrcUserId) return;

        // Accumulate into instance player history
        if (!string.IsNullOrEmpty(userId) && !_cumulativeInstancePlayers.ContainsKey(userId))
        {
            var img = _friendNameImg.TryGetValue(userId, out var fi) ? fi.image : "";
            _cumulativeInstancePlayers[userId] = (displayName, img);
            // Store name in UserTimeTracker so this player appears in Time Spent list
            // even when they are not a friend and not in the timeline top-200
            _timeTracker.UpdateUserInfo(userId, displayName, img);

            // Live-update the instance_join timeline event so the UI shows players immediately
            if (_pendingInstanceEventId != null)
            {
                var evId = _pendingInstanceEventId;
                var snap = _cumulativeInstancePlayers.Select(kv => new TimelineService.PlayerSnap
                {
                    UserId      = kv.Key,
                    DisplayName = kv.Value.displayName,
                    Image       = ResolvePlayerImage(kv.Key, kv.Value.image)
                }).ToList();
                _timeline.UpdateEvent(evId, ev => ev.Players = snap);
                var updated = _timeline.GetEvents().FirstOrDefault(e => e.Id == evId);
                if (updated != null) SendToJS("timelineEvent", BuildTimelinePayload(updated));
            }
        }

        // First-meet detection, only after known-users set is seeded
        if (!string.IsNullOrEmpty(userId) && _timeline.KnownUsersSeeded && !_timeline.IsKnownUser(userId))
        {
            _timeline.AddKnownUser(userId);
            var img = _friendNameImg.TryGetValue(userId, out var fi) ? fi.image : "";
            var meetEv = new TimelineService.TimelineEvent
            {
                Type      = "first_meet",
                Timestamp = DateTime.UtcNow.ToString("o"),
                UserId    = userId,
                UserName  = displayName,
                UserImage = img,
                WorldId   = _logWatcher.CurrentWorldId ?? "",
                Location  = _logWatcher.CurrentLocation ?? ""
            };
            _timeline.AddEvent(meetEv);
            SendToJS("timelineEvent", BuildTimelinePayload(meetEv));
            SendToJS("log", new { msg = $"[TIMELINE] First meet: {displayName}", color = "sec" });

            // If no image yet, fetch async and update the event
            if (string.IsNullOrEmpty(img) && _vrcApi.IsLoggedIn)
            {
                var evId = meetEv.Id;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var profile = await _vrcApi.GetUserAsync(userId);
                        if (profile == null) return;
                        var fetchedImg = VRChatApiService.GetUserImage(profile);
                        if (string.IsNullOrEmpty(fetchedImg)) return;
                        _timeline.UpdateEvent(evId, ev => ev.UserImage = fetchedImg);
                        var updated = _timeline.GetEvents().FirstOrDefault(e => e.Id == evId);
                        if (updated != null) Invoke(() => SendToJS("timelineEvent", BuildTimelinePayload(updated)));
                    }
                    catch { }
                });
            }
        }
        else if (!string.IsNullOrEmpty(userId))
        {
            _timeline.AddKnownUser(userId);

            // Meet Again: known user, not yet seen in this instance
            if (_timeline.KnownUsersSeeded && !_meetAgainThisInstance.Contains(userId))
            {
                _meetAgainThisInstance.Add(userId);
                var img = _friendNameImg.TryGetValue(userId, out var fi2) ? fi2.image : "";
                var meetAgainEv = new TimelineService.TimelineEvent
                {
                    Type      = "meet_again",
                    Timestamp = DateTime.UtcNow.ToString("o"),
                    UserId    = userId,
                    UserName  = displayName,
                    UserImage = img,
                    WorldId   = _logWatcher.CurrentWorldId ?? "",
                    Location  = _logWatcher.CurrentLocation ?? ""
                };
                _timeline.AddEvent(meetAgainEv);
                SendToJS("timelineEvent", BuildTimelinePayload(meetAgainEv));

                // Async-fetch image if missing
                if (string.IsNullOrEmpty(img) && _vrcApi.IsLoggedIn)
                {
                    var maEvId = meetAgainEv.Id;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var profile = await _vrcApi.GetUserAsync(userId);
                            if (profile == null) return;
                            var fetchedImg = VRChatApiService.GetUserImage(profile);
                            if (string.IsNullOrEmpty(fetchedImg)) return;
                            _timeline.UpdateEvent(maEvId, ev => ev.UserImage = fetchedImg);
                            var updated = _timeline.GetEvents().FirstOrDefault(e => e.Id == maEvId);
                            if (updated != null) Invoke(() => SendToJS("timelineEvent", BuildTimelinePayload(updated)));
                        }
                        catch { }
                    });
                }
            }
        }

        // Instantly push updated player list to JS (no REST call needed)
        PushCurrentInstanceFromCache();
    }

    // Timeline - helpers

    private string FixLocalUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("http://localhost:")) return url;
        var slash = url.IndexOf('/', "http://localhost:".Length);
        return slash < 0 ? url : $"http://localhost:{_httpPort}{url[slash..]}";
    }

    /// <summary>
    /// If url is an original CDN URL → serve via imgcache (cached locally or original as fallback).
    /// If url is already a localhost URL (legacy stored) → fix the port.
    /// </summary>
    private string ResolveAndCache(string url, bool longTtl = false)
    {
        if (string.IsNullOrEmpty(url)) return url;
        // Old record stored as localhost URL → just fix the port, file is already on disk
        if (url.StartsWith("http://localhost:")) return FixLocalUrl(url);
        // New record stored as original CDN URL → serve via imgcache (downloads if needed, falls back to CDN URL)
        if (_imgCache == null) return url;
        return longTtl ? _imgCache.GetWorld(url) : _imgCache.Get(url);
    }

    private object BuildTimelinePayload(TimelineService.TimelineEvent ev) => new
    {
        id          = ev.Id,
        type        = ev.Type,
        timestamp   = ev.Timestamp,
        worldId     = ev.WorldId,
        worldName   = ev.WorldName,
        worldThumb  = ResolveAndCache(ev.WorldThumb, longTtl: true),
        location    = ev.Location,
        players     = ev.Players.Select(p => new { userId = p.UserId, displayName = p.DisplayName, image = ResolveAndCache(ResolvePlayerImage(p.UserId, p.Image)) }).ToList(),
        photoPath   = ev.PhotoPath,
        photoUrl    = !string.IsNullOrEmpty(ev.PhotoPath) ? GetVirtualMediaUrl(ev.PhotoPath) : FixLocalUrl(ev.PhotoUrl),
        userId      = ev.UserId,
        userName    = ev.UserName,
        userImage   = ResolveAndCache(ResolvePlayerImage(ev.UserId, ev.UserImage)),
        meetCount   = ev.Type == "meet_again" ? _timeline.GetMeetAgainCount(ev.UserId) : 0,
        notifId     = ev.NotifId,
        notifType   = ev.NotifType,
        notifTitle  = ev.NotifTitle,
        senderName  = ev.SenderName,
        senderId    = ev.SenderId,
        senderImage = ResolveAndCache(ResolvePlayerImage(ev.SenderId, ev.SenderImage)),
        message     = ev.Message,
    };

    private object BuildFriendTimelinePayload(TimelineService.FriendTimelineEvent ev) => new
    {
        id          = ev.Id,
        type        = ev.Type,
        timestamp   = ev.Timestamp,
        friendId    = ev.FriendId,
        friendName  = ev.FriendName,
        friendImage = ResolveAndCache(ResolvePlayerImage(ev.FriendId, ev.FriendImage)),
        worldId     = ev.WorldId,
        worldName   = ev.WorldName,
        worldThumb  = ResolveAndCache(ev.WorldThumb, longTtl: true),
        location    = ev.Location,
        oldValue    = ev.OldValue,
        newValue    = ev.NewValue,
    };

    /// <summary>
    /// Returns the best available profile image for a user: prefers the live friend/player
    /// image caches (which use the corrected GetUserImage chain) over anything stored in the DB.
    /// </summary>
    private string ResolvePlayerImage(string? userId, string? storedImage)
    {
        if (!string.IsNullOrEmpty(userId))
        {
            if (_friendNameImg.TryGetValue(userId, out var fi) && !string.IsNullOrEmpty(fi.image))
                return fi.image;
            if (_tlPlayerImageCache.TryGetValue(userId, out var ci) && !string.IsNullOrEmpty(ci))
                return ci;
        }
        return storedImage ?? "";
    }
}
