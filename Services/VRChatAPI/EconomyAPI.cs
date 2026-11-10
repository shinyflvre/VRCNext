using Newtonsoft.Json.Linq;

namespace VRCNext.Services;

public class EconomyAPI(VRChatApiService ctx)
{
    public async Task<int> GetBalanceAsync()
    {
        if (!ctx.IsLoggedIn) return -1;
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/user/{ctx.CurrentUserId}/balance");
            if (resp.IsSuccessStatusCode)
            {
                var obj = JObject.Parse(await resp.Content.ReadAsStringAsync());
                return obj["balance"]?.Value<int>() ?? obj["varBalance"]?.Value<int>() ?? -1;
            }
            ctx.Log($"GetBalance failed: {(int)resp.StatusCode} — {await resp.Content.ReadAsStringAsync()}");
        }
        catch (Exception ex) { ctx.Log($"GetBalance exception: {ex.Message}"); }
        return -1;
    }

    public async Task<JArray?> GetPurchasesAsync()
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(ctx.CurrentUserId)) return null;
        const int pageSize = 50;
        const int maxRows = 2000;
        var purchases = new JArray();
        try
        {
            for (var offset = 0; offset < maxRows; offset += pageSize)
            {
                var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/economy/purchases?buyerId={ctx.CurrentUserId}&n={pageSize}&offset={offset}&sort=purchaseDate&order=desc");
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    ctx.Log($"GetPurchases {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
                    return offset == 0 ? null : purchases;
                }
                var page = JArray.Parse(body);
                foreach (var row in page.OfType<JObject>())
                {
                    if (row["buyerId"]?.ToString() != ctx.CurrentUserId) continue;
                    var imageId = row["listingImageId"]?.ToString();
                    row["imageUrl"] = string.IsNullOrEmpty(imageId) ? "" : $"{VRChatApiService.BASE}/image/{imageId}/1/128";
                    purchases.Add(row);
                }
                if (page.Count < pageSize) break;
            }
            return purchases;
        }
        catch (Exception ex) { ctx.Log($"GetPurchases exception: {ex.Message}"); }
        return null;
    }

    public async Task<JArray?> GetCreditTransactionsAsync()
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(ctx.CurrentUserId)) return null;
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/user/{ctx.CurrentUserId}/economy/transactions?n=100");
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                ctx.Log($"GetCreditTransactions {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
                return null;
            }
            var token = JToken.Parse(body);
            if (token is JObject obj) return obj["transactions"] as JArray ?? new JArray();
            return token as JArray ?? new JArray();
        }
        catch (Exception ex) { ctx.Log($"GetCreditTransactions exception: {ex.Message}"); }
        return null;
    }

    public async Task<int> GetOnlineCountAsync()
    {
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/visits");
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode && int.TryParse(body.Trim(), out var count))
                return count;
        }
        catch (Exception ex) { ctx.Log($"GetOnlineCount exception: {ex.Message}"); }
        return 0;
    }
}
