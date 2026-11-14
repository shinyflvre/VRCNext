using Newtonsoft.Json.Linq;
using VRCNext.Services;

namespace VRCNext;

// Owns all Discord Rich Presence state, logic, and message handling.

public class DiscordController : IDisposable
{
    private readonly CoreLibrary _core;
    private readonly InstanceController _instance;
    private readonly VROverlayController _vroCtrl;
    private DateTime _nextConnectAttempt = DateTime.MinValue;

    // Field (moved from MainForm.Fields.cs)
    private DiscordPresenceService? _discordPresence;

    // Public Accessors (for other domains)
    public bool IsConnected => _discordPresence?.IsConnected ?? false;

    public DiscordController(CoreLibrary core, InstanceController instance, VROverlayController vroCtrl)
    {
        _core = core;
        _instance = instance;
        _vroCtrl = vroCtrl;
    }

    // Message Handler

    public void HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "dpStart":
                {
                    _discordPresence?.Dispose();
                    _discordPresence = CreatePresenceService();
                    _nextConnectAttempt = DateTime.MinValue;
                    bool ok = ConnectPresence();
                    _core.SendToJS("dpState", new { running = ok });
                    if (ok) PushPresence();
                    _vroCtrl.UpdateToolStates();
                }
                break;

            case "dpStop":
                _discordPresence?.Disconnect();
                _discordPresence?.Dispose();
                _discordPresence = null;
                _core.SendToJS("dpState", new { running = false });
                _vroCtrl.UpdateToolStates();
                break;
        }
    }

    // Push Presence (moved from MainForm.Relay.cs)

    public void PushPresence()
    {
        if (_discordPresence == null || !ConnectPresence()) return;
        if (string.IsNullOrEmpty(_instance.CachedInstWorldName)) { _discordPresence.ClearPresence(); return; }

        var (_, rawInstanceId, instanceType) = VRChatApiService.ParseLocation(_instance.CachedInstLocation);

        // Extract the short numeric instance ID, e.g. "12345" from "12345~friends+(usr_...)~nonce"
        var shortId = rawInstanceId.Contains('~') ? rawInstanceId[..rawInstanceId.IndexOf('~')] : rawInstanceId;

        var typeLabel = instanceType switch
        {
            "public"        => "Public",
            "friends"       => "Friends",
            "friends+"      => "Friends+",
            "hidden"        => "Friends+",   // VRChat API: hidden = Friends+
            "private"       => "Invite",     // VRChat API: private = Invite Only
            "invite_plus"   => "Invite+",
            "group"         => "Group",
            "group-public"  => "Group Public",
            "group-plus"    => "Group+",
            "group-members" => "Group",
            _               => "Public",
        };

        var nUsers = _core.LogWatcher.GetCurrentPlayers().Count;
        if (nUsers == 0) nUsers = 1; // at minimum we are in the instance

        // Privacy flags based on current VRC status
        var myStatus = _core.MyVrcStatus;
        bool isJoinMe = myStatus == "join me";
        bool isOnline = myStatus is "active" or "online" or "";
        bool isAskMe  = myStatus == "ask me";
        bool isBusy   = myStatus == "busy";

        bool hideInstId  = (isJoinMe && _core.Settings.DpHideInstIdJoinMe)  || (isOnline && _core.Settings.DpHideInstIdOnline)
                         || (isAskMe && _core.Settings.DpHideInstIdAskMe)   || (isBusy   && _core.Settings.DpHideInstIdBusy);
        bool hideLoc     = (isJoinMe && _core.Settings.DpHideLocJoinMe)     || (isOnline && _core.Settings.DpHideLocOnline)
                         || (isAskMe && _core.Settings.DpHideLocAskMe)      || (isBusy   && _core.Settings.DpHideLocBusy);
        bool hidePlayers = (isJoinMe && _core.Settings.DpHidePlayersJoinMe) || (isOnline && _core.Settings.DpHidePlayersOnline)
                         || (isAskMe && _core.Settings.DpHidePlayersAskMe)  || (isBusy   && _core.Settings.DpHidePlayersBusy);

        var stateParts = new System.Text.StringBuilder(typeLabel);
        if (!hideInstId)  stateParts.Append($" #{shortId}");
        if (!hidePlayers) stateParts.Append($" · ({nUsers}/{_instance.CachedInstCapacity})");
        var state = stateParts.ToString();

        var worldName  = hideLoc ? "" : _instance.CachedInstWorldName;
        var worldThumb = hideLoc ? "" : _instance.CachedInstWorldThumb;
        var joinedAt = _core.DiscordJoinedAt == DateTime.MinValue ? DateTime.Now : _core.DiscordJoinedAt;

        bool hideJoinBtn = (isJoinMe && _core.Settings.DpHideJoinBtnJoinMe) || (isOnline && _core.Settings.DpHideJoinBtnOnline)
                         || (isAskMe && _core.Settings.DpHideJoinBtnAskMe)  || (isBusy   && _core.Settings.DpHideJoinBtnBusy);
        string? joinUrl = null;
        if (!hideJoinBtn && !string.IsNullOrEmpty(_instance.CachedInstLocation))
        {
            var (worldId2, _, _) = VRChatApiService.ParseLocation(_instance.CachedInstLocation);
            var encodedInst = Uri.EscapeDataString(rawInstanceId);
            joinUrl = $"https://vrchat.com/home/launch?worldId={worldId2}&instanceId={encodedInst}";
        }

        _discordPresence.UpdatePresence(worldName, state, worldThumb, myStatus, joinedAt, joinUrl);
    }

    // Toggle (called from VR overlay)

    public void Toggle()
    {
        if (_discordPresence != null)
        {
            _discordPresence.Disconnect();
            _discordPresence.Dispose();
            _discordPresence = null;
            _nextConnectAttempt = DateTime.MinValue;
            _core.SendToJS("dpState", new { running = false });
        }
        else
        {
            _discordPresence = CreatePresenceService();
            _nextConnectAttempt = DateTime.MinValue;
            bool ok = ConnectPresence();
            _core.SendToJS("dpState", new { running = ok });
            if (ok) PushPresence();
        }
    }

    private DiscordPresenceService CreatePresenceService()
    {
        var service = new DiscordPresenceService("1480822566854852762");
        service.OnLog += message => Invoke(() => _core.SendToJS("log", new { msg = message, color = "sec" }));
        return service;
    }

    private bool ConnectPresence()
    {
        if (_discordPresence?.IsConnected == true) return true;
        if (_discordPresence == null || DateTime.UtcNow < _nextConnectAttempt) return false;
        _nextConnectAttempt = DateTime.UtcNow.AddSeconds(15);
        var connected = _discordPresence.Connect();
        _core.SendToJS("dpState", new { running = connected });
        _vroCtrl.UpdateToolStates();
        return connected;
    }

    // Disposal

    public void Dispose()
    {
        _discordPresence?.Dispose();
        _discordPresence = null;
    }

    // Photino compatibility shim
    private static void Invoke(Action action) => action();
}
