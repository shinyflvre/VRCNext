#if WINDOWS
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace VRCNext;

// Owns all VR wrist-overlay state, message handling, and lifecycle.
// VROverlayService runs in an isolated subprocess — see VRSubprocessHost / VRSubprocess.

public class VROverlayController : IDisposable
{
    private readonly CoreLibrary _core;
    private readonly FriendsController _friends;
    private VRSubprocessHost? _wiredHost;
    private bool _disposed;

    // Callbacks set by AppShell
    public Action<int>?    OnToolToggle    { get; set; }
    public Action<float>?  OnVrScaleChange { get; set; }
    public Action<List<uint>, List<string>, int>? OnVrScaleKeybindRecorded { get; set; }
    public Func<(bool discord, bool voice, bool kikitan, bool space, bool relay, bool chatbox, bool frameShot)>? GetToolStates { get; set; }

    public VROverlayController(CoreLibrary core, FriendsController friends)
    {
        _core    = core;
        _friends = friends;
    }

    private VRSubprocessHost EnsureHost()
    {
        if (_core.VrOverlay == null)
        {
            _core.VrOverlay = new VRSubprocessHost(
                s => Invoke(() => _core.SendToJS("log", new { msg = s, color = "sec" })));
        }

        var h = _core.VrOverlay;
        if (!ReferenceEquals(_wiredHost, h))
        {
            _wiredHost = h;

            h.OnVroState += d => Invoke(() => _core.SendToJS("vroState", d));

            h.OnVroKeybindRecorded += (ids, names, hand, mode) =>
                Invoke(() => _core.SendToJS("vroKeybindRecorded", new { ids, names, hand, mode }));

            h.OnVroToolToggle += idx => Invoke(() => OnToolToggle?.Invoke(idx));

            h.OnVroJoinRequest += (fid, loc) => Invoke(async () =>
            {
                bool ok;
                if (loc.Contains(':'))
                {
                    ok = await _core.Instances.InviteSelfAsync(loc);
                    _core.SendToJS("log", new { msg = ok ? "Self-invite sent — check VRChat notifications!" : "Failed to send self-invite.", color = ok ? "ok" : "err" });
                }
                else
                {
                    ok = await _core.Invite.RequestInviteAsync(fid);
                    _core.SendToJS("log", new { msg = ok ? "Invite request sent!" : "Failed to send invite request.", color = ok ? "ok" : "err" });
                }
            });

            h.OnVroInviteFriend += fid => Invoke(async () =>
            {
                var loc = _core.LogWatcher?.CurrentLocation ?? "";
                if (string.IsNullOrEmpty(loc) || loc == "offline" || loc == "traveling")
                {
                    _core.SendToJS("log", new { msg = "Can't invite: you're not in an instance.", color = "err" });
                    return;
                }
                var ok = await _core.Invite.InviteFriendAsync(fid, loc);
                _core.SendToJS("log", new { msg = ok ? "Invite sent!" : "Failed to send invite.", color = ok ? "ok" : "err" });
            });

            h.OnVroToastSound     += () => Invoke(() => _core.SendToJS("vroPlayToastSound",  new { }));
            h.OnVroWaterAlarm     += () => Invoke(() => _core.SendToJS("vroPlayWaterSound",  new { }));
            h.OnVroWaterDismissed += () => Invoke(() => _core.SendToJS("vroStopWaterSound",  new { }));
            h.OnVroScaleChange    += delta => Invoke(() => OnVrScaleChange?.Invoke(delta));
            h.OnVroScaleKeybindRecorded += (ids, names, hand) =>
                Invoke(() =>
                {
                    OnVrScaleKeybindRecorded?.Invoke(ids, names, hand);
                    _core.SendToJS("vroScaleKeybindRecorded", new { ids, names, hand });
                });

            h.OnVroNotifAccept += (notifId, notifType, senderId, notifData) => Invoke(async () =>
            {
                bool ok = false;
                string resultMsg = "";
                if (notifType == "friendRequest")
                {
                    ok = await _core.Notifications.AcceptNotificationAsync(notifId);
                    resultMsg = ok ? "Friend request accepted!" : "Failed to accept.";
                }
                else if (notifType == "invite")
                {
                    if (!string.IsNullOrEmpty(notifData) && notifData.Contains(":"))
                    {
                        ok = await _core.Instances.InviteSelfAsync(notifData);
                        if (ok) await _core.Notifications.AcceptNotificationAsync(notifId);
                        resultMsg = ok ? "Joining world..." : "Failed to join.";
                    }
                    else
                    {
                        ok = await _core.Notifications.AcceptNotificationAsync(notifId);
                        resultMsg = ok ? "Invite accepted!" : "Failed.";
                    }
                }
                else if (notifType == "group.invite")
                {
                    if (!string.IsNullOrEmpty(notifData))
                    {
                        ok = await _core.Groups.JoinGroupAsync(notifData);
                        if (ok) await _core.Notifications.HideNotificationAsync(notifId, false);
                        resultMsg = ok ? "Group joined!" : "Failed to join group.";
                    }
                    else
                    {
                        ok = await _core.Notifications.AcceptNotificationAsync(notifId);
                        resultMsg = ok ? "Accepted!" : "Failed.";
                    }
                }
                _core.SendToJS("log", new { msg = resultMsg, color = ok ? "ok" : "err" });
                _core.SendToJS("vrcActionResult", new { action = "acceptNotif", success = ok, message = resultMsg });
            });

            h.OnVroQuit += () =>
            {
                if (!h.AnyConnected) _core.VrOverlay = null;
                Invoke(() =>
                {
                    _core.SendToJS("vroState", new { connected = false });
                    UpdateToolStates();
                });
            };
        }

        return _core.VrOverlay;
    }

    public async Task HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "vroConnect":
            {
                if (_core.VrOverlay?.VroConnected == true)
                    _core.VrOverlay.VroDisconnect();

                var host = EnsureHost();
                var (auth, tfa) = _core.VrcApi.GetCookies();
                host.InputMode = _core.Settings.VrInputMode;
                host.EnsureRunning("", _core.HttpPort, auth, tfa);

                // Send theme colors
                if (msg["themeColors"] is JObject tc)
                    host.VroThemeColors(tc.Properties().ToDictionary(p => p.Name, p => p.Value.ToString()));
                else
                    host.VroThemeColors(new Dictionary<string, string>()); // subprocess will use built-in theme

                // Send overlay config
                host.VroConfig(
                    _core.Settings.VroAttachLeft, _core.Settings.VroAttachHand,
                    _core.Settings.VroPosX, _core.Settings.VroPosY, _core.Settings.VroPosZ,
                    _core.Settings.VroRotX, _core.Settings.VroRotY, _core.Settings.VroRotZ,
                    _core.Settings.VroWidth,
                    _core.Settings.VrInputMode == 1 ? _core.Settings.VroIdxKeybind : _core.Settings.VroKeybind,
                    _core.Settings.VrInputMode == 1 ? _core.Settings.VroIdxKeybindHand : _core.Settings.VroKeybindHand,
                    _core.Settings.VroKeybindMode,
                    _core.Settings.VrInputMode == 1 ? _core.Settings.VroIdxKeybindDt : _core.Settings.VroKeybindDt,
                    _core.Settings.VrInputMode == 1 ? _core.Settings.VroIdxKeybindDtHand : _core.Settings.VroKeybindDtHand,
                    _core.Settings.VroControlRadius,
                    _core.Settings.VroDynVis, _core.Settings.VroFocusRadius,
                    _core.Settings.VroSeamless);

                // Send toast config
                host.VroApplyToastConfig(
                    _core.Settings.VroToastEnabled, _core.Settings.VroToastFavOnly,
                    _core.Settings.VroToastSize, _core.Settings.VroToastOffsetX, _core.Settings.VroToastOffsetY,
                    _core.Settings.VroToastOnline, _core.Settings.VroToastOffline,
                    _core.Settings.VroToastGps, _core.Settings.VroToastStatus,
                    _core.Settings.VroToastStatusDesc, _core.Settings.VroToastBio,
                    _core.Settings.VroToastDuration, _core.Settings.VroToastStack,
                    _core.Settings.VroToastFriendReq, _core.Settings.VroToastInvite, _core.Settings.VroToastGroupInv,
                    _core.Settings.VroToastJoined);

                // Send language (for weekday localization in dashboard)
                host.VroSetLanguage(_core.Settings.Language ?? "en");

                // Send water config
                host.VroWaterConfig(_core.Settings.VroWaterEnabled,
                    _core.Settings.VroWaterHours * 3600 + _core.Settings.VroWaterMinutes * 60);

                // Send scale config
                host.VroScaleConfig(
                    _core.Settings.VroScaleEnabled,
                    _core.Settings.VroScaleLeftThumb, _core.Settings.VroScaleRightThumb,
                    _core.Settings.VrInputMode == 1 ? _core.Settings.VroIdxScaleKeybind : _core.Settings.VroScaleKeybind,
                    _core.Settings.VrInputMode == 1 ? _core.Settings.VroIdxScaleKeybindHand : _core.Settings.VroScaleKeybindHand,
                    _core.Settings.AsScale, _core.Settings.VroScaleScrollSensitivity);

                // Connect — subprocess sends back vro_state with result
                host.VroConnect();

                // Push current data to the overlay
                _friends.PushVroLocations();
                _friends.PushVroOnlineFriends();
                UpdateToolStates();
                break;
            }

            case "overlayThemeColors":
            {
                if (msg["colors"] is JObject colors)
                {
                    var dict = colors.Properties().ToDictionary(p => p.Name, p => p.Value.ToString());
                    _core.VrOverlay?.VroThemeColors(dict);
                    _core.OnTrayThemeUpdate?.Invoke(dict);
                }
                break;
            }

            case "vroDisconnect":
            {
                if (_core.VrOverlay != null)
                {
                    _core.VrOverlay.VroDisconnect();
                    if (!_core.VrOverlay.AnyConnected) _core.VrOverlay = null;
                }
                _core.SendToJS("vroState", new { connected = false, visible = false, recording = false });
                break;
            }

            case "vroShow":    _core.VrOverlay?.VroShow();   break;
            case "vroHide":    _core.VrOverlay?.VroHide();   break;

            case "vroConfig":
            {
                bool left   = msg["attachLeft"]?.Value<bool>() ?? true;
                bool hand   = msg["attachHand"]?.Value<bool>() ?? true;
                float px    = msg["posX"]?.Value<float>() ?? 0f;
                float py    = msg["posY"]?.Value<float>() ?? 0.07f;
                float pz    = msg["posZ"]?.Value<float>() ?? -0.05f;
                float rx    = msg["rotX"]?.Value<float>() ?? -80f;
                float ry    = msg["rotY"]?.Value<float>() ?? 0f;
                float rz    = msg["rotZ"]?.Value<float>() ?? 0f;
                float width = msg["width"]?.Value<float>() ?? 0.22f;
                var kb       = msg["keybind"]?.ToObject<List<uint>>() ?? new();
                int kbHand   = msg["keybindHand"]?.Value<int>() ?? 0;
                int kbMode   = msg["keybindMode"]?.Value<int>() ?? 0;
                var kbDt     = msg["keybindDt"]?.ToObject<List<uint>>() ?? new();
                int kbDtHand = msg["keybindDtHand"]?.Value<int>() ?? 0;
                int ctrlR    = msg["controlRadius"]?.Value<int>() ?? 28;
                bool dynVis  = msg["dynVis"]?.Value<bool>() ?? false;
                int focusR   = msg["focusRadius"]?.Value<int>() ?? 35;
                bool seamless = msg["seamless"]?.Value<bool>() ?? false;
                int inputMode   = msg["inputMode"]?.Value<int>() ?? -1;
                bool persistKb  = inputMode >= 0;
                if (!persistKb) inputMode = _core.Settings.VrInputMode;

                _core.Settings.VroAttachLeft    = left;
                _core.Settings.VroAttachHand    = hand;
                _core.Settings.VroPosX = px; _core.Settings.VroPosY = py; _core.Settings.VroPosZ = pz;
                _core.Settings.VroRotX = rx; _core.Settings.VroRotY = ry; _core.Settings.VroRotZ = rz;
                _core.Settings.VroWidth         = width;
                _core.Settings.VroKeybindMode   = kbMode;
                if (persistKb && inputMode == 1)
                {
                    _core.Settings.VroIdxKeybind       = kb;
                    _core.Settings.VroIdxKeybindHand   = kbHand;
                    _core.Settings.VroIdxKeybindDt     = kbDt;
                    _core.Settings.VroIdxKeybindDtHand = kbDtHand;
                }
                else if (persistKb)
                {
                    _core.Settings.VroKeybind       = kb;
                    _core.Settings.VroKeybindHand   = kbHand;
                    _core.Settings.VroKeybindDt     = kbDt;
                    _core.Settings.VroKeybindDtHand = kbDtHand;
                }
                _core.Settings.VroControlRadius = ctrlR;
                _core.Settings.VroDynVis        = dynVis;
                _core.Settings.VroFocusRadius   = focusR;
                _core.Settings.VroSeamless      = seamless;
                if (persistKb && inputMode != _core.Settings.VrInputMode)
                {
                    _core.Settings.VrInputMode = inputMode;
                    _core.VrOverlay?.ApplyInputMode(inputMode);
                }
                _core.Settings.Save();

                _core.VrOverlay?.VroConfig(left, hand, px, py, pz, rx, ry, rz, width,
                    kb, kbHand, kbMode, kbDt, kbDtHand, ctrlR, dynVis, focusR, seamless);
                break;
            }

            case "vroAutoSave":
                _core.Settings.VroAutoStart   = msg["autoStart"]?.Value<bool>()   ?? false;
                _core.Settings.VroAutoStartVR = msg["autoStartVR"]?.Value<bool>() ?? false;
                _core.Settings.Save();
                break;

            case "vroRecordKeybind":
                _core.VrOverlay?.VroRecordKeybind();
                break;

            case "vroCancelRecording":
                _core.VrOverlay?.VroCancelRecording();
                break;

            case "vroWaterConfig":
            {
                _core.Settings.VroWaterEnabled = msg["enabled"]?.Value<bool>() ?? false;
                _core.Settings.VroWaterHours   = msg["hours"]?.Value<int>()    ?? 1;
                _core.Settings.VroWaterMinutes = msg["minutes"]?.Value<int>()  ?? 0;
                _core.Settings.Save();
                int intervalSec = _core.Settings.VroWaterHours * 3600 + _core.Settings.VroWaterMinutes * 60;
                _core.VrOverlay?.VroWaterConfig(_core.Settings.VroWaterEnabled, intervalSec);
                break;
            }

            case "vroToastConfig":
            {
                bool enabled    = msg["enabled"]?.Value<bool>()    ?? true;
                bool favOnly    = msg["favOnly"]?.Value<bool>()    ?? false;
                int  size       = msg["size"]?.Value<int>()        ?? 50;
                float offX      = msg["offsetX"]?.Value<float>()   ?? 0f;
                float offY      = msg["offsetY"]?.Value<float>()   ?? -0.12f;
                bool online     = msg["online"]?.Value<bool>()     ?? true;
                bool offline    = msg["offline"]?.Value<bool>()    ?? true;
                bool gps        = msg["gps"]?.Value<bool>()        ?? true;
                bool status     = msg["status"]?.Value<bool>()     ?? true;
                bool statusDesc = msg["statusDesc"]?.Value<bool>() ?? true;
                bool bio        = msg["bio"]?.Value<bool>()        ?? true;
                int  duration   = msg["duration"]?.Value<int>()    ?? 8;
                int  stack      = msg["stack"]?.Value<int>()       ?? 2;
                bool friendReq  = msg["friendReq"]?.Value<bool>()  ?? true;
                bool invite     = msg["invite"]?.Value<bool>()     ?? true;
                bool groupInv   = msg["groupInv"]?.Value<bool>()   ?? true;
                bool joined     = msg["joined"]?.Value<bool>()     ?? true;

                _core.Settings.VroToastTtsOnline = msg["ttsOnline"]?.Value<bool>() ?? false;
                _core.Settings.VroToastTtsOffline = msg["ttsOffline"]?.Value<bool>() ?? false;
                _core.Settings.VroToastTtsGps = msg["ttsGps"]?.Value<bool>() ?? false;
                _core.Settings.VroToastTtsStatus = msg["ttsStatus"]?.Value<bool>() ?? false;
                _core.Settings.VroToastTtsStatusDesc = msg["ttsStatusDesc"]?.Value<bool>() ?? false;
                _core.Settings.VroToastTtsBio = msg["ttsBio"]?.Value<bool>() ?? false;
                _core.Settings.VroToastTtsFriendReq = msg["ttsFriendReq"]?.Value<bool>() ?? false;
                _core.Settings.VroToastTtsInvite = msg["ttsInvite"]?.Value<bool>() ?? false;
                _core.Settings.VroToastTtsGroupInv = msg["ttsGroupInv"]?.Value<bool>() ?? false;
                _core.Settings.VroToastTtsJoined = msg["ttsJoined"]?.Value<bool>() ?? false;
                if (VRCNext.Services.Helpers.AudioDeviceManager.TryReadSelectionFromMessage(msg["ttsDeviceId"], msg["ttsDeviceName"]?.ToString(), false, _core.Settings.VroTtsDeviceName, out var ttsId, out var ttsName))
                {
                    _core.Settings.VroTtsDeviceId = ttsId;
                    _core.Settings.VroTtsDeviceName = ttsName;
                }
                _core.Settings.VroTtsVoice  = msg["ttsVoice"]?.ToString() ?? "";
                _core.Settings.VroTtsEngine = msg["ttsEngine"]?.ToString() ?? "sapi";
                _core.Settings.VroToastEnabled    = enabled;
                _core.Settings.VroToastFavOnly    = favOnly;
                _core.Settings.VroToastSize       = size;
                _core.Settings.VroToastOffsetX    = offX;
                _core.Settings.VroToastOffsetY    = offY;
                _core.Settings.VroToastOnline     = online;
                _core.Settings.VroToastOffline    = offline;
                _core.Settings.VroToastGps        = gps;
                _core.Settings.VroToastStatus     = status;
                _core.Settings.VroToastStatusDesc = statusDesc;
                _core.Settings.VroToastBio        = bio;
                _core.Settings.VroToastDuration   = Math.Clamp(duration, 2, 10);
                _core.Settings.VroToastStack      = Math.Clamp(stack, 1, 4);
                _core.Settings.VroToastFriendReq  = friendReq;
                _core.Settings.VroToastInvite     = invite;
                _core.Settings.VroToastGroupInv   = groupInv;
                _core.Settings.VroToastJoined     = joined;
                _core.Settings.Save();

                _core.VrOverlay?.VroApplyToastConfig(enabled, favOnly, size, offX, offY,
                    online, offline, gps, status, statusDesc, bio, duration, stack,
                    friendReq, invite, groupInv, joined);
                break;
            }

            case "vroScaleConfig":
            {
                bool scEnabled   = msg["enabled"]?.Value<bool>()     ?? true;
                bool leftThumb   = msg["leftThumb"]?.Value<bool>()   ?? false;
                bool rightThumb  = msg["rightThumb"]?.Value<bool>()  ?? true;
                var  kb          = msg["keybind"]?.ToObject<List<uint>>() ?? new();
                int  kbHand      = msg["keybindHand"]?.Value<int>()  ?? 0;
                int  sensitivity = msg["scrollSensitivity"]?.Value<int>() ?? 25;
                bool scPersist   = (msg["inputMode"]?.Value<int>() ?? -1) >= 0;

                _core.Settings.VroScaleEnabled            = scEnabled;
                _core.Settings.VroScaleLeftThumb          = leftThumb;
                _core.Settings.VroScaleRightThumb         = rightThumb;
                _core.Settings.VroScaleScrollSensitivity  = sensitivity;
                if (scPersist && _core.Settings.VrInputMode == 1)
                {
                    _core.Settings.VroIdxScaleKeybind     = kb;
                    _core.Settings.VroIdxScaleKeybindHand = kbHand;
                }
                else if (scPersist)
                {
                    _core.Settings.VroScaleKeybind     = kb;
                    _core.Settings.VroScaleKeybindHand = kbHand;
                }
                _core.Settings.Save();

                _core.VrOverlay?.VroScaleConfig(scEnabled, leftThumb, rightThumb, kb, kbHand,
                    _core.Settings.AsScale, sensitivity);
                break;
            }

            case "vroRecordScaleKeybind":
                _core.VrOverlay?.VroRecordScaleKeybind();
                break;

            case "vroCancelScaleRecording":
                _core.VrOverlay?.VroCancelScaleRecording();
                break;
        }
    }

    public bool ShouldSpeakToast(string evType) => evType switch
    {
        "friend_online"     => _core.Settings.VroToastTtsOnline,
        "friend_offline"    => _core.Settings.VroToastTtsOffline,
        "friend_gps"        => _core.Settings.VroToastTtsGps,
        "friend_status"     => _core.Settings.VroToastTtsStatus,
        "friend_statusdesc" => _core.Settings.VroToastTtsStatusDesc,
        "friend_bio"        => _core.Settings.VroToastTtsBio,
        "notif_friendreq"   => _core.Settings.VroToastTtsFriendReq,
        "notif_invite"      => _core.Settings.VroToastTtsInvite,
        "notif_groupinvite" => _core.Settings.VroToastTtsGroupInv,
        "friend_joined"     => _core.Settings.VroToastTtsJoined,
        _                   => false,
    };

    public void SpeakToast(string evType, string friendName, string evText)
    {
        if (!ShouldSpeakToast(evType)) return;
        var line = string.IsNullOrWhiteSpace(evText) ? friendName : $"{friendName} {evText}";
        VRCNext.Services.Helpers.TtsService.Speak(
            line, _core.Settings.VroTtsEngine, _core.Settings.VroTtsVoice,
            VRCNext.Services.Helpers.AudioSelection.From(_core.Settings.VroTtsDeviceId, _core.Settings.VroTtsDeviceName), 100, 0);
    }

    public void UpdateToolStates()
    {
        if (_core.VrOverlay == null) return;
        var states = GetToolStates?.Invoke() ?? default;
        _core.VrOverlay.SetToolStates(states.discord, states.voice, states.kikitan, states.space, states.relay, states.chatbox, states.frameShot);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed  = true;
        _wiredHost = null;
        _core.VrOverlay?.Dispose();
        _core.VrOverlay = null;
    }

    private static void Invoke(Action action) => action();
}
#else
namespace VRCNext;

// Stub for non-Windows platforms — all methods are no-ops.
public class VROverlayController : IDisposable
{
    public Action<int>? OnToolToggle { get; set; }
    public Func<(bool discord, bool voice, bool kikitan, bool space, bool relay, bool chatbox, bool frameShot)>? GetToolStates { get; set; }

    public VROverlayController(CoreLibrary core, FriendsController friends) { }
    public Task HandleMessage(string action, Newtonsoft.Json.Linq.JObject msg) => Task.CompletedTask;
    public void UpdateToolStates() { }
    public void Dispose() { }
}
#endif
