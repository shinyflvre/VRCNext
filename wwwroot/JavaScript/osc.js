// OSC Tool - live avatar parameter viewer and editor

function oscConnect() {
    const btn = document.getElementById('oscConnBtn');
    if (oscConnected) {
        sendToCS({ action: 'oscDisconnect' });
    } else {
        if (btn) { btn.disabled = true; btn.innerHTML = '<span class="msi" style="font-size:16px;">hourglass_empty</span> Connecting...'; }
        sendToCS({ action: 'oscConnect' });
    }
}

function handleOscState(data) {
    oscConnected = !!data.connected;
    const dot = document.getElementById('oscDot');
    const txt = document.getElementById('oscStatusText');
    const btn = document.getElementById('oscConnBtn');
    if (dot) dot.className = 'sf-dot ' + (oscConnected ? 'online' : 'offline');
    if (txt) txt.textContent = oscConnected ? 'Connected — toggle OSC in VRChat to load params' : 'Not connected';
    if (btn) {
        btn.disabled = false;
        btn.innerHTML = oscConnected
            ? '<span class="msi" style="font-size:16px;">link_off</span> Disconnect'
            : '<span class="msi" style="font-size:16px;">link</span> Connect';
    }
    if (!oscConnected) {
        oscParams = {};
        renderOscParams('');
        const banner = document.getElementById('oscOutputBanner');
        if (banner) banner.style.display = 'none';
    }
}

// Called when VRChat sends /avatar/change. C# reads config and sends full param list.
function handleOscAvatarParams(data) {
    const { avatarId, paramList } = data;
    oscParams = {};
    // Populate from config, avatar change always starts fresh
    for (const p of (paramList || [])) {
        const defaultVal = p.Type === 'bool' ? false : 0;
        oscParams[p.Name] = { value: defaultVal, type: p.Type, live: false, hasOutput: p.HasOutput };
    }
    const search = (document.getElementById('oscSearch')?.value || '').toLowerCase();
    renderOscParams(search);

    // Show banner if some params have no output configured
    const noOutput = (paramList || []).filter(p => !p.HasOutput).length;
    _updateOutputBanner(noOutput, (paramList || []).length);

    const total = Object.keys(oscParams).length;
    const live = Object.values(oscParams).filter(p => p.live).length;
    const txt = document.getElementById('oscStatusText');
    if (txt && oscConnected) {
        txt.textContent = live > 0
            ? `Connected — ${total} params (${live} live)`
            : `Connected — ${total} params loaded`;
    }
}

function _updateOutputBanner(noOutputCount, total) {
    const banner = document.getElementById('oscOutputBanner');
    if (!banner) return;
    if (noOutputCount > 0) {
        banner.style.display = '';
        banner.querySelector('.osc-banner-text').textContent =
            `${noOutputCount} of ${total} params have no OSC output — VRChat won't send live updates for them.`;
    } else {
        banner.style.display = 'none';
    }
}

function oscEnableOutputs() {
    const btn = document.getElementById('oscEnableBtn');
    if (btn) { btn.disabled = true; btn.textContent = 'Updating...'; }
    sendToCS({ action: 'oscEnableOutputs' });
}

function handleOscOutputsEnabled(data) {
    const btn = document.getElementById('oscEnableBtn');
    const count = data.filesUpdated || 0;
    if (btn) {
        btn.disabled = false;
        btn.innerHTML = '<span class="msi" style="font-size:14px;">check</span> Done';
        setTimeout(() => {
            btn.innerHTML = '<span class="msi" style="font-size:14px;">output</span> Enable All Outputs';
            btn.disabled = false;
        }, 3000);
    }
    const banner = document.getElementById('oscOutputBanner');
    if (banner) {
        banner.querySelector('.osc-banner-text').textContent =
            count > 0
                ? `Updated ${count} config file(s). Reload your avatar in VRChat to receive all params.`
                : 'All parameters already have output enabled.';
    }
}

function handleOscParam(data) {
    const { name, value, type } = data;
    const wasNew = !oscParams[name];
    oscParams[name] = { value, type, live: true, hasOutput: true };

    const search = (document.getElementById('oscSearch')?.value || '').toLowerCase();
    const visible = !search || name.toLowerCase().includes(search);
    if (!visible) { _updateOscParamCount(); return; }

    // Remove placeholder if present
    const empty = document.getElementById('oscEmptyMsg');
    if (empty) empty.remove();

    const row = document.querySelector(`[data-osc-param="${CSS.escape(name)}"]`);
    if (row) {
        // Mark as live if it was pending
        if (row.classList.contains('osc-row-pending')) {
            row.classList.remove('osc-row-pending');
        }
        _updateOscParamRowEl(row, name, type, value);
    } else if (wasNew) {
        _insertOscParamRow(name, type, value, true);
    }
    _updateOscParamCount();
}

function _updateOscParamRowEl(row, name, type, value) {
    const activeEl = document.activeElement;
    if (row.contains(activeEl)) return; // don't clobber while user edits

    if (type === 'bool') {
        const inp = row.querySelector('input[type="checkbox"]');
        if (inp) inp.checked = !!value;
    } else if (type === 'float') {
        const slider = row.querySelector('.osc-float-slider');
        const num = row.querySelector('.osc-float-num');
        const v = parseFloat(value) || 0;
        if (slider) slider.value = v;
        if (num) num.value = v.toFixed(3);
    } else if (type === 'int') {
        const inp = row.querySelector('.osc-int-inp');
        if (inp) inp.value = parseInt(value) || 0;
    }
}

function _insertOscParamRow(name, type, value, live) {
    const list = document.getElementById('oscParamList');
    if (!list) return;
    const html = _oscRowHtml(name, type, value, live);
    const tmp = document.createElement('div');
    tmp.innerHTML = html;
    const newRow = tmp.firstElementChild;
    if (!newRow) return;
    const rows = list.querySelectorAll('[data-osc-param]');
    let inserted = false;
    for (const r of rows) {
        if (r.getAttribute('data-osc-param').localeCompare(name) > 0) {
            list.insertBefore(newRow, r);
            inserted = true;
            break;
        }
    }
    if (!inserted) list.appendChild(newRow);
}

function _oscRowHtml(name, type, value, live) {
    const esc_name = esc(name);
    const jsName = jsq(name);
    const badgeClass = 'osc-badge-' + type;
    const pendingClass = live ? '' : ' osc-row-pending';

    let ctrl = '';
    if (type === 'bool') {
        const checked = value ? 'checked' : '';
        ctrl = `<label class="toggle osc-toggle">
            <input type="checkbox" ${checked} onchange="oscSetParam('${jsName}','bool',this.checked)">
            <div class="toggle-track"><div class="toggle-knob"></div></div>
        </label>`;
    } else if (type === 'float') {
        const v = (parseFloat(value) || 0).toFixed(3);
        ctrl = `<div class="osc-float-wrap">
            <input type="range" class="osc-float-slider" min="-1" max="1" step="0.001" value="${v}"
                oninput="this.closest('.osc-float-wrap').querySelector('.osc-float-num').value=parseFloat(this.value).toFixed(3);oscSetParam('${jsName}','float',parseFloat(this.value))">
            <input type="number" class="osc-float-num" min="-1" max="1" step="0.001" value="${v}"
                oninput="this.closest('.osc-float-wrap').querySelector('.osc-float-slider').value=this.value;oscSetParam('${jsName}','float',parseFloat(this.value))">
        </div>`;
    } else if (type === 'int') {
        const v = parseInt(value) || 0;
        ctrl = `<input type="number" class="osc-int-inp" min="0" max="255" step="1" value="${v}"
            onchange="oscSetParam('${jsName}','int',parseInt(this.value)||0)">`;
    }

    return `<div class="osc-row${pendingClass}" data-osc-param="${esc_name}">
        <div class="osc-row-left">
            <span class="osc-type-badge ${badgeClass}">${type.toUpperCase()}</span>
            <span class="osc-param-name" title="${esc_name}">${esc_name}</span>
        </div>
        <div class="osc-ctrl">${ctrl}</div>
    </div>`;
}

function filterOscParams() {
    const search = (document.getElementById('oscSearch')?.value || '').toLowerCase();
    renderOscParams(search);
}

function renderOscParams(search) {
    const list = document.getElementById('oscParamList');
    if (!list) return;
    list.innerHTML = '';

    const keys = Object.keys(oscParams).sort((a, b) => a.localeCompare(b));
    const filtered = search ? keys.filter(k => k.toLowerCase().includes(search)) : keys;

    if (filtered.length === 0) {
        const msg = keys.length === 0
            ? 'No parameters received yet.<br>Connect and load into VRChat.'
            : 'No parameters match your search.';
        list.innerHTML = `<div class="osc-empty" id="oscEmptyMsg">${msg}</div>`;
    } else {
        const frag = document.createDocumentFragment();
        for (const name of filtered) {
            const { value, type, live } = oscParams[name];
            const tmp = document.createElement('div');
            tmp.innerHTML = _oscRowHtml(name, type, value, live);
            if (tmp.firstElementChild) frag.appendChild(tmp.firstElementChild);
        }
        list.appendChild(frag);
    }
    _updateOscParamCount();
}

function _updateOscParamCount() {
    const total = Object.keys(oscParams).length;
    const live = Object.values(oscParams).filter(p => p.live).length;
    const el = document.getElementById('oscParamCount');
    if (!el) return;
    if (total === 0) { el.textContent = '0 params'; return; }
    el.textContent = live < total ? `${total} params (${live} live)` : `${total} params`;
}

function oscSetParam(name, type, value) {
    if (!oscConnected) return;
    if (type === 'float') value = Math.max(-1, Math.min(1, parseFloat(value) || 0));
    if (type === 'int') value = Math.max(0, Math.min(255, parseInt(value) || 0));
    if (oscParams[name]) oscParams[name].value = value;
    sendToCS({ action: 'oscSend', name, type, value });
}
