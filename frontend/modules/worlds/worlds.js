/* === Search (Worlds, Groups, People) === */
/* === World Tab: Favorites / Search filter === */
document.documentElement.addEventListener('languagechange', () => {
    document.getElementById('worldSortSelect')?._vnRefresh?.();
});
let _favRefreshTimer = null;
let _favWorldsLoaded = false;
let _worldEditMode = false;
let _worldEditSelected = new Set();
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

function getWorldRegionLabel(region) {
    const key = (region || '').toLowerCase();
    const labels = {
        eu: t('worlds.regions.eu', 'Europe'),
        us: t('worlds.regions.us', 'US West'),
        use: t('worlds.regions.use', 'US East'),
        jp: t('worlds.regions.jp', 'Japan')
    };
    return labels[key] || String(region || '').toUpperCase();
}

function getWorldPlayersLabel(count) {
    const key = count === 1 ? 'worlds.meta.players.one' : 'worlds.meta.players.other';
    const fallback = count === 1 ? '{count} player' : '{count} players';
    return tf(key, { count }, fallback);
}

function getWorldVisitCountLabel(count) {
    const key = count === 1 ? 'worlds.time_spent.visits.one' : 'worlds.time_spent.visits.other';
    const fallback = count === 1 ? '{count} visit' : '{count} visits';
    return tf(key, { count }, fallback);
}

function setWorldFilter(filter) {
    if (_worldEditMode) exitWorldEditMode();
    worldFilter = filter;
    document.getElementById('worldFilterFav').classList.toggle('active', filter === 'favorites');
    document.getElementById('worldFilterRecent').classList.toggle('active', filter === 'recent');
    document.getElementById('worldFilterMine').classList.toggle('active', filter === 'mine');
    document.getElementById('worldFilterSearch').classList.toggle('active', filter === 'search');
    document.getElementById('worldFavArea').style.display    = filter === 'favorites' ? '' : 'none';
    document.getElementById('worldRecentArea').style.display = filter === 'recent'    ? '' : 'none';
    document.getElementById('worldMineArea').style.display   = filter === 'mine'      ? '' : 'none';
    document.getElementById('worldSearchArea').style.display = filter === 'search'    ? '' : 'none';
    document.getElementById('worldToolbar')?.classList.toggle('tt-split', filter === 'favorites' || filter === 'search');
    const editBtn = document.getElementById('worldEditModeBtn');
    if (editBtn) editBtn.style.display = (filter === 'favorites' || filter === 'mine') ? '' : 'none';
    const wlBtn = document.getElementById('worldViewList');
    if (wlBtn) wlBtn.style.display = '';
    setPaginator('worldRecentPaginatorBar', '');
    renderWorldsListView();
    if (filter === 'favorites' && favWorldsData.length === 0) sendToCS({ action: 'vrcGetFavoriteWorlds' });
    if (filter === 'mine' && !_myWorldsLoaded) {
        _myWorldsLoaded = true;
        sendToCS({ action: 'vrcGetMyWorlds' });
    }
    if (filter === 'recent') sendToCS({ action: 'vrcGetVisitedWorlds' });
}

let _visitedWorldsData = [];
let _worldRecentPage = 0;

function renderVisitedWorlds(worlds) {
    if (Array.isArray(worlds)) _visitedWorldsData = worlds;
    const el = document.getElementById('worldRecentGrid');
    if (!el) return;
    const list = _visitedWorldsData;
    if (!Array.isArray(list) || list.length === 0) {
        el.innerHTML = emptyStateHtml('history', t('worlds.recent.empty', 'No recently visited worlds'));
        setPaginator('worldRecentPaginatorBar', '');
        return;
    }
    if (lvViewMode('worlds') === 'list' && lvReady()) {
        lvKeepScroll(el, () => _worldsListPage(el, list, _worldRecentPage, 'worldRecentPaginatorBar', 'worldRecentGoPage', p => { _worldRecentPage = p; }));
        return;
    }
    setPaginator('worldRecentPaginatorBar', '');
    el.classList.add('search-grid');
    el.innerHTML = list.map(w => renderWorldCard(w)).join('');
}

let _myWorldsData = [];
let _worldMinePage = 0;
let _favWorldsPage = 0;

function setWorldsViewMode(mode) {
    lvSetViewMode('worlds', mode);
    _worldMinePage = 0; _favWorldsPage = 0; _worldRecentPage = 0;
    _worldsSyncViewBtns();
    renderWorldsListView();
}

function _worldsSyncViewBtns() {
    const isList = lvViewMode('worlds') === 'list';
    document.getElementById('worldViewList')?.classList.toggle('active', isList);
    if (isList) {
        document.getElementById('worldGridLarge')?.classList.remove('active');
        document.getElementById('worldGridSmall')?.classList.remove('active');
    } else {
        const compact = localStorage.getItem('vrcn_gridSize_worlds') === 'compact';
        document.getElementById('worldGridLarge')?.classList.toggle('active', !compact);
        document.getElementById('worldGridSmall')?.classList.toggle('active', compact);
    }
}

function renderWorldsListView() {
    if (worldFilter === 'mine') renderMyWorlds(_myWorldsData);
    else if (worldFilter === 'recent') renderVisitedWorlds();
    else if (worldFilter === 'search') {
        const st = searchState?.worlds;
        if (st && st.results && st.results.length) renderSearchResults('worlds', st.results, 0, st.hasMore);
    }
    else filterFavWorlds();
}

function setWorldsListPageSize(v) { lvSetPageSize('worlds', v, () => { _worldMinePage = 0; _favWorldsPage = 0; _worldRecentPage = 0; renderWorldsListView(); }); }
function worldMineGoPage(p) { if (p < 0) return; _worldMinePage = p; renderMyWorlds(_myWorldsData); document.getElementById('worldMineGrid')?.scrollTo(0, 0); }
function favWorldsGoPage(p) { if (p < 0) return; _favWorldsPage = p; filterFavWorlds(); document.getElementById('favWorldsGrid')?.scrollTo(0, 0); }
function worldRecentGoPage(p) { if (p < 0) return; _worldRecentPage = p; renderVisitedWorlds(); document.getElementById('worldRecentGrid')?.scrollTo(0, 0); }

function _wlAuthorTags(w) {
    return (w.tags || []).filter(x => x.startsWith('author_tag_')).map(x => x.replace('author_tag_', ''));
}

function _wlPublished(w) { return w.created_at || w.createdAt || ''; }
function _wlUpdated(w)   { return w.updated_at || w.updatedAt || ''; }

function _wlListDate(value) {
    if (!value) return '';
    const raw = /^\d{4}-\d{2}-\d{2}$/.test(value) ? value + 'T00:00:00' : value;
    const d = new Date(raw);
    return isNaN(d) ? String(value) : fmtShortDate(d);
}

function _wlValue(w, field) {
    switch (field) {
        case 'name':      return (w.name || '').toLowerCase();
        case 'tags':      return _wlAuthorTags(w).join(' ').toLowerCase();
        case 'favorites': return w.favorites || 0;
        case 'users':     return w.occupants || 0;
        case 'visits':    return w.worldVisitCount || 0;
        case 'published': return Date.parse(_wlPublished(w)) || 0;
        case 'updated':   return Date.parse(_wlUpdated(w)) || 0;
        case 'time':      return w.worldTimeSeconds || 0;
        case 'lastseen':  return w.worldLastVisited || '';
        default:          return (w.name || '').toLowerCase();
    }
}

function buildWorldsListHtml(worlds, staticHeader) {
    let rows = '';
    worlds.forEach(w => {
        const wid = jsq(w.id || '');
        const thumb = w.thumbnailImageUrl || w.imageUrl || '';
        const tags = _wlAuthorTags(w);
        const tagsHtml = tags.length
            ? `<div class="lv-tags" title="${esc(tags.join(', '))}">${tags.slice(0, 3).map(x => `<span class="vrcn-badge">${esc(x)}</span>`).join('')}${tags.length > 3 ? `<span class="lv-tags-more">+${tags.length - 3}</span>` : ''}</div>`
            : '';
        rows += tlTableRow('worldsList', ` data-wid="${esc(w.id || '')}" onclick="openWorldSearchDetail('${wid}')"`, {
            icon:      `<td>${lvIcon(thumb, w.name, true)}</td>`,
            name:      `<td class="lv-name">${esc(w.name || '')}</td>`,
            tags:      `<td>${tagsHtml}</td>`,
            favorites: `<td class="lv-num">${w.favorites ? esc(Number(w.favorites).toLocaleString()) : ''}</td>`,
            users:     `<td class="lv-num">${w.occupants ? esc(Number(w.occupants).toLocaleString()) : ''}</td>`,
            visits:    `<td class="lv-num">${w.worldVisitCount ? esc(String(w.worldVisitCount)) : ''}</td>`,
            published: `<td class="lv-sub">${esc(_wlListDate(_wlPublished(w)))}</td>`,
            updated:   `<td class="lv-sub">${esc(_wlListDate(_wlUpdated(w)))}</td>`,
            time:      `<td class="lv-num">${esc(lvDuration(w.worldTimeSeconds))}</td>`,
            lastseen:  `<td class="lv-date">${esc(lvDateTime(w.worldLastVisited))}</td>`,
        });
    });
    return `<div class="lv-scroll">${tlTableHtml('worldsList', rows, staticHeader)}</div>`;
}

function _worldsListPage(el, all, page, barId, pageFn, setPage) {
    const sorted = lvSort(all, 'worldsList', _wlValue);
    const size = lvPageSize('worlds');
    const totalPages = Math.ceil(sorted.length / size) || 1;
    let p = page;
    if (p >= totalPages) p = totalPages - 1;
    if (p < 0) p = 0;
    setPage(p);
    el.classList.remove('search-grid');
    el.innerHTML = buildWorldsListHtml(sorted.slice(p * size, (p + 1) * size));
    lvEditDecorateList(el, 'worlds');
    setPaginator(barId, lvPaginator('worlds', p, totalPages, pageFn, sorted.length, 'setWorldsListPageSize'));
}

function renderMyWorlds(worlds) {
    const el = document.getElementById('worldMineGrid');
    if (!el) return;
    if (Array.isArray(worlds)) _myWorldsData = worlds;
    const list = _myWorldsData;
    if (!Array.isArray(list) || list.length === 0) {
        el.innerHTML = `<div class="empty-msg"><div class="empty-msg-icon"><span class="msi">upload</span></div><div class="empty-msg-title">${t('worlds.mine.upload_title', 'Upload a world')}</div><div class="empty-msg-desc">${t('worlds.mine.empty', 'No worlds uploaded yet')}</div></div>`;
        setPaginator('worldMinePaginatorBar', '');
        return;
    }
    if (lvViewMode('worlds') === 'list' && lvReady()) {
        lvKeepScroll(el, () => _worldsListPage(el, list, _worldMinePage, 'worldMinePaginatorBar', 'worldMineGoPage', p => { _worldMinePage = p; }));
        return;
    }
    setPaginator('worldMinePaginatorBar', '');
    el.classList.add('search-grid');
    el.innerHTML = list.map(w => renderWorldCard(w)).join('');
}

function _wdGroupOptionLabel(g) {
    const count = favWorldsData.filter(w => w.favoriteGroup === g.name).length;
    const cap   = isLocalFavGroup(g) ? (g.capacity || 200) : Math.max(g.capacity || 100, 100);
    const marker = (!isLocalFavGroup(g) && g.type === 'vrcPlusWorld') ? ' [VRC+]' : '';
    return `${esc(g.displayName || g.name)} ${count}/${cap}${marker}`;
}

function renderFavWorlds(payload) {
    // Reset refresh button if it was spinning
    const refreshBtn = document.getElementById('favWorldsRefreshBtn');
    if (refreshBtn) { refreshBtn.disabled = false; const ico = refreshBtn.querySelector('.msi'); if (ico) ico.textContent = 'refresh'; }
    // payload is { worlds: [...], groups: [...] }
    const worlds = payload?.worlds || payload || [];
    const groups = payload?.groups || [];
    _favWorldsLoaded = true;
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
        sel.innerHTML = `<option value="">${t('worlds.favorites.group.all', 'All Favorites')}</option>` +
            groups.map(g => `<option value="${esc(g.name)}">${_wdGroupOptionLabel(g)}</option>`).join('');
        const stillValid = groups.some(g => g.name === prev);
        favWorldGroupFilter = stillValid ? prev : '';
        sel.value = favWorldGroupFilter;
        if (sel._vnRefresh) sel._vnRefresh();
    }
    updateFavWorldGroupHeader();
    filterFavWorlds();
    if (typeof renderDashFavWorlds === 'function') renderDashFavWorlds();
}

function setFavWorldGroup(val) {
    favWorldGroupFilter = val;
    updateFavWorldGroupHeader();
    filterFavWorlds();
}

function updateFavWorldGroupHeader() {
    const header = document.getElementById('favWorldGroupHeader');
    const delBtn = document.getElementById('favWorldGroupDeleteBtn');
    const badge = document.getElementById('favWorldGroupVrcPlusBadge');
    const localBadge = document.getElementById('favWorldGroupLocalBadge');
    if (!header) return;
    if (!favWorldGroupFilter) {
        if (delBtn) delBtn.style.display = 'none';
        if (badge) badge.style.display = 'none';
        if (localBadge) localBadge.style.display = 'none';
    } else {
        const g = favWorldGroups.find(x => x.name === favWorldGroupFilter);
        const isLocal = isLocalFavGroup(g);
        if (delBtn) delBtn.style.display = isLocal ? '' : 'none';
        if (badge) badge.style.display = (g?.type === 'vrcPlusWorld') ? '' : 'none';
        if (localBadge) localBadge.style.display = isLocal ? '' : 'none';
    }
    const anyVisible = [delBtn, badge, localBadge]
        .some(el => el && el.style.display !== 'none');
    header.style.display = anyVisible ? 'flex' : 'none';
}


function _favGroupVisDropdown(groupName, groupType, currentVis) {
    const opts = [
        { value: 'public',  key: 'worlds.favorites.visibility.public',  label: 'Visible for everyone' },
        { value: 'friends', key: 'worlds.favorites.visibility.friends', label: 'Visible for friends' },
        { value: 'private', key: 'worlds.favorites.visibility.private', label: 'Visible only to you' },
    ];
    const optsHtml = opts.map(o =>
        `<option value="${o.value}"${o.value === currentVis ? ' selected' : ''}>${esc(t(o.key, o.label))}</option>`
    ).join('');
    return `<select class="vrcn-dropdown" style="min-width:160px;" onchange="saveFavGroupVisibility(this.value,'${jsq(groupName)}')">${optsHtml}</select>`;
}

function saveFavGroupVisibility(visibility, groupName) {
    const name = groupName || favWorldGroupFilter;
    const g = favWorldGroups.find(x => x.name === name);
    if (!g) return;
    sendToCS({ action: 'vrcUpdateFavoriteGroup', groupType: g.type, groupName: g.name, displayName: g.displayName || g.name, visibility });
}

function onFavoriteGroupUpdated(data) {
    if (!data.ok) { if (_worldEditMode) filterFavWorlds(); return; }
    const g = favWorldGroups.find(x => x.name === data.groupName);
    if (g) {
        if (data.displayName) g.displayName = data.displayName;
        if (data.visibility)  g.visibility  = data.visibility;
    }
    // Update dropdown option
    const sel = document.getElementById('favWorldGroupFilter');
    if (sel) {
        const opt = [...sel.options].find(o => o.value === data.groupName);
        if (opt && g) opt.textContent = _wdGroupOptionLabel(g);
    }
    updateFavWorldGroupHeader();
    filterFavWorlds(); // re-render headers with updated visibility
}

function _wdGroupHeaderHtml(g, count, first) {
    const isLocal = isLocalFavGroup(g);
    const cap = isLocal ? (g.capacity || 200) : Math.max(g.capacity || 100, 100);
    const visHtml = (!isLocal && _worldEditMode)
        ? _favGroupVisDropdown(g.name, g.type, g.visibility)
        : '';
    return `<div class="fav-group-header${first ? ' fav-group-header-first' : ''}">
        ${_wdGroupTitleHtml(g)}
        ${favGroupBadge(g)}
        <span class="fav-group-count">${count}/${cap}</span>
        ${visHtml}
    </div>`;
}

function _wdGroupTitleHtml(g) {
    const disp = g.displayName || g.name;
    if (!_worldEditMode) return `<span class="topbar-title">${esc(disp)}</span>`;
    return `<span class="fav-group-name-edit">
        <input class="vrcn-edit-field fav-group-name-input" maxlength="64" value="${esc(disp)}" data-group="${esc(g.name)}" data-type="${esc(g.type || 'world')}" data-orig="${esc(disp)}" oninput="wdOnGroupNameInput(this)" onclick="event.stopPropagation()">
        <span class="fav-group-name-actions" style="display:none;">
            <button class="vrcn-button vrcn-btn-primary" onclick="wdSaveGroupName(this)">${t('common.save', 'Save')}</button>
            <button class="vrcn-button" onclick="wdCancelGroupName(this)">${t('common.cancel', 'Cancel')}</button>
        </span>
    </span>`;
}

function wdOnGroupNameInput(inp) {
    const actions = inp.closest('.fav-group-name-edit')?.querySelector('.fav-group-name-actions');
    if (!actions) return;
    const v = inp.value.trim();
    actions.style.display = (v && v !== inp.dataset.orig) ? 'inline-flex' : 'none';
}

function wdSaveGroupName(btn) {
    const inp = btn.closest('.fav-group-name-edit')?.querySelector('.fav-group-name-input');
    if (!inp) return;
    const newName = inp.value.trim();
    if (!newName || newName === inp.dataset.orig) return;
    btn.disabled = true;
    sendToCS({ action: 'vrcUpdateFavoriteGroup', groupType: inp.dataset.type, groupName: inp.dataset.group, displayName: newName });
}

function wdCancelGroupName(btn) {
    const wrap = btn.closest('.fav-group-name-edit');
    const inp = wrap?.querySelector('.fav-group-name-input');
    if (inp) inp.value = inp.dataset.orig;
    const actions = wrap?.querySelector('.fav-group-name-actions');
    if (actions) actions.style.display = 'none';
}

/* === Shared world card renderer (search + favorites) === */
function renderWorldCard(w) {
    const thumb = w.thumbnailImageUrl || w.imageUrl || '';
    const tags = (w.tags || []).filter(t => t.startsWith('author_tag_')).map(t => t.replace('author_tag_','')).slice(0,4);
    const tagsHtml = tags.length ? `<div class="cc-tags">${tags.map(t => `<span class="vrcn-badge">${esc(t)}</span>`).join('')}</div>` : '';
    const wid = jsq(w.id);
    const ts = w.worldTimeSeconds || 0;
    const timeBadge = ts > 0 ? `<div class="cc-time-top"><span class="msi">schedule</span> ${formatDuration(ts)}</div>` : '';
    if (_worldEditMode) {
        const isSelected = _worldEditSelected.has(w.id);
        const checkIcon = isSelected
            ? `<span class="msi" style="font-size:22px;color:var(--accent);">check_circle</span>`
            : `<span class="msi" style="font-size:22px;color:rgba(255,255,255,0.7);">radio_button_unchecked</span>`;
        return `<div class="vrcn-content-card" data-wid="${esc(w.id)}" onclick="toggleWorldEditSelect('${wid}',this)" style="user-select:none;">
            <div class="cc-bg" style="background-image:url('${cssUrl(imgThumb(thumb, 256))}')"></div>
            <div class="cc-scrim"></div>
            ${timeBadge}
            <div class="wd-edit-check">${checkIcon}</div>
            <div class="cc-content">
                <div class="cc-name">${esc(w.name)}</div>
                <div class="cc-bottom-row">
                    <div class="cc-meta">${esc(w.authorName)} · <span class="msi">person</span>${w.occupants} · <span class="msi">favorite</span>${w.favorites}</div>
                    ${tagsHtml}
                </div>
            </div>
            ${isSelected ? '<div class="wd-edit-sel-border"></div>' : ''}</div>`;
    }
    return `<div class="vrcn-content-card" onclick="openWorldSearchDetail('${wid}')">
        <div class="cc-bg" style="background-image:url('${cssUrl(imgThumb(thumb, 256))}')"></div>
        <div class="cc-scrim"></div>
        ${timeBadge}
        <div class="cc-content">
            <div class="cc-name">${esc(w.name)}</div>
            <div class="cc-bottom-row">
                <div class="cc-meta">${esc(w.authorName)} · <span class="msi">person</span>${w.occupants} · <span class="msi">favorite</span>${w.favorites}</div>
                ${tagsHtml}
            </div>
        </div>
    </div>`;
}

function filterFavWorlds() {
    const q = (document.getElementById('favWorldSearchInput')?.value || '').toLowerCase();
    let filtered = favWorldsData;
    if (favWorldGroupFilter) filtered = filtered.filter(w => w.favoriteGroup === favWorldGroupFilter);
    if (q) filtered = filtered.filter(w => (w.name||'').toLowerCase().includes(q) || (w.authorName||'').toLowerCase().includes(q));
    const el = document.getElementById('favWorldsGrid');
    if (!filtered.length) {
        el.innerHTML = q || favWorldGroupFilter
            ? emptyStateHtml('search', t('worlds.favorites.no_match', 'No favorites match your filter'))
            : emptyStateHtml('favorite', t('worlds.favorites.empty', 'No favorite worlds found'));
        setPaginator('favWorldsPaginatorBar', '');
        if (_worldEditMode) updateWorldEditBar();
        return;
    }
    if (lvViewMode('worlds') === 'list' && lvReady()) {
        lvKeepScroll(el, () => _worldsListPage(el, filtered, _favWorldsPage, 'favWorldsPaginatorBar', 'favWorldsGoPage', p => { _favWorldsPage = p; }));
        if (_worldEditMode) updateWorldEditBar();
        return;
    }
    setPaginator('favWorldsPaginatorBar', '');
    el.classList.add('search-grid');
    // Group by category when showing All Favorites
    if (!favWorldGroupFilter && favWorldGroups.length > 1) {
        let html = '';
        let first = true;
        favWorldGroups.forEach(g => {
            const groupWorlds = filtered.filter(w => w.favoriteGroup === g.name);
            if (!groupWorlds.length && !_worldEditMode) return;
            html += _wdGroupHeaderHtml(g, groupWorlds.length, first);
            html += groupWorlds.map(w => renderWorldCard(w)).join('');
            first = false;
        });
        el.innerHTML = html;
        el.querySelectorAll('select.vrcn-dropdown').forEach(initVnSelect);
    } else {
        const selected = favWorldGroupFilter
            ? favWorldGroups.find(x => x.name === favWorldGroupFilter)
            : null;
        const head = selected ? _wdGroupHeaderHtml(selected, filtered.length, true) : '';
        el.innerHTML = head + filtered.map(w => renderWorldCard(w)).join('');
        if (head) el.querySelectorAll('select.vrcn-dropdown').forEach(initVnSelect);
    }
    if (_worldEditMode) updateWorldEditBar();
}

/* === World Edit Mode === */
function toggleWorldEditMode() {
    if (_worldEditMode) { exitWorldEditMode(); return; }
    _worldEditMode = true;
    _worldEditSelected = new Set();
    const btn = document.getElementById('worldEditModeBtn');
    if (btn) { btn.innerHTML = `<span class="msi" style="font-size:16px;">check</span> <span>${t('worlds.edit.done', 'Done')}</span>`; btn.classList.add('active'); }
    const bar = document.getElementById('worldEditBar');
    if (bar) bar.style.display = 'flex';
    _wdEditRerender();
    updateFavWorldGroupHeader();
}

function exitWorldEditMode() {
    _worldEditMode = false;
    _worldEditSelected = new Set();
    const btn = document.getElementById('worldEditModeBtn');
    if (btn) { btn.innerHTML = `<span class="msi" style="font-size:16px;">edit</span> <span>${t('worlds.edit.button', 'Edit')}</span>`; btn.classList.remove('active'); }
    const bar = document.getElementById('worldEditBar');
    if (bar) bar.style.display = 'none';
    ['worldEditMovePicker', 'worldEditAddFavPicker'].forEach(id => {
        const picker = document.getElementById(id);
        if (picker) { picker.style.display = 'none'; picker.innerHTML = ''; }
    });
    _wdEditRerender();
    updateFavWorldGroupHeader();
}

function toggleWorldEditSelect(id, el) {
    if (_worldEditSelected.has(id)) {
        _worldEditSelected.delete(id);
        const chk = el?.querySelector('.wd-edit-check .msi');
        if (chk) { chk.textContent = 'radio_button_unchecked'; chk.style.color = 'rgba(255,255,255,0.7)'; }
        el?.querySelector('.wd-edit-sel-border')?.remove();
    } else {
        _worldEditSelected.add(id);
        const chk = el?.querySelector('.wd-edit-check .msi');
        if (chk) { chk.textContent = 'check_circle'; chk.style.color = 'var(--accent)'; }
        if (el && !el.querySelector('.wd-edit-sel-border')) {
            el.insertAdjacentHTML('beforeend', '<div class="wd-edit-sel-border"></div>');
        }
    }
    updateWorldEditBar();
}

function worldEditSelectAll() {
    const filtered = _wdEditVisibleList();
    const allSelected = filtered.length > 0 && filtered.every(w => _worldEditSelected.has(w.id));
    if (allSelected) filtered.forEach(w => _worldEditSelected.delete(w.id));
    else filtered.forEach(w => _worldEditSelected.add(w.id));
    _wdEditRerender();
}

function updateWorldEditBar() {
    const count = _worldEditSelected.size;
    const countEl = document.getElementById('worldEditCount');
    if (countEl) countEl.textContent = tf('worlds.edit.selected', { count }, '{count} selected');
    const selectAllBtn = document.getElementById('worldEditSelectAllBtn');
    if (selectAllBtn) {
        const filtered = _wdEditVisibleList();
        const allSel = filtered.length > 0 && filtered.every(w => _worldEditSelected.has(w.id));
        selectAllBtn.textContent = allSel ? t('worlds.edit.deselect_all', 'Deselect All') : t('worlds.edit.select_all', 'Select All');
    }
    document.querySelectorAll('.wd-edit-action').forEach(b => b.disabled = count === 0);
    _wdEditSyncButtons();
}

function worldEditShowMoveMenu(btn) {
    if (_worldEditSelected.size === 0) return;
    const picker = document.getElementById('worldEditMovePicker');
    if (!picker) return;
    if (picker.style.display === 'block') { picker.style.display = 'none'; picker.innerHTML = ''; return; }
    const groups = (typeof favWorldGroups !== 'undefined') ? favWorldGroups : [];
    picker.innerHTML = groups.map(g => {
        const count = favWorldsData.filter(fw => fw.favoriteGroup === g.name).length;
        const gn = jsq(g.name), gt = jsq(g.type);
        return `<div class="vn-select-option" onclick="worldEditMoveSelected('${gn}','${gt}')">
            <span class="msi" style="font-size:14px;flex-shrink:0;">folder</span>
            <span style="flex:1;">${esc(g.displayName || g.name)}</span>
            ${favGroupBadge(g)}
            <span style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx2);flex-shrink:0;">${count}</span>
        </div>`;
    }).join('');
    picker.style.display = 'block';
    setTimeout(() => {
        const close = (e) => {
            if (!picker.contains(e.target) && e.target !== btn) {
                picker.style.display = 'none';
                picker.innerHTML = '';
                document.removeEventListener('click', close);
            }
        };
        document.addEventListener('click', close);
    }, 0);
}

function worldEditMoveSelected(groupName, groupType) {
    if (_worldEditSelected.size === 0) return;
    const picker = document.getElementById('worldEditMovePicker');
    if (picker) { picker.style.display = 'none'; picker.innerHTML = ''; }
    const toMove = [..._worldEditSelected];
    toMove.forEach(worldId => {
        const entry = favWorldsData.find(w => w.id === worldId);
        if (entry && entry.favoriteGroup !== groupName) {
            sendToCS({ action: 'vrcAddWorldFavorite', worldId, groupName, groupType, oldFvrtId: entry.favoriteId || '' });
        }
    });
    exitWorldEditMode();
}

function worldEditRemoveSelected() {
    if (_worldEditSelected.size === 0) return;
    const toRemove = [..._worldEditSelected];
    toRemove.forEach(worldId => {
        const entry = favWorldsData.find(w => w.id === worldId);
        if (entry) sendToCS({ action: 'vrcRemoveWorldFavorite', worldId, fvrtId: entry.favoriteId });
    });
    exitWorldEditMode();
}

/* === Local Groups (Worlds) === */
function worldLocalGroupCount() {
    return (typeof favWorldGroups !== 'undefined' ? favWorldGroups : []).filter(isLocalFavGroup).length;
}

function worldShowCreateLocalGroup(btn) {
    const panel = document.getElementById('worldCreateLocalPanel');
    if (!panel) return;
    if (panel.style.display === 'block') { worldCancelCreateLocalGroup(); return; }
    if (worldLocalGroupCount() >= 100) { showToast(false, localFavErrorText('group_limit')); return; }
    panel.style.display = 'block';
    const input = document.getElementById('worldCreateLocalInput');
    if (input) { input.value = ''; input.focus(); }
    setTimeout(() => {
        const close = (e) => {
            if (!panel.contains(e.target) && !(btn && btn.contains(e.target))) {
                panel.style.display = 'none';
                document.removeEventListener('click', close);
            }
        };
        document.addEventListener('click', close);
    }, 0);
}

function worldCancelCreateLocalGroup() {
    const panel = document.getElementById('worldCreateLocalPanel');
    if (panel) panel.style.display = 'none';
}

function worldSaveLocalGroup() {
    const input = document.getElementById('worldCreateLocalInput');
    const name = (input?.value || '').trim();
    if (!name) { showToast(false, localFavErrorText('empty_name')); return; }
    sendToCS({ action: 'vrcCreateLocalGroup', kind: 'world', displayName: name });
    worldCancelCreateLocalGroup();
}

function deleteCurrentLocalWorldGroup(btn) {
    const g = favWorldGroups.find(x => x.name === favWorldGroupFilter);
    if (!g || !isLocalFavGroup(g)) return;
    if (btn && btn.dataset.confirm !== '1') {
        btn.dataset.confirm = '1';
        btn.classList.add('myp-edit-btn-danger');
        btn.title = t('favorites.delete_local_group_confirm', 'Click again to delete');
        clearTimeout(btn._confirmTimer);
        btn._confirmTimer = setTimeout(() => {
            btn.dataset.confirm = '0';
            btn.classList.remove('myp-edit-btn-danger');
            btn.title = t('favorites.delete_local_group', 'Delete local group');
        }, 3000);
        return;
    }
    if (btn) { btn.dataset.confirm = '0'; btn.classList.remove('myp-edit-btn-danger'); clearTimeout(btn._confirmTimer); }
    sendToCS({ action: 'vrcDeleteLocalGroup', kind: 'world', groupName: g.name });
    favWorldGroupFilter = '';
}


_worldsSyncViewBtns();

function _wdEditIsMine() {
    return worldFilter === 'mine';
}

function _wdEditVisibleList() {
    if (_wdEditIsMine()) {
        const q = (document.getElementById('worldMineSearchInput')?.value || '').toLowerCase();
        return q
            ? _myWorldsData.filter(w => (w.name || '').toLowerCase().includes(q) || (w.authorName || '').toLowerCase().includes(q))
            : _myWorldsData;
    }
    const q = (document.getElementById('favWorldSearchInput')?.value || '').toLowerCase();
    let filtered = favWorldsData;
    if (favWorldGroupFilter) filtered = filtered.filter(w => w.favoriteGroup === favWorldGroupFilter);
    if (q) filtered = filtered.filter(w => (w.name || '').toLowerCase().includes(q) || (w.authorName || '').toLowerCase().includes(q));
    return filtered;
}

function _wdEditRerender() {
    if (_wdEditIsMine()) renderMyWorlds();
    else filterFavWorlds();
}

function _wdEditSyncButtons() {
    const isMine = _wdEditIsMine();
    const show = (id, on) => {
        const el = document.getElementById(id);
        if (el) el.style.display = on ? '' : 'none';
    };
    show('worldEditAddFavWrap', isMine);
    show('worldEditMoveWrap', !isMine);
    show('worldEditRemoveBtn', !isMine);
    show('worldEditDeleteBtn', isMine);
}

lvEditRegister('worlds', {
    attr: 'data-wid',
    isActive: () => _worldEditMode,
    isSelected: id => _worldEditSelected.has(id),
    toggle: id => { if (_worldEditSelected.has(id)) _worldEditSelected.delete(id); else _worldEditSelected.add(id); },
    onChange: () => updateWorldEditBar(),
});

function worldEditShowAddFavMenu(btn) {
    if (_worldEditSelected.size === 0) return;
    const picker = document.getElementById('worldEditAddFavPicker');
    if (!picker) return;
    if (picker.style.display === 'block') { picker.style.display = 'none'; picker.innerHTML = ''; return; }
    const groups = (typeof favWorldGroups !== 'undefined') ? favWorldGroups : [];
    picker.innerHTML = groups.map(g => {
        const count = favWorldsData.filter(w => w.favoriteGroup === g.name).length;
        const gn = jsq(g.name), gt = jsq(g.type);
        return `<div class="vn-select-option" onclick="worldEditAddToFavorites('${gn}','${gt}')">
            <span class="msi" style="font-size:14px;flex-shrink:0;">favorite</span>
            <span style="flex:1;">${esc(g.displayName || g.name)}</span>
            ${favGroupBadge(g)}
            <span style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx2);flex-shrink:0;">${count}</span>
        </div>`;
    }).join('') || `<div class="vn-select-option" style="pointer-events:none;color:var(--tx3);">${esc(t('worlds.favorites.no_groups', 'No favorite groups'))}</div>`;
    picker.style.display = 'block';
    setTimeout(() => {
        const close = (e) => {
            if (!picker.contains(e.target) && e.target !== btn) {
                picker.style.display = 'none';
                picker.innerHTML = '';
                document.removeEventListener('click', close);
            }
        };
        document.addEventListener('click', close);
    }, 0);
}

function worldEditAddToFavorites(groupName, groupType) {
    if (_worldEditSelected.size === 0) return;
    const picker = document.getElementById('worldEditAddFavPicker');
    if (picker) { picker.style.display = 'none'; picker.innerHTML = ''; }
    const ids = [..._worldEditSelected];
    ids.forEach(worldId => {
        if (favWorldsData.some(w => w.id === worldId && w.favoriteGroup === groupName)) return;
        sendToCS({ action: 'vrcAddWorldFavorite', worldId, groupName, groupType, oldFvrtId: '' });
    });
    showToast(true, tf('worlds.edit.added_to_favorites', { count: ids.length }, 'Added {count} worlds to favorites'));
    exitWorldEditMode();
}

let _wdBulkDeletePending = 0;
let _wdBulkDeleteOk = 0;

function worldEditDeleteSelected() {
    const ids = [..._worldEditSelected];
    if (!ids.length) return;
    const names = ids.map(id => (_myWorldsData.find(w => w.id === id)?.name) || id).slice(0, 6);
    const more = ids.length - names.length;
    const listHtml = names.map(n => `<div>${esc(n)}</div>`).join('')
        + (more > 0 ? `<div>${esc(tf('worlds.edit.bulk_delete_more', { count: more }, '+{count} more'))}</div>` : '');

    vrcnConfirmDelete({
        id: 'worldBulkDeleteModal',
        title: t('worlds.edit.bulk_delete', 'Bulk Delete'),
        icon: 'delete',
        message: tf('worlds.edit.bulk_delete_confirm', { count: ids.length },
            'Delete {count} worlds? They are hidden and their files are removed. This cannot be undone.'),
        listHtml,
        confirmLabel: t('worlds.edit.bulk_delete', 'Bulk Delete'),
        onConfirm: () => {
            _wdBulkDeletePending = ids.length;
            _wdBulkDeleteOk = 0;
            ids.forEach(worldId => sendToCS({ action: 'vrcDeleteWorld', worldId }));
            const gone = new Set(ids);
            _myWorldsData = _myWorldsData.filter(w => !gone.has(w.id));
            exitWorldEditMode();
        },
    });
}

function wdBulkDeleteConsume(success) {
    if (_wdBulkDeletePending <= 0) return false;
    _wdBulkDeletePending--;
    if (success) _wdBulkDeleteOk++;
    if (_wdBulkDeletePending === 0) {
        showToast(_wdBulkDeleteOk > 0, tf('worlds.edit.bulk_delete_done', { count: _wdBulkDeleteOk }, 'Deleted {count} worlds'));
        sendToCS({ action: 'vrcGetMyWorlds' });
    }
    return true;
}
