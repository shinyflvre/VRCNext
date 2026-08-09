using Newtonsoft.Json.Linq;
using VRCNext.Services;
using VRCNext.Services.Helpers;

namespace VRCNext;

// Owns all notification-related logic, message handling, and WebSocket events.

public class NotificationsController
{
    private readonly CoreLibrary _core;
    private readonly FriendsController _friends;
    private readonly InstanceController _instance;

    // Persists resolved images across multiple notification refreshes
    // (ProcessSingleNotif skips re-fetching already-logged IDs, so we cache here)
    private readonly Dictionary<string, string> _notifImageCache = new();

    // Constructor

    public NotificationsController(CoreLibrary core, FriendsController friends, InstanceController instance)
    {
        _core = core;
        _friends = friends;
        _instance = instance;
    }

    // WebSocket Wiring

    public void WireWebSocket(VRChatWebSocketService ws)
    {
        ws.NotificationArrived += (_, args) =>
        {
            if (!_core.VrcApi.IsLoggedIn) return;
            // update/delete or missing payload → full REST refresh
            if (args.WsType is "notification-v2-update" or "notification-v2-delete" || args.Data == null)
            {
                _ = GetNotificationsAsync();
                return;
            }
            // notification/notification-v2: process the WS payload directly — no REST needed
            _ = Task.Run(() =>
            {
                try
                {
                    var n = args.WsType == "notification-v2"
                        ? NormalizeNotifV2(args.Data)
                        : NormalizeNotifV1(args.Data);
                    ProcessSingleNotif(n, prependToJs: true);
                }
                catch (Exception ex)
                {
                    Invoke(() => _core.SendToJS("log", new { msg = $"[WS Notif] parse error: {ex.Message}", color = "err" }));
                    _ = GetNotificationsAsync();
                }
            });
        };
    }

    // Message Handler

    public async Task HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "vrcGetNotifications":
                _ = GetNotificationsAsync();
                break;

            case "vrcGetRespondMessages":
            {
                /* notifType in {invite, requestInvite} → pool {response, requestResponse}
                   per openapi.yaml /invite/{notificationId}/response. */
                var notifType = msg["notifType"]?.ToString() ?? "invite";
                var pool      = notifType == "requestInvite" ? "requestResponse" : "response";
                _ = Task.Run(async () =>
                {
                    var uid  = _core.VrcApi.CurrentUserId;
                    if (string.IsNullOrEmpty(uid)) return;
                    var msgs = await _core.Invite.GetInviteMessagesAsync(uid, pool);
                    _core.SendToJS("vrcRespondMessages", new { pool, messages = msgs ?? new JArray() });
                });
                break;
            }

            case "vrcUpdateRespondMessage":
            {
                var notifType = msg["notifType"]?.ToString() ?? "invite";
                var pool      = notifType == "requestInvite" ? "requestResponse" : "response";
                var slot      = msg["slot"]?.Value<int>() ?? -1;
                var text      = msg["message"]?.ToString() ?? "";
                if (slot < 0 || string.IsNullOrEmpty(text)) break;
                _ = Task.Run(async () =>
                {
                    var uid = _core.VrcApi.CurrentUserId;
                    if (string.IsNullOrEmpty(uid)) return;
                    var (ok, arr, cooldown) = await _core.Invite.UpdateInviteMessageAsync(uid, slot, text, pool);
                    if (ok && arr != null)
                        _core.SendToJS("vrcRespondMessages", new { pool, messages = arr });
                    else
                        _core.SendToJS("vrcRespondMessageUpdateFailed", new { pool, slot, cooldown });
                });
                break;
            }

            case "vrcRespondToNotification":
            {
                var notifId = msg["notifId"]?.ToString();
                var slot    = msg["responseSlot"]?.Value<int>() ?? -1;
                if (string.IsNullOrEmpty(notifId) || slot < 0) break;
                _ = Task.Run(async () =>
                {
                    var (ok, err) = await _core.Invite.RespondToNotificationBySlotAsync(notifId, slot);
                    _core.SendToJS("vrcActionResult", new {
                        action  = "respondNotif",
                        success = ok,
                        message = ok ? "Response sent!" : ("Response failed: " + err),
                    });
                    if (ok) await _core.Notifications.HideNotificationAsync(notifId, isV2: false);
                });
                break;
            }

            case "vrcRespondToNotificationWithPhoto":
            {
                var notifId  = msg["notifId"]?.ToString();
                var slot     = msg["responseSlot"]?.Value<int>() ?? -1;
                var fileUrl  = msg["fileUrl"]?.ToString();
                if (string.IsNullOrEmpty(notifId) || slot < 0 || string.IsNullOrEmpty(fileUrl)) break;
                _ = Task.Run(async () =>
                {
                    var (ok, err) = await _core.Invite.RespondToNotificationBySlotWithPhotoAsync(notifId, slot, fileUrl);
                    _core.SendToJS("vrcActionResult", new {
                        action  = "respondNotif",
                        success = ok,
                        message = ok ? "Response sent!" : ("Response failed: " + err),
                    });
                    if (ok) await _core.Notifications.HideNotificationAsync(notifId, isV2: false);
                });
                break;
            }

            case "vrcGetHiddenNotifications":
                _ = GetHiddenNotificationsAsync();
                break;

            case "vrcGetAllNotifications":
                _ = GetAllNotificationsAsync();
                break;

            case "vrcAcceptNotification":
            {
                var anId   = msg["notifId"]?.ToString();
                var anType = msg["type"]?.ToString();
                var anIsV2 = msg["_v2"]?.Value<bool>() ?? false;

                // details: nested JObject or JSON-encoded string (v1)
                JObject? anDet = null;
                { var rawDet = msg["details"];
                  if (rawDet is JObject d1) anDet = d1;
                  else if (rawDet?.Type == JTokenType.String) try { anDet = JObject.Parse(rawDet.ToString()); } catch { } }

                // _data: v2 group-specific payload (groupId, requestUserId, etc.)
                JObject? anData = null;
                { var rawData = msg["_data"];
                  if (rawData is JObject d2) anData = d2;
                  else if (rawData?.Type == JTokenType.String) try { anData = JObject.Parse(rawData.ToString()); } catch { } }

                var anLink = msg["_link"]?.ToString();
                _core.SendToJS("log", new { msg = $"AcceptNotif: type={anType} v2={anIsV2} det={anDet?.ToString(Newtonsoft.Json.Formatting.None)??"null"} data={anData?.ToString(Newtonsoft.Json.Formatting.None)??"null"}", color = "ok" });

                if (!string.IsNullOrEmpty(anId))
                {
                    if (anType == "invite")
                    {
                        // World invite: join the instance
                        var invLoc = anDet?["worldId"]?.ToString();
                        if (!string.IsNullOrEmpty(invLoc) && invLoc.Contains(":"))
                        {
                            if (_core.IsVrcRunning?.Invoke() ?? false)
                            {
                                _ = Task.Run(async () => {
                                    var ok = await _core.Instances.InviteSelfAsync(invLoc);
                                    await _core.Notifications.AcceptNotificationAsync(anId);
                                    Invoke(() => _core.SendToJS("vrcActionResult", new { action = "acceptNotif", success = ok,
                                        message = ok ? "Joining world... Check VRChat." : "Failed to join." }));
                                });
                            }
                            else
                            {
                                Invoke(() => _core.SendToJS("vrcLaunchNeeded", new { location = invLoc, steamVr = _core.IsSteamVrRunning?.Invoke() ?? false }));
                            }
                            break;
                        }
                    }
                    else if (anType == "group.invite")
                    {
                        // Group invite: join via Groups API (not notification accept endpoint)
                        var groupId = anDet?["groupId"]?.ToString()
                                   ?? anData?["groupId"]?.ToString()
                                   ?? ExtractGroupIdFromLink(anLink);
                        if (!string.IsNullOrEmpty(groupId))
                        {
                            _ = Task.Run(async () => {
                                var ok = await _core.Groups.JoinGroupAsync(groupId);
                                await _core.Notifications.HideNotificationAsync(anId, anIsV2);
                                Invoke(() => _core.SendToJS("vrcActionResult", new { action = "acceptNotif", success = ok,
                                    message = ok ? "Group joined!" : "Failed to join group.", groupJoined = ok }));
                                if (ok) _ = GetNotificationsAsync();
                            });
                            break;
                        }
                    }
                    else if (anType == "group.joinRequest")
                    {
                        // Someone wants to join your group — approve via Groups API
                        var groupId       = anDet?["groupId"]?.ToString()
                                         ?? anData?["groupId"]?.ToString()
                                         ?? ExtractGroupIdFromLink(anLink);
                        var groupShortCode = anData?["groupName"]?.ToString() ?? anDet?["groupName"]?.ToString();
                        var requestUser   = anDet?["requestUserId"]?.ToString()
                                         ?? anData?["requestUserId"]?.ToString()
                                         ?? anDet?["userId"]?.ToString()
                                         ?? anData?["userId"]?.ToString()
                                         ?? msg["senderId"]?.ToString();
                        _ = Task.Run(async () => {
                            // Resolve groupId via shortCode lookup if not directly in payload
                            var resolvedGroupId = groupId;
                            if (string.IsNullOrEmpty(resolvedGroupId) && !string.IsNullOrEmpty(groupShortCode))
                                resolvedGroupId = await _core.Groups.FindGroupIdByShortCodeAsync(groupShortCode);
                            if (!string.IsNullOrEmpty(resolvedGroupId) && !string.IsNullOrEmpty(requestUser))
                            {
                                var ok = await _core.Groups.RespondGroupJoinRequestAsync(resolvedGroupId, requestUser, "accept");
                                await _core.Notifications.HideNotificationAsync(anId, anIsV2);
                                Invoke(() => _core.SendToJS("vrcActionResult", new { action = "acceptNotif", success = ok,
                                    message = ok ? "Join request approved!" : "Failed to approve." }));
                                if (ok) _ = GetNotificationsAsync();
                            }
                            else
                            {
                                Invoke(() => _core.SendToJS("log", new { msg = $"group.joinRequest: could not resolve groupId (shortCode={groupShortCode}) or requestUser", color = "warn" }));
                            }
                        });
                        break;
                    }
                    else if (anType == "friendRequest")
                    {
                        // Friend request: v1 notification accept endpoint
                        _ = Task.Run(async () => {
                            var ok = await _core.Notifications.AcceptNotificationAsync(anId);
                            Invoke(() => _core.SendToJS("vrcActionResult", new { action = "acceptNotif", success = ok,
                                message = ok ? "Friend request accepted!" : "Failed." }));
                            if (ok) _ = GetNotificationsAsync();
                        });
                        break;
                    }
                    else if (anType == "requestInvite")
                    {
                        // Someone asked for an invite — send them to our current world via POST /invite/{userId}
                        var requesterId = msg["senderId"]?.ToString();
                        _ = Task.Run(async () => {
                            bool ok = false;
                            if (!string.IsNullOrEmpty(requesterId))
                                ok = await _core.Invite.InviteFriendAsync(requesterId, _core.LogWatcher.CurrentLocation ?? "");
                            // fallback: try notification accept endpoint
                            if (!ok)
                                ok = await _core.Notifications.AcceptNotificationAsync(anId);
                            else
                                await _core.Notifications.HideNotificationAsync(anId, anIsV2);
                            Invoke(() => _core.SendToJS("vrcActionResult", new { action = "acceptNotif", success = ok,
                                message = ok ? "Invite sent!" : "Failed. Are you in a world?" }));
                            if (ok) _ = GetNotificationsAsync();
                        });
                        break;
                    }

                    // Fallback for any other acceptable type
                    _ = Task.Run(async () => {
                        var ok = await _core.Notifications.AcceptNotificationAsync(anId);
                        Invoke(() => _core.SendToJS("vrcActionResult", new { action = "acceptNotif", success = ok,
                            message = ok ? "Accepted!" : "Failed." }));
                    });
                }
                break;
            }

            case "vrcMarkNotifRead":
                var mnId = msg["notifId"]?.ToString();
                if (!string.IsNullOrEmpty(mnId))
                    _ = Task.Run(async () => await _core.Notifications.MarkNotificationReadAsync(mnId));
                break;

            case "vrcHideNotification":
            {
                var hnId   = msg["notifId"]?.ToString();
                var hnType = msg["type"]?.ToString();
                var hnV2   = msg["_v2"]?.Value<bool>() ?? false;

                JObject? hnDet = null;
                { var r = msg["details"];
                  if (r is JObject d1) hnDet = d1;
                  else if (r?.Type == JTokenType.String) try { hnDet = JObject.Parse(r.ToString()); } catch { } }

                JObject? hnData = null;
                { var r = msg["_data"];
                  if (r is JObject d2) hnData = d2;
                  else if (r?.Type == JTokenType.String) try { hnData = JObject.Parse(r.ToString()); } catch { } }

                var hnLink = msg["_link"]?.ToString();

                if (!string.IsNullOrEmpty(hnId))
                {
                    _ = Task.Run(async () =>
                    {
                        bool ok;
                        if (hnType == "group.joinRequest")
                        {
                            // Reject via Groups API — also hides the notification on VRChat's side
                            var groupId       = hnDet?["groupId"]?.ToString() ?? hnData?["groupId"]?.ToString()
                                             ?? ExtractGroupIdFromLink(hnLink);
                            var groupShortCode = hnData?["groupName"]?.ToString() ?? hnDet?["groupName"]?.ToString();
                            var requestUser   = hnDet?["requestUserId"]?.ToString() ?? hnData?["requestUserId"]?.ToString()
                                             ?? hnDet?["userId"]?.ToString()         ?? hnData?["userId"]?.ToString()
                                             ?? msg["senderId"]?.ToString();
                            // Resolve groupId via shortCode lookup if not directly in payload
                            if (string.IsNullOrEmpty(groupId) && !string.IsNullOrEmpty(groupShortCode))
                                groupId = await _core.Groups.FindGroupIdByShortCodeAsync(groupShortCode);
                            if (!string.IsNullOrEmpty(groupId) && !string.IsNullOrEmpty(requestUser))
                                ok = await _core.Groups.RespondGroupJoinRequestAsync(groupId, requestUser, "reject");
                            else
                                ok = await _core.Notifications.HideNotificationAsync(hnId, hnV2);
                        }
                        else if (hnType == "group.invite")
                        {
                            // Decline invite via Groups API, then hide notification
                            var groupId = hnDet?["groupId"]?.ToString()
                                       ?? hnData?["groupId"]?.ToString()
                                       ?? ExtractGroupIdFromLink(hnLink);
                            if (!string.IsNullOrEmpty(groupId))
                                ok = await _core.Groups.DeclineGroupInviteAsync(groupId);
                            else
                                ok = await _core.Notifications.HideNotificationAsync(hnId, hnV2);
                            if (ok) await _core.Notifications.HideNotificationAsync(hnId, hnV2);
                        }
                        else
                        {
                            ok = await _core.Notifications.HideNotificationAsync(hnId, hnV2);
                        }
                        if (!ok) Invoke(() => _core.SendToJS("vrcActionResult", new { action = "hideNotif", success = false, message = "" }));
                    });
                }
                break;
            }
        }
    }

    // Static Helpers
    private static string? ExtractGroupIdFromLink(string? link)
    {
        if (string.IsNullOrEmpty(link)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(link, @"grp_[0-9a-f\-]+");
        return m.Success ? m.Value : null;
    }

    // Boop emoji can arrive as details/data, either as an object or as an embedded JSON string.
    private static string? ExtractEmojiId(JObject n)
    {
        foreach (var key in new[] { "details", "data" })
        {
            var tok = n[key];
            if (tok is null) continue;
            if (tok.Type == JTokenType.String)
            {
                var raw = tok.ToString();
                if (string.IsNullOrWhiteSpace(raw) || !raw.TrimStart().StartsWith("{")) continue;
                try { tok = JObject.Parse(raw); } catch { continue; }
            }
            var id = (tok as JObject)?["emojiId"]?.ToString();
            if (!string.IsNullOrEmpty(id)) return id;
        }
        return n["emojiId"]?.ToString();
    }

    private static dynamic NormalizeNotifV1(JObject n) => (dynamic)new {
        id             = n["id"]?.ToString() ?? "",
        type           = n["type"]?.ToString() ?? "",
        senderUserId   = n["senderUserId"]?.ToString() ?? "",
        senderUsername = n["senderUsername"]?.ToString() ?? "",
        message        = n["message"]?.ToString() ?? "",
        created_at     = n["created_at"]?.Type == JTokenType.Date
                           ? n["created_at"]!.Value<DateTime>().ToString("o")
                           : n["created_at"]?.ToString() ?? DateTime.UtcNow.ToString("o"),
        seen           = n["seen"]?.Value<bool>() ?? false,
        details        = n["details"],
        _v2            = false,
        _title         = (string?)null,
        _link          = (string?)null,
        _emojiId       = ExtractEmojiId(n),
    };

    private static dynamic NormalizeNotifV2(JObject n) => (dynamic)new {
        id             = n["id"]?.ToString() ?? "",
        type           = n["type"]?.ToString() ?? "",
        senderUserId   = n["senderUserId"]?.ToString() ?? "",
        // v2 uses senderDisplayName; fall back to senderUsername for safety
        senderUsername = n["senderDisplayName"]?.ToString()
                      ?? n["senderUsername"]?.ToString()
                      ?? "",
        message        = n["message"]?.ToString() ?? "",
        created_at     = n["createdAt"]?.Type == JTokenType.Date
                           ? n["createdAt"]!.Value<DateTime>().ToString("o")
                           : n["createdAt"]?.ToString() ?? DateTime.UtcNow.ToString("o"),
        seen           = n["seen"]?.Value<bool>() ?? false,
        details        = (object?)null,
        _v2            = true,
        _title         = n["title"]?.ToString(),
        _link          = n["link"]?.ToString(),
        _data          = n["data"],  // group-specific data: groupId, requestUserId, etc.
        _emojiId       = ExtractEmojiId(n),
    };

    // Core Logic

    private object? ProcessSingleNotif(dynamic n, bool prependToJs)
    {
        if (_core.Timeline.IsLoggedNotif((string)n.id)) return null;
        _core.Timeline.AddLoggedNotif((string)n.id);

        // VRCN Chat intercept
        var nType = (string)n.type;

        // Boop → store as chat entry so it appears in history and inbox
        if (nType == "boop")
        {
            var boopSender = (string?)n.senderUserId ?? "";
            if (!string.IsNullOrEmpty(boopSender))
            {
                var boopEntry = _friends.StoreChatMessage(boopSender, boopSender, "\ud83d\udc95 Boop!", "boop", (string?)n._emojiId);
                Invoke(() => _core.SendToJS("vrcChatMessage", boopEntry));
            }
        }

        // invite OR requestInvite whose slot text starts with "msg " are chat messages.
        if (nType == "invite" || nType == "requestInvite")
        {
            var invMsg = "";
            JObject? det = null;
            try
            {
                var rawDet = n.details as JToken;
                if (rawDet is JObject jo) det = jo;
                else if (rawDet?.Type == JTokenType.String) det = JObject.Parse(rawDet.ToString());
                // invite → inviteMessage, requestInvite → requestMessage
                invMsg = det?["inviteMessage"]?.ToString()
                      ?? det?["requestMessage"]?.ToString()
                      ?? "";
            }
            catch { }

            if (invMsg.StartsWith("msg "))
            {
                var chatText     = invMsg["msg ".Length..];
                var senderId     = (string?)n.senderUserId ?? "";
                var entry        = _friends.StoreChatMessage(senderId, senderId, chatText);
                var notifId      = (string)n.id;
                Invoke(() => _core.SendToJS("vrcChatMessage", entry));
                // Auto-hide & mark seen so it doesn't appear in notification panel
                if (!string.IsNullOrEmpty(notifId) && _core.VrcApi.IsLoggedIn)
                    _ = Task.Run(async () =>
                    {
                        await _core.Notifications.MarkNotificationReadAsync(notifId);
                        await _core.Notifications.HideNotificationAsync(notifId);
                    });
                return null; // skip timeline + notification panel
            }
        }

        // Permini auto-invite: silently invite and hide the notification
        if (nType == "requestInvite")
        {
            var pmSenderId = (string?)n.senderUserId ?? "";
            if (!string.IsNullOrEmpty(pmSenderId)
                && _core.PerminiList.TryGetValue(pmSenderId, out var pmEntry))
            {
                var myStatus  = _core.MyVrcStatus; // "active","join me","ask me","busy"
                var shouldAuto = myStatus switch
                {
                    "active"  or "join me" => pmEntry.allowActive,
                    "ask me"               => pmEntry.allowAskMe,
                    "busy"                 => pmEntry.allowDnD,
                    _                      => false,
                };
                if (shouldAuto)
                {
                    var pmNotifId = (string)n.id;
                    var pmIsV2   = (bool)n._v2;
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.Invite.InviteFriendAsync(pmSenderId, _core.LogWatcher.CurrentLocation ?? "");
                        if (ok) await _core.Notifications.HideNotificationAsync(pmNotifId, pmIsV2);
                        else    await _core.Notifications.AcceptNotificationAsync(pmNotifId);
                        Invoke(() => _core.SendToJS("log", new {
                            msg   = $"[Permini] Auto-invited {pmSenderId}: {(ok ? "OK" : "failed")}",
                            color = ok ? "ok" : "warn"
                        }));
                    });
                    return null; // don't show notification in panel
                }
            }
        }

        var senderImg    = "";
        var senderUserId = (string?)n.senderUserId;
        var notifIdForImg = (string?)n.id ?? "";
        // Check image cache first (covers group icons + non-friend user images from prior fetches)
        lock (_notifImageCache)
        {
            if (!string.IsNullOrEmpty(notifIdForImg))
                _notifImageCache.TryGetValue(notifIdForImg, out senderImg!);
        }
        senderImg ??= "";
        // Fallback to friend store
        if (string.IsNullOrEmpty(senderImg) && !string.IsNullOrEmpty(senderUserId))
        {
            if (_friends.TryGetNameImage(senderUserId, out var fi) && !string.IsNullOrEmpty(fi.image))
                senderImg = ImageCacheHelper.GetUserUrl(senderUserId, fi.image);
        }

        JObject? detObj = null;
        {
            var rawDet = n.details as JToken;
            if (rawDet is JObject jo) detObj = jo;
            else if (rawDet?.Type == JTokenType.String) { try { detObj = JObject.Parse(rawDet.ToString()); } catch { } }
        }

        // Extract message — for v1 invite responses the text lives in details, not message
        var msgText = (string)n.message;
        if (string.IsNullOrEmpty(msgText) && detObj != null)
        {
            msgText = detObj["responseMessage"]?.ToString()
                   ?? detObj["inviteMessage"]?.ToString()
                   ?? detObj["requestMessage"]?.ToString()
                   ?? "";
        }

        // Invite notifications carry the target instance in details.worldId
        var notifLoc       = detObj?["worldId"]?.ToString() ?? "";
        var notifWorldName = detObj?["worldName"]?.ToString() ?? "";
        var notifWorldId   = "";
        if (notifLoc.StartsWith("wrld_", StringComparison.Ordinal))
        {
            var ci = notifLoc.IndexOf(':');
            notifWorldId = ci > 0 ? notifLoc[..ci] : notifLoc;
        }
        else notifLoc = "";

        var notifEv = new TimelineService.TimelineEvent
        {
            Type        = "notification",
            Timestamp   = n.created_at,
            NotifId     = n.id,
            NotifType   = n.type,
            NotifTitle  = (string?)n._title ?? "",
            SenderName  = n.senderUsername,
            SenderId    = n.senderUserId,
            SenderImage = senderImg,
            Message     = msgText,
            Location    = notifLoc,
            WorldId     = notifWorldId,
            WorldName   = notifWorldName,
        };
        _core.Timeline.AddEvent(notifEv);

        // Build enriched notification for JS (includes sender image)
        var jsNotif = JObject.FromObject(n);
        jsNotif["_image"] = senderImg;

        // Push to VR overlay (wrist alerts + HMD toast) for actionable types
        PushToVrOverlay(n, senderImg);

        if (prependToJs)
            Invoke(() => {
                _core.SendToJS("vrcNotificationPrepend", jsNotif);
                _core.SendToJS("timelineEvent", _instance.BuildTimelinePayload(notifEv));
            });

        // Async image + name fetch if not cached
        if (!string.IsNullOrEmpty(senderUserId) && _core.VrcApi.IsLoggedIn)
        {
            var needsImg  = string.IsNullOrEmpty(senderImg);
            var needsName = string.IsNullOrEmpty((string?)n.senderUsername);
            if (needsImg || needsName)
            {
                var evId    = notifEv.Id;
                var uid     = senderUserId;
                var notifId = (string)n.id;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var profile = await _core.Users.GetUserAsync(uid);
                        if (profile == null) return;
                        var img  = needsImg  ? VRChatApiService.GetUserImage(profile) : "";
                        var name = needsName ? (profile["displayName"]?.ToString() ?? "") : "";
                        if (string.IsNullOrEmpty(img) && string.IsNullOrEmpty(name)) return;
                        if (!string.IsNullOrEmpty(img))
                        {
                            _core.Timeline.UpdateEvent(evId, ev => ev.SenderImage = img);
                            lock (_notifImageCache) _notifImageCache[notifId] = img;
                        }
                        if (!string.IsNullOrEmpty(name))
                            _core.Timeline.UpdateEvent(evId, ev => ev.SenderName = name);
                        Invoke(() =>
                        {
                            var updated = _core.Timeline.GetEvents().FirstOrDefault(e => e.Id == evId);
                            if (updated != null) _core.SendToJS("timelineEvent", _instance.BuildTimelinePayload(updated));
                            _core.SendToJS("vrcNotifImageUpdate", new { notifId, image = ImageCacheHelper.GetUserUrl(uid, img), senderUsername = name });
                        });
                    }
                    catch { }
                });
            }
        }

        // For group notifications: fetch group name + icon from _data
        // (runs for ALL group.* types — even when senderUserId is present, we want the group image)
        var notifTypeStr = (string)n.type;
        if (notifTypeStr.StartsWith("group.") && _core.VrcApi.IsLoggedIn)
        {
            JObject? dataObj = null;
            try
            {
                var rawData = n._data as JToken;
                if (rawData is JObject djo) dataObj = djo;
                else if (rawData?.Type == JTokenType.String) { try { dataObj = JObject.Parse(rawData.ToString()); } catch { } }
            }
            catch { }
            // Also try details (v1 group notifications)
            if (dataObj == null)
            {
                try
                {
                    var rawDet = n.details as JToken;
                    if (rawDet is JObject djo2) dataObj = djo2;
                    else if (rawDet?.Type == JTokenType.String) { try { dataObj = JObject.Parse(rawDet.ToString()); } catch { } }
                }
                catch { }
            }
            // groupId can be in "groupId" directly, or in "ownerId" (group.event.created uses ownerId = grp_xxx)
            var groupId = dataObj?["groupId"]?.ToString();
            if (string.IsNullOrEmpty(groupId))
            {
                var ownerId = dataObj?["ownerId"]?.ToString();
                if (!string.IsNullOrEmpty(ownerId) && ownerId.StartsWith("grp_")) groupId = ownerId;
            }
            // Fallback: extract from _link
            if (string.IsNullOrEmpty(groupId))
                groupId = ExtractGroupIdFromLink((string?)n._link);
            if (!string.IsNullOrEmpty(groupId))
            {
                var evId    = notifEv.Id;
                var notifId = (string)n.id;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var group = await _core.Groups.GetGroupAsync(groupId);
                        if (group == null) return;
                        var groupName = group["name"]?.ToString() ?? "";
                        var groupIcon = ImageCacheHelper.GetGroupUrl(groupId, group["iconUrl"]?.ToString());
                        if (string.IsNullOrEmpty(groupName) && string.IsNullOrEmpty(groupIcon)) return;
                        _core.Timeline.UpdateEvent(evId, ev => { ev.SenderName = groupName; ev.SenderImage = groupIcon; });
                        if (!string.IsNullOrEmpty(groupIcon))
                            lock (_notifImageCache) _notifImageCache[notifId] = groupIcon;
                        Invoke(() =>
                        {
                            var updated = _core.Timeline.GetEvents().FirstOrDefault(e => e.Id == evId);
                            if (updated != null) _core.SendToJS("timelineEvent", _instance.BuildTimelinePayload(updated));
                            _core.SendToJS("vrcNotifImageUpdate", new { notifId, image = ImageCacheHelper.GetGroupUrl(groupId, groupIcon), senderUsername = groupName });
                        });
                    }
                    catch { }
                });
            }
        }

        return _instance.BuildTimelinePayload(notifEv);
    }

    public Task GetNotificationsAsync() => Task.Run(async () =>
    {
        // Restore persisted v2 support flag — avoids one 404 call per session restart
        _core.VrcApi.NotifV2Supported = _core.Settings.NotifV2Supported;

        var t1 = _core.Notifications.GetNotificationsAsync();
        var t2 = _core.Notifications.GetNotificationsV2Async();
        await Task.WhenAll(t1, t2);

        // Persist if v2 was disabled this session (404)
        if (!_core.VrcApi.NotifV2Supported && _core.Settings.NotifV2Supported)
        {
            _core.Settings.NotifV2Supported = false;
            _core.Settings.Save();
        }

        var list = t1.Result.Cast<JObject>().Select(NormalizeNotifV1).ToList();
        Invoke(() => _core.SendToJS("log", new { msg = $"[Notif REST] v1={t1.Result.Count} types=[{string.Join(",", t1.Result.Cast<JObject>().Select(n => n["type"]?.ToString()))}]", color = "sec" }));

        var v2Ids = new HashSet<string>(list.Select(n => (string)n.id));
        foreach (JObject n in t2.Result.Cast<JObject>())
        {
            var id = n["id"]?.ToString() ?? "";
            if (v2Ids.Contains(id)) continue;
            list.Add(NormalizeNotifV2(n));
        }
        Invoke(() => _core.SendToJS("log", new { msg = $"[Notif REST] v2={t2.Result.Count} types=[{string.Join(",", t2.Result.Cast<JObject>().Select(n => n["type"]?.ToString()))}]", color = "sec" }));

        list = list.OrderByDescending(n => (string)n.created_at).ToList();

        // Seed in-memory cache from persisted timeline (survives restarts, safe with duplicates)
        lock (_notifImageCache)
        {
            foreach (var e in _core.Timeline.GetEvents())
            {
                if (e.Type == "notification" && !string.IsNullOrEmpty(e.NotifId)
                    && !string.IsNullOrEmpty(e.SenderImage))
                    _notifImageCache.TryAdd(e.NotifId, e.SenderImage);
            }
        }

        // Build enriched list for JS — check image cache first, then friend store
        var enrichedList = new JArray();
        foreach (var n in list)
        {
            var j = JObject.FromObject(n);
            var nid = (string?)n.id ?? "";
            var sid = (string?)n.senderUserId;
            string img = "";
            // Cache (in-memory + seeded from timeline DB above)
            lock (_notifImageCache)
            {
                if (!string.IsNullOrEmpty(nid) && _notifImageCache.TryGetValue(nid, out var cached))
                    img = cached ?? "";
            }
            // Fallback to friend store
            if (string.IsNullOrEmpty(img) && !string.IsNullOrEmpty(sid)
                && _friends.TryGetNameImage(sid, out var fi) && !string.IsNullOrEmpty(fi.image))
                img = fi.image;
            j["_image"] = img;
            enrichedList.Add(j);
        }

        var newTimeline = new List<object>();
        foreach (var n in list)
        {
            var ev = ProcessSingleNotif(n, prependToJs: false);
            if (ev != null) newTimeline.Add(ev);
        }

        Invoke(() =>
        {
            _core.SendToJS("vrcNotifications", enrichedList);
            foreach (var ev in newTimeline)
                _core.SendToJS("timelineEvent", ev);
        });
    });

    // Enriches a list of normalized notifications with user avatar images.
    // Priority: in-memory notif cache → friend store → disk image cache → API call.
    // API calls are batched in parallel and only made for users not found by faster means.
    private async Task<JArray> EnrichNotifUserImagesAsync(IEnumerable<dynamic> list)
    {
        var items = list.Select(n => (n, j: JObject.FromObject(n))).ToList();

        // Collect senderUserIds that still need an image after cheap lookups
        var needApi = new List<(string uid, JObject j)>();

        foreach (var (n, j) in items)
        {
            var sid = (string?)n.senderUserId;
            string img = "";

            // 1. In-memory notif image cache (seeded from timeline)
            lock (_notifImageCache)
            {
                var nid = (string?)n.id ?? "";
                if (!string.IsNullOrEmpty(nid)) _notifImageCache.TryGetValue(nid, out img!);
            }

            // 2. Friend store
            if (string.IsNullOrEmpty(img) && !string.IsNullOrEmpty(sid)
                && _friends.TryGetNameImage(sid, out var fi))
                img = fi.image ?? "";

            // 3. Disk image cache (already downloaded from a previous profile view)
            if (string.IsNullOrEmpty(img) && !string.IsNullOrEmpty(sid))
            {
                var diskPath = ImageCacheHelper.GetUserCached(sid);
                if (diskPath != null) img = ImageCacheHelper.ToLocalUrl(diskPath);
            }

            j["_image"] = img;

            // 4. Queue for API call if still empty
            if (string.IsNullOrEmpty(img) && !string.IsNullOrEmpty(sid))
                needApi.Add((sid, j));
        }

        // Batch API calls in parallel for users not found by any cheap method
        if (needApi.Count > 0)
        {
            await Task.WhenAll(needApi.Select(async entry =>
            {
                var user = await _core.Users.GetUserAsync(entry.uid);
                if (user != null)
                    entry.j["_image"] = ImageCacheHelper.GetUserUrl(entry.uid, VRChatApiService.GetUserImage(user));
            }));
        }

        var result = new JArray();
        foreach (var (_, j) in items) result.Add(j);
        return result;
    }

    public Task GetHiddenNotificationsAsync() => Task.Run(async () =>
    {
        var raw  = await _core.Notifications.GetHiddenFriendRequestsAsync();
        var list = raw.Cast<JObject>().Select(NormalizeNotifV1).ToList();

        // Build initial list immediately using only cheap sources (no API calls)
        var initial = new JArray();
        var needApi = new List<(string uid, string notifId)>();

        foreach (var n in list)
        {
            var j   = JObject.FromObject(n);
            var sid = (string?)n.senderUserId ?? "";
            var nid = (string?)n.id ?? "";
            string img = "";

            lock (_notifImageCache)
                if (!string.IsNullOrEmpty(nid)) _notifImageCache.TryGetValue(nid, out img!);

            if (string.IsNullOrEmpty(img) && !string.IsNullOrEmpty(sid)
                && _friends.TryGetNameImage(sid, out var fi))
                img = fi.image ?? "";

            if (string.IsNullOrEmpty(img) && !string.IsNullOrEmpty(sid))
            {
                var diskPath = ImageCacheHelper.GetUserCached(sid);
                if (diskPath != null) img = ImageCacheHelper.ToLocalUrl(diskPath);
            }

            j["_image"] = img;
            initial.Add(j);

            if (string.IsNullOrEmpty(img) && !string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(nid))
                needApi.Add((sid, nid));
        }

        // Send list immediately
        Invoke(() => _core.SendToJS("vrcHiddenNotifications", initial));

        // Fetch missing images in background, push updates one by one
        if (needApi.Count > 0)
        {
            await Task.WhenAll(needApi.Select(async entry =>
            {
                var user = await _core.Users.GetUserAsync(entry.uid);
                if (user == null) return;
                var img = ImageCacheHelper.GetUserUrl(entry.uid, VRChatApiService.GetUserImage(user));
                if (!string.IsNullOrEmpty(img))
                    Invoke(() => _core.SendToJS("vrcNotifImageUpdate", new { notifId = entry.notifId, image = img }));
            }));
        }
    });

    public Task GetAllNotificationsAsync() => Task.Run(async () =>
    {
        var t1 = _core.Notifications.GetNotificationsAsync();
        var t2 = _core.Notifications.GetNotificationsV2Async();
        var t3 = _core.Notifications.GetHiddenFriendRequestsAsync();
        await Task.WhenAll(t1, t2, t3);

        var list = t1.Result.Cast<JObject>().Select(NormalizeNotifV1).ToList();
        var seenIds = new HashSet<string>(list.Select(n => (string)n.id));
        foreach (JObject n in t2.Result.Cast<JObject>())
        {
            var id = n["id"]?.ToString() ?? "";
            if (!seenIds.Contains(id)) { list.Add(NormalizeNotifV2(n)); seenIds.Add(id); }
        }
        foreach (JObject n in t3.Result.Cast<JObject>())
        {
            var id = n["id"]?.ToString() ?? "";
            if (!seenIds.Contains(id)) { list.Add(NormalizeNotifV1(n)); seenIds.Add(id); }
        }
        var sorted   = list.OrderByDescending(n => (string)n.created_at);
        var enriched = await EnrichNotifUserImagesAsync(sorted);
        Invoke(() => _core.SendToJS("vrcAllNotifications", enriched));
    });

    // Push actionable notifications to VR overlay (wrist alerts tab + HMD toast)
#if !WINDOWS
    private void PushToVrOverlay(dynamic n, string senderImg) { }
#else
    private void PushToVrOverlay(dynamic n, string senderImg)
    {
        var vro = _core.VrOverlay;
        if (vro == null) return;

        var nType = (string)n.type;
        string? overlayEvType = nType switch
        {
            "friendRequest" => "notif_friendreq",
            "invite"        => "notif_invite",
            "group.invite"  => "notif_groupinvite",
            _               => null,
        };
        if (overlayEvType == null) return;

        var notifId    = (string)n.id;
        var senderName = (string?)n.senderUsername ?? "Unknown";
        var senderId   = (string?)n.senderUserId ?? "";
        var time       = VRCNext.Services.Helpers.DateTimeHelper.FormatTime(DateTime.Now);

        // Parse details & _data upfront
        JObject? det = null;
        JObject? data = null;
        try
        {
            var rawDet = n.details as JToken;
            if (rawDet is JObject jo) det = jo;
            else if (rawDet?.Type == JTokenType.String) det = JObject.Parse(rawDet.ToString());
        }
        catch { }
        try
        {
            var rawData = n._data as JToken;
            if (rawData is JObject djo) data = djo;
            else if (rawData?.Type == JTokenType.String) data = JObject.Parse(rawData.ToString());
        }
        catch { }

        // Extract extra data depending on type
        string notifData = "";
        string evText = "";
        if (nType == "friendRequest")
        {
            evText = "Friend Request";
        }
        else if (nType == "invite")
        {
            notifData = det?["worldId"]?.ToString() ?? "";
            evText = "World Invite";
        }
        else if (nType == "group.invite")
        {
            notifData = det?["groupId"]?.ToString()
                     ?? data?["groupId"]?.ToString()
                     ?? ExtractGroupIdFromLink((string?)n._link) ?? "";
            var groupName = (string?)n._title;
            evText = !string.IsNullOrEmpty(groupName) ? groupName : "Group Invite";
        }

        // Async: fetch all data (image, world name, group info) then show both wrist + toast at once
        var capturedEvType  = overlayEvType;
        var capturedNotifId = notifId;
        var capturedName    = senderName;
        var capturedEvText  = evText;
        var capturedImg     = senderImg;
        var capturedData    = notifData;

        _ = Task.Run(async () =>
        {
            try
            {
                // --- Resolve image (VRChat CDN images are public, no auth needed) ---
                if (string.IsNullOrEmpty(capturedImg) && _core.VrcApi.IsLoggedIn)
                {
                    if (nType is "friendRequest" or "invite" && !string.IsNullOrEmpty(senderId))
                    {
                        var profile = await _core.Users.GetUserAsync(senderId);
                        if (profile != null)
                            capturedImg = VRChatApiService.GetUserImage(profile);
                    }
                    else if (nType == "group.invite" && !string.IsNullOrEmpty(capturedData))
                    {
                        var group = await _core.Groups.GetGroupAsync(capturedData);
                        if (group != null)
                        {
                            var groupIcon = group["iconUrl"]?.ToString() ?? "";
                            var groupName = group["name"]?.ToString() ?? "";
                            if (!string.IsNullOrEmpty(groupIcon))
                                capturedImg = groupIcon;
                            if (!string.IsNullOrEmpty(groupName))
                                capturedEvText = groupName;
                        }
                    }
                }

                // --- Resolve world name for invite ---
                if (nType == "invite" && !string.IsNullOrEmpty(capturedData) && _core.VrcApi.IsLoggedIn)
                {
                    var worldId = capturedData.Contains(':') ? capturedData.Split(':')[0] : capturedData;
                    var world = await _core.World.GetWorldAsync(worldId);
                    var worldName = world?["name"]?.ToString();
                    if (!string.IsNullOrEmpty(worldName))
                        capturedEvText = $"→ {worldName}";
                }

                // --- Add to wrist overlay + enqueue toast with all data ready ---
                _core.VrOverlay?.AddNotification(capturedEvType, capturedName, capturedEvText, time,
                    capturedImg, senderId, "", capturedNotifId, capturedData);
                _core.VrOverlay?.EnqueueToast(capturedEvType, capturedName, capturedEvText, time, capturedImg, isFavorited: false);
                _core.SpeakToast?.Invoke(capturedEvType, capturedName, capturedEvText);
            }
            catch { }
        });
    }
#endif

    // Photino compatibility shim
    private static void Invoke(Action action) => action();
}
