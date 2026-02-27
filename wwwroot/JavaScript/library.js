function refreshLibrary() {
    // Render cached items immediately so the grid isn't empty during scan
    try {
        const raw = localStorage.getItem('vrcnext_lib_cache');
        if (raw) {
            const cached = JSON.parse(raw);
            if (cached.files && cached.files.length) {
                libraryFiles = cached.files;
                filterLibrary();
                const cnt = document.getElementById('libCount');
                if (cnt) cnt.textContent = cached.files.length + ' files (refreshing…)';
            }
        }
    } catch {}
    sendToCS({ action: 'scanLibrary' });
}

function renderLibrary(f) {
    libraryFiles = f || [];
    // Persist last 100 items to localStorage for instant display on next open
    try {
        const cacheItems = (f || []).slice(0, 100).map(x => ({
            name: x.name, path: x.path, folder: x.folder, type: x.type,
            size: x.size, modified: x.modified, time: x.time,
            url: x.url, worldId: x.worldId || '',
            players: (x.players || []).slice(0, 4),
        }));
        localStorage.setItem('vrcnext_lib_cache', JSON.stringify({ timestamp: Date.now(), files: cacheItems }));
    } catch {}
    // Collect unique worldIds that need resolution
    const unknownWorldIds = [];
    (f || []).forEach(x => {
        if (x.worldId && !worldInfoCache[x.worldId]) unknownWorldIds.push(x.worldId);
    });
    const unique = [...new Set(unknownWorldIds)];
    if (unique.length > 0) sendToCS({ action: 'vrcResolveWorlds', worldIds: unique.slice(0, 30) });
    filterLibrary();
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
    const ff = document.getElementById('libFolderFilter').value, tf = document.getElementById('libTypeFilter').value;
    let f = libraryFiles;
    if (showFavOnly) f = f.filter(x => favorites.has(x.path));
    if (ff !== '__all__') f = f.filter(x => x.folder === ff);
    if (tf !== 'all') f = f.filter(x => x.type === tf);
    f.sort((a, b) => new Date(b.modified) - new Date(a.modified));
    document.getElementById('libCount').textContent = f.length + ' files';
    const g = document.getElementById('libGrid');
    if (!f.length) {
        g.innerHTML = '<div class="empty-msg">' + (showFavOnly ? 'No favorites yet.' : 'No media files found.') + '</div>';
        return;
    }
    const groups = {};
    f.forEach(x => {
        const d = new Date(x.modified), k = d.toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' });
        if (!groups[k]) groups[k] = [];
        groups[k].push(x);
    });
    let h = '';
    for (const [dt, items] of Object.entries(groups)) {
        h += `<div class="lib-date-group">${esc(dt)}</div>`;
        items.forEach(x => {
            const su = x.url || '';
            const suAttr = esc(su);
            const suJs = jsq(su);
            const sp = jsq(x.path || '');
            const sn = jsq(x.name || '');
            const ck = x.path || '';
            const iF = favorites.has(x.path), fc = iF ? ' active' : '';
            const iH = hiddenMedia.has(x.path), hc = iH ? ' active' : '';
            const ac = [
                'lib-actions',
                iF ? 'has-fav' : '',
                iH ? 'has-hidden' : ''
            ].filter(Boolean).join(' ');
            const acts = `<div class="${ac}"><button class="lib-act-btn lib-btn-clip" onclick="event.stopPropagation();copyToClipboard('${suJs}','${sp}','${x.type}')" title="Copy to clipboard"><span class="msi" style="font-size:16px;">content_copy</span></button><button class="lib-act-btn lib-btn-fav${fc}" onclick="event.stopPropagation();toggleFavorite('${sp}')" title="Favorite"><span class="msi" style="font-size:16px;">star</span></button><button class="lib-act-btn lib-btn-hide${hc}" onclick="event.stopPropagation();toggleHidden('${sp}')" title="${iH ? 'Unhide' : 'Hide'}"><span class="msi" style="font-size:16px;">${iH ? 'visibility' : 'visibility_off'}</span></button><button class="lib-act-btn lib-btn-del" onclick="event.stopPropagation();showDeleteModal('${sp}','${sn}')"><span class="msi" style="font-size:16px;">delete</span></button></div>`;
            const blurClass = iH ? ' lib-blurred' : '';
            if (x.type === 'image') {
                let worldBadge = '';
                if (x.worldId) {
                    const wInfo = worldInfoCache[x.worldId];
                    const wName = wInfo ? esc(wInfo.name) : 'View World';
                    const wThumb = wInfo?.thumbnailImageUrl || '';
                    worldBadge = `<button class="lib-world-badge" data-wid="${esc(x.worldId)}" onclick="event.stopPropagation();openWorldSearchDetail('${esc(x.worldId)}')" title="${wName}"><span class="lib-world-badge-thumb" style="${wThumb ? 'background-image:url(\''+wThumb+'\')' : ''}"></span><span class="lib-world-badge-text">${wName}</span></button>`;
                }
                // Player avatars at bottom-right
                let playersOverlay = '';
                const players = x.players || [];
                if (players.length > 0) {
                    const show = players.slice(0, 3);
                    const remaining = players.length - show.length;
                    playersOverlay = `<div class="lib-players-overlay" onclick="event.stopPropagation();openPhotoDetail(${libraryFiles.indexOf(x)})">` +
                        show.map(p => {
                            const fr = vrcFriendsData.find(f => f.id === p.userId);
                            const img = p.image || fr?.image || '';
                            return img
                                ? `<div class="lib-player-av" style="background-image:url('${img}')" title="${esc(p.displayName)}"></div>`
                                : `<div class="lib-player-av lib-player-av-letter" title="${esc(p.displayName)}">${esc((p.displayName||'?')[0])}</div>`;
                        }).join('') +
                        (remaining > 0 ? `<div class="lib-player-av lib-player-av-more">+${remaining}</div>` : '') +
                        `</div>`;
                }
                const idx = libraryFiles.indexOf(x);
                h += `<div class="lib-card">${acts}<div class="lib-thumb-wrap${blurClass}" onclick="openLightbox('${suJs}','image')"><img class="lib-thumb" src="${suAttr}" loading="lazy" onerror="this.outerHTML='<div style=\\'width:100%;height:100%;display:flex;align-items:center;justify-content:center;color:var(--tx3);font-size:11px;font-weight:700\\'>No Preview</div>'">${iH ? '<div class="lib-blur-hint"><span class="msi" style="font-size:18px;">visibility_off</span></div>' : ''}${worldBadge}${playersOverlay}</div><div class="lib-info" onclick="event.stopPropagation();openPhotoDetail(${idx})" style="cursor:pointer;"><div class="lib-name">${esc(x.name)}</div><div class="lib-meta"><span>${x.size}</span><span>${x.time}</span></div></div></div>`;
            } else {
                const cached = thumbCache[ck];
                const th = cached ? `<img class="lib-thumb" src="${cached}">` : `<video class="lib-vid-thumb-video" src="${suAttr}" preload="metadata" muted onloadeddata="cacheVidThumb(this,'${sp}')" onerror="this.outerHTML='<div class=\\'lib-vid-thumb-fallback\\'>VIDEO</div>'"></video>`;
                h += `<div class="lib-card">${acts}<div class="lib-thumb-wrap${blurClass}" onclick="openLightbox('${suJs}','video')">${th}<div class="lib-vid-overlay"><div class="lib-play-icon"><span class="msi" style="font-size:22px;">play_arrow</span></div></div><span class="lib-vid-badge">VIDEO</span>${iH ? '<div class="lib-blur-hint"><span class="msi" style="font-size:18px;">visibility_off</span></div>' : ''}</div><div class="lib-info"><div class="lib-name">${esc(x.name)}</div><div class="lib-meta"><span>${x.size}</span><span>${x.time}</span></div></div></div>`;
            }
        });
    }
    g.innerHTML = h;
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
            const fr = vrcFriendsData.find(f => f.id === p.userId);
            const img = p.image || fr?.image || '';
            const imgTag = img
                ? `<div class="inst-user-av" style="background-image:url('${img}')"></div>`
                : `<div class="inst-user-av inst-user-av-letter">${esc((p.displayName||'?')[0].toUpperCase())}</div>`;
            const click = p.userId ? ` onclick="document.getElementById('modalDetail').style.display='none';openFriendDetail('${esc(p.userId)}')"` : '';
            const isFriend = fr ? '<span style="font-size:9px;color:var(--ok);margin-left:auto;">Friend</span>' : '';
            playersHtml += `<div class="inst-user-row"${click}>${imgTag}<span class="inst-user-name">${esc(p.displayName)}</span>${isFriend}</div>`;
        });
        playersHtml += `</div>`;
    }

    el.innerHTML = `${bannerHtml}<div class="fd-content${imgUrl ? ' fd-has-banner' : ''}" style="padding:20px;">
        <h2 style="margin:0 0 12px;color:var(--tx0);font-size:16px;">${esc(x.name)}</h2>
        ${metaHtml}${playersHtml}
        <div style="margin-top:14px;text-align:right;"><button class="modal-btn modal-btn-cancel" onclick="document.getElementById('modalDetail').style.display='none'">Close</button></div>
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
    o.innerHTML = `<div class="modal-box"><div class="modal-icon danger"><span class="msi" style="font-size:22px;">delete</span></div><div class="modal-title">Delete File</div><div class="modal-msg">Permanently delete from disk:<br><span class="modal-fname">${esc(fn)}</span></div><div class="modal-btns"><button class="modal-btn modal-btn-cancel" onclick="closeDeleteModal()">Cancel</button><button class="modal-btn modal-btn-delete" onclick="confirmDelete()">Delete</button></div></div>`;
    document.body.appendChild(o);
    o.querySelector('.modal-btn-cancel').focus();
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
    o.innerHTML = `<div class="modal-box"><div class="modal-icon danger"><span class="msi" style="font-size:22px;">delete</span></div><div class="modal-title">Delete All Posts</div><div class="modal-msg">Delete all <strong>${postedFiles.length}</strong> post(s) from Discord?</div><div class="modal-btns"><button class="modal-btn modal-btn-cancel" onclick="closeDeleteModal()">Cancel</button><button class="modal-btn modal-btn-delete" onclick="confirmDeleteAll()">Delete All</button></div></div>`;
    document.body.appendChild(o);
}

function confirmDeleteAll() {
    postedFiles.forEach(f => {
        if (f.messageId) sendToCS({ action: 'deletePost', messageId: f.messageId, webhookUrl: f.webhookUrl });
    });
    closeDeleteModal();
}

async function copyToClipboard(url, path, type) {
    try {
        if (type === 'image') {
            const resp = await fetch(url);
            const blob = await resp.blob();
            await navigator.clipboard.write([new ClipboardItem({ [blob.type]: blob })]);
            showMypToast(true, 'Image copied to clipboard');
        } else {
            await navigator.clipboard.writeText(path);
            showMypToast(true, 'Path copied to clipboard');
        }
    } catch {
        showMypToast(false, 'Clipboard copy failed');
    }
}
