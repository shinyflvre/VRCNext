/* === Incoming Notification Cards (bottom-right) ===
 * Shows a rich toast card for each new unseen VRChat notification.
 * Called from messages.js on every vrcNotifications payload.
 */

const _shownNotifCardIds = new Set();

function showNotificationToasts(notifList) {
    const fresh = (notifList || []).filter(n => !n.seen && !_shownNotifCardIds.has(n.id) && n.type !== 'boop'); // boops only in messenger
    fresh.forEach(n => {
        _shownNotifCardIds.add(n.id);
        _showNotifCard(n);
    });
}

function _showNotifCard(n) {
    const area = document.getElementById('notifCardArea');
    if (!area) return;

    const det = typeof n.details === 'string'
        ? (() => { try { return JSON.parse(n.details); } catch { return {}; } })()
        : (n.details || {});

    let icon = 'notifications', accentColor = 'var(--accent)';
    switch (n.type) {
        case 'friendRequest':          icon = 'person_add';       accentColor = 'var(--ok)';     break;
        case 'invite':                 icon = 'mail';              accentColor = 'var(--accent)'; break;
        case 'requestInvite':          icon = 'forward_to_inbox';  accentColor = 'var(--accent)'; break;
        case 'inviteResponse':         icon = 'reply';             accentColor = 'var(--accent)'; break;
        case 'requestInviteResponse':  icon = 'reply_all';         accentColor = 'var(--accent)'; break;
        case 'votetokick':             icon = 'gavel';             accentColor = 'var(--err)';    break;
        case 'group.announcement':     icon = 'campaign';          accentColor = 'var(--accent)'; break;
        case 'group.invite':           icon = 'group_add';         accentColor = 'var(--ok)';     break;
        case 'group.joinRequest':      icon = 'group';             accentColor = 'var(--accent)'; break;
        default:
            if (n.type?.startsWith('group.')) icon = 'groups';
    }

    const canAccept = ['friendRequest','invite','requestInvite','group.invite','group.joinRequest'].includes(n.type);
    const sender = esc(n.senderUsername || '?');

    let titleHtml, subText = '';
    if (n._v2 && n._title) {
        // v2: VRChat pre-builds the title with the correct sender name — use it directly
        titleHtml = esc(n._title);
        subText = n.message || '';
    } else if (n.type === 'invite') {
        const worldName = det.worldName ? esc(det.worldName) : 'a world';
        titleHtml = `<strong>${sender}</strong> <span>invited you to</span> <strong>${worldName}</strong>`;
        subText = det.inviteMessage || '';
    } else if (n.type === 'requestInvite') {
        titleHtml = `<strong>${sender}</strong> <span>wants an invite</span>`;
        subText = det.requestMessage || '';
    } else if (n.type === 'friendRequest') {
        titleHtml = `<strong>${sender}</strong> <span>sent you a friend request</span>`;
    } else if (n.type === 'group.invite') {
        titleHtml = `<strong>${sender}</strong> <span>invited you to a group</span>`;
    } else if (n.type === 'group.joinRequest') {
        titleHtml = `<strong>${sender}</strong> <span>wants to join your group</span>`;
    } else if (n.type === 'inviteResponse') {
        titleHtml = `<strong>${sender}</strong> <span>responded to your invite</span>`;
        subText = det.responseMessage || det.requestMessage || det.inviteMessage || n.message || '';
    } else if (n.type === 'requestInviteResponse') {
        titleHtml = `<strong>${sender}</strong> <span>responded to your invite request</span>`;
        subText = det.responseMessage || det.requestMessage || n.message || '';
    } else if (n.type === 'group.announcement') {
        titleHtml = `<span>Group announcement</span>`;
        subText = n.message || '';
    } else {
        titleHtml = `<strong>${sender}</strong>`;
        subText = n.message || '';
    }

    const nid = esc(n.id);
    const card = document.createElement('div');
    card.className = 'nc-card';
    card.innerHTML = `
        <div class="nc-inner">
            <span class="msi nc-icon" style="color:${accentColor};">${icon}</span>
            <div class="nc-body">
                <div class="nc-title">${titleHtml}</div>
                ${subText ? `<div class="nc-sub">${esc(subText)}</div>` : ''}
                ${canAccept ? `<button class="vrcn-notify-button primary" onclick="_acceptNotifCard('${nid}',this)"><span class="msi">check</span> Accept</button>` : ''}
            </div>
            <button class="nc-close-btn" onclick="_dismissNotifCard(this.closest('.nc-card'))"><span class="msi" style="font-size:15px;">close</span></button>
        </div>
        <div class="nc-timer"><div class="nc-timer-bar" style="background:${accentColor};"></div></div>`;

    area.appendChild(card);

    // Slide in
    requestAnimationFrame(() => {
        card.classList.add('nc-visible');
        // Start timer bar (GPU-accelerated scaleX)
        const bar = card.querySelector('.nc-timer-bar');
        bar.style.transition = 'transform 8s linear';
        requestAnimationFrame(() => { bar.style.transform = 'scaleX(0)'; });
    });

    const timer = setTimeout(() => _dismissNotifCard(card), 8200);
    card._ncTimer = timer;
}

function _dismissNotifCard(card) {
    if (!card || !card.parentNode) return;
    clearTimeout(card._ncTimer);
    card.classList.remove('nc-visible');
    setTimeout(() => { if (card.parentNode) card.remove(); }, 350);
}

function _acceptNotifCard(notifId, btn) {
    if (btn) { btn.disabled = true; btn.innerHTML = '<span class="msi" style="font-size:13px;">hourglass_empty</span>'; }
    const n = (typeof notifications !== 'undefined' ? notifications : []).find(x => x.id === notifId);
    const det = typeof n?.details === 'string'
        ? (() => { try { return JSON.parse(n.details); } catch { return {}; } })()
        : (n?.details || {});
    sendToCS({ action: 'vrcAcceptNotification', notifId, type: n?.type, details: det,
               _v2: n?._v2 || false, _data: n?._data || null, _link: n?._link || null,
               senderId: n?.senderUserId || null });
    // Remove immediately so the merge logic doesn't re-add it after REST refresh
    if (typeof notifications !== 'undefined')
        notifications = notifications.filter(x => x.id !== notifId);
    const card = btn?.closest('.nc-card');
    if (card) setTimeout(() => _dismissNotifCard(card), 900);
    setTimeout(() => { if (typeof refreshNotifications === 'function') refreshNotifications(); }, 1200);
}
