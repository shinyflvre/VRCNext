/* ── VRCN Messenger ────────────────────────────────────────────────────────
 * iMessage-style chat over VRChat invite message slots.
 * Protocol: invites to wrld_4432ea9b... whose slot text starts with "msg "
 * are intercepted by VRCN clients and stored as chat messages.
 * Rate limit: 1 message every ~2.5 min (24 slots × 60 min cooldown).
 */

const MSGR_MAX_CHARS = 60; // 64 slot limit − 4 for "msg " prefix

let _messengerUserId   = null;
let _messengerName     = '';
let _messengerCooldown = null;
let _messengerSlots    = { used: 0, total: 24 };
let _pendingBoopUserId = null;
let _msgrCdInterval    = null;
let _msgrCdEnd         = 0; // Date.now() timestamp when cooldown expires (global, survives close/reopen)

const MSGR_SEND_COOLDOWN = 45; // seconds

// Chat inbox: userId → { userId, displayName, image, status, statusDesc, text, time, count }
const _chatInbox = new Map();
let _chatPanelDismiss = null;

function toggleChatPanel() {
    const panel = document.getElementById('chatPanel');
    const isOpen = panel.style.display !== 'none';
    if (isOpen) {
        panel.style.display = 'none';
        if (_chatPanelDismiss) { document.removeEventListener('click', _chatPanelDismiss); _chatPanelDismiss = null; }
    } else {
        panel.style.display = '';
        renderChatPanel();
        setTimeout(() => {
            _chatPanelDismiss = e => {
                const btn = document.getElementById('btnChat');
                if (!panel.contains(e.target) && !btn?.contains(e.target)) toggleChatPanel();
            };
            document.addEventListener('click', _chatPanelDismiss);
        }, 0);
    }
}

function renderChatPanel() {
    const list = document.getElementById('chatPanelList');
    if (!list) return;
    if (_chatInbox.size === 0) { list.innerHTML = '<div class="empty-msg">No messages</div>'; return; }
    list.innerHTML = [..._chatInbox.values()]
        .sort((a, b) => b.time - a.time)
        .map(entry => {
            const avatarStyle = entry.image
                ? `background-image:url('${cssUrl(entry.image)}');background-size:cover;background-position:center;`
                : '';
            const time = entry.time
                ? new Date(entry.time).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
                : '';
            return `<div class="chat-inbox-item" onclick="chatPanelOpen('${esc(entry.userId)}')">
                <div class="chat-inbox-avatar" style="${avatarStyle}"></div>
                <div class="chat-inbox-body">
                    <div class="chat-inbox-row">
                        <span class="chat-inbox-name">${esc(entry.displayName)}</span>
                        <span class="chat-inbox-time">${time}</span>
                    </div>
                    <div class="chat-inbox-text">${esc(entry.text)}</div>
                </div>
                ${entry.count > 1 ? `<div class="chat-inbox-count">${entry.count}</div>` : ''}
            </div>`;
        }).join('');
}

function chatPanelOpen(userId) {
    toggleChatPanel();
    const entry = _chatInbox.get(userId);
    _chatInbox.delete(userId);
    updateChatBadge();
    openMessenger(userId, entry?.displayName || userId, entry?.image || '', entry?.status || '', entry?.statusDesc || '');
}

function updateChatBadge() {
    const total = [..._chatInbox.values()].reduce((s, e) => s + e.count, 0);
    const badge = document.getElementById('chatBadge');
    if (!badge) return;
    badge.textContent = total;
    badge.style.display = total > 0 ? '' : 'none';
}

function openMessenger(userId, displayName, image, status, statusDesc) {
    closeMessenger();
    _messengerUserId = userId;
    _messengerName   = displayName;

    const statusColor = _msgrStatusColor(status);
    const statusText  = statusDesc || statusLabel(status) || 'Offline';
    const avatarStyle = image
        ? `background-image:url('${cssUrl(image)}');background-size:cover;background-position:center;`
        : '';

    const el = document.createElement('div');
    el.id = 'messengerPanel';
    el.innerHTML = `
        <div id="msgrHeader">
            <div id="msgrHeaderInfo">
                <div id="msgrAvatarWrap">
                    <div id="msgrAvatar" style="${avatarStyle}"></div>
                    <div id="msgrStatusDot" style="background:${statusColor}"></div>
                </div>
                <div id="msgrHeaderText">
                    <div id="msgrName">${esc(displayName)}</div>
                    <div id="msgrSub">${esc(statusText)}</div>
                </div>
            </div>
            <div id="msgrHeaderRight">
                <div id="msgrSlotWrap" title="Message slots used (60 min cooldown each)">
                    <svg id="msgrSlotRing" viewBox="0 0 36 36" width="28" height="28" style="transform:rotate(-90deg)">
                        <circle class="msgr-ring-bg" cx="18" cy="18" r="15.9"/>
                        <circle id="msgrRingFg" cx="18" cy="18" r="15.9"
                            style="fill:none;stroke:#2DD48C;stroke-width:3;stroke-linecap:round;stroke-dasharray:0 99.9"/>
                    </svg>
                    <div id="msgrSlotText">0/24</div>
                </div>
                <button id="msgrClose" onclick="closeMessenger()"><span class="msi">close</span></button>
            </div>
        </div>
        <div id="msgrMessages"></div>
        <div id="msgrCooldownBar" style="display:none;">
            <span class="msi" style="font-size:13px;">schedule</span>
            <span id="msgrCooldownText"></span>
        </div>
        <div id="msgrFooter">
            <div id="msgrInputWrap">
                <textarea id="msgrInput" placeholder="Message ${esc(displayName)}…" rows="1" maxlength="${MSGR_MAX_CHARS}"
                    oninput="msgrOnInput(this)"
                    onkeydown="if((event.ctrlKey||event.metaKey)&&event.key==='Enter'){event.preventDefault();messengerSend();}"
                ></textarea>
                <span id="msgrCharCount">${MSGR_MAX_CHARS}</span>
            </div>
            <button id="msgrSendBtn" onclick="messengerSend()">
                <span class="msi">arrow_upward</span>
            </button>
        </div>`;

    document.body.appendChild(el);
    sendToCS({ action: 'vrcGetChatHistory', userId });
    // Resume cooldown UI if still active from a previous send
    if (Date.now() < _msgrCdEnd) {
        setTimeout(_applyCooldownUI, 0);
    } else {
        setTimeout(() => el.querySelector('#msgrInput')?.focus(), 80);
    }
}

function msgrOnInput(el) {
    el.style.height = 'auto';
    el.style.height = Math.min(el.scrollHeight, 120) + 'px';
    const remaining = MSGR_MAX_CHARS - el.value.length;
    const counter = document.getElementById('msgrCharCount');
    if (!counter) return;
    counter.textContent = remaining;
    counter.className = remaining <= 10 ? 'msgr-char-warn' : remaining <= 20 ? 'msgr-char-low' : '';
}

function _msgrStatusColor(status) {
    if (!status) return 'var(--tx3)';
    const s = (typeof STATUS_LIST !== 'undefined') && STATUS_LIST.find(x => x.key === status);
    if (s) return s.color;
    const m = { active: '#2DD48C', 'join me': '#42A5F5', 'ask me': '#FFA726', busy: '#EF5350' };
    return m[status] || 'var(--tx3)';
}

function closeMessenger() {
    clearInterval(_messengerCooldown);
    clearInterval(_msgrCdInterval); // stop UI ticker — _msgrCdEnd timestamp is kept globally
    _messengerCooldown = null;
    _msgrCdInterval    = null;
    _messengerUserId   = null;
    document.getElementById('messengerPanel')?.remove();
}

function messengerSend() {
    const input = document.getElementById('msgrInput');
    const text  = input?.value?.trim();
    if (!text || !_messengerUserId || Date.now() < _msgrCdEnd) return;
    input.value = '';
    input.style.height = '';
    msgrOnInput(input);
    input.disabled = true;
    document.getElementById('msgrSendBtn').disabled = true;
    sendToCS({ action: 'vrcSendChatMessage', userId: _messengerUserId, text });
}

// Called from messages.js when C# fires vrcChatSlotInfo
function handleChatSlotInfo(info) {
    _messengerSlots = info;
    updateSlotIndicator(info.used, info.total);
}

function updateSlotIndicator(used, total) {
    const ring = document.getElementById('msgrRingFg');
    const text = document.getElementById('msgrSlotText');
    if (!ring || !text) return;
    const C   = 99.9; // 2π × r=15.9
    const pct = total > 0 ? used / total : 0;
    const color = pct >= 0.9 ? '#EF5350' : pct >= 0.6 ? '#FFA726' : '#2DD48C';
    ring.style.stroke          = color;
    ring.style.strokeDasharray = `${(pct * C).toFixed(2)} ${C}`;
    text.textContent = `${used}/${total}`;
    text.style.color = color;
}

// Called from messages.js when C# fires vrcChatHistory
function handleChatHistory(payload) {
    if (payload.userId !== _messengerUserId) return;
    const container = document.getElementById('msgrMessages');
    if (!container) return;
    container.innerHTML = '';
    (payload.messages || []).forEach(m => appendChatMessage(m, false));
    container.scrollTop = container.scrollHeight;
}

// Called from messages.js when C# fires vrcChatMessage
function handleChatMessage(msg) {
    // Append to open conversation
    if (document.getElementById('messengerPanel') &&
        (msg.from === _messengerUserId || msg.from === 'me')) {
        appendChatMessage(msg, true);
        return;
    }
    // Incoming while messenger is closed → add to inbox
    if (msg.from === 'me') return;
    const f = (typeof vrcFriendsData !== 'undefined') && vrcFriendsData.find(x => x.id === msg.from);
    const existing = _chatInbox.get(msg.from) || {
        userId: msg.from,
        displayName: f?.displayName || msg.from,
        image: f?.image || '',
        status: f?.status || '',
        statusDesc: f?.statusDescription || '',
        count: 0,
    };
    existing.text  = msg.text;
    existing.time  = msg.time ? new Date(msg.time).getTime() : Date.now();
    existing.count++;
    _chatInbox.set(msg.from, existing);
    updateChatBadge();
    if (document.getElementById('chatPanel')?.style.display !== 'none') renderChatPanel();
}

function msgrRegisterBoopSent(userId) {
    _pendingBoopUserId = userId;
}

function handleBoopSent() {
    const uid = _pendingBoopUserId;
    _pendingBoopUserId = null;
    // Show bubble if messenger is open with the booped user,
    // or as fallback if messenger is open but we lost track of the target.
    if (!document.getElementById('messengerPanel')) return;
    if (!uid || uid === _messengerUserId) appendBoopBubble(true, _messengerName);
}

function handleBoopReceived(senderUserId, senderUsername) {
    if (!document.getElementById('messengerPanel')) return;
    const idMatch   = senderUserId && senderUserId === _messengerUserId;
    const nameMatch = senderUsername && senderUsername === _messengerName;
    if (!idMatch && !nameMatch) return;
    appendBoopBubble(false, senderUsername || _messengerName);
}

function appendBoopBubble(isMine, name, isoTime) {
    const container = document.getElementById('msgrMessages');
    if (!container) return;
    const time = isoTime
        ? new Date(isoTime).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
        : new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    const text = isMine ? `You booped ${esc(name)}!` : `${esc(name)} booped you!`;
    const div = document.createElement('div');
    div.className = 'msgr-boop-event';
    div.innerHTML = `<span class="msi msgr-boop-icon">favorite</span><span class="msgr-boop-text">${text}</span><span class="msgr-boop-time">${time}</span>`;
    container.appendChild(div);
    container.scrollTop = container.scrollHeight;
}

function appendChatMessage(msg, scroll) {
    if (msg.type === 'boop') {
        appendBoopBubble(msg.from === 'me', _messengerName, msg.time);
        return;
    }
    const container = document.getElementById('msgrMessages');
    if (!container) return;
    const isMine = msg.from === 'me';
    const time   = msg.time ? new Date(msg.time).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : '';
    const div    = document.createElement('div');
    div.className = 'msgr-msg ' + (isMine ? 'msgr-mine' : 'msgr-theirs');
    div.innerHTML = `<div class="msgr-bubble">${esc(msg.text)}</div><div class="msgr-time">${time}</div>`;
    container.appendChild(div);
    if (scroll) container.scrollTop = container.scrollHeight;
}

// Called from messages.js on vrcActionResult for sendChatMessage
function handleChatActionResult(payload) {
    const input   = document.getElementById('msgrInput');
    const sendBtn = document.getElementById('msgrSendBtn');

    if (!payload.success) {
        if (input)   { input.disabled = false; input.focus(); }
        if (sendBtn) sendBtn.disabled = false;
        const bar  = document.getElementById('msgrCooldownBar');
        const text = document.getElementById('msgrCooldownText');
        if (bar && text) {
            bar.style.display = 'flex';
            text.textContent = payload.message || 'Rate limited';
            clearTimeout(_messengerCooldown);
            _messengerCooldown = setTimeout(() => { if (bar) bar.style.display = 'none'; }, 5000);
        }
        return;
    }

    // Success → start 45s cooldown shown in the input placeholder
    _startSendCooldown();
}

function _startSendCooldown() {
    _msgrCdEnd = Date.now() + MSGR_SEND_COOLDOWN * 1000;
    _applyCooldownUI();
}

function _applyCooldownUI() {
    if (_msgrCdInterval) clearInterval(_msgrCdInterval);
    const remaining = () => Math.max(0, Math.ceil((_msgrCdEnd - Date.now()) / 1000));

    function tick() {
        const inp = document.getElementById('msgrInput');
        const btn = document.getElementById('msgrSendBtn');
        if (!inp) { clearInterval(_msgrCdInterval); _msgrCdInterval = null; return; }
        const r = remaining();
        if (r <= 0) {
            clearInterval(_msgrCdInterval);
            _msgrCdInterval   = null;
            inp.disabled      = false;
            inp.placeholder   = `Message ${_messengerName}…`;
            if (btn) btn.disabled = false;
            inp.focus();
        } else {
            inp.disabled      = true;
            inp.placeholder   = `Cooldown... ${r}`;
            if (btn) btn.disabled = true;
        }
    }

    tick();
    _msgrCdInterval = setInterval(tick, 1000);
}
