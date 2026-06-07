using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VRCNext.Services.Helpers;

// Manages VRChat's native config.json (cache settings, FOV, etc.) and the
// VRChat asset cache directory. The config.json lives in
// %LOCALAPPDATA%\..\LocalLow\VRChat\VRChat\config.json and is owned by VRChat
// itself, so we read/write it directly — it is not part of VRCNext settings.
public static class VrcConfigHelper
{
    private static string VrcAppDataDir() => VrcPaths.VrcDataDir();

    private static string ConfigPath() => Path.Combine(VrcAppDataDir(), "config.json");

    private static string CacheDir()
    {
        // Honour custom cache_directory in config.json (matches VRCX-0 behaviour).
        try
        {
            var json = File.Exists(ConfigPath()) ? File.ReadAllText(ConfigPath()) : "";
            if (!string.IsNullOrWhiteSpace(json))
            {
                var v = JObject.Parse(json);
                var custom = v["cache_directory"]?.ToString();
                if (!string.IsNullOrWhiteSpace(custom) && Directory.Exists(custom))
                    return Path.Combine(custom, "Cache-WindowsPlayer");
            }
        }
        catch { }
        return Path.Combine(VrcAppDataDir(), "Cache-WindowsPlayer");
    }

    public static (JObject? config, long cacheBytes) ReadConfigAndCacheSize()
    {
        JObject? cfg = null;
        try
        {
            if (File.Exists(ConfigPath()))
            {
                var raw = File.ReadAllText(ConfigPath());
                if (!string.IsNullOrWhiteSpace(raw))
                    cfg = JObject.Parse(raw);
            }
        }
        catch { cfg = null; }
        return (cfg, GetCacheSize());
    }

    public static long GetCacheSize()
    {
        try
        {
            var dir = CacheDir();
            if (!Directory.Exists(dir)) return 0;
            long total = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
            return total;
        }
        catch { return 0; }
    }

    public static bool DeleteAllCache(out string? error)
    {
        error = null;
        try
        {
            var dir = CacheDir();
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
            }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    // Sweep cache: for each asset id directory, keep only the newest version
    // directory (and any locked version). Mirrors VRCX-0's sweep behaviour.
    public static int SweepCache(out string? error)
    {
        error = null;
        int removed = 0;
        try
        {
            var dir = CacheDir();
            if (!Directory.Exists(dir)) return 0;

            foreach (var assetDir in Directory.EnumerateDirectories(dir))
            {
                var versions = Directory.EnumerateDirectories(assetDir).ToList();
                if (versions.Count == 0) continue;

                versions.Sort((a, b) =>
                {
                    DateTime ta, tb;
                    try { ta = new DirectoryInfo(a).LastWriteTimeUtc; } catch { ta = DateTime.MinValue; }
                    try { tb = new DirectoryInfo(b).LastWriteTimeUtc; } catch { tb = DateTime.MinValue; }
                    return ta.CompareTo(tb);
                });

                for (int i = 0; i < versions.Count; i++)
                {
                    var versionDir = versions[i];

                    // Empty version dir → always remove
                    bool isEmpty = !Directory.EnumerateFileSystemEntries(versionDir).Any();
                    if (isEmpty)
                    {
                        try { Directory.Delete(versionDir); } catch { }
                        continue;
                    }

                    // Skip newest (last after sort)
                    if (i == versions.Count - 1) continue;

                    // Skip locked entries
                    if (File.Exists(Path.Combine(versionDir, "__lock"))) continue;

                    try
                    {
                        Directory.Delete(versionDir, true);
                        removed++;
                    }
                    catch { }
                }

                // Remove asset dir if empty
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(assetDir).Any())
                        Directory.Delete(assetDir);
                }
                catch { }
            }
        }
        catch (Exception ex) { error = ex.Message; }
        return removed;
    }

    public static bool WriteConfig(JObject input, out string? error)
    {
        error = null;
        try
        {
            // Mirror VRCX-0's normalize: drop empty / false values, coerce numeric strings.
            // picture_output_split_by_date is removed when true (VRChat treats absence as enabled).
            var output = new JObject();
            foreach (var prop in input.Properties())
            {
                var key = prop.Name;
                var val = prop.Value;

                if (key == "picture_output_split_by_date")
                {
                    if (val.Type == JTokenType.Boolean && val.Value<bool>())
                        continue;
                    output[key] = val;
                    continue;
                }

                if (val.Type == JTokenType.Null) continue;

                if (val.Type == JTokenType.String)
                {
                    var s = val.ToString();
                    if (string.IsNullOrEmpty(s)) continue;
                    if (int.TryParse(s, out var n))
                        output[key] = n;
                    else
                        output[key] = s;
                    continue;
                }

                if (val.Type == JTokenType.Boolean && !val.Value<bool>()) continue;

                output[key] = val;
            }

            var dir = Path.GetDirectoryName(ConfigPath());
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonConvert.SerializeObject(output, Formatting.Indented);
            File.WriteAllText(ConfigPath(), json);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
}
