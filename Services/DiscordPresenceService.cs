using DiscordRPC;
using DiscordRPC.Logging;

namespace VRCNext.Services;

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
            _client?.ClearPresence();
            _client?.Dispose();
        }
        catch { }
        _client = null;
        Log("[Discord] Disconnected");
    }

    /// <summary>
    /// Updates the Discord Rich Presence.
    /// </summary>
    /// <param name="worldName">World name shown as Details (top line)</param>
    /// <param name="instanceState">e.g. "Friends+ #12345 (13/32)"</param>
    /// <param name="worldImageUrl">VRChat world thumbnail URL (Discord supports external URLs)</param>
    /// <param name="status">VRC status string: "active", "join me", "busy", "ask me"</param>
    /// <param name="joinedAt">When the user joined the instance (for elapsed timer)</param>
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

            // Discord supports external image URLs directly
            if (!string.IsNullOrEmpty(worldImageUrl))
                assets.LargeImageKey = worldImageUrl;

            Button[]? buttons = null;
            if (!string.IsNullOrEmpty(joinUrl))
                buttons = [new Button { Label = "Join Instance", Url = joinUrl }];

            _client.SetPresence(new RichPresence
            {
                Details    = worldName,
                State      = instanceState,
                Assets     = assets,
                Timestamps = new Timestamps(joinedAt.ToUniversalTime()),
                Buttons    = buttons,
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
