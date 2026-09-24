using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
#if WINDOWS
using NAudio.Wave;
#endif

namespace VRCNext.Services.KikitanXD;

#if !WINDOWS
public sealed class KikitanXDService : IKikitanSpeechService
{
    public event Action<string, bool>? OnRecognized;
    public event Action<string>? OnTranslated;
    public event Action<string>? OnOutput;
    public event Action<string>? OnLog;
    public event Action? OnChatboxSent;
    public bool IsRunning => false;
    public float MeterLevel => 0f;
    public void Start(int deviceIndex, KikitanXDSettings settings) { }
    public void UpdateSettings(KikitanXDSettings settings) { }
    public void Stop() { }
    public void Dispose() { }
    public static Task<string> TranslateStandaloneAsync(string apiKey, string text, string sourceLang, string targetLang) => Task.FromResult("");
}
#else

public sealed class KikitanXDService : IKikitanSpeechService
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

    private string _apiKey = "";
    private string _sourceLang = "auto";
    private string _targetLang = "en";
    private bool _translateEnabled;
    private bool _oscEnabled;
    private string _personality = "raw";
    private volatile string[] _blockedWords = Array.Empty<string>();
    private volatile string[] _blockedSentences = Array.Empty<string>();

    private static readonly HttpClient _http = new();

    private const int SampleRate = 16000;
    private const int Channels = 1;
    private const int BitsPerSample = 16;

    // VAD thresholds — SilenceThreshold is derived from user noise gate (percent / 100 / 6)
    private volatile float _silenceThreshold = 0.0167f; // ~10% on meter
    private const int SilenceFlushMs = 800;
    private const int MinSpeechMs = 250;
    private const int MaxSegmentMs = 10000;

    public void UpdateSettings(KikitanXDSettings s)
    {
        _apiKey = s.ApiKey;
        _sourceLang = s.SourceLang;
        _targetLang = s.TargetLang;
        _translateEnabled = s.TranslateEnabled;
        _oscEnabled = s.OscEnabled;
        _personality = s.Personality ?? "raw";
        _silenceThreshold = Math.Clamp(s.NoiseGatePercent / 100f / 6f, 0.001f, 0.5f);
        _blockedWords = NormalizeList(s.BlockedWords);
        _blockedSentences = NormalizeList(s.BlockedSentences);
        _disableNonSpeech = s.DisableNonSpeech;
        _chatboxNotify = s.ChatboxNotify;
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
    {
        return s.Trim().Trim(' ', '.', ',', '!', '?', ';', ':', '"', '\'', '。', '！', '？', '、', '…').Trim();
    }


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
            {
                if (norm.Equals(NormalizeSentence(s), StringComparison.OrdinalIgnoreCase))
                    return "";
            }
        }

        var words = _blockedWords;
        if (words.Length > 0)
        {
            foreach (var w in words)
            {
                if (string.IsNullOrWhiteSpace(w)) continue;
                text = System.Text.RegularExpressions.Regex.Replace(
                    text,
                    $@"\b{System.Text.RegularExpressions.Regex.Escape(w)}\b",
                    "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s{2,}", " ").Trim();
        }

        return text;
    }

    private const string TranslateSystemPromptRaw =
        "You are a raw linguistic parsing protocol. Your only function is to convert text from [LANG_SRC] to [LANG_TARGET]. " +
        "Output only the direct translation. No pre-text, no post-text, no explanations. " +
        "If the text is already in [LANG_TARGET], output it unchanged.";

    private const string KawaiPersonalityAddon =
        " After the translated sentence, append EXACTLY ONE single kaomoji (Japanese-style text emoticon built from punctuation/letters/symbols, like ^-^ or (>﹏<)) on the same line that matches the emotional mood of the sentence. " +
        "Never output more than one kaomoji. Never output zero — always exactly one. Do not add explanations or labels — just the kaomoji at the very end. " +
        "Output ONLY kaomojis (text emoticons). NEVER output Unicode emoji like 😀 ❤️ 😢 — only ASCII/punctuation-based kaomojis. " +
        "You may use the examples below OR freestyle your own kaomoji — be creative and pick whichever fits the mood best. " +
        "Mood reference pools (just examples — feel free to invent new ones):\n" +
        "Happy / cheerful / positive: ^-^   ^^   ♡(>ᴗ•)   (✿◕‿◕)   (≧◡≦)\n" +
        "Love / affection / romantic: ♡   (♡˙︶˙♡)   ( ˘ ³˘)♥\n" +
        "Sad / disappointed / down: (ノ_<。)   ｡ﾟ･ (>﹏<) ･ﾟ｡   (╥﹏╥)\n" +
        "Embarrassed / shy / pain / 'ahhhh' / frustrated: ( 〃▽〃)   !!>_<!!   >_<   (x_x)⌒☆   (╯°□°)╯\n" +
        "Surprised / shocked: w(°ｏ°)w   (×_×)   Σ(°ロ°)";

    private static string BuildTranslateSystemPrompt(string personality)
    {
        return string.Equals(personality, "kawai", StringComparison.OrdinalIgnoreCase)
            ? TranslateSystemPromptRaw + KawaiPersonalityAddon
            : TranslateSystemPromptRaw;
    }

    public void Start(int deviceIndex, KikitanXDSettings s)
    {
        Stop();
        _apiKey = s.ApiKey;
        _sourceLang = s.SourceLang;
        _targetLang = s.TargetLang;
        _translateEnabled = s.TranslateEnabled;
        _oscEnabled = s.OscEnabled;
        _personality = s.Personality ?? "raw";
        _silenceThreshold = Math.Clamp(s.NoiseGatePercent / 100f / 6f, 0.001f, 0.5f);
        _blockedWords = NormalizeList(s.BlockedWords);
        _blockedSentences = NormalizeList(s.BlockedSentences);
        _disableNonSpeech = s.DisableNonSpeech;
        _chatboxNotify = s.ChatboxNotify;

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
        Log("Kikitan XD: listening started");
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

        _meterLevel = 0f;
        Log("Kikitan XD: stopped");
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
        int silentMs = 0;
        int speechMs = 0;
        bool inSpeech = false;
        int bytesPerMs = SampleRate * Channels * (BitsPerSample / 8) / 1000;

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

                    bool flushSilence = inSpeech && silentMs >= SilenceFlushMs && speechMs >= MinSpeechMs;
                    bool flushMax = inSpeech && speechMs >= MaxSegmentMs;

                    if (flushSilence || flushMax)
                    {
                        var segment = speechBuffer.ToArray();
                        speechBuffer.Clear();
                        speechMs = 0;
                        silentMs = 0;
                        inSpeech = false;
                        ThreadPool.QueueUserWorkItem(_ => ProcessSegment(segment));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Kikitan XD: worker error — {ex.Message}");
            CrashHandler.AddBreadcrumb($"KikitanXD.WorkerLoop: {ex.GetType().Name}: {ex.Message}");
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
            var wavBytes = PcmToWav(pcm, SampleRate, Channels, BitsPerSample);
            string srcText = TranscribeAsync(wavBytes).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(srcText)) return;

            srcText = ApplyBlockFilters(srcText);
            if (string.IsNullOrWhiteSpace(srcText)) return;

            OnRecognized?.Invoke(srcText, false);

            if (!_translateEnabled || string.IsNullOrWhiteSpace(_targetLang))
            {
                string outText = srcText;
                if (string.Equals(_personality, "kawai", StringComparison.OrdinalIgnoreCase))
                {
                    string withKaomoji = AppendKaomojiAsync(srcText).GetAwaiter().GetResult();
                    if (!string.IsNullOrWhiteSpace(withKaomoji)) outText = withKaomoji;
                }
                if (_oscEnabled) { SendChatbox(outText, _chatboxNotify); OnChatboxSent?.Invoke(); }
                OnOutput?.Invoke(outText);
                return;
            }

            string translated = TranslateAsync(srcText, _sourceLang, _targetLang).GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(translated))
            {
                OnTranslated?.Invoke(translated);
                if (_oscEnabled) { SendChatbox(translated, _chatboxNotify); OnChatboxSent?.Invoke(); }
                OnOutput?.Invoke(translated);
            }
        }
        catch (Exception ex)
        {
            Log($"Kikitan XD: process error — {ex.Message}");
        }
    }

    private async Task<string> TranscribeAsync(byte[] wavBytes)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(wavBytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");
        content.Add(fileContent, "file", "audio.wav");
        content.Add(new StringContent("whisper-large-v3-turbo"), "model");
        if (!string.Equals(_sourceLang, "auto", StringComparison.OrdinalIgnoreCase))
            content.Add(new StringContent(_sourceLang), "language");
        content.Add(new StringContent("json"), "response_format");

        using var req = new HttpRequestMessage(HttpMethod.Post,
            "https://api.groq.com/openai/v1/audio/transcriptions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = content;

        var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            Log($"Kikitan XD: STT error {(int)resp.StatusCode}");
            return "";
        }
        var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
        return json["text"]?.ToString()?.Trim() ?? "";
    }

    private static readonly Dictionary<string, string> LangNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English", ["ja"] = "Japanese", ["zh"] = "Chinese", ["ko"] = "Korean",
        ["de"] = "German", ["fr"] = "French", ["es"] = "Spanish", ["pt"] = "Portuguese",
        ["ru"] = "Russian", ["ar"] = "Arabic", ["it"] = "Italian", ["nl"] = "Dutch",
        ["pl"] = "Polish", ["sv"] = "Swedish", ["tr"] = "Turkish", ["id"] = "Indonesian",
        ["fi"] = "Finnish", ["no"] = "Norwegian", ["cs"] = "Czech", ["hu"] = "Hungarian",
        ["ro"] = "Romanian", ["uk"] = "Ukrainian", ["th"] = "Thai", ["vi"] = "Vietnamese",
        ["hi"] = "Hindi"
    };

    private const string GroqPrimaryModel  = "qwen/qwen3.8-27b";
    private const string GroqFallbackModel = "openai/gpt-oss-20b";

    private static async Task<(string content, int status, string model)> GroqChatAsync(string apiKey, JObject body)
    {
        int status = 0;
        foreach (var model in new[] { GroqPrimaryModel, GroqFallbackModel })
        {
            body["model"] = model;
            body["reasoning_effort"] = model == GroqPrimaryModel ? "none" : "low";
            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://api.groq.com/openai/v1/chat/completions");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            req.Content = new StringContent(body.ToString(), System.Text.Encoding.UTF8, "application/json");
            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req); }
            catch { status = -1; continue; }
            using (resp)
            {
                status = (int)resp.StatusCode;
                if (!resp.IsSuccessStatusCode) continue;
                var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
                var content = json["choices"]?[0]?["message"]?["content"]?.ToString()?.Trim() ?? "";
                if (content.Length > 0) return (content, status, model);
            }
        }
        return ("", status, GroqFallbackModel);
    }

    public static async Task<string> TranslateStandaloneAsync(string apiKey, string text, string sourceLang, string targetLang)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(targetLang))
            return "";

        string targetName = LangNames.TryGetValue(targetLang, out var tn) ? tn : targetLang;

        string systemPrompt =
            $"You are a translation engine. Translate the user's text into {targetName}. " +
            "Auto-detect the source language. Output ONLY the translated text. " +
            "Do not add prefixes, suffixes, explanations, quotes, or notes. " +
            $"If the source text is already in {targetName}, output it unchanged.";

        var body = new JObject
        {
            ["reasoning_format"] = "hidden",
            ["temperature"] = 0.2,
            ["max_completion_tokens"] = 1024,
            ["messages"] = new JArray
            {
                new JObject { ["role"] = "system", ["content"] = systemPrompt },
                new JObject { ["role"] = "user", ["content"] = text }
            }
        };

        var (content, _, _) = await GroqChatAsync(apiKey, body);
        return content;
    }

    private const string KaomojiOnlySystemPrompt =
        "You append exactly one kaomoji (Japanese-style text emoticon built from punctuation/letters/symbols, like ^-^ or (>﹏<)) to the end of the user's text. " +
        "Output the original text UNCHANGED (same language, same words, same punctuation), then a single space, then the kaomoji on the same line. " +
        "Never translate. Never modify the original text. Never output more than one kaomoji. Never output zero. No explanations. " +
        "Output ONLY kaomojis (text emoticons). NEVER output Unicode emoji like 😀 ❤️ 😢 — only ASCII/punctuation-based kaomojis. " +
        "You may use the examples below OR freestyle your own kaomoji — be creative and pick whichever fits the mood best. " +
        "Mood reference pools (just examples — feel free to invent new ones):\n" +
        "Happy / cheerful / positive: ^-^   ^^   ♡(>ᴗ•)   (✿◕‿◕)   (≧◡≦)\n" +
        "Love / affection / romantic: ♡   (♡˙︶˙♡)   ( ˘ ³˘)♥\n" +
        "Sad / disappointed / down: (ノ_<。)   ｡ﾟ･ (>﹏<) ･ﾟ｡   (╥﹏╥)\n" +
        "Embarrassed / shy / pain / 'ahhhh' / frustrated: ( 〃▽〃)   !!>_<!!   >_<   (x_x)⌒☆   (╯°□°)╯\n" +
        "Surprised / shocked: w(°ｏ°)w   (×_×)   Σ(°ロ°)";

    private async Task<string> AppendKaomojiAsync(string text)
    {
        var body = new JObject
        {
            ["reasoning_format"] = "hidden",
            ["temperature"] = 0.5,
            ["max_completion_tokens"] = 256,
            ["messages"] = new JArray
            {
                new JObject { ["role"] = "system", ["content"] = KaomojiOnlySystemPrompt },
                new JObject { ["role"] = "user", ["content"] = text }
            }
        };

        var (content, status, model) = await GroqChatAsync(_apiKey, body);
        if (content.Length == 0)
        {
            Log($"Kikitan XD: kaomoji error {status}");
            return "";
        }
        if (model == GroqFallbackModel) Log($"Kikitan XD: kaomoji via fallback model {model}");
        return content;
    }

    private async Task<string> TranslateAsync(string text, string source, string target)
    {
        var body = new JObject
        {
            ["reasoning_format"] = "hidden",
            ["temperature"] = 1,
            ["max_completion_tokens"] = 512,
            ["messages"] = new JArray
            {
                new JObject { ["role"] = "system", ["content"] = BuildTranslateSystemPrompt(_personality) },
                new JObject { ["role"] = "user", ["content"] = $"{source} | {target} | {text}" }
            }
        };

        var (content, status, model) = await GroqChatAsync(_apiKey, body);
        if (content.Length == 0)
        {
            Log($"Kikitan XD: translate error {status}");
            return "";
        }
        if (model == GroqFallbackModel) Log($"Kikitan XD: translate via fallback model {model}");
        return content;
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
        if (pad == 0) pad = 4;
        buf.AddRange(new byte[pad]);
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
    }
}
#endif
