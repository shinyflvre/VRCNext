/* === Inventory Tab === */

// Pending state for inventory delete modal
let _invPendingDelete = null; // { type: 'file'|'print', id, versionId }

// VRChat deletes file versions asynchronously. Keep local filter sets so
// deleted items stay hidden even if the API still returns them after refresh.
const _invPendingFileDeletes  = new Set(); // fileIds
const _invPendingPrintDeletes = new Set(); // printIds

const INV_TABS = {
    photos:    { tag: 'gallery',  label: 'Photos',    canUpload: true,  icon: 'photo_library',  hint: 'PNG, recommended 1200×900 (4:3)' },
    icons:     { tag: 'icon',     label: 'Icons',     canUpload: true,  icon: 'account_circle', hint: 'PNG, 1024×1024 (requires VRC+)' },
    emojis:    { tag: 'emoji',    label: 'Emojis',    canUpload: true,  icon: 'emoji_emotions', hint: 'PNG, 1024×1024 (requires VRC+, max 18)' },
    stickers:  { tag: 'sticker',  label: 'Stickers',  canUpload: true,  icon: 'sticky_note_2',  hint: 'PNG, max 1024×1024 (requires VRC+, max 18)' },
    prints:    { tag: null,       label: 'Prints',    canUpload: false, icon: 'print',           hint: 'In-game prints from VRChat' },
    inventory: { tag: null,       label: 'Inventory', canUpload: false, icon: 'inventory_2',     hint: null },
};

function switchInvTab(tab) {
    activeInvTab = tab;
    // Update filter button active states
    Object.keys(INV_TABS).forEach(t => {
        const btn = document.getElementById('invF' + t.charAt(0).toUpperCase() + t.slice(1));
        if (btn) btn.classList.toggle('active', t === tab);
    });
    // Show/hide upload button
    const info = INV_TABS[tab];
    const uploadBtn = document.getElementById('invUploadBtn');
    if (uploadBtn) uploadBtn.style.display = info?.canUpload ? '' : 'none';

    // Show cached data immediately, then refresh if empty
    const cached = tab === 'prints'    ? invPrintsCache :
                   tab === 'inventory' ? invInventoryCache :
                   (invFilesCache[info?.tag] || null);
    if (cached && cached.length > 0) {
        if (tab === 'prints') renderInvPrints(cached);
        else if (tab === 'inventory') renderInvInventory(cached);
        else renderInvFiles(cached, tab);
    } else {
        refreshInventory();
    }
}

function refreshInventory() {
    const tab = activeInvTab;
    const info = INV_TABS[tab];
    const grid = document.getElementById('invGrid');
    if (!grid) return;

    // Show loading skeleton
    grid.innerHTML = sk('feed', 6);
    document.getElementById('invCount').textContent = '';

    if (tab === 'prints') {
        sendToCS({ action: 'invGetPrints' });
    } else if (tab === 'inventory') {
        sendToCS({ action: 'invGetInventory' });
    } else if (info?.tag) {
        sendToCS({ action: 'invGetFiles', tag: info.tag });
    }
}

// Render files (photos / icons / emojis / stickers)

function renderInvFiles(files, tab) {
    // Filter out locally-deleted files (VRChat deletion is async, API may still return them)
    if (_invPendingFileDeletes.size > 0)
        files = files.filter(f => !_invPendingFileDeletes.has(f.id));
    const tag = INV_TABS[tab]?.tag;
    if (tag) invFilesCache[tag] = files;

    const grid = document.getElementById('invGrid');
    if (!grid) return;

    const count = document.getElementById('invCount');
    if (count) count.textContent = files.length + ' item' + (files.length !== 1 ? 's' : '');

    if (!files.length) {
        const hint = INV_TABS[tab]?.hint || '';
        grid.innerHTML = `<div class="empty-msg">No ${INV_TABS[tab]?.label || 'items'} found.<br><span style="font-size:11px;color:var(--tx3);">${esc(hint)}</span></div>`;
        return;
    }

    // Group by date
    const groups = {};
    files.forEach(f => {
        const d = f.createdAt ? new Date(f.createdAt) : new Date(0);
        const k = isNaN(d.getTime()) ? 'Unknown date'
            : d.toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' });
        if (!groups[k]) groups[k] = [];
        groups[k].push(f);
    });

    let h = '';
    for (const [dt, items] of Object.entries(groups)) {
        h += `<div class="lib-date-group">${esc(dt)}</div>`;
        items.forEach(f => {
            h += buildInvFileCard(f, tab);
        });
    }
    grid.innerHTML = h;
}

function buildInvFileCard(f, _tab) {
    const imgUrl = f.fileUrl || '';
    const imgAttr = esc(imgUrl);
    const imgJs = jsq(imgUrl);
    const fileId = jsq(f.id || '');
    const fileName = jsq((f.name || 'image') + '.png');
    const sizeStr = formatFileSize(f.sizeBytes || 0);
    const _fd = f.createdAt ? new Date(f.createdAt) : null;
    const timeStr = _fd && !isNaN(_fd.getTime()) ? _fd.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' }) : '';
    const nameDisp = esc(f.name || 'Unnamed');

    // Animated badge
    const isAnim = (f.tags || []).includes('emojianimated');
    const animBadge = isAnim ? '<span class="inv-anim-badge">ANIM</span>' : '';

    const acts = `<div class="lib-actions">
        <button class="lib-act-btn lib-btn-clip" onclick="event.stopPropagation();invDownload('${imgJs}','${fileName}')" title="Download"><span class="msi" style="font-size:16px;">download</span></button>
        <button class="lib-act-btn lib-btn-del" onclick="event.stopPropagation();invConfirmDeleteFile('${fileId}')" title="Delete"><span class="msi" style="font-size:16px;">delete</span></button>
    </div>`;

    return `<div class="lib-card inv-card">
        ${acts}
        <div class="lib-thumb-wrap" onclick="openLightbox('${imgJs}','image')">
            ${imgUrl
                ? `<img class="lib-thumb" src="${imgAttr}" loading="lazy" onerror="this.outerHTML='<div class=\\'inv-no-preview\\'>No Preview</div>'">`
                : '<div class="inv-no-preview">No Preview</div>'
            }
            ${animBadge}
        </div>
        <div class="lib-info">
            <div class="lib-name">${nameDisp}</div>
            <div class="lib-meta"><span>${sizeStr}</span><span>${timeStr}</span></div>
        </div>
    </div>`;
}

// Render prints

function renderInvPrints(prints) {
    // Filter out locally-deleted prints
    if (_invPendingPrintDeletes.size > 0)
        prints = prints.filter(p => !_invPendingPrintDeletes.has(p.id));
    invPrintsCache = prints;
    const grid = document.getElementById('invGrid');
    if (!grid) return;

    const count = document.getElementById('invCount');
    if (count) count.textContent = prints.length + ' print' + (prints.length !== 1 ? 's' : '');

    if (!prints.length) {
        grid.innerHTML = '<div class="empty-msg">No prints found.<br><span style="font-size:11px;color:var(--tx3);">Prints are photos taken inside VRChat.</span></div>';
        return;
    }

    const groups = {};
    prints.forEach(p => {
        const d = p.createdAt ? new Date(p.createdAt) : new Date(0);
        const k = isNaN(d.getTime()) ? 'Unknown date'
            : d.toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' });
        if (!groups[k]) groups[k] = [];
        groups[k].push(p);
    });

    let h = '';
    for (const [dt, items] of Object.entries(groups)) {
        h += `<div class="lib-date-group">${esc(dt)}</div>`;
        items.forEach(p => {
            h += buildInvPrintCard(p);
        });
    }
    grid.innerHTML = h;
}

function buildInvPrintCard(p) {
    const imgUrl = p.imageUrl || '';
    const imgAttr = esc(imgUrl);
    const imgJs = jsq(imgUrl);
    const printId = jsq(p.id || '');
    const _pd = p.createdAt ? new Date(p.createdAt) : null;
    const timeStr = _pd && !isNaN(_pd.getTime()) ? _pd.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' }) : '';
    const worldName = esc(p.worldName || '');
    const note = esc(p.note || '');

    const acts = `<div class="lib-actions">
        ${imgUrl ? `<button class="lib-act-btn lib-btn-clip" onclick="event.stopPropagation();invDownload('${imgJs}','print.png')" title="Download"><span class="msi" style="font-size:16px;">download</span></button>` : ''}
        <button class="lib-act-btn lib-btn-del" onclick="event.stopPropagation();invConfirmDeletePrint('${printId}')" title="Delete"><span class="msi" style="font-size:16px;">delete</span></button>
    </div>`;

    const metaParts = [];
    if (worldName) metaParts.push(`<span>${worldName}</span>`);
    if (timeStr) metaParts.push(`<span>${timeStr}</span>`);

    return `<div class="lib-card inv-card">
        ${acts}
        <div class="lib-thumb-wrap" onclick="${imgUrl ? `openLightbox('${imgJs}','image')` : ''}">
            ${imgUrl
                ? `<img class="lib-thumb" src="${imgAttr}" loading="lazy" onerror="this.outerHTML='<div class=\\'inv-no-preview\\'>No Preview</div>'">`
                : '<div class="inv-no-preview">No Preview</div>'
            }
        </div>
        <div class="lib-info">
            <div class="lib-name">${note || 'Print'}</div>
            <div class="lib-meta">${metaParts.join('')}</div>
        </div>
    </div>`;
}

// Inventory items

function renderInvInventory(items) {
    invInventoryCache = items;
    const grid = document.getElementById('invGrid');
    if (!grid) return;

    const count = document.getElementById('invCount');
    if (count) count.textContent = items.length + ' item' + (items.length !== 1 ? 's' : '');

    if (!items.length) {
        grid.innerHTML = '<div class="empty-msg">No inventory items found.<br><span style="font-size:11px;color:var(--tx3);">Items you own appear here (props, emojis, stickers from bundles).</span></div>';
        return;
    }

    let h = '';
    items.forEach(item => { h += buildInvItemCard(item); });
    grid.innerHTML = h;
}

function buildInvItemCard(item) {
    const imgUrl = item.imageUrl || '';
    const imgAttr = esc(imgUrl);
    const imgJs = jsq(imgUrl);
    const nameDisp = esc(item.name || 'Item');
    const typeLabel = esc(item.itemType || '');

    return `<div class="lib-card inv-card">
        <div class="lib-thumb-wrap" onclick="${imgUrl ? `openLightbox('${imgJs}','image')` : ''}">
            ${imgUrl
                ? `<img class="lib-thumb" src="${imgAttr}" loading="lazy" onerror="this.outerHTML='<div class=\\'inv-no-preview\\'>No Preview</div>'">`
                : '<div class="inv-no-preview">No Preview</div>'
            }
            ${typeLabel ? `<span class="inv-anim-badge">${typeLabel.toUpperCase()}</span>` : ''}
        </div>
        <div class="lib-info">
            <div class="lib-name">${nameDisp}</div>
        </div>
    </div>`;
}

function handleInvInventoryResult(payload) {
    if (payload.error) {
        const grid = document.getElementById('invGrid');
        if (grid) grid.innerHTML = `<div class="empty-msg">Error: ${esc(payload.error)}</div>`;
        return;
    }
    renderInvInventory(payload.items || []);
}

// Upload

function invBrowseUpload() {
    const info = INV_TABS[activeInvTab];
    if (!info?.canUpload) return;
    sendToCS({ action: 'invBrowseUpload', tag: info.tag });
}

// Called from messages.js when upload completes
function handleInvUploadResult(payload) {
    if (payload.success && payload.file) {
        const tag = payload.tag;
        if (!invFilesCache[tag]) invFilesCache[tag] = [];
        invFilesCache[tag].unshift(payload.file);
        if (INV_TABS[activeInvTab]?.tag === tag) {
            renderInvFiles(invFilesCache[tag], activeInvTab);
        }
        iuHandleUploadDone(true);
        showToast(true, 'Uploaded successfully!');
    } else {
        iuHandleUploadDone(false);
        showToast(false, payload.error || 'Upload failed');
    }
}

// Delete

function invConfirmDeleteFile(fileId) {
    const tag  = INV_TABS[activeInvTab]?.tag;
    const file = tag && invFilesCache[tag] ? invFilesCache[tag].find(f => f.id === fileId) : null;
    showInvDeleteModal('file', fileId, null, file?.name || 'this item');
}

function invConfirmDeletePrint(printId) {
    const p = invPrintsCache.find(x => x.id === printId);
    showInvDeleteModal('print', printId, null, p?.note || 'this print');
}

function showInvDeleteModal(type, id, versionId, name) {
    _invPendingDelete = { type, id, versionId };
    const x = document.getElementById('invDeleteModal');
    if (x) x.remove();
    const o = document.createElement('div');
    o.className = 'modal-overlay';
    o.id = 'invDeleteModal';
    o.onclick = e => { if (e.target === o) closeInvDeleteModal(); };
    o.innerHTML = `<div class="modal-box"><div class="modal-icon danger"><span class="msi" style="font-size:22px;">delete</span></div><div class="modal-title">Delete Item</div><div class="modal-msg">Permanently delete from VRChat:<br><span class="modal-fname">${esc(name)}</span><br><span style="font-size:11px;color:var(--tx3);">This cannot be undone.</span></div><div class="modal-btns"><button id="invDelCancelBtn" class="fd-btn" onclick="closeInvDeleteModal()">Cancel</button><button class="fd-btn fd-btn-danger" onclick="confirmInvDelete()">Delete</button></div></div>`;
    document.body.appendChild(o);
    o.querySelector('#invDelCancelBtn').focus();
    const handler = e => {
        if (e.key === 'Escape') { closeInvDeleteModal(); document.removeEventListener('keydown', handler); }
        if (e.key === 'Enter')  { confirmInvDelete();    document.removeEventListener('keydown', handler); }
    };
    document.addEventListener('keydown', handler);
}

function closeInvDeleteModal() {
    _invPendingDelete = null;
    const m = document.getElementById('invDeleteModal');
    if (m) m.remove();
}

function confirmInvDelete() {
    if (!_invPendingDelete) { closeInvDeleteModal(); return; }
    const { type, id } = _invPendingDelete;
    if (type === 'file')  sendToCS({ action: 'invDeleteFile',  fileId: id });
    if (type === 'print') sendToCS({ action: 'invDeletePrint', printId: id });
    closeInvDeleteModal();
}

function handleInvDeleteResult(payload) {
    if (payload.success) {
        // Add to pending set; clear after 10 min (enough for VRChat's async deletion)
        _invPendingFileDeletes.add(payload.fileId);
        setTimeout(() => _invPendingFileDeletes.delete(payload.fileId), 10 * 60 * 1000);

        const tag = INV_TABS[activeInvTab]?.tag;
        if (tag && invFilesCache[tag]) {
            invFilesCache[tag] = invFilesCache[tag].filter(f => f.id !== payload.fileId);
            renderInvFiles(invFilesCache[tag], activeInvTab);
        }
        showToast(true, 'Deleted');
    } else {
        showToast(false, 'Delete failed');
    }
}

function handleInvPrintDeleteResult(payload) {
    if (payload.success) {
        _invPendingPrintDeletes.add(payload.printId);
        setTimeout(() => _invPendingPrintDeletes.delete(payload.printId), 10 * 60 * 1000);

        invPrintsCache = invPrintsCache.filter(p => p.id !== payload.printId);
        renderInvPrints(invPrintsCache);
        showToast(true, 'Print deleted');
    } else {
        showToast(false, 'Delete failed');
    }
}

// Download

function invDownload(url, fileName) {
    sendToCS({ action: 'invDownload', url, fileName });
}

// Helpers

function formatFileSize(bytes) {
    if (!bytes || bytes === 0) return '';
    if (bytes < 1024) return bytes + ' B';
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
    return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
}

