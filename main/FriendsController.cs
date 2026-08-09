using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VRCNext.Services;
using VRCNext.Services.Helpers;

namespace VRCNext;

// owns all friend related state, logic, message handling, and WebSocket events.

public class FriendsController
{
    private readonly CoreLibrary _core;

    // Friend State
    private readonly Dictionary<string, JObject> _friendStore = new();
    private readonly Dictionary<string, string> _friendLastLoc = new();
    private readonly Dictionary<string, string> _friendCurrentGpsEventId = new();
    private readonly Dictionary<string, string> _friendLastStatus = new();
    private readonly Dictionary<string, string> _friendLastStatusDesc = new();
    private readonly Dictionary<string, string> _friendLastBio = new();
    private readonly Dictionary<string, string> _friendLastAvatarFileId = new();
    private readonly Dictionary<string, (string name, string image)> _friendNameImg = new();
    private readonly Dictionary<string, (string fvrtId, string groupName)> _favoriteFriends = new();
    private int _favFriendsInFlight = 0;
    private bool _friendStateSeeded;
    private readonly SemaphoreSlim _friendsRefreshLock = new(1, 1);
    private readonly HashSet<string> _profileRefreshInFlight = new();

    // Push-debounce: coalesces rapid WebSocket events into a single frontend send.
    // Trailing debounce (300 ms) with a hard max-wait (1500 ms) so a continuous
    // event stream still flushes periodically instead of being deferred forever.
    private CancellationTokenSource? _pushDebounce;
    private DateTime _pushEarliestFlush = DateTime.MinValue;
    private readonly object _pushLock = new();
    private const int PushDebounceMs = 300;
    private const int PushMaxDelayMs = 1500;

    // Chat Storage
    private static readonly string _chatDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCNext", "chat");
    public record ChatEntry(string id, string from, string text, string time, string? type = null, string? emoji = null);

    // Public Accessors (for other domains)
    public bool FriendStateSeeded => _friendStateSeeded;

    public (string name, string image) GetNameImage(string userId)
        => _friendNameImg.GetValueOrDefault(userId, ("", ""));

    public bool TryGetNameImage(string userId, out (string name, string image) result)
        => _friendNameImg.TryGetValue(userId, out result);

    public bool IsInStore(string userId)
    {
        lock (_friendStore) return _friendStore.ContainsKey(userId);
    }

    public bool IsFavorited(string userId) => _favoriteFriends.ContainsKey(userId);
    public string GetFavoriteFriendId(string userId)
        => _favoriteFriends.TryGetValue(userId, out var v) ? v.fvrtId : "";
    public string GetFavoriteFriendGroup(string userId)
        => _favoriteFriends.TryGetValue(userId, out var v) ? v.groupName : "group_0";

    public List<JObject> GetStoreSnapshot()
    {
        lock (_friendStore) return _friendStore.Values.ToList();
    }

    public JObject? GetStoreValue(string userId)
    {
        lock (_friendStore) return _friendStore.TryGetValue(userId, out var v) ? v : null;
    }

    public List<string> GetTrackedUserIds() => _friendNameImg.Keys.ToList();

    public string ResolvePlayerImage(string? userId, string? storedImage)
    {
        if (!string.IsNullOrEmpty(userId))
        {
            if (_friendNameImg.TryGetValue(userId, out var fi) && !string.IsNullOrEmpty(fi.image))
                return fi.image;
        }
        return storedImage ?? "";
    }

    public string ResolveWithDiskFallback(string? userId, string? storedImage)
    {
        if (string.IsNullOrEmpty(userId)) return storedImage ?? "";
        var disk = ImageCacheHelper.GetUserCached(userId);
        if (disk != null) return ImageCacheHelper.ToLocalUrl(disk);
        return ResolvePlayerImage(userId, storedImage);
    }

    private readonly Dictionary<string, DateTime> _recentMod = new();

    private async Task LogModerationEventAsync(string userId, string modType, bool active)
    {
        if (string.IsNullOrEmpty(userId)) return;
        var (mname, mimg) = _friendNameImg.GetValueOrDefault(userId, ("", ""));

        if (string.IsNullOrEmpty(mname))
        {
            try
            {
                var pl = _core.LogWatcher.GetCurrentPlayers().FirstOrDefault(p => p.UserId == userId);
                if (pl != null) mname = pl.DisplayName ?? "";
            }
            catch { }
        }

        if (string.IsNullOrEmpty(mname) && _core.TimeEngine.Users.TryGetValue(userId, out var uRec))
        {
            mname = uRec.DisplayName ?? "";
            if (string.IsNullOrEmpty(mimg)) mimg = uRec.Image ?? "";
        }

        if (string.IsNullOrEmpty(mname))
        {
            try
            {
                var cached = _core.TimeEngine.GetUserDetail(userId);
                if (cached != null)
                {
                    mname = cached.DisplayName ?? "";
                    if (string.IsNullOrEmpty(mimg)) mimg = cached.Image ?? "";
                }
            }
            catch { }
        }

        // Moderating someone you have never shared an instance with leaves no local
        // trace at all, so the API is the only remaining source for their name.
        if (string.IsNullOrEmpty(mname))
        {
            try
            {
                var u = await _core.Users.GetUserAsync(userId);
                if (u != null)
                {
                    mname = u["displayName"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(mimg)) mimg = VRChatApiService.GetUserImage(u);
                }
            }
            catch { }
        }

        LogModeration(userId, mname, mimg, modType, active);
    }

    public void LogModeration(string userId, string name, string image, string modType, bool active)
    {
        if (string.IsNullOrEmpty(modType)) return;
        if (string.IsNullOrEmpty(userId) && string.IsNullOrEmpty(name)) return;

        var now      = DateTime.UtcNow;
        var stateKey = modType + "|" + (active ? "1" : "0");
        var keys     = new List<string>();
        if (!string.IsNullOrEmpty(userId)) keys.Add(stateKey + "|u:" + userId);
        if (!string.IsNullOrEmpty(name))   keys.Add(stateKey + "|n:" + name.ToLowerInvariant());

        lock (_recentMod)
        {
            if (_recentMod.Count > 256)
                foreach (var k in _recentMod.Where(kv => (now - kv.Value).TotalSeconds > 30).Select(kv => kv.Key).ToList())
                    _recentMod.Remove(k);

            foreach (var k in keys)
                if (_recentMod.TryGetValue(k, out var ts) && (now - ts).TotalSeconds < 6)
                {
                    foreach (var kk in keys) _recentMod[kk] = now;
                    return;
                }
            foreach (var k in keys) _recentMod[k] = now;
        }

        var ev = new TimelineService.TimelineEvent
        {
            Type      = "moderation",
            UserId    = userId,
            UserName  = name,
            UserImage = image,
            NotifType = modType,
            Message   = active ? "on" : "off",
        };
        _core.Timeline.AddEvent(ev);
        _core.SendToJS("timelineEvent", new
        {
            id        = ev.Id,
            type      = ev.Type,
            timestamp = ev.Timestamp,
            userId    = ev.UserId,
            userName  = ev.UserName,
            userImage = ResolveWithDiskFallback(ev.UserId, ev.UserImage),
            notifType = ev.NotifType,
            message   = ev.Message,
        });
    }

    // Constructor

    public FriendsController(CoreLibrary core) => _core = core;

    // WebSocket Wiring

    public void WireWebSocket(VRChatWebSocketService ws)
    {
        ws.FriendsChanged += (_, _) =>
        {
            if (_core.VrcApi.IsLoggedIn && _friendStateSeeded)
                PushFriendsFromStore();
        };

        ws.FriendListChanged += (_, _) =>
        {
            if (_core.VrcApi.IsLoggedIn)
                _ = RefreshFriendsAsync(true);
        };

        ws.FriendLocationChanged += OnWsFriendLocation;
        ws.FriendWentOffline     += OnWsFriendOffline;
        ws.FriendWentOnline      += OnWsFriendOnline;
        ws.FriendUpdated         += OnWsFriendUpdated;
        ws.FriendBecameActive    += OnWsFriendActive;
        ws.FriendAdded           += OnWsFriendAdded;
        ws.FriendRemoved         += OnWsFriendRemoved;
    }

    // Message Handler

    public async Task HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "vrcRefreshFriends":
                await RefreshFriendsAsync();
                break;

            case "vrcUpdateStatus":
                await UpdateStatusAsync(
                    msg["status"]?.ToString() ?? "active",
                    msg["statusDescription"]?.ToString() ?? "");
                break;

            case "vrcGetFriendDetail":
                var fdId = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(fdId))
                    await GetFriendDetailAsync(fdId);
                break;

            case "vrcGetUserBasic":
            {
                var ubId        = msg["userId"]?.ToString();
                var ubCtx       = msg["contextId"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(ubId)) break;

                // 1. Live friend store (fastest, no API call)
                JObject? ubLive;
                lock (_friendStore) _friendStore.TryGetValue(ubId, out ubLive);
                if (ubLive != null)
                {
                    _core.SendToJS("vrcUserBasic", new {
                        contextId         = ubCtx,
                        id                = ubId,
                        displayName       = ubLive["displayName"]?.ToString() ?? "",
                        image             = await ResolveUserImageAsync(ubId, VRChatApiService.GetUserImage(ubLive)),
                        status            = ubLive["status"]?.ToString() ?? "offline",
                        statusDescription = ubLive["statusDescription"]?.ToString() ?? "",
                    });
                    break;
                }

                // 2. SQLite cache (fast, no API call)
                var ubCache = _core.TimeEngine.GetUserProfileCache(ubId);
                if (ubCache != null)
                {
                    _core.SendToJS("vrcUserBasic", new {
                        contextId         = ubCtx,
                        id                = ubId,
                        displayName       = ubCache.DisplayName,
                        image             = await ResolveUserImageAsync(ubId, ubCache.Image),
                        status            = ubCache.ProfileStatus,
                        statusDescription = ubCache.ProfileStatusDesc,
                    });
                    break;
                }

                var ubUser = await _core.Users.GetUserAsync(ubId);
                if (ubUser != null)
                {
                    var ubRawImg = VRChatApiService.GetUserImage(ubUser);
                    var ubImg = await ResolveUserImageAsync(ubId, ubRawImg);
                    var ubName = ubUser["displayName"]?.ToString() ?? "";
                    var ubStatus = ubUser["status"]?.ToString() ?? "offline";
                    var ubStatusDesc = ubUser["statusDescription"]?.ToString() ?? "";

                    _core.TimeEngine.SaveUserProfileCache(ubId, new JObject
                    {
                        ["id"]                    = ubId,
                        ["displayName"]           = ubName,
                        ["image"]                 = ubImg,
                        ["status"]                = ubStatus,
                        ["statusDescription"]     = ubStatusDesc,
                        ["bio"]                   = ubUser["bio"]?.ToString() ?? "",
                        ["dateJoined"]            = ubUser["date_joined"]?.ToString() ?? "",
                        ["lastLogin"]             = ParseIsoDate(ubUser["last_login"]),
                        ["lastActivity"]          = ParseIsoDate(ubUser["last_activity"]),
                        ["currentAvatarId"]       = ubUser["currentAvatar"]?.ToString() ?? "",
                        ["currentAvatarImageUrl"] = ImageCacheHelper.GetAvatarUrl(ubUser["currentAvatar"]?.ToString(), ubUser["currentAvatarImageUrl"]?.ToString()),
                        ["profilePicOverride"]    = ImageCacheHelper.GetUserPicOverrideUrl(ubId, ubUser["profilePicOverride"]?.ToString()),
                        ["bannerUrl"]             = ImageCacheHelper.GetUserBannerUrl(ubId, ubUser["bannerUrl"]?.ToString()),
                        ["pronouns"]              = ubUser["pronouns"]?.ToString() ?? "",
                        ["tags"]                  = ubUser["tags"] as JArray ?? new JArray(),
                        ["badges"]                = ubUser["badges"] as JArray ?? new JArray(),
                    }.ToString());

                    _core.SendToJS("vrcUserBasic", new {
                        contextId         = ubCtx,
                        id                = ubId,
                        displayName       = ubName,
                        image             = ubImg,
                        status            = ubStatus,
                        statusDescription = ubStatusDesc,
                    });
                }
                break;
            }

            case "vrcGetFriendPreview":
            {
                var prevId = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(prevId))
                {
                    var bio = "";
                    var profilePicOverride = "";
                    var bannerUrl = "";

                    // SQLite cache
                    var bgType = ""; var bgTexture = ""; var bgTop = ""; var bgBottom = "";
                    var thBtn = ""; var thIcon = ""; var thSub = "";
                    var prevSqlite = _core.TimeEngine.GetUserProfileCache(prevId);
                    if (prevSqlite != null)
                    {
                        bio = prevSqlite.ProfileBio;
                        profilePicOverride = prevSqlite.ProfilePicOverride;
                        bannerUrl          = prevSqlite.ProfileBannerUrl;
                        bgType    = prevSqlite.ProfileBgType;
                        bgTexture = prevSqlite.ProfileBgTexture;
                        bgTop     = prevSqlite.ProfileBgGradTop;
                        bgBottom  = prevSqlite.ProfileBgGradBottom;
                        thBtn     = prevSqlite.ProfileThemeButton;
                        thIcon    = prevSqlite.ProfileThemeIcon;
                        thSub     = prevSqlite.ProfileThemeSubtext;
                    }

                    // Live API fallback if no SQLite cache yet
                    if (string.IsNullOrEmpty(bio))
                    {
                        _core.SendToJS("vrcFriendPreview", new { id = prevId, bio, profilePicOverride, bannerUrl });
                        var user = await _core.Users.GetUserAsync(prevId);
                        if (user != null)
                        {
                            bio = user["bio"]?.ToString() ?? "";
                            var pic = user["profilePicOverride"]?.ToString() ?? "";
                            if (!string.IsNullOrEmpty(pic)) profilePicOverride = pic;
                        }
                    }

                    // Only pulled when the feature is on - this fires on every hover and
                    // the appearance endpoint is a separate request per user.
                    var needAppearance = (_core.Settings.EnableProfileBackgrounds && string.IsNullOrEmpty(bgType))
                                     || (_core.Settings.EnableProfileThemes && string.IsNullOrEmpty(thBtn));
                    if (needAppearance)
                    {
                        var prevAppearance = await _core.Users.GetProfileAppearanceAsync(prevId);
                        if (prevAppearance != null)
                        {
                            bgType    = prevAppearance["backgroundType"]?.ToString() ?? "";
                            bgTexture = prevAppearance["backgroundTextureId"]?.ToString() ?? "";
                            bgTop     = prevAppearance["backgroundGradientTop"]?.ToString() ?? "";
                            bgBottom  = prevAppearance["backgroundGradientBottom"]?.ToString() ?? "";
                            var pvTheme = ResolveActiveTheme(prevAppearance);
                            thBtn  = pvTheme.button;
                            thIcon = pvTheme.icon;
                            thSub  = pvTheme.subtext;
                        }
                    }

                    _core.SendToJS("vrcFriendPreview", new {
                        id = prevId,
                        bio,
                        profilePicOverride = ImageCacheHelper.GetUserPicOverrideUrl(prevId, profilePicOverride),
                        bannerUrl          = ImageCacheHelper.GetUserBannerUrl(prevId, bannerUrl),
                        backgroundType           = bgType,
                        backgroundTextureId      = bgTexture,
                        backgroundTextureUrl     = ProfileBackgroundHelper.UrlFor(bgTexture),
                        backgroundGradientTop    = bgTop,
                        backgroundGradientBottom = bgBottom,
                        themeButtonColor         = thBtn,
                        themeIconColor           = thIcon,
                        themeSubtextColor        = thSub,
                    });
                }
                break;
            }
            // Refactor later for new group-mutual endpoint parsing
            case "vrcLookupAvatarByFileId":
            {
                var fileId = msg["fileId"]?.ToString() ?? "";
                var openModal = msg["openModal"]?.Value<bool>() ?? false;
                var forUserId = msg["userId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(fileId))
                {
                    var (avtrId, avtrData) = await _core.Avatars.GetAvatarIdByFileIdAsync(fileId);
                    string avatarName = "", avatarImage = "", avatarAuthor = "";
                    if (!string.IsNullOrEmpty(avtrId))
                    {
                        avatarName = avtrData?["name"]?.ToString() ?? "";
                        avatarImage = ImageCacheHelper.GetAvatarUrl(avtrId, avtrData?["imageUrl"]?.ToString());
                        avatarAuthor = avtrData?["authorName"]?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(forUserId))
                            _core.TimeEngine.SetAvatarInfoCache(forUserId, fileId, avtrId, avatarName, avatarAuthor);
                    }
                    _core.SendToJS("vrcAvatarByFileId", new { fileId, avatarId = avtrId ?? "", avatarName, avatarImage, avatarAuthor, openModal });
                }
                break;
            }

            case "vrcGetAvatarInfo":
            {
                var avtrId = msg["avatarId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(avtrId))
                {
                    if (ModalCacheHelper.IsCached(avtrId)) break;
                    ModalCacheHelper.Mark(avtrId);
                    var avtrObj = await _core.Avatars.GetAvatarAsync(avtrId);
                    var avatarName = avtrObj?["name"]?.ToString() ?? "";
                    var avatarImage = ImageCacheHelper.GetAvatarUrl(avtrId, avtrObj?["imageUrl"]?.ToString());
                    var avatarAuthor = avtrObj?["authorName"]?.ToString() ?? "";
                    _core.SendToJS("vrcAvatarInfo", new { avatarId = avtrId, avatarName, avatarImage, avatarAuthor });
                }
                break;
            }

            case "vrcGetInstanceAvatars":
            {
                var idsToken = msg["userIds"];
                if (idsToken is JArray idsArr && idsArr.Count > 0)
                {
                    var ids = idsArr.Select(t => t.ToString()).Where(s => !string.IsNullOrEmpty(s)).ToList();
                    _ = Task.Run(async () =>
                    {
                        foreach (var uid in ids)
                        {
                            try
                            {
                                const string RobotFileId = "file_0e8c4e32-7444-44ea-ade4-313c010d4bae";
                                string fileId = "";
                                JObject? stored = GetStoreValue(uid);
                                if (stored != null)
                                    fileId = ExtractAvatarFileId(stored);
                                if (fileId == RobotFileId) fileId = "";

                                JObject? userObj = null;
                                if (string.IsNullOrEmpty(fileId))
                                {
                                    userObj = await _core.Users.GetUserAsync(uid);
                                    if (userObj != null) fileId = ExtractAvatarFileId(userObj);
                                    if (fileId == RobotFileId) fileId = "";
                                }

                                // Fallback: represented group → members/search endpoint (exposes real avatar URL)
                                if (string.IsNullOrEmpty(fileId))
                                {
                                    var displayName = stored?["displayName"]?.ToString()
                                                   ?? userObj?["displayName"]?.ToString() ?? "";
                                    var repGroup = await _core.Users.GetUserRepresentedGroupAsync(uid);
                                    var groupId = repGroup?["groupId"]?.ToString() ?? repGroup?["id"]?.ToString() ?? "";
                                    if (!string.IsNullOrEmpty(groupId) && !string.IsNullOrEmpty(displayName))
                                    {
                                        var member = await _core.Groups.FindGroupMemberByDisplayNameAsync(groupId, displayName, uid);
                                        if (member != null)
                                            fileId = ExtractAvatarFileId(member["user"] as JObject ?? member);
                                    }
                                }

                                string avtrId = "";
                                if (!string.IsNullOrEmpty(fileId))
                                    avtrId = (await _core.Avatars.GetAvatarIdByFileIdAsync(fileId)).id ?? "";
                                _core.SendToJS("vrcInstanceAvatarFound", new { userId = uid, avatarId = avtrId });
                            }
                            catch
                            {
                                _core.SendToJS("vrcInstanceAvatarFound", new { userId = uid, avatarId = "" });
                            }
                        }
                    });
                }
                break;
            }

            case "vrcGetUserAvatars":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid) && uid == (_core.VrcApi.CurrentUserId ?? ""))
                {
                    try
                    {
                        var own = await _core.Avatars.GetOwnAvatarsAsync();
                        var ownAvatars = own.Select(a => new
                        {
                            id                = a["id"]?.ToString() ?? "",
                            name              = a["name"]?.ToString() ?? "",
                            thumbnailImageUrl = ImageCacheHelper.GetAvatarUrl(a["id"]?.ToString(), a["thumbnailImageUrl"]?.ToString() ?? a["imageUrl"]?.ToString()),
                            imageUrl          = ImageCacheHelper.GetAvatarUrl(a["id"]?.ToString(), a["imageUrl"]?.ToString() ?? a["thumbnailImageUrl"]?.ToString()),
                            authorName        = a["authorName"]?.ToString() ?? "",
                            releaseStatus     = a["releaseStatus"]?.ToString() ?? "private",
                            unityPackages     = a["unityPackages"] as JArray ?? new JArray(),
                        }).ToList();
                        _core.SendToJS("vrcUserAvatars", new { userId = uid, avatars = ownAvatars });
                    }
                    catch
                    {
                        _core.SendToJS("vrcUserAvatars", new { userId = uid, avatars = new JArray() });
                    }
                    break;
                }
                if (!string.IsNullOrEmpty(uid))
                {
                    try
                    {
                        // Serve from SQLite cache if fresh (TTL 1 day)
                        var avDbCache = _core.TimeEngine.GetUserProfileCache(uid);
                        if (IsDbCacheFresh(avDbCache?.ContentCachedAt, TimeSpan.FromDays(1)))
                        {
                            var cached = TryParseJObject(avDbCache!.ContentJson);
                            // avatarsV gates the cache: bumped to 2 when paginated fetch
                            // landed, so old truncated (single-page) caches are refetched once.
                            if (cached?["avatarsV"]?.Value<int>() == 2 && cached["avatars"] is JArray cachedAvtrs)
                            {
                                foreach (var a in cachedAvtrs)
                                    if (a is JObject ao)
                                    {
                                        ao["imageUrl"] = ImageCacheHelper.GetAvatarUrl(ao["id"]?.ToString(), ao["imageUrl"]?.ToString() ?? ao["thumbnailImageUrl"]?.ToString());
                                        ao["thumbnailImageUrl"] = ao["imageUrl"];
                                    }
                                _core.SendToJS("vrcUserAvatars", new { userId = uid, avatars = cachedAvtrs });
                                break;
                            }
                        }

                        var raw = await _core.Avatars.SearchAvatarsByAuthorAsync(uid);
                        foreach (var a in raw.Cast<JObject>())
                        {
                            var vid = a["vrc_id"]?.ToString() ?? a["id"]?.ToString() ?? "";
                            if (vid.StartsWith("avtr_")) _core.VrcndbSubmit?.Invoke(vid);
                        }
                        var avatars = raw.Cast<JObject>().Select(a => new
                        {
                            id                = a["vrc_id"]?.ToString() ?? a["id"]?.ToString() ?? "",
                            name              = a["name"]?.ToString() ?? "",
                            thumbnailImageUrl = ImageCacheHelper.GetAvatarUrl(a["vrc_id"]?.ToString() ?? a["id"]?.ToString(), a["image_url"]?.ToString() ?? a["thumbnailImageUrl"]?.ToString() ?? a["imageUrl"]?.ToString()),
                            imageUrl          = ImageCacheHelper.GetAvatarUrl(a["vrc_id"]?.ToString() ?? a["id"]?.ToString(), a["image_url"]?.ToString() ?? a["imageUrl"]?.ToString()),
                            authorName        = a["author"]?["name"]?.ToString() ?? a["authorName"]?.ToString() ?? "",
                            releaseStatus     = "public",
                            compatibility     = a["compatibility"] as JArray ?? new JArray(),
                        }).ToList();

                        var avDbCache2 = _core.TimeEngine.GetUserProfileCache(uid);
                        var cf2 = (TryParseJObject(avDbCache2?.ContentJson ?? "") ?? new JObject());
                        cf2["avatars"] = JToken.FromObject(avatars);
                        cf2["avatarsV"] = 2;
                        _core.TimeEngine.SaveUserContentCache(uid, cf2.ToString(Newtonsoft.Json.Formatting.None));
                        _core.SendToJS("vrcUserAvatars", new { userId = uid, avatars });
                    }
                    catch
                    {
                        _core.SendToJS("vrcUserAvatars", new { userId = uid, avatars = new JArray() });
                    }
                }
                break;
            }

            case "vrcJoinFriend":
                var joinLoc = msg["location"]?.ToString();
                if (!string.IsNullOrEmpty(joinLoc))
                    await HandleJoinFriendAsync(joinLoc);
                break;

            case "vrcInviteFriend":
            {
                var uid = msg["userId"]?.ToString();
                var slot = msg["messageSlot"]?.Value<int?>();
                if (!string.IsNullOrEmpty(uid))
                {
                    var ok = await _core.Invite.InviteFriendAsync(uid, _core.LogWatcher.CurrentLocation ?? "", slot);
                    _core.SendToJS("vrcActionResult", new
                    {
                        action = "invite", success = ok,
                        message = ok ? "Invite sent!" : "Failed to send invite. Make sure you are in a valid instance."
                    });
                }
                break;
            }

            case "vrcInviteFriendWithPhoto":
            {
                var uid = msg["userId"]?.ToString();
                var fileUrl = msg["fileUrl"]?.ToString();
                var slot = msg["messageSlot"]?.Value<int?>();
                if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(fileUrl))
                {
                    var ok = await _core.Invite.InviteFriendWithPhotoAsync(uid, _core.LogWatcher.CurrentLocation ?? "", fileUrl, slot);
                    _core.SendToJS("vrcActionResult", new
                    {
                        action = "invite", success = ok,
                        message = ok ? "Invite sent!" : "Failed to send invite. Make sure you are in a valid instance."
                    });
                }
                break;
            }

            case "vrcGetInviteMessages":
            {
                var uid = msg["userId"]?.ToString() ?? _core.VrcApi.CurrentUserId;
                if (!string.IsNullOrEmpty(uid))
                {
                    var msgs = await _core.Invite.GetInviteMessagesAsync(uid);
                    _core.SendToJS("vrcInviteMessages", msgs ?? new JArray());
                }
                break;
            }

            case "vrcUpdateInviteMessage":
            {
                var uid = msg["userId"]?.ToString() ?? _core.VrcApi.CurrentUserId;
                var slot = msg["slot"]?.Value<int>() ?? -1;
                var text = msg["message"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(uid) && slot >= 0 && !string.IsNullOrEmpty(text))
                {
                    var (ok, arr, cooldown) = await _core.Invite.UpdateInviteMessageAsync(uid, slot, text);
                    if (ok && arr != null)
                        _core.SendToJS("vrcInviteMessages", arr);
                    else
                        _core.SendToJS("vrcInviteMessageUpdateFailed", new { slot, cooldown });
                }
                break;
            }

            case "vrcRequestInvite":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                {
                    var ok = await _core.Invite.RequestInviteAsync(uid);
                    _core.SendToJS("vrcActionResult", new
                    {
                        action = "requestInvite", success = ok,
                        message = ok ? "Invite request sent!" : "Failed to request invite."
                    });
                }
                break;
            }

            case "vrcUpdateNote":
            {
                var uid = msg["userId"]?.ToString() ?? "";
                var note = msg["note"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(uid))
                {
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.Users.UpdateUserNoteAsync(uid, note);
                        _core.SendToJS("vrcNoteUpdated", new { success = ok, userId = uid, note });
                    });
                }
                break;
            }

            case "setUserMemo":
            {
                var uid = msg["userId"]?.ToString() ?? "";
                var memo = msg["memo"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(uid))
                {
                    _core.Timeline?.SetUserMemo(uid, memo);
                    _core.SendToJS("userMemoUpdated", new { userId = uid, memo });
                }
                break;
            }

            case "vrcBatchInvite":
            {
                var ids = msg["userIds"]?.ToObject<List<string>>() ?? new();
                var locOverride = msg["location"]?.ToString();
                if (ids.Count > 0)
                {
                    _ = Task.Run(async () =>
                    {
                        int done = 0, success = 0, fail = 0;
                        int total = ids.Count;
                        var loc = !string.IsNullOrEmpty(locOverride) ? locOverride : (_core.LogWatcher.CurrentLocation ?? "");
                        foreach (var uid in ids)
                        {
                            var ok = await _core.Invite.InviteFriendAsync(uid, loc);
                            done++;
                            if (ok) success++; else fail++;
                            _core.SendToJS("vrcBatchInviteProgress", new { done, total, success, fail });
                            if (done < total) await Task.Delay(1500);
                        }
                    });
                }
                break;
            }

            case "vrcGetFavoriteFriends":
                _ = Task.Run(async () =>
                {
                    if (_core.Settings.FfcEnabled)
                    {
                        var cached = _core.Cache.LoadRaw(CacheHandler.KeyFavFriends);
                        if (cached != null) _core.SendToJS("vrcFavoriteFriends", cached);
                    }
                    await FetchAndCacheFavFriendsAsync();
                });
                break;

            case "vrcAddFavoriteFriend":
            {
                var uid = msg["userId"]?.ToString() ?? "";
                var groupName = msg["groupName"]?.ToString() ?? "group_0";
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_core.LocalFavorites.IsLocalGroup(groupName))
                        {
                            var (lok, lerr, lid) = _core.LocalFavorites.AddItem(groupName, "friend", uid, new JObject());
                            if (lok) _core.SendToJS("vrcFavoriteFriendToggled", new { userId = uid, fvrtId = lid, isFavorited = true, groupName });
                            else _core.SendToJS("vrcLocalGroupResult", new { ok = false, kind = "friend", action = "add", error = lerr });
                            return;
                        }
                        var result = await _core.Favorites.AddFavoriteFriendAsync(uid, groupName);
                        if (result == null) return;
                        var fvrtId = result["id"]?.ToString() ?? "";
                        if (string.IsNullOrEmpty(fvrtId)) return;
                        lock (_favoriteFriends) _favoriteFriends[uid] = (fvrtId, groupName);
                        _core.SendToJS("vrcFavoriteFriendToggled", new { userId = uid, fvrtId, isFavorited = true, groupName });
                    }
                    catch { }
                });
                break;
            }

            case "vrcRemoveFavoriteFriend":
            {
                var uid = msg["userId"]?.ToString() ?? "";
                var fvrtId = msg["fvrtId"]?.ToString() ?? "";
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (LocalFavoritesStore.IsLocalId(fvrtId))
                        {
                            if (_core.LocalFavorites.RemoveItem(fvrtId))
                                _core.SendToJS("vrcFavoriteFriendToggled", new { userId = uid, fvrtId = "", isFavorited = false });
                            return;
                        }
                        var ok = await _core.Favorites.RemoveFavoriteFriendAsync(fvrtId);
                        if (!ok) return;
                        lock (_favoriteFriends) _favoriteFriends.Remove(uid);
                        _core.SendToJS("vrcFavoriteFriendToggled", new { userId = uid, fvrtId = "", isFavorited = false });
                    }
                    catch { }
                });
                break;
            }

            case "vrcAddFavoriteFriendToGroup":
            {
                var uid     = msg["userId"]?.ToString() ?? "";
                var group   = msg["groupName"]?.ToString() ?? "group_0";
                var oldFvrt = msg["oldFvrtId"]?.ToString();
                _ = Task.Run(async () =>
                {
                    bool targetLocal = _core.LocalFavorites.IsLocalGroup(group);
                    if (LocalFavoritesStore.IsLocalId(oldFvrt)) { _core.LocalFavorites.RemoveItem(oldFvrt!); oldFvrt = null; }
                    else if (targetLocal && !string.IsNullOrEmpty(oldFvrt)) { await _core.Favorites.RemoveFavoriteFriendAsync(oldFvrt); lock (_favoriteFriends) _favoriteFriends.Remove(uid); oldFvrt = null; }
                    if (targetLocal)
                    {
                        var (lok, lerr, lid) = _core.LocalFavorites.AddItem(group, "friend", uid, new JObject());
                        _core.SendToJS("vrcFriendFavoriteResult",
                            new { ok = lok, userId = uid, groupName = group, newFvrtId = lok ? lid : "", error = lok ? "" : lerr });
                        return;
                    }
                    var (ok, resultData) = await _core.Favorites.AddFavoriteFriendToGroupAsync(uid, group, oldFvrt);
                    if (ok) lock (_favoriteFriends) _favoriteFriends[uid] = (resultData, group);
                    _core.SendToJS("vrcFriendFavoriteResult",
                        new { ok, userId = uid, groupName = group, newFvrtId = ok ? resultData : "", error = ok ? "" : resultData });
                });
                break;
            }


            case "vrcSendFriendRequest":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                {
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.Friends.SendFriendRequestAsync(uid);
                        _core.SendToJS("vrcActionResult", new { action = "friendRequest", success = ok,
                            message = ok ? "Friend request sent!" : "Failed to send request" });
                    });
                }
                break;
            }

            case "vrcUnfriend":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                {
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.Friends.UnfriendAsync(uid);
                        _core.SendToJS("vrcActionResult", new { action = "unfriend", success = ok,
                            message = ok ? "Unfriended" : "Failed to unfriend" });
                        if (ok) _core.SendToJS("vrcUnfriendDone", new { userId = uid });
                    });
                }
                break;
            }

            case "vrcGetBlocked":
                _ = Task.Run(async () =>
                {
                    var arr = await _core.PlayerModeration.GetPlayerModerationsAsync("block");
                    await EnrichModerationsWithImagesAsync(arr);
                    _core.SendToJS("vrcBlockedList", arr);
                });
                break;

            case "vrcGetMuted":
                _ = Task.Run(async () =>
                {
                    var arr = await _core.PlayerModeration.GetPlayerModerationsAsync("mute");
                    await EnrichModerationsWithImagesAsync(arr);
                    _core.SendToJS("vrcMutedList", arr);
                });
                break;

            case "vrcGetAllModerations":
                _ = Task.Run(async () =>
                {
                    var tasks = new[]
                    {
                        _core.PlayerModeration.GetPlayerModerationsAsync("block"),
                        _core.PlayerModeration.GetPlayerModerationsAsync("mute"),
                        _core.PlayerModeration.GetPlayerModerationsAsync("hideAvatar"),
                        _core.PlayerModeration.GetPlayerModerationsAsync("interactOff"),
                        _core.PlayerModeration.GetPlayerModerationsAsync("muteChat"),
                    };
                    await Task.WhenAll(tasks);
                    var blockArr   = tasks[0].Result;
                    var muteArr    = tasks[1].Result;
                    var hideArr    = tasks[2].Result;
                    var interactArr= tasks[3].Result;
                    var chatArr    = tasks[4].Result;
                    await EnrichModerationsWithImagesAsync(blockArr);
                    await EnrichModerationsWithImagesAsync(muteArr);
                    _core.SendToJS("vrcBlockedList",    blockArr);
                    _core.SendToJS("vrcMutedList",      muteArr);
                    _core.SendToJS("vrcHideAvatarList", hideArr);
                    _core.SendToJS("vrcInteractOffList",interactArr);
                    _core.SendToJS("vrcMuteChatList",   chatArr);
                });
                break;

            case "vrcBlock":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                {
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.PlayerModeration.ModerateUserAsync(uid, "block");
                        _core.SendToJS("vrcActionResult", new { action = "block", success = ok,
                            message = ok ? "Blocked" : "Failed to block" });
                        if (ok) { _core.SendToJS("vrcModDone", new { userId = uid, type = "block", active = true }); await LogModerationEventAsync(uid,"block", true); }
                    });
                }
                break;
            }

            case "vrcMute":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                {
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.PlayerModeration.ModerateUserAsync(uid, "mute");
                        _core.SendToJS("vrcActionResult", new { action = "mute", success = ok,
                            message = ok ? "Muted" : "Failed to mute" });
                        if (ok) { _core.SendToJS("vrcModDone", new { userId = uid, type = "mute", active = true }); await LogModerationEventAsync(uid,"mute", true); }
                    });
                }
                break;
            }

            case "vrcUnblock":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                {
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.PlayerModeration.UnmoderateUserAsync(uid, "block");
                        _core.SendToJS("vrcActionResult", new { action = "unblock", success = ok,
                            message = ok ? "Unblocked" : "Failed to unblock" });
                        if (ok) { _core.SendToJS("vrcModDone", new { userId = uid, type = "block", active = false }); await LogModerationEventAsync(uid,"block", false); }
                    });
                }
                break;
            }

            case "vrcUnmute":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                {
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.PlayerModeration.UnmoderateUserAsync(uid, "mute");
                        _core.SendToJS("vrcActionResult", new { action = "unmute", success = ok,
                            message = ok ? "Unmuted" : "Failed to unmute" });
                        if (ok) { _core.SendToJS("vrcModDone", new { userId = uid, type = "mute", active = false }); await LogModerationEventAsync(uid,"mute", false); }
                    });
                }
                break;
            }

            case "vrcHideAvatar":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.PlayerModeration.ModerateUserAsync(uid, "hideAvatar");
                        if (ok) { _core.SendToJS("vrcModDone", new { userId = uid, type = "hideAvatar", active = true }); await LogModerationEventAsync(uid,"hideAvatar", true); }
                    });
                break;
            }

            case "vrcShowAvatar":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.PlayerModeration.UnmoderateUserAsync(uid, "hideAvatar");
                        if (ok) { _core.SendToJS("vrcModDone", new { userId = uid, type = "hideAvatar", active = false }); await LogModerationEventAsync(uid,"hideAvatar", false); }
                    });
                break;
            }

            case "vrcInteractOff":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.PlayerModeration.ModerateUserAsync(uid, "interactOff");
                        if (ok) { _core.SendToJS("vrcModDone", new { userId = uid, type = "interactOff", active = true }); await LogModerationEventAsync(uid,"interactOff", true); }
                    });
                break;
            }

            case "vrcInteractOn":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.PlayerModeration.UnmoderateUserAsync(uid, "interactOff");
                        if (ok) { _core.SendToJS("vrcModDone", new { userId = uid, type = "interactOff", active = false }); await LogModerationEventAsync(uid,"interactOff", false); }
                    });
                break;
            }

            case "vrcMuteChat":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.PlayerModeration.ModerateUserAsync(uid, "muteChat");
                        if (ok) { _core.SendToJS("vrcModDone", new { userId = uid, type = "muteChat", active = true }); await LogModerationEventAsync(uid,"muteChat", true); }
                    });
                break;
            }

            case "vrcUnmuteChat":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.PlayerModeration.UnmoderateUserAsync(uid, "muteChat");
                        if (ok) { _core.SendToJS("vrcModDone", new { userId = uid, type = "muteChat", active = false }); await LogModerationEventAsync(uid,"muteChat", false); }
                    });
                break;
            }

            case "vrcBoop":
            {
                var uid = msg["userId"]?.ToString();
                var boopEmoji = msg["emojiId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                {
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.Users.SendBoopAsync(uid, boopEmoji);
                        if (ok)
                        {
                            var entry = StoreChatMessage(uid, "me", "💕 Boop!", "boop", boopEmoji);
                            _core.SendToJS("vrcChatMessage", entry);
                        }
                        _core.SendToJS("vrcActionResult", new { action = "boop", success = ok,
                            message = ok ? "Booped!" : "Failed to boop" });
                    });
                }
                break;
            }

            case "vrcSendChatMessage":
            {
                var uid = msg["userId"]?.ToString();
                var text = msg["text"]?.ToString();
                if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(text))
                {
                    _ = Task.Run(async () =>
                    {
                        var (ok, err, slotsUsed) = await _core.Invite.SendChatMessageAsync(uid, text);
                        if (ok)
                        {
                            var entry = StoreChatMessage(uid, "me", text);
                            _core.SendToJS("vrcChatMessage", entry);
                        }
                        _core.SendToJS("vrcChatSlotInfo", new { used = slotsUsed, total = 24 });
                        _core.SendToJS("vrcActionResult", new { action = "sendChatMessage", success = ok, message = ok ? "Sent!" : err });
                    });
                }
                break;
            }

            case "vrcGetChatHistory":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                {
                    _core.SendToJS("vrcChatHistory", new { userId = uid, messages = GetChatHistory(uid) });
                    _ = Task.Run(async () =>
                    {
                        var (used, total) = await _core.Invite.LoadChatSlotStatusAsync();
                        _core.SendToJS("vrcChatSlotInfo", new { used, total });
                    });
                }
                break;
            }

            case "vrcGetUserFavWorlds":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                    _ = Task.Run(async () => await GetUserFavWorldsAsync(uid));
                break;
            }

            case "vrcGetUser":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                {
                    _ = Task.Run(async () =>
                    {
                        var u = await _core.Users.GetUserAsync(uid);
                        if (u != null) _core.SendToJS("vrcUserDetail", new
                        {
                            id = u["id"]?.ToString() ?? "", displayName = u["displayName"]?.ToString() ?? "",
                            image = ImageCacheHelper.GetUserUrl(u["id"]?.ToString(), VRChatApiService.GetUserImage(u)), status = u["status"]?.ToString() ?? "offline",
                            statusDescription = u["statusDescription"]?.ToString() ?? "",
                            bio = u["bio"]?.ToString() ?? "", location = u["location"]?.ToString() ?? "",
                            isFriend = u["isFriend"]?.Value<bool>() ?? false,
                            currentAvatarImageUrl = ImageCacheHelper.GetAvatarUrl(u["currentAvatar"]?.ToString(), u["currentAvatarImageUrl"]?.ToString()),
                        });
                    });
                }
                break;
            }

            case "vrcSetFriendAlert":
            {
                var uid   = msg["userId"]?.ToString() ?? "";
                var level = msg["level"]?.Value<int>() ?? 0;
                if (!string.IsNullOrEmpty(uid))
                    _core.TimeEngine.SetFriendAlert(uid, level);
                _core.SendToJS("vrcFriendAlertState", new { userId = uid, level });
                break;
            }

            case "vrcGetFriendAlert":
            {
                var uid = msg["userId"]?.ToString() ?? "";
                var level = string.IsNullOrEmpty(uid) ? 0 : _core.TimeEngine.GetFriendAlert(uid);
                _core.SendToJS("vrcFriendAlertState", new { userId = uid, level });
                break;
            }
        }
    }

    private async Task GetUserFavWorldsAsync(string userId)
    {
        var isSelfFav = userId == (_core.VrcApi.CurrentUserId ?? "");

        // Serve from cache if fresh (TTL 3 days) — no API call needed
        if (!isSelfFav && _core.Settings.FfcEnabled && _core.Cache.IsFresh(CacheHandler.KeyUserFavContent(userId), TimeSpan.FromDays(3)))
        {
            var cached = _core.Cache.LoadRaw(CacheHandler.KeyUserFavContent(userId));
            if (cached != null)
            {
                // Re-process thumbnailImageUrl through ImageCache.. FFC does stores raw CDN URLs
                // that bypass the image cache and load directly in browser without being cached locally.
                if (cached is Newtonsoft.Json.Linq.JObject cobj)
                {
                    foreach (var grp in cobj["groups"] ?? new Newtonsoft.Json.Linq.JArray())
                        foreach (var w in grp["worlds"] ?? new Newtonsoft.Json.Linq.JArray())
                        {
                            var wid = w["id"]?.ToString() ?? "";
                            var raw = w["thumbnailImageUrl"]?.ToString() ?? "";
                            if (!string.IsNullOrEmpty(wid))
                                ((Newtonsoft.Json.Linq.JObject)w)["thumbnailImageUrl"] = ImageCacheHelper.GetWorldUrl(wid, raw);
                        }
                }
                _core.SendToJS("vrcUserFavWorlds", cached);
                return;
            }
        }

        // Fetch fresh data from API
        var groups = await _core.Favorites.GetUserFavWorldGroupsAsync(userId);
        var result = new List<object>();
        foreach (var g in groups)
        {
            if (g is not JObject grp) continue;
            var type = grp["type"]?.ToString() ?? "";
            if (type != "world") continue;
            var name = grp["name"]?.ToString() ?? "";
            var displayName = grp["displayName"]?.ToString() ?? name;
            var visibility = grp["visibility"]?.ToString() ?? "private";
            List<object> worlds = new();
            if (visibility != "private" || isSelfFav)
            {
                IEnumerable<JToken> wArr = isSelfFav
                    ? await _core.Favorites.GetFavoriteWorldsByGroupAsync(name)
                    : await _core.Favorites.GetUserFavWorldsInGroupAsync(userId, name);
                foreach (var w in wArr)
                {
                    if (w is not JObject wo) continue;
                    var wfid = wo["id"]?.ToString() ?? "";
                    worlds.Add(new {
                        id = wfid,
                        name = wo["name"]?.ToString() ?? "",
                        thumbnailImageUrl = ImageCacheHelper.GetWorldUrl(wfid, wo["imageUrl"]?.ToString() ?? wo["thumbnailImageUrl"]?.ToString()),
                        occupants = wo["occupants"]?.Value<int>() ?? 0,
                        favorites = wo["favorites"]?.Value<int>() ?? 0,
                        authorName = wo["authorName"]?.ToString() ?? "",
                    });
                }
            }
            result.Add(new { name, displayName, visibility, worlds });
        }
        var payload = new { userId, groups = result };
        if (_core.Settings.FfcEnabled) _core.Cache.Save(CacheHandler.KeyUserFavContent(userId), payload);
        _core.SendToJS("vrcUserFavWorlds", payload);
    }

    // Set of actions this controller handles
    private static readonly HashSet<string> _handledActions = new()
    {
        "vrcRefreshFriends", "vrcUpdateStatus", "vrcGetFriendDetail", "vrcGetFriendPreview", "vrcJoinFriend",
        "vrcInviteFriend", "vrcInviteFriendWithPhoto", "vrcGetInviteMessages",
        "vrcUpdateInviteMessage", "vrcRequestInvite", "vrcUpdateNote", "vrcBatchInvite",
        "vrcGetFavoriteFriends", "vrcAddFavoriteFriend", "vrcRemoveFavoriteFriend",
        "vrcAddFavoriteFriendToGroup",
        "vrcSendFriendRequest", "vrcUnfriend", "vrcGetBlocked", "vrcGetMuted", "vrcGetAllModerations",
        "vrcBlock", "vrcMute", "vrcUnblock", "vrcUnmute",
        "vrcHideAvatar", "vrcShowAvatar", "vrcInteractOff", "vrcInteractOn", "vrcMuteChat", "vrcUnmuteChat",
        "vrcBoop",
        "vrcSendChatMessage", "vrcGetChatHistory", "vrcGetUser",
        "vrcGetUserAvatars", "vrcGetUserFavWorlds",
        "vrcSetFriendAlert", "vrcGetFriendAlert",
    };

    public static bool HandlesAction(string action) => _handledActions.Contains(action);

    // Core Friend Methods

    public async Task FetchAndCacheFavFriendsAsync()
    {
        if (Interlocked.CompareExchange(ref _favFriendsInFlight, 1, 0) != 0) return;
        try
        {
            var allGroups = await _core.Favorites.GetFavoriteGroupsAsync();
            var groupList = allGroups
                .Where(g => g["type"]?.ToString() == "friend")
                .Select(g => new AuthController.WFavGroup
                {
                    name        = g["name"]?.ToString() ?? "",
                    displayName = g["displayName"]?.ToString() ?? "",
                    type        = "friend",
                    visibility  = g["visibility"]?.ToString() ?? "private",
                    capacity    = g["capacity"]?.Value<int>() ?? 150,
                })
                .Where(g => !string.IsNullOrEmpty(g.name))
                .ToList();
            groupList = AuthController.FillMissingFriendSlots(groupList);

            var favs = await _core.Favorites.GetFavoriteFriendsAsync();
            lock (_favoriteFriends)
            {
                _favoriteFriends.Clear();
                foreach (var fav in favs)
                {
                    var uid    = fav["favoriteId"]?.ToString() ?? "";
                    var fvrtId = fav["id"]?.ToString() ?? "";
                    var tag    = (fav["tags"] as JArray)?.FirstOrDefault()?.ToString() ?? "group_0";
                    if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(fvrtId))
                        _favoriteFriends[uid] = (fvrtId, tag);
                }
            }

            var friends = favs
                .Select(f => new
                {
                    fvrtId     = f["id"]?.ToString() ?? "",
                    favoriteId = f["favoriteId"]?.ToString() ?? "",
                    groupName  = (f["tags"] as JArray)?.FirstOrDefault()?.ToString() ?? "group_0",
                })
                .Where(f => !string.IsNullOrEmpty(f.favoriteId))
                .ToList();

            groupList.AddRange(AuthController.BuildLocalGroups(_core.LocalFavorites.GetGroups("friend"), "localFriend"));
            var friendsList = friends.Cast<object>().ToList();
            foreach (var it in _core.LocalFavorites.GetItems("friend"))
                friendsList.Add(new { fvrtId = it.Id, favoriteId = it.EntityId, groupName = it.GroupName });

            var payload = new { friends = friendsList, groups = groupList };
            if (_core.Settings.FfcEnabled) _core.Cache.Save(CacheHandler.KeyFavFriends, payload);
            _core.SendToJS("vrcFavoriteFriends", payload);
        }
        catch { }
        finally { Interlocked.Exchange(ref _favFriendsInFlight, 0); }
    }

    public async Task RefreshFriendsAsync(bool silent = false)
    {
        if (!_core.VrcApi.IsLoggedIn) return;
        if (!await _friendsRefreshLock.WaitAsync(0)) return;
        try
        {
            var online  = await _core.Friends.GetOnlineFriendsAsync();
            var offline = await _core.Friends.GetOfflineFriendsAsync();

            lock (_friendStore)
            {
                var onlineIds = new HashSet<string>(
                    online.Select(f => f["id"]?.ToString() ?? "").Where(id => !string.IsNullOrEmpty(id)));
                foreach (var f in online)
                {
                    var uid = f["id"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(uid)) _friendStore[uid] = f;
                }
                foreach (var f in offline)
                {
                    var uid = f["id"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(uid) || onlineIds.Contains(uid)) continue;
                    var copy = (JObject)f.DeepClone();
                    copy["location"] = "offline";
                    copy["status"] = "offline";
                    _friendStore[uid] = copy;
                }
            }

            await WarmDecorationsAsync(online.Concat(offline));

            var seenIds = new HashSet<string>();
            var onlineList = online.Select(f =>
            {
                var id = f["id"]?.ToString() ?? "";
                seenIds.Add(id);
                var location = f["location"]?.ToString() ?? "";
                var platform = f["platform"]?.ToString() ?? f["last_platform"]?.ToString() ?? "";
                bool isWebPlatform = platform.Equals("web", StringComparison.OrdinalIgnoreCase);
                bool isInGame = !string.IsNullOrEmpty(location) && location != "offline" && location != "" && !isWebPlatform;
                var facts = CachedFacts(id);
                return new
                {
                    id, displayName = f["displayName"]?.ToString() ?? "",
                    image = ImageCacheHelper.GetUserUrl(id, VRChatApiService.GetUserImage(f)),
                    iconFrame = f["iconFrame"]?.ToString() ?? "",
                    iconFrameUrl = IconFrameHelper.UrlFor(f["iconFrame"]?.ToString(), _core.Inventory),
                    nameplateEffect = f["nameplateEffect"]?.ToString() ?? "",
                    nameplateUrl = IconFrameHelper.UrlFor(f["nameplateEffect"]?.ToString(), _core.Inventory),
                    profileEffect = f["profileEffect"]?.ToString() ?? "",
                    profileEffectUrl = IconFrameHelper.UrlFor(f["profileEffect"]?.ToString(), _core.Inventory),
                    status = f["status"]?.ToString() ?? "offline",
                    statusDescription = f["statusDescription"]?.ToString() ?? "",
                    location, platform,
                    presence = isInGame ? "game" : "web",
                    tags = f["tags"]?.ToObject<List<string>>() ?? new(),
                    bioLinks = f["bioLinks"]?.ToObject<List<string>>() ?? new(),
                    lastLogin = f["last_login"]?.ToString() ?? "",
                    lastActivity = f["last_activity"]?.ToString() ?? "",
                    dateJoined = facts.dateJoined,
                    pronouns = PickPronouns(f, facts.pronouns),
                    mutualFriends = facts.mutualFriends,
                    mutualGroups = facts.mutualGroups,
                    lastSeen = facts.lastSeen,
                };
            }).ToList();

            var offlineList = offline
                .Where(f => !seenIds.Contains(f["id"]?.ToString() ?? ""))
                .Select(f =>
                {
                var offFacts = CachedFacts(f["id"]?.ToString() ?? "");
                return new
                {
                    id = f["id"]?.ToString() ?? "",
                    displayName = f["displayName"]?.ToString() ?? "",
                    image = ImageCacheHelper.GetUserUrl(f["id"]?.ToString(), VRChatApiService.GetUserImage(f)),
                    iconFrame = f["iconFrame"]?.ToString() ?? "",
                    iconFrameUrl = IconFrameHelper.UrlFor(f["iconFrame"]?.ToString(), _core.Inventory),
                    nameplateEffect = f["nameplateEffect"]?.ToString() ?? "",
                    nameplateUrl = IconFrameHelper.UrlFor(f["nameplateEffect"]?.ToString(), _core.Inventory),
                    profileEffect = f["profileEffect"]?.ToString() ?? "",
                    profileEffectUrl = IconFrameHelper.UrlFor(f["profileEffect"]?.ToString(), _core.Inventory),
                    status = "offline",
                    statusDescription = f["statusDescription"]?.ToString() ?? "",
                    location = "offline",
                    platform = f["last_platform"]?.ToString() ?? "",
                    presence = "offline",
                    tags = f["tags"]?.ToObject<List<string>>() ?? new(),
                    bioLinks = f["bioLinks"]?.ToObject<List<string>>() ?? new(),
                    lastLogin = f["last_login"]?.ToString() ?? "",
                    lastActivity = f["last_activity"]?.ToString() ?? "",
                    dateJoined = offFacts.dateJoined,
                    pronouns = PickPronouns(f, offFacts.pronouns),
                    mutualFriends = offFacts.mutualFriends,
                    mutualGroups = offFacts.mutualGroups,
                    lastSeen = offFacts.lastSeen,
                };
                }).ToList();

            var friendList = onlineList
                .OrderBy(f => f.presence == "game" ? 0 : 1)
                .ThenBy(f => f.status switch { "join me" => 0, "active" => 1, "ask me" => 2, "busy" => 3, _ => 4 })
                .Cast<object>()
                .Concat(offlineList.OrderBy(f => f.displayName).Cast<object>())
                .ToList();

            var counts = new
            {
                game = onlineList.Count(f => f.presence == "game"),
                web = onlineList.Count(f => f.presence == "web"),
                offline = offlineList.Count
            };

            if (!_core.Timeline.KnownUsersSeeded)
            {
                var allIds = online.Select(f => f["id"]?.ToString())
                    .Concat(offline.Select(f => f["id"]?.ToString()))
                    .Where(id => !string.IsNullOrEmpty(id)).Cast<string>().ToList();
                _core.Timeline.SeedKnownUsers(allIds);
            }

            if (!_friendStateSeeded)
            {
                foreach (var f in online)
                {
                    var uid = f["id"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(uid)) continue;
                    _friendLastLoc[uid] = f["location"]?.ToString() ?? "";
                    _friendLastStatus[uid] = f["status"]?.ToString() ?? "";
                    _core.Timeline.UpdateUserLastStatus(uid, f["status"]?.ToString() ?? "");
                    _friendLastStatusDesc[uid] = (f["statusDescription"]?.ToString() ?? "").Trim();
                    _friendLastBio[uid] = (f["bio"]?.ToString() ?? "").Trim();
                    var img0 = VRChatApiService.GetUserImage(f);
                    _friendNameImg[uid] = (f["displayName"]?.ToString() ?? "", img0);
                    var fid0 = ExtractAvatarFileId(f);
                    if (!string.IsNullOrEmpty(fid0)) _friendLastAvatarFileId[uid] = fid0;
                }
                foreach (var f in offline)
                {
                    var uid = f["id"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(uid)) continue;
                    _friendLastLoc[uid] = "offline";
                    _friendLastStatus[uid] = f["status"]?.ToString() ?? "";
                    _friendLastStatusDesc[uid] = (f["statusDescription"]?.ToString() ?? "").Trim();
                    _friendLastBio[uid] = (f["bio"]?.ToString() ?? "").Trim();
                    var img0 = VRChatApiService.GetUserImage(f);
                    _friendNameImg[uid] = (f["displayName"]?.ToString() ?? "", img0);
                    var fid0 = ExtractAvatarFileId(f);
                    if (!string.IsNullOrEmpty(fid0)) _friendLastAvatarFileId[uid] = fid0;
                }
                _friendStateSeeded = true;

                // Startup recovery: resume or close open tracked GPS events
                var openGpsEvents = _core.Timeline.GetOpenTrackedGpsEvents();
                var now = DateTime.UtcNow.ToString("o");
                foreach (var ev in openGpsEvents)
                {
                    var curLoc    = _friendLastLoc.GetValueOrDefault(ev.FriendId, "");
                    var storedBase = ev.Location.Contains('~') ? ev.Location[..ev.Location.IndexOf('~')] : ev.Location;
                    var curBase    = curLoc.Contains('~')      ? curLoc[..curLoc.IndexOf('~')]           : curLoc;
                    if (!string.IsNullOrEmpty(curBase) && curBase == storedBase && curBase != "offline" && curBase != "traveling")
                        _friendCurrentGpsEventId[ev.FriendId] = ev.Id;  // still in same instance — resume
                    else
                        _core.Timeline.SetFriendEventLeftAt(ev.Id, now); // left — close
                }
            }
            else
            {
                foreach (var f in online.Concat(offline))
                {
                    var uid = f["id"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(uid)) continue;
                    var img = VRChatApiService.GetUserImage(f);
                    if (img.Length > 0)
                    {
                        _friendNameImg[uid] = (f["displayName"]?.ToString() ?? _friendNameImg.GetValueOrDefault(uid).name ?? "", img);
                    }
                }
            }

            if (_core.Settings.FfcEnabled) _core.Cache.Save(CacheHandler.KeyFriends, new { friends = friendList, counts });
            _core.SendToJS("vrcFriends", new { friends = friendList, counts });
            if (!silent)
                _core.SendToJS("log", new { msg = $"VRChat: {counts.game} in-game, {counts.web} web, {counts.offline} offline", color = "ok" });

            // Proactively resolve world info for in-game friends
            var inGameWorldIds = online
                .Select(f => f["location"]?.ToString() ?? "")
                .Where(l => l.Contains(':'))
                .Select(l => l.Split(':')[0])
                .Where(id => id.StartsWith("wrld_"))
                .Distinct().ToList();
            if (inGameWorldIds.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var tasks = inGameWorldIds.Select(async wid =>
                        {
                            try
                            {
                                var world = await _core.World.GetWorldAsync(wid);
                                if (world == null) return (wid, null as object);
                                var url = ImageCacheHelper.GetWorldUrl(wid, world["imageUrl"]?.ToString() ?? world["thumbnailImageUrl"]?.ToString());
                                return (wid, (object)new
                                {
                                    name = world["name"]?.ToString() ?? "",
                                    thumbnailImageUrl = url,
                                    imageUrl = url
                                });
                            }
                            catch { return (wid, null as object); }
                        });
                        var results = await Task.WhenAll(tasks);
                        var dict = results.Where(r => r.Item2 != null).ToDictionary(r => r.wid, r => r.Item2!);
                        if (dict.Count > 0)
                        {
                            _core.SendToJS("vrcWorldsResolved", dict);
#if WINDOWS
                            foreach (var (wid, wobj) in dict)
                            {
                                var jo = JObject.FromObject(wobj);
                                var wname = jo["name"]?.ToString() ?? "";
                                var wthumb = jo["thumbnailImageUrl"]?.ToString() ?? "";
                                lock (_core.VrWorldCache) _core.VrWorldCache[wid] = (wname, wthumb);
                            }
                            PushVroLocations();
#endif
                        }
                    }
                    catch { }
                });
            }

            // Friend tracking: update LastSeen/LastSeenLocation (no time accumulation — handled by UnifiedTimeEngine sessions)
            try
            {
                var trackData = onlineList.Select(f => (userId: f.id, location: f.location, presence: f.presence));
                _core.TimeEngine.UpdateFriendTracking(trackData);
            }
            catch { }
        }
        catch (Exception ex)
        {
            if (!silent)
                _core.SendToJS("log", new { msg = $"VRChat: Friends error — {ex.Message}", color = "err" });
        }
        finally
        {
            _friendsRefreshLock.Release();
        }
    }

    public async Task UpdateStatusAsync(string status, string statusDescription)
    {
        if (!_core.VrcApi.IsLoggedIn) return;
        var user = await _core.Users.UpdateStatusAsync(status, statusDescription);
        if (user != null)
        {
            _core.SendToJS("log", new { msg = $"VRChat: Status updated to {status}", color = "ok" });
        }
        else
        {
            _core.SendToJS("log", new { msg = "VRChat: Failed to update status", color = "err" });
        }
    }

    public async Task EnrichModerationsWithImagesAsync(JArray entries)
    {
        var tasks = entries.OfType<JObject>().Select(async entry =>
        {
            var uid = entry["targetUserId"]?.ToString();
            if (string.IsNullOrEmpty(uid)) return;
            var user = await _core.Users.GetUserAsync(uid);
            if (user != null) entry["image"] = ImageCacheHelper.GetUserUrl(uid, VRChatApiService.GetUserImage(user));
        });
        await Task.WhenAll(tasks);
    }

    // Live Friend Store

    public void MergeFriendStore(string userId, JObject? userObj,
        string? location = null, string? platform = null, bool wentOffline = false)
    {
        if (string.IsNullOrEmpty(userId)) return;
        lock (_friendStore)
        {
            if (!_friendStore.TryGetValue(userId, out var entry))
            { entry = new JObject(); _friendStore[userId] = entry; }
            if (userObj != null)
            {
                foreach (var prop in userObj.Properties()) entry[prop.Name] = prop.Value;
                var img = VRChatApiService.GetUserImage(userObj);
                if (!string.IsNullOrEmpty(img))
                    _friendNameImg[userId] = (userObj["displayName"]?.ToString() ?? _friendNameImg.GetValueOrDefault(userId).name ?? "", img);
            }
            if (location != null) entry["location"] = location;
            if (platform != null) entry["last_platform"] = platform;
            if (wentOffline)
            {
                entry["location"] = "offline";
                entry["status"] = "offline";
            }
        }
    }

    public void PushFriendsFromStore()
    {
        CancellationTokenSource? oldCts;
        CancellationTokenSource cts;
        int delay;
        lock (_pushLock)
        {
            oldCts = _pushDebounce;
            oldCts?.Cancel();
            var now = DateTime.UtcNow;
            if (_pushEarliestFlush == DateTime.MinValue)
                _pushEarliestFlush = now.AddMilliseconds(PushMaxDelayMs);
            delay = (int)Math.Max(0, Math.Min(PushDebounceMs,
                (_pushEarliestFlush - now).TotalMilliseconds));
            _pushDebounce = cts = new CancellationTokenSource();
        }
        oldCts?.Dispose();
        var token = cts.Token;
        _ = Task.Delay(delay, token).ContinueWith(_ =>
        {
            if (token.IsCancellationRequested) return;
            lock (_pushLock) { _pushEarliestFlush = DateTime.MinValue; }
            DoPushFriendsFromStore();
        }, TaskContinuationOptions.NotOnCanceled);
    }

    private void PushFriendUpdate(string userId)
    {
        JObject? f;
        lock (_friendStore) _friendStore.TryGetValue(userId, out f);
        if (f == null) return;
        var location = f["location"]?.ToString() ?? "";
        var platform = f["last_platform"]?.ToString() ?? f["platform"]?.ToString() ?? "";
        bool isWebPlatform = platform.Equals("web", StringComparison.OrdinalIgnoreCase);
        bool isInGame = !string.IsNullOrEmpty(location) && location != "offline" && location != "" && !isWebPlatform;
        var status = f["status"]?.ToString() ?? "offline";
        var presence = (location == "offline" && status == "offline") ? "offline" : isInGame ? "game" : "web";
        var (locWorldId, _, locInstType) = VRChatApiService.ParseLocation(location);
        (string name, string thumb) locWorld = ("", "");
        if (locWorldId.StartsWith("wrld_"))
            lock (_core.VrWorldCache) _core.VrWorldCache.TryGetValue(locWorldId, out locWorld);
        var upFacts = CachedFacts(f["id"]?.ToString() ?? "");
        _core.SendToJS("vrcFriendUpdate", new
        {
            id = f["id"]?.ToString() ?? "",
            displayName = f["displayName"]?.ToString() ?? "",
            image = ImageCacheHelper.GetUserUrl(f["id"]?.ToString(), VRChatApiService.GetUserImage(f)),
            iconFrame = f["iconFrame"]?.ToString() ?? "",
            iconFrameUrl = IconFrameHelper.UrlFor(f["iconFrame"]?.ToString(), _core.Inventory),
            nameplateEffect = f["nameplateEffect"]?.ToString() ?? "",
            nameplateUrl = IconFrameHelper.UrlFor(f["nameplateEffect"]?.ToString(), _core.Inventory),
            profileEffect = f["profileEffect"]?.ToString() ?? "",
            profileEffectUrl = IconFrameHelper.UrlFor(f["profileEffect"]?.ToString(), _core.Inventory),
            status, statusDescription = f["statusDescription"]?.ToString() ?? "",
            location, platform, presence,
            worldName = locWorld.name,
            worldThumb = locWorld.thumb,
            instanceType = locInstType,
            tags = f["tags"]?.ToObject<List<string>>() ?? new List<string>(),
            ageVerified = f["ageVerified"]?.Value<bool>() ?? false,
            avatarFileId = ExtractAvatarFileId(f),
            bio = f["bio"]?.ToString() ?? "",
            pronouns = PickPronouns(f, upFacts.pronouns),
            bioLinks = f["bioLinks"]?.ToObject<List<string>>() ?? new List<string>(),
            profilePicOverride = f["profilePicOverride"]?.ToString() ?? "",
            lastLogin = f["last_login"]?.ToString() ?? "",
            lastActivity = f["last_activity"]?.ToString() ?? "",
            dateJoined = upFacts.dateJoined,
            mutualFriends = upFacts.mutualFriends,
            mutualGroups = upFacts.mutualGroups,
            lastSeen = upFacts.lastSeen,
            bannerUrl = f["bannerUrl"]?.ToString() ?? "",
            currentAvatarImageUrl = f["currentAvatarImageUrl"]?.ToString() ?? f["currentAvatarThumbnailImageUrl"]?.ToString() ?? "",
            badges = f["badges"] ?? new JArray(),
        });
    }

    private void DoPushFriendsFromStore()
    {
        List<JObject> snapshot;
        lock (_friendStore) snapshot = _friendStore.Values.ToList();

        _core.SendToJS("log", new { msg = $"[WS] DoPushFriendsFromStore: {snapshot.Count} friends @ {DateTime.UtcNow:HH:mm:ss.fff}", color = "info" });

        var list = snapshot.Select(f =>
        {
            var location = f["location"]?.ToString() ?? "";
            var platform = f["last_platform"]?.ToString() ?? f["platform"]?.ToString() ?? "";
            bool isWebPlatform = platform.Equals("web", StringComparison.OrdinalIgnoreCase);
            bool isInGame = !string.IsNullOrEmpty(location) && location != "offline" && location != "" && !isWebPlatform;
            var status = f["status"]?.ToString() ?? "offline";
            var presence = (location == "offline" && status == "offline") ? "offline" : isInGame ? "game" : "web";
            var facts = CachedFacts(f["id"]?.ToString() ?? "");
            return new
            {
                id = f["id"]?.ToString() ?? "",
                displayName = f["displayName"]?.ToString() ?? "",
                image = ImageCacheHelper.GetUserUrl(f["id"]?.ToString(), VRChatApiService.GetUserImage(f)),
                iconFrame = f["iconFrame"]?.ToString() ?? "",
                iconFrameUrl = IconFrameHelper.UrlFor(f["iconFrame"]?.ToString(), _core.Inventory),
                nameplateEffect = f["nameplateEffect"]?.ToString() ?? "",
                nameplateUrl = IconFrameHelper.UrlFor(f["nameplateEffect"]?.ToString(), _core.Inventory),
                profileEffect = f["profileEffect"]?.ToString() ?? "",
                profileEffectUrl = IconFrameHelper.UrlFor(f["profileEffect"]?.ToString(), _core.Inventory),
                status, statusDescription = f["statusDescription"]?.ToString() ?? "",
                location, platform, presence,
                tags = f["tags"]?.ToObject<List<string>>() ?? new List<string>(),
                ageVerified = f["ageVerified"]?.Value<bool>() ?? false,
                avatarFileId = ExtractAvatarFileId(f),
                bioLinks = f["bioLinks"]?.ToObject<List<string>>() ?? new List<string>(),
                lastLogin = f["last_login"]?.ToString() ?? "",
                lastActivity = f["last_activity"]?.ToString() ?? "",
                dateJoined = facts.dateJoined,
                pronouns = PickPronouns(f, facts.pronouns),
                mutualFriends = facts.mutualFriends,
                mutualGroups = facts.mutualGroups,
                lastSeen = facts.lastSeen,
            };
        })
        .OrderBy(f => f.presence switch { "game" => 0, "web" => 1, _ => 2 })
        .ThenBy(f => f.status switch { "join me" => 0, "active" => 1, "ask me" => 2, "busy" => 3, _ => 4 })
        .ThenBy(f => f.displayName)
        .ToList();

        var counts = new
        {
            game = list.Count(f => f.presence == "game"),
            web = list.Count(f => f.presence == "web"),
            offline = list.Count(f => f.presence == "offline"),
        };

        _core.SendToJS("vrcFriends", new { friends = list, counts });

        if (_warmRepushPending) _warmRepushPending = false;
        else WarmIconFrames(snapshot);

#if WINDOWS
        if (_core.VrOverlay != null) { PushVroLocations(); PushVroOnlineFriends(); }
#endif
    }

    private async Task WarmDecorationsAsync(IEnumerable<JObject> friends)
    {
        var ids = friends
            .SelectMany(f => new[] { f["iconFrame"]?.ToString(), f["nameplateEffect"]?.ToString(), f["profileEffect"]?.ToString() })
            .Where(id => !string.IsNullOrEmpty(id) && !ImageCacheHelper.IsVrcPlusFresh(id, InventoryAPI.DecorationTtl))
            .Distinct()
            .ToList();
        foreach (var id in ids) await _core.Inventory.ResolveDecorationAsync(id!);
    }

    private volatile bool _warmRepushPending;
    private void WarmIconFrames(List<JObject> friends)
    {
        var needsWork = friends
            .SelectMany(f => new[] { f["iconFrame"]?.ToString(), f["nameplateEffect"]?.ToString() })
            .Where(id => !string.IsNullOrEmpty(id) && !ImageCacheHelper.IsVrcPlusFresh(id, InventoryAPI.DecorationTtl))
            .Distinct()
            .ToList();
        if (needsWork.Count == 0) return;
        _ = Task.Run(async () =>
        {
            foreach (var id in needsWork) await _core.Inventory.ResolveDecorationAsync(id!);
            _warmRepushPending = true;
            DoPushFriendsFromStore();
        });
    }

#if WINDOWS
    public void PushVroLocations()
    {
        var overlay = _core.VrOverlay;
        if (overlay == null) return;
        List<JObject> snapshot;
        lock (_friendStore) snapshot = _friendStore.Values.ToList();

        var entries = snapshot
            .Where(f =>
            {
                var loc = f["location"]?.ToString() ?? "";
                return loc.Contains(':') && loc.Split(':')[0].StartsWith("wrld_");
            })
            .Select(f =>
            {
                var loc = f["location"]?.ToString() ?? "";
                var wid = loc.Split(':')[0];
                var iid = loc.Contains(':') ? loc.Split(':', 2)[1].Split('~')[0] : "";
                (string name, string thumb) world = ("", "");
                lock (_core.VrWorldCache) _core.VrWorldCache.TryGetValue(wid, out world);
                var rawFriendImg = VRChatApiService.GetUserImage(f);
                return (
                    worldId: wid, instanceId: iid,
                    worldName: world.name,
                    worldImageUrl: world.thumb,
                    friendId: f["id"]?.ToString() ?? "",
                    friendName: f["displayName"]?.ToString() ?? "",
                    friendImageUrl: ImageCacheHelper.GetUserUrl(f["id"]?.ToString(), rawFriendImg),
                    location: loc
                );
            })
            .ToList();

        overlay.SetFriendLocations(entries);
    }

    public void PushVroOnlineFriends()
    {
        var overlay = _core.VrOverlay;
        if (overlay == null) return;
        List<JObject> snapshot;
        lock (_friendStore) snapshot = _friendStore.Values.ToList();

        var entries = snapshot
            .Where(f =>
            {
                var loc = f["location"]?.ToString() ?? "";
                var status = f["status"]?.ToString() ?? "offline";
                var platform = f["last_platform"]?.ToString() ?? f["platform"]?.ToString() ?? "";
                bool isWeb = platform.Equals("web", StringComparison.OrdinalIgnoreCase);
                bool isInGame = !string.IsNullOrEmpty(loc) && loc != "offline" && !isWeb;
                return status != "offline" && isInGame;
            })
            .Select(f =>
            {
                var loc = f["location"]?.ToString() ?? "";
                var wid = loc.Contains(':') ? loc.Split(':')[0] : "";
                (string name, string thumb) world = ("", "");
                if (wid.StartsWith("wrld_"))
                    lock (_core.VrWorldCache) _core.VrWorldCache.TryGetValue(wid, out world);
                var rawImg = VRChatApiService.GetUserImage(f);
                return (
                    friendId: f["id"]?.ToString() ?? "",
                    friendName: f["displayName"]?.ToString() ?? "",
                    friendImageUrl: ImageCacheHelper.GetUserUrl(f["id"]?.ToString(), rawImg),
                    status: f["status"]?.ToString() ?? "",
                    statusDescription: f["statusDescription"]?.ToString() ?? "",
                    location: loc,
                    worldName: world.name
                );
            })
            .OrderBy(f => f.status switch { "join me" => 0, "active" => 1, "ask me" => 2, "busy" => 3, _ => 4 })
            .ThenBy(f => f.friendName)
            .ToList();

        overlay.SetOnlineFriends(entries);

        var selfRaw = _core.VrcApi.CurrentUserRaw;
        if (selfRaw != null)
        {
            var selfId     = selfRaw["id"]?.ToString() ?? "";
            var selfImg    = ImageCacheHelper.GetUserUrl(selfId, VRChatApiService.GetUserImage(selfRaw));
            var selfStatus = selfRaw["status"]?.ToString() ?? "offline";
            overlay.SetSelfUser(selfId, selfImg, selfStatus);
        }
    }
#endif

    // Friend Detail

    public async Task GetFriendDetailAsync(string userId)
    {
        if (!_core.VrcApi.IsLoggedIn) return;

        bool isFriend;
        lock (_friendStore) isFriend = _friendStore.ContainsKey(userId);

        var cachedEntry = _core.TimeEngine.GetUserProfileCache(userId);
        if (cachedEntry != null)
        {
            var (cGroups, _)               = BuildGroupsDisplay(TryParseJArray(cachedEntry.GroupsJson) ?? new JArray());
            var cRepGroup                  = TryParseJObject(cachedEntry.ProfileRepresentedGroup);
            var cWorlds                    = BuildWorldsDisplay(TryParseJObject(cachedEntry.ContentJson)?["worlds"] as JArray ?? new JArray());
            var mutualsRaw                 = TryParseJObject(cachedEntry.MutualsJson) ?? new JObject();
            var cMutuals                   = BuildMutualsDisplay(mutualsRaw["mutuals"] as JArray ?? new JArray());
            var cMutualsOptedOut           = mutualsRaw["optedOut"]?.Value<bool>() ?? false;
            var cMutualGroups              = BuildMutualGroupsDisplay(TryParseJArray(cachedEntry.MutualGroupsJson) ?? new JArray());

            JObject? live;
            lock (_friendStore) _friendStore.TryGetValue(userId, out live);
            var liveStatus          = live?["status"]?.ToString()                                            ?? cachedEntry.ProfileStatus;
            var liveStatusDesc      = live?["statusDescription"]?.ToString()                                 ?? cachedEntry.ProfileStatusDesc;
            var liveLoc             = live?["location"]?.ToString()                                          ?? cachedEntry.ProfileLocation;
            var liveDisplayName     = live?["displayName"]?.ToString();
            var liveRawImage        = live != null ? VRChatApiService.GetUserImage(live) : "";
            var liveBio             = live?["bio"]?.ToString();
            var livePronouns        = live?["pronouns"]?.ToString();
            var liveAvatarImg       = live?["currentAvatarImageUrl"]?.ToString() ?? live?["currentAvatarThumbnailImageUrl"]?.ToString();
            var livePicOverride     = live?["profilePicOverride"]?.ToString();
            var liveBannerUrl       = live?["bannerUrl"]?.ToString();
            var liveTags            = live?["tags"] as JArray;
            var liveBioLinks        = live?["bioLinks"] as JArray;
            var liveBadges          = live?["badges"] as JArray;
            var liveAgeVerified     = live?["ageVerified"]?.Value<bool>();
            var liveAgeVerifStatus  = live?["ageVerificationStatus"]?.ToString();
            var livePlatform        = live?["platform"]?.ToString();
            var liveLastPlatform    = live?["last_platform"]?.ToString() ?? live?["lastMobile"]?.ToString();
            var (_, _, liveInstType) = VRChatApiService.ParseLocation(liveLoc);
            var liveWid = liveLoc.Contains(':') ? liveLoc.Split(':')[0] : "";
            (string name, string thumb) liveWorld = ("", "");
            if (liveWid.StartsWith("wrld_"))
                lock (_core.VrWorldCache) _core.VrWorldCache.TryGetValue(liveWid, out liveWorld);
            bool liveIsInWorld = !string.IsNullOrEmpty(liveLoc) && liveLoc != "offline" && liveLoc != "private" && liveLoc != "traveling";
            bool liveInGame    = !string.IsNullOrEmpty(liveLoc) && liveLoc != "offline";
            var liveAvatarId   = live?["currentAvatar"]?.ToString() ?? cachedEntry.ProfileCurrentAvatarId;
            var liveFileId     = live != null ? ExtractAvatarFileId(live) : "";
            if (string.IsNullOrEmpty(liveFileId)) liveFileId = cachedEntry.ProfileAvatarFileId;
            var isCoPresent    = (_core.IsVrcRunning?.Invoke() ?? false) && _core.LogWatcher.GetCurrentPlayers().Any(p => p.UserId == userId);
            var (totalSecs, _) = _core.TimeEngine.GetUserStats(userId, isCoPresent);

            var diskProfile = new JObject
            {
                ["id"]                    = userId,
                // Memos live in the timeline DB, not the profile cache, so they have to
                // be attached here too - otherwise the cached path drops them and the UI
                // falls back to the display name.
                ["memo"]                  = _core.Timeline?.GetUserMemo(userId) ?? "",
                ["displayName"]           = !string.IsNullOrEmpty(liveDisplayName) ? liveDisplayName : cachedEntry.DisplayName,
                ["image"]                 = !string.IsNullOrEmpty(liveRawImage) ? ImageCacheHelper.GetUserUrl(userId, liveRawImage) : cachedEntry.Image,
                ["status"]                = liveStatus,
                ["statusDescription"]     = liveStatusDesc,
                ["bio"]                   = liveBio ?? cachedEntry.ProfileBio,
                ["lastLogin"]             = cachedEntry.ProfileLastLogin,
                ["lastActivity"]          = cachedEntry.ProfileLastActivity,
                ["dateJoined"]            = cachedEntry.ProfileDateJoined,
                ["location"]              = liveLoc,
                ["worldName"]             = liveWorld.name,
                ["worldThumb"]            = liveWorld.thumb,
                ["instanceType"]          = liveIsInWorld ? liveInstType : cachedEntry.ProfileInstanceType,
                // Reuse the last-known count/capacity only while the friend is still in the
                // same instance, so the XX/XX badge survives the modal cache like instanceType.
                ["userCount"]             = (liveIsInWorld && liveLoc == cachedEntry.ProfileLocation) ? cachedEntry.ProfileUserCount : 0,
                ["worldCapacity"]         = (liveIsInWorld && liveLoc == cachedEntry.ProfileLocation) ? cachedEntry.ProfileWorldCapacity : 0,
                ["ageGate"]               = liveIsInWorld && liveLoc.Contains("~ageGate"),
                ["isFriend"]              = cachedEntry.ProfileIsFriend != 0,
                ["canJoin"]               = liveIsInWorld && liveInstType is "public" or "friends" or "friends+" or "hidden" or "group-public" or "group-plus" or "group-members" or "group",
                ["canRequestInvite"]      = liveInstType is "private" or "invite_plus",
                ["canInvite"]             = true,
                ["currentAvatarImageUrl"] = !string.IsNullOrEmpty(liveAvatarImg) ? ImageCacheHelper.GetAvatarUrl(liveAvatarId, liveAvatarImg) : cachedEntry.ProfileAvatarImg,
                ["currentAvatarId"]       = liveAvatarId,
                ["avatarFileId"]          = liveFileId,
                ["profilePicOverride"]    = !string.IsNullOrEmpty(livePicOverride) ? ImageCacheHelper.GetUserPicOverrideUrl(userId, livePicOverride) : cachedEntry.ProfilePicOverride,
                ["bannerUrl"]             = !string.IsNullOrEmpty(liveBannerUrl) ? ImageCacheHelper.GetUserBannerUrl(userId, liveBannerUrl) : cachedEntry.ProfileBannerUrl,
                ["tags"]                  = liveTags ?? TryParseJArray(cachedEntry.ProfileTags) ?? new JArray(),
                ["note"]                  = cachedEntry.ProfileNote,
                ["friendKey"]             = cachedEntry.ProfileFriendKey,
                ["travelingToLocation"]   = live?["travelingToLocation"]?.ToString() ?? "",
                ["state"]                 = (liveStatus != "offline" && !liveInGame) ? "active" : "",
                ["lastPlatform"]          = !string.IsNullOrEmpty(liveLastPlatform) ? liveLastPlatform : cachedEntry.ProfileLastPlatform,
                ["platform"]              = !string.IsNullOrEmpty(livePlatform) ? livePlatform : cachedEntry.ProfilePlatform,
                ["userNote"]              = cachedEntry.ProfileUserNote,
                ["totalTimeSeconds"]      = totalSecs,
                ["meets"]                 = _core.Timeline?.GetMeetAgainCount(userId) ?? 0,
                ["firstMeetDate"]         = _core.Timeline?.GetFirstMeetDate(userId) ?? "",
                ["inSameInstance"]        = isCoPresent,
                ["lastSeenTracked"]       = _core.Timeline?.GetLastSeenTimestamp(userId) ?? "",
                ["pronouns"]              = !string.IsNullOrEmpty(livePronouns) ? livePronouns : cachedEntry.ProfilePronouns,
                ["ageVerificationStatus"] = !string.IsNullOrEmpty(liveAgeVerifStatus) ? liveAgeVerifStatus : cachedEntry.ProfileAgeVerification,
                ["ageVerified"]           = liveAgeVerified ?? cachedEntry.ProfileAgeVerified != 0,
                ["representedGroup"]      = (JToken?)cRepGroup ?? JValue.CreateNull(),
                ["userGroups"]            = JArray.FromObject(cGroups),
                ["mutuals"]               = JArray.FromObject(cMutuals),
                ["mutualGroups"]          = JArray.FromObject(cMutualGroups),
                ["mutualsOptedOut"]       = cMutualsOptedOut,
                ["userWorlds"]            = JArray.FromObject(cWorlds),
                ["bioLinks"]              = liveBioLinks ?? TryParseJArray(cachedEntry.ProfileBioLinks) ?? new JArray(),
                ["discordId"]             = live?["discordId"]?.ToString() ?? "",
                ["isFavorited"]           = _favoriteFriends.ContainsKey(userId),
                ["favFriendId"]           = GetFavoriteFriendId(userId),
                ["badges"]                = liveBadges ?? TryParseJArray(cachedEntry.ProfileBadges) ?? new JArray(),
                ["cachedAvatar"]          = (JToken?)TryParseJObject(cachedEntry.ProfileCurrentAvatar) ?? JValue.CreateNull(),
                ["iconFrame"]             = live?["iconFrame"]?.ToString() ?? cachedEntry.ProfileIconFrame,
                ["iconFrameUrl"]          = IconFrameHelper.UrlFor(live?["iconFrame"]?.ToString() ?? cachedEntry.ProfileIconFrame, _core.Inventory),
                ["nameplateEffect"]       = live?["nameplateEffect"]?.ToString() ?? cachedEntry.ProfileNameplate,
                ["nameplateUrl"]          = IconFrameHelper.UrlFor(live?["nameplateEffect"]?.ToString() ?? cachedEntry.ProfileNameplate, _core.Inventory),
                ["profileEffect"]         = live?["profileEffect"]?.ToString() ?? cachedEntry.ProfileEffect,
                ["profileEffectUrl"]      = IconFrameHelper.UrlFor(live?["profileEffect"]?.ToString() ?? cachedEntry.ProfileEffect, _core.Inventory),
                // Cached like the other VRC+ decorations so the background is there on
                // the first paint instead of waiting for the appearance request.
                ["backgroundType"]           = cachedEntry.ProfileBgType,
                ["backgroundTextureId"]      = cachedEntry.ProfileBgTexture,
                ["backgroundTextureUrl"]     = ProfileBackgroundHelper.UrlFor(cachedEntry.ProfileBgTexture),
                ["backgroundGradientTop"]    = cachedEntry.ProfileBgGradTop,
                ["backgroundGradientBottom"] = cachedEntry.ProfileBgGradBottom,
                ["themeButtonColor"]         = cachedEntry.ProfileThemeButton,
                ["themeIconColor"]           = cachedEntry.ProfileThemeIcon,
                ["themeSubtextColor"]        = cachedEntry.ProfileThemeSubtext,
            };
            _core.SendToJS("vrcFriendDetail", diskProfile);

            if (ModalCacheHelper.IsCached(userId))
            {
                if (!ModalCacheHelper.IsGroupsMutualsCached(userId))
                {
                    ModalCacheHelper.MarkGroupsMutuals(userId);
                    _ = Task.Run(async () =>
                    {
                    try
                    {
                        var gm = await RefreshGroupsMutualsAsync(userId);
                        if (gm == null) return;
                        var v = gm.Value;
                        var changed =
                            !JToken.DeepEquals(diskProfile["userGroups"],      v.userGroups) ||
                            !JToken.DeepEquals(diskProfile["representedGroup"], v.representedGroup) ||
                            !JToken.DeepEquals(diskProfile["mutuals"],          v.mutuals) ||
                            !JToken.DeepEquals(diskProfile["mutualGroups"],     v.mutualGroups) ||
                            (diskProfile["mutualsOptedOut"]?.Value<bool>() ?? false) != v.optedOut;
                        diskProfile["userGroups"]       = v.userGroups;
                        diskProfile["representedGroup"]  = v.representedGroup;
                        diskProfile["mutuals"]           = v.mutuals;
                        diskProfile["mutualGroups"]      = v.mutualGroups;
                        diskProfile["mutualsOptedOut"]   = v.optedOut;
                        if (changed) _core.SendToJS("vrcFriendDetail", diskProfile);
                    }
                    catch { }
                    });
                }
                return;
            }

            ModalCacheHelper.Mark(userId);

            bool startRefresh;
            lock (_profileRefreshInFlight) startRefresh = _profileRefreshInFlight.Add(userId);
            if (startRefresh)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var fresh = await BuildUserDetailPayloadAsync(userId);
                        if (fresh == null) return;
                        ModalCacheHelper.MarkGroupsMutuals(userId);
                        _core.TimeEngine.SaveUserProfileCache(userId, Newtonsoft.Json.JsonConvert.SerializeObject(fresh));
                        _core.SendToJS("vrcFriendDetail", fresh);
                    }
                    catch { }
                    finally { lock (_profileRefreshInFlight) _profileRefreshInFlight.Remove(userId); }
                });
            }
            return;
        }

        try
        {
            var payload = await BuildUserDetailPayloadAsync(userId);
            if (payload == null)
            {
                _core.SendToJS("vrcFriendDetailError", new { error = "Could not load user profile" });
                return;
            }
            ModalCacheHelper.MarkGroupsMutuals(userId);
            _core.TimeEngine.SaveUserProfileCache(userId, Newtonsoft.Json.JsonConvert.SerializeObject(payload));
            _core.SendToJS("vrcFriendDetail", payload);
        }
        catch (Exception ex)
        {
            _core.SendToJS("vrcFriendDetailError", new { error = ex.Message });
            _core.SendToJS("log", new { msg = $"VRChat: Error loading profile — {ex.Message}", color = "err" });
        }
    }

    // Extract the file_ UUID from avatar image URLs for avtrdb lookup.
    private static bool IsDbCacheFresh(string? cachedAt, TimeSpan ttl)
    {
        if (string.IsNullOrEmpty(cachedAt)) return false;
        return DateTime.TryParse(cachedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
            && DateTime.UtcNow - t < ttl;
    }

    private static JArray? TryParseJArray(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JArray.Parse(json); } catch { return null; }
    }

    private static JObject? TryParseJObject(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JObject.Parse(json); } catch { return null; }
    }

    private static string ParseIsoDate(JToken? token)
    {
        var s = token?.ToString();
        if (string.IsNullOrEmpty(s)) return "";
        if (DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return dt.ToString("o");
        return "";
    }

    private static async Task<string> ResolveUserImageAsync(string userId, string? rawImageUrl)
    {
        var cached = ImageCacheHelper.GetUserCached(userId);
        if (cached != null) return ImageCacheHelper.ToLocalUrl(cached);
        if (!string.IsNullOrWhiteSpace(rawImageUrl))
        {
            await ImageCacheHelper.CacheUserAsync(userId, rawImageUrl);
            cached = ImageCacheHelper.GetUserCached(userId);
            if (cached != null) return ImageCacheHelper.ToLocalUrl(cached);
        }
        return "";
    }

    private static readonly System.Text.RegularExpressions.Regex _fileIdRx =
        new(@"(file_[a-f0-9\-]{36})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string ExtractAvatarFileId(JObject user)
    {
        foreach (var field in new[] { "currentAvatarImageUrl", "currentAvatarThumbnailImageUrl" })
        {
            var url = user[field]?.ToString() ?? "";
            var m = _fileIdRx.Match(url);
            if (m.Success) return m.Groups[1].Value;
        }
        return "";
    }

    private (List<object> userGroups, object? representedGroup) BuildGroupsDisplay(JArray raw, string? overrideRepId = null)
    {
        var userGroups = new List<object>();
        object? representedGroup = null;
        foreach (var g in raw)
        {
            var gid = g["groupId"]?.ToString() ?? g["id"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(gid)) continue;
            // overrideRepId comes from a fresh /users/{id}/groups/represented call
            // so the cached groups list (1-day TTL) doesn't pin a stale rep group.
            var isRep = overrideRepId != null
                ? gid == overrideRepId
                : (g["isRepresenting"]?.Value<bool>() ?? false);
            userGroups.Add(new
            {
                id = gid, name = g["name"]?.ToString() ?? "",
                shortCode = g["shortCode"]?.ToString() ?? "",
                discriminator = g["discriminator"]?.ToString() ?? "",
                iconUrl = ImageCacheHelper.GetGroupUrl(gid, g["iconUrl"]?.ToString()),
                bannerUrl = ImageCacheHelper.NormalizeTo512(g["bannerUrl"]?.ToString() ?? ""),
                memberCount = g["memberCount"]?.Value<int>() ?? 0,
                isRepresenting = isRep,
                ownerId = g["ownerId"]?.ToString() ?? "",
            });
            if (isRep && representedGroup == null)
                representedGroup = new
                {
                    id = gid, name = g["name"]?.ToString() ?? "",
                    shortCode = g["shortCode"]?.ToString() ?? "",
                    discriminator = g["discriminator"]?.ToString() ?? "",
                    iconUrl = ImageCacheHelper.GetGroupUrl(gid, g["iconUrl"]?.ToString()),
                    bannerUrl = ImageCacheHelper.NormalizeTo512(g["bannerUrl"]?.ToString() ?? ""),
                    memberCount = g["memberCount"]?.Value<int>() ?? 0,
                };
        }
        return (userGroups, representedGroup);
    }

    private static List<object> BuildWorldsDisplay(JArray raw)
    {
        var list = new List<object>();
        foreach (var w in raw)
        {
            if (w is not JObject wObj) continue;
            list.Add(new
            {
                id = wObj["id"]?.ToString() ?? "", name = wObj["name"]?.ToString() ?? "",
                thumbnailImageUrl = ImageCacheHelper.GetWorldUrl(wObj["id"]?.ToString(), wObj["imageUrl"]?.ToString() ?? wObj["thumbnailImageUrl"]?.ToString()),
                occupants = wObj["occupants"]?.Value<int>() ?? 0,
                favorites = wObj["favorites"]?.Value<int>() ?? 0,
                visits = wObj["visits"]?.Value<int>() ?? 0,
            });
        }
        return list;
    }

    private List<object> BuildMutualsDisplay(JArray mutualsArr)
    {
        var list = new List<object>();
        foreach (var mu in mutualsArr)
        {
            if (mu is not JObject muObj) continue;
            var muId = muObj["id"]?.ToString() ?? "";
            var muImage = ImageCacheHelper.GetUserUrl(muId, (_friendNameImg.TryGetValue(muId, out var muFi) && !string.IsNullOrEmpty(muFi.image))
                ? muFi.image : VRChatApiService.GetUserImage(muObj));
            var muLocation = muObj["location"]?.ToString() ?? "";
            var muStatus = muObj["status"]?.ToString() ?? "offline";
            bool muIsInGame = !string.IsNullOrEmpty(muLocation) && muLocation != "offline" && muLocation != "private" && muLocation != "traveling";
            bool muIsOffline = muStatus == "offline" || muLocation == "offline";
            list.Add(new
            {
                id = muId, displayName = muObj["displayName"]?.ToString() ?? "",
                image = muImage, status = muStatus,
                statusDescription = muObj["statusDescription"]?.ToString() ?? "",
                presence = muIsOffline ? "offline" : muIsInGame ? "game" : "web",
                currentAvatarThumbnailImageUrl = muObj["currentAvatarThumbnailImageUrl"]?.ToString() ?? "",
            });
        }
        return list;
    }

    private static List<object> BuildMutualGroupsDisplay(JArray raw)
    {
        var list = new List<object>();
        foreach (var mg in raw)
        {
            if (mg is not JObject mgObj) continue;
            var gid = mgObj["groupId"]?.ToString() ?? mgObj["id"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(gid)) continue;
            list.Add(new
            {
                id = gid, name = mgObj["name"]?.ToString() ?? "",
                shortCode = mgObj["shortCode"]?.ToString() ?? "",
                discriminator = mgObj["discriminator"]?.ToString() ?? "",
                iconUrl = ImageCacheHelper.GetGroupUrl(gid, mgObj["iconUrl"]?.ToString()),
                bannerUrl = ImageCacheHelper.NormalizeTo512(mgObj["bannerUrl"]?.ToString() ?? ""),
                memberCount = mgObj["memberCount"]?.Value<int>() ?? 0,
            });
        }
        return list;
    }

    private static (string button, string icon, string subtext) ResolveActiveTheme(JObject? appearance)
    {
        if (appearance == null) return ("", "", "");

        var flatButton  = UsersAPI.NormalizeThemeColor(appearance["themeButtonColor"]?.ToString());
        var flatIcon    = UsersAPI.NormalizeThemeColor(appearance["themeIconColor"]?.ToString());
        var flatSubtext = UsersAPI.NormalizeThemeColor(appearance["themeSubtextColor"]?.ToString());
        if (flatButton != "" || flatIcon != "" || flatSubtext != "")
            return (flatButton, flatIcon, flatSubtext);

        var id = appearance["themeId"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(id) || appearance["themes"] is not JArray list) return ("", "", "");
        foreach (var t in list.OfType<JObject>())
        {
            if (t["id"]?.ToString() != id) continue;
            return (UsersAPI.NormalizeThemeColor(t["buttonColor"]?.ToString()),
                    UsersAPI.NormalizeThemeColor(t["iconColor"]?.ToString()),
                    UsersAPI.NormalizeThemeColor(t["subtextColor"]?.ToString()));
        }
        return ("", "", "");
    }

    public async Task<object?> BuildUserDetailPayloadAsync(string userId, bool forceFresh = false)
    {
        JObject? user;
        JObject? storeSnapshot;
        lock (_friendStore) _friendStore.TryGetValue(userId, out storeSnapshot);

        // Backgrounds/banner live on their own endpoint, /users/{id} does not have them.
        var isSelfProfile = userId == _core.VrcApi.CurrentUserId;
        var appearance = await _core.Users.GetProfileAppearanceAsync(userId, isSelfProfile);
        var activeTheme = ResolveActiveTheme(appearance);

        user = storeSnapshot;
        if (forceFresh || user == null || user["badges"] == null)
        {
            var fresh = await _core.Users.GetUserAsync(userId);
            if (fresh != null) user = fresh;
            else if (user == null) return null;
        }
        var _freshImg = VRChatApiService.GetUserImage(user);

        if (storeSnapshot != null)
        {
            var liveStatus = storeSnapshot["status"]?.ToString();
            var liveLoc = storeSnapshot["location"]?.ToString();
            if (!string.IsNullOrEmpty(liveStatus)) user["status"] = liveStatus;
            if (liveLoc != null) user["location"] = liveLoc;
        }

        var location = user["location"]?.ToString() ?? "private";
        var (worldId, instanceId, instanceType) = VRChatApiService.ParseLocation(location);
        bool hasWorld = !string.IsNullOrEmpty(worldId) && worldId.StartsWith("wrld_");

        var profileTtl = TimeSpan.FromDays(1);
        var dbCache = _core.TimeEngine.GetUserProfileCache(userId);
        JObject? cachedContent    = IsDbCacheFresh(dbCache?.ContentCachedAt,      profileTtl)           ? TryParseJObject(dbCache!.ContentJson)    : null;
        JArray? cachedWorlds      = cachedContent?["worlds"] as JArray;

        var instTask           = hasWorld ? _core.Instances.GetInstanceAsync(location) : Task.FromResult<JObject?>(null);
        var grpsTask           = _core.Users.GetUserGroupsByIdAsync(userId);
        var worldsTask         = cachedWorlds != null
            ? Task.FromResult(cachedWorlds)
            : _core.World.GetUserWorldsAsync(userId);
        var mutualsTask        = _core.Users.GetUserMutualsAsync(userId);
        var mutualGroupsTask   = _core.Users.GetUserMutualGroupsAsync(userId);
        var repGroupTask       = _core.Users.GetUserRepresentedGroupAsync(userId);

        await Task.WhenAll(new Task[] { instTask, grpsTask, worldsTask, mutualsTask, mutualGroupsTask, repGroupTask }
            .Select(t => t.ContinueWith(_ => { })));

        var inst = instTask.IsCompletedSuccessfully ? instTask.Result : null;
        var groups = grpsTask.IsCompletedSuccessfully ? grpsTask.Result : new JArray();
        var worlds = worldsTask.IsCompletedSuccessfully ? worldsTask.Result : new JArray();
        var mutualGroupsArr = mutualGroupsTask.IsCompletedSuccessfully ? mutualGroupsTask.Result : new JArray();
        var freshRepGroup = repGroupTask.IsCompletedSuccessfully ? repGroupTask.Result : null;

        if (cachedWorlds == null && worldsTask.IsCompletedSuccessfully)
        {
            var cf = (cachedContent ?? new JObject());
            cf["worlds"] = JToken.FromObject(worlds);
            _core.TimeEngine.SaveUserContentCache(userId, cf.ToString(Newtonsoft.Json.Formatting.None));
        }
        if (mutualGroupsTask.IsCompletedSuccessfully && mutualGroupsArr.Count > 0)
            _core.TimeEngine.SaveUserMutualGroupsCache(userId, Newtonsoft.Json.JsonConvert.SerializeObject(mutualGroupsArr));
        if (grpsTask.IsCompletedSuccessfully && groups.Count > 0)
            _core.TimeEngine.SaveUserGroupsCache(userId, Newtonsoft.Json.JsonConvert.SerializeObject(groups));

        var (mutualsArr, mutualsOptedOut) = mutualsTask.IsCompletedSuccessfully
            ? mutualsTask.Result : (new JArray(), false);

        if (mutualsTask.IsCompletedSuccessfully && (mutualsArr.Count > 0 || mutualsOptedOut))
            _core.TimeEngine.SaveUserMutualsCache(userId, Newtonsoft.Json.JsonConvert.SerializeObject(new { mutuals = mutualsArr, optedOut = mutualsOptedOut }));
        var badgesArr = user["badges"] as JArray ?? new JArray();

        if (instanceType == "private" && inst?["canRequestInvite"]?.Value<bool>() == true)
            instanceType = "invite_plus";

        var instWorld = inst?["world"] as JObject;
        string worldName = instWorld?["name"]?.ToString() ?? "";
        string worldThumb = ImageCacheHelper.GetWorldUrl(worldId, instWorld?["imageUrl"]?.ToString() ?? instWorld?["thumbnailImageUrl"]?.ToString());
        int worldCapacity = instWorld?["capacity"]?.Value<int>() ?? inst?["capacity"]?.Value<int>() ?? 0;
        int userCount = inst?["n_users"]?.Value<int>() ?? inst?["userCount"]?.Value<int>() ?? 0;
        string userNote = user["note"]?.ToString() ?? "";

        bool canJoin = instanceType is "public" or "friends" or "friends+" or "hidden"
            or "group-public" or "group-plus" or "group-members" or "group";
        bool canRequestInvite = instanceType is "private" or "invite_plus";
        bool isInWorld = !string.IsNullOrEmpty(worldId) && location != "private" && location != "offline" && location != "traveling";

        JObject? cachedRepGroup = null;
        if (freshRepGroup == null && !string.IsNullOrEmpty(dbCache?.ProfileRepresentedGroup))
            cachedRepGroup = TryParseJObject(dbCache!.ProfileRepresentedGroup);
        var repSrc = freshRepGroup ?? cachedRepGroup;

        var freshRepGid = repSrc?["groupId"]?.ToString()
                       ?? repSrc?["id"]?.ToString();
        if (string.IsNullOrEmpty(freshRepGid)) freshRepGid = null;

        var (userGroups, representedGroup) = BuildGroupsDisplay(groups, freshRepGid);
        if (representedGroup == null && repSrc != null && freshRepGid != null)
        {
            representedGroup = new
            {
                id = freshRepGid,
                name = repSrc["name"]?.ToString() ?? "",
                shortCode = repSrc["shortCode"]?.ToString() ?? "",
                discriminator = repSrc["discriminator"]?.ToString() ?? "",
                iconUrl = ImageCacheHelper.GetGroupUrl(freshRepGid, repSrc["iconUrl"]?.ToString()),
                bannerUrl = ImageCacheHelper.NormalizeTo512(repSrc["bannerUrl"]?.ToString() ?? ""),
                memberCount = repSrc["memberCount"]?.Value<int>() ?? 0,
            };
        }

        var userWorlds                     = BuildWorldsDisplay(worlds);
        var mutualGroupsList               = BuildMutualGroupsDisplay(mutualGroupsArr);
        var mutualsList                    = BuildMutualsDisplay(mutualsArr);

        List<object> badges = new();
        foreach (var b in badgesArr)
        {
            if (b is not JObject bObj) continue;
            var rawBadgeUrl = bObj["badgeImageUrl"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(rawBadgeUrl)) continue;
            var badgeId = bObj["badgeId"]?.ToString() ?? "";
            badges.Add(new
            {
                id = badgeId,
                name = bObj["badgeName"]?.ToString() ?? "",
                description = bObj["badgeDescription"]?.ToString() ?? "",
                imageUrl = ImageCacheHelper.GetBadgeUrl(badgeId, rawBadgeUrl),
                showcased = bObj["showcased"]?.Value<bool>() ?? false,
            });
        }

        var isCoPresent = (_core.IsVrcRunning?.Invoke() ?? false)
            && _core.LogWatcher.GetCurrentPlayers().Any(p => p.UserId == userId);
        var (totalSeconds, lastSeenLocal) = _core.TimeEngine.GetUserStats(userId, isCoPresent);

        var _dbgFileId = ExtractAvatarFileId(user);
        _core.SendToJS("log", new { msg = $"[Avatar] user={userId} avatarFileId='{_dbgFileId}'", color = "info" });

        return new
        {
            id = user["id"]?.ToString() ?? "",
            displayName = user["displayName"]?.ToString() ?? "",
            image = ImageCacheHelper.GetUserUrl(user["id"]?.ToString(), VRChatApiService.GetUserImage(user)),
            iconFrame = user["iconFrame"]?.ToString() ?? "",
            iconFrameUrl = IconFrameHelper.UrlFor(user["iconFrame"]?.ToString(), _core.Inventory),
            nameplateEffect = user["nameplateEffect"]?.ToString() ?? "",
            nameplateUrl = IconFrameHelper.UrlFor(user["nameplateEffect"]?.ToString(), _core.Inventory),
            profileEffect = user["profileEffect"]?.ToString() ?? "",
            profileEffectUrl = IconFrameHelper.UrlFor(user["profileEffect"]?.ToString(), _core.Inventory),
            // VRC+ profile background. The texture id is mapped to an asset URL in the
            // frontend, where the file list lives next to the CSS that uses it.
            themeId                  = appearance?["themeId"]?.ToString() ?? "",
            themes                   = appearance?["themes"] as JArray ?? new JArray(),
            themeButtonColor         = activeTheme.button,
            themeIconColor           = activeTheme.icon,
            themeSubtextColor        = activeTheme.subtext,
            backgroundType           = appearance?["backgroundType"]?.ToString() ?? "",
            backgroundTextureId      = appearance?["backgroundTextureId"]?.ToString() ?? "",
            backgroundTextureUrl     = ProfileBackgroundHelper.UrlFor(appearance?["backgroundTextureId"]?.ToString()),
            backgroundGradientTop    = appearance?["backgroundGradientTop"]?.ToString() ?? "",
            backgroundGradientBottom = appearance?["backgroundGradientBottom"]?.ToString() ?? "",
            status = user["status"]?.ToString() ?? "offline",
            statusDescription = user["statusDescription"]?.ToString() ?? "",
            bio = user["bio"]?.ToString() ?? "",
            lastLogin = ParseIsoDate(user["last_login"]),
            lastActivity = ParseIsoDate(user["last_activity"]),
            dateJoined = user["date_joined"]?.ToString() ?? "",
            location, worldName, worldThumb, instanceType, userCount, worldCapacity,
            ageGate = location.Contains("~ageGate"),
            isFriend = user["isFriend"]?.Value<bool>() ?? !string.IsNullOrEmpty(user["friendKey"]?.ToString()),
            canJoin = isInWorld && canJoin, canRequestInvite, canInvite = true,
            currentAvatarImageUrl = ImageCacheHelper.GetAvatarUrl(user["currentAvatar"]?.ToString(), user["currentAvatarImageUrl"]?.ToString()),
            currentAvatarId = user["currentAvatar"]?.ToString() ?? "",
            avatarFileId = ExtractAvatarFileId(user),
            profilePicOverride = ImageCacheHelper.GetUserPicOverrideUrl(user["id"]?.ToString(), user["profilePicOverride"]?.ToString()),
            bannerUrl = ImageCacheHelper.GetUserBannerUrl(user["id"]?.ToString(), user["bannerUrl"]?.ToString()),
            tags = user["tags"]?.ToObject<List<string>>() ?? new(),
            note = user["note"]?.ToString() ?? "",
            friendKey = user["friendKey"]?.ToString() ?? "",
            travelingToLocation = user["travelingToLocation"]?.ToString() ?? "",
            state = user["state"]?.ToString() ?? "",
            lastPlatform = user["last_platform"]?.ToString() ?? "",
            platform = user["platform"]?.ToString() ?? "",
            userNote, totalTimeSeconds = totalSeconds,
            meets = _core.Timeline?.GetMeetAgainCount(userId) ?? 0,
            firstMeetDate = _core.Timeline?.GetFirstMeetDate(userId) ?? "",
            inSameInstance = (_core.IsVrcRunning?.Invoke() ?? false)
                && _core.LogWatcher.GetCurrentPlayers().Any(p => p.UserId == userId),
            lastSeenTracked = _core.Timeline?.GetLastSeenTimestamp(userId) ?? "",
            pronouns = user["pronouns"]?.ToString() ?? "",
            ageVerificationStatus = user["ageVerificationStatus"]?.ToString() ?? "",
            ageVerified = user["ageVerified"]?.Value<bool>() ?? false,
            allowAvatarCopying = user["allowAvatarCopying"]?.Value<bool>() ?? false,
            representedGroup, userGroups, mutuals = mutualsList, mutualGroups = mutualGroupsList, mutualsOptedOut, userWorlds,
            bioLinks = user["bioLinks"]?.ToObject<List<string>>() ?? new List<string>(),
            discordId = user["discordId"]?.ToString() ?? "",
            isFavorited = _favoriteFriends.ContainsKey(userId),
            favFriendId = GetFavoriteFriendId(userId),
            memo = _core.Timeline?.GetUserMemo(userId) ?? "",
            badges,
            cachedAvatar = TryParseJObject(dbCache?.ProfileCurrentAvatar ?? "") ?? (object?)null,
            rawJson = user,
        };
    }

    private async Task<(JArray userGroups, JToken representedGroup, JArray mutuals, JArray mutualGroups, bool optedOut)?> RefreshGroupsMutualsAsync(string userId)
    {
        if (!_core.VrcApi.IsLoggedIn) return null;

        var grpsTask         = _core.Users.GetUserGroupsByIdAsync(userId);
        var mutualsTask      = _core.Users.GetUserMutualsAsync(userId);
        var mutualGroupsTask = _core.Users.GetUserMutualGroupsAsync(userId);
        var repGroupTask     = _core.Users.GetUserRepresentedGroupAsync(userId);

        await Task.WhenAll(new Task[] { grpsTask, mutualsTask, mutualGroupsTask, repGroupTask }
            .Select(t => t.ContinueWith(_ => { })));

        var groups          = grpsTask.IsCompletedSuccessfully ? grpsTask.Result : new JArray();
        var (mutualsArr, mutualsOptedOut) = mutualsTask.IsCompletedSuccessfully ? mutualsTask.Result : (new JArray(), false);
        var mutualGroupsArr = mutualGroupsTask.IsCompletedSuccessfully ? mutualGroupsTask.Result : new JArray();
        var freshRepGroup   = repGroupTask.IsCompletedSuccessfully ? repGroupTask.Result : null;

        if (grpsTask.IsCompletedSuccessfully && groups.Count > 0)
            _core.TimeEngine.SaveUserGroupsCache(userId, Newtonsoft.Json.JsonConvert.SerializeObject(groups));
        if (mutualGroupsTask.IsCompletedSuccessfully && mutualGroupsArr.Count > 0)
            _core.TimeEngine.SaveUserMutualGroupsCache(userId, Newtonsoft.Json.JsonConvert.SerializeObject(mutualGroupsArr));
        if (mutualsTask.IsCompletedSuccessfully && (mutualsArr.Count > 0 || mutualsOptedOut))
            _core.TimeEngine.SaveUserMutualsCache(userId, Newtonsoft.Json.JsonConvert.SerializeObject(new { mutuals = mutualsArr, optedOut = mutualsOptedOut }));

        var freshRepGid = freshRepGroup?["groupId"]?.ToString() ?? freshRepGroup?["id"]?.ToString();
        if (string.IsNullOrEmpty(freshRepGid)) freshRepGid = null;

        var (userGroups, representedGroup) = BuildGroupsDisplay(groups, freshRepGid);
        if (representedGroup == null && freshRepGroup != null && freshRepGid != null)
            representedGroup = new
            {
                id = freshRepGid,
                name = freshRepGroup["name"]?.ToString() ?? "",
                shortCode = freshRepGroup["shortCode"]?.ToString() ?? "",
                discriminator = freshRepGroup["discriminator"]?.ToString() ?? "",
                iconUrl = ImageCacheHelper.GetGroupUrl(freshRepGid, freshRepGroup["iconUrl"]?.ToString()),
                bannerUrl = ImageCacheHelper.NormalizeTo512(freshRepGroup["bannerUrl"]?.ToString() ?? ""),
                memberCount = freshRepGroup["memberCount"]?.Value<int>() ?? 0,
            };

        var mutualGroupsList = BuildMutualGroupsDisplay(mutualGroupsArr);
        var mutualsList      = BuildMutualsDisplay(mutualsArr);

        return (
            JArray.FromObject(userGroups),
            representedGroup != null ? JObject.FromObject(representedGroup) : (JToken)JValue.CreateNull(),
            JArray.FromObject(mutualsList),
            JArray.FromObject(mutualGroupsList),
            mutualsOptedOut
        );
    }

    // Join Friend

    private (string dateJoined, string pronouns, int mutualFriends, int mutualGroups, string lastSeen) CachedFacts(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return ("", "", 0, 0, "");
        var lastSeen = LastSeenTogether(userId);
        try
        {
            var c = _core.TimeEngine.GetUserProfileCache(userId);
            if (c == null) return ("", "", 0, 0, lastSeen);
            return (c.ProfileDateJoined, c.ProfilePronouns, MutualFriendCount(c.MutualsJson), JsonArrayCount(c.MutualGroupsJson), lastSeen);
        }
        catch { return ("", "", 0, 0, lastSeen); }
    }

    private readonly object _lastSeenLock = new();
    private Dictionary<string, string>? _lastSeenMap;
    private DateTime _lastSeenMapAt = DateTime.MinValue;

    private string LastSeenTogether(string userId)
    {
        try
        {
            lock (_lastSeenLock)
            {
                if (_lastSeenMap == null || (DateTime.UtcNow - _lastSeenMapAt).TotalSeconds > 30)
                {
                    _lastSeenMap = _core.Timeline.GetLastSeenTogetherMap();
                    _lastSeenMapAt = DateTime.UtcNow;
                }
                return _lastSeenMap.TryGetValue(userId, out var v) ? v : "";
            }
        }
        catch { return ""; }
    }

    private static int MutualFriendCount(string json)
    {
        try
        {
            var o = JObject.Parse(json);
            if (o["optedOut"]?.Value<bool>() == true) return 0;
            return (o["mutuals"] as JArray)?.Count ?? 0;
        }
        catch { return 0; }
    }

    private static int JsonArrayCount(string json)
    {
        try { return JArray.Parse(json).Count; }
        catch { return 0; }
    }

    public void EnrichFromProfileCache(JObject target, string userId, bool preferLive)
    {
        if (target == null || string.IsNullOrEmpty(userId)) return;

        var seen = LastSeenTogether(userId);
        if (seen.Length > 0 && string.IsNullOrEmpty(target["lastSeen"]?.ToString()))
            target["lastSeen"] = seen;

        UnifiedTimeEngine.UserProfileCache? c;
        try { c = _core.TimeEngine.GetUserProfileCache(userId); }
        catch { return; }
        if (c == null) return;

        void Str(string key, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (preferLive && !string.IsNullOrEmpty(target[key]?.ToString())) return;
            target[key] = value;
        }
        void Arr(string key, string json)
        {
            JArray arr;
            try { arr = JArray.Parse(json); } catch { return; }
            if (arr.Count == 0) return;
            if (preferLive && target[key] is JArray live && live.Count > 0) return;
            target[key] = arr;
        }

        Str("displayName", c.DisplayName);
        Str("image", c.Image);
        Str("status", c.ProfileStatus);
        Str("statusDescription", c.ProfileStatusDesc);
        Str("bio", c.ProfileBio);
        Str("pronouns", c.ProfilePronouns);
        Str("lastLogin", c.ProfileLastLogin);
        Str("lastActivity", c.ProfileLastActivity);
        Str("dateJoined", c.ProfileDateJoined);
        Str("platform", c.ProfileLastPlatform);
        Arr("tags", c.ProfileTags);
        Arr("bioLinks", c.ProfileBioLinks);

        var mf = MutualFriendCount(c.MutualsJson);
        var mg = JsonArrayCount(c.MutualGroupsJson);
        if (mf > 0) target["mutualFriends"] = mf;
        if (mg > 0) target["mutualGroups"] = mg;
    }

    public void PushFriendFacts(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return;
        var facts = CachedFacts(userId);
        JObject? f;
        lock (_friendStore) _friendStore.TryGetValue(userId, out f);
        _core.SendToJS("vrcFriendFacts", new
        {
            id = userId,
            dateJoined = facts.dateJoined,
            pronouns = f != null ? PickPronouns(f, facts.pronouns) : facts.pronouns,
            mutualFriends = facts.mutualFriends,
            mutualGroups = facts.mutualGroups,
            lastSeen = facts.lastSeen,
        });
    }

    private static string PickPronouns(JObject f, string cached)
    {
        var live = f["pronouns"]?.ToString() ?? "";
        return string.IsNullOrEmpty(live) ? cached : live;
    }

    private Task HandleJoinFriendAsync(string joinLoc)
    {
        _core.SendToJS("vrcLaunchNeeded", new { location = joinLoc, steamVr = _core.IsSteamVrRunning?.Invoke() ?? false });
        return Task.CompletedTask;
    }

    // WebSocket Event Handlers

    private void OnWsFriendLocation(object? sender, FriendEventArgs e)
    {
        if (string.IsNullOrEmpty(e.UserId) || !_friendStateSeeded) return;

        var loc = e.Location ?? "";

        // friend-location can fire with location="offline" or location="" (pseudo-null).
        // Do NOT overwrite the store with these non-location values.
        if (loc == "offline" || loc == "") return;

        // "traveling" means switching worlds — keep them as "in-game", don't log anything
        if (loc == "traveling")
        {
            _friendLastLoc[e.UserId] = "traveling";
            return;
        }

        MergeFriendStore(e.UserId, e.User, location: loc,
            platform: string.IsNullOrEmpty(e.Platform) ? null : e.Platform);
        PushFriendUpdate(e.UserId);

        if (e.User != null)
            _friendNameImg[e.UserId] = (
                e.User["displayName"]?.ToString() ?? _friendNameImg.GetValueOrDefault(e.UserId).name ?? "",
                VRChatApiService.GetUserImage(e.User).Length > 0
                    ? VRChatApiService.GetUserImage(e.User)
                    : _friendNameImg.GetValueOrDefault(e.UserId).image ?? ""
            );

        var newLoc = e.Location;
        var worldId = newLoc.Contains(':') ? newLoc.Split(':')[0] : newLoc;
        if (!worldId.StartsWith("wrld_")) { _friendLastLoc[e.UserId] = newLoc; return; }

        var oldLoc = _friendLastLoc.GetValueOrDefault(e.UserId, "");
        var oldWorldId = oldLoc.Contains(':') ? oldLoc.Split(':')[0] : oldLoc;
        if (oldLoc == newLoc || oldWorldId == worldId) { _friendLastLoc[e.UserId] = newLoc; return; }

        _friendLastLoc[e.UserId] = newLoc;

        // Close previous GPS event for this friend
        if (_friendCurrentGpsEventId.TryGetValue(e.UserId, out var prevGpsId))
            _core.Timeline.SetFriendEventLeftAt(prevGpsId, DateTime.UtcNow.ToString("o"));

        var (fname, fimg) = _friendNameImg.GetValueOrDefault(e.UserId, ("", ""));
        var fev = new TimelineService.FriendTimelineEvent
        {
            Type = "friend_gps", FriendId = e.UserId, FriendName = fname,
            FriendImage = fimg, WorldId = worldId, Location = newLoc, Tracked = 1,
        };
        _core.Timeline.AddFriendEvent(fev);
        _friendCurrentGpsEventId[e.UserId] = fev.Id;

        // Cross-reference colocated friends in the same instance
        var newLocBase = newLoc.Contains('~') ? newLoc[..newLoc.IndexOf('~')] : newLoc;
        var (myName, myImg) = (fname, fimg);
        foreach (var (coId, coLoc) in _friendLastLoc.ToList())
        {
            if (coId == e.UserId || string.IsNullOrEmpty(coLoc) || coLoc == "offline" || coLoc == "traveling") continue;
            var coBase = coLoc.Contains('~') ? coLoc[..coLoc.IndexOf('~')] : coLoc;
            if (coBase != newLocBase) continue;
            var (coName, coImg) = _friendNameImg.GetValueOrDefault(coId, ("", ""));
            _core.Timeline.AddFriendEventColocated(fev.Id, coId, coName, coImg);
            if (_friendCurrentGpsEventId.TryGetValue(coId, out var coEvId))
                _core.Timeline.AddFriendEventColocated(coEvId, e.UserId, myName, myImg);
        }

        _core.SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev));

        var evId = fev.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                var world = await _core.World.GetWorldAsync(worldId);
                if (world == null) return;
                var wname = world["name"]?.ToString() ?? "";
                var wthumb = world["thumbnailImageUrl"]?.ToString() ?? "";
                _core.Timeline.UpdateFriendEventWorld(evId, wname, wthumb);
                var updated = _core.Timeline.GetFriendEvents().FirstOrDefault(x => x.Id == evId);
                if (updated != null)
                    _core.SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(updated));
#if WINDOWS
                lock (_core.VrWorldCache) _core.VrWorldCache[worldId] = (wname, wthumb);
                PushFriendUpdate(e.UserId);
                PushVroLocations();
#endif
            }
            catch { }
        });
    }

    private void OnWsFriendActive(object? sender, FriendEventArgs e)
    {
        // friend-active = website/app activity, NOT in-game.
        if (string.IsNullOrEmpty(e.UserId) || !_friendStateSeeded) return;

        // Detect "left the game": if friend was previously in a world, they just exited
        var prevLoc = _friendLastLoc.GetValueOrDefault(e.UserId, "");
        bool wasInGame = !string.IsNullOrEmpty(prevLoc) && prevLoc != "offline" && prevLoc != "";

        MergeFriendStore(e.UserId, e.User,
            location: string.IsNullOrEmpty(e.Location) ? "" : e.Location,
            platform: string.IsNullOrEmpty(e.Platform) ? null : e.Platform);
        PushFriendUpdate(e.UserId);

        var (fname, fimg) = _friendNameImg.GetValueOrDefault(e.UserId, ("", ""));
        if (e.User != null)
        {
            fname = e.User["displayName"]?.ToString() ?? fname;
            var img = VRChatApiService.GetUserImage(e.User);
            if (img.Length > 0) fimg = img;
            _friendNameImg[e.UserId] = (fname, fimg);
        }

        // Left the game → log offline (but NOT if traveling — that's a world change, not leaving)
        if (wasInGame && prevLoc != "traveling")
        {
            _friendLastLoc[e.UserId] = "";
            if (_friendCurrentGpsEventId.TryGetValue(e.UserId, out var gpsId))
            {
                _core.Timeline.SetFriendEventLeftAt(gpsId, DateTime.UtcNow.ToString("o"));
                _friendCurrentGpsEventId.Remove(e.UserId);
            }
            var fev = new TimelineService.FriendTimelineEvent
            {
                Type = "friend_offline", FriendId = e.UserId, FriendName = fname, FriendImage = fimg,
            };
            _core.Timeline.AddFriendEvent(fev);
            _core.SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev));
        }
        else
        {
            _friendLastLoc[e.UserId] = "";
        }

        // friend-active only updates friendslist (dot→circle), no timeline event for web.
    }

    private void OnWsFriendOffline(object? sender, FriendEventArgs e)
    {
        if (string.IsNullOrEmpty(e.UserId) || !_friendStateSeeded) return;

        MergeFriendStore(e.UserId, null, wentOffline: true);
        PushFriendUpdate(e.UserId);

        var (fname, fimg) = _friendNameImg.GetValueOrDefault(e.UserId, ("", ""));
        if (e.User != null)
        {
            fname = e.User["displayName"]?.ToString() ?? fname;
            var img = VRChatApiService.GetUserImage(e.User);
            if (img.Length > 0) fimg = img;
            _friendNameImg[e.UserId] = (fname, fimg);
        }

        var prevLoc = _friendLastLoc.GetValueOrDefault(e.UserId, "");
        bool wasInGame = !string.IsNullOrEmpty(prevLoc) && prevLoc != "offline" && prevLoc != "";
        _friendLastLoc[e.UserId] = "offline";

        // Only log game offline, not web offline
        // Don't log offline if they were "traveling" — that's a world change, not leaving
        if (!wasInGame || prevLoc == "traveling") return;

        if (_friendCurrentGpsEventId.TryGetValue(e.UserId, out var gpsId))
        {
            _core.Timeline.SetFriendEventLeftAt(gpsId, DateTime.UtcNow.ToString("o"));
            _friendCurrentGpsEventId.Remove(e.UserId);
        }

        var fev = new TimelineService.FriendTimelineEvent
        {
            Type = "friend_offline", FriendId = e.UserId, FriendName = fname, FriendImage = fimg,
        };
        _core.Timeline.AddFriendEvent(fev);
        _core.SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev));
    }

    private void OnWsFriendOnline(object? sender, FriendEventArgs e)
    {
        if (string.IsNullOrEmpty(e.UserId) || !_friendStateSeeded) return;

        MergeFriendStore(e.UserId, e.User,
            location: string.IsNullOrEmpty(e.Location) ? "" : e.Location,
            platform: string.IsNullOrEmpty(e.Platform) ? null : e.Platform);
        PushFriendUpdate(e.UserId);

        var fname = "";
        var fimg = "";
        if (e.User != null)
        {
            fname = e.User["displayName"]?.ToString() ?? "";
            fimg = VRChatApiService.GetUserImage(e.User);
            _friendNameImg[e.UserId] = (fname, fimg);
        }
        else
        {
            (fname, fimg) = _friendNameImg.GetValueOrDefault(e.UserId, ("", ""));
        }

        // Skip duplicate "Came Online" if the friend is already known as in-game.
        // This happens after WebSocket reconnects (re-sends friend-online for all online friends).
        var prevLoc = _friendLastLoc.GetValueOrDefault(e.UserId, "");
        bool alreadyInGame = !string.IsNullOrEmpty(prevLoc) && prevLoc != "offline" && prevLoc != "";

        var onlineLoc = e.Location ?? "";
        _friendLastLoc[e.UserId] = (string.IsNullOrEmpty(onlineLoc) && prevLoc.StartsWith("wrld_")) ? prevLoc : onlineLoc;

        if (alreadyInGame) return; // already online, don't spam timeline

        if (_core.Settings.FriendOnlineToastEnabled && !string.IsNullOrEmpty(fname))
        {
            var alertLevel = _core.TimeEngine.GetFriendAlert(e.UserId);
            var shouldToast = alertLevel == 1 // always notify (per friend override)
                || (alertLevel == 0 && (!_core.Settings.FriendOnlineToastFavOnly || IsFavorited(e.UserId)));
            if (shouldToast)
                _core.SendToJS("friendOnlineToast", new { userId = e.UserId, displayName = fname, image = fimg, alertLevel });
        }

        var fev = new TimelineService.FriendTimelineEvent
        {
            Type = "friend_online", FriendId = e.UserId, FriendName = fname, FriendImage = fimg,
        };
        _core.Timeline.AddFriendEvent(fev);
        _core.SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev));
    }

    private void OnWsFriendUpdated(object? sender, FriendEventArgs e)
    {
        if (e.User == null || string.IsNullOrEmpty(e.UserId) || !_friendStateSeeded) return;

        _core.SendToJS("log", new { msg = $"[WS] friend-update: {e.UserId} ({e.User["displayName"]}) @ {DateTime.UtcNow:HH:mm:ss.fff}", color = "info" });

        MergeFriendStore(e.UserId, e.User);
        PushFriendUpdate(e.UserId);

        var fname = e.User["displayName"]?.ToString() ?? _friendNameImg.GetValueOrDefault(e.UserId).name ?? "";
        var fimg = VRChatApiService.GetUserImage(e.User);
        if (fimg.Length == 0) fimg = _friendNameImg.GetValueOrDefault(e.UserId).image ?? "";
        _friendNameImg[e.UserId] = (fname, fimg);

        var newStatus = e.User["status"]?.ToString() ?? "";
        var newStatusDesc = (e.User["statusDescription"]?.ToString() ?? "").Trim();
        var newBio = (e.User["bio"]?.ToString() ?? "").Trim();

        if (!string.IsNullOrEmpty(newStatus))
        {
            _core.Timeline.UpdateUserLastStatus(e.UserId, newStatus);
            var oldStatus = _friendLastStatus.GetValueOrDefault(e.UserId, "");
            if (oldStatus != newStatus && !string.IsNullOrEmpty(oldStatus))
            {
                var fev = new TimelineService.FriendTimelineEvent
                {
                    Type = "friend_status", FriendId = e.UserId, FriendName = fname,
                    FriendImage = fimg, OldValue = oldStatus, NewValue = newStatus,
                };
                _core.Timeline.AddFriendEvent(fev);
                _core.SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev));

                // "ask me" and "busy" hide location from WS — close the open GPS event
                if ((newStatus == "ask me" || newStatus == "busy") &&
                    _friendCurrentGpsEventId.TryGetValue(e.UserId, out var gpsId))
                {
                    _core.Timeline.SetFriendEventLeftAt(gpsId, DateTime.UtcNow.ToString("o"));
                    _friendCurrentGpsEventId.Remove(e.UserId);
                }
            }
            _friendLastStatus[e.UserId] = newStatus;
        }

        var oldStatusDesc = _friendLastStatusDesc.GetValueOrDefault(e.UserId, "");
        if (oldStatusDesc != newStatusDesc && !string.IsNullOrEmpty(oldStatusDesc))
        {
            var fev = new TimelineService.FriendTimelineEvent
            {
                Type = "friend_statusdesc", FriendId = e.UserId, FriendName = fname,
                FriendImage = fimg, OldValue = oldStatusDesc, NewValue = newStatusDesc,
            };
            _core.Timeline.AddFriendEvent(fev);
            _core.SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev));
        }
        _friendLastStatusDesc[e.UserId] = newStatusDesc;

        var oldBio = _friendLastBio.GetValueOrDefault(e.UserId, "");
        if (!string.IsNullOrEmpty(newBio) && oldBio != newBio && !string.IsNullOrEmpty(oldBio))
        {
            var fev = new TimelineService.FriendTimelineEvent
            {
                Type = "friend_bio", FriendId = e.UserId, FriendName = fname,
                FriendImage = fimg,
                OldValue = oldBio.Length > 500 ? oldBio[..500] : oldBio,
                NewValue = newBio.Length > 500 ? newBio[..500] : newBio,
            };
            _core.Timeline.AddFriendEvent(fev);
            _core.SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev));
        }
        if (!string.IsNullOrEmpty(newBio))
            _friendLastBio[e.UserId] = newBio;

        // Avatar change detection
        var newFileId = ExtractAvatarFileId(e.User);
        if (!string.IsNullOrEmpty(newFileId))
        {
            var oldFileId = _friendLastAvatarFileId.GetValueOrDefault(e.UserId, "");
            if (!string.IsNullOrEmpty(oldFileId) && oldFileId != newFileId)
            {
                var capturedUserId = e.UserId;
                var capturedFname  = fname;
                var capturedFimg   = fimg;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (avtrId, avtrData) = await _core.Avatars.GetAvatarIdByFileIdAsync(newFileId);
                        if (string.IsNullOrEmpty(avtrId)) return;

                        var avtrName = avtrData?["name"]?.ToString() ?? "";
                        if (string.IsNullOrEmpty(avtrName)) return;

                        var avtrThumb = ImageCacheHelper.GetAvatarUrl(avtrId, avtrData?["imageUrl"]?.ToString() ?? "");

                        var fev = new TimelineService.FriendTimelineEvent
                        {
                            Type        = "friend_avatar",
                            FriendId    = capturedUserId,
                            FriendName  = capturedFname,
                            FriendImage = capturedFimg,
                            WorldId     = avtrId,
                            WorldName   = avtrName,
                            WorldThumb  = avtrThumb,
                            NewValue    = avtrName,
                        };
                        _core.Timeline.AddFriendEvent(fev);
                        _core.SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev));
                    }
                    catch { }
                });
            }
            _friendLastAvatarFileId[e.UserId] = newFileId;
        }
    }

    private void OnWsFriendAdded(object? sender, FriendEventArgs e)
    {
        if (string.IsNullOrEmpty(e.UserId) || !_friendStateSeeded) return;

        var fname = "";
        var fimg = "";
        if (e.User != null)
        {
            fname = e.User["displayName"]?.ToString() ?? "";
            fimg = VRChatApiService.GetUserImage(e.User);
            _friendNameImg[e.UserId] = (fname, fimg);
        }

        var fev = new TimelineService.FriendTimelineEvent
        {
            Type = "friend_added", FriendId = e.UserId, FriendName = fname, FriendImage = fimg,
        };
        _core.Timeline.AddFriendEvent(fev);
        _core.SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev));
    }

    private void OnWsFriendRemoved(object? sender, FriendEventArgs e)
    {
        if (string.IsNullOrEmpty(e.UserId) || !_friendStateSeeded) return;

        if (!IsInStore(e.UserId)) return;

        var (fname, fimg) = _friendNameImg.GetValueOrDefault(e.UserId, ("", ""));

        if (string.IsNullOrEmpty(fname) && _friendStore.TryGetValue(e.UserId, out var stored))
            fname = stored["displayName"]?.ToString() ?? e.UserId;

        if (string.IsNullOrEmpty(fname))
            fname = _core.Timeline.GetLastKnownFriendName(e.UserId);

        _friendStore.Remove(e.UserId);
        _friendLastLoc.Remove(e.UserId);
        _friendLastStatus.Remove(e.UserId);
        _friendLastStatusDesc.Remove(e.UserId);
        _friendLastBio.Remove(e.UserId);
        PushFriendsFromStore();

        var fev = new TimelineService.FriendTimelineEvent
        {
            Type = "friend_removed", FriendId = e.UserId, FriendName = fname, FriendImage = fimg,
        };
        _core.Timeline.AddFriendEvent(fev);
        _core.SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev));
    }

    // Friend Timeline Payload

    public object BuildFriendTimelinePayload(TimelineService.FriendTimelineEvent ev)
    {
        var isRecent = DateTime.TryParse(ev.Timestamp, out var evTs) && evTs >= DateTime.UtcNow - TimeSpan.FromDays(7);
        string wThumb;
        if (isRecent)
        {
            wThumb = ImageCacheHelper.GetWorldUrl(ev.WorldId, ev.WorldThumb);
        }
        else
        {
            var disk = ImageCacheHelper.GetWorldCached(ev.WorldId);
            wThumb = disk != null ? ImageCacheHelper.ToLocalUrl(disk) : ImageCacheHelper.NormalizeTo512(ev.WorldThumb ?? "");
        }
        var friendName = ev.FriendName;
        if (string.IsNullOrEmpty(friendName) && !string.IsNullOrEmpty(ev.FriendId))
            friendName = _core.Timeline.GetLastKnownFriendName(ev.FriendId);
        return new
        {
            id = ev.Id, type = ev.Type, timestamp = ev.Timestamp,
            friendId = ev.FriendId, friendName = friendName,
            friendImage = ResolveWithDiskFallback(ev.FriendId, ev.FriendImage),
            worldId = ev.WorldId, worldName = ev.WorldName,
            worldThumb = wThumb,
            location = ev.Location, oldValue = ev.OldValue, newValue = ev.NewValue,
            leftAt = string.IsNullOrEmpty(ev.LeftAt) ? null : ev.LeftAt,
            tracked = ev.Tracked,
        };
    }

    // Chat Storage

    private static string ChatFile(string userId) =>
        Path.Combine(_chatDir, $"chat_{userId}.json");

    public List<ChatEntry> GetChatHistory(string userId)
    {
        try
        {
            var file = ChatFile(userId);
            if (!File.Exists(file)) return [];
            var json = File.ReadAllText(file);
            return JsonConvert.DeserializeObject<List<ChatEntry>>(json) ?? [];
        }
        catch (Exception ex) { CrashHandler.WriteEntry("GetChatHistory", ex); return []; }
    }

    public ChatEntry StoreChatMessage(string userId, string from, string text, string? type = null, string? emoji = null)
    {
        var entry = new ChatEntry(Guid.NewGuid().ToString(), from, text, DateTime.UtcNow.ToString("o"), type, string.IsNullOrEmpty(emoji) ? null : emoji);
        try
        {
            Directory.CreateDirectory(_chatDir);
            var history = GetChatHistory(userId);
            history.Add(entry);
            if (history.Count > 500) history = history[^500..];
            File.WriteAllText(ChatFile(userId), JsonConvert.SerializeObject(history));
        }
        catch (Exception ex) { CrashHandler.WriteEntry("StoreChatMessage", ex); }
        return entry;
    }
}
