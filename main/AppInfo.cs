namespace VRCNext;

public static class AppInfo
{
    public const string Version = "2026.62.5";
    public const string ContactEmail = "vrcn@shinyflvres.com";
    public const string Website = "vrcn.shinyflvres.com";
    
    private static string? _customUserAgent;
    private static string? _cachedUserAgent;
    
    /// <summary>
    /// Sets a custom user-agent string for this fork/modification of VRCNext.
    /// Format: "YourName-VRCNext/Version (your@email.com)"
    /// </summary>
    public static void SetCustomUserAgent(string customUserAgent)
    {
        _customUserAgent = customUserAgent;
        _cachedUserAgent = null; // Clear cache when changed
    }
    
    /// <summary>
    /// Gets the user-agent string. Returns custom user-agent if set, otherwise the default.
    /// </summary>
    public static string UserAgent
    {
        get
        {
            if (_cachedUserAgent != null)
                return _cachedUserAgent;
            
            if (!string.IsNullOrEmpty(_customUserAgent))
            {
                _cachedUserAgent = _customUserAgent;
            }
            else
            {
                _cachedUserAgent = $"Nano-VRCNext/{Version} ({ContactEmail})";
            }
            
            return _cachedUserAgent;
        }
    }

    public static string SelfExecutable
    {
        get
        {
            var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
            if (!string.IsNullOrEmpty(appImage) && File.Exists(appImage)) return appImage;
            return Environment.ProcessPath ?? "";
        }
    }
}
