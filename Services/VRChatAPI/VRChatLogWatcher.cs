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

    public readonly record struct GameLogLine(string Type, string Timestamp, string Message, string Detail);

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
    public event Action<string>? ImageLoadError;
    public event Action? AvatarBlockedPerf;
    public event Action? ConnectionLost;
    public event Action<string, string, bool>? PlayerModerated;
    private readonly Dictionary<string, string> _wornAvatars = new();

    public event Action<string>? AvatarSeen;
    public event Action<string>? PrintSeen;
    public event Action<string, string, string>? StickerSeen;
    private static readonly System.Text.RegularExpressions.Regex RxStickerSpawn =
        new(@"\[StickersManager\] User (usr_[0-9a-fA-F-]+) \((.*?)\) spawned sticker (inv_[0-9a-fA-F-]+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    public event Action<GameLogLine>? GameLogEntry;

    private static string GlIso(DateTime ts) => ts.ToString("yyyy-MM-ddTHH:mm:ss");

    private void EmitGameLog(bool catchUp, string type, DateTime ts, string message, string detail)
    {
        if (catchUp) return;
        GameLogEntry?.Invoke(new GameLogLine(type, GlIso(ts), message, detail));
    }

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
    private static readonly Regex RxAvatarId = new(
        @"avtr_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);
    private const string RxPortalStr     = "[PortalManager] Pending portal request fulfilled";
    private const string RxAvatarBlkStr  = "Avatar was blocked by local perf limits";
    private const string RxConnLostStr   = "Lost connection to realtime network";

    public string GetWornAvatarName(string displayName)
    {
        if (string.IsNullOrEmpty(displayName)) return "";
        lock (_lock) return _wornAvatars.TryGetValue(displayName, out var v) ? v : "";
    }

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
    public bool SelfLeftRoom { get; private set; }

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

    private static string GetLogDirectory()
    {
        return VRCNext.Services.Helpers.VrcPathsHelper.AppDataDir();
    }

    private static List<string> GetLogFilesNewestFirst()
    {
        try
        {
            var dir = GetLogDirectory();
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory.GetFiles(dir, "output_log_*.txt")
                .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                .ToList();
        }
        catch { return new List<string>(); }
    }

    public static List<GameLogLine> BuildGameLogHistory(int limit = 1000)
    {
        var all = new List<GameLogLine>();
        foreach (var f in GetLogFilesNewestFirst())
        {
            all.AddRange(ScanFileForGameLog(f));
            if (all.Count >= limit) break;
        }
        all.Sort((a, b) => string.CompareOrdinal(b.Timestamp, a.Timestamp));
        if (all.Count > limit) all.RemoveRange(limit, all.Count - limit);
        return all;
    }

    private static List<GameLogLine> ScanFileForGameLog(string path)
    {
        var outp = new List<GameLogLine>();
        if (!File.Exists(path)) return outp;
        string curWorldId = "";
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length < 30) continue;
                var ts = GlIso(ParseLogTimestamp(line));

                if (line.Contains("Joining wrld_"))
                {
                    var m = RxRoomJoin.Match(line);
                    if (m.Success)
                    {
                        var loc = m.Groups[1].Value;
                        var colon = loc.IndexOf(':');
                        curWorldId = colon >= 0 ? loc.Substring(0, colon) : loc;
                    }
                    continue;
                }
                if (line.Contains("Entering Room:"))
                {
                    var m = RxRoomEnter.Match(line);
                    if (m.Success) outp.Add(new GameLogLine("gl_world_join", ts, m.Groups[1].Value.Trim(), curWorldId));
                    continue;
                }
                if (line.Contains("OnPlayerJoined"))
                {
                    var m = RxPlayerJoined.Match(line);
                    if (m.Success) outp.Add(new GameLogLine("gl_player_join", ts, m.Groups[1].Value.Trim(), m.Groups[2].Success ? m.Groups[2].Value : ""));
                    continue;
                }
                if (line.Contains("OnPlayerLeft"))
                {
                    var m = RxPlayerLeft.Match(line);
                    if (m.Success) outp.Add(new GameLogLine("gl_player_left", ts, m.Groups[1].Value.Trim(), m.Groups[2].Success ? m.Groups[2].Value : ""));
                    continue;
                }
                if (line.Contains("resolve URL '") || line.Contains("Resolving URL '"))
                {
                    var m = RxVideoUrl.Match(line);
                    if (m.Success)
                    {
                        var url = m.Groups[1].Value;
                        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
                        outp.Add(new GameLogLine("gl_video_url", ts, host, url));
                    }
                    continue;
                }
                if (line.Contains("Instance closed:"))
                {
                    var m = RxInstanceClosed.Match(line);
                    if (m.Success) outp.Add(new GameLogLine("gl_instance_closed", ts, "Instance closed", m.Groups[1].Value));
                    continue;
                }
                if (line.Contains("[VRC Camera] Took screenshot to:"))
                {
                    var m = RxScreenshot.Match(line);
                    if (m.Success) { var p = m.Groups[1].Value.Trim(); outp.Add(new GameLogLine("gl_screenshot", ts, Path.GetFileName(p), p)); }
                    continue;
                }
                if (line.Contains(RxPortalStr)) { outp.Add(new GameLogLine("gl_portal", ts, "Portal used", "")); continue; }
                if (line.Contains(RxAvatarBlkStr)) { outp.Add(new GameLogLine("gl_avatar_blocked", ts, "Avatar blocked (perf limit)", "")); continue; }
                if (line.Contains(RxConnLostStr)) { outp.Add(new GameLogLine("gl_connection_lost", ts, "Connection lost", "")); continue; }
                if (line.Contains("web request exception occurred while loading image"))
                {
                    var m = RxImageError.Match(line);
                    if (m.Success) { var url = m.Groups[1].Value; var host = Uri.TryCreate(url, UriKind.Absolute, out var u2) ? u2.Host : url; outp.Add(new GameLogLine("gl_image_error", ts, $"Image failed: {host}", url)); }
                    continue;
                }
            }
        }
        catch { }
        return outp;
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
            var end = FindLastNewlineEnd(fs, _lastPosition);
            if (end <= _lastPosition) return;
            fs.Seek(_lastPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(new BoundedReadStream(fs, end - _lastPosition));
            string? line;
            while ((line = reader.ReadLine()) != null) ParseLine(line, catchUp);
            _lastPosition = end;
        }
        catch (IOException) { }
        catch (Exception ex) { Log($"LogWatcher: Read error: {ex.Message}"); }
    }

    private static long FindLastNewlineEnd(FileStream fs, long from)
    {
        var buf = new byte[64 * 1024];
        var pos = fs.Length;
        while (pos > from)
        {
            var chunk = (int)Math.Min(buf.Length, pos - from);
            fs.Seek(pos - chunk, SeekOrigin.Begin);
            var read = 0;
            while (read < chunk)
            {
                var n = fs.Read(buf, read, chunk - read);
                if (n <= 0) break;
                read += n;
            }
            for (var i = read - 1; i >= 0; i--)
                if (buf[i] == (byte)'\n') return pos - chunk + i + 1;
            pos -= chunk;
        }
        return from;
    }

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;
        public BoundedReadStream(Stream inner, long length) { _inner = inner; _remaining = length; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            var n = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static bool TryParseModeration(string rest, out string name, out string modType, out bool active)
    {
        name = ""; modType = ""; active = false;
        static string Pre(string s, string p) => s.Substring(p.Length).Trim();
        static string Suf(string s, string x)
        {
            var n = s.Substring(0, s.Length - x.Length).Trim();
            if (n.EndsWith("'s", StringComparison.Ordinal)) n = n.Substring(0, n.Length - 2).Trim();
            return n;
        }

        if (rest.StartsWith("Requesting block on ",   StringComparison.Ordinal)) { name = Pre(rest, "Requesting block on ");   modType = "block"; active = true;  return name.Length > 0; }
        if (rest.StartsWith("Requesting unblock on ", StringComparison.Ordinal)) { name = Pre(rest, "Requesting unblock on "); modType = "block"; active = false; return name.Length > 0; }

        if (rest.EndsWith(" is now Chatbox Muted", StringComparison.Ordinal)) { name = Suf(rest, " is now Chatbox Muted"); modType = "muteChat"; active = true;  return name.Length > 0; }
        if (rest.EndsWith("Chatbox Shown",         StringComparison.Ordinal)) { name = Suf(rest, "Chatbox Shown");         modType = "muteChat"; active = false; return name.Length > 0; }

        if (rest.EndsWith(" is now Drone Hidden", StringComparison.Ordinal)) { name = Suf(rest, " is now Drone Hidden"); modType = "drone"; active = true;  return name.Length > 0; }
        if (rest.EndsWith("Drone Shown",          StringComparison.Ordinal)) { name = Suf(rest, "Drone Shown");          modType = "drone"; active = false; return name.Length > 0; }

        if (rest.EndsWith(" avatar is hidden",  StringComparison.Ordinal)) { name = Suf(rest, " avatar is hidden");  modType = "hideAvatar"; active = true;  return name.Length > 0; }
        if (rest.EndsWith(" avatar is enabled", StringComparison.Ordinal)) { name = Suf(rest, " avatar is enabled"); modType = "hideAvatar"; active = false; return name.Length > 0; }

        if (rest.EndsWith(" is no longer Muted", StringComparison.Ordinal)) { name = Suf(rest, " is no longer Muted"); modType = "mute"; active = false; return name.Length > 0; }
        if (rest.EndsWith(" is now Muted",       StringComparison.Ordinal)) { name = Suf(rest, " is now Muted");       modType = "mute"; active = true;  return name.Length > 0; }

        return false;
    }

    internal static string ExtractPrintId(string line)
    {
        const string marker = "/api/1/prints/";
        var idx = line.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return "";
        var start = idx + marker.Length;
        var end = start;
        while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_' || line[end] == '-')) end++;
        var id = line.Substring(start, end - start);
        return id.StartsWith("prnt_", StringComparison.Ordinal) ? id : "";
    }

    private void ParseLine(string line, bool catchUp)
    {
        if (line.Length < 30) return;

        if (AvatarSeen != null && line.Contains("avtr_"))
            foreach (Match am in RxAvatarId.Matches(line))
                AvatarSeen.Invoke(am.Value);

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
                SelfLeftRoom = false;
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
                SelfLeftRoom = false;
                _totalRoomEvents++;
                if (!catchUp) Log($"LogWatcher: 🌍 {PendingWorldName}");
                EmitGameLog(catchUp, "gl_world_join", ParseLogTimestamp(line), PendingWorldName, _currentWorldId ?? "");
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
                    EmitGameLog(catchUp, "gl_player_join", joinTs, name, uid);
                }
                return;
            }
        }

        if (line.Contains("OnLeftRoom"))
        {
            SelfLeftRoom = true;
            return;
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
                    EmitGameLog(catchUp, "gl_player_left", leftTs, name, uid);
                }
                return;
            }
        }

        if (line.Contains("resolve URL '") || line.Contains("Resolving URL '"))
        {
            var m = RxVideoUrl.Match(line);
            if (m.Success && !catchUp)
            {
                VideoUrl?.Invoke(m.Groups[1].Value);
                var url = m.Groups[1].Value;
                var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
                EmitGameLog(catchUp, "gl_video_url", ParseLogTimestamp(line), host, url);
            }
            return;
        }

        if (line.Contains("Switching ") && line.Contains(" to avatar "))
        {
            var m = RxAvatarSwitch.Match(line);
            if (m.Success)
            {
                var swName   = m.Groups[1].Value.Trim();
                var swAvatar = m.Groups[2].Value.Trim();
                if (swName.Length > 0 && swAvatar.Length > 0)
                    lock (_lock) _wornAvatars[swName] = swAvatar;
                if (!catchUp) AvatarChanged?.Invoke(swName, swAvatar);
            }
            return;
        }

        if (line.Contains("[ModerationManager]"))
        {
            if (catchUp) return;
            var mi = line.IndexOf("[ModerationManager]", StringComparison.Ordinal);
            var rest = line.Substring(mi + "[ModerationManager]".Length).Trim();
            if (TryParseModeration(rest, out var mn, out var mt, out var ma))
                PlayerModerated?.Invoke(mn, mt, ma);
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
                EmitGameLog(catchUp, "gl_instance_closed", ParseLogTimestamp(line), "Instance closed", loc);
            }
            return;
        }

        if (!catchUp && PrintSeen != null && line.Contains("] Sending Get request to "))
        {
            var pid = ExtractPrintId(line);
            if (pid.Length > 0) { PrintSeen.Invoke(pid); return; }
        }

        if (!catchUp && StickerSeen != null && line.Contains("[StickersManager] User "))
        {
            var sm = RxStickerSpawn.Match(line);
            if (sm.Success) { StickerSeen.Invoke(sm.Groups[1].Value, sm.Groups[2].Value, sm.Groups[3].Value); return; }
        }

        if (line.Contains("[VRC Camera] Took screenshot to:"))
        {
            var m = RxScreenshot.Match(line);
            if (m.Success && !catchUp)
            {
                var p = m.Groups[1].Value.Trim();
                ScreenshotTaken?.Invoke(p);
                EmitGameLog(catchUp, "gl_screenshot", ParseLogTimestamp(line), Path.GetFileName(p), p);
            }
            return;
        }

        if (line.Contains(RxPortalStr) && !catchUp)
        {
            PortalUsed?.Invoke();
            EmitGameLog(catchUp, "gl_portal", ParseLogTimestamp(line), "Portal used", "");
            return;
        }

        if (line.Contains(RxAvatarBlkStr) && !catchUp)
        {
            AvatarBlockedPerf?.Invoke();
            EmitGameLog(catchUp, "gl_avatar_blocked", ParseLogTimestamp(line), "Avatar blocked (perf limit)", "");
            return;
        }

        if (line.Contains(RxConnLostStr) && !catchUp)
        {
            ConnectionLost?.Invoke();
            EmitGameLog(catchUp, "gl_connection_lost", ParseLogTimestamp(line), "Connection lost", "");
            return;
        }

        if (line.Contains("web request exception occurred while loading image"))
        {
            var m = RxImageError.Match(line);
            if (m.Success && !catchUp)
            {
                var url = m.Groups[1].Value;
                ImageLoadError?.Invoke(url);
                var host = Uri.TryCreate(url, UriKind.Absolute, out var u3) ? u3.Host : url;
                EmitGameLog(catchUp, "gl_image_error", ParseLogTimestamp(line), $"Image failed: {host}", url);
            }
            return;
        }
    }

    public void Dispose() { _disposed = true; Stop(); }
}
