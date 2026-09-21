using Microsoft.Data.Sqlite;

namespace VRCNext.Services.Helpers;

// Persistent avatar cache 
public static class AvtrdbCacheHelper
{
    private const long TTLSeconds = 30 * 86400L; 
    private const long UnresolvedRetrySeconds = 3600L;

    private static readonly string _dbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "AvatarDB", "Avatars.db");

    private static readonly object _lock = new();
    private static SqliteConnection? _conn;

    private static SqliteConnection Conn()
    {
        if (_conn != null) return _conn;
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        _conn = new SqliteConnection($"Data Source={_dbPath}");
        _conn.Open();
        Run("PRAGMA journal_mode=WAL");
        Run("PRAGMA cache_size=-1024");
        Run("""
            CREATE TABLE IF NOT EXISTS Avatar_Deletion (
                Avtr_ID     TEXT PRIMARY KEY,
                Submit_Date INTEGER NOT NULL,
                DB_Source   TEXT    NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Avatar_User_Content (
                User_ID     TEXT    NOT NULL,
                Avtr_ID     TEXT    NOT NULL,
                Avtr_Date   INTEGER NOT NULL,
                Submit_Date INTEGER NOT NULL,
                DB_Source   TEXT    NOT NULL,
                PRIMARY KEY (User_ID, Avtr_ID)
            );
            CREATE TABLE IF NOT EXISTS Avatar_File_Cache (
                File_ID     TEXT PRIMARY KEY,
                Avtr_ID     TEXT    NOT NULL DEFAULT '',
                Name        TEXT    NOT NULL DEFAULT '',
                Author_Name TEXT    NOT NULL DEFAULT '',
                Author_ID   TEXT    NOT NULL DEFAULT '',
                Image_URL   TEXT    NOT NULL DEFAULT '',
                DB_Source   TEXT    NOT NULL DEFAULT '',
                Resolved_At INTEGER NOT NULL DEFAULT 0
            );
            """);
        return _conn;
    }

    private static void Run(string sql)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static long Cutoff() => Now() - TTLSeconds;

    public static List<string> LoadAllDeletedIds()
    {
        lock (_lock)
        {
            using var cmd = Conn().CreateCommand();
            cmd.CommandText = "SELECT Avtr_ID FROM Avatar_Deletion";
            var list = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    public static bool IsDeletedCached(string avatarId)
    {
        lock (_lock)
        {
            using var cmd = Conn().CreateCommand();
            cmd.CommandText = "SELECT Submit_Date FROM Avatar_Deletion WHERE Avtr_ID = @id";
            cmd.Parameters.AddWithValue("@id", avatarId);
            return cmd.ExecuteScalar() is long ts && ts >= Cutoff();
        }
    }

    public static void MarkDeletedBatch(IEnumerable<string> avatarIds, string dbSource)
    {
        lock (_lock)
        {
            var conn = Conn();
            using var tx   = conn.BeginTransaction();
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO Avatar_Deletion (Avtr_ID, Submit_Date, DB_Source) VALUES (@id, @ts, @src)";
            var pId  = cmd.Parameters.Add("@id",  SqliteType.Text);
            var pTs  = cmd.Parameters.Add("@ts",  SqliteType.Integer);
            var pSrc = cmd.Parameters.Add("@src", SqliteType.Text);
            pTs.Value  = Now();
            pSrc.Value = dbSource;
            foreach (var id in avatarIds) { pId.Value = id; cmd.ExecuteNonQuery(); }
            tx.Commit();
        }
    }

    public sealed record FileAvatarEntry(
        string FileId, string AvtrId, string Name,
        string AuthorName, string AuthorId, string ImageUrl, string Source, long ResolvedAt = 0);

    public static bool NeedsRetry(FileAvatarEntry e)
        => string.IsNullOrEmpty(e.AvtrId) && Now() - e.ResolvedAt >= UnresolvedRetrySeconds;

    public static FileAvatarEntry? GetFileAvatar(string fileId)
    {
        if (string.IsNullOrEmpty(fileId)) return null;
        lock (_lock)
        {
            try
            {
                using var cmd = Conn().CreateCommand();
                cmd.CommandText = """
                    SELECT Avtr_ID, Name, Author_Name, Author_ID, Image_URL, DB_Source, Resolved_At
                    FROM Avatar_File_Cache WHERE File_ID = @id AND Resolved_At >= @cutoff
                    """;
                cmd.Parameters.AddWithValue("@id",     fileId);
                cmd.Parameters.AddWithValue("@cutoff", Cutoff());
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;
                return new FileAvatarEntry(fileId, r.GetString(0), r.GetString(1),
                    r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt64(6));
            }
            catch { return null; }
        }
    }

    public static void SaveFileAvatar(string fileId, string avtrId, string name,
        string authorName, string authorId, string imageUrl, string source)
    {
        if (string.IsNullOrEmpty(fileId)) return;
        lock (_lock)
        {
            try
            {
                using var cmd = Conn().CreateCommand();
                cmd.CommandText = """
                    INSERT INTO Avatar_File_Cache
                        (File_ID, Avtr_ID, Name, Author_Name, Author_ID, Image_URL, DB_Source, Resolved_At)
                    VALUES (@fid, @aid, @n, @an, @auid, @img, @src, @ts)
                    ON CONFLICT(File_ID) DO UPDATE SET
                        Avtr_ID     = excluded.Avtr_ID,
                        Name        = excluded.Name,
                        Author_Name = excluded.Author_Name,
                        Author_ID   = excluded.Author_ID,
                        Image_URL   = excluded.Image_URL,
                        DB_Source   = excluded.DB_Source,
                        Resolved_At = excluded.Resolved_At
                    """;
                cmd.Parameters.AddWithValue("@fid",  fileId);
                cmd.Parameters.AddWithValue("@aid",  avtrId     ?? "");
                cmd.Parameters.AddWithValue("@n",    name       ?? "");
                cmd.Parameters.AddWithValue("@an",   authorName ?? "");
                cmd.Parameters.AddWithValue("@auid", authorId   ?? "");
                cmd.Parameters.AddWithValue("@img",  imageUrl   ?? "");
                cmd.Parameters.AddWithValue("@src",  source     ?? "");
                cmd.Parameters.AddWithValue("@ts",   Now());
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }

    public static bool IsUserContentCached(string avatarId)
    {
        lock (_lock)
        {
            using var cmd = Conn().CreateCommand();
            cmd.CommandText = "SELECT 1 FROM Avatar_User_Content WHERE Avtr_ID = @id AND Submit_Date >= @cutoff LIMIT 1";
            cmd.Parameters.AddWithValue("@id",     avatarId);
            cmd.Parameters.AddWithValue("@cutoff", Cutoff());
            return cmd.ExecuteScalar() != null;
        }
    }

    public static void MarkUserContentBatch(string userId, IEnumerable<string> avatarIds, string dbSource)
    {
        lock (_lock)
        {
            var conn = Conn();
            using var tx  = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Avatar_User_Content (User_ID, Avtr_ID, Avtr_Date, Submit_Date, DB_Source)
                VALUES (@uid, @id, @now, @now, @src)
                ON CONFLICT(User_ID, Avtr_ID) DO UPDATE SET
                    Submit_Date = excluded.Submit_Date,
                    DB_Source   = excluded.DB_Source
                """;
            var pUid = cmd.Parameters.Add("@uid", SqliteType.Text);
            var pId  = cmd.Parameters.Add("@id",  SqliteType.Text);
            var pNow = cmd.Parameters.Add("@now", SqliteType.Integer);
            var pSrc = cmd.Parameters.Add("@src", SqliteType.Text);
            pUid.Value = userId;
            pNow.Value = Now();
            pSrc.Value = dbSource;
            foreach (var id in avatarIds) { pId.Value = id; cmd.ExecuteNonQuery(); }
            tx.Commit();
        }
    }
}
