let _ipTicker = null;

function _ipActive() {
    const tab = document.getElementById('tab3');
    return !!(tab && tab.classList.contains('active') && peopleFilter === 'instance');
}

function _ipUsers() {
    const data = (typeof currentInstanceData !== 'undefined') ? currentInstanceData : null;
    if (!data || data.empty || data.error) return { data: null, users: [] };

    let users = (data.users || []).slice();
    if (users.length === 0 && data.location) {
        const base = data.location.split('~')[0];
        users = vrcFriendsData.filter(f =>
            f.location && f.location !== 'private' && f.location !== 'offline' &&
            f.location.split('~')[0] === base);
    }

    const byId = {};
    const byName = {};
    vrcFriendsData.forEach(f => {
        if (f.id) byId[f.id] = f;
        if (f.displayName) byName[f.displayName.toLowerCase()] = f;
    });

    return {
        data,
        users: users.map(u => {
            const friend = (u.id && byId[u.id]) || (u.displayName && byName[(u.displayName || '').toLowerCase()]) || null;
            return { ...u, _friend: friend };
        }),
    };
}

function _ipValue(u, field) {
    const f = u._friend;
    switch (field) {
        case 'timer':
        case 'joined':   return u.joinedAt || 0;
        case 'rank':     return _PL_RANK_ORDER.indexOf(getTrustRank((f?.tags?.length ? f.tags : u.tags) || [])?.cls || '');
        case 'status':   return (f?.status || u.status || '').toLowerCase();
        case 'age':      return (f?.ageVerificationStatus || u.ageVerificationStatus) === '18+' ? 2 : ((f?.ageVerified || u.ageVerified) ? 1 : 0);
        case 'platform': return (f?.platform || u.platform || '').toLowerCase();
        case 'language': return ((f?.tags || u.tags || []).find(x => x.startsWith('language_')) || '');
        case 'biolinks': return (Array.isArray(u.bioLinks) ? u.bioLinks.filter(Boolean).length : 0);
        case 'pronouns': return (u.pronouns || f?.pronouns || '').toLowerCase();
        case 'avatar':   return (u.avatarName || '').toLowerCase();
        case 'meets':     return (_peopleStatsMap[u.id]?.meets) || 0;
        case 'timespent': return (_peopleStatsMap[u.id]?.seconds) || 0;
        case 'joineddate': return u.dateJoined || '';
        case 'lastseen':  return u.lastSeen || '';
        default:         return (u.displayName || '').toLowerCase();
    }
}

function avatarStateIconHtml(unresolved, size = 12) {
    const icon  = unresolved ? 'hourglass_empty' : 'lock';
    const title = unresolved
        ? t('profiles.badges.avatar_unresolved', 'Unresolved, IDs are rechecked every 10 minutes. If it takes too long, open the profile or use Check for Avatar.')
        : t('profiles.badges.avatar_not_in_db', 'Avatar not found in any database');
    return `<span class="msi" title="${esc(title)}" style="font-size:${size}px;color:var(--tx2);margin-left:4px;vertical-align:-2px;">${icon}</span>`;
}

function instanceAvatarCellHtml(u) {
    const name = u?.avatarName || '';
    if (!name) return '';
    if (!u.avatarId) {
        return `<span class="ip-avatar-name">${esc(name)}${avatarStateIconHtml(!!u.avatarUnresolved)}</span>`;
    }
    return `<span class="ip-avatar-name ip-avatar-link" onclick="event.stopPropagation();navOpenModal('avatar','${jsq(u.avatarId)}','${jsq(name)}')" title="${esc(name)}">${esc(name)}</span>`;
}

function buildInstancePlayersHtml(users, iStart, iTotal, now) {
    _plEnsureStats();
    let rows = '';
    users.forEach(u => {
        const f = u._friend;
        const isSelf = (typeof currentVrcUser !== 'undefined') && currentVrcUser && u.id && u.id === currentVrcUser.id;
        const src = isSelf ? currentVrcUser : f;
        const uid = jsq(u.id || '');
        const displayName = u.displayName || '?';
        const image = src?.image || u.image || '';
        const status = src?.status || u.status || '';
        const statusDesc = src?.statusDescription ?? u.statusDescription ?? '';
        const tags = (f?.tags?.length ? f.tags : null) || u.tags || [];
        const platform = f?.platform || u.platform || '';
        const ageVerified = !!(f?.ageVerified || u.ageVerified);
        const is18 = (f?.ageVerificationStatus || u.ageVerificationStatus) === '18+';
        const st = _peopleStatsMap[u.id];

        const av = image
            ? `<span class="pl-av" style="background-image:url('${cssUrl(imgThumb(image, 64))}')"></span>`
            : `<span class="pl-av pl-av-letter">${esc(displayName[0].toUpperCase())}</span>`;

        const rank = getTrustRank(tags);
        const langs = tags.filter(x => x.startsWith('language_'))
            .map(x => `<span class="vrcn-badge">${esc((typeof LANG_MAP !== 'undefined' && LANG_MAP[x]) || x.replace('language_', '').toUpperCase())}</span>`).join('');

        let platIcon = '';
        if (platform === 'standalonewindows') platIcon = `<span class="msi" title="${esc(t('instance.platform.pc', 'PC'))}" style="font-size:16px;color:var(--tx2);">computer</span>`;
        else if (platform === 'android') platIcon = `<span class="msi" title="${esc(t('instance.platform.quest', 'Quest'))}" style="font-size:16px;color:var(--tx2);">view_in_ar</span>`;

        let bar = '';
        if (iTotal > 0 && u.joinedAt) {
            const pEnd = u.leftAt || now;
            const left = Math.max(0, Math.min(100, (u.joinedAt - iStart) / iTotal * 100));
            const width = Math.max(0, Math.min(100 - left, (pEnd - u.joinedAt) / iTotal * 100));
            const cls = (f || isSelf) ? ' friend' : '';
            bar = `<div class="tl-player-bar-wrap"><div class="tl-player-bar${cls}" style="left:${left.toFixed(1)}%;width:${width.toFixed(1)}%"></div></div>`;
        }

        rows += tlTableRow('instanceList', u.id ? ` onclick="openFriendDetail('${uid}')"` : '', {
            profile:  `<td class="pl-profile">${av}</td>`,
            timer:    `<td class="pl-date">${u.joinedAt ? esc(formatInstanceTimer(u.joinedAt, now)) : ''}</td>`,
            joined:   `<td class="pl-date">${u.joinedAt ? esc(fmtTime(new Date(u.joinedAt))) : ''}</td>`,
            name:     `<td class="pl-name">${esc(displayName)}</td>`,
            avatar:   `<td class="pl-name">${instanceAvatarCellHtml(u)}</td>`,
            rank:     `<td>${rank ? `<span class="vrcn-badge ${rank.cls}">${esc(rank.label)}</span>` : ''}</td>`,
            status:   `<td class="pl-status">${status ? `<span class="vrc-status-dot ${statusDotClass(status)}"></span><span class="pl-status-txt">${esc(statusDesc || statusLabel(status))}</span>` : ''}</td>`,
            age:      `<td>${is18 ? `<span class="vrcn-badge ip-age">18+</span>` : (ageVerified ? `<span class="vrcn-badge ip-age">Verified</span>` : '')}</td>`,
            platform: `<td>${platIcon}</td>`,
            language: `<td class="pl-langs">${langs}</td>`,
            biolinks: `<td class="pl-bios">${_plBioLinksCell(u)}</td>`,
            pronouns: `<td class="lv-sub">${esc(u.pronouns || f?.pronouns || '')}</td>`,
            meets:     `<td class="pl-num">${st && st.meets ? esc(String(st.meets)) : ''}</td>`,
            timespent: `<td class="pl-num">${st && st.seconds > 0 ? esc(_plTimeSpent(st.seconds)) : ''}</td>`,
            joineddate: `<td class="pl-date">${esc(_plDate(u.dateJoined))}</td>`,
            lastseen:  `<td class="pl-date">${esc(_plDateTime(u.lastSeen))}</td>`,
            presence: `<td class="ip-bar-cell">${bar}</td>`,
        });
    });
    return tlTableHtml('instanceList', rows);
}

function renderInstancePlayers() {
    const el = document.getElementById('instancePlayersGrid');
    if (!el) return;

    const titleEl = document.getElementById('instancePlayersTitle');
    const countEl = document.getElementById('instancePlayersCount');
    const { data, users } = _ipUsers();

    if (!data) {
        el.innerHTML = emptyStateHtml('travel_explore', esc(t('profiles.people.instance.empty', 'You are not in an instance')));
        if (titleEl) titleEl.textContent = '';
        if (countEl) countEl.textContent = '';
        return;
    }

    if (titleEl) titleEl.textContent = data.worldName || '';
    if (countEl) {
        const badge = (typeof getInstanceBadge === 'function') ? getInstanceBadge(data.instanceType) : null;
        const instNum = (data.location || '').split(':')[1]?.split('~')[0] || '';
        const parts = [];
        if (badge?.label) parts.push(`<span class="vrcn-badge ${badge.cls || ''}">${esc(badge.label)}</span>`);
        if (instNum) parts.push(`<span class="vrcn-id-clip" onclick="copyInstanceLink('${jsq(data.location || '')}')"><span class="msi" style="font-size:12px;">content_copy</span>#${esc(instNum)}</span>`);
        if (data.ageGate) parts.push(`<span class="vrcn-badge" style="background:rgba(255,75,85,.15);color:var(--err);">${esc(t('worlds.instances.age_gated', 'Age Gated'))}</span>`);
        if (typeof getOwnerBadgeHtml === 'function') {
            const owner = getOwnerBadgeHtml(data.ownerId || '', data.ownerName || '', data.ownerGroup || '', '');
            if (owner) parts.push(owner);
        }
        parts.push(`<span class="vrcn-badge"><span class="msi" style="font-size:11px;">person</span>&nbsp;${users.length || data.nUsers || 0}${data.capacity ? '/' + data.capacity : ''}</span>`);
        countEl.innerHTML = parts.join('');
    }

    const q = (document.getElementById('instancePlayersSearch')?.value || '').toLowerCase();
    const filtered = q ? users.filter(u => {
        const f = u._friend;
        const status = f?.status || u.status || '';
        const statusDesc = f?.statusDescription ?? u.statusDescription ?? '';
        return (u.displayName || '').toLowerCase().includes(q)
            || (u.id || '').toLowerCase().includes(q)
            || (u.avatarName || '').toLowerCase().includes(q)
            || statusDesc.toLowerCase().includes(q)
            || status.toLowerCase().includes(q)
            || (typeof statusLabel === 'function' && status ? statusLabel(status).toLowerCase().includes(q) : false);
    }) : users;

    if (!filtered.length) {
        el.innerHTML = emptyStateHtml('search', esc(t('profiles.people.no_results', 'No results')));
        return;
    }

    const sorted = lvSort(filtered, 'instanceList', _ipValue);
    const now = Date.now();
    const iStart = sorted.reduce((min, u) => (u.joinedAt && u.joinedAt < min) ? u.joinedAt : min, now);
    const iTotal = now - iStart;

    lvKeepScroll(el, () => { el.innerHTML = buildInstancePlayersHtml(sorted, iStart, iTotal, now); });
}

function instancePlayersSyncTicker() {
    const need = _ipActive();
    if (need && !_ipTicker) {
        _ipTicker = setInterval(() => {
            if (!_ipActive()) return;
            renderInstancePlayers();
        }, 5000);
    } else if (!need && _ipTicker) {
        clearInterval(_ipTicker);
        _ipTicker = null;
    }
}

function onInstancePlayersLive() {
    if (!_ipActive()) return;
    renderInstancePlayers();
}

document.documentElement.addEventListener('tabchange', instancePlayersSyncTicker);
document.documentElement.addEventListener('languagechange', () => { if (_ipActive()) renderInstancePlayers(); });
