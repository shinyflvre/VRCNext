/* === Inventory Upload Modal === */

// Upload requirements per tab
const INV_UPLOAD_REQS = {
    photos:   { maxMB: 8,  ratioW: null, ratioH: null, minPx: 64, maxPx: 2048, hint: 'PNG • max 8 MB • 64×64 to 2048×2048 • any aspect ratio' },
    icons:    { maxMB: 1,  ratioW: 1, ratioH: 1, targetW: 1024, targetH: 1024, hint: 'PNG • max 1 MB • 1024×1024 • aspect ratio 1:1 • requires VRC+' },
    emojis:   { maxMB: 1,  ratioW: 1, ratioH: 1, targetW: 1024, targetH: 1024, hint: 'PNG • max 1 MB • 1024×1024 • aspect ratio 1:1 • max 18 • requires VRC+', hasAnimStyle: true },
    stickers: { maxMB: 1,  ratioW: 1, ratioH: 1, targetW: 1024, targetH: 1024, hint: 'PNG • max 1 MB • max 1024×1024 • aspect ratio 1:1 • max 18 • requires VRC+' },
};

// All 27 VRChat emoji particle/animation styles
const IU_ANIM_STYLES = [
    { value: 'aura',      label: 'Aura' },
    { value: 'bats',      label: 'Bats' },
    { value: 'bees',      label: 'Bees' },
    { value: 'bounce',    label: 'Bounce' },
    { value: 'cloud',     label: 'Cloud' },
    { value: 'confetti',  label: 'Confetti' },
    { value: 'crying',    label: 'Crying' },
    { value: 'dislike',   label: 'Dislike' },
    { value: 'fire',      label: 'Fire' },
    { value: 'idea',      label: 'Idea' },
    { value: 'lasers',    label: 'Lasers' },
    { value: 'like',      label: 'Like' },
    { value: 'magnet',    label: 'Magnet' },
    { value: 'mistletoe', label: 'Mistletoe' },
    { value: 'money',     label: 'Money' },
    { value: 'noise',     label: 'Noise' },
    { value: 'orbit',     label: 'Orbit' },
    { value: 'pizza',     label: 'Pizza' },
    { value: 'rain',      label: 'Rain' },
    { value: 'rotate',    label: 'Rotate' },
    { value: 'shake',     label: 'Shake' },
    { value: 'snow',      label: 'Snow' },
    { value: 'snowball',  label: 'Snowball' },
    { value: 'spin',      label: 'Spin' },
    { value: 'splash',    label: 'Splash' },
    { value: 'stop',      label: 'Stop' },
    { value: 'zzz',       label: 'ZZZ' },
];

// maskTag is always "square" — required by VRChat API but not user-selectable
const IU_MASK_TAG_DEFAULT = 'square';

// State
let _iuTab        = '';
let _iuFile       = null;
let _iuImg        = null;
let _iuAnimStyle  = 'aura';

// Canvas editor state
let _iuCanvas     = null;
let _iuCtx        = null;
let _iuScale      = 1;
let _iuOffX       = 0;
let _iuOffY       = 0;
let _iuDragging   = false;
let _iuDragLast   = { x: 0, y: 0 };
let _iuCropW      = 0;
let _iuCropH      = 0;

// ── Open / Close ──────────────────────────────────────────────────────────────

function openInvUploadModal() {
    const tab = activeInvTab;
    if (!INV_TABS[tab]?.canUpload) return;

    _iuTab       = tab;
    _iuFile      = null;
    _iuImg       = null;
    _iuAnimStyle = 'aura';
    _iuCanvas    = null;
    _iuCtx       = null;

    const existing = document.getElementById('invUploadModal');
    if (existing) existing.remove();

    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    overlay.id = 'invUploadModal';
    overlay.onclick = e => { if (e.target === overlay) closeInvUploadModal(); };
    overlay.innerHTML = _iuBuildHTML(tab);
    document.body.appendChild(overlay);

    const handler = e => {
        if (e.key === 'Escape') { closeInvUploadModal(); document.removeEventListener('keydown', handler); }
    };
    document.addEventListener('keydown', handler);
    overlay._kh = handler;
}

function closeInvUploadModal() {
    const m = document.getElementById('invUploadModal');
    if (m) {
        if (m._kh) document.removeEventListener('keydown', m._kh);
        m.remove();
    }
    _iuCanvas = null;
    _iuCtx    = null;
}

// ── Build initial HTML ────────────────────────────────────────────────────────

function _iuBuildHTML(tab) {
    const req      = INV_UPLOAD_REQS[tab];
    const tabLabel = INV_TABS[tab]?.label || tab;
    const emojiHtml = req?.hasAnimStyle ? `
        <div id="iuEmojiOptions" style="display:none;margin-top:14px;">
            <div style="font-size:12px;font-weight:600;color:var(--tx2);margin-bottom:8px;">Particle style</div>
            <div style="display:flex;gap:6px;flex-wrap:wrap;" id="iuAnimBtns">
                ${IU_ANIM_STYLES.map(s => `<button class="btn-fav avatar-filter-btn${s.value === 'aura' ? ' active' : ''}" onclick="iuSetAnimStyle('${s.value}',this)">${esc(s.label)}</button>`).join('')}
            </div>
        </div>` : '';

    return `<div class="modal-box wide" id="invUploadContent" style="max-width:560px;">
        <div style="margin-bottom:14px;">
            <div style="font-size:16px;font-weight:700;color:var(--tx0);">Upload to ${esc(tabLabel)}</div>
        </div>
        <div style="background:var(--bg-input);border-radius:8px;padding:10px 14px;margin-bottom:14px;font-size:12px;color:var(--tx2);">${esc(req?.hint || '')}</div>
        <div id="iuDropZone" class="iu-dropzone"
            onclick="iuBrowse()"
            ondragover="event.preventDefault();this.classList.add('dragover')"
            ondragleave="this.classList.remove('dragover')"
            ondrop="iuDrop(event)">
            <span class="msi" style="font-size:40px;color:var(--tx3);display:block;margin-bottom:10px;pointer-events:none;">upload_file</span>
            <div style="font-size:14px;font-weight:600;color:var(--tx1);pointer-events:none;">
                Drop PNG here or <span style="color:var(--accent);">browse</span>
            </div>
            <div style="font-size:11px;color:var(--tx3);margin-top:6px;pointer-events:none;">PNG files only</div>
            <input type="file" id="iuFileInput" accept="image/png" style="display:none;" onchange="iuHandleFileInput(this)">
        </div>
        <div id="iuEditorArea" style="display:none;"></div>
        <div id="iuPreviewArea" style="display:none;"></div>
        ${emojiHtml}
        <div id="iuError" style="display:none;margin-top:10px;padding:10px 14px;background:rgba(220,50,50,.12);border-radius:8px;font-size:12px;color:#e05252;"></div>
        <div style="display:flex;gap:8px;margin-top:16px;justify-content:flex-end;">
            <button class="fd-btn" onclick="closeInvUploadModal()">Cancel</button>
            <button class="fd-btn fd-btn-join" id="iuUploadBtn" style="display:none;" onclick="iuDoUpload()">
                <span class="msi" style="font-size:14px;">upload</span> Upload
            </button>
        </div>
    </div>`;
}

// ── File input / drop ─────────────────────────────────────────────────────────

function iuBrowse() {
    document.getElementById('iuFileInput')?.click();
}

function iuDrop(e) {
    e.preventDefault();
    e.currentTarget.classList.remove('dragover');
    const file = e.dataTransfer.files?.[0];
    if (file) iuHandleFile(file);
}

function iuHandleFileInput(input) {
    const file = input.files?.[0];
    if (file) iuHandleFile(file);
}

function iuHandleFile(file) {
    _iuFile = null;
    _iuImg  = null;
    iuClearError();

    if (!file.type.includes('png') && !file.name.toLowerCase().endsWith('.png')) {
        iuShowError('Only PNG files are supported.');
        return;
    }

    const req = INV_UPLOAD_REQS[_iuTab];
    if (req && file.size > req.maxMB * 1024 * 1024) {
        iuShowError(`File too large (${(file.size / 1024 / 1024).toFixed(1)} MB). Maximum is ${req.maxMB} MB.`);
        return;
    }

    const reader = new FileReader();
    reader.onload = e => {
        const img = new Image();
        img.onload = () => {
            _iuFile = file;
            _iuImg  = img;
            _iuValidateAndShow(img, file, req);
        };
        img.onerror = () => iuShowError('Failed to load image.');
        img.src = e.target.result;
    };
    reader.readAsDataURL(file);
}

function _iuValidateAndShow(img, file, req) {
    if (!req) { _iuShowPreview(img, file, false); return; }

    // Dimension bounds check (photos: 64–2048)
    if (req.minPx != null && (img.naturalWidth < req.minPx || img.naturalHeight < req.minPx)) {
        iuShowError(`Image too small. Minimum size is ${req.minPx}×${req.minPx} px.`);
        return;
    }
    if (req.maxPx != null && (img.naturalWidth > req.maxPx || img.naturalHeight > req.maxPx)) {
        iuShowError(`Image too large. Maximum size is ${req.maxPx}×${req.maxPx} px.`);
        return;
    }

    // No ratio requirement → accept as-is
    if (req.ratioW == null) { _iuShowPreview(img, file, false); return; }

    const imgRatio    = img.naturalWidth / img.naturalHeight;
    const targetRatio = req.ratioW / req.ratioH;
    const ratioOk     = Math.abs(imgRatio - targetRatio) < 0.02;

    if (!ratioOk) {
        _iuShowEditor(img, req);
    } else {
        _iuShowPreview(img, file, false);
    }
}

// ── Preview ───────────────────────────────────────────────────────────────────

function _iuShowPreview(img, file, wasCropped) {
    const dz        = document.getElementById('iuDropZone');
    const ea        = document.getElementById('iuEditorArea');
    const pa        = document.getElementById('iuPreviewArea');
    const ub        = document.getElementById('iuUploadBtn');
    const emojiOpts = document.getElementById('iuEmojiOptions');

    if (dz) dz.style.display = 'none';
    if (ea) ea.style.display = 'none';

    const sizeStr = file.size < 1024 * 1024
        ? (file.size / 1024).toFixed(0) + ' KB'
        : (file.size / 1024 / 1024).toFixed(2) + ' MB';
    const dimStr = img.naturalWidth + '×' + img.naturalHeight;

    if (pa) {
        pa.style.display  = 'flex';
        pa.style.gap      = '16px';
        pa.style.alignItems = 'flex-start';
        pa.innerHTML = `
            <div style="flex-shrink:0;width:120px;height:120px;border-radius:8px;overflow:hidden;background:var(--bg-input);display:flex;align-items:center;justify-content:center;">
                <img src="${esc(img.src)}" style="max-width:100%;max-height:100%;object-fit:contain;">
            </div>
            <div style="flex:1;min-width:0;">
                <div style="font-weight:600;color:var(--tx1);margin-bottom:4px;word-break:break-all;">${esc(file.name)}</div>
                <div style="font-size:12px;color:var(--tx3);margin-bottom:4px;">${dimStr} • ${sizeStr}</div>
                ${wasCropped ? '<div style="font-size:12px;color:var(--accent);margin-bottom:4px;"><span class="msi" style="font-size:13px;vertical-align:-3px;">crop</span> Cropped to fit</div>' : ''}
                <div style="font-size:12px;color:#4caf50;"><span class="msi" style="font-size:13px;vertical-align:-3px;">check_circle</span> Ready to upload</div>
                <button class="fd-btn" style="margin-top:10px;font-size:11px;padding:4px 10px;" onclick="iuReset()">Choose different</button>
            </div>`;
    }

    if (emojiOpts) emojiOpts.style.display = _iuTab === 'emojis' ? '' : 'none';
    if (ub) ub.style.display = '';
}

// ── Canvas editor / cropper ───────────────────────────────────────────────────

function _iuShowEditor(img, req) {
    const dz = document.getElementById('iuDropZone');
    const ea = document.getElementById('iuEditorArea');
    const pa = document.getElementById('iuPreviewArea');
    const ub = document.getElementById('iuUploadBtn');

    if (dz) dz.style.display = 'none';
    if (pa) pa.style.display = 'none';
    if (ub) ub.style.display = 'none';

    const ratio = req.ratioW / req.ratioH;

    if (!ea) return;
    ea.style.display = '';
    ea.innerHTML = `
        <div style="font-size:12px;color:var(--tx2);margin-bottom:10px;">
            <span class="msi" style="font-size:13px;vertical-align:-3px;">crop</span>
            Crop to <strong>${req.ratioW}:${req.ratioH}</strong> — drag to reposition, scroll to zoom
        </div>
        <canvas id="iuCropCanvas" style="border-radius:8px;cursor:grab;user-select:none;display:block;width:100%;touch-action:none;"></canvas>
        <div style="display:flex;align-items:center;gap:10px;margin-top:10px;">
            <span class="msi" style="color:var(--tx3);font-size:16px;">zoom_out</span>
            <input type="range" id="iuZoomSlider" min="10" max="400" value="100" style="flex:1;" oninput="iuSetZoom(this.value/100)">
            <span class="msi" style="color:var(--tx3);font-size:16px;">zoom_in</span>
        </div>
        <div style="display:flex;gap:8px;margin-top:10px;justify-content:flex-end;">
            <button class="fd-btn" style="font-size:12px;" onclick="iuReset()">Choose different</button>
            <button class="fd-btn fd-btn-join" onclick="iuCropAndContinue()">
                <span class="msi" style="font-size:14px;">crop</span> Crop & continue
            </button>
        </div>`;

    const canvas  = document.getElementById('iuCropCanvas');
    _iuCanvas = canvas;
    _iuCtx    = canvas.getContext('2d');

    const CANVAS_W = 480;
    const CANVAS_H = Math.min(Math.round(CANVAS_W / ratio), 360);
    canvas.width  = CANVAS_W;
    canvas.height = CANVAS_H;

    const pad   = 20;
    const availW = CANVAS_W - pad * 2;
    const availH = CANVAS_H - pad * 2;
    if (availW / ratio <= availH) {
        _iuCropW = availW;
        _iuCropH = Math.round(availW / ratio);
    } else {
        _iuCropH = availH;
        _iuCropW = Math.round(availH * ratio);
    }

    // Initial scale: fill the crop box (cover)
    const scaleX = _iuCropW / img.naturalWidth;
    const scaleY = _iuCropH / img.naturalHeight;
    _iuScale = Math.max(scaleX, scaleY);

    // Center image
    _iuOffX = (CANVAS_W - img.naturalWidth  * _iuScale) / 2;
    _iuOffY = (CANVAS_H - img.naturalHeight * _iuScale) / 2;

    const slider = document.getElementById('iuZoomSlider');
    if (slider) slider.value = Math.round(_iuScale * 100);

    _iuDrawCanvas();

    canvas.addEventListener('mousedown',  _iuMouseDown);
    canvas.addEventListener('mousemove',  _iuMouseMove);
    canvas.addEventListener('mouseup',    _iuMouseUp);
    canvas.addEventListener('mouseleave', _iuMouseUp);
    canvas.addEventListener('wheel',      _iuWheel, { passive: false });
}

function _iuDrawCanvas() {
    if (!_iuCtx || !_iuImg) return;
    const c   = _iuCanvas;
    const ctx = _iuCtx;
    const cw  = c.width;
    const ch  = c.height;
    const cx  = (cw - _iuCropW) / 2;
    const cy  = (ch - _iuCropH) / 2;
    const iw  = _iuImg.naturalWidth  * _iuScale;
    const ih  = _iuImg.naturalHeight * _iuScale;

    ctx.clearRect(0, 0, cw, ch);

    // Background checkerboard (shows transparency)
    ctx.save();
    for (let y = 0; y < ch; y += 10) {
        for (let x = 0; x < cw; x += 10) {
            ctx.fillStyle = ((x + y) / 10 % 2 < 1) ? '#2a2a2a' : '#333';
            ctx.fillRect(x, y, 10, 10);
        }
    }
    ctx.restore();

    // Draw image
    ctx.drawImage(_iuImg, _iuOffX, _iuOffY, iw, ih);

    // Dimming overlay outside crop box
    ctx.save();
    ctx.fillStyle = 'rgba(0,0,0,0.55)';
    ctx.fillRect(0, 0, cw, ch);
    ctx.globalCompositeOperation = 'destination-out';
    ctx.fillStyle = 'rgba(0,0,0,1)';
    ctx.fillRect(cx, cy, _iuCropW, _iuCropH);
    ctx.restore();

    // Redraw image inside crop box only (crisp on top of dim)
    ctx.save();
    ctx.beginPath();
    ctx.rect(cx, cy, _iuCropW, _iuCropH);
    ctx.clip();
    ctx.drawImage(_iuImg, _iuOffX, _iuOffY, iw, ih);
    ctx.restore();

    // Crop border + corner handles
    ctx.strokeStyle = 'rgba(255,255,255,0.85)';
    ctx.lineWidth   = 1.5;
    ctx.strokeRect(cx, cy, _iuCropW, _iuCropH);

    const hs = 10;
    ctx.fillStyle = '#fff';
    [[cx, cy],[cx + _iuCropW - hs, cy],[cx, cy + _iuCropH - hs],[cx + _iuCropW - hs, cy + _iuCropH - hs]].forEach(([x, y]) => {
        ctx.fillRect(x, y, hs, hs);
    });

    // Rule-of-thirds grid lines
    ctx.strokeStyle = 'rgba(255,255,255,0.18)';
    ctx.lineWidth   = 0.5;
    for (let i = 1; i <= 2; i++) {
        ctx.beginPath();
        ctx.moveTo(cx + _iuCropW * i / 3, cy);
        ctx.lineTo(cx + _iuCropW * i / 3, cy + _iuCropH);
        ctx.stroke();
        ctx.beginPath();
        ctx.moveTo(cx, cy + _iuCropH * i / 3);
        ctx.lineTo(cx + _iuCropW, cy + _iuCropH * i / 3);
        ctx.stroke();
    }
}

function _iuMouseDown(e) {
    _iuDragging = true;
    _iuDragLast = { x: e.clientX, y: e.clientY };
    _iuCanvas.style.cursor = 'grabbing';
}

function _iuMouseMove(e) {
    if (!_iuDragging) return;
    const rect = _iuCanvas.getBoundingClientRect();
    const sx   = _iuCanvas.width  / rect.width;
    const sy   = _iuCanvas.height / rect.height;
    _iuOffX   += (e.clientX - _iuDragLast.x) * sx;
    _iuOffY   += (e.clientY - _iuDragLast.y) * sy;
    _iuDragLast = { x: e.clientX, y: e.clientY };
    _iuDrawCanvas();
}

function _iuMouseUp() {
    _iuDragging = false;
    if (_iuCanvas) _iuCanvas.style.cursor = 'grab';
}

function _iuWheel(e) {
    e.preventDefault();
    const factor   = e.deltaY > 0 ? 0.9 : 1.1;
    const newScale = Math.max(0.05, Math.min(8, _iuScale * factor));
    const cx       = _iuCanvas.width  / 2;
    const cy       = _iuCanvas.height / 2;
    _iuOffX  = cx - (cx - _iuOffX) * (newScale / _iuScale);
    _iuOffY  = cy - (cy - _iuOffY) * (newScale / _iuScale);
    _iuScale = newScale;
    const slider = document.getElementById('iuZoomSlider');
    if (slider) slider.value = Math.round(_iuScale * 100);
    _iuDrawCanvas();
}

function iuSetZoom(scale) {
    const newScale = Math.max(0.05, Math.min(8, +scale));
    if (_iuCanvas && _iuScale > 0) {
        const cx = _iuCanvas.width  / 2;
        const cy = _iuCanvas.height / 2;
        _iuOffX  = cx - (cx - _iuOffX) * (newScale / _iuScale);
        _iuOffY  = cy - (cy - _iuOffY) * (newScale / _iuScale);
    }
    _iuScale = newScale;
    _iuDrawCanvas();
}

function iuCropAndContinue() {
    if (!_iuImg || !_iuCanvas) return;
    const req     = INV_UPLOAD_REQS[_iuTab];
    const targetW = req?.targetW || _iuCropW;
    const targetH = req?.targetH || _iuCropH;

    const cropX = (_iuCanvas.width  - _iuCropW) / 2;
    const cropY = (_iuCanvas.height - _iuCropH) / 2;

    // Compute source region in the ORIGINAL image (no overlay drawn on it)
    const srcX = (cropX - _iuOffX) / _iuScale;
    const srcY = (cropY - _iuOffY) / _iuScale;
    const srcW = _iuCropW / _iuScale;
    const srcH = _iuCropH / _iuScale;

    const out = document.createElement('canvas');
    out.width  = targetW;
    out.height = targetH;
    out.getContext('2d').drawImage(_iuImg, srcX, srcY, srcW, srcH, 0, 0, targetW, targetH);

    out.toBlob(blob => {
        if (!blob) { iuShowError('Crop failed.'); return; }
        const url = URL.createObjectURL(blob);
        const img = new Image();
        img.onload = () => {
            _iuImg  = img;
            _iuFile = new File([blob], _iuFile?.name || 'cropped.png', { type: 'image/png' });
            _iuShowPreview(img, _iuFile, true);
        };
        img.src = url;
    }, 'image/png');
}

// ── Emoji options ─────────────────────────────────────────────────────────────

function iuSetAnimStyle(value, btn) {
    _iuAnimStyle = value;
    document.querySelectorAll('#iuAnimBtns button').forEach(b => b.classList.remove('active'));
    btn.classList.add('active');
}


// ── Reset ─────────────────────────────────────────────────────────────────────

function iuReset() {
    _iuFile   = null;
    _iuImg    = null;
    _iuCanvas = null;
    _iuCtx    = null;

    const dz        = document.getElementById('iuDropZone');
    const ea        = document.getElementById('iuEditorArea');
    const pa        = document.getElementById('iuPreviewArea');
    const ub        = document.getElementById('iuUploadBtn');
    const emojiOpts = document.getElementById('iuEmojiOptions');

    if (dz) dz.style.display = '';
    if (ea) { ea.style.display = 'none'; ea.innerHTML = ''; }
    if (pa) { pa.style.display = 'none'; pa.innerHTML = ''; }
    if (ub) ub.style.display = 'none';
    if (emojiOpts) emojiOpts.style.display = 'none';
    iuClearError();
}

// ── Upload ────────────────────────────────────────────────────────────────────

function iuDoUpload() {
    if (!_iuImg) return;

    const ub = document.getElementById('iuUploadBtn');
    if (ub) { ub.disabled = true; ub.innerHTML = '<span class="msi" style="font-size:14px;">hourglass_empty</span> Uploading…'; }

    const req = INV_UPLOAD_REQS[_iuTab];

    // Draw to output canvas at target size
    const out    = document.createElement('canvas');
    const targetW = req?.targetW || _iuImg.naturalWidth;
    const targetH = req?.targetH || _iuImg.naturalHeight;
    out.width  = targetW;
    out.height = targetH;
    out.getContext('2d').drawImage(_iuImg, 0, 0, targetW, targetH);

    out.toBlob(blob => {
        const reader = new FileReader();
        reader.onload = e => {
            sendToCS({
                action:         'invUploadFromData',
                tag:            INV_TABS[_iuTab]?.tag,
                data:           e.target.result,
                animationStyle: _iuTab === 'emojis' ? _iuAnimStyle       : '',
                maskTag:        _iuTab === 'emojis' ? IU_MASK_TAG_DEFAULT : '',
            });
        };
        reader.readAsDataURL(blob);
    }, 'image/png');
}

// Called from messages.js when upload completes
function iuHandleUploadDone(success) {
    if (success) {
        closeInvUploadModal();
    } else {
        const ub = document.getElementById('iuUploadBtn');
        if (ub) { ub.disabled = false; ub.innerHTML = '<span class="msi" style="font-size:14px;">upload</span> Upload'; }
    }
}

// ── Error helpers ─────────────────────────────────────────────────────────────

function iuShowError(msg) {
    const el = document.getElementById('iuError');
    if (el) { el.style.display = ''; el.textContent = msg; }
    const ub = document.getElementById('iuUploadBtn');
    if (ub) { ub.disabled = false; ub.innerHTML = '<span class="msi" style="font-size:14px;">upload</span> Upload'; }
}

function iuClearError() {
    const el = document.getElementById('iuError');
    if (el) el.style.display = 'none';
}
