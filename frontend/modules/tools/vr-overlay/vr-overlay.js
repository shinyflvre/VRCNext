/* === VR Wrist Overlay === */

let vroConnected    = false;
let vroVisible      = false;
let vroRecording    = false;
let vroKeybindMode  = 0;   // 0=combo(hold), 1=doubletap — which mode is ACTIVE

// Combo slot (1–4 buttons held)
let vroComboIds    = [];
let vroComboHand   = 0;   // 0=any, 1=left, 2=right

// Double-tap slot (exactly 1 button, double-press)
let vroDtIds       = [];
let vroDtHand      = 0;   // 0=any, 1=left, 2=right

let _vroAutoTimer   = null;
let _vroPrevHand    = null; // tracks previous hand selection for mirror logic
let _vroLastError   = '';

// Scale keybind state (VR overlay Size tab)
let _vroScaleKeybindIds    = [];
let _vroScaleKeybindHand   = 0;
let _vroScaleRecording     = false;
let _vroScaleManualIds     = [];
let _vroScaleManualEditing = false;
let _vroScaleAutoTimer     = null;
let _asAutoStartedByVro    = false;
let _vroScaleWasEnabled    = true;

function vroGetNames(ids) {
    return vriNames(ids);
}

let _vroKbOther     = { combo: [], comboHand: 0, dt: [], dtHand: 0 };
let _vroScaleOther  = { ids: [], hand: 0 };

function vroApplyInputMode() {
    _vroManualEditing = false;
    _vroManualIds     = [];
    _vroScaleManualEditing = false;
    _vroScaleManualIds     = [];
    document.getElementById('vroControllerVisual')?.classList.remove('editing');
    document.getElementById('vroScaleControllerVisual')?.classList.remove('editing');
    updateModePill();
    updateKeybindDisplay();
    updateRecordingUI();
    updateScaleKeybindDisplay();
    updateScaleRecordingUI();
}

function vroSwapKeybindSets() {
    const cur = { combo: vroComboIds, comboHand: vroComboHand, dt: vroDtIds, dtHand: vroDtHand };
    vroComboIds  = _vroKbOther.combo;
    vroComboHand = _vroKbOther.comboHand;
    vroDtIds     = _vroKbOther.dt;
    vroDtHand    = _vroKbOther.dtHand;
    _vroKbOther  = cur;

    const sc = { ids: _vroScaleKeybindIds, hand: _vroScaleKeybindHand };
    _vroScaleKeybindIds  = _vroScaleOther.ids;
    _vroScaleKeybindHand = _vroScaleOther.hand;
    _vroScaleOther       = sc;

    vroSendConfig();
    vroScaleSendConfig();
}

function _vroSideActive(hand, side) {
    if (!side) return true;
    return hand === 0 || (hand === 1 && side === 'left') || (hand === 2 && side === 'right');
}

function vroToggleKeybindView(btn) {
    const v = document.getElementById('vroControllerVisual');
    if (!v) return;
    btn?.classList.toggle('active', v.classList.toggle('legacy'));
}

function vroToggleScaleView(btn) {
    const v = document.getElementById('vroScaleControllerVisual');
    if (!v) return;
    btn?.classList.toggle('active', v.classList.toggle('legacy'));
}

function vroRadiusLabelText(value) {
    return tf('vro.transform.radius_value', { value }, `${value} cm`);
}

function vroFocusRadiusLabelText(value) {
    return tf('vro.visibility.radius_value', { value }, `${value} cm`);
}

// Helpers to get/set active slot.

function vroActiveIds()  { return vroKeybindMode === 0 ? vroComboIds  : vroDtIds;  }
function vroActiveHand() { return vroKeybindMode === 0 ? vroComboHand : vroDtHand; }

// State sync from C#.

function handleVroState(d) {
    const _wasConnected = vroConnected;
    vroConnected    = !!d.connected;
    vroVisible      = !!d.visible;
    vroRecording    = !!d.recording;
    _vroLastError   = d.error || '';

    if (d.keybind     !== undefined) vroComboIds   = d.keybind     || [];
    if (d.keybindHand !== undefined) vroComboHand  = d.keybindHand ?? 0;
    if (d.keybindDt   !== undefined) vroDtIds      = d.keybindDt   || [];
    if (d.keybindDtHand !== undefined) vroDtHand   = d.keybindDtHand ?? 0;
    if (d.keybindMode !== undefined) vroKeybindMode = d.keybindMode ?? 0;

    const dot   = document.getElementById('vroDot');
    const txt   = document.getElementById('vroStatusText');
    const btn   = document.getElementById('vroConnBtn');
    const badge = document.getElementById('badgeVro');
    const badgeTop = document.getElementById('badgeVroTop');

    if (!_wasConnected && vroConnected && document.getElementById('vroScaleEnabled')?.checked && !_asConnected) {
        sendToCS({ action: 'asConnect' });
        _asAutoStartedByVro = true;
    }

    if (d.connected) {
        dot?.classList.replace('offline', 'online');
        if (txt) txt.textContent = t('vro.status.connected', 'Connected');
        if (txt) txt.style.color = 'var(--ok)';
        if (btn) btn.innerHTML = `<span class="msi" style="font-size:16px;">link_off</span> ${t('common.disconnect', 'Disconnect')}`;
        badge?.classList.replace('offline', 'online');
        if (badgeTop) badgeTop.classList.add('tb-active');
        if (currentSpecialTheme === 'auto') applyAutoColor();
        else {
            const t = (typeof THEMES !== 'undefined' && THEMES[currentTheme])
                   || (typeof customThemes !== 'undefined' && customThemes.find(x => x.key === currentTheme));
            if (t) applyColors(t.c, t.light ? { on: true, colors: t.cLight } : null);
        }
    } else {
        dot?.classList.replace('online', 'offline');
        if (txt) txt.textContent = d.error || t('vro.status.not_connected', 'Not connected');
        if (txt) txt.style.color = d.error ? 'var(--err)' : 'var(--tx3)';
        if (btn) btn.innerHTML = `<span class="msi" style="font-size:16px;">link</span> ${t('common.connect', 'Connect')}`;
        badge?.classList.replace('online', 'offline');
        if (badgeTop) badgeTop.classList.remove('tb-active');
    }

    const controlCard = document.getElementById('vroControlCard');
    if (controlCard) controlCard.style.display = d.connected ? '' : 'none';

    const showBtn = document.getElementById('vroShowBtn');
    const hideBtn = document.getElementById('vroHideBtn');
    if (showBtn) { showBtn.disabled = !d.connected; showBtn.style.opacity = d.connected ? '1' : '0.4'; }
    if (hideBtn) { hideBtn.disabled = !d.connected; hideBtn.style.opacity = d.connected ? '1' : '0.4'; }

    const visIco = document.getElementById('vroVisIcon');
    const visTxt = document.getElementById('vroVisText');
    if (visIco) visIco.textContent = d.visible ? 'visibility_off' : 'visibility';
    if (visTxt) visTxt.textContent = d.visible
        ? t('vro.control.hide_overlay', 'Hide Overlay')
        : t('vro.control.show_overlay', 'Show Overlay');

    updateModePill();
    updateKeybindDisplay();
    updateRecordingUI();

    const leftEl  = document.getElementById('vroCtrlL');
    const rightEl = document.getElementById('vroCtrlR');
    if (leftEl)  leftEl.classList.toggle('detected', !!d.leftController);
    if (rightEl) rightEl.classList.toggle('detected', !!d.rightController);
}

function handleVroKeybindRecorded(d) {
    const ids  = d.ids  || [];
    const hand = d.hand ?? 0;
    const mode = d.mode ?? 0;

    if (mode === 0) { vroComboIds = ids; vroComboHand = hand; }
    else            { vroDtIds    = ids; vroDtHand    = hand; }

    // Switch active mode to match what was just recorded
    vroKeybindMode = mode;
    vroRecording   = false;
    updateModePill();
    updateKeybindDisplay();
    updateRecordingUI();
    vroSendConfig();
}

// Connect / disconnect.

function vroConnect() {
    if (vroConnected) {
        sendToCS({ action: 'vroDisconnect' });
    } else {
        let colors = null;
        if (currentSpecialTheme === 'auto') {
            const s = getComputedStyle(document.documentElement);
            const keys = ['bg-card','bg-hover','accent','ok','warn','err','cyan','tx1','tx2','tx3','brd'];
            colors = {};
            for (const k of keys) {
                const v = s.getPropertyValue('--' + k).trim();
                if (v) colors[k] = v;
            }
        } else {
            const t = (typeof THEMES !== 'undefined' && THEMES[currentTheme])
                   || (typeof customThemes !== 'undefined' && customThemes.find(x => x.key === currentTheme));
            if (t) {
                colors = { ...t.c };
                if (t.light && t.cLight) for (const k in t.cLight) colors[k] = t.cLight[k];
            }
        }
        sendToCS({ action: 'vroConnect', themeColors: colors || null });
        vroSendConfig();
    }
}

// Show / hide overlay.

function vroToggleVisibility() {
    if (!vroConnected) return;
    sendToCS({ action: vroVisible ? 'vroHide' : 'vroShow' });
}

// Config.

function vroSendConfig() {
    const attachLeft = document.getElementById('vroAttachLeft')?.value === 'left';

    sendToCS({
        action:        'vroConfig',
        attachLeft,
        attachHand:    true,
        posX:          parseFloat(document.getElementById('vroPosX')?.value)  || -0.10,
        posY:          parseFloat(document.getElementById('vroPosY')?.value)  || -0.03,
        posZ:          parseFloat(document.getElementById('vroPosZ')?.value)  || 0.11,
        rotX:          parseFloat(document.getElementById('vroRotX')?.value)  || -180,
        rotY:          parseFloat(document.getElementById('vroRotY')?.value)  || 46,
        rotZ:          parseFloat(document.getElementById('vroRotZ')?.value)  || 85,
        width:         parseFloat(document.getElementById('vroWidth')?.value) || 0.16,
        keybind:        vroComboIds,
        keybindHand:    vroComboHand,
        keybindDt:      vroDtIds,
        keybindDtHand:  vroDtHand,
        inputMode:      vriLoaded ? vrInputMode : -1,
        keybindMode:    vroKeybindMode,
        controlRadius:  parseFloat(document.getElementById('vroControlRadius')?.value) || 28,
        dynVis:         !!document.getElementById('vroDynVis')?.checked,
        focusRadius:    parseInt(document.getElementById('vroFocusRadius')?.value) || 35,
        seamless:       !!document.getElementById('vroSeamless')?.checked,
    });
}

function vroAutoSave() {
    vroSendConfig();
    clearTimeout(_vroAutoTimer);
    _vroAutoTimer = setTimeout(() => saveSettings(), 600);
}

// Called when the Hand dropdown changes — mirrors transform then saves
function vroMirrorAndSave() {
    const handEl = document.getElementById('vroAttachLeft');
    const isLeft = handEl?.value === 'left';

    if (_vroPrevHand !== null && _vroPrevHand !== isLeft) {
        const posX = document.getElementById('vroPosX');
        const rotY = document.getElementById('vroRotY');
        const rotZ = document.getElementById('vroRotZ');
        if (posX) { posX.value = String(-parseFloat(posX.value)); vroUpdateTransformLabel('vroPosX'); }
        if (rotY) { rotY.value = String(-parseFloat(rotY.value)); vroUpdateTransformLabel('vroRotY'); }
        if (rotZ) { rotZ.value = String(-parseFloat(rotZ.value)); vroUpdateTransformLabel('vroRotZ'); }
    }
    _vroPrevHand = isLeft;
    vroAutoSave();
}

function vroAutoSaveSettings() {
    sendToCS({
        action: 'vroAutoSave',
        autoStart:   false, // legacy
        autoStartVR: !!document.getElementById('setVroAutoStartVR')?.checked,
    });
    clearTimeout(_vroAutoTimer);
    _vroAutoTimer = setTimeout(() => saveSettings(), 600);
}

// Mode pill.

function vroSetMode(mode) {
    if (mode === vroKeybindMode) return; // already active — don't clear anything
    vroKeybindMode = mode;
    updateModePill();
    updateKeybindDisplay();
    updateRecordingUI();
    vroSendConfig();
}

function updateModePill() {
    const comboBtn  = document.getElementById('vroModeCombo');
    const dtBtn     = document.getElementById('vroModeDoubleTap');
    if (!comboBtn || !dtBtn) return;
    comboBtn.classList.toggle('active', vroKeybindMode === 0);
    dtBtn.classList.toggle('active',    vroKeybindMode === 1);
}

// Keybind recording.

let _vroManualIds  = [];
let _vroManualHand = 0;
let _vroManualEditing = false;

function vroStartRecording() {
    if (!vroConnected) return;
    vroRecording = true;
    updateRecordingUI();
    sendToCS({ action: 'vroRecordKeybind' });
}

function vroCancelRecording() {
    vroRecording = false;
    updateRecordingUI();
    sendToCS({ action: 'vroCancelRecording' });
}

// Manual edit (no C# interaction, pure JS).

function vroStartManualEdit() {
    _vroManualEditing = true;
    // Pre-fill with current keybind so user can tweak it
    _vroManualIds  = [...vroActiveIds()];
    _vroManualHand = vroActiveHand();
    _vroSetManualHandUI(_vroManualHand);
    // Wire clicks
    const visual = document.getElementById('vroControllerVisual');
    visual?.classList.add('editing');
    visual?.querySelectorAll('.vro-btn').forEach(el => {
        el.onclick = () => vriZoneClick(el, 'data-btn-id', _vroManualIds, vroToggleManualBtn);
    });
    _vroRenderManualToggle();
    updateRecordingUI();
}

function vroStopManualEdit() {
    _vroManualEditing = false;
    _vroManualIds = [];
    // Unwire clicks
    const visual = document.getElementById('vroControllerVisual');
    visual?.classList.remove('editing');
    visual?.querySelectorAll('.vro-btn').forEach(el => { el.onclick = null; });
    updateRecordingUI();
    updateKeybindDisplay(); // restore display to current saved keybind
}

function _vroRenderManualToggle() {
    document.getElementById('vroControllerVisual')?.querySelectorAll('.vro-btn').forEach(el => {
        vriMarkBtn(el, _vroManualIds, 'data-btn-id', _vroSideActive(_vroManualHand, el.dataset.side));
    });
}

function vroToggleManualBtn(id) {
    if (!_vroManualEditing) return;
    const i = _vroManualIds.indexOf(id);
    // Double-tap mode: only 1 button allowed
    const max = vroKeybindMode === 1 ? 1 : 4;
    if (i >= 0) _vroManualIds.splice(i, 1);
    else {
        _vroManualIds = vriDropZoneSiblings(_vroManualIds, id);
        if (_vroManualIds.length >= max) _vroManualIds = [id];
        else _vroManualIds.push(id);
    }
    _vroRenderManualToggle();
}

function vroSetManualHand(hand) {
    _vroManualHand = hand;
    _vroSetManualHandUI(hand);
    if (_vroManualEditing) _vroRenderManualToggle();
}

function _vroSetManualHandUI(hand) {
    ['vroHandAny','vroHandLeft','vroHandRight'].forEach((id, i) => {
        document.getElementById(id)?.classList.toggle('active', i === hand);
    });
}

function vroConfirmManual() {
    if (_vroManualIds.length === 0) return;
    if (vroKeybindMode === 0) { vroComboIds = [..._vroManualIds]; vroComboHand = _vroManualHand; }
    else                      { vroDtIds    = [..._vroManualIds]; vroDtHand    = _vroManualHand; }
    vroStopManualEdit();
    updateKeybindDisplay();
    vroSendConfig();
}

function vroClearKeybind() {
    if (vroKeybindMode === 0) { vroComboIds = []; vroComboHand = 0; }
    else                      { vroDtIds    = []; vroDtHand    = 0; }
    updateKeybindDisplay();
    vroSendConfig();
}

// Transform value display.

function vroUpdateTransformLabel(id) {
    const input = document.getElementById(id);
    const label = document.getElementById(id + 'Val');
    if (!input || !label) return;
    label.textContent = parseFloat(input.value).toFixed(2);
}

// Toast notification settings.

let _vroToastTimer = null;

function vroToastUpdateLabel(id) {
    const input = document.getElementById(id);
    const label = document.getElementById(id + 'Val');
    if (!input || !label) return;
    if (id === 'vroToastSize') label.textContent = input.value + '%';
    else if (id === 'vroToastDuration') label.textContent = input.value + 's';
    else if (id === 'vroToastStack') label.textContent = input.value;
    else label.textContent = parseFloat(input.value).toFixed(2);
}

let _vroTtsVoices = [];

let _vroTtsWanted = null;

function vroTtsEngineChanged() {
    const eng = document.getElementById('vroTtsEngine')?.value || 'sapi';
    const voiceSel = document.getElementById('vroTtsVoice');
    if (voiceSel) {
        voiceSel.innerHTML = `<option value="">${esc(t('tts.loading', 'Loading voices...'))}</option>`;
        if (voiceSel._vnRefresh) voiceSel._vnRefresh();
    }
    const devSel = document.getElementById('vroTtsDevice');
    _vroTtsWanted = {
        device: audioSelectionFromSelect(devSel),
        engine: eng,
        voice: '',
        lang: '',
        gender: '',
    };
    sendToCS({ action: 'getTtsDevices', target: 'vro', engine: eng });
    vroToastAutoSave();
}

function vroTtsPreviewVoice(voice) {
    if (!voice) return;
    const dev = audioDeviceValue('vroTtsDevice') || { id: '', name: '' };
    sendToCS({
        action: 'ttsPreview',
        text: t('tts.preview_line', 'This is how this voice sounds.'),
        engine: document.getElementById('vroTtsEngine')?.value || 'sapi',
        voice,
        deviceId: dev.id,
        deviceName: dev.name,
        rate: 0,
    });
}

function vroTtsTest() {
    const dev = audioDeviceValue('vroTtsDevice') || { id: '', name: '' };
    sendToCS({
        action: 'ttsTest',
        text: t('vro.notifications.tts_test_line', 'VRCNext text to speech is working.'),
        engine: document.getElementById('vroTtsEngine')?.value || 'sapi',
        voice: document.getElementById('vroTtsVoice')?.value || '',
        deviceId: dev.id,
        deviceName: dev.name,
        rate: 0,
        volume: 100,
    });
}

function vroPopulateTtsDevices(p) {
    if (p.target && p.target !== 'vro') {
        if (typeof kxdTtsHandleDevices === 'function') kxdTtsHandleDevices(p);
        return;
    }

    // Snapshot taken when the request was sent — the reply is async and the global
    // settings object may not be populated yet on a cold start.
    const want = _vroTtsWanted || {};
    const devSel = document.getElementById('vroTtsDevice');
    const wantDevice = want.device
        ?? (devSel?.dataset.audioReady === '1'
            ? audioSelectionFromSelect(devSel)
            : audioSelectionFromSaved(settings?.VroTtsDeviceId ?? settings?.vroTtsDeviceId ?? '', settings?.VroTtsDeviceName ?? settings?.vroTtsDeviceName ?? ''));
    const wantVoice  = want.voice  ?? settings?.vroTtsVoice  ?? '';
    const wantEngine = want.engine ?? settings?.vroTtsEngine ?? 'sapi';
    const wantLang   = want.lang   ?? settings?.vroTtsLang   ?? '';
    const wantGender = want.gender ?? settings?.vroTtsGender ?? '';

    if (devSel && p.devices) {
        audioFillDeviceSelect(devSel, p.devices, wantDevice);
    }
    const engSel = document.getElementById('vroTtsEngine');
    if (engSel && !p.engine) {
        engSel.value = wantEngine || 'sapi';
        if (engSel._vnRefresh) engSel._vnRefresh();
    }

    const langSel = document.getElementById('vroTtsLang');
    const genSel  = document.getElementById('vroTtsGender');
    if (langSel && wantLang) langSel.dataset.pick = wantLang;
    if (genSel  && wantGender) genSel.dataset.pick = wantGender;

    _vroTtsVoices = p.voices || [];
    ttsBuildCascade('vro', _vroTtsVoices, wantVoice,
                    t('vro.notifications.tts_voice_default', 'Default voice'));
    _vroTtsWanted = null;
}

function vroTtsFilterChanged() {
    const lang = document.getElementById('vroTtsLang');
    const gen  = document.getElementById('vroTtsGender');
    if (lang) lang.dataset.pick = lang.value;
    if (gen)  gen.dataset.pick  = gen.value;
    ttsBuildCascade('vro', _vroTtsVoices, '', t('vro.notifications.tts_voice_default', 'Default voice'));
    vroToastAutoSave();
    saveSettings();
}

function vroToastAutoSave() {
    vroToastSendConfig();
    clearTimeout(_vroToastTimer);
    _vroToastTimer = setTimeout(() => saveSettings(), 600);
}

function vroToastSendConfig() {
    const cfg = {
        action:      'vroToastConfig',
        enabled:     !!document.getElementById('vroToastEnabled')?.checked,
        favOnly:     !!document.getElementById('vroToastFavOnly')?.checked,
        size:        parseInt(document.getElementById('vroToastSize')?.value) || 50,
        offsetX:     parseFloat(document.getElementById('vroToastOffsetX')?.value) || 0,
        offsetY:     parseFloat(document.getElementById('vroToastOffsetY')?.value) || -0.12,
        online:      !!document.getElementById('vroToastOnline')?.checked,
        offline:     !!document.getElementById('vroToastOffline')?.checked,
        gps:         !!document.getElementById('vroToastGps')?.checked,
        status:      !!document.getElementById('vroToastStatus')?.checked,
        statusDesc:  !!document.getElementById('vroToastStatusDesc')?.checked,
        bio:         !!document.getElementById('vroToastBio')?.checked,
        duration:    parseInt(document.getElementById('vroToastDuration')?.value) || 8,
        stack:       parseInt(document.getElementById('vroToastStack')?.value) || 2,
        friendReq:   !!document.getElementById('vroToastFriendReq')?.checked,
        invite:      !!document.getElementById('vroToastInvite')?.checked,
        groupInv:    !!document.getElementById('vroToastGroupInv')?.checked,
        joined:      !!document.getElementById('vroToastJoined')?.checked,
        ttsJoined:   !!document.getElementById('vroToastJoinedTts')?.checked,
        ttsOnline: !!document.getElementById('vroToastOnlineTts')?.checked,
        ttsOffline: !!document.getElementById('vroToastOfflineTts')?.checked,
        ttsGps: !!document.getElementById('vroToastGpsTts')?.checked,
        ttsStatus: !!document.getElementById('vroToastStatusTts')?.checked,
        ttsStatusDesc: !!document.getElementById('vroToastStatusDescTts')?.checked,
        ttsBio: !!document.getElementById('vroToastBioTts')?.checked,
        ttsFriendReq: !!document.getElementById('vroToastFriendReqTts')?.checked,
        ttsInvite: !!document.getElementById('vroToastInviteTts')?.checked,
        ttsGroupInv: !!document.getElementById('vroToastGroupInvTts')?.checked,
        ttsVoice:    document.getElementById('vroTtsVoice')?.value || '',
        ttsEngine:   document.getElementById('vroTtsEngine')?.value || 'sapi',
    };
    const ttsDev = audioDeviceValue('vroTtsDevice');
    if (ttsDev) { cfg.ttsDeviceId = ttsDev.id; cfg.ttsDeviceName = ttsDev.name; }
    sendToCS(cfg);
}

// Water reminder settings.

let _vroWaterTimer = null;

function vroWaterAutoSave() {
    sendToCS({
        action:   'vroWaterConfig',
        enabled:  !!document.getElementById('vroWaterEnabled')?.checked,
        hours:    parseInt(document.getElementById('vroWaterHours')?.value   ?? '1', 10),
        minutes:  parseInt(document.getElementById('vroWaterMinutes')?.value ?? '0', 10),
    });
    clearTimeout(_vroWaterTimer);
    _vroWaterTimer = setTimeout(() => saveSettings(), 600);
}

// Avatar Scale keybind (VR overlay Size tab).

function vroScaleUpdateSensitivityLabel() {
    const lbl = document.getElementById('vroScaleSensitivityVal');
    const val = document.getElementById('vroScaleSensitivity')?.value ?? 25;
    if (lbl) lbl.textContent = val + '%';
}

function vroScaleSendConfig() {
    sendToCS({
        action:              'vroScaleConfig',
        enabled:             !!document.getElementById('vroScaleEnabled')?.checked,
        leftThumb:           !!document.getElementById('vroScaleLeftThumb')?.checked,
        rightThumb:          !!document.getElementById('vroScaleRightThumb')?.checked,
        keybind:             _vroScaleKeybindIds,
        keybindHand:         _vroScaleKeybindHand,
        inputMode:           vriLoaded ? vrInputMode : -1,
        scrollSensitivity:   parseInt(document.getElementById('vroScaleSensitivity')?.value) || 25,
    });
}

function vroScaleAutoSave() {
    const nowEnabled = !!document.getElementById('vroScaleEnabled')?.checked;
    if (nowEnabled !== _vroScaleWasEnabled) {
        _vroScaleWasEnabled = nowEnabled;
        if (nowEnabled && !_asConnected) {
            sendToCS({ action: 'asConnect' });
            _asAutoStartedByVro = true;
        } else if (!nowEnabled && _asConnected && _asAutoStartedByVro) {
            sendToCS({ action: 'asDisconnect' });
            _asAutoStartedByVro = false;
        }
    }
    vroScaleSendConfig();
    clearTimeout(_vroScaleAutoTimer);
    _vroScaleAutoTimer = setTimeout(() => saveSettings(), 600);
}

function vroScaleStartRecord() {
    if (!vroConnected) return;
    _vroScaleRecording = true;
    updateScaleRecordingUI();
    sendToCS({ action: 'vroRecordScaleKeybind' });
}

function vroScaleCancelRecord() {
    _vroScaleRecording = false;
    updateScaleRecordingUI();
    sendToCS({ action: 'vroCancelScaleRecording' });
}

function vroScaleStartManual() {
    _vroScaleManualEditing = true;
    _vroScaleManualIds = [..._vroScaleKeybindIds];
    const visual = document.getElementById('vroScaleControllerVisual');
    visual?.classList.add('editing');
    visual?.querySelectorAll('.vro-btn').forEach(el => {
        el.onclick = () => vriZoneClick(el, 'data-scale-btn-id', _vroScaleManualIds, vroScaleToggleManualBtn);
    });
    _vroScaleRenderManual();
    updateScaleRecordingUI();
}

function vroScaleStopManual() {
    _vroScaleManualEditing = false;
    _vroScaleManualIds = [];
    const visual = document.getElementById('vroScaleControllerVisual');
    visual?.classList.remove('editing');
    visual?.querySelectorAll('.vro-btn').forEach(el => {
        el.onclick = null;
        el.classList.remove('active');
    });
    updateScaleRecordingUI();
    updateScaleKeybindDisplay();
}

function _vroScaleRenderManual() {
    document.getElementById('vroScaleControllerVisual')?.querySelectorAll('.vro-btn').forEach(el => {
        vriMarkBtn(el, _vroScaleManualIds, 'data-scale-btn-id', _vroSideActive(_vroScaleKeybindHand, el.dataset.side));
    });
}

function vroScaleToggleManualBtn(id) {
    if (!_vroScaleManualEditing) return;
    const i = _vroScaleManualIds.indexOf(id);
    if (i >= 0) _vroScaleManualIds.splice(i, 1);
    else {
        _vroScaleManualIds = vriDropZoneSiblings(_vroScaleManualIds, id);
        if (_vroScaleManualIds.length >= 4) _vroScaleManualIds = [id];
        else _vroScaleManualIds.push(id);
    }
    _vroScaleRenderManual();
}

function vroScaleSetHand(hand) {
    _vroScaleKeybindHand = hand;
    _vroScaleSetManualHandUI(hand);
    if (_vroScaleManualEditing) _vroScaleRenderManual();
    else vroScaleAutoSave();
}

function _vroScaleSetManualHandUI(hand) {
    ['vroScaleHandAny', 'vroScaleHandLeft', 'vroScaleHandRight'].forEach((id, i) => {
        document.getElementById(id)?.classList.toggle('active', i === hand);
    });
}

function vroScaleConfirmManual() {
    if (_vroScaleManualIds.length === 0) return;
    _vroScaleKeybindIds = [..._vroScaleManualIds];
    vroScaleStopManual();
    updateScaleKeybindDisplay();
    vroScaleSendConfig();
}

function vroScaleClearKeybind() {
    _vroScaleKeybindIds  = [];
    _vroScaleKeybindHand = 0;
    _vroScaleSetManualHandUI(0);
    updateScaleKeybindDisplay();
    vroScaleAutoSave();
}

function updateScaleKeybindDisplay() {
    const display = document.getElementById('vroScaleKeybindDisplay');
    const visual  = document.getElementById('vroScaleControllerVisual');

    if (!display) return;

    if (_vroScaleKeybindIds.length === 0) {
        display.innerHTML = `<span style="color:var(--tx3);font-style:italic;">${t('vro.keybind.none', 'No keybind set')}</span>`;
    } else {
        const names = vroGetNames(_vroScaleKeybindIds);
        const sideLabel = _vroScaleKeybindHand === 1
            ? '<span class="vro-keybind-chip" style="background:var(--accent20);color:var(--accent);">L</span>'
            : _vroScaleKeybindHand === 2
                ? '<span class="vro-keybind-chip" style="background:var(--accent20);color:var(--accent);">R</span>'
                : '';
        const chips = names
            .map(name => `<span class="vro-keybind-chip">${name}</span>`)
            .join('<span class="vro-keybind-plus">+</span>');
        const sep = sideLabel ? '<span class="vro-keybind-plus">/</span>' : '';
        display.innerHTML = sideLabel + sep + chips;
    }

    if (!visual) return;
    visual.querySelectorAll('.vro-btn').forEach(el => {
        vriMarkBtn(el, _vroScaleKeybindIds, 'data-scale-btn-id', _vroSideActive(_vroScaleKeybindHand, el.dataset.side));
    });
}

function updateScaleRecordingUI() {
    const idleBtns    = document.getElementById('vroScaleIdleButtons');
    const recordBtns  = document.getElementById('vroScaleRecordingButtons');
    const manualPanel = document.getElementById('vroScaleManualPanel');
    const hint        = document.getElementById('vroScaleRecordHint');

    if (_vroScaleManualEditing) {
        if (idleBtns)    idleBtns.style.display    = 'none';
        if (recordBtns)  recordBtns.style.display   = 'none';
        if (manualPanel) manualPanel.style.display   = 'block';
        if (hint)        hint.textContent = '';
        return;
    }

    if (_vroScaleRecording) {
        if (idleBtns)    idleBtns.style.display    = 'none';
        if (recordBtns)  recordBtns.style.display   = 'flex';
        if (manualPanel) manualPanel.style.display   = 'none';
        if (hint) {
            hint.textContent = t('vro.scale.hint.record', 'Hold 1–4 buttons simultaneously to record a combo…');
            hint.style.color = 'var(--warn)';
        }
        return;
    }

    if (idleBtns)    idleBtns.style.display    = 'flex';
    if (recordBtns)  recordBtns.style.display   = 'none';
    if (manualPanel) manualPanel.style.display   = 'none';
    if (hint) {
        hint.textContent = t('vro.scale.hint.idle', 'Hold keybind + move thumbstick up/down to scale.');
        hint.style.color = 'var(--tx3)';
    }
}

function handleVroScaleKeybindRecorded(d) {
    _vroScaleKeybindIds  = d.ids  || [];
    _vroScaleKeybindHand = d.hand ?? 0;
    _vroScaleRecording   = false;
    _vroScaleSetManualHandUI(_vroScaleKeybindHand);
    updateScaleKeybindDisplay();
    updateScaleRecordingUI();
    vroScaleSendConfig();
}

function updateRecordingUI() {
    const idleBtns     = document.getElementById('vroIdleButtons');
    const recordBtns   = document.getElementById('vroRecordingButtons');
    const manualPanel  = document.getElementById('vroManualEditPanel');
    const hint         = document.getElementById('vroRecordHint');

    if (_vroManualEditing) {
        if (idleBtns)    idleBtns.style.display   = 'none';
        if (recordBtns)  recordBtns.style.display  = 'none';
        if (manualPanel) manualPanel.style.display  = 'block';
        if (hint) { hint.textContent = ''; }
        return;
    }

    if (vroRecording) {
        if (idleBtns)    idleBtns.style.display   = 'none';
        if (recordBtns)  recordBtns.style.display  = 'flex';
        if (manualPanel) manualPanel.style.display  = 'none';
        if (hint) {
            hint.textContent = vroKeybindMode === 1
                ? t('vro.keybind.hint.record_double_tap', 'Press one button and hold to record for Double Tap...')
                : t('vro.keybind.hint.record_combo', 'Hold 1-4 buttons simultaneously to record a combo...');
            hint.style.color = 'var(--warn)';
        }
        return;
    }

    // idle state
    if (idleBtns)    idleBtns.style.display   = 'flex';
    if (recordBtns)  recordBtns.style.display  = 'none';
    if (manualPanel) manualPanel.style.display  = 'none';
    if (hint) {
        hint.textContent = vroKeybindMode === 1
            ? t('vro.keybind.hint.idle_double_tap', 'Double-tap a single button to toggle the overlay.')
            : t('vro.keybind.hint.idle_combo', 'Hold 1-4 buttons on one controller to toggle the overlay.');
        hint.style.color = 'var(--tx3)';
    }
}

function updateKeybindDisplay() {
    const display = document.getElementById('vroKeybindDisplay');
    const visual = document.getElementById('vroControllerVisual');
    const ids = vroActiveIds();
    const hand = vroActiveHand();

    if (!display) return;

    if (ids.length === 0) {
        display.innerHTML = `<span style="color:var(--tx3);font-style:italic;">${t('vro.keybind.none', 'No keybind set')}</span>`;
    } else {
        const names = vroGetNames(ids);
        const sideLabel = hand === 1
            ? '<span class="vro-keybind-chip" style="background:var(--accent20);color:var(--accent);">L</span>'
            : hand === 2
                ? '<span class="vro-keybind-chip" style="background:var(--accent20);color:var(--accent);">R</span>'
                : '';
        const modeChip = vroKeybindMode === 1
            ? '<span class="vro-keybind-chip" style="background:color-mix(in srgb,var(--cyan) 20%,transparent);color:var(--cyan);border-color:var(--cyan);">x2</span>'
            : '';
        const chips = names
            .map(name => `<span class="vro-keybind-chip">${name}</span>`)
            .join('<span class="vro-keybind-plus">+</span>');
        const sep = (sideLabel || modeChip) ? '<span class="vro-keybind-plus">/</span>' : '';
        display.innerHTML = modeChip + sideLabel + sep + chips;
    }

    if (!visual) return;
    visual.querySelectorAll('.vro-btn').forEach(el => {
        vriMarkBtn(el, ids, 'data-btn-id', _vroSideActive(hand, el.dataset.side));
    });
}

function vroUpdateControlRadius() {
    const input = document.getElementById('vroControlRadius');
    const label = document.getElementById('vroControlRadiusVal');
    if (input && label) label.textContent = vroRadiusLabelText(input.value);
    vroAutoSave();
}

function vroUpdateFocusRadius() {
    const input = document.getElementById('vroFocusRadius');
    const label = document.getElementById('vroFocusRadiusVal');
    if (input && label) label.textContent = vroFocusRadiusLabelText(input.value);
    vroAutoSave();
}

function vroResetTransform() {
    const defaults = {
        vroPosX: -0.10,
        vroPosY: -0.03,
        vroPosZ: 0.11,
        vroRotX: -180,
        vroRotY: 46,
        vroRotZ: 85,
        vroWidth: 0.16
    };

    for (const [id, val] of Object.entries(defaults)) {
        const el = document.getElementById(id);
        if (el) {
            el.value = val;
            vroUpdateTransformLabel(id);
        }
    }

    const radiusInput = document.getElementById('vroControlRadius');
    const radiusLabel = document.getElementById('vroControlRadiusVal');
    if (radiusInput) radiusInput.value = 16;
    if (radiusLabel) radiusLabel.textContent = vroRadiusLabelText(16);
    vroAutoSave();
}

function vroLoadSettings(s) {
    if (!s) return;

    const attachLeftEl = document.getElementById('vroAttachLeft');
    if (attachLeftEl) attachLeftEl.value = s.vroAttachLeft ? 'left' : 'right';

    const ids = ['vroPosX', 'vroPosY', 'vroPosZ', 'vroRotX', 'vroRotY', 'vroRotZ', 'vroWidth'];
    ids.forEach(id => {
        const el = document.getElementById(id);
        if (el && s[id] !== undefined) {
            el.value = s[id];
            vroUpdateTransformLabel(id);
        }
    });

    const autoVrEl = document.getElementById('setVroAutoStartVR');
    if (autoVrEl) autoVrEl.checked = !!(s.vroAutoStartVR ?? s.autoStartVR ?? false);

    const _vroIdx = (s.vrInputMode ?? 0) === 1;
    vroComboIds  = (_vroIdx ? s.vroIdxKeybind       : s.vroKeybind)       || [];
    vroComboHand = (_vroIdx ? s.vroIdxKeybindHand   : s.vroKeybindHand)   ?? 0;
    vroDtIds     = (_vroIdx ? s.vroIdxKeybindDt     : s.vroKeybindDt)     || [];
    vroDtHand    = (_vroIdx ? s.vroIdxKeybindDtHand : s.vroKeybindDtHand) ?? 0;
    _vroKbOther  = {
        combo:     (_vroIdx ? s.vroKeybind       : s.vroIdxKeybind)       || [],
        comboHand: (_vroIdx ? s.vroKeybindHand   : s.vroIdxKeybindHand)   ?? 0,
        dt:        (_vroIdx ? s.vroKeybindDt     : s.vroIdxKeybindDt)     || [],
        dtHand:    (_vroIdx ? s.vroKeybindDtHand : s.vroIdxKeybindDtHand) ?? 0,
    };
    vroKeybindMode = s.vroKeybindMode ?? 0;

    const radiusInput = document.getElementById('vroControlRadius');
    const radiusLabel = document.getElementById('vroControlRadiusVal');
    const radiusValue = s.vroControlRadius ?? 28;
    if (radiusInput) radiusInput.value = radiusValue;
    if (radiusLabel) radiusLabel.textContent = vroRadiusLabelText(radiusValue);

    const dynVisEl = document.getElementById('vroDynVis');
    if (dynVisEl) dynVisEl.checked = !!(s.vroDynVis ?? false);

    const seamlessEl = document.getElementById('vroSeamless');
    if (seamlessEl) seamlessEl.checked = !!(s.vroSeamless ?? false);

    const focusInput = document.getElementById('vroFocusRadius');
    const focusLabel = document.getElementById('vroFocusRadiusVal');
    const focusValue = s.vroFocusRadius ?? 35;
    if (focusInput) focusInput.value = focusValue;
    if (focusLabel) focusLabel.textContent = vroFocusRadiusLabelText(focusValue);

    updateModePill();
    updateKeybindDisplay();
    updateRecordingUI();

    _vroPrevHand = s.vroAttachLeft ? true : false;

    const toastValues = {
        vroToastEnabled: s.vroToastEnabled ?? true,
        vroToastFavOnly: s.vroToastFavOnly ?? false,
        vroToastOnline: s.vroToastOnline ?? true,
        vroToastOffline: s.vroToastOffline ?? true,
        vroToastGps: s.vroToastGps ?? true,
        vroToastStatus: s.vroToastStatus ?? true,
        vroToastStatusDesc: s.vroToastStatusDesc ?? true,
        vroToastBio: s.vroToastBio ?? true,
        vroToastFriendReq: s.vroToastFriendReq ?? true,
        vroToastInvite: s.vroToastInvite ?? true,
        vroToastGroupInv: s.vroToastGroupInv ?? true,
        vroToastJoined: s.vroToastJoined ?? true,
        vroToastJoinedTts: s.vroToastTtsJoined ?? false,
        vroToastOnlineTts: s.vroToastTtsOnline ?? false,
        vroToastOfflineTts: s.vroToastTtsOffline ?? false,
        vroToastGpsTts: s.vroToastTtsGps ?? false,
        vroToastStatusTts: s.vroToastTtsStatus ?? false,
        vroToastStatusDescTts: s.vroToastTtsStatusDesc ?? false,
        vroToastBioTts: s.vroToastTtsBio ?? false,
        vroToastFriendReqTts: s.vroToastTtsFriendReq ?? false,
        vroToastInviteTts: s.vroToastTtsInvite ?? false,
        vroToastGroupInvTts: s.vroToastTtsGroupInv ?? false
    };

    for (const [id, val] of Object.entries(toastValues)) {
        const el = document.getElementById(id);
        if (el) el.checked = val;
    }

    const vroEngEl = document.getElementById('vroTtsEngine');
    if (vroEngEl) { vroEngEl.value = s.vroTtsEngine || 'sapi'; if (vroEngEl._vnRefresh) vroEngEl._vnRefresh(); }
    const vroTtsSaved = audioSelectionFromSaved(s.vroTtsDeviceId || '', s.vroTtsDeviceName || '');
    audioPrefillDeviceSelect(document.getElementById('vroTtsDevice'), vroTtsSaved);
    _vroTtsWanted = {
        device: vroTtsSaved,
        engine: s.vroTtsEngine || 'sapi',
        voice: s.vroTtsVoice || '',
        lang: s.vroTtsLang || '',
        gender: s.vroTtsGender || '',
    };
    sendToCS({ action: 'getTtsDevices', target: 'vro', engine: s.vroTtsEngine || 'sapi' });

    const toastSizeEl = document.getElementById('vroToastSize');
    if (toastSizeEl) {
        toastSizeEl.value = s.vroToastSize ?? 50;
        vroToastUpdateLabel('vroToastSize');
    }

    const toastOffsetXEl = document.getElementById('vroToastOffsetX');
    if (toastOffsetXEl) {
        toastOffsetXEl.value = s.vroToastOffsetX ?? 0;
        vroToastUpdateLabel('vroToastOffsetX');
    }

    const toastOffsetYEl = document.getElementById('vroToastOffsetY');
    if (toastOffsetYEl) {
        toastOffsetYEl.value = s.vroToastOffsetY ?? -0.12;
        vroToastUpdateLabel('vroToastOffsetY');
    }

    const toastDurationEl = document.getElementById('vroToastDuration');
    if (toastDurationEl) {
        toastDurationEl.value = s.vroToastDuration ?? 8;
        vroToastUpdateLabel('vroToastDuration');
    }

    const toastStackEl = document.getElementById('vroToastStack');
    if (toastStackEl) {
        toastStackEl.value = s.vroToastStack ?? 2;
        vroToastUpdateLabel('vroToastStack');
    }

    const waterEnabledEl = document.getElementById('vroWaterEnabled');
    if (waterEnabledEl) waterEnabledEl.checked = !!(s.vroWaterEnabled ?? false);

    const waterHoursEl = document.getElementById('vroWaterHours');
    if (waterHoursEl) waterHoursEl.value = s.vroWaterHours ?? 1;

    const waterMinutesEl = document.getElementById('vroWaterMinutes');
    if (waterMinutesEl) waterMinutesEl.value = s.vroWaterMinutes ?? 0;

    // Scale keybind settings
    _vroScaleKeybindIds  = ((s.vrInputMode ?? 0) === 1 ? s.vroIdxScaleKeybind     : s.vroScaleKeybind)     || [];
    _vroScaleKeybindHand = ((s.vrInputMode ?? 0) === 1 ? s.vroIdxScaleKeybindHand : s.vroScaleKeybindHand) ?? 0;
    _vroScaleOther = {
        ids:  ((s.vrInputMode ?? 0) === 1 ? s.vroScaleKeybind     : s.vroIdxScaleKeybind)     || [],
        hand: ((s.vrInputMode ?? 0) === 1 ? s.vroScaleKeybindHand : s.vroIdxScaleKeybindHand) ?? 0,
    };

    const scaleEnabledEl    = document.getElementById('vroScaleEnabled');
    if (scaleEnabledEl)    scaleEnabledEl.checked    = !!(s.vroScaleEnabled    ?? true);
    _vroScaleWasEnabled = !!(s.vroScaleEnabled ?? true);
    const scaleLeftThumbEl  = document.getElementById('vroScaleLeftThumb');
    if (scaleLeftThumbEl)  scaleLeftThumbEl.checked   = !!(s.vroScaleLeftThumb  ?? false);
    const scaleRightThumbEl = document.getElementById('vroScaleRightThumb');
    if (scaleRightThumbEl) scaleRightThumbEl.checked  = !!(s.vroScaleRightThumb ?? true);
    const scaleSensEl = document.getElementById('vroScaleSensitivity');
    if (scaleSensEl) { scaleSensEl.value = s.vroScaleScrollSensitivity ?? 25; vroScaleUpdateSensitivityLabel(); }

    _vroScaleSetManualHandUI(_vroScaleKeybindHand);
    updateScaleKeybindDisplay();
    updateScaleRecordingUI();
}

function rerenderVroTranslations() {
    const statusText = document.getElementById('vroStatusText');
    if (statusText) {
        if (vroConnected) {
            statusText.textContent = t('vro.status.connected', 'Connected');
            statusText.style.color = 'var(--ok)';
        } else {
            statusText.textContent = _vroLastError || t('vro.status.not_connected', 'Not connected');
            statusText.style.color = _vroLastError ? 'var(--err)' : 'var(--tx3)';
        }
    }

    const connBtn = document.getElementById('vroConnBtn');
    if (connBtn) {
        connBtn.innerHTML = vroConnected
            ? `<span class="msi" style="font-size:16px;">link_off</span> ${t('common.disconnect', 'Disconnect')}`
            : `<span class="msi" style="font-size:16px;">link</span> ${t('common.connect', 'Connect')}`;
    }

    const visText = document.getElementById('vroVisText');
    if (visText) {
        visText.textContent = vroVisible
            ? t('vro.control.hide_overlay', 'Hide Overlay')
            : t('vro.control.show_overlay', 'Show Overlay');
    }

    const radiusInput = document.getElementById('vroControlRadius');
    const radiusLabel = document.getElementById('vroControlRadiusVal');
    if (radiusInput && radiusLabel) radiusLabel.textContent = vroRadiusLabelText(radiusInput.value);

    const focusInput = document.getElementById('vroFocusRadius');
    const focusLabel = document.getElementById('vroFocusRadiusVal');
    if (focusInput && focusLabel) focusLabel.textContent = vroFocusRadiusLabelText(focusInput.value);

    updateKeybindDisplay();
    updateRecordingUI();
    updateScaleRecordingUI();
}

document.documentElement.addEventListener('languagechange', rerenderVroTranslations);
