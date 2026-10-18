using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace VRCNext.Services.Helpers;

public static class EdgeTtsService
{
    private const string Token = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private const string Chromium = "143.0.3650.75";
    private const string ChromiumMajor = "143";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/" + ChromiumMajor + ".0.0.0 Safari/537.36 Edg/" + ChromiumMajor + ".0.0.0";

    public static Action<string>? Log;

    private static List<JObject>? _voiceCache;
    private static DateTime _voiceCacheAt;

    private static string SecMsGec() => SecMsGec(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    private static string SecMsGec(long unixSeconds)
    {
        long seconds = unixSeconds + 11644473600L;
        seconds -= seconds % 300L;
        long ticks = seconds * 10_000_000L;
        var data = Encoding.ASCII.GetBytes(ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + Token);
        var digest = GecSha256(data);
        var (cs0, cs1) = GecCs64(data, digest);
        var cs = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(cs, cs0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(cs.AsSpan(4), cs1);
        return Convert.ToHexString(cs) + "-" + Convert.ToHexString(digest);
    }

    private static string Query(string basePath) =>
        $"{basePath}?TrustedClientToken={Token}&Sec-MS-GEC={SecMsGec()}&Sec-MS-GEC-Version=2-{Chromium}";

    private static readonly uint[] GecShaInitial =
    {
        0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A, 0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19,
    };
    private static readonly uint[] GecShaState =
    {
        0xFD9FBEA2, 0x91243CE7, 0xBE499094, 0xCD2E1E0A, 0x6C006F49, 0x04BD35AB, 0xFEB0A7E4, 0x03EFFBD2,
    };
    private static readonly uint[] GecShaSchedule =
    {
        0xEB24BF9E, 0x1558691F, 0xA0C09EE2, 0x0CFA32BA, 0xA622EE8C, 0xD1DD9491, 0xC0DD25A0,
        0xF9E70AB6, 0xF0C31573, 0x7058CE4E, 0x7188E606, 0x00E5AB33, 0xD258B060, 0x72766564,
    };
    private static readonly uint[] GecShaRound =
    {
        0x428A2F98, 0x71374491, 0xB5C0FBCF, 0xE9B5DBA5, 0x3956C25B, 0x59F111F1, 0x923F82A4, 0xAB1C5ED5,
        0xD807AA98, 0x12835B01, 0x243185BE, 0x550C7DC3, 0x72BE5D74, 0x80DEB1FE, 0x9BDC06A7, 0xC19BF174,
        0xE49B69C1, 0xEFBE4786, 0x0FC19DC6, 0x240CA1CC, 0x2DE92C6F, 0x4A7484AA, 0x5CB0A9DC, 0x76F988DA,
        0x983E5152, 0xA831C66D, 0xB00327C8, 0xBF597FC7, 0xC6E00BF3, 0xD5A79147, 0x06CA6351, 0x14292967,
        0x27B70A85, 0x2E1B2138, 0x4D2C6DFC, 0x53380D13, 0x650A7354, 0x766A0ABB, 0x81C2C92E, 0x92722C85,
        0xA2BFE8A1, 0xA81A664B, 0xC24B8B70, 0xC76C51A3, 0xD192E819, 0xD6990624, 0xF40E3585, 0x106AA070,
        0x19A4C116, 0x1E376C08, 0x2748774C, 0x34B0BCB5, 0x391C0CB3, 0x4ED8AA4A, 0x5B9CCA4F, 0x682E6FF3,
        0x748F82EE, 0x78A5636F, 0x84C87814, 0x8CC70208, 0x90BEFFFA, 0xA4506CEB, 0xBEF9A3F7, 0xC67178F2,
    };
    private const uint GecShaAdjustment = 0xC9DC06A7;
    private const uint GecSigma0Adjustment = 0x059C0B80;
    private const uint GecSigma1Adjustment = 0x000B92C0;
    private static readonly uint[] GecShaBlock = BuildGecShaBlock();

    private static uint[] BuildGecShaBlock()
    {
        var k = (uint[])GecShaRound.Clone();
        k[14] = GecShaAdjustment;
        return k;
    }

    private static readonly (uint coef, uint offset)[] GecCs64Coefficients =
    {
        (0xE82E91B1, 0xB32700D0), (0x44DE24C6, 0x88DFF51D), (0x3BEB65BC, 0x29514038),
        (0xC89C5933, 0xB328CBDF), (0x8D1EC211, 0xD405B4D5), (0x314EF5D5, 0x26E55F3C),
        (0xE92491B1, 0x607801C6), (0xF0A5B9D5, 0x82B2C1FB), (0x06064CF3, 0xFA51A3DA),
        (0xDA79FA60, 0x207CC464), (0xBED20560, 0x3C3C74F3), (0x89F9C9F8, 0xD2D14B61),
        (0x35FA38C6, 0xBB04A2C1), (0xCB49E3C6, 0xC5626167),
    };
    private const uint GecCs64Factor = 0x82893933;
    private const uint GecCs64Adjustment0 = 0xF2F90B1A;
    private const uint GecCs64Adjustment1 = 0x0B1A0000;

    private static uint Rol16(uint v) => (v << 16) | (v >> 16);
    private static uint Rotr(uint v, int n) => (v >> n) | (v << (32 - n));
    private static uint Sigma0(uint v) => Rotr(v, 7) ^ Rotr(v, 18) ^ (v >> 3);
    private static uint Sigma1(uint v) => Rotr(v, 17) ^ Rotr(v, 19) ^ (v >> 10);

    private static void GecExpand(uint[] w, int first)
    {
        for (int i = first; i < 64; i++)
            w[i] = Sigma1(w[i - 2]) + w[i - 7] + Sigma0(w[i - 15]) + w[i - 16];
    }

    private static uint[] GecRounds(uint[] state, uint[] w, int firstRound)
    {
        var k = firstRound == 14 ? GecShaBlock : GecShaRound;
        uint a = state[0], b = state[1], c = state[2], d = state[3], e = state[4], f = state[5], g = state[6], h = state[7];
        for (int i = firstRound; i < 64; i++)
        {
            uint t1 = h + (Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25)) + (g ^ (e & (f ^ g))) + k[i] + w[i];
            uint t2 = (Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22)) + ((a & b) | (c & (a | b)));
            h = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
        }
        return new[] { a, b, c, d, e, f, g, h };
    }

    private static uint[] GecShaInit(byte[] data)
    {
        var w = new uint[64];
        w[14] = ((uint)data[0] << 16) | ((uint)data[1] << 8) | data[2];
        w[15] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(3, 4));
        for (int i = 16; i < 30; i++)
        {
            uint v = GecShaSchedule[i - 16];
            if (i - 2 >= 14) v += i == 16 ? Sigma1(w[i - 2]) ^ GecSigma1Adjustment : Sigma1(w[i - 2]);
            if (i - 7 >= 14) v += w[i - 7];
            if (i - 15 >= 14) v += i == 29 ? Sigma0(w[i - 15]) ^ GecSigma0Adjustment : Sigma0(w[i - 15]);
            w[i] = v;
        }
        w[30] = Sigma1(w[28]) + w[23] + Sigma0(w[15]) + w[14] + GecShaAdjustment - GecShaRound[14];
        GecExpand(w, 31);
        var working = GecRounds(GecShaState, w, 14);
        var result = new uint[8];
        for (int i = 0; i < 8; i++) result[i] = GecShaInitial[i] + working[i];
        return result;
    }

    private static byte[] GecSha256(byte[] data)
    {
        int length = 57 + data.Length;
        int pad = ((55 - length) % 64 + 64) % 64;
        var padded = new byte[data.Length - 7 + 1 + pad + 8];
        Array.Copy(data, 7, padded, 0, data.Length - 7);
        padded[data.Length - 7] = 0x80;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(padded.AsSpan(padded.Length - 8), (ulong)length * 8);
        var state = GecShaInit(data);
        var w = new uint[64];
        for (int off = 0; off < padded.Length; off += 64)
        {
            for (int i = 0; i < 16; i++)
                w[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(padded.AsSpan(off + i * 4, 4));
            GecExpand(w, 16);
            var working = GecRounds(state, w, 0);
            for (int i = 0; i < 8; i++) state[i] += working[i];
        }
        var digest = new byte[32];
        for (int i = 0; i < 8; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(digest.AsSpan(i * 4), state[i]);
        return digest;
    }

    private static (uint, uint) GecCs64(byte[] data, byte[] digest)
    {
        uint keyA = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(digest.AsSpan(0, 4)) | 1;
        uint keyB = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(digest.AsSpan(4, 4)) | 1;
        uint factorA = keyA * GecCs64Factor;
        uint factorB = keyB * GecCs64Factor;

        var terms = new List<uint>();
        var offsets = new List<uint>();
        for (int i = 0; i < GecCs64Coefficients.Length; i++)
        {
            terms.Add(GecCs64Coefficients[i].coef * (i % 2 == 0 ? factorA : factorB));
            offsets.Add(GecCs64Coefficients[i].offset);
        }

        var src = new byte[1 + data.Length];
        Array.Copy(data, 0, src, 1, data.Length);
        int wordCount = (src.Length + 3) / 4;
        if (wordCount % 2 != 0) wordCount++;
        var bytes = new byte[wordCount * 4];
        Array.Copy(src, bytes, src.Length);
        for (int i = 0; i < wordCount; i++)
        {
            uint word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4, 4));
            bool even = i % 2 == 0;
            uint term = word * (even ? keyA : keyB);
            uint offset = Rol16(word) * (even ? 0xEF0569FBu : 0xC31713DBu);
            if (i == 0)
            {
                term += GecCs64Adjustment0 * factorA;
                offset += GecCs64Adjustment1;
            }
            terms.Add(term);
            offsets.Add(offset);
        }

        uint swapAcc = 0, swapLast = 0, state = 0, acc = 0;
        for (int i = 0; i < terms.Count; i += 2)
        {
            uint x = terms[i], y = terms[i + 1];
            uint t = Rol16(offsets[i] + x);
            uint q = t * 0x76F50F34;
            swapAcc += Rol16(q) * 0xC3EFEA97 + q;
            t = Rol16(offsets[i + 1] + y);
            q = t * 0x1881CE3C;
            swapLast = Rol16(q) * 0x35BD1EC9 + q;
            swapAcc += swapLast;

            state = state * keyA + x;
            state = Rol16(state) * 0xCF98B111;
            state = Rol16(state) * 0x87085B9F;
            state = Rol16(state) * 0x12CEB96D;
            state = Rol16(state) * 0x257E1D83;
            acc += state;
            t = state * keyB + y;
            t = Rol16(t) * 0xA27416F5;
            t = Rol16(t) * 0xD38396FF;
            state = Rol16(Rol16(t) * 0x7C932B89) * 0xBFA49F69;
            acc += state;
        }
        return (swapLast ^ state, swapAcc ^ acc);
    }

    public static async Task<List<JObject>> GetVoicesAsync()
    {
        if (_voiceCache != null && (DateTime.UtcNow - _voiceCacheAt).TotalHours < 12) return _voiceCache;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestVersion = System.Net.HttpVersion.Version20;
            http.DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower;
            http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
            var url = Query("https://speech.platform.bing.com/consumer/speech/synthesize/readaloud/voices/list");
            var json = await http.GetStringAsync(url);
            _voiceCache = JArray.Parse(json).OfType<JObject>().ToList();
            _voiceCacheAt = DateTime.UtcNow;
            return _voiceCache;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[TTS] Edge voice list failed: {ex.Message}");
            return _voiceCache ?? new List<JObject>();
        }
    }

    public static async Task<byte[]> SynthesizeAsync(string text, string voice, int rate, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(voice)) voice = "en-US-AriaNeural";

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var lct = linkedCts.Token;

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("User-Agent", UserAgent);
        ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        ws.Options.SetRequestHeader("Pragma", "no-cache");
        ws.Options.SetRequestHeader("Cache-Control", "no-cache");
        ws.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");

        var uri = new Uri(Query("wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1")
                          + "&ConnectionId=" + Guid.NewGuid().ToString("N"));
        await ws.ConnectAsync(uri, lct);

        string stamp = DateTime.UtcNow.ToString("R");
        await SendTextAsync(ws,
            $"X-Timestamp:{stamp}\r\n" +
            "Content-Type:application/json; charset=utf-8\r\n" +
            "Path:speech.config\r\n\r\n" +
            "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{" +
            "\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"}," +
            "\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}", lct);

        int pct = Math.Clamp(rate, -10, 10) * 10;
        string ssml =
            "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>" +
            $"<voice name='{voice}'><prosody rate='{(pct >= 0 ? "+" : "")}{pct}%' pitch='+0Hz'>" +
            Escape(text) + "</prosody></voice></speak>";

        await SendTextAsync(ws,
            $"X-RequestId:{Guid.NewGuid():N}\r\n" +
            "Content-Type:application/ssml+xml\r\n" +
            $"X-Timestamp:{stamp}Z\r\n" +
            "Path:ssml\r\n\r\n" + ssml, lct);

        using var audio = new MemoryStream();
        var buf = new byte[16 * 1024];
        while (ws.State == WebSocketState.Open && !lct.IsCancellationRequested)
        {
            using var frame = new MemoryStream();
            WebSocketReceiveResult res;
            do
            {
                res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), lct);
                if (res.MessageType == WebSocketMessageType.Close) break;
                frame.Write(buf, 0, res.Count);
            } while (!res.EndOfMessage);

            if (res.MessageType == WebSocketMessageType.Close) break;
            var data = frame.ToArray();

            if (res.MessageType == WebSocketMessageType.Text)
            {
                if (Encoding.UTF8.GetString(data).Contains("Path:turn.end")) break;
                continue;
            }

            if (data.Length < 2) continue;
            int headerLen = (data[0] << 8) | data[1];
            int start = 2 + headerLen;
            if (start < data.Length) audio.Write(data, start, data.Length - start);
        }

        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        return audio.ToArray();
    }

    private static Task SendTextAsync(ClientWebSocket ws, string msg, CancellationToken ct) =>
        ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)),
                     WebSocketMessageType.Text, true, ct);

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
         .Replace("\"", "&quot;").Replace("'", "&apos;");
}
