using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VRCNext.Services;

public class VRChatApiService
{
    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new();
    private const string BASE = "https://api.vrchat.cloud/api/1";
    private const string UA = "VRCNext/1.01.0 contact@vrcnext.app";

    public bool IsLoggedIn { get; private set; }
    public JObject? CurrentUserRaw { get; private set; }
    public string? CurrentUserId => CurrentUserRaw?["id"]?.ToString();
    public string? CurrentAvatarId => CurrentUserRaw?["currentAvatar"]?.ToString();

    public event Action<string>? DebugLog;
    private void Log(string msg) => DebugLog?.Invoke(msg);

    public class LoginResult
    {
        public bool Success { get; set; }
        public bool Requires2FA { get; set; }
        public string TwoFactorType { get; set; } = "";
        public string? Error { get; set; }
        public JObject? User { get; set; }
    }

    public VRChatApiService()
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
        };
        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UA);
    }

    // Cookie persistence for session resume
    public void RestoreCookies(string? authCookie, string? twoFactorAuthCookie)
    {
        var uri = new Uri("https://api.vrchat.cloud");
        if (!string.IsNullOrEmpty(authCookie))
            _cookies.Add(uri, new Cookie("auth", authCookie, "/", ".vrchat.cloud"));
        if (!string.IsNullOrEmpty(twoFactorAuthCookie))
            _cookies.Add(uri, new Cookie("twoFactorAuth", twoFactorAuthCookie, "/", ".vrchat.cloud"));
    }

    public (string? auth, string? twoFactorAuth) GetCookies()
    {
        var cookies = _cookies.GetCookies(new Uri("https://api.vrchat.cloud"));
        string? auth = null, tfa = null;
        foreach (Cookie c in cookies)
        {
            if (c.Name == "auth") auth = c.Value;
            if (c.Name == "twoFactorAuth") tfa = c.Value;
        }
        return (auth, tfa);
    }

    /// <summary>
    /// Try to resume a session using stored cookies (no 2FA needed).
    /// </summary>
    public async Task<LoginResult> TryResumeSessionAsync()
    {
        try
        {
            var resp = await _http.GetAsync($"{BASE}/auth/user");
            var body = await resp.Content.ReadAsStringAsync();
            Log($"Resume session response: {(int)resp.StatusCode}");

            if (!resp.IsSuccessStatusCode)
                return new LoginResult { Error = "Session expired" };

            var json = JObject.Parse(body);

            if (json["requiresTwoFactorAuth"] != null)
                return new LoginResult { Error = "Session expired (2FA required)" };

            CurrentUserRaw = json;
            IsLoggedIn = true;
            Log($"Resumed session as: {json["displayName"]}");
            return new LoginResult { Success = true, User = json };
        }
        catch (Exception ex)
        {
            Log($"Resume session exception: {ex.Message}");
            return new LoginResult { Error = ex.Message };
        }
    }

    // Login & 2FA
    public async Task<LoginResult> LoginAsync(string username, string password)
    {
        try
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}"));

            var req = new HttpRequestMessage(HttpMethod.Get, $"{BASE}/auth/user");
            req.Headers.TryAddWithoutValidation("Authorization", $"Basic {encoded}");

            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            Log($"Login response: {(int)resp.StatusCode}");
            Log($"Body preview: {(body.Length > 200 ? body[..200] : body)}");

            var cookies = _cookies.GetCookies(new Uri("https://api.vrchat.cloud"));
            Log($"Cookies after login: {string.Join(", ", cookies.Cast<Cookie>().Select(c => $"{c.Name}={c.Value[..Math.Min(8, c.Value.Length)]}..."))}");

            JObject json;
            try { json = JObject.Parse(body); }
            catch { return new LoginResult { Error = $"Invalid response: {body[..Math.Min(100, body.Length)]}" }; }

            var require2fa = json["requiresTwoFactorAuth"];
            if (require2fa != null && require2fa.Type == JTokenType.Array)
            {
                var methods = require2fa.ToObject<List<string>>() ?? new();
                Log($"2FA required: {string.Join(", ", methods)}");
                var type = methods.Contains("totp") ? "totp" :
                           methods.Contains("emailOtp") ? "emailotp" :
                           methods.Contains("otp") ? "totp" :
                           methods.FirstOrDefault() ?? "totp";
                return new LoginResult { Requires2FA = true, TwoFactorType = type };
            }

            if (!resp.IsSuccessStatusCode)
            {
                var errMsg = json["error"]?["message"]?.ToString() ?? body[..Math.Min(100, body.Length)];
                return new LoginResult { Error = errMsg };
            }

            CurrentUserRaw = json;
            IsLoggedIn = true;
            Log($"Logged in as: {json["displayName"]}  status: {json["status"]}");
            return new LoginResult { Success = true, User = json };
        }
        catch (Exception ex)
        {
            Log($"Login exception: {ex.Message}");
            return new LoginResult { Error = ex.Message };
        }
    }

    public async Task<LoginResult> Verify2FAAsync(string code, string type)
    {
        try
        {
            string endpoint = type == "emailotp"
                ? $"{BASE}/auth/twofactorauth/emailotp/verify"
                : $"{BASE}/auth/twofactorauth/totp/verify";

            var json = JsonConvert.SerializeObject(new Dictionary<string, string> { { "code", code } });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(endpoint, content);
            var body = await resp.Content.ReadAsStringAsync();

            Log($"2FA verify response: {(int)resp.StatusCode} body: {body}");

            var cookies = _cookies.GetCookies(new Uri("https://api.vrchat.cloud"));
            Log($"Cookies after 2FA: {string.Join(", ", cookies.Cast<Cookie>().Select(c => $"{c.Name}={c.Value[..Math.Min(8, c.Value.Length)]}..."))}");

            if (!resp.IsSuccessStatusCode)
                return new LoginResult { Error = $"2FA failed ({(int)resp.StatusCode}): {body}" };

            var result = JObject.Parse(body);
            if (result["verified"]?.Value<bool>() != true)
                return new LoginResult { Error = "2FA code invalid or expired" };

            await Task.Delay(300);
            var userResp = await _http.GetAsync($"{BASE}/auth/user");
            var userBody = await userResp.Content.ReadAsStringAsync();

            Log($"Post-2FA user fetch: {(int)userResp.StatusCode}");

            if (!userResp.IsSuccessStatusCode)
                return new LoginResult { Error = $"Failed to get user after 2FA: {(int)userResp.StatusCode}" };

            var user = JObject.Parse(userBody);
            if (user["requiresTwoFactorAuth"] != null)
                return new LoginResult { Error = "Still requires 2FA after verification" };

            CurrentUserRaw = user;
            IsLoggedIn = true;
            Log($"Logged in after 2FA: {user["displayName"]}  status: {user["status"]}");
            return new LoginResult { Success = true, User = user };
        }
        catch (Exception ex)
        {
            Log($"2FA exception: {ex.Message}");
            return new LoginResult { Error = ex.Message };
        }
    }

    // Friends
    public async Task<List<JObject>> GetOnlineFriendsAsync()
    {
        var all = new List<JObject>();
        if (!IsLoggedIn) { Log("GetFriends: not logged in"); return all; }

        try
        {
            int offset = 0;
            while (true)
            {
                var url = $"{BASE}/auth/user/friends?offline=false&n=50&offset={offset}";
                Log($"Fetching friends: offset={offset}");
                var resp = await _http.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();
                Log($"Friends response: {(int)resp.StatusCode}  length={body.Length}");

                if (!resp.IsSuccessStatusCode)
                {
                    Log($"Friends error: {body[..Math.Min(200, body.Length)]}");
                    break;
                }

                var batch = JArray.Parse(body);
                Log($"Friends batch: {batch.Count} items");

                if (batch.Count == 0) break;
                foreach (var item in batch)
                    all.Add((JObject)item);

                if (batch.Count < 50) break;
                offset += 50;
                await Task.Delay(1100);
            }
        }
        catch (Exception ex)
        {
            Log($"Friends exception: {ex.Message}");
        }

        Log($"Total online friends: {all.Count}");
        return all;
    }

    public async Task<List<JObject>> GetOfflineFriendsAsync()
    {
        var all = new List<JObject>();
        if (!IsLoggedIn) return all;

        try
        {
            int offset = 0;
            while (true)
            {
                var url = $"{BASE}/auth/user/friends?offline=true&n=50&offset={offset}";
                Log($"Fetching offline friends: offset={offset}");
                var resp = await _http.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode) break;

                var batch = JArray.Parse(body);
                if (batch.Count == 0) break;
                foreach (var item in batch)
                    all.Add((JObject)item);

                if (batch.Count < 50) break;
                offset += 50;
                await Task.Delay(1100);
            }
        }
        catch (Exception ex)
        {
            Log($"Offline friends exception: {ex.Message}");
        }

        Log($"Total offline friends: {all.Count}");
        return all;
    }

    // Friends (enriched with world info for dashboard)
    public async Task<List<JObject>> GetOnlineFriendsEnrichedAsync()
    {
        var friends = await GetOnlineFriendsAsync();
        if (friends.Count == 0) return friends;

        // Collect unique worldIds from locations
        var worldIds = new HashSet<string>();
        foreach (var f in friends)
        {
            var loc = f["location"]?.ToString();
            if (string.IsNullOrEmpty(loc) || loc == "private" || loc == "offline" || loc == "traveling") continue;
            var (worldId, _, _) = ParseLocation(loc);
            if (!string.IsNullOrEmpty(worldId)) worldIds.Add(worldId);
        }

        // Fetch world info for each unique worldId
        var worldCache = new Dictionary<string, JObject>();
        foreach (var wid in worldIds)
        {
            try
            {
                var world = await GetWorldAsync(wid);
                if (world != null) worldCache[wid] = world;
                await Task.Delay(300); // rate limit
            }
            catch (Exception ex) { Log($"EnrichFriends world fetch error: {ex.Message}"); }
        }

        // Attach world info to each friend
        foreach (var f in friends)
        {
            var loc = f["location"]?.ToString();
            if (string.IsNullOrEmpty(loc) || loc == "private" || loc == "offline" || loc == "traveling") continue;
            var (worldId, _, instanceType) = ParseLocation(loc);
            if (!string.IsNullOrEmpty(worldId) && worldCache.TryGetValue(worldId, out var world))
            {
                f["worldName"] = world["name"]?.ToString() ?? "";
                f["worldThumb"] = world["thumbnailImageUrl"]?.ToString() ?? "";
                f["worldCapacity"] = world["capacity"]?.Value<int>() ?? 0;
                f["instanceType"] = instanceType;
            }
        }

        Log($"Enriched {friends.Count} friends with {worldCache.Count} worlds");
        return friends;
    }

    // Update own status and statusDescription
    public async Task<JObject?> UpdateStatusAsync(string status, string statusDescription)
    {
        if (!IsLoggedIn || CurrentUserId == null) return null;
        try
        {
            var payload = new JObject { ["status"] = status, ["statusDescription"] = statusDescription };
            var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            var resp = await _http.PutAsync($"{BASE}/users/{CurrentUserId}", content);
            var body = await resp.Content.ReadAsStringAsync();
            Log($"UpdateStatus response: {(int)resp.StatusCode}");

            if (resp.IsSuccessStatusCode)
            {
                var user = JObject.Parse(body);
                CurrentUserRaw = user;
                return user;
            }
            Log($"UpdateStatus error: {body[..Math.Min(200, body.Length)]}");
        }
        catch (Exception ex) { Log($"UpdateStatus exception: {ex.Message}"); }
        return null;
    }

    // Update own profile (bio, pronouns, links, tags)
    public async Task<JObject?> UpdateProfileAsync(string? bio, string? pronouns, List<string>? bioLinks, List<string>? tags)
    {
        if (!IsLoggedIn || CurrentUserId == null) return null;
        try
        {
            var payload = new JObject();
            if (bio != null) payload["bio"] = bio;
            if (pronouns != null) payload["pronouns"] = pronouns;
            if (bioLinks != null) payload["bioLinks"] = JArray.FromObject(bioLinks);
            if (tags != null) payload["tags"] = JArray.FromObject(tags);
            var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            var resp = await _http.PutAsync($"{BASE}/users/{CurrentUserId}", content);
            var body = await resp.Content.ReadAsStringAsync();
            Log($"UpdateProfile response: {(int)resp.StatusCode}");
            if (resp.IsSuccessStatusCode)
            {
                var user = JObject.Parse(body);
                CurrentUserRaw = user;
                return user;
            }
            Log($"UpdateProfile error: {body[..Math.Min(200, body.Length)]}");
        }
        catch (Exception ex) { Log($"UpdateProfile exception: {ex.Message}"); }
        return null;
    }

    // Get user profile by ID
    public async Task<JObject?> GetUserAsync(string userId)
    {
        if (!IsLoggedIn) return null;
        try
        {
            var resp = await _http.GetAsync($"{BASE}/users/{userId}");
            var body = await resp.Content.ReadAsStringAsync();
            Log($"GetUser response: {(int)resp.StatusCode}");
            if (resp.IsSuccessStatusCode) return JObject.Parse(body);
            Log($"GetUser error: {body[..Math.Min(200, body.Length)]}");
        }
        catch (Exception ex) { Log($"GetUser exception: {ex.Message}"); }
        return null;
    }

    // Get favorite worlds
    public async Task<List<JObject>> GetFavoriteWorldsAsync(int count = 25)
    {
        var all = new List<JObject>();
        if (!IsLoggedIn) { Log("GetFavoriteWorlds: not logged in"); return all; }
        try
        {
            int offset = 0;
            while (all.Count < count)
            {
                var n = Math.Min(count - all.Count, 50);
                var url = $"{BASE}/worlds/favorites?n={n}&offset={offset}";
                Log($"Fetching favorite worlds: offset={offset}");
                var resp = await _http.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();
                Log($"FavoriteWorlds response: {(int)resp.StatusCode} length={body.Length}");

                if (!resp.IsSuccessStatusCode)
                {
                    Log($"FavoriteWorlds error: {body[..Math.Min(200, body.Length)]}");
                    break;
                }

                var batch = JArray.Parse(body);
                Log($"FavoriteWorlds batch: {batch.Count} items");
                if (batch.Count == 0) break;

                foreach (var item in batch)
                    all.Add((JObject)item);

                if (batch.Count < n) break;
                offset += batch.Count;
                await Task.Delay(1100);
            }
        }
        catch (Exception ex)
        {
            Log($"FavoriteWorlds exception: {ex.Message}");
        }

        Log($"Total favorite worlds: {all.Count}");
        return all;
    }

    // Get world info
    public async Task<JObject?> GetWorldAsync(string worldId)
    {
        if (!IsLoggedIn) return null;
        try
        {
            var resp = await _http.GetAsync($"{BASE}/worlds/{worldId}");
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode) return JObject.Parse(body);
        }
        catch (Exception ex) { Log($"GetWorld exception: {ex.Message}"); }
        return null;
    }

    // Get instance info
    public async Task<JObject?> GetInstanceAsync(string location)
    {
        if (!IsLoggedIn || string.IsNullOrEmpty(location)) return null;
        try
        {
            // location = "wrld_xxx:12345~friends(usr_abc)~region(eu)"
            // VRChat API expects the raw location string, NOT percent-encoded
            var resp = await _http.GetAsync($"{BASE}/instances/{location}");
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode) return JObject.Parse(body);
            Log($"GetInstance {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        }
        catch (Exception ex) { Log($"GetInstance exception: {ex.Message}"); }
        return null;
    }

    // Self-invite (join instance via API)
    // Endpoint: POST /api/1/invite/myself/to/{worldId}:{instanceId}
    // IMPORTANT: The location must NOT be URL-encoded!
    // Characters :~() are valid in URL paths and VRChat's WAF
    // rejects percent-encoded versions (400 "malformed url").
    public async Task<bool> InviteSelfAsync(string location)
    {
        if (!IsLoggedIn || CurrentUserId == null) { Log("InviteSelf: not logged in"); return false; }
        try
        {
            // location = "wrld_xxx:12345~friends(usr_abc)~region(eu)"
            // Split into worldId and instanceId for the URL path
            var parts = location.Split(':', 2);
            if (parts.Length < 2) { Log($"InviteSelf: invalid location format: {location}"); return false; }
            var worldId = parts[0];   // wrld_xxx
            var instanceId = parts[1]; // 12345~friends(usr_abc)~region(eu)

            // Build URL WITHOUT any encoding. VRChat expects raw :~() in the path
            var url = $"{BASE}/instances/{worldId}:{instanceId}/invite";
            Log($"InviteSelf: POST {url}");

            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(url, content);
            var body = await resp.Content.ReadAsStringAsync();
            Log($"InviteSelf response: {(int)resp.StatusCode} body: {body[..Math.Min(300, body.Length)]}");

            if (resp.IsSuccessStatusCode) return true;

            // Fallback: try the older /invite/myself/to/ endpoint
            var url2 = $"{BASE}/invite/myself/to/{worldId}:{instanceId}";
            Log($"InviteSelf fallback: POST {url2}");
            var content2 = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp2 = await _http.PostAsync(url2, content2);
            var body2 = await resp2.Content.ReadAsStringAsync();
            Log($"InviteSelf fallback response: {(int)resp2.StatusCode} body: {body2[..Math.Min(300, body2.Length)]}");
            return resp2.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"InviteSelf exception: {ex.Message}"); return false; }
    }

    /// <summary>
    /// Build a vrchat:// launch URI that can be opened with Process.Start
    /// to make the VRChat client join an instance directly.
    /// Format: vrchat://launch?ref=vrchat.com&amp;id={worldId}:{instanceId}
    /// </summary>
    public static string BuildLaunchUri(string location)
    {
        return $"vrchat://launch?ref=vrchat.com&id={location}";
    }

    /// <summary>
    /// Build a VRChat instance location string.
    /// type: "public", "friends", "hidden" (invite+), "private" (invite)
    /// region: "us", "use", "eu", "jp"
    /// </summary>
    public string BuildInstanceLocation(string worldId, string type, string region)
    {
        var rng = new Random();
        var instanceNum = rng.Next(10000, 99999);
        var nonce = Guid.NewGuid().ToString();
        var userId = CurrentUserId ?? "";

        string instanceId;
        switch (type)
        {
            case "friends":
                instanceId = $"{instanceNum}~friends({userId})~region({region})~nonce({nonce})";
                break;
            case "hidden": // invite+
                instanceId = $"{instanceNum}~hidden({userId})~region({region})~nonce({nonce})";
                break;
            case "private": // invite
                instanceId = $"{instanceNum}~private({userId})~canRequestInvite~region({region})~nonce({nonce})";
                break;
            default: // public
                instanceId = $"{instanceNum}~region({region})";
                break;
        }

        return $"{worldId}:{instanceId}";
    }

    // Avatars - list own, list favorites, select
    //  GET  /avatars?user=me&releaseStatus=all&n=50
    //  GET  /avatars/favorites?n=50
    //  PUT  /avatars/{avatarId}/select
    public async Task<List<JObject>> GetOwnAvatarsAsync()
    {
        if (!IsLoggedIn) return new();
        var all = new List<JObject>();
        try
        {
            for (int offset = 0; offset < 500; offset += 50)
            {
                var resp = await _http.GetAsync(
                    $"{BASE}/avatars?user=me&releaseStatus=all&n=50&offset={offset}&sort=updated&order=descending");
                if (!resp.IsSuccessStatusCode) break;
                var arr = JArray.Parse(await resp.Content.ReadAsStringAsync());
                if (arr.Count == 0) break;
                all.AddRange(arr.Cast<JObject>());
                if (arr.Count < 50) break;
                await Task.Delay(300);
            }
            Log($"GetOwnAvatars: found {all.Count}");
        }
        catch (Exception ex) { Log($"GetOwnAvatars exception: {ex.Message}"); }
        return all;
    }

    public async Task<List<JObject>> GetFavoriteAvatarsAsync()
    {
        if (!IsLoggedIn) return new();
        var all = new List<JObject>();
        try
        {
            for (int offset = 0; offset < 500; offset += 50)
            {
                var resp = await _http.GetAsync($"{BASE}/avatars/favorites?n=50&offset={offset}");
                if (!resp.IsSuccessStatusCode) break;
                var arr = JArray.Parse(await resp.Content.ReadAsStringAsync());
                if (arr.Count == 0) break;
                all.AddRange(arr.Cast<JObject>());
                if (arr.Count < 50) break;
                await Task.Delay(300);
            }
            Log($"GetFavoriteAvatars: found {all.Count}");
        }
        catch (Exception ex) { Log($"GetFavoriteAvatars exception: {ex.Message}"); }
        return all;
    }

    public async Task<JArray> SearchAvatarsAsync(string query, int n = 20, int offset = 0)
    {
        // VRChat's official API has NO text search for avatars.
        // We use third-party avatar databases like VRCX does.
        var providers = new[]
        {
            $"https://avtrdb.com/vrcx_search.php?search={Uri.EscapeDataString(query)}&n={n}&offset={offset}",
            $"https://avtr.just-h.party/vrcx_search.php?search={Uri.EscapeDataString(query)}&n={n}&offset={offset}",
        };

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VRCX/2025.03.01");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        foreach (var url in providers)
        {
            try
            {
                Log($"SearchAvatars trying: {url}");
                var resp = await client.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();
                var preview = body.Length > 300 ? body[..300] : body;
                Log($"SearchAvatars [{(int)resp.StatusCode}] len={body.Length} preview={preview}");

                if (!resp.IsSuccessStatusCode) continue;
                if (string.IsNullOrWhiteSpace(body) || body == "null" || body == "[]") continue;
                if (body.TrimStart().StartsWith("<")) { Log("SearchAvatars: got HTML, skipping"); continue; }

                var parsed = Newtonsoft.Json.Linq.JToken.Parse(body);
                JArray results = null;

                if (parsed is JArray arr && arr.Count > 0) results = arr;
                else if (parsed is JObject obj)
                {
                    results = (obj["avatars"] ?? obj["data"] ?? obj["results"] ?? obj["items"]) as JArray;
                    if (results == null || results.Count == 0)
                        if (obj["id"] != null) results = new JArray(obj);
                }

                if (results != null && results.Count > 0)
                {
                    Log($"SearchAvatars: found {results.Count} results from {url}");
                    return results;
                }
                Log($"SearchAvatars: parsed OK but empty from {url}");
            }
            catch (Exception ex)
            {
                Log($"SearchAvatars exception for {url}: {ex.Message}");
            }
        }

        Log("SearchAvatars: all providers returned no results");
        return new JArray();
    }

    public async Task<bool> SelectAvatarAsync(string avatarId)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp = await _http.PutAsync($"{BASE}/avatars/{avatarId}/select", content);
            var body = await resp.Content.ReadAsStringAsync();
            Log($"SelectAvatar {avatarId}: {(int)resp.StatusCode}");
            if (resp.IsSuccessStatusCode)
            {
                // Update local user data with new avatar
                try { CurrentUserRaw = JObject.Parse(body); } catch { }
                return true;
            }
            return false;
        }
        catch (Exception ex) { Log($"SelectAvatar exception: {ex.Message}"); return false; }
    }

    // Invite friend to your instance
    public async Task<bool> InviteFriendAsync(string userId)
    {
        if (!IsLoggedIn) return false;
        try
        {
            // Use GetMyLocationAsync for reliable location; /auth/user often returns null
            var loc = await GetMyLocationAsync();
            if (string.IsNullOrEmpty(loc) || loc == "offline" || loc == "traveling")
            {
                Log("InviteFriend: no valid instance to invite to");
                return false;
            }
            var worldId = loc.Contains(':') ? loc.Split(':')[0] : loc;
            var payload = new JObject { ["instanceId"] = loc, ["worldId"] = worldId };
            var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"{BASE}/invite/{userId}", content);
            var body = await resp.Content.ReadAsStringAsync();
            Log($"InviteFriend response: {(int)resp.StatusCode} body: {body[..Math.Min(200, body.Length)]}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"InviteFriend exception: {ex.Message}"); return false; }
    }

    // Request invite from friend
    public async Task<bool> RequestInviteAsync(string userId)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var payload = new JObject { ["instanceId"] = "" };
            var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"{BASE}/requestInvite/{userId}", content);
            var body = await resp.Content.ReadAsStringAsync();
            Log($"RequestInvite response: {(int)resp.StatusCode} body: {body[..Math.Min(200, body.Length)]}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"RequestInvite exception: {ex.Message}"); return false; }
    }

    // Search - users, worlds, groups
    public async Task<JArray> SearchUsersAsync(string query, int n = 20, int offset = 0)
    {
        if (!IsLoggedIn || string.IsNullOrEmpty(query)) return new JArray();
        try
        {
            var resp = await _http.GetAsync($"{BASE}/users?search={Uri.EscapeDataString(query)}&n={n}&offset={offset}");
            if (resp.IsSuccessStatusCode) return JArray.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { Log($"SearchUsers exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JArray> SearchWorldsAsync(string query, int n = 20, int offset = 0)
    {
        if (!IsLoggedIn || string.IsNullOrEmpty(query)) return new JArray();
        try
        {
            var resp = await _http.GetAsync($"{BASE}/worlds?search={Uri.EscapeDataString(query)}&n={n}&offset={offset}&sort=relevance");
            if (resp.IsSuccessStatusCode) return JArray.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { Log($"SearchWorlds exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JArray> SearchGroupsAsync(string query, int n = 20, int offset = 0)
    {
        if (!IsLoggedIn || string.IsNullOrEmpty(query)) return new JArray();
        try
        {
            var resp = await _http.GetAsync($"{BASE}/groups?query={Uri.EscapeDataString(query)}&n={n}&offset={offset}");
            if (resp.IsSuccessStatusCode) return JArray.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { Log($"SearchGroups exception: {ex.Message}"); }
        return new JArray();
    }

    // Groups - user's groups, get, join, leave
    public async Task<JArray> GetUserGroupsAsync()
    {
        if (!IsLoggedIn || CurrentUserId == null) return new JArray();
        return await GetUserGroupsByIdAsync(CurrentUserId);
    }

    public async Task<JArray> GetUserGroupsByIdAsync(string userId)
    {
        if (!IsLoggedIn) return new JArray();
        try
        {
            var resp = await _http.GetAsync($"{BASE}/users/{userId}/groups");
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                var arr = JArray.Parse(body);
                Log($"GetUserGroups({userId}): {arr.Count} items");
                return arr;
            }
            else
                Log($"GetUserGroups({userId}) failed: {(int)resp.StatusCode}");
        }
        catch (Exception ex) { Log($"GetUserGroups({userId}) exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JObject?> GetUserRepresentedGroupAsync(string userId)
    {
        if (!IsLoggedIn) return null;
        try
        {
            var resp = await _http.GetAsync($"{BASE}/users/{userId}/groups/represented");
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                var obj = JObject.Parse(body);
                Log($"GetRepresentedGroup({userId}): {obj["name"]}");
                return obj;
            }
            Log($"GetRepresentedGroup({userId}): {(int)resp.StatusCode}");
        }
        catch (Exception ex) { Log($"GetRepresentedGroup({userId}) exception: {ex.Message}"); }
        return null;
    }

    public async Task<(JArray mutuals, bool optedOut)> GetUserMutualsAsync(string userId)
    {
        if (!IsLoggedIn) return (new JArray(), false);
        try
        {
            // /users/{id}/mutuals returns only counts; /users/{id}/mutuals/friends returns the actual user objects
            var resp = await _http.GetAsync($"{BASE}/users/{userId}/mutuals/friends");
            var body = await resp.Content.ReadAsStringAsync();
            Log($"GetUserMutuals/friends({userId}): status={(int)resp.StatusCode}, bodyLen={body.Length}, preview={body.Substring(0, Math.Min(300, body.Length))}");

            if (resp.IsSuccessStatusCode)
            {
                var token = Newtonsoft.Json.Linq.JToken.Parse(body);
                JArray arr;
                if (token is JArray a)
                    arr = a;
                else if (token is JObject obj)
                {
                    arr = obj["friends"] as JArray
                       ?? obj["mutuals"] as JArray
                       ?? obj["users"] as JArray
                       ?? obj["data"] as JArray
                       ?? new JArray();
                    if (arr.Count == 0)
                        Log($"GetUserMutuals/friends({userId}): object keys={string.Join(", ", obj.Properties().Select(p => p.Name))}");
                }
                else
                    arr = new JArray();

                Log($"GetUserMutuals/friends({userId}): parsed {arr.Count} mutual friends");
                return (arr, false);
            }
            if ((int)resp.StatusCode == 403)
            {
                Log($"GetUserMutuals/friends({userId}): 403 – user opted out. Body: {body.Substring(0, Math.Min(200, body.Length))}");
                return (new JArray(), true);
            }
            Log($"GetUserMutuals/friends({userId}): unexpected status {(int)resp.StatusCode}");
        }
        catch (Exception ex) { Log($"GetUserMutuals/friends({userId}) exception: {ex.Message}"); }
        return (new JArray(), false);
    }

    public async Task<JObject?> GetGroupAsync(string groupId)
    {
        if (!IsLoggedIn) return null;
        try
        {
            var resp = await _http.GetAsync($"{BASE}/groups/{groupId}?includeRoles=true");
            if (resp.IsSuccessStatusCode) return JObject.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { Log($"GetGroup exception: {ex.Message}"); }
        return null;
    }

    public async Task<bool> JoinGroupAsync(string groupId)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"{BASE}/groups/{groupId}/join", content);
            Log($"JoinGroup {groupId}: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"JoinGroup exception: {ex.Message}"); return false; }
    }

    public async Task<bool> LeaveGroupAsync(string groupId)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"{BASE}/groups/{groupId}/leave", content);
            Log($"LeaveGroup {groupId}: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"LeaveGroup exception: {ex.Message}"); return false; }
    }

    public async Task<JArray> GetGroupPostsAsync(string groupId, int n = 10)
    {
        if (!IsLoggedIn) return new JArray();
        try
        {
            var resp = await _http.GetAsync($"{BASE}/groups/{groupId}/posts?n={n}&offset=0");
            var body = await resp.Content.ReadAsStringAsync();
            Log($"GetGroupPosts({groupId}): status={(int)resp.StatusCode}, bodyLen={body.Length}, preview={body.Substring(0, Math.Min(300, body.Length))}");
            if (resp.IsSuccessStatusCode)
            {
                var token = Newtonsoft.Json.Linq.JToken.Parse(body);
                if (token is JArray arr) return arr;
                // Could be an object wrapper
                if (token is JObject obj)
                {
                    var posts = obj["posts"] as JArray ?? obj["data"] as JArray;
                    if (posts != null) return posts;
                    Log($"GetGroupPosts: object keys={string.Join(", ", obj.Properties().Select(p => p.Name))}");
                }
            }
        }
        catch (Exception ex) { Log($"GetGroupPosts exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JArray> GetGroupInstancesAsync(string groupId)
    {
        if (!IsLoggedIn) return new JArray();
        try
        {
            var resp = await _http.GetAsync($"{BASE}/groups/{groupId}/instances");
            if (resp.IsSuccessStatusCode) return JArray.Parse(await resp.Content.ReadAsStringAsync());
            Log($"GetGroupInstances({groupId}): {(int)resp.StatusCode}");
        }
        catch (Exception ex) { Log($"GetGroupInstances exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JArray> GetGroupGalleryImagesAsync(string groupId, string galleryId, int n = 20)
    {
        if (!IsLoggedIn) return new JArray();
        try
        {
            var resp = await _http.GetAsync($"{BASE}/groups/{groupId}/galleries/{galleryId}?n={n}&offset=0");
            if (resp.IsSuccessStatusCode) return JArray.Parse(await resp.Content.ReadAsStringAsync());
            Log($"GetGroupGallery({groupId}/{galleryId}): {(int)resp.StatusCode}");
        }
        catch (Exception ex) { Log($"GetGroupGallery exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JArray> GetGroupMembersAsync(string groupId, int n = 50, int offset = 0)
    {
        if (!IsLoggedIn) return new JArray();
        try
        {
            var resp = await _http.GetAsync($"{BASE}/groups/{groupId}/members?n={n}&offset={offset}&sort=joinedAt:desc");
            if (resp.IsSuccessStatusCode) return JArray.Parse(await resp.Content.ReadAsStringAsync());
            Log($"GetGroupMembers({groupId}): {(int)resp.StatusCode}");
        }
        catch (Exception ex) { Log($"GetGroupMembers exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JArray> GetGroupEventsAsync(string groupId, int n = 10)
    {
        if (!IsLoggedIn) return new JArray();
        try
        {
            var resp = await _http.GetAsync($"{BASE}/groups/{groupId}/events?n={n}&offset=0");
            if (resp.IsSuccessStatusCode) return JArray.Parse(await resp.Content.ReadAsStringAsync());
            Log($"GetGroupEvents({groupId}): {(int)resp.StatusCode}");
        }
        catch (Exception ex) { Log($"GetGroupEvents exception: {ex.Message}"); }
        return new JArray();
    }

    // Friend requests
    public async Task<bool> SendFriendRequestAsync(string userId)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"{BASE}/user/{userId}/friendRequest", content);
            Log($"FriendRequest {userId}: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"FriendRequest exception: {ex.Message}"); return false; }
    }

    public async Task<bool> UnfriendAsync(string userId)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var resp = await _http.DeleteAsync($"{BASE}/auth/user/friends/{userId}");
            Log($"Unfriend {userId}: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"Unfriend exception: {ex.Message}"); return false; }
    }

    // Notifications
    public async Task<JArray> GetNotificationsAsync()
    {
        if (!IsLoggedIn) return new JArray();
        try
        {
            // VRChat API v1: correct endpoint is /auth/user/notifications
            var resp = await _http.GetAsync($"{BASE}/auth/user/notifications?n=100");
            if (resp.IsSuccessStatusCode)
            {
                var result = JArray.Parse(await resp.Content.ReadAsStringAsync());
                Log($"GetNotifications: got {result.Count} notifications");
                return result;
            }
            else
            {
                var body = await resp.Content.ReadAsStringAsync();
                Log($"GetNotifications failed: {(int)resp.StatusCode} — {body.Substring(0, Math.Min(body.Length, 200))}");
            }
        }
        catch (Exception ex) { Log($"GetNotifications exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<bool> AcceptNotificationAsync(string notifId)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp = await _http.PutAsync($"{BASE}/auth/user/notifications/{notifId}/accept", content);
            Log($"AcceptNotification {notifId}: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"AcceptNotification exception: {ex.Message}"); return false; }
    }

    public async Task<bool> HideNotificationAsync(string notifId)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp = await _http.PutAsync($"{BASE}/auth/user/notifications/{notifId}/hide", content);
            Log($"HideNotification {notifId}: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"HideNotification exception: {ex.Message}"); return false; }
    }

    public async Task<bool> MarkNotificationReadAsync(string notifId)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp = await _http.PutAsync($"{BASE}/auth/user/notifications/{notifId}/see", content);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"MarkNotifRead exception: {ex.Message}"); return false; }
    }

    // Refresh current user data (get latest location, etc.)
    public async Task<JObject?> RefreshCurrentUserAsync()
    {
        if (!IsLoggedIn) return null;
        try
        {
            var resp = await _http.GetAsync($"{BASE}/auth/user");
            if (resp.IsSuccessStatusCode)
            {
                var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
                if (json["requiresTwoFactorAuth"] == null)
                {
                    CurrentUserRaw = json;
                    return json;
                }
            }
        }
        catch (Exception ex) { Log($"RefreshCurrentUser exception: {ex.Message}"); }
        return CurrentUserRaw;
    }

    /// <summary>
    /// Gets the current user's real-time location by fetching /users/{id}.
    /// /auth/user does NOT reliably return location (often null).
    /// /users/{id} returns the actual live location.
    /// </summary>
    public async Task<string?> GetMyLocationAsync()
    {
        if (!IsLoggedIn || CurrentUserId == null) return null;
        try
        {
            Log($"GetMyLocation: GET /users/{CurrentUserId}");
            var resp = await _http.GetAsync($"{BASE}/users/{CurrentUserId}");
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
            {
                var json = JObject.Parse(body);
                var loc = json["location"]?.ToString();
                Log($"GetMyLocation: location=[{loc ?? "NULL"}]");
                return loc;
            }
            Log($"GetMyLocation: HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) { Log($"GetMyLocation exception: {ex.Message}"); }
        return null;
    }

    // Current instance details
    public async Task<JObject?> GetCurrentInstanceAsync()
    {
        if (!IsLoggedIn) return null;
        var loc = await GetMyLocationAsync();
        if (string.IsNullOrEmpty(loc) || loc == "offline" || loc == "private" || loc == "traveling") return null;
        return await GetInstanceAsync(loc);
    }

    // User Notes
    public async Task<JObject?> GetUserNoteAsync(string targetUserId)
    {
        if (!IsLoggedIn) return null;
        try
        {
            // GET /userNotes with n=100 and search through for matching targetUserId
            var resp = await _http.GetAsync($"{BASE}/userNotes?n=100");
            if (!resp.IsSuccessStatusCode) { Log($"GetUserNotes: HTTP {(int)resp.StatusCode}"); return null; }
            var body = await resp.Content.ReadAsStringAsync();
            var arr = JArray.Parse(body);
            foreach (var note in arr)
            {
                if (note["targetUserId"]?.ToString() == targetUserId)
                    return note as JObject;
            }
            return null; // no note found for this user
        }
        catch (Exception ex) { Log($"GetUserNote exception: {ex.Message}"); return null; }
    }

    public async Task<bool> UpdateUserNoteAsync(string targetUserId, string noteText)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var payload = new { targetUserId, note = noteText };
            var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"{BASE}/userNotes", content);
            Log($"UpdateUserNote {targetUserId}: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"UpdateUserNote exception: {ex.Message}"); return false; }
    }

    public async Task LogoutAsync()
    {
        try { await _http.PutAsync($"{BASE}/logout", null); } catch { }
        IsLoggedIn = false;
        CurrentUserRaw = null;
    }

    /// Exposes the authenticated HttpClient for use by ImageCacheService.
    public HttpClient GetHttpClient() => _http;

    // Inventory - Files (icons, gallery, stickers, emojis)

    /// <summary>List files by tag. tag = gallery | icon | sticker | emoji | emojianimated</summary>
    public async Task<JArray> GetInventoryFilesAsync(string tag, int n = 100, int offset = 0)
    {
        if (!IsLoggedIn) return new JArray();
        try
        {
            var url = $"{BASE}/files?tag={Uri.EscapeDataString(tag)}&n={n}&offset={offset}";
            Log($"GetInventoryFiles tag={tag} n={n} offset={offset}");
            var resp = await _http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            Log($"GetInventoryFiles response: {(int)resp.StatusCode} len={body.Length}");
            if (resp.IsSuccessStatusCode)
                return JArray.Parse(body);
            Log($"GetInventoryFiles error: {body[..Math.Min(200, body.Length)]}");
        }
        catch (Exception ex) { Log($"GetInventoryFiles exception: {ex.Message}"); }
        return new JArray();
    }

    /// <summary>Upload a PNG image as icon/gallery/sticker/emoji/emojianimated.</summary>
    public async Task<(bool ok, JObject? file, string error)> UploadInventoryImageAsync(byte[] bytes, string tag)
    {
        if (!IsLoggedIn) return (false, null, "Not logged in");
        try
        {
            using var form = new System.Net.Http.MultipartFormDataContent();
            form.Add(new StringContent(tag), "tag");
            var fileContent = new System.Net.Http.ByteArrayContent(bytes);
            fileContent.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("image/png");
            form.Add(fileContent, "file", "upload.png");

            Log($"UploadInventoryImage tag={tag} size={bytes.Length}");
            var resp = await _http.PostAsync($"{BASE}/file/image", form);
            var body = await resp.Content.ReadAsStringAsync();
            Log($"UploadInventoryImage response: {(int)resp.StatusCode} preview={body[..Math.Min(200, body.Length)]}");
            if (resp.IsSuccessStatusCode)
                return (true, JObject.Parse(body), "");
            var errMsg = TryGetApiError(body) ?? $"HTTP {(int)resp.StatusCode}";
            return (false, null, errMsg);
        }
        catch (Exception ex) { Log($"UploadInventoryImage exception: {ex.Message}"); return (false, null, ex.Message); }
    }

    /// <summary>Delete an entire file (gallery/icon/emoji/sticker). Uses DELETE /file/{fileId}, same as VRCX.</summary>
    public async Task<bool> DeleteInventoryFileAsync(string fileId)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var resp = await _http.DeleteAsync($"{BASE}/file/{fileId}");
            var body = await resp.Content.ReadAsStringAsync();
            Log($"DeleteInventoryFile {fileId}: {(int)resp.StatusCode} body={body[..Math.Min(300, body.Length)]}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"DeleteInventoryFile exception: {ex.Message}"); return false; }
    }

    // Inventory - Prints

    public async Task<JArray> GetUserPrintsAsync(string userId)
    {
        if (!IsLoggedIn) return new JArray();

        const int pageSize = 100;
        const int maxPages = 20; // safety cap: 2000 prints max
        var all = new JArray();

        for (int page = 0; page < maxPages; page++)
        {
            int offset = page * pageSize;
            try
            {
                var url = $"{BASE}/prints/user/{Uri.EscapeDataString(userId)}?n={pageSize}&offset={offset}";
                Log($"GetUserPrints userId={userId} page={page} offset={offset}");
                var resp = await _http.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();
                Log($"GetUserPrints response: {(int)resp.StatusCode} len={body.Length}");
                if (!resp.IsSuccessStatusCode) break;

                JArray pageArr;
                var token = JToken.Parse(body);
                if      (token is JArray a)  pageArr = a;
                else if (token is JObject o) pageArr = o["prints"] as JArray ?? o["data"] as JArray ?? new JArray();
                else break;

                foreach (var item in pageArr) all.Add(item);

                if (pageArr.Count < pageSize) break; // last page reached
            }
            catch (Exception ex) { Log($"GetUserPrints exception: {ex.Message}"); break; }
        }

        Log($"GetUserPrints total fetched: {all.Count}");
        return all;
    }

    public async Task<bool> DeletePrintAsync(string printId)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var resp = await _http.DeleteAsync($"{BASE}/prints/{printId}");
            var body = await resp.Content.ReadAsStringAsync();
            Log($"DeletePrint {printId}: {(int)resp.StatusCode} body={body[..Math.Min(300, body.Length)]}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"DeletePrint exception: {ex.Message}"); return false; }
    }

    // Inventory - Items

    public async Task<(JArray items, int totalCount)> GetInventoryItemsAsync(int n = 100, int offset = 0)
    {
        if (!IsLoggedIn) return (new JArray(), 0);
        try
        {
            var url = $"{BASE}/inventory?n={n}&offset={offset}";
            Log($"GetInventoryItems n={n} offset={offset}");
            var resp = await _http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            Log($"GetInventoryItems response: {(int)resp.StatusCode} len={body.Length} preview={body[..Math.Min(300, body.Length)]}");
            if (resp.IsSuccessStatusCode)
            {
                var token = JToken.Parse(body);
                if (token is JArray arr) return (arr, arr.Count);
                if (token is JObject obj)
                {
                    var data = obj["data"] as JArray ?? new JArray();
                    var total = obj["totalCount"]?.Value<int>() ?? data.Count;
                    return (data, total);
                }
            }
            else Log($"GetInventoryItems error: {body[..Math.Min(200, body.Length)]}");
        }
        catch (Exception ex) { Log($"GetInventoryItems exception: {ex.Message}"); }
        return (new JArray(), 0);
    }

    // Favorite Friends

    /// <summary>Returns all favorited friends as Favorite objects {id, favoriteId, type, tags}.</summary>
    public async Task<JArray> GetFavoriteFriendsAsync()
    {
        if (!IsLoggedIn) return new JArray();
        try
        {
            var all = new JArray();
            for (int offset = 0; offset < 2000; offset += 100)
            {
                var resp = await _http.GetAsync($"{BASE}/favorites?type=friend&n=100&offset={offset}");
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) break;
                var batch = JArray.Parse(body);
                foreach (var item in batch) all.Add(item);
                if (batch.Count < 100) break;
            }
            Log($"GetFavoriteFriends: {all.Count} entries");
            return all;
        }
        catch (Exception ex) { Log($"GetFavoriteFriends exception: {ex.Message}"); return new JArray(); }
    }

    /// <summary>Adds a friend to favorites (group_0). Returns the Favorite object or null on error.</summary>
    public async Task<JObject?> AddFavoriteFriendAsync(string userId)
    {
        if (!IsLoggedIn) return null;
        try
        {
            var json = JsonConvert.SerializeObject(new { type = "friend", favoriteId = userId, tags = new[] { "group_0" } });
            var resp = await _http.PostAsync($"{BASE}/favorites",
                new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) { Log($"AddFavoriteFriend error: {body[..Math.Min(200, body.Length)]}"); return null; }
            return JObject.Parse(body);
        }
        catch (Exception ex) { Log($"AddFavoriteFriend exception: {ex.Message}"); return null; }
    }

    /// <summary>Removes a favorite by its fvrt_xxx id.</summary>
    public async Task<bool> RemoveFavoriteFriendAsync(string fvrtId)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var resp = await _http.DeleteAsync($"{BASE}/favorites/{fvrtId}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"RemoveFavoriteFriend exception: {ex.Message}"); return false; }
    }

    private static string? TryGetApiError(string body)
    {
        try
        {
            var j = JObject.Parse(body);
            return j["error"]?["message"]?.ToString() ?? j["message"]?.ToString();
        }
        catch { return null; }
    }

    // Helper to extract user image with fallback chain
    public static string GetUserImage(JObject user)
    {
        return user["profilePicOverrideThumbnail"]?.ToString() is string s1 && !string.IsNullOrEmpty(s1) ? s1 :
               user["profilePicOverride"]?.ToString() is string s2 && !string.IsNullOrEmpty(s2) ? s2 :
               user["currentAvatarThumbnailImageUrl"]?.ToString() is string s3 && !string.IsNullOrEmpty(s3) ? s3 :
               user["userIcon"]?.ToString() is string s4 && !string.IsNullOrEmpty(s4) ? s4 : "";
    }

    // Helper to parse location string
    public static (string worldId, string instanceId, string instanceType) ParseLocation(string? location)
    {
        if (string.IsNullOrEmpty(location) || location == "private" || location == "offline" || location == "traveling")
            return ("", "", location ?? "private");

        var worldId = "";
        var instanceId = "";
        var instanceType = "public";

        if (location.Contains(':'))
        {
            var parts = location.Split(':', 2);
            worldId = parts[0];
            instanceId = parts[1];

            if (instanceId.Contains("~private(")) instanceType = "private";
            else if (instanceId.Contains("~friends+(")) instanceType = "friends+";
            else if (instanceId.Contains("~friends(")) instanceType = "friends";
            else if (instanceId.Contains("~hidden(")) instanceType = "hidden";
            else if (instanceId.Contains("~group("))
            {
                // Parse groupAccessType: public, plus, members
                var gatMatch = System.Text.RegularExpressions.Regex.Match(instanceId, @"groupAccessType\(([^)]+)\)");
                var gat = gatMatch.Success ? gatMatch.Groups[1].Value.ToLower() : "";
                if (gat == "public") instanceType = "group-public";
                else if (gat == "plus") instanceType = "group-plus";
                else if (gat == "members") instanceType = "group-members";
                else instanceType = "group";
            }
            else instanceType = "public";
        }

        return (worldId, instanceId, instanceType);
    }
}
