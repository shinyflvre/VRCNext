#if WINDOWS
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VRCNext.Services;
using VRCNext.Services.Helpers;

namespace VRCNext;

static class VRSubprocess
{
    private static volatile bool _toolConnected;
    private static System.Threading.Timer? _idleExitTimer;

    public static void Run()
    {
        Console.InputEncoding  = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        var initLine = Console.ReadLine();
        if (initLine == null) return;

        JObject init;
        try { init = JObject.Parse(initLine); }
        catch { return; }

        var authCookie = init["authCookie"]?.Value<string>();
        var tfaCookie  = init["tfaCookie"]?.Value<string>();

        VrInputActions.SetRequested((init["inputMode"]?.Value<int>() ?? 0) == 1);

        var cookieJar = new CookieContainer();
        var vrchatUri = new Uri("https://api.vrchat.cloud");
        if (!string.IsNullOrEmpty(authCookie))
            cookieJar.Add(vrchatUri, new Cookie("auth", authCookie, "/", ".vrchat.cloud"));
        if (!string.IsNullOrEmpty(tfaCookie))
            cookieJar.Add(vrchatUri, new Cookie("twoFactorAuth", tfaCookie, "/", ".vrchat.cloud"));

        var httpHandler = new HttpClientHandler { CookieContainer = cookieJar };
        var http = new HttpClient(httpHandler);
        http.DefaultRequestVersion = System.Net.HttpVersion.Version20;
        http.DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower;
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent);

        ImageCacheHelper.Initialize(http);

        void Log(string s) => SendLine(new JObject { ["t"] = "log", ["text"] = s });

        var settings = AppSettings.Load();
        ImageCacheHelper.LimitGb         = settings.ImgCacheLimitGb;
        ImageCacheHelper.OptimizeEnabled = settings.ImgCacheOptimizeEnabled;
        ImageCacheHelper.Log             = (msg, color) => Log(msg);

        var vro = new VROverlayService(Log);

        var sf = new SteamVRService(Log);

#if WINDOWS
        var fs = new FrameShotService(Log);
#endif

        vro.OnStateUpdate += d =>
        {
            var obj = JObject.FromObject(d);
            obj["t"] = "vro_state";
            SendLine(obj);
        };
        vro.OnKeybindRecorded += (ids, names, hand, mode) =>
            SendLine(new JObject
            {
                ["t"]     = "vro_keybind_recorded",
                ["ids"]   = JArray.FromObject(ids),
                ["names"] = JArray.FromObject(names),
                ["hand"]  = hand,
                ["mode"]  = mode,
            });
        vro.OnJoinRequest += (fid, loc) =>
            SendLine(new JObject { ["t"] = "vro_join_request", ["friendId"] = fid, ["location"] = loc });
        vro.OnInviteFriend += fid =>
            SendLine(new JObject { ["t"] = "vro_invite_friend", ["friendId"] = fid });
        vro.OnNotifAccept += (notifId, notifType, senderId, notifData) =>
            SendLine(new JObject
            {
                ["t"]         = "vro_notif_accept",
                ["notifId"]   = notifId,
                ["notifType"] = notifType,
                ["senderId"]  = senderId,
                ["notifData"] = notifData,
            });
        vro.OnToolToggle  += idx => SendLine(new JObject { ["t"] = "vro_tool_toggle",  ["index"] = idx });
        vro.OnToastSound  += ()  => SendLine(new JObject { ["t"] = "vro_toast_sound" });
        vro.OnWaterAlarm     += () => SendLine(new JObject { ["t"] = "vro_water_alarm" });
        vro.OnWaterDismissed += () => SendLine(new JObject { ["t"] = "vro_water_dismissed" });
        vro.OnScaleChange    += delta => SendLine(new JObject { ["t"] = "vro_scale_change", ["delta"] = delta });
        vro.OnScaleKeybindRecorded += (ids, names, hand) =>
            SendLine(new JObject
            {
                ["t"]     = "vro_scale_keybind_recorded",
                ["ids"]   = JArray.FromObject(ids),
                ["names"] = JArray.FromObject(names),
                ["hand"]  = hand,
            });
        vro.OnVRQuit      += ()  => Environment.Exit(0);

        sf.SetUpdateCallback(data =>
        {
            var obj = JObject.FromObject(data);
            obj["t"] = "sf_update";
            SendLine(obj);
        });
        sf.SetTurnUpdateCallback(data =>
        {
            var obj = JObject.FromObject(data);
            obj["t"] = "st_update";
            SendLine(obj);
        });
        sf.OnVRQuit += () => Environment.Exit(0);

        fs.OnStateUpdate += d =>
        {
            var obj = JObject.FromObject(d);
            obj["t"] = "fs_update";
            SendLine(obj);
        };
        fs.OnPhotoSaved += path =>
            SendLine(new JObject { ["t"] = "fs_photo_saved", ["path"] = path });
        fs.OnQrLink += url =>
            SendLine(new JObject { ["t"] = "fs_qr_link", ["url"] = url });
        fs.OnVRQuit += () => Environment.Exit(0);

        _idleExitTimer = new System.Threading.Timer(_ =>
        {
            if (!_toolConnected)
            {
                Log("[Sub] No tool connected, exiting idle subprocess");
                Environment.Exit(0);
            }
        }, null, 20_000, System.Threading.Timeout.Infinite);

        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var cmd = JObject.Parse(line);
                Dispatch(cmd, vro, sf, fs);
            }
            catch (Exception ex) { Log($"[Sub] Dispatch error: {ex.Message}"); }
        }

        try { vro.Dispose(); } catch { }
        try { sf.Dispose();  } catch { }
        try { fs.Dispose();  } catch { }
    }

    private static void Dispatch(JObject cmd, VROverlayService vro, SteamVRService sf, FrameShotService fs)
    {
        var t = cmd["t"]?.Value<string>() ?? "";

        switch (t)
        {
            case "vro_connect":
            {
                _toolConnected = true;
                bool ok = vro.Connect();
                if (ok) vro.StartPolling();
                SendLine(new JObject
                {
                    ["t"]         = "vro_state",
                    ["connected"] = ok,
                    ["visible"]   = false,
                    ["recording"] = false,
                    ["error"]     = ok ? null : vro.LastError,
                });
                break;
            }

            case "vro_disconnect":
                vro.Disconnect();
                break;

            case "vro_config":
                vro.ApplyConfig(
                    B(cmd, "attachLeft", true), B(cmd, "attachHand", true),
                    F(cmd, "px"), F(cmd, "py", 0.07f), F(cmd, "pz", -0.05f),
                    F(cmd, "rx", -80f), F(cmd, "ry"), F(cmd, "rz"),
                    F(cmd, "width", 0.22f),
                    UList(cmd, "keybind"), I(cmd, "keybindHand"), I(cmd, "keybindMode"),
                    UList(cmd, "keybindDt"), I(cmd, "keybindDtHand"), F(cmd, "controlRadius", 28f),
                    B(cmd, "dynVis", false), F(cmd, "focusRadius", 35f),
                    B(cmd, "seamless", false));
                break;

            case "vro_toast_config":
                vro.ApplyToastConfig(
                    B(cmd, "enabled", true), B(cmd, "favOnly"),
                    I(cmd, "size", 50), F(cmd, "offX"), F(cmd, "offY", -0.12f),
                    B(cmd, "online", true), B(cmd, "offline", true),
                    B(cmd, "gps", true), B(cmd, "status", true),
                    B(cmd, "statusDesc", true), B(cmd, "bio", true),
                    I(cmd, "durationSec", 8), I(cmd, "stackSize", 2),
                    B(cmd, "friendReq", true), B(cmd, "invite", true), B(cmd, "groupInv", true),
                    B(cmd, "joined", true), B(cmd, "left", true), B(cmd, "reqInvite", true));
                break;

            case "vro_show":    vro.Show();    break;
            case "vro_hide":    vro.Hide();    break;

            case "vro_water_config":
                vro.ApplyWaterConfig(B(cmd, "enabled"), I(cmd, "intervalSec") * 1000L);
                break;

            case "vro_set_language":
                vro.SetLanguage(S(cmd, "lang"));
                break;

            case "vro_water_dismiss":
                vro.DismissWaterAlarm();
                break;

            case "vro_theme_colors":
            {
                if (cmd["colors"] is JObject c)
                {
                    var dict = c.Properties().ToDictionary(p => p.Name, p => p.Value.ToString());
                    vro.SetThemeColors(dict);
                }
                break;
            }

            case "vro_kikitan":
                vro.SetKikitanState(
                    S(cmd, "sourceText"), S(cmd, "translatedText"), B(cmd, "isFinal"),
                    S(cmd, "sourceLang"), S(cmd, "targetLang"), S(cmd, "engine"),
                    B(cmd, "translateEnabled"));
                break;

            case "vro_tool_states":
                vro.SetToolStates(
                    B(cmd, "discord"), B(cmd, "voice"), B(cmd, "kikitan"),
                    B(cmd, "space"),   B(cmd, "relay"), B(cmd, "chatbox"),
                    B(cmd, "frameShot"), B(cmd, "spaceTurn"));
                break;

            case "vro_add_notif":
                vro.AddNotification(
                    S(cmd, "evType"), S(cmd, "friendName"), S(cmd, "evText"), S(cmd, "time"),
                    S(cmd, "imageUrl"), S(cmd, "friendId"), S(cmd, "location"),
                    S(cmd, "notifId"), S(cmd, "notifData"));
                break;

            case "vro_update_notif":
                vro.UpdateNotification(S(cmd, "notifId"),
                    SN(cmd, "newText"), SN(cmd, "newImageUrl"), SN(cmd, "newFriendName"));
                break;

            case "vro_enqueue_toast":
                vro.EnqueueToast(
                    S(cmd, "evType"), S(cmd, "friendName"), S(cmd, "evText"), S(cmd, "time"),
                    S(cmd, "imageUrl"), B(cmd, "isFavorited"), S(cmd, "friendId"));
                break;

            case "vro_set_locations":
            {
                if (cmd["entries"] is JArray arr)
                {
                    var list = new List<(string, string, string, string, string, string, string, string)>();
                    foreach (var e in arr)
                        list.Add((S(e, "worldId"), S(e, "instanceId"), S(e, "worldName"),
                            S(e, "worldImageUrl"), S(e, "friendId"), S(e, "friendName"),
                            S(e, "friendImageUrl"), S(e, "location")));
                    vro.SetFriendLocations(list);
                }
                break;
            }

            case "vro_set_online_friends":
            {
                if (cmd["entries"] is JArray arr)
                {
                    var list = new List<(string, string, string, string, string, string, string)>();
                    foreach (var e in arr)
                        list.Add((S(e, "friendId"), S(e, "friendName"), S(e, "friendImageUrl"),
                            S(e, "status"), S(e, "statusDescription"), S(e, "location"), S(e, "worldName")));
                    vro.SetOnlineFriends(list);
                }
                break;
            }

            case "vro_set_self":
                vro.SetSelfUser(S(cmd, "userId"), S(cmd, "imageUrl"), S(cmd, "status"));
                break;

            case "vro_update_media":
                vro.UpdateMediaInfo(S(cmd, "title"), S(cmd, "artist"),
                    D(cmd, "position"), D(cmd, "duration"), B(cmd, "playing"));
                break;

            case "vr_input_mode":
            {
                bool wantIdx = I(cmd, "mode") == 1;
                VrInputActions.SetRequested(wantIdx);
                if (wantIdx && Valve.VR.OpenVR.System != null)
                    VrInputActions.Initialize(s => SendLine(new JObject { ["t"] = "log", ["text"] = s }));
                break;
            }

            case "vro_record_keybind":   vro.StartKeybindRecording(); break;
            case "vro_cancel_recording": vro.StopKeybindRecording();  break;

            case "vro_scale_config":
                vro.SetScaleConfig(
                    B(cmd, "scaleEnabled", true), B(cmd, "leftThumb"), B(cmd, "rightThumb", true),
                    UList(cmd, "keybind"), I(cmd, "keybindHand"), F(cmd, "currentScale", 1f),
                    I(cmd, "scrollSensitivity", 25));
                break;

            case "vro_scale_update":
                vro.SetCurrentScale(F(cmd, "scale", 1f));
                break;

            case "vro_record_scale_keybind":
                vro.StartScaleKeybindRecording();
                break;

            case "vro_cancel_scale_recording":
                vro.StopScaleKeybindRecording();
                break;

            case "sf_connect":
            {
                _toolConnected = true;
                bool ok = sf.Connect();
                if (ok)
                {
                    sf.DragEnabled = true;
                    sf.ApplyConfig(F(cmd, "multiplier", 1f),
                        B(cmd, "lockX"), B(cmd, "lockY"), B(cmd, "lockZ"),
                        (uint)I(cmd, "leftResetBtn",  32),
                        (uint)I(cmd, "rightResetBtn",  0),
                        (uint)I(cmd, "leftDragBtn",    0),
                        (uint)I(cmd, "rightDragBtn",  32),
                        (uint)I(cmd, "leftGravityBtn",  0),
                        (uint)I(cmd, "rightGravityBtn", 0),
                        F(cmd, "gravity", 9.8f));
                    sf.StartPolling();
                }
                break;
            }

            case "sf_disconnect":
                sf.DragEnabled = false;
                if (!sf.TurnEnabled) sf.Disconnect();
                break;

            case "st_connect":
            {
                _toolConnected = true;
                bool ok = sf.Connect();
                if (ok)
                {
                    sf.TurnEnabled = true;
                    sf.ApplyTurnConfig(F(cmd, "multiplier", 1f),
                        F(cmd, "snapDegrees", 0f),
                        B(cmd, "invert"),
                        F(cmd, "smoothing", 0f),
                        (uint)I(cmd, "leftTurnBtn",   2),
                        (uint)I(cmd, "rightTurnBtn",  0),
                        (uint)I(cmd, "leftResetBtn",  0),
                        (uint)I(cmd, "rightResetBtn", 0));
                    sf.StartPolling();
                }
                break;
            }

            case "st_disconnect":
                sf.TurnEnabled = false;
                if (!sf.DragEnabled) sf.Disconnect();
                break;

            case "st_config":
                sf.ApplyTurnConfig(F(cmd, "multiplier", 1f),
                    F(cmd, "snapDegrees", 0f),
                    B(cmd, "invert"),
                    F(cmd, "smoothing", 0f),
                    (uint)I(cmd, "leftTurnBtn",   2),
                    (uint)I(cmd, "rightTurnBtn",  0),
                    (uint)I(cmd, "leftResetBtn",  0),
                    (uint)I(cmd, "rightResetBtn", 0));
                break;

            case "st_reset":
                sf.ResetRotation();
                break;

            case "sf_config":
                sf.ApplyConfig(F(cmd, "multiplier", 1f),
                    B(cmd, "lockX"), B(cmd, "lockY"), B(cmd, "lockZ"),
                    (uint)I(cmd, "leftResetBtn",  32),
                    (uint)I(cmd, "rightResetBtn",  0),
                    (uint)I(cmd, "leftDragBtn",    0),
                    (uint)I(cmd, "rightDragBtn",  32),
                    (uint)I(cmd, "leftGravityBtn",  0),
                    (uint)I(cmd, "rightGravityBtn", 0),
                    F(cmd, "gravity", 9.8f));
                break;

            case "sf_reset":
                sf.ResetPosition();
                break;

            case "fs_connect":
            {
                _toolConnected = true;
                bool ok = fs.Connect();
                if (ok)
                {
                    fs.ApplyConfig(
                        (uint)I(cmd, "leftButton",  2),
                        (uint)I(cmd, "rightButton", 2),
                        I(cmd, "activationRadius", 15) / 100f,
                        (uint)I(cmd, "leftRecordButton",  0),
                        (uint)I(cmd, "rightRecordButton", 0),
                        I(cmd, "gifMaxDim", 512),
                        I(cmd, "gifFps", 10),
                        B(cmd, "useHmdRotations"),
                        (uint)I(cmd, "leftVideoButton",  0),
                        (uint)I(cmd, "rightVideoButton", 0),
                        S(cmd, "videoDeviceA"),
                        S(cmd, "videoDeviceB"),
                        I(cmd, "videoFps", 30),
                        S(cmd, "videoQuality", "1080p"),
                        S(cmd, "videoBitrateQuality", "medium"),
                        I(cmd, "audioKbps", 256),
                        (uint)I(cmd, "leftAcceptButton",  0),
                        (uint)I(cmd, "rightAcceptButton", 0));
                    fs.SetOutputDevice(VRCNext.Services.Helpers.AudioSelection.From(S(cmd, "outputDeviceId"), S(cmd, "outputDeviceName")));
                    fs.StartPolling();
                }
                break;
            }

            case "fs_disconnect":
                fs.Disconnect();
                break;

            case "fs_config":
                fs.ApplyConfig(
                    (uint)I(cmd, "leftButton",  2),
                    (uint)I(cmd, "rightButton", 2),
                    I(cmd, "activationRadius", 15) / 100f,
                    (uint)I(cmd, "leftRecordButton",  0),
                    (uint)I(cmd, "rightRecordButton", 0),
                    I(cmd, "gifMaxDim", 512),
                    I(cmd, "gifFps", 10),
                    B(cmd, "useHmdRotations"),
                    (uint)I(cmd, "leftVideoButton",  0),
                    (uint)I(cmd, "rightVideoButton", 0),
                    S(cmd, "videoDeviceA"),
                    S(cmd, "videoDeviceB"),
                    I(cmd, "videoFps", 30),
                    S(cmd, "videoQuality", "1080p"),
                    S(cmd, "videoBitrateQuality", "medium"),
                    I(cmd, "audioKbps", 256),
                    (uint)I(cmd, "leftAcceptButton",  0),
                    (uint)I(cmd, "rightAcceptButton", 0));
                break;

            case "fs_set_output":
                fs.SetOutputDevice(VRCNext.Services.Helpers.AudioSelection.From(S(cmd, "deviceId"), S(cmd, "deviceName")));
                break;

            case "fs_get_audio_devices":
            {
                var devices = FrameShotService.GetAudioDevices();
                var arr = new JArray();
                foreach (var (id, label) in devices)
                    arr.Add(new JObject { ["id"] = id, ["label"] = label });
                SendLine(new JObject { ["t"] = "fs_audio_devices", ["devices"] = arr });
                break;
            }

            case "fs_get_devices":
            {
                var arr = new JArray();
                foreach (var (id, name) in VRCNext.Services.Helpers.AudioDeviceManager.ListOutputs())
                    arr.Add(new JObject { ["id"] = id, ["name"] = name });
                SendLine(new JObject
                {
                    ["t"]       = "fs_devices",
                    ["devices"] = arr,
                });
                break;
            }

            case "trim":
                _ = Task.Run(() =>
                {
                    try
                    {
                        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                        EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
                    }
                    catch { }
                });
                break;
        }
    }

    [System.Runtime.InteropServices.DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(nint hProcess);

    private static readonly object _writeLock = new();

    private static void SendLine(JObject obj)
    {
        var json = obj.ToString(Formatting.None);
        lock (_writeLock)
        {
            Console.WriteLine(json);
            Console.Out.Flush();
        }
    }

    private static string S(JToken t, string k, string def = "") => t[k]?.Value<string>() ?? def;
    private static bool   B(JToken t, string k, bool   def = false) => t[k]?.Value<bool>()   ?? def;
    private static int    I(JToken t, string k, int    def = 0)     => t[k]?.Value<int>()    ?? def;
    private static float  F(JToken t, string k, float  def = 0f)    => t[k]?.Value<float>()  ?? def;
    private static double D(JToken t, string k, double def = 0)     => t[k]?.Value<double>() ?? def;
    private static string S(JToken t, string k, string def = "")    => t[k]?.Value<string>() ?? def;
    private static string? SN(JToken t, string k) => t[k]?.Type == JTokenType.Null ? null : t[k]?.Value<string>();

    private static List<uint> UList(JToken t, string k)
    {
        if (t[k] is not JArray arr) return new();
        var list = new List<uint>(arr.Count);
        foreach (var item in arr) list.Add(item.Value<uint>());
        return list;
    }
}
#else
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VRCNext.Services;

namespace VRCNext;

static class VRSubprocess
{
    private static readonly object _writeLock = new();

    public static void Run()
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;
        var initLine = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(initLine)) return;
        JObject init;
        try { init = JObject.Parse(initLine); }
        catch (JsonException ex) { SendLine("log", new { text = $"[Sub] Invalid initialization message: {ex.Message}" }); return; }

        void Log(string text) => SendLine("log", new { text });
        VrInputActions.SetRequested((init["inputMode"]?.Value<int>() ?? 0) == 1);
        using var steamVr = new SteamVRService(Log);
        steamVr.SetUpdateCallback(state => SendLine("sf_update", state));
        steamVr.SetTurnUpdateCallback(state => SendLine("st_update", state));
        steamVr.OnVRQuit += () => Environment.Exit(0);

        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { Dispatch(JObject.Parse(line), steamVr); }
            catch (Exception ex) { Log($"[Sub] Dispatch error: {ex.Message}"); }
        }
    }

    private static void Dispatch(JObject cmd, SteamVRService steamVr)
    {
        switch (cmd["t"]?.Value<string>() ?? "")
        {
            case "vr_input_mode":
                VrInputActions.SetRequested(cmd["mode"]?.Value<int>() == 1);
                if (VrInputActions.Requested && Valve.VR.OpenVR.System != null)
                    VrInputActions.Initialize(s => SendLine("log", new { text = s }));
                break;
            case "sf_connect":
                if (!steamVr.Connect()) { SendLine("sf_update", new { connected = false, dragging = false, offsetX = 0, offsetY = 0, offsetZ = 0, leftController = false, rightController = false, error = steamVr.LastError }); break; }
                steamVr.DragEnabled = true;
                steamVr.ApplyConfig(F(cmd,"multiplier",1f), B(cmd,"lockX"), B(cmd,"lockY"), B(cmd,"lockZ"), U(cmd,"leftResetBtn",32), U(cmd,"rightResetBtn"), U(cmd,"leftDragBtn"), U(cmd,"rightDragBtn",32), U(cmd,"leftGravityBtn"), U(cmd,"rightGravityBtn"), F(cmd,"gravity",9.8f));
                steamVr.StartPolling();
                break;
            case "sf_disconnect": steamVr.DragEnabled = false; if (!steamVr.TurnEnabled) steamVr.Disconnect(); break;
            case "sf_config": steamVr.ApplyConfig(F(cmd,"multiplier",1f), B(cmd,"lockX"), B(cmd,"lockY"), B(cmd,"lockZ"), U(cmd,"leftResetBtn",32), U(cmd,"rightResetBtn"), U(cmd,"leftDragBtn"), U(cmd,"rightDragBtn",32), U(cmd,"leftGravityBtn"), U(cmd,"rightGravityBtn"), F(cmd,"gravity",9.8f)); break;
            case "sf_reset": steamVr.ResetPosition(); break;
            case "st_connect":
                if (!steamVr.Connect()) { SendLine("st_update", new { connected = false, turning = false, rotation = 0f, leftController = false, rightController = false, error = steamVr.LastError }); break; }
                steamVr.TurnEnabled = true;
                steamVr.ApplyTurnConfig(F(cmd,"multiplier",1f), F(cmd,"snapDegrees"), B(cmd,"invert"), F(cmd,"smoothing"), U(cmd,"leftTurnBtn",2), U(cmd,"rightTurnBtn"), U(cmd,"leftResetBtn"), U(cmd,"rightResetBtn"));
                steamVr.StartPolling();
                break;
            case "st_disconnect": steamVr.TurnEnabled = false; if (!steamVr.DragEnabled) steamVr.Disconnect(); break;
            case "st_config": steamVr.ApplyTurnConfig(F(cmd,"multiplier",1f), F(cmd,"snapDegrees"), B(cmd,"invert"), F(cmd,"smoothing"), U(cmd,"leftTurnBtn",2), U(cmd,"rightTurnBtn"), U(cmd,"leftResetBtn"), U(cmd,"rightResetBtn")); break;
            case "st_reset": steamVr.ResetRotation(); break;
        }
    }

    private static void SendLine(string type, object payload)
    {
        var message = JObject.FromObject(payload); message["t"] = type;
        lock (_writeLock) { Console.WriteLine(message.ToString(Formatting.None)); Console.Out.Flush(); }
    }
    private static bool B(JObject v,string n,bool d=false)=>v[n]?.Value<bool>()??d;
    private static float F(JObject v,string n,float d=0f)=>v[n]?.Value<float>()??d;
    private static uint U(JObject v,string n,uint d=0)=>v[n]?.Value<uint>()??d;
}
#endif
