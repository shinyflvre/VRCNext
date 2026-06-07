using System.Text.RegularExpressions;

namespace VRCNext.Services;

public class VRChatLogWatcher : IDisposable
{
    public class PlayerInfo
    {
        public string DisplayName { get; set; } = "";
        public string UserId { get; set; } = "";
        public DateTime JoinedAt { get; set; } = DateTime.Now;
    }

    public struct PlayerEvent
    {
        public string   UserId      { get; init; }
        public string   DisplayName { get; init; }
        public string   Type        { get; init; } // "join" | "left"
        public DateTime Timestamp   { get; init; }
    }

    private readonly Dictionary<string, PlayerInfo> _players = new();
    // Append-only log of every OnPlayerJoined / OnPlayerLeft since the last room change.
    // Captured during catchUp AND live, used as source-of-truth for restart reconciliation.
    private readonly List<PlayerEvent> _playerEventLog = new();
    // Last OnPlayerLeft timestamp per player key, used to close stale sessions after VRCNext restart.
    private readonly Dictionary<string, DateTime> _pastLefts = new();
    private readonly object _lock = new();

    private string? _currentLogFile;
    private long _lastPosition;
    private System.Threading.Timer? _pollTimer;
    private string? _currentWorldId;
    private string? _currentLocation; // full instance string e.g. "wrld_abc:12345~private~..."
    private bool _disposed;
    private bool _started;
    private int _polling; // 0 = idle, 1 = busy — prevents concurrent poll execution
    private int _totalJoinEvents;
    private int _totalLeftEvents;
    private int _totalRoomEvents;

    public event Action<string>?         DebugLog;
    public event Action<string, string>? WorldChanged;
    public event Action<string>? InstanceClosed;
    public event Action<string, string>? AvatarChanged;
    public event Action<string>? VideoUrl;
    public event Action<string, string>? PlayerJoined;
    public event Action<string, string>? PlayerLeft;
    public event Action<string>? ScreenshotTaken;
    public event Action? PortalUsed;
    public event Action? AvatarBlockedPerf;
    public event Action<string>? ImageLoadError;
    public event Action? ConnectionLost;

    public DateTime? WorldJoinedAt { get; private set; }
    public string PendingWorldName { get; private set; } = "";

    private void Log(string msg) => DebugLog?.Invoke(msg);

    private static DateTime ParseLogTimestamp(string line)
    {
        // Manual parse avoids Interop+Globalization.CompareString (ICU)
        if (line.Length >= 19
            && int.TryParse(line.AsSpan(0,  4), out int yr)
            && int.TryParse(line.AsSpan(5,  2), out int mo)
            && int.TryParse(line.AsSpan(8,  2), out int dy)
            && int.TryParse(line.AsSpan(11, 2), out int hh)
            && int.TryParse(line.AsSpan(14, 2), out int mm)
            && int.TryParse(line.AsSpan(17, 2), out int ss))
        {
            try { return new DateTime(yr, mo, dy, hh, mm, ss); } catch { }
        }
        return DateTime.Now;
    }

    // [Behaviour] prefix required to avoid false matches from world scripts
    private static readonly Regex RxPlayerJoined = new(
        @"\[Behaviour\]\s+OnPlayerJoined (.+?)(?:\s+\(([A-Za-z0-9_\-]+)\))?\s*$",
        RegexOptions.Compiled);
    private static readonly Regex RxPlayerLeft = new(
        @"\[Behaviour\]\s+OnPlayerLeft (.+?)(?:\s+\(([A-Za-z0-9_\-]+)\))?\s*$",
        RegexOptions.Compiled);
    private static readonly Regex RxRoomJoin = new(
        @"Joining (wrld_[^\s]+)", RegexOptions.Compiled);
    private static readonly Regex RxInstanceClosed = new(
        @"Instance closed: (wrld_[^\s]+)", RegexOptions.Compiled);
    private static readonly Regex RxAvatarSwitch = new(
        @"Switching (.+?) to avatar (.+)$", RegexOptions.Compiled);
    private static readonly Regex RxVideoUrl = new(
        @"(?:Attempting to resolve|Resolving) URL '([^']+)'", RegexOptions.Compiled);
    private static readonly Regex RxRoomEnter = new(
        @"Entering Room: (.+)", RegexOptions.Compiled);
    private static readonly Regex RxScreenshot = new(
        @"\[VRC Camera\] Took screenshot to: (.+)", RegexOptions.Compiled);
    private static readonly Regex RxImageError = new(
        @"\[Image Download\] .+web request exception occurred while loading image from URL '([^']+)'", RegexOptions.Compiled);
    private const string RxPortalStr     = "[PortalManager] Pending portal request fulfilled";
    private const string RxAvatarBlkStr  = "Avatar was blocked by local perf limits";
    private const string RxConnLostStr   = "Lost connection to realtime network";

    public List<PlayerInfo> GetCurrentPlayers()
    {
        lock (_lock) return _players.Values.ToList();
    }

    public DateTime? GetLastLeftTime(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return null;
        lock (_lock) return _pastLefts.TryGetValue(userId, out var t) ? t : null;
    }

    // Returns a copy of every player join/left observed in the current room session, in log order.
    public List<PlayerEvent> GetPlayerEventLog()
    {
        lock (_lock) return new List<PlayerEvent>(_playerEventLog);
    }

    public int PlayerCount { get { lock (_lock) return _players.Count; } }
    public string? CurrentWorldId => _currentWorldId;
    public string? CurrentLocation => _currentLocation;

    public string GetDiagnostics()
    {
        var dir = GetLogDirectory();
        var exists = Directory.Exists(dir);
        int fc = 0;
        try { if (exists) fc = Directory.GetFiles(dir, "output_log_*.txt").Length; } catch { }
        return $"dir={dir} exists={exists} files={fc} watching={Path.GetFileName(_currentLogFile ?? "NONE")} " +
               $"pos={_lastPosition} players={PlayerCount} joins={_totalJoinEvents} lefts={_totalLeftEvents} rooms={_totalRoomEvents}";
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        Stop();
        Log("LogWatcher: Starting...");

        var dir = GetLogDirectory();
        Log($"LogWatcher: Path = {dir}");
        Log($"LogWatcher: Exists = {Directory.Exists(dir)}");

        FindLatestLogFile();
        if (_currentLogFile != null)
        {
            var fi = new FileInfo(_currentLogFile);
            Log($"LogWatcher: File = {fi.Name}, {fi.Length / 1024}KB");
            ReadNewLines(catchUp: true);
            Log($"LogWatcher: Catch-up: {_players.Count} players, {_totalJoinEvents} joins, {_totalRoomEvents} rooms");
        }
        else
        {
            Log("LogWatcher: ⚠ No log file found!");
            try
            {
                if (Directory.Exists(dir))
                {
                    var txt = Directory.GetFiles(dir, "*.txt").Take(5).Select(Path.GetFileName);
                    Log($"LogWatcher: txt files: {string.Join(", ", txt)}");
                }
            }
            catch { }
        }
        _pollTimer = new System.Threading.Timer(_ => PollLogFile(), null, 1000, 1000);
    }

    public void Stop() { _pollTimer?.Dispose(); _pollTimer = null; }

    private string GetLogDirectory() => Helpers.VrcPaths.VrcDataDir();

    private void FindLatestLogFile()
    {
        try
        {
            var dir = GetLogDirectory();
            if (!Directory.Exists(dir)) return;
            var files = Directory.GetFiles(dir, "output_log_*.txt")
                .OrderByDescending(f => new FileInfo(f).LastWriteTime).ToList();
            if (files.Count == 0) return;
            var latest = files[0];
            if (latest != _currentLogFile)
            {
                _currentLogFile = latest;
                _lastPosition = 0;
                lock (_lock) _players.Clear();
                _totalJoinEvents = 0; _totalLeftEvents = 0; _totalRoomEvents = 0;
                Log($"LogWatcher: Switched to {Path.GetFileName(latest)}");
            }
        }
        catch (Exception ex) { Log($"LogWatcher: FindLatest error: {ex.Message}"); }
    }

    private void PollLogFile()
    {
        if (_disposed) return;
        if (System.Threading.Interlocked.Exchange(ref _polling, 1) == 1) return; // skip if previous poll still running
        try
        {
            FindLatestLogFile();
            if (_currentLogFile != null) ReadNewLines(catchUp: false);
        }
        catch (Exception ex) { Log($"LogWatcher: Poll error: {ex.Message}"); }
        finally { System.Threading.Interlocked.Exchange(ref _polling, 0); }
    }

    private void ReadNewLines(bool catchUp)
    {
        if (_currentLogFile == null || !File.Exists(_currentLogFile)) return;
        try
        {
            using var fs = new FileStream(_currentLogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < _lastPosition) { _lastPosition = 0; lock (_lock) _players.Clear(); }
            if (fs.Length == _lastPosition) return;
            fs.Seek(_lastPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) != null) ParseLine(line, catchUp);
            _lastPosition = fs.Position;
        }
        catch (IOException) { }
        catch (Exception ex) { Log($"LogWatcher: Read error: {ex.Message}"); }
    }

    private void ParseLine(string line, bool catchUp)
    {
        if (line.Length < 30) return;

        if (line.Contains("Joining wrld_"))
        {
            var m = RxRoomJoin.Match(line);
            if (m.Success)
            {
                _currentLocation = m.Groups[1].Value;
                var colon = _currentLocation.IndexOf(':');
                _currentWorldId = colon >= 0 ? _currentLocation.Substring(0, colon) : _currentLocation;
                WorldJoinedAt = ParseLogTimestamp(line);
                lock (_lock) { _players.Clear(); _pastLefts.Clear(); _playerEventLog.Clear(); }
                _totalRoomEvents++;
                if (!catchUp)
                {
                    Log($"LogWatcher: 🌍 Joined {_currentLocation}");
                    WorldChanged?.Invoke(_currentWorldId, _currentLocation);
                }
                return;
            }
        }
        if (line.Contains("Entering Room:"))
        {
            var m = RxRoomEnter.Match(line);
            if (m.Success)
            {
                PendingWorldName = m.Groups[1].Value.Trim();
                lock (_lock) { _players.Clear(); _pastLefts.Clear(); _playerEventLog.Clear(); }
                _totalRoomEvents++;
                if (!catchUp) Log($"LogWatcher: 🌍 {PendingWorldName}");
                return;
            }
        }

        if (line.Contains("OnPlayerJoined"))
        {
            var m = RxPlayerJoined.Match(line);
            if (m.Success)
            {
                var name = m.Groups[1].Value.Trim();
                var uid = m.Groups[2].Success ? m.Groups[2].Value : "";
                var key = !string.IsNullOrEmpty(uid) ? uid : name;
                var joinTs = ParseLogTimestamp(line);
                bool isNew;
                lock (_lock)
                {
                    isNew = !_players.ContainsKey(key);
                    _players[key] = new PlayerInfo { DisplayName = name, UserId = uid, JoinedAt = joinTs };
                    _pastLefts.Remove(key);
                    _playerEventLog.Add(new PlayerEvent { UserId = uid, DisplayName = name, Type = "join", Timestamp = joinTs });
                }
                _totalJoinEvents++;
                if (!catchUp && isNew)
                {
                    Log($"LogWatcher: ➕ {name} ({_players.Count} now)");
                    PlayerJoined?.Invoke(uid, name);
                }
                return;
            }
        }

        if (line.Contains("OnPlayerLeft"))
        {
            var m = RxPlayerLeft.Match(line);
            if (m.Success)
            {
                var name = m.Groups[1].Value.Trim();
                var uid = m.Groups[2].Success ? m.Groups[2].Value : "";
                var key = !string.IsNullOrEmpty(uid) ? uid : name;
                var leftTs = ParseLogTimestamp(line);
                bool wasPresent;
                lock (_lock)
                {
                    wasPresent = _players.Remove(key);
                    if (!wasPresent)
                    {
                        var alt = _players.Where(p => p.Value.DisplayName == name).Select(p => p.Key).FirstOrDefault();
                        if (alt != null) { _players.Remove(alt); wasPresent = true; key = alt; }
                    }
                    if (!string.IsNullOrEmpty(uid)) _pastLefts[uid] = leftTs;
                    _playerEventLog.Add(new PlayerEvent { UserId = uid, DisplayName = name, Type = "left", Timestamp = leftTs });
                }
                _totalLeftEvents++;
                if (!catchUp && wasPresent)
                {
                    Log($"LogWatcher: ➖ {name} ({_players.Count} now)");
                    PlayerLeft?.Invoke(uid, name);
                }
                return;
            }
        }

        if (line.Contains("resolve URL '") || line.Contains("Resolving URL '"))
        {
            var m = RxVideoUrl.Match(line);
            if (m.Success && !catchUp)
                VideoUrl?.Invoke(m.Groups[1].Value);
            return;
        }

        if (line.Contains("Switching ") && line.Contains(" to avatar "))
        {
            var m = RxAvatarSwitch.Match(line);
            if (m.Success && !catchUp)
                AvatarChanged?.Invoke(m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim());
            return;
        }

        if (line.Contains("Instance closed:"))
        {
            var m = RxInstanceClosed.Match(line);
            if (m.Success && !catchUp)
            {
                var loc = m.Groups[1].Value;
                Log($"LogWatcher: 🔒 Instance closed: {loc}");
                InstanceClosed?.Invoke(loc);
            }
            return;
        }

        if (line.Contains("[VRC Camera] Took screenshot to:"))
        {
            var m = RxScreenshot.Match(line);
            if (m.Success && !catchUp)
                ScreenshotTaken?.Invoke(m.Groups[1].Value.Trim());
            return;
        }

        if (line.Contains(RxPortalStr) && !catchUp)
        {
            PortalUsed?.Invoke();
            return;
        }

        if (line.Contains(RxAvatarBlkStr) && !catchUp)
        {
            AvatarBlockedPerf?.Invoke();
            return;
        }

        if (line.Contains(RxConnLostStr) && !catchUp)
        {
            ConnectionLost?.Invoke();
            return;
        }

        if (line.Contains("web request exception occurred while loading image"))
        {
            var m = RxImageError.Match(line);
            if (m.Success && !catchUp)
                ImageLoadError?.Invoke(m.Groups[1].Value);
            return;
        }
    }

    public void Dispose() { _disposed = true; Stop(); }
}
