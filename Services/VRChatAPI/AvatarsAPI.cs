using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VRCNext.Services;

public class AvatarsAPI(VRChatApiService ctx)
{
    private const string UA = AppInfo.UserAgent;

    public async Task<JObject?> GetAvatarAsync(string avatarId)
    {
        if (!ctx.IsLoggedIn) return null;
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/avatars/{avatarId}");
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
            {
                var avatar = JObject.Parse(body);
                RememberAvatarFile(avatar);
                return avatar;
            }
            ctx.Log($"GetAvatar {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        }
        catch (Exception ex) { ctx.Log($"GetAvatar exception: {ex.Message}"); }
        return null;
    }

    public async Task<(int status, JObject? data)> GetFileAnalysisAsync(string fileId, int version, string variant = "security")
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(fileId)) return (0, null);
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/analysis/{Uri.EscapeDataString(fileId)}/{version}/{Uri.EscapeDataString(variant)}");
            var body = await resp.Content.ReadAsStringAsync();
            var status = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode && body.TrimStart().StartsWith("{"))
            {
                try { return (status, JObject.Parse(body)); } catch { return (status, null); }
            }
            if (!resp.IsSuccessStatusCode) ctx.Log($"GetFileAnalysis {status}: {body[..Math.Min(200, body.Length)]}");
            return (status, null);
        }
        catch (Exception ex) { ctx.Log($"GetFileAnalysis exception: {ex.Message}"); }
        return (0, null);
    }

    public async Task<(bool ok, string error)> UpdateAvatarAsync(string avatarId, string name, string description, string releaseStatus, List<string> tags)
    {
        if (!ctx.IsLoggedIn) return (false, "Not logged in");
        try
        {
            var body = JsonConvert.SerializeObject(new { name, description, releaseStatus, tags });
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/avatars/{avatarId}", content);
            var respBody = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode) return (true, "");
            ctx.Log($"UpdateAvatar {(int)resp.StatusCode}: {respBody[..Math.Min(200, respBody.Length)]}");
            return (false, $"API error {(int)resp.StatusCode}");
        }
        catch (Exception ex) { ctx.Log($"UpdateAvatar exception: {ex.Message}"); return (false, ex.Message); }
    }

    public async Task<bool> SelectAvatarAsync(string avatarId)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/avatars/{avatarId}/select", content);
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"SelectAvatar {avatarId}: {(int)resp.StatusCode}");
            if (resp.IsSuccessStatusCode)
            {
                try { ctx.CurrentUserRaw = JObject.Parse(body); } catch { }
                return true;
            }
            return false;
        }
        catch (Exception ex) { ctx.Log($"SelectAvatar exception: {ex.Message}"); return false; }
    }

    public async Task<List<JObject>> GetOwnAvatarsAsync()
    {
        if (!ctx.IsLoggedIn) return new();
        var all = new List<JObject>();
        try
        {
            for (int offset = 0; offset < 10000; offset += 50)
            {
                var resp = await ctx._http.GetAsync(
                    $"{VRChatApiService.BASE}/avatars?user=me&releaseStatus=all&n=50&offset={offset}&sort=updated&order=descending");
                if (!resp.IsSuccessStatusCode) break;
                var arr = JArray.Parse(await resp.Content.ReadAsStringAsync());
                if (arr.Count == 0) break;
                all.AddRange(arr.Cast<JObject>().Where(a => !IsHidden(a)));
                if (arr.Count < 50) break;
                await Task.Delay(300);
            }
            ctx.Log($"GetOwnAvatars: found {all.Count}");
        }
        catch (Exception ex) { ctx.Log($"GetOwnAvatars exception: {ex.Message}"); }
        return all;
    }

    internal static bool IsHidden(JObject item)
        => string.Equals(item?["releaseStatus"]?.ToString(), "hidden", StringComparison.OrdinalIgnoreCase);

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

    public async Task<(bool ok, string result)> AddAvatarFavoriteAsync(string avatarId, string groupName, string groupType = "avatar", string? oldFvrtId = null)
    {
        if (!ctx.IsLoggedIn) return (false, "Not logged in");
        try
        {
            if (!string.IsNullOrEmpty(oldFvrtId))
            {
                await ctx._http.DeleteAsync($"{VRChatApiService.BASE}/favorites/{oldFvrtId}");
                await Task.Delay(400);
            }
            var json = JsonConvert.SerializeObject(new { type = groupType, favoriteId = avatarId, tags = new[] { groupName } });
            var resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/favorites",
                new StringContent(json, Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                var errMsg = VRChatApiService.TryGetApiError(body) ?? $"HTTP {(int)resp.StatusCode}";
                ctx.Log($"AddAvatarFavorite error: {errMsg}");
                return (false, errMsg);
            }
            var result = JObject.Parse(body);
            var newFvrtId = result["id"]?.ToString() ?? "";
            return (true, newFvrtId);
        }
        catch (Exception ex) { ctx.Log($"AddAvatarFavorite exception: {ex.Message}"); return (false, ex.Message); }
    }

    public async Task<JArray> SearchAvatarsAsync(string query, int n = 20, int page = 0)
    {
        var url = $"https://api.avtrdb.com/v2/avatar/search?query={Uri.EscapeDataString(query)}&limit={n}&page={page}";
        using var client = new HttpClient();
        client.DefaultRequestVersion = System.Net.HttpVersion.Version20;
        client.DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionExact;
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UA);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        try
        {
            ctx.Log("[AVTRDB] GET search");
            ctx.Log($"SearchAvatars: {url}");
            var resp = await client.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"SearchAvatars [{(int)resp.StatusCode}] len={body.Length}");
            if (!resp.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body)) return new JArray();
            var parsed = JToken.Parse(body);
            if (parsed is JObject obj && obj["avatars"] is JArray arr) return arr;
            if (parsed is JArray directArr) return directArr;
        }
        catch (Exception ex) { ctx.Log($"SearchAvatars exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JArray> SearchAvatarsAvtrIcuAsync(string query, int n = 20, int offset = 0)
    {
        var url = $"https://avtr.icu/search?search={Uri.EscapeDataString(query)}&limit={n}&offset={offset}";
        using var client = new HttpClient();
        client.DefaultRequestVersion = System.Net.HttpVersion.Version20;
        client.DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower;
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UA);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        try
        {
            ctx.Log($"SearchAvatarsAvtrIcu: {url}");
            var resp = await client.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"SearchAvatarsAvtrIcu [{(int)resp.StatusCode}] len={body.Length}");
            if (!resp.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body)) return new JArray();
            var parsed = JToken.Parse(body);
            if (parsed is JArray arr) return arr;
        }
        catch (Exception ex) { ctx.Log($"SearchAvatarsAvtrIcu exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JArray> SearchAvatarsVrcnAsync(string query, int n = 20, int page = 0,
        string platform = "", string perf = "", string content = "", bool ft = false)
    {
        query = (query ?? "").Trim();
        // Avatar ids aren't in the search index; look them up directly like the website does.
        if (System.Text.RegularExpressions.Regex.IsMatch(query, @"^avtr_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"))
            return page > 0 ? new JArray() : await GetAvatarVrcnByIdAsync(query);

        var qs = new List<string> { $"limit={n}", $"page={page + 1}" };
        if (System.Text.RegularExpressions.Regex.IsMatch(query, @"^usr_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"))
            qs.Add($"author={Uri.EscapeDataString(query)}");
        else if (query != "")
            qs.Add($"q={Uri.EscapeDataString(query)}");
        if (!string.IsNullOrEmpty(platform)) qs.Add($"platform={Uri.EscapeDataString(platform)}");
        if (!string.IsNullOrEmpty(perf))     qs.Add($"perf={Uri.EscapeDataString(perf)}");
        if (!string.IsNullOrEmpty(content))  qs.Add($"content={Uri.EscapeDataString(content)}");
        if (ft)                              qs.Add("ft=1");

        var url = "https://db.vrcnext.com/api/search.php?" + string.Join("&", qs);
        using var client = new HttpClient();
        client.DefaultRequestVersion = System.Net.HttpVersion.Version20;
        client.DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower;
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UA);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        try
        {
            ctx.Log($"SearchAvatarsVrcn: {url}");
            var resp = await client.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"SearchAvatarsVrcn [{(int)resp.StatusCode}] len={body.Length}");
            if (!resp.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body)) return new JArray();
            var parsed = JToken.Parse(body);
            if (parsed is JObject obj && obj["results"] is JArray arr) return arr;
        }
        catch (Exception ex) { ctx.Log($"SearchAvatarsVrcn exception: {ex.Message}"); }
        return new JArray();
    }

    // Direct id lookup against VRCNDb (avatar.php returns the same card shape as search).
    public async Task<JArray> GetAvatarVrcnByIdAsync(string avatarId)
    {
        var url = $"https://db.vrcnext.com/api/avatar.php?id={Uri.EscapeDataString(avatarId)}";
        using var client = new HttpClient();
        client.DefaultRequestVersion = System.Net.HttpVersion.Version20;
        client.DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower;
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UA);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        try
        {
            ctx.Log($"GetAvatarVrcnById: {url}");
            var resp = await client.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"GetAvatarVrcnById [{(int)resp.StatusCode}] len={body.Length}");
            if (!resp.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body)) return new JArray();
            var parsed = JToken.Parse(body);
            if (parsed is JObject obj && obj["id"] != null) return new JArray { obj };
        }
        catch (Exception ex) { ctx.Log($"GetAvatarVrcnById exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JArray> SearchSimilarAvatarsAvtrIcuAsync(string avatarId, int n = 20)
    {
        var url = $"https://avtr.icu/similar/{Uri.EscapeDataString(avatarId)}?limit={n}";
        using var client = new HttpClient();
        client.DefaultRequestVersion = System.Net.HttpVersion.Version20;
        client.DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower;
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UA);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        try
        {
            ctx.Log($"SearchSimilarAvtrIcu: {url}");
            var resp = await client.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"SearchSimilarAvtrIcu [{(int)resp.StatusCode}] len={body.Length}");
            if (!resp.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body)) return new JArray();
            var parsed = JToken.Parse(body);
            if (parsed is JArray arr) return arr;
        }
        catch (Exception ex) { ctx.Log($"SearchSimilarAvtrIcu exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JArray> SearchAvatarsByAuthorAsync(string authorId, int n = 50)
    {
        var all = new JArray();
        var seen = new HashSet<string>();
        using var client = new HttpClient();
        client.DefaultRequestVersion = System.Net.HttpVersion.Version20;
        client.DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionExact;
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UA);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        // kubectl db ignores the requested limit and answers with has more thus the pagesize.
        // the comparison should be only fallback for the array response shape. might check later to change.
        int pageSize = -1;
        for (int page = 0; page < 25; page++)
        {
            var url = $"https://api.avtrdb.com/v2/avatar/search?query={Uri.EscapeDataString(authorId)}&limit={n}&page={page}";
            JArray? arr = null;
            bool? hasMore = null;
            try
            {
                ctx.Log("[AVTRDB] GET author-search");
                ctx.Log($"SearchAvatarsByAuthor: {url}");
                var resp = await client.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();
                ctx.Log($"SearchAvatarsByAuthor [{(int)resp.StatusCode}] len={body.Length}");
                if (!resp.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body)) break;
                var parsed = JToken.Parse(body);
                var obj = parsed as JObject;
                arr = obj?["avatars"] as JArray ?? parsed as JArray;
                if (obj?["has_more"] is JValue hm && hm.Type != JTokenType.Null) hasMore = hm.Value<bool>();
            }
            catch (Exception ex) { ctx.Log($"SearchAvatarsByAuthor exception: {ex.Message}"); break; }

            if (arr == null || arr.Count == 0) break;

            foreach (var item in arr)
            {
                var id = item["vrc_id"]?.ToString() ?? item["id"]?.ToString() ?? "";
                if (id.Length > 0 && !seen.Add(id)) continue;
                all.Add(item);
            }

            if (hasMore.HasValue)
            {
                if (!hasMore.Value) break;
            }
            else
            {
                if (pageSize >= 0 && arr.Count < pageSize) break;
                if (arr.Count > pageSize) pageSize = arr.Count;
            }

            await Task.Delay(200);
        }
        return all;
    }

    private static readonly AvtrdbResolver _avtrdbResolver = new();
    private static readonly IcuResolver    _icuResolver    = new();
    private static readonly VrcndbResolver _vrcndbResolver = new();

    private const string RobotAvatarId = "avtr_c38a1615-5bf5-42b4-84eb-a8b6c37cbd11";

    private static (string? id, JObject? data) MapResolvedIcu(JObject? o)
    {
        if (o == null) return (null, null);
        var id = o["id"]?.ToString();
        if (string.IsNullOrEmpty(id) || id == RobotAvatarId) return (null, null);

        var mapped = new JObject
        {
            ["id"]            = id,
            ["name"]          = o["name"],
            ["description"]   = o["description"],
            ["imageUrl"]      = o["imageUrl"],
            ["authorName"]    = o["authorName"],
            ["authorId"]      = o["authorId"],
            ["created_at"]    = o["created_at"],
            ["updated_at"]    = o["updated_at"],
            ["compatibility"] = o["platforms"],
            ["performance"]   = o["performanceRating"],
            ["tags"]          = o["tags"],
            ["styles"]        = o["styles"],
            ["explicit"]      = o["explicit"],
        };
        return (id, mapped);
    }

    private static (string? id, JObject? data) MapResolved(JObject? o)
    {
        if (o == null) return (null, null);
        var id = o["vrc_id"]?.ToString();
        if (string.IsNullOrEmpty(id) || id == RobotAvatarId) return (null, null);

        var mapped = new JObject
        {
            ["id"]          = id,
            ["name"]        = o["name"],
            ["description"] = o["description"],
            ["imageUrl"]    = o["image_url"],
            ["authorName"]  = o["author"]?["name"],
            ["authorId"]    = o["author"]?["vrc_id"],
            ["created_at"]  = o["created_at"],
            ["updated_at"]  = o["updated_at"],
            ["compatibility"] = o["compatibility"],
            ["performance"] = o["performance"],
            ["tags"]        = o["tags"],
            ["styles"]      = o["styles"],
            ["explicit"]    = o["explicit"],
        };
        return (id, mapped);
    }

    private static readonly System.Text.RegularExpressions.Regex _imageFileIdRx =
        new(@"(file_[a-f0-9\-]{36})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static void RememberAvatarFile(JObject? avatar)
    {
        var id = avatar?["id"]?.ToString() ?? "";
        if (!id.StartsWith("avtr_")) return;
        var img = avatar!["imageUrl"]?.ToString() ?? avatar["thumbnailImageUrl"]?.ToString() ?? "";
        var m = _imageFileIdRx.Match(img);
        if (!m.Success) return;
        Helpers.AvtrdbCacheHelper.SaveFileAvatar(m.Groups[1].Value, id,
            avatar["name"]?.ToString()       ?? "",
            avatar["authorName"]?.ToString() ?? "",
            avatar["authorId"]?.ToString()   ?? "",
            img, "vrchat");
    }

    private static (string? id, JObject? data) FromFileCache(Helpers.AvtrdbCacheHelper.FileAvatarEntry c)
    {
        if (string.IsNullOrEmpty(c.AvtrId) && string.IsNullOrEmpty(c.Name)) return (null, null);
        var data = new JObject
        {
            ["id"]         = c.AvtrId,
            ["name"]       = c.Name,
            ["imageUrl"]   = c.ImageUrl,
            ["authorName"] = c.AuthorName,
            ["authorId"]   = c.AuthorId,
        };
        return (string.IsNullOrEmpty(c.AvtrId) ? null : c.AvtrId, data);
    }

    private static void RememberFile(string fileId, string source, (string? id, JObject? data) res)
    {
        if (string.IsNullOrEmpty(fileId)) return;
        var d = res.data;
        Helpers.AvtrdbCacheHelper.SaveFileAvatar(fileId,
            res.id ?? "",
            d?["name"]?.ToString()       ?? "",
            d?["authorName"]?.ToString() ?? "",
            d?["authorId"]?.ToString()   ?? "",
            d?["imageUrl"]?.ToString()   ?? "",
            source);
    }

    private static readonly System.Text.RegularExpressions.Regex _fileAvatarNameRx =
        new(@"Avatar - (.*) - (?:Image|Asset bundle|Unity package) -", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private async Task<(string? id, JObject? data)> ResolveByVrcFileAsync(string fileId)
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(fileId)) return (null, null);
        try
        {
            ctx.Log($"[FILE] resolve {fileId}");
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/file/{fileId}");
            if (!resp.IsSuccessStatusCode)
            {
                ctx.Log($"[FILE] {fileId} -> HTTP {(int)resp.StatusCode}");
                return (null, null);
            }
            var obj = JObject.Parse(await resp.Content.ReadAsStringAsync());
            var rawName = obj["name"]?.ToString() ?? "";
            var m = _fileAvatarNameRx.Match(rawName);
            if (!m.Success)
            {
                ctx.Log($"[FILE] {fileId} -> no avatar name in '{rawName}'");
                return (null, null);
            }
            var name = m.Groups[1].Value.Trim();
            if (name.Length == 0)
            {
                ctx.Log($"[FILE] {fileId} -> empty name in '{rawName}'");
                return (null, null);
            }
            ctx.Log($"[FILE] {fileId} -> '{name}'");
            return (null, new JObject
            {
                ["id"]         = "",
                ["name"]       = name,
                ["imageUrl"]   = "",
                ["authorName"] = "",
                ["authorId"]   = obj["ownerId"]?.ToString() ?? "",
            });
        }
        catch (Exception ex) { ctx.Log($"[FILE] {fileId} exception: {ex.Message}"); return (null, null); }
    }

    public async Task<(string? id, JObject? data)> GetAvatarIdByFileIdAsync(string fileId)
    {
        if (AvtrdbResolver.IsPlaceholderFileId(fileId))
        {
            ctx.Log($"[FILE] {fileId} is the hidden avatar placeholder, nothing to resolve");
            return (null, null);
        }
        var cached = Helpers.AvtrdbCacheHelper.GetFileAvatar(fileId);
        if (cached != null) return FromFileCache(cached);
        try
        {
            var res = MapResolved(await _avtrdbResolver.ResolveAsync(fileId));
            if (res.id != null) { RememberFile(fileId, "avtrdb", res); return res; }

            var icu = MapResolvedIcu(await _icuResolver.ResolveAsync(fileId));
            if (icu.id != null)
            {
                ctx.Log($"icu fallback resolved {fileId} -> {icu.id}");
                RememberFile(fileId, "icu", icu);
                return icu;
            }

            var fb = MapResolved(await _vrcndbResolver.ResolveAsync(fileId));
            if (fb.id != null)
            {
                ctx.Log($"vrcndb fallback resolved {fileId} -> {fb.id}");
                RememberFile(fileId, "vrcndb", fb);
                return fb;
            }

            ctx.Log($"[FILE] {fileId} unknown to avtrdb, avtr.icu and vrcndb, asking VRChat");
            var file = await ResolveByVrcFileAsync(fileId);
            RememberFile(fileId, file.data != null ? "vrcfile" : "none", file);
            return file;
        }
        catch (Exception ex) { ctx.Log($"GetAvatarIdByFileId exception: {ex.Message}"); }
        return (null, null);
    }

    public async Task<(string? id, JObject? data)> ResolveByFileIdSourceAsync(string source, string fileId)
    {
        try
        {
            var picked = source switch
            {
                "avtrdb" => MapResolved(await _avtrdbResolver.ResolveDirectAsync(fileId)),
                "icu"    => MapResolvedIcu(await _icuResolver.ResolveDirectAsync(fileId)),
                "vrcndb" => MapResolved(await _vrcndbResolver.ResolveDirectAsync(fileId)),
                _        => await GetAvatarIdByFileIdAsync(fileId),
            };
            if (picked.id != null) RememberFile(fileId, source, picked);
            return picked;
        }
        catch (Exception ex) { ctx.Log($"ResolveByFileIdSource({source}) exception: {ex.Message}"); }
        return (null, null);
    }

    public async Task<Dictionary<string, (string? id, JObject? data)>> GetAvatarIdsByFileIdsAsync(IEnumerable<string> fileIds)
    {
        var result = new Dictionary<string, (string? id, JObject? data)>();
        try
        {
            var pending = new List<string>();
            foreach (var f in fileIds.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct())
            {
                if (AvtrdbResolver.IsPlaceholderFileId(f)) continue;
                var hit = Helpers.AvtrdbCacheHelper.GetFileAvatar(f);
                if (hit != null) result[f] = FromFileCache(hit);
                else pending.Add(f);
            }
            if (pending.Count == 0) return result;

            var raw = await _avtrdbResolver.ResolveManyAsync(pending);
            foreach (var kv in raw)
            {
                var mapped = MapResolved(kv.Value);
                result[kv.Key] = mapped;
                if (mapped.id != null) RememberFile(kv.Key, "avtrdb", mapped);
            }

            var missing = pending.Where(f => result[f].id == null).ToList();
            if (missing.Count > 0)
            {
                var icu = await _icuResolver.ResolveManyAsync(missing);
                foreach (var kv in icu)
                {
                    var mapped = MapResolvedIcu(kv.Value);
                    if (mapped.id == null) continue;
                    result[kv.Key] = mapped;
                    RememberFile(kv.Key, "icu", mapped);
                    ctx.Log($"icu fallback resolved {kv.Key} -> {mapped.id}");
                }
            }

            missing = pending.Where(f => result[f].id == null).ToList();
            if (missing.Count > 0)
            {
                var fb = await _vrcndbResolver.ResolveManyAsync(missing);
                foreach (var kv in fb)
                {
                    var mapped = MapResolved(kv.Value);
                    if (mapped.id == null) continue;
                    result[kv.Key] = mapped;
                    RememberFile(kv.Key, "vrcndb", mapped);
                    ctx.Log($"vrcndb fallback resolved {kv.Key} -> {mapped.id}");
                }
            }

            foreach (var f in pending.Where(f => result[f].id == null))
                RememberFile(f, "none", (null, null));
        }
        catch (Exception ex) { ctx.Log($"GetAvatarIdsByFileIds exception: {ex.Message}"); }
        return result;
    }

    public async Task<JArray> GetAvatarGalleryAsync(string avatarId)
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(avatarId)) return new JArray();
        try
        {
            var url = $"{VRChatApiService.BASE}/files?tag=avatargallery&galleryId={Uri.EscapeDataString(avatarId)}&n=100";
            ctx.Log($"GetAvatarGallery avatarId={avatarId}");
            var resp = await ctx._http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"GetAvatarGallery [{(int)resp.StatusCode}] len={body.Length}");
            if (resp.IsSuccessStatusCode) return JArray.Parse(body);
            ctx.Log($"GetAvatarGallery error: {body[..Math.Min(200, body.Length)]}");
        }
        catch (Exception ex) { ctx.Log($"GetAvatarGallery exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<(bool ok, string error)> UploadAvatarGalleryImageAsync(string avatarId, byte[] bytes)
    {
        if (!ctx.IsLoggedIn) return (false, "Not logged in");
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("avatargallery"), "tag");
            form.Add(new StringContent(avatarId), "galleryId");
            var fileContent = new ByteArrayContent(bytes);
            fileContent.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("image/png");
            form.Add(fileContent, "file", "gallery.png");
            ctx.Log($"UploadAvatarGalleryImage avatarId={avatarId} size={bytes.Length}");
            var resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/file/image", form);
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"UploadAvatarGalleryImage [{(int)resp.StatusCode}] preview={body[..Math.Min(200, body.Length)]}");
            if (resp.IsSuccessStatusCode) return (true, "");
            return (false, VRChatApiService.TryGetApiError(body) ?? $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) { ctx.Log($"UploadAvatarGalleryImage exception: {ex.Message}"); return (false, ex.Message); }
    }

    public Task<(bool ok, string imageUrl, string error)> UploadAvatarMainImageAsync(
        string avatarId, string existingImageUrl, byte[] imageBytes)
        => FilesAPI.ReplaceEntityImageAsync(ctx, "avatars", avatarId, existingImageUrl, imageBytes);

    public async Task<(bool ok, string error)> DeleteAvatarAsync(string avatarId)
    {
        if (!ctx.IsLoggedIn) return (false, "Not logged in");
        try
        {
            var resp = await ctx._http.DeleteAsync($"{VRChatApiService.BASE}/avatars/{avatarId}");
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"DeleteAvatar {avatarId}: {(int)resp.StatusCode}");
            if (resp.IsSuccessStatusCode) return (true, "");
            return (false, VRChatApiService.TryGetApiError(body) ?? $"API error {(int)resp.StatusCode}");
        }
        catch (Exception ex) { ctx.Log($"DeleteAvatar exception: {ex.Message}"); return (false, ex.Message); }
    }
}
