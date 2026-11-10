using System.Text;
using Newtonsoft.Json.Linq;

namespace VRCNext.Services;

public class FriendsAPI(VRChatApiService ctx)
{
    public Task<List<JObject>> GetOnlineFriendsAsync()  => FetchFriendsParallelAsync(offline: false);
    public Task<List<JObject>> GetOfflineFriendsAsync() => FetchFriendsParallelAsync(offline: true);

    private async Task<List<JObject>> FetchFriendsParallelAsync(bool offline)
    {
        var all = new List<JObject>();
        if (!ctx.IsLoggedIn) return all;

        const int pageSize   = 50;
        const int concurrency = 5;
        const int maxOffset  = 7500;
        const int maxRetry   = 5;

        var nextOffset = 0;
        var done = false;
        var lck  = new object();
        var label       = offline ? "offline" : "online";
        var offlineBool = offline.ToString().ToLower(); 
        async Task Worker()
        {
            while (true)
            {
                int myOffset;
                lock (lck)
                {
                    if (done) return;
                    if (nextOffset > maxOffset) { done = true; return; }
                    myOffset    = nextOffset;
                    nextOffset += pageSize;
                }

                JArray? batch = null;
                for (var attempt = 1; attempt <= maxRetry; attempt++)
                {
                    try
                    {
                        var resp = await ctx._http.GetAsync(
                            $"{VRChatApiService.BASE}/auth/user/friends?offline={offlineBool}&n={pageSize}&offset={myOffset}");
                        var body = await resp.Content.ReadAsStringAsync();
                        ctx.Log($"Friends ({label}) offset={myOffset} → {(int)resp.StatusCode}");

                        if ((int)resp.StatusCode == 429)
                        {
                            var delay = (int)Math.Pow(2, attempt) * 1000;
                            ctx.Log($"Friends rate limited, retry {attempt}/{maxRetry} in {delay}ms");
                            await Task.Delay(delay);
                            continue;
                        }
                        if (!resp.IsSuccessStatusCode)
                        {
                            ctx.Log($"Friends error at offset={myOffset}: {body[..Math.Min(200, body.Length)]}");
                            if (attempt < maxRetry) { await Task.Delay(1000 * attempt); continue; }
                            lock (lck) done = true;
                            return;
                        }

                        batch = JArray.Parse(body);
                        break;
                    }
                    catch (Exception ex)
                    {
                        ctx.Log($"Friends worker offset={myOffset} attempt {attempt}: {ex.Message}");
                        if (attempt < maxRetry) { await Task.Delay(1000 * attempt); continue; }
                        lock (lck) done = true;
                        return;
                    }
                }

                if (batch == null) return;

                ctx.Log($"Friends ({label}) batch at {myOffset}: {batch.Count}");

                if (batch.Count == 0)
                {
                    lock (lck) done = true;
                    return;
                }

                lock (all) foreach (var x in batch) all.Add((JObject)x);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => Worker()));
        ctx.Log($"Total {label} friends fetched: {all.Count}");
        return all;
    }

    public async Task<bool> SendFriendRequestAsync(string userId)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/user/{userId}/friendRequest", content);
            ctx.Log($"FriendRequest {userId}: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"FriendRequest exception: {ex.Message}"); return false; }
    }

    public async Task<JObject?> GetFriendStatusAsync(string userId)
    {
        if (!ctx.IsLoggedIn) return null;
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/user/{userId}/friendStatus");
            if (resp.IsSuccessStatusCode) return JObject.Parse(await resp.Content.ReadAsStringAsync());
            ctx.Log($"GetFriendStatus {userId}: {(int)resp.StatusCode}");
        }
        catch (Exception ex) { ctx.Log($"GetFriendStatus exception: {ex.Message}"); }
        return null;
    }

    public async Task<bool> CancelFriendRequestAsync(string userId)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var resp = await ctx._http.DeleteAsync($"{VRChatApiService.BASE}/user/{userId}/friendRequest");
            ctx.Log($"CancelFriendRequest {userId}: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"CancelFriendRequest exception: {ex.Message}"); return false; }
    }

    public async Task<bool> UnfriendAsync(string userId)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var resp = await ctx._http.DeleteAsync($"{VRChatApiService.BASE}/auth/user/friends/{userId}");
            ctx.Log($"Unfriend {userId}: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"Unfriend exception: {ex.Message}"); return false; }
    }
}
