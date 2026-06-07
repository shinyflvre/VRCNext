namespace VRCNext.Services.Helpers;

public static class VrcPaths
{
    private const string AppId = "438100";

    public static string VrcDataDir()
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.GetFullPath(Path.Combine(local, "..", "LocalLow", "VRChat", "VRChat"));
        }
        return LinuxVrcDataDir();
    }

    private static string LinuxVrcDataDir()
    {
        static string Rel(string prefix) => Path.Combine(prefix, "pfx", "drive_c", "users",
            "steamuser", "AppData", "LocalLow", "VRChat", "VRChat");

        string? fallback = null;
        foreach (var prefix in CompatPrefixes())
        {
            var dir = Rel(prefix);
            fallback ??= dir;
            if (Directory.Exists(dir)) return dir;
        }
        if (fallback != null) return fallback;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Rel(Path.Combine(home, ".local", "share", "Steam", "steamapps", "compatdata", AppId));
    }

    private static IEnumerable<string> CompatPrefixes()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".steam", "root"),
        };

        var seen = new HashSet<string>();
        foreach (var root in roots)
        {
            var compat = Path.Combine(root, "steamapps", "compatdata", AppId);
            if (seen.Add(compat)) yield return compat;

            foreach (var lib in VdfLibraries(Path.Combine(root, "steamapps", "libraryfolders.vdf")))
            {
                var c = Path.Combine(lib, "steamapps", "compatdata", AppId);
                if (seen.Add(c)) yield return c;
            }
        }
    }

    private static List<string> VdfLibraries(string vdfPath)
    {
        var result = new List<string>();
        try
        {
            if (!File.Exists(vdfPath)) return result;
            var vdf = File.ReadAllText(vdfPath);
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\""))
            {
                result.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            }
        }
        catch { }
        return result;
    }
}
