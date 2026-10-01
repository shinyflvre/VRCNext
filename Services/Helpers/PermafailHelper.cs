using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace VRCNext.Services.Helpers;

// Permafail types
public static class PfType
{
    public const string Image  = "Image";
    public const string Entity = "Entity";
}

public static class PermafailHelper
{
    private static string _dbPath = "";
    private static SqliteConnection? _db;
    private static readonly object _lock = new();

    // TTL per fail count: 1→30d, 2→60d, 3+→infinite
    private static readonly TimeSpan[] _ttls =
    [
        TimeSpan.FromDays(30),
        TimeSpan.FromDays(60),
    ];

    private record Entry(int StatusCode, DateTime AddedAt, int FailCount);
    private static readonly ConcurrentDictionary<(string key, string type), Entry> _cache = new();

    public static void Initialize()
    {
        _dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VRCNext", "Caches", "Permafail", "Permafail.db");

        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);

        _db = Database.Track(new SqliteConnection($"Data Source={_dbPath}"));
        _db.Open();

        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"
            PRAGMA cache_size=-1024;
            CREATE TABLE IF NOT EXISTS permafail (
                key         TEXT NOT NULL,
                type        TEXT NOT NULL,
                status_code INTEGER NOT NULL DEFAULT 0,
                added_at    TEXT NOT NULL,
                fail_count  INTEGER NOT NULL DEFAULT 1,
                PRIMARY KEY (key, type)
            );
            CREATE INDEX IF NOT EXISTS idx_pf_type ON permafail(type);";
        cmd.ExecuteNonQuery();

        _Migrate();
        _LoadIntoMemory();
        _PurgeExpired();
    }

    public static bool IsPermafailed(string key, string type)
    {
        if (!_cache.TryGetValue((key, type), out var e)) return false;
        if (IsInfinite(e.FailCount)) return true;
        var ttl = _ttls[Math.Min(e.FailCount - 1, _ttls.Length - 1)];
        if (DateTime.UtcNow - e.AddedAt <= ttl) return true;
        // TTL expired — remove from memory so next fail increments counter
        _cache.TryRemove((key, type), out _);
        return false;
    }

    public static void Add(string key, string type, int statusCode)
    {
        // If already in cache (shouldn't happen since IsPermafailed removes expired),
        // increment counter. Otherwise start at 1.
        _cache.TryGetValue((key, type), out var existing);
        var count = (existing?.FailCount ?? 0) + 1;
        var entry = new Entry(statusCode, DateTime.UtcNow, count);
        _cache[(key, type)] = entry;
        _ = Task.Run(() => _Upsert(key, type, statusCode, entry.AddedAt, count));
    }

    public static void Remove(string key, string type)
    {
        _cache.TryRemove((key, type), out _);
        _ = Task.Run(() =>
        {
            lock (_lock)
            {
                if (_db == null) return;
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "DELETE FROM permafail WHERE key = $key AND type = $type";
                cmd.Parameters.AddWithValue("$key", key);
                cmd.Parameters.AddWithValue("$type", type);
                cmd.ExecuteNonQuery();
            }
        });
    }

    private static bool IsInfinite(int failCount) => failCount >= 3;

    private static void _Migrate()
    {
        // Add fail_count column if it doesn't exist (upgrade from older schema)
        try
        {
            lock (_lock)
            {
                using var cmd = _db!.CreateCommand();
                cmd.CommandText = "ALTER TABLE permafail ADD COLUMN fail_count INTEGER NOT NULL DEFAULT 1";
                cmd.ExecuteNonQuery();
            }
        }
        catch { } // column already exists
    }

    private static void _LoadIntoMemory()
    {
        lock (_lock)
        {
            using var cmd = _db!.CreateCommand();
            cmd.CommandText = "SELECT key, type, status_code, added_at, fail_count FROM permafail";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key       = r.GetString(0);
                var type      = r.GetString(1);
                var sc        = r.GetInt32(2);
                var count     = r.GetInt32(4);
                if (!DateTime.TryParse(r.GetString(3), out var dt)) continue;
                // Keep infinite entries always; keep others only if not expired
                if (!IsInfinite(count))
                {
                    var ttl = _ttls[Math.Min(count - 1, _ttls.Length - 1)];
                    if (DateTime.UtcNow - dt > ttl) continue;
                }
                _cache[(key, type)] = new Entry(sc, dt, count);
            }
        }
    }

    private static void _PurgeExpired()
    {
        _ = Task.Run(() =>
        {
            lock (_lock)
            {
                if (_db == null) return;
                // Purge count=1 older than 30d and count=2 older than 60d; leave count>=3 forever
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"
                    DELETE FROM permafail WHERE
                        (fail_count = 1 AND added_at < $c30) OR
                        (fail_count = 2 AND added_at < $c60)";
                cmd.Parameters.AddWithValue("$c30", (DateTime.UtcNow - TimeSpan.FromDays(30)).ToString("o"));
                cmd.Parameters.AddWithValue("$c60", (DateTime.UtcNow - TimeSpan.FromDays(60)).ToString("o"));
                cmd.ExecuteNonQuery();
            }
        });
    }

    private static void _Upsert(string key, string type, int statusCode, DateTime addedAt, int failCount)
    {
        lock (_lock)
        {
            if (_db == null) return;
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO permafail (key, type, status_code, added_at, fail_count)
                VALUES ($key, $type, $sc, $at, $fc)
                ON CONFLICT(key, type) DO UPDATE SET
                    status_code = $sc,
                    added_at    = $at,
                    fail_count  = $fc";
            cmd.Parameters.AddWithValue("$key",  key);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$sc",   statusCode);
            cmd.Parameters.AddWithValue("$at",   addedAt.ToString("o"));
            cmd.Parameters.AddWithValue("$fc",   failCount);
            cmd.ExecuteNonQuery();
        }
    }
}
