/* Custom Chatbox OSC */
function toggleChatbox() {
    chatboxEnabled = !chatboxEnabled;
    const btn = document.getElementById('cbConnBtn');
    const dot = document.getElementById('cbDot');
    const txt = document.getElementById('cbStatusText');
    if (chatboxEnabled) {
        if (btn) btn.innerHTML = '<span class="msi" style="font-size:16px;">stop</span> Stop';
        if (dot) dot.className = 'sf-dot online';
        if (txt) txt.textContent = 'Running';
    } else {
        if (btn) btn.innerHTML = '<span class="msi" style="font-size:16px;">play_arrow</span> Start';
        if (dot) dot.className = 'sf-dot offline';
        if (txt) txt.textContent = 'Not running';
    }
    document.getElementById('badgeChatbox').className = chatboxEnabled ? 'mini-badge online' : 'mini-badge offline';
    updateChatboxConfig();
}

function updateChatboxConfig() {
    const showAfk = document.getElementById('cbShowAfk').checked;
    document.getElementById('cbAfkCard').style.display = showAfk ? '' : 'none';
    sendToCS({
        action: 'chatboxConfig',
        enabled: chatboxEnabled,
        showTime: document.getElementById('cbShowTime').checked,
        showMedia: document.getElementById('cbShowMedia').checked,
        showPlaytime: document.getElementById('cbShowPlaytime').checked,
        showCustomText: document.getElementById('cbShowCustom').checked,
        showSystemStats: document.getElementById('cbShowSystemStats').checked,
        showAfk: showAfk,
        afkMessage: document.getElementById('cbAfkMessage').value || 'Currently AFK',
        suppressSound: document.getElementById('cbSuppressSound').checked,
        timeFormat: document.getElementById('cbTimeFormat').value,
        separator: document.getElementById('cbSeparator').value,
        intervalMs: parseInt(document.getElementById('cbInterval').value) || 5000,
        customLines: chatboxCustomLines,
    });
}

function addChatboxLine() {
    const inp = document.getElementById('cbNewLine');
    const text = inp.value.trim();
    if (!text) return;
    chatboxCustomLines.push(text);
    inp.value = '';
    renderChatboxLines();
    updateChatboxConfig();
}

function removeChatboxLine(i) {
    chatboxCustomLines.splice(i, 1);
    renderChatboxLines();
    updateChatboxConfig();
}

function renderChatboxLines() {
    const el = document.getElementById('cbCustomLines');
    if (chatboxCustomLines.length === 0) {
        el.innerHTML = '<div style="font-size:11px;color:var(--tx3);padding:6px 0;">No custom lines added</div>';
        return;
    }
    el.innerHTML = chatboxCustomLines.map((line, i) =>
        `<div class="cb-line-item">
            <span class="cb-line-text">${esc(line)}</span>
            <button class="cb-line-del" onclick="removeChatboxLine(${i})"><span class="msi" style="font-size:14px;">close</span></button>
        </div>`
    ).join('');
}

// Handle updates from backend
function handleChatboxUpdate(data) {
    // Sync topbar badge with enabled state
    if (data.enabled !== undefined) {
        document.getElementById('badgeChatbox').className = data.enabled ? 'mini-badge online' : 'mini-badge offline';
    }

    // Update preview
    const previewText = document.getElementById('cbPreviewText');
    const charCount = document.getElementById('cbCharCount');
    if (data.chatboxText) {
        previewText.textContent = data.chatboxText;
        charCount.textContent = data.chatboxText.length;
        charCount.style.color = data.chatboxText.length > 130 ? 'var(--err)' : 'var(--tx3)';
    } else {
        previewText.textContent = chatboxEnabled ? 'Waiting for data...' : 'Enable chatbox to see preview';
        charCount.textContent = '0';
    }

    // Update media info display
    const mediaInfo = document.getElementById('cbMediaInfo');
    if (data.isPlaying && data.currentTitle) {
        const pos = formatMediaTime(data.positionMs);
        const dur = formatMediaTime(data.durationMs);
        const progress = data.durationMs > 0 ? (data.positionMs / data.durationMs * 100) : 0;
        mediaInfo.innerHTML = `
            <div class="cb-media-now-playing">
                <span class="msi" style="font-size:16px;color:var(--accent);">music_note</span>
                <div class="cb-media-details">
                    <div class="cb-media-title">${esc(data.currentTitle)}</div>
                    <div class="cb-media-artist">${esc(data.currentArtist || 'Unknown')}</div>
                </div>
            </div>
            <div class="cb-progress-bar"><div class="cb-progress-fill" style="width:${progress}%"></div></div>
            <div class="cb-media-time">${pos} / ${dur}</div>`;
    } else {
        mediaInfo.innerHTML = '<div class="cb-media-idle"><span class="msi" style="font-size:16px;vertical-align:middle;">music_off</span> No media playing</div>';
    }
}

function formatMediaTime(ms) {
    const s = Math.floor(ms / 1000);
    const m = Math.floor(s / 60);
    const sec = s % 60;
    if (m >= 60) {
        const h = Math.floor(m / 60);
        return `${h}:${String(m % 60).padStart(2,'0')}:${String(sec).padStart(2,'0')}`;
    }
    return `${m}:${String(sec).padStart(2,'0')}`;
}
