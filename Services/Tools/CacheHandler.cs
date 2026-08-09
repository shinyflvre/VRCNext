using Newtonsoft.Json;

namespace VRCNext.Services;

public class CacheHandler
{
    private static readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCNext");

    // Per-account subdirectory inserted into cache paths for secondary accounts.
    // Primary uses the default layout under "Caches/..." so existing files keep working.
    private static string _accountSubdir = "";

    public static readonly string KeyFavWorlds      = "Caches/fav_worlds_cache.json";
    public static readonly string KeyFavFriends     = "Caches/fav_friends_cache.json";
    public static readonly string KeyFavAvatars     = "Caches/fav_avatars_cache.json";
    public static readonly string KeyAvatars        = "Caches/avatars_cache.json";
    public static readonly string KeyGroups         = "Caches/groups_cache.json";
    public static readonly string KeyFriends        = "Caches/friends_cache.json";
    public static readonly string KeyMutuals        = "Caches/mutual_cache.json";
    public static readonly string KeyInventory       = "Caches/inventory_cache.json";
    public static readonly string KeyCustomColors   = "custom_colors.json";
    public static readonly string KeyPermini        = "permini_list.json";
    public static readonly string KeySharedContent    = "Caches/shared_content_cache.json";
    public static readonly string KeyRecentWorlds     = "Caches/dashboard_recently_visited.json";
    public static readonly string KeyWorldMeta        = "Caches/world_meta_cache.json";

    public static string KeyUserProfile(string userId)    => $"profiles/{userId}.json";
    public static string KeyUserFavWorlds(string userId)  => $"favworlds/{userId}.json"; // legacy — kept for FFC profile caching
    public static string KeyUserGroups(string userId)        => $"Caches/Profiles/{userId}/user_groups_cache.json";
    public static string KeyUserContent(string userId)       => $"Caches/Profiles/{userId}/user_content_cache.json";
    public static string KeyUserFavContent(string userId)    => $"Caches/Profiles/{userId}/user_fav_content_cache.json";
    public static string KeyUserMutualGroups(string userId)  => $"Caches/Profiles/{userId}/mutual_groups_cache.json";
    public static string KeyUserMutuals(string userId)       => $"Caches/Profiles/{userId}/user_mutuals_cache.json";

    // Keys that are universal and stay shared across all accounts (app-wide theme, world metadata).
    private static readonly HashSet<string> _sharedKeys = new()
    {
        "custom_colors.json",
        "Caches/world_meta_cache.json",
    };

    // Routes per-account keys through Accounts/{userId}/Caches/... for secondary accounts.
    public static void SetActiveAccount(VrcAccount? account)
    {
        if (account == null || account.IsPrimary || string.IsNullOrEmpty(account.UserId))
        {
            _accountSubdir = "";
        }
        else
        {
            _accountSubdir = Path.Combine("Accounts", account.UserId);
        }
    }

    private static string Resolve(string key)
    {
        if (string.IsNullOrEmpty(_accountSubdir) || _sharedKeys.Contains(key))
            return Path.Combine(_dir, key);
        return Path.Combine(_dir, _accountSubdir, key);
    }

    public object? LoadRaw(string key)
    {
        var path = Resolve(key);
        if (!File.Exists(path)) return null;
        try   { return JsonConvert.DeserializeObject(File.ReadAllText(path)); }
        catch { return null; }
    }

    public void Save(string key, object data)
    {
        try
        {
            var path = Resolve(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonConvert.SerializeObject(data));
        }
        catch (Exception ex) { CrashHandler.WriteEntry("CacheHandler.Save", ex); }
    }

    public bool Has(string key) => File.Exists(Resolve(key));

    /// <summary>Returns true if the cache file exists and was written less than <paramref name="ttl"/> ago.</summary>
    public bool IsFresh(string key, TimeSpan ttl)
    {
        var path = Resolve(key);
        if (!File.Exists(path)) return false;
        return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < ttl;
    }

    public void Delete(string key)
    {
        try { File.Delete(Resolve(key)); }
        catch { }
    }

    // Clears caches for the currently active account only.
    public void ClearAll()
    {
        try
        {
            Delete(KeyFavWorlds);
            Delete(KeyFavFriends);
            Delete(KeyFavAvatars);
            Delete(KeyAvatars);
            Delete(KeyGroups);
            Delete(KeyFriends);
            Delete(KeyInventory);
            // Sub-folders are per-account, resolve under the current account root.
            var root = string.IsNullOrEmpty(_accountSubdir) ? _dir : Path.Combine(_dir, _accountSubdir);
            var profilesDir = Path.Combine(root, "profiles");
            if (Directory.Exists(profilesDir)) Directory.Delete(profilesDir, true);
            var favWorldsDir = Path.Combine(root, "favworlds");
            if (Directory.Exists(favWorldsDir)) Directory.Delete(favWorldsDir, true);
            var profilesCacheDir = Path.Combine(root, "Caches", "Profiles");
            if (Directory.Exists(profilesCacheDir)) Directory.Delete(profilesCacheDir, true);
        }
        catch (Exception ex) { CrashHandler.WriteEntry("CacheHandler.ClearAll", ex); }
    }
}
