using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rsync.Delta;

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
            if (resp.IsSuccessStatusCode) return JObject.Parse(body);
            ctx.Log($"GetAvatar {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        }
        catch (Exception ex) { ctx.Log($"GetAvatar exception: {ex.Message}"); }
        return null;
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
            for (int offset = 0; offset < 500; offset += 50)
            {
                var resp = await ctx._http.GetAsync(
                    $"{VRChatApiService.BASE}/avatars?user=me&releaseStatus=all&n=50&offset={offset}&sort=updated&order=descending");
                if (!resp.IsSuccessStatusCode) break;
                var arr = JArray.Parse(await resp.Content.ReadAsStringAsync());
                if (arr.Count == 0) break;
                all.AddRange(arr.Cast<JObject>());
                if (arr.Count < 50) break;
                await Task.Delay(300);
            }
            ctx.Log($"GetOwnAvatars: found {all.Count}");
        }
        catch (Exception ex) { ctx.Log($"GetOwnAvatars exception: {ex.Message}"); }
        return all;
    }

    public async Task<List<JObject>> GetFavoriteAvatarsAsync()
    {
        if (!ctx.IsLoggedIn) return new();
        var all = new List<JObject>();
        try
        {
            for (int offset = 0; offset < 500; offset += 50)
            {
                var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/avatars/favorites?n=50&offset={offset}");
                if (!resp.IsSuccessStatusCode) break;
                var arr = JArray.Parse(await resp.Content.ReadAsStringAsync());
                if (arr.Count == 0) break;
                all.AddRange(arr.Cast<JObject>());
                if (arr.Count < 50) break;
                await Task.Delay(300);
            }
            ctx.Log($"GetFavoriteAvatars: found {all.Count}");
        }
        catch (Exception ex) { ctx.Log($"GetFavoriteAvatars exception: {ex.Message}"); }
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
        client.DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower;
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UA);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        try
        {
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
        client.DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower;
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UA);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        // avtrdb caps the page size, so paginate until a short/empty page is hit.
        int pageSize = -1;
        for (int page = 0; page < 25; page++)
        {
            var url = $"https://api.avtrdb.com/v2/avatar/search?query={Uri.EscapeDataString(authorId)}&limit={n}&page={page}";
            JArray? arr = null;
            try
            {
                ctx.Log($"SearchAvatarsByAuthor: {url}");
                var resp = await client.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();
                ctx.Log($"SearchAvatarsByAuthor [{(int)resp.StatusCode}] len={body.Length}");
                if (!resp.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body)) break;
                var parsed = JToken.Parse(body);
                arr = (parsed as JObject)?["avatars"] as JArray ?? parsed as JArray;
            }
            catch (Exception ex) { ctx.Log($"SearchAvatarsByAuthor exception: {ex.Message}"); break; }

            if (arr == null || arr.Count == 0) break;
            if (pageSize < 0) pageSize = arr.Count;

            foreach (var item in arr)
            {
                var id = item["vrc_id"]?.ToString() ?? item["id"]?.ToString() ?? "";
                if (id.Length > 0 && !seen.Add(id)) continue;
                all.Add(item);
            }

            if (arr.Count < pageSize) break;
            await Task.Delay(200);
        }
        return all;
    }

    private static readonly AvtrdbResolver _avtrdbResolver = new();

    private static (string? id, JObject? data) MapResolved(JObject? o)
    {
        if (o == null) return (null, null);
        var id = o["vrc_id"]?.ToString();
        if (string.IsNullOrEmpty(id)) return (null, null);

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

    public async Task<(string? id, JObject? data)> GetAvatarIdByFileIdAsync(string fileId)
    {
        try
        {
            return MapResolved(await _avtrdbResolver.ResolveAsync(fileId));
        }
        catch (Exception ex) { ctx.Log($"GetAvatarIdByFileId exception: {ex.Message}"); }
        return (null, null);
    }

    public async Task<Dictionary<string, (string? id, JObject? data)>> GetAvatarIdsByFileIdsAsync(IEnumerable<string> fileIds)
    {
        var result = new Dictionary<string, (string? id, JObject? data)>();
        try
        {
            var raw = await _avtrdbResolver.ResolveManyAsync(fileIds);
            foreach (var kv in raw) result[kv.Key] = MapResolved(kv.Value);
        }
        catch (Exception ex) { ctx.Log($"GetAvatarIdsByFileIds exception: {ex.Message}"); }
        return result;
    }

    public async Task<bool> CheckAvatarExistsAvtrIcuAsync(string avatarId)
    {
        var results = await SearchAvatarsAvtrIcuAsync(avatarId, 5, 0);
        return results.Any(a => a["id"]?.ToString() == avatarId);
    }

    public async Task<bool> CheckAvatarExistsAvtrdbAsync(string avatarId)
    {
        var results = await SearchAvatarsAsync(avatarId, 1);
        return results.Count > 0 && results.Any(a =>
            (a["vrc_id"]?.ToString() ?? a["id"]?.ToString() ?? "") == avatarId);
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

    public async Task<(bool ok, string imageUrl, string error)> UploadAvatarMainImageAsync(
        string avatarId, string existingImageUrl, byte[] imageBytes)
    {
        if (!ctx.IsLoggedIn) return (false, "", "Not logged in");
        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(existingImageUrl, @"/file/([^/]+)/\d+");
            if (!match.Success) return (false, "", "Could not extract file ID from existing image URL");
            var sourceFileId = match.Groups[1].Value;

            using var md5 = MD5.Create();
            var fileMd5 = Convert.ToBase64String(md5.ComputeHash(imageBytes));
            var fileSizeInBytes = imageBytes.Length;

            var signatureBytes = await ComputeRsyncSignatureAsync(imageBytes);
            var signatureMd5 = Convert.ToBase64String(md5.ComputeHash(signatureBytes));
            var signatureSizeInBytes = signatureBytes.Length;

            var initBody = JsonConvert.SerializeObject(new { fileMd5, fileSizeInBytes, signatureMd5, signatureSizeInBytes });
            ctx.Log($"UploadAvatarMainImage: POST file/{sourceFileId} fileSize={fileSizeInBytes} sigSize={signatureSizeInBytes}");
            var r = await ctx._http.PostAsync(
                $"{VRChatApiService.BASE}/file/{sourceFileId}",
                new StringContent(initBody, Encoding.UTF8, "application/json"));
            var rb = await r.Content.ReadAsStringAsync();
            ctx.Log($"UploadAvatarMainImage: init [{(int)r.StatusCode}] preview={rb[..Math.Min(200, rb.Length)]}");
            if (!r.IsSuccessStatusCode) return (false, "", VRChatApiService.TryGetApiError(rb) ?? $"HTTP {(int)r.StatusCode}");

            var uploadObj = JObject.Parse(rb);
            var uploadedFileId = uploadObj["id"]?.ToString();
            var versions = uploadObj["versions"] as JArray;
            var fileVersion = versions?.OfType<JObject>().LastOrDefault()?["version"]?.Value<int>();
            if (string.IsNullOrEmpty(uploadedFileId) || fileVersion == null)
                return (false, "", "No file version returned");

            await UploadFileSegmentAsync(uploadedFileId, fileVersion.Value, "file", imageBytes, "image/png", fileMd5);
            await UploadFileSegmentAsync(uploadedFileId, fileVersion.Value, "signature", signatureBytes, "application/x-rsync-signature", signatureMd5);

            var newImageUrl = $"{VRChatApiService.BASE}/file/{uploadedFileId}/{fileVersion}/file";
            var avatarBody = JsonConvert.SerializeObject(new { id = avatarId, imageUrl = newImageUrl });
            ctx.Log($"UploadAvatarMainImage: PUT avatars/{avatarId}");
            var ar = await ctx._http.PutAsync(
                $"{VRChatApiService.BASE}/avatars/{avatarId}",
                new StringContent(avatarBody, Encoding.UTF8, "application/json"));
            var arb = await ar.Content.ReadAsStringAsync();
            ctx.Log($"UploadAvatarMainImage: PUT avatar [{(int)ar.StatusCode}] preview={arb[..Math.Min(200, arb.Length)]}");
            if (!ar.IsSuccessStatusCode) return (false, "", VRChatApiService.TryGetApiError(arb) ?? $"HTTP {(int)ar.StatusCode}");
            return (true, newImageUrl, "");
        }
        catch (Exception ex) { ctx.Log($"UploadAvatarMainImage exception: {ex.Message}"); return (false, "", ex.Message); }
    }

    private async Task UploadFileSegmentAsync(string fileId, int version, string segment, byte[] data, string mimeType, string md5Base64)
    {
        var startUrl = $"{VRChatApiService.BASE}/file/{fileId}/{version}/{segment}/start";
        ctx.Log($"UploadFileSegment [{segment}]: PUT start");
        var startResp = await ctx._http.PutAsync(startUrl, new StringContent("{}", Encoding.UTF8, "application/json"));
        var startBody = await startResp.Content.ReadAsStringAsync();
        ctx.Log($"UploadFileSegment [{segment}]: start [{(int)startResp.StatusCode}] preview={startBody[..Math.Min(200, startBody.Length)]}");
        var uploadUrl = JObject.Parse(startBody)["url"]?.ToString();

        if (!string.IsNullOrEmpty(uploadUrl))
        {
            ctx.Log($"UploadFileSegment [{segment}]: PUT to CDN");
            var fileContent = new ByteArrayContent(data);
            fileContent.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(mimeType);
            // VRChat internal upload URLs require auth cookies; S3 URLs do not
            System.Net.Http.HttpResponseMessage cdnResp;
            if (uploadUrl.StartsWith(VRChatApiService.BASE, StringComparison.OrdinalIgnoreCase))
            {
                cdnResp = await ctx._http.PutAsync(uploadUrl, fileContent);
            }
            else
            {
                fileContent.Headers.Add("Content-MD5", md5Base64);
                using var s3Client = new HttpClient();
                s3Client.DefaultRequestVersion = System.Net.HttpVersion.Version20;
                s3Client.DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower;
                s3Client.Timeout = TimeSpan.FromSeconds(120);
                cdnResp = await s3Client.PutAsync(uploadUrl, fileContent);
            }
            ctx.Log($"UploadFileSegment [{segment}]: CDN [{(int)cdnResp.StatusCode}]");
        }

        var finishUrl = $"{VRChatApiService.BASE}/file/{fileId}/{version}/{segment}/finish";
        var finishBody = JsonConvert.SerializeObject(new { maxParts = 0, nextPartNumber = 0 });
        ctx.Log($"UploadFileSegment [{segment}]: PUT finish");
        var finishResp = await ctx._http.PutAsync(finishUrl, new StringContent(finishBody, Encoding.UTF8, "application/json"));
        ctx.Log($"UploadFileSegment [{segment}]: finish [{(int)finishResp.StatusCode}]");
    }

    private static async Task<byte[]> ComputeRsyncSignatureAsync(byte[] bytes)
    {
        var rdiff = new Rdiff();
        using var inputStream = new MemoryStream(bytes);
        using var outputStream = new MemoryStream();
        var options = new SignatureOptions(
            blockLength: 2048,
            strongHashLength: 8,
            rollingHashAlgorithm: RollingHashAlgorithm.Adler,
            strongHashAlgorithm: StrongHashAlgorithm.Blake2b);
        await rdiff.SignatureAsync(inputStream, outputStream, options);
        return outputStream.ToArray();
    }
}
