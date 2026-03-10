using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace VRCNext.Services;

/// <summary>Event args for typed friend WebSocket events.</summary>
public class FriendEventArgs : EventArgs
{
    public string  UserId   { get; init; } = "";
    public JObject? User    { get; init; }
    public string  Location { get; init; } = "";
    public string  WorldId  { get; init; } = "";
    public string  Platform { get; init; } = "";
}

/// <summary>Event args for WebSocket notification events.</summary>
public class NotificationEventArgs : EventArgs
{
    /// <summary>"notification", "notification-v2", "notification-v2-update", "notification-v2-delete"</summary>
    public string  WsType { get; init; } = "";
    /// <summary>Parsed content object from the WS message. Null if parsing failed.</summary>
    public JObject? Data  { get; init; }
}

/// <summary>
/// Persistent WebSocket connection to wss://pipeline.vrchat.cloud/
/// Fires events for friend changes, new notifications, and own location changes.
/// </summary>
internal sealed class VRChatWebSocketService : IDisposable
{
    // Public events

    /// <summary>Friend state changed (status/location/bio). Update store from WS data — no REST needed.</summary>
    public event EventHandler? FriendsChanged;

    /// <summary>Friend was added or removed. Requires a full REST refresh of the friend list.</summary>
    public event EventHandler? FriendListChanged;

    /// <summary>A new notification arrived via WebSocket. Data contains the full notification payload.</summary>
    public event EventHandler<NotificationEventArgs>? NotificationArrived;

    /// <summary>Own location changed (contains the new instance location string).</summary>
    public event EventHandler<string>? OwnLocationChanged;

    /// <summary>Own profile updated via user-update event (status, bio, statusDescription, etc.).</summary>
    public event EventHandler<JObject>? OwnUserUpdated;

    /// <summary>WebSocket connected successfully.</summary>
    public event EventHandler? Connected;

    /// <summary>WebSocket disconnected (will attempt reconnect).</summary>
    public event EventHandler? Disconnected;

    /// <summary>Connection error message for logging.</summary>
    public event EventHandler<string>? ConnectError;

    /// <summary>A friend moved to a different world.</summary>
    public event EventHandler<FriendEventArgs>? FriendLocationChanged;

    /// <summary>A friend went offline.</summary>
    public event EventHandler<FriendEventArgs>? FriendWentOffline;

    /// <summary>A friend came online.</summary>
    public event EventHandler<FriendEventArgs>? FriendWentOnline;

    /// <summary>A friend's profile data changed (status, bio, etc.).</summary>
    public event EventHandler<FriendEventArgs>? FriendUpdated;

    // State

    private string _authToken = "";
    private string _tfaToken  = "";

    /// <summary>
    /// Optional delegate called before every connect attempt to get fresh auth cookies.
    /// Avoids using a stale token when the VRChat session rotates mid-run.
    /// </summary>
    private Func<(string auth, string tfa)>? _getTokens;

    private CancellationTokenSource _cts = new();
    private Task? _connectTask;
    private bool _running;
    private bool _disposed;

    // Debounce: collect rapid friend events, fire once after a short delay
    private System.Threading.Timer? _friendsDebounce;
    private const int FriendsDebounceMs = 500;

    // Heartbeat watchdog: if no data is received for this long, the TCP connection
    // is considered silently dead and the loop forces a reconnect.
    private long _lastReceiveTicks = DateTime.UtcNow.Ticks; // written/read via Interlocked
    private const int HeartbeatTimeoutSec = 75; // VRChat pipeline sends events regularly

    // Public API

    /// <summary>
    /// Start (or restart) the WebSocket loop.
    /// Pass <paramref name="getTokens"/> so fresh cookies are used on every internal reconnect.
    /// </summary>
    public void Start(string authToken, string tfaToken = "",
                      Func<(string auth, string tfa)>? getTokens = null)
    {
        var prevTask = _connectTask;
        Stop();
        _cts.Dispose();
        _authToken  = authToken;
        _tfaToken   = tfaToken;
        _getTokens  = getTokens;
        _running    = true;
        _cts        = new CancellationTokenSource();
        _connectTask = Task.Run(() => ConnectLoopAsync(_cts.Token));
        // Observe previous task so it doesn't become an unobserved faulted task
        prevTask?.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>Stop and disconnect. Does not dispose.</summary>
    public void Stop()
    {
        _running = false;
        _cts.Cancel();
        _friendsDebounce?.Dispose();
        _friendsDebounce = null;
    }

    // Connection loop

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        int delaySec = 1;

        while (_running && !ct.IsCancellationRequested)
        {
            // Refresh tokens before each attempt so a rotated session cookie is picked up
            if (_getTokens != null)
            {
                try
                {
                    var (a, t) = _getTokens();
                    if (!string.IsNullOrEmpty(a))
                    {
                        _authToken = a;
                        _tfaToken  = t ?? "";
                    }
                }
                catch { /* ignore, use last known tokens */ }
            }

            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("User-Agent", "VRCNext/1.01.0 contact@vrcnext.app");

                // Send auth cookies in the WebSocket handshake headers
                var jar = new CookieContainer();
                var pipeUri = new Uri("https://pipeline.vrchat.cloud");
                jar.Add(pipeUri, new Cookie("auth", _authToken));
                if (!string.IsNullOrEmpty(_tfaToken))
                    jar.Add(pipeUri, new Cookie("twoFactorAuth", _tfaToken));
                ws.Options.Cookies = jar;

                var uri = new Uri($"wss://pipeline.vrchat.cloud/?authToken={Uri.EscapeDataString(_authToken)}");
                await ws.ConnectAsync(uri, ct);

                delaySec = 1; // reset backoff on successful connect
                Interlocked.Exchange(ref _lastReceiveTicks, DateTime.UtcNow.Ticks);
                Connected?.Invoke(this, EventArgs.Empty);

                // Run receive loop and watchdog concurrently.
                // Watchdog aborts ws if no data arrives for HeartbeatTimeoutSec seconds,
                // which causes ReceiveLoopAsync to exit so the outer loop can reconnect.
                using var wdCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var receiveTask = ReceiveLoopAsync(ws, ct);
                var watchdogTask = WatchdogAsync(ws, wdCts.Token);

                await Task.WhenAny(receiveTask, watchdogTask);
                wdCts.Cancel(); // stop watchdog when receive loop ends (or vice-versa)

                // Observe any exception to prevent UnobservedTaskException
                try { await receiveTask; } catch { }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                ConnectError?.Invoke(this, ex.Message);
            }

            if (!_running || ct.IsCancellationRequested) break;

            Disconnected?.Invoke(this, EventArgs.Empty);

            try { await Task.Delay(TimeSpan.FromSeconds(delaySec), ct); }
            catch (OperationCanceledException) { break; }

            delaySec = Math.Min(delaySec * 2, 30);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            sb.Clear();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            // Update heartbeat timestamp on every received message
            Interlocked.Exchange(ref _lastReceiveTicks, DateTime.UtcNow.Ticks);

            HandleMessage(sb.ToString());
        }
    }

    /// <summary>
    /// Runs alongside ReceiveLoopAsync. Checks every 30 s whether any data was received
    /// within HeartbeatTimeoutSec. If not, the TCP connection is silently dead and we
    /// abort the socket to force an immediate reconnect.
    /// </summary>
    private async Task WatchdogAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            try { await Task.Delay(30_000, ct); }
            catch (OperationCanceledException) { return; }

            var lastTicks = Interlocked.Read(ref _lastReceiveTicks);
            var elapsed   = DateTime.UtcNow - new DateTime(lastTicks);

            if (elapsed.TotalSeconds > HeartbeatTimeoutSec)
            {
                ConnectError?.Invoke(this, $"No data for {(int)elapsed.TotalSeconds}s — reconnecting");
                try { ws.Abort(); } catch { }
                return;
            }
        }
    }

    // Message routing

    private void HandleMessage(string json)
    {
        try
        {
            var msg = JObject.Parse(json);
            var type = msg["type"]?.Value<string>() ?? "";

            // VRChat double-encodes the content field as a JSON string
            var contentStr = msg["content"]?.Value<string>() ?? "{}";

            switch (type)
            {
                case "friend-location":
                    DebounceFriendsRefresh();
                    try
                    {
                        var c = JObject.Parse(contentStr);
                        var uid = c["userId"]?.Value<string>() ?? "";
                        var loc = c["location"]?.Value<string>() ?? "";
                        var wid = c["worldId"]?.Value<string>() ?? (loc.Contains(':') ? loc.Split(':')[0] : "");
                        var usr = c["user"] as JObject;
                        var plt = c["platform"]?.Value<string>() ?? usr?["platform"]?.Value<string>() ?? "";
                        FriendLocationChanged?.Invoke(this, new FriendEventArgs { UserId = uid, User = usr, Location = loc, WorldId = wid, Platform = plt });
                    }
                    catch { }
                    break;

                case "friend-offline":
                    DebounceFriendsRefresh();
                    try
                    {
                        var c = JObject.Parse(contentStr);
                        var uid = c["userId"]?.Value<string>() ?? "";
                        var usr = c["user"] as JObject;
                        FriendWentOffline?.Invoke(this, new FriendEventArgs { UserId = uid, User = usr });
                    }
                    catch { }
                    break;

                case "friend-online":
                    try
                    {
                        var c = JObject.Parse(contentStr);
                        var uid = c["userId"]?.Value<string>() ?? "";
                        var loc = c["location"]?.Value<string>() ?? "";
                        var plt = c["platform"]?.Value<string>() ?? "";
                        var usr = c["user"] as JObject;
                        FriendWentOnline?.Invoke(this, new FriendEventArgs { UserId = uid, User = usr, Location = loc, Platform = plt });
                    }
                    catch { }
                    break;

                case "friend-update":
                    try
                    {
                        var c = JObject.Parse(contentStr);
                        var uid = c["userId"]?.Value<string>() ?? "";
                        var usr = c["user"] as JObject;
                        FriendUpdated?.Invoke(this, new FriendEventArgs { UserId = uid, User = usr });
                    }
                    catch { }
                    break;

                case "friend-active":
                    try
                    {
                        var c = JObject.Parse(contentStr);
                        // VRChat uses lowercase "userid" in friend-active (unlike other events)
                        var uid = c["userid"]?.Value<string>() ?? c["userId"]?.Value<string>() ?? "";
                        var plt = c["platform"]?.Value<string>() ?? "";
                        var usr = c["user"] as JObject;
                        FriendWentOnline?.Invoke(this, new FriendEventArgs { UserId = uid, User = usr, Platform = plt });
                    }
                    catch { }
                    break;

                case "friend-add":
                case "friend-delete":
                    FriendListChanged?.Invoke(this, EventArgs.Empty);
                    break;

                case "notification":
                case "notification-v2":
                case "notification-v2-update":
                case "notification-v2-delete":
                    try
                    {
                        var c = JObject.Parse(contentStr);
                        NotificationArrived?.Invoke(this, new NotificationEventArgs { WsType = type, Data = c });
                    }
                    catch
                    {
                        NotificationArrived?.Invoke(this, new NotificationEventArgs { WsType = type });
                    }
                    break;

                case "user-location":
                    try
                    {
                        var content = JObject.Parse(contentStr);
                        var location = content["location"]?.Value<string>() ?? "";
                        if (!string.IsNullOrEmpty(location))
                            OwnLocationChanged?.Invoke(this, location);
                    }
                    catch { }
                    break;

                case "user-update":
                    try
                    {
                        var content = JObject.Parse(contentStr);
                        var userObj = content["user"] as JObject;
                        if (userObj != null)
                            OwnUserUpdated?.Invoke(this, userObj);
                    }
                    catch { }
                    break;
            }
        }
        catch { /* ignore malformed messages */ }
    }

    private void DebounceFriendsRefresh()
    {
        if (_friendsDebounce == null)
            _friendsDebounce = new System.Threading.Timer(
                _ => FriendsChanged?.Invoke(this, EventArgs.Empty),
                null, FriendsDebounceMs, Timeout.Infinite);
        else
            _friendsDebounce.Change(FriendsDebounceMs, Timeout.Infinite);
    }

    // IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts.Dispose();
    }
}
