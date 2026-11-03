using Newtonsoft.Json.Linq;
using System.Diagnostics;
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
    private int _windowToolsInstallRunning;

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
            case "chatboxWindowToolsCheck":
                SendWindowToolsState();
                break;

            case "chatboxWindowToolsInstall":
                if (Interlocked.CompareExchange(ref _windowToolsInstallRunning, 1, 0) == 0)
                    _ = InstallWindowToolsAsync();
                break;

            case "chatboxToggle":
                Toggle();
                break;

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
                    var afkMouseSeconds = msg["afkMouseSeconds"]?.Value<int>() ?? 10;
                    var afkKeyboardSeconds = msg["afkKeyboardSeconds"]?.Value<int>() ?? 10;
                    var afkMessage = msg["afkMessage"]?.ToString() ?? "Currently AFK";
                    var suppressSound = msg["suppressSound"]?.Value<bool>() ?? true;
                    var timeFormat = msg["timeFormat"]?.ToString() ?? "hh:mm tt";
                    var separator = msg["separator"]?.ToString() ?? " | ";
                    var intervalMs = msg["intervalMs"]?.Value<int>() ?? 5000;
                    var customLines = msg["customLines"]?.ToObject<List<CbCustomLine>>() ?? new();
                    var hideBackground = msg["hideBackground"]?.Value<bool>() ?? false;
                    var customTemplate = msg["customTemplate"]?.ToString() ?? "";
                    var showAfkTime = msg["showAfkTime"]?.Value<bool>() ?? true;
                    var lineOrder = msg["lineOrder"]?.ToObject<List<string>>() ?? new(ChatboxService.DefaultLineOrder);
                    var statCpu = msg["statCpu"]?.Value<bool>() ?? true;
                    var statRam = msg["statRam"]?.Value<bool>() ?? true;
                    var statGpu = msg["statGpu"]?.Value<bool>() ?? false;
                    var statVram = msg["statVram"]?.Value<bool>() ?? false;
                    var showPulse = msg["showPulse"]?.Value<bool>() ?? false;
                    var pulseFormat = msg["pulseFormat"]?.ToString() ?? "\u2665 {bpm} BPM";
                    var hypeRateId = msg["hypeRateId"]?.ToString() ?? "";
                    var afHeartRate = msg["afHeartRate"]?.Value<bool>() ?? false;
                    var showWindow = msg["showWindow"]?.Value<bool>() ?? false;
                    var windowFormat = msg["windowFormat"]?.ToString() ?? "";
                    var showWeather = msg["showWeather"]?.Value<bool>() ?? false;
                    var weatherCity = msg["weatherCity"]?.ToString() ?? "";
                    var weatherUnit = msg["weatherUnit"]?.ToString() ?? "celsius";
                    var weatherFormat = msg["weatherFormat"]?.ToString() ?? "";

                    _chatbox.ApplyConfig(enabled, showTime, showMedia, showPlaytime,
                        showCustomText, showSystemStats, showAfk, afkMessage,
                        suppressSound, timeFormat, separator, intervalMs, customLines, hideBackground,
                        customTemplate: customTemplate,
                        lineOrder: lineOrder, showAfkTime: showAfkTime,
                        statCpu: statCpu, statRam: statRam, statGpu: statGpu, statVram: statVram,
                        showPulse: showPulse, pulseFormat: pulseFormat,
                        showWindow: showWindow, windowFormat: windowFormat,
                        showWeather: showWeather, weatherFormat: weatherFormat,
                        afkMouseSeconds: afkMouseSeconds, afkKeyboardSeconds: afkKeyboardSeconds);
                    _chatbox.PulseProvider = () => _hypeRate != null && _hypeRate.HasFreshData ? _hypeRate.CurrentBpm : 0;
                    _chatbox.WeatherProvider = CurrentWeather;
                    ApplyHypeRate((showPulse && enabled) || afHeartRate, hypeRateId);
                    ApplyWeather(showWeather && enabled, weatherCity, weatherUnit);
                    _vroCtrl.UpdateToolStates();

                    // Persist chatbox settings
                    _core.Settings.CbShowTime = showTime;
                    _core.Settings.CbShowMedia = showMedia;
                    _core.Settings.CbShowPlaytime = showPlaytime;
                    _core.Settings.CbShowCustomText = showCustomText;
                    _core.Settings.CbShowSystemStats = showSystemStats;
                    _core.Settings.CbShowAfk = showAfk;
                    _core.Settings.CbAfkMouseSeconds = afkMouseSeconds;
                    _core.Settings.CbAfkKeyboardSeconds = afkKeyboardSeconds;
                    _core.Settings.CbAfkMessage = afkMessage;
                    _core.Settings.CbSuppressSound = suppressSound;
                    _core.Settings.CbTimeFormat = timeFormat;
                    _core.Settings.CbSeparator = separator;
                    _core.Settings.CbCustomTemplate = customTemplate;
                    _core.Settings.CbIntervalMs = intervalMs;
                    _core.Settings.CbCustomLines = customLines;
                    _core.Settings.CbHideBackground = hideBackground;
                    _core.Settings.CbShowAfkTime = showAfkTime;
                    _core.Settings.CbLineOrder = lineOrder;
                    _core.Settings.CbStatCpu = statCpu;
                    _core.Settings.CbStatRam = statRam;
                    _core.Settings.CbStatGpu = statGpu;
                    _core.Settings.CbStatVram = statVram;
                    _core.Settings.CbShowPulse = showPulse;
                    _core.Settings.CbPulseFormat = pulseFormat;
                    _core.Settings.CbHypeRateId = hypeRateId;
                    _core.Settings.CbAfHeartRate = afHeartRate;
                    _core.Settings.CbShowWindow = showWindow;
                    _core.Settings.CbWindowFormat = windowFormat;
                    _core.Settings.CbShowWeather = showWeather;
                    _core.Settings.CbWeatherCity = weatherCity;
                    _core.Settings.CbWeatherUnit = weatherUnit;
                    _core.Settings.CbWeatherFormat = weatherFormat;
                    _core.Settings.Save();
                    if (_core.Settings.LastSaveError != null)
                        _core.SendToJS("toast", new { ok = false, msg = "Failed to save this setting, please report this error" });
                    else
                        _core.SendToJS("toast", new { ok = true, msg = "Saved" });
                }
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
                        using var svc = new ChatboxService(s => Invoke(() => _core.SendToJS("log", new { msg = s, color = "sec" })));
                        svc.SendDirect(text);
                    }
                }
                break;

            case "oscConnect":
                {
                    _osc ??= new OscService(s => Invoke(() => _core.SendToJS("log", new { msg = s, color = "sec" })));
                    _osc.SetParamCallback(QueueOscParam);
                    StartOscFlushTimer();
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
                            bool gotLive = await _osc.TryOscQueryAsync(QueueOscParam);
                            if (!gotLive)
                            {
                                var wornId = _core.VrcApi.CurrentAvatarId ?? "";
                                var avatarId = wornId;
                                var paramDefs = string.IsNullOrEmpty(wornId)
                                    ? new List<OscParamDef>()
                                    : _osc.LoadAvatarConfig(wornId);
                                if (paramDefs.Count == 0)
                                    (avatarId, paramDefs) = _osc.LoadMostRecentAvatarConfig();
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
                StopOscFlushTimer();
                _core.SendToJS("oscState", new { connected = false });
                break;

            case "oscSend":
                {
                    var pName = msg["name"]?.ToString() ?? "";
                    var pType = msg["type"]?.ToString() ?? "";
                    _osc ??= new OscService(s => Invoke(() => _core.SendToJS("log", new { msg = s, color = "sec" })));
                    if (!string.IsNullOrEmpty(pName))
                    {
                        if (pType == "bool") _osc.SendBool(pName, msg["value"]?.Value<bool>() ?? false);
                        else if (pType == "float") _osc.SendFloat(pName, msg["value"]?.Value<float>() ?? 0f);
                        else if (pType == "int") _osc.SendInt(pName, msg["value"]?.Value<int>() ?? 0);
                    }
                }
                break;

            case "oscSendRaw":
                {
                    _osc ??= new OscService(s => Invoke(() => _core.SendToJS("log", new { msg = s, color = "sec" })));
                    var address = msg["address"]?.ToString() ?? "";
                    var pType   = msg["type"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(address))
                    {
                        if (pType == "float") _osc.SendRawFloat(address, msg["value"]?.Value<float>() ?? 0f);
                        else if (pType == "bool") _osc.SendRawBool(address, msg["value"]?.Value<bool>() ?? false);
                        else if (pType == "int") _osc.SendRawInt(address, msg["value"]?.Value<int>() ?? 0);
                    }
                }
                break;

            case "hypeRateGetState":
                if (_hypeRate == null && _core.Settings.CbAfHeartRate)
                    ApplyHypeRate(true, _core.Settings.CbHypeRateId);
                else
                    SendHypeRateState();
                break;

            case "weatherGetState":
                SendWeatherState();
                break;

            case "oscSetTabVisible":
                _oscTabVisible = msg["visible"]?.Value<bool>() ?? true;
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

    private const int OscFlushIntervalMs = 100;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (object Value, string Type)> _oscPending = new();
    private System.Threading.Timer? _oscFlushTimer;
    private volatile bool _oscTabVisible = true;

    private void QueueOscParam(string name, object value, string type)
    {
        _oscPending[name] = (value, type);
    }

    private void StartOscFlushTimer()
    {
        if (_oscFlushTimer != null) return;
        _oscFlushTimer = new System.Threading.Timer(_ => FlushOscParams(), null, OscFlushIntervalMs, OscFlushIntervalMs);
    }

    private void StopOscFlushTimer()
    {
        _oscFlushTimer?.Dispose();
        _oscFlushTimer = null;
        _oscPending.Clear();
    }

    private void FlushOscParams()
    {
        if (_oscPending.IsEmpty) return;
        try
        {
            var batch = new List<object>(_oscPending.Count);
            foreach (var key in _oscPending.Keys)
            {
                if (_oscPending.TryRemove(key, out var entry))
                    batch.Add(new { name = key, value = entry.Value, type = entry.Type });
            }
            if (batch.Count == 0) return;
            Invoke(() => _core.SendToJS("oscParams", new { list = batch, render = _oscTabVisible }));
        }
        catch (Exception ex) { CrashHandler.WriteEntry("Osc.FlushOscParams", ex); }
    }

    private WeatherService? _weather;

    private (string icon, string temp)? CurrentWeather()
    {
        if (_weather == null || !_weather.HasData) return null;
        var temp = Math.Round(_weather.Temperature).ToString(System.Globalization.CultureInfo.InvariantCulture) + _weather.UnitSuffix;
        return (WeatherService.CodeToEmoji(_weather.WeatherCode), temp);
    }

    private void ApplyWeather(bool wanted, string city, string unit)
    {
        if (!wanted || string.IsNullOrWhiteSpace(city))
        {
            _weather?.Stop();
            SendWeatherState();
            return;
        }
        if (_weather == null)
        {
            _weather = new WeatherService(s => Invoke(() => _core.SendToJS("log", new { msg = s, color = "sec" })));
            _weather.StateChanged += () => Invoke(SendWeatherState);
        }
        _weather.Start(city, unit);
        SendWeatherState();
    }

    private void SendWeatherState()
    {
        var w = CurrentWeather();
        _core.SendToJS("weatherState", new
        {
            ok    = w != null,
            city  = _weather?.CityLabel ?? "",
            text  = w != null ? w.Value.icon + " " + w.Value.temp : "",
            error = _weather?.LastError ?? "",
        });
    }

    private HypeRateService? _hypeRate;

    private void ApplyHypeRate(bool wanted, string deviceId)
    {
        if (!wanted || string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(BuildSecrets.HypeRateApiKey))
        {
            _hypeRate?.Stop();
            SendHypeRateState();
            return;
        }
        if (_hypeRate == null)
        {
            _hypeRate = new HypeRateService(s => Invoke(() => _core.SendToJS("log", new { msg = s, color = "sec" })));
            _hypeRate.StateChanged += () => Invoke(SendHypeRateState);
        }
        _hypeRate.Start(deviceId, BuildSecrets.HypeRateApiKey);
        SendHypeRateState();
    }

    private void SendHypeRateState()
    {
        _core.SendToJS("hypeRateState", new
        {
            connected = _hypeRate?.IsConnected ?? false,
            bpm       = (_hypeRate?.HasFreshData ?? false) ? _hypeRate!.CurrentBpm : 0,
            error     = _hypeRate?.LastError ?? "",
            available = !string.IsNullOrWhiteSpace(BuildSecrets.HypeRateApiKey),
        });
    }

    private void SendWindowToolsState(string? message = null, bool success = false)
    {
        var missing = GetMissingWindowTools();
        _core.SendToJS("chatboxWindowToolsState", new
        {
            available = missing.Count == 0,
            installing = Volatile.Read(ref _windowToolsInstallRunning) != 0,
            missing,
            message,
            success,
        });
    }

    private static List<string> GetMissingWindowTools()
    {
        var missing = new List<string>();
        if (FindExecutable("xdotool") == null) missing.Add("xdotool");
        if (FindExecutable("xprop") == null) missing.Add("xprop");
        return missing;
    }

    private static string? FindExecutable(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private async Task InstallWindowToolsAsync()
    {
        var manager = GetWindowToolsPackageManager();
        if (manager == null)
        {
            Interlocked.Exchange(ref _windowToolsInstallRunning, 0);
            SendWindowToolsState("Could not detect a supported package manager. Install xdotool and xprop using your distribution's package manager.", false);
            return;
        }

        var pkexec = FindExecutable("pkexec");
        if (pkexec == null)
        {
            Interlocked.Exchange(ref _windowToolsInstallRunning, 0);
            SendWindowToolsState($"Graphical administrator authorization (pkexec) is unavailable. Install them manually with: {manager.ManualCommand}", false);
            return;
        }

        SendWindowToolsState();
        try
        {
            var startInfo = new ProcessStartInfo(pkexec) { UseShellExecute = false };
            startInfo.ArgumentList.Add(manager.Executable);
            foreach (var argument in manager.Arguments) startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException("Could not start the package manager through pkexec.");
            await process.WaitForExitAsync();

            var missing = GetMissingWindowTools();
            if (process.ExitCode == 0 && missing.Count == 0)
            {
                Interlocked.Exchange(ref _windowToolsInstallRunning, 0);
                SendWindowToolsState("Window Activity tools installed.", true);
                return;
            }

            var detail = process.ExitCode == 126 || process.ExitCode == 127
                ? "Authorization was cancelled or failed."
                : $"The package installer exited with code {process.ExitCode}.";
            Interlocked.Exchange(ref _windowToolsInstallRunning, 0);
            SendWindowToolsState($"{detail} Install the tools manually with: {manager.ManualCommand}", false);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _windowToolsInstallRunning, 0);
            SendWindowToolsState($"Could not install Window Activity tools: {ex.Message}. You can install them manually with: {manager.ManualCommand}", false);
        }
    }

    private static WindowToolsPackageManager? GetWindowToolsPackageManager()
    {
        if (FindExecutable("apt-get") is { } apt)
            return new(apt, ["install", "-y", "xdotool", "x11-utils"], "sudo apt-get install xdotool x11-utils");
        if (FindExecutable("dnf") is { } dnf)
            return new(dnf, ["install", "-y", "xdotool", "xorg-x11-utils"], "sudo dnf install xdotool xorg-x11-utils");
        if (FindExecutable("pacman") is { } pacman)
            return new(pacman, ["-S", "--needed", "--noconfirm", "xdotool", "xorg-xprop"], "sudo pacman -S xdotool xorg-xprop");
        if (FindExecutable("zypper") is { } zypper)
            return new(zypper, ["--non-interactive", "install", "xdotool", "xprop"], "sudo zypper install xdotool xprop");
        if (FindExecutable("apk") is { } apk)
            return new(apk, ["add", "xdotool", "xprop"], "sudo apk add xdotool xprop");
        if (FindExecutable("xbps-install") is { } xbps)
            return new(xbps, ["-Sy", "xdotool", "xprop"], "sudo xbps-install -Sy xdotool xprop");
        if (FindExecutable("eopkg") is { } eopkg)
            return new(eopkg, ["install", "-y", "xdotool", "xprop"], "sudo eopkg install xdotool xprop");
        return null;
    }

    private sealed record WindowToolsPackageManager(string Executable, string[] Arguments, string ManualCommand);

    // Toggle (called from VR overlay)

    public void Toggle()
    {
        if (_chatbox?.Enabled == true)
        {
            _chatbox.Stop();
            _chatbox = null;
            ApplyHypeRate(_core.Settings.CbAfHeartRate, _core.Settings.CbHypeRateId);
            ApplyWeather(false, "", "celsius");
            _core.SendToJS("chatboxUpdate", new { enabled = false });
        }
        else
        {
            _chatbox ??= new ChatboxService(s => Invoke(() => _core.SendToJS("log", new { msg = s, color = "sec" })));
            _chatbox.SetUpdateCallback(data => { try { Invoke(() => _core.SendToJS("chatboxUpdate", data)); } catch (Exception ex) { CrashHandler.WriteEntry("Chatbox.SetUpdateCallback", ex); } });
            _chatbox.ApplyConfig(true, _core.Settings.CbShowTime, _core.Settings.CbShowMedia, _core.Settings.CbShowPlaytime,
                _core.Settings.CbShowCustomText, _core.Settings.CbShowSystemStats, _core.Settings.CbShowAfk, _core.Settings.CbAfkMessage,
                _core.Settings.CbSuppressSound, _core.Settings.CbTimeFormat, _core.Settings.CbSeparator, _core.Settings.CbIntervalMs, _core.Settings.CbCustomLines, _core.Settings.CbHideBackground,
                customTemplate: _core.Settings.CbCustomTemplate,
                lineOrder: _core.Settings.CbLineOrder, showAfkTime: _core.Settings.CbShowAfkTime,
                statCpu: _core.Settings.CbStatCpu, statRam: _core.Settings.CbStatRam,
                statGpu: _core.Settings.CbStatGpu, statVram: _core.Settings.CbStatVram,
                showPulse: _core.Settings.CbShowPulse, pulseFormat: _core.Settings.CbPulseFormat,
                showWindow: _core.Settings.CbShowWindow, windowFormat: _core.Settings.CbWindowFormat,
                showWeather: _core.Settings.CbShowWeather, weatherFormat: _core.Settings.CbWeatherFormat,
                afkMouseSeconds: _core.Settings.CbAfkMouseSeconds, afkKeyboardSeconds: _core.Settings.CbAfkKeyboardSeconds);
            _chatbox.PulseProvider = () => _hypeRate != null && _hypeRate.HasFreshData ? _hypeRate.CurrentBpm : 0;
            _chatbox.WeatherProvider = CurrentWeather;
            ApplyHypeRate(_core.Settings.CbShowPulse || _core.Settings.CbAfHeartRate, _core.Settings.CbHypeRateId);
            ApplyWeather(_core.Settings.CbShowWeather, _core.Settings.CbWeatherCity, _core.Settings.CbWeatherUnit);
            _core.SendToJS("chatboxUpdate", new { enabled = true });
        }
    }

    // Disposal

    public void Dispose()
    {
        _hypeRate?.Dispose();
        _weather?.Dispose();
        _chatbox?.Dispose();
        _chatbox = null;
        _osc?.Dispose();
        _osc = null;
    }

    // Photino compatibility shim
    private static void Invoke(Action action) => action();
}
