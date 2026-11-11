using System.Text;
using Newtonsoft.Json.Linq;

namespace VRCNext.Services;

public class PlayerModerationAPI(VRChatApiService ctx)
{
    public async Task<JArray> GetPlayerModerationsAsync(string type)
    {
        if (!ctx.IsLoggedIn) return new JArray();
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/auth/user/playermoderations?type={Uri.EscapeDataString(type)}");
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"GetPlayerModerations({type}): status={(int)resp.StatusCode}, bodyLen={body.Length}");
            if (resp.IsSuccessStatusCode)
            {
                var token = JToken.Parse(body);
                if (token is JArray arr) return arr;
            }
            else ctx.Log($"GetPlayerModerations({type}): error body={body[..Math.Min(200, body.Length)]}");
        }
        catch (Exception ex) { ctx.Log($"GetPlayerModerations({type}) exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<bool> ModerateUserAsync(string userId, string type)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var body = new StringContent(
                $"{{\"moderated\":\"{userId}\",\"type\":\"{type}\"}}",
                Encoding.UTF8, "application/json");
            var resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/auth/user/playermoderations", body);
            ctx.Log($"ModerateUser({userId},{type}): {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"ModerateUser({userId},{type}) exception: {ex.Message}"); return false; }
    }

    public async Task<bool> UnmoderateUserAsync(string userId, string type)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var body = new StringContent(
                $"{{\"moderated\":\"{userId}\",\"type\":\"{type}\"}}",
                Encoding.UTF8, "application/json");
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/auth/user/unplayermoderate", body);
            ctx.Log($"UnmoderateUser({userId},{type}): {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"UnmoderateUser({userId},{type}) exception: {ex.Message}"); return false; }
    }

    public async Task<JObject?> GetMyReportsAsync(int n, int offset, bool closedOnly, string type)
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(ctx.CurrentUserId)) return null;
        try
        {
            var url = $"{VRChatApiService.BASE}/moderationReports?reportingUserId={ctx.CurrentUserId}&n={n}&offset={offset}";
            if (closedOnly) url += "&status=closed";
            if (!string.IsNullOrEmpty(type)) url += $"&type={Uri.EscapeDataString(type)}";
            var resp = await ctx._http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode) return JObject.Parse(body);
            ctx.Log($"GetMyReports {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        }
        catch (Exception ex) { ctx.Log($"GetMyReports exception: {ex.Message}"); }
        return null;
    }
}
