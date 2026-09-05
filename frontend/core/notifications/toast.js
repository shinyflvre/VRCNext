/* === Unified Toast System ===
 * Single source of truth for all feedback toasts in the app.
 * showToast(ok, msg) - action results (Blocked, Muted, Invite Sent, Saved, etc.)
 * showNotifToast(type, sender, message) - incoming VRChat notifications.
 */

const NOTIF_TOAST_TYPE_META = {
    friendRequest: { key: 'notifications.types.friend_request', fallback: 'Friend Request' },
    invite: { key: 'notifications.types.world_invite', fallback: 'World Invite' },
    requestInvite: { key: 'notifications.types.invite_request', fallback: 'Invite Request' },
    inviteResponse: { key: 'notifications.types.invite_response', fallback: 'Invite Response' },
    requestInviteResponse: { key: 'notifications.types.invite_request_response', fallback: 'Invite Req. Response' },
    votetokick: { key: 'notifications.types.vote_to_kick', fallback: 'Vote to Kick' },
    boop: { key: 'notifications.types.boop', fallback: 'Boop' },
    message: { key: 'notifications.types.message', fallback: 'Message' },
    'group.announcement': { key: 'notifications.types.group_announcement', fallback: 'Group Announcement' },
    'instance.announcement': { key: 'notifications.types.instance_announcement', fallback: 'Instance Announcement' },
    'group.invite': { key: 'notifications.types.group_invite', fallback: 'Group Invite' },
    'group.joinRequest': { key: 'notifications.types.group_join_request', fallback: 'Group Join Request' },
    'group.informationRequest': { key: 'notifications.types.group_info_request', fallback: 'Group Info Request' },
    'group.transfer': { key: 'notifications.types.group_transfer', fallback: 'Group Transfer' },
    'group.informative': { key: 'notifications.types.group_info', fallback: 'Group Info' },
    'group.post': { key: 'notifications.types.group_post', fallback: 'Group Post' },
    'group.event.created': { key: 'notifications.types.group_event', fallback: 'Group Event' },
    'group.event.starting': { key: 'notifications.types.group_event_starting', fallback: 'Group Event Starting' },
    'avatarreview.success': { key: 'notifications.types.avatar_approved', fallback: 'Avatar Approved' },
    'avatarreview.failure': { key: 'notifications.types.avatar_rejected', fallback: 'Avatar Rejected' },
    'badge.earned': { key: 'notifications.types.badge_earned', fallback: 'Badge Earned' },
    'economy.alert': { key: 'notifications.types.economy_alert', fallback: 'Economy Alert' },
    'economy.received.gift': { key: 'notifications.types.gift_received', fallback: 'Gift Received' },
    'event.announcement': { key: 'notifications.types.event', fallback: 'Event' },
    'invite.instance.contentGated': { key: 'notifications.types.content_gated_invite', fallback: 'Content Gated Invite' },
    'moderation.contentrestriction': { key: 'notifications.types.content_restriction', fallback: 'Content Restriction' },
    'moderation.notice': { key: 'notifications.types.moderation_notice', fallback: 'Moderation Notice' },
    'moderation.report.closed': { key: 'notifications.types.report_closed', fallback: 'Report Closed' },
    'moderation.warning.group': { key: 'notifications.types.group_warning', fallback: 'Group Warning' },
    'promo.redeem': { key: 'notifications.types.promo_redeemed', fallback: 'Promo Redeemed' },
    'vrcplus.gift': { key: 'notifications.types.vrcplus_gift', fallback: 'VRC+ Gift' },
};

function notifToastLabel(type) {
    const meta = NOTIF_TOAST_TYPE_META[type];
    return meta ? t(meta.key, meta.fallback) : (type || 'Notification');
}

function showToast(ok, msg) {
    const area = document.getElementById('notifToastArea');
    if (!area) return;
    const t = document.createElement('div');
    t.className = 'notif-toast';
    t.innerHTML = `<span class="msi" style="font-size:18px;color:${ok ? 'var(--ok)' : 'var(--err)'};">${ok ? 'check_circle' : 'error'}</span><div style="font-size:calc(13px + var(--fs-off, 0px));">${esc(msg)}</div>`;
    area.appendChild(t);
    setTimeout(() => t.classList.add('notif-toast-show'), 10);
    setTimeout(() => { t.classList.remove('notif-toast-show'); setTimeout(() => t.remove(), 300); }, 3000);
}

function showNotifToast(type, sender, message) {
    const area = document.getElementById('notifToastArea');
    if (!area) return;
    const icon = type === 'invite' ? 'mail' : type === 'friendRequest' ? 'person_add' : 'notifications';
    const label = notifToastLabel(type);
    const subtitle = tf('notifications.title.from', { label, sender }, `${label} from ${sender}`);
    const t = document.createElement('div');
    t.className = 'notif-toast';
    t.innerHTML = `<span class="msi" style="font-size:18px;color:var(--accent);">${icon}</span><div><strong>${esc(label)}</strong><div style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx2);">${esc(subtitle)}${message ? ': ' + esc(message) : ''}</div></div>`;
    area.appendChild(t);
    setTimeout(() => t.classList.add('notif-toast-show'), 10);
    setTimeout(() => { t.classList.remove('notif-toast-show'); setTimeout(() => t.remove(), 300); }, 5000);
}
