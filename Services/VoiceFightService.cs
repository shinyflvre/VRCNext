using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
#if WINDOWS
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Vosk;
using System.Runtime.InteropServices;
#endif

namespace VRCNext.Services;

#if !WINDOWS
// stub for non-windows platforms, voicefight requires windows audio apis
public sealed class VoiceFightService : IDisposable
{
    public event Action<string>? OnKeywordTriggered;
    public event Action<string, string, bool>? OnRecognized;
    public event Action<string>? OnLog;
    public bool IsRunning => false;
    public float MeterLevel => 0f;
    public void SetKeywords(List<VoiceFightSettings.VfSoundItem> items) { }
    public void SetStopWord(string word) { }
    public void Start(int inputDeviceIndex, int outputDeviceIndex) { }
    public void Stop() { }
    public static List<string> GetInputDevices() => new();
    public static List<string> GetOutputDevices() => new();
    public static TimeSpan GetDuration(string filePath) => TimeSpan.Zero;
    public void PlayFile(string filePath, float volumePercent) { }
    public void StopPlayback() { }
    public void ReloadBlockList() { }
    public void Dispose() { }
}
#else

// voice-triggered soundboard using VOSK speech recognition and NAudio. captures mic, detects trigger words, plays sounds.
public sealed class VoiceFightService : IDisposable
{
    public event Action<string>? OnKeywordTriggered;
    public event Action<string, string, bool>? OnRecognized;
    public event Action<string>? OnLog;

    private static readonly string ModelPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "voice", "vosk-model-small-en-us-0.15");

    private readonly Dictionary<string, VoiceFightSettings.VfSoundItem> _keywordMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _keywordLock = new();
    private static readonly Random _rng = new();

    private const int KeywordCooldownMs = 1500;
    private readonly Dictionary<string, DateTime> _lastTriggered = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastStopTriggered = DateTime.MinValue;

    private string _stopWord = "";
    private readonly object _stopWordLock = new();
    private static readonly string StopSoundPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "voice", "sounds", "stop.wav");

    private static readonly string BlockListPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCNext", "block.txt");
    private static readonly string[] BlockListDefaults = ["huh", "heh", "hah"];
    private HashSet<string> _blockList = new(StringComparer.OrdinalIgnoreCase);

    private WaveIn? _waveIn;
    private volatile float _meterLevel;
    public float MeterLevel => _meterLevel;

    private readonly ConcurrentQueue<byte[]> _pcmQueue = new();
    private readonly AutoResetEvent _workerEvent = new(false);
    private Thread? _workerThread;
    private volatile bool _workerRunning;

    private WaveOut? _waveOut;
    private IDisposable? _currentReader;
    private int _outputDeviceIndex = -1;
    private readonly object _playLock = new();

    private Model? _model;
    private volatile bool _modelLoaded;

    public bool IsRunning => _waveIn != null;
    public bool ModelOk => _modelLoaded;

    public static TimeSpan GetDuration(string path)
    {
        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".ogg") using (var r = new NVorbis.VorbisReader(path)) return r.TotalTime;
            if (ext == ".wav") using (var r = new WaveFileReader(path)) return r.TotalTime;
            using (var r = new MediaFoundationReader(path)) return r.TotalTime;
        }
        catch { return TimeSpan.Zero; }
    }

    public void Start(int deviceIndex, int outputDeviceIndex = -1)
    {
        Stop();

        LoadBlockList();
        EnsureModel();

        _outputDeviceIndex = outputDeviceIndex;
        _waveOut = new WaveOut { DeviceNumber = _outputDeviceIndex };

        _waveIn = new WaveIn
        {
            DeviceNumber = deviceIndex,
            WaveFormat = new WaveFormat(16000, 1),
            BufferMilliseconds = 100
        };
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;

        _workerRunning = true;
        _workerThread = new Thread(WorkerLoop) { IsBackground = true };
        _workerThread.Start();

        _waveIn.StartRecording();
        Log("Voice Fight: listening started");
    }

    public void Stop()
    {
        if (_waveIn != null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            try { _waveIn.StopRecording(); } catch { }
            _waveIn.Dispose();
            _waveIn = null;
        }

        _workerRunning = false;
        _workerEvent.Set();
        _workerThread?.Join(1000);
        _workerThread = null;

        lock (_playLock)
        {
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveOut = null;
            _currentReader?.Dispose();
            _currentReader = null;
        }

        _meterLevel = 0f;

        _model?.Dispose();
        _model = null;
        _modelLoaded = false;

        Log("Voice Fight: stopped");
    }

    public void SetKeywords(IEnumerable<VoiceFightSettings.VfSoundItem> items)
    {
        lock (_keywordLock)
        {
            _keywordMap.Clear();
            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Word) || item.Files.Count == 0)
                    continue;
                var key = NormalizeWord(item.Word);
                if (key.Length > 0)
                    _keywordMap[key] = item;
            }
        }
    }

    public void SetStopWord(string word)
    {
        lock (_stopWordLock) _stopWord = NormalizeWord(word);
    }

    public void StopPlayback()
    {
        ThreadPool.QueueUserWorkItem(_ => { try { StopPlaybackAndConfirm(); } catch (Exception ex) { CrashHandler.WriteEntry("VoiceFight.StopPlayback", ex); } });
    }

    public void PlayFile(string filePath, float volumePercent)
    {
        ThreadPool.QueueUserWorkItem(_ => { try { PlayFileInternal(filePath, volumePercent); } catch (Exception ex) { CrashHandler.WriteEntry("VoiceFight.PlayFile", ex); } });
    }

    public void ReloadBlockList() => LoadBlockList();

    private void LoadBlockList()
    {
        if (!File.Exists(BlockListPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BlockListPath)!);
            File.WriteAllLines(BlockListPath, new[]
            {
                "# Words listed here are stripped from VOSK recognition results before keyword matching.",
                "# One word or phrase per line. Lines starting with # are comments."
            }.Concat(BlockListDefaults));
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(BlockListPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var norm = NormalizeWord(line);
            if (norm.Length > 0) set.Add(norm);
        }
        _blockList = set;
    }

    private void EnsureModel()
    {
        if (_model != null) return;

        if (!Directory.Exists(ModelPath))
        {
            Log($"Voice Fight: VOSK model not found at '{ModelPath}'");
            return;
        }

        try
        {
            Vosk.Vosk.SetLogLevel(-1);
            _model = new Model(ModelPath);
            _modelLoaded = true;
            Log("Voice Fight: model loaded");
        }
        catch (Exception ex)
        {
            Log($"Voice Fight: model load failed — {ex.Message}");
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        UpdateMeter(e.Buffer, e.BytesRecorded);

        if (_pcmQueue.Count > 500) return;
        var copy = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);
        _pcmQueue.Enqueue(copy);
        _workerEvent.Set();
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            Log($"Voice Fight: recording stopped with error — {e.Exception.Message}");
    }

    private void UpdateMeter(byte[] buf, int length)
    {
        if (length < 2) return;
        double sum = 0;
        int samples = length / 2;
        for (int i = 0; i < length - 1; i += 2)
        {
            short s = (short)(buf[i] | (buf[i + 1] << 8));
            double v = s / 32768.0;
            sum += v * v;
        }
        float rms = (float)Math.Sqrt(sum / samples);
        _meterLevel = Math.Min(1f, rms * 6f);
    }

    private void WorkerLoop()
    {
        VoskRecognizer? rec = null;

        try
        {
            if (_model != null)
            {
                rec = BuildRecognizer();
            }

            while (_workerRunning)
            {
                _workerEvent.WaitOne(20);

                if (rec == null && _model != null)
                    rec = BuildRecognizer();

                while (_pcmQueue.TryDequeue(out var chunk))
                {
                    if (rec == null) continue;

                    bool finalized = rec.AcceptWaveform(chunk, chunk.Length);

                    if (finalized)
                    {
                        var json = rec.Result();
                        ProcessResult(json, partial: false);
                    }
                    else
                    {
                        var json = rec.PartialResult();
                        ProcessResult(json, partial: true);
                    }
                }
            }

            if (rec != null)
            {
                var final = rec.FinalResult();
                ProcessResult(final, partial: false);
            }
        }
        catch (Exception ex)
        {
            Log($"Voice Fight: worker error — {ex.Message}");
            CrashHandler.AddBreadcrumb($"VoiceFight.WorkerLoop: {ex.GetType().Name}: {ex.Message}");
            CrashHandler.WriteEntry("VoiceFightService.WorkerLoop", ex);
        }
        finally
        {
            rec?.Dispose();
            if (_workerRunning)
            {
                _workerRunning = false;
                try { _waveIn?.StopRecording(); } catch { }
                while (_pcmQueue.TryDequeue(out _)) { }
            }
        }
    }

    private VoskRecognizer BuildRecognizer()
    {
        var r = new VoskRecognizer(_model, 16000f);
        r.SetWords(false);
        r.SetMaxAlternatives(0);
        return r;
    }

    private void StopPlaybackAndConfirm()
    {
        lock (_playLock)
        {
            if (_waveOut == null) return;

            try { _waveOut.Stop(); _waveOut.Dispose(); _waveOut = new WaveOut { DeviceNumber = _outputDeviceIndex }; }
            catch { _waveOut = new WaveOut { DeviceNumber = _outputDeviceIndex }; }

            if (!File.Exists(StopSoundPath)) return;
            try
            {
                _currentReader?.Dispose();
                _currentReader = null;
                var (disposable, provider) = OpenForPlayback(StopSoundPath, 1f);
                _currentReader = disposable;
                _waveOut.Init(provider);
                _waveOut.Play();
            }
            catch (Exception ex)
            {
                Log($"Voice Fight: stop-sound error — {ex.Message}");
            }
        }
    }

    private bool ProcessResult(string json, bool partial)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;

        string key = partial ? "partial" : "text";
        string? text = ExtractJsonString(json, key);
        if (string.IsNullOrWhiteSpace(text)) return false;

        var filtered = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !string.Equals(w, "[unk]", StringComparison.OrdinalIgnoreCase)));
        if (!string.IsNullOrWhiteSpace(filtered))
        {
            var displayHtml = BuildDisplayHtml(filtered, _blockList);
            var cleanText = BuildCleanText(filtered, _blockList);
            OnRecognized?.Invoke(displayHtml, cleanText, partial);
        }

        var textNorm = NormalizeWord(text);
        if (textNorm.Length == 0) return false;

        foreach (var blocked in _blockList)
            textNorm = RemoveWholeWord(textNorm, blocked);
        if (textNorm.Length == 0) return false;

        string sw;
        lock (_stopWordLock) sw = _stopWord;
        if (sw.Length > 0 && ContainsPhrase(textNorm, sw))
        {
            var now = DateTime.UtcNow;
            if ((now - _lastStopTriggered).TotalMilliseconds >= KeywordCooldownMs)
            {
                _lastStopTriggered = now;
                ThreadPool.QueueUserWorkItem(_ => StopPlaybackAndConfirm());
            }
            return true;
        }

        Dictionary<string, VoiceFightSettings.VfSoundItem> snapshot;
        lock (_keywordLock)
            snapshot = new Dictionary<string, VoiceFightSettings.VfSoundItem>(_keywordMap, StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in snapshot)
        {
            if (!ContainsPhrase(textNorm, kvp.Key)) continue;

            var now = DateTime.UtcNow;
            if (_lastTriggered.TryGetValue(kvp.Key, out var last) &&
                (now - last).TotalMilliseconds < KeywordCooldownMs) continue;
            _lastTriggered[kvp.Key] = now;

            OnKeywordTriggered?.Invoke(kvp.Key);
            var item = kvp.Value;
            var file = item.Files.Count == 1 ? item.Files[0] : item.Files[_rng.Next(item.Files.Count)];
            PlayFileInternal(file.FilePath, file.VolumePercent);
            return true;
        }

        return false;
    }

    private void PlayFileInternal(string filePath, float volumePercent)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

        lock (_playLock)
        {
            if (_waveOut == null) return;

            try
            {
                _waveOut.Stop();
                _waveOut.Dispose();
                _waveOut = new WaveOut { DeviceNumber = _outputDeviceIndex };
            }
            catch { _waveOut = new WaveOut { DeviceNumber = _outputDeviceIndex }; }

            try
            {
                _currentReader?.Dispose();
                _currentReader = null;
                var (disposable, provider) = OpenForPlayback(filePath, Math.Clamp(volumePercent / 100f, 0f, 1f));
                _currentReader = disposable;
                _waveOut.Init(provider);
                _waveOut.Play();
            }
            catch (Exception ex)
            {
                Log($"Voice Fight: playback error — {ex.Message}");
            }
        }
    }

    private static (IDisposable disposable, IWaveProvider provider) OpenForPlayback(string path, float volume01)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".ogg")
        {
            var vs = new VorbisSampleProvider(path);
            var vol = new VolumeSampleProvider(vs) { Volume = volume01 };
            return (vs, vol.ToWaveProvider());
        }
        WaveStream stream = ext == ".wav" ? new WaveFileReader(path) : new MediaFoundationReader(path);
        var volume = new VolumeSampleProvider(stream.ToSampleProvider()) { Volume = volume01 };
        return (stream, volume.ToWaveProvider());
    }

    private sealed class VorbisSampleProvider : ISampleProvider, IDisposable
    {
        private readonly NVorbis.VorbisReader _reader;
        private readonly WaveFormat _format;
        public VorbisSampleProvider(string path)
        {
            _reader = new NVorbis.VorbisReader(path);
            _format = WaveFormat.CreateIeeeFloatWaveFormat(_reader.SampleRate, _reader.Channels);
        }
        public WaveFormat WaveFormat => _format;
        public int Read(Span<float> buffer) => _reader.ReadSamples(buffer);
        public void Dispose() => _reader.Dispose();
    }

    private static string NormalizeWord(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var s = input.Trim().ToLowerInvariant();
        var buf = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
            if (char.IsLetterOrDigit(c)) buf.Append(c);
            else buf.Append(' ');
        return string.Join(' ', buf.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string HtmlEncode(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private string BuildDisplayHtml(string text, HashSet<string> blockList)
    {
        var words = text.Split(' ');
        var sb = new System.Text.StringBuilder();
        foreach (var word in words)
        {
            if (sb.Length > 0) sb.Append(' ');
            if (word.Length > 0 && blockList.Count > 0 && blockList.Contains(NormalizeWord(word)))
                sb.Append("<span class=\"vf-blocked\">").Append(HtmlEncode(word)).Append("</span>");
            else
                sb.Append(HtmlEncode(word));
        }
        return sb.ToString();
    }

    private string BuildCleanText(string text, HashSet<string> blockList)
    {
        if (blockList.Count == 0) return text;
        return string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !blockList.Contains(NormalizeWord(w))));
    }

    private static string RemoveWholeWord(string text, string word)
    {
        int idx = text.IndexOf(word, StringComparison.Ordinal);
        while (idx >= 0)
        {
            bool startOk = idx == 0 || text[idx - 1] == ' ';
            bool endOk = idx + word.Length == text.Length || text[idx + word.Length] == ' ';
            if (startOk && endOk)
            {
                int from = idx > 0 ? idx - 1 : 0;
                int len = idx > 0 ? word.Length + 1 : word.Length + (idx + word.Length < text.Length ? 1 : 0);
                text = text.Remove(from, len).Trim();
                idx = text.IndexOf(word, Math.Max(0, from), StringComparison.Ordinal);
            }
            else
            {
                idx = text.IndexOf(word, idx + 1, StringComparison.Ordinal);
            }
        }
        return text;
    }

    private static bool ContainsPhrase(string text, string phrase)
    {
        if (phrase.Length == 0) return false;
        int idx = text.IndexOf(phrase, StringComparison.Ordinal);
        while (idx >= 0)
        {
            bool startOk = idx == 0 || text[idx - 1] == ' ';
            bool endOk = idx + phrase.Length == text.Length || text[idx + phrase.Length] == ' ';
            if (startOk && endOk) return true;
            idx = text.IndexOf(phrase, idx + 1, StringComparison.Ordinal);
        }
        return false;
    }

    private static string? ExtractJsonString(string json, string key)
    {
        string needle = "\"" + key + "\"";
        int k = json.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (k < 0) return null;
        int colon = json.IndexOf(':', k + needle.Length);
        if (colon < 0) return null;
        int q1 = json.IndexOf('"', colon + 1);
        if (q1 < 0) return null;
        int q2 = json.IndexOf('"', q1 + 1);
        if (q2 < 0) return null;
        return json.Substring(q1 + 1, q2 - q1 - 1);
    }

    private void Log(string msg) => OnLog?.Invoke(msg);

    public void Dispose()
    {
        Stop();
        _model?.Dispose();
        _model = null;
        _workerEvent.Dispose();
    }
}
#endif
