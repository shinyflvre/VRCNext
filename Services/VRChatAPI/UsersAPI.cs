using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VRCNext.Services;

public class UsersAPI(VRChatApiService ctx)
{
    private readonly Dictionary<string, Task<JObject?>> _userFetchTasks = new();

    public Task<JObject?> GetUserAsync(string userId)
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(userId)) return Task.FromResult<JObject?>(null);
        lock (_userFetchTasks)
        {
            if (_userFetchTasks.TryGetValue(userId, out var existing)) return existing;
            var task = FetchUserAsync(userId);
            _userFetchTasks[userId] = task;
            task.ContinueWith(_ => { lock (_userFetchTasks) _userFetchTasks.Remove(userId); });
            return task;
        }
    }

    private async Task<JObject?> FetchUserAsync(string userId)
    {
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/users/{userId}");
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"GetUser response: {(int)resp.StatusCode}");
            if (resp.IsSuccessStatusCode) return JObject.Parse(body);
            ctx.Log($"GetUser error: {body[..Math.Min(200, body.Length)]}");
        }
        catch (Exception ex) { ctx.Log($"GetUser exception: {ex.Message}"); }
        return null;
    }

    public async Task<(JObject? result, int status)> GetUserWithStatusAsync(string userId)
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(userId)) return (null, 0);
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/users/{userId}");
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"GetUser response: {(int)resp.StatusCode}");
            if (resp.IsSuccessStatusCode) return (JObject.Parse(body), (int)resp.StatusCode);
            ctx.Log($"GetUser error: {body[..Math.Min(200, body.Length)]}");
            return (null, (int)resp.StatusCode);
        }
        catch (Exception ex) { ctx.Log($"GetUser exception: {ex.Message}"); }
        return (null, 0);
    }

    public async Task<JObject?> UpdateStatusAsync(string status, string statusDescription)
    {
        if (!ctx.IsLoggedIn || ctx.CurrentUserId == null) return null;
        try
        {
            var payload = new JObject { ["status"] = status, ["statusDescription"] = statusDescription };
            var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/users/{ctx.CurrentUserId}", content);
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"UpdateStatus response: {(int)resp.StatusCode}");
            if (resp.IsSuccessStatusCode)
            {
                var user = JObject.Parse(body);
                ctx.CurrentUserRaw = user;
                return user;
            }
            ctx.Log($"UpdateStatus error: {body[..Math.Min(200, body.Length)]}");
        }
        catch (Exception ex) { ctx.Log($"UpdateStatus exception: {ex.Message}"); }
        return null;
    }

    public async Task<bool> SetHomeWorldAsync(string worldId)
    {
        if (!ctx.IsLoggedIn || ctx.CurrentUserId == null) return false;
        try
        {
            var payload = new JObject { ["homeLocation"] = worldId };
            var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/users/{ctx.CurrentUserId}", content);
            ctx.Log($"SetHomeWorld({worldId}): {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"SetHomeWorld exception: {ex.Message}"); return false; }
    }

    public async Task<bool> UpdateProfileFieldsAsync(string? bio, List<string>? bioLinks, List<string>? languages, string? userIcon)
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(ctx.CurrentUserId)) return false;
        var payload = new JObject();
        if (bio != null) payload["bio"] = bio;
        if (bioLinks != null) payload["bioLinks"] = JArray.FromObject(bioLinks);
        if (languages != null) payload["languages"] = JArray.FromObject(languages);
        if (userIcon != null) payload["userIcon"] = userIcon;
        if (!payload.HasValues) return true;
        try
        {
            var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/profile/{Uri.EscapeDataString(ctx.CurrentUserId)}", content);
            var names = string.Join(",", payload.Properties().Select(p => p.Name));
            ctx.Log($"UpdateProfileFields [{names}]: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"UpdateProfileFields exception: {ex.Message}"); return false; }
    }

    public async Task<JObject?> UpdateUserProfileAsync(string? pronouns)
    {
        if (!ctx.IsLoggedIn || ctx.CurrentUserId == null || pronouns == null) return null;
        try
        {
            var payload = new JObject { ["pronouns"] = pronouns };
            var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/users/{ctx.CurrentUserId}", content);
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"UpdateUserProfile response: {(int)resp.StatusCode}");
            if (resp.IsSuccessStatusCode)
            {
                var user = JObject.Parse(body);
                ctx.CurrentUserRaw = user;
                return user;
            }
            ctx.Log($"UpdateUserProfile error: {body[..Math.Min(200, body.Length)]}");
        }
        catch (Exception ex) { ctx.Log($"UpdateUserProfile exception: {ex.Message}"); }
        return null;
    }

    public async Task<JArray> SearchUsersAsync(string query, int n = 20, int offset = 0)
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(query)) return new JArray();
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/users?search={Uri.EscapeDataString(query)}&n={n}&offset={offset}");
            if (resp.IsSuccessStatusCode) return JArray.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { ctx.Log($"SearchUsers exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<bool> UpdateBadgeAsync(string badgeId, bool showcased)
    {
        if (!ctx.IsLoggedIn || ctx.CurrentUserId == null || string.IsNullOrEmpty(badgeId)) return false;
        try
        {
            var payload = new JObject { ["showcased"] = showcased };
            var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/users/{ctx.CurrentUserId}/badges/{Uri.EscapeDataString(badgeId)}", content);
            ctx.Log($"UpdateBadge({badgeId}, showcased={showcased}): {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"UpdateBadge exception: {ex.Message}"); return false; }
    }

    // VRC+ profile appearance (backgrounds, banner, theme). This lives on its own
    // endpoint - /users/{id} does not carry the background fields.
    public async Task<JObject?> GetProfileAppearanceAsync(string userId, bool asSelf = false)
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(userId)) return null;
        try
        {
            var url = $"{VRChatApiService.BASE}/profile/{Uri.EscapeDataString(userId)}";
            if (asSelf) url += "?asSelf=true";
            var resp = await ctx._http.GetAsync(url);
            if (resp.IsSuccessStatusCode) return JObject.Parse(await resp.Content.ReadAsStringAsync());
            ctx.Log($"GetProfileAppearance({userId}): {(int)resp.StatusCode}");
        }
        catch (Exception ex) { ctx.Log($"GetProfileAppearance exception: {ex.Message}"); }
        return null;
    }

    public async Task<JObject?> SetProfileBannerAsync(string userId, string fileUrl)
        => await UpdateProfileAppearanceAsync(userId, new JObject
        {
            ["bannerType"]      = "customImage",
            ["bannerCustomUrl"] = fileUrl,
        });

    // PUT profile/{userId}. Bodies observed in the web client:
    //   { backgroundType: "default" }
    //   { backgroundType: "gradient", backgroundGradientTop: "5d3f86", backgroundGradientBottom: "21385B" }
    //   { backgroundType: "texture",  backgroundTextureId: "grid" }
    // Gradient colours are sent as bare hex without a leading '#'.
    public async Task<JObject?> UpdateProfileAppearanceAsync(string userId, JObject body)
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(userId)) return null;
        try
        {
            var content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/profile/{Uri.EscapeDataString(userId)}", content);
            var text = await resp.Content.ReadAsStringAsync();
            ctx.Log($"UpdateProfileAppearance({userId}): {(int)resp.StatusCode}");
            if (resp.IsSuccessStatusCode) return JObject.Parse(text);
        }
        catch (Exception ex) { ctx.Log($"UpdateProfileAppearance exception: {ex.Message}"); }
        return null;
    }

    public static string NormalizeThemeColor(string? value)
    {
        var hex = (value ?? "").Trim().TrimStart('#').ToLowerInvariant();
        return System.Text.RegularExpressions.Regex.IsMatch(hex, "^[0-9a-f]{6}$") ? hex : "";
    }

    public async Task<JObject?> CreateProfileThemeAsync(string name, string button, string icon, string subtext)
    {
        if (!ctx.IsLoggedIn) return null;
        try
        {
            var body = new JObject
            {
                ["name"]         = name,
                ["buttonColor"]  = NormalizeThemeColor(button),
                ["iconColor"]    = NormalizeThemeColor(icon),
                ["subtextColor"] = NormalizeThemeColor(subtext),
            };
            var content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
            var resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/profile/theme", content);
            var text = await resp.Content.ReadAsStringAsync();
            ctx.Log($"CreateProfileTheme: {(int)resp.StatusCode}");
            if (resp.IsSuccessStatusCode) return JObject.Parse(text);
        }
        catch (Exception ex) { ctx.Log($"CreateProfileTheme exception: {ex.Message}"); }
        return null;
    }

    public async Task<JObject?> UpdateProfileThemeAsync(string themeId, string name, string button, string icon, string subtext)
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(themeId)) return null;
        try
        {
            var body = new JObject
            {
                ["themeId"]      = themeId,
                ["name"]         = name,
                ["buttonColor"]  = NormalizeThemeColor(button),
                ["iconColor"]    = NormalizeThemeColor(icon),
                ["subtextColor"] = NormalizeThemeColor(subtext),
            };
            var content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/profile/theme/{Uri.EscapeDataString(themeId)}", content);
            var text = await resp.Content.ReadAsStringAsync();
            ctx.Log($"UpdateProfileTheme({themeId}): {(int)resp.StatusCode}");
            if (resp.IsSuccessStatusCode)
            {
                if ((text ?? "").TrimStart().StartsWith("{")) return JObject.Parse(text);
                return new JObject
                {
                    ["id"]           = themeId,
                    ["name"]         = name,
                    ["buttonColor"]  = NormalizeThemeColor(button),
                    ["iconColor"]    = NormalizeThemeColor(icon),
                    ["subtextColor"] = NormalizeThemeColor(subtext),
                };
            }
        }
        catch (Exception ex) { ctx.Log($"UpdateProfileTheme exception: {ex.Message}"); }
        return null;
    }

    public async Task<bool> DeleteProfileThemeAsync(string themeId)
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(themeId)) return false;
        try
        {
            var resp = await ctx._http.DeleteAsync($"{VRChatApiService.BASE}/profile/theme/{Uri.EscapeDataString(themeId)}");
            ctx.Log($"DeleteProfileTheme({themeId}): {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"DeleteProfileTheme exception: {ex.Message}"); return false; }
    }

    public async Task<bool> SendBoopAsync(string userId, string? emojiId = null)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var url = string.IsNullOrEmpty(emojiId)
                ? $"{VRChatApiService.BASE}/users/{userId}/boop"
                : $"{VRChatApiService.BASE}/users/{userId}/boop?emojiId={Uri.EscapeDataString(emojiId)}";
            var resp = await ctx._http.PostAsync(url, new StringContent("{}", Encoding.UTF8, "application/json"));
            ctx.Log($"SendBoop({userId}): {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"SendBoop({userId}) exception: {ex.Message}"); return false; }
    }

    public async Task<bool> UpdateUserNoteAsync(string targetUserId, string noteText)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var payload = new { targetUserId, note = noteText };
            var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
            var resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/userNotes", content);
            ctx.Log($"UpdateUserNote {targetUserId}: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"UpdateUserNote exception: {ex.Message}"); return false; }
    }

    public async Task<JArray> GetUserGroupsByIdAsync(string userId)
    {
        if (!ctx.IsLoggedIn) return new JArray();
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/users/{userId}/groups");
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                var arr = JArray.Parse(body);
                ctx.Log($"GetUserGroups({userId}): {arr.Count} items");
                return arr;
            }
            ctx.Log($"GetUserGroups({userId}) failed: {(int)resp.StatusCode}");
        }
        catch (Exception ex) { ctx.Log($"GetUserGroups({userId}) exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JObject?> GetUserRepresentedGroupAsync(string userId)
    {
        if (!ctx.IsLoggedIn) return null;
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/users/{userId}/groups/represented");
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                var obj = JObject.Parse(body);
                ctx.Log($"GetRepresentedGroup({userId}): {obj["name"]}");
                return obj;
            }
            ctx.Log($"GetRepresentedGroup({userId}): {(int)resp.StatusCode}");
        }
        catch (Exception ex) { ctx.Log($"GetRepresentedGroup({userId}) exception: {ex.Message}"); }
        return null;
    }

    public async Task<(JArray mutuals, bool optedOut)> GetUserMutualsAsync(string userId)
    {
        if (!ctx.IsLoggedIn) return (new JArray(), false);
        try
        {
            const int pageSize = 100;
            var all = new JArray();
            int offset = 0;
            while (true)
            {
                var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/users/{userId}/mutuals/friends?n={pageSize}&offset={offset}");
                var body = await resp.Content.ReadAsStringAsync();
                ctx.Log($"GetUserMutuals/friends({userId}): offset={offset}, status={(int)resp.StatusCode}, bodyLen={body.Length}");

                if (resp.IsSuccessStatusCode)
                {
                    var token = JToken.Parse(body);
                    JArray page;
                    if (token is JArray a)
                        page = a;
                    else if (token is JObject obj)
                    {
                        page = obj["friends"] as JArray
                            ?? obj["mutuals"] as JArray
                            ?? obj["users"] as JArray
                            ?? obj["data"] as JArray
                            ?? new JArray();
                        if (page.Count == 0)
                            ctx.Log($"GetUserMutuals/friends({userId}): object keys={string.Join(", ", obj.Properties().Select(p => p.Name))}");
                    }
                    else
                        page = new JArray();

                    foreach (var item in page) all.Add(item);
                    ctx.Log($"GetUserMutuals/friends({userId}): page {page.Count}, total so far {all.Count}");
                    if (page.Count < pageSize) break;
                    offset += pageSize;
                }
                else if ((int)resp.StatusCode == 403)
                {
                    ctx.Log($"GetUserMutuals/friends({userId}): 403 – user opted out.");
                    return (new JArray(), true);
                }
                else
                {
                    ctx.Log($"GetUserMutuals/friends({userId}): unexpected status {(int)resp.StatusCode}");
                    break;
                }
            }
            return (all, false);
        }
        catch (Exception ex) { ctx.Log($"GetUserMutuals/friends({userId}) exception: {ex.Message}"); }
        return (new JArray(), false);
    }

    public async Task<JArray> GetUserMutualGroupsAsync(string userId)
    {
        if (!ctx.IsLoggedIn) return new JArray();
        try
        {
            const int pageSize = 100;
            var all = new JArray();
            int offset = 0;
            while (true)
            {
                var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/users/{userId}/mutuals/groups?n={pageSize}&offset={offset}");
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) break;
                var token = JToken.Parse(body);
                var page = token is JArray a ? a : new JArray();
                foreach (var item in page) all.Add(item);
                if (page.Count < pageSize) break;
                offset += pageSize;
            }
            return all;
        }
        catch (Exception ex) { ctx.Log($"GetUserMutualGroups({userId}) exception: {ex.Message}"); }
        return new JArray();
    }
}
