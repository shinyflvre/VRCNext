using System.Text.RegularExpressions;

namespace VRCNext.Services;

/// <summary>
/// Watches VRChat's output_log_*.txt files to track players joining/leaving instances.
/// IMPORTANT: VRChat obfuscates component tags (e.g. [NetworkManager] becomes random unicode).
/// So we match on event keywords (OnPlayerJoined etc.) NOT on the component tags.
/// Log path: %LocalAppData%Low\VRChat\VRChat\output_log_*.txt
/// </summary>
public class VRChatLogWatcher : IDisposable
{
    public class PlayerInfo
    {
        public string DisplayName { get; set; } = "";
        public string UserId { get; set; } = "";
        public DateTime JoinedAt { get; set; } = DateTime.Now;
    }

    private readonly Dictionary<string, PlayerInfo> _players = new();
    private readonly object _lock = new();

    private string? _currentLogFile;
    private long _lastPosition;
    private System.Threading.Timer? _pollTimer;
    private string? _currentWorldId;
    private string? _currentLocation; // full instance string e.g. "wrld_abc:12345~private~..."
    private bool _disposed;
    private bool _started;
    private int _totalJoinEvents;
    private int _totalLeftEvents;
    private int _totalRoomEvents;

    public event Action<string>?         DebugLog;
    /// <summary>Fires on a real-time world/instance change (not during log catch-up).</summary>
    public event Action<string, string>? WorldChanged;   // worldId, location
    /// <summary>Fires when a player joins during live play (not during log catch-up).</summary>
    public event Action<string, string>? PlayerJoined;   // userId, displayName
    /// <summary>Fires when a player leaves during live play (not during log catch-up).</summary>
    public event Action<string, string>? PlayerLeft;     // userId, displayName

    private void Log(string msg) => DebugLog?.Invoke(msg);

    // Regex - do NOT match [NetworkManager] or [Behaviour], VRChat obfuscates them!

    // "OnPlayerJoined DisplayName (usr_xxx)" or without userId
    private static readonly Regex RxPlayerJoined = new(
        @"OnPlayerJoined (.+?)(?:\s+\((usr_[a-f0-9\-]+)\))?\s*$",
        RegexOptions.Compiled);
    private static readonly Regex RxPlayerLeft = new(
        @"OnPlayerLeft (.+?)(?:\s+\((usr_[a-f0-9\-]+)\))?\s*$",
        RegexOptions.Compiled);
    // "Joining wrld_xxx:12345~..." captures the full location string
    private static readonly Regex RxRoomJoin = new(
        @"Joining (wrld_[^\s]+)", RegexOptions.Compiled);
    // "Entering Room: WorldName"
    private static readonly Regex RxRoomEnter = new(
        @"Entering Room: (.+)", RegexOptions.Compiled);

    public List<PlayerInfo> GetCurrentPlayers()
    {
        lock (_lock) return _players.Values.ToList();
    }

    public int PlayerCount { get { lock (_lock) return _players.Count; } }
    public string? CurrentWorldId => _currentWorldId;
    /// <summary>Full VRChat instance location string from the log, e.g. "wrld_abc:12345~private~..."</summary>
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

    private string GetLogDirectory()
    {
        // Need: C:\Users\X\AppData\LocalLow\VRChat\VRChat
        // SpecialFolder.LocalApplicationData = C:\Users\X\AppData\Local
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Directory.GetParent(local)?.FullName ?? local;
        return Path.Combine(appData, "LocalLow", "VRChat", "VRChat");
    }

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
        try
        {
            FindLatestLogFile();
            if (_currentLogFile != null) ReadNewLines(catchUp: false);
        }
        catch (Exception ex) { Log($"LogWatcher: Poll error: {ex.Message}"); }
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

        // Room join - clears player list
        if (line.Contains("Joining wrld_"))
        {
            var m = RxRoomJoin.Match(line);
            if (m.Success)
            {
                _currentLocation = m.Groups[1].Value;
                // Extract world ID (part before ':')
                var colon = _currentLocation.IndexOf(':');
                _currentWorldId = colon >= 0 ? _currentLocation.Substring(0, colon) : _currentLocation;
                lock (_lock) _players.Clear();
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
                lock (_lock) _players.Clear();
                _totalRoomEvents++;
                if (!catchUp) Log($"LogWatcher: 🌍 {m.Groups[1].Value}");
                return;
            }
        }

        // Player joined
        if (line.Contains("OnPlayerJoined"))
        {
            var m = RxPlayerJoined.Match(line);
            if (m.Success)
            {
                var name = m.Groups[1].Value.Trim();
                var uid = m.Groups[2].Success ? m.Groups[2].Value : "";
                var key = !string.IsNullOrEmpty(uid) ? uid : name;
                lock (_lock)
                {
                    _players[key] = new PlayerInfo { DisplayName = name, UserId = uid, JoinedAt = DateTime.Now };
                }
                _totalJoinEvents++;
                if (!catchUp)
                {
                    Log($"LogWatcher: ➕ {name} ({_players.Count} now)");
                    PlayerJoined?.Invoke(uid, name);
                }
                return;
            }
        }

        // Player left
        if (line.Contains("OnPlayerLeft"))
        {
            var m = RxPlayerLeft.Match(line);
            if (m.Success)
            {
                var name = m.Groups[1].Value.Trim();
                var uid = m.Groups[2].Success ? m.Groups[2].Value : "";
                var key = !string.IsNullOrEmpty(uid) ? uid : name;
                lock (_lock)
                {
                    if (!_players.Remove(key))
                    {
                        var alt = _players.Where(p => p.Value.DisplayName == name).Select(p => p.Key).FirstOrDefault();
                        if (alt != null) _players.Remove(alt);
                    }
                }
                _totalLeftEvents++;
                if (!catchUp)
                {
                    Log($"LogWatcher: ➖ {name} ({_players.Count} now)");
                    PlayerLeft?.Invoke(uid, name);
                }
                return;
            }
        }
    }

    public void Dispose() { _disposed = true; Stop(); }
}
