#if WINDOWS
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Valve.VR;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Media.Control;
using VRCNext.Services.Helpers;
using VRCNext.Services.VrDraw;
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;
using PointF = System.Drawing.PointF;
using Rectangle = System.Drawing.Rectangle;
using RectangleF = System.Drawing.RectangleF;
using Bitmap = VRCNext.Services.VrDraw.VrBitmap;
using Graphics = VRCNext.Services.VrDraw.D2DGraphics;
using Font = VRCNext.Services.VrDraw.Font;
using FontFamily = VRCNext.Services.VrDraw.FontFamily;
using FontStyle = VRCNext.Services.VrDraw.FontStyle;
using GraphicsUnit = VRCNext.Services.VrDraw.GraphicsUnit;
using Brush = VRCNext.Services.VrDraw.Brush;
using SolidBrush = VRCNext.Services.VrDraw.SolidBrush;
using Pen = VRCNext.Services.VrDraw.Pen;
using StringFormat = VRCNext.Services.VrDraw.StringFormat;
using StringAlignment = VRCNext.Services.VrDraw.StringAlignment;
using StringTrimming = VRCNext.Services.VrDraw.StringTrimming;
using StringFormatFlags = VRCNext.Services.VrDraw.StringFormatFlags;
using InterpolationMode = VRCNext.Services.VrDraw.InterpolationMode;
using D2DTarget = Vortice.Direct2D1.ID2D1Bitmap1;

namespace VRCNext.Services
{
    public class VROverlayService : IDisposable
    {
        // Config
        public bool AttachToLeft   { get; set; } = true;
        public bool AttachToHand   { get; set; } = true;
        public float PosX          { get; set; } = 0.0f;
        public float PosY          { get; set; } = 0.07f;
        public float PosZ          { get; set; } = -0.05f;
        public float RotX          { get; set; } = -80f;
        public float RotY          { get; set; } = 0f;
        public float RotZ          { get; set; } = 0f;
        public float WidthMeters   { get; set; } = 0.22f;
        public List<uint> Keybind       { get; private set; } = new();
        public int        KeybindHand   { get; private set; } = 0; // 0=any, 1=left, 2=right
        public int        KeybindMode   { get; private set; } = 0; // 0=combo(hold), 1=doubletap
        public List<uint> KeybindDt     { get; private set; } = new();
        public int        KeybindDtHand { get; private set; } = 0; // 0=any, 1=left, 2=right (doubletap slot)

        // State
        public bool IsConnected    { get; private set; }
        public bool IsVisible      { get; private set; }
        public bool IsRecording    { get; private set; }
        public string? LastError   { get; private set; }

        // Events
        public event Action<object>? OnStateUpdate;
        public event Action<List<uint>, List<string>, int, int>? OnKeybindRecorded; // (ids, names, hand, mode)
        public event Action<int>? OnToolToggle;
        public event Action<string, string>? OnJoinRequest;    // (friendId, location) — join friend's instance
        public event Action<string>?         OnInviteFriend;  // (friendId) — invite friend to MY instance
        public event Action<string, string, string, string>? OnNotifAccept; // (notifId, notifType, senderId, notifData)

        // OpenVR handles
        private volatile CVRSystem? _vrSystem;
        private bool _ownedInit;
        private ulong _overlayHandle;

        // Poll loop
        private CancellationTokenSource? _cts;
        private Task? _pollTask;
        private bool _running;
        private bool _disposed;
        private readonly Action<string> _log;

        // Controller tracking
        private uint _leftIdx  = OpenVR.k_unTrackedDeviceIndexInvalid;
        private uint _rightIdx = OpenVR.k_unTrackedDeviceIndexInvalid;
        private readonly TrackedDevicePose_t[] _poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];

        private float _fps = 0f;
        private uint _lastFrameIndex;
        private double _lastFrameTime;
        private float _hmdBattery = -1f;
        private float _leftBattery = -1f;
        private float _rightBattery = -1f;

        // Keybind recording
        private ulong _lastPressedButtons;
        private int   _stableFrames;
        private const int STABLE_FRAMES_REQUIRED = 25; // ~275ms at 11ms poll

        // Event-driven button state — updated from VREvent_ButtonPress/Unpress,
        private ulong _eventButtonsHeld = 0;
        private ulong _eventLeftHeld    = 0; 
        private ulong _eventRightHeld   = 0;
        private bool  _keybindTriggered = false;
        private int   _keybindReleaseFrames = 0;
        private const int KEYBIND_RELEASE_REQUIRED = 8;
        // Double-tap state
        private ulong    _prevTriggerHeld      = 0;
        private uint     _doubleTapLastButton  = uint.MaxValue;
        private DateTime _doubleTapLastTime    = DateTime.MinValue;

        private ID3D11Device?        _d3dDevice;
        private ID3D11DeviceContext? _d3dContext;
        private ID3D11Texture2D?     _overlayTex;
        private D2DRenderer?         _d2d;
        private D2DTarget?           _overlayTarget;
        private readonly object      _renderLock = new();

        // Rendering
        private const int W = 512;
        private const int H = 384;
        private const int ContentVShift = (H - 384) / 2 > 0 ? (H - 384) / 2 : 0;
        private const int MusicArtSize  = 128;
        private const int MusicArtY     = 68 + 10 + ContentVShift;
        private const int MusicBarY     = MusicArtY + MusicArtSize + 62;
        private const int MusicBarH     = 6;
        private const int MusicCtrlCY   = MusicBarY + MusicBarH + 38;
        private const int MusicPlayR    = 26;
        private const int TabBarBottom  = 60;
        private const int HeaderH = 58;
        private const int TexH = H + HeaderH;
        private const int RenderScale = 2; // render at 2× resolution for sharper overlay
        // SMTC poll — query media session every ~3 s (270 × 11 ms)
        private int  _smtcTick = 0;
        private bool _smtcPolling = false;
        private const int SMTC_POLL_INTERVAL = 270;
        // Local position interpolation — avoids re-rendering every second
        private double   _mediaPositionAtPoll = 0;
        private DateTime _mediaLastPollTime   = DateTime.MinValue;
        private int      _lastDisplayedSecond = -1;
        // Cached SMTC session for media control commands
        private GlobalSystemMediaTransportControlsSession? _smtcSession;
        // Track last controller index that a valid transform was applied for
        private uint _lastTransformIdx = OpenVR.k_unTrackedDeviceIndexInvalid;

        // Profile image cache (notification avatars)
        private readonly Dictionary<string, Bitmap?> _notifImgCache = new();

        // Join button cooldowns (friendId → click time)
        private readonly Dictionary<string, DateTime> _joinCooldowns = new();

        // Material Symbols Rounded font (downloaded once, used for tool icons)
        private FontFamily? _matSymFamily;

        // Album art (SMTC thumbnail)
        private Bitmap? _albumArt;

        // Proximity interaction
        private bool  _interactMode      = false;
        public float ControlRadius { get; private set; } = 0.28f; // enter dist in metres
        private float InteractEnterDist => Math.Max(0.03f, ControlRadius);
        private float InteractLeaveDist => InteractEnterDist + 0.08f;

        // Seamless pointer (own laser, SteamVR never captures the controller)
        private bool  _seamless   = false;
        private ulong _laserHandle;
        private ulong _dotHandle;
        private bool  _laserShown;
        private bool  _dotShown;
        private bool  _ptrDown;
        private bool  _ptrArmed;
        private float _ptrNY;
        private Vector3 _tipPosL = Vector3.Zero, _tipDirL = -Vector3.UnitZ;
        private Vector3 _tipPosR = Vector3.Zero, _tipDirR = -Vector3.UnitZ;
        private uint  _tipIdxL = OpenVR.k_unTrackedDeviceIndexInvalid;
        private uint  _tipIdxR = OpenVR.k_unTrackedDeviceIndexInvalid;
        private const float LaserThickness = 0.0035f;
        private const int   LaserTexSize   = 32;
        private const int   DotTexSize     = 64;

        private bool  _dynVisEnabled = false;
        public float  FocusRadius { get; private set; } = 0.35f;
        private bool  _inFocus     = true;
        private float _dynVisAlpha = FullAlpha;
        private const float FullAlpha      = 0.97f;
        private const float DynVisMinAlpha = 0f;
        private const float DynVisFalloff  = 0.05f;

        // Overlay content
        private int                   _activeTab = 1;
        private float                 _tabIndicatorX = 0f;
        private readonly List<NotifEntry> _notifications = new();
        private string   _mediaTitle    = "";
        private string   _mediaArtist   = "";
        private double   _mediaDuration = 0;
        private bool     _mediaPlaying  = false;
        private bool     _dirty         = true;

        // Tool states
        private bool _toolDiscord    = false;
        private bool _toolVoice      = false;
        private bool _toolKikitan    = false;
        private bool _toolSpaceFlt   = false;
        private bool _toolRelay      = false;
        private bool _toolChatbox    = false;
        private bool _toolFrameShot  = false;
        private bool _toolSpaceTurn  = false;

        // Tools tab scroll state (analog to _locationScrollY)
        private float _toolsScrollY  = 0f;
        private float _toolsScrollVY = 0f;

        // Toast notification overlay (HMD-attached)
        private ulong _toastHandle;
        private ID3D11Texture2D? _toastOverlayTex;
        private D2DTarget? _toastTarget;
        private const int TW = 420;  
        private const int TH = 72;    
        private const int TH_GAP = 6;   
        private const int MAX_STACK = 4;
        private const int TH_FULL = TH * MAX_STACK + TH_GAP * (MAX_STACK - 1); // max texture height

        // Toast config
        private bool  _toastEnabled    = true;
        private bool  _toastFavOnly    = false;
        private int   _toastSize       = 50;    // 0–100
        private float _toastOffsetX    = 0f;
        private float _toastOffsetY    = -0.12f;
        private bool  _toastOnline     = true;
        private bool  _toastOffline    = true;
        private bool  _toastGps        = true;
        private bool  _toastStatus     = true;
        private bool  _toastStatusDesc = true;
        private bool  _toastBio        = true;
        private bool  _toastFriendReq  = true;
        private bool  _toastInvite     = true;
        private bool  _toastGroupInv   = true;
        private bool  _toastJoined     = true;
        private bool  _toastLeft       = true;
        private bool  _toastReqInvite  = true;

        // Toast animation state
        private record ToastItem(string EvType, string FriendName, string EvText, string Time, string ImageUrl, string FriendId = "");
        private record ActiveToast(ToastItem Item, DateTime StartTime);
        private readonly Queue<ToastItem> _toastQueue = new();
        private readonly List<ActiveToast> _activeToasts = new();
        private int   _toastStackSize  = 2;      // 1–4
        private double _toastVisibleMs = 8000;    // configurable duration
        private const double TOAST_FADE_IN_MS  = 350;
        private const double TOAST_FADE_OUT_MS = 400;
        private double _toastTotalMs => TOAST_FADE_IN_MS + _toastVisibleMs + TOAST_FADE_OUT_MS;
        private bool _toastDirty;

        // Callback to trigger sound playback on JS side
        public event Action? OnToastSound;
        public event Action? OnVRQuit;

        // Water Reminder (Dashboard tab)
        private bool     _waterEnabled     = false;
        private long     _waterIntervalMs  = 3_600_000; // 1 hour default
        private long     _waterRemainMs    = 3_600_000;
        private bool     _waterAlarmActive = false;
        private DateTime _waterLastTick    = DateTime.UtcNow;
        private int      _lastDashSecond   = -1; // for clock tick dirty
        private string   _selfImageUrl     = "";
        private string   _selfStatus       = "offline";
        private string   _language         = "en";
        public event Action? OnWaterAlarm;
        public event Action? OnWaterDismissed;

        // Avatar Scale Tab (tab 6)
        private float      _scaleValue        = 1.0f;
        private bool       _scaleEnabled           = true;
        private bool       _scaleLeftThumb    = false;
        private bool       _scaleRightThumb   = true;
        private List<uint> _scaleKeybind           = new();
        private int        _scaleKeybindHand       = 0;
        private int        _scaleScrollSensitivity = 25;
        private float      _thumbDisplayX          = 0f;
        private float      _thumbDisplayY          = 0f;
        private bool       _isScaleRecording  = false;
        private ulong      _scaleLastPressed  = 0;
        private int        _scaleStableFrames = 0;
        public event Action<float>? OnScaleChange;
        public event Action<List<uint>, List<string>, int>? OnScaleKeybindRecorded;

        public void SetScaleConfig(bool scaleEnabled, bool leftThumb, bool rightThumb, List<uint> keybind, int keybindHand, float currentScale, int scrollSensitivity = 25)
        {
            _scaleEnabled            = scaleEnabled;
            _scaleLeftThumb          = leftThumb;
            _scaleRightThumb         = rightThumb;
            _scaleKeybind            = keybind ?? new();
            _scaleKeybindHand        = keybindHand;
            _scaleValue              = currentScale;
            _scaleScrollSensitivity  = Math.Clamp(scrollSensitivity, 1, 100);
            ClampActiveTab();
            _dirty = true;
        }

        public void SetCurrentScale(float scale)
        {
            _scaleValue = scale;
            if (_activeTab == TabSize) _dirty = true;
        }

        public void StartScaleKeybindRecording()
        {
            _isScaleRecording  = true;
            _scaleLastPressed  = 0;
            _scaleStableFrames = 0;
            EmitState();
        }

        public void StopScaleKeybindRecording()
        {
            _isScaleRecording = false;
            EmitState();
        }

        public void SetLanguage(string lang) => _language = string.IsNullOrWhiteSpace(lang) ? "en" : lang;

        // Embedded VR-overlay UI strings — keys must be lowercase
        private static readonly Dictionary<string, Dictionary<string, string>> _vroStrings = new()
        {
            ["system_time"] = new() {
                ["en"] = "SYSTEM TIME",    ["de"] = "SYSTEMZEIT",
                ["es"] = "HORA SISTEMA",   ["fr"] = "HEURE SYSTÈME",
                ["ja"] = "システム時刻",     ["zh-cn"] = "系统时间",
                ["zh-tw"] = "系統時間",
            },
            ["water_reminder"] = new() {
                ["en"] = "WATER REMINDER", ["de"] = "WASSERALARM",
                ["es"] = "RECORDATORIO",   ["fr"] = "RAPPEL D'EAU",
                ["ja"] = "水分補給",         ["zh-cn"] = "喝水提醒",
                ["zh-tw"] = "喝水提醒",
            },
            ["active"] = new() {
                ["en"] = "ACTIVE",  ["de"] = "AKTIV",  ["es"] = "ACTIVO",
                ["fr"] = "ACTIF",   ["ja"] = "有効",    ["zh-cn"] = "活跃",
                ["zh-tw"] = "活躍",
            },
            ["min"] = new() {
                ["en"] = "MIN", ["de"] = "MIN", ["es"] = "MIN",
                ["fr"] = "MIN", ["ja"] = "分",  ["zh-cn"] = "分",
                ["zh-tw"] = "分",
            },
            ["sec"] = new() {
                ["en"] = "SEC", ["de"] = "SEK", ["es"] = "SEG",
                ["fr"] = "SEC", ["ja"] = "秒",  ["zh-cn"] = "秒",
                ["zh-tw"] = "秒",
            },
            ["recent_notifications"] = new() {
                ["en"] = "RECENT NOTIFICATIONS", ["de"] = "BENACHRICHTIGUNGEN",
                ["es"] = "NOTIFICACIONES",        ["fr"] = "NOTIFICATIONS",
                ["ja"] = "最近の通知",              ["zh-cn"] = "最近通知",
                ["zh-tw"] = "最近通知",
            },
            ["no_notifications"] = new() {
                ["en"] = "No recent notifications", ["de"] = "Keine Benachrichtigungen",
                ["es"] = "Sin notificaciones",       ["fr"] = "Pas de notifications",
                ["ja"] = "通知なし",                   ["zh-cn"] = "暂无通知",
                ["zh-tw"] = "暫無通知",
            },
            ["alarm_title"] = new() {
                ["en"] = "Drink Water!",     ["de"] = "Trink Wasser!",
                ["es"] = "¡Bebe Agua!",      ["fr"] = "Buvez de l'eau !",
                ["ja"] = "水を飲もう！",       ["zh-cn"] = "喝水啦！",
                ["zh-tw"] = "喝水啦！",
            },
            ["alarm_sub"] = new() {
                ["en"] = "Stay hydrated. Stay focused.",  ["de"] = "Trink genug. Bleib fokussiert.",
                ["es"] = "Mantente hidratado.",            ["fr"] = "Restez hydraté.",
                ["ja"] = "水分補給を忘れずに。",             ["zh-cn"] = "保持水分，保持专注。",
                ["zh-tw"] = "保持水分，保持專注。",
            },
            ["alarm_btn"] = new() {
                ["en"] = "I Did Drink!",    ["de"] = "Ich hab getrunken!",
                ["es"] = "¡Ya bebí!",       ["fr"] = "J'ai bu !",
                ["ja"] = "飲んだよ！",        ["zh-cn"] = "我喝了！",
                ["zh-tw"] = "我喝了！",
            },
        };

        private string VroL(string key)
        {
            if (_vroStrings.TryGetValue(key, out var map))
            {
                var lang = _language.ToLowerInvariant();
                if (map.TryGetValue(lang, out var s)) return s;
                if (map.TryGetValue("en", out var fallback)) return fallback;
            }
            return key;
        }

        public void ApplyWaterConfig(bool enabled, long intervalMs)
        {
            _waterEnabled    = enabled;
            _waterIntervalMs = Math.Max(60_000, intervalMs);
            if (!_waterAlarmActive)
                _waterRemainMs = _waterIntervalMs;
            if (enabled && !_waterAlarmActive)
                _waterLastTick = DateTime.UtcNow;
            _dirty = true;
        }

        public void DismissWaterAlarm()
        {
            _waterAlarmActive = false;
            _waterRemainMs    = _waterIntervalMs;
            _waterLastTick    = DateTime.UtcNow;
            _dirty = true;
            OnWaterDismissed?.Invoke();
        }

        public void SetKikitanState(string sourceText, string translatedText, bool isFinal,
                                    string sourceLang, string targetLang, string engine, bool translateEnabled)
        {
            _kxSource      = sourceText ?? "";
            _kxTranslation = translatedText ?? "";
            _kxFinal       = isFinal;
            _kxSourceLang  = string.IsNullOrWhiteSpace(sourceLang) ? "Auto" : sourceLang;
            _kxTargetLang  = string.IsNullOrWhiteSpace(targetLang) ? "" : targetLang;
            _kxEngine      = string.IsNullOrWhiteSpace(engine) ? "" : engine;
            _kxTranslate   = translateEnabled;
            if (_activeTab == TabKikitan) _dirty = true;
        }

        public void SetToolStates(bool discord, bool voiceFight, bool kikitan, bool spaceFlight, bool relay, bool chatbox, bool frameShot, bool spaceTurn = false)
        {
            _toolDiscord    = discord;
            _toolVoice      = voiceFight;
            _toolKikitan    = kikitan;
            _toolSpaceFlt   = spaceFlight;
            _toolRelay      = relay;
            _toolChatbox    = chatbox;
            _toolFrameShot  = frameShot;
            _toolSpaceTurn  = spaceTurn;
            ClampActiveTab();
            _dirty = true;
        }

        public void ApplyToastConfig(bool enabled, bool favOnly, int size, float offX, float offY,
            bool online, bool offline,
            bool gps, bool status, bool statusDesc, bool bio,
            int durationSec = 8, int stackSize = 2,
            bool friendReq = true, bool invite = true, bool groupInv = true, bool joined = true,
            bool left = true, bool reqInvite = true)
        {
            bool wasEnabled = _toastEnabled;
            _toastEnabled    = enabled;
            _toastFavOnly    = favOnly;
            _toastSize       = Math.Clamp(size, 0, 100);
            _toastOffsetX    = offX;
            _toastOffsetY    = offY;
            _toastOnline     = online;
            _toastOffline    = offline;
            _toastGps        = gps;
            _toastStatus     = status;
            _toastStatusDesc = statusDesc;
            _toastBio        = bio;
            _toastFriendReq  = friendReq;
            _toastInvite     = invite;
            _toastGroupInv   = groupInv;
            _toastJoined     = joined;
            _toastLeft       = left;
            _toastReqInvite  = reqInvite;
            _toastVisibleMs  = Math.Clamp(durationSec, 2, 10) * 1000.0;
            int newStack     = Math.Clamp(stackSize, 1, MAX_STACK);

            // If stack size reduced, dismiss toasts in excess slots
            if (newStack < _toastStackSize)
            {
                while (_activeToasts.Count > newStack)
                    _activeToasts.RemoveAt(_activeToasts.Count - 1);
            }
            _toastStackSize = newStack;

            // Reapply overlay width based on size % (0.10m at 0%, 0.30m at 100%)
            if (_toastHandle != 0 && OpenVR.Overlay != null)
            {
                OpenVR.Overlay.SetOverlayWidthInMeters(_toastHandle, 0.10f + _toastSize * 0.002f);

                // If just disabled, immediately hide active toasts and clear queue
                if (wasEnabled && !enabled)
                {
                    if (_activeToasts.Count > 0)
                    {
                        _activeToasts.Clear();
                        OpenVR.Overlay.SetOverlayAlpha(_toastHandle, 0f);
                        OpenVR.Overlay.HideOverlay(_toastHandle);
                        _toastDirty = false;
                    }
                    lock (_toastQueue) _toastQueue.Clear();
                }
            }

            // Reapply position when offset changes
            if (_toastHandle != 0 && IsConnected) ApplyToastTransform();
        }

        private bool ShouldShowToast(string evType) => evType switch
        {
            "friend_online"      => _toastOnline,
            "friend_offline"     => _toastOffline,
            "friend_gps"         => _toastGps,
            "friend_status"      => _toastStatus,
            "friend_statusdesc"  => _toastStatusDesc,
            "friend_bio"         => _toastBio,
            "notif_friendreq"    => _toastFriendReq,
            "notif_invite"       => _toastInvite,
            "notif_groupinvite"  => _toastGroupInv,
            "friend_joined"      => _toastJoined,
            "friend_left"        => _toastLeft,
            "notif_requestinvite" => _toastReqInvite,
            "notif_actionflow"   => true,
            _                    => false,
        };

        // Per-friend cooldown: only one toast per friend within this window
        private readonly Dictionary<string, DateTime> _toastFriendCooldown = new();
        private const double TOAST_FRIEND_COOLDOWN_MS = 2000; // 2 seconds — blocks WebSocket rapid-fire but allows real events

        /// <summary>Called from AppShell after AddNotification returns isNew=true.</summary>
        public void EnqueueToast(string evType, string friendName, string evText, string time, string imageUrl, bool isFavorited, string friendId = "")
        {
            // Global enable
            if (!_toastEnabled || !IsConnected) return;

            // Per-event-type filter
            if (!ShouldShowToast(evType)) return;

            // Favorites-only filter (skip for VRChat notification types — they're not friend events)
            if (_toastFavOnly && !isFavorited && !evType.StartsWith("notif_")) return;

            // Skip friend_gps with empty world name — the async update with the real name will follow
            if (evType == "friend_gps" && (evText == "→ a world" || string.IsNullOrWhiteSpace(evText))) return;

            // Per-friend cooldown: max one toast per friend within the cooldown window
            if (evType != "notif_actionflow")
            {
                lock (_toastFriendCooldown)
                {
                    var now = DateTime.UtcNow;
                    if (_toastFriendCooldown.TryGetValue(friendName, out var last) &&
                        (now - last).TotalMilliseconds < TOAST_FRIEND_COOLDOWN_MS)
                        return;
                    _toastFriendCooldown[friendName] = now;

                    // Cleanup old entries
                    if (_toastFriendCooldown.Count > 50)
                    {
                        var expired = new List<string>();
                        foreach (var kv in _toastFriendCooldown)
                            if ((now - kv.Value).TotalMilliseconds > TOAST_FRIEND_COOLDOWN_MS) expired.Add(kv.Key);
                        foreach (var k in expired) _toastFriendCooldown.Remove(k);
                    }
                }
            }

            lock (_toastQueue)
            {
                _toastQueue.Enqueue(new ToastItem(evType, friendName, evText, time, imageUrl, friendId));
            }
        }

        // Allowed buttons & keybind limits
        private const ulong ALLOWED_BUTTON_MASK =
            (1UL << 1) | (1UL << 2) | (1UL << 7) | (1UL << 32) | (1UL << 33);
        private const int MAX_KEYBIND_BUTTONS  = 4;
        private const int DOUBLE_TAP_WINDOW_MS = 400;

        // Button name maps
        private static readonly Dictionary<uint, string> ButtonNames = new()
        {
            { (uint)EVRButtonId.k_EButton_ApplicationMenu, "B/Y"       },
            { (uint)EVRButtonId.k_EButton_Grip,            "Grip"      },
            { (uint)EVRButtonId.k_EButton_A,               "A/X"       },
            { (uint)EVRButtonId.k_EButton_Axis0,           "Stick"     },
            { (uint)EVRButtonId.k_EButton_Axis1,           "Trigger"   },
        };

        private static ulong ActiveButtonMask
            => VrInputActions.Active ? VrInputActions.AllowedMask : ALLOWED_BUTTON_MASK;

        private static ulong RecordButtonMask
            => VrInputActions.Active ? VrInputActions.RecordMask : ALLOWED_BUTTON_MASK;

        private static string ButtonLabel(uint id)
        {
            if (VrInputActions.Active)
                return VrInputActions.ButtonNames.TryGetValue(id, out var sn) ? sn : $"Button{id}";
            return ButtonNames.TryGetValue(id, out var n) ? n : $"Button{id}";
        }

        private record NotifEntry(string EvType, string FriendName, string EvText, string Time, string ImageUrl = "", string FriendId = "", string Location = "", string NotifId = "", string NotifData = "");

        // Location tab
        private record LocationEntry(string WorldId, string InstanceId, string WorldName, string WorldImageUrl, string FriendId, string FriendName, string FriendImageUrl, string Location);
        private readonly List<LocationEntry>         _friendLocations  = new();
        private readonly Dictionary<string, Bitmap?> _locationImgCache = new(); 
        // Scroll state — replaces integer page fields
        private float _locationScrollY  = 0f;
        private float _locationScrollVY = 0f;
        private string? _openWorldKey   = null;
        private float _wdAnim           = 0f;
        private float _wdTarget         = 0f;
        private float _notifScrollY     = 0f;
        private float _notifScrollVY    = 0f;
        private string _kxSource        = "";
        private string _kxTranslation   = "";
        private string _kxSourceLang    = "Auto";
        private string _kxTargetLang    = "";
        private string _kxEngine        = "";
        private bool   _kxFinal         = true;
        private bool   _kxTranslate     = false;
        private float _friendsScrollY   = 0f;
        private float _friendsScrollVY  = 0f;

        // Drag tracking (for scroll gesture)
        private bool  _mouseDown       = false;
        private float _mouseDownNX     = 0f;
        private float _mouseDownNY     = 0f;
        private bool  _scrollDragging  = false;
        private float _scrollLastNY    = 0f;
        private float _scrollLastDeltaY = 0f; 

        // Friends tab (online/in-game friends list)
        private record FriendTabEntry(string FriendId, string FriendName, string FriendImageUrl, string Status, string StatusDescription, string Location, string WorldName);
        private readonly List<FriendTabEntry> _onlineFriends = new();

        // Location layout constants (shared by Draw + Click)
        private const int LocPadX          = 12;
        private const int LocColGap        = 6;
        private const int LocRowGap        = 6;
        private const int LocCardH         = 68;
        private const int LocContentY      = 72;
        private static int LocColW         => (W - 2 * LocPadX - LocColGap) / 2; // = 241

        // Friends tab layout constants
        private const int FrdPadX     = 12;
        private const int FrdCardH    = 50;
        private const int FrdGap      = 6;
        private const int FrdContentY = 72;

        // Shared scroll area (used by both location + friends tabs)
        private const int ScrollContentBottom = H - 12;
        private const int ScrollContentH      = ScrollContentBottom - LocContentY;
        private const int ScrollBarW          = 3;

        private const int TabAlerts = 1, TabLocation = 2, TabMusic = 3, TabTools = 4,
                          TabFriends = 5, TabKikitan = 6, TabSize = 7;

        private List<int> VisibleTabs()
        {
            var t = new List<int> { TabAlerts, TabLocation, TabMusic, TabTools, TabFriends };
            if (_toolKikitan) t.Add(TabKikitan);
            if (_scaleEnabled) t.Add(TabSize);
            return t;
        }

        private void ClampActiveTab()
        {
            if (!VisibleTabs().Contains(_activeTab)) { _activeTab = TabAlerts; _dirty = true; }
        }

        // Theme colors
        private OverlayTheme _theme = OverlayTheme.FromName("vrcn");

        // called from JS applyColors, handles both named themes and auto color
        public void SetThemeColors(Dictionary<string, string> colors)
        {
            _theme = OverlayTheme.FromColors(colors);
            ApplyPointerTint();
            _dirty = true;
        }

        public readonly struct OverlayTheme
        {
            public Color BgCard  { get; init; }
            public Color BgHover { get; init; }
            public Color Accent  { get; init; }
            public Color Ok      { get; init; }
            public Color Warn    { get; init; }
            public Color Err     { get; init; }
            public Color Cyan    { get; init; }
            public Color Tx1     { get; init; }
            public Color Tx2     { get; init; }
            public Color Tx3     { get; init; }
            public Color Brd     { get; init; }

            public static OverlayTheme FromName(string n) =>
                _palettes.TryGetValue(n ?? "vrcn", out var t) ? t : _palettes["vrcn"];

            public static OverlayTheme FromColors(Dictionary<string, string> c)
            {
                Color G(string k) => c.TryGetValue(k, out var v) ? H(v) : Color.Transparent;
                return new OverlayTheme
                {
                    BgCard  = G("bg-card"),
                    BgHover = G("bg-hover"),
                    Accent  = G("accent"),
                    Ok      = G("ok"),
                    Warn    = G("warn"),
                    Err     = G("err"),
                    Cyan    = G("cyan"),
                    Tx1     = G("tx1"),
                    Tx2     = G("tx2"),
                    Tx3     = G("tx3"),
                    Brd     = G("brd"),
                };
            }

            private static Color H(string hex) =>
                Color.FromArgb(255,
                    Convert.ToInt32(hex[1..3], 16),
                    Convert.ToInt32(hex[3..5], 16),
                    Convert.ToInt32(hex[5..7], 16));

            private static readonly Dictionary<string, OverlayTheme> _palettes = new()
            {
                ["vrcn"]          = new() { BgCard=H("#0F0F0F"),BgHover=H("#1C1C1F"),Accent=H("#8E8EA7"),Ok=H("#2DD48C"),Warn=H("#FFBA37"),Err=H("#FF4B55"),Cyan=H("#8CA5FF"),Tx1=H("#A8A8B8"),Tx2=H("#9B9CAA"),Tx3=H("#60606F"),Brd=H("#1C1C1F") },
                ["blood"]     = new() { BgCard=H("#190F26"),BgHover=H("#251936"),Accent=H("#DF2A4E"),Ok=H("#2DD48C"),Warn=H("#FFBA37"),Err=H("#FF4B55"),Cyan=H("#DC7A56"),Tx1=H("#D2CCDB"),Tx2=H("#D2CCDB"),Tx3=H("#D2CCDB"),Brd=H("#291B3C") },
                ["halloween"] = new() { BgCard=H("#110F26"),BgHover=H("#1B1936"),Accent=H("#DF462A"),Ok=H("#2DD48C"),Warn=H("#FFBA37"),Err=H("#FF4B55"),Cyan=H("#DCA956"),Tx1=H("#F0EFF5"),Tx2=H("#F0EFF5"),Tx3=H("#F0EFF5"),Brd=H("#1E1B3C") },
                ["miku"]      = new() { BgCard=H("#080D14"),BgHover=H("#66B4D2"),Accent=H("#66B4D2"),Ok=H("#2DD48C"),Warn=H("#FFBA37"),Err=H("#FF4B55"),Cyan=H("#66B4D2"),Tx1=H("#FFFFFF"),Tx2=H("#FFFFFF"),Tx3=H("#FFFFFF"),Brd=H("#13223F") },
                ["vrchat"]    = new() { BgCard=H("#181B1F"),BgHover=H("#042E39"),Accent=H("#0B748E"),Ok=H("#18A86A"),Warn=H("#D4860A"),Err=H("#D93040"),Cyan=H("#53C0D5"),Tx1=H("#FFFFFF"),Tx2=H("#FFFFFF"),Tx3=H("#FFFFFF"),Brd=H("#042E39") },
                ["copper"]    = new() { BgCard=H("#151517"),BgHover=H("#232326"),Accent=H("#D08A4F"),Ok=H("#46C88C"),Warn=H("#E0A43C"),Err=H("#E05555"),Cyan=H("#8FB4D9"),Tx1=H("#E4DFDA"),Tx2=H("#ABA49D"),Tx3=H("#7C7670"),Brd=H("#232326") },
                ["nature"]    = new() { BgCard=H("#151714"),BgHover=H("#232620"),Accent=H("#8DBF63"),Ok=H("#6ECB86"),Warn=H("#D9A441"),Err=H("#E05C5C"),Cyan=H("#D8926A"),Tx1=H("#E0E4DA"),Tx2=H("#A6AC9E"),Tx3=H("#767C6F"),Brd=H("#232620") },
                ["flippernano"] = new() { BgCard=H("#E7EAF8"),BgHover=H("#FF896F"),Accent=H("#FF896F"),Ok=H("#2BFF00"),Warn=H("#FF7455"),Err=H("#FF2E00"),Cyan=H("#FF896F"),Tx1=H("#494949"),Tx2=H("#494949"),Tx3=H("#494949"),Brd=H("#D3D6E6") },
                ["spaceout"]    = new() { BgCard=H("#0A0714"),BgHover=H("#191327"),Accent=H("#FF9F60"),Ok=H("#2DD48C"),Warn=H("#FFBA37"),Err=H("#FF4B55"),Cyan=H("#8CA5FF"),Tx1=H("#EBEBFF"),Tx2=H("#B7B7C3"),Tx3=H("#FFFFFF"),Brd=H("#1C162C") },
                ["fluffy"]      = new() { BgCard=H("#FAE8FF"),BgHover=H("#FFCCE9"),Accent=H("#DFBFFF"),Ok=H("#2BFF00"),Warn=H("#FF7455"),Err=H("#FF2E00"),Cyan=H("#DCAFFF"),Tx1=H("#49414E"),Tx2=H("#3D3547"),Tx3=H("#3B3441"),Brd=H("#E5D3E6") },
                ["ender"]       = new() { BgCard=H("#0A0714"),BgHover=H("#191327"),Accent=H("#CC60FF"),Ok=H("#2DD48C"),Warn=H("#FFBA37"),Err=H("#FF4B55"),Cyan=H("#8CA5FF"),Tx1=H("#EBEBFF"),Tx2=H("#B7B7C3"),Tx3=H("#FFFFFF"),Brd=H("#1C162C") },
                ["redruby"]       = new() { BgCard=H("#151517"),BgHover=H("#232326"),Accent=H("#D04F4F"),Ok=H("#46C88C"),Warn=H("#E0A43C"),Err=H("#E05555"),Cyan=H("#D9A58F"),Tx1=H("#E4DADA"),Tx2=H("#AB9D9D"),Tx3=H("#7C7070"),Brd=H("#232326") },
                ["mates"]         = new() { BgCard=H("#151517"),BgHover=H("#232326"),Accent=H("#4F50D0"),Ok=H("#46C88C"),Warn=H("#E0A43C"),Err=H("#E05555"),Cyan=H("#9E8FD9"),Tx1=H("#DADCE4"),Tx2=H("#9D9EAB"),Tx3=H("#70727C"),Brd=H("#232326") },
            };
        }

        // 

        public VROverlayService(Action<string> log) => _log = log;

        // Public API

        public bool Connect()
        {
            if (IsConnected) return true;
            LastError = null;

            try
            {
                if (OpenVR.System == null)
                {
                    var err = EVRInitError.None;
                    _vrSystem = OpenVR.Init(ref err, EVRApplicationType.VRApplication_Overlay);
                    if (err != EVRInitError.None)
                    {
                        var overlayErr = err;
                        err = EVRInitError.None;
                        try { OpenVR.Shutdown(); } catch { }
                        _vrSystem = OpenVR.Init(ref err, EVRApplicationType.VRApplication_Background);
                        if (err != EVRInitError.None)
                        {
                            _log($"[VROverlay] OpenVR init failed: {err}");
                            var hint = OpenVrInitHint.Describe(overlayErr, err);
                            if (hint != null) _log($"[VROverlay] {hint}");
                            LastError = hint ?? $"OpenVR init failed: {err}";
                            return false;
                        }
                    }
                    _log("[VROverlay] OpenVR initialized");
                }
                else
                {
                    _vrSystem = OpenVR.System;
                    _log("[VROverlay] Reusing existing OpenVR session");
                }
                OpenVRSession.Acquire();
                _ownedInit = true;

                if (VrInputActions.Requested) VrInputActions.Initialize(_log);

                if (OpenVR.Overlay == null)
                {
                    LastError = "IVROverlay not available";
                    _log($"[VROverlay] {LastError}");
                    return false;
                }

                // Create world (non-dashboard) overlay
                var oErr = OpenVR.Overlay.CreateOverlay("vrcnext.wristoverlay", "VRCNext Wrist", ref _overlayHandle);
                if (oErr == EVROverlayError.KeyInUse)
                {
                    OpenVR.Overlay.FindOverlay("vrcnext.wristoverlay", ref _overlayHandle);
                    _log("[VROverlay] Found existing overlay handle");
                }
                else if (oErr != EVROverlayError.None)
                {
                    LastError = $"CreateOverlay: {oErr}";
                    _log($"[VROverlay] {LastError}");
                    return false;
                }

                OpenVR.Overlay.SetOverlayWidthInMeters(_overlayHandle, WidthMeters);
                OpenVR.Overlay.SetOverlayAlpha(_overlayHandle, 0.97f);
                // Start non-interactive; proximity detection switches to Mouse when
                // the free hand gets close to the wrist, then back to None on leave.
                OpenVR.Overlay.SetOverlayInputMethod(_overlayHandle, VROverlayInputMethod.None);
                var mouseScale = new HmdVector2_t { v0 = W, v1 = TexH };
                OpenVR.Overlay.SetOverlayMouseScale(_overlayHandle, ref mouseScale);

                try
                {
                    try
                    {
                        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                            [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
                            out _d3dDevice, out _d3dContext);
                    }
                    catch (Exception hwEx)
                    {
                        _log($"[VROverlay] Hardware D3D11 failed ({hwEx.Message}), trying WARP");
                        D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport,
                            [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
                            out _d3dDevice, out _d3dContext);
                    }

                    var overlayDesc = new Texture2DDescription
                    {
                        Width = W * RenderScale, Height = TexH * RenderScale, MipLevels = 1, ArraySize = 1,
                        Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                    };
                    _overlayTex = _d3dDevice!.CreateTexture2D(overlayDesc);

                    _d2d = new D2DRenderer(_d3dDevice) { Log = _log };
                    _overlayTarget = _d2d.CreateTargetBitmap(_overlayTex);
                    OpenVR.Overlay.SetOverlayFlag(_overlayHandle, VROverlayFlags.IsPremultiplied, true);
                    _log("[VROverlay] D3D11 + Direct2D render target ready");
                }
                catch (Exception ex)
                {
                    LastError = $"D3D11/D2D init failed: {ex.Message}";
                    _log($"[VROverlay] {LastError}");
                    _overlayTarget?.Dispose(); _overlayTarget = null;
                    _d2d?.Dispose();        _d2d        = null;
                    _overlayTex?.Dispose(); _overlayTex = null;
                    _d3dContext?.Dispose(); _d3dContext = null;
                    _d3dDevice?.Dispose();  _d3dDevice  = null;
                    return false;
                }

                // Toast overlay (HMD-attached, separate from wrist overlay).
                var tErr = OpenVR.Overlay.CreateOverlay("vrcnext.toast", "VRCNext Toast", ref _toastHandle);
                if (tErr == EVROverlayError.KeyInUse)
                    OpenVR.Overlay.FindOverlay("vrcnext.toast", ref _toastHandle);
                if (_toastHandle != 0)
                {
                    OpenVR.Overlay.SetOverlayWidthInMeters(_toastHandle, 0.10f + _toastSize * 0.002f);
                    OpenVR.Overlay.SetOverlayAlpha(_toastHandle, 0f); // start invisible
                    OpenVR.Overlay.SetOverlayInputMethod(_toastHandle, VROverlayInputMethod.None);
                    if (_d3dDevice != null && _d2d != null)
                    {
                        try
                        {
                            _toastOverlayTex = _d3dDevice.CreateTexture2D(new Texture2DDescription
                            {
                                Width = TW, Height = TH_FULL, MipLevels = 1, ArraySize = 1,
                                Format = Format.B8G8R8A8_UNorm,
                                SampleDescription = new SampleDescription(1, 0),
                                Usage = ResourceUsage.Default,
                                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                            });
                            _toastTarget = _d2d.CreateTargetBitmap(_toastOverlayTex);
                            OpenVR.Overlay.SetOverlayFlag(_toastHandle, VROverlayFlags.IsPremultiplied, true);
                        }
                        catch (Exception ex)
                        {
                            _log($"[VROverlay] Toast D3D11 init failed: {ex.Message}");
                            _toastTarget = null;
                            _toastOverlayTex?.Dispose(); _toastOverlayTex = null;
                        }
                    }
                    _log($"[VROverlay] Toast overlay created: {tErr}");
                }

                CreatePointerOverlays();

                UpdateControllerIndices();
                ApplyTransform();

                IsConnected = true;
                _dirty = true;
                _log("[VROverlay] Connected");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _log($"[VROverlay] Connect error: {ex.Message}");
                return false;
            }
        }

        public void Disconnect()
        {
            StopPolling();
            if (!IsConnected) return;

            if (_overlayHandle != 0 && OpenVR.Overlay != null)
            {
                try
                {
                    OpenVR.Overlay.HideOverlay(_overlayHandle);
                    OpenVR.Overlay.DestroyOverlay(_overlayHandle);
                }
                catch { }
                _overlayHandle = 0;
            }

            if (_toastHandle != 0 && OpenVR.Overlay != null)
            {
                try
                {
                    OpenVR.Overlay.HideOverlay(_toastHandle);
                    OpenVR.Overlay.DestroyOverlay(_toastHandle);
                }
                catch { }
                _toastHandle = 0;
            }
            DestroyPointerOverlays();

            _toastTarget?.Dispose(); _toastTarget = null;
            _toastOverlayTex?.Dispose(); _toastOverlayTex = null;
            _activeToasts.Clear();
            lock (_toastQueue) _toastQueue.Clear();
            lock (_toastFriendCooldown) _toastFriendCooldown.Clear();

            if (_ownedInit)
            {
                OpenVRSession.Release();
                _ownedInit = false;
            }

            _overlayTarget?.Dispose(); _overlayTarget = null;
            _d2d?.Dispose();        _d2d         = null;
            _overlayTex?.Dispose(); _overlayTex  = null;
            _d3dContext?.Dispose(); _d3dContext   = null;
            _d3dDevice?.Dispose();  _d3dDevice    = null;
            _albumArt?.Dispose();   _albumArt     = null;
            lock (_notifImgCache)
            {
                foreach (var bmp in _notifImgCache.Values) bmp?.Dispose();
                _notifImgCache.Clear();
                foreach (var (bmp, _) in _notifImgGraveyard) { try { bmp.Dispose(); } catch { } }
                _notifImgGraveyard.Clear();
            }
            lock (_locationImgCache)
            {
                foreach (var bmp in _locationImgCache.Values) bmp?.Dispose();
                _locationImgCache.Clear();
            }

            IsConnected = false;
            IsVisible   = false;
            _vrSystem   = null;
            _log("[VROverlay] Disconnected");
        }

        public void StartPolling()
        {
            if (_running) return;
            _cts     = new CancellationTokenSource();
            _running = true;
            _pollTask = PollLoopAsync(_cts.Token);
            _ = Task.Run(EnsureMaterialSymbolsAsync);
            StartVrserverMonitor(_cts.Token);
        }

        // Monitors vrserver.exe with WaitForExitAsync — zero overhead in the poll
        private void StartVrserverMonitor(CancellationToken ct)
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("vrserver");
            if (procs.Length == 0) return;
            var proc = procs[0];
            for (int i = 1; i < procs.Length; i++) procs[i].Dispose();
            _ = Task.Run(async () =>
            {
                try
                {
                    await proc.WaitForExitAsync(ct);
                    if (!ct.IsCancellationRequested && _vrSystem != null)
                    {
                        _log("[VROverlay] vrserver.exe exited — nulling OpenVR interface");
                        _vrSystem = null;
                        _cts?.Cancel();
                    }
                }
                catch { }
                finally { proc.Dispose(); }
            }, ct);
        }

        private async Task EnsureMaterialSymbolsAsync()
        {
            if (_matSymFamily != null) return;
            string cacheDir  = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRCNext");
            string fontPath  = Path.Combine(cacheDir, "MaterialSymbolsRounded.ttf");
            if (!File.Exists(fontPath))
            {
                try
                {
                    Directory.CreateDirectory(cacheDir);
                    using var http  = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    var bytes = await http.GetByteArrayAsync(
                        "https://github.com/google/material-design-icons/raw/master/variablefont/MaterialSymbolsRounded%5BFILL%2CGRAD%2Copsz%2Cwght%5D.ttf");
                    await File.WriteAllBytesAsync(fontPath, bytes);
                    _log("[VROverlay] Downloaded Material Symbols Rounded font");
                }
                catch (Exception ex) { _log($"[VROverlay] Font download failed: {ex.Message}"); return; }
            }
            var fam = D2DRenderer.LoadFontFamily(fontPath, _log);
            if (fam != null)
            {
                _matSymFamily = fam;
                _log($"[VROverlay] Loaded font: {fam.Name}");
                _dirty = true;
            }
        }

        public void StopPolling()
        {
            _running = false;
            _cts?.Cancel();
            // Wait for poll loop to exit before Disconnect disposes resources
            try { _pollTask?.Wait(2000); } catch { }
            _pollTask = null;
        }

        public void Show()
        {
            if (!IsConnected || OpenVR.Overlay == null) return;
            ApplyTransform();
            // Render the first frame BEFORE ShowOverlay so SteamVR never displays
            // a blank/white overlay — this prevents the initial flash/flicker.
            _dirty = true;
            Render();
            OpenVR.Overlay.ShowOverlay(_overlayHandle);
            IsVisible = true;
            EmitState();
        }

        public void Hide()
        {
            if (!IsConnected || OpenVR.Overlay == null) return;
            DisableInteract(); // always exit interact mode when hiding
            OpenVR.Overlay.HideOverlay(_overlayHandle);
            IsVisible = false;
            _lastTransformIdx = OpenVR.k_unTrackedDeviceIndexInvalid; // re-apply on next Show()
            EmitState();
        }

        public void Toggle()
        {
            if (IsVisible) Hide(); else Show();
        }

        public void ApplyConfig(bool attachLeft, bool attachHand,
            float px, float py, float pz,
            float rx, float ry, float rz,
            float width, List<uint> keybind, int keybindHand = 0, int keybindMode = 0,
            List<uint>? keybindDt = null, int keybindDtHand = 0, float controlRadius = 28f,
            bool dynVis = false, float focusRadius = 35f, bool seamless = false)
        {
            if (seamless != _seamless)
            {
                _seamless = seamless;
                DisableInteract();
            }
            AttachToLeft  = attachLeft;
            AttachToHand  = attachHand;
            PosX = px; PosY = py; PosZ = pz;
            RotX = rx; RotY = ry; RotZ = rz;
            WidthMeters   = Math.Clamp(width, 0.05f, 1.0f);
            Keybind       = keybind ?? new();
            KeybindHand   = keybindHand;
            KeybindMode   = keybindMode;
            KeybindDt     = keybindDt ?? new();
            KeybindDtHand = keybindDtHand;
            ControlRadius = Math.Clamp(controlRadius / 100f, 0.03f, 0.28f); // stored in metres
            _dynVisEnabled = dynVis;
            FocusRadius    = Math.Clamp(focusRadius / 100f, 0.20f, 0.60f); // stored in metres

            if (IsConnected && OpenVR.Overlay != null)
            {
                OpenVR.Overlay.SetOverlayWidthInMeters(_overlayHandle, WidthMeters);
                ApplyTransform();
            }
        }

        public void AddNotification(string evType, string friendName, string evText, string time,
            string imageUrl = "", string friendId = "", string location = "",
            string notifId = "", string notifData = "")
        {
            lock (_notifications)
            {
                var entry = new NotifEntry(evType, friendName, evText, time, imageUrl, friendId, location, notifId, notifData);
                _notifications.Insert(0, entry);
                while (_notifications.Count > MaxNotifications) _notifications.RemoveAt(_notifications.Count - 1);
            }
            PruneNotifImageCache();
            if (!string.IsNullOrEmpty(imageUrl))
            {
                var fid = friendId;
                _ = Task.Run(() => EnsureNotifImageAsync(imageUrl, fid));
            }
            _dirty = true;
        }

        public void UpdateNotification(string notifId, string? newText = null, string? newImageUrl = null, string? newFriendName = null)
        {
            if (string.IsNullOrEmpty(notifId)) return;
            string? notifFriendId = null;
            lock (_notifications)
            {
                for (int i = 0; i < _notifications.Count; i++)
                {
                    if (_notifications[i].NotifId != notifId) continue;
                    var e = _notifications[i];
                    notifFriendId = e.FriendId;
                    _notifications[i] = e with
                    {
                        EvText     = newText       ?? e.EvText,
                        ImageUrl   = newImageUrl   ?? e.ImageUrl,
                        FriendName = newFriendName ?? e.FriendName,
                    };
                    break;
                }
            }
            if (!string.IsNullOrEmpty(newImageUrl))
            {
                var fid = notifFriendId ?? "";
                _ = Task.Run(() => EnsureNotifImageAsync(newImageUrl!, fid));
            }
            PruneNotifImageCache();
            _dirty = true;
        }

        private void PruneNotifImageCache()
        {
            var active = new HashSet<string>();
            lock (_notifications)
            {
                foreach (var n in _notifications)
                    if (!string.IsNullOrEmpty(n.ImageUrl)) active.Add(n.ImageUrl);
            }
            lock (_toastQueue)
            {
                foreach (var t in _toastQueue)
                    if (!string.IsNullOrEmpty(t.ImageUrl)) active.Add(t.ImageUrl);
            }
            lock (_activeToasts)
            {
                foreach (var t in _activeToasts)
                    if (!string.IsNullOrEmpty(t.Item.ImageUrl)) active.Add(t.Item.ImageUrl);
            }
            lock (_notifImgCache)
            {
                var now = DateTime.UtcNow;
                for (int i = _notifImgGraveyard.Count - 1; i >= 0; i--)
                {
                    if ((now - _notifImgGraveyard[i].at).TotalSeconds < 5) continue;
                    try { _notifImgGraveyard[i].bmp.Dispose(); } catch { }
                    _notifImgGraveyard.RemoveAt(i);
                }
                if (_notifImgCache.Count == 0) return;
                var stale = _notifImgCache.Keys.Where(k => !active.Contains(k)).ToList();
                foreach (var k in stale)
                {
                    var bmp = _notifImgCache[k];
                    if (bmp != null) _notifImgGraveyard.Add((bmp, now));
                    _notifImgCache.Remove(k);
                }
            }
        }

        private readonly List<(Bitmap bmp, DateTime at)> _notifImgGraveyard = new();

        private async Task EnsureNotifImageAsync(string url, string friendId)
        {
            lock (_notifImgCache) { if (_notifImgCache.TryGetValue(url, out var existing) && existing != null) return; }
            var bmp = await LoadImageFromCacheAsync(url, friendId, "Users");
            if (bmp == null) return;
            lock (_notifImgCache) { _notifImgCache[url] = bmp; }
            _dirty = true;
        }

        private async Task EnsureLocationImageAsync(string url, string entityId, string subdir)
        {
            lock (_locationImgCache) { if (_locationImgCache.TryGetValue(url, out var b) && b != null) return; }
            var bmp = await LoadImageFromCacheAsync(url, entityId, subdir);
            if (bmp == null) return;
            lock (_locationImgCache) { _locationImgCache[url] = bmp; }
            _dirty = true;
        }

        private async Task<Bitmap?> LoadImageFromCacheAsync(string url, string entityId, string subdir)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(entityId)) return null;
            try
            {
                string? localPath = subdir switch
                {
                    "Worlds" => ImageCacheHelper.GetWorldCached(entityId)
                                ?? await ImageCacheHelper.CacheWorldAsync(entityId, url),
                    "Users"  => ImageCacheHelper.GetUserCached(entityId)
                                ?? await ImageCacheHelper.CacheUserAsync(entityId, url),
                    _        => null,
                };
                if (string.IsNullOrEmpty(localPath)) return null;
                return Bitmap.FromFile(localPath);
            }
            catch (Exception ex)
            {
                _log($"[VRO-IMG] {subdir}/{entityId}: {ex.Message}");
                return null;
            }
        }

        // Friend location data (Location tab)

        public void SetFriendLocations(IReadOnlyList<(string worldId, string instanceId, string worldName, string worldImageUrl, string friendId, string friendName, string friendImageUrl, string location)> entries)
        {
            _worldGroupsCache = null;
            lock (_friendLocations)
            {
                _friendLocations.Clear();
                _friendLocations.AddRange(entries.Select(e => new LocationEntry(
                    e.worldId, e.instanceId, e.worldName, e.worldImageUrl,
                    e.friendId, e.friendName, e.friendImageUrl, e.location)));
            }
            // Kick off image downloads for any URL not yet successfully loaded (bitmap == null).
            // No sentinel is written on failure, so retries happen on next SetFriendLocations call.
            foreach (var e in entries)
            {
                var wurl = e.worldImageUrl;
                var furl = e.friendImageUrl;
                var wid  = e.worldId;
                var fid  = e.friendId;
                if (!string.IsNullOrEmpty(wurl))
                {
                    bool needed;
                    lock (_locationImgCache) needed = !_locationImgCache.TryGetValue(wurl, out var b) || b == null;
                    if (needed) _ = Task.Run(() => EnsureLocationImageAsync(wurl, wid, "Worlds"));
                }
                if (!string.IsNullOrEmpty(furl))
                {
                    bool needed;
                    lock (_locationImgCache) needed = !_locationImgCache.TryGetValue(furl, out var b) || b == null;
                    if (needed) _ = Task.Run(() => EnsureLocationImageAsync(furl, fid, "Users"));
                }
            }
            PruneLocationImageCache();
            _dirty = true;
        }


        // Online friends list (Friends tab)

        public void SetOnlineFriends(IReadOnlyList<(string friendId, string friendName, string friendImageUrl, string status, string statusDescription, string location, string worldName)> entries)
        {
            lock (_onlineFriends)
            {
                _onlineFriends.Clear();
                _onlineFriends.AddRange(entries.Select(e => new FriendTabEntry(
                    e.friendId, e.friendName, e.friendImageUrl,
                    e.status, e.statusDescription, e.location, e.worldName)));
            }
            // Kick off image downloads for friend avatars not yet cached
            foreach (var e in entries)
            {
                var furl = e.friendImageUrl;
                var fid  = e.friendId;
                if (!string.IsNullOrEmpty(furl))
                {
                    bool needed;
                    lock (_locationImgCache) needed = !_locationImgCache.TryGetValue(furl, out var b) || b == null;
                    if (needed) _ = Task.Run(() => EnsureLocationImageAsync(furl, fid, "Users"));
                }
            }
            PruneLocationImageCache();
            _dirty = true;
        }

        public void SetSelfUser(string userId, string imageUrl, string status)
        {
            bool hadPrev    = !string.IsNullOrEmpty(_selfImageUrl);
            bool imgChanged = hadPrev && (imageUrl ?? "") != _selfImageUrl;
            _selfImageUrl = imageUrl ?? "";
            _selfStatus   = string.IsNullOrEmpty(status) ? "offline" : status;
            if (!string.IsNullOrEmpty(_selfImageUrl) && !string.IsNullOrEmpty(userId))
            {
                bool cached;
                lock (_locationImgCache) cached = _locationImgCache.TryGetValue(_selfImageUrl, out var b) && b != null;
                if (imgChanged || !cached)
                    _ = Task.Run(() => RefreshSelfImageAsync(_selfImageUrl, userId, imgChanged));
            }
            _dirty = true;
        }

        private async Task RefreshSelfImageAsync(string url, string userId, bool force)
        {
            try
            {
                string? localPath = force ? null : ImageCacheHelper.GetUserCached(userId);
                localPath ??= await ImageCacheHelper.CacheUserAsync(userId, url, forceRefresh: force);
                if (string.IsNullOrEmpty(localPath)) return;
                var bmp = Bitmap.FromFile(localPath);
                if (bmp == null) return;
                lock (_locationImgCache)
                {
                    if (_locationImgCache.TryGetValue(url, out var old) && old != null && !ReferenceEquals(old, bmp)) old.Dispose();
                    _locationImgCache[url] = bmp;
                }
                _dirty = true;
            }
            catch { }
        }

        private void PruneLocationImageCache()
        {
            // Collect all URLs currently referenced by both location and friends lists
            var active = new HashSet<string>();
            lock (_friendLocations)
            {
                foreach (var e in _friendLocations)
                {
                    if (!string.IsNullOrEmpty(e.WorldImageUrl))  active.Add(e.WorldImageUrl);
                    if (!string.IsNullOrEmpty(e.FriendImageUrl)) active.Add(e.FriendImageUrl);
                }
            }
            lock (_onlineFriends)
            {
                foreach (var e in _onlineFriends)
                    if (!string.IsNullOrEmpty(e.FriendImageUrl)) active.Add(e.FriendImageUrl);
            }

            lock (_locationImgCache)
            {
                var stale = _locationImgCache.Keys.Where(k => !active.Contains(k)).ToList();
                foreach (var k in stale)
                {
                    _locationImgCache[k]?.Dispose();
                    _locationImgCache.Remove(k);
                }
            }
        }

        public void UpdateMediaInfo(string title, string artist, double position, double duration, bool playing)
        {
            _mediaTitle          = title;
            _mediaArtist         = artist;
            _mediaPositionAtPoll = position;
            _mediaLastPollTime   = DateTime.UtcNow;
            _mediaDuration       = duration;
            _mediaPlaying        = playing;
            _lastDisplayedSecond = -1;
            _dirty = true;
        }

        private double GetCurrentMediaPosition()
        {
            if (!_mediaPlaying || _mediaLastPollTime == DateTime.MinValue)
                return _mediaPositionAtPoll;
            return _mediaPositionAtPoll + (DateTime.UtcNow - _mediaLastPollTime).TotalSeconds;
        }

        private async Task PollSmtcAsync()
        {
            try
            {
                var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync()
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                var sessions = mgr.GetSessions();
                var s = sessions.FirstOrDefault(sess =>
                            sess.GetPlaybackInfo()?.PlaybackStatus ==
                            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        ?? mgr.GetCurrentSession();
                if (s == null)
                {
                    _smtcSession = null;
                    if (_mediaTitle != "") { _mediaTitle = ""; _mediaArtist = ""; _mediaPlaying = false; _dirty = true; }
                    return;
                }
                _smtcSession = s;

                var playing = s.GetPlaybackInfo()?.PlaybackStatus ==
                              GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var props = await s.TryGetMediaPropertiesAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                var title  = props?.Title  ?? "";
                var artist = props?.Artist ?? "";
                var tl  = s.GetTimelineProperties();
                var pos = tl?.Position.TotalSeconds ?? 0;
                var dur = tl != null ? (tl.EndTime - tl.StartTime).TotalSeconds : 0;

                _mediaPositionAtPoll = pos;
                _mediaLastPollTime   = DateTime.UtcNow;
                _mediaDuration       = dur;

                bool trackChanged = title != _mediaTitle || artist != _mediaArtist;
                if (trackChanged || playing != _mediaPlaying)
                {
                    _mediaTitle   = title;
                    _mediaArtist  = artist;
                    _mediaPlaying = playing;
                    _lastDisplayedSecond = -1;
                    _dirty = true;
                }

                // Fetch album art when track changes
                if (trackChanged && props?.Thumbnail != null)
                {
                    try
                    {
                        using var ras    = await props.Thumbnail.OpenReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                        using var stream = ras.AsStreamForRead();
                        using var ms     = new MemoryStream();
                        await stream.CopyToAsync(ms);
                        var newArt = Bitmap.FromBytes(ms.ToArray());
                        _albumArt?.Dispose();
                        _albumArt = newArt;
                        _dirty    = true;
                    }
                    catch (Exception ex) { _log($"[VRO/SMTC] Album art failed: {ex.Message}"); _albumArt?.Dispose(); _albumArt = null; }
                }
                else if (trackChanged)
                {
                    _albumArt?.Dispose();
                    _albumArt = null;
                }
            }
            catch (Exception ex) { _log($"[VRO/SMTC] Exception: {ex.GetType().Name}: {ex.Message}"); }
        }

        private void SendSmtcCommand(string cmd)
        {
            var session = _smtcSession;
            if (session == null) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    switch (cmd)
                    {
                        case "prev": await session.TrySkipPreviousAsync(); break;
                        case "next": await session.TrySkipNextAsync();     break;
                        case "playpause":
                            var status = session.GetPlaybackInfo()?.PlaybackStatus;
                            if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                                await session.TryPauseAsync();
                            else
                                await session.TryPlayAsync();
                            break;
                    }
                    // Refresh metadata immediately after command
                    await Task.Delay(300);
                    await PollSmtcAsync();
                    _dirty = true;
                }
                catch { }
            });
        }

        private void SeekSmtc(double positionSeconds)
        {
            var session = _smtcSession;
            if (session == null) return;
            // Update local interpolation immediately for responsive UI
            _mediaPositionAtPoll = positionSeconds;
            _mediaLastPollTime   = DateTime.UtcNow;
            _lastDisplayedSecond = -1;
            _dirty = true;

            long ticks = (long)(positionSeconds * TimeSpan.TicksPerSecond);
            _ = Task.Run(async () =>
            {
                try
                {
                    await session.TryChangePlaybackPositionAsync(ticks);
                    await Task.Delay(300);
                    await PollSmtcAsync();
                    _dirty = true;
                }
                catch { }
            });
        }

        public void StartKeybindRecording()
        {
            IsRecording         = true;
            _stableFrames       = 0;
            _lastPressedButtons = 0;
            _eventButtonsHeld   = 0; // clear stale state so nothing fires immediately
            _eventLeftHeld      = 0;
            _eventRightHeld     = 0;
            _log("[VROverlay] Keybind recording started");
            EmitState();
        }

        public void StopKeybindRecording()
        {
            IsRecording = false;
            EmitState();
        }

        // Private helpers

        // proximity check: enables laser pointer when free hand is near the wrist overlay, disables when far
        private void UpdateProximityInteract()
        {
            if (!IsVisible || _vrSystem == null || OpenVR.Overlay == null || _overlayHandle == 0) return;

            if (_dynVisEnabled && !_inFocus)
            {
                if (_interactMode) DisableInteract();
                return;
            }

            var wristIdx = AttachToLeft ? _leftIdx : _rightIdx;
            var freeIdx  = AttachToLeft ? _rightIdx : _leftIdx;

            if (wristIdx == OpenVR.k_unTrackedDeviceIndexInvalid ||
                freeIdx  == OpenVR.k_unTrackedDeviceIndexInvalid)
            {
                if (_interactMode) DisableInteract();
                return;
            }

            _vrSystem.GetDeviceToAbsoluteTrackingPose(
                ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, _poses);

            if (!_poses[wristIdx].bPoseIsValid || !_poses[freeIdx].bPoseIsValid)
            {
                if (_interactMode) DisableInteract();
                return;
            }

            var wm = _poses[wristIdx].mDeviceToAbsoluteTracking;
            var fm = _poses[freeIdx].mDeviceToAbsoluteTracking;

            // Transform the overlay's local offset (PosX/Y/Z) into world space using the
            // wrist controller's device-to-absolute matrix.  This makes the activation
            // sphere truly centred on the overlay panel rather than on the controller origin,
            // so the radius is equal from every direction as seen visually.
            var overlayWorldPos = new Vector3(
                wm.m0 * PosX + wm.m1 * PosY + wm.m2 * PosZ + wm.m3,
                wm.m4 * PosX + wm.m5 * PosY + wm.m6 * PosZ + wm.m7,
                wm.m8 * PosX + wm.m9 * PosY + wm.m10 * PosZ + wm.m11);
            var freePos = new Vector3(fm.m3, fm.m7, fm.m11);

            float dist = Vector3.Distance(overlayWorldPos, freePos);

            if (!_interactMode && dist < InteractEnterDist)
            {
                _interactMode = true;
                _ptrArmed     = false;
                if (!_seamless)
                {
                    OpenVR.Overlay.SetOverlayInputMethod(_overlayHandle, VROverlayInputMethod.Mouse);
                    OpenVR.Overlay.SetOverlayFlag(_overlayHandle,
                        VROverlayFlags.MakeOverlaysInteractiveIfVisible, true);
                }
            }
            else if (_interactMode && dist > InteractLeaveDist)
            {
                DisableInteract();
            }
        }

        private void DisableInteract()
        {
            _interactMode = false;
            _ptrArmed     = false;
            CancelPointer();
            HidePointer();
            if (OpenVR.Overlay == null || _overlayHandle == 0) return;
            OpenVR.Overlay.SetOverlayFlag(_overlayHandle,
                VROverlayFlags.MakeOverlaysInteractiveIfVisible, false);
            OpenVR.Overlay.SetOverlayInputMethod(_overlayHandle, VROverlayInputMethod.None);
        }

        // Seamless pointer: own ray-cast + own laser so SteamVR never takes the
        // controller away from the running game.

        private void UpdateSeamlessPointer()
        {
            if (!_seamless || OpenVR.Overlay == null || _overlayHandle == 0) return;

            var sys = _vrSystem;
            if (sys == null || !IsVisible || !_interactMode) { HidePointer(); return; }

            uint freeIdx  = AttachToLeft ? _rightIdx : _leftIdx;
            uint wristIdx = AttachToLeft ? _leftIdx  : _rightIdx;
            if (freeIdx  == OpenVR.k_unTrackedDeviceIndexInvalid ||
                wristIdx == OpenVR.k_unTrackedDeviceIndexInvalid ||
                !_poses[freeIdx].bPoseIsValid)
            { HidePointer(); return; }

            Vector3 tipPos, tipDir;
            if (AttachToLeft)
            {
                ResolveTip(freeIdx, ref _tipPosR, ref _tipDirR, ref _tipIdxR);
                tipPos = _tipPosR; tipDir = _tipDirR;
            }
            else
            {
                ResolveTip(freeIdx, ref _tipPosL, ref _tipDirL, ref _tipIdxL);
                tipPos = _tipPosL; tipDir = _tipDirL;
            }

            var fm = _poses[freeIdx].mDeviceToAbsoluteTracking;
            var origin = XformPoint(fm, tipPos);
            var dir    = Vector3.Normalize(XformDir(fm, tipDir));

            var ip = new VROverlayIntersectionParams_t
            {
                vSource    = new HmdVector3_t { v0 = origin.X, v1 = origin.Y, v2 = origin.Z },
                vDirection = new HmdVector3_t { v0 = dir.X,    v1 = dir.Y,    v2 = dir.Z    },
                eOrigin    = ETrackingUniverseOrigin.TrackingUniverseStanding,
            };
            var ir  = new VROverlayIntersectionResults_t();
            bool hit = OpenVR.Overlay.ComputeOverlayIntersection(_overlayHandle, ref ip, ref ir)
                       && ir.fDistance > 0.001f;

            float nx = 0f, ny = 0f;
            if (hit)
            {
                nx = ir.vUVs.v0;
                ny = ir.vUVs.v1 * TexH / H;
            }

            ulong trigger = VrInputActions.Active
                ? 1UL << (int)VrInputActions.BtnTrigger
                : 1UL << (int)EVRButtonId.k_EButton_SteamVR_Trigger;
            bool held = (GetSideButtonState(AttachToLeft ? 2 : 1) & trigger) != 0;
            if (!held) _ptrArmed = true;

            if (!_ptrDown)
            {
                if (held && hit && _ptrArmed)
                {
                    _ptrDown = true;
                    _ptrNY   = ny;
                    PointerDown(nx, ny);
                }
            }
            else if (!held)
            {
                _ptrDown = false;
                PointerUp(_ptrNY);
            }
            else if (hit)
            {
                _ptrNY = ny;
                PointerMove(ny);
            }
            else
            {
                _ptrDown = false;
                CancelPointer();
            }

            float len = hit ? Math.Clamp(ir.fDistance, 0.01f, 4f) : 0.2f;
            ShowLaser(freeIdx, tipPos, tipDir, len);
            if (hit) ShowDot(wristIdx, ir.vUVs.v0, ir.vUVs.v1); else HideDot();
        }

        private void ShowLaser(uint freeIdx, Vector3 tipPos, Vector3 tipDir, float len)
        {
            if (OpenVR.Overlay == null || _laserHandle == 0) return;

            var hmdIdx = OpenVR.k_unTrackedDeviceIndex_Hmd;
            var fm     = _poses[freeIdx].mDeviceToAbsoluteTracking;
            var mid    = tipPos + tipDir * (len * 0.5f);

            var toView = -Vector3.UnitZ;
            if (_poses[hmdIdx].bPoseIsValid)
            {
                var hm      = _poses[hmdIdx].mDeviceToAbsoluteTracking;
                var hmdWorld = new Vector3(hm.m3, hm.m7, hm.m11);
                var hmdLocal = InvXformPoint(fm, hmdWorld);
                toView = hmdLocal - mid;
            }

            var right = Vector3.Cross(tipDir, toView);
            if (right.LengthSquared() < 1e-8f) right = Vector3.Cross(tipDir, Vector3.UnitY);
            if (right.LengthSquared() < 1e-8f) right = Vector3.UnitX;
            right = Vector3.Normalize(right);
            var normal = Vector3.Normalize(Vector3.Cross(right, tipDir));

            var m = new HmdMatrix34_t
            {
                m0 = right.X * LaserThickness, m1 = tipDir.X * len, m2  = normal.X, m3  = mid.X,
                m4 = right.Y * LaserThickness, m5 = tipDir.Y * len, m6  = normal.Y, m7  = mid.Y,
                m8 = right.Z * LaserThickness, m9 = tipDir.Z * len, m10 = normal.Z, m11 = mid.Z,
            };
            OpenVR.Overlay.SetOverlayTransformTrackedDeviceRelative(_laserHandle, freeIdx, ref m);

            if (!_laserShown)
            {
                OpenVR.Overlay.ShowOverlay(_laserHandle);
                _laserShown = true;
            }
        }

        private void ShowDot(uint wristIdx, float u, float v)
        {
            if (OpenVR.Overlay == null || _dotHandle == 0) return;

            float panelH = WidthMeters * TexH / W;
            float lx     = (u - 0.5f) * WidthMeters;
            float ly     = (v - 0.5f) * panelH;
            float size   = Math.Clamp(WidthMeters * 0.045f, 0.004f, 0.02f);

            var t   = BuildTransform(PosX, PosY, PosZ, RotX, RotY, RotZ);
            var off = XformDir(t, new Vector3(lx, ly, 0.0015f));

            var m = new HmdMatrix34_t
            {
                m0 = t.m0 * size, m1 = t.m1 * size, m2  = t.m2,  m3  = t.m3  + off.X,
                m4 = t.m4 * size, m5 = t.m5 * size, m6  = t.m6,  m7  = t.m7  + off.Y,
                m8 = t.m8 * size, m9 = t.m9 * size, m10 = t.m10, m11 = t.m11 + off.Z,
            };
            OpenVR.Overlay.SetOverlayTransformTrackedDeviceRelative(_dotHandle, wristIdx, ref m);

            if (!_dotShown)
            {
                OpenVR.Overlay.ShowOverlay(_dotHandle);
                _dotShown = true;
            }
        }

        private void HideDot()
        {
            if (!_dotShown || OpenVR.Overlay == null || _dotHandle == 0) return;
            OpenVR.Overlay.HideOverlay(_dotHandle);
            _dotShown = false;
        }

        private void HidePointer()
        {
            if (OpenVR.Overlay == null) return;
            if (_laserShown && _laserHandle != 0)
            {
                OpenVR.Overlay.HideOverlay(_laserHandle);
                _laserShown = false;
            }
            HideDot();
        }

        private void ResolveTip(uint idx, ref Vector3 pos, ref Vector3 dir, ref uint cachedIdx)
        {
            if (cachedIdx == idx) return;
            cachedIdx = idx;
            pos = Vector3.Zero;
            dir = -Vector3.UnitZ;

            var sys = _vrSystem;
            var rm  = OpenVR.RenderModels;
            if (sys == null || rm == null) return;

            try
            {
                var err = ETrackedPropertyError.TrackedProp_Success;
                var sb  = new System.Text.StringBuilder(256);
                sys.GetStringTrackedDeviceProperty(idx,
                    ETrackedDeviceProperty.Prop_RenderModelName_String, sb, 256, ref err);
                if (err != ETrackedPropertyError.TrackedProp_Success || sb.Length == 0) return;

                var cs   = new VRControllerState_t();
                var mode = new RenderModel_ControllerMode_State_t();
                var comp = new RenderModel_ComponentState_t();
                if (!rm.GetComponentState(sb.ToString(), OpenVR.k_pch_Controller_Component_Tip,
                        ref cs, ref mode, ref comp)) return;

                var m  = comp.mTrackingToComponentLocal;
                var fw = new Vector3(-m.m2, -m.m6, -m.m10);
                if (fw.LengthSquared() < 1e-8f) return;
                pos = new Vector3(m.m3, m.m7, m.m11);
                dir = Vector3.Normalize(fw);
                _log($"[VROverlay] Pointer tip resolved for device {idx}");
            }
            catch { }
        }

        private static Vector3 XformPoint(in HmdMatrix34_t m, Vector3 v) => new(
            m.m0 * v.X + m.m1 * v.Y + m.m2  * v.Z + m.m3,
            m.m4 * v.X + m.m5 * v.Y + m.m6  * v.Z + m.m7,
            m.m8 * v.X + m.m9 * v.Y + m.m10 * v.Z + m.m11);

        private static Vector3 XformDir(in HmdMatrix34_t m, Vector3 v) => new(
            m.m0 * v.X + m.m1 * v.Y + m.m2  * v.Z,
            m.m4 * v.X + m.m5 * v.Y + m.m6  * v.Z,
            m.m8 * v.X + m.m9 * v.Y + m.m10 * v.Z);

        private static Vector3 InvXformPoint(in HmdMatrix34_t m, Vector3 v)
        {
            float x = v.X - m.m3, y = v.Y - m.m7, z = v.Z - m.m11;
            return new Vector3(
                m.m0 * x + m.m4 * y + m.m8  * z,
                m.m1 * x + m.m5 * y + m.m9  * z,
                m.m2 * x + m.m6 * y + m.m10 * z);
        }

        private void PointerDown(float nx, float ny)
        {
            _mouseDown        = true;
            _mouseDownNX      = nx;
            _mouseDownNY      = ny;
            _scrollDragging   = false;
            _scrollLastNY     = ny;
            _scrollLastDeltaY = 0f;
            if (_activeTab == 1) _notifScrollVY    = 0f;
            if (_activeTab == 2) _locationScrollVY = 0f;
            if (_activeTab == 5) _friendsScrollVY  = 0f;
            if (_activeTab == 4) _toolsScrollVY    = 0f;
        }

        private void PointerMove(float ny)
        {
            if (!_mouseDown) return;
            if (_activeTab != 1 && _activeTab != 2 && _activeTab != 4 && _activeTab != 5) return;
            if (_mouseDownNY >= 1f - (float)(LocContentY - 6) / H) return;

            if (!_scrollDragging && MathF.Abs((ny - _mouseDownNY) * H) > 20f)
                _scrollDragging = true;
            if (!_scrollDragging) return;

            float delta = (ny - _scrollLastNY) * H;
            _scrollLastDeltaY = delta;
            _scrollLastNY     = ny;
            if (_activeTab == 1)
                _notifScrollY    = Math.Clamp(_notifScrollY    + delta, 0f, GetNotifMaxScroll());
            else if (_activeTab == 2)
                _locationScrollY = Math.Clamp(_locationScrollY + delta, 0f, GetLocationMaxScroll());
            else if (_activeTab == 4)
                _toolsScrollY    = Math.Clamp(_toolsScrollY    + delta, 0f, GetToolsMaxScroll());
            else
                _friendsScrollY  = Math.Clamp(_friendsScrollY  + delta, 0f, GetFriendsMaxScroll());
            _dirty = true;
        }

        private void PointerUp(float ny)
        {
            float totalMove = MathF.Abs((ny - _mouseDownNY) * H);
            if (_scrollDragging && totalMove >= 20f)
            {
                if (_activeTab == 1) _notifScrollVY    = _scrollLastDeltaY * 0.5f;
                if (_activeTab == 2) _locationScrollVY = _scrollLastDeltaY * 0.5f;
                if (_activeTab == 4) _toolsScrollVY    = _scrollLastDeltaY * 0.5f;
                if (_activeTab == 5) _friendsScrollVY  = _scrollLastDeltaY * 0.5f;
            }
            else
            {
                HandleOverlayClick(_mouseDownNX, _mouseDownNY);
            }
            _mouseDown      = false;
            _scrollDragging = false;
        }

        private void CancelPointer()
        {
            if (!_mouseDown) return;
            if (_scrollDragging)
            {
                if (_activeTab == 1) _notifScrollVY    = _scrollLastDeltaY * 0.5f;
                if (_activeTab == 2) _locationScrollVY = _scrollLastDeltaY * 0.5f;
                if (_activeTab == 4) _toolsScrollVY    = _scrollLastDeltaY * 0.5f;
                if (_activeTab == 5) _friendsScrollVY  = _scrollLastDeltaY * 0.5f;
            }
            _mouseDown      = false;
            _scrollDragging = false;
            _ptrDown        = false;
        }

        private void CreatePointerOverlays()
        {
            if (OpenVR.Overlay == null) return;

            var lErr = OpenVR.Overlay.CreateOverlay("vrcnext.laser", "VRCNext Laser", ref _laserHandle);
            if (lErr == EVROverlayError.KeyInUse)
                OpenVR.Overlay.FindOverlay("vrcnext.laser", ref _laserHandle);
            if (_laserHandle != 0)
            {
                OpenVR.Overlay.SetOverlayWidthInMeters(_laserHandle, 1f);
                OpenVR.Overlay.SetOverlayInputMethod(_laserHandle, VROverlayInputMethod.None);
                OpenVR.Overlay.SetOverlaySortOrder(_laserHandle, 150);
                OpenVR.Overlay.SetOverlayAlpha(_laserHandle, 0.85f);
                UploadPointerTexture(_laserHandle, BuildBeamTexture(), LaserTexSize, LaserTexSize);
            }

            var dErr = OpenVR.Overlay.CreateOverlay("vrcnext.laserdot", "VRCNext Pointer", ref _dotHandle);
            if (dErr == EVROverlayError.KeyInUse)
                OpenVR.Overlay.FindOverlay("vrcnext.laserdot", ref _dotHandle);
            if (_dotHandle != 0)
            {
                OpenVR.Overlay.SetOverlayWidthInMeters(_dotHandle, 1f);
                OpenVR.Overlay.SetOverlayInputMethod(_dotHandle, VROverlayInputMethod.None);
                OpenVR.Overlay.SetOverlaySortOrder(_dotHandle, 160);
                UploadPointerTexture(_dotHandle, BuildDotTexture(), DotTexSize, DotTexSize);
            }

            ApplyPointerTint();
            _laserShown = false;
            _dotShown   = false;
            _log($"[VROverlay] Pointer overlays created: {lErr} / {dErr}");
        }

        private void DestroyPointerOverlays()
        {
            if (OpenVR.Overlay != null)
            {
                if (_laserHandle != 0)
                    try { OpenVR.Overlay.HideOverlay(_laserHandle); OpenVR.Overlay.DestroyOverlay(_laserHandle); } catch { }
                if (_dotHandle != 0)
                    try { OpenVR.Overlay.HideOverlay(_dotHandle); OpenVR.Overlay.DestroyOverlay(_dotHandle); } catch { }
            }
            _laserHandle = 0; _dotHandle = 0;
            _laserShown  = false; _dotShown = false;
        }

        private void ApplyPointerTint()
        {
            if (OpenVR.Overlay == null) return;
            var c = _theme.Accent;
            if (c.A == 0) return;
            float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
            if (_laserHandle != 0) OpenVR.Overlay.SetOverlayColor(_laserHandle, r, g, b);
            if (_dotHandle   != 0) OpenVR.Overlay.SetOverlayColor(_dotHandle,   r, g, b);
        }

        private static void UploadPointerTexture(ulong handle, byte[] buf, int w, int h)
        {
            if (OpenVR.Overlay == null || handle == 0) return;
            var pinned = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try { OpenVR.Overlay.SetOverlayRaw(handle, pinned.AddrOfPinnedObject(), (uint)w, (uint)h, 4); }
            finally { pinned.Free(); }
        }

        private static byte[] BuildBeamTexture()
        {
            var buf = new byte[LaserTexSize * LaserTexSize * 4];
            for (int x = 0; x < LaserTexSize; x++)
            {
                float d = MathF.Abs((x + 0.5f) / LaserTexSize - 0.5f) * 2f;
                float a = MathF.Max(0f, 1f - d);
                a = a * a * (0.4f + 0.6f * a);
                byte alpha = (byte)Math.Clamp(a * 255f, 0f, 255f);
                for (int y = 0; y < LaserTexSize; y++)
                {
                    int i = (y * LaserTexSize + x) * 4;
                    buf[i] = 255; buf[i + 1] = 255; buf[i + 2] = 255; buf[i + 3] = alpha;
                }
            }
            return buf;
        }

        private static byte[] BuildDotTexture()
        {
            var buf = new byte[DotTexSize * DotTexSize * 4];
            float c = DotTexSize * 0.5f;
            for (int y = 0; y < DotTexSize; y++)
            {
                for (int x = 0; x < DotTexSize; x++)
                {
                    float dx = (x + 0.5f - c) / c, dy = (y + 0.5f - c) / c;
                    float r  = MathF.Sqrt(dx * dx + dy * dy);
                    float a  = r <= 0.5f ? 1f : (r >= 0.9f ? 0f : 1f - (r - 0.5f) / 0.4f);
                    int i = (y * DotTexSize + x) * 4;
                    buf[i] = 255; buf[i + 1] = 255; buf[i + 2] = 255;
                    buf[i + 3] = (byte)Math.Clamp(a * 255f, 0f, 255f);
                }
            }
            return buf;
        }

        private void UpdateDynamicVisibility()
        {
            if (OpenVR.Overlay == null || _overlayHandle == 0) return;

            if (!_dynVisEnabled)
            {
                _inFocus = true;
                if (MathF.Abs(_dynVisAlpha - FullAlpha) > 0.001f)
                {
                    _dynVisAlpha = FullAlpha;
                    OpenVR.Overlay.SetOverlayAlpha(_overlayHandle, FullAlpha);
                }
                return;
            }

            if (!IsVisible || _vrSystem == null) return;

            var wristIdx = AttachToLeft ? _leftIdx : _rightIdx;
            var hmdIdx   = OpenVR.k_unTrackedDeviceIndex_Hmd;
            if (wristIdx == OpenVR.k_unTrackedDeviceIndexInvalid) return;

            _vrSystem.GetDeviceToAbsoluteTrackingPose(
                ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, _poses);

            if (!_poses[wristIdx].bPoseIsValid || !_poses[hmdIdx].bPoseIsValid) return;

            var wm = _poses[wristIdx].mDeviceToAbsoluteTracking;
            var hm = _poses[hmdIdx].mDeviceToAbsoluteTracking;

            var overlayWorldPos = new Vector3(
                wm.m0 * PosX + wm.m1 * PosY + wm.m2 * PosZ + wm.m3,
                wm.m4 * PosX + wm.m5 * PosY + wm.m6 * PosZ + wm.m7,
                wm.m8 * PosX + wm.m9 * PosY + wm.m10 * PosZ + wm.m11);
            var hmdPos = new Vector3(hm.m3, hm.m7, hm.m11);

            float dist = Vector3.Distance(overlayWorldPos, hmdPos);
            _inFocus = dist <= FocusRadius;

            float t      = Math.Clamp((dist - FocusRadius) / DynVisFalloff, 0f, 1f);
            float target = FullAlpha + (DynVisMinAlpha - FullAlpha) * t;

            _dynVisAlpha += (target - _dynVisAlpha) * 0.25f;
            OpenVR.Overlay.SetOverlayAlpha(_overlayHandle, _dynVisAlpha);
        }

        private async Task PollLoopAsync(CancellationToken ct)
        {
            _log("[VROverlay] Poll loop started");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    PollEvents();
                    UpdateControllerIndices();

                    if (IsRecording)
                        PollKeybindRecording();
                    else if (_isScaleRecording)
                        PollScaleKeybindRecording();
                    else
                        PollKeybindTrigger();

                    // Poll SMTC in background so the loop is never blocked by WinRT await
                    if (++_smtcTick >= SMTC_POLL_INTERVAL && !_smtcPolling)
                    {
                        _smtcTick    = 0;
                        _smtcPolling = true;
                        _ = Task.Run(async () => { try { await PollSmtcAsync(); } finally { _smtcPolling = false; } });
                    }

                    UpdateDynamicVisibility();
                    UpdateToastFollow();

                    // Proximity-based interaction: enable Mouse+Interactive when free
                    // hand is near the wrist, revert to None otherwise.
                    UpdateProximityInteract();
                    UpdateSeamlessPointer();

                    if (IsVisible)
                    {
                        const int tabX = 8;
                        var visTabs = VisibleTabs();
                        int tabW = (W - 16) / Math.Max(1, visTabs.Count);
                        int tabPos = Math.Max(0, visTabs.IndexOf(_activeTab));
                        float targetX = tabX + 2f + tabPos * tabW;
                        if (MathF.Abs(_wdAnim - _wdTarget) > 0.004f)
                        {
                            _wdAnim += (_wdTarget - _wdAnim) * 0.34f;
                            _dirty = true;
                        }
                        else if (_wdAnim != _wdTarget)
                        {
                            _wdAnim = _wdTarget;
                            if (_wdTarget <= 0f) _openWorldKey = null;
                            _dirty = true;
                        }
                        if (MathF.Abs(_tabIndicatorX - targetX) > 0.5f)
                        {
                            _tabIndicatorX += (targetX - _tabIndicatorX) * 0.25f; // lerp
                            _dirty = true;
                        }
                        else if (_tabIndicatorX != targetX)
                        {
                            _tabIndicatorX = targetX;
                            _dirty = true;
                        }

                        // Scroll inertia — decays to 0, marks dirty while moving
                        if (!_scrollDragging)
                        {
                            if (MathF.Abs(_notifScrollVY) > 0.3f)
                            {
                                _notifScrollVY *= 0.87f;
                                _notifScrollY   = Math.Clamp(_notifScrollY + _notifScrollVY, 0f, GetNotifMaxScroll());
                                _dirty = true;
                            }
                            if (MathF.Abs(_locationScrollVY) > 0.3f)
                            {
                                _locationScrollVY *= 0.87f;
                                _locationScrollY   = Math.Clamp(_locationScrollY + _locationScrollVY, 0f, GetLocationMaxScroll());
                                _dirty = true;
                            }
                            if (MathF.Abs(_friendsScrollVY) > 0.3f)
                            {
                                _friendsScrollVY *= 0.87f;
                                _friendsScrollY   = Math.Clamp(_friendsScrollY + _friendsScrollVY, 0f, GetFriendsMaxScroll());
                                _dirty = true;
                            }
                            if (MathF.Abs(_toolsScrollVY) > 0.3f)
                            {
                                _toolsScrollVY *= 0.87f;
                                _toolsScrollY   = Math.Clamp(_toolsScrollY + _toolsScrollVY, 0f, GetToolsMaxScroll());
                                _dirty = true;
                            }
                        }

                        // Re-apply transform if the active controller index just became
                        // valid or changed (e.g. controller connected after Show() was called).
                        var curIdx = AttachToLeft ? _leftIdx : _rightIdx;
                        if (curIdx != OpenVR.k_unTrackedDeviceIndexInvalid && curIdx != _lastTransformIdx)
                        {
                            _lastTransformIdx = curIdx;
                            ApplyTransform();
                        }

                        int ds = DateTime.Now.Second;
                        if (ds != _lastDashSecond)
                        {
                            _lastDashSecond = ds;
                            UpdateStats();
                            _dirty = true;
                        }

                        // Keep re-rendering for alarm pulse animation (any tab)
                        if (_waterAlarmActive) _dirty = true;

                        // For the music player tab, re-render only when the displayed second
                        // actually changes — avoids calling SetOverlayRaw every tick.
                        if (_activeTab == 3 && _mediaPlaying)
                        {
                            int sec = (int)GetCurrentMediaPosition();
                            if (sec != _lastDisplayedSecond)
                            {
                                _lastDisplayedSecond = sec;
                                _dirty = true;
                            }
                        }

                        // Scale tab: poll thumbstick and apply scale when keybind held
                        if (_activeTab == TabSize)
                        {
                            float tx = 0f, ty = 0f;
                            if (_scaleLeftThumb)  ReadThumb(1, ref tx, ref ty);
                            if (_scaleRightThumb) ReadThumb(2, ref tx, ref ty);
                            if (MathF.Abs(tx - _thumbDisplayX) > 0.02f || MathF.Abs(ty - _thumbDisplayY) > 0.02f)
                            {
                                _thumbDisplayX = tx;
                                _thumbDisplayY = ty;
                                _dirty = true;
                            }
                            // Apply scale when keybind held + thumb moved past deadzone
                            if (!_isScaleRecording && _scaleKeybind.Count > 0 && MathF.Abs(ty) > 0.15f)
                            {
                                ulong scaleMask = 0;
                                foreach (var b in _scaleKeybind) scaleMask |= 1UL << (int)b;
                                bool held = (GetSideButtonState(_scaleKeybindHand) & scaleMask) == scaleMask;
                                if (held)
                                {
                                    float delta = ty * 0.096f * (_scaleScrollSensitivity / 25f);
                                    _scaleValue = Math.Clamp(_scaleValue + delta, 0.01f, 10000f);
                                    _dirty = true;
                                    OnScaleChange?.Invoke(delta);
                                }
                            }
                        }

                        // Mark dirty while any join cooldown is still active (so button resets after 5s)
                        if (_joinCooldowns.Count > 0)
                        {
                            var now = DateTime.UtcNow;
                            bool anyCooldownActive = false;
                            bool anyCooldownExpired = false;
                            foreach (var kv in _joinCooldowns)
                            {
                                double elapsed = (now - kv.Value).TotalSeconds;
                                if (elapsed < 5) anyCooldownActive = true;
                                else anyCooldownExpired = true;
                            }
                            if (anyCooldownExpired)
                            {
                                var expired = new List<string>();
                                foreach (var kv in _joinCooldowns)
                                    if ((now - kv.Value).TotalSeconds >= 5) expired.Add(kv.Key);
                                foreach (var k in expired) _joinCooldowns.Remove(k);
                                _dirty = true;
                            }
                            else if (anyCooldownActive)
                            {
                                _dirty = true;
                            }
                        }

                        // Only upload a new texture when content actually changed.
                        if (_dirty)
                        {
                            _dirty = false;
                            Render();
                        }
                    }

                    // Water reminder countdown — always runs regardless of overlay visibility
                    if (_waterEnabled && !_waterAlarmActive)
                    {
                        var wNow = DateTime.UtcNow;
                        long wElapsed = (long)(wNow - _waterLastTick).TotalMilliseconds;
                        if (wElapsed >= 1000)
                        {
                            _waterLastTick  = wNow;
                            _waterRemainMs -= wElapsed;
                            if (_waterRemainMs <= 0)
                            {
                                _waterAlarmActive = true;
                                _waterRemainMs    = 0;
                                OnWaterAlarm?.Invoke();
                                // Auto-show the overlay so the alarm is visible
                                if (!IsVisible) Show();
                            }
                        }
                    }

                    // Toast overlay tick (always runs, independent of wrist overlay visibility).
                    TickToast();

                    // Use minimal delay while scrolling to hit ~90fps; 11ms otherwise (~64fps steady)
                    bool activeScroll = _scrollDragging || MathF.Abs(_locationScrollVY) > 0.5f || MathF.Abs(_friendsScrollVY) > 0.5f || MathF.Abs(_toolsScrollVY) > 0.5f;
                    await Task.Delay(activeScroll ? 1 : 11, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _log($"[VROverlay] PollLoop: {ex.Message}");
                    try { await Task.Delay(500, ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
            _running = false;
        }

        private void PollEvents()
        {
            // Read once into a local — volatile ensures the JIT cannot cache the field
            var sys = _vrSystem;
            if (sys == null) return;

            // Reconcile event-driven button state with GetControllerState
            if (!VrInputActions.Active)
            {
                var s  = new VRControllerState_t();
                var sz = (uint)Marshal.SizeOf<VRControllerState_t>();
                ulong polledAll = 0;
                if (_leftIdx != OpenVR.k_unTrackedDeviceIndexInvalid)
                {
                    if (sys.GetControllerState(_leftIdx, ref s, sz))
                    {
                        _eventLeftHeld &= s.ulButtonPressed;
                        polledAll |= s.ulButtonPressed;
                    }
                }
                if (_rightIdx != OpenVR.k_unTrackedDeviceIndexInvalid)
                {
                    if (sys.GetControllerState(_rightIdx, ref s, sz))
                    {
                        _eventRightHeld &= s.ulButtonPressed;
                        polledAll |= s.ulButtonPressed;
                    }
                }

                _eventButtonsHeld = (_eventButtonsHeld & ~ALLOWED_BUTTON_MASK)
                                  | (_eventLeftHeld & ALLOWED_BUTTON_MASK)
                                  | (_eventRightHeld & ALLOWED_BUTTON_MASK);
            }

            var evt = new VREvent_t();
            var evtSize = (uint)Marshal.SizeOf<VREvent_t>();

            // Drain system-level events.
            while (sys.PollNextEvent(ref evt, evtSize))
            {
                var eType = (EVREventType)evt.eventType;
                if (eType == EVREventType.VREvent_Quit)
                {
                    // Null _vrSystem FIRST... sys (local snapshot) is used for the
                    // acknowledge call so no further code touches the field.
                    _vrSystem = null;
                    try { sys.AcknowledgeQuit_Exiting(); } catch { }
                    _cts?.Cancel();
                    OnVRQuit?.Invoke();
                    return;
                }
                else if (eType == EVREventType.VREvent_ButtonPress)
                {
                    ulong bit = 1UL << (int)evt.data.controller.button;
                    _eventButtonsHeld |= bit;
                    if (evt.trackedDeviceIndex == _leftIdx)  _eventLeftHeld  |= bit;
                    if (evt.trackedDeviceIndex == _rightIdx) _eventRightHeld |= bit;
                }
                else if (eType == EVREventType.VREvent_ButtonUnpress)
                {
                    ulong bit = 1UL << (int)evt.data.controller.button;
                    _eventButtonsHeld &= ~bit;
                    if (evt.trackedDeviceIndex == _leftIdx)  _eventLeftHeld  &= ~bit;
                    if (evt.trackedDeviceIndex == _rightIdx) _eventRightHeld &= ~bit;
                }
            }

            // Drain overlay-specific events (laser pointer mouse interactions).
            if (OpenVR.Overlay != null && _overlayHandle != 0)
            {
                while (OpenVR.Overlay.PollNextOverlayEvent(_overlayHandle, ref evt, evtSize))
                {
                    var oType = (EVREventType)evt.eventType;
                    if (oType == EVREventType.VREvent_ButtonPress)
                    {
                        ulong bit = 1UL << (int)evt.data.controller.button;
                        _eventButtonsHeld |= bit;
                        if (evt.trackedDeviceIndex == _leftIdx)  _eventLeftHeld  |= bit;
                        if (evt.trackedDeviceIndex == _rightIdx) _eventRightHeld |= bit;
                    }
                    else if (oType == EVREventType.VREvent_ButtonUnpress)
                    {
                        ulong bit = 1UL << (int)evt.data.controller.button;
                        _eventButtonsHeld &= ~bit;
                        if (evt.trackedDeviceIndex == _leftIdx)  _eventLeftHeld  &= ~bit;
                        if (evt.trackedDeviceIndex == _rightIdx) _eventRightHeld &= ~bit;
                    }
                    else if (oType == EVREventType.VREvent_MouseButtonDown)
                    {
                        var mu = evt.data.mouse;
                        PointerDown(mu.x / W, mu.y / H);
                    }
                    else if (oType == EVREventType.VREvent_MouseMove)
                    {
                        PointerMove(evt.data.mouse.y / H);
                    }
                    else if (oType == EVREventType.VREvent_MouseButtonUp)
                    {
                        PointerUp(evt.data.mouse.y / H);
                    }
                }
            }
        }

        private void HandleOverlayClick(float nx, float ny)
        {
            if (ny > 1f) return;
            // Tab bar: GDI+ y=8–58 → OpenVR ny ≈ 0.85–0.98 (y=0 at bottom)
            // 4 tabs, each 124px: tabTW=496/4=124 → thresholds at nx 0.25, 0.50, 0.75
            if (ny > 1f - (float)TabBarBottom / H)
            {
                var hitTabs = VisibleTabs();
                int hitIdx = Math.Clamp((int)(nx * hitTabs.Count), 0, hitTabs.Count - 1);
                int hitTab = hitTabs[hitIdx];
                if (hitTab == TabLocation && _activeTab == TabLocation && _openWorldKey != null)
                {
                    _wdTarget = 0f;
                    _locationScrollY = 0f; _locationScrollVY = 0f;
                    _dirty = true;
                    return;
                }
                _activeTab = hitTab;
                _lastDisplayedSecond = -1;
                _notifScrollY    = 0f; _notifScrollVY    = 0f;
                _locationScrollY = 0f; _locationScrollVY = 0f;
                _friendsScrollY  = 0f; _friendsScrollVY  = 0f;
                _toolsScrollY    = 0f; _toolsScrollVY    = 0f;
                _dirty = true;
                return;
            }

            // Water alarm covers full overlay — any tap dismisses it
            if (_waterAlarmActive)
            {
                DismissWaterAlarm();
                return;
            }

            // Music player
            if (_activeTab == 3 && _mediaDuration > 0
                && ny <= 1f - (float)(MusicBarY - 8) / H
                && ny >= 1f - (float)(MusicBarY + MusicBarH + 8) / H)
            {
                const int barPad = 22;
                float barNxStart = (float)barPad / W;
                float barNxEnd   = (float)(W - barPad) / W;
                if (nx >= barNxStart && nx <= barNxEnd)
                {
                    float seekFrac = (nx - barNxStart) / (barNxEnd - barNxStart);
                    seekFrac = Math.Clamp(seekFrac, 0f, 1f);
                    double seekPos = seekFrac * _mediaDuration;
                    SeekSmtc(seekPos);
                }
            }

            // Music player controls:
            //  Controls GDI+ y 286–338 → ny 0.12–0.25
            //  Prev cx=172±18 → nx 0.27–0.40, Play cx=256±26 → nx 0.43–0.57, Next cx=340±18 → nx 0.60–0.73
            if (_activeTab == 3
                && ny <= 1f - (float)(MusicCtrlCY - MusicPlayR) / H
                && ny >= 1f - (float)(MusicCtrlCY + MusicPlayR) / H)
            {
                if      (nx >= 0.27f && nx <= 0.40f) SendSmtcCommand("prev");
                else if (nx >= 0.43f && nx <= 0.57f) SendSmtcCommand("playpause");
                else if (nx >= 0.60f && nx <= 0.73f) SendSmtcCommand("next");
            }

            // Tools tab card clicks — same constants as DrawTools, accounts for scroll
            if (_activeTab == 4)
            {
                int cardW = ToolsCardW;
                int cardH = ToolsCardH;
                int gdix  = (int)(nx * W);
                int gdiy  = (int)((1f - ny) * H);
                if (gdiy >= ToolsStartY && gdiy < ToolsBottom)
                {
                    int scrolledY = gdiy - ToolsStartY + (int)_toolsScrollY;
                    int col   = (gdix - ToolsPadX) / (cardW + ToolsGap);
                    int row   = scrolledY / (cardH + ToolsGap);
                    int maxRows = (GetToolsCount() + 1) / 2;
                    if (col >= 0 && col < 2 && row >= 0 && row < maxRows)
                    {
                        int localX = (gdix - ToolsPadX) % (cardW + ToolsGap);
                        int localY = scrolledY % (cardH + ToolsGap);
                        int idx = row * 2 + col;
                        if (localX < cardW && localY < cardH && idx < GetToolsCount())
                            OnToolToggle?.Invoke(idx);
                    }
                }
            }

            // Location tab: scrollable 2-column grid — no pagination
            if (_activeTab == 2)
            {
                int gdixL = (int)(nx * W);
                int gdiyL = (int)((1f - ny) * H);

                var openWg = OpenWorldGroup();
                if (openWg != null)
                {
                    int listTop = LocContentY + WdHeadH;
                    if (gdiyL >= listTop && gdiyL < ScrollContentBottom)
                    {
                        int scrolled = gdiyL - listTop + (int)_locationScrollY;
                        int cursor = 0;
                        foreach (var inst in openWg.Instances)
                        {
                            cursor += WdInstH;
                            foreach (var e in inst)
                            {
                                if (scrolled >= cursor && scrolled < cursor + FrdCardH)
                                {
                                    int invX  = W - LocPadX - ActBtnW - 6;
                                    int joinX = invX - ActBtnW - 6;
                                    if (gdixL >= joinX && gdixL < joinX + ActBtnW)
                                    {
                                        bool cd = _joinCooldowns.TryGetValue(e.FriendId, out var t1)
                                            && (DateTime.UtcNow - t1).TotalSeconds < 5;
                                        if (!cd)
                                        {
                                            _joinCooldowns[e.FriendId] = DateTime.UtcNow;
                                            _dirty = true;
                                            OnJoinRequest?.Invoke(e.FriendId, e.Location);
                                        }
                                    }
                                    else if (gdixL >= invX && gdixL < invX + ActBtnW)
                                    {
                                        string key = e.FriendId + "#inv";
                                        bool cd = _joinCooldowns.TryGetValue(key, out var t2)
                                            && (DateTime.UtcNow - t2).TotalSeconds < 5;
                                        if (!cd)
                                        {
                                            _joinCooldowns[key] = DateTime.UtcNow;
                                            _dirty = true;
                                            OnInviteFriend?.Invoke(e.FriendId);
                                        }
                                    }
                                    return;
                                }
                                cursor += FrdCardH + WdRowGap;
                            }
                        }
                    }
                    return;
                }

                int colW = LocColW;
                if (gdiyL >= LocContentY && gdiyL < ScrollContentBottom)
                {
                    int scrolledY = gdiyL - LocContentY + (int)_locationScrollY;
                    int row    = scrolledY / (LocCardH + LocRowGap);
                    int localY = scrolledY % (LocCardH + LocRowGap);
                    int col    = gdixL < LocPadX + colW ? 0 : 1;
                    int cardX  = LocPadX + col * (colW + LocColGap);
                    if (localY < LocCardH && gdixL >= cardX && gdixL < cardX + colW)
                    {
                        var groups = GetWorldGroups();
                        int absIdx = row * 2 + col;
                        if (absIdx >= 0 && absIdx < groups.Count)
                        {
                            _openWorldKey     = groups[absIdx].WorldId;
                            _wdTarget         = 1f;
                            _locationScrollY  = 0f;
                            _locationScrollVY = 0f;
                            _dirty = true;
                        }
                    }
                }
            }

            if (_activeTab == 1)
            {
                int gdix2 = (int)(nx * W);
                int gdiy2 = (int)((1f - ny) * H);
                int btnLeft2 = W - 12 - NotifBtnW - 10;

                if (gdix2 >= btnLeft2 && gdix2 <= btnLeft2 + NotifBtnW
                    && gdiy2 >= NotifContentY && gdiy2 < ScrollContentBottom)
                {
                    int scrolled2 = gdiy2 - NotifContentY + (int)_notifScrollY;
                    int row2   = scrolled2 / NotifItemH;
                    int local2 = scrolled2 % NotifItemH;
                    int btnTop2 = NotifItemH - 4 - NotifBtnH - 7;
                    if (row2 >= 0 && local2 >= btnTop2 && local2 <= btnTop2 + NotifBtnH)
                    {
                        {
                            List<NotifEntry> snapJ;
                            lock (_notifications) snapJ = new List<NotifEntry>(_notifications);
                            if (row2 < snapJ.Count)
                            {
                                var notif = snapJ[row2];
                                // Join button (friend_gps)
                                if (notif.EvType == "friend_gps" && !string.IsNullOrEmpty(notif.Location))
                                {
                                    bool inCooldown = _joinCooldowns.TryGetValue(notif.FriendId, out var cd)
                                        && (DateTime.UtcNow - cd).TotalSeconds < 5;
                                    if (!inCooldown)
                                    {
                                        _joinCooldowns[notif.FriendId] = DateTime.UtcNow;
                                        _dirty = true;
                                        OnJoinRequest?.Invoke(notif.FriendId, notif.Location);
                                    }
                                }
                                // Accept button (notification types)
                                else if (notif.EvType is "notif_friendreq" or "notif_groupinvite"
                                      && !string.IsNullOrEmpty(notif.NotifId))
                                {
                                    bool inCooldown = _joinCooldowns.TryGetValue(notif.NotifId, out var cd)
                                        && (DateTime.UtcNow - cd).TotalSeconds < 5;
                                    if (!inCooldown)
                                    {
                                        _joinCooldowns[notif.NotifId] = DateTime.UtcNow;
                                        _dirty = true;
                                        // Map overlay event type back to VRChat notification type
                                        string notifType = notif.EvType switch
                                        {
                                            "notif_friendreq"   => "friendRequest",
                                            "notif_groupinvite" => "group.invite",
                                            _ => ""
                                        };
                                        OnNotifAccept?.Invoke(notif.NotifId, notifType, notif.FriendId, notif.NotifData);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Friends tab: scrollable list — no pagination
            if (_activeTab == 5)
            {
                int gdixF = (int)(nx * W);
                int gdiyF = (int)((1f - ny) * H);

                if (gdiyF >= FrdContentY && gdiyF < ScrollContentBottom)
                {
                    // Account for scroll offset
                    int scrolledY = gdiyF - FrdContentY + (int)_friendsScrollY;
                    int row    = scrolledY / (FrdCardH + FrdGap);
                    int localY = scrolledY % (FrdCardH + FrdGap);

                    if (localY < FrdCardH)
                    {
                        List<FriendTabEntry> snapF;
                        lock (_onlineFriends) snapF = new List<FriendTabEntry>(_onlineFriends);
                        if (row >= 0 && row < snapF.Count)
                        {
                            var friend = snapF[row];
                            bool hasLoc  = !string.IsNullOrEmpty(friend.Location) && friend.Location != "offline";
                            int inviteX  = W - FrdPadX - 4 - 58;
                            int jrX      = (hasLoc ? inviteX - 6 : W - FrdPadX - 4) - 52;
                            if (hasLoc && gdixF >= inviteX && gdixF < inviteX + 58)
                            {
                                bool inCd = _joinCooldowns.TryGetValue(friend.FriendId, out var cd)
                                    && (DateTime.UtcNow - cd).TotalSeconds < 5;
                                if (!inCd)
                                {
                                    _joinCooldowns[friend.FriendId] = DateTime.UtcNow;
                                    _dirty = true;
                                    OnInviteFriend?.Invoke(friend.FriendId);
                                }
                            }
                            else if (gdixF >= jrX && gdixF < jrX + 52)
                            {
                                bool inCd = _joinCooldowns.TryGetValue(friend.FriendId + "#jr", out var cd)
                                    && (DateTime.UtcNow - cd).TotalSeconds < 5;
                                if (!inCd)
                                {
                                    bool canJoin = CanJoinLocation(friend.Location);
                                    _joinCooldowns[friend.FriendId + "#jr"] = DateTime.UtcNow;
                                    _dirty = true;
                                    OnJoinRequest?.Invoke(friend.FriendId, canJoin ? friend.Location : "");
                                }
                            }
                        }
                    }
                }
            }

            // Scale tab: +/- buttons
            // Layout (GDI): [-] x=68..156  [+] x=356..444  y=303..337
            if (_activeTab == TabSize && !_isScaleRecording)
            {
                int gx = (int)(nx * W);
                int gy = (int)((1f - ny) * H);
                if (gy >= 303 && gy <= 337)
                {
                    if (gx >= 68 && gx <= 156)
                    {
                        // minus
                        float delta = -0.1f;
                        _scaleValue = Math.Clamp(_scaleValue + delta, 0.01f, 10000f);
                        _dirty = true;
                        OnScaleChange?.Invoke(delta);
                    }
                    else if (gx >= 356 && gx <= 444)
                    {
                        // plus
                        float delta = 0.1f;
                        _scaleValue = Math.Clamp(_scaleValue + delta, 0.01f, 10000f);
                        _dirty = true;
                        OnScaleChange?.Invoke(delta);
                    }
                }
            }
        }

        private float GetFriendsMaxScroll()
        {
            int count;
            lock (_onlineFriends) count = _onlineFriends.Count;
            if (count == 0) return 0f;
            int totalH = count * (FrdCardH + FrdGap) - FrdGap;
            return Math.Max(0f, totalH - ScrollContentH);
        }

        // merges GetControllerState with _eventButtonsHeld to work whether Steam overlay is open or closed
        private void ReadThumb(int side, ref float tx, ref float ty)
        {
            float x, y;
            if (VrInputActions.Active)
            {
                if (!VrInputActions.GetThumb(side, out x, out y)) return;
            }
            else
            {
                var sys = _vrSystem;
                uint idx = side == 1 ? _leftIdx : _rightIdx;
                if (sys == null || idx == OpenVR.k_unTrackedDeviceIndexInvalid) return;

                var cs  = new VRControllerState_t();
                var csz = (uint)Marshal.SizeOf<VRControllerState_t>();
                if (!sys.GetControllerState(idx, ref cs, csz)) return;
                x = cs.rAxis0.x;
                y = cs.rAxis0.y;
            }
            if (MathF.Abs(y) > MathF.Abs(ty)) { ty = y; tx = x; }
        }

        private ulong GetMergedButtonState()
        {
            if (VrInputActions.Active) return VrInputActions.GetButtons(0);

            ulong state = _eventButtonsHeld;
            if (_vrSystem == null) return state;
            var s  = new VRControllerState_t();
            var sz = (uint)Marshal.SizeOf<VRControllerState_t>();
            if (_leftIdx  != OpenVR.k_unTrackedDeviceIndexInvalid)
                if (_vrSystem.GetControllerState(_leftIdx,  ref s, sz)) state |= s.ulButtonPressed;
            if (_rightIdx != OpenVR.k_unTrackedDeviceIndexInvalid)
                if (_vrSystem.GetControllerState(_rightIdx, ref s, sz)) state |= s.ulButtonPressed;
            return state;
        }

        // returns button state for a specific controller side (0=both, 1=left, 2=right)
        private ulong GetSideButtonState(int side)
        {
            if (side == 0) return GetMergedButtonState();
            if (VrInputActions.Active) return VrInputActions.GetButtons(side);

            uint idx   = side == 1 ? _leftIdx : _rightIdx;
            ulong held = side == 1 ? _eventLeftHeld : _eventRightHeld;

            if (_vrSystem != null && idx != OpenVR.k_unTrackedDeviceIndexInvalid)
            {
                var s  = new VRControllerState_t();
                var sz = (uint)Marshal.SizeOf<VRControllerState_t>();
                if (_vrSystem.GetControllerState(idx, ref s, sz)) held |= s.ulButtonPressed;
            }
            return held;
        }

        private void PollKeybindRecording()
        {
            ulong pressed  = GetMergedButtonState() & RecordButtonMask;
            int   bitCount = CountBits(pressed);

            // Combo: 1–4 buttons held stably. DoubleTap: exactly 1 button held stably.
            int minButtons = 1;
            int maxButtons = KeybindMode == 1 ? 1 : MAX_KEYBIND_BUTTONS;

            if (bitCount >= minButtons && bitCount <= maxButtons && pressed == _lastPressedButtons)
            {
                _stableFrames++;
                if (_stableFrames >= STABLE_FRAMES_REQUIRED)
                    FinishKeybindRecording(pressed);
            }
            else
            {
                _lastPressedButtons = pressed;
                _stableFrames = 0;
            }
        }

        private void PollKeybindTrigger()
        {
            bool activeSlotEmpty = KeybindMode == 1 ? KeybindDt.Count == 0 : Keybind.Count == 0;
            if (activeSlotEmpty) return;

            if (KeybindMode == 1)
            {
                // Double-tap mode
                ulong dtMask   = 1UL << (int)KeybindDt[0];
                ulong cur      = GetSideButtonState(KeybindDtHand) & dtMask;
                ulong newPress = cur & ~_prevTriggerHeld; // edge: newly pressed this frame
                _prevTriggerHeld = cur;

                if (newPress == 0)
                {
                    // No new press — re-arm once button has been released long enough
                    if (cur == 0)
                    {
                        _keybindReleaseFrames++;
                        if (_keybindReleaseFrames >= KEYBIND_RELEASE_REQUIRED)
                        {
                            _keybindTriggered = false;
                            _keybindReleaseFrames = 0;
                        }
                    }
                    return;
                }
                _keybindReleaseFrames = 0;

                // Take only the lowest set bit (first new button pressed this frame)
                uint btn = FirstSetBit(newPress);
                uint keybindBtn = KeybindDt.Count > 0 ? KeybindDt[0] : uint.MaxValue;
                if (btn != keybindBtn) { _doubleTapLastButton = uint.MaxValue; return; }

                var now = DateTime.UtcNow;
                if (btn == _doubleTapLastButton
                    && (now - _doubleTapLastTime).TotalMilliseconds < DOUBLE_TAP_WINDOW_MS
                    && !_keybindTriggered)
                {
                    _keybindTriggered     = true;
                    _doubleTapLastButton  = uint.MaxValue;
                    Toggle();
                }
                else
                {
                    _doubleTapLastButton = btn;
                    _doubleTapLastTime   = now;
                }
            }
            else
            {
                // Combo (hold) mode
                ulong mask = 0;
                foreach (var b in Keybind) mask |= 1UL << (int)b;
                bool allHeld = mask != 0 && (GetSideButtonState(KeybindHand) & mask) == mask;

                if (allHeld)
                {
                    _keybindReleaseFrames = 0;
                    if (!_keybindTriggered) { _keybindTriggered = true; Toggle(); }
                }
                else
                {
                    _keybindReleaseFrames++;
                    if (_keybindReleaseFrames >= KEYBIND_RELEASE_REQUIRED)
                    {
                        _keybindTriggered = false;
                        _keybindReleaseFrames = 0;
                    }
                }
            }
        }

        private void FinishKeybindRecording(ulong pressed)
        {
            IsRecording = false;
            _stableFrames = 0;

            var ids   = new List<uint>();
            var names = new List<string>();
            int added = 0;
            for (int b = 0; b < 64 && added < MAX_KEYBIND_BUTTONS; b++)
            {
                if ((pressed & (1UL << b)) != 0)
                {
                    var id = (uint)b;
                    ids.Add(id);
                    names.Add(ButtonLabel(id));
                    added++;
                }
            }

            // Determine which controller side the combo came from
            bool leftHasAll  = (GetSideButtonState(1) & pressed) == pressed;
            bool rightHasAll = (GetSideButtonState(2) & pressed) == pressed;
            int hand = leftHasAll && !rightHasAll ? 1
                     : rightHasAll && !leftHasAll ? 2
                     : 0;

            if (KeybindMode == 1) { KeybindDt = ids; KeybindDtHand = hand; }
            else                  { Keybind = ids;   KeybindHand   = hand; }

            string modeLabel = KeybindMode == 1 ? "DoubleTap" : "Combo";
            string side      = hand == 1 ? "Left" : hand == 2 ? "Right" : "Any";
            _log($"[VROverlay] Keybind recorded ({modeLabel}): {side} — {string.Join("+", names)}");
            OnKeybindRecorded?.Invoke(ids, names, hand, KeybindMode);
            EmitState();
        }

        private void PollScaleKeybindRecording()
        {
            ulong pressed  = GetMergedButtonState() & RecordButtonMask;
            int   bitCount = CountBits(pressed);

            if (bitCount >= 1 && bitCount <= MAX_KEYBIND_BUTTONS && pressed == _scaleLastPressed)
            {
                _scaleStableFrames++;
                if (_scaleStableFrames >= STABLE_FRAMES_REQUIRED)
                    FinishScaleKeybindRecording(pressed);
            }
            else
            {
                _scaleLastPressed  = pressed;
                _scaleStableFrames = 0;
            }
        }

        private void FinishScaleKeybindRecording(ulong pressed)
        {
            _isScaleRecording  = false;
            _scaleStableFrames = 0;

            var ids   = new List<uint>();
            var names = new List<string>();
            int added = 0;
            for (int b = 0; b < 64 && added < MAX_KEYBIND_BUTTONS; b++)
            {
                if ((pressed & (1UL << b)) != 0)
                {
                    var id = (uint)b;
                    ids.Add(id);
                    names.Add(ButtonLabel(id));
                    added++;
                }
            }

            bool leftHasAll  = (GetSideButtonState(1) & pressed) == pressed;
            bool rightHasAll = (GetSideButtonState(2) & pressed) == pressed;
            int hand = leftHasAll && !rightHasAll ? 1
                     : rightHasAll && !leftHasAll ? 2
                     : 0;

            _scaleKeybind     = ids;
            _scaleKeybindHand = hand;

            _log($"[VROverlay] Scale keybind recorded: {string.Join("+", names)}");
            OnScaleKeybindRecorded?.Invoke(ids, names, hand);
            EmitState();
        }

        private static uint FirstSetBit(ulong v)
        {
            for (int b = 0; b < 64; b++)
                if ((v & (1UL << b)) != 0) return (uint)b;
            return uint.MaxValue;
        }

        private static int CountBits(ulong v)
        {
            int c = 0;
            while (v != 0) { c += (int)(v & 1); v >>= 1; }
            return c;
        }

        private void UpdateControllerIndices()
        {
            if (_vrSystem == null) return;
            _leftIdx  = _vrSystem.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.LeftHand);
            _rightIdx = _vrSystem.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.RightHand);
        }

        private void UpdateStats()
        {
            _hmdBattery   = ReadBattery(OpenVR.k_unTrackedDeviceIndex_Hmd);
            _leftBattery  = ReadBattery(_leftIdx);
            _rightBattery = ReadBattery(_rightIdx);

            var comp = OpenVR.Compositor;
            if (comp != null)
            {
                var ft = new Compositor_FrameTiming { m_nSize = (uint)Marshal.SizeOf<Compositor_FrameTiming>() };
                if (comp.GetFrameTiming(ref ft, 0))
                {
                    if (_lastFrameTime > 0)
                    {
                        double dt  = ft.m_flSystemTimeInSeconds - _lastFrameTime;
                        long   dfi = (long)ft.m_nFrameIndex - (long)_lastFrameIndex;
                        if (dt > 0.1 && dfi > 0 && dfi < 100000)
                        {
                            float inst = (float)(dfi / dt);
                            _fps = _fps <= 0f ? inst : _fps * 0.5f + inst * 0.5f;
                        }
                    }
                    _lastFrameIndex = ft.m_nFrameIndex;
                    _lastFrameTime  = ft.m_flSystemTimeInSeconds;
                }
            }
        }

        private float ReadBattery(uint idx)
        {
            var sys = _vrSystem;
            if (sys == null || idx == OpenVR.k_unTrackedDeviceIndexInvalid) return -1f;

            var err = ETrackedPropertyError.TrackedProp_Success;
            float pct = sys.GetFloatTrackedDeviceProperty(idx, ETrackedDeviceProperty.Prop_DeviceBatteryPercentage_Float, ref err);
            if (err != ETrackedPropertyError.TrackedProp_Success) return -1f;
            return Math.Clamp(pct, 0f, 1f);
        }

        private void ApplyTransform()
        {
            if (!IsConnected || OpenVR.Overlay == null || _overlayHandle == 0) return;

            var idx = AttachToLeft ? _leftIdx : _rightIdx;
            if (idx == OpenVR.k_unTrackedDeviceIndexInvalid) return;

            var transform = BuildTransform(PosX, PosY, PosZ, RotX, RotY, RotZ);
            OpenVR.Overlay.SetOverlayTransformTrackedDeviceRelative(_overlayHandle, idx, ref transform);
        }

        private static HmdMatrix34_t BuildTransform(float px, float py, float pz, float rxDeg, float ryDeg, float rzDeg)
        {
            var m = Matrix4x4.CreateFromYawPitchRoll(
                ryDeg * MathF.PI / 180f,
                rxDeg * MathF.PI / 180f,
                rzDeg * MathF.PI / 180f);
            return new HmdMatrix34_t
            {
                m0 = m.M11, m1 = m.M12, m2 = m.M13, m3 = px,
                m4 = m.M21, m5 = m.M22, m6 = m.M23, m7 = py,
                m8 = m.M31, m9 = m.M32, m10 = m.M33, m11 = pz
            };
        }

        private void EmitState()
        {
            OnStateUpdate?.Invoke(new
            {
                connected  = IsConnected,
                visible    = IsVisible,
                recording  = IsRecording,
                keybind    = Keybind,
                keybindNames = GetKeybindNames(),
                keybindHand  = KeybindHand,
                keybindMode  = KeybindMode,
                keybindDt     = KeybindDt,
                keybindDtHand = KeybindDtHand,
                leftController  = _leftIdx  != OpenVR.k_unTrackedDeviceIndexInvalid,
                rightController = _rightIdx != OpenVR.k_unTrackedDeviceIndexInvalid,
                error           = LastError,
                scaleRecording  = _isScaleRecording
            });
        }

        private List<string> GetKeybindNames()
        {
            var names = new List<string>();
            foreach (var id in Keybind)
                names.Add(ButtonLabel(id));
            return names;
        }

        // Toast overlay (HMD-attached, separate from the wrist overlay).

        private void TickToast()
        {
            if (_toastHandle == 0 || OpenVR.Overlay == null) return;

            // If toasts are disabled, immediately dismiss all and clear queue
            if (!_toastEnabled)
            {
                if (_activeToasts.Count > 0)
                {
                    _activeToasts.Clear();
                    OpenVR.Overlay.SetOverlayAlpha(_toastHandle, 0f);
                    OpenVR.Overlay.HideOverlay(_toastHandle);
                    _toastDirty = false;
                }
                lock (_toastQueue) _toastQueue.Clear();
                return;
            }

            bool hadActive = _activeToasts.Count > 0;

            // Expire finished toasts
            for (int i = _activeToasts.Count - 1; i >= 0; i--)
            {
                double elapsed = (DateTime.UtcNow - _activeToasts[i].StartTime).TotalMilliseconds;
                if (elapsed >= _toastTotalMs)
                    _activeToasts.RemoveAt(i);
            }

            // Dequeue new toasts into free slots
            while (_activeToasts.Count < _toastStackSize)
            {
                ToastItem? next = null;
                lock (_toastQueue) { if (_toastQueue.Count > 0) next = _toastQueue.Dequeue(); }
                if (next == null) break;
                _activeToasts.Add(new ActiveToast(next, DateTime.UtcNow));
                OnToastSound?.Invoke();
            }

            // Show/hide overlay based on active toast count
            if (_activeToasts.Count > 0 && !hadActive)
            {
                ApplyToastTransform();
                OpenVR.Overlay.ShowOverlay(_toastHandle);
                OpenVR.Overlay.SetOverlayAlpha(_toastHandle, 1f);
            }
            else if (_activeToasts.Count == 0 && hadActive)
            {
                OpenVR.Overlay.SetOverlayAlpha(_toastHandle, 0f);
                OpenVR.Overlay.HideOverlay(_toastHandle);
                _toastDirty = false;
                return;
            }

            if (_activeToasts.Count > 0)
            {
                _toastDirty = true; // always re-render for progress bars + per-toast alpha
            }

            if (_toastDirty && _activeToasts.Count > 0)
            {
                _toastDirty = false;
                RenderToast();
            }
        }

        private const float ToastFollowTau  = 0.20f;
        private const float ToastFollowDist = 0.45f;
        private readonly TrackedDevicePose_t[] _toastPoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        private Vector3 _toastPos;
        private Vector3 _toastFwd = -Vector3.UnitZ;
        private bool    _toastPoseInit;
        private DateTime _toastPoseLast = DateTime.UtcNow;

        private void UpdateToastFollow()
        {
            if (_toastHandle == 0 || OpenVR.Overlay == null || _vrSystem == null) return;

            lock (_activeToasts) { if (_activeToasts.Count == 0) { _toastPoseInit = false; return; } }

            _vrSystem.GetDeviceToAbsoluteTrackingPose(
                ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, _toastPoses);

            var hmdIdx = OpenVR.k_unTrackedDeviceIndex_Hmd;
            if (!_toastPoses[hmdIdx].bPoseIsValid) return;
            var m = _toastPoses[hmdIdx].mDeviceToAbsoluteTracking;

            var hmdPos = new Vector3(m.m3, m.m7, m.m11);
            var fwd    = new Vector3(-m.m2, -m.m6, -m.m10);
            if (fwd.LengthSquared() < 1e-6f) return;
            fwd = Vector3.Normalize(fwd);

            var right = Vector3.Cross(fwd, Vector3.UnitY);
            if (right.LengthSquared() < 1e-6f) right = new Vector3(m.m0, m.m4, m.m8);
            right = Vector3.Normalize(right);
            var up = Vector3.Normalize(Vector3.Cross(right, fwd));

            float widthMeters = 0.10f + _toastSize * 0.002f;
            float yComp = (widthMeters * TH_FULL / TW - widthMeters * TH / TW) / 2f;
            var target = hmdPos + fwd * ToastFollowDist
                       + right * _toastOffsetX
                       + up * (_toastOffsetY + yComp);

            var now = DateTime.UtcNow;
            float dt = Math.Clamp((float)(now - _toastPoseLast).TotalSeconds, 0.001f, 0.1f);
            _toastPoseLast = now;

            if (!_toastPoseInit)
            {
                _toastPos = target;
                _toastFwd = fwd;
                _toastPoseInit = true;
            }
            else
            {
                float a = 1f - MathF.Exp(-dt / ToastFollowTau);
                _toastPos = Vector3.Lerp(_toastPos, target, a);
                var blended = Vector3.Lerp(_toastFwd, fwd, a);
                if (blended.LengthSquared() > 1e-6f) _toastFwd = Vector3.Normalize(blended);
            }

            var r2 = Vector3.Cross(_toastFwd, Vector3.UnitY);
            if (r2.LengthSquared() < 1e-6f) r2 = right;
            r2 = Vector3.Normalize(r2);
            var u2 = Vector3.Normalize(Vector3.Cross(r2, _toastFwd));

            var t = new HmdMatrix34_t
            {
                m0 = r2.X, m1 = u2.X, m2  = -_toastFwd.X, m3  = _toastPos.X,
                m4 = r2.Y, m5 = u2.Y, m6  = -_toastFwd.Y, m7  = _toastPos.Y,
                m8 = r2.Z, m9 = u2.Z, m10 = -_toastFwd.Z, m11 = _toastPos.Z
            };
            OpenVR.Overlay.SetOverlayTransformAbsolute(_toastHandle,
                ETrackingUniverseOrigin.TrackingUniverseStanding, ref t);
        }

        private void ApplyToastTransform()
        {
            if (_toastHandle == 0 || OpenVR.Overlay == null) return;
            // Compensate Y for taller bitmap: anchor bottom of overlay at the same position
            // as the original single-toast overlay. The overlay center shifts upward when
            // the bitmap is taller, so we subtract the extra half-height.
            float widthMeters = 0.10f + _toastSize * 0.002f;
            float fullHeightM = widthMeters * TH_FULL / TW;
            float singleHeightM = widthMeters * TH / TW;
            float yCompensation = (fullHeightM - singleHeightM) / 2f;
            _toastPoseInit = false;
            _toastPoseLast = DateTime.UtcNow;
            UpdateToastFollow();
        }

        private float ComputeToastAlpha(double elapsedMs)
        {
            float alpha;
            if (elapsedMs < TOAST_FADE_IN_MS)
                alpha = (float)(elapsedMs / TOAST_FADE_IN_MS);
            else if (elapsedMs < TOAST_FADE_IN_MS + _toastVisibleMs)
                alpha = 1f;
            else
                alpha = 1f - (float)((elapsedMs - TOAST_FADE_IN_MS - _toastVisibleMs) / TOAST_FADE_OUT_MS);
            return Math.Clamp(alpha, 0f, 1f);
        }

        private void RenderToast()
        {
            if (_d2d == null || _toastTarget == null || _activeToasts.Count == 0 || OpenVR.Overlay == null || _toastHandle == 0) return;
            try
            {
                lock (_renderLock)
                {
                using (var g = _d2d.CreateGraphics(_toastTarget))
                {
                    g.SmoothingMode     = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    g.Clear(Color.Transparent);

                    // Draw toasts bottom-up: index 0 at the bottom of the texture
                    for (int i = 0; i < _activeToasts.Count; i++)
                    {
                        var at = _activeToasts[i];
                        double elapsed = (DateTime.UtcNow - at.StartTime).TotalMilliseconds;
                        float alpha = ComputeToastAlpha(elapsed);
                        int y = TH_FULL - (i + 1) * TH - i * TH_GAP;
                        DrawToastContent(g, at.Item, y, alpha, elapsed);
                    }
                }

                PresentToastTexture();
                }
            }
            catch (Exception ex) { _log($"[VROverlay] ToastRender: {ex.Message}"); }
        }

        // Helper: multiply an alpha value by the per-toast fade alpha
        private static int A(int baseAlpha, float fade) => Math.Clamp((int)(baseAlpha * fade), 0, 255);

        private void DrawToastContent(Graphics g, ToastItem toast, int oY, float fade, double elapsedMs)
        {
            var th = _theme;

            // Background — rounded card
            using var bg = new SolidBrush(Color.FromArgb(A(220, fade), th.BgCard));
            FillRoundedRect(g, bg, 0, oY, TW, TH, 14);

            // Border
            using var brdPen = new Pen(Color.FromArgb(A(80, fade), th.Brd), 1f);
            DrawRoundedRect(g, brdPen, 0, oY, TW, TH, 14);

            // Avatar — 36x36, rounded
            const int avSize = 36, avR = 8;
            int avX = 12, avY = oY + (TH - avSize - 6) / 2; // leave room for progress bar

            Bitmap? avatar = null;
            if (!string.IsNullOrEmpty(toast.ImageUrl))
                lock (_notifImgCache) { _notifImgCache.TryGetValue(toast.ImageUrl, out avatar); }

            var oldClip = g.Clip;
            using var avPath = RoundedRectPath(avX, avY, avSize, avSize, avR);
            g.SetClip(avPath, CombineMode.Intersect);
            if (avatar != null)
            {
                var avDest = new Rectangle(avX, avY, avSize, avSize);
                g.DrawImage(avatar, avDest, new Rectangle(0, 0, avatar.Width, avatar.Height), fade);
            }
            else
            {
                using var avBg = new SolidBrush(Color.FromArgb(A(255, fade), th.BgHover));
                g.FillPath(avBg, avPath);
                g.ResetClip();
                if (!DrawNotifTypeIcon(g, toast.EvType, avX, avY, avSize, Color.FromArgb(A(255, fade), th.Accent)))
                {
                    string initials = toast.FriendName.Length > 0 ? toast.FriendName[0].ToString().ToUpper() : "?";
                    using var initFont  = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Point);
                    using var initBrush = new SolidBrush(Color.FromArgb(A(255, fade), th.Tx2));
                    var initFmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(initials, initFont, initBrush, new RectangleF(avX, avY, avSize, avSize), initFmt);
                }
            }
            g.SetClip(oldClip, CombineMode.Replace);

            if (EventHasStatusDot(toast.EvType))
            {
                var toastSt = FriendStatus(toast.FriendId);
                if (!string.IsNullOrEmpty(toastSt))
                    DrawStatusDot(g, toastSt, avX + avSize - 3f, avY + avSize - 3f, 5f,
                                  Color.FromArgb(A(220, fade), th.BgCard));
            }

            int textX = avX + avSize + 10;
            int textRight = TW - 14;

            var evColor = EventColor(toast.EvType);
            float row1Y = avY + 2f;
            using var nameFont  = new Font("Segoe UI", 10.5f, FontStyle.Bold, GraphicsUnit.Point);
            using var nameBrush = new SolidBrush(Color.FromArgb(A(255, fade), th.Tx1));
            var ellipsisFmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(toast.FriendName, nameFont, nameBrush,
                new RectangleF(textX, row1Y, Math.Max(textRight - textX, 20f), 18f), ellipsisFmt);

            // Row 2: event content text
            float row2Y = row1Y + 18f + 1f;
            string evText = EventSentence(toast.EvType, toast.EvText);
            using var evFont  = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            using var evBrush = new SolidBrush(Color.FromArgb(A(255, fade), th.Tx3));
            g.DrawString(evText, evFont, evBrush,
                new RectangleF(textX, row2Y, textRight - textX, 16f), ellipsisFmt);

            // Progress bar at bottom
            double barProgress;
            if (elapsedMs < TOAST_FADE_IN_MS) barProgress = 0;
            else if (elapsedMs >= TOAST_FADE_IN_MS + _toastVisibleMs) barProgress = 1;
            else barProgress = (elapsedMs - TOAST_FADE_IN_MS) / _toastVisibleMs;

            int barYPos = oY + TH - 4;
            int barH = 3;
            int barFullW = TW - 24;
            int barX = 12;
            int barW = (int)(barFullW * barProgress);

            // Track
            using var trackBrush = new SolidBrush(Color.FromArgb(A(60, fade), th.Tx3));
            FillRoundedRect(g, trackBrush, barX, barYPos, barFullW, barH, 2);

            // Fill
            if (barW > 0)
            {
                using var fillBrush = new SolidBrush(Color.FromArgb(A(180, fade), evColor));
                FillRoundedRect(g, fillBrush, barX, barYPos, barW, barH, 2);
            }
        }

        private void PresentToastTexture()
        {
            if (_toastOverlayTex == null || OpenVR.Overlay == null || _toastHandle == 0) return;
            var tex = new Valve.VR.Texture_t
            {
                handle      = _toastOverlayTex.NativePointer,
                eType       = ETextureType.DirectX,
                eColorSpace = EColorSpace.Auto,
            };
            OpenVR.Overlay.SetOverlayTexture(_toastHandle, ref tex);
            _d3dContext?.Flush();
        }

        // Rendering

        private void Render()
        {
            if (_d2d == null || _overlayTarget == null || OpenVR.Overlay == null || _overlayHandle == 0) return;
            try
            {
                bool scrolling = _scrollDragging
                    || MathF.Abs(_locationScrollVY) > 0.5f
                    || MathF.Abs(_friendsScrollVY)  > 0.5f;

                lock (_renderLock)
                {
                using (var g = _d2d.CreateGraphics(_overlayTarget))
                {
                    g.SmoothingMode     = scrolling ? SmoothingMode.None : SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    g.InterpolationMode = scrolling ? InterpolationMode.Bilinear
                                                    : InterpolationMode.HighQualityBicubic;
                    g.Clear(Color.Transparent);
                    g.ScaleTransform(RenderScale, RenderScale);

                    DrawBackground(g);
                    DrawHeader(g);
                    var hdrState = g.Save();
                    g.TranslateTransform(0, HeaderH);
                    DrawTabBar(g);
                    var tabClip = g.Clip;
                    g.SetClip(new Rectangle(0, TabBarBottom, W, H - TabBarBottom), CombineMode.Intersect);
                    if      (_activeTab == 1) DrawNotifications(g);
                    else if (_activeTab == 2) DrawLocations(g);
                    else if (_activeTab == 3) DrawMusicPlayer(g);
                    else if (_activeTab == 4) DrawTools(g);
                    else if (_activeTab == 5) DrawFriends(g);
                    else if (_activeTab == TabKikitan) DrawKikitan(g);
                    else if (_activeTab == TabSize) DrawScaleTab(g);
                    g.SetClip(tabClip, CombineMode.Replace);
                    tabClip.Dispose();
                    if (_waterAlarmActive) DrawDashboardAlarm(g);
                    g.Restore(hdrState);
                }

                PresentOverlayTexture();
                }
            }
            catch (Exception ex)
            {
                _log($"[VROverlay] Render: {ex.Message}");
            }
        }

        private void DrawBackground(Graphics g)
        {
            var th = _theme;
            const int r = 24;

            bool hasArt = _activeTab == 3 && _albumArt != null && !string.IsNullOrWhiteSpace(_mediaTitle);

            if (hasArt)
            {
                // Music tab: blurred art fills entire card
                // Clip drawing to rounded card shape
                using var cardClip = RoundedRectPath(0, HeaderH, W, H, r);
                using var oldClip = g.Clip;
                g.SetClip(cardClip, CombineMode.Intersect);

                g.DrawImageBlurred(_albumArt!, new Rectangle(0, HeaderH, W, H), 64, 48);

                // Dark overlay — 50% darker so UI elements stay readable
                using var darkOver = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
                g.FillRectangle(darkOver, 0, HeaderH, W, H);

                // Top gradient: solid bg-card → transparent, ends just above cover art (artY=78)
                // Keeps tab buttons legible while art bleeds through below
                using var topGrad = new LinearGradientBrush(
                    new Point(0, HeaderH), new Point(0, HeaderH + 78),
                    Color.FromArgb(220, th.BgCard),
                    Color.FromArgb(0,   th.BgCard));
                g.FillRectangle(topGrad, 0, HeaderH, W, 78);

                // Bottom gradient: transparent → dark, starts just below cover art (artBottom=206)
                using var botGrad = new LinearGradientBrush(
                    new Point(0, 206 + HeaderH), new Point(0, TexH),
                    Color.FromArgb(0,   th.BgCard),
                    Color.FromArgb(180, th.BgCard));
                g.FillRectangle(botGrad, 0, 206 + HeaderH, W, TexH - (206 + HeaderH));

                g.SetClip(oldClip, CombineMode.Replace);
            }
            else
            {
                // All other tabs: solid themed card
                using var brush = new SolidBrush(Color.FromArgb(235, th.BgCard));
                FillRoundedRect(g, brush, 0, HeaderH, W, H, r);
            }

            // Card border always on top
            using var pen = new Pen(Color.FromArgb(80, th.Brd), 1.5f);
            DrawRoundedRect(g, pen, 1, HeaderH + 1, W - 2, H - 2, r - 1);
        }

        private void DrawHeader(Graphics g)
        {
            var now = DateTime.Now;
            int padX = 8;
            var sfNoPad = StringFormat.GenericTypographic;

            using (var tf = new Font("Segoe UI", 18f, FontStyle.Bold, GraphicsUnit.Point))
            using (var wb = new SolidBrush(Color.White))
                g.DrawString(now.ToString("HH:mm:ss"), tf, wb, new RectangleF(padX, 3f, W, 30f), sfNoPad);

            using (var df = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point))
            using (var wb2 = new SolidBrush(Color.White))
            {
                var culture = System.Globalization.CultureInfo.GetCultureInfoByIetfLanguageTag(_language);
                string line = now.ToString("dd.MM.yyyy") + "   |   " + now.ToString("dddd", culture);
                if (_waterEnabled)
                {
                    long ws = Math.Max(0, _waterRemainMs / 1000);
                    line += $"   |   Water: {ws / 3600:D2}:{ws / 60 % 60:D2}:{ws % 60:D2}";
                }
                g.DrawString(line, df, wb2, new RectangleF(padX, 37f, W, 16f), sfNoPad);
            }

            DrawHeaderStats(g);
            DrawHeaderAvatar(g);
        }

        private void DrawHeaderStats(Graphics g)
        {
            const int avSz = 36;
            int statsRight = W - avSz - 12 - 12;

            var parts = new System.Collections.Generic.List<string>
            {
                $"FPS: {(_fps > 0f ? (int)MathF.Round(_fps) : 0)}"
            };
            if (_leftBattery  >= 0f) parts.Add($"L: {(int)MathF.Round(_leftBattery  * 100f)}%");
            if (_rightBattery >= 0f) parts.Add($"R: {(int)MathF.Round(_rightBattery * 100f)}%");
            if (_hmdBattery   >= 0f) parts.Add($"HMD: {(int)MathF.Round(_hmdBattery * 100f)}%");
            string stats = string.Join("    ", parts);

            using var sf = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Center
            };
            using var font = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
            using var brush = new SolidBrush(Color.FromArgb(230, 255, 255, 255));
            g.DrawString(stats, font, brush, new RectangleF(120f, 36f, statsRight - 120f, 18f), sf);
        }

        private void DrawHeaderAvatar(Graphics g)
        {
            var th = _theme;
            const int avSz = 36, avR = 8;
            int avX = W - avSz - 12;
            int avY = (HeaderH - avSz) / 2;

            Bitmap? avImg = null;
            if (!string.IsNullOrEmpty(_selfImageUrl))
                lock (_locationImgCache) { _locationImgCache.TryGetValue(_selfImageUrl, out avImg); }

            var avRect = new Rectangle(avX, avY, avSz, avSz);
            var oldClip = g.Clip;
            using var avPath = RoundedRectPath(avX, avY, avSz, avSz, avR);
            g.SetClip(avPath, CombineMode.Intersect);
            if (avImg != null)
                DrawImageCover(g, avImg, avRect);
            else
            {
                using var avBg = new SolidBrush(th.BgHover);
                g.FillPath(avBg, avPath);
            }
            g.SetClip(oldClip, CombineMode.Replace);
            using var avBorder = new Pen(Color.FromArgb(50, th.Brd), 1f);
            DrawRoundedRect(g, avBorder, avX, avY, avSz, avSz, avR);

            int dotSz = 10;
            int dotX = avX + avSz - dotSz + 1;
            int dotY = avY + avSz - dotSz + 1;
            var statusColor = StatusColor(_selfStatus);
            using var dotBg = new SolidBrush(th.BgCard);
            g.FillEllipse(dotBg, dotX - 2, dotY - 2, dotSz + 4, dotSz + 4);
            using var dotBrush = new SolidBrush(statusColor);
            g.FillEllipse(dotBrush, dotX, dotY, dotSz, dotSz);
        }

        private void DrawTabBar(Graphics g)
        {
            var th = _theme;
            int tabH   = 50;
            int tabX   = 8;
            int tabTW  = W - 16;
            var tabs   = VisibleTabs();
            int tabW   = tabTW / Math.Max(1, tabs.Count);

            bool artBg = _activeTab == 3 && _albumArt != null && !string.IsNullOrWhiteSpace(_mediaTitle);
            if (!artBg)
            {
                using var tabBg = new SolidBrush(Color.FromArgb(50, th.BgHover));
                FillRoundedRect(g, tabBg, tabX, 8, tabTW, tabH, 14);
            }

            // Sliding active indicator
            int indicatorW = tabW - 4;
            using var indicatorBg = new SolidBrush(Color.FromArgb(200, th.Accent));
            FillRoundedRect(g, indicatorBg, (int)_tabIndicatorX, 10, indicatorW, tabH - 4, 12);

            for (int i = 0; i < tabs.Count; i++)
            {
                int id = tabs[i];
                int tw = i == tabs.Count - 1 ? tabTW - tabW * i : tabW;
                string icon = id switch
                {
                    TabAlerts   => "\uE7F4",
                    TabLocation => _activeTab == TabLocation && _openWorldKey != null ? "\uE5C4" : "\uE0C8",
                    TabMusic    => "\uE405",
                    TabTools    => "\uE869",
                    TabFriends  => "\uE7FB",
                    TabKikitan  => "\uE8E2",
                    _           => "\uEA16",
                };
                DrawTab(g, icon, "", id, tabX + tabW * i, 8, tw, tabH);
            }

            if (!artBg)
            {
                using var sep = new Pen(Color.FromArgb(60, th.Brd), 1f);
                g.DrawLine(sep, 12, 8 + tabH + 2, W - 12, 8 + tabH + 2);
            }
        }

        private void DrawTab(Graphics g, string icon, string label, int index, int x, int y, int w, int h)
        {
            var th = _theme;
            bool active = _activeTab == index;
            // Active background is now drawn as a sliding indicator in DrawTabBar

            using var brush = new SolidBrush(active ? Color.White : Color.FromArgb(180, th.Tx2));
            var fmtC = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            // Icon only — centered in full tab height
            using var iconFont = _matSymFamily != null
                ? new Font(_matSymFamily, 18f, FontStyle.Regular, GraphicsUnit.Point)
                : new Font("Segoe MDL2 Assets", 18f, FontStyle.Regular, GraphicsUnit.Point);
            g.DrawString(icon, iconFont, brush, new RectangleF(x, y, w, h), fmtC);
        }

        private void DrawDashboardAlarm(Graphics g)
        {
            var th   = _theme;
            var fmtC = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            const int padX = 20;

            // Clip to overlay's rounded shape (r=24, same as DrawBackground)
            var oldClip = g.Clip;
            using var alarmClip = RoundedRectPath(0, 0, W, H, 24);
            g.SetClip(alarmClip, CombineMode.Intersect);

            // Dark overlay — covers everything inside rounded rect
            using (var ovBr = new SolidBrush(Color.FromArgb(250, 6, 9, 20)))
                g.FillRectangle(ovBr, 0, 0, W, H);

            // Bounce animation (±4px at ~1.5Hz)
            float bounce = (float)(Math.Sin(DateTime.UtcNow.TimeOfDay.TotalSeconds * Math.PI * 1.5) * 4.0);

            // Button layout (anchored to bottom)
            double t   = DateTime.UtcNow.TimeOfDay.TotalSeconds;
            int    btH = 50, btW = W - padX * 3, btX = padX + padX / 2;
            int    btY = H - btH - 22;

            // Icon + text block: center vertically in the space above the button
            // Block: icon(54) + 10 + title(28) + 8 + subtitle(18) = 118px
            int blockH    = 54 + 10 + 28 + 8 + 18;
            int blockTopY = (btY - blockH) / 2;          // center in [0, btY]
            int iconY     = blockTopY;
            int titleY    = iconY  + 54 + 10;
            int subY      = titleY + 28 + 8;

            // Water drop icon
            using (var iconFnt = _matSymFamily != null
                ? new Font(_matSymFamily, 42f, FontStyle.Regular, GraphicsUnit.Point)
                : new Font("Segoe MDL2 Assets", 42f, FontStyle.Regular, GraphicsUnit.Point))
            using (var iconBr = new SolidBrush(th.Cyan))
                g.DrawString("\uE798", iconFnt, iconBr,
                    new RectangleF(0, iconY + bounce, W, 54), fmtC);

            // "Drink Water!" title
            using (var tf = new Font("Segoe UI", 17f, FontStyle.Bold, GraphicsUnit.Point))
            using (var tb = new SolidBrush(th.Cyan))
                g.DrawString(VroL("alarm_title"), tf, tb,
                    new RectangleF(padX, titleY + bounce, W - padX * 2, 28), fmtC);

            // Subtitle
            using (var sf = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point))
            using (var sb = new SolidBrush(th.Tx3))
                g.DrawString(VroL("alarm_sub"), sf, sb,
                    new RectangleF(padX, subY + bounce, W - padX * 2, 18), fmtC);

            // Pulsing button (fixed at bottom, no bounce)
            int glowAlpha = (int)(20 + Math.Sin(t * Math.PI * 2) * 15);
            using (var glowBr = new SolidBrush(Color.FromArgb(Math.Clamp(glowAlpha, 5, 40), th.Cyan)))
                FillRoundedRect(g, glowBr, btX - 4, btY - 4, btW + 8, btH + 8, 14);
            using (var btnBr = new SolidBrush(th.Cyan))
                FillRoundedRect(g, btnBr, btX, btY, btW, btH, 10);
            using (var btnTf = new Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Point))
            using (var btnTb = new SolidBrush(Color.FromArgb(12, 18, 36)))
                g.DrawString(VroL("alarm_btn"), btnTf, btnTb,
                    new RectangleF(btX, btY, btW, btH), fmtC);

            g.Clip = oldClip;
            fmtC.Dispose();
        }

        private const int WdHeadH   = 50;
        private const int WdInstH   = 20;
        private const int WdRowGap  = 5;
        private const int ActBtnW   = 58;
        private const int ActBtnH   = 42;

        private record WorldGroup(string WorldId, string WorldName, string WorldImageUrl,
                                  List<List<LocationEntry>> Instances, List<LocationEntry> All);

        private List<WorldGroup>? _worldGroupsCache;

        private List<WorldGroup> GetWorldGroups()
        {
            var cached = _worldGroupsCache;
            if (cached != null) return cached;

            List<WorldGroup> built;
            lock (_friendLocations)
                built = _friendLocations
                    .GroupBy(e => e.WorldId)
                    .Select(w =>
                    {
                        var all = w.ToList();
                        var inst = all.GroupBy(e => e.InstanceId).Select(i => i.ToList()).ToList();
                        return new WorldGroup(w.Key, all[0].WorldName, all[0].WorldImageUrl, inst, all);
                    })
                    .ToList();
            _worldGroupsCache = built;
            return built;
        }

        private WorldGroup? OpenWorldGroup()
            => _openWorldKey == null ? null : GetWorldGroups().FirstOrDefault(w => w.WorldId == _openWorldKey);

        private float GetLocationMaxScroll()
        {
            var open = OpenWorldGroup();
            if (open != null)
            {
                int total = 0;
                foreach (var inst in open.Instances)
                    total += WdInstH + inst.Count * (FrdCardH + WdRowGap);
                return Math.Max(0f, total - (ScrollContentH - WdHeadH));
            }
            int rows = (GetWorldGroups().Count + 1) / 2;
            return Math.Max(0f, rows * (LocCardH + LocRowGap) - ScrollContentH);
        }

        private static string InstanceShortId(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return "#?";
            var cut = instanceId.IndexOf('~');
            var id = cut > 0 ? instanceId.Substring(0, cut) : instanceId;
            return "#" + id;
        }

        private void DrawCircleAvatar(Graphics g, string imageUrl, string name, int cx, int cy, int size, Color ring)
        {
            Bitmap? img = null;
            if (!string.IsNullOrEmpty(imageUrl))
                lock (_locationImgCache) { _locationImgCache.TryGetValue(imageUrl, out img); }

            using (var ringBr = new SolidBrush(ring))
                g.FillEllipse(ringBr, cx - 2, cy - 2, size + 4, size + 4);

            var dest = new Rectangle(cx, cy, size, size);

            var oldClip = g.Clip;
            using var path = new GraphicsPath();
            path.AddEllipse(cx, cy, size, size);
            g.SetClip(path, CombineMode.Intersect);
            if (img != null)
            {
                var prevMode = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.Bilinear;
                DrawImageCover(g, img, dest);
                g.InterpolationMode = prevMode;
                g.SetClip(oldClip, CombineMode.Replace);
            }
            else
            {
                using var bg = new SolidBrush(_theme.BgHover);
                g.FillPath(bg, path);
                g.SetClip(oldClip, CombineMode.Replace);
                string init = name.Length > 0 ? name[0].ToString().ToUpper() : "?";
                var f = GetCachedFont("Segoe UI", size * 0.46f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var b = new SolidBrush(_theme.Tx2);
                var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(init, f, b, new RectangleF(cx, cy, size, size), fmt);
            }
        }

        private void DrawLocations(Graphics g)
        {
            var open = OpenWorldGroup();
            float t = _wdAnim;

            if (open == null && t <= 0.001f) { DrawLocationGrid(g, GetWorldGroups()); return; }

            bool showDetail = t >= 0.5f && open != null;
            if (showDetail) DrawWorldDetail(g, open!);
            else            DrawLocationGrid(g, GetWorldGroups());

            float veil = Math.Clamp(showDetail ? (1f - t) * 2f : t * 2f, 0f, 1f);
            if (veil > 0.01f)
            {
                using var veilBrush = new SolidBrush(Color.FromArgb((int)(235 * veil), _theme.BgCard));
                g.FillRectangle(veilBrush, 0, LocContentY - 6, W, ScrollContentBottom - LocContentY + 12);
            }
        }

        private void DrawLocationGrid(Graphics g, List<WorldGroup> groups)
        {
            var th = _theme;
            if (groups.Count == 0)
            {
                using var emptyFont  = new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point);
                using var emptyBrush = new SolidBrush(th.Tx3);
                var emptyFmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("No friends online in worlds", emptyFont, emptyBrush,
                    new RectangleF(LocPadX, LocContentY, W - 2 * LocPadX, ScrollContentH), emptyFmt);
                return;
            }

            float maxScroll = GetLocationMaxScroll();
            _locationScrollY = Math.Clamp(_locationScrollY, 0f, maxScroll);
            int scrollY = (int)_locationScrollY;
            int colW = LocColW;

            var oldClip = g.Clip;
            g.SetClip(new Rectangle(0, LocContentY, W, ScrollContentH), CombineMode.Intersect);

            for (int i = 0; i < groups.Count; i++)
            {
                int row = i / 2, col = i % 2;
                int cx = LocPadX + col * (colW + LocColGap);
                int cy = LocContentY + row * (LocCardH + LocRowGap) - scrollY;
                if (cy + LocCardH < LocContentY || cy >= ScrollContentBottom) continue;
                DrawWorldCard(g, groups[i], cx, cy, colW, LocCardH);
            }

            g.SetClip(oldClip, CombineMode.Replace);
            oldClip.Dispose();

            if (maxScroll > 0)
            {
                float trackH = ScrollContentH;
                float thumbH = Math.Max(20f, trackH * trackH / (trackH + maxScroll));
                float thumbY = LocContentY + (_locationScrollY / maxScroll) * (trackH - thumbH);
                int sbX = W - LocPadX / 2 - ScrollBarW;
                using var trackBr = new SolidBrush(Color.FromArgb(25, th.Tx3));
                g.FillRectangle(trackBr, sbX, LocContentY, ScrollBarW, (int)trackH);
                using var thumbBr = new SolidBrush(Color.FromArgb(90, th.Tx2));
                g.FillRectangle(thumbBr, sbX, (int)thumbY, ScrollBarW, (int)thumbH);
            }
        }

        private void DrawWorldCard(Graphics g, WorldGroup wg, int x, int y, int w, int h)
        {
            var th = _theme;
            var cardColor = Color.FromArgb(190, th.BgCard);
            using (var cardBg = new SolidBrush(cardColor))
                FillRoundedRect(g, cardBg, x, y, w, h, 8);

            const int imgW = 52;
            Bitmap? worldImg = null;
            if (!string.IsNullOrEmpty(wg.WorldImageUrl))
                lock (_locationImgCache) { _locationImgCache.TryGetValue(wg.WorldImageUrl, out worldImg); }

            var imgRect = new Rectangle(x + 8, y + 8, imgW, h - 16);
            var oldClip = g.Clip;
            using (var imgPath = RoundedRectPath(imgRect.X, imgRect.Y, imgRect.Width, imgRect.Height, 6))
            {
                g.SetClip(imgPath, CombineMode.Intersect);
                if (worldImg != null) DrawImageCover(g, worldImg, imgRect);
                else
                {
                    using var fb = new SolidBrush(Color.FromArgb(80, th.Accent));
                    g.FillPath(fb, imgPath);
                }
                g.SetClip(oldClip, CombineMode.Replace);
            }

            int tx = imgRect.Right + 8;
            int tr = x + w - 8;
            var ellip = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

            string chip = wg.Instances.Count + " Inst.";
            {
                var chipFont = GetCachedFont("Segoe UI", 7.5f, FontStyle.Bold, GraphicsUnit.Point);
                var chipSz = g.MeasureString(chip, chipFont);
                float chipW = chipSz.Width + 10f, chipH = 14f;
                float chipX = tr - chipW, chipY = y + 9f;
                using (var chipBg = new SolidBrush(Color.FromArgb(40, th.Accent)))
                    FillRoundedRect(g, chipBg, (int)chipX, (int)chipY, (int)chipW, (int)chipH, 4);
                using var chipBr = new SolidBrush(th.Accent);
                var chipFmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(chip, chipFont, chipBr, new RectangleF(chipX, chipY, chipW, chipH), chipFmt);
                tr = (int)chipX - 6;
            }

            using (var nameBr = new SolidBrush(th.Tx1))
                g.DrawString(wg.WorldName, GetCachedFont("Segoe UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point), nameBr,
                    new RectangleF(tx, y + 8f, Math.Max(tr - tx, 10f), 16f), ellip);

            var names = string.Join(", ", wg.All.Take(3).Select(e => e.FriendName));
            using (var nmBr = new SolidBrush(th.Tx3))
                g.DrawString(names, GetCachedFont("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point), nmBr,
                    new RectangleF(tx, y + 26f, Math.Max(x + w - 8 - tx, 10f), 14f), ellip);

            const int avSz = 20;
            int avY = y + h - avSz - 8;
            int avX = tx;
            int shown = Math.Min(3, wg.All.Count);
            for (int i = 0; i < shown; i++)
            {
                DrawCircleAvatar(g, wg.All[i].FriendImageUrl, wg.All[i].FriendName, avX, avY, avSz, cardColor);
                avX += avSz - 6;
            }
            int more = wg.All.Count - shown;
            if (more > 0)
            {
                using var mb = new SolidBrush(th.Accent);
                g.DrawString("+" + more, GetCachedFont("Segoe UI", 8f, FontStyle.Bold, GraphicsUnit.Point), mb,
                    new RectangleF(avX + 11f, avY + 3f, 40f, 14f));
            }
        }

        private void DrawWorldDetail(Graphics g, WorldGroup wg)
        {
            var th = _theme;

            const int thumb = 42;
            int hx = LocPadX, hy = LocContentY;
            Bitmap? worldImg = null;
            if (!string.IsNullOrEmpty(wg.WorldImageUrl))
                lock (_locationImgCache) { _locationImgCache.TryGetValue(wg.WorldImageUrl, out worldImg); }

            var imgRect = new Rectangle(hx, hy, thumb, thumb);
            var oldClip0 = g.Clip;
            using (var imgPath = RoundedRectPath(hx, hy, thumb, thumb, 6))
            {
                g.SetClip(imgPath, CombineMode.Intersect);
                if (worldImg != null) DrawImageCover(g, worldImg, imgRect);
                else
                {
                    using var fb = new SolidBrush(Color.FromArgb(80, th.Accent));
                    g.FillPath(fb, imgPath);
                }
                g.SetClip(oldClip0, CombineMode.Replace);
            }

            var ellip = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            int mx = hx + thumb + 10;
            using (var nameFont = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Point))
            using (var nameBr = new SolidBrush(th.Tx1))
                g.DrawString(wg.WorldName, nameFont, nameBr,
                    new RectangleF(mx, hy + 1f, W - mx - LocPadX, 18f), ellip);

            float chipX = mx;
            void Chip(string text, Color fg, Color bg)
            {
                using var f = new Font("Segoe UI", 7.5f, FontStyle.Bold, GraphicsUnit.Point);
                var sz = g.MeasureString(text, f);
                float cw = sz.Width + 12f, ch = 15f, cy = hy + 22f;
                using (var b = new SolidBrush(bg)) FillRoundedRect(g, b, (int)chipX, (int)cy, (int)cw, (int)ch, 4);
                using var br = new SolidBrush(fg);
                var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(text, f, br, new RectangleF(chipX, cy, cw, ch), fmt);
                chipX += cw + 6f;
            }
            Chip(wg.Instances.Count + (wg.Instances.Count == 1 ? " instance" : " instances"), th.Tx2, Color.FromArgb(200, th.BgHover));
            Chip(wg.All.Count + (wg.All.Count == 1 ? " friend" : " friends"), th.Accent, Color.FromArgb(40, th.Accent));

            int listTop = LocContentY + WdHeadH;
            int listH   = ScrollContentBottom - listTop;
            float maxScroll = GetLocationMaxScroll();
            _locationScrollY = Math.Clamp(_locationScrollY, 0f, maxScroll);
            int scrollY = (int)_locationScrollY;

            var oldClip = g.Clip;
            g.SetClip(new Rectangle(0, listTop, W, listH), CombineMode.Intersect);

            int cy2 = listTop - scrollY;
            foreach (var inst in wg.Instances)
            {
                if (cy2 + WdInstH >= listTop && cy2 < ScrollContentBottom)
                {
                    using var idFont = new Font("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Point);
                    using var idBr   = new SolidBrush(th.Tx1);
                    string idTxt = InstanceShortId(inst[0].InstanceId);
                    g.DrawString(idTxt, idFont, idBr, new RectangleF(LocPadX + 2, cy2 + 2, 160f, 14f));
                    var idSz = g.MeasureString(idTxt, idFont);

                    using var cntFont = new Font("Segoe UI", 7.5f, FontStyle.Regular, GraphicsUnit.Point);
                    using var cntBr   = new SolidBrush(th.Tx3);
                    string cntTxt = inst.Count + (inst.Count == 1 ? " friend" : " friends");
                    g.DrawString(cntTxt, cntFont, cntBr, new RectangleF(LocPadX + 6 + idSz.Width, cy2 + 4, 120f, 13f));

                    using var linePen = new Pen(Color.FromArgb(90, th.Brd), 1f);
                    float lineX = LocPadX + 12 + idSz.Width + g.MeasureString(cntTxt, cntFont).Width;
                    g.DrawLine(linePen, lineX, cy2 + 10, W - LocPadX, cy2 + 10);
                }
                cy2 += WdInstH;

                foreach (var e in inst)
                {
                    if (cy2 + FrdCardH >= listTop && cy2 < ScrollContentBottom)
                        DrawInstanceFriendRow(g, e, wg.WorldName, LocPadX, cy2, W - 2 * LocPadX, FrdCardH);
                    cy2 += FrdCardH + WdRowGap;
                }
            }

            g.SetClip(oldClip, CombineMode.Replace);
            oldClip.Dispose();

            if (maxScroll > 0)
            {
                float trackH = listH;
                float thumbH = Math.Max(20f, trackH * trackH / (trackH + maxScroll));
                float thumbY = listTop + (_locationScrollY / maxScroll) * (trackH - thumbH);
                int sbX = W - LocPadX / 2 - ScrollBarW;
                using var trackBr = new SolidBrush(Color.FromArgb(25, th.Tx3));
                g.FillRectangle(trackBr, sbX, listTop, ScrollBarW, (int)trackH);
                using var thumbBr = new SolidBrush(Color.FromArgb(90, th.Tx2));
                g.FillRectangle(thumbBr, sbX, (int)thumbY, ScrollBarW, (int)thumbH);
            }
        }

        private void DrawInstanceFriendRow(Graphics g, LocationEntry e, string worldName, int x, int y, int w, int h)
        {
            var th = _theme;
            var cardColor = Color.FromArgb(190, th.BgCard);
            using (var bg = new SolidBrush(cardColor))
                FillRoundedRect(g, bg, x, y, w, h, 8);

            const int avSize = 34, avR = 7;
            int avX = x + 8, avY = y + (h - avSize) / 2;
            DrawLocPortrait(g, e.FriendImageUrl, e.FriendName, e.FriendId, avX, avY, avSize, avR, cardColor);

            int invX  = x + w - ActBtnW - 6;
            int joinX = invX - ActBtnW - 6;
            int btnY  = y + (h - ActBtnH) / 2;

            bool joinCd = _joinCooldowns.TryGetValue(e.FriendId, out var jc) && (DateTime.UtcNow - jc).TotalSeconds < 5;
            bool invCd  = _joinCooldowns.TryGetValue(e.FriendId + "#inv", out var ic) && (DateTime.UtcNow - ic).TotalSeconds < 5;
            DrawActionButton(g, joinX, btnY, "Join",   joinCd);
            DrawActionButton(g, invX,  btnY, "Invite", invCd);

            int tx = avX + avSize + 10;
            int tr = joinX - 8;
            var ellip = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

            using (var nameFont = new Font("Segoe UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point))
            using (var nameBr = new SolidBrush(th.Tx1))
                g.DrawString(e.FriendName, nameFont, nameBr,
                    new RectangleF(tx, y + 8f, Math.Max(tr - tx, 10f), 16f), ellip);

            using (var subFont = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point))
            using (var subBr = new SolidBrush(th.Tx3))
                g.DrawString(worldName + " " + InstanceShortId(e.InstanceId), subFont, subBr,
                    new RectangleF(tx, y + 25f, Math.Max(tr - tx, 10f), 14f), ellip);
        }

        private void DrawLocPortrait(Graphics g, string imageUrl, string name, string friendId,
                                     int avX, int avY, int avSize, int avR, Color ringColor)
        {
            Bitmap? img = null;
            if (!string.IsNullOrEmpty(imageUrl))
                lock (_locationImgCache) { _locationImgCache.TryGetValue(imageUrl, out img); }

            var oldClip = g.Clip;
            using (var path = RoundedRectPath(avX, avY, avSize, avSize, avR))
            {
                g.SetClip(path, CombineMode.Intersect);
                if (img != null)
                {
                    DrawImageCover(g, img, new Rectangle(avX, avY, avSize, avSize));
                    g.SetClip(oldClip, CombineMode.Replace);
                }
                else
                {
                    using var bg = new SolidBrush(_theme.BgHover);
                    g.FillPath(bg, path);
                    g.SetClip(oldClip, CombineMode.Replace);
                    string init = name.Length > 0 ? name[0].ToString().ToUpper() : "?";
                    using var f = new Font("Segoe UI", avSize * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel);
                    using var b = new SolidBrush(_theme.Tx2);
                    var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(init, f, b, new RectangleF(avX, avY, avSize, avSize), fmt);
                }
            }

            var st = FriendStatus(friendId);
            if (!string.IsNullOrEmpty(st))
                DrawStatusDot(g, st, avX + avSize - 3f, avY + avSize - 3f, 5f, ringColor);
        }

        private void DrawActionButton(Graphics g, int x, int y, string label, bool inCooldown)
        {
            var th = _theme;
            using var bg = new SolidBrush(inCooldown ? Color.FromArgb(170, th.Ok) : Color.FromArgb(210, th.Accent));
            FillRoundedRect(g, bg, x, y, ActBtnW, ActBtnH, 6);
            using var br = new SolidBrush(Color.White);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            if (inCooldown)
            {
                using var iconFont = _matSymFamily != null
                    ? new Font(_matSymFamily, 16f, FontStyle.Regular, GraphicsUnit.Point)
                    : new Font("Segoe MDL2 Assets", 14f, FontStyle.Regular, GraphicsUnit.Point);
                g.DrawString("\uE876", iconFont, br, new RectangleF(x, y, ActBtnW, ActBtnH), fmt);
            }
            else
            {
                using var f = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point);
                g.DrawString(label, f, br, new RectangleF(x, y, ActBtnW, ActBtnH), fmt);
            }
        }

        private static bool CanJoinLocation(string location)
        {
            if (string.IsNullOrEmpty(location) || location == "private"
                || location == "offline" || location == "traveling") return false;
            if (!location.Contains(':')) return false;
            return !location.Contains("~private(");
        }

        private void DrawFriends(Graphics g)
        {
            var th    = _theme;
            int cardW = W - 2 * FrdPadX;

            List<FriendTabEntry> snap;
            lock (_onlineFriends) snap = new List<FriendTabEntry>(_onlineFriends);

            if (snap.Count == 0)
            {
                using var emptyFont  = new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point);
                using var emptyBrush = new SolidBrush(th.Tx3);
                var emptyFmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("No friends online in-game", emptyFont, emptyBrush,
                    new RectangleF(FrdPadX, FrdContentY, cardW, ScrollContentH), emptyFmt);
                return;
            }

            float maxScroll = GetFriendsMaxScroll();
            _friendsScrollY = Math.Clamp(_friendsScrollY, 0f, maxScroll);
            int scrollY = (int)_friendsScrollY;

            var oldClip = g.Clip;
            g.SetClip(new Rectangle(0, FrdContentY, W, ScrollContentH), CombineMode.Intersect);

            for (int i = 0; i < snap.Count; i++)
            {
                int cy = FrdContentY + i * (FrdCardH + FrdGap) - scrollY;
                if (cy + FrdCardH < FrdContentY || cy >= ScrollContentBottom) continue;
                DrawFriendCard(g, snap[i], FrdPadX, cy, cardW, FrdCardH);
            }

            g.SetClip(oldClip, CombineMode.Replace);
            oldClip.Dispose();

            // Thin scrollbar strip on right edge
            if (maxScroll > 0)
            {
                float trackH  = ScrollContentH;
                float thumbH  = Math.Max(20f, trackH * trackH / (trackH + maxScroll));
                float thumbY  = FrdContentY + (_friendsScrollY / maxScroll) * (trackH - thumbH);
                int   sbX     = W - FrdPadX / 2 - ScrollBarW;
                using var trackBr = new SolidBrush(Color.FromArgb(25, th.Tx3));
                g.FillRectangle(trackBr, sbX, FrdContentY, ScrollBarW, (int)trackH);
                using var thumbBr = new SolidBrush(Color.FromArgb(90, th.Tx2));
                g.FillRectangle(thumbBr, sbX, (int)thumbY, ScrollBarW, (int)thumbH);
            }
        }

        private void DrawFriendCard(Graphics g, FriendTabEntry friend, int x, int y, int w, int h)
        {
            var th = _theme;
            string locKey = friend.FriendId;
            bool inCooldown = _joinCooldowns.TryGetValue(locKey, out var cdT)
                && (DateTime.UtcNow - cdT).TotalSeconds < 5;
            bool hasLocation = !string.IsNullOrEmpty(friend.Location) && friend.Location != "offline";

            // Card background
            using var cardBg = new SolidBrush(Color.FromArgb(190, th.BgCard));
            FillRoundedRect(g, cardBg, x, y, w, h, 8);

            // Invite button (right side)
            const int btnW = 58;
            int btnX = x + w - btnW - 4;
            int btnY = y + 4;
            int btnH = h - 8;

            const int jrW = 52, jrGap = 6;
            int jrX = (hasLocation ? btnX - jrGap : x + w - 4) - jrW;
            {
                bool canJoin = CanJoinLocation(friend.Location);
                bool jrCd = _joinCooldowns.TryGetValue(locKey + "#jr", out var jcd)
                    && (DateTime.UtcNow - jcd).TotalSeconds < 5;
                var jrColor = jrCd ? Color.FromArgb(170, th.Ok) : Color.FromArgb(210, th.Accent);
                using var jrBg = new SolidBrush(jrColor);
                FillRoundedRect(g, jrBg, jrX, btnY, jrW, btnH, 6);
                using var jrBrush = new SolidBrush(Color.White);
                var jrFmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                using var jrFont = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point);
                g.DrawString(jrCd ? "✓" : (canJoin ? "Join" : "Req."), jrFont, jrBrush,
                    new RectangleF(jrX, btnY, jrW, btnH), jrFmt);
            }

            if (hasLocation)
            {
                var btnColor = inCooldown ? Color.FromArgb(170, th.Ok) : Color.FromArgb(210, th.Accent);
                using var btnBg = new SolidBrush(btnColor);
                FillRoundedRect(g, btnBg, btnX, btnY, btnW, btnH, 6);
                using var btnBrush = new SolidBrush(Color.White);
                var btnFmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                if (inCooldown)
                {
                    using var iconFont = _matSymFamily != null
                        ? new Font(_matSymFamily, 16f, FontStyle.Regular, GraphicsUnit.Point)
                        : new Font("Segoe MDL2 Assets", 14f, FontStyle.Regular, GraphicsUnit.Point);
                    g.DrawString("", iconFont, btnBrush, new RectangleF(btnX, btnY, btnW, btnH), btnFmt);
                }
                else
                {
                    using var lblFont = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point);
                    g.DrawString("Invite", lblFont, btnBrush, new RectangleF(btnX, btnY, btnW, btnH), btnFmt);
                }
            }

            // Avatar (36×36, rounded 8px)
            const int avSz = 36, avR = 8;
            int avX = x + 8;
            int avY = y + (h - avSz) / 2;

            Bitmap? avImg = null;
            if (!string.IsNullOrEmpty(friend.FriendImageUrl))
                lock (_locationImgCache) { _locationImgCache.TryGetValue(friend.FriendImageUrl, out avImg); }

            var avRect = new Rectangle(avX, avY, avSz, avSz);
            var oldClip = g.Clip;
            using var avPath = RoundedRectPath(avX, avY, avSz, avSz, avR);
            g.SetClip(avPath, CombineMode.Intersect);
            if (avImg != null)
                DrawImageCover(g, avImg, avRect);
            else
            {
                using var avBg = new SolidBrush(th.BgHover);
                g.FillPath(avBg, avPath);
                g.SetClip(oldClip, CombineMode.Replace);
                string init = friend.FriendName.Length > 0 ? friend.FriendName[0].ToString().ToUpper() : "?";
                using var initFont  = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Point);
                using var initBrush = new SolidBrush(th.Tx2);
                var initFmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(init, initFont, initBrush, new RectangleF(avX, avY, avSz, avSz), initFmt);
            }
            g.SetClip(oldClip, CombineMode.Replace);
            using var avBorder = new Pen(Color.FromArgb(50, th.Brd), 1f);
            DrawRoundedRect(g, avBorder, avX, avY, avSz, avSz, avR);

            // Status dot (10px, overlaid on bottom-right of avatar)
            int dotSz = 10;
            int dotX = avX + avSz - dotSz + 1;
            int dotY = avY + avSz - dotSz + 1;
            var statusColor = StatusColor(friend.Status);
            using var dotBg = new SolidBrush(th.BgCard); // outline ring
            g.FillEllipse(dotBg, dotX - 2, dotY - 2, dotSz + 4, dotSz + 4);
            using var dotBrush = new SolidBrush(statusColor);
            g.FillEllipse(dotBrush, dotX, dotY, dotSz, dotSz);

            // Text area
            int textX = avX + avSz + 10;
            int textW = jrX - 6 - textX;
            var ellipsisFmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

            using var nameFont  = new Font("Segoe UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point);
            using var nameBrush = new SolidBrush(th.Tx1);
            g.DrawString(friend.FriendName, nameFont, nameBrush,
                new RectangleF(textX, y + 4, Math.Max(textW, 20f), 16), ellipsisFmt);

            // Row 2: Status description (if any)
            string row2 = !string.IsNullOrEmpty(friend.StatusDescription) ? friend.StatusDescription : "";
            if (!string.IsNullOrEmpty(row2))
            {
                using var descFont  = new Font("Segoe UI", 7.5f, FontStyle.Regular, GraphicsUnit.Point);
                using var descBrush = new SolidBrush(th.Tx3);
                g.DrawString(row2, descFont, descBrush, new RectangleF(textX, y + 21, textW, 13), ellipsisFmt);
            }

            // Row 3: World location
            string worldDisplay = !string.IsNullOrEmpty(friend.WorldName) ? friend.WorldName : (hasLocation ? "In a world" : "Online");
            using var locFont  = new Font("Segoe UI", 7f, FontStyle.Regular, GraphicsUnit.Point);
            using var locBrush = new SolidBrush(th.Tx3);
            g.DrawString(worldDisplay, locFont, locBrush, new RectangleF(textX, y + 35, textW, 13), ellipsisFmt);
        }

        private static readonly Color StatusColorJoin    = Color.FromArgb(0x42, 0xA5, 0xF5); // --status-join   #42A5F5
        private static readonly Color StatusColorOnline  = Color.FromArgb(0x2D, 0xD4, 0x8C); // --status-online #2DD48C
        private static readonly Color StatusColorAsk     = Color.FromArgb(0xFF, 0xA7, 0x26); // --status-ask    #FFA726
        private static readonly Color StatusColorBusy    = Color.FromArgb(0xEF, 0x53, 0x50); // --status-busy   #EF5350
        private static readonly Color StatusColorOffline = Color.FromArgb(0x74, 0x7F, 0x8D); // --status-offline#747F8D

        private static Color StatusColor(string status) => status switch
        {
            "join me" => StatusColorJoin,
            "active"  => StatusColorOnline,
            "online"  => StatusColorOnline,
            "ask me"  => StatusColorAsk,
            "busy"    => StatusColorBusy,
            _         => StatusColorOffline,
        };

        // Tools tab layout (shared between Draw + Click)
        private const int ToolsStartY = 76;
        private const int ToolsGap    = 8;
        private const int ToolsPadX   = 12;
        private const int ToolsBottom = H - 12;
        private static int ToolsCardW => (W - ToolsPadX * 2 - ToolsGap) / 2;
        private static int ToolsCardH => (H - ToolsStartY - ToolsPadX - ToolsGap * 2) / 3;
        private static int ToolsViewportH => ToolsBottom - ToolsStartY;

        private int GetToolsCount() =>
            // discord, voice, kikitan, space, relay, chatbox, frameshot, spaceturn
            8;

        private float GetToolsMaxScroll()
        {
            int rows = (GetToolsCount() + 1) / 2;
            int contentH = rows * ToolsCardH + (rows - 1) * ToolsGap;
            return MathF.Max(0f, contentH - ToolsViewportH);
        }

        private void DrawTools(Graphics g)
        {
            var th = _theme;
            const int startY = 76;
            const int gap    = 8;
            const int padX   = 12;
            int cardW = (W - padX * 2 - gap) / 2;
            int cardH = (H - startY - padX - gap * 2) / 3;

            // Scrollable: clip card region, offset y by scroll, draw scrollbar.
            float maxScroll = GetToolsMaxScroll();
            _toolsScrollY = Math.Clamp(_toolsScrollY, 0f, maxScroll);
            int scrollY = (int)_toolsScrollY;
            var oldClip = g.Clip;
            g.SetClip(new Rectangle(0, ToolsStartY, W, ToolsViewportH), CombineMode.Intersect);

            // Layout: 2 cols × 3 rows
            // Icons: Material Symbols Rounded codepoints — 1:1 same as sidebar
            // sensors=\uE51E  mic=\uE31D  translate=  rocket_launch=\uEB9B  cell_tower=\uEBBA  chat=\uE0C9
            var tools = new (string Icon, string Label, bool Active)[]
            {
                ("\uE51E", "Discord Presence", _toolDiscord),
                ("\uE31D", "Voice Fight",      _toolVoice),
                ("\uE927", "Kikitan XD",      _toolKikitan),
                ("\uEB9B", "Space Flight",     _toolSpaceFlt),
                ("\uEBBA", "Media Relay",      _toolRelay),
                ("\uE0C9", "Custom Chatbox",   _toolChatbox),
                ("\uE412", "FrameShot",       _toolFrameShot),
                ("\uE41A", "Space Turn",      _toolSpaceTurn),
            };

            for (int i = 0; i < tools.Length; i++)
            {
                int col = i % 2;
                int row = i / 2;
                int x   = padX + col * (cardW + gap);
                int y   = startY + row * (cardH + gap) - scrollY;
                if (y + cardH < ToolsStartY || y >= ToolsBottom) continue;
                DrawToolCard(g, tools[i].Icon, tools[i].Label, tools[i].Active, x, y, cardW, cardH);
            }

            g.SetClip(oldClip, CombineMode.Replace);
            oldClip.Dispose();

            // Scrollbar strip (only on overflow)
            if (maxScroll > 0)
            {
                float trackH = ToolsViewportH;
                float thumbH = Math.Max(20f, trackH * trackH / (trackH + maxScroll));
                float thumbY = ToolsStartY + (_toolsScrollY / maxScroll) * (trackH - thumbH);
                int sbX = W - ToolsPadX / 2 - ScrollBarW;
                using var trackBr = new SolidBrush(Color.FromArgb(25, th.Tx3));
                g.FillRectangle(trackBr, sbX, ToolsStartY, ScrollBarW, (int)trackH);
                using var thumbBr = new SolidBrush(Color.FromArgb(90, th.Tx2));
                g.FillRectangle(thumbBr, sbX, (int)thumbY, ScrollBarW, (int)thumbH);
            }
        }

        private void DrawToolCard(Graphics g, string icon, string label, bool active, int x, int y, int w, int h)
        {
            var th = _theme;

            // Card background
            if (active)
            {
                using var bg = new SolidBrush(Color.FromArgb(55, th.Accent));
                FillRoundedRect(g, bg, x, y, w, h, 10);
                using var border = new Pen(Color.FromArgb(130, th.Accent), 1.5f);
                DrawRoundedRect(g, border, x, y, w, h, 10);
            }
            else
            {
                using var bg = new SolidBrush(Color.FromArgb(35, th.BgHover));
                FillRoundedRect(g, bg, x, y, w, h, 10);
                using var border = new Pen(Color.FromArgb(45, th.Brd), 1f);
                DrawRoundedRect(g, border, x, y, w, h, 10);
            }

            // Icon — Material Symbols Rounded (same font as sidebar), fallback to Segoe MDL2 Assets
            int iconH = (int)(h * 0.58f);
            using var iconFont  = _matSymFamily != null
                ? new Font(_matSymFamily, 20f, FontStyle.Regular, GraphicsUnit.Point)
                : new Font("Segoe MDL2 Assets", 20f, FontStyle.Regular, GraphicsUnit.Point);
            using var iconBrush = new SolidBrush(active ? th.Accent : Color.FromArgb(110, th.Tx2));
            var iconFmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(icon, iconFont, iconBrush, new RectangleF(x, y, w, iconH), iconFmt);

            // Label (bottom ~45%)
            int labelY = y + iconH;
            int labelH = h - iconH;
            using var nameFont  = new Font("Segoe UI", 8.5f, active ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
            using var nameBrush = new SolidBrush(active ? Color.White : Color.FromArgb(130, th.Tx2));
            var nameFmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
                                             Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(label, nameFont, nameBrush, new RectangleF(x + 4, labelY, w - 8, labelH), nameFmt);

            // Status dot (top-right corner)
            int dotR = 5;
            int dotX = x + w - dotR * 2 - 5;
            int dotY = y + 5;
            using var dotBr = new SolidBrush(active ? th.Ok : Color.FromArgb(70, th.Tx3));
            g.FillEllipse(dotBr, dotX, dotY, dotR * 2, dotR * 2);
        }

        private const int KxPadX     = 12;
        private const int KxTopY     = 72;
        private const int KxTopH     = 26;
        private const int KxCardGap  = 8;
        private const int KxLabelH   = 22;
        private const int KxCardPad  = 8;

        private void DrawKikitanChip(Graphics g, string text, Color fg, Color bg, ref float x, float y)
        {
            using var f = new Font("Segoe UI", 8f, FontStyle.Bold, GraphicsUnit.Point);
            var sz = g.MeasureString(text, f);
            float cw = sz.Width + 14f, ch = 18f;
            using (var b = new SolidBrush(bg)) FillRoundedRect(g, b, (int)x, (int)y, (int)cw, (int)ch, 5);
            using var br = new SolidBrush(fg);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, f, br, new RectangleF(x, y, cw, ch), fmt);
            x += cw + 6f;
        }

        private void DrawKikitanCard(Graphics g, string label, string icon, string text, bool isFinal,
                                     bool translated, int x, int y, int w, int h, float fontSize)
        {
            var th = _theme;
            using (var bg = new SolidBrush(Color.FromArgb(190, th.BgCard)))
                FillRoundedRect(g, bg, x, y, w, h, 8);

            int lx = x + KxCardPad;
            if (_matSymFamily != null)
            {
                using var iconFont = new Font(_matSymFamily, 11f, FontStyle.Regular, GraphicsUnit.Point);
                using var iconBr   = new SolidBrush(th.Tx2);
                g.DrawString(icon, iconFont, iconBr, new RectangleF(lx, y + KxCardPad, 18f, 16f));
                lx += 20;
            }

            using (var lf = new Font("Segoe UI", 8f, FontStyle.Bold, GraphicsUnit.Point))
            using (var lb = new SolidBrush(th.Tx2))
                g.DrawString(label, lf, lb, new RectangleF(lx, y + KxCardPad + 1f, w - 110f, 16f));

            string state = isFinal ? "final" : "partial";
            var stateFg  = isFinal ? th.Ok : th.Warn;
            using (var sf = new Font("Segoe UI", 8f, FontStyle.Bold, GraphicsUnit.Point))
            {
                var sz = g.MeasureString(state, sf);
                float sw = sz.Width + 12f, sh = 16f;
                float sx = x + w - KxCardPad - sw, sy = y + KxCardPad;
                using (var sb = new SolidBrush(Color.FromArgb(45, stateFg)))
                    FillRoundedRect(g, sb, (int)sx, (int)sy, (int)sw, (int)sh, 4);
                using var sbr = new SolidBrush(stateFg);
                var sfmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(state, sf, sbr, new RectangleF(sx, sy, sw, sh), sfmt);
            }

            var body = new RectangleF(x + KxCardPad, y + KxLabelH + KxCardPad,
                                      w - KxCardPad * 2, h - KxLabelH - KxCardPad * 2);
            var textColor = translated ? th.Cyan : (isFinal ? th.Tx1 : th.Tx2);
            using var bodyFont  = new Font("Segoe UI", fontSize, FontStyle.Regular, GraphicsUnit.Point);
            using var bodyBrush = new SolidBrush(textColor);
            var bodyFmt = new StringFormat { Trimming = StringTrimming.EllipsisWord };

            if (string.IsNullOrWhiteSpace(text))
            {
                using var phBrush = new SolidBrush(Color.FromArgb(110, th.Tx3));
                g.DrawString("Listening...", bodyFont, phBrush, body, bodyFmt);
                return;
            }
            g.DrawString(text, bodyFont, bodyBrush, body, bodyFmt);
        }

        private void DrawKikitan(Graphics g)
        {
            var th = _theme;
            bool withTr = _kxTranslate && !string.IsNullOrEmpty(_kxTargetLang);

            float chipX = KxPadX, chipY = KxTopY + 2f;
            if (!string.IsNullOrEmpty(_kxEngine))
                DrawKikitanChip(g, _kxEngine, th.Accent, Color.FromArgb(40, th.Accent), ref chipX, chipY);
            DrawKikitanChip(g, _kxSourceLang, th.Tx2, Color.FromArgb(200, th.BgHover), ref chipX, chipY);
            if (withTr)
            {
                using (var af = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point))
                using (var ab = new SolidBrush(th.Tx3))
                    g.DrawString("→", af, ab, new RectangleF(chipX, chipY + 1f, 16f, 16f));
                chipX += 16f;
                DrawKikitanChip(g, _kxTargetLang, th.Tx2, Color.FromArgb(200, th.BgHover), ref chipX, chipY);
            }

            int top    = KxTopY + KxTopH;
            int availH = ScrollContentBottom - top;
            int cardW  = W - KxPadX * 2;

            if (withTr)
            {
                int cardH = (availH - KxCardGap) / 2;
                DrawKikitanCard(g, "Source · " + _kxSourceLang, "", _kxSource, _kxFinal, false,
                                KxPadX, top, cardW, cardH, 14f);
                DrawKikitanCard(g, "Translation · " + _kxTargetLang, "", _kxTranslation, _kxFinal, true,
                                KxPadX, top + cardH + KxCardGap, cardW, cardH, 14f);
            }
            else
            {
                DrawKikitanCard(g, "Source · " + _kxSourceLang, "", _kxSource, _kxFinal, false,
                                KxPadX, top, cardW, availH, 21f);
            }
        }

        private void DrawScaleTab(Graphics g)
        {
            var th   = _theme;
            var fmtC = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            // Header label
            using (var hf = new Font("Segoe UI", 7.5f, FontStyle.Bold, GraphicsUnit.Point))
            using (var hb = new SolidBrush(th.Tx3))
                g.DrawString("AVATAR SIZE", hf, hb, new RectangleF(0, 62 + ContentVShift, W, 16), fmtC);

            // Ring + thumbstick circle
            int cx = W / 2;
            int cy = 178 + ContentVShift;
            int outerR = 72;
            int innerR = 56;
            int dotR   = 13;
            int maxOff = innerR - dotR; // max offset from center for dot

            using (var ringPen = new Pen(Color.FromArgb(60, th.Brd), innerR - outerR < 0 ? outerR - innerR : 16f))
            {
                // draw ring as thick circle stroke
                using var ringBrushOuter = new SolidBrush(Color.FromArgb(40, th.Accent));
                g.FillEllipse(ringBrushOuter, cx - outerR, cy - outerR, outerR * 2, outerR * 2);
                using var ringBrushInner = new SolidBrush(Color.FromArgb(200, th.BgCard));
                g.FillEllipse(ringBrushInner, cx - innerR, cy - innerR, innerR * 2, innerR * 2);
            }

            // Thumbstick dot
            float tx = Math.Clamp(_thumbDisplayX, -1f, 1f);
            float ty = Math.Clamp(_thumbDisplayY, -1f, 1f);
            float dotOffX = tx * maxOff;
            float dotOffY = -ty * maxOff; // GDI Y is flipped
            int dotX = cx + (int)dotOffX - dotR;
            int dotY = cy + (int)dotOffY - dotR;
            using (var dotBg = new SolidBrush(Color.FromArgb(180, th.Accent)))
                g.FillEllipse(dotBg, dotX, dotY, dotR * 2, dotR * 2);

            // Scale value label
            string scaleText = $"{_scaleValue:F2} m";
            using (var sf = new Font("Segoe UI", 18f, FontStyle.Bold, GraphicsUnit.Point))
            using (var sb = new SolidBrush(th.Tx1))
                g.DrawString(scaleText, sf, sb, new RectangleF(0, 260 + ContentVShift, W, 32), fmtC);

            // Recording hint (overlays button row when recording)
            if (_isScaleRecording)
            {
                using var hf = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
                using var hb = new SolidBrush(th.Warn);
                g.DrawString("Hold 1-4 buttons to record hold keybind...", hf, hb,
                    new RectangleF(12, 303 + ContentVShift, W - 24, 34), fmtC);
                return;
            }

            // [-] and [+] buttons (pill style, same as notification invite buttons)
            // [-]: x=68 y=303 w=88 h=34  [+]: x=356 y=303 w=88 h=34
            const int szBtnW = 88, szBtnGap = 200;
            int szBtnY = 303 + ContentVShift, szLeftX = (W - szBtnW * 2 - szBtnGap) / 2;
            DrawScaleButton(g, "", szLeftX,                     szBtnY, szBtnW, 34);
            DrawScaleButton(g, "", szLeftX + szBtnW + szBtnGap, szBtnY, szBtnW, 34);

            // Hint: grip keybind
            string holdHint = _scaleKeybind.Count > 0
                ? $"Hold {string.Join("+", _scaleKeybind.Select(ButtonLabel))} + Stick to scale"
                : "Set hold keybind in settings to scale with Stick";
            using (var hf2 = new Font("Segoe UI", 7f, FontStyle.Regular, GraphicsUnit.Point))
            using (var hb2 = new SolidBrush(th.Tx3))
                g.DrawString(holdHint, hf2, hb2, new RectangleF(12, 346 + ContentVShift, W - 24, 30), fmtC);
        }

        private void DrawScaleButton(Graphics g, string icon, int x, int y, int w, int h)
        {
            var th = _theme;
            using var bg = new SolidBrush(Color.FromArgb(55, th.Accent));
            FillRoundedRect(g, bg, x, y, w, h, h / 2);
            using var border = new Pen(Color.FromArgb(100, th.Accent), 1f);
            DrawRoundedRect(g, border, x, y, w, h, h / 2);
            using var iconFont = _matSymFamily != null
                ? new Font(_matSymFamily, 18f, FontStyle.Regular, GraphicsUnit.Point)
                : new Font("Segoe MDL2 Assets", 16f, FontStyle.Regular, GraphicsUnit.Point);
            using var iconBrush = new SolidBrush(th.Tx1);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(icon, iconFont, iconBrush, new RectangleF(x, y, w, h), fmt);
        }

        private const int MaxNotifications = 32;
        private const int NotifContentY    = 72;
        private const int NotifItemH       = 75;
        private const int NotifViewportH   = ScrollContentBottom - NotifContentY;

        private float GetNotifMaxScroll()
        {
            int count;
            lock (_notifications) count = _notifications.Count;
            return Math.Max(0f, count * NotifItemH - NotifViewportH);
        }

        private void DrawNotifications(Graphics g)
        {
            var th = _theme;

            List<NotifEntry> snap;
            lock (_notifications) snap = new List<NotifEntry>(_notifications);

            if (snap.Count == 0)
            {
                using var font = new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point);
                using var brush = new SolidBrush(th.Tx3);
                var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("No recent notifications", font, brush,
                    new RectangleF(12, NotifContentY, W - 24, NotifViewportH), fmt);
                return;
            }

            float maxScroll = GetNotifMaxScroll();
            _notifScrollY = Math.Clamp(_notifScrollY, 0f, maxScroll);
            int scrollY = (int)_notifScrollY;

            var oldClip = g.Clip;
            g.SetClip(new Rectangle(0, NotifContentY, W, NotifViewportH), CombineMode.Intersect);

            for (int i = 0; i < snap.Count; i++)
            {
                int iy = NotifContentY + i * NotifItemH - scrollY;
                if (iy + NotifItemH < NotifContentY || iy >= ScrollContentBottom) continue;
                DrawNotificationItem(g, snap[i], 12, iy, W - 24, NotifItemH - 4);
            }

            g.SetClip(oldClip, CombineMode.Replace);
            oldClip.Dispose();

            if (maxScroll > 0)
            {
                float trackH = NotifViewportH;
                float thumbH = Math.Max(20f, trackH * trackH / (trackH + maxScroll));
                float thumbY = NotifContentY + (_notifScrollY / maxScroll) * (trackH - thumbH);
                int sbX = W - LocPadX / 2 - ScrollBarW;
                using var trackBr = new SolidBrush(Color.FromArgb(25, th.Tx3));
                g.FillRectangle(trackBr, sbX, NotifContentY, ScrollBarW, (int)trackH);
                using var thumbBr = new SolidBrush(Color.FromArgb(90, th.Tx2));
                g.FillRectangle(thumbBr, sbX, (int)thumbY, ScrollBarW, (int)thumbH);
            }
        }

        private const int NotifBtnW = 58, NotifBtnH = 30;

        private string FriendStatus(string friendId)
        {
            if (string.IsNullOrEmpty(friendId)) return "";
            lock (_onlineFriends)
                return _onlineFriends.FirstOrDefault(e => e.FriendId == friendId)?.Status ?? "";
        }

        private void DrawStatusDot(Graphics g, string status, float cx, float cy, float r, Color ring)
        {
            using var ringBr = new SolidBrush(ring);
            g.FillEllipse(ringBr, cx - r - 2f, cy - r - 2f, (r + 2f) * 2f, (r + 2f) * 2f);
            using var dotBr = new SolidBrush(StatusColor(status));
            g.FillEllipse(dotBr, cx - r, cy - r, r * 2f, r * 2f);
        }

        private const string ActionFlowIcon = "\uE65F";

        private bool DrawNotifTypeIcon(Graphics g, string evType, int avX, int avY, int avSize, Color color)
        {
            if (evType != "notif_actionflow") return false;
            using var iconFont = _matSymFamily != null
                ? new Font(_matSymFamily, avSize * 0.5f, FontStyle.Regular, GraphicsUnit.Pixel)
                : new Font("Segoe MDL2 Assets", avSize * 0.5f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var iconBrush = new SolidBrush(color);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(ActionFlowIcon, iconFont, iconBrush, new RectangleF(avX, avY, avSize, avSize), fmt);
            return true;
        }

        private void DrawNotifPortrait(Graphics g, string imageUrl, string name, string friendId, string evType,
                                       bool showDot, int avX, int avY, int avSize, int avR, Color ringColor)
        {
            Bitmap? avatar = null;
            if (!string.IsNullOrEmpty(imageUrl))
                lock (_notifImgCache) { _notifImgCache.TryGetValue(imageUrl, out avatar); }

            var oldClip = g.Clip;
            using (var avPath = RoundedRectPath(avX, avY, avSize, avSize, avR))
            {
                g.SetClip(avPath, CombineMode.Intersect);
                if (avatar != null)
                {
                    DrawImageCover(g, avatar, new Rectangle(avX, avY, avSize, avSize));
                    g.SetClip(oldClip, CombineMode.Replace);
                }
                else
                {
                    using var avBg = new SolidBrush(_theme.BgHover);
                    g.FillPath(avBg, avPath);
                    g.SetClip(oldClip, CombineMode.Replace);
                    if (!DrawNotifTypeIcon(g, evType, avX, avY, avSize, _theme.Accent))
                    {
                        string initials = name.Length > 0 ? name[0].ToString().ToUpper() : "?";
                        using var initFont  = new Font("Segoe UI", avSize * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel);
                        using var initBrush = new SolidBrush(_theme.Tx2);
                        var initFmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        g.DrawString(initials, initFont, initBrush, new RectangleF(avX, avY, avSize, avSize), initFmt);
                    }
                }
            }

            if (showDot)
            {
                var st = FriendStatus(friendId);
                if (!string.IsNullOrEmpty(st))
                    DrawStatusDot(g, st, avX + avSize - 3f, avY + avSize - 3f, 5f, ringColor);
            }
        }

        private void DrawNotificationItem(Graphics g, NotifEntry entry, int x, int y, int w, int h, bool showButton = true)
        {
            var th        = _theme;
            bool hasJoin  = showButton && entry.EvType == "friend_gps" && !string.IsNullOrEmpty(entry.Location);
            bool hasAccept = showButton && entry.EvType is "notif_friendreq" or "notif_groupinvite"
                          && !string.IsNullOrEmpty(entry.NotifId);
            bool hasButton = hasJoin || hasAccept;
            string buttonCdKey = hasJoin ? entry.FriendId : entry.NotifId;

            var cardColor = Color.FromArgb(190, th.BgCard);
            using (var bg = new SolidBrush(cardColor))
                FillRoundedRect(g, bg, x, y, w, h, 8);

            int jbX = x + w - NotifBtnW - 10;

            using (var timeFont  = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point))
            using (var timeBrush = new SolidBrush(Color.FromArgb(220, th.Tx3)))
            {
                var timeFmt = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Near };
                g.DrawString(entry.Time, timeFont, timeBrush,
                    new RectangleF(jbX, y + 7, NotifBtnW, 14f), timeFmt);
            }

            if (hasButton)
            {
                bool inCooldown = _joinCooldowns.TryGetValue(buttonCdKey, out var cdTime)
                    && (DateTime.UtcNow - cdTime).TotalSeconds < 5;
                int jbY = y + h - NotifBtnH - 7;
                using var jbBg = new SolidBrush(inCooldown ? Color.FromArgb(170, th.Ok) : Color.FromArgb(210, th.Accent));
                FillRoundedRect(g, jbBg, jbX, jbY, NotifBtnW, NotifBtnH, 6);
                using var jbBrush = new SolidBrush(Color.White);
                var jbFmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                if (inCooldown)
                {
                    using var iconFont = _matSymFamily != null
                        ? new Font(_matSymFamily, 16f, FontStyle.Regular, GraphicsUnit.Point)
                        : new Font("Segoe MDL2 Assets", 14f, FontStyle.Regular, GraphicsUnit.Point);
                    g.DrawString("", iconFont, jbBrush, new RectangleF(jbX, jbY, NotifBtnW, NotifBtnH), jbFmt);
                }
                else
                {
                    using var lblFont = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point);
                    g.DrawString(hasJoin ? "Join" : "Accept", lblFont, jbBrush,
                        new RectangleF(jbX, jbY, NotifBtnW, NotifBtnH), jbFmt);
                }
            }

            const int avSize = 40, avR = 8;
            int avX = x + 10;
            int avY = y + (h - avSize) / 2;
            DrawNotifPortrait(g, entry.ImageUrl, entry.FriendName, entry.FriendId, entry.EvType,
                              EventHasStatusDot(entry.EvType), avX, avY, avSize, avR, cardColor);

            int textX     = avX + avSize + 11;
            int textRight = jbX - 8;

            const float row1H = 17f, row2H = 15f, rowGap = 3f;
            float row1Y = y + (h - row1H - rowGap - row2H) / 2f;
            float row2Y = row1Y + row1H + rowGap;
            var ellipsisFmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

            using (var nameFont  = new Font("Segoe UI", 10.5f, FontStyle.Bold, GraphicsUnit.Point))
            using (var nameBrush = new SolidBrush(th.Tx1))
                g.DrawString(entry.FriendName, nameFont, nameBrush,
                    new RectangleF(textX, row1Y, Math.Max(textRight - textX, 10f), row1H), ellipsisFmt);

            using (var evFont  = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point))
            using (var evBrush = new SolidBrush(th.Tx3))
                g.DrawString(EventSentence(entry.EvType, entry.EvText), evFont, evBrush,
                    new RectangleF(textX, row2Y, Math.Max(textRight - textX, 10f), row2H), ellipsisFmt);
        }

        private static bool EventHasStatusDot(string evType) => evType switch
        {
            "friend_removed"    => false,
            "notif_friendreq"   => false,
            "notif_groupinvite" => false,
            _                   => true,
        };

        private static string EventSentence(string evType, string evText) => evType switch
        {
            "friend_online"      => "Went Online",
            "friend_offline"     => "Went Offline",
            "friend_gps"         => string.IsNullOrWhiteSpace(evText) ? "Changed world" : "Joined \"" + evText.TrimStart('→', ' ') + "\"",
            "friend_status"      => string.IsNullOrWhiteSpace(evText) ? "Changed status" : "Changed status to \"" + evText + "\"",
            "friend_statusdesc"  => string.IsNullOrWhiteSpace(evText) ? "Changed status text" : "\"" + evText + "\"",
            "friend_bio"         => "Updated their bio",
            "friend_added"       => "Added you as a friend",
            "friend_removed"     => "Removed you as a friend",
            "notif_friendreq"    => "Has sent you a friend request",
            "notif_invite"       => string.IsNullOrWhiteSpace(evText) ? "Invited you" : "Invited you to \"" + evText + "\"",
            "notif_groupinvite"  => string.IsNullOrWhiteSpace(evText) ? "Invited you to a group" : "Invited you to the group \"" + evText + "\"",
            "notif_requestinvite" => string.IsNullOrWhiteSpace(evText) ? "Wants an invite" : "Wants an invite: \"" + evText + "\"",
            _                    => evText ?? "",
        };

        private Color EventColor(string evType) => evType switch
        {
            "friend_online"      => _theme.Ok,
            "friend_offline"     => _theme.Tx3,
            "friend_gps"         => _theme.Accent,
            "friend_status"      => _theme.Warn,
            "friend_statusdesc"  => _theme.Cyan,
            "friend_bio"         => _theme.Cyan,
            "friend_added"       => _theme.Ok,
            "friend_removed"     => _theme.Err,
            "notif_friendreq"    => _theme.Ok,
            "notif_invite"       => _theme.Accent,
            "notif_groupinvite"  => _theme.Warn,
            "notif_requestinvite" => _theme.Accent,
            _                    => _theme.Tx2,
        };

        private void DrawMusicPlayer(Graphics g)
        {
            var th        = _theme;
            const int tabBottom = 68;
            const int pad       = 18;

            bool hasMedia = !string.IsNullOrWhiteSpace(_mediaTitle);

            if (!hasMedia)
            {
                using var font  = new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point);
                using var brush = new SolidBrush(th.Tx3);
                var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("No media playing", font, brush,
                    new RectangleF(pad, tabBottom, W - pad * 2, H - tabBottom - pad), fmt);
                return;
            }

            // Background is drawn by DrawBackground() — no duplicate here.

            // Layout constants
            const int artSize = MusicArtSize;
            int artX = (W - artSize) / 2;   // centered
            int artY = MusicArtY;

            // Album art (centered, rounded)
            if (_albumArt != null)
            {
                var artRect = new Rectangle(artX, artY, artSize, artSize);
                using var artPath = RoundedRectPath(artX, artY, artSize, artSize, 14);
                var oldClip = g.Clip;
                g.SetClip(artPath, CombineMode.Intersect);
                g.DrawImage(_albumArt, artRect);
                g.SetClip(oldClip, CombineMode.Replace);
            }
            else
            {
                using var artBg = new SolidBrush(Color.FromArgb(70, th.BgHover));
                FillRoundedRect(g, artBg, artX, artY, artSize, artSize, 14);
                using var noteFnt = new Font("Segoe UI", 36f, FontStyle.Regular, GraphicsUnit.Point);
                using var noteBr  = new SolidBrush(Color.FromArgb(80, th.Tx2));
                var noteFmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("♫", noteFnt, noteBr, new RectangleF(artX, artY, artSize, artSize), noteFmt);
            }

            int artBottom = artY + artSize;

            // Title + Artist (centered below art)
            var ellipsisFmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter,
                                                  FormatFlags = StringFormatFlags.NoWrap,
                                                  Alignment = StringAlignment.Center };

            using var titleFont  = new Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Point);
            using var titleBrush = new SolidBrush(Color.White);
            g.DrawString(_mediaTitle, titleFont, titleBrush,
                new RectangleF(pad, artBottom + 8, W - pad * 2, 26), ellipsisFmt);

            if (!string.IsNullOrWhiteSpace(_mediaArtist))
            {
                using var artistFont  = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
                using var artistBrush = new SolidBrush(Color.FromArgb(200, th.Tx2));
                g.DrawString(_mediaArtist, artistFont, artistBrush,
                    new RectangleF(pad, artBottom + 36, W - pad * 2, 20), ellipsisFmt);
            }

            // Progress bar
            int barY = MusicBarY;
            int barH = MusicBarH;
            int barX = pad + 4;
            int barW = W - (barX + pad + 4);

            double curPos = GetCurrentMediaPosition();
            float  prog   = _mediaDuration > 0 ? (float)(curPos / _mediaDuration) : 0f;
            prog = Math.Clamp(prog, 0f, 1f);

            // Track
            using var trackBr = new SolidBrush(Color.FromArgb(55, th.Tx2));
            FillRoundedRect(g, trackBr, barX, barY, barW, barH, barH / 2);
            // Fill
            if (prog > 0)
            {
                int fillW = Math.Max(barH, (int)(barW * prog));
                using var fillBr = new SolidBrush(th.Accent);
                FillRoundedRect(g, fillBr, barX, barY, fillW, barH, barH / 2);
                // Knob
                int knobX = barX + fillW - 6;
                int knobY = barY - 3;
                using var knobBr = new SolidBrush(Color.White);
                g.FillEllipse(knobBr, knobX, knobY, 12, 12);
            }

            // Time labels
            string posStr = FormatTime(curPos);
            string durStr = FormatTime(_mediaDuration);
            using var timeFnt = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point);
            using var timeBr  = new SolidBrush(Color.FromArgb(160, th.Tx2));
            g.DrawString(posStr, timeFnt, timeBr,
                new RectangleF(barX, barY + barH + 4, 55, 15),
                new StringFormat { Alignment = StringAlignment.Near });
            g.DrawString(durStr, timeFnt, timeBr,
                new RectangleF(barX + barW - 55, barY + barH + 4, 55, 15),
                new StringFormat { Alignment = StringAlignment.Far });

            // Controls
            // Play button: large filled accent circle, center at (W/2, ctrlCY)
            // Prev/Next: smaller, subtle bg circle
            int ctrlCY = MusicCtrlCY;
            int ctrlCX = W / 2;
            const int playR  = MusicPlayR;
            const int skipR  = 18;            // skip circle radius
            const int skipGap = 84;           // center-to-center from play

            // Prev button
            DrawSkipButton(g, th, ctrlCX - skipGap, ctrlCY, skipR, prev: true);
            // Play/Pause button
            DrawPlayButton(g, th, ctrlCX, ctrlCY, playR, _mediaPlaying);
            // Next button
            DrawSkipButton(g, th, ctrlCX + skipGap, ctrlCY, skipR, prev: false);
        }

        private void DrawPlayButton(Graphics g, OverlayTheme th, int cx, int cy, int r, bool playing)
        {
            // Filled accent circle
            using var bgBr = new SolidBrush(th.Accent);
            g.FillEllipse(bgBr, cx - r, cy - r, r * 2, r * 2);
            // White icon drawn as GDI+ shapes
            if (playing)
            {
                // Pause: two white rounded bars
                int bw = 6, bh = (int)(r * 0.85f), bx1 = cx - bw - 3, bx2 = cx + 3;
                int by = cy - bh / 2;
                using var wb = new SolidBrush(Color.White);
                FillRoundedRect(g, wb, bx1, by, bw, bh, 3);
                FillRoundedRect(g, wb, bx2, by, bw, bh, 3);
            }
            else
            {
                // Play: white filled triangle shifted right slightly
                int th2 = (int)(r * 0.75f);
                var pts = new PointF[]
                {
                    new(cx - th2 / 2 + 2, cy - th2),
                    new(cx - th2 / 2 + 2, cy + th2),
                    new(cx + th2 + 2,      cy),
                };
                using var wb = new SolidBrush(Color.White);
                g.FillPolygon(wb, pts);
            }
        }

        private void DrawSkipButton(Graphics g, OverlayTheme th, int cx, int cy, int r, bool prev)
        {
            // Subtle semi-transparent circle
            using var bgBr = new SolidBrush(Color.FromArgb(60, th.BgHover));
            g.FillEllipse(bgBr, cx - r, cy - r, r * 2, r * 2);
            using var border = new Pen(Color.FromArgb(40, th.Brd), 1f);
            g.DrawEllipse(border, cx - r, cy - r, r * 2, r * 2);

            using var wb = new SolidBrush(Color.FromArgb(220, Color.White));
            int tw = (int)(r * 0.52f); // triangle half-height
            int tx = prev ? cx + 2 : cx - 2;

            if (prev)
            {
                // Bar on left, triangle pointing left
                g.FillRectangle(wb, tx - tw - 4, cy - tw, 3, tw * 2);
                var pts = new PointF[]
                {
                    new(tx - tw + 2, cy),
                    new(tx + tw - 2, cy - tw),
                    new(tx + tw - 2, cy + tw),
                };
                g.FillPolygon(wb, pts);
            }
            else
            {
                // Triangle pointing right, bar on right
                var pts = new PointF[]
                {
                    new(tx + tw - 2, cy),
                    new(tx - tw + 2, cy - tw),
                    new(tx - tw + 2, cy + tw),
                };
                g.FillPolygon(wb, pts);
                g.FillRectangle(wb, tx + tw + 1, cy - tw, 3, tw * 2);
            }
        }

        private static string FormatTime(double secs)
        {
            if (secs <= 0) return "0:00";
            var ts = TimeSpan.FromSeconds(secs);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes}:{ts.Seconds:D2}";
        }

        private void PresentOverlayTexture()
        {
            if (_overlayTex == null || OpenVR.Overlay == null || _overlayHandle == 0) return;
            var tex = new Valve.VR.Texture_t
            {
                handle      = _overlayTex.NativePointer,
                eType       = ETextureType.DirectX,
                eColorSpace = EColorSpace.Auto,
            };
            OpenVR.Overlay.SetOverlayTexture(_overlayHandle, ref tex);
            _d3dContext?.Flush();
        }

        private static Font GetCachedFont(string family, float size, FontStyle style, GraphicsUnit unit = GraphicsUnit.Point)
            => new(family, size, style, unit);

        private static Rectangle CoverSrcRect(Bitmap img, Rectangle dest)
        {
            float srcAspect = (float)img.Width / img.Height;
            float dstAspect = (float)dest.Width / dest.Height;
            if (srcAspect > dstAspect)
            {
                // Source wider → crop left/right
                int srcW = (int)(img.Height * dstAspect);
                return new Rectangle((img.Width - srcW) / 2, 0, srcW, img.Height);
            }
            // Source taller → crop top/bottom
            int srcH = (int)(img.Width / dstAspect);
            return new Rectangle(0, (img.Height - srcH) / 2, img.Width, srcH);
        }

        private static void DrawImageCover(Graphics g, Bitmap img, Rectangle dest)
            => g.DrawImage(img, dest, CoverSrcRect(img, dest), GraphicsUnit.Pixel);

        private static void FillRoundedRect(Graphics g, Brush brush, int x, int y, int w, int h, int r)
        {
            if (w <= 0 || h <= 0) return;
            r = Math.Min(r, Math.Min(w / 2, h / 2));
            using var path = RoundedRectPath(x, y, w, h, r);
            g.FillPath(brush, path);
        }

        private static void DrawRoundedRect(Graphics g, Pen pen, int x, int y, int w, int h, int r)
        {
            if (w <= 0 || h <= 0) return;
            r = Math.Min(r, Math.Min(w / 2, h / 2));
            using var path = RoundedRectPath(x, y, w, h, r);
            g.DrawPath(pen, path);
        }

        private static GraphicsPath RoundedRectPath(int x, int y, int w, int h, int r)
        {
            var path = new GraphicsPath();
            int d = r * 2;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
            _cts?.Dispose();
        }
    }
}
#endif
