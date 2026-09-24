using System.Text;
using Newtonsoft.Json;

namespace VRCNext.Services;

public static class PopularityReporter
{
    private const string HitEndpoint  = "https://db.vrcnext.com/api/hit.php";
    private const string LikeEndpoint = "https://db.vrcnext.com/api/like.php";
    private static readonly HttpClient _client = new(new VrcndbSigningHandler()) { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly string _syncFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "vrcndb_synced_likes.txt");
    private static readonly object _syncLock = new();
    private static HashSet<string>? _synced;

    public static void Report(string avatarId, string source = "client", string? kind = null)
    {
        if (string.IsNullOrWhiteSpace(avatarId) || !avatarId.StartsWith("avtr_")) return;
        _ = Task.Run(async () =>
        {
            try
            {
                object payloadObj = string.IsNullOrEmpty(kind)
                    ? new { ids = new[] { avatarId }, source }
                    : new { ids = new[] { avatarId }, source, kind };
                var payload = JsonConvert.SerializeObject(payloadObj);
                using var req = new HttpRequestMessage(HttpMethod.Post, HitEndpoint)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                req.Headers.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent);
                req.Headers.TryAddWithoutValidation("Referer", $"https://{AppInfo.Website}");
                await _client.SendAsync(req);
            }
            catch { }
        });
    }

    private static HashSet<string> Synced()
    {
        if (_synced != null) return _synced;
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(_syncFile))
                foreach (var line in File.ReadAllLines(_syncFile))
                {
                    var s = line.Trim();
                    if (s.Length > 0) set.Add(s);
                }
        }
        catch { }
        _synced = set;
        return set;
    }

    // Marks favorited avatars as a "like" on VRCNDb (per IP). Deduplicates against a
    // persisted set so only newly favorited avatars are sent. Unknown avatars are
    // submitted server-side automatically.
    public static void SyncFavoriteLikes(IEnumerable<string> favoriteIds)
    {
        var toSend = new List<string>();
        lock (_syncLock)
        {
            var set = Synced();
            foreach (var id in favoriteIds)
            {
                if (string.IsNullOrWhiteSpace(id) || !id.StartsWith("avtr_")) continue;
                if (!set.Contains(id) && !toSend.Contains(id)) toSend.Add(id);
            }
        }
        if (toSend.Count == 0) return;
        _ = SendLikes(toSend);
    }

    private static async Task SendLikes(List<string> ids)
    {
        var sent = new List<string>();
        for (int i = 0; i < ids.Count; i += 100)
        {
            var batch = ids.GetRange(i, Math.Min(100, ids.Count - i));
            try
            {
                var payload = JsonConvert.SerializeObject(new { ids = batch, action = "like" });
                using var req = new HttpRequestMessage(HttpMethod.Post, LikeEndpoint)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                req.Headers.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent);
                req.Headers.TryAddWithoutValidation("Referer", $"https://{AppInfo.Website}");
                var resp = await _client.SendAsync(req);
                if (resp.IsSuccessStatusCode) sent.AddRange(batch);
            }
            catch { }
            if (i + 100 < ids.Count) await Task.Delay(300);
        }
        if (sent.Count > 0)
        {
            lock (_syncLock)
            {
                var set = Synced();
                foreach (var id in sent) set.Add(id);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_syncFile)!);
                    File.AppendAllLines(_syncFile, sent);
                }
                catch { }
            }
        }
    }
}
