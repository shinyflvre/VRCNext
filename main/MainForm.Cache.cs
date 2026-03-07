using Newtonsoft.Json.Linq;
using VRCNext.Services;

namespace VRCNext;

public partial class MainForm
{
    private class WFavGroup
    {
        public string name        { get; set; } = "";
        public string displayName { get; set; } = "";
        public string type        { get; set; } = "";
        public int    capacity    { get; set; } = 25;
    }

    /// <summary>
    /// VRChat API only returns world favorite groups that have at least one world in them.
    /// This fills in the standard empty slots so all 8 (or 4) groups are always visible.
    /// </summary>
    private static List<WFavGroup> FillMissingWorldSlots(List<WFavGroup> groupList)
    {
        var existing = new HashSet<string>(groupList.Select(g => g.name));

        var regularSlots = new[] {
            ("worlds1", "Worlds 1", "world"), ("worlds2", "Worlds 2", "world"),
            ("worlds3", "Worlds 3", "world"), ("worlds4", "Worlds 4", "world")
        };
        foreach (var (sName, sDisplay, sType) in regularSlots)
            if (!existing.Contains(sName))
                groupList.Add(new WFavGroup { name = sName, displayName = sDisplay, type = sType });

        bool hasVrcPlus = groupList.Any(g => g.type == "vrcPlusWorld");
        if (hasVrcPlus)
        {
            var vrcPlusSlots = new[] {
                ("vrcPlusWorlds1", "VRC+ Worlds 1", "vrcPlusWorld"), ("vrcPlusWorlds2", "VRC+ Worlds 2", "vrcPlusWorld"),
                ("vrcPlusWorlds3", "VRC+ Worlds 3", "vrcPlusWorld"), ("vrcPlusWorlds4", "VRC+ Worlds 4", "vrcPlusWorld")
            };
            foreach (var (sName, sDisplay, sType) in vrcPlusSlots)
                if (!existing.Contains(sName))
                    groupList.Add(new WFavGroup { name = sName, displayName = sDisplay, type = sType });
        }

        return groupList
            .OrderBy(g => g.type == "vrcPlusWorld" ? 1 : 0)
            .ThenBy(g => g.name)
            .ToList();
    }

    // ── Cache fetch helpers ───────────────────────────────────────────────────

    private async Task FetchAndCacheFavWorldsAsync()
    {
        try
        {
            var groups = await _vrcApi.GetFavoriteGroupsAsync();
            var worldTypes = new HashSet<string> { "world", "vrcPlusWorld" };
            var groupList = groups
                .Where(g => worldTypes.Contains(g["type"]?.ToString() ?? ""))
                .Select(g => new WFavGroup {
                    name        = g["name"]?.ToString() ?? "",
                    displayName = g["displayName"]?.ToString() ?? "",
                    type        = g["type"]?.ToString() ?? "world"
                })
                .Where(g => !string.IsNullOrEmpty(g.name))
                .ToList();
            groupList = FillMissingWorldSlots(groupList);

            var sem = new SemaphoreSlim(4, 4);
            var perGroup = new System.Collections.Concurrent.ConcurrentDictionary<string, List<JObject>>();
            await Task.WhenAll(groupList.Select(async g =>
            {
                await sem.WaitAsync();
                try { perGroup[g.name] = await _vrcApi.GetFavoriteWorldsByGroupAsync(g.name, 100); }
                finally { sem.Release(); }
            }));

            var allWorlds = new List<object>();
            foreach (var g in groupList)
            {
                if (!perGroup.TryGetValue(g.name, out var groupWorlds)) continue;
                foreach (var w in groupWorlds)
                {
                    var wid = w["id"]?.ToString() ?? "";
                    var stats = _worldTimeTracker.GetWorldStats(wid);
                    allWorlds.Add(new
                    {
                        id                = wid,
                        name              = w["name"]?.ToString() ?? "",
                        imageUrl          = w["imageUrl"]?.ToString() ?? "",
                        thumbnailImageUrl = w["thumbnailImageUrl"]?.ToString() ?? "",
                        authorName        = w["authorName"]?.ToString() ?? "",
                        occupants         = w["occupants"]?.Value<int>()  ?? 0,
                        capacity          = w["capacity"]?.Value<int>()   ?? 0,
                        favorites         = w["favorites"]?.Value<int>()  ?? 0,
                        visits            = w["visits"]?.Value<int>()     ?? 0,
                        tags              = w["tags"]?.ToObject<List<string>>() ?? new List<string>(),
                        favoriteGroup     = g.name,
                        favoriteId        = w["favoriteId"]?.ToString() ?? "",
                        worldTimeSeconds  = stats.totalSeconds,
                        worldVisitCount   = stats.visitCount,
                    });
                }
            }

            var payload = new { worlds = allWorlds, groups = groupList };
            if (_settings.FfcEnabled) _cache.Save(CacheHandler.KeyFavWorlds, payload);
            Invoke(() => SendToJS("vrcFavoriteWorlds", payload));
        }
        catch (Exception ex)
        {
            Invoke(() => SendToJS("log", new { msg = $"Favorite worlds error: {ex.Message}", color = "err" }));
        }
    }

    private static List<WFavGroup> FillMissingAvatarSlots(List<WFavGroup> groupList)
    {
        var existing = new HashSet<string>(groupList.Select(g => g.name));

        // VRChat has 6 avatar favorite groups (avatars1–avatars6), all with type "avatar".
        // avatars1 is free; avatars2–6 require VRC+.
        // The API only returns groups that have been renamed or contain items,
        // so we fill in any missing slots so empty groups are still visible.
        // I wonder why they use more World Groups for worlds but for avatars they expand the slots
        // VRC please fix ur sh :D
        var slots = new[] {
            ("avatars1", "Avatars 1", "avatar"),
            ("avatars2", "Avatars 2", "avatar"),
            ("avatars3", "Avatars 3", "avatar"),
            ("avatars4", "Avatars 4", "avatar"),
            ("avatars5", "Avatars 5", "avatar"),
            ("avatars6", "Avatars 6", "avatar"),
        };
        foreach (var (sName, sDisplay, sType) in slots)
            if (!existing.Contains(sName))
                groupList.Add(new WFavGroup { name = sName, displayName = sDisplay, type = sType });

        return groupList
            .OrderBy(g => g.name)
            .ToList();
    }

    private async Task FetchAndCacheFavAvatarsAsync()
    {
        try
        {
            var groups = await _vrcApi.GetFavoriteGroupsAsync();
            var avatarTypes = new HashSet<string> { "avatar" };
            var groupList = groups
                .Where(g => avatarTypes.Contains(g["type"]?.ToString() ?? ""))
                .Select(g => new WFavGroup {
                    name        = g["name"]?.ToString() ?? "",
                    displayName = g["displayName"]?.ToString() ?? "",
                    type        = g["type"]?.ToString() ?? "avatar"
                })
                .Where(g => !string.IsNullOrEmpty(g.name))
                .ToList();
            groupList = FillMissingAvatarSlots(groupList);
            int avCap = _vrcApi.HasVrcPlus ? 50 : 25;
            foreach (var g in groupList) g.capacity = avCap;

            var sem = new SemaphoreSlim(4, 4);
            var perGroup = new System.Collections.Concurrent.ConcurrentDictionary<string, List<JObject>>();
            await Task.WhenAll(groupList.Select(async g =>
            {
                await sem.WaitAsync();
                try { perGroup[g.name] = await _vrcApi.GetFavoriteAvatarsByGroupAsync(g.name, 100); }
                finally { sem.Release(); }
            }));

            var allAvatars = new List<object>();
            foreach (var g in groupList)
            {
                if (!perGroup.TryGetValue(g.name, out var groupAvatars)) continue;
                foreach (var a in groupAvatars)
                {
                    allAvatars.Add(new
                    {
                        id                = a["id"]?.ToString() ?? "",
                        name              = a["name"]?.ToString() ?? "",
                        imageUrl          = a["imageUrl"]?.ToString() ?? "",
                        thumbnailImageUrl = a["thumbnailImageUrl"]?.ToString() ?? "",
                        authorName        = a["authorName"]?.ToString() ?? "",
                        releaseStatus     = a["releaseStatus"]?.ToString() ?? "private",
                        favoriteGroup     = g.name,
                        favoriteId        = a["favoriteId"]?.ToString() ?? "",
                        unityPackages     = a["unityPackages"] as JArray ?? new JArray(),
                    });
                }
            }

            var payload = new { avatars = allAvatars, groups = groupList };
            Invoke(() => SendToJS("vrcFavoriteAvatars", payload));
        }
        catch (Exception ex)
        {
            Invoke(() => SendToJS("log", new { msg = $"Favorite avatars error: {ex.Message}", color = "err" }));
        }
    }

    private async Task FetchAndCacheAvatarsAsync()
    {
        try
        {
            var avatars = await _vrcApi.GetOwnAvatarsAsync();
var list = avatars.Select(a => new
            {
                id                = a["id"]?.ToString() ?? "",
                name              = a["name"]?.ToString() ?? "",
                imageUrl          = a["imageUrl"]?.ToString() ?? "",
                thumbnailImageUrl = a["thumbnailImageUrl"]?.ToString() ?? "",
                authorName        = a["authorName"]?.ToString() ?? "",
                releaseStatus     = a["releaseStatus"]?.ToString() ?? "private",
                description       = a["description"]?.ToString() ?? "",
                unityPackages     = a["unityPackages"] as JArray ?? new JArray(),
            }).ToList();
            var payload = new { filter = "own", avatars = list, currentAvatarId = _vrcApi.CurrentAvatarId ?? "" };
            if (_settings.FfcEnabled) _cache.Save(CacheHandler.KeyAvatars, payload);
            Invoke(() => SendToJS("vrcAvatars", payload));
        }
        catch (Exception ex)
        {
            Invoke(() => SendToJS("log", new { msg = $"Avatar load error: {ex.Message}", color = "err" }));
        }
    }

    private async Task FetchAndCacheGroupsAsync()
    {
        try
        {
            var groups = await _vrcApi.GetUserGroupsAsync();
            var ids = groups.Cast<JObject>()
                .Select(g => g["groupId"]?.ToString() ?? g["id"]?.ToString() ?? "")
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .ToList();

            var fullGroups = await Task.WhenAll(ids.Select(id => _vrcApi.GetGroupAsync(id)));

            var enriched = new List<object>();
            for (int i = 0; i < ids.Count; i++)
            {
                var full = fullGroups[i];
                if (full == null) continue;

                var myMember = full["myMember"] as JObject;
                var perms = myMember?["permissions"]?.ToObject<List<string>>();
                var name = full["name"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(name)) continue;

                var canCreate = perms == null
                    || perms.Contains("*")
                    || perms.Contains("group-instance-open-create")
                    || perms.Contains("group-instance-plus-create")
                    || perms.Contains("group-instance-public-create")
                    || perms.Contains("group-instance-restricted-create");

                var canPost  = perms != null && (perms.Contains("*") || perms.Contains("group-announcement-manage"));
                var canEvent = perms != null && (perms.Contains("*") || perms.Contains("group-calendar-manage"));

                enriched.Add(new {
                    id = full["id"]?.ToString() ?? ids[i],
                    name,
                    shortCode    = full["shortCode"]?.ToString() ?? "",
                    description  = full["description"]?.ToString() ?? "",
                    iconUrl      = full["iconUrl"]?.ToString() ?? "",
                    bannerUrl    = full["bannerUrl"]?.ToString() ?? "",
                    memberCount  = full["memberCount"]?.Value<int>() ?? 0,
                    privacy      = full["privacy"]?.ToString() ?? "",
                    joinState    = full["joinState"]?.ToString() ?? "",
                    canCreateInstance = canCreate,
                    canPost, canEvent,
                });
            }
            if (_settings.FfcEnabled) _cache.Save(CacheHandler.KeyGroups, enriched);
            Invoke(() => {
                SendToJS("log", new { msg = $"[GROUPS] {enriched.Count} loaded", color = "sec" });
                SendToJS("vrcMyGroups", enriched);
            });
        }
        catch (Exception ex)
        {
            Invoke(() => SendToJS("log", new { msg = $"Groups load error: {ex.Message}", color = "err" }));
        }
    }

    /// <summary>
    /// Sends all available disk-cached data to JS immediately (called on startup before API refresh).
    /// </summary>
    private void SendAllCachedData()
    {
        if (!_settings.FfcEnabled) return;

        // Friends are NOT sent from cache — status/location must always be live.
        // VrcRefreshFriendsAsync() fills the friends list with fresh data on startup.

        var avatars = _cache.LoadRaw(CacheHandler.KeyAvatars);
        if (avatars != null) SendToJS("vrcAvatars", avatars);

        var groups = _cache.LoadRaw(CacheHandler.KeyGroups);
        if (groups != null) SendToJS("vrcMyGroups", groups);

        var favWorlds = _cache.LoadRaw(CacheHandler.KeyFavWorlds);
        if (favWorlds != null) SendToJS("vrcFavoriteWorlds", favWorlds);
    }

    /// <summary>
    /// Kicks off background refresh of avatars, groups, and favorite worlds after login.
    /// Friends are already refreshed by VrcRefreshFriendsAsync.
    /// </summary>
    private async Task TriggerStartupBackgroundRefreshAsync()
    {
        if (!_vrcApi.IsLoggedIn) return;
        _ = Task.Run(FetchAndCacheAvatarsAsync);
        _ = Task.Run(FetchAndCacheGroupsAsync);
        _ = Task.Run(FetchAndCacheFavWorldsAsync);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Bulk-caches all friend profiles, avatars, groups, and favorite worlds.
    /// Sends ffcProgress updates to the UI. Always saves to cache regardless of FfcEnabled.
    /// </summary>
    private async Task ForceFfcAllAsync()
    {
        if (!_vrcApi.IsLoggedIn) return;

        void Progress(int current, int total, string label) =>
            Invoke(() => SendToJS("ffcProgress", new {
                progress = total > 0 ? (int)((double)current / total * 100) : 0,
                label,
                done = false
            }));

        try
        {
            var friendIds = _friendNameImg.Keys.ToList();
            int total = friendIds.Count + 3; // +3 for avatars, groups, worlds
            int completed = 0;

            Progress(completed, total, "Caching avatars...");
            await FetchAndCacheAvatarsAsync();
            Progress(++completed, total, "Caching groups...");
            await FetchAndCacheGroupsAsync();
            Progress(++completed, total, "Caching worlds...");
            await FetchAndCacheFavWorldsAsync();

            // Cache friend profiles 4 at a time.
            var semaphore = new SemaphoreSlim(4, 4);
            var tasks = friendIds.Select(async uid =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var payload = await BuildUserDetailPayloadAsync(uid, fetchNote: false);
                    if (payload != null)
                    {
                        CacheUserDetail(uid, payload);
                        _cache.Save(CacheHandler.KeyUserProfile(uid), payload);
                    }
                    await Task.Delay(250); // rate-limit gap before next profile
                }
                catch { }
                finally
                {
                    semaphore.Release();
                    int c = Interlocked.Increment(ref completed);
                    Progress(c, total, $"Caching profiles... ({c - 3}/{friendIds.Count})");
                }
            });

            await Task.WhenAll(tasks);

            Invoke(() =>
            {
                SendToJS("ffcProgress", new { progress = 100, label = $"Done! {friendIds.Count} profiles cached.", done = true });
                SendToJS("log", new { msg = $"FFC: {friendIds.Count} profiles + avatars + groups + worlds cached.", color = "ok" });
            });
        }
        catch (Exception ex)
        {
            Invoke(() => SendToJS("ffcProgress", new { progress = 0, label = "Error: " + ex.Message, done = true }));
        }
    }
}
