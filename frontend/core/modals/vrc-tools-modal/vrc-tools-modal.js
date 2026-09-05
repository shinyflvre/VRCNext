var _vrcCfgRaw = {};

var VRC_CFG_RES_SCREENSHOT = [
    { name: '1280x720 (720p)', width: 1280, height: 720 },
    { name: '1920x1080 (1080p Default)', width: '', height: '' },
    { name: '2560x1440 (1440p)', width: 2560, height: 1440 },
    { name: '3840x2160 (4K)', width: 3840, height: 2160 },
];

var VRC_CFG_RES_CAMERA = VRC_CFG_RES_SCREENSHOT.concat([
    { name: '7680x4320 (8K)', width: 7680, height: 4320 },
]);

function _vrcCfgResKey(width, height) {
    var w = Number(width), h = Number(height);
    return (w > 0 && h > 0) ? (w + 'x' + h) : '__default__';
}

function _vrcCfgFillResSelect(id, rows) {
    var sel = document.getElementById(id);
    if (!sel) return;
    sel.innerHTML = rows.map(function (r) {
        return '<option value="' + _vrcCfgResKey(r.width, r.height) + '">' + esc(r.name) + '</option>';
    }).join('');
}

function _vrcCfgPrintsStatus(p) {
    var ico = document.getElementById('vrcCfgPrintsStatusIco');
    var txt = document.getElementById('vrcCfgPrintsStatus');
    var btn = document.getElementById('vrcCfgPrintsFlagBtn');
    if (!txt) return;
    var state = p.logOk ? 'ok' : (p.flagSet ? 'pending' : 'off');
    if (state === 'ok') {
        txt.textContent = t('vrc_config.prints_status_ok', 'VRChat is logging the required data. Prints will be saved.');
        if (ico) { ico.textContent = 'check_circle'; ico.style.color = 'var(--ok)'; }
    } else if (state === 'pending') {
        txt.textContent = t('vrc_config.prints_status_pending', 'Launch option is set. Restart VRChat through VRCNext so it takes effect.');
        if (ico) { ico.textContent = 'restart_alt'; ico.style.color = 'var(--tx3)'; }
    } else {
        txt.textContent = t('vrc_config.prints_requires_flag', 'VRChat only logs the required data with the launch option --enable-sdk-log-levels.');
        if (ico) { ico.textContent = 'error'; ico.style.color = 'var(--err)'; }
    }
    if (btn) btn.style.display = p.flagSet ? 'none' : '';
}

function vrcCfgAddSdkLogFlag() {
    sendToCS({ action: 'vrcAddSdkLogFlag' });
    setTimeout(function () { sendToCS({ action: 'vrcConfigGet' }); }, 300);
}

function vrcCfgSavePrintsToggle() {
    var on = document.getElementById('vrcCfgSavePrints')?.checked;
    var field = document.getElementById('vrcCfgPrintsDir')?.closest('.vrc-cfg-field');
    if (field) field.style.opacity = on ? '1' : '.5';
    var inp = document.getElementById('vrcCfgPrintsDir');
    if (inp) inp.disabled = !on;
}

function vrcCfgSaveStickersToggle() {
    var on = document.getElementById('vrcCfgSaveStickers')?.checked;
    var field = document.getElementById('vrcCfgStickersDir')?.closest('.vrc-cfg-field');
    if (field) field.style.opacity = on ? '1' : '.5';
    var inp = document.getElementById('vrcCfgStickersDir');
    if (inp) inp.disabled = !on;
}

function vrcCfgPickFolder(targetId) {
    sendToCS({ action: 'pickFolder', target: targetId });
}

function openVrcConfigModal() {
    document.getElementById('modalVrcConfig').style.display = 'flex';
    document.getElementById('vrcCfgCacheSize').textContent = '...';
    document.getElementById('vrcCfgMaxCacheSize').value = '';
    document.getElementById('vrcCfgCacheExpiry').value = '';
    document.getElementById('vrcCfgSteadycamFov').value = '';
    document.getElementById('vrcCfgCacheDir').value = '';
    document.getElementById('vrcCfgPictureDir').value = '';
    document.getElementById('vrcCfgPrintsDir').value = '';
    document.getElementById('vrcCfgStickersDir').value = '';
    _vrcCfgFillResSelect('vrcCfgCameraRes', VRC_CFG_RES_CAMERA);
    _vrcCfgFillResSelect('vrcCfgSpoutRes', VRC_CFG_RES_SCREENSHOT);
    _vrcCfgFillResSelect('vrcCfgScreenshotRes', VRC_CFG_RES_SCREENSHOT);
    sendToCS({ action: 'vrcConfigGet' });
}

function openVrcLaunchOptionsModal() {
    document.getElementById('modalVrcLaunchOptions').style.display = 'flex';
    document.getElementById('vrcLaArgs').value = '';
    document.getElementById('vrcLaPath').value = '';
    sendToCS({ action: 'vrcLaunchOptionsGet' });
}

function _vrcCfgFormatBytes(b) {
    var n = Number(b) || 0;
    if (n <= 0) return '0 GB';
    return (n / 1024 / 1024 / 1024).toFixed(2) + ' GB';
}

function _vrcCfgApplyData(payload) {
    if (payload.config && typeof payload.config === 'object') {
        _vrcCfgRaw = payload.config || {};
        document.getElementById('vrcCfgMaxCacheSize').value = _vrcCfgRaw.cache_size != null ? _vrcCfgRaw.cache_size : '';
        document.getElementById('vrcCfgCacheExpiry').value = _vrcCfgRaw.cache_expiry_delay != null ? _vrcCfgRaw.cache_expiry_delay : '';
        document.getElementById('vrcCfgSteadycamFov').value = _vrcCfgRaw.fpv_steadycam_fov != null ? _vrcCfgRaw.fpv_steadycam_fov : '';
        document.getElementById('vrcCfgCacheDir').value = _vrcCfgRaw.cache_directory || '';
        document.getElementById('vrcCfgPictureDir').value = _vrcCfgRaw.picture_output_folder || '';
        _vrcCfgSetRes('vrcCfgCameraRes', _vrcCfgRaw.camera_res_width, _vrcCfgRaw.camera_res_height);
        _vrcCfgSetRes('vrcCfgSpoutRes', _vrcCfgRaw.camera_spout_res_width, _vrcCfgRaw.camera_spout_res_height);
        _vrcCfgSetRes('vrcCfgScreenshotRes', _vrcCfgRaw.screenshot_res_width, _vrcCfgRaw.screenshot_res_height);
        document.getElementById('vrcCfgSplitByDate').checked = _vrcCfgRaw.picture_output_split_by_date !== false;
        document.getElementById('vrcCfgDisableRichPresence').checked = _vrcCfgRaw.disableRichPresence === true;
    }
    if (payload.inGame) {
        var hint = document.getElementById('vrcCfgCameraResHint');
        var res = (payload.inGame.cameraRes || '').trim();
        if (hint) {
            hint.textContent = res ? tf('vrc_config.in_game_value', { value: res + 'p' }, 'Currently set in VRChat: ' + res + 'p') : '';
            hint.style.display = res ? '' : 'none';
        }
    }
    if (payload.prints) {
        document.getElementById('vrcCfgSavePrints').checked = !!payload.prints.enabled;
        document.getElementById('vrcCfgPrintsDir').value = payload.prints.path || '';
        document.getElementById('vrcCfgPrintsDir').placeholder = payload.prints.defaultPath || '';
        _vrcCfgPrintsStatus(payload.prints);
        vrcCfgSavePrintsToggle();
    }
    if (payload.stickers) {
        document.getElementById('vrcCfgSaveStickers').checked = !!payload.stickers.enabled;
        document.getElementById('vrcCfgStickersDir').value = payload.stickers.path || '';
        document.getElementById('vrcCfgStickersDir').placeholder = payload.stickers.defaultPath || '';
        vrcCfgSaveStickersToggle();
    }
    if (payload.cacheBytes != null) {
        document.getElementById('vrcCfgCacheSize').textContent = _vrcCfgFormatBytes(payload.cacheBytes);
    }
}

function vrcCfgRefreshCache() {
    document.getElementById('vrcCfgCacheSize').textContent = '...';
    sendToCS({ action: 'vrcCacheRefresh' });
}

function vrcCfgDeleteCache() {
    vnConfirmModal({
        title: t('vrc_config.confirm_delete_title', 'Delete Asset Cache'),
        icon: 'delete_sweep',
        message: esc(t('vrc_config.confirm_delete', 'Delete the entire VRChat asset cache?')),
        confirmLabel: t('common.delete', 'Delete'),
        onConfirm: () => {
            document.getElementById('vrcCfgCacheSize').textContent = '...';
            sendToCS({ action: 'vrcCacheDeleteAll' });
        },
    });
}

function vrcCfgSweepCache() {
    document.getElementById('vrcCfgCacheSize').textContent = '...';
    sendToCS({ action: 'vrcCacheSweep' });
}

function _vrcCfgParseNum(v) {
    if (v == null || v === '') return null;
    var n = parseInt(v, 10);
    return isNaN(n) ? null : n;
}

function _vrcCfgSetRes(id, width, height) {
    var sel = document.getElementById(id);
    if (!sel) return;
    var key = _vrcCfgResKey(width, height);
    sel.value = [].some.call(sel.options, function (o) { return o.value === key; }) ? key : '__default__';
    if (sel._vnRefresh) sel._vnRefresh();
}

function _vrcCfgApplyRes(merged, id, prefix) {
    var val = document.getElementById(id)?.value || '__default__';
    if (val === '__default__') {
        delete merged[prefix + '_width'];
        delete merged[prefix + '_height'];
        return;
    }
    var parts = val.split('x');
    merged[prefix + '_width'] = parseInt(parts[0], 10) || 0;
    merged[prefix + '_height'] = parseInt(parts[1], 10) || 0;
}

function vrcCfgSave() {
    var merged = Object.assign({}, _vrcCfgRaw);
    var max = _vrcCfgParseNum(document.getElementById('vrcCfgMaxCacheSize').value);
    var exp = _vrcCfgParseNum(document.getElementById('vrcCfgCacheExpiry').value);
    var fov = _vrcCfgParseNum(document.getElementById('vrcCfgSteadycamFov').value);
    if (max != null) merged.cache_size = max; else delete merged.cache_size;
    if (exp != null) merged.cache_expiry_delay = exp; else delete merged.cache_expiry_delay;
    if (fov != null) merged.fpv_steadycam_fov = fov; else delete merged.fpv_steadycam_fov;

    var cacheDir = (document.getElementById('vrcCfgCacheDir').value || '').trim();
    var picDir = (document.getElementById('vrcCfgPictureDir').value || '').trim();
    if (cacheDir) merged.cache_directory = cacheDir; else delete merged.cache_directory;
    if (picDir) merged.picture_output_folder = picDir; else delete merged.picture_output_folder;

    _vrcCfgApplyRes(merged, 'vrcCfgCameraRes', 'camera_res');
    _vrcCfgApplyRes(merged, 'vrcCfgSpoutRes', 'camera_spout_res');
    _vrcCfgApplyRes(merged, 'vrcCfgScreenshotRes', 'screenshot_res');

    if (document.getElementById('vrcCfgSplitByDate').checked) delete merged.picture_output_split_by_date;
    else merged.picture_output_split_by_date = false;
    if (document.getElementById('vrcCfgDisableRichPresence').checked) merged.disableRichPresence = true;
    else delete merged.disableRichPresence;

    sendToCS({
        action: 'vrcConfigSave',
        config: merged,
        prints: {
            enabled: document.getElementById('vrcCfgSavePrints').checked,
            path: (document.getElementById('vrcCfgPrintsDir').value || '').trim(),
        },
        stickers: {
            enabled: document.getElementById('vrcCfgSaveStickers').checked,
            path: (document.getElementById('vrcCfgStickersDir').value || '').trim(),
        },
    });
    document.getElementById('modalVrcConfig').style.display = 'none';
}

function _vrcLaApplyData(payload) {
    document.getElementById('vrcLaArgs').value = payload.args || '';
    document.getElementById('vrcLaPath').value = payload.path || '';
}

function vrcLaSave() {
    var args = document.getElementById('vrcLaArgs').value || '';
    var path = document.getElementById('vrcLaPath').value || '';
    sendToCS({ action: 'vrcLaunchOptionsSave', args: args, path: path });
    document.getElementById('modalVrcLaunchOptions').style.display = 'none';
}

/* === Message Templates === */

const MT_POOLS = ['message', 'response', 'request', 'requestResponse'];
const MT_TAB_IDS = {
    message: 'mtTabMessage',
    response: 'mtTabResponse',
    request: 'mtTabRequest',
    requestResponse: 'mtTabRequestResponse',
};

let _mtPool = 'message';
let _mtCache = {};
let _mtEditSlot = -1;
let _mtSavingSlot = -1;

function openMessageTemplatesModal() {
    const overlay = document.getElementById('modalMessageTemplates');
    if (!overlay) return;
    overlay.style.display = 'flex';
    _mtEditSlot = -1;
    mtSetPool(_mtPool);
}

function closeMessageTemplatesModal() {
    const overlay = document.getElementById('modalMessageTemplates');
    if (overlay) overlay.style.display = 'none';
    _mtEditSlot = -1;
}

function mtSetPool(pool) {
    if (!MT_POOLS.includes(pool)) pool = 'message';
    _mtPool = pool;
    _mtEditSlot = -1;
    MT_POOLS.forEach(p => {
        document.getElementById(MT_TAB_IDS[p])?.classList.toggle('active', p === pool);
    });
    if (_mtCache[pool]) mtRender();
    else mtLoad(pool);
}

function mtLoad(pool, force) {
    pool = pool || _mtPool;
    if (force) delete _mtCache[pool];
    const list = document.getElementById('mtList');
    if (list) list.innerHTML = `<div class="mt-empty">${t('msg_tpl.loading', 'Loading templates...')}</div>`;
    sendToCS({ action: 'vrcGetMessageTemplates', pool });
}

function onMessageTemplates(payload) {
    const pool = payload?.pool || 'message';
    _mtCache[pool] = payload.messages || [];
    if (payload.error) showToast(false, payload.error);
    if (pool === _mtPool) mtRender();
}

function onMessageTemplateResult(payload) {
    _mtSavingSlot = -1;
    if (payload?.ok) {
        showToast(true, t('msg_tpl.saved', 'Template saved'));
        _mtEditSlot = -1;
        return;
    }
    const cooldown = payload?.cooldown ?? 0;
    showToast(false, cooldown > 0
        ? tf('msg_tpl.cooldown_error', { minutes: cooldown }, 'Slot is on cooldown for {minutes} more minutes')
        : t('msg_tpl.save_failed', 'Could not save template'));
    mtRender();
}

function mtRender() {
    const list = document.getElementById('mtList');
    if (!list) return;
    const rows = (_mtCache[_mtPool] || []).slice().sort((a, b) => (a.slot ?? 0) - (b.slot ?? 0));

    if (!rows.length) {
        list.innerHTML = `<div class="mt-empty">${t('msg_tpl.empty', 'No templates found.')}</div>`;
        return;
    }

    list.innerHTML = rows.map(row => {
        const slot = row.slot ?? 0;
        const text = row.message || '';
        const cd = row.remainingCooldownMinutes ?? 0;
        const locked = cd > 0 || row.canBeUpdated === false;

        if (slot === _mtEditSlot) {
            return `<div class="mt-row mt-row-edit">
                <div class="mt-slot">${slot}</div>
                <div class="mt-body">
                    <input type="text" id="mtInput" class="vrcn-edit-field" maxlength="64" value="${esc(text)}"
                        onkeydown="if(event.key==='Enter'){mtSave(${slot});}else if(event.key==='Escape'){mtCancelEdit();}">
                    <div class="myp-edit-actions">
                        <button class="vrcn-button" onclick="mtCancelEdit()">${esc(t('common.cancel', 'Cancel'))}</button>
                        <button class="vrcn-button vrcn-btn-primary" onclick="mtSave(${slot})">${esc(t('common.save', 'Save'))}</button>
                    </div>
                </div>
            </div>`;
        }

        const cdBadge = cd > 0
            ? `<span class="vrcn-badge mt-cd"><span class="msi" style="font-size:11px;">schedule</span> ${tf('msg_tpl.cooldown', { minutes: cd }, '{minutes}m')}</span>`
            : '';
        const editBtn = locked
            ? `<button class="myp-edit-btn" disabled title="${esc(t('msg_tpl.locked', 'On cooldown'))}"><span class="msi" style="font-size:14px;">lock</span></button>`
            : `<button class="myp-edit-btn" onclick="mtStartEdit(${slot})" title="${esc(t('common.edit', 'Edit'))}"><span class="msi" style="font-size:14px;">edit</span></button>`;

        return `<div class="mt-row${locked ? ' mt-row-locked' : ''}">
            <div class="mt-slot">${slot}</div>
            <div class="mt-body">
                <div class="mt-text">${text ? esc(text) : `<span class="mt-text-empty">${esc(t('msg_tpl.slot_empty', 'Empty slot'))}</span>`}</div>
            </div>
            ${cdBadge}
            ${editBtn}
        </div>`;
    }).join('');
}

function mtStartEdit(slot) {
    _mtEditSlot = slot;
    mtRender();
    const input = document.getElementById('mtInput');
    if (input) { input.focus(); input.select(); }
}

function mtCancelEdit() {
    _mtEditSlot = -1;
    mtRender();
}

function mtSave(slot) {
    const input = document.getElementById('mtInput');
    if (!input) return;
    const text = input.value.trim();
    if (!text) { showToast(false, t('msg_tpl.empty_error', 'Message cannot be empty')); return; }
    _mtSavingSlot = slot;
    sendToCS({ action: 'vrcUpdateMessageTemplate', pool: _mtPool, slot, message: text });
}

/* === VRChat Log Viewer === */

const LGV_LEVELS = ['Debug', 'Warning', 'Error'];
const LGV_LEVEL_BOX = { Debug: 'lgvLvlDebug', Warning: 'lgvLvlWarning', Error: 'lgvLvlError' };

let _lgvFiles = [];
let _lgvEntries = [];
let _lgvCategories = [];
let _lgvSelectedCats = new Set();
let _lgvQuery = '';
let _lgvTimer = null;
let _lgvActiveLevels = new Set(LGV_LEVELS);
let _lgvSelected = new Set();

function openLogViewerModal() {
    const overlay = document.getElementById('modalLogViewer');
    if (!overlay) return;
    overlay.style.display = 'flex';
    lgvSyncLevelButtons();
    _lgvSelected.clear();
    lvRefresh();
}

function closeLogViewerModal() {
    const overlay = document.getElementById('modalLogViewer');
    if (overlay) overlay.style.display = 'none';
    clearTimeout(_lgvTimer);
    lgvCloseCatMenu();
}

function lvRefresh() {
    lgvSetStatus(t('log_viewer.loading', 'Loading log entries'));
    sendToCS({ action: 'vrcGetLogFiles' });
}

function onLogFiles(payload) {
    _lgvFiles = payload?.files || [];
    const sel = document.getElementById('lgvFile');
    if (!sel) return;

    if (!_lgvFiles.length) {
        sel.innerHTML = `<option value="">${esc(t('log_viewer.no_files', 'No VRChat output logs found'))}</option>`;
        if (sel._vnRefresh) sel._vnRefresh();
        lgvSetStatus('');
        lgvShowEmpty(t('log_viewer.no_files', 'No VRChat output logs found'),
            t('log_viewer.no_files_description', 'Start VRChat once and refresh after output_log_*.txt appears.'));
        return;
    }

    sel.innerHTML = _lgvFiles.map((f, i) =>
        `<option value="${esc(f.name)}">${esc(f.name)} — ${esc(formatFileSize(f.sizeBytes || 0))}${i === 0 ? ' · ' + esc(t('log_viewer.latest', 'latest')) : ''}</option>`).join('');
    if (sel._vnRefresh) sel._vnRefresh();
    lgvLoad();
}

function lgvOnSearch(value) {
    _lgvQuery = value;
    clearTimeout(_lgvTimer);
    _lgvTimer = setTimeout(lgvLoad, 300);
}

function lgvActiveLevels() {
    return LGV_LEVELS.filter(l => _lgvActiveLevels.has(l));
}

function lgvToggleLevel(level) {
    if (_lgvActiveLevels.has(level)) _lgvActiveLevels.delete(level);
    else _lgvActiveLevels.add(level);
    lgvSyncLevelButtons();
    lgvLoad();
}

function lgvSyncLevelButtons() {
    LGV_LEVELS.forEach(l => {
        document.getElementById(LGV_LEVEL_BOX[l])?.classList.toggle('active', _lgvActiveLevels.has(l));
    });
}

function lgvLoad() {
    const sel = document.getElementById('lgvFile');
    const file = sel?.value || '';
    if (!file) return;
    lgvSetStatus(t('log_viewer.loading', 'Loading log entries'));
    sendToCS({
        action: 'vrcReadLogFile',
        file,
        query: _lgvQuery.trim(),
        levels: lgvActiveLevels(),
        categories: [..._lgvSelectedCats],
        max: 2000,
    });
}

function onLogLines(payload) {
    if (payload?.error) {
        lgvSetStatus('');
        lgvShowEmpty(t('log_viewer.error_load_entries', 'Failed to load VRChat log entries.'), payload.error);
        return;
    }

    _lgvEntries = payload?.entries || [];
    if (Array.isArray(payload?.categories)) {
        _lgvCategories = payload.categories;
        [..._lgvSelectedCats].forEach(c => { if (!_lgvCategories.includes(c)) _lgvSelectedCats.delete(c); });
        lgvRenderCatLabel();
    }

    const body = document.getElementById('lgvBody');
    if (!body) return;

    if (!_lgvEntries.length) {
        lgvShowEmpty(t('log_viewer.no_entries', 'No entries match the current filters'),
            t('log_viewer.no_entries_description', 'Adjust levels, category, or search text.'));
        lgvSetStatus('');
        return;
    }

    const visible = new Set(_lgvEntries.map(e => e.lineNumber));
    [..._lgvSelected].forEach(n => { if (!visible.has(n)) _lgvSelected.delete(n); });

    body.innerHTML = _lgvEntries.map(e => lgvRowHtml(e)).join('');
    body.scrollTop = body.scrollHeight;
    lgvSyncSelectAll();

    const parts = [tf('log_viewer.loaded_count', { loaded: _lgvEntries.length, total: payload.total ?? 0 }, '{loaded} / {total} loaded')];
    if (payload.truncated) parts.push(t('log_viewer.status.truncated', 'showing the newest 2000'));
    lgvSetStatus(parts.join(' · '));
    lgvUpdateSelectionStatus();
}

function lgvRowHtml(e) {
    const cat = e.category || t('log_viewer.no_category', 'No category');
    const contCount = (e.contLines || []).length;
    const cont = contCount > 0
        ? `<span class="lgv-cont">${esc(tf('log_viewer.continuation_count', { count: contCount }, '+{count} lines'))}</span>`
        : '';
    const sel = _lgvSelected.has(e.lineNumber);
    return `<div class="lgv-row${sel ? ' lgv-row-sel' : ''}" data-line="${e.lineNumber}"
        onclick="lgvToggleRow(${e.lineNumber})"
        oncontextmenu="lgvRowMenu(event, ${e.lineNumber})">
        <div class="lgv-check"><span class="msi ${sel ? 'lgv-chk-on' : 'lgv-chk-off'}">${sel ? 'check_circle' : 'radio_button_unchecked'}</span></div>
        <div class="lgv-time">${esc(e.timestamp)}</div>
        <div><span class="vrcn-badge ${lgvBadgeClass(e.level)}">${esc(e.level)}</span></div>
        <div class="lgv-cat" title="${esc(cat)}">${esc(cat)}</div>
        <div class="lgv-msg">${lgvHighlight(e.message)}${cont}</div>
    </div>`;
}

function lgvEntry(lineNumber) {
    return _lgvEntries.find(e => e.lineNumber === lineNumber) || null;
}

function lgvRedrawRow(lineNumber) {
    const e = lgvEntry(lineNumber);
    const el = document.querySelector(`.lgv-row[data-line="${lineNumber}"]`);
    if (!e || !el) return;
    el.outerHTML = lgvRowHtml(e);
}

function lgvToggleRow(lineNumber) {
    if (_lgvSelected.has(lineNumber)) _lgvSelected.delete(lineNumber);
    else _lgvSelected.add(lineNumber);
    lgvRedrawRow(lineNumber);
    lgvSyncSelectAll();
    lgvUpdateSelectionStatus();
}

function lgvToggleSelectAll() {
    const all = _lgvEntries.length > 0 && _lgvEntries.every(e => _lgvSelected.has(e.lineNumber));
    if (all) _lgvSelected.clear();
    else _lgvEntries.forEach(e => _lgvSelected.add(e.lineNumber));
    _lgvEntries.forEach(e => lgvRedrawRow(e.lineNumber));
    lgvSyncSelectAll();
    lgvUpdateSelectionStatus();
}

function lgvSyncSelectAll() {
    const el = document.getElementById('lgvSelAll');
    if (!el) return;
    const all = _lgvEntries.length > 0 && _lgvEntries.every(e => _lgvSelected.has(e.lineNumber));
    el.textContent = all ? 'check_circle' : 'radio_button_unchecked';
    el.classList.toggle('lgv-chk-on', all);
    el.classList.toggle('lgv-chk-off', !all);
}

function lgvClearSelection() {
    const was = [..._lgvSelected];
    _lgvSelected.clear();
    was.forEach(lgvRedrawRow);
    lgvSyncSelectAll();
    lgvUpdateSelectionStatus();
}

function lgvUpdateSelectionStatus() {
    const el = document.getElementById('lgvSelCount');
    if (!el) return;
    el.textContent = _lgvSelected.size
        ? tf('log_viewer.selected_count', { count: _lgvSelected.size }, '{count} selected')
        : '';
}

function lgvEntryText(e) {
    return [e.raw || `${e.timestamp} ${e.level} - ${e.message}`].concat(e.contLines || []).join('\n');
}

function lgvEntryMessageText(e) {
    return [e.message].concat(e.contLines || []).join('\n');
}

function lgvCopyText(text, empty) {
    if (!text) { if (empty) showToast(false, empty); return; }
    navigator.clipboard.writeText(text)
        .then(() => showToast(true, t('log_viewer.copied', 'Copied log text to clipboard')))
        .catch(() => showToast(false, t('log_viewer.copy_failed', 'Failed to copy log text')));
}

function lgvCopySelected() {
    const text = _lgvEntries
        .filter(e => _lgvSelected.has(e.lineNumber))
        .map(lgvEntryText)
        .join('\n');
    lgvCopyText(text, t('log_viewer.nothing_selected', 'Nothing selected'));
}

function lgvRowMenu(event, lineNumber) {
    event.preventDefault();
    event.stopPropagation();
    const e = lgvEntry(lineNumber);
    if (!e) return;
    if (typeof VrcnShowContextMenu !== 'function') return;

    VrcnShowContextMenu(event.clientX, event.clientY, [
        { icon: 'content_copy', label: t('log_viewer.copy_entry', 'Copy row'),
          action: () => lgvCopyText(lgvEntryText(e)) },
        { icon: 'notes', label: t('log_viewer.copy_message', 'Copy message'),
          action: () => lgvCopyText(lgvEntryMessageText(e)) },
        'sep',
        { icon: 'select_all', label: t('log_viewer.copy_selected', 'Copy selected'),
          action: () => lgvCopySelected() },
        { icon: 'close', label: t('log_viewer.clear_selected', 'Clear selected'),
          action: () => lgvClearSelection() },
    ]);
}

function lgvBadgeClass(level) {
    if (level === 'Error') return 'lgv-b-error';
    if (level === 'Warning') return 'lgv-b-warn';
    return 'lgv-b-debug';
}

function lgvShowEmpty(title, desc) {
    const body = document.getElementById('lgvBody');
    if (!body) return;
    body.innerHTML = `<div class="lgv-empty"><div class="lgv-empty-title">${esc(title)}</div><div>${esc(desc || '')}</div></div>`;
}

function lgvHighlight(text) {
    const q = _lgvQuery.trim();
    if (!q) return esc(text);
    const lower = text.toLowerCase();
    const needle = q.toLowerCase();
    let out = '';
    let at = 0;
    for (;;) {
        const idx = lower.indexOf(needle, at);
        if (idx < 0) { out += esc(text.slice(at)); break; }
        out += esc(text.slice(at, idx)) + '<mark class="lgv-hit">' + esc(text.slice(idx, idx + needle.length)) + '</mark>';
        at = idx + needle.length;
    }
    return out;
}

function lgvSetStatus(text) {
    const el = document.getElementById('lgvStatus');
    if (el) el.textContent = text || '';
}

function lgvRenderCatLabel() {
    const el = document.getElementById('lgvCatLabel');
    if (!el) return;
    el.textContent = _lgvSelectedCats.size
        ? tf('log_viewer.categories_selected', { count: _lgvSelectedCats.size }, '{count} categories')
        : t('log_viewer.all_categories', 'All categories');
}

function lgvToggleCatMenu() {
    const menu = document.getElementById('lgvCatMenu');
    if (!menu) return;
    if (menu.style.display === 'block') { lgvCloseCatMenu(); return; }

    const rows = _lgvCategories.map(c => {
        const on = _lgvSelectedCats.has(c);
        return `<div class="vn-select-option" onclick="lgvToggleCat('${jsq(c)}')">
            <span class="msi" style="font-size:15px;flex-shrink:0;color:${on ? 'var(--accent)' : 'var(--tx3)'};">${on ? 'check_circle' : 'radio_button_unchecked'}</span>
            <span style="flex:1;">${esc(c)}</span>
        </div>`;
    }).join('');

    menu.innerHTML = `<div class="vn-select-option lgv-catclear" onclick="lgvClearCats()">${esc(t('log_viewer.clear_categories', 'Clear categories'))}</div>`
        + (rows || `<div class="vn-select-option" style="pointer-events:none;color:var(--tx3);">${esc(t('log_viewer.no_category', 'No category'))}</div>`);
    menu.style.display = 'block';

    setTimeout(() => {
        const close = e => {
            if (!menu.contains(e.target) && !e.target.closest('#lgvCatBtn')) {
                lgvCloseCatMenu();
                document.removeEventListener('click', close);
            }
        };
        document.addEventListener('click', close);
    }, 0);
}

function lgvCloseCatMenu() {
    const menu = document.getElementById('lgvCatMenu');
    if (menu) { menu.style.display = 'none'; menu.innerHTML = ''; }
}

function lgvToggleCat(cat) {
    if (_lgvSelectedCats.has(cat)) _lgvSelectedCats.delete(cat);
    else _lgvSelectedCats.add(cat);
    lgvRenderCatLabel();
    lgvCloseCatMenu();
    lgvLoad();
}

function lgvClearCats() {
    _lgvSelectedCats.clear();
    lgvRenderCatLabel();
    lgvCloseCatMenu();
    lgvLoad();
}

function lgvCopy() {
    if (_lgvSelected.size) { lgvCopySelected(); return; }
    if (!_lgvEntries.length) return;
    lgvCopyText(_lgvEntries.map(lgvEntryText).join('\n'));
}
