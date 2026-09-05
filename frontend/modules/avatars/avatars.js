/* === Avatars Tab === */
let _avFavRefreshTimer = null;
let _avEditMode = false;
let _avEditSelected = new Set();
let _avIcuBuffer = [];
let _avIcuBufferCursor = 0;
let _avIcuFetchHasMore = false;
function avatarEmptyMessage(key, fallback) {
    const icon = key.includes('login') ? 'login'
        : (key.includes('no_match') || key.includes('no_results')) ? 'search'
        : key.includes('recent') ? 'history'
        : 'checkroom';
    return emptyStateHtml(icon, t(key, fallback));
}

function avatarSearchPrompt() {
    return `<div class="empty-msg"><div class="empty-msg-icon"><span class="msi">checkroom</span></div><div class="empty-msg-title">${t('avatars.search.empty_prompt', 'Find an avatar')}</div><div class="empty-msg-desc">${t('avatars.search.empty_prompt_desc', 'Search public avatars by name.')}</div></div>`;
}

function avatarCountText(count) {
    return tf(count === 1 ? 'avatars.count.one' : 'avatars.count.other', { count }, count === 1 ? '{count} avatar' : '{count} avatars');
}

function avatarResultCountText(count) {
    return tf(count === 1 ? 'avatars.results.one' : 'avatars.results.other', { count }, count === 1 ? '{count} result' : '{count} results');
}

function avatarStatusBadge(isPublic) {
    return isPublic
        ? `<span class="vrcn-badge accent"><span class="msi" style="font-size:10px;">public</span> ${t('avatars.labels.public', 'Public')}</span>`
        : `<span class="vrcn-badge private"><span class="msi" style="font-size:10px;">lock</span> ${t('avatars.labels.private', 'Private')}</span>`;
}

function avatarCurrentBadge(isActive) {
    return isActive ? `<span class="vrcn-badge current">${t('avatars.labels.current', 'Current')}</span>` : '';
}

function avatarFavoriteActionLabel(isFavorite) {
    return t(isFavorite ? 'avatars.actions.unfavorite' : 'avatars.actions.favorite', isFavorite ? 'Unfavorite' : 'Favorite');
}

function avatarDetailFieldLabel(field) {
    const labels = {
        name: t('avatars.detail.fields.name', 'Avatar name'),
        desc: t('avatars.detail.sections.description', 'Description'),
        visibility: t('avatars.detail.sections.visibility', 'Visibility'),
        tags: t('avatars.detail.sections.tags', 'Tags'),
    };
    return labels[field] || t('avatars.detail.fields.avatar', 'Avatar');
}

function rerenderAvatarTranslations() {
    const renameSaveBtn = document.querySelector('#favAvatarGroupRenameRow .vrcn-btn-primary');
    if (renameSaveBtn && !renameSaveBtn.disabled) renameSaveBtn.textContent = t('common.save', 'Save');
    if (avatarFilter === 'favorites') {
        updateFavAvatarGroupHeader();
        filterFavAvatars();
    } else if (avatarFilter === 'search') {
        if (avatarSearchResults.length) renderSearchGrid();
        else {
            const grid = document.getElementById('avatarSearchGrid');
            if (grid) grid.innerHTML = avatarSearchPrompt();
        }
    } else if (avatarFilter === 'rose') {
        if (roseDbLoaded) filterRoseDb();
    } else {
        filterOwnAvatars();
    }
    if (_avFavPickerAvatarId) renderAvFavPickerList(_avFavPickerAvatarId);
}

document.documentElement.addEventListener('languagechange', () => {
    rerenderAvatarTranslations();
});

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
        document.getElementById('avatarGrid').innerHTML = avatarEmptyMessage('avatars.empty.login_prompt', 'Login to VRChat to see your avatars');
        return;
    }
    document.getElementById('avatarGrid').classList.add('avatar-grid');
    document.getElementById('avatarGrid').innerHTML = sk('avatar', 6);
    sendToCS({ action: 'vrcGetAvatars', filter: 'own' });
}

function refreshFavAvatars() {
    const btn = document.getElementById('favAvatarsRefreshBtn');
    if (btn) { btn.disabled = true; btn.querySelector('.msi').textContent = 'hourglass_empty'; }
    sendToCS({ action: 'vrcGetAvatars', filter: 'favorites' });
}

function setAvatarFilter(filter) {
    if (_avEditMode) exitAvEditMode();
    avatarFilter = filter;
    document.querySelectorAll('#avatarFilterBtns .sub-tab-btn').forEach(b => b.classList.remove('active'));
    const btnMap = { own: 'avatarFilterOwn', favorites: 'avatarFilterFav', recent: 'avatarFilterRecent', rose: 'avatarFilterRose', search: 'avatarFilterSearch' };
    const btn = document.getElementById(btnMap[filter]);
    if (btn) btn.classList.add('active');

    const ownArea    = document.getElementById('avatarOwnArea');
    const favArea    = document.getElementById('avatarFavArea');
    const recentArea = document.getElementById('avatarRecentArea');
    const roseArea   = document.getElementById('avatarRoseArea');
    const searchArea = document.getElementById('avatarSearchArea');

    if (ownArea)    ownArea.style.display    = filter === 'own'       ? '' : 'none';
    if (favArea)    favArea.style.display    = filter === 'favorites' ? '' : 'none';
    if (recentArea) recentArea.style.display = filter === 'recent'    ? '' : 'none';
    if (roseArea)   roseArea.style.display   = filter === 'rose'      ? '' : 'none';
    if (searchArea) searchArea.style.display = filter === 'search'    ? '' : 'none';

    const alBtn = document.getElementById('avatarViewList');
    if (alBtn) alBtn.style.display = '';
    setPaginator('avatarRecentPaginatorBar', '');
    setPaginator('avatarRosePaginatorBar', '');

    const _dbDrop = document.getElementById('avatarSearchDbDrop');
    if (_dbDrop) {
        const _dbWrap = _dbDrop._vnSelect ? _dbDrop.parentNode : _dbDrop;
        _dbWrap.style.display = filter === 'search' ? '' : 'none';
    }
    _avUpdateVrcnFilterVisibility();

    document.getElementById('avatarCount').textContent = '';
    const _sc = document.getElementById('avatarSearchCount'); if (_sc) _sc.textContent = '';

    _avSyncEditBtn();
    if (filter === 'own') {
        const inp = document.getElementById('ownAvatarSearchInput');
        if (inp) inp.value = '';
        refreshAvatars();
    } else if (filter === 'favorites') {
        updateFavAvatarGroupHeader();
        filterFavAvatars();
        if (favAvatarsData.length === 0) sendToCS({ action: 'vrcGetAvatars', filter: 'favorites' });
    } else if (filter === 'recent') {
        const inp = document.getElementById('recentAvatarSearchInput');
        if (inp) inp.value = '';
        filterRecentAvatars();
        sendToCS({ action: 'vrcGetRecentAvatars' });
    } else if (filter === 'rose') {
        if (roseDbLoaded) filterRoseDb();
        loadRoseDatabase();
    } else {
        document.getElementById('avatarSearchGrid').innerHTML = avatarSearchPrompt();
        setTimeout(() => document.getElementById('avatarSearchInput')?.focus(), 50);
    }
}

/* === Own Avatars === */
let _avOwnPage = 0;
let _avFavPage = 0;

let _avRecentPage = 0;
let _avRosePage = 0;

function setAvatarsViewMode(mode) {
    lvSetViewMode('avatars', mode);
    _avOwnPage = 0; _avFavPage = 0; _avRecentPage = 0; _avRosePage = 0;
    _avatarsSyncViewBtns();
    renderAvatarsListView();
}

function _avatarsSyncViewBtns() {
    const isList = lvViewMode('avatars') === 'list';
    document.getElementById('avatarViewList')?.classList.toggle('active', isList);
    if (isList) {
        document.getElementById('avatarGridLarge')?.classList.remove('active');
        document.getElementById('avatarGridSmall')?.classList.remove('active');
    } else {
        const compact = localStorage.getItem('vrcn_gridSize_avatars') === 'compact';
        document.getElementById('avatarGridLarge')?.classList.toggle('active', !compact);
        document.getElementById('avatarGridSmall')?.classList.toggle('active', compact);
    }
}

function renderAvatarsListView() {
    if (avatarFilter === 'favorites')   filterFavAvatars();
    else if (avatarFilter === 'recent') filterRecentAvatars();
    else if (avatarFilter === 'rose')   filterRoseDb();
    else if (avatarFilter === 'search') { if (avatarSearchResults.length) renderSearchGrid(); }
    else filterOwnAvatars();
}

function setAvatarsListPageSize(v) { lvSetPageSize('avatars', v, () => { _avOwnPage = 0; _avFavPage = 0; _avRecentPage = 0; _avRosePage = 0; renderAvatarsListView(); }); }
function avOwnGoPage(p) { if (p < 0) return; _avOwnPage = p; filterOwnAvatars(); document.getElementById('avatarGrid')?.closest('.lv-tab-scroll')?.scrollTo(0, 0); }
function avFavGoPage(p) { if (p < 0) return; _avFavPage = p; filterFavAvatars(); document.getElementById('favAvatarsGrid')?.closest('.lv-tab-scroll')?.scrollTo(0, 0); }
function avRecentGoPage(p) { if (p < 0) return; _avRecentPage = p; filterRecentAvatars(); document.getElementById('avatarRecentGrid')?.closest('.lv-tab-scroll')?.scrollTo(0, 0); }
function avRoseGoPage(p) { if (p < 0) return; _avRosePage = p; filterRoseDb(); document.getElementById('roseDbGrid')?.closest('.lv-tab-scroll')?.scrollTo(0, 0); }

const _AV_PERF_ORDER = ['excellent', 'good', 'medium', 'poor', 'verypoor'];
function _avPerfRank(v) {
    const k = String(v || '').toLowerCase().replace(/[^a-z]/g, '');
    const i = _AV_PERF_ORDER.indexOf(k);
    return i < 0 ? 99 : i;
}

function _alValue(a, field) {
    const p = _avPerfByPlatform(a);
    switch (field) {
        case 'name':    return (a.name || '').toLowerCase();
        case 'creator': return (a.authorName || '').toLowerCase();
        case 'status':  return (a.releaseStatus || '').toLowerCase();
        case 'pc':      return _avPerfRank(p.pc);
        case 'android': return _avPerfRank(p.quest);
        case 'ios':     return _avPerfRank(p.ios);
        case 'created': return Date.parse(a.created_at || '') || 0;
        case 'updated': return Date.parse(a.updated_at || '') || 0;
        case 'tags':    return (_avAuthorTags(a)[0] || '').toLowerCase();
        default:        return (a.name || '').toLowerCase();
    }
}

function _avAuthorTags(a) {
    return (a.tags || [])
        .filter(x => typeof x === 'string' && x.startsWith('author_tag_'))
        .map(x => x.replace('author_tag_', ''));
}

function _avListDate(value) {
    if (!value) return '';
    const d = new Date(value);
    return isNaN(d) ? String(value) : fmtShortDate(d);
}

function _avListTagsHtml(a) {
    const tags = _avAuthorTags(a);
    if (!tags.length) return '';
    const shown = tags.slice(0, 3).map(x => `<span class="vrcn-badge">${esc(x)}</span>`).join('');
    const more = tags.length > 3 ? `<span class="vrcn-badge">+${tags.length - 3}</span>` : '';
    return `<span class="av-list-tags">${shown}${more}</span>`;
}

function buildAvatarsListHtml(avatars, staticHeader) {
    let rows = '';
    avatars.forEach(a => {
        const aid = jsq(a.id || '');
        const p = _avPerfByPlatform(a);
        const isPub = (a.releaseStatus || '') === 'public';
        const statusBadge = a.releaseStatus
            ? `<span class="vrcn-badge ${isPub ? 'public' : 'private'}">${esc(isPub ? t('avatars.status.public', 'Public') : t('avatars.status.private', 'Private'))}</span>`
            : '';
        rows += tlTableRow('avatarsList', ` data-avid="${esc(a.id || '')}" onclick="openAvatarDetail('${aid}')"`, {
            icon:    `<td>${lvIcon(a.thumbnailImageUrl || a.imageUrl, a.name, true)}</td>`,
            name:    `<td class="lv-name">${esc(a.name || '')}</td>`,
            creator: `<td class="lv-sub">${esc(a.authorName || '')}</td>`,
            status:  `<td>${statusBadge}</td>`,
            pc:      `<td class="lv-perf">${avatarPerfIcon(p.pc, 20, 'PC')}</td>`,
            android: `<td class="lv-perf">${avatarPerfIcon(p.quest, 20, 'Android')}</td>`,
            ios:     `<td class="lv-perf">${avatarPerfIcon(p.ios, 20, 'iOS')}</td>`,
            created: `<td class="lv-sub">${esc(_avListDate(a.created_at))}</td>`,
            updated: `<td class="lv-sub">${esc(_avListDate(a.updated_at))}</td>`,
            tags:    `<td>${_avListTagsHtml(a)}</td>`,
        });
    });
    return `<div class="lv-scroll">${tlTableHtml('avatarsList', rows, staticHeader)}</div>`;
}

function _avatarsListPage(el, all, page, barId, pageFn, setPage) {
    const sorted = lvSort(all, 'avatarsList', _alValue);
    const size = lvPageSize('avatars');
    const totalPages = Math.ceil(sorted.length / size) || 1;
    let p = page;
    if (p >= totalPages) p = totalPages - 1;
    if (p < 0) p = 0;
    setPage(p);
    lvKeepScroll(el, () => {
        el.classList.remove('avatar-grid');
        el.innerHTML = buildAvatarsListHtml(sorted.slice(p * size, (p + 1) * size));
        lvEditDecorateList(el, 'avatars');
    });
    setPaginator(barId, lvPaginator('avatars', p, totalPages, pageFn, sorted.length, 'setAvatarsListPageSize'));
}

function filterOwnAvatars() {
    _avSyncEditBtn();
    const q = (document.getElementById('ownAvatarSearchInput')?.value || '').toLowerCase();
    const el = document.getElementById('avatarGrid');
    if (!el) return;
    if (!currentVrcUser) {
        el.innerHTML = avatarEmptyMessage('avatars.empty.login_prompt', 'Login to VRChat to see your avatars');
        setPaginator('avatarOwnPaginatorBar', '');
        return;
    }
    const filtered = (q
        ? avatarsData.filter(a => (a.name || '').toLowerCase().includes(q) || (a.authorName || '').toLowerCase().includes(q))
        : avatarsData).filter(_avPassesFilters);
    document.getElementById('avatarCount').textContent = filtered.length ? avatarCountText(filtered.length) : '';
    if (!filtered.length) {
        el.classList.add('avatar-grid');
        el.innerHTML = avatarEmptyMessage(q ? 'avatars.empty.no_match' : 'avatars.empty.none', q ? 'No avatars match your filter' : 'No avatars found');
        setPaginator('avatarOwnPaginatorBar', '');
        return;
    }
    if (lvViewMode('avatars') === 'list' && lvReady()) {
        _avatarsListPage(el, filtered, _avOwnPage, 'avatarOwnPaginatorBar', 'avOwnGoPage', p => { _avOwnPage = p; });
        if (_avEditMode) updateAvEditBar();
        return;
    }
    setPaginator('avatarOwnPaginatorBar', '');
    el.classList.add('avatar-grid');
    el.innerHTML = filtered.map(a => _avEditMode ? _renderFavAvCard(a) : renderAvatarCard(a, 'own')).join('');
    if (_avEditMode) updateAvEditBar();
}

function renderAvatarGrid() {
    const el = document.getElementById('avatarGrid');
    if (!el) return;
    if (!currentVrcUser) {
        el.innerHTML = avatarEmptyMessage('avatars.empty.login_prompt', 'Login to VRChat to see your avatars');
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
        el.classList.add('avatar-grid');
        el.innerHTML = avatarEmptyMessage('avatars.search.no_results', 'No results found');
        return;
    }
    const results = avatarSearchResults.filter(_avPassesFilters);
    document.getElementById('avatarCount').textContent = avatarResultCountText(results.length);
    if (results.length === 0) {
        el.classList.add('avatar-grid');
        el.innerHTML = avatarEmptyMessage('avatars.empty.no_match', 'No avatars match your filter');
        return;
    }
    if (lvViewMode('avatars') === 'list' && lvReady()) {
        el.classList.remove('avatar-grid');
        const more = avatarSearchHasMore
            ? `<div class="lv-more"><button class="vrcn-button" onclick="doAvatarSearch(true)">${esc(t('avatars.search.load_more', 'Load More'))}</button></div>`
            : '';
        el.innerHTML = buildAvatarsListHtml(results, true) + more;
        _checkAvatarsExist(avatarSearchResults.map(a => a.id).filter(Boolean));
        return;
    }
    el.classList.add('avatar-grid');
    let html = results.map(a => renderAvatarCard(a, 'search')).join('');
    if (avatarSearchHasMore) {
        html += `<div style="grid-column:1/-1;text-align:center;margin-top:6px;">
            <button class="vrcn-button" onclick="doAvatarSearch(true)">${t('avatars.search.load_more', 'Load More')}</button>
        </div>`;
    }
    el.innerHTML = html;
    // Check if avatars still exist on VRChat
    _checkAvatarsExist(avatarSearchResults.map(a => a.id).filter(Boolean));
}

const _deletedAvatarCache = new Set();

function _checkAvatarsExist(ids) {
    if (!ids.length) return;
    // Mark already-cached deleted avatars immediately
    const cached = ids.filter(id => _deletedAvatarCache.has(id));
    if (cached.length) _markDeletedAvatars(cached);
    // Only check uncached IDs via API
    const unchecked = ids.filter(id => !_deletedAvatarCache.has(id));
    if (unchecked.length) sendToCS({ action: 'vrcCheckAvatars', ids: unchecked });
}

function _markDeletedAvatars(deletedIds) {
    deletedIds.forEach(id => {
        _deletedAvatarCache.add(id);
        document.querySelectorAll(`.av-card[onclick*="'${id}'"]`).forEach(card => {
            if (card.dataset.deleted) return;
            card.dataset.deleted = '1';
            card.style.pointerEvents = 'none';
            card.style.opacity = '0.5';
            const thumb = card.querySelector('.cc-bg');
            if (thumb) {
                thumb.style.filter = 'grayscale(1) brightness(0.4)';
                const badge = document.createElement('span');
                badge.className = 'vrcn-badge';
                badge.style.cssText = 'position:absolute;top:50%;left:50%;transform:translate(-50%,-50%);z-index:2;background:rgba(0,0,0,.75);color:var(--err);font-size:11px;';
                badge.innerHTML = `<span class="msi" style="font-size:10px;">delete</span> ${t('avatars.labels.deleted', 'Deleted')}`;
                thumb.appendChild(badge);
            }
        });
        document.querySelectorAll(`.vrcn-mini-content[data-avatar-id="${id}"]`).forEach(card => {
            if (card.dataset.deleted) return;
            card.dataset.deleted = '1';
            card.style.pointerEvents = 'none';
            card.style.opacity = '0.5';
            const thumb = card.querySelector('.vrcn-mini-content-thumb');
            if (thumb) thumb.style.filter = 'grayscale(1) brightness(0.4)';
            const badges = card.querySelector('.vrcn-mini-content-badges');
            if (badges) {
                const badge = document.createElement('span');
                badge.className = 'vrcn-badge';
                badge.style.cssText = 'background:rgba(0,0,0,.75);color:var(--err);';
                badge.innerHTML = `<span class="msi" style="font-size:10px;">delete</span> ${t('avatars.labels.deleted', 'Deleted')}`;
                badges.prepend(badge);
            }
        });
    });
}

function _avPlatformBadges(a) {
    let hasPC, hasQuest, hasIos;
    if (a.compatibility && a.compatibility.length > 0) {
        hasPC    = a.compatibility.includes('pc') || a.compatibility.includes('standalonewindows');
        hasQuest = a.compatibility.includes('android');
        hasIos   = a.compatibility.includes('ios');
    } else {
        const real = (a.unityPackages || []).filter(p => p.variant !== 'impostor');
        hasPC    = real.some(p => p.platform === 'standalonewindows');
        hasQuest = real.some(p => p.platform === 'android');
        hasIos   = real.some(p => p.platform === 'ios');
    }
    if (!hasPC && !hasQuest && !hasIos) return '';
    return `<div style="display:flex;gap:3px;">${hasPC ? '<span class="vrcn-badge platform-pc">PC</span>' : ''}${hasQuest ? '<span class="vrcn-badge platform-quest">Quest</span>' : ''}${hasIos ? '<span class="vrcn-badge platform-ios">iOS</span>' : ''}</div>`;
}

/* === Publish / Platform filters (shared across avatar sub-tabs) === */
let avatarPublishFilter  = 'all';
let avatarPlatformFilter = 'all';

function _avPlatformInfo(a) {
    let hasPC = false, hasQuest = false, hasIos = false;
    if (a.compatibility && a.compatibility.length > 0) {
        hasPC    = a.compatibility.includes('pc') || a.compatibility.includes('standalonewindows');
        hasQuest = a.compatibility.includes('android');
        hasIos   = a.compatibility.includes('ios');
    } else if (a.unityPackages && a.unityPackages.length > 0) {
        const real = a.unityPackages.filter(p => p.variant !== 'impostor');
        hasPC    = real.some(p => p.platform === 'standalonewindows');
        hasQuest = real.some(p => p.platform === 'android');
        hasIos   = real.some(p => p.platform === 'ios');
    }
    return { hasPC, hasQuest, hasIos };
}

function _avPerfPretty(perf) {
    const key = String(perf || '').toLowerCase().replace(/[^a-z]/g, '');
    return { excellent: 'Excellent', good: 'Good', medium: 'Medium', poor: 'Poor', verypoor: 'Very Poor' }[key] || '';
}

function avatarPerfIcon(perf, size = 16, label = '') {
    const key = String(perf || '').toLowerCase().replace(/[^a-z]/g, '');
    if (!['excellent', 'good', 'medium', 'poor', 'verypoor'].includes(key)) return '';
    const pretty = _avPerfPretty(perf);
    const title = label ? `${label} - ${pretty}` : pretty;
    return `<img src="assets/Avatars/${key}.png" title="${esc(title)}" style="height:${size}px;width:auto;vertical-align:middle;">`;
}

function _avPerfValid(v) {
    const k = String(v || '').toLowerCase().replace(/[^a-z]/g, '');
    return ['excellent', 'good', 'medium', 'poor', 'verypoor'].includes(k) ? v : '';
}

function _avPerfByPlatform(a) {
    const perf = (a.performance && typeof a.performance === 'object') ? a.performance : null;
    const fromObj = (...keys) => {
        for (const k of keys) {
            const v = _avPerfValid(perf?.[k]);
            if (v) return v;
        }
        return '';
    };
    const real = (a.unityPackages || []).filter(p => p.variant !== 'impostor');
    const fromPkg = plat => real
        .filter(p => p.platform === plat)
        .map(p => _avPerfValid(p.performanceRating))
        .filter(Boolean)
        .sort((x, y) => _avPerfRank(x) - _avPerfRank(y))[0] || '';

    return {
        pc:    fromObj('pc', 'standalonewindows')  || fromPkg('standalonewindows'),
        quest: fromObj('quest', 'android')         || fromPkg('android'),
        ios:   fromObj('ios')                      || fromPkg('ios'),
    };
}

function _avPerfBadges(a) {
    const p = _avPerfByPlatform(a);
    const icons = [
        avatarPerfIcon(p.pc, 23, 'PC'),
        avatarPerfIcon(p.quest, 23, 'Android'),
        avatarPerfIcon(p.ios, 23, 'iOS'),
    ].filter(Boolean);
    return icons.length ? `<div class="cc-perf-top">${icons.join('')}</div>` : '';
}

function _avPlatformIcons(a) {
    const { hasPC, hasQuest, hasIos } = _avPlatformInfo(a);
    const ico = (has, img, label) => has ? `<img class="cc-plat-ico" src="assets/Avatars/${img}" title="${esc(label)}">` : '';
    return ico(hasPC, 'pc.png', 'PC') + ico(hasQuest, 'android.png', 'Android') + ico(hasIos, 'ios.png', 'iOS');
}

// An avatar passes when the data is unknown (lenient) or it matches the active filter.
function _avPassesFilters(a) {
    if (avatarPublishFilter !== 'all' && a.releaseStatus) {
        const isPub = a.releaseStatus === 'public';
        if (avatarPublishFilter === 'public'  && !isPub) return false;
        if (avatarPublishFilter === 'private' &&  isPub) return false;
    }
    if (avatarPlatformFilter !== 'all') {
        const { hasPC, hasQuest, hasIos } = _avPlatformInfo(a);
        if (hasPC || hasQuest || hasIos) {
            if (avatarPlatformFilter === 'pc'    && !hasPC)    return false;
            if (avatarPlatformFilter === 'quest' && !hasQuest) return false;
            if (avatarPlatformFilter === 'ios'   && !hasIos)   return false;
        }
    }
    return true;
}

function setAvPublishFilter(v) {
    avatarPublishFilter = v;
    document.querySelectorAll('select.av-publish-filter').forEach(s => { if (s.value !== v) { s.value = v; s._vnRefresh && s._vnRefresh(); } });
    _rerenderAvatarFilter();
}
function setAvPlatformFilter(v) {
    avatarPlatformFilter = v;
    document.querySelectorAll('select.av-platform-filter').forEach(s => { if (s.value !== v) { s.value = v; s._vnRefresh && s._vnRefresh(); } });
    _rerenderAvatarFilter();
}
function _rerenderAvatarFilter() {
    if      (avatarFilter === 'own')       filterOwnAvatars();
    else if (avatarFilter === 'favorites') filterFavAvatars();
    else if (avatarFilter === 'recent')    filterRecentAvatars();
    else if (avatarFilter === 'search')    renderSearchGrid();
    else if (avatarFilter === 'rose')      filterRoseDb();
}

function _avDbBadge(context, a) {
    if (context !== 'search') return '';
    const srcs = a.sources || [avatarSearchDb];
    const hasDb   = srcs.includes('avtrdb');
    const hasIcu  = srcs.includes('avtricu');
    const hasVrcn = srcs.includes('vrcn');
    if (!hasDb && !hasIcu && !hasVrcn) return '';
    return [
        hasDb   ? '<span class="vrcn-badge db-avtrdb">Avtrdb</span>'   : '',
        hasIcu  ? '<span class="vrcn-badge db-avtricu">Avtr.icu</span>' : '',
        hasVrcn ? '<span class="vrcn-badge db-vrcndb">VRCN</span>'      : '',
    ].join('');
}

function renderAvatarCard(a, context) {
    const thumb = a.thumbnailImageUrl || a.imageUrl || '';
    const isActive = a.id === currentAvatarId;
    const isPublic = context === 'search' || a.releaseStatus === 'public';
    const statusBadge = avatarStatusBadge(isPublic);
    const aid = jsq(a.id || '');
    const thumbStyle = thumb ? `background-image:url('${cssUrl(imgThumb(thumb, 256))}')` : '';
    return `<div class="vrcn-content-card av-card${isActive ? ' av-active' : ''}" onclick="selectAvatar('${aid}')">
        <div class="cc-bg" style="${thumbStyle}"></div>
        <div class="cc-scrim"></div>
        <div class="cc-badges-top">${_avPlatformIcons(a)}</div>${_avPerfBadges(a)}
        <div class="cc-badge-db">${_avDbBadge(context, a)}${statusBadge}</div>
        <div class="cc-content">
            <div class="cc-name">${esc(a.name || t('avatars.labels.unnamed', 'Unnamed'))}</div>
            <div class="cc-meta">${esc(a.authorName || '')}</div>
        </div>
    </div>`;
}

/* === Recently Used Avatars === */
let _recentAvatarsData = [];
function renderRecentAvatars(avatars) {
    _recentAvatarsData = Array.isArray(avatars) ? avatars : [];
    filterRecentAvatars();
}

function filterRecentAvatars() {
    const el = document.getElementById('avatarRecentGrid');
    if (!el) return;
    const q = (document.getElementById('recentAvatarSearchInput')?.value || '').toLowerCase();
    const list = (q
        ? _recentAvatarsData.filter(a => (a.name || '').toLowerCase().includes(q) || (a.authorName || '').toLowerCase().includes(q))
        : _recentAvatarsData).filter(_avPassesFilters);
    if (!list.length) {
        el.classList.add('avatar-grid');
        el.innerHTML = avatarEmptyMessage(q ? 'avatars.empty.no_match' : 'avatars.recent.empty', q ? 'No avatars match your filter' : 'No recently used avatars');
        setPaginator('avatarRecentPaginatorBar', '');
        return;
    }
    if (lvViewMode('avatars') === 'list' && lvReady()) {
        _avatarsListPage(el, list, _avRecentPage, 'avatarRecentPaginatorBar', 'avRecentGoPage', p => { _avRecentPage = p; });
        return;
    }
    setPaginator('avatarRecentPaginatorBar', '');
    el.classList.add('avatar-grid');
    el.innerHTML = list.map(a => renderAvatarCard(a, 'recent')).join('');
}

function avatarLookup(avatarId) {
    const pools = [
        typeof avatarsData !== 'undefined' ? avatarsData : null,
        typeof favAvatarsData !== 'undefined' ? favAvatarsData : null,
        typeof _recentAvatarsData !== 'undefined' ? _recentAvatarsData : null,
        typeof avatarSearchResults !== 'undefined' ? avatarSearchResults : null,
    ];
    for (const p of pools) {
        if (!Array.isArray(p)) continue;
        const hit = p.find(a => a.id === avatarId);
        if (hit) return hit;
    }
    if (typeof roseDbData !== 'undefined' && Array.isArray(roseDbData)) {
        const r = roseDbData.find(a => a.avatar_id === avatarId);
        if (r) return { id: r.avatar_id, name: r.avatar_name, authorName: r.author, thumbnailImageUrl: r._cachedThumb || r.avatar_image_url };
    }
    return null;
}

function selectAvatar(avatarId) {
    if (!avatarId || avatarId === currentAvatarId) return;
    const a = avatarLookup(avatarId) || {};
    showAvatarWearModal(avatarId, a.name || '', a.thumbnailImageUrl || a.imageUrl || '', a.authorName || '');
}

function avatarWearNow(avatarId) {
    if (!avatarId || avatarId === currentAvatarId) return;
    document.querySelectorAll('.av-card').forEach(c => {
        c.style.pointerEvents = 'none';
        c.style.opacity = '0.6';
    });
    sendToCS({ action: 'vrcSelectAvatar', avatarId: avatarId });
}

/* === Search === */
let avatarVrcnFt = false;

function _avVrcnContentCsv() {
    const menu = document.getElementById('avVrcnContentMenu');
    return menu ? [...menu.querySelectorAll('input:checked')].map(c => c.value).join(',') : '';
}
function _avUpdateVrcnContentCount() {
    const n = document.getElementById('avVrcnContentMenu')?.querySelectorAll('input:checked').length || 0;
    const cnt = document.getElementById('avVrcnContentCount');
    if (cnt) { cnt.textContent = n ? `(${n})` : ''; cnt.style.display = n ? '' : 'none'; }
}
function updateAvVrcnContent() { _avUpdateVrcnContentCount(); if (avatarSearchQuery) doAvatarSearch(); }
function toggleAvVrcnFt() {
    avatarVrcnFt = !avatarVrcnFt;
    document.getElementById('avVrcnFtBtn')?.classList.toggle('active', avatarVrcnFt);
    if (avatarSearchQuery) doAvatarSearch();
}
function toggleAvVrcnContentMenu() {
    const menu = document.getElementById('avVrcnContentMenu');
    if (!menu) return;
    const open = menu.style.display !== 'none';
    menu.style.display = open ? 'none' : 'block';
    if (!open) setTimeout(() => document.addEventListener('mousedown', _avVrcnContentOutside), 0);
}
function _avVrcnContentOutside(e) {
    const wrap = document.getElementById('avVrcnContentWrap');
    if (!wrap || !wrap.contains(e.target)) {
        const m = document.getElementById('avVrcnContentMenu'); if (m) m.style.display = 'none';
        document.removeEventListener('mousedown', _avVrcnContentOutside);
    }
}
function _avUpdateVrcnFilterVisibility() {
    const show = avatarSearchDb === 'vrcn';
    const perf = document.getElementById('avVrcnPerf');
    if (perf) (perf._vnSelect ? perf.parentNode : perf).style.display = show ? '' : 'none';
    const ftBtn = document.getElementById('avVrcnFtBtn');
    if (ftBtn) ftBtn.style.display = show ? '' : 'none';
    const cWrap = document.getElementById('avVrcnContentWrap');
    if (cWrap) cWrap.style.display = show ? '' : 'none';
}
function _avResetVrcnFilters() {
    avatarVrcnFt = false;
    document.getElementById('avVrcnFtBtn')?.classList.remove('active');
    const perfSel = document.getElementById('avVrcnPerf');
    if (perfSel) { perfSel.value = ''; perfSel._vnRefresh && perfSel._vnRefresh(); }
    document.getElementById('avVrcnContentMenu')?.querySelectorAll('input:checked').forEach(c => { c.checked = false; });
    _avUpdateVrcnContentCount();
}

function setAvatarSearchDb(db) {
    avatarSearchDb = db;
    avatarSearchPage = 0;
    avatarSearchResults = [];
    avatarSearchQuery = '';
    avatarSearchHasMore = false;
    _avIcuBuffer = [];
    _avIcuBufferCursor = 0;
    _avIcuFetchHasMore = false;
    _avResetVrcnFilters();
    _avUpdateVrcnFilterVisibility();
    const grid = document.getElementById('avatarSearchGrid');
    if (grid) grid.innerHTML = avatarSearchPrompt();
    document.getElementById('avatarCount').textContent = '';
}

function doAvatarSearch(loadMore) {
    const q = document.getElementById('avatarSearchInput').value.trim();
    if (!q) return;
    if (!loadMore) {
        avatarSearchPage = 0;
        avatarSearchResults = [];
        avatarSearchQuery = q;
        avatarSearchHasMore = false;
        _avIcuBuffer = [];
        _avIcuBufferCursor = 0;
        _avIcuFetchHasMore = false;
        document.getElementById('avatarSearchGrid').classList.add('avatar-grid');
        document.getElementById('avatarSearchGrid').innerHTML = sk('avatar', 6);
    } else if (avatarSearchDb === 'avtricu' && _avIcuBufferCursor < _avIcuBuffer.length) {
        const slice = _avIcuBuffer.slice(_avIcuBufferCursor, _avIcuBufferCursor + 20);
        _avIcuBufferCursor += slice.length;
        avatarSearchResults = [...avatarSearchResults, ...slice];
        avatarSearchHasMore = _avIcuBufferCursor < _avIcuBuffer.length || _avIcuFetchHasMore;
        renderSearchGrid();
        return;
    } else {
        avatarSearchPage++;
    }
    sendToCS({ action: 'vrcSearchAvatars', query: avatarSearchQuery, page: avatarSearchPage, db: avatarSearchDb,
        perf: document.getElementById('avVrcnPerf')?.value || '', content: _avVrcnContentCsv(), ft: avatarVrcnFt });
}

/* === Favorites: group dropdown + header === */
function _avGroupOptionLabel(g) {
    const count    = favAvatarsData.filter(a => a.favoriteGroup === g.name).length;
    const cap      = g.capacity || 25;
    const marker   = (!isLocalFavGroup(g) && g.name !== 'avatars1') ? ' [VRC+]' : '';
    return `${esc(g.displayName || g.name)} ${count}/${cap}${marker}`;
}

function renderFavAvatars(payload) {
    const refreshBtn = document.getElementById('favAvatarsRefreshBtn');
    if (refreshBtn) { refreshBtn.disabled = false; const ico = refreshBtn.querySelector('.msi'); if (ico) ico.textContent = 'refresh'; }

    const avatars = payload?.avatars || [];
    const groups  = payload?.groups  || [];
    favAvatarsData  = avatars;
    favAvatarGroups = groups;

    if (_pendingFavDeepLink && favAvatarGroups.length > 0) {
        const pending = _pendingFavDeepLink;
        _pendingFavDeepLink = null;
        deepLinkFavoriteAvatar(pending.avatarId, pending.slot);
    }

    const sel = document.getElementById('favAvatarGroupFilter');
    if (sel) {
        const prev = favAvatarGroupFilter;
        sel.innerHTML = `<option value="">${t('avatars.favorites.group.all', 'All Favorites')}</option>` +
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
    const header  = document.getElementById('favAvatarGroupHeader');
    const delBtn  = document.getElementById('favAvatarGroupDeleteBtn');
    if (!header) return;
    if (!favAvatarGroupFilter) {
        if (delBtn) delBtn.style.display = 'none';
    } else {
        const g = favAvatarGroups.find(x => x.name === favAvatarGroupFilter);
        if (delBtn) delBtn.style.display = isLocalFavGroup(g) ? '' : 'none';
    }
    const anyVisible = [delBtn].some(el => el && el.style.display !== 'none');
    header.style.display = anyVisible ? 'flex' : 'none';
}


function cancelEditAvatarGroupName() {
    updateFavAvatarGroupHeader();
    const row = document.getElementById('favAvatarGroupRenameRow');
    if (row) row.style.display = 'none';
    const saveBtn = document.querySelector('#favAvatarGroupRenameRow .vrcn-btn-primary');
    if (saveBtn) { saveBtn.disabled = false; saveBtn.textContent = t('common.save', 'Save'); }
}

function saveAvatarGroupName() {
    const g = favAvatarGroups.find(x => x.name === favAvatarGroupFilter);
    if (!g) return;
    const input = document.getElementById('favAvatarGroupNameInput');
    const newName = (input?.value || '').trim();
    if (!newName) return;
    const saveBtn = document.querySelector('#favAvatarGroupRenameRow .vrcn-btn-primary');
    if (saveBtn) { saveBtn.disabled = true; saveBtn.textContent = t('common.saving', 'Saving...'); }
    sendToCS({ action: 'vrcUpdateFavoriteGroup', groupType: g.type, groupName: g.name, displayName: newName });
}

function _avGroupHeaderHtml(g, count, first) {
    const cap = g.capacity || 25;
    return `<div class="fav-group-header${first ? ' fav-group-header-first' : ''}">
        ${_avGroupTitleHtml(g)}
        ${favGroupBadge(g)}
        <span class="fav-group-count">${count}/${cap}</span>
    </div>`;
}

function _avGroupTitleHtml(g) {
    const disp = g.displayName || g.name;
    if (!_avEditMode) return `<span class="topbar-title">${esc(disp)}</span>`;
    return `<span class="fav-group-name-edit">
        <input class="vrcn-edit-field fav-group-name-input" maxlength="64" value="${esc(disp)}" data-group="${esc(g.name)}" data-type="${esc(g.type || 'avatar')}" data-orig="${esc(disp)}" oninput="avOnGroupNameInput(this)" onclick="event.stopPropagation()">
        <span class="fav-group-name-actions" style="display:none;">
            <button class="vrcn-button vrcn-btn-primary" onclick="avSaveGroupName(this)">${t('common.save', 'Save')}</button>
            <button class="vrcn-button" onclick="avCancelGroupName(this)">${t('common.cancel', 'Cancel')}</button>
        </span>
    </span>`;
}

function avOnGroupNameInput(inp) {
    const actions = inp.closest('.fav-group-name-edit')?.querySelector('.fav-group-name-actions');
    if (!actions) return;
    const v = inp.value.trim();
    actions.style.display = (v && v !== inp.dataset.orig) ? 'inline-flex' : 'none';
}

function avSaveGroupName(btn) {
    const inp = btn.closest('.fav-group-name-edit')?.querySelector('.fav-group-name-input');
    if (!inp) return;
    const newName = inp.value.trim();
    if (!newName || newName === inp.dataset.orig) return;
    btn.disabled = true;
    sendToCS({ action: 'vrcUpdateFavoriteGroup', groupType: inp.dataset.type, groupName: inp.dataset.group, displayName: newName });
}

function avCancelGroupName(btn) {
    const wrap = btn.closest('.fav-group-name-edit');
    const inp = wrap?.querySelector('.fav-group-name-input');
    if (inp) inp.value = inp.dataset.orig;
    const actions = wrap?.querySelector('.fav-group-name-actions');
    if (actions) actions.style.display = 'none';
}

function _renderFavAvCard(a) {
    const thumb = a.thumbnailImageUrl || a.imageUrl || '';
    const thumbStyle = thumb ? `background-image:url('${cssUrl(imgThumb(thumb, 256))}')` : '';
    const isActive = a.id === currentAvatarId;
    const aid = jsq(a.id || '');
    const isPublic = a.releaseStatus === 'public';
    const statusBadge = avatarStatusBadge(isPublic);
    if (_avEditMode) {
        const isSelected = _avEditSelected.has(a.id);
        const checkIcon = isSelected
            ? `<span class="msi" style="font-size:22px;color:var(--accent);">check_circle</span>`
            : `<span class="msi" style="font-size:22px;color:rgba(255,255,255,0.7);">radio_button_unchecked</span>`;
        return `<div class="vrcn-content-card av-card${isActive ? ' av-active' : ''}" data-avid="${esc(a.id)}" onclick="toggleAvEditSelect('${aid}',this)" style="user-select:none;">
            <div class="cc-bg" style="${thumbStyle}"></div>
            <div class="cc-scrim"></div>
            <div class="cc-badges-top">${_avPlatformIcons(a)}</div>${_avPerfBadges(a)}
            <div class="cc-badge-db">${statusBadge}</div>
            <div class="wd-edit-check">${checkIcon}</div>
            <div class="cc-content">
                <div class="cc-name">${esc(a.name || t('avatars.labels.unnamed', 'Unnamed'))}</div>
                <div class="cc-meta">${esc(a.authorName || '')}</div>
            </div>
            ${isSelected ? '<div class="wd-edit-sel-border"></div>' : ''}
        </div>`;
    }
    return `<div class="vrcn-content-card av-card${isActive ? ' av-active' : ''}" onclick="selectAvatar('${aid}')">
        <div class="cc-bg" style="${thumbStyle}"></div>
        <div class="cc-scrim"></div>
        <div class="cc-badges-top">${_avPlatformIcons(a)}</div>${_avPerfBadges(a)}
        <div class="cc-badge-db">${statusBadge}</div>
        <div class="cc-content">
            <div class="cc-name">${esc(a.name || t('avatars.labels.unnamed', 'Unnamed'))}</div>
            <div class="cc-meta">${esc(a.authorName || '')}</div>
        </div>
    </div>`;
}

function filterFavAvatars() {
    const q = (document.getElementById('favAvatarSearchInput')?.value || '').toLowerCase();
    let filtered = favAvatarsData;
    if (favAvatarGroupFilter) filtered = filtered.filter(a => a.favoriteGroup === favAvatarGroupFilter);
    if (q) filtered = filtered.filter(a => (a.name || '').toLowerCase().includes(q) || (a.authorName || '').toLowerCase().includes(q));
    filtered = filtered.filter(_avPassesFilters);
    const el = document.getElementById('favAvatarsGrid');
    if (!el) return;
    if (!filtered.length) {
        el.classList.add('avatar-grid');
        el.innerHTML = avatarEmptyMessage(
            q || favAvatarGroupFilter ? 'avatars.favorites.no_match' : 'avatars.favorites.empty',
            q || favAvatarGroupFilter ? 'No favorites match your filter' : 'No favorite avatars found'
        );
        setPaginator('avatarFavPaginatorBar', '');
        if (_avEditMode) updateAvEditBar();
        return;
    }
    if (lvViewMode('avatars') === 'list' && lvReady()) {
        _avatarsListPage(el, filtered, _avFavPage, 'avatarFavPaginatorBar', 'avFavGoPage', p => { _avFavPage = p; });
        return;
    }
    setPaginator('avatarFavPaginatorBar', '');
    el.classList.add('avatar-grid');
    if (!favAvatarGroupFilter && favAvatarGroups.length > 1) {
        let html = '';
        let first = true;
        favAvatarGroups.forEach(g => {
            const groupAvatars = filtered.filter(a => a.favoriteGroup === g.name);
            if (!groupAvatars.length && !_avEditMode) return;
            html += _avGroupHeaderHtml(g, groupAvatars.length, first);
            html += groupAvatars.map(a => _renderFavAvCard(a)).join('');
            first = false;
        });
        el.innerHTML = html;
    } else {
        const selected = favAvatarGroupFilter
            ? favAvatarGroups.find(x => x.name === favAvatarGroupFilter)
            : null;
        const head = selected ? _avGroupHeaderHtml(selected, filtered.length, true) : '';
        el.innerHTML = head + filtered.map(a => _renderFavAvCard(a)).join('');
    }
    if (_avEditMode) updateAvEditBar();
}

/* === Avatar Edit Mode === */
function toggleAvEditMode() {
    if (_avEditMode) { exitAvEditMode(); return; }
    _avEditMode = true;
    _avEditSelected = new Set();
    const btn = document.getElementById('avatarEditModeBtn');
    if (btn) { btn.innerHTML = `<span class="msi" style="font-size:16px;">check</span> <span>${t('avatars.edit.done', 'Done')}</span>`; btn.classList.add('active'); }
    const bar = document.getElementById('avatarEditBar');
    if (bar) bar.style.display = 'flex';
    _avEditRerender();
}

function exitAvEditMode() {
    _avEditMode = false;
    _avEditSelected = new Set();
    const btn = document.getElementById('avatarEditModeBtn');
    if (btn) { btn.innerHTML = `<span class="msi" style="font-size:16px;">edit</span> <span>${t('avatars.edit.button', 'Edit')}</span>`; btn.classList.remove('active'); }
    const bar = document.getElementById('avatarEditBar');
    if (bar) bar.style.display = 'none';
    ['avatarEditMovePicker', 'avatarEditAddFavPicker'].forEach(id => {
        const picker = document.getElementById(id);
        if (picker) { picker.style.display = 'none'; picker.innerHTML = ''; }
    });
    avatarCancelCreateLocalGroup();
    _avEditRerender();
}

/* === Local Groups (Avatars) === */
function avatarLocalGroupCount() {
    return (typeof favAvatarGroups !== 'undefined' ? favAvatarGroups : []).filter(isLocalFavGroup).length;
}

function avatarShowCreateLocalGroup(btn) {
    const panel = document.getElementById('avatarCreateLocalPanel');
    if (!panel) return;
    if (panel.style.display === 'block') { avatarCancelCreateLocalGroup(); return; }
    if (avatarLocalGroupCount() >= 100) { showToast(false, localFavErrorText('group_limit')); return; }
    panel.style.display = 'block';
    const input = document.getElementById('avatarCreateLocalInput');
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

function avatarCancelCreateLocalGroup() {
    const panel = document.getElementById('avatarCreateLocalPanel');
    if (panel) panel.style.display = 'none';
}

function avatarSaveLocalGroup() {
    const input = document.getElementById('avatarCreateLocalInput');
    const name = (input?.value || '').trim();
    if (!name) { showToast(false, localFavErrorText('empty_name')); return; }
    sendToCS({ action: 'vrcCreateLocalGroup', kind: 'avatar', displayName: name });
    avatarCancelCreateLocalGroup();
}

function deleteCurrentLocalAvatarGroup(btn) {
    const g = favAvatarGroups.find(x => x.name === favAvatarGroupFilter);
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
    sendToCS({ action: 'vrcDeleteLocalGroup', kind: 'avatar', groupName: g.name });
    favAvatarGroupFilter = '';
}

function toggleAvEditSelect(id, el) {
    if (_avEditSelected.has(id)) {
        _avEditSelected.delete(id);
        const chk = el?.querySelector('.wd-edit-check .msi');
        if (chk) { chk.textContent = 'radio_button_unchecked'; chk.style.color = 'rgba(255,255,255,0.7)'; }
        el?.querySelector('.wd-edit-sel-border')?.remove();
    } else {
        _avEditSelected.add(id);
        const chk = el?.querySelector('.wd-edit-check .msi');
        if (chk) { chk.textContent = 'check_circle'; chk.style.color = 'var(--accent)'; }
        if (el && !el.querySelector('.wd-edit-sel-border')) {
            el.insertAdjacentHTML('beforeend', '<div class="wd-edit-sel-border"></div>');
        }
    }
    updateAvEditBar();
}

function avEditSelectAll() {
    const filtered = _avEditVisibleList();
    const allSelected = filtered.length > 0 && filtered.every(a => _avEditSelected.has(a.id));
    if (allSelected) filtered.forEach(a => _avEditSelected.delete(a.id));
    else filtered.forEach(a => _avEditSelected.add(a.id));
    _avEditRerender();
}

function updateAvEditBar() {
    const count = _avEditSelected.size;
    const countEl = document.getElementById('avatarEditCount');
    if (countEl) countEl.textContent = tf('avatars.edit.selected', { count }, '{count} selected');
    const selectAllBtn = document.getElementById('avatarEditSelectAllBtn');
    if (selectAllBtn) {
        const filtered = _avEditVisibleList();
        const allSel = filtered.length > 0 && filtered.every(a => _avEditSelected.has(a.id));
        selectAllBtn.textContent = allSel ? t('avatars.edit.deselect_all', 'Deselect All') : t('avatars.edit.select_all', 'Select All');
    }
    document.querySelectorAll('.av-edit-action').forEach(b => b.disabled = count === 0);
    _avEditSyncButtons();
}

function avEditShowMoveMenu(btn) {
    if (_avEditSelected.size === 0) return;
    const picker = document.getElementById('avatarEditMovePicker');
    if (!picker) return;
    if (picker.style.display === 'block') { picker.style.display = 'none'; picker.innerHTML = ''; return; }
    const groups = (typeof favAvatarGroups !== 'undefined') ? favAvatarGroups : [];
    picker.innerHTML = groups.map(g => {
        const count = favAvatarsData.filter(fw => fw.favoriteGroup === g.name).length;
        const gn = jsq(g.name), gt = jsq(g.type);
        return `<div class="vn-select-option" onclick="avEditMoveSelected('${gn}','${gt}')">
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

function avEditMoveSelected(groupName, groupType) {
    if (_avEditSelected.size === 0) return;
    const picker = document.getElementById('avatarEditMovePicker');
    if (picker) { picker.style.display = 'none'; picker.innerHTML = ''; }
    const toMove = [..._avEditSelected];
    toMove.forEach(avatarId => {
        const entry = favAvatarsData.find(a => a.id === avatarId);
        if (entry && entry.favoriteGroup !== groupName) {
            sendToCS({ action: 'vrcAddAvatarFavorite', avatarId, groupName, groupType, oldFvrtId: entry.favoriteId || '' });
        }
    });
    exitAvEditMode();
}

function avEditRemoveSelected() {
    if (_avEditSelected.size === 0) return;
    const toRemove = [..._avEditSelected];
    toRemove.forEach(avatarId => {
        const entry = favAvatarsData.find(a => a.id === avatarId);
        if (entry) sendToCS({ action: 'vrcRemoveAvatarFavorite', avatarId, fvrtId: entry.favoriteId });
    });
    exitAvEditMode();
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
        document.getElementById('avFavPickerList').innerHTML = `<div style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);padding:8px 0;">${t('avatars.favorites.loading_groups', 'Loading groups...')}</div>`;
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
    document.removeEventListener('click', _avPickerOutside);
}

function renderAvFavPickerList(avatarId) {
    const list = document.getElementById('avFavPickerList');
    if (!list) return;
    if (favAvatarGroups.length === 0) return;

    const currentEntry = favAvatarsData.find(f => f.id === avatarId);
    const currentGroup = currentEntry?.favoriteGroup || '';

    list.innerHTML = favAvatarGroups.map(g => {
        const count = favAvatarsData.filter(f => f.favoriteGroup === g.name).length;
        const isCurrent = g.name === currentGroup;
        const vrcBadge = favGroupBadge(g);
        const check = isCurrent
            ? `<span class="msi" style="color:var(--accent);font-size:18px;flex-shrink:0;">check_circle</span>`
            : '';
        const gn = jsq(g.name), gt = jsq(g.type), aid = jsq(avatarId);
        const oldFvrt = isCurrent ? jsq(currentEntry?.favoriteId || '') : '';
        return `<div class="fd-group-card ci-group-card${isCurrent ? ' ci-group-selected' : ''}"
            onclick="addAvatarToFavGroup('${aid}','${gn}','${gt}','${oldFvrt}',this)" style="cursor:pointer;">
            <div style="flex:1;min-width:0;">
                <div style="display:flex;align-items:center;gap:5px;flex-wrap:wrap;">
                    <span style="font-size:calc(12px + var(--fs-off, 0px));font-weight:600;color:var(--tx0);">${esc(g.displayName || g.name)}</span>
                    ${vrcBadge}
                </div>
                <div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx2);margin-top:1px;">${tf('avatars.favorites.group_count', { count, capacity: g.capacity || 25 }, '{count}/{capacity} slots')}</div>
            </div>
            ${check}
        </div>`;
    }).join('');
}

let _pendingFavDeepLink = null;

function deepLinkFavoriteAvatar(avatarId, slot) {
    if (!avatarId || !slot) return;
    if (!favAvatarGroups || favAvatarGroups.length === 0) {
        _pendingFavDeepLink = { avatarId, slot };
        sendToCS({ action: 'vrcGetAvatars', filter: 'favorites' });
        return;
    }
    const groupName = 'avatars' + slot;
    const group = favAvatarGroups.find(g => g.name === groupName);
    if (!group) {
        showToast(false, tf('avatars.favorites.group_missing', { group: groupName }, 'Favorite group {group} not available'));
        return;
    }
    const entry = favAvatarsData.find(f => f.id === avatarId);
    sendToCS({
        action: 'vrcAddAvatarFavorite',
        avatarId,
        groupName,
        groupType: group.type,
        oldFvrtId: entry?.favoriteGroup === groupName ? '' : (entry?.favoriteId || ''),
    });
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
        const group = favAvatarGroups.find(g => g.name === data.groupName);
        const groupLabel = group?.displayName || data.groupName;
        const entry = favAvatarsData.find(f => f.id === data.avatarId);
        const avatarName = entry?.name || '';
        showToast(true, avatarName
            ? tf('avatars.favorites.toast.saved_to_group.named', { avatar: avatarName, group: groupLabel }, '"{avatar}" saved to {group}')
            : tf('avatars.favorites.toast.saved_to_group.unnamed', { group: groupLabel }, 'Saved to {group}'));
        closeAvFavPicker();
        // Re-render star on current card
        if (avatarFilter === 'own') renderAvatarGrid();
        else if (avatarFilter === 'search') renderSearchGrid();
        else if (avatarFilter === 'favorites') filterFavAvatars();
        else if (avatarFilter === 'rose') filterRoseDb();
        if (typeof updateAvatarModalFavBtn === 'function') updateAvatarModalFavBtn(data.avatarId);
        _scheduleAvFavRefresh();
    } else {
        if (data.error) showToast(false, localFavErrorText(data.error));
        const list = document.getElementById('avFavPickerList');
        if (list) {
            list.innerHTML = `<div style="font-size:calc(11px + var(--fs-off, 0px));color:var(--err,#e55);padding:6px 0;">${t('avatars.favorites.failed_prefix', 'Failed:')} ${esc(data.error || t('avatars.favorites.try_again', 'Try again'))}</div>`;
            setTimeout(() => { if (_avFavPickerAvatarId) renderAvFavPickerList(_avFavPickerAvatarId); }, 1800);
        }
    }
}

function onAvatarUnfavoriteResult(data) {
    if (data.ok) {
        const removed = favAvatarsData.find(f => f.id === data.avatarId);
        const avatarName = removed?.name || '';
        showToast(true, avatarName
            ? tf('avatars.favorites.toast.removed.named', { avatar: avatarName }, '"{avatar}" removed from favorites')
            : t('avatars.favorites.toast.removed', 'Removed from favorites'));
        favAvatarsData = favAvatarsData.filter(f => f.id !== data.avatarId);
        if (avatarFilter === 'favorites') filterFavAvatars();
        else if (avatarFilter === 'own') renderAvatarGrid();
        else if (avatarFilter === 'search') renderSearchGrid();
        else if (avatarFilter === 'rose') filterRoseDb();
        if (typeof updateAvatarModalFavBtn === 'function') updateAvatarModalFavBtn(data.avatarId);
        _scheduleAvFavRefresh();
    }
}

function onAvatarFavGroupsLoaded(groups) {
    favAvatarGroups = groups;
    // Update group dropdown if favorites tab is open
    const sel = document.getElementById('favAvatarGroupFilter');
    if (sel) {
        sel.innerHTML = `<option value="">${t('avatars.favorites.group.all', 'All Favorites')}</option>` +
            groups.map(g => `<option value="${esc(g.name)}">${_avGroupOptionLabel(g)}</option>`).join('');
        sel.value = favAvatarGroupFilter;
        if (sel._vnRefresh) sel._vnRefresh();
    }
    // Re-render picker if open
    if (_avFavPickerAvatarId) renderAvFavPickerList(_avFavPickerAvatarId);
}

// Handles rename result (shared with worlds via vrcFavoriteGroupUpdated)
function onAvatarFavoriteGroupUpdated(data) {
    if (!data.ok) { if (_avEditMode) filterFavAvatars(); else cancelEditAvatarGroupName(); return; }
    const g = favAvatarGroups.find(x => x.name === data.groupName);
    if (g) g.displayName = data.displayName;
    const sel = document.getElementById('favAvatarGroupFilter');
    if (sel) {
        const opt = [...sel.options].find(o => o.value === data.groupName);
        if (opt && g) opt.textContent = _avGroupOptionLabel(g);
        if (sel._vnRefresh) sel._vnRefresh();
    }
    if (_avEditMode) { filterFavAvatars(); return; }
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
    if (grid) { grid.classList.add('avatar-grid'); grid.innerHTML = sk('avatar', 6); }
    if (btn)  { btn.disabled = true; btn.querySelector('.msi').textContent = 'hourglass_empty'; }

    fetch('https://gist.githubusercontent.com/TheZiver/bb99f9facb8d14fd607dbb79e9a99d83/raw')
        .then(r => r.json())
        .then(data => {
            roseDbData   = data.community_avatars || [];
            roseDbLoaded = true;
            sendToCS({ action: 'vrcCacheAvatarBatch', avatars: roseDbData.map(a => ({ id: a.avatar_id, imageUrl: a.avatar_image_url })) });
            filterRoseDb();
        })
        .catch(() => {
            if (grid) grid.innerHTML = avatarEmptyMessage('avatars.rose.failed', 'Failed to load Rose Database. Check your connection.');
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
    document.getElementById('avatarCount').textContent = list.length ? avatarCountText(list.length) : '';
}

function _roseToAvatar(a) {
    const d = a._detail || {};
    const p = d.performance || null;
    const compat = [];
    if (p) {
        if (p.pc)    compat.push('standalonewindows');
        if (p.quest) compat.push('android');
        if (p.ios)   compat.push('ios');
    }
    return {
        id: a.avatar_id || '',
        name: a.avatar_name || '',
        authorName: a.author || d.authorName || '',
        thumbnailImageUrl: a._cachedThumb || a.avatar_image_url || '',
        releaseStatus: d.releaseStatus || 'public',
        performance: p || undefined,
        compatibility: compat.length ? compat : undefined,
        created_at: d.created_at || a.created_at || '',
        updated_at: d.updated_at || a.updated_at || '',
        tags: d.tags || (Array.isArray(a.tags) ? a.tags.map(x => 'author_tag_' + x) : []),
    };
}

function renderRoseGrid(list) {
    const grid = document.getElementById('roseDbGrid');
    if (!grid) return;
    if (!list || list.length === 0) {
        grid.classList.add('avatar-grid');
        grid.innerHTML = avatarEmptyMessage('avatars.empty.none', 'No avatars found');
        setPaginator('avatarRosePaginatorBar', '');
        return;
    }
    if (lvViewMode('avatars') === 'list' && lvReady()) {
        _avatarsListPage(grid, list.map(_roseToAvatar), _avRosePage, 'avatarRosePaginatorBar', 'avRoseGoPage', p => { _avRosePage = p; });
        return;
    }
    setPaginator('avatarRosePaginatorBar', '');
    grid.classList.add('avatar-grid');
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

function _avApplyDetails(list, keyField, details) {
    let changed = false;
    (list || []).forEach(a => {
        const d = details[a[keyField]];
        if (!d) return;
        if (d.authorName && !a.authorName) { a.authorName = d.authorName; changed = true; }
        if (d.releaseStatus && !a.releaseStatus) { a.releaseStatus = d.releaseStatus; changed = true; }
        if (d.created_at && a.created_at !== d.created_at) { a.created_at = d.created_at; changed = true; }
        if (d.updated_at && a.updated_at !== d.updated_at) { a.updated_at = d.updated_at; changed = true; }
        if (Array.isArray(d.tags) && d.tags.length && !(a.tags || []).length) { a.tags = d.tags; changed = true; }
        const p = d.performance;
        if (p && (p.pc || p.quest || p.ios)) {
            const cur = a.performance || {};
            if (cur.pc !== p.pc || cur.quest !== p.quest || cur.ios !== p.ios) {
                a.performance = { pc: p.pc || cur.pc || '', quest: p.quest || cur.quest || '', ios: p.ios || cur.ios || '' };
                changed = true;
            }
        }
    });
    return changed;
}

function _avApplyRoseDetails(details) {
    if (typeof roseDbData === 'undefined' || !Array.isArray(roseDbData)) return false;
    let changed = false;
    roseDbData.forEach(a => {
        const d = details[a.avatar_id];
        if (!d) return;
        const cur = a._detail || {};
        if (JSON.stringify(cur) === JSON.stringify(d)) return;
        a._detail = d;
        changed = true;
    });
    return changed;
}

function onAvatarDetailsBatch(details) {
    if (!details) return;
    let changed = false;
    if (typeof avatarSearchResults !== 'undefined') changed = _avApplyDetails(avatarSearchResults, 'id', details) || changed;
    if (typeof _recentAvatarsData !== 'undefined')  changed = _avApplyDetails(_recentAvatarsData, 'id', details) || changed;
    if (typeof favAvatarsData !== 'undefined')      changed = _avApplyDetails(favAvatarsData, 'id', details) || changed;
    if (typeof avatarsData !== 'undefined')         changed = _avApplyDetails(avatarsData, 'id', details) || changed;
    changed = _avApplyRoseDetails(details) || changed;
    if (!changed) return;
    const tab = document.getElementById('tab4');
    if (!tab || !tab.classList.contains('active')) return;
    const gridId = { search: 'avatarSearchGrid', recent: 'avatarRecentGrid', favorites: 'favAvatarsGrid', own: 'avatarGrid', rose: 'roseDbGrid' }[avatarFilter];
    lvKeepScroll(document.getElementById(gridId), () => renderAvatarsListView());
}

function onAvatarDetailLive(a) {
    if (!a || !a.id) return;
    const perf = { pc: a.pcPerf || '', quest: a.questPerf || '', ios: a.iosPerf || '' };
    const hasMeta = !!(a.created_at || a.updated_at || (a.tags || []).length);
    if (!perf.pc && !perf.quest && !perf.ios && !a.authorName && !hasMeta) return;
    onAvatarDetailsBatch({
        [a.id]: {
            authorName: a.authorName || '',
            releaseStatus: a.releaseStatus || '',
            created_at: a.created_at || '',
            updated_at: a.updated_at || '',
            tags: a.tags || [],
            performance: perf,
        },
    });
}

function onRoseDbBatchDetails(details) {
    if (!details) return;
    if (_avApplyRoseDetails(details) && avatarFilter === 'rose') {
        lvKeepScroll(document.getElementById('roseDbGrid'), () => filterRoseDb());
    }
}

function onRoseDbBatchCached(mapping) {
    roseDbData.forEach(a => {
        const url = mapping[a.avatar_id];
        if (url) a._cachedThumb = url;
    });
    if (avatarFilter === 'rose') filterRoseDb();
}

function renderRoseAvatarCard(a) {
    const thumb  = a._cachedThumb || a.avatar_image_url || '';
    const aid    = jsq(a.avatar_id || '');
    const isFav  = favAvatarsData.some(f => f.id === a.avatar_id);
    const thumbStyle = thumb ? `background-image:url('${cssUrl(imgThumb(thumb, 256))}')` : '';

    // Sort tags in defined order, unknown tags appended at end
    const rawTags  = (a.tags || []);
    const sorted   = [
        ...ROSE_TAG_ORDER.filter(k => rawTags.some(t => t.toUpperCase().replace(/\s+/g,'_') === k)),
        ...rawTags.filter(t => !ROSE_TAG_ORDER.includes(t.toUpperCase().replace(/\s+/g,'_'))),
    ];
    const tags = sorted.map(t => _roseTagBadge(t)).join('');

    const av = _roseToAvatar(a);
    const platIcons = _avPlatformIcons(av);

    return `<div class="vrcn-content-card av-card" onclick="selectAvatar('${aid}')">
        <div class="cc-bg" style="${thumbStyle}"></div>
        <div class="cc-scrim"></div>
        <div class="cc-badges-top">${platIcons || avatarStatusBadge(true)}</div>${_avPerfBadges(av)}
        ${platIcons ? `<div class="cc-badge-db">${avatarStatusBadge(true)}</div>` : ''}
        <div class="cc-content">
            <div class="cc-name">${esc(a.avatar_name || t('avatars.labels.unnamed', 'Unnamed'))}</div>
            <div class="cc-bottom-row">
                <div class="cc-meta">${esc(a.author || '')}</div>
                ${tags ? `<div class="cc-tags">${tags}</div>` : ''}
            </div>
        </div>
    </div>`;
}


_avatarsSyncViewBtns();

function _avEditIsOwn() {
    return avatarFilter === 'own';
}

function _avEditVisibleList() {
    if (_avEditIsOwn()) {
        const q = (document.getElementById('ownAvatarSearchInput')?.value || '').toLowerCase();
        const base = q
            ? avatarsData.filter(a => (a.name || '').toLowerCase().includes(q) || (a.authorName || '').toLowerCase().includes(q))
            : avatarsData;
        return base.filter(_avPassesFilters);
    }
    const q = (document.getElementById('favAvatarSearchInput')?.value || '').toLowerCase();
    let filtered = favAvatarsData;
    if (favAvatarGroupFilter) filtered = filtered.filter(a => a.favoriteGroup === favAvatarGroupFilter);
    if (q) filtered = filtered.filter(a => (a.name || '').toLowerCase().includes(q) || (a.authorName || '').toLowerCase().includes(q));
    return filtered.filter(_avPassesFilters);
}

function _avEditRerender() {
    if (_avEditIsOwn()) filterOwnAvatars();
    else filterFavAvatars();
}

function _avEditSyncButtons() {
    const isOwn = _avEditIsOwn();
    const show = (id, on) => {
        const el = document.getElementById(id);
        if (el) el.style.display = on ? '' : 'none';
    };
    show('avatarEditAddFavWrap', isOwn);
    show('avatarEditMoveWrap', !isOwn);
    show('avatarEditRemoveBtn', !isOwn);
    show('avatarEditDeleteBtn', isOwn);
}

lvEditRegister('avatars', {
    attr: 'data-avid',
    isActive: () => _avEditMode,
    isSelected: id => _avEditSelected.has(id),
    toggle: id => { if (_avEditSelected.has(id)) _avEditSelected.delete(id); else _avEditSelected.add(id); },
    onChange: () => updateAvEditBar(),
});

function avEditShowAddFavMenu(btn) {
    if (_avEditSelected.size === 0) return;
    const picker = document.getElementById('avatarEditAddFavPicker');
    if (!picker) return;
    if (picker.style.display === 'block') { picker.style.display = 'none'; picker.innerHTML = ''; return; }
    const groups = (typeof favAvatarGroups !== 'undefined') ? favAvatarGroups : [];
    picker.innerHTML = groups.map(g => {
        const count = favAvatarsData.filter(a => a.favoriteGroup === g.name).length;
        const gn = jsq(g.name), gt = jsq(g.type);
        return `<div class="vn-select-option" onclick="avEditAddToFavorites('${gn}','${gt}')">
            <span class="msi" style="font-size:14px;flex-shrink:0;">favorite</span>
            <span style="flex:1;">${esc(g.displayName || g.name)}</span>
            ${favGroupBadge(g)}
            <span style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx2);flex-shrink:0;">${count}</span>
        </div>`;
    }).join('') || `<div class="vn-select-option" style="pointer-events:none;color:var(--tx3);">${esc(t('avatars.favorites.no_groups', 'No favorite groups'))}</div>`;
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

function avEditAddToFavorites(groupName, groupType) {
    if (_avEditSelected.size === 0) return;
    const picker = document.getElementById('avatarEditAddFavPicker');
    if (picker) { picker.style.display = 'none'; picker.innerHTML = ''; }
    const ids = [..._avEditSelected];
    ids.forEach(avatarId => {
        if (favAvatarsData.some(a => a.id === avatarId && a.favoriteGroup === groupName)) return;
        sendToCS({ action: 'vrcAddAvatarFavorite', avatarId, groupName, groupType, oldFvrtId: '' });
    });
    showToast(true, tf('avatars.edit.added_to_favorites', { count: ids.length }, 'Added {count} avatars to favorites'));
    exitAvEditMode();
}

let _avBulkDeletePending = 0;
let _avBulkDeleteOk = 0;

function avEditDeleteSelected() {
    const ids = [..._avEditSelected];
    if (!ids.length) return;
    const names = ids.map(id => (avatarsData.find(a => a.id === id)?.name) || id).slice(0, 6);
    const more = ids.length - names.length;
    const listHtml = names.map(n => `<div>${esc(n)}</div>`).join('')
        + (more > 0 ? `<div>${esc(tf('avatars.edit.bulk_delete_more', { count: more }, '+{count} more'))}</div>` : '');

    vrcnConfirmDelete({
        id: 'avatarBulkDeleteModal',
        title: t('avatars.edit.bulk_delete', 'Bulk Delete'),
        icon: 'delete',
        message: tf('avatars.edit.bulk_delete_confirm', { count: ids.length },
            'Delete {count} avatars? They are hidden and their files are removed. This cannot be undone.'),
        listHtml,
        confirmLabel: t('avatars.edit.bulk_delete', 'Bulk Delete'),
        onConfirm: () => {
            _avBulkDeletePending = ids.length;
            _avBulkDeleteOk = 0;
            ids.forEach(avatarId => sendToCS({ action: 'vrcDeleteAvatar', avatarId }));
            const gone = new Set(ids);
            avatarsData = avatarsData.filter(a => !gone.has(a.id));
            exitAvEditMode();
        },
    });
}

function avBulkDeleteConsume(success) {
    if (_avBulkDeletePending <= 0) return false;
    _avBulkDeletePending--;
    if (success) _avBulkDeleteOk++;
    if (_avBulkDeletePending === 0) {
        showToast(_avBulkDeleteOk > 0, tf('avatars.edit.bulk_delete_done', { count: _avBulkDeleteOk }, 'Deleted {count} avatars'));
        sendToCS({ action: 'vrcGetAvatars', filter: 'own' });
    }
    return true;
}

function _avSyncEditBtn() {
    const btn = document.getElementById('avatarEditModeBtn');
    if (btn) btn.style.display = (avatarFilter === 'favorites' || avatarFilter === 'own') ? '' : 'none';
}
