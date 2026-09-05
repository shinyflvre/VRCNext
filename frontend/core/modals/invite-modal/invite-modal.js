/* === Invite Modal === */
let _inviteSelected = new Set();
let _inviteSending = false;
let _inviteOverride = null; // { location, worldName, worldThumb, instanceType } for non-current invites
let _inviteFilter = '';
let _inviteProgressState = null;

function openInviteModal() {
    if (!currentVrcUser) return;
    if (!currentInstanceData || currentInstanceData.empty || currentInstanceData.error || !currentInstanceData.worldId) {
        showToast(false, t('invite.multi.error.no_instance', 'You must be in an instance to invite friends.'));
        return;
    }
    const m = document.getElementById('modalInvite');
    if (!m) return;
    _inviteSelected = new Set();
    _inviteSending = false;
    _inviteOverride = null;
    _inviteFilter = '';
    _inviteProgressState = null;
    _renderInviteModal();
    m.style.display = 'flex';
}

function closeInviteModal() {
    const m = document.getElementById('modalInvite');
    if (m) m.style.display = 'none';
    _inviteSelected = new Set();
    _inviteSending = false;
    _inviteOverride = null;
    _inviteFilter = '';
    _inviteProgressState = null;
    if (typeof _grpInvGroupId !== 'undefined') _grpInvGroupId = null;
}

function openInviteModalForLocation(location, worldName, worldThumb, instanceType) {
    const m = document.getElementById('modalInvite');
    if (!m) return;
    _inviteSelected = new Set();
    _inviteSending = false;
    _inviteOverride = { location, worldName, worldThumb, instanceType };
    _inviteFilter = '';
    _inviteProgressState = null;
    _renderInviteModal();
    m.style.display = 'flex';
}

function _inviteSendButtonLabel(count) {
    const base = t('profiles.invite.send', 'Send Invite');
    return count > 0 ? `${base} (${count})` : base;
}

function _renderInviteModal() {
    const box = document.getElementById('inviteBox');
    if (!box) return;
    const worldName = _inviteOverride?.worldName || currentInstanceData?.worldName || t('invite.multi.current_instance', 'Current Instance');
    const worldThumb = _inviteOverride?.worldThumb || currentInstanceData?.worldThumb || '';
    const instanceType = _inviteOverride?.instanceType || currentInstanceData?.instanceType || '';
    const ageGate = _inviteOverride?.ageGate || currentInstanceData?.ageGate || false;
    const { cls: badgeCls, label: badgeLabel } = getInstanceBadge(instanceType || 'public');
    const typeBadge = instanceType && instanceType !== 'public'
        ? `<span class="vrcn-badge ${badgeCls}">${esc(badgeLabel)}</span>` : '';
    const ageGateBadge = ageGate
        ? `<span class="vrcn-badge" style="background:rgba(255,75,85,.15);color:var(--err);">${esc(t('worlds.instances.age_gated', 'Age Gated'))}</span>` : '';
    box.innerHTML = `
        ${renderModalBar(worldName, [modalCloseAction('closeInviteModal()')], { flush: true })}
        <div class="inv-world-banner" style="background-image:url('${esc(worldThumb)}')">
            <div class="inv-world-fade"></div>
            <div class="inv-world-info">
                ${(typeBadge || ageGateBadge) ? `<div style="margin-bottom:4px;display:flex;gap:4px;flex-wrap:wrap;">${typeBadge}${ageGateBadge}</div>` : ''}
                <div class="inv-world-name">${esc(worldName)}</div>
                <div style="font-size:calc(10px + var(--fs-off, 0px));color:rgba(255,255,255,.65);margin-top:3px;">${esc(t('invite.multi.subtitle', 'Invite to this instance'))}</div>
            </div>
        </div>
        <div class="inv-search-wrap">
            <span class="msi inv-search-icon">search</span>
            <input type="text" id="inviteSearch" class="inv-search-input" placeholder="${esc(t('invite.multi.search_placeholder', 'Search friends...'))}" oninput="_dbFilterInvite()">
        </div>
        <div id="inviteList" class="inv-list"></div>
        <div class="inv-footer">
            <span id="inviteSelCount" class="inv-sel-count"></span>
            <button id="inviteSendBtn" class="vrcn-button" onclick="sendMultiInvite()" disabled>${esc(_inviteSendButtonLabel(_inviteSelected.size))}</button>
        </div>
        <div id="inviteProgress" class="inv-progress-wrap" style="display:none;">
            <div class="inv-progress-track"><div id="inviteProgressBar" class="inv-progress-bar"></div></div>
            <div id="inviteProgressText" class="inv-progress-text"></div>
        </div>`;
    const search = document.getElementById('inviteSearch');
    if (search) search.value = _inviteFilter;
    renderInviteList(_inviteFilter);
    if (_inviteProgressState?.total > 0) {
        const prog = document.getElementById('inviteProgress');
        if (prog) prog.style.display = '';
        _applyInviteProgress(_inviteProgressState.done, _inviteProgressState.total, _inviteProgressState.success, _inviteProgressState.fail);
    }
}

function renderInviteList(filter) {
    const el = document.getElementById('inviteList');
    if (!el) return;

    const myLocBase = currentInstanceData?.location?.split('~')[0] || null;
    const _instUserIds = myLocBase ? new Set((currentInstanceData.users || []).map(u => u.id).filter(Boolean)) : new Set();

    const isGroupInvite = typeof _grpInvGroupId !== 'undefined' && _grpInvGroupId != null;
    const allFriends = (vrcFriendsData || []).map(f =>
        (_instUserIds.has(f.id) && (!f.location || f.location === 'private')) ? { ...f, location: currentInstanceData.location } : f
    ).filter(f => {
        if (!isGroupInvite && f.presence === 'offline') return false;
        if (filter) {
            if (!(f.displayName || '').toLowerCase().includes(filter.toLowerCase())) return false;
        }
        return true;
    });

    const instFriends = myLocBase ? allFriends.filter(f => f.location && f.location.split('~')[0] === myLocBase) : [];
    const friends = allFriends.filter(f => !myLocBase || !f.location || f.location.split('~')[0] !== myLocBase);

    if (allFriends.length === 0) {
        el.innerHTML = `<div class="inv-empty">${filter ? t('profiles.people.no_results', 'No results') : t('invite.multi.empty', 'No friends available to invite')}</div>`;
        _updateInviteFooter();
        return;
    }

    function card(f) {
        const sel = _inviteSelected.has(f.id);
        const trailing = `<div class="inv-check${sel ? ' inv-check-on' : ''}" style="margin-left:auto;flex-shrink:0;">${sel ? '<span class="msi" style="font-size:13px;line-height:1;">check</span>' : ''}</div>`;
        return renderUserItem(f, `toggleInviteSelect('${jsq(f.id || '')}')`, {
            cls: sel ? 'inv-row-sel' : '',
            trailing,
        });
    }

    let h = '';

    // When search is active: flat list, max 100 total regardless of category
    if (filter) {
        const capped = allFriends.slice(0, 100);
        if (allFriends.length > 100) {
            h += `<div class="inv-section-lbl" style="color:var(--tx2);font-size:calc(11px + var(--fs-off, 0px));font-weight:400;">${esc(tf('invite.multi.search.showing', { total: allFriends.length }, 'Showing 100 of {total} — refine search to see more'))}</div>`;
        }
        capped.forEach(f => h += card(f));
        el.innerHTML = h;
        _updateInviteFooter();
        return;
    }

    if (isGroupInvite) {
        // Group invite: flat list, all friends including offline, capped per section
        const onlineFriends = allFriends.filter(f => f.presence !== 'offline');
        const offlineFriends = allFriends.filter(f => f.presence === 'offline');
        if (onlineFriends.length > 0) {
            h += `<div class="inv-section-lbl">${esc(tf('invite.multi.section.online', { count: onlineFriends.length }, 'ONLINE - {count}'))}</div>`;
            onlineFriends.slice(0, 100).forEach(f => h += card(f));
        }
        if (offlineFriends.length > 0) {
            h += `<div class="inv-section-lbl">${esc(tf('invite.multi.section.offline', { count: offlineFriends.length }, 'OFFLINE - {count}'))}</div>`;
            offlineFriends.slice(0, 100).forEach(f => h += card(f));
        }
    } else {
        // Instance invite: grouped by location, capped per section
        if (instFriends.length > 0) {
            h += `<div class="inv-section-lbl">${esc(tf('invite.multi.section.in_instance', { count: instFriends.length }, 'IN-INSTANCE - {count}'))}</div>`;
            instFriends.slice(0, 100).forEach(f => h += card(f));
        }
        const gameFriends = friends.filter(f => f.presence === 'game');
        const webFriends = friends.filter(f => f.presence === 'web');
        if (gameFriends.length > 0) {
            h += `<div class="inv-section-lbl">${esc(tf('invite.multi.section.in_game', { count: gameFriends.length }, 'IN-GAME - {count}'))}</div>`;
            gameFriends.slice(0, 100).forEach(f => h += card(f));
        }
        if (webFriends.length > 0) {
            h += `<div class="inv-section-lbl">${esc(tf('invite.multi.section.web_active', { count: webFriends.length }, 'WEB / ACTIVE - {count}'))}</div>`;
            webFriends.slice(0, 100).forEach(f => h += card(f));
        }
    }

    el.innerHTML = h;
    _updateInviteFooter();
}

function filterInviteList() {
    const q = document.getElementById('inviteSearch')?.value || '';
    _inviteFilter = q;
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
        btn.textContent = _inviteSendButtonLabel(count);
    }
    if (lbl) lbl.textContent = count > 0 ? tf('invite.multi.selected', { count }, '{count} selected') : '';
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
    const msg = { action: 'vrcBatchInvite', userIds: ids };
    if (_inviteOverride?.location) msg.location = _inviteOverride.location;
    sendToCS(msg);
}

function handleBatchInviteProgress(payload) {
    _applyInviteProgress(payload.done, payload.total, payload.success, payload.fail);
    if (payload.done >= payload.total) {
        _inviteSending = false;
        setTimeout(() => _updateInviteFooter(), 1500);
    }
}

function _applyInviteProgress(done, total, success, fail) {
    _inviteProgressState = { done, total, success, fail };
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
            txt.textContent = tf('invite.multi.progress.sending', { current: done + 1, total }, 'Sending {current} of {total}...');
            txt.style.color = 'var(--tx2)';
        } else {
            const parts = [];
            if (success > 0) parts.push(tf('invite.multi.result.sent', { count: success }, '{count} sent'));
            if (fail > 0) parts.push(tf('invite.multi.result.failed', { count: fail }, '{count} failed'));
            txt.textContent = parts.length
                ? `${parts.join(', ')} - ${t('invite.multi.progress.done', 'Done!')}`
                : t('invite.multi.progress.done', 'Done!');
            txt.style.color = fail === 0 ? 'var(--ok)' : 'var(--accent)';
        }
    }
}

function rerenderInviteTranslations() {
    if (document.getElementById('modalInvite')?.style.display !== 'none') _renderInviteModal();
}

document.documentElement.addEventListener('languagechange', rerenderInviteTranslations);
