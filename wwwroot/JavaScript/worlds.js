/* === Search (Worlds, Groups, People) === */
/* === World Tab: Favorites / Search filter === */
let _favRefreshTimer = null;
let _wdLiveTimer = null;
function _scheduleBgFavRefresh() {
    clearTimeout(_favRefreshTimer);
    _favRefreshTimer = setTimeout(() => sendToCS({ action: 'vrcGetFavoriteWorlds' }), 2000);
}
function refreshFavWorlds() {
    const btn = document.getElementById('favWorldsRefreshBtn');
    if (btn) { btn.disabled = true; btn.querySelector('.msi').textContent = 'hourglass_empty'; }
    sendToCS({ action: 'vrcGetFavoriteWorlds' });
}
let _myWorldsLoaded = false;

function setWorldFilter(filter) {
    worldFilter = filter;
    document.getElementById('worldFilterFav').classList.toggle('active', filter === 'favorites');
    document.getElementById('worldFilterMine').classList.toggle('active', filter === 'mine');
    document.getElementById('worldFilterSearch').classList.toggle('active', filter === 'search');
    document.getElementById('worldFavArea').style.display    = filter === 'favorites' ? '' : 'none';
    document.getElementById('worldMineArea').style.display   = filter === 'mine'      ? '' : 'none';
    document.getElementById('worldSearchArea').style.display = filter === 'search'    ? '' : 'none';
    if (filter === 'favorites' && favWorldsData.length === 0) sendToCS({ action: 'vrcGetFavoriteWorlds' });
    if (filter === 'mine' && !_myWorldsLoaded) {
        _myWorldsLoaded = true;
        sendToCS({ action: 'vrcGetMyWorlds' });
    }
}

function renderMyWorlds(worlds) {
    const el = document.getElementById('worldMineGrid');
    if (!el) return;
    if (!Array.isArray(worlds) || worlds.length === 0) {
        el.innerHTML = '<div class="empty-msg">No worlds uploaded yet</div>';
        return;
    }
    el.innerHTML = worlds.map(w => renderWorldCard(w)).join('');
}

function _wdGroupOptionLabel(g) {
    const count = favWorldsData.filter(w => w.favoriteGroup === g.name).length;
    const cap   = Math.max(g.capacity || 100, 100);
    const isVrcPlus = g.type === 'vrcPlusWorld';
    return `${esc(g.displayName || g.name)} ${count}/${cap}${isVrcPlus ? ' [VRC+]' : ''}`;
}

function renderFavWorlds(payload) {
    // Reset refresh button if it was spinning
    const refreshBtn = document.getElementById('favWorldsRefreshBtn');
    if (refreshBtn) { refreshBtn.disabled = false; const ico = refreshBtn.querySelector('.msi'); if (ico) ico.textContent = 'refresh'; }
    // payload is { worlds: [...], groups: [...] }
    const worlds = payload?.worlds || payload || [];
    const groups = payload?.groups || [];
    favWorldsData = worlds;
    favWorldGroups = groups;
    // Populate world info cache for library badges
    favWorldsData.forEach(w => {
        if (w.id) worldInfoCache[w.id] = { id: w.id, name: w.name, thumbnailImageUrl: w.thumbnailImageUrl || w.imageUrl };
    });
    // Populate group dropdown
    const sel = document.getElementById('favWorldGroupFilter');
    if (sel) {
        const prev = favWorldGroupFilter;
        sel.innerHTML = '<option value="">All Favorites</option>' +
            groups.map(g => `<option value="${esc(g.name)}">${_wdGroupOptionLabel(g)}</option>`).join('');
        const stillValid = groups.some(g => g.name === prev);
        favWorldGroupFilter = stillValid ? prev : '';
        sel.value = favWorldGroupFilter;
        if (sel._vnRefresh) sel._vnRefresh();
    }
    updateFavWorldGroupHeader();
    filterFavWorlds();
}

function setFavWorldGroup(val) {
    favWorldGroupFilter = val;
    cancelEditWorldGroupName();
    updateFavWorldGroupHeader();
    filterFavWorlds();
}

function updateFavWorldGroupHeader() {
    const label = document.getElementById('favWorldGroupLabel');
    const editBtn = document.getElementById('favWorldGroupEditBtn');
    const badge = document.getElementById('favWorldGroupVrcPlusBadge');
    if (!label) return;
    if (!favWorldGroupFilter) {
        label.textContent = 'All Favorites';
        if (editBtn) editBtn.style.display = 'none';
        if (badge) badge.style.display = 'none';
    } else {
        const g = favWorldGroups.find(x => x.name === favWorldGroupFilter);
        label.textContent = g ? (g.displayName || g.name) : favWorldGroupFilter;
        if (editBtn) editBtn.style.display = '';
        if (badge) badge.style.display = (g?.type === 'vrcPlusWorld') ? '' : 'none';
    }
}

function startEditWorldGroupName() {
    const g = favWorldGroups.find(x => x.name === favWorldGroupFilter);
    if (!g) return;
    const input = document.getElementById('favWorldGroupNameInput');
    if (input) input.value = g.displayName || g.name;
    document.getElementById('favWorldGroupHeader').style.display = 'none';
    const row = document.getElementById('favWorldGroupRenameRow');
    if (row) { row.style.display = 'flex'; }
    if (input) input.focus();
}

function cancelEditWorldGroupName() {
    document.getElementById('favWorldGroupHeader').style.display = 'flex';
    const row = document.getElementById('favWorldGroupRenameRow');
    if (row) row.style.display = 'none';
    const saveBtn = document.querySelector('#favWorldGroupRenameRow .vrcn-btn-primary');
    if (saveBtn) { saveBtn.disabled = false; saveBtn.textContent = 'Save'; }
}

function saveWorldGroupName() {
    const g = favWorldGroups.find(x => x.name === favWorldGroupFilter);
    if (!g) return;
    const input = document.getElementById('favWorldGroupNameInput');
    const newName = (input?.value || '').trim();
    if (!newName) return;
    const saveBtn = document.querySelector('#favWorldGroupRenameRow .vrcn-btn-primary');
    if (saveBtn) { saveBtn.disabled = true; saveBtn.textContent = 'Saving...'; }
    sendToCS({ action: 'vrcUpdateFavoriteGroup', groupType: g.type, groupName: g.name, displayName: newName });
}

function onFavoriteGroupUpdated(data) {
    if (!data.ok) { cancelEditWorldGroupName(); return; }
    const g = favWorldGroups.find(x => x.name === data.groupName);
    if (g) g.displayName = data.displayName;
    // Update dropdown option
    const sel = document.getElementById('favWorldGroupFilter');
    if (sel) {
        const opt = [...sel.options].find(o => o.value === data.groupName);
        if (opt && g) opt.textContent = _wdGroupOptionLabel(g);
    }
    cancelEditWorldGroupName();
    updateFavWorldGroupHeader();
}

/* === Shared world card renderer (search + favorites) === */
function renderWorldCard(w) {
    const thumb = w.thumbnailImageUrl || w.imageUrl || '';
    const desc = w.description ? w.description.substring(0, 100) + (w.description.length > 100 ? '...' : '') : '';
    const tags = (w.tags || []).filter(t => t.startsWith('author_tag_')).map(t => t.replace('author_tag_','')).slice(0,4);
    const tagsHtml = tags.length ? `<div class="s-card-tags">${tags.map(t => `<span class="vrcn-badge">${esc(t)}</span>`).join('')}</div>` : '';
    const wid = jsq(w.id);
    const ts = w.worldTimeSeconds || 0;
    const timeBadge = ts > 0 ? `<div class="s-card-time-badge"><span class="msi" style="font-size:11px;">schedule</span> ${formatDuration(ts)}</div>` : '';
    return `<div class="s-card" onclick="openWorldSearchDetail('${wid}')">
        <div class="s-card-img" style="background-image:url('${cssUrl(thumb)}')">${timeBadge}</div>
        <div class="s-card-body"><div class="s-card-title">${esc(w.name)}</div>
        <div class="s-card-sub">${esc(w.authorName)} · <span class="msi" style="font-size:11px;">person</span> ${w.occupants} · <span class="msi" style="font-size:11px;">star</span> ${w.favorites}</div>
        ${desc ? `<div class="s-card-desc">${esc(desc)}</div>` : ''}
        ${tagsHtml}</div></div>`;
}

function filterFavWorlds() {
    const q = (document.getElementById('favWorldSearchInput')?.value || '').toLowerCase();
    let filtered = favWorldsData;
    if (favWorldGroupFilter) filtered = filtered.filter(w => w.favoriteGroup === favWorldGroupFilter);
    if (q) filtered = filtered.filter(w => (w.name||'').toLowerCase().includes(q) || (w.authorName||'').toLowerCase().includes(q));
    const el = document.getElementById('favWorldsGrid');
    if (!filtered.length) {
        el.innerHTML = `<div class="empty-msg">${q || favWorldGroupFilter ? 'No favorites match your filter' : 'No favorite worlds found'}</div>`;
        return;
    }
    el.innerHTML = filtered.map(w => renderWorldCard(w)).join('');
}

/* === Detail Modals (shared) === */
function openWorldSearchDetail(id) {
    const el = document.getElementById('detailModalContent');
    el.innerHTML = sk('detail');
    document.getElementById('modalDetail').style.display = 'flex';
    sendToCS({ action: 'vrcGetWorldDetail', worldId: id });
}

function renderWorldSearchDetail(w) {
    // Cache full world data so favorites grid can render it immediately after favoriting
    if (w.id) worldInfoCache[w.id] = w;
    const el = document.getElementById('detailModalContent');
    const thumb = w.thumbnailImageUrl || w.imageUrl || '';
    const desc = w.description || '';
    const wid = w.id || '';
    const authorTags = (w.tags || []).filter(t => t.startsWith('author_tag_')).map(t => t.replace('author_tag_', ''));
    const systemTags = (w.tags || []).filter(t => !t.startsWith('author_tag_') && !t.startsWith('system_') && !t.startsWith('admin_'));

    // Tags HTML
    let tagsHtml = '';
    if (authorTags.length || systemTags.length) {
        const allTags = [...authorTags, ...systemTags].slice(0, 12);
        tagsHtml = `<div class="fd-lang-tags">${allTags.map(t => `<span class="vrcn-badge">${esc(t)}</span>`).join('')}</div>`;
    }

    // Active instances HTML
    const regionLabels = { us: 'US West', use: 'US East', eu: 'Europe', jp: 'Japan' };

    // Build friend-by-location map for this world (for friend badges + inferred instances)
    const worldFriendsByLoc = {};
    if (typeof vrcFriendsData !== 'undefined') {
        vrcFriendsData.forEach(f => {
            const { worldId: fwid } = parseFriendLocation(f.location);
            if (fwid === w.id) {
                if (!worldFriendsByLoc[f.location]) worldFriendsByLoc[f.location] = [];
                worldFriendsByLoc[f.location].push(f);
            }
        });
    }

    // Merge API instances with friend-inferred non-public instances
    // Strip nonce for comparison: API instances don't have ~nonce(...) but friend locations do
    const stripNonce = l => (l || '').replace(/~nonce\([^)]*\)/g, '');
    const allInstances = [...(w.instances || [])];
    Object.keys(worldFriendsByLoc).forEach(loc => {
        const existing = allInstances.find(i => stripNonce(i.location) === stripNonce(loc));
        if (existing) {
            // Update location to friend's key so friend badges + sort work correctly
            existing.location = loc;
        } else {
            const { instanceType: iType } = parseFriendLocation(loc);
            const regionMatch = loc.match(/region\(([^)]+)\)/);
            allInstances.push({
                instanceId: loc.includes(':') ? loc.split(':')[1] : loc,
                users: worldFriendsByLoc[loc].length,
                type: iType,
                region: regionMatch ? regionMatch[1] : 'us',
                location: loc
            });
        }
    });

    // Instances with friends first
    allInstances.sort((a, b) => ((worldFriendsByLoc[b.location] || []).length) - ((worldFriendsByLoc[a.location] || []).length));

    let instancesHtml = '';
    if (allInstances.length > 0) {
        instancesHtml = `<div class="wd-section-label" style="margin-top:4px;">ACTIVE INSTANCES (${allInstances.length})</div><div class="wd-instances-list">`;
        allInstances.forEach(inst => {
            instancesHtml += renderInstanceItem({
                instanceType: inst.type,
                instanceId:   inst.instanceId || '',
                owner:        inst.ownerName  || '',
                ownerGroup:   inst.ownerGroup || '',
                ownerId:      inst.ownerId    || '',
                region:       regionLabels[inst.region] || inst.region.toUpperCase(),
                userCount:    inst.users,
                capacity:     w.capacity || 0,
                friends:      worldFriendsByLoc[inst.location] || [],
                location:     inst.location,
            });
        });
        instancesHtml += '</div>';
    } else {
        instancesHtml = '<div style="font-size:11px;color:var(--tx3);margin-bottom:14px;">No active instances</div>';
    }

    // Create instance UI
    const createHtml = `<div class="wd-section-label" style="margin-top:6px;">CREATE INSTANCE</div>
        <div class="wd-create-row">
            <select id="ciType" class="wd-create-select" onchange="onCiTypeChange()">
                <option value="public">Public</option>
                <option value="friends">Friends</option>
                <option value="hidden">Friends+</option>
                <option value="invite_plus">Invite+</option>
                <option value="private">Invite</option>
                <optgroup label="─────────"></optgroup>
                <option value="group_public">Group Public</option>
                <option value="group_members">Group</option>
                <option value="group_plus">Group+</option>
            </select>
            <select id="ciRegion" class="wd-create-select">
                <option value="eu">Europe</option>
                <option value="us">US West</option>
                <option value="use">US East</option>
                <option value="jp">Japan</option>
            </select>
            <button class="vrcn-button" id="ciBtn" onclick="createWorldInstance('${esc(w.id)}')" style="background:var(--accent);color:#fff;"><span class="msi" style="font-size:14px;">add</span> Create & Join</button>
        </div>
        <div id="ciGroupRow" style="display:none;margin-top:8px;">
            <div style="font-size:11px;color:var(--tx3);margin-bottom:6px;">Select group for this instance:</div>
            <input type="hidden" id="ciGroupId" value="">
            <div class="ci-group-list" id="ciGroupList"></div>
        </div>`;

    const isFavWorld = favWorldsData.some(fw => fw.id === w.id);
    const favBtnLabel = isFavWorld
        ? '<span class="msi" style="font-size:16px;">star</span>Unfavorite'
        : '<span class="msi" style="font-size:16px;">star_outline</span>Favorite';

    el.innerHTML = `${thumb ? `<div class="fd-banner"><img src="${thumb}" onerror="this.parentElement.style.display='none'"><div class="fd-banner-fade"></div></div>` : ''}
        <div class="fd-content${thumb ? ' fd-has-banner' : ''}" style="padding:20px;">
        <h2 style="margin:0 0 4px;color:var(--tx0);font-size:18px;">${esc(w.name)}</h2>
        <div style="font-size:12px;color:var(--tx3);margin-bottom:12px;">by ${w.authorId ? `<span onclick="document.getElementById('modalDetail').style.display='none';openFriendDetail('${esc(w.authorId)}')" style="display:inline-flex;align-items:center;padding:1px 8px;border-radius:20px;background:var(--bg-hover);font-size:11px;font-weight:600;color:var(--tx1);cursor:pointer;line-height:1.8;">${esc(w.authorName)}</span>` : esc(w.authorName)}</div>
        <div class="fd-badges-row">
            <span class="vrcn-badge"><span class="msi" style="font-size:11px;">person</span> ${w.occupants} Active</span>
            <span class="vrcn-badge"><span class="msi" style="font-size:11px;">star</span> ${w.favorites}</span>
            <span class="vrcn-badge"><span class="msi" style="font-size:11px;">visibility</span> ${w.visits}</span>
        </div>
        <div style="margin:10px 0 6px;">
            <button class="vrcn-button-round${isFavWorld ? ' active' : ''}" id="wdFavBtn" onclick="toggleWorldFavPicker('${wid}')" style="margin-left:auto;">${favBtnLabel}</button>
        </div>
        <div id="wdFavPicker" style="display:none;margin-bottom:14px;">
            <div class="wd-section-label" style="margin-bottom:6px;">ADD TO FAVORITE GROUP</div>
            <div class="ci-group-list" id="wdFavGroupList"><div style="font-size:11px;color:var(--tx3);padding:8px 0;">Loading groups…</div></div>
        </div>
        ${(w.worldTimeSeconds > 0 || currentInstanceData?.worldId === wid) ? `<div class="wd-your-time"><span class="msi" style="font-size:15px;">schedule</span><div><div style="font-size:12px;font-weight:600;color:var(--tx1);">Your Time Spent</div><div style="font-size:11px;color:var(--tx3);"><span id="wdTimeSpent">${formatDuration(w.worldTimeSeconds || 0)}</span>${w.worldVisitCount > 0 ? ' · ' + w.worldVisitCount + ' visit' + (w.worldVisitCount > 1 ? 's' : '') : ''}</div></div></div>` : ''}
        <div style="margin-bottom:10px;">${idBadge(wid)}</div>
        ${desc ? `<div style="font-size:12px;color:var(--tx2);margin-bottom:14px;max-height:150px;overflow-y:auto;line-height:1.5;white-space:pre-wrap;">${esc(desc)}</div>` : ''}
        ${tagsHtml}
        <div class="fd-meta" style="margin-bottom:14px;">
            ${w.recommendedCapacity ? `<div class="fd-meta-row"><span class="fd-meta-label">Recommended</span><span>${w.recommendedCapacity} Players</span></div>` : ''}
            <div class="fd-meta-row"><span class="fd-meta-label">Max Capacity</span><span>${w.capacity} Players</span></div>
        </div>
        ${instancesHtml}
        ${createHtml}
        <div style="margin-top:14px;text-align:right;"><button class="vrcn-button-round" onclick="closeWorldSearchDetail()">Close</button></div>
        </div>`;

    document.querySelectorAll('#ciType, #ciRegion').forEach(initVnSelect);

    // Live timer – only when currently in this world
    if (_wdLiveTimer) { clearInterval(_wdLiveTimer); _wdLiveTimer = null; }
    if (currentInstanceData?.worldId === wid) {
        let liveSecs = w.worldTimeSeconds || 0;
        _wdLiveTimer = setInterval(() => {
            liveSecs++;
            const el = document.getElementById('wdTimeSpent');
            if (el) el.textContent = formatDuration(liveSecs);
            else { clearInterval(_wdLiveTimer); _wdLiveTimer = null; }
        }, 1000);
    }
}

function closeWorldSearchDetail() {
    if (_wdLiveTimer) { clearInterval(_wdLiveTimer); _wdLiveTimer = null; }
    document.getElementById('modalDetail').style.display = 'none';
}

function toggleWorldFavPicker(worldId) {
    const entry = favWorldsData.find(fw => fw.id === worldId);
    if (entry) {
        removeWorldFavorite(worldId, entry.favoriteId);
        return;
    }
    const picker = document.getElementById('wdFavPicker');
    if (!picker) return;
    const open = picker.style.display !== 'none';
    picker.style.display = open ? 'none' : '';
    if (!open) renderWorldFavPicker(worldId);
}

function removeWorldFavorite(worldId, fvrtId) {
    const btn = document.getElementById('wdFavBtn');
    if (btn) btn.disabled = true;
    sendToCS({ action: 'vrcRemoveWorldFavorite', worldId, fvrtId });
}

function onWorldUnfavoriteResult(data) {
    const btn = document.getElementById('wdFavBtn');
    if (data.ok) {
        favWorldsData = favWorldsData.filter(fw => fw.id !== data.worldId);
        if (btn) {
            btn.disabled = false;
            btn.classList.remove('active');
            btn.innerHTML = '<span class="msi" style="font-size:16px;">star_outline</span>Favorite';
        }
        filterFavWorlds();
        _scheduleBgFavRefresh();
    } else {
        if (btn) btn.disabled = false;
    }
}

function renderWorldFavPicker(worldId) {
    const list = document.getElementById('wdFavGroupList');
    if (!list) return;
    // If groups not loaded yet, request them
    if (favWorldGroups.length === 0) {
        list.innerHTML = '<div style="font-size:11px;color:var(--tx3);padding:8px 0;">Loading groups…</div>';
        sendToCS({ action: 'vrcGetWorldFavGroups' });
        // Store pending worldId so we can render when groups arrive
        list.dataset.pendingWorldId = worldId;
        return;
    }
    const currentEntry = favWorldsData.find(fw => fw.id === worldId);
    const currentGroup = currentEntry?.favoriteGroup || '';
    list.innerHTML = favWorldGroups.map(g => {
        const count = favWorldsData.filter(fw => fw.favoriteGroup === g.name).length;
        const isVrcPlus = g.type === 'vrcPlusWorld';
        const isCurrent = g.name === currentGroup;
        const vrcBadge = isVrcPlus
            ? `<span style="font-size:8px;font-weight:700;color:#FFD700;background:#FFD70022;border:1px solid #FFD70055;border-radius:3px;padding:1px 5px;box-shadow:0 0 5px #FFD70066;letter-spacing:.3px;flex-shrink:0;">VRC+</span>`
            : '';
        const check = isCurrent
            ? `<span class="msi" style="color:var(--accent);font-size:18px;flex-shrink:0;">check_circle</span>`
            : '';
        const gn = jsq(g.name), gt = jsq(g.type), wid = jsq(worldId);
        const oldFvrt = isCurrent ? jsq(currentEntry?.favoriteId || '') : '';
        return `<div class="fd-group-card ci-group-card${isCurrent ? ' ci-group-selected' : ''}"
            onclick="addWorldToFavGroup('${wid}','${gn}','${gt}','${oldFvrt}',this)" style="cursor:pointer;">
            <div style="flex:1;min-width:0;">
                <div style="display:flex;align-items:center;gap:5px;flex-wrap:wrap;">
                    <span style="font-size:12px;font-weight:600;color:var(--tx1);">${esc(g.displayName || g.name)}</span>
                    ${vrcBadge}
                </div>
                <div style="font-size:10px;color:var(--tx3);margin-top:1px;">${count}/100 worlds</div>
            </div>
            ${check}
        </div>`;
    }).join('');
}

function addWorldToFavGroup(worldId, groupName, groupType, oldFvrtId, rowEl) {
    // Optimistic UI: mark as selected
    document.querySelectorAll('#wdFavGroupList .ci-group-card').forEach(c => {
        c.classList.remove('ci-group-selected');
        const chk = c.querySelector('.msi');
        if (chk && chk.textContent === 'check_circle') chk.remove();
    });
    rowEl.classList.add('ci-group-selected');
    rowEl.insertAdjacentHTML('beforeend', '<span class="msi" style="color:var(--accent);font-size:18px;flex-shrink:0;">check_circle</span>');
    sendToCS({ action: 'vrcAddWorldFavorite', worldId, groupName, groupType, oldFvrtId });
}

function onWorldFavoriteResult(data) {
    if (data.ok) {
        const cached = worldInfoCache[data.worldId] || {};
        const worldName  = cached.name || favWorldsData.find(w => w.id === data.worldId)?.name || '';
        const group      = (typeof favWorldGroups !== 'undefined') && favWorldGroups.find(g => g.name === data.groupName);
        const groupLabel = group?.displayName || data.groupName;
        showToast(true, worldName ? `"${worldName}" saved to ${groupLabel}` : `Saved to ${groupLabel}`);

        const existing = favWorldsData.find(w => w.id === data.worldId);
        if (existing) {
            existing.favoriteGroup = data.groupName;
            existing.favoriteId   = data.newFvrtId;
        } else {
            favWorldsData.push({
                id: data.worldId,
                favoriteGroup:     data.groupName,
                favoriteId:        data.newFvrtId,
                name:              cached.name              || '',
                thumbnailImageUrl: cached.thumbnailImageUrl || cached.imageUrl || '',
                imageUrl:          cached.imageUrl          || '',
                authorName:        cached.authorName        || '',
                authorId:          cached.authorId          || '',
                occupants:         cached.occupants         || 0,
                favorites:         cached.favorites         || 0,
                visits:            cached.visits            || 0,
                capacity:          cached.capacity          || 0,
                tags:              cached.tags              || [],
                worldTimeSeconds:  cached.worldTimeSeconds  || 0,
                worldVisitCount:   cached.worldVisitCount   || 0,
            });
        }
        const btn = document.getElementById('wdFavBtn');
        if (btn) {
            btn.classList.add('active');
            btn.innerHTML = '<span class="msi" style="font-size:16px;">star</span>Unfavorite';
        }
        const list = document.getElementById('wdFavGroupList');
        if (list) renderWorldFavPicker(data.worldId);
        filterFavWorlds();
        _scheduleBgFavRefresh();
    } else {
        const list = document.getElementById('wdFavGroupList');
        if (list) {
            list.innerHTML = '<div style="font-size:11px;color:var(--err,#e55);padding:6px 0;">Failed to add to favorites. Try again.</div>';
            setTimeout(() => { if (document.getElementById('wdFavGroupList')) renderWorldFavPicker(data.worldId); }, 1800);
        }
    }
}

function onWorldFavGroupsLoaded(groups) {
    favWorldGroups = groups;
    // Check if picker is open and waiting
    const list = document.getElementById('wdFavGroupList');
    if (list && list.dataset.pendingWorldId) {
        const wid = list.dataset.pendingWorldId;
        delete list.dataset.pendingWorldId;
        renderWorldFavPicker(wid);
    }
}

function onCiTypeChange() {
    const type = document.getElementById('ciType')?.value || '';
    const groupRow = document.getElementById('ciGroupRow');
    if (!groupRow) return;
    const isGroup = type.startsWith('group_');
    groupRow.style.display = isGroup ? '' : 'none';
    const hidden = document.getElementById('ciGroupId');
    if (hidden) hidden.value = '';
    if (isGroup) {
        if (!myGroupsLoaded) {
            renderCiGroupPicker(null); // show loading state
            loadMyGroups();            // triggers vrcMyGroups → renderCiGroupPicker via messages.js
        } else {
            renderCiGroupPicker(myGroups);
        }
    }
}

function renderCiGroupPicker(groups) {
    const el = document.getElementById('ciGroupList');
    if (!el) return;
    if (groups === null) { el.innerHTML = '<div style="font-size:11px;color:var(--tx3);padding:6px 0;">Loading groups...</div>'; return; }
    const validGroups = groups.filter(g => g.canCreateInstance !== false);
    if (!validGroups.length) {
        el.innerHTML = '<div style="font-size:11px;color:var(--tx3);padding:6px 0;">No groups with instance creation rights</div>';
        return;
    }
    el.innerHTML = validGroups.map(g => {
        const icon = g.iconUrl
            ? `<img class="fd-group-icon" src="${esc(g.iconUrl)}" onerror="this.style.display='none'">`
            : `<div class="fd-group-icon fd-group-icon-empty"><span class="msi" style="font-size:18px;">group</span></div>`;
        return `<div class="fd-group-card ci-group-card" data-gid="${esc(g.id)}" onclick="ciSelectGroup('${esc(g.id)}',this)">
            ${icon}
            <div class="fd-group-card-info">
                <div class="fd-group-card-name">${esc(g.name)}</div>
                <div class="fd-group-card-meta">${esc(g.shortCode || '')} · ${g.memberCount || 0} members</div>
            </div>
        </div>`;
    }).join('');
}

function ciSelectGroup(groupId, el) {
    document.getElementById('ciGroupId').value = groupId;
    document.querySelectorAll('.ci-group-card').forEach(c => c.classList.remove('ci-group-selected'));
    el.classList.add('ci-group-selected');
}

function createInstance(worldId) {
    createWorldInstance(worldId);
}

function createWorldInstance(worldId) {
    const type = document.getElementById('ciType').value;
    const region = document.getElementById('ciRegion').value;
    const btn = document.getElementById('ciBtn');
    if (btn) { btn.disabled = true; btn.innerHTML = '<span class="msi" style="font-size:14px;">hourglass_empty</span> Creating...'; }

    if (type.startsWith('group_')) {
        const groupId = document.getElementById('ciGroupId')?.value || '';
        if (!groupId) {
            if (btn) { btn.disabled = false; btn.innerHTML = '<span class="msi" style="font-size:14px;">add</span> Create & Join'; }
            return;
        }
        // group_public → "public", group_members → "members", group_plus → "plus"
        const accessType = type === 'group_public' ? 'public' : type === 'group_plus' ? 'plus' : 'members';
        sendToCS({ action: 'vrcCreateGroupInstance', worldId, groupId, groupAccessType: accessType, region });
    } else {
        sendToCS({ action: 'vrcCreateInstance', worldId, type, region });
    }
}

/* === World Detail Modal === */
function openWorldDetail(worldId) {
    if (!worldId) return;
    const m = document.getElementById('modalWorldDetail');
    const c = document.getElementById('worldDetailContent');

    // Enrich friends whose location is hidden but are known to be in our current instance
    const _instLoc = currentInstanceData?.worldId === worldId ? currentInstanceData.location : null;
    const _instUserIds = _instLoc ? new Set((currentInstanceData.users || []).map(u => u.id).filter(Boolean)) : new Set();
    const friendsRaw = vrcFriendsData.map(f =>
        (_instUserIds.has(f.id) && (!f.location || f.location === 'private')) ? { ...f, location: _instLoc } : f
    );

    // Find all friends in this world
    const friends = friendsRaw.filter(f => {
        const { worldId: wid } = parseFriendLocation(f.location);
        return wid === worldId;
    });

    const cached = dashWorldCache[worldId];
    const worldName = cached?.name || worldId;
    const thumb = cached?.thumbnailImageUrl || cached?.imageUrl || '';

    // Group friends by instance (full location string)
    const instanceMap = {};
    friends.forEach(f => {
        const loc = f.location;
        if (!instanceMap[loc]) {
            const { instanceType: iType } = parseFriendLocation(loc);
            const numMatch = loc.match(/:(\d+)/);
            instanceMap[loc] = { location: loc, instanceType: iType, instanceNum: numMatch ? numMatch[1] : '', friends: [] };
        }
        instanceMap[loc].friends.push(f);
    });
    const instanceList = Object.values(instanceMap);
    const multiInstance = instanceList.length > 1;

    // Resolve myInst early — needed for instance separation below
    const myInst = (typeof _myInstancesData !== 'undefined')
        ? _myInstancesData.find(i => i.worldId === worldId)
        : null;

    // Build header with banner fade (matching profiles/groups)
    const bannerHtml = thumb
        ? `<div class="fd-banner"><img src="${thumb}" onerror="this.parentElement.style.display='none'"><div class="fd-banner-fade"></div></div>`
        : '';

    // Separate friends in MY instance from friends in other instances
    const myInstNum = myInst ? (myInst.location?.match(/:(\d+)/)?.[1] || '') : '';
    let myInstFriends = [];
    const otherInstMap = {};
    instanceList.forEach(inst => {
        if (myInstNum && inst.instanceNum === myInstNum) {
            myInstFriends = inst.friends;
        } else {
            otherInstMap[inst.location] = inst;
        }
    });
    const otherInstList = Object.values(otherInstMap);
    const totalSections = (myInst ? 1 : 0) + otherInstList.length;

    let friendsHtml = `<div class="wd-friends-label">${totalSections > 1 ? `FRIENDS IN THIS WORLD (${totalSections} instances)` : 'FRIENDS IN THIS INSTANCE'}</div>`;

    // Render MY instance first (always, if it exists)
    if (myInst) {
        const { cls: iCls, label: iLabel } = getInstanceBadge(myInst.instanceType);
        const mnum = myInstNum;
        const mCopyBadge = mnum
            ? `<span class="vrcn-id-clip" style="font-size:10px;" onclick="copyInstanceLink('${jsq(myInst.location)}')"><span class="msi" style="font-size:10px;">content_copy</span>#${esc(mnum)}</span>`
            : '';
        if (totalSections > 1) {
            friendsHtml += `<div class="wd-instance-header">
                <span class="vrcn-badge ${iCls}">${iLabel}</span>
                ${mCopyBadge}
            </div>`;
        }
        friendsHtml += '<div class="wd-friends-list">';
        if (myInstFriends.length > 0) {
            myInstFriends.forEach(f => {
                friendsHtml += renderProfileItem(f, `closeWorldDetail();openFriendDetail('${jsq(f.id || '')}')`);
            });
        } else {
            friendsHtml += `<div class="vrcn-profile-item" style="pointer-events:none;opacity:0.55;">
                <div class="fd-profile-item-avatar" style="display:flex;align-items:center;justify-content:center;"><span class="msi" style="font-size:20px;color:var(--tx3);">person</span></div>
                <div class="fd-profile-item-info">
                    <div class="fd-profile-item-name">No friends here yet!</div>
                    <div class="fd-profile-item-status">Invite friends to this instance!</div>
                </div>
            </div>`;
        }
        friendsHtml += '</div>';
    }

    // Render other instances
    otherInstList.forEach(inst => {
        let iResolvedType = inst.instanceType;
        const { cls: iCls, label: iLabel } = getInstanceBadge(iResolvedType);
        const canJoinInst = iResolvedType !== 'private' && iResolvedType !== 'invite_plus';
        const instLoc = inst.location.replace(/'/g, "\\'");
        const instCopyBadge = inst.instanceNum
            ? `<span class="vrcn-id-clip" style="font-size:10px;" onclick="copyInstanceLink('${jsq(inst.location)}')"><span class="msi" style="font-size:10px;">content_copy</span>#${esc(inst.instanceNum)}</span>`
            : '';
        if (totalSections > 1) {
            friendsHtml += `<div class="wd-instance-header">
                <span class="vrcn-badge ${iCls}">${iLabel}</span>
                ${instCopyBadge}
                ${canJoinInst ? `<button class="vrcn-button-round vrcn-btn-join" style="margin-left:auto;" onclick="worldJoinAction('${instLoc}')">Join</button>` : ''}
            </div>`;
        }
        friendsHtml += '<div class="wd-friends-list">';
        inst.friends.forEach(f => {
            friendsHtml += renderProfileItem(f, `closeWorldDetail();openFriendDetail('${jsq(f.id || '')}')`);
        });
        friendsHtml += '</div>';
    });

    // Actions — single instance: show Join World button; multi-instance: join buttons are per-instance above
    const anyLoc = otherInstList.length > 0 ? otherInstList[0].location : '';
    const anyInstType = otherInstList.length > 0 ? otherInstList[0].instanceType : 'public';
    const wid = worldId.replace(/'/g, "\\'");
    // Use myInst.instanceType (API-verified) when available — parseFriendLocation cannot detect Invite+
    const displayInstType = myInst?.instanceType || anyInstType;
    const { cls: instClass, label: instLabel } = getInstanceBadge(displayInstType);
    const loc = anyLoc.replace(/'/g, "\\'");
    const canJoin = !myInst && !multiInstance && anyLoc && anyInstType !== 'private' && anyInstType !== 'invite_plus';

    // Single-instance copy badge — shown in fd-badges-row
    const instanceLoc = myInst?.location || anyLoc;
    const singleInstNum = instanceLoc.match(/:(\d+)/)?.[1] || '';
    const singleInstCopy = singleInstNum
        ? `<span class="vrcn-id-clip" onclick="copyInstanceLink('${jsq(instanceLoc)}')"><span class="msi" style="font-size:12px;">content_copy</span>#${esc(singleInstNum)}</span>`
        : '';

    let actionsHtml = '<div class="fd-actions">';
    if (canJoin) actionsHtml += `<button class="vrcn-button-round vrcn-btn-join" onclick="worldJoinAction('${loc}')">Join World</button>`;
    actionsHtml += `<button class="vrcn-button-round" onclick="closeWorldDetail();openWorldSearchDetail('${wid}')">Open World</button>`;
    actionsHtml += `<button class="vrcn-button-round" style="margin-left:auto;" onclick="closeWorldDetail()">Close</button>`;
    actionsHtml += '</div>';

    c.innerHTML = `${bannerHtml}<div class="fd-content${thumb ? ' fd-has-banner' : ''}" style="padding:16px;">
        <h2 style="margin:0 0 4px;color:var(--tx0);font-size:18px;">${esc(worldName)}</h2>
        <div class="fd-badges-row">${(myInst || multiInstance) ? '' : `<span class="vrcn-badge ${instClass}">${instLabel}</span>${singleInstCopy}`}</div>
        ${friendsHtml}${actionsHtml}</div>`;
    m.style.display = 'flex';
}

function closeWorldDetail() {
    document.getElementById('modalWorldDetail').style.display = 'none';
}

function worldJoinAction(location) {
    const btns = document.querySelectorAll('#worldDetailContent button');
    btns.forEach(b => b.disabled = true);
    sendToCS({ action: 'vrcJoinFriend', location: location });
}
