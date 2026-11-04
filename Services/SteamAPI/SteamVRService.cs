using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Valve.VR;

namespace VRCNext.Services
{
    public class SteamVRService : IDisposable
    {
        public float DragMultiplier { get; set; } = 1.0f;
        public bool LockX { get; set; }
        public bool LockY { get; set; }
        public bool LockZ { get; set; }
        // Per-hand button assignments — 0 means "None" (that hand has no action).
        // Defaults: left Thumbstick = Reset, right Thumbstick = Drag.
        public uint LeftResetButton  { get; set; } = (uint)EVRButtonId.k_EButton_Axis0; // 32
        public uint RightResetButton { get; set; } = 0;
        public uint LeftDragButton   { get; set; } = 0;
        public uint RightDragButton  { get; set; } = (uint)EVRButtonId.k_EButton_Axis0; // 32

        public bool DragEnabled { get; set; }
        public bool TurnEnabled { get; set; }
        public float TurnMultiplier { get; set; } = 1.0f;
        public float TurnSnapDegrees { get; set; }
        public bool TurnInvert { get; set; }
        public float TurnSmoothing { get; set; }
        private float _turnSmoothDelta;
        public uint LeftTurnButton  { get; set; } = (uint)EVRButtonId.k_EButton_Grip; // 2
        public uint RightTurnButton { get; set; } = 0;
        public uint LeftTurnResetButton  { get; set; } = 0;
        public uint RightTurnResetButton { get; set; } = 0;

        public bool IsConnected { get; private set; }
        public bool IsDragging { get; private set; }
        public bool IsTurning { get; private set; }
        public float RotationDeg { get; private set; }
        public float OffsetX { get; private set; }
        public float OffsetY { get; private set; }
        public float OffsetZ { get; private set; }
        public string? LastError { get; private set; }

        private CVRSystem? _vrSystem;
        private CancellationTokenSource? _cts;
        private bool _running;
        private bool _disposed;
        private readonly Action<string> _log;
        private Action<object>? _onUpdate;
        private Action<object>? _onTurnUpdate;

        private bool _turnEmitPrimed;
        private bool _turnEmitConnected;
        private bool _turnEmitTurning;
        private float _turnEmitRotation;
        private bool _turnEmitLeftController;
        private bool _turnEmitRightController;

        private bool _leftTurning;
        private bool _rightTurning;
        private float _leftYawPrev;
        private float _rightYawPrev;
        private float _turnAccum;
        private bool _leftTurnResetHeldPrev;
        private bool _rightTurnResetHeldPrev;

        // Playspace delta applied on top of the cached base poses: standing = _deltaR * base + _deltaT (raw space).
        private float[] _deltaR = Identity3();
        private Vector3 _deltaT;

        private bool _emitPrimed;
        private bool _emitConnected;
        private bool _emitDragging;
        private float _emitOffsetX;
        private float _emitOffsetY;
        private float _emitOffsetZ;
        private bool _emitLeftController;
        private bool _emitRightController;

        private bool _leftDragging;
        private bool _rightDragging;
        private Vector3 _leftRawAnchor;
        private Vector3 _rightRawAnchor;
        private Vector3 _leftOffsetAtGrab;
        private Vector3 _rightOffsetAtGrab;
        // Edge detection for Reset — fire ResetOffset once per press, not every poll.
        private bool _leftResetHeldPrev;
        private bool _rightResetHeldPrev;

        public float Gravity { get; set; } = 9.8f;
        public uint LeftGravityButton  { get; set; } = 0;
        public uint RightGravityButton { get; set; } = 0;
        private bool _leftGravDragging;
        private bool _rightGravDragging;
        private Vector3 _leftGravRawAnchor;
        private Vector3 _rightGravRawAnchor;
        private Vector3 _leftGravOffsetAtGrab;
        private Vector3 _rightGravOffsetAtGrab;
        private Vector3 _offsetVel;
        private Vector3 _prevOffset;
        private bool _ballistic;
        private Vector3 _ballVel;
        private const float DT = 0.011f;
        private const float GRAV_AIR_DRAG = 0.1f;

        private uint _leftIdx = OpenVR.k_unTrackedDeviceIndexInvalid;
        private uint _rightIdx = OpenVR.k_unTrackedDeviceIndexInvalid;
        private readonly TrackedDevicePose_t[] _rawPoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];

        public event Action? OnVRQuit;

        private volatile bool _vrQuit;
        private bool _loggedRightPress;
        private bool _loggedLeftPress;
        private ulong _overlayHandle;
        private ulong _thumbHandle;
        private bool _previewActive;

        private HmdMatrix34_t _baseStandingPose;
        private HmdMatrix34_t _baseSeatedPose;
        private bool _hasBaseStandingPose;
        private bool _hasBaseSeatedPose;

        private static readonly ulong GRIP_MASK = 1UL << (int)EVRButtonId.k_EButton_Grip;
        private static readonly ulong STICK_CLICK_MASK = 1UL << (int)EVRButtonId.k_EButton_Axis0;
        private static readonly ulong A_BUTTON_MASK = 1UL << (int)EVRButtonId.k_EButton_A;
        private bool _ownedInit = false;

        public SteamVRService(Action<string> log)
        {
            _log = log;
        }

        public void SetUpdateCallback(Action<object> cb) => _onUpdate = cb;
        public void SetTurnUpdateCallback(Action<object> cb) => _onTurnUpdate = cb;

        public bool Connect()
        {
            if (IsConnected) return true;
            LastError = null;
            _vrQuit = false;

            try
            {
                if (OpenVR.System != null)
                {
                    // OpenVR already initialized by another service (e.g., VROverlayService); reuse it
                    _vrSystem = OpenVR.System;
                    _log("[SteamVR] Reusing existing OpenVR session");
                }
                else
                {
                    var err = EVRInitError.None;
                    _vrSystem = OpenVR.Init(ref err, EVRApplicationType.VRApplication_Overlay);
                    if (err != EVRInitError.None)
                    {
                        var overlayErr = err;
                        _log($"[SteamVR] Overlay init: {err}, fallback Background...");
                        try { OpenVR.Shutdown(); } catch { }
                        err = EVRInitError.None;
                        _vrSystem = OpenVR.Init(ref err, EVRApplicationType.VRApplication_Background);
                        if (err != EVRInitError.None)
                        {
                            _log($"[SteamVR] OpenVR: {err}");
                            var hint = OpenVrInitHint.Describe(overlayErr, err);
                            if (hint != null) _log($"[SteamVR] {hint}");
                            LastError = hint ?? $"OpenVR: {err}";
                            return false;
                        }
                        _log("[SteamVR] Init: Background");
                    }
                    else
                    {
                        _log("[SteamVR] Init: Overlay");
                    }
                }
                OpenVRSession.Acquire();
                _ownedInit = true;

                if (VrInputActions.Requested) VrInputActions.Initialize(_log);

                if (OpenVR.Overlay != null)
                {
                    var oErr = OpenVR.Overlay.CreateDashboardOverlay("vrcnext.spaceflight", "VRCNext Space Flight", ref _overlayHandle, ref _thumbHandle);
                    if (oErr == EVROverlayError.KeyInUse)
                        OpenVR.Overlay.FindOverlay("vrcnext.spaceflight", ref _overlayHandle);
                    _log($"[SteamVR] Overlay: {oErr}");
                }

                RegisterManifest();
                IsConnected = true;
                UpdateControllerIndices();
                CacheBasePoses();

                if (_hasBaseStandingPose)
                {
                    _log($"[SteamVR] StandingPose base=({_baseStandingPose.m3:F4}, {_baseStandingPose.m7:F4}, {_baseStandingPose.m11:F4})");
                    OpenVR.ChaperoneSetup?.ShowWorkingSetPreview();
                    OpenVR.ChaperoneSetup?.HideWorkingSetPreview();
                    _log("[SteamVR] ShowWorkingSetPreview: OK");
                }

                _log($"[SteamVR] Connected L={_leftIdx != OpenVR.k_unTrackedDeviceIndexInvalid} R={_rightIdx != OpenVR.k_unTrackedDeviceIndexInvalid}");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _log($"[SteamVR] Error: {ex.Message}");
                return false;
            }
        }

        private void RegisterManifest()
        {
            try
            {
                var exePath = Environment.ProcessPath ?? "VRCNext.exe";
#if WINDOWS
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vrcnext.vrmanifest");
                var application = new
                {
                    app_key = "vrcnext.spaceflight.overlay",
                    launch_type = "binary",
                    binary_path_windows = Path.GetFileName(exePath),
                    is_dashboard_overlay = true,
                    strings = new { en_us = new { name = "VRCNext Space Flight" } }
                };
#else
                // AppImage and installed Linux builds are commonly read-only. SteamVR
                // only needs a user-scoped manifest registration, so keep it in XDG data.
                var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                if (string.IsNullOrWhiteSpace(dataHome))
                    dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
                var dir = Path.Combine(dataHome!, "vrcnext");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "vrcnext.vrmanifest");
                var application = new
                {
                    app_key = "vrcnext.spaceflight.overlay",
                    launch_type = "binary",
                    binary_path_linux = exePath,
                    is_dashboard_overlay = true,
                    strings = new { en_us = new { name = "VRCNext Space Flight" } }
                };
#endif
                var json = JsonSerializer.Serialize(new
                {
                    source = "builtin",
                    applications = new[] { application }
                }, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
                if (OpenVR.Applications != null)
                    _log($"[SteamVR] Manifest: {OpenVR.Applications.AddApplicationManifest(path, true)}");
            }
            catch (Exception ex)
            {
                _log($"[SteamVR] Manifest: {ex.Message}");
            }
        }

        private void CacheBasePoses()
        {
            if (OpenVR.ChaperoneSetup == null) return;

            try
            {
                OpenVR.ChaperoneSetup.RevertWorkingCopy();

                var standing = new HmdMatrix34_t();
                _hasBaseStandingPose = OpenVR.ChaperoneSetup.GetWorkingStandingZeroPoseToRawTrackingPose(ref standing);
                if (_hasBaseStandingPose) _baseStandingPose = standing;

                var seated = new HmdMatrix34_t();
                _hasBaseSeatedPose = OpenVR.ChaperoneSetup.GetWorkingSeatedZeroPoseToRawTrackingPose(ref seated);
                if (_hasBaseSeatedPose) _baseSeatedPose = seated;
            }
            catch (Exception ex)
            {
                _hasBaseStandingPose = false;
                _hasBaseSeatedPose = false;
                _log($"[SteamVR] BasePose cache failed: {ex.Message}");
            }
        }

        public void Disconnect()
        {
            StopPolling();
            if (!IsConnected) return;

            if (_previewActive && OpenVR.ChaperoneSetup != null)
            {
                try
                {
                    OpenVR.ChaperoneSetup.HideWorkingSetPreview();
                    OpenVR.ChaperoneSetup.RevertWorkingCopy();
                }
                catch { }
            }

            if (_overlayHandle != 0 && OpenVR.Overlay != null)
            {
                try { OpenVR.Overlay.DestroyOverlay(_overlayHandle); } catch { }
                _overlayHandle = 0;
            }

            if (_ownedInit)
            {
                OpenVRSession.Release();
                _ownedInit = false;
            }

            IsConnected = false;
            IsDragging = false;
            IsTurning = false;
            DragEnabled = false;
            TurnEnabled = false;
            _vrSystem = null;
            _hasBaseStandingPose = false;
            _hasBaseSeatedPose = false;
            _log("[SteamVR] Disconnected");
        }

        public void StartPolling()
        {
            if (_running) return;
            _cts = new CancellationTokenSource();
            _running = true;
            _emitPrimed = false;
            _turnEmitPrimed = false;
            StartVrserverMonitor(_cts.Token);
            _ = PollLoopAsync(_cts.Token);
        }

        public void StopPolling()
        {
            _running = false;
            _cts?.Cancel();
        }

        private async Task PollLoopAsync(CancellationToken ct)
        {
            _log("[SteamVR] Polling started");
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        ProcessFrame();
                        EmitState();
                        await Task.Delay(11, ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _log($"[SteamVR] {ex.Message}");
                        CrashHandler.AddBreadcrumb($"SteamVR.PollLoop: {ex.GetType().Name}: {ex.Message}");
                        CrashHandler.WriteEntry("SteamVRService.PollLoop", ex);
                        try { await Task.Delay(500, ct); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            }
            catch (Exception ex)
            {
                CrashHandler.WriteEntry("SteamVRService.PollLoop.Outer", ex);
            }
            _running = false;
        }

        private void ProcessFrame()
        {
            if (_vrQuit || _vrSystem == null) return;

            var evt = new VREvent_t();
            while (_vrSystem.PollNextEvent(ref evt, (uint)Marshal.SizeOf<VREvent_t>()))
            {
                if ((EVREventType)evt.eventType == EVREventType.VREvent_Quit)
                {
                    _vrQuit = true;
                    _vrSystem = null; // null immediately — no further P/Invoke calls reach dead memory
                    try { OpenVR.System?.AcknowledgeQuit_Exiting(); } catch { }
                    _cts?.Cancel();
                    // Fire OnVRQuit on a thread pool thread — handlers (e.g. Disconnect) make OpenVR
                    // calls that would AV if called synchronously from inside ProcessFrame while
                    // SteamVR is mid-teardown.
                    _ = Task.Run(() => OnVRQuit?.Invoke());
                    return;
                }
            }

            _vrSystem.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseRawAndUncalibrated, 0, _rawPoses);
            UpdateControllerIndices();

            // Read controller states once per hand
            ulong leftBtns  = ReadButtons(_leftIdx);
            ulong rightBtns = ReadButtons(_rightIdx);

            ProcessTurn(leftBtns, rightBtns);

            if (!DragEnabled)
            {
                _leftDragging = false;
                _rightDragging = false;
                _leftGravDragging = false;
                _rightGravDragging = false;
                _ballistic = false;
                _leftResetHeldPrev = false;
                _rightResetHeldPrev = false;
                IsDragging = false;
                return;
            }

            // RESET — edge-triggered, fires once per press (any configured hand)
            bool leftResetHeld  = IsBitSet(leftBtns,  LeftResetButton);
            bool rightResetHeld = IsBitSet(rightBtns, RightResetButton);
            if (leftResetHeld  && !_leftResetHeldPrev)  ResetPosition();
            if (rightResetHeld && !_rightResetHeldPrev) ResetPosition();
            _leftResetHeldPrev  = leftResetHeld;
            _rightResetHeldPrev = rightResetHeld;

            // DRAG — per hand, held while button is pressed
            bool lp = false, rp = false;
            bool lValid = false, rValid = false;
            Vector3 lRaw = default, rRaw = default;

            if (LeftDragButton != 0 && _leftIdx != OpenVR.k_unTrackedDeviceIndexInvalid)
            {
                lp = IsBitSet(leftBtns, LeftDragButton);
                if (lp) lValid = TryGetRawPos(_leftIdx, out lRaw);
                if (lp && lValid && !_loggedLeftPress)
                {
                    _loggedLeftPress = true;
                    _log($"[SteamVR] L-DRAG raw=({lRaw.X:F3},{lRaw.Y:F3},{lRaw.Z:F3})");
                }
                if (!lp) _loggedLeftPress = false;
            }
            if (RightDragButton != 0 && _rightIdx != OpenVR.k_unTrackedDeviceIndexInvalid)
            {
                rp = IsBitSet(rightBtns, RightDragButton);
                if (rp) rValid = TryGetRawPos(_rightIdx, out rRaw);
                if (rp && rValid && !_loggedRightPress)
                {
                    _loggedRightPress = true;
                    _log($"[SteamVR] R-DRAG raw=({rRaw.X:F3},{rRaw.Y:F3},{rRaw.Z:F3})");
                }
                if (!rp) _loggedRightPress = false;
            }

            if (LeftDragButton != 0)
                HandleHandDrag(ref _leftDragging, lp, lValid, lRaw, ref _leftRawAnchor, ref _leftOffsetAtGrab);
            else
                _leftDragging = false;

            if (RightDragButton != 0)
                HandleHandDrag(ref _rightDragging, rp, rValid, rRaw, ref _rightRawAnchor, ref _rightOffsetAtGrab);
            else
                _rightDragging = false;

            bool gravWasDragging = _leftGravDragging || _rightGravDragging;

            bool lgp = false, rgp = false;
            bool lgValid = false, rgValid = false;
            Vector3 lgRaw = default, rgRaw = default;
            if (LeftGravityButton != 0 && _leftIdx != OpenVR.k_unTrackedDeviceIndexInvalid)
            {
                lgp = IsBitSet(leftBtns, LeftGravityButton);
                if (lgp) lgValid = TryGetRawPos(_leftIdx, out lgRaw);
            }
            if (RightGravityButton != 0 && _rightIdx != OpenVR.k_unTrackedDeviceIndexInvalid)
            {
                rgp = IsBitSet(rightBtns, RightGravityButton);
                if (rgp) rgValid = TryGetRawPos(_rightIdx, out rgRaw);
            }

            if (LeftGravityButton != 0)
                HandleHandDrag(ref _leftGravDragging, lgp, lgValid, lgRaw, ref _leftGravRawAnchor, ref _leftGravOffsetAtGrab);
            else
                _leftGravDragging = false;

            if (RightGravityButton != 0)
                HandleHandDrag(ref _rightGravDragging, rgp, rgValid, rgRaw, ref _rightGravRawAnchor, ref _rightGravOffsetAtGrab);
            else
                _rightGravDragging = false;

            bool gravNowDragging = _leftGravDragging || _rightGravDragging;
            bool anyHold = _leftDragging || _rightDragging || gravNowDragging;

            if (anyHold)
            {
                _ballistic = false;
            }
            else if (gravWasDragging && !gravNowDragging)
            {
                _ballistic = true;
                _ballVel = _offsetVel;
            }

            var curOff = new Vector3(OffsetX, OffsetY, OffsetZ);
            var instVel = (curOff - _prevOffset) / DT;
            _offsetVel = Vector3.Lerp(_offsetVel, instVel, 0.6f);
            _prevOffset = curOff;

            if (_ballistic && !anyHold)
                StepBallistic();

            IsDragging = _rightDragging || _leftDragging || gravNowDragging || _ballistic;
        }

        // SPACE TURN — hold the turn button and rotate the controller around the vertical axis.
        // The playspace rotates with the hand, pivoting around the HMD so the user stays in place.
        private void ProcessTurn(ulong leftBtns, ulong rightBtns)
        {
            if (!TurnEnabled)
            {
                _leftTurning = false;
                _rightTurning = false;
                _turnAccum = 0f;
                _leftTurnResetHeldPrev = false;
                _rightTurnResetHeldPrev = false;
                IsTurning = false;
                return;
            }

            bool leftResetHeld  = IsBitSet(leftBtns,  LeftTurnResetButton);
            bool rightResetHeld = IsBitSet(rightBtns, RightTurnResetButton);
            if (leftResetHeld  && !_leftTurnResetHeldPrev)  ResetRotation();
            if (rightResetHeld && !_rightTurnResetHeldPrev) ResetRotation();
            _leftTurnResetHeldPrev  = leftResetHeld;
            _rightTurnResetHeldPrev = rightResetHeld;

            // One hand at a time; the left hand takes priority while both turn buttons are held.
            float delta = HandleHandTurn(ref _leftTurning, LeftTurnButton != 0 && IsBitSet(leftBtns, LeftTurnButton), _leftIdx, ref _leftYawPrev);
            if (_leftTurning)
                _rightTurning = false;
            else
                delta = HandleHandTurn(ref _rightTurning, RightTurnButton != 0 && IsBitSet(rightBtns, RightTurnButton), _rightIdx, ref _rightYawPrev);

            IsTurning = _leftTurning || _rightTurning;
            if (!IsTurning) { _turnAccum = 0f; _turnSmoothDelta = 0f; return; }

            delta *= TurnMultiplier;
            if (TurnInvert) delta = -delta;

            // Smoothing: exponential low-pass on the per-frame yaw delta. 0 = raw, 100 = heavy filtering.
            if (TurnSmoothing > 0f)
            {
                float alpha = 1f - (TurnSmoothing / 100f) * 0.95f;
                _turnSmoothDelta += (delta - _turnSmoothDelta) * alpha;
                delta = _turnSmoothDelta;
                if (MathF.Abs(delta) < 0.00002f) { _turnSmoothDelta = 0f; return; }
            }
            else if (delta == 0f) return;

            if (TurnSnapDegrees <= 0f)
            {
                ApplyTurn(delta);
                return;
            }

            float snap = TurnSnapDegrees * MathF.PI / 180f;
            _turnAccum += delta;
            while (_turnAccum >= snap)  { ApplyTurn(snap);  _turnAccum -= snap; }
            while (_turnAccum <= -snap) { ApplyTurn(-snap); _turnAccum += snap; }
        }

        // Returns the yaw change (radians) of this hand since the previous frame while its turn button is held.
        private float HandleHandTurn(ref bool wasTurning, bool pressed, uint idx, ref float yawPrev)
        {
            if (!pressed || idx == OpenVR.k_unTrackedDeviceIndexInvalid)
            {
                wasTurning = false;
                return 0f;
            }

            if (!TryGetRawYaw(idx, out var yaw)) return 0f;

            if (!wasTurning)
            {
                wasTurning = true;
                yawPrev = yaw;
                return 0f;
            }

            float d = WrapAngle(yaw - yawPrev);
            yawPrev = yaw;
            return d;
        }

        private void ApplyTurn(float radians)
        {
            if (radians == 0f) return;

            Vector3 pivot;
            if (OpenVR.k_unTrackedDeviceIndex_Hmd < _rawPoses.Length && _rawPoses[OpenVR.k_unTrackedDeviceIndex_Hmd].bPoseIsValid)
            {
                var hm = _rawPoses[OpenVR.k_unTrackedDeviceIndex_Hmd].mDeviceToAbsoluteTracking;
                pivot = new Vector3(hm.m3, hm.m7, hm.m11);
            }
            else
            {
                pivot = _deltaT;
            }

            var rot = RotY(radians);
            var newR = Mul3(rot, _deltaR);
            var newT = Apply3(rot, _deltaT - pivot) + pivot;

            _deltaR = newR;
            RotationDeg = WrapAngle((RotationDeg * MathF.PI / 180f) + radians) * 180f / MathF.PI;
            ApplyOffset(newT);
        }

        private bool TryGetRawYaw(uint i, out float yaw)
        {
            yaw = 0f;
            if (i >= _rawPoses.Length || !_rawPoses[i].bPoseIsValid || !_rawPoses[i].bDeviceIsConnected) return false;
            var m = _rawPoses[i].mDeviceToAbsoluteTracking;
            float fx = -m.m2, fz = -m.m10;
            if (fx * fx + fz * fz < 0.0025f)
            {
                fx = m.m0; fz = m.m8;
                if (fx * fx + fz * fz < 0.0025f) return false;
                yaw = MathF.Atan2(fx, fz) - MathF.PI / 2f;
                return true;
            }
            yaw = MathF.Atan2(fx, fz);
            return true;
        }

        private static float WrapAngle(float a)
        {
            while (a > MathF.PI)  a -= 2f * MathF.PI;
            while (a < -MathF.PI) a += 2f * MathF.PI;
            return a;
        }

        private static float[] Identity3() => new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

        private static float[] RotY(float a)
        {
            float c = MathF.Cos(a), s = MathF.Sin(a);
            return new float[] { c, 0, s, 0, 1, 0, -s, 0, c };
        }

        private static float[] Mul3(float[] a, float[] b)
        {
            var r = new float[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
            return r;
        }

        private static Vector3 Apply3(float[] r, Vector3 v) => new Vector3(
            r[0] * v.X + r[1] * v.Y + r[2] * v.Z,
            r[3] * v.X + r[4] * v.Y + r[5] * v.Z,
            r[6] * v.X + r[7] * v.Y + r[8] * v.Z);

        private HmdMatrix34_t ComposeDelta(HmdMatrix34_t b)
        {
            var r = _deltaR;
            return new HmdMatrix34_t
            {
                m0  = r[0] * b.m0 + r[1] * b.m4 + r[2] * b.m8,
                m1  = r[0] * b.m1 + r[1] * b.m5 + r[2] * b.m9,
                m2  = r[0] * b.m2 + r[1] * b.m6 + r[2] * b.m10,
                m3  = r[0] * b.m3 + r[1] * b.m7 + r[2] * b.m11 + _deltaT.X,
                m4  = r[3] * b.m0 + r[4] * b.m4 + r[5] * b.m8,
                m5  = r[3] * b.m1 + r[4] * b.m5 + r[5] * b.m9,
                m6  = r[3] * b.m2 + r[4] * b.m6 + r[5] * b.m10,
                m7  = r[3] * b.m3 + r[4] * b.m7 + r[5] * b.m11 + _deltaT.Y,
                m8  = r[6] * b.m0 + r[7] * b.m4 + r[8] * b.m8,
                m9  = r[6] * b.m1 + r[7] * b.m5 + r[8] * b.m9,
                m10 = r[6] * b.m2 + r[7] * b.m6 + r[8] * b.m10,
                m11 = r[6] * b.m3 + r[7] * b.m7 + r[8] * b.m11 + _deltaT.Z,
            };
        }

        private void StepBallistic()
        {
            _ballVel.Y += Gravity * DT;
            _ballVel *= (1f - GRAV_AIR_DRAG * DT);

            var target = new Vector3(OffsetX, OffsetY, OffsetZ) + _ballVel * DT;

            bool landed = false;
            if (target.Y > 0f) { target.Y = 0f; landed = true; }

            ApplyOffset(target);

            if (landed
                || _ballVel.Length() < 0.03f
                || MathF.Abs(target.X) > 300f || MathF.Abs(target.Z) > 300f || target.Y < -300f)
            {
                _ballistic = false;
                _ballVel = Vector3.Zero;
            }
        }

        private ulong ReadButtons(uint deviceIdx)
        {
            if (deviceIdx == OpenVR.k_unTrackedDeviceIndexInvalid) return 0;
            if (VrInputActions.Active)
                return VrInputActions.GetButtons(deviceIdx == _leftIdx ? 1 : deviceIdx == _rightIdx ? 2 : 0);

            if (_vrSystem == null) return 0;
            var s = new VRControllerState_t();
            return _vrSystem.GetControllerState(deviceIdx, ref s, (uint)Marshal.SizeOf<VRControllerState_t>())
                ? s.ulButtonPressed
                : 0;
        }

        private static bool IsBitSet(ulong buttons, uint buttonId)
            => buttonId != 0 && (buttons & (1UL << (int)buttonId)) != 0;

        private void HandleHandDrag(ref bool wasDragging, bool pressed, bool rawValid, Vector3 rawPos, ref Vector3 rawAnchor, ref Vector3 offsetAtGrab)
        {
            if (pressed && !rawValid) return;

            if (pressed && !wasDragging)
            {
                wasDragging = true;
                rawAnchor = rawPos;
                offsetAtGrab = new Vector3(OffsetX, OffsetY, OffsetZ);
                return;
            }

            if (pressed && wasDragging)
            {
                var rawDelta = rawPos - rawAnchor;
                if (MathF.Abs(rawDelta.X) > 10f || MathF.Abs(rawDelta.Y) > 10f || MathF.Abs(rawDelta.Z) > 10f)
                {
                    rawAnchor = rawPos;
                    offsetAtGrab = new Vector3(OffsetX, OffsetY, OffsetZ);
                    return;
                }

                var target = offsetAtGrab;
                var mult = DragMultiplier;

                if (!LockX) target.X += rawDelta.X * mult;
                if (!LockY) target.Y += rawDelta.Y * mult;
                if (!LockZ) target.Z += rawDelta.Z * mult;

                ApplyOffset(target);
                return;
            }

            if (!pressed && wasDragging)
            {
                wasDragging = false;
            }
        }

        private void ApplyOffset(Vector3 off, bool verbose = false)
        {
            if (OpenVR.ChaperoneSetup == null) return;
            if (!_hasBaseStandingPose) CacheBasePoses();
            if (!_hasBaseStandingPose) return;

            off.X = Math.Clamp(off.X, -500f, 500f);
            off.Y = Math.Clamp(off.Y, -500f, 500f);
            off.Z = Math.Clamp(off.Z, -500f, 500f);

            OffsetX = off.X;
            OffsetY = off.Y;
            OffsetZ = off.Z;
            _deltaT = off;

            OpenVR.ChaperoneSetup.RevertWorkingCopy();

            var standing = ComposeDelta(_baseStandingPose);
            OpenVR.ChaperoneSetup.SetWorkingStandingZeroPoseToRawTrackingPose(ref standing);

            if (_hasBaseSeatedPose)
            {
                var seated = ComposeDelta(_baseSeatedPose);
                OpenVR.ChaperoneSetup.SetWorkingSeatedZeroPoseToRawTrackingPose(ref seated);
            }

            OpenVR.ChaperoneSetup.ShowWorkingSetPreview();
            _previewActive = true;
            OpenVR.ChaperoneSetup.CommitWorkingCopy(EChaperoneConfigFile.Live);

            if (verbose)
                _log($"[SteamVR] Offset ({off.X:F3},{off.Y:F3},{off.Z:F3})");
        }

        // Space Flight reset: clears the translation only, the Space Turn rotation is kept.
        public void ResetPosition()
        {
            _leftDragging = false;
            _rightDragging = false;
            _leftGravDragging = false;
            _rightGravDragging = false;
            _ballistic = false;
            _ballVel = Vector3.Zero;
            _offsetVel = Vector3.Zero;
            _prevOffset = Vector3.Zero;

            if (RotationDeg == 0f)
            {
                ResetOffset();
                return;
            }

            ApplyOffset(Vector3.Zero);
            _log("[SteamVR] Reset position");
        }

        // Space Turn reset: rotates back to zero around the HMD, the Space Flight offset is kept.
        public void ResetRotation()
        {
            _leftTurning = false;
            _rightTurning = false;
            _turnAccum = 0f;

            if (RotationDeg == 0f) return;

            if (OffsetX == 0f && OffsetY == 0f && OffsetZ == 0f)
            {
                ResetOffset();
                return;
            }

            ApplyTurn(-RotationDeg * MathF.PI / 180f);
            _deltaR = Identity3();
            RotationDeg = 0f;
            ApplyOffset(_deltaT);
            _log("[SteamVR] Reset rotation");
        }

        public void ResetOffset()
        {
            OffsetX = 0;
            OffsetY = 0;
            OffsetZ = 0;
            _leftDragging = false;
            _rightDragging = false;
            _leftRawAnchor = Vector3.Zero;
            _rightRawAnchor = Vector3.Zero;
            _leftOffsetAtGrab = Vector3.Zero;
            _rightOffsetAtGrab = Vector3.Zero;
            _leftGravDragging = false;
            _rightGravDragging = false;
            _ballistic = false;
            _ballVel = Vector3.Zero;
            _offsetVel = Vector3.Zero;
            _prevOffset = Vector3.Zero;
            _deltaR = Identity3();
            _deltaT = Vector3.Zero;
            RotationDeg = 0f;
            _leftTurning = false;
            _rightTurning = false;
            _turnAccum = 0f;

            if (OpenVR.ChaperoneSetup != null)
            {
                OpenVR.ChaperoneSetup.RevertWorkingCopy();

                if (_hasBaseStandingPose)
                {
                    var standing = _baseStandingPose;
                    OpenVR.ChaperoneSetup.SetWorkingStandingZeroPoseToRawTrackingPose(ref standing);
                }

                if (_hasBaseSeatedPose)
                {
                    var seated = _baseSeatedPose;
                    OpenVR.ChaperoneSetup.SetWorkingSeatedZeroPoseToRawTrackingPose(ref seated);
                }

                OpenVR.ChaperoneSetup.ShowWorkingSetPreview();
                OpenVR.ChaperoneSetup.CommitWorkingCopy(EChaperoneConfigFile.Live);
                OpenVR.ChaperoneSetup.HideWorkingSetPreview();
                _previewActive = false;
            }

            try { OpenVR.Chaperone?.ForceBoundsVisible(false); } catch { }

            _log("[SteamVR] Reset");
        }

        private bool TryGetRawPos(uint i, out Vector3 pos)
        {
            if (i < _rawPoses.Length && _rawPoses[i].bPoseIsValid && _rawPoses[i].bDeviceIsConnected)
            {
                pos = new Vector3(
                    _rawPoses[i].mDeviceToAbsoluteTracking.m3,
                    _rawPoses[i].mDeviceToAbsoluteTracking.m7,
                    _rawPoses[i].mDeviceToAbsoluteTracking.m11);
                return true;
            }

            pos = Vector3.Zero;
            return false;
        }

        private void UpdateControllerIndices()
        {
            if (_vrSystem == null) return;
            _leftIdx = _vrSystem.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.LeftHand);
            _rightIdx = _vrSystem.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.RightHand);
        }

        private void EmitState()
        {
            EmitTurnState();
            if (!DragEnabled && !_emitPrimed) return;
            var offsetX = MathF.Round(OffsetX, 3);
            var offsetY = MathF.Round(OffsetY, 3);
            var offsetZ = MathF.Round(OffsetZ, 3);
            var leftController = _leftIdx != OpenVR.k_unTrackedDeviceIndexInvalid;
            var rightController = _rightIdx != OpenVR.k_unTrackedDeviceIndexInvalid;

            bool dragConnected = IsConnected && DragEnabled;
            if (_emitPrimed
                && _emitConnected == dragConnected
                && _emitDragging == IsDragging
                && _emitOffsetX.Equals(offsetX)
                && _emitOffsetY.Equals(offsetY)
                && _emitOffsetZ.Equals(offsetZ)
                && _emitLeftController == leftController
                && _emitRightController == rightController)
                return;

            _emitPrimed = true;
            _emitConnected = dragConnected;
            _emitDragging = IsDragging;
            _emitOffsetX = offsetX;
            _emitOffsetY = offsetY;
            _emitOffsetZ = offsetZ;
            _emitLeftController = leftController;
            _emitRightController = rightController;

            _onUpdate?.Invoke(new
            {
                connected = dragConnected,
                dragging = IsDragging,
                offsetX = offsetX,
                offsetY = offsetY,
                offsetZ = offsetZ,
                leftController = leftController,
                rightController = rightController,
                error = (string?)null
            });
        }

        private void EmitTurnState()
        {
            if (!TurnEnabled && !_turnEmitPrimed) return;
            bool turnConnected = IsConnected && TurnEnabled;
            var rotation = MathF.Round(RotationDeg, 1);
            var leftController = _leftIdx != OpenVR.k_unTrackedDeviceIndexInvalid;
            var rightController = _rightIdx != OpenVR.k_unTrackedDeviceIndexInvalid;

            if (_turnEmitPrimed
                && _turnEmitConnected == turnConnected
                && _turnEmitTurning == IsTurning
                && _turnEmitRotation.Equals(rotation)
                && _turnEmitLeftController == leftController
                && _turnEmitRightController == rightController)
                return;

            _turnEmitPrimed = true;
            _turnEmitConnected = turnConnected;
            _turnEmitTurning = IsTurning;
            _turnEmitRotation = rotation;
            _turnEmitLeftController = leftController;
            _turnEmitRightController = rightController;

            _onTurnUpdate?.Invoke(new
            {
                connected = turnConnected,
                turning = IsTurning,
                rotation = rotation,
                leftController = leftController,
                rightController = rightController,
                error = (string?)null
            });
        }

        public void ApplyTurnConfig(float turnMultiplier, float snapDegrees, bool invert, float smoothing,
                                    uint leftTurnBtn, uint rightTurnBtn,
                                    uint leftResetBtn, uint rightResetBtn)
        {
            TurnMultiplier       = Math.Clamp(turnMultiplier, 0.1f, 10f);
            TurnSnapDegrees      = Math.Clamp(snapDegrees, 0f, 180f);
            TurnInvert           = invert;
            TurnSmoothing        = Math.Clamp(smoothing, 0f, 100f);
            LeftTurnButton       = leftTurnBtn;
            RightTurnButton      = rightTurnBtn;
            LeftTurnResetButton  = leftResetBtn;
            RightTurnResetButton = rightResetBtn;
        }

        public void ApplyConfig(float dragMultiplier, bool lockX, bool lockY, bool lockZ,
                                uint leftResetBtn, uint rightResetBtn,
                                uint leftDragBtn,  uint rightDragBtn,
                                uint leftGravityBtn, uint rightGravityBtn, float gravity)
        {
            DragMultiplier    = Math.Clamp(dragMultiplier, 0.1f, 100f);
            LockX             = lockX;
            LockY             = lockY;
            LockZ             = lockZ;
            LeftResetButton   = leftResetBtn;
            RightResetButton  = rightResetBtn;
            LeftDragButton    = leftDragBtn;
            RightDragButton   = rightDragBtn;
            LeftGravityButton = leftGravityBtn;
            RightGravityButton= rightGravityBtn;
            Gravity           = Math.Clamp(gravity, 1.0f, 20.0f);
        }

        // Monitors vrserver.exe with WaitForExitAsync — zero overhead in the poll loop.
        // If vrserver hard-crashes (no VREvent_Quit), sets _vrQuit and cancels the CTS
        // before the next ProcessFrame tick so no further P/Invoke calls reach dead memory.
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
                    if (!ct.IsCancellationRequested && !_vrQuit)
                    {
                        _log("[SteamVR] vrserver.exe exited — nulling OpenVR interface");
                        _vrQuit = true;
                        _vrSystem = null;
                        _cts?.Cancel();
                        _ = Task.Run(() => OnVRQuit?.Invoke());
                    }
                }
                catch { }
                finally { proc.Dispose(); }
            }, CancellationToken.None); // use None so monitor survives ct cancellation during cleanup
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
            _cts?.Dispose();
        }
    }
}