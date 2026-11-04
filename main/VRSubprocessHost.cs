#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace VRCNext;

/// <summary>
/// Manages the VR subprocess (VRCNext.exe --vr-subprocess).
/// VROverlayService + SteamVRService run inside that subprocess, isolated from VRCNext.exe.
/// If SteamVR hard-crashes (native AV), only the subprocess dies; VRCNext.exe survives.
/// </summary>
public sealed class VRSubprocessHost : IDisposable
{
    private readonly Action<string> _log;
    private Process? _process;
    private StreamWriter? _stdin;
    private readonly object _stdinLock = new();
    private CancellationTokenSource? _readCts;
    private bool _disposed;

    public bool VroConnected { get; private set; }
    public bool SfConnected  { get; private set; }
    public bool StConnected  { get; private set; }
    public bool FsConnected  { get; private set; }

    public bool AnyConnected => VroConnected || SfConnected || StConnected || FsConnected;

    public int InputMode { get; set; }
    private int _runningInputMode = -1;

    // Events fired when the subprocess sends a message over stdout.
    public event Action<JObject>? OnVroState;
    public event Action<List<uint>, List<string>, int, int>? OnVroKeybindRecorded;
    public event Action<string, string>? OnVroJoinRequest;
    public event Action<string>? OnVroInviteFriend;
    public event Action<string, string, string, string>? OnVroNotifAccept;
    public event Action<int>? OnVroToolToggle;
    public event Action? OnVroToastSound;
    public event Action? OnVroWaterAlarm;
    public event Action? OnVroWaterDismissed;
    public event Action<float>? OnVroScaleChange;
    public event Action<List<uint>, List<string>, int>? OnVroScaleKeybindRecorded;
    public event Action? OnVroQuit;
    public event Action<JObject>? OnSfUpdate;
    public event Action? OnSfQuit;
    public event Action<JObject>? OnStUpdate;
    public event Action? OnStQuit;
    public event Action<JObject>? OnFsUpdate;
    public event Action? OnFsQuit;
    public event Action<JArray>? OnFsDevices;
    public event Action<string>? OnFsPhotoSaved;
    public event Action<string>? OnFsQrLink;

    public VRSubprocessHost(Action<string> log) => _log = log;

    /// <summary>Starts the subprocess if it isn't already running, then sends the init message.</summary>
    public void EnsureRunning(string cacheDir, int httpPort, string? authCookie, string? tfaCookie)
    {
        if (_process is { HasExited: false }) return;
        if (_process != null)
        {
            _process.Dispose();
            _process = null;
        }

        _readCts?.Cancel();
        _readCts = new CancellationTokenSource();

        var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
        var psi = new ProcessStartInfo(exe!, "--vr-subprocess")
        {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = false,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            StandardInputEncoding  = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
        };

        _process = Process.Start(psi)!;
        _stdin   = _process.StandardInput;
        _stdin.AutoFlush = true;

        _process.EnableRaisingEvents = true;
        _process.Exited += OnProcessExited;

        _ = ReadLoopAsync(_process.StandardOutput, _readCts.Token);

        SendRaw(new JObject
        {
            ["t"]          = "init",
            ["cacheDir"]   = cacheDir,
            ["httpPort"]   = httpPort,
            ["authCookie"] = authCookie,
            ["tfaCookie"]  = tfaCookie,
            ["inputMode"]  = InputMode,
        });

        _runningInputMode = InputMode;
        _log($"[VRSub] Subprocess started (input mode {InputMode})");
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        _log("[VRSub] Subprocess exited");
        bool wasVro = VroConnected;
        bool wasSf  = SfConnected;
        bool wasSt  = StConnected;
        bool wasFs  = FsConnected;
        VroConnected = false;
        SfConnected  = false;
        StConnected  = false;
        FsConnected  = false;
        lock (_stdinLock) _stdin = null;
        if (wasVro) OnVroQuit?.Invoke();
        if (wasSf)  OnSfQuit?.Invoke();
        if (wasSt)  OnStQuit?.Invoke();
        if (wasFs)  OnFsQuit?.Invoke();
    }

    private void Kill()
    {
        if (_process != null)
        {
            _process.Exited -= OnProcessExited; // prevent spurious Exited event
            try { _process.Kill(); } catch { }
            _process = null;
        }
        lock (_stdinLock) _stdin = null;
    }

    private void SendRaw(JObject obj)
    {
        try
        {
            lock (_stdinLock)
                _stdin?.WriteLine(obj.ToString(Newtonsoft.Json.Formatting.None));
        }
        catch (Exception ex) { _log($"[VRSub] Send failed: {ex.Message}"); }
    }

    private void Send(string t, object? payload = null)
    {
        var obj = payload != null ? JObject.FromObject(payload) : new JObject();
        obj["t"] = t;
        SendRaw(obj);
    }

    private async Task ReadLoopAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break; // EOF
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { Dispatch(JObject.Parse(line)); }
                catch (Exception ex) { _log($"[VRSub] Parse error: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"[VRSub] Reader crashed: {ex.Message}"); }
    }

    private void Dispatch(JObject msg)
    {
        var t = msg["t"]?.Value<string>() ?? "";
        msg.Remove("t");

        switch (t)
        {
            case "log":
                _log(msg["text"]?.Value<string>() ?? "");
                break;
            case "vro_state":
                VroConnected = msg["connected"]?.Value<bool>() ?? VroConnected;
                OnVroState?.Invoke(msg);
                break;
            case "vro_keybind_recorded":
                OnVroKeybindRecorded?.Invoke(
                    msg["ids"]?.ToObject<List<uint>>()   ?? new(),
                    msg["names"]?.ToObject<List<string>>() ?? new(),
                    msg["hand"]?.Value<int>()  ?? 0,
                    msg["mode"]?.Value<int>()  ?? 0);
                break;
            case "vro_join_request":
                OnVroJoinRequest?.Invoke(
                    msg["friendId"]?.Value<string>()  ?? "",
                    msg["location"]?.Value<string>()  ?? "");
                break;
            case "vro_invite_friend":
                OnVroInviteFriend?.Invoke(msg["friendId"]?.Value<string>() ?? "");
                break;
            case "vro_notif_accept":
                OnVroNotifAccept?.Invoke(
                    msg["notifId"]?.Value<string>()   ?? "",
                    msg["notifType"]?.Value<string>() ?? "",
                    msg["senderId"]?.Value<string>()  ?? "",
                    msg["notifData"]?.Value<string>() ?? "");
                break;
            case "vro_tool_toggle":
                OnVroToolToggle?.Invoke(msg["index"]?.Value<int>() ?? 0);
                break;
            case "vro_toast_sound":
                OnVroToastSound?.Invoke();
                break;
            case "vro_water_alarm":
                OnVroWaterAlarm?.Invoke();
                break;
            case "vro_water_dismissed":
                OnVroWaterDismissed?.Invoke();
                break;
            case "vro_scale_change":
                OnVroScaleChange?.Invoke(msg["delta"]?.Value<float>() ?? 0f);
                break;
            case "vro_scale_keybind_recorded":
                OnVroScaleKeybindRecorded?.Invoke(
                    msg["ids"]?.ToObject<List<uint>>()   ?? new(),
                    msg["names"]?.ToObject<List<string>>() ?? new(),
                    msg["hand"]?.Value<int>()  ?? 0);
                break;
            case "sf_update":
                OnSfUpdate?.Invoke(msg);
                break;
            case "st_update":
                OnStUpdate?.Invoke(msg);
                break;
            case "fs_update":
                OnFsUpdate?.Invoke(msg);
                break;
            case "fs_devices":
                OnFsDevices?.Invoke(msg["devices"] as JArray ?? new JArray());
                break;
            case "fs_photo_saved":
                OnFsPhotoSaved?.Invoke(msg["path"]?.Value<string>() ?? "");
                break;
            case "fs_qr_link":
                OnFsQrLink?.Invoke(msg["url"]?.Value<string>() ?? "");
                break;
            case "fs_audio_devices":
            {
                var list = new List<(string, string)>();
                if (msg["devices"] is JArray arr)
                    foreach (var item in arr.OfType<JObject>())
                        list.Add((item["id"]?.Value<string>() ?? "", item["label"]?.Value<string>() ?? ""));
                OnFsAudioDevices?.Invoke(list);
                break;
            }
        }
    }

    /// <summary>
    /// Applies a changed VR input mode to the already running subprocess.
    /// Switching to SteamVR Input works live. Switching back to legacy cannot,
    /// because SetActionManifestPath disables GetControllerState for the whole
    /// process, so the subprocess is restarted instead.
    /// </summary>
    public void ApplyInputMode(int mode)
    {
        InputMode = mode;
        if (_process is not { HasExited: false }) return;
        if (mode == _runningInputMode) return;

        if (mode == 1)
        {
            _runningInputMode = 1;
            Send("vr_input_mode", new { mode });
            _log("[VRSub] Switched to SteamVR Input");
            return;
        }

        _log("[VRSub] Switching back to legacy input, restarting subprocess");
        bool wasVro = VroConnected, wasSf = SfConnected, wasSt = StConnected, wasFs = FsConnected;
        Kill();
        _runningInputMode = -1;
        VroConnected = false;
        SfConnected  = false;
        StConnected  = false;
        FsConnected  = false;
        if (wasVro) OnVroQuit?.Invoke();
        if (wasSf)  OnSfQuit?.Invoke();
        if (wasSt)  OnStQuit?.Invoke();
        if (wasFs)  OnFsQuit?.Invoke();
    }

    public void VroConnect()
    {
        VroConnected = true;
        Send("vro_connect");
    }

    public void VroDisconnect()
    {
        VroConnected = false;
        Send("vro_disconnect");
        if (!SfConnected && !StConnected && !FsConnected) Kill();
    }

    public void VroShow()            => Send("vro_show");
    public void VroHide()            => Send("vro_hide");
    public void VroRecordKeybind()   => Send("vro_record_keybind");
    public void VroCancelRecording() => Send("vro_cancel_recording");

    public void VroConfig(bool attachLeft, bool attachHand,
        float px, float py, float pz, float rx, float ry, float rz, float width,
        List<uint> keybind, int keybindHand, int keybindMode,
        List<uint> keybindDt, int keybindDtHand, float controlRadius,
        bool dynVis, float focusRadius, bool seamless)
        => Send("vro_config", new { attachLeft, attachHand, px, py, pz, rx, ry, rz, width,
            keybind, keybindHand, keybindMode, keybindDt, keybindDtHand, controlRadius,
            dynVis, focusRadius, seamless });

    public void VroApplyToastConfig(bool enabled, bool favOnly, int size, float offX, float offY,
        bool online, bool offline, bool gps, bool status, bool statusDesc, bool bio,
        int durationSec, int stackSize, bool friendReq, bool invite, bool groupInv, bool joined,
        bool left, bool reqInvite)
        => Send("vro_toast_config", new { enabled, favOnly, size, offX, offY,
            online, offline, gps, status, statusDesc, bio, durationSec, stackSize,
            friendReq, invite, groupInv, joined, left, reqInvite });

    public void VroThemeColors(Dictionary<string, string> colors)
        => Send("vro_theme_colors", new { colors });

    public void VroWaterConfig(bool enabled, int intervalSec)
        => Send("vro_water_config", new { enabled, intervalSec });

    public void VroScaleConfig(bool scaleEnabled, bool leftThumb, bool rightThumb, List<uint> keybind, int keybindHand, float currentScale, int scrollSensitivity = 25)
        => Send("vro_scale_config", new { scaleEnabled, leftThumb, rightThumb, keybind, keybindHand, currentScale, scrollSensitivity });

    public void VroScaleUpdate(float scale)
        => Send("vro_scale_update", new { scale });

    public void VroRecordScaleKeybind()
        => Send("vro_record_scale_keybind");

    public void VroCancelScaleRecording()
        => Send("vro_cancel_scale_recording");

    public void VroSetLanguage(string lang)
        => Send("vro_set_language", new { lang });

    // These match VROverlayService's public API so callers on _core.VrOverlay compile unchanged.
    public void AddNotification(string evType, string friendName, string evText, string time,
        string imageUrl = "", string friendId = "", string location = "", string notifId = "", string notifData = "")
        => Send("vro_add_notif", new { evType, friendName, evText, time, imageUrl, friendId, location, notifId, notifData });

    public void UpdateNotification(string notifId, string? newText = null, string? newImageUrl = null, string? newFriendName = null)
        => Send("vro_update_notif", new { notifId, newText, newImageUrl, newFriendName });

    public void EnqueueToast(string evType, string friendName, string evText, string time,
        string imageUrl, bool isFavorited, string friendId = "")
        => Send("vro_enqueue_toast", new { evType, friendName, evText, time, imageUrl, isFavorited, friendId });

    public void SetFriendLocations(IReadOnlyList<(string worldId, string instanceId, string worldName,
        string worldImageUrl, string friendId, string friendName, string friendImageUrl, string location)> entries)
    {
        var list = entries.Select(e => new {
            e.worldId, e.instanceId, e.worldName, e.worldImageUrl,
            e.friendId, e.friendName, e.friendImageUrl, e.location
        }).ToList();
        Send("vro_set_locations", new { entries = list });
    }

    public void SetOnlineFriends(IReadOnlyList<(string friendId, string friendName,
        string friendImageUrl, string status, string statusDescription, string location, string worldName)> entries)
    {
        var list = entries.Select(e => new {
            e.friendId, e.friendName, e.friendImageUrl,
            e.status, e.statusDescription, e.location, e.worldName
        }).ToList();
        Send("vro_set_online_friends", new { entries = list });
    }

    public void SetSelfUser(string userId, string imageUrl, string status)
        => Send("vro_set_self", new { userId, imageUrl, status });

    public void UpdateMediaInfo(string title, string artist, double position, double duration, bool playing)
        => Send("vro_update_media", new { title, artist, position, duration, playing });

    public void SetToolStates(bool discord, bool voice, bool kikitan, bool space, bool relay, bool chatbox, bool frameShot, bool spaceTurn = false)
        => Send("vro_tool_states", new { discord, voice, kikitan, space, relay, chatbox, frameShot, spaceTurn });

    public void SetKikitanState(string sourceText, string translatedText, bool isFinal,
        string sourceLang, string targetLang, string engine, bool translateEnabled)
        => Send("vro_kikitan", new { sourceText, translatedText, isFinal, sourceLang, targetLang, engine, translateEnabled });

    public void SfConnect(float multiplier, bool lockX, bool lockY, bool lockZ,
        uint leftResetBtn, uint rightResetBtn, uint leftDragBtn, uint rightDragBtn,
        uint leftGravityBtn, uint rightGravityBtn, float gravity)
    {
        SfConnected = true;
        Send("sf_connect", new { multiplier, lockX, lockY, lockZ, leftResetBtn, rightResetBtn, leftDragBtn, rightDragBtn, leftGravityBtn, rightGravityBtn, gravity });
    }

    public void SfDisconnect()
    {
        SfConnected = false;
        Send("sf_disconnect");
        if (!VroConnected && !StConnected && !FsConnected) Kill();
    }

    public void SfConfig(float multiplier, bool lockX, bool lockY, bool lockZ,
        uint leftResetBtn, uint rightResetBtn, uint leftDragBtn, uint rightDragBtn,
        uint leftGravityBtn, uint rightGravityBtn, float gravity)
        => Send("sf_config", new { multiplier, lockX, lockY, lockZ, leftResetBtn, rightResetBtn, leftDragBtn, rightDragBtn, leftGravityBtn, rightGravityBtn, gravity });

    public void SfReset() => Send("sf_reset");

    public void StConnect(float multiplier, float snapDegrees, bool invert, float smoothing,
        uint leftTurnBtn, uint rightTurnBtn, uint leftResetBtn, uint rightResetBtn)
    {
        StConnected = true;
        Send("st_connect", new { multiplier, snapDegrees, invert, smoothing, leftTurnBtn, rightTurnBtn, leftResetBtn, rightResetBtn });
    }

    public void StDisconnect()
    {
        StConnected = false;
        Send("st_disconnect");
        if (!VroConnected && !SfConnected && !FsConnected) Kill();
    }

    public void StConfig(float multiplier, float snapDegrees, bool invert, float smoothing,
        uint leftTurnBtn, uint rightTurnBtn, uint leftResetBtn, uint rightResetBtn)
        => Send("st_config", new { multiplier, snapDegrees, invert, smoothing, leftTurnBtn, rightTurnBtn, leftResetBtn, rightResetBtn });

    public void StReset() => Send("st_reset");

    public void FsConnect(uint leftButton, uint rightButton, string outputDeviceId, string outputDeviceName, int activationRadius,
                          uint leftRecordButton, uint rightRecordButton,
                          int gifMaxDim, int gifFps, bool useHmdRotations,
                          uint leftVideoButton, uint rightVideoButton,
                          string videoDeviceA, string videoDeviceB,
                          int videoFps, string videoQuality, string videoBitrateQuality, int audioKbps,
                          uint leftAcceptButton = 0, uint rightAcceptButton = 0)
    {
        FsConnected = true;
        Send("fs_connect", new { leftButton, rightButton, outputDeviceId, outputDeviceName, activationRadius, leftRecordButton, rightRecordButton, gifMaxDim, gifFps, useHmdRotations, leftVideoButton, rightVideoButton, videoDeviceA, videoDeviceB, videoFps, videoQuality, videoBitrateQuality, audioKbps, leftAcceptButton, rightAcceptButton });
    }

    public void FsDisconnect()
    {
        FsConnected = false;
        Send("fs_disconnect");
        if (!VroConnected && !SfConnected && !StConnected) Kill();
    }

    public void FsConfig(uint leftButton, uint rightButton, int activationRadius,
                         uint leftRecordButton, uint rightRecordButton,
                         int gifMaxDim, int gifFps, bool useHmdRotations,
                         uint leftVideoButton, uint rightVideoButton,
                         string videoDeviceA, string videoDeviceB,
                         int videoFps, string videoQuality, string videoBitrateQuality, int audioKbps,
                         uint leftAcceptButton = 0, uint rightAcceptButton = 0)
        => Send("fs_config", new { leftButton, rightButton, activationRadius, leftRecordButton, rightRecordButton, gifMaxDim, gifFps, useHmdRotations, leftVideoButton, rightVideoButton, videoDeviceA, videoDeviceB, videoFps, videoQuality, videoBitrateQuality, audioKbps, leftAcceptButton, rightAcceptButton });

    public void FsGetAudioDevices() => Send("fs_get_audio_devices");
    public event Action<List<(string id, string label)>>? OnFsAudioDevices;

    public void FsSetOutput(string deviceId, string deviceName)
        => Send("fs_set_output", new { deviceId, deviceName });

    public void FsGetDevices()
        => Send("fs_get_devices");

    public void TrimMemory() => Send("trim");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _readCts?.Cancel();
        Kill();
        _readCts?.Dispose();
    }
}
#else
using System.Diagnostics;
using System.Text;
using Newtonsoft.Json.Linq;
using VRCNext.Services.Helpers;

namespace VRCNext;

public sealed class VRSubprocessHost : IDisposable
{
    private readonly Action<string> _log;
    private readonly object _stdinLock = new();
    private Process? _process;
    private StreamWriter? _stdin;

    public bool VroConnected { get; private set; }
    public event Action<JObject>? OnVroState;
    public event Action? OnVroQuit;
    public bool SfConnected  { get; private set; }
    public bool StConnected  { get; private set; }
    public bool FsConnected  { get; private set; }
    public bool AnyConnected => VroConnected || SfConnected || StConnected || FsConnected;

    public int InputMode { get; set; }
    public void ApplyInputMode(int mode)
    {
        InputMode = mode;
        Send(new JObject { ["t"] = "vr_input_mode", ["mode"] = mode });
    }

    public event Action<JObject>? OnSfUpdate;
    public event Action? OnSfQuit;
    public event Action<JObject>? OnStUpdate;
    public event Action? OnStQuit;

    public VRSubprocessHost(Action<string> log) => _log = log;

    public void EnsureRunning(string cacheDir, int httpPort, string? authCookie, string? tfaCookie)
    {
        if (_process is { HasExited: false }) return;

        var executable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(executable))
            throw new InvalidOperationException("Could not determine the VRCNext executable path for the SteamVR helper process.");

        // AppImage launches through AppRun; ProcessPath may point at the wrapper rather
        // than the managed apphost. Launch the actual apphost so --vr-subprocess is parsed.
        if (Path.GetFileName(executable).Equals("AppRun", StringComparison.OrdinalIgnoreCase))
        {
            var appHost = Path.Combine(Path.GetDirectoryName(executable) ?? "", "usr", "bin", "vrcnext", "VRCNext");
            if (File.Exists(appHost)) executable = appHost;
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        startInfo.ArgumentList.Add("--vr-subprocess");

        var openVrLibraryDir = VrcPathsHelper.SteamLibraries()
            .Select(library => Path.Combine(library, "steamapps", "common", "SteamVR", "bin", "linux64"))
            .FirstOrDefault(directory => File.Exists(Path.Combine(directory, "libopenvr_api.so")));
        if (openVrLibraryDir != null)
        {
            var existingPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
            startInfo.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existingPath)
                ? openVrLibraryDir
                : openVrLibraryDir + Path.PathSeparator + existingPath;
        }
        else
        {
            _log("[VRSub] SteamVR libopenvr_api.so was not found in Steam libraries; SteamVR may be unavailable.");
        }

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the VRCNext SteamVR helper process.");
        _stdin = _process.StandardInput;
        _stdin.AutoFlush = true;
        _process.EnableRaisingEvents = true;
        _process.Exited += OnProcessExited;
        _ = ReadLoopAsync(_process.StandardOutput);
        Send(new JObject
        {
            ["t"] = "init",
            ["cacheDir"] = cacheDir,
            ["httpPort"] = httpPort,
            ["authCookie"] = authCookie,
            ["tfaCookie"] = tfaCookie,
            ["inputMode"] = InputMode,
        });
        _log($"[VRSub] Linux SteamVR helper process started (input mode {InputMode}).");
    }

    private async Task ReadLoopAsync(StreamReader reader)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var message = JObject.Parse(line);
                    switch (message["t"]?.Value<string>())
                    {
                        case "log":
                            _log(message["text"]?.Value<string>() ?? "");
                            break;
                        case "vro_state":
                            VroConnected = message["connected"]?.Value<bool>() ?? VroConnected;
                            OnVroState?.Invoke(message);
                            break;
                        case "sf_update":
                            SfConnected = message["connected"]?.Value<bool>() ?? SfConnected;
                            OnSfUpdate?.Invoke(message);
                            break;
                        case "st_update":
                            StConnected = message["connected"]?.Value<bool>() ?? StConnected;
                            OnStUpdate?.Invoke(message);
                            break;
                    }
                }
                catch (Newtonsoft.Json.JsonException ex)
                {
                    _log($"[VRSub] Invalid helper response: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _log($"[VRSub] Helper output ended: {ex.Message}");
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var hadVro = VroConnected;
        var hadSf = SfConnected;
        var hadSt = StConnected;
        VroConnected = false;
        SfConnected = false;
        StConnected = false;
        lock (_stdinLock) _stdin = null;
        if (hadVro) OnVroQuit?.Invoke();
        if (hadSf) OnSfQuit?.Invoke();
        if (hadSt) OnStQuit?.Invoke();
    }

    private void Send(JObject message)
    {
        try
        {
            lock (_stdinLock)
                _stdin?.WriteLine(message.ToString(Newtonsoft.Json.Formatting.None));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _log($"[VRSub] Could not send command to SteamVR helper: {ex.Message}");
        }
    }

    private void Kill()
    {
        if (_process == null) return;
        _process.Exited -= OnProcessExited;
        try { if (!_process.HasExited) _process.Kill(); } catch (InvalidOperationException) { }
        _process.Dispose();
        _process = null;
        lock (_stdinLock) _stdin = null;
    }

    public void VroConnect() { Send(new JObject { ["t"] = "vro_connect" }); }
    public void VroDisconnect()
    {
        VroConnected = false;
        Send(new JObject { ["t"] = "vro_disconnect" });
        if (!AnyConnected) Kill();
    }
    public void VroShow() { Send(new JObject { ["t"] = "vro_show" }); }
    public void VroHide() { Send(new JObject { ["t"] = "vro_hide" }); }
    public void VroRecordKeybind()   { Send(new JObject { ["t"] = "vro_record_keybind" }); }
    public void VroCancelRecording() { Send(new JObject { ["t"] = "vro_cancel_recording" }); }
    public void VroConfig(bool a, bool b, float c, float d, float e, float f, float g, float h, float i,
        System.Collections.Generic.List<uint> j, int k, int l,
        System.Collections.Generic.List<uint> m, int n, float o,
        bool p, float q, bool r)
    {
        Send(new JObject { ["t"]="vro_config", ["attachLeft"]=a, ["attachHand"]=b, ["px"]=c, ["py"]=d, ["pz"]=e, ["rx"]=f, ["ry"]=g, ["rz"]=h, ["width"]=i, ["keybind"]=JArray.FromObject(j), ["keybindHand"]=k, ["keybindMode"]=l, ["keybindDt"]=JArray.FromObject(m), ["keybindDtHand"]=n, ["controlRadius"]=o, ["dynVis"]=p, ["focusRadius"]=q, ["seamless"]=r });
    }
    public void VroApplyToastConfig(bool a, bool b, int c, float d, float e,
        bool f, bool g, bool h, bool i, bool j, bool k, int l, int m, bool n, bool o, bool p, bool q,
        bool r, bool s) => Send(new JObject { ["t"]="vro_toast_config", ["enabled"]=a, ["favOnly"]=b, ["size"]=c, ["offX"]=d, ["offY"]=e, ["online"]=f, ["offline"]=g, ["gps"]=h, ["status"]=i, ["statusDesc"]=j, ["bio"]=k, ["durationSec"]=l, ["stackSize"]=m, ["friendReq"]=n, ["invite"]=o, ["groupInv"]=p, ["joined"]=q, ["left"]=r, ["reqInvite"]=s });
    public void VroThemeColors(System.Collections.Generic.Dictionary<string, string> colors) => Send(new JObject { ["t"]="vro_theme_colors", ["colors"]=JObject.FromObject(colors) });
    public void AddNotification(string a, string b, string c, string d,
        string e = "", string f = "", string g = "", string h = "",         string i = "") => Send(new JObject { ["t"]="vro_add_notif", ["evType"]=a, ["friendName"]=b, ["evText"]=c, ["time"]=d, ["imageUrl"]=e, ["friendId"]=f, ["location"]=g, ["notifId"]=h, ["notifData"]=i });
    public void UpdateNotification(string a, string? b = null, string? c = null, string? d = null) => Send(new JObject { ["t"]="vro_update_notif", ["notifId"]=a, ["newText"]=b, ["newImageUrl"]=c, ["newFriendName"]=d });
    public void EnqueueToast(string a, string b, string c, string d, string e, bool f, string g = "") => Send(new JObject { ["t"]="vro_enqueue_toast", ["evType"]=a, ["friendName"]=b, ["evText"]=c, ["time"]=d, ["imageUrl"]=e, ["isFavorited"]=f, ["friendId"]=g });
    public void SetFriendLocations(System.Collections.Generic.IReadOnlyList<(string, string, string, string, string, string, string, string)> entries)
        => Send(new JObject { ["t"] = "vro_set_locations", ["entries"] = JArray.FromObject(entries.Select(e => new { worldId=e.Item1, instanceId=e.Item2, worldName=e.Item3, worldImageUrl=e.Item4, friendId=e.Item5, friendName=e.Item6, friendImageUrl=e.Item7, location=e.Item8 })) });
    public void SetOnlineFriends(System.Collections.Generic.IReadOnlyList<(string, string, string, string, string, string, string)> entries)
        => Send(new JObject { ["t"] = "vro_set_online_friends", ["entries"] = JArray.FromObject(entries.Select(e => new { friendId=e.Item1, friendName=e.Item2, friendImageUrl=e.Item3, status=e.Item4, statusDescription=e.Item5, location=e.Item6, worldName=e.Item7 })) });
    public void SetSelfUser(string userId, string imageUrl, string status) => Send(new JObject { ["t"]="vro_set_self", ["userId"]=userId, ["imageUrl"]=imageUrl, ["status"]=status });
    public void UpdateMediaInfo(string a, string b, double c, double d, bool e) => Send(new JObject { ["t"]="vro_update_media", ["title"]=a, ["artist"]=b, ["position"]=c, ["duration"]=d, ["playing"]=e });
    public void SetToolStates(bool a, bool b, bool c, bool d, bool e, bool f, bool g, bool h = false) => Send(new JObject { ["t"]="vro_tool_states", ["discord"]=a, ["voice"]=b, ["kikitan"]=c, ["space"]=d, ["relay"]=e, ["chatbox"]=f, ["frameShot"]=g, ["spaceTurn"]=h });
    public void SetKikitanState(string a, string b, bool c, string d, string e, string f, bool g) => Send(new JObject { ["t"]="vro_kikitan", ["sourceText"]=a, ["translatedText"]=b, ["isFinal"]=c, ["sourceLang"]=d, ["targetLang"]=e, ["engine"]=f, ["translateEnabled"]=g });
    public void SfConnect(float multiplier, bool lockX, bool lockY, bool lockZ, uint leftResetBtn, uint rightResetBtn, uint leftDragBtn, uint rightDragBtn, uint leftGravityBtn, uint rightGravityBtn, float gravity)
    {
        SfConnected = true;
        Send(new JObject { ["t"] = "sf_connect", ["multiplier"] = multiplier, ["lockX"] = lockX, ["lockY"] = lockY, ["lockZ"] = lockZ,
            ["leftResetBtn"] = leftResetBtn, ["rightResetBtn"] = rightResetBtn, ["leftDragBtn"] = leftDragBtn, ["rightDragBtn"] = rightDragBtn,
            ["leftGravityBtn"] = leftGravityBtn, ["rightGravityBtn"] = rightGravityBtn, ["gravity"] = gravity });
    }
    public void SfDisconnect()
    {
        SfConnected = false;
        Send(new JObject { ["t"] = "sf_disconnect" });
        if (!StConnected) Kill();
    }
    public void SfConfig(float multiplier, bool lockX, bool lockY, bool lockZ, uint leftResetBtn, uint rightResetBtn, uint leftDragBtn, uint rightDragBtn, uint leftGravityBtn, uint rightGravityBtn, float gravity)
        => Send(new JObject { ["t"] = "sf_config", ["multiplier"] = multiplier, ["lockX"] = lockX, ["lockY"] = lockY, ["lockZ"] = lockZ,
            ["leftResetBtn"] = leftResetBtn, ["rightResetBtn"] = rightResetBtn, ["leftDragBtn"] = leftDragBtn, ["rightDragBtn"] = rightDragBtn,
            ["leftGravityBtn"] = leftGravityBtn, ["rightGravityBtn"] = rightGravityBtn, ["gravity"] = gravity });
    public void SfReset() => Send(new JObject { ["t"] = "sf_reset" });
    public void StConnect(float multiplier, float snapDegrees, bool invert, float smoothing, uint leftTurnBtn, uint rightTurnBtn, uint leftResetBtn, uint rightResetBtn)
    {
        StConnected = true;
        Send(new JObject { ["t"] = "st_connect", ["multiplier"] = multiplier, ["snapDegrees"] = snapDegrees, ["invert"] = invert, ["smoothing"] = smoothing,
            ["leftTurnBtn"] = leftTurnBtn, ["rightTurnBtn"] = rightTurnBtn, ["leftResetBtn"] = leftResetBtn, ["rightResetBtn"] = rightResetBtn });
    }
    public void StDisconnect()
    {
        StConnected = false;
        Send(new JObject { ["t"] = "st_disconnect" });
        if (!SfConnected) Kill();
    }
    public void StConfig(float multiplier, float snapDegrees, bool invert, float smoothing, uint leftTurnBtn, uint rightTurnBtn, uint leftResetBtn, uint rightResetBtn)
        => Send(new JObject { ["t"] = "st_config", ["multiplier"] = multiplier, ["snapDegrees"] = snapDegrees, ["invert"] = invert, ["smoothing"] = smoothing,
            ["leftTurnBtn"] = leftTurnBtn, ["rightTurnBtn"] = rightTurnBtn, ["leftResetBtn"] = leftResetBtn, ["rightResetBtn"] = rightResetBtn });
    public void StReset() => Send(new JObject { ["t"] = "st_reset" });
    public void FsConnect(uint a, uint b, string c, string c2, int d, uint e, uint f, int g, int h, bool i, uint j, uint k, string l, string m, int n2, string n, string o, int p, uint q = 0, uint r = 0) { }
    public void FsDisconnect() { }
    public void FsConfig(uint a, uint b, int c, uint d, uint e, int f, int g, bool h, uint i, uint j, string k, string l, int m2, string m, string n, int o, uint p = 0, uint q = 0) { }
    public void FsGetAudioDevices() { }
    public event System.Action<System.Collections.Generic.List<(string, string)>>? OnFsAudioDevices;
    public void FsSetOutput(string a, string b) { }
    public void FsGetDevices() { }
    public event System.Action<Newtonsoft.Json.Linq.JObject>? OnFsUpdate;
    public event System.Action? OnFsQuit;
    public event System.Action<Newtonsoft.Json.Linq.JArray>? OnFsDevices;
    public event System.Action<string>? OnFsPhotoSaved;
    public event System.Action<string>? OnFsQrLink;
    public void TrimMemory() { }
    public void VroWaterConfig(bool enabled, int intervalSec) { Send(new JObject { ["t"]="vro_water_config", ["enabled"]=enabled, ["intervalSec"]=intervalSec }); }
    public void VroScaleConfig(bool enabled, bool leftThumb, bool rightThumb, System.Collections.Generic.List<uint> keybind, int hand, float scale, int sensitivity = 25) => Send(new JObject { ["t"]="vro_scale_config", ["enabled"]=enabled, ["leftThumb"]=leftThumb, ["rightThumb"]=rightThumb, ["keybind"]=JArray.FromObject(keybind), ["hand"]=hand, ["scale"]=scale, ["sensitivity"]=sensitivity });
    public void VroScaleUpdate(float scale) => Send(new JObject { ["t"]="vro_scale_update", ["scale"]=scale });
    public void VroRecordScaleKeybind() => Send(new JObject { ["t"]="vro_record_scale_keybind" });
    public void VroCancelScaleRecording() => Send(new JObject { ["t"]="vro_cancel_scale_recording" });
    public void VroSetLanguage(string lang) => Send(new JObject { ["t"]="vro_set_language", ["lang"]=lang });
    public event System.Action? OnVroWaterAlarm;
    public event System.Action? OnVroWaterDismissed;
    public event System.Action<float>? OnVroScaleChange;
    public event System.Action<System.Collections.Generic.List<uint>, System.Collections.Generic.List<string>, int>? OnVroScaleKeybindRecorded;
    public void Dispose() => Kill();
}
#endif
