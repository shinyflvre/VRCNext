/**
 * Universal profile list item — fd-profile-item design.
 * Used by Profiles → Mutuals, Groups → Members, World Detail.
 *
 * @param {object} user    - { id, displayName, image, status, statusDescription, presence }
 * @param {string} onclick - Inline JS onclick string; caller uses jsq() for user id
 * @returns {string} HTML string
 */
function renderProfileItem(user, onclick) {
    const name = user.displayName || '?';

    // Friends: prefer live image from vrcFriendsData (correctly resolved from friends API,
    // which returns userIcon reliably). Partial APIs like group members often omit userIcon.
    // Non-friends: use the payload image as-is.
    const live  = vrcFriendsData.find(f => f.id === user.id);
    const image = live?.image || user.image || '';

    const img = image
        ? `<img class="fd-profile-item-avatar" src="${esc(image)}" onerror="this.outerHTML='<div class=\\'fd-profile-item-avatar\\' style=\\'display:flex;align-items:center;justify-content:center;font-size:14px;font-weight:700;color:var(--tx3)\\'>${esc(name[0])}</div>'">`
        : `<div class="fd-profile-item-avatar" style="display:flex;align-items:center;justify-content:center;font-size:14px;font-weight:700;color:var(--tx3)">${esc(name[0])}</div>`;

    // Status: live friend data first, then payload (covers non-friends in groups etc.).
    const status     = live?.status            || user.status            || '';
    const statusDesc = live?.statusDescription || user.statusDescription || '';
    const presence   = live ? live.presence    : (user.presence         || '');
    const indicator  = presence === 'web' ? 'vrc-status-ring' : 'vrc-status-dot';

    const statusLine = status
        ? `<div class="fd-profile-item-status"><span class="${indicator} ${statusDotClass(status)}" style="width:6px;height:6px;flex-shrink:0;"></span>${statusLabel(status)}${statusDesc ? ' — ' + esc(statusDesc) : ''}</div>`
        : '';

    return `<div class="vrcn-profile-item" onclick="${onclick}">${img}<div class="fd-profile-item-info"><div class="fd-profile-item-name">${esc(name)}</div>${statusLine}</div></div>`;
}
