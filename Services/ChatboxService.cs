using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
        public bool ShowAfk { get; set; }
        public string AfkMessage { get; set; } = "Currently AFK";
        public bool SuppressNotifSound { get; set; } = true;
        public string TimeFormat { get; set; } = "hh:mm tt";
        public string Separator { get; set; } = " | ";
        public int IntervalMs { get; set; } = 5000;
        public List<string> CustomLines { get; set; } = new();
        private int _customLineIndex;

        // Media state
        public string CurrentTitle { get; private set; } = "";
        public string CurrentArtist { get; private set; } = "";
        public TimeSpan CurrentPosition { get; private set; }
        public TimeSpan CurrentDuration { get; private set; }
        public bool IsPlaying { get; private set; }

        // System stats
#if WINDOWS
        private PerformanceCounter? _cpuCounter;
#endif
        private float _cpuPercent;
        private float _ramUsedGB;
        private float _ramTotalGB;

        // AFK
        private bool _isAfk;
        private DateTime _afkSince;

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
                    if (Enabled && !string.IsNullOrEmpty(text))
                        SendOscChatbox(text, SuppressNotifSound);

                    _onUpdate?.Invoke(new {
                        currentTitle = CurrentTitle, currentArtist = CurrentArtist,
                        positionMs = (long)CurrentPosition.TotalMilliseconds,
                        durationMs = (long)CurrentDuration.TotalMilliseconds,
                        isPlaying = IsPlaying, chatboxText = text, enabled = Enabled,
                        cpuPercent = _cpuPercent, ramUsedGB = _ramUsedGB, ramTotalGB = _ramTotalGB,
                        isAfk = _isAfk,
                    });
                    await Task.Delay(Math.Max(IntervalMs, MIN_INTERVAL_MS), ct);
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex) { _log($"[Chatbox] Error: {ex.Message}"); await Task.Delay(2000, ct); }
            }
#if WINDOWS
            _cpuCounter?.Dispose(); _cpuCounter = null;
#endif
        }

        private string BuildChatboxText()
        {
            if (ShowAfk && _isAfk)
            {
                var d = DateTime.Now - _afkSince;
                var t = d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes}m" : $"{(int)d.TotalMinutes}m";
                var msg = $"{AfkMessage} ({t})";
                if (ShowTime) msg = DateTime.Now.ToString(TimeFormat) + Separator + msg;
                return msg.Length > MAX_CHATBOX_CHARS ? msg[..MAX_CHATBOX_CHARS] : msg;
            }

            var parts = new List<string>();
            if (ShowTime) parts.Add(DateTime.Now.ToString(TimeFormat));

            if (ShowMedia && IsPlaying && !string.IsNullOrEmpty(CurrentTitle))
            {
                var m = $"\"{CurrentTitle}\"";
                if (!string.IsNullOrEmpty(CurrentArtist)) m += $" by {CurrentArtist}";
                if (ShowPlaytime && CurrentDuration.TotalSeconds > 0)
                    m += $" [{FormatTime(CurrentPosition)}/{FormatTime(CurrentDuration)}]";
                parts.Add(m);
            }

            if (ShowSystemStats)
            {
                var s = $"CPU {_cpuPercent:0}%";
                if (_ramTotalGB > 0) s += $" RAM {_ramUsedGB:0.0}/{_ramTotalGB:0.0}GB";
                parts.Add(s);
            }

            if (ShowCustomText && CustomLines.Count > 0)
            {
                var line = CustomLines[_customLineIndex % CustomLines.Count];
                if (!string.IsNullOrWhiteSpace(line)) parts.Add(line);
                _customLineIndex++;
            }

            var result = string.Join(Separator, parts);
            return result.Length > MAX_CHATBOX_CHARS ? result[..MAX_CHATBOX_CHARS] : result;
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
                if (!focused && !_isAfk) { _isAfk = true; _afkSince = DateTime.Now; }
                else if (focused) _isAfk = false;
            }
            catch { _isAfk = false; }
#endif
        }

        private Task UpdateMediaInfoAsync()
        {
            // Windows Media Control (WinRT) not available on Linux
            IsPlaying = false;
            return Task.CompletedTask;
        }

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
            int intervalMs, List<string> customLines)
        {
            var was = Enabled; Enabled = enabled;
            ShowTime = showTime; ShowMedia = showMedia; ShowPlaytime = showPlaytime;
            ShowCustomText = showCustomText; ShowSystemStats = showSystemStats;
            ShowAfk = showAfk;
            if (!string.IsNullOrWhiteSpace(afkMessage)) AfkMessage = afkMessage;
            SuppressNotifSound = suppressSound;
            if (!string.IsNullOrWhiteSpace(timeFormat)) TimeFormat = timeFormat;
            if (separator != null) Separator = separator;
            IntervalMs = Math.Max(intervalMs, MIN_INTERVAL_MS);
            CustomLines = customLines ?? new();
            if (enabled && !was) Start(); else if (!enabled && was) Stop();
        }

        public void Dispose() { Stop(); _cts?.Dispose(); }
    }
}
