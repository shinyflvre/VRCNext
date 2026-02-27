/* === WebView message handler === */
if (window.chrome?.webview) {
    window.chrome.webview.addEventListener('message', e => {
        const { type, payload } = e.data;
        switch (type) {
            case 'loadSettings': loadSettingsToUI(payload); break;
            case 'relayState': setRelayState(payload.running, payload.streams); break;
            case 'log': addLog(payload.msg, payload.color); break;
            case 'vcState': handleVcState(payload); break;
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
            case 'libraryFileDeleted':
                libraryFiles = libraryFiles.filter(f => f.path !== payload.path);
                filterLibrary();
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
                requestInstanceInfo();
                refreshNotifications();
                break;
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
                document.getElementById('friendDetailContent').innerHTML = `<div class="fd-loading" style="color:var(--err);">${esc(payload.error || 'Error loading profile')}</div><div style="margin-top:10px;text-align:right;"><button class="modal-btn modal-btn-cancel" onclick="closeFriendDetail()">Close</button></div>`;
                break;
            case 'vrcActionResult': showFriendActionToast(payload.success, payload.message); break;
            case 'vrcProfileUpdated':
                if (payload.success) {
                    renderMyProfileContent();
                    showMypToast(true, 'Saved!');
                } else {
                    document.querySelectorAll('#mypBox .myp-save-btn').forEach(b => b.disabled = false);
                    showMypToast(false, payload.error || 'Update failed');
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
            case 'vrcAvatars':
                if (payload.filter === 'own') avatarsData = payload.avatars || [];
                else avatarFavData = payload.avatars || [];
                avatarsLoaded = true;
                if (payload.currentAvatarId) currentAvatarId = payload.currentAvatarId;
                renderAvatarGrid();
                break;
            case 'vrcAvatarSelected':
                if (payload.avatarId) currentAvatarId = payload.avatarId;
                document.querySelectorAll('.av-card').forEach(c => { c.style.pointerEvents = ''; c.style.opacity = ''; });
                renderAvatarGrid();
                break;
            case 'vrcSearchResults':
                renderSearchResults(payload.type, payload.results, payload.offset || 0, payload.hasMore || false);
                break;
            case 'vrcMyGroups':
                renderMyGroups(payload);
                break;
            case 'vrcGroupDetail':
                renderGroupDetail(payload);
                break;
            case 'vrcGroupDetailError':
                document.getElementById('detailModalContent').innerHTML = `<div style="padding:30px;text-align:center;color:var(--err);">${esc(payload.error || 'Error loading group')}</div><div style="text-align:center;margin-top:10px;"><button class="modal-btn modal-btn-cancel" onclick="document.getElementById('modalDetail').style.display='none'">Close</button></div>`;
                break;
            case 'vrcGroupMembersPage':
                {
                    const list = document.getElementById('gdMembersList');
                    if (list && payload.members) {
                        payload.members.forEach(m => { list.insertAdjacentHTML('beforeend', renderGroupMemberCard(m)); });
                        window._gdMembersOffset = (window._gdMembersOffset || 0) + payload.members.length;
                    }
                    const loadMoreDiv = document.getElementById('gdMembersLoadMore');
                    if (loadMoreDiv) {
                        if (payload.hasMore) {
                            loadMoreDiv.innerHTML = '<button class="btn-f" onclick="loadMoreGroupMembers()">Load More Members</button>';
                        } else {
                            loadMoreDiv.innerHTML = '<div style="font-size:11px;color:var(--tx3);padding:6px;">All members loaded</div>';
                        }
                    }
                }
                break;
            case 'vrcWorldDetail':
                renderWorldSearchDetail(payload);
                break;
            case 'vrcFavoriteWorlds':
                renderFavWorlds(payload);
                break;
            case 'vrcWorldsResolved':
                onWorldsResolved(payload);
                break;
            case 'vrcWorldDetailError':
                document.getElementById('detailModalContent').innerHTML = `<div style="padding:30px;text-align:center;color:var(--err);">${esc(payload.error || 'Error loading world')}</div><div style="text-align:center;margin-top:10px;"><button class="modal-btn modal-btn-cancel" onclick="document.getElementById('modalDetail').style.display='none'">Close</button></div>`;
                break;
            case 'vrcNotifications':
                renderNotifications(payload);
                break;
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
            case 'dashBgSelected':
                dashBgPath = payload.path || '';
                dashBgDataUri = payload.dataUri || '';
                if (dashBgPath) document.getElementById('dashBgName').textContent = dashBgPath.split(/[\\\\/]/).pop();
                renderDashboard();
                break;
            case 'chatboxUpdate':
                handleChatboxUpdate(payload);
                break;
            case 'sfUpdate':
                handleSfUpdate(payload);
                break;
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
            case 'friendTimelineData':  renderFriendTimeline(payload); break;
            case 'friendTimelineEvent': handleFriendTimelineEvent(payload); break;
            case 'invFiles':
                if (!payload.error) renderInvFiles(payload.files || [], activeInvTab);
                else {
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
        }
    });
    sendToCS({ action: 'ready' });
}
