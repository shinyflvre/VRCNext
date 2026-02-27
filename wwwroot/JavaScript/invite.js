/* === Multi-Invite Modal === */
let _inviteSelected = new Set();
let _inviteSending = false;

function openInviteModal() {
    if (!currentVrcUser) return;
    if (!currentInstanceData || currentInstanceData.empty || currentInstanceData.error || !currentInstanceData.worldId) {
        showMypToast(false, 'You must be in an instance to invite friends.');
        return;
    }
    const m = document.getElementById('modalInvite');
    if (!m) return;
    _inviteSelected = new Set();
    _inviteSending = false;
    _renderInviteModal();
    m.style.display = 'flex';
}

function closeInviteModal() {
    const m = document.getElementById('modalInvite');
    if (m) m.style.display = 'none';
    _inviteSelected = new Set();
    _inviteSending = false;
}

function _renderInviteModal() {
    const box = document.getElementById('inviteBox');
    if (!box) return;
    const worldName = currentInstanceData?.worldName || 'Current Instance';
    const worldThumb = currentInstanceData?.worldThumb || '';
    const instanceType = currentInstanceData?.instanceType || '';
    const typeBadge = instanceType && instanceType !== 'public'
        ? `<span class="inst-type-badge">${esc(getInstanceBadge(instanceType).label)}</span>` : '';
    box.innerHTML = `
        <div class="inv-world-banner" style="background-image:url('${esc(worldThumb)}')">
            <div class="inv-world-fade"></div>
            <div class="inv-world-info">
                <div class="inv-world-name">${esc(worldName)} ${typeBadge}</div>
                <div style="font-size:10px;color:rgba(255,255,255,.65);margin-top:3px;">Invite to this instance</div>
            </div>
            <button class="inv-close-btn" onclick="closeInviteModal()"><span class="msi">close</span></button>
        </div>
        <div class="inv-search-wrap">
            <span class="msi inv-search-icon">search</span>
            <input type="text" id="inviteSearch" class="inv-search-input" placeholder="Search friends..." oninput="filterInviteList()">
        </div>
        <div id="inviteList" class="inv-list"></div>
        <div class="inv-footer">
            <span id="inviteSelCount" class="inv-sel-count"></span>
            <button id="inviteSendBtn" class="inv-send-btn" onclick="sendMultiInvite()" disabled>Send Invite</button>
        </div>
        <div id="inviteProgress" class="inv-progress-wrap" style="display:none;">
            <div class="inv-progress-track"><div id="inviteProgressBar" class="inv-progress-bar"></div></div>
            <div id="inviteProgressText" class="inv-progress-text"></div>
        </div>`;
    renderInviteList();
}

function renderInviteList(filter) {
    const el = document.getElementById('inviteList');
    if (!el) return;

    const myLocBase = currentInstanceData?.location?.split('~')[0] || null;

    const friends = (vrcFriendsData || []).filter(f => {
        if (f.presence === 'offline') return false;
        if (myLocBase && f.location && f.location.split('~')[0] === myLocBase) return false;
        if (filter) {
            if (!(f.displayName || '').toLowerCase().includes(filter.toLowerCase())) return false;
        }
        return true;
    });

    if (friends.length === 0) {
        el.innerHTML = `<div class="inv-empty">${filter ? 'No results' : 'No friends available to invite'}</div>`;
        return;
    }

    const gameFriends = friends.filter(f => f.presence === 'game');
    const webFriends = friends.filter(f => f.presence === 'web');

    let h = '';

    function card(f) {
        const sel = _inviteSelected.has(f.id);
        const hasImg = f.image && f.image.length > 5;
        const av = hasImg
            ? `<img class="fd-mutual-avatar" src="${esc(f.image)}" onerror="this.outerHTML='<div class=\\'fd-mutual-avatar\\' style=\\'display:flex;align-items:center;justify-content:center;font-size:14px;font-weight:700;color:var(--tx3)\\'>${esc((f.displayName||'?')[0])}</div>'">`
            : `<div class="fd-mutual-avatar" style="display:flex;align-items:center;justify-content:center;font-size:14px;font-weight:700;color:var(--tx3)">${esc((f.displayName||'?')[0])}</div>`;
        const isWeb = f.presence === 'web';
        const loc = isWeb ? 'Web / Mobile' : (f.statusDescription || statusLabel(f.status || 'offline'));
        const indicatorClass = isWeb ? 'vrc-status-ring' : 'vrc-status-dot';
        const statusCls = statusDotClass(f.status || 'offline');
        const fid = jsq(f.id || '');
        return `<div class="inv-row${sel ? ' inv-row-sel' : ''}" onclick="toggleInviteSelect('${fid}')">
            <div class="inv-check${sel ? ' inv-check-on' : ''}">
                ${sel ? '<span class="msi" style="font-size:13px;line-height:1;">check</span>' : ''}
            </div>
            ${av}
            <div class="fd-mutual-info">
                <div class="fd-mutual-name">${esc(f.displayName)}</div>
                <div class="fd-mutual-status"><span class="${indicatorClass} ${statusCls}" style="width:6px;height:6px;flex-shrink:0;"></span>${esc(loc)}</div>
            </div>
        </div>`;
    }

    if (gameFriends.length > 0) {
        h += `<div class="inv-section-lbl">IN-GAME — ${gameFriends.length}</div>`;
        gameFriends.forEach(f => h += card(f));
    }
    if (webFriends.length > 0) {
        h += `<div class="inv-section-lbl">WEB / ACTIVE — ${webFriends.length}</div>`;
        webFriends.forEach(f => h += card(f));
    }

    el.innerHTML = h;
    _updateInviteFooter();
}

function filterInviteList() {
    const q = document.getElementById('inviteSearch')?.value || '';
    renderInviteList(q);
}

function toggleInviteSelect(userId) {
    if (_inviteSending) return;
    if (_inviteSelected.has(userId)) {
        _inviteSelected.delete(userId);
    } else {
        _inviteSelected.add(userId);
    }
    const q = document.getElementById('inviteSearch')?.value || '';
    renderInviteList(q);
}

function _updateInviteFooter() {
    const count = _inviteSelected.size;
    const btn = document.getElementById('inviteSendBtn');
    const lbl = document.getElementById('inviteSelCount');
    if (btn) {
        btn.disabled = count === 0 || _inviteSending;
        btn.textContent = count > 0 ? `Send Invite (${count})` : 'Send Invite';
    }
    if (lbl) lbl.textContent = count > 0 ? `${count} selected` : '';
}

function sendMultiInvite() {
    const ids = Array.from(_inviteSelected);
    if (!ids.length || _inviteSending) return;
    _inviteSending = true;

    const btn = document.getElementById('inviteSendBtn');
    if (btn) btn.disabled = true;

    const prog = document.getElementById('inviteProgress');
    if (prog) prog.style.display = '';

    _applyInviteProgress(0, ids.length, 0, 0);
    sendToCS({ action: 'vrcBatchInvite', userIds: ids });
}

function handleBatchInviteProgress(payload) {
    _applyInviteProgress(payload.done, payload.total, payload.success, payload.fail);
    if (payload.done >= payload.total) {
        _inviteSending = false;
        setTimeout(() => _updateInviteFooter(), 1500);
    }
}

function _applyInviteProgress(done, total, success, fail) {
    const bar = document.getElementById('inviteProgressBar');
    const txt = document.getElementById('inviteProgressText');
    const pct = total > 0 ? Math.round((done / total) * 100) : 0;

    if (bar) {
        bar.style.width = pct + '%';
        bar.style.background = done >= total
            ? (fail === 0 ? 'var(--ok)' : 'var(--accent)')
            : 'var(--accent)';
    }
    if (txt) {
        if (done < total) {
            txt.textContent = `Sending ${done + 1} of ${total}...`;
            txt.style.color = 'var(--tx2)';
        } else {
            const parts = [];
            if (success > 0) parts.push(`${success} sent`);
            if (fail > 0) parts.push(`${fail} failed`);
            txt.textContent = parts.join(', ') + ' — Done!';
            txt.style.color = fail === 0 ? 'var(--ok)' : 'var(--accent)';
        }
    }
}
