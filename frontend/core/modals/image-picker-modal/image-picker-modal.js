/* === Image Picker Modal === */
let _pickerContext = null; // { type: 'profile-icon'|'profile-banner'|'group-icon'|'group-banner', targetId }

function openImagePicker(type, targetId) {
    _pickerContext = { type, targetId: targetId || null };
    window._pickerSelectedUrl = null;

    const isIcon = type.endsWith('-icon');
    const tag    = isIcon ? 'icon' : 'gallery';
    const title  = isIcon
        ? t('profiles.picker.select_icon', 'Select Icon')
        : t('profiles.picker.select_banner', 'Select Banner Photo');

    // Build overlay fresh each time so it's always above current modal stack
    let overlay = document.getElementById('imagePickerOverlay');
    if (overlay) overlay.remove();

    overlay = document.createElement('div');
    overlay.id = 'imagePickerOverlay';
    overlay.setAttribute('data-wm-portal', '');
    overlay.style.cssText = 'position:fixed;inset:0;z-index:10003;background:rgba(0,0,0,.55);display:flex;align-items:center;justify-content:center;animation:fadeIn .12s ease;backdrop-filter:blur(4px);';
    overlay.innerHTML = `
        <div class="gp-modal" style="width:460px;max-height:80vh;display:flex;flex-direction:column;">
            ${renderModalBar(title, [modalCloseAction('closeImagePicker()')], { titleId: 'imagePickerTitle' })}
            <div class="gp-modal-body" style="flex:1;overflow-y:auto;">
                <div id="imagePickerGrid" style="display:grid;grid-template-columns:repeat(auto-fill,minmax(80px,1fr));gap:6px;padding:4px 0;">
                    <div style="grid-column:1/-1;text-align:center;padding:20px;font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);">${t('common.loading', 'Loading...')}</div>
                </div>
            </div>
            <div class="gp-modal-footer" style="display:flex;align-items:center;gap:8px;">
                <button class="vrcn-button" id="imagePickerUpload" onclick="imagePickerUpload()">
                    <span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">upload</span>${t('inventory.actions.upload', 'Upload')}
                </button>
                <span style="flex:1;"></span>
                <button class="vrcn-button-round vrcn-btn-join" id="imagePickerApply" disabled onclick="applyImagePicker()" style="opacity:.45;">
                    <span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">check</span>${t('common.apply', 'Apply')}
                </button>
            </div>
        </div>`;
    document.body.appendChild(overlay);
    overlay.addEventListener('click', e => { if (e.target === overlay) closeImagePicker(); });

    // Always fetch fresh to avoid stale or mismatched inventory cache data.
    const pickerGrid = document.getElementById('imagePickerGrid');
    if (pickerGrid) pickerGrid.dataset.state = 'loading';
    sendToCS({ action: 'invGetFiles', tag });
}

function _renderImagePickerGrid(files) {
    const grid = document.getElementById('imagePickerGrid');
    if (!grid) return;
    if (!files || !files.length) {
        grid.dataset.state = 'empty';
        grid.innerHTML = `<div style="grid-column:1/-1;text-align:center;padding:20px;font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);">${t('profiles.picker.no_items', 'No items found.')}<br>${t('profiles.picker.upload_via_inventory', 'Upload images via the Inventory tab.')}</div>`;
        return;
    }
    grid.dataset.state = 'loaded';
    grid.innerHTML = files.map(f => {
        const url = f.fileUrl || '';
        const fid = f.id || '';
        if (!url) return '';
        return `<img src="${esc(url)}" data-file-id="${esc(fid)}" style="width:100%;aspect-ratio:1;object-fit:cover;border-radius:6px;cursor:pointer;opacity:.85;transition:opacity .15s,outline .12s;" onmouseover="this.style.opacity=1" onmouseout="this.style.opacity=.85" onclick="selectPickerImage(this,'${jsq(url)}','${jsq(fid)}')" onerror="this.parentElement?.remove()">`;
    }).join('');
}

function selectPickerImage(el, url, fileId) {
    document.querySelectorAll('#imagePickerGrid img').forEach(i => { i.style.opacity = '.85'; i.style.outline = 'none'; });
    el.style.opacity = '1';
    el.style.outline = '2px solid var(--accent)';
    window._pickerSelectedUrl = url;
    window._pickerSelectedFileId = fileId || '';
    const btn = document.getElementById('imagePickerApply');
    if (btn) { btn.disabled = false; btn.style.opacity = '1'; }
}

function imagePickerUpload() {
    if (!_pickerContext || typeof openInvUploadModal !== 'function') return;
    const isIcon = _pickerContext.type.endsWith('-icon');
    const tag = isIcon ? 'icon' : 'gallery';
    openInvUploadModal(isIcon ? 'icons' : 'photos', file => {
        if (!document.getElementById('imagePickerOverlay') || !_pickerContext) return;
        const files = (typeof invFilesCache !== 'undefined' && invFilesCache[tag]) || [file];
        _renderImagePickerGrid(files);
        const el = document.querySelector(`#imagePickerGrid img[data-file-id="${CSS.escape(file.id || '')}"]`);
        if (el) selectPickerImage(el, file.fileUrl || '', file.id || '');
    });
}

function closeImagePicker() {
    const overlay = document.getElementById('imagePickerOverlay');
    if (overlay) overlay.remove();
    _pickerContext = null;
    window._pickerSelectedUrl = null;
    window._pickerSelectedFileId = null;
}

function refreshImagePickerTranslations() {
    const overlay = document.getElementById('imagePickerOverlay');
    if (!overlay || !_pickerContext) return;
    const isIcon = _pickerContext.type.endsWith('-icon');
    const titleEl = document.getElementById('imagePickerTitle');
    const newTitle = isIcon
        ? t('profiles.picker.select_icon', 'Select Icon')
        : t('profiles.picker.select_banner', 'Select Banner Photo');
    if (titleEl) { titleEl.textContent = newTitle; titleEl.title = newTitle; }
    const closeBtn = overlay.querySelector('.fd-modal-bar-actions .fd-action-btn');
    if (closeBtn) closeBtn.title = t('common.close', 'Close');
    const uploadBtn = document.getElementById('imagePickerUpload');
    if (uploadBtn) uploadBtn.innerHTML = `<span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">upload</span>${t('inventory.actions.upload', 'Upload')}`;
    const applyBtn = document.getElementById('imagePickerApply');
    if (applyBtn) applyBtn.innerHTML = `<span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">check</span>${t('common.apply', 'Apply')}`;
    const grid = document.getElementById('imagePickerGrid');
    if (!grid || grid.querySelector('img')) return;
    if (grid.dataset.state === 'empty') {
        grid.innerHTML = `<div style="grid-column:1/-1;text-align:center;padding:20px;font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);">${t('profiles.picker.no_items', 'No items found.')}<br>${t('profiles.picker.upload_via_inventory', 'Upload images via the Inventory tab.')}</div>`;
    } else {
        grid.innerHTML = `<div style="grid-column:1/-1;text-align:center;padding:20px;font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);">${t('common.loading', 'Loading...')}</div>`;
    }
}

function applyImagePicker() {
    const url    = window._pickerSelectedUrl;
    const fileId = window._pickerSelectedFileId;
    if (!url || !_pickerContext) return;
    const { type, targetId } = _pickerContext;

    // Profile: pass URL (VRChat user API accepts CDN URLs directly)
    if (type === 'profile-icon')        sendToCS({ action: 'vrcUpdateProfile', userIcon: url });
    else if (type === 'profile-banner') sendToCS({ action: 'vrcUpdateProfileBanner', bannerCustomUrl: url });
    // Groups: VRChat group API requires file IDs (iconId/bannerId), not URLs
    else if (type === 'group-icon')     sendToCS({ action: 'vrcUpdateGroup', groupId: targetId, iconId:   fileId });
    else if (type === 'group-banner')   sendToCS({ action: 'vrcUpdateGroup', groupId: targetId, bannerId: fileId });
    else if (type === 'create-group-icon' || type === 'create-group-banner') { if (typeof cgApplyPickedImage === 'function') cgApplyPickedImage(type.endsWith('-icon') ? 'icon' : 'banner', fileId, url); }

    closeImagePicker();
}

// Called from messages.js when invFiles arrives while picker is open and waiting
function onImagePickerFilesLoaded(files, tag) {
    const overlay = document.getElementById('imagePickerOverlay');
    if (!overlay || !_pickerContext) return;
    const expectedTag = _pickerContext.type.endsWith('-icon') ? 'icon' : 'gallery';
    if (tag !== expectedTag) return;
    _renderImagePickerGrid(files);
}
