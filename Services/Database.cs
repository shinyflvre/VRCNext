using Microsoft.Data.Sqlite;

namespace VRCNext.Services;

/// <summary>
/// Central database access point. All services share a single VRCNData.db file.
/// </summary>
internal static class Database
{
    internal static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "VRCNData.db");

    /// <summary>
    /// Opens a new SQLite connection to VRCNData.db with WAL mode enabled.
    /// Creates the directory if it does not exist. Caller is responsible for disposing.
    /// </summary>
    internal static SqliteConnection OpenConnection()
    {
        var dir = Path.GetDirectoryName(DbPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        cmd.ExecuteNonQuery();

        return conn;
    }
}
