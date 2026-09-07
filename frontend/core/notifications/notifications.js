/* === Notifications === */
let _notifDismiss = null;
let _notifTab = 'current';
let _notifNoDecline = false;

function _updatePillIndicator() {
    const group     = document.getElementById('notifPillGroup');
    const indicator = document.getElementById('notifPillIndicator');
    const active    = group?.querySelector('.vrcn-mini-pill.active');
    if (!group || !indicator || !active) return;
    const gRect = group.getBoundingClientRect();
    const aRect = active.getBoundingClientRect();
    indicator.style.width     = aRect.width + 'px';
    indicator.style.transform = `translateX(${aRect.left - gRect.left - 2}px)`;
}

function setNotifTab(tab) {
    _notifTab = tab;
    _notifNoDecline = tab === 'hidden';
    notifications = [];
    ['current', 'hidden'].forEach(t => {
        const el = document.getElementById('notifTab' + t.charAt(0).toUpperCase() + t.slice(1));
        if (el) el.classList.toggle('active', t === tab);
    });
    _updatePillIndicator();
    refreshNotifications();
}

function toggleNotifPanel() {
    notifPanelOpen = !notifPanelOpen;
    const panel = document.getElementById('notifPanel');
    if (notifPanelOpen) {
        panel.style.display = '';
        requestAnimationFrame(() => { panel.classList.add('panel-open'); _updatePillIndicator(); });
        refreshNotifications();
        setTimeout(() => {
            _notifDismiss = e => {
                const panel = document.getElementById('notifPanel');
                const btn   = document.getElementById('btnNotif');
                // Use composedPath() so the check survives DOM mutations caused by
                // acceptNotif/declineNotif re-rendering the list before bubbling completes
                const path = e.composedPath();
                if (!path.includes(panel) && !path.includes(btn)) toggleNotifPanel();
            };
            document.addEventListener('click', _notifDismiss);
        }, 0);
    } else {
        if (_notifDismiss) { document.removeEventListener('click', _notifDismiss); _notifDismiss = null; }
        panel.classList.remove('panel-open');
        setTimeout(() => { if (!notifPanelOpen) panel.style.display = 'none'; }, 90);
    }
}

function refreshNotifications() {
    sendToCS({ action: _notifTab === 'hidden' ? 'vrcGetHiddenNotifications' : 'vrcGetNotifications' });
}

function clearAllNotifications() {
    if (!notifications.length) return;
    renderNotifications([]);
    sendToCS({ action: 'vrcClearNotifications' });
}

function openNotificationsTimeline() {
    if (notifPanelOpen) toggleNotifPanel();
    showTab(12);
    if (typeof setTlMode === 'function') setTlMode('personal');
    if (typeof setTlFilter === 'function') setTlFilter('notification');
}

function getNotificationTypeMeta(type) {
    switch (type) {
        case 'friendRequest': return { icon: 'person_add', label: t('notifications.types.friend_request', 'Friend Request') };
        case 'invite': return { icon: 'mail', label: t('notifications.types.world_invite', 'World Invite') };
        case 'requestInvite': return { icon: 'forward_to_inbox', label: t('notifications.types.invite_request', 'Invite Request') };
        case 'inviteResponse': return { icon: 'reply', label: t('notifications.types.invite_response', 'Invite Response') };
        case 'requestInviteResponse': return { icon: 'reply_all', label: t('notifications.types.invite_request_response', 'Invite Req. Response') };
        case 'votetokick': return { icon: 'gavel', label: t('notifications.types.vote_to_kick', 'Vote to Kick') };
        case 'boop': return { icon: 'waving_hand', label: t('notifications.types.boop', 'Boop') };
        case 'message': return { icon: 'chat', label: t('notifications.types.message', 'Message') };
        case 'group.announcement': return { icon: 'campaign', label: t('notifications.types.group_announcement', 'Group Announcement') };
        case 'instance.announcement': return { icon: 'campaign', label: t('notifications.types.instance_announcement', 'Instance Announcement') };
        case 'group.invite': return { icon: 'group_add', label: t('notifications.types.group_invite', 'Group Invite') };
        case 'group.joinRequest': return { icon: 'group', label: t('notifications.types.group_join_request', 'Group Join Request') };
        case 'group.informationRequest': return { icon: 'info', label: t('notifications.types.group_info_request', 'Group Info Request') };
        case 'group.transfer': return { icon: 'swap_horiz', label: t('notifications.types.group_transfer', 'Group Transfer') };
        case 'group.informative': return { icon: 'info', label: t('notifications.types.group_info', 'Group Info') };
        case 'group.post': return { icon: 'article', label: t('notifications.types.group_post', 'Group Post') };
        case 'group.event.created': return { icon: 'event_note', label: t('notifications.types.group_event', 'Group Event') };
        case 'group.event.starting': return { icon: 'event_available', label: t('notifications.types.group_event_starting', 'Group Event Starting') };
        case 'avatarreview.success': return { icon: 'check_circle', label: t('notifications.types.avatar_approved', 'Avatar Approved') };
        case 'avatarreview.failure': return { icon: 'cancel', label: t('notifications.types.avatar_rejected', 'Avatar Rejected') };
        case 'badge.earned': return { icon: 'military_tech', label: t('notifications.types.badge_earned', 'Badge Earned') };
        case 'economy.alert': return { icon: 'account_balance_wallet', label: t('notifications.types.economy_alert', 'Economy Alert') };
        case 'economy.received.gift': return { icon: 'card_giftcard', label: t('notifications.types.gift_received', 'Gift Received') };
        case 'event.announcement': return { icon: 'event', label: t('notifications.types.event', 'Event') };
        case 'invite.instance.contentGated': return { icon: 'lock', label: t('notifications.types.content_gated_invite', 'Content Gated Invite') };
        case 'moderation.contentrestriction': return { icon: 'shield', label: t('notifications.types.content_restriction', 'Content Restriction') };
        case 'moderation.notice': return { icon: 'policy', label: t('notifications.types.moderation_notice', 'Moderation Notice') };
        case 'moderation.report.closed': return { icon: 'task_alt', label: t('notifications.types.report_closed', 'Report Closed') };
        case 'moderation.warning.group': return { icon: 'warning', label: t('notifications.types.group_warning', 'Group Warning') };
        case 'promo.redeem': return { icon: 'local_offer', label: t('notifications.types.promo_redeemed', 'Promo Redeemed') };
        case 'text.adventure': return { icon: 'auto_stories', label: t('notifications.types.text_adventure', 'Text Adventure') };
        case 'vrcplus.gift': return { icon: 'volunteer_activism', label: t('notifications.types.vrcplus_gift', 'VRC+ Gift') };
        default:
            if (type && type.startsWith('group.')) {
                return {
                    icon: 'groups',
                    label: tf('notifications.types.group_default', { name: type.replace('group.', '') }, 'Group: {name}')
                };
            }
            return { icon: 'notifications', label: type || t('common.notifications', 'Notifications') };
    }
}

function getNotificationTime(createdAt) {
    if (!createdAt) return '';
    const dt = new Date(createdAt);
    if (isNaN(dt)) return '';
    return fmtShortDate(dt) + ' ' + fmtTime(dt);
}

function getNotificationAge(createdAt) {
    if (!createdAt) return '';
    const dt = new Date(createdAt);
    if (isNaN(dt)) return '';
    const diff = Date.now() - dt.getTime();
    if (diff < 60000)     return t('profiles.last_seen.just_now', 'Just now');
    if (diff < 3600000)   return tf('profiles.last_seen.minutes_ago', { count: Math.floor(diff / 60000) }, '{count}m ago');
    if (diff < 86400000)  return tf('profiles.last_seen.hours_ago', { count: Math.floor(diff / 3600000) }, '{count}h ago');
    if (diff < 604800000) return tf('profiles.last_seen.days_ago', { count: Math.floor(diff / 86400000) }, '{count}d ago');
    return fmtShortDate(dt);
}

function formatInstanceTimer(joinedAt, now) {
    if (!joinedAt) return '';
    const seconds = Math.max(0, Math.floor((now - joinedAt) / 1000));
    const hours = Math.floor(seconds / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);
    if (hours > 0) return tf('instance.timer.hours_minutes', { hours, minutes }, '{hours}h {minutes}m');
    if (minutes > 0) return tf('instance.timer.minutes', { minutes }, '{minutes}m');
    return t('instance.timer.less_than_minute', '<1m');
}

function _notifBtnBusy(btn) {
    btn.disabled = true;
    const ico = btn.querySelector('.msi');
    if (ico) ico.textContent = 'hourglass_empty';
    else btn.textContent = '...';
}

function _notifTypeToneClass(type) {
    if (type === 'friendRequest') return 'nt-friend';
    if (typeof type === 'string' && (type.startsWith('moderation.') || type === 'votetokick')) return 'nt-alert';
    if (typeof type === 'string' && type.startsWith('group.')) return 'nt-group';
    if (type === 'invite' || type === 'requestInvite' || type === 'inviteResponse'
        || type === 'requestInviteResponse' || type === 'invite.instance.contentGated') return 'nt-invite';
    return 'nt-default';
}

function _resolveNotifImage(n) {
    if (n._image) return n._image;
    if (n.senderUserId && typeof vrcFriendsData !== 'undefined') {
        const f = vrcFriendsData.find(f => f.id === n.senderUserId);
        if (f && f.image) return f.image;
    }
    return '';
}

function renderNotifications(list, noDecline = _notifNoDecline) {
    notifications = (list || []).filter(n => n.type !== 'boop'); // boops only in messenger
    const unseen = notifications.filter(n => !n.seen).length;
    const badge = document.getElementById('notifBadge');
    if (unseen > 0) { badge.textContent = unseen; badge.style.display = ''; }
    else badge.style.display = 'none';

    const clearBtn = document.getElementById('notifClearAllBtn');
    if (clearBtn) clearBtn.style.display = (_notifTab === 'hidden' || notifications.length === 0) ? 'none' : '';

    const el = document.getElementById('notifList');
    if (notifications.length === 0) {
        el.innerHTML = `<div class="empty-msg">${t('notifications.empty', 'No notifications')}</div>`;
        return;
    }
    el.innerHTML = notifications.map(n => {
        const { icon, label } = getNotificationTypeMeta(n.type);
        const time = getNotificationTime(n.created_at);
        const age  = getNotificationAge(n.created_at);
        const canAccept = ['friendRequest', 'invite', 'requestInvite', 'group.invite', 'group.joinRequest'].includes(n.type);
        const canAnswer = (n.type === 'invite' || n.type === 'requestInvite');
        const nid = esc(n.id);
        const senderLink = n.senderUserId
            ? `<strong style="cursor:pointer;" onclick="toggleNotifPanel();openFriendDetail('${esc(n.senderUserId)}')">${esc(n.senderUsername || n.senderUserId)}</strong>`
            : (n.senderUsername ? `<strong>${esc(n.senderUsername)}</strong>` : '');
        const det = typeof n.details === 'string' ? (() => { try { return JSON.parse(n.details); } catch { return {}; } })() : (n.details || {});

        // Resolve avatar image
        const nImg = _resolveNotifImage(n);
        const hasImg = nImg && nImg.length > 5;
        const initial = (n.senderUsername || '?')[0].toUpperCase();
        const toneCls   = _notifTypeToneClass(n.type);
        const typeBadge = `<span class="msi notif-avatar-badge ${toneCls}" title="${esc(label)}">${icon}</span>`;
        const avatarHtml = hasImg
            ? `<div class="notif-avatar" style="background-image:url('${cssUrl(imgThumb(nImg, 64))}')">${typeBadge}</div>`
            : n.senderUsername
                ? `<div class="notif-avatar notif-avatar-ph">${esc(initial)}${typeBadge}</div>`
                : `<div class="notif-avatar notif-avatar-ph ${toneCls}" title="${esc(label)}"><span class="msi notif-avatar-icon">${icon}</span></div>`;

        const _notifGroupId = (() => {
            const d = (n._data && typeof n._data === 'string')
                ? (() => { try { return JSON.parse(n._data); } catch { return {}; } })()
                : (n._data || {});
            return det.groupId || d.groupId
                || (n._link && (n._link.match(/grp_[0-9a-f-]+/) || [])[0])
                || '';
        })();

        let titleHtml;
        let subHtml = '';
        let bodyHtml = '';
        if (n._v2 && n._title) {
            const isGroupNotif = typeof n.type === 'string' && n.type.startsWith('group.');
            titleHtml = (isGroupNotif && _notifGroupId)
                ? `<strong style="cursor:pointer;" onclick="toggleNotifPanel();openGroupDetail('${jsq(_notifGroupId)}')">${esc(n._title)}</strong>`
                : esc(n._title);
            if (n.message) bodyHtml = `<div class="notif-msg">${esc(n.message)}</div>`;
        } else if (n.type === 'invite') {
            const worldName = det.worldName ? esc(det.worldName) : t('notifications.unknown_world', 'unknown world');
            const wid = det.worldId ? det.worldId.split(':')[0] : '';
            const instanceId = det.instanceId || '';
            const location = wid && instanceId ? `${wid}:${instanceId}` : (det.worldId || '');
            const { instanceType } = location ? parseFriendLocation(location) : { instanceType: 'public' };
            const worldLink = wid
                ? `<strong style="cursor:pointer;" onclick="toggleNotifPanel();openInstanceDetailFromData({location:'${jsq(location)}',worldId:'${jsq(wid)}',worldName:'${jsq(det.worldName||'')}',instanceType:'${jsq(instanceType)}'})">${worldName}</strong>`
                : `<strong>${worldName}</strong>`;
            const msg = det.inviteMessage || '';
            titleHtml = senderLink || esc(label);
            subHtml = `${t('notifications.title.invited_you_to', 'invited you to')} ${worldLink}`;
            if (msg) bodyHtml = `<div class="notif-msg">${esc(msg)}</div>`;
        } else if (n.type === 'requestInvite') {
            const msg = det.requestMessage || '';
            titleHtml = senderLink || esc(label);
            subHtml = t('notifications.title.wants_invite', 'wants an invite');
            if (msg) bodyHtml = `<div class="notif-msg">${esc(msg)}</div>`;
        } else if (n.type === 'boop') {
            titleHtml = senderLink || esc(label);
            subHtml = t('notifications.title.booped_you', 'booped you');
        } else if (n.type === 'friendRequest') {
            titleHtml = senderLink || esc(label);
            subHtml = t('notifications.title.friend_request_received', 'sent you a friend request');
            if (n.message) bodyHtml = `<div class="notif-msg">${esc(n.message)}</div>`;
        } else if (n.type === 'inviteResponse' || n.type === 'requestInviteResponse') {
            titleHtml = senderLink || esc(label);
            const msg = det.responseMessage || det.requestMessage || det.inviteMessage || n.message || '';
            if (msg) bodyHtml = `<div class="notif-msg">${esc(msg)}</div>`;
        } else {
            titleHtml = senderLink || esc(label);
            if (n.message) bodyHtml = `<div class="notif-msg">${esc(n.message)}</div>`;
        }
        const actionsHtml = [
            canAccept ? `<button class="notif-act notif-act-accept notif-accept-btn" onclick="acceptNotif('${nid}',this)" title="${esc(t('notifications.actions.accept', 'Accept'))}"><span class="msi">check</span></button>` : '',
            canAnswer ? `<button class="notif-act notif-act-answer notif-answer-btn" onclick="openNotifRespondModal('${nid}')" title="${esc(t('notifications.actions.answer_tooltip', 'Decline with a message or image'))}"><span class="msi">reply</span></button>` : '',
            (!noDecline && (canAccept || !n.seen)) ? `<button class="notif-act notif-act-dismiss notif-decline-btn" onclick="declineNotif('${nid}',this)" title="${esc(t('notifications.actions.decline', 'Decline'))}"><span class="msi">close</span></button>` : '',
        ].join('');
        const msgHtml = subHtml
            ? `${titleHtml} <span class="notif-msg-sub">${subHtml}</span>`
            : titleHtml;
        return `<div class="notif-item ${n.seen && !canAccept ? 'notif-seen' : ''}" data-notif-id="${nid}">
            ${avatarHtml}
            <div class="notif-item-text">
                <div class="notif-msg-col">
                    <div class="notif-msg-main">${msgHtml}</div>
                    ${bodyHtml}
                </div>
                <div class="notif-meta-right">
                    <span class="notif-time" title="${esc(time)}">${esc(age)}</span>
                    ${actionsHtml ? `<div class="notif-actions">${actionsHtml}</div>` : ''}
                </div>
            </div>
        </div>`;
    }).join('');
}

function acceptNotif(notifId, btn) {
    if (btn) _notifBtnBusy(btn);
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

let _launchModalLoc = '';

function _launchModalMeta(location) {
    const out = { worldId: '', worldName: '', worldThumb: '', instanceType: '' };
    if (!location) return out;
    const colon = location.indexOf(':');
    out.worldId = colon > 0 ? location.slice(0, colon) : location;
    if (typeof parseFriendLocation === 'function') {
        const p = parseFriendLocation(location);
        out.instanceType = p?.instanceType || '';
        if (p?.worldId) out.worldId = p.worldId;
    }
    const wc = (typeof dashWorldCache !== 'undefined') ? dashWorldCache[out.worldId] : null;
    out.worldName = wc?.name || '';
    out.worldThumb = wc?.thumbnailImageUrl || wc?.imageUrl || '';
    return out;
}

function showLaunchModal(location, steamVrOpen) {
    closeLaunchModal();
    location = location || '';
    _launchModalLoc = location;
    const meta = _launchModalMeta(location);
    const hasLoc = !!(meta.worldId && meta.worldId.startsWith('wrld_'));
    const running = !!window.vrcGameRunning;

    const subParts = [];
    if (hasLoc) {
        subParts.push(meta.worldName || t('dashboard.friends.location_world', 'In World'));
        if (meta.instanceType && typeof getInstanceBadge === 'function') {
            const b = getInstanceBadge(meta.instanceType);
            if (b?.label) subParts.push(b.label);
        }
    }
    const subText = subParts.length
        ? subParts.join(' · ')
        : (running ? t('launch.sub.running', 'VRChat is running') : t('launch.sub.not_running', 'VRChat is not currently running'));

    const el = document.createElement('div');
    el.className = 'modal-overlay';
    el.style.display = 'flex'; // inline display required by _closeTopModal (Escape)
    el.style.zIndex = '10004';
    el.innerHTML = `
        <div class="launch-modal" role="dialog" aria-labelledby="_lmTitle">
            <div class="launch-head">
                <div class="launch-head-txt">
                    <div class="launch-title" id="_lmTitle">${esc(t('launch.title', 'Launch'))}</div>
                    <div class="launch-sub">${esc(subText)}</div>
                </div>
                <button class="launch-close" id="_lmClose" title="${esc(t('common.close', 'Close'))}" aria-label="${esc(t('common.close', 'Close'))}">
                    <span class="msi">close</span>
                </button>
            </div>
            <div class="launch-modes">
                <button class="launch-mode${running ? ' is-unavailable' : ''}" id="_lmVr"${running ? ' disabled' : ''}>
                    <span class="msi">view_in_ar</span>
                    <span class="launch-mode-label">${esc(t('launch.mode.vr', 'VR'))}</span>
                    ${running ? `<span class="launch-mode-note">${esc(t('launch.mode.already_running', 'Already running'))}</span>` : ''}
                </button>
                <button class="launch-mode${running ? ' is-unavailable' : ''}" id="_lmDesktop"${running ? ' disabled' : ''}>
                    <span class="msi">desktop_windows</span>
                    <span class="launch-mode-label">${esc(t('launch.mode.desktop', 'Desktop'))}</span>
                    ${running ? `<span class="launch-mode-note">${esc(t('launch.mode.already_running', 'Already running'))}</span>` : ''}
                </button>
                <button class="launch-mode${running && hasLoc ? '' : ' is-unavailable'}" id="_lmInGame"${running && hasLoc ? '' : ' disabled'}>
                    <span class="msi">sports_esports</span>
                    <span class="launch-mode-label">${esc(t('launch.mode.ingame', 'In-Game'))}</span>
                    ${running && hasLoc ? '' : `<span class="launch-mode-note">${esc(hasLoc ? t('launch.mode.ingame_note', 'VRChat not running') : t('launch.mode.ingame_no_instance', 'No instance'))}</span>`}
                </button>
            </div>
            ${hasLoc ? `<div class="launch-foot">
                <button class="launch-act" id="_lmInvite">
                    <span class="msi">person_add</span>${esc(t('launch.invite', 'Invite'))}
                </button>
                <button class="launch-act" id="_lmSelfInvite">
                    <span class="msi">mail</span>${esc(t('launch.self_invite', 'Self Invite'))}
                </button>
                <div class="launch-foot-spacer"></div>
                <button class="launch-icon-btn" id="_lmCopyInst" title="${esc(t('launch.copy_instance', 'Copy Instance Link'))}" aria-label="${esc(t('launch.copy_instance', 'Copy Instance Link'))}">
                    <span class="msi">link</span>
                </button>
                <button class="launch-icon-btn" id="_lmCopyWorld" title="${esc(t('launch.copy_world', 'Copy World Link'))}" aria-label="${esc(t('launch.copy_world', 'Copy World Link'))}">
                    <span class="msi">location_on</span>
                </button>
            </div>` : ''}
        </div>`;

    const on = (id, fn) => { const b = el.querySelector(id); if (b) b.addEventListener('click', fn); };
    on('#_lmClose', closeLaunchModal);
    on('#_lmVr', () => { if (!window.vrcGameRunning) launchAndJoin(location, true); });
    on('#_lmDesktop', () => { if (!window.vrcGameRunning) launchAndJoin(location, false); });
    on('#_lmInGame', () => {
        if (!window.vrcGameRunning || !hasLoc) return;
        sendToCS({ action: 'vrcOpenInGame', location });
        closeLaunchModal();
    });
    on('#_lmInvite', () => {
        if (typeof openInviteModalForLocation === 'function')
            openInviteModalForLocation(location, meta.worldName, meta.worldThumb, meta.instanceType);
    });
    on('#_lmSelfInvite', () => {
        sendToCS({ action: 'vrcSelfInvite', location });
        closeLaunchModal();
    });
    on('#_lmCopyInst', () => { if (typeof copyInstanceLink === 'function') copyInstanceLink(location); });
    on('#_lmCopyWorld', () => {
        navigator.clipboard.writeText('https://vrchat.com/home/world/' + meta.worldId)
            .then(() => showToast(true, t('launch.toast.world_link_copied', 'World link copied!')))
            .catch(() => showToast(false, t('timeline.toast.copy_failed', 'Failed to copy')));
    });

    el.addEventListener('click', e => { if (e.target === el) closeLaunchModal(); });
    document.body.appendChild(el);
    window._launchModalEl = el;
}

// Keeps the In-Game button in sync while the modal stays open (game state polls every 5s)
function _lmSetMode(btn, usable, noteText) {
    if (!btn) return;
    btn.classList.toggle('is-unavailable', !usable);
    btn.disabled = !usable;
    const note = btn.querySelector('.launch-mode-note');
    if (usable) { if (note) note.remove(); return; }
    if (note) note.textContent = noteText;
    else {
        const s = document.createElement('span');
        s.className = 'launch-mode-note';
        s.textContent = noteText;
        btn.appendChild(s);
    }
}

function launchModalSyncGameState() {
    const el = window._launchModalEl;
    if (!el) return;
    const meta = _launchModalMeta(_launchModalLoc);
    const hasLoc = !!(meta.worldId && meta.worldId.startsWith('wrld_'));
    const running = !!window.vrcGameRunning;
    const runNote = t('launch.mode.already_running', 'Already running');
    _lmSetMode(el.querySelector('#_lmVr'), !running, runNote);
    _lmSetMode(el.querySelector('#_lmDesktop'), !running, runNote);
    _lmSetMode(el.querySelector('#_lmInGame'), running && hasLoc,
        hasLoc ? t('launch.mode.ingame_note', 'VRChat not running') : t('launch.mode.ingame_no_instance', 'No instance'));
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
    _launchModalLoc = '';
}

function declineNotif(notifId, btn) {
    if (btn) _notifBtnBusy(btn);
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
    if (typeof _pplUpdateCounts === 'function') _pplUpdateCounts();
    if (typeof onInstancePlayersLive === 'function') onInstancePlayersLive();

    // Feed Discord presence preview
    if (typeof dpOnInstanceUpdate === 'function' && data && !data.empty && !data.error && data.worldName) {
        const typeLabel = getInstanceBadge(data.instanceType).label;
        const shortId = (data.location || '').split(':')[1]?.split('~')[0] || '';
        const stateStr = `${typeLabel} #${shortId} (${data.nUsers}/${data.capacity})`;
        dpOnInstanceUpdate(data.worldName, data.worldThumb, stateStr, null);
    } else if (!data || data.empty) {
        if (typeof dpClearPresencePreview === 'function') dpClearPresencePreview();
    }

    const el = document.getElementById('vrcInstanceArea');
    if (!el) return;

    if (!data || data.empty) { el.innerHTML = ''; return; }
    if (data.error) {
        el.innerHTML = `<div style="font-size:calc(11px + var(--fs-off, 0px));color:var(--err);padding:6px 0;">${esc(data.error)}</div>`;
        return;
    }
    if (!data.worldName && !data.worldId) { el.innerHTML = ''; return; }

    const name = data.worldName || data.worldId || t('instance.unknown_world', 'Unknown World');
    let users = data.users || [];

    // Build friend lookup maps
    const _byId = {}, _byName = {};
    vrcFriendsData.forEach(f => {
        if (f.id) _byId[f.id] = f;
        if (f.displayName) _byName[f.displayName.toLowerCase()] = f;
    });

    // If backend gave no users, fall back to friends in same location
    if (users.length === 0 && data.location && vrcFriendsData.length > 0) {
        const myLocBase = data.location.split('~')[0];
        users = vrcFriendsData.filter(f => {
            if (!f.location || f.location === 'private' || f.location === 'offline') return false;
            return f.location.split('~')[0] === myLocBase;
        });
    }

    // Enrich with live friend data (image, status, statusDescription) and own user via currentVrcUser
    users = users.map(u => {
        if (currentVrcUser && u.id && u.id === currentVrcUser.id)
            return { ...u, image: currentVrcUser.image || u.image, status: currentVrcUser.status || u.status, statusDescription: currentVrcUser.statusDescription ?? u.statusDescription };
        const m = (u.id && _byId[u.id]) || (u.displayName && _byName[(u.displayName || '').toLowerCase()]);
        if (!m) return u;
        return { ...u, image: m.image || u.image, id: m.id || u.id, status: m.status || u.status, statusDescription: m.statusDescription ?? u.statusDescription };
    });

    // Split: friends vs other players
    const friendUsers = users.filter(u =>
        (u.id && _byId[u.id]) || (u.displayName && _byName[(u.displayName || '').toLowerCase()]));
    const otherUsers = users.filter(u =>
        !(u.id && _byId[u.id]) && !(u.displayName && _byName[(u.displayName || '').toLowerCase()]));

    function renderSidebarRow(u, isFriend) {
        const hasImg = u.image && u.image.length > 5;
        const initial = (u.displayName || '?')[0].toUpperCase();
        const avInner = hasImg
            ? `<div class="inst-user-av" style="background-image:url('${cssUrl(imgThumb(u.image, 64))}')"></div>`
            : `<div class="inst-user-av inst-user-av-letter">${esc(initial)}</div>`;
        const dotShape = u.presence === 'web' ? 'vrc-status-ring' : 'vrc-status-dot';
        const avDot = u.status ? `<span class="inst-user-av-dot ${dotShape} ${statusDotClass(u.status)}"></span>` : '';
        const avatar = `<div class="inst-user-av-wrap">${avInner}${avDot}</div>`;
        const click = u.id ? ` onclick="openFriendDetail('${esc(u.id)}')"` : '';
        const statusLine = u.status
            ? `<span class="inst-user-status">${esc(u.statusDescription || statusLabel(u.status))}</span>`
            : '';
        return `<div class="inst-user-row"${click}>${avatar}<div class="inst-user-info"><span class="inst-user-name">${esc(u.displayName)}</span>${statusLine ? `<div class="inst-user-status-row">${statusLine}</div>` : ''}</div></div>`;
    }

    const lbl = `font-size:calc(10px + var(--fs-off, 0px));font-weight:700;color:var(--tx3);padding:6px 10px 2px;letter-spacing:.05em;`;
    let usersHtml = '';
    if (users.length > 0) {
        usersHtml = `<div class="inst-users">`;
        if (friendUsers.length > 0) {
            usersHtml += `<div style="${lbl}">${tf('instance.sections.friends_in_instance', { count: friendUsers.length }, 'FRIENDS IN INSTANCE ({count})')}</div>`;
            usersHtml += friendUsers.map(u => renderSidebarRow(u)).join('');
        }
        if (otherUsers.length > 0) {
            usersHtml += `<div style="${lbl}">${tf('instance.sections.players_in_instance', { count: otherUsers.length }, 'PLAYERS IN INSTANCE ({count})')}</div>`;
            usersHtml += otherUsers.map(u => renderSidebarRow(u)).join('');
        }
        usersHtml += `</div>`;
    } else {
        usersHtml = `<div style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);padding:8px 10px;">${t('instance.no_player_data', 'No player data')}</div>`;
    }

    const { cls: _instCls, label: _instLabel } = getInstanceBadge(data.instanceType);
    const typeBadge = data.instanceType && data.instanceType !== 'public'
        ? `<span class="inst-type-badge vrcn-badge ${_instCls}">${esc(_instLabel)}</span>` : '';
    const _ageGateLabel = t('worlds.instances.age_gated', 'Age Gated');
    const ageGateBadge = data.ageGate
        ? `<span class="inst-type-badge vrcn-badge" style="right:auto;left:8px;background:rgba(255,75,85,.15);color:var(--err);border-color:rgba(255,75,85,.3);">${esc(_ageGateLabel)}</span>` : '';

    const displayCount = users.length || data.nUsers || 0;
    const prevInstScroll = el.querySelector('.inst-users')?.scrollTop || 0;
    el.innerHTML = `<div class="inst-card" data-inst-type="${_instCls}">
        <div class="inst-header" style="background-image:url('${cssUrl(imgThumb(data.worldThumb || '', 96))}');cursor:pointer;" onclick="openInstanceInfoModal()">
            <div class="inst-header-fade"></div>
            ${typeBadge}
            ${ageGateBadge}
            <div class="inst-header-info">
                <div class="inst-world-name">${esc(name)}</div>
                <div class="inst-player-count"><span class="msi" style="font-size:13px;">person</span> ${displayCount}${data.capacity ? '/' + data.capacity : ''}</div>
            </div>
        </div>
        ${usersHtml}
        <div class="inst-invite-bar">
            <button class="vrcn-button inst-invite-btn" onclick="openInviteModal()">
                <span class="msi">person_add</span> ${t('dashboard.instances.invite_friends', 'Invite Friends')}
            </button>
        </div>
    </div>
    `;
    if (prevInstScroll > 0) {
        const newInstUsers = el.querySelector('.inst-users');
        if (newInstUsers) newInstUsers.scrollTop = prevInstScroll;
    }
    // If instance info modal is open, refresh it live
    const _iim = document.getElementById('modalInstanceInfo');
    if (_iim && _iim.style.display !== 'none') openInstanceInfoModal();
}

