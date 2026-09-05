/* Custom Chatbox OSC */
let _cbLastUpdate = {};
let _cbChatHistory = [];
let _cbPauseTimer = null;
let _cbPauseRemaining = 0;
const CB_MAX_HISTORY = 100;
const CB_PAUSE_SECONDS = 10;
const CB_LINE_IDS = ['time', 'media', 'stats', 'pulse', 'weather', 'window', 'custom'];
let _cbEditLineIndex = -1;
let _cbDragCleanup = null;
let _cbLineDragCleanup = null;

function chatboxButtonHtml() {
    return chatboxEnabled
        ? `<span class="msi" style="font-size:16px;">stop</span> ${t('common.stop', 'Stop')}`
        : `<span class="msi" style="font-size:16px;">play_arrow</span> ${t('common.start', 'Start')}`;
}

function chatboxStatusText() {
    return chatboxEnabled
        ? t('chatbox.status.running', 'Running')
        : t('chatbox.status.not_running', 'Not running');
}

function chatboxPreviewFallback() {
    return chatboxEnabled
        ? t('chatbox.preview.waiting', 'Waiting for data...')
        : t('chatbox.preview.enable_prompt', 'Enable chatbox to see preview');
}

function syncChatboxToggleUi() {
    const btn = document.getElementById('cbConnBtn');
    const dot = document.getElementById('cbDot');
    const txt = document.getElementById('cbStatusText');
    if (btn) btn.innerHTML = chatboxButtonHtml();
    if (dot) dot.className = chatboxEnabled ? 'sf-dot online' : 'sf-dot offline';
    if (txt) txt.textContent = chatboxStatusText();
}

function rerenderChatboxTranslations() {
    syncChatboxToggleUi();
    renderChatboxLines();
    renderChatboxHistory();
    handleChatboxUpdate(_cbLastUpdate || {});
}

document.documentElement.addEventListener('languagechange', rerenderChatboxTranslations);

function toggleChatbox() {
    chatboxEnabled = !chatboxEnabled;
    syncChatboxToggleUi();
    document.getElementById('badgeChatbox').classList.toggle('tb-active', chatboxEnabled);
    updateChatboxConfig();
}

function _cbChecked(id, fallback) {
    const el = document.getElementById(id);
    return el ? el.checked : fallback;
}

function _cbNormalizeLines(list) {
    if (!Array.isArray(list)) return [];
    return list
        .map(l => typeof l === 'string'
            ? { text: l, enabled: true }
            : { text: String((l && (l.Text ?? l.text)) || ''), enabled: (l && (l.Enabled ?? l.enabled)) !== false })
        .filter(l => l.text.length > 0);
}

function _cbNormalizeOrder(order) {
    const out = [];
    (Array.isArray(order) ? order : []).forEach(id => {
        const key = String(id || '').toLowerCase();
        if (CB_LINE_IDS.includes(key) && !out.includes(key)) out.push(key);
    });
    CB_LINE_IDS.forEach(key => { if (!out.includes(key)) out.push(key); });
    return out;
}

function cbReadLineOrder() {
    const list = document.getElementById('cbLineOrder');
    if (!list) return _cbNormalizeOrder(chatboxLineOrder);
    return _cbNormalizeOrder([...list.children].map(b => b.dataset.line));
}

function cbApplyLineOrder(order) {
    chatboxLineOrder = _cbNormalizeOrder(order);
    const list = document.getElementById('cbLineOrder');
    if (!list) return;
    chatboxLineOrder.forEach(id => {
        const block = list.querySelector(`.cb-ord-block[data-line="${id}"]`);
        if (block) list.appendChild(block);
    });
    _cbInitLineOrderDrag();
}

function cbInsertToken(token) {
    const el = document.getElementById('cbTemplate');
    if (!el) return;
    const start = el.selectionStart ?? el.value.length;
    const end   = el.selectionEnd   ?? el.value.length;
    el.value = el.value.slice(0, start) + token + el.value.slice(end);
    const caret = start + token.length;
    el.focus();
    el.setSelectionRange(caret, caret);
    updateChatboxConfig();
}

function cbSyncTemplateUi() {
    const wrap = document.getElementById('cbTemplateWrap');
    const sep  = document.getElementById('cbSeparator');
    if (wrap && sep) wrap.style.display = sep.value === 'custom' ? '' : 'none';
}

function updateChatboxConfig() {
    cbSyncTemplateUi();
    const showAfk = document.getElementById('cbShowAfk').checked;
    const showStats = document.getElementById('cbShowSystemStats').checked;
    chatboxLineOrder = cbReadLineOrder();
    sendToCS({
        action: 'chatboxConfig',
        enabled: chatboxEnabled,
        showTime: document.getElementById('cbShowTime').checked,
        showMedia: document.getElementById('cbShowMedia').checked,
        showPlaytime: document.getElementById('cbShowPlaytime').checked,
        showCustomText: document.getElementById('cbShowCustom').checked,
        showSystemStats: showStats,
        showAfk: showAfk,
        afkMouseSeconds: parseInt(document.getElementById('cbAfkMouseSec')?.value, 10) || 10,
        afkKeyboardSeconds: parseInt(document.getElementById('cbAfkKeyboardSec')?.value, 10) || 10,
        showAfkTime: _cbChecked('cbShowAfkTime', true),
        statCpu: _cbChecked('cbStatCpu', true),
        statRam: _cbChecked('cbStatRam', true),
        statGpu: _cbChecked('cbStatGpu', false),
        statVram: _cbChecked('cbStatVram', false),
        showPulse: _cbChecked('cbShowPulse', false),
        pulseFormat: document.getElementById('cbPulseFormat')?.value || '\u2665 {bpm} BPM',
        hypeRateId: document.getElementById('cbHypeRateId')?.value.trim() || '',
        afHeartRate: _cbChecked('cbAfHeartRate', false),
        showWindow: _cbChecked('cbShowWindow', false),
        windowFormat: document.getElementById('cbWindowFormat')?.value || '',
        showWeather: _cbChecked('cbShowWeather', false),
        weatherCity: document.getElementById('cbWeatherCity')?.value.trim() || '',
        weatherUnit: document.getElementById('cbWeatherUnit')?.value || 'celsius',
        weatherFormat: document.getElementById('cbWeatherFormat')?.value || '',
        afkMessage: document.getElementById('cbAfkMessage').value || t('chatbox.afk.default_message', 'Currently AFK'),
        suppressSound: document.getElementById('cbSuppressSound').checked,
        timeFormat: document.getElementById('cbTimeFormat').value,
        separator: document.getElementById('cbSeparator').value,
        customTemplate: document.getElementById('cbTemplate')?.value || '',
        intervalMs: parseInt(document.getElementById('cbInterval').value, 10) || 5000,
        lineOrder: chatboxLineOrder,
        customLines: chatboxCustomLines.map(l => ({ text: l.text, enabled: l.enabled })),
        hideBackground: document.getElementById('cbHideBackground').checked,
    });
}

function addChatboxLine() {
    const inp = document.getElementById('cbNewLine');
    const text = inp.value.trim();
    if (!text) return;
    chatboxCustomLines.push({ text, enabled: true });
    inp.value = '';
    _cbEditLineIndex = -1;
    renderChatboxLines();
    updateChatboxConfig();
}

function removeChatboxLine(i) {
    chatboxCustomLines.splice(i, 1);
    _cbEditLineIndex = -1;
    renderChatboxLines();
    updateChatboxConfig();
}

function toggleChatboxLine(i, on) {
    if (!chatboxCustomLines[i]) return;
    chatboxCustomLines[i].enabled = !!on;
    renderChatboxLines();
    updateChatboxConfig();
}

function startEditChatboxLine(i) {
    if (!chatboxCustomLines[i]) return;
    _cbEditLineIndex = i;
    renderChatboxLines();
    const inp = document.getElementById('cbEditLine');
    if (inp) { inp.focus(); inp.select(); }
}

function cancelEditChatboxLine() {
    _cbEditLineIndex = -1;
    renderChatboxLines();
}

function saveEditChatboxLine(i) {
    const inp = document.getElementById('cbEditLine');
    const line = chatboxCustomLines[i];
    _cbEditLineIndex = -1;
    if (!inp || !line) { renderChatboxLines(); return; }
    const text = inp.value.trim();
    if (!text || text === line.text) { renderChatboxLines(); return; }
    line.text = text;
    renderChatboxLines();
    updateChatboxConfig();
}

function renderChatboxLines() {
    const el = document.getElementById('cbCustomLines');
    if (!el) return;
    _cbInitCustomLineDrag();
    if (chatboxCustomLines.length === 0) {
        el.innerHTML = `<div style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);padding:6px 0;">${t('chatbox.custom_lines.empty', 'No custom lines added')}</div>`;
        return;
    }
    el.innerHTML = chatboxCustomLines.map((line, i) => {
        if (i === _cbEditLineIndex) {
            return `<div class="cb-line-item cb-line-editing" data-idx="${i}">
                <input type="text" id="cbEditLine" class="vrcn-edit-field cb-line-input" value="${esc(line.text)}"
                    onkeydown="if(event.key==='Enter'){saveEditChatboxLine(${i});}else if(event.key==='Escape'){cancelEditChatboxLine();}">
                <button class="cb-line-btn cb-line-save" onclick="saveEditChatboxLine(${i})" title="${esc(t('common.save', 'Save'))}"><span class="msi" style="font-size:14px;">check</span></button>
                <button class="cb-line-btn" onclick="cancelEditChatboxLine()" title="${esc(t('common.cancel', 'Cancel'))}"><span class="msi" style="font-size:14px;">close</span></button>
            </div>`;
        }
        return `<div class="cb-line-item${line.enabled ? '' : ' cb-line-off'}" data-idx="${i}">
            <span class="msi cb-ord-handle cb-line-handle">drag_indicator</span>
            <label class="toggle cb-line-toggle"><input type="checkbox" ${line.enabled ? 'checked' : ''} onchange="toggleChatboxLine(${i}, this.checked)"><div class="toggle-track"><div class="toggle-knob"></div></div></label>
            <span class="cb-line-text">${esc(line.text)}</span>
            <button class="cb-line-btn" onclick="startEditChatboxLine(${i})" title="${esc(t('common.edit', 'Edit'))}"><span class="msi" style="font-size:14px;">edit</span></button>
            <button class="cb-line-del" onclick="removeChatboxLine(${i})" title="${esc(t('common.remove', 'Remove'))}"><span class="msi" style="font-size:14px;">close</span></button>
        </div>`;
    }).join('');
}

function cbToggleModule(el) {
    const block = el.closest('.cb-ord-block');
    if (block) block.classList.toggle('cb-open');
}

function _cbInitCustomLineDrag() {
    const list = document.getElementById('cbCustomLines');
    if (!list) return;
    if (_cbLineDragCleanup) { _cbLineDragCleanup(); _cbLineDragCleanup = null; }

    const ANIM_MS = 200;
    const EASE = 'cubic-bezier(.2,.7,.3,1)';
    let drag = null;

    function items() { return [...list.querySelectorAll('.cb-line-item')]; }

    function snap() {
        const map = new Map();
        items().forEach(el => map.set(el, el.getBoundingClientRect().top));
        return map;
    }

    function flip(prev) {
        items().forEach(el => {
            if (!prev.has(el)) return;
            const dy = prev.get(el) - el.getBoundingClientRect().top;
            if (!dy) return;
            el.animate([{ transform: `translateY(${dy}px)` }, { transform: 'translateY(0)' }], { duration: ANIM_MS, easing: EASE });
        });
    }

    function resolveTarget(clientY, dragged) {
        let best = null;
        for (const item of items()) {
            if (item === dragged) continue;
            const rect = item.getBoundingClientRect();
            if (clientY < rect.top + rect.height / 2) return { mode: 'before', target: item };
            best = { mode: 'after', target: item };
        }
        return best;
    }

    function onDown(e) {
        if (e.button !== 0) return;
        const handle = e.target.closest('.cb-line-handle');
        if (!handle) return;
        const item = handle.closest('.cb-line-item');
        if (!item || list.querySelector('.cb-line-editing')) return;
        e.preventDefault();

        const rect = item.getBoundingClientRect();
        const ghost = item.cloneNode(true);
        Object.assign(ghost.style, {
            position: 'fixed',
            top: rect.top + 'px',
            left: rect.left + 'px',
            width: rect.width + 'px',
            pointerEvents: 'none',
            zIndex: '10020',
            opacity: '0.92',
            boxShadow: '0 14px 40px rgba(0,0,0,.55)',
            margin: '0',
            transform: 'scale(1.01)',
        });
        document.body.appendChild(ghost);
        item.classList.add('cb-ord-dragging');

        drag = { item, ghost, offsetX: e.clientX - rect.left, offsetY: e.clientY - rect.top, lastKey: null };

        handle.setPointerCapture?.(e.pointerId);
        window.addEventListener('pointermove', onMove);
        window.addEventListener('pointerup', onUp);
        window.addEventListener('pointercancel', onUp);
        document.body.style.cursor = 'grabbing';
    }

    function onMove(e) {
        if (!drag) return;
        drag.ghost.style.top = (e.clientY - drag.offsetY) + 'px';
        drag.ghost.style.left = (e.clientX - drag.offsetX) + 'px';

        const drop = resolveTarget(e.clientY, drag.item);
        const key = drop ? `${drop.mode}:${drop.target.dataset.idx}` : 'none';
        if (key === drag.lastKey) return;
        drag.lastKey = key;

        const prev = snap();
        if (drop) {
            if (drop.mode === 'before') list.insertBefore(drag.item, drop.target);
            else list.insertBefore(drag.item, drop.target.nextSibling);
        }
        flip(prev);
    }

    function onUp() {
        if (!drag) return;
        window.removeEventListener('pointermove', onMove);
        window.removeEventListener('pointerup', onUp);
        window.removeEventListener('pointercancel', onUp);
        document.body.style.cursor = '';

        const { item, ghost } = drag;
        drag = null;

        const finalRect = item.getBoundingClientRect();
        const ghostRect = ghost.getBoundingClientRect();
        const dx = finalRect.left - ghostRect.left;
        const dy = finalRect.top - ghostRect.top;

        ghost.animate(
            [
                { transform: 'translate(0,0) scale(1.01)', opacity: 0.92 },
                { transform: `translate(${dx}px,${dy}px) scale(1)`, opacity: 1 },
            ],
            { duration: ANIM_MS, easing: EASE, fill: 'forwards' }
        ).onfinish = () => {
            ghost.remove();
            item.classList.remove('cb-ord-dragging');
            const order = items().map(el => parseInt(el.dataset.idx, 10));
            const next = order.map(i => chatboxCustomLines[i]).filter(Boolean);
            if (next.length === chatboxCustomLines.length) chatboxCustomLines = next;
            renderChatboxLines();
            updateChatboxConfig();
        };
    }

    list.addEventListener('pointerdown', onDown);
    _cbLineDragCleanup = () => list.removeEventListener('pointerdown', onDown);
}

function _cbInitLineOrderDrag() {
    const list = document.getElementById('cbLineOrder');
    if (!list) return;
    if (_cbDragCleanup) { _cbDragCleanup(); _cbDragCleanup = null; }

    const ANIM_MS = 200;
    const EASE = 'cubic-bezier(.2,.7,.3,1)';
    let drag = null;

    function snap() {
        const map = new Map();
        list.querySelectorAll('.cb-ord-block').forEach(el => map.set(el, el.getBoundingClientRect().top));
        return map;
    }

    function flip(prev) {
        list.querySelectorAll('.cb-ord-block').forEach(el => {
            if (!prev.has(el)) return;
            const dy = prev.get(el) - el.getBoundingClientRect().top;
            if (!dy) return;
            el.animate(
                [{ transform: `translateY(${dy}px)` }, { transform: 'translateY(0)' }],
                { duration: ANIM_MS, easing: EASE }
            );
        });
    }

    function resolveTarget(clientY, dragged) {
        let best = null;
        for (const block of list.children) {
            if (block === dragged) continue;
            const rect = block.getBoundingClientRect();
            if (clientY < rect.top + rect.height / 2) return { mode: 'before', target: block };
            best = { mode: 'after', target: block };
        }
        return best;
    }

    function onDown(e) {
        if (e.button !== 0) return;
        const handle = e.target.closest('.cb-ord-handle');
        if (!handle) return;
        const block = handle.closest('.cb-ord-block');
        if (!block) return;
        e.preventDefault();

        const rect = block.getBoundingClientRect();
        const ghost = block.cloneNode(true);
        Object.assign(ghost.style, {
            position: 'fixed',
            top: rect.top + 'px',
            left: rect.left + 'px',
            width: rect.width + 'px',
            pointerEvents: 'none',
            zIndex: '10020',
            opacity: '0.92',
            boxShadow: '0 14px 40px rgba(0,0,0,.55)',
            borderRadius: '8px',
            background: 'var(--bg-card)',
            padding: '2px 10px',
            transform: 'scale(1.01)',
        });
        document.body.appendChild(ghost);
        block.classList.add('cb-ord-dragging');

        drag = {
            block, ghost,
            offsetX: e.clientX - rect.left,
            offsetY: e.clientY - rect.top,
            lastKey: null,
        };

        handle.setPointerCapture?.(e.pointerId);
        window.addEventListener('pointermove', onMove);
        window.addEventListener('pointerup', onUp);
        window.addEventListener('pointercancel', onUp);
        document.body.style.cursor = 'grabbing';
    }

    function onMove(e) {
        if (!drag) return;
        drag.ghost.style.top = (e.clientY - drag.offsetY) + 'px';
        drag.ghost.style.left = (e.clientX - drag.offsetX) + 'px';

        const drop = resolveTarget(e.clientY, drag.block);
        const key = drop ? `${drop.mode}:${drop.target.dataset.line}` : 'none';
        if (key === drag.lastKey) return;
        drag.lastKey = key;

        const prev = snap();
        if (drop) {
            if (drop.mode === 'before') list.insertBefore(drag.block, drop.target);
            else list.insertBefore(drag.block, drop.target.nextSibling);
        }
        flip(prev);
    }

    function onUp() {
        if (!drag) return;
        window.removeEventListener('pointermove', onMove);
        window.removeEventListener('pointerup', onUp);
        window.removeEventListener('pointercancel', onUp);
        document.body.style.cursor = '';

        const { block, ghost } = drag;
        drag = null;

        const finalRect = block.getBoundingClientRect();
        const ghostRect = ghost.getBoundingClientRect();
        const dx = finalRect.left - ghostRect.left;
        const dy = finalRect.top - ghostRect.top;

        ghost.animate(
            [
                { transform: 'translate(0,0) scale(1.01)', opacity: 0.92 },
                { transform: `translate(${dx}px,${dy}px) scale(1)`, opacity: 1 },
            ],
            { duration: ANIM_MS, easing: EASE, fill: 'forwards' }
        ).onfinish = () => {
            ghost.remove();
            block.classList.remove('cb-ord-dragging');
            chatboxLineOrder = cbReadLineOrder();
            updateChatboxConfig();
        };
    }

    list.addEventListener('pointerdown', onDown);
    _cbDragCleanup = () => list.removeEventListener('pointerdown', onDown);
}

let hypeRateBpm = 0;

function handleHypeRateState(p) {
    const on  = !!(p && p.connected);
    const bpm = (p && p.bpm) || 0;
    hypeRateBpm = bpm;
    const dot = document.getElementById('cbPulseDot');
    const txt = document.getElementById('cbPulseStatus');
    if (!dot || !txt) return;
    dot.className = 'sf-dot ' + (on ? 'online' : 'offline');
    if (p && p.available === false) txt.textContent = t('chatbox.pulse.status.unavailable', 'Not available in this build');
    else if (on && bpm > 0)         txt.textContent = tf('chatbox.pulse.status.bpm', { bpm }, bpm + ' BPM');
    else if (on)                    txt.textContent = t('chatbox.pulse.status.waiting', 'Connected, waiting for data');
    else if (p && p.error)          txt.textContent = p.error;
    else                            txt.textContent = t('chatbox.pulse.status.off', 'Not connected');
}

function handleWeatherState(p) {
    const dot = document.getElementById('cbWeatherDot');
    const txt = document.getElementById('cbWeatherStatus');
    if (!dot || !txt) return;
    const ok = !!(p && p.ok);
    dot.className = 'sf-dot ' + (ok ? 'online' : 'offline');
    if (ok)                                 txt.textContent = (p.city ? p.city + ' \u00b7 ' : '') + p.text;
    else if (p && p.error === 'city_not_found') txt.textContent = t('chatbox.weather.status.not_found', 'City not found');
    else if (p && p.error)                  txt.textContent = p.error;
    else                                    txt.textContent = t('chatbox.weather.status.off', 'No city set');
}

function handleChatboxUpdate(data) {
    _cbLastUpdate = { ..._cbLastUpdate, ...data };

    if (data.enabled !== undefined) {
        const wasEnabled = chatboxEnabled;
        chatboxEnabled = !!data.enabled;
        document.getElementById('badgeChatbox').classList.toggle('tb-active', chatboxEnabled);
        syncChatboxToggleUi();
        if (chatboxEnabled !== wasEnabled) renderDashboard();
    }

    const previewText = document.getElementById('cbPreviewText');
    const charCount = document.getElementById('cbCharCount');
    const text = (_cbLastUpdate.chatboxText || '').replace(/[]/g, '');
    if (previewText && charCount) {
        if (text) {
            previewText.textContent = text;
            charCount.textContent = text.length;
            charCount.style.color = text.length > 130 ? 'var(--err)' : 'var(--tx3)';
        } else {
            previewText.textContent = chatboxPreviewFallback();
            charCount.textContent = '0';
            charCount.style.color = 'var(--tx3)';
        }
    }

    const mediaInfo = document.getElementById('cbMediaInfo');
    if (!mediaInfo) return;
    if (_cbLastUpdate.isPlaying && _cbLastUpdate.currentTitle) {
        const pos = formatMediaTime(_cbLastUpdate.positionMs || 0);
        const dur = formatMediaTime(_cbLastUpdate.durationMs || 0);
        const progress = (_cbLastUpdate.durationMs || 0) > 0
            ? ((_cbLastUpdate.positionMs || 0) / _cbLastUpdate.durationMs * 100)
            : 0;
        mediaInfo.innerHTML = `
            <div class="cb-media-now-playing">
                <span class="msi" style="font-size:16px;color:var(--accent);">music_note</span>
                <div class="cb-media-details">
                    <div class="cb-media-title">${esc(_cbLastUpdate.currentTitle)}</div>
                    <div class="cb-media-artist">${esc(_cbLastUpdate.currentArtist || t('chatbox.media.unknown_artist', 'Unknown'))}</div>
                </div>
            </div>
            <div class="cb-progress-bar"><div class="cb-progress-fill" style="width:${progress}%"></div></div>
            <div class="cb-media-time">${pos} / ${dur}</div>`;
    } else {
        mediaInfo.innerHTML = `<div class="cb-media-idle"><span class="msi" style="font-size:16px;vertical-align:middle;">music_off</span> ${t('chatbox.media.none', 'No media playing')}</div>`;
    }
}

function sendChatboxDirectMessage() {
    const inp = document.getElementById('cbChatInput');
    const text = inp.value.trim();
    if (!text) return;
    const limited = text.slice(0, 144);
    sendToCS({ action: 'chatboxDirectSend', text: limited });
    const now = new Date();
    const ts = now.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    _cbChatHistory.push({ text: limited, ts });
    if (_cbChatHistory.length > CB_MAX_HISTORY) _cbChatHistory.shift();
    inp.value = '';
    updateCbChatCharCount('');
    renderChatboxHistory();
    startCbPause();
}

function updateCbChatCharCount(val) {
    const el = document.getElementById('cbChatCharCount');
    if (!el) return;
    el.textContent = val.length;
    el.style.color = val.length > 130 ? 'var(--err)' : val.length > 110 ? '#FFA726' : 'var(--tx3)';
}

function renderChatboxHistory() {
    const el = document.getElementById('cbChatHistory');
    if (!el) return;
    if (_cbChatHistory.length === 0) {
        el.innerHTML = `<div class="osc-empty">${t('chatbox.live_chat.empty', 'No messages sent yet')}</div>`;
        return;
    }
    el.innerHTML = _cbChatHistory.map((m, i) =>
        `<div class="msgr-msg msgr-mine cb-msg-hoverable">
            <button class="vrcn-resend-button" onclick="resendChatboxMessage(${i})" title="${esc(t('common.resend', 'Resend'))}"><span class="msi" style="font-size:14px;">refresh</span></button>
            <div class="msgr-bubble">${esc(m.text)}</div>
            <div class="msgr-time">${esc(m.ts)}</div>
        </div>`
    ).join('');
    el.scrollTop = el.scrollHeight;
}

function resendChatboxMessage(i) {
    const m = _cbChatHistory[i];
    if (!m) return;
    const text = (m.text || '').slice(0, 144);
    if (!text) return;
    sendToCS({ action: 'chatboxDirectSend', text });
    const now = new Date();
    const ts = now.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    _cbChatHistory.push({ text, ts });
    if (_cbChatHistory.length > CB_MAX_HISTORY) _cbChatHistory.shift();
    renderChatboxHistory();
    startCbPause();
}

function startCbPause() {
    if (_cbPauseTimer) clearInterval(_cbPauseTimer);
    _cbPauseRemaining = CB_PAUSE_SECONDS;
    _updateCbPauseDisplay();
    _cbPauseTimer = setInterval(() => {
        _cbPauseRemaining--;
        _updateCbPauseDisplay();
        if (_cbPauseRemaining <= 0) {
            clearInterval(_cbPauseTimer);
            _cbPauseTimer = null;
            _updateCbPauseDisplay();
        }
    }, 1000);
}

function _updateCbPauseDisplay() {}

function formatMediaTime(ms) {
    const s = Math.floor(ms / 1000);
    const m = Math.floor(s / 60);
    const sec = s % 60;
    if (m >= 60) {
        const h = Math.floor(m / 60);
        return `${h}:${String(m % 60).padStart(2, '0')}:${String(sec).padStart(2, '0')}`;
    }
    return `${m}:${String(sec).padStart(2, '0')}`;
}
