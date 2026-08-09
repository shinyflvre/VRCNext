let _sidebarGroupInstances = null;

const RVF_THROTTLE_MS = 300;
let _rvfTimer = null;
let _rvfLastRun = 0;
function scheduleRenderVrcFriends() {
    if (_rvfTimer) return;
    const wait = Math.max(0, RVF_THROTTLE_MS - (Date.now() - _rvfLastRun));
    _rvfTimer = setTimeout(() => {
        _rvfTimer = null;
        renderVrcFriends(vrcFriendsData);
    }, wait);
}

function getRegionCode(region) {
    return (region || 'us').toUpperCase();
}
function _friendLocLineInner(f, presenceType, statusText, locationText) {
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
    const ownAvatarWrap = `<div class="vrc-profile-avatar-wrap">${imgTag}${(typeof iconFrameHtml === 'function') ? iconFrameHtml(u.iconFrameUrl) : ''}<span class="vrc-friend-status-badge vrc-status-dot ${ownStatusCls}"></span></div>`;
    a.innerHTML = `<div class="vrc-profile" data-status="${ownStatusCls}" onclick="openMyProfileModal()">${(typeof nameplateDecoHtml === 'function') ? nameplateDecoHtml(u.nameplateUrl) : ''}${ownAvatarWrap}<div class="vrc-profile-info"><div class="vrc-profile-name">${esc(u.displayName)}</div><div class="vrc-profile-status">${getStatusText(u.status, u.statusDescription)}</div></div><span class="msi" style="font-size:16px;color:var(--tx3);flex-shrink:0;">manage_accounts</span></div>`;
}

function renderVrcFriends(friends, counts) {
    _rvfLastRun = Date.now();
    const el = document.getElementById('vrcFriendsList');
    const lp = document.getElementById('vrcLoginPrompt');
    if (lp) lp.style.display = 'none';
    document.getElementById('vrcFriendRefreshBtn')?.classList.remove('spinning');
    vrcFriendsData = friends || [];
    if (typeof _renderLibIconSelects === 'function') _renderLibIconSelects();

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
                const isCompact = document.getElementById('modalFriendDetail')?.classList.contains('fd-style-compact');
                if (isCompact) {
                    // Compact: status text is just the description; dot lives on the avatar.
                    detailStatusEl.innerHTML = lf.statusDescription ? esc(lf.statusDescription) : '';
                    const statusRow = detailStatusEl.closest('.fd-status-row');
                    if (statusRow) statusRow.style.display = lf.statusDescription ? '' : 'none';
                    const detailDotEl = document.getElementById('fd-live-dot');
                    if (detailDotEl) {
                        const dotClass = isWeb ? 'vrc-status-ring' : 'vrc-status-dot';
                        detailDotEl.className = `${dotClass} ${isOff ? 's-offline' : statusDotClass(lf.status)} fd-left-status-dot`;
                    }
                } else {
                    // Classic: dot is inline in the status text, full status label + web suffix + description.
                    const dotClass = isWeb ? 'vrc-status-ring' : 'vrc-status-dot';
                    detailStatusEl.innerHTML = `<span class="${dotClass} ${isOff ? 's-offline' : statusDotClass(lf.status)}" style="width:8px;height:8px;"></span>${isOff ? t('status.offline', 'Offline') : statusLabel(lf.status)}${(!isOff && isWeb) ? ' ' + t('profiles.friends.web_suffix', '(Web)') : ''}${(!isOff && lf.statusDescription) ? ' - ' + esc(lf.statusDescription) : ''}`;
                }
            }
        }
    }

    const searchBar = document.getElementById('vrcFriendSearch');
    if (searchBar) searchBar.style.display = vrcFriendsData.length > 0 ? '' : 'none';

    if (!friends || !friends.length) {
        el.innerHTML = `<div class="vrc-section-label">${getFriendSectionLabel('onlineZero', 0)}</div><div style="padding:16px;text-align:center;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('dashboard.friends.empty', 'No friends online')}</div>`;
        return;
    }

    const gameFriends = friends.filter(f => f.presence === 'game');
    const webFriends = friends.filter(f => f.presence === 'web');
    const offlineFriends = friends.filter(f => f.presence === 'offline');

    const gc = counts ? counts.game : gameFriends.length;
    const wc = counts ? counts.web : webFriends.length;
    const oc = counts ? counts.offline : offlineFriends.length;

    const renderCard = (f, presenceType) => {
        const img = f.image || '';
        const imgTag = img
            ? `<img class="vrc-friend-avatar" src="${img}" loading="lazy" decoding="async" onerror="this.style.display='none'">`
            : `<div class="vrc-friend-avatar" style="display:flex;align-items:center;justify-content:center;font-size:calc(12px + var(--fs-off, 0px));font-weight:700;color:var(--tx3)">${esc((f.displayName || '?')[0])}</div>`;
        const statusCls = presenceType === 'offline' ? 's-offline' : statusDotClass(f.status);
        const rank = getTrustRank(f.tags || []);
        const useRankColor = (typeof settings !== 'undefined') && settings.friendsSidebarRankColor === true;
        const rankBadge = (rank && !useRankColor) ? `<span class="vrcn-badge ${rank.cls}">${rank.label}</span>` : '';
        const nameColorStyle = (useRankColor && rank) ? `color:${rank.color};` : '';
        const fid = (f.id || '').replace(/'/g, "\\'");
        const statusText = f.statusDescription || statusLabel(f.status);
        const locationText = getFriendLocationLabel(presenceType, f.location);
        const badgeDotCls = presenceType === 'web' ? 'vrc-status-ring' : 'vrc-status-dot';
        const avatarWrap = `<div class="vrc-friend-avatar-wrap">${imgTag}${(typeof iconFrameHtml === 'function') ? iconFrameHtml(f.iconFrameUrl) : ''}<span class="vrc-friend-status-badge ${badgeDotCls} ${statusCls}"></span></div>`;
        return `<div class="vrc-friend-card" data-uid="${fid}" data-status="${statusCls}" onclick="openFriendDetail('${fid}')">${(typeof nameplateDecoHtml === 'function') ? nameplateDecoHtml(f.nameplateUrl) : ''}${avatarWrap}<div class="vrc-friend-info"><div class="vrc-friend-name"><span style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;${nameColorStyle}">${esc(f.displayName)}</span>${rankBadge}</div><div class="vrc-friend-loc">${_friendLocLineInner(f, presenceType, statusText, locationText)}</div></div></div>`;
    };

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
    if (!rsidebarCollapsed) {
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
                const _wid = locBase.split(':')[0];
                const _iid = locBase.split(':')[1] || '';
                const _wc = (typeof dashWorldCache !== 'undefined' && dashWorldCache[_wid]) || null;
                const _wname = _wc?.name || '';
                const _wthumb = _wc?.thumbnailImageUrl || _wc?.imageUrl || '';
                const _grpLabel = _wname
                    ? `${_wname}${_iid ? ' · #' + _iid : ''}`
                    : (_iid ? '#' + _iid : _wid);
                const { instanceType: _iType } = parseFriendLocation(list[0]?.location || '');
                const { cls: _iCls, label: _iLabel } = getInstanceBadge(_iType);
                const _badgeHtml = `<span class="vrcn-badge ${_iCls}">${esc(_iLabel)}</span>`;
                h += `<div class="sloc-inst-card">`;
                if (_wthumb) h += `<div class="sloc-inst-bg" style="background-image:url('${cssUrl(_wthumb)}')"></div>`;
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

    if (!rsidebarCollapsed && _favSubs.length > 1) {
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
    } else {
        appendSection('favorites', favFriends.length, favFriends.slice(0, 100), f => f.presence);
    }

    // Group Instances section (expanded sidebar only, same as Same Location)
    if (!rsidebarCollapsed && _sidebarGroupInstances !== null && _sidebarGroupInstances.length > 0) {
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
                    ? `<img class="vrc-gi-group-icon" src="${grp.icon}" loading="lazy" decoding="async" onerror="this.style.display='none'">`
                    : `<span class="msi" style="font-size:13px;flex-shrink:0;">group</span>`;
                h += `<div class="vrc-section-label vrc-gi-group-header vrc-offline-toggle${_subActive}" onclick="toggleFriendSection('${_subKey}')" style="cursor:pointer;padding-left:16px;"><span class="ni msi">group</span><span class="nl" style="display:flex;align-items:center;gap:5px;overflow:hidden;">${_iconHtml}<span style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${esc(grp.name)}</span><span style="flex-shrink:0;">· ${grp.instances.length}</span></span><span class="nav-group-arrow msi nl" id="${_subKey}Chevron">${_subChev}</span></div>`;
                h += `<div id="${_subKey}FriendsSection" class="friend-section-items${friendSectionCollapsed[_subKey] ? ' collapsed' : ''}">`;
                grp.instances.forEach(inst => {
                    const _loc = (inst.location || '').replace(/'/g, "\\'");
                    const _thumbHtml = inst.worldThumb ? `<img class="vrc-gi-world-thumb" src="${inst.worldThumb}" loading="lazy" decoding="async" onerror="this.style.display='none'">` : '';
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

    const ingameFriends = gameFriends.filter(f => !favIds.has(f.id));
    appendSection('ingame', ingameFriends.length, ingameFriends.slice(0, 100), 'game');
    appendSection('web', wc, webFriends.slice(0, 100), 'web');
    appendSection('offline', oc, offlineFriends.slice(0, 100), 'offline');

    el.innerHTML = h;
    // Only apply search filter if there is an active query
    const _activeQ = (document.getElementById('vrcFriendSearchInput')?.value || '').toLowerCase().trim();
    if (_activeQ) filterFriendsList();
}

function onSidebarGroupInstances(instances) {
    _sidebarGroupInstances = instances || [];
    document.getElementById('vrcFriendRefreshBtn')?.classList.remove('spinning');
    if (vrcFriendsData && vrcFriendsData.length) renderVrcFriends(vrcFriendsData);
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
        el.innerHTML = `<div style="padding:16px;text-align:center;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('profiles.people.no_results', 'No results')}</div>`;
        return;
    }

    const countLabel = all.length > 100
        ? `<div style="padding:6px 12px 2px;font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);">${tf('profiles.friends.search.showing', { total: all.length }, 'Showing 100 of {total} results')}</div>`
        : '';

    let h = countLabel + `<div class="friend-section-items">`;
    capped.forEach(f => {
        const img = f.image || '';
        const imgTag = img
            ? `<img class="vrc-friend-avatar" src="${img}" loading="lazy" decoding="async" onerror="this.style.display='none'">`
            : `<div class="vrc-friend-avatar" style="display:flex;align-items:center;justify-content:center;font-size:calc(12px + var(--fs-off, 0px));font-weight:700;color:var(--tx3)">${esc((f.displayName || '?')[0])}</div>`;
        const presenceType = f.presence || 'offline';
        const statusCls = presenceType === 'offline' ? 's-offline' : statusDotClass(f.status);
        const rank = getTrustRank(f.tags || []);
        const useRankColor = (typeof settings !== 'undefined') && settings.friendsSidebarRankColor === true;
        const rankBadge = (rank && !useRankColor) ? `<span class="vrcn-badge ${rank.cls}">${rank.label}</span>` : '';
        const nameColorStyle = (useRankColor && rank) ? `color:${rank.color};` : '';
        const fid = (f.id || '').replace(/'/g, "\\'");
        const statusText = f.statusDescription || statusLabel(f.status);
        const locationText = getFriendLocationLabel(presenceType, f.location);
        const badgeDotCls = presenceType === 'web' ? 'vrc-status-ring' : 'vrc-status-dot';
        const avatarWrap = `<div class="vrc-friend-avatar-wrap">${imgTag}${(typeof iconFrameHtml === 'function') ? iconFrameHtml(f.iconFrameUrl) : ''}<span class="vrc-friend-status-badge ${badgeDotCls} ${statusCls}"></span></div>`;
        h += `<div class="vrc-friend-card" data-uid="${fid}" data-status="${statusCls}" onclick="openFriendDetail('${fid}')">${(typeof nameplateDecoHtml === 'function') ? nameplateDecoHtml(f.nameplateUrl) : ''}${avatarWrap}<div class="vrc-friend-info"><div class="vrc-friend-name"><span style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;${nameColorStyle}">${esc(f.displayName)}</span>${rankBadge}</div><div class="vrc-friend-loc">${_friendLocLineInner(f, presenceType, statusText, locationText)}</div></div></div>`;
    });
    h += `</div>`;
    el.innerHTML = h;
}
