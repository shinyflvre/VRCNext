/* === Inventory Tab === */

// Pending state for inventory delete modal
let _invPendingDelete = null; // { type: 'file'|'print', id, versionId }

// VRChat deletes file versions asynchronously. Keep local filter sets so
// deleted items stay hidden even if the API still returns them after refresh.
const _invPendingFileDeletes = new Set(); // fileIds
const _invPendingPrintDeletes = new Set(); // printIds

const INV_TABS = {
    photos: {
        tag: 'gallery',
        canUpload: true,
        icon: 'photo_library',
        get label() { return t('inventory.tabs.photos', 'Photos'); },
        get hint() { return t('inventory.hint.photos', 'PNG, recommended 1200x900 (4:3)'); }
    },
    icons: {
        tag: 'icon',
        canUpload: true,
        icon: 'account_circle',
        get label() { return t('inventory.tabs.icons', 'Icons'); },
        get hint() { return t('inventory.hint.icons', 'PNG, 1024x1024 (requires VRC+)'); }
    },
    emojis: {
        tag: 'emoji',
        canUpload: true,
        icon: 'emoji_emotions',
        get label() { return t('inventory.tabs.emojis', 'Emojis'); },
        get hint() { return t('inventory.hint.emojis', 'PNG, 1024x1024 (requires VRC+, max 18)'); }
    },
    stickers: {
        tag: 'sticker',
        canUpload: true,
        icon: 'sticky_note_2',
        get label() { return t('inventory.tabs.stickers', 'Stickers'); },
        get hint() { return t('inventory.hint.stickers', 'PNG, max 1024x1024 (requires VRC+, max 18)'); }
    },
    prints: {
        tag: null,
        canUpload: true,
        icon: 'print',
        get label() { return t('inventory.tabs.prints', 'Prints'); },
        get hint() { return t('inventory.hint.prints', 'In-game prints from VRChat'); }
    },
    inventory: {
        tag: null,
        canUpload: false,
        icon: 'inventory_2',
        get label() { return t('inventory.tabs.inventory', 'Inventory'); },
        get hint() { return ''; }
    }
};


function invTabLabel(tab) {
    return INV_TABS[tab]?.label || tab;
}

function invTabHint(tab) {
    return INV_TABS[tab]?.hint || '';
}

function invCountText(kind, count) {
    const form = count === 1 ? 'one' : 'other';
    if (kind === 'prints') {
        return tf(`inventory.count.prints.${form}`, { count }, `${count} print${count === 1 ? '' : 's'}`);
    }
    return tf(`inventory.count.items.${form}`, { count }, `${count} item${count === 1 ? '' : 's'}`);
}

function invGroupDateLabel(value) {
    const date = value ? new Date(value) : new Date(0);
    if (isNaN(date.getTime())) return t('inventory.date.unknown', 'Unknown date');
    return fmtLongDate(date);
}

function invTimeLabel(value) {
    const date = value ? new Date(value) : null;
    if (!date || isNaN(date.getTime())) return '';
    return fmtTime(date);
}

function invNoPreviewMarkup() {
    return `<div class="inv-no-preview">${esc(t('inventory.empty.no_preview', 'No Preview'))}</div>`;
}

function renderInvFetchError(message, hintKey = '') {
    const grid = document.getElementById('invGrid');
    if (!grid) return;

    const msg = tf('inventory.error.message', { message: esc(message) }, `Error: ${esc(message)}`);
    const hint = hintKey
        ? `<br><span style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);">${esc(t(hintKey, ''))}</span>`
        : '';
    grid.innerHTML = `<div class="empty-msg" style="color:var(--err);">${msg}${hint}</div>`;
}

function switchInvTab(tab) {
    activeInvTab = tab;

    Object.keys(INV_TABS).forEach(key => {
        const btn = document.getElementById('invF' + key.charAt(0).toUpperCase() + key.slice(1));
        if (btn) btn.classList.toggle('active', key === tab);
    });

    const info = INV_TABS[tab];
    const uploadBtn = document.getElementById('invUploadBtn');
    if (uploadBtn) uploadBtn.style.display = info?.canUpload ? '' : 'none';

    const cached = tab === 'prints'
        ? invPrintsCache
        : tab === 'inventory'
            ? invInventoryCache
            : (invFilesCache[info?.tag] || null);

    if (Array.isArray(cached) && cached.length > 0) {
        if (tab === 'prints') renderInvPrints(cached);
        else if (tab === 'inventory') renderInvInventory(cached);
        else renderInvFiles(cached, tab);
        return;
    }

    refreshInventory();
}

function refreshInventory(force = false) {
    const tab = activeInvTab;
    const info = INV_TABS[tab];
    const grid = document.getElementById('invGrid');
    if (!grid) return;

    grid.innerHTML = sk('feed', 6);

    const count = document.getElementById('invCount');
    if (count) count.textContent = '';

    if (tab === 'prints') {
        sendToCS({ action: 'invGetPrints', force });
    } else if (tab === 'inventory') {
        sendToCS({ action: 'invGetInventory', force });
    } else if (info?.tag) {
        sendToCS({ action: 'invGetFiles', tag: info.tag, force });
    }
}

function renderInvFiles(files, tab) {
    if (_invPendingFileDeletes.size > 0) {
        files = files.filter(file => !_invPendingFileDeletes.has(file.id));
    }

    const tag = INV_TABS[tab]?.tag;
    if (tag) invFilesCache[tag] = files;

    const grid = document.getElementById('invGrid');
    if (!grid) return;

    const count = document.getElementById('invCount');
    if (count) count.textContent = invCountText('items', files.length);

    if (!files.length) {
        const hint = invTabHint(tab);
        grid.innerHTML = `<div class="empty-msg">${t(`inventory.empty.${tab}`, `No ${invTabLabel(tab)} found.`)}${hint ? `<br><span style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);">${esc(hint)}</span>` : ''}</div>`;
        return;
    }

    const groups = {};
    files.forEach(file => {
        const key = invGroupDateLabel(file.createdAt);
        if (!groups[key]) groups[key] = [];
        groups[key].push(file);
    });

    let html = '';
    for (const [groupLabel, items] of Object.entries(groups)) {
        html += `<div class="lib-date-group">${esc(groupLabel)}</div>`;
        items.forEach(file => {
            html += buildInvFileCard(file);
        });
    }
    grid.innerHTML = html;
}

function buildInvFileCard(file) {
    const imgUrl = file.fileUrl || '';
    const imgAttr = esc(imgUrl);
    const imgJs = jsq(imgUrl);
    const fileId = jsq(file.id || '');
    const defaultFileBase = file.name || t('inventory.card.image', 'image');
    const fileName = jsq(defaultFileBase + '.png');
    const sizeStr = formatFileSize(file.sizeBytes || 0);
    const timeStr = invTimeLabel(file.createdAt);
    const nameDisp = esc(file.name || t('inventory.card.unnamed', 'Unnamed'));
    const noPreviewHtml = `<div class=\\'inv-no-preview\\'>${esc(t('inventory.empty.no_preview', 'No Preview'))}</div>`;

    const isAnim = (file.tags || []).includes('emojianimated');
    const animBadge = isAnim
        ? `<span class="inv-anim-badge vrcn-badge accent">${esc(t('inventory.badge.anim', 'ANIM'))}</span>`
        : '';

    const actions = `<div class="lib-actions">
        <button class="vrcn-lib-button clip" onclick="event.stopPropagation();invDownload('${imgJs}','${fileName}')" title="${esc(t('inventory.actions.download', 'Download'))}"><span class="msi" style="font-size:16px;">download</span></button>
        <button class="vrcn-lib-button del" onclick="event.stopPropagation();invConfirmDeleteFile('${fileId}')" title="${esc(t('inventory.actions.delete', 'Delete'))}"><span class="msi" style="font-size:16px;">delete</span></button>
    </div>`;

    return `<div class="lib-card inv-card">
        ${actions}
        <div class="lib-thumb-wrap" onclick="openLightbox('${imgJs}','image')">
            ${imgUrl
                ? `<img class="lib-thumb" src="${imgAttr}" loading="lazy" onerror="this.outerHTML='${noPreviewHtml}'">`
                : invNoPreviewMarkup()
            }
            ${animBadge}
        </div>
        <div class="lib-info">
            <div class="lib-name">${nameDisp}</div>
            <div class="lib-meta"><span>${sizeStr}</span><span>${timeStr}</span></div>
        </div>
    </div>`;
}

function renderInvPrints(prints) {
    if (_invPendingPrintDeletes.size > 0) {
        prints = prints.filter(print => !_invPendingPrintDeletes.has(print.id));
    }

    invPrintsCache = prints;

    const grid = document.getElementById('invGrid');
    if (!grid) return;

    const count = document.getElementById('invCount');
    if (count) count.textContent = invCountText('prints', prints.length);

    if (!prints.length) {
        grid.innerHTML = `<div class="empty-msg">${t('inventory.empty.prints', 'No prints found.')}<br><span style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);">${esc(t('inventory.empty.prints_desc', 'Prints are photos taken inside VRChat.'))}</span></div>`;
        return;
    }

    const groups = {};
    prints.forEach(print => {
        const key = invGroupDateLabel(print.createdAt);
        if (!groups[key]) groups[key] = [];
        groups[key].push(print);
    });

    let html = '';
    for (const [groupLabel, items] of Object.entries(groups)) {
        html += `<div class="lib-date-group">${esc(groupLabel)}</div>`;
        items.forEach(print => {
            html += buildInvPrintCard(print);
        });
    }
    grid.innerHTML = html;
}

function buildInvPrintCard(print) {
    const imgUrl = print.imageUrl || '';
    const imgAttr = esc(imgUrl);
    const imgJs = jsq(imgUrl);
    const printId = jsq(print.id || '');
    const timeStr = invTimeLabel(print.createdAt);
    const worldName = esc(print.worldName || '');
    const note = esc(print.note || '');
    const noPreviewHtml = `<div class=\\'inv-no-preview\\'>${esc(t('inventory.empty.no_preview', 'No Preview'))}</div>`;

    const actions = `<div class="lib-actions">
        ${imgUrl ? `<button class="vrcn-lib-button clip" onclick="event.stopPropagation();invDownload('${imgJs}','print.png')" title="${esc(t('inventory.actions.download', 'Download'))}"><span class="msi" style="font-size:16px;">download</span></button>` : ''}
        <button class="vrcn-lib-button del" onclick="event.stopPropagation();invConfirmDeletePrint('${printId}')" title="${esc(t('inventory.actions.delete', 'Delete'))}"><span class="msi" style="font-size:16px;">delete</span></button>
    </div>`;

    const metaParts = [];
    if (worldName) metaParts.push(`<span>${worldName}</span>`);
    if (timeStr) metaParts.push(`<span>${timeStr}</span>`);

    return `<div class="lib-card inv-card">
        ${actions}
        <div class="lib-thumb-wrap" onclick="${imgUrl ? `openLightbox('${imgJs}','image')` : ''}">
            ${imgUrl
                ? `<img class="lib-thumb" src="${imgAttr}" loading="lazy" onerror="this.outerHTML='${noPreviewHtml}'">`
                : invNoPreviewMarkup()
            }
        </div>
        <div class="lib-info">
            <div class="lib-name">${note || esc(t('inventory.card.print', 'Print'))}</div>
            <div class="lib-meta">${metaParts.join('')}</div>
        </div>
    </div>`;
}

function renderInvInventory(items) {
    invInventoryCache = items;

    const grid = document.getElementById('invGrid');
    if (!grid) return;

    const count = document.getElementById('invCount');
    if (count) count.textContent = invCountText('items', items.length);

    if (!items.length) {
        grid.innerHTML = `<div class="empty-msg">${t('inventory.empty.inventory', 'No inventory items found.')}<br><span style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);">${esc(t('inventory.empty.inventory_desc', 'Items you own appear here (props, emojis, stickers from bundles).'))}</span></div>`;
        return;
    }

    let html = '';
    items.forEach(item => {
        html += buildInvItemCard(item);
    });
    grid.innerHTML = html;
}

function buildInvItemCard(item) {
    const imgUrl = item.imageUrl || '';
    const imgAttr = esc(imgUrl);
    const imgJs = jsq(imgUrl);
    const nameDisp = esc(item.name || t('inventory.card.item', 'Item'));
    const typeLabel = esc(item.itemType || '');
    const noPreviewHtml = `<div class=\\'inv-no-preview\\'>${esc(t('inventory.empty.no_preview', 'No Preview'))}</div>`;

    return `<div class="lib-card inv-card">
        <div class="lib-thumb-wrap" onclick="${imgUrl ? `openLightbox('${imgJs}','image')` : ''}">
            ${imgUrl
                ? `<img class="lib-thumb" src="${imgAttr}" loading="lazy" onerror="this.outerHTML='${noPreviewHtml}'">`
                : invNoPreviewMarkup()
            }
            ${typeLabel ? `<span class="inv-anim-badge vrcn-badge accent">${typeLabel.toUpperCase()}</span>` : ''}
        </div>
        <div class="lib-info">
            <div class="lib-name">${nameDisp}</div>
        </div>
    </div>`;
}

function handleInvInventoryResult(payload) {
    if (payload.error) {
        renderInvFetchError(payload.error);
        return;
    }
    renderInvInventory(payload.items || []);
}

function invBrowseUpload() {
    const info = INV_TABS[activeInvTab];
    if (!info?.canUpload) return;
    sendToCS({ action: 'invBrowseUpload', tag: info.tag });
}

function handleInvUploadResult(payload) {
    if (payload.success && payload.file) {
        const tag = payload.tag;
        if (!invFilesCache[tag]) invFilesCache[tag] = [];
        invFilesCache[tag].unshift(payload.file);
        if (INV_TABS[activeInvTab]?.tag === tag) {
            renderInvFiles(invFilesCache[tag], activeInvTab);
        }
        iuHandleUploadDone(true, payload.file);
        showToast(true, t('inventory.toast.upload_success', 'Uploaded successfully!'));
        return;
    }

    iuHandleUploadDone(false, null);
    showToast(false, payload.error || t('inventory.toast.upload_failed', 'Upload failed'));
}

function invConfirmDeleteFile(fileId) {
    const tag = INV_TABS[activeInvTab]?.tag;
    const file = tag && invFilesCache[tag] ? invFilesCache[tag].find(entry => entry.id === fileId) : null;
    showInvDeleteModal('file', fileId, null, file?.name || t('inventory.delete.this_item', 'this item'));
}

function invConfirmDeletePrint(printId) {
    const print = invPrintsCache.find(entry => entry.id === printId);
    showInvDeleteModal('print', printId, null, print?.note || t('inventory.delete.this_print', 'this print'));
}

function showInvDeleteModal(type, id, versionId, name) {
    _invPendingDelete = { type, id, versionId };

    const existing = document.getElementById('invDeleteModal');
    if (existing) existing.remove();

    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    overlay.style.display = 'flex'; // inline display required by _closeTopModal (Escape)
    overlay.id = 'invDeleteModal';
    overlay.onclick = event => { if (event.target === overlay) closeInvDeleteModal(); };
    overlay.innerHTML = `<div class="modal-box">${renderModalBar(t('inventory.modal.delete_title', 'Delete Item'), [modalCloseAction('closeInvDeleteModal()')])}<div class="modal-icon danger" style="margin-top:20px;"><span class="msi" style="font-size:22px;">delete</span></div><div class="modal-msg">${esc(t('inventory.modal.delete_message', 'Permanently delete from VRChat:'))}<br><span class="modal-fname">${esc(name)}</span><br><span style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx2);">${esc(t('inventory.modal.delete_irreversible', 'This cannot be undone.'))}</span></div><div class="modal-btns"><button class="vrcn-button-round vrcn-btn-danger" onclick="confirmInvDelete()">${esc(t('inventory.actions.delete', 'Delete'))}</button></div></div>`;
    document.body.appendChild(overlay);

    overlay.querySelector('.fd-modal-bar-actions .fd-action-btn')?.focus();

    const handler = event => {
        if (event.key === 'Escape') {
            closeInvDeleteModal();
            document.removeEventListener('keydown', handler);
        }
        if (event.key === 'Enter') {
            confirmInvDelete();
            document.removeEventListener('keydown', handler);
        }
    };
    document.addEventListener('keydown', handler);
}

function closeInvDeleteModal() {
    _invPendingDelete = null;
    const modal = document.getElementById('invDeleteModal');
    if (modal) modal.remove();
}

function confirmInvDelete() {
    if (!_invPendingDelete) {
        closeInvDeleteModal();
        return;
    }

    const { type, id } = _invPendingDelete;
    if (type === 'file') sendToCS({ action: 'invDeleteFile', fileId: id });
    if (type === 'print') sendToCS({ action: 'invDeletePrint', printId: id });
    closeInvDeleteModal();
}

function handleInvDeleteResult(payload) {
    if (payload.success) {
        if (typeof onAvGalleryItemDeleted === 'function') onAvGalleryItemDeleted(payload.fileId);
        _invPendingFileDeletes.add(payload.fileId);
        setTimeout(() => _invPendingFileDeletes.delete(payload.fileId), 10 * 60 * 1000);

        const tag = INV_TABS[activeInvTab]?.tag;
        if (tag && invFilesCache[tag]) {
            invFilesCache[tag] = invFilesCache[tag].filter(file => file.id !== payload.fileId);
            renderInvFiles(invFilesCache[tag], activeInvTab);
        }
        showToast(true, t('inventory.toast.deleted', 'Deleted'));
        return;
    }

    showToast(false, t('inventory.toast.delete_failed', 'Delete failed'));
}

function handleInvPrintDeleteResult(payload) {
    if (payload.success) {
        _invPendingPrintDeletes.add(payload.printId);
        setTimeout(() => _invPendingPrintDeletes.delete(payload.printId), 10 * 60 * 1000);

        invPrintsCache = invPrintsCache.filter(print => print.id !== payload.printId);
        renderInvPrints(invPrintsCache);
        showToast(true, t('inventory.toast.print_deleted', 'Print deleted'));
        return;
    }

    showToast(false, t('inventory.toast.delete_failed', 'Delete failed'));
}

function invDownload(url, fileName) {
    sendToCS({ action: 'invDownload', url, fileName });
}

function rerenderInventoryTranslations() {
    const grid = document.getElementById('invGrid');
    if (!grid) return;

    const tab = activeInvTab || 'photos';
    const info = INV_TABS[tab];
    const cached = tab === 'prints'
        ? (Array.isArray(invPrintsCache) ? invPrintsCache : null)
        : tab === 'inventory'
            ? (Array.isArray(invInventoryCache) ? invInventoryCache : null)
            : (info?.tag && Array.isArray(invFilesCache[info.tag]) ? invFilesCache[info.tag] : null);

    if (cached) {
        if (tab === 'prints') renderInvPrints(cached);
        else if (tab === 'inventory') renderInvInventory(cached);
        else renderInvFiles(cached, tab);
        return;
    }

    const empty = grid.querySelector('.empty-msg');
    if (empty) empty.textContent = t('inventory.select_category', 'Select a category above');
}

document.documentElement.addEventListener('languagechange', rerenderInventoryTranslations);

function formatFileSize(bytes) {
    if (!bytes || bytes === 0) return '';
    if (bytes < 1024) return bytes + ' B';
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
    return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
}

function handleInvPrintUploadResult(payload) {
    if (payload.success) {
        if (typeof closeInvUploadModal === 'function') closeInvUploadModal();
        showToast(true, t('inventory.upload.print_done', 'Print uploaded'));
        invPrintsCache = [];
        if (activeInvTab === 'prints') refreshInventory(true);
        return;
    }
    if (typeof iuHandleUploadDone === 'function') iuHandleUploadDone(false, null);
    showToast(false, payload.error || t('inventory.upload.print_failed', 'Print upload failed'));
}
