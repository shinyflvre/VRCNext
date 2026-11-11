using System.Text;
using Newtonsoft.Json.Linq;

namespace VRCNext.Services;

public class InstancesAPI(VRChatApiService ctx)
{
    public async Task<JObject?> GetInstanceAsync(string location)
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(location)) return null;
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/instances/{location}");
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode) return JObject.Parse(body);
            ctx.Log($"GetInstance {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        }
        catch (Exception ex) { ctx.Log($"GetInstance exception: {ex.Message}"); }
        return null;
    }

    private const int InstanceListPageSize = 50;
    private const int ActiveInstanceMaxPages = 4;
    private const int InstanceListSpacingMs = 333;

    public async Task<JArray?> GetActiveInstancesAsync()
    {
        if (!ctx.IsLoggedIn) return null;
        var rows = new JArray();
        for (var page = 0; page < ActiveInstanceMaxPages; page++)
        {
            if (page > 0) await Task.Delay(InstanceListSpacingMs);
            try
            {
                var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/instances/active?n={InstanceListPageSize}&offset={page * InstanceListPageSize}");
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    ctx.Log($"GetActiveInstances {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
                    return page == 0 ? null : rows;
                }
                var pageRows = JArray.Parse(body);
                foreach (var row in pageRows) rows.Add(row);
                if (pageRows.Count < InstanceListPageSize) break;
            }
            catch (Exception ex)
            {
                ctx.Log($"GetActiveInstances exception: {ex.Message}");
                return page == 0 ? null : rows;
            }
        }
        return rows;
    }

    public async Task<JArray?> DiscoverInstancesAsync()
    {
        if (!ctx.IsLoggedIn) return null;
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/instances/discover?n={InstanceListPageSize}");
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode) return JObject.Parse(body)["instances"] as JArray ?? new JArray();
            ctx.Log($"DiscoverInstances {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        }
        catch (Exception ex) { ctx.Log($"DiscoverInstances exception: {ex.Message}"); }
        return null;
    }

    public static JArray GroupInstancesByWorld(JArray instances)
    {
        var worlds = new Dictionary<string, (JObject world, int users)>();
        foreach (var instance in instances.OfType<JObject>())
        {
            if (instance["world"] is not JObject world) continue;
            var id = world["id"]?.ToString();
            if (string.IsNullOrEmpty(id)) continue;
            var users = instance["userCount"]?.Value<int?>() ?? instance["n_users"]?.Value<int?>() ?? 0;
            worlds[id] = worlds.TryGetValue(id, out var entry) ? (entry.world, entry.users + users) : (world, users);
        }
        var result = new JArray();
        foreach (var entry in worlds.Values
            .Select(e => (e.world, players: Math.Max(e.world["occupants"]?.Value<int?>() ?? 0, e.users)))
            .OrderByDescending(e => e.players))
        {
            entry.world["occupants"] = entry.players;
            result.Add(entry.world);
        }
        return result;
    }

    public async Task<bool> CloseInstanceAsync(string location)
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(location)) return false;
        try
        {
            var inst = await GetInstanceAsync(location);
            if (inst == null)
            {
                ctx.Log("CloseInstance: instance not found (already gone)");
                return true;
            }
            var fullLoc = inst["location"]?.ToString() ?? "";
            var colonIdx = fullLoc.IndexOf(':');
            if (colonIdx < 0) return false;
            var worldId    = fullLoc[..colonIdx];
            var instanceId = fullLoc[(colonIdx + 1)..];
            var resp = await ctx._http.DeleteAsync($"{VRChatApiService.BASE}/instances/{worldId}:{instanceId}?hardClose=true");
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"CloseInstance {(int)resp.StatusCode}: {body[..Math.Min(120, body.Length)]}");
            if ((int)resp.StatusCode == 403 && body.Contains("already closed")) return true;
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"CloseInstance exception: {ex.Message}"); return false; }
    }

    public async Task<bool> InviteSelfAsync(string location)
    {
        if (!ctx.IsLoggedIn || ctx.CurrentUserId == null) { ctx.Log("InviteSelf: not logged in"); return false; }
        try
        {
            var parts = location.Split(':', 2);
            if (parts.Length < 2) { ctx.Log($"InviteSelf: invalid location format: {location}"); return false; }
            var worldId    = parts[0];
            var instanceId = parts[1];

            var url = $"{VRChatApiService.BASE}/instances/{worldId}:{instanceId}/invite";
            ctx.Log($"InviteSelf: POST {url}");

            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp = await ctx._http.PostAsync(url, content);
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"InviteSelf response: {(int)resp.StatusCode} body: {body[..Math.Min(300, body.Length)]}");

            if (resp.IsSuccessStatusCode) return true;

            var url2 = $"{VRChatApiService.BASE}/invite/myself/to/{worldId}:{instanceId}";
            ctx.Log($"InviteSelf fallback: POST {url2}");
            var content2 = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp2 = await ctx._http.PostAsync(url2, content2);
            var body2 = await resp2.Content.ReadAsStringAsync();
            ctx.Log($"InviteSelf fallback response: {(int)resp2.StatusCode} body: {body2[..Math.Min(300, body2.Length)]}");
            return resp2.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"InviteSelf exception: {ex.Message}"); return false; }
    }

    public async Task<string> GetInstanceShortNameAsync(string location)
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(location)) return "";
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/instances/{location}/shortName");
            if (!resp.IsSuccessStatusCode) return "";
            var body = await resp.Content.ReadAsStringAsync();
            var jo = Newtonsoft.Json.Linq.JObject.Parse(body);
            return jo["shortName"]?.ToString() ?? jo["secureName"]?.ToString() ?? "";
        }
        catch (Exception ex) { ctx.Log($"GetInstanceShortName exception: {ex.Message}"); return ""; }
    }

    public static string BuildLaunchUri(string location) =>
        $"vrchat://launch?ref=vrchat.com&id={location}";

    public async Task<string?> CreateInstanceAsync(string worldId, string type, string region, InstanceCreateExtras extras)
    {
        if (!ctx.IsLoggedIn) return null;
        var apiType = type == "invite_plus" ? "private" : type;
        var body = new JObject
        {
            ["worldId"] = worldId,
            ["type"]    = apiType,
            ["region"]  = region,
        };
        if (apiType != "public") body["ownerId"] = ctx.CurrentUserId;
        if (type == "invite_plus") body["canRequestInvite"] = true;
        ApplyCreateExtras(body, extras);
        return await PostInstanceAsync(body, worldId);
    }

    public async Task<string?> CreateGroupInstanceAsync(string worldId, string groupId, string groupAccessType, string region,
        InstanceCreateExtras extras, bool queueEnabled = false, bool ageGateEnabled = false, string minAvatarPerf = "")
    {
        if (!ctx.IsLoggedIn) return null;
        var body = new JObject
        {
            ["worldId"]         = worldId,
            ["type"]            = "group",
            ["ownerId"]         = groupId,
            ["groupAccessType"] = groupAccessType,
            ["region"]          = region,
            ["queueEnabled"]    = queueEnabled,
            ["ageGate"]         = ageGateEnabled,
        };
        ApplyCreateExtras(body, extras);
        if (minAvatarPerf == "Good" || minAvatarPerf == "Medium" || minAvatarPerf == "Poor")
            body["minimumAvatarPerformance"] = minAvatarPerf;
        return await PostInstanceAsync(body, worldId);
    }

    private static void ApplyCreateExtras(JObject body, InstanceCreateExtras extras)
    {
        if (!string.IsNullOrWhiteSpace(extras.InstanceName)) body["displayName"] = extras.InstanceName;
        if (!string.IsNullOrWhiteSpace(extras.Description)) body["description"] = extras.Description;
        if (!string.IsNullOrWhiteSpace(extras.CategoryId)) body["categoryId"] = extras.CategoryId;
        if (extras.VibeIds.Count > 0) body["vibeIds"] = new JArray(extras.VibeIds);
        if (extras.Languages.Count > 0) body["languages"] = new JArray(extras.Languages);
    }

    private async Task<string?> PostInstanceAsync(JObject body, string worldId)
    {
        try
        {
            var resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/instances",
                new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"));
            var respBody = await resp.Content.ReadAsStringAsync();
            ctx.Log($"CreateInstance: {(int)resp.StatusCode} {respBody[..Math.Min(600, respBody.Length)]}");
            if (resp.IsSuccessStatusCode)
            {
                var obj = JObject.Parse(respBody);
                var location = obj["location"]?.ToString();
                if (!string.IsNullOrEmpty(location)) return location;
                var instanceId = obj["instanceId"]?.ToString() ?? obj["id"]?.ToString();
                if (!string.IsNullOrEmpty(instanceId)) return $"{worldId}:{instanceId}";
            }
        }
        catch (Exception ex) { ctx.Log($"CreateInstance exception: {ex.Message}"); }
        return null;
    }

    private JArray? _instanceCategories;
    private JArray? _instanceVibes;

    public async Task<(JArray categories, JArray vibes)> GetInstanceCreateOptionsAsync()
    {
        _instanceCategories ??= await FetchCreateOptionsAsync("instanceCategories");
        _instanceVibes ??= await FetchCreateOptionsAsync("instanceVibes");
        return (_instanceCategories ?? new JArray(), _instanceVibes ?? new JArray());
    }

    private async Task<JArray?> FetchCreateOptionsAsync(string path)
    {
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/{path}");
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                ctx.Log($"{path} {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
                return null;
            }
            var live = JArray.Parse(body)
                .Where(row => row["deleted"]?.Value<bool>() != true)
                .OrderBy(row => row["order"]?.Value<int>() ?? 0);
            return new JArray(live);
        }
        catch (Exception ex) { ctx.Log($"{path} exception: {ex.Message}"); }
        return null;
    }

    public async Task<object> BuildInstanceMetaAsync(JObject? inst)
    {
        var (categories, vibes) = await GetInstanceCreateOptionsAsync();
        var categoryId = inst?["categoryId"]?.ToString() ?? "";
        var vibeTitles = (inst?["vibeIds"] as JArray)?
            .Select(id => vibes.FirstOrDefault(v => v["id"]?.ToString() == id.ToString())?["title"]?.ToString() ?? "")
            .Where(title => title.Length > 0)
            .ToArray() ?? Array.Empty<string>();
        var languages = (inst?["languagesIso639"] as JArray)?
            .Select(code => code.ToString())
            .Where(code => code.Length > 0)
            .Take(3)
            .ToArray() ?? Array.Empty<string>();
        return new
        {
            description  = inst?["description"]?.ToString() ?? "",
            categoryName = categories.FirstOrDefault(c => c["id"]?.ToString() == categoryId)?["name"]?.ToString() ?? "",
            vibes        = vibeTitles,
            languages,
        };
    }

    public async Task<JArray> GetAllGroupInstancesAsync()
    {
        if (!ctx.IsLoggedIn || ctx.CurrentUserId == null) return new JArray();
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/users/{ctx.CurrentUserId}/instances/groups");
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"GetAllGroupInstances: {(int)resp.StatusCode}, len={body.Length}");
            if (resp.IsSuccessStatusCode)
            {
                var token = JToken.Parse(body);
                if (token is JObject obj && obj["instances"] is JArray arr) return arr;
                if (token is JArray directArr) return directArr;
            }
        }
        catch (Exception ex) { ctx.Log($"GetAllGroupInstances exception: {ex.Message}"); }
        return new JArray();
    }
}

public record InstanceCreateExtras(string InstanceName, string Description, string CategoryId, List<string> VibeIds, List<string> Languages)
{
    public static InstanceCreateExtras FromMessage(JObject msg) => new(
        msg["instanceName"]?.ToString() ?? "",
        msg["description"]?.ToString() ?? "",
        msg["categoryId"]?.ToString() ?? "",
        msg["vibeIds"]?.ToObject<List<string>>() ?? new List<string>(),
        msg["languages"]?.ToObject<List<string>>() ?? new List<string>());
}
