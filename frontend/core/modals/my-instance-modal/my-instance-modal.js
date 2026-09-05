/* === My Instance Modal === */
const _instanceDetailCache = {};
const _instanceEnrichedData = {};
let _miModalWorldId = null;
const _miBannerImgs = {};
function _getMiBannerImg(worldId, src) {
    if (!worldId || !src) return null;
    if (!_miBannerImgs[worldId]) {
        const img = new Image();
        img.className = 'mi-world-banner';
        img.src = src;
        img.onerror = () => { if (img.parentElement) img.parentElement.style.display = 'none'; };
        _miBannerImgs[worldId] = { img, src };
    } else if (_miBannerImgs[worldId].src !== src) {
        _miBannerImgs[worldId].img.src = src;
        _miBannerImgs[worldId].src = src;
    }
    return _miBannerImgs[worldId].img;
}

function closeMyInstanceDetail(silent) {
    const m = document.getElementById('modalMyInstance');
    if (m) m.style.display = 'none';
    if (!silent) navClear();
    _miModalWorldId = null;
}

function _reopenCachedInstance(location) {
    const fn = _instanceDetailCache[location];
    if (typeof fn === 'function') fn();
}

function openInstanceDetailFromData(inst) {
    if (!inst || inst.error) { showToast(false, t('instance.load_error', 'Could not load instance.')); return; }
    if (inst.location) {
        _instanceDetailCache[inst.location] = () => setInstanceModal(inst);
        navOpenModal('instance', inst.location, inst.worldName || '');
    } else {
        setInstanceModal(inst);
    }
}

function _miAuthorHtml(authorId, authorName) {
    if (!authorName) return '';
    const badge = authorId
        ? `<span onclick="navOpenModal('friend','${jsq(authorId)}','${jsq(authorName)}')" style="display:inline-flex;align-items:center;padding:1px 8px;border-radius:20px;background:var(--badge-bg);font-size:calc(11px + var(--fs-off, 0px));font-weight:600;color:var(--tx0);cursor:pointer;line-height:1.8;">${esc(authorName)}</span>`
        : esc(authorName);
    return `<div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx2);margin-bottom:2px;">${t('worlds.meta.by', 'by')} ${badge}</div>`;
}

// Called from messages.js when backend sends worldInstancesDetail
function handleWorldInstancesDetail(payload) {
    if (!payload) return;

    if (payload.instances) {
        payload.instances.forEach(inst => {
            if (inst.location) _instanceEnrichedData[inst.location] = inst;
        });
    }

    if (payload.world && payload.worldId && typeof dashWorldCache !== 'undefined') {
        const w = payload.world;
        dashWorldCache[payload.worldId] = Object.assign({}, dashWorldCache[payload.worldId], {
            name:              w.name || dashWorldCache[payload.worldId]?.name || '',
            authorName:        w.authorName || '',
            authorId:          w.authorId || '',
            description:       w.description || '',
            thumbnailImageUrl: w.thumb || dashWorldCache[payload.worldId]?.thumbnailImageUrl || '',
            _descFetched:      true,
        });
        const iim = document.getElementById('modalInstanceInfo');
        if (iim && iim.style.display !== 'none'
            && typeof currentInstanceData !== 'undefined' && currentInstanceData?.worldId === payload.worldId
            && typeof openInstanceInfoModal === 'function') {
            openInstanceInfoModal();
        }
    }

    const m = document.getElementById('modalMyInstance');
    if (!m || m.style.display === 'none' || _miModalWorldId !== payload.worldId) return;

    // Update left panel in-place from world payload
    if (payload.world) {
        const w = payload.world;
        if (w.name) {
            const nameEl = m.querySelector('.mi-world-name');
            if (nameEl) nameEl.textContent = w.name;
        }
        if (w.thumb) {
            const s = m.querySelector('#mi-banner-slot');
            const bi = _getMiBannerImg(payload.worldId, w.thumb);
            if (s && bi) s.insertBefore(bi, s.firstChild);
        }
        if (w.authorId || w.authorName) {
            const authorEl = m.querySelector('.mi-world-author');
            if (authorEl) authorEl.innerHTML = _miAuthorHtml(w.authorId, w.authorName);
        }
        if (w.description) {
            const descEl = m.querySelector('.mi-world-description');
            if (descEl) descEl.textContent = w.description;
        }
    }

    // Update instance cards in-place
    if (payload.instances) {
        payload.instances.forEach(inst => _miApplyEnrichedData(inst));
    }
}

function _miApplyEnrichedData(inst) {
    const locBase = (inst.location || '').split('~')[0];
    const card = document.querySelector(`.mi-instance-card[data-miloc="${CSS.escape(locBase)}"]`);
    if (!card) return;

    // Occupant count
    if (inst.userCount != null) {
        const occEl = card.querySelector('.mi-inst-occ');
        if (occEl) {
            occEl.innerHTML = `<span class="msi" style="font-size:11px;">person</span>${inst.userCount}${inst.capacity ? '/' + inst.capacity : ''}`;
            occEl.style.display = '';
        }
    }

    // Display name
    if (inst.displayName) {
        const dnEl = card.querySelector('.mi-inst-display-name');
        if (dnEl) { dnEl.textContent = inst.displayName; dnEl.style.display = ''; }
    }

    // Region → header badge
    const region = (inst.region || '').toUpperCase() || _parseRegionFromLocation(inst.location || '');
    if (region) {
        const regEl = card.querySelector('.mi-inst-region');
        if (regEl) {
            regEl.innerHTML = `<span class="msi" style="font-size:10px;">language</span>${region}`;
            regEl.style.display = '';
        }
    }

    // Platform % → header badges
    const pcCnt  = inst.platforms?.pc      || 0;
    const andCnt = inst.platforms?.android || 0;
    const total  = pcCnt + andCnt + (inst.platforms?.ios || 0);
    const platEl = card.querySelector('.mi-inst-platforms');
    if (platEl && total > 0) {
        const pcPct  = Math.round((pcCnt  / total) * 100);
        const andPct = Math.round((andCnt / total) * 100);
        let platHtml = `<span class="vrcn-badge"><span class="msi" style="font-size:10px;">computer</span>${pcPct}%</span>`;
        if (andCnt > 0) platHtml += `<span class="vrcn-badge"><span class="msi" style="font-size:10px;">view_in_ar</span>${andPct}%</span>`;
        platEl.innerHTML = platHtml;
    }

    // Queue → stats row
    const statsEl = card.querySelector('.mi-inst-stats');
    if (statsEl) {
        const html = _buildInstStatsHtml(inst);
        if (html) { statsEl.innerHTML = html; statsEl.style.display = ''; }
        else { statsEl.style.display = 'none'; }
    }
}

function _parseRegionFromLocation(loc) {
    const m = (loc || '').match(/~region\(([^)]+)\)/);
    return m ? m[1].toUpperCase() : '';
}

function _buildInstStatsHtml(inst) {
    if (!inst.queueEnabled) return '';
    return `<div class="mi-stats-inner"><span class="mi-stat-item"><span class="msi">queue</span>${t('instance.queue', 'Queue')} ${inst.queueSize || 0}</span></div>`;
}

function _buildInstanceCard(locBase, g, isMyInstance) {
    const { cls, label: typeLabel } = getInstanceBadge(g.instanceType);
    const instId = g.instId || '';
    const loc    = (g.location || '').replace(/'/g, "\\'");
    const mloc   = jsq(g.location || '');

    const copyBadge = instId
        ? `<span class="vrcn-badge" style="cursor:pointer;" onclick="copyInstanceLink('${mloc}')"><span class="msi" style="font-size:10px;">content_copy</span>#${esc(instId)}</span>`
        : '';

    const ageGateBadge = (g.location || '').includes('~ageGate')
        ? `<span class="vrcn-badge" style="background:rgba(255,75,85,.15);color:var(--err);">${esc(t('worlds.instances.age_gated', 'Age Gated'))}</span>`
        : '';

    const ownerBadge = isMyInstance && g.ownerId
        ? getOwnerBadgeHtml(g.ownerId, g.ownerName || '', g.ownerGroup || '', 'closeMyInstanceDetail()')
        : '';

    // Friends list — full profile items
    const friends = g.friends || [];

    // Occupant count — real API data only (filled immediately if available, else by _miApplyEnrichedData)
    const userCount = g.userCount;
    const capacity  = g.capacity;
    const occInner  = userCount != null
        ? `<span class="msi" style="font-size:11px;">person</span>${userCount}${capacity ? '/' + capacity : ''}`
        : '';
    const occHtml = `<span class="mi-inst-occ vrcn-badge"${userCount == null ? ' style="display:none"' : ''}>${occInner}</span>`;

    // Initial enriched data (may already be cached from a prior fetch)
    const enriched  = _instanceEnrichedData[g.location] || {};
    const initInst  = {
        region:       _parseRegionFromLocation(g.location || '') || g.region || enriched.region || '',
        queueEnabled: g.queueEnabled ?? enriched.queueEnabled,
        queueSize:    g.queueSize    ?? enriched.queueSize,
        platforms:    g.platforms    || enriched.platforms,
    };

    // Header: region badge
    const initRegion = (initInst.region || '').toUpperCase();
    const regionHtml = initRegion
        ? `<span class="mi-inst-region vrcn-badge"><span class="msi" style="font-size:10px;">language</span>${esc(initRegion)}</span>`
        : `<span class="mi-inst-region vrcn-badge" style="display:none;"></span>`;

    // Header: platform badges (empty placeholder if not yet known)
    let initPlatHtml = '';
    if (initInst.platforms) {
        const pcCnt  = initInst.platforms.pc      || 0;
        const andCnt = initInst.platforms.android || 0;
        const total  = pcCnt + andCnt + (initInst.platforms.ios || 0);
        if (total > 0) {
            const pcPct  = Math.round((pcCnt  / total) * 100);
            const andPct = Math.round((andCnt / total) * 100);
            initPlatHtml = `<span class="vrcn-badge"><span class="msi" style="font-size:10px;">computer</span>${pcPct}%</span>`;
            if (andCnt > 0) initPlatHtml += `<span class="vrcn-badge"><span class="msi" style="font-size:10px;">view_in_ar</span>${andPct}%</span>`;
        }
    }
    const platformsHtml = `<span class="mi-inst-platforms">${initPlatHtml}</span>`;

    // Queue in stats row only
    const statsHtml = _buildInstStatsHtml(initInst);
    let friendsHtml;
    if (friends.length > 0) {
        friendsHtml = friends.map(f =>
            renderProfileItem(f, `navOpenModal('friend','${jsq(f.id || '')}','${jsq(f.displayName || '')}')`)
        ).join('');
    } else {
        friendsHtml = `<div class="vrcn-user-item" style="pointer-events:none;opacity:0.55;">
            <div class="vrcn-user-item-avatar vrcn-user-item-avatar-letter"><span class="msi" style="font-size:20px;color:var(--tx2);">person</span></div>
            <div class="vrcn-user-item-info"><div class="vrcn-user-item-name">${t('dashboard.instances.no_friends_title', 'No friends here yet!')}</div></div>
        </div>`;
    }

    // Per-card action buttons
    let actionsHtml;
    if (isMyInstance) {
        const mwn = jsq(g.worldName  || '');
        const mwt = jsq(g.worldThumb || '');
        const mit = jsq(g.instanceType || '');
        actionsHtml = `
            <button class="vrcn-button-round vrcn-btn-join" title="${esc(t('dashboard.instances.join_world', 'Join World'))}" onclick="sendToCS({action:'vrcJoinFriend',location:'${loc}'})"><span class="msi" style="font-size:14px;">login</span></button>
            <button class="vrcn-button-round" title="${esc(t('dashboard.instances.invite_friends', 'Invite Friends'))}" onclick="closeMyInstanceDetail();openInviteModalForLocation('${mloc}','${mwn}','${mwt}','${mit}')"><span class="msi" style="font-size:14px;">person_add</span></button>
            <button class="vrcn-button-round vrcn-btn-danger" title="${esc(t('dashboard.instances.remove_instance', 'Remove Instance'))}" onclick="removeMyInstance('${loc}')"><span class="msi" style="font-size:14px;">delete</span></button>`;
    } else {
        actionsHtml = `<button class="vrcn-button-round vrcn-btn-join" title="${esc(t('dashboard.instances.join_world', 'Join World'))}" onclick="sendToCS({action:'vrcJoinFriend',location:'${loc}'})"><span class="msi" style="font-size:14px;">login</span></button>`;
    }

    return `<div class="mi-instance-card" data-miloc="${esc(locBase)}">
        <div class="mi-instance-header">
            <span class="vrcn-badge ${cls}">${typeLabel}</span>
            ${copyBadge}
            ${ageGateBadge}${ownerBadge}
            <span class="mi-inst-display-name vrcn-badge" style="display:none;"></span>
            ${regionHtml}${platformsHtml}
            ${occHtml}
            <div class="mi-header-actions">${actionsHtml}</div>
        </div>
        <div class="mi-inst-stats"${!statsHtml ? ' style="display:none"' : ''}>${statsHtml}</div>
        <div class="mi-friends-list">${friendsHtml}</div>
    </div>`;
}

function setInstanceModal(inst) {
    const m = document.getElementById('modalMyInstance');
    const c = document.getElementById('myInstanceContent');
    if (!m || !c) return;

    const thumb     = inst.worldThumb || '';
    const worldId   = inst.worldId || (inst.location || '').split(':')[0] || '';
    const worldName = inst.worldName || worldId || t('dashboard.instances.unknown_world', 'Unknown World');

    _miModalWorldId = worldId;

    const miBar = (typeof renderModalActions === 'function')
        ? renderModalActions([{ icon: 'close', title: t('common.close', 'Close'), onclick: 'closeMyInstanceDetail()' }])
        : '';

    const instanceGroups = {}; // locBase → { friends, instanceType, instId, location }

    if (inst.allInstances) {
        // Dashboard Friends Location: group all friends in this world by instance
        const allWorldFriends = (typeof vrcFriendsData !== 'undefined')
            ? vrcFriendsData.filter(f => f.location && f.location.split(':')[0] === worldId)
            : [];
        const _selfM = (typeof _selfInstanceMember === 'function') ? _selfInstanceMember() : null;
        if (_selfM && _selfM.location.split(':')[0] === worldId && !allWorldFriends.some(f => f.id === _selfM.id)) {
            allWorldFriends.unshift(_selfM);
        }
        allWorldFriends.forEach(f => {
            const key = f.location.split('~')[0];
            if (!instanceGroups[key]) {
                const { instanceType: iType } = (typeof parseFriendLocation === 'function')
                    ? parseFriendLocation(f.location) : { instanceType: 'public' };
                instanceGroups[key] = {
                    friends: [], instanceType: iType,
                    instId: key.includes(':') ? key.split(':')[1] : '',
                    location: f.location,
                };
            }
            instanceGroups[key].friends.push(f);
        });
        const locations = Object.values(instanceGroups).map(g => g.location).filter(Boolean);
        if (locations.length > 0) sendToCS({ action: 'vrcGetWorldInstancesDetail', worldId, locations });
    } else {
        // Group Activity / Sidebar: specific instance
        const locBase = (inst.location || '').split('~')[0];
        const instId  = locBase.includes(':') ? locBase.split(':')[1] : '';
        const locFriends = (typeof vrcFriendsData !== 'undefined')
            ? vrcFriendsData.filter(f => f.location && f.location.split('~')[0] === locBase)
            : [];
        const _selfMi = (typeof _selfInstanceMember === 'function') ? _selfInstanceMember() : null;
        if (_selfMi && _selfMi.location.split('~')[0] === locBase && !locFriends.some(f => f.id === _selfMi.id)) {
            locFriends.unshift(_selfMi);
        }
        const curBase  = (currentInstanceData?.location || '').split('~')[0];
        const logUsers = (locBase && curBase === locBase && currentInstanceData?.users) ? currentInstanceData.users : [];
        const friendById = {};
        if (typeof vrcFriendsData !== 'undefined') vrcFriendsData.forEach(f => { if (f.id) friendById[f.id] = f; });
        const seenIds = new Set(locFriends.map(f => f.id));
        logUsers.forEach(u => { if (u.id && !seenIds.has(u.id) && friendById[u.id]) { locFriends.push(friendById[u.id]); seenIds.add(u.id); } });
        instanceGroups[locBase] = { friends: locFriends, instanceType: inst.instanceType, instId, location: inst.location };
        sendToCS({ action: 'vrcGetWorldInstancesDetail', worldId, locations: [inst.location] });
    }

    const groupKeys    = Object.keys(instanceGroups);
    const totalFriends = groupKeys.reduce((s, k) => s + instanceGroups[k].friends.length, 0);

    const rightHtml = groupKeys.length === 0
        ? `<div style="padding:24px;text-align:center;color:var(--tx0);font-size:calc(12px + var(--fs-off, 0px));">${t('dashboard.instances.no_friends_title', 'No friends here yet!')}</div>`
        : groupKeys.map(key => _buildInstanceCard(key, instanceGroups[key], false)).join('');

    c.innerHTML = `${miBar}<div class="mi-layout">
        <div class="mi-left">
            <div class="mi-world-banner-wrap"><div id="mi-banner-slot"><div class="mi-world-banner-fade"></div></div></div>
            <div class="mi-world-info">
                <div class="mi-world-name">${esc(worldName)}</div>
                <div class="mi-world-author"></div>
                <div class="mi-world-description"></div>
            </div>
            <div class="mi-left-actions">
                <button class="vrcn-button-round mi-action-btn" onclick="navOpenModal('worldSearch','${jsq(worldId)}','')">${t('dashboard.instances.open_world', 'Open World')}</button>
            </div>
        </div>
        <div class="mi-right"><div class="mi-right-scroll"><div class="mi-instance-list">${rightHtml}</div></div></div>
    </div>`;
    if (thumb) { const s = c.querySelector('#mi-banner-slot'); const bi = _getMiBannerImg(worldId, thumb); if (s && bi) s.insertBefore(bi, s.firstChild); }
    m.style.display = 'flex';
}

function openMyInstanceDetail(worldId, location) {
    const inst = _myInstancesData.find(i => i.location === location) || _myInstancesData.find(i => i.worldId === worldId);
    if (!inst) return;
    _instanceDetailCache[inst.location] = () => setOwnInstanceModal(inst);
    navOpenModal('instance', inst.location, inst.worldName || '');
}

function setOwnInstanceModal(inst) {
    const m = document.getElementById('modalMyInstance');
    const c = document.getElementById('myInstanceContent');
    if (!m || !c) return;

    const thumb     = inst.worldThumb || '';
    const worldId   = inst.worldId || '';
    const worldName = inst.worldName || worldId || t('dashboard.instances.unknown_world', 'Unknown World');

    _miModalWorldId = worldId;

    const miBar = (typeof renderModalActions === 'function')
        ? renderModalActions([{ icon: 'close', title: t('common.close', 'Close'), onclick: 'closeMyInstanceDetail()' }])
        : '';

    const locBase = (inst.location || '').split('~')[0];
    const instId  = locBase.includes(':') ? locBase.split(':')[1] : '';

    const locFriends = (typeof vrcFriendsData !== 'undefined')
        ? vrcFriendsData.filter(f => f.location && f.location.split('~')[0] === locBase)
        : [];
    const _selfMo = (typeof _selfInstanceMember === 'function') ? _selfInstanceMember() : null;
    if (_selfMo && _selfMo.location.split('~')[0] === locBase && !locFriends.some(f => f.id === _selfMo.id)) {
        locFriends.unshift(_selfMo);
    }
    const curBase  = (currentInstanceData?.location || '').split('~')[0];
    const logUsers = (locBase && curBase === locBase && currentInstanceData?.users) ? currentInstanceData.users : [];
    const friendById = {};
    if (typeof vrcFriendsData !== 'undefined') vrcFriendsData.forEach(f => { if (f.id) friendById[f.id] = f; });
    const seenIds = new Set(locFriends.map(f => f.id));
    logUsers.forEach(u => { if (u.id && !seenIds.has(u.id) && friendById[u.id]) { locFriends.push(friendById[u.id]); seenIds.add(u.id); } });

    const groupData = {
        friends:      locFriends,
        instanceType: inst.instanceType,
        instId,
        location:     inst.location,
        worldName,
        worldThumb:   thumb,
        userCount:    inst.userCount,
        capacity:     inst.capacity,
        region:       inst.region,
        ownerId:      inst.ownerId,
        ownerName:    inst.ownerName,
        ownerGroup:   inst.ownerGroup,
    };

    if (inst.location) sendToCS({ action: 'vrcGetWorldInstancesDetail', worldId, locations: [inst.location] });

    const playerCount = inst.userCount != null ? `${inst.userCount}${inst.capacity ? '/' + inst.capacity : ''}` : '';

    c.innerHTML = `${miBar}<div class="mi-layout">
        <div class="mi-left">
            <div class="mi-world-banner-wrap"><div id="mi-banner-slot"><div class="mi-world-banner-fade"></div></div></div>
            <div class="mi-world-info">
                <div class="mi-world-name">${esc(worldName)}</div>
                <div class="mi-world-author">${_miAuthorHtml(inst.authorId || '', inst.authorName || '')}</div>
                <div class="mi-world-description"></div>
            </div>
            <div class="mi-left-actions">
                <button class="vrcn-button-round mi-action-btn" onclick="navOpenModal('worldSearch','${jsq(worldId)}','')">${t('dashboard.instances.open_world', 'Open World')}</button>
            </div>
        </div>
        <div class="mi-right"><div class="mi-right-scroll"><div class="mi-instance-list">${_buildInstanceCard(locBase, groupData, true)}</div></div></div>
    </div>`;
    if (thumb) { const s = c.querySelector('#mi-banner-slot'); const bi = _getMiBannerImg(worldId, thumb); if (s && bi) s.insertBefore(bi, s.firstChild); }
    m.style.display = 'flex';
}

function _miWmState(s) {
    if (s === undefined) return { worldId: _miModalWorldId };
    _miModalWorldId = (s || {}).worldId ?? null;
}
