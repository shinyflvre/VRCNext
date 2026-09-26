using Microsoft.Data.Sqlite;

namespace VRCNext.Services;

public static class SQLiteMigrator
{
    // Scans all photo timeline entries and removes any whose file no longer exists on disk.
    public static async Task PruneOrphanedTimelinePhotosAsync(
        TimelineService timeline,
        Action<int>?    onProgress = null,
        Action<string>? onDeleted  = null)
    {
        await Task.Delay(4000); 
        await Task.Run(() =>
        {
            onProgress?.Invoke(1);
            var deleted = timeline.PruneOrphanedPhotos(onProgress);
            foreach (var id in deleted)
                onDeleted?.Invoke(id);
            onProgress?.Invoke(-1);
        });
    }

    public static async Task RepairEventPlayerSessionsAsync(
        AppSettings     settings,
        TimelineService timeline,
        Action<int>?    onProgress = null)
    {
        if (settings.EventPlayerSessionsRepaired) return;

        await Task.Delay(8000);
        onProgress?.Invoke(1);

        try
        {
            var paths = Database.EnumerateAccountDbPaths();
            var count = Math.Max(paths.Count, 1);
            for (var i = 0; i < paths.Count; i++)
            {
                var from = 5 + (int)(i * 90.0 / count);
                var to   = 5 + (int)((i + 1) * 90.0 / count);
                List<(string EventId, string UserId, List<string> LeftAts)> fixes;
                using (var db = Database.OpenConnectionAt(paths[i]))
                    fixes = RepairEventPlayerSessions(db, onProgress, from, to);
                if (fixes.Count > 0 && string.Equals(paths[i], Database.DbPath, StringComparison.OrdinalIgnoreCase))
                    timeline.ApplyPlayerSessionRepairs(fixes);
                await Task.Delay(15);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Migration] RepairEventPlayerSessions failed: {ex.Message}");
            onProgress?.Invoke(-1);
            return;
        }

        onProgress?.Invoke(100);
        settings.EventPlayerSessionsRepaired = true;
        settings.Save();
    }

    public static async Task RepairRewindHistoryAsync(
        AppSettings            settings,
        Func<TimelineService>  timeline,
        VrcSessionTracker      sessions,
        Action<int>?           onProgress = null)
    {
        if (settings.RewindHistoryRepaired) return;

        await sessions.WhenRepaired;
        await Task.Delay(3000);
        onProgress?.Invoke(1);

        try
        {
            await Task.Run(() =>
            {
                var paths = Database.EnumerateAccountDbPaths();
                var joins = new List<(DateTime At, string Loc)>();
                foreach (var path in paths)
                    using (var db = Database.OpenConnectionAt(path))
                        joins.AddRange(ReadJoins(db, null));
                joins.Sort((a, b) => a.At.CompareTo(b.At));

                for (var i = 0; i < paths.Count; i++)
                {
                    var active = string.Equals(paths[i], Database.DbPath, StringComparison.OrdinalIgnoreCase);
                    bool changed;
                    using (var db = Database.OpenConnectionAt(paths[i]))
                        changed = RepairRewindHistory(db, AccountIdForDb(paths[i], settings), joins, active ? sessions.OpenStartId : null);
                    if (changed && active) timeline().ReloadFromDb();
                    onProgress?.Invoke(5 + (int)((i + 1) * 90.0 / Math.Max(paths.Count, 1)));
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Migration] RepairRewindHistory failed: {ex.Message}");
            onProgress?.Invoke(-1);
            return;
        }

        onProgress?.Invoke(100);
        settings.RewindHistoryRepaired = true;
        settings.Save();
    }

    private const string DbFileSuffix = "_VRCNData.db";
    private static readonly TimeSpan SameVisit  = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SameJoin   = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ChainGap   = TimeSpan.FromMinutes(5);

    private static List<(DateTime At, string Loc)> ReadJoins(SqliteConnection db, SqliteTransaction? tx)
    {
        var result = new List<(DateTime At, string Loc)>();
        using var cmd = Cmd(db, tx, "SELECT timestamp, COALESCE(location,'') FROM events WHERE type='instance_join'");
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (Helpers.DateTimeHelper.TryParseUtc(r.GetString(0), out var t)) result.Add((t, r.GetString(1)));
        return result;
    }

    private static string AccountIdForDb(string path, AppSettings settings)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("usr_", StringComparison.Ordinal) && name.EndsWith(DbFileSuffix, StringComparison.Ordinal)
            ? name[..^DbFileSuffix.Length]
            : settings.PrimaryAccount?.UserId ?? "";
    }

    private static string Iso(DateTime utc) => utc.ToString("o");

    private static SqliteCommand Cmd(SqliteConnection db, SqliteTransaction? tx, string sql)
    {
        var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static List<DateTime> ReadTimes(SqliteConnection db, SqliteTransaction? tx, string sql)
    {
        var result = new List<DateTime>();
        using var cmd = Cmd(db, tx, sql);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (!r.IsDBNull(0) && Helpers.DateTimeHelper.TryParseUtc(r.GetString(0), out var t)) result.Add(t);
        result.Sort();
        return result;
    }

    private static void DeleteEvents(SqliteConnection db, SqliteTransaction tx, IEnumerable<string> ids)
    {
        using var delPlayers = Cmd(db, tx, "DELETE FROM event_players WHERE event_id=$id");
        using var delEvent   = Cmd(db, tx, "DELETE FROM events WHERE id=$id");
        var p1 = delPlayers.Parameters.Add("$id", SqliteType.Text);
        var p2 = delEvent.Parameters.Add("$id", SqliteType.Text);
        foreach (var id in ids)
        {
            p1.Value = id; delPlayers.ExecuteNonQuery();
            p2.Value = id; delEvent.ExecuteNonQuery();
        }
    }

    private static bool RepairRewindHistory(SqliteConnection db, string owner, List<(DateTime At, string Loc)> allJoins, string? skipId)
    {
        using var tx = db.BeginTransaction();
        var changed = DedupeVisits(db, tx);
        changed |= CapVisits(db, tx, allJoins);
        changed |= DedupeAvatarSwitches(db, tx);
        if (!string.IsNullOrEmpty(owner))
        {
            changed |= RepairLaunches(db, tx, owner, skipId);
            changed |= RemoveForeignPhotos(db, tx, owner);
        }
        tx.Commit();
        return changed;
    }

    private static bool DedupeVisits(SqliteConnection db, SqliteTransaction tx)
    {
        var rows = new List<(string Id, DateTime At, string Loc, long Tracked, long Players)>();
        using (var cmd = Cmd(db, tx, @"SELECT e.id, e.timestamp, COALESCE(e.location,''), COALESCE(e.tracked,0),
                (SELECT COUNT(*) FROM event_players p WHERE p.event_id=e.id)
                FROM events e WHERE e.type='instance_join'"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                if (Helpers.DateTimeHelper.TryParseUtc(r.GetString(1), out var at)) rows.Add((r.GetString(0), at, r.GetString(2), r.GetInt64(3), r.GetInt64(4)));

        var drop = new List<string>();
        foreach (var group in rows.Where(x => x.Loc.Length > 0).GroupBy(x => x.Loc))
        {
            var list = group.OrderBy(x => x.At).ToList();
            var keep = list[0];
            for (var i = 1; i < list.Count; i++)
            {
                var cur = list[i];
                if (cur.At - keep.At > SameVisit) { keep = cur; continue; }
                var better = (cur.Tracked, cur.Players).CompareTo((keep.Tracked, keep.Players)) > 0;
                drop.Add(better ? keep.Id : cur.Id);
                if (better) keep = cur;
            }
        }
        if (drop.Count == 0) return false;
        DeleteEvents(db, tx, drop);
        return true;
    }

    private static int FirstIndexAfter<T>(List<T> sorted, Func<T, DateTime> key, DateTime t)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (key(sorted[mid]) <= t) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static bool CapVisits(SqliteConnection db, SqliteTransaction tx, List<(DateTime At, string Loc)> allJoins)
    {
        var stops = ReadTimes(db, tx, "SELECT timestamp FROM events WHERE type='profile' AND notif_type='launch' AND message='stop'");
        var visits = new List<(string Id, DateTime Start, DateTime End, string Loc)>();
        using (var cmd = Cmd(db, tx, "SELECT id, timestamp, left_at, COALESCE(location,'') FROM events WHERE type='instance_join' AND tracked=1 AND left_at<>''"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                if (Helpers.DateTimeHelper.TryParseUtc(r.GetString(1), out var s) && Helpers.DateTimeHelper.TryParseUtc(r.GetString(2), out var e)) visits.Add((r.GetString(0), s, e, r.GetString(3)));

        var capped = new List<(string Id, DateTime End)>();
        foreach (var (id, start, end, loc) in visits)
        {
            var cap = end;
            for (var i = FirstIndexAfter(allJoins, j => j.At, start + SameVisit); i < allJoins.Count && allJoins[i].At < cap; i++)
            {
                if (allJoins[i].Loc == loc && allJoins[i].At - start <= SameJoin) continue;
                cap = allJoins[i].At;
                break;
            }
            var si = FirstIndexAfter(stops, t => t, start);
            if (si < stops.Count && stops[si] < cap) cap = stops[si];
            if (cap < end) capped.Add((id, cap < start ? start : cap));
        }
        if (capped.Count == 0) return false;

        using var updEvent = Cmd(db, tx, "UPDATE events SET left_at=$la WHERE id=$id");
        var pLa = updEvent.Parameters.Add("$la", SqliteType.Text);
        var pId = updEvent.Parameters.Add("$id", SqliteType.Text);
        using var selPlayers = Cmd(db, tx, "SELECT user_id, joined_at, left_at FROM event_players WHERE event_id=$id");
        var pSel = selPlayers.Parameters.Add("$id", SqliteType.Text);
        using var updPlayer = Cmd(db, tx, "UPDATE event_players SET joined_at=$ja, left_at=$la WHERE event_id=$id AND user_id=$uid");
        var uJa = updPlayer.Parameters.Add("$ja", SqliteType.Text);
        var uLa = updPlayer.Parameters.Add("$la", SqliteType.Text);
        var uId = updPlayer.Parameters.Add("$id", SqliteType.Text);
        var uUid = updPlayer.Parameters.Add("$uid", SqliteType.Text);

        foreach (var (id, end) in capped)
        {
            var endIso = Iso(end);
            pLa.Value = endIso; pId.Value = id;
            updEvent.ExecuteNonQuery();

            var players = new List<(string Uid, string Ja, string La)>();
            pSel.Value = id;
            using (var r = selPlayers.ExecuteReader())
                while (r.Read())
                    players.Add((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2)));

            foreach (var (uid, ja, la) in players)
            {
                var joinsIn  = TimelineService.PlayerSnap.ParseSessions(ja);
                var leftsIn  = TimelineService.PlayerSnap.ParseSessions(la);
                var joinsOut = new List<string>();
                var leftsOut = new List<string>();
                for (var k = 0; k < joinsIn.Count; k++)
                {
                    if (!Helpers.DateTimeHelper.TryParseUtc(joinsIn[k], out var j) || j >= end) continue;
                    joinsOut.Add(joinsIn[k]);
                    var left = k < leftsIn.Count ? leftsIn[k] : "";
                    leftsOut.Add(!Helpers.DateTimeHelper.TryParseUtc(left, out var l) || l > end ? endIso : left);
                }
                if (joinsOut.SequenceEqual(joinsIn) && leftsOut.SequenceEqual(leftsIn)) continue;
                uJa.Value = TimelineService.PlayerSnap.SerializeSessions(joinsOut);
                uLa.Value = TimelineService.PlayerSnap.SerializeSessions(leftsOut);
                uId.Value = id; uUid.Value = uid;
                updPlayer.ExecuteNonQuery();
            }
        }
        return true;
    }

    private static bool DedupeAvatarSwitches(SqliteConnection db, SqliteTransaction tx)
    {
        var drop = new List<string>();
        string prevId = "", prevName = "";
        var first = true;
        using (var cmd = Cmd(db, tx, "SELECT id, COALESCE(user_id,''), COALESCE(user_name,'') FROM events WHERE type='avatar_switch' ORDER BY timestamp"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var avatarId = r.GetString(1);
                var name     = r.GetString(2);
                var same = !first && (avatarId.Length > 0 && prevId.Length > 0 ? avatarId == prevId : name == prevName);
                if (same) { drop.Add(r.GetString(0)); continue; }
                prevId = avatarId; prevName = name; first = false;
            }
        if (drop.Count == 0) return false;
        DeleteEvents(db, tx, drop);
        return true;
    }

    private static bool RepairLaunches(SqliteConnection db, SqliteTransaction tx, string owner, string? skipId)
    {
        var rows = new List<(string Id, DateTime At, string LeftAt, bool IsStart, string Name, string Image)>();
        using (var cmd = Cmd(db, tx, @"SELECT id, timestamp, COALESCE(left_at,''), COALESCE(message,''),
                COALESCE(user_name,''), COALESCE(user_image,'')
                FROM events WHERE type='profile' AND notif_type='launch' AND user_id=$uid ORDER BY timestamp"))
        {
            cmd.Parameters.AddWithValue("$uid", owner);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (Helpers.DateTimeHelper.TryParseUtc(r.GetString(1), out var at))
                    rows.Add((r.GetString(0), at, r.GetString(2), r.GetString(3) == "start", r.GetString(4), r.GetString(5)));
        }
        if (rows.Count == 0) return false;

        var visits = new List<(DateTime Start, DateTime End)>();
        using (var cmd = Cmd(db, tx, "SELECT timestamp, left_at FROM events WHERE type='instance_join' AND tracked=1 AND left_at<>''"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                if (Helpers.DateTimeHelper.TryParseUtc(r.GetString(0), out var s) && Helpers.DateTimeHelper.TryParseUtc(r.GetString(1), out var e) && e > s) visits.Add((s, e));
        visits.Sort((a, b) => a.Start.CompareTo(b.Start));

        DateTime ChainForward(DateTime from, DateTime until)
        {
            var cur = from;
            foreach (var v in visits)
            {
                if (v.End <= from) continue;
                if (v.Start > cur + ChainGap || v.Start >= until) break;
                if (v.End > cur) cur = v.End < until ? v.End : until;
            }
            return cur;
        }

        DateTime ChainBackward(DateTime to, DateTime after)
        {
            var cur = to;
            for (var i = visits.Count - 1; i >= 0; i--)
            {
                var v = visits[i];
                if (v.Start >= to) continue;
                if (v.End < cur - ChainGap || v.End <= after) break;
                if (v.Start < cur) cur = v.Start > after ? v.Start : after;
            }
            return cur;
        }

        using var updLeft = Cmd(db, tx, "UPDATE events SET left_at=$la WHERE id=$id");
        var pLa = updLeft.Parameters.Add("$la", SqliteType.Text);
        var pId = updLeft.Parameters.Add("$id", SqliteType.Text);
        using var ins = Cmd(db, tx, @"INSERT INTO events(id,type,timestamp,user_id,user_name,user_image,notif_type,notif_title,message,left_at,tracked)
            VALUES($id,'profile',$ts,$uid,$un,$ui,'launch','',$msg,$la,0)");
        var iId  = ins.Parameters.Add("$id",  SqliteType.Text);
        var iTs  = ins.Parameters.Add("$ts",  SqliteType.Text);
        var iMsg = ins.Parameters.Add("$msg", SqliteType.Text);
        var iLa  = ins.Parameters.Add("$la",  SqliteType.Text);
        var iUn  = ins.Parameters.Add("$un",  SqliteType.Text);
        var iUi  = ins.Parameters.Add("$ui",  SqliteType.Text);
        ins.Parameters.AddWithValue("$uid", owner);

        void Insert(DateTime at, string msg, string leftAt, string name, string image)
        {
            iId.Value = Guid.NewGuid().ToString("N")[..8];
            iTs.Value = Iso(at); iMsg.Value = msg; iLa.Value = leftAt; iUn.Value = name; iUi.Value = image;
            ins.ExecuteNonQuery();
        }

        var changed = false;
        var now = DateTime.UtcNow;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Id == skipId) continue;
            var prev = i > 0 ? rows[i - 1] : default;
            var next = i + 1 < rows.Count ? rows[i + 1] : default;

            if (row.IsStart)
            {
                if (next.Id != null && !next.IsStart)
                {
                    if (row.LeftAt == Iso(next.At)) continue;
                    pLa.Value = Iso(next.At); pId.Value = row.Id;
                    updLeft.ExecuteNonQuery();
                    changed = true;
                    continue;
                }
                var until = next.Id != null ? next.At : now;
                var end = ChainForward(row.At, until);
                if (Helpers.DateTimeHelper.TryParseUtc(row.LeftAt, out var beat) && beat > end && beat <= until) end = beat;
                pLa.Value = Iso(end); pId.Value = row.Id;
                updLeft.ExecuteNonQuery();
                Insert(end, "stop", "", row.Name, row.Image);
                changed = true;
            }
            else if (prev.Id == null || !prev.IsStart)
            {
                var after = prev.Id != null ? prev.At : DateTime.MinValue;
                var start = ChainBackward(row.At, after);
                if (start >= row.At) continue;
                Insert(start, "start", Iso(row.At), row.Name, row.Image);
                changed = true;
            }
        }
        return changed;
    }

    private static bool RemoveForeignPhotos(SqliteConnection db, SqliteTransaction tx, string owner)
    {
        var photos = new List<(string Id, string Path)>();
        using (var cmd = Cmd(db, tx, "SELECT id, photo_path FROM events WHERE type='photo' AND COALESCE(photo_path,'')<>''"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) photos.Add((r.GetString(0), r.GetString(1)));
        if (photos.Count == 0) return false;

        var players = new Dictionary<string, List<string>>();
        using (var cmd = Cmd(db, tx, @"SELECT p.event_id, p.user_id FROM event_players p
                JOIN events e ON e.id=p.event_id WHERE e.type='photo'"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                if (!players.TryGetValue(r.GetString(0), out var list)) players[r.GetString(0)] = list = new();
                list.Add(r.GetString(1));
            }

        var drop = photos
            .Where(p => File.Exists(p.Path)
                && !UnifiedTimeEngine.IsOwnPhoto(p.Path, players.TryGetValue(p.Id, out var ids) ? ids : new List<string>(), owner))
            .Select(p => p.Id)
            .ToList();
        if (drop.Count == 0) return false;
        DeleteEvents(db, tx, drop);
        return true;
    }

    private static List<(string EventId, string UserId, List<string> LeftAts)> RepairEventPlayerSessions(
        SqliteConnection db, Action<int>? onProgress, int pctFrom, int pctTo)
    {
        var rows = new List<(string EventId, string UserId, string Ja, string La, string End)>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = @"SELECT ep.event_id, ep.user_id, ep.joined_at, ep.left_at, e.left_at
                                FROM event_players ep
                                JOIN events e ON e.id = ep.event_id
                                WHERE e.type = 'instance_join'
                                  AND e.left_at IS NOT NULL AND e.left_at != ''
                                  AND ep.joined_at LIKE '[%'";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add((
                    r.GetString(0),
                    r.GetString(1),
                    r.IsDBNull(2) ? "" : r.GetString(2),
                    r.IsDBNull(3) ? "" : r.GetString(3),
                    r.IsDBNull(4) ? "" : r.GetString(4)));
            }
        }

        var fixes = new List<(string EventId, string UserId, List<string> LeftAts)>();
        var total = Math.Max(rows.Count, 1);
        for (var i = 0; i < rows.Count; i++)
        {
            var (eventId, userId, ja, la, end) = rows[i];
            var joins    = TimelineService.PlayerSnap.ParseSessions(ja);
            var lefts    = TimelineService.PlayerSnap.ParseSessions(la);
            var repaired = TimelineService.PlayerSnap.RepairLefts(joins, lefts, end);
            if (!repaired.SequenceEqual(lefts)) fixes.Add((eventId, userId, repaired));
            if (i % 200 == 0) onProgress?.Invoke(pctFrom + (int)(i * (pctTo - pctFrom) * 0.7 / total));
        }

        if (fixes.Count == 0) return fixes;

        using var tx  = db.BeginTransaction();
        using var upd = db.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = "UPDATE event_players SET left_at = $la WHERE event_id = $eid AND user_id = $uid";
        var pLa  = upd.Parameters.Add("$la",  SqliteType.Text);
        var pEid = upd.Parameters.Add("$eid", SqliteType.Text);
        var pUid = upd.Parameters.Add("$uid", SqliteType.Text);
        foreach (var (eventId, userId, leftAts) in fixes)
        {
            pLa.Value  = TimelineService.PlayerSnap.SerializeSessions(leftAts);
            pEid.Value = eventId;
            pUid.Value = userId;
            upd.ExecuteNonQuery();
        }
        tx.Commit();
        onProgress?.Invoke(pctTo);
        return fixes;
    }
}
