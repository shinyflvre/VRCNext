using System.Globalization;
using System.Text;
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

 // legacy — kept for FFC profile caching
    public static string KeyUserFavContent(string userId)    => $"Caches/Profiles/{userId}/user_fav_content_cache.json";
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
        try
        {
            var chunks = ReadFileChunks(path);
            var serializer = JsonSerializer.CreateDefault();
            serializer.CheckAdditionalContent = true;
            using var reader = new JsonTextReader(new StreamReader(new ChunkReadStream(chunks), Encoding.UTF8, true));
            return serializer.Deserialize(reader);
        }
        catch { return null; }
    }

    public void Save(string key, object data)
    {
        try
        {
            var path = Resolve(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var serializer = JsonSerializer.CreateDefault();
            var sb = new StringBuilder(256);
            using (var sw = new StringWriter(sb, CultureInfo.InvariantCulture))
            using (var jw = new JsonTextWriter(sw))
            {
                jw.Formatting = serializer.Formatting;
                serializer.Serialize(jw, data, null);
            }
            using var fw = new StreamWriter(path, false);
            foreach (var chunk in sb.GetChunks()) fw.Write(chunk.Span);
        }
        catch (Exception ex) { CrashHandler.WriteEntry("CacheHandler.Save", ex); }
    }

    private const int FileChunkSize = 64 * 1024;

    private static List<(byte[] Buf, int Len)> ReadFileChunks(string path)
    {
        var chunks = new List<(byte[] Buf, int Len)>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        while (true)
        {
            var buf = new byte[FileChunkSize];
            int len = 0;
            while (len < buf.Length)
            {
                int n = fs.Read(buf, len, buf.Length - len);
                if (n == 0) break;
                len += n;
            }
            if (len > 0) chunks.Add((buf, len));
            if (len < buf.Length) break;
        }
        return chunks;
    }

    private sealed class ChunkReadStream : Stream
    {
        private readonly List<(byte[] Buf, int Len)> _chunks;
        private int _index;
        private int _offset;
        public ChunkReadStream(List<(byte[] Buf, int Len)> chunks) { _chunks = chunks; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            while (_index < _chunks.Count)
            {
                var (buf, len) = _chunks[_index];
                if (_offset < len)
                {
                    int n = Math.Min(buffer.Length, len - _offset);
                    buf.AsSpan(_offset, n).CopyTo(buffer);
                    _offset += n;
                    return n;
                }
                _index++;
                _offset = 0;
            }
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
