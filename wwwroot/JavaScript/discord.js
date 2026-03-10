// Discord Rich Presence
let _dpRunning = false;
let _dpJoinedAt = null;
let _dpTickInterval = null;
let _dpCurrentWorld = '';
let _dpCurrentImg = '';
let _dpCurrentState = '';

// ── Tab activation ──────────────────────────────────────────────────────────

document.documentElement.addEventListener('tabchange', () => {
    const tab = document.getElementById('tab19');
    if (tab && tab.classList.contains('active')) {
        dpUpdatePreviewClock();
    }
});

// ── AutoSave (called by toggle onchange) ────────────────────────────────────

function dpAutoSave() {
    if (typeof saveSettings === 'function') saveSettings();
}

// ── Toggle ──────────────────────────────────────────────────────────────────

function dpToggle() {
    if (_dpRunning) {
        sendToCS({ action: 'dpStop' });
    } else {
        sendToCS({ action: 'dpStart' });
    }
}

// ── Receive from C# ─────────────────────────────────────────────────────────

function dpOnState(p) {
    _dpRunning = !!p.running;
    const dot = document.getElementById('dpDot');
    const txt = document.getElementById('dpStatusText');
    const btn = document.getElementById('dpConnBtn');
    const preview = document.getElementById('dpPreviewCard');
    if (!dot) return;

    if (_dpRunning) {
        dot.className = 'sf-dot online';
        txt.textContent = 'Connected to Discord';
        btn.innerHTML = '<span class="msi" style="font-size:16px;">stop</span> Stop';
        if (preview) preview.style.display = '';
        if (!_dpJoinedAt) _dpJoinedAt = Date.now();
        dpUpdateStatusDot();
        dpStartClock();
    } else {
        dot.className = 'sf-dot offline';
        txt.textContent = 'Not connected';
        btn.innerHTML = '<span class="msi" style="font-size:16px;">play_arrow</span> Start';
        if (preview) preview.style.display = 'none';
        dpStopClock();
    }
}

// Called from messages.js when the instance panel updates
function dpOnInstanceUpdate(worldName, worldImg, instanceState, joinedAt) {
    _dpCurrentWorld = worldName || '';
    _dpCurrentImg   = worldImg  || '';
    _dpCurrentState = instanceState || '';
    if (joinedAt) _dpJoinedAt = new Date(joinedAt).getTime();

    const title = document.getElementById('dpPreviewTitle');
    const state = document.getElementById('dpPreviewState');
    const img   = document.getElementById('dpPreviewImg');
    if (title) title.textContent = _dpCurrentWorld || '—';
    if (state) state.textContent = _dpCurrentState || '—';
    if (img && _dpCurrentImg) img.src = _dpCurrentImg;

    dpUpdateStatusDot();
    if (_dpRunning) dpUpdatePreviewClock();
}

// ── Status dot color ─────────────────────────────────────────────────────────

function dpUpdateStatusDot() {
    const dot = document.getElementById('dpPreviewStatusDot');
    if (!dot) return;
    const status = (typeof currentVrcUser !== 'undefined' && currentVrcUser?.status || '').toLowerCase();
    const cls = status === 'join me'  ? 'join_me'
              : status === 'busy'     ? 'busy'
              : status === 'ask me'   ? 'ask_me'
              : status === 'offline'  ? 'offline'
              : 'online';
    dot.className = 'dp-status-dot ' + cls;
}

// ── Elapsed clock ────────────────────────────────────────────────────────────

function dpStartClock() {
    dpStopClock();
    _dpTickInterval = setInterval(dpUpdatePreviewClock, 1000);
}

function dpStopClock() {
    if (_dpTickInterval) { clearInterval(_dpTickInterval); _dpTickInterval = null; }
}

function dpUpdatePreviewClock() {
    const el = document.getElementById('dpPreviewTime');
    if (!el) return;
    if (!_dpJoinedAt) { el.textContent = '—'; return; }
    const sec = Math.floor((Date.now() - _dpJoinedAt) / 1000);
    const h = Math.floor(sec / 3600);
    const m = Math.floor((sec % 3600) / 60);
    const s = sec % 60;
    el.textContent = h > 0
        ? `${h}:${String(m).padStart(2,'0')}:${String(s).padStart(2,'0')} elapsed`
        : `${m}:${String(s).padStart(2,'0')} elapsed`;
}
