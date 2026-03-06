/* === Notifications === */
let _notifDismiss = null;

function toggleNotifPanel() {
    notifPanelOpen = !notifPanelOpen;
    document.getElementById('notifPanel').style.display = notifPanelOpen ? '' : 'none';
    if (notifPanelOpen) {
        refreshNotifications();
        setTimeout(() => {
            _notifDismiss = e => {
                const panel = document.getElementById('notifPanel');
                const btn   = document.getElementById('btnNotif');
                if (!panel?.contains(e.target) && !btn?.contains(e.target)) toggleNotifPanel();
            };
            document.addEventListener('click', _notifDismiss);
        }, 0);
    } else {
        if (_notifDismiss) { document.removeEventListener('click', _notifDismiss); _notifDismiss = null; }
    }
}

function refreshNotifications() {
    sendToCS({ action: 'vrcGetNotifications' });
}

function renderNotifications(list) {
    notifications = (list || []).filter(n => n.type !== 'boop'); // boops only in messenger
    const unseen = notifications.filter(n => !n.seen).length;
    const badge = document.getElementById('notifBadge');
    if (unseen > 0) { badge.textContent = unseen; badge.style.display = ''; }
    else badge.style.display = 'none';

    const el = document.getElementById('notifList');
    if (notifications.length === 0) { el.innerHTML = '<div class="empty-msg">No notifications</div>'; return; }
    el.innerHTML = notifications.map(n => {
        // Map notification type → icon + label
        let icon = 'notifications', label = n.type;
        switch (n.type) {
            // v1 types
            case 'friendRequest':          icon = 'person_add';          label = 'Friend Request'; break;
            case 'invite':                 icon = 'mail';                 label = 'World Invite'; break;
            case 'requestInvite':          icon = 'forward_to_inbox';    label = 'Invite Request'; break;
            case 'inviteResponse':         icon = 'reply';                label = 'Invite Response'; break;
            case 'requestInviteResponse':  icon = 'reply_all';            label = 'Invite Req. Response'; break;
            case 'votetokick':             icon = 'gavel';                label = 'Vote to Kick'; break;
            case 'boop':                   icon = 'waving_hand';          label = 'Boop'; break;
            case 'message':                icon = 'chat';                 label = 'Message'; break;
            // group types (v1 + v2)
            case 'group.announcement':     icon = 'campaign';             label = 'Group Announcement'; break;
            case 'group.invite':           icon = 'group_add';            label = 'Group Invite'; break;
            case 'group.joinRequest':      icon = 'group';                label = 'Group Join Request'; break;
            case 'group.informationRequest': icon = 'info';               label = 'Group Info Request'; break;
            case 'group.transfer':         icon = 'swap_horiz';           label = 'Group Transfer'; break;
            case 'group.informative':      icon = 'info';                 label = 'Group Info'; break;
            case 'group.post':             icon = 'article';              label = 'Group Post'; break;
            case 'group.event.created':    icon = 'event_note';           label = 'Group Event'; break;
            case 'group.event.starting':   icon = 'event_available';      label = 'Group Event Starting'; break;
            // v2-only types
            case 'avatarreview.success':   icon = 'check_circle';         label = 'Avatar Approved'; break;
            case 'avatarreview.failure':   icon = 'cancel';               label = 'Avatar Rejected'; break;
            case 'badge.earned':           icon = 'military_tech';        label = 'Badge Earned'; break;
            case 'economy.alert':          icon = 'account_balance_wallet'; label = 'Economy Alert'; break;
            case 'economy.received.gift':  icon = 'card_giftcard';        label = 'Gift Received'; break;
            case 'event.announcement':     icon = 'event';                label = 'Event'; break;
            case 'invite.instance.contentGated': icon = 'lock';           label = 'Content Gated Invite'; break;
            case 'moderation.contentrestriction': icon = 'shield';        label = 'Content Restriction'; break;
            case 'moderation.notice':      icon = 'policy';               label = 'Moderation Notice'; break;
            case 'moderation.report.closed': icon = 'task_alt';           label = 'Report Closed'; break;
            case 'moderation.warning.group': icon = 'warning';            label = 'Group Warning'; break;
            case 'promo.redeem':           icon = 'local_offer';          label = 'Promo Redeemed'; break;
            case 'text.adventure':         icon = 'auto_stories';         label = 'Text Adventure'; break;
            case 'vrcplus.gift':           icon = 'volunteer_activism';   label = 'VRC+ Gift'; break;
            default: if (n.type && n.type.startsWith('group.')) { icon = 'groups'; label = n.type.replace('group.', 'Group: '); }
        }
        const time = n.created_at ? new Date(n.created_at).toLocaleString() : '';
        const canAccept = ['friendRequest','invite','requestInvite','group.invite','group.joinRequest'].includes(n.type);
        const nid = esc(n.id);
        const senderLink = n.senderUserId
            ? `<strong style="cursor:pointer;" onclick="toggleNotifPanel();openFriendDetail('${esc(n.senderUserId)}')">${esc(n.senderUsername || n.senderUserId)}</strong>`
            : (n.senderUsername ? `<strong>${esc(n.senderUsername)}</strong>` : '');
        // VRChat REST API sends `details` as a stringified JSON string — parse it
        const det = typeof n.details === 'string' ? (() => { try { return JSON.parse(n.details); } catch { return {}; } })() : (n.details || {});
        let titleHtml, bodyHtml = '';
        if (n._v2 && n._title) {
            // v2: VRChat provides a pre-built title — use it directly
            titleHtml = esc(n._title);
            if (n.message) bodyHtml = `<div class="notif-msg">${esc(n.message)}</div>`;
        } else if (n.type === 'invite') {
            const worldName = det.worldName ? esc(det.worldName) : 'unknown world';
            const wid = det.worldId ? det.worldId.split(':')[0] : '';
            const worldLink = wid
                ? `<strong style="cursor:pointer;" onclick="toggleNotifPanel();openWorldDetail('${esc(wid)}')">${worldName}</strong>`
                : `<strong>${worldName}</strong>`;
            const msg = det.inviteMessage || '';
            titleHtml = `${senderLink} <span style="color:var(--tx2);font-weight:400;">invited you to</span> ${worldLink}`;
            if (msg) bodyHtml = `<div class="notif-msg">${esc(msg)}</div>`;
        } else if (n.type === 'requestInvite') {
            const msg = det.requestMessage || '';
            titleHtml = `${senderLink} <span style="color:var(--tx2);font-weight:400;">wants an invite</span>`;
            if (msg) bodyHtml = `<div class="notif-msg">${esc(msg)}</div>`;
        } else if (n.type === 'boop') {
            titleHtml = `${senderLink} <span style="color:var(--tx2);font-weight:400;">booped you</span>`;
        } else if (n.type === 'inviteResponse' || n.type === 'requestInviteResponse') {
            titleHtml = senderLink ? `${esc(label)} from ${senderLink}` : esc(label);
            const msg = det.responseMessage || det.requestMessage || det.inviteMessage || n.message || '';
            if (msg) bodyHtml = `<div class="notif-msg">${esc(msg)}</div>`;
        } else {
            titleHtml = senderLink ? `${esc(label)} from ${senderLink}` : esc(label);
            if (n.message) bodyHtml = `<div class="notif-msg">${esc(n.message)}</div>`;
        }
        return `<div class="notif-item ${n.seen && !canAccept ? 'notif-seen' : ''}">
            <span class="msi notif-icon" style="font-size:18px;">${icon}</span>
            <div class="notif-body">
                <div class="notif-title" style="display:flex;align-items:center;gap:5px;flex-wrap:wrap;">${titleHtml}</div>
                ${bodyHtml}
                <div class="notif-time">${time}</div>
            </div>
            <div class="notif-actions">
                ${canAccept ? `<button class="btn-f notif-accept-btn" onclick="acceptNotif('${nid}',this)" style="padding:2px 8px;font-size:10px;"><span class="msi" style="font-size:14px;">check</span> Accept</button>` : ''}
                ${(canAccept || !n.seen) ? `<button class="btn-fd notif-decline-btn" onclick="declineNotif('${nid}',this)" style="padding:2px 8px;font-size:10px;" title="Decline"><span class="msi" style="font-size:14px;">close</span></button>` : ''}
            </div>
        </div>`;
    }).join('');
}

function acceptNotif(notifId, btn) {
    if (btn) { btn.disabled = true; btn.textContent = '...'; }
    const n = notifications.find(x => x.id === notifId);
    const det = typeof n?.details === 'string' ? (() => { try { return JSON.parse(n.details); } catch { return {}; } })() : (n?.details || {});
    sendToCS({ action: 'vrcAcceptNotification', notifId, type: n?.type, details: det,
               _v2: n?._v2 || false, _data: n?._data || null, _link: n?._link || null,
               senderId: n?.senderUserId || null });
    // Remove immediately so the merge logic doesn't re-add it after REST refresh
    notifications = notifications.filter(x => x.id !== notifId);
    renderNotifications(notifications);
    setTimeout(() => refreshNotifications(), 1200);
}

function showLaunchModal(location, steamVrOpen) {
    closeLaunchModal();
    const el = document.createElement('div');
    el.className = 'launch-modal-overlay';
    el.innerHTML = `
        <div class="launch-modal">
            <div class="launch-modal-title">VRChat is not open</div>
            <div class="launch-modal-sub">How do you want to play?</div>
            <div class="launch-modal-btns">
                <button class="launch-btn${steamVrOpen ? ' launch-btn-primary' : ''}" onclick="launchAndJoin('${location}',true)">
                    <span class="msi">visibility</span> Play in VR
                </button>
                <button class="launch-btn${!steamVrOpen ? ' launch-btn-primary' : ''}" onclick="launchAndJoin('${location}',false)">
                    <span class="msi">desktop_windows</span> Play on Desktop
                </button>
            </div>
            <button class="launch-modal-cancel" onclick="closeLaunchModal()">Cancel</button>
        </div>`;
    el.addEventListener('click', e => { if (e.target === el) closeLaunchModal(); });
    document.body.appendChild(el);
    window._launchModalEl = el;
}

function launchAndJoin(location, vr) {
    sendToCS({ action: 'vrcLaunchAndJoin', location, vr });
    closeLaunchModal();
}

function closeLaunchModal() {
    const el = window._launchModalEl;
    if (!el) return;
    el.style.opacity = '0';
    el.style.transition = 'opacity .15s';
    setTimeout(() => el.remove(), 150);
    window._launchModalEl = null;
}

function declineNotif(notifId, btn) {
    if (btn) { btn.disabled = true; btn.textContent = '...'; }
    const n = notifications.find(x => x.id === notifId);
    const det = typeof n?.details === 'string' ? (() => { try { return JSON.parse(n.details); } catch { return {}; } })() : (n?.details || {});
    sendToCS({ action: 'vrcHideNotification', notifId,
               type: n?.type, _v2: n?._v2 || false,
               details: det, _data: n?._data || null, _link: n?._link || null,
               senderId: n?.senderUserId || null });
    // Remove locally immediately
    notifications = notifications.filter(x => x.id !== notifId);
    setTimeout(() => renderNotifications(notifications), 300);
}


/* === Current Instance (sidebar) === */
function renderCurrentInstance(data) {
    currentInstanceData = data;
    const el = document.getElementById('vrcInstanceArea');
    if (!el) return;

    if (!data || data.empty) { el.innerHTML = ''; return; }
    if (data.error) {
        el.innerHTML = `<div style="font-size:11px;color:var(--err);padding:6px 0;">${esc(data.error)}</div>`;
        return;
    }
    if (!data.worldName && !data.worldId) { el.innerHTML = ''; return; }

    const name = data.worldName || data.worldId || 'Unknown World';
    let users = data.users || [];
    let sourceLabel = '';

    if (data.playerSource === 'logfile' || data.playerSource === 'api') {
        sourceLabel = 'PLAYERS IN INSTANCE';
    }

    // If backend gave no users, cross-reference friends as last resort
    let isFriendsFallback = false;
    if (users.length === 0 && data.location && vrcFriendsData.length > 0) {
        const myLocBase = data.location.split('~')[0];
        users = vrcFriendsData.filter(f => {
            if (!f.location || f.location === 'private' || f.location === 'offline') return false;
            return f.location.split('~')[0] === myLocBase;
        });
        if (users.length > 0) {
            isFriendsFallback = true;
            sourceLabel = 'FRIENDS IN THIS INSTANCE';
        }
    }

    // Enrich players with friend profile pictures.
    // LogWatcher only gives displayName + userId (no images).
    // Build lookup maps from friends list to match images.
    if (users.length > 0 && vrcFriendsData.length > 0) {
        const byId = {};
        const byName = {};
        vrcFriendsData.forEach(f => {
            if (f.id) byId[f.id] = f;
            if (f.displayName) byName[f.displayName.toLowerCase()] = f;
        });
        users = users.map(u => {
            // Already has image? Keep it
            if (u.image) return u;
            // Match by userId
            const matchById = u.id ? byId[u.id] : null;
            if (matchById) return { ...u, image: matchById.image || '', id: matchById.id };
            // Match by displayName
            const matchByName = u.displayName ? byName[u.displayName.toLowerCase()] : null;
            if (matchByName) return { ...u, image: matchByName.image || '', id: matchByName.id || u.id };
            return u;
        });
    }

    const typeBadge = data.instanceType && data.instanceType !== 'public'
        ? `<span class="inst-type-badge">${esc(getInstanceBadge(data.instanceType).label)}</span>` : '';

    let usersHtml = '';
    if (users.length > 0) {
        usersHtml = `<div class="inst-users">
            <div style="font-size:10px;font-weight:700;color:var(--tx3);padding:6px 10px 2px;letter-spacing:.05em;">${sourceLabel} (${users.length})</div>
            ${users.map(u => {
                const hasImg = u.image && u.image.length > 5;
                const initial = (u.displayName || '?')[0].toUpperCase();
                const avatar = hasImg
                    ? `<div class="inst-user-av" style="background-image:url('${cssUrl(u.image)}')"></div>`
                    : `<div class="inst-user-av inst-user-av-letter">${esc(initial)}</div>`;
                const click = u.id ? ` onclick="openFriendDetail('${esc(u.id)}')"` : '';
                return `<div class="inst-user-row"${click}>${avatar}<span class="inst-user-name">${esc(u.displayName)}</span></div>`;
            }).join('')}
        </div>`;
    } else {
        usersHtml = `<div style="font-size:11px;color:var(--tx3);padding:8px 10px;">${data.nUsers || 0} players in instance</div>`;
    }

    el.innerHTML = `<div class="inst-card">
        <div class="inst-header" style="background-image:url('${cssUrl(data.worldThumb || '')}');cursor:pointer;" onclick="openWorldSearchDetail('${esc(data.worldId || '')}')">
            <div class="inst-header-fade"></div>
            ${typeBadge}
            <div class="inst-header-info">
                <div class="inst-world-name">${esc(name)}</div>
                <div class="inst-player-count"><span class="msi" style="font-size:13px;">person</span> ${data.nUsers || 0}${data.capacity ? '/' + data.capacity : ''}</div>
            </div>
        </div>
        ${usersHtml}
        <div class="inst-invite-bar">
            <button class="inst-invite-btn" onclick="openInviteModal()">
                <span class="msi" style="font-size:15px;">person_add</span> Invite Friends
            </button>
        </div>
    </div>
    <div style="font-size:10px;font-weight:700;color:var(--tx3);padding:8px 0 4px;letter-spacing:.05em;">FRIENDS</div>`;
}

let _instanceInfoTimer = null;
let _instancePollInterval = null;
function requestInstanceInfo() {
    if (!currentVrcUser) return;
    clearTimeout(_instanceInfoTimer);
    _instanceInfoTimer = setTimeout(() => sendToCS({ action: 'vrcGetCurrentInstance' }), 500);
    // Start passive 60s poll if not already running
    if (!_instancePollInterval) {
        _instancePollInterval = setInterval(() => {
            if (currentVrcUser) sendToCS({ action: 'vrcGetCurrentInstance' });
        }, 60000);
    }
}
