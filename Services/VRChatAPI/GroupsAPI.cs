using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VRCNext.Services;

public class GroupsAPI(VRChatApiService ctx)
{
    public async Task<JArray> SearchGroupsAsync(string query, int n = 20, int offset = 0)
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(query)) return new JArray();
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/groups?query={Uri.EscapeDataString(query)}&n={n}&offset={offset}");
            if (resp.IsSuccessStatusCode) return JArray.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { ctx.Log($"SearchGroups exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JArray> GetUserGroupsAsync()
    {
        if (!ctx.IsLoggedIn || ctx.CurrentUserId == null) return new JArray();
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/users/{ctx.CurrentUserId}/groups");
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                var arr = JArray.Parse(body);
                ctx.Log($"GetUserGroups({ctx.CurrentUserId}): {arr.Count} items");
                return arr;
            }
            ctx.Log($"GetUserGroups({ctx.CurrentUserId}) failed: {(int)resp.StatusCode}");
        }
        catch (Exception ex) { ctx.Log($"GetUserGroups({ctx.CurrentUserId}) exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JObject?> GetRepresentedGroupAsync()
    {
        if (!ctx.IsLoggedIn || ctx.CurrentUserId == null) return null;
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/users/{ctx.CurrentUserId}/groups/represented");
            if (resp.IsSuccessStatusCode)
                return JObject.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { ctx.Log($"GetRepresentedGroup exception: {ex.Message}"); }
        return null;
    }

    public async Task<JObject> GetUserGroupPermissionsAsync()
    {
        if (!ctx.IsLoggedIn || ctx.CurrentUserId == null) return new JObject();
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/users/{ctx.CurrentUserId}/groups/permissions");
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                ctx.Log($"GetUserGroupPermissions({ctx.CurrentUserId}): ok");
                return JObject.Parse(body);
            }
            ctx.Log($"GetUserGroupPermissions({ctx.CurrentUserId}) failed: {(int)resp.StatusCode}");
        }
        catch (Exception ex) { ctx.Log($"GetUserGroupPermissions exception: {ex.Message}"); }
        return new JObject();
    }

    public async Task<JObject?> GetGroupAsync(string groupId)
    {
        if (!ctx.IsLoggedIn) return null;
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/groups/{groupId}?includeRoles=true");
            if (resp.IsSuccessStatusCode)
            {
                using var reader = new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(await resp.Content.ReadAsStringAsync())) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None };
                return JObject.Load(reader);
            }
        }
        catch (Exception ex) { ctx.Log($"GetGroup exception: {ex.Message}"); }
        return null;
    }

    public async Task<(JObject? result, int status)> GetGroupWithStatusAsync(string groupId)
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(groupId)) return (null, 0);
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/groups/{groupId}");
            if (!resp.IsSuccessStatusCode) return (null, (int)resp.StatusCode);
            using var reader = new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(await resp.Content.ReadAsStringAsync())) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None };
            return (JObject.Load(reader), (int)resp.StatusCode);
        }
        catch (Exception ex) { ctx.Log($"GetGroup exception: {ex.Message}"); }
        return (null, 0);
    }

    public async Task<bool> JoinGroupAsync(string groupId)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/groups/{groupId}/join", content);
            ctx.Log($"JoinGroup {groupId}: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"JoinGroup exception: {ex.Message}"); return false; }
    }

    public async Task<bool> LeaveGroupAsync(string groupId)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/groups/{groupId}/leave", content);
            ctx.Log($"LeaveGroup {groupId}: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"LeaveGroup exception: {ex.Message}"); return false; }
    }

    public async Task<bool> SetRepresentedGroupAsync(string groupId)
    {
        if (!ctx.IsLoggedIn || ctx.CurrentUserId == null) return false;
        try
        {
            var body = new StringContent(
                JsonConvert.SerializeObject(new { isRepresenting = true }),
                Encoding.UTF8, "application/json");
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/groups/{groupId}/representation", body);
            ctx.Log($"SetRepresentedGroup({groupId}): {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"SetRepresentedGroup exception: {ex.Message}"); return false; }
    }

    public async Task<bool> UpdateGroupAsync(string groupId, string? description = null, string? rules = null, List<string>? languages = null, List<string>? links = null, string? iconId = null, string? bannerId = null, string? joinState = null)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var body = new JObject();
            if (description != null) body["description"] = description;
            if (rules       != null) body["rules"]       = rules;
            if (languages   != null) body["languages"]   = new JArray(languages);
            if (links       != null) body["links"]       = new JArray(links);
            if (iconId      != null) body["iconId"]      = iconId;
            if (bannerId    != null) body["bannerId"]     = bannerId;
            if (joinState   != null) body["joinState"]   = joinState;
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/groups/{groupId}",
                new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"));
            ctx.Log($"UpdateGroup({groupId}): {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode)
                ctx.Log($"UpdateGroup body: {await resp.Content.ReadAsStringAsync()}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"UpdateGroup exception: {ex.Message}"); return false; }
    }

    public async Task<JArray> GetGroupPostsAsync(string groupId, int n = 10, bool publicOnly = false)
    {
        if (!ctx.IsLoggedIn) return new JArray();
        try
        {
            var url = $"{VRChatApiService.BASE}/groups/{groupId}/posts?n={n}&offset=0{(publicOnly ? "&publicOnly=true" : "")}";
            var resp = await ctx._http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"GetGroupPosts({groupId}): status={(int)resp.StatusCode}, bodyLen={body.Length}, preview={body[..Math.Min(300, body.Length)]}");
            if (resp.IsSuccessStatusCode)
            {
                var token = JToken.Parse(body);
                if (token is JArray arr) return arr;
                if (token is JObject obj)
                {
                    var posts = obj["posts"] as JArray ?? obj["data"] as JArray;
                    if (posts != null) return posts;
                    ctx.Log($"GetGroupPosts: object keys={string.Join(", ", obj.Properties().Select(p => p.Name))}");
                }
            }
        }
        catch (Exception ex) { ctx.Log($"GetGroupPosts exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<bool> CreateGroupPostAsync(string groupId, string title, string text, string visibility, bool sendNotification, string? imageId)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var body = new JObject
            {
                ["title"]            = title,
                ["text"]             = text,
                ["visibility"]       = visibility,
                ["sendNotification"] = sendNotification,
            };
            if (!string.IsNullOrEmpty(imageId)) body["imageId"] = imageId;
            var resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/groups/{groupId}/posts",
                new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"));
            ctx.Log($"CreateGroupPost({groupId}): {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode)
                ctx.Log($"CreateGroupPost body: {await resp.Content.ReadAsStringAsync()}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"CreateGroupPost exception: {ex.Message}"); return false; }
    }

    public async Task<bool> UpdateGroupPostAsync(string groupId, string postId, string title, string text, string visibility, string? imageId)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var body = new JObject
            {
                ["title"]      = title,
                ["text"]       = text,
                ["visibility"] = visibility,
            };
            if (!string.IsNullOrEmpty(imageId)) body["imageId"] = imageId;
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/groups/{groupId}/posts/{postId}",
                new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"));
            ctx.Log($"UpdateGroupPost({groupId}/{postId}): {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode)
                ctx.Log($"UpdateGroupPost body: {await resp.Content.ReadAsStringAsync()}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"UpdateGroupPost exception: {ex.Message}"); return false; }
    }

    public async Task<bool> DeleteGroupPostAsync(string groupId, string postId)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var resp = await ctx._http.DeleteAsync($"{VRChatApiService.BASE}/groups/{groupId}/posts/{postId}");
            ctx.Log($"DeleteGroupPost({groupId}/{postId}): {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode)
                ctx.Log($"DeleteGroupPost body: {await resp.Content.ReadAsStringAsync()}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"DeleteGroupPost exception: {ex.Message}"); return false; }
    }

    public async Task<JArray> GetGroupMembersAsync(string groupId, int n = 50, int offset = 0)
    {
        if (!ctx.IsLoggedIn) return new JArray();
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/groups/{groupId}/members?n={n}&offset={offset}&sort=joinedAt:desc");
            if (resp.IsSuccessStatusCode) return JArray.Parse(await resp.Content.ReadAsStringAsync());
            ctx.Log($"GetGroupMembers({groupId}): {(int)resp.StatusCode}");
        }
        catch (Exception ex) { ctx.Log($"GetGroupMembers exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JObject?> FindGroupMemberByDisplayNameAsync(string groupId, string displayName, string userId)
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(userId)) return null;
        const int pageSize = 100;
        const int maxPages = 10;
        try
        {
            if (displayName.Length >= 3)
            {
                var searchUrl = $"{VRChatApiService.BASE}/groups/{groupId}/members/search?query={Uri.EscapeDataString(displayName)}&n={pageSize}&offset=0";
                var sResp = await ctx._http.GetAsync(searchUrl);
                if (sResp.IsSuccessStatusCode)
                {
                    var sTok = JToken.Parse(await sResp.Content.ReadAsStringAsync());
                    var results = (sTok as JObject)?["results"] as JArray ?? sTok as JArray;
                    var hit = results?.FirstOrDefault(m => m["userId"]?.ToString() == userId) as JObject;
                    ctx.Log($"[GRP] member search {displayName}: {results?.Count ?? 0} result(s), match={hit != null}");
                    if (hit != null) return hit;
                }
                else ctx.Log($"[GRP] member search {displayName}: HTTP {(int)sResp.StatusCode}");
            }

            for (int page = 0; page < maxPages; page++)
            {
                var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/groups/{groupId}/members?n={pageSize}&offset={page * pageSize}&sort=joinedAt:desc");
                if (!resp.IsSuccessStatusCode)
                {
                    ctx.Log($"FindGroupMember({groupId}/{displayName}): {(int)resp.StatusCode}");
                    break;
                }
                var arr = JArray.Parse(await resp.Content.ReadAsStringAsync());
                var match = arr.FirstOrDefault(m => m["userId"]?.ToString() == userId) as JObject;
                ctx.Log($"[GRP] member list page {page}: {arr.Count} visible member(s), match={match != null}");
                if (match != null) return match;
                if (arr.Count < pageSize) break;
            }
        }
        catch (Exception ex) { ctx.Log($"FindGroupMember exception: {ex.Message}"); }
        return null;
    }

    public async Task<JArray> SearchGroupMembersAsync(string groupId, string query)
    {
        if (!ctx.IsLoggedIn) return new JArray();
        var matches = new JArray();
        int offset = 0;
        const int pageSize = 100;
        const int maxPages = 10;
        for (int page = 0; page < maxPages; page++)
        {
            try
            {
                var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/groups/{groupId}/members?n={pageSize}&offset={offset}&sort=joinedAt:desc");
                if (!resp.IsSuccessStatusCode) break;
                var page_data = JArray.Parse(await resp.Content.ReadAsStringAsync());
                foreach (var m in page_data)
                {
                    var name = m["user"]?["displayName"]?.ToString() ?? m["displayName"]?.ToString() ?? "";
                    if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
                        matches.Add(m);
                }
                if (page_data.Count < pageSize) break;
                offset += pageSize;
                if (matches.Count >= 50) break;
            }
            catch (Exception ex) { ctx.Log($"SearchGroupMembers exception: {ex.Message}"); break; }
        }
        return matches;
    }

    public async Task<JArray> GetGroupRoleMembersAsync(string groupId, string roleId)
    {
        if (!ctx.IsLoggedIn) return new JArray();
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/groups/{groupId}/members?n=100&roleId={Uri.EscapeDataString(roleId)}");
            if (resp.IsSuccessStatusCode) return JArray.Parse(await resp.Content.ReadAsStringAsync());
            ctx.Log($"GetGroupRoleMembers({groupId}/{roleId}): {(int)resp.StatusCode}");
        }
        catch (Exception ex) { ctx.Log($"GetGroupRoleMembers exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<bool> KickGroupMemberAsync(string groupId, string userId)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var resp = await ctx._http.DeleteAsync($"{VRChatApiService.BASE}/groups/{groupId}/members/{userId}");
            ctx.Log($"KickGroupMember({groupId}/{userId}): {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode) ctx.Log($"KickGroupMember body: {await resp.Content.ReadAsStringAsync()}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"KickGroupMember exception: {ex.Message}"); return false; }
    }

    public async Task<bool> BanGroupMemberAsync(string groupId, string userId)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var body = new JObject { ["userId"] = userId };
            var resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/groups/{groupId}/bans",
                new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"));
            ctx.Log($"BanGroupMember({groupId}/{userId}): {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode) ctx.Log($"BanGroupMember body: {await resp.Content.ReadAsStringAsync()}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"BanGroupMember exception: {ex.Message}"); return false; }
    }

    // Audit logs are paged and, unlike most group endpoints, return an object
    // ({ results, totalCount, hasNext }) rather than a bare array.
    // Requires the group-audit-view permission.
    public async Task<JObject?> GetGroupAuditLogsAsync(string groupId, int n = 50, int offset = 0, string eventTypes = "")
    {
        if (!ctx.IsLoggedIn) return null;
        try
        {
            var url = $"{VRChatApiService.BASE}/groups/{groupId}/auditLogs?n={n}&offset={offset}";
            if (!string.IsNullOrWhiteSpace(eventTypes))
                url += $"&eventTypes={Uri.EscapeDataString(eventTypes)}";

            var resp = await ctx._http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode) return JObject.Parse(body);
            ctx.Log($"GetGroupAuditLogs({groupId}): {(int)resp.StatusCode}");
        }
        catch (Exception ex) { ctx.Log($"GetGroupAuditLogs exception: {ex.Message}"); }
        return null;
    }

    public async Task<JArray> GetGroupBansAsync(string groupId)
    {
        if (!ctx.IsLoggedIn) return new JArray();
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/groups/{groupId}/bans");
            if (resp.IsSuccessStatusCode) return JArray.Parse(await resp.Content.ReadAsStringAsync());
            ctx.Log($"GetGroupBans({groupId}): {(int)resp.StatusCode}");
        }
        catch (Exception ex) { ctx.Log($"GetGroupBans exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<bool> UnbanGroupMemberAsync(string groupId, string userId)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var resp = await ctx._http.DeleteAsync($"{VRChatApiService.BASE}/groups/{groupId}/bans/{userId}");
            ctx.Log($"UnbanGroupMember({groupId}/{userId}): {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"UnbanGroupMember exception: {ex.Message}"); return false; }
    }

    public async Task<bool> AddGroupMemberRoleAsync(string groupId, string userId, string roleId)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/groups/{groupId}/members/{userId}/roles/{roleId}",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            ctx.Log($"AddGroupMemberRole({groupId}/{userId}/{roleId}): {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"AddGroupMemberRole exception: {ex.Message}"); return false; }
    }

    public async Task<bool> RemoveGroupMemberRoleAsync(string groupId, string userId, string roleId)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var resp = await ctx._http.DeleteAsync($"{VRChatApiService.BASE}/groups/{groupId}/members/{userId}/roles/{roleId}");
            ctx.Log($"RemoveGroupMemberRole({groupId}/{userId}/{roleId}): {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"RemoveGroupMemberRole exception: {ex.Message}"); return false; }
    }

    public async Task<JObject?> CreateGroupRoleAsync(string groupId, string name, string description, List<string> permissions, bool isAddedOnJoin, bool isSelfAssignable, bool requiresTwoFactor)
    {
        if (!ctx.IsLoggedIn) return null;
        try
        {
            var body = new JObject
            {
                ["name"]              = name,
                ["description"]       = description,
                ["isAddedOnJoin"]     = isAddedOnJoin,
                ["isSelfAssignable"]  = isSelfAssignable,
                ["requiresTwoFactor"] = requiresTwoFactor,
            };
            var resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/groups/{groupId}/roles",
                new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"));
            ctx.Log($"CreateGroupRole({groupId}): {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode) { ctx.Log($"CreateGroupRole body: {await resp.Content.ReadAsStringAsync()}"); return null; }
            var role = JObject.Parse(await resp.Content.ReadAsStringAsync());
            if (permissions.Count > 0)
            {
                var roleId = role["id"]?.ToString();
                if (!string.IsNullOrEmpty(roleId))
                    await UpdateGroupRoleAsync(groupId, roleId, null, null, permissions, null, null, null);
            }
            return role;
        }
        catch (Exception ex) { ctx.Log($"CreateGroupRole exception: {ex.Message}"); }
        return null;
    }

    public async Task<bool> UpdateGroupRoleAsync(string groupId, string roleId, string? name, string? description, List<string>? permissions, bool? isAddedOnJoin, bool? isSelfAssignable, bool? requiresTwoFactor)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var body = new JObject();
            if (name        != null) body["name"]        = name;
            if (description != null) body["description"] = description;
            if (permissions != null) body["permissions"] = new JArray(permissions);
            if (isAddedOnJoin.HasValue)     body["isAddedOnJoin"]     = isAddedOnJoin.Value;
            if (isSelfAssignable.HasValue)  body["isSelfAssignable"]  = isSelfAssignable.Value;
            if (requiresTwoFactor.HasValue) body["requiresTwoFactor"] = requiresTwoFactor.Value;
            var reqBody = body.ToString(Newtonsoft.Json.Formatting.None);
            if (permissions != null)
                ctx.Log($"UpdateGroupRole perms: [{string.Join(", ", permissions)}]");
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/groups/{groupId}/roles/{roleId}",
                new StringContent(reqBody, Encoding.UTF8, "application/json"));
            ctx.Log($"UpdateGroupRole({groupId}/{roleId}): {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode) ctx.Log($"UpdateGroupRole body: {await resp.Content.ReadAsStringAsync()}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"UpdateGroupRole exception: {ex.Message}"); return false; }
    }

    public async Task<bool> DeleteGroupRoleAsync(string groupId, string roleId)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var resp = await ctx._http.DeleteAsync($"{VRChatApiService.BASE}/groups/{groupId}/roles/{roleId}");
            ctx.Log($"DeleteGroupRole({groupId}/{roleId}): {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"DeleteGroupRole exception: {ex.Message}"); return false; }
    }

    public async Task<bool> SetGroupMemberVisibilityAsync(string groupId, string userId, string visibility)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var content = new StringContent(
                $"{{\"visibility\":\"{visibility}\"}}",
                Encoding.UTF8, "application/json");
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/groups/{groupId}/members/{userId}", content);
            ctx.Log($"SetGroupMemberVisibility({groupId},{userId},{visibility}): {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"SetGroupMemberVisibility exception: {ex.Message}"); return false; }
    }

    public async Task<bool> RespondGroupJoinRequestAsync(string groupId, string userId, string action)
    {
        if (!ctx.IsLoggedIn) return false;
        try
        {
            var body = new StringContent(JsonConvert.SerializeObject(new { action }), Encoding.UTF8, "application/json");
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/groups/{groupId}/requests/{userId}", body);
            ctx.Log($"RespondGroupJoinRequest {groupId}/{userId} ({action}): {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"RespondGroupJoinRequest exception: {ex.Message}"); return false; }
    }

    public async Task<(bool ok, string? error)> CreateGroupInviteAsync(string groupId, string userId)
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(groupId) || string.IsNullOrEmpty(userId)) return (false, null);
        try
        {
            var body = new StringContent(JsonConvert.SerializeObject(new { userId, confirmOverrideBlock = false }), Encoding.UTF8, "application/json");
            var resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/groups/{groupId}/invites", body);
            var respBody = await resp.Content.ReadAsStringAsync();
            ctx.Log($"CreateGroupInvite {groupId} user={userId}: {(int)resp.StatusCode} body: {respBody[..Math.Min(300, respBody.Length)]}");
            if (resp.IsSuccessStatusCode) return (true, null);
            try { var err = JObject.Parse(respBody); return (false, err["error"]?["message"]?.ToString()); } catch { }
            return (false, $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) { ctx.Log($"CreateGroupInvite exception: {ex.Message}"); return (false, ex.Message); }
    }

    public async Task<bool> DeclineGroupInviteAsync(string groupId)
    {
        if (!ctx.IsLoggedIn || string.IsNullOrEmpty(groupId)) return false;
        try
        {
            var body = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp = await ctx._http.PutAsync($"{VRChatApiService.BASE}/groups/{groupId}/invites", body);
            ctx.Log($"DeclineGroupInvite {groupId}: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { ctx.Log($"DeclineGroupInvite exception: {ex.Message}"); return false; }
    }

    public async Task<string?> FindGroupIdByShortCodeAsync(string shortCode)
    {
        var groups = await GetUserGroupsAsync();
        var match = groups.Cast<JObject>()
            .FirstOrDefault(g => string.Equals(g["shortCode"]?.ToString(), shortCode, StringComparison.OrdinalIgnoreCase));
        var gid = match?["groupId"]?.ToString();
        if (string.IsNullOrEmpty(gid)) gid = match?["id"]?.ToString();
        return gid?.StartsWith("grp_") == true ? gid : null;
    }

    public async Task<JArray> GetGroupGalleryImagesAsync(string groupId, string galleryId, int n = 20)
    {
        if (!ctx.IsLoggedIn) return new JArray();
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/groups/{groupId}/galleries/{galleryId}?n={n}&offset=0");
            if (resp.IsSuccessStatusCode) return JArray.Parse(await resp.Content.ReadAsStringAsync());
            ctx.Log($"GetGroupGallery({groupId}/{galleryId}): {(int)resp.StatusCode}");
        }
        catch (Exception ex) { ctx.Log($"GetGroupGallery exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<JArray> GetGroupInstancesAsync(string groupId)
    {
        if (!ctx.IsLoggedIn || ctx.CurrentUserId == null) return new JArray();
        try
        {
            var resp = await ctx._http.GetAsync($"{VRChatApiService.BASE}/users/{ctx.CurrentUserId}/instances/groups/{groupId}");
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"GetGroupInstances({groupId}): {(int)resp.StatusCode}, len={body.Length}");
            if (resp.IsSuccessStatusCode)
            {
                var token = JToken.Parse(body);
                if (token is JObject obj && obj["instances"] is JArray arr) return arr;
                if (token is JArray directArr) return directArr;
            }
        }
        catch (Exception ex) { ctx.Log($"GetGroupInstances exception: {ex.Message}"); }
        return new JArray();
    }

    public async Task<(bool ok, string error, string groupId)> CreateGroupAsync(string name, string shortCode, string description, string joinState, string privacy, string roleTemplate, string? iconId, string? bannerId)
    {
        if (!ctx.IsLoggedIn) return (false, "Not logged in", "");
        try
        {
            var body = new JObject
            {
                ["name"]         = name,
                ["shortCode"]    = shortCode,
                ["description"]  = description,
                ["joinState"]    = joinState,
                ["privacy"]      = privacy,
                ["roleTemplate"] = roleTemplate,
            };
            if (!string.IsNullOrEmpty(iconId))   body["iconId"]   = iconId;
            if (!string.IsNullOrEmpty(bannerId)) body["bannerId"] = bannerId;
            var resp = await ctx._http.PostAsync($"{VRChatApiService.BASE}/groups",
                new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"));
            var respBody = await resp.Content.ReadAsStringAsync();
            ctx.Log($"CreateGroup {shortCode}: {(int)resp.StatusCode}");
            if (resp.IsSuccessStatusCode)
                return (true, "", JObject.Parse(respBody)["id"]?.ToString() ?? "");
            return (false, VRChatApiService.TryGetApiError(respBody) ?? $"API error {(int)resp.StatusCode}", "");
        }
        catch (Exception ex) { ctx.Log($"CreateGroup exception: {ex.Message}"); return (false, ex.Message, ""); }
    }

    public async Task<(bool ok, string error)> DeleteGroupAsync(string groupId)
    {
        if (!ctx.IsLoggedIn) return (false, "Not logged in");
        try
        {
            var resp = await ctx._http.DeleteAsync($"{VRChatApiService.BASE}/groups/{groupId}");
            var body = await resp.Content.ReadAsStringAsync();
            ctx.Log($"DeleteGroup {groupId}: {(int)resp.StatusCode}");
            if (resp.IsSuccessStatusCode) return (true, "");
            return (false, VRChatApiService.TryGetApiError(body) ?? $"API error {(int)resp.StatusCode}");
        }
        catch (Exception ex) { ctx.Log($"DeleteGroup exception: {ex.Message}"); return (false, ex.Message); }
    }
}
