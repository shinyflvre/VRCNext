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
