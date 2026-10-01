using NativeFileDialogSharp;
using Newtonsoft.Json.Linq;
using VRCNext.Services;
using VRCNext.Services.Helpers;

namespace VRCNext;

// Owns all Voice Fight state, logic, and message handling.

public class VoiceFightController : IDisposable
{
    private readonly CoreLibrary _core;
    private readonly VROverlayController _vroCtrl;

    // Fields (moved from MainForm.Fields.cs)
    private VoiceFightService? _voiceFight;
    private VoiceFightSettings _vfSettings;

    // Public Accessors (for other domains)
    public bool IsRunning => _voiceFight?.IsRunning ?? false;
    public float MeterLevel => _voiceFight?.MeterLevel ?? 0f;

    public VoiceFightController(CoreLibrary core, VROverlayController vroCtrl)
    {
        _core = core;
        _vroCtrl = vroCtrl;
        _vfSettings = VoiceFightSettings.Load();
        MigrateDeviceSelections();
    }

    private AudioSelection InputSelection  => AudioSelection.From(_vfSettings.InputDeviceId, _vfSettings.InputDeviceName);
    private AudioSelection OutputSelection => AudioSelection.From(_vfSettings.OutputDeviceId, _vfSettings.OutputDeviceName);

    private void MigrateDeviceSelections()
    {
        var changed = false;
        var inSel = AudioDeviceManager.TryMigrateLegacy(true, InputSelection);
        if (inSel.Mode == AudioSelectionMode.Endpoint && _vfSettings.InputDeviceId.Length == 0)
        {
            _vfSettings.InputDeviceId = inSel.EndpointId;
            _vfSettings.InputDeviceName = inSel.DisplayName;
            changed = true;
        }
        var outSel = AudioDeviceManager.TryMigrateLegacy(false, OutputSelection);
        if (outSel.Mode == AudioSelectionMode.Endpoint && _vfSettings.OutputDeviceId.Length == 0)
        {
            _vfSettings.OutputDeviceId = outSel.EndpointId;
            _vfSettings.OutputDeviceName = outSel.DisplayName;
            changed = true;
        }
        if (changed) _vfSettings.Save();
    }

    private void ApplyDeviceSelection(JObject msg, string idKey, string nameKey, bool input)
    {
        if (input)
        {
            if (AudioDeviceManager.TryReadSelectionFromMessage(msg[idKey], msg[nameKey]?.ToString(), true, _vfSettings.InputDeviceName, out var id, out var name))
            {
                _vfSettings.InputDeviceId = id;
                _vfSettings.InputDeviceName = name;
            }
        }
        else if (AudioDeviceManager.TryReadSelectionFromMessage(msg[idKey], msg[nameKey]?.ToString(), false, _vfSettings.OutputDeviceName, out var id, out var name))
        {
            _vfSettings.OutputDeviceId = id;
            _vfSettings.OutputDeviceName = name;
        }
    }

    private object BuildDevicesPayload()
    {
        var inputs  = AudioDeviceManager.ListInputs();
        var outputs = AudioDeviceManager.ListOutputs();
        return new
        {
            inputs  = inputs.Select(d => new { id = d.Id, name = d.Name }).ToArray(),
            outputs = outputs.Select(d => new { id = d.Id, name = d.Name }).ToArray(),
            input   = SelectionPayload(InputSelection, inputs),
            output  = SelectionPayload(OutputSelection, outputs),
            stopWord = _vfSettings.StopWord,
        };
    }

    private static object SelectionPayload(AudioSelection sel, (string Id, string Name)[] endpoints) => new
    {
        mode = sel.ModeString,
        id = sel.EndpointId,
        name = sel.DisplayName,
        available = AudioDeviceManager.IsAvailable(sel, endpoints),
    };

    // Message Handler

    public void HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "vfGetDevices":
                {
                    _core.SendToJS("vfDevices", BuildDevicesPayload());
                }
                break;

            case "vfGetItems":
                _core.SendToJS("vfItems", VfBuildItemsPayload());
                break;

            case "vfStart":
                {
                    ApplyDeviceSelection(msg, "deviceId", "deviceName", true);
                    ApplyDeviceSelection(msg, "outputDeviceId", "outputDeviceName", false);
                    _vfSettings.Save();

                    var inSel  = InputSelection;
                    var outSel = OutputSelection;
                    var inIdx  = AudioDeviceManager.ResolveInputIndex(inSel);
                    var outIdx = AudioDeviceManager.ResolveOutputIndex(outSel);
                    if (inIdx == null || outIdx == null)
                    {
                        var missing = inIdx == null ? inSel : outSel;
                        var what = inIdx == null ? "Microphone" : "Output device";
                        _core.SendToJS("toast", new { ok = false, msg = $"{what} '{missing.DisplayName}' is not available." });
                        _core.SendToJS("log", new { msg = $"Voice Fight: {what} '{missing.DisplayName}' unavailable, not starting (selection kept).", color = "err" });
                        _core.SendToJS("vfState", new { running = false });
                        break;
                    }

                    _voiceFight?.Dispose();
                    _voiceFight = new VoiceFightService();
                    _voiceFight.OnLog += s => Invoke(() => _core.SendToJS("log", new { msg = s, color = "sec" }));
                    _voiceFight.OnKeywordTriggered += word => Invoke(() => _core.SendToJS("vfKeyword", new { word }));
                    _voiceFight.OnRecognized += (displayHtml, cleanText, isPartial) =>
                        Invoke(() => _core.SendToJS("vfRecognized", new { text = displayHtml, isPartial }));
                    _voiceFight.SetKeywords(_vfSettings.Items);
                    _voiceFight.SetStopWord(_vfSettings.StopWord);
                    _voiceFight.Start(inIdx.Value, outIdx.Value);
                    _core.SendToJS("vfState", new { running = true });
                    _vroCtrl.UpdateToolStates();
                }
                break;

            case "vfStop":
                _voiceFight?.Stop();
                _core.SendToJS("vfState", new { running = false });
                _core.SendToJS("vfMeter", new { level = 0f });
                _vroCtrl.UpdateToolStates();
                break;

            case "vfAddSound":
                {
                    var r = Dialog.FileOpen("wav,mp3,ogg");
                    if (r.IsOk)
                    {
                        var path = r.Path;
                        var duration = VoiceFightService.GetDuration(path);
                        var file = new VoiceFightSettings.VfSoundItem.VfSoundFile { FilePath = path, VolumePercent = 100f };
                        var item = new VoiceFightSettings.VfSoundItem { Word = "", Files = new() { file } };
                        _vfSettings.Items.Add(item);
                        _vfSettings.Save();
                        _voiceFight?.SetKeywords(_vfSettings.Items);
                        int newIdx = _vfSettings.Items.Count - 1;
                        _core.SendToJS("vfItemAdded", new
                        {
                            index = newIdx,
                            word = "",
                            files = new[] { new { soundIndex = 0, filePath = path, fileName = Path.GetFileName(path), durationMs = (int)duration.TotalMilliseconds, volumePercent = 100f } }
                        });
                    }
                }
                break;

            case "vfAddSoundToItem":
                {
                    int itemIdx = msg["itemIndex"]?.Value<int>() ?? -1;
                    if (itemIdx >= 0 && itemIdx < _vfSettings.Items.Count)
                    {
                        var r = Dialog.FileOpen("wav,mp3,ogg");
                        if (r.IsOk)
                        {
                            var path = r.Path;
                            var duration = VoiceFightService.GetDuration(path);
                            var file = new VoiceFightSettings.VfSoundItem.VfSoundFile { FilePath = path, VolumePercent = 100f };
                            _vfSettings.Items[itemIdx].Files.Add(file);
                            _vfSettings.Save();
                            _voiceFight?.SetKeywords(_vfSettings.Items);
                            _core.SendToJS("vfSoundAdded", new
                            {
                                itemIndex = itemIdx,
                                soundIndex = _vfSettings.Items[itemIdx].Files.Count - 1,
                                filePath = path,
                                fileName = Path.GetFileName(path),
                                durationMs = (int)duration.TotalMilliseconds,
                                volumePercent = 100f
                            });
                        }
                    }
                }
                break;

            case "vfDeleteItem":
                {
                    int idx = msg["index"]?.Value<int>() ?? -1;
                    if (idx >= 0 && idx < _vfSettings.Items.Count)
                    {
                        _vfSettings.Items.RemoveAt(idx);
                        _vfSettings.Save();
                        _voiceFight?.SetKeywords(_vfSettings.Items);
                        _core.SendToJS("vfItems", VfBuildItemsPayload());
                    }
                }
                break;

            case "vfDeleteSound":
                {
                    int itemIdx = msg["itemIndex"]?.Value<int>() ?? -1;
                    int soundIdx = msg["soundIndex"]?.Value<int>() ?? -1;
                    if (itemIdx >= 0 && itemIdx < _vfSettings.Items.Count)
                    {
                        var item = _vfSettings.Items[itemIdx];
                        if (soundIdx >= 0 && soundIdx < item.Files.Count)
                        {
                            item.Files.RemoveAt(soundIdx);
                            _vfSettings.Save();
                            _voiceFight?.SetKeywords(_vfSettings.Items);
                            _core.SendToJS("vfItems", VfBuildItemsPayload());
                        }
                    }
                }
                break;

            case "vfPlaySound":
                {
                    int itemIdx = msg["itemIndex"]?.Value<int>() ?? -1;
                    int soundIdx = msg["soundIndex"]?.Value<int>() ?? -1;
                    if (itemIdx >= 0 && itemIdx < _vfSettings.Items.Count)
                    {
                        var item = _vfSettings.Items[itemIdx];
                        if (soundIdx >= 0 && soundIdx < item.Files.Count)
                        {
                            var f = item.Files[soundIdx];
                            _voiceFight?.PlayFile(f.FilePath, f.VolumePercent);
                        }
                    }
                }
                break;

            case "vfSetStopWord":
                {
                    var stopWord = msg["word"]?.ToString() ?? "";
                    _vfSettings.StopWord = stopWord;
                    _vfSettings.Save();
                    _voiceFight?.SetStopWord(stopWord);
                }
                break;

            case "vfStopSound":
                _voiceFight?.StopPlayback();
                break;

            case "vfGetBlockList":
                {
                    var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCNext", "block.txt");
                    if (!System.IO.File.Exists(path))
                    {
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                        System.IO.File.WriteAllLines(path, new[]
                        {
                            "# Words listed here are stripped from VOSK recognition results before keyword matching.",
                            "# One word or phrase per line. Lines starting with # are comments.",
                            "huh", "heh", "hah"
                        });
                    }
                    var words = new List<string>();
                    foreach (var raw in System.IO.File.ReadAllLines(path))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith('#')) continue;
                        words.Add(line);
                    }
                    _core.SendToJS("vfBlockList", new { words });
                }
                break;

            case "vfSetBlockList":
                {
                    var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCNext", "block.txt");
                    var words = msg["words"]?.ToObject<List<string>>() ?? new List<string>();
                    var lines = new List<string>
                    {
                        "# Words listed here are stripped from VOSK recognition results before keyword matching.",
                        "# One word or phrase per line. Lines starting with # are comments."
                    };
                    lines.AddRange(words.Select(w => w.Trim().ToLowerInvariant()).Where(w => w.Length > 0).Distinct());
                    System.IO.File.WriteAllLines(path, lines);
                    _voiceFight?.ReloadBlockList();
                }
                break;

            case "vfSetWord":
                {
                    int idx = msg["index"]?.Value<int>() ?? -1;
                    var word = msg["word"]?.ToString() ?? "";
                    if (idx >= 0 && idx < _vfSettings.Items.Count)
                    {
                        _vfSettings.Items[idx].Word = word;
                        _vfSettings.Save();
                        _voiceFight?.SetKeywords(_vfSettings.Items);
                    }
                }
                break;

            case "vfSetVolume":
                {
                    int itemIdx = msg["itemIndex"]?.Value<int>() ?? -1;
                    int soundIdx = msg["soundIndex"]?.Value<int>() ?? -1;
                    float vol = msg["volume"]?.Value<float>() ?? 100f;
                    if (itemIdx >= 0 && itemIdx < _vfSettings.Items.Count)
                    {
                        var item = _vfSettings.Items[itemIdx];
                        if (soundIdx >= 0 && soundIdx < item.Files.Count)
                        {
                            item.Files[soundIdx].VolumePercent = vol;
                            _vfSettings.Save();
                        }
                    }
                }
                break;

            case "vfSetInputDevice":
            case "vfSetOutputDevice":
                {
                    ApplyDeviceSelection(msg, "deviceId", "deviceName", action == "vfSetInputDevice");
                    _vfSettings.Save();
                    _core.SendToJS("toast", new { ok = true, msg = "Saved" });
                    if (_voiceFight?.IsRunning == true)
                    {
                        _voiceFight.Stop();
                        var inIdx  = AudioDeviceManager.ResolveInputIndex(InputSelection);
                        var outIdx = AudioDeviceManager.ResolveOutputIndex(OutputSelection);
                        if (inIdx != null && outIdx != null)
                        {
                            _voiceFight.Start(inIdx.Value, outIdx.Value);
                        }
                        else
                        {
                            _core.SendToJS("log", new { msg = "Voice Fight: selected device unavailable, stopped (selection kept).", color = "err" });
                            _core.SendToJS("vfState", new { running = false });
                            _vroCtrl.UpdateToolStates();
                        }
                    }
                }
                break;
        }
    }

    // Toggle (called from VR overlay)

    public void Toggle()
    {
        if (_voiceFight != null)
        {
            _voiceFight.Stop();
            _voiceFight = null;
            _core.SendToJS("vfState", new { running = false });
            _core.SendToJS("vfMeter", new { level = 0f });
        }
        else
        {
            _voiceFight = new VoiceFightService();
            _voiceFight.OnLog += s => Invoke(() => _core.SendToJS("log", new { msg = s, color = "sec" }));
            _voiceFight.OnKeywordTriggered += word => Invoke(() => _core.SendToJS("vfKeyword", new { word }));
            _voiceFight.OnRecognized += (displayHtml, cleanText, isPartial) =>
                Invoke(() => _core.SendToJS("vfRecognized", new { text = displayHtml, isPartial }));
            var togIn  = AudioDeviceManager.ResolveInputIndex(InputSelection);
            var togOut = AudioDeviceManager.ResolveOutputIndex(OutputSelection);
            if (togIn == null || togOut == null)
            {
                _core.SendToJS("log", new { msg = "Voice Fight: selected device unavailable, not starting (selection kept).", color = "err" });
                _core.SendToJS("vfState", new { running = false });
                return;
            }
            _voiceFight.SetKeywords(_vfSettings.Items);
            _voiceFight.SetStopWord(_vfSettings.StopWord);
            _voiceFight.Start(togIn.Value, togOut.Value);
            _core.SendToJS("vfState", new { running = true });
        }
    }

    // Voice Fight helpers (moved from MainForm.Relay.cs)

    private object VfBuildItemsPayload() =>
        _vfSettings.Items.Select((item, i) => new
        {
            index = i,
            word = item.Word,
            files = item.Files.Select((f, si) => new
            {
                soundIndex = si,
                filePath = f.FilePath,
                fileName = Path.GetFileName(f.FilePath),
                durationMs = (int)VoiceFightService.GetDuration(f.FilePath).TotalMilliseconds,
                volumePercent = f.VolumePercent
            }).ToList()
        }).ToList();

    // Disposal

    public void Dispose()
    {
        _voiceFight?.Dispose();
        _voiceFight = null;
    }

    // Photino compatibility shim
    private static void Invoke(Action action) => action();
}
