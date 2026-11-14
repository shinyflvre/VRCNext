let _swLastState = null;

let _swKeybindMode = 'walk';
const SW_MODE_SELECTS = {
    walk:  { left: 'swLeftWalk',  right: 'swRightWalk'  },
    reset: { left: 'swLeftReset', right: 'swRightReset' },
};

const SW_BTN_IDS = ['swLeftWalk', 'swRightWalk', 'swLeftReset', 'swRightReset'];
const SW_BTN_DEFAULTS = { swLeftWalk: 0, swRightWalk: 2, swLeftReset: 0, swRightReset: 0 };

let _swOtherBtns   = {};
let _swPendingBtns = null;

function swReadBtns() {
    const o = {};
    SW_BTN_IDS.forEach(id => {
        o[id] = parseInt(document.getElementById(id)?.value ?? String(SW_BTN_DEFAULTS[id]), 10) || 0;
    });
    return o;
}

function swWriteBtns(vals) {
    SW_BTN_IDS.forEach(id => {
        const el = document.getElementById(id);
        if (!el) return;
        const v = String(vals?.[id] ?? 0);
        el.value = el.querySelector(`option[value="${v}"]`) ? v : '0';
        if (el._vnRefresh) el._vnRefresh();
    });
}

function swSwapButtonSets() {
    const cur = swReadBtns();
    _swPendingBtns = _swOtherBtns;
    _swOtherBtns   = cur;
}

function swApplyInputMode() {
    if (_swPendingBtns) { swWriteBtns(_swPendingBtns); _swPendingBtns = null; }
    swRenderKeybind();
    swSendConfig();
}

function swSetMode(mode) {
    if (!SW_MODE_SELECTS[mode]) return;
    _swKeybindMode = mode;
    swRenderKeybind();
}

function swRenderKeybind() {
    const pills = { walk: 'swModeWalk', reset: 'swModeReset' };
    for (const [m, id] of Object.entries(pills)) {
        document.getElementById(id)?.classList.toggle('active', m === _swKeybindMode);
    }

    const map = SW_MODE_SELECTS[_swKeybindMode];
    const leftVal  = parseInt(document.getElementById(map.left)?.value  ?? '0', 10);
    const rightVal = parseInt(document.getElementById(map.right)?.value ?? '0', 10);

    document.querySelectorAll('#swControllerVisual .vro-btn').forEach(el => {
        const want = el.dataset.side === 'left' ? leftVal : rightVal;
        vriMarkBtn(el, want !== 0 ? [want] : [], 'data-sw-btn-id', true);
    });
}

function swKeybindClick(el) {
    const side = el.dataset.side;
    const map  = SW_MODE_SELECTS[_swKeybindMode];
    const sel  = document.getElementById(side === 'left' ? map.left : map.right);
    if (!sel) return;

    vriZoneClick(el, 'data-sw-btn-id', [parseInt(sel.value, 10) || 0], id => {
        const cur = parseInt(sel.value, 10);
        sel.value = cur === id ? '0' : String(id);
        if (sel._vnRefresh) sel._vnRefresh();
        swRenderKeybind();
        swAutoSave();
    });
}

let _swLegacyView = false;
function swToggleView(btn) {
    _swLegacyView = !_swLegacyView;
    const vis  = document.getElementById('swControllerVisual');
    const pill = document.getElementById('swKeybindPills');
    const leg  = document.getElementById('swLegacyView');
    if (vis)  vis.style.display  = _swLegacyView ? 'none' : '';
    if (pill) pill.style.display = _swLegacyView ? 'none' : '';
    if (leg)  leg.style.display  = _swLegacyView ? '' : 'none';
    btn?.classList.toggle('active', _swLegacyView);
}

function swConnectBtnHtml() {
    return `<span class="msi" style="font-size:16px;">link</span> ${esc(t('common.connect', 'Connect'))}`;
}

function swDisconnectBtnHtml() {
    return `<span class="msi" style="font-size:16px;">link_off</span> ${esc(t('common.disconnect', 'Disconnect'))}`;
}

function swStatusText(state) {
    if (!state?.connected) return state?.error || t('steamvr.status.not_connected', 'Not connected');
    return state.walking
        ? t('spacewalk.status.walking', 'Rotating...')
        : t('steamvr.status.connected', 'Connected to Space');
}

function swConnect() {
    if (swConnected) {
        sendToCS({ action: 'swDisconnect' });
    } else {
        sendToCS({ action: 'swConnect' });
        swSendConfig();
    }
}

function swReset() {
    sendToCS({ action: 'swReset' });
}

function swSendConfig() {
    sendToCS({
        action: 'swConfig',
        multiplier:    parseFloat(document.getElementById('swMultiplier')?.value) || 1,
        smoothing:     parseFloat(document.getElementById('swSmoothing')?.value) || 0,
        momentum:      parseFloat(document.getElementById('swMomentum')?.value) || 0,
        invert:        !!document.getElementById('swInvert')?.checked,
        lockPitch:     !!document.getElementById('swLockPitch')?.checked,
        lockRoll:      !!document.getElementById('swLockRoll')?.checked,
        lockYaw:       !!document.getElementById('swLockYaw')?.checked,
        leftWalkBtn:   parseInt(document.getElementById('swLeftWalk')?.value   ?? '0', 10),
        rightWalkBtn:  parseInt(document.getElementById('swRightWalk')?.value  ?? '2', 10),
        leftResetBtn:  parseInt(document.getElementById('swLeftReset')?.value  ?? '0', 10),
        rightResetBtn: parseInt(document.getElementById('swRightReset')?.value ?? '0', 10),
    });
}

let _swAutoTimer = null;
function swAutoSave() {
    swSendConfig();
    clearTimeout(_swAutoTimer);
    _swAutoTimer = setTimeout(() => saveSettings(), 600);
}

function handleSwUpdate(data) {
    _swLastState = { ...data };
    swConnected = data.connected;

    const dot = document.getElementById('swDot');
    const txt = document.getElementById('swStatusText');
    const btn = document.getElementById('swConnBtn');
    const badge = document.getElementById('badgeSpaceWalk');
    if (!dot || !txt || !btn) return;

    if (data.connected) {
        dot.classList.remove('offline');
        dot.classList.add('online');
        txt.textContent = swStatusText(data);
        txt.style.color = data.walking ? 'var(--warn)' : 'var(--ok)';
        btn.innerHTML = swDisconnectBtnHtml();
        if (badge) badge.classList.add('tb-active');
    } else {
        dot.classList.remove('online');
        dot.classList.add('offline');
        txt.textContent = swStatusText(data);
        txt.style.color = data.error ? 'var(--err)' : 'var(--tx3)';
        btn.innerHTML = swConnectBtnHtml();
        if (badge) badge.classList.remove('tb-active');
    }

    const deg = v => (v ?? 0).toFixed(1) + '°';
    const pitch = document.getElementById('swPitch');
    const roll  = document.getElementById('swRoll');
    const yaw   = document.getElementById('swYaw');
    if (pitch) pitch.textContent = deg(data.pitch);
    if (roll)  roll.textContent  = deg(data.roll);
    if (yaw)   yaw.textContent   = deg(data.yaw);

    const note = document.getElementById('swTiltNote');
    if (note) note.style.display = data.connected && data.tiltSupported === false ? '' : 'none';

    const lc = document.getElementById('swCtrlL');
    const rc = document.getElementById('swCtrlR');
    if (lc) lc.classList.toggle('detected', !!data.leftController);
    if (rc) rc.classList.toggle('detected', !!data.rightController);
}

function rerenderSpaceWalkTranslations() {
    if (_swLastState) handleSwUpdate(_swLastState);
}

document.documentElement.addEventListener('languagechange', rerenderSpaceWalkTranslations);
