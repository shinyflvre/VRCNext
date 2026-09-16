using Microsoft.Data.Sqlite;
using System.Diagnostics;

namespace VRCNext.Services;

public static class SQLiteOptimizing
{
    private static readonly (string Col, string Label, bool IsInt)[] CleanableCols =
    [
        ("last_seen",                 "Last Seen",           false),
        ("last_seen_location",        "Last Location",       false),
        ("profile_status",            "Status",              false),
        ("profile_status_desc",       "Status Desc",         false),
        ("profile_bio",               "Bio",                 false),
        ("profile_location",          "Location",            false),
        ("profile_cached_at",         "Profile Cache",       false),
        ("profile_last_login",        "Last Login",          false),
        ("profile_last_activity",     "Last Activity",       false),
        ("profile_date_joined",       "Date Joined",         false),
        ("profile_world_name",        "World Name",          false),
        ("profile_instance_type",     "Instance Type",       false),
        ("profile_world_capacity",    "World Capacity",      true),
        ("profile_can_join",          "Can Join",            true),
        ("profile_can_request_invite","Can Req Invite",      true),
        ("profile_can_invite",        "Can Invite",          true),
        ("profile_avatar_file_id",    "Avatar File ID",      false),
        ("profile_state",             "Profile State",       false),
        ("profile_last_platform",     "Last Platform",       false),
        ("profile_user_note",         "User Note",           false),
        ("profile_pronouns",          "Pronouns",            false),
        ("profile_age_verification",  "Age Verification",    false),
        ("profile_bio_links",         "Bio Links",           false),
        ("groups",                    "Groups",              false),
        ("groups_cached_at",          "Groups Cache",        false),
        ("content",                   "Content",             false),
        ("content_cached_at",         "Content Cache",       false),
        ("mutuals",                   "Mutuals",             false),
        ("mutuals_cached_at",         "Mutuals Cache",       false),
        ("mutual_groups",             "Mutual Groups",       false),
        ("mutual_groups_cached_at",   "Mutual Groups Cache", false),
    ];

    public class AnalysisResult
    {
        public long TotalRows          { get; set; }
        public long FriendRows         { get; set; }
        public long CleanableRows      { get; set; }
        public List<(string Label, long Count)> Counts { get; set; } = new();
        public long FriendOnlineCount      { get; set; }
        public long FriendOfflineCount     { get; set; }
        public long FriendStatusCount      { get; set; }
        public long FriendStatusDescCount  { get; set; }
        public long FriendBioCount         { get; set; }
        public long FriendAvatarCount      { get; set; }
        public long NotificationCount      { get; set; }
        public long VideoUrlCount          { get; set; }
        public long AvatarSwitchCount      { get; set; }
        public long InstancePlayersCount   { get; set; }
    }

    public static AnalysisResult Analyze()
    {
        var result = new AnalysisResult();
        using var db = Database.OpenConnection();

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(CASE WHEN profile_is_friend=1 THEN 1 ELSE 0 END),0) FROM user_tracking";
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                result.TotalRows     = r.IsDBNull(0) ? 0 : r.GetInt64(0);
                result.FriendRows    = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                result.CleanableRows = result.TotalRows - result.FriendRows;
            }
        }

        var selects = CleanableCols.Select(c =>
            c.IsInt
                ? $"COALESCE(SUM(CASE WHEN {c.Col}!=0 THEN 1 ELSE 0 END),0)"
                : $"COALESCE(SUM(CASE WHEN {c.Col}!='' AND {c.Col} IS NOT NULL THEN 1 ELSE 0 END),0)");

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = $"SELECT {string.Join(",", selects)} FROM user_tracking WHERE (profile_is_friend IS NULL OR profile_is_friend!=1)";
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                for (int i = 0; i < CleanableCols.Length; i++)
                    result.Counts.Add((CleanableCols[i].Label, r.IsDBNull(i) ? 0 : r.GetInt64(i)));
            }
        }

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(SUM(CASE WHEN profile_bio_links<>'' AND profile_bio_links<>'[]' THEN 1 ELSE 0 END),0) FROM user_tracking WHERE (profile_is_friend IS NULL OR profile_is_friend!=1)";
            var bioCount = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            var idx = result.Counts.FindIndex(x => x.Label == "Bio Links");
            if (idx >= 0) result.Counts[idx] = ("Bio Links", bioCount);
        }

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = @"SELECT
                COALESCE(SUM(CASE WHEN type='friend_online'     THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN type='friend_offline'    THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN type='friend_status'     THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN type='friend_statusdesc' THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN type='friend_bio'        THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN type='friend_avatar'     THEN 1 ELSE 0 END),0)
                FROM friend_events";
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                result.FriendOnlineCount     = r.IsDBNull(0) ? 0 : r.GetInt64(0);
                result.FriendOfflineCount    = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                result.FriendStatusCount     = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                result.FriendStatusDescCount = r.IsDBNull(3) ? 0 : r.GetInt64(3);
                result.FriendBioCount        = r.IsDBNull(4) ? 0 : r.GetInt64(4);
                result.FriendAvatarCount     = r.IsDBNull(5) ? 0 : r.GetInt64(5);
            }
        }

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(COUNT(*),0) FROM events WHERE type='notification'";
            using var r = cmd.ExecuteReader();
            if (r.Read()) result.NotificationCount = r.IsDBNull(0) ? 0 : r.GetInt64(0);
        }

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(COUNT(*),0) FROM events WHERE type='video_url'";
            using var r = cmd.ExecuteReader();
            if (r.Read()) result.VideoUrlCount = r.IsDBNull(0) ? 0 : r.GetInt64(0);
        }

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(COUNT(*),0) FROM events WHERE type='avatar_switch'";
            using var r = cmd.ExecuteReader();
            if (r.Read()) result.AvatarSwitchCount = r.IsDBNull(0) ? 0 : r.GetInt64(0);
        }

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(COUNT(*),0) FROM event_players WHERE event_id IN (SELECT id FROM events WHERE type='instance_join')";
            using var r = cmd.ExecuteReader();
            if (r.Read()) result.InstancePlayersCount = r.IsDBNull(0) ? 0 : r.GetInt64(0);
        }

        return result;
    }

    public static (int UserTrackingCleaned, int FriendEventsCleaned, int NotificationsCleaned, int InstancePlayersCleaned) Optimize()
    {
        using var db = Database.OpenConnection();

        int userCleaned;
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = @"UPDATE user_tracking SET
                last_seen='', last_seen_location='',
                profile_status='', profile_status_desc='', profile_bio='', profile_location='',
                profile_avatar_img='', profile_cached_at='',
                profile_last_login='', profile_last_activity='', profile_date_joined='',
                profile_world_name='', profile_world_thumb='', profile_instance_type='',
                profile_user_count=0, profile_world_capacity=0,
                profile_can_join=0, profile_can_request_invite=0, profile_can_invite=0,
                profile_current_avatar_id='', profile_avatar_file_id='', profile_pic_override='', profile_banner_url='', profile_banner_type='', profile_banner_color='',
                profile_tags='[]', profile_note='', profile_friend_key='', profile_traveling_to='',
                profile_state='', profile_last_platform='', profile_platform='', profile_user_note='',
                profile_in_same_instance=0, profile_pronouns='', profile_age_verification='',
                profile_age_verified=0, profile_economy_creator=0, profile_bio_links='[]', profile_is_favorited=0,
                profile_fav_friend_id='', profile_badges='[]',
                groups='', groups_cached_at='', content='', content_cached_at='',
                mutuals='', mutuals_cached_at='', mutual_groups='', mutual_groups_cached_at=''
                WHERE (profile_is_friend IS NULL OR profile_is_friend!=1)";
            userCleaned = cmd.ExecuteNonQuery();
        }


        const int keepRecent = 100;

        var friendKeep = new Dictionary<string, int>
        {
            ["friend_online"]      = 1000,
            ["friend_offline"]     = 1000,
            ["friend_status"]      = keepRecent,
            ["friend_statusdesc"]  = 200,
            ["friend_bio"]         = 200,
            ["friend_avatar"]      = keepRecent,
        };

        int feCleaned = 0;
        foreach (var (ftype, keep) in friendKeep)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"DELETE FROM friend_events
                WHERE type = $t
                  AND id NOT IN (SELECT id FROM friend_events WHERE type = $t ORDER BY timestamp DESC LIMIT $n)";
            cmd.Parameters.AddWithValue("$t", ftype);
            cmd.Parameters.AddWithValue("$n", keep);
            feCleaned += cmd.ExecuteNonQuery();
        }

        int notifCleaned = 0;
        foreach (var etype in new[] { "notification", "video_url", "avatar_switch" })
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"DELETE FROM events
                WHERE type = $t
                  AND id NOT IN (SELECT id FROM events WHERE type = $t ORDER BY timestamp DESC LIMIT $n)";
            cmd.Parameters.AddWithValue("$t", etype);
            cmd.Parameters.AddWithValue("$n", keepRecent);
            notifCleaned += cmd.ExecuteNonQuery();
        }

        int epCleaned;
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = @"DELETE FROM event_players
                WHERE event_id IN (
                    SELECT id FROM events
                    WHERE type = 'instance_join'
                      AND id NOT IN (SELECT id FROM events WHERE type = 'instance_join' ORDER BY timestamp DESC LIMIT $n)
                )";
            cmd.Parameters.AddWithValue("$n", keepRecent);
            epCleaned = cmd.ExecuteNonQuery();
        }

        return (userCleaned, feCleaned, notifCleaned, epCleaned);
    }

    public class TableSize
    {
        public string Table { get; set; } = "";
        public string Label { get; set; } = "";
        public long   Bytes { get; set; }
        public long   Rows  { get; set; }
    }

    private static readonly Dictionary<string, string> TableLabels = new()
    {
        ["active_session"]            = "Active Sessions",
        ["avatar_tracking"]           = "Avatar Tracking",
        ["avatar_worn_history"]       = "Avatar Worn History",
        ["chatbox_messages"]          = "Chatbox Messages",
        ["detective_changes"]         = "Detective Changes",
        ["detective_tracking"]        = "Detective Tracking",
        ["event_players"]             = "Event Players",
        ["event_tracking"]            = "Event Tracking",
        ["events"]                    = "Timeline Events",
        ["friend_event_colocated"]    = "Friend Event Co-location",
        ["friend_events"]             = "Friend Timeline Events",
        ["group_tracking"]            = "Group Tracking",
        ["image_cache"]               = "Image Cache",
        ["image_versions"]            = "Image Versions",
        ["instance_history"]          = "Instance History",
        ["instance_player_presence"]  = "Instance Player Presence",
        ["instance_sessions"]         = "Instance Sessions",
        ["known_users"]               = "Known Users",
        ["local_fav_groups"]          = "Local Favorite Groups",
        ["local_fav_items"]           = "Local Favorite Items",
        ["logged_notifs"]             = "Logged Notifications",
        ["moderation_log"]            = "Moderation Log",
        ["photo_record_players"]      = "Photo Record Players",
        ["photo_records"]             = "Photo Records",
        ["search_history"]            = "Search History",
        ["user_image_cache"]          = "User Image Cache",
        ["user_tracking"]             = "User Tracking",
        ["world_stats"]               = "World Stats",
        ["world_tracking"]            = "World Tracking",
    };

    private static string PrettyTableLabel(string table)
    {
        if (TableLabels.TryGetValue(table, out var l)) return l;
        var parts = table.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    public static List<TableSize> MemoryUsage()
    {
        using var db = Database.OpenConnection();

        var tables = new List<string>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var r = cmd.ExecuteReader();
            while (r.Read()) tables.Add(r.GetString(0));
        }

        var sizes = new Dictionary<string, long>();
        bool dbstatOk = false;
        try
        {
            var idxToTable = new Dictionary<string, string>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT name, tbl_name FROM sqlite_master WHERE type='index'";
                using var r = cmd.ExecuteReader();
                while (r.Read()) idxToTable[r.GetString(0)] = r.GetString(1);
            }
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT name, SUM(pgsize) FROM dbstat GROUP BY name";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var name = r.GetString(0);
                    var sz   = r.IsDBNull(1) ? 0L : r.GetInt64(1);
                    var tbl  = idxToTable.TryGetValue(name, out var t) ? t : name;
                    sizes[tbl] = (sizes.TryGetValue(tbl, out var cur) ? cur : 0L) + sz;
                }
            }
            dbstatOk = true;
        }
        catch { dbstatOk = false; }

        var result = new List<TableSize>();
        foreach (var tbl in tables)
        {
            long bytes = 0;
            if (dbstatOk) sizes.TryGetValue(tbl, out bytes);
            else bytes = EstimateTableBytes(db, tbl);

            long rows = 0;
            try
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM \"{tbl}\"";
                rows = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }
            catch { }

            result.Add(new TableSize { Table = tbl, Label = PrettyTableLabel(tbl), Bytes = bytes, Rows = rows });
        }

        return result.OrderByDescending(x => x.Bytes).ToList();
    }

    public static (long FileBytes, long FreeBytes) DbFileStats()
    {
        try
        {
            using var db = Database.OpenConnection();
            long Pragma(string p)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "PRAGMA " + p;
                return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }
            long pageSize  = Pragma("page_size");
            long pageCount = Pragma("page_count");
            long freelist  = Pragma("freelist_count");
            return (pageSize * pageCount, pageSize * freelist);
        }
        catch { return (0, 0); }
    }

    private static long EstimateTableBytes(SqliteConnection db, string table)
    {
        try
        {
            var cols = new List<string>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
                using var r = cmd.ExecuteReader();
                while (r.Read()) cols.Add(r.GetString(1));
            }
            if (cols.Count == 0) return 0;
            var sumExpr = string.Join("+", cols.Select(c => $"COALESCE(LENGTH(\"{c}\"),0)"));
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = $"SELECT COALESCE(SUM({sumExpr}),0) FROM \"{table}\"";
                return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }
        }
        catch { return 0; }
    }

    public static void Vacuum()
    {
        using var db = Database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "VACUUM";
        cmd.ExecuteNonQuery();
    }

    public static string CreateBackup()
    {
        var backupDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VRCNext", "Backup");
        Directory.CreateDirectory(backupDir);

        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var destPath = Path.Combine(backupDir, $"Database_Backup_{stamp}.db");

        using var db = Database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"VACUUM INTO '{destPath.Replace("'", "''")}'";
        cmd.ExecuteNonQuery();

        return destPath;
    }

    public static string CreateRegistryBackup()
    {
        var backupDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VRCNext", "Backup");
        Directory.CreateDirectory(backupDir);

        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

#if WINDOWS
        var destPath = Path.Combine(backupDir, $"VRChat_Registry_{stamp}.reg");

        var psi = new ProcessStartInfo("reg")
        {
            Arguments              = $"export \"HKCU\\Software\\VRChat\" \"{destPath}\" /y",
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        using var proc = Process.Start(psi) ?? throw new Exception("Could not start reg.exe");
        proc.WaitForExit(15000);
        if (proc.ExitCode != 0)
            throw new Exception($"reg export failed (exit code {proc.ExitCode})");

        return destPath;
#else
        var pfx = VRCNext.Services.Helpers.VrcPathsHelper.FindProtonPrefix()
            ?? throw new Exception("VRChat Proton prefix not found — is VRChat installed via Steam?");
        var userReg = Path.Combine(pfx, "user.reg");
        if (!File.Exists(userReg))
            throw new Exception("user.reg not found in the VRChat Proton prefix");

        var destPath = Path.Combine(backupDir, $"VRChat_Registry_{stamp}_proton_user.reg");
        File.Copy(userReg, destPath, overwrite: true);
        return destPath;
#endif
    }
}
