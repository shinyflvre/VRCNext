/* === Inventory Upload Modal === */

const INV_UPLOAD_REQS = {
    avatarGallery: {
        maxMB: 3,
        ratioW: null,
        ratioH: null,
        minPx: 1,
        maxPx: 2000,
        directBlob: true,
        get hint() { return t('avatars.upload.reqs.gallery', 'PNG/JPG, max 3 MB, max 2000x2000'); }
    },
    avatarImage: {
        maxMB: 3,
        ratioW: null,
        ratioH: null,
        minPx: 1,
        maxPx: 2000,
        directBlob: true,
        get hint() { return t('avatars.upload.reqs.image', 'PNG/JPG, max 3 MB, max 2000x2000'); }
    },
    worldImage: {
        maxMB: 3,
        ratioW: null,
        ratioH: null,
        minPx: 1,
        maxPx: 2000,
        directBlob: true,
        get hint() { return t('worlds.upload.reqs.image', 'PNG/JPG, max 3 MB, max 2000x2000'); }
    },
    prints: {
        maxMB: 10,
        ratioW: null,
        ratioH: null,
        minPx: 64,
        maxPx: 2048,
        get hint() { return t('inventory.upload.reqs.prints', 'PNG/JPG, max 10 MB, up to 2048x2048'); }
    },
    photos: {
        maxMB: 8,
        ratioW: null,
        ratioH: null,
        minPx: 64,
        maxPx: 2048,
        get hint() { return t('inventory.upload.reqs.photos', 'PNG, max 8 MB, 64x64 to 2048x2048, any aspect ratio'); }
    },
    icons: {
        maxMB: 8,
        ratioW: 1,
        ratioH: 1,
        minPx: 64,
        maxPx: 2048,
        get hint() { return t('inventory.upload.reqs.icons', 'PNG, max 8 MB, 64x64 to 2048x2048, aspect ratio 1:1, requires VRC+'); }
    },
    emojis: {
        maxMB: 8,
        ratioW: 1,
        ratioH: 1,
        targetW: 1024,
        targetH: 1024,
        hasAnimStyle: true,
        get hint() { return t('inventory.upload.reqs.emojis', 'PNG, max 8 MB, 1024x1024, aspect ratio 1:1, max 18, requires VRC+'); }
    },
    stickers: {
        maxMB: 8,
        ratioW: 1,
        ratioH: 1,
        targetW: 1024,
        targetH: 1024,
        get hint() { return t('inventory.upload.reqs.stickers', 'PNG, max 8 MB, max 1024x1024, aspect ratio 1:1, max 18, requires VRC+'); }
    }
};

const IU_ANIM_STYLES = [
    { value: 'aura', key: 'inventory.upload.anim.aura', fallback: 'Aura' },
    { value: 'bats', key: 'inventory.upload.anim.bats', fallback: 'Bats' },
    { value: 'bees', key: 'inventory.upload.anim.bees', fallback: 'Bees' },
    { value: 'bounce', key: 'inventory.upload.anim.bounce', fallback: 'Bounce' },
    { value: 'cloud', key: 'inventory.upload.anim.cloud', fallback: 'Cloud' },
    { value: 'confetti', key: 'inventory.upload.anim.confetti', fallback: 'Confetti' },
    { value: 'crying', key: 'inventory.upload.anim.crying', fallback: 'Crying' },
    { value: 'dislike', key: 'inventory.upload.anim.dislike', fallback: 'Dislike' },
    { value: 'fire', key: 'inventory.upload.anim.fire', fallback: 'Fire' },
    { value: 'idea', key: 'inventory.upload.anim.idea', fallback: 'Idea' },
    { value: 'lasers', key: 'inventory.upload.anim.lasers', fallback: 'Lasers' },
    { value: 'like', key: 'inventory.upload.anim.like', fallback: 'Like' },
    { value: 'magnet', key: 'inventory.upload.anim.magnet', fallback: 'Magnet' },
    { value: 'mistletoe', key: 'inventory.upload.anim.mistletoe', fallback: 'Mistletoe' },
    { value: 'money', key: 'inventory.upload.anim.money', fallback: 'Money' },
    { value: 'noise', key: 'inventory.upload.anim.noise', fallback: 'Noise' },
    { value: 'orbit', key: 'inventory.upload.anim.orbit', fallback: 'Orbit' },
    { value: 'pizza', key: 'inventory.upload.anim.pizza', fallback: 'Pizza' },
    { value: 'rain', key: 'inventory.upload.anim.rain', fallback: 'Rain' },
    { value: 'rotate', key: 'inventory.upload.anim.rotate', fallback: 'Rotate' },
    { value: 'shake', key: 'inventory.upload.anim.shake', fallback: 'Shake' },
    { value: 'snow', key: 'inventory.upload.anim.snow', fallback: 'Snow' },
    { value: 'snowball', key: 'inventory.upload.anim.snowball', fallback: 'Snowball' },
    { value: 'spin', key: 'inventory.upload.anim.spin', fallback: 'Spin' },
    { value: 'splash', key: 'inventory.upload.anim.splash', fallback: 'Splash' },
    { value: 'stop', key: 'inventory.upload.anim.stop', fallback: 'Stop' },
    { value: 'zzz', key: 'inventory.upload.anim.zzz', fallback: 'ZZZ' }
];

const IU_MASK_TAG_DEFAULT = 'square';

let _iuTab = '';
let _iuCallback = null;
let _iuFile = null;
let _iuImg = null;
let _iuAnimStyle = 'aura';
let _iuTooBig = false;
let _iuWasCropped = false;
let _iuWasCompressed = false;
let _iuWasResized = false;

let _iuCanvas = null;
let _iuCtx = null;
let _iuScale = 1;
let _iuOffX = 0;
let _iuOffY = 0;
let _iuDragging = false;
let _iuDragLast = { x: 0, y: 0 };
let _iuCropW = 0;
let _iuCropH = 0;

function iuUploadButtonHtml(isUploading = false) {
    return isUploading
        ? `<span class="msi" style="font-size:14px;">hourglass_empty</span> ${esc(t('inventory.upload.uploading', 'Uploading...'))}`
        : `<span class="msi" style="font-size:14px;">upload</span> ${esc(t('inventory.actions.upload', 'Upload'))}`;
}

function iuAnimLabel(style) {
    return t(style.key, style.fallback);
}

// Media Library -> upload. Opens the upload modal pre-loaded with an existing
// library image (fetched from its local URL), so the crop/compress/resize
// pipeline applies just like a hand-picked file.
function mediaLibUploadFlow(tab, sourceUrl, sourceName, callback) {
    if (!sourceUrl) return;
    openInvUploadModal(tab, callback || null);
    iuLoadFromUrl(sourceUrl, sourceName);
}

function mediaSetAsProfileIcon(url, name) {
    mediaLibUploadFlow('icons', url, name, file => {
        if (file?.fileUrl) sendToCS({ action: 'vrcUpdateProfile', userIcon: file.fileUrl });
    });
}

function mediaSetAsProfileBanner(url, name) {
    mediaLibUploadFlow('photos', url, name, file => {
        if (file?.fileUrl) sendToCS({ action: 'vrcUpdateProfileBanner', bannerCustomUrl: file.fileUrl });
    });
}

function mediaUploadToPhotos(url, name) { mediaLibUploadFlow('photos', url, name, null); }
function mediaUploadToIcons(url, name)  { mediaLibUploadFlow('icons',  url, name, null); }

// Fetch a library image and normalize it to a PNG File via a same-origin blob
// URL (never taints the canvas), then hand it to the normal upload pipeline.
// Flattens GIFs to their first frame, matching VRChat's static icon/photo uploads.
function iuLoadFromUrl(url, name) {
    iuClearError();
    fetch(url)
        .then(resp => { if (!resp.ok) throw new Error('fetch failed'); return resp.blob(); })
        .then(srcBlob => {
            const objUrl = URL.createObjectURL(srcBlob);
            const img = new Image();
            img.onload = () => {
                const canvas = document.createElement('canvas');
                canvas.width = img.naturalWidth;
                canvas.height = img.naturalHeight;
                canvas.getContext('2d').drawImage(img, 0, 0);
                URL.revokeObjectURL(objUrl);
                canvas.toBlob(blob => {
                    if (!blob) { iuShowError(t('inventory.upload.error.load_failed', 'Failed to load image.')); return; }
                    const base = (name || 'image').replace(/\.[^.]+$/, '');
                    iuHandleFile(new File([blob], base + '.png', { type: 'image/png' }));
                }, 'image/png');
            };
            img.onerror = () => { URL.revokeObjectURL(objUrl); iuShowError(t('inventory.upload.error.load_failed', 'Failed to load image.')); };
            img.src = objUrl;
        })
        .catch(() => iuShowError(t('inventory.upload.error.load_failed', 'Failed to load image.')));
}

function openInvUploadModal(tabOverride, callback) {
    const tab = tabOverride || activeInvTab;
    if (!tabOverride && !INV_TABS[tab]?.canUpload) return;
    if (!INV_UPLOAD_REQS[tab]) return;

    _iuTab = tab;
    _iuCallback = callback || null;
    _iuFile = null;
    _iuImg = null;
    _iuAnimStyle = 'aura';
    _iuCanvas = null;
    _iuCtx = null;
    _iuTooBig = false;
    _iuWasCropped = false;
    _iuWasCompressed = false;
    _iuWasResized = false;

    const existing = document.getElementById('invUploadModal');
    if (existing) existing.remove();

    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    overlay.style.display = 'flex'; // inline display required by _closeTopModal (Escape)
    overlay.id = 'invUploadModal';
    overlay.style.zIndex = '20010';
    overlay.onclick = event => { if (event.target === overlay) closeInvUploadModal(); };
    overlay.innerHTML = _iuBuildHTML(tab);
    document.body.appendChild(overlay);

    const animSel = document.getElementById('iuAnimSelect');
    if (animSel && typeof initVnSelect === 'function') initVnSelect(animSel);
    iuUpdateAnimPreview();

    const handler = event => {
        if (event.key === 'Escape') {
            closeInvUploadModal();
            document.removeEventListener('keydown', handler);
        }
    };

    document.addEventListener('keydown', handler);
    overlay._kh = handler;
}

function closeInvUploadModal() {
    const modal = document.getElementById('invUploadModal');
    if (modal) {
        if (modal._kh) document.removeEventListener('keydown', modal._kh);
        modal.remove();
    }
    _iuCanvas = null;
    _iuCtx = null;
}

function _iuBuildHTML(tab) {
    const req = INV_UPLOAD_REQS[tab];
    const tabLabel = INV_TABS[tab]?.label || tab;
    const browseHtml = `<span style="color:var(--accent);">${esc(t('inventory.upload.browse', 'browse'))}</span>`;
    const dropPrompt = tf('inventory.upload.drop_prompt', { browse: browseHtml }, `Drop image here or ${browseHtml}`);
    const emojiHtml = req?.hasAnimStyle ? `
        <div id="iuEmojiOptions" style="display:none;margin-top:14px;">
            <div style="font-size:calc(12px + var(--fs-off, 0px));font-weight:600;color:var(--tx0);margin-bottom:8px;">${esc(t('inventory.upload.particle_style', 'Particle style'))}</div>
            <select id="iuAnimSelect" class="vrcn-dropdown" style="width:100%;" onchange="iuSetAnimStyle(this.value)">
                ${IU_ANIM_STYLES.map(style => `<option value="${esc(style.value)}"${style.value === 'aura' ? ' selected' : ''}>${esc(iuAnimLabel(style))}</option>`).join('')}
            </select>
            <div style="margin-top:12px;display:flex;justify-content:center;align-items:center;min-height:120px;background:var(--bg-input);border-radius:8px;padding:10px;">
                <img id="iuAnimPreviewImg" alt="" style="max-width:100%;max-height:200px;border-radius:6px;">
            </div>
        </div>` : '';

    return `<div class="modal-box wide" id="invUploadContent" style="max-width:560px;">
        ${renderModalBar(tf('inventory.upload.title', { tab: tabLabel }, `Upload to ${tabLabel}`), [modalCloseAction('closeInvUploadModal()')])}
        <div style="background:var(--bg-input);border-radius:8px;padding:10px 14px;margin:20px 0 14px;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${esc(req?.hint || '')}</div>
        <div id="iuDropZone" class="iu-dropzone"
            onclick="iuBrowse()"
            ondragover="event.preventDefault();this.classList.add('dragover')"
            ondragleave="this.classList.remove('dragover')"
            ondrop="iuDrop(event)">
            <span class="msi" style="font-size:40px;color:var(--tx2);display:block;margin-bottom:10px;pointer-events:none;">upload_file</span>
            <div style="font-size:calc(14px + var(--fs-off, 0px));font-weight:600;color:var(--tx0);pointer-events:none;">${dropPrompt}</div>
            <div style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx2);margin-top:6px;pointer-events:none;">${esc(t('inventory.upload.file_types', 'PNG, JPG, JPEG'))}</div>
            <input type="file" id="iuFileInput" accept="image/png,image/jpeg" style="display:none;" onchange="iuHandleFileInput(this)">
        </div>
        <div id="iuEditorArea" style="display:none;"></div>
        <div id="iuPreviewArea" style="display:none;"></div>
        ${emojiHtml}
        <div id="iuError" style="display:none;margin-top:10px;padding:10px 14px;background:rgba(220,50,50,.12);border-radius:8px;font-size:calc(12px + var(--fs-off, 0px));color:#e05252;"></div>
        <div style="display:flex;gap:8px;margin-top:16px;justify-content:flex-end;">
            <button class="vrcn-button-round vrcn-btn-join" id="iuUploadBtn" style="display:none;" onclick="iuDoUpload()">${iuUploadButtonHtml()}</button>
        </div>
    </div>`;
}

function iuBrowse() {
    document.getElementById('iuFileInput')?.click();
}

function iuDrop(event) {
    event.preventDefault();
    event.currentTarget.classList.remove('dragover');
    const file = event.dataTransfer.files?.[0];
    if (file) iuHandleFile(file);
}

function iuHandleFileInput(input) {
    const file = input.files?.[0];
    if (file) iuHandleFile(file);
}

function iuHandleFile(file) {
    _iuFile = null;
    _iuImg = null;
    _iuTooBig = false;
    iuClearError();

    const nameLower = file.name.toLowerCase();
    const validType = file.type.includes('png')
        || file.type.includes('jpeg')
        || nameLower.endsWith('.png')
        || nameLower.endsWith('.jpg')
        || nameLower.endsWith('.jpeg');

    if (!validType) {
        iuShowError(t('inventory.upload.error.invalid_type', 'Only PNG, JPG, and JPEG files are supported.'));
        return;
    }

    const req = INV_UPLOAD_REQS[_iuTab];
    const isTooBig = req && file.size > req.maxMB * 1024 * 1024;

    const reader = new FileReader();
    reader.onload = event => {
        const img = new Image();
        img.onload = () => {
            _iuFile = file;
            _iuImg = img;
            _iuTooBig = isTooBig;

            if (isTooBig) {
                const size = (file.size / 1024 / 1024).toFixed(1);
                iuShowError(
                    tf('inventory.upload.error.too_large', { size, max: req.maxMB }, `File too large (${size} MB). Maximum is ${req.maxMB} MB.`),
                    `<button class="vrcn-button-round" style="margin-top:8px;font-size:calc(12px + var(--fs-off, 0px));" onclick="iuCompressAndContinue()"><span class="msi" style="font-size:13px;vertical-align:-2px;">compress</span> ${esc(t('inventory.upload.button.compress_image', 'Compress Image'))}</button>`
                );
                return;
            }

            _iuValidateAndShow(img, file, req);
        };
        img.onerror = () => iuShowError(t('inventory.upload.error.load_failed', 'Failed to load image.'));
        img.src = event.target.result;
    };
    reader.readAsDataURL(file);
}

function _iuValidateAndShow(img, file, req) {
    if (!req) {
        _iuShowPreview(img, file, false, false, false);
        return;
    }

    if (req.minPx != null && (img.naturalWidth < req.minPx || img.naturalHeight < req.minPx)) {
        iuShowError(tf('inventory.upload.error.too_small', { size: req.minPx }, `Image too small. Minimum size is ${req.minPx}x${req.minPx} px.`));
        return;
    }

    if (req.maxPx != null && (img.naturalWidth > req.maxPx || img.naturalHeight > req.maxPx)) {
        _iuAutoResize(img, req);
        return;
    }

    if (req.ratioW == null) {
        _iuShowPreview(img, file, false, false, false);
        return;
    }

    const imgRatio = img.naturalWidth / img.naturalHeight;
    const targetRatio = req.ratioW / req.ratioH;
    const ratioOk = Math.abs(imgRatio - targetRatio) < 0.02;

    if (!ratioOk) {
        _iuShowEditor(img, req);
    } else {
        _iuShowPreview(img, file, false, false, false);
    }
}

function _iuShowPreview(img, file, wasCropped, wasCompressed, wasResized) {
    const dropZone = document.getElementById('iuDropZone');
    const editorArea = document.getElementById('iuEditorArea');
    const previewArea = document.getElementById('iuPreviewArea');
    const uploadBtn = document.getElementById('iuUploadBtn');
    const emojiOptions = document.getElementById('iuEmojiOptions');

    _iuWasCropped = !!wasCropped;
    _iuWasCompressed = !!wasCompressed;
    _iuWasResized = !!wasResized;

    if (dropZone) dropZone.style.display = 'none';
    if (editorArea) editorArea.style.display = 'none';

    const sizeStr = file.size < 1024 * 1024
        ? `${(file.size / 1024).toFixed(0)} KB`
        : `${(file.size / 1024 / 1024).toFixed(2)} MB`;
    const dimStr = `${img.naturalWidth}x${img.naturalHeight}`;

    if (previewArea) {
        previewArea.style.display = 'flex';
        previewArea.style.gap = '16px';
        previewArea.style.alignItems = 'flex-start';
        previewArea.innerHTML = `
            <div style="flex-shrink:0;width:120px;height:120px;border-radius:8px;overflow:hidden;background:var(--bg-input);display:flex;align-items:center;justify-content:center;">
                <img src="${esc(img.src)}" style="max-width:100%;max-height:100%;object-fit:contain;">
            </div>
            <div style="flex:1;min-width:0;">
                <div style="font-weight:600;color:var(--tx0);margin-bottom:4px;word-break:break-all;">${esc(file.name)}</div>
                <div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);margin-bottom:4px;">${dimStr} - ${sizeStr}</div>
                ${wasCropped ? `<div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--accent);margin-bottom:4px;"><span class="msi" style="font-size:13px;vertical-align:-3px;">crop</span> ${esc(t('inventory.upload.status.cropped', 'Cropped to fit'))}</div>` : ''}
                ${wasResized ? `<div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--accent);margin-bottom:4px;"><span class="msi" style="font-size:13px;vertical-align:-3px;">photo_size_select_large</span> ${esc(t('inventory.upload.status.resized', 'Resized to fit'))}</div>` : ''}
                ${wasCompressed ? `<div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--accent);margin-bottom:4px;"><span class="msi" style="font-size:13px;vertical-align:-3px;">compress</span> ${esc(t('inventory.upload.status.compressed', 'Compressed'))}</div>` : ''}
                <div style="font-size:calc(12px + var(--fs-off, 0px));color:#4caf50;"><span class="msi" style="font-size:13px;vertical-align:-3px;">check_circle</span> ${esc(t('inventory.upload.status.ready', 'Ready to upload'))}</div>
                <button class="vrcn-button-round" style="margin-top:10px;" onclick="iuReset()">${esc(t('inventory.upload.button.choose_different', 'Choose different'))}</button>
            </div>`;
    }

    if (emojiOptions) emojiOptions.style.display = _iuTab === 'emojis' ? '' : 'none';
    if (uploadBtn) {
        uploadBtn.style.display = '';
        if (!uploadBtn.disabled) uploadBtn.innerHTML = iuUploadButtonHtml(false);
    }
}

function _iuShowEditor(img, req) {
    const dropZone = document.getElementById('iuDropZone');
    const editorArea = document.getElementById('iuEditorArea');
    const previewArea = document.getElementById('iuPreviewArea');
    const uploadBtn = document.getElementById('iuUploadBtn');

    _iuWasCropped = false;
    _iuWasCompressed = false;
    _iuWasResized = false;

    if (dropZone) dropZone.style.display = 'none';
    if (previewArea) previewArea.style.display = 'none';
    if (uploadBtn) uploadBtn.style.display = 'none';

    const ratio = req.ratioW / req.ratioH;

    if (!editorArea) return;
    editorArea.style.display = '';
    editorArea.innerHTML = `
        <div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx2);margin-bottom:10px;">
            <span class="msi" style="font-size:13px;vertical-align:-3px;">crop</span>
            ${esc(tf('inventory.upload.editor.crop_hint', { ratio: `${req.ratioW}:${req.ratioH}` }, `Crop to ${req.ratioW}:${req.ratioH} - drag to reposition, scroll to zoom`))}
        </div>
        <canvas id="iuCropCanvas" style="border-radius:8px;cursor:grab;user-select:none;display:block;width:100%;touch-action:none;"></canvas>
        <div style="display:flex;align-items:center;gap:10px;margin-top:10px;">
            <span class="msi" style="color:var(--tx2);font-size:16px;">zoom_out</span>
            <input type="range" id="iuZoomSlider" min="10" max="400" value="100" style="flex:1;" oninput="iuSetZoom(this.value/100)">
            <span class="msi" style="color:var(--tx2);font-size:16px;">zoom_in</span>
        </div>
        <div style="display:flex;gap:8px;margin-top:10px;justify-content:flex-end;">
            <button class="vrcn-button-round" style="font-size:calc(12px + var(--fs-off, 0px));" onclick="iuReset()">${esc(t('inventory.upload.button.choose_different', 'Choose different'))}</button>
            <button class="vrcn-button-round vrcn-btn-join" onclick="iuCropAndContinue()">
                <span class="msi" style="font-size:14px;">crop</span> ${esc(t('inventory.upload.button.crop_continue', 'Crop and continue'))}
            </button>
        </div>`;

    const canvas = document.getElementById('iuCropCanvas');
    _iuCanvas = canvas;
    _iuCtx = canvas.getContext('2d');

    const canvasWidth = 480;
    const canvasHeight = Math.min(Math.round(canvasWidth / ratio), 360);
    canvas.width = canvasWidth;
    canvas.height = canvasHeight;

    const pad = 20;
    const availW = canvasWidth - pad * 2;
    const availH = canvasHeight - pad * 2;

    if (availW / ratio <= availH) {
        _iuCropW = availW;
        _iuCropH = Math.round(availW / ratio);
    } else {
        _iuCropH = availH;
        _iuCropW = Math.round(availH * ratio);
    }

    const scaleX = _iuCropW / img.naturalWidth;
    const scaleY = _iuCropH / img.naturalHeight;
    _iuScale = Math.max(scaleX, scaleY);

    _iuOffX = (canvasWidth - img.naturalWidth * _iuScale) / 2;
    _iuOffY = (canvasHeight - img.naturalHeight * _iuScale) / 2;

    const slider = document.getElementById('iuZoomSlider');
    if (slider) slider.value = Math.round(_iuScale * 100);

    _iuDrawCanvas();

    canvas.addEventListener('mousedown', _iuMouseDown);
    canvas.addEventListener('mousemove', _iuMouseMove);
    canvas.addEventListener('mouseup', _iuMouseUp);
    canvas.addEventListener('mouseleave', _iuMouseUp);
    canvas.addEventListener('wheel', _iuWheel, { passive: false });
}

function _iuDrawCanvas() {
    if (!_iuCtx || !_iuImg) return;

    const canvas = _iuCanvas;
    const ctx = _iuCtx;
    const canvasWidth = canvas.width;
    const canvasHeight = canvas.height;
    const cropX = (canvasWidth - _iuCropW) / 2;
    const cropY = (canvasHeight - _iuCropH) / 2;
    const imgW = _iuImg.naturalWidth * _iuScale;
    const imgH = _iuImg.naturalHeight * _iuScale;

    ctx.clearRect(0, 0, canvasWidth, canvasHeight);

    ctx.save();
    for (let y = 0; y < canvasHeight; y += 10) {
        for (let x = 0; x < canvasWidth; x += 10) {
            ctx.fillStyle = ((x + y) / 10 % 2 < 1) ? '#2a2a2a' : '#333';
            ctx.fillRect(x, y, 10, 10);
        }
    }
    ctx.restore();

    ctx.drawImage(_iuImg, _iuOffX, _iuOffY, imgW, imgH);

    ctx.save();
    ctx.fillStyle = 'rgba(0,0,0,0.55)';
    ctx.fillRect(0, 0, canvasWidth, canvasHeight);
    ctx.globalCompositeOperation = 'destination-out';
    ctx.fillStyle = 'rgba(0,0,0,1)';
    ctx.fillRect(cropX, cropY, _iuCropW, _iuCropH);
    ctx.restore();

    ctx.save();
    ctx.beginPath();
    ctx.rect(cropX, cropY, _iuCropW, _iuCropH);
    ctx.clip();
    ctx.drawImage(_iuImg, _iuOffX, _iuOffY, imgW, imgH);
    ctx.restore();

    ctx.strokeStyle = 'rgba(255,255,255,0.85)';
    ctx.lineWidth = 1.5;
    ctx.strokeRect(cropX, cropY, _iuCropW, _iuCropH);

    const handleSize = 10;
    ctx.fillStyle = '#fff';
    [[cropX, cropY], [cropX + _iuCropW - handleSize, cropY], [cropX, cropY + _iuCropH - handleSize], [cropX + _iuCropW - handleSize, cropY + _iuCropH - handleSize]].forEach(([x, y]) => {
        ctx.fillRect(x, y, handleSize, handleSize);
    });

    ctx.strokeStyle = 'rgba(255,255,255,0.18)';
    ctx.lineWidth = 0.5;
    for (let i = 1; i <= 2; i++) {
        ctx.beginPath();
        ctx.moveTo(cropX + _iuCropW * i / 3, cropY);
        ctx.lineTo(cropX + _iuCropW * i / 3, cropY + _iuCropH);
        ctx.stroke();

        ctx.beginPath();
        ctx.moveTo(cropX, cropY + _iuCropH * i / 3);
        ctx.lineTo(cropX + _iuCropW, cropY + _iuCropH * i / 3);
        ctx.stroke();
    }
}

function _iuMouseDown(event) {
    _iuDragging = true;
    _iuDragLast = { x: event.clientX, y: event.clientY };
    _iuCanvas.style.cursor = 'grabbing';
}

function _iuMouseMove(event) {
    if (!_iuDragging) return;

    const rect = _iuCanvas.getBoundingClientRect();
    const scaleX = _iuCanvas.width / rect.width;
    const scaleY = _iuCanvas.height / rect.height;

    _iuOffX += (event.clientX - _iuDragLast.x) * scaleX;
    _iuOffY += (event.clientY - _iuDragLast.y) * scaleY;
    _iuDragLast = { x: event.clientX, y: event.clientY };
    _iuDrawCanvas();
}

function _iuMouseUp() {
    _iuDragging = false;
    if (_iuCanvas) _iuCanvas.style.cursor = 'grab';
}

function _iuWheel(event) {
    event.preventDefault();

    const factor = event.deltaY > 0 ? 0.9 : 1.1;
    const newScale = Math.max(0.05, Math.min(8, _iuScale * factor));
    const centerX = _iuCanvas.width / 2;
    const centerY = _iuCanvas.height / 2;

    _iuOffX = centerX - (centerX - _iuOffX) * (newScale / _iuScale);
    _iuOffY = centerY - (centerY - _iuOffY) * (newScale / _iuScale);
    _iuScale = newScale;

    const slider = document.getElementById('iuZoomSlider');
    if (slider) slider.value = Math.round(_iuScale * 100);
    _iuDrawCanvas();
}

function iuSetZoom(scale) {
    const newScale = Math.max(0.05, Math.min(8, +scale));
    if (_iuCanvas && _iuScale > 0) {
        const centerX = _iuCanvas.width / 2;
        const centerY = _iuCanvas.height / 2;
        _iuOffX = centerX - (centerX - _iuOffX) * (newScale / _iuScale);
        _iuOffY = centerY - (centerY - _iuOffY) * (newScale / _iuScale);
    }
    _iuScale = newScale;
    _iuDrawCanvas();
}

function iuCropAndContinue() {
    if (!_iuImg || !_iuCanvas) return;

    const req = INV_UPLOAD_REQS[_iuTab];
    const targetW = req?.targetW || _iuCropW;
    const targetH = req?.targetH || _iuCropH;

    const cropX = (_iuCanvas.width - _iuCropW) / 2;
    const cropY = (_iuCanvas.height - _iuCropH) / 2;

    const srcX = (cropX - _iuOffX) / _iuScale;
    const srcY = (cropY - _iuOffY) / _iuScale;
    const srcW = _iuCropW / _iuScale;
    const srcH = _iuCropH / _iuScale;

    const out = document.createElement('canvas');
    out.width = targetW;
    out.height = targetH;
    out.getContext('2d').drawImage(_iuImg, srcX, srcY, srcW, srcH, 0, 0, targetW, targetH);

    out.toBlob(blob => {
        if (!blob) {
            iuShowError(t('inventory.upload.error.crop_failed', 'Crop failed.'));
            return;
        }

        const url = URL.createObjectURL(blob);
        const img = new Image();
        img.onload = () => {
            _iuImg = img;
            _iuFile = new File([blob], _iuFile?.name || 'cropped.png', { type: 'image/png' });
            _iuShowPreview(img, _iuFile, true, false, false);
        };
        img.src = url;
    }, 'image/png');
}

function _iuAutoResize(img, req) {
    const maxPx = req.maxPx;
    const scale = maxPx / Math.max(img.naturalWidth, img.naturalHeight);
    const newW = Math.round(img.naturalWidth * scale);
    const newH = Math.round(img.naturalHeight * scale);

    const out = document.createElement('canvas');
    out.width = newW;
    out.height = newH;
    out.getContext('2d').drawImage(img, 0, 0, newW, newH);

    out.toBlob(blob => {
        if (!blob) {
            iuShowError(t('inventory.upload.error.resize_failed', 'Failed to resize image.'));
            return;
        }

        const url = URL.createObjectURL(blob);
        const resized = new Image();
        resized.onload = () => {
            _iuImg = resized;
            _iuFile = new File([blob], _iuFile?.name || 'resized.png', { type: 'image/png' });

            if (req.ratioW != null) {
                const imgRatio = resized.naturalWidth / resized.naturalHeight;
                const targetRatio = req.ratioW / req.ratioH;
                if (Math.abs(imgRatio - targetRatio) < 0.02) _iuShowPreview(resized, _iuFile, false, false, true);
                else _iuShowEditor(resized, req);
            } else {
                _iuShowPreview(resized, _iuFile, false, false, true);
            }
        };
        resized.src = url;
    }, 'image/png');
}

async function iuCompressAndContinue() {
    if (!_iuImg) return;

    const req = INV_UPLOAD_REQS[_iuTab];
    const maxBytes = (req?.maxMB || 8) * 1024 * 1024;

    const maxPx = req?.maxPx;
    let width = _iuImg.naturalWidth;
    let height = _iuImg.naturalHeight;
    if (maxPx && (width > maxPx || height > maxPx)) {
        const scale = maxPx / Math.max(width, height);
        width = Math.round(width * scale);
        height = Math.round(height * scale);
    }

    for (const quality of [0.85, 0.75, 0.60, 0.45, 0.30]) {
        const blob = await new Promise(resolve => {
            const out = document.createElement('canvas');
            out.width = width;
            out.height = height;
            out.getContext('2d').drawImage(_iuImg, 0, 0, width, height);
            out.toBlob(resolve, 'image/jpeg', quality);
        });

        if (blob && blob.size <= maxBytes) {
            _iuApplyCompressed(blob);
            return;
        }
    }

    for (const scale of [0.75, 0.5, 0.4]) {
        const scaledWidth = Math.round(_iuImg.naturalWidth * scale);
        const scaledHeight = Math.round(_iuImg.naturalHeight * scale);
        const blob = await new Promise(resolve => {
            const out = document.createElement('canvas');
            out.width = scaledWidth;
            out.height = scaledHeight;
            out.getContext('2d').drawImage(_iuImg, 0, 0, scaledWidth, scaledHeight);
            out.toBlob(resolve, 'image/jpeg', 0.45);
        });

        if (blob && blob.size <= maxBytes) {
            _iuApplyCompressed(blob);
            return;
        }
    }

    iuShowError(t('inventory.upload.error.compress_failed', 'Could not compress the image enough. Please use a smaller image.'));
}

function _iuApplyCompressed(blob) {
    const url = URL.createObjectURL(blob);
    const img = new Image();
    img.onload = () => {
        _iuImg = img;
        _iuFile = new File([blob], (_iuFile?.name?.replace(/\.[^.]+$/, '') || 'image') + '.jpg', { type: 'image/jpeg' });
        _iuTooBig = false;
        iuClearError();
        _iuShowPreview(img, _iuFile, false, true, false);
    };
    img.src = url;
}

function iuSetAnimStyle(value) {
    _iuAnimStyle = value;
    iuUpdateAnimPreview();
}

function iuAnimGif(value) {
    const style = IU_ANIM_STYLES.find(s => s.value === value);
    return style ? `assets/Emojis/${style.fallback}.gif` : '';
}

function iuUpdateAnimPreview() {
    const img = document.getElementById('iuAnimPreviewImg');
    if (!img) return;
    const src = iuAnimGif(_iuAnimStyle);
    if (src) { img.src = src; img.style.display = ''; }
    else { img.removeAttribute('src'); img.style.display = 'none'; }
}

function iuReset() {
    _iuFile = null;
    _iuImg = null;
    _iuCanvas = null;
    _iuCtx = null;
    _iuTooBig = false;
    _iuWasCropped = false;
    _iuWasCompressed = false;
    _iuWasResized = false;

    const dropZone = document.getElementById('iuDropZone');
    const editorArea = document.getElementById('iuEditorArea');
    const previewArea = document.getElementById('iuPreviewArea');
    const uploadBtn = document.getElementById('iuUploadBtn');
    const emojiOptions = document.getElementById('iuEmojiOptions');

    if (dropZone) dropZone.style.display = '';
    if (editorArea) {
        editorArea.style.display = 'none';
        editorArea.innerHTML = '';
    }
    if (previewArea) {
        previewArea.style.display = 'none';
        previewArea.innerHTML = '';
    }
    if (uploadBtn) uploadBtn.style.display = 'none';
    if (emojiOptions) emojiOptions.style.display = 'none';
    iuClearError();
}

function iuDoUpload() {
    if (!_iuImg) return;

    const uploadBtn = document.getElementById('iuUploadBtn');
    if (uploadBtn) {
        uploadBtn.disabled = true;
        uploadBtn.innerHTML = iuUploadButtonHtml(true);
    }

    const req = INV_UPLOAD_REQS[_iuTab];
    const out = document.createElement('canvas');
    const targetW = req?.targetW || _iuImg.naturalWidth;
    const targetH = req?.targetH || _iuImg.naturalHeight;
    out.width = targetW;
    out.height = targetH;
    out.getContext('2d').drawImage(_iuImg, 0, 0, targetW, targetH);

    out.toBlob(blob => {
        if (req?.directBlob && _iuCallback) {
            const cb = _iuCallback;
            _iuCallback = null;
            closeInvUploadModal();
            cb(blob);
            return;
        }
        const reader = new FileReader();
        reader.onload = event => {
            if (_iuTab === 'prints') {
                const world = (typeof currentInstanceData !== 'undefined' && currentInstanceData) || null;
                sendToCS({
                    action: 'invUploadPrint',
                    data: event.target.result,
                    note: '',
                    worldId: world?.worldId || '',
                    worldName: world?.worldName || world?.name || '',
                });
                return;
            }
            sendToCS({
                action: 'invUploadFromData',
                tag: INV_TABS[_iuTab]?.tag,
                data: event.target.result,
                animationStyle: _iuTab === 'emojis' ? _iuAnimStyle : '',
                maskTag: _iuTab === 'emojis' ? IU_MASK_TAG_DEFAULT : ''
            });
        };
        reader.readAsDataURL(blob);
    }, 'image/png');
}

function iuHandleUploadDone(success, file) {
    if (success) {
        const callback = _iuCallback;
        _iuCallback = null;
        closeInvUploadModal();
        if (callback && file) callback(file);
        return;
    }

    const uploadBtn = document.getElementById('iuUploadBtn');
    if (uploadBtn) {
        uploadBtn.disabled = false;
        uploadBtn.innerHTML = iuUploadButtonHtml(false);
    }
}

function iuShowError(msg, extraHtml = '') {
    const errorEl = document.getElementById('iuError');
    if (errorEl) {
        errorEl.style.display = '';
        errorEl.innerHTML = esc(msg) + (extraHtml ? `<br>${extraHtml}` : '');
    }

    const uploadBtn = document.getElementById('iuUploadBtn');
    if (uploadBtn) {
        uploadBtn.disabled = false;
        uploadBtn.innerHTML = iuUploadButtonHtml(false);
    }
}

function iuClearError() {
    const errorEl = document.getElementById('iuError');
    if (errorEl) errorEl.style.display = 'none';
}

function rerenderInventoryUploadTranslations() {
    const modal = document.getElementById('invUploadModal');
    if (!modal || !_iuTab) return;

    if (_iuCanvas && _iuImg) {
        _iuShowEditor(_iuImg, INV_UPLOAD_REQS[_iuTab]);
        return;
    }

    if (_iuImg && _iuFile) {
        _iuShowPreview(_iuImg, _iuFile, _iuWasCropped, _iuWasCompressed, _iuWasResized);
        return;
    }

    const content = document.getElementById('invUploadContent');
    if (content) content.outerHTML = _iuBuildHTML(_iuTab);
}

document.documentElement.addEventListener('languagechange', rerenderInventoryUploadTranslations);
