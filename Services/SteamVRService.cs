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
        public bool LeftHandEnabled { get; set; }
        public bool RightHandEnabled { get; set; } = true;
        public bool UseGripButton { get; set; } = true;

        public bool IsConnected { get; private set; }
        public bool IsDragging { get; private set; }
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

        private bool _leftDragging;
        private bool _rightDragging;
        private Vector3 _leftRawAnchor;
        private Vector3 _rightRawAnchor;
        private Vector3 _leftOffsetAtGrab;
        private Vector3 _rightOffsetAtGrab;

        private uint _leftIdx = OpenVR.k_unTrackedDeviceIndexInvalid;
        private uint _rightIdx = OpenVR.k_unTrackedDeviceIndexInvalid;
        private readonly TrackedDevicePose_t[] _rawPoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];

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

        public SteamVRService(Action<string> log)
        {
            _log = log;
        }

        public void SetUpdateCallback(Action<object> cb) => _onUpdate = cb;

        public bool Connect()
        {
            if (IsConnected) return true;
            LastError = null;

            try
            {
                var err = EVRInitError.None;
                _vrSystem = OpenVR.Init(ref err, EVRApplicationType.VRApplication_Overlay);
                if (err != EVRInitError.None)
                {
                    _log($"[SteamVR] Overlay init: {err}, fallback Background...");
                    try { OpenVR.Shutdown(); } catch { }
                    err = EVRInitError.None;
                    _vrSystem = OpenVR.Init(ref err, EVRApplicationType.VRApplication_Background);
                    if (err != EVRInitError.None)
                    {
                        LastError = $"OpenVR: {err}";
                        _log($"[SteamVR] {LastError}");
                        return false;
                    }
                    _log("[SteamVR] Init: Background");
                }
                else
                {
                    _log("[SteamVR] Init: Overlay");
                }

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
                var dir = AppDomain.CurrentDomain.BaseDirectory;
                var exe = Path.GetFileName(Environment.ProcessPath ?? "VRCNext.exe");
                var path = Path.Combine(dir, "vrcnext.vrmanifest");
                var json = JsonSerializer.Serialize(new
                {
                    source = "builtin",
                    applications = new[]
                    {
                        new
                        {
                            app_key = "vrcnext.spaceflight.overlay",
                            launch_type = "binary",
                            binary_path_windows = exe,
                            is_dashboard_overlay = true,
                            strings = new { en_us = new { name = "VRCNext Space Flight" } }
                        }
                    }
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

            try { OpenVR.Shutdown(); } catch { }

            IsConnected = false;
            IsDragging = false;
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
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    ProcessFrame();
                    EmitState();
                    await Task.Delay(11, ct);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log($"[SteamVR] {ex.Message}");
                    await Task.Delay(500, ct);
                }
            }
            _running = false;
        }

        private ulong GetDragMask() => UseGripButton ? GRIP_MASK : (STICK_CLICK_MASK | A_BUTTON_MASK);

        private void ProcessFrame()
        {
            if (_vrSystem == null) return;

            var evt = new VREvent_t();
            while (_vrSystem.PollNextEvent(ref evt, (uint)Marshal.SizeOf<VREvent_t>())) { }

            _vrSystem.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseRawAndUncalibrated, 0, _rawPoses);
            UpdateControllerIndices();

            var mask = GetDragMask();
            bool rp = false;
            bool lp = false;
            Vector3 rRaw = default;
            Vector3 lRaw = default;

            if (_rightIdx != OpenVR.k_unTrackedDeviceIndexInvalid && RightHandEnabled)
            {
                var s = new VRControllerState_t();
                if (_vrSystem.GetControllerState(_rightIdx, ref s, (uint)Marshal.SizeOf<VRControllerState_t>()))
                {
                    rp = (s.ulButtonPressed & mask) != 0;
                    rRaw = GetRawPos(_rightIdx);
                    if (rp && !_loggedRightPress)
                    {
                        _loggedRightPress = true;
                        _log($"[SteamVR] R-DRAG raw=({rRaw.X:F3},{rRaw.Y:F3},{rRaw.Z:F3})");
                    }
                    if (!rp) _loggedRightPress = false;
                }
            }

            if (_leftIdx != OpenVR.k_unTrackedDeviceIndexInvalid)
            {
                var s = new VRControllerState_t();
                if (_vrSystem.GetControllerState(_leftIdx, ref s, (uint)Marshal.SizeOf<VRControllerState_t>()))
                {
                    if ((s.ulButtonPressed & STICK_CLICK_MASK) != 0)
                        ResetOffset();

                    if (LeftHandEnabled)
                    {
                        lp = (s.ulButtonPressed & mask) != 0;
                        lRaw = GetRawPos(_leftIdx);
                        if (lp && !_loggedLeftPress)
                        {
                            _loggedLeftPress = true;
                            _log($"[SteamVR] L-DRAG raw=({lRaw.X:F3},{lRaw.Y:F3},{lRaw.Z:F3})");
                        }
                        if (!lp) _loggedLeftPress = false;
                    }
                }
            }

            HandleHandDrag(ref _rightDragging, rp, rRaw, ref _rightRawAnchor, ref _rightOffsetAtGrab);

            if (LeftHandEnabled)
                HandleHandDrag(ref _leftDragging, lp, lRaw, ref _leftRawAnchor, ref _leftOffsetAtGrab);
            else
                _leftDragging = false;

            IsDragging = _rightDragging || _leftDragging;
        }

        private void HandleHandDrag(ref bool wasDragging, bool pressed, Vector3 rawPos, ref Vector3 rawAnchor, ref Vector3 offsetAtGrab)
        {
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

            off.X = Math.Clamp(off.X, -50f, 50f);
            off.Y = Math.Clamp(off.Y, -50f, 50f);
            off.Z = Math.Clamp(off.Z, -50f, 50f);

            OffsetX = off.X;
            OffsetY = off.Y;
            OffsetZ = off.Z;

            OpenVR.ChaperoneSetup.RevertWorkingCopy();

            var standing = _baseStandingPose;
            standing.m3 += off.X;
            standing.m7 += off.Y;
            standing.m11 += off.Z;
            OpenVR.ChaperoneSetup.SetWorkingStandingZeroPoseToRawTrackingPose(ref standing);

            if (_hasBaseSeatedPose)
            {
                var seated = _baseSeatedPose;
                seated.m3 += off.X;
                seated.m7 += off.Y;
                seated.m11 += off.Z;
                OpenVR.ChaperoneSetup.SetWorkingSeatedZeroPoseToRawTrackingPose(ref seated);
            }

            OpenVR.ChaperoneSetup.ShowWorkingSetPreview();
            _previewActive = true;
            OpenVR.ChaperoneSetup.CommitWorkingCopy(EChaperoneConfigFile.Live);

            if (verbose)
                _log($"[SteamVR] Offset ({off.X:F3},{off.Y:F3},{off.Z:F3})");
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

        public void SetOffset(float x, float y, float z)
        {
            ApplyOffset(new Vector3(x, y, z), true);
        }

        private Vector3 GetRawPos(uint i)
        {
            if (i < _rawPoses.Length && _rawPoses[i].bPoseIsValid)
            {
                return new Vector3(
                    _rawPoses[i].mDeviceToAbsoluteTracking.m3,
                    _rawPoses[i].mDeviceToAbsoluteTracking.m7,
                    _rawPoses[i].mDeviceToAbsoluteTracking.m11);
            }

            return Vector3.Zero;
        }

        private void UpdateControllerIndices()
        {
            if (_vrSystem == null) return;
            _leftIdx = _vrSystem.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.LeftHand);
            _rightIdx = _vrSystem.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.RightHand);
        }

        private void EmitState()
        {
            _onUpdate?.Invoke(new
            {
                connected = IsConnected,
                dragging = IsDragging,
                offsetX = MathF.Round(OffsetX, 3),
                offsetY = MathF.Round(OffsetY, 3),
                offsetZ = MathF.Round(OffsetZ, 3),
                leftController = _leftIdx != OpenVR.k_unTrackedDeviceIndexInvalid,
                rightController = _rightIdx != OpenVR.k_unTrackedDeviceIndexInvalid,
                error = (string?)null
            });
        }

        public void ApplyConfig(float dragMultiplier, bool lockX, bool lockY, bool lockZ, bool leftHand, bool rightHand, bool useGrip)
        {
            DragMultiplier = Math.Clamp(dragMultiplier, 0.1f, 100f);
            LockX = lockX;
            LockY = lockY;
            LockZ = lockZ;
            LeftHandEnabled = leftHand;
            RightHandEnabled = rightHand;
            UseGripButton = useGrip;
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