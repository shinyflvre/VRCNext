using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VRCNext.Services;
#if WINDOWS
using Windows.Media.Control;
#endif

namespace VRCNext
{
    public class ChatboxService : IDisposable
    {
#if WINDOWS
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
#endif

        private const string OSC_IP = "127.0.0.1";
        private const int OSC_PORT = 9000;
        private const int MAX_CHATBOX_CHARS = 144;
        private const int MIN_INTERVAL_MS = 1500;

        private UdpClient? _udp;
        private CancellationTokenSource? _cts;
        private bool _running;

        // Config
        public bool Enabled { get; set; }
        public bool ShowTime { get; set; } = true;
        public bool ShowMedia { get; set; } = true;
        public bool ShowPlaytime { get; set; } = true;
        public bool ShowCustomText { get; set; } = true;
        public bool ShowSystemStats { get; set; }
        public bool ShowPulse { get; set; }
        public string PulseFormat { get; set; } = "\U0001F49A {bpm} BPM";
        public Func<int>? PulseProvider { get; set; }
        public bool ShowWindow { get; set; }
        public string WindowFormat { get; set; } = "\U0001FA9F On desktop \"{app}\"";
        public bool ShowWeather { get; set; }
        public string WeatherFormat { get; set; } = "{icon} {temp}";
        public Func<(string icon, string temp)?>? WeatherProvider { get; set; }
        public bool StatCpu { get; set; } = true;
        public bool StatRam { get; set; } = true;
        public bool StatGpu { get; set; }
        public bool StatVram { get; set; }
        public bool ShowAfk { get; set; }
        public bool ShowAfkTime { get; set; } = true;
        public string AfkMessage { get; set; } = "Currently AFK";
        public int AfkMouseSeconds { get; set; } = 10;
        public int AfkKeyboardSeconds { get; set; } = 10;
        public bool SuppressNotifSound { get; set; } = true;
        public bool HideChatboxBackground { get; set; } = false;
        public string TimeFormat { get; set; } = "hh:mm tt";
        public string Separator { get; set; } = " | ";
        public const string CustomSeparatorKey = "custom";
        public string CustomTemplate { get; set; } = "";
        public int IntervalMs { get; set; } = 5000;
        public List<CbCustomLine> CustomLines { get; set; } = new();
        public List<string> LineOrder { get; set; } = new(DefaultLineOrder);
        private int _customLineIndex;

        public static readonly string[] DefaultLineOrder = { "time", "media", "stats", "pulse", "weather", "window", "custom" };

        // Media state
        public string CurrentTitle { get; private set; } = "";
        public string CurrentArtist { get; private set; } = "";
        public TimeSpan CurrentPosition { get; private set; }
        public TimeSpan CurrentDuration { get; private set; }
        public bool IsPlaying { get; private set; }

        // Position interpolation (browsers don't push continuous SMTC updates)
        private string _smtcTrackKey = "";
        private TimeSpan _smtcLastReportedPos;
        private TimeSpan _smtcBasePos;
        private DateTimeOffset _smtcBaseTime = DateTimeOffset.MinValue;

        // System stats
#if WINDOWS
        private PerformanceCounter? _cpuCounter;
#else
        private long _prevCpuTotal, _prevCpuIdle;
#endif
        private float _cpuPercent;
        private float _ramUsedGB;
        private float _ramTotalGB;
        private float _gpuPercent;
        private float _vramUsedGB;
        private float _vramTotalGB;
        private bool  _gpuAvailable;
#if WINDOWS
        private readonly List<PerformanceCounter> _gpuCounters  = new();
        private readonly List<PerformanceCounter> _vramCounters = new();
        private DateTime _gpuCountersBuilt = DateTime.MinValue;
#endif

        // Direct send pause
        private long _pauseUntilTick;

        // AFK
        private bool _isAfk;
        private DateTime _afkSince;
#if WINDOWS
        private POINT _lastCursor;
        private uint _lastInputTick;
        private int _lastMouseMoveTick;
        private int _lastKeyboardTick;
        private bool _idlePrimed;
#endif

        private readonly Action<string> _log;
        private Action<object>? _onUpdate;

        public ChatboxService(Action<string> log) { _log = log; }
        public void SetUpdateCallback(Action<object> cb) => _onUpdate = cb;

        public void Start()
        {
            if (_running) return;
            _running = true;
            _cts = new CancellationTokenSource();
            _udp = new UdpClient();
            _udp.Connect(IPAddress.Parse(OSC_IP), OSC_PORT);
            _log("[Chatbox] Started");
            _ = RunLoopAsync(_cts.Token);
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            _cts?.Cancel();
            try { SendOscChatbox("", true); } catch { }
            _udp?.Close(); _udp = null;
            _log("[Chatbox] Stopped");
        }

        private async Task RunLoopAsync(CancellationToken ct)
        {
#if WINDOWS
            try { _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true); _cpuCounter.NextValue(); }
            catch (Exception ex) { _log($"[Chatbox] CPU counter init: {ex.Message}"); }
#endif
            try { var gi = GC.GetGCMemoryInfo(); _ramTotalGB = gi.TotalAvailableMemoryBytes / (1024f * 1024f * 1024f); }
            catch { _ramTotalGB = 0; }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (ShowMedia) await UpdateMediaInfoAsync();
                    if (ShowSystemStats) UpdateSystemStats();
                    if (ShowAfk) UpdateAfkState();

                    var text = BuildChatboxText();
                    if (Enabled && !string.IsNullOrEmpty(text) && Environment.TickCount64 >= Interlocked.Read(ref _pauseUntilTick))
                        SendOscChatbox(text, SuppressNotifSound);

                    _onUpdate?.Invoke(new {
                        currentTitle = CurrentTitle, currentArtist = CurrentArtist,
                        positionMs = (long)CurrentPosition.TotalMilliseconds,
                        durationMs = (long)CurrentDuration.TotalMilliseconds,
                        isPlaying = IsPlaying, chatboxText = text, enabled = Enabled,
                        cpuPercent = _cpuPercent, ramUsedGB = _ramUsedGB, ramTotalGB = _ramTotalGB,
                        gpuPercent = _gpuPercent, vramUsedGB = _vramUsedGB, vramTotalGB = _vramTotalGB, gpuAvailable = _gpuAvailable,
                        isAfk = _isAfk,
                    });
                    await Task.Delay(Math.Max(IntervalMs, MIN_INTERVAL_MS), ct);
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex) { _log($"[Chatbox] Error: {ex.Message}"); await Task.Delay(2000, ct); }
            }
#if WINDOWS
            _cpuCounter?.Dispose(); _cpuCounter = null;
            DisposeGpuCounters();
#endif
        }

        private string BuildChatboxText()
        {
            // When hiding background, append \u0003\u001f — VRChat renders text without the bubble background.
            // Reserve 2 chars for the suffix, so max usable text is 142 instead of 144.
            int limit = HideChatboxBackground ? MAX_CHATBOX_CHARS - 2 : MAX_CHATBOX_CHARS;

            if (ShowAfk && _isAfk)
            {
                var msg = AfkMessage;
                if (ShowAfkTime)
                {
                    var d = DateTime.Now - _afkSince;
                    var t = d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes}m" : $"{(int)d.TotalMinutes}m";
                    msg = $"{msg} ({t})";
                }
                if (msg.Length > limit) msg = msg[..limit];
                return HideChatboxBackground ? msg + "\u0003\u001f" : msg;
            }

            if (Separator == CustomSeparatorKey)
            {
                var tpl = BuildFromTemplate();
                if (tpl.Length > limit) tpl = tpl[..limit];
                return HideChatboxBackground ? tpl + "\u0003\u001f" : tpl;
            }

            var parts = new List<string>();
            foreach (var segment in EffectiveLineOrder())
            {
                var part = segment switch
                {
                    "time"   => ShowTime ? FormatClock() : null,
                    "media"  => BuildMediaPart(),
                    "stats"  => BuildStatsPart(),
                    "pulse"  => BuildPulsePart(),
                    "weather" => BuildWeatherPart(),
                    "window" => BuildWindowPart(),
                    "custom" => NextCustomLine(),
                    _        => null,
                };
                if (!string.IsNullOrEmpty(part)) parts.Add(part!);
            }

            var result = string.Join(Separator, parts);
            if (result.Length > limit) result = result[..limit];
            return HideChatboxBackground ? result + "\u0003\u001f" : result;
        }

        private string? BuildPulsePart()
        {
            if (!ShowPulse || PulseProvider == null) return null;
            int bpm;
            try { bpm = PulseProvider(); } catch { return null; }
            if (bpm <= 0) return null;
            var fmt = string.IsNullOrWhiteSpace(PulseFormat) ? "\u2665 {bpm} BPM" : PulseFormat;
            return fmt.Replace("{bpm}", bpm.ToString(CultureInfo.InvariantCulture));
        }

        private string? BuildWindowPart()
        {
            if (!ShowWindow) return null;
            var app = VRCNext.Services.Helpers.ForegroundAppHelper.GetActiveAppName();
            if (string.IsNullOrWhiteSpace(app)) return null;
            var fmt = string.IsNullOrWhiteSpace(WindowFormat) ? "{app}" : WindowFormat;
            return fmt.Replace("{app}", app);
        }

        private string? BuildWeatherPart()
        {
            if (!ShowWeather || WeatherProvider == null) return null;
            (string icon, string temp)? w;
            try { w = WeatherProvider(); } catch { return null; }
            if (w == null) return null;
            var fmt = string.IsNullOrWhiteSpace(WeatherFormat) ? "{icon} {temp}" : WeatherFormat;
            return fmt.Replace("{icon}", w.Value.icon).Replace("{temp}", w.Value.temp);
        }

        private string? PartFor(string key) => key switch
        {
            "time"                            => ShowTime ? FormatClock() : null,
            "media" or "playing" or "music"   => BuildMediaPart(),
            "system" or "stats" or "sysinfo"  => BuildStatsPart(),
            "pulse" or "heart" or "bpm" or "heartrate" or "heart rate" => BuildPulsePart(),
            "weather"                         => BuildWeatherPart(),
            "window" or "app"                 => BuildWindowPart(),
            "custom" or "customtext" or "custom text" => NextCustomLine(),
            _                                 => null,
        };

        /// <summary>
        /// Renders the user template. Placeholders look like [time] or [custom text]; anything else is
        /// literal. Glue around a placeholder that resolves to nothing is dropped, so a line never ends
        /// up with a dangling separator.
        /// </summary>
        private string BuildFromTemplate()
        {
            var lines = (CustomTemplate ?? "").Replace("\r\n", "\n").Split('\n');
            var outLines = new List<string>();

            foreach (var line in lines)
            {
                var sb = new System.Text.StringBuilder();
                var emitted = false;
                var pending = "";
                var pos = 0;

                while (pos < line.Length)
                {
                    var open = line.IndexOf('[', pos);
                    if (open < 0) break;
                    var close = line.IndexOf(']', open + 1);
                    if (close < 0) break;

                    pending += line[pos..open];
                    var key = line[(open + 1)..close].Trim().ToLowerInvariant();
                    string? value = null;
                    try { value = PartFor(key); } catch { }

                    if (!string.IsNullOrEmpty(value))
                    {
                        if (emitted) sb.Append(pending);
                        sb.Append(value);
                        emitted = true;
                    }
                    pending = "";
                    pos = close + 1;
                }

                var tail = pending + (pos < line.Length ? line[pos..] : "");
                if (tail.Any(char.IsLetterOrDigit)) sb.Append(tail);

                var text = sb.ToString().Trim();
                if (text.Length > 0) outLines.Add(text);
            }

            return string.Join("\n", outLines);
        }

        private List<string> EffectiveLineOrder()
        {
            var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var raw in LineOrder ?? new List<string>())
            {
                var key = (raw ?? "").Trim().ToLowerInvariant();
                if (Array.IndexOf(DefaultLineOrder, key) >= 0 && seen.Add(key)) order.Add(key);
            }
            foreach (var key in DefaultLineOrder)
                if (seen.Add(key)) order.Add(key);
            return order;
        }

        private string FormatClock()
        {
            try { return DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture); }
            catch { return DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture); }
        }

        private string? BuildMediaPart()
        {
            if (!ShowMedia || !IsPlaying || string.IsNullOrEmpty(CurrentTitle)) return null;
            var m = $"\"{CurrentTitle}\"";
            if (!string.IsNullOrEmpty(CurrentArtist)) m += $" by {CurrentArtist}";
            if (ShowPlaytime && CurrentDuration.TotalSeconds > 0)
                m += $" [{FormatTime(CurrentPosition)}/{FormatTime(CurrentDuration)}]";
            return m;
        }

        private string? BuildStatsPart()
        {
            if (!ShowSystemStats) return null;
            var bits = new List<string>();
            if (StatCpu)  bits.Add($"CPU {_cpuPercent:0}%");
            if (StatRam && _ramTotalGB > 0) bits.Add($"RAM {_ramUsedGB:0.0}/{_ramTotalGB:0.0}GB");
            if (StatGpu && _gpuAvailable)   bits.Add($"GPU {_gpuPercent:0}%");
            if (StatVram && _vramUsedGB > 0) bits.Add(_vramTotalGB > 0 ? $"VRAM {_vramUsedGB:0.0}/{_vramTotalGB:0}GB" : $"VRAM {_vramUsedGB:0.0}GB");
            return bits.Count == 0 ? null : string.Join(" ", bits);
        }

        private string? NextCustomLine()
        {
            if (!ShowCustomText) return null;
            var active = CustomLines
                .Where(l => l != null && l.Enabled && !string.IsNullOrWhiteSpace(l.Text))
                .ToList();
            if (active.Count == 0) return null;
            var line = active[_customLineIndex % active.Count];
            _customLineIndex = (_customLineIndex + 1) % active.Count;
            return "\U0001F4AD " + line.Text;
        }

        private static string FormatTime(TimeSpan ts) =>
            ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");

        private void UpdateSystemStats()
        {
#if WINDOWS
            try
            {
                if (_cpuCounter != null) _cpuPercent = _cpuCounter.NextValue();
                using var ram = new PerformanceCounter("Memory", "Available MBytes", true);
                float availMB = ram.NextValue();
                _ramUsedGB = (_ramTotalGB * 1024f - availMB) / 1024f;
            }
            catch { }

            if (StatGpu || StatVram) UpdateGpuStats();
#else
            // CPU via /proc/stat
            try
            {
                var statParts = File.ReadLines("/proc/stat").First()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // cpu user nice system idle iowait irq softirq steal ...
                long user   = long.Parse(statParts[1]);
                long nice   = long.Parse(statParts[2]);
                long system = long.Parse(statParts[3]);
                long idle   = long.Parse(statParts[4]);
                long iowait = long.Parse(statParts[5]);
                long irq    = long.Parse(statParts[6]);
                long softirq = long.Parse(statParts[7]);
                long total  = user + nice + system + idle + iowait + irq + softirq;
                long idleAll = idle + iowait;
                if (_prevCpuTotal > 0)
                {
                    long dt = total - _prevCpuTotal;
                    long di = idleAll - _prevCpuIdle;
                    _cpuPercent = dt > 0 ? (1f - (float)di / dt) * 100f : 0f;
                }
                _prevCpuTotal = total;
                _prevCpuIdle  = idleAll;
            }
            catch { }

            // RAM via /proc/meminfo
            try
            {
                long memTotalKB = 0, memAvailKB = 0;
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:"))
                        memTotalKB = long.Parse(line.Split(':')[1].Trim().Split(' ')[0]);
                    else if (line.StartsWith("MemAvailable:"))
                        memAvailKB = long.Parse(line.Split(':')[1].Trim().Split(' ')[0]);
                    if (memTotalKB > 0 && memAvailKB > 0) break;
                }
                _ramTotalGB = memTotalKB / (1024f * 1024f);
                _ramUsedGB  = (memTotalKB - memAvailKB) / (1024f * 1024f);
            }
            catch { }
#endif
        }

#if WINDOWS
        private void UpdateGpuStats()
        {
            try
            {
                if ((DateTime.UtcNow - _gpuCountersBuilt).TotalSeconds > 15) RebuildGpuCounters();

                float gpu = 0f;
                foreach (var c in _gpuCounters)
                {
                    try { gpu += c.NextValue(); } catch { }
                }
                _gpuPercent = Math.Min(gpu, 100f);

                double vramBytes = 0;
                foreach (var c in _vramCounters)
                {
                    try { vramBytes += c.NextValue(); } catch { }
                }
                _vramUsedGB = (float)(vramBytes / (1024.0 * 1024.0 * 1024.0));
            }
            catch { }
        }

        private void RebuildGpuCounters()
        {
            DisposeGpuCounters();
            _gpuCountersBuilt = DateTime.UtcNow;
            try
            {
                foreach (var inst in new PerformanceCounterCategory("GPU Engine").GetInstanceNames())
                {
                    if (inst.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    try
                    {
                        var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true);
                        c.NextValue();
                        _gpuCounters.Add(c);
                    }
                    catch { }
                }
            }
            catch (Exception ex) { _log($"[Chatbox] GPU counters unavailable: {ex.Message}"); }

            try
            {
                foreach (var inst in new PerformanceCounterCategory("GPU Adapter Memory").GetInstanceNames())
                {
                    try
                    {
                        var c = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", inst, true);
                        c.NextValue();
                        _vramCounters.Add(c);
                    }
                    catch { }
                }
            }
            catch { }

            _gpuAvailable = _gpuCounters.Count > 0 || _vramCounters.Count > 0;
            if (_vramTotalGB <= 0) _vramTotalGB = ReadTotalVramGB();
        }

        private float ReadTotalVramGB()
        {
            try
            {
                using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                if (cls == null) return 0f;
                long best = 0;
                foreach (var name in cls.GetSubKeyNames())
                {
                    if (name.Length != 4 || !name.All(char.IsDigit)) continue;
                    using var k = cls.OpenSubKey(name);
                    if (k == null) continue;
                    long bytes = 0;
                    var qw = k.GetValue("HardwareInformation.qwMemorySize");
                    if (qw is long l) bytes = l;
                    else if (qw is int i) bytes = (uint)i;
                    else if (qw is byte[] b && b.Length >= 8) bytes = BitConverter.ToInt64(b, 0);
                    if (bytes <= 0)
                    {
                        var ms = k.GetValue("HardwareInformation.MemorySize");
                        if (ms is int mi) bytes = (uint)mi;
                        else if (ms is byte[] mb && mb.Length >= 4) bytes = BitConverter.ToUInt32(mb, 0);
                    }
                    if (bytes > best) best = bytes;
                }
                return best > 0 ? (float)(best / (1024.0 * 1024.0 * 1024.0)) : 0f;
            }
            catch (Exception ex) { _log($"[Chatbox] VRAM total unavailable: {ex.Message}"); return 0f; }
        }

        private void DisposeGpuCounters()
        {
            foreach (var c in _gpuCounters)  { try { c.Dispose(); } catch { } }
            foreach (var c in _vramCounters) { try { c.Dispose(); } catch { } }
            _gpuCounters.Clear();
            _vramCounters.Clear();
        }
#endif

#if WINDOWS
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT p);

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
#endif

        /// <summary>
        /// Seconds since the mouse last moved and since the last non-mouse input. GetLastInputInfo
        /// covers every device, so a change without cursor movement is treated as keyboard input.
        /// </summary>
        private (int mouseIdle, int keyIdle) ReadIdleSeconds()
        {
#if WINDOWS
            var now = Environment.TickCount;

            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            var haveInput = GetLastInputInfo(ref lii);
            var haveCursor = GetCursorPos(out var cursor);

            if (!_idlePrimed)
            {
                _idlePrimed = true;
                _lastCursor = cursor;
                _lastInputTick = lii.dwTime;
                _lastMouseMoveTick = now;
                _lastKeyboardTick = now;
                return (0, 0);
            }

            var moved = haveCursor && (cursor.X != _lastCursor.X || cursor.Y != _lastCursor.Y);
            if (moved)
            {
                _lastCursor = cursor;
                _lastMouseMoveTick = now;
            }

            if (haveInput && lii.dwTime != _lastInputTick)
            {
                _lastInputTick = lii.dwTime;
                if (!moved) _lastKeyboardTick = now;
            }

            return ((now - _lastMouseMoveTick) / 1000, (now - _lastKeyboardTick) / 1000);
#else
            return (0, 0);
#endif
        }

        private void UpdateAfkState()
        {
#if WINDOWS
            try
            {
                bool focused = false;
                var hwnd = GetForegroundWindow();
                if (hwnd != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    try { focused = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant().Contains("vrchat"); }
                    catch { }
                }
                if (focused)
                {
                    _isAfk = false;
                    return;
                }

                var (mouseIdle, keyIdle) = ReadIdleSeconds();
                var idle = mouseIdle >= Math.Max(1, AfkMouseSeconds)
                        && keyIdle   >= Math.Max(1, AfkKeyboardSeconds);

                if (idle && !_isAfk) { _isAfk = true; _afkSince = DateTime.Now; }
                else if (!idle) _isAfk = false;
            }
            catch { _isAfk = false; }
#endif
        }

        private async Task UpdateMediaInfoAsync()
        {
#if WINDOWS
            try
            {
                var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync()
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                var sessions = mgr.GetSessions();
                _log($"[Chatbox/SMTC] {sessions.Count} session(s) found");
                foreach (var sess in sessions)
                    _log($"[Chatbox/SMTC]   app={sess.SourceAppUserModelId} status={sess.GetPlaybackInfo()?.PlaybackStatus}");
                var s = sessions.FirstOrDefault(sess =>
                            sess.GetPlaybackInfo()?.PlaybackStatus ==
                            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        ?? mgr.GetCurrentSession();
                if (s == null)
                {
                    _log("[Chatbox/SMTC] No session selected → no media");
                    IsPlaying = false; CurrentTitle = ""; CurrentArtist = ""; return;
                }
                _log($"[Chatbox/SMTC] Using session: {s.SourceAppUserModelId}");
                IsPlaying = s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var p = await s.TryGetMediaPropertiesAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                if (p != null) { CurrentTitle = p.Title ?? ""; CurrentArtist = p.Artist ?? ""; }
                _log($"[Chatbox/SMTC] title=\"{CurrentTitle}\" artist=\"{CurrentArtist}\" playing={IsPlaying}");
                var tl = s.GetTimelineProperties();
                if (tl != null)
                {
                    CurrentDuration = tl.EndTime - tl.StartTime;
                    var trackKey = $"{CurrentTitle}||{(long)CurrentDuration.TotalSeconds}";
                    if (trackKey != _smtcTrackKey)
                    {
                        _smtcTrackKey = trackKey;
                        _smtcLastReportedPos = tl.Position;
                        _smtcBasePos = tl.Position;
                        _smtcBaseTime = DateTimeOffset.Now;
                    }
                    else if (Math.Abs((tl.Position - _smtcLastReportedPos).TotalMilliseconds) > 500)
                    {
                        _smtcLastReportedPos = tl.Position;
                        _smtcBasePos = tl.Position;
                        _smtcBaseTime = DateTimeOffset.Now;
                    }
                    var pos = _smtcBasePos + (IsPlaying ? (DateTimeOffset.Now - _smtcBaseTime) : TimeSpan.Zero);
                    if (CurrentDuration > TimeSpan.Zero && pos > CurrentDuration) pos = CurrentDuration;
                    if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
                    CurrentPosition = pos;
                }
            }
            catch (Exception ex) { _log($"[Chatbox/SMTC] Exception: {ex.GetType().Name}: {ex.Message}"); IsPlaying = false; }
#else
            // MPRIS2 via playerctl — works on KDE Plasma, GNOME, and most Linux DEs
            try
            {
                // Single call: tab-separated status, title, artist, length(µs), position(µs)
                var raw = await RunProcessAsync("playerctl",
                    "--format={{status}}\t{{title}}\t{{artist}}\t{{mpris:length}}\t{{mpris:position}} metadata");
                var cols = raw.Trim().Split('\t');
                if (cols.Length < 2 || string.IsNullOrWhiteSpace(cols[0]))
                {
                    IsPlaying = false; CurrentTitle = ""; CurrentArtist = "";
                    return;
                }
                IsPlaying = cols[0].Trim() == "Playing";
                CurrentTitle  = cols.Length > 1 ? cols[1].Trim() : "";
                CurrentArtist = cols.Length > 2 ? cols[2].Trim() : "";
                if (cols.Length > 3 && long.TryParse(cols[3].Trim(), out long lenUs))
                    CurrentDuration = TimeSpan.FromMicroseconds(lenUs);
                if (cols.Length > 4 && long.TryParse(cols[4].Trim().Split(' ')[0], out long posUs))
                    CurrentPosition = TimeSpan.FromMicroseconds(posUs);
            }
            catch { IsPlaying = false; }
#endif
        }

#if !WINDOWS
        private static async Task<string> RunProcessAsync(string exe, string args)
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return output;
        }
#endif

        private void SendOscChatbox(string text, bool suppressSound = true)
        {
            if (_udp == null) return;
            try { var p = BuildOscMessage("/chatbox/input", text, true, !suppressSound); _udp.Send(p, p.Length); }
            catch (Exception ex) { _log($"[Chatbox] OSC send error: {ex.Message}"); }
        }

        private static byte[] BuildOscMessage(string address, string text, bool sendImmediate, bool notifySound)
        {
            var buf = new List<byte>();
            WriteOscString(buf, address);
            WriteOscString(buf, "," + "s" + (sendImmediate ? "T" : "F") + (notifySound ? "T" : "F"));
            WriteOscString(buf, text);
            return buf.ToArray();
        }

        private static void WriteOscString(List<byte> buf, string s)
        {
            var b = Encoding.UTF8.GetBytes(s); buf.AddRange(b);
            int pad = 4 - (b.Length % 4); if (pad == 0) pad = 4;
            for (int i = 0; i < pad; i++) buf.Add(0);
        }

        public void ApplyConfig(bool enabled, bool showTime, bool showMedia, bool showPlaytime,
            bool showCustomText, bool showSystemStats, bool showAfk, string afkMessage,
            bool suppressSound, string timeFormat, string separator,
            int intervalMs, List<CbCustomLine> customLines, bool hideBackground = false,
            string? customTemplate = null,
            List<string>? lineOrder = null, bool showAfkTime = true,
            bool statCpu = true, bool statRam = true, bool statGpu = false, bool statVram = false,
            bool showPulse = false, string? pulseFormat = null,
            bool showWindow = false, string? windowFormat = null,
            bool showWeather = false, string? weatherFormat = null,
            int afkMouseSeconds = 10, int afkKeyboardSeconds = 10)
        {
            var was = Enabled; Enabled = enabled;
            ShowPulse = showPulse;
            if (!string.IsNullOrWhiteSpace(pulseFormat)) PulseFormat = pulseFormat;
            ShowWindow = showWindow;
            if (!string.IsNullOrWhiteSpace(windowFormat)) WindowFormat = windowFormat;
            ShowWeather = showWeather;
            if (!string.IsNullOrWhiteSpace(weatherFormat)) WeatherFormat = weatherFormat;
            ShowTime = showTime; ShowMedia = showMedia; ShowPlaytime = showPlaytime;
            ShowCustomText = showCustomText; ShowSystemStats = showSystemStats;
            ShowAfk = showAfk; ShowAfkTime = showAfkTime;
            StatCpu = statCpu; StatRam = statRam; StatGpu = statGpu; StatVram = statVram;
            if (!string.IsNullOrWhiteSpace(afkMessage)) AfkMessage = afkMessage;
            AfkMouseSeconds = Math.Clamp(afkMouseSeconds, 1, 3600);
            AfkKeyboardSeconds = Math.Clamp(afkKeyboardSeconds, 1, 3600);
            SuppressNotifSound = suppressSound;
            if (!string.IsNullOrWhiteSpace(timeFormat)) TimeFormat = timeFormat;
            if (separator != null) Separator = separator;
            CustomTemplate = customTemplate ?? "";
            IntervalMs = Math.Max(intervalMs, MIN_INTERVAL_MS);
            CustomLines = customLines ?? new();
            LineOrder = (lineOrder != null && lineOrder.Count > 0) ? lineOrder : new List<string>(DefaultLineOrder);
            _customLineIndex = 0;
            HideChatboxBackground = hideBackground;
            if (enabled && !was) Start(); else if (!enabled && was) Stop();
        }

        public void SendDirect(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            bool ownUdp = _udp == null;
            if (ownUdp)
            {
                _udp = new UdpClient();
                _udp.Connect(IPAddress.Parse(OSC_IP), OSC_PORT);
            }
            SendOscChatbox(text, SuppressNotifSound);
            if (ownUdp) { _udp?.Close(); _udp = null; }
            else Interlocked.Exchange(ref _pauseUntilTick, Environment.TickCount64 + 10_000);
        }

        public void PauseDirectSend(int ms)
        {
            Interlocked.Exchange(ref _pauseUntilTick, Environment.TickCount64 + ms);
        }

        public void Dispose() { Stop(); _cts?.Dispose(); }
    }
}
