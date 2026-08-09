using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VRCNext.Services;

public sealed class AvtrdbResolver
{
    public const int MaxBatch = 80;

    private const string Endpoint     = "https://api.avtrdb.com/v3/avatar/resolve";
    private const string RobotAvatar  = "avtr_c38a1615-5bf5-42b4-84eb-a8b6c37cbd11";
    private const int    MinIntervalMs = 1000;
    private const int    CoalesceMs    = 250;
    private const int    CacheLimit    = 4000;

    private readonly object _lock = new();
    private readonly Dictionary<string, List<TaskCompletionSource<JObject?>>> _waiting = new();
    private readonly Queue<string> _order = new();
    private readonly Dictionary<string, JObject?> _cache = new();
    private readonly Queue<string> _cacheOrder = new();
    private readonly Action<string>? _log;

    private Task? _worker;
    private static readonly HttpMethod QueryMethod = new("QUERY");
    private DateTime _lastRequestUtc = DateTime.MinValue;

    public AvtrdbResolver(Action<string>? log = null) => _log = log;

    public Task<JObject?> ResolveAsync(string fileId)
    {
        if (string.IsNullOrWhiteSpace(fileId)) return Task.FromResult<JObject?>(null);

        var tcs = new TaskCompletionSource<JObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (_cache.TryGetValue(fileId, out var hit))
                return Task.FromResult(hit);

            if (_waiting.TryGetValue(fileId, out var list)) list.Add(tcs);
            else { _waiting[fileId] = new List<TaskCompletionSource<JObject?>> { tcs }; _order.Enqueue(fileId); }

            if (_worker == null || _worker.IsCompleted) _worker = Task.Run(DrainAsync);
        }
        return tcs.Task;
    }

    public async Task<Dictionary<string, JObject?>> ResolveManyAsync(IEnumerable<string> fileIds)
    {
        var ids = fileIds.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct().ToList();
        var tasks = ids.Select(ResolveAsync).ToList();
        var results = await Task.WhenAll(tasks);
        var map = new Dictionary<string, JObject?>();
        for (int i = 0; i < ids.Count; i++) map[ids[i]] = results[i];
        return map;
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            await Task.Delay(CoalesceMs);

            List<string> batch;
            lock (_lock)
            {
                if (_order.Count == 0) { _worker = null; return; }
                batch = new List<string>(Math.Min(MaxBatch, _order.Count));
                while (batch.Count < MaxBatch && _order.Count > 0) batch.Add(_order.Dequeue());
            }

            var wait = MinIntervalMs - (int)(DateTime.UtcNow - _lastRequestUtc).TotalMilliseconds;
            if (wait > 0) await Task.Delay(wait);
            _lastRequestUtc = DateTime.UtcNow;

            Dictionary<string, JObject?> result;
            try { result = await PostBatchAsync(batch); }
            catch (Exception ex)
            {
                _log?.Invoke($"avtrdb resolve failed: {ex.Message}");
                result = new Dictionary<string, JObject?>();
            }

            lock (_lock)
            {
                foreach (var id in batch)
                {
                    result.TryGetValue(id, out var data);
                    Remember(id, data);
                    if (!_waiting.Remove(id, out var listeners)) continue;
                    foreach (var t in listeners) t.TrySetResult(data);
                }
            }
        }
    }

    private void Remember(string fileId, JObject? data)
    {
        if (_cache.ContainsKey(fileId)) return;
        _cache[fileId] = data;
        _cacheOrder.Enqueue(fileId);
        while (_cacheOrder.Count > CacheLimit && _cacheOrder.TryDequeue(out var old))
            _cache.Remove(old);
    }

    private async Task<Dictionary<string, JObject?>> PostBatchAsync(List<string> fileIds)
    {
        var payload = JsonConvert.SerializeObject(new { file_ids = fileIds });

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestVersion = System.Net.HttpVersion.Version20;
        client.DefaultVersionPolicy  = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower;
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", $"https://{AppInfo.Website}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        var body = await SendAsync(client, payload);
        if (string.IsNullOrWhiteSpace(body)) return new Dictionary<string, JObject?>();

        var map = new Dictionary<string, JObject?>();
        if (JToken.Parse(body) is not JObject root) return map;
        foreach (var prop in root.Properties())
        {
            if (prop.Value is not JObject o) { map[prop.Name] = null; continue; }
            map[prop.Name] = o["vrc_id"]?.ToString() == RobotAvatar ? null : o;
        }
        return map;
    }

    private async Task<string> SendAsync(HttpClient client, string payload)
    {
        using var req = new HttpRequestMessage(QueryMethod, Endpoint)
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
        };
        var resp = await client.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            _log?.Invoke($"avtrdb resolve [{(int)resp.StatusCode}]: {text[..Math.Min(160, text.Length)]}");
            return "";
        }
        return text.TrimStart().StartsWith("<") ? "" : text;
    }
}
