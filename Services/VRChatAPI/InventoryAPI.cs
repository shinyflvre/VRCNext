using System.Collections.Concurrent;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VRCNext.Services.Helpers;

namespace VRCNext.Services;

public class InventoryAPI(VRChatApiService ctx)
{
    private static readonly ConcurrentDictionary<string, Task> _decoResolving = new();
    public static readonly TimeSpan DecorationTtl = TimeSpan.FromDays(7);

    private static readonly HashSet<string> DecorationSlots = new() { "iconFrame", "nameplateEffect", "profileEffect" };

    private static readonly ConcurrentDictionary<string, JObject> _cosmeticIndex = new();
    private static readonly object _cosmeticIndexLock = new();
    private static Task? _cosmeticIndexLoad;

    private static string? ClassifyDecorationSlot(JObject item)
    {
        var type = item["itemType"]?.ToString() ?? "";
        if (DecorationSlots.Contains(type)) return type;
        if (item["equipSlots"] is JArray es)
            foreach (var s in es) { var v = s?.ToString() ?? ""; if (DecorationSlots.Contains(v)) return v; }
        return null;
    }

    public async Task<List<JObject>> GetOwnDecorationsAsync()
    {
        var result = new List<JObject>();
        if (!ctx.IsLoggedIn) return result;
        for (int offset = 0; offset < 500; offset += 100)
        {
            var (items, _) = await GetInventoryItemsAsync(100, offset);
            if (items.Count == 0) break;
            foreach (var it in items.OfType<JObject>())
            {
                var slot = ClassifyDecorationSlot(it);
                if (slot == null) continue;
                it["__slot"] = slot;
                result.Add(it);
            }
            if (items.Count < 100) break;
        }
        return result;
    }

    public async Task<bool> SetProfileDecorationAsync(string field, string value)
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(ctx.CurrentUserId)) return false;
        if (!DecorationSlots.Contains(field)) return false;
        try
        {
            var json = JsonConvert.SerializeObject(new Dictionary<string, string> { [field] = value ?? "" });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/profile/{ctx.CurrentUserId}", content);
            ctx.Log($"SetProfileDecoration [{field}={value}]: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"SetProfileDecoration exception: {ex.Message}"); return false; }
    }

    public async Task<JObject?> GetInventoryTemplateAsync(string templateId)
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(templateId)) return null;
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/inventory/template/{Uri.EscapeDataString(templateId)}");
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"GetInventoryTemplate {templateId}: {(int)resp.StatusCode} len={body.Length}");
            if (!resp.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body)) return null;
            return JObject.Parse(body);
        }
        catch (Exception ex) { ctx.Log($"GetInventoryTemplate exception: {ex.Message}"); return null; }
    }

    public static string? ExtractDecorationAssetUrl(JObject? tpl)
    {
        if (tpl == null) return null;
        if (tpl["metadata"]?["assets"] is JArray assets)
        {
            string? Pick(string type) => assets.FirstOrDefault(a => a["type"]?.ToString() == type)?["url"]?.ToString();
            var url = Pick("mainAnimation") ?? Pick("base");
            if (!string.IsNullOrEmpty(url)) return url;
        }
        return tpl["imageUrl"]?.ToString();
    }

    private Task EnsureCosmeticIndexAsync()
    {
        if (!ctx.IsLoggedIn) return Task.CompletedTask;
        lock (_cosmeticIndexLock)
        {
            _cosmeticIndexLoad ??= LoadCosmeticIndexAsync();
            return _cosmeticIndexLoad;
        }
    }

    private async Task LoadCosmeticIndexAsync()
    {
        foreach (var itemType in DecorationSlots)
        {
            try
            {
                var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/cosmetics/index/{itemType}");
                var body = await resp.Content.ReadAsStringAsync();
                ctx.Log($"CosmeticIndex [{itemType}]: {(int)resp.StatusCode} len={body.Length}");
                if (!resp.IsSuccessStatusCode || JToken.Parse(body) is not JArray arr) continue;
                foreach (var entry in arr.OfType<JObject>())
                {
                    var id = entry["id"]?.ToString();
                    if (!string.IsNullOrEmpty(id)) _cosmeticIndex[id] = entry;
                }
            }
            catch (Exception ex) { ctx.Log($"CosmeticIndex [{itemType}] exception: {ex.Message}"); }
        }
        ctx.Log($"CosmeticIndex: {_cosmeticIndex.Count} entries cached");
        if (_cosmeticIndex.IsEmpty)
            lock (_cosmeticIndexLock) _cosmeticIndexLoad = null;
    }

    public Task ResolveDecorationAsync(string? templateId)
    {
        if (string.IsNullOrEmpty(templateId)) return Task.CompletedTask;
        if (ImageCacheHelper.IsVrcPlusFresh(templateId, DecorationTtl)) return Task.CompletedTask;
        return _decoResolving.GetOrAdd(templateId, id =>
            ResolveDecorationInnerAsync(id).ContinueWith(_ => { _decoResolving.TryRemove(id, out _); }, TaskScheduler.Default));
    }

    private async Task ResolveDecorationInnerAsync(string templateId)
    {
        try
        {
            await EnsureCosmeticIndexAsync();
            var tpl = _cosmeticIndex.TryGetValue(templateId, out var indexed)
                ? indexed
                : await GetInventoryTemplateAsync(templateId);
            var url = ExtractDecorationAssetUrl(tpl);
            if (!string.IsNullOrEmpty(url))
            {
                bool exists = ImageCacheHelper.GetVrcPlusCached(templateId) != null;
                await ImageCacheHelper.CacheVrcPlusAsync(templateId, url, forceRefresh: exists);
            }
        }
        catch (Exception ex) { ctx.Log($"ResolveDecoration exception: {ex.Message}"); }
    }

    public async Task<(JArray items, int totalCount)> GetInventoryItemsAsync(int n = 100, int offset = 0)
    {
        if (!ctx.IsLoggedIn) return (new JArray(), 0);
        try
        {
            var url = $"{VRChatApiService.BASE}/inventory?n={n}&offset={offset}";
            ctx.Log($"GetInventoryItems n={n} offset={offset}");
            var resp = await ctx._http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"GetInventoryItems response: {(int)resp.StatusCode} len={body.Length} preview={body[..Math.Min(300, body.Length)]}");
            if (resp.IsSuccessStatusCode)
            {
                var token = JToken.Parse(body);
                if (token is JArray arr) return (arr, arr.Count);
                if (token is JObject obj)
                {
                    var data  = obj["data"] as JArray ?? new JArray();
                    var total = obj["totalCount"]?.Value<int>() ?? data.Count;
                    return (data, total);
                }
            }
            else ctx.Log($"GetInventoryItems error: {body[..Math.Min(200, body.Length)]}");
        }
        catch (Exception ex) { ctx.Log($"GetInventoryItems exception: {ex.Message}"); }
        return (new JArray(), 0);
    }

    public async Task<JArray> GetUserPrintsAsync(string userId)
    {
        if (!ctx.IsLoggedIn) return new JArray();

        const int pageSize = 100;
        const int maxPages = 20;
        var all = new JArray();

        for (int page = 0; page < maxPages; page++)
        {
            int offset = page * pageSize;
            try
            {
                var url = $"{VRChatApiService.BASE}/prints/user/{Uri.EscapeDataString(userId)}?n={pageSize}&offset={offset}";
                ctx.Log($"GetUserPrints userId={userId} page={page} offset={offset}");
                var resp = await ctx._http.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();
                ctx.Log($"GetUserPrints response: {(int)resp.StatusCode} len={body.Length}");
                if (!resp.IsSuccessStatusCode) break;

                JArray pageArr;
                var token = JToken.Parse(body);
                if      (token is JArray a)  pageArr = a;
                else if (token is JObject o) pageArr = o["prints"] as JArray ?? o["data"] as JArray ?? new JArray();
                else break;

                foreach (var item in pageArr) all.Add(item);
                if (pageArr.Count < pageSize) break;
            }
            catch (Exception ex) { ctx.Log($"GetUserPrints exception: {ex.Message}"); break; }
        }

        ctx.Log($"GetUserPrints total fetched: {all.Count}");
        return all;
    }

    public async Task<(bool ok, JObject? print, string error)> UploadPrintAsync(
        byte[] png, string note, DateTime timestampUtc, string worldId, string worldName)
    {
        if (!ctx.IsLoggedIn) return (false, null, "Not logged in");
        try
        {
            using var form = new MultipartFormDataContent();
            var image = new ByteArrayContent(png);
            image.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("image/png");
            form.Add(image, "image", "print.png");
            form.Add(new StringContent(timestampUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture)), "timestamp");
            if (!string.IsNullOrWhiteSpace(note))      form.Add(new StringContent(note), "note");
            if (!string.IsNullOrWhiteSpace(worldId))   form.Add(new StringContent(worldId), "worldId");
            if (!string.IsNullOrWhiteSpace(worldName)) form.Add(new StringContent(worldName), "worldName");

            ctx.Log($"UploadPrint size={png.Length} world={worldId}");
            var resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/prints", form);
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"UploadPrint [{(int)resp.StatusCode}] preview={body[..Math.Min(200, body.Length)]}");
            if (resp.IsSuccessStatusCode) return (true, JObject.Parse(body), "");
            return (false, null, VRChatApiService.TryGetApiError(body) ?? $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) { ctx.Log($"UploadPrint exception: {ex.Message}"); return (false, null, ex.Message); }
    }

    public async Task<bool> DeletePrintAsync(string printId)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var resp = await ctx._http.DeleteAsync($"{VRChatApiService.BASE}/prints/{printId}");
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"DeletePrint {printId}: {(int)resp.StatusCode} body={body[..Math.Min(300, body.Length)]}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"DeletePrint exception: {ex.Message}"); return false; }
    }
}
