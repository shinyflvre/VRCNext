using Newtonsoft.Json.Linq;
using VRCNext.Services;

namespace VRCNext;

public partial class MainForm
{
    // Favorite Friends

    private async Task LoadFavoriteFriendsAsync()
    {
        try
        {
            var favs = await _vrcApi.GetFavoriteFriendsAsync();
            lock (_favoriteFriends)
            {
                _favoriteFriends.Clear();
                foreach (var fav in favs)
                {
                    var uid   = fav["favoriteId"]?.ToString() ?? "";
                    var fvrtId = fav["id"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(fvrtId))
                        _favoriteFriends[uid] = fvrtId;
                }
            }
            // Send the list to JS so the People Favorites tab can render
            var list = favs.Select(f => new
            {
                fvrtId     = f["id"]?.ToString() ?? "",
                favoriteId = f["favoriteId"]?.ToString() ?? "",
            }).Where(f => !string.IsNullOrEmpty(f.favoriteId)).ToList();
            Invoke(() => SendToJS("vrcFavoriteFriends", list));
        }
        catch { }
    }

    private void SendVrcUserData(JObject user, bool loginFlow = false)
    {
        _currentVrcUserId = user["id"]?.ToString() ?? "";

        // Start log watcher and load login-only data only on initial login/session-resume,
        // not on every status or profile update.
        if (loginFlow)
        {
            _logWatcher.Start();
            StartVrcPhotoWatcher();
            _ = LoadFavoriteFriendsAsync();
        }

        // Bootstrap once per app session: if VRChat was already running when the app started,
        // the catch-up read populated CurrentWorldId without firing WorldChanged.
        if (!_logWatcherBootstrapped)
        {
            _logWatcherBootstrapped = true;
            if (!string.IsNullOrEmpty(_logWatcher.CurrentWorldId) && _pendingInstanceEventId == null)
            {
                var loc = _logWatcher.CurrentLocation ?? _logWatcher.CurrentWorldId;
                // Avoid creating a duplicate if we already logged this exact instance on a previous run
                var lastJoin = _timeline.GetEvents().FirstOrDefault(e => e.Type == "instance_join");
                if (lastJoin != null && lastJoin.Location == loc)
                {
                    // Reuse the existing event so players continue to accumulate into it
                    _pendingInstanceEventId = lastJoin.Id;
                    // Pre-populate from previously persisted players so they aren't lost across restarts
                    if (lastJoin.Players != null)
                    {
                        foreach (var p in lastJoin.Players)
                        {
                            if (!string.IsNullOrEmpty(p.UserId))
                                _cumulativeInstancePlayers[p.UserId] = (p.DisplayName, p.Image ?? "");
                        }
                    }
                    // Resume world tracking without counting an extra visit (app restart in same world)
                    _worldTimeTracker.ResumeWorld(_logWatcher.CurrentWorldId);
                    _lastTrackedWorldId = _logWatcher.CurrentWorldId;
                }
                else
                {
                    HandleWorldChangedOnUiThread(_logWatcher.CurrentWorldId, loc);
                }
                // Populate cumulative players from the log watcher's already-parsed list
                foreach (var p in _logWatcher.GetCurrentPlayers())
                {
                    if (!string.IsNullOrEmpty(p.UserId) && !_cumulativeInstancePlayers.ContainsKey(p.UserId))
                        _cumulativeInstancePlayers[p.UserId] = (p.DisplayName, "");
                    // Also store name in UserTimeTracker for players already in instance on startup
                    if (!string.IsNullOrEmpty(p.UserId) && !string.IsNullOrEmpty(p.DisplayName))
                        _timeTracker.UpdateUserInfo(p.UserId, p.DisplayName, "");
                }
            }
        }

        // Keep own status in sync for Discord Presence
        var rawStatus = user["status"]?.ToString() ?? "";
        if (!string.IsNullOrEmpty(rawStatus)) _myVrcStatus = rawStatus;
        PushDiscordPresence();

        SendToJS("vrcUser", new
        {
            id = user["id"]?.ToString() ?? "",
            displayName = user["displayName"]?.ToString() ?? "",
            image = VRChatApiService.GetUserImage(user),
            status = user["status"]?.ToString() ?? "offline",
            statusDescription = user["statusDescription"]?.ToString() ?? "",
            currentAvatar = user["currentAvatar"]?.ToString() ?? "",
            bio = user["bio"]?.ToString() ?? "",
            pronouns = user["pronouns"]?.ToString() ?? "",
            bioLinks = user["bioLinks"]?.ToObject<List<string>>() ?? new List<string>(),
            tags = user["tags"]?.ToObject<List<string>>() ?? new List<string>(),
            profilePicOverride    = _imgCache?.Get(user["profilePicOverride"]?.ToString() ?? "") ?? user["profilePicOverride"]?.ToString() ?? "",
            currentAvatarImageUrl = _imgCache?.Get(user["currentAvatarImageUrl"]?.ToString() ?? "") ?? user["currentAvatarImageUrl"]?.ToString() ?? "",
        });

        // Fetch VRChat credit balance only on login — balance doesn't change on status/profile updates
        if (loginFlow)
        {
            _ = Task.Run(async () =>
            {
                var balance = await _vrcApi.GetBalanceAsync();
                if (balance >= 0)
                    Invoke(() => SendToJS("vrcCredits", new { balance }));
            });
        }
    }

    private async Task VrcRefreshFriendsAsync(bool silent = false)
    {
        if (!_vrcApi.IsLoggedIn) return;
        if (!await _friendsRefreshLock.WaitAsync(0)) return; // skip if already running
        try
        {
            var online = await _vrcApi.GetOnlineFriendsAsync();
            var offline = await _vrcApi.GetOfflineFriendsAsync();

            // Seed live friend store from authoritative REST data.
            // onlineIds dedup: some web-active friends appear in BOTH online and offline REST lists.
            // Without dedup the offline loop overwrites the correct online entry with location="offline".
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
                    copy["status"]   = "offline";
                    _friendStore[uid] = copy;
                }
            }

            // Track IDs already seen from online list to avoid duplicates
            var seenIds = new HashSet<string>();

            var onlineList = online.Select(f =>
            {
                var id = f["id"]?.ToString() ?? "";
                seenIds.Add(id);
                var location = f["location"]?.ToString() ?? "";
                var platform = f["platform"]?.ToString() ?? f["last_platform"]?.ToString() ?? "";
                // "private" / "traveling" = in-game with hidden or transitioning instance.
                // The only reliable web indicator is platform == "web".
                bool isWebPlatform = platform.Equals("web", StringComparison.OrdinalIgnoreCase);
                bool isInGame = !string.IsNullOrEmpty(location)
                    && location != "offline"
                    && location != ""
                    && !isWebPlatform;
                bool isWebActive = !isInGame;
                return new
                {
                    id,
                    displayName = f["displayName"]?.ToString() ?? "",
                    image = VRChatApiService.GetUserImage(f),
                    status = f["status"]?.ToString() ?? "offline",
                    statusDescription = f["statusDescription"]?.ToString() ?? "",
                    location = location,
                    platform = platform,
                    presence = isInGame ? "game" : "web",
                    tags = f["tags"]?.ToObject<List<string>>() ?? new(),
                };
            }).ToList();

            var offlineList = offline
                .Where(f => !seenIds.Contains(f["id"]?.ToString() ?? ""))
                .Select(f => new
                {
                    id = f["id"]?.ToString() ?? "",
                    displayName = f["displayName"]?.ToString() ?? "",
                    image = VRChatApiService.GetUserImage(f),
                    status = "offline",
                    statusDescription = f["statusDescription"]?.ToString() ?? "",
                    location = "offline",
                    platform = f["last_platform"]?.ToString() ?? "",
                    presence = "offline",
                    tags = f["tags"]?.ToObject<List<string>>() ?? new(),
                }).ToList();

            // Sort: in-game first (by status), then web-active (by status), then offline
            var friendList = onlineList
                .OrderBy(f => f.presence == "game" ? 0 : 1)
                .ThenBy(f => f.status switch
                {
                    "join me" => 0,
                    "active" => 1,
                    "ask me" => 2,
                    "busy" => 3,
                    _ => 4
                })
                .Cast<object>()
                .Concat(offlineList.OrderBy(f => f.displayName).Cast<object>())
                .ToList();

            var counts = new
            {
                game = onlineList.Count(f => f.presence == "game"),
                web = onlineList.Count(f => f.presence == "web"),
                offline = offlineList.Count
            };

            // Timeline: seed known users on first run so we don't get false first-meet alerts
            if (!_timeline.KnownUsersSeeded)
            {
                var allIds = online.Select(f => f["id"]?.ToString())
                    .Concat(offline.Select(f => f["id"]?.ToString()))
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Cast<string>()
                    .ToList();
                _timeline.SeedKnownUsers(allIds);
            }

            // Friends Timeline: seed per-friend state on first load to avoid false positives
            if (!_friendStateSeeded)
            {
                foreach (var f in online)
                {
                    var uid = f["id"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(uid)) continue;
                    _friendLastLoc[uid]        = f["location"]?.ToString() ?? "";
                    _friendLastStatus[uid]     = f["status"]?.ToString() ?? "";
                    _friendLastStatusDesc[uid] = (f["statusDescription"]?.ToString() ?? "").Trim();
                    _friendLastBio[uid]        = (f["bio"]?.ToString() ?? "").Trim();
                    _friendNameImg[uid]        = (f["displayName"]?.ToString() ?? "", VRChatApiService.GetUserImage(f));
                }
                foreach (var f in offline)
                {
                    var uid = f["id"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(uid)) continue;
                    _friendLastLoc[uid]        = "offline";
                    _friendLastStatus[uid]     = "offline";
                    _friendLastStatusDesc[uid] = (f["statusDescription"]?.ToString() ?? "").Trim();
                    _friendLastBio[uid]        = (f["bio"]?.ToString() ?? "").Trim();
                    _friendNameImg[uid]        = (f["displayName"]?.ToString() ?? "", VRChatApiService.GetUserImage(f));
                }
                _friendStateSeeded = true;
            }
            else
            {
                // On subsequent refreshes, update name/image cache for all friends
                foreach (var f in online.Concat(offline))
                {
                    var uid = f["id"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(uid)) continue;
                    var img = VRChatApiService.GetUserImage(f);
                    if (img.Length > 0)
                        _friendNameImg[uid] = (f["displayName"]?.ToString() ?? _friendNameImg.GetValueOrDefault(uid).name ?? "", img);
                }
            }

            if (_settings.FfcEnabled) _cache.Save(CacheHandler.KeyFriends, new { friends = friendList, counts });
            Invoke(() =>
            {
                SendToJS("vrcFriends", new { friends = friendList, counts });
                if (!silent)
                    SendToJS("log", new { msg = $"VRChat: {counts.game} in-game, {counts.web} web, {counts.offline} offline", color = "ok" });
            });

            // Proactively resolve world info for in-game friends without waiting for JS to ask.
            // On cache-hits this is instant; on misses it races with vrcResolveWorlds and the
            // first result populates _worldCache so the other is a no-op.
            var inGameWorldIds = online
                .Select(f => f["location"]?.ToString() ?? "")
                .Where(l => l.Contains(':'))
                .Select(l => l.Split(':')[0])
                .Where(id => id.StartsWith("wrld_"))
                .Distinct()
                .ToList();
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
                                var world = await _vrcApi.GetWorldAsync(wid);
                                if (world == null) return (wid, null as object);
                                return (wid, (object)new
                                {
                                    name              = world["name"]?.ToString() ?? "",
                                    thumbnailImageUrl = world["thumbnailImageUrl"]?.ToString() ?? "",
                                    imageUrl          = world["imageUrl"]?.ToString() ?? ""
                                });
                            }
                            catch { return (wid, null as object); }
                        });
                        var results = await Task.WhenAll(tasks);
                        var dict = results
                            .Where(r => r.Item2 != null)
                            .ToDictionary(r => r.wid, r => r.Item2!);
                        if (dict.Count > 0)
                            SendToJS("vrcWorldsResolved", dict);
                    }
                    catch { }
                });
            }

            // Time tracking: update my location and tick tracker
            try
            {
                // Use log-derived location only — no API fallback needed.
                // If VRChat is not running, don't track location regardless of cached log state.
                var myLoc = IsVrcRunning() ? _logWatcher.CurrentLocation : null;

                _timeTracker.SetMyLocation(myLoc ?? "");
                var trackData = onlineList.Select(f => (
                    userId: f.id,
                    location: f.location,
                    presence: f.presence
                )).ToList();

                if (!string.IsNullOrEmpty(myLoc) && myLoc != "offline" && myLoc != "private" && myLoc != "traveling")
                {
                    var logPlayers = _logWatcher.GetCurrentPlayers();
                    var logPlayerIds = new HashSet<string>(
                        logPlayers.Where(p => !string.IsNullOrEmpty(p.UserId)).Select(p => p.UserId));

                    // Fix: friends whose API location is "private" but confirmed in same instance via log
                    for (int i = 0; i < trackData.Count; i++)
                    {
                        var t = trackData[i];
                        if (t.location == "private" && logPlayerIds.Contains(t.userId))
                            trackData[i] = (t.userId, myLoc, t.presence);
                    }

                    // Track non-friends from LogWatcher
                    var trackedIds = new HashSet<string>(trackData.Select(t => t.userId));
                    foreach (var p in logPlayers)
                    {
                        if (!string.IsNullOrEmpty(p.UserId) && !trackedIds.Contains(p.UserId))
                            trackData.Add((userId: p.UserId, location: myLoc, presence: "game"));
                    }
                }

                _timeTracker.Tick(trackData);
                _timeTracker.Save();

                // World time tracking – visit detection is handled by the log watcher (HandleWorldChangedOnUiThread).
                // Here we only accumulate elapsed time for the current world.
                var (myWorldId, _, _) = VRChatApiService.ParseLocation(myLoc);
                if (!string.IsNullOrEmpty(myWorldId) && myWorldId.StartsWith("wrld_"))
                {
                    _worldTimeTracker.Tick();
                    _worldTimeTracker.Save();
                }
                else if (!string.IsNullOrEmpty(_lastTrackedWorldId))
                {
                    _lastTrackedWorldId = "";
                }
            }
            catch { /* don't break refresh if tracking fails */ }
        }
        catch (Exception ex)
        {
            if (!silent)
                Invoke(() => SendToJS("log", new { msg = $"VRChat: Friends error — {ex.Message}", color = "err" }));
        }
        finally
        {
            _friendsRefreshLock.Release();
        }
    }

    private async Task VrcUpdateStatusAsync(string status, string statusDescription)
    {
        if (!_vrcApi.IsLoggedIn) return;
        var user = await _vrcApi.UpdateStatusAsync(status, statusDescription);
        if (user != null)
        {
            SendVrcUserData(user);
            SendToJS("log", new { msg = $"VRChat: Status updated to {status}", color = "ok" });
        }
        else
        {
            SendToJS("log", new { msg = "VRChat: Failed to update status", color = "err" });
        }
    }

    private async Task EnrichModerationsWithImagesAsync(JArray entries)
    {
        var tasks = entries.OfType<JObject>().Select(async entry =>
        {
            var uid = entry["targetUserId"]?.ToString();
            if (string.IsNullOrEmpty(uid)) return;
            var user = await _vrcApi.GetUserAsync(uid);
            if (user != null)
                entry["image"] = VRChatApiService.GetUserImage(user);
        });
        await Task.WhenAll(tasks);
    }

    // ── Live Friend Store helpers ─────────────────────────────────────────────

    /// <summary>
    /// Merges a WebSocket user object (and optional location/platform) into the live friend store.
    /// Call from WS event handlers instead of triggering a REST friends refresh.
    /// </summary>
    private void MergeFriendStore(string userId, JObject? userObj,
                                   string? location = null, string? platform = null,
                                   bool wentOffline = false)
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
            if (location != null)  entry["location"]      = location;
            if (platform != null)  entry["last_platform"] = platform;
            if (wentOffline)
            {
                entry["location"] = "offline";
                entry["status"]   = "offline";
            }
        }
    }

    /// <summary>
    /// Builds the vrcFriends payload from the live store and pushes it to JS.
    /// Same shape as VrcRefreshFriendsAsync — no REST calls.
    /// </summary>
    private void PushFriendsFromStore()
    {
        List<JObject> snapshot;
        lock (_friendStore) snapshot = _friendStore.Values.ToList();

        var list = snapshot.Select(f =>
        {
            var location  = f["location"]?.ToString() ?? "";
            var platform  = f["last_platform"]?.ToString() ?? f["platform"]?.ToString() ?? "";
            bool isWebPlatform = platform.Equals("web", StringComparison.OrdinalIgnoreCase);
            bool isInGame = !string.IsNullOrEmpty(location)
                && location != "offline"
                && location != ""
                && !isWebPlatform;
            var status = f["status"]?.ToString() ?? "offline";
            var presence = (location == "offline" && status == "offline") ? "offline"
                         : isInGame ? "game" : "web";
            return new
            {
                id                = f["id"]?.ToString() ?? "",
                displayName       = f["displayName"]?.ToString() ?? "",
                image             = VRChatApiService.GetUserImage(f),
                status,
                statusDescription = f["statusDescription"]?.ToString() ?? "",
                location,
                platform,
                presence,
                tags              = f["tags"]?.ToObject<List<string>>() ?? new List<string>(),
            };
        })
        .OrderBy(f => f.presence switch { "game" => 0, "web" => 1, _ => 2 })
        .ThenBy(f => f.status switch
        {
            "join me" => 0, "active" => 1, "ask me" => 2, "busy" => 3, _ => 4
        })
        .ThenBy(f => f.displayName)
        .ToList();

        var counts = new
        {
            game    = list.Count(f => f.presence == "game"),
            web     = list.Count(f => f.presence == "web"),
            offline = list.Count(f => f.presence == "offline"),
        };

        Invoke(() => SendToJS("vrcFriends", new { friends = list, counts }));
    }

    // ─────────────────────────────────────────────────────────────────────────

    private async Task VrcGetFriendDetailAsync(string userId)
    {
        if (!_vrcApi.IsLoggedIn) return;

        // Disk cache → serve instantly, then refresh in background (deduplicated per userId)
        var diskCached = _settings.FfcEnabled ? _cache.LoadRaw(CacheHandler.KeyUserProfile(userId)) : null;
        if (diskCached is JObject diskProfile)
        {
            // Overlay live fields from the friend store (kept fresh by WebSocket).
            JObject? live;
            lock (_friendStore) _friendStore.TryGetValue(userId, out live);
            diskProfile["status"]              = live?["status"]?.ToString() ?? "offline";
            diskProfile["statusDescription"]   = live?["statusDescription"]?.ToString() ?? "";
            diskProfile["location"]            = live?["location"]?.ToString() ?? "";
            diskProfile["worldName"]           = "";
            diskProfile["worldThumb"]          = "";
            diskProfile["instanceType"]        = "";
            diskProfile["userCount"]           = 0;
            diskProfile["worldCapacity"]       = 0;
            diskProfile["canJoin"]             = false;
            diskProfile["canRequestInvite"]    = false;
            diskProfile["inSameInstance"]      = false;
            diskProfile["travelingToLocation"] = "";
            diskProfile["state"]               = "";
            SendToJS("vrcFriendDetail", diskProfile);

            // Background refresh — at most one per userId at a time
            bool startRefresh;
            lock (_profileRefreshInFlight) startRefresh = _profileRefreshInFlight.Add(userId);
            if (startRefresh)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var fresh = await BuildUserDetailPayloadAsync(userId, fetchNote: false);
                        if (fresh == null) return;
                        Invoke(() =>
                        {
                            if (_settings.FfcEnabled) _cache.Save(CacheHandler.KeyUserProfile(userId), fresh);
                            SendToJS("vrcFriendDetail", fresh);
                        });
                    }
                    catch { }
                    finally { lock (_profileRefreshInFlight) _profileRefreshInFlight.Remove(userId); }
                });
            }
            return;
        }

        // Cold fetch — no disk cache entry yet
        try
        {
            var payload = await BuildUserDetailPayloadAsync(userId);
            if (payload == null)
            {
                SendToJS("vrcFriendDetailError", new { error = "Could not load user profile" });
                return;
            }
            if (_settings.FfcEnabled) _cache.Save(CacheHandler.KeyUserProfile(userId), payload);
            SendToJS("vrcFriendDetail", payload);
        }
        catch (Exception ex)
        {
            SendToJS("vrcFriendDetailError", new { error = ex.Message });
            SendToJS("log", new { msg = $"VRChat: Error loading profile — {ex.Message}", color = "err" });
        }
    }

    private async Task<object?> BuildUserDetailPayloadAsync(string userId, bool fetchNote = true)
    {
        // Use live store data if available for location/status (kept fresh by WebSocket events).
        // Always fetch the full user object via REST when store data lacks badges, since the
        // friends-list endpoints (/auth/user/friends) do not include the badges array.
        JObject? user;
        lock (_friendStore) _friendStore.TryGetValue(userId, out user);
        if (user == null || user["badges"] == null)
        {
            var fresh = await _vrcApi.GetUserAsync(userId);
            if (fresh != null) user = fresh;
            else if (user == null) return null;
        }

        var location = user["location"]?.ToString() ?? "private";
        var (worldId, instanceId, instanceType) = VRChatApiService.ParseLocation(location);
        bool hasWorld = !string.IsNullOrEmpty(worldId) && worldId.StartsWith("wrld_");

        // Launch all secondary fetches in parallel after GetUser completes.
        // Instance response embeds world data (inst["world"]), so no separate GetWorld call needed.
        var instTask    = hasWorld ? _vrcApi.GetInstanceAsync(location) : Task.FromResult<JObject?>(null);
        var noteTask    = fetchNote ? _vrcApi.GetUserNoteAsync(userId) : Task.FromResult<JObject?>(null);
        var grpsTask    = _vrcApi.GetUserGroupsByIdAsync(userId);
        var worldsTask  = _vrcApi.GetUserWorldsAsync(userId);
        var mutualsTask = _vrcApi.GetUserMutualsAsync(userId);

        // Wait for all; ContinueWith swallows individual task exceptions
        await Task.WhenAll(new Task[] { instTask, noteTask, grpsTask, worldsTask, mutualsTask }
            .Select(t => t.ContinueWith(_ => { })));

        var inst     = instTask.IsCompletedSuccessfully    ? instTask.Result    : null;
        var noteObj  = noteTask.IsCompletedSuccessfully    ? noteTask.Result    : null;
        var groups   = grpsTask.IsCompletedSuccessfully    ? grpsTask.Result    : new JArray();
        var worlds   = worldsTask.IsCompletedSuccessfully  ? worldsTask.Result  : new JArray();
        var (mutualsArr, mutualsOptedOut) = mutualsTask.IsCompletedSuccessfully
            ? mutualsTask.Result : (new JArray(), false);
        // Badges come from the full user object via GET /users/{userId} (ensured above)
        var badgesArr = user["badges"] as JArray ?? new JArray();

        // Use instance API canRequestInvite field (authoritative) to distinguish Invite from Invite+
        if (instanceType == "private" && inst?["canRequestInvite"]?.Value<bool>() == true)
            instanceType = "invite_plus";

        // World data comes from inst["world"] — the instance endpoint embeds it
        var instWorld = inst?["world"] as JObject;
        string worldName     = instWorld?["name"]?.ToString() ?? "";
        string worldThumb    = _imgCache?.GetWorld(instWorld?["thumbnailImageUrl"]?.ToString()) ?? instWorld?["thumbnailImageUrl"]?.ToString() ?? "";
        int    worldCapacity = instWorld?["capacity"]?.Value<int>() ?? inst?["capacity"]?.Value<int>() ?? 0;
        int    userCount     = inst?["n_users"]?.Value<int>() ?? inst?["userCount"]?.Value<int>() ?? 0;
        string userNote      = noteObj?["note"]?.ToString() ?? "";

        bool canJoin = instanceType == "public" || instanceType == "friends" || instanceType == "friends+"
                    || instanceType == "hidden"
                    || instanceType == "group-public" || instanceType == "group-plus"
                    || instanceType == "group-members" || instanceType == "group";
        bool canRequestInvite = instanceType == "private" || instanceType == "invite_plus";
        bool isInWorld = !string.IsNullOrEmpty(worldId) && location != "private" && location != "offline" && location != "traveling";

        // Represented group is derived from the groups list (isRepresenting == true),
        // so no separate /groups/represented API call is needed.
        object? representedGroup = null;
        var repGroup = groups.OfType<JObject>().FirstOrDefault(g => g["isRepresenting"]?.Value<bool>() == true);
        if (repGroup != null && !string.IsNullOrEmpty(repGroup["groupId"]?.ToString() ?? repGroup["id"]?.ToString()))
        {
            representedGroup = new
            {
                id            = repGroup["groupId"]?.ToString() ?? repGroup["id"]?.ToString() ?? "",
                name          = repGroup["name"]?.ToString() ?? "",
                shortCode     = repGroup["shortCode"]?.ToString() ?? "",
                discriminator = repGroup["discriminator"]?.ToString() ?? "",
                iconUrl       = repGroup["iconUrl"]?.ToString() ?? "",
                bannerUrl     = repGroup["bannerUrl"]?.ToString() ?? "",
                memberCount   = repGroup["memberCount"]?.Value<int>() ?? 0,
            };
        }

        List<object> userGroups = new();
        foreach (var g in groups)
        {
            var gid = g["groupId"]?.ToString() ?? g["id"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(gid)) continue;
            userGroups.Add(new
            {
                id            = gid,
                name          = g["name"]?.ToString() ?? "",
                shortCode     = g["shortCode"]?.ToString() ?? "",
                discriminator = g["discriminator"]?.ToString() ?? "",
                iconUrl       = g["iconUrl"]?.ToString() ?? g["iconId"]?.ToString() ?? "",
                bannerUrl     = g["bannerUrl"]?.ToString() ?? "",
                memberCount   = g["memberCount"]?.Value<int>() ?? 0,
                isRepresenting = g["isRepresenting"]?.Value<bool>() ?? false,
            });
        }

        List<object> userWorlds = new();
        foreach (var w in worlds)
        {
            var wObj = w as JObject;
            if (wObj == null) continue;
            userWorlds.Add(new
            {
                id                = wObj["id"]?.ToString() ?? "",
                name              = wObj["name"]?.ToString() ?? "",
                thumbnailImageUrl = wObj["thumbnailImageUrl"]?.ToString() ?? "",
                occupants         = wObj["occupants"]?.Value<int>() ?? 0,
                favorites         = wObj["favorites"]?.Value<int>() ?? 0,
                visits            = wObj["visits"]?.Value<int>() ?? 0,
            });
        }

        List<object> mutualsList = new();
        foreach (var mu in mutualsArr)
        {
            var muObj = mu as JObject;
            if (muObj == null) continue;
            var muId       = muObj["id"]?.ToString() ?? "";
            var muImage    = (_friendNameImg.TryGetValue(muId, out var muFi) && !string.IsNullOrEmpty(muFi.image))
                                ? muFi.image
                                : VRChatApiService.GetUserImage(muObj);
            var muLocation = muObj["location"]?.ToString() ?? "";
            var muStatus   = muObj["status"]?.ToString() ?? "offline";
            bool muIsInGame  = !string.IsNullOrEmpty(muLocation)
                && muLocation != "offline" && muLocation != "private" && muLocation != "traveling";
            bool muIsOffline = muStatus == "offline" || muLocation == "offline";
            mutualsList.Add(new
            {
                id                = muObj["id"]?.ToString() ?? "",
                displayName       = muObj["displayName"]?.ToString() ?? "",
                image             = muImage,
                status            = muStatus,
                statusDescription = muObj["statusDescription"]?.ToString() ?? "",
                presence          = muIsOffline ? "offline" : muIsInGame ? "game" : "web",
            });
        }

        List<object> badges = new();
        foreach (var b in badgesArr)
        {
            var bObj = b as JObject;
            if (bObj == null) continue;
            var imageUrl = bObj["badgeImageUrl"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(imageUrl)) continue;
            badges.Add(new
            {
                id          = bObj["badgeId"]?.ToString() ?? "",
                name        = bObj["badgeName"]?.ToString() ?? "",
                description = bObj["badgeDescription"]?.ToString() ?? "",
                imageUrl,
                showcased   = bObj["showcased"]?.Value<bool>() ?? false,
            });
        }

        // Check via log watcher (reliable) rather than API location (often "private")
        var isCoPresent = _logWatcher.GetCurrentPlayers().Any(p => p.UserId == userId);
        var (totalSeconds, lastSeenLocal) = _timeTracker.GetUserStats(userId, isCoPresent);
        var lastLogin = user["last_login"]?.ToString() ?? "";

        return new
        {
            id = user["id"]?.ToString() ?? "",
            displayName = user["displayName"]?.ToString() ?? "",
            image = VRChatApiService.GetUserImage(user),
            status = user["status"]?.ToString() ?? "offline",
            statusDescription = user["statusDescription"]?.ToString() ?? "",
            bio = user["bio"]?.ToString() ?? "",
            lastLogin,
            dateJoined = user["date_joined"]?.ToString() ?? "",
            location,
            worldName,
            worldThumb,
            instanceType,
            userCount,
            worldCapacity,
            isFriend = user["isFriend"]?.Value<bool>() ?? !string.IsNullOrEmpty(user["friendKey"]?.ToString()),
            canJoin = isInWorld && canJoin,
            canRequestInvite = canRequestInvite,
            canInvite = true,
            currentAvatarImageUrl = _imgCache?.Get(user["currentAvatarImageUrl"]?.ToString() ?? "") ?? user["currentAvatarImageUrl"]?.ToString() ?? "",
            profilePicOverride    = _imgCache?.Get(user["profilePicOverride"]?.ToString() ?? "") ?? user["profilePicOverride"]?.ToString() ?? "",
            tags = user["tags"]?.ToObject<List<string>>() ?? new(),
            note = user["note"]?.ToString() ?? "",
            friendKey = user["friendKey"]?.ToString() ?? "",
            travelingToLocation = user["travelingToLocation"]?.ToString() ?? "",
            state = user["state"]?.ToString() ?? "",
            lastPlatform = user["last_platform"]?.ToString() ?? "",
            platform = user["platform"]?.ToString() ?? "",
            userNote,
            totalTimeSeconds = totalSeconds,
            inSameInstance = _logWatcher.GetCurrentPlayers().Any(p => p.UserId == userId),
            lastSeenTracked = lastSeenLocal,
            pronouns = user["pronouns"]?.ToString() ?? "",
            ageVerificationStatus = user["ageVerificationStatus"]?.ToString() ?? "",
            ageVerified = user["ageVerified"]?.Value<bool>() ?? false,
            representedGroup,
            userGroups,
            mutuals = mutualsList,
            mutualsOptedOut,
            userWorlds,
            bioLinks = user["bioLinks"]?.ToObject<List<string>>() ?? new List<string>(),
            isFavorited = _favoriteFriends.ContainsKey(userId),
            favFriendId = _favoriteFriends.GetValueOrDefault(userId, ""),
            badges,
        };
    }

    // Friends Timeline - WebSocket event handlers

    private void OnWsFriendLocation(object? sender, FriendEventArgs e)
    {
        if (string.IsNullOrEmpty(e.UserId) || !_friendStateSeeded) return;

        // Update live store and push to JS — no REST call needed
        MergeFriendStore(e.UserId, e.User, location: e.Location,
            platform: string.IsNullOrEmpty(e.Platform) ? null : e.Platform);
        PushFriendsFromStore();

        // Update name/image cache
        if (e.User != null)
            _friendNameImg[e.UserId] = (
                e.User["displayName"]?.ToString() ?? _friendNameImg.GetValueOrDefault(e.UserId).name ?? "",
                VRChatApiService.GetUserImage(e.User).Length > 0
                    ? VRChatApiService.GetUserImage(e.User)
                    : _friendNameImg.GetValueOrDefault(e.UserId).image ?? ""
            );

        var newLoc = e.Location;

        // Derive worldId exclusively from the location string itself.
        // VRChat can send friend-location with location="traveling" but worldId="wrld_xxx"
        // (destination announced before the user has actually loaded in).
        // Using e.WorldId would cause a GPS entry during traveling — we only want one
        // entry after the friend has truly arrived.
        var worldId = newLoc.Contains(':') ? newLoc.Split(':')[0] : newLoc;

        // Only log when the location string itself confirms a real world
        if (!worldId.StartsWith("wrld_")) { _friendLastLoc[e.UserId] = newLoc; return; }

        var oldLoc     = _friendLastLoc.GetValueOrDefault(e.UserId, "");
        var oldWorldId = oldLoc.Contains(':') ? oldLoc.Split(':')[0] : oldLoc;

        // Skip if no world change — VRChat fires a second friend-location event once
        // the instance is fully loaded (same wrld_ but with full instance params appended).
        if (oldLoc == newLoc || oldWorldId == worldId) { _friendLastLoc[e.UserId] = newLoc; return; }

        _friendLastLoc[e.UserId] = newLoc;

        var (fname, fimg) = _friendNameImg.GetValueOrDefault(e.UserId, ("", ""));
        var fev = new TimelineService.FriendTimelineEvent
        {
            Type        = "friend_gps",
            FriendId    = e.UserId,
            FriendName  = fname,
            FriendImage = fimg,
            WorldId     = worldId,
            Location    = newLoc,
        };
        _timeline.AddFriendEvent(fev);

        var fevPayload = BuildFriendTimelinePayload(fev);
        Invoke(() => SendToJS("friendTimelineEvent", fevPayload));

        // Async-resolve world name + thumb
        var evId = fev.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                var world = await _vrcApi.GetWorldAsync(worldId);
                if (world == null) return;
                var wname  = world["name"]?.ToString() ?? "";
                var wthumb = _imgCache?.GetWorld(world["thumbnailImageUrl"]?.ToString()) ?? world["thumbnailImageUrl"]?.ToString() ?? "";
                _timeline.UpdateFriendEventWorld(evId, wname, wthumb);
                var updated = _timeline.GetFriendEvents().FirstOrDefault(x => x.Id == evId);
                if (updated != null)
                    Invoke(() => SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(updated)));
            }
            catch { }
        });
    }

    private void OnWsFriendOffline(object? sender, FriendEventArgs e)
    {
        if (string.IsNullOrEmpty(e.UserId) || !_friendStateSeeded) return;

        // Mark offline in store and push — friend-offline has no user object
        MergeFriendStore(e.UserId, null, wentOffline: true);
        PushFriendsFromStore();

        var (fname, fimg) = _friendNameImg.GetValueOrDefault(e.UserId, ("", ""));
        if (e.User != null)
        {
            fname = e.User["displayName"]?.ToString() ?? fname;
            var img = VRChatApiService.GetUserImage(e.User);
            if (img.Length > 0) fimg = img;
            _friendNameImg[e.UserId] = (fname, fimg);
        }

        _friendLastLoc[e.UserId] = "offline";

        var fev = new TimelineService.FriendTimelineEvent
        {
            Type        = "friend_offline",
            FriendId    = e.UserId,
            FriendName  = fname,
            FriendImage = fimg,
        };
        _timeline.AddFriendEvent(fev);
        Invoke(() => SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev)));
    }

    private void OnWsFriendOnline(object? sender, FriendEventArgs e)
    {
        if (string.IsNullOrEmpty(e.UserId) || !_friendStateSeeded) return;

        // Update store with online user data and push — no REST call needed
        // Pass "" (not null) when no location: clears any previous "offline" location in the store
        MergeFriendStore(e.UserId, e.User,
            location: string.IsNullOrEmpty(e.Location) ? "" : e.Location,
            platform: string.IsNullOrEmpty(e.Platform) ? null : e.Platform);
        PushFriendsFromStore();

        var fname = "";
        var fimg  = "";
        if (e.User != null)
        {
            fname = e.User["displayName"]?.ToString() ?? "";
            fimg  = VRChatApiService.GetUserImage(e.User);
            _friendNameImg[e.UserId] = (fname, fimg);
        }
        else
        {
            (fname, fimg) = _friendNameImg.GetValueOrDefault(e.UserId, ("", ""));
        }

        // Guard: don't let an empty/non-world online event overwrite a world location
        // that a friend-location event may have already written.
        var onlineLoc = e.Location ?? "";
        var curLoc    = _friendLastLoc.GetValueOrDefault(e.UserId, "");
        _friendLastLoc[e.UserId] = (string.IsNullOrEmpty(onlineLoc) && curLoc.StartsWith("wrld_"))
            ? curLoc : onlineLoc;

        var fev = new TimelineService.FriendTimelineEvent
        {
            Type        = "friend_online",
            FriendId    = e.UserId,
            FriendName  = fname,
            FriendImage = fimg,
        };
        _timeline.AddFriendEvent(fev);
        Invoke(() => SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev)));
    }

    private void OnWsFriendUpdated(object? sender, FriendEventArgs e)
    {
        if (e.User == null || string.IsNullOrEmpty(e.UserId) || !_friendStateSeeded) return;

        // Update store with fresh user data and push — no REST call needed
        MergeFriendStore(e.UserId, e.User);
        PushFriendsFromStore();

        var fname  = e.User["displayName"]?.ToString() ?? _friendNameImg.GetValueOrDefault(e.UserId).name ?? "";
        var fimg   = VRChatApiService.GetUserImage(e.User);
        if (fimg.Length == 0) fimg = _friendNameImg.GetValueOrDefault(e.UserId).image ?? "";
        _friendNameImg[e.UserId] = (fname, fimg);

        var newStatus     = e.User["status"]?.ToString() ?? "";
        var newStatusDesc = (e.User["statusDescription"]?.ToString() ?? "").Trim();
        var newBio        = (e.User["bio"]?.ToString() ?? "").Trim();

        // Status change
        if (!string.IsNullOrEmpty(newStatus))
        {
            var oldStatus = _friendLastStatus.GetValueOrDefault(e.UserId, "");
            if (oldStatus != newStatus && !string.IsNullOrEmpty(oldStatus))
            {
                var fev = new TimelineService.FriendTimelineEvent
                {
                    Type        = "friend_status",
                    FriendId    = e.UserId,
                    FriendName  = fname,
                    FriendImage = fimg,
                    OldValue    = oldStatus,
                    NewValue    = newStatus,
                };
                _timeline.AddFriendEvent(fev);
                Invoke(() => SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev)));
            }
            _friendLastStatus[e.UserId] = newStatus;
        }

        // Status text change
        var oldStatusDesc = _friendLastStatusDesc.GetValueOrDefault(e.UserId, "");
        if (oldStatusDesc != newStatusDesc && !string.IsNullOrEmpty(oldStatusDesc))
        {
            var fev = new TimelineService.FriendTimelineEvent
            {
                Type        = "friend_statusdesc",
                FriendId    = e.UserId,
                FriendName  = fname,
                FriendImage = fimg,
                OldValue    = oldStatusDesc,
                NewValue    = newStatusDesc,
            };
            _timeline.AddFriendEvent(fev);
            Invoke(() => SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev)));
        }
        _friendLastStatusDesc[e.UserId] = newStatusDesc;

        // Bio change
        var oldBio = _friendLastBio.GetValueOrDefault(e.UserId, "");
        if (!string.IsNullOrEmpty(newBio) && oldBio != newBio && !string.IsNullOrEmpty(oldBio))
        {
            var fev = new TimelineService.FriendTimelineEvent
            {
                Type        = "friend_bio",
                FriendId    = e.UserId,
                FriendName  = fname,
                FriendImage = fimg,
                OldValue    = oldBio.Length > 500 ? oldBio[..500] : oldBio,
                NewValue    = newBio.Length > 500 ? newBio[..500] : newBio,
            };
            _timeline.AddFriendEvent(fev);
            Invoke(() => SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev)));
        }
        if (!string.IsNullOrEmpty(newBio))
            _friendLastBio[e.UserId] = newBio;
    }
}
