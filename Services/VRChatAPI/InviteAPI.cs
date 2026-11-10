using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VRCNext.Services;

public class InviteAPI(VRChatApiService ctx)
{
    private const string ChatWorldId = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd";
    private readonly Dictionary<int, DateTime> _chatSlotTimestamps = new();
    private readonly Dictionary<int, string>   _chatSlotTexts      = new();
    private readonly Dictionary<int, string>   _chatSlotRawTexts   = new();
    private const int ChatTotalSlots = 24;
    private const int PoolSlotCount = 12;
    private readonly Dictionary<string, Dictionary<int, DateTime>> _poolTimestamps = new()
    {
        ["response"]        = new(),
        ["requestResponse"] = new(),
    };
    private readonly Dictionary<string, Dictionary<int, string>> _poolRawTexts = new()
    {
        ["response"]        = new(),
        ["requestResponse"] = new(),
    };
    private readonly Dictionary<string, DateTime> _poolLastLoaded = new()
    {
        ["response"]        = DateTime.MinValue,
        ["requestResponse"] = DateTime.MinValue,
    };

    public int ChatSlotsUsed =>
        _chatSlotTimestamps.Count(x => DateTime.Now - x.Value < TimeSpan.FromMinutes(60));

    public async Task<bool> InviteFriendAsync(string userId, string myLocation, int? messageSlot = null)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var loc = myLocation;
            if (string.IsNullOrEmpty(loc) || loc == "offline" || loc == "traveling")
            {
                ctx.Log("InviteFriend: no valid instance to invite to");
                return false;
            }
            var worldId = loc.Contains(':') ? loc.Split(':')[0] : loc;
            var payload = new JObject { ["instanceId"] = loc, ["worldId"] = worldId };
            if (messageSlot.HasValue) payload["messageSlot"] = messageSlot.Value;
            var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            var resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/invite/{userId}", content);
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"InviteFriend response: {(int)resp.StatusCode} body: {body[..Math.Min(200, body.Length)]}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"InviteFriend exception: {ex.Message}"); return false; }
    }

    public async Task<bool> InviteFriendWithPhotoAsync(string userId, string myLocation, string imageUrl, int? messageSlot = null)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var loc = myLocation;
            if (string.IsNullOrEmpty(loc) || loc == "offline" || loc == "traveling")
            { ctx.Log("InviteWithPhoto: no valid instance"); return false; }

            var imgBytes = await ReadPhotoBytesAsync(imageUrl);

            var dataObj = new JObject { ["instanceId"] = loc };
            if (messageSlot.HasValue) dataObj["messageSlot"] = messageSlot.Value;

            using var form = new MultipartFormDataContent();
            var imgContent = new ByteArrayContent(imgBytes);
            imgContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(imgContent, "image", "photo.png");
            form.Add(new StringContent(dataObj.ToString(Newtonsoft.Json.Formatting.None)), "data");

            var resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/invite/{userId}/photo", form);
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"InviteWithPhoto response: {(int)resp.StatusCode} body: {body[..Math.Min(200, body.Length)]}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"InviteWithPhoto exception: {ex.Message}"); return false; }
    }

    private async Task<byte[]> ReadPhotoBytesAsync(string imageUrl)
    {
        if (imageUrl.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
        {
            var comma = imageUrl.IndexOf(',');
            return Convert.FromBase64String(imageUrl[(comma + 1)..]);
        }
        return await ctx._http.GetByteArrayAsync(imageUrl);
    }

    public async Task<bool> RequestInviteWithPhotoAsync(string userId, string imageUrl, int? requestSlot = null)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var imgBytes = await ReadPhotoBytesAsync(imageUrl);
            var dataObj = new JObject();
            if (requestSlot.HasValue) dataObj["requestSlot"] = requestSlot.Value;

            using var form = new MultipartFormDataContent();
            var imgContent = new ByteArrayContent(imgBytes);
            imgContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(imgContent, "image", "photo.png");
            form.Add(new StringContent(dataObj.ToString(Newtonsoft.Json.Formatting.None)), "data");

            var resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/requestInvite/{userId}/photo", form);
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"RequestInviteWithPhoto response: {(int)resp.StatusCode} body: {body[..Math.Min(200, body.Length)]}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"RequestInviteWithPhoto exception: {ex.Message}"); return false; }
    }

    public async Task<bool> RequestInviteAsync(string userId, int? requestSlot = null)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var payload = requestSlot.HasValue
                ? new JObject { ["requestSlot"] = requestSlot.Value }
                : new JObject { ["instanceId"] = "" };
            var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            var resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/requestInvite/{userId}", content);
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"RequestInvite response: {(int)resp.StatusCode} body: {body[..Math.Min(200, body.Length)]}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"RequestInvite exception: {ex.Message}"); return false; }
    }

    public async Task<JArray?> GetInviteMessagesAsync(string userId, string messageType = "message")
    {
        if (!ctx.IsLoggedIn) return null;
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/message/{userId}/{messageType}");
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync();
            return JArray.Parse(body);
        }
        catch (Exception ex) { ctx.Log($"GetInviteMessages exception: {ex.Message}"); return null; }
    }

    public async Task<(bool ok, JArray? messages, int cooldown)> UpdateInviteMessageAsync(string userId, int slot, string message, string messageType = "message")
    {
        if (!ctx.IsLoggedIn) return (false, null, 0);
        try
        {
            var payload = new JObject { ["message"] = message };
            var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/message/{userId}/{messageType}/{slot}", content);
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode) return (true, JArray.Parse(body), 0);
            if ((int)resp.StatusCode == 429)
            {
                try { var err = JObject.Parse(body); return (false, null, err["remainingCooldownMinutes"]?.Value<int>() ?? 60); } catch { }
                return (false, null, 60);
            }
            ctx.Log($"UpdateInviteMessage {messageType}/{slot} failed: {(int)resp.StatusCode} {body[..Math.Min(200, body.Length)]}");
            return (false, null, 0);
        }
        catch (Exception ex) { ctx.Log($"UpdateInviteMessage exception: {ex.Message}"); return (false, null, 0); }
    }

    public async Task<(bool ok, string error)> RespondToNotificationBySlotAsync(string notificationId, int responseSlot)
    {
        if (!ctx.IsLoggedIn) return (false, "Not logged in");
        try
        {
            var payload = new JObject { ["responseSlot"] = responseSlot };
            var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            var resp    = await ctx._http.PostAsync($"{VRChatApiService.BASE}/invite/{notificationId}/response", content);
            var body    = await resp.Content.ReadAsStringAsync();
            ctx.Log($"RespondToNotificationBySlot({notificationId}) slot={responseSlot}: {(int)resp.StatusCode} {body[..Math.Min(150, body.Length)]}");
            return resp.IsSuccessStatusCode ? (true, "") : (false, $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) { ctx.Log($"RespondToNotificationBySlot exception: {ex.Message}"); return (false, ex.Message); }
    }

    public async Task<(bool ok, string error)> RespondToNotificationBySlotWithPhotoAsync(string notificationId, int responseSlot, string imageUrl)
    {
        if (!ctx.IsLoggedIn) return (false, "Not logged in");
        try
        {
            byte[] imgBytes;
            if (imageUrl.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                var comma = imageUrl.IndexOf(',');
                imgBytes = Convert.FromBase64String(imageUrl[(comma + 1)..]);
            }
            else
            {
                imgBytes = await ctx._http.GetByteArrayAsync(imageUrl);
            }

            var dataObj = new JObject { ["responseSlot"] = responseSlot };
            using var form = new MultipartFormDataContent();
            var imgContent = new ByteArrayContent(imgBytes);
            imgContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(imgContent, "image", "photo.png");
            form.Add(new StringContent(dataObj.ToString(Newtonsoft.Json.Formatting.None)), "data");

            var resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/invite/{notificationId}/response/photo", form);
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"RespondToNotificationBySlotWithPhoto({notificationId}) slot={responseSlot}: {(int)resp.StatusCode} {body[..Math.Min(150, body.Length)]}");
            return resp.IsSuccessStatusCode ? (true, "") : (false, $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) { ctx.Log($"RespondToNotificationBySlotWithPhoto exception: {ex.Message}"); return (false, ex.Message); }
    }

    public async Task<(int used, int total)> LoadChatSlotStatusAsync()
    {
        if (!ctx.IsLoggedIn || ctx.CurrentUserId == null) return (0, ChatTotalSlots);
        try
        {
            var t1 = ctx._http.GetAsync($"{VRChatApiService.BASE}/message/{ctx.CurrentUserId}/message");
            Task<HttpResponseMessage>? t2 = ctx._requestMessageSupported
                ? ctx._http.GetAsync($"{VRChatApiService.BASE}/message/{ctx.CurrentUserId}/request")
                : null;
            if (t2 != null) await Task.WhenAll(t1, t2);
            else await t1;

            var pairs = t2 != null
                ? new[] { (t1.Result, 0), (t2.Result, 12) }
                : new[] { (t1.Result, 0) };
            foreach (var (resp, offset) in pairs)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    if (offset == 12 && (int)resp.StatusCode == 400)
                    {
                        ctx._requestMessageSupported = false;
                        ctx.Log("LoadChatSlotStatus: request returned 400 — disabling endpoint");
                    }
                    continue;
                }
                var arr = JArray.Parse(await resp.Content.ReadAsStringAsync());
                foreach (JObject slot in arr.Cast<JObject>())
                {
                    var idx      = slot["slot"]?.Value<int>() ?? -1;
                    var canEdit  = slot["canBeUpdated"]?.Value<bool>() ?? true;
                    var cooldown = slot["remainingCooldownMinutes"]?.Value<int>() ?? 0;
                    var slotText = slot["message"]?.ToString() ?? "";
                    if (idx < 0 || idx >= 12) continue;
                    var vSlot = idx + offset;
                    if (!canEdit && cooldown > 0)
                        _chatSlotTimestamps[vSlot] = DateTime.Now - TimeSpan.FromMinutes(60 - cooldown);
                    else
                        _chatSlotTimestamps.Remove(vSlot);
                    if (!string.IsNullOrEmpty(slotText)) _chatSlotTexts[vSlot] = slotText;
                    else                                 _chatSlotTexts.Remove(vSlot);
                    if (_chatSlotRawTexts.TryGetValue(vSlot, out var raw))
                    {
                        if (string.IsNullOrEmpty(slotText) || raw.Length != slotText.Length)
                            _chatSlotRawTexts.Remove(vSlot);
                    }
                }
            }
            ctx.Log($"ChatSlotStatus: {ChatSlotsUsed}/{ChatTotalSlots} in cooldown");
        }
        catch (Exception ex) { ctx.Log($"LoadChatSlotStatus exception: {ex.Message}"); }
        return (ChatSlotsUsed, ChatTotalSlots);
    }

    private int GetNextFreeSlot()
    {
        for (int i = 0; i < ChatTotalSlots; i++)
            if (!_chatSlotTimestamps.TryGetValue(i, out var t) || DateTime.Now - t >= TimeSpan.FromMinutes(60))
                return i;
        return -1;
    }

    private async Task<(bool ok, int cooldown)> UpdateChatSlotAsync(int virtualSlot, string text)
    {
        if (ctx.CurrentUserId == null) return (false, 0);
        var type     = virtualSlot < 12 ? "message" : "request";
        var realSlot = virtualSlot < 12 ? virtualSlot : virtualSlot - 12;
        var payload  = new JObject { ["message"] = text };
        var content  = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
        var resp     = await ctx._http.PutAsync($"{VRChatApiService.BASE}/message/{ctx.CurrentUserId}/{type}/{realSlot}", content);
        var body     = await resp.Content.ReadAsStringAsync();
        if (resp.IsSuccessStatusCode) return (true, 0);
        if ((int)resp.StatusCode == 429)
        {
            try { return (false, JObject.Parse(body)["remainingCooldownMinutes"]?.Value<int>() ?? 60); } catch { }
            return (false, 60);
        }
        ctx.Log($"UpdateChatSlot({type}/{realSlot}) failed: {(int)resp.StatusCode}");
        return (false, 0);
    }

    public async Task<(bool ok, string error, int slotsUsed)> SendChatMessageAsync(string userId, string text)
    {
        if (!ctx.IsLoggedIn || ctx.CurrentUserId == null) return (false, "Not logged in", 0);
        try
        {
            var vSlot = GetNextFreeSlot();
            if (vSlot < 0)
                return (false, $"All {ChatTotalSlots} slots in cooldown", ChatSlotsUsed);

            var (updateOk, cooldown) = await UpdateChatSlotAsync(vSlot, "msg " + text);
            if (!updateOk)
            {
                _chatSlotTimestamps[vSlot] = DateTime.Now - TimeSpan.FromMinutes(60 - Math.Max(cooldown, 1));
                return (false, $"Slot {vSlot} cooldown: {cooldown} min (refreshing…)", ChatSlotsUsed);
            }

            _chatSlotTimestamps[vSlot] = DateTime.Now;
            _chatSlotTexts[vSlot]      = "msg " + text;
            _chatSlotRawTexts[vSlot]   = "msg " + text;

            HttpResponseMessage resp;
            string body;
            if (vSlot < 12)
            {
                var payload = new JObject
                {
                    ["instanceId"]  = $"{ChatWorldId}:0~region(eu)",
                    ["worldId"]     = ChatWorldId,
                    ["messageSlot"] = vSlot,
                };
                var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
                resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/invite/{userId}", content);
                body = await resp.Content.ReadAsStringAsync();
            }
            else
            {
                var realSlot = vSlot - 12;
                var payload  = new JObject { ["messageSlot"] = realSlot };
                var content  = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
                resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/requestInvite/{userId}", content);
                body = await resp.Content.ReadAsStringAsync();
            }

            ctx.Log($"SendChatMessage({userId}) vSlot={vSlot}: {(int)resp.StatusCode} {body[..Math.Min(150, body.Length)]}");
            return resp.IsSuccessStatusCode
                ? (true, "", ChatSlotsUsed)
                : (false, $"HTTP {(int)resp.StatusCode}", ChatSlotsUsed);
        }
        catch (Exception ex) { ctx.Log($"SendChatMessage exception: {ex.Message}"); return (false, ex.Message, 0); }
    }

    private async Task LoadPoolStatusAsync(string pool)
    {
        if (ctx.CurrentUserId == null) return;
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/message/{ctx.CurrentUserId}/{pool}");
            if (!resp.IsSuccessStatusCode) return;
            var arr  = JArray.Parse(await resp.Content.ReadAsStringAsync());
            var ts   = _poolTimestamps[pool];
            var raws = _poolRawTexts[pool];
            foreach (JObject slot in arr.Cast<JObject>())
            {
                var idx      = slot["slot"]?.Value<int>() ?? -1;
                var canEdit  = slot["canBeUpdated"]?.Value<bool>() ?? true;
                var cooldown = slot["remainingCooldownMinutes"]?.Value<int>() ?? 0;
                var slotText = slot["message"]?.ToString() ?? "";
                if (idx < 0 || idx >= PoolSlotCount) continue;
                if (!canEdit && cooldown > 0)
                    ts[idx] = DateTime.Now - TimeSpan.FromMinutes(60 - cooldown);
                else
                    ts.Remove(idx);
                if (raws.TryGetValue(idx, out var raw))
                {
                    if (string.IsNullOrEmpty(slotText) || raw.Length != slotText.Length)
                        raws.Remove(idx);
                }
            }
            _poolLastLoaded[pool] = DateTime.Now;
            ctx.Log($"LoadPoolStatus({pool}): {ts.Count(x => DateTime.Now - x.Value < TimeSpan.FromMinutes(60))}/{PoolSlotCount} in cooldown");
        }
        catch (Exception ex) { ctx.Log($"LoadPoolStatus({pool}) exception: {ex.Message}"); }
    }

    public async Task<(bool ok, string error)> RespondToNotificationAsync(string notificationId, string notifType, string text)
    {
        if (!ctx.IsLoggedIn || ctx.CurrentUserId == null) return (false, "Not logged in");
        var pool = notifType == "requestInvite" ? "requestResponse" : "response";

        if (DateTime.Now - _poolLastLoaded[pool] > TimeSpan.FromMinutes(5))
            await LoadPoolStatusAsync(pool);

        try
        {
            var ts   = _poolTimestamps[pool];
            var raws = _poolRawTexts[pool];

            int slot = -1;
            foreach (var kv in raws)
            {
                if (kv.Value == text) { slot = kv.Key; break; }
            }

            if (slot < 0)
            {
                for (int i = 0; i < PoolSlotCount; i++)
                {
                    if (!ts.TryGetValue(i, out var t) || DateTime.Now - t >= TimeSpan.FromMinutes(60))
                    { slot = i; break; }
                }
                if (slot < 0) return (false, $"All {PoolSlotCount} {pool} slots in cooldown");

                var putPayload = new JObject { ["message"] = text };
                var putContent = new StringContent(putPayload.ToString(), Encoding.UTF8, "application/json");
                var putResp    = await ctx._http.PutAsync($"{VRChatApiService.BASE}/message/{ctx.CurrentUserId}/{pool}/{slot}", putContent);
                var putBody    = await putResp.Content.ReadAsStringAsync();
                if (!putResp.IsSuccessStatusCode)
                {
                    if ((int)putResp.StatusCode == 429)
                    {
                        var cd = 60;
                        try { cd = JObject.Parse(putBody)["remainingCooldownMinutes"]?.Value<int>() ?? 60; } catch { }
                        ts[slot] = DateTime.Now - TimeSpan.FromMinutes(60 - Math.Max(cd, 1));
                        return (false, $"{pool} slot {slot} cooldown: {cd} min");
                    }
                    ctx.Log($"RespondToNotification PUT {pool}/{slot}: {(int)putResp.StatusCode}");
                    return (false, $"PUT HTTP {(int)putResp.StatusCode}");
                }
                ts[slot]   = DateTime.Now;
                raws[slot] = text;
            }
            else
            {
                ctx.Log($"RespondToNotification({notificationId}): reusing {pool} slot {slot} (text match)");
            }

            var payload = new JObject { ["responseSlot"] = slot };
            var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            var resp    = await ctx._http.PostAsync($"{VRChatApiService.BASE}/invite/{notificationId}/response", content);
            var body    = await resp.Content.ReadAsStringAsync();
            ctx.Log($"RespondToNotification({notificationId}) {pool}/{slot}: {(int)resp.StatusCode} {body[..Math.Min(150, body.Length)]}");
            return resp.IsSuccessStatusCode
                ? (true, "")
                : (false, $"POST HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) { ctx.Log($"RespondToNotification exception: {ex.Message}"); return (false, ex.Message); }
    }

    public async Task<(bool ok, string error, int slotsUsed)> SendChatMessageDedupedAsync(string userId, string text)
    {
        if (!ctx.IsLoggedIn || ctx.CurrentUserId == null) return (false, "Not logged in", 0);
        try
        {
            int vSlot = -1;
            foreach (var kv in _chatSlotRawTexts)
            {
                if (kv.Value == text) { vSlot = kv.Key; break; }
            }

            if (vSlot < 0)
            {
                vSlot = GetNextFreeSlot();
                if (vSlot < 0) return (false, $"All {ChatTotalSlots} slots in cooldown", ChatSlotsUsed);

                var (updateOk, cooldown) = await UpdateChatSlotAsync(vSlot, text);
                if (!updateOk)
                {
                    _chatSlotTimestamps[vSlot] = DateTime.Now - TimeSpan.FromMinutes(60 - Math.Max(cooldown, 1));
                    return (false, $"Slot {vSlot} cooldown: {cooldown} min (refreshing…)", ChatSlotsUsed);
                }
                _chatSlotTimestamps[vSlot] = DateTime.Now;
                _chatSlotTexts[vSlot]      = text;
                _chatSlotRawTexts[vSlot]   = text;
            }
            else
            {
                ctx.Log($"SendChatMessageDeduped({userId}): reusing slot {vSlot} (text match)");
            }

            HttpResponseMessage resp;
            string body;
            if (vSlot < 12)
            {
                var payload = new JObject
                {
                    ["instanceId"]  = $"{ChatWorldId}:0~region(eu)",
                    ["worldId"]     = ChatWorldId,
                    ["messageSlot"] = vSlot,
                };
                var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
                resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/invite/{userId}", content);
                body = await resp.Content.ReadAsStringAsync();
            }
            else
            {
                var realSlot = vSlot - 12;
                var payload  = new JObject { ["messageSlot"] = realSlot };
                var content  = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
                resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/requestInvite/{userId}", content);
                body = await resp.Content.ReadAsStringAsync();
            }

            ctx.Log($"SendChatMessageDeduped({userId}) vSlot={vSlot}: {(int)resp.StatusCode} {body[..Math.Min(150, body.Length)]}");
            return resp.IsSuccessStatusCode
                ? (true, "", ChatSlotsUsed)
                : (false, $"HTTP {(int)resp.StatusCode}", ChatSlotsUsed);
        }
        catch (Exception ex) { ctx.Log($"SendChatMessageDeduped exception: {ex.Message}"); return (false, ex.Message, 0); }
    }
}
