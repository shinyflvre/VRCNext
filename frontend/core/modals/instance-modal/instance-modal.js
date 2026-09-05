/* === Instance Modal === */
/* === Instance Info Modal === */

function openInstanceInfoModal() {
    const data = currentInstanceData;
    if (!data || data.empty || data.error || (!data.worldName && !data.worldId)) return;

    const m = document.getElementById('modalInstanceInfo');
    const c = document.getElementById('instanceInfoContent');
    if (!m || !c) return;

    const thumb = data.worldThumb || '';
    const name  = data.worldName || data.worldId || t('instance.unknown_world', 'Unknown World');
    const { cls: instCls, label: instLabel } = getInstanceBadge(data.instanceType);
    const instNum = (data.location || '').match(/:(\d+)/)?.[1] || '';

    const bannerHtml = thumb
        ? `<div class="fd-banner"><img src="${thumb}" onerror="this.parentElement.style.display='none'"><div class="fd-banner-fade"></div></div>`
        : '';

    // Build friend lookup maps
    const byId   = {};
    const byName = {};
    vrcFriendsData.forEach(f => {
        if (f.id)          byId[f.id] = f;
        if (f.displayName) byName[f.displayName.toLowerCase()] = f;
    });

    // User list — fall back to friends in same location
    let users = (data.users || []).slice();
    if (users.length === 0 && data.location) {
        const myLocBase = data.location.split('~')[0];
        users = vrcFriendsData.filter(f => {
            if (!f.location || f.location === 'private' || f.location === 'offline') return false;
            return f.location.split('~')[0] === myLocBase;
        });
    }

    // Enrich with live friend data
    const enriched = users.map(u => {
        const friend = (u.id && byId[u.id]) || (u.displayName && byName[(u.displayName || '').toLowerCase()]);
        return { ...u, _friend: friend || null };
    });

    _iimSortEntries(enriched);

    const now = Date.now();
    const hasTimers = enriched.some(u => u.joinedAt);
    const iStart = enriched.reduce((min, u) => (u.joinedAt && u.joinedAt < min) ? u.joinedAt : min, now);
    const iTotal = now - iStart;

    function fmtTimer(joinedAt) {
        return formatInstanceTimer(joinedAt, now);
    }

    const iimCols = _iimOrder(hasTimers);
    const iimLabels = {
        profile:  t('instance.table.profile', 'Profile'),
        timer:    t('instance.table.timer', 'Timer'),
        joined:   t('instance.table.joined', 'Joined'),
        name:     t('instance.table.display_name', 'Display Name'),
        avatar:   t('instance.table.avatar', 'Avatar'),
        rank:     t('instance.table.rank', 'Rank'),
        status:   t('instance.table.status', 'Status'),
        age:      '18+',
        platform: t('instance.table.platform', 'Platform'),
        language: t('instance.table.language', 'Language'),
        biolinks: t('people.list.header.bio_links', 'Bio Links'),
        presence: t('profiles.people.instance.presence', 'Presence'),
    };
    const listHead = `<div class="iim-list-head">${iimCols.map(id => _iimHeadCell(id, iimLabels[id])).join('')}</div>`;
    const iimGridStyle = `--iim-grid-cols:${iimCols.map(id => IIM_COL_WIDTHS[id]).join(' ')};`;

    const copyBadge = instNum
        ? `<span class="vrcn-id-clip" onclick="copyInstanceLink('${jsq(data.location || '')}')"><span class="msi" style="font-size:12px;">content_copy</span>#${esc(instNum)}</span>`
        : '';
    const wid = jsq(data.worldId || '');

    // Split enriched list into friends and non-friends
    const friendsEnriched = enriched.filter(u => !!u._friend);
    const othersEnriched  = enriched.filter(u => !u._friend);

    function makeRow(u) {
        const f           = u._friend;
        const isSelf      = currentVrcUser && u.id && u.id === currentVrcUser.id;
        const src         = isSelf ? currentVrcUser : f;
        const id          = u.id || '';
        const displayName = u.displayName || '?';
        const image       = src?.image           || u.image             || '';
        const status      = src?.status          || u.status            || '';
        const statusDesc  = src?.statusDescription ?? u.statusDescription ?? '';
        const tags        = (f?.tags?.length ? f.tags : null) || u.tags || [];
        const platform    = f?.platform          || u.platform          || '';
        const ageVerified = !!(f?.ageVerified || u.ageVerified);
        const is18 = (f?.ageVerificationStatus || u.ageVerificationStatus) === '18+';
        const avHtml = image
            ? `<div class="iim-av" style="background-image:url('${cssUrl(imgThumb(image, 64))}')"></div>`
            : `<div class="iim-av iim-av-letter">${esc(displayName[0].toUpperCase())}</div>`;
        const timerCell = hasTimers
            ? `<div class="iim-cell iim-muted-cell">${esc(fmtTimer(u.joinedAt))}</div>`
            : '';
        const trust = getTrustRank(tags);
        const rankCell = `<div class="iim-cell">${trust ? `<span class="vrcn-badge ${trust.cls}" style="font-size:calc(10px + var(--fs-off, 0px));">${esc(trust.label)}</span>` : ''}</div>`;
        const dotCls = statusDotClass(status);
        const statusCell = `<div class="iim-cell"><div class="iim-status-cell">
            ${status ? `<span class="vrc-status-dot ${dotCls}" style="width:7px;height:7px;flex-shrink:0;"></span>` : ''}
            <span style="font-size:calc(11px + var(--fs-off, 0px));">${esc(statusDesc || statusLabel(status))}</span>
        </div></div>`;
        let platIcon = '';
        if      (platform === 'standalonewindows') platIcon = `<span class="msi" title="${t('instance.platform.pc', 'PC')}" style="font-size:16px;color:var(--tx2);">computer</span>`;
        else if (platform === 'android')           platIcon = `<span class="msi" title="${t('instance.platform.quest', 'Quest')}" style="font-size:16px;color:var(--tx2);">view_in_ar</span>`;
        const platformCell = `<div class="iim-cell">${platIcon}</div>`;
        const langsHtml = tags.filter(x => x.startsWith('language_'))
            .map(x => `<span class="vrcn-badge">${esc(LANG_MAP[x] || x.replace('language_', '').toUpperCase())}</span>`).join('');
        const langCell  = `<div class="iim-cell"><div class="iim-lang-cell">${langsHtml}</div></div>`;
        const bioLinks  = (Array.isArray(u.bioLinks) && u.bioLinks.length ? u.bioLinks : f?.bioLinks) || [];
        const bioCell   = `<div class="iim-cell">${typeof _plBioLinksCell === 'function' ? _plBioLinksCell({ bioLinks }) : ''}</div>`;
        const nameCell  = `<div class="iim-cell"><span class="iim-name">${esc(displayName)}</span></div>`;
        const avatarCell = `<div class="iim-cell">${typeof instanceAvatarCellHtml === 'function' ? instanceAvatarCellHtml(u) : ''}</div>`;
        const ageCell   = `<div class="iim-cell">${is18 ? `<span class="vrcn-badge ip-age" style="font-size:calc(10px + var(--fs-off, 0px));">18+</span>` : (ageVerified ? `<span class="vrcn-badge ip-age" style="font-size:calc(10px + var(--fs-off, 0px));">Verified</span>` : '')}</div>`;
        const fromCell  = `<div class="iim-cell iim-muted-cell">${u.joinedAt ? esc(fmtTime(new Date(u.joinedAt))) : '&mdash;'}</div>`;
        let barHtml = '';
        if (iTotal > 0 && u.joinedAt) {
            const pStart   = u.joinedAt;
            const pEnd     = u.leftAt || now;
            const leftPct  = Math.max(0, Math.min(100, (pStart - iStart) / iTotal * 100));
            const widthPct = Math.max(0, Math.min(100 - leftPct, (pEnd - pStart) / iTotal * 100));
            const barCls   = (u._friend || isSelf) ? ' friend' : '';
            barHtml = `<div class="tl-player-bar-wrap"><div class="tl-player-bar${barCls}" style="left:${leftPct.toFixed(1)}%;width:${widthPct.toFixed(1)}%"></div></div>`;
        }
        const itemClick    = id ? ` onclick="openFriendDetail('${jsq(id)}')"` : '';
        const clickableCls = id ? ' clickable' : '';
        const cellMap = {
            profile:  `<div class="iim-cell iim-profile-cell">${avHtml}</div>`,
            timer:    timerCell,
            joined:   fromCell,
            name:     nameCell,
            avatar:   avatarCell,
            rank:     rankCell,
            status:   statusCell,
            age:      ageCell,
            platform: platformCell,
            language: langCell,
            biolinks: bioCell,
            presence: `<div class="iim-cell iim-bar-cell">${barHtml}</div>`,
        };
        return `<div class="iim-user-item${clickableCls}"${itemClick}>
            <div class="iim-user-row">${iimCols.map(id => cellMap[id] || '').join('')}</div>
        </div>`;
    }

    let bodyRows = '';
    if (friendsEnriched.length > 0)
        bodyRows += `<div class="iim-section-label"><div class="fd-group-rep-label" style="margin:0;">${tf('instance.sections.friends_in_instance', { count: friendsEnriched.length }, 'FRIENDS IN INSTANCE ({count})')}</div></div>` + friendsEnriched.map(makeRow).join('');
    if (othersEnriched.length > 0)
        bodyRows += `<div class="iim-section-label"><div class="fd-group-rep-label" style="margin:0;">${tf('instance.sections.players_in_instance', { count: othersEnriched.length }, 'PLAYERS IN INSTANCE ({count})')}</div></div>` + othersEnriched.map(makeRow).join('');

    const wc = (typeof dashWorldCache !== 'undefined' && data.worldId) ? (dashWorldCache[data.worldId] || null) : null;
    if (data.worldId && (!wc || (!wc.description && !wc._descFetched)) && typeof sendToCS === 'function') sendToCS({ action: 'vrcGetWorldInstancesDetail', worldId: data.worldId, locations: data.location ? [data.location] : [] });
    const worldAuthor   = wc?.authorName || '';
    const worldAuthorId = wc?.authorId || '';
    const worldDesc     = wc?.description || '';

    const bannerImg = thumb
        ? `<img class="mi-world-banner" src="${thumb}" onerror="this.style.display='none'">`
        : '';

    const authorHtml = worldAuthor
        ? `<div class="mi-world-author">${t('worlds.meta.by', 'by')} ${worldAuthorId
            ? `<span onclick="closeInstanceInfoModal();navOpenModal('friend','${jsq(worldAuthorId)}','${jsq(worldAuthor)}')" style="display:inline-flex;align-items:center;padding:1px 8px;border-radius:20px;background:var(--badge-bg);font-size:calc(11px + var(--fs-off, 0px));font-weight:600;color:var(--tx2);cursor:pointer;line-height:1.8;">${esc(worldAuthor)}</span>`
            : esc(worldAuthor)}</div>`
        : '';
    const descHtml = worldDesc ? `<div class="mi-world-description">${esc(worldDesc)}</div>` : '';

    const leftHtml = `<div class="mi-left">
        <div class="mi-world-banner-wrap">${bannerImg}<div class="mi-world-banner-fade"></div></div>
        <div class="mi-world-info">
            <div class="mi-world-name">${esc(name)}</div>
            ${authorHtml}
            ${descHtml}
        </div>
        <div class="mi-left-actions">
            <button class="vrcn-button-round mi-action-btn" onclick="closeInstanceInfoModal();openInviteModal()"><span class="msi" style="font-size:14px;">person_add</span> ${t('instance.actions.invite', 'Invite')}</button>
            <button class="vrcn-button-round mi-action-btn" onclick="closeInstanceInfoModal();openWorldSearchDetail('${wid}')">${t('dashboard.instances.open_world', 'Open World')}</button>
        </div>
    </div>`;

    const joinBtn = data.location
        ? `<button class="vrcn-button-round vrcn-btn-join" style="margin-left:auto;" title="${esc(t('common.join', 'Join'))}" onclick="sendToCS({action:'vrcJoinFriend',location:'${jsq(data.location)}'})"><span class="msi" style="font-size:14px;">login</span> ${esc(t('common.join', 'Join'))}</button>`
        : '';
    const cardHeader = `<div class="mi-instance-header">
        <span class="vrcn-badge ${instCls}">${instLabel}</span>
        ${copyBadge}
        ${data.ageGate ? `<span class="vrcn-badge" style="background:rgba(255,75,85,.15);color:var(--err);">${esc(t('worlds.instances.age_gated', 'Age Gated'))}</span>` : ''}
        ${getOwnerBadgeHtml(data.ownerId || '', data.ownerName || '', data.ownerGroup || '', 'closeInstanceInfoModal()')}
        <span class="vrcn-badge"><span class="msi" style="font-size:11px;">person</span>&nbsp;${users.length || data.nUsers || 0}${data.capacity ? '/' + data.capacity : ''}</span>
        ${joinBtn}
    </div>`;

    const playersHtml = enriched.length > 0
        ? `<div class="iim-list${hasTimers ? ' has-timers' : ''}" style="${iimGridStyle}">${listHead}<div class="iim-list-body">${bodyRows}</div></div>`
        : `<div style="padding:14px;color:var(--tx3);font-size:calc(12px + var(--fs-off, 0px));">${t('instance.no_player_data_available', 'No player data available.')}</div>`;

    const rightHtml = `<div class="mi-right"><div class="mi-right-scroll" style="overflow:auto;"><div class="mi-instance-list"><div class="mi-instance-card">${cardHeader}${playersHtml}</div></div></div></div>`;

    const prevScroller  = c.querySelector('.mi-right-scroll');
    const prevScrollTop = prevScroller?.scrollTop || 0;

    const leftHidden = _iimLeftHidden();
    c.classList.toggle('iim-no-left', leftHidden);
    const panelAction = {
        icon: leftHidden ? 'left_panel_open' : 'left_panel_close',
        title: leftHidden
            ? t('instance.actions.show_world_panel', 'Show world panel')
            : t('instance.actions.hide_world_panel', 'Hide world panel'),
        onclick: 'iimToggleLeftPanel()',
    };
    const moreAction = {
        icon: 'open_in_full',
        title: t('instance.actions.show_more_info', 'Show More Informations'),
        onclick: 'iimOpenInstanceTab()',
    };
    c.innerHTML = `${renderModalBar(name, [moreAction, panelAction, modalCloseAction('closeInstanceInfoModal()')])}<div class="mi-layout">${leftHtml}${rightHtml}</div>`;

    m.style.display = 'flex';
    if (prevScrollTop > 0) {
        const newScroll = c.querySelector('.mi-right-scroll');
        if (newScroll) newScroll.scrollTop = prevScrollTop;
    }
}

const IIM_SORT_KEY = 'vrcn_iim_sort';
const IIM_LEFT_KEY = 'vrcn_iim_hide_left';

function _iimLeftHidden() {
    try { return localStorage.getItem(IIM_LEFT_KEY) === '1'; } catch { return false; }
}

function iimOpenInstanceTab() {
    closeInstanceInfoModal();
    if (typeof showTab === 'function') showTab(3);
    if (typeof setPeopleFilter === 'function') setPeopleFilter('instance');
}

function iimToggleLeftPanel() {
    try { localStorage.setItem(IIM_LEFT_KEY, _iimLeftHidden() ? '0' : '1'); } catch {}
    openInstanceInfoModal();
}

const IIM_STATUS_ORDER = ['join me', 'active', 'ask me', 'busy', 'offline'];
let _iimSort = null;

function _iimSortState() {
    if (_iimSort) return _iimSort;
    let saved = null;
    try { saved = JSON.parse(localStorage.getItem(IIM_SORT_KEY) || 'null'); } catch {}
    _iimSort = {
        id:  typeof saved?.id === 'string' ? saved.id : 'joined',
        dir: saved?.dir === 'desc' ? 'desc' : 'asc',
    };
    return _iimSort;
}

function _iimTrustOrder(tags) {
    const rank = (typeof getTrustRank === 'function') ? getTrustRank(tags || []) : null;
    const order = ['rank-visitor', 'rank-new', 'rank-user', 'rank-known', 'rank-trusted'];
    const idx = rank ? order.indexOf(rank.cls) : -1;
    return idx < 0 ? -1 : idx;
}

function _iimSortValue(u, id) {
    const live = u._friend;
    switch (id) {
        case 'timer':
        case 'joined':
        case 'presence': return u.joinedAt || 0;
        case 'profile':
        case 'name':     return (u.displayName || '').toLowerCase();
        case 'avatar':   return (u.avatarName || '').toLowerCase();
        case 'rank':     return _iimTrustOrder((live?.tags?.length ? live.tags : null) || u.tags || []);
        case 'status': {
            const s = (live?.status || u.status || '').toLowerCase();
            const idx = IIM_STATUS_ORDER.indexOf(s);
            return idx < 0 ? IIM_STATUS_ORDER.length : idx;
        }
        case 'age':      return (live?.ageVerificationStatus || u.ageVerificationStatus) === '18+' ? 2 : ((live?.ageVerified || u.ageVerified) ? 1 : 0);
        case 'biolinks': {
            const bl = (Array.isArray(u.bioLinks) && u.bioLinks.length ? u.bioLinks : live?.bioLinks) || [];
            return bl.filter(Boolean).length;
        }
        case 'platform': return (live?.platform || u.platform || '').toLowerCase();
        case 'language': {
            const tags = (live?.tags?.length ? live.tags : null) || u.tags || [];
            const lang = tags.find(x => x.startsWith('language_')) || '';
            return lang.replace('language_', '');
        }
        default:         return 0;
    }
}

function _iimSortEntries(entries) {
    const st  = _iimSortState();
    const dir = st.dir === 'asc' ? 1 : -1;
    entries.sort((a, b) => {
        const va = _iimSortValue(a, st.id);
        const vb = _iimSortValue(b, st.id);
        if (va === vb) return (a.joinedAt || 0) - (b.joinedAt || 0);
        return (va > vb ? 1 : -1) * dir;
    });
}

const IIM_COL_WIDTHS = {
    profile:  '104px',
    timer:    '92px',
    joined:   '100px',
    name:     'minmax(120px, .72fr)',
    avatar:   'minmax(120px, .9fr)',
    rank:     '110px',
    status:   'minmax(100px, 136px)',
    age:      '76px',
    platform: '98px',
    language: 'minmax(100px, .8fr)',
    biolinks: '96px',
    presence: '200px',
};
const IIM_DEFAULT_ORDER = ['profile', 'timer', 'joined', 'name', 'avatar', 'rank', 'status', 'age', 'platform', 'language', 'biolinks', 'presence'];
const IIM_ORDER_KEY = 'vrcn_iim_order';

function _iimOrder(hasTimers) {
    let saved = null;
    try { saved = JSON.parse(localStorage.getItem(IIM_ORDER_KEY) || 'null'); } catch {}
    let order;
    if (Array.isArray(saved)) {
        order = saved.filter(id => IIM_DEFAULT_ORDER.includes(id));
        IIM_DEFAULT_ORDER.forEach((id, idx) => {
            if (!order.includes(id)) order.splice(Math.min(idx, order.length), 0, id);
        });
    } else {
        order = IIM_DEFAULT_ORDER.slice();
    }
    return hasTimers ? order : order.filter(id => id !== 'timer' && id !== 'presence');
}

function _iimSaveOrder(order) {
    try { localStorage.setItem(IIM_ORDER_KEY, JSON.stringify(order)); } catch {}
}

function _iimHeadCell(id, label, extraCls) {
    const st     = _iimSortState();
    const active = st.id === id;
    const arrow  = active ? (st.dir === 'asc' ? 'arrow_upward' : 'arrow_downward') : 'unfold_more';
    return `<div class="iim-head-cell iim-head-sortable${active ? ' iim-head-sorted' : ''}${extraCls ? ' ' + extraCls : ''}"
        data-iim-col="${id}" onclick="iimSort('${id}')" title="${esc(t('timeline.list.header.hint', 'Click to sort, drag to reorder'))}">
        <span>${esc(label)}</span><span class="msi iim-head-arrow">${arrow}</span><span class="msi iim-head-grip" title="${esc(t('timeline.list.header.reorder', 'Drag to reorder'))}">drag_indicator</span>
    </div>`;
}

function iimSort(id) {
    const st = _iimSortState();
    if (st.id === id) st.dir = st.dir === 'asc' ? 'desc' : 'asc';
    else { st.id = id; st.dir = 'asc'; }
    try { localStorage.setItem(IIM_SORT_KEY, JSON.stringify(st)); } catch {}
    openInstanceInfoModal();
}

function closeInstanceInfoModal() {
    document.getElementById('modalInstanceInfo').style.display = 'none';
}

//Avatar Lookup avtrdb context logic
function handleInstanceAvatarFound(payload) {
    const { userId, avatarId } = payload;
    if (!userId) return;
    if (avatarId) openAvatarDetail(avatarId);
    else showToast(false, t('context_menu.avatar_not_found', 'No public avatar found'));
}

function ctxCheckAvatar(userId) {
    sendToCS({ action: 'vrcGetInstanceAvatars', userIds: [userId] });
}

let _instanceInfoTimer = null;
function requestInstanceInfo() {
    if (!currentVrcUser) return;
    clearTimeout(_instanceInfoTimer);
    _instanceInfoTimer = setTimeout(() => sendToCS({ action: 'vrcGetCurrentInstance' }), 500);
}

let _iimDrag = null;

document.addEventListener('mousedown', e => {
    const grip = e.target.closest?.('.iim-head-grip');
    if (!grip || e.button !== 0) return;
    const cell = grip.closest('.iim-head-cell');
    const head = cell?.closest('.iim-list-head');
    if (!cell || !head) return;
    e.preventDefault();
    e.stopPropagation();
    _iimDrag = { head, id: cell.dataset.iimCol, moved: false };
    cell.classList.add('iim-head-dragging');
});

document.addEventListener('mousemove', e => {
    if (!_iimDrag) return;
    _iimDrag.moved = true;
    const cells = [...(_iimDrag.head.querySelectorAll('.iim-head-cell'))];
    const from = cells.findIndex(c => c.dataset.iimCol === _iimDrag.id);
    if (from < 0) return;
    const target = cells.findIndex(c => {
        const r = c.getBoundingClientRect();
        return e.clientX >= r.left && e.clientX <= r.right;
    });
    if (target < 0 || target === from) return;
    const node = cells[from];
    const ref  = cells[target];
    _iimDrag.head.insertBefore(node, target > from ? ref.nextSibling : ref);
}, { passive: true });

document.addEventListener('mouseup', () => {
    if (!_iimDrag) return;
    const { head, moved } = _iimDrag;
    head.querySelector('.iim-head-dragging')?.classList.remove('iim-head-dragging');
    _iimDrag = null;
    if (!moved) return;
    const order = [...head.querySelectorAll('.iim-head-cell')].map(c => c.dataset.iimCol).filter(Boolean);
    const full = IIM_DEFAULT_ORDER.filter(id => !order.includes(id));
    _iimSaveOrder(order.concat(full));
    openInstanceInfoModal();
});
