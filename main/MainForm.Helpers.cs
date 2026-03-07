using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VRCNext;

public partial class MainForm
{
    private const int UserDetailCacheMax = 200;
    private void CacheUserDetail(string userId, object payload)
    {
        _userDetailCache[userId] = (payload, DateTime.UtcNow);
        if (_userDetailCache.Count > UserDetailCacheMax)
        {
            var oldest = _userDetailCache.MinBy(kv => kv.Value.cachedAt).Key;
            _userDetailCache.Remove(oldest);
        }
    }

    // Library file cache entry
    private record LibFileEntry(FileInfo Fi, string Host, string Folder);

    // Helper: always returns ISO 8601 date string from a JToken
    private static string IsoDate(JToken? t)
    {
        if (t == null) return "";
        if (t.Type == JTokenType.Date)
            return t.Value<DateTime>().ToUniversalTime().ToString("o");
        return t.ToString();
    }

    // ── VRCN Chat Storage ─────────────────────────────────────────────────────
    private static readonly string _chatDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCNext", "chat");

    private record ChatEntry(string id, string from, string text, string time, string? type = null);

    private static string ChatFile(string userId) =>
        Path.Combine(_chatDir, $"chat_{userId}.json");

    private List<ChatEntry> GetChatHistory(string userId)
    {
        try
        {
            var file = ChatFile(userId);
            if (!File.Exists(file)) return [];
            var json = File.ReadAllText(file);
            return JsonConvert.DeserializeObject<List<ChatEntry>>(json) ?? [];
        }
        catch { return []; }
    }

    private ChatEntry StoreChatMessage(string userId, string from, string text, string? type = null)
    {
        var entry = new ChatEntry(Guid.NewGuid().ToString(), from, text, DateTime.UtcNow.ToString("o"), type);
        try
        {
            Directory.CreateDirectory(_chatDir);
            var history = GetChatHistory(userId);
            history.Add(entry);
            // Keep last 500 messages per conversation
            if (history.Count > 500) history = history[^500..];
            File.WriteAllText(ChatFile(userId), JsonConvert.SerializeObject(history));
        }
        catch { }
        return entry;
    }
}
