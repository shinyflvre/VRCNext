using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace VRCNext.Services;

internal static class Database
{
    private static readonly string BaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCNext");
    private static readonly string PrimaryDbPath = Path.Combine(BaseDir, "VRCNData.db");

    private static string _currentDbPath = PrimaryDbPath;
    internal static string DbPath => _currentDbPath;

    private static readonly Regex VrcUserIdRegex =
        new(@"^usr_[a-zA-Z0-9-]+$", RegexOptions.Compiled);

    // Validates VRChat UserId format for DB-path resolution and API-response checks.
    internal static bool IsValidVrcUserId(string? id) =>
        !string.IsNullOrEmpty(id) && VrcUserIdRegex.IsMatch(id!);

    // Sets the active DB path from the account, throwing if a secondary lacks a valid UserId.
    internal static void SetActiveAccount(VrcAccount? account)
    {
        if (account == null || account.IsPrimary)
        {
            _currentDbPath = PrimaryDbPath;
            return;
        }
        if (!IsValidVrcUserId(account.UserId))
        {
            throw new InvalidOperationException(
                $"Secondary account '{account.AccountId}' has missing or invalid UserId — cannot resolve DB path.");
        }
        _currentDbPath = Path.Combine(BaseDir, $"{account.UserId}_VRCNData.db");
    }

    internal static SqliteConnection OpenConnection() => OpenConnectionAt(_currentDbPath);

    internal static SqliteConnection OpenConnectionAt(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        var conn = Track(new SqliteConnection($"Data Source={dbPath}"));
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA cache_size=-1024;";
        cmd.ExecuteNonQuery();

        return conn;
    }

    private static readonly object _trackedLock = new();
    private static readonly List<WeakReference<SqliteConnection>> _tracked = new();

    internal static SqliteConnection Track(SqliteConnection conn)
    {
        lock (_trackedLock)
        {
            if (_tracked.Count >= 64) _tracked.RemoveAll(w => !w.TryGetTarget(out _));
            _tracked.Add(new WeakReference<SqliteConnection>(conn));
        }
        return conn;
    }

    internal static void ReleaseConnectionMemory()
    {
        var live = new List<SqliteConnection>();
        lock (_trackedLock)
        {
            for (int i = _tracked.Count - 1; i >= 0; i--)
            {
                if (_tracked[i].TryGetTarget(out var conn)) live.Add(conn);
                else _tracked.RemoveAt(i);
            }
        }
        foreach (var conn in live) ReleaseConnectionMemory(conn);
    }

    private static void ReleaseConnectionMemory(SqliteConnection conn)
    {
        try
        {
            if (conn.State != System.Data.ConnectionState.Open) return;
            var handle = conn.Handle;
            if (handle == null || handle.IsInvalid) return;
            bool added = false;
            try
            {
                handle.DangerousAddRef(ref added);
                var db = handle.DangerousGetHandle();
                if (db != IntPtr.Zero && sqlite3_db_mutex(db) != IntPtr.Zero)
                    sqlite3_db_release_memory(db);
            }
            finally { if (added) handle.DangerousRelease(); }
        }
        catch { }
    }

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_db_mutex(IntPtr db);

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_db_release_memory(IntPtr db);

    private static readonly Regex AccountDbFileRegex =
        new(@"^usr_[a-zA-Z0-9-]+_VRCNData\.db$", RegexOptions.Compiled);

    internal static List<string> EnumerateAccountDbPaths()
    {
        var result = new List<string>();
        try
        {
            if (!Directory.Exists(BaseDir)) return result;
            foreach (var file in Directory.GetFiles(BaseDir, "*VRCNData.db"))
            {
                var name = Path.GetFileName(file);
                if (name == "VRCNData.db" || AccountDbFileRegex.IsMatch(name)) result.Add(file);
            }
        }
        catch { }
        return result;
    }

    private static readonly string MediaDbPath = Path.Combine(BaseDir, "MediaLibrary.db");

    internal static SqliteConnection OpenMediaConnection()
    {
        if (!Directory.Exists(BaseDir)) Directory.CreateDirectory(BaseDir);

        var conn = Track(new SqliteConnection($"Data Source={MediaDbPath}"));
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA cache_size=-1024;";
        cmd.ExecuteNonQuery();

        return conn;
    }

    private static readonly string VRCNPlusDbPath = Path.Combine(BaseDir, "VRCNPlus.sqlite");

    internal static SqliteConnection OpenVRCNPlusConnection()
    {
        if (!Directory.Exists(BaseDir)) Directory.CreateDirectory(BaseDir);

        var conn = Track(new SqliteConnection($"Data Source={VRCNPlusDbPath}"));
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA cache_size=-1024;";
        cmd.ExecuteNonQuery();

        return conn;
    }
}
