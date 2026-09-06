// Placeholder (4-byte GIF) — forces Chromium to release decoded bitmaps.
const PLACEHOLDER = 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7';
const LIB_PAGE_SIZE = 50;

// State.
let _libTotal        = 0;
let _libHasMore      = false;
let _libLoading      = false;
let _libObserver     = null;
let _libPage         = 0;
let _libFiltered     = [];
let _libViewMode     = localStorage.getItem('libViewMode') || 'grid';
let _libFolderPath   = null;
let _libFriendFilter = '__all__';
let _libWorldFilter  = '__all__';
let _libEditMode     = false;
let _libEditSelected = new Set();
let _libRatingFilter = '__all__';
let _libTagFilter     = '__all__';
let _libUserTagFilter = '__all__';
let _mediaTagsRequested = false;
let _ratingsScanRequested = false;
let _libRatingsRenderTimer = null;

// Destroy / cleanup.
function destroyLibrary() {
    _libHasMore = false; // stop any in-flight _fetchNextMetaPage chain
    const g = document.getElementById('libGrid');
    if (g) {
        // Release decoded bitmaps BEFORE clearing DOM
        g.querySelectorAll('.lib-thumb').forEach(img => { img.src = PLACEHOLDER; });
        g.querySelectorAll('video').forEach(v => { try { v.pause(); } catch {} v.src = ''; });
        g.innerHTML = '';
    }
    setPaginator('libPaginatorBar','');
    _libFiltered     = [];
    _libPage         = 0;
    _libTotal        = 0;
    _libHasMore      = false;
    _libLoading      = false;
    _libFolderPath   = null;
    _libFriendFilter = '__all__';
    _libWorldFilter  = '__all__';
    _libTagFilter     = '__all__';
    _libUserTagFilter = '__all__';
    if (_libEditMode) _exitLibEditModeUi();
    _renderLibIconSelects();
}

// Tags / user tags.
function mediaTagLabel(tag) {
    return t('library.tags.' + tag, tag.charAt(0).toUpperCase() + tag.slice(1));
}

function getMediaTags(path) {
    return (path && mediaTags.get(path)) || [];
}

function getMediaUserTags(path) {
    return (path && mediaUserTags.get(path)) || [];
}

function _libRefilter() {
    _renderLibIconSelects();
    if (document.getElementById('libFolderFilter')) filterLibrary(true);
}

function requestMediaTags(force = false) {
    if (_mediaTagsRequested && !force) return;
    _mediaTagsRequested = true;
    sendToCS({ action: 'getMediaTags' });
}

function onMediaTagsData(payload) {
    mediaTags = new Map(Object.entries(payload?.tags || {}));
    mediaUserTags = new Map(Object.entries(payload?.userTags || {}));
    _libRefilter();
    _photoRefreshTagUi();
}

function onMediaTagsUpdated(payload) {
    const path = payload?.path || '';
    if (!path) return;
    const tags = payload.tags || [];
    if (tags.length) mediaTags.set(path, tags);
    else mediaTags.delete(path);
    _libRefilter();
    _photoRefreshTagUi();
}

function onMediaUserTagsUpdated(payload) {
    const path = payload?.path || '';
    if (!path) return;
    const list = payload.userTags || [];
    if (list.length) mediaUserTags.set(path, list);
    else mediaUserTags.delete(path);
    _libRefilter();
    _photoRefreshTagUi();
}

function _libEditTargets(path) {
    if (!_libEditMode || _libEditSelected.size === 0) return [path];
    if (document.getElementById('photoDetailModal')) return [path];
    const sel = [..._libEditSelected];
    return sel.includes(path) ? sel : [path];
}

function toggleMediaTag(path, tag) {
    if (!path || !MEDIA_TAG_CATALOG.includes(tag)) return;
    const add = !getMediaTags(path).includes(tag);
    _libEditTargets(path).forEach(p => {
        const current = getMediaTags(p);
        if (add === current.includes(tag)) return;
        const next = add ? [...current, tag] : current.filter(x => x !== tag);
        if (next.length) mediaTags.set(p, next);
        else mediaTags.delete(p);
        sendToCS({ action: 'setMediaTags', path: p, tags: next });
    });
    _photoRefreshTagUi();
    _libRefilter();
}

function _photoRefreshTagUi() {
    const modal = document.getElementById('photoDetailModal');
    const x = _photoState.item;
    if (!modal || !x) return;
    const infoPane = modal.querySelector('.photo-detail-info-pane');
    if (infoPane && !x.remote) infoPane.innerHTML = _photoBuildInfoPaneContent(x);
    _photoRenderUserTags();
}

// Data loading.
// First tab open: show localStorage cache immediately, then ask C# (returns
// instantly from its own in-memory cache after the first scan).
function refreshLibrary() {
    document.getElementById('libViewGrid')?.classList.toggle('active', _libViewMode === 'grid');
    document.getElementById('libViewFolder')?.classList.toggle('active', _libViewMode === 'folder');
    try {
        const raw = localStorage.getItem('vrcnext_lib_cache');
        if (raw) {
            const cached = JSON.parse(raw);
            if (cached.files && cached.files.length) {
                libraryFiles = cached.files;
                _libTotal    = cached.total || cached.files.length;
                _libHasMore  = false;
                filterLibrary();
            }
        }
    } catch {}
    requestMediaTags();
    sendToCS({ action: 'scanLibrary' });
}

// Refresh button: force a full filesystem rescan.
function forceRefreshLibrary() {
    _libHasMore  = false;
    _libLoading  = false;
    libraryFiles = [];
    _libFiltered = [];
    _libPage     = 0;
    _libTotal    = 0;
    const g = document.getElementById('libGrid');
    if (g) {
        g.querySelectorAll('.lib-thumb').forEach(img => { img.src = PLACEHOLDER; });
        g.querySelectorAll('video').forEach(v => { try { v.pause(); } catch {} v.src = ''; });
        g.innerHTML = `<div class="empty-msg">${t('library.scanning', 'Scanning...')}</div>`;
    }
    setPaginator('libPaginatorBar','');
    sendToCS({ action: 'scanLibraryForce' });
}

function renderLibrary(data) {
    document.getElementById('libViewGrid')?.classList.toggle('active', _libViewMode === 'grid');
    document.getElementById('libViewFolder')?.classList.toggle('active', _libViewMode === 'folder');
    const files  = Array.isArray(data) ? data : (data.files || []);
    libraryFiles = files;
    _libTotal    = Array.isArray(data) ? files.length : (data.total || files.length);
    _libHasMore  = Array.isArray(data) ? false : (data.hasMore || false);
    _libLoading  = false;

    try {
        const cacheItems = files.slice(0, 100).map(x => ({
            name: x.name, path: x.path, folder: x.folder, type: x.type,
            size: x.size, modified: x.modified, time: x.time,
            url: x.url, worldId: x.worldId || '',
            players: (x.players || []).slice(0, 4),
        }));
        localStorage.setItem('vrcnext_lib_cache', JSON.stringify({ timestamp: Date.now(), files: cacheItems, total: _libTotal }));
    } catch {}

    _resolveWorldIds(files);
    requestMediaTags();
    filterLibrary();
    _renderLibIconSelects();
    if (typeof navUpdateBadges === 'function') navUpdateBadges();
    _fetchNextMetaPage();
}

function appendLibraryPage(data) {
    const newFiles = data.files || [];
    _libTotal   = data.total || _libTotal;
    _libHasMore = data.hasMore || false;
    _libLoading = false;
    if (!newFiles.length) return;

    newFiles.forEach(f => libraryFiles.push(f));
    _resolveWorldIds(newFiles);

    // Apply current filters to new files and append to _libFiltered
    const ff   = document.getElementById('libFolderFilter')?.value ?? '__all__';
    const tf   = document.getElementById('libTypeFilter')?.value ?? 'all';
    let more   = newFiles;
    if (showFavOnly)                    more = more.filter(x => favorites.has(x.path));
    if (ff !== '__all__')               more = more.filter(x => x.folder === ff);
    if (tf !== 'all')                   more = more.filter(x => x.type === tf);
    if (_libFriendFilter !== '__all__') more = more.filter(x => (x.players || []).some(p => p.userId === _libFriendFilter));
    if (_libWorldFilter !== '__all__')  more = more.filter(x => x.worldId === _libWorldFilter);
    if (_libTagFilter !== '__all__')    more = more.filter(x => getMediaTags(x.path).includes(_libTagFilter));
    if (_libUserTagFilter !== '__all__') more = more.filter(x => getMediaUserTags(x.path).some(u => u.userId === _libUserTagFilter));
    more.forEach(f => _libFiltered.push(f));
    _libFiltered.sort((a, b) => new Date(b.modified) - new Date(a.modified));

    // Update paginator to reflect the newly available total (grid mode only)
    if (_libViewMode !== 'folder') {
        const totalPages = Math.ceil(_libFiltered.length / LIB_PAGE_SIZE) || 1;
        setPaginator('libPaginatorBar',buildLibPagination(_libPage, totalPages));
    }

    // Continue chaining until all metadata is loaded
    _fetchNextMetaPage();
}

// Immediately requests the next metadata batch from C# — no scroll required.
// Chains automatically until _libHasMore is false.
function _fetchNextMetaPage() {
    if (!_libHasMore || _libLoading) return;
    _libLoading = true;
    sendToCS({ action: 'loadLibraryPage', offset: libraryFiles.length });
}

// Background enrichment: C# sends batches of { path → worldId } after the fast scan.
// Patches libraryFiles in-place and injects world badges into already-rendered cards.
function applyLibraryWorldIds(dict) {
    if (!dict || !Object.keys(dict).length) return;
    const newIds = [];
    for (const [path, worldId] of Object.entries(dict)) {
        if (!worldId) continue;
        // Patch in-memory item
        const item = libraryFiles.find(f => f.path === path);
        if (item) { item.worldId = worldId; }
        if (!worldInfoCache[worldId]) newIds.push(worldId);
        // Patch visible card if rendered
        const card = document.querySelector(`.lib-card[data-path="${CSS.escape(path)}"]`);
        if (!card) continue;
        const wrap = card.querySelector('.lib-thumb-wrap');
        if (!wrap) continue;
        const wInfo = worldInfoCache[worldId];
        const wName  = wInfo ? esc(wInfo.name) : t('library.view_world', 'View World');
        const wThumb = wInfo?.thumbnailImageUrl || '';
        const badgeHtml = `<button class="lib-world-badge" data-wid="${esc(worldId)}" onclick="event.stopPropagation();openWorldSearchDetail('${esc(worldId)}')" title="${wName}"><span class="lib-world-badge-thumb" style="${wThumb ? `background-image:url('${cssUrl(imgThumb(wThumb, 64))}')` : ''}"></span><span class="lib-world-badge-text">${wName}</span></button>`;
        const existingBadge = wrap.querySelector('.lib-world-badge');
        if (existingBadge) existingBadge.outerHTML = badgeHtml;
        else wrap.insertAdjacentHTML('beforeend', badgeHtml);
    }
    if (newIds.length) _queueWorldIds(newIds);
    _renderLibIconSelects();
}

function applyLibraryAuthors(dict) {
    if (!dict || !Object.keys(dict).length) return;
    for (const [path, author] of Object.entries(dict)) {
        if (!author) continue;
        const item = libraryFiles.find(f => f.path === path);
        if (item) {
            item.authorName = author.name || '';
            item.authorId   = author.id   || '';
        }
        const photoModal = document.getElementById('photoDetailModal');
        if (photoModal && _photoState.item?.path === path) {
            if (_photoState.item) { _photoState.item.authorName = author.name || ''; _photoState.item.authorId = author.id || ''; }
            const infoPane = photoModal.querySelector('.photo-detail-info-pane');
            if (infoPane) infoPane.innerHTML = _photoBuildInfoPaneContent(_photoState.item);
        }
    }
}

// Called when a new file lands in a watch folder — no rescan needed.
function addNewLibraryFile(item) {
    if (!item || libraryFiles.find(f => f.path === item.path)) return;
    libraryFiles.unshift(item); // prepend — newest first
    _resolveWorldIds([item]);
    filterLibrary(true); // re-filter current page so new file appears at top of page 0
    if (typeof navUpdateBadges === 'function') navUpdateBadges();
}

const LIB_WORLD_BATCH = 30;
const LIB_WORLD_BATCH_DELAY_MS = 1200;
const _libWorldQueue = new Set();
let _libWorldBatchTimer = null;
let _libWorldBatchInFlight = false;

function _queueWorldIds(ids) {
    let added = false;
    for (const id of ids || []) {
        if (!id || worldInfoCache[id] || _libWorldQueue.has(id)) continue;
        _libWorldQueue.add(id);
        added = true;
    }
    if (added) _pumpWorldQueue();
}

function _pumpWorldQueue(force) {
    if (_libWorldBatchTimer && !force) return;
    if (_libWorldBatchInFlight && !force) return;
    clearTimeout(_libWorldBatchTimer);
    _libWorldBatchTimer = null;
    for (const id of _libWorldQueue) if (worldInfoCache[id]) _libWorldQueue.delete(id);
    if (_libWorldQueue.size === 0) { _libWorldBatchInFlight = false; return; }
    const batch = [..._libWorldQueue].slice(0, LIB_WORLD_BATCH);
    _libWorldBatchInFlight = true;
    sendToCS({ action: 'vrcResolveWorlds', worldIds: batch });
    _libWorldBatchTimer = setTimeout(() => {
        _libWorldBatchTimer = null;
        for (const id of batch) _libWorldQueue.delete(id);
        _libWorldBatchInFlight = false;
        _pumpWorldQueue(true);
    }, LIB_WORLD_BATCH_DELAY_MS);
}

function _resolveWorldIds(files) {
    _queueWorldIds((files || []).filter(x => x.worldId).map(x => x.worldId));
}

// Page rendering.
function _renderLibPage() {
    const g = document.getElementById('libGrid');
    if (!g) return;

    // Release decoded bitmaps from the previous page before clearing the DOM.
    // Setting src to the 4-byte placeholder is the only reliable way to free
    // bitmaps in Chromium — img.src='' and removeAttribute do NOT free them.
    g.querySelectorAll('.lib-thumb').forEach(img => { img.src = PLACEHOLDER; });
    g.querySelectorAll('video').forEach(v => { try { v.pause(); } catch {} v.src = ''; });

    const start      = _libPage * LIB_PAGE_SIZE;
    const pageItems  = _libFiltered.slice(start, start + LIB_PAGE_SIZE);
    const totalPages = Math.ceil(_libFiltered.length / LIB_PAGE_SIZE) || 1;

    if (!pageItems.length) {
        const isFiltered = showFavOnly
            || (document.getElementById('libFolderFilter')?.value ?? '__all__') !== '__all__'
            || (document.getElementById('libTypeFilter')?.value ?? 'all') !== 'all';
        g.innerHTML = '<div class="empty-msg">' + (showFavOnly
            ? t('library.empty.favorites', 'No favorites yet.')
            : isFiltered
                ? t('library.empty.filtered', 'No media files found.')
                : t('library.empty.watch_folders', 'Add watch folders in Settings.')) + '</div>';
        setPaginator('libPaginatorBar','');
        return;
    }

    const groups = {};
    pageItems.forEach(x => {
        const d = new Date(x.modified);
        const k = fmtLongDate(d);
        if (!groups[k]) groups[k] = [];
        groups[k].push(x);
    });

    let h = '';
    for (const [dt, items] of Object.entries(groups)) {
        h += `<div class="lib-date-group-container" data-date="${esc(dt)}"><div class="lib-date-group">${esc(dt)}</div><div class="lib-date-group-cards">`;
        items.forEach(x => { h += _buildLibCard(x); });
        h += `</div></div>`;
    }
    g.innerHTML = h;

    setPaginator('libPaginatorBar',buildLibPagination(_libPage, totalPages));
}

function buildLibPagination(page, totalPages) {
    const countHtml = `<span style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx2);padding:0 8px;">${tf('library.pagination.files', { count: _libFiltered.length.toLocaleString() }, '{count} files')}</span>`;
    return buildPaginator(page, totalPages, 'libGoPage', countHtml);
}

function libGoPage(page) {
    if (page < 0) return;
    const totalPages = Math.ceil(_libFiltered.length / LIB_PAGE_SIZE) || 1;
    if (page >= totalPages) return;
    if (page === _libPage) return;
    _libPage = page;
    _renderLibPage();
    const wrap = document.querySelector('.lib-wrap');
    if (wrap) wrap.scrollTop = 0;
}


// Filter.
// keepPage=true: stay on current page (delete / favorite / hide actions)
// keepPage=false (default): reset to page 0 (filter/sort changes)
function filterLibrary(keepPage = false) {
    const ff         = document.getElementById('libFolderFilter').value;
    const typeFilter = document.getElementById('libTypeFilter').value;
    let f            = [...libraryFiles]; // always a copy — never share ref with libraryFiles
    if (showFavOnly)                   f = f.filter(x => favorites.has(x.path));
    if (_libRatingFilter !== '__all__') f = f.filter(x => (photoRatings.get(x.path) || 0) === Number(_libRatingFilter));
    if (ff !== '__all__')              f = f.filter(x => x.folder === ff);
    if (typeFilter !== 'all')          f = f.filter(x => x.type === typeFilter);
    if (_libFriendFilter !== '__all__') f = f.filter(x => (x.players || []).some(p => p.userId === _libFriendFilter));
    if (_libWorldFilter !== '__all__')  f = f.filter(x => x.worldId === _libWorldFilter);
    if (_libTagFilter !== '__all__')    f = f.filter(x => getMediaTags(x.path).includes(_libTagFilter));
    if (_libUserTagFilter !== '__all__') f = f.filter(x => getMediaUserTags(x.path).some(u => u.userId === _libUserTagFilter));
    f.sort((a, b) => new Date(b.modified) - new Date(a.modified));

    _libFiltered = f;
    if (!keepPage) _libPage = 0;
    // Clamp page in case items were removed and total pages shrank
    const totalPages = Math.ceil(_libFiltered.length / LIB_PAGE_SIZE) || 1;
    if (_libPage >= totalPages) _libPage = totalPages - 1;

    if (_libViewMode === 'folder') {
        if (_libFolderPath) {
            _renderFolderContents();
        } else {
            _renderFolderView();
        }
    } else {
        _renderLibPage();
    }
}

function resetLibFilters() {
    _libFriendFilter  = '__all__';
    _libWorldFilter   = '__all__';
    _libRatingFilter  = '__all__';
    _libTagFilter     = '__all__';
    _libUserTagFilter = '__all__';
    _renderLibIconSelects();
    filterLibrary();
}

function _updateLibResetBtn() {
    const btn = document.getElementById('libResetFiltersBtn');
    const active = _libFriendFilter !== '__all__' || _libWorldFilter !== '__all__'
        || _libRatingFilter !== '__all__' || _libTagFilter !== '__all__' || _libUserTagFilter !== '__all__';
    if (btn) btn.style.display = active ? '' : 'none';
}

function _renderLibIconSelects() {
    _updateLibResetBtn();
    _renderLibRatingSelect();
    _renderLibIconSelect(
        'libFriendFilterWrap',
        _buildFriendItems(),
        _libFriendFilter,
        t('library.filters.all_friends', 'All Friends'),
        'person',
        true,
        function(val) { _libFriendFilter = val; _renderLibIconSelects(); filterLibrary(); }
    );
    _renderLibIconSelect(
        'libWorldFilterWrap',
        _buildWorldItems(),
        _libWorldFilter,
        t('library.filters.all_worlds', 'All Worlds'),
        'travel_explore',
        false,
        function(val) { _libWorldFilter = val; _renderLibIconSelects(); filterLibrary(); }
    );
    _renderLibIconSelect(
        'libTagFilterWrap',
        _buildTagItems(),
        _libTagFilter,
        t('library.filters.all_tags', 'All Tags'),
        'sell',
        false,
        function(val) { _libTagFilter = val; _renderLibIconSelects(); filterLibrary(); },
        { alwaysShow: true }
    );
    _renderLibIconSelect(
        'libUserTagFilterWrap',
        _buildUserTagItems(),
        _libUserTagFilter,
        t('library.filters.all_user_tags', 'All User Tags'),
        'person_check',
        true,
        function(val) { _libUserTagFilter = val; _renderLibIconSelects(); filterLibrary(); }
    );
}

function _libCrossFilterBase(excludeFriend, excludeWorld, excludeRating, excludeTag, excludeUserTag) {
    let base = libraryFiles;
    if (showFavOnly)                                     base = base.filter(x => favorites.has(x.path));
    if (!excludeRating && _libRatingFilter !== '__all__') base = base.filter(x => (photoRatings.get(x.path) || 0) === Number(_libRatingFilter));
    if (!excludeFriend && _libFriendFilter !== '__all__') base = base.filter(x => (x.players || []).some(p => p.userId === _libFriendFilter));
    if (!excludeWorld && _libWorldFilter !== '__all__')   base = base.filter(x => x.worldId === _libWorldFilter);
    if (!excludeTag && _libTagFilter !== '__all__')       base = base.filter(x => getMediaTags(x.path).includes(_libTagFilter));
    if (!excludeUserTag && _libUserTagFilter !== '__all__') base = base.filter(x => getMediaUserTags(x.path).some(u => u.userId === _libUserTagFilter));
    return base;
}

function _buildTagItems() {
    const base = _libCrossFilterBase(false, false, false, true, false);
    const counts = {};
    base.forEach(x => getMediaTags(x.path).forEach(tag => { counts[tag] = (counts[tag] || 0) + 1; }));
    return MEDIA_TAG_CATALOG
        .map((tag, i) => ({ value: tag, label: mediaTagLabel(tag), thumb: '', count: counts[tag] || 0, round: false, _i: i }))
        .sort((a, b) => (b.count - a.count) || (a._i - b._i));
}

function _buildUserTagItems() {
    const base = _libCrossFilterBase(false, false, false, false, true);
    const map = {};
    base.forEach(x => {
        getMediaUserTags(x.path).forEach(u => {
            if (!u.userId) return;
            if (!map[u.userId]) {
                const fr = (typeof vrcFriendsData !== 'undefined') ? vrcFriendsData.find(f => f.id === u.userId) : null;
                map[u.userId] = {
                    value: u.userId,
                    label: fr?.displayName || u.displayName || u.userId,
                    thumb: fr?.image || '',
                    count: 0,
                    round: true,
                };
            }
            map[u.userId].count++;
        });
    });
    return Object.values(map).sort((a, b) => b.count - a.count);
}

function _buildRatingCounts() {
    const counts = { 1: 0, 2: 0, 3: 0, 4: 0, 5: 0 };
    _libCrossFilterBase(false, false, true, false, false).forEach(x => {
        const r = photoRatings.get(x.path) || 0;
        if (r >= 1 && r <= 5) counts[r]++;
    });
    return counts;
}

function _buildFriendItems() {
    const base = _libCrossFilterBase(true, false, false, false, false);
    const map = {};
    base.forEach(x => {
        (x.players || []).forEach(p => {
            if (!p.userId) return;
            const fr = (typeof vrcFriendsData !== 'undefined') ? vrcFriendsData.find(f => f.id === p.userId) : null;
            if (!fr) return;
            if (!map[p.userId]) {
                map[p.userId] = { value: p.userId, label: fr.displayName || p.displayName || p.userId, thumb: fr.image || p.image || '', count: 0, round: true };
            }
            map[p.userId].count++;
        });
    });
    return Object.values(map).sort((a, b) => b.count - a.count);
}

function _buildWorldItems() {
    const base = _libCrossFilterBase(false, true, false, false, false);
    const map = {};
    base.forEach(x => {
        if (!x.worldId) return;
        if (!map[x.worldId]) {
            const wInfo = (typeof worldInfoCache !== 'undefined') ? worldInfoCache[x.worldId] : null;
            map[x.worldId] = { value: x.worldId, label: wInfo?.name || x.worldId, thumb: wInfo?.thumbnailImageUrl || wInfo?.imageUrl || '', count: 0, round: false };
        }
        map[x.worldId].count++;
    });
    return Object.values(map).sort((a, b) => b.count - a.count);
}

function _libHeartsHtml(n, size) {
    let h = '';
    for (let i = 1; i <= 5; i++) {
        const filled = i <= n;
        h += `<span class="msi${filled ? ' lib-heart-filled' : ''}" style="font-size:${size}px;">favorite</span>`;
    }
    return h;
}

function _renderLibRatingSelect() {
    const container = document.getElementById('libRatingFilterWrap');
    if (!container) return;
    if (window._isLinuxUi) { container.innerHTML = ''; return; }

    const wasOpen = !!container.querySelector('.vn-select.vn-open');
    const iconBox = `<span class="lib-is-thumb lib-is-thumb-icon"><span class="msi">favorite</span></span>`;

    const allActive = _libRatingFilter === '__all__' ? ' vn-active' : '';
    const allOptHtml = `<div class="vn-select-option${allActive}" data-rval="__all__">
        ${iconBox}<span class="vn-select-label">${esc(t('library.filters.rating', 'Rating'))}</span>
    </div>`;

    const counts = _buildRatingCounts();
    const optHtml = [5, 4, 3, 2, 1].map(n => {
        const active = _libRatingFilter === String(n) ? ' vn-active' : '';
        return `<div class="vn-select-option${active}" data-rval="${n}">
            <span class="vn-select-label lib-rating-hearts">${_libHeartsHtml(n, 13)}</span>
            <span class="lib-rating-count">${counts[n]}</span>
        </div>`;
    }).join('');

    const selLabel = _libRatingFilter !== '__all__'
        ? tf('library.filters.rating_n', { n: _libRatingFilter }, '{n} Hearts')
        : t('library.filters.rating', 'Rating');

    container.innerHTML = `<div class="vn-select lib-is-select">
        <div class="vn-select-trigger">${iconBox}<span class="vn-select-label">${esc(selLabel)}</span><span class="msi vn-select-arrow">expand_more</span></div>
        <div class="vn-select-panel">${allOptHtml}${optHtml}</div>
    </div>`;

    const wrap    = container.querySelector('.vn-select');
    const trigger = container.querySelector('.vn-select-trigger');
    const panel   = container.querySelector('.vn-select-panel');

    function close() { wrap.classList.remove('vn-open'); }
    function open() {
        if (!_ratingsScanRequested) { _ratingsScanRequested = true; sendToCS({ action: 'scanLibraryRatings' }); }
        wrap.classList.add('vn-open');
        const rect  = wrap.getBoundingClientRect();
        const below = rect.bottom + 220 < window.innerHeight;
        panel.style.top    = below ? 'calc(100% + 4px)' : 'auto';
        panel.style.bottom = below ? 'auto' : 'calc(100% + 4px)';
        vnPanelAnchor(wrap, panel, below);
        setTimeout(() => document.addEventListener('click', onOut, { once: true }), 0);
    }
    function onOut(e) { wrap.contains(e.target) ? document.addEventListener('click', onOut, { once: true }) : close(); }

    trigger.addEventListener('click', e => { e.stopPropagation(); wrap.classList.contains('vn-open') ? close() : open(); });
    panel.querySelectorAll('[data-rval]').forEach(opt => {
        opt.addEventListener('click', e => {
            e.stopPropagation();
            _libRatingFilter = opt.dataset.rval;
            close();
            _renderLibIconSelects();
            filterLibrary();
        });
    });

    if (wasOpen) open();
}

function _renderLibIconSelect(wrapperId, items, currentVal, allLabel, allIcon, round, onSelect, opts) {
    const container = document.getElementById(wrapperId);
    if (!container) return;
    if (!items.length && !opts?.alwaysShow) { container.innerHTML = ''; return; }

    const selItem  = currentVal !== '__all__' ? items.find(i => i.value === currentVal) : null;
    const selLabel = selItem ? selItem.label : allLabel;
    const selThumb = selItem?.thumb || '';
    const selRound = selItem?.round ?? round;

    function thumbHtml(thumb, icon, isRound) {
        const rc = isRound ? ' lib-is-thumb-round' : '';
        return thumb
            ? `<span class="lib-is-thumb${rc}" style="background-image:url('${cssUrl(imgThumb(thumb, 64))}')"></span>`
            : `<span class="lib-is-thumb lib-is-thumb-icon${rc}"><span class="msi">${esc(icon)}</span></span>`;
    }

    const optHtml = [
        `<div class="vn-select-option${currentVal === '__all__' ? ' vn-active' : ''}" data-isval="__all__">${thumbHtml('', allIcon, false)}<span class="vn-select-label">${esc(allLabel)}</span></div>`,
        ...items.map(it => `<div class="vn-select-option${currentVal === it.value ? ' vn-active' : ''}" data-isval="${esc(it.value)}">${thumbHtml(it.thumb, allIcon, it.round ?? round)}<span class="vn-select-label">${esc(it.label)}</span><span style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx2);flex-shrink:0;margin-left:auto;">${it.count}</span></div>`)
    ].join('');

    container.innerHTML = `<div class="vn-select lib-is-select">
        <div class="vn-select-trigger">${thumbHtml(selThumb, allIcon, selRound)}<span class="vn-select-label">${esc(selLabel)}</span><span class="msi vn-select-arrow">expand_more</span></div>
        <div class="vn-select-panel">${optHtml}</div>
    </div>`;

    const wrap    = container.querySelector('.vn-select');
    const trigger = container.querySelector('.vn-select-trigger');
    const panel   = container.querySelector('.vn-select-panel');

    function close() { wrap.classList.remove('vn-open'); }
    function open() {
        wrap.classList.add('vn-open');
        const rect  = wrap.getBoundingClientRect();
        const below = rect.bottom + 270 < window.innerHeight;
        panel.style.top    = below ? 'calc(100% + 4px)' : 'auto';
        panel.style.bottom = below ? 'auto' : 'calc(100% + 4px)';
        vnPanelAnchor(wrap, panel, below);
        setTimeout(() => document.addEventListener('click', onOut, { once: true }), 0);
    }
    function onOut(e) { wrap.contains(e.target) ? document.addEventListener('click', onOut, { once: true }) : close(); }

    trigger.addEventListener('click', e => { e.stopPropagation(); wrap.classList.contains('vn-open') ? close() : open(); });
    panel.querySelectorAll('[data-isval]').forEach(opt => {
        opt.addEventListener('click', () => { onSelect(opt.dataset.isval); close(); });
    });
}

// Folder mode.
function setLibViewMode(mode) {
    _libViewMode   = mode;
    _libFolderPath = null;
    localStorage.setItem('libViewMode', mode);
    document.getElementById('libViewGrid')?.classList.toggle('active', mode === 'grid');
    document.getElementById('libViewFolder')?.classList.toggle('active', mode === 'folder');
    _updateLibBreadcrumb();
    filterLibrary();
}

function _updateLibBreadcrumb() {
    const bc = document.getElementById('libFolderBreadcrumb');
    if (!bc) return;
    if (_libViewMode === 'folder' && _libFolderPath) {
        const name = _libFolderPath.split(/[\\/]/).pop() || _libFolderPath;
        const nameEl = document.getElementById('libFolderBreadcrumbName');
        if (nameEl) nameEl.textContent = name;
        bc.style.display = 'flex';
    } else {
        bc.style.display = 'none';
    }
}

// Returns the full path of the immediate parent directory for a file.
function _getFileSubfolderPath(x) {
    const fp   = x.path || '';
    const last = Math.max(fp.lastIndexOf('/'), fp.lastIndexOf('\\'));
    return last > 0 ? fp.substring(0, last) : (x.folder || '');
}

function _renderFolderView() {
    const g = document.getElementById('libGrid');
    if (!g) return;
    g.querySelectorAll('.lib-thumb').forEach(img => { img.src = PLACEHOLDER; });
    g.querySelectorAll('video').forEach(v => { try { v.pause(); } catch {} v.src = ''; });
    setPaginator('libPaginatorBar','');

    if (!_libFiltered.length) {
        g.innerHTML = '<div class="empty-msg">' + t('library.empty.watch_folders', 'Add watch folders in Settings.') + '</div>';
        return;
    }

    // Group files by immediate parent directory
    const groups = {};
    _libFiltered.forEach(x => {
        const sub = _getFileSubfolderPath(x);
        if (!groups[sub]) groups[sub] = [];
        groups[sub].push(x);
    });

    // Sort groups by most-recent file descending
    const sorted = Object.entries(groups).sort((a, b) => {
        const latestA = a[1].reduce((mx, f) => Math.max(mx, new Date(f.modified).getTime()), 0);
        const latestB = b[1].reduce((mx, f) => Math.max(mx, new Date(f.modified).getTime()), 0);
        return latestB - latestA;
    });

    let h = '<div class="lib-date-group-cards">';
    for (const [subPath, files] of sorted) {
        h += _buildFolderCard(subPath, files);
    }
    h += '</div>';
    g.innerHTML = h;
}

function _buildFolderCard(subPath, files) {
    const name     = subPath.split(/[\\/]/).pop() || subPath;
    const previews = files.filter(x => x.type === 'image').slice(0, 4);
    const count    = files.length;
    const sp       = jsq(subPath);

    let slots = '';
    for (let i = 0; i < 4; i++) {
        const p = previews[i];
        if (p && p.url) {
            const blurStyle = hiddenMedia.has(p.path) ? ' style="filter:blur(20px);transform:scale(1.08);"' : '';
            slots += `<img src="${esc(p.url)}?thumb=1" loading="lazy"${blurStyle} onerror="this.className='lib-folder-preview-slot'">`;
        } else {
            slots += `<div class="lib-folder-preview-slot"></div>`;
        }
    }

    const countLabel = count === 1
        ? t('library.folder.one_file', '1 file')
        : tf('library.folder.file_count', { count }, '{count} files');
    return `<div class="lib-card" style="cursor:pointer;" onclick="_openLibFolder('${sp}')"><div class="lib-folder-preview">${slots}</div><div class="lib-info"><div class="lib-name">${esc(name)}</div><div class="lib-meta"><span>${esc(countLabel)}</span></div></div></div>`;
}

function _openLibFolder(subPath) {
    _libFolderPath = subPath;
    _updateLibBreadcrumb();
    _renderFolderContents();
}

function _backToFolderList() {
    _libFolderPath = null;
    _updateLibBreadcrumb();
    _renderFolderView();
}

function _renderFolderContents() {
    const g = document.getElementById('libGrid');
    if (!g) return;
    g.querySelectorAll('.lib-thumb').forEach(img => { img.src = PLACEHOLDER; });
    g.querySelectorAll('video').forEach(v => { try { v.pause(); } catch {} v.src = ''; });
    setPaginator('libPaginatorBar','');

    const files = _libFiltered.filter(x => _getFileSubfolderPath(x) === _libFolderPath);
    if (!files.length) {
        g.innerHTML = '<div class="empty-msg">' + t('library.empty.filtered', 'No media files found.') + '</div>';
        return;
    }

    const groups = {};
    files.forEach(x => {
        const k = fmtLongDate(new Date(x.modified));
        if (!groups[k]) groups[k] = [];
        groups[k].push(x);
    });

    let h = '';
    for (const [dt, items] of Object.entries(groups)) {
        h += `<div class="lib-date-group-container" data-date="${esc(dt)}"><div class="lib-date-group">${esc(dt)}</div><div class="lib-date-group-cards">`;
        items.forEach(x => { h += _buildLibCard(x); });
        h += `</div></div>`;
    }
    g.innerHTML = h;
}

// Resolution tag.
const _RES_PRESETS = [
    ['SD', 1280 * 720],
    ['HD', 1920 * 1080],
    ['2K', 2560 * 1440],
    ['4K', 3840 * 2160],
    ['8K', 7680 * 4320],
];

function _resTag(x) {
    const px = (x.imgW || 0) * (x.imgH || 0);
    if (!px) return '';
    let best = _RES_PRESETS[0][0];
    let bestDiff = Infinity;
    for (const [label, presetPx] of _RES_PRESETS) {
        const diff = Math.abs(Math.log(px / presetPx));
        if (diff < bestDiff) { bestDiff = diff; best = label; }
    }
    return best;
}

function _libMetaHtml(x) {
    const parts = [];
    if (x.size) parts.push(esc(x.size));
    const res = _resTag(x);
    if (res) parts.push(esc(res));
    const rating = photoRatings.get(x.path) || 0;
    if (rating > 0) {
        parts.push(`<span class="lib-meta-rating"><span class="msi lib-heart-filled">favorite</span>${rating}x</span>`);
    }
    return parts.join('<span class="lib-meta-dot">·</span>');
}

// Card building.
function _buildLibCard(x) {
    const su     = x.url || '';
    const suAttr = esc(su);
    const suJs   = jsq(su);
    const sp     = jsq(x.path || '');
    const sn     = jsq(x.name || '');
    const iF     = favorites.has(x.path),  fc = iF ? ' active' : '';
    const iH     = hiddenMedia.has(x.path), hc = iH ? ' active' : '';
    const ac     = ['lib-actions', iF ? 'has-fav' : '', iH ? 'has-hidden' : ''].filter(Boolean).join(' ');
    const acts   = `<div class="${ac}"><button class="vrcn-lib-button clip" onclick="event.stopPropagation();copyToClipboard('${suJs}','${sp}','${x.type}')" title="${esc(t('library.actions.copy_clipboard', 'Copy to clipboard'))}"><span class="msi" style="font-size:16px;">content_copy</span></button><button class="vrcn-lib-button fav${fc}" onclick="event.stopPropagation();toggleFavorite('${sp}')" title="${esc(t('library.actions.favorite', 'Favorite'))}"><span class="msi" style="font-size:16px;">favorite</span></button><button class="vrcn-lib-button hide${hc}" onclick="event.stopPropagation();toggleHidden('${sp}')" title="${esc(iH ? t('library.actions.unhide', 'Unhide') : t('library.actions.hide', 'Hide'))}"><span class="msi" style="font-size:16px;">${iH ? 'visibility' : 'visibility_off'}</span></button><button class="vrcn-lib-button del" onclick="event.stopPropagation();showDeleteModal('${sp}','${sn}')"><span class="msi" style="font-size:16px;">delete</span></button></div>`;
    const blurClass = iH ? ' lib-blurred' : '';
    const idx       = libraryFiles.indexOf(x);

    if (_libEditMode) {
        const isSel = _libEditSelected.has(x.path);
        const checkIcon = isSel
            ? `<span class="msi" style="font-size:22px;color:var(--accent);">check_circle</span>`
            : `<span class="msi" style="font-size:22px;color:rgba(255,255,255,0.7);">radio_button_unchecked</span>`;
        const thumbSrc = suAttr ? suAttr + '?thumb=1' : '';
        const isVid = x.type === 'video';
        const media = isVid
            ? `<img class="lib-thumb" src="${thumbSrc}" loading="lazy" onerror="this.outerHTML='<div class=\\'lib-vid-thumb-fallback\\'>${jsq(t('library.video_badge', 'VIDEO'))}</div>'"><span class="lib-vid-badge">${t('library.video_badge', 'VIDEO')}</span>`
            : `<img class="lib-thumb" src="${thumbSrc}" loading="lazy" onerror="this.outerHTML='<div style=\\'width:100%;height:100%;display:flex;align-items:center;justify-content:center;color:var(--tx3);font-size:calc(11px + var(--fs-off, 0px));font-weight:700\\'>${jsq(t('library.no_preview', 'No Preview'))}</div>'">`;
        return `<div class="lib-card lib-card-edit${isSel ? ' lib-card-selected' : ''}" data-path="${esc(x.path||'')}" onclick="toggleLibEditSelect('${sp}',this)" style="user-select:none;cursor:pointer;"><div class="lib-thumb-wrap${blurClass}">${media}<div class="wd-edit-check">${checkIcon}</div></div><div class="lib-info"><div class="lib-name">${esc(x.name)}</div><div class="lib-meta"><span class="lib-meta-left">${_libMetaHtml(x)}</span><span>${x.time}</span></div></div>${isSel ? '<div class="wd-edit-sel-border"></div>' : ''}</div>`;
    }

    let worldBadge = '';
    if (x.worldId) {
        const wInfo  = worldInfoCache[x.worldId];
        const wName  = wInfo ? esc(wInfo.name) : t('library.view_world', 'View World');
        const wThumb = wInfo?.thumbnailImageUrl || '';
        worldBadge   = `<button class="lib-world-badge" data-wid="${esc(x.worldId)}" onclick="event.stopPropagation();openWorldSearchDetail('${esc(x.worldId)}')" title="${wName}"><span class="lib-world-badge-thumb" style="${wThumb ? `background-image:url('${cssUrl(imgThumb(wThumb, 64))}')` : ''}"></span><span class="lib-world-badge-text">${wName}</span></button>`;
    }
    let playersOverlay = '';
    const players = x.players || [];
    if (players.length > 0) {
        const show      = players.slice(0, 3);
        const remaining = players.length - show.length;
        playersOverlay  = `<div class="lib-players-overlay" onclick="event.stopPropagation();openPhotoDetail(${idx})">` +
            show.map(p => {
                const isOwn = currentVrcUser && p.userId === currentVrcUser.id;
                const fr  = isOwn ? currentVrcUser : vrcFriendsData.find(f => f.id === p.userId);
                const img = fr?.image || p.image || '';
                return img
                    ? `<div class="lib-player-av" style="background-image:url('${cssUrl(imgThumb(img, 64))}')" title="${esc(p.displayName)}"></div>`
                    : `<div class="lib-player-av lib-player-av-letter" title="${esc(p.displayName)}">${esc((p.displayName||'?')[0])}</div>`;
            }).join('') +
            (remaining > 0 ? `<div class="lib-player-av lib-player-av-more">+${remaining}</div>` : '') +
            `</div>`;
    }
    const thumbSrc = suAttr ? suAttr + '?thumb=1' : '';

    if (x.type === 'image' || x.type === 'gif') {
        const gifBadge = x.type === 'gif' ? `<span class="lib-vid-badge">${t('library.gif_badge', 'GIF')}</span>` : '';
        return `<div class="lib-card" data-path="${esc(x.path||'')}" data-url="${suAttr}" data-type="${x.type}" data-name="${esc(x.name||'')}">${acts}<div class="lib-thumb-wrap${blurClass}" onclick="openPhotoDetail(${idx})"><img class="lib-thumb" src="${thumbSrc}" loading="lazy" onerror="this.outerHTML='<div style=\\'width:100%;height:100%;display:flex;align-items:center;justify-content:center;color:var(--tx3);font-size:calc(11px + var(--fs-off, 0px));font-weight:700\\'>${jsq(t('library.no_preview', 'No Preview'))}</div>'">${gifBadge}${iH ? '<div class="lib-blur-hint"><span class="msi" style="font-size:18px;">visibility_off</span></div>' : ''}${worldBadge}${playersOverlay}</div><div class="lib-info" onclick="event.stopPropagation();openPhotoDetail(${idx})" style="cursor:pointer;"><div class="lib-name">${esc(x.name)}</div><div class="lib-meta"><span class="lib-meta-left">${_libMetaHtml(x)}</span><span>${x.time}</span></div></div></div>`;
    } else {
        const th = `<img class="lib-thumb" src="${thumbSrc}" loading="lazy" onerror="this.outerHTML='<div class=\\'lib-vid-thumb-fallback\\'>${jsq(t('library.video_badge', 'VIDEO'))}</div>'">`;
        return `<div class="lib-card" data-path="${esc(x.path||'')}" data-url="${suAttr}" data-type="video" data-name="${esc(x.name||'')}">${acts}<div class="lib-thumb-wrap${blurClass}" onclick="openPhotoDetail(${idx})">${th}<div class="lib-vid-overlay"><div class="lib-play-icon"><span class="msi" style="font-size:22px;">play_arrow</span></div></div><span class="lib-vid-badge">${t('library.video_badge', 'VIDEO')}</span>${iH ? '<div class="lib-blur-hint"><span class="msi" style="font-size:18px;">visibility_off</span></div>' : ''}${worldBadge}${playersOverlay}</div><div class="lib-info" onclick="event.stopPropagation();openPhotoDetail(${idx})" style="cursor:pointer;"><div class="lib-name">${esc(x.name)}</div><div class="lib-meta"><span>${x.size}</span><span>${x.time}</span></div></div></div>`;
    }
}

function _libGifHover(card, on) {
    const img = card.querySelector('.lib-thumb');
    const url = card.dataset.url || '';
    if (!img || !url) return;
    const want = on ? url : url + '?thumb=1';
    if (img.getAttribute('src') !== want) img.src = want;
}

document.addEventListener('mouseover', e => {
    if (!(e.target instanceof Element)) return;
    const card = e.target.closest('.lib-card[data-type="gif"]');
    if (card) _libGifHover(card, true);
});

document.addEventListener('mouseout', e => {
    if (!(e.target instanceof Element)) return;
    const card = e.target.closest('.lib-card[data-type="gif"]');
    if (card && !(e.relatedTarget instanceof Element && card.contains(e.relatedTarget))) _libGifHover(card, false);
});

function _libEditRerender() {
    filterLibrary(true);
}

function _exitLibEditModeUi() {
    _libEditMode = false;
    _libEditSelected = new Set();
    const btn = document.getElementById('libEditModeBtn');
    if (btn) {
        btn.innerHTML = `<span class="msi" style="font-size:16px;">edit</span> <span>${t('library.edit.button', 'Edit')}</span>`;
        btn.classList.remove('active');
    }
    const bar = document.getElementById('libEditBar');
    if (bar) bar.style.display = 'none';
}

function toggleLibEditMode() {
    if (_libEditMode) { exitLibEditMode(); return; }
    _libEditMode = true;
    _libEditSelected = new Set();
    const btn = document.getElementById('libEditModeBtn');
    if (btn) {
        btn.innerHTML = `<span class="msi" style="font-size:16px;">check</span> <span>${t('library.edit.done', 'Done')}</span>`;
        btn.classList.add('active');
    }
    const bar = document.getElementById('libEditBar');
    if (bar) bar.style.display = 'flex';
    _libEditRerender();
    updateLibEditBar();
}

function exitLibEditMode() {
    _exitLibEditModeUi();
    _libEditRerender();
}

function toggleLibEditSelect(path, el) {
    if (_libEditSelected.has(path)) {
        _libEditSelected.delete(path);
        const chk = el?.querySelector('.wd-edit-check .msi');
        if (chk) { chk.textContent = 'radio_button_unchecked'; chk.style.color = 'rgba(255,255,255,0.7)'; }
        el?.querySelector('.wd-edit-sel-border')?.remove();
        el?.classList.remove('lib-card-selected');
    } else {
        _libEditSelected.add(path);
        const chk = el?.querySelector('.wd-edit-check .msi');
        if (chk) { chk.textContent = 'check_circle'; chk.style.color = 'var(--accent)'; }
        if (el && !el.querySelector('.wd-edit-sel-border')) el.insertAdjacentHTML('beforeend', '<div class="wd-edit-sel-border"></div>');
        el?.classList.add('lib-card-selected');
    }
    updateLibEditBar();
}

function libEditSelectAll() {
    const all = _libFiltered;
    const allSel = all.length > 0 && all.every(x => _libEditSelected.has(x.path));
    if (allSel) all.forEach(x => _libEditSelected.delete(x.path));
    else        all.forEach(x => _libEditSelected.add(x.path));
    _libEditRerender();
    updateLibEditBar();
}

function updateLibEditBar() {
    const sel   = [..._libEditSelected];
    const count = sel.length;

    const countEl = document.getElementById('libEditCount');
    if (countEl) countEl.textContent = tf('library.edit.selected', { count }, '{count} selected');

    const selAll = document.getElementById('libEditSelectAllBtn');
    if (selAll) {
        const all = _libFiltered;
        const allSel = all.length > 0 && all.every(x => _libEditSelected.has(x.path));
        selAll.textContent = allSel ? t('library.edit.deselect_all', 'Deselect All') : t('library.edit.select_all', 'Select All');
    }

    const setBtn = (id, icon, label) => {
        const btn = document.getElementById(id);
        if (!btn) return;
        const ico = btn.querySelector('.msi');
        const lbl = btn.querySelector('.lib-edit-action-label');
        if (ico) ico.textContent = icon;
        if (lbl) lbl.textContent = label;
    };

    const allFav = count > 0 && sel.every(p => favorites.has(p));
    setBtn('libEditFavBtn', allFav ? 'favorite_border' : 'favorite',
        allFav ? tf('library.edit.unfavorite', { count }, 'Unfavorite ({count})')
               : tf('library.edit.favorite',   { count }, 'Favorite ({count})'));

    const allHidden = count > 0 && sel.every(p => hiddenMedia.has(p));
    setBtn('libEditHideBtn', allHidden ? 'visibility' : 'visibility_off',
        allHidden ? tf('library.edit.unhide', { count }, 'Unhide ({count})')
                  : tf('library.edit.hide',   { count }, 'Hide ({count})'));

    setBtn('libEditDeleteBtn', 'delete', tf('library.edit.delete', { count }, 'Delete ({count})'));

    document.querySelectorAll('.lib-edit-action').forEach(b => b.disabled = count === 0);
}

function libEditFavoriteSelected() {
    if (_libEditSelected.size === 0) return;
    const sel = [..._libEditSelected];
    const allFav = sel.every(p => favorites.has(p));
    sel.forEach(p => {
        if (allFav) { favorites.delete(p); sendToCS({ action: 'removeFavorite', path: p }); }
        else if (!favorites.has(p)) { favorites.add(p); sendToCS({ action: 'addFavorite', path: p }); }
    });
    _libEditRerender();
    updateLibEditBar();
    if (typeof _wdRenderPhotosPage === 'function') _wdRenderPhotosPage();
}

function libEditHideSelected() {
    if (_libEditSelected.size === 0) return;
    const sel = [..._libEditSelected];
    const allHidden = sel.every(p => hiddenMedia.has(p));
    sel.forEach(p => { if (allHidden) hiddenMedia.delete(p); else hiddenMedia.add(p); });
    try { localStorage.setItem('vrcnext_hidden', JSON.stringify([...hiddenMedia])); } catch {}
    _libEditRerender();
    updateLibEditBar();
    if (typeof renderDashRecentPhotos === 'function') renderDashRecentPhotos();
    if (typeof _wdRenderPhotosPage === 'function') _wdRenderPhotosPage();
}

function libEditDeleteSelected() {
    if (_libEditSelected.size === 0) return;
    const count = _libEditSelected.size;
    const x = document.getElementById('deleteModal');
    if (x) x.remove();
    const o = document.createElement('div');
    o.className = 'modal-overlay';
    o.style.display = 'flex'; // inline display required by _closeTopModal (Escape)
    o.id        = 'deleteModal';
    o.onclick   = e => { if (e.target === o) closeDeleteModal(); };
    o.innerHTML = `<div class="modal-box">${renderModalBar(t('library.delete.title', 'Delete File'), [modalCloseAction('closeDeleteModal()')])}<div class="modal-icon danger" style="margin-top:20px;"><span class="msi" style="font-size:22px;">delete</span></div><div class="modal-msg">${tf('library.edit.delete_confirm', { count }, 'Permanently delete {count} file(s) from disk?')}</div><div class="modal-btns"><button class="vrcn-button-round vrcn-btn-danger" onclick="confirmLibEditDelete()">${t('library.delete.confirm', 'Delete')}</button></div></div>`;
    document.body.appendChild(o);
    o.querySelector('.fd-modal-bar-actions .fd-action-btn')?.focus();
}

function confirmLibEditDelete() {
    [..._libEditSelected].forEach(p => { sendToCS({ action: 'deleteLibraryFile', path: p }); favorites.delete(p); });
    closeDeleteModal();
    exitLibEditMode();
}

// World info.
function onWorldsResolved(dict) {
    if (!dict || typeof dict !== 'object') return;
    Object.entries(dict).forEach(([id, w]) => {
        worldInfoCache[id] = { id, name: w.name || '', thumbnailImageUrl: w.thumbnailImageUrl || w.imageUrl || '' };
        _libWorldQueue.delete(id);
    });
    if (typeof _renderLibIconSelects === 'function') _renderLibIconSelects();
    Object.assign(dashWorldCache, dict);
    renderDashboard();
    if (typeof scheduleRenderVrcFriends === 'function' && vrcFriendsData?.length) scheduleRenderVrcFriends();
    if (typeof refreshAllUserItemWorlds === 'function') refreshAllUserItemWorlds();
    document.querySelectorAll('.lib-world-badge[data-wid]').forEach(btn => {
        const wid  = btn.getAttribute('data-wid');
        const info = worldInfoCache[wid];
        if (info) {
            const thumbEl = btn.querySelector('.lib-world-badge-thumb');
            const textEl  = btn.querySelector('.lib-world-badge-text');
            if (thumbEl && info.thumbnailImageUrl) thumbEl.style.backgroundImage = `url('${info.thumbnailImageUrl}')`;
            if (textEl) textEl.textContent = info.name || t('library.view_world', 'View World');
        }
    });
}

// Folder filter.
function updateFolderFilterOptions(fs) {
    const s = document.getElementById('libFolderFilter'), c = s.value;
    s.innerHTML = `<option value="__all__">${t('library.filters.all_folders', 'All Folders')}</option>`;
    (fs || []).forEach(f => {
        const n = f.split(/[\\\\/]/).pop() || f;
        s.innerHTML += `<option value="${esc(f)}">${esc(n)}</option>`;
    });
    s.value = c || '__all__';
    if (s._vnRefresh) s._vnRefresh();
}

// Favorites / hidden.
function toggleFavFilter() {
    showFavOnly = !showFavOnly;
    document.getElementById('libFavBtn').classList.toggle('active', showFavOnly);
    _renderLibIconSelects();
    filterLibrary();
}

function toggleFavorite(p) {
    if (favorites.has(p)) {
        favorites.delete(p);
        sendToCS({ action: 'removeFavorite', path: p });
    } else {
        favorites.add(p);
        sendToCS({ action: 'addFavorite', path: p });
    }
    filterLibrary(true); // stay on current page
    if (typeof _wdRenderPhotosPage === 'function') _wdRenderPhotosPage();
    // Refresh photo detail modal info pane if it's showing this photo
    const photoModal = document.getElementById('photoDetailModal');
    if (photoModal && _photoState.item?.path === p) {
        const infoPane = photoModal.querySelector('.photo-detail-info-pane');
        if (infoPane) infoPane.innerHTML = _photoBuildInfoPaneContent(_photoState.item);
    }
}

function toggleHidden(p) {
    if (hiddenMedia.has(p)) {
        hiddenMedia.delete(p);
    } else {
        hiddenMedia.add(p);
    }
    try { localStorage.setItem('vrcnext_hidden', JSON.stringify([...hiddenMedia])); } catch {}
    filterLibrary(true); // stay on current page
    renderDashRecentPhotos();
    if (typeof _wdRenderPhotosPage === 'function') _wdRenderPhotosPage();
    const photoModal = document.getElementById('photoDetailModal');
    if (photoModal && _photoState.item?.path === p) {
        _photoState.revealed = false;
        _photoApplyBlur(photoModal, _photoState.item);
    }
}

async function setLibItemAsDashBg(path, url) {
    if (path.toLowerCase().endsWith('.mp4') && url) {
        try {
            const resp = await fetch(url, { method: 'HEAD' });
            const size = parseInt(resp.headers.get('content-length') || '0');
            if (size > 60 * 1024 * 1024) {
                showToast(false, t('library.bg_video_too_large', 'Video must be under 60 MB'));
                return;
            }
        } catch { /* proceed if check fails */ }
    }
    dashBgPath = path;
    dashBgDataUri = '';
    dashBgSample = '';
    sendToCS({ action: 'vrcLoadDashBg', path });
    const nameEl = document.getElementById('dashBgName');
    if (nameEl) nameEl.textContent = path.split(/[/\\]/).pop();
    renderDashboard();
    if (typeof renderDashBgPreview === 'function') renderDashBgPreview();
    autoSave();
    showToast(true, t('library.background_updated', 'Background updated'));
}

// Video thumbnail.

// Photo detail modal — image on the left, info card on the right.
// Accepts: number (libraryFiles index), string (file path → looked up in libraryFiles), or item object.
const _photoState = { scale: 1, rotation: 0, tx: 0, ty: 0, item: null, drag: null, revealed: false, shownPath: '', navOwned: false };
let _photoResizeHandler = null;
let _photoKeyHandler = null;
const _photoNavCache = {};

function openPhotoDetail(target) {
    let x;
    if (typeof target === 'number')      x = libraryFiles[target];
    else if (typeof target === 'string') x = libraryFiles.find(f => f.path === target) || _photoNavCache[target];
    else                                  x = target;
    if (!x) return;

    if (x.path) _photoNavCache[x.path] = x;
    if (x.path && !x.remote) requestMediaTags();

    _photoState.item = x;
    _photoState.scale = 1;
    _photoState.rotation = 0;
    _photoState.tx = 0;
    _photoState.ty = 0;
    _photoState.drag = null;
    _photoState.revealed = false;

    _photoState.navOwned = !!x.path;
    if (x.path && typeof navSetCurrent === 'function') {
        navSetCurrent('photo', x.path);
        if (typeof navUpdateLabel === 'function') navUpdateLabel(x.name || '');
    }
    if (x.path && !x.remote && !window._isLinuxUi && !photoRatings.has(x.path)) {
        sendToCS({ action: 'getPhotoRating', path: x.path });
    }

    const existing = document.getElementById('photoDetailModal');
    if (existing) {
        _photoRenderContent(existing, x);
        _photoApplyTransform();
    } else {
        _photoCreateModal(x);
    }
}

function _photoIsBlurred(x) {
    return !_photoState.revealed
        && !!(x && x.path)
        && typeof hiddenMedia !== 'undefined'
        && hiddenMedia.has(x.path);
}

function _photoRevealClick(e) {
    if (e.target.closest('.photo-detail-toolbar, .pd-video-controls-mount')) return;
    if (!_photoIsBlurred(_photoState.item)) return;
    e.stopPropagation();
    _photoState.revealed = true;
    const modal = document.getElementById('photoDetailModal');
    if (modal) _photoApplyBlur(modal, _photoState.item);
}

function _photoApplyBlur(modal, x) {
    const pane = modal.querySelector('.photo-detail-img-pane');
    if (pane) pane.classList.toggle('pd-blurred', _photoIsBlurred(x));
}

function _photoCreateModal(x) {
    const o = document.createElement('div');
    o.className = 'modal-overlay photo-detail-overlay';
    o.id        = 'photoDetailModal';
    o.onclick   = e => { if (e.target === o) closePhotoDetail(); };
    o.innerHTML = `<div class="photo-detail-box">
        ${renderModalBar(x?.name || t('timeline.photo', 'Photo'), [modalCloseAction('closePhotoDetail()')], { flush: true })}
        <div class="photo-detail-panes">
            <div class="photo-detail-img-pane">
                <img class="photo-detail-img" alt="" draggable="false" style="display:none;" onerror="this.style.display='none'">
                <video class="photo-detail-video" playsinline style="display:none;"></video>
                <div class="pd-usertag-layer"></div>
                <div class="pd-blur-hint"><span class="msi">visibility_off</span><span>${esc(t('library.detail.click_to_reveal', 'Click to reveal'))}</span></div>
                <div class="pd-video-controls-mount"></div>
                <div class="photo-detail-toolbar-mount"></div>
            </div>
            <div class="photo-detail-info-pane"></div>
        </div>
    </div>`;
    document.body.appendChild(o);

    _photoRenderContent(o, x);

    const imgPane = o.querySelector('.photo-detail-img-pane');
    if (imgPane) {
        imgPane.addEventListener('wheel',     _photoOnWheel, { passive: false });
        imgPane.addEventListener('mousedown', _photoOnMouseDown);
        imgPane.addEventListener('click',     _photoRevealClick);
    }

    const ok = e => {
        if (e.key === 'Escape')          { closePhotoDetail(); }
        else if (e.key === 'ArrowLeft')  { e.preventDefault(); photoNavPrev(); }
        else if (e.key === 'ArrowRight') { e.preventDefault(); photoNavNext(); }
    };
    document.addEventListener('keydown', ok);
    _photoKeyHandler = ok;

    _photoResizeHandler = () => _photoRenderUserTags();
    window.addEventListener('resize', _photoResizeHandler);
}

function _photoRenderContent(modal, x) {
    const isVid = x.type === 'video';
    const box = modal.querySelector('.photo-detail-box');
    if (box) {
        box.classList.toggle('pd-is-video', isVid);
        box.classList.toggle('pd-no-info', !!x.remote);
    }

    const barTitle = modal.querySelector('.fd-modal-bar-title');
    if (barTitle) {
        const label = x.name || t('timeline.photo', 'Photo');
        barTitle.textContent = label;
        barTitle.title = label;
    }

    const prevPath = _photoState.shownPath || '';
    if (prevPath && prevPath !== x.path && typeof hiddenMedia !== 'undefined' && hiddenMedia.has(prevPath)) {
        const oldImg = modal.querySelector('.photo-detail-img');
        const oldVid = modal.querySelector('.photo-detail-video');
        if (oldImg) oldImg.src = PLACEHOLDER;
        if (oldVid) {
            try { oldVid.pause(); } catch {}
            oldVid.removeAttribute('src');
            try { oldVid.load(); } catch {}
        }
    }
    _photoState.shownPath = x.path || '';

    _photoApplyBlur(modal, x);

    const imgPane = modal.querySelector('.photo-detail-img-pane');
    if (imgPane) {
        imgPane.dataset.path = x.path || '';
        imgPane.dataset.url  = x.url  || '';
        imgPane.dataset.type = x.type || 'image';
        imgPane.dataset.name = x.name || '';
    }

    const imgEl   = modal.querySelector('.photo-detail-img');
    const vidEl   = modal.querySelector('.photo-detail-video');
    const vcMount = modal.querySelector('.pd-video-controls-mount');

    if (isVid) {
        if (imgEl) { imgEl.style.display = 'none'; imgEl.removeAttribute('src'); }
        if (vidEl) {
            const url = x.url || '';
            vidEl.style.display = '';
            if (vidEl.getAttribute('src') !== url) vidEl.src = url;
        }
        if (vcMount) vcMount.innerHTML = _photoBuildVideoControls();
        _photoSetupVideo(vidEl, vcMount);
    } else {
        if (vidEl) {
            try { vidEl.pause(); } catch {}
            if (vidEl._pdCleanup) { vidEl._pdCleanup(); vidEl._pdCleanup = null; }
            vidEl.removeAttribute('src');
            vidEl.style.display = 'none';
        }
        if (vcMount) vcMount.innerHTML = '';
        if (imgEl) {
            const url = x.url || '';
            imgEl.style.transform = '';
            if (url) {
                imgEl.style.display = '';
                if (imgEl.getAttribute('src') !== url) imgEl.src = url;
            } else {
                imgEl.style.display = 'none';
                imgEl.removeAttribute('src');
            }
        }
    }

    const oldToolbar = modal.querySelector('.photo-detail-toolbar, .photo-detail-toolbar-mount');
    if (oldToolbar) oldToolbar.outerHTML = _photoBuildToolbar(x);

    const infoPane = modal.querySelector('.photo-detail-info-pane');
    if (infoPane) infoPane.innerHTML = x.remote ? '' : _photoBuildInfoPaneContent(x);

    if (imgEl && !isVid) {
        if (!imgEl._pdTagLoadHooked) {
            imgEl.addEventListener('load', () => _photoRenderUserTags());
            imgEl._pdTagLoadHooked = true;
        }
        if (window.ResizeObserver && !imgEl._pdTagRO) {
            imgEl._pdTagRO = new ResizeObserver(() => _photoRenderUserTags());
            imgEl._pdTagRO.observe(imgEl);
        }
    }
    _photoRenderUserTags();
    requestAnimationFrame(() => _photoRenderUserTags());
}

function _photoBuildVideoControls() {
    return `<div class="pd-video-controls" onmousedown="event.stopPropagation()">
        <button class="pd-vc-btn pd-vc-play" title="${esc(t('library.detail.play', 'Play / Pause'))}"><span class="msi">play_arrow</span></button>
        <span class="pd-vc-time pd-vc-cur">0:00</span>
        <input type="range" class="pd-vc-seek" min="0" max="0" value="0" step="0.1">
        <span class="pd-vc-time pd-vc-dur">0:00</span>
        <button class="pd-vc-btn pd-vc-mute" title="${esc(t('library.detail.mute', 'Mute'))}"><span class="msi">volume_up</span></button>
        <input type="range" class="pd-vc-vol" min="0" max="1" value="1" step="0.01">
        <button class="pd-vc-btn pd-vc-full" title="${esc(t('library.detail.fullscreen', 'Fullscreen'))}"><span class="msi">fullscreen</span></button>
    </div>`;
}

function _photoSetupVideo(video, mount) {
    if (!video || !mount) return;
    if (video._pdCleanup) { video._pdCleanup(); video._pdCleanup = null; }

    const q = s => mount.querySelector(s);
    const playBtn = q('.pd-vc-play'), seek = q('.pd-vc-seek'), curEl = q('.pd-vc-cur'),
          durEl = q('.pd-vc-dur'), muteBtn = q('.pd-vc-mute'), volEl = q('.pd-vc-vol'), fullBtn = q('.pd-vc-full');

    const fmt = s => { if (!isFinite(s) || s < 0) return '0:00'; s = Math.floor(s); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`; };
    const setPlayIcon = () => { const i = playBtn?.querySelector('.msi'); if (i) i.textContent = video.paused ? 'play_arrow' : 'pause'; };
    const setMuteIcon = () => { const i = muteBtn?.querySelector('.msi'); if (i) i.textContent = (video.muted || !video.volume) ? 'volume_off' : (video.volume < 0.5 ? 'volume_down' : 'volume_up'); };
    const togglePlay = () => { if (video.paused) video.play().catch(() => {}); else video.pause(); };
    const fill = (el, pct) => el && el.style.setProperty('--pd-fill', Math.max(0, Math.min(100, pct)) + '%');
    const fillSeek = () => { const max = Number(seek?.max) || 0; fill(seek, max ? (Number(seek.value) / max) * 100 : 0); };
    const fillVol  = () => fill(volEl, (video.muted ? 0 : video.volume) * 100);

    let seeking = false;
    const onMeta = () => { if (durEl) durEl.textContent = fmt(video.duration); if (seek) seek.max = String(Math.max(0, Math.floor(video.duration))); fillSeek(); };
    const onTime = () => { if (seek && !seeking) seek.value = String(video.currentTime); if (curEl) curEl.textContent = fmt(video.currentTime); fillSeek(); };
    const onVol  = () => { setMuteIcon(); if (volEl && !volEl.matches(':active')) volEl.value = String(video.muted ? 0 : video.volume); fillVol(); };

    const handlers = [
        [video,   'loadedmetadata', onMeta],
        [video,   'durationchange', onMeta],
        [video,   'timeupdate',     onTime],
        [video,   'play',           setPlayIcon],
        [video,   'pause',          setPlayIcon],
        [video,   'ended',          setPlayIcon],
        [video,   'volumechange',   onVol],
        [video,   'click',          togglePlay],
        [playBtn, 'click',          e => { e.stopPropagation(); togglePlay(); }],
        [seek,    'input',          () => { seeking = true; video.currentTime = Number(seek.value); if (curEl) curEl.textContent = fmt(video.currentTime); fillSeek(); }],
        [seek,    'change',         () => { seeking = false; }],
        [muteBtn, 'click',          e => { e.stopPropagation(); video.muted = !video.muted; if (!video.muted && !video.volume) video.volume = 1; }],
        [volEl,   'input',          () => { video.volume = Number(volEl.value); video.muted = Number(volEl.value) === 0; fillVol(); }],
        [fullBtn, 'click',          e => { e.stopPropagation(); const tgt = video.closest('.photo-detail-img-pane') || video; if (document.fullscreenElement) document.exitFullscreen(); else (tgt.requestFullscreen || video.requestFullscreen)?.call(tgt.requestFullscreen ? tgt : video); }],
    ];
    handlers.forEach(([el, ev, fn]) => el && el.addEventListener(ev, fn));

    onMeta(); onTime(); setPlayIcon(); setMuteIcon();
    if (volEl) volEl.value = String(video.muted ? 0 : video.volume);
    fillVol();

    video._pdCleanup = () => handlers.forEach(([el, ev, fn]) => el && el.removeEventListener(ev, fn));
}

function _photoBuildToolbar(x) {
    const sameKind = f => (f.type === 'video') === (x.type === 'video');
    const inFilt = x.path ? _libFiltered.some(f => f.path === x.path) : false;
    const navList = x.path ? (inFilt ? _libFiltered : libraryFiles).filter(sameKind) : [];
    const navIdx  = x.path ? navList.findIndex(f => f.path === x.path) : -1;
    const prevDisabled = (navIdx <= 0)                              ? ' disabled' : '';
    const nextDisabled = (navIdx < 0 || navIdx >= navList.length-1) ? ' disabled' : '';

    const navBtns = x.remote
        ? { prev: '', next: '' }
        : {
            prev: `<button class="pdt-btn" onclick="photoNavPrev()" title="${esc(t('library.detail.prev', 'Previous'))}"${prevDisabled}><span class="msi">chevron_left</span></button><span class="pdt-sep"></span>`,
            next: `<span class="pdt-sep"></span><button class="pdt-btn" onclick="photoNavNext()" title="${esc(t('library.detail.next', 'Next'))}"${nextDisabled}><span class="msi">chevron_right</span></button>`,
        };

    const copyBtns = x.remote
        ? `<button class="pdt-btn" onclick="photoDownload()" title="${esc(t('context_menu.image.download', 'Download Image'))}"><span class="msi">download</span></button>
           <button class="pdt-btn" onclick="photoCopyImage()" title="${esc(t('library.actions.copy_clipboard', 'Copy to clipboard'))}"><span class="msi">content_copy</span></button>`
        : `<button class="pdt-btn" onclick="photoCopy()" title="${esc(t('library.actions.copy_clipboard', 'Copy to clipboard'))}"><span class="msi">content_copy</span></button>`;

    return `<div class="photo-detail-toolbar" onmousedown="event.stopPropagation()">
        ${navBtns.prev}
        ${copyBtns}
        <button class="pdt-btn" onclick="photoZoom(1.25)" title="${esc(t('library.detail.zoom_in', 'Zoom In'))}"><span class="msi">zoom_in</span></button>
        <button class="pdt-btn" onclick="photoZoom(0.8)" title="${esc(t('library.detail.zoom_out', 'Zoom Out'))}"><span class="msi">zoom_out</span></button>
        <button class="pdt-btn" onclick="photoRotate(-90)" title="${esc(t('library.detail.rotate_left', 'Rotate Left'))}"><span class="msi">rotate_left</span></button>
        <button class="pdt-btn" onclick="photoRotate(90)" title="${esc(t('library.detail.rotate_right', 'Rotate Right'))}"><span class="msi">rotate_right</span></button>
        <button class="pdt-btn" onclick="photoReset()" title="${esc(t('library.detail.reset', 'Reset'))}"><span class="msi">refresh</span></button>
        ${navBtns.next}
    </div>`;
}

function _photoBuildInfoPaneContent(x) {
    const players   = x.players || [];
    const worldId   = x.worldId || '';
    const wInfo     = worldId ? worldInfoCache[worldId] : null;
    const worldName = wInfo?.name || worldId || '';
    const date      = new Date(x.modified);
    const dateStr   = fmtLongDate(date);
    const timeStr   = fmtTime(date);
    const resTag    = _resTag(x);
    const resStr    = (x.imgW && x.imgH)
        ? (resTag ? `${resTag} (${x.imgW}×${x.imgH})` : `${x.imgW}×${x.imgH}`)
        : resTag;

    const worldRowClick = worldId ? ` onclick="navOpenModal('worldSearch','${jsq(worldId)}','${jsq(worldName || '')}')"` : '';
    const worldCursor   = worldId ? 'cursor:pointer;' : '';
    const isFav = x.path && (typeof favorites !== 'undefined') && favorites.has(x.path);
    const favBadge = `<span class="vrcn-badge accent"><span class="msi" style="font-size:11px;">favorite</span>${esc(t('library.detail.favorited', 'Favorited'))}</span>`;

    const authorName = x.authorName || '';
    const authorId   = x.authorId   || '';
    let authorRow = '';
    if (authorName) {
        const authorLabel = esc(t('library.detail.author', 'Author'));
        if (authorId) {
            authorRow = `<div style="display:flex;justify-content:space-between;gap:8px;align-items:baseline;font-size:calc(11px + var(--fs-off, 0px));cursor:pointer;" onclick="navOpenModal('friend','${jsq(authorId)}','${jsq(authorName)}')"><span style="color:var(--tx2);">${authorLabel}</span><span style="color:var(--accent-lt);font-weight:700;text-align:right;">${esc(authorName)}</span></div>`;
        } else {
            authorRow = _tlMr(authorLabel, `<span style="font-weight:700;">${esc(authorName)}</span>`);
        }
    }

    const mTags = x.path ? getMediaTags(x.path) : [];
    const tagsValue = mTags.length
        ? mTags.map(tag => `<span class="vrcn-badge pd-tag-badge">${esc(mediaTagLabel(tag))}</span>`).join('')
        : `<span style="color:var(--tx3);">${esc(t('library.detail.no_tags', 'None'))}</span>`;

    const infoRows = [
        _tlMr(esc(t('library.detail.date', 'Date')), esc(dateStr)),
        _tlMr(esc(t('library.detail.time', 'Time')), esc(timeStr)),
        x.size ? _tlMr(esc(t('library.detail.size', 'Size')), esc(x.size)) : '',
        worldName ? `<div style="display:flex;justify-content:space-between;gap:8px;align-items:baseline;font-size:calc(11px + var(--fs-off, 0px));${worldCursor}"${worldRowClick}><span style="color:var(--tx2);">${esc(t('library.detail.world', 'World'))}</span><span style="color:var(--accent-lt);font-weight:700;text-align:right;">${esc(worldName)}</span></div>` : '',
        resStr ? _tlMr(esc(t('library.detail.resolution', 'Resolution')), esc(resStr)) : '',
        authorRow,
        isFav ? _tlMr(esc(t('library.detail.favorited', 'Favorited')), favBadge) : '',
        (x.path && !x.remote) ? _tlMr(esc(t('library.detail.tags', 'Tags')), `<span class="pd-tag-row">${tagsValue}</span>`) : '',
    ].filter(Boolean).join('');

    let playersHtml = '';
    if (players.length > 0) {
        let grid = '';
        players.forEach(p => {
            const name    = p.displayName || '?';
            const isOwn   = currentVrcUser && p.userId === currentVrcUser.id;
            const live    = isOwn ? currentVrcUser : (p.userId ? vrcFriendsData.find(f => f.id === p.userId) : null);
            const image   = live?.image || p.image || '';
            const av      = image
                ? `<div class="tl-player-card-av" style="background-image:url('${cssUrl(imgThumb(image, 64))}')"></div>`
                : `<div class="tl-player-card-av">${esc(name[0].toUpperCase())}</div>`;
            const badge   = live ? `<span class="vrcn-badge bdg-friend"><span class="msi" style="font-size:10px;">check_circle</span>${t('profiles.badges.friend', 'Friend')}</span>` : '';
            const onclick = p.userId ? `onclick="navOpenModal('friend','${jsq(p.userId)}','${jsq(name)}')"` : '';
            const clickCls = p.userId ? ' clickable' : '';
            grid += `<div class="tl-player-card${clickCls}" ${onclick}>${av}<div class="tl-player-card-info"><div class="tl-player-card-name"><span>${esc(name)}</span>${badge}</div></div></div>`;
        });
        playersHtml = `<div class="fd-info-card photo-detail-players-card">
            <div class="fd-group-rep-label">${tf('library.detail.players_title', { count: players.length }, 'PLAYERS IN INSTANCE ({count})')}</div>
            <div class="tl-players-grid photo-detail-players-grid">${grid}</div>
        </div>`;
    }

    return `<h2 class="photo-detail-name">${esc(x.name)}</h2>
        ${_tlInfoCard(esc(t('library.detail.info', 'Info')), infoRows)}
        ${_photoBuildRatingCard(x)}
        ${_photoBuildUserTagsCard(x)}
        ${playersHtml}`;
}

function _photoBuildUserTagsCard(x) {
    if (!x.path || x.remote) return '';
    const list = getMediaUserTags(x.path);
    if (!list.length) return '';
    const rows = list.map(u => {
        const fr = (typeof vrcFriendsData !== 'undefined') ? vrcFriendsData.find(f => f.id === u.userId) : null;
        const name = fr?.displayName || u.displayName || u.userId;
        const av = fr?.image
            ? `<div class="tl-player-card-av" style="background-image:url('${cssUrl(imgThumb(fr.image, 64))}')"></div>`
            : `<div class="tl-player-card-av">${esc((name || '?')[0].toUpperCase())}</div>`;
        return `<div class="tl-player-card clickable" onclick="navOpenModal('friend','${jsq(u.userId)}','${jsq(name)}')">${av}
            <div class="tl-player-card-info"><div class="tl-player-card-name"><span>${esc(name)}</span></div></div>
            <span class="msi pd-ut-card-remove" title="${esc(t('library.user_tags.remove', 'Remove Tag'))}" onclick="event.stopPropagation();removeMediaUserTag('${jsq(x.path)}','${jsq(u.userId)}')">close</span>
        </div>`;
    }).join('');
    return `<div class="fd-info-card">
        <div class="fd-group-rep-label">${tf('library.user_tags.card_title', { count: list.length }, 'TAGGED USERS ({count})')}</div>
        <div class="tl-players-grid photo-detail-players-grid">${rows}</div>
    </div>`;
}

function _photoBuildRatingCard(x) {
    if (!x.path || x.remote || window._isLinuxUi) return '';
    const stars = photoRatings.get(x.path) || 0;
    let hearts = '';
    for (let i = 1; i <= 5; i++) {
        const filled = i <= stars;
        hearts += `<span class="msi pd-rating-heart${filled ? ' pd-rating-filled' : ''}" onclick="setPhotoRatingClick('${jsq(x.path)}', ${i})">favorite</span>`;
    }
    return `<div class="fd-info-card pd-rating-card">
        <div class="fd-group-rep-label">${esc(t('library.detail.rating', 'Photo Rating'))}</div>
        <div class="pd-rating-hearts">${hearts}</div>
    </div>`;
}

function setPhotoRatingClick(path, n) {
    const current = photoRatings.get(path) || 0;
    setPhotoRatingValue(path, current === n ? 0 : n);
}

function setPhotoRatingValue(path, stars) {
    const targets = _libEditTargets(path);
    targets.forEach(p => {
        photoRatings.set(p, stars);
        sendToCS({ action: 'setPhotoRating', path: p, stars });
    });
    _photoRefreshInfoPaneIfShowing(path);
    _renderLibRatingSelect();
    if (_libRatingFilter !== '__all__') filterLibrary(true);
    else targets.forEach(p => _libUpdateCardMeta(p));
}

function _libUpdateCardMeta(path) {
    if (!path) return;
    const item = libraryFiles.find(f => f.path === path);
    if (!item) return;
    document.querySelectorAll(`.lib-card[data-path="${CSS.escape(path)}"] .lib-meta-left`)
        .forEach(el => { el.innerHTML = _libMetaHtml(item); });
}

function _photoRefreshInfoPaneIfShowing(path) {
    const photoModal = document.getElementById('photoDetailModal');
    if (photoModal && _photoState.item?.path === path) {
        const infoPane = photoModal.querySelector('.photo-detail-info-pane');
        if (infoPane) infoPane.innerHTML = _photoBuildInfoPaneContent(_photoState.item);
    }
}

function onPhotoRating(payload) {
    if (!payload || !payload.path) return;
    photoRatings.set(payload.path, payload.stars || 0);
    _photoRefreshInfoPaneIfShowing(payload.path);
    _renderLibRatingSelect();
    if (_libRatingFilter !== '__all__') filterLibrary(true);
    else _libUpdateCardMeta(payload.path);
}

function onLibraryRatings(payload) {
    if (!payload) return;
    Object.entries(payload).forEach(([path, stars]) => photoRatings.set(path, stars));
    if (_libRatingsRenderTimer) clearTimeout(_libRatingsRenderTimer);
    _libRatingsRenderTimer = setTimeout(() => {
        _libRatingsRenderTimer = null;
        _renderLibRatingSelect();
        if (_libRatingFilter !== '__all__') filterLibrary(true);
        else Object.keys(payload).forEach(_libUpdateCardMeta);
    }, 150);
}

function closePhotoDetail(fromNav = false) {
    const clearNav = !fromNav && _photoState.navOwned && typeof navClear === 'function';
    const m = document.getElementById('photoDetailModal');
    if (!m) { if (clearNav) navClear(); return; }
    if (_photoKeyHandler) { document.removeEventListener('keydown', _photoKeyHandler); _photoKeyHandler = null; }
    if (_photoResizeHandler) { window.removeEventListener('resize', _photoResizeHandler); _photoResizeHandler = null; }
    document.removeEventListener('mousemove', _photoOnMouseMove);
    document.removeEventListener('mouseup',   _photoOnMouseUp);
    _photoState.drag = null;
    const tagImg = m.querySelector('.photo-detail-img');
    if (tagImg?._pdTagRO) { try { tagImg._pdTagRO.disconnect(); } catch {} tagImg._pdTagRO = null; }
    const vid = m.querySelector('.photo-detail-video');
    if (vid) { try { vid.pause(); } catch {} if (vid._pdCleanup) vid._pdCleanup(); vid.removeAttribute('src'); }
    m.querySelectorAll('img').forEach(img => { img.src = PLACEHOLDER; });
    m.remove();
    _photoState.shownPath = '';
    _photoState.navOwned = false;
    if (clearNav) navClear();
}

// === Photo modal interactions ===
function _photoApplyTransform() {
    const img = document.querySelector('#photoDetailModal .photo-detail-img');
    if (!img) return;
    const s = _photoState;
    img.style.transform = `translate(${s.tx}px, ${s.ty}px) scale(${s.scale}) rotate(${s.rotation}deg)`;
    _photoRenderUserTags();
}

// The tag layer mirrors the image's untransformed layout box and reuses the very
// same CSS transform, so markers stay pinned to their pixels through zoom, pan and
// rotation without measuring the transformed bounding box.
function _photoRenderUserTags() {
    const modal = document.getElementById('photoDetailModal');
    const layer = modal?.querySelector('.pd-usertag-layer');
    if (!layer) return;

    const x = _photoState.item;
    const img = modal.querySelector('.photo-detail-img');
    const tags = (x && x.path && !x.remote) ? getMediaUserTags(x.path) : [];

    if (!tags.length || !img || img.style.display === 'none' || !img.offsetWidth || !img.offsetHeight) {
        layer.innerHTML = '';
        layer.style.display = 'none';
        return;
    }

    const s = _photoState;
    layer.style.display = '';
    layer.style.left      = img.offsetLeft   + 'px';
    layer.style.top       = img.offsetTop    + 'px';
    layer.style.width     = img.offsetWidth  + 'px';
    layer.style.height    = img.offsetHeight + 'px';
    layer.style.transform = `translate(${s.tx}px, ${s.ty}px) scale(${s.scale}) rotate(${s.rotation}deg)`;

    // Counter the layer transform so the marker keeps its screen size and stays upright.
    const counter = `translate(-50%, -50%) rotate(${-s.rotation}deg) scale(${1 / s.scale})`;

    layer.innerHTML = tags.map(u => {
        const fr = (typeof vrcFriendsData !== 'undefined') ? vrcFriendsData.find(f => f.id === u.userId) : null;
        const name = fr?.displayName || u.displayName || u.userId;
        const av = fr?.image
            ? `<span class="pd-ut-av" style="background-image:url('${cssUrl(imgThumb(fr.image, 64))}')"></span>`
            : `<span class="pd-ut-av pd-ut-av-letter">${esc((name || '?')[0].toUpperCase())}</span>`;
        return `<div class="pd-usertag" style="left:${(u.x * 100).toFixed(3)}%;top:${(u.y * 100).toFixed(3)}%;transform:${counter};">
            <span class="pd-ut-frame"></span>
            <div class="pd-ut-chip">
                ${av}
                <span class="pd-ut-name" onclick="event.stopPropagation();navOpenModal('friend','${jsq(u.userId)}','${jsq(name)}')">${esc(name)}</span>
                <span class="msi pd-ut-remove" title="${esc(t('library.user_tags.remove', 'Remove Tag'))}" onclick="event.stopPropagation();removeMediaUserTag('${jsq(x.path)}','${jsq(u.userId)}')">close</span>
            </div>
        </div>`;
    }).join('');
}

// Inverse of the image transform: viewport point -> 0..1 inside the picture.
function _photoPointToImage(clientX, clientY) {
    const modal = document.getElementById('photoDetailModal');
    const img = modal?.querySelector('.photo-detail-img');
    const pane = modal?.querySelector('.photo-detail-img-pane');
    if (!img || !pane || !img.offsetWidth || !img.offsetHeight) return null;

    const pr = pane.getBoundingClientRect();
    const s = _photoState;
    const cx = img.offsetLeft + img.offsetWidth  / 2 + s.tx;
    const cy = img.offsetTop  + img.offsetHeight / 2 + s.ty;

    const dx = ((clientX - pr.left) - cx) / s.scale;
    const dy = ((clientY - pr.top)  - cy) / s.scale;

    const rad = -(s.rotation || 0) * Math.PI / 180;
    const rx = dx * Math.cos(rad) - dy * Math.sin(rad);
    const ry = dx * Math.sin(rad) + dy * Math.cos(rad);

    return {
        x: (rx + img.offsetWidth  / 2) / img.offsetWidth,
        y: (ry + img.offsetHeight / 2) / img.offsetHeight,
    };
}

function removeMediaUserTag(path, userId) {
    if (!path || !userId) return;
    const list = getMediaUserTags(path).filter(u => u.userId !== userId);
    if (list.length) mediaUserTags.set(path, list);
    else mediaUserTags.delete(path);
    sendToCS({ action: 'removeMediaUserTag', path, userId });
    _photoRefreshTagUi();
    _libRefilter();
}

// Called from the photo detail context menu. sx/sy are 0..1 inside the visible
// image box; they get converted to unrotated image space before storing.
let _userTagDraft = null;
let _userTagFilter = '';

function openUserTagPicker(path, clientX, clientY) {
    if (!path) return;
    const pos = _photoPointToImage(clientX, clientY);
    if (!pos || pos.x < 0 || pos.x > 1 || pos.y < 0 || pos.y > 1) return;
    _userTagDraft = { path, x: pos.x, y: pos.y };
    _userTagFilter = '';
    _renderUserTagPicker();
}

function closeUserTagPicker() {
    document.getElementById('userTagPickerModal')?.remove();
    _userTagDraft = null;
}

function filterUserTagPicker(value) {
    _userTagFilter = value || '';
    _renderUserTagPickerList();
}

function _renderUserTagPicker() {
    closeUserTagPickerKeepDraft();
    const o = document.createElement('div');
    o.className = 'modal-overlay';
    o.id = 'userTagPickerModal';
    o.onclick = e => { if (e.target === o) closeUserTagPicker(); };
    o.innerHTML = `<div class="modal-box inv-box">
        ${renderModalBar(t('library.user_tags.pick_title', 'Tag a Friend'), [modalCloseAction('closeUserTagPicker()')], { flush: true })}
        <div class="inv-search-wrap">
            <span class="msi inv-search-icon">search</span>
            <input type="text" id="userTagSearch" class="inv-search-input" placeholder="${esc(t('invite.multi.search_placeholder', 'Search friends...'))}" oninput="filterUserTagPicker(this.value)">
        </div>
        <div id="userTagPickerList" class="inv-list"></div>
    </div>`;
    document.body.appendChild(o);
    _renderUserTagPickerList();
    setTimeout(() => document.getElementById('userTagSearch')?.focus(), 30);
}

function closeUserTagPickerKeepDraft() {
    document.getElementById('userTagPickerModal')?.remove();
}

function _renderUserTagPickerList() {
    const el = document.getElementById('userTagPickerList');
    if (!el) return;
    const q = _userTagFilter.toLowerCase().trim();
    const tagged = new Set(getMediaUserTags(_userTagDraft?.path || '').map(u => u.userId));
    const friends = (typeof vrcFriendsData !== 'undefined' ? vrcFriendsData : [])
        .filter(f => !q || (f.displayName || '').toLowerCase().includes(q))
        .slice(0, 100);

    if (!friends.length) {
        el.innerHTML = `<div class="inv-empty">${esc(t('profiles.people.no_results', 'No results'))}</div>`;
        return;
    }

    el.innerHTML = friends.map(f => {
        const already = tagged.has(f.id);
        const trailing = already
            ? `<span class="msi" style="margin-left:auto;flex-shrink:0;font-size:15px;color:var(--accent);">check</span>` : '';
        return renderUserItem(f, `confirmUserTag('${jsq(f.id)}','${jsq(f.displayName || '')}')`, { trailing });
    }).join('');
}

function confirmUserTag(userId, displayName) {
    const draft = _userTagDraft;
    if (!draft || !userId) return;
    const list = getMediaUserTags(draft.path).filter(u => u.userId !== userId);
    list.push({ userId, displayName, x: draft.x, y: draft.y });
    mediaUserTags.set(draft.path, list);
    sendToCS({ action: 'setMediaUserTag', path: draft.path, userId, displayName, x: draft.x, y: draft.y });
    closeUserTagPicker();
    _photoRefreshTagUi();
    _libRefilter();
}

function photoZoom(factor) {
    _photoState.scale = Math.max(0.1, Math.min(20, _photoState.scale * factor));
    _photoApplyTransform();
}

function photoRotate(deg) {
    _photoState.rotation += deg;
    _photoApplyTransform();
}

function photoReset() {
    _photoState.scale    = 1;
    _photoState.rotation = 0;
    _photoState.tx       = 0;
    _photoState.ty       = 0;
    _photoApplyTransform();
}

function photoCopy() {
    const it = _photoState.item;
    if (!it || !it.path) return;
    copyToClipboard(it.url || '', it.path, it.type || 'image');
}

function photoDownload() {
    const it = _photoState.item;
    if (!it || !it.url) return;
    const guess = (it.url.split('?')[0].split('/').pop() || '').trim();
    const fileName = /\.[a-z0-9]{3,4}$/i.test(guess) ? guess : 'image.png';
    sendToCS({ action: 'invDownload', url: it.url, fileName });
}


function photoCopyImage() {
    const it = _photoState.item;
    if (!it || !it.url) return;
    sendToCS({ action: 'copyImageToClipboard', url: it.url });
}

function photoNavPrev() { _photoNav(-1); }
function photoNavNext() { _photoNav(1);  }

function _photoNav(dir) {
    const it = _photoState.item;
    if (!it || !it.path) return;
    const sameKind = f => (f.type === 'video') === (it.type === 'video');
    const inFilt = _libFiltered.some(f => f.path === it.path);
    const list   = (inFilt ? _libFiltered : libraryFiles).filter(sameKind);
    const idx    = list.findIndex(f => f.path === it.path);
    if (idx < 0) return;
    const next = list[idx + dir];
    if (next) openPhotoDetail(next);
}

function onLibraryFileDeleted(path) {
    const modal = document.getElementById('photoDetailModal');
    const showingDeleted = modal && _photoState.item?.path === path;

    // Pick the neighbour BEFORE removal, while the deleted item is still in the list.
    let neighbor = null;
    if (showingDeleted) {
        const delKind = _photoState.item?.type === 'video';
        const sameKind = f => (f.type === 'video') === delKind;
        const inFilt = _libFiltered.some(f => f.path === path);
        const list   = (inFilt ? _libFiltered : libraryFiles).filter(sameKind);
        const idx    = list.findIndex(f => f.path === path);
        if (idx >= 0) neighbor = list[idx + 1] || list[idx - 1] || null;
    }

    libraryFiles = libraryFiles.filter(f => f.path !== path);
    filterLibrary(true); // stay on current page after delete
    if (typeof navUpdateBadges === 'function') navUpdateBadges();

    if (typeof _wdOnFileDeleted === 'function') _wdOnFileDeleted(path);

    if (showingDeleted) {
        if (neighbor) openPhotoDetail(neighbor);
        else          closePhotoDetail();
    }
}

function _photoOnWheel(e) {
    if (_photoState.item?.type === 'video') return;
    e.preventDefault();
    const factor = e.deltaY < 0 ? 1.15 : 1/1.15;
    const pane = e.currentTarget;
    const rect = pane.getBoundingClientRect();
    const img  = pane.querySelector('img');
    const cx   = img ? (img.offsetLeft + img.offsetWidth  / 2) : rect.width  / 2;
    const cy   = img ? (img.offsetTop  + img.offsetHeight / 2) : rect.height / 2;
    const ox   = e.clientX - rect.left - cx;
    const oy   = e.clientY - rect.top  - cy;
    const oldScale = _photoState.scale;
    const newScale = Math.max(0.1, Math.min(20, oldScale * factor));
    _photoState.tx += (ox - _photoState.tx) * (1 - newScale / oldScale);
    _photoState.ty += (oy - _photoState.ty) * (1 - newScale / oldScale);
    _photoState.scale = newScale;
    _photoApplyTransform();
}

function _photoOnMouseDown(e) {
    if (e.button !== 0) return; // left button only — right/middle pass through (e.g. for context menu)
    if (_photoState.item?.type === 'video') return;
    if (e.target.closest('.photo-detail-toolbar')) return;
    _photoState.drag = { startX: e.clientX, startY: e.clientY, baseTx: _photoState.tx, baseTy: _photoState.ty };
    document.querySelector('#photoDetailModal .photo-detail-img-pane')?.classList.add('dragging');
    document.addEventListener('mousemove', _photoOnMouseMove);
    document.addEventListener('mouseup',   _photoOnMouseUp);
    e.preventDefault();
}

function _photoOnMouseMove(e) {
    const d = _photoState.drag;
    if (!d) return;
    _photoState.tx = d.baseTx + (e.clientX - d.startX);
    _photoState.ty = d.baseTy + (e.clientY - d.startY);
    _photoApplyTransform();
}

function _photoOnMouseUp() {
    _photoState.drag = null;
    document.querySelector('#photoDetailModal .photo-detail-img-pane')?.classList.remove('dragging');
    document.removeEventListener('mousemove', _photoOnMouseMove);
    document.removeEventListener('mouseup',   _photoOnMouseUp);
}

// Lightbox — reuses the photo modal without the info sidebar.
function openLightbox(u, kind) {
    if (!u) return;
    openPhotoDetail({
        name:     '',
        path:     '',
        url:      u,
        type:     kind === 'video' ? 'video' : 'image',
        modified: Date.now(),
        size:     '',
        players:  [],
        imgW: 0, imgH: 0,
        remote:   true,
    });
}

// Delete modal.
function showDeleteModal(fp, fn) {
    pendingDeletePath = fp;
    const x = document.getElementById('deleteModal');
    if (x) x.remove();
    const o = document.createElement('div');
    o.className = 'modal-overlay';
    o.style.display = 'flex'; // inline display required by _closeTopModal (Escape)
    o.id        = 'deleteModal';
    o.onclick   = e => { if (e.target === o) closeDeleteModal(); };
    o.innerHTML = `<div class="modal-box">${renderModalBar(t('library.delete.title', 'Delete File'), [modalCloseAction('closeDeleteModal()')])}<div class="modal-icon danger" style="margin-top:20px;"><span class="msi" style="font-size:22px;">delete</span></div><div class="modal-msg">${t('library.delete.message', 'Permanently delete from disk:')}<br><span class="modal-fname">${esc(fn)}</span></div><div class="modal-btns"><button class="vrcn-button-round vrcn-btn-danger" onclick="confirmDelete()">${t('library.delete.confirm', 'Delete')}</button></div></div>`;
    document.body.appendChild(o);
    o.querySelector('.fd-modal-bar-actions .fd-action-btn')?.focus();
    const ok = e => {
        if (e.key === 'Escape') { closeDeleteModal(); document.removeEventListener('keydown', ok); }
        if (e.key === 'Enter')  { confirmDelete();    document.removeEventListener('keydown', ok); }
    };
    document.addEventListener('keydown', ok);
}

function closeDeleteModal() {
    pendingDeletePath = null;
    const m = document.getElementById('deleteModal');
    if (m) m.remove();
}

function confirmDelete() {
    if (pendingDeletePath) {
        sendToCS({ action: 'deleteLibraryFile', path: pendingDeletePath });
        favorites.delete(pendingDeletePath);
    }
    closeDeleteModal();
}

function showDeleteAllModal() {
    if (!postedFiles.length) return;
    const x = document.getElementById('deleteModal');
    if (x) x.remove();
    const o = document.createElement('div');
    o.className = 'modal-overlay';
    o.style.display = 'flex'; // inline display required by _closeTopModal (Escape)
    o.id        = 'deleteModal';
    o.onclick   = e => { if (e.target === o) closeDeleteModal(); };
    o.innerHTML = `<div class="modal-box">${renderModalBar(t('library.delete_all.title', 'Delete All Posts'), [modalCloseAction('closeDeleteModal()')])}<div class="modal-icon danger" style="margin-top:20px;"><span class="msi" style="font-size:22px;">delete</span></div><div class="modal-msg">${tf('library.delete_all.message', { count: postedFiles.length }, 'Delete all {count} post(s) from Discord?')}</div><div class="modal-btns"><button class="vrcn-button-round vrcn-btn-danger" onclick="confirmDeleteAll()">${t('library.delete_all.confirm', 'Delete All')}</button></div></div>`;
    document.body.appendChild(o);
}

function confirmDeleteAll() {
    postedFiles.forEach(f => {
        if (f.messageId) sendToCS({ action: 'deletePost', messageId: f.messageId, webhookUrl: f.webhookUrl });
    });
    closeDeleteModal();
}

// Clipboard.
function copyToClipboard(_url, path, type) {
    sendToCS({ action: 'copyImageToClipboard', path });
}
