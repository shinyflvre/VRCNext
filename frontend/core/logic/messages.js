/* === Photino message handler === */
window.external.receiveMessage(rawMsg => {
    const { type, payload } = JSON.parse(rawMsg);
    // Forward every incoming event to Action Flow's websocket-trigger router.
    // Fire-and-forget; any errors must not break the main handler.
    if (typeof window.__afOnWebsocketEvent === 'function') {
        try { window.__afOnWebsocketEvent(type, payload); } catch (e) { console.error('[ActionFlow] ws hook', e); }
    }
    switch (type) {
            case 'openDeepLink':
                if (!payload) break;
                if (payload.prefix === 'avtr' && (payload.action || '').startsWith('put/fav')) {
                    if (typeof deepLinkFavoriteAvatar === 'function')
                        deepLinkFavoriteAvatar(payload.id, payload.action.slice('put/fav'.length));
                } else if (typeof msgrContentOpen === 'function') {
                    msgrContentOpen(payload.id, payload.prefix);
                }
                break;
            case 'translationData': handleTranslationData(payload); break;
            case 'loadSettings':
                loadSettingsToUI(payload);
                if (typeof requestAccountsList === 'function') requestAccountsList();
                sendToCS({ action: 'hypeRateGetState' });
                if (!window.__vrcndbConsentChecked) {
                    window.__vrcndbConsentChecked = true;
                    const shown = payload.VrcndbConsentShown ?? payload.vrcndbConsentShown ?? false;
                    if (!shown && typeof showVrcndbConsent === 'function') showVrcndbConsent();
                }
                break;
            // Multi-Account events.
            case 'accountsList':
                if (typeof renderAccountsList === 'function') renderAccountsList(payload);
                break;
            case 'accountAddNeeds2FA':
                if (typeof show2FAModal === 'function') show2FAModal(payload.twoFactorType || 'totp', 'addAccount');
                break;
            case 'accountAddSuccess': {
                if (typeof hideAddAccountForm === 'function') hideAddAccountForm();
                if (typeof requestAccountsList === 'function') requestAccountsList();
                const m = document.getElementById('modal2FA');
                if (m) m.style.display = 'none';
                break;
            }
            case 'accountAddError': {
                const s = document.getElementById('addAccountStatus');
                if (s) s.textContent = payload && payload.error ? payload.error : t('settings.accounts.add_error_login_failed', 'Login failed');
                break;
            }
            case 'accountSwitchStarting':
                if (typeof showLoadingOverlay === 'function') showLoadingOverlay(t('settings.accounts.switching', 'Switching account, restarting...'));
                break;
            case 'accountSwitchError':
                if (typeof hideLoadingOverlay === 'function') hideLoadingOverlay();
                if (typeof requestAccountsList === 'function') requestAccountsList();
                if (typeof showToast === 'function') showToast(t('settings.accounts.switch_error', 'Account switch failed') + (payload && payload.reason ? ': ' + payload.reason : ''));
                break;
            case 'accountSwitchInProgress':
                if (typeof showToast === 'function') showToast(t('settings.accounts.switch_in_progress', 'Account switch in progress, action rejected') + (payload && payload.rejectedAction ? ': ' + payload.rejectedAction : ''));
                break;
            case 'dateTimeFormat': applyDateTimeFormat(payload); break;
            case 'systemFonts':
                loadSystemFonts(payload?.fonts);
                break;
            case 'ttsDevices':
                if (typeof vroPopulateTtsDevices === 'function') vroPopulateTtsDevices(payload || {});
                break;
            case 'cursorFiles': _localHttpPort = payload.port || _localHttpPort; renderCursorThemeChips(payload.files); applyCursorTheme(currentCursorTheme); break;
            case 'customThemes': _localHttpPort = payload.port || _localHttpPort; _customThemes = payload.themes || []; applyCustomThemesFromSettings([..._activeCustomThemes]); break;
            case 'vrcLaunched': {
                // Fired when a VRChat session starts (VRCNext launch or detected externally)
                if (window._isLinuxUi) break;
                const vr = !!payload.vr;
                const chk = id => !!document.getElementById(id)?.checked;
                const session = (window._autoStartToken = (window._autoStartToken || 0) + 1);
                let delay = 300;
                const ensure = (cond, isRunning, start) => {
                    if (!cond) return;
                    const first = delay; delay += 100;
                    let tries = 0;
                    const tick = () => {
                        if (window._autoStartToken !== session) return;
                        if (isRunning()) return;
                        if (tries >= 5) return;
                        tries++;
                        start();
                        setTimeout(tick, 5000);
                    };
                    setTimeout(tick, first);
                };
                // Chatbox
                ensure(vr ? chk('setCbAutoStartVR') : chk('setCbAutoStartDesktop'),
                    () => typeof chatboxEnabled !== 'undefined' && chatboxEnabled,
                    () => toggleChatbox());
                // Space Flight (VR only)
                ensure(vr && chk('setSfAutoStartVR'),
                    () => typeof sfConnected !== 'undefined' && sfConnected,
                    () => sfConnect());
                // Space Turn (VR only)
                ensure(vr && chk('setStAutoStartVR'),
                    () => typeof stConnected !== 'undefined' && stConnected,
                    () => { if (typeof stConnect === 'function') stConnect(); });
                // FrameShot (VR only)
                ensure(vr && chk('setFsAutoStartVR'),
                    () => typeof fsConnected !== 'undefined' && fsConnected,
                    () => { if (typeof fsConnect === 'function') fsConnect(); });
                // Media Relay
                ensure(vr ? chk('setAutoStartVR') : chk('setAutoStartDesktop'),
                    () => typeof relayOn !== 'undefined' && relayOn,
                    () => sendToCS({ action: 'startRelay' }));
                // YouTube Fix
                ensure(vr ? chk('setYtAutoStartVR') : chk('setYtAutoStartDesktop'),
                    () => !!document.getElementById('vcDot')?.classList.contains('online'),
                    () => { if (typeof toggleVc === 'function') toggleVc(); });
                // Voice Fight
                ensure(vr ? chk('setVfAutoStartVR') : chk('setVfAutoStartDesktop'),
                    () => typeof vfRunning !== 'undefined' && vfRunning,
                    () => { if (typeof vfConnect === 'function') vfConnect(); });
                // Discord Presence
                ensure(vr ? chk('setDpAutoStartVR') : chk('setDpAutoStartDesktop'),
                    () => typeof _dpRunning !== 'undefined' && _dpRunning,
                    () => sendToCS({ action: 'dpStart' }));
                // VR Overlay (VR only)
                ensure(vr && chk('setVroAutoStartVR'),
                    () => typeof vroConnected !== 'undefined' && vroConnected,
                    () => { if (typeof vroConnect === 'function') vroConnect(); });
                // Avatar Scaling
                ensure(vr ? chk('setAsAutoStartVR') : chk('setAsAutoStartDesktop'),
                    () => typeof _asConnected !== 'undefined' && _asConnected,
                    () => sendToCS({ action: 'asConnect' }));
                break;
            }
            case 'vrcClosed': {
                window._autoStartToken = (window._autoStartToken || 0) + 1;
                // Fired when VRChat closes and "Close with VRChat" is enabled.
                // Mirror of vrcLaunched: shut down the VRCNext tools that are running.
                let delay = 0;
                const stop = fn => { setTimeout(fn, delay); delay += 80; };
                // Chatbox
                if (typeof chatboxEnabled !== 'undefined' && chatboxEnabled) stop(() => toggleChatbox());
                // Space Flight
                if (typeof sfConnected !== 'undefined' && sfConnected) stop(() => sendToCS({ action: 'sfDisconnect' }));
                // Space Turn
                if (typeof stConnected !== 'undefined' && stConnected) stop(() => sendToCS({ action: 'stDisconnect' }));
                // FrameShot
                if (typeof fsConnected !== 'undefined' && fsConnected) stop(() => sendToCS({ action: 'fsDisconnect' }));
                // Media Relay
                if (typeof relayOn !== 'undefined' && relayOn) stop(() => sendToCS({ action: 'stopRelay' }));
                // YouTube Fix (VRCVideoCacher)
                if (document.getElementById('vcDot')?.classList.contains('online')) stop(() => sendToCS({ action: 'vcStop' }));
                // Voice Fight
                if (typeof vfRunning !== 'undefined' && vfRunning) stop(() => sendToCS({ action: 'vfStop' }));
                // Discord Presence
                if (typeof _dpRunning !== 'undefined' && _dpRunning) stop(() => sendToCS({ action: 'dpStop' }));
                // VR Overlay
                if (typeof vroConnected !== 'undefined' && vroConnected) stop(() => sendToCS({ action: 'vroDisconnect' }));
                // Avatar Scaling
                if (typeof _asConnected !== 'undefined' && _asConnected) stop(() => sendToCS({ action: 'asDisconnect' }));
                break;
            }
            case 'asState': if (typeof handleAsState === 'function') handleAsState(payload); break;
            case 'relayState': setRelayState(payload.running, payload.streams); break;
            case 'imgCacheSize':             updateImgCacheSizeBar(payload.bytes); break;
            case 'imgCacheOptimizeProgress': handleImgCacheOptimizeProgress(payload); break;
            case 'dbAnalyzeProgress': handleDbAnalyzeProgress(payload); break;
            case 'dbAnalyzeResult':   handleDbAnalyzeResult(payload); break;
            case 'dbMemoryResult':    handleDbMemoryResult(payload); break;
            case 'dbOptimizeProgress':handleDbOptimizeProgress(payload); break;
            case 'dbOptimizeDone':    handleDbOptimizeDone(payload); break;
            case 'dbBackupDone':      handleDbBackupDone(payload); break;
            case 'regBackupDone':     handleRegBackupDone(payload); break;
            case 'log': addLog(payload.msg, payload.color); break;
            case 'consoleOutput': addLog(payload.text, payload.color); break;
            case 'debugImgCacheState':
                if (typeof setImgCacheDebug === 'function') setImgCacheDebug(payload.enabled);
                break;
            case 'debugAvatarLookupState':
                window.avatarLookupDebug = !!payload.enabled;
                break;
            case 'toast': showToast(payload.ok, payload.msg); break;
            case 'vrcConfigData':
                if (typeof _vrcCfgApplyData === 'function') _vrcCfgApplyData(payload || {});
                break;
            case 'vrcLaunchOptionsData':
                if (typeof _vrcLaApplyData === 'function') _vrcLaApplyData(payload || {});
                break;
            case 'showCrashModal': showCrashModal(payload); break;
            case 'wsStatus': {
                const badge = document.getElementById('wsBadge');
                if (badge) {
                    badge.className = 'log-ws ' + (payload.connected ? 'online' : 'offline');
                    badge.querySelector('.log-ws-icon').textContent = payload.connected ? 'wifi' : 'wifi_off';
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
            case 'filePosted': addFileToList(payload); playMediaRelaySound(); break;
            case 'deleteResult':
                if (payload.success) {
                    postedFiles = postedFiles.filter(f => f.messageId !== payload.messageId);
                    renderFileList();
                    addLog(t('messages.relay.deleted_from_discord', 'Deleted from Discord'), 'ok');
                } else addLog(t('messages.delete_failed', 'Delete failed'), 'err');
                break;
            case 'folderPicked': {
                const el = document.getElementById(payload.target || '');
                if (el && payload.path) el.value = payload.path;
                break;
            }
            case 'folderAdded':
                if (!settings.folders) settings.folders = [];
                settings.folders.push(payload);
                renderFolders(settings.folders);
                updateFolderFilterOptions(settings.folders);
                autoSave();
                break;
            case 'libraryData': renderLibrary(payload); renderDashRecentPhotos(); break;
            case 'libraryPageData': appendLibraryPage(payload); break;
            case 'libraryWorldIds': applyLibraryWorldIds(payload); break;
            case 'libraryAuthors': applyLibraryAuthors(payload); break;
            case 'libraryRatings': if (typeof onLibraryRatings === 'function') onLibraryRatings(payload); break;
            case 'mediaTagsData': if (typeof onMediaTagsData === 'function') onMediaTagsData(payload); break;
            case 'mediaTagsUpdated': if (typeof onMediaTagsUpdated === 'function') onMediaTagsUpdated(payload); break;
            case 'mediaUserTagsUpdated': if (typeof onMediaUserTagsUpdated === 'function') onMediaUserTagsUpdated(payload); break;
            case 'photoRating':
            case 'photoRatingSet':
                if (typeof onPhotoRating === 'function') onPhotoRating(payload);
                break;
            case 'libraryNewFile': addNewLibraryFile(payload); renderDashRecentPhotos(); break;
            case 'libraryFileDeleted':
                onLibraryFileDeleted(payload.path);
                renderDashRecentPhotos();
                break;
            case 'favoritesLoaded':
                favorites = new Set(payload || []);
                filterLibrary();
                break;
            case 'exeAdded':
                if (payload.target === 'vrchat') {
                    var _setVrcPathEl = document.getElementById('setVrcPath');
                    if (_setVrcPathEl) _setVrcPathEl.value = payload.path;
                    var _vrcLaPathEl = document.getElementById('vrcLaPath');
                    var _laModal = document.getElementById('modalVrcLaunchOptions');
                    if (_vrcLaPathEl && _laModal && _laModal.style.display !== 'none') {
                        _vrcLaPathEl.value = payload.path;
                    }
                } else if (payload.target === 'extra-desktop') {
                    if (!settings.extraExeDesktop) settings.extraExeDesktop = [];
                    settings.extraExeDesktop.push(payload.path);
                    renderExtraExeDesktop(settings.extraExeDesktop);
                } else if (payload.target === 'extra-vr') {
                    if (!settings.extraExeVR) settings.extraExeVR = [];
                    settings.extraExeVR.push(payload.path);
                    renderExtraExeVR(settings.extraExeVR);
                }
                autoSave();
                break;
            case 'vrcProfileBackgroundUpdated':
                if (typeof onProfileBackgroundUpdated === 'function') onProfileBackgroundUpdated(payload);
                break;
            case 'vrcProfileBannerUpdated':
                if (typeof onProfileBannerUpdated === 'function') onProfileBannerUpdated(payload);
                break;
            case 'vrcProfileThemeSaved':
                if (typeof onProfileThemeSaved === 'function') onProfileThemeSaved(payload);
                break;
            case 'vrcProfileThemeDeleted':
                if (typeof onProfileThemeDeleted === 'function') onProfileThemeDeleted(payload);
                break;
            case 'vrcActiveProfileThemeSet':
                break;
            case 'vrcSelfAppearance':
                if (typeof currentVrcUser !== 'undefined' && currentVrcUser) {
                    Object.assign(currentVrcUser, payload);
                    if (document.getElementById('modalMyProfile')?.style.display !== 'none'
                        && typeof renderMyProfileContent === 'function') renderMyProfileContent();
                }
                break;
            case 'vrcRunningChanged':
                if (typeof onVrcRunningChanged === 'function') onVrcRunningChanged(payload);
                break;
            case 'vrcUser':
                renderVrcProfile(payload);
                if (currentInstanceData) renderCurrentInstance(currentInstanceData);
                if (payload.currentAvatar) currentAvatarId = payload.currentAvatar;
                // Login state is reflected in the Accounts tab now that the old login card is gone.
                if (typeof requestAccountsList === 'function') requestAccountsList();
                if (typeof updateTbAppUserHeader === 'function') updateTbAppUserHeader();
                document.getElementById('modal2FA').style.display = 'none';
                if (!vrcFriendsLoaded) {
                    const fsl = document.getElementById('vrcFriendsList');
                    if (fsl) fsl.innerHTML = `<div class="vrc-section-label">${t('profiles.friends.sections.loading', 'IN-GAME - ...')}</div>` + sk('friend', 10);
                }
                renderDashboard();
                loadMyInstances();
                requestInstanceInfo();
                refreshNotifications();
                if (typeof libraryFiles !== 'undefined' && !libraryFiles.length && typeof sendToCS === 'function') sendToCS({ action: 'scanLibrary' });
                { _hasVrcPlus = Array.isArray(payload.tags) && payload.tags.includes('system_supporter');
                  applyTbBadgeVisibility(); }
                if (!window._lastModerationFetch || Date.now() - window._lastModerationFetch >= 120 * 60 * 1000) {
                    window._lastModerationFetch = Date.now();
                    sendToCS({ action: 'vrcGetAllModerations' });
                }
                break;
            case 'vrcCredits': {
                const bc = document.getElementById('badgeVrcCredits');
                const bl = document.getElementById('badgeVrcCreditsLabel');
                if (bc && bl) { bl.textContent = payload.balance.toLocaleString(); _hasVrcCredits = true; applyTbBadgeVisibility(); }
                break;
            }
            case 'vrcMyBadges':
                if (currentVrcUser && payload.badges) {
                    currentVrcUser.badges = payload.badges;
                    const myp = document.getElementById('modalMyProfile');
                    if (myp && myp.style.display !== 'none') renderMyProfileContent();
                }
                break;
            case 'vrcBadgeUpdated':
                if (currentVrcUser?.badges && payload.badgeId) {
                    const b = currentVrcUser.badges.find(x => x.id === payload.badgeId);
                    if (b && payload.success) {
                        b.showcased = payload.showcased;
                        const wrap = document.querySelector(`.myp-badge-item[data-badge-id="${payload.badgeId}"]`);
                        if (wrap) wrap.classList.toggle('myp-badge-hidden', !payload.showcased);
                    }
                }
                break;
            case 'vrcFriendUpdate': {
                const idx = vrcFriendsData.findIndex(f => f.id === payload.id);
                const prevFriend = idx >= 0 ? vrcFriendsData[idx] : null;
                if (idx >= 0) vrcFriendsData[idx] = payload;
                else vrcFriendsData.push(payload);
                if (!(typeof tryPatchVrcFriendCard === 'function' && tryPatchVrcFriendCard(prevFriend, payload)))
                    scheduleRenderVrcFriends();
                if (favFriendsData.length > 0) filterFavFriendsIfVisible();
                if (typeof filterAllFriendsIfLive === 'function') filterAllFriendsIfLive();
                if (typeof updateUserItemWorld === 'function') updateUserItemWorld(payload);
                if (typeof patchFriendDetailLive === 'function') patchFriendDetailLive(payload);
                if (typeof scheduleRenderDashboardFriendSections === 'function') scheduleRenderDashboardFriendSections();
                break;
            }
            case 'vrcFriends':
                vrcFriendsLoaded = true;
                if (typeof friendFetchInit === 'function') friendFetchInit();
                if (payload.friends) {
                    vrcFriendsData = payload.friends;
                    _rvfPendingCounts = payload.counts || null;
                } else {
                    vrcFriendsData = payload;
                    _rvfPendingCounts = null;
                }
                scheduleRenderVrcFriends();
                requestWorldResolution(); renderDashboardFriendSections(); requestInstanceInfo();
                if (currentInstanceData) renderCurrentInstance(currentInstanceData);
                if (favFriendsData.length > 0) filterFavFriendsIfVisible();
                if (typeof filterAllFriendsIfLive === 'function') filterAllFriendsIfLive();
                break;
            case 'vrcProfileDecorations':
                if (typeof onProfileDecorations === 'function') onProfileDecorations(payload);
                break;
            case 'vrcSetProfileDecorationResult':
                if (typeof onSetProfileDecorationResult === 'function') onSetProfileDecorationResult(payload);
                break;
            case 'vrcNeeds2FA': show2FAModal(payload.type || 'totp'); break;
            case 'vrcLoginError':
                {
                    const err = payload.error || t('profiles.login.failed', 'Login failed');
                    const _e1 = document.getElementById('modal2FAError'); if (_e1) _e1.textContent = err;
                    const _e2 = document.getElementById('vrcQuickError'); if (_e2) _e2.textContent = err;
                    const _e3 = document.getElementById('reloginStatus'); if (_e3) _e3.textContent = err;
                }
                break;
            case 'vrcLoggedOut':
                vrcFriendsLoaded = false;
                renderVrcProfile(null);
                { const _fl = document.getElementById('vrcFriendsList'); _fl.innerHTML = ''; _fl.__lastHtml = null; }
                { _hasVrcPlus = false; _hasVrcCredits = false; applyTbBadgeVisibility(); }
                if (typeof updateTbAppUserHeader === 'function') updateTbAppUserHeader();
                break;
            case 'vrcPrefillLogin':
                if (payload.username) {
                    document.getElementById('vrcQuickUser').value = payload.username;
                    document.getElementById('vrcQuickPass').value = payload.password || '';
                }
                break;
            case 'vrcFriendDetail':
                renderFriendDetail(payload);
                if (typeof patchFriendProfileFacts === 'function') patchFriendProfileFacts(payload);
                break;
            case 'vrcFriendFetchProgress':
                if (typeof onFriendFetchProgress === 'function') onFriendFetchProgress(payload);
                break;
            case 'vrcFriendFacts':
                if (typeof applyFriendFacts === 'function') applyFriendFacts(payload);
                break;
            case 'vrcFriendPreview': if (typeof handleFriendPreview === 'function') handleFriendPreview(payload); break;
            case 'vrcUserBasic':
                if (typeof handleUserBasic === 'function') handleUserBasic(payload);
                if (typeof pinsOnUserBasic === 'function') pinsOnUserBasic(payload);
                if (typeof tlOnCreatorResolved === 'function') tlOnCreatorResolved(payload);
                if (typeof commentsOnUserBasic === 'function') commentsOnUserBasic(payload);
                break;
            case 'commentsApiResult':
                if (typeof commentsApiResult === 'function') commentsApiResult(payload);
                break;
            case 'vrcAvatarByFileId': handleAvatarByFileId(payload); break;
            case 'vrcAvatarInfo':
                if (payload.context === 'myprofile') { if (typeof onMypAvatarInfo === 'function') onMypAvatarInfo(payload); }
                else handleAvatarByFileId(payload);
                break;
            case 'vrcInstanceAvatarFound': handleInstanceAvatarFound(payload); break;
            case 'vrcFavoriteFriends': renderFavFriends(payload); break;
            case 'vrcFavoriteFriendToggled': handleFavFriendToggled(payload); break;
            case 'vrcFriendDetailError':
                document.getElementById('friendDetailContent').innerHTML = `<div class="fd-loading" style="color:var(--err);">${esc(payload.error || t('profiles.friend_detail.error_loading', 'Error loading profile'))}</div><div style="margin-top:10px;text-align:right;"><button class="vrcn-button-round" onclick="closeFriendDetail()">${t('common.close', 'Close')}</button></div>`;
                break;
            case 'vrcActionResult':
                if (payload.action === 'sendChatMessage') { if (typeof handleChatActionResult === 'function') handleChatActionResult(payload); break; }
                if (payload.action === 'boop') { showToast(payload.success, payload.message); break; }
                if (payload.action === 'hideNotif') {
                    if (!payload.success) showToast(false, t('notifications.toast.decline_failed', 'Could not decline notification'));
                    break;
                }
                if (payload.action === 'representGroup') {
                    showToast(payload.success, payload.message);
                    if (payload.success && payload.groupId && typeof myGroups !== 'undefined') {
                        myGroups.forEach(g => { g.isRepresenting = (g.id === payload.groupId); });
                        const myp = document.getElementById('modalMyProfile');
                        if (myp && myp.style.display !== 'none') renderMyProfileContent();
                        if (typeof filterMyGroups === 'function') filterMyGroups();
                    }
                    break;
                }
                if (payload.action === 'createInstance') {
                    showToast(payload.success, payload.message);
                    if (payload.success) {
                        loadMyInstances();
                        closeCreateInstanceModal();
                    } else {
                        const cb = document.getElementById('ciCreateBtn');
                        const jb = document.getElementById('ciCreateJoinBtn');
                        if (cb) { cb.disabled = false; cb.innerHTML = `<span class="msi" style="font-size:14px;">add_circle_outline</span> Create Instance`; }
                        if (jb) { jb.disabled = false; jb.innerHTML = `<span class="msi" style="font-size:14px;">play_circle</span> Create &amp; Join`; }
                    }
                    break;
                }
                if (payload.action === 'deleteGroupEvent') {
                    if (payload.success) {
                        const card = document.querySelector(`.fd-group-card[data-event-id="${payload.eventId}"]`);
                        if (card) card.remove();
                        showToast(true, t('groups.toast.event_deleted', 'Group event deleted.'));
                    } else {
                        const card = document.querySelector(`.fd-group-card[data-event-id="${payload.eventId}"]`);
                        if (card) {
                            card.style.opacity = '';
                            card.style.pointerEvents = '';
                            const btn = card.querySelector('.gd-post-del');
                            if (btn) { btn.disabled = false; btn.querySelector('.msi').textContent = 'delete'; }
                        }
                        showToast(false, t('messages.delete_failed', 'Delete failed.'));
                    }
                } else if (payload.action === 'deleteGroupPost') {
                    if (payload.success) {
                        const card = document.querySelector(`.fd-group-card[data-post-id="${payload.postId}"]`);
                        if (card) card.remove();
                        showToast(true, t('groups.toast.post_deleted', 'Group post deleted.'));
                    } else {
                        const card = document.querySelector(`.fd-group-card[data-post-id="${payload.postId}"]`);
                        if (card) {
                            card.style.opacity = '';
                            card.style.pointerEvents = '';
                            const btn = card.querySelector('.gd-post-del');
                            if (btn) { btn.disabled = false; btn.querySelector('.msi').textContent = 'delete'; }
                        }
                        showToast(false, t('messages.delete_failed', 'Delete failed.'));
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
                            bannedList.querySelectorAll('.vrcn-user-item').forEach(card => {
                                if ((card.getAttribute('onclick') || '').includes(payload.userId)) card.remove();
                            });
                            if (!bannedList.querySelector('.vrcn-user-item'))
                                bannedList.innerHTML = `<div style="padding:20px;text-align:center;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('groups.empty.no_banned_members', 'No banned members')}</div>`;
                        }
                    }
                } else {
                    if (payload.action === 'leaveGroup' && typeof groupBulkLeaveConsume === 'function'
                        && groupBulkLeaveConsume(payload.success)) break;
                    showToast(payload.success, payload.message);
                    // Auto-refresh groups list on join/leave (from anywhere in the app)
                    if (payload.success && (payload.action === 'joinGroup' || payload.action === 'leaveGroup' || payload.groupJoined)) {
                        loadMyGroups();
                    }
                    if (payload.action === 'joinGroup' && typeof onChangelogJoinResult === 'function') {
                        onChangelogJoinResult(payload.success);
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
                        : `<div class="myp-empty">${t('groups.empty.no_description', 'No description')}</div>`;
                    if (rv && payload.rules != null) rv.innerHTML = payload.rules
                        ? `<div style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx2);padding:8px;background:var(--bg-input);border-radius:8px;max-height:120px;overflow-y:auto;white-space:pre-wrap;">${esc(payload.rules)}</div>`
                        : `<div class="myp-empty">${t('groups.empty.no_rules', 'No rules set')}</div>`;
                    if (lnv && payload.links != null) {
                        const links = (payload.links || []).filter(Boolean);
                        lnv.innerHTML = links.length
                            ? `<div class="fd-bio-links">${links.map(url => renderBioLink(url)).join('')}</div>`
                            : `<div class="myp-empty">${t('profiles.my_profile.empty.no_links', 'No links added')}</div>`;
                    }
                    if (lav && payload.languages != null) {
                        const langs = payload.languages || [];
                        lav.innerHTML = langs.length
                            ? `<div class="fd-lang-tags">${langs.map(l => `<span class="vrcn-badge">${esc(LANG_MAP['language_'+l] || l.toUpperCase())}</span>`).join('')}</div>`
                            : `<div class="myp-empty">${t('profiles.my_profile.empty.no_languages', 'No languages set')}</div>`;
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
                    showToast(true, t('groups.updated', 'Group updated!'));
                } else {
                    document.querySelectorAll('#gdescDescEdit .vrcn-btn-primary, #gdescRulesEdit .vrcn-btn-primary, #ggrpLinksEdit .vrcn-btn-primary, #ggrpLangsEdit .vrcn-btn-primary, #ggrpJoinStateEdit .vrcn-btn-primary').forEach(b => b.disabled = false);
                    showToast(false, t('common.update_failed', 'Update failed'));
                }
                break;
            case 'vrcProfileUpdated':
                if (payload.success) {
                    renderMyProfileContent();
                    showToast(true, t('profiles.my_profile.saved', 'Saved!'));
                } else {
                    document.querySelectorAll('#mypBox .vrcn-btn-primary').forEach(b => b.disabled = false);
                    showToast(false, payload.error || t('common.update_failed', 'Update failed'));
                }
                break;
            case 'vrcNoteUpdated':
                if (payload.success && currentFriendDetail?.id === payload.userId) {
                    if (currentFriendDetail) currentFriendDetail.note = payload.note;
                    const view = document.getElementById('fdVrcNoteView');
                    if (view) {
                        view.innerHTML = payload.note
                            ? `<div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx1);line-height:1.5;">${esc(payload.note)}</div>`
                            : `<div class="myp-empty">${t('profiles.notes.no_note', 'No notes added yet')}</div>`;
                    }
                    fdCancelNote();
                } else if (!payload.success) {
                    const btn = document.getElementById('fdVrcNoteSaveBtn');
                    if (btn) btn.disabled = false;
                    showToast(false, t('profiles.notes.failed', 'Failed to save note'));
                }
                break;
            case 'vrcUnfriendDone':
                if (typeof friendBulkUnfriendConsume === 'function' && friendBulkUnfriendConsume()) break;
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
            case 'vrcHideAvatarList':
                hiddenAvatarData = Array.isArray(payload) ? payload : [];
                break;
            case 'vrcInteractOffList':
                interactOffData = Array.isArray(payload) ? payload : [];
                break;
            case 'vrcMuteChatList':
                muteChatData = Array.isArray(payload) ? payload : [];
                break;
            case 'vrcModDone': {
                const { userId: modUid, type: modType, active: modActive } = payload;
                const modName = (currentFriendDetail && currentFriendDetail.id === modUid)
                    ? (currentFriendDetail.displayName || modUid)
                    : modUid;
                const modImage = (currentFriendDetail && currentFriendDetail.id === modUid)
                    ? (currentFriendDetail.image || '') : '';
                const _modEntry = { targetUserId: modUid, targetDisplayName: modName, image: modImage };
                if (modType === 'block') {
                    if (modActive) {
                        if (!Array.isArray(blockedData)) blockedData = [];
                        if (!blockedData.some(e => e.targetUserId === modUid)) blockedData.push(_modEntry);
                    } else {
                        blockedData = (blockedData || []).filter(e => e.targetUserId !== modUid);
                    }
                    renderModList('blockedList', blockedData, 'block');
                } else if (modType === 'mute') {
                    if (modActive) {
                        if (!Array.isArray(mutedData)) mutedData = [];
                        if (!mutedData.some(e => e.targetUserId === modUid)) mutedData.push(_modEntry);
                    } else {
                        mutedData = (mutedData || []).filter(e => e.targetUserId !== modUid);
                    }
                    renderModList('mutedList', mutedData, 'mute');
                } else if (modType === 'hideAvatar') {
                    if (modActive) {
                        if (!hiddenAvatarData.some(e => e.targetUserId === modUid)) hiddenAvatarData.push(_modEntry);
                        showToast(true, t('context_menu.friend.hide_avatar', 'Hide Avatar'));
                    } else {
                        hiddenAvatarData = hiddenAvatarData.filter(e => e.targetUserId !== modUid);
                        showToast(true, t('context_menu.friend.show_avatar', 'Show Avatar'));
                    }
                } else if (modType === 'interactOff') {
                    if (modActive) {
                        if (!interactOffData.some(e => e.targetUserId === modUid)) interactOffData.push(_modEntry);
                        showToast(true, t('context_menu.friend.interact_off', 'Turn Off Interactions'));
                    } else {
                        interactOffData = interactOffData.filter(e => e.targetUserId !== modUid);
                        showToast(true, t('context_menu.friend.interact_on', 'Turn On Interactions'));
                    }
                } else if (modType === 'muteChat') {
                    if (modActive) {
                        if (!muteChatData.some(e => e.targetUserId === modUid)) muteChatData.push(_modEntry);
                        showToast(true, t('context_menu.friend.mute_chat', 'Mute Chat'));
                    } else {
                        muteChatData = muteChatData.filter(e => e.targetUserId !== modUid);
                        showToast(true, t('context_menu.friend.unmute_chat', 'Unmute Chat'));
                    }
                }
                // Update legacy header buttons
                const btn = document.getElementById(modType === 'block' ? 'fdBlockBtn' : modType === 'mute' ? 'fdMuteBtn' : null);
                if (btn) {
                    btn.classList.toggle('active', modActive);
                    btn.title = modActive
                        ? (modType === 'block' ? t('profiles.actions.unblock', 'Unblock') : t('profiles.actions.unmute', 'Unmute'))
                        : (modType === 'block' ? t('profiles.actions.block', 'Block') : t('profiles.actions.mute', 'Mute'));
                    const icon = btn.querySelector('.msi');
                    if (icon) icon.textContent = modType === 'block' ? (modActive ? 'block' : 'shield') : (modActive ? 'mic_off' : 'mic');
                }
                // Refresh moderation card in open profile modal
                if (typeof renderFdModerationCard === 'function' && currentFriendDetail && currentFriendDetail.id === modUid)
                    renderFdModerationCard(modUid);
                if (typeof refreshFdTaskbarActions === 'function' && currentFriendDetail && currentFriendDetail.id === modUid)
                    refreshFdTaskbarActions();
                break;
            }
            case 'vrcAvatars':
                if (payload.filter === 'own') {
                    avatarsData = payload.avatars || [];
                    if (payload.currentAvatarId) currentAvatarId = payload.currentAvatarId;
                    avatarsLoaded = true;
                    (payload.avatars || []).forEach(a => {
                        if (a.id) avatarInfoCache[a.id] = { id: a.id, name: a.name || '' };
                    });
                    if (avatarFilter === 'own') renderAvatarGrid();
                    renderDashOwnAvatars();
                }
                break;
            case 'vrcFavoriteAvatars':
                avatarsLoaded = true;
                renderFavAvatars(payload);
                renderDashFavAvatars();
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
                renderDashFavAvatars();
                renderDashOwnAvatars();
                break;
            case 'vrcAvatarSearchResults':
                if (avatarSearchDb === 'avtricu') {
                    _avIcuBuffer = payload.results || [];
                    _avIcuFetchHasMore = payload.hasMore || false;
                    const _icuSlice = _avIcuBuffer.slice(0, 20);
                    _avIcuBufferCursor = _icuSlice.length;
                    if (payload.page === 0) avatarSearchResults = _icuSlice;
                    else avatarSearchResults = [...avatarSearchResults, ..._icuSlice];
                    avatarSearchHasMore = _avIcuBufferCursor < _avIcuBuffer.length || _avIcuFetchHasMore;
                } else {
                    if (payload.page === 0) avatarSearchResults = payload.results || [];
                    else avatarSearchResults = [...avatarSearchResults, ...(payload.results || [])];
                    avatarSearchHasMore = payload.hasMore || false;
                }
                renderSearchGrid();
                break;
            case 'vrcAvatarBatchCached':
                if (typeof onRoseDbBatchCached === 'function') onRoseDbBatchCached(payload);
                break;
            case 'vrcAvatarBatchDetails':
                if (typeof onRoseDbBatchDetails === 'function') onRoseDbBatchDetails(payload);
                break;
            case 'vrcAvatarDetailsBatch':
                if (typeof onAvatarDetailsBatch === 'function') onAvatarDetailsBatch(payload);
                break;
            case 'vrcUserAvatars':
                renderFdUserAvatars(payload);
                if (typeof onMypUserAvatars === 'function') onMypUserAvatars(payload);
                break;
            case 'vrcUserFavWorlds':
                if (typeof renderUserFavWorlds === 'function') renderUserFavWorlds(payload);
                if (typeof renderMypFavWorlds === 'function') renderMypFavWorlds(payload);
                break;
            case 'vrcAvatarsDeleted':
                _markDeletedAvatars(payload.ids || []);
                break;
            case 'avtrdbCollecting':
                avtrdbCollecting(payload.count);
                break;
            case 'avtrdbReport':
                addAvtrdbReport(payload.count, payload.enqueued, payload.invalid, payload.ticket, payload.type, 'avtrdb');
                break;
            case 'avtrIcuReport':
                addAvtrdbReport(payload.count, payload.count, 0, null, payload.type, 'avtricu');
                break;
            case 'vrcndbReport':
                if (typeof addVrcndbReport === 'function') addVrcndbReport(payload.count, payload.enqueued, payload.duplicates, payload.type);
                break;
            case 'vrcSearchResults':
                renderSearchResults(payload.type, payload.results, payload.offset || 0, payload.hasMore || false);
                break;
            case 'vrcMyGroups':
                renderMyGroups(payload);
                if (document.getElementById('ciGroupRow')?.style.display !== 'none')
                    renderCiGroupPicker(myGroups);
                { const _myp = document.getElementById('modalMyProfile'); if (_myp && _myp.style.display !== 'none') renderMyProfileContent(); }
                break;
            case 'vrcRepresentedGroup':
                myRepresentedGroup = payload && payload.id ? payload : null;
                { const _myp = document.getElementById('modalMyProfile'); if (_myp && _myp.style.display !== 'none') renderMyProfileContent(); }
                break;
            case 'vrcDashGroupInstances':
                if (typeof onDashGroupInstances === 'function') onDashGroupInstances(payload);
                if (typeof onSidebarGroupInstances === 'function') onSidebarGroupInstances(payload);
                break;
            case 'vrcGroupDetail':
            {
                let data = payload;
                if (!payload.isJoined && typeof myGroups !== 'undefined') {
                    const mg = myGroups.find(x => x.id === payload.id);
                    if (mg) {
                        data = { ...payload, isJoined: true, canPost: mg.canPost, canEvent: mg.canEvent,
                            canInvite: mg.canInvite, canEdit: mg.canEdit, canKick: mg.canKick,
                            canBan: mg.canBan, canManageRoles: mg.canManageRoles, canAssignRoles: mg.canAssignRoles,
                            visibility: mg.visibility };
                    }
                }
                renderGroupDetail(data);
                break;
            }
            case 'groupVisibilityUpdated':
                if (payload.success) {
                    const icons = { visible: 'public', friends: 'people', hidden: 'visibility_off' };
                    ['visible','friends','hidden'].forEach(v => {
                        const btn = document.getElementById('ggrpVis_' + v);
                        if (!btn) return;
                        const isActive = v === payload.visibility;
                        btn.classList.toggle('vrcn-btn-primary', isActive);
                        btn.querySelector('.msi').textContent = isActive ? 'check_circle' : icons[v];
                    });
                    if (typeof myGroups !== 'undefined') {
                        const entry = myGroups.find(g => g.id === payload.groupId);
                        if (entry) entry.visibility = payload.visibility;
                    }
                }
                break;
            case 'vrcGroupBans':
                renderGroupBans(payload.groupId, payload.bans);
                break;
            case 'vrcGroupLogs':
                if (typeof renderGroupLogs === 'function') renderGroupLogs(payload);
                break;
            case 'vrcGroupRoleResult':
                onGroupRoleResult(payload);
                break;
            case 'vrcGroupRoleMembers':
                onGroupRoleMembers(payload);
                break;
            case 'vrcGroupInviteProgress':
                handleGroupInviteProgress(payload);
                break;
            case 'vrcGroupDetailError':
                document.getElementById('modalDetail').classList.remove('wd-style-compact', 'gd-style-compact', 'tl-style-compact');
                document.getElementById('detailModalContent').innerHTML = `<div style="padding:30px;text-align:center;color:var(--err);">${esc(payload.error || t('groups.error.loading_detail', 'Error loading group'))}</div><div style="text-align:center;margin-top:10px;"><button class="vrcn-button-round" onclick="document.getElementById('modalDetail').style.display='none'">${t('common.close', 'Close')}</button></div>`;
                break;
            case 'vrcGroupMembersPage':
                {
                    const list = document.getElementById('gdMembersList');
                    if (list && payload.members) {
                        // offset=0 means fresh load (reset), append otherwise
                        if (payload.offset === 0) {
                            list.innerHTML = payload.members.map(m => renderGroupMemberCard(m)).join('')
                                || `<div style="padding:16px;text-align:center;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('groups.empty.no_members', 'No members')}</div>`;
                            window._gdMembersOffset = payload.members.length;
                        } else {
                            payload.members.forEach(m => { list.insertAdjacentHTML('beforeend', renderGroupMemberCard(m)); });
                            window._gdMembersOffset = (window._gdMembersOffset || 0) + payload.members.length;
                        }
                    }
                    const loadMoreDiv = document.getElementById('gdMembersLoadMore');
                    if (loadMoreDiv) {
                        if (payload.hasMore) {
                            loadMoreDiv.innerHTML = `<button class="vrcn-button" onclick="loadMoreGroupMembers()">${t('groups.members.load_more', 'Load More Members')}</button>`;
                        } else {
                            loadMoreDiv.innerHTML = `<div style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);padding:6px;">${t('groups.members.all_loaded', 'All members loaded')}</div>`;
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
                            : `<div style="padding:16px;text-align:center;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('groups.members.search_no_results', 'No members found for')} "<em>${esc(payload.query || '')}</em>"</div>`;
                    }
                    const loadMoreDiv = document.getElementById('gdMembersLoadMore');
                    if (loadMoreDiv) loadMoreDiv.innerHTML = '';
                }
                break;
            case 'vrcSharedContentInfo':
                if (typeof handleSharedContentInfo === 'function') handleSharedContentInfo(payload);
                break;
            case 'vrcWorldDetail':
                renderWorldSearchDetail(payload);
                break;
            case 'vrcAvatarDetail':
                renderAvatarDetail(payload);
                if (typeof onAvatarDetailLive === 'function') onAvatarDetailLive(payload);
                break;
            case 'vrcAvatarGallery':
                if (typeof onAvatarGallery === 'function') onAvatarGallery(payload);
                break;
            case 'vrcAvatarGalleryResult':
                if (typeof onAvatarGalleryResult === 'function') onAvatarGalleryResult(payload);
                break;
            case 'vrcAvatarImageResult':
                if (typeof onAvatarImageResult === 'function') onAvatarImageResult(payload);
                break;
            case 'vrcAvatarUpdateResult':
                onAvatarUpdateResult(payload);
                break;
            case 'vrcWorldUpdateResult':
                if (typeof onWorldUpdateResult === 'function') onWorldUpdateResult(payload);
                break;
            case 'vrcWorldImageResult':
                if (typeof onWorldImageResult === 'function') onWorldImageResult(payload);
                break;
            case 'vrcAvatarDeleteResult':
                if (typeof onAvatarDeleteResult === 'function') onAvatarDeleteResult(payload);
                break;
            case 'vrcWorldDeleteResult':
                if (typeof onWorldDeleteResult === 'function') onWorldDeleteResult(payload);
                break;
            case 'vrcGroupCreateResult':
                if (typeof onGroupCreateResult === 'function') onGroupCreateResult(payload);
                break;
            case 'vrcGroupDeleteResult':
                if (typeof onGroupDeleteResult === 'function') onGroupDeleteResult(payload);
                break;
            case 'vrcOnlineCount':
                _dashOnlineCount = payload.count || 0;
                updateDashSub();
                break;
            case 'vrcAvatarDetailError':
                { const ac = document.getElementById('avatarDetailContent');
                  if (ac) ac.innerHTML = `<div style="padding:30px;text-align:center;color:var(--err);">${esc(payload.error || t('avatars.error.loading_detail', 'Error loading avatar'))}</div><div style="text-align:center;margin-top:10px;"><button class="vrcn-button-round" onclick="closeAvatarDetail()">${t('common.close', 'Close')}</button></div>`; }
                break;
            case 'customColors':
                loadCustomThemes(payload);
                break;
            case 'vrcFavoriteWorlds':
                renderFavWorlds(payload);
                break;
            case 'vrcMyWorlds':
                renderMyWorlds(payload);
                if (typeof onMypMyWorlds === 'function') onMypMyWorlds(payload);
                if (typeof navSetMyWorldsCount === 'function') navSetMyWorldsCount(payload);
                break;
            case 'worldInsights':
                if (typeof wiHandleData === 'function') wiHandleData(payload);
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
            case 'vrcFriendFavGroups':
                if (typeof onFriendFavGroupsLoaded === 'function') onFriendFavGroupsLoaded(payload);
                break;
            case 'vrcFriendFavoriteGroupUpdated':
                if (typeof onFriendFavoriteGroupUpdated === 'function') onFriendFavoriteGroupUpdated(payload);
                break;
            case 'vrcFriendFavoriteResult':
                if (typeof handleFriendFavoriteResult === 'function') handleFriendFavoriteResult(payload);
                break;
            case 'vrcLocalGroupResult':
                if (typeof onLocalGroupResult === 'function') onLocalGroupResult(payload);
                break;
            case 'vrcWorldsResolved':
                onWorldsResolved(payload);
                if (typeof onCreateInstanceWorldResolved === 'function') onCreateInstanceWorldResolved(payload);
                break;
            case 'vrcGroupsResolved':
                if (payload && typeof payload === 'object') {
                    Object.assign(dashGroupCache, payload);
                    renderDashboard();
                    if (typeof tlOnCreatorGroupResolved === 'function') tlOnCreatorGroupResolved(payload);
                }
                break;
case 'vrcNews':
                if (typeof onVrcNews === 'function') onVrcNews(payload.items);
                break;
            case 'vrcNewsArticle':
                if (typeof renderNewsArticle === 'function') renderNewsArticle(payload);
                break;
            case 'popularWorlds':
                onPopularWorlds(payload.worlds);
                break;
            case 'activeWorlds':
                onActiveWorlds(payload.worlds);
                break;
            case 'recentWorlds':
                onRecentWorlds(payload.worlds);
                break;
            case 'visitedWorlds':
                if (typeof renderVisitedWorlds === 'function') renderVisitedWorlds(payload.worlds);
                break;
            case 'recentSeenPlayers':
                if (typeof renderRecentSeen === 'function') renderRecentSeen(payload.players);
                break;
            case 'recentAvatars':
                if (typeof renderRecentAvatars === 'function') renderRecentAvatars(payload.avatars);
                break;
            case 'vrcWorldDetailError':
                document.getElementById('modalDetail').classList.remove('wd-style-compact', 'gd-style-compact', 'tl-style-compact');
                document.getElementById('detailModalContent').innerHTML = `<div style="padding:30px;text-align:center;color:var(--err);">${esc(payload.error || t('worlds.error.loading_detail', 'Error loading world'))}</div><div style="text-align:center;margin-top:10px;"><button class="vrcn-button-round" onclick="document.getElementById('modalDetail').style.display='none'">${t('common.close', 'Close')}</button></div>`;
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
            case 'vrcHiddenNotifications':
                renderNotifications(payload || [], true);
                break;
            case 'vrcNotificationPrepend':
                // Single notification arrived via WebSocket — prepend to existing list
                notifications = [payload, ...(notifications || []).filter(n => n.id !== payload.id)];
                renderNotifications(notifications);
                showNotificationToasts([payload]);
                break;
            case 'vrcNotifImageUpdate':
                // Async image/name update for a notification (resolved after initial send)
                if (payload.notifId && notifications) {
                    const ni = notifications.findIndex(x => x.id === payload.notifId);
                    if (ni !== -1) {
                        if (payload.image) notifications[ni]._image = payload.image;
                        if (payload.senderUsername) notifications[ni].senderUsername = payload.senderUsername;
                        renderNotifications(notifications);
                    }
                    // Update visible toast card if still showing
                    const toastCard = document.querySelector(`.nc-card[data-notif-id="${payload.notifId}"]`);
                    if (toastCard && payload.image) {
                        const oldIcon = toastCard.querySelector('.nc-icon');
                        if (oldIcon) {
                            const icon = oldIcon.textContent;
                            const color = oldIcon.style.color;
                            const av = document.createElement('div');
                            av.className = 'nc-avatar';
                            av.style.backgroundImage = `url('${cssUrl(payload.image)}')`;
                            av.innerHTML = `<span class="msi nc-avatar-badge" style="color:${color};">${icon}</span>`;
                            oldIcon.replaceWith(av);
                        }
                    }
                    // Fire pending tray notification with the now-resolved localhost image URL
                    if (typeof _pendingTrayNotifs !== 'undefined' && _pendingTrayNotifs[payload.notifId]) {
                        const p = _pendingTrayNotifs[payload.notifId];
                        delete _pendingTrayNotifs[payload.notifId];
                        sendToCS({ action: 'trayNotification', title: p.title, subtitle: p.subtitle, imageUrl: payload.image || '', accentKey: p.accentKey });
                    }
                }
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
            case 'vrcRespondMessages':
                if (typeof handleVrcRespondMessages === 'function') handleVrcRespondMessages(payload);
                break;
            case 'vrcRespondMessageUpdateFailed':
                if (typeof handleVrcRespondMessageUpdateFailed === 'function') handleVrcRespondMessageUpdateFailed(payload);
                break;
            case 'updateAvailable':      showUpdateAvailable(payload.version); break;
            case 'updateProgress':       onUpdateProgress(payload); break;
            case 'updateReady':          onUpdateReady(); break;
            case 'showChangelog':        onShowChangelog(payload); break;
            case 'dbMigrationProgress':  onDbMigrationProgress(payload); break;
            case 'gameLogEvent':         addGameLogEntry(payload);        break;
            case 'gameLogHistory':       if (typeof setGameLogHistory === 'function') setGameLogHistory(payload.entries || []); break;
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
            case 'instanceDetail':
                if (typeof openInstanceDetailFromData === 'function') openInstanceDetailFromData(payload);
                break;
            case 'worldInstancesDetail':
                if (typeof handleWorldInstancesDetail === 'function') handleWorldInstancesDetail(payload);
                break;
            case 'dashBgSelected':
                dashBgPath = payload.path || '';
                dashBgDataUri = payload.url || '';
                dashBgSample = payload.sample || '';
                if (dashBgPath) document.getElementById('dashBgName').textContent = dashBgPath.split(/[\\\\/]/).pop();
                if (typeof applyDashHeroBg === 'function') applyDashHeroBg();
                renderDashboard();
                if (currentSpecialTheme === 'auto' && typeof applyAutoColor === 'function') applyAutoColor();
                if (typeof renderDashBgPreview === 'function') renderDashBgPreview();
                autoSave();
                break;
            case 'chatboxUpdate':
                handleChatboxUpdate(payload);
                break;
            case 'sfUpdate':
                handleSfUpdate(payload);
                break;
            case 'stUpdate':
                if (typeof handleStUpdate === 'function') handleStUpdate(payload);
                break;
            case 'fsUpdate':
                if (typeof handleFsUpdate === 'function') handleFsUpdate(payload);
                break;
            case 'fsDevices':
                if (typeof handleFsDevices === 'function') handleFsDevices(payload);
                break;
            case 'fsAudioDevices':
                if (typeof handleFsAudioDevices === 'function') handleFsAudioDevices(payload);
                break;
            case 'fsFfmpegState':
                if (typeof handleFsFfmpegState === 'function') handleFsFfmpegState(payload);
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
            case 'kxdState': handleKxdState(payload); break;
            case 'kxdDevices': populateKxdDevices(payload); break;
            case 'kxdMeter': updateKxdMeter(payload.level); break;
            case 'kxdRecognized': handleKxdRecognized(payload); break;
            case 'kxdTranslated': handleKxdTranslated(payload); break;
            case 'kxdProfileTranslated': handleKxdProfileTranslated(payload); break;
            case 'kxdLocalState': kxdRenderLocalState(payload); break;
            case 'kxdLocalProgress': kxdOnLocalProgress(payload); break;
            case 'kxdLocalFinished': kxdOnLocalFinished(payload); break;
            case 'snipeStatus': handleSnipeStatus(payload); break;
            case 'snipeFound': handleSnipeFound(payload); break;
            case 'snipeJoinResult': handleSnipeJoinResult(payload); break;
            case 'oscState':
                handleOscState(payload);
                break;
            case 'oscParam':
                handleOscParam(payload);
                break;
            case 'hypeRateState':
                if (typeof handleHypeRateState === 'function') handleHypeRateState(payload);
                break;
            case 'weatherState':
                if (typeof handleWeatherState === 'function') handleWeatherState(payload);
                break;
            case 'oscParams':
                handleOscParamBatch(payload);
                break;
            case 'oscAvatarParams':
                handleOscAvatarParams(payload);
                break;
            case 'oscOutputsEnabled':
                handleOscOutputsEnabled(payload);
                break;
            case 'timelineMonthActivity': handleTimelineMonthActivity(payload); break;
            case 'instanceChartData': if (typeof renderInstanceChart === 'function') renderInstanceChart(payload); break;
            case 'timelineData': renderTimeline(payload); renderDashRecentPhotos(); break;
            case 'timelineEvent': handleTimelineEvent(payload); renderDashRecentPhotos(); break;
            case 'timelineEventDeleted': handleTimelineEventDeleted(payload); renderDashRecentPhotos(); break;
            case 'friendTimelineEventDeleted': handleFriendTimelineEventDeleted(payload); break;
            case 'timelineReload': handleTimelineReload(payload); break;
            case 'rewindData': if (typeof handleRewindData === 'function') handleRewindData(payload); break;
            case 'timelineSearchResults': handleTlSearchResults(payload); break;
            case 'friendTimelineData':          renderFriendTimeline(payload); break;
            case 'friendTimelineEvent':         handleFriendTimelineEvent(payload); break;
            case 'friendOnlineToast':      if (typeof _showFriendOnlineCard  === 'function') _showFriendOnlineCard(payload);  break;
            case 'vrcFriendAlertState':    if (typeof _fotHandleAlertState  === 'function') _fotHandleAlertState(payload);  break;
            case 'friendTimelineSearchResults': handleFtlSearchResults(payload); break;
            case 'timelineForUser':        renderFdTimeline(payload.userId, payload.events); if (typeof renderMypTimeline === 'function') renderMypTimeline(payload.userId, payload.events); break;
            case 'timelineForWorld':       if (typeof renderWdInstanceHistory === 'function') renderWdInstanceHistory(payload.worldId, payload.events); break;
            case 'friendActivityForUser':  renderFdUserActivity(payload.userId, payload.events); break;
            case 'profileInsights':        renderFdProfileInsights(payload); if (typeof renderMypProfileInsights === 'function') renderMypProfileInsights(payload); break;
            case 'userOnlineHeatmap':      renderFdOnlineHeatmap(payload); if (typeof renderMypOnlineHeatmap === 'function') renderMypOnlineHeatmap(payload); break;
            case 'userStatusTime':         renderFdStatusTime(payload); if (typeof renderMypStatusTime === 'function') renderMypStatusTime(payload); break;
            case 'exportList':             if (typeof renderExportModal === 'function') renderExportModal(payload); break;
            case 'importFile':             if (typeof renderImportModal === 'function') renderImportModal(payload); break;
            case 'importProgress':         if (typeof onImportProgress === 'function') onImportProgress(payload); break;
            case 'importDone':             if (typeof onImportDone === 'function') onImportDone(payload); break;
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
                    if (typeof onBoopModalFilesLoaded === 'function')
                        onBoopModalFilesLoaded(payload.files || [], payload.tag);
                    if (payload.tag === 'gallery' && typeof _invModalOnGalleryLoaded === 'function')
                        _invModalOnGalleryLoaded(payload.files || []);
                    if (payload.tag === 'gallery' && typeof _nrModalOnGalleryLoaded === 'function')
                        _nrModalOnGalleryLoaded(payload.files || []);
                } else {
                    renderInvFetchError(payload.error, 'inventory.error.requires_login');
                }
                break;
            case 'invPrints':
                if (!payload.error) renderInvPrints(payload.prints || []);
                else renderInvFetchError(payload.error);
                break;
            case 'invUploadResult': handleInvUploadResult(payload); break;
            case 'invPrintUploadResult': handleInvPrintUploadResult(payload); break;
            case 'vrcMessageTemplates':
                if (typeof onMessageTemplates === 'function') onMessageTemplates(payload);
                break;
            case 'vrcMessageTemplateResult':
                if (typeof onMessageTemplateResult === 'function') onMessageTemplateResult(payload);
                break;
            case 'vrcLogFiles':
                if (typeof onLogFiles === 'function') onLogFiles(payload);
                break;
            case 'debugKitExported':
                if (payload.ok) {
                    showToast(true, tf('debugkit.toast.done', { path: payload.path || '' }, 'Debug kit saved: {path}'));
                    sendToCS({ action: 'revealInExplorer', path: payload.path });
                } else {
                    showToast(false, tf('debugkit.toast.failed', { error: payload.error || '' }, 'Debug kit export failed: {error}'));
                }
                break;
            case 'vrcLogLines':
                if (typeof onLogLines === 'function') onLogLines(payload);
                break;
            case 'invDeleteResult': handleInvDeleteResult(payload); break;
            case 'invPrintDeleteResult': handleInvPrintDeleteResult(payload); break;
            case 'invInventory': handleInvInventoryResult(payload); break;
            case 'vrcMutualsForNetwork':
                if (typeof networkAddMutuals === 'function') networkAddMutuals(payload);
                break;
            case 'vrcMutualCacheLoaded':
                if (typeof networkCacheLoaded === 'function') networkCacheLoaded(payload.json);
                break;
            case 'vrcGroupsForNetwork':
                if (typeof networkAddGroups === 'function') networkAddGroups(payload);
                break;
            case 'vrcNetworkSessions':
                if (typeof networkSessionsLoaded === 'function') networkSessionsLoaded(payload);
                break;
            case 'vrcNetworkCacheLoaded':
                if (typeof networkSubCacheLoaded === 'function') networkSubCacheLoaded(payload);
                break;
            case 'vrcApiHealth':
                if (typeof onApiHealth === 'function') onApiHealth(payload);
                break;
            case 'vrcApiHealthDetail':
                if (typeof onApiHealthDetail === 'function') onApiHealthDetail(payload);
                break;
            case 'meetNetworkData':
                if (typeof meetNetworkDataLoaded === 'function') meetNetworkDataLoaded(payload);
                break;
            case 'meetNetworkWorlds':
                if (typeof meetNetworkWorldsLoaded === 'function') meetNetworkWorldsLoaded(payload);
                break;
            case 'vrcTimeSpentData':
                if (typeof tsOnData === 'function') tsOnData(payload);
                break;
            case 'vrcPeopleStatsData':
                if (typeof onPeopleStatsData === 'function') onPeopleStatsData(payload);
                break;
        case 'setPlatform':
            window._isLinuxUi = !!payload?.isLinux;
            if (payload?.isLinux) {
                document.documentElement.classList.add('linux-ui');
                window._kxdProfileTranslationEnabled = false;
                if (typeof navSetLinux === 'function') navSetLinux(true);
                document.querySelectorAll('[data-windows-only]').forEach(el => el.style.display = 'none');
                document.querySelectorAll('[data-linux-only]').forEach(el => el.style.display = '');
                const lbl = document.getElementById('labelStartWithSystem');
                if (lbl) {
                    lbl.setAttribute('data-i18n', 'settings.launch.auto_start_linux');
                    lbl.textContent = t('settings.launch.auto_start_linux', 'Auto-start VRCNext with Linux');
                }
                const lblDesc = document.getElementById('descStartWithSystem');
                if (lblDesc) {
                    lblDesc.setAttribute('data-i18n', 'settings.launch.auto_start_linux_desc');
                    lblDesc.textContent = t('settings.launch.auto_start_linux_desc', 'VRCNext launches automatically when Linux starts. It will open minimized.');
                }
                const vrcPathInput = document.getElementById('setVrcPath');
                if (vrcPathInput) { vrcPathInput.value = 'steam://rungameid/438100'; vrcPathInput.readOnly = true; vrcPathInput.style.opacity = '0.5'; }
                const browseVrcBtn = document.getElementById('browseVrcBtn');
                if (browseVrcBtn) browseVrcBtn.style.display = 'none';
                const dbHint = document.getElementById('dbPathHint');
                if (dbHint) dbHint.textContent = '~/.config/VRCNext';
                if (typeof applyDashLayout === 'function') applyDashLayout();
            }
            break;
        case 'ftAlsoWasHere':
            renderFtAlsoWasHereResult(payload);
            break;
        case 'vrcxSelectCancelled':
            { const btn = document.getElementById('vrcxSelectBtn'); if (btn) { btn.disabled = false; btn.innerHTML = vrcxSelectBtnHtml(false); } }
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
        case 'perminiData':
            onPerminiData(payload);
            break;
        case 'vroState':
            handleVroState(payload);
            break;
        case 'vroKeybindRecorded':
            handleVroKeybindRecorded(payload);
            break;
        case 'vroScaleKeybindRecorded':
            handleVroScaleKeybindRecorded(payload);
            break;
        case 'vroPlayToastSound':
            playSteamOverlaySound();
            break;
        case 'vroPlayWaterSound':
            if (waterAudio) { waterAudio.loop = true; waterAudio.currentTime = 0; waterAudio.play().catch(() => {}); }
            break;
        case 'vroStopWaterSound':
            if (waterAudio) { waterAudio.loop = false; waterAudio.pause(); waterAudio.currentTime = 0; }
            break;
        case 'afFlows':
        case 'afSaveResult':
            if (typeof window.__afHandleMessage === 'function') window.__afHandleMessage(type, payload);
            break;
        case 'afGameRunning':
            window.vrcGameRunning = !!(payload && payload.running);
            if (typeof navUpdatePlaySubtitle === 'function') navUpdatePlaySubtitle();
            if (typeof launchModalSyncGameState === 'function') launchModalSyncGameState();
            if (typeof window.__afHandleMessage === 'function') window.__afHandleMessage(type, payload);
            break;

        case 'ssRules':
            if (typeof ssOnRules === 'function') ssOnRules(payload);
            break;
        case 'ssApplied':
            if (typeof ssOnApplied === 'function') ssOnApplied(payload);
            break;
        case 'ssSaveResult':
            if (typeof ssOnSaveResult === 'function') ssOnSaveResult(payload);
            break;
        case 'vrcnPlusEntitlement':
            if (typeof window.vrcnPlusEntitlement === 'function') window.vrcnPlusEntitlement(payload);
            break;
        case 'screenPickResult':
            if (typeof window.onScreenPickResult === 'function') window.onScreenPickResult(payload);
            break;
    }
    if (typeof navUpdateBadges === 'function') navUpdateBadges();
    if (typeof navUpdatePlaySubtitle === 'function') navUpdatePlaySubtitle();
});
// 'ready' and 'kxdGetDevices' are sent by the fragment loader at the end of
// index.html, AFTER all scripts have loaded. Sending them here would cause the
// backend's loadSettings response to arrive before init.js runs, and init.js's
// renderWebhookCards([{},{},{},{}]) would overwrite the loaded webhook data.

// Crash Report Modal

function showCrashModal(payload) {
    const CRASH_MODAL_ENABLED = false;
    if (!CRASH_MODAL_ENABLED) return;
    const preview = document.getElementById('crashLogPreview');
    if (preview) {
        preview.innerHTML = '';
        const lines = (payload.preview || '').split('\n');
        lines.forEach(line => {
            const row = document.createElement('div');
            row.className = 'li-f';
            const msg = document.createElement('span');
            msg.className = 'li-msg';
            // Style section header lines differently
            if (line.startsWith('═══') || line.startsWith('───')) {
                msg.style.color = 'var(--tx3)';
                msg.style.fontWeight = '600';
            } else if (line.trim().startsWith('at ') || line.trim().startsWith('Type') || line.trim().startsWith('Message')) {
                msg.style.color = 'var(--err)';
            }
            msg.textContent = line || '\u00a0';
            row.appendChild(msg);
            preview.appendChild(row);
        });
    }
    document.getElementById('modalCrash').style.display = '';
}
