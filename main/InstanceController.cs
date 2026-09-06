using Newtonsoft.Json.Linq;
using VRCNext.Services;
using VRCNext.Services.Helpers;

namespace VRCNext;

// Owns all instance-related state, logic, and message handling.

public class InstanceController
{
    private readonly CoreLibrary _core;
    private readonly FriendsController _friends;

    // Instance State
    private string _cachedInstLocation   = "";
    private string _cachedInstWorldName  = "";
    private string _cachedInstWorldThumb = "";
    private int    _cachedInstCapacity   = 0;
    private string _cachedInstType       = "";
    private string _cachedInstOwnerId    = "";
    private string _cachedInstOwnerName  = "";
    private string _cachedInstOwnerGroup = "";
    private string _cachedInstDisplayName = "";

    private readonly Dictionary<string, (string displayName, string image)> _cumulativeInstancePlayers = new();
    private readonly Dictionary<string, List<string>> _playerJoinTimes = new();
    private readonly Dictionary<string, List<string>> _playerLeftTimes = new();
    private readonly HashSet<string> _meetAgainThisInstance = new();
    private string? _pendingInstanceEventId;
    private System.Threading.Timer? _instanceSnapshotTimer;
    private System.Threading.Timer? _instAvatarBatchTimer;
    private System.Threading.Timer? _instAvatarBatchCycle;
    private string   _instAvatarBatchLoc = "";
    private DateTime _instAvatarBatchAt  = DateTime.MinValue;
    private const int InstAvatarSettleMs    = 10_000;
    private const int InstAvatarIntervalMin = 10;
    private bool _logWatcherBootstrapped;
    private string _lastTrackedWorldId = "";
    private readonly HashSet<string> _recentlyClosedLocs = new();

    // Public Accessors (for other domains)
    public string CachedInstLocation   => _cachedInstLocation;
    public string CachedInstWorldName  => _cachedInstWorldName;
    public string CachedInstWorldThumb => _cachedInstWorldThumb;
    public int    CachedInstCapacity   => _cachedInstCapacity;
    public string CachedInstType       => _cachedInstType;
    public Dictionary<string, (string displayName, string image)> CumulativeInstancePlayers => _cumulativeInstancePlayers;
    public Dictionary<string, List<string>> PlayerJoinTimes => _playerJoinTimes;
    public Dictionary<string, List<string>> PlayerLeftTimes => _playerLeftTimes;
    public HashSet<string> MeetAgainThisInstance => _meetAgainThisInstance;
    public string? PendingInstanceEventId { get => _pendingInstanceEventId; set => _pendingInstanceEventId = value; }
    public bool LogWatcherBootstrapped { get => _logWatcherBootstrapped; set => _logWatcherBootstrapped = value; }
    public string LastTrackedWorldId { get => _lastTrackedWorldId; set => _lastTrackedWorldId = value; }
    public HashSet<string> RecentlyClosedLocs => _recentlyClosedLocs;

    // Constructor

    public InstanceController(CoreLibrary core, FriendsController friends)
    {
        _core = core;
        _friends = friends;
        _core.LogWatcher.AvatarChanged += (_, _) => ScheduleWornAvatarPush();
        _instAvatarBatchCycle = new System.Threading.Timer(
            _ => _ = Task.Run(RunInstanceAvatarBatchAsync), null,
            TimeSpan.FromMinutes(InstAvatarIntervalMin), TimeSpan.FromMinutes(InstAvatarIntervalMin));
        _core.TimeEngine.OnVrcClosed = () =>
        {
            if (_pendingInstanceEventId == null) return;
            var id = _pendingInstanceEventId;
            _pendingInstanceEventId = null;
            var now = DateTime.UtcNow.ToString("o");
            var finalPlayers = _cumulativeInstancePlayers
                .Select(kv => BuildPlayerSnap(kv.Key, kv.Value.displayName, kv.Value.image, finalizeLeftAt: now))
                .ToList();
            _core.Timeline.UpdateEvent(id, ev => ev.Players = finalPlayers);
            _core.Timeline.SetInstanceEventLeftAt(id, now);
            var closed = _core.Timeline.GetEvents().FirstOrDefault(e => e.Id == id);
            if (closed != null) _core.SendToJS("timelineEvent", BuildTimelinePayload(closed));
        };
    }

    // finalizeLeftAt closes any still-open session with that timestamp.
    private TimelineService.PlayerSnap BuildPlayerSnap(string uid, string displayName, string image, string? finalizeLeftAt = null)
    {
        var joins = _playerJoinTimes.TryGetValue(uid, out var jt) ? new List<string>(jt) : new List<string>();
        var lefts = _playerLeftTimes.TryGetValue(uid, out var lt) ? new List<string>(lt) : new List<string>();
        if (finalizeLeftAt != null && lefts.Count < joins.Count)
            lefts.Add(finalizeLeftAt);
        return new TimelineService.PlayerSnap
        {
            UserId      = uid,
            DisplayName = displayName,
            Image       = ResolveWithDiskFallback(uid, image),
            JoinedAts   = joins,
            LeftAts     = lefts,
        };
    }

    // Static Helpers

    public static string ParseInstanceTypeFromLoc(string loc)
    {
        if (loc.Contains("~private(")) return loc.Contains("~canRequestInvite") ? "invite_plus" : "private";
        if (loc.Contains("~friends(")) return "friends";
        if (loc.Contains("~hidden("))  return "hidden";
        if (loc.Contains("~group("))
        {
            var gat = System.Text.RegularExpressions.Regex
                .Match(loc, @"groupAccessType\(([^)]+)\)").Groups[1].Value.ToLowerInvariant();
            if (gat == "public")  return "group-public";
            if (gat == "plus")    return "group-plus";
            return "group-members"; // covers "members" and unknown
        }
        return "public";
    }

    public static string ParseRegionFromLoc(string loc)
    {
        var m = System.Text.RegularExpressions.Regex.Match(loc, @"~region\(([^)]+)\)");
        return m.Success ? m.Groups[1].Value : "eu";
    }

    // Message Handler

    public async Task HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "vrcGetCurrentInstance":
                _ = GetCurrentInstanceAsync();
                break;

            case "vrcGetMyInstances":
                _ = Task.Run(async () =>
                {
                    var myId = _core.VrcApi.CurrentUserId ?? "";

                    // 1. Inject current location as candidate (LogWatcher is instant — same source as friends panel)
                    //    Ownership is validated by the loop below via API ownerId check.
                    //    This handles startup (already in-game) AND joining a new instance while VRCNext is running.
                    var logLoc = (_core.IsVrcRunning?.Invoke() == true) ? (_core.LogWatcher.CurrentLocation ?? "") : "";
                    if (string.IsNullOrEmpty(logLoc) || !logLoc.Contains(':')) logLoc = _cachedInstLocation;
                    if (!string.IsNullOrEmpty(logLoc) && logLoc.Contains(':')
                        && !_recentlyClosedLocs.Contains(logLoc)
                        && !_core.Settings.MyInstances.Contains(logLoc))
                    {
                        _core.Settings.MyInstances.Insert(0, logLoc);
                        // No save yet — loop validates ownership; removed via miDead if not owner
                    }

                    // 2. Verify all stored instances via API — remove dead ones, keep active
                    var miRaw = new List<(string loc, string worldId, string worldName, string worldThumb,
                        string instanceType, int userCount, int capacity, string region, string ownerId,
                        string authorId, string authorName)>();
                    var miDead = new List<string>();
                    foreach (var instLoc in _core.Settings.MyInstances.ToList())
                    {
                        var inst = await _core.Instances.GetInstanceAsync(instLoc);
                        var shortId = instLoc.Contains(':') ? instLoc.Split(':')[1].Split('~')[0] : instLoc;
                        if (inst == null)
                        {
                            _core.SendToJS("log", new { msg = $"INST: Instance {shortId} returned null - Removed", color = "err" });
                            miDead.Add(instLoc); continue;
                        }
                        var closedAt = inst["closedAt"]?.Type == Newtonsoft.Json.Linq.JTokenType.Null
                            ? null : inst["closedAt"]?.ToString();
                        if (!string.IsNullOrEmpty(closedAt))
                        {
                            _core.SendToJS("log", new { msg = $"INST: Instance {shortId} returned closed at {closedAt} - Removed", color = "err" });
                            miDead.Add(instLoc); continue;
                        }
                        var apiOwnerId = inst["ownerId"]?.ToString() ?? "";
                        var effectiveOwner = apiOwnerId.StartsWith("grp_")
                            ? (inst["creatorId"]?.ToString() ?? apiOwnerId)
                            : apiOwnerId;
                        if (!string.IsNullOrEmpty(myId) && effectiveOwner != myId) { miDead.Add(instLoc); continue; }
                        var iType = ParseInstanceTypeFromLoc(instLoc);
                        if (iType == "private" && inst["canRequestInvite"]?.Value<bool>() == true) iType = "invite_plus";
                        var userCount = inst["userCount"]?.Value<int>() ?? 0;
                        _core.SendToJS("log", new { msg = $"INST: Instance {shortId} returned {userCount} users", color = "ok" });
                        miRaw.Add((instLoc,
                            inst["worldId"]?.ToString() ?? "",
                            inst["world"]?["name"]?.ToString() ?? "",
                            ImageCacheHelper.GetWorldUrl(
                                inst["worldId"]?.ToString(),
                                inst["world"]?["imageUrl"]?.ToString() ?? inst["world"]?["thumbnailImageUrl"]?.ToString()),
                            iType,
                            userCount,
                            inst["capacity"]?.Value<int>() ?? 0,
                            inst["region"]?.ToString() ?? ParseRegionFromLoc(instLoc),
                            apiOwnerId,
                            inst["world"]?["authorId"]?.ToString()   ?? "",
                            inst["world"]?["authorName"]?.ToString() ?? ""));
                    }
                    foreach (var d in miDead) _core.Settings.MyInstances.Remove(d);
                    if (miDead.Count > 0) _core.Settings.Save();

                    // Resolve owner/group names (same pattern as world modal)
                    var miGroupIds = miRaw.Where(r => r.ownerId.StartsWith("grp_")).Select(r => r.ownerId).Distinct().ToList();
                    var miGroupMap = new Dictionary<string, (string name, string shortCode)>();
                    if (miGroupIds.Count > 0)
                    {
                        var gTasks = miGroupIds.ToDictionary(id => id, id => _core.Groups.GetGroupAsync(id));
                        try { await Task.WhenAll(gTasks.Values); } catch { }
                        foreach (var kv in gTasks)
                            if (!kv.Value.IsFaulted && kv.Value.Result != null)
                                miGroupMap[kv.Key] = (kv.Value.Result["name"]?.ToString() ?? "", kv.Value.Result["shortCode"]?.ToString() ?? "");
                    }
                    var miResults = miRaw.Select(r => {
                        var ownerName = "";
                        var ownerGroup = "";
                        if (r.ownerId.StartsWith("usr_"))
                            { var f = _friends.GetStoreValue(r.ownerId); ownerName = f?["displayName"]?.ToString() ?? ""; }
                        else if (r.ownerId.StartsWith("grp_") && miGroupMap.TryGetValue(r.ownerId, out var info))
                            (ownerName, ownerGroup) = info;
                        return (object)new {
                            location = r.loc, r.worldId, r.worldName, r.worldThumb,
                            r.instanceType, r.userCount, r.capacity, r.region,
                            r.ownerId, ownerName, ownerGroup,
                            r.authorId, r.authorName,
                        };
                    }).ToList();
                    _core.SendToJS("myInstances", miResults);
                });
                break;

            case "vrcGetInstanceDetail":
                _ = Task.Run(async () =>
                {
                    var detailLoc = msg["location"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(detailLoc)) return;
                    var inst = await _core.Instances.GetInstanceAsync(detailLoc);
                    if (inst == null)
                    {
                        _core.SendToJS("instanceDetail", new { error = true, location = detailLoc });
                        return;
                    }
                    var iType = ParseInstanceTypeFromLoc(detailLoc);
                    if (iType == "private" && inst["canRequestInvite"]?.Value<bool>() == true) iType = "invite_plus";
                    var worldName  = inst["world"]?["name"]?.ToString() ?? "";
                    var worldThumb = inst["world"]?["imageUrl"]?.ToString() ?? inst["world"]?["thumbnailImageUrl"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(worldName))
                    {
                        var parsedWid = inst["worldId"]?.ToString() ?? detailLoc.Split(':')[0];
                        if (!string.IsNullOrEmpty(parsedWid))
                        {
                            var world = await _core.World.GetWorldAsync(parsedWid);
                            if (world != null)
                            {
                                worldName  = world["name"]?.ToString() ?? "";
                                worldThumb = ImageCacheHelper.GetWorldUrl(parsedWid, world["imageUrl"]?.ToString());
                            }
                        }
                    }
                    var ownerId    = inst["ownerId"]?.ToString() ?? "";
                    var ownerName  = "";
                    var ownerGroup = "";
                    if (ownerId.StartsWith("grp_"))
                    {
                        var grp = await _core.Groups.GetGroupAsync(ownerId);
                        if (grp != null)
                        {
                            ownerName  = grp["name"]?.ToString() ?? "";
                            ownerGroup = grp["shortCode"]?.ToString() ?? "";
                        }
                    }
                    else if (ownerId.StartsWith("usr_"))
                    {
                        var f = _friends.GetStoreValue(ownerId);
                        ownerName = f?["displayName"]?.ToString() ?? "";
                        if (string.IsNullOrEmpty(ownerName))
                        {
                            var ownerUser = await _core.Users.GetUserAsync(ownerId);
                            ownerName = ownerUser?["displayName"]?.ToString() ?? "";
                        }
                    }
                    _core.SendToJS("instanceDetail", new
                    {
                        location     = inst["location"]?.ToString() ?? detailLoc,
                        worldId      = inst["worldId"]?.ToString() ?? detailLoc.Split(':')[0],
                        worldName,
                        worldThumb,
                        instanceType = iType,
                        userCount    = inst["userCount"]?.Value<int>() ?? 0,
                        capacity     = inst["capacity"]?.Value<int>() ?? 0,
                        ownerId,
                        ownerName,
                        ownerGroup,
                    });
                });
                break;

            case "vrcGetWorldInstancesDetail":
                _ = Task.Run(async () =>
                {
                    var wid  = msg["worldId"]?.ToString() ?? "";
                    var locs = msg["locations"]?.ToObject<List<string>>() ?? new List<string>();
                    if (locs.Count == 0) return;

                    // 1. SQLite cache hit → send world info immediately
                    var wdCached = _core.TimeEngine.GetWorldDetail(wid);
                    if (wdCached != null)
                    {
                        _core.SendToJS("worldInstancesDetail", new
                        {
                            worldId = wid,
                            world = new
                            {
                                name        = wdCached.WorldName,
                                thumb       = ImageCacheHelper.GetWorldUrl(wid, wdCached.WorldThumb),
                                description = wdCached.Description,
                                authorId    = wdCached.AuthorId,
                                authorName  = wdCached.AuthorName,
                                capacity    = wdCached.Capacity,
                                favorites   = wdCached.Favorites,
                                visits      = wdCached.Visits,
                            },
                            instances = new List<object>()
                        });
                    }

                    // 2. Always fetch fresh from API
                    var results       = new System.Collections.Concurrent.ConcurrentBag<object>();
                    var sem           = new SemaphoreSlim(3);
                    JObject? firstWorld = null;
                    var firstWorldLock  = new object();

                    await Task.WhenAll(locs.Select(async loc =>
                    {
                        await sem.WaitAsync();
                        try
                        {
                            var inst = await _core.Instances.GetInstanceAsync(loc);
                            if (inst == null) return;

                            if (inst["world"] is JObject wObj)
                                lock (firstWorldLock) { if (firstWorld == null) firstWorld = wObj; }

                            var iType = ParseInstanceTypeFromLoc(loc);
                            if (iType == "private" && inst["canRequestInvite"]?.Value<bool>() == true) iType = "invite_plus";

                            var apiOwnerId = inst["ownerId"]?.ToString() ?? "";
                            var ownerName  = "";
                            var ownerGroup = "";
                            if (apiOwnerId.StartsWith("grp_"))
                            {
                                var grp = await _core.Groups.GetGroupAsync(apiOwnerId);
                                if (grp != null) { ownerName = grp["name"]?.ToString() ?? ""; ownerGroup = grp["shortCode"]?.ToString() ?? ""; }
                            }
                            else if (apiOwnerId.StartsWith("usr_"))
                            {
                                var f = _friends.GetStoreValue(apiOwnerId);
                                ownerName = f?["displayName"]?.ToString() ?? "";
                            }

                            var fullLoc = inst["location"]?.ToString() ?? loc;
                            var pl      = inst["platforms"];
                            var cs      = inst["contentSettings"];

                            results.Add(new
                            {
                                location     = fullLoc,
                                instanceType = iType,
                                userCount    = inst["userCount"]?.Value<int>()  ?? 0,
                                capacity     = inst["capacity"]?.Value<int>()   ?? 0,
                                region       = inst["region"]?.ToString()       ?? ParseRegionFromLoc(loc),
                                queueEnabled = inst["queueEnabled"]?.Value<bool>() ?? false,
                                queueSize    = inst["queueSize"]?.Value<int>()  ?? 0,
                                displayName  = inst["displayName"]?.ToString() ?? "",
                                ageGate      = fullLoc.Contains("~ageGate"),
                                ownerId      = apiOwnerId,
                                ownerName,
                                ownerGroup,
                                authorId     = inst["world"]?["authorId"]?.ToString()   ?? "",
                                authorName   = inst["world"]?["authorName"]?.ToString() ?? "",
                                platforms = new
                                {
                                    pc      = pl?["standalonewindows"]?.Value<int>() ?? 0,
                                    android = pl?["android"]?.Value<int>()           ?? 0,
                                    ios     = pl?["ios"]?.Value<int>()               ?? 0,
                                },
                                contentSettings = new
                                {
                                    emoji     = cs?["emoji"]?.Value<bool>()     ?? true,
                                    drones    = cs?["drones"]?.Value<bool>()    ?? true,
                                    pedestals = cs?["pedestals"]?.Value<bool>() ?? true,
                                    props     = cs?["props"]?.Value<bool>()     ?? true,
                                    prints    = cs?["prints"]?.Value<bool>()    ?? true,
                                    stickers  = cs?["stickers"]?.Value<bool>()  ?? true,
                                },
                            });
                        }
                        catch (Exception ex)
                        {
                            _core.SendToJS("log", new { msg = $"GetWorldInstancesDetail ex: {ex.Message}", color = "err" });
                        }
                        finally { sem.Release(); }
                    }));

                    // 3. Save world detail and send final payload
                    object? worldPayload = null;
                    if (!string.IsNullOrEmpty(wid) && firstWorld != null)
                    {
                        var wName   = firstWorld["name"]?.ToString()                    ?? "";
                        var wThumb  = firstWorld["thumbnailImageUrl"]?.ToString()
                                   ?? firstWorld["imageUrl"]?.ToString()                ?? "";
                        var wImg    = firstWorld["imageUrl"]?.ToString()                ?? "";
                        var wDesc   = firstWorld["description"]?.ToString()             ?? "";
                        var wAuth   = firstWorld["authorName"]?.ToString()              ?? "";
                        var wAId    = firstWorld["authorId"]?.ToString()                ?? "";
                        var wPub    = firstWorld["created_at"]?.ToString()              ?? "";
                        var wUpd    = firstWorld["updated_at"]?.ToString()              ?? "";
                        var wCap    = firstWorld["capacity"]?.Value<int>()              ?? 0;
                        var wRCap   = firstWorld["recommendedCapacity"]?.Value<int>()   ?? 0;
                        var wTags   = firstWorld["tags"]?.ToObject<List<string>>()      ?? new List<string>();
                        var wFav    = firstWorld["favorites"]?.Value<int>()             ?? 0;
                        var wVis    = firstWorld["visits"]?.Value<int>()                ?? 0;
                        var wHeat   = firstWorld["heat"]?.Value<int>()                  ?? 0;
                        var wPop    = firstWorld["popularity"]?.Value<int>()            ?? 0;
                        var wPubOcc = firstWorld["publicOccupants"]?.Value<int>()       ?? 0;
                        var wPriOcc = firstWorld["privateOccupants"]?.Value<int>()      ?? 0;
                        var wVer    = firstWorld["version"]?.Value<int>()               ?? 0;

                        _core.TimeEngine.SaveWorldDetail(wid, wName, wThumb, wDesc, wImg, wAuth, wAId, wPub, wUpd,
                            wCap, wRCap, wTags, wFav, wVis, 0, 0, 0, wHeat, wPop, wPubOcc, wPriOcc, wVer);

                        worldPayload = new
                        {
                            name        = wName,
                            thumb       = ImageCacheHelper.GetWorldUrl(wid, wThumb),
                            description = wDesc,
                            authorId    = wAId,
                            authorName  = wAuth,
                            capacity    = wCap,
                            favorites   = wFav,
                            visits      = wVis,
                        };
                    }

                    if (results.Count > 0)
                        _core.SendToJS("worldInstancesDetail", new { worldId = wid, world = worldPayload, instances = results.ToList() });
                });
                break;

            case "vrcRemoveMyInstance":
                _ = Task.Run(async () =>
                {
                    var rmInstLoc = msg["location"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(rmInstLoc)) return;
                    // Close instance via VRChat API (DELETE)
                    await _core.Instances.CloseInstanceAsync(rmInstLoc);
                    _core.Settings.MyInstances.Remove(rmInstLoc);
                    _core.Settings.Save();
                });
                break;

            case "vrcSelfInvite":
            {
                var siLoc = msg["location"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(siLoc)) break;
                _ = Task.Run(async () =>
                {
                    var ok = await _core.Instances.InviteSelfAsync(siLoc);
                    Invoke(() => _core.SendToJS("vrcActionResult", new
                    {
                        action = "selfInvite",
                        success = ok,
                        message = ok ? "Self-invite sent! Check VRChat." : "Self-invite failed. The instance may no longer exist.",
                    }));
                });
                break;
            }

            case "vrcOpenInGame":
            {
                var oigLoc = msg["location"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(oigLoc)) break;
                _ = Task.Run(async () =>
                {
                    var url = VRChatApiService.BuildLaunchUri(oigLoc);
                    var iType = ParseInstanceTypeFromLoc(oigLoc);
                    if (iType != "public")
                    {
                        var shortName = await _core.Instances.GetInstanceShortNameAsync(oigLoc);
                        if (!string.IsNullOrEmpty(shortName)) url += $"&shortName={Uri.EscapeDataString(shortName)}";
                    }

                    var ok = await VrcLaunchPipe.SendAsync(url);
                    if (ok)
                    {
                        Invoke(() => _core.SendToJS("vrcActionResult", new
                        {
                            action = "openInGame",
                            success = true,
                            message = "Opened in VRChat.",
                        }));
                        return;
                    }

                    _core.SendToJS("log", new { msg = "[INST] Open in-game failed (pipe unavailable) - falling back to self-invite", color = "warn" });
                    var invited = await _core.Instances.InviteSelfAsync(oigLoc);
                    Invoke(() => _core.SendToJS("vrcActionResult", new
                    {
                        action = "openInGame",
                        success = invited,
                        message = invited
                            ? "Could not open in VRChat, sent a self-invite instead."
                            : "Could not open in VRChat. Is the game running?",
                    }));
                });
                break;
            }

            case "vrcCreateInstance":
                var ciWorldId = msg["worldId"]?.ToString() ?? "";
                var ciType = msg["type"]?.ToString() ?? "public";
                var ciRegion = msg["region"]?.ToString() ?? "eu";
                var ciAndJoin = msg["andJoin"]?.ToObject<bool>() ?? true;
                if (!string.IsNullOrEmpty(ciWorldId))
                {
                    _ = Task.Run(async () =>
                    {
                        var location = _core.Instances.BuildInstanceLocation(ciWorldId, ciType, ciRegion);
                        bool ok;
                        string message;
                        var ciVrcRunning = _core.IsVrcRunning?.Invoke() ?? false;
                        if (ciAndJoin && !ciVrcRunning)
                        {
                            ok = true;
                            message = "Instance created! Launching VRChat...";
                        }
                        else if (ciAndJoin)
                        {
                            ok = await _core.Instances.InviteSelfAsync(location);
                            message = ok ? "Instance created! Self-invite sent." : "Failed to create instance.";
                        }
                        else
                        {
                            ok = true;
                            message = "Instance created.";
                        }
                        if (ok)
                        {
                            _core.Settings.MyInstances.Remove(location);
                            _core.Settings.MyInstances.Insert(0, location);
                            _core.Settings.Save();
                        }
                        Invoke(() =>
                        {
                            _core.SendToJS("vrcActionResult", new
                            {
                                action = "createInstance",
                                success = ok,
                                message,
                                location
                            });
                            if (ok && ciAndJoin && !ciVrcRunning)
                                _core.SendToJS("vrcLaunchNeeded", new
                                {
                                    location,
                                    steamVr = _core.IsSteamVrRunning?.Invoke() ?? false
                                });
                        });
                    });
                }
                break;

            case "vrcResolveWorlds":
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var worldIds = msg["worldIds"]?.ToObject<List<string>>() ?? new();
                        var tasks = worldIds.Select(async wid =>
                        {
                            try
                            {
                                var world = await _core.World.GetWorldAsync(wid);
                                if (world == null) return (wid, null as object);
                                var wThumb = ImageCacheHelper.GetWorldUrl(wid, world["imageUrl"]?.ToString() ?? world["thumbnailImageUrl"]?.ToString());
                                var prev = _core.TimeEngine.GetWorldDetail(wid);
                                _core.TimeEngine.SaveWorldDetail(wid,
                                    world["name"]?.ToString() ?? "",
                                    world["thumbnailImageUrl"]?.ToString() ?? world["imageUrl"]?.ToString() ?? "",
                                    world["description"]?.ToString() ?? "",
                                    world["imageUrl"]?.ToString() ?? "",
                                    world["authorName"]?.ToString() ?? "",
                                    world["authorId"]?.ToString() ?? "",
                                    DateTimeHelper.Iso(world["created_at"]),
                                    DateTimeHelper.Iso(world["updated_at"]),
                                    world["capacity"]?.Value<int>() ?? 0,
                                    world["recommendedCapacity"]?.Value<int>() ?? 0,
                                    world["tags"]?.ToObject<List<string>>() ?? new List<string>(),
                                    world["favorites"]?.Value<int>() ?? 0,
                                    world["visits"]?.Value<int>() ?? 0,
                                    prev?.PcSize ?? 0, prev?.AndroidSize ?? 0, prev?.IosSize ?? 0,
                                    world["heat"]?.Value<int>() ?? 0,
                                    world["popularity"]?.Value<int>() ?? 0,
                                    world["publicOccupants"]?.Value<int>() ?? 0,
                                    world["privateOccupants"]?.Value<int>() ?? 0,
                                    world["version"]?.Value<int>() ?? 0);
                                return (wid, (object)new
                                {
                                    name             = world["name"]?.ToString() ?? "",
                                    thumbnailImageUrl = wThumb,
                                    imageUrl         = wThumb,
                                });
                            }
                            catch { return (wid, null as object); }
                        });
                        var results = await Task.WhenAll(tasks);
                        var dict = results
                            .Where(r => r.Item2 != null)
                            .ToDictionary(r => r.wid, r => r.Item2!);
                        if (dict.Count > 0)
                            _core.SendToJS("vrcWorldsResolved", dict);
                    }
                    catch (Exception ex)
                    {
                        _core.SendToJS("log", new { msg = $"World resolve error: {ex.Message}", color = "err" });
                    }
                });
                break;

            case "vrcResolveGroups":
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var groupIds = msg["groupIds"]?.ToObject<List<string>>() ?? new();
                        var tasks = groupIds.Select(async gid =>
                        {
                            try
                            {
                                var grp = await _core.Groups.GetGroupAsync(gid);
                                if (grp == null) return (gid, null as object);
                                return (gid, (object)new
                                {
                                    name      = grp["name"]?.ToString() ?? "",
                                    shortCode = grp["shortCode"]?.ToString() ?? "",
                                });
                            }
                            catch { return (gid, null as object); }
                        });
                        var results = await Task.WhenAll(tasks);
                        var dict = results
                            .Where(r => r.Item2 != null)
                            .ToDictionary(r => r.gid, r => r.Item2!);
                        if (dict.Count > 0)
                            _core.SendToJS("vrcGroupsResolved", dict);
                    }
                    catch (Exception ex)
                    {
                        _core.SendToJS("log", new { msg = $"Group resolve error: {ex.Message}", color = "err" });
                    }
                });
                break;

            case "vrcGetOnlineCount":
                _ = Task.Run(async () =>
                {
                    var count = await _core.Economy.GetOnlineCountAsync();
                    if (count > 0)
                        Invoke(() => _core.SendToJS("vrcOnlineCount", new { count }));
                });
                break;

            case "vrcGetPeopleStats":
            {
                var psMyId = _core.VrcApi.CurrentUserId ?? "";
                _ = Task.Run(() =>
                {
                    var psList = _core.TimeEngine.GetAllTimeSpentPersonStats(psMyId);

                    Invoke(() => _core.SendToJS("vrcPeopleStatsData", new
                    {
                        stats = psList.Select(p => new { userId = p.UserId, seconds = p.Seconds, meets = p.Meets }),
                    }));
                });
                break;
            }

            case "vrcGetTimeSpent":
            {
                var tsMyId    = _core.VrcApi.CurrentUserId ?? "";
                var tsView    = msg["view"]?.ToString() ?? "worlds";
                var tsQuery   = (msg["query"]?.ToString() ?? "").Trim().ToLowerInvariant();
                var tsPage    = msg["page"]?.ToObject<int>() ?? 0;
                var tsReqId   = msg["reqId"]?.ToObject<long>() ?? 0;
                const int tsPageSize = 100;

                _ = Task.Run(async () =>
                {
                    var worldData  = _core.TimeEngine.GetTimeSpentWorldPage(
                        tsView == "worlds" ? tsQuery : "", tsView == "worlds" ? tsPage : 0, tsPageSize);
                    var personData = _core.TimeEngine.GetTimeSpentPersonPage(
                        tsMyId, tsView == "persons" ? tsQuery : "", tsView == "persons" ? tsPage : 0, tsPageSize);
                    var groupData = _core.TimeEngine.GetTimeSpentGroupPage(
                        tsView == "groups" ? tsQuery : "", tsView == "groups" ? tsPage : 0, tsPageSize);

                    var worldPage  = worldData.Rows;
                    var personPage = personData.Rows;
                    var friendIds = new HashSet<string>();
                    foreach (var fj in _friends.GetStoreSnapshot())
                    {
                        var fid = fj["id"]?.ToString();
                        if (!string.IsNullOrEmpty(fid) && fid != tsMyId) friendIds.Add(fid);
                    }

                    var globalFriendCount   = _core.TimeEngine.CountTimeSpentPersons(tsMyId, friendIds);
                    var globalStrangerCount = personData.TotalAll - globalFriendCount;
                    var (globalTopFriend, globalTopStranger) =
                        _core.TimeEngine.GetTopTimeSpentPersons(tsMyId, friendIds);
                    foreach (var p in personPage)
                    {
                        var image = ImageCacheHelper.GetUserCached(p.UserId) is { } diskImg
                            ? ImageCacheHelper.ToLocalUrl(diskImg)
                            : _friends.ResolvePlayerImage(p.UserId, null);
                        if (string.IsNullOrEmpty(image)) image = p.Image;
                        if (string.IsNullOrEmpty(image))
                        {
                            var fj = _friends.GetStoreValue(p.UserId);
                            if (fj != null) image = VRChatApiService.GetUserImage(fj);
                        }
                        p.Image = image;
                    }

                    void SendPage() => Invoke(() => _core.SendToJS("vrcTimeSpentData", new
                    {
                        totalSeconds  = worldData.TotalSeconds,
                        page          = tsPage,
                        reqId         = tsReqId,
                        totalWorlds   = worldData.TotalFiltered,
                        totalPersons  = personData.TotalFiltered,
                        allUniqueWorlds   = worldData.TotalAll,
                        allUniquePersons  = personData.TotalAll,
                        totalGroups       = groupData.TotalFiltered,
                        allUniqueGroups   = groupData.TotalAll,
                        globalGroupSeconds = groupData.TotalSeconds,
                        globalGroupJoins   = groupData.TotalJoins,
                        globalTopGroupName = groupData.TopGroupName,
                        maxGroupSeconds    = groupData.MaxSeconds,
                        globalFriendCount,
                        globalStrangerCount,
                        globalTopFriendName    = globalTopFriend?.DisplayName ?? "",
                        globalTopFriendSeconds = globalTopFriend?.Seconds ?? 0,
                        globalTopStrangerName    = globalTopStranger?.DisplayName ?? "",
                        globalTopStrangerSeconds = globalTopStranger?.Seconds ?? 0,
                        globalTotalWithOthers = personData.TotalSeconds,
                        globalTopWorldName    = worldData.TopWorldName,
                        globalTotalVisits     = worldData.TotalVisits,
                        maxWorldSeconds       = worldData.MaxSeconds,
                        maxPersonSeconds      = personData.MaxSeconds,
                        worlds = worldPage.Select(w => new
                        {
                            worldId    = w.WorldId,
                            worldName  = w.WorldName,
                            worldThumb = ImageCacheHelper.GetWorldUrl(w.WorldId, w.WorldThumb),
                            seconds    = w.Seconds,
                            visits     = w.Visits,
                            rank       = w.Rank,
                        }),
                        persons = personPage.Select(p => new
                        {
                            userId      = p.UserId,
                            displayName = p.DisplayName,
                            image       = ImageCacheHelper.GetUserUrl(p.UserId, p.Image),
                            seconds     = p.Seconds,
                            meets       = p.Meets,
                            rank        = p.Rank,
                        }),
                        groups = groupData.Rows.Select(g => new
                        {
                            groupId   = g.GroupId,
                            groupName = g.GroupName,
                            iconUrl   = ImageCacheHelper.GetGroupUrl(g.GroupId, g.IconUrl),
                            shortCode = g.ShortCode,
                            seconds   = g.Seconds,
                            joins     = g.Joins,
                            rank      = g.Rank,
                        }),
                    }));

                    SendPage();

                    if (tsPage > 1) return;

                    foreach (var w in worldPage.Where(w => !string.IsNullOrEmpty(w.WorldId) && !string.IsNullOrEmpty(w.WorldThumb)))
                        ImageCacheHelper.CacheWorldBackground(w.WorldId, w.WorldThumb);

                    // Only call API for worlds with no stored URL AND no cached file
                    var missingWorldIds = worldPage
                        .Where(w => !string.IsNullOrEmpty(w.WorldId)
                            && string.IsNullOrEmpty(w.WorldThumb)
                            && ImageCacheHelper.GetWorldCached(w.WorldId) == null
                            && !PermafailHelper.IsPermafailed(w.WorldId, PfType.Entity))
                        .Select(w => w.WorldId).Distinct().Take(20).ToList();

                    bool anyResolved = false;
                    foreach (var wid in missingWorldIds)
                    {
                        try
                        {
                            var wResult = await _core.World.GetWorldWithStatusAsync(wid);
                            if (wResult.result != null)
                            {
                                var wName  = wResult.result["name"]?.ToString() ?? "";
                                var wThumb = wResult.result["imageUrl"]?.ToString() ?? "";
                                ImageCacheHelper.CacheWorldBackground(wid, wThumb);
                                _core.TimeEngine.UpdateWorldInfo(wid, wName, wThumb);
                                var e = worldPage.FirstOrDefault(x => x.WorldId == wid);
                                if (e != null)
                                {
                                    if (string.IsNullOrEmpty(e.WorldName)) e.WorldName = wName;
                                    e.WorldThumb = wThumb;
                                    anyResolved = true;
                                }
                            }
                            else if (wResult.status == 403 || wResult.status == 404)
                            {
                                PermafailHelper.Add(wid, PfType.Entity, wResult.status);
                            }
                        }
                        catch { }
                    }

                    // Backfill missing person images on the current page
                    var missingPersonIds = personPage
                        .Where(p => string.IsNullOrEmpty(p.Image)
                            && !string.IsNullOrEmpty(p.UserId)
                            && ImageCacheHelper.GetUserCached(p.UserId) == null
                            && !PermafailHelper.IsPermafailed(p.UserId, PfType.Entity))
                        .Select(p => p.UserId).Distinct().Take(30).ToList();

                    if (missingPersonIds.Count > 0)
                    {
                        var sem = new SemaphoreSlim(3);
                        await Task.WhenAll(missingPersonIds.Select(async uid =>
                        {
                            await sem.WaitAsync();
                            try
                            {
                                string resolved = "";
                                var disk = ImageCacheHelper.GetUserCached(uid);
                                if (disk != null)
                                    resolved = ImageCacheHelper.ToLocalUrl(disk);
                                else if (_friends.TryGetNameImage(uid, out var fi) && !string.IsNullOrEmpty(fi.image))
                                    resolved = fi.image;
                                else
                                {
                                    var uResult = await _core.Users.GetUserWithStatusAsync(uid);
                                    if (uResult.result != null)
                                    {
                                        var img = VRChatApiService.GetUserImage(uResult.result);
                                        if (!string.IsNullOrEmpty(img))
                                            resolved = ImageCacheHelper.GetUserUrl(uid, img);
                                    }
                                    else if (uResult.status == 403 || uResult.status == 404)
                                    {
                                        PermafailHelper.Add(uid, PfType.Entity, uResult.status);
                                    }
                                    await Task.Delay(250);
                                }
                                if (!string.IsNullOrEmpty(resolved))
                                {
                                    var p = personPage.FirstOrDefault(x => x.UserId == uid);
                                    if (p != null)
                                    {
                                        p.Image = resolved;
                                        anyResolved = true;
                                    }
                                }
                            }
                            finally { sem.Release(); }
                        }));
                    }

                    if (anyResolved)
                    {
                        SendPage();
                    }
                });
                break;
            }
        }
    }

    // Instance Methods

    public Task GetCurrentInstanceAsync() => Task.Run(async () =>
    {
        try
        {
            // Step 1: Location from log watcher — no API call. If VRChat not running, treat as offline.
            var loc = (_core.IsVrcRunning?.Invoke() == true) ? _core.LogWatcher.CurrentLocation : null;
            if (string.IsNullOrEmpty(loc) || loc == "offline" || loc == "private" || loc == "traveling")
            {
                _cachedInstLocation   = "";
                _cachedInstWorldName  = "";
                _cachedInstWorldThumb = "";
                _cachedInstCapacity   = 0;
                _cachedInstType       = "";
                _cachedInstOwnerId    = "";
                _cachedInstOwnerName  = "";
                _cachedInstOwnerGroup = "";
                _cachedInstDisplayName = "";
                Invoke(() =>
                {
                    _core.PushDiscordPresence?.Invoke();
                    _core.SendToJS("vrcCurrentInstance", new { empty = true });
                });
                return;
            }

            var parsed = VRChatApiService.ParseLocation(loc);

            // Only fetch world info from API once per instance (when location changes or cache is empty).
            // Player count comes from LogWatcher — no need to poll the instance endpoint repeatedly.
            string worldName, worldThumb, ownerId, ownerName, ownerGroup, displayName;
            int worldCapacity;

            bool locationChanged = _cachedInstLocation != loc || string.IsNullOrEmpty(_cachedInstWorldName);
            if (locationChanged)
            {
                var inst = await _core.Instances.GetInstanceAsync(loc);
                worldName     = inst?["world"]?["name"]?.ToString() ?? "";
                worldThumb    = inst?["world"]?["imageUrl"]?.ToString() ?? inst?["world"]?["thumbnailImageUrl"]?.ToString() ?? "";
                worldCapacity = inst?["world"]?["capacity"]?.Value<int>() ?? inst?["capacity"]?.Value<int>() ?? 0;
                displayName   = inst?["displayName"]?.ToString() ?? "";

                // Resolve instance owner / group
                ownerId = inst?["ownerId"]?.ToString() ?? "";
                ownerName = "";
                ownerGroup = "";
                if (ownerId.StartsWith("grp_"))
                {
                    var grp = await _core.Groups.GetGroupAsync(ownerId);
                    if (grp != null)
                    {
                        ownerName  = grp["name"]?.ToString() ?? "";
                        ownerGroup = grp["shortCode"]?.ToString() ?? "";
                        _core.TimeEngine.SaveGroupTimeIdentity(ownerId, ownerName, ownerGroup,
                            grp["iconUrl"]?.ToString() ?? "");
                    }
                }
                else if (ownerId.StartsWith("usr_"))
                {
                    var f = _friends.GetStoreValue(ownerId);
                    ownerName = f?["displayName"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(ownerName))
                    {
                        var ownerUser = await _core.Users.GetUserAsync(ownerId);
                        ownerName = ownerUser?["displayName"]?.ToString() ?? "";
                    }
                }

                if (string.IsNullOrEmpty(worldName) && !string.IsNullOrEmpty(parsed.worldId))
                {
                    var world = await _core.World.GetWorldAsync(parsed.worldId);
                    if (world != null)
                    {
                        worldName     = world["name"]?.ToString() ?? "";
                        worldThumb    = ImageCacheHelper.GetWorldUrl(parsed.worldId, world["imageUrl"]?.ToString());
                        worldCapacity = world["capacity"]?.Value<int>() ?? 0;
                    }
                }
                if (string.IsNullOrEmpty(worldName)) worldName = parsed.worldId;
            }
            else
            {
                worldName     = _cachedInstWorldName;
                worldThumb    = _cachedInstWorldThumb;
                worldCapacity = _cachedInstCapacity;
                ownerId       = _cachedInstOwnerId;
                ownerName     = _cachedInstOwnerName;
                ownerGroup    = _cachedInstOwnerGroup;
                displayName   = _cachedInstDisplayName;
            }

            // Step 4: Build player list. Prefer LogWatcher (reads VRChat logs),
            // fall back to API users array
            var users = new List<object>();
            string playerSource = "none";

            Invoke(() => _core.SendToJS("log", new { msg = $"[LOG] {_core.LogWatcher.GetDiagnostics()}", color = "sec" }));

            // Source A: VRChat log file (most complete, shows ALL players)
            var logPlayers = _core.LogWatcher.GetCurrentPlayers();
            if (logPlayers.Count > 0)
            {
                playerSource = "logfile";

                // Only players with a real usr_ ID can be looked up via the VRChat API.
                // Old-format IDs (e.g. "GGQdjFCSD4") are kept for display but not fetched.
                var playersWithId = logPlayers.Where(p => !string.IsNullOrEmpty(p.UserId) && p.UserId.StartsWith("usr_")).ToList();
                var userProfiles  = new Dictionary<string, JObject>();

                // Skip only if we have a previously cached full profile (tags, platform, ageVerified etc.)
                var needFetch = playersWithId.Where(p =>
                    !_core.PlayerProfileCache.ContainsKey(p.UserId)
                ).ToList();

                if (needFetch.Count > 0)
                {
                    var semaphore = new SemaphoreSlim(5);
                    var tasks = needFetch.Select(async p =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            var profile = await _core.Users.GetUserAsync(p.UserId);
                            if (profile != null)
                            {
                                var img = VRChatApiService.GetUserImage(profile);
                                _core.PlayerAgeVerifiedCache[p.UserId] = profile["ageVerified"]?.Value<bool>() ?? false;
                                _core.StorePlayerProfile(p.UserId, profile);
                                _core.TimeEngine.SaveUserProfileCache(p.UserId, profile.ToString(Newtonsoft.Json.Formatting.None));
                                lock (userProfiles)
                                    userProfiles[p.UserId] = profile;
                            }
                        }
                        finally { semaphore.Release(); }
                    });
                    await Task.WhenAll(tasks);

                }

                // Load previously cached profiles for players that were skipped
                foreach (var p in playersWithId)
                {
                    if (!userProfiles.ContainsKey(p.UserId) && _core.PlayerProfileCache.TryGetValue(p.UserId, out var cached))
                        userProfiles[p.UserId] = cached;
                }

                Invoke(() => _core.SendToJS("log", new { msg = $"[LOG] Profiles: {needFetch.Count} fetched, {playersWithId.Count - needFetch.Count} cached", color = "sec" }));

                foreach (var p in logPlayers)
                {
                    var img               = "";
                    var status            = "";
                    var statusDescription = "";
                    JObject? profObj = null;
                    if (!string.IsNullOrEmpty(p.UserId))
                    {
                        if (userProfiles.TryGetValue(p.UserId, out var prof))
                        {
                            profObj           = prof;
                            img               = VRChatApiService.GetUserImage(prof);
                            status            = prof["status"]?.ToString() ?? "";
                            statusDescription = prof["statusDescription"]?.ToString() ?? "";
                        }
                        else if (_friends.TryGetNameImage(p.UserId, out var fi) && !string.IsNullOrEmpty(fi.image))
                        {
                            img = fi.image;
                        }
                    }
                    users.Add(BuildInstanceUser(p.UserId, p.DisplayName, img,
                        new DateTimeOffset(p.JoinedAt).ToUnixTimeMilliseconds(), profObj, false));
                }
            }

            var nUsers = users.Count;

            _cachedInstLocation   = loc;
            _cachedInstWorldName  = worldName;
            _cachedInstWorldThumb = worldThumb;
            _cachedInstCapacity   = worldCapacity;
            _cachedInstType       = parsed.instanceType;
            _cachedInstOwnerId    = ownerId;
            _cachedInstOwnerName  = ownerName;
            _cachedInstOwnerGroup = ownerGroup;
            _cachedInstDisplayName = displayName;

            Invoke(() =>
            {
                _core.PushDiscordPresence?.Invoke();
                _core.SendToJS("log", new { msg = $"Instance: {worldName} — {nUsers} total, {users.Count} tracked ({playerSource})", color = "ok" });
                _core.SendToJS("vrcCurrentInstance", new {
                    location = loc, worldId = parsed.worldId,
                    worldName, worldThumb,
                    instanceType = parsed.instanceType,
                    nUsers, capacity = worldCapacity, users, playerSource,
                    ownerId, ownerName, ownerGroup, displayName,
                    ageGate = loc.Contains("~ageGate"),
                });
            });

            ScheduleInstanceAvatarBatch();
        }
        catch (Exception ex)
        {
            Invoke(() =>
            {
                _core.SendToJS("log", new { msg = $"\u274c Instance error: {ex.Message}", color = "err" });
                _core.SendToJS("vrcCurrentInstance", new { error = ex.Message });
            });
        }
    });

    private System.Threading.Timer? _wornPushTimer;

    private void ScheduleWornAvatarPush()
    {
        _wornPushTimer?.Dispose();
        _wornPushTimer = new System.Threading.Timer(_ =>
        {
            try { Invoke(PushCurrentInstanceFromCache); } catch { }
        }, null, 2000, System.Threading.Timeout.Infinite);
    }

    private void ScheduleInstanceAvatarBatch()
    {
        _instAvatarBatchTimer?.Dispose();
        _instAvatarBatchTimer = new System.Threading.Timer(
            _ => _ = Task.Run(RunInstanceAvatarBatchAsync), null,
            InstAvatarSettleMs, System.Threading.Timeout.Infinite);
    }

    private async Task RunInstanceAvatarBatchAsync()
    {
        try
        {
            var loc = _cachedInstLocation;
            if (string.IsNullOrEmpty(loc) || !_core.VrcApi.IsLoggedIn) return;

            var fileIds = new HashSet<string>();
            foreach (var p in _core.LogWatcher.GetCurrentPlayers())
            {
                if (string.IsNullOrEmpty(p.UserId) || !p.UserId.StartsWith("usr_")) continue;
                if (!_core.PlayerProfileCache.TryGetValue(p.UserId, out var prof)) continue;
                var fid = FriendsController.ExtractAvatarFileId(prof);
                if (!string.IsNullOrEmpty(fid)) fileIds.Add(fid);
            }
            if (fileIds.Count == 0) return;

            _instAvatarBatchLoc = loc;
            _instAvatarBatchAt  = DateTime.UtcNow;

            var cachedCount = fileIds.Count(f => AvtrdbCacheHelper.GetFileAvatar(f) != null);
            var queryCount  = fileIds.Count - cachedCount;
            if (queryCount == 0) return;
            var res = await _core.Avatars.GetAvatarIdsByFileIdsAsync(fileIds);
            var resolved = res.Count(kv => kv.Value.id != null);
            Invoke(() => _core.SendToJS("log", new
            {
                msg = $"[Avatars] Instance batch: {fileIds.Count} avatar(s): {cachedCount} cached, {queryCount} queried, {resolved} known",
                color = "sec",
            }));
        }
        catch (Exception ex) { CrashHandler.WriteEntry("InstanceAvatarBatch", ex); }
    }

    // Push cached instance data + live LogWatcher players to JS (no REST)
    public void PushCurrentInstanceFromCache()
    {
        if (string.IsNullOrEmpty(_cachedInstLocation)) return;
        var parsed = VRChatApiService.ParseLocation(_cachedInstLocation);
        var logPlayers = _core.LogWatcher.GetCurrentPlayers();

        // Player leave time tracking is handled by OnPlayerLeft in AuthController event wiring
        var users = logPlayers.Select(p =>
        {
            string img = "";
            if (_friends.TryGetNameImage(p.UserId ?? "", out var fi) && !string.IsNullOrEmpty(fi.image))
                img = fi.image;

            var av = !string.IsNullOrEmpty(p.UserId) && _core.PlayerAgeVerifiedCache.TryGetValue(p.UserId, out var cached) && cached;

            JObject? profObj = null;
            if (!string.IsNullOrEmpty(p.UserId) && _core.PlayerProfileCache.TryGetValue(p.UserId, out var prof))
            {
                profObj = prof;
                if (string.IsNullOrEmpty(img))
                    img = VRChatApiService.GetUserImage(prof);
            }

            return (object)BuildInstanceUser(p.UserId, p.DisplayName, img,
                new DateTimeOffset(p.JoinedAt).ToUnixTimeMilliseconds(), profObj, av);
        }).ToList();

        _core.SendToJS("vrcCurrentInstance", new {
            location     = _cachedInstLocation,
            worldId      = parsed.worldId,
            worldName    = _cachedInstWorldName,
            worldThumb   = _cachedInstWorldThumb,
            instanceType = _cachedInstType,
            nUsers       = logPlayers.Count,
            capacity     = _cachedInstCapacity,
            users,
            playerSource = "logfile",
            ownerId      = _cachedInstOwnerId,
            ownerName    = _cachedInstOwnerName,
            ownerGroup   = _cachedInstOwnerGroup,
            displayName  = _cachedInstDisplayName,
            ageGate      = _cachedInstLocation.Contains("~ageGate"),
        });
    }

    // Timeline - LogWatcher event handlers (run on UI thread)

    public void HandleWorldChangedOnUiThread(string worldId, string location)
    {
        // Finalise previous instance event
        if (_pendingInstanceEventId != null)
        {
            var now = DateTime.UtcNow.ToString("o");
            var finalPlayers = _cumulativeInstancePlayers
                .Select(kv => BuildPlayerSnap(kv.Key, kv.Value.displayName, kv.Value.image, finalizeLeftAt: now))
                .ToList();
            var prevId = _pendingInstanceEventId;
            _core.Timeline.UpdateEvent(prevId, ev => ev.Players = finalPlayers);
            _core.Timeline.SetInstanceEventLeftAt(prevId, now);
            var finalEv = _core.Timeline.GetEvents().FirstOrDefault(e => e.Id == prevId);
            if (finalEv != null) _core.SendToJS("timelineEvent", BuildTimelinePayload(finalEv));
        }

        _cumulativeInstancePlayers.Clear();
        _playerJoinTimes.Clear();
        _playerLeftTimes.Clear();
        _meetAgainThisInstance.Clear();
        _instanceSnapshotTimer?.Dispose();
        _instanceSnapshotTimer = null;

        var selfRaw  = _core.VrcApi.CurrentUserRaw;
        var selfId   = _core.VrcApi.CurrentUserId ?? "";
        var selfName = selfRaw?["displayName"]?.ToString() ?? "";
        var selfImg  = selfRaw != null ? ImageCacheHelper.GetUserUrl(selfId, VRChatApiService.GetUserImage(selfRaw)) : "";
        if (!string.IsNullOrEmpty(selfId))
        {
            // HandlePlayerJoinedOnUiThread skips self, so this is the only place where the
            // local user is recorded. Use fallbacks so selfId is never absent from tracking
            // even if CurrentUserRaw["displayName"] isn't available yet.
            var resolvedName = !string.IsNullOrEmpty(selfName) ? selfName
                : !string.IsNullOrEmpty(_core.Settings.ActiveAccount?.DisplayName) ? _core.Settings.ActiveAccount.DisplayName
                : selfId;
            _cumulativeInstancePlayers[selfId] = (resolvedName, selfImg);
            _playerJoinTimes[selfId] = new List<string> { DateTime.UtcNow.ToString("o") };
        }

        var evId  = Guid.NewGuid().ToString("N")[..8];
        _pendingInstanceEventId = evId;

        var instEv = new TimelineService.TimelineEvent
        {
            Id        = evId,
            Type      = "instance_join",
            Timestamp = DateTime.UtcNow.ToString("o"),
            WorldId   = worldId,
            Location  = location,
            Tracked   = 1,
        };
        _core.Timeline.AddEvent(instEv);
        _core.SendToJS("timelineEvent", BuildTimelinePayload(instEv));
        _core.SendToJS("log", new { msg = $"[TIMELINE] Instance join: {worldId}", color = "sec" });

        // Reset Discord join timer for the new instance
        _core.DiscordJoinedAt = DateTime.Now;

        // Unified time engine: start world + player tracking for new instance
        _core.TimeEngine.OnWorldJoined(worldId, location);
        _lastTrackedWorldId = worldId;

        // Immediately refresh instance panel so sidebar doesn't wait for the 60s poll
        _core.SendToJS("vrcWorldJoined", new { worldId });

        // Auto-detect owned instances — add as candidate immediately (no API, just string check),
        // then validate via ownerId API inside HandleMessage so the dashboard updates without any manual action.
        var miCandId = _core.VrcApi.CurrentUserId ?? "";
        if (!string.IsNullOrEmpty(miCandId) && !string.IsNullOrEmpty(location)
            && location.Contains(miCandId)
            && !_recentlyClosedLocs.Contains(location)
            && !_core.Settings.MyInstances.Contains(location))
        {
            _core.Settings.MyInstances.Insert(0, location);
            while (_core.Settings.MyInstances.Count > 4)
                _core.Settings.MyInstances.RemoveAt(_core.Settings.MyInstances.Count - 1);
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500); // let VRChat register the instance before we query it
            await HandleMessage("vrcGetMyInstances", new JObject());
        });

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
                        var snap = _cumulativeInstancePlayers
                            .Select(kv => BuildPlayerSnap(kv.Key, kv.Value.displayName, kv.Value.image))
                            .ToList();

                        // Resolve world name: DB cache first, API fallback
                        var (wName, wThumb) = ResolveWorldInfoFromCache(worldId);
                        if (string.IsNullOrEmpty(wName) && worldId.StartsWith("wrld_") && _core.VrcApi.IsLoggedIn)
                        {
                            var world = await _core.World.GetWorldAsync(worldId);
                            if (world != null)
                            {
                                wName  = world["name"]?.ToString() ?? "";
                                wThumb = world["thumbnailImageUrl"]?.ToString() ?? "";
                            }
                        }

                        _core.Timeline.UpdateEvent(evId, ev =>
                        {
                            ev.WorldName  = wName;
                            ev.WorldThumb = wThumb;
                            ev.Players    = snap;
                        });

                        // Store name/thumb in TimeEngine so future lookups skip the API
                        if (!string.IsNullOrEmpty(wName))
                        {
                            _core.TimeEngine.UpdateWorldInfo(worldId, wName, wThumb);
                            // Backfill ALL events (entire DB) with same WorldId that are missing WorldName
                            BackfillWorldName(worldId, wName, wThumb);
                        }

                        var updated = _core.Timeline.GetEvents().FirstOrDefault(e => e.Id == evId);
                        if (updated != null) _core.SendToJS("timelineEvent", BuildTimelinePayload(updated));
                    }
                    catch { }
                });
            }
            catch { }
        }, null, 15_000, System.Threading.Timeout.Infinite);
    }

    private void NotifyFriendJoinedInstance(string userId, string displayName)
    {
#if WINDOWS
        if (_core.VrOverlay == null) return;
        if (!_friends.TryGetNameImage(userId, out var fi)) return;

        var name  = !string.IsNullOrEmpty(fi.name) ? fi.name : displayName;
        var text  = "Joined your instance";
        var time  = VRCNext.Services.Helpers.DateTimeHelper.FormatTime(DateTime.Now);
        var loc   = _core.LogWatcher.CurrentLocation ?? "";

        try
        {
            _core.VrOverlay.AddNotification("friend_joined", name, text, time, fi.image, userId, loc);
            _core.VrOverlay.EnqueueToast("friend_joined", name, text, time, fi.image,
                                         _friends.IsFavorited(userId));
            _core.SpeakToast?.Invoke("friend_joined", name, text);
        }
        catch { }
#endif
    }

    private void NotifyFriendLeftInstance(string userId, string displayName)
    {
#if WINDOWS
        if (_core.VrOverlay == null) return;
        if (!string.IsNullOrEmpty(_core.CurrentVrcUserId) && userId == _core.CurrentVrcUserId) return;
        if (_core.LogWatcher.SelfLeftRoom) return;
        if (!_friends.TryGetNameImage(userId, out var fi)) return;

        var name = !string.IsNullOrEmpty(fi.name) ? fi.name : displayName;
        var text = "Left your instance";
        var time = VRCNext.Services.Helpers.DateTimeHelper.FormatTime(DateTime.Now);

        try
        {
            _core.VrOverlay.AddNotification("friend_left", name, text, time, fi.image, userId, "");
            _core.VrOverlay.EnqueueToast("friend_left", name, text, time, fi.image,
                                         _friends.IsFavorited(userId));
            _core.SpeakToast?.Invoke("friend_left", name, text);
        }
        catch { }
#endif
    }

    public void HandlePlayerJoinedOnUiThread(string userId, string displayName)
    {
        // Skip events for the local player; VRChat logs OnPlayerJoined for self too
        if (!string.IsNullOrEmpty(_core.CurrentVrcUserId) && userId == _core.CurrentVrcUserId) return;
        if (string.IsNullOrEmpty(userId)) return;

        var isFirstSeen = !_cumulativeInstancePlayers.ContainsKey(userId);
        if (isFirstSeen)
        {
            var img = _friends.TryGetNameImage(userId, out var fi) ? fi.image : "";
            _cumulativeInstancePlayers[userId] = (displayName, img);
            // Store name so this player appears in Time Spent list even when not a friend
            _core.TimeEngine.UpdateUserInfo(userId, displayName, img);
        }

        // Always append, also on re-join.
        var logPlayer = _core.LogWatcher.GetCurrentPlayers().FirstOrDefault(p => p.UserId == userId);
        var joinedAtUtc = logPlayer != null ? logPlayer.JoinedAt.ToUniversalTime() : DateTime.UtcNow;
        if (!_playerJoinTimes.TryGetValue(userId, out var joinList))
        {
            joinList = new List<string>();
            _playerJoinTimes[userId] = joinList;
        }
        joinList.Add(joinedAtUtc.ToString("o"));
        _core.TimeEngine.OnPlayerJoined(userId, joinedAtUtc);

        NotifyFriendJoinedInstance(userId, displayName);

        // Live-update the instance_join timeline event so the UI shows players immediately
        if (_pendingInstanceEventId != null)
        {
            var evId = _pendingInstanceEventId;
            var snap = _cumulativeInstancePlayers
                .Select(kv => BuildPlayerSnap(kv.Key, kv.Value.displayName, kv.Value.image))
                .ToList();
            _core.Timeline.UpdateEvent(evId, ev =>
            {
                ev.Players = snap;
                // A join means the instance is still active — clear any stale LeftAt that may have been set on startup/auth.
                if (!string.IsNullOrEmpty(ev.LeftAt)) ev.LeftAt = "";
            });
            var updated = _core.Timeline.GetEvents().FirstOrDefault(e => e.Id == evId);
            if (updated != null) _core.SendToJS("timelineEvent", BuildTimelinePayload(updated));
        }

        // First-meet detection, only after known-users set is seeded
        if (!string.IsNullOrEmpty(userId) && _core.Timeline.KnownUsersSeeded && !_core.Timeline.IsKnownUser(userId))
        {
            _core.Timeline.AddKnownUser(userId);
            var img = _friends.TryGetNameImage(userId, out var fi) ? fi.image : "";
            var fmWorldId = _core.LogWatcher.CurrentWorldId ?? "";
            var (fmWorldName, fmWorldThumb) = ResolveWorldInfoFromCache(fmWorldId);
            var meetEv = new TimelineService.TimelineEvent
            {
                Type       = "first_meet",
                Timestamp  = DateTime.UtcNow.ToString("o"),
                UserId     = userId,
                UserName   = displayName,
                UserImage  = img,
                WorldId    = fmWorldId,
                WorldName  = fmWorldName,
                WorldThumb = fmWorldThumb,
                Location   = _core.LogWatcher.CurrentLocation ?? ""
            };
            _core.Timeline.AddEvent(meetEv);
            _core.SendToJS("timelineEvent", BuildTimelinePayload(meetEv));
            _core.SendToJS("log", new { msg = $"[TIMELINE] First meet: {displayName}", color = "sec" });

            // If world name still unknown, async-fetch and backfill ALL events with same WorldId
            if (string.IsNullOrEmpty(fmWorldName) && fmWorldId.StartsWith("wrld_") && _core.VrcApi.IsLoggedIn)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var world = await _core.World.GetWorldAsync(fmWorldId);
                        if (world == null) return;
                        var fn = world["name"]?.ToString() ?? "";
                        var ft = world["thumbnailImageUrl"]?.ToString() ?? "";
                        if (string.IsNullOrEmpty(fn)) return;
                        _core.TimeEngine.UpdateWorldInfo(fmWorldId, fn, ft);
                        Invoke(() => BackfillWorldName(fmWorldId, fn, ft));
                    }
                    catch { }
                });
            }

            // If no image yet, fetch async and update the event
            if (string.IsNullOrEmpty(img) && _core.VrcApi.IsLoggedIn)
            {
                var evId = meetEv.Id;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var profile = await _core.Users.GetUserAsync(userId);
                        if (profile == null) return;
                        var fetchedImg = ImageCacheHelper.GetUserUrl(userId, VRChatApiService.GetUserImage(profile));
                        if (string.IsNullOrEmpty(fetchedImg)) return;
                        _core.StorePlayerProfile(userId, profile);
                        _core.PlayerAgeVerifiedCache[userId] = profile["ageVerified"]?.Value<bool>() ?? false;
                        if (_cumulativeInstancePlayers.TryGetValue(userId, out var ex2) && string.IsNullOrEmpty(ex2.image))
                            _cumulativeInstancePlayers[userId] = (ex2.displayName, fetchedImg);
                        _core.Timeline.UpdateEvent(evId, ev => ev.UserImage = fetchedImg);
                        var updated = _core.Timeline.GetEvents().FirstOrDefault(e => e.Id == evId);
                        if (updated != null) Invoke(() => _core.SendToJS("timelineEvent", BuildTimelinePayload(updated)));
                    }
                    catch { }
                });
            }
        }
        else if (!string.IsNullOrEmpty(userId))
        {
            _core.Timeline.AddKnownUser(userId);

            // Meet Again: known user, not yet seen in this instance
            if (_core.Timeline.KnownUsersSeeded && !_meetAgainThisInstance.Contains(userId))
            {
                _meetAgainThisInstance.Add(userId);
                var img = _friends.TryGetNameImage(userId, out var fi2) ? fi2.image : "";
                var maWorldId = _core.LogWatcher.CurrentWorldId ?? "";
                var (maWorldName, maWorldThumb) = ResolveWorldInfoFromCache(maWorldId);
                var meetAgainEv = new TimelineService.TimelineEvent
                {
                    Type       = "meet_again",
                    Timestamp  = DateTime.UtcNow.ToString("o"),
                    UserId     = userId,
                    UserName   = displayName,
                    UserImage  = img,
                    WorldId    = maWorldId,
                    WorldName  = maWorldName,
                    WorldThumb = maWorldThumb,
                    Location   = _core.LogWatcher.CurrentLocation ?? ""
                };
                _core.Timeline.AddEvent(meetAgainEv);
                _core.SendToJS("timelineEvent", BuildTimelinePayload(meetAgainEv));

                // If world name still unknown, async-fetch and backfill ALL events with same WorldId
                if (string.IsNullOrEmpty(maWorldName) && maWorldId.StartsWith("wrld_") && _core.VrcApi.IsLoggedIn)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var world = await _core.World.GetWorldAsync(maWorldId);
                            if (world == null) return;
                            var mn = world["name"]?.ToString() ?? "";
                            var mt = world["thumbnailImageUrl"]?.ToString() ?? "";
                            if (string.IsNullOrEmpty(mn)) return;
                            _core.TimeEngine.UpdateWorldInfo(maWorldId, mn, mt);
                            Invoke(() => BackfillWorldName(maWorldId, mn, mt));
                        }
                        catch { }
                    });
                }

                // Async-fetch image if missing
                if (string.IsNullOrEmpty(img) && _core.VrcApi.IsLoggedIn)
                {
                    var maEvId = meetAgainEv.Id;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var profile = await _core.Users.GetUserAsync(userId);
                            if (profile == null) return;
                            var fetchedImg = ImageCacheHelper.GetUserUrl(userId, VRChatApiService.GetUserImage(profile));
                            if (string.IsNullOrEmpty(fetchedImg)) return;
                            _core.StorePlayerProfile(userId, profile);
                            _core.PlayerAgeVerifiedCache[userId] = profile["ageVerified"]?.Value<bool>() ?? false;
                            if (_cumulativeInstancePlayers.TryGetValue(userId, out var ex3) && string.IsNullOrEmpty(ex3.image))
                                _cumulativeInstancePlayers[userId] = (ex3.displayName, fetchedImg);
                            _core.Timeline.UpdateEvent(maEvId, ev => ev.UserImage = fetchedImg);
                            var updated = _core.Timeline.GetEvents().FirstOrDefault(e => e.Id == maEvId);
                            if (updated != null) Invoke(() => _core.SendToJS("timelineEvent", BuildTimelinePayload(updated)));
                        }
                        catch { }
                    });
                }
            }
        }

        // Instantly push updated player list to JS (no REST call needed)
        PushCurrentInstanceFromCache();

        // If we don't have a cached profile for this player yet, fetch it async so the
        // instance panel and modal get enriched data (image, status, platform, etc.)
        if (!string.IsNullOrEmpty(userId) && userId.StartsWith("usr_")
            && !_core.PlayerProfileCache.ContainsKey(userId) && _core.VrcApi.IsLoggedIn)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var profile = await _core.Users.GetUserAsync(userId);
                    if (profile == null) return;
                    var img = VRChatApiService.GetUserImage(profile);
                    _core.PlayerAgeVerifiedCache[userId] = profile["ageVerified"]?.Value<bool>() ?? false;
                    _core.StorePlayerProfile(userId, profile);
                    _core.TimeEngine.SaveUserProfileCache(userId, profile.ToString(Newtonsoft.Json.Formatting.None));

                    // Also enrich the cumulative instance player record with the resolved image
                    if (_cumulativeInstancePlayers.TryGetValue(userId, out var existing) && string.IsNullOrEmpty(existing.image))
                        _cumulativeInstancePlayers[userId] = (existing.displayName, img);

                    Invoke(() =>
                    {
                        PushCurrentInstanceFromCache();

                        // Immediately persist the updated image to the pending instance_join
                        // timeline event — don't wait for the 15 s snapshot, so images are
                        // written to DB even if the snapshot timer fires before all fetches finish.
                        if (_pendingInstanceEventId != null)
                        {
                            var evId = _pendingInstanceEventId;
                            var snap = _cumulativeInstancePlayers
                                .Select(kv => BuildPlayerSnap(kv.Key, kv.Value.displayName, kv.Value.image))
                                .ToList();
                            _core.Timeline.UpdateEvent(evId, ev => ev.Players = snap);
                        }
                    });
                }
                catch { }
            });
        }
    }

    public void HandlePlayerLeftOnUiThread(string userId, string displayName = "")
    {
        if (string.IsNullOrEmpty(userId)) return;
        if (!_playerLeftTimes.TryGetValue(userId, out var leftList))
        {
            leftList = new List<string>();
            _playerLeftTimes[userId] = leftList;
        }
        var joinCount = _playerJoinTimes.TryGetValue(userId, out var joinList) ? joinList.Count : 0;
        if (leftList.Count < joinCount)
        {
            var leftAtUtc = _core.LogWatcher.GetLastLeftTime(userId)?.ToUniversalTime() ?? DateTime.UtcNow;
            leftList.Add(leftAtUtc.ToString("o"));
        }

        NotifyFriendLeftInstance(userId, displayName);

        if (_pendingInstanceEventId != null)
        {
            var evId = _pendingInstanceEventId;
            var snap = _cumulativeInstancePlayers
                .Select(kv => BuildPlayerSnap(kv.Key, kv.Value.displayName, kv.Value.image))
                .ToList();
            _core.Timeline.UpdateEvent(evId, ev => ev.Players = snap);
            var updated = _core.Timeline.GetEvents().FirstOrDefault(e => e.Id == evId);
            if (updated != null) _core.SendToJS("timelineEvent", BuildTimelinePayload(updated));
        }
    }

    // Timeline - helpers

    /// Resolve world name + thumb from in-memory caches (no API call).
    /// Priority: instance cache (only if worldId matches) → TimeEngine DB cache.
    private (string name, string thumb) ResolveWorldInfoFromCache(string worldId)
    {
        if (string.IsNullOrEmpty(worldId)) return ("", "");
        if (!string.IsNullOrEmpty(_cachedInstWorldName) && _cachedInstLocation.StartsWith(worldId))
            return (_cachedInstWorldName, _cachedInstWorldThumb);
        if (_core.TimeEngine.Worlds.TryGetValue(worldId, out var rec) && !string.IsNullOrEmpty(rec.WorldName))
            return (rec.WorldName, rec.WorldThumb);
        return ("", "");
    }

    /// Backfill all timeline events with matching worldId that have empty WorldName.
    /// Pushes updated events to JS automatically.
    private void BackfillWorldName(string worldId, string wName, string wThumb)
    {
        if (string.IsNullOrEmpty(worldId) || string.IsNullOrEmpty(wName)) return;
        var toFix = _core.Timeline.GetEvents()
            .Where(e => e.WorldId == worldId && string.IsNullOrEmpty(e.WorldName))
            .ToList();
        foreach (var ev in toFix)
        {
            _core.Timeline.UpdateEvent(ev.Id, e => { e.WorldName = wName; e.WorldThumb = wThumb; });
            Invoke(() => _core.SendToJS("timelineEvent", BuildTimelinePayload(
                _core.Timeline.GetEvents().FirstOrDefault(e => e.Id == ev.Id) ?? ev)));
        }
    }

    private static readonly TimeSpan _tlPayloadCacheCutoff = TimeSpan.FromDays(7);

    private string ResolveWithDiskFallback(string? userId, string? storedImage)
    {
        if (string.IsNullOrEmpty(userId)) return storedImage ?? "";
        var disk = ImageCacheHelper.GetUserCached(userId);
        if (disk != null) return ImageCacheHelper.ToLocalUrl(disk);
        return _friends.ResolvePlayerImage(userId, storedImage);
    }

    public object BuildTimelinePayload(TimelineService.TimelineEvent ev)
    {
        var isRecent = VRCNext.Services.Helpers.DateTimeHelper.TryParseUtc(ev.Timestamp, out var evTs) && evTs >= DateTime.UtcNow - _tlPayloadCacheCutoff;
        string wThumb;
        if (isRecent)
        {
            wThumb = ImageCacheHelper.GetWorldUrl(ev.WorldId, ev.WorldThumb);
        }
        else
        {
            var disk = ImageCacheHelper.GetWorldCached(ev.WorldId);
            wThumb = disk != null ? ImageCacheHelper.ToLocalUrl(disk) : ImageCacheHelper.NormalizeTo512(ev.WorldThumb ?? "");
        }
        return new
        {
        id          = ev.Id,
        type        = ev.Type,
        timestamp   = ev.Timestamp,
        worldId     = ev.WorldId,
        worldName   = ev.WorldName,
        worldThumb  = wThumb,
        location    = ev.Location,
        players     = ev.Players.Select(p => new {
            userId      = p.UserId,
            displayName = p.DisplayName,
            image       = ResolveWithDiskFallback(p.UserId, p.Image),
            joinedAts   = p.JoinedAts,
            leftAts     = p.LeftAts,
        }).ToList(),
        photoPath   = ev.PhotoPath,
        photoUrl    = !string.IsNullOrEmpty(ev.PhotoPath) ? (_core.GetVirtualMediaUrl?.Invoke(ev.PhotoPath) ?? "") : _core.FixLocalUrl(ev.PhotoUrl),
        userId      = ev.UserId,
        userName    = ev.UserName,
        userImage   = ev.Type == "avatar_switch"
            ? ImageCacheHelper.GetAvatarUrl(ev.UserId, ev.UserImage)
            : ResolveWithDiskFallback(ev.UserId, ev.UserImage),
        meetCount   = ev.Type == "meet_again" ? _core.Timeline.GetMeetAgainCount(ev.UserId) : 0,
        notifId     = ev.NotifId,
        notifType   = ev.NotifType,
        notifTitle  = ev.NotifTitle,
        senderName  = ev.SenderName,
        senderId    = ev.SenderId,
        senderImage = ResolveWithDiskFallback(ev.SenderId, ev.SenderImage),
        message     = ev.Message,
        leftAt      = ev.LeftAt,
        tracked     = ev.Tracked,
        };
    }

    // Photino compatibility shim
    private static void Invoke(Action action) => action();
    private static T Invoke<T>(Func<T> func) => func();

    private JObject BuildInstanceUser(string userId, string displayName, string image, long joinedAtMs, JObject? prof, bool ageVerifiedFallback)
    {
        var o = new JObject
        {
            ["id"] = userId ?? "",
            ["displayName"] = displayName ?? "",
            ["image"] = image ?? "",
            ["joinedAt"] = joinedAtMs,
            ["status"] = prof?["status"]?.ToString() ?? "",
            ["statusDescription"] = prof?["statusDescription"]?.ToString() ?? "",
            ["platform"] = prof?["last_platform"]?.ToString() ?? prof?["platform"]?.ToString() ?? "",
            ["ageVerified"] = prof?["ageVerified"]?.Value<bool>() ?? ageVerifiedFallback,
            ["ageVerificationStatus"] = prof?["ageVerificationStatus"]?.ToString() ?? "",
            ["tags"] = prof?["tags"] as JArray ?? new JArray(),
            ["bioLinks"] = prof?["bioLinks"] as JArray ?? new JArray(),
            ["bio"] = prof?["bio"]?.ToString() ?? "",
            ["pronouns"] = prof?["pronouns"]?.ToString() ?? "",
            ["dateJoined"] = prof?["date_joined"]?.ToString() ?? "",
            ["lastLogin"] = prof?["last_login"]?.ToString() ?? "",
            ["lastActivity"] = prof?["last_activity"]?.ToString() ?? "",
        };
        var avFileId = prof != null ? FriendsController.ExtractAvatarFileId(prof) : "";
        var avTried  = VRCNext.Services.AvtrdbResolver.IsPlaceholderFileId(avFileId);
        if (!string.IsNullOrEmpty(avFileId) && !avTried)
        {
            var av = AvtrdbCacheHelper.GetFileAvatar(avFileId);
            if (av != null)
            {
                avTried = true;
                o["avatarId"]     = av.AvtrId;
                o["avatarName"]   = av.Name;
                o["avatarAuthor"] = av.AuthorName;
            }
        }
        if (string.IsNullOrEmpty(o["avatarName"]?.ToString()))
        {
            var worn = _core.LogWatcher.GetWornAvatarName(displayName ?? "");
            if (!string.IsNullOrEmpty(worn)) o["avatarName"] = worn;
        }
        if (string.IsNullOrEmpty(o["avatarId"]?.ToString())) o["avatarUnresolved"] = !avTried;
        _friends.EnrichFromProfileCache(o, userId ?? "", true);
        return o;
    }
}
