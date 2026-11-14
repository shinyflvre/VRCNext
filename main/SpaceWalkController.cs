using Newtonsoft.Json.Linq;

namespace VRCNext;

public class SpaceWalkController : IDisposable
{
    private readonly CoreLibrary _core;
    private readonly VROverlayController _vroCtrl;
    private VRSubprocessHost? _swWiredHost;
    private JObject? _swLastState;

#if WINDOWS
    public bool IsConnected => _core.VrOverlay?.SwConnected ?? false;
#else
    public bool IsConnected => false;
#endif

    public SpaceWalkController(CoreLibrary core, VROverlayController vroCtrl)
    {
        _core    = core;
        _vroCtrl = vroCtrl;
    }

#if WINDOWS
    private VRSubprocessHost EnsureHost()
    {
        if (_core.VrOverlay == null)
        {
            _core.VrOverlay = new VRSubprocessHost(
                s => _core.SendToJS("log", new { msg = s, color = "sec" }));
        }

        var h = _core.VrOverlay;
        if (!ReferenceEquals(_swWiredHost, h))
        {
            _swWiredHost = h;

            h.OnSwUpdate += d =>
            {
                _swLastState = d;
                _core.SendToJS("swUpdate", d);
            };

            h.OnSwQuit += () =>
            {
                if (!h.AnyConnected) _core.VrOverlay = null;
                _core.SendToJS("swUpdate", DisconnectedState());
                _vroCtrl.UpdateToolStates();
            };
        }

        return _core.VrOverlay;
    }

    private static object DisconnectedState() => new
    {
        connected = false, walking = false, pitch = 0f, yaw = 0f, roll = 0f, tiltSupported = true,
        leftController = false, rightController = false, error = (string?)null
    };

    private void Connect()
    {
        var host = EnsureHost();
        var (auth, tfa) = _core.VrcApi.GetCookies();
        host.InputMode = _core.Settings.VrInputMode;
        host.EnsureRunning("", _core.HttpPort, auth, tfa);
        host.SwConnect(
            _core.Settings.SwMultiplier,
            _core.Settings.SwSmoothing,
            _core.Settings.SwMomentum,
            _core.Settings.SwInvert,
            _core.Settings.SwLockPitch,
            _core.Settings.SwLockRoll,
            _core.Settings.SwLockYaw,
            SwBtn(_core.Settings.SwLeftWalkButton,   _core.Settings.SwIdxLeftWalkButton),
            SwBtn(_core.Settings.SwRightWalkButton,  _core.Settings.SwIdxRightWalkButton),
            SwBtn(_core.Settings.SwLeftResetButton,  _core.Settings.SwIdxLeftResetButton),
            SwBtn(_core.Settings.SwRightResetButton, _core.Settings.SwIdxRightResetButton));
    }

    private void Disconnect()
    {
        if (_core.VrOverlay != null)
        {
            _core.VrOverlay.SwDisconnect();
            if (!_core.VrOverlay.AnyConnected) _core.VrOverlay = null;
        }
        _core.SendToJS("swUpdate", DisconnectedState());
    }
#endif

    public void HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
#if WINDOWS
            case "swConnect":
                Connect();
                _vroCtrl.UpdateToolStates();
                break;

            case "swDisconnect":
                Disconnect();
                _vroCtrl.UpdateToolStates();
                break;

            case "swReset":
                _core.VrOverlay?.SwReset();
                break;

            case "swConfig":
            {
                var mult = msg["multiplier"]?.Value<float>() ?? 1f;
                var smo  = msg["smoothing"]?.Value<float>() ?? 0f;
                var mom  = msg["momentum"]?.Value<float>() ?? 0f;
                var inv  = msg["invert"]?.Value<bool>() ?? false;
                var lp   = msg["lockPitch"]?.Value<bool>() ?? false;
                var lr   = msg["lockRoll"]?.Value<bool>() ?? false;
                var ly   = msg["lockYaw"]?.Value<bool>() ?? false;
                var lw   = (uint)(msg["leftWalkBtn"]?.Value<int>()   ?? 0);
                var rw   = (uint)(msg["rightWalkBtn"]?.Value<int>()  ?? 2);
                var lrs  = (uint)(msg["leftResetBtn"]?.Value<int>()  ?? 0);
                var rrs  = (uint)(msg["rightResetBtn"]?.Value<int>() ?? 0);
                _core.VrOverlay?.SwConfig(mult, smo, mom, inv, lp, lr, ly, lw, rw, lrs, rrs);
                break;
            }
#endif
        }
    }

    public void ResendState()
    {
#if WINDOWS
        if (_core.VrOverlay?.SwConnected != true) return;
        if (_swLastState != null) _core.SendToJS("swUpdate", _swLastState);
#endif
    }

    private uint SwBtn(uint legacy, uint index) => _core.Settings.VrInputMode == 1 ? index : legacy;

    public void Toggle()
    {
#if WINDOWS
        if (_core.VrOverlay?.SwConnected == true) Disconnect();
        else Connect();
#endif
    }

    public void Dispose()
    {
        _swWiredHost = null;
        _swLastState = null;
    }
}
