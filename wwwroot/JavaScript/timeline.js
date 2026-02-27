// Timeline - Tab 12
// Globals: timelineEvents, tlFilter  (declared in core.js)

// Pending scroll-to target: consumed by filterTimeline() after DOM is built
let _tlScrollTarget = null;

// Filter button map
const TL_FILTER_IDS = {
    all:           'tlFAll',
    instance_join: 'tlFJoin',
    photo:         'tlFPhoto',
    first_meet:    'tlFMeet',
    meet_again:    'tlFMeetAgain',
    notification:  'tlFNotif',
};

// Type colours
const TL_TYPE_COLOR = {
    instance_join: 'var(--accent)',
    photo:         'var(--ok)',
    first_meet:    'var(--cyan)',
    meet_again:    '#AB47BC',
    notification:  'var(--warn)',
};

// Type labels and icons
const TL_TYPE_META = {
    instance_join: { icon: 'travel_explore', label: 'Instance Join' },
    photo:         { icon: 'camera',         label: 'Photo'         },
    first_meet:    { icon: 'person_add',     label: 'First Meet'    },
    meet_again:    { icon: 'person_check',   label: 'Meet Again'    },
    notification:  { icon: 'notifications',  label: 'Notification'  },
};

// Notification type labels
const NOTIF_TYPE_LABELS = {
    friendRequest:  'Friend Request',
    invite:         'Invite',
    requestInvite:  'Invite Request',
    votetokick:     'Vote to Kick',
    message:        'Message',
    halted:         'Instance Closed',
};

// Public API

function setTlMode(mode) {
    tlMode = mode;
    document.getElementById('tlModePersonal')?.classList.toggle('active', mode === 'personal');
    document.getElementById('tlModeFriends')?.classList.toggle('active',  mode === 'friends');
    const pf = document.getElementById('tlPersonalFilters');
    const ff = document.getElementById('tlFriendsFilters');
    if (pf) pf.style.display = mode === 'personal' ? '' : 'none';
    if (ff) ff.style.display = mode === 'friends'  ? '' : 'none';
    refreshTimeline();
}

function refreshTimeline() {
    if (tlMode === 'friends') { refreshFriendTimeline(); return; }
    // If we're navigating to a specific event and already have data, skip re-fetching
    // and render directly so _tlScrollTarget is consumed synchronously
    if (_tlScrollTarget && timelineEvents.length > 0) {
        filterTimeline();
        return;
    }
    const c = document.getElementById('tlContainer');
    if (c) c.innerHTML = '<div class="tl-loading"><div class="tl-sk-line"></div><div class="tl-sk-line tl-sk-short"></div><div class="tl-sk-line"></div><div class="tl-sk-line tl-sk-short"></div><div class="tl-sk-line"></div></div>';
    sendToCS({ action: 'getTimeline' });
}

function renderTimeline(events) {
    timelineEvents = Array.isArray(events) ? events : [];
    filterTimeline();
    // Update friend-detail preview if it's currently open
    if (typeof updateFdTlPreview === 'function') updateFdTlPreview();
}

function handleTimelineEvent(ev) {
    if (!ev || !ev.id) return;
    const idx = timelineEvents.findIndex(e => e.id === ev.id);
    if (idx >= 0) timelineEvents[idx] = ev;
    else timelineEvents.unshift(ev);
    // Re-sort by timestamp descending
    timelineEvents.sort((a, b) => b.timestamp.localeCompare(a.timestamp));
    filterTimeline();
    // Update friend-detail preview if it's currently open
    if (typeof updateFdTlPreview === 'function') updateFdTlPreview();
}

function setTlFilter(f) {
    tlFilter = f;
    document.querySelectorAll('#tlPersonalFilters .avatar-filter-btn').forEach(b => b.classList.remove('active'));
    const btn = document.getElementById(TL_FILTER_IDS[f]);
    if (btn) btn.classList.add('active');
    filterTimeline();
}

function filterTimeline() {
    if (tlMode !== 'personal') return;
    const search  = (document.getElementById('tlSearchInput')?.value ?? '').toLowerCase().trim();
    let filtered  = timelineEvents;

    if (tlFilter !== 'all')
        filtered = filtered.filter(e => e.type === tlFilter);

    if (search)
        filtered = filtered.filter(e => tlSearchable(e).includes(search));

    const c = document.getElementById('tlContainer');
    if (!c) return;

    if (!filtered.length) {
        c.innerHTML = '<div class="empty-msg">No timeline events match your filter.</div>';
        return;
    }

    c.innerHTML = buildTimelineHtml(filtered);

    // Scroll to and highlight a specific card if requested (e.g. from friend detail preview).
    // Only consume _tlScrollTarget if the card is actually in the newly-built DOM — if a
    // spurious handleTimelineEvent fires before getTimeline returns, the target card won't
    // be there yet. In that case keep _tlScrollTarget so the next filterTimeline retries.
    if (_tlScrollTarget) {
        const probe = c.querySelector('[data-tlid="' + _tlScrollTarget + '"]');
        if (probe) {
            const target = _tlScrollTarget;
            _tlScrollTarget = null;
            setTimeout(() => {
                const card = c.querySelector('[data-tlid="' + target + '"]');
                if (card) {
                    card.scrollIntoView({ behavior: 'instant', block: 'center' });
                    card.classList.add('tl-card-highlight');
                    setTimeout(() => card.classList.remove('tl-card-highlight'), 2000);
                }
            }, 50);
        }
    }
}

// Rendering helpers

function tlSearchable(e) {
    return [
        e.worldName, e.userName, e.senderName, e.notifType,
        NOTIF_TYPE_LABELS[e.notifType],
        e.message,
        e.photoPath ? e.photoPath.split(/[\\/]/).pop() : '',
        ...(e.players || []).map(p => p.displayName),
    ].filter(Boolean).join(' ').toLowerCase();
}

function buildTimelineHtml(events) {
    // Group by local date
    const byDate = {};
    events.forEach(e => {
        const d   = new Date(e.timestamp);
        const key = d.toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' });
        if (!byDate[key]) byDate[key] = [];
        byDate[key].push(e);
    });

    let html = '<div class="tl-wrap">';
    let cardIdx = 0;

    Object.entries(byDate).forEach(([date, evs]) => {
        html += `<div class="tl-date-sep"><span class="tl-date-label">${esc(date)}</span></div>`;
        evs.forEach(e => {
            const side = cardIdx % 2 === 0 ? 'left' : 'right';
            html += renderTlRow(e, side);
            cardIdx++;
        });
    });

    html += '</div>';
    return html;
}

function renderTlRow(ev, side) {
    const color   = TL_TYPE_COLOR[ev.type]  ?? 'var(--tx3)';
    const cardHtml = renderTlCard(ev);
    const dotHtml  = `<div class="tl-dot" style="background:${color}"></div>`;

    if (side === 'left') {
        return `<div class="tl-row">
            <div class="tl-card-side tl-side-left">${cardHtml}</div>
            <div class="tl-center-col">${dotHtml}</div>
            <div class="tl-card-side tl-side-right"></div>
        </div>`;
    }
    return `<div class="tl-row">
        <div class="tl-card-side tl-side-left"></div>
        <div class="tl-center-col">${dotHtml}</div>
        <div class="tl-card-side tl-side-right">${cardHtml}</div>
    </div>`;
}

function renderTlCard(ev) {
    const time  = new Date(ev.timestamp).toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
    const meta  = TL_TYPE_META[ev.type] ?? { icon: 'circle', label: ev.type };
    const color = TL_TYPE_COLOR[ev.type] ?? 'var(--tx3)';
    const ei    = jsq(ev.id);

    const header = `<div class="tl-card-header">
        <span class="msi tl-type-icon" style="color:${color}">${meta.icon}</span>
        <span class="tl-type-label">${esc(meta.label)}</span>
        <span class="tl-time">${esc(time)}</span>
    </div>`;

    let body = '';
    switch (ev.type) {
        case 'instance_join': body = renderTlJoinBody(ev);      break;
        case 'photo':         body = renderTlPhotoBody(ev);     break;
        case 'first_meet':    body = renderTlMeetBody(ev);      break;
        case 'meet_again':    body = renderTlMeetAgainBody(ev); break;
        case 'notification':  body = renderTlNotifBody(ev);     break;
    }

    return `<div class="tl-card" data-tlid="${esc(ev.id)}" onclick="openTlDetail('${ei}')">${header}${body}</div>`;
}

// Card bodies

function renderTlJoinBody(ev) {
    const thumb = ev.worldThumb
        ? `<div class="tl-thumb" style="background-image:url('${ev.worldThumb}')"></div>`
        : `<div class="tl-thumb tl-thumb-empty"><span class="msi" style="font-size:18px;color:var(--tx3);">travel_explore</span></div>`;
    const name  = ev.worldName || ev.worldId || 'Unknown World';
    const cnt   = (ev.players || []).length;
    const avs   = tlPlayerAvatars(ev.players, 3);
    const more  = cnt > 3 ? `<span class="tl-player-more">+${cnt - 3}</span>` : '';
    const bottom = cnt > 0
        ? `<div class="tl-player-row">${avs}${more}<span class="tl-player-label">${cnt} player${cnt !== 1 ? 's' : ''}</span></div>`
        : `<div class="tl-no-players">No player data yet</div>`;
    return `<div class="tl-card-body">${thumb}<div class="tl-card-info"><div class="tl-main-label">${esc(name)}</div>${bottom}</div></div>`;
}

function renderTlPhotoBody(ev) {
    const thumb = ev.photoUrl
        ? `<div class="tl-thumb tl-thumb-photo" style="background-image:url('${ev.photoUrl}')"></div>`
        : `<div class="tl-thumb tl-thumb-empty"><span class="msi" style="font-size:18px;color:var(--tx3);">camera</span></div>`;
    const name   = ev.photoPath ? ev.photoPath.split(/[\\/]/).pop() : 'Photo';
    const sub    = ev.worldName ? `<div class="tl-sub-label">${esc(ev.worldName)}</div>` : '';
    const cnt    = (ev.players || []).length;
    const avs    = tlPlayerAvatars(ev.players, 3);
    const more   = cnt > 3 ? `<span class="tl-player-more">+${cnt - 3}</span>` : '';
    const bottom = cnt > 0
        ? `<div class="tl-player-row">${avs}${more}<span class="tl-player-label">${cnt} player${cnt !== 1 ? 's' : ''}</span></div>`
        : `<div class="tl-no-players">No player data yet</div>`;
    return `<div class="tl-card-body">${thumb}<div class="tl-card-info"><div class="tl-main-label">${esc(name)}</div>${sub}${bottom}</div></div>`;
}

function renderTlMeetBody(ev) {
    const av   = ev.userImage
        ? `<div class="tl-av" style="background-image:url('${ev.userImage}')"></div>`
        : `<div class="tl-av tl-av-letter">${esc((ev.userName || '?')[0].toUpperCase())}</div>`;
    const sub  = ev.worldName ? `<div class="tl-sub-label">${esc(ev.worldName)}</div>` : '';
    return `<div class="tl-card-body">${av}<div class="tl-card-info"><div class="tl-main-label">${esc(ev.userName || 'Unknown')}</div>${sub}</div></div>`;
}

function renderTlMeetAgainBody(ev) {
    const av  = ev.userImage
        ? `<div class="tl-av" style="background-image:url('${ev.userImage}')"></div>`
        : `<div class="tl-av tl-av-letter">${esc((ev.userName || '?')[0].toUpperCase())}</div>`;
    const sub = ev.worldName ? `<div class="tl-sub-label">${esc(ev.worldName)}</div>` : '';
    return `<div class="tl-card-body">${av}<div class="tl-card-info"><div class="tl-main-label">${esc(ev.userName || 'Unknown')}</div>${sub}</div></div>`;
}

function renderTlNotifBody(ev) {
    const typeLabel = NOTIF_TYPE_LABELS[ev.notifType] || ev.notifType || 'Notification';
    const av  = ev.senderImage
        ? `<div class="tl-av" style="background-image:url('${ev.senderImage}')"></div>`
        : `<div class="tl-av tl-av-letter">${esc((ev.senderName || '?')[0].toUpperCase())}</div>`;
    const sub = ev.message ? `<div class="tl-sub-label">${esc(ev.message.slice(0, 70))}${ev.message.length > 70 ? '…' : ''}</div>` : '';
    return `<div class="tl-card-body">${av}<div class="tl-card-info"><div class="tl-main-label">${esc(ev.senderName || 'Unknown')}</div><div class="tl-type-chip">${esc(typeLabel)}</div>${sub}</div></div>`;
}

function tlPlayerAvatars(players, max) {
    return (players || []).slice(0, max).map(p => {
        return p.image
            ? `<div class="tl-player-av" style="background-image:url('${p.image}')" title="${esc(p.displayName)}"></div>`
            : `<div class="tl-player-av tl-player-av-letter" title="${esc(p.displayName)}">${esc((p.displayName || '?')[0].toUpperCase())}</div>`;
    }).join('');
}

// Detail modals (reuses #modalDetail / #detailModalContent)

function openTlDetail(id) {
    const ev = timelineEvents.find(e => e.id === id);
    if (!ev) return;
    const el = document.getElementById('detailModalContent');
    if (!el) return;

    switch (ev.type) {
        case 'instance_join': renderTlDetailJoin(ev, el);      break;
        case 'photo':         renderTlDetailPhoto(ev, el);     break;
        case 'first_meet':    renderTlDetailMeet(ev, el);      break;
        case 'meet_again':    renderTlDetailMeetAgain(ev, el); break;
        case 'notification':  renderTlDetailNotif(ev, el);     break;
    }

    document.getElementById('modalDetail').style.display = 'flex';
}

// Navigate to a specific event in the Timeline tab
function navigateToTlEvent(id) {
    if (!id) return;
    // Set the scroll target BEFORE switching tabs. filterTimeline() will consume it
    // once the cards are actually in the DOM (after C# responds to getTimeline).
    _tlScrollTarget = id;
    // Reset filter button state silently (don't call filterTimeline() yet, that
    // would consume _tlScrollTarget before the tab has rendered its cards)
    tlFilter = 'all';
    tlMode = 'personal';
    document.querySelectorAll('#tlPersonalFilters .avatar-filter-btn').forEach(b => b.classList.remove('active'));
    document.getElementById('tlModePersonal')?.classList.add('active');
    document.getElementById('tlModeFriends')?.classList.remove('active');
    const pf = document.getElementById('tlPersonalFilters');
    const ff = document.getElementById('tlFriendsFilters');
    if (pf) pf.style.display = '';
    if (ff) ff.style.display = 'none';
    const allBtn = document.getElementById(TL_FILTER_IDS['all']);
    if (allBtn) allBtn.classList.add('active');
    // Switch to Tab 12 -> refreshTimeline() -> C# sends timelineData -> renderTimeline()
    // -> filterTimeline() -> _tlScrollTarget consumed there
    showTab(12);
}

// Detail: instance join

function renderTlDetailJoin(ev, el) {
    const d       = new Date(ev.timestamp);
    const dateStr = d.toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' });
    const timeStr = d.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
    const banner  = ev.worldThumb
        ? `<div class="fd-banner"><img src="${ev.worldThumb}" onerror="this.parentElement.style.display='none'"><div class="fd-banner-fade"></div></div>`
        : '';
    const players = ev.players || [];

    let playersHtml = '';
    if (players.length > 0) {
        playersHtml = `<div class="tl-detail-sect">PLAYERS IN INSTANCE (${players.length})</div><div class="photo-players-list">`;
        players.forEach(p => {
            const fr    = vrcFriendsData.find(f => f.id === p.userId);
            const img   = p.image || fr?.image || '';
            const imgEl = img
                ? `<div class="inst-user-av" style="background-image:url('${img}')"></div>`
                : `<div class="inst-user-av inst-user-av-letter">${esc((p.displayName || '?')[0].toUpperCase())}</div>`;
            const click  = p.userId ? ` onclick="document.getElementById('modalDetail').style.display='none';openFriendDetail('${esc(p.userId)}')" style="cursor:pointer;"` : '';
            const badge  = fr ? '<span style="font-size:9px;color:var(--ok);margin-left:auto;">Friend</span>' : '';
            playersHtml += `<div class="inst-user-row"${click}>${imgEl}<span class="inst-user-name">${esc(p.displayName)}</span>${badge}</div>`;
        });
        playersHtml += '</div>';
    }

    const worldClick = ev.worldId
        ? ` style="cursor:pointer;" onclick="document.getElementById('modalDetail').style.display='none';openWorldSearchDetail('${esc(ev.worldId)}')"` : '';

    el.innerHTML = `${banner}<div class="fd-content${banner ? ' fd-has-banner' : ''}" style="padding:20px;">
        <h2 style="margin:0 0 12px;color:var(--tx0);font-size:16px;">${esc(ev.worldName || ev.worldId || 'Unknown World')}</h2>
        <div class="fd-meta">
            <div class="fd-meta-row"><span class="fd-meta-label">Date</span><span>${esc(dateStr)}</span></div>
            <div class="fd-meta-row"><span class="fd-meta-label">Time</span><span>${esc(timeStr)}</span></div>
            ${ev.worldId ? `<div class="fd-meta-row"${worldClick}><span class="fd-meta-label">World</span><span style="color:var(--accent-lt);">${esc(ev.worldName || ev.worldId)}</span></div>` : ''}
        </div>
        ${playersHtml}
        <div style="margin-top:14px;text-align:right;">
            <button class="modal-btn modal-btn-cancel" onclick="document.getElementById('modalDetail').style.display='none'">Close</button>
        </div>
    </div>`;
}

// Detail: photo

function renderTlDetailPhoto(ev, el) {
    const d       = new Date(ev.timestamp);
    const dateStr = d.toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' });
    const timeStr = d.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
    const photoJs = ev.photoUrl ? jsq(ev.photoUrl) : '';
    const banner  = ev.photoUrl
        ? `<div class="fd-banner" style="cursor:pointer;" onclick="openLightbox('${photoJs}','image')"><img src="${ev.photoUrl}" onerror="this.parentElement.style.display='none'"><div class="fd-banner-fade"></div></div>`
        : '';
    const fileName = ev.photoPath ? ev.photoPath.split(/[\\/]/).pop() : 'Photo';
    const players  = ev.players || [];

    let playersHtml = '';
    if (players.length > 0) {
        playersHtml = `<div class="tl-detail-sect">PLAYERS IN INSTANCE (${players.length})</div><div class="photo-players-list">`;
        players.forEach(p => {
            const fr    = vrcFriendsData.find(f => f.id === p.userId);
            const img   = p.image || fr?.image || '';
            const imgEl = img
                ? `<div class="inst-user-av" style="background-image:url('${img}')"></div>`
                : `<div class="inst-user-av inst-user-av-letter">${esc((p.displayName || '?')[0].toUpperCase())}</div>`;
            const click = p.userId ? ` onclick="document.getElementById('modalDetail').style.display='none';openFriendDetail('${esc(p.userId)}')" style="cursor:pointer;"` : '';
            const badge = fr ? '<span style="font-size:9px;color:var(--ok);margin-left:auto;">Friend</span>' : '';
            playersHtml += `<div class="inst-user-row"${click}>${imgEl}<span class="inst-user-name">${esc(p.displayName)}</span>${badge}</div>`;
        });
        playersHtml += '</div>';
    }

    const worldClick = ev.worldId
        ? ` style="cursor:pointer;" onclick="document.getElementById('modalDetail').style.display='none';openWorldSearchDetail('${esc(ev.worldId)}')"` : '';

    el.innerHTML = `${banner}<div class="fd-content${banner ? ' fd-has-banner' : ''}" style="padding:20px;">
        <h2 style="margin:0 0 12px;color:var(--tx0);font-size:16px;">${esc(fileName)}</h2>
        <div class="fd-meta">
            <div class="fd-meta-row"><span class="fd-meta-label">Date</span><span>${esc(dateStr)}</span></div>
            <div class="fd-meta-row"><span class="fd-meta-label">Time</span><span>${esc(timeStr)}</span></div>
            ${ev.worldId ? `<div class="fd-meta-row"${worldClick}><span class="fd-meta-label">World</span><span style="color:var(--accent-lt);">${esc(ev.worldName || ev.worldId)}</span></div>` : ''}
        </div>
        ${playersHtml}
        <div style="margin-top:14px;display:flex;gap:8px;justify-content:flex-end;">
            ${ev.photoUrl ? `<button class="modal-btn modal-btn-accent" onclick="openLightbox('${photoJs}','image')"><span class="msi" style="font-size:14px;">open_in_full</span> Full Size</button>` : ''}
            <button class="modal-btn modal-btn-cancel" onclick="document.getElementById('modalDetail').style.display='none'">Close</button>
        </div>
    </div>`;
}

// Detail: first meet

function renderTlDetailMeet(ev, el) {
    const d       = new Date(ev.timestamp);
    const dateStr = d.toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' });
    const timeStr = d.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
    const av      = ev.userImage
        ? `<div class="tl-detail-av" style="background-image:url('${ev.userImage}')"></div>`
        : `<div class="tl-detail-av tl-detail-av-letter">${esc((ev.userName || '?')[0].toUpperCase())}</div>`;

    const worldClickMeet = ev.worldId ? ` style="cursor:pointer;" onclick="document.getElementById('modalDetail').style.display='none';openWorldSearchDetail('${esc(ev.worldId)}')"` : '';
    el.innerHTML = `<div class="fd-content" style="padding:20px;">
        <div style="display:flex;gap:16px;align-items:center;margin-bottom:20px;">
            ${av}
            <div>
                <h2 style="margin:0 0 4px;color:var(--tx0);font-size:18px;">${esc(ev.userName || 'Unknown')}</h2>
                <div style="font-size:11px;color:var(--cyan);font-weight:700;letter-spacing:.05em;">FIRST MEET</div>
            </div>
        </div>
        <div class="fd-meta">
            <div class="fd-meta-row"><span class="fd-meta-label">Date</span><span>${esc(dateStr)}</span></div>
            <div class="fd-meta-row"><span class="fd-meta-label">Time</span><span>${esc(timeStr)}</span></div>
            ${ev.worldId ? `<div class="fd-meta-row"${worldClickMeet}><span class="fd-meta-label">World</span><span style="color:var(--accent-lt);">${esc(ev.worldName || ev.worldId)}</span></div>` : ''}
        </div>
        <div style="margin-top:14px;display:flex;gap:8px;justify-content:flex-end;">
            ${ev.userId ? `<button class="modal-btn modal-btn-accent" onclick="document.getElementById('modalDetail').style.display='none';openFriendDetail('${esc(ev.userId)}')">View Profile</button>` : ''}
            <button class="modal-btn modal-btn-cancel" onclick="document.getElementById('modalDetail').style.display='none'">Close</button>
        </div>
    </div>`;
}

// Detail: meet again

function renderTlDetailMeetAgain(ev, el) {
    const d       = new Date(ev.timestamp);
    const dateStr = d.toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' });
    const timeStr = d.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
    const av      = ev.userImage
        ? `<div class="tl-detail-av" style="background-image:url('${ev.userImage}')"></div>`
        : `<div class="tl-detail-av tl-detail-av-letter">${esc((ev.userName || '?')[0].toUpperCase())}</div>`;

    const worldClickAgain = ev.worldId ? ` style="cursor:pointer;" onclick="document.getElementById('modalDetail').style.display='none';openWorldSearchDetail('${esc(ev.worldId)}')"` : '';
    el.innerHTML = `<div class="fd-content" style="padding:20px;">
        <div style="display:flex;gap:16px;align-items:center;margin-bottom:20px;">
            ${av}
            <div>
                <h2 style="margin:0 0 4px;color:var(--tx0);font-size:18px;">${esc(ev.userName || 'Unknown')}</h2>
                <div style="font-size:11px;color:#AB47BC;font-weight:700;letter-spacing:.05em;">MET AGAIN</div>
            </div>
        </div>
        <div class="fd-meta">
            <div class="fd-meta-row"><span class="fd-meta-label">Date</span><span>${esc(dateStr)}</span></div>
            <div class="fd-meta-row"><span class="fd-meta-label">Time</span><span>${esc(timeStr)}</span></div>
            ${ev.worldId ? `<div class="fd-meta-row"${worldClickAgain}><span class="fd-meta-label">World</span><span style="color:var(--accent-lt);">${esc(ev.worldName || ev.worldId)}</span></div>` : ''}
        </div>
        <div style="margin-top:14px;display:flex;gap:8px;justify-content:flex-end;">
            ${ev.userId ? `<button class="modal-btn modal-btn-accent" onclick="document.getElementById('modalDetail').style.display='none';openFriendDetail('${esc(ev.userId)}')">View Profile</button>` : ''}
            <button class="modal-btn modal-btn-cancel" onclick="document.getElementById('modalDetail').style.display='none'">Close</button>
        </div>
    </div>`;
}

// Detail: notification

function renderTlDetailNotif(ev, el) {
    const d         = new Date(ev.timestamp);
    const dateStr   = d.toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' });
    const timeStr   = d.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
    const typeLabel = NOTIF_TYPE_LABELS[ev.notifType] || ev.notifType || 'Notification';
    const av        = ev.senderImage
        ? `<div class="tl-detail-av" style="background-image:url('${ev.senderImage}')"></div>`
        : `<div class="tl-detail-av tl-detail-av-letter">${esc((ev.senderName || '?')[0].toUpperCase())}</div>`;

    el.innerHTML = `<div class="fd-content" style="padding:20px;">
        <div style="display:flex;gap:16px;align-items:center;margin-bottom:20px;">
            ${av}
            <div>
                <h2 style="margin:0 0 4px;color:var(--tx0);font-size:18px;">${esc(ev.senderName || 'Unknown')}</h2>
                <div style="font-size:11px;color:var(--warn);font-weight:700;letter-spacing:.05em;">${esc(typeLabel.toUpperCase())}</div>
            </div>
        </div>
        <div class="fd-meta">
            <div class="fd-meta-row"><span class="fd-meta-label">Date</span><span>${esc(dateStr)}</span></div>
            <div class="fd-meta-row"><span class="fd-meta-label">Time</span><span>${esc(timeStr)}</span></div>
            <div class="fd-meta-row"><span class="fd-meta-label">Type</span><span>${esc(typeLabel)}</span></div>
            ${ev.message ? `<div class="fd-meta-row"><span class="fd-meta-label">Message</span><span>${esc(ev.message)}</span></div>` : ''}
        </div>
        <div style="margin-top:14px;display:flex;gap:8px;justify-content:flex-end;">
            ${ev.senderId ? `<button class="modal-btn modal-btn-accent" onclick="document.getElementById('modalDetail').style.display='none';openFriendDetail('${esc(ev.senderId)}')">View Profile</button>` : ''}
            <button class="modal-btn modal-btn-cancel" onclick="document.getElementById('modalDetail').style.display='none'">Close</button>
        </div>
    </div>`;
}

// === Friends Timeline ===

const FT_FILTER_IDS = {
    all:            'ftFAll',
    friend_gps:     'ftFGps',
    friend_status:  'ftFStatus',
    friend_online:  'ftFOnline',
    friend_offline: 'ftFOffline',
    friend_bio:     'ftFBio',
};

const FT_TYPE_COLOR = {
    friend_gps:     'var(--accent)',
    friend_status:  'var(--cyan)',
    friend_online:  'var(--ok)',
    friend_offline: 'var(--tx3)',
    friend_bio:     '#AB47BC',
};

const FT_TYPE_META = {
    friend_gps:     { icon: 'location_on',       label: 'Location'   },
    friend_status:  { icon: 'circle',             label: 'Status'     },
    friend_online:  { icon: 'login',              label: 'Online'     },
    friend_offline: { icon: 'power_settings_new', label: 'Offline'    },
    friend_bio:     { icon: 'edit_note',          label: 'Bio Change' },
};

const STATUS_COLORS = {
    'join me': 'var(--accent)',
    'active':  'var(--ok)',
    'ask me':  'var(--warn)',
    'busy':    'var(--err)',
    'offline': 'var(--tx3)',
};

function statusCssClass(s) {
    return (s || '').toLowerCase().replace(/\s+/g, '-');
}

// Public API

function refreshFriendTimeline() {
    const c = document.getElementById('tlContainer');
    if (c) c.innerHTML = '<div class="tl-loading"><div class="tl-sk-line"></div><div class="tl-sk-line tl-sk-short"></div><div class="tl-sk-line"></div><div class="tl-sk-line tl-sk-short"></div><div class="tl-sk-line"></div></div>';
    sendToCS({ action: 'getFriendTimeline' });
}

function renderFriendTimeline(events) {
    friendTimelineEvents = Array.isArray(events) ? events : [];
    filterFriendTimeline();
}

function handleFriendTimelineEvent(ev) {
    if (!ev || !ev.id) return;
    const idx = friendTimelineEvents.findIndex(e => e.id === ev.id);
    if (idx >= 0) friendTimelineEvents[idx] = ev;
    else friendTimelineEvents.unshift(ev);
    friendTimelineEvents.sort((a, b) => b.timestamp.localeCompare(a.timestamp));
    if (tlMode === 'friends') filterFriendTimeline();
}

function setFtFilter(f) {
    ftFilter = f;
    document.querySelectorAll('#tlFriendsFilters .avatar-filter-btn').forEach(b => b.classList.remove('active'));
    const btn = document.getElementById(FT_FILTER_IDS[f]);
    if (btn) btn.classList.add('active');
    filterFriendTimeline();
}

function filterFriendTimeline() {
    const search = (document.getElementById('tlSearchInput')?.value ?? '').toLowerCase().trim();
    let filtered = friendTimelineEvents;

    if (ftFilter !== 'all')
        filtered = filtered.filter(e => e.type === ftFilter);

    if (search)
        filtered = filtered.filter(e => ftSearchable(e).includes(search));

    const c = document.getElementById('tlContainer');
    if (!c) return;

    if (!filtered.length) {
        c.innerHTML = '<div class="empty-msg">No friend activity logged yet. Events appear here as friends move, change status, etc.</div>';
        return;
    }

    c.innerHTML = buildFriendTimelineHtml(filtered);
}

function ftSearchable(e) {
    return [e.friendName, e.worldName, e.newValue, e.oldValue, e.location]
        .filter(Boolean).join(' ').toLowerCase();
}

// Rendering

function buildFriendTimelineHtml(events) {
    const byDate = {};
    events.forEach(e => {
        const d   = new Date(e.timestamp);
        const key = d.toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' });
        if (!byDate[key]) byDate[key] = [];
        byDate[key].push(e);
    });

    let html = '<div class="tl-wrap">';
    let cardIdx = 0;
    Object.entries(byDate).forEach(([date, evs]) => {
        html += `<div class="tl-date-sep"><span class="tl-date-label">${esc(date)}</span></div>`;
        evs.forEach(e => {
            const side = cardIdx % 2 === 0 ? 'left' : 'right';
            html += renderFtRow(e, side);
            cardIdx++;
        });
    });
    html += '</div>';
    return html;
}

function renderFtRow(ev, side) {
    const color   = FT_TYPE_COLOR[ev.type] ?? 'var(--tx3)';
    const cardHtml = renderFtCard(ev);
    const dotHtml  = `<div class="tl-dot" style="background:${color}"></div>`;

    if (side === 'left') {
        return `<div class="tl-row">
            <div class="tl-card-side tl-side-left">${cardHtml}</div>
            <div class="tl-center-col">${dotHtml}</div>
            <div class="tl-card-side tl-side-right"></div>
        </div>`;
    }
    return `<div class="tl-row">
        <div class="tl-card-side tl-side-left"></div>
        <div class="tl-center-col">${dotHtml}</div>
        <div class="tl-card-side tl-side-right">${cardHtml}</div>
    </div>`;
}

function renderFtCard(ev) {
    const time  = new Date(ev.timestamp).toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
    const meta  = FT_TYPE_META[ev.type] ?? { icon: 'circle', label: ev.type };
    const color = FT_TYPE_COLOR[ev.type] ?? 'var(--tx3)';
    const ei    = jsq(ev.id);

    const header = `<div class="tl-card-header">
        <span class="msi tl-type-icon" style="color:${color}">${meta.icon}</span>
        <span class="tl-type-label">${esc(meta.label)}</span>
        <span class="tl-time">${esc(time)}</span>
    </div>`;

    let body = '';
    switch (ev.type) {
        case 'friend_gps':     body = renderFtGpsBody(ev);     break;
        case 'friend_status':  body = renderFtStatusBody(ev);  break;
        case 'friend_online':  body = renderFtOnlineBody(ev);  break;
        case 'friend_offline': body = renderFtOfflineBody(ev); break;
        case 'friend_bio':     body = renderFtBioBody(ev);     break;
    }

    return `<div class="tl-card" data-ftid="${esc(ev.id)}" onclick="openFtDetail('${ei}')">${header}${body}</div>`;
}

// Card bodies

function ftFriendAv(ev, cssClass) {
    return ev.friendImage
        ? `<div class="${cssClass}" style="background-image:url('${ev.friendImage}')"></div>`
        : `<div class="${cssClass} tl-av-letter">${esc((ev.friendName || '?')[0].toUpperCase())}</div>`;
}

function renderFtGpsBody(ev) {
    const thumb = ev.worldThumb
        ? `<div class="tl-thumb" style="background-image:url('${ev.worldThumb}')"></div>`
        : `<div class="tl-thumb tl-thumb-empty"><span class="msi" style="font-size:18px;color:var(--tx3);">travel_explore</span></div>`;
    const wname = ev.worldName || ev.worldId || 'Unknown World';
    const av    = ftFriendAv(ev, 'tl-player-av');
    return `<div class="tl-card-body">${thumb}<div class="tl-card-info">
        <div class="tl-main-label">${esc(wname)}</div>
        <div class="tl-player-row">${av}<span class="tl-player-label">${esc(ev.friendName || 'Unknown')}</span></div>
    </div></div>`;
}

function renderFtStatusBody(ev) {
    const av      = ftFriendAv(ev, 'tl-av');
    const oldCls  = statusCssClass(ev.oldValue);
    const newCls  = statusCssClass(ev.newValue);
    const chips   = `<div style="display:flex;align-items:center;gap:6px;margin-top:4px;">
        <span class="ft-status-chip ${oldCls}">${esc(ev.oldValue || '?')}</span>
        <span class="msi" style="font-size:12px;color:var(--tx3);">arrow_forward</span>
        <span class="ft-status-chip ${newCls}">${esc(ev.newValue || '?')}</span>
    </div>`;
    return `<div class="tl-card-body">${av}<div class="tl-card-info">
        <div class="tl-main-label">${esc(ev.friendName || 'Unknown')}</div>${chips}
    </div></div>`;
}

function renderFtOnlineBody(ev) {
    const av = ftFriendAv(ev, 'tl-av');
    return `<div class="tl-card-body">${av}<div class="tl-card-info">
        <div class="tl-main-label">${esc(ev.friendName || 'Unknown')}</div>
        <div class="tl-sub-label" style="color:var(--ok);">Came online</div>
    </div></div>`;
}

function renderFtOfflineBody(ev) {
    const av = ftFriendAv(ev, 'tl-av');
    return `<div class="tl-card-body">${av}<div class="tl-card-info">
        <div class="tl-main-label">${esc(ev.friendName || 'Unknown')}</div>
        <div class="tl-sub-label" style="color:var(--tx3);">Went offline</div>
    </div></div>`;
}

function renderFtBioBody(ev) {
    const av      = ftFriendAv(ev, 'tl-av');
    const preview = (ev.newValue || '').slice(0, 60);
    const ellipsis = (ev.newValue || '').length > 60 ? '...' : '';
    return `<div class="tl-card-body">${av}<div class="tl-card-info">
        <div class="tl-main-label">${esc(ev.friendName || 'Unknown')}</div>
        <div class="tl-sub-label">${esc(preview)}${ellipsis}</div>
    </div></div>`;
}

// Detail modals

function openFtDetail(id) {
    const ev = friendTimelineEvents.find(e => e.id === id);
    if (!ev) return;
    const el = document.getElementById('detailModalContent');
    if (!el) return;

    switch (ev.type) {
        case 'friend_gps':     renderFtDetailGps(ev, el);     break;
        case 'friend_status':  renderFtDetailStatus(ev, el);  break;
        case 'friend_online':  renderFtDetailOnline(ev, el);  break;
        case 'friend_offline': renderFtDetailOffline(ev, el); break;
        case 'friend_bio':     renderFtDetailBio(ev, el);     break;
    }

    document.getElementById('modalDetail').style.display = 'flex';
}

function ftDetailDatetime(ev) {
    const d = new Date(ev.timestamp);
    return {
        dateStr: d.toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' }),
        timeStr: d.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' }),
    };
}

function ftDetailAvRow(ev) {
    const av = ev.friendImage
        ? `<div class="tl-detail-av" style="background-image:url('${ev.friendImage}')"></div>`
        : `<div class="tl-detail-av tl-detail-av-letter">${esc((ev.friendName || '?')[0].toUpperCase())}</div>`;
    return `<div style="display:flex;gap:16px;align-items:center;margin-bottom:20px;">${av}
        <div><h2 style="margin:0 0 4px;color:var(--tx0);font-size:18px;">${esc(ev.friendName || 'Unknown')}</h2>
        ${ev.friendId ? `<div style="font-size:10px;color:var(--tx3);">${esc(ev.friendId)}</div>` : ''}
        </div></div>`;
}

function ftDetailClose() {
    return `<button class="modal-btn modal-btn-cancel" onclick="document.getElementById('modalDetail').style.display='none'">Close</button>`;
}

function ftDetailViewProfile(ev) {
    return ev.friendId
        ? `<button class="modal-btn modal-btn-accent" onclick="document.getElementById('modalDetail').style.display='none';openFriendDetail('${esc(ev.friendId)}')">View Profile</button>`
        : '';
}

function renderFtDetailGps(ev, el) {
    const { dateStr, timeStr } = ftDetailDatetime(ev);
    const banner = ev.worldThumb
        ? `<div class="fd-banner"><img src="${ev.worldThumb}" onerror="this.parentElement.style.display='none'"><div class="fd-banner-fade"></div></div>`
        : '';
    const wname = ev.worldName || ev.worldId || 'Unknown World';
    const worldClick = ev.worldId
        ? ` style="cursor:pointer;" onclick="document.getElementById('modalDetail').style.display='none';openWorldSearchDetail('${esc(ev.worldId)}')"` : '';

    el.innerHTML = `${banner}<div class="fd-content${banner ? ' fd-has-banner' : ''}" style="padding:20px;">
        ${ftDetailAvRow(ev)}
        <div class="fd-meta">
            <div class="fd-meta-row"><span class="fd-meta-label">Date</span><span>${esc(dateStr)}</span></div>
            <div class="fd-meta-row"><span class="fd-meta-label">Time</span><span>${esc(timeStr)}</span></div>
            <div class="fd-meta-row"${worldClick}><span class="fd-meta-label">World</span><span style="color:var(--accent-lt);">${esc(wname)}</span></div>
        </div>
        <div style="margin-top:14px;display:flex;gap:8px;justify-content:flex-end;">
            ${ev.worldId ? `<button class="modal-btn modal-btn-accent" onclick="document.getElementById('modalDetail').style.display='none';openWorldSearchDetail('${esc(ev.worldId)}')"><span class="msi" style="font-size:14px;">travel_explore</span> Open World</button>` : ''}
            ${ftDetailViewProfile(ev)}
            ${ftDetailClose()}
        </div>
    </div>`;
}

function renderFtDetailStatus(ev, el) {
    const { dateStr, timeStr } = ftDetailDatetime(ev);
    const oldCls = statusCssClass(ev.oldValue);
    const newCls = statusCssClass(ev.newValue);

    el.innerHTML = `<div class="fd-content" style="padding:20px;">
        ${ftDetailAvRow(ev)}
        <div class="fd-meta">
            <div class="fd-meta-row"><span class="fd-meta-label">Date</span><span>${esc(dateStr)}</span></div>
            <div class="fd-meta-row"><span class="fd-meta-label">Time</span><span>${esc(timeStr)}</span></div>
            <div class="fd-meta-row"><span class="fd-meta-label">Change</span>
                <span style="display:flex;align-items:center;gap:6px;">
                    <span class="ft-status-chip ${oldCls}">${esc(ev.oldValue || '?')}</span>
                    <span class="msi" style="font-size:12px;color:var(--tx3);">arrow_forward</span>
                    <span class="ft-status-chip ${newCls}">${esc(ev.newValue || '?')}</span>
                </span>
            </div>
        </div>
        <div style="margin-top:14px;display:flex;gap:8px;justify-content:flex-end;">
            ${ftDetailViewProfile(ev)}${ftDetailClose()}
        </div>
    </div>`;
}

function renderFtDetailOnline(ev, el) {
    const { dateStr, timeStr } = ftDetailDatetime(ev);
    el.innerHTML = `<div class="fd-content" style="padding:20px;">
        ${ftDetailAvRow(ev)}
        <div class="fd-meta">
            <div class="fd-meta-row"><span class="fd-meta-label">Date</span><span>${esc(dateStr)}</span></div>
            <div class="fd-meta-row"><span class="fd-meta-label">Time</span><span>${esc(timeStr)}</span></div>
            <div class="fd-meta-row"><span class="fd-meta-label">Event</span><span style="color:var(--ok);">Came online</span></div>
        </div>
        <div style="margin-top:14px;display:flex;gap:8px;justify-content:flex-end;">
            ${ftDetailViewProfile(ev)}${ftDetailClose()}
        </div>
    </div>`;
}

function renderFtDetailOffline(ev, el) {
    const { dateStr, timeStr } = ftDetailDatetime(ev);
    el.innerHTML = `<div class="fd-content" style="padding:20px;">
        ${ftDetailAvRow(ev)}
        <div class="fd-meta">
            <div class="fd-meta-row"><span class="fd-meta-label">Date</span><span>${esc(dateStr)}</span></div>
            <div class="fd-meta-row"><span class="fd-meta-label">Time</span><span>${esc(timeStr)}</span></div>
            <div class="fd-meta-row"><span class="fd-meta-label">Event</span><span style="color:var(--tx3);">Went offline</span></div>
        </div>
        <div style="margin-top:14px;display:flex;gap:8px;justify-content:flex-end;">
            ${ftDetailViewProfile(ev)}${ftDetailClose()}
        </div>
    </div>`;
}

function renderFtDetailBio(ev, el) {
    const { dateStr, timeStr } = ftDetailDatetime(ev);
    el.innerHTML = `<div class="fd-content" style="padding:20px;">
        ${ftDetailAvRow(ev)}
        <div class="fd-meta">
            <div class="fd-meta-row"><span class="fd-meta-label">Date</span><span>${esc(dateStr)}</span></div>
            <div class="fd-meta-row"><span class="fd-meta-label">Time</span><span>${esc(timeStr)}</span></div>
        </div>
        ${ev.oldValue ? `<div style="margin-top:12px;"><div style="font-size:10px;color:var(--tx3);margin-bottom:4px;">PREVIOUS BIO</div>
            <div style="font-size:12px;color:var(--tx2);background:var(--bg2);padding:8px 10px;border-radius:6px;white-space:pre-wrap;">${esc(ev.oldValue)}</div></div>` : ''}
        ${ev.newValue ? `<div style="margin-top:10px;"><div style="font-size:10px;color:var(--tx3);margin-bottom:4px;">NEW BIO</div>
            <div style="font-size:12px;color:var(--tx1);background:var(--bg2);padding:8px 10px;border-radius:6px;white-space:pre-wrap;">${esc(ev.newValue)}</div></div>` : ''}
        <div style="margin-top:14px;display:flex;gap:8px;justify-content:flex-end;">
            ${ftDetailViewProfile(ev)}${ftDetailClose()}
        </div>
    </div>`;
}
