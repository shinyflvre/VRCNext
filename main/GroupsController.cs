using Newtonsoft.Json.Linq;
using VRCNext.Services;
using VRCNext.Services.Helpers;

namespace VRCNext;

// Owns all VRChat group-related message handling and the groups cache refresh.

public class GroupsController
{
    private readonly CoreLibrary _core;
    private readonly FriendsController _friends;
    private int _groupsInFlight = 0;
    private Dictionary<string, GroupMemberPerms> _memberPerms = new();
    private readonly HashSet<string> _deletedGroupIds = new();

    public void MarkDeleted(string groupId)
    {
        lock (_deletedGroupIds) _deletedGroupIds.Add(groupId);
    }

    private record GroupMemberPerms(
        bool CanPost, bool CanEvent, bool CanInvite, bool CanEdit,
        bool CanKick, bool CanBan, bool CanManageRoles, bool CanAssignRoles,
        bool CanViewAudit, string Visibility);

    // Newtonsoft turns date-time fields into JTokenType.Date, whose ToString() emits the
    // machine's locale format - which new Date() in JS cannot parse. Force ISO-8601.
    private static string? NetworkCacheFile(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-')) return null;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCNext", "Caches");
        return Path.Combine(dir, $"network_{name}.json");
    }

    private static string GroupIsoDate(JToken? t)
    {
        if (t == null) return "";
        if (t.Type == JTokenType.Date)
            return t.Value<DateTime>().ToUniversalTime().ToString("o");
        return t.ToString();
    }

    public GroupsController(CoreLibrary core, FriendsController friends)
    {
        _core = core;
        _friends = friends;
        ImageCacheHelper.OnImageRefreshed = (subdir, entityId) =>
        {
            if (subdir == "Groups") ScheduleGroupsRepush();
        };
    }

    private System.Threading.Timer? _repushTimer;

    private void ScheduleGroupsRepush()
    {
        _repushTimer?.Dispose();
        _repushTimer = new System.Threading.Timer(_ => { _ = FetchAndCacheAsync(); }, null, 1500, Timeout.Infinite);
    }

    // Represented group

    public async Task FetchRepresentedGroupAsync()
    {
        var g = await _core.Groups.GetRepresentedGroupAsync();
        if (g == null) { _core.SendToJS("vrcRepresentedGroup", (object?)null); return; }
        var gid = g["groupId"]?.ToString() ?? "";
        var iconUrl = ImageCacheHelper.GetGroupUrl(gid, g["iconUrl"]?.ToString());
        _core.SendToJS("vrcRepresentedGroup", new
        {
            id            = gid,
            name          = g["name"]?.ToString() ?? "",
            shortCode     = g["shortCode"]?.ToString() ?? "",
            discriminator = g["discriminator"]?.ToString() ?? "",
            iconUrl,
            memberCount   = g["memberCount"]?.Value<int>() ?? 0,
            isRepresenting = true,
        });
    }

    // Cache fetch

    public async Task FetchAndCacheAsync()
    {
        if (Interlocked.CompareExchange(ref _groupsInFlight, 1, 0) != 0) return; // already running
        try
        {
            var groupsTask = _core.Groups.GetUserGroupsAsync();
            var permsTask  = _core.Groups.GetUserGroupPermissionsAsync();
            await Task.WhenAll(groupsTask, permsTask);

            var groups   = groupsTask.Result;
            var allPerms = permsTask.Result;

            var enriched = new List<object>();
            var newPerms = new Dictionary<string, GroupMemberPerms>();

            foreach (var g in groups.Cast<JObject>())
            {
                var gid  = g["groupId"]?.ToString() ?? "";
                var name = g["name"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(gid) || string.IsNullOrEmpty(name)) continue;
                lock (_deletedGroupIds) { if (_deletedGroupIds.Contains(gid)) continue; }

                var perms = allPerms[gid]?.ToObject<List<string>>();

                var canCreate = perms == null
                    || perms.Contains("*")
                    || perms.Contains("group-instance-open-create")
                    || perms.Contains("group-instance-plus-create")
                    || perms.Contains("group-instance-public-create")
                    || perms.Contains("group-instance-restricted-create");

                var canPost        = perms != null && (perms.Contains("*") || perms.Contains("group-announcement-manage"));
                var canEvent       = perms != null && (perms.Contains("*") || perms.Contains("group-calendar-manage"));
                var canInvite      = perms != null && (perms.Contains("*") || perms.Contains("group-invites-manage"));
                var canEdit        = perms != null && (perms.Contains("*") || perms.Contains("group-data-manage"));
                var canKick        = perms != null && (perms.Contains("*") || perms.Contains("group-members-remove"));
                var canBan         = perms != null && (perms.Contains("*") || perms.Contains("group-bans-manage"));
                var canManageRoles = perms != null && (perms.Contains("*") || perms.Contains("group-roles-manage"));
                var canAssignRoles = perms != null && (perms.Contains("*") || perms.Contains("group-roles-manage") || perms.Contains("group-roles-assign"));
                var canViewAudit   = perms != null && (perms.Contains("*") || perms.Contains("group-audit-view"));
                var canModInstance = perms != null && (perms.Contains("*") || perms.Contains("group-instance-moderate") || perms.Contains("group-instance-manage"));
                var canManageMembers = perms != null && (perms.Contains("*") || perms.Contains("group-members-manage"));
                var vis            = g["memberVisibility"]?.ToString() ?? "visible";

                newPerms[gid] = new GroupMemberPerms(canPost, canEvent, canInvite, canEdit, canKick, canBan, canManageRoles, canAssignRoles, canViewAudit, vis);

                var gCached = _core.TimeEngine.GetGroupDetail(gid);
                enriched.Add(new {
                    id = gid,
                    name,
                    createdAt      = DateTimeHelper.Iso(gCached?.CreatedAt ?? ""),
                    joinedAt       = DateTimeHelper.Iso(gCached?.JoinedAt ?? ""),
                    shortCode      = g["shortCode"]?.ToString() ?? "",
                    discriminator  = g["discriminator"]?.ToString() ?? "",
                    description    = g["description"]?.ToString() ?? "",
                    iconUrl        = g["iconUrl"]?.ToString() ?? "",
                    bannerUrl      = g["bannerUrl"]?.ToString() ?? "",
                    memberCount    = g["memberCount"]?.Value<int>() ?? 0,
                    privacy        = g["privacy"]?.ToString() ?? "",
                    ownerId        = g["ownerId"]?.ToString() ?? "",
                    isRepresenting = g["isRepresenting"]?.Value<bool>() ?? false,
                    visibility     = vis,
                    canCreateInstance = canCreate,
                    canPost, canEvent, canInvite, canEdit, canKick, canBan, canManageRoles, canAssignRoles,
                    canViewAudit, canModInstance, canManageMembers,
                });
            }
            _memberPerms = newPerms;
            var enrichedForJs = enriched.Select(g => {
                var jo = JObject.FromObject(g);
                var gid = jo["id"]?.ToString();
                jo["iconUrl"]   = ImageCacheHelper.GetGroupUrl(gid, jo["iconUrl"]?.ToString());
                jo["bannerUrl"] = ImageCacheHelper.GetGroupBannerUrl(gid, jo["bannerUrl"]?.ToString());
                return (object)jo;
            }).ToList();
            _core.SendToJS("log", new { msg = $"[GROUPS] {enriched.Count} loaded", color = "sec" });
            _core.SendToJS("vrcMyGroups", enrichedForJs);
        }
        catch (Exception ex)
        {
            _core.SendToJS("log", new { msg = $"Groups load error: {ex.Message}", color = "err" });
        }
        finally { Interlocked.Exchange(ref _groupsInFlight, 0); }
    }

    // Message handler

    public async Task HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "vrcSearchGroups":
            {
                var gQ = msg["query"]?.ToString() ?? "";
                var gOff = msg["offset"]?.Value<int>() ?? 0;
                _ = Task.Run(async () =>
                {
                    var res = await _core.Groups.SearchGroupsAsync(gQ, 20, gOff);
                    var list = res.Cast<JObject>().Select(g => new {
                        id = g["id"]?.ToString() ?? "", name = g["name"]?.ToString() ?? "",
                        shortCode = g["shortCode"]?.ToString() ?? "", description = g["description"]?.ToString() ?? "",
                        iconUrl = ImageCacheHelper.GetGroupUrl(g["id"]?.ToString(), g["iconUrl"]?.ToString()), bannerUrl = ImageCacheHelper.GetGroupBannerUrl(g["id"]?.ToString(), g["bannerUrl"]?.ToString()),
                        memberCount = g["memberCount"]?.Value<int>() ?? 0, privacy = g["privacy"]?.ToString() ?? "",
                        createdAt = DateTimeHelper.Iso(g["createdAt"]), joinedAt = "",
                    }).ToList();
                    _core.SendToJS("vrcSearchResults", new { type = "groups", results = list, offset = gOff, hasMore = list.Count >= 20 });
                });
                break;
            }

            case "vrcGetDashGroupInstances":
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                    // Fetch group metadata (name/icon) and all instances in parallel — 2 calls total
                    var groupTask     = _core.Groups.GetUserGroupsAsync();
                    var instancesTask = _core.Instances.GetAllGroupInstancesAsync();
                    await Task.WhenAll(groupTask, instancesTask);

                    var groupMap = groupTask.Result.Cast<JObject>()
                        .Select(g => new {
                            gid  = g["groupId"]?.ToString() ?? g["id"]?.ToString() ?? "",
                            name = g["name"]?.ToString() ?? "",
                            icon = ImageCacheHelper.GetGroupUrl(g["groupId"]?.ToString() ?? g["id"]?.ToString(), g["iconUrl"]?.ToString()),
                        })
                        .Where(g => !string.IsNullOrEmpty(g.gid))
                        .GroupBy(g => g.gid).Select(grp => grp.First())
                        .ToDictionary(g => g.gid);

                    var combined = instancesTask.Result.Cast<JObject>()
                        .Select(i => {
                            var gid = i["ownerId"]?.ToString() ?? "";
                            groupMap.TryGetValue(gid, out var grp);
                            return new {
                                groupId   = gid,
                                groupName = grp?.name ?? "",
                                groupIcon = grp?.icon ?? "",
                                location  = i["location"]?.ToString() ?? "",
                                worldName = i["world"]?["name"]?.ToString() ?? "",
                                worldThumb = ImageCacheHelper.GetWorldUrl(
                                    i["world"]?["id"]?.ToString(),
                                    i["world"]?["imageUrl"]?.ToString() ?? i["world"]?["thumbnailImageUrl"]?.ToString()),
                                userCount = i["n_users"]?.Value<int>()
                                          ?? i["userCount"]?.Value<int>() ?? 0,
                                capacity  = i["capacity"]?.Value<int>()
                                          ?? i["world"]?["capacity"]?.Value<int>() ?? 0,
                            };
                        })
                        .Where(x => !string.IsNullOrEmpty(x.location))
                        .OrderByDescending(x => x.userCount)
                        .ToList();

                    _core.SendToJS("log", new { msg = $"[DASH-GRP-INST] {combined.Count} instances via single call", color = "sec" });
                    _core.SendToJS("vrcDashGroupInstances", combined);
                    }
                    catch (Exception ex)
                    {
                        CrashHandler.WriteEntry("vrcGetDashGroupInstances", ex);
                        _core.SendToJS("log", new { msg = "[DASH-GRP-INST] failed: " + ex.Message, color = "err" });
                        _core.SendToJS("vrcDashGroupInstances", new List<object>());
                    }
                });
                break;
            }

            case "vrcGetMyGroups":
            {
                if (msg["force"]?.Value<bool>() == true) ImageCacheHelper.ResetRevalidation("Groups");
                _ = Task.Run(FetchAndCacheAsync);
                break;
            }

            case "vrcGetRepresentedGroup":
            {
                _ = Task.Run(FetchRepresentedGroupAsync);
                break;
            }

            case "vrcGetGroup":
            {
                var ggId = msg["groupId"]?.ToString();
                if (!string.IsNullOrEmpty(ggId))
                {
                    var ggCached = _core.TimeEngine.GetGroupDetail(ggId);
                    _memberPerms.TryGetValue(ggId, out var gp);
                    if (ggCached != null)
                    {
                        var cachedPost  = string.IsNullOrEmpty(ggCached.LastPostJson)  ? null : Newtonsoft.Json.JsonConvert.DeserializeObject(ggCached.LastPostJson);
                        var cachedEvent = string.IsNullOrEmpty(ggCached.LastEventJson) ? null : Newtonsoft.Json.JsonConvert.DeserializeObject(ggCached.LastEventJson);
                        _core.SendToJS("vrcGroupDetail", new {
                            id = ggId, name = ggCached.Name, shortCode = ggCached.ShortCode,
                            description = ggCached.Description, iconUrl = ImageCacheHelper.GetGroupUrl(ggId, ggCached.IconUrl),
                            bannerUrl = ImageCacheHelper.GetGroupBannerUrl(ggId, ggCached.BannerUrl), memberCount = ggCached.MemberCount,
                            privacy = ggCached.Privacy, joinState = ggCached.JoinState,
                            createdAt = DateTimeHelper.Iso(ggCached.CreatedAt), isVerified = ggCached.IsVerified,
                            joinedAt = DateTimeHelper.Iso(ggCached.JoinedAt), isRepresenting = ggCached.IsRepresenting,
                            ownerId = ggCached.OwnerId, ownerDisplayName = ggCached.OwnerName,
                            visibility = gp?.Visibility ?? "", rules = ggCached.Rules,
                            languages = ggCached.Languages.ToArray(),
                            links = ggCached.Links.ToArray(),
                            isJoined = gp != null, canPost = gp?.CanPost ?? false, canEvent = gp?.CanEvent ?? false, canEdit = gp?.CanEdit ?? false,
                            canInvite = gp?.CanInvite ?? false, canKick = gp?.CanKick ?? false, canBan = gp?.CanBan ?? false,
                            canManageRoles = gp?.CanManageRoles ?? false, canAssignRoles = gp?.CanAssignRoles ?? false,
                            canViewAudit = gp?.CanViewAudit ?? false,
                            roles = Array.Empty<object>(),
                            posts = cachedPost != null ? new[] { cachedPost } : Array.Empty<object>(),
                            groupEvents = cachedEvent != null ? new[] { cachedEvent } : Array.Empty<object>(),
                            groupInstances = Array.Empty<object>(),
                            galleryImages = Array.Empty<object>(), groupMembers = Array.Empty<object>(),
                        });
                    }
                    _ = Task.Run(async () =>
                    {
                        var g = await _core.Groups.GetGroupAsync(ggId);
                        if (g != null)
                        {
                            // Save basic detail to DB immediately so future opens are instant
                            var saveId = g["id"]?.ToString() ?? "";
                            var earlyMember = g["myMember"] as JObject;
                            _core.TimeEngine.SaveGroupDetail(
                                saveId,
                                g["name"]?.ToString() ?? "",
                                g["shortCode"]?.ToString() ?? "",
                                g["description"]?.ToString() ?? "",
                                g["iconUrl"]?.ToString() ?? "",
                                g["bannerUrl"]?.ToString() ?? "",
                                g["memberCount"]?.Value<int>() ?? 0,
                                g["privacy"]?.ToString() ?? "",
                                g["joinState"]?.ToString() ?? "",
                                g["ownerId"]?.ToString() ?? "", "",
                                g["rules"]?.ToString() ?? "",
                                (g["languages"] as JArray)?.Select(x => x.ToString()).ToList() ?? new(),
                                (g["links"]     as JArray)?.Select(x => x.ToString()).ToList() ?? new(),
                                createdAt:      DateTimeHelper.Iso(g["createdAt"]),
                                isVerified:     g["isVerified"]?.Value<bool>() ?? false,
                                joinedAt:       DateTimeHelper.Iso(earlyMember?["joinedAt"]),
                                isRepresenting: earlyMember?["isRepresenting"]?.Value<bool>() ?? false);

                            bool isMember = g["myMember"] != null && g["myMember"]!.Type != JTokenType.Null;
                            // Fetch additional data in parallel
                            var postsTask = _core.Groups.GetGroupPostsAsync(ggId, publicOnly: !isMember);
                            var instancesTask = _core.Groups.GetGroupInstancesAsync(ggId);
                            var membersTask = _core.Groups.GetGroupMembersAsync(ggId);
                            var eventsTask = _core.Calendar.GetGroupEventsAsync(ggId);

                            await Task.WhenAll(postsTask, instancesTask, membersTask, eventsTask);

                            var posts = postsTask.Result;
                            var instances = instancesTask.Result;
                            var members = membersTask.Result;
                            var events = eventsTask.Result;

                            // Fetch gallery images for all galleries
                            var galleries = g["galleries"] as JArray ?? new JArray();
                            var galleryImages = new List<object>();
                            foreach (var gal in galleries)
                            {
                                var galId = gal["id"]?.ToString();
                                var galName = gal["name"]?.ToString() ?? "";
                                if (!string.IsNullOrEmpty(galId))
                                {
                                    var imgs = await _core.Groups.GetGroupGalleryImagesAsync(ggId, galId);
                                    foreach (var img in imgs)
                                    {
                                        galleryImages.Add(new {
                                            imageUrl = ImageCacheHelper.GetGroupUrl(img["id"]?.ToString(), img["imageUrl"]?.ToString()),
                                            galleryName = galName,
                                            createdAt = img["createdAt"]?.ToString() ?? "",
                                        });
                                    }
                                }
                            }

                            var myMember = g["myMember"] as JObject;
                            var myPerms = myMember?["permissions"] as JArray ?? new JArray();
                            var canPost   = myPerms.Any(p => p.ToString() == "*" || p.ToString() == "group-announcement-manage");
                            var canEvent  = myPerms.Any(p => p.ToString() == "*" || p.ToString() == "group-calendar-manage");
                            var canEdit   = myPerms.Any(p => p.ToString() == "*" || p.ToString() == "group-data-manage");
                            var canInvite = myPerms.Any(p => p.ToString() == "*" || p.ToString() == "group-invites-manage");
                            var canKick        = myPerms.Any(p => p.ToString() == "*" || p.ToString() == "group-members-remove");
                            var canBan         = myPerms.Any(p => p.ToString() == "*" || p.ToString() == "group-bans-manage");
                            var canManageRoles = myPerms.Any(p => p.ToString() == "*" || p.ToString() == "group-roles-manage");
                            var canAssignRoles = myPerms.Any(p => p.ToString() == "*" || p.ToString() == "group-roles-manage" || p.ToString() == "group-roles-assign");
                            var canViewAudit   = myPerms.Any(p => p.ToString() == "*" || p.ToString() == "group-audit-view");

                            var ownerId = g["ownerId"]?.ToString() ?? "";
                            var ownerMember = members.FirstOrDefault(m => m["userId"]?.ToString() == ownerId);
                            var ownerDisplayName = ownerMember?["user"]?["displayName"]?.ToString()
                                ?? ownerMember?["displayName"]?.ToString() ?? "";
                            if (string.IsNullOrEmpty(ownerDisplayName) && !string.IsNullOrEmpty(ownerId))
                            {
                                var ownerUser = await _core.Users.GetUserAsync(ownerId);
                                ownerDisplayName = ownerUser?["displayName"]?.ToString() ?? "";
                            }

                            var firstPost  = posts.Cast<JObject>().FirstOrDefault();
                            var firstEvent = events.Cast<JObject>().FirstOrDefault();
                            var lastPostJson = firstPost == null ? "" : Newtonsoft.Json.JsonConvert.SerializeObject(new {
                                id        = firstPost["id"]?.ToString() ?? "",
                                title     = firstPost["title"]?.ToString() ?? "",
                                text      = firstPost["text"]?.ToString() ?? "",
                                imageUrl  = ImageCacheHelper.GetGroupUrl(firstPost["id"]?.ToString(), firstPost["imageUrl"]?.ToString()),
                                createdAt = firstPost["createdAt"]?.ToString() ?? "",
                                visibility = firstPost["visibility"]?.ToString() ?? "",
                            });
                            var lastEventJson = firstEvent == null ? "" : Newtonsoft.Json.JsonConvert.SerializeObject(new {
                                id          = firstEvent["id"]?.ToString() ?? "",
                                title       = firstEvent["title"]?.ToString() ?? "",
                                description = firstEvent["description"]?.ToString() ?? "",
                                imageUrl    = ImageCacheHelper.GetEventUrl(firstEvent["id"]?.ToString(), firstEvent["imageUrl"]?.ToString()),
                                startsAt    = firstEvent["startsAt"]?.ToString() ?? "",
                                endsAt      = firstEvent["endsAt"]?.ToString() ?? "",
                                accessType  = firstEvent["accessType"]?.ToString() ?? "",
                            });
                            _core.TimeEngine.SaveGroupDetail(
                                g["id"]?.ToString() ?? "",
                                g["name"]?.ToString() ?? "",
                                g["shortCode"]?.ToString() ?? "",
                                g["description"]?.ToString() ?? "",
                                g["iconUrl"]?.ToString() ?? "",
                                g["bannerUrl"]?.ToString() ?? "",
                                g["memberCount"]?.Value<int>() ?? 0,
                                g["privacy"]?.ToString() ?? "",
                                g["joinState"]?.ToString() ?? "",
                                ownerId, ownerDisplayName,
                                g["rules"]?.ToString() ?? "",
                                (g["languages"] as JArray)?.Select(x => x.ToString()).ToList() ?? new(),
                                (g["links"]     as JArray)?.Select(x => x.ToString()).ToList() ?? new(),
                                createdAt:      DateTimeHelper.Iso(g["createdAt"]),
                                isVerified:     g["isVerified"]?.Value<bool>() ?? false,
                                joinedAt:       DateTimeHelper.Iso(myMember?["joinedAt"]),
                                isRepresenting: myMember?["isRepresenting"]?.Value<bool>() ?? false,
                                lastPostJson:   lastPostJson,
                                lastEventJson:  lastEventJson);
                            _core.SendToJS("vrcGroupDetail", new {
                                id = g["id"]?.ToString() ?? "", name = g["name"]?.ToString() ?? "",
                                shortCode = g["shortCode"]?.ToString() ?? "", description = g["description"]?.ToString() ?? "",
                                iconUrl = ImageCacheHelper.GetGroupUrl(g["id"]?.ToString(), g["iconUrl"]?.ToString(), authoritative: true), bannerUrl = ImageCacheHelper.GetGroupBannerUrl(g["id"]?.ToString(), g["bannerUrl"]?.ToString(), authoritative: true),
                                memberCount = g["memberCount"]?.Value<int>() ?? 0, onlineMemberCount = g["onlineMemberCount"]?.Value<int>() ?? 0, privacy = g["privacy"]?.ToString() ?? "",
                                joinState = g["joinState"]?.ToString() ?? "",
                                createdAt    = g["createdAt"]?.ToString() ?? "",
                                isVerified   = g["isVerified"]?.Value<bool>() ?? false,
                                joinedAt     = DateTimeHelper.Iso(myMember?["joinedAt"]),
                                isRepresenting = myMember?["isRepresenting"]?.Value<bool>() ?? false,
                                ownerId, ownerDisplayName,
                                visibility = myMember?["visibility"]?.ToString() ?? "",
                                rules = g["rules"]?.ToString() ?? "",
                                languages = (g["languages"] as JArray)?.Select(x => x.ToString()).ToArray() ?? Array.Empty<string>(),
                                links     = (g["links"]     as JArray)?.Select(x => x.ToString()).ToArray() ?? Array.Empty<string>(),
                                isJoined = g["myMember"] != null && g["myMember"].Type != JTokenType.Null,
                                canPost, canEvent, canEdit, canInvite, canKick, canBan, canManageRoles, canAssignRoles,
                                canViewAudit,
                                myRoleIds = (myMember?["roleIds"] as JArray)?.Select(x => x.ToString()).ToArray() ?? Array.Empty<string>(),
                                roles = (g["roles"] as JArray ?? new JArray()).Select(r => {
                                    var rPerms = (r["permissions"] as JArray)?.Select(p => p.ToString()).ToArray() ?? Array.Empty<string>();
                                    _core.SendToJS("log", new { msg = $"[ROLE] \"{r["name"]}\" perms: [{string.Join(", ", rPerms)}]", color = "sec" });
                                    return new {
                                        id              = r["id"]?.ToString() ?? "",
                                        name            = r["name"]?.ToString() ?? "",
                                        description     = r["description"]?.ToString() ?? "",
                                        permissions     = rPerms,
                                        isAddedOnJoin   = r["isAddedOnJoin"]?.Value<bool>() ?? false,
                                        isSelfAssignable  = r["isSelfAssignable"]?.Value<bool>() ?? false,
                                        requiresTwoFactor = r["requiresTwoFactor"]?.Value<bool>() ?? false,
                                        isManagementRole  = r["isManagementRole"]?.Value<bool>() ?? false,
                                    };
                                }),
                                posts = posts.Select(p => new {
                                    id = p["id"]?.ToString() ?? "",
                                    title = p["title"]?.ToString() ?? "",
                                    text = p["text"]?.ToString() ?? "",
                                    imageUrl = ImageCacheHelper.GetGroupUrl(p["id"]?.ToString(), p["imageUrl"]?.ToString()),
                                    createdAt = p["createdAt"]?.ToString() ?? "",
                                    authorId = p["authorId"]?.ToString() ?? "",
                                    visibility = p["visibility"]?.ToString() ?? "",
                                }),
                                groupEvents = events.Select(e => new {
                                    id = e["id"]?.ToString() ?? "",
                                    ownerId = e["ownerId"]?.ToString() ?? "",
                                    title = e["title"]?.ToString() ?? "",
                                    description = e["description"]?.ToString() ?? "",
                                    startsAt = e["startsAt"]?.ToString() ?? "",
                                    endsAt = e["endsAt"]?.ToString() ?? "",
                                    imageUrl = ImageCacheHelper.GetEventUrl(e["id"]?.ToString(), e["imageUrl"]?.ToString()),
                                    accessType = e["accessType"]?.ToString() ?? "",
                                }),
                                groupInstances = instances.Select(i => new {
                                    instanceId = i["instanceId"]?.ToString() ?? "",
                                    location = i["location"]?.ToString() ?? "",
                                    worldName = i["world"]?["name"]?.ToString() ?? "",
                                    worldThumb = ImageCacheHelper.GetWorldUrl(i["world"]?["id"]?.ToString(), i["world"]?["imageUrl"]?.ToString()),
                                    userCount = i["n_users"]?.Value<int>() ?? i["userCount"]?.Value<int>() ?? 0,
                                    capacity = i["capacity"]?.Value<int>() ?? i["world"]?["capacity"]?.Value<int>() ?? 0,
                                }),
                                galleryImages,
                                groupMembers = members.Select(m => new {
                                    id = m["userId"]?.ToString() ?? "",
                                    displayName = m["user"]?["displayName"]?.ToString() ?? m["displayName"]?.ToString() ?? "",
                                    image = m["user"] is JObject gmu
                                        ? ImageCacheHelper.GetUserUrl(m["userId"]?.ToString(), _friends.GetNameImage(m["userId"]?.ToString() ?? "").image is string fi && !string.IsNullOrEmpty(fi) ? fi : VRChatApiService.GetUserImage(gmu))
                                        : "",
                                    status = m["user"]?["status"]?.ToString() ?? "",
                                    statusDescription = m["user"]?["statusDescription"]?.ToString() ?? "",
                                    roleIds = (m["roleIds"] as JArray)?.Select(r => r.ToString()).ToArray() ?? Array.Empty<string>(),
                                    joinedAt = m["joinedAt"]?.ToString() ?? "",
                                    currentAvatarThumbnailImageUrl = m["user"]?["currentAvatarThumbnailImageUrl"]?.ToString() ?? "",
                                }),
                                rawJson = g,
                            });
                        }
                        else
                        {
                            _core.SendToJS("vrcGroupDetailError", new { error = $"Could not load group {ggId}" });
                        }
                    });
                }
                break;
            }

            case "vrcJoinGroup":
            {
                var jgId = msg["groupId"]?.ToString();
                if (!string.IsNullOrEmpty(jgId))
                {
                    _ = Task.Run(async () => {
                        var ok = await _core.Groups.JoinGroupAsync(jgId);
                        _core.SendToJS("vrcActionResult", new { action = "joinGroup", success = ok,
                            message = ok ? "Group join request sent!" : "Failed to join group" });
                    });
                }
                break;
            }

            case "vrcGetGroupMembers":
            {
                var gmId = msg["groupId"]?.ToString();
                var gmOffset = msg["offset"]?.Value<int>() ?? 0;
                if (!string.IsNullOrEmpty(gmId))
                {
                    _ = Task.Run(async () => {
                        var members = await _core.Groups.GetGroupMembersAsync(gmId, 50, gmOffset);
                        var list = members.Select(m => new {
                            id = m["userId"]?.ToString() ?? "",
                            displayName = m["user"]?["displayName"]?.ToString() ?? m["displayName"]?.ToString() ?? "",
                            image = m["user"] is JObject gmu2
                                ? ImageCacheHelper.GetUserUrl(m["userId"]?.ToString(), _friends.GetNameImage(m["userId"]?.ToString() ?? "").image is string fi2 && !string.IsNullOrEmpty(fi2) ? fi2 : VRChatApiService.GetUserImage(gmu2))
                                : "",
                            status = m["user"]?["status"]?.ToString() ?? "",
                            statusDescription = m["user"]?["statusDescription"]?.ToString() ?? "",
                            roleIds = (m["roleIds"] as JArray)?.Select(r => r.ToString()).ToArray() ?? Array.Empty<string>(),
                            joinedAt = m["joinedAt"]?.ToString() ?? "",
                            currentAvatarThumbnailImageUrl = m["user"]?["currentAvatarThumbnailImageUrl"]?.ToString() ?? "",
                        }).ToList();
                        _core.SendToJS("vrcGroupMembersPage", new {
                            groupId = gmId, offset = gmOffset, members = list,
                            hasMore = members.Count >= 50,
                        });
                    });
                }
                break;
            }

            case "vrcSearchGroupMembers":
            {
                var sgmId = msg["groupId"]?.ToString() ?? "";
                var sgmQuery = msg["query"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(sgmId) && !string.IsNullOrEmpty(sgmQuery))
                {
                    _ = Task.Run(async () => {
                        var members = await _core.Groups.SearchGroupMembersAsync(sgmId, sgmQuery);
                        var list = members.Select(m => new {
                            id = m["userId"]?.ToString() ?? "",
                            displayName = m["user"]?["displayName"]?.ToString() ?? m["displayName"]?.ToString() ?? "",
                            image = m["user"] is JObject sgmu
                                ? ImageCacheHelper.GetUserUrl(m["userId"]?.ToString(), _friends.GetNameImage(m["userId"]?.ToString() ?? "").image is string sfi && !string.IsNullOrEmpty(sfi) ? sfi : VRChatApiService.GetUserImage(sgmu))
                                : "",
                            status = m["user"]?["status"]?.ToString() ?? "",
                            statusDescription = m["user"]?["statusDescription"]?.ToString() ?? "",
                            roleIds = (m["roleIds"] as JArray)?.Select(r => r.ToString()).ToArray() ?? Array.Empty<string>(),
                            joinedAt = m["joinedAt"]?.ToString() ?? "",
                            currentAvatarThumbnailImageUrl = m["user"]?["currentAvatarThumbnailImageUrl"]?.ToString() ?? "",
                        }).ToList();
                        _core.SendToJS("vrcGroupSearchResults", new {
                            groupId = sgmId, query = sgmQuery, members = list,
                        });
                    });
                }
                break;
            }

            case "vrcGetGroupRoleMembers":
            {
                var grmGroupId = msg["groupId"]?.ToString() ?? "";
                var grmRoleId  = msg["roleId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(grmGroupId) && !string.IsNullOrEmpty(grmRoleId))
                    _ = Task.Run(async () => {
                        var members = await _core.Groups.GetGroupRoleMembersAsync(grmGroupId, grmRoleId);
                        var list = members.Select(m => new {
                            id = m["userId"]?.ToString() ?? "",
                            displayName = m["user"]?["displayName"]?.ToString() ?? m["displayName"]?.ToString() ?? "",
                            image = m["user"] is JObject ru
                                ? ImageCacheHelper.GetUserUrl(m["userId"]?.ToString(), VRChatApiService.GetUserImage(ru))
                                : "",
                            status = m["user"]?["status"]?.ToString() ?? "",
                            statusDescription = m["user"]?["statusDescription"]?.ToString() ?? "",
                            currentAvatarThumbnailImageUrl = m["user"]?["currentAvatarThumbnailImageUrl"]?.ToString() ?? "",
                            roleIds = (m["roleIds"] as JArray)?.Select(r => r.ToString()).ToArray() ?? Array.Empty<string>(),
                        }).ToList();
                        _core.SendToJS("vrcGroupRoleMembers", new { groupId = grmGroupId, roleId = grmRoleId, members = list });
                    });
                break;
            }

            case "vrcLeaveGroup":
            {
                var lgId = msg["groupId"]?.ToString();
                if (!string.IsNullOrEmpty(lgId))
                {
                    _ = Task.Run(async () => {
                        var ok = await _core.Groups.LeaveGroupAsync(lgId);
                        _core.SendToJS("vrcActionResult", new { action = "leaveGroup", success = ok,
                            message = ok ? "Left group" : "Failed to leave group" });
                    });
                }
                break;
            }

            case "vrcRepresentGroup":
            {
                var rgId = msg["groupId"]?.ToString();
                if (!string.IsNullOrEmpty(rgId))
                {
                    _ = Task.Run(async () => {
                        var ok = await _core.Groups.SetRepresentedGroupAsync(rgId);
                        _core.SendToJS("vrcActionResult", new { action = "representGroup", success = ok,
                            groupId = rgId,
                            message = ok ? "Now representing group" : "Failed to represent group" });
                    });
                }
                break;
            }

            case "vrcSetGroupVisibility":
            {
                var svGroupId  = msg["groupId"]?.ToString() ?? "";
                var svVis      = msg["visibility"]?.ToString() ?? "visible";
                var svUserId   = _core.VrcApi.CurrentUserId ?? "";
                if (!string.IsNullOrEmpty(svGroupId) && !string.IsNullOrEmpty(svUserId))
                {
                    _ = Task.Run(async () => {
                        var ok = await _core.Groups.SetGroupMemberVisibilityAsync(svGroupId, svUserId, svVis);
                        _core.SendToJS("groupVisibilityUpdated", new { groupId = svGroupId, visibility = svVis, success = ok });
                    });
                }
                break;
            }

            case "vrcCreateGroupPost":
            {
                var cpGroupId = msg["groupId"]?.ToString() ?? "";
                var cpTitle = msg["title"]?.ToString() ?? "";
                var cpText = msg["text"]?.ToString() ?? "";
                var cpVisibility = msg["visibility"]?.ToString() ?? "group";
                var cpNotify = msg["sendNotification"]?.Value<bool>() ?? false;
                var cpImageBase64 = msg["imageBase64"]?.ToString();
                var cpImageFileId = msg["imageFileId"]?.ToString();
                if (!string.IsNullOrEmpty(cpGroupId) && !string.IsNullOrEmpty(cpTitle))
                {
                    _ = Task.Run(async () =>
                    {
                        string? imageId = null;
                        if (!string.IsNullOrEmpty(cpImageFileId))
                        {
                            imageId = cpImageFileId;
                            _core.SendToJS("log", new { msg = $"[GroupPost] Using library image: {imageId}", color = "sec" });
                        }
                        else if (!string.IsNullOrEmpty(cpImageBase64))
                        {
                            try
                            {
                                var b64 = cpImageBase64;
                                string imgMime = "image/png";
                                string imgExt = ".png";
                                if (b64.StartsWith("data:"))
                                {
                                    var semi = b64.IndexOf(';');
                                    if (semi > 5) imgMime = b64[5..semi];
                                    imgExt = imgMime switch
                                    {
                                        "image/jpeg" => ".jpg",
                                        "image/gif"  => ".gif",
                                        "image/webp" => ".webp",
                                        _            => ".png"
                                    };
                                }
                                var commaIdx = b64.IndexOf(',');
                                if (commaIdx >= 0) b64 = b64[(commaIdx + 1)..];
                                var imgBytes = Convert.FromBase64String(b64);
                                _core.SendToJS("log", new { msg = $"[GroupPost] Uploading image {imgMime} {imgBytes.Length / 1024} KB", color = "sec" });
                                imageId = await _core.Files.UploadImageAsync(imgBytes, imgMime, imgExt);
                                if (imageId == null)
                                    _core.SendToJS("log", new { msg = "[GroupPost] Image upload failed, posting without image", color = "warn" });
                                else
                                    _core.SendToJS("log", new { msg = $"[GroupPost] Image uploaded: {imageId}", color = "sec" });
                            }
                            catch (Exception ex)
                            {
                                _core.SendToJS("log", new { msg = $"[GroupPost] Image parse error: {ex.Message}", color = "err" });
                            }
                        }
                        var ok = await _core.Groups.CreateGroupPostAsync(cpGroupId, cpTitle, cpText, cpVisibility, cpNotify, imageId);
                        _core.SendToJS("vrcActionResult", new
                        {
                            action = "createGroupPost",
                            success = ok,
                            message = ok ? "Post created!" : "Failed to create post"
                        });
                    });
                }
                break;
            }

            case "vrcCreateGroup":
            {
                var cgName     = msg["name"]?.ToString()?.Trim() ?? "";
                var cgShort    = msg["shortCode"]?.ToString()?.Trim() ?? "";
                var cgDesc     = msg["description"]?.ToString() ?? "";
                var cgJoin     = msg["joinState"]?.ToString() ?? "open";
                var cgPrivacy  = msg["privacy"]?.ToString() ?? "default";
                var cgTemplate = msg["roleTemplate"]?.ToString() ?? "default";
                var cgIconId   = msg["iconId"]?.ToString();
                var cgBannerId = msg["bannerId"]?.ToString();
                _ = Task.Run(async () =>
                {
                    if (!_core.VrcApi.HasVrcPlus)
                    {
                        _core.SendToJS("vrcGroupCreateResult", new { ok = false, error = "Creating groups requires a VRChat+ subscription.", vrcPlusRequired = true });
                        return;
                    }
                    var (ok, error, groupId) = await _core.Groups.CreateGroupAsync(cgName, cgShort, cgDesc, cgJoin, cgPrivacy, cgTemplate, cgIconId, cgBannerId);
                    if (ok) _ = FetchAndCacheAsync();
                    _core.SendToJS("vrcGroupCreateResult", new { ok, error, groupId });
                });
                break;
            }

            case "vrcUpdateGroupPost":
            {
                var ugpGroupId    = msg["groupId"]?.ToString() ?? "";
                var ugpPostId     = msg["postId"]?.ToString() ?? "";
                var ugpTitle      = msg["title"]?.ToString() ?? "";
                var ugpText       = msg["text"]?.ToString() ?? "";
                var ugpVis        = msg["visibility"]?.ToString() ?? "group";
                var ugpImageBase64 = msg["imageBase64"]?.ToString();
                var ugpImageFileId = msg["imageFileId"]?.ToString();
                if (!string.IsNullOrEmpty(ugpGroupId) && !string.IsNullOrEmpty(ugpPostId) && !string.IsNullOrEmpty(ugpTitle))
                {
                    _ = Task.Run(async () =>
                    {
                        string? imageId = null;
                        if (!string.IsNullOrEmpty(ugpImageFileId))
                        {
                            imageId = ugpImageFileId;
                        }
                        else if (!string.IsNullOrEmpty(ugpImageBase64))
                        {
                            try
                            {
                                var b64 = ugpImageBase64;
                                string imgMime = "image/png";
                                string imgExt = ".png";
                                if (b64.StartsWith("data:"))
                                {
                                    var semi = b64.IndexOf(';');
                                    if (semi > 5) imgMime = b64[5..semi];
                                    imgExt = imgMime switch { "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/webp" => ".webp", _ => ".png" };
                                }
                                var commaIdx = b64.IndexOf(',');
                                if (commaIdx >= 0) b64 = b64[(commaIdx + 1)..];
                                imageId = await _core.Files.UploadImageAsync(Convert.FromBase64String(b64), imgMime, imgExt);
                            }
                            catch (Exception ex) { _core.SendToJS("log", new { msg = $"[UpdateGroupPost] Image parse error: {ex.Message}", color = "err" }); }
                        }
                        var ok = await _core.Groups.UpdateGroupPostAsync(ugpGroupId, ugpPostId, ugpTitle, ugpText, ugpVis, imageId);
                        _core.SendToJS("vrcActionResult", new
                        {
                            action = "updateGroupPost",
                            success = ok,
                            postId = ugpPostId,
                            message = ok ? "Post updated!" : "Failed to update post"
                        });
                    });
                }
                break;
            }

            case "vrcDeleteGroupPost":
            {
                var dgpGroupId = msg["groupId"]?.ToString() ?? "";
                var dgpPostId  = msg["postId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(dgpGroupId) && !string.IsNullOrEmpty(dgpPostId))
                {
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.Groups.DeleteGroupPostAsync(dgpGroupId, dgpPostId);
                        _core.SendToJS("vrcActionResult", new { action = "deleteGroupPost", success = ok, postId = dgpPostId });
                    });
                }
                break;
            }

            case "vrcDeleteGroupEvent":
            {
                var dgeGroupId  = msg["groupId"]?.ToString() ?? "";
                var dgeEventId  = msg["eventId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(dgeGroupId) && !string.IsNullOrEmpty(dgeEventId))
                {
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.Calendar.DeleteGroupEventAsync(dgeGroupId, dgeEventId);
                        _core.SendToJS("vrcActionResult", new { action = "deleteGroupEvent", success = ok, eventId = dgeEventId });
                    });
                }
                break;
            }

            case "vrcUpdateGroup":
            {
                var ugGroupId   = msg["groupId"]?.ToString() ?? "";
                var ugDesc      = msg["description"] != null ? msg["description"]!.ToString() : (string?)null;
                var ugRules     = msg["rules"]       != null ? msg["rules"]!.ToString()       : (string?)null;
                var ugLanguages = msg["languages"]?.ToObject<List<string>>();
                var ugLinks     = msg["links"]?.ToObject<List<string>>();
                var ugIconId    = msg["iconId"]    != null ? msg["iconId"]!.ToString()    : (string?)null;
                var ugBannerId  = msg["bannerId"]  != null ? msg["bannerId"]!.ToString()  : (string?)null;
                var ugJoinState = msg["joinState"] != null ? msg["joinState"]!.ToString() : (string?)null;
                if (!string.IsNullOrEmpty(ugGroupId))
                {
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.Groups.UpdateGroupAsync(ugGroupId, ugDesc, ugRules, ugLanguages, ugLinks, ugIconId, ugBannerId, ugJoinState);
                        _core.SendToJS("vrcGroupUpdated", new {
                            success = ok, groupId = ugGroupId,
                            description = ugDesc, rules = ugRules,
                            languages = ugLanguages, links = ugLinks,
                            iconId = ugIconId, bannerId = ugBannerId,
                            joinState = ugJoinState
                        });
                    });
                }
                break;
            }

            case "vrcKickGroupMember":
            {
                var kmGroupId = msg["groupId"]?.ToString() ?? "";
                var kmUserId  = msg["userId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(kmGroupId) && !string.IsNullOrEmpty(kmUserId))
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.Groups.KickGroupMemberAsync(kmGroupId, kmUserId);
                        _core.SendToJS("vrcActionResult", new { action = "kickGroupMember", success = ok, message = ok ? "Member kicked." : "Kick failed." });
                    });
                break;
            }

            case "vrcBanGroupMember":
            {
                var bmGroupId = msg["groupId"]?.ToString() ?? "";
                var bmUserId  = msg["userId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(bmGroupId) && !string.IsNullOrEmpty(bmUserId))
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.Groups.BanGroupMemberAsync(bmGroupId, bmUserId);
                        _core.SendToJS("vrcActionResult", new { action = "banGroupMember", success = ok, message = ok ? "Member banned." : "Ban failed." });
                    });
                break;
            }

            case "vrcGetGroupLogs":
            {
                var glId     = msg["groupId"]?.ToString() ?? "";
                var glOffset = msg["offset"]?.Value<int>() ?? 0;
                var glTypes  = msg["eventTypes"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(glId))
                    _ = Task.Run(async () => {
                        const int pageSize = 50;
                        var page = await _core.Groups.GetGroupAuditLogsAsync(glId, pageSize, glOffset, glTypes);
                        if (page == null)
                        {
                            _core.SendToJS("vrcGroupLogs", new { groupId = glId, offset = glOffset, logs = new object[0], hasNext = false, error = true });
                            return;
                        }

                        var rows = (page["results"] as JArray) ?? new JArray();
                        var list = rows.OfType<JObject>().Select(r => {
                            var actorId = r["actorId"]?.ToString() ?? "";
                            return new {
                                id          = r["id"]?.ToString() ?? "",
                                created_at  = GroupIsoDate(r["created_at"]),
                                eventType   = r["eventType"]?.ToString() ?? "",
                                description = r["description"]?.ToString() ?? "",
                                actorId,
                                actorDisplayName = r["actorDisplayName"]?.ToString() ?? "",
                                actorImage  = ImageCacheHelper.GetUserUrl(actorId, _friends.GetNameImage(actorId).image),
                                targetId    = r["targetId"]?.ToString() ?? "",
                                data        = r["data"],
                            };
                        }).ToList();

                        var total   = page["totalCount"]?.Value<int>() ?? 0;
                        var hasNext = rows.Count >= pageSize && (total == 0 || glOffset + rows.Count < total);

                        _core.SendToJS("vrcGroupLogs", new { groupId = glId, offset = glOffset, logs = list, hasNext, totalCount = total });
                    });
                break;
            }

            case "vrcGetGroupBans":
            {
                var gbId = msg["groupId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(gbId))
                    _ = Task.Run(async () => {
                        var bans = await _core.Groups.GetGroupBansAsync(gbId);
                        var list = bans.Select(b => new {
                            id          = b["userId"]?.ToString() ?? "",
                            displayName = b["user"]?["displayName"]?.ToString() ?? b["displayName"]?.ToString() ?? "",
                            image       = ImageCacheHelper.GetUserUrl(b["userId"]?.ToString(), _friends.GetNameImage(b["userId"]?.ToString() ?? "").image is string bfi && !string.IsNullOrEmpty(bfi) ? bfi : (b["user"] is JObject gu ? VRChatApiService.GetUserImage(gu) : "")),
                            bannedAt    = b["bannedAt"]?.ToString() ?? b["createdAt"]?.ToString() ?? "",
                        }).ToList();
                        _core.SendToJS("vrcGroupBans", new { groupId = gbId, bans = list });
                    });
                break;
            }

            case "vrcUnbanGroupMember":
            {
                var ubGroupId = msg["groupId"]?.ToString() ?? "";
                var ubUserId  = msg["userId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(ubGroupId) && !string.IsNullOrEmpty(ubUserId))
                    _ = Task.Run(async () => {
                        var ok = await _core.Groups.UnbanGroupMemberAsync(ubGroupId, ubUserId);
                        _core.SendToJS("vrcActionResult", new { action = "unbanGroupMember", success = ok, userId = ubUserId, message = ok ? "Member unbanned." : "Unban failed." });
                    });
                break;
            }

            case "vrcCreateGroupRole":
            {
                var crGroupId = msg["groupId"]?.ToString() ?? "";
                var crName    = msg["name"]?.ToString() ?? "";
                var crDesc    = msg["description"]?.ToString() ?? "";
                var crPerms   = msg["permissions"]?.ToObject<List<string>>() ?? new List<string>();
                var crJoin    = msg["isAddedOnJoin"]?.Value<bool>() ?? false;
                var crSelf    = msg["isSelfAssignable"]?.Value<bool>() ?? false;
                var crTfa     = msg["requiresTwoFactor"]?.Value<bool>() ?? false;
                if (!string.IsNullOrEmpty(crGroupId) && !string.IsNullOrEmpty(crName))
                    _ = Task.Run(async () => {
                        var role = await _core.Groups.CreateGroupRoleAsync(crGroupId, crName, crDesc, crPerms, crJoin, crSelf, crTfa);
                        var ok = role != null;
                        object? roleData = ok ? (object)new {
                            id              = role!["id"]?.ToString() ?? "",
                            name            = role["name"]?.ToString() ?? "",
                            description     = role["description"]?.ToString() ?? "",
                            permissions     = (role["permissions"] as JArray)?.Select(p => p.ToString()).ToArray() ?? Array.Empty<string>(),
                            isAddedOnJoin   = role["isAddedOnJoin"]?.Value<bool>() ?? false,
                            isSelfAssignable  = role["isSelfAssignable"]?.Value<bool>() ?? false,
                            requiresTwoFactor = role["requiresTwoFactor"]?.Value<bool>() ?? false,
                            isManagementRole  = role["isManagementRole"]?.Value<bool>() ?? false,
                        } : null;
                        _core.SendToJS("vrcGroupRoleResult", new { action = "create", success = ok, groupId = crGroupId, role = roleData });
                    });
                break;
            }

            case "vrcUpdateGroupRole":
            {
                var urGroupId = msg["groupId"]?.ToString() ?? "";
                var urRoleId  = msg["roleId"]?.ToString() ?? "";
                var urName    = msg["name"]        != null ? msg["name"]!.ToString()        : (string?)null;
                var urDesc    = msg["description"] != null ? msg["description"]!.ToString() : (string?)null;
                var urPerms   = msg["permissions"]?.ToObject<List<string>>();
                var urJoin    = msg["isAddedOnJoin"]    != null ? (bool?)msg["isAddedOnJoin"]!.Value<bool>()    : null;
                var urSelf    = msg["isSelfAssignable"] != null ? (bool?)msg["isSelfAssignable"]!.Value<bool>() : null;
                var urTfa     = msg["requiresTwoFactor"]!= null ? (bool?)msg["requiresTwoFactor"]!.Value<bool>(): null;
                if (!string.IsNullOrEmpty(urGroupId) && !string.IsNullOrEmpty(urRoleId))
                    _ = Task.Run(async () => {
                        var ok = await _core.Groups.UpdateGroupRoleAsync(urGroupId, urRoleId, urName, urDesc, urPerms, urJoin, urSelf, urTfa);
                        _core.SendToJS("vrcGroupRoleResult", new { action = "update", success = ok, groupId = urGroupId, roleId = urRoleId });
                    });
                break;
            }

            case "vrcDeleteGroupRole":
            {
                var drGroupId = msg["groupId"]?.ToString() ?? "";
                var drRoleId  = msg["roleId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(drGroupId) && !string.IsNullOrEmpty(drRoleId))
                    _ = Task.Run(async () => {
                        var ok = await _core.Groups.DeleteGroupRoleAsync(drGroupId, drRoleId);
                        _core.SendToJS("vrcGroupRoleResult", new { action = "delete", success = ok, groupId = drGroupId, roleId = drRoleId });
                    });
                break;
            }

            case "vrcAddGroupMemberRole":
            {
                var amrGroupId = msg["groupId"]?.ToString() ?? "";
                var amrUserId  = msg["userId"]?.ToString() ?? "";
                var amrRoleId  = msg["roleId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(amrGroupId) && !string.IsNullOrEmpty(amrUserId) && !string.IsNullOrEmpty(amrRoleId))
                    _ = Task.Run(async () => {
                        var ok = await _core.Groups.AddGroupMemberRoleAsync(amrGroupId, amrUserId, amrRoleId);
                        _core.SendToJS("vrcActionResult", new { action = "addGroupMemberRole", success = ok, userId = amrUserId, roleId = amrRoleId, message = ok ? "Role assigned." : "Failed to assign role." });
                    });
                break;
            }

            case "vrcRemoveGroupMemberRole":
            {
                var rmrGroupId = msg["groupId"]?.ToString() ?? "";
                var rmrUserId  = msg["userId"]?.ToString() ?? "";
                var rmrRoleId  = msg["roleId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(rmrGroupId) && !string.IsNullOrEmpty(rmrUserId) && !string.IsNullOrEmpty(rmrRoleId))
                    _ = Task.Run(async () => {
                        var ok = await _core.Groups.RemoveGroupMemberRoleAsync(rmrGroupId, rmrUserId, rmrRoleId);
                        _core.SendToJS("vrcActionResult", new { action = "removeGroupMemberRole", success = ok, userId = rmrUserId, roleId = rmrRoleId, message = ok ? "Role removed." : "Failed to remove role." });
                    });
                break;
            }

            case "vrcCreateGroupEvent":
            {
                var ceGroupId   = msg["groupId"]?.ToString() ?? "";
                var ceTitle     = msg["title"]?.ToString() ?? "";
                var ceDesc      = msg["description"]?.ToString() ?? "";
                var ceStartsAt  = msg["startsAt"]?.ToString() ?? "";
                var ceEndsAt    = msg["endsAt"]?.ToString() ?? "";
                var ceCategory  = msg["category"]?.ToString() ?? "other";
                var ceAccess    = msg["accessType"]?.ToString() ?? "group";
                var ceNotify    = msg["sendCreationNotification"]?.Value<bool>() ?? false;
                var ceImageB64  = msg["imageBase64"]?.ToString();
                var ceImageFileId = msg["imageFileId"]?.ToString();
                if (!string.IsNullOrEmpty(ceGroupId) && !string.IsNullOrEmpty(ceTitle) && !string.IsNullOrEmpty(ceStartsAt))
                {
                    _ = Task.Run(async () =>
                    {
                        string? imageId = null;
                        if (!string.IsNullOrEmpty(ceImageFileId))
                        {
                            imageId = ceImageFileId;
                        }
                        else if (!string.IsNullOrEmpty(ceImageB64))
                        {
                            try
                            {
                                var b64 = ceImageB64;
                                string imgMime = "image/png", imgExt = ".png";
                                if (b64.StartsWith("data:"))
                                {
                                    var semi = b64.IndexOf(';');
                                    if (semi > 5) imgMime = b64[5..semi];
                                    imgExt = imgMime switch { "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/webp" => ".webp", _ => ".png" };
                                }
                                var commaIdx = b64.IndexOf(',');
                                if (commaIdx >= 0) b64 = b64[(commaIdx + 1)..];
                                var imgBytes = Convert.FromBase64String(b64);
                                _core.SendToJS("log", new { msg = $"[GroupEvent] Uploading image {imgMime} {imgBytes.Length / 1024} KB", color = "sec" });
                                imageId = await _core.Files.UploadImageAsync(imgBytes, imgMime, imgExt);
                                if (imageId == null)
                                    _core.SendToJS("log", new { msg = "[GroupEvent] Image upload failed, creating event without image", color = "warn" });
                            }
                            catch (Exception ex) { _core.SendToJS("log", new { msg = $"[GroupEvent] Image error: {ex.Message}", color = "err" }); }
                        }
                        var result = await _core.Calendar.CreateGroupEventAsync(ceGroupId, ceTitle, ceDesc, ceStartsAt, ceEndsAt, ceCategory, ceAccess, ceNotify, imageId);
                        var ok = result != null;
                        _core.SendToJS("vrcActionResult", new
                        {
                            action = "createGroupEvent",
                            success = ok,
                            message = ok ? "Event created!" : "Failed to create event"
                        });
                    });
                }
                break;
            }

            case "vrcGetMutualsForNetwork":
            {
                var mnUid = msg["userId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(mnUid))
                {
                    _ = Task.Run(async () =>
                    {
                        var (arr, optedOut) = await _core.Users.GetUserMutualsAsync(mnUid);
                        var ids = optedOut ? Array.Empty<string>()
                                           : arr.Select(m => m["id"]?.ToString() ?? "").Where(s => s != "").ToArray();
                        _core.SendToJS("vrcMutualsForNetwork", new { userId = mnUid, mutualIds = ids, optedOut });
                    });
                }
                break;
            }

            case "vrcGetGroupsForNetwork":
            {
                var gnUid = msg["userId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(gnUid))
                {
                    _ = Task.Run(async () =>
                    {
                        var arr = await _core.Users.GetUserGroupsByIdAsync(gnUid);
                        var groups = arr.Select(g => new
                        {
                            id      = g["groupId"]?.ToString() ?? g["id"]?.ToString() ?? "",
                            members = g["memberCount"]?.Value<int?>() ?? 0,
                        }).Where(g => !string.IsNullOrEmpty(g.id)).ToArray();
                        _core.SendToJS("vrcGroupsForNetwork", new { userId = gnUid, groups });
                    });
                }
                break;
            }

            case "vrcGetNetworkSessions":
            {
                var nsIds = msg["ids"]?.ToObject<List<string>>() ?? new List<string>();
                _ = Task.Run(() =>
                {
                    List<int[]> pairs;
                    try { pairs = _core.Timeline.GetSharedSessionWeights(nsIds); }
                    catch { pairs = new List<int[]>(); }
                    _core.SendToJS("vrcNetworkSessions", new { pairs });
                });
                break;
            }

            case "vrcSaveNetworkCache":
            {
                var ncName = msg["name"]?.ToString() ?? "";
                var ncJson = msg["cache"]?.ToString() ?? "{}";
                if (NetworkCacheFile(ncName) is string ncPath)
                {
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(ncPath)!);
                            File.WriteAllText(ncPath, ncJson, System.Text.Encoding.UTF8);
                        }
                        catch { }
                    });
                }
                break;
            }

            case "vrcLoadNetworkCache":
            {
                var nlName = msg["name"]?.ToString() ?? "";
                var nlPath = NetworkCacheFile(nlName);
                _ = Task.Run(() =>
                {
                    var json = "{}";
                    try { if (nlPath != null && File.Exists(nlPath)) json = File.ReadAllText(nlPath, System.Text.Encoding.UTF8); }
                    catch { json = "{}"; }
                    _core.SendToJS("vrcNetworkCacheLoaded", new { name = nlName, json });
                });
                break;
            }

            case "vrcClearNetworkCache":
            {
                var nxPath = NetworkCacheFile(msg["name"]?.ToString() ?? "");
                if (nxPath != null)
                {
                    _ = Task.Run(() =>
                    {
                        try { if (File.Exists(nxPath)) File.Delete(nxPath); } catch { }
                    });
                }
                break;
            }

            case "vrcSaveMutualCache":
            {
                var mcJson = msg["cache"]?.ToString() ?? "{}";
                _ = Task.Run(() =>
                {
                    try
                    {
                        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCNext", "Caches");
                        Directory.CreateDirectory(dir);
                        File.WriteAllText(Path.Combine(dir, "mutual_cache.json"), mcJson, System.Text.Encoding.UTF8);
                    }
                    catch { /* non-critical */ }
                });
                break;
            }

            case "vrcLoadMutualCache":
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCNext", "Caches", "mutual_cache.json");
                        var json = File.Exists(path) ? File.ReadAllText(path, System.Text.Encoding.UTF8) : "{}";
                        _core.SendToJS("vrcMutualCacheLoaded", new { json });
                    }
                    catch
                    {
                        _core.SendToJS("vrcMutualCacheLoaded", new { json = "{}" });
                    }
                });
                break;
            }

            case "vrcClearMutualCache":
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCNext", "Caches", "mutual_cache.json");
                        if (File.Exists(path)) File.Delete(path);
                    }
                    catch { /* non-critical */ }
                });
                break;
            }

            case "vrcInviteToGroup":
            {
                var invGid = msg["groupId"]?.ToString() ?? "";
                var invUids = msg["userIds"]?.ToObject<List<string>>() ?? new();
                if (!string.IsNullOrEmpty(invGid) && invUids.Count > 0)
                {
                    _ = Task.Run(async () =>
                    {
                        int done = 0, success = 0, fail = 0;
                        foreach (var uid in invUids)
                        {
                            var (ok, error) = await _core.Groups.CreateGroupInviteAsync(invGid, uid);
                            if (ok) success++; else fail++;
                            done++;
                            _core.SendToJS("vrcGroupInviteProgress", new { done, total = invUids.Count, success, fail, error });
                            if (done < invUids.Count) await Task.Delay(1000);
                        }
                    });
                }
                break;
            }

            case "vrcCreateGroupInstance":
            {
                var cgiWorldId = msg["worldId"]?.ToString() ?? "";
                var cgiGroupId = msg["groupId"]?.ToString() ?? "";
                var cgiAccessType = msg["groupAccessType"]?.ToString() ?? "members";
                var cgiRegion = msg["region"]?.ToString() ?? "eu";
                var cgiInstanceName = msg["instanceName"]?.ToString() ?? "";
                var cgiQueueEnabled = msg["queueEnabled"]?.ToObject<bool>() ?? false;
                var cgiAgeGateEnabled = msg["ageGateEnabled"]?.ToObject<bool>() ?? false;
                var cgiMinPerf = msg["minAvatarPerf"]?.ToString() ?? "";
                var cgiAndJoin = msg["andJoin"]?.ToObject<bool>() ?? true;
                if (!string.IsNullOrEmpty(cgiWorldId) && !string.IsNullOrEmpty(cgiGroupId))
                {
                    _ = Task.Run(async () =>
                    {
                        var location = await _core.Instances.CreateGroupInstanceAsync(
                            cgiWorldId, cgiGroupId, cgiAccessType, cgiRegion,
                            cgiInstanceName, cgiQueueEnabled, cgiAgeGateEnabled, cgiMinPerf);
                        if (!string.IsNullOrEmpty(location))
                        {
                            bool ok;
                            string message;
                            if (cgiAndJoin)
                            {
                                ok = await _core.Instances.InviteSelfAsync(location);
                                message = ok ? "Group instance created! Self-invite sent." : "Instance created but invite failed.";
                            }
                            else
                            {
                                ok = true;
                                message = "Group instance created.";
                            }
                            if (ok)
                            {
                                _core.Settings.MyInstances.Remove(location);
                                _core.Settings.MyInstances.Insert(0, location);
                                _core.Settings.Save();
                            }
                            _core.SendToJS("vrcActionResult", new
                            {
                                action = "createInstance",
                                success = ok,
                                message,
                                location
                            });
                        }
                        else
                        {
                            _core.SendToJS("vrcActionResult", new
                            {
                                action = "createInstance",
                                success = false,
                                message = "Failed to create group instance."
                            });
                        }
                    });
                }
                break;
            }
        }

        await Task.CompletedTask;
    }
}
