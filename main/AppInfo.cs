namespace VRCNext;

public static class AppInfo
{
    public const string Version = "2026.62.0";
    public const string ContactEmail = "hollynn@kittymail.com";
    public const string Website = "vrcn.shinyflvres.com";
    public const string UserAgent = $"Nano-VRCNext/{Version} ({ContactEmail})";

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
