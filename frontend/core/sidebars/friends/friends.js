let _sidebarGroupInstances = null;

const RVF_THROTTLE_MS = 300;
let _rvfTimer = null;
let _rvfLastRun = 0;
let _rvfPendingCounts = null;
function scheduleRenderVrcFriends() {
    if (_rvfTimer) return;
    const wait = Math.max(0, RVF_THROTTLE_MS - (Date.now() - _rvfLastRun));
    _rvfTimer = setTimeout(() => {
        _rvfTimer = null;
        const counts = _rvfPendingCounts;
        _rvfPendingCounts = null;
        renderVrcFriends(vrcFriendsData, counts);
    }, wait);
}

function buildFriendCardHtml(f, presenceType) {
    const img = f.image || '';
    const imgTag = img
        ? `<img class="vrc-friend-avatar" src="${imgThumb(img, 96)}" loading="lazy" decoding="async" onerror="this.style.display='none'">`
        : `<div class="vrc-friend-avatar" style="display:flex;align-items:center;justify-content:center;font-size:calc(12px + var(--fs-off, 0px));font-weight:700;color:var(--tx3)">${esc((f.displayName || '?')[0])}</div>`;
    const statusCls = (f.pendingOffline || presenceType === 'offline') ? 's-offline' : statusDotClass(f.status);
    const rank = getTrustRank(f.tags || []);
    const rankBadge = '';
    const nameColorStyle = rank ? `color:${rank.color};` : '';
    const fid = (f.id || '').replace(/'/g, "\\'");
    const statusText = f.statusDescription || statusLabel(f.status);
    const locationText = getFriendLocationLabel(presenceType, f.location);
    const badgeDotCls = presenceType === 'web' ? 'vrc-status-ring' : 'vrc-status-dot';
    const avatarWrap = `<div class="vrc-friend-avatar-wrap">${imgTag}${(typeof iconFrameHtml === 'function') ? iconFrameHtml(f.iconFrameUrl) : ''}<span class="vrc-friend-status-badge ${badgeDotCls} ${statusCls}"></span></div>`;
    return `<div class="vrc-friend-card${(typeof decoSelfCls === 'function') ? decoSelfCls(f) : ''}" data-uid="${fid}" data-status="${statusCls}" onclick="openFriendDetail('${fid}')">${(typeof nameplateDecoHtml === 'function') ? nameplateDecoHtml(f.nameplateUrl) : ''}${avatarWrap}<div class="vrc-friend-info"><div class="vrc-friend-name"><span style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;${nameColorStyle}">${esc(f.displayName)}</span>${rankBadge}</div><div class="vrc-friend-loc">${_friendLocLineInner(f, presenceType, statusText, locationText)}</div></div></div>`;
}

function tryPatchVrcFriendCard(prev, f) {
    if (!prev || !f || !f.id) return false;
    if ((document.getElementById('vrcFriendSearchInput')?.value || '').trim()) return false;
    if (prev.presence !== f.presence) return false;
    if ((prev.location || '') !== (f.location || '')) return false;
    const list = document.getElementById('vrcFriendsList');
    if (!list) return false;
    const cards = list.querySelectorAll(`.vrc-friend-card[data-uid="${(f.id || '').replace(/"/g, '\\"')}"]`);
    if (!cards.length) return true;
    const presenceType = f.presence === 'game' ? 'game' : (f.presence || 'offline');
    const html = buildFriendCardHtml(f, presenceType);
    cards.forEach(c => { c.outerHTML = html; });
    list.__lastHtml = null;
    return true;
}

function getRegionCode(region) {
    return (region || 'us').toUpperCase();
}
function _friendLocLineInner(f, presenceType, statusText, locationText) {
    if (f.pendingOffline) return esc(t('profiles.friends.pending_offline', 'Pending offline...'));
    if (f.traveling) return esc(t('profiles.friends.traveling', 'Traveling...'));
    const locationOnly = (typeof settings !== 'undefined') && settings.friendsSidebarLocationOnly === true;
    if (locationOnly) {
        const inWorld = f.location && f.location.startsWith('wrld_');
        if (!inWorld) return esc(statusText);
        const parsed      = parseFriendLocation(f.location);
        const wc          = (typeof dashWorldCache !== 'undefined') ? dashWorldCache[parsed.worldId] : null;
        const wname       = wc?.name || locationText;
        const regionRaw   = (f.location.match(/~region\(([^)]+)\)/) || [])[1] || '';
        return `<span class="vrc-loc-world"><span class="msi vrc-loc-wicon">public</span><span class="vrcn-badge vrc-region-badge">${esc(getRegionCode(regionRaw))}</span><span class="vrc-loc-wname">${esc(wname)}</span></span>`;
    }
    return `${esc(statusText)} &middot; ${esc(locationText)}`;
}

function _sepFavTabs() {
    return (typeof settings !== 'undefined') && settings.separateFavoriteFriends === true;
}

function applyFriendsSidebarFavTabs() {
    if (!_sepFavTabs() && friendsSidebarTab === 'favorites') { setFriendsSidebarTab('friends'); return; }
    _updateFriendTabCounts();
    if (typeof vrcFriendsData !== 'undefined') renderVrcFriends(vrcFriendsData);
}

function _favGameFriends() {
    const favIds = new Set((favFriendsData || []).map(f => f.favoriteId));
    if (!favIds.size) return [];
    return (vrcFriendsData || []).filter(f => favIds.has(f.id) && f.presence === 'game');
}

function _updateFriendTabCounts() {
    const fc = document.getElementById('vrcFriendTabFriendsCount');
    const vc = document.getElementById('vrcFriendTabFavoritesCount');
    const gc = document.getElementById('vrcFriendTabGroupsCount');
    if (fc) fc.textContent = (vrcFriendsData || []).length;
    if (vc) vc.textContent = _favGameFriends().length;
    const vb = document.getElementById('vrcFriendTabFavorites');
    if (vb) vb.style.display = _sepFavTabs() ? '' : 'none';
    if (gc) gc.textContent = (_sidebarGroupInstances || []).length;
    const _tab = (friendsSidebarTab === 'favorites' && !_sepFavTabs()) ? 'friends' : friendsSidebarTab;
    document.getElementById('vrcFriendTabFriends')?.classList.toggle('active', _tab === 'friends');
    document.getElementById('vrcFriendTabFavorites')?.classList.toggle('active', _tab === 'favorites');
    document.getElementById('vrcFriendTabGroups')?.classList.toggle('active', _tab === 'groups');
}

function setFriendsSidebarTab(tab) {
    friendsSidebarTab = (tab === 'groups' || tab === 'favorites') ? tab : 'friends';
    try { localStorage.setItem('friendsSidebarTab', friendsSidebarTab); } catch {}
    _updateFriendTabCounts();
    renderVrcFriends(vrcFriendsData);
}

function toggleRsidebar() {
    rsidebarCollapsed = !rsidebarCollapsed;
    localStorage.setItem('vrcnext_rsidebar', rsidebarCollapsed ? '1' : '0');
    const rs = document.getElementById('rsidebar');
    const rsEl = document.getElementById('rsIcon'); if (rsEl) rsEl.textContent = rsidebarCollapsed ? 'chevron_left' : 'chevron_right';
    rs.classList.toggle('collapsed', rsidebarCollapsed);
    if (typeof renderVrcFriends === 'function' && vrcFriendsData?.length) renderVrcFriends(vrcFriendsData);
    if (typeof _applyLightInterp === 'function') _applyLightInterp();
}

function renderVrcProfile(u) {
    const a = document.getElementById('vrcProfileArea');
    const rs = document.getElementById('rsidebar');
    if (rs) rs.classList.toggle('logged-out', !u);
    if (!u) { a.innerHTML = ''; currentVrcUser = null; return; }
    if (u.rawJson) _mypRawJson = u.rawJson;
    currentVrcUser = u;
    if (!window._rewindChecked) { window._rewindChecked = true; setTimeout(() => sendToCS({ action: 'checkRewind' }), 4000); }
    // If My Profile modal is open, refresh it immediately
    const _myp = document.getElementById('modalMyProfile');
    if (_myp && _myp.style.display !== 'none') renderMyProfileContent();
    const img = u.image || '';
    const imgTag = img
        ? `<img class="vrc-avatar" src="${img}" onerror="this.style.display='none'">`
        : `<div class="vrc-avatar" style="display:flex;align-items:center;justify-content:center;font-size:calc(13px + var(--fs-off, 0px));font-weight:700;color:var(--tx3)">${esc((u.displayName || '?')[0])}</div>`;
    const ownStatusCls = statusDotClass(u.status);
    const ownDotShape = u.vrcRunning ? 'vrc-status-dot' : 'vrc-status-ring';
    const ownAvatarWrap = `<div class="vrc-profile-avatar-wrap">${imgTag}${(typeof iconFrameHtml === 'function') ? iconFrameHtml(u.iconFrameUrl, true) : ''}<span class="vrc-friend-status-badge ${ownDotShape} ${ownStatusCls}"></span></div>`;
    a.innerHTML = `<div class="vrc-profile deco-self" data-status="${ownStatusCls}" onclick="openMyProfileModal()">${(typeof nameplateDecoHtml === 'function') ? nameplateDecoHtml(u.nameplateUrl, true) : ''}${ownAvatarWrap}<div class="vrc-profile-info"><div class="vrc-profile-name">${esc(u.displayName)}</div><div class="vrc-profile-status">${getStatusText(u.status, u.statusDescription)}</div></div><span class="msi" style="font-size:16px;color:var(--tx3);flex-shrink:0;">manage_accounts</span></div>`;
}

function onVrcRunningChanged(payload) {
    if (!currentVrcUser) return;
    currentVrcUser.vrcRunning = !!payload?.running;
    renderVrcProfile(currentVrcUser);
}

function renderVrcFriends(friends, counts) {
    _rvfLastRun = Date.now();
    const el = document.getElementById('vrcFriendsList');
    document.getElementById('vrcFriendRefreshBtn')?.classList.remove('spinning');
    vrcFriendsData = friends || [];
    friends = vrcFriendsData;
    if (typeof _renderLibIconSelects === 'function') _renderLibIconSelects();
    const _favSeparate = _sepFavTabs();
    const _activeTab = (rsidebarCollapsed || (friendsSidebarTab === 'favorites' && !_favSeparate)) ? 'friends' : friendsSidebarTab;
    const _favInline = _activeTab === 'friends' && (!_favSeparate || rsidebarCollapsed);
    _updateFriendTabCounts();

    // Lazy-load group instances once on first render
    if (_sidebarGroupInstances === null && !window._groupInstInFlight) {
        window._groupInstInFlight = true;
        sendToCS({ action: 'vrcGetDashGroupInstances' });
    }

    if (currentFriendDetail && friends) {
        const lf = friends.find(f => f.id === currentFriendDetail.id);
        if (lf) {
            currentFriendDetail.status = lf.status;
            currentFriendDetail.statusDescription = lf.statusDescription;
            currentFriendDetail.location = lf.location;
            currentFriendDetail.presence = lf.presence;
            const detailStatusEl = document.getElementById('fd-live-status');
            if (detailStatusEl) {
                const isWeb = lf.presence === 'web';
                const isOff = lf.presence === 'offline';
                // Status text is just the description; dot lives on the avatar.
                detailStatusEl.innerHTML = lf.statusDescription ? esc(lf.statusDescription) : '';
                const statusRow = detailStatusEl.closest('.fd-status-row');
                if (statusRow) statusRow.style.display = lf.statusDescription ? '' : 'none';
                const detailDotEl = document.getElementById('fd-live-dot');
                if (detailDotEl) {
                    const dotClass = isWeb ? 'vrc-status-ring' : 'vrc-status-dot';
                    detailDotEl.className = `${dotClass} ${isOff ? 's-offline' : statusDotClass(lf.status)} fd-left-status-dot`;
                }
            }
        }
    }

    const controls = document.getElementById('vrcFriendControls');
    if (controls) controls.style.display = vrcFriendsData.length > 0 ? '' : 'none';

    if (_activeTab === 'friends' && !friends.length) {
        setHtmlIfChanged(el, `<div class="vrc-section-label">${getFriendSectionLabel('onlineZero', 0)}</div><div style="padding:16px;text-align:center;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('dashboard.friends.empty', 'No friends online')}</div>`);
        return;
    }

    const gameFriends = friends.filter(f => f.presence === 'game');
    const webFriends = friends.filter(f => f.presence === 'web');
    const offlineFriends = friends.filter(f => f.presence === 'offline');

    const gc = counts ? counts.game : gameFriends.length;
    const wc = counts ? counts.web : webFriends.length;
    const oc = counts ? counts.offline : offlineFriends.length;

    const renderCard = (f, presenceType) => buildFriendCardHtml(f, presenceType);

    const favIds = new Set(favFriendsData.map(f => f.favoriteId));
    const favFriends = favIds.size > 0 ? friends.filter(f => favIds.has(f.id) && f.presence === 'game') : [];

    const _sectionIcons = { favorites: 'favorite', ingame: 'sports_esports', web: 'language', offline: 'wifi_off' };
    let h = '';
    const appendSection = (key, count, list, presenceResolver) => {
        if (!list.length) return;
        const chev = friendSectionCollapsed[key] ? 'expand_more' : 'expand_less';
        const _active = !friendSectionCollapsed[key] ? ' active' : '';
        const _navCls = rsidebarCollapsed ? 'nav-btn nav-group-btn ' : '';
        h += `<div class="${_navCls}vrc-section-label vrc-offline-toggle${_active}" id="${key}SectionLabel" onclick="toggleFriendSection('${key}')" style="cursor:pointer;"><span class="ni msi">${_sectionIcons[key] || 'group'}</span><span class="nl">${getFriendSectionLabel(key, count)}</span><span class="nav-group-arrow msi nl" id="${key}Chevron">${chev}</span></div>`;
        h += `<div id="${key}FriendsSection" class="friend-section-items${friendSectionCollapsed[key] ? ' collapsed' : ''}">`;
        list.forEach(f => {
            const resolvedPresence = typeof presenceResolver === 'function' ? presenceResolver(f) : presenceResolver;
            h += renderCard(f, resolvedPresence);
        });
        h += `</div>`;
    };

    // Same Location — only shown when sidebar is expanded
    const _slocIds = new Set();
    if (_activeTab === 'friends' && !rsidebarCollapsed) {
        const _instGroups = {};
        gameFriends.filter(f => f.location && f.location.startsWith('wrld_')).forEach(f => {
            const locBase = f.location.split('~')[0];
            if (!_instGroups[locBase]) _instGroups[locBase] = [];
            _instGroups[locBase].push(f);
        });
        const _sharedInst = Object.entries(_instGroups).filter(([, list]) => list.length >= 2);
        if (_sharedInst.length) {
            const _slTotal = _sharedInst.reduce((s, [, l]) => s + l.length, 0);
            const _slChev = friendSectionCollapsed.samelocation ? 'expand_more' : 'expand_less';
            const _slNavCls = rsidebarCollapsed ? 'nav-btn nav-group-btn ' : '';
            h += `<div class="${_slNavCls}vrc-section-label vrc-offline-toggle" onclick="toggleFriendSection('samelocation')" style="cursor:pointer;"><span class="ni msi">location_on</span><span class="nl">${tf('profiles.friends.sections.same_location', { count: _slTotal }, 'IN INSTANCE - {count}')}</span><span class="nav-group-arrow msi nl" id="samelocationChevron">${_slChev}</span></div>`;
            h += `<div id="samelocationFriendsSection" class="friend-section-items${friendSectionCollapsed.samelocation ? ' collapsed' : ''}">`;
            _sharedInst.forEach(([locBase, list]) => {
                list.forEach(f => _slocIds.add(f.id));
                const _wid = locBase.split(':')[0];
                const _iid = locBase.split(':')[1] || '';
                const _wc = (typeof dashWorldCache !== 'undefined' && dashWorldCache[_wid]) || null;
                const _wname = _wc?.name || '';
                const _grpLabel = _wname
                    ? `${_wname}${_iid ? ' · #' + _iid : ''}`
                    : (_iid ? '#' + _iid : _wid);
                const { instanceType: _iType } = parseFriendLocation(list[0]?.location || '');
                const { cls: _iCls, label: _iLabel } = getInstanceBadge(_iType);
                const _badgeHtml = `<span class="vrcn-badge ${_iCls}">${esc(_iLabel)}</span>`;
                h += `<div class="sloc-inst-card">`;
                h += `<div class="sloc-inst-content">`;
                h += `<div class="sloc-inst-label">${esc(_grpLabel)} <span class="sloc-inst-count">${list.length}</span>${_badgeHtml}</div>`;
                list.forEach(f => { h += renderCard(f, 'game'); });
                h += `</div></div>`;
            });
            h += `</div>`;
        }
    }

    const _favGroupOf = new Map(favFriendsData.map(f => [f.favoriteId, f.groupName || '']));
    const _favGroupList = (typeof favFriendGroups !== 'undefined' && Array.isArray(favFriendGroups)) ? favFriendGroups : [];
    const _favByGroup = new Map();
    favFriends.forEach(f => {
        const gn = _favGroupOf.get(f.id) || '';
        if (!_favByGroup.has(gn)) _favByGroup.set(gn, []);
        _favByGroup.get(gn).push(f);
    });
    const _favSubs = _favGroupList
        .filter(g => (_favByGroup.get(g.name) || []).length > 0)
        .map(g => ({ g, list: _favByGroup.get(g.name) }));
    const _favKnown = new Set(_favGroupList.map(g => g.name));
    const _favOther = [];
    _favByGroup.forEach((list, gn) => { if (!_favKnown.has(gn)) _favOther.push(...list); });

    if (_activeTab === 'favorites' && !favFriends.length) {
        setHtmlIfChanged(el, `<div style="padding:16px;text-align:center;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('sidebar.favorites.empty', 'No favorite friends online')}</div>`);
        return;
    }

    if ((_activeTab === 'favorites' || _favInline) && !rsidebarCollapsed && _favSubs.length > 1) {
        const _fvChev = friendSectionCollapsed.favorites ? 'expand_more' : 'expand_less';
        const _fvActive = !friendSectionCollapsed.favorites ? ' active' : '';
        h += `<div class="vrc-section-label vrc-offline-toggle${_fvActive}" id="favoritesSectionLabel" onclick="toggleFriendSection('favorites')" style="cursor:pointer;"><span class="ni msi">favorite</span><span class="nl">${getFriendSectionLabel('favorites', favFriends.length)}</span><span class="nav-group-arrow msi nl" id="favoritesChevron">${_fvChev}</span></div>`;
        h += `<div id="favoritesFriendsSection" class="friend-section-items${friendSectionCollapsed.favorites ? ' collapsed' : ''}">`;

        const _favSub = (key, label, badge, list) => {
            const _sChev = friendSectionCollapsed[key] ? 'expand_more' : 'expand_less';
            const _sActive = !friendSectionCollapsed[key] ? ' active' : '';
            h += `<div class="vrc-section-label vrc-gi-group-header vrc-offline-toggle${_sActive}" onclick="toggleFriendSection('${key}')" style="cursor:pointer;padding-left:16px;"><span class="ni msi">favorite</span><span class="nl" style="display:flex;align-items:center;gap:5px;overflow:hidden;"><span style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${esc(label)}</span>${badge}<span style="flex-shrink:0;">· ${list.length}</span></span><span class="nav-group-arrow msi nl" id="${key}Chevron">${_sChev}</span></div>`;
            h += `<div id="${key}FriendsSection" class="friend-section-items${friendSectionCollapsed[key] ? ' collapsed' : ''}">`;
            list.slice(0, 100).forEach(f => { h += renderCard(f, f.presence); });
            h += `</div>`;
        };

        _favSubs.forEach(({ g, list }) => {
            const badge = typeof favGroupBadge === 'function' ? favGroupBadge(g) : '';
            _favSub(`fav_${g.name}`, g.displayName || g.name, badge, list);
        });
        if (_favOther.length) _favSub('fav__other', t('sidebar.favorites.other', 'Other'), '', _favOther);

        h += `</div>`;
    } else if (_activeTab === 'favorites' || _favInline) {
        appendSection('favorites', favFriends.length, favFriends.slice(0, 100), f => f.presence);
    }

    // Group Instances section (Groups tab)
    if (_activeTab === 'groups') {
        if (!_sidebarGroupInstances || !_sidebarGroupInstances.length) {
            setHtmlIfChanged(el, `<div style="padding:16px;text-align:center;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('sidebar.groups.empty', 'No group instances')}</div>`);
            return;
        }
        const _giByGroup = {};
        _sidebarGroupInstances.forEach(inst => {
            const gid = inst.groupId || '';
            if (!gid) return;
            if (!_giByGroup[gid]) _giByGroup[gid] = { name: inst.groupName || gid, icon: inst.groupIcon || '', instances: [] };
            _giByGroup[gid].instances.push(inst);
        });
        const _giEntries = Object.entries(_giByGroup);
        if (_giEntries.length) {
            const _giTotal = _giEntries.reduce((s, [, g]) => s + g.instances.length, 0);
            const _giKey = 'groupinstances';
            const _giChev = friendSectionCollapsed[_giKey] ? 'expand_more' : 'expand_less';
            const _giActive = !friendSectionCollapsed[_giKey] ? ' active' : '';
            h += `<div class="vrc-section-label vrc-offline-toggle${_giActive}" onclick="toggleFriendSection('${_giKey}')" style="cursor:pointer;"><span class="ni msi">group</span><span class="nl">${t('sidebar.groups.label', 'GROUPS')} · ${_giTotal}</span><span class="nav-group-arrow msi nl" id="${_giKey}Chevron">${_giChev}</span></div>`;
            h += `<div id="${_giKey}FriendsSection" class="friend-section-items${friendSectionCollapsed[_giKey] ? ' collapsed' : ''}">`;
            _giEntries.forEach(([gid, grp]) => {
                const _subKey = `gi_${gid}`;
                const _subChev = friendSectionCollapsed[_subKey] ? 'expand_more' : 'expand_less';
                const _subActive = !friendSectionCollapsed[_subKey] ? ' active' : '';
                const _iconHtml = grp.icon
                    ? `<img class="vrc-gi-group-icon" src="${imgThumb(grp.icon, 64)}" loading="lazy" decoding="async" onerror="this.style.display='none'">`
                    : `<span class="msi" style="font-size:13px;flex-shrink:0;">group</span>`;
                h += `<div class="vrc-section-label vrc-gi-group-header vrc-offline-toggle${_subActive}" onclick="toggleFriendSection('${_subKey}')" style="cursor:pointer;padding-left:16px;"><span class="ni msi">group</span><span class="nl" style="display:flex;align-items:center;gap:5px;overflow:hidden;">${_iconHtml}<span style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${esc(grp.name)}</span><span style="flex-shrink:0;">· ${grp.instances.length}</span></span><span class="nav-group-arrow msi nl" id="${_subKey}Chevron">${_subChev}</span></div>`;
                h += `<div id="${_subKey}FriendsSection" class="friend-section-items${friendSectionCollapsed[_subKey] ? ' collapsed' : ''}">`;
                grp.instances.forEach(inst => {
                    const _loc = (inst.location || '').replace(/'/g, "\\'");
                    const _thumbHtml = inst.worldThumb ? `<img class="vrc-gi-world-thumb" src="${imgThumb(inst.worldThumb, 64)}" loading="lazy" decoding="async" onerror="this.style.display='none'">` : '';
                    h += `<div class="vrc-gi-instance-card" onclick="openGroupInstanceDetail('${_loc}')" style="cursor:pointer;">`;
                    if (_thumbHtml) h += _thumbHtml;
                    h += `<div class="vrc-gi-instance-info"><div class="vrc-gi-instance-name">${esc(inst.worldName || inst.location || '')}</div><div class="vrc-gi-instance-count">${inst.userCount}/${inst.capacity}</div></div>`;
                    h += `</div>`;
                });
                h += `</div>`;
            });
            h += `</div>`;
        }
    }

    if (_activeTab === 'friends') {
        const ingameFriends = gameFriends.filter(f => !_slocIds.has(f.id) && !(_favInline && favIds.has(f.id)));
        appendSection('ingame', ingameFriends.length, ingameFriends.slice(0, 100), 'game');
        appendSection('web', wc, webFriends.slice(0, 100), 'web');
        appendSection('offline', oc, offlineFriends.slice(0, 100), 'offline');
    }

    setHtmlIfChanged(el, h);
    // Only apply search filter if there is an active query
    const _activeQ = (document.getElementById('vrcFriendSearchInput')?.value || '').toLowerCase().trim();
    if (_activeQ) filterFriendsList();
}

function onSidebarGroupInstances(instances) {
    _sidebarGroupInstances = instances || [];
    document.getElementById('vrcFriendRefreshBtn')?.classList.remove('spinning');
    _updateFriendTabCounts();
    if (friendsSidebarTab === 'groups' || (vrcFriendsData && vrcFriendsData.length)) renderVrcFriends(vrcFriendsData);
}

// Manual refresh button next to the friends search: refreshes the friends list
// and the group instances (same data the dashboard's group activity refresh pulls).
function refreshVrcFriendsAndGroups() {
    document.getElementById('vrcFriendRefreshBtn')?.classList.add('spinning');
    sendToCS({ action: 'vrcRefreshFriends' });
    window._groupInstInFlight = true;
    sendToCS({ action: 'vrcGetDashGroupInstances' });
}

function toggleFriendSection(key) {
    friendSectionCollapsed[key] = !friendSectionCollapsed[key];
    try { localStorage.setItem('friendSectionCollapsed', JSON.stringify(friendSectionCollapsed)); } catch {}
    const ids = { samelocation: ['samelocationFriendsSection', 'samelocationChevron'], favorites: ['favoritesFriendsSection', 'favoritesChevron'], ingame: ['ingameFriendsSection', 'ingameChevron'], web: ['webFriendsSection', 'webChevron'], offline: ['offlineFriendsSection', 'offlineChevron'] };
    const [secId, chevId] = ids[key] || [`${key}FriendsSection`, `${key}Chevron`];
    const sec = secId && document.getElementById(secId);
    const chev = chevId && document.getElementById(chevId);
    if (sec) sec.classList.toggle('collapsed', !!friendSectionCollapsed[key]);
    if (chev) chev.textContent = friendSectionCollapsed[key] ? 'expand_more' : 'expand_less';
    const label = document.getElementById(`${key}SectionLabel`);
    if (label) label.classList.toggle('active', !friendSectionCollapsed[key]);
}

function filterFriendsList() {
    const el = document.getElementById('vrcFriendsList');
    if (!el) return;
    const q = (document.getElementById('vrcFriendSearchInput')?.value || '').toLowerCase().trim();

    if (q && friendsSidebarTab !== 'friends') { setFriendsSidebarTab('friends'); return; }

    if (!q) {
        // No search active — re-render normal capped sections
        renderVrcFriends(vrcFriendsData);
        return;
    }

    // Search: filter full vrcFriendsData array, show up to 100 results as a flat list
    const all = vrcFriendsData.filter(f =>
        (f.displayName || '').toLowerCase().includes(q) ||
        (f.username || f.userName || '').toLowerCase().includes(q) ||
        (f.id || '').toLowerCase().includes(q)
    );
    const capped = all.slice(0, 100);

    if (!capped.length) {
        setHtmlIfChanged(el, `<div style="padding:16px;text-align:center;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('profiles.people.no_results', 'No results')}</div>`);
        return;
    }

    const countLabel = all.length > 100
        ? `<div style="padding:6px 12px 2px;font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);">${tf('profiles.friends.search.showing', { total: all.length }, 'Showing 100 of {total} results')}</div>`
        : '';

    let h = countLabel + `<div class="friend-section-items">`;
    capped.forEach(f => {
        h += buildFriendCardHtml(f, f.presence || 'offline');
    });
    h += `</div>`;
    setHtmlIfChanged(el, h);
}
