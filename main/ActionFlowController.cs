using Newtonsoft.Json.Linq;
using VRCNext.Services;
using VRCNext.Services.Helpers;

namespace VRCNext;

public class ActionFlowController : IDisposable
{
    private readonly CoreLibrary _core;
    private ActionFlowSettings   _settings;

#if WINDOWS
    public Func<WindowsTrayService?>? TrayServiceProvider { get; set; }
#endif

    public ActionFlowController(CoreLibrary core)
    {
        _core = core;
        _settings = ActionFlowSettings.Load();
    }

    public void HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "afLoadFlows":
            {

                var arr = new JArray();
                foreach (var f in _settings.Flows)
                {
                    arr.Add(new JObject {
                        ["id"]        = f.Id,
                        ["name"]      = f.Name,
                        ["enabled"]   = f.Enabled,
                        ["workspace"] = f.Workspace,
                        ["createdAt"] = f.CreatedAt,
                        ["updatedAt"] = f.UpdatedAt,
                    });
                }
                _core.SendToJS("afFlows", new {
                    flows             = arr,
                    conditions        = _settings.Conditions,
                    conditionDefaults = _settings.ConditionDefaults,
                });
                break;
            }

            case "afSaveConditions":
            {
                var conds  = msg["conditions"]?.ToObject<Dictionary<string, bool>>();
                var defs   = msg["defaults"]?.ToObject<Dictionary<string, bool>>();
                if (conds != null) _settings.Conditions = conds;
                if (defs  != null) _settings.ConditionDefaults = defs;
                _settings.Save();
                if (_settings.LastSaveError != null)
                    _core.SendToJS("log", new { msg = "[ActionFlow] conditions save failed: " + _settings.LastSaveError, color = "err" });
                break;
            }

            case "afSaveFlows":
            {
                var arr = msg["flows"] as JArray;
                if (arr == null) {
                    _core.SendToJS("afSaveResult", new { ok = false, error = "missing flows" });
                    _core.SendToJS("log", new { msg = "[ActionFlow] save rejected: missing flows", color = "err" });
                    break;
                }

                try
                {
                    _settings.Flows = arr.ToObject<List<ActionFlowSettings.ActionFlow>>() ?? new();
                }
                catch (Exception ex)
                {
                    _core.SendToJS("afSaveResult", new { ok = false, error = "parse: " + ex.Message });
                    _core.SendToJS("log", new { msg = "[ActionFlow] save parse error: " + ex.Message, color = "err" });
                    break;
                }

                _settings.Save();
                if (_settings.LastSaveError != null)
                {
                    _core.SendToJS("afSaveResult", new { ok = false, error = _settings.LastSaveError });
                    _core.SendToJS("log", new { msg = "[ActionFlow] save failed: " + _settings.LastSaveError, color = "err" });
                }
                else
                {
                    var nonEmpty = _settings.Flows.Count(f => f.Workspace != null && f.Workspace.HasValues);
                    _core.SendToJS("afSaveResult", new { ok = true, count = _settings.Flows.Count, nonEmpty });
                }
                break;
            }

            case "afTrayNotify":
            {
                var title    = msg["title"]?.ToString()    ?? "Action Flow";
                var subtitle = msg["subtitle"]?.ToString() ?? "";
                var accent   = msg["accent"]?.ToString()   ?? "info";
                var imageUrl = msg["imageUrl"]?.ToString() ?? "";
                var friendId = msg["friendId"]?.ToString() ?? "";

#if WINDOWS
                var tray = TrayServiceProvider?.Invoke();
                tray?.ShowNotification(title, subtitle, imageUrl, accent);
#else
                if (string.IsNullOrWhiteSpace(imageUrl))
                    _core.Window?.SendNotification(title, subtitle);
                else
                    _ = SendLinuxNotificationAsync(title, subtitle, imageUrl);
#endif
#if WINDOWS
                var notifTime = DateTimeHelper.FormatTime(DateTime.Now);
                _core.VrOverlay?.AddNotification("notif_actionflow", title, subtitle, notifTime, imageUrl, friendId);
                _core.VrOverlay?.EnqueueToast("notif_actionflow", title, subtitle, notifTime, imageUrl, false, friendId);
#endif
                break;
            }

            case "afCloseVrchat":
            {
                _ = Task.Run(() =>
                {
                    var closed = false;
                    try
                    {
                        foreach (var p in System.Diagnostics.Process.GetProcessesByName("VRChat"))
                        {
                            try { if (!p.HasExited) { p.CloseMainWindow(); if (!p.WaitForExit(5000)) p.Kill(); closed = true; } }
                            catch (Exception ex) { CrashHandler.WriteEntry("afCloseVrchat.Process", ex); }
                            finally { p.Dispose(); }
                        }
                    }
                    catch (Exception ex) { CrashHandler.WriteEntry("afCloseVrchat", ex); }
                    if (!closed)
                        _core.SendToJS("log", new { msg = "[ActionFlow] close VRChat: no running instance found", color = "warn" });
                });
                break;
            }

            case "afGetGameRunning":
            {
                var running = _core.IsVrcRunning?.Invoke() ?? false;
                _core.SendToJS("afGameRunning", new { running });
                break;
            }

            case "afInstanceWebhook":
            {
                var url = msg["url"]?.ToString();
                if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) break;
                var scope         = msg["scope"]?.ToString() ?? "own";
                var advanced      = msg["advanced"]?.Value<bool>() ?? false;
                var worldId       = msg["worldId"]?.ToString() ?? "";
                var worldName     = msg["worldName"]?.ToString() ?? "";
                var typeLabel     = msg["instanceTypeLabel"]?.ToString() ?? "";
                var authorName    = msg["authorName"]?.ToString() ?? "";
                var authorUserId  = msg["authorUserId"]?.ToString() ?? "";
                var authorIconUrl = msg["authorIconUrl"]?.ToString() ?? "";
                var capacity      = msg["capacity"]?.Value<int>() ?? 0;
                var location      = msg["location"]?.ToString() ?? "";

                _ = Task.Run(async () =>
                {
                    try
                    {
                        static string ExtOrPng(string p)
                        {
                            var e = Path.GetExtension(p);
                            return string.IsNullOrEmpty(e) ? ".png" : e;
                        }

                        var (locWorldId, instanceId, _) = VRChatApiService.ParseLocation(location);
                        if (string.IsNullOrEmpty(worldId)) worldId = locWorldId;
                        var (instName, region, groupId) = ParseInstanceParts(instanceId);

                        var launchUrl = !string.IsNullOrEmpty(locWorldId) && !string.IsNullOrEmpty(instanceId)
                            ? $"https://vrchat.com/home/launch?worldId={locWorldId}&instanceId={Uri.EscapeDataString(instanceId)}"
                            : "";

                        var groupName = "";
                        if (!string.IsNullOrEmpty(groupId))
                        {
                            try
                            {
                                var grp = await _core.Groups.GetGroupAsync(groupId);
                                groupName = grp?["name"]?.ToString() ?? "";
                            }
                            catch (Exception ex) { CrashHandler.WriteEntry("afInstanceWebhook.GetGroup", ex); }
                        }

                        var lines       = new List<string>();
                        var playerLines = new List<string>();
                        if (!string.IsNullOrEmpty(typeLabel)) lines.Add($"**Instance Type:** {typeLabel}");
                        if (!string.IsNullOrEmpty(instName))  lines.Add($"**Instance:** {instName}");
                        if (!string.IsNullOrEmpty(region))    lines.Add($"**Region:** {region.ToUpperInvariant()}");
                        if (!string.IsNullOrEmpty(groupName)) lines.Add($"**Group:** {groupName}");

                        if (scope == "own")
                        {
                            await WaitForInstancePopulationAsync(worldId);
                            var players = _core.LogWatcher.GetCurrentPlayers().OrderBy(p => p.JoinedAt).ToList();
                            int count = players.Count;
                            lines.Add($"**Players:** {(capacity > 0 ? $"{count}/{capacity}" : count.ToString())}");
                            if (advanced && count > 0)
                            {
                                playerLines.Add("");
                                playerLines.Add($"**Players in Instance ({count})**");
                                playerLines.AddRange(players.Select(p => p.DisplayName));
                            }
                        }
                        else if (!string.IsNullOrEmpty(location))
                        {
                            try
                            {
                                var inst = await _core.Instances.GetInstanceAsync(location);
                                if (inst != null)
                                {
                                    var userCount = inst["userCount"]?.Value<int>() ?? 0;
                                    var cap       = inst["capacity"]?.Value<int>() ?? capacity;
                                    lines.Add($"**Players:** {(cap > 0 ? $"{userCount}/{cap}" : userCount.ToString())}");
                                }
                            }
                            catch (Exception ex) { CrashHandler.WriteEntry("afInstanceWebhook.GetInstance", ex); }
                        }

                        if (!string.IsNullOrEmpty(launchUrl))
                        {
                            lines.Add("");
                            lines.Add($"[Join Instance]({launchUrl})");
                        }
                        lines.AddRange(playerLines);

                        var description = string.Join("\n", lines);
                        if (description.Length > 4000) description = description[..4000];

                        var embed = new JObject
                        {
                            ["color"]     = 10459903,
                            ["timestamp"] = DateTime.UtcNow.ToString("o"),
                        };
                        if (!string.IsNullOrEmpty(worldName))   embed["title"]       = $"Joined \"{worldName}\"";
                        if (!string.IsNullOrEmpty(launchUrl))   embed["url"]         = launchUrl;
                        if (!string.IsNullOrEmpty(description))  embed["description"] = description;

                        var attachments = new List<(string, string)>();

                        var worldFile = string.IsNullOrEmpty(worldId) ? null : ImageCacheHelper.GetWorldCached(worldId);
                        if (worldFile != null && File.Exists(worldFile))
                        {
                            var wn = "world" + ExtOrPng(worldFile);
                            embed["thumbnail"] = new JObject { ["url"] = "attachment://" + wn };
                            attachments.Add((worldFile, wn));
                        }

                        if (!string.IsNullOrEmpty(authorName))
                        {
                            var author = new JObject { ["name"] = authorName };
                            var iconFile = string.IsNullOrEmpty(authorUserId) ? null : ImageCacheHelper.GetUserCached(authorUserId);
                            if (iconFile == null && !string.IsNullOrEmpty(authorUserId)
                                && authorIconUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            {
                                try { iconFile = await ImageCacheHelper.CacheUserAsync(authorUserId, authorIconUrl); } catch { }
                            }
                            if (iconFile != null && File.Exists(iconFile))
                            {
                                var inm = "icon" + ExtOrPng(iconFile);
                                author["icon_url"] = "attachment://" + inm;
                                attachments.Add((iconFile, inm));
                            }
                            else if (authorIconUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                            {
                                author["icon_url"] = authorIconUrl;
                            }
                            embed["author"] = author;
                        }

                        var payload = new JObject
                        {
                            ["embeds"] = new JArray { embed },
                        };

                        var json = payload.ToString(Newtonsoft.Json.Formatting.None);
                        var res = attachments.Count > 0
                            ? await _core.Webhook.PostEmbedWithFilesAsync(url, json, attachments)
                            : await _core.Webhook.PostJsonAsync(url, json);
                        if (!res.Success)
                            _core.SendToJS("log", new { msg = "[ActionFlow] webhook send failed: " + res.Error, color = "err" });
                    }
                    catch (Exception ex)
                    {
                        _core.SendToJS("log", new { msg = "[ActionFlow] webhook error: " + ex.Message, color = "err" });
                    }
                });
                break;
            }

            case "afTextWebhook":
            {
                var twUrl = msg["url"]?.ToString();
                var twText = msg["text"]?.ToString() ?? "";
                var twIconUserId = msg["iconUserId"]?.ToString() ?? "";
                var twIconUrl    = msg["iconUrl"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(twUrl)
                    || !twUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || (string.IsNullOrWhiteSpace(twText) && string.IsNullOrWhiteSpace(twIconUserId))) break;
                if (twText.Length > 2000) twText = twText[..2000];
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(twIconUserId))
                        {
                            var embed = new JObject { ["color"] = 0x7B61FF };
                            if (!string.IsNullOrWhiteSpace(twText)) embed["description"] = twText;
                            var attachments = new List<(string Path, string Name)>();
                            var iconFile = ImageCacheHelper.GetUserCached(twIconUserId);
                            if (iconFile == null && twIconUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            {
                                try { iconFile = await ImageCacheHelper.CacheUserAsync(twIconUserId, twIconUrl); } catch { }
                            }
                            if (iconFile != null && File.Exists(iconFile))
                            {
                                var ext = Path.GetExtension(iconFile);
                                if (string.IsNullOrEmpty(ext)) ext = ".png";
                                var name = "icon" + ext;
                                embed["thumbnail"] = new JObject { ["url"] = "attachment://" + name };
                                attachments.Add((iconFile, name));
                            }
                            else if (twIconUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                            {
                                embed["thumbnail"] = new JObject { ["url"] = twIconUrl };
                            }
                            var embedPayload = new JObject { ["embeds"] = new JArray { embed } };
                            var embedJson = embedPayload.ToString(Newtonsoft.Json.Formatting.None);
                            var embedRes = attachments.Count > 0
                                ? await _core.Webhook.PostEmbedWithFilesAsync(twUrl, embedJson, attachments)
                                : await _core.Webhook.PostJsonAsync(twUrl, embedJson);
                            if (!embedRes.Success)
                                _core.SendToJS("log", new { msg = "[ActionFlow] webhook send failed: " + embedRes.Error, color = "err" });
                            return;
                        }
                        var payload = new JObject { ["content"] = twText };
                        var res = await _core.Webhook.PostJsonAsync(twUrl, payload.ToString(Newtonsoft.Json.Formatting.None));
                        if (!res.Success)
                            _core.SendToJS("log", new { msg = "[ActionFlow] webhook send failed: " + res.Error, color = "err" });
                    }
                    catch (Exception ex)
                    {
                        _core.SendToJS("log", new { msg = "[ActionFlow] webhook error: " + ex.Message, color = "err" });
                    }
                });
                break;
            }

            case "afSendChatMessage":
            {
                var uid       = msg["userId"]?.ToString();
                var text      = msg["text"]?.ToString();
                var notifId   = msg["notificationId"]?.ToString();
                var notifType = msg["notifType"]?.ToString();
                if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(text)) break;
                _ = Task.Run(async () =>
                {
                    if (!string.IsNullOrEmpty(notifId) && !string.IsNullOrEmpty(notifType))
                    {
                        var (ok, err) = await _core.Invite.RespondToNotificationAsync(notifId, notifType, text);
                        if (!ok) _core.SendToJS("log", new { msg = "[ActionFlow] reply failed → " + uid + ": " + err, color = "err" });
                    }
                    else
                    {
                        var (ok, err, _) = await _core.Invite.SendChatMessageDedupedAsync(uid, text);
                        if (!ok) _core.SendToJS("log", new { msg = "[ActionFlow] chat send failed → " + uid + ": " + err, color = "err" });
                    }
                });
                break;
            }
        }
    }

#if !WINDOWS
    private async Task SendLinuxNotificationAsync(string title, string body, string imageUrl)
    {
        try
        {
            var sent = await LinuxNotificationHelper.TrySendAsync(
                title, body, imageUrl, _core.VrcApi.GetHttpClient(),
                message => _core.SendToJS("log", new { msg = message, color = "warn" }),
                LinuxNotificationHelper.ActionFlowEvent);
            if (!sent) _core.Window?.SendNotification(title, body);
        }
        catch (Exception ex)
        {
            _core.SendToJS("log", new { msg = "[Notifications] " + ex.Message, color = "warn" });
            _core.Window?.SendNotification(title, body);
        }
    }
#endif

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    // The player list is built from log lines that arrive well after the world
    // switch, so a flow firing shortly afterwards would report an empty instance.
    // Waits until the count has stopped growing, bounded so a genuinely empty
    // instance still reports.
    private static (string name, string region, string groupId) ParseInstanceParts(string instanceId)
    {
        if (string.IsNullOrEmpty(instanceId)) return ("", "", "");
        var tilde = instanceId.IndexOf('~');
        var name   = tilde >= 0 ? instanceId[..tilde] : instanceId;
        var region = System.Text.RegularExpressions.Regex.Match(instanceId, @"~region\(([^)]+)\)");
        var group  = System.Text.RegularExpressions.Regex.Match(instanceId, @"~group\(([^)]+)\)");
        return (name,
                region.Success ? region.Groups[1].Value : "",
                group.Success  ? group.Groups[1].Value  : "");
    }

    private async Task WaitForInstancePopulationAsync(string worldId)
    {
        const int PollMs      = 500;
        const int StableTicks = 3;
        const int MaxWaitMs   = 25_000;

        var waited = 0;
        var last   = -1;
        var stable = 0;

        while (waited < MaxWaitMs)
        {
            if (!string.IsNullOrEmpty(worldId)
                && !string.IsNullOrEmpty(_core.LogWatcher.CurrentWorldId)
                && _core.LogWatcher.CurrentWorldId != worldId)
                return;

            var count = _core.LogWatcher.PlayerCount;
            if (count > 0 && count == last)
            {
                if (++stable >= StableTicks) return;
            }
            else
            {
                stable = 0;
            }
            last = count;

            await Task.Delay(PollMs);
            waited += PollMs;
        }
    }
}
