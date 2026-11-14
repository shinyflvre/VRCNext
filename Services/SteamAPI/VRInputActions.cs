using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Valve.VR;

namespace VRCNext.Services
{
    internal static class VrInputActions
    {
        public const uint BtnA           = 1;
        public const uint BtnB           = 2;
        public const uint BtnTrigger     = 3;
        public const uint BtnGrip        = 4;
        public const uint BtnPad         = 5;
        public const uint BtnPadTouch    = 6;
        public const uint BtnStick       = 7;
        public const uint BtnStickTouch  = 8;
        public const uint BtnATouch      = 10;
        public const uint BtnBTouch      = 11;
        public const uint BtnTriggerTouch = 12;
        public const uint BtnGripForce   = 14;
        public const uint BtnPadForce    = 15;

        public static readonly Dictionary<uint, string> ButtonNames = new()
        {
            { BtnA,            "A"             },
            { BtnB,            "B"             },
            { BtnTrigger,      "Trigger"       },
            { BtnGrip,         "Grip"          },
            { BtnPad,          "Trackpad"      },
            { BtnPadTouch,     "Trackpad Touch" },
            { BtnStick,        "Stick"         },
            { BtnStickTouch,   "Stick Touch"   },
            { BtnATouch,       "A Touch"       },
            { BtnBTouch,       "B Touch"       },
            { BtnTriggerTouch, "Trigger Touch" },
            { BtnGripForce,    "Grip Force"    },
            { BtnPadForce,     "Trackpad Force" },
        };

        public const ulong AllowedMask =
            (1UL << (int)BtnA)            | (1UL << (int)BtnB)            |
            (1UL << (int)BtnTrigger)      | (1UL << (int)BtnGrip)         |
            (1UL << (int)BtnPad)          | (1UL << (int)BtnPadTouch)     |
            (1UL << (int)BtnStick)        | (1UL << (int)BtnStickTouch)   |
            (1UL << (int)BtnATouch)       | (1UL << (int)BtnBTouch)       |
            (1UL << (int)BtnTriggerTouch) | (1UL << (int)BtnGripForce)    |
            (1UL << (int)BtnPadForce);

        public const ulong RecordMask =
            (1UL << (int)BtnA)       | (1UL << (int)BtnB)    |
            (1UL << (int)BtnTrigger) | (1UL << (int)BtnGrip) |
            (1UL << (int)BtnPad)     | (1UL << (int)BtnStick);

        private const float ForceThreshold = 0.25f;
        private const int   PollIntervalMs = 5;

        private static readonly object _lock = new();

        private static bool   _ready;
        private static bool   _warned;
        private static string _error = "";
        private static Action<string>? _log;

        private static ulong _setHandle;
        private static ulong _srcLeft;
        private static ulong _srcRight;

        private static readonly Dictionary<uint, ulong> _digital = new();
        private static ulong _hTriggerValue, _hGripValue, _hGripForce, _hPadForce, _hStickPos, _hPadPos;

        private static VRActiveActionSet_t[] _sets = new VRActiveActionSet_t[1];
        private static uint _setSize, _digSize, _anaSize;

        private static ulong _leftBits, _rightBits;
        private static int   _lastPollTick;

        public static bool   Ready     => _ready;
        public static string LastError => _error;

        public static bool Requested { get; private set; }

        public static bool Active => Requested && _ready;

        public static void SetRequested(bool on) => Requested = on;

        public static bool Initialize(Action<string> log)
        {
            lock (_lock)
            {
                if (_ready) return true;
                _log = log;

                var input = OpenVR.Input;
                if (input == null) { _error = "OpenVR input interface unavailable"; Warn(log); return false; }

                var manifest = Path.Combine(AppContext.BaseDirectory, "vrinput", "actions.json");
                if (!File.Exists(manifest)) { _error = "actions.json not found: " + manifest; Warn(log); return false; }

                var res = input.SetActionManifestPath(manifest);
                if (res != EVRInputError.None) { _error = "SetActionManifestPath: " + res; Warn(log); return false; }

                if (input.GetActionSetHandle("/actions/vrcnext", ref _setHandle) != EVRInputError.None || _setHandle == 0)
                { _error = "action set handle failed"; Warn(log); return false; }

                input.GetInputSourceHandle("/user/hand/left",  ref _srcLeft);
                input.GetInputSourceHandle("/user/hand/right", ref _srcRight);

                Bind(input, BtnA,            "a_click");
                Bind(input, BtnATouch,       "a_touch");
                Bind(input, BtnB,            "b_click");
                Bind(input, BtnBTouch,       "b_touch");
                Bind(input, BtnTrigger,      "trigger_click");
                Bind(input, BtnTriggerTouch, "trigger_touch");
                Bind(input, BtnGrip,         "grip_click");
                Bind(input, BtnPad,          "pad_click");
                Bind(input, BtnPadTouch,     "pad_touch");
                Bind(input, BtnStick,        "stick_click");
                Bind(input, BtnStickTouch,   "stick_touch");

                input.GetActionHandle("/actions/vrcnext/in/trigger_value",  ref _hTriggerValue);
                input.GetActionHandle("/actions/vrcnext/in/grip_value",     ref _hGripValue);
                input.GetActionHandle("/actions/vrcnext/in/grip_force",     ref _hGripForce);
                input.GetActionHandle("/actions/vrcnext/in/pad_force",      ref _hPadForce);
                input.GetActionHandle("/actions/vrcnext/in/stick_pos",      ref _hStickPos);
                input.GetActionHandle("/actions/vrcnext/in/pad_pos",        ref _hPadPos);

                _sets[0] = new VRActiveActionSet_t
                {
                    ulActionSet          = _setHandle,
                    ulRestrictedToDevice = OpenVR.k_ulInvalidInputValueHandle,
                    ulSecondaryActionSet = 0,
                    nPriority            = 0,
                };
                _setSize = (uint)Marshal.SizeOf<VRActiveActionSet_t>();
                _digSize = (uint)Marshal.SizeOf<InputDigitalActionData_t>();
                _anaSize = (uint)Marshal.SizeOf<InputAnalogActionData_t>();

                _ready = true;
                _error = "";
                _warned = false;
                log("[VRInput] SteamVR Input active, manifest " + manifest);
                return true;
            }
        }

        private static void Warn(Action<string> log)
        {
            if (_warned) return;
            _warned = true;
            log("[VRInput] SteamVR Input unavailable, staying on legacy input: " + _error);
        }

        private static void Bind(CVRInput input, uint bit, string action)
        {
            ulong h = 0;
            input.GetActionHandle("/actions/vrcnext/in/" + action, ref h);
            _digital[bit] = h;
        }

        public static ulong GetButtons(int side)
        {
            Poll();
            return side == 1 ? _leftBits : side == 2 ? _rightBits : _leftBits | _rightBits;
        }

        public static bool GetThumb(int side, out float x, out float y)
        {
            x = 0f; y = 0f;
            if (!_ready) return false;
            Poll();

            var input = OpenVR.Input;
            if (input == null) return false;

            ulong src = side == 1 ? _srcLeft : _srcRight;
            var a = new InputAnalogActionData_t();

            if (_hStickPos != 0
                && input.GetAnalogActionData(_hStickPos, ref a, _anaSize, src) == EVRInputError.None
                && a.bActive && (MathF.Abs(a.x) > 0.001f || MathF.Abs(a.y) > 0.001f))
            { x = a.x; y = a.y; return true; }

            if (_hPadPos != 0
                && input.GetAnalogActionData(_hPadPos, ref a, _anaSize, src) == EVRInputError.None
                && a.bActive)
            { x = a.x; y = a.y; return true; }

            return false;
        }

        private static void Poll()
        {
            if (!_ready) return;

            var now = Environment.TickCount;
            if (unchecked(now - _lastPollTick) < PollIntervalMs) return;

            lock (_lock)
            {
                if (!_ready) return;
                if (unchecked(Environment.TickCount - _lastPollTick) < PollIntervalMs) return;
                _lastPollTick = Environment.TickCount;

                var input = OpenVR.Input;
                if (input == null) return;

                if (input.UpdateActionState(_sets, _setSize) != EVRInputError.None) return;

                ulong left = 0, right = 0;
                var d = new InputDigitalActionData_t();

                foreach (var kv in _digital)
                {
                    if (kv.Value == 0) continue;
                    if (input.GetDigitalActionData(kv.Value, ref d, _digSize, _srcLeft) == EVRInputError.None
                        && d.bActive && d.bState) left |= 1UL << (int)kv.Key;
                    if (input.GetDigitalActionData(kv.Value, ref d, _digSize, _srcRight) == EVRInputError.None
                        && d.bActive && d.bState) right |= 1UL << (int)kv.Key;
                }

                Force(input, _hGripForce, BtnGripForce, ref left, ref right);
                Force(input, _hPadForce,  BtnPadForce,  ref left, ref right);

                _leftBits  = left;
                _rightBits = right;
            }
        }

        private static void Force(CVRInput input, ulong handle, uint bit, ref ulong left, ref ulong right)
        {
            if (handle == 0) return;
            var a = new InputAnalogActionData_t();
            if (input.GetAnalogActionData(handle, ref a, _anaSize, _srcLeft) == EVRInputError.None
                && a.bActive && a.x >= ForceThreshold) left |= 1UL << (int)bit;
            if (input.GetAnalogActionData(handle, ref a, _anaSize, _srcRight) == EVRInputError.None
                && a.bActive && a.x >= ForceThreshold) right |= 1UL << (int)bit;
        }

    }
}
