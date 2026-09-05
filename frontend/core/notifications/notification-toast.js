/* === Incoming Notification Cards (bottom-right) ===
 * Shows a rich toast card for each new unseen VRChat notification.
 * Called from messages.js on every vrcNotifications payload.
 */

const _shownNotifCardIds = new Set();
const _pendingTrayNotifs = {};

function showNotificationToasts(notifList) {
    const all = (notifList || []).filter(n => !n.seen && !_shownNotifCardIds.has(n.id));
    const boops = all.filter(n => n.type === 'boop');
    const fresh = all.filter(n => n.type !== 'boop'); // boops only in messenger
    if (boops.length) playMessageSound();
    if (fresh.length) playNotificationSound();
    fresh.forEach(n => {
        _shownNotifCardIds.add(n.id);
        _showNotifCard(n);
    });
    boops.forEach(n => _shownNotifCardIds.add(n.id));
}

function _showNotifCard(n) {
    const area = document.getElementById('notifCardArea');
    if (!area) return;

    const det = typeof n.details === 'string'
        ? (() => { try { return JSON.parse(n.details); } catch { return {}; } })()
        : (n.details || {});

    let icon = 'notifications';
    let accentColor = 'var(--accent)';
    switch (n.type) {
        case 'friendRequest':          icon = 'person_add';       accentColor = 'var(--ok)';     break;
        case 'invite':                 icon = 'mail';             accentColor = 'var(--accent)'; break;
        case 'requestInvite':          icon = 'forward_to_inbox'; accentColor = 'var(--accent)'; break;
        case 'inviteResponse':         icon = 'reply';            accentColor = 'var(--accent)'; break;
        case 'requestInviteResponse':  icon = 'reply_all';        accentColor = 'var(--accent)'; break;
        case 'votetokick':             icon = 'gavel';            accentColor = 'var(--err)';    break;
        case 'group.announcement':     icon = 'campaign';         accentColor = 'var(--accent)'; break;
        case 'instance.announcement':  icon = 'campaign';         accentColor = 'var(--accent)'; break;
        case 'group.invite':           icon = 'group_add';        accentColor = 'var(--ok)';     break;
        case 'group.joinRequest':      icon = 'group';            accentColor = 'var(--accent)'; break;
        default:
            if (n.type?.startsWith('group.')) icon = 'groups';
    }

    const canAccept = ['friendRequest', 'invite', 'requestInvite', 'group.invite', 'group.joinRequest'].includes(n.type);
    const sender = esc(n.senderUsername || '?');

    let titleHtml;
    let subText = '';
    if (n._v2 && n._title) {
        titleHtml = esc(n._title);
        subText = n.message || '';
    } else if (n.type === 'invite') {
        const worldName = det.worldName ? esc(det.worldName) : esc(t('notifications.world_fallback', 'a world'));
        titleHtml = `<strong>${sender}</strong> <span>${esc(t('notifications.title.invited_you_to', 'invited you to'))}</span> <strong>${worldName}</strong>`;
        subText = det.inviteMessage || '';
    } else if (n.type === 'requestInvite') {
        titleHtml = `<strong>${sender}</strong> <span>${esc(t('notifications.title.wants_invite', 'wants an invite'))}</span>`;
        subText = det.requestMessage || '';
    } else if (n.type === 'friendRequest') {
        titleHtml = `<strong>${sender}</strong> <span>${esc(t('notifications.title.friend_request_received', 'sent you a friend request'))}</span>`;
    } else if (n.type === 'group.invite') {
        titleHtml = `<strong>${sender}</strong> <span>${esc(t('notifications.title.group_invited_you', 'invited you to a group'))}</span>`;
    } else if (n.type === 'group.joinRequest') {
        titleHtml = `<strong>${sender}</strong> <span>${esc(t('notifications.title.group_join_request', 'wants to join your group'))}</span>`;
    } else if (n.type === 'inviteResponse') {
        titleHtml = `<strong>${sender}</strong> <span>${esc(t('notifications.title.invite_response', 'responded to your invite'))}</span>`;
        subText = det.responseMessage || det.requestMessage || det.inviteMessage || n.message || '';
    } else if (n.type === 'requestInviteResponse') {
        titleHtml = `<strong>${sender}</strong> <span>${esc(t('notifications.title.invite_request_response', 'responded to your invite request'))}</span>`;
        subText = det.responseMessage || det.requestMessage || n.message || '';
    } else if (n.type === 'group.announcement') {
        titleHtml = `<span>${esc(t('notifications.title.group_announcement', 'Group announcement'))}</span>`;
        subText = n.message || '';
    } else if (n.type === 'instance.announcement') {
        titleHtml = `<span>${esc(t('notifications.title.instance_announcement', 'Instance announcement'))}</span>`;
        subText = n.message || '';
    } else {
        titleHtml = `<strong>${sender}</strong>`;
        subText = n.message || '';
    }

    // Resolve avatar image
    const nImg = (typeof _resolveNotifImage === 'function') ? _resolveNotifImage(n) : (n._image || '');
    const hasImg = nImg && nImg.length > 5;
    const avatarHtml = hasImg
        ? `<div class="nc-avatar" style="background-image:url('${cssUrl(imgThumb(nImg, 96))}')"><span class="msi nc-avatar-badge" style="color:${accentColor};">${icon}</span></div>`
        : `<span class="msi nc-icon" style="color:${accentColor};">${icon}</span>`;

    const nid = esc(n.id);
    const card = document.createElement('div');
    card.className = 'nc-card';
    card.dataset.notifId = n.id;
    card.innerHTML = `
        <div class="nc-inner">
            ${avatarHtml}
            <div class="nc-body">
                <div class="nc-title">${titleHtml}</div>
                ${subText ? `<div class="nc-sub">${esc(subText)}</div>` : ''}
                ${canAccept ? `<button class="vrcn-notify-button primary" onclick="_acceptNotifCard('${nid}',this)"><span class="msi">check</span> ${esc(t('notifications.actions.accept', 'Accept'))}</button>` : ''}
            </div>
            <button class="nc-close-btn" onclick="_dismissNotifCard(this.closest('.nc-card'))" title="${esc(t('common.close', 'Close'))}"><span class="msi" style="font-size:15px;">close</span></button>
        </div>
        <div class="nc-timer"><div class="nc-timer-bar" style="background:${accentColor};"></div></div>`;

    area.appendChild(card);

    requestAnimationFrame(() => {
        card.classList.add('nc-visible');
        const bar = card.querySelector('.nc-timer-bar');
        bar.style.transition = 'transform 8s linear';
        requestAnimationFrame(() => { bar.style.transform = 'scaleX(0)'; });
    });

    const timer = setTimeout(() => _dismissNotifCard(card), 8200);
    card._ncTimer = timer;

    // Mirror to system tray — defer until vrcNotifImageUpdate provides the resolved localhost image URL
    if (document.getElementById('setMinimizeToTray')?.checked && document.getElementById('setTrayNotifications')?.checked) {
        let plainTitle = '';
        if (n._v2 && n._title) {
            plainTitle = n._title;
        } else if (n.type === 'invite') {
            const worldName = det.worldName || t('notifications.world_fallback', 'a world');
            plainTitle = `${n.senderUsername || '?'} ${t('notifications.title.invited_you_to', 'invited you to')} ${worldName}`;
        } else if (n.type === 'requestInvite') {
            plainTitle = `${n.senderUsername || '?'} ${t('notifications.title.wants_invite', 'wants an invite')}`;
        } else if (n.type === 'friendRequest') {
            plainTitle = `${n.senderUsername || '?'} ${t('notifications.title.friend_request_received', 'sent you a friend request')}`;
        } else if (n.type === 'group.invite') {
            plainTitle = `${n.senderUsername || '?'} ${t('notifications.title.group_invited_you', 'invited you to a group')}`;
        } else if (n.type === 'group.joinRequest') {
            plainTitle = `${n.senderUsername || '?'} ${t('notifications.title.group_join_request', 'wants to join your group')}`;
        } else if (n.type === 'group.announcement') {
            plainTitle = t('notifications.title.group_announcement', 'Group announcement');
        } else if (n.type === 'instance.announcement') {
            plainTitle = t('notifications.title.instance_announcement', 'Instance announcement');
        } else {
            plainTitle = n.senderUsername || notifToastLabel(n.type);
        }
        const accentKey = accentColor === 'var(--ok)' ? 'ok' : accentColor === 'var(--err)' ? 'err' : 'accent';
        _pendingTrayNotifs[n.id] = { title: plainTitle, subtitle: subText, accentKey };
        // Fallback: fire after 5s with whatever image is available if vrcNotifImageUpdate never arrives
        setTimeout(() => {
            if (!_pendingTrayNotifs[n.id]) return;
            delete _pendingTrayNotifs[n.id];
            sendToCS({ action: 'trayNotification', title: plainTitle, subtitle: subText, imageUrl: nImg, accentKey });
        }, 5000);
    }
}

function _dismissNotifCard(card) {
    if (!card || !card.parentNode) return;
    clearTimeout(card._ncTimer);
    card.classList.remove('nc-visible');
    setTimeout(() => { if (card.parentNode) card.remove(); }, 350);
}

// context menu for friend toast right-click options
let _fotCtxCard = null;
let _fotCtxEl   = null;

function _fotShowCtx(card, uid, name, currentLevel, ev) {
    ev.preventDefault();
    ev.stopPropagation();

    // remove any existing menu
    _fotCtxEl?.remove();
    _fotCtxEl = document.createElement('div');
    _fotCtxEl.className = 'fot-ctx-menu';

    const items = [
        { label: t('notifications.friend_toast.always_notify', 'Always notify'), icon: 'notifications_active', level: 1,  active: currentLevel === 1  },
        { label: t('notifications.friend_toast.never_notify',  'Never notify'),  icon: 'notifications_off',    level: -1, active: currentLevel === -1 },
        { label: t('notifications.friend_toast.default',       'Default'),       icon: 'notifications',        level: 0,  active: currentLevel === 0  },
    ];
    _fotCtxEl.innerHTML = `<div class="fot-ctx-name">${esc(name)}</div>` + items.map(it =>
        `<div class="fot-ctx-item${it.active ? ' active' : ''}" onclick="_fotSetAlert('${jsq(uid)}',${it.level},this)">
            <span class="msi" style="font-size:15px;">${it.icon}</span>${esc(it.label)}
        </div>`
    ).join('');

    document.body.appendChild(_fotCtxEl);

    // position near cursor, keep on screen
    const mw = 170, mh = 110;
    let x = ev.clientX, y = ev.clientY;
    if (x + mw > window.innerWidth)  x = window.innerWidth  - mw - 8;
    if (y + mh > window.innerHeight) y = window.innerHeight - mh - 8;
    _fotCtxEl.style.left = x + 'px';
    _fotCtxEl.style.top  = y + 'px';

    _fotCtxCard = card;

    const dismiss = e => { if (!_fotCtxEl?.contains(e.target)) { _fotCtxEl?.remove(); _fotCtxEl = null; document.removeEventListener('mousedown', dismiss); } };
    setTimeout(() => document.addEventListener('mousedown', dismiss), 0);
}

function _fotSetAlert(uid, level, itemEl) {
    sendToCS({ action: 'vrcSetFriendAlert', userId: uid, level });
    // update active state visually
    itemEl.closest('.fot-ctx-menu')?.querySelectorAll('.fot-ctx-item').forEach(el => el.classList.remove('active'));
    itemEl.classList.add('active');
    setTimeout(() => { _fotCtxEl?.remove(); _fotCtxEl = null; }, 350);
}

function _fotHandleAlertState(payload) {
    if (!window._friendAlertCache) window._friendAlertCache = {};
    const level = payload.level ?? 0;
    if (payload.userId) window._friendAlertCache[payload.userId] = level;
    if (_fotCtxCard) _fotCtxCard.dataset.alertLevel = level;
    if (window._pendingFotSubmenuUpdate?.userId === payload.userId) {
        window._pendingFotSubmenuUpdate.fn(level);
        window._pendingFotSubmenuUpdate = null;
    }
}

function _showFriendOnlineCard(data) {
    const area = document.getElementById('notifCardArea');
    if (!area) return;

    const name  = esc(data.displayName || data.userId || '?');
    const img   = data.image || '';
    const uid   = data.userId || '';
    const level = data.alertLevel ?? 0;
    const accentColor = 'var(--ok)';

    const avatarHtml = img
        ? `<div class="nc-avatar" style="background-image:url('${cssUrl(imgThumb(img, 96))}')"><span class="msi nc-avatar-badge" style="color:${accentColor};">wifi</span></div>`
        : `<span class="msi nc-icon" style="color:${accentColor};">wifi</span>`;

    const card = document.createElement('div');
    card.className = 'nc-card';
    card.dataset.alertLevel = level;
    card.innerHTML = `
        <div class="nc-inner" style="cursor:pointer;" onclick="_dismissNotifCard(this.closest('.nc-card'));navOpenModal('friend','${jsq(uid)}','${jsq(data.displayName || '')}')">
            ${avatarHtml}
            <div class="nc-body">
                <div class="nc-title"><strong>${name}</strong> <span style="color:var(--tx1);font-weight:400;">${esc(t('notifications.friend_toast.came_online', 'came online'))}</span></div>
            </div>
            <button class="nc-close-btn" onclick="event.stopPropagation();_dismissNotifCard(this.closest('.nc-card'))" title="${esc(t('common.close', 'Close'))}"><span class="msi" style="font-size:15px;">close</span></button>
        </div>
        <div class="nc-timer"><div class="nc-timer-bar" style="background:${accentColor};"></div></div>`;

    card.addEventListener('contextmenu', ev => _fotShowCtx(card, uid, data.displayName || uid, parseInt(card.dataset.alertLevel ?? '0'), ev));

    area.appendChild(card);
    playNotificationSound();

    requestAnimationFrame(() => {
        card.classList.add('nc-visible');
        const bar = card.querySelector('.nc-timer-bar');
        bar.style.transition = 'transform 5s linear';
        requestAnimationFrame(() => { bar.style.transform = 'scaleX(0)'; });
    });

    card._ncTimer = setTimeout(() => _dismissNotifCard(card), 5200);

    if (document.getElementById('setMinimizeToTray')?.checked && document.getElementById('setTrayNotifications')?.checked) {
        sendToCS({ action: 'trayNotification', title: data.displayName || uid, subtitle: t('notifications.friend_toast.came_online', 'came online'), imageUrl: img, accentKey: 'ok' });
    }
}

function _acceptNotifCard(notifId, btn) {
    if (btn) {
        btn.disabled = true;
        btn.innerHTML = '<span class="msi" style="font-size:13px;">hourglass_empty</span>';
    }
    const n = (typeof notifications !== 'undefined' ? notifications : []).find(x => x.id === notifId);
    const det = typeof n?.details === 'string'
        ? (() => { try { return JSON.parse(n.details); } catch { return {}; } })()
        : (n?.details || {});
    sendToCS({
        action: 'vrcAcceptNotification',
        notifId,
        type: n?.type,
        details: det,
        _v2: n?._v2 || false,
        _data: n?._data || null,
        _link: n?._link || null,
        senderId: n?.senderUserId || null
    });
    if (typeof notifications !== 'undefined') {
        notifications = notifications.filter(x => x.id !== notifId);
    }
    const card = btn?.closest('.nc-card');
    if (card) setTimeout(() => _dismissNotifCard(card), 900);
    setTimeout(() => { if (typeof refreshNotifications === 'function') refreshNotifications(); }, 1200);
}
