using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VRCNext;

namespace VRCNext.Services.VRCNPlusAPI;

public class VRCNPlusService : IDisposable
{
    public const string ApiBase = "https://db.vrcnext.com/api/vrcnplus.php";
    private static readonly TimeSpan CacheFreshness = TimeSpan.FromMinutes(15);

    private readonly SqliteConnection _db;
    private readonly HttpClient _http;
    private bool _disposed;

    public VRCNPlusService()
    {
        _db = Database.OpenVRCNPlusConnection();
        InitSchema();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestVersion = System.Net.HttpVersion.Version20;
        _http.DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
    }

    private void InitSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"
            DROP TABLE IF EXISTS profile_themes;
            CREATE TABLE IF NOT EXISTS entitlement_cache (
                user_id     TEXT PRIMARY KEY,
                is_plus     INTEGER NOT NULL,
                checked_at  TEXT NOT NULL
            );";
        cmd.ExecuteNonQuery();
    }

    public async Task<bool> CheckEntitlementAsync(string userId, bool forceRefresh = false)
    {
        if (!forceRefresh)
        {
            using var rd = _db.CreateCommand();
            rd.CommandText = "SELECT is_plus, checked_at FROM entitlement_cache WHERE user_id = $id";
            rd.Parameters.AddWithValue("$id", userId);
            using var r = rd.ExecuteReader();
            if (r.Read())
            {
                var checkedAt = r.GetString(1);
                if (DateTime.TryParse(checkedAt, out var t) &&
                    (DateTime.UtcNow - t.ToUniversalTime()) < TimeSpan.FromMinutes(5))
                {
                    return r.GetInt32(0) == 1;
                }
            }
        }

        var isPlus = false;
        try
        {
            var url  = $"{ApiBase}?action=checkEntitlement&userId={Uri.EscapeDataString(userId)}";
            var resp = await _http.GetAsync(url);
            if (resp.IsSuccessStatusCode)
            {
                var body = JObject.Parse(await resp.Content.ReadAsStringAsync());
                isPlus = body["ok"]?.Value<bool>() == true && body["isPlus"]?.Value<bool>() == true;
            }
        }
        catch { return false; }

        using var up = _db.CreateCommand();
        up.CommandText = @"
            INSERT INTO entitlement_cache (user_id, is_plus, checked_at)
            VALUES ($id, $p, $t)
            ON CONFLICT(user_id) DO UPDATE SET is_plus = excluded.is_plus, checked_at = excluded.checked_at";
        up.Parameters.AddWithValue("$id", userId);
        up.Parameters.AddWithValue("$p", isPlus ? 1 : 0);
        up.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        up.ExecuteNonQuery();
        return isPlus;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _http.Dispose(); } catch { }
        try { _db.Dispose();   } catch { }
    }
}
