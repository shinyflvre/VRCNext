/**
 * @param {object} user 
 * @param {string} onclick 
 * @param {object} [opts] 
 * @returns {string} 
 */
function renderUserItem(user, onclick, opts) {
    opts = opts || {};
    const id = user.id || user.userId || '';
    const name = user.displayName || '?';

    const live = id ? vrcFriendsData.find(f => f.id === id) : null;
    const image = live?.image || user.image || '';

    const letter = esc((name[0] || '?').toUpperCase());
    const avatar = image
        ? `<img class="vrcn-user-item-avatar" src="${esc(imgThumb(image, 96))}" loading="lazy" decoding="async" onerror="this.outerHTML='<div class=\\'vrcn-user-item-avatar vrcn-user-item-avatar-letter\\'>${letter}</div>'">`
        : `<div class="vrcn-user-item-avatar vrcn-user-item-avatar-letter">${letter}</div>`;

    const _frameUrl = live?.iconFrameUrl || user.iconFrameUrl || '';
    const _nameUrl  = live?.nameplateUrl || user.nameplateUrl || '';
    const _frameDeco = (typeof iconFrameHtml === 'function') ? iconFrameHtml(_frameUrl) : '';
    const _nameDeco  = (typeof nameplateDecoHtml === 'function') ? nameplateDecoHtml(_nameUrl) : '';

    const status = live?.status || user.status || '';
    const statusDesc = live?.statusDescription || user.statusDescription || '';
    const presence = live ? live.presence : (user.presence || '');
    const pendingOffline = !!(live ? live.pendingOffline : user.pendingOffline);
    const dotCls = presence === 'web' ? 'vrc-status-ring' : 'vrc-status-dot';
    const dot = `<span class="${dotCls} ${pendingOffline ? 's-offline' : statusDotClass(status)} vrcn-user-item-dot"></span>`;

    const platform = live?.platform || user.platform || '';
    const platBadge = getPlatformBadgeHtml(platform);

    const statusText = statusDesc || (status ? statusLabel(status) : t('status.offline', 'Offline'));

    const worldInner = opts.noWorld ? '' : _userItemWorldInner(live?.location || user.location || '');
    const traveling = !!(live ? live.traveling : user.traveling);
    const secondRow = pendingOffline
        ? `<div class="vrcn-user-item-status">${esc(t('profiles.friends.pending_offline', 'Pending offline...'))}</div>`
        : traveling
            ? `<div class="vrcn-user-item-status">${esc(t('profiles.friends.traveling', 'Traveling...'))}</div>`
            : worldInner
                ? `<div class="vrcn-user-item-world">${worldInner}</div>`
                : `<div class="vrcn-user-item-status">${esc(statusText)}</div>`;

    const attrs = opts.attrs ? ' ' + opts.attrs : '';
    const cls = opts.cls ? ' ' + opts.cls : '';
    const idAttr = id ? ` data-uid="${esc(id)}"` : '';
    const trailing = opts.trailing || '';

    const statsRow = (id && typeof window !== 'undefined' && window._peopleStatsMode && typeof buildPeopleStatsRow === 'function')
        ? buildPeopleStatsRow(id)
        : '';

    return `<div class="vrcn-user-item${cls}${(typeof decoSelfCls === 'function') ? decoSelfCls(user) : ''}"${idAttr}${attrs} onclick="${onclick}">
        ${_nameDeco}
        <div class="vrcn-user-item-avatar-wrap">${avatar}${_frameDeco}${dot}</div>
        <div class="vrcn-user-item-info">
            <div class="vrcn-user-item-name">${esc(name)}${platBadge ? `<span class="vrcn-user-item-plat">${platBadge}</span>` : ''}</div>
            ${secondRow}
        </div>
        ${statsRow}
        ${trailing}
    </div>`;
}

function _userItemWorldInner(location) {
    const parsed = (location && typeof parseFriendLocation === 'function') ? parseFriendLocation(location) : null;
    if (parsed && parsed.worldId && parsed.worldId.startsWith('wrld_')) {
        const wc = (typeof dashWorldCache !== 'undefined') ? dashWorldCache[parsed.worldId] : null;
        const wname = wc?.name || t('dashboard.friends.location_world', 'In World');
        return `<span class="msi">public</span><span class="vrcn-user-item-world-name">${esc(wname)}</span>`;
    }
    return '';
}

function updateUserItemWorld(idOrUser) {
    const id = typeof idOrUser === 'string' ? idOrUser : (idOrUser && (idOrUser.id || idOrUser.userId));
    if (!id) return;
    const items = document.querySelectorAll(`.vrcn-user-item[data-uid="${id}"]`);
    if (!items.length) return;
    const live = (typeof vrcFriendsData !== 'undefined') ? vrcFriendsData.find(f => f.id === id) : null;
    const location = live?.location || (typeof idOrUser === 'object' ? idOrUser.location : '') || '';
    const inner = _userItemWorldInner(location);
    items.forEach(item => {
        const wl = item.querySelector('.vrcn-user-item-world');
        if (wl) wl.innerHTML = inner;
    });
}

function refreshAllUserItemWorlds() {
    document.querySelectorAll('.vrcn-user-item[data-uid]').forEach(item => {
        const wl = item.querySelector('.vrcn-user-item-world');
        if (!wl) return;
        const id = item.getAttribute('data-uid');
        const live = (typeof vrcFriendsData !== 'undefined') ? vrcFriendsData.find(f => f.id === id) : null;
        wl.innerHTML = _userItemWorldInner(live?.location || '');
    });
}

function renderProfileItem(user, onclick, opts) {
    return renderUserItem(user, onclick, opts);
}
