let _libTotal = 0;
let _libHasMore = false;
let _libLoading = false;
let _libObserver = null;

function refreshLibrary() {
    try {
        const raw = localStorage.getItem('vrcnext_lib_cache');
        if (raw) {
            const cached = JSON.parse(raw);
            if (cached.files && cached.files.length) {
                libraryFiles = cached.files;
                _libTotal = cached.total || cached.files.length;
                _libHasMore = false;
                filterLibrary();
                const cnt = document.getElementById('libCount');
                if (cnt) cnt.textContent = cached.files.length + ' / ' + _libTotal + ' files (refreshing…)';
            }
        }
    } catch {}
    sendToCS({ action: 'scanLibrary' });
}

function renderLibrary(data) {
    // Support both legacy plain array and new paginated format
    const files = Array.isArray(data) ? data : (data.files || []);
    libraryFiles = files;
    _libTotal = Array.isArray(data) ? files.length : (data.total || files.length);
    _libHasMore = Array.isArray(data) ? false : (data.hasMore || false);
    _libLoading = false;

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
    filterLibrary();
    _setupLibSentinel();
}

function appendLibraryPage(data) {
    const newFiles = data.files || [];
    _libTotal = data.total || _libTotal;
    _libHasMore = data.hasMore || false;
    _libLoading = false;
    if (!newFiles.length) return;

    newFiles.forEach(f => libraryFiles.push(f));
    _resolveWorldIds(newFiles);
    _appendLibCards(newFiles);
    _updateLibCount();
    if (_libHasMore) _setupLibSentinel();
    else _teardownLibSentinel();
}

function _resolveWorldIds(files) {
    const unknown = [...new Set((files || []).filter(x => x.worldId && !worldInfoCache[x.worldId]).map(x => x.worldId))];
    if (unknown.length > 0) sendToCS({ action: 'vrcResolveWorlds', worldIds: unknown.slice(0, 30) });
}

function _updateLibCount() {
    const cnt = document.getElementById('libCount');
    if (cnt) cnt.textContent = libraryFiles.length + ' / ' + _libTotal + ' files';
}

function _setupLibSentinel() {
    _teardownLibSentinel();
    const g = document.getElementById('libGrid');
    if (!g) return;
    let sentinel = document.getElementById('libSentinel');
    if (!sentinel) {
        sentinel = document.createElement('div');
        sentinel.id = 'libSentinel';
        sentinel.style.cssText = 'height:1px;width:100%;grid-column:1/-1;';
        g.appendChild(sentinel);
    }
    _libObserver = new IntersectionObserver(entries => {
        if (entries[0].isIntersecting && !_libLoading && _libHasMore) {
            _libLoading = true;
            sendToCS({ action: 'loadLibraryPage', offset: libraryFiles.length });
        }
    }, { rootMargin: '300px' });
    _libObserver.observe(sentinel);
}

function _teardownLibSentinel() {
    if (_libObserver) { _libObserver.disconnect(); _libObserver = null; }
    const s = document.getElementById('libSentinel');
    if (s) s.remove();
}

function _buildLibCard(x) {
    const su = x.url || '';
    const suAttr = esc(su);
    const suJs = jsq(su);
    const sp = jsq(x.path || '');
    const sn = jsq(x.name || '');
    const iF = favorites.has(x.path), fc = iF ? ' active' : '';
    const iH = hiddenMedia.has(x.path), hc = iH ? ' active' : '';
    const ac = ['lib-actions', iF ? 'has-fav' : '', iH ? 'has-hidden' : ''].filter(Boolean).join(' ');
    const acts = `<div class="${ac}"><button class="vrcn-lib-button clip" onclick="event.stopPropagation();copyToClipboard('${suJs}','${sp}','${x.type}')" title="Copy to clipboard"><span class="msi" style="font-size:16px;">content_copy</span></button><button class="vrcn-lib-button fav${fc}" onclick="event.stopPropagation();toggleFavorite('${sp}')" title="Favorite"><span class="msi" style="font-size:16px;">star</span></button><button class="vrcn-lib-button hide${hc}" onclick="event.stopPropagation();toggleHidden('${sp}')" title="${iH ? 'Unhide' : 'Hide'}"><span class="msi" style="font-size:16px;">${iH ? 'visibility' : 'visibility_off'}</span></button><button class="vrcn-lib-button del" onclick="event.stopPropagation();showDeleteModal('${sp}','${sn}')"><span class="msi" style="font-size:16px;">delete</span></button></div>`;
    const blurClass = iH ? ' lib-blurred' : '';
    const idx = libraryFiles.indexOf(x);
    if (x.type === 'image') {
        let worldBadge = '';
        if (x.worldId) {
            const wInfo = worldInfoCache[x.worldId];
            const wName = wInfo ? esc(wInfo.name) : 'View World';
            const wThumb = wInfo?.thumbnailImageUrl || '';
            worldBadge = `<button class="lib-world-badge" data-wid="${esc(x.worldId)}" onclick="event.stopPropagation();openWorldSearchDetail('${esc(x.worldId)}')" title="${wName}"><span class="lib-world-badge-thumb" style="${wThumb ? `background-image:url('${cssUrl(wThumb)}')` : ''}"></span><span class="lib-world-badge-text">${wName}</span></button>`;
        }
        let playersOverlay = '';
        const players = x.players || [];
        if (players.length > 0) {
            const show = players.slice(0, 3);
            const remaining = players.length - show.length;
            playersOverlay = `<div class="lib-players-overlay" onclick="event.stopPropagation();openPhotoDetail(${idx})">` +
                show.map(p => {
                    const fr = vrcFriendsData.find(f => f.id === p.userId);
                    const img = fr?.image || p.image || '';
                    return img ? `<div class="lib-player-av" style="background-image:url('${cssUrl(img)}')" title="${esc(p.displayName)}"></div>`
                               : `<div class="lib-player-av lib-player-av-letter" title="${esc(p.displayName)}">${esc((p.displayName||'?')[0])}</div>`;
                }).join('') +
                (remaining > 0 ? `<div class="lib-player-av lib-player-av-more">+${remaining}</div>` : '') +
                `</div>`;
        }
        return `<div class="lib-card">${acts}<div class="lib-thumb-wrap${blurClass}" onclick="openLightbox('${suJs}','image')"><img class="lib-thumb" src="${suAttr}" loading="lazy" onerror="this.outerHTML='<div style=\\'width:100%;height:100%;display:flex;align-items:center;justify-content:center;color:var(--tx3);font-size:11px;font-weight:700\\'>No Preview</div>'">${iH ? '<div class="lib-blur-hint"><span class="msi" style="font-size:18px;">visibility_off</span></div>' : ''}${worldBadge}${playersOverlay}</div><div class="lib-info" onclick="event.stopPropagation();openPhotoDetail(${idx})" style="cursor:pointer;"><div class="lib-name">${esc(x.name)}</div><div class="lib-meta"><span>${x.size}</span><span>${x.time}</span></div></div></div>`;
    } else {
        const ck = x.path || '';
        const cached = thumbCache[ck];
        const th = cached ? `<img class="lib-thumb" src="${cached}">` : `<video class="lib-vid-thumb-video" src="${suAttr}" preload="metadata" muted onloadeddata="cacheVidThumb(this,'${sp}')" onerror="this.outerHTML='<div class=\\'lib-vid-thumb-fallback\\'>VIDEO</div>'"></video>`;
        return `<div class="lib-card">${acts}<div class="lib-thumb-wrap${blurClass}" onclick="openLightbox('${suJs}','video')">${th}<div class="lib-vid-overlay"><div class="lib-play-icon"><span class="msi" style="font-size:22px;">play_arrow</span></div></div><span class="lib-vid-badge">VIDEO</span>${iH ? '<div class="lib-blur-hint"><span class="msi" style="font-size:18px;">visibility_off</span></div>' : ''}</div><div class="lib-info"><div class="lib-name">${esc(x.name)}</div><div class="lib-meta"><span>${x.size}</span><span>${x.time}</span></div></div></div>`;
    }
}

function _appendLibCards(newFiles) {
    const g = document.getElementById('libGrid');
    if (!g) return;
    const sentinel = document.getElementById('libSentinel');

    // Group new files by date and append into existing date groups or create new ones
    const groups = {};
    newFiles.forEach(x => {
        const d = new Date(x.modified);
        const k = d.toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' });
        if (!groups[k]) groups[k] = [];
        groups[k].push(x);
    });

    for (const [dt, items] of Object.entries(groups)) {
        // Find existing date group container or create one
        let container = g.querySelector(`.lib-date-group-container[data-date="${CSS.escape(dt)}"]`);
        if (!container) {
            container = document.createElement('div');
            container.className = 'lib-date-group-container';
            container.setAttribute('data-date', dt);
            container.innerHTML = `<div class="lib-date-group">${esc(dt)}</div><div class="lib-date-group-cards"></div>`;
            if (sentinel) g.insertBefore(container, sentinel);
            else g.appendChild(container);
        }
        const cardsEl = container.querySelector('.lib-date-group-cards');
        items.forEach(x => { cardsEl.insertAdjacentHTML('beforeend', _buildLibCard(x)); });
    }
}

function onWorldsResolved(dict) {
    // dict = { "wrld_xxx": { name, thumbnailImageUrl, imageUrl }, ... }
    if (!dict || typeof dict !== 'object') return;
    Object.entries(dict).forEach(([id, w]) => {
        worldInfoCache[id] = { id, name: w.name || '', thumbnailImageUrl: w.thumbnailImageUrl || w.imageUrl || '' };
    });
    // Update dashboard world cache + re-render
    Object.assign(dashWorldCache, dict);
    renderDashboard();
    renderDiscovery();
    // Update existing library world badges in the DOM
    document.querySelectorAll('.lib-world-badge[data-wid]').forEach(btn => {
        const wid = btn.getAttribute('data-wid');
        const info = worldInfoCache[wid];
        if (info) {
            const thumbEl = btn.querySelector('.lib-world-badge-thumb');
            const textEl = btn.querySelector('.lib-world-badge-text');
            if (thumbEl && info.thumbnailImageUrl) thumbEl.style.backgroundImage = `url('${info.thumbnailImageUrl}')`;
            if (textEl) textEl.textContent = info.name || 'View World';
        }
    });
}

function toggleFavFilter() {
    showFavOnly = !showFavOnly;
    document.getElementById('libFavBtn').classList.toggle('active', showFavOnly);
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
    filterLibrary();
}

function toggleHidden(p) {
    if (hiddenMedia.has(p)) {
        hiddenMedia.delete(p);
    } else {
        hiddenMedia.add(p);
    }
    try { localStorage.setItem('vrcnext_hidden', JSON.stringify([...hiddenMedia])); } catch {}
    filterLibrary();
}

function filterLibrary() {
    _teardownLibSentinel();
    const ff = document.getElementById('libFolderFilter').value, tf = document.getElementById('libTypeFilter').value;
    let f = libraryFiles;
    if (showFavOnly) f = f.filter(x => favorites.has(x.path));
    if (ff !== '__all__') f = f.filter(x => x.folder === ff);
    if (tf !== 'all') f = f.filter(x => x.type === tf);
    f.sort((a, b) => new Date(b.modified) - new Date(a.modified));

    const isFiltered = showFavOnly || ff !== '__all__' || tf !== 'all';
    const cnt = document.getElementById('libCount');
    if (cnt) cnt.textContent = isFiltered ? `${f.length} files (filtered)` : `${libraryFiles.length} / ${_libTotal} files`;

    const g = document.getElementById('libGrid');
    if (!f.length) {
        g.innerHTML = '<div class="empty-msg">' + (showFavOnly ? 'No favorites yet.' : 'No media files found.') + '</div>';
        return;
    }

    const groups = {};
    f.forEach(x => {
        const d = new Date(x.modified);
        const k = d.toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' });
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

    if (!isFiltered && _libHasMore) _setupLibSentinel();
}

function cacheVidThumb(v, fp) {
    try {
        v.currentTime = 1;
        v.addEventListener('seeked', function () {
            const c = document.createElement('canvas');
            c.width = v.videoWidth || 320;
            c.height = v.videoHeight || 240;
            c.getContext('2d').drawImage(v, 0, 0, c.width, c.height);
            const data = c.toDataURL('image/jpeg', 0.7);
            thumbCache[fp] = data;
            const img = document.createElement('img');
            img.className = 'lib-thumb';
            img.src = data;
            v.replaceWith(img);
            try { v.pause(); } catch (e) {}
            v.removeAttribute('src');
            try { v.load(); } catch (e) {}
        }, { once: true });
    } catch (e) {}
}

function updateFolderFilterOptions(fs) {
    const s = document.getElementById('libFolderFilter'), c = s.value;
    s.innerHTML = '<option value="__all__">All Folders</option>';
    (fs || []).forEach(f => {
        const n = f.split(/[\\\\/]/).pop() || f;
        s.innerHTML += `<option value="${esc(f)}">${esc(n)}</option>`;
    });
    s.value = c || '__all__';
    if (s._vnRefresh) s._vnRefresh();
}

function openPhotoDetail(idx) {
    const x = libraryFiles[idx];
    if (!x) return;
    const el = document.getElementById('detailModalContent');
    const imgUrl = x.url || '';
    const players = x.players || [];
    const worldId = x.worldId || '';
    const wInfo = worldId ? worldInfoCache[worldId] : null;
    const worldName = wInfo?.name || worldId || '';
    const date = new Date(x.modified);
    const dateStr = date.toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' });
    const timeStr = date.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });

    // Banner with photo
    const bannerHtml = imgUrl ? `<div class="fd-banner"><img src="${imgUrl}" onerror="this.parentElement.style.display='none'"><div class="fd-banner-fade"></div></div>` : '';

    // Meta info
    let metaHtml = `<div class="fd-meta">
        <div class="fd-meta-row"><span class="fd-meta-label">Date</span><span>${esc(dateStr)}</span></div>
        <div class="fd-meta-row"><span class="fd-meta-label">Time</span><span>${esc(timeStr)}</span></div>
        <div class="fd-meta-row"><span class="fd-meta-label">Size</span><span>${esc(x.size)}</span></div>`;
    if (worldName) {
        metaHtml += `<div class="fd-meta-row" style="cursor:pointer;" onclick="document.getElementById('modalDetail').style.display='none';openWorldSearchDetail('${esc(worldId)}')"><span class="fd-meta-label">World</span><span style="color:var(--accent-lt);">${esc(worldName)}</span></div>`;
    }
    metaHtml += `</div>`;

    // Players list
    let playersHtml = '';
    if (players.length > 0) {
        playersHtml = `<div style="font-size:10px;font-weight:700;color:var(--tx3);padding:8px 0 4px;letter-spacing:.05em;">PLAYERS IN INSTANCE (${players.length})</div><div class="photo-players-list">`;
        players.forEach(p => {
            const onclick = p.userId ? `document.getElementById('modalDetail').style.display='none';openFriendDetail('${jsq(p.userId)}')` : '';
            playersHtml += renderProfileItemSmall({ id: p.userId, displayName: p.displayName, image: p.image }, onclick);
        });
        playersHtml += `</div>`;
    }

    el.innerHTML = `${bannerHtml}<div class="fd-content${imgUrl ? ' fd-has-banner' : ''}" style="padding:20px;">
        <h2 style="margin:0 0 12px;color:var(--tx0);font-size:16px;">${esc(x.name)}</h2>
        ${metaHtml}${playersHtml}
        <div style="margin-top:14px;text-align:right;"><button class="vrcn-button-round" onclick="document.getElementById('modalDetail').style.display='none'">Close</button></div>
    </div>`;
    document.getElementById('modalDetail').style.display = 'flex';
}

function openLightbox(u, t) {
    const lb = document.createElement('div');
    lb.className = 'lib-lightbox';
    lb.onclick = e => { if (e.target === lb) lb.remove(); };
    if (t === 'video') {
        const v = document.createElement('video');
        v.src = u;
        v.controls = true;
        v.autoplay = true;
        v.style.cssText = 'max-width:90%;max-height:90%;border-radius:8px;';
        v.onclick = e => e.stopPropagation();
        lb.appendChild(v);
    } else {
        lb.innerHTML = `<img src="${u}">`;
    }
    document.body.appendChild(lb);
    const ok = e => {
        if (e.key === 'Escape') { lb.remove(); document.removeEventListener('keydown', ok); }
    };
    document.addEventListener('keydown', ok);
}

function showDeleteModal(fp, fn) {
    pendingDeletePath = fp;
    const x = document.getElementById('deleteModal');
    if (x) x.remove();
    const o = document.createElement('div');
    o.className = 'modal-overlay';
    o.id = 'deleteModal';
    o.onclick = e => { if (e.target === o) closeDeleteModal(); };
    o.innerHTML = `<div class="modal-box"><div class="modal-icon danger"><span class="msi" style="font-size:22px;">delete</span></div><div class="modal-title">Delete File</div><div class="modal-msg">Permanently delete from disk:<br><span class="modal-fname">${esc(fn)}</span></div><div class="modal-btns"><button id="libDelCancelBtn" class="vrcn-button-round" onclick="closeDeleteModal()">Cancel</button><button class="vrcn-button-round vrcn-btn-danger" onclick="confirmDelete()">Delete</button></div></div>`;
    document.body.appendChild(o);
    o.querySelector('#libDelCancelBtn').focus();
    const ok = e => {
        if (e.key === 'Escape') { closeDeleteModal(); document.removeEventListener('keydown', ok); }
        if (e.key === 'Enter') { confirmDelete(); document.removeEventListener('keydown', ok); }
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
    o.id = 'deleteModal';
    o.onclick = e => { if (e.target === o) closeDeleteModal(); };
    o.innerHTML = `<div class="modal-box"><div class="modal-icon danger"><span class="msi" style="font-size:22px;">delete</span></div><div class="modal-title">Delete All Posts</div><div class="modal-msg">Delete all <strong>${postedFiles.length}</strong> post(s) from Discord?</div><div class="modal-btns"><button class="vrcn-button-round" onclick="closeDeleteModal()">Cancel</button><button class="vrcn-button-round vrcn-btn-danger" onclick="confirmDeleteAll()">Delete All</button></div></div>`;
    document.body.appendChild(o);
}

function confirmDeleteAll() {
    postedFiles.forEach(f => {
        if (f.messageId) sendToCS({ action: 'deletePost', messageId: f.messageId, webhookUrl: f.webhookUrl });
    });
    closeDeleteModal();
}

function copyToClipboard(url, path, type) {
    if (type === 'image') {
        sendToCS({ action: 'copyImageToClipboard', path });
    } else {
        navigator.clipboard.writeText(path).then(
            () => showToast(true, 'Path copied to clipboard'),
            () => showToast(false, 'Clipboard copy failed')
        );
    }
}
