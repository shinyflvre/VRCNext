namespace VRCNext;

public static class AppInfo
{
    public const string Version = "2026.41.5";
    public const string ContactEmail = "vrcn@shinyflvres.com";
    public const string Website = "vrcn.shinyflvres.com";
    public const string UserAgent = $"VRCNext/{Version} ({ContactEmail})";

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
