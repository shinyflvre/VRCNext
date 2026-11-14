using Newtonsoft.Json.Linq;

namespace VRCNext;

// Owns all Space Flight (SteamVR playspace drag) state, logic, and message handling.
// SteamVRService runs in the shared VR subprocess — see VRSubprocessHost / VRSubprocess.

public class SpaceFlightController : IDisposable
{
    private readonly CoreLibrary _core;
    private readonly VROverlayController _vroCtrl;
    private VRSubprocessHost? _sfWiredHost;
    private JObject? _sfLastState;

    public bool IsConnected => _core.VrOverlay?.SfConnected ?? false;

    public SpaceFlightController(CoreLibrary core, VROverlayController vroCtrl)
    {
        _core    = core;
        _vroCtrl = vroCtrl;
    }

    private VRSubprocessHost EnsureHost()
    {
        if (_core.VrOverlay == null)
        {
            _core.VrOverlay = new VRSubprocessHost(
                s => _core.SendToJS("log", new { msg = s, color = "sec" }));
        }

        var h = _core.VrOverlay;
        if (!ReferenceEquals(_sfWiredHost, h))
        {
            _sfWiredHost = h;

            h.OnSfUpdate += d =>
            {
                _sfLastState = d;
                _core.SendToJS("sfUpdate", d);
            };

            h.OnSfQuit += () =>
            {
                if (!h.AnyConnected) _core.VrOverlay = null;
                _core.SendToJS("sfUpdate", new
                {
                    connected = false, dragging = false,
                    offsetX = 0, offsetY = 0, offsetZ = 0,
                    leftController = false, rightController = false, error = (string?)null
                });
                _vroCtrl.UpdateToolStates();
            };
        }

        return _core.VrOverlay;
    }
    public void HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "sfConnect":
            {
                var host = EnsureHost();
                var (auth, tfa) = _core.VrcApi.GetCookies();
                host.InputMode = _core.Settings.VrInputMode;
                host.EnsureRunning("", _core.HttpPort, auth, tfa);
                host.SfConnect(
                    _core.Settings.SfMultiplier,
                    _core.Settings.SfLockX, _core.Settings.SfLockY, _core.Settings.SfLockZ,
                    SfBtn(_core.Settings.SfLeftResetButton,    _core.Settings.SfIdxLeftResetButton),
                    SfBtn(_core.Settings.SfRightResetButton,   _core.Settings.SfIdxRightResetButton),
                    SfBtn(_core.Settings.SfLeftDragButton,     _core.Settings.SfIdxLeftDragButton),
                    SfBtn(_core.Settings.SfRightDragButton,    _core.Settings.SfIdxRightDragButton),
                    SfBtn(_core.Settings.SfLeftGravityButton,  _core.Settings.SfIdxLeftGravityButton),
                    SfBtn(_core.Settings.SfRightGravityButton, _core.Settings.SfIdxRightGravityButton),
                    _core.Settings.SfGravity);
                _vroCtrl.UpdateToolStates();
                break;
            }

            case "sfDisconnect":
                if (_core.VrOverlay != null)
                {
                    _core.VrOverlay.SfDisconnect();
                    if (!_core.VrOverlay.AnyConnected) _core.VrOverlay = null;
                }
                _core.SendToJS("sfUpdate", new
                {
                    connected = false, dragging = false,
                    offsetX = 0, offsetY = 0, offsetZ = 0,
                    leftController = false, rightController = false, error = (string?)null
                });
                _vroCtrl.UpdateToolStates();
                break;

            case "sfReset":
                _core.VrOverlay?.SfReset();
                break;

            case "sfConfig":
            {
                var mult = msg["dragMultiplier"]?.Value<float>() ?? 1f;
                var lx   = msg["lockX"]?.Value<bool>() ?? false;
                var ly   = msg["lockY"]?.Value<bool>() ?? false;
                var lz   = msg["lockZ"]?.Value<bool>() ?? false;
                var lr   = (uint)(msg["leftResetBtn"]?.Value<int>()  ?? 32);
                var rr   = (uint)(msg["rightResetBtn"]?.Value<int>() ?? 0);
                var ld   = (uint)(msg["leftDragBtn"]?.Value<int>()   ?? 0);
                var rd   = (uint)(msg["rightDragBtn"]?.Value<int>()  ?? 32);
                var lg   = (uint)(msg["leftGravityBtn"]?.Value<int>()  ?? 0);
                var rg   = (uint)(msg["rightGravityBtn"]?.Value<int>() ?? 0);
                var grav = msg["gravity"]?.Value<float>() ?? 9.8f;
                _core.VrOverlay?.SfConfig(mult, lx, ly, lz, lr, rr, ld, rd, lg, rg, grav);
                break;
            }
        }
    }

    public void ResendState()
    {
        if (_core.VrOverlay?.SfConnected != true) return;
        if (_sfLastState != null) _core.SendToJS("sfUpdate", _sfLastState);
    }

    private uint SfBtn(uint legacy, uint index)
    {
        return _core.Settings.VrInputMode == 1 ? index : legacy;
    }

    public void Toggle()
    {
        if (_core.VrOverlay?.SfConnected == true)
        {
            _core.VrOverlay.SfDisconnect();
            if (!_core.VrOverlay.AnyConnected) _core.VrOverlay = null;
            _core.SendToJS("sfUpdate", new
            {
                connected = false, dragging = false,
                offsetX = 0, offsetY = 0, offsetZ = 0,
                leftController = false, rightController = false, error = (string?)null
            });
        }
        else
        {
            var host = EnsureHost();
            var (auth, tfa) = _core.VrcApi.GetCookies();
            host.InputMode = _core.Settings.VrInputMode;
            host.EnsureRunning("", _core.HttpPort, auth, tfa);
            host.SfConnect(
                _core.Settings.SfMultiplier,
                _core.Settings.SfLockX, _core.Settings.SfLockY, _core.Settings.SfLockZ,
                SfBtn(_core.Settings.SfLeftResetButton,    _core.Settings.SfIdxLeftResetButton),
                SfBtn(_core.Settings.SfRightResetButton,   _core.Settings.SfIdxRightResetButton),
                SfBtn(_core.Settings.SfLeftDragButton,     _core.Settings.SfIdxLeftDragButton),
                SfBtn(_core.Settings.SfRightDragButton,    _core.Settings.SfIdxRightDragButton),
                SfBtn(_core.Settings.SfLeftGravityButton,  _core.Settings.SfIdxLeftGravityButton),
                SfBtn(_core.Settings.SfRightGravityButton, _core.Settings.SfIdxRightGravityButton),
                _core.Settings.SfGravity);
        }
    }

    public void Dispose()
    {
        _sfWiredHost = null;
        _sfLastState = null;
    }
}
