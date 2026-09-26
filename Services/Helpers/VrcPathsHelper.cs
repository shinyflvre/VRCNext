namespace VRCNext.Services.Helpers;

public static class VrcPathsHelper
{
    private const string VrcSteamAppId = "438100";

    public static string AppDataDir()
    {
#if WINDOWS
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.GetFullPath(Path.Combine(local, "..", "LocalLow", "VRChat", "VRChat"));
#else
        var pfx = FindProtonPrefix();
        if (pfx != null)
            return Path.Combine(pfx, "drive_c", "users", "steamuser", "AppData", "LocalLow", "VRChat", "VRChat");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.GetFullPath(Path.Combine(local, "..", "LocalLow", "VRChat", "VRChat"));
#endif
    }

    public static string TempDir()
    {
#if WINDOWS
        return Path.Combine(Path.GetTempPath(), "VRChat", "VRChat");
#else
        var pfx = FindProtonPrefix();
        if (pfx != null)
            return Path.Combine(pfx, "drive_c", "users", "steamuser", "AppData", "Local", "Temp", "VRChat", "VRChat");
        return Path.Combine(Path.GetTempPath(), "VRChat", "VRChat");
#endif
    }

    public static string CrashDumpDir() => Path.Combine(TempDir(), "Crashes");

    private static string? _cachedPhotoDir;
    private static DateTime _cachedPhotoDirAt = DateTime.MinValue;

    public static string PhotoDir()
    {
        if (_cachedPhotoDir != null && (DateTime.UtcNow - _cachedPhotoDirAt).TotalMinutes < 5)
            return _cachedPhotoDir;
#if WINDOWS
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VRChat");
#else
        var pfx = FindProtonPrefix();
        string dir = "";
        if (pfx != null)
        {
            var p1 = Path.Combine(pfx, "drive_c", "users", "steamuser", "My Pictures", "VRChat");
            var p2 = Path.Combine(pfx, "drive_c", "users", "steamuser", "Pictures", "VRChat");
            if (Directory.Exists(p1)) dir = p1;
            else if (Directory.Exists(p2)) dir = p2;
        }
        if (dir.Length == 0)
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures", "VRChat");
#endif
        _cachedPhotoDir = dir;
        _cachedPhotoDirAt = DateTime.UtcNow;
        return dir;
    }

    private static readonly System.Text.RegularExpressions.Regex VrcPhotoNameRx = new(
        @"^VRChat_(?:\d+x\d+_)?(\d{4})-(\d{2})-(\d{2})_(\d{2})-(\d{2})-(\d{2})\.\d{3}(?:_\d+x\d+)?\.(?:png|jpe?g|webp)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool TryParseVrcPhotoTime(string fileName, out DateTime local)
    {
        local = default;
        var m = VrcPhotoNameRx.Match(fileName);
        if (!m.Success) return false;
        try
        {
            local = new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), int.Parse(m.Groups[6].Value), DateTimeKind.Local);
            return true;
        }
        catch { return false; }
    }

    public static string TranslateGamePath(string path)
    {
#if WINDOWS
        return path;
#else
        if (string.IsNullOrWhiteSpace(path)) return path;
        if (path.Length < 3 || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))
            return path;
        var drive = char.ToLowerInvariant(path[0]);
        var rest = path[3..].Replace('\\', '/');
        if (drive == 'z') return "/" + rest;
        var pfx = FindProtonPrefix();
        if (pfx == null) return path;
        if (drive == 'c') return Path.Combine(pfx, "drive_c", rest);
        return Path.Combine(pfx, "dosdevices", $"{drive}:", rest);
#endif
    }

#if !WINDOWS
    private static string? _cachedPrefix;
    private static DateTime _cachedPrefixAt = DateTime.MinValue;

    public static string? FindProtonPrefix()
    {
        if (_cachedPrefix != null && (DateTime.UtcNow - _cachedPrefixAt).TotalMinutes < 5)
            return _cachedPrefix;
        foreach (var lib in SteamLibraries())
        {
            var pfx = Path.Combine(lib, "steamapps", "compatdata", VrcSteamAppId, "pfx");
            if (Directory.Exists(pfx))
            {
                _cachedPrefix = pfx;
                _cachedPrefixAt = DateTime.UtcNow;
                return pfx;
            }
        }
        _cachedPrefix = null;
        _cachedPrefixAt = DateTime.UtcNow;
        return null;
    }

    public static IEnumerable<string> SteamLibraries()
    {
        var seen = new HashSet<string>();
        foreach (var root in SteamRoots())
        {
            if (!Directory.Exists(root) || !seen.Add(root)) continue;
            yield return root;
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            string text;
            try { text = File.ReadAllText(vdf); }
            catch { continue; }
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
            {
                var lib = m.Groups[1].Value.Replace("\\\\", "\\");
                if (Directory.Exists(lib) && seen.Add(lib))
                    yield return lib;
            }
        }
    }

    private static IEnumerable<string> SteamRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".local", "share", "Steam");
        yield return Path.Combine(home, ".steam", "steam");
        yield return Path.Combine(home, ".steam", "root");
        yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdg))
            yield return Path.Combine(xdg, "Steam");
    }
#endif
}
