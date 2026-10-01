using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VRCNext.Services;

public class WorldAPI
{
    private readonly VRChatApiService ctx;
    private readonly Dictionary<string, Task<JObject?>> _worldFetchTasks = new();

    private const int MemCacheMax = 256;
    private readonly object _worldCacheLock = new();
    private readonly Dictionary<string, LinkedListNode<(string worldId, JObject world)>> _worldCacheMap = new();
    private readonly LinkedList<(string worldId, JObject world)> _worldCacheLru = new();

    public int CachedWorldCount { get { lock (_worldCacheLock) return _worldCacheMap.Count; } }

    private bool TryGetCachedWorld(string worldId, out JObject world)
    {
        lock (_worldCacheLock)
        {
            if (_worldCacheMap.TryGetValue(worldId, out var node))
            {
                _worldCacheLru.Remove(node);
                _worldCacheLru.AddLast(node);
                world = node.Value.world;
                return true;
            }
        }
        world = null!;
        return false;
    }

    private void StoreCachedWorld(string worldId, JObject world)
    {
        lock (_worldCacheLock)
        {
            if (_worldCacheMap.TryGetValue(worldId, out var node))
            {
                node.Value = (worldId, world);
                _worldCacheLru.Remove(node);
                _worldCacheLru.AddLast(node);
                return;
            }
            _worldCacheMap[worldId] = _worldCacheLru.AddLast((worldId, world));
            while (_worldCacheMap.Count > MemCacheMax)
            {
                var oldest = _worldCacheLru.First!;
                _worldCacheLru.RemoveFirst();
                _worldCacheMap.Remove(oldest.Value.worldId);
            }
        }
    }

    private const int DiskCacheMax = 128;
    private readonly Dictionary<string, JObject> _diskCache;
    private readonly HashSet<string> _loadedIds    = new();
    private readonly HashSet<string> _usedFromCache = new();
    private int  _addedCount     = 0;
    private bool _flushScheduled = false;

    private static readonly TimeSpan DiskCacheTtl = TimeSpan.FromDays(7);

    public event Action<string>? OnCacheLog;

    public WorldAPI(VRChatApiService ctx)
    {
        this.ctx = ctx;
        var dict = new Dictionary<string, JObject>();
        try
        {
            var ch = new CacheHandler();
            if (ch.IsFresh(CacheHandler.KeyWorldMeta, DiskCacheTtl))
            {
                var raw = ch.LoadRaw(CacheHandler.KeyWorldMeta);
                if (raw is JArray arr)
                {
                    foreach (var item in arr.OfType<JObject>())
                    {
                        var id = item["id"]?.ToString();
                        if (!string.IsNullOrEmpty(id) && !dict.ContainsKey(id))
                            dict[id] = item;
                    }
                    arr.Clear();
                }
                while (dict.Count > DiskCacheMax)
                    dict.Remove(dict.Keys.First());
                foreach (var item in dict.Values)
                    PreventAdoption(item);
            }
        }
        catch { }
        _diskCache = dict;
        _loadedIds.UnionWith(dict.Keys);
    }

    private void PersistWorld(string worldId, JObject world)
    {
        lock (_diskCache)
        {
            bool isNew = !_diskCache.ContainsKey(worldId);
            if (isNew)
            {
                _addedCount++;
                if (_diskCache.Count >= DiskCacheMax)
                {
                    // Evict a loaded-but-unused entry first to free stale slots
                    var toEvict = _loadedIds.FirstOrDefault(k => !_usedFromCache.Contains(k) && _diskCache.ContainsKey(k));
                    if (toEvict != null)
                    {
                        _diskCache.Remove(toEvict);
                        _loadedIds.Remove(toEvict);
                    }
                    else
                    {
                        // Fallback: evict any entry not currently serving as a cache hit
                        var fallback = _diskCache.Keys.FirstOrDefault(k => !_usedFromCache.Contains(k));
                        _diskCache.Remove(fallback ?? _diskCache.Keys.First());
                    }
                }
            }
            _diskCache[worldId] = world;
            if (!_saveScheduled)
            {
                _saveScheduled = true;
                _ = Task.Delay(5000).ContinueWith(_ => SaveDiskCache());
            }

            if (!_flushScheduled)
            {
                _flushScheduled = true;
                _ = Task.Delay(5000).ContinueWith(_ => FlushStartupCache());
            }
        }
    }

    private bool _saveScheduled;

    private void SaveDiskCache()
    {
        lock (_diskCache)
        {
            _saveScheduled = false;
            try { new CacheHandler().Save(CacheHandler.KeyWorldMeta, _diskCache.Values.ToList()); }
            catch { }
        }
    }

    private void FlushStartupCache()
    {
        lock (_diskCache)
        {
            var toRemove = _loadedIds.Where(k => !_usedFromCache.Contains(k) && _diskCache.ContainsKey(k)).ToList();
            foreach (var k in toRemove) { _diskCache.Remove(k); _loadedIds.Remove(k); }
            try { new CacheHandler().Save(CacheHandler.KeyWorldMeta, _diskCache.Values.ToList()); }
            catch { }
            OnCacheLog?.Invoke($"Used {_usedFromCache.Count} cached world entries");
            OnCacheLog?.Invoke($"Freed {toRemove.Count} cached World entries");
            OnCacheLog?.Invoke($"Added {_addedCount} new World entries to cache");
        }
    }

    public Task<JObject?> GetWorldAsync(string worldId)
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(worldId)) return Task.FromResult<JObject?>(null);
        if (TryGetCachedWorld(worldId, out var mem)) return Task.FromResult<JObject?>(mem);
        lock (_diskCache)
        {
            if (_diskCache.TryGetValue(worldId, out var disk))
            {
                _usedFromCache.Add(worldId);
                StoreCachedWorld(worldId, disk);
                return Task.FromResult<JObject?>(disk);
            }
        }
        lock (_worldFetchTasks)
        {
            if (_worldFetchTasks.TryGetValue(worldId, out var existing)) return existing;
            var task = FetchWorldAsync(worldId);
            _worldFetchTasks[worldId] = task;
            task.ContinueWith(_ => { lock (_worldFetchTasks) _worldFetchTasks.Remove(worldId); });
            return task;
        }
    }

    // Parents the world to a private one-element array so Json.NET clones it on Add instead of adopting it..
    // therwise the cached instance pins every container it was ever added to.
    private static void PreventAdoption(JObject world) => _ = new JArray(world);

    private static readonly JsonLoadSettings WorldLoadSettings = new() { LineInfoHandling = LineInfoHandling.Ignore };

    private async Task<JObject?> FetchWorldAsync(string worldId)
    {
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/worlds/{worldId}");
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
            {
                var world = JObject.Parse(body, WorldLoadSettings);
                PreventAdoption(world);
                StoreCachedWorld(worldId, world);
                PersistWorld(worldId, world);
                return world;
            }
        }
        catch (Exception ex) { ctx.Log($"GetWorld exception: {ex.Message}"); }
        return null;
    }

    public async Task<(JObject? result, int status)> GetWorldWithStatusAsync(string worldId)
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(worldId)) return (null, 0);
        if (TryGetCachedWorld(worldId, out var mem)) return (mem, 200);
        lock (_diskCache)
        {
            if (_diskCache.TryGetValue(worldId, out var disk))
            {
                _usedFromCache.Add(worldId);
                StoreCachedWorld(worldId, disk);
                return (disk, 200);
            }
        }
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/worlds/{worldId}");
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
            {
                var world = JObject.Parse(body, WorldLoadSettings);
                PreventAdoption(world);
                StoreCachedWorld(worldId, world);
                PersistWorld(worldId, world);
                return (world, 200);
            }
            return (null, (int)resp.StatusCode);
        }
        catch (Exception ex) { ctx.Log($"GetWorld exception: {ex.Message}"); }
        return (null, 0);
    }

    public async Task<JObject?> GetWorldFreshAsync(string worldId)
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(worldId)) return null;
        return await FetchWorldAsync(worldId);
    }

    public async Task<JArray> GetMyWorldsAsync()
    {
        if (!ctx.IsLoggedIn) return new JArray();
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/worlds?user=me&releaseStatus=all&n=100&sort=updated");
            ctx.Log($"GetMyWorlds: {(int)resp.StatusCode}");
            if (resp.IsSuccessStatusCode)
            {
                var arr = JArray.Parse(await resp.Content.ReadAsStringAsync());
                var visible = new JArray();
                foreach (var w in arr.OfType<JObject>())
                    if (!AvatarsAPI.IsHidden(w)) visible.Add(w);
                return visible;
            }
        }
        catch (Exception ex) { ctx.Log($"GetMyWorlds exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JArray> GetUserWorldsAsync(string userId)
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(userId)) return new JArray();
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/worlds?userId={Uri.EscapeDataString(userId)}&releaseStatus=public&n=100&sort=updated");
            if (resp.IsSuccessStatusCode) return JArray.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { ctx.Log($"GetUserWorlds exception: {ex.Message}"); }
        return new JArray();
    }

    private const int RecentWorldsMax = 32;

    public async Task<JArray> GetRecentWorldsAsync()
    {
        if (!ctx.IsLoggedIn) return new JArray();
        try
        {
            // Load persistent cache: worldId → full world object
            var cache = new Dictionary<string, JObject>();
            var cacheHandler = new CacheHandler();
            var cached = cacheHandler.IsFresh(CacheHandler.KeyRecentWorlds, DiskCacheTtl)
                ? cacheHandler.LoadRaw(CacheHandler.KeyRecentWorlds)
                : null;
            if (cached is Newtonsoft.Json.Linq.JArray cachedArr)
                foreach (var item in cachedArr.OfType<JObject>())
                {
                    var cid = item["id"]?.ToString();
                    if (!string.IsNullOrEmpty(cid)) cache[cid] = item;
                }

            // Fetch current location strings to determine order
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/instances/recent");
            if (!resp.IsSuccessStatusCode) return BuildFromCache(cache);
            var locations = JArray.Parse(await resp.Content.ReadAsStringAsync());

            var seen     = new HashSet<string>();
            var worldIds = new List<string>();
            foreach (var loc in locations)
            {
                var locStr  = loc.ToString();
                var worldId = locStr.Contains(':') ? locStr.Split(':')[0] : locStr;
                if (worldId.StartsWith("wrld_") && seen.Add(worldId))
                    worldIds.Add(worldId);
            }
            worldIds = worldIds.Take(16).ToList();

            // Fetch only world IDs not already in cache
            var missing = worldIds.Where(id => !cache.ContainsKey(id)).ToList();
            if (missing.Count > 0)
            {
                var fetched = await Task.WhenAll(missing.Select(async wid =>
                {
                    try { return await GetWorldAsync(wid); }
                    catch { return null; }
                }));
                foreach (var w in fetched)
                {
                    var wid = w?["id"]?.ToString();
                    if (w != null && !string.IsNullOrEmpty(wid)) cache[wid] = w;
                }
            }

            // Build ordered result matching /instances/recent order
            var result = new JArray();
            foreach (var id in worldIds)
                if (cache.TryGetValue(id, out var w)) result.Add(w);

            // Persist updated cache: current order first, then remaining cached worlds, max 32
            var updatedCache = new JArray();
            var inResult = new HashSet<string>(worldIds);
            foreach (var id in worldIds)
                if (cache.TryGetValue(id, out var w)) updatedCache.Add(w);
            foreach (var kv in cache)
                if (!inResult.Contains(kv.Key) && updatedCache.Count < RecentWorldsMax) updatedCache.Add(kv.Value);
            cacheHandler.Save(CacheHandler.KeyRecentWorlds, updatedCache);

            ctx.Log($"GetRecentWorlds: {result.Count} worlds ({missing.Count} fetched, {worldIds.Count - missing.Count} from cache)");
            return result;
        }
        catch (Exception ex) { ctx.Log($"GetRecentWorlds exception: {ex.Message}"); }
        return new JArray();
    }

    // Resolve a list of world IDs to full world objects (order preserved), using the world cache.
    public async Task<JArray> GetWorldsByIdsAsync(IEnumerable<string> ids)
    {
        var arr = new JArray();
        if (!ctx.IsLoggedIn) return arr;
        var list = ids.Where(id => !string.IsNullOrEmpty(id)).ToList();
        var fetched = await Task.WhenAll(list.Select(async id =>
        {
            try { return await GetWorldAsync(id); } catch { return null; }
        }));
        foreach (var w in fetched) if (w != null) arr.Add(w);
        return arr;
    }

    private static JArray BuildFromCache(Dictionary<string, JObject> cache)
    {
        var arr = new JArray();
        foreach (var w in cache.Values.Take(16)) arr.Add(w);
        return arr;
    }

    public async Task<JArray> GetPopularWorldsAsync(int n = 32)
    {
        if (!ctx.IsLoggedIn) return new JArray();
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/worlds?sort=popularity&order=descending&releaseStatus=public&n={n}");
            if (resp.IsSuccessStatusCode) return JArray.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { ctx.Log($"GetPopularWorlds exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JArray> GetActiveWorldsAsync(int n = 32)
    {
        if (!ctx.IsLoggedIn) return new JArray();
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/worlds?sort=heat&order=descending&releaseStatus=public&n={n}");
            if (resp.IsSuccessStatusCode) return JArray.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { ctx.Log($"GetActiveWorlds exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JArray> SearchWorldsAsync(string query, int n = 20, int offset = 0, string sort = "relevance")
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(query)) return new JArray();
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/worlds?search={Uri.EscapeDataString(query)}&n={n}&offset={offset}&sort={Uri.EscapeDataString(sort)}");
            if (resp.IsSuccessStatusCode) return JArray.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { ctx.Log($"SearchWorlds exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<(bool ok, string error)> UpdateWorldAsync(string worldId, string name, string description, List<string> tags)
    {
        if (!ctx.IsLoggedIn) return (false, "Not logged in");
        try
        {
            var body = JsonConvert.SerializeObject(new { name, description, tags });
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/worlds/{worldId}", content);
            var respBody = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
            {
                try { StoreCachedWorld(worldId, JObject.Parse(respBody)); } catch { }
                return (true, "");
            }
            ctx.Log($"UpdateWorld {(int)resp.StatusCode}: {respBody[..Math.Min(200, respBody.Length)]}");
            return (false, VRChatApiService.TryGetApiError(respBody) ?? $"API error {(int)resp.StatusCode}");
        }
        catch (Exception ex) { ctx.Log($"UpdateWorld exception: {ex.Message}"); return (false, ex.Message); }
    }

    public Task<(bool ok, string imageUrl, string error)> UploadWorldMainImageAsync(
        string worldId, string existingImageUrl, byte[] imageBytes)
        => FilesAPI.ReplaceEntityImageAsync(ctx, "worlds", worldId, existingImageUrl, imageBytes);

    public async Task<(bool ok, string error)> DeleteWorldAsync(string worldId)
    {
        if (!ctx.IsLoggedIn) return (false, "Not logged in");
        try
        {
            var resp = await ctx._http.DeleteAsync($"{VRChatApiService.BASE}/worlds/{worldId}");
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"DeleteWorld {worldId}: {(int)resp.StatusCode}");
            if (resp.IsSuccessStatusCode) return (true, "");
            return (false, VRChatApiService.TryGetApiError(body) ?? $"API error {(int)resp.StatusCode}");
        }
        catch (Exception ex) { ctx.Log($"DeleteWorld exception: {ex.Message}"); return (false, ex.Message); }
    }
}
