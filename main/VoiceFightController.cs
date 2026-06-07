using VRCNext.Services.Helpers;
using Newtonsoft.Json.Linq;
using VRCNext.Services;

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
    }

    // Message Handler

    public void HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "vfGetDevices":
                {
                    var devices = VoiceFightService.GetInputDevices();
                    var outputDevices = VoiceFightService.GetOutputDevices();
                    _core.SendToJS("vfDevices", new { devices, savedIndex = _vfSettings.InputDeviceIndex, outputDevices, savedOutputIndex = _vfSettings.OutputDeviceIndex, stopWord = _vfSettings.StopWord });
                }
                break;

            case "vfGetItems":
                _core.SendToJS("vfItems", VfBuildItemsPayload());
                break;

            case "vfStart":
                {
                    int devIdx = msg["deviceIndex"]?.Value<int>() ?? 0;
                    int outIdx = msg["outputDeviceIndex"]?.Value<int>() ?? _vfSettings.OutputDeviceIndex;
                    _vfSettings.InputDeviceIndex = devIdx;
                    _vfSettings.OutputDeviceIndex = outIdx;
                    _vfSettings.Save();

                    _voiceFight?.Dispose();
                    _voiceFight = new VoiceFightService();
                    _voiceFight.OnLog += s => Invoke(() => _core.SendToJS("log", new { msg = s, color = "sec" }));
                    _voiceFight.OnKeywordTriggered += word => Invoke(() => _core.SendToJS("vfKeyword", new { word }));
                    _voiceFight.OnRecognized += (displayHtml, cleanText, isPartial) =>
                        Invoke(() => _core.SendToJS("vfRecognized", new { text = displayHtml, isPartial }));
                    _voiceFight.SetKeywords(_vfSettings.Items);
                    _voiceFight.SetStopWord(_vfSettings.StopWord);
                    _voiceFight.Start(devIdx, outIdx);
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
                    var r = FilePicker.FileOpen("wav,mp3,ogg");
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
                        var r = FilePicker.FileOpen("wav,mp3,ogg");
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
                {
                    int devIdx = msg["deviceIndex"]?.Value<int>() ?? 0;
                    _vfSettings.InputDeviceIndex = devIdx;
                    _vfSettings.Save();
                    _core.SendToJS("toast", new { ok = true, msg = "Saved" });
                    if (_voiceFight?.IsRunning == true)
                    {
                        _voiceFight.Stop();
                        _voiceFight.Start(devIdx, _vfSettings.OutputDeviceIndex);
                    }
                }
                break;

            case "vfSetOutputDevice":
                {
                    int outIdx = msg["deviceIndex"]?.Value<int>() ?? -1;
                    _vfSettings.OutputDeviceIndex = outIdx;
                    _vfSettings.Save();
                    _core.SendToJS("toast", new { ok = true, msg = "Saved" });
                    if (_voiceFight?.IsRunning == true)
                    {
                        _voiceFight.Stop();
                        _voiceFight.Start(_vfSettings.InputDeviceIndex, outIdx);
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
            _voiceFight.SetKeywords(_vfSettings.Items);
            _voiceFight.SetStopWord(_vfSettings.StopWord);
            _voiceFight.Start(_vfSettings.InputDeviceIndex, _vfSettings.OutputDeviceIndex);
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

    private static void VfSendChatbox(string text)
    {
        try
        {
            using var udp = new System.Net.Sockets.UdpClient();
            udp.Connect("127.0.0.1", 9000);
            var buf = new List<byte>();
            VfOscString(buf, "/chatbox/input");
            VfOscString(buf, ",sTF"); // string, sendImmediate=true, notifySound=false
            VfOscString(buf, text.Length > 144 ? text[..144] : text);
            var pkt = buf.ToArray();
            udp.Send(pkt, pkt.Length);
        }
        catch { }
    }

    private static void VfOscString(List<byte> buf, string s)
    {
        var b = System.Text.Encoding.UTF8.GetBytes(s);
        buf.AddRange(b);
        int pad = 4 - (b.Length % 4);
        if (pad == 0) pad = 4;
        buf.AddRange(new byte[pad]);
    }

    // Disposal

    public void Dispose()
    {
        _voiceFight?.Dispose();
        _voiceFight = null;
    }

    // Photino compatibility shim
    private static void Invoke(Action action) => action();
}
