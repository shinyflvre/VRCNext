using Microsoft.Data.Sqlite;
using NativeFileDialogSharp;
using VRCNext.Services;

namespace VRCNext;

public partial class MainForm
{
    /// <summary>
    /// Opens the VRCX database file picker, reads row counts and sends a preview to JS.
    /// The path is stored in _vrcxImportPath; JS then calls importVrcxStart to execute.
    /// </summary>
    private void VrcxSelectAndPreview()
    {
        var r = Dialog.FileOpen("sqlite3,db");
        if (!r.IsOk) return;
        _vrcxImportPath = r.Path;

        try
        {
            using var vrcx = new SqliteConnection($"Data Source={_vrcxImportPath};Mode=ReadOnly");
            vrcx.Open();
            using var cmd = vrcx.CreateCommand();

            long Count(string sql) { cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L); }

            var worlds      = Count("SELECT COUNT(DISTINCT world_id) FROM gamelog_location WHERE world_id != ''");
            var locations   = Count("SELECT COUNT(*) FROM gamelog_location WHERE world_id != ''");
            var friendTimes = Count("SELECT COUNT(DISTINCT user_id) FROM gamelog_join_leave WHERE type='OnPlayerLeft' AND user_id != '' AND time > 0");

            long feedCount(string suffix)
            {
                long total = 0;
                cmd.CommandText = $"SELECT name FROM sqlite_master WHERE name LIKE '%{suffix}' AND type='table'";
                var tables = new List<string>();
                using (var tr = cmd.ExecuteReader()) while (tr.Read()) tables.Add(tr.GetString(0));
                foreach (var t in tables)
                {
                    cmd.CommandText = $"SELECT COUNT(*) FROM \"{t}\"";
                    total += Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
                }
                return total;
            }

            var gps         = feedCount("_feed_gps");
            var onlineOf    = feedCount("_feed_online_offline");
            var statuses    = feedCount("_feed_status");
            var bios        = feedCount("_feed_bio");

            SendToJS("vrcxPreview", new
            {
                path        = System.IO.Path.GetFileName(_vrcxImportPath),
                worlds,
                locations,
                friendTimes,
                gps,
                onlineOffline = onlineOf,
                statuses,
                bios,
            });
        }
        catch (Exception ex)
        {
            SendToJS("vrcxImportError", new { error = ex.Message });
        }
    }

    /// <summary>
    /// Imports VRCX data into VRCNext:
    ///   - World time  (gamelog_location → world_tracking)
    ///   - Friend time (gamelog_join_leave → user_tracking)
    ///   - Timeline instance joins (gamelog_location → events)
    ///   - Friend events: GPS, Online/Offline, Status, Bio (feed_* → friend_events)
    /// Existing VRCNext data is preserved; VRCX values are added on top.
    /// </summary>
    private void ImportVrcxAsync(string vrcxPath)
    {
        try
        {
            SendToJS("vrcxImportProgress", new { status = "Reading database...", percent = 10 });

            var worldMerge   = new List<(string worldId, string worldName, long seconds, int visits, string lastVisited)>();
            var friendMerge  = new List<(string userId, string displayName, long seconds, string lastSeen)>();
            var tlEvents     = new List<TimelineService.TimelineEvent>();
            var friendEvents = new List<TimelineService.FriendTimelineEvent>();

            using var vrcx = new SqliteConnection($"Data Source={vrcxPath};Mode=ReadOnly");
            vrcx.Open();

            using var cmd = vrcx.CreateCommand();

            // ── 1. World time ────────────────────────────────────────────────────
            cmd.CommandText = @"
                SELECT world_id, world_name, SUM(time)/1000, COUNT(*), MAX(created_at)
                FROM gamelog_location
                WHERE world_id != '' AND time > 0
                GROUP BY world_id";
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    worldMerge.Add((r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt32(3), r.GetString(4)));

            SendToJS("vrcxImportProgress", new { status = "Reading friend data...", percent = 25 });

            // ── 2. Friend time ────────────────────────────────────────────────────
            cmd.CommandText = @"
                SELECT user_id, display_name, SUM(time)/1000, MAX(created_at)
                FROM gamelog_join_leave
                WHERE type='OnPlayerLeft' AND user_id != '' AND time > 0
                GROUP BY user_id";
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    friendMerge.Add((r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3)));

            SendToJS("vrcxImportProgress", new { status = "Reading timeline events...", percent = 40 });

            // ── 3a. Build location → players map from gamelog_join_leave ─────────
            var locationPlayers = new Dictionary<string, List<TimelineService.PlayerSnap>>();
            cmd.CommandText = "SELECT DISTINCT user_id, display_name, location FROM gamelog_join_leave WHERE type='OnPlayerJoined' AND user_id != ''";
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var uid = r.GetString(0);
                    var dn  = r.GetString(1);
                    var loc = r.GetString(2);
                    if (!locationPlayers.TryGetValue(loc, out var list))
                        locationPlayers[loc] = list = new List<TimelineService.PlayerSnap>();
                    list.Add(new TimelineService.PlayerSnap { UserId = uid, DisplayName = dn });
                }

            // ── 3b. Timeline: instance_join from gamelog_location ─────────────────
            cmd.CommandText = "SELECT created_at, world_id, world_name, location FROM gamelog_location WHERE world_id != ''";
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var ts  = r.GetString(0);
                    var wid = r.GetString(1);
                    var wn  = r.GetString(2);
                    var loc = r.GetString(3);
                    tlEvents.Add(new TimelineService.TimelineEvent
                    {
                        Id        = "vrcx_loc_" + VrcxHash(ts + wid),
                        Type      = "instance_join",
                        Timestamp = ts,
                        WorldId   = wid,
                        WorldName = wn,
                        Location  = loc,
                        Players   = locationPlayers.TryGetValue(loc, out var pl) ? pl : new(),
                    });
                }

            SendToJS("vrcxImportProgress", new { status = "Reading friend events...", percent = 55 });

            // ── 4. Friend events from all {userId}_feed_* tables ─────────────────
            var userPrefixes = new List<string>();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE name LIKE '%_feed_gps' AND type='table'";
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var tbl = r.GetString(0);
                    userPrefixes.Add(tbl[..tbl.IndexOf("_feed_gps", StringComparison.Ordinal)]);
                }

            foreach (var prefix in userPrefixes)
            {
                // GPS
                TryImportFeed(vrcx, $"{prefix}_feed_gps", r =>
                    new TimelineService.FriendTimelineEvent
                    {
                        Id         = "vrcx_gps_" + VrcxHash(prefix + r.GetInt64(0)),
                        Type       = "friend_gps",
                        Timestamp  = r.GetString(1),
                        FriendId   = r.GetString(2),
                        FriendName = r.GetString(3),
                        Location   = r.GetString(4),
                        WorldName  = r.GetString(5),
                        WorldId    = ExtractWorldId(r.GetString(4)),
                        OldValue   = r.GetString(6), // previous_location
                        NewValue   = r.GetString(4), // new location
                    }, friendEvents);

                // Online / Offline
                TryImportFeed(vrcx, $"{prefix}_feed_online_offline", r =>
                    new TimelineService.FriendTimelineEvent
                    {
                        Id         = "vrcx_oo_" + VrcxHash(prefix + r.GetInt64(0)),
                        Type       = r.GetString(4) == "Online" ? "friend_online" : "friend_offline",
                        Timestamp  = r.GetString(1),
                        FriendId   = r.GetString(2),
                        FriendName = r.GetString(3),
                        Location   = r.GetString(5),
                        WorldName  = r.GetString(6),
                    }, friendEvents);

                // Status — category change (friend_status) + text change (friend_statusdesc)
                try
                {
                    using var stCmd = vrcx.CreateCommand();
                    stCmd.CommandText = $"SELECT * FROM \"{prefix}_feed_status\"";
                    using var stR = stCmd.ExecuteReader();
                    while (stR.Read())
                    {
                        var rowId  = stR.GetInt64(0);
                        var ts     = stR.GetString(1);
                        var uid    = stR.GetString(2);
                        var dn     = stR.GetString(3);
                        var newSt  = stR.IsDBNull(4) ? "" : stR.GetString(4); // status category
                        var newTxt = stR.IsDBNull(5) ? "" : stR.GetString(5); // status_description
                        var oldSt  = stR.IsDBNull(6) ? "" : stR.GetString(6); // previous_status
                        var oldTxt = stR.IsDBNull(7) ? "" : stR.GetString(7); // previous_status_description

                        friendEvents.Add(new TimelineService.FriendTimelineEvent
                        {
                            Id         = "vrcx_st_"  + VrcxHash(prefix + rowId),
                            Type       = "friend_status",
                            Timestamp  = ts,
                            FriendId   = uid,
                            FriendName = dn,
                            OldValue   = oldSt,
                            NewValue   = newSt,
                        });

                        if (newTxt != oldTxt)
                            friendEvents.Add(new TimelineService.FriendTimelineEvent
                            {
                                Id         = "vrcx_sd_" + VrcxHash(prefix + rowId),
                                Type       = "friend_statusdesc",
                                Timestamp  = ts,
                                FriendId   = uid,
                                FriendName = dn,
                                OldValue   = oldTxt,
                                NewValue   = newTxt,
                            });
                    }
                }
                catch { /* table may not exist */ }

                // Bio
                TryImportFeed(vrcx, $"{prefix}_feed_bio", r =>
                    new TimelineService.FriendTimelineEvent
                    {
                        Id         = "vrcx_bio_" + VrcxHash(prefix + r.GetInt64(0)),
                        Type       = "friend_bio",
                        Timestamp  = r.GetString(1),
                        FriendId   = r.GetString(2),
                        FriendName = r.GetString(3),
                        NewValue   = r.GetString(4), // bio
                        OldValue   = r.GetString(5), // previous_bio
                    }, friendEvents);
            }

            SendToJS("vrcxImportProgress", new { status = "Generating meet events...", percent = 65 });

            // ── 5. First meet / Meet again from gamelog_join_leave ────────────────
            var meetEvents   = new List<TimelineService.TimelineEvent>();
            var knownIds     = _timeline.GetKnownUserIds();
            var importSeen   = new HashSet<string>();   // new users discovered during import
            var instanceSeen = new HashSet<string>();   // uid|loc pairs for meet_again dedup

            // location → (worldId, worldName) built from gamelog_location rows already in worldMerge
            var locWorldInfo = new Dictionary<string, (string wid, string wn)>();
            cmd.CommandText = "SELECT location, world_id, world_name FROM gamelog_location WHERE world_id != ''";
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    locWorldInfo[r.GetString(0)] = (r.GetString(1), r.GetString(2));

            cmd.CommandText = @"
                SELECT user_id, display_name, location, created_at
                FROM gamelog_join_leave
                WHERE type='OnPlayerJoined' AND user_id != ''
                ORDER BY created_at";
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var uid = r.GetString(0);
                    var dn  = r.GetString(1);
                    var loc = r.GetString(2);
                    var ts  = r.GetString(3);
                    var (wid, wn) = locWorldInfo.TryGetValue(loc, out var wi) ? wi : (ExtractWorldId(loc), "");

                    var isKnown = knownIds.Contains(uid) || importSeen.Contains(uid);
                    if (!isKnown)
                    {
                        meetEvents.Add(new TimelineService.TimelineEvent
                        {
                            Id        = "vrcx_fm_" + VrcxHash(uid),
                            Type      = "first_meet",
                            Timestamp = ts,
                            UserId    = uid,
                            UserName  = dn,
                            WorldId   = wid,
                            WorldName = wn,
                            Location  = loc,
                        });
                        importSeen.Add(uid);
                        knownIds.Add(uid);
                    }
                    else
                    {
                        var key = uid + "|" + loc;
                        if (!instanceSeen.Contains(key))
                        {
                            instanceSeen.Add(key);
                            meetEvents.Add(new TimelineService.TimelineEvent
                            {
                                Id        = "vrcx_ma_" + VrcxHash(uid + loc),
                                Type      = "meet_again",
                                Timestamp = ts,
                                UserId    = uid,
                                UserName  = dn,
                                WorldId   = wid,
                                WorldName = wn,
                                Location  = loc,
                            });
                        }
                    }
                }

            SendToJS("vrcxImportProgress", new { status = "Merging into VRCNext...", percent = 75 });

            // ── 6. Merge into VRCNext ─────────────────────────────────────────────
            _worldTimeTracker.BulkMerge(worldMerge);
            _timeTracker.BulkMerge(friendMerge);
            SendToJS("vrcxImportProgress", new { status = "Saving timeline...", percent = 88 });
            _timeline.BulkImportEvents(tlEvents);
            _timeline.BulkImportEvents(meetEvents);
            _timeline.BulkImportFriendEvents(friendEvents);
            if (importSeen.Count > 0) _timeline.SeedKnownUsers(importSeen);

            SendToJS("vrcxImportDone", new
            {
                worlds        = worldMerge.Count,
                friends       = friendMerge.Count,
                timelineJoins = tlEvents.Count,
                friendEvents  = friendEvents.Count,
                meetEvents    = meetEvents.Count,
            });
        }
        catch (Exception ex)
        {
            SendToJS("vrcxImportError", new { error = ex.Message });
        }
    }

    private static void TryImportFeed(
        SqliteConnection vrcx,
        string tableName,
        Func<SqliteDataReader, TimelineService.FriendTimelineEvent> map,
        List<TimelineService.FriendTimelineEvent> target)
    {
        try
        {
            using var cmd = vrcx.CreateCommand();
            cmd.CommandText = $"SELECT * FROM \"{tableName}\"";
            using var r = cmd.ExecuteReader();
            while (r.Read()) target.Add(map(r));
        }
        catch { /* table may not exist */ }
    }

    private static string ExtractWorldId(string location)
    {
        if (string.IsNullOrEmpty(location)) return "";
        var colon = location.IndexOf(':');
        var id = colon > 0 ? location[..colon] : location;
        return id.StartsWith("wrld_") ? id : "";
    }

    private static string VrcxHash(object key)
        => Math.Abs(key?.GetHashCode() ?? 0).ToString("x8");
}
