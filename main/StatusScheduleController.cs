using Newtonsoft.Json.Linq;
using VRCNext.Services;

namespace VRCNext;

public class StatusScheduleController : IDisposable
{
    private readonly CoreLibrary _core;
    private StatusScheduleSettings _settings;

    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;

    // Rule currently driving the status, plus what to put back when it ends.
    private string? _activeRuleId;
    private string? _lastPushedRuleId;
    private string? _restoreStatus;
    private string? _restoreMessage;

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    public StatusScheduleController(CoreLibrary core)
    {
        _core = core;
        _settings = StatusScheduleSettings.Load();
    }

    public void Start()
    {
        if (_loopTask != null) return;
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        WireInstanceEvents();
    }

    private bool _instanceEventsWired;

    private void WireInstanceEvents()
    {
        if (_instanceEventsWired) return;
        _instanceEventsWired = true;
        _core.LogWatcher.PlayerJoined  += (_, _) => RequestReevaluate();
        _core.LogWatcher.PlayerLeft    += (_, _) => RequestReevaluate();
        _core.LogWatcher.WorldChanged  += (_, _) => RequestReevaluate();
    }

    public void RequestReevaluate()
    {
        if (_cts.IsCancellationRequested) return;
        _ = Task.Run(async () =>
        {
            try { await EvaluateAsync(); }
            catch (Exception ex) { CrashHandler.WriteEntry("StatusSchedule.RequestReevaluate", ex); }
        });
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await EvaluateAsync();
                // Push state whenever the active rule changed, otherwise the UI would
                // only ever learn about it when the user happens to trigger an action.
                if (_activeRuleId != _lastPushedRuleId)
                {
                    _lastPushedRuleId = _activeRuleId;
                    SendState();
                }
            }
            catch (Exception ex) { Log($"evaluate failed: {ex.Message}", "err"); }

            try { await Task.Delay(TickInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Log(string msg, string color = "info")
        => _core.SendToJS("log", new { msg = "[StatusSchedule] " + msg, color });

    // Rule Matching

    // Returns true when `now` falls inside the rule's window. A window whose end is
    // not after its start is treated as crossing midnight (e.g. 22:00 -> 06:00).
    internal static bool IsWithinWindow(StatusScheduleSettings.StatusRule rule, DateTime now)
            => VRCNext.Services.Helpers.TimeWindowHelper.IsWithin(rule.Start, rule.End, rule.Days, now);

    private StatusScheduleSettings.StatusRule? PickRule(DateTime now, bool vrcRunning)
    {
        var needsInstance = _settings.Rules.Any(r => r.Enabled &&
            (r.InstanceTypes.Count > 0 || r.MinPlayers > 0 || r.FriendIds.Count > 0));

        var instanceType = "";
        var playerCount  = 0;
        HashSet<string> presentIds = new();

        if (needsInstance && vrcRunning)
        {
            var (_, _, itype) = VRChatApiService.ParseLocation(_core.LogWatcher.CurrentLocation);
            instanceType = itype;
            var players  = _core.LogWatcher.GetCurrentPlayers();
            playerCount  = players.Count;
            presentIds   = new HashSet<string>(
                players.Where(p => !string.IsNullOrEmpty(p.UserId)).Select(p => p.UserId));
        }

        StatusScheduleSettings.StatusRule? best = null;
        foreach (var rule in _settings.Rules)
        {
            if (!rule.Enabled) continue;
            if (rule.OnlyWhileInGame && !vrcRunning) continue;
            if (rule.OnlyWhileOutsideGame && vrcRunning) continue;
            if (!IsWithinWindow(rule, now)) continue;

            var hasInstanceCondition = rule.InstanceTypes.Count > 0
                                    || rule.MinPlayers > 0
                                    || rule.FriendIds.Count > 0;
            if (hasInstanceCondition)
            {
                if (!vrcRunning) continue;
                if (rule.InstanceTypes.Count > 0 && !rule.InstanceTypes.Contains(instanceType)) continue;
                if (rule.MinPlayers > 0 && playerCount < rule.MinPlayers) continue;
                if (rule.FriendIds.Count > 0)
                {
                    var match = rule.FriendsRequireAll
                        ? rule.FriendIds.All(presentIds.Contains)
                        : rule.FriendIds.Any(presentIds.Contains);
                    if (!match) continue;
                }
            }

            if (best == null || rule.Priority > best.Priority) best = rule;
        }
        return best;
    }

    // Evaluation

    private async Task EvaluateAsync(bool force = false)
    {
        if (!_settings.Enabled || !_core.VrcApi.IsLoggedIn)
        {
            _activeRuleId = null;
            return;
        }

        var now         = DateTime.Now;
        var vrcRunning  = _core.IsVrcRunning?.Invoke() ?? false;
        var rule        = PickRule(now, vrcRunning);

        if (rule == null)
        {
            if (_activeRuleId == null) return;

            var restoreStatus  = _restoreStatus;
            var restoreMessage = _restoreMessage;
            _activeRuleId   = null;
            _restoreStatus  = null;
            _restoreMessage = null;

            if (restoreStatus != null)
            {
                await _core.Users.UpdateStatusAsync(restoreStatus, restoreMessage ?? "");
                Log($"window ended, restored status to {restoreStatus}", "ok");
                _core.SendToJS("ssApplied", new { ruleId = (string?)null, status = restoreStatus, restored = true });
            }
            return;
        }

        if (_activeRuleId == rule.Id && !force) return;

        var current        = _core.VrcApi.CurrentUserRaw;
        var currentStatus  = current?["status"]?.ToString() ?? "";
        var currentMessage = current?["statusDescription"]?.ToString() ?? "";

        // Only capture the restore point when moving in from "no rule active", so a
        // handover between two rules does not overwrite the user's original status.
        if (_activeRuleId == null && rule.RestorePreviousStatus)
        {
            _restoreStatus  = currentStatus;
            _restoreMessage = currentMessage;
        }
        else if (_activeRuleId == null)
        {
            _restoreStatus  = null;
            _restoreMessage = null;
        }

        _activeRuleId = rule.Id;

        var message = rule.SetStatusMessage ? rule.StatusMessage : currentMessage;
        var user = await _core.Users.UpdateStatusAsync(rule.Status, message);
        if (user != null)
        {
            Log($"rule '{rule.Name}' applied status {rule.Status}", "ok");
            _core.SendToJS("ssApplied", new { ruleId = rule.Id, status = rule.Status, message, restored = false });
        }
        else
        {
            Log($"rule '{rule.Name}' failed to apply status", "err");
            _activeRuleId = null;
        }
    }

    // Messages

    public async Task HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "ssLoadRules":
                // Evaluate first so activeRuleId is current when the tab opens, rather
                // than whatever the last background tick happened to leave behind.
                await EvaluateAsync();
                _lastPushedRuleId = _activeRuleId;
                SendState();
                break;

            case "ssSaveRules":
            {
                var arr = msg["rules"] as JArray;
                if (arr == null)
                {
                    _core.SendToJS("ssSaveResult", new { ok = false, error = "missing rules" });
                    break;
                }

                try
                {
                    _settings.Rules = arr.ToObject<List<StatusScheduleSettings.StatusRule>>() ?? new();
                }
                catch (Exception ex)
                {
                    _core.SendToJS("ssSaveResult", new { ok = false, error = "parse: " + ex.Message });
                    break;
                }

                // The two game-state filters are mutually exclusive; normalise here so a
                // malformed payload cannot produce a rule that never matches.
                foreach (var r in _settings.Rules)
                    if (r.OnlyWhileInGame) r.OnlyWhileOutsideGame = false;

                // Deliberately does not touch Enabled - that has its own action. Letting a
                // rule save carry it risks writing back a stale client-side default.
                _settings.Save();
                if (_settings.LastSaveError != null)
                    _core.SendToJS("ssSaveResult", new { ok = false, error = _settings.LastSaveError });
                else
                    _core.SendToJS("ssSaveResult", new { ok = true, count = _settings.Rules.Count });

                // A changed rule set can invalidate the active window immediately.
                await EvaluateAsync(force: true);
                _lastPushedRuleId = _activeRuleId;
                SendState();
                break;
            }

            case "ssSetEnabled":
            {
                _settings.Enabled = msg["enabled"]?.Value<bool>() ?? true;
                _settings.Save();
                if (_settings.LastSaveError != null)
                    Log("enabled save failed: " + _settings.LastSaveError, "err");
                await EvaluateAsync(force: true);
                _lastPushedRuleId = _activeRuleId;
                SendState();
                break;
            }

        }
    }

    private void SendState()
    {
        // SendToJS serializes without a camelCase resolver, so the payload is built by
        // hand - otherwise the frontend would receive PascalCase keys and lose rule.id.
        var arr = new JArray();
        foreach (var r in _settings.Rules)
        {
            arr.Add(new JObject
            {
                ["id"]                    = r.Id,
                ["name"]                  = r.Name,
                ["enabled"]               = r.Enabled,
                ["priority"]              = r.Priority,
                ["start"]                 = r.Start,
                ["end"]                   = r.End,
                ["days"]                  = new JArray(r.Days ?? new List<int>()),
                ["onlyWhileInGame"]       = r.OnlyWhileInGame,
                ["onlyWhileOutsideGame"]  = r.OnlyWhileOutsideGame,
                ["restorePreviousStatus"] = r.RestorePreviousStatus,
                ["status"]                = r.Status,
                ["setStatusMessage"]      = r.SetStatusMessage,
                ["statusMessage"]         = r.StatusMessage,
                ["instanceTypes"]         = new JArray(r.InstanceTypes ?? new List<string>()),
                ["minPlayers"]            = r.MinPlayers,
                ["friendIds"]             = new JArray(r.FriendIds ?? new List<string>()),
                ["friendsRequireAll"]     = r.FriendsRequireAll,
            });
        }

        _core.SendToJS("ssRules", new
        {
            enabled      = _settings.Enabled,
            rules        = arr,
            activeRuleId = _activeRuleId,
            vrcRunning   = _core.IsVrcRunning?.Invoke() ?? false,
        });
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _loopTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
