using System.Net;
using Newtonsoft.Json.Linq;
using VRCNext.Services.Helpers;

namespace VRCNext.Services;

public class VRChatApiService
{
    // Recreated by ResetSession so these are no longer readonly.
    internal HttpClient _http = null!;
    internal CookieContainer _cookies = new();
    internal const string BASE = "https://api.vrchat.cloud/api/1";
    private const string UA = AppInfo.UserAgent;

    public bool IsLoggedIn { get; internal set; }
    public JObject? CurrentUserRaw { get; internal set; }
    public string? CurrentUserId => CurrentUserRaw?["id"]?.ToString();
    public string? CurrentAvatarId => CurrentUserRaw?["currentAvatar"]?.ToString();
    public bool HasVrcPlus => CurrentUserRaw?["tags"] is JArray tags && tags.Any(t => t.ToString() == "system_supporter");

    public event Action<string>? DebugLog;
    internal void Log(string msg) => DebugLog?.Invoke(msg);

    public bool NotifV2Supported { get => _notifV2Supported; set => _notifV2Supported = value; }
    internal bool _notifV2Supported = true;
    internal bool _requestMessageSupported = true;

    public class LoginResult
    {
        public bool Success { get; set; }
        public bool Requires2FA { get; set; }
        public bool NetworkError { get; set; }
        public string TwoFactorType { get; set; } = "";
        public string? Error { get; set; }
        public JObject? User { get; set; }
    }

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
        BuildHttpClient();
    }

    private void BuildHttpClient()
    {
        _cookies = new CookieContainer();
        var inner = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
        };
        _http = new HttpClient(new BackoffHandler(new LoggingHandler(inner, Log), Log));
        _http.DefaultRequestVersion = HttpVersion.Version20;
        _http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UA);
    }

    // Fully resets the API session by recreating CookieContainer and HttpClient so no prior state leaks.
    public void ResetSession()
    {
        try { _http?.Dispose(); } catch { }
        IsLoggedIn = false;
        CurrentUserRaw = null;
        BuildHttpClient();
    }

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

    public HttpClient GetHttpClient() => _http;

    internal static string? TryGetApiError(string body)
    {
        try
        {
            var j = JObject.Parse(body);
            return j["error"]?["message"]?.ToString() ?? j["message"]?.ToString();
        }
        catch { return null; }
    }

    public static string BuildLaunchUri(string location) =>
        $"vrchat://launch?ref=vrchat.com&id={location}";

    public static string GetUserImage(JObject user)
    {
        var url = user["iconUrl"]?.ToString() is string s0 && !string.IsNullOrEmpty(s0) ? s0 :
                  user["userIcon"]?.ToString() is string s1 && !string.IsNullOrEmpty(s1) ? s1 :
                  user["profilePicOverride"]?.ToString() is string s2 && !string.IsNullOrEmpty(s2) ? s2 :
                  user["currentAvatarImageUrl"]?.ToString() is string s3 && !string.IsNullOrEmpty(s3) ? s3 :
                  user["currentAvatarThumbnailImageUrl"]?.ToString() is string s4 && !string.IsNullOrEmpty(s4) ? s4 : "";
        return UpgradeImageResolution(url);
    }

    public static string UpgradeImageResolution(string url)
    {
        if (url.Contains("/api/1/image/"))
        {
            if (url.EndsWith("/256")) return url[..^3] + "800";
            if (url.EndsWith("/512")) return url[..^3] + "800";
        }
        return url;
    }

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

            if (instanceId.Contains("~private(")) instanceType = instanceId.Contains("~canRequestInvite") ? "invite_plus" : "private";
            else if (instanceId.Contains("~friends+(")) instanceType = "friends+";
            else if (instanceId.Contains("~friends(")) instanceType = "friends";
            else if (instanceId.Contains("~hidden(")) instanceType = "hidden";
            else if (instanceId.Contains("~group("))
            {
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
