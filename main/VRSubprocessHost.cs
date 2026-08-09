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
    public bool FsConnected  { get; private set; }

    public bool AnyConnected => VroConnected || SfConnected || FsConnected;

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
    public event Action<JObject>? OnFsUpdate;
    public event Action? OnFsQuit;
    public event Action<List<string>>? OnFsDevices;
    public event Action<string>? OnFsPhotoSaved;

    public VRSubprocessHost(Action<string> log) => _log = log;

    /// <summary>Starts the subprocess if it isn't already running, then sends the init message.</summary>
    public void EnsureRunning(string cacheDir, int httpPort, string? authCookie, string? tfaCookie)
    {
        if (_process is { HasExited: false }) return;

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
        });

        _log("[VRSub] Subprocess started");
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        _log("[VRSub] Subprocess exited");
        bool wasVro = VroConnected;
        bool wasSf  = SfConnected;
        bool wasFs  = FsConnected;
        VroConnected = false;
        SfConnected  = false;
        FsConnected  = false;
        lock (_stdinLock) _stdin = null;
        if (wasVro) OnVroQuit?.Invoke();
        if (wasSf)  OnSfQuit?.Invoke();
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
            case "fs_update":
                OnFsUpdate?.Invoke(msg);
                break;
            case "fs_devices":
                OnFsDevices?.Invoke(msg["devices"]?.ToObject<List<string>>() ?? new());
                break;
            case "fs_photo_saved":
                OnFsPhotoSaved?.Invoke(msg["path"]?.Value<string>() ?? "");
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

    public void VroConnect()
    {
        VroConnected = true;
        Send("vro_connect");
    }

    public void VroDisconnect()
    {
        VroConnected = false;
        Send("vro_disconnect");
        if (!SfConnected && !FsConnected) Kill();
    }

    public void VroShow()            => Send("vro_show");
    public void VroHide()            => Send("vro_hide");
    public void VroToggle()          => Send("vro_toggle");
    public void VroSetTab(int tab)   => Send("vro_set_tab",   new { tab });
    public void VroRecordKeybind()   => Send("vro_record_keybind");
    public void VroCancelRecording() => Send("vro_cancel_recording");

    public void VroConfig(bool attachLeft, bool attachHand,
        float px, float py, float pz, float rx, float ry, float rz, float width,
        List<uint> keybind, int keybindHand, int keybindMode,
        List<uint> keybindDt, int keybindDtHand, float controlRadius,
        bool dynVis, float focusRadius)
        => Send("vro_config", new { attachLeft, attachHand, px, py, pz, rx, ry, rz, width,
            keybind, keybindHand, keybindMode, keybindDt, keybindDtHand, controlRadius,
            dynVis, focusRadius });

    public void VroApplyToastConfig(bool enabled, bool favOnly, int size, float offX, float offY,
        bool online, bool offline, bool gps, bool status, bool statusDesc, bool bio,
        int durationSec, int stackSize, bool friendReq, bool invite, bool groupInv, bool joined)
        => Send("vro_toast_config", new { enabled, favOnly, size, offX, offY,
            online, offline, gps, status, statusDesc, bio, durationSec, stackSize,
            friendReq, invite, groupInv, joined });

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

    public void SetToolStates(bool discord, bool voice, bool kikitan, bool space, bool relay, bool chatbox, bool frameShot)
        => Send("vro_tool_states", new { discord, voice, kikitan, space, relay, chatbox, frameShot });

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
        if (!VroConnected && !FsConnected) Kill();
    }

    public void SfConfig(float multiplier, bool lockX, bool lockY, bool lockZ,
        uint leftResetBtn, uint rightResetBtn, uint leftDragBtn, uint rightDragBtn,
        uint leftGravityBtn, uint rightGravityBtn, float gravity)
        => Send("sf_config", new { multiplier, lockX, lockY, lockZ, leftResetBtn, rightResetBtn, leftDragBtn, rightDragBtn, leftGravityBtn, rightGravityBtn, gravity });

    public void SfReset() => Send("sf_reset");

    public void FsConnect(uint leftButton, uint rightButton, string outputDevice, int activationRadius,
                          uint leftRecordButton, uint rightRecordButton,
                          int gifMaxDim, int gifFps, bool useHmdRotations,
                          uint leftVideoButton, uint rightVideoButton,
                          string videoDeviceA, string videoDeviceB,
                          int videoFps, string videoQuality, string videoBitrateQuality, int audioKbps)
    {
        FsConnected = true;
        Send("fs_connect", new { leftButton, rightButton, outputDevice, activationRadius, leftRecordButton, rightRecordButton, gifMaxDim, gifFps, useHmdRotations, leftVideoButton, rightVideoButton, videoDeviceA, videoDeviceB, videoFps, videoQuality, videoBitrateQuality, audioKbps });
    }

    public void FsDisconnect()
    {
        FsConnected = false;
        Send("fs_disconnect");
        if (!VroConnected && !SfConnected) Kill();
    }

    public void FsConfig(uint leftButton, uint rightButton, int activationRadius,
                         uint leftRecordButton, uint rightRecordButton,
                         int gifMaxDim, int gifFps, bool useHmdRotations,
                         uint leftVideoButton, uint rightVideoButton,
                         string videoDeviceA, string videoDeviceB,
                         int videoFps, string videoQuality, string videoBitrateQuality, int audioKbps)
        => Send("fs_config", new { leftButton, rightButton, activationRadius, leftRecordButton, rightRecordButton, gifMaxDim, gifFps, useHmdRotations, leftVideoButton, rightVideoButton, videoDeviceA, videoDeviceB, videoFps, videoQuality, videoBitrateQuality, audioKbps });

    public void FsGetAudioDevices() => Send("fs_get_audio_devices");
    public event Action<List<(string id, string label)>>? OnFsAudioDevices;

    public void FsSetOutput(string deviceName)
        => Send("fs_set_output", new { deviceName });

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
namespace VRCNext;

public sealed class VRSubprocessHost : IDisposable
{
    public bool VroConnected { get; private set; }
    public bool SfConnected  { get; private set; }
    public bool FsConnected  { get; private set; }
    public bool AnyConnected => VroConnected || SfConnected || FsConnected;

    public VRSubprocessHost(Action<string> log) { }
    public void EnsureRunning(string c, int p, string? a, string? t) { }
    public void VroConnect()    { }
    public void VroDisconnect() { }
    public void VroShow()       { }
    public void VroHide()       { }
    public void VroToggle()     { }
    public void VroSetTab(int tab) { }
    public void VroRecordKeybind()   { }
    public void VroCancelRecording() { }
    public void VroConfig(bool a, bool b, float c, float d, float e, float f, float g, float h, float i,
        System.Collections.Generic.List<uint> j, int k, int l,
        System.Collections.Generic.List<uint> m, int n, float o,
        bool p, float q) { }
    public void VroApplyToastConfig(bool a, bool b, int c, float d, float e,
        bool f, bool g, bool h, bool i, bool j, bool k, int l, int m, bool n, bool o, bool p, bool q) { }
    public void VroThemeColors(System.Collections.Generic.Dictionary<string, string> colors) { }
    public void AddNotification(string a, string b, string c, string d,
        string e = "", string f = "", string g = "", string h = "", string i = "") { }
    public void UpdateNotification(string a, string? b = null, string? c = null, string? d = null) { }
    public void EnqueueToast(string a, string b, string c, string d, string e, bool f, string g = "") { }
    public void SetFriendLocations(System.Collections.Generic.IReadOnlyList<(string, string, string, string, string, string, string, string)> entries) { }
    public void SetOnlineFriends(System.Collections.Generic.IReadOnlyList<(string, string, string, string, string, string, string)> entries) { }
    public void SetSelfUser(string userId, string imageUrl, string status) { }
    public void UpdateMediaInfo(string a, string b, double c, double d, bool e) { }
    public void SetToolStates(bool a, bool b, bool c, bool d, bool e, bool f, bool g) { }
    public void SetKikitanState(string a, string b, bool c, string d, string e, string f, bool g) { }
    public void SfConnect(float a, bool b, bool c, bool d, uint e, uint f, uint g, uint h, uint i, uint j, float k) { }
    public void SfDisconnect() { }
    public void SfConfig(float a, bool b, bool c, bool d, uint e, uint f, uint g, uint h, uint i, uint j, float k) { }
    public void SfReset() { }
    public void FsConnect(uint a, uint b, string c, int d, uint e, uint f, int g, int h, bool i, uint j, uint k, string l, string m, int n2, string n, string o, int p) { }
    public void FsDisconnect() { }
    public void FsConfig(uint a, uint b, int c, uint d, uint e, int f, int g, bool h, uint i, uint j, string k, string l, int m2, string m, string n, int o) { }
    public void FsGetAudioDevices() { }
    public event System.Action<System.Collections.Generic.List<(string, string)>>? OnFsAudioDevices;
    public void FsSetOutput(string a) { }
    public void FsGetDevices() { }
    public event System.Action<Newtonsoft.Json.Linq.JObject>? OnFsUpdate;
    public event System.Action? OnFsQuit;
    public event System.Action<System.Collections.Generic.List<string>>? OnFsDevices;
    public event System.Action<string>? OnFsPhotoSaved;
    public void TrimMemory() { }
    public void VroWaterConfig(bool enabled, int intervalSec) { }
    public void VroScaleConfig(bool a0, bool a, bool b, System.Collections.Generic.List<uint> c, int d, float e, int f = 25) { }
    public void VroScaleUpdate(float scale) { }
    public void VroRecordScaleKeybind() { }
    public void VroCancelScaleRecording() { }
    public void VroSetLanguage(string lang) { }
    public event System.Action? OnVroWaterAlarm;
    public event System.Action? OnVroWaterDismissed;
    public event System.Action<float>? OnVroScaleChange;
    public event System.Action<System.Collections.Generic.List<uint>, System.Collections.Generic.List<string>, int>? OnVroScaleKeybindRecorded;
    public void Dispose() { }
}
#endif
