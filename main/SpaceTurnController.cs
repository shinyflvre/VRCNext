using Newtonsoft.Json.Linq;

namespace VRCNext;

// Owns all Space Turn (SteamVR playspace rotation) state, logic, and message handling.
// Shares the SteamVRService instance with Space Flight inside the VR subprocess, so both
// tools work on the same playspace offset and rotation.

public class SpaceTurnController : IDisposable
{
    private readonly CoreLibrary _core;
    private readonly VROverlayController _vroCtrl;
    private VRSubprocessHost? _stWiredHost;
    private JObject? _stLastState;

    public bool IsConnected => _core.VrOverlay?.StConnected ?? false;

    public SpaceTurnController(CoreLibrary core, VROverlayController vroCtrl)
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
        if (!ReferenceEquals(_stWiredHost, h))
        {
            _stWiredHost = h;

            h.OnStUpdate += d =>
            {
                _stLastState = d;
                _core.SendToJS("stUpdate", d);
            };

            h.OnStQuit += () =>
            {
                if (!h.AnyConnected) _core.VrOverlay = null;
                _core.SendToJS("stUpdate", DisconnectedState());
                _vroCtrl.UpdateToolStates();
            };
        }

        return _core.VrOverlay;
    }

    private static object DisconnectedState() => new
    {
        connected = false, turning = false, rotation = 0f,
        leftController = false, rightController = false, error = (string?)null
    };

    private void Connect()
    {
        var host = EnsureHost();
        var (auth, tfa) = _core.VrcApi.GetCookies();
        host.InputMode = _core.Settings.VrInputMode;
        host.EnsureRunning("", _core.HttpPort, auth, tfa);
        host.StConnect(
            _core.Settings.StMultiplier,
            _core.Settings.StSnapDegrees,
            _core.Settings.StInvert,
            _core.Settings.StSmoothing,
            StBtn(_core.Settings.StLeftTurnButton,   _core.Settings.StIdxLeftTurnButton),
            StBtn(_core.Settings.StRightTurnButton,  _core.Settings.StIdxRightTurnButton),
            StBtn(_core.Settings.StLeftResetButton,  _core.Settings.StIdxLeftResetButton),
            StBtn(_core.Settings.StRightResetButton, _core.Settings.StIdxRightResetButton));
    }

    private void Disconnect()
    {
        if (_core.VrOverlay != null)
        {
            _core.VrOverlay.StDisconnect();
            if (!_core.VrOverlay.AnyConnected) _core.VrOverlay = null;
        }
        _core.SendToJS("stUpdate", DisconnectedState());
    }
    public void HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "stConnect":
                Connect();
                _vroCtrl.UpdateToolStates();
                break;

            case "stDisconnect":
                Disconnect();
                _vroCtrl.UpdateToolStates();
                break;

            case "stReset":
                _core.VrOverlay?.StReset();
                break;

            case "stConfig":
            {
                var mult = msg["turnMultiplier"]?.Value<float>() ?? 1f;
                var snap = msg["snapDegrees"]?.Value<float>() ?? 0f;
                var inv  = msg["invert"]?.Value<bool>() ?? false;
                var smo  = msg["smoothing"]?.Value<float>() ?? 0f;
                var lt   = (uint)(msg["leftTurnBtn"]?.Value<int>()   ?? 2);
                var rt   = (uint)(msg["rightTurnBtn"]?.Value<int>()  ?? 0);
                var lr   = (uint)(msg["leftResetBtn"]?.Value<int>()  ?? 0);
                var rr   = (uint)(msg["rightResetBtn"]?.Value<int>() ?? 0);
                _core.VrOverlay?.StConfig(mult, snap, inv, smo, lt, rt, lr, rr);
                break;
            }
        }
    }

    public void ResendState()
    {
        if (_core.VrOverlay?.StConnected != true) return;
        if (_stLastState != null) _core.SendToJS("stUpdate", _stLastState);
    }

    private uint StBtn(uint legacy, uint index)
    {
        return _core.Settings.VrInputMode == 1 ? index : legacy;
    }

    public void Toggle()
    {
        if (_core.VrOverlay?.StConnected == true) Disconnect();
        else Connect();
    }

    public void Dispose()
    {
        _stWiredHost = null;
        _stLastState = null;
    }
}
