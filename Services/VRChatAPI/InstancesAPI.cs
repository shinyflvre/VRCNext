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

    public string BuildInstanceLocation(string worldId, string type, string region)
    {
        var rng = new Random();
        var instanceNum = rng.Next(10000, 99999);
        var nonce = Guid.NewGuid().ToString();
        var userId = ctx.CurrentUserId ?? "";

        string instanceId = type switch
        {
            "friends"     => $"{instanceNum}~friends({userId})~region({region})~nonce({nonce})",
            "hidden"      => $"{instanceNum}~hidden({userId})~region({region})~nonce({nonce})",
            "invite_plus" => $"{instanceNum}~private({userId})~canRequestInvite~region({region})~nonce({nonce})",
            "private"     => $"{instanceNum}~private({userId})~region({region})~nonce({nonce})",
            _             => $"{instanceNum}~region({region})",
        };

        return $"{worldId}:{instanceId}";
    }

    public async Task<string?> CreateGroupInstanceAsync(string worldId, string groupId, string groupAccessType, string region,
        string instanceName = "", bool queueEnabled = false, bool ageGateEnabled = false, string minAvatarPerf = "")
    {
        if (!ctx.IsLoggedIn) return null;
        try
        {
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
            if (!string.IsNullOrWhiteSpace(instanceName))
                body["displayName"] = instanceName;
            if (minAvatarPerf == "Good" || minAvatarPerf == "Medium" || minAvatarPerf == "Poor")
                body["minimumAvatarPerformance"] = minAvatarPerf;
            var resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/instances",
                new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"));
            var respBody = await resp.Content.ReadAsStringAsync();
            ctx.Log($"CreateGroupInstance: {(int)resp.StatusCode} {respBody[..Math.Min(600, respBody.Length)]}");
            if (resp.IsSuccessStatusCode)
            {
                var obj = JObject.Parse(respBody);
                var location = obj["location"]?.ToString();
                if (!string.IsNullOrEmpty(location)) return location;
                var instanceId = obj["instanceId"]?.ToString() ?? obj["id"]?.ToString();
                if (!string.IsNullOrEmpty(instanceId)) return $"{worldId}:{instanceId}";
            }
        }
        catch (Exception ex) { ctx.Log($"CreateGroupInstance exception: {ex.Message}"); }
        return null;
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
