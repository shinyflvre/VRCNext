/* === Photino message handler === */
window.external.receiveMessage(rawMsg => {
    const { type, payload } = JSON.parse(rawMsg);
    switch (type) {
            case 'loadSettings': loadSettingsToUI(payload); break;
            case 'relayState': setRelayState(payload.running, payload.streams); break;
            case 'log': addLog(payload.msg, payload.color); break;
            case 'toast': showToast(payload.ok, payload.msg); break;
            case 'wsStatus': {
                const badge = document.getElementById('wsBadge');
                if (badge) {
                    badge.className = 'mini-badge ' + (payload.connected ? 'online' : 'offline');
                    badge.querySelector('.mini-badge-icon').textContent = payload.connected ? 'wifi' : 'wifi_off';
                }
                break;
            }
            case 'vcState': handleVcState(payload); break;
            case 'ffcProgress': handleFfcProgress(payload); break;
            case 'stats':
                if (payload.files !== undefined) document.getElementById('statFiles').textContent = payload.files;
                if (payload.size !== undefined) document.getElementById('statSize').textContent = payload.size;
                break;
            case 'uptimeTick': document.getElementById('statUptime').textContent = payload; break;
            case 'windowMaxState':
                document.getElementById('winMaxIcon').textContent = payload ? 'close_fullscreen' : 'open_in_full';
                document.body.classList.toggle('maximized', !!payload);
                break;
            case 'filePosted': addFileToList(payload); playNotifySound(); break;
            case 'deleteResult':
                if (payload.success) {
                    postedFiles = postedFiles.filter(f => f.messageId !== payload.messageId);
                    renderFileList();
                    addLog('Deleted from Discord', 'ok');
                } else addLog('Delete failed', 'err');
                break;
            case 'folderAdded':
                if (!settings.folders) settings.folders = [];
                settings.folders.push(payload);
                renderFolders(settings.folders);
                updateFolderFilterOptions(settings.folders);
                autoSave();
                break;
            case 'libraryData': renderLibrary(payload); break;
            case 'libraryPageData': appendLibraryPage(payload); break;
            case 'libraryWorldIds': applyLibraryWorldIds(payload); break;
            case 'libraryNewFile': addNewLibraryFile(payload); break;
            case 'libraryFileDeleted':
                libraryFiles = libraryFiles.filter(f => f.path !== payload.path);
                filterLibrary(true); // stay on current page after delete
                break;
            case 'favoritesLoaded':
                favorites = new Set(payload || []);
                filterLibrary();
                break;
            case 'exeAdded':
                if (payload.target === 'vrchat') document.getElementById('setVrcPath').value = payload.path;
                else {
                    if (!settings.extraExe) settings.extraExe = [];
                    settings.extraExe.push(payload.path);
                    renderExtraExe(settings.extraExe);
                }
                autoSave();
                break;
            case 'vrcUser':
                renderVrcProfile(payload);
                if (payload.currentAvatar) currentAvatarId = payload.currentAvatar;
                document.getElementById('vrcLoginPrompt') && (document.getElementById('vrcLoginPrompt').style.display = 'none');
                document.getElementById('btnVrcLogin').style.display = 'none';
                document.getElementById('btnVrcLogout').style.display = '';
                document.getElementById('vrcLoginStatus').textContent = 'Connected as ' + payload.displayName;
                document.getElementById('modal2FA').style.display = 'none';
                // Show friend skeleton cards in sidebar while friends load
                if (!vrcFriendsLoaded) {
                    const fsl = document.getElementById('vrcFriendsList');
                    if (fsl) fsl.innerHTML = '<div class="vrc-section-label">IN-GAME — ···</div>' + sk('friend', 10);
                }
                renderDashboard();
                loadMyInstances();
                requestInstanceInfo();
                refreshNotifications();
                { const vp = document.getElementById('badgeVrcPlus');
                  if (vp) { const isVrcPlus = Array.isArray(payload.tags) && payload.tags.includes('system_supporter'); vp.style.display = isVrcPlus ? '' : 'none'; } }
                break;
            case 'vrcCredits': {
                const bc = document.getElementById('badgeVrcCredits');
                const bl = document.getElementById('badgeVrcCreditsLabel');
                if (bc && bl) { bl.textContent = 'V ' + payload.balance.toLocaleString(); bc.style.display = ''; }
                break;
            }
            case 'vrcFriends':
                vrcFriendsLoaded = true;
                if (payload.friends) {
                    renderVrcFriends(payload.friends, payload.counts);
                    vrcFriendsData = payload.friends;
                } else {
                    renderVrcFriends(payload);
                    vrcFriendsData = payload;
                }
                requestWorldResolution(); renderDashboard(); requestInstanceInfo();
                if (favFriendsData.length > 0) filterFavFriends();
                break;
            case 'vrcNeeds2FA': show2FAModal(payload.type || 'totp'); break;
            case 'vrcLoginError':
                document.getElementById('modal2FAError').textContent = payload.error || 'Login failed';
                document.getElementById('vrcQuickError').textContent = payload.error || 'Login failed';
                document.getElementById('vrcLoginStatus').textContent = payload.error || 'Login failed';
                break;
            case 'vrcLoggedOut':
                vrcFriendsLoaded = false;
                renderVrcProfile(null);
                document.getElementById('vrcFriendsList').innerHTML = '';
                document.getElementById('vrcLoginPrompt') && (document.getElementById('vrcLoginPrompt').style.display = '');
                { const vp = document.getElementById('badgeVrcPlus'); if (vp) vp.style.display = 'none'; }
                break;
            case 'vrcPrefillLogin':
                if (payload.username) {
                    document.getElementById('vrcQuickUser').value = payload.username;
                    document.getElementById('vrcQuickPass').value = payload.password || '';
                }
                break;
            case 'vrcFriendDetail': renderFriendDetail(payload); break;
            case 'vrcFavoriteFriends': renderFavFriends(payload); break;
            case 'vrcFavoriteFriendToggled': handleFavFriendToggled(payload); break;
            case 'vrcFriendDetailError':
                document.getElementById('friendDetailContent').innerHTML = `<div class="fd-loading" style="color:var(--err);">${esc(payload.error || 'Error loading profile')}</div><div style="margin-top:10px;text-align:right;"><button class="vrcn-button-round" onclick="closeFriendDetail()">Close</button></div>`;
                break;
            case 'vrcActionResult':
                if (payload.action === 'sendChatMessage') { if (typeof handleChatActionResult === 'function') handleChatActionResult(payload); break; }
                if (payload.action === 'boop') { showToast(payload.success, payload.message); break; }
                if (payload.action === 'createInstance' && payload.success) { loadMyInstances(); }
                if (payload.action === 'deleteGroupEvent') {
                    if (payload.success) {
                        const card = document.querySelector(`.fd-group-card[data-event-id="${payload.eventId}"]`);
                        if (card) card.remove();
                        showToast(true, 'Group event deleted.');
                    } else {
                        const card = document.querySelector(`.fd-group-card[data-event-id="${payload.eventId}"]`);
                        if (card) {
                            card.style.opacity = '';
                            card.style.pointerEvents = '';
                            const btn = card.querySelector('.gd-post-del');
                            if (btn) { btn.disabled = false; btn.querySelector('.msi').textContent = 'delete'; }
                        }
                        showToast(false, 'Delete failed.');
                    }
                } else if (payload.action === 'deleteGroupPost') {
                    if (payload.success) {
                        const card = document.querySelector(`.fd-group-card[data-post-id="${payload.postId}"]`);
                        if (card) card.remove();
                        showToast(true, 'Group post deleted.');
                    } else {
                        const card = document.querySelector(`.fd-group-card[data-post-id="${payload.postId}"]`);
                        if (card) {
                            card.style.opacity = '';
                            card.style.pointerEvents = '';
                            const btn = card.querySelector('.gd-post-del');
                            if (btn) { btn.disabled = false; btn.querySelector('.msi').textContent = 'delete'; }
                        }
                        showToast(false, 'Delete failed.');
                    }
                } else if (payload.action === 'addGroupMemberRole' || payload.action === 'removeGroupMemberRole') {
                    showToast(payload.success, payload.message);
                    if (payload.success && payload.userId && payload.roleId) {
                        if (!window._gdMemberRoleIds) window._gdMemberRoleIds = {};
                        const cur = window._gdMemberRoleIds[payload.userId] || [];
                        if (payload.action === 'addGroupMemberRole') {
                            if (!cur.includes(payload.roleId)) window._gdMemberRoleIds[payload.userId] = [...cur, payload.roleId];
                        } else {
                            window._gdMemberRoleIds[payload.userId] = cur.filter(r => r !== payload.roleId);
                        }
                        // Invalidate role-member tabs so they refetch on next open
                        document.querySelectorAll('[id^="gdrole-members-list-"]').forEach(el => delete el.dataset.loaded);
                    }
                } else if (payload.action === 'unbanGroupMember') {
                    showToast(payload.success, payload.message);
                    if (payload.success) {
                        // Remove card from banned list
                        const bannedList = document.getElementById('gdBannedList');
                        if (bannedList) {
                            bannedList.querySelectorAll('.vrcn-profile-item').forEach(card => {
                                if (card.innerHTML.includes(`openFriendDetail('${payload.userId}')`)) card.remove();
                            });
                            if (!bannedList.querySelector('.vrcn-profile-item'))
                                bannedList.innerHTML = '<div style="padding:20px;text-align:center;font-size:12px;color:var(--tx3);">No banned members</div>';
                        }
                    }
                } else {
                    showToast(payload.success, payload.message);
                    // Auto-refresh groups list on join/leave (from anywhere in the app)
                    if (payload.success && (payload.action === 'joinGroup' || payload.action === 'leaveGroup' || payload.groupJoined)) {
                        loadMyGroups();
                    }
                    // Re-enable friend action buttons if open
                    if (!['createGroupPost', 'acceptNotif', 'join'].includes(payload.action)) {
                        const fdActions = document.querySelector('#friendDetailContent .fd-actions');
                        if (fdActions) fdActions.querySelectorAll('button').forEach(b => b.disabled = false);
                    }
                }
                break;
            case 'vrcGroupUpdated':
                if (payload.success) {
                    // Only update each view if that field was actually part of the save
                    const dv = document.getElementById('gdescDescView');
                    const rv = document.getElementById('gdescRulesView');
                    const lnv = document.getElementById('ggrpLinksView');
                    const lav = document.getElementById('ggrpLangsView');
                    const jsv = document.getElementById('ggrpJoinStateView');
                    if (dv && payload.description != null) dv.innerHTML = payload.description
                        ? `<div class="fd-bio">${esc(payload.description)}</div>`
                        : '<div class="myp-empty">No description</div>';
                    if (rv && payload.rules != null) rv.innerHTML = payload.rules
                        ? `<div style="font-size:11px;color:var(--tx3);padding:8px;background:var(--bg-input);border-radius:8px;max-height:120px;overflow-y:auto;white-space:pre-wrap;">${esc(payload.rules)}</div>`
                        : '<div class="myp-empty">No rules set</div>';
                    if (lnv && payload.links != null) {
                        const links = (payload.links || []).filter(Boolean);
                        lnv.innerHTML = links.length
                            ? `<div class="fd-bio-links">${links.map(url => renderBioLink(url)).join('')}</div>`
                            : '<div class="myp-empty">No links added</div>';
                    }
                    if (lav && payload.languages != null) {
                        const langs = payload.languages || [];
                        lav.innerHTML = langs.length
                            ? `<div class="fd-lang-tags">${langs.map(l => `<span class="vrcn-badge">${esc(LANG_MAP['language_'+l] || l.toUpperCase())}</span>`).join('')}</div>`
                            : '<div class="myp-empty">No languages set</div>';
                    }
                    if (jsv && payload.joinState != null) {
                        jsv.innerHTML = joinStateBadge(payload.joinState);
                        const hb = document.getElementById('ggrpHeaderBadge');
                        if (hb) hb.innerHTML = joinStateBadge(payload.joinState);
                    }
                    // Icon/banner changed — re-fetch the group to get the new resolved URLs
                    if (payload.iconId || payload.bannerId) {
                        const gid = payload.groupId;
                        if (gid) sendToCS({ action: 'vrcGetGroup', groupId: gid });
                    }
                    // Only cancel the field that was actually being edited
                    if (payload.description != null) cancelGroupField('desc');
                    if (payload.rules != null) cancelGroupField('rules');
                    if (payload.links != null) cancelGroupField('links');
                    if (payload.languages != null) cancelGroupField('langs');
                    if (payload.joinState != null) cancelGroupField('joinState');
                    showToast(true, 'Group updated!');
                } else {
                    document.querySelectorAll('#gdescDescEdit .vrcn-btn-primary, #gdescRulesEdit .vrcn-btn-primary, #ggrpLinksEdit .vrcn-btn-primary, #ggrpLangsEdit .vrcn-btn-primary, #ggrpJoinStateEdit .vrcn-btn-primary').forEach(b => b.disabled = false);
                    showToast(false, 'Update failed.');
                }
                break;
            case 'vrcProfileUpdated':
                if (payload.success) {
                    renderMyProfileContent();
                    showToast(true, 'Saved!');
                } else {
                    document.querySelectorAll('#mypBox .vrcn-btn-primary').forEach(b => b.disabled = false);
                    showToast(false, payload.error || 'Update failed');
                }
                break;
            case 'vrcNoteUpdated':
                const savedEl = document.getElementById('fdNoteSaved');
                if (savedEl) {
                    if (payload.success) { savedEl.textContent = '✓ Saved'; savedEl.style.color = 'var(--ok)'; }
                    else { savedEl.textContent = '✗ Failed'; savedEl.style.color = 'var(--err)'; }
                    setTimeout(() => { if (savedEl) savedEl.textContent = ''; }, 3000);
                }
                break;
            case 'vrcUnfriendDone':
                closeFriendDetail();
                sendToCS({ action: 'vrcRefreshFriends' });
                break;
            case 'vrcCalendarEvents': renderCalendarEvents(payload); break;
            case 'vrcCalendarEvent':  renderEventDetail(payload); break;
            case 'vrcBlockedList':
                blockedData = Array.isArray(payload) ? payload : [];
                renderModList('blockedList', blockedData, 'block');
                break;
            case 'vrcMutedList':
                mutedData = Array.isArray(payload) ? payload : [];
                renderModList('mutedList', mutedData, 'mute');
                break;
            case 'vrcModDone': {
                const { userId: modUid, type: modType, active: modActive } = payload;
                const modName = (currentFriendDetail && currentFriendDetail.id === modUid)
                    ? (currentFriendDetail.displayName || modUid)
                    : modUid;
                const modImage = (currentFriendDetail && currentFriendDetail.id === modUid)
                    ? (currentFriendDetail.image || '') : '';
                if (modType === 'block') {
                    if (modActive) {
                        if (!Array.isArray(blockedData)) blockedData = [];
                        if (!blockedData.some(e => e.targetUserId === modUid))
                            blockedData.push({ targetUserId: modUid, targetDisplayName: modName, image: modImage });
                    } else {
                        blockedData = (blockedData || []).filter(e => e.targetUserId !== modUid);
                    }
                    renderModList('blockedList', blockedData, 'block');
                } else {
                    if (modActive) {
                        if (!Array.isArray(mutedData)) mutedData = [];
                        if (!mutedData.some(e => e.targetUserId === modUid))
                            mutedData.push({ targetUserId: modUid, targetDisplayName: modName, image: modImage });
                    } else {
                        mutedData = (mutedData || []).filter(e => e.targetUserId !== modUid);
                    }
                    renderModList('mutedList', mutedData, 'mute');
                }
                // Update buttons in open detail modal
                const btn = document.getElementById(modType === 'block' ? 'fdBlockBtn' : 'fdMuteBtn');
                if (btn) {
                    btn.classList.toggle('active', modActive);
                    btn.title = modActive ? (modType === 'block' ? 'Unblock' : 'Unmute') : (modType === 'block' ? 'Block' : 'Mute');
                    const icon = btn.querySelector('.msi');
                    if (icon) icon.textContent = modType === 'block' ? (modActive ? 'block' : 'shield') : (modActive ? 'mic_off' : 'mic');
                }
                break;
            }
            case 'vrcAvatars':
                if (payload.filter === 'own') {
                    avatarsData = payload.avatars || [];
                    if (payload.currentAvatarId) currentAvatarId = payload.currentAvatarId;
                    avatarsLoaded = true;
                    if (avatarFilter === 'own') renderAvatarGrid();
                }
                break;
            case 'vrcFavoriteAvatars':
                avatarsLoaded = true;
                renderFavAvatars(payload);
                break;
            case 'vrcAvatarFavoriteResult':
                onAvatarFavoriteResult(payload);
                break;
            case 'vrcAvatarUnfavoriteResult':
                onAvatarUnfavoriteResult(payload);
                break;
            case 'vrcAvatarFavGroups':
                onAvatarFavGroupsLoaded(payload);
                break;
            case 'vrcAvatarSelected':
                if (payload.avatarId) currentAvatarId = payload.avatarId;
                document.querySelectorAll('.av-card').forEach(c => { c.style.pointerEvents = ''; c.style.opacity = ''; });
                if (avatarFilter === 'own') renderAvatarGrid();
                else if (avatarFilter === 'favorites') filterFavAvatars();
                break;
            case 'vrcAvatarSearchResults':
                if (payload.page === 0) avatarSearchResults = payload.results || [];
                else avatarSearchResults = [...avatarSearchResults, ...(payload.results || [])];
                avatarSearchHasMore = payload.hasMore || false;
                renderSearchGrid();
                break;
            case 'vrcSearchResults':
                renderSearchResults(payload.type, payload.results, payload.offset || 0, payload.hasMore || false);
                break;
            case 'vrcMyGroups':
                renderMyGroups(payload);
                if (document.getElementById('ciGroupRow')?.style.display !== 'none')
                    renderCiGroupPicker(myGroups);
                break;
            case 'vrcGroupDetail':
                renderGroupDetail(payload);
                break;
            case 'vrcGroupBans':
                renderGroupBans(payload.groupId, payload.bans);
                break;
            case 'vrcGroupRoleResult':
                onGroupRoleResult(payload);
                break;
            case 'vrcGroupRoleMembers':
                onGroupRoleMembers(payload);
                break;
            case 'vrcGroupDetailError':
                document.getElementById('detailModalContent').innerHTML = `<div style="padding:30px;text-align:center;color:var(--err);">${esc(payload.error || 'Error loading group')}</div><div style="text-align:center;margin-top:10px;"><button class="vrcn-button-round" onclick="document.getElementById('modalDetail').style.display='none'">Close</button></div>`;
                break;
            case 'vrcGroupMembersPage':
                {
                    const list = document.getElementById('gdMembersList');
                    if (list && payload.members) {
                        // offset=0 means fresh load (reset), append otherwise
                        if (payload.offset === 0) {
                            list.innerHTML = payload.members.map(m => renderGroupMemberCard(m)).join('')
                                || '<div style="padding:16px;text-align:center;font-size:12px;color:var(--tx3);">No members</div>';
                            window._gdMembersOffset = payload.members.length;
                        } else {
                            payload.members.forEach(m => { list.insertAdjacentHTML('beforeend', renderGroupMemberCard(m)); });
                            window._gdMembersOffset = (window._gdMembersOffset || 0) + payload.members.length;
                        }
                    }
                    const loadMoreDiv = document.getElementById('gdMembersLoadMore');
                    if (loadMoreDiv) {
                        if (payload.hasMore) {
                            loadMoreDiv.innerHTML = '<button class="vrcn-button" onclick="loadMoreGroupMembers()">Load More Members</button>';
                        } else {
                            loadMoreDiv.innerHTML = '<div style="font-size:11px;color:var(--tx3);padding:6px;">All members loaded</div>';
                        }
                    }
                }
                break;
            case 'vrcGroupSearchResults':
                {
                    const list = document.getElementById('gdMembersList');
                    if (list) {
                        list.innerHTML = payload.members && payload.members.length > 0
                            ? payload.members.map(m => renderGroupMemberCard(m)).join('')
                            : `<div style="padding:16px;text-align:center;font-size:12px;color:var(--tx3);">No members found for "<em>${esc(payload.query || '')}</em>"</div>`;
                    }
                    const loadMoreDiv = document.getElementById('gdMembersLoadMore');
                    if (loadMoreDiv) loadMoreDiv.innerHTML = '';
                }
                break;
            case 'vrcWorldDetail':
                renderWorldSearchDetail(payload);
                break;
            case 'vrcAvatarDetail':
                renderAvatarDetail(payload);
                break;
            case 'vrcAvatarUpdateResult':
                onAvatarUpdateResult(payload);
                break;
            case 'vrcOnlineCount':
                _dashOnlineCount = payload.count || 0;
                updateDashSub();
                break;
            case 'vrcAvatarDetailError':
                { const ac = document.getElementById('avatarDetailContent');
                  if (ac) ac.innerHTML = `<div style="padding:30px;text-align:center;color:var(--err);">${esc(payload.error || 'Error loading avatar')}</div><div style="text-align:center;margin-top:10px;"><button class="vrcn-button-round" onclick="closeAvatarDetail()">Close</button></div>`; }
                break;
            case 'vrcFavoriteWorlds':
                renderFavWorlds(payload);
                break;
            case 'vrcMyWorlds':
                renderMyWorlds(payload);
                break;
            case 'vrcFavoriteGroupUpdated':
                onFavoriteGroupUpdated(payload);
                onAvatarFavoriteGroupUpdated(payload);
                break;
            case 'vrcWorldFavoriteResult':
                onWorldFavoriteResult(payload);
                break;
            case 'vrcWorldUnfavoriteResult':
                onWorldUnfavoriteResult(payload);
                break;
            case 'vrcWorldFavGroups':
                onWorldFavGroupsLoaded(payload);
                break;
            case 'vrcWorldsResolved':
                onWorldsResolved(payload);
                break;
            case 'discoveryFeed':
                onDiscoveryFeed(payload.json);
                break;
            case 'popularWorlds':
                onPopularWorlds(payload.worlds);
                break;
            case 'activeWorlds':
                onActiveWorlds(payload.worlds);
                break;
            case 'vrcWorldDetailError':
                document.getElementById('detailModalContent').innerHTML = `<div style="padding:30px;text-align:center;color:var(--err);">${esc(payload.error || 'Error loading world')}</div><div style="text-align:center;margin-top:10px;"><button class="vrcn-button-round" onclick="document.getElementById('modalDetail').style.display='none'">Close</button></div>`;
                break;
            case 'vrcChatHistory':
                if (typeof handleChatHistory === 'function') handleChatHistory(payload);
                break;
            case 'vrcChatMessage':
                if (typeof handleChatMessage  === 'function') handleChatMessage(payload);
                break;
            case 'vrcChatSlotInfo':
                if (typeof handleChatSlotInfo === 'function') handleChatSlotInfo(payload);
                break;
            case 'vrcNotifications':
                // Merge: keep WS-received notifications not present in the REST response
                // (group announcements / v2 types may not persist in REST endpoint)
                {
                    const restIds = new Set((payload || []).map(n => n.id));
                    const wsOnly = (notifications || []).filter(n => n.id && !restIds.has(n.id));
                    const merged = [...payload, ...wsOnly].sort((a, b) =>
                        (new Date(b.created_at) - new Date(a.created_at)) || 0);
                    renderNotifications(merged);
                    showNotificationToasts(merged);
                }
                break;
            case 'vrcNotificationPrepend':
                // Single notification arrived via WebSocket — prepend to existing list
                notifications = [payload, ...(notifications || []).filter(n => n.id !== payload.id)];
                renderNotifications(notifications);
                showNotificationToasts([payload]);
                break;
            case 'vrcLaunchNeeded':
                showLaunchModal(payload.location, payload.steamVr);
                break;
            case 'vrcInviteMessages':
                handleVrcInviteMessages(payload);
                break;
            case 'vrcInviteMessageUpdateFailed':
                handleVrcInviteMessageUpdateFailed(payload);
                break;
            case 'updateAvailable':   showUpdateAvailable(payload.version); break;
            case 'updateProgress':    onUpdateProgress(payload); break;
            case 'updateReady':       onUpdateReady(); break;
            case 'vrcRefreshNotifs':
                refreshNotifications();
                break;
            case 'vrcUserDetail':
                // Unified: redirect to friend detail modal
                renderFriendDetail(payload);
                break;
            case 'vrcCurrentInstance':
                renderCurrentInstance(payload);
                break;
            case 'vrcWorldJoined':
                // Fired immediately when log watcher detects a new instance join
                requestInstanceInfo();
                break;
            case 'vrcBatchInviteProgress':
                handleBatchInviteProgress(payload);
                break;
            case 'myInstances':
                if (typeof renderMyInstances === 'function') renderMyInstances(payload);
                break;
            case 'dashBgSelected':
                dashBgPath = payload.path || '';
                dashBgDataUri = payload.dataUri || '';
                if (dashBgPath) document.getElementById('dashBgName').textContent = dashBgPath.split(/[\\\\/]/).pop();
                renderDashboard();
                autoSave();
                break;
            case 'chatboxUpdate':
                handleChatboxUpdate(payload);
                break;
            case 'sfUpdate':
                handleSfUpdate(payload);
                break;
            case 'dpState': dpOnState(payload); break;
            case 'vfState': handleVfState(payload); break;
            case 'vfDevices': populateVfDevices(payload); break;
            case 'vfItems': renderVfItems(payload); break;
            case 'vfItemAdded': vfOnItemAdded(payload); break;
            case 'vfSoundAdded': vfOnSoundAdded(payload); break;
            case 'vfMeter': updateVfMeter(payload.level); break;
            case 'vfKeyword': vfOnKeyword(payload.word); break;
            case 'vfRecognized': vfOnRecognized(payload.text, payload.isPartial); break;
            case 'vfBlockList': handleVfBlockList(payload.words); break;
            case 'oscState':
                handleOscState(payload);
                break;
            case 'oscParam':
                handleOscParam(payload);
                break;
            case 'oscAvatarParams':
                handleOscAvatarParams(payload);
                break;
            case 'oscOutputsEnabled':
                handleOscOutputsEnabled(payload);
                break;
            case 'timelineData': renderTimeline(payload); break;
            case 'timelineEvent': handleTimelineEvent(payload); break;
            case 'timelineSearchResults': handleTlSearchResults(payload); break;
            case 'friendTimelineData':          renderFriendTimeline(payload); break;
            case 'friendTimelineEvent':         handleFriendTimelineEvent(payload); break;
            case 'friendTimelineSearchResults': handleFtlSearchResults(payload); break;
            case 'invFiles':
                if (!payload.error) {
                    // Cache by actual tag (not activeInvTab) to prevent cross-contamination
                    if (payload.tag && typeof invFilesCache !== 'undefined') invFilesCache[payload.tag] = payload.files || [];
                    renderInvFiles(payload.files || [], activeInvTab);
                    if (payload.tag === 'gallery' && typeof onGroupPostGalleryLoaded === 'function')
                        onGroupPostGalleryLoaded(payload.files || []);
                    if (payload.tag === 'gallery' && typeof onGroupEventGalleryLoaded === 'function')
                        onGroupEventGalleryLoaded(payload.files || []);
                    if (typeof onImagePickerFilesLoaded === 'function')
                        onImagePickerFilesLoaded(payload.files || [], payload.tag);
                    if (payload.tag === 'gallery' && typeof _invModalOnGalleryLoaded === 'function')
                        _invModalOnGalleryLoaded(payload.files || []);
                } else {
                    const g = document.getElementById('invGrid');
                    if (g) g.innerHTML = `<div class="empty-msg" style="color:var(--err);">Error: ${esc(payload.error)}<br><span style="font-size:11px;color:var(--tx3);">This feature may require VRC+ or a VRChat login.</span></div>`;
                }
                break;
            case 'invPrints':
                if (!payload.error) renderInvPrints(payload.prints || []);
                else {
                    const g2 = document.getElementById('invGrid');
                    if (g2) g2.innerHTML = `<div class="empty-msg" style="color:var(--err);">Error: ${esc(payload.error)}</div>`;
                }
                break;
            case 'invUploadResult': handleInvUploadResult(payload); break;
            case 'invDeleteResult': handleInvDeleteResult(payload); break;
            case 'invPrintDeleteResult': handleInvPrintDeleteResult(payload); break;
            case 'invInventory': handleInvInventoryResult(payload); break;
            case 'vrcMutualsForNetwork':
                if (typeof networkAddMutuals === 'function') networkAddMutuals(payload);
                break;
            case 'vrcMutualCacheLoaded':
                if (typeof networkCacheLoaded === 'function') networkCacheLoaded(payload.json);
                break;
            case 'vrcTimeSpentData':
                if (typeof tsOnData === 'function') tsOnData(payload);
                break;
        case 'setPlatform':
            if (payload?.isLinux) {
                document.querySelectorAll('[data-windows-only]').forEach(el => el.style.display = 'none');
                const lbl = document.getElementById('labelStartWithSystem');
                if (lbl) lbl.textContent = 'Auto-start VRCNext with system';
                const vrcPathInput = document.getElementById('setVrcPath');
                if (vrcPathInput) { vrcPathInput.value = 'steam://rungameid/438100'; vrcPathInput.readOnly = true; vrcPathInput.style.opacity = '0.5'; }
                const browseVrcBtn = document.getElementById('browseVrcBtn');
                if (browseVrcBtn) browseVrcBtn.style.display = 'none';
            }
            break;
        case 'ftAlsoWasHere':
            renderFtAlsoWasHereResult(payload);
            break;
        case 'vrcxPreview':
            vrcxShowPreview(payload);
            break;
        case 'vrcxImportProgress':
            vrcxShowProgress(payload);
            break;
        case 'vrcxImportDone':
            vrcxShowDone(payload);
            break;
        case 'vrcxImportError':
            vrcxShowError(payload?.error);
            break;
    }
});
sendToCS({ action: 'ready' });
