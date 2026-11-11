using System.Text;
using Newtonsoft.Json.Linq;

namespace VRCNext.Services;

public class NotificationsAPI(VRChatApiService ctx)
{
    public async Task<JArray> GetNotificationsAsync()
    {
        if (!ctx.IsLoggedIn) return new JArray();
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/auth/user/notifications?n=100&hidden=false");
            if (resp.IsSuccessStatusCode)
            {
                var result = JArray.Parse(await resp.Content.ReadAsStringAsync());
                ctx.Log($"GetNotifications: got {result.Count} notifications");
                return result;
            }
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"GetNotifications failed: {(int)resp.StatusCode} — {body[..Math.Min(200, body.Length)]}");
        }
        catch (Exception ex) { ctx.Log($"GetNotifications exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JArray> GetNotificationsV2Async()
    {
        if (!ctx.IsLoggedIn || !ctx._notifV2Supported) return new JArray();
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/auth/user/notificationsV2?n=100");
            if (resp.IsSuccessStatusCode)
            {
                var result = JArray.Parse(await resp.Content.ReadAsStringAsync());
                ctx.Log($"GetNotificationsV2: got {result.Count} notifications");
                return result;
            }
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                ctx._notifV2Supported = false;
                ctx.Log("GetNotificationsV2: 404 — endpoint not available for this account, disabling v2");
            }
        }
        catch (Exception ex) { ctx.Log($"GetNotificationsV2 exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JArray> GetHiddenFriendRequestsAsync()
    {
        if (!ctx.IsLoggedIn) return new JArray();
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/auth/user/notifications?type=friendRequest&hidden=true&n=100");
            if (resp.IsSuccessStatusCode)
            {
                var result = JArray.Parse(await resp.Content.ReadAsStringAsync());
                ctx.Log($"GetHiddenFriendRequests: got {result.Count}");
                return result;
            }
        }
        catch (Exception ex) { ctx.Log($"GetHiddenFriendRequests exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<string?> FindFriendRequestIdAsync(string senderUserId)
    {
        if (!ctx.IsLoggedIn) return null;
        try
        {
            foreach (var hidden in new[] { "false", "true" })
            {
                var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/auth/user/notifications?type=friendRequest&hidden={hidden}&n=100");
                if (!resp.IsSuccessStatusCode) continue;
                var match = JArray.Parse(await resp.Content.ReadAsStringAsync())
                    .FirstOrDefault(n => n["senderUserId"]?.ToString() == senderUserId);
                if (match != null) return match["id"]?.ToString();
            }
        }
        catch (Exception ex) { ctx.Log($"FindFriendRequestId exception: {ex.Message}"); }
        return null;
    }

    public async Task<bool> AcceptNotificationAsync(string notifId)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/auth/user/notifications/{notifId}/accept", content);
            ctx.Log($"AcceptNotification {notifId}: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"AcceptNotification exception: {ex.Message}"); return false; }
    }

    public async Task<bool> HideNotificationAsync(string notifId, bool isV2 = false)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/auth/user/notifications/{notifId}/hide", content);
            ctx.Log($"HideNotification {notifId}: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"HideNotification exception: {ex.Message}"); return false; }
    }

    public async Task<bool> ClearNotificationsAsync()
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/auth/user/notifications/clear", content);
            ctx.Log($"ClearNotifications: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"ClearNotifications exception: {ex.Message}"); return false; }
    }

    public async Task<bool> MarkNotificationReadAsync(string notifId)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/auth/user/notifications/{notifId}/see", content);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"MarkNotifRead exception: {ex.Message}"); return false; }
    }
}
