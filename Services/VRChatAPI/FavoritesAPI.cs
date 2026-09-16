using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VRCNext.Services;

public class FavoritesAPI(VRChatApiService ctx)
{
    private Task<List<JObject>>? _groupsFetch;
    private readonly object _groupsFetchLock = new();

    public Task<List<JObject>> GetFavoriteGroupsAsync()
    {
        lock (_groupsFetchLock)
        {
            if (_groupsFetch != null) return _groupsFetch;
            var task = FetchFavoriteGroupsAsync();
            _groupsFetch = task;
            task.ContinueWith(_ => { lock (_groupsFetchLock) _groupsFetch = null; }, TaskScheduler.Default);
            return task;
        }
    }

    private async Task<List<JObject>> FetchFavoriteGroupsAsync()
    {
        var all = new List<JObject>();
        if (!ctx.IsLoggedIn) { ctx.Log("GetFavoriteGroups: not logged in"); return all; }
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/favorite/groups?n=100");
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"FavoriteGroups: {(int)resp.StatusCode} len={body.Length}");
            if (resp.IsSuccessStatusCode)
            {
                var arr = JArray.Parse(body);
                ctx.Log($"FavoriteGroups: {arr.Count} total groups returned");
                foreach (var item in arr)
                {
                    ctx.Log($"  group: type={item["type"]} name={item["name"]} displayName={item["displayName"]}");
                    all.Add((JObject)item);
                }
            }
            else ctx.Log($"FavoriteGroups error: {body[..Math.Min(200, body.Length)]}");
        }
        catch (Exception ex) { ctx.Log($"FavoriteGroups exception: {ex.Message}"); }
        return all;
    }

    public async Task<bool> UpdateFavoriteGroupAsync(string type, string name, string displayName, string? visibility = null)
    {
        if (!ctx.IsLoggedIn || ctx.CurrentUserId == null) return false;
        try
        {
            object bodyObj = visibility != null
                ? (object)new { displayName, visibility }
                : new { displayName };
            var body = JsonConvert.SerializeObject(bodyObj);
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/favorite/group/{type}/{name}/{ctx.CurrentUserId}", content);
            ctx.Log($"UpdateFavoriteGroup [{type}/{name}]: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"UpdateFavoriteGroup exception: {ex.Message}"); return false; }
    }

    public async Task<List<JObject>> GetFavoriteWorldsByGroupAsync(string groupTag, int max = 100)
    {
        var all = new List<JObject>();
        if (!ctx.IsLoggedIn) return all;
        try
        {
            int offset = 0;
            while (all.Count < max)
            {
                var n = Math.Min(max - all.Count, 100);
                var url = $"{VRChatApiService.BASE}/worlds/favorites?tag={Uri.EscapeDataString(groupTag)}&n={n}&offset={offset}";
                var resp = await ctx._http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) break;
                var batch = JArray.Parse(await resp.Content.ReadAsStringAsync());
                if (batch.Count == 0) break;
                foreach (var item in batch) all.Add((JObject)item);
                if (batch.Count < n) break;
                offset += batch.Count;
                await Task.Delay(200);
            }
            ctx.Log($"FavoriteWorlds [{groupTag}]: {all.Count} worlds");
        }
        catch (Exception ex) { ctx.Log($"FavoriteWorldsByGroup exception [{groupTag}]: {ex.Message}"); }
        return all;
    }

    public async Task<List<JObject>> GetFavoriteAvatarsByGroupAsync(string groupTag, int max = 100)
    {
        var all = new List<JObject>();
        if (!ctx.IsLoggedIn) return all;
        try
        {
            int offset = 0;
            while (all.Count < max)
            {
                var n = Math.Min(max - all.Count, 100);
                var url = $"{VRChatApiService.BASE}/avatars/favorites?tag={Uri.EscapeDataString(groupTag)}&n={n}&offset={offset}";
                var resp = await ctx._http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) break;
                var batch = JArray.Parse(await resp.Content.ReadAsStringAsync());
                if (batch.Count == 0) break;
                foreach (var item in batch) all.Add((JObject)item);
                if (batch.Count < n) break;
                offset += batch.Count;
                await Task.Delay(200);
            }
            ctx.Log($"FavoriteAvatars [{groupTag}]: {all.Count} avatars");
        }
        catch (Exception ex) { ctx.Log($"FavoriteAvatarsByGroup exception [{groupTag}]: {ex.Message}"); }
        return all;
    }

    public async Task<JArray> GetFavoriteFriendsAsync()
    {
        if (!ctx.IsLoggedIn) return new JArray();
        try
        {
            var all = new JArray();
            for (int offset = 0; offset < 2000; offset += 100)
            {
                var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/favorites?type=friend&n=100&offset={offset}");
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) break;
                var batch = JArray.Parse(body);
                foreach (var item in batch) all.Add(item);
                if (batch.Count < 100) break;
            }
            ctx.Log($"GetFavoriteFriends: {all.Count} entries");
            return all;
        }
        catch (Exception ex) { ctx.Log($"GetFavoriteFriends exception: {ex.Message}"); return new JArray(); }
    }

    public async Task<JObject?> AddFavoriteFriendAsync(string userId, string groupName = "group_0")
    {
        if (!ctx.IsLoggedIn) return null;
        try
        {
            var json = JsonConvert.SerializeObject(new { type = "friend", favoriteId = userId, tags = new[] { groupName } });
            var resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/favorites",
                new StringContent(json, Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) { ctx.Log($"AddFavoriteFriend error: {body[..Math.Min(200, body.Length)]}"); return null; }
            return JObject.Parse(body);
        }
        catch (Exception ex) { ctx.Log($"AddFavoriteFriend exception: {ex.Message}"); return null; }
    }

    public async Task<(bool ok, string error)> AddFavoriteFriendToGroupAsync(string userId, string groupName, string? oldFvrtId = null)
    {
        if (!ctx.IsLoggedIn) return (false, "Not logged in");
        try
        {
            if (!string.IsNullOrEmpty(oldFvrtId))
            {
                await ctx._http.DeleteAsync($"{VRChatApiService.BASE}/favorites/{oldFvrtId}");
                await Task.Delay(400);
            }
            var json = JsonConvert.SerializeObject(new { type = "friend", favoriteId = userId, tags = new[] { groupName } });
            var resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/favorites",
                new StringContent(json, Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                var msg = VRChatApiService.TryGetApiError(body) ?? $"HTTP {(int)resp.StatusCode}";
                ctx.Log($"AddFavoriteFriendToGroup error: {msg}");
                return (false, msg);
            }
            var newFvrtId = JObject.Parse(body)["id"]?.ToString() ?? "";
            return (true, newFvrtId);
        }
        catch (Exception ex) { ctx.Log($"AddFavoriteFriendToGroup exception: {ex.Message}"); return (false, ex.Message); }
    }

    public async Task<(bool ok, string error)> AddWorldFavoriteAsync(string worldId, string groupName, string groupType = "world", string? oldFvrtId = null)
    {
        if (!ctx.IsLoggedIn) return (false, "Not logged in");
        try
        {
            if (!string.IsNullOrEmpty(oldFvrtId))
            {
                await ctx._http.DeleteAsync($"{VRChatApiService.BASE}/favorites/{oldFvrtId}");
                await Task.Delay(400);
            }
            var json = JsonConvert.SerializeObject(new { type = groupType, favoriteId = worldId, tags = new[] { groupName } });
            var resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/favorites",
                new StringContent(json, Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                var msg = VRChatApiService.TryGetApiError(body) ?? $"HTTP {(int)resp.StatusCode}";
                ctx.Log($"AddWorldFavorite error: {msg}");
                return (false, msg);
            }
            var result = JObject.Parse(body);
            var newFvrtId = result["id"]?.ToString() ?? "";
            return (true, newFvrtId);
        }
        catch (Exception ex) { ctx.Log($"AddWorldFavorite exception: {ex.Message}"); return (false, ex.Message); }
    }

    public async Task<bool> RemoveFavoriteFriendAsync(string fvrtId)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var resp = await ctx._http.DeleteAsync($"{VRChatApiService.BASE}/favorites/{fvrtId}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"RemoveFavoriteFriend exception: {ex.Message}"); return false; }
    }

    public async Task<JArray> GetUserFavWorldGroupsAsync(string userId)
    {
        if (!ctx.IsLoggedIn) return new JArray();
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/favorite/groups?ownerId={Uri.EscapeDataString(userId)}&n=100");
            if (!resp.IsSuccessStatusCode) return new JArray();
            return JArray.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { ctx.Log($"GetUserFavWorldGroups exception: {ex.Message}"); return new JArray(); }
    }

    public async Task<JArray> GetUserFavWorldsInGroupAsync(string userId, string groupName)
    {
        if (!ctx.IsLoggedIn) return new JArray();
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/worlds/favorites?userId={Uri.EscapeDataString(userId)}&tag={Uri.EscapeDataString(groupName)}&n=100");
            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden) return new JArray();
            if (!resp.IsSuccessStatusCode) return new JArray();
            return JArray.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { ctx.Log($"GetUserFavWorldsInGroup exception: {ex.Message}"); return new JArray(); }
    }
}
