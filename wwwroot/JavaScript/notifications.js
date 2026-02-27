/* === Notifications === */
function toggleNotifPanel() {
    notifPanelOpen = !notifPanelOpen;
    document.getElementById('notifPanel').style.display = notifPanelOpen ? '' : 'none';
    if (notifPanelOpen) refreshNotifications();
}

function refreshNotifications() {
    sendToCS({ action: 'vrcGetNotifications' });
}

function renderNotifications(list) {
    notifications = list || [];
    const unseen = notifications.filter(n => !n.seen).length;
    const badge = document.getElementById('notifBadge');
    if (unseen > 0) { badge.textContent = unseen; badge.style.display = ''; }
    else badge.style.display = 'none';

    const el = document.getElementById('notifList');
    if (notifications.length === 0) { el.innerHTML = '<div class="empty-msg">No notifications</div>'; return; }
    el.innerHTML = notifications.map(n => {
        // Map all VRChat notification types
        let icon = 'notifications', label = n.type;
        switch (n.type) {
            case 'friendRequest': icon = 'person_add'; label = 'Friend Request'; break;
            case 'invite': icon = 'mail'; label = 'World Invite'; break;
            case 'requestInvite': icon = 'forward_to_inbox'; label = 'Invite Request'; break;
            case 'inviteResponse': icon = 'reply'; label = 'Invite Response'; break;
            case 'requestInviteResponse': icon = 'reply_all'; label = 'Invite Req. Response'; break;
            case 'votetokick': icon = 'gavel'; label = 'Vote to Kick'; break;
            case 'group.announcement': icon = 'campaign'; label = 'Group Announcement'; break;
            case 'group.invite': icon = 'group_add'; label = 'Group Invite'; break;
            case 'group.joinRequest': icon = 'group'; label = 'Group Join Request'; break;
            case 'group.informationRequest': icon = 'info'; label = 'Group Info Request'; break;
            case 'group.transfer': icon = 'swap_horiz'; label = 'Group Transfer'; break;
            default: if (n.type.startsWith('group.')) { icon = 'groups'; label = n.type.replace('group.', 'Group: '); }
        }
        const time = n.created_at ? new Date(n.created_at).toLocaleString() : '';
        const canAccept = ['friendRequest','invite','requestInvite','group.invite','group.joinRequest'].includes(n.type);
        const nid = esc(n.id);
        return `<div class="notif-item ${n.seen && !canAccept ? 'notif-seen' : ''}">
            <span class="msi notif-icon" style="font-size:18px;">${icon}</span>
            <div class="notif-body">
                <div class="notif-title">${esc(label)} from <strong>${esc(n.senderUsername)}</strong></div>
                ${n.message ? `<div class="notif-msg">${esc(n.message)}</div>` : ''}
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
    sendToCS({ action: 'vrcAcceptNotification', notifId });
    const n = notifications.find(x => x.id === notifId);
    if (n) n.seen = true;
    setTimeout(() => refreshNotifications(), 800);
}

function declineNotif(notifId, btn) {
    if (btn) { btn.disabled = true; btn.textContent = '...'; }
    sendToCS({ action: 'vrcHideNotification', notifId });
    // Remove locally immediately
    notifications = notifications.filter(x => x.id !== notifId);
    setTimeout(() => renderNotifications(notifications), 300);
}

function showNotifToast(type, sender, message) {
    const area = document.getElementById('notifToastArea');
    const icon = type === 'invite' ? 'mail' : type === 'friendRequest' ? 'person_add' : 'notifications';
    const label = type === 'invite' ? 'Invite' : type === 'friendRequest' ? 'Friend Request' : type;
    const toast = document.createElement('div');
    toast.className = 'notif-toast';
    toast.innerHTML = `<span class="msi" style="font-size:18px;color:var(--accent);">${icon}</span><div><strong>${esc(label)}</strong><div style="font-size:11px;color:var(--tx3);">from ${esc(sender)}${message ? ': '+esc(message) : ''}</div></div>`;
    area.appendChild(toast);
    setTimeout(() => toast.classList.add('notif-toast-show'), 10);
    setTimeout(() => { toast.classList.remove('notif-toast-show'); setTimeout(() => toast.remove(), 300); }, 5000);
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
                    ? `<div class="inst-user-av" style="background-image:url('${u.image}')"></div>`
                    : `<div class="inst-user-av inst-user-av-letter">${esc(initial)}</div>`;
                const click = u.id ? ` onclick="openFriendDetail('${esc(u.id)}')"` : '';
                return `<div class="inst-user-row"${click}>${avatar}<span class="inst-user-name">${esc(u.displayName)}</span></div>`;
            }).join('')}
        </div>`;
    } else {
        usersHtml = `<div style="font-size:11px;color:var(--tx3);padding:8px 10px;">${data.nUsers || 0} players in instance</div>`;
    }

    el.innerHTML = `<div class="inst-card">
        <div class="inst-header" style="background-image:url('${data.worldThumb || ''}');cursor:pointer;" onclick="openWorldSearchDetail('${esc(data.worldId || '')}')">
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

function requestInstanceInfo() {
    if (currentVrcUser) sendToCS({ action: 'vrcGetCurrentInstance' });
}
