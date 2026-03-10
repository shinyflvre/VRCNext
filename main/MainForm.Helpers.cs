using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VRCNext;

public partial class MainForm
{
    // Photino compatibility shim: SendWebMessage is thread-safe, so Invoke is a direct call
    private static void Invoke(Action action) => action();
    private static T Invoke<T>(Func<T> func) => func();


    private static string ParseInstanceTypeFromLoc(string loc)
    {
        if (loc.Contains("~private(")) return loc.Contains("~canRequestInvite") ? "invite_plus" : "private";
        if (loc.Contains("~friends(")) return "friends";
        if (loc.Contains("~hidden("))  return "hidden";
        if (loc.Contains("~group("))   return "group";
        return "public";
    }

    private static string ParseRegionFromLoc(string loc)
    {
        var m = System.Text.RegularExpressions.Regex.Match(loc, @"~region\(([^)]+)\)");
        return m.Success ? m.Groups[1].Value : "eu";
    }

    // Library file cache entry
    private record LibFileEntry(FileInfo Fi, int FolderIndex, string Folder);

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
