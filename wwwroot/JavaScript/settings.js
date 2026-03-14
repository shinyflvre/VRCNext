function addFileToList(f) {
    postedFiles.unshift(f);
    renderFileList();
}

function renderFileList() {
    const e = document.getElementById('fileList');
    if (!postedFiles.length) {
        e.innerHTML = '<div class="empty-msg">No files posted yet</div>';
        return;
    }
    e.innerHTML = postedFiles.map((f, i) =>
        `<div class="file-row"><span class="file-name">${esc(f.name)}</span><span class="file-channel">${esc(f.channel)}</span><span class="file-size">${f.size}</span><span class="file-time">${f.time}</span><button class="file-del" onclick="deleteFile(${i})" title="Delete"><span class="msi" style="font-size:16px;">delete</span></button></div>`
    ).join('');
}

function deleteFile(i) {
    const f = postedFiles[i];
    if (f?.messageId) sendToCS({ action: 'deletePost', messageId: f.messageId, webhookUrl: f.webhookUrl });
}

function renderWebhookCards(w) {
    const e = document.getElementById('whCards'), s = (w || []).slice(0, 4);
    while (s.length < 4) s.push({});
    e.innerHTML = s.map((w, i) =>
        `<div class="wh-card"><div class="wh-top"><span class="wh-num">#${i + 1}</span><input class="vrcn-edit-field" id="whName${i}" value="${esc(w.Name || w.name || '')}" placeholder="Channel ${i + 1}" style="width:120px;"><label class="toggle"><input type="checkbox" id="whOn${i}" ${(w.Enabled || w.enabled) ? 'checked' : ''}><div class="toggle-track"><div class="toggle-knob"></div></div></label></div><input class="vrcn-edit-field" id="whUrl${i}" value="${esc(w.Url || w.url || '')}" placeholder="https://discord.com/api/webhooks/..." style="width:100%;"></div>`
    ).join('');
}

function renderFolders(f) {
    const e = document.getElementById('folderList');
    if (!f || !f.length) {
        e.innerHTML = '<div class="folder-empty">No folders</div>';
        return;
    }
    e.innerHTML = f.map((x, i) =>
        `<div class="folder-item" onclick="selectedFolderIdx=${i}" style="${selectedFolderIdx === i ? 'background:var(--bg-hover)' : ''}"><span>${esc(x)}</span><button class="folder-remove" onclick="event.stopPropagation();removeFolderAt(${i})" title="Remove"><span class="msi" style="font-size:16px;">close</span></button></div>`
    ).join('');
}

function addFolder() {
    sendToCS({ action: 'addFolder' });
}

function removeFolder() {
    if (selectedFolderIdx >= 0 && settings.folders?.[selectedFolderIdx]) {
        settings.folders.splice(selectedFolderIdx, 1);
        selectedFolderIdx = -1;
        renderFolders(settings.folders);
        autoSave();
    }
}

function removeFolderAt(i) {
    if (settings.folders) {
        settings.folders.splice(i, 1);
        selectedFolderIdx = -1;
        renderFolders(settings.folders);
        autoSave();
    }
}

function renderExtraExe(l) {
    const e = document.getElementById('extraExeList');
    if (!l || !l.length) {
        e.innerHTML = '<div class="folder-empty">None</div>';
        return;
    }
    e.innerHTML = l.map((x, i) =>
        `<div class="exe-item"><span>${esc(x.split(/[\\\\/]/).pop())}</span><button class="exe-remove" onclick="removeExtraExe(${i})" title="Remove"><span class="msi" style="font-size:16px;">close</span></button></div>`
    ).join('');
}

function browseExe(t) {
    sendToCS({ action: 'browseExe', target: t });
}

function removeExtraExe(i) {
    if (settings.extraExe) {
        settings.extraExe.splice(i, 1);
        renderExtraExe(settings.extraExe);
        autoSave();
    }
}

function saveSettings() {
    const w = [];
    for (let i = 0; i < 4; i++) {
        const nameEl = document.getElementById('whName' + i);
        const urlEl = document.getElementById('whUrl' + i);
        const onEl = document.getElementById('whOn' + i);
        w.push({
            Name: nameEl?.value || '',
            Url: urlEl?.value || '',
            Enabled: onEl?.checked || false
        });
    }
    const payload = {
        action: 'saveSettings',
        data: {
            botName: document.getElementById('setBotName').value,
            botAvatar: document.getElementById('setBotAvatar').value,
            webhooks: w,
            folders: settings.folders || [],
            vrcPath: document.getElementById('setVrcPath').value,
            extraExe: settings.extraExe || [],
            autoStart: false, // legacy — kept for JSON compat
            relayAutoStartVR:        document.getElementById('setAutoStartVR')?.checked       ?? false,
            relayAutoStartDesktop:   document.getElementById('setAutoStartDesktop')?.checked  ?? false,
            startWithWindows: document.getElementById('setStartWithWindows').checked,
            notifySound: document.getElementById('setNotifySound').checked,
            theme: currentTheme,
            specialTheme: currentSpecialTheme,
            autoColorAccuracy: autoColorAccuracy,
            playBtnTheme: currentPlayBtnTheme,
            dashBgPath: dashBgPath,
            dashOpacity: parseInt(document.getElementById('setDashOpacity').value) || 40,
            randomDashBg: document.getElementById('setRandomBg').checked,
            vrcUsername: document.getElementById('setVrcUser').value,
            vrcPassword: document.getElementById('setVrcPass').value,
            sfMultiplier: parseFloat(document.getElementById('sfMultiplier').value) || 1,
            sfLockX: document.getElementById('sfLockX').checked,
            sfLockY: document.getElementById('sfLockY').checked,
            sfLockZ: document.getElementById('sfLockZ').checked,
            sfLeftHand: document.getElementById('sfLeftHand').checked,
            sfRightHand: document.getElementById('sfRightHand').checked,
            sfUseGrip: document.getElementById('sfUseGrip').checked,
            chatboxAutoStart: false, // legacy
            chatboxAutoStartVR:       document.getElementById('setCbAutoStartVR')?.checked        ?? false,
            chatboxAutoStartDesktop:  document.getElementById('setCbAutoStartDesktop')?.checked   ?? false,
            sfAutoStart: false, // legacy
            sfAutoStartVR:            document.getElementById('setSfAutoStartVR')?.checked        ?? false,
            ytAutoStartVR:            document.getElementById('setYtAutoStartVR')?.checked        ?? false,
            ytAutoStartDesktop:       document.getElementById('setYtAutoStartDesktop')?.checked   ?? false,
            vfAutoStartVR:            document.getElementById('setVfAutoStartVR')?.checked        ?? false,
            vfAutoStartDesktop:       document.getElementById('setVfAutoStartDesktop')?.checked   ?? false,
            discordPresenceAutoStart: false, // legacy
            dpAutoStartVR:            document.getElementById('setDpAutoStartVR')?.checked        ?? false,
            dpAutoStartDesktop:       document.getElementById('setDpAutoStartDesktop')?.checked   ?? false,
            vroAutoStart: false, // legacy
            vroAutoStartVR:           document.getElementById('setVroAutoStartVR')?.checked       ?? false,
            vroAttachLeft:   document.getElementById('vroAttachLeft')?.value === 'left',
            vroAttachHand:   document.getElementById('vroAttachPart')?.value === 'hand',
            vroPosX:  parseFloat(document.getElementById('vroPosX')?.value) || 0,
            vroPosY:  parseFloat(document.getElementById('vroPosY')?.value) || 0.07,
            vroPosZ:  parseFloat(document.getElementById('vroPosZ')?.value) || -0.05,
            vroRotX:  parseFloat(document.getElementById('vroRotX')?.value) || -80,
            vroRotY:  parseFloat(document.getElementById('vroRotY')?.value) || 0,
            vroRotZ:  parseFloat(document.getElementById('vroRotZ')?.value) || 0,
            vroWidth: parseFloat(document.getElementById('vroWidth')?.value) || 0.22,
            vroKeybind:        vroComboIds    ?? [],
            vroKeybindHand:    vroComboHand   ?? 0,
            vroKeybindDt:      vroDtIds       ?? [],
            vroKeybindDtHand:  vroDtHand      ?? 0,
            vroKeybindMode:    vroKeybindMode ?? 0,
            vroControlRadius:  parseInt(document.getElementById('vroControlRadius')?.value) || 28,
            dpHideJoinBtnJoinMe: document.getElementById('dpHideJoinBtn_joinme')?.checked ?? false,
            dpHideJoinBtnOnline: document.getElementById('dpHideJoinBtn_online')?.checked ?? false,
            dpHideJoinBtnAskMe:  document.getElementById('dpHideJoinBtn_askme')?.checked  ?? false,
            dpHideJoinBtnBusy:   document.getElementById('dpHideJoinBtn_busy')?.checked   ?? false,
            dpHideInstIdJoinMe:  document.getElementById('dpHideInstId_joinme')?.checked ?? false,
            dpHideInstIdOnline:  document.getElementById('dpHideInstId_online')?.checked ?? false,
            dpHideInstIdAskMe:   document.getElementById('dpHideInstId_askme')?.checked  ?? false,
            dpHideInstIdBusy:    document.getElementById('dpHideInstId_busy')?.checked   ?? false,
            dpHideLocJoinMe:     document.getElementById('dpHideLoc_joinme')?.checked    ?? false,
            dpHideLocOnline:     document.getElementById('dpHideLoc_online')?.checked    ?? false,
            dpHideLocAskMe:      document.getElementById('dpHideLoc_askme')?.checked     ?? false,
            dpHideLocBusy:       document.getElementById('dpHideLoc_busy')?.checked      ?? false,
            dpHidePlayersJoinMe: document.getElementById('dpHidePlayers_joinme')?.checked ?? false,
            dpHidePlayersOnline: document.getElementById('dpHidePlayers_online')?.checked ?? false,
            dpHidePlayersAskMe:  document.getElementById('dpHidePlayers_askme')?.checked  ?? false,
            dpHidePlayersBusy:   document.getElementById('dpHidePlayers_busy')?.checked   ?? false,
            imgCacheEnabled: document.getElementById('setImgCacheEnabled').checked,
            imgCacheLimitGb: parseInt(document.getElementById('setImgCacheLimit').value) || 5,
            ffcEnabled: document.getElementById('setFfcEnabled').checked,
            memoryTrimEnabled: document.getElementById('setMemoryTrimEnabled').checked
        }
    };
    sendToCS(payload);
}

// Autosave: debounced save on any settings change
let _autoSaveTimer = null;
function autoSave() {
    clearTimeout(_autoSaveTimer);
    _autoSaveTimer = setTimeout(() => saveSettings(), 600);
}
// Attach autosave listeners after DOM ready
function initAutoSave() {
    const ids = ['setBotName','setBotAvatar','setVrcPath','setStartWithWindows',
        'setNotifySound','setDashOpacity','setRandomBg',
        'setVrcUser','setVrcPass',
        'setAutoStartVR','setAutoStartDesktop',
        'setCbAutoStartVR','setCbAutoStartDesktop',
        'setSfAutoStartVR',
        'setYtAutoStartVR','setYtAutoStartDesktop',
        'setVfAutoStartVR','setVfAutoStartDesktop',
        'setDpAutoStartVR','setDpAutoStartDesktop',
        'setImgCacheEnabled','setImgCacheLimit','setMemoryTrimEnabled'];
    ids.forEach(id => {
        const el = document.getElementById(id);
        if (!el) return;
        if (el.type === 'checkbox') el.addEventListener('change', autoSave);
        else if (el.type === 'range') el.addEventListener('input', autoSave);
        else el.addEventListener('input', autoSave);
    });
    // Webhook fields (4 slots x 3 fields)
    for (let i = 0; i < 4; i++) {
        ['whName','whUrl','whOn'].forEach(prefix => {
            const el = document.getElementById(prefix + i);
            if (!el) return;
            if (el.type === 'checkbox') el.addEventListener('change', autoSave);
            else el.addEventListener('input', autoSave);
        });
    }
}

function loadSettingsToUI(s) {
    settings = s;
    // Debug: log webhook data received from C#
    const wh = s.Webhooks || s.webhooks || [];
    console.log('[LOAD] Settings received. Webhooks:', JSON.stringify(wh));
    document.getElementById('setBotName').value = s.BotName || s.botName || '';
    document.getElementById('setBotAvatar').value = s.BotAvatarUrl || s.botAvatarUrl || '';
    document.getElementById('setVrcPath').value = s.VrcPath || s.vrcPath || '';
    document.getElementById('setVrcUser').value = s.VrcUsername || s.vrcUsername || '';
    document.getElementById('setVrcPass').value = s.VrcPassword || s.vrcPassword || '';
    const _asVR  = document.getElementById('setAutoStartVR');  if (_asVR)  _asVR.checked  = s.RelayAutoStartVR  ?? s.relayAutoStartVR  ?? false;
    const _asDT  = document.getElementById('setAutoStartDesktop'); if (_asDT) _asDT.checked = s.RelayAutoStartDesktop ?? s.relayAutoStartDesktop ?? false;
    document.getElementById('setStartWithWindows').checked = s.StartWithWindows || s.startWithWindows || false;
    document.getElementById('setNotifySound').checked = s.NotifySound || s.notifySound || false;
    settings.folders = s.WatchFolders || s.watchFolders || s.folders || [];
    settings.extraExe = s.ExtraExe || s.extraExe || [];
    settings.notifySound = s.NotifySound || s.notifySound || false;
    dashBgPath = s.DashBgPath || s.dashBgPath || '';
    dashOpacity = s.DashOpacity || s.dashOpacity || 40;
    document.getElementById('setDashOpacity').value = dashOpacity;
    document.getElementById('dashOpacityVal').textContent = dashOpacity + '%';
    const randomBg = s.RandomDashBg || s.randomDashBg || false;
    document.getElementById('setRandomBg').checked = randomBg;
    if (randomBg) {
        // Request random image from watch folders
        sendToCS({ action: 'vrcRandomDashBg' });
    } else if (dashBgPath) {
        document.getElementById('dashBgName').textContent = dashBgPath.split(/[\\\\/]/).pop();
        sendToCS({ action: 'vrcLoadDashBg', path: dashBgPath });
    }
    renderWebhookCards((s.Webhooks || s.webhooks || []).slice(0, 4));
    renderFolders(settings.folders);
    renderExtraExe(settings.extraExe);
    updateFolderFilterOptions(settings.folders);
    currentTheme = s.Theme || s.theme || 'midnight';
    currentSpecialTheme = s.SpecialTheme || s.specialTheme || '';
    autoColorAccuracy = s.AutoColorAccuracy ?? s.autoColorAccuracy ?? 50;
    const accSlider = document.getElementById('setAutoAccuracy');
    if (accSlider) { accSlider.value = autoColorAccuracy; document.getElementById('autoAccuracyVal').textContent = autoColorAccuracy + '%'; }
    const accRow = document.getElementById('autoAccuracyRow');
    if (accRow) accRow.style.display = currentSpecialTheme === 'auto' ? 'flex' : 'none';
    if (THEMES[currentTheme]) applyColors(THEMES[currentTheme].c);
    else if (!currentTheme.startsWith('custom_')) { currentTheme = 'midnight'; applyColors(THEMES.midnight.c); }
    // custom_ themes are applied later when customColors loads
    renderThemeChips();
    renderSpecialThemeChips();
    currentPlayBtnTheme = s.PlayBtnTheme || s.playBtnTheme || '';
    applyPlayBtnTheme(currentPlayBtnTheme);

    // Restore chatbox settings
    document.getElementById('cbShowTime').checked = s.CbShowTime ?? s.cbShowTime ?? true;
    document.getElementById('cbShowMedia').checked = s.CbShowMedia ?? s.cbShowMedia ?? true;
    document.getElementById('cbShowPlaytime').checked = s.CbShowPlaytime ?? s.cbShowPlaytime ?? true;
    document.getElementById('cbShowCustom').checked = s.CbShowCustomText ?? s.cbShowCustomText ?? true;
    document.getElementById('cbShowSystemStats').checked = s.CbShowSystemStats ?? s.cbShowSystemStats ?? false;
    document.getElementById('cbShowAfk').checked = s.CbShowAfk ?? s.cbShowAfk ?? false;
    document.getElementById('cbAfkMessage').value = s.CbAfkMessage || s.cbAfkMessage || 'Currently AFK';
    document.getElementById('cbSuppressSound').checked = s.CbSuppressSound ?? s.cbSuppressSound ?? true;
    const cbAfkOn = s.CbShowAfk ?? s.cbShowAfk ?? false;
    document.getElementById('cbAfkCard').style.display = cbAfkOn ? '' : 'none';
    const cbTf = s.CbTimeFormat || s.cbTimeFormat || 'hh:mm tt';
    const cbTfEl = document.getElementById('cbTimeFormat');
    if (cbTfEl) cbTfEl.value = cbTf;
    const cbSep = s.CbSeparator || s.cbSeparator || ' | ';
    const cbSepEl = document.getElementById('cbSeparator');
    if (cbSepEl) cbSepEl.value = cbSep;
    const cbInt = s.CbIntervalMs || s.cbIntervalMs || 5000;
    const cbIntEl = document.getElementById('cbInterval');
    if (cbIntEl) cbIntEl.value = String(cbInt);
    chatboxCustomLines = s.CbCustomLines || s.cbCustomLines || [];
    renderChatboxLines();

    // Restore Space Flight settings
    document.getElementById('sfMultiplier').value = s.SfMultiplier ?? s.sfMultiplier ?? 1;
    document.getElementById('sfMultVal').textContent = (s.SfMultiplier ?? s.sfMultiplier ?? 1) + 'x';
    document.getElementById('sfLockX').checked = s.SfLockX ?? s.sfLockX ?? false;
    document.getElementById('sfLockY').checked = s.SfLockY ?? s.sfLockY ?? false;
    document.getElementById('sfLockZ').checked = s.SfLockZ ?? s.sfLockZ ?? false;
    document.getElementById('sfLeftHand').checked = s.SfLeftHand ?? s.sfLeftHand ?? false;
    document.getElementById('sfRightHand').checked = s.SfRightHand ?? s.sfRightHand ?? true;
    document.getElementById('sfUseGrip').checked = s.SfUseGrip ?? s.sfUseGrip ?? true;

    // Restore VR/Desktop auto-start flags
    const _set = (id, v) => { const el = document.getElementById(id); if (el) el.checked = !!v; };
    _set('setCbAutoStartVR',      s.ChatboxAutoStartVR      ?? s.chatboxAutoStartVR      ?? false);
    _set('setCbAutoStartDesktop', s.ChatboxAutoStartDesktop ?? s.chatboxAutoStartDesktop ?? false);
    _set('setSfAutoStartVR',      s.SfAutoStartVR           ?? s.sfAutoStartVR           ?? false);
    _set('setYtAutoStartVR',      s.YtAutoStartVR           ?? s.ytAutoStartVR           ?? false);
    _set('setYtAutoStartDesktop', s.YtAutoStartDesktop      ?? s.ytAutoStartDesktop      ?? false);
    _set('setVfAutoStartVR',      s.VfAutoStartVR           ?? s.vfAutoStartVR           ?? false);
    _set('setVfAutoStartDesktop', s.VfAutoStartDesktop      ?? s.vfAutoStartDesktop      ?? false);
    _set('setDpAutoStartVR',      s.DpAutoStartVR           ?? s.dpAutoStartVR           ?? false);
    _set('setDpAutoStartDesktop', s.DpAutoStartDesktop      ?? s.dpAutoStartDesktop      ?? false);
    // Restore Discord privacy + join button toggles
    const _dpPv = [
        ['dpHideInstId_joinme', 'DpHideInstIdJoinMe'],  ['dpHideInstId_online', 'DpHideInstIdOnline'],
        ['dpHideInstId_askme',  'DpHideInstIdAskMe'],   ['dpHideInstId_busy',   'DpHideInstIdBusy'],
        ['dpHideLoc_joinme',    'DpHideLocJoinMe'],     ['dpHideLoc_online',    'DpHideLocOnline'],
        ['dpHideLoc_askme',     'DpHideLocAskMe'],      ['dpHideLoc_busy',      'DpHideLocBusy'],
        ['dpHidePlayers_joinme','DpHidePlayersJoinMe'], ['dpHidePlayers_online','DpHidePlayersOnline'],
        ['dpHidePlayers_askme', 'DpHidePlayersAskMe'],  ['dpHidePlayers_busy',  'DpHidePlayersBusy'],
        ['dpHideJoinBtn_joinme','DpHideJoinBtnJoinMe'], ['dpHideJoinBtn_online','DpHideJoinBtnOnline'],
        ['dpHideJoinBtn_askme', 'DpHideJoinBtnAskMe'],  ['dpHideJoinBtn_busy',  'DpHideJoinBtnBusy'],
    ];
    for (const [id, key] of _dpPv) {
        const el = document.getElementById(id);
        if (el) el.checked = s[key] ?? s[key.charAt(0).toLowerCase() + key.slice(1)] ?? false;
    }

    // VR Overlay settings
    vroLoadSettings({
        vroAttachLeft: s.VroAttachLeft ?? s.vroAttachLeft ?? true,
        vroAttachHand: s.VroAttachHand ?? s.vroAttachHand ?? true,
        vroPosX: s.VroPosX ?? s.vroPosX ?? -0.10,
        vroPosY: s.VroPosY ?? s.vroPosY ?? -0.03,
        vroPosZ: s.VroPosZ ?? s.vroPosZ ?? 0.11,
        vroRotX: s.VroRotX ?? s.vroRotX ?? -180,
        vroRotY: s.VroRotY ?? s.vroRotY ?? 46,
        vroRotZ: s.VroRotZ ?? s.vroRotZ ?? 85,
        vroWidth: s.VroWidth ?? s.vroWidth ?? 0.16,
        vroAutoStartVR:      s.VroAutoStartVR      ?? s.vroAutoStartVR      ?? false,
        vroKeybind:       s.VroKeybind       ?? s.vroKeybind       ?? [],
        vroKeybindHand:   s.VroKeybindHand   ?? s.vroKeybindHand   ?? 0,
        vroKeybindDt:     s.VroKeybindDt     ?? s.vroKeybindDt     ?? [],
        vroKeybindDtHand: s.VroKeybindDtHand ?? s.vroKeybindDtHand ?? 0,
        vroKeybindMode:    s.VroKeybindMode   ?? s.vroKeybindMode   ?? 0,
        vroControlRadius:  s.VroControlRadius ?? s.vroControlRadius ?? 16
    });
    // Auto-starts are now triggered by vrcLaunched (see messages.js)

    // Image cache settings
    const imgCacheEnabled = s.ImgCacheEnabled ?? s.imgCacheEnabled ?? true;
    const imgCacheLimitGb = s.ImgCacheLimitGb ?? s.imgCacheLimitGb ?? 5;
    document.getElementById('setImgCacheEnabled').checked = imgCacheEnabled;
    document.getElementById('setImgCacheLimit').value = imgCacheLimitGb;
    document.getElementById('imgCacheLimitVal').textContent = imgCacheLimitGb + ' GB';
    updateImgCacheUi();

    // Fast Fetch Cache
    document.getElementById('setFfcEnabled').checked = s.FfcEnabled ?? s.ffcEnabled ?? true;

    // Memory Trim
    document.getElementById('setMemoryTrimEnabled').checked = s.MemoryTrimEnabled ?? s.memoryTrimEnabled ?? false;

    // Sync custom dropdowns to reflect programmatically set values
    document.querySelectorAll('select').forEach(s => s._vnRefresh && s._vnRefresh());

    // Setup autosave listeners after UI is populated
    setTimeout(initAutoSave, 100);
}

function updateImgCacheUi() {
    const enabled = document.getElementById('setImgCacheEnabled').checked;
    document.getElementById('imgCacheLimitRow').style.display = enabled ? '' : 'none';
}

// ── VRCX Import ──────────────────────────────────────────────────────────────

function vrcxSelectFile() {
    const btn = document.getElementById('vrcxSelectBtn');
    btn.disabled = true;
    btn.innerHTML = '<span class="msi" style="font-size:16px;">hourglass_empty</span> Selecting...';
    sendToCS({ action: 'importVrcxSelect' });
}

function vrcxReset() {
    document.getElementById('vrcxPreviewBox').style.display = 'none';
    document.getElementById('vrcxProgressWrap').style.display = 'none';
    document.getElementById('vrcxSuccessCard').style.display = 'none';
    document.getElementById('vrcxImportError').style.display = 'none';
    document.getElementById('vrcxActionBtns').style.display = 'flex';
    document.getElementById('vrcxDoneBtn').style.display = 'none';
    const btn = document.getElementById('vrcxSelectBtn');
    btn.style.display = '';
    btn.disabled = false;
    btn.innerHTML = '<span class="msi" style="font-size:16px;">storage</span> Select VRCX Database';
    const start = document.getElementById('vrcxStartBtn');
    start.disabled = false;
    start.innerHTML = '<span class="msi" style="font-size:16px;">upload</span> Start Import';
}

function vrcxShowPreview(p) {
    document.getElementById('vrcxSelectBtn').style.display = 'none';
    document.getElementById('vrcxFileName').textContent = p.path || 'VRCX.sqlite3';
    const rows = [
        ['Worlds tracked',    p.worlds],
        ['Location visits',   p.locations],
        ['Friends (time)',    p.friendTimes],
        ['GPS events',        p.gps],
        ['Online / Offline',  p.onlineOffline],
        ['Status changes',    p.statuses],
        ['Bio changes',       p.bios],
    ];
    document.getElementById('vrcxPreviewRows').innerHTML = rows.map(([label, val]) =>
        `<div style="display:flex;justify-content:space-between;align-items:center;padding:4px 8px;background:var(--bg-input);border-radius:6px;">
            <span style="font-size:12px;opacity:.7;">${label}</span>
            <span style="font-size:12px;font-weight:600;">${(val ?? 0).toLocaleString()}</span>
        </div>`
    ).join('');
    document.getElementById('vrcxProgressWrap').style.display = 'none';
    document.getElementById('vrcxSuccessCard').style.display = 'none';
    document.getElementById('vrcxImportError').style.display = 'none';
    document.getElementById('vrcxActionBtns').style.display = 'flex';
    document.getElementById('vrcxDoneBtn').style.display = 'none';
    const start = document.getElementById('vrcxStartBtn');
    start.disabled = false;
    start.innerHTML = '<span class="msi" style="font-size:16px;">upload</span> Start Import';
    document.getElementById('vrcxPreviewBox').style.display = '';
}

function vrcxStartImport() {
    document.getElementById('vrcxActionBtns').style.display = 'none';
    document.getElementById('vrcxImportError').style.display = 'none';
    _vrcxSetProgress(5, 'Reading database...');
    document.getElementById('vrcxProgressWrap').style.display = '';
    sendToCS({ action: 'importVrcxStart' });
}

function _vrcxSetProgress(pct, label) {
    document.getElementById('vrcxProgressBar').style.width = pct + '%';
    document.getElementById('vrcxProgressLabel').textContent = label || '';
}

function vrcxShowProgress(p) {
    _vrcxSetProgress(p.percent ?? 0, p.status ?? '');
}

function vrcxShowDone(p) {
    _vrcxSetProgress(100, 'Done');
    setTimeout(() => {
        document.getElementById('vrcxProgressWrap').style.display = 'none';
        document.getElementById('vrcxSuccessDetail').innerHTML =
            `${(p.worlds ?? 0).toLocaleString()} worlds &nbsp;·&nbsp; ` +
            `${(p.friends ?? 0).toLocaleString()} friends &nbsp;·&nbsp; ` +
            `${(p.timelineJoins ?? 0).toLocaleString()} joins &nbsp;·&nbsp; ` +
            `${(p.friendEvents ?? 0).toLocaleString()} friend events &nbsp;·&nbsp; ` +
            `${(p.meetEvents ?? 0).toLocaleString()} meets`;
        document.getElementById('vrcxSuccessCard').style.display = '';
        document.getElementById('vrcxDoneBtn').style.display = '';
    }, 400);
}

function vrcxShowError(err) {
    document.getElementById('vrcxProgressWrap').style.display = 'none';
    const el = document.getElementById('vrcxImportError');
    el.textContent = 'Error: ' + (err || 'Unknown error');
    el.style.display = '';
    document.getElementById('vrcxActionBtns').style.display = 'flex';
    const start = document.getElementById('vrcxStartBtn');
    start.disabled = false;
    start.innerHTML = '<span class="msi" style="font-size:16px;">upload</span> Retry Import';
}
