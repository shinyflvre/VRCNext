#if WINDOWS
using DiscordRPC;
using DiscordRPC.Logging;
#endif

namespace VRCNext.Services;

#if !WINDOWS
public class DiscordPresenceService : IDisposable
{
    private readonly string _clientId;
    private System.Net.Sockets.Socket? _socket;
    private readonly object _lock = new();
    private volatile bool _connected;
    private readonly object _sendLock = new();

    public bool IsConnected => _connected && _socket != null;
    public event Action<string>? OnLog;

    public DiscordPresenceService(string clientId) => _clientId = clientId;

    public bool Connect()
    {
        lock (_lock)
        {
            if (IsConnected) return true;
            DisconnectCore();

            var failures = new List<string>();
            foreach (var path in GetIpcPaths())
            {
                System.Net.Sockets.Socket? socket = null;
                try
                {
                    socket = new System.Net.Sockets.Socket(
                        System.Net.Sockets.AddressFamily.Unix,
                        System.Net.Sockets.SocketType.Stream,
                        System.Net.Sockets.ProtocolType.Unspecified)
                    {
                        ReceiveTimeout = 3000,
                        SendTimeout = 3000,
                    };
                    socket.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
                    _socket = socket;
                    SendFrame(0, System.Text.Json.JsonSerializer.Serialize(new { v = 1, client_id = _clientId }));
                    var (opcode, payload) = ReceiveFrame(socket);
                    using var response = System.Text.Json.JsonDocument.Parse(payload);
                    var ready = response.RootElement.TryGetProperty("evt", out var evt)
                        && evt.GetString() == "READY";
                    if (opcode != 1 || !ready)
                    {
                        var detail = response.RootElement.TryGetProperty("data", out var data)
                            ? data.ToString()
                            : payload;
                        throw new InvalidOperationException($"Discord RPC handshake rejected: {detail}");
                    }
                    socket.ReceiveTimeout = 0;
                    _connected = true;
                    _ = Task.Run(() => ReadLoop(socket));
                    OnLog?.Invoke("[Discord] RPC connected");
                    return true;
                }
                catch (Exception ex)
                {
                    failures.Add($"{path}: {ex.Message}");
                    socket?.Dispose();
                    DisconnectCore();
                }
            }
            OnLog?.Invoke(failures.Count == 0
                ? "[Discord] Connect failed: no Discord IPC socket found. Start the Discord desktop client and check its Flatpak/Snap permissions."
                : $"[Discord] Connect failed: {string.Join(" | ", failures)}");
            return false;
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            try
            {
                if (IsConnected) SendCommand("SET_ACTIVITY", new { pid = Environment.ProcessId, activity = (object?)null });
            }
            catch (Exception ex) { OnLog?.Invoke($"[Discord] Clear presence failed: {ex.Message}"); }
            DisconnectCore();
        }
    }

    public void UpdatePresence(string worldName, string instanceState, string worldImageUrl, string status, DateTime joinedAt, string? joinUrl = null)
    {
        var (smallKey, smallText) = status switch
        {
            "join me" => ("join_me", "Join Me"),
            "busy" => ("busy", "Do Not Disturb"),
            "ask me" => ("ask_me", "Ask Me"),
            "offline" => ("offline", "Offline"),
            _ => ("online", "Online"),
        };
        var assets = new Dictionary<string, string> { ["small_image"] = smallKey, ["small_text"] = smallText, ["large_text"] = worldName };
        if (!string.IsNullOrEmpty(worldImageUrl)) assets["large_image"] = worldImageUrl;
        var activity = new Dictionary<string, object?>
        {
            ["details"] = worldName,
            ["state"] = instanceState,
            ["assets"] = assets,
            ["timestamps"] = new { start = new DateTimeOffset(joinedAt.ToUniversalTime()).ToUnixTimeSeconds() },
        };
        if (!string.IsNullOrEmpty(joinUrl))
            activity["buttons"] = new[] { new { label = "Join Instance", url = joinUrl } };

        lock (_lock)
        {
            try { SendCommand("SET_ACTIVITY", new { pid = Environment.ProcessId, activity }); }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Discord] UpdatePresence failed: {ex.Message}");
                DisconnectCore();
            }
        }
    }

    public void ClearPresence()
    {
        lock (_lock)
        {
            try { SendCommand("SET_ACTIVITY", new { pid = Environment.ProcessId, activity = (object?)null }); }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Discord] Clear presence failed: {ex.Message}");
                DisconnectCore();
            }
        }
    }

    private void SendCommand(string command, object args)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            cmd = command,
            args,
            nonce = Guid.NewGuid().ToString("N"),
        });
        SendFrame(1, payload);
    }

    private void SendFrame(int opcode, string payload)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
        var frame = new byte[8 + bytes.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), opcode);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), bytes.Length);
        bytes.CopyTo(frame, 8);
        lock (_sendLock)
        {
            var socket = _socket ?? throw new InvalidOperationException("Discord IPC socket is not connected");
            var sent = 0;
            while (sent < frame.Length) sent += socket.Send(frame, sent, frame.Length - sent, System.Net.Sockets.SocketFlags.None);
        }
    }

    private static (int Opcode, string Payload) ReceiveFrame(System.Net.Sockets.Socket socket)
    {
        var header = ReceiveExact(socket, 8);
        var opcode = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (length < 0 || length > 1024 * 1024) throw new InvalidOperationException("Invalid Discord IPC response length");
        return (opcode, System.Text.Encoding.UTF8.GetString(ReceiveExact(socket, length)));
    }

    private static byte[] ReceiveExact(System.Net.Sockets.Socket socket, int length)
    {
        var result = new byte[length];
        var received = 0;
        while (received < length)
        {
            var count = socket.Receive(result, received, length - received, System.Net.Sockets.SocketFlags.None);
            if (count == 0) throw new System.IO.IOException("Discord closed the IPC connection");
            received += count;
        }
        return result;
    }

    private void ReadLoop(System.Net.Sockets.Socket socket)
    {
        try
        {
            while (_connected && ReferenceEquals(socket, _socket))
            {
                var (opcode, payload) = ReceiveFrame(socket);
                if (opcode == 2)
                    throw new System.IO.IOException("Discord closed the IPC connection");
                if (opcode == 3)
                {
                    SendFrame(4, payload);
                    continue;
                }

                using var message = System.Text.Json.JsonDocument.Parse(payload);
                var root = message.RootElement;
                if (root.TryGetProperty("evt", out var evt) && evt.GetString() == "ERROR")
                {
                    var error = root.TryGetProperty("data", out var data) ? data.ToString() : payload;
                    OnLog?.Invoke($"[Discord] RPC error: {error}");
                }
            }
        }
        catch (Exception ex)
        {
            if (_connected && ReferenceEquals(socket, _socket))
            {
                OnLog?.Invoke($"[Discord] IPC connection lost: {ex.Message}");
                _connected = false;
                DisconnectCore();
            }
        }
    }

    private IEnumerable<string> GetIpcPaths()
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        var configured = Environment.GetEnvironmentVariable("DISCORD_IPC_PATH");
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var uid = getuid();
        var uidPath = $"/run/user/{uid}";
        var searchDirectories = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(runtimeDir)) searchDirectories.Add(runtimeDir);
        searchDirectories.Add(uidPath);
        searchDirectories.Add(System.IO.Path.GetTempPath());
        searchDirectories.Add("/tmp");
        foreach (var appId in new[] { "com.discordapp.Discord", "com.discordapp.DiscordCanary", "com.discordapp.DiscordPTB" })
        {
            if (!string.IsNullOrWhiteSpace(runtimeDir))
                searchDirectories.Add(System.IO.Path.Combine(runtimeDir, "app", appId));
            searchDirectories.Add(System.IO.Path.Combine(uidPath, "app", appId));
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            searchDirectories.Add(System.IO.Path.Combine(home, ".var", "app", appId, "config"));
        }

        foreach (var directory in searchDirectories)
        {
            try
            {
                if (!Directory.Exists(directory)) continue;
                foreach (var path in Directory.EnumerateFileSystemEntries(directory, "discord-ipc-*"))
                    candidates.Add(path);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                OnLog?.Invoke($"[Discord] Could not inspect IPC directory '{directory}': {ex.Message}");
            }
        }

        return candidates;
    }

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern uint getuid();

    private void DisconnectCore()
    {
        _connected = false;
        try { _socket?.Dispose(); } catch { }
        _socket = null;
    }

    public void Dispose() => Disconnect();
}
#else
public class DiscordPresenceService : IDisposable
{
    private DiscordRpcClient? _client;
    private readonly string _clientId;
    private bool _disposed;

    public bool IsConnected => _client?.IsInitialized == true;

    public event Action<string>? OnLog;
    private void Log(string msg) => OnLog?.Invoke(msg);

    public DiscordPresenceService(string clientId)
    {
        _clientId = clientId;
    }

    public bool Connect()
    {
        try
        {
            if (_client?.IsInitialized == true) return true;
            _client?.Dispose();
            _client = new DiscordRpcClient(_clientId)
            {
                Logger = new NullLogger()
            };
            _client.OnError        += (_, e)  => Log($"[Discord] Error: {e.Message}");
            _client.OnConnectionFailed += (_, _) => Log("[Discord] Connection failed");
            _client.OnReady        += (_, e)  => Log($"[Discord] Ready: {e.User.Username}");
            _client.Initialize();
            Log("[Discord] RPC connected");
            return true;
        }
        catch (Exception ex)
        {
            Log($"[Discord] Connect failed: {ex.Message}");
            return false;
        }
    }

    public void Disconnect()
    {
        try
        {
            if (_client?.IsInitialized == true)
            {
                _client.ClearPresence();
                _client.Invoke();
            }
            _client?.Dispose();
        }
        catch { }
        _client = null;
        Log("[Discord] Disconnected");
    }

    public void UpdatePresence(string worldName, string instanceState, string worldImageUrl, string status, DateTime joinedAt, string? joinUrl = null)
    {
        if (_client?.IsInitialized != true) return;
        try
        {
            var (smallKey, smallText) = status switch
            {
                "join me"  => ("join_me",  "Join Me"),
                "busy"     => ("busy",     "Do Not Disturb"),
                "ask me"   => ("ask_me",   "Ask Me"),
                "offline"  => ("offline",  "Offline"),
                _          => ("online",   "Online"),
            };

            var assets = new Assets
            {
                LargeImageText = worldName,
                SmallImageKey  = smallKey,
                SmallImageText = smallText,
            };

            if (!string.IsNullOrEmpty(worldImageUrl))
                assets.LargeImageKey = worldImageUrl;

            DiscordRPC.Button[]? buttons = null;
            if (!string.IsNullOrEmpty(joinUrl))
                buttons = [new DiscordRPC.Button { Label = "Join Instance", Url = joinUrl }];

            _client.SetPresence(new RichPresence
            {
                Details       = worldName,
                State         = instanceState,
                Assets        = assets,
                Timestamps    = new Timestamps(joinedAt.ToUniversalTime()),
                Buttons       = buttons,
                StatusDisplay = StatusDisplayType.Details,
            });
            _client.Invoke();
        }
        catch (Exception ex)
        {
            Log($"[Discord] UpdatePresence failed: {ex.Message}");
        }
    }

    public void ClearPresence()
    {
        if (_client?.IsInitialized != true) return;
        try { _client.ClearPresence(); _client.Invoke(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }
}
#endif
