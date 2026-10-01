using Newtonsoft.Json.Linq;
using VRCNext.Services;
using VRCNext.Services.Helpers;
using VRCNext.Services.KikitanXD;

namespace VRCNext;

public class KikitanXDController : IDisposable
{
    private readonly CoreLibrary _core;
    private IKikitanSpeechService? _service;
    private KikitanXDSettings _settings;
    private readonly LocalAiManager _localAi = new();

    public bool IsRunning => _service?.IsRunning ?? false;
    public float MeterLevel => _service?.MeterLevel ?? 0f;

    public KikitanXDController(CoreLibrary core)
    {
        _core = core;
        _settings = KikitanXDSettings.Load();
        MigrateDeviceSelections();

        _localAi.OnProgress += (id, pct, done, total) =>
            Invoke(() => _core.SendToJS("kxdLocalProgress", new { id, pct, done, total }));
        _localAi.OnFinished += (id, ok, error) =>
            Invoke(() =>
            {
                _core.SendToJS("kxdLocalFinished", new { id, ok, error });
                _core.SendToJS("kxdLocalState", _localAi.GetState());
            });
        _localAi.OnLog += s =>
            Invoke(() => _core.SendToJS("log", new { msg = s, color = "sec" }));
    }

    private AudioSelection InputSelection => AudioSelection.From(_settings.InputDeviceId, _settings.InputDeviceName);
    private AudioSelection TtsSelection   => AudioSelection.From(_settings.TtsDeviceId, _settings.TtsDeviceName);

    private void MigrateDeviceSelections()
    {
        var changed = false;
        var inSel = AudioDeviceManager.TryMigrateLegacy(true, InputSelection);
        if (inSel.Mode == AudioSelectionMode.Endpoint && _settings.InputDeviceId.Length == 0)
        {
            _settings.InputDeviceId = inSel.EndpointId;
            _settings.InputDeviceName = inSel.DisplayName;
            changed = true;
        }
        var ttsSel = AudioDeviceManager.TryMigrateLegacy(false, TtsSelection);
        if (ttsSel.Mode == AudioSelectionMode.Endpoint && _settings.TtsDeviceId.Length == 0)
        {
            _settings.TtsDeviceId = ttsSel.EndpointId;
            _settings.TtsDeviceName = ttsSel.DisplayName;
            changed = true;
        }
        if (changed) _settings.Save();
    }

    private static void ApplyDeviceSelection(JObject msg, string idKey, string nameKey, bool input, Action<string, string> store, string currentName)
    {
        if (AudioDeviceManager.TryReadSelectionFromMessage(msg[idKey], msg[nameKey]?.ToString(), input, currentName, out var id, out var name))
            store(id, name);
    }

    public void HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "kxdGetDevices":
            {
                var inputSel = InputSelection;
                var ttsSel   = TtsSelection;
                var inputs   = AudioDeviceManager.ListInputs();
                var outputs  = AudioDeviceManager.ListOutputs();
                _core.SendToJS("kxdDevices", new
                {
                    devices = inputs.Select(d => new { id = d.Id, name = d.Name }).ToArray(),
                    input = new { mode = inputSel.ModeString, id = inputSel.EndpointId, name = inputSel.DisplayName, available = AudioDeviceManager.IsAvailable(inputSel, inputs) },
                    apiKey = _settings.ApiKey,
                    sourceLang = _settings.SourceLang,
                    targetLang = _settings.TargetLang,
                    translateEnabled = _settings.TranslateEnabled,
                    oscEnabled = _settings.OscEnabled,
                    partialOsc = _settings.PartialOsc,
                    noiseGatePct = _settings.NoiseGatePercent,
                    disableNonSpeech = _settings.DisableNonSpeech,
                    chatboxNotify = _settings.ChatboxNotify,
                    profileTranslationEnabled = OperatingSystem.IsWindows() && _settings.ProfileTranslationEnabled,
                    profileTargetLang = _settings.ProfileTargetLang,
                    personality = _settings.Personality,
                    blockWords = _settings.BlockedWords,
                    blockSentences = _settings.BlockedSentences,
                    model = _settings.Model,
                    googleApiKey = _settings.GoogleApiKey,
                    localSttModel = _settings.LocalSttModel,
                    localLlmModel = _settings.LocalLlmModel,
                    localVadEnabled = _settings.LocalVadEnabled,
                    localUseGpu = _settings.LocalUseGpu,
                    ttsEnabled = _settings.TtsEnabled,
                    tts = new { mode = ttsSel.ModeString, id = ttsSel.EndpointId, name = ttsSel.DisplayName, available = AudioDeviceManager.IsAvailable(ttsSel, outputs) },
                    ttsVoice = _settings.TtsVoice,
                    ttsEngine = _settings.TtsEngine,
                    ttsRate = _settings.TtsRate,
                    ttsDevices = outputs.Select(d => new { id = d.Id, name = d.Name }).ToArray(),
                    ttsVoices = VRCNext.Services.Helpers.TtsService.GetSapiVoices()
                });
                break;
            }

            case "kxdStart":
            {
                ApplyDeviceSelection(msg, "deviceId", "deviceName", true, (id, name) => { _settings.InputDeviceId = id; _settings.InputDeviceName = name; }, _settings.InputDeviceName);
                if (msg["apiKey"] is JToken ak0) _settings.ApiKey = ak0.ToString();
                if (msg["googleApiKey"] is JToken gk0) _settings.GoogleApiKey = gk0.ToString();
                if (msg["sourceLang"] is JToken sl0) _settings.SourceLang = sl0.ToString();
                if (msg["targetLang"] is JToken tl0) _settings.TargetLang = tl0.ToString();
                if (msg["translateEnabled"] is JToken te0) _settings.TranslateEnabled = te0.Value<bool>();
                if (msg["oscEnabled"] is JToken oe0) _settings.OscEnabled = oe0.Value<bool>();
                if (msg["partialOsc"] is JToken po0) _settings.PartialOsc = po0.Value<bool>();
                if (msg["noiseGatePct"]?.Value<int?>() is int ng0) _settings.NoiseGatePercent = ng0;
                if (msg["disableNonSpeech"] is JToken dns0) _settings.DisableNonSpeech = dns0.Value<bool>();
                if (msg["chatboxNotify"] is JToken cn0) _settings.ChatboxNotify = cn0.Value<bool>();
                if (msg["personality"] is JToken pe0) _settings.Personality = pe0.ToString();
                if (msg["model"] is JToken md0) _settings.Model = md0.ToString();
                if (msg["blockWords"] is JToken bw0) _settings.BlockedWords = bw0.ToObject<List<string>>() ?? new();
                if (msg["blockSentences"] is JToken bs0) _settings.BlockedSentences = bs0.ToObject<List<string>>() ?? new();
                _settings.Save();

                try
                {
                    StartServiceFromSettings();
                }
                catch (Exception ex)
                {
                    _service?.Dispose();
                    _service = null;
                    _core.SendToJS("kxdState", new { running = false });
                    _core.SendToJS("toast", new { ok = false, msg = ex.Message });
                    _core.SendToJS("log", new { msg = $"Kikitan XD: {ex.Message}", color = "err" });
                }
                break;
            }

            case "kxdStop":
                _service?.Stop();
                _core.SendToJS("kxdState", new { running = false });
                _core.SendToJS("kxdMeter", new { level = 0f });
                break;

            case "kxdSaveSettings":
            {
                var prevModel  = _settings.Model;
                var prevDevice = _settings.InputDeviceId + "|" + _settings.InputDeviceName;

                ApplyDeviceSelection(msg, "deviceId", "deviceName", true, (id, name) => { _settings.InputDeviceId = id; _settings.InputDeviceName = name; }, _settings.InputDeviceName);
                if (msg["apiKey"] is JToken ak) _settings.ApiKey = ak.ToString();
                if (msg["googleApiKey"] is JToken gk) _settings.GoogleApiKey = gk.ToString();
                if (msg["model"] is JToken md) _settings.Model = md.ToString();
                if (msg["sourceLang"] is JToken sl) _settings.SourceLang = sl.ToString();
                if (msg["targetLang"] is JToken tl) _settings.TargetLang = tl.ToString();
                if (msg["translateEnabled"] is JToken te) _settings.TranslateEnabled = te.Value<bool>();
                if (msg["oscEnabled"] is JToken oe) _settings.OscEnabled = oe.Value<bool>();
                if (msg["partialOsc"] is JToken po) _settings.PartialOsc = po.Value<bool>();
                if (msg["noiseGatePct"]?.Value<int?>() is int ng) _settings.NoiseGatePercent = ng;
                if (msg["disableNonSpeech"] is JToken dns) _settings.DisableNonSpeech = dns.Value<bool>();
                if (msg["chatboxNotify"] is JToken cn) _settings.ChatboxNotify = cn.Value<bool>();
                if (msg["profileTranslationEnabled"] is JToken pte) _settings.ProfileTranslationEnabled = pte.Value<bool>();
                if (msg["profileTargetLang"] is JToken ptl) _settings.ProfileTargetLang = ptl.ToString();
                if (msg["personality"] is JToken pers) _settings.Personality = pers.ToString();
                if (msg["blockWords"] is JToken bw) _settings.BlockedWords = bw.ToObject<List<string>>() ?? new();
                if (msg["blockSentences"] is JToken bs) _settings.BlockedSentences = bs.ToObject<List<string>>() ?? new();
                if (msg["ttsEnabled"] is JToken tts) _settings.TtsEnabled = tts.Value<bool>();
                ApplyDeviceSelection(msg, "ttsDeviceId", "ttsDeviceName", false, (id, name) => { _settings.TtsDeviceId = id; _settings.TtsDeviceName = name; }, _settings.TtsDeviceName);
                if (msg["ttsVoice"] is JToken ttv) _settings.TtsVoice = ttv.ToString();
                if (msg["ttsEngine"] is JToken tte) _settings.TtsEngine = tte.ToString();
                if (msg["ttsRate"]?.Value<int?>() is int ttr) _settings.TtsRate = Math.Clamp(ttr, -10, 10);
                _settings.Save();
                _core.SendToJS("toast", new { ok = true, msg = "Saved" });

                bool backendChanged = !string.Equals(prevModel, _settings.Model, StringComparison.OrdinalIgnoreCase);
                bool deviceChanged  = prevDevice != _settings.InputDeviceId + "|" + _settings.InputDeviceName;

                if (IsRunning && (backendChanged || deviceChanged))
                    RestartService(backendChanged ? "model" : "input device");
                else
                    _service?.UpdateSettings(_settings);
                break;
            }

            case "kxdLocalGetState":
                _localAi.CleanObsolete();
                _core.SendToJS("kxdLocalState", _localAi.GetState());
                break;

            case "kxdLocalDownload":
            {
                var id = msg["id"]?.ToString() ?? "";
                if (id.Length == 0 || _localAi.IsBusy(id)) break;
                _ = Task.Run(() => _localAi.DownloadAsync(id));
                _core.SendToJS("kxdLocalState", _localAi.GetState());
                break;
            }

            case "kxdLocalCancel":
                _localAi.Cancel(msg["id"]?.ToString() ?? "");
                break;

            case "kxdLocalUninstall":
            {
                var id = msg["id"]?.ToString() ?? "";
                var ok = _localAi.Uninstall(id);
                _core.SendToJS("kxdLocalState", _localAi.GetState());
                if (!ok) _core.SendToJS("toast", new { ok = false, msg = "Could not remove, is it in use?" });
                break;
            }

            case "kxdLocalSaveSelection":
            {
                if (msg["sttModel"] is JToken sm) _settings.LocalSttModel = sm.ToString();
                if (msg["llmModel"] is JToken lm) _settings.LocalLlmModel = lm.ToString();
                if (msg["vadEnabled"] is JToken ve) _settings.LocalVadEnabled = ve.Value<bool>();
                if (msg["useGpu"] is JToken ug) _settings.LocalUseGpu = ug.Value<bool>();
                _settings.Save();
                _service?.UpdateSettings(_settings);
                break;
            }

            case "kxdTranslateProfileText":
            {
                string text = msg["text"]?.ToString() ?? "";
                string reqId = msg["reqId"]?.ToString() ?? "";
                string targetLang = !string.IsNullOrEmpty(msg["targetLang"]?.ToString())
                    ? msg["targetLang"]!.ToString()
                    : _settings.ProfileTargetLang;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var translated = await KikitanXDService.TranslateStandaloneAsync(
                            _settings.ApiKey, text, "auto", targetLang);
                        _core.SendToJS("kxdProfileTranslated", new { reqId, text = translated, ok = !string.IsNullOrWhiteSpace(translated) });
                    }
                    catch (Exception ex)
                    {
                        _core.SendToJS("kxdProfileTranslated", new { reqId, text = "", ok = false, error = ex.Message });
                    }
                });
                break;
            }
        }
    }

    public void Toggle()
    {
        if (IsRunning)
        {
            _service?.Stop();
            _core.SendToJS("kxdState", new { running = false });
            _core.SendToJS("kxdMeter", new { level = 0f });
        }
        else
        {
            try
            {
                StartServiceFromSettings();
            }
            catch (Exception ex)
            {
                _service?.Dispose();
                _service = null;
                _core.SendToJS("kxdState", new { running = false });
                _core.SendToJS("log", new { msg = $"Kikitan XD: {ex.Message}", color = "err" });
            }
        }
    }

    private void RestartService(string reason)
    {
        try
        {
            StartServiceFromSettings();
            _core.SendToJS("log", new { msg = $"Kikitan XD: restarted to apply the new {reason}", color = "sec" });
        }
        catch (Exception ex)
        {
            _service?.Dispose();
            _service = null;
            _core.SendToJS("kxdState", new { running = false });
            _core.SendToJS("kxdMeter", new { level = 0f });
            _core.SendToJS("toast", new { ok = false, msg = ex.Message });
            _core.SendToJS("log", new { msg = $"Kikitan XD: {ex.Message}", color = "err" });
        }
    }

    private void StartServiceFromSettings()
    {
        _service?.Dispose();
        _service = _settings.Model?.ToLowerInvariant() switch
        {
            "google" => new GeminiLiveService(),
            "local"  => new LocalKikitanService(),
            _        => new KikitanXDService(),
        };
        _service.OnLog += s => Invoke(() => _core.SendToJS("log", new { msg = s, color = "sec" }));
        _service.OnRecognized += (text, isPartial) =>
        {
            _kxLastSource = text ?? "";
            _kxLastFinal  = !isPartial;
            PushKikitanToOverlay();
            Invoke(() => _core.SendToJS("kxdRecognized", new { text, isPartial }));
        };
        _service.OnTranslated += text =>
        {
            _kxLastTranslation = text ?? "";
            PushKikitanToOverlay();
            Invoke(() => _core.SendToJS("kxdTranslated", new { text }));
        };
        _service.OnOutput += SpeakTts;
        _service.OnChatboxSent += () => _core.OnChatboxPauseRequest?.Invoke(15_000);
        var micSel = InputSelection;
        var micIdx = AudioDeviceManager.ResolveInputIndex(micSel);
        if (micIdx == null)
            throw new Exception(micSel.Mode == AudioSelectionMode.UnresolvedLegacy
                ? $"Microphone '{micSel.DisplayName}' could not be identified after the update, please re-select it."
                : $"Microphone '{micSel.DisplayName}' is not connected.");
        _service.Start(micIdx.Value, _settings);
        _kxLastSource = "";
        _kxLastTranslation = "";
        _kxLastFinal = true;
        PushKikitanToOverlay();
        _core.SendToJS("kxdState", new { running = true });
    }

    private string _kxLastSource      = "";
    private string _kxLastTranslation = "";
    private bool   _kxLastFinal       = true;

    private static readonly Dictionary<string, string> KxLangNames = new()
    {
        ["auto"] = "Auto", ["en"] = "English",  ["ja"] = "Japanese", ["zh"] = "Chinese",
        ["ko"]   = "Korean", ["de"] = "German", ["fr"] = "French",   ["es"] = "Spanish",
        ["pt"]   = "Portuguese", ["ru"] = "Russian", ["it"] = "Italian", ["nl"] = "Dutch",
        ["pl"]   = "Polish", ["tr"] = "Turkish", ["ar"] = "Arabic",  ["hi"] = "Hindi",
    };

    private static string KxLangName(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "Auto";
        return KxLangNames.TryGetValue(code.ToLowerInvariant(), out var n) ? n : code.ToUpperInvariant();
    }

    private void PushKikitanToOverlay()
    {
#if WINDOWS
        try
        {
            _core.VrOverlay?.SetKikitanState(
                _kxLastSource,
                _kxLastTranslation,
                _kxLastFinal,
                KxLangName(_settings.SourceLang),
                KxLangName(_settings.TargetLang),
                string.Equals(_settings.Model, "google", StringComparison.OrdinalIgnoreCase) ? "Gemini Live" : "Groq",
                _settings.TranslateEnabled);
        }
        catch { }
#endif
    }

    public void Dispose()
    {
        _service?.Dispose();
        _service = null;
        _kxLastSource = "";
        _kxLastTranslation = "";
        PushKikitanToOverlay();
    }

    private void SpeakTts(string text)
    {
        if (!_settings.TtsEnabled || string.IsNullOrWhiteSpace(text)) return;
        VRCNext.Services.Helpers.TtsService.Speak(
            text, _settings.TtsEngine, _settings.TtsVoice,
            TtsSelection, 100, _settings.TtsRate);
    }

    private static void Invoke(Action action) => action();
}
