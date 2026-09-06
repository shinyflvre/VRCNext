/* === Timeline Modal === */
// Which detail modal the timeline-detail content currently targets. Normally
// '#modalDetail'. When opened while '#modalDetail' is already showing (the World
// modal lives there), detail renders into the stacked '#modalDetail2' so the
// underlying modal stays open — same behaviour as the profile modal.
let _tlMid = 'modalDetail';
let _tlStacked = false;

function openTlDetail(id, stacked) {
    const ev = timelineEvents.find(e => e.id === id)
             || _tlSearchEvents.find(e => e.id === id)
             || _fdTimelineEvents.find(e => e.id === id)
             || (typeof _wdInstanceHistory !== 'undefined' ? _wdInstanceHistory.find(e => e.id === id) : null)
             || (typeof _icData !== 'undefined' ? _icData.find(e => e.id === id) : null);
    if (!ev) return;

    if (ev.type === 'photo') {
        const libItem = (typeof libraryFiles !== 'undefined' && ev.photoPath)
            ? libraryFiles.find(f => f.path === ev.photoPath)
            : null;
        openPhotoDetail(libItem || {
            name:     ev.photoPath ? ev.photoPath.split(/[\\/]/).pop() : t('timeline.photo', 'Photo'),
            path:     ev.photoPath || '',
            url:      ev.photoUrl  || '',
            modified: ev.timestamp,
            size:     '',
            worldId:  ev.worldId   || '',
            players:  ev.players   || [],
            imgW: 0, imgH: 0,
        });
        return;
    }

    const mainOpen = !!stacked || document.getElementById('modalDetail')?.style.display === 'flex';
    _tlMid = mainOpen ? 'modalDetail2' : 'modalDetail';
    _tlStacked = !!stacked;
    const el = document.getElementById(mainOpen ? 'detailModalContent2' : 'detailModalContent');
    if (!el) return;

    if (!mainOpen && typeof navSetCurrent === 'function') navSetCurrent('tlEvent', id);

    switch (ev.type) {
        case 'instance_join': renderTlDetailJoin(ev, el);      break;
        case 'first_meet':    renderTlDetailMeet(ev, el);      break;
        case 'meet_again':    renderTlDetailMeetAgain(ev, el); break;
        case 'notification':  renderTlDetailNotif(ev, el);     break;
        case 'avatar_switch': renderTlDetailAvatar(ev, el);    break;
        case 'video_url':     renderTlDetailUrl(ev, el);       break;
        case 'moderation':    renderTlDetailModeration(ev, el); break;
        case 'profile':       renderTlDetailProfile(ev, el);   break;
    }

    const _mb = document.querySelector(`#${_tlMid} .modal-box`);
    if (_mb) _mb.classList.toggle('narrow', ev.type !== 'instance_join');
    const _ov = document.getElementById(_tlMid);
    _ov.classList.remove('wd-style-compact', 'gd-style-compact');
    _ov.classList.toggle('tl-style-compact', ev.type === 'instance_join');
    _ov.style.display = 'flex';
}

// Navigate to a specific event in the Timeline tab

// Shared helpers for timeline detail modals
function _tlMr(label, val) {
    return `<div style="display:flex;justify-content:space-between;gap:8px;align-items:baseline;font-size:calc(11px + var(--fs-off, 0px));"><span style="color:var(--tx2);">${label}</span><span style="color:var(--tx1);text-align:right;">${val}</span></div>`;
}
function _tlCreatorName(ownerId) {
    if (typeof currentVrcUser !== 'undefined' && currentVrcUser && currentVrcUser.id === ownerId)
        return currentVrcUser.displayName || '';
    if (typeof vrcFriendsData !== 'undefined')
        return (vrcFriendsData.find(f => f.id === ownerId) || {}).displayName || '';
    return '';
}

function _tlCreatorRow(loc) {
    const { ownerId } = parseFriendLocation(loc || '');
    if (!ownerId) return '';
    const label = esc(t('timeline.detail.instance_creator', 'Instance Creator'));
    if (ownerId.startsWith('grp_')) {
        const cached = (typeof dashGroupCache !== 'undefined' && dashGroupCache[ownerId]) || null;
        const gName = (cached && cached.name)
            || (typeof myGroups !== 'undefined' ? (myGroups.find(g => g.id === ownerId) || {}).name : '')
            || '';
        if (gName)
            return _tlMr(label, `<span class="tl-creator-link" onclick="navOpenModal('group','${jsq(ownerId)}','${jsq(gName)}')">${esc(gName)}</span>`);
        sendToCS({ action: 'vrcResolveGroups', groupIds: [ownerId] });
        return _tlMr(label, `<span id="tlCreatorSlot" data-owner-id="${esc(ownerId)}" style="color:var(--tx3);">${esc(t('common.loading', 'Loading...'))}</span>`);
    }
    if (!ownerId.startsWith('usr_')) return '';
    const name = _tlCreatorName(ownerId);
    if (name)
        return _tlMr(label, `<span class="tl-creator-link" onclick="navOpenModal('friend','${jsq(ownerId)}','${jsq(name)}')">${esc(name)}</span>`);
    sendToCS({ action: 'vrcGetUserBasic', userId: ownerId, contextId: 'tlCreator' });
    return _tlMr(label, `<span id="tlCreatorSlot" data-owner-id="${esc(ownerId)}" style="color:var(--tx3);">${esc(t('common.loading', 'Loading...'))}</span>`);
}

function _tlCreatorFill(type, id, name) {
    const slot = document.getElementById('tlCreatorSlot');
    if (!slot || slot.dataset.ownerId !== id) return;
    if (!name) { slot.textContent = t('timeline.unknown', 'Unknown'); return; }
    slot.outerHTML = `<span class="tl-creator-link" onclick="navOpenModal('${type}','${jsq(id)}','${jsq(name)}')">${esc(name)}</span>`;
}

function tlOnCreatorResolved(payload) {
    if (!payload) return;
    _tlCreatorFill('friend', payload.id, payload.displayName || '');
}

function tlOnCreatorGroupResolved(payload) {
    const slot = document.getElementById('tlCreatorSlot');
    if (!slot || !payload || typeof payload !== 'object') return;
    const entry = payload[slot.dataset.ownerId];
    if (!entry) return;
    _tlCreatorFill('group', slot.dataset.ownerId, entry.name || '');
}

function _tlInfoCard(title, rowsHtml) {
    return `<div class="fd-info-card"><div class="fd-group-rep-label">${title}</div><div style="display:grid;gap:6px;">${rowsHtml}</div></div>`;
}
function _tlBar(title) {
    if (_tlMid === 'modalDetail' && typeof navUpdateLabel === 'function') navUpdateLabel(title);
    return renderModalBar(title, [modalCloseAction('closeTlDetail()')]);
}

function closeTlDetail(fromNav = false) {
    const ov = document.getElementById(_tlMid);
    if (ov) ov.style.display = 'none';
    const wasStacked = _tlStacked;
    _tlStacked = false;
    if (wasStacked) return;
    if (!fromNav && typeof navClear === 'function') navClear();
}
function _tlBanner(hasThumb) {
    return hasThumb ? `<div class="fd-banner" id="tl-banner-slot"><div class="fd-banner-fade"></div></div>` : '';
}
function _tlInsertBanner(el, key, src) {
    if (!src || !key) return;
    const s = el.querySelector('#tl-banner-slot');
    if (!s) return;
    const img = new Image();
    img.src = src;
    img.onerror = () => { if (img.parentElement) img.parentElement.style.display = 'none'; };
    s.insertBefore(img, s.firstChild);
}
function _tlAvRow(image, name, label, labelColor) {
    const av = image
        ? `<div class="tl-detail-av" style="background-image:url('${cssUrl(imgThumb(image, 128))}')"></div>`
        : `<div class="tl-detail-av tl-detail-av-letter">${esc((name || '?')[0].toUpperCase())}</div>`;
    return `<div style="display:flex;gap:16px;align-items:center;margin-bottom:16px;">${av}<div>
        <h2 style="margin:0 0 4px;color:var(--tx0);font-size:calc(18px + var(--fs-off, 0px));">${esc(name || t('timeline.unknown', 'Unknown'))}</h2>
        <div style="font-size:calc(11px + var(--fs-off, 0px));color:${labelColor};font-weight:700;letter-spacing:.05em;">${esc(label)}</div>
    </div></div>`;
}

// Detail: instance join

// Falls back to legacy single-value joinedAt/leftAt when arrays are missing.
function _tlPlayerSessions(p) {
    let joins = Array.isArray(p.joinedAts) ? p.joinedAts.slice() : (p.joinedAt ? [p.joinedAt] : []);
    let lefts = Array.isArray(p.leftAts)   ? p.leftAts.slice()   : (p.leftAt   ? [p.leftAt]   : []);
    return { joins, lefts };
}

function _tlPlayerCard(p, instanceStart, instanceEnd) {
    const name   = p.displayName || '?';
    const live   = p.userId ? vrcFriendsData.find(f => f.id === p.userId) : null;
    const image  = live?.image || p.image || '';
    const av     = image
        ? `<div class="tl-player-card-av" style="background-image:url('${cssUrl(imgThumb(image, 64))}')"></div>`
        : `<div class="tl-player-card-av">${esc(name[0].toUpperCase())}</div>`;
    const badge  = live ? `<span class="vrcn-badge bdg-friend"><span class="msi" style="font-size:10px;">check_circle</span>${t('profiles.badges.friend', 'Friend')}</span>` : '';
    const onclick = p.userId ? `onclick="navOpenModal('friend','${jsq(p.userId)}','${jsq(name)}')"` : '';
    const clickCls = p.userId ? ' clickable' : '';

    const { joins, lefts } = _tlPlayerSessions(p);
    const nowIso = new Date().toISOString();

    // Open session uses "now" as end.
    let totalSecs = 0;
    for (let i = 0; i < joins.length; i++) {
        const jMs = new Date(joins[i]).getTime();
        const lIso = i < lefts.length ? lefts[i] : nowIso;
        const lMs = new Date(lIso).getTime();
        if (isFinite(jMs) && isFinite(lMs) && lMs > jMs) totalSecs += Math.floor((lMs - jMs) / 1000);
    }

    let timesHtml;
    if (joins.length === 0 && lefts.length === 0) {
        timesHtml = `${esc(t('timeline.detail.none', 'None'))}`;
    } else {
        const ongoing = lefts.length < joins.length
            ? `&nbsp;·&nbsp;<span style="color:var(--ok);">&#9679;&nbsp;${esc(t('timeline.detail.ongoing', 'Ongoing'))}</span>`
            : '';
        const visits = joins.length > 1
            ? `${esc(tf('timeline.detail.visits_count', { count: joins.length }, '{count} visits'))}&nbsp;·&nbsp;`
            : '';
        const spent = totalSecs > 0
            ? `${esc(t('nav.time_spent', 'Time Spent'))} ${esc(formatDuration(totalSecs))}`
            : '';
        timesHtml = `${visits}${spent}${ongoing}`;
    }

    let barHtml = '';
    const iStart = instanceStart ? new Date(instanceStart).getTime() : 0;
    const nowMs  = Date.now();
    let   iEnd   = instanceEnd ? new Date(instanceEnd).getTime() : nowMs;
    // Extend iEnd if a session ends after the recorded instance close (re-join after a stale close).
    for (let i = 0; i < joins.length; i++) {
        const endCandidate = i < lefts.length ? new Date(lefts[i]).getTime() : nowMs;
        if (isFinite(endCandidate) && endCandidate > iEnd) iEnd = endCandidate;
    }
    const total = iEnd - iStart;
    if (total > 0 && joins.length > 0) {
        const barCls = live ? ' friend' : '';
        let segments = '';
        for (let i = 0; i < joins.length; i++) {
            const jIso = joins[i];
            const lIso = i < lefts.length ? lefts[i] : null;
            const pStart = new Date(jIso).getTime();
            const pEnd   = lIso ? new Date(lIso).getTime() : iEnd;
            if (!isFinite(pStart) || !isFinite(pEnd) || pEnd < pStart) continue;
            const leftPct  = Math.max(0, Math.min(100, (pStart - iStart) / total * 100));
            const widthPct = Math.max(0.6, Math.min(100 - leftPct, (pEnd - pStart) / total * 100));
            const fromStr = tlFormatTime(jIso);
            const untilStr = lIso ? tlFormatTime(lIso) : t('timeline.detail.ongoing', 'Ongoing');
            const tip = `${t('timeline.detail.from', 'From')} ${fromStr} · ${t('timeline.detail.until', 'Until')} ${untilStr}`;
            segments += `<div class="tl-player-bar${barCls}" style="left:${leftPct.toFixed(2)}%;width:${widthPct.toFixed(2)}%" title="${esc(tip)}"></div>`;
        }
        if (segments) barHtml = `<div class="tl-player-bar-wrap">${segments}</div>`;
    }

    return `<div class="tl-player-card${clickCls}" data-pname="${esc(name.toLowerCase())}" data-visits="${joins.length}" data-secs="${totalSecs}" data-friend="${live ? 1 : 0}" ${onclick} style="flex-wrap:wrap;">${av}<div class="tl-player-card-info"><div class="tl-player-card-name"><span>${esc(name)}</span>${badge}</div><div class="tl-player-card-times">${timesHtml}</div></div>${barHtml ? `<div style="flex-basis:100%;padding:0 2px;">${barHtml}</div>` : ''}</div>`;
}

function _tlSortPlayers(sel) {
    const grid = document.getElementById('tlPlayersGrid');
    if (!grid || !sel.value) return;
    const mode = sel.value;
    const num = (c, k) => parseFloat(c.dataset[k]) || 0;
    const cards = Array.from(grid.querySelectorAll('.tl-player-card'));
    cards.sort((a, b) => {
        if (mode === 'visits')      return num(b, 'visits') - num(a, 'visits');
        if (mode === 'time')        return num(b, 'secs') - num(a, 'secs');
        if (mode === 'visits_time') return (num(b, 'visits') - num(a, 'visits')) || (num(b, 'secs') - num(a, 'secs'));
        if (mode === 'friends')     return num(b, 'friend') - num(a, 'friend');
        return 0;
    });
    cards.forEach(c => grid.appendChild(c));
}

function _tlFilterPlayers(inp) {
    const q = (inp.value || '').toLowerCase().trim();
    const grid = document.getElementById('tlPlayersGrid');
    if (!grid) return;
    let visible = 0;
    grid.querySelectorAll('.tl-player-card').forEach(c => {
        const show = !q || (c.dataset.pname || '').includes(q);
        c.style.display = show ? '' : 'none';
        if (show) visible++;
    });
    const empty = document.getElementById('tlPlayersEmpty');
    if (empty) empty.style.display = visible ? 'none' : '';
}

function renderTlDetailJoin(ev, el) {
    const players = ev.players || [];
    const instEnd = ev.leftAt || new Date().toISOString();
    let grid = '';
    players.forEach(p => { grid += _tlPlayerCard(p, ev.timestamp, instEnd); });

    const dateStr = tlFormatLongDate(ev.timestamp);
    const fromStr = ev.timestamp ? tlFormatTime(ev.timestamp) : null;
    const toStr   = ev.leftAt    ? tlFormatTime(ev.leftAt)    : null;

    const loc = ev.location || '';
    const { instanceType } = parseFriendLocation(loc);
    const { cls: instCls, label: instLabel } = getInstanceBadge(instanceType);
    const regionCode  = parseInstanceRegion(loc);
    const instIdMatch = loc.match(/:(\d+)/);
    const instanceId  = instIdMatch ? instIdMatch[1] : '';

    const worldRowClick = ev.worldId ? ` onclick="navOpenModal('worldSearch','${jsq(ev.worldId)}','${jsq(ev.worldName || '')}')" style="cursor:pointer;"` : '';
    const infoRows = [
        fromStr ? _tlMr(esc(t('timeline.detail.from', 'From')), esc(fromStr)) : '',
        (fromStr && ev.tracked) ? _tlMr(esc(t('timeline.detail.to', 'To')), toStr ? esc(toStr) : `<span style="color:var(--ok);">&#9679;&nbsp;${esc(t('timeline.detail.ongoing', 'Ongoing'))}</span>`) : '',
        (ev.tracked && ev.leftAt && ev.timestamp) ? _tlMr(esc(t('nav.time_spent', 'Time Spent')), esc(formatDuration(Math.floor((new Date(ev.leftAt) - new Date(ev.timestamp)) / 1000)))) : '',
        ev.worldId ? `<div style="display:flex;justify-content:space-between;gap:8px;align-items:baseline;font-size:calc(11px + var(--fs-off, 0px));"${worldRowClick}><span style="color:var(--tx2);">${esc(t('timeline.detail.world', 'World'))}</span><span style="color:var(--accent-lt);text-align:right;">${esc(ev.worldName || ev.worldId)}</span></div>` : '',
        _tlMr(esc(t('timeline.detail.instance_type', 'Instance Type')), `<span class="vrcn-badge ${instCls}">${instLabel}</span>`),
        regionCode ? _tlMr(esc(t('timeline.detail.server', 'Server')), `<span class="vrcn-badge"><span class="msi" style="font-size:10px;">language</span>${esc(getRegionShortLabel(regionCode))}</span>`) : '',
        instanceId ? _tlMr(esc(t('timeline.detail.instance_id', 'Instance ID')), `<span style="font-family:monospace;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx2);">#${esc(instanceId)}</span>`) : '',
        _tlCreatorRow(loc),
    ].filter(Boolean).join('');

    const canCopyLink = !!(ev.location && ev.location.indexOf(':') > 0 && ev.location.startsWith('wrld_'));
    const leftHtml = `<div class="fd-left">
        <div class="fd-left-banner" id="tl-banner-slot">${ev.worldThumb ? '<div class="fd-banner-fade"></div>' : ''}</div>
        <div class="fd-left-body">
            <div>
                <h2 style="margin:0 0 4px;color:var(--tx0);font-size:calc(18px + var(--fs-off, 0px));">${esc(ev.worldName || ev.worldId || t('timeline.unknown_world', 'Unknown World'))}</h2>
                <div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx2);margin-bottom:12px;">${esc(dateStr)}</div>
            </div>
            <div class="fd-actions">
                <button class="vrcn-button-round vrcn-btn-join" title="${esc(t('instance.actions.force_join', 'Force-Join'))}" onclick="sendToCS({action:'vrcJoinFriend',location:'${jsq(ev.location)}'});"><span class="msi" style="font-size:16px;">play_arrow</span></button>
                ${canCopyLink ? `<button class="vrcn-button-round" title="${esc(t('timeline.actions.copy_instance_link', 'Copy Instance Link'))}" onclick="copyInstanceLink('${jsq(ev.location)}')"><span class="msi" style="font-size:16px;">content_copy</span></button>` : ''}
            </div>
            ${_tlInfoCard(esc(t('timeline.detail.info', 'Info')), infoRows)}
        </div>
    </div>`;

    const rightHtml = `<div class="fd-right">
        <div class="tl-players-head">
            <div class="fd-group-rep-label" style="margin-bottom:6px;">${esc(tf('timeline.detail.players_in_instance', { count: players.length }, `Players in instance (${players.length})`))}</div>
            <div style="font-size:calc(10.5px + var(--fs-off, 0px));color:var(--tx2);line-height:1.4;${players.length ? 'margin-bottom:10px;' : ''}">${esc(t('timeline.detail.players_disclaimer', 'Shows when each player was tracked while you were in the instance. This does not mean they joined or left at those exact times.'))}</div>
            ${players.length ? `<div class="search-bar-row" style="margin-bottom:0;"><span class="msi search-ico">search</span><input type="text" id="tlPlayersSearch" class="vrcn-input" placeholder="${esc(t('timeline.detail.search_players', 'Search players...'))}" style="background:var(--bg-input);" oninput="_tlFilterPlayers(this)"><select id="tlPlayersSort" class="vrcn-dropdown" style="min-width:150px;" onchange="_tlSortPlayers(this)">
                <option value="">${esc(t('timeline.detail.sort_placeholder', 'Sort...'))}</option>
                <option value="visits">${esc(t('timeline.detail.sort_visits', 'Visits'))}</option>
                <option value="time">${esc(t('timeline.detail.sort_time', 'Overall Time'))}</option>
                <option value="visits_time">${esc(t('timeline.detail.sort_visits_time', 'Visits and Time'))}</option>
                <option value="friends">${esc(t('timeline.detail.sort_friends', 'Friends'))}</option>
            </select></div>` : ''}
        </div>
        <div class="fd-right-scroll">
            ${players.length
                ? `<div class="tl-players-grid" id="tlPlayersGrid" style="margin-top:0;">${grid}</div>
                   <div id="tlPlayersEmpty" style="display:none;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);padding:12px 2px;">${esc(t('timeline.detail.no_players_match', 'No players match your search.'))}</div>`
                : `<div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);padding:12px 2px;">${esc(t('timeline.detail.no_players', 'No players tracked in this instance.'))}</div>`}
        </div>
    </div>`;

    el.innerHTML = `${_tlBar(ev.worldName || ev.worldId || t('timeline.unknown_world', 'Unknown World'))}<div class="fd-layout">${leftHtml}${rightHtml}</div>`;
    const _tlSortSel = el.querySelector('#tlPlayersSort');
    if (_tlSortSel && typeof initVnSelect === 'function') initVnSelect(_tlSortSel);
    _tlInsertBanner(el, ev.worldId || ev.id, ev.worldThumb);
}

// Detail: first meet

function renderTlDetailMeet(ev, el) {
    const dateStr = tlFormatLongDate(ev.timestamp);
    const timeStr = tlFormatTime(ev.timestamp);
    const worldRowClick = ev.worldId ? ` onclick="navOpenModal('worldSearch','${jsq(ev.worldId)}','${jsq(ev.worldName || '')}')" style="cursor:pointer;"` : '';
    const infoRows = [
        _tlMr(esc(t('timeline.detail.date', 'Date')), esc(dateStr)),
        _tlMr(esc(t('timeline.detail.time', 'Time')), esc(timeStr)),
        ev.worldId ? `<div style="display:flex;justify-content:space-between;gap:8px;align-items:baseline;font-size:calc(11px + var(--fs-off, 0px));"${worldRowClick}><span style="color:var(--tx2);">${esc(t('timeline.detail.world', 'World'))}</span><span style="color:var(--accent-lt);text-align:right;">${esc(ev.worldName || ev.worldId)}</span></div>` : '',
    ].filter(Boolean).join('');
    el.innerHTML = `${_tlBar(ev.userName || t('timeline.unknown', 'Unknown'))}<div class="fd-content" style="padding:20px 0;">
        ${_tlAvRow(ev.userImage, ev.userName, t('timeline.detail.first_meet', 'FIRST MEET'), 'var(--cyan)')}
        ${_tlInfoCard(esc(t('timeline.detail.info', 'Info')), infoRows)}
        ${ev.userId ? `<div style="margin-top:14px;display:flex;gap:8px;">
            <button class="vrcn-button-round vrcn-btn-join" onclick="navOpenModal('friend','${jsq(ev.userId)}','${jsq(ev.userName || '')}')">${esc(t('timeline.actions.view_profile', 'View Profile'))}</button>
        </div>` : ''}
    </div>`;
}

// Detail: meet again

function renderTlDetailMeetAgain(ev, el) {
    const dateStr = tlFormatLongDate(ev.timestamp);
    const timeStr = tlFormatTime(ev.timestamp);
    const worldRowClick = ev.worldId ? ` onclick="navOpenModal('worldSearch','${jsq(ev.worldId)}','${jsq(ev.worldName || '')}')" style="cursor:pointer;"` : '';
    const infoRows = [
        _tlMr(esc(t('timeline.detail.date', 'Date')), esc(dateStr)),
        _tlMr(esc(t('timeline.detail.time', 'Time')), esc(timeStr)),
        ev.worldId ? `<div style="display:flex;justify-content:space-between;gap:8px;align-items:baseline;font-size:calc(11px + var(--fs-off, 0px));"${worldRowClick}><span style="color:var(--tx2);">${esc(t('timeline.detail.world', 'World'))}</span><span style="color:var(--accent-lt);text-align:right;">${esc(ev.worldName || ev.worldId)}</span></div>` : '',
    ].filter(Boolean).join('');
    el.innerHTML = `${_tlBar(ev.userName || t('timeline.unknown', 'Unknown'))}<div class="fd-content" style="padding:20px 0;">
        ${_tlAvRow(ev.userImage, ev.userName, t('timeline.detail.met_again', 'MET AGAIN'), '#6554FF')}
        ${_tlInfoCard(esc(t('timeline.detail.info', 'Info')), infoRows)}
        ${ev.userId ? `<div style="margin-top:14px;display:flex;gap:8px;">
            <button class="vrcn-button-round vrcn-btn-join" onclick="navOpenModal('friend','${jsq(ev.userId)}','${jsq(ev.userName || '')}')">${esc(t('timeline.actions.view_profile', 'View Profile'))}</button>
        </div>` : ''}
    </div>`;
}

// Detail: notification

function renderTlDetailNotif(ev, el) {
    const dateStr   = tlFormatLongDate(ev.timestamp);
    const timeStr   = tlFormatTime(ev.timestamp);
    const typeLabel = tlNotifTypeLabel(ev.notifType);
    const loc       = ev.location || '';
    const hasInst   = loc.startsWith('wrld_') && loc.indexOf(':') > 0;

    let instRows = '';
    if (hasInst) {
        const { instanceType } = parseFriendLocation(loc);
        const { cls: instCls, label: instLabel } = getInstanceBadge(instanceType);
        const idMatch    = loc.match(/:(\d+)/);
        const instanceId = idMatch ? idMatch[1] : '';
        const worldClick = ev.worldId ? ` onclick="navOpenModal('worldSearch','${jsq(ev.worldId)}','${jsq(ev.worldName || '')}')" style="cursor:pointer;"` : '';
        instRows = [
            (ev.worldName || ev.worldId)
                ? `<div style="display:flex;justify-content:space-between;gap:8px;align-items:baseline;font-size:calc(11px + var(--fs-off, 0px));"${worldClick}><span style="color:var(--tx2);">${esc(t('timeline.detail.world', 'World'))}</span><span style="color:var(--accent-lt);text-align:right;">${esc(ev.worldName || ev.worldId)}</span></div>`
                : '',
            _tlMr(esc(t('timeline.detail.instance_type', 'Instance Type')), `<span class="vrcn-badge ${instCls}">${instLabel}</span>`),
            instanceId ? _tlMr(esc(t('timeline.detail.instance_id', 'Instance ID')), `<span style="font-family:monospace;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx2);">#${esc(instanceId)}</span>`) : '',
        ].filter(Boolean).join('');
    }

    const infoRows = [
        _tlMr(esc(t('timeline.detail.date', 'Date')), esc(dateStr)),
        _tlMr(esc(t('timeline.detail.time', 'Time')), esc(timeStr)),
        _tlMr(esc(t('timeline.detail.type', 'Type')), esc(typeLabel)),
        instRows,
        ev.notifTitle ? _tlMr(esc(t('timeline.detail.context', 'Context')), esc(ev.notifTitle)) : '',
        ev.message    ? _tlMr(esc(t('timeline.detail.message', 'Message')), esc(ev.message))    : '',
    ].filter(Boolean).join('');

    const actions = [
        hasInst ? `<button class="vrcn-button-round vrcn-btn-join" onclick="sendToCS({action:'vrcJoinFriend',location:'${jsq(loc)}'})"><span class="msi" style="font-size:16px;">play_arrow</span> ${esc(t('timeline.actions.join_instance', 'Join Instance'))}</button>` : '',
        hasInst ? `<button class="vrcn-button-round" title="${esc(t('timeline.actions.copy_instance_link', 'Copy Instance Link'))}" onclick="copyInstanceLink('${jsq(loc)}')"><span class="msi" style="font-size:16px;">content_copy</span></button>` : '',
        ev.senderId ? `<button class="vrcn-button-round" onclick="navOpenModal('friend','${jsq(ev.senderId)}','${jsq(ev.senderName || '')}')">${esc(t('timeline.actions.view_profile', 'View Profile'))}</button>` : '',
    ].filter(Boolean).join('');

    el.innerHTML = `${_tlBar(ev.senderName || typeLabel)}<div class="fd-content" style="padding:20px 0;">
        ${_tlAvRow(ev.senderImage, ev.senderName || typeLabel, typeLabel.toUpperCase(), 'var(--warn)')}
        ${_tlInfoCard(esc(t('timeline.detail.info', 'Info')), infoRows)}
        ${actions ? `<div style="margin-top:14px;display:flex;gap:8px;flex-wrap:wrap;">${actions}</div>` : ''}
    </div>`;
}

// Detail: avatar switch

function renderTlDetailAvatar(ev, el) {
    const dateStr = tlFormatLongDate(ev.timestamp);
    const timeStr = tlFormatTime(ev.timestamp);
    const banner  = _tlBanner(!!ev.userImage);
    const infoRows = [
        _tlMr(esc(t('timeline.detail.date', 'Date')), esc(dateStr)),
        _tlMr(esc(t('timeline.detail.time', 'Time')), esc(timeStr)),
        ev.userId ? _tlMr(esc(t('timeline.detail.avatar_id', 'Avatar ID')), `<span style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx2);">${esc(ev.userId)}</span>`) : '',
    ].filter(Boolean).join('');
    el.innerHTML = `${_tlBar(ev.userName || t('timeline.unknown_avatar', 'Unknown Avatar'))}${banner}<div class="fd-content${banner ? ' fd-has-banner' : ''}" style="padding:20px 0;">
        <h2 style="margin:0 0 12px;color:var(--tx0);font-size:calc(18px + var(--fs-off, 0px));">${esc(ev.userName || t('timeline.unknown_avatar', 'Unknown Avatar'))}</h2>
        ${_tlInfoCard(esc(t('timeline.detail.info', 'Info')), infoRows)}
        ${ev.userId ? `<div style="margin-top:14px;display:flex;gap:8px;">
            <button class="vrcn-button-round vrcn-btn-join" onclick="navOpenModal('avatar','${jsq(ev.userId)}','${jsq(ev.userName || '')}')">${esc(t('timeline.actions.view_avatar', 'View Avatar'))}</button>
        </div>` : ''}
    </div>`;
    _tlInsertBanner(el, ev.userId || ev.id, ev.userImage);
}

// Detail: video URL

function renderTlDetailUrl(ev, el) {
    const dateStr = tlFormatLongDate(ev.timestamp);
    const timeStr = tlFormatTime(ev.timestamp);
    const url     = ev.message || '';
    const plat    = _urlPlatform(url);
    const favicon = `<div style="display:flex;align-items:center;justify-content:center;width:64px;height:64px;border-radius:12px;background:var(--bg2);">${_urlFaviconHtml(plat)}</div>`;
    const worldRowClick = ev.worldId ? ` onclick="navOpenModal('worldSearch','${jsq(ev.worldId)}','${jsq(ev.worldName || '')}')" style="cursor:pointer;"` : '';
    const infoRows = [
        _tlMr(esc(t('timeline.detail.date', 'Date')), esc(dateStr)),
        _tlMr(esc(t('timeline.detail.time', 'Time')), esc(timeStr)),
        ev.worldName ? `<div style="display:flex;justify-content:space-between;gap:8px;align-items:baseline;font-size:calc(11px + var(--fs-off, 0px));"${worldRowClick}><span style="color:var(--tx2);">${esc(t('timeline.detail.world', 'World'))}</span><span style="color:var(--accent-lt);text-align:right;">${esc(ev.worldName)}</span></div>` : '',
        _tlMr(esc(t('timeline.detail.url', 'URL')), `<span style="word-break:break-all;font-size:calc(11px + var(--fs-off, 0px));color:var(--tx2);">${esc(url)}</span>`),
    ].filter(Boolean).join('');
    el.innerHTML = `${_tlBar(plat.name)}<div class="fd-content" style="padding:20px 0;">
        <div style="display:flex;gap:16px;align-items:center;margin-bottom:16px;">
            ${favicon}
            <div>
                <h2 style="margin:0 0 4px;color:var(--tx0);font-size:calc(18px + var(--fs-off, 0px));">${esc(plat.name)}</h2>
                <div style="font-size:calc(11px + var(--fs-off, 0px));color:${plat.color};font-weight:700;letter-spacing:.05em;">${esc(t('timeline.detail.video_url', 'VIDEO URL'))}</div>
            </div>
        </div>
        ${_tlInfoCard(esc(t('timeline.detail.info', 'Info')), infoRows)}
        <div style="margin-top:14px;display:flex;gap:8px;">
            <button class="vrcn-button-round vrcn-btn-join" onclick="sendToCS({action:'openUrl',url:'${jsq(url)}'})">${esc(t('timeline.actions.open_url', 'Open URL'))}</button>
            <button class="vrcn-button-round" onclick="navigator.clipboard.writeText('${jsq(url)}').then(()=>showToast(true,t('timeline.toast.copied','Copied!')))">${esc(t('timeline.actions.copy', 'Copy'))}</button>
        </div>
    </div>`;
}

// Detail: moderation

function renderTlDetailModeration(ev, el) {
    const dateStr = tlFormatLongDate(ev.timestamp);
    const timeStr = tlFormatTime(ev.timestamp);
    const active  = (ev.message || 'on') !== 'off';
    const label   = (typeof tlModTypeLabel === 'function') ? tlModTypeLabel(ev.notifType, active) : (ev.notifType || '');
    const infoRows = [
        _tlMr(esc(t('timeline.detail.date', 'Date')), esc(dateStr)),
        _tlMr(esc(t('timeline.detail.time', 'Time')), esc(timeStr)),
        _tlMr(esc(t('timeline.detail.moderation_type', 'Moderation Type')), `<span style="color:${active ? 'var(--err)' : 'var(--ok)'};font-weight:600;">${esc(label)}</span>`),
    ].filter(Boolean).join('');
    el.innerHTML = `${_tlBar(ev.userName || label)}<div class="fd-content" style="padding:20px 0;">
        ${_tlAvRow(ev.userImage, ev.userName, t('timeline.detail.moderation', 'MODERATION'), 'var(--err)')}
        ${_tlInfoCard(esc(t('timeline.detail.info', 'Info')), infoRows)}
        ${ev.userId ? `<div style="margin-top:14px;display:flex;gap:8px;">
            <button class="vrcn-button-round vrcn-btn-join" onclick="navOpenModal('friend','${jsq(ev.userId)}','${jsq(ev.userName || '')}')">${esc(t('timeline.actions.view_profile', 'View Profile'))}</button>
        </div>` : ''}
    </div>`;
}

// Detail: profile (own status / status text / bio / game start-close)

function renderTlDetailProfile(ev, el) {
    const dateStr = tlFormatLongDate(ev.timestamp);
    const timeStr = tlFormatTime(ev.timestamp);
    const meta    = (typeof tlProfileMeta === 'function') ? tlProfileMeta(ev) : { icon: 'account_circle', label: t('timeline.types.profile', 'Profile'), color: '#5C6BC0' };

    const infoRows = [
        _tlMr(esc(t('timeline.detail.date', 'Date')), esc(dateStr)),
        _tlMr(esc(t('timeline.detail.time', 'Time')), esc(timeStr)),
    ];
    if (ev.notifType === 'status') {
        const oldCls = statusCssClass(ev.notifTitle);
        const newCls = statusCssClass(ev.message);
        infoRows.push(_tlMr(esc(t('timeline.detail.change', 'Change')), `<span style="display:flex;align-items:center;gap:6px;justify-content:flex-end;"><span class="ft-status-chip ${oldCls}" title="${esc(statusLabel(ev.notifTitle) || '?')}">${esc(statusLabel(ev.notifTitle) || '?')}</span><span class="msi" style="font-size:12px;color:var(--tx2);">arrow_forward</span><span class="ft-status-chip ${newCls}" title="${esc(statusLabel(ev.message) || '?')}">${esc(statusLabel(ev.message) || '?')}</span></span>`));
    } else if (ev.notifType === 'launch') {
        infoRows.push(_tlMr(esc(t('timeline.detail.event', 'Event')), `<span style="color:${meta.color};font-weight:600;">${esc(meta.label)}</span>`));
    }
    const infoHtml = infoRows.filter(Boolean).join('');

    let extraCards = '';
    if (ev.notifType === 'statusdesc') {
        extraCards = `${ev.notifTitle ? `<div class="fd-info-card" style="margin-top:10px;"><div class="fd-group-rep-label">${esc(t('timeline.detail.previous_status_text', 'PREVIOUS STATUS TEXT'))}</div><div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx0);white-space:pre-wrap;">${esc(ev.notifTitle)}</div></div>` : ''}
        <div class="fd-info-card" style="margin-top:10px;"><div class="fd-group-rep-label">${esc(t('timeline.detail.new_status_text', 'NEW STATUS TEXT'))}</div><div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx2);white-space:pre-wrap;">${ev.message ? esc(ev.message) : tlDetailClearedHtml()}</div></div>`;
    } else if (ev.notifType === 'bio') {
        extraCards = `${ev.notifTitle ? `<div class="fd-info-card" style="margin-top:10px;"><div class="fd-group-rep-label">${esc(t('timeline.detail.previous_bio', 'PREVIOUS BIO'))}</div><div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx0);white-space:pre-wrap;">${esc(ev.notifTitle)}</div></div>` : ''}
        ${ev.message ? `<div class="fd-info-card" style="margin-top:10px;"><div class="fd-group-rep-label">${esc(t('timeline.detail.new_bio', 'NEW BIO'))}</div><div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx1);white-space:pre-wrap;">${esc(ev.message)}</div></div>` : ''}`;
    }

    el.innerHTML = `${_tlBar(meta.label)}<div class="fd-content" style="padding:20px 0;">
        <div style="display:flex;gap:16px;align-items:center;margin-bottom:16px;">
            ${ev.userImage
                ? `<div class="tl-detail-av" style="background-image:url('${cssUrl(imgThumb(ev.userImage, 128))}')"></div>`
                : `<div style="display:flex;align-items:center;justify-content:center;width:64px;height:64px;border-radius:12px;background:var(--bg2);"><span class="msi" style="font-size:30px;color:${meta.color};">${meta.icon}</span></div>`}
            <div>
                <h2 style="margin:0 0 4px;color:var(--tx2);font-size:calc(18px + var(--fs-off, 0px));">${esc(meta.label)}</h2>
                <div style="font-size:calc(11px + var(--fs-off, 0px));color:${meta.color};font-weight:700;letter-spacing:.05em;">${esc(t('timeline.detail.profile', 'PROFILE'))}</div>
            </div>
        </div>
        ${_tlInfoCard(esc(t('timeline.detail.info', 'Info')), infoHtml)}
        ${extraCards}
    </div>`;
}


/* === Friend GPS Instance Log Modal === */
function openFtGpsDetail(evId) {
    const ev = friendTimelineEvents.find(e => e.id === evId)
             || _ftlSearchEvents.find(e => e.id === evId)
             || (typeof _fdUserActivityEvents !== 'undefined' ? _fdUserActivityEvents.find(e => e.id === evId) : null);
    if (!ev) return;
    if (typeof navSetCurrent === 'function') navSetCurrent('ftGps', evId);
    renderFtGpsDetailModal(ev);
    _ftGpsShowModal();
}

function _ftGpsShowModal() {
    const box = document.getElementById('ftGpsDetailContent');
    if (box) box.classList.remove('narrow');
    const ov = document.getElementById('modalFtGpsDetail');
    if (!ov) return;
    ov.classList.add('tl-style-compact');
    ov.style.display = 'flex';
}

function closeFtGpsDetail(fromNav = false) {
    document.getElementById('modalFtGpsDetail').style.display = 'none';
    if (!fromNav && typeof navClear === 'function') navClear();
}

function renderFtGpsDetailModal(ev) {
    const loc = ev.location || '';
    const { instanceType } = parseFriendLocation(loc);
    const { cls: instCls, label: instLabel } = getInstanceBadge(instanceType);
    const regionCode = parseInstanceRegion(loc);

    const instIdMatch = loc.match(/:(\d+)/);
    const instanceId = instIdMatch ? instIdMatch[1] : '';

    const { dateStr, timeStr } = ftDetailDatetime(ev);

    const worldName = ev.worldName || ev.worldId || t('timeline.unknown_world', 'Unknown World');

    const fromStr = ev.timestamp ? tlFormatTime(ev.timestamp) : null;
    const toStr   = ev.leftAt    ? tlFormatTime(ev.leftAt)    : null;

    const infoHtml = _tlInfoCard(esc(t('timeline.detail.info', 'Info')), [
        _tlMr(esc(t('timeline.detail.date', 'Date')), esc(dateStr)),
        _tlMr(esc(t('timeline.detail.time', 'Time')), esc(timeStr)),
        fromStr ? _tlMr(esc(t('timeline.detail.from', 'From')), esc(fromStr)) : '',
        (fromStr && ev.tracked) ? _tlMr(esc(t('timeline.detail.to', 'To')), toStr ? esc(toStr) : `<span style="color:var(--ok);">&#9679;&nbsp;${esc(t('timeline.detail.ongoing', 'Ongoing'))}</span>`) : '',
        (ev.tracked && ev.leftAt && ev.timestamp) ? _tlMr(esc(t('nav.time_spent', 'Time Spent')), esc(formatDuration(Math.floor((new Date(ev.leftAt) - new Date(ev.timestamp)) / 1000)))) : '',
        _tlMr(esc(t('timeline.detail.instance_type', 'Instance Type')), `<span class="vrcn-badge ${instCls}">${instLabel}</span>`),
        regionCode ? _tlMr(esc(t('timeline.detail.server', 'Server')), `<span class="vrcn-badge"><span class="msi" style="font-size:10px;">language</span>${esc(getRegionShortLabel(regionCode))}</span>`) : '',
        instanceId ? _tlMr(esc(t('timeline.detail.instance_id', 'Instance ID')), `<span style="font-family:monospace;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx2);">#${esc(instanceId)}</span>`) : '',
        _tlCreatorRow(loc),
        _tlMr(esc(t('timeline.detail.event', 'Event')), `<span style="color:var(--tx0);">${esc(tf('timeline.detail.friend_joined_world', { name: ev.friendName || t('timeline.unknown', 'Unknown') }, `${ev.friendName || 'Unknown'} joined this world`))}</span>`),
    ].filter(Boolean).join(''));

    const canCopyLink = !!(loc && loc.indexOf(':') > 0 && loc.startsWith('wrld_'));

    const leftHtml = `<div class="fd-left">
        <div class="fd-left-banner" id="tl-banner-slot">${ev.worldThumb ? '<div class="fd-banner-fade"></div>' : ''}</div>
        <div class="fd-left-body">
            ${ftDetailAvRow(ev)}
            <div>
                <h2 style="margin:0 0 4px;color:var(--tx0);font-size:calc(18px + var(--fs-off, 0px));">${esc(worldName)}</h2>
                <div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx2);margin-bottom:8px;">${esc(dateStr)}</div>
                ${idBadge(ev.worldId || '')}
            </div>
            <div class="fd-actions">
                ${loc ? `<button class="vrcn-button-round vrcn-btn-join" title="${esc(t('instance.actions.force_join', 'Force-Join'))}" onclick="sendToCS({action:'vrcJoinFriend',location:'${jsq(loc)}'});"><span class="msi" style="font-size:16px;">play_arrow</span></button>` : ''}
                ${canCopyLink ? `<button class="vrcn-button-round" title="${esc(t('timeline.actions.copy_instance_link', 'Copy Instance Link'))}" onclick="copyInstanceLink('${jsq(loc)}')"><span class="msi" style="font-size:16px;">content_copy</span></button>` : ''}
                ${ev.worldId ? `<button class="vrcn-button-round" title="${esc(t('timeline.actions.open_world', 'Open World'))}" onclick="navOpenModal('worldSearch','${jsq(ev.worldId)}','${jsq(ev.worldName || '')}')"><span class="msi" style="font-size:16px;">travel_explore</span></button>` : ''}
            </div>
            ${infoHtml}
        </div>
    </div>`;

    const rightHtml = `<div class="fd-right">
        <div class="tl-players-head">
            <div class="fd-group-rep-label" id="ftGpsAlsoTab" style="margin-bottom:0;">${esc(t('timeline.detail.was_also_here', 'Was also here'))}</div>
        </div>
        <div class="fd-right-scroll" id="ftGpsTabAlso">
            <div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);padding:12px 0;">${esc(t('common.loading', 'Loading...'))}</div>
        </div>
    </div>`;

    if (typeof navUpdateLabel === 'function') navUpdateLabel(worldName);

    const el = document.getElementById('ftGpsDetailContent');
    el.innerHTML = `${renderModalBar(worldName, [modalCloseAction('closeFtGpsDetail()')])}<div class="fd-layout">${leftHtml}${rightHtml}</div>`;

    // Async: ask server for all friends at this location (searches full DB, not just loaded page)
    _tlInsertBanner(el, ev.worldId || ev.id, ev.worldThumb);
    sendToCS({ action: 'getFtAlsoWasHere', location: loc, excludeId: ev.id });
}

function renderFtAlsoWasHereResult(payload) {
    const tab = document.getElementById('ftGpsTabAlso');
    const tabBtn = document.getElementById('ftGpsAlsoTab');
    if (!tab) return;
    const friends = payload?.friends ?? [];
    if (friends.length === 0) {
        tab.innerHTML = `<div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);padding:12px 0;">${esc(t('timeline.detail.no_other_friends', 'No other friends tracked in this instance.'))}</div>`;
    } else {
        tab.innerHTML = friends.map(f =>
            renderProfileItemSmall(
                { id: f.friendId, displayName: f.friendName || t('timeline.unknown', 'Unknown'), image: f.friendImage },
                `navOpenModal('friend','${jsq(f.friendId)}','${jsq(f.friendName || '')}')`
            )
        ).join('');
    }
    if (tabBtn && friends.length > 0) tabBtn.textContent = tf('timeline.detail.was_also_here_count', { count: friends.length }, `Was also here (${friends.length})`);
}

function openFtDetail(id) {
    const ev = friendTimelineEvents.find(e => e.id === id)
             || _ftlSearchEvents.find(e => e.id === id)
             || (typeof _fdUserActivityEvents !== 'undefined' ? _fdUserActivityEvents.find(e => e.id === id) : null);
    if (!ev) return;
    _tlMid = 'modalDetail';
    _tlStacked = false;
    const el = document.getElementById('detailModalContent');
    if (!el) return;

    if (typeof navSetCurrent === 'function') navSetCurrent('ftEvent', id);

    switch (ev.type) {
        case 'friend_gps':        renderFtDetailGps(ev, el);        break;
        case 'friend_status':     renderFtDetailStatus(ev, el);     break;
        case 'friend_statusdesc': renderFtDetailStatusDesc(ev, el); break;
        case 'friend_online':      renderFtDetailOnline(ev, el);     break;
        case 'friend_offline':     renderFtDetailOffline(ev, el);    break;
        case 'friend_bio':        renderFtDetailBio(ev, el);        break;
        case 'friend_added':      renderFtDetailAdded(ev, el);      break;
        case 'friend_removed':    renderFtDetailRemoved(ev, el);    break;
        case 'friend_avatar':     renderFtDetailFriendAvatar(ev, el); break;
    }

    const _mb2 = document.querySelector('#modalDetail .modal-box');
    if (_mb2) _mb2.classList.add('narrow');
    document.getElementById('modalDetail').classList.remove('wd-style-compact', 'gd-style-compact', 'tl-style-compact');
    document.getElementById('modalDetail').style.display = 'flex';
}

function openFdActivityDetail(id) {
    const ev = _fdUserActivityEvents.find(e => e.id === id);
    if (!ev) return;
    if (ev.type === 'friend_gps') {
        if (typeof navSetCurrent === 'function') navSetCurrent('ftGps', id);
        renderFtGpsDetailModal(ev);
        _ftGpsShowModal();
        return;
    }
    _tlMid = 'modalDetail';
    const el = document.getElementById('detailModalContent');
    if (!el) return;

    if (typeof navSetCurrent === 'function') navSetCurrent('ftEvent', id);
    switch (ev.type) {
        case 'friend_status':     renderFtDetailStatus(ev, el);     break;
        case 'friend_statusdesc': renderFtDetailStatusDesc(ev, el); break;
        case 'friend_online':      renderFtDetailOnline(ev, el);     break;
        case 'friend_offline':     renderFtDetailOffline(ev, el);    break;
        case 'friend_bio':        renderFtDetailBio(ev, el);        break;
        case 'friend_added':      renderFtDetailAdded(ev, el);      break;
        case 'friend_removed':    renderFtDetailRemoved(ev, el);    break;
    }
    const _mb3 = document.querySelector('#modalDetail .modal-box');
    if (_mb3) _mb3.classList.add('narrow');
    document.getElementById('modalDetail').classList.remove('wd-style-compact', 'gd-style-compact', 'tl-style-compact');
    document.getElementById('modalDetail').style.display = 'flex';
}

function ftDetailDatetime(ev) {
    return {
        dateStr: tlFormatLongDate(ev.timestamp),
        timeStr: tlFormatTime(ev.timestamp),
    };
}

function ftDetailAvRow(ev) {
    const av = ev.friendImage
        ? `<div class="tl-detail-av" style="background-image:url('${cssUrl(imgThumb(ev.friendImage, 128))}')"></div>`
        : `<div class="tl-detail-av tl-detail-av-letter">${esc((ev.friendName || '?')[0].toUpperCase())}</div>`;
    return `<div style="display:flex;gap:16px;align-items:center;margin-bottom:20px;">${av}
        <div><h2 style="margin:0 0 4px;color:var(--tx0);font-size:calc(18px + var(--fs-off, 0px));">${esc(ev.friendName || t('timeline.unknown', 'Unknown'))}</h2>
        ${ev.friendId ? `<div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx2);">${esc(ev.friendId)}</div>` : ''}
        </div></div>`;
}

function ftDetailBar(ev) {
    return _tlBar(ev.friendName || t('timeline.unknown', 'Unknown'));
}

function ftDetailViewProfile(ev) {
    return ev.friendId
        ? `<button class="vrcn-button-round vrcn-btn-join" onclick="navOpenModal('friend','${jsq(ev.friendId)}','${jsq(ev.friendName || '')}')">${esc(t('timeline.actions.view_profile', 'View Profile'))}</button>`
        : '';
}

function renderFtDetailGps(ev, el) {
    const { dateStr, timeStr } = ftDetailDatetime(ev);
    const banner = _tlBanner(!!ev.worldThumb);
    const wname  = ev.worldName || ev.worldId || t('timeline.unknown_world', 'Unknown World');
    const worldRowClick = ev.worldId ? ` onclick="navOpenModal('worldSearch','${jsq(ev.worldId)}','${jsq(ev.worldName || '')}')" style="cursor:pointer;"` : '';
    const infoRows = [
        _tlMr(esc(t('timeline.detail.date', 'Date')), esc(dateStr)),
        _tlMr(esc(t('timeline.detail.time', 'Time')), esc(timeStr)),
        `<div style="display:flex;justify-content:space-between;gap:8px;align-items:baseline;font-size:calc(11px + var(--fs-off, 0px));"${worldRowClick}><span style="color:var(--tx2);">${esc(t('timeline.detail.world', 'World'))}</span><span style="color:var(--accent-lt);text-align:right;">${esc(wname)}</span></div>`,
    ].join('');
    el.innerHTML = `${ftDetailBar(ev)}${banner}<div class="fd-content${banner ? ' fd-has-banner' : ''}" style="padding:20px 0;">
        ${ftDetailAvRow(ev)}
        ${_tlInfoCard(esc(t('timeline.detail.info', 'Info')), infoRows)}
        <div style="margin-top:14px;display:flex;gap:8px;">
            ${ev.worldId ? `<button class="vrcn-button-round vrcn-btn-join" onclick="navOpenModal('worldSearch','${jsq(ev.worldId)}','${jsq(ev.worldName || '')}')"><span class="msi" style="font-size:14px;">travel_explore</span> ${esc(t('timeline.actions.open_world', 'Open World'))}</button>` : ''}
            ${ftDetailViewProfile(ev)}
        </div>
    </div>`;
    _tlInsertBanner(el, ev.worldId || ev.id, ev.worldThumb);
}

function renderFtDetailStatus(ev, el) {
    const { dateStr, timeStr } = ftDetailDatetime(ev);
    const oldCls = statusCssClass(ev.oldValue);
    const newCls = statusCssClass(ev.newValue);
    const infoRows = [
        _tlMr(esc(t('timeline.detail.date', 'Date')), esc(dateStr)),
        _tlMr(esc(t('timeline.detail.time', 'Time')), esc(timeStr)),
        _tlMr(esc(t('timeline.detail.change', 'Change')), `<span style="display:flex;align-items:center;gap:6px;"><span class="ft-status-chip ${oldCls}" title="${esc(statusLabel(ev.oldValue) || '?')}">${esc(statusLabel(ev.oldValue) || '?')}</span><span class="msi" style="font-size:12px;color:var(--tx2);">arrow_forward</span><span class="ft-status-chip ${newCls}" title="${esc(statusLabel(ev.newValue) || '?')}">${esc(statusLabel(ev.newValue) || '?')}</span></span>`),
    ].join('');
    el.innerHTML = `${ftDetailBar(ev)}<div class="fd-content" style="padding:20px 0;">
        ${ftDetailAvRow(ev)}
        ${_tlInfoCard(esc(t('timeline.detail.info', 'Info')), infoRows)}
        <div style="margin-top:14px;display:flex;gap:8px;">${ftDetailViewProfile(ev)}</div>
    </div>`;
}

function renderFtDetailOnline(ev, el) {
    const { dateStr, timeStr } = ftDetailDatetime(ev);
    const infoRows = [
        _tlMr(esc(t('timeline.detail.date', 'Date')), esc(dateStr)),
        _tlMr(esc(t('timeline.detail.time', 'Time')), esc(timeStr)),
        _tlMr(esc(t('timeline.detail.event', 'Event')), `<span style="color:var(--ok);">${esc(t('timeline.friend.online_game', 'Online (Game)'))}</span>`),
    ].join('');
    el.innerHTML = `${ftDetailBar(ev)}<div class="fd-content" style="padding:20px 0;">
        ${ftDetailAvRow(ev)}
        ${_tlInfoCard(esc(t('timeline.detail.info', 'Info')), infoRows)}
        <div style="margin-top:14px;display:flex;gap:8px;">${ftDetailViewProfile(ev)}</div>
    </div>`;
}

function renderFtDetailOffline(ev, el) {
    const { dateStr, timeStr } = ftDetailDatetime(ev);
    const infoRows = [
        _tlMr(esc(t('timeline.detail.date', 'Date')), esc(dateStr)),
        _tlMr(esc(t('timeline.detail.time', 'Time')), esc(timeStr)),
        _tlMr(esc(t('timeline.detail.event', 'Event')), `<span style="color:var(--tx3);">${esc(t('timeline.friend.went_offline', 'Went Offline'))}</span>`),
    ].join('');
    el.innerHTML = `${ftDetailBar(ev)}<div class="fd-content" style="padding:20px 0;">
        ${ftDetailAvRow(ev)}
        ${_tlInfoCard(esc(t('timeline.detail.info', 'Info')), infoRows)}
        <div style="margin-top:14px;display:flex;gap:8px;">${ftDetailViewProfile(ev)}</div>
    </div>`;
}

function renderFtDetailAdded(ev, el) {
    const { dateStr, timeStr } = ftDetailDatetime(ev);
    const infoRows = [
        _tlMr(esc(t('timeline.detail.date', 'Date')), esc(dateStr)),
        _tlMr(esc(t('timeline.detail.time', 'Time')), esc(timeStr)),
        _tlMr(esc(t('timeline.detail.event', 'Event')), `<span style="color:var(--ok);">${esc(t('timeline.friend.added_full', 'Friend Added'))}</span>`),
    ].join('');
    el.innerHTML = `${ftDetailBar(ev)}<div class="fd-content" style="padding:20px 0;">
        ${ftDetailAvRow(ev)}
        ${_tlInfoCard(esc(t('timeline.detail.info', 'Info')), infoRows)}
        <div style="margin-top:14px;display:flex;gap:8px;">${ftDetailViewProfile(ev)}</div>
    </div>`;
}

function renderFtDetailRemoved(ev, el) {
    const { dateStr, timeStr } = ftDetailDatetime(ev);
    const infoRows = [
        _tlMr(esc(t('timeline.detail.date', 'Date')), esc(dateStr)),
        _tlMr(esc(t('timeline.detail.time', 'Time')), esc(timeStr)),
        _tlMr(esc(t('timeline.detail.event', 'Event')), `<span style="color:var(--err);">${esc(t('timeline.friend.unfriended', 'Unfriended'))}</span>`),
        ev.friendId ? _tlMr(esc(t('timeline.detail.user_id', 'User ID')), `<span style="font-size:calc(11px + var(--fs-off, 0px));opacity:.7;">${esc(ev.friendId)}</span>`) : '',
    ].filter(Boolean).join('');
    el.innerHTML = `${ftDetailBar(ev)}<div class="fd-content" style="padding:20px 0;">
        ${ftDetailAvRow(ev)}
        ${_tlInfoCard(esc(t('timeline.detail.info', 'Info')), infoRows)}
        <div style="margin-top:14px;display:flex;gap:8px;">${ftDetailViewProfile(ev)}</div>
    </div>`;
}

function renderFtDetailStatusDesc(ev, el) {
    const { dateStr, timeStr } = ftDetailDatetime(ev);
    const infoRows = [
        _tlMr(esc(t('timeline.detail.date', 'Date')), esc(dateStr)),
        _tlMr(esc(t('timeline.detail.time', 'Time')), esc(timeStr)),
    ].join('');
    el.innerHTML = `${ftDetailBar(ev)}<div class="fd-content" style="padding:20px 0;">
        ${ftDetailAvRow(ev)}
        ${_tlInfoCard(esc(t('timeline.detail.info', 'Info')), infoRows)}
        ${ev.oldValue ? `<div class="fd-info-card" style="margin-top:10px;"><div class="fd-group-rep-label">${esc(t('timeline.detail.previous_status_text', 'PREVIOUS STATUS TEXT'))}</div><div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx1);white-space:pre-wrap;">${esc(ev.oldValue)}</div></div>` : ''}
        ${ev.newValue !== undefined ? `<div class="fd-info-card" style="margin-top:10px;"><div class="fd-group-rep-label">${esc(t('timeline.detail.new_status_text', 'NEW STATUS TEXT'))}</div><div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx2);white-space:pre-wrap;">${ev.newValue ? esc(ev.newValue) : tlDetailClearedHtml()}</div></div>` : ''}
        <div style="margin-top:14px;display:flex;gap:8px;">${ftDetailViewProfile(ev)}</div>
    </div>`;
}

function renderFtDetailBio(ev, el) {
    const { dateStr, timeStr } = ftDetailDatetime(ev);
    const infoRows = [
        _tlMr(esc(t('timeline.detail.date', 'Date')), esc(dateStr)),
        _tlMr(esc(t('timeline.detail.time', 'Time')), esc(timeStr)),
    ].join('');
    el.innerHTML = `${ftDetailBar(ev)}<div class="fd-content" style="padding:20px 0;">
        ${ftDetailAvRow(ev)}
        ${_tlInfoCard(esc(t('timeline.detail.info', 'Info')), infoRows)}
        ${ev.oldValue ? `<div class="fd-info-card" style="margin-top:10px;"><div class="fd-group-rep-label">${esc(t('timeline.detail.previous_bio', 'PREVIOUS BIO'))}</div><div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx1);white-space:pre-wrap;">${esc(ev.oldValue)}</div></div>` : ''}
        ${ev.newValue ? `<div class="fd-info-card" style="margin-top:10px;"><div class="fd-group-rep-label">${esc(t('timeline.detail.new_bio', 'NEW BIO'))}</div><div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx1);white-space:pre-wrap;">${esc(ev.newValue)}</div></div>` : ''}
        <div style="margin-top:14px;display:flex;gap:8px;">${ftDetailViewProfile(ev)}</div>
    </div>`;
}

function renderFtDetailFriendAvatar(ev, el) {
    const { dateStr, timeStr } = ftDetailDatetime(ev);
    const banner = _tlBanner(!!ev.worldThumb);
    const infoRows = [
        _tlMr(esc(t('timeline.detail.date', 'Date')), esc(dateStr)),
        _tlMr(esc(t('timeline.detail.time', 'Time')), esc(timeStr)),
        ev.worldName ? _tlMr(esc(t('timeline.detail.avatar', 'Avatar')), `<span style="color:var(--accent-lt);">${esc(ev.worldName)}</span>${ev.worldId ? '' : `<span class="msi" title="${esc(t('profiles.badges.avatar_not_in_db', 'Avatar not found in any database'))}" style="font-size:13px;color:var(--tx2);margin-left:5px;vertical-align:-2px;">lock</span>`}`) : '',
        ev.worldId   ? _tlMr(esc(t('timeline.detail.avatar_id', 'Avatar ID')), `<span style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx2);">${esc(ev.worldId)}</span>`) : '',
    ].filter(Boolean).join('');
    el.innerHTML = `${ftDetailBar(ev)}${banner}<div class="fd-content${banner ? ' fd-has-banner' : ''}" style="padding:20px 0;">
        ${ftDetailAvRow(ev)}
        ${_tlInfoCard(esc(t('timeline.detail.info', 'Info')), infoRows)}
        <div style="margin-top:14px;display:flex;gap:8px;">
            ${ev.worldId ? `<button class="vrcn-button-round vrcn-btn-join" onclick="navOpenModal('avatar','${jsq(ev.worldId)}','${jsq(ev.worldName || '')}')">${esc(t('timeline.actions.view_avatar', 'View Avatar'))}</button>` : ''}
            ${ftDetailViewProfile(ev)}
        </div>
    </div>`;
    _tlInsertBanner(el, ev.worldId || ev.id, ev.worldThumb);
}

// ═══════════════════════════════════════════════════════════════════
// List View — Personal Timeline
// ═══════════════════════════════════════════════════════════════════

function _tlListProfHtml(img, name) {
    if (img) return `<div class="tl-av" style="width:26px;height:26px;background-image:url('${cssUrl(imgThumb(img, 64))}')"></div>`;
    if (name) return `<div class="tl-av tl-av-letter" style="width:26px;height:26px;display:flex;align-items:center;justify-content:center;font-size:calc(10px + var(--fs-off, 0px));">${esc(name[0].toUpperCase())}</div>`;
    return '';
}

function _tlPersonalProfHtml(ev) {
    if (ev.type === 'notification') return _tlListProfHtml(ev.senderImage, ev.senderName);
    if (ev.type === 'instance_join') {
        const players = ev.players || [];
        if (players.length > 0) return _tlListPlayerAvatars(players, 3);
        return _tlListProfHtml(currentVrcUser?.image, currentVrcUser?.displayName);
    }
    if (ev.type === 'photo' || ev.type === 'profile') return _tlListProfHtml(ev.userImage || currentVrcUser?.image, ev.userName || currentVrcUser?.displayName);
    return _tlListProfHtml(ev.userImage, ev.userName);
}

function buildPersonalListHtml(events, staticHeader) {
    if (!events.length) {
        return `<div class="empty-msg">${esc(t('timeline.list.empty.personal', 'No timeline events match your filter.'))}</div>`;
    }

    let rows = '';
    events.forEach(ev => {
        const meta  = tlTypeMeta(ev.type);
        const color = getTlEventColor(ev);
        const ei    = jsq(ev.id);
        const { userHtml, detail } = _tlListData(ev);
        const listMeetCount = ev.type === 'meet_again' ? (ev.meetCount || 0) : 0;
        let listTypeLabel = listMeetCount > 0 ? `${meta.label} (${listMeetCount})` : meta.label;
        if (ev.type === 'notification') listTypeLabel = tlNotifTypeLabel(ev.notifType);

        rows += tlTableRow('personal', ` data-tlid="${esc(ev.id)}" onclick="openTlDetail('${ei}')"`, {
            dt:      `<td class="tl-list-dt">${esc(`${tlFormatShortDate(ev.timestamp)} | ${tlFormatTime(ev.timestamp)}`)}</td>`,
            type:    `<td class="tl-list-type"><span class="msi tl-list-icon" style="color:${color}">${(tlNotifStyle(ev) || meta).icon}</span><span>${esc(listTypeLabel)}</span></td>`,
            profile: `<td class="tl-list-profile">${_tlPersonalProfHtml(ev)}</td>`,
            user:    `<td class="tl-list-user">${userHtml || tlListNaHtml()}</td>`,
            detail:  `<td class="tl-list-detail">${detail || tlListNaHtml()}</td>`,
        });
    });

    return tlTableHtml('personal', rows, staticHeader);
}

function _tlListPlayerAvatars(players, max) {
    if (!players || !players.length) return '';
    const shown = players.slice(0, max);
    const rest  = players.length - max;
    let html = '<span class="tl-list-avs">';
    shown.forEach(p => {
        html += p.image
            ? `<span class="tl-list-av" style="background-image:url('${cssUrl(imgThumb(p.image, 64))}')" title="${esc(p.displayName || '')}"></span>`
            : `<span class="tl-list-av tl-list-av-letter" title="${esc(p.displayName || '')}">${esc((p.displayName || '?')[0].toUpperCase())}</span>`;
    });
    if (rest > 0) html += `<span class="tl-list-av tl-list-av-more">+${rest}</span>`;
    html += '</span>';
    return html;
}

function tlMiniListHtml(rowsHtml, moreOnclick) {
    return `<div class="tl-list-wrap tl-list-mini"><table class="tl-list-table">
        <colgroup><col style="width:150px"><col style="width:170px"><col></colgroup>
        <thead><tr>
            <th>${esc(t('timeline.list.header.date_time', 'Date / Time'))}</th>
            <th>${esc(t('timeline.list.header.type', 'Type'))}</th>
            <th><div class="tl-list-th-flex"><span>${esc(t('timeline.list.header.detail', 'Detail'))}</span>${moreOnclick ? `<button type="button" class="tl-list-more" onclick="window._tlMoreEl=this;${moreOnclick}">${esc(t('timeline.list.show_more', 'Show more'))}</button>` : ''}</div></th>
        </tr></thead>
        <tbody>${rowsHtml}</tbody>
    </table></div>`;
}

function _tlListData(ev) {
    switch (ev.type) {
        case 'instance_join':
            return { userHtml: esc(currentVrcUser?.displayName || t('timeline.unknown', 'Unknown')), detail: tlInstanceListDetail(ev, ev.worldName || ev.worldId || t('timeline.unknown_world', 'Unknown World')) };
        case 'photo':
            return { userHtml: _tlListPlayerAvatars(ev.players, 3), detail: esc(ev.photoPath ? ev.photoPath.split(/[\\/]/).pop() : t('timeline.photo', 'Photo')) };
        case 'first_meet':
            return { userHtml: esc(ev.userName || t('timeline.unknown', 'Unknown')), detail: ev.worldName ? esc(ev.worldName) : '' };
        case 'meet_again':
            return { userHtml: esc(ev.userName || t('timeline.unknown', 'Unknown')), detail: ev.worldName ? esc(ev.worldName) : '' };
        case 'notification': {
            const sender = ev.senderName || '';
            const userHtml = sender ? esc(sender) : '';
            if (sender && ev.notifType === 'invite') {
                const s = ev.worldName
                    ? tf('timeline.notif.invited_you_to', { name: sender, world: ev.worldName }, `${sender} invited you to ${ev.worldName}`)
                    : tf('timeline.notif.invited_you', { name: sender }, `${sender} invited you`);
                return { userHtml, detail: esc(s) };
            }
            if (sender && ev.notifType === 'requestInvite') {
                return { userHtml, detail: esc(tf('timeline.notif.wants_invite', { name: sender }, `${sender} wants an invite`)) };
            }
            const text = ev.message || ev.notifTitle || '';
            return { userHtml, detail: text ? `${esc(text.slice(0, 90))}${text.length > 90 ? '…' : ''}` : '' };
        }
        case 'avatar_switch':
            return { userHtml: esc(currentVrcUser?.displayName || t('timeline.unknown', 'Unknown')), detail: esc(ev.userName || '') };
        case 'video_url': {
            const url = ev.message || '';
            const short = url.length > 60 ? url.slice(0, 60) + '...' : url;
            return { userHtml: '', detail: esc(short) };
        }
        case 'moderation': {
            const active = (ev.message || 'on') !== 'off';
            const label  = (typeof tlModTypeLabel === 'function') ? tlModTypeLabel(ev.notifType, active) : (ev.notifType || '');
            return { userHtml: esc(ev.userName || t('timeline.unknown', 'Unknown')), detail: `<span style="color:${active ? 'var(--err)' : 'var(--ok)'}">${esc(label)}</span>` };
        }
        case 'profile': {
            const meta = (typeof tlProfileMeta === 'function') ? tlProfileMeta(ev) : { label: t('timeline.types.profile', 'Profile') };
            const uh = esc(ev.userName || currentVrcUser?.displayName || t('timeline.unknown', 'Unknown'));
            let detail;
            if (ev.notifType === 'status') {
                const oldCls = statusCssClass(ev.notifTitle);
                const newCls = statusCssClass(ev.message);
                detail = `<span class="ft-status-chip ${oldCls}" title="${esc(statusLabel(ev.notifTitle) || '?')}">${esc(statusLabel(ev.notifTitle) || '?')}</span>`
                       + `<span class="msi" style="font-size:12px;color:var(--tx2);vertical-align:middle;margin:0 4px;">arrow_forward</span>`
                       + `<span class="ft-status-chip ${newCls}" title="${esc(statusLabel(ev.message) || '?')}">${esc(statusLabel(ev.message) || '?')}</span>`;
            } else if (ev.notifType === 'statusdesc' || ev.notifType === 'bio') {
                const v = (ev.message || '').slice(0, 80);
                detail = v ? esc(v) + ((ev.message || '').length > 80 ? '...' : '') : tlClearedHtml();
            } else {
                detail = `<span style="color:${meta.color || 'var(--tx2)'}">${esc(meta.label)}</span>`;
            }
            return { userHtml: uh, detail };
        }
        default:
            return { userHtml: '', detail: '' };
    }
}

// ═══════════════════════════════════════════════════════════════════
// List View — Friends Timeline
// ═══════════════════════════════════════════════════════════════════

function buildFriendListHtml(events, staticHeader) {
    if (!events.length) {
        return `<div class="empty-msg">${esc(t('timeline.list.empty.friends', 'No friend activity logged yet.'))}</div>`;
    }

    let rows = '';
    events.forEach(ev => {
        const meta  = ftTypeMeta(ev.type);
        const color = ev.type === 'friend_gps' ? getFtGpsColor(ev) : (FT_TYPE_COLOR[ev.type] ?? 'var(--tx3)');
        const ei    = jsq(ev.id);
        const detail = _ftListDetail(ev);
        const clickAction = ev.type === 'friend_gps'
            ? `openFtGpsDetail('${ei}')`
            : `openFtDetail('${ei}')`;

        rows += tlTableRow('friends', ` data-ftid="${esc(ev.id)}" onclick="${clickAction}"`, {
            dt:      `<td class="tl-list-dt">${esc(`${tlFormatShortDate(ev.timestamp)} | ${tlFormatTime(ev.timestamp)}`)}</td>`,
            type:    `<td class="tl-list-type"><span class="msi tl-list-icon" style="color:${color}">${meta.icon}</span><span>${esc(meta.label)}</span></td>`,
            profile: `<td class="tl-list-profile">${_tlListProfHtml(ev.friendImage, ev.friendName)}</td>`,
            user:    `<td class="tl-list-user">${esc(ev.friendName || t('timeline.unknown', 'Unknown'))}</td>`,
            detail:  `<td class="tl-list-detail">${detail || tlListNaHtml()}</td>`,
        });
    });

    return tlTableHtml('friends', rows, staticHeader);
}

function _ftListDetail(ev) {
    switch (ev.type) {
        case 'friend_online':      return `<span style="color:var(--ok)">${esc(t('timeline.friend.came_online', 'Came Online'))}</span>`;
        case 'friend_offline':     return `<span style="color:var(--tx3)">${esc(t('timeline.friend.went_offline', 'Went Offline'))}</span>`;
        case 'friend_gps':        return tlInstanceListDetail(ev, ev.worldName || ev.worldId || t('timeline.unknown_world', 'Unknown World'));
        case 'friend_status': {
            const oldCls = statusCssClass(ev.oldValue);
            const newCls = statusCssClass(ev.newValue);
            return `<span class="ft-status-chip ${oldCls}" title="${esc(statusLabel(ev.oldValue) || '?')}">${esc(statusLabel(ev.oldValue) || '?')}</span>`
                 + `<span class="msi" style="font-size:12px;color:var(--tx2);vertical-align:middle;margin:0 4px;">arrow_forward</span>`
                 + `<span class="ft-status-chip ${newCls}" title="${esc(statusLabel(ev.newValue) || '?')}">${esc(statusLabel(ev.newValue) || '?')}</span>`;
        }
        case 'friend_statusdesc': {
            const v = (ev.newValue || '').slice(0, 80);
            return v ? esc(v) + ((ev.newValue || '').length > 80 ? '...' : '') : tlClearedHtml();
        }
        case 'friend_bio': {
            const v = (ev.newValue || '').slice(0, 80);
            return v ? esc(v) + ((ev.newValue || '').length > 80 ? '...' : '') : tlClearedHtml();
        }
        case 'friend_added':      return `<span style="color:var(--ok)">${esc(t('timeline.friend.added_full', 'Friend Added'))}</span>`;
        case 'friend_removed':    return `<span style="color:var(--err)">${esc(t('timeline.friend.unfriended', 'Unfriended'))}</span>`;
        case 'friend_avatar':     return esc(ev.worldName || ev.newValue || t('timeline.unknown', 'Unknown'))
            + (ev.worldId ? '' : `<span class="msi" title="${esc(t('profiles.badges.avatar_not_in_db', 'Avatar not found in any database'))}" style="font-size:12px;color:var(--tx2);vertical-align:middle;margin-left:4px;">lock</span>`);
        default: return '';
    }
}

function rerenderTimelineTranslations() {
    const label = document.getElementById('tlDateLabel');
    if (label && tlDateFilter) {
        label.textContent = tlFormatDateFilterLabel(tlDateFilter);
        label.style.display = '';
    }
    if (document.getElementById('tlDatePicker')?.style.display !== 'none') {
        renderDatePickerCalendar();
    }
    if (tlViewMode !== 'timeline') return;
    const search = (document.getElementById('tlSearchInput')?.value ?? '').toLowerCase().trim();
    if (tlMode === 'friends') {
        if (_ftlSearchMode && search) _renderFtlSearchResults(search);
        else if (friendTimelineEvents.length) filterFriendTimeline();
    } else {
        if (_tlSearchMode && search) _renderTlSearchResults(search);
        else if (timelineEvents.length) filterTimeline();
    }
}

document.documentElement.addEventListener('languagechange', rerenderTimelineTranslations);
