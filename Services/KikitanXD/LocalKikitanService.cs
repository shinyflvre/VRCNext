using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
#if WINDOWS
using NAudio.Wave;
using Whisper.net;
using Whisper.net.LibraryLoader;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
#endif

namespace VRCNext.Services.KikitanXD;

#if !WINDOWS
public sealed class LocalKikitanService : IKikitanSpeechService
{
    public event Action<string, bool>? OnRecognized;
    public event Action<string>? OnTranslated;
    public event Action<string>? OnOutput;
    public event Action<string>? OnLog;
    public event Action? OnChatboxSent;

    public bool IsRunning => false;
    public float MeterLevel => 0f;

    public void Start(int deviceIndex, KikitanXDSettings settings)
        => throw new PlatformNotSupportedException("Local models are Windows only.");
    public void UpdateSettings(KikitanXDSettings settings) { }
    public void Stop() { }
    public void Dispose() { }
}
#else

public sealed class LocalKikitanService : IKikitanSpeechService
{
    public event Action<string, bool>? OnRecognized;
    public event Action<string>? OnTranslated;
    public event Action<string>? OnOutput;
    public event Action<string>? OnLog;
    public event Action? OnChatboxSent;

    private WaveIn? _waveIn;
    private volatile float _meterLevel;
    public float MeterLevel => _meterLevel;
    public bool IsRunning => _waveIn != null;

    private readonly ConcurrentQueue<byte[]> _pcmQueue = new();
    private readonly AutoResetEvent _workerEvent = new(false);
    private Thread? _workerThread;
    private volatile bool _workerRunning;

    private WhisperFactory? _whisperFactory;
    private WhisperProcessor? _whisper;
    private readonly object _whisperLock = new();

    private WhisperVadFactory? _vadFactory;
    private WhisperVadProcessor? _vad;
    private readonly object _vadLock = new();
    private bool _vadEnabled = true;
    private bool _loadedVadEnabled;

    private LLamaWeights? _llmWeights;
    private StatelessExecutor? _llm;
    private readonly SemaphoreSlim _llmLock = new(1, 1);

    private string _loadedSttId = "";
    private string _loadedLlmId = "";
    private string _loadedSourceLang = "";
    private bool   _loadedGpu;

    private bool NeedsLlm => _translateEnabled
        || string.Equals(_personality, "kawai", StringComparison.OrdinalIgnoreCase);

    private bool LiveTypingActive => _partialOsc && !_translateEnabled;

    private string _sourceLang = "auto";
    private string _targetLang = "en";
    private bool _translateEnabled;
    private bool _oscEnabled;
    private volatile bool _partialOsc;
    private DateTime _lastPartialSend = DateTime.MinValue;
    private string _lastPartialText = "";
    private const int PartialMinIntervalMs = 350;
    private string _personality = "raw";
    private volatile string[] _blockedWords = Array.Empty<string>();
    private volatile string[] _blockedSentences = Array.Empty<string>();

    private const int SampleRate = 16000;
    private const int Channels = 1;
    private const int BitsPerSample = 16;

    private volatile float _silenceThreshold = 0.0167f;
    private const int SilenceFlushMs = 800;
    private const int MinSpeechMs = 250;
    private const int MaxSegmentMs = 10000;
    private const int InterimMinMs = 600;
    private const int InterimIntervalMs = 500;
    private volatile bool _interimRunning;

    private static bool _llamaConfigured;
    private static readonly object _nativeLock = new();

    [System.Runtime.InteropServices.DllImport("kernel32", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryW(string? lpPathName);

    public static string WhisperRoot =>
        Path.Combine(LocalAiManager.Root, LocalAiCatalog.CoreDir, "whisper");

    private static void EnsureNatives(bool useGpu, Action<string> log)
    {
        lock (_nativeLock)
        {
            RuntimeOptions.LibraryPath = Path.Combine(WhisperRoot, "whisper.net");
            RuntimeOptions.RuntimeLibraryOrder = useGpu
                ? new List<RuntimeLibrary> { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu }
                : new List<RuntimeLibrary> { RuntimeLibrary.Cpu };
            log($"Local: whisper probe root {WhisperRoot}");

            if (_llamaConfigured) return;

            var llamaDll = FindLlamaLibrary(useGpu);
            if (llamaDll == null) return;

            try
            {
                var dir  = Path.GetDirectoryName(llamaDll)!;
                var mtmd = Path.Combine(dir, "mtmd.dll");
                SetDllDirectoryW(dir);
                NativeLibraryConfig.All.WithLibrary(llamaDll, File.Exists(mtmd) ? mtmd : null);
                _llamaConfigured = true;
                log($"Local: llama runtime {llamaDll}");
            }
            catch (Exception ex) { log($"Local: llama runtime selection failed — {ex.Message}"); }
        }
    }

    private static void VerifyWhisperRuntime(bool useGpu)
    {
        var cpu    = Path.Combine(WhisperRoot, "runtimes", "win-x64", "whisper.dll");
        var vulkan = Path.Combine(WhisperRoot, "runtimes", "vulkan", "win-x64", "whisper.dll");

        if (useGpu && File.Exists(vulkan)) return;
        if (File.Exists(cpu)) return;

        if (File.Exists(vulkan))
            throw new Exception("Only the Vulkan runtime is installed, but GPU is switched off. Turn GPU back on, or download 'Whisper Runtime (CPU)'.");

        var found = Directory.Exists(WhisperRoot)
            ? string.Join(", ", Directory.EnumerateFiles(WhisperRoot, "*.dll", SearchOption.AllDirectories)
                                         .Select(f => Path.GetRelativePath(WhisperRoot, f)).Take(8))
            : "(folder does not exist)";

        throw new Exception(
            $"Whisper runtime not found. Expected: {cpu}. Present under {WhisperRoot}: {found}. " +
            "Download 'Whisper Runtime (CPU)' under Model > Local Models.");
    }

    private static string? FindLlamaLibrary(bool useGpu)
    {
        var root = Path.Combine(LocalAiManager.Root, LocalAiCatalog.CoreDir, "llama");
        if (!Directory.Exists(root)) return null;

        var all = Directory.EnumerateFiles(root, "llama.dll", SearchOption.AllDirectories).ToList();
        if (all.Count == 0) return null;

        string? Pick(string tag) => all.FirstOrDefault(p => p.Contains(tag, StringComparison.OrdinalIgnoreCase));

        if (useGpu)
        {
            var vulkan = Pick("vulkan");
            if (vulkan != null) return vulkan;
        }

        return Pick("avx2") ?? all.FirstOrDefault(p =>
            !p.Contains("cuda", StringComparison.OrdinalIgnoreCase) &&
            !p.Contains("vulkan", StringComparison.OrdinalIgnoreCase));
    }

    public void Start(int deviceIndex, KikitanXDSettings s)
    {
        Stop();
        ApplySettings(s);

        var sttItem = LocalAiCatalog.Find(s.LocalSttModel ?? "");
        if (sttItem == null || !File.Exists(LocalAiManager.PathFor(sttItem)))
            throw new Exception("No recognition model installed. Download one under Model > Local Models.");

        bool useGpu = s.LocalUseGpu;
        if (useGpu && !LocalAiManager.VulkanAvailable())
        {
            useGpu = false;
            Log("Kikitan XD: no Vulkan driver found, using CPU instead");
        }

        EnsureNatives(useGpu, Log);
        VerifyWhisperRuntime(useGpu);

        BuildWhisper(LocalAiManager.PathFor(sttItem));
        _loadedSttId = sttItem.Id;
        _loadedGpu   = useGpu;
        LoadVad();

        if (NeedsLlm)
        {
            LoadLlm(s, useGpu);
            _loadedLlmId = s.LocalLlmModel ?? "";
        }

        _waveIn = new WaveIn
        {
            DeviceNumber = deviceIndex,
            WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels),
            BufferMilliseconds = 50
        };
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;

        _workerRunning = true;
        _workerThread = new Thread(WorkerLoop) { IsBackground = true };
        _workerThread.Start();

        _waveIn.StartRecording();
        Log("Kikitan XD: local listening started");
    }

    private void LoadLlm(KikitanXDSettings s, bool useGpu)
    {
        var llmItem = LocalAiCatalog.Find(s.LocalLlmModel ?? "");
        if (llmItem == null || !File.Exists(LocalAiManager.PathFor(llmItem)))
            throw new Exception("No translation model installed. Download one under Model > Local Models.");

        if (FindLlamaLibrary(useGpu) == null)
            throw new Exception(
                $"Translation runtime not found under {Path.Combine(LocalAiManager.Root, LocalAiCatalog.CoreDir, "llama")}. " +
                "Download 'Translation Runtime (CPU)' under Model > Local Models.");

        try
        {
            var p = new ModelParams(LocalAiManager.PathFor(llmItem))
            {
                ContextSize   = 2048,
                GpuLayerCount = useGpu ? 999 : 0,
            };
            _llmWeights = LLamaWeights.LoadFromFile(p);
            _llm = new StatelessExecutor(_llmWeights, p);
            Log($"Kikitan XD: loaded {llmItem.Name} ({(useGpu ? "GPU" : "CPU")})");
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException?.Message ?? ex.Message;
            throw new Exception($"Translation runtime could not be loaded. ({inner})");
        }
    }

    private void LoadVad()
    {
        _loadedVadEnabled = _vadEnabled;
        if (!_vadEnabled) return;

        var item = LocalAiCatalog.Find("vad-silero");
        if (item == null || !File.Exists(LocalAiManager.PathFor(item)))
        {
            Log("Kikitan XD: Silero VAD model not installed, using the noise gate only");
            return;
        }

        try
        {
            var factory = WhisperVadFactory.FromPath(LocalAiManager.PathFor(item));
            var processor = factory.CreateBuilder()
                .WithUseGpu(false)
                .WithThreads(2)
                .WithThreshold(0.5f)
                .WithMinSpeechDuration(TimeSpan.FromMilliseconds(MinSpeechMs))
                .WithMinSilenceDuration(TimeSpan.FromMilliseconds(100))
                .WithSpeechPadding(TimeSpan.FromMilliseconds(200))
                .Build();
            lock (_vadLock)
            {
                _vadFactory = factory;
                _vad = processor;
            }
            Log("Kikitan XD: Silero VAD enabled");
        }
        catch (Exception ex)
        {
            Log($"Kikitan XD: Silero VAD could not be loaded ({ex.Message}), using the noise gate only");
        }
    }

    private void UnloadVad()
    {
        lock (_vadLock)
        {
            try { _vad?.Dispose(); } catch { }
            _vad = null;
            try { _vadFactory?.Dispose(); } catch { }
            _vadFactory = null;
        }
    }

    private bool TrimToSpeech(ref byte[] pcm)
    {
        if (_vad == null) return true;
        int n = pcm.Length / 2;
        if (n == 0) return false;

        var samples = new float[n];
        for (int i = 0; i < n; i++)
            samples[i] = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8)) / 32768f;

        double start = -1, end = -1;
        lock (_vadLock)
        {
            var vad = _vad;
            if (vad == null) return true;
            try
            {
                foreach (var seg in vad.DetectSpeech(samples))
                {
                    if (start < 0) start = seg.Start.TotalSeconds;
                    end = seg.End.TotalSeconds;
                }
            }
            catch (Exception ex)
            {
                Log($"Kikitan XD: Silero VAD error, {ex.Message}");
                return true;
            }
        }
        if (start < 0) return false;

        int bytesPerSec = SampleRate * Channels * (BitsPerSample / 8);
        int from = Math.Clamp((int)(start * bytesPerSec) & ~1, 0, pcm.Length);
        int to   = Math.Clamp((int)(end * bytesPerSec) & ~1, from, pcm.Length);
        if (to <= from) return false;
        if (from == 0 && to == pcm.Length) return true;

        var cut = new byte[to - from];
        Buffer.BlockCopy(pcm, from, cut, 0, cut.Length);
        pcm = cut;
        return true;
    }

    private void BuildWhisper(string modelPath)
    {
        try { _whisperFactory = WhisperFactory.FromPath(modelPath); }
        catch (Exception ex) { throw new Exception($"Whisper runtime could not be loaded. ({ex.Message})"); }

        BuildProcessor();
    }

    private void BuildProcessor()
    {
        if (_whisperFactory == null) return;
        var builder = _whisperFactory.CreateBuilder();
        builder = string.Equals(_sourceLang, "auto", StringComparison.OrdinalIgnoreCase)
            ? builder.WithLanguageDetection()
            : builder.WithLanguage(_sourceLang);
        _whisper = builder.Build();
        _loadedSourceLang = _sourceLang ?? "auto";
    }

    public void UpdateSettings(KikitanXDSettings s)
    {
        ApplySettings(s);
        if (_waveIn == null) return;

        bool gpu = s.LocalUseGpu && LocalAiManager.VulkanAvailable();
        bool sttChanged  = (s.LocalSttModel ?? "") != _loadedSttId || gpu != _loadedGpu;
        bool langChanged = (s.SourceLang ?? "auto") != _loadedSourceLang;
        bool llmChanged  = NeedsLlm && ((s.LocalLlmModel ?? "") != _loadedLlmId || gpu != _loadedGpu);
        bool vadChanged  = s.LocalVadEnabled != _loadedVadEnabled;
        if (!sttChanged && !langChanged && !llmChanged && !vadChanged) return;

        ThreadPool.QueueUserWorkItem(_ => SwapModels(s, gpu, sttChanged, langChanged, llmChanged, vadChanged));
    }

    private void SwapModels(KikitanXDSettings s, bool gpu, bool swapStt, bool swapLang, bool swapLlm, bool swapVad)
    {
        try
        {
            if (gpu != _loadedGpu && _llamaConfigured)
                Log("Kikitan XD: GPU switch applies to translation only after restarting VRCNext");

            EnsureNatives(gpu, Log);

            if (swapStt)
            {
                var item = LocalAiCatalog.Find(s.LocalSttModel ?? "");
                if (item != null && File.Exists(LocalAiManager.PathFor(item)))
                {
                    lock (_whisperLock)
                    {
                        try { _whisper?.Dispose(); } catch { }
                        _whisper = null;
                        try { _whisperFactory?.Dispose(); } catch { }
                        _whisperFactory = null;
                        BuildWhisper(LocalAiManager.PathFor(item));
                    }
                    _loadedSttId = item.Id;
                    Log($"Kikitan XD: recognition switched to {item.Name}");
                }
            }
            else if (swapLang)
            {
                lock (_whisperLock)
                {
                    try { _whisper?.Dispose(); } catch { }
                    _whisper = null;
                    BuildProcessor();
                }
                Log($"Kikitan XD: source language switched to {_sourceLang}");
            }

            if (swapLlm)
            {
                bool held = _llmLock.Wait(20000);
                try
                {
                    (_llm as IDisposable)?.Dispose();
                    _llm = null;
                    try { _llmWeights?.Dispose(); } catch { }
                    _llmWeights = null;
                    GC.Collect();
                    GC.WaitForPendingFinalizers();

                    LoadLlm(s, gpu);
                    _loadedLlmId = s.LocalLlmModel ?? "";
                }
                finally { if (held) _llmLock.Release(); }
            }

            if (swapVad)
            {
                UnloadVad();
                LoadVad();
                if (!_vadEnabled) Log("Kikitan XD: Silero VAD disabled");
            }

            _loadedGpu = gpu;
        }
        catch (Exception ex)
        {
            Log($"Kikitan XD: model switch failed — {ex.Message}");
        }
    }

    private void ApplySettings(KikitanXDSettings s)
    {
        _sourceLang = s.SourceLang;
        _targetLang = s.TargetLang;
        _translateEnabled = s.TranslateEnabled;
        _oscEnabled = s.OscEnabled;
        _partialOsc = s.PartialOsc;
        _personality = s.Personality ?? "raw";
        _silenceThreshold = Math.Clamp(s.NoiseGatePercent / 100f / 6f, 0.001f, 0.5f);
        _blockedWords = NormalizeList(s.BlockedWords);
        _blockedSentences = NormalizeList(s.BlockedSentences);
        _disableNonSpeech = s.DisableNonSpeech;
        _chatboxNotify = s.ChatboxNotify;
        _vadEnabled = s.LocalVadEnabled;
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
        _workerThread?.Join(1500);
        _workerThread = null;

        for (int i = 0; i < 150 && _interimRunning; i++) Thread.Sleep(20);

        UnloadModels();

        while (_pcmQueue.TryDequeue(out _)) { }
        _meterLevel = 0f;
        _lastPartialText = "";
        Log("Kikitan XD: local stopped, models unloaded");
    }

    private void UnloadModels()
    {
        bool held = _llmLock.Wait(10000);
        try
        {
            (_llm as IDisposable)?.Dispose();
            _llm = null;
            try { _llmWeights?.Dispose(); } catch { }
            _llmWeights = null;
            _loadedLlmId = "";
        }
        finally { if (held) _llmLock.Release(); }

        UnloadVad();

        lock (_whisperLock)
        {
            try { _whisper?.Dispose(); } catch { }
            _whisper = null;
            try { _whisperFactory?.Dispose(); } catch { }
            _whisperFactory = null;
            _loadedSttId = "";
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
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
            Log($"Kikitan XD: recording stopped — {e.Exception.Message}");
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
        _meterLevel = Math.Min(1f, (float)Math.Sqrt(sum / samples) * 6f);
    }

    private void WorkerLoop()
    {
        var speechBuffer = new List<byte>();
        int silentMs = 0, speechMs = 0;
        bool inSpeech = false;
        int bytesPerMs = SampleRate * Channels * (BitsPerSample / 8) / 1000;
        bool busy = false;
        int lastInterim = Environment.TickCount;

        try
        {
            while (_workerRunning)
            {
                _workerEvent.WaitOne(20);

                while (_pcmQueue.TryDequeue(out var chunk))
                {
                    double sum = 0;
                    int samples = chunk.Length / 2;
                    for (int i = 0; i < chunk.Length - 1; i += 2)
                    {
                        short s = (short)(chunk[i] | (chunk[i + 1] << 8));
                        double v = s / 32768.0;
                        sum += v * v;
                    }
                    float rms = samples > 0 ? (float)Math.Sqrt(sum / samples) : 0f;
                    int chunkMs = bytesPerMs > 0 ? chunk.Length / bytesPerMs : 0;

                    if (rms > _silenceThreshold)
                    {
                        silentMs = 0;
                        speechBuffer.AddRange(chunk);
                        speechMs += chunkMs;
                        inSpeech = true;
                    }
                    else if (inSpeech)
                    {
                        silentMs += chunkMs;
                        speechBuffer.AddRange(chunk);
                    }

                    bool flush = inSpeech && ((silentMs >= SilenceFlushMs && speechMs >= MinSpeechMs) || speechMs >= MaxSegmentMs);

                    if (!flush)
                    {
                        if (!inSpeech || !LiveTypingActive || busy || _interimRunning) continue;
                        if (speechMs < InterimMinMs) continue;
                        if (unchecked(Environment.TickCount - lastInterim) < InterimIntervalMs) continue;

                        lastInterim = Environment.TickCount;
                        _interimRunning = true;
                        var partialPcm = speechBuffer.ToArray();
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try { ProcessInterim(partialPcm); }
                            finally { _interimRunning = false; }
                        });
                        continue;
                    }

                    var segment = speechBuffer.ToArray();
                    speechBuffer.Clear();
                    speechMs = 0; silentMs = 0; inSpeech = false;

                    if (busy) continue;
                    busy = true;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { ProcessSegment(segment); }
                        finally { busy = false; }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Kikitan XD: local worker error — {ex.Message}");
        }
        finally
        {
            if (_workerRunning)
            {
                _workerRunning = false;
                try { _waveIn?.StopRecording(); } catch { }
                while (_pcmQueue.TryDequeue(out _)) { }
            }
        }
    }

    private void ProcessSegment(byte[] pcm)
    {
        try
        {
            if (!TrimToSpeech(ref pcm)) return;
            string srcText = Transcribe(pcm);
            if (string.IsNullOrWhiteSpace(srcText)) return;

            srcText = ApplyBlockFilters(srcText);
            if (string.IsNullOrWhiteSpace(srcText)) return;

            OnRecognized?.Invoke(srcText, false);

            string outText = srcText;
            bool kawai = string.Equals(_personality, "kawai", StringComparison.OrdinalIgnoreCase);
            if (_translateEnabled && !string.IsNullOrWhiteSpace(_targetLang))
            {
                string translated = Translate(srcText).GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(translated))
                {
                    outText = translated;
                    if (kawai)
                    {
                        string kaomoji = PickKaomoji(srcText).GetAwaiter().GetResult();
                        if (kaomoji.Length > 0) outText = translated + " " + kaomoji;
                    }
                    OnTranslated?.Invoke(outText);
                }
            }
            else if (kawai)
            {
                string kaomoji = PickKaomoji(srcText).GetAwaiter().GetResult();
                if (kaomoji.Length > 0) outText = srcText + " " + kaomoji;
            }

            if (_oscEnabled) { SendChatbox(outText, _chatboxNotify); OnChatboxSent?.Invoke(); }
            OnOutput?.Invoke(outText);
        }
        catch (Exception ex)
        {
            Log($"Kikitan XD: local segment error — {ex.Message}");
        }
    }

    private void ProcessInterim(byte[] pcm)
    {
        try
        {
            if (!TrimToSpeech(ref pcm)) return;
            string text = Transcribe(pcm);
            if (string.IsNullOrWhiteSpace(text)) return;

            text = ApplyBlockFilters(text);
            if (string.IsNullOrWhiteSpace(text)) return;

            OnRecognized?.Invoke(text, true);
            if (_oscEnabled) SendPartial(text);
        }
        catch (Exception ex)
        {
            Log($"Kikitan XD: interim error — {ex.Message}");
        }
    }

    private string Transcribe(byte[] pcm)
    {
        var processor = _whisper;
        if (processor == null) return "";

        lock (_whisperLock)
        {
            if (_whisper == null) return "";
            using var ms = new MemoryStream(PcmToWav(pcm, SampleRate, Channels, BitsPerSample));
            var sb = new System.Text.StringBuilder();
            foreach (var seg in _whisper.ProcessAsync(ms).ToBlockingEnumerable())
                sb.Append(seg.Text);
            return sb.ToString().Trim();
        }
    }

    private const string TranslateSystemPrompt =
        "You are a translation engine. Translate the user's text into {TARGET}. " +
        "Output ONLY the translated text. No explanations, no quotes, no labels. " +
        "If the text is already in {TARGET}, output it unchanged.";

    private const string KaomojiSystemPrompt =
        "You choose ONE kaomoji (Japanese-style text emoticon built from punctuation, letters and symbols) that matches the emotional mood of the user's text. " +
        "Output ONLY the kaomoji. No words, no explanations, no quotes, no Unicode emoji like 😀 or 😢.\n" +
        "Mood reference pools (examples, you may also invent your own):\n" +
        "Happy / cheerful / positive: ^-^   ^^   ♡(>ᴗ•)   (✿◕‿◕)   (≧◡≦)\n" +
        "Love / affection / romantic: ♡   (♡˙︶˙♡)   ( ˘ ³˘)♥\n" +
        "Sad / disappointed / down: (ノ_<。)   (>﹏<)   (╥﹏╥)   (T_T)\n" +
        "Embarrassed / shy / pain / frustrated: ( 〃▽〃)   >_<   (x_x)   (╯°□°)╯\n" +
        "Surprised / shocked: w(°ｏ°)w   (×_×)   Σ(°ロ°)\n" +
        "Neutral / calm / informative: (・_・)   (._.)   (¬_¬)";

    private static readonly Dictionary<string, string> LangNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English", ["ja"] = "Japanese", ["zh"] = "Chinese", ["ko"] = "Korean",
        ["de"] = "German", ["fr"] = "French", ["es"] = "Spanish", ["pt"] = "Portuguese",
        ["ru"] = "Russian", ["ar"] = "Arabic", ["it"] = "Italian", ["nl"] = "Dutch",
        ["pl"] = "Polish", ["sv"] = "Swedish", ["tr"] = "Turkish", ["id"] = "Indonesian",
        ["fi"] = "Finnish", ["no"] = "Norwegian", ["cs"] = "Czech", ["hu"] = "Hungarian",
        ["ro"] = "Romanian", ["uk"] = "Ukrainian", ["th"] = "Thai", ["vi"] = "Vietnamese",
        ["hi"] = "Hindi",
    };

    private Task<string> Translate(string text)
    {
        string target = LangNames.TryGetValue(_targetLang, out var n) ? n : _targetLang;
        return RunLlm(TranslateSystemPrompt.Replace("{TARGET}", target), text, 256);
    }

    private async Task<string> PickKaomoji(string text)
    {
        string raw = await RunLlm(KaomojiSystemPrompt, text, 24);
        return SanitizeKaomoji(raw);
    }

    private static string SanitizeKaomoji(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string s = raw.Split('\n')[0].Trim().Trim('"', '\'', '`');
        if (s.Length == 0 || s.Length > 24) return "";
        if (System.Text.RegularExpressions.Regex.IsMatch(s, "[A-Za-z]{3,}")) return "";
        if (System.Text.RegularExpressions.Regex.IsMatch(s, "[\\uD800-\\uDFFF]")) return "";
        return s;
    }

    private void SendPartial(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (text == _lastPartialText) return;
        if ((DateTime.UtcNow - _lastPartialSend).TotalMilliseconds < PartialMinIntervalMs) return;
        _lastPartialSend = DateTime.UtcNow;
        _lastPartialText = text;
        SendChatbox(text.Length > 140 ? text[^140..] + "..." : text + "...", false);
    }

    private async Task<string> RunLlm(string system, string user, int maxTokens)
    {
        var exec = _llm;
        if (exec == null) return "";

        string prompt =
            $"<|im_start|>system\n{system}<|im_end|>\n" +
            $"<|im_start|>user\n{user}<|im_end|>\n" +
            "<|im_start|>assistant\n";

        var inferParams = new InferenceParams
        {
            MaxTokens = maxTokens,
            AntiPrompts = new List<string> { "<|im_end|>", "<|im_start|>" },
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.3f },
        };

        await _llmLock.WaitAsync();
        try
        {
            var sb = new System.Text.StringBuilder();
            await foreach (var token in exec.InferAsync(prompt, inferParams))
                sb.Append(token);

            var result = sb.ToString();
            foreach (var stop in new[] { "<|im_end|>", "<|im_start|>" })
            {
                int idx = result.IndexOf(stop, StringComparison.Ordinal);
                if (idx >= 0) result = result[..idx];
            }
            return result.Trim();
        }
        catch (Exception ex)
        {
            Log($"Kikitan XD: local translate error — {ex.Message}");
            return "";
        }
        finally { _llmLock.Release(); }
    }

    private static string[] NormalizeList(IEnumerable<string>? items)
    {
        if (items == null) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var i in items)
            if (!string.IsNullOrWhiteSpace(i)) list.Add(i.Trim());
        return list.ToArray();
    }

    private static string NormalizeSentence(string s)
        => s.Trim().Trim(' ', '.', ',', '!', '?', ';', ':', '"', '\'', '。', '！', '？', '、', '…').Trim();


    private volatile bool _disableNonSpeech = true;
    private volatile bool _chatboxNotify = true;

    private static readonly System.Text.RegularExpressions.Regex NonSpeechRx =
        new(@"\([^)]*\)|\[[^\]]*\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private string StripNonSpeech(string text)
    {
        if (!_disableNonSpeech || string.IsNullOrWhiteSpace(text)) return text;
        var cleaned = NonSpeechRx.Replace(text, " ");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s{2,}", " ").Trim();

        foreach (var c in cleaned)
            if (char.IsLetterOrDigit(c)) return cleaned;
        return "";
    }

    private string ApplyBlockFilters(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        text = StripNonSpeech(text);
        if (string.IsNullOrWhiteSpace(text)) return "";

        var sentences = _blockedSentences;
        if (sentences.Length > 0)
        {
            string norm = NormalizeSentence(text);
            foreach (var s in sentences)
                if (norm.Equals(NormalizeSentence(s), StringComparison.OrdinalIgnoreCase)) return "";
        }

        var words = _blockedWords;
        if (words.Length > 0)
        {
            foreach (var w in words)
            {
                if (string.IsNullOrWhiteSpace(w)) continue;
                text = System.Text.RegularExpressions.Regex.Replace(
                    text, $@"\b{System.Text.RegularExpressions.Regex.Escape(w)}\b", "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s{2,}", " ").Trim();
        }

        return text;
    }

    private static void SendChatbox(string text, bool notify)
    {
        try
        {
            if (text.Length > 144) text = text[..144];
            using var udp = new System.Net.Sockets.UdpClient();
            udp.Connect("127.0.0.1", 9000);
            var buf = new List<byte>();
            OscString(buf, "/chatbox/input");
            OscString(buf, notify ? ",sTT" : ",sTF");
            OscString(buf, text);
            var pkt = buf.ToArray();
            udp.Send(pkt, pkt.Length);
        }
        catch { }
    }

    private static void OscString(List<byte> buf, string s)
    {
        var b = System.Text.Encoding.UTF8.GetBytes(s);
        buf.AddRange(b);
        int pad = 4 - (b.Length % 4);
        for (int i = 0; i < pad; i++) buf.Add(0);
    }

    private static byte[] PcmToWav(byte[] pcm, int sampleRate, int channels, int bitsPerSample)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        int blockAlign = channels * bitsPerSample / 8;
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + pcm.Length);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)blockAlign);
        w.Write((short)bitsPerSample);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(pcm.Length);
        w.Write(pcm);
        return ms.ToArray();
    }

    private void Log(string msg) => OnLog?.Invoke(msg);

    public void Dispose()
    {
        Stop();
        _workerEvent.Dispose();
        _llmLock.Dispose();
    }
}
#endif
