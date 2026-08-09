using Newtonsoft.Json.Linq;
using VRCNext.Services;

namespace VRCNext;

// Owns all Chatbox + OSC state, logic, and message handling.

public class ChatboxController : IDisposable
{
    private readonly CoreLibrary _core;
    private readonly VROverlayController _vroCtrl;

    // Fields (moved from MainForm.Fields.cs)
    private ChatboxService? _chatbox;
    private OscService? _osc;

    // Public Accessors (for other domains)
    public bool IsEnabled => _chatbox?.Enabled ?? false;
    public OscService? Osc => _osc;

    public ChatboxController(CoreLibrary core, VROverlayController vroCtrl)
    {
        _core = core;
        _vroCtrl = vroCtrl;
        _core.OnChatboxPauseRequest = ms => _chatbox?.PauseDirectSend(ms);
    }

    // Message Handler

    public void HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "chatboxConfig":
                {
                    _chatbox ??= new ChatboxService(s => Invoke(() => _core.SendToJS("log", new { msg = s, color = "sec" })));
                    _chatbox.SetUpdateCallback(data => {
                        try { Invoke(() => _core.SendToJS("chatboxUpdate", data)); } catch (Exception ex) { CrashHandler.WriteEntry("Chatbox.SetUpdateCallback", ex); }
#if WINDOWS
                        try
                        {
                            var d = JObject.FromObject(data);
                            var title   = d["currentTitle"]?.ToString() ?? "";
                            var artist  = d["currentArtist"]?.ToString() ?? "";
                            var posMs   = d["positionMs"]?.Value<double>() ?? 0;
                            var durMs   = d["durationMs"]?.Value<double>() ?? 0;
                            var playing = d["isPlaying"]?.Value<bool>() ?? false;
                            if (!string.IsNullOrEmpty(title))
                                _core.VrOverlay?.UpdateMediaInfo(title, artist, posMs / 1000.0, durMs / 1000.0, playing);
                        }
                        catch (Exception ex) { CrashHandler.WriteEntry("Chatbox.UpdateMediaInfo", ex); }
#endif
                    });

                    var enabled = msg["enabled"]?.Value<bool>() ?? false;
                    var showTime = msg["showTime"]?.Value<bool>() ?? true;
                    var showMedia = msg["showMedia"]?.Value<bool>() ?? true;
                    var showPlaytime = msg["showPlaytime"]?.Value<bool>() ?? true;
                    var showCustomText = msg["showCustomText"]?.Value<bool>() ?? true;
                    var showSystemStats = msg["showSystemStats"]?.Value<bool>() ?? false;
                    var showAfk = msg["showAfk"]?.Value<bool>() ?? false;
                    var afkMessage = msg["afkMessage"]?.ToString() ?? "Currently AFK";
                    var suppressSound = msg["suppressSound"]?.Value<bool>() ?? true;
                    var timeFormat = msg["timeFormat"]?.ToString() ?? "hh:mm tt";
                    var separator = msg["separator"]?.ToString() ?? " | ";
                    var intervalMs = msg["intervalMs"]?.Value<int>() ?? 5000;
                    var customLines = msg["customLines"]?.ToObject<List<string>>() ?? new();
                    var hideBackground = msg["hideBackground"]?.Value<bool>() ?? false;

                    _chatbox.ApplyConfig(enabled, showTime, showMedia, showPlaytime,
                        showCustomText, showSystemStats, showAfk, afkMessage,
                        suppressSound, timeFormat, separator, intervalMs, customLines, hideBackground);
                    _vroCtrl.UpdateToolStates();

                    // Persist chatbox settings
                    _core.Settings.CbShowTime = showTime;
                    _core.Settings.CbShowMedia = showMedia;
                    _core.Settings.CbShowPlaytime = showPlaytime;
                    _core.Settings.CbShowCustomText = showCustomText;
                    _core.Settings.CbShowSystemStats = showSystemStats;
                    _core.Settings.CbShowAfk = showAfk;
                    _core.Settings.CbAfkMessage = afkMessage;
                    _core.Settings.CbSuppressSound = suppressSound;
                    _core.Settings.CbTimeFormat = timeFormat;
                    _core.Settings.CbSeparator = separator;
                    _core.Settings.CbIntervalMs = intervalMs;
                    _core.Settings.CbCustomLines = customLines;
                    _core.Settings.CbHideBackground = hideBackground;
                    _core.Settings.Save();
                    if (_core.Settings.LastSaveError != null)
                        _core.SendToJS("toast", new { ok = false, msg = "Failed to save this setting, please report this error" });
                    else
                        _core.SendToJS("toast", new { ok = true, msg = "Saved" });
                }
                break;

            case "chatboxStop":
                _chatbox?.Stop();
                _chatbox = null;
                _vroCtrl.UpdateToolStates();
                break;

            case "chatboxDirectSend":
                {
                    var text = msg["text"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(text)) break;
                    if (text.Length > 144) text = text[..144];
                    if (_chatbox != null)
                    {
                        _chatbox.SendDirect(text);
                    }
                    else
                    {
                        var svc = new ChatboxService(_ => { });
                        svc.SendDirect(text);
                    }
                }
                break;

            case "oscConnect":
                {
                    _osc ??= new OscService(s => Invoke(() => _core.SendToJS("log", new { msg = s, color = "sec" })));
                    _osc.SetParamCallback((name, val, type) => {
                        try { Invoke(() => _core.SendToJS("oscParam", new { name, value = val, type })); } catch (Exception ex) { CrashHandler.WriteEntry("Osc.SetParamCallback", ex); }
                    });
                    _osc.SetAvatarChangeCallback((avatarId, paramDefs) => {
                        try
                        {
                            var paramList = paramDefs.Select(p => new { p.Name, p.Type, p.HasInput, p.HasOutput }).ToList();
                            Invoke(() => _core.SendToJS("oscAvatarParams", new { avatarId, paramList }));
                        }
                        catch (Exception ex) { CrashHandler.WriteEntry("Osc.SetAvatarChangeCallback", ex); }
                    });
                    bool oscOk = _osc.Start();
                    _core.SendToJS("oscState", new { connected = oscOk });
                    if (oscOk)
                    {
                        _ = Task.Run(async () =>
                        {
                            // Try OSCQuery first; gets all live values instantly (VRChat v2023.3.1+)
                            bool gotLive = await _osc.TryOscQueryAsync((name, val, type) =>
                            {
                                try { Invoke(() => _core.SendToJS("oscParam", new { name, value = val, type })); } catch (Exception ex) { CrashHandler.WriteEntry("Osc.TryOscQueryAsync", ex); }
                            });
                            // Fallback: load config file as pending params so the full list is visible
                            if (!gotLive)
                            {
                                var (avatarId, paramDefs) = _osc.LoadMostRecentAvatarConfig();
                                if (paramDefs.Count > 0)
                                {
                                    var paramList = paramDefs.Select(p => new { p.Name, p.Type, p.HasInput, p.HasOutput }).ToList();
                                    Invoke(() => _core.SendToJS("oscAvatarParams", new { avatarId, paramList }));
                                }
                            }
                        });
                    }
                }
                break;

            case "oscDisconnect":
                _osc?.Stop();
                _core.SendToJS("oscState", new { connected = false });
                break;

            case "oscSend":
                {
                    var pName = msg["name"]?.ToString() ?? "";
                    var pType = msg["type"]?.ToString() ?? "";
                    if (_osc?.IsConnected != true)
                    {
                        _core.SendToJS("log", new { msg = $"[OSC] Send skipped — not connected (osc={_osc != null}, running={_osc?.IsConnected})", color = "err" });
                    }
                    else if (!string.IsNullOrEmpty(pName))
                    {
                        if (pType == "bool") _osc.SendBool(pName, msg["value"]?.Value<bool>() ?? false);
                        else if (pType == "float") _osc.SendFloat(pName, msg["value"]?.Value<float>() ?? 0f);
                        else if (pType == "int") _osc.SendInt(pName, msg["value"]?.Value<int>() ?? 0);
                    }
                }
                break;

            case "oscSendRaw":
                {
                    if (_osc?.IsConnected == true)
                    {
                        var address = msg["address"]?.ToString() ?? "";
                        var pType   = msg["type"]?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(address))
                        {
                            if (pType == "float") _osc.SendRawFloat(address, msg["value"]?.Value<float>() ?? 0f);
                            else if (pType == "bool") _osc.SendRawBool(address, msg["value"]?.Value<bool>() ?? false);
                        }
                    }
                }
                break;

            case "oscEnableOutputs":
                {
                    int filesUpdated = _osc != null ? _osc.EnableAllOutputs()
                        : new OscService(s => { }).EnableAllOutputs();
                    _core.SendToJS("oscOutputsEnabled", new { filesUpdated });
                }
                break;
        }
    }

    // Toggle (called from VR overlay)

    public void Toggle()
    {
        if (_chatbox != null)
        {
            _chatbox.Stop();
            _chatbox = null;
            _core.SendToJS("chatboxUpdate", new { enabled = false });
        }
        else
        {
            _chatbox = new ChatboxService(s => Invoke(() => _core.SendToJS("log", new { msg = s, color = "sec" })));
            _chatbox.SetUpdateCallback(data => { try { Invoke(() => _core.SendToJS("chatboxUpdate", data)); } catch (Exception ex) { CrashHandler.WriteEntry("Chatbox.SetUpdateCallback", ex); } });
            _chatbox.ApplyConfig(true, _core.Settings.CbShowTime, _core.Settings.CbShowMedia, _core.Settings.CbShowPlaytime,
                _core.Settings.CbShowCustomText, _core.Settings.CbShowSystemStats, _core.Settings.CbShowAfk, _core.Settings.CbAfkMessage,
                _core.Settings.CbSuppressSound, _core.Settings.CbTimeFormat, _core.Settings.CbSeparator, _core.Settings.CbIntervalMs, _core.Settings.CbCustomLines, _core.Settings.CbHideBackground);
            _core.SendToJS("chatboxUpdate", new { enabled = true });
        }
    }

    // Disposal

    public void Dispose()
    {
        _chatbox?.Dispose();
        _chatbox = null;
        _osc?.Dispose();
        _osc = null;
    }

    // Photino compatibility shim
    private static void Invoke(Action action) => action();
}
