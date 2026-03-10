/* === Avatars Tab === */
let _avFavRefreshTimer = null;
function _scheduleAvFavRefresh() {
    clearTimeout(_avFavRefreshTimer);
    _avFavRefreshTimer = setTimeout(() => sendToCS({ action: 'vrcGetAvatars', filter: 'favorites' }), 2000);
}

function refreshAvatars() {
    if (avatarFilter === 'search') {
        if (avatarSearchQuery) doAvatarSearch();
        return;
    }
    if (avatarFilter === 'favorites') {
        refreshFavAvatars();
        return;
    }
    if (!currentVrcUser) {
        document.getElementById('avatarGrid').innerHTML = '<div class="empty-msg">Login to VRChat to see your avatars</div>';
        return;
    }
    document.getElementById('avatarGrid').innerHTML = sk('avatar', 6);
    sendToCS({ action: 'vrcGetAvatars', filter: 'own' });
}

function refreshFavAvatars() {
    const btn = document.getElementById('favAvatarsRefreshBtn');
    if (btn) { btn.disabled = true; btn.querySelector('.msi').textContent = 'hourglass_empty'; }
    sendToCS({ action: 'vrcGetAvatars', filter: 'favorites' });
}

function setAvatarFilter(filter) {
    avatarFilter = filter;
    document.querySelectorAll('.sub-tab-btn').forEach(b => b.classList.remove('active'));
    const btnMap = { own: 'avatarFilterOwn', favorites: 'avatarFilterFav', rose: 'avatarFilterRose', search: 'avatarFilterSearch' };
    const btn = document.getElementById(btnMap[filter]);
    if (btn) btn.classList.add('active');

    const ownArea    = document.getElementById('avatarOwnArea');
    const favArea    = document.getElementById('avatarFavArea');
    const roseArea   = document.getElementById('avatarRoseArea');
    const searchArea = document.getElementById('avatarSearchArea');

    if (ownArea)    ownArea.style.display    = filter === 'own'       ? '' : 'none';
    if (favArea)    favArea.style.display    = filter === 'favorites' ? '' : 'none';
    if (roseArea)   roseArea.style.display   = filter === 'rose'      ? '' : 'none';
    if (searchArea) searchArea.style.display = filter === 'search'    ? '' : 'none';

    document.getElementById('avatarCount').textContent = '';

    if (filter === 'own') {
        const inp = document.getElementById('ownAvatarSearchInput');
        if (inp) inp.value = '';
        refreshAvatars();
    } else if (filter === 'favorites') {
        if (favAvatarsData.length === 0) sendToCS({ action: 'vrcGetAvatars', filter: 'favorites' });
        else { updateFavAvatarGroupHeader(); filterFavAvatars(); }
    } else if (filter === 'rose') {
        loadRoseDatabase();
    } else {
        document.getElementById('avatarSearchGrid').innerHTML = '<div class="empty-msg">Search for public avatars</div>';
        setTimeout(() => document.getElementById('avatarSearchInput')?.focus(), 50);
    }
}

/* === Own Avatars === */
function filterOwnAvatars() {
    const q = (document.getElementById('ownAvatarSearchInput')?.value || '').toLowerCase();
    const el = document.getElementById('avatarGrid');
    if (!el) return;
    if (!currentVrcUser) {
        el.innerHTML = '<div class="empty-msg">Login to VRChat to see your avatars</div>';
        return;
    }
    const filtered = q
        ? avatarsData.filter(a => (a.name || '').toLowerCase().includes(q) || (a.authorName || '').toLowerCase().includes(q))
        : avatarsData;
    document.getElementById('avatarCount').textContent = filtered.length ? `${filtered.length} avatar${filtered.length !== 1 ? 's' : ''}` : '';
    el.innerHTML = filtered.length
        ? filtered.map(a => renderAvatarCard(a, 'own')).join('')
        : `<div class="empty-msg">${q ? 'No avatars match your filter' : 'No avatars found'}</div>`;
}

function renderAvatarGrid() {
    const el = document.getElementById('avatarGrid');
    if (!el) return;
    if (!currentVrcUser) {
        el.innerHTML = '<div class="empty-msg">Login to VRChat to see your avatars</div>';
        return;
    }
    // Reset search then apply filter
    const inp = document.getElementById('ownAvatarSearchInput');
    if (inp) inp.value = '';
    filterOwnAvatars();
}

function renderSearchGrid() {
    const el = document.getElementById('avatarSearchGrid');
    if (!el) return;
    if (avatarSearchResults.length === 0) {
        el.innerHTML = '<div class="empty-msg">No results found</div>';
        return;
    }
    document.getElementById('avatarCount').textContent = `${avatarSearchResults.length} result${avatarSearchResults.length !== 1 ? 's' : ''}`;
    let html = avatarSearchResults.map(a => renderAvatarCard(a, 'search')).join('');
    if (avatarSearchHasMore) {
        html += `<div style="grid-column:1/-1;text-align:center;margin-top:6px;">
            <button class="vrcn-button" onclick="doAvatarSearch(true)">Load More</button>
        </div>`;
    }
    el.innerHTML = html;
}

function _avPlatformBadges(a) {
    let hasPC, hasQuest;
    if (a.compatibility && a.compatibility.length > 0) {
        // avtrdb.com search results: compatibility = ["pc", "android", "ios"]
        hasPC    = a.compatibility.includes('pc');
        hasQuest = a.compatibility.includes('android');
    } else {
        // Own / favorite avatars: use unityPackages, exclude auto-generated impostors
        const real = (a.unityPackages || []).filter(p => p.variant !== 'impostor');
        hasPC    = real.some(p => p.platform === 'standalonewindows');
        hasQuest = real.some(p => p.platform === 'android');
    }
    if (!hasPC && !hasQuest) return '';
    return `<div style="display:flex;gap:3px;">${hasPC ? '<span class="vrcn-badge platform-pc">PC</span>' : ''}${hasQuest ? '<span class="vrcn-badge platform-quest">Quest</span>' : ''}</div>`;
}

function renderAvatarCard(a, context) {
    const thumb = a.thumbnailImageUrl || a.imageUrl || '';
    const isActive = a.id === currentAvatarId;
    const isPublic = context === 'search' || a.releaseStatus === 'public';
    const isFav = favAvatarsData.some(f => f.id === a.id);
    const statusBadge = isPublic
        ? '<span class="vrcn-badge accent"><span class="msi" style="font-size:10px;">public</span> Public</span>'
        : '<span class="vrcn-badge private"><span class="msi" style="font-size:10px;">lock</span> Private</span>';
    const activeBadge = isActive ? '<span class="vrcn-badge current">Current</span>' : '';
    const aid = jsq(a.id || '');
    const thumbStyle = thumb ? `background-image:url('${cssUrl(thumb)}')` : '';
    return `<div class="av-card ${isActive ? 'av-active' : ''}" onclick="selectAvatar('${aid}')">
        <div class="av-thumb" style="${thumbStyle}">
            <div class="av-thumb-overlay"></div>
            <div class="av-badges-top">${activeBadge}</div>
            <div class="av-badges-bottom">${statusBadge}${_avPlatformBadges(a)}</div>
        </div>
        <div class="av-info" style="display:flex;align-items:center;gap:6px;">
            <div style="flex:1;min-width:0;">
                <div class="av-name">${esc(a.name || 'Unnamed')}</div>
                <div class="av-author">${esc(a.authorName || '')}</div>
            </div>
            <button class="vrcn-button-round${isFav ? ' active' : ''}" onclick="event.stopPropagation();openAvFavPicker('${aid}',this)" style="flex-shrink:0;margin-left:auto;">
                <span class="msi" style="font-size:14px;">${isFav ? 'star' : 'star_outline'}</span>${isFav ? 'Unfavorite' : 'Favorite'}
            </button>
        </div>
    </div>`;
}

function selectAvatar(avatarId) {
    if (!avatarId || avatarId === currentAvatarId) return;
    document.querySelectorAll('.av-card').forEach(c => {
        c.style.pointerEvents = 'none';
        c.style.opacity = '0.6';
    });
    sendToCS({ action: 'vrcSelectAvatar', avatarId: avatarId });
}

/* === Search === */
function doAvatarSearch(loadMore) {
    const q = document.getElementById('avatarSearchInput').value.trim();
    if (!q) return;
    if (!loadMore) {
        avatarSearchPage = 0;
        avatarSearchResults = [];
        avatarSearchQuery = q;
        document.getElementById('avatarSearchGrid').innerHTML = sk('avatar', 6);
    } else {
        avatarSearchPage++;
    }
    sendToCS({ action: 'vrcSearchAvatars', query: avatarSearchQuery, page: avatarSearchPage });
}

/* === Favorites: group dropdown + header === */
function _avGroupOptionLabel(g) {
    const count    = favAvatarsData.filter(a => a.favoriteGroup === g.name).length;
    const cap      = g.capacity || 25;
    const isVrcPlus = g.name !== 'avatars1';
    return `${esc(g.displayName || g.name)} ${count}/${cap}${isVrcPlus ? ' [VRC+]' : ''}`;
}

function renderFavAvatars(payload) {
    const refreshBtn = document.getElementById('favAvatarsRefreshBtn');
    if (refreshBtn) { refreshBtn.disabled = false; const ico = refreshBtn.querySelector('.msi'); if (ico) ico.textContent = 'refresh'; }

    const avatars = payload?.avatars || [];
    const groups  = payload?.groups  || [];
    favAvatarsData  = avatars;
    favAvatarGroups = groups;

    const sel = document.getElementById('favAvatarGroupFilter');
    if (sel) {
        const prev = favAvatarGroupFilter;
        sel.innerHTML = '<option value="">All Favorites</option>' +
            groups.map(g => `<option value="${esc(g.name)}">${_avGroupOptionLabel(g)}</option>`).join('');
        const stillValid = groups.some(g => g.name === prev);
        favAvatarGroupFilter = stillValid ? prev : '';
        sel.value = favAvatarGroupFilter;
        if (sel._vnRefresh) sel._vnRefresh();
    }
    updateFavAvatarGroupHeader();
    filterFavAvatars();
}

function setFavAvatarGroup(val) {
    favAvatarGroupFilter = val;
    cancelEditAvatarGroupName();
    updateFavAvatarGroupHeader();
    filterFavAvatars();
}

function updateFavAvatarGroupHeader() {
    const label   = document.getElementById('favAvatarGroupLabel');
    const editBtn = document.getElementById('favAvatarGroupEditBtn');
    const badge   = document.getElementById('favAvatarGroupVrcPlusBadge');
    if (!label) return;
    if (!favAvatarGroupFilter) {
        label.textContent = 'All Favorites';
        if (editBtn) editBtn.style.display = 'none';
        if (badge) badge.style.display = 'none';
    } else {
        const g = favAvatarGroups.find(x => x.name === favAvatarGroupFilter);
        label.textContent = g ? (g.displayName || g.name) : favAvatarGroupFilter;
        if (editBtn) editBtn.style.display = '';
        if (badge) badge.style.display = (g && g.name !== 'avatars1') ? '' : 'none';
    }
}

function startEditAvatarGroupName() {
    const g = favAvatarGroups.find(x => x.name === favAvatarGroupFilter);
    if (!g) return;
    const input = document.getElementById('favAvatarGroupNameInput');
    if (input) input.value = g.displayName || g.name;
    document.getElementById('favAvatarGroupHeader').style.display = 'none';
    const row = document.getElementById('favAvatarGroupRenameRow');
    if (row) row.style.display = 'flex';
    if (input) input.focus();
}

function cancelEditAvatarGroupName() {
    document.getElementById('favAvatarGroupHeader').style.display = 'flex';
    const row = document.getElementById('favAvatarGroupRenameRow');
    if (row) row.style.display = 'none';
    const saveBtn = document.querySelector('#favAvatarGroupRenameRow .vrcn-btn-primary');
    if (saveBtn) { saveBtn.disabled = false; saveBtn.textContent = 'Save'; }
}

function saveAvatarGroupName() {
    const g = favAvatarGroups.find(x => x.name === favAvatarGroupFilter);
    if (!g) return;
    const input = document.getElementById('favAvatarGroupNameInput');
    const newName = (input?.value || '').trim();
    if (!newName) return;
    const saveBtn = document.querySelector('#favAvatarGroupRenameRow .vrcn-btn-primary');
    if (saveBtn) { saveBtn.disabled = true; saveBtn.textContent = 'Saving...'; }
    sendToCS({ action: 'vrcUpdateFavoriteGroup', groupType: g.type, groupName: g.name, displayName: newName });
}

function filterFavAvatars() {
    const q = (document.getElementById('favAvatarSearchInput')?.value || '').toLowerCase();
    let filtered = favAvatarsData;
    if (favAvatarGroupFilter) filtered = filtered.filter(a => a.favoriteGroup === favAvatarGroupFilter);
    if (q) filtered = filtered.filter(a => (a.name || '').toLowerCase().includes(q) || (a.authorName || '').toLowerCase().includes(q));
    const el = document.getElementById('favAvatarsGrid');
    if (!el) return;
    if (!filtered.length) {
        el.innerHTML = `<div class="empty-msg">${q || favAvatarGroupFilter ? 'No favorites match your filter' : 'No favorite avatars found'}</div>`;
        return;
    }
    el.innerHTML = filtered.map(a => {
        const thumb = a.thumbnailImageUrl || a.imageUrl || '';
        const thumbStyle = thumb ? `background-image:url('${cssUrl(thumb)}')` : '';
        const isActive = a.id === currentAvatarId;
        const aid = jsq(a.id || '');
        const fid = jsq(a.favoriteId || '');
        const activeBadge = isActive ? '<span class="vrcn-badge current">Current</span>' : '';
        const isPublic = a.releaseStatus === 'public';
        const statusBadge = isPublic
            ? '<span class="vrcn-badge accent"><span class="msi" style="font-size:10px;">public</span> Public</span>'
            : '<span class="vrcn-badge private"><span class="msi" style="font-size:10px;">lock</span> Private</span>';
        return `<div class="av-card ${isActive ? 'av-active' : ''}" onclick="selectAvatar('${aid}')">
            <div class="av-thumb" style="${thumbStyle}">
                <div class="av-thumb-overlay"></div>
                <div class="av-badges-top">${activeBadge}</div>
                <div class="av-badges-bottom">${statusBadge}${_avPlatformBadges(a)}</div>
            </div>
            <div class="av-info" style="display:flex;align-items:center;gap:6px;">
                <div style="flex:1;min-width:0;">
                    <div class="av-name">${esc(a.name || 'Unnamed')}</div>
                    <div class="av-author">${esc(a.authorName || '')}</div>
                </div>
                <button class="vrcn-button-round active" onclick="event.stopPropagation();removeAvatarFavorite('${aid}','${fid}')" style="flex-shrink:0;margin-left:auto;">
                    <span class="msi" style="font-size:14px;">star</span>Unfavorite
                </button>
            </div>
        </div>`;
    }).join('');
}

/* === Favorite Picker Popup === */
let _avFavPickerAvatarId = null;

function openAvFavPicker(avatarId, btnEl) {
    const entry = favAvatarsData.find(f => f.id === avatarId);
    if (entry) {
        removeAvatarFavorite(avatarId, entry.favoriteId);
        return;
    }

    _avFavPickerAvatarId = avatarId;
    const panel = document.getElementById('avFavPickerPanel');
    if (!panel) return;

    renderAvFavPickerList(avatarId);
    panel.style.display = '';

    // Position near button
    const rect = btnEl.getBoundingClientRect();
    let top = rect.bottom + 4;
    let left = rect.left;
    if (left + 280 > window.innerWidth) left = window.innerWidth - 290;
    if (top + 300 > window.innerHeight) top = rect.top - 300;
    panel.style.top  = top  + 'px';
    panel.style.left = left + 'px';

    // If groups not yet loaded, request them
    if (favAvatarGroups.length === 0) {
        document.getElementById('avFavPickerList').innerHTML = '<div style="font-size:11px;color:var(--tx3);padding:8px 0;">Loading groups…</div>';
        sendToCS({ action: 'vrcGetAvatarFavGroups' });
    }

    setTimeout(() => document.addEventListener('click', _avPickerOutside, { once: true }), 0);
}

function _avPickerOutside(e) {
    const panel = document.getElementById('avFavPickerPanel');
    if (panel && panel.contains(e.target)) {
        document.addEventListener('click', _avPickerOutside, { once: true });
    } else {
        closeAvFavPicker();
    }
}

function closeAvFavPicker() {
    const panel = document.getElementById('avFavPickerPanel');
    if (panel) panel.style.display = 'none';
    _avFavPickerAvatarId = null;
}

function renderAvFavPickerList(avatarId) {
    const list = document.getElementById('avFavPickerList');
    if (!list) return;
    if (favAvatarGroups.length === 0) return;

    const currentEntry = favAvatarsData.find(f => f.id === avatarId);
    const currentGroup = currentEntry?.favoriteGroup || '';

    list.innerHTML = favAvatarGroups.map(g => {
        const count = favAvatarsData.filter(f => f.favoriteGroup === g.name).length;
        const isVrcPlus = g.name !== 'avatars1';
        const isCurrent = g.name === currentGroup;
        const vrcBadge = isVrcPlus
            ? `<span style="font-size:8px;font-weight:700;color:#FFD700;background:#FFD70022;border:1px solid #FFD70055;border-radius:3px;padding:1px 5px;box-shadow:0 0 5px #FFD70066;letter-spacing:.3px;flex-shrink:0;">VRC+</span>`
            : '';
        const check = isCurrent
            ? `<span class="msi" style="color:var(--accent);font-size:18px;flex-shrink:0;">check_circle</span>`
            : '';
        const gn = jsq(g.name), gt = jsq(g.type), aid = jsq(avatarId);
        const oldFvrt = isCurrent ? jsq(currentEntry?.favoriteId || '') : '';
        return `<div class="fd-group-card ci-group-card${isCurrent ? ' ci-group-selected' : ''}"
            onclick="addAvatarToFavGroup('${aid}','${gn}','${gt}','${oldFvrt}',this)" style="cursor:pointer;">
            <div style="flex:1;min-width:0;">
                <div style="display:flex;align-items:center;gap:5px;flex-wrap:wrap;">
                    <span style="font-size:12px;font-weight:600;color:var(--tx1);">${esc(g.displayName || g.name)}</span>
                    ${vrcBadge}
                </div>
                <div style="font-size:10px;color:var(--tx3);margin-top:1px;">${count}/${g.capacity || 25} slots</div>
            </div>
            ${check}
        </div>`;
    }).join('');
}

function addAvatarToFavGroup(avatarId, groupName, groupType, oldFvrtId, rowEl) {
    document.querySelectorAll('#avFavPickerList .ci-group-card').forEach(c => {
        c.classList.remove('ci-group-selected');
        const chk = c.querySelector('.msi');
        if (chk && chk.textContent === 'check_circle') chk.remove();
    });
    rowEl.classList.add('ci-group-selected');
    rowEl.insertAdjacentHTML('beforeend', '<span class="msi" style="color:var(--accent);font-size:18px;flex-shrink:0;">check_circle</span>');
    sendToCS({ action: 'vrcAddAvatarFavorite', avatarId, groupName, groupType, oldFvrtId });
}

function removeAvatarFavorite(avatarId, fvrtId) {
    closeAvFavPicker();
    sendToCS({ action: 'vrcRemoveAvatarFavorite', avatarId, fvrtId });
}

function onAvatarFavoriteResult(data) {
    if (data.ok) {
        const existing = favAvatarsData.find(f => f.id === data.avatarId);
        if (existing) {
            existing.favoriteGroup = data.groupName;
            existing.favoriteId   = data.newFvrtId;
        } else {
            // Find avatar info from own or search data
            const src = avatarsData.find(a => a.id === data.avatarId) || avatarSearchResults.find(a => a.id === data.avatarId) || {};
            favAvatarsData.push({
                id:                data.avatarId,
                favoriteGroup:     data.groupName,
                favoriteId:        data.newFvrtId,
                name:              src.name              || '',
                thumbnailImageUrl: src.thumbnailImageUrl || src.imageUrl || '',
                imageUrl:          src.imageUrl          || '',
                authorName:        src.authorName        || '',
                releaseStatus:     src.releaseStatus     || 'public',
            });
        }
        closeAvFavPicker();
        // Re-render star on current card
        if (avatarFilter === 'own') renderAvatarGrid();
        else if (avatarFilter === 'search') renderSearchGrid();
        else if (avatarFilter === 'favorites') filterFavAvatars();
        else if (avatarFilter === 'rose') filterRoseDb();
        _scheduleAvFavRefresh();
    } else {
        const list = document.getElementById('avFavPickerList');
        if (list) {
            list.innerHTML = `<div style="font-size:11px;color:var(--err,#e55);padding:6px 0;">Failed: ${esc(data.error || 'Try again')}</div>`;
            setTimeout(() => { if (_avFavPickerAvatarId) renderAvFavPickerList(_avFavPickerAvatarId); }, 1800);
        }
    }
}

function onAvatarUnfavoriteResult(data) {
    if (data.ok) {
        favAvatarsData = favAvatarsData.filter(f => f.id !== data.avatarId);
        if (avatarFilter === 'favorites') filterFavAvatars();
        else if (avatarFilter === 'own') renderAvatarGrid();
        else if (avatarFilter === 'search') renderSearchGrid();
        else if (avatarFilter === 'rose') filterRoseDb();
        _scheduleAvFavRefresh();
    }
}

function onAvatarFavGroupsLoaded(groups) {
    favAvatarGroups = groups;
    // Update group dropdown if favorites tab is open
    const sel = document.getElementById('favAvatarGroupFilter');
    if (sel) {
        sel.innerHTML = '<option value="">All Favorites</option>' +
            groups.map(g => `<option value="${esc(g.name)}">${_avGroupOptionLabel(g)}</option>`).join('');
        sel.value = favAvatarGroupFilter;
        if (sel._vnRefresh) sel._vnRefresh();
    }
    // Re-render picker if open
    if (_avFavPickerAvatarId) renderAvFavPickerList(_avFavPickerAvatarId);
}

// Handles rename result (shared with worlds via vrcFavoriteGroupUpdated)
function onAvatarFavoriteGroupUpdated(data) {
    if (!data.ok) { cancelEditAvatarGroupName(); return; }
    const g = favAvatarGroups.find(x => x.name === data.groupName);
    if (g) g.displayName = data.displayName;
    const sel = document.getElementById('favAvatarGroupFilter');
    if (sel) {
        const opt = [...sel.options].find(o => o.value === data.groupName);
        if (opt && g) opt.textContent = _avGroupOptionLabel(g);
        if (sel._vnRefresh) sel._vnRefresh();
    }
    cancelEditAvatarGroupName();
    updateFavAvatarGroupHeader();
}

/* === Rose Database === */
let roseDbData   = [];
let roseDbLoaded = false;

function loadRoseDatabase(forceRefresh) {
    if (roseDbLoaded && !forceRefresh) { filterRoseDb(); return; }
    const grid = document.getElementById('roseDbGrid');
    const btn  = document.getElementById('roseRefreshBtn');
    if (grid) grid.innerHTML = sk('avatar', 6);
    if (btn)  { btn.disabled = true; btn.querySelector('.msi').textContent = 'hourglass_empty'; }

    fetch('https://gist.githubusercontent.com/TheZiver/bb99f9facb8d14fd607dbb79e9a99d83/raw')
        .then(r => r.json())
        .then(data => {
            roseDbData   = data.community_avatars || [];
            roseDbLoaded = true;
            filterRoseDb();
        })
        .catch(() => {
            if (grid) grid.innerHTML = '<div class="empty-msg">Failed to load Rose Database. Check your connection.</div>';
        })
        .finally(() => {
            if (btn) { btn.disabled = false; btn.querySelector('.msi').textContent = 'refresh'; }
        });
}

function filterRoseDb() {
    const q    = (document.getElementById('roseSearchInput')?.value || '').toLowerCase();
    const list = q
        ? roseDbData.filter(a =>
            (a.avatar_name || '').toLowerCase().includes(q) ||
            (a.author      || '').toLowerCase().includes(q) ||
            (a.tags        || []).some(t => t.toLowerCase().includes(q)))
        : roseDbData;
    renderRoseGrid(list);
    document.getElementById('avatarCount').textContent = list.length ? `${list.length} avatar${list.length !== 1 ? 's' : ''}` : '';
}

function renderRoseGrid(list) {
    const grid = document.getElementById('roseDbGrid');
    if (!grid) return;
    if (!list || list.length === 0) {
        grid.innerHTML = '<div class="empty-msg">No avatars found</div>';
        return;
    }
    grid.innerHTML = list.map(a => renderRoseAvatarCard(a)).join('');
}

const ROSE_TAG_ORDER = ['FISH', 'ROSE_FISH', 'ARCADE_FISH', 'VAPOR_FISH', 'CHEESE_FISH', 'COSMIC_FISH'];

const ROSE_TAG_STYLES = {
    FISH:        { label: 'Fish',   bg: 'rgba(255,255,255,0.15)', color: '#ffffff',              border: '1px solid rgba(255,255,255,0.45)' },
    ROSE_FISH:   { label: 'Rose',   bg: 'rgba(220,38,38,0.20)',   color: '#f87171',              border: '1px solid rgba(220,38,38,0.50)'   },
    ARCADE_FISH: { label: 'Arcade', bg: 'linear-gradient(90deg,rgba(236,72,153,0.25),rgba(6,182,212,0.25))', color: '#e879f9', border: '1px solid rgba(167,139,250,0.45)' },
    VAPOR_FISH:  { label: 'Vapor',  bg: 'rgba(6,182,212,0.20)',   color: '#22d3ee',              border: '1px solid rgba(6,182,212,0.50)'   },
    CHEESE_FISH: { label: 'Cheese', bg: 'rgba(234,179,8,0.20)',   color: '#facc15',              border: '1px solid rgba(234,179,8,0.50)'   },
    COSMIC_FISH: { label: 'Cosmic', bg: 'rgba(59,130,246,0.20)',  color: '#60a5fa',              border: '1px solid rgba(59,130,246,0.50)'  },
};

function _roseTagBadge(rawTag) {
    const key = rawTag.toUpperCase().replace(/\s+/g, '_');
    const s   = ROSE_TAG_STYLES[key];
    if (!s) return `<span class="vrcn-badge" style="background:var(--bg2);color:var(--tx2);border:1px solid var(--brd-lt);">${esc(rawTag)}</span>`;
    const bg = s.bg.startsWith('linear') ? s.bg : s.bg;
    return `<span class="vrcn-badge" style="background:${bg};color:${s.color};border:${s.border};">${esc(s.label)}</span>`;
}

function renderRoseAvatarCard(a) {
    const thumb  = a.avatar_image_url || '';
    const aid    = jsq(a.avatar_id || '');
    const isFav  = favAvatarsData.some(f => f.id === a.avatar_id);
    const thumbStyle = thumb ? `background-image:url('${cssUrl(thumb)}')` : '';

    // Sort tags in defined order, unknown tags appended at end
    const rawTags  = (a.tags || []);
    const sorted   = [
        ...ROSE_TAG_ORDER.filter(k => rawTags.some(t => t.toUpperCase().replace(/\s+/g,'_') === k)),
        ...rawTags.filter(t => !ROSE_TAG_ORDER.includes(t.toUpperCase().replace(/\s+/g,'_'))),
    ];
    const tags = sorted.map(t => _roseTagBadge(t)).join('');

    return `<div class="av-card" onclick="selectAvatar('${aid}')">
        <div class="av-thumb" style="${thumbStyle}">
            <div class="av-thumb-overlay"></div>
            <div class="av-badges-bottom"><span class="vrcn-badge accent"><span class="msi" style="font-size:10px;">public</span> Public</span></div>
        </div>
        <div class="av-info" style="display:flex;align-items:center;gap:6px;">
            <div style="flex:1;min-width:0;">
                <div class="av-name">${esc(a.avatar_name || 'Unnamed')}</div>
                <div class="av-author" style="display:flex;align-items:center;gap:6px;flex-wrap:wrap;">
                    <span>${esc(a.author || '')}</span>
                    ${tags ? `<span style="display:inline-flex;gap:3px;flex-wrap:wrap;">${tags}</span>` : ''}
                </div>
            </div>
            <button class="vrcn-button-round${isFav ? ' active' : ''}" onclick="event.stopPropagation();openAvFavPicker('${aid}',this)" style="flex-shrink:0;margin-left:auto;">
                <span class="msi" style="font-size:14px;">${isFav ? 'star' : 'star_outline'}</span>${isFav ? 'Unfavorite' : 'Favorite'}
            </button>
        </div>
    </div>`;
}

/* === Avatar Detail Modal === */
let _avDetailData = null;
let _avEditTags   = [];

function openAvatarDetail(avatarId) {
    const c = document.getElementById('avatarDetailContent');
    if (c) c.innerHTML = sk('detail');
    document.getElementById('modalAvatarDetail').style.display = 'flex';
    sendToCS({ action: 'vrcGetAvatarDetail', avatarId });
}

function closeAvatarDetail() {
    document.getElementById('modalAvatarDetail').style.display = 'none';
    _avDetailData = null;
}

const _avFieldIds = {
    name:       { view: 'avfNameView',       edit: 'avfNameEdit'       },
    desc:       { view: 'avfDescView',       edit: 'avfDescEdit'       },
    visibility: { view: 'avfVisView',        edit: 'avfVisEdit'        },
    tags:       { view: 'avfTagsView',       edit: 'avfTagsEdit'       },
};
let _avSavingField = '';

function renderAvatarDetail(a) {
    _avDetailData = a;
    const c = document.getElementById('avatarDetailContent');
    if (!c) return;

    const thumb  = a.thumbnailImageUrl || a.imageUrl || '';
    const isOwn  = currentVrcUser && a.authorId === currentVrcUser.id;
    const aid    = jsq(a.id || '');

    function platBadge(label, cssClass, icon, perf) {
        const perfHtml = perf ? `<span style="opacity:.8;font-weight:400;"> · ${esc(perf)}</span>` : '';
        return `<span class="vrcn-badge ${cssClass}"><span class="msi" style="font-size:10px;">${icon}</span>${label}${perfHtml}</span>`;
    }

    const isPublic    = a.releaseStatus === 'public';
    const statusBadge = isPublic
        ? '<span class="vrcn-badge accent"><span class="msi" style="font-size:10px;">public</span> Public</span>'
        : '<span class="vrcn-badge private"><span class="msi" style="font-size:10px;">lock</span> Private</span>';
    const pcBadge       = a.hasPC      ? platBadge('PC',    'platform-pc',    'computer', a.pcPerf)    : '';
    const questBadge    = a.hasQuest   ? platBadge('Quest', 'platform-quest', 'android',  a.questPerf) : '';
    const impostorBadge = a.hasImpostor
        ? '<span class="vrcn-badge" style="background:rgba(138,43,226,.18);color:#b47aff;"><span class="msi" style="font-size:10px;">smart_toy</span> Impostor</span>'
        : '';

    function fmtDate(iso) {
        if (!iso) return '–';
        const d = new Date(iso);
        if (isNaN(d)) return iso;
        const pad = n => String(n).padStart(2, '0');
        return `${pad(d.getDate())}.${pad(d.getMonth()+1)}.${d.getFullYear()}, ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
    }

    const authorHtml = a.authorId
        ? `<span onclick="closeAvatarDetail();openFriendDetail('${jsq(a.authorId)}')" style="display:inline-flex;align-items:center;padding:1px 8px;border-radius:20px;background:var(--bg-hover);font-size:11px;font-weight:600;color:var(--tx1);cursor:pointer;line-height:1.8;">${esc(a.authorName || a.authorId)}</span>`
        : esc(a.authorName || '');

    const metaRows = [
        `<div class="fd-meta-row"><span class="fd-meta-label">Created At</span><span>${fmtDate(a.created_at)}</span></div>`,
        `<div class="fd-meta-row"><span class="fd-meta-label">Last Updated</span><span>${fmtDate(a.updated_at)}</span></div>`,
        a.version ? `<div class="fd-meta-row"><span class="fd-meta-label">Version</span><span>v${a.version}</span></div>` : '',
    ].join('');

    // Tags view
    const tagsViewHtml = (a.tags && a.tags.length)
        ? `<div class="fd-lang-tags">${a.tags.map(t => `<span class="vrcn-badge">${esc(t)}</span>`).join('')}</div>`
        : `<div class="myp-empty">No tags</div>`;

    c.innerHTML = `
        ${thumb ? `<div class="fd-banner"><img src="${thumb}" onerror="this.parentElement.style.display='none'"><div class="fd-banner-fade"></div></div>` : ''}
        <div class="fd-content${thumb ? ' fd-has-banner' : ''}">

            <!-- Name -->
            <div id="avfNameView" style="display:flex;align-items:center;gap:6px;padding:20px 20px 0;">
                <h2 style="margin:0;color:var(--tx0);font-size:18px;flex:1;min-width:0;">${esc(a.name || 'Unnamed Avatar')}</h2>
                ${isOwn ? `<button class="myp-edit-btn" onclick="editAvField('name')" title="Edit name"><span class="msi" style="font-size:14px;">edit</span></button>` : ''}
            </div>
            ${isOwn ? `<div id="avfNameEdit" style="display:none;padding:8px 20px 0;">
                <input id="avNameInput" class="vrcn-edit-field" value="${esc(a.name || '')}" maxlength="64" style="width:100%;">
                <div class="myp-edit-actions">
                    <button class="vrcn-button" onclick="cancelAvField('name')">Cancel</button>
                    <button class="vrcn-button vrcn-btn-primary" onclick="saveAvField('name','${aid}')">Save</button>
                </div>
            </div>` : ''}

            <!-- Author + badges -->
            <div style="padding:4px 20px 12px;">
                <div style="font-size:12px;color:var(--tx3);margin-bottom:10px;">by ${authorHtml}</div>
                <div style="display:flex;flex-wrap:wrap;gap:5px;margin-bottom:10px;">
                    ${statusBadge}${pcBadge}${questBadge}${impostorBadge}
                </div>
                ${idBadge(a.id)}
            </div>

            <div style="padding:0 20px 20px;">
                <!-- Description -->
                <div class="myp-section">
                    <div class="myp-section-header">
                        <span class="myp-section-title">Description</span>
                        ${isOwn ? `<button class="myp-edit-btn" onclick="editAvField('desc')"><span class="msi" style="font-size:14px;">edit</span></button>` : ''}
                    </div>
                    <div id="avfDescView">
                        ${a.description ? `<div class="fd-bio">${esc(a.description)}</div>` : '<div class="myp-empty">No description</div>'}
                    </div>
                    ${isOwn ? `<div id="avfDescEdit" style="display:none;">
                        <textarea id="avDescInput" class="myp-textarea" rows="4" maxlength="2000" placeholder="Avatar description...">${esc(a.description||'')}</textarea>
                        <div class="myp-edit-actions">
                            <button class="vrcn-button" onclick="cancelAvField('desc')">Cancel</button>
                            <button class="vrcn-button vrcn-btn-primary" onclick="saveAvField('desc','${aid}')">Save</button>
                        </div>
                    </div>` : ''}
                </div>

                <!-- Visibility -->
                <div class="myp-section">
                    <div class="myp-section-header">
                        <span class="myp-section-title">Visibility</span>
                        ${isOwn ? `<button class="myp-edit-btn" onclick="editAvField('visibility')"><span class="msi" style="font-size:14px;">edit</span></button>` : ''}
                    </div>
                    <div id="avfVisView">${statusBadge}</div>
                    ${isOwn ? `<div id="avfVisEdit" style="display:none;">
                        <div style="display:flex;gap:6px;margin-bottom:8px;">
                            <button id="avVisPublicBtn" class="vrcn-button-round${isPublic ? ' active' : ''}" onclick="avVisToggle('public',this)">
                                <span class="msi" style="font-size:14px;">public</span> Public
                            </button>
                            <button id="avVisPrivateBtn" class="vrcn-button-round${!isPublic ? ' active' : ''}" onclick="avVisToggle('private',this)">
                                <span class="msi" style="font-size:14px;">lock</span> Private
                            </button>
                        </div>
                        <div class="myp-edit-actions">
                            <button class="vrcn-button" onclick="cancelAvField('visibility')">Cancel</button>
                            <button class="vrcn-button vrcn-btn-primary" onclick="saveAvField('visibility','${aid}')">Save</button>
                        </div>
                    </div>` : ''}
                </div>

                <!-- Tags -->
                <div class="myp-section">
                    <div class="myp-section-header">
                        <span class="myp-section-title">Tags</span>
                        ${isOwn ? `<button class="myp-edit-btn" onclick="editAvField('tags')"><span class="msi" style="font-size:14px;">edit</span></button>` : ''}
                    </div>
                    <div id="avfTagsView">${tagsViewHtml}</div>
                    ${isOwn ? `<div id="avfTagsEdit" style="display:none;">
                        <div id="avTagsChips" class="myp-lang-chips" style="margin-bottom:6px;"></div>
                        <div style="display:flex;gap:6px;margin-bottom:8px;">
                            <input id="avTagInput" class="vrcn-edit-field" placeholder="Add tag…" style="flex:1;"
                                onkeydown="if(event.key==='Enter'){event.preventDefault();avAddTag();}">
                            <button class="myp-add-lang-btn" onclick="avAddTag()"><span class="msi" style="font-size:15px;">add</span></button>
                        </div>
                        <div class="myp-edit-actions">
                            <button class="vrcn-button" onclick="cancelAvField('tags')">Cancel</button>
                            <button class="vrcn-button vrcn-btn-primary" onclick="saveAvField('tags','${aid}')">Save</button>
                        </div>
                    </div>` : ''}
                </div>

                <!-- Meta -->
                <div class="fd-meta" style="margin-bottom:14px;">${metaRows}</div>

                <!-- Actions -->
                <div style="display:flex;justify-content:flex-end;gap:6px;">
                    <button class="vrcn-button-round vrcn-btn-join" onclick="selectAvatar('${aid}');closeAvatarDetail()">
                        <span class="msi" style="font-size:14px;">checkroom</span> Use Avatar
                    </button>
                    <button class="vrcn-button-round" onclick="closeAvatarDetail()">Close</button>
                </div>
            </div>
        </div>`;
}

/* === Avatar inline edit === */
let _avVisState = 'public';

function editAvField(field) {
    Object.keys(_avFieldIds).forEach(f => {
        if (f === field) return;
        const ids = _avFieldIds[f];
        const v = document.getElementById(ids.view); if (v) v.style.display = '';
        const e = document.getElementById(ids.edit); if (e) e.style.display = 'none';
    });
    const ids = _avFieldIds[field];
    if (!ids) return;
    const v = document.getElementById(ids.view); if (v) v.style.display = 'none';
    const e = document.getElementById(ids.edit); if (e) e.style.display = '';

    if (field === 'name') {
        document.getElementById('avNameInput')?.focus();
    } else if (field === 'desc') {
        document.getElementById('avDescInput')?.focus();
    } else if (field === 'visibility') {
        _avVisState = (_avDetailData?.releaseStatus === 'public') ? 'public' : 'private';
    } else if (field === 'tags') {
        _avEditTags = [...(_avDetailData?.tags || [])];
        avRenderTagChips();
        document.getElementById('avTagInput')?.focus();
    }
}

function cancelAvField(field) {
    const ids = _avFieldIds[field];
    if (!ids) return;
    const v = document.getElementById(ids.view); if (v) v.style.display = '';
    const e = document.getElementById(ids.edit); if (e) e.style.display = 'none';
}

function saveAvField(field, avatarId) {
    const a = _avDetailData;
    if (!a) return;
    _avSavingField = field;
    const ids = _avFieldIds[field];
    const saveBtn = document.querySelector(`#${ids.edit} .vrcn-btn-primary`);
    if (saveBtn) saveBtn.disabled = true;

    // Build payload — always send all four fields, only the edited one changes
    const name          = field === 'name'       ? (document.getElementById('avNameInput')?.value.trim() || '') : (a.name || '');
    const description   = field === 'desc'       ? (document.getElementById('avDescInput')?.value ?? '') : (a.description || '');
    const releaseStatus = field === 'visibility' ? _avVisState : (a.releaseStatus || 'private');
    const tags          = field === 'tags'       ? [..._avEditTags] : (a.tags || []);

    sendToCS({ action: 'vrcUpdateAvatar', avatarId, name, description, releaseStatus, tags });
}

function avVisToggle(state, btn) {
    _avVisState = state;
    document.querySelectorAll('#avVisPublicBtn,#avVisPrivateBtn').forEach(b => b.classList.remove('active'));
    btn.classList.add('active');
}

function avRenderTagChips() {
    const container = document.getElementById('avTagsChips');
    if (!container) return;
    container.innerHTML = _avEditTags.length
        ? _avEditTags.map((t, i) =>
            `<span class="myp-lang-chip" data-idx="${i}">${esc(t)}<span class="myp-lang-remove" onclick="avRemoveTag(${i})">×</span></span>`
          ).join('')
        : `<div class="myp-empty">No tags</div>`;
}

function avAddTag() {
    const inp = document.getElementById('avTagInput');
    if (!inp) return;
    const val = inp.value.trim();
    if (val && !_avEditTags.includes(val)) {
        _avEditTags.push(val);
        avRenderTagChips();
    }
    inp.value = '';
    inp.focus();
}

function avRemoveTag(idx) {
    _avEditTags.splice(idx, 1);
    avRenderTagChips();
}

function onAvatarUpdateResult(data) {
    const fieldLabels = { name: 'Avatar name', desc: 'Description', visibility: 'Visibility', tags: 'Tags' };
    if (data.ok) {
        if (_avDetailData) {
            if (data.name          != null) _avDetailData.name          = data.name;
            if (data.description   != null) _avDetailData.description   = data.description;
            if (data.releaseStatus != null) _avDetailData.releaseStatus = data.releaseStatus;
            if (data.tags          != null) _avDetailData.tags          = data.tags;
        }
        const label = fieldLabels[_avSavingField] || 'Avatar';
        showToast(true, `${label} saved`);
        renderAvatarDetail(_avDetailData);
        if (avatarFilter === 'own') filterOwnAvatars();
    } else {
        showToast(false, data.error || 'Update failed');
        const ids = _avSavingField ? _avFieldIds[_avSavingField] : null;
        const saveBtn = ids ? document.querySelector(`#${ids.edit} .vrcn-btn-primary`) : null;
        if (saveBtn) saveBtn.disabled = false;
    }
}
