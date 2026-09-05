using Newtonsoft.Json.Linq;
using VRCNext.Services.Helpers;

namespace VRCNext.Services.Tools;

public class InstanceStickerSaver
{
    private const int MaxRecentIds = 100;
    private static readonly TimeSpan SaveInterval = TimeSpan.FromMilliseconds(1500);

    private readonly CoreLibrary _core;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<string> _recent = new();
    private readonly object _lock = new();

    public InstanceStickerSaver(CoreLibrary core) => _core = core;

    public string ResolveFolder()
    {
        var custom = (_core.Settings.InstanceStickersPath ?? "").Trim();
        if (custom.Length > 0) return VrcPathsHelper.TranslateGamePath(custom);
        return Path.Combine(VrcPathsHelper.PhotoDir(), "Stickers");
    }

    public void OnStickerSeen(string userId, string displayName, string inventoryId)
    {
        if (!_core.Settings.SaveInstanceStickers) return;
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(inventoryId)) return;
        if (AlreadySeen(inventoryId)) return;
        _ = Task.Run(() => SaveAsync(userId, displayName, inventoryId));
    }

    private bool AlreadySeen(string inventoryId)
    {
        lock (_lock)
        {
            if (_recent.Contains(inventoryId)) return true;
            _recent.Enqueue(inventoryId);
            while (_recent.Count > MaxRecentIds) _recent.Dequeue();
            return false;
        }
    }

    private async Task SaveAsync(string userId, string displayName, string inventoryId)
    {
        await _gate.WaitAsync();
        try
        {
            await Task.Delay(SaveInterval);
            if (!_core.Settings.SaveInstanceStickers) return;
            if (!_core.VrcApi.IsLoggedIn) return;

            var resp = await _core.VrcApi._http.GetAsync($"{VRChatApiService.BASE}/user/{userId}/inventory/{inventoryId}");
            if (!resp.IsSuccessStatusCode) return;
            var item = JObject.Parse(await resp.Content.ReadAsStringAsync());

            var itemType = item["itemType"]?.ToString() ?? "";
            if (!string.Equals(itemType, "sticker", StringComparison.OrdinalIgnoreCase)) return;
            var flags = item["flags"] as JArray;
            if (flags != null && !flags.Any(f => string.Equals(f?.ToString(), "ugc", StringComparison.OrdinalIgnoreCase))) return;

            var imageUrl = item["metadata"]?["imageUrl"]?.ToString() ?? item["imageUrl"]?.ToString() ?? "";
            if (imageUrl.Length == 0) return;

            var createdRaw = item["created_at"]?.ToString() ?? "";
            var created = DateTime.TryParse(createdRaw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt.ToLocalTime() : DateTime.Now;

            var author = Sanitize(string.IsNullOrWhiteSpace(displayName) ? (item["holderId"]?.ToString() ?? userId) : displayName);
            var folder = Path.Combine(ResolveFolder(), created.ToString("yyyy-MM"));
            Directory.CreateDirectory(folder);

            var fileName = $"{author}_{created:yyyy-MM-dd_HH-mm-ss}_{inventoryId}.png";
            var target = Path.Combine(folder, fileName);
            if (File.Exists(target)) return;

            var imgResp = await _core.VrcApi._http.GetAsync(imageUrl);
            if (!imgResp.IsSuccessStatusCode) return;
            var bytes = await imgResp.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0) return;
            await File.WriteAllBytesAsync(target, bytes);

            _core.SendToJS("log", new { msg = $"Sticker saved: {fileName}", color = "ok" });
        }
        catch (Exception ex)
        {
            _core.SendToJS("log", new { msg = $"Sticker save failed ({inventoryId}): {ex.Message}", color = "err" });
        }
        finally { _gate.Release(); }
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim().Length == 0 ? "Unknown" : name.Trim();
    }
}
