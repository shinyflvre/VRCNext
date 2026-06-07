using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using VRCNext.Services;
using VRCNext.Services.Helpers;

namespace VRCNext;

public class TimelineController
{
    private readonly CoreLibrary _core;
    private readonly FriendsController _friends;
    private readonly InstanceController _instance;
    private readonly PhotosController _photos;

    // VRCX import — path retained between preview and start
    private string _vrcxImportPath = "";

    // Cancellation tokens for in-flight enrichment
    private CancellationTokenSource _tlFetchCts  = new();
    private CancellationTokenSource _ftlFetchCts = new();

    public TimelineController(
        CoreLibrary core,
        FriendsController friends,
        InstanceController instance,
        PhotosController photos)
    {
        _core     = core;
        _friends  = friends;
        _instance = instance;
        _photos   = photos;
    }

    // Message Dispatch

    public async Task HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "importVrcxSelect":
                _ = Task.Run(() => SelectAndPreview());
                break;

            case "importVrcxStart":
                if (!string.IsNullOrEmpty(_vrcxImportPath))
                    _ = Task.Run(() => ImportAsync(_vrcxImportPath));
                break;

            case "getTimeline":
                HandleGetTimeline(msg);
                break;

            case "getTimelinePage":
                HandleGetTimelinePage(msg);
                break;

            case "searchTimeline":
                HandleSearchTimeline(msg);
                break;

            case "searchFriendTimeline":
                HandleSearchFriendTimeline(msg);
                break;

            case "getFriendTimeline":
                HandleGetFriendTimeline(msg);
                break;

            case "getFriendTimelinePage":
                HandleGetFriendTimelinePage(msg);
                break;

            case "getFtAlsoWasHere":
                HandleGetFtAlsoWasHere(msg);
                break;

            case "getTimelineByDate":
                HandleGetTimelineByDate(msg);
                break;

            case "getFriendTimelineByDate":
                HandleGetFriendTimelineByDate(msg);
                break;

            case "getTimelineForUser":
                HandleGetTimelineForUser(msg);
                break;

            case "getTimelineMonthActivity":
                HandleGetTimelineMonthActivity(msg);
                break;
        }
    }

    // getTimeline

    private void HandleGetTimeline(JObject msg)
    {
        _tlFetchCts.Cancel();
        _tlFetchCts = new CancellationTokenSource();
        var tlCt = _tlFetchCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                // Import any existing photos from PhotoPlayersStore not yet in timeline
                await _photos.BootstrapPhotoTimeline();
                if (tlCt.IsCancellationRequested) return;

                var tlTypeFilter = msg["type"]?.ToString() ?? "";

                // 0) Backfill missing world names from TimeEngine DB cache (entire DB, no API calls)
                var allEvents = _core.Timeline.GetEvents();
                foreach (var ev in allEvents.Where(e => !string.IsNullOrEmpty(e.WorldId) && string.IsNullOrEmpty(e.WorldName)))
                {
                    if (_core.TimeEngine.Worlds.TryGetValue(ev.WorldId, out var wRec) && !string.IsNullOrEmpty(wRec.WorldName))
                    {
                        _core.Timeline.UpdateEvent(ev.Id, e =>
                        {
                            e.WorldName = wRec.WorldName;
                            if (string.IsNullOrEmpty(e.WorldThumb)) e.WorldThumb = wRec.WorldThumb;
                        });
                    }
                }

                var (events, hasMore) = _core.Timeline.GetEventsPaged(100, 0, tlTypeFilter);
                var total   = _core.Timeline.GetEventCount(tlTypeFilter);
                var payload = events.Select(e => _instance.BuildTimelinePayload(e)).ToList();
                _core.SendToJS("timelineData", new { events = payload, hasMore, offset = 0, total, type = tlTypeFilter });

                if (!_core.VrcApi.IsLoggedIn || tlCt.IsCancellationRequested) return;
                await EnrichTimelineEventsAsync(events, hasMore, offset: 0, (int)total, tlTypeFilter, date: null, tlCt);
            }
            catch (Exception ex)
            {
                _core.SendToJS("log", new { msg = $"[TIMELINE] Load error: {ex.Message}", color = "err" });
            }
        });
    }

    // getTimelinePage

    private void HandleGetTimelinePage(JObject msg)
    {
        _tlFetchCts.Cancel();
        _tlFetchCts = new CancellationTokenSource();
        _ = Task.Run(() =>
        {
            try
            {
                var pageOffset   = msg["offset"]?.Value<int>() ?? 0;
                var tlTypeFilter = msg["type"]?.ToString() ?? "";
                var (events, hasMore) = _core.Timeline.GetEventsPaged(100, pageOffset, tlTypeFilter);
                var total   = _core.Timeline.GetEventCount(tlTypeFilter);
                var payload = events.Select(e => _instance.BuildTimelinePayload(e)).ToList();
                _core.SendToJS("timelineData", new { events = payload, hasMore, offset = pageOffset, total, type = tlTypeFilter });
            }
            catch { }
        });
    }

    // searchTimeline

    private void HandleSearchTimeline(JObject msg)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var srchQuery  = msg["query"]?.ToString() ?? "";
                var srchDate   = msg["date"]?.ToString() ?? "";
                var srchOffset = msg["offset"]?.Value<int>() ?? 0;
                var srchType   = msg["type"]?.ToString() ?? "";
                var (events, _) = _core.Timeline.SearchEvents(srchQuery, srchType, srchDate, srchOffset);
                var total   = _core.Timeline.SearchEventsCount(srchQuery, srchType, srchDate);
                var payload = events.Select(e => _instance.BuildTimelinePayload(e)).ToList();
                _core.SendToJS("timelineSearchResults", new { events = payload, query = srchQuery, date = srchDate, total, offset = srchOffset });
            }
            catch { }
        });
    }

    // searchFriendTimeline

    private void HandleSearchFriendTimeline(JObject msg)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var srchQuery  = msg["query"]?.ToString() ?? "";
                var srchDate   = msg["date"]?.ToString() ?? "";
                var srchOffset = msg["offset"]?.Value<int>() ?? 0;
                var srchType   = msg["type"]?.ToString() ?? "";
                var (events, _) = _core.Timeline.SearchFriendEvents(srchQuery, srchDate, srchOffset, srchType);
                var total   = _core.Timeline.SearchFriendEventsCount(srchQuery, srchDate, srchType);
                var payload = events.Select(e => _friends.BuildFriendTimelinePayload(e)).ToList();
                _core.SendToJS("friendTimelineSearchResults", new { events = payload, query = srchQuery, date = srchDate, total, offset = srchOffset });
            }
            catch { }
        });
    }

    // getFriendTimeline

    private void HandleGetFriendTimeline(JObject msg)
    {
        _ftlFetchCts.Cancel();
        _ftlFetchCts = new CancellationTokenSource();
        var ftlCt = _ftlFetchCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var typeFilter = msg["type"]?.ToString() ?? "";
                var (fevents, hasMore) = _core.Timeline.GetFriendEventsPaged(100, 0, typeFilter);
                var ftTotal  = _core.Timeline.GetFriendEventCount(typeFilter);
                var fpayload = fevents.Select(e => _friends.BuildFriendTimelinePayload(e)).ToList();
                _core.SendToJS("friendTimelineData", new { events = fpayload, hasMore, offset = 0, total = ftTotal, type = typeFilter });

                if (!_core.VrcApi.IsLoggedIn) return;
                if (ftlCt.IsCancellationRequested) return;

                var ftlCutoff    = DateTime.UtcNow - TimeSpan.FromDays(7);
                var recentFevents = fevents.Where(e =>
                    DateTime.TryParse(e.Timestamp, out var ts) && ts >= ftlCutoff).ToList();

                foreach (var ev in recentFevents.Where(e => !string.IsNullOrEmpty(e.WorldId) && !string.IsNullOrEmpty(e.WorldThumb)))
                    ImageCacheHelper.CacheWorldBackground(ev.WorldId, ev.WorldThumb);

                var unknownGpsWorlds = recentFevents
                    .Where(e => !string.IsNullOrEmpty(e.WorldId)
                             && string.IsNullOrEmpty(e.WorldThumb)
                             && ImageCacheHelper.GetWorldCached(e.WorldId) == null)
                    .Select(e => e.WorldId).Distinct().ToList();

                bool anyFevResolved = false;
                foreach (var wid in unknownGpsWorlds)
                {
                    if (ftlCt.IsCancellationRequested) return;
                    try
                    {
                        var w = await _core.World.GetWorldAsync(wid);
                        if (w == null) continue;
                        var wName  = w["name"]?.ToString() ?? "";
                        var wThumb = w["imageUrl"]?.ToString() ?? "";
                        ImageCacheHelper.CacheWorldBackground(wid, wThumb);
                        if (!string.IsNullOrEmpty(wThumb))
                        {
                            foreach (var ev in fevents.Where(e => e.WorldId == wid))
                            {
                                _core.Timeline.UpdateFriendEventWorld(ev.Id, wName, wThumb);
                                ev.WorldName  = wName;
                                ev.WorldThumb = wThumb;
                                anyFevResolved = true;
                            }
                        }
                    }
                    catch { }
                }

                // Apply session-cached thumbs to all friend events
                foreach (var ev in fevents.Where(e => !string.IsNullOrEmpty(e.WorldId)))
                {
                }

                // Resolve missing friend images from cache, then API
                var missingFriendIds = recentFevents
                    .Where(e => !string.IsNullOrEmpty(e.FriendId) && string.IsNullOrEmpty(e.FriendImage))
                    .Select(e => e.FriendId).Distinct().ToList();

                var fetchedFriendImgs = new Dictionary<string, string>();
                foreach (var fid in missingFriendIds)
                {
                    var disk = ImageCacheHelper.GetUserCached(fid);
                    if (disk != null) { fetchedFriendImgs[fid] = ImageCacheHelper.ToLocalUrl(disk); continue; }
                    if (_friends.TryGetNameImage(fid, out var fi) && !string.IsNullOrEmpty(fi.image))
                        fetchedFriendImgs[fid] = fi.image;
                }

                var needApiImg = missingFriendIds.Where(fid => !fetchedFriendImgs.ContainsKey(fid)).Take(20).ToList();
                if (needApiImg.Count > 0)
                {
                    var semFi = new SemaphoreSlim(3);
                    var fiTasks = needApiImg.Select(async fid =>
                    {
                        await semFi.WaitAsync();
                        try
                        {
                            if (ftlCt.IsCancellationRequested) return;
                            var diskFi = ImageCacheHelper.GetUserCached(fid);
                            if (diskFi != null) { fetchedFriendImgs[fid] = ImageCacheHelper.ToLocalUrl(diskFi); return; }
                            var profile = await _core.Users.GetUserAsync(fid);
                            if (profile != null)
                            {
                                var img = VRChatApiService.GetUserImage(profile);
                                if (!string.IsNullOrEmpty(img))
                                    fetchedFriendImgs[fid] = ImageCacheHelper.GetUserUrl(fid, img);
                            }
                            await Task.Delay(250);
                        }
                        finally { semFi.Release(); }
                    });
                    await Task.WhenAll(fiTasks);
                }

                foreach (var (fid, img) in fetchedFriendImgs)
                    foreach (var ev in fevents.Where(e => e.FriendId == fid && string.IsNullOrEmpty(e.FriendImage)))
                    {
                        _core.Timeline.UpdateFriendEventImage(ev.Id, img);
                        ev.FriendImage = img;
                        anyFevResolved = true;
                    }

                if (anyFevResolved)
                {
                    var updated = fevents.Select(e => _friends.BuildFriendTimelinePayload(e)).ToList();
                    _core.SendToJS("friendTimelineData", new { events = updated, hasMore, offset = 0, total = ftTotal, type = typeFilter });
                }
            }
            catch (Exception ex)
            {
                _core.SendToJS("log", new { msg = $"[FRIEND TIMELINE] Load error: {ex.Message}", color = "err" });
            }
        });
    }

    // getFriendTimelinePage

    private void HandleGetFriendTimelinePage(JObject msg)
    {
        _ftlFetchCts.Cancel();
        _ftlFetchCts = new CancellationTokenSource();
        _ = Task.Run(() =>
        {
            try
            {
                var pageOffset = msg["offset"]?.Value<int>() ?? 0;
                var typeFilter = msg["type"]?.ToString() ?? "";
                var (fevents, hasMore) = _core.Timeline.GetFriendEventsPaged(100, pageOffset, typeFilter);
                var ftTotal  = _core.Timeline.GetFriendEventCount(typeFilter);
                var fpayload = fevents.Select(e => _friends.BuildFriendTimelinePayload(e)).ToList();
                _core.SendToJS("friendTimelineData", new { events = fpayload, hasMore, offset = pageOffset, total = ftTotal, type = typeFilter });
            }
            catch { }
        });
    }

    // getFtAlsoWasHere

    private void HandleGetFtAlsoWasHere(JObject msg)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var location  = msg["location"]?.ToString() ?? "";
                var excludeId = msg["excludeId"]?.ToString() ?? "";
                var colocated = _core.Timeline.GetFriendGpsColocated(location, excludeId);
                var payload   = colocated.Select(e => new
                {
                    friendId    = e.FriendId,
                    friendName  = e.FriendName,
                    friendImage = _friends.ResolveWithDiskFallback(e.FriendId, e.FriendImage),
                }).ToList();
                _core.SendToJS("ftAlsoWasHere", new { excludeId, friends = payload });
            }
            catch { }
        });
    }

    // getTimelineByDate

    private void HandleGetTimelineByDate(JObject msg)
    {
        _tlFetchCts.Cancel();
        _tlFetchCts = new CancellationTokenSource();
        var tlCt = _tlFetchCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var dateStr    = msg["date"]?.ToString() ?? "";
                var typeFilter = msg["type"]?.ToString() ?? "";
                if (!DateTime.TryParse(dateStr, out var localDate)) return;
                localDate = DateTime.SpecifyKind(localDate, DateTimeKind.Local);
                var events = _core.Timeline.GetEventsByDate(localDate);
                if (!string.IsNullOrEmpty(typeFilter))
                    events = events.Where(e => e.Type == typeFilter).ToList();
                var total = events.Count;
                var payload = events.Select(e => _instance.BuildTimelinePayload(e)).ToList();
                _core.SendToJS("timelineData", new { events = payload, hasMore = false, offset = 0, total, type = typeFilter, date = dateStr });

                if (!_core.VrcApi.IsLoggedIn || tlCt.IsCancellationRequested) return;
                await EnrichTimelineEventsAsync(events, hasMore: false, offset: 0, total, typeFilter, dateStr, tlCt);
            }
            catch { }
        });
    }

    // Shared enrichment: resolves missing world thumbs + player/user images, then re-sends if anything changed.
    private async Task EnrichTimelineEventsAsync(
        List<TimelineService.TimelineEvent> events,
        bool hasMore, int offset, int total,
        string typeFilter, string? date,
        CancellationToken ct)
    {
        bool anyResolved = false;
        var enrichCutoff = DateTime.UtcNow - TimeSpan.FromDays(7);
        var recentEvents = events.Where(e =>
            DateTime.TryParse(e.Timestamp, out var ts) && ts >= enrichCutoff).ToList();

        foreach (var ev in recentEvents.Where(e => !string.IsNullOrEmpty(e.WorldId) && !string.IsNullOrEmpty(e.WorldThumb)))
            ImageCacheHelper.CacheWorldBackground(ev.WorldId, ev.WorldThumb);

        var unknownWorlds = recentEvents
            .Where(e => !string.IsNullOrEmpty(e.WorldId)
                     && string.IsNullOrEmpty(e.WorldThumb)
                     && ImageCacheHelper.GetWorldCached(e.WorldId) == null)
            .Select(e => e.WorldId).Distinct().ToList();

        foreach (var wid in unknownWorlds)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var w = await _core.World.GetWorldAsync(wid);
                if (w != null)
                {
                    var wName  = w["name"]?.ToString() ?? "";
                    var wThumb = w["imageUrl"]?.ToString() ?? "";
                    ImageCacheHelper.CacheWorldBackground(wid, wThumb);
                    if (!string.IsNullOrEmpty(wThumb))
                    {
                        _core.TimeEngine.UpdateWorldInfo(wid, wName, wThumb);
                        foreach (var ev in events.Where(e => e.WorldId == wid))
                        {
                            _core.Timeline.UpdateEvent(ev.Id, e => { e.WorldName = wName; e.WorldThumb = wThumb; });
                            ev.WorldName  = wName;
                            ev.WorldThumb = wThumb;
                            anyResolved = true;
                        }
                    }
                }
            }
            catch { }
        }

        // Apply session-cached thumbs to all events
        foreach (var ev in events.Where(e => !string.IsNullOrEmpty(e.WorldId)))
        {
        }

        // 2) Player / user images
        var fetchedImgs   = new Dictionary<string, string>();
        var playerRefs    = new List<(string evId, string userId)>();
        var userEventRefs = new List<(string evId, string userId)>();

        foreach (var ev in recentEvents)
        {
            if (ev.Type == "instance_join")
            {
                foreach (var p in ev.Players.Where(p => string.IsNullOrEmpty(p.Image) && !string.IsNullOrEmpty(p.UserId)))
                {
                    if (!fetchedImgs.ContainsKey(p.UserId)) fetchedImgs[p.UserId] = "";
                    playerRefs.Add((ev.Id, p.UserId));
                }
            }
            else if (ev.Type is "first_meet" or "meet_again")
            {
                if (string.IsNullOrEmpty(ev.UserImage) && !string.IsNullOrEmpty(ev.UserId))
                {
                    if (!fetchedImgs.ContainsKey(ev.UserId)) fetchedImgs[ev.UserId] = "";
                    userEventRefs.Add((ev.Id, ev.UserId));
                }
            }
        }

        var avatarEventRefs  = recentEvents
            .Where(e => e.Type == "avatar_switch" && string.IsNullOrEmpty(e.UserImage) && !string.IsNullOrEmpty(e.UserId))
            .Select(e => (evId: e.Id, avatarId: e.UserId))
            .ToList();
        if (avatarEventRefs.Count > 0)
        {
            var fetchedAvatarImgs = new Dictionary<string, string>();
            foreach (var aid in avatarEventRefs.Select(r => r.avatarId).Distinct())
                fetchedAvatarImgs[aid] = "";

            var avSem = new SemaphoreSlim(3);
            await Task.WhenAll(fetchedAvatarImgs.Keys.Select(async aid =>
            {
                await avSem.WaitAsync();
                try
                {
                    if (ct.IsCancellationRequested) return;
                    var av = await _core.Avatars.GetAvatarAsync(aid);
                    if (av != null)
                    {
                        var img = av["thumbnailImageUrl"]?.ToString() ?? av["imageUrl"]?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(img))
                        {
                            fetchedAvatarImgs[aid] = img;
                            ImageCacheHelper.CacheAvatarBackground(aid, img);
                        }
                    }
                    await Task.Delay(250);
                }
                finally { avSem.Release(); }
            }));

            foreach (var (evId, aid) in avatarEventRefs)
            {
                if (!fetchedAvatarImgs.TryGetValue(aid, out var img) || string.IsNullOrEmpty(img)) continue;
                var localImg = img;
                _core.Timeline.UpdateEvent(evId, ev => { if (string.IsNullOrEmpty(ev.UserImage)) ev.UserImage = localImg; });
                var localEv = events.FirstOrDefault(e => e.Id == evId);
                if (localEv != null && string.IsNullOrEmpty(localEv.UserImage)) localEv.UserImage = img;
                anyResolved = true;
            }
        }

        if (fetchedImgs.Count > 0)
        {
            var toFetch  = fetchedImgs.Keys.Take(60).ToList();
            var sem      = new SemaphoreSlim(3);
            await Task.WhenAll(toFetch.Select(async uid =>
            {
                await sem.WaitAsync();
                try
                {
                    if (ct.IsCancellationRequested) return;
                    var diskU = ImageCacheHelper.GetUserCached(uid);
                    if (diskU != null) { fetchedImgs[uid] = ImageCacheHelper.ToLocalUrl(diskU); return; }
                    if (_friends.TryGetNameImage(uid, out var fi) && !string.IsNullOrEmpty(fi.image))
                    { fetchedImgs[uid] = fi.image; return; }
                    var profile = await _core.Users.GetUserAsync(uid);
                    if (profile != null)
                    {
                        var img = VRChatApiService.GetUserImage(profile);
                        if (!string.IsNullOrEmpty(img))
                        { fetchedImgs[uid] = ImageCacheHelper.GetUserUrl(uid, img); }
                    }
                    await Task.Delay(250);
                }
                finally { sem.Release(); }
            }));

            foreach (var (evId, uid) in playerRefs)
            {
                if (!fetchedImgs.TryGetValue(uid, out var img) || string.IsNullOrEmpty(img)) continue;
                var localImg = img; var localUid = uid;
                _core.Timeline.UpdateEvent(evId, ev =>
                {
                    var p = ev.Players.FirstOrDefault(x => x.UserId == localUid);
                    if (p != null && string.IsNullOrEmpty(p.Image)) p.Image = localImg;
                });
                var localEv = events.FirstOrDefault(e => e.Id == evId);
                if (localEv != null) { var p = localEv.Players.FirstOrDefault(x => x.UserId == uid); if (p != null && string.IsNullOrEmpty(p.Image)) p.Image = img; }
                anyResolved = true;
            }
            foreach (var (evId, uid) in userEventRefs)
            {
                if (!fetchedImgs.TryGetValue(uid, out var img) || string.IsNullOrEmpty(img)) continue;
                var localImg = img;
                _core.Timeline.UpdateEvent(evId, ev => { if (string.IsNullOrEmpty(ev.UserImage)) ev.UserImage = localImg; });
                var localEv = events.FirstOrDefault(e => e.Id == evId);
                if (localEv != null && string.IsNullOrEmpty(localEv.UserImage)) localEv.UserImage = img;
                anyResolved = true;
            }
        }

        if (anyResolved && !ct.IsCancellationRequested)
        {
            var updated = events.Select(e => _instance.BuildTimelinePayload(e)).ToList();
            if (date != null)
                _core.SendToJS("timelineData", new { events = updated, hasMore, offset, total, type = typeFilter, date });
            else
                _core.SendToJS("timelineData", new { events = updated, hasMore, offset, total, type = typeFilter });
        }
    }

    // getFriendTimelineByDate

    private void HandleGetFriendTimelineByDate(JObject msg)
    {
        _ftlFetchCts.Cancel();
        _ftlFetchCts = new CancellationTokenSource();
        var ftlCt = _ftlFetchCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var dateStr    = msg["date"]?.ToString() ?? "";
                var typeFilter = msg["type"]?.ToString() ?? "";
                if (!DateTime.TryParse(dateStr, out var localDate)) return;
                localDate = DateTime.SpecifyKind(localDate, DateTimeKind.Local);
                var fevents   = _core.Timeline.GetFriendEventsByDate(localDate, typeFilter);
                var ftdTotal  = fevents.Count;
                var fpayload  = fevents.Select(e => _friends.BuildFriendTimelinePayload(e)).ToList();
                _core.SendToJS("friendTimelineData", new { events = fpayload, hasMore = false, offset = 0, total = ftdTotal, type = typeFilter, date = dateStr });

                if (!_core.VrcApi.IsLoggedIn || ftlCt.IsCancellationRequested) return;

                foreach (var ev in fevents.Where(e => !string.IsNullOrEmpty(e.WorldId) && !string.IsNullOrEmpty(e.WorldThumb)))
                    ImageCacheHelper.CacheWorldBackground(ev.WorldId, ev.WorldThumb);

                var unknownWorlds = fevents
                    .Where(e => !string.IsNullOrEmpty(e.WorldId)
                             && string.IsNullOrEmpty(e.WorldThumb)
                             && ImageCacheHelper.GetWorldCached(e.WorldId) == null)
                    .Select(e => e.WorldId).Distinct().ToList();

                bool anyFtdResolved = false;
                foreach (var wid in unknownWorlds)
                {
                    if (ftlCt.IsCancellationRequested) return;
                    try
                    {
                        var w = await _core.World.GetWorldAsync(wid);
                        if (w == null) continue;
                        var wName  = w["name"]?.ToString() ?? "";
                        var wThumb = w["imageUrl"]?.ToString() ?? "";
                        ImageCacheHelper.CacheWorldBackground(wid, wThumb);
                        if (!string.IsNullOrEmpty(wThumb))
                        {
                            foreach (var ev in fevents.Where(e => e.WorldId == wid))
                            {
                                _core.Timeline.UpdateFriendEventWorld(ev.Id, wName, wThumb);
                                ev.WorldName  = wName;
                                ev.WorldThumb = wThumb;
                                anyFtdResolved = true;
                            }
                        }
                    }
                    catch { }
                }


                // Enrich missing friend images
                var missingFriendIds = fevents
                    .Where(e => !string.IsNullOrEmpty(e.FriendId) && string.IsNullOrEmpty(e.FriendImage))
                    .Select(e => e.FriendId).Distinct().Take(20).ToList();

                foreach (var fid in missingFriendIds)
                {
                    if (ftlCt.IsCancellationRequested) break;
                    string img = "";
                    if (_friends.TryGetNameImage(fid, out var fi) && !string.IsNullOrEmpty(fi.image))
                        img = ImageCacheHelper.GetUserUrl(fid, fi.image);
                    if (!string.IsNullOrEmpty(img))
                        foreach (var ev in fevents.Where(e => e.FriendId == fid && string.IsNullOrEmpty(e.FriendImage)))
                        { ev.FriendImage = img; anyFtdResolved = true; }
                }

                if (anyFtdResolved && !ftlCt.IsCancellationRequested)
                {
                    var updated = fevents.Select(e => _friends.BuildFriendTimelinePayload(e)).ToList();
                    _core.SendToJS("friendTimelineData", new { events = updated, hasMore = false, offset = 0, total = ftdTotal, type = typeFilter, date = dateStr });
                }
            }
            catch { }
        });
    }

    // VRCX Import

    private void SelectAndPreview()
    {
        var r = FilePicker.FileOpen("sqlite3,db");
        if (!r.IsOk) { _core.SendToJS("vrcxSelectCancelled", null); return; }
        _vrcxImportPath = r.Path;

        try
        {
            using var vrcx = new SqliteConnection($"Data Source={_vrcxImportPath};Mode=ReadOnly");
            vrcx.Open();
            using var cmd = vrcx.CreateCommand();

            long Count(string sql) { cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L); }

            var worlds      = Count("SELECT COUNT(DISTINCT world_id) FROM gamelog_location WHERE world_id != ''");
            var locations   = Count("SELECT COUNT(*) FROM gamelog_location WHERE world_id != ''");
            var friendTimes = Count("SELECT COUNT(DISTINCT user_id) FROM gamelog_join_leave WHERE type='OnPlayerLeft' AND user_id != '' AND time > 0");

            long feedCount(string suffix)
            {
                long total = 0;
                cmd.CommandText = $"SELECT name FROM sqlite_master WHERE name LIKE '%{suffix}' AND type='table'";
                var tables = new List<string>();
                using (var tr = cmd.ExecuteReader()) while (tr.Read()) tables.Add(tr.GetString(0));
                foreach (var t in tables)
                {
                    cmd.CommandText = $"SELECT COUNT(*) FROM \"{t}\"";
                    total += Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
                }
                return total;
            }

            var gps         = feedCount("_feed_gps");
            var onlineOf    = feedCount("_feed_online_offline");
            var statuses    = feedCount("_feed_status");
            var bios        = feedCount("_feed_bio");

            _core.SendToJS("vrcxPreview", new
            {
                path        = Path.GetFileName(_vrcxImportPath),
                worlds,
                locations,
                friendTimes,
                gps,
                onlineOffline = onlineOf,
                statuses,
                bios,
            });
        }
        catch (Exception ex)
        {
            _core.SendToJS("vrcxImportError", new { error = ex.Message });
        }
    }

    // Merges VRCX world/friend time, timeline joins, and friend events into VRCNext
    private void ImportAsync(string vrcxPath)
    {
        try
        {
            _core.SendToJS("vrcxImportProgress", new { status = "Reading database...", percent = 10 });

            var worldMerge   = new List<(string worldId, string worldName, long seconds, int visits, string lastVisited)>();
            var friendMerge  = new List<(string userId, string displayName, long seconds, string lastSeen)>();
            var tlEvents     = new List<TimelineService.TimelineEvent>();
            var friendEvents = new List<TimelineService.FriendTimelineEvent>();

            using var vrcx = new SqliteConnection($"Data Source={vrcxPath};Mode=ReadOnly");
            vrcx.Open();

            using var cmd = vrcx.CreateCommand();

            // 1. World time
            cmd.CommandText = @"
                SELECT world_id, world_name, SUM(time)/1000, COUNT(*), MAX(created_at)
                FROM gamelog_location
                WHERE world_id != '' AND time > 0
                GROUP BY world_id";
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    worldMerge.Add((r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt32(3), r.GetString(4)));

            _core.SendToJS("vrcxImportProgress", new { status = "Reading friend data...", percent = 25 });

            // 2. Friend time
            cmd.CommandText = @"
                SELECT user_id, display_name, SUM(time)/1000, MAX(created_at)
                FROM gamelog_join_leave
                WHERE type='OnPlayerLeft' AND user_id != '' AND time > 0
                GROUP BY user_id";
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    friendMerge.Add((r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3)));

            _core.SendToJS("vrcxImportProgress", new { status = "Reading timeline events...", percent = 40 });

            // 3a. Build location -> players map from gamelog_join_leave
            var locationPlayers = new Dictionary<string, List<TimelineService.PlayerSnap>>();
            cmd.CommandText = "SELECT DISTINCT user_id, display_name, location FROM gamelog_join_leave WHERE type='OnPlayerJoined' AND user_id != ''";
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var uid = r.GetString(0);
                    var dn  = r.GetString(1);
                    var loc = r.GetString(2);
                    if (!locationPlayers.TryGetValue(loc, out var list))
                        locationPlayers[loc] = list = new List<TimelineService.PlayerSnap>();
                    list.Add(new TimelineService.PlayerSnap { UserId = uid, DisplayName = dn });
                }

            // 3b. Timeline: instance_join from gamelog_location
            cmd.CommandText = "SELECT created_at, world_id, world_name, location FROM gamelog_location WHERE world_id != ''";
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var ts  = r.GetString(0);
                    var wid = r.GetString(1);
                    var wn  = r.GetString(2);
                    var loc = r.GetString(3);
                    tlEvents.Add(new TimelineService.TimelineEvent
                    {
                        Id        = "vrcx_loc_" + VrcxHash(ts + wid),
                        Type      = "instance_join",
                        Timestamp = ts,
                        WorldId   = wid,
                        WorldName = wn,
                        Location  = loc,
                        Players   = locationPlayers.TryGetValue(loc, out var pl) ? pl : new(),
                    });
                }

            _core.SendToJS("vrcxImportProgress", new { status = "Reading friend events...", percent = 55 });

            // 4. Friend events from all {userId}_feed_* tables
            var userPrefixes = new List<string>();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE name LIKE '%_feed_gps' AND type='table'";
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var tbl = r.GetString(0);
                    userPrefixes.Add(tbl[..tbl.IndexOf("_feed_gps", StringComparison.Ordinal)]);
                }

            foreach (var prefix in userPrefixes)
            {
                // GPS
                TryImportFeed(vrcx, $"{prefix}_feed_gps", r =>
                    new TimelineService.FriendTimelineEvent
                    {
                        Id         = "vrcx_gps_" + VrcxHash(prefix + r.GetInt64(0)),
                        Type       = "friend_gps",
                        Timestamp  = r.GetString(1),
                        FriendId   = r.GetString(2),
                        FriendName = r.GetString(3),
                        Location   = r.GetString(4),
                        WorldName  = r.GetString(5),
                        WorldId    = ExtractWorldId(r.GetString(4)),
                        OldValue   = r.GetString(6), // previous_location
                        NewValue   = r.GetString(4), // new location
                    }, friendEvents);

                // Online / Offline
                TryImportFeed(vrcx, $"{prefix}_feed_online_offline", r =>
                    new TimelineService.FriendTimelineEvent
                    {
                        Id         = "vrcx_oo_" + VrcxHash(prefix + r.GetInt64(0)),
                        Type       = r.GetString(4) == "Online" ? "friend_online" : "friend_offline",
                        Timestamp  = r.GetString(1),
                        FriendId   = r.GetString(2),
                        FriendName = r.GetString(3),
                        Location   = r.GetString(5),
                        WorldName  = r.GetString(6),
                    }, friendEvents);

                // Status — category change (friend_status) + text change (friend_statusdesc)
                try
                {
                    using var stCmd = vrcx.CreateCommand();
                    stCmd.CommandText = $"SELECT * FROM \"{prefix}_feed_status\"";
                    using var stR = stCmd.ExecuteReader();
                    while (stR.Read())
                    {
                        var rowId  = stR.GetInt64(0);
                        var ts     = stR.GetString(1);
                        var uid    = stR.GetString(2);
                        var dn     = stR.GetString(3);
                        var newSt  = stR.IsDBNull(4) ? "" : stR.GetString(4); // status category
                        var newTxt = stR.IsDBNull(5) ? "" : stR.GetString(5); // status_description
                        var oldSt  = stR.IsDBNull(6) ? "" : stR.GetString(6); // previous_status
                        var oldTxt = stR.IsDBNull(7) ? "" : stR.GetString(7); // previous_status_description

                        friendEvents.Add(new TimelineService.FriendTimelineEvent
                        {
                            Id         = "vrcx_st_"  + VrcxHash(prefix + rowId),
                            Type       = "friend_status",
                            Timestamp  = ts,
                            FriendId   = uid,
                            FriendName = dn,
                            OldValue   = oldSt,
                            NewValue   = newSt,
                        });

                        if (newTxt != oldTxt)
                            friendEvents.Add(new TimelineService.FriendTimelineEvent
                            {
                                Id         = "vrcx_sd_" + VrcxHash(prefix + rowId),
                                Type       = "friend_statusdesc",
                                Timestamp  = ts,
                                FriendId   = uid,
                                FriendName = dn,
                                OldValue   = oldTxt,
                                NewValue   = newTxt,
                            });
                    }
                }
                catch { /* table may not exist */ }

                // Bio
                TryImportFeed(vrcx, $"{prefix}_feed_bio", r =>
                    new TimelineService.FriendTimelineEvent
                    {
                        Id         = "vrcx_bio_" + VrcxHash(prefix + r.GetInt64(0)),
                        Type       = "friend_bio",
                        Timestamp  = r.GetString(1),
                        FriendId   = r.GetString(2),
                        FriendName = r.GetString(3),
                        NewValue   = r.GetString(4), // bio
                        OldValue   = r.GetString(5), // previous_bio
                    }, friendEvents);
            }

            _core.SendToJS("vrcxImportProgress", new { status = "Generating meet events...", percent = 65 });

            // 5. First meet / Meet again from gamelog_join_leave
            var meetEvents   = new List<TimelineService.TimelineEvent>();
            var knownIds     = _core.Timeline.GetKnownUserIds();
            var importSeen   = new HashSet<string>();   // new users discovered during import
            var instanceSeen = new HashSet<string>();   // uid|loc pairs for meet_again dedup

            // location -> (worldId, worldName) built from gamelog_location rows already in worldMerge
            var locWorldInfo = new Dictionary<string, (string wid, string wn)>();
            cmd.CommandText = "SELECT location, world_id, world_name FROM gamelog_location WHERE world_id != ''";
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    locWorldInfo[r.GetString(0)] = (r.GetString(1), r.GetString(2));

            cmd.CommandText = @"
                SELECT user_id, display_name, location, created_at
                FROM gamelog_join_leave
                WHERE type='OnPlayerJoined' AND user_id != ''
                ORDER BY created_at";
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var uid = r.GetString(0);
                    var dn  = r.GetString(1);
                    var loc = r.GetString(2);
                    var ts  = r.GetString(3);
                    var (wid, wn) = locWorldInfo.TryGetValue(loc, out var wi) ? wi : (ExtractWorldId(loc), "");

                    var isKnown = knownIds.Contains(uid) || importSeen.Contains(uid);
                    if (!isKnown)
                    {
                        meetEvents.Add(new TimelineService.TimelineEvent
                        {
                            Id        = "vrcx_fm_" + VrcxHash(uid),
                            Type      = "first_meet",
                            Timestamp = ts,
                            UserId    = uid,
                            UserName  = dn,
                            WorldId   = wid,
                            WorldName = wn,
                            Location  = loc,
                        });
                        importSeen.Add(uid);
                        knownIds.Add(uid);
                    }
                    else
                    {
                        var key = uid + "|" + loc;
                        if (!instanceSeen.Contains(key))
                        {
                            instanceSeen.Add(key);
                            meetEvents.Add(new TimelineService.TimelineEvent
                            {
                                Id        = "vrcx_ma_" + VrcxHash(uid + loc),
                                Type      = "meet_again",
                                Timestamp = ts,
                                UserId    = uid,
                                UserName  = dn,
                                WorldId   = wid,
                                WorldName = wn,
                                Location  = loc,
                            });
                        }
                    }
                }

            _core.SendToJS("vrcxImportProgress", new { status = "Merging into VRCNext...", percent = 75 });

            // 6. Merge into VRCNext
            _core.TimeEngine.BulkMergeWorlds(worldMerge);
            _core.TimeEngine.BulkMergeUsers(friendMerge);
            _core.SendToJS("vrcxImportProgress", new { status = "Saving timeline...", percent = 88 });
            _core.Timeline.BulkImportEvents(tlEvents);
            _core.Timeline.BulkImportEvents(meetEvents);
            _core.Timeline.BulkImportFriendEvents(friendEvents);
            if (importSeen.Count > 0) _core.Timeline.SeedKnownUsers(importSeen);

            _core.SendToJS("vrcxImportDone", new
            {
                worlds        = worldMerge.Count,
                friends       = friendMerge.Count,
                timelineJoins = tlEvents.Count,
                friendEvents  = friendEvents.Count,
                meetEvents    = meetEvents.Count,
            });
        }
        catch (Exception ex)
        {
            _core.SendToJS("vrcxImportError", new { error = ex.Message });
        }
    }

    // Import Helpers

    private static void TryImportFeed(
        SqliteConnection vrcx,
        string tableName,
        Func<SqliteDataReader, TimelineService.FriendTimelineEvent> map,
        List<TimelineService.FriendTimelineEvent> target)
    {
        try
        {
            using var cmd = vrcx.CreateCommand();
            cmd.CommandText = $"SELECT * FROM \"{tableName}\"";
            using var r = cmd.ExecuteReader();
            while (r.Read()) target.Add(map(r));
        }
        catch { /* table may not exist */ }
    }

    private static string ExtractWorldId(string location)
    {
        if (string.IsNullOrEmpty(location)) return "";
        var colon = location.IndexOf(':');
        var id = colon > 0 ? location[..colon] : location;
        return id.StartsWith("wrld_") ? id : "";
    }

    private static string VrcxHash(object key)
        => Math.Abs(key?.GetHashCode() ?? 0).ToString("x8");

    // getTimelineForUser — mini-timeline in profile modal

    private void HandleGetTimelineForUser(JObject msg)
    {
        var userId = msg["userId"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(userId)) return;
        _ = Task.Run(() =>
        {
            var events  = _core.Timeline.GetEventsForUser(userId, 10);
            var payload = events.Select(e => _instance.BuildTimelinePayload(e)).ToList();
            _core.SendToJS("timelineForUser", new { userId, events = payload });
        });
    }

    // getTimelineMonthActivity — debug: verify payload delivery

    private void HandleGetTimelineMonthActivity(JObject msg)
    {
        var year  = msg["year"]?.Value<int>()  ?? 0;
        var month = msg["month"]?.Value<int>() ?? 0;
        if (year < 2000 || year > 2100 || month < 1 || month > 12)
        {
            _core.SendToJS("timelineMonthActivity", new { year, month, days = Array.Empty<object>() });
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                var (personal, friends) = _core.Timeline.GetMonthActivity(year, month);
                var allDates = personal.Keys.Union(friends.Keys).OrderBy(x => x).ToList();
                var days = allDates.Select(d => new
                {
                    date     = d,
                    personal = personal.TryGetValue(d, out var p) ? p : 0,
                    friends  = friends.TryGetValue(d, out var f)  ? f : 0,
                }).ToList();
                _core.SendToJS("timelineMonthActivity", new { year, month, days });
            }
            catch { }
        });
    }

    // getFriendActivityForUser — user activity (friend_events) in profile modal

    internal void HandleGetFriendActivityForUser(JObject msg)
    {
        var userId = msg["userId"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(userId)) return;
        _ = Task.Run(() =>
        {
            var events  = _core.Timeline.GetFriendEventsForUser(userId, 10);
            var payload = events.Select(e => _friends.BuildFriendTimelinePayload(e)).ToList();
            _core.SendToJS("friendActivityForUser", new { userId, events = payload });
        });
    }
}
