namespace VRCNext.Services;

public sealed class VrcSessionTracker
{
    private static readonly TimeSpan SameProcess  = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BeatInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LogWait      = TimeSpan.FromSeconds(90);
    private static readonly DateTime AppStartUtc  = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();

    private readonly CoreLibrary _core;
    private readonly object _lock = new();
    private readonly TaskCompletionSource _repaired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TimelineService? _timeline;
    private string _userId = "";
    private List<VRChatLogWatcher.VrcLogSession>? _logs;
    private bool _isRepaired;
    private string? _openId;
    private DateTime _skipProcStartUtc;
    private DateTime _lastBeat;

    public VrcSessionTracker(CoreLibrary core) { _core = core; }

    public Task WhenRepaired => _repaired.Task;
    public string? OpenStartId { get { lock (_lock) return _openId; } }

    private static string Iso(DateTime utc) => utc.ToString("o");
    private static bool Near(DateTime a, DateTime b) => (a - b).Duration() <= SameProcess;

    private List<VRChatLogWatcher.VrcLogSession> Logs() => _logs ??= VRChatLogWatcher.ReadLogSessions();

    public void Tick(int pid, DateTime procStartUtc)
    {
        lock (_lock)
        {
            var uid = _core.CurrentVrcUserId;
            if (string.IsNullOrEmpty(uid) || _core.Timeline == null) return;
            if (uid != _userId || !ReferenceEquals(_timeline, _core.Timeline))
            {
                _userId = uid;
                _timeline = _core.Timeline;
                _logs = null;
                _isRepaired = false;
                _openId = null;
                _skipProcStartUtc = default;
            }
            if (!_isRepaired)
            {
                RepairLocked(pid != 0 ? procStartUtc : null);
                _isRepaired = true;
                _repaired.TrySetResult();
            }
            if (pid == 0) return;

            var now = DateTime.UtcNow;
            if (_openId == null) OpenLocked(procStartUtc, now);
            else if (now - _lastBeat >= BeatInterval)
            {
                _timeline.SetInstanceEventLeftAt(_openId, Iso(now));
                _lastBeat = now;
            }
        }
    }

    public void End(DateTime endUtc)
    {
        lock (_lock)
        {
            _skipProcStartUtc = default;
            if (_openId == null || _timeline == null) return;
            CloseLocked(_openId, null, endUtc);
            _openId = null;
        }
    }

    public string ResolveVisitEnd(string visitStartIso)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (!Helpers.DateTimeHelper.TryParseUtc(visitStartIso, out var start)) return Iso(now);
            var end = start;

            var log = Logs().FirstOrDefault(l => start >= l.StartUtc - SameProcess && start <= l.EndUtc + SameProcess);
            if (log != null)
            {
                end = log.EndUtc;
                foreach (var left in log.LeftRoomsUtc)
                    if (left >= start) { if (left < end) end = left; break; }
                foreach (var (at, _) in log.JoinsUtc)
                    if (at > start + SameProcess) { if (at < end) end = at; break; }
            }
            else
            {
                var session = _core.Timeline.GetLaunchSessions(_core.CurrentVrcUserId).LastOrDefault(s => s.StartUtc <= start);
                if (session?.EndUtc is DateTime e && e > start) end = e;
            }

            if (end > now) end = now;
            if (end < start) end = start;
            return Iso(end);
        }
    }

    private void OpenLocked(DateTime procStartUtc, DateTime now)
    {
        if (_skipProcStartUtc != default && Near(_skipProcStartUtc, procStartUtc)) return;

        var last = _timeline!.GetLaunchSessions(_userId).LastOrDefault();
        if (last != null && last.StopId == null && Near(last.StartUtc, procStartUtc))
        {
            _openId = last.StartId;
            _lastBeat = DateTime.MinValue;
            return;
        }

        var logUser = _core.LogWatcher.CurrentLogStartUtc is DateTime logStart && Near(logStart, procStartUtc)
            ? _core.LogWatcher.AuthenticatedUserId
            : "";
        if (logUser.Length == 0 && now - procStartUtc < LogWait) return;
        if (logUser.Length > 0 && logUser != _userId)
        {
            _skipProcStartUtc = procStartUtc;
            return;
        }

        var start = logUser == _userId || procStartUtc >= AppStartUtc ? procStartUtc : now;
        if (last?.EndUtc is DateTime lastEnd && start < lastEnd) start = lastEnd;

        var id = _core.LogSelfProfile?.Invoke("launch", "", "start", Iso(start));
        if (string.IsNullOrEmpty(id)) return;
        _timeline.SetInstanceEventLeftAt(id, Iso(now));
        _openId = id;
        _lastBeat = now;
    }

    private void CloseLocked(string startId, string? stopId, DateTime endUtc)
    {
        _timeline!.SetInstanceEventLeftAt(startId, Iso(endUtc));
        if (stopId != null) _timeline.SetEventSpan(stopId, Iso(endUtc), "");
        else _core.LogSelfProfile?.Invoke("launch", "", "stop", Iso(endUtc));
    }

    private void DeleteLocked(string? id)
    {
        if (string.IsNullOrEmpty(id) || !_timeline!.DeleteEvent(id)) return;
        _core.SendToJS("timelineEventDeleted", new { id });
    }

    private void RepairLocked(DateTime? runningStartUtc)
    {
        var own = Logs().Where(l => l.UserId == _userId).ToList();
        bool IsRunning(DateTime t) => runningStartUtc is DateTime r && Near(t, r);

        foreach (var s in _timeline!.GetLaunchSessions(_userId))
        {
            if (s.StopId != null || IsRunning(s.StartUtc)) continue;
            var log = own.FirstOrDefault(l => s.StartUtc >= l.StartUtc - SameProcess && s.StartUtc <= l.EndUtc);
            var end = log?.EndUtc ?? s.EndUtc ?? s.StartUtc;
            CloseLocked(s.StartId, null, end < s.StartUtc ? s.StartUtc : end);
        }

        foreach (var log in own)
        {
            if (IsRunning(log.StartUtc)) continue;
            var overlap = _timeline.GetLaunchSessions(_userId)
                .Where(s => s.StartUtc <= log.EndUtc && (s.EndUtc ?? s.StartUtc) >= log.StartUtc - SameProcess)
                .ToList();
            if (overlap.Count == 0)
            {
                var id = _core.LogSelfProfile?.Invoke("launch", "", "start", Iso(log.StartUtc));
                if (!string.IsNullOrEmpty(id)) CloseLocked(id, null, log.EndUtc);
                continue;
            }

            var keep = overlap[0];
            foreach (var extra in overlap.Skip(1))
            {
                DeleteLocked(extra.StartId);
                DeleteLocked(extra.StopId);
            }
            var start = log.StartUtc < keep.StartUtc ? log.StartUtc : keep.StartUtc;
            var last  = overlap[^1].EndUtc ?? keep.StartUtc;
            var end   = log.EndUtc > last ? log.EndUtc : last;
            _timeline.SetEventSpan(keep.StartId, Iso(start), Iso(end));
            CloseLocked(keep.StartId, keep.StopId, end);
        }
    }
}
