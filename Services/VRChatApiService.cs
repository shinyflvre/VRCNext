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
    private const string UA = "VRCNext/2026.1.0 contact@vrcnext.app";

    public bool IsLoggedIn { get; private set; }
    public JObject? CurrentUserRaw { get; private set; }
    public string? CurrentUserId => CurrentUserRaw?["id"]?.ToString();
    public string? CurrentAvatarId => CurrentUserRaw?["currentAvatar"]?.ToString();
    public bool HasVrcPlus => CurrentUserRaw?["tags"] is JArray tags && tags.Any(t => t.ToString() == "system_supporter");

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

    /// <summary>Logs every HTTP request and response status for full visibility.</summary>
    private class LoggingHandler : DelegatingHandler
    {
        private readonly Action<string> _log;
        public LoggingHandler(HttpMessageHandler inner, Action<string> log) : base(inner) => _log = log;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri?.PathAndQuery ?? "?";
            _log($"[REST] {request.Method} {path}");
            var resp = await base.SendAsync(request, ct);
            var status = (int)resp.StatusCode;
            var flag = status >= 400 ? " !!!" : "";
            _log($"[REST] {request.Method} {path} → {status} {resp.StatusCode}{flag}");
            return resp;
        }
    }

    public VRChatApiService()
    {
        var inner = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
        };
        _http = new HttpClient(new LoggingHandler(inner, Log));
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

    // Friends — 3 parallel workers, no artificial delays
    public Task<List<JObject>> GetOnlineFriendsAsync()  => FetchFriendsParallelAsync(offline: false);
    public Task<List<JObject>> GetOfflineFriendsAsync() => FetchFriendsParallelAsync(offline: true);

    private async Task<List<JObject>> FetchFriendsParallelAsync(bool offline)
    {
        var all  = new List<JObject>();
        if (!IsLoggedIn) { if (!offline) Log("GetFriends: not logged in"); return all; }

        const int pageSize   = 50;
        const int concurrency = 3;
        var nextOffset = 0;
        var done       = false;
        var lck        = new object();

        async Task Worker()
        {
            while (true)
            {
                int myOffset;
                lock (lck) { if (done) return; myOffset = nextOffset; nextOffset += pageSize; }
                try
                {
                    var flag = offline ? "true" : "false";
                    var resp = await _http.GetAsync($"{BASE}/auth/user/friends?offline={flag}&n={pageSize}&offset={myOffset}");
                    var body = await resp.Content.ReadAsStringAsync();
                    Log($"Fetching {(offline ? "offline" : "online")} friends: offset={myOffset} → {(int)resp.StatusCode}");
                    if (!resp.IsSuccessStatusCode)
                    {
                        Log($"Friends error: {body[..Math.Min(200, body.Length)]}");
                        lock (lck) done = true; return;
                    }
                    var batch = JArray.Parse(body);
                    Log($"Friends batch at {myOffset}: {batch.Count}");
                    lock (all) foreach (var x in batch) all.Add((JObject)x);
                    if (batch.Count < pageSize) { lock (lck) done = true; return; }
                }
                catch (Exception ex) { Log($"Friends worker: {ex.Message}"); lock (lck) done = true; return; }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => Worker()));
        Log($"Total {(offline ? "offline" : "online")} friends: {all.Count}");
        return all;
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

    public async Task<bool> SetHomeWorldAsync(string worldId)
    {
        if (!IsLoggedIn || CurrentUserId == null) return false;
        try
        {
            var payload = new JObject { ["homeLocation"] = worldId };
            var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            var resp = await _http.PutAsync($"{BASE}/users/{CurrentUserId}", content);
            Log($"SetHomeWorld({worldId}): {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"SetHomeWorld exception: {ex.Message}"); return false; }
    }

    // Update own profile (bio, pronouns, links, tags, icon, banner)
    public async Task<JObject?> UpdateProfileAsync(string? bio, string? pronouns, List<string>? bioLinks, List<string>? tags, string? userIcon = null, string? profilePicOverride = null)
    {
        if (!IsLoggedIn || CurrentUserId == null) return null;
        try
        {
            var payload = new JObject();
            if (bio != null) payload["bio"] = bio;
            if (pronouns != null) payload["pronouns"] = pronouns;
            if (bioLinks != null) payload["bioLinks"] = JArray.FromObject(bioLinks);
            if (tags != null) payload["tags"] = JArray.FromObject(tags);
            if (userIcon != null) payload["userIcon"] = userIcon;
            if (profilePicOverride != null) payload["profilePicOverride"] = profilePicOverride;
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

    // Get user profile by ID — in-flight deduplication (same userId → shared Task)
    private readonly Dictionary<string, Task<JObject?>> _userFetchTasks = new();

    public Task<JObject?> GetUserAsync(string userId)
    {
        if (!IsLoggedIn || string.IsNullOrEmpty(userId)) return Task.FromResult<JObject?>(null);
        lock (_userFetchTasks)
        {
            if (_userFetchTasks.TryGetValue(userId, out var existing)) return existing;
            var task = FetchUserAsync(userId);
            _userFetchTasks[userId] = task;
            task.ContinueWith(_ => { lock (_userFetchTasks) _userFetchTasks.Remove(userId); });
            return task;
        }
    }

    private async Task<JObject?> FetchUserAsync(string userId)
    {
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

    // Get all favorite groups (world, vrcPlusWorld, avatar, friend)
    // VRC+ world groups have type="vrcPlusWorld" and names like "vrcPlusWorlds1"
    public async Task<List<JObject>> GetFavoriteGroupsAsync()
    {
        var all = new List<JObject>();
        if (!IsLoggedIn) { Log("GetFavoriteGroups: not logged in"); return all; }
        try
        {
            var resp = await _http.GetAsync($"{BASE}/favorite/groups?n=100");
            var body = await resp.Content.ReadAsStringAsync();
            Log($"FavoriteGroups: {(int)resp.StatusCode} len={body.Length}");
            if (resp.IsSuccessStatusCode)
            {
                var arr = JArray.Parse(body);
                Log($"FavoriteGroups: {arr.Count} total groups returned");
                foreach (var item in arr)
                {
                    Log($"  group: type={item["type"]} name={item["name"]} displayName={item["displayName"]}");
                    all.Add((JObject)item);
                }
            }
            else Log($"FavoriteGroups error: {body[..Math.Min(200, body.Length)]}");
        }
        catch (Exception ex) { Log($"FavoriteGroups exception: {ex.Message}"); }
        return all;
    }

    // Update a favorite group's display name
    // type: "world" or "vrcPlusWorld", name: "worlds1", "vrcPlusWorlds1", etc.
    public async Task<bool> UpdateFavoriteGroupAsync(string type, string name, string displayName)
    {
        if (!IsLoggedIn || CurrentUserId == null) return false;
        try
        {
            var body = JsonConvert.SerializeObject(new { displayName });
            var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            var resp = await _http.PutAsync($"{BASE}/favorite/group/{type}/{name}/{CurrentUserId}", content);
            Log($"UpdateFavoriteGroup [{type}/{name}]: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"UpdateFavoriteGroup exception: {ex.Message}"); return false; }
    }

    // Get favorite worlds for a specific group tag (e.g. "worlds1", "vrcPlusWorlds1")
    public async Task<List<JObject>> GetFavoriteWorldsByGroupAsync(string groupTag, int max = 100)
    {
        var all = new List<JObject>();
        if (!IsLoggedIn) return all;
        try
        {
            int offset = 0;
            while (all.Count < max)
            {
                var n = Math.Min(max - all.Count, 100);
                var url = $"{BASE}/worlds/favorites?tag={Uri.EscapeDataString(groupTag)}&n={n}&offset={offset}";
                var resp = await _http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) break;
                var batch = JArray.Parse(await resp.Content.ReadAsStringAsync());
                if (batch.Count == 0) break;
                foreach (var item in batch) all.Add((JObject)item);
                if (batch.Count < n) break;
                offset += batch.Count;
                await Task.Delay(200); // only for pagination >100, which never happens for favorites
            }
            Log($"FavoriteWorlds [{groupTag}]: {all.Count} worlds");
        }
        catch (Exception ex) { Log($"FavoriteWorldsByGroup exception [{groupTag}]: {ex.Message}"); }
        return all;
    }

    // Get world info — session cache + in-flight deduplication
    private readonly Dictionary<string, Task<JObject?>> _worldFetchTasks = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, JObject> _worldCache = new();

    public Task<JObject?> GetWorldAsync(string worldId)
    {
        if (!IsLoggedIn || string.IsNullOrEmpty(worldId)) return Task.FromResult<JObject?>(null);
        // Session cache hit — no API call needed
        if (_worldCache.TryGetValue(worldId, out var cached)) return Task.FromResult<JObject?>(cached);
        lock (_worldFetchTasks)
        {
            if (_worldFetchTasks.TryGetValue(worldId, out var existing)) return existing;
            var task = FetchWorldAsync(worldId);
            _worldFetchTasks[worldId] = task;
            task.ContinueWith(_ => { lock (_worldFetchTasks) _worldFetchTasks.Remove(worldId); });
            return task;
        }
    }

    private async Task<JObject?> FetchWorldAsync(string worldId)
    {
        try
        {
            var resp = await _http.GetAsync($"{BASE}/worlds/{worldId}");
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
            {
                var world = JObject.Parse(body);
                _worldCache[worldId] = world;
                return world;
            }
        }
        catch (Exception ex) { Log($"GetWorld exception: {ex.Message}"); }
        return null;
    }

    // Get avatar info by ID
    public async Task<JObject?> GetAvatarAsync(string avatarId)
    {
        if (!IsLoggedIn) return null;
        try
        {
            var resp = await _http.GetAsync($"{BASE}/avatars/{avatarId}");
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode) return JObject.Parse(body);
            Log($"GetAvatar {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        }
        catch (Exception ex) { Log($"GetAvatar exception: {ex.Message}"); }
        return null;
    }

    public async Task<int> GetOnlineCountAsync()
    {
        try
        {
            var resp = await _http.GetAsync($"{BASE}/visits");
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode && int.TryParse(body.Trim(), out var count))
                return count;
        }
        catch (Exception ex) { Log($"GetOnlineCount exception: {ex.Message}"); }
        return 0;
    }

    public async Task<(bool ok, string error)> UpdateAvatarAsync(string avatarId, string name, string description, string releaseStatus, List<string> tags)
    {
        if (!IsLoggedIn) return (false, "Not logged in");
        try
        {
            var body = JsonConvert.SerializeObject(new { name, description, releaseStatus, tags });
            var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            var resp = await _http.PutAsync($"{BASE}/avatars/{avatarId}", content);
            var respBody = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode) return (true, "");
            Log($"UpdateAvatar {(int)resp.StatusCode}: {respBody[..Math.Min(200, respBody.Length)]}");
            return (false, $"API error {(int)resp.StatusCode}");
        }
        catch (Exception ex) { Log($"UpdateAvatar exception: {ex.Message}"); return (false, ex.Message); }
    }

    // Close (delete) a VRChat instance via API — owner only
    // Fetches full instance ID (with nonce) first, then sends DELETE with hardClose=true
    public async Task<bool> CloseInstanceAsync(string location)
    {
        if (!IsLoggedIn || string.IsNullOrEmpty(location)) return false;
        try
        {
            // Fetch the live instance to get the full id (includes ~nonce(...))
            var inst = await GetInstanceAsync(location);
            if (inst == null)
            {
                Log("CloseInstance: instance not found (already gone)");
                return true; // Already gone, treat as success
            }
            // inst["location"] = "wrld_xxx:12345~hidden(...)~nonce(...)" — split at first colon
            var fullLoc = inst["location"]?.ToString() ?? "";
            var colonIdx = fullLoc.IndexOf(':');
            if (colonIdx < 0) return false;
            var worldId    = fullLoc[..colonIdx];
            var instanceId = fullLoc[(colonIdx + 1)..];  // "12345~hidden(...)~region(eu)~nonce(xxx)"
            var resp = await _http.DeleteAsync($"{BASE}/instances/{worldId}:{instanceId}?hardClose=true");
            var body = await resp.Content.ReadAsStringAsync();
            Log($"CloseInstance {(int)resp.StatusCode}: {body[..Math.Min(120, body.Length)]}");
            if ((int)resp.StatusCode == 403 && body.Contains("already closed")) return true;
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"CloseInstance exception: {ex.Message}"); return false; }
    }

    // Get fresh current user location from API (more reliable than log watcher alone)
    public async Task<string?> GetCurrentUserLocationAsync()
    {
        if (!IsLoggedIn) return null;
        try
        {
            var resp = await _http.GetAsync($"{BASE}/auth/user");
            if (!resp.IsSuccessStatusCode) return null;
            var obj = JObject.Parse(await resp.Content.ReadAsStringAsync());
            CurrentUserRaw = obj; // keep CurrentUserRaw fresh
            return obj["currentLocation"]?.ToString();
        }
        catch { return null; }
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
            case "hidden": // friends+
                instanceId = $"{instanceNum}~hidden({userId})~region({region})~nonce({nonce})";
                break;
            case "invite_plus": // invite+
                instanceId = $"{instanceNum}~private({userId})~canRequestInvite~region({region})~nonce({nonce})";
                break;
            case "private": // invite
                instanceId = $"{instanceNum}~private({userId})~region({region})~nonce({nonce})";
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

    public async Task<List<JObject>> GetFavoriteAvatarsByGroupAsync(string groupTag, int max = 100)
    {
        var all = new List<JObject>();
        if (!IsLoggedIn) return all;
        try
        {
            int offset = 0;
            while (all.Count < max)
            {
                var n = Math.Min(max - all.Count, 100);
                var url = $"{BASE}/avatars/favorites?tag={Uri.EscapeDataString(groupTag)}&n={n}&offset={offset}";
                var resp = await _http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) break;
                var batch = JArray.Parse(await resp.Content.ReadAsStringAsync());
                if (batch.Count == 0) break;
                foreach (var item in batch) all.Add((JObject)item);
                if (batch.Count < n) break;
                offset += batch.Count;
                await Task.Delay(200);
            }
            Log($"FavoriteAvatars [{groupTag}]: {all.Count} avatars");
        }
        catch (Exception ex) { Log($"FavoriteAvatarsByGroup exception [{groupTag}]: {ex.Message}"); }
        return all;
    }

    /// <summary>Adds an avatar to a favorite group. If oldFvrtId is set, removes it first (move between groups).</summary>
    public async Task<(bool ok, string result)> AddAvatarFavoriteAsync(string avatarId, string groupName, string groupType = "avatar", string? oldFvrtId = null)
    {
        if (!IsLoggedIn) return (false, "Not logged in");
        try
        {
            if (!string.IsNullOrEmpty(oldFvrtId))
            {
                await _http.DeleteAsync($"{BASE}/favorites/{oldFvrtId}");
                await Task.Delay(400);
            }
            var json = JsonConvert.SerializeObject(new { type = groupType, favoriteId = avatarId, tags = new[] { groupName } });
            var resp = await _http.PostAsync($"{BASE}/favorites",
                new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                var errMsg = TryGetApiError(body) ?? $"HTTP {(int)resp.StatusCode}";
                Log($"AddAvatarFavorite error: {errMsg}");
                return (false, errMsg);
            }
            var result = JObject.Parse(body);
            var newFvrtId = result["id"]?.ToString() ?? "";
            return (true, newFvrtId);
        }
        catch (Exception ex) { Log($"AddAvatarFavorite exception: {ex.Message}"); return (false, ex.Message); }
    }

    public async Task<JArray> SearchAvatarsAsync(string query, int n = 20, int page = 0)
    {
        // VRChat's official API has NO text search for avatars.
        // We use avtrdb.com which provides a public avatar search API.
        var url = $"https://api.avtrdb.com/v2/avatar/search?query={Uri.EscapeDataString(query)}&limit={n}&page={page}";

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UA);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        try
        {
            Log($"SearchAvatars: {url}");
            var resp = await client.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            Log($"SearchAvatars [{(int)resp.StatusCode}] len={body.Length}");

            if (!resp.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body)) return new JArray();

            var parsed = Newtonsoft.Json.Linq.JToken.Parse(body);
            if (parsed is JObject obj && obj["avatars"] is JArray arr)
            {
                Log($"SearchAvatars: found {arr.Count} results");
                return arr;
            }
            if (parsed is JArray directArr)
            {
                Log($"SearchAvatars: found {directArr.Count} results");
                return directArr;
            }
        }
        catch (Exception ex)
        {
            Log($"SearchAvatars exception: {ex.Message}");
        }

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
    public async Task<bool> InviteFriendAsync(string userId, string myLocation, int? messageSlot = null)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var loc = myLocation;
            if (string.IsNullOrEmpty(loc) || loc == "offline" || loc == "traveling")
            {
                Log("InviteFriend: no valid instance to invite to");
                return false;
            }
            var worldId = loc.Contains(':') ? loc.Split(':')[0] : loc;
            var payload = new JObject { ["instanceId"] = loc, ["worldId"] = worldId };
            if (messageSlot.HasValue) payload["messageSlot"] = messageSlot.Value;
            var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"{BASE}/invite/{userId}", content);
            var body = await resp.Content.ReadAsStringAsync();
            Log($"InviteFriend response: {(int)resp.StatusCode} body: {body[..Math.Min(200, body.Length)]}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"InviteFriend exception: {ex.Message}"); return false; }
    }

    // Invite friend to your instance with a photo attachment (VRC+)
    // imageUrl: CDN URL (library) or data: URI (upload)
    public async Task<bool> InviteFriendWithPhotoAsync(string userId, string myLocation, string imageUrl, int? messageSlot = null)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var loc = myLocation;
            if (string.IsNullOrEmpty(loc) || loc == "offline" || loc == "traveling")
            { Log("InviteWithPhoto: no valid instance"); return false; }

            // Get image bytes — decode data URI (upload) or download from CDN (library)
            byte[] imgBytes;
            if (imageUrl.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                var comma = imageUrl.IndexOf(',');
                imgBytes = Convert.FromBase64String(imageUrl[(comma + 1)..]);
            }
            else
            {
                imgBytes = await _http.GetByteArrayAsync(imageUrl);
            }

            // Build the 'data' JSON field (no worldId, no fileId — just instanceId + optional messageSlot)
            var dataObj = new JObject { ["instanceId"] = loc };
            if (messageSlot.HasValue) dataObj["messageSlot"] = messageSlot.Value;

            // Send as multipart/form-data
            using var form = new MultipartFormDataContent();
            var imgContent = new ByteArrayContent(imgBytes);
            imgContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(imgContent, "image", "photo.png");
            form.Add(new StringContent(dataObj.ToString(Newtonsoft.Json.Formatting.None)), "data");

            var resp = await _http.PostAsync($"{BASE}/invite/{userId}/photo", form);
            var body = await resp.Content.ReadAsStringAsync();
            Log($"InviteWithPhoto response: {(int)resp.StatusCode} body: {body[..Math.Min(200, body.Length)]}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"InviteWithPhoto exception: {ex.Message}"); return false; }
    }

    // ── VRCN Messenger ────────────────────────────────────────────────────────
    // Protocol: invites to the VRChat home world whose slot text starts with
    // "msg " are intercepted by VRCN as chat messages (auto-hidden, stored locally).
    //
    // 24 slots across 2 types → 1 msg every ~2.5 min sustained:
    //   Virtual slots  0–11 → type "message"        → POST /invite/{userId}
    //   Virtual slots 12–23 → type "requestMessage" → POST /requestInvite/{userId}
    private const string ChatWorldId = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd";
    // Key: virtual slot 0-23, Value: last-used timestamp
    private readonly Dictionary<int, DateTime> _chatSlotTimestamps = new();

    private const int ChatTotalSlots = 24;

    /// How many of the 24 slots are currently in cooldown.
    public int ChatSlotsUsed =>
        _chatSlotTimestamps.Count(x => DateTime.Now - x.Value < TimeSpan.FromMinutes(60));

    /// Fetch real cooldown state from VRChat API for both slot types and populate _chatSlotTimestamps.
    /// Returns (slotsUsed, total).
    public async Task<(int used, int total)> LoadChatSlotStatusAsync()
    {
        if (!IsLoggedIn || CurrentUserId == null) return (0, ChatTotalSlots);
        try
        {
            var t1 = _http.GetAsync($"{BASE}/message/{CurrentUserId}/message");
            Task<HttpResponseMessage>? t2 = _requestMessageSupported
                ? _http.GetAsync($"{BASE}/message/{CurrentUserId}/requestMessage")
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
                        _requestMessageSupported = false;
                        Log("LoadChatSlotStatus: requestMessage returned 400 — disabling endpoint");
                    }
                    continue;
                }
                var arr = JArray.Parse(await resp.Content.ReadAsStringAsync());
                foreach (JObject slot in arr.Cast<JObject>())
                {
                    var idx      = slot["slot"]?.Value<int>() ?? -1;
                    var canEdit  = slot["canBeUpdated"]?.Value<bool>() ?? true;
                    var cooldown = slot["remainingCooldownMinutes"]?.Value<int>() ?? 0;
                    if (idx < 0 || idx >= 12) continue;
                    var vSlot = idx + offset;
                    if (!canEdit && cooldown > 0)
                        // Mark as used: set timestamp so (60 - cooldown) minutes have "elapsed"
                        _chatSlotTimestamps[vSlot] = DateTime.Now - TimeSpan.FromMinutes(60 - cooldown);
                    else
                        // Free: remove any stale entry
                        _chatSlotTimestamps.Remove(vSlot);
                }
            }
            Log($"ChatSlotStatus: {ChatSlotsUsed}/{ChatTotalSlots} in cooldown");
        }
        catch (Exception ex) { Log($"LoadChatSlotStatus exception: {ex.Message}"); }
        return (ChatSlotsUsed, ChatTotalSlots);
    }

    /// Returns the first virtual slot (0-23) not currently in cooldown. -1 if all busy.
    private int GetNextFreeSlot()
    {
        for (int i = 0; i < ChatTotalSlots; i++)
            if (!_chatSlotTimestamps.TryGetValue(i, out var t) || DateTime.Now - t >= TimeSpan.FromMinutes(60))
                return i;
        return -1;
    }

    private async Task<(bool ok, int cooldown)> UpdateChatSlotAsync(int virtualSlot, string text)
    {
        if (CurrentUserId == null) return (false, 0);
        var type     = virtualSlot < 12 ? "message" : "requestMessage";
        var realSlot = virtualSlot < 12 ? virtualSlot : virtualSlot - 12;
        var payload  = new JObject { ["message"] = text };
        var content  = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
        var resp     = await _http.PutAsync($"{BASE}/message/{CurrentUserId}/{type}/{realSlot}", content);
        var body     = await resp.Content.ReadAsStringAsync();
        if (resp.IsSuccessStatusCode) return (true, 0);
        if ((int)resp.StatusCode == 429)
        {
            try { return (false, JObject.Parse(body)["remainingCooldownMinutes"]?.Value<int>() ?? 60); } catch { }
            return (false, 60);
        }
        Log($"UpdateChatSlot({type}/{realSlot}) failed: {(int)resp.StatusCode}");
        return (false, 0);
    }

    public async Task<(bool ok, string error, int slotsUsed)> SendChatMessageAsync(string userId, string text)
    {
        if (!IsLoggedIn || CurrentUserId == null) return (false, "Not logged in", 0);
        try
        {
            var vSlot = GetNextFreeSlot();
            if (vSlot < 0)
                return (false, $"All {ChatTotalSlots} slots in cooldown", ChatSlotsUsed);

            var (updateOk, cooldown) = await UpdateChatSlotAsync(vSlot, "msg " + text);
            if (!updateOk)
            {
                // Slot was in cooldown despite our local state — update timestamps and report
                _chatSlotTimestamps[vSlot] = DateTime.Now - TimeSpan.FromMinutes(60 - Math.Max(cooldown, 1));
                return (false, $"Slot {vSlot} cooldown: {cooldown} min (refreshing…)", ChatSlotsUsed);
            }

            _chatSlotTimestamps[vSlot] = DateTime.Now;

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
                resp = await _http.PostAsync($"{BASE}/invite/{userId}", content);
                body = await resp.Content.ReadAsStringAsync();
            }
            else
            {
                var realSlot = vSlot - 12;
                var payload  = new JObject { ["messageSlot"] = realSlot };
                var content  = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
                resp = await _http.PostAsync($"{BASE}/requestInvite/{userId}", content);
                body = await resp.Content.ReadAsStringAsync();
            }

            Log($"SendChatMessage({userId}) vSlot={vSlot}: {(int)resp.StatusCode} {body[..Math.Min(150, body.Length)]}");
            return resp.IsSuccessStatusCode
                ? (true, "", ChatSlotsUsed)
                : (false, $"HTTP {(int)resp.StatusCode}", ChatSlotsUsed);
        }
        catch (Exception ex) { Log($"SendChatMessage exception: {ex.Message}"); return (false, ex.Message, 0); }
    }

    // Get user's predefined invite message slots (type "message" = outgoing invites)
    public async Task<JArray?> GetInviteMessagesAsync(string userId)
    {
        if (!IsLoggedIn) return null;
        try
        {
            var resp = await _http.GetAsync($"{BASE}/message/{userId}/message");
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync();
            return JArray.Parse(body);
        }
        catch (Exception ex) { Log($"GetInviteMessages exception: {ex.Message}"); return null; }
    }

    // Update a single invite message slot (60-min cooldown per slot)
    public async Task<(bool ok, JArray? messages, int cooldown)> UpdateInviteMessageAsync(string userId, int slot, string message)
    {
        if (!IsLoggedIn) return (false, null, 0);
        try
        {
            var payload = new JObject { ["message"] = message };
            var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            var resp = await _http.PutAsync($"{BASE}/message/{userId}/message/{slot}", content);
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
            {
                var arr = JArray.Parse(body);
                return (true, arr, 0);
            }
            if ((int)resp.StatusCode == 429)
            {
                // Extract remainingCooldownMinutes from error body if possible
                try { var err = JObject.Parse(body); return (false, null, err["remainingCooldownMinutes"]?.Value<int>() ?? 60); } catch { }
                return (false, null, 60);
            }
            Log($"UpdateInviteMessage {slot} failed: {(int)resp.StatusCode} {body[..Math.Min(200,body.Length)]}");
            return (false, null, 0);
        }
        catch (Exception ex) { Log($"UpdateInviteMessage exception: {ex.Message}"); return (false, null, 0); }
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

    public async Task<JArray> GetUserBadgesAsync(string userId)
    {
        if (!IsLoggedIn || string.IsNullOrEmpty(userId)) return new JArray();
        try
        {
            var resp = await _http.GetAsync($"{BASE}/users/{Uri.EscapeDataString(userId)}/badges");
            if (resp.IsSuccessStatusCode) return JArray.Parse(await resp.Content.ReadAsStringAsync());
            Log($"GetUserBadges({userId}) failed: {(int)resp.StatusCode}");
        }
        catch (Exception ex) { Log($"GetUserBadges({userId}) exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JArray> GetMyWorldsAsync()
    {
        if (!IsLoggedIn) return new JArray();
        try
        {
            var resp = await _http.GetAsync($"{BASE}/worlds?user=me&releaseStatus=all&n=100&sort=updated");
            Log($"GetMyWorlds: {(int)resp.StatusCode}");
            if (resp.IsSuccessStatusCode) return JArray.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { Log($"GetMyWorlds exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JArray> GetUserWorldsAsync(string userId)
    {
        if (!IsLoggedIn || string.IsNullOrEmpty(userId)) return new JArray();
        try
        {
            var resp = await _http.GetAsync($"{BASE}/worlds?userId={Uri.EscapeDataString(userId)}&releaseStatus=public&n=100&sort=updated");
            if (resp.IsSuccessStatusCode) return JArray.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { Log($"GetUserWorlds exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JArray> GetRecentWorldsAsync()
    {
        if (!IsLoggedIn) return new JArray();
        try
        {
            var resp = await _http.GetAsync($"{BASE}/instances/recent");
            if (!resp.IsSuccessStatusCode) return new JArray();
            // Response is an array of location strings: "wrld_xxx:12345~public"
            var locations = JArray.Parse(await resp.Content.ReadAsStringAsync());
            var seen = new HashSet<string>();
            var worldIds = new List<string>();
            foreach (var loc in locations)
            {
                var locStr = loc.ToString();
                var worldId = locStr.Contains(':') ? locStr.Split(':')[0] : locStr;
                if (worldId.StartsWith("wrld_") && seen.Add(worldId))
                    worldIds.Add(worldId);
            }
            var tasks = worldIds.Take(16).Select(async wid =>
            {
                try { return await GetWorldAsync(wid); }
                catch { return null; }
            });
            var worlds = await Task.WhenAll(tasks);
            var result = new JArray();
            foreach (var w in worlds)
                if (w != null) result.Add(w);
            return result;
        }
        catch (Exception ex) { Log($"GetRecentWorlds exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JArray> GetPopularWorldsAsync(int n = 32)
    {
        if (!IsLoggedIn) return new JArray();
        try
        {
            var resp = await _http.GetAsync($"{BASE}/worlds?sort=popularity&order=descending&releaseStatus=public&n={n}");
            if (resp.IsSuccessStatusCode) return JArray.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { Log($"GetPopularWorlds exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JArray> GetActiveWorldsAsync(int n = 32)
    {
        if (!IsLoggedIn) return new JArray();
        try
        {
            var resp = await _http.GetAsync($"{BASE}/worlds?sort=heat&order=descending&releaseStatus=public&n={n}");
            if (resp.IsSuccessStatusCode) return JArray.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { Log($"GetActiveWorlds exception: {ex.Message}"); }
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
            // Paginate with n=100 until all mutuals are fetched
            const int pageSize = 100;
            var all = new JArray();
            int offset = 0;
            while (true)
            {
                var resp = await _http.GetAsync($"{BASE}/users/{userId}/mutuals/friends?n={pageSize}&offset={offset}");
                var body = await resp.Content.ReadAsStringAsync();
                Log($"GetUserMutuals/friends({userId}): offset={offset}, status={(int)resp.StatusCode}, bodyLen={body.Length}");

                if (resp.IsSuccessStatusCode)
                {
                    var token = Newtonsoft.Json.Linq.JToken.Parse(body);
                    JArray page;
                    if (token is JArray a)
                        page = a;
                    else if (token is JObject obj)
                    {
                        page = obj["friends"] as JArray
                            ?? obj["mutuals"] as JArray
                            ?? obj["users"] as JArray
                            ?? obj["data"] as JArray
                            ?? new JArray();
                        if (page.Count == 0)
                            Log($"GetUserMutuals/friends({userId}): object keys={string.Join(", ", obj.Properties().Select(p => p.Name))}");
                    }
                    else
                        page = new JArray();

                    foreach (var item in page) all.Add(item);
                    Log($"GetUserMutuals/friends({userId}): page {page.Count}, total so far {all.Count}");

                    // If fewer results than requested, we've reached the end
                    if (page.Count < pageSize) break;
                    offset += pageSize;
                }
                else if ((int)resp.StatusCode == 403)
                {
                    Log($"GetUserMutuals/friends({userId}): 403 – user opted out. Body: {body.Substring(0, Math.Min(200, body.Length))}");
                    return (new JArray(), true);
                }
                else
                {
                    Log($"GetUserMutuals/friends({userId}): unexpected status {(int)resp.StatusCode}");
                    break;
                }
            }
            return (all, false);
        }
        catch (Exception ex) { Log($"GetUserMutuals/friends({userId}) exception: {ex.Message}"); }
        return (new JArray(), false);
    }

    public async Task<JArray> GetPlayerModerationsAsync(string type)
    {
        if (!IsLoggedIn) return new JArray();
        try
        {
            var resp = await _http.GetAsync($"{BASE}/auth/user/playermoderations?type={Uri.EscapeDataString(type)}");
            var body = await resp.Content.ReadAsStringAsync();
            Log($"GetPlayerModerations({type}): status={(int)resp.StatusCode}, bodyLen={body.Length}");
            if (resp.IsSuccessStatusCode)
            {
                var token = Newtonsoft.Json.Linq.JToken.Parse(body);
                if (token is JArray arr) return arr;
            }
            else Log($"GetPlayerModerations({type}): error body={body[..Math.Min(200, body.Length)]}");
        }
        catch (Exception ex) { Log($"GetPlayerModerations({type}) exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<bool> ModerateUserAsync(string userId, string type)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var body = new StringContent(
                $"{{\"moderated\":\"{userId}\",\"type\":\"{type}\"}}",
                Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"{BASE}/auth/user/playermoderations", body);
            Log($"ModerateUser({userId},{type}): {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"ModerateUser({userId},{type}) exception: {ex.Message}"); return false; }
    }

    public async Task<bool> UnmoderateUserAsync(string userId, string type)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var body = new StringContent(
                $"{{\"moderated\":\"{userId}\",\"type\":\"{type}\"}}",
                Encoding.UTF8, "application/json");
            var resp = await _http.PutAsync($"{BASE}/auth/user/unplayermoderate", body);
            Log($"UnmoderateUser({userId},{type}): {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"UnmoderateUser({userId},{type}) exception: {ex.Message}"); return false; }
    }

    public async Task<bool> SendBoopAsync(string userId, string? emojiId = null)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var url = string.IsNullOrEmpty(emojiId)
                ? $"{BASE}/users/{userId}/boop"
                : $"{BASE}/users/{userId}/boop?emojiId={Uri.EscapeDataString(emojiId)}";
            var resp = await _http.PostAsync(url, new StringContent("{}", Encoding.UTF8, "application/json"));
            Log($"SendBoop({userId}): {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"SendBoop({userId}) exception: {ex.Message}"); return false; }
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

    public async Task<JArray> GetGroupPostsAsync(string groupId, int n = 10, bool publicOnly = false)
    {
        if (!IsLoggedIn) return new JArray();
        try
        {
            var url = $"{BASE}/groups/{groupId}/posts?n={n}&offset=0{(publicOnly ? "&publicOnly=true" : "")}";
            var resp = await _http.GetAsync(url);
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
        if (!IsLoggedIn || CurrentUserId == null) return new JArray();
        try
        {
            // Use /users/{userId}/instances/groups/{groupId} — works for members AND non-members
            // (VRCX pattern: returns groupPublic instances even if not a member)
            var resp = await _http.GetAsync($"{BASE}/users/{CurrentUserId}/instances/groups/{groupId}");
            var body = await resp.Content.ReadAsStringAsync();
            Log($"GetGroupInstances({groupId}): {(int)resp.StatusCode}, len={body.Length}");
            if (resp.IsSuccessStatusCode)
            {
                var token = JToken.Parse(body);
                // Response shape: { "fetchedAt": "...", "instances": [...] }
                if (token is JObject obj && obj["instances"] is JArray arr) return arr;
                if (token is JArray directArr) return directArr;
            }
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

    public async Task<JArray> GetGroupRoleMembersAsync(string groupId, string roleId)
    {
        if (!IsLoggedIn) return new JArray();
        try
        {
            var resp = await _http.GetAsync($"{BASE}/groups/{groupId}/members?n=100&roleId={Uri.EscapeDataString(roleId)}");
            if (resp.IsSuccessStatusCode) return JArray.Parse(await resp.Content.ReadAsStringAsync());
            Log($"GetGroupRoleMembers({groupId}/{roleId}): {(int)resp.StatusCode}");
        }
        catch (Exception ex) { Log($"GetGroupRoleMembers exception: {ex.Message}"); }
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

    /// <summary>
    /// Searches group members by displayName — fetches pages until 50 matches found or 10 pages exhausted.
    /// VRChat has no server-side search for group members, so we paginate and filter client-side.
    /// </summary>
    public async Task<JArray> SearchGroupMembersAsync(string groupId, string query)
    {
        if (!IsLoggedIn) return new JArray();
        var matches = new JArray();
        int offset = 0;
        const int pageSize = 100;
        const int maxPages = 10;
        for (int page = 0; page < maxPages; page++)
        {
            try
            {
                var resp = await _http.GetAsync($"{BASE}/groups/{groupId}/members?n={pageSize}&offset={offset}&sort=joinedAt:desc");
                if (!resp.IsSuccessStatusCode) break;
                var page_data = JArray.Parse(await resp.Content.ReadAsStringAsync());
                foreach (var m in page_data)
                {
                    var name = m["user"]?["displayName"]?.ToString() ?? m["displayName"]?.ToString() ?? "";
                    if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
                        matches.Add(m);
                }
                if (page_data.Count < pageSize) break; // last page
                offset += pageSize;
                if (matches.Count >= 50) break; // enough results
            }
            catch (Exception ex) { Log($"SearchGroupMembers exception: {ex.Message}"); break; }
        }
        return matches;
    }

    public async Task<JArray> GetCalendarEventsAsync(string filter = "all", int year = 0, int month = 0)
    {
        if (!IsLoggedIn) return new JArray();
        try
        {
            var now = DateTime.UtcNow;
            int y = year  > 0 ? year  : now.Year;
            int m = month > 0 ? month : now.Month;
            // Correct parameter name is "date" (not "monthDate")
            var dateStr = Uri.EscapeDataString(new DateTime(y, m, 1, 0, 0, 0, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));

            var allEvents  = new JArray();
            int pageOffset = 0;
            const int pageSize = 100;

            while (true)
            {
                // "featured" uses the same /calendar endpoint — filtering to featured events is done client-side
                // because /calendar/featured only returns upcoming featured events and ignores past months
                var endpoint = filter switch {
                    "following" => $"{BASE}/calendar/following?n={pageSize}&offset={pageOffset}&date={dateStr}",
                    _           => $"{BASE}/calendar?n={pageSize}&offset={pageOffset}&date={dateStr}"
                };
                var resp = await _http.GetAsync(endpoint);
                var body = await resp.Content.ReadAsStringAsync();
                Log($"GetCalendarEvents({filter},{y}/{m},off={pageOffset}): {(int)resp.StatusCode}, len={body.Length}, preview={body[..Math.Min(300,body.Length)]}");
                if (!resp.IsSuccessStatusCode) break;

                JArray? page = null;
                var token = JToken.Parse(body);
                if (token is JArray arr) page = arr;
                else if (token is JObject obj)
                    foreach (var key in new[] { "results", "events", "data", "items" })
                        if (obj[key] is JArray found) { page = found; break; }

                if (page == null || page.Count == 0) break;
                foreach (var ev in page) allEvents.Add(ev);
                if (page.Count < pageSize) break;
                pageOffset += pageSize;
                if (pageOffset >= pageSize * 5) break; // safety: max 5 pages
            }

            Log($"GetCalendarEvents({filter},{y}/{m}): total={allEvents.Count}");
            return allEvents;
        }
        catch (Exception ex) { Log($"GetCalendarEvents exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JObject?> GetCalendarEventAsync(string groupId, string calendarId)
    {
        if (!IsLoggedIn) return null;
        try
        {
            var resp = await _http.GetAsync($"{BASE}/calendar/{groupId}/{calendarId}");
            var body = await resp.Content.ReadAsStringAsync();
            Log($"GetCalendarEvent({groupId},{calendarId}): {(int)resp.StatusCode}");
            if (resp.IsSuccessStatusCode) return JObject.Parse(body);
        }
        catch (Exception ex) { Log($"GetCalendarEvent exception: {ex.Message}"); }
        return null;
    }

    public async Task<bool> FollowEventAsync(string groupId, string calendarId, bool follow)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var content = new StringContent($"{{\"isFollowing\":{(follow ? "true" : "false")}}}", Encoding.UTF8, "application/json");
            var resp    = await _http.PostAsync($"{BASE}/calendar/{groupId}/{calendarId}/follow", content);
            Log($"FollowEvent({groupId},{calendarId},{follow}): {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"FollowEvent exception: {ex.Message}"); return false; }
    }

    public async Task<JArray> GetGroupEventsAsync(string groupId, int n = 60)
    {
        if (!IsLoggedIn) return new JArray();
        try
        {
            var resp = await _http.GetAsync($"{BASE}/calendar/{groupId}?n={n}&offset=0");
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                JToken token;
                using (var reader = new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(body)) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })
                    token = JToken.Load(reader);
                if (token is JArray arr) return arr;
                if (token is JObject obj && obj["results"] is JArray results) return results;
                Log($"GetGroupEvents({groupId}): unexpected shape");
                return new JArray();
            }
            Log($"GetGroupEvents({groupId}): {(int)resp.StatusCode}");
        }
        catch (Exception ex) { Log($"GetGroupEvents exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<bool> CreateGroupPostAsync(string groupId, string title, string text, string visibility, bool sendNotification, string? imageId)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var body = new JObject
            {
                ["title"] = title,
                ["text"] = text,
                ["visibility"] = visibility,
                ["sendNotification"] = sendNotification,
            };
            if (!string.IsNullOrEmpty(imageId)) body["imageId"] = imageId;
            var resp = await _http.PostAsync($"{BASE}/groups/{groupId}/posts",
                new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"));
            Log($"CreateGroupPost({groupId}): {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode)
                Log($"CreateGroupPost body: {await resp.Content.ReadAsStringAsync()}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"CreateGroupPost exception: {ex.Message}"); return false; }
    }

    public async Task<bool> DeleteGroupPostAsync(string groupId, string postId)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var resp = await _http.DeleteAsync($"{BASE}/groups/{groupId}/posts/{postId}");
            Log($"DeleteGroupPost({groupId}/{postId}): {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode)
                Log($"DeleteGroupPost body: {await resp.Content.ReadAsStringAsync()}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"DeleteGroupPost exception: {ex.Message}"); return false; }
    }

    public async Task<bool> KickGroupMemberAsync(string groupId, string userId)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var resp = await _http.DeleteAsync($"{BASE}/groups/{groupId}/members/{userId}");
            Log($"KickGroupMember({groupId}/{userId}): {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode) Log($"KickGroupMember body: {await resp.Content.ReadAsStringAsync()}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"KickGroupMember exception: {ex.Message}"); return false; }
    }

    public async Task<bool> BanGroupMemberAsync(string groupId, string userId)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var body = new JObject { ["userId"] = userId };
            var resp = await _http.PostAsync($"{BASE}/groups/{groupId}/bans",
                new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"));
            Log($"BanGroupMember({groupId}/{userId}): {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode) Log($"BanGroupMember body: {await resp.Content.ReadAsStringAsync()}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"BanGroupMember exception: {ex.Message}"); return false; }
    }

    public async Task<JArray> GetGroupBansAsync(string groupId)
    {
        if (!IsLoggedIn) return new JArray();
        try
        {
            var resp = await _http.GetAsync($"{BASE}/groups/{groupId}/bans");
            if (resp.IsSuccessStatusCode) return JArray.Parse(await resp.Content.ReadAsStringAsync());
            Log($"GetGroupBans({groupId}): {(int)resp.StatusCode}");
        }
        catch (Exception ex) { Log($"GetGroupBans exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<bool> UnbanGroupMemberAsync(string groupId, string userId)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var resp = await _http.DeleteAsync($"{BASE}/groups/{groupId}/bans/{userId}");
            Log($"UnbanGroupMember({groupId}/{userId}): {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"UnbanGroupMember exception: {ex.Message}"); return false; }
    }

    public async Task<JObject?> CreateGroupRoleAsync(string groupId, string name, string description, List<string> permissions, bool isAddedOnJoin, bool isSelfAssignable, bool requiresTwoFactor)
    {
        if (!IsLoggedIn) return null;
        try
        {
            // VRChat rejects permissions in the create body — create first, then update with permissions
            var body = new JObject
            {
                ["name"] = name,
                ["description"] = description,
                ["isAddedOnJoin"] = isAddedOnJoin,
                ["isSelfAssignable"] = isSelfAssignable,
                ["requiresTwoFactor"] = requiresTwoFactor,
            };
            var resp = await _http.PostAsync($"{BASE}/groups/{groupId}/roles",
                new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"));
            Log($"CreateGroupRole({groupId}): {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode) { Log($"CreateGroupRole body: {await resp.Content.ReadAsStringAsync()}"); return null; }
            var role = JObject.Parse(await resp.Content.ReadAsStringAsync());
            // If permissions were requested, apply them via update
            if (permissions.Count > 0)
            {
                var roleId = role["id"]?.ToString();
                if (!string.IsNullOrEmpty(roleId))
                    await UpdateGroupRoleAsync(groupId, roleId, null, null, permissions, null, null, null);
            }
            return role;
        }
        catch (Exception ex) { Log($"CreateGroupRole exception: {ex.Message}"); }
        return null;
    }

    public async Task<bool> UpdateGroupRoleAsync(string groupId, string roleId, string? name, string? description, List<string>? permissions, bool? isAddedOnJoin, bool? isSelfAssignable, bool? requiresTwoFactor)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var body = new JObject();
            if (name        != null) body["name"]        = name;
            if (description != null) body["description"] = description;
            if (permissions != null) body["permissions"] = new JArray(permissions);
            if (isAddedOnJoin.HasValue)    body["isAddedOnJoin"]    = isAddedOnJoin.Value;
            if (isSelfAssignable.HasValue) body["isSelfAssignable"] = isSelfAssignable.Value;
            if (requiresTwoFactor.HasValue) body["requiresTwoFactor"] = requiresTwoFactor.Value;
            var reqBody = body.ToString(Newtonsoft.Json.Formatting.None);
            if (permissions != null)
                Log($"UpdateGroupRole perms: [{string.Join(", ", permissions)}]");
            var resp = await _http.PutAsync($"{BASE}/groups/{groupId}/roles/{roleId}",
                new StringContent(reqBody, Encoding.UTF8, "application/json"));
            Log($"UpdateGroupRole({groupId}/{roleId}): {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode) Log($"UpdateGroupRole body: {await resp.Content.ReadAsStringAsync()}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"UpdateGroupRole exception: {ex.Message}"); return false; }
    }

    public async Task<bool> DeleteGroupRoleAsync(string groupId, string roleId)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var resp = await _http.DeleteAsync($"{BASE}/groups/{groupId}/roles/{roleId}");
            Log($"DeleteGroupRole({groupId}/{roleId}): {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"DeleteGroupRole exception: {ex.Message}"); return false; }
    }

    public async Task<bool> AddGroupMemberRoleAsync(string groupId, string userId, string roleId)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var resp = await _http.PutAsync($"{BASE}/groups/{groupId}/members/{userId}/roles/{roleId}",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Log($"AddGroupMemberRole({groupId}/{userId}/{roleId}): {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"AddGroupMemberRole exception: {ex.Message}"); return false; }
    }

    public async Task<bool> RemoveGroupMemberRoleAsync(string groupId, string userId, string roleId)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var resp = await _http.DeleteAsync($"{BASE}/groups/{groupId}/members/{userId}/roles/{roleId}");
            Log($"RemoveGroupMemberRole({groupId}/{userId}/{roleId}): {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"RemoveGroupMemberRole exception: {ex.Message}"); return false; }
    }

    public async Task<bool> UpdateGroupAsync(string groupId, string? description = null, string? rules = null, List<string>? languages = null, List<string>? links = null, string? iconId = null, string? bannerId = null, string? joinState = null)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var body = new JObject();
            if (description != null) body["description"] = description;
            if (rules       != null) body["rules"]       = rules;
            if (languages   != null) body["languages"]   = new JArray(languages);
            if (links       != null) body["links"]       = new JArray(links);
            if (iconId      != null) body["iconId"]      = iconId;
            if (bannerId    != null) body["bannerId"]     = bannerId;
            if (joinState   != null) body["joinState"]   = joinState;
            var resp = await _http.PutAsync($"{BASE}/groups/{groupId}",
                new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"));
            Log($"UpdateGroup({groupId}): {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode)
                Log($"UpdateGroup body: {await resp.Content.ReadAsStringAsync()}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"UpdateGroup exception: {ex.Message}"); return false; }
    }

    public async Task<bool> DeleteGroupEventAsync(string groupId, string eventId)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var resp = await _http.DeleteAsync($"{BASE}/calendar/{groupId}/{eventId}");
            Log($"DeleteGroupEvent({groupId}/{eventId}): {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode)
                Log($"DeleteGroupEvent body: {await resp.Content.ReadAsStringAsync()}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"DeleteGroupEvent exception: {ex.Message}"); return false; }
    }

    /// <summary>Create a group calendar event via POST /calendar/{groupId}/event. Returns the created event JObject or null on failure.</summary>
    public async Task<JObject?> CreateGroupEventAsync(string groupId, string title, string description, string startsAt, string endsAt, string category, string accessType, bool sendCreationNotification, string? imageId)
    {
        if (!IsLoggedIn) return null;
        try
        {
            var body = new JObject
            {
                ["title"]                    = title,
                ["description"]              = description,
                ["startsAt"]                 = startsAt,
                ["endsAt"]                   = endsAt,
                ["category"]                 = category,
                ["accessType"]               = accessType,
                ["sendCreationNotification"] = sendCreationNotification,
                ["isDraft"]                  = false,
            };
            if (!string.IsNullOrEmpty(imageId)) body["imageId"] = imageId;
            var resp = await _http.PostAsync($"{BASE}/calendar/{groupId}/event",
                new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"));
            var respBody = await resp.Content.ReadAsStringAsync();
            var retryAfter = resp.Headers.TryGetValues("Retry-After", out var ra) ? string.Join(",", ra) : null;
            Log($"CreateGroupEvent({groupId}): {(int)resp.StatusCode}{(retryAfter != null ? $" retry-after={retryAfter}s" : "")}");
            if (!resp.IsSuccessStatusCode) { Log($"CreateGroupEvent body: {respBody}"); return null; }
            return JObject.Parse(respBody);
        }
        catch (Exception ex) { Log($"CreateGroupEvent exception: {ex.Message}"); return null; }
    }

    /// <summary>Upload an image to VRChat via POST /file/image multipart. Returns fileId or null on failure.</summary>
    public async Task<string?> UploadImageAsync(byte[] imageBytes, string mimeType = "image/png", string ext = ".png")
    {
        if (!IsLoggedIn) return null;
        try
        {
            using var form = new System.Net.Http.MultipartFormDataContent();
            form.Add(new StringContent("gallery"), "tag");
            var fileContent = new System.Net.Http.ByteArrayContent(imageBytes);
            fileContent.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(mimeType);
            form.Add(fileContent, "file", "image" + ext);

            Log($"UploadImage mimeType={mimeType} size={imageBytes.Length}");
            var resp = await _http.PostAsync($"{BASE}/file/image", form);
            var body = await resp.Content.ReadAsStringAsync();
            Log($"UploadImage response: {(int)resp.StatusCode} preview={body[..Math.Min(200, body.Length)]}");
            if (!resp.IsSuccessStatusCode) return null;

            var fileObj = JObject.Parse(body);
            return fileObj["id"]?.ToString();
        }
        catch (Exception ex) { Log($"UploadImage exception: {ex.Message}"); return null; }
    }

    /// <summary>Create a group instance via POST /instances and return the location string.</summary>
    public async Task<string?> CreateGroupInstanceAsync(string worldId, string groupId, string groupAccessType, string region)
    {
        if (!IsLoggedIn) return null;
        try
        {
            var body = new JObject
            {
                ["worldId"] = worldId,
                ["type"] = "group",
                ["ownerId"] = groupId,
                ["groupAccessType"] = groupAccessType,
                ["region"] = region
            };
            var resp = await _http.PostAsync($"{BASE}/instances",
                new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"));
            var respBody = await resp.Content.ReadAsStringAsync();
            Log($"CreateGroupInstance: {(int)resp.StatusCode} {respBody[..Math.Min(300, respBody.Length)]}");
            if (resp.IsSuccessStatusCode)
            {
                var obj = JObject.Parse(respBody);
                var location = obj["location"]?.ToString();
                if (!string.IsNullOrEmpty(location)) return location;
                // Build location from worldId + instanceId if needed
                var instanceId = obj["instanceId"]?.ToString() ?? obj["id"]?.ToString();
                if (!string.IsNullOrEmpty(instanceId)) return $"{worldId}:{instanceId}";
            }
        }
        catch (Exception ex) { Log($"CreateGroupInstance exception: {ex.Message}"); }
        return null;
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

    private bool _notifV2Supported = true;
    private bool _requestMessageSupported = true;

    /// <summary>Returns the user's VRChat credit balance, or -1 on failure.</summary>
    public async Task<int> GetBalanceAsync()
    {
        if (!IsLoggedIn) return -1;
        try
        {
            var resp = await _http.GetAsync($"{BASE}/user/{CurrentUserId}/balance");
            if (resp.IsSuccessStatusCode)
            {
                var obj = JObject.Parse(await resp.Content.ReadAsStringAsync());
                // SDK field is "VarBalance" serialized as "balance"
                return obj["balance"]?.Value<int>() ?? obj["varBalance"]?.Value<int>() ?? -1;
            }
            Log($"GetBalance failed: {(int)resp.StatusCode} — {await resp.Content.ReadAsStringAsync()}");
        }
        catch (Exception ex) { Log($"GetBalance exception: {ex.Message}"); }
        return -1;
    }

    public async Task<JArray> GetNotificationsV2Async()
    {
        if (!IsLoggedIn || !_notifV2Supported) return new JArray();
        try
        {
            var resp = await _http.GetAsync($"{BASE}/auth/user/notificationsV2?n=100");
            if (resp.IsSuccessStatusCode)
            {
                var result = JArray.Parse(await resp.Content.ReadAsStringAsync());
                Log($"GetNotificationsV2: got {result.Count} notifications");
                return result;
            }
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _notifV2Supported = false;
                Log("GetNotificationsV2: 404 — endpoint not available for this account, disabling v2");
            }
        }
        catch (Exception ex) { Log($"GetNotificationsV2 exception: {ex.Message}"); }
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

    public async Task<bool> HideNotificationAsync(string notifId, bool isV2 = false)
    {
        if (!IsLoggedIn) return false;
        try
        {
            if (isV2)
            {
                // Try v2 delete first
                var r1 = await _http.DeleteAsync($"{BASE}/auth/user/notificationsV2/{notifId}");
                Log($"HideNotificationV2 {notifId}: {(int)r1.StatusCode}");
                if (r1.IsSuccessStatusCode) return true;
                // Fall back to v1 hide (VRChat may accept v1 hide for cross-version IDs)
                Log($"HideNotificationV2 fallback to v1 hide");
            }
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp = await _http.PutAsync($"{BASE}/auth/user/notifications/{notifId}/hide", content);
            Log($"HideNotification(v1) {notifId}: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"HideNotification exception: {ex.Message}"); return false; }
    }

    public async Task<bool> RespondGroupJoinRequestAsync(string groupId, string userId, string action)
    {
        if (!IsLoggedIn) return false;
        try
        {
            var body = new StringContent(JsonConvert.SerializeObject(new { action }), Encoding.UTF8, "application/json");
            var resp = await _http.PutAsync($"{BASE}/groups/{groupId}/requests/{userId}", body);
            Log($"RespondGroupJoinRequest {groupId}/{userId} ({action}): {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log($"RespondGroupJoinRequest exception: {ex.Message}"); return false; }
    }

    /// <summary>Finds the grp_xxx groupId for a group by its shortCode among the current user's groups.</summary>
    public async Task<string?> FindGroupIdByShortCodeAsync(string shortCode)
    {
        var groups = await GetUserGroupsAsync();
        var match = groups.Cast<JObject>()
            .FirstOrDefault(g => string.Equals(g["shortCode"]?.ToString(), shortCode, StringComparison.OrdinalIgnoreCase));
        // membership objects have "groupId" (grp_xxx); "id" is the membership id (gmem_xxx)
        var gid = match?["groupId"]?.ToString();
        if (string.IsNullOrEmpty(gid)) gid = match?["id"]?.ToString();
        return gid?.StartsWith("grp_") == true ? gid : null;
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
    public async Task<(bool ok, JObject? file, string error)> UploadInventoryImageAsync(byte[] bytes, string tag, string animationStyle = "", string maskTag = "")
    {
        if (!IsLoggedIn) return (false, null, "Not logged in");
        try
        {
            using var form = new System.Net.Http.MultipartFormDataContent();
            form.Add(new StringContent(tag), "tag");
            if (!string.IsNullOrEmpty(animationStyle))
                form.Add(new StringContent(animationStyle), "animationStyle");
            if (!string.IsNullOrEmpty(maskTag))
                form.Add(new StringContent(maskTag), "maskTag");
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

    /// <summary>Adds a world to a favorite group. If oldFvrtId is set, removes it first (move between groups).</summary>
    public async Task<(bool ok, string error)> AddWorldFavoriteAsync(string worldId, string groupName, string groupType = "world", string? oldFvrtId = null)
    {
        if (!IsLoggedIn) return (false, "Not logged in");
        try
        {
            // Remove from old group first if moving
            if (!string.IsNullOrEmpty(oldFvrtId))
            {
                await _http.DeleteAsync($"{BASE}/favorites/{oldFvrtId}");
                await Task.Delay(400);
            }
            var json = JsonConvert.SerializeObject(new { type = groupType, favoriteId = worldId, tags = new[] { groupName } });
            var resp = await _http.PostAsync($"{BASE}/favorites",
                new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                var msg = TryGetApiError(body) ?? $"HTTP {(int)resp.StatusCode}";
                Log($"AddWorldFavorite error: {msg}");
                return (false, msg);
            }
            var result = JObject.Parse(body);
            var newFvrtId = result["id"]?.ToString() ?? "";
            return (true, newFvrtId); // ok=true, error field used for new favoriteId
        }
        catch (Exception ex) { Log($"AddWorldFavorite exception: {ex.Message}"); return (false, ex.Message); }
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

    // Helper to extract user image with fallback chain.
    // Priority: userIcon (profile pic) → profilePicOverride (banner) → currentAvatarThumbnailImageUrl (avatar thumbnail)
    public static string GetUserImage(JObject user)
    {
        return user["userIcon"]?.ToString() is string s1 && !string.IsNullOrEmpty(s1) ? s1 :
               user["profilePicOverride"]?.ToString() is string s2 && !string.IsNullOrEmpty(s2) ? s2 :
               user["currentAvatarThumbnailImageUrl"]?.ToString() is string s3 && !string.IsNullOrEmpty(s3) ? s3 : "";
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
