using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace VRCNext;

// Owns all Avatar Scaling state, logic, and message handling.
// Uses SO_REUSEADDR to co-bind port 9001 alongside OscService and receive /avatar/eyeheight
// feedback from VRChat so scale stays in sync with radial-menu changes.
// On Windows, installs a global low-level keyboard hook for the scale-up/down keybinds.

public class AvatarScalingController : IDisposable
{
    private readonly CoreLibrary _core;

    // OSC UDP clients
    private UdpClient? _sender;                     // send-only → port 9000
    private UdpClient? _receiver;                   // receive from VRChat on port 9001
    private CancellationTokenSource? _receiveCts;
    private bool _connected;
    private const int VRC_SEND_PORT    = 9000;
    private const int VRC_RECEIVE_PORT = 9001;

    private const float SafetyMin = 0.1f;
    private const float SafetyMax = 100f;

    // Current scale state
    private float _currentScale;
    private bool _useSafety;
    private bool _saveScale;
    private float _smoothing;
    private int _keyUp;
    private int _keyDown;

    // Scale tick timer (fires while key is held)
    private System.Threading.Timer? _scaleTimer;
    private volatile int _scaleDir; // -1, 0, +1
    private bool _keyUpHeld;
    private bool _keyDownHeld;

    // Periodic resend timer (when saveScale is enabled)
    private System.Threading.Timer? _resendTimer;

#if WINDOWS
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP   = 0x0101;
    private const uint WM_QUIT   = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    private delegate IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, KeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private KeyboardProc? _hookProc; 
    private IntPtr _hookId = IntPtr.Zero;
    private Thread? _hookThread;
    private uint _hookThreadId;
#endif

    public AvatarScalingController(CoreLibrary core)
    {
        _core = core;
        _currentScale = core.Settings.AsScale;
        _useSafety    = core.Settings.AsUseSafetySettings;
        _saveScale    = core.Settings.AsSaveScaleBetweenWorlds;
        _keyUp        = core.Settings.AsKeyUp;
        _keyDown      = core.Settings.AsKeyDown;
        _smoothing    = core.Settings.AsSmoothing;
    }

    public void HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "asConnect":
                Connect();
                break;

            case "asDisconnect":
                Disconnect();
                break;

            case "asSaveSettings":
                ApplySettings(msg);
                break;

            case "asSetScale":
            {
                var v = msg["value"]?.Value<float>() ?? _currentScale;
                SetScale(v);
                break;
            }

            case "asRecordKey":
            {
                var slot    = msg["slot"]?.ToString() ?? "";
                var keyCode = msg["keyCode"]?.Value<int>() ?? 0;
                if (slot == "up")
                {
                    _keyUp = keyCode;
                    _core.Settings.AsKeyUp = keyCode;
                }
                else if (slot == "down")
                {
                    _keyDown = keyCode;
                    _core.Settings.AsKeyDown = keyCode;
                }
                _core.Settings.Save();
                break;
            }
        }
    }

// Connect

    private void Connect()
    {
        try
        {
            _sender = new UdpClient();
            _sender.Connect("127.0.0.1", VRC_SEND_PORT);
            _connected = true;

#if WINDOWS
            InstallHook();
#endif
            SendAllParams();
            StartResendTimerIfNeeded();
        }
        catch (Exception ex)
        {
            _core.SendToJS("log", new { msg = $"[AvatarScaling] Connect error: {ex.Message}", color = "err" });
            _connected = false;
        }

        StartReceiver();
        SendState();
    }

    private void Disconnect()
    {
#if WINDOWS
        UninstallHook();
#endif
        StopScaleTimer();
        StopResendTimer();
        StopReceiver();
        _sender?.Close();
        _sender = null;
        _connected = false;
        SendState();
    }

// Feeder VSC

    private void StartReceiver()
    {
        StopReceiver();
        try
        {
            _receiveCts = new CancellationTokenSource();
            var client = new UdpClient();
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(IPAddress.Any, VRC_RECEIVE_PORT));
            _receiver = client;
            _ = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
        }
        catch (Exception ex)
        {
            _core.SendToJS("log", new { msg = $"[AvatarScaling] Receiver bind error: {ex.Message}", color = "warn" });
        }
    }

    private void StopReceiver()
    {
        _receiveCts?.Cancel();
        _receiveCts?.Dispose();
        _receiveCts = null;
        _receiver?.Close();
        _receiver = null;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var client = _receiver;
        if (client == null) return;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(ct);
                ParseEyeHeightPacket(result.Buffer);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _core.SendToJS("log", new { msg = $"[AvatarScaling] Receiver error: {ex.Message}", color = "warn" });
        }
    }

    private void ParseEyeHeightPacket(byte[] data)
    {
        try
        {
            int pos = 0;
            string address = ReadOscString(data, ref pos);
            if (address != "/avatar/eyeheight") return;
            string typeTag = ReadOscString(data, ref pos);
            if (typeTag.Length < 2 || typeTag[0] != ',' || typeTag[1] != 'f') return;
            if (pos + 4 > data.Length) return;
            float value = ReadBigEndianFloat(data, ref pos);
            UpdateFromVrc(value);
        }
        catch { }
    }

    private void UpdateFromVrc(float value)
    {
        var min = _useSafety ? SafetyMin : 0.01f;
        var max = _useSafety ? SafetyMax : 10000f;
        var clamped = Math.Clamp(value, min, max);
        if (MathF.Abs(clamped - _currentScale) < 0.0001f) return; // no change
        _currentScale = clamped;
        _core.Settings.AsScale = _currentScale;
        SendState();
#if WINDOWS
        _core.VrOverlay?.VroScaleUpdate(_currentScale);
#endif
    }

    private static string ReadOscString(byte[] data, ref int pos)
    {
        int start = pos;
        while (pos < data.Length && data[pos] != 0) pos++;
        string s = Encoding.UTF8.GetString(data, start, pos - start);
        pos++;
        int pad = 4 - (pos % 4);
        if (pad < 4) pos += pad;
        return s;
    }

    private static float ReadBigEndianFloat(byte[] data, ref int pos)
    {
        byte[] b = { data[pos + 3], data[pos + 2], data[pos + 1], data[pos] };
        pos += 4;
        return BitConverter.ToSingle(b, 0);
    }

// Setting

    private void ApplySettings(JObject msg)
    {
        _useSafety  = msg["useSafety"]?.Value<bool>()  ?? _useSafety;
        _saveScale  = msg["saveScale"]?.Value<bool>()  ?? _saveScale;
        _keyUp      = msg["keyUp"]?.Value<int>()       ?? _keyUp;
        _keyDown    = msg["keyDown"]?.Value<int>()     ?? _keyDown;
        _smoothing  = msg["smoothing"]?.Value<float>() ?? _smoothing;

        var newScale = msg["scale"]?.Value<float>();
        if (newScale.HasValue) SetScale(newScale.Value);

        _core.Settings.AsAutoStartVR            = msg["autoStartVR"]?.Value<bool>()      ?? _core.Settings.AsAutoStartVR;
        _core.Settings.AsAutoStartDesktop       = msg["autoStartDesktop"]?.Value<bool>() ?? _core.Settings.AsAutoStartDesktop;
        _core.Settings.AsUseSafetySettings      = _useSafety;
        _core.Settings.AsSaveScaleBetweenWorlds = _saveScale;
        _core.Settings.AsKeyUp                  = _keyUp;
        _core.Settings.AsKeyDown                = _keyDown;
        _core.Settings.AsSmoothing              = _smoothing;
        _core.Settings.Save();

        StartResendTimerIfNeeded();
    }

// Scale Logic

    private void SetScale(float value)
    {
        var min = _useSafety ? SafetyMin : 0.01f;
        var max = _useSafety ? SafetyMax : 10000f;
        _currentScale = Math.Clamp(value, min, max);
        _core.Settings.AsScale = _currentScale;

        if (_connected) SendAllParams();
        SendState();
        // Push updated scale back to VR overlay if open
#if WINDOWS
        _core.VrOverlay?.VroScaleUpdate(_currentScale);
#endif
    }

    public void ApplyVrScaleDelta(float delta)
    {
        SetScale(_currentScale + delta);
    }

    private void StartScaleTimer(int dir)
    {
        _scaleDir = dir;
        if (_scaleTimer != null) return;
        _scaleTimer = new System.Threading.Timer(OnScaleTick, null, 0, 50);
    }

    private void StopScaleTimer()
    {
        _scaleDir = 0;
        _keyUpHeld = false;
        _keyDownHeld = false;
        _scaleTimer?.Dispose();
        _scaleTimer = null;
    }

    private void OnScaleTick(object? state)
    {
        if (_scaleDir == 0 || !_connected) return;

        // smoothing 0 = fast (large step), smoothing 100 = slow (small step)
        float t = Math.Clamp(_smoothing / 100f, 0f, 1f);
        float step = (0.05f * (1f - t) + 0.001f * t) * _scaleDir;
        var min = _useSafety ? SafetyMin : 0.01f;
        var max = _useSafety ? SafetyMax : 10000f;
        _currentScale = Math.Clamp(_currentScale + step, min, max);
        _core.Settings.AsScale = _currentScale;

        SendAllParams();
        try { Invoke(() => _core.SendToJS("asState", BuildState())); } catch { }
    }

// Resend timer to try to save between worlds

    private void StartResendTimerIfNeeded()
    {
        StopResendTimer();
        if (_connected && _saveScale)
            _resendTimer = new System.Threading.Timer(_ => { if (_connected) SendAllParams(); }, null, 5000, 5000);
    }

    private void StopResendTimer()
    {
        _resendTimer?.Dispose();
        _resendTimer = null;
    }

// OSC Sender

    private void SendAllParams()
    {
        if (_sender == null) return;
        var min = _useSafety ? SafetyMin : 0.01f;
        var max = _useSafety ? SafetyMax : 10000f;
        SendRawBool("/avatar/eyeheightscalingallowed", true);
        SendRawFloat("/avatar/eyeheight",    _currentScale);
        SendRawFloat("/avatar/eyeheightmin", min);
        SendRawFloat("/avatar/eyeheightmax", max);
    }

    private void SendRawFloat(string address, float value)
    {
        if (_sender == null) return;
        try
        {
            var buf = new List<byte>();
            WriteOscString(buf, address);
            WriteOscString(buf, ",f");
            var fb = BitConverter.GetBytes(value);
            buf.Add(fb[3]); buf.Add(fb[2]); buf.Add(fb[1]); buf.Add(fb[0]);
            var p = buf.ToArray();
            _sender.Send(p, p.Length);
        }
        catch { }
    }

    private void SendRawBool(string address, bool value)
    {
        if (_sender == null) return;
        try
        {
            var buf = new List<byte>();
            WriteOscString(buf, address);
            WriteOscString(buf, value ? ",T" : ",F");
            var p = buf.ToArray();
            _sender.Send(p, p.Length);
        }
        catch { }
    }

    private static void WriteOscString(List<byte> buf, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        buf.AddRange(b);
        int pad = 4 - (b.Length % 4);
        if (pad == 0) pad = 4;
        for (int i = 0; i < pad; i++) buf.Add(0);
    }

// Initi Broadcast

    private object BuildState() => new
    {
        connected  = _connected,
        scale      = _currentScale,
        keyUp      = _keyUp,
        keyDown    = _keyDown,
    };

    private void SendState() => _core.SendToJS("asState", BuildState());

#if WINDOWS
// Key Hook

    private void InstallHook()
    {
        if (_hookId != IntPtr.Zero) return;

        var ready = new System.Threading.ManualResetEventSlim(false);

        _hookThread = new Thread(() =>
        {
            _hookThreadId = GetCurrentThreadId();
            _hookProc = HookCallback;
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
            ready.Set();

            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        });
        _hookThread.IsBackground = true;
        _hookThread.Name = "AsScaleHook";
        _hookThread.Start();

        ready.Wait(TimeSpan.FromSeconds(2));
    }

    private void UninstallHook()
    {
        if (_hookThreadId != 0)
            PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);

        _hookThread?.Join(TimeSpan.FromSeconds(2));
        _hookThread   = null;
        _hookThreadId = 0;
        _hookId       = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (_keyUp != 0 || _keyDown != 0))
        {
            var ks = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var vk = (int)ks.vkCode;

            if (wParam == (IntPtr)WM_KEYDOWN)
            {
                if (vk == _keyUp && !_keyUpHeld)
                {
                    _keyUpHeld = true;
                    StartScaleTimer(1);
                }
                else if (vk == _keyDown && !_keyDownHeld)
                {
                    _keyDownHeld = true;
                    StartScaleTimer(-1);
                }
            }
            else if (wParam == (IntPtr)WM_KEYUP)
            {
                if (vk == _keyUp)
                {
                    _keyUpHeld = false;
                    if (!_keyDownHeld) StopScaleTimer();
                    else { _scaleDir = -1; }
                }
                else if (vk == _keyDown)
                {
                    _keyDownHeld = false;
                    if (!_keyUpHeld) StopScaleTimer();
                    else { _scaleDir = 1; }
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }
#endif

    private static void Invoke(Action action) => action();

    public void Dispose()
    {
        Disconnect();
    }
}
