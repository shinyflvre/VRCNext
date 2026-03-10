// Timeline - Tab 12
// Globals: timelineEvents, tlFilter  (declared in core.js)

// Pending scroll-to target: consumed by filterTimeline() after DOM is built
let _tlScrollTarget = null;

// Personal Timeline pagination state
let tlOffset = 0, tlLoading = false, tlHasMore = false;
// Total count from server (for accurate paginator)
let tlTotal = 0;
// Timeline view: how many events to render (Load More adds 100)
let tlRenderedCount = 100;
// List view: current page (0-indexed, 100 per page)
let tlListPage = 0;
// When set, next renderTimeline call replaces timelineEvents with the fetched page
let _tlPendingListPage = null;

// Friends Timeline pagination state
let ftlOffset = 0, ftlLoading = false, ftlHasMore = false;
// Total count from server (for accurate paginator)
let ftlTotal = 0;
// Timeline view: how many events to render (Load More adds 100)
let ftlRenderedCount = 100;
// List view: current page (0-indexed, 100 per page)
let ftlListPage = 0;
// When set, next renderFriendTimeline call replaces friendTimelineEvents with the fetched page
let _ftlPendingListPage = null;

// Active date filter (ISO string like "2026-03-01", empty = no filter)
let tlDateFilter = '';
let tlTabInited  = false;

// View mode: 'timeline' (card view) or 'list' (table view) — persisted in localStorage
let tlViewMode = localStorage.getItem('tlViewMode') || 'timeline';

// Server-side search state – Personal Timeline
let _tlSearchTimer  = null;
let _tlSearchMode   = false;
let _tlSearchEvents = [];   // current search page's events (max 100)
let _tlSearchQuery  = '';
let _tlSearchDate   = '';
let _tlSearchTotal  = 0;    // real total count from DB (COUNT query)
let _tlSearchPage   = 0;    // current search page (0-indexed)

// Server-side search state – Friends Timeline
let _ftlSearchTimer  = null;
let _ftlSearchMode   = false;
let _ftlSearchEvents = [];
let _ftlSearchQuery  = '';
let _ftlSearchDate   = '';
let _ftlSearchTotal  = 0;
let _ftlSearchPage   = 0;

// Filter button map
const TL_FILTER_IDS = {
    all:           'tlFAll',
    instance_join: 'tlFJoin',
    photo:         'tlFPhoto',
    first_meet:    'tlFMeet',
    meet_again:    'tlFMeetAgain',
    notification:  'tlFNotif',
    avatar_switch: 'tlFAvatar',
    video_url:     'tlFUrl',
};

// Type colours
const TL_TYPE_COLOR = {
    instance_join: 'var(--accent)',
    photo:         'var(--ok)',
    first_meet:    'var(--cyan)',
    meet_again:    '#AB47BC',
    notification:  'var(--warn)',
    avatar_switch: '#FF7043',
    video_url:     '#29B6F6',
};

// Type labels and icons
const TL_TYPE_META = {
    instance_join: { icon: 'travel_explore', label: 'Instance Join' },
    photo:         { icon: 'camera',         label: 'Photo'         },
    first_meet:    { icon: 'person_add',     label: 'First Meet'    },
    meet_again:    { icon: 'person_check',   label: 'Meet Again'    },
    notification:  { icon: 'notifications',  label: 'Notification'  },
    avatar_switch: { icon: 'checkroom',      label: 'Avatar Switch' },
    video_url:     { icon: 'link',           label: 'URL'           },
};

// Notification type labels
const NOTIF_TYPE_LABELS = {
    // v1
    friendRequest:              'Friend Request',
    invite:                     'World Invite',
    requestInvite:              'Invite Request',
    inviteResponse:             'Invite Response',
    requestInviteResponse:      'Invite Req. Response',
    votetokick:                 'Vote to Kick',
    boop:                       'Boop',
    message:                    'Message',
    halted:                     'Instance Closed',
    // group
    'group.announcement':       'Group Announcement',
    'group.invite':             'Group Invite',
    'group.joinRequest':        'Group Join Request',
    'group.informationRequest': 'Group Info Request',
    'group.transfer':           'Group Transfer',
    'group.informative':        'Group Info',
    'group.post':               'Group Post',
    'group.event.created':      'Group Event Created',
    'group.event.starting':     'Group Event Starting',
    // v2-only
    'avatarreview.success':     'Avatar Approved',
    'avatarreview.failure':     'Avatar Rejected',
    'badge.earned':             'Badge Earned',
    'economy.alert':            'Economy Alert',
    'economy.received.gift':    'Gift Received',
    'event.announcement':       'Event Announcement',
    'invite.instance.contentGated': 'Content Gated Invite',
    'moderation.contentrestriction': 'Content Restriction',
    'moderation.notice':        'Moderation Notice',
    'moderation.report.closed': 'Report Closed',
    'moderation.warning.group': 'Group Warning',
    'promo.redeem':             'Promo Redeemed',
    'vrcplus.gift':             'VRC+ Gift',
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
    _tlSearchMode = false; _tlSearchQuery = ''; _tlSearchDate = '';
    _ftlSearchMode = false; _ftlSearchQuery = ''; _ftlSearchDate = '';
    const activeSearch = (document.getElementById('tlSearchInput')?.value ?? '').trim();
    if (activeSearch) {
        if (mode === 'friends') { filterFriendTimeline(); return; }
        else { filterTimeline(); return; }
    }
    refreshTimeline();
}

function setTlViewMode(mode) {
    tlViewMode = mode;
    localStorage.setItem('tlViewMode', mode);
    document.getElementById('tlViewTimeline')?.classList.toggle('active', mode === 'timeline');
    document.getElementById('tlViewList')?.classList.toggle('active', mode === 'list');
    if (tlMode === 'friends') filterFriendTimeline();
    else filterTimeline();
}

function _initTlViewButtons() {
    document.getElementById('tlViewTimeline')?.classList.toggle('active', tlViewMode === 'timeline');
    document.getElementById('tlViewList')?.classList.toggle('active', tlViewMode === 'list');
}

function refreshTimeline() {
    _initTlViewButtons();
    if (tlMode === 'friends') { refreshFriendTimeline(); return; }
    if (!tlTabInited) {
        tlTabInited = true;
        const t = new Date();
        applyTlDateFilter(`${t.getFullYear()}-${String(t.getMonth()+1).padStart(2,'0')}-${String(t.getDate()).padStart(2,'0')}`);
        return;
    }
    // If we're navigating to a specific event and already have data, skip re-fetching
    // and render directly so _tlScrollTarget is consumed synchronously
    if (_tlScrollTarget && timelineEvents.length > 0) {
        filterTimeline();
        return;
    }
    timelineEvents  = [];
    tlOffset        = 0;
    tlHasMore       = false;
    tlLoading       = false;
    tlRenderedCount = 100;
    tlListPage      = 0;
    tlTotal         = 0;
    // If search is active, keep showing existing results during refresh instead of a loading flash
    const activeSearch = (document.getElementById('tlSearchInput')?.value ?? '').trim();
    const c = document.getElementById('tlContainer');
    if (c && !(_tlSearchMode && activeSearch)) {
        c.innerHTML = '<div class="tl-loading"><div class="tl-sk-line"></div><div class="tl-sk-line tl-sk-short"></div><div class="tl-sk-line"></div><div class="tl-sk-line tl-sk-short"></div><div class="tl-sk-line"></div></div>';
    }
    const typeParam = tlFilter === 'all' ? '' : tlFilter;
    if (tlDateFilter) sendToCS({ action: 'getTimelineByDate', date: tlDateFilter, type: typeParam });
    else              sendToCS({ action: 'getTimeline', type: typeParam });
}

function renderTimeline(payload) {
    const events  = Array.isArray(payload) ? payload : (payload?.events  ?? []);
    const hasMore = Array.isArray(payload) ? false   : (payload?.hasMore ?? false);
    const offset  = Array.isArray(payload) ? 0       : (payload?.offset  ?? 0);
    const total   = Array.isArray(payload) ? 0       : (payload?.total   ?? 0);

    // Discard stale response if filter was switched while this request was in-flight
    if (!Array.isArray(payload) && payload?.type !== undefined) {
        const expectedType = tlFilter === 'all' ? '' : tlFilter;
        if (payload.type !== expectedType) return;
    }

    if (total > 0) tlTotal = total;

    if (_tlPendingListPage !== null) {
        // Direct page navigation: replace current events with this page's data
        timelineEvents     = events;
        tlListPage         = _tlPendingListPage;
        tlRenderedCount    = events.length;
        _tlPendingListPage = null;
    } else if (offset === 0) {
        timelineEvents  = events;
        tlRenderedCount = 100;
        tlListPage      = 0;
    } else {
        // Load More append (timeline/card view)
        timelineEvents  = timelineEvents.concat(events);
        tlRenderedCount += events.length;
    }
    tlOffset  = offset + events.length;
    tlHasMore = hasMore;
    tlLoading = false;
    filterTimeline();
    if (typeof updateFdTlPreview === 'function') updateFdTlPreview();
}

function handleTimelineEvent(ev) {
    if (!ev || !ev.id) return;
    // If a type filter is active, only inject events that match it to avoid polluting the view
    if (tlFilter !== 'all' && ev.type !== tlFilter) return;
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
    tlFilter        = f;
    tlListPage      = 0;
    tlRenderedCount = 100;
    tlTotal         = 0;
    document.querySelectorAll('#tlPersonalFilters .sub-tab-btn').forEach(b => b.classList.remove('active'));
    const btn = document.getElementById(TL_FILTER_IDS[f]);
    if (btn) btn.classList.add('active');
    // Reset and re-fetch from server with type filter (server-side filtering)
    timelineEvents = [];
    tlOffset   = 0;
    tlHasMore  = false;
    tlLoading  = false;
    _tlSearchMode = false; _tlSearchQuery = ''; _tlSearchDate = '';
    const activeSearch = (document.getElementById('tlSearchInput')?.value ?? '').trim();
    if (activeSearch) { filterTimeline(); return; }
    const c = document.getElementById('tlContainer');
    if (c) c.innerHTML = '<div class="tl-loading"><div class="tl-sk-line"></div><div class="tl-sk-line tl-sk-short"></div><div class="tl-sk-line"></div><div class="tl-sk-line tl-sk-short"></div><div class="tl-sk-line"></div></div>';
    const typeParam = f === 'all' ? '' : f;
    if (tlDateFilter) sendToCS({ action: 'getTimelineByDate', date: tlDateFilter, type: typeParam });
    else              sendToCS({ action: 'getTimeline', type: typeParam });
}

function filterTimeline() {
    if (tlMode !== 'personal') return;
    const search = (document.getElementById('tlSearchInput')?.value ?? '').toLowerCase().trim();

    // When a search query is active: use server-side search for complete results
    if (search) {
        if (_tlSearchMode && search === _tlSearchQuery && tlDateFilter === _tlSearchDate) {
            // We have fresh results for exactly this query+date – render (handles filter-only changes)
            _renderTlSearchResults(search);
            return;
        }
        // Query or date changed → clear stale state, show loading, debounce
        _tlSearchMode   = false;
        _tlSearchQuery  = '';
        _tlSearchDate   = '';
        tlListPage      = 0;
        tlRenderedCount = 100;
        const c = document.getElementById('tlContainer');
        if (c) c.innerHTML = '<div class="tl-loading"><div class="tl-sk-line"></div><div class="tl-sk-line tl-sk-short"></div><div class="tl-sk-line"></div></div>';
        _setTlPaginator('');
        clearTimeout(_tlSearchTimer);
        _tlSearchTimer = setTimeout(() => {
            const typeParam = tlFilter === 'all' ? '' : tlFilter;
            sendToCS({ action: 'searchTimeline', query: search, date: tlDateFilter, offset: 0, type: typeParam });
        }, 300);
        return;
    }

    // No search – clear search mode and show paginated events
    _tlSearchMode   = false;
    _tlSearchEvents = [];

    const c = document.getElementById('tlContainer');
    if (!c) return;

    if (!timelineEvents.length && !tlLoading) {
        // Events cleared (e.g. filter switched while searching) — reload from server
        refreshTimeline();
        return;
    }

    const prevScrollTop = c.scrollTop;

    // Both views use server-side pagination — timelineEvents holds current page only
    const totalPages = tlTotal > 0
        ? Math.ceil(tlTotal / 100)
        : Math.max(tlListPage + 1, 1) + (tlHasMore ? 1 : 0);

    const contentHtml = tlViewMode === 'list'
        ? buildPersonalListHtml(timelineEvents)
        : buildTimelineHtml(timelineEvents);
    c.innerHTML = contentHtml;
    _setTlPaginator(buildTlPagination(tlListPage, totalPages, tlHasMore));

    if (prevScrollTop > 0) c.scrollTop = prevScrollTop;

    // Scroll to and highlight a specific card if requested (e.g. from friend detail preview).
    // Only consume _tlScrollTarget if the card is actually in the newly-built DOM.
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

function _renderTlSearchResults(search) {
    const c = document.getElementById('tlContainer');
    if (!c) return;

    const events = _tlSearchEvents; // already type-filtered by server (type sent in request)

    if (!events.length) {
        c.innerHTML = `<div class="empty-msg">No results for "<b>${esc(search)}</b>".</div>`;
        _setTlPaginator('');
        return;
    }

    const total      = _tlSearchTotal;
    const totalPages = total > 0 ? Math.ceil(total / 100) : 1;
    const banner = `<div style="padding:6px 12px;font-size:11px;color:var(--tx3);border-bottom:1px solid var(--brd);">`
        + `${total.toLocaleString()} result${total !== 1 ? 's' : ''} for "<b>${esc(search)}</b>"</div>`;
    let html = banner + (tlViewMode === 'list' ? buildPersonalListHtml(events) : buildTimelineHtml(events));
    c.innerHTML = html;
    _setTlPaginator(buildSearchPagination(_tlSearchPage, totalPages, 'tlGoSearchPage'));
}

// Called when backend delivers search results
function handleTlSearchResults(payload) {
    const q = (payload.query || '').toLowerCase().trim();
    // Ignore stale responses: user has already typed something different or changed the date
    const currentSearch = (document.getElementById('tlSearchInput')?.value ?? '').toLowerCase().trim();
    if (q !== currentSearch) return;
    if ((payload.date || '') !== tlDateFilter) return;
    const offset = payload.offset ?? 0;
    // Always replace — each page nav fetches the exact page, no appending
    _tlSearchEvents = payload.events || [];
    _tlSearchTotal  = payload.total ?? 0;
    _tlSearchPage   = Math.floor(offset / 100);
    _tlSearchMode   = true;
    _tlSearchQuery  = q;
    _tlSearchDate   = payload.date || '';
    filterTimeline();
}

function tlGoSearchPage(page) {
    if (page < 0) return;
    const typeParam = tlFilter === 'all' ? '' : tlFilter;
    sendToCS({ action: 'searchTimeline', query: _tlSearchQuery, date: _tlSearchDate, offset: page * 100, type: typeParam });
}

function buildSearchPagination(page, totalPages, onPageFn) {
    if (totalPages <= 1) return '';
    const delta = 3;
    const range = [];
    for (let i = 0; i < totalPages; i++) {
        if (i === 0 || i === totalPages - 1 || (i >= page - delta && i <= page + delta))
            range.push(i);
    }
    let btns = '';
    let prev = -1;
    range.forEach(i => {
        if (prev !== -1 && i > prev + 1) btns += `<span style="padding:0 4px;color:var(--tx3);">…</span>`;
        const active = i === page ? 'style="background:var(--accent);color:#fff;"' : '';
        btns += `<button class="vrcn-button" ${active} onclick="${onPageFn}(${i})">${i + 1}</button>`;
        prev = i;
    });
    const prevDis = page === 0 ? 'disabled' : '';
    const nextDis = page >= totalPages - 1 ? 'disabled' : '';
    return `<button class="vrcn-button" ${prevDis} onclick="${onPageFn}(${page - 1})"><span class="msi" style="font-size:16px;">chevron_left</span></button>
        ${btns}
        <button class="vrcn-button" ${nextDis} onclick="${onPageFn}(${page + 1})"><span class="msi" style="font-size:16px;">chevron_right</span></button>`;
}

// Personal Timeline pagination helpers

function loadMoreTimeline() {
    if (tlLoading) return;
    // Drain already-loaded pool first (timeline/card view)
    if (timelineEvents.length > tlRenderedCount) {
        tlRenderedCount += 100;
        filterTimeline();
        return;
    }
    if (!tlHasMore) return;
    tlLoading = true;
    const btn = document.getElementById('tlLoadMoreBtn');
    if (btn) { btn.disabled = true; btn.innerHTML = '<span class="msi" style="font-size:16px;">hourglass_empty</span> Loading…'; }
    sendToCS({ action: 'getTimelinePage', offset: tlOffset, type: tlFilter === 'all' ? '' : tlFilter });
}

function _setTlPaginator(html) {
    const bar = document.getElementById('tlPaginatorBar');
    if (bar) bar.innerHTML = html;
}

function buildTlPagination(page, totalPages, hasMore) {
    if (totalPages <= 1 && !hasMore) return '';
    const delta = 3;
    const range = [];
    for (let i = 0; i < totalPages; i++) {
        if (i === 0 || i === totalPages - 1 || (i >= page - delta && i <= page + delta))
            range.push(i);
    }
    let btns = '';
    let prev = -1;
    range.forEach(i => {
        if (prev !== -1 && i > prev + 1) btns += `<span style="padding:0 4px;color:var(--tx3);">…</span>`;
        const active = i === page ? 'style="background:var(--accent);color:#fff;"' : '';
        btns += `<button class="vrcn-button" ${active} onclick="tlGoPage(${i})">${i + 1}</button>`;
        prev = i;
    });
    const prevDis = page === 0 ? 'disabled' : '';
    const nextDis = (page >= totalPages - 1 && !hasMore) ? 'disabled' : '';
    const countInfo = tlTotal > 0 ? `<span style="font-size:11px;color:var(--tx3);padding:0 8px;">${tlTotal.toLocaleString()} total</span>` : '';
    return `<button class="vrcn-button" ${prevDis} onclick="tlGoPage(${page - 1})"><span class="msi" style="font-size:16px;">chevron_left</span></button>
        ${btns}
        <button class="vrcn-button" ${nextDis} onclick="tlGoPage(${page + 1})"><span class="msi" style="font-size:16px;">chevron_right</span></button>
        ${countInfo}`;
}

function tlGoPage(page) {
    if (page < 0) return;
    const totalPages = tlTotal > 0 ? Math.ceil(tlTotal / 100) : null;
    if (totalPages !== null && page >= totalPages) return;
    if (page === tlListPage && _tlPendingListPage === null && !tlLoading) {
        // Already on this page — just scroll top
        const c = document.getElementById('tlContainer');
        if (c) c.scrollTop = 0;
        return;
    }
    // Fetch this page directly from DB at absolute offset
    _tlPendingListPage = page;
    tlLoading = true;
    sendToCS({ action: 'getTimelinePage', offset: page * 100, type: tlFilter === 'all' ? '' : tlFilter });
    const c = document.getElementById('tlContainer');
    if (c) c.scrollTop = 0;
}

// Date filter

let _dpYear = 0, _dpMonth = 0; // currently rendered calendar month

function toggleTlDatePicker() {
    const picker = document.getElementById('tlDatePicker');
    if (!picker) return;
    if (picker.style.display !== 'none') { picker.style.display = 'none'; return; }

    const btn = document.getElementById('tlDateBtn');
    const rect = btn.getBoundingClientRect();

    // Init calendar to selected date or today
    const base = tlDateFilter ? new Date(tlDateFilter + 'T00:00:00') : new Date();
    _dpYear  = base.getFullYear();
    _dpMonth = base.getMonth();
    renderDatePickerCalendar();

    picker.style.display = '';
    // Position below (or above if not enough room)
    const ph = picker.offsetHeight || 290;
    const top = rect.bottom + 6 + ph > window.innerHeight ? rect.top - ph - 6 : rect.bottom + 6;
    picker.style.top  = Math.max(6, top) + 'px';
    picker.style.left = Math.min(rect.left, window.innerWidth - 268) + 'px';

    // Close on outside click
    setTimeout(() => document.addEventListener('click', _closeDpOutside), 0);
}

function _closeDpOutside(e) {
    const picker = document.getElementById('tlDatePicker');
    const btn    = document.getElementById('tlDateBtn');
    if (!picker) return;
    if (!picker.contains(e.target) && e.target !== btn && !btn.contains(e.target)) {
        picker.style.display = 'none';
        document.removeEventListener('click', _closeDpOutside);
    } else {
        // Re-attach for next click
        setTimeout(() => document.addEventListener('click', _closeDpOutside), 0);
    }
}

function renderDatePickerCalendar() {
    const monthNames = ['January','February','March','April','May','June',
                        'July','August','September','October','November','December'];
    const label = document.getElementById('tlDpMonthLabel');
    const grid  = document.getElementById('tlDpDaysGrid');
    if (!label || !grid) return;

    label.textContent = monthNames[_dpMonth] + ' ' + _dpYear;

    const today    = new Date();
    const todayStr = _dpFmt(today.getFullYear(), today.getMonth(), today.getDate());
    const selStr   = tlDateFilter || '';

    const firstDow      = new Date(_dpYear, _dpMonth, 1).getDay();     // 0=Sun
    const daysInMonth   = new Date(_dpYear, _dpMonth + 1, 0).getDate();
    const daysInPrevMo  = new Date(_dpYear, _dpMonth, 0).getDate();

    let html = '';
    // Leading prev-month days
    for (let i = firstDow - 1; i >= 0; i--) {
        const d   = daysInPrevMo - i;
        const ds  = _dpFmt(_dpYear, _dpMonth - 1, d);
        html += `<button class="tl-dp-day other-month${ds === selStr ? ' selected' : ''}" onclick="selectDpDate('${ds}')">${d}</button>`;
    }
    // Current month
    for (let d = 1; d <= daysInMonth; d++) {
        const ds  = _dpFmt(_dpYear, _dpMonth, d);
        const cls = (ds === todayStr ? ' today' : '') + (ds === selStr ? ' selected' : '');
        html += `<button class="tl-dp-day${cls}" onclick="selectDpDate('${ds}')">${d}</button>`;
    }
    // Trailing next-month days
    const used      = firstDow + daysInMonth;
    const remaining = used % 7 === 0 ? 0 : 7 - (used % 7);
    for (let d = 1; d <= remaining; d++) {
        const ds  = _dpFmt(_dpYear, _dpMonth + 1, d);
        html += `<button class="tl-dp-day other-month${ds === selStr ? ' selected' : ''}" onclick="selectDpDate('${ds}')">${d}</button>`;
    }
    grid.innerHTML = html;
}

function _dpFmt(year, month, day) {
    const d = new Date(year, month, day);
    return d.getFullYear() + '-'
        + String(d.getMonth() + 1).padStart(2, '0') + '-'
        + String(d.getDate()).padStart(2, '0');
}

function dpNavMonth(dir) {
    _dpMonth += dir;
    if (_dpMonth < 0)  { _dpMonth = 11; _dpYear--; }
    if (_dpMonth > 11) { _dpMonth = 0;  _dpYear++; }
    renderDatePickerCalendar();
}

function selectDpDate(dateStr) {
    document.getElementById('tlDatePicker').style.display = 'none';
    document.removeEventListener('click', _closeDpOutside);
    applyTlDateFilter(dateStr);
}

function dpSelectToday() {
    const t = new Date();
    selectDpDate(_dpFmt(t.getFullYear(), t.getMonth(), t.getDate()));
}

function dpClear() {
    document.getElementById('tlDatePicker').style.display = 'none';
    document.removeEventListener('click', _closeDpOutside);
    clearTlDateFilter();
}

function applyTlDateFilter(dateStr) {
    if (!dateStr) { clearTlDateFilter(); return; }
    tlDateFilter = dateStr;

    const label = document.getElementById('tlDateLabel');
    const clear = document.getElementById('tlDateClear');
    const btn   = document.getElementById('tlDateBtn');
    if (label) {
        const d = new Date(dateStr + 'T00:00:00');
        label.textContent = d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
        label.style.display = '';
    }
    if (clear) clear.style.display = '';
    if (btn)   btn.classList.add('dp-active');

    // Reset state and reload for current mode
    const activeSearch = (document.getElementById('tlSearchInput')?.value ?? '').trim();
    if (tlMode === 'friends') {
        friendTimelineEvents = [];
        ftlOffset = 0; ftlHasMore = false; ftlLoading = false;
        ftlListPage = 0; ftlRenderedCount = 100;
        _ftlSearchMode = false; _ftlSearchQuery = ''; _ftlSearchDate = '';
        if (activeSearch) { filterFriendTimeline(); return; }
        const c = document.getElementById('tlContainer');
        if (c) c.innerHTML = '<div class="tl-loading"><div class="tl-sk-line"></div><div class="tl-sk-line tl-sk-short"></div><div class="tl-sk-line"></div><div class="tl-sk-line tl-sk-short"></div><div class="tl-sk-line"></div></div>';
        sendToCS({ action: 'getFriendTimelineByDate', date: dateStr, type: ftFilter === 'all' ? '' : ftFilter });
    } else {
        timelineEvents  = [];
        tlOffset        = 0;
        tlHasMore       = false;
        tlLoading       = false;
        tlRenderedCount = 100;
        tlListPage      = 0;
        _tlSearchMode = false; _tlSearchQuery = ''; _tlSearchDate = '';
        if (activeSearch) { filterTimeline(); return; }
        const c = document.getElementById('tlContainer');
        if (c) c.innerHTML = '<div class="tl-loading"><div class="tl-sk-line"></div><div class="tl-sk-line tl-sk-short"></div><div class="tl-sk-line"></div><div class="tl-sk-line tl-sk-short"></div><div class="tl-sk-line"></div></div>';
        sendToCS({ action: 'getTimelineByDate', date: dateStr });
    }
}

function clearTlDateFilter() {
    tlDateFilter = '';
    const label = document.getElementById('tlDateLabel');
    const clear = document.getElementById('tlDateClear');
    const btn   = document.getElementById('tlDateBtn');
    if (label) { label.textContent = ''; label.style.display = 'none'; }
    if (clear) clear.style.display = 'none';
    if (btn)   btn.classList.remove('dp-active');
    refreshTimeline();
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
    const d     = new Date(ev.timestamp);
    const time  = d.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
    const date  = d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
    const meta  = TL_TYPE_META[ev.type] ?? { icon: 'circle', label: ev.type };
    const color = TL_TYPE_COLOR[ev.type] ?? 'var(--tx3)';
    const ei    = jsq(ev.id);

    const meetCount = ev.type === 'meet_again' ? (ev.meetCount || 0) : 0;
    const typeLabel = meetCount > 0 ? `${meta.label} (${meetCount})` : meta.label;
    const header = `<div class="tl-card-header">
        <span class="msi tl-type-icon" style="color:${color}">${meta.icon}</span>
        <span class="tl-type-label">${esc(typeLabel)}</span>
        <div class="tl-time-col"><span class="tl-time">${esc(time)}</span><span class="tl-date">${esc(date)}</span></div>
    </div>`;

    let body = '';
    switch (ev.type) {
        case 'instance_join': body = renderTlJoinBody(ev);      break;
        case 'photo':         body = renderTlPhotoBody(ev);     break;
        case 'first_meet':    body = renderTlMeetBody(ev);      break;
        case 'meet_again':    body = renderTlMeetAgainBody(ev); break;
        case 'notification':  body = renderTlNotifBody(ev);     break;
        case 'avatar_switch': body = renderTlAvatarBody(ev);    break;
        case 'video_url':     body = renderTlUrlBody(ev);       break;
    }

    return `<div class="tl-card" data-tlid="${esc(ev.id)}" onclick="openTlDetail('${ei}')">${header}${body}</div>`;
}

// Card bodies

function renderTlJoinBody(ev) {
    const thumb = ev.worldThumb
        ? `<div class="tl-thumb" style="background-image:url('${cssUrl(ev.worldThumb)}')"></div>`
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
        ? `<div class="tl-thumb tl-thumb-photo" style="background-image:url('${cssUrl(ev.photoUrl)}')"></div>`
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
        ? `<div class="tl-av" style="background-image:url('${cssUrl(ev.userImage)}')"></div>`
        : `<div class="tl-av tl-av-letter">${esc((ev.userName || '?')[0].toUpperCase())}</div>`;
    const sub  = ev.worldName ? `<div class="tl-sub-label">${esc(ev.worldName)}</div>` : '';
    return `<div class="tl-card-body">${av}<div class="tl-card-info"><div class="tl-main-label">${esc(ev.userName || 'Unknown')}</div>${sub}</div></div>`;
}

function renderTlMeetAgainBody(ev) {
    const av  = ev.userImage
        ? `<div class="tl-av" style="background-image:url('${cssUrl(ev.userImage)}')"></div>`
        : `<div class="tl-av tl-av-letter">${esc((ev.userName || '?')[0].toUpperCase())}</div>`;
    const sub = ev.worldName ? `<div class="tl-sub-label">${esc(ev.worldName)}</div>` : '';
    return `<div class="tl-card-body">${av}<div class="tl-card-info"><div class="tl-main-label">${esc(ev.userName || 'Unknown')}</div>${sub}</div></div>`;
}

function renderTlNotifBody(ev) {
    const typeLabel = NOTIF_TYPE_LABELS[ev.notifType] || ev.notifType || 'Notification';
    const av  = ev.senderImage
        ? `<div class="tl-av" style="background-image:url('${cssUrl(ev.senderImage)}')"></div>`
        : `<div class="tl-av tl-av-letter">${esc((ev.senderName || '?')[0].toUpperCase())}</div>`;
    const titleCtx = ev.notifTitle ? `<div class="tl-sub-label" style="color:var(--tx2);">${esc(ev.notifTitle.slice(0, 60))}${ev.notifTitle.length > 60 ? '…' : ''}</div>` : '';
    const sub = ev.message ? `<div class="tl-sub-label">${esc(ev.message.slice(0, 60))}${ev.message.length > 60 ? '…' : ''}</div>` : '';
    return `<div class="tl-card-body">${av}<div class="tl-card-info"><div class="tl-main-label">${esc(ev.senderName || typeLabel)}</div><div class="tl-type-chip">${esc(typeLabel)}</div>${titleCtx}${sub}</div></div>`;
}

// Platform detection for URLs
function _urlPlatform(url) {
    try {
        const h = new URL(url).hostname.replace(/^www\./, '');
        if (h.includes('youtube.com') || h.includes('youtu.be'))  return { name: 'YouTube',    color: '#FF0000', favicon: 'youtube.com'    };
        if (h.includes('soundcloud.com'))                          return { name: 'SoundCloud', color: '#FF5500', favicon: 'soundcloud.com' };
        if (h.includes('twitch.tv'))                               return { name: 'Twitch',     color: '#9146FF', favicon: 'twitch.tv'      };
        if (h.includes('spotify.com'))                             return { name: 'Spotify',    color: '#1DB954', favicon: 'open.spotify.com' };
        if (h.includes('nicovideo.jp'))                            return { name: 'NicoNico',   color: '#E6001F', favicon: 'nicovideo.jp'   };
        if (h.includes('bilibili.com'))                            return { name: 'Bilibili',   color: '#00A1D6', favicon: 'bilibili.com'   };
        if (h.includes('vimeo.com'))                               return { name: 'Vimeo',      color: '#1AB7EA', favicon: 'vimeo.com'      };
        return { name: h, color: '#29B6F6', favicon: h };
    } catch { return { name: 'URL', color: '#29B6F6', favicon: null }; }
}

function _urlFaviconHtml(plat) {
    if (!plat.favicon) return `<span class="msi" style="font-size:22px;color:${plat.color};">link</span>`;
    return `<img src="https://www.google.com/s2/favicons?domain=${encodeURIComponent(plat.favicon)}&sz=64"
        style="width:32px;height:32px;border-radius:6px;object-fit:contain;"
        onerror="this.replaceWith(Object.assign(document.createElement('span'),{className:'msi',textContent:'link',style:'font-size:22px;color:${plat.color}'}))">`;
}

function renderTlUrlBody(ev) {
    const url  = ev.message || '';
    const plat = _urlPlatform(url);
    const icon = `<div class="tl-av" style="display:flex;align-items:center;justify-content:center;background:var(--bg2);">${_urlFaviconHtml(plat)}</div>`;
    const label = plat.name !== new URL(url).hostname.replace(/^www\./,'') ? plat.name : '';
    const sub  = ev.worldName ? `<div class="tl-sub-label">${esc(ev.worldName)}</div>` : '';
    const disp = url.length > 60 ? url.slice(0, 60) + '…' : url;
    return `<div class="tl-card-body">${icon}<div class="tl-card-info"><div class="tl-main-label">${label ? esc(label) : esc(disp)}</div>${label ? `<div class="tl-sub-label" style="word-break:break-all;">${esc(disp)}</div>` : ''}${sub}</div></div>`;
}

function renderTlAvatarBody(ev) {
    const thumb = ev.userImage
        ? `<div class="tl-av" style="background-image:url('${cssUrl(ev.userImage)}')"></div>`
        : `<div class="tl-av tl-av-letter"><span class="msi" style="font-size:18px;">checkroom</span></div>`;
    return `<div class="tl-card-body">${thumb}<div class="tl-card-info"><div class="tl-main-label">${esc(ev.userName || 'Unknown Avatar')}</div></div></div>`;
}

function tlPlayerAvatars(players, max) {
    return (players || []).slice(0, max).map(p => {
        return p.image
            ? `<div class="tl-player-av" style="background-image:url('${cssUrl(p.image)}')" title="${esc(p.displayName)}"></div>`
            : `<div class="tl-player-av tl-player-av-letter" title="${esc(p.displayName)}">${esc((p.displayName || '?')[0].toUpperCase())}</div>`;
    }).join('');
}

// Detail modals (reuses #modalDetail / #detailModalContent)

function copyInstanceLink(location) {
    if (!location) return;
    const colon = location.indexOf(':');
    if (colon <= 0) return;
    const worldId    = location.slice(0, colon);
    const instanceId = location.slice(colon + 1);
    if (!worldId.startsWith('wrld_')) return;
    const url = `https://vrchat.com/home/launch?worldId=${encodeURIComponent(worldId)}&instanceId=${encodeURIComponent(instanceId)}`;
    navigator.clipboard.writeText(url)
        .then(() => showToast(true, 'Instance link copied!'))
        .catch(() => showToast(false, 'Failed to copy'));
}

function _instanceLinkBtn(location, closeJs) {
    if (!location || location.indexOf(':') <= 0 || !location.startsWith('wrld_')) return '';
    return `<button class="vrcn-button-round" onclick="${closeJs ? closeJs + ';' : ''}copyInstanceLink('${jsq(location)}')"><span class="msi" style="font-size:14px;">content_copy</span> Copy Instance Link</button>`;
}

function openTlDetail(id) {
    const ev = timelineEvents.find(e => e.id === id)
             || _tlSearchEvents.find(e => e.id === id);
    if (!ev) return;
    const el = document.getElementById('detailModalContent');
    if (!el) return;

    switch (ev.type) {
        case 'instance_join': renderTlDetailJoin(ev, el);      break;
        case 'photo':         renderTlDetailPhoto(ev, el);     break;
        case 'first_meet':    renderTlDetailMeet(ev, el);      break;
        case 'meet_again':    renderTlDetailMeetAgain(ev, el); break;
        case 'notification':  renderTlDetailNotif(ev, el);     break;
        case 'avatar_switch': renderTlDetailAvatar(ev, el);    break;
        case 'video_url':     renderTlDetailUrl(ev, el);       break;
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
    document.querySelectorAll('#tlPersonalFilters .sub-tab-btn').forEach(b => b.classList.remove('active'));
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
            const onclick = p.userId ? `document.getElementById('modalDetail').style.display='none';openFriendDetail('${jsq(p.userId)}')` : '';
            playersHtml += renderProfileItemSmall({ id: p.userId, displayName: p.displayName, image: p.image }, onclick);
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
        <div style="margin-top:14px;display:flex;gap:8px;justify-content:flex-end;">
            ${_instanceLinkBtn(ev.location, '')}
            <button class="vrcn-button-round" onclick="document.getElementById('modalDetail').style.display='none'">Close</button>
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
            const onclick = p.userId ? `document.getElementById('modalDetail').style.display='none';openFriendDetail('${jsq(p.userId)}')` : '';
            playersHtml += renderProfileItemSmall({ id: p.userId, displayName: p.displayName, image: p.image }, onclick);
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
            ${ev.photoUrl ? `<button class="vrcn-button-round vrcn-btn-join" onclick="openLightbox('${photoJs}','image')"><span class="msi" style="font-size:14px;">open_in_full</span> Full Size</button>` : ''}
            <button class="vrcn-button-round" onclick="document.getElementById('modalDetail').style.display='none'">Close</button>
        </div>
    </div>`;
}

// Detail: first meet

function renderTlDetailMeet(ev, el) {
    const d       = new Date(ev.timestamp);
    const dateStr = d.toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' });
    const timeStr = d.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
    const av      = ev.userImage
        ? `<div class="tl-detail-av" style="background-image:url('${cssUrl(ev.userImage)}')"></div>`
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
            ${ev.userId ? `<button class="vrcn-button-round vrcn-btn-join" onclick="document.getElementById('modalDetail').style.display='none';openFriendDetail('${esc(ev.userId)}')">View Profile</button>` : ''}
            <button class="vrcn-button-round" onclick="document.getElementById('modalDetail').style.display='none'">Close</button>
        </div>
    </div>`;
}

// Detail: meet again

function renderTlDetailMeetAgain(ev, el) {
    const d       = new Date(ev.timestamp);
    const dateStr = d.toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' });
    const timeStr = d.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
    const av      = ev.userImage
        ? `<div class="tl-detail-av" style="background-image:url('${cssUrl(ev.userImage)}')"></div>`
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
            ${ev.userId ? `<button class="vrcn-button-round vrcn-btn-join" onclick="document.getElementById('modalDetail').style.display='none';openFriendDetail('${esc(ev.userId)}')">View Profile</button>` : ''}
            <button class="vrcn-button-round" onclick="document.getElementById('modalDetail').style.display='none'">Close</button>
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
        ? `<div class="tl-detail-av" style="background-image:url('${cssUrl(ev.senderImage)}')"></div>`
        : `<div class="tl-detail-av tl-detail-av-letter">${esc((ev.senderName || '?')[0].toUpperCase())}</div>`;

    el.innerHTML = `<div class="fd-content" style="padding:20px;">
        <div style="display:flex;gap:16px;align-items:center;margin-bottom:20px;">
            ${av}
            <div>
                <h2 style="margin:0 0 4px;color:var(--tx0);font-size:18px;">${esc(ev.senderName || typeLabel)}</h2>
                <div style="font-size:11px;color:var(--warn);font-weight:700;letter-spacing:.05em;">${esc(typeLabel.toUpperCase())}</div>
            </div>
        </div>
        <div class="fd-meta">
            <div class="fd-meta-row"><span class="fd-meta-label">Date</span><span>${esc(dateStr)}</span></div>
            <div class="fd-meta-row"><span class="fd-meta-label">Time</span><span>${esc(timeStr)}</span></div>
            <div class="fd-meta-row"><span class="fd-meta-label">Type</span><span>${esc(typeLabel)}</span></div>
            ${ev.notifTitle ? `<div class="fd-meta-row"><span class="fd-meta-label">Context</span><span>${esc(ev.notifTitle)}</span></div>` : ''}
            ${ev.message ? `<div class="fd-meta-row"><span class="fd-meta-label">Message</span><span>${esc(ev.message)}</span></div>` : ''}
        </div>
        <div style="margin-top:14px;display:flex;gap:8px;justify-content:flex-end;">
            ${ev.senderId ? `<button class="vrcn-button-round vrcn-btn-join" onclick="document.getElementById('modalDetail').style.display='none';openFriendDetail('${esc(ev.senderId)}')">View Profile</button>` : ''}
            <button class="vrcn-button-round" onclick="document.getElementById('modalDetail').style.display='none'">Close</button>
        </div>
    </div>`;
}

// Detail: avatar switch

function renderTlDetailAvatar(ev, el) {
    const d       = new Date(ev.timestamp);
    const dateStr = d.toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' });
    const timeStr = d.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
    const banner  = ev.userImage
        ? `<div class="fd-banner"><img src="${ev.userImage}" onerror="this.parentElement.style.display='none'"><div class="fd-banner-fade"></div></div>`
        : '';
    const openBtn = ev.userId
        ? `<button class="vrcn-button-round vrcn-btn-join" onclick="document.getElementById('modalDetail').style.display='none';openAvatarDetail('${jsq(ev.userId)}')">View Avatar</button>`
        : '';
    el.innerHTML = `${banner}<div class="fd-content${banner ? ' fd-has-banner' : ''}" style="padding:20px;">
        <h2 style="margin:0 0 12px;color:var(--tx0);font-size:16px;">${esc(ev.userName || 'Unknown Avatar')}</h2>
        <div class="fd-meta">
            <div class="fd-meta-row"><span class="fd-meta-label">Avatar</span><span>${esc(ev.userName || 'Unknown')}</span></div>
            <div class="fd-meta-row"><span class="fd-meta-label">Date</span><span>${esc(dateStr)}</span></div>
            <div class="fd-meta-row"><span class="fd-meta-label">Time</span><span>${esc(timeStr)}</span></div>
            ${ev.userId ? `<div class="fd-meta-row"><span class="fd-meta-label">Avatar ID</span><span style="font-size:11px;color:var(--tx3);">${esc(ev.userId)}</span></div>` : ''}
        </div>
        <div style="margin-top:14px;display:flex;gap:8px;justify-content:flex-end;">
            ${openBtn}
            <button class="vrcn-button-round" onclick="document.getElementById('modalDetail').style.display='none'">Close</button>
        </div>
    </div>`;
}

// Detail: video URL

function renderTlDetailUrl(ev, el) {
    const d       = new Date(ev.timestamp);
    const dateStr = d.toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' });
    const timeStr = d.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
    const url     = ev.message || '';
    const plat    = _urlPlatform(url);
    const favicon = `<div style="display:flex;align-items:center;justify-content:center;width:64px;height:64px;border-radius:12px;background:var(--bg2);">${_urlFaviconHtml(plat)}</div>`;
    const worldClick = ev.worldId
        ? ` style="cursor:pointer;" onclick="document.getElementById('modalDetail').style.display='none';openWorldSearchDetail('${esc(ev.worldId)}')"` : '';

    el.innerHTML = `<div class="fd-content" style="padding:20px;">
        <div style="display:flex;gap:16px;align-items:center;margin-bottom:20px;">
            ${favicon}
            <div>
                <h2 style="margin:0 0 4px;color:var(--tx0);font-size:18px;">${esc(plat.name)}</h2>
                <div style="font-size:11px;color:${plat.color};font-weight:700;letter-spacing:.05em;">VIDEO URL</div>
            </div>
        </div>
        <div class="fd-meta">
            <div class="fd-meta-row"><span class="fd-meta-label">Date</span><span>${esc(dateStr)}</span></div>
            <div class="fd-meta-row"><span class="fd-meta-label">Time</span><span>${esc(timeStr)}</span></div>
            ${ev.worldName ? `<div class="fd-meta-row"${worldClick}><span class="fd-meta-label">World</span><span style="color:var(--accent-lt);">${esc(ev.worldName)}</span></div>` : ''}
            <div class="fd-meta-row" style="align-items:flex-start;"><span class="fd-meta-label">URL</span><span style="word-break:break-all;font-size:11px;color:var(--tx2);">${esc(url)}</span></div>
        </div>
        <div style="margin-top:14px;display:flex;gap:8px;justify-content:flex-end;">
            <button class="vrcn-button-round vrcn-btn-join" onclick="sendToCS({action:'openUrl',url:'${jsq(url)}'})">Open URL</button>
            <button class="vrcn-button-round" onclick="navigator.clipboard.writeText('${jsq(url)}').then(()=>showToast(true,'Copied!'))">Copy</button>
            <button class="vrcn-button-round" onclick="document.getElementById('modalDetail').style.display='none'">Close</button>
        </div>
    </div>`;
}

// === Friends Timeline ===

const FT_FILTER_IDS = {
    all:               'ftFAll',
    friend_gps:        'ftFGps',
    friend_status:     'ftFStatus',
    friend_statusdesc: 'ftFStatusDesc',
    friend_online:     'ftFOnline',
    friend_offline:    'ftFOffline',
    friend_bio:        'ftFBio',
};

const FT_TYPE_COLOR = {
    friend_gps:        'var(--accent)',
    friend_status:     'var(--cyan)',
    friend_statusdesc: 'var(--cyan)',
    friend_online:     'var(--ok)',
    friend_offline:    'var(--tx3)',
    friend_bio:        '#AB47BC',
};

const FT_TYPE_META = {
    friend_gps:        { icon: 'location_on',       label: 'Location'    },
    friend_status:     { icon: 'circle',             label: 'Status'      },
    friend_statusdesc: { icon: 'chat_bubble_outline', label: 'Status Text' },
    friend_online:     { icon: 'login',              label: 'Online'      },
    friend_offline:    { icon: 'power_settings_new', label: 'Offline'     },
    friend_bio:        { icon: 'edit_note',          label: 'Bio Change'  },
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
    friendTimelineEvents = [];
    ftlOffset        = 0;
    ftlHasMore       = false;
    ftlLoading       = false;
    ftlRenderedCount = 100;
    ftlListPage      = 0;
    ftlTotal         = 0;
    const activeSearch = (document.getElementById('tlSearchInput')?.value ?? '').trim();
    const c = document.getElementById('tlContainer');
    if (c && !(_ftlSearchMode && activeSearch)) {
        c.innerHTML = '<div class="tl-loading"><div class="tl-sk-line"></div><div class="tl-sk-line tl-sk-short"></div><div class="tl-sk-line"></div><div class="tl-sk-line tl-sk-short"></div><div class="tl-sk-line"></div></div>';
    }
    if (tlDateFilter) sendToCS({ action: 'getFriendTimelineByDate', date: tlDateFilter, type: ftFilter === 'all' ? '' : ftFilter });
    else              sendToCS({ action: 'getFriendTimeline', type: ftFilter === 'all' ? '' : ftFilter });
}

function renderFriendTimeline(payload) {
    const events  = Array.isArray(payload) ? payload : (payload?.events  ?? []);
    const hasMore = Array.isArray(payload) ? false   : (payload?.hasMore ?? false);
    const offset  = Array.isArray(payload) ? 0       : (payload?.offset  ?? 0);
    const total   = Array.isArray(payload) ? 0       : (payload?.total   ?? 0);

    // Discard stale response if filter was switched while this request was in-flight
    if (!Array.isArray(payload) && payload?.type !== undefined) {
        const expectedType = ftFilter === 'all' ? '' : ftFilter;
        if (payload.type !== expectedType) return;
    }

    if (total > 0) ftlTotal = total;

    if (_ftlPendingListPage !== null) {
        // Direct page navigation: replace current events with this page's data
        friendTimelineEvents  = events;
        ftlListPage           = _ftlPendingListPage;
        ftlRenderedCount      = events.length;
        _ftlPendingListPage   = null;
    } else if (offset === 0) {
        friendTimelineEvents = events;
        ftlRenderedCount = 100;
        ftlListPage      = 0;
    } else {
        // Load More append (timeline/card view)
        friendTimelineEvents = friendTimelineEvents.concat(events);
        ftlRenderedCount    += events.length;
    }
    ftlOffset  = offset + events.length;
    ftlHasMore = hasMore;
    ftlLoading = false;
    filterFriendTimeline();
}

function handleFriendTimelineEvent(ev) {
    if (!ev || !ev.id) return;
    // If a type filter is active, only inject events that match it to avoid polluting the view
    if (typeof ftFilter !== 'undefined' && ftFilter !== 'all' && ev.type !== ftFilter) return;
    const idx = friendTimelineEvents.findIndex(e => e.id === ev.id);
    if (idx >= 0) friendTimelineEvents[idx] = ev;
    else friendTimelineEvents.unshift(ev);
    friendTimelineEvents.sort((a, b) => b.timestamp.localeCompare(a.timestamp));
    if (tlMode === 'friends') filterFriendTimeline();
}

function setFtFilter(f) {
    ftFilter = f;
    document.querySelectorAll('#tlFriendsFilters .sub-tab-btn').forEach(b => b.classList.remove('active'));
    const btn = document.getElementById(FT_FILTER_IDS[f]);
    if (btn) btn.classList.add('active');
    // Reset pagination and reload from server with new type filter
    friendTimelineEvents = [];
    ftlOffset        = 0;
    ftlHasMore       = false;
    ftlLoading       = false;
    ftlRenderedCount = 100;
    ftlListPage      = 0;
    ftlTotal         = 0;
    _ftlSearchMode = false; _ftlSearchQuery = ''; _ftlSearchDate = '';
    const activeSearch = (document.getElementById('tlSearchInput')?.value ?? '').trim();
    if (activeSearch) { filterFriendTimeline(); return; }
    const c = document.getElementById('tlContainer');
    if (c) c.innerHTML = '<div class="tl-loading"><div class="tl-sk-line"></div><div class="tl-sk-line tl-sk-short"></div><div class="tl-sk-line"></div><div class="tl-sk-line tl-sk-short"></div><div class="tl-sk-line"></div></div>';
    if (tlDateFilter) sendToCS({ action: 'getFriendTimelineByDate', date: tlDateFilter, type: f === 'all' ? '' : f });
    else              sendToCS({ action: 'getFriendTimeline', type: f === 'all' ? '' : f });
}

function filterFriendTimeline() {
    const search = (document.getElementById('tlSearchInput')?.value ?? '').toLowerCase().trim();
    const c = document.getElementById('tlContainer');
    if (!c) return;

    if (search) {
        if (_ftlSearchMode && search === _ftlSearchQuery && tlDateFilter === _ftlSearchDate) {
            _renderFtlSearchResults(search);
            return;
        }
        _ftlSearchMode  = false;
        _ftlSearchQuery = '';
        _ftlSearchDate  = '';
        ftlListPage = 0; ftlRenderedCount = 100;
        c.innerHTML = '<div class="tl-loading"><div class="tl-sk-line"></div><div class="tl-sk-line tl-sk-short"></div><div class="tl-sk-line"></div></div>';
        _setTlPaginator('');
        clearTimeout(_ftlSearchTimer);
        _ftlSearchTimer = setTimeout(() => {
            sendToCS({ action: 'searchFriendTimeline', query: search, date: tlDateFilter, offset: 0, type: ftFilter === 'all' ? '' : ftFilter });
        }, 300);
        return;
    }

    // No search – clear search mode and show paginated events
    _ftlSearchMode   = false;
    _ftlSearchEvents = [];

    if (!friendTimelineEvents.length && !ftlLoading) {
        // Events cleared (e.g. filter switched while searching) — reload from server
        refreshFriendTimeline();
        return;
    }

    const prevScrollTop = c.scrollTop;

    // Both views use server-side pagination — friendTimelineEvents holds current page only
    const totalPages = ftlTotal > 0
        ? Math.ceil(ftlTotal / 100)
        : Math.max(ftlListPage + 1, 1) + (ftlHasMore ? 1 : 0);

    const contentHtml = tlViewMode === 'list'
        ? buildFriendListHtml(friendTimelineEvents)
        : buildFriendTimelineHtml(friendTimelineEvents);
    c.innerHTML = contentHtml;
    _setTlPaginator(buildFtlPagination(ftlListPage, totalPages, ftlHasMore));

    if (prevScrollTop > 0) c.scrollTop = prevScrollTop;
}

function buildFtlPagination(page, totalPages, hasMore) {
    if (totalPages <= 1 && !hasMore) return '';
    const delta = 3;
    const range = [];
    for (let i = 0; i < totalPages; i++) {
        if (i === 0 || i === totalPages - 1 || (i >= page - delta && i <= page + delta))
            range.push(i);
    }
    let btns = '';
    let prev = -1;
    range.forEach(i => {
        if (prev !== -1 && i > prev + 1) btns += `<span style="padding:0 4px;color:var(--tx3);">…</span>`;
        const active = i === page ? 'style="background:var(--accent);color:#fff;"' : '';
        btns += `<button class="vrcn-button" ${active} onclick="ftlGoPage(${i})">${i + 1}</button>`;
        prev = i;
    });
    const prevDis = page === 0 ? 'disabled' : '';
    const nextDis = (page >= totalPages - 1 && !hasMore) ? 'disabled' : '';
    const countInfo = ftlTotal > 0 ? `<span style="font-size:11px;color:var(--tx3);padding:0 8px;">${ftlTotal.toLocaleString()} total</span>` : '';
    return `<button class="vrcn-button" ${prevDis} onclick="ftlGoPage(${page - 1})"><span class="msi" style="font-size:16px;">chevron_left</span></button>
        ${btns}
        <button class="vrcn-button" ${nextDis} onclick="ftlGoPage(${page + 1})"><span class="msi" style="font-size:16px;">chevron_right</span></button>
        ${countInfo}`;
}

function _renderFtlSearchResults(search) {
    const c = document.getElementById('tlContainer');
    if (!c) return;

    const events = _ftlSearchEvents;

    if (!events.length) {
        c.innerHTML = `<div class="empty-msg">No results for "<b>${esc(search)}</b>".</div>`;
        _setTlPaginator('');
        return;
    }

    const total      = _ftlSearchTotal;
    const totalPages = total > 0 ? Math.ceil(total / 100) : 1;
    const banner = `<div style="padding:6px 12px;font-size:11px;color:var(--tx3);border-bottom:1px solid var(--brd);">`
        + `${total.toLocaleString()} result${total !== 1 ? 's' : ''} for "<b>${esc(search)}</b>"</div>`;
    let html = banner + (tlViewMode === 'list' ? buildFriendListHtml(events) : buildFriendTimelineHtml(events));
    c.innerHTML = html;
    _setTlPaginator(buildSearchPagination(_ftlSearchPage, totalPages, 'ftlGoSearchPage'));
}

function handleFtlSearchResults(payload) {
    const q = (payload.query || '').toLowerCase().trim();
    const currentSearch = (document.getElementById('tlSearchInput')?.value ?? '').toLowerCase().trim();
    if (q !== currentSearch) return;
    if ((payload.date || '') !== tlDateFilter) return;
    const offset = payload.offset ?? 0;
    // Always replace — each page nav fetches the exact page, no appending
    _ftlSearchEvents = payload.events || [];
    _ftlSearchTotal  = payload.total ?? 0;
    _ftlSearchPage   = Math.floor(offset / 100);
    _ftlSearchMode   = true;
    _ftlSearchQuery  = q;
    _ftlSearchDate   = payload.date || '';
    filterFriendTimeline();
}

function ftlGoSearchPage(page) {
    if (page < 0) return;
    sendToCS({ action: 'searchFriendTimeline', query: _ftlSearchQuery, date: _ftlSearchDate, offset: page * 100, type: ftFilter === 'all' ? '' : ftFilter });
}

function ftSearchable(e) {
    return [e.friendName, e.worldName, e.newValue, e.oldValue, e.location]
        .filter(Boolean).join(' ').toLowerCase();
}

// Friends Timeline pagination helpers

function loadMoreFriendTimeline() {
    if (ftlLoading) return;
    // Drain already-loaded pool first (timeline/card view)
    if (friendTimelineEvents.length > ftlRenderedCount) {
        ftlRenderedCount += 100;
        filterFriendTimeline();
        return;
    }
    if (!ftlHasMore) return;
    ftlLoading = true;
    const btn = document.getElementById('ftlLoadMoreBtn');
    if (btn) { btn.disabled = true; btn.innerHTML = '<span class="msi" style="font-size:16px;">hourglass_empty</span> Loading…'; }
    sendToCS({ action: 'getFriendTimelinePage', offset: ftlOffset, type: ftFilter === 'all' ? '' : ftFilter });
}

function ftlGoPage(page) {
    if (page < 0) return;
    const totalPages = ftlTotal > 0 ? Math.ceil(ftlTotal / 100) : null;
    if (totalPages !== null && page >= totalPages) return;
    if (page === ftlListPage && _ftlPendingListPage === null && !ftlLoading) {
        // Already on this page — just scroll top
        const c = document.getElementById('tlContainer');
        if (c) c.scrollTop = 0;
        return;
    }
    // Fetch this page directly from DB at absolute offset
    _ftlPendingListPage = page;
    ftlLoading = true;
    sendToCS({ action: 'getFriendTimelinePage', offset: page * 100, type: ftFilter === 'all' ? '' : ftFilter });
    const c = document.getElementById('tlContainer');
    if (c) c.scrollTop = 0;
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
    const d     = new Date(ev.timestamp);
    const time  = d.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
    const date  = d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
    const meta  = FT_TYPE_META[ev.type] ?? { icon: 'circle', label: ev.type };
    const color = FT_TYPE_COLOR[ev.type] ?? 'var(--tx3)';
    const ei    = jsq(ev.id);

    const header = `<div class="tl-card-header">
        <span class="msi tl-type-icon" style="color:${color}">${meta.icon}</span>
        <span class="tl-type-label">${esc(meta.label)}</span>
        <div class="tl-time-col"><span class="tl-time">${esc(time)}</span><span class="tl-date">${esc(date)}</span></div>
    </div>`;

    let body = '';
    switch (ev.type) {
        case 'friend_gps':        body = renderFtGpsBody(ev);        break;
        case 'friend_status':     body = renderFtStatusBody(ev);     break;
        case 'friend_statusdesc': body = renderFtStatusDescBody(ev); break;
        case 'friend_online':     body = renderFtOnlineBody(ev);     break;
        case 'friend_offline':    body = renderFtOfflineBody(ev);    break;
        case 'friend_bio':        body = renderFtBioBody(ev);        break;
    }

    const clickAction = ev.type === 'friend_gps'
        ? `openFtGpsDetail('${ei}')`
        : `openFtDetail('${ei}')`;

    return `<div class="tl-card" data-ftid="${esc(ev.id)}" onclick="${clickAction}">${header}${body}</div>`;
}

// Card bodies

function ftFriendAv(ev, cssClass) {
    return ev.friendImage
        ? `<div class="${cssClass}" style="background-image:url('${cssUrl(ev.friendImage)}')"></div>`
        : `<div class="${cssClass} tl-av-letter">${esc((ev.friendName || '?')[0].toUpperCase())}</div>`;
}

function renderFtGpsBody(ev) {
    const thumb = ev.worldThumb
        ? `<div class="tl-thumb" style="background-image:url('${cssUrl(ev.worldThumb)}')"></div>`
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

function renderFtStatusDescBody(ev) {
    const av      = ftFriendAv(ev, 'tl-av');
    const preview = (ev.newValue || '').slice(0, 60);
    const ellipsis = (ev.newValue || '').length > 60 ? '...' : '';
    return `<div class="tl-card-body">${av}<div class="tl-card-info">
        <div class="tl-main-label">${esc(ev.friendName || 'Unknown')}</div>
        <div class="tl-sub-label">${esc(preview)}${ellipsis}</div>
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

/* === Friend GPS Instance Log Modal === */
function openFtGpsDetail(evId) {
    const ev = friendTimelineEvents.find(e => e.id === evId)
             || _ftlSearchEvents.find(e => e.id === evId);
    if (!ev) return;
    renderFtGpsDetailModal(ev);
    document.getElementById('modalFtGpsDetail').style.display = 'flex';
}

function closeFtGpsDetail() {
    document.getElementById('modalFtGpsDetail').style.display = 'none';
}

function switchFtGpsTab(tab) {
    document.getElementById('ftGpsTabInfo').style.display = tab === 'info' ? '' : 'none';
    document.getElementById('ftGpsTabAlso').style.display = tab === 'also' ? '' : 'none';
    document.querySelectorAll('#ftGpsDetailContent .ftgps-tab-btn').forEach(b => b.classList.toggle('active', b.dataset.tab === tab));
}

function renderFtGpsDetailModal(ev) {
    const loc = ev.location || '';
    const { instanceType } = parseFriendLocation(loc);
    const { cls: instCls, label: instLabel } = getInstanceBadge(instanceType);

    const instIdMatch = loc.match(/:(\d+)/);
    const instanceId = instIdMatch ? instIdMatch[1] : '';

    const { dateStr, timeStr } = ftDetailDatetime(ev);

    const banner = ev.worldThumb
        ? `<div class="fd-banner"><img src="${ev.worldThumb}" onerror="this.parentElement.style.display='none'"><div class="fd-banner-fade"></div></div>`
        : '';
    const worldName = ev.worldName || ev.worldId || 'Unknown World';

    // Was Also Here: populated async from server (covers all pages, not just loaded memory)
    const alsoList = [];

    const infoHtml = `<div class="fd-meta">
        <div class="fd-meta-row"><span class="fd-meta-label">Date</span><span>${esc(dateStr)}</span></div>
        <div class="fd-meta-row"><span class="fd-meta-label">Time</span><span>${esc(timeStr)}</span></div>
        <div class="fd-meta-row"><span class="fd-meta-label">Instance Type</span><span class="vrcn-badge ${instCls}">${instLabel}</span></div>
        ${instanceId ? `<div class="fd-meta-row"><span class="fd-meta-label">Instance ID</span><span style="font-family:monospace;font-size:12px;color:var(--tx2);">#${esc(instanceId)}</span></div>` : ''}
        <div class="fd-meta-row"><span class="fd-meta-label">Event</span><span style="color:var(--tx2);">${esc(ev.friendName || 'Unknown')} joined this world</span></div>
    </div>`;

    const el = document.getElementById('ftGpsDetailContent');
    el.innerHTML = `${banner}<div class="fd-content${banner ? ' fd-has-banner' : ''}" style="padding:16px;">
        <h2 style="margin:0 0 4px;color:var(--tx0);font-size:18px;">${esc(worldName)}</h2>
        <div style="margin-bottom:12px;">${idBadge(ev.worldId || '')}</div>
        <div class="fd-tabs" style="margin-bottom:14px;">
            <button class="fd-tab active ftgps-tab-btn" data-tab="info" onclick="switchFtGpsTab('info')">Info</button>
            <button class="fd-tab ftgps-tab-btn" data-tab="also" id="ftGpsAlsoTab" onclick="switchFtGpsTab('also')">Was also here</button>
        </div>
        <div id="ftGpsTabInfo">${infoHtml}</div>
        <div id="ftGpsTabAlso" style="display:none;"><div style="font-size:12px;color:var(--tx3);padding:12px 0;">Loading...</div></div>
        <div style="margin-top:14px;display:flex;gap:8px;justify-content:flex-end;">
            ${_instanceLinkBtn(loc, '')}
            ${ev.worldId ? `<button class="vrcn-button-round vrcn-btn-join" onclick="closeFtGpsDetail();openWorldSearchDetail('${esc(ev.worldId)}')"><span class="msi" style="font-size:14px;">travel_explore</span> Open World</button>` : ''}
            <button class="vrcn-button-round" onclick="closeFtGpsDetail()">Close</button>
        </div>
    </div>`;

    // Async: ask server for all friends at this location (searches full DB, not just loaded page)
    sendToCS({ action: 'getFtAlsoWasHere', location: loc, excludeId: ev.id });
}

function renderFtAlsoWasHereResult(payload) {
    const tab = document.getElementById('ftGpsTabAlso');
    const tabBtn = document.getElementById('ftGpsAlsoTab');
    if (!tab) return;
    const friends = payload?.friends ?? [];
    if (friends.length === 0) {
        tab.innerHTML = '<div style="font-size:12px;color:var(--tx3);padding:12px 0;">No other friends tracked in this instance.</div>';
    } else {
        tab.innerHTML = friends.map(f =>
            renderProfileItemSmall(
                { id: f.friendId, displayName: f.friendName || 'Unknown', image: f.friendImage },
                `closeFtGpsDetail();openFriendDetail('${jsq(f.friendId)}')`
            )
        ).join('');
    }
    if (tabBtn && friends.length > 0) tabBtn.textContent = `Was also here (${friends.length})`;
}

function openFtDetail(id) {
    const ev = friendTimelineEvents.find(e => e.id === id)
             || _ftlSearchEvents.find(e => e.id === id);
    if (!ev) return;
    const el = document.getElementById('detailModalContent');
    if (!el) return;

    switch (ev.type) {
        case 'friend_gps':        renderFtDetailGps(ev, el);        break;
        case 'friend_status':     renderFtDetailStatus(ev, el);     break;
        case 'friend_statusdesc': renderFtDetailStatusDesc(ev, el); break;
        case 'friend_online':     renderFtDetailOnline(ev, el);     break;
        case 'friend_offline':    renderFtDetailOffline(ev, el);    break;
        case 'friend_bio':        renderFtDetailBio(ev, el);        break;
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
        ? `<div class="tl-detail-av" style="background-image:url('${cssUrl(ev.friendImage)}')"></div>`
        : `<div class="tl-detail-av tl-detail-av-letter">${esc((ev.friendName || '?')[0].toUpperCase())}</div>`;
    return `<div style="display:flex;gap:16px;align-items:center;margin-bottom:20px;">${av}
        <div><h2 style="margin:0 0 4px;color:var(--tx0);font-size:18px;">${esc(ev.friendName || 'Unknown')}</h2>
        ${ev.friendId ? `<div style="font-size:10px;color:var(--tx3);">${esc(ev.friendId)}</div>` : ''}
        </div></div>`;
}

function ftDetailClose() {
    return `<button class="vrcn-button-round" onclick="document.getElementById('modalDetail').style.display='none'">Close</button>`;
}

function ftDetailViewProfile(ev) {
    return ev.friendId
        ? `<button class="vrcn-button-round vrcn-btn-join" onclick="document.getElementById('modalDetail').style.display='none';openFriendDetail('${esc(ev.friendId)}')">View Profile</button>`
        : '';
}

function renderFtDetailGps(ev, el) {
    const { dateStr, timeStr } = ftDetailDatetime(ev);
    const banner = ev.worldThumb
        ? `<div class="fd-banner"><img src="${ev.worldThumb}" onerror="this.parentElement.style.display='none'"><div class="fd-banner-fade"></div></div>`
        : '';
    const wname = ev.worldName || ev.worldId || 'Unknown World';
    const worldClick = ev.worldId
        ? ` style="cursor:pointer;" onclick="document.getElementById('modalDetail').style.display='none';openWorldDetail('${esc(ev.worldId)}')"` : '';

    el.innerHTML = `${banner}<div class="fd-content${banner ? ' fd-has-banner' : ''}" style="padding:20px;">
        ${ftDetailAvRow(ev)}
        <div class="fd-meta">
            <div class="fd-meta-row"><span class="fd-meta-label">Date</span><span>${esc(dateStr)}</span></div>
            <div class="fd-meta-row"><span class="fd-meta-label">Time</span><span>${esc(timeStr)}</span></div>
            <div class="fd-meta-row"${worldClick}><span class="fd-meta-label">World</span><span style="color:var(--accent-lt);">${esc(wname)}</span></div>
        </div>
        <div style="margin-top:14px;display:flex;gap:8px;justify-content:flex-end;">
            ${ev.worldId ? `<button class="vrcn-button-round vrcn-btn-join" onclick="document.getElementById('modalDetail').style.display='none';openWorldDetail('${esc(ev.worldId)}')"><span class="msi" style="font-size:14px;">travel_explore</span> Open World</button>` : ''}
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

function renderFtDetailStatusDesc(ev, el) {
    const { dateStr, timeStr } = ftDetailDatetime(ev);
    el.innerHTML = `<div class="fd-content" style="padding:20px;">
        ${ftDetailAvRow(ev)}
        <div class="fd-meta">
            <div class="fd-meta-row"><span class="fd-meta-label">Date</span><span>${esc(dateStr)}</span></div>
            <div class="fd-meta-row"><span class="fd-meta-label">Time</span><span>${esc(timeStr)}</span></div>
        </div>
        ${ev.oldValue ? `<div style="margin-top:12px;"><div style="font-size:10px;color:var(--tx3);margin-bottom:4px;">PREVIOUS STATUS TEXT</div>
            <div style="font-size:12px;color:var(--tx2);background:var(--bg2);padding:8px 10px;border-radius:6px;">${esc(ev.oldValue)}</div></div>` : ''}
        ${ev.newValue !== undefined ? `<div style="margin-top:10px;"><div style="font-size:10px;color:var(--tx3);margin-bottom:4px;">NEW STATUS TEXT</div>
            <div style="font-size:12px;color:var(--tx1);background:var(--bg2);padding:8px 10px;border-radius:6px;">${esc(ev.newValue) || '<em style="color:var(--tx3)">cleared</em>'}</div></div>` : ''}
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

// ═══════════════════════════════════════════════════════════════════
// List View — Personal Timeline
// ═══════════════════════════════════════════════════════════════════

function buildPersonalListHtml(events) {
    if (!events.length) return '<div class="empty-msg">No timeline events match your filter.</div>';

    let rows = '';
    events.forEach(ev => {
        const d     = new Date(ev.timestamp);
        const dt    = d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })
                    + ' · ' + d.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
        const meta  = TL_TYPE_META[ev.type] ?? { icon: 'circle', label: ev.type };
        const color = TL_TYPE_COLOR[ev.type] ?? 'var(--tx3)';
        const ei    = jsq(ev.id);
        const { userHtml, detail } = _tlListData(ev);
        const listMeetCount = ev.type === 'meet_again' ? (ev.meetCount || 0) : 0;
        const listTypeLabel = listMeetCount > 0 ? `${meta.label} (${listMeetCount})` : meta.label;

        rows += `<tr class="tl-list-row" onclick="openTlDetail('${ei}')">
            <td class="tl-list-dt">${esc(dt)}</td>
            <td class="tl-list-type"><span class="msi tl-list-icon" style="color:${color}">${meta.icon}</span><span>${esc(listTypeLabel)}</span></td>
            <td class="tl-list-user">${userHtml || '<span class="tl-list-na">—</span>'}</td>
            <td class="tl-list-detail">${detail || '<span class="tl-list-na">—</span>'}</td>
        </tr>`;
    });

    return `<div class="tl-list-wrap">
        <table class="tl-list-table">
            <thead><tr>
                <th>Date / Time</th><th>Type</th><th>User</th><th>Detail</th>
            </tr></thead>
            <tbody>${rows}</tbody>
        </table>
    </div>`;
}

function _tlListPlayerAvatars(players, max) {
    if (!players || !players.length) return '';
    const shown = players.slice(0, max);
    const rest  = players.length - max;
    let html = '<span class="tl-list-avs">';
    shown.forEach(p => {
        html += p.image
            ? `<span class="tl-list-av" style="background-image:url('${cssUrl(p.image)}')" title="${esc(p.displayName || '')}"></span>`
            : `<span class="tl-list-av tl-list-av-letter" title="${esc(p.displayName || '')}">${esc((p.displayName || '?')[0].toUpperCase())}</span>`;
    });
    if (rest > 0) html += `<span class="tl-list-av tl-list-av-more">+${rest}</span>`;
    html += '</span>';
    return html;
}

function _tlListData(ev) {
    switch (ev.type) {
        case 'instance_join':
            return { userHtml: _tlListPlayerAvatars(ev.players, 3), detail: esc(ev.worldName || ev.worldId || 'Unknown World') };
        case 'photo':
            return { userHtml: _tlListPlayerAvatars(ev.players, 3), detail: esc(ev.photoPath ? ev.photoPath.split(/[\\/]/).pop() : 'Photo') };
        case 'first_meet':
            return { userHtml: esc(ev.userName || 'Unknown'), detail: ev.worldName ? esc(ev.worldName) : '' };
        case 'meet_again':
            return { userHtml: esc(ev.userName || 'Unknown'), detail: ev.worldName ? esc(ev.worldName) : '' };
        case 'notification': {
            const typeLabel = NOTIF_TYPE_LABELS[ev.notifType] || ev.notifType || 'Notification';
            const sender    = ev.senderName ? ` from ${esc(ev.senderName)}` : '';
            const msg       = ev.message
                ? ` — ${esc(ev.message.slice(0, 80))}${ev.message.length > 80 ? '…' : ''}`
                : '';
            return { userHtml: ev.senderName ? esc(ev.senderName) : '', detail: esc(typeLabel) + sender + msg };
        }
        case 'avatar_switch':
            return { userHtml: '', detail: esc(ev.userName || '') };
        case 'video_url': {
            const url = ev.message || '';
            const short = url.length > 60 ? url.slice(0, 60) + '…' : url;
            return { userHtml: '', detail: esc(short) };
        }
        default:
            return { userHtml: '', detail: '' };
    }
}

// ═══════════════════════════════════════════════════════════════════
// List View — Friends Timeline
// ═══════════════════════════════════════════════════════════════════

function buildFriendListHtml(events) {
    if (!events.length) return '<div class="empty-msg">No friend activity logged yet.</div>';

    let rows = '';
    events.forEach(ev => {
        const d     = new Date(ev.timestamp);
        const dt    = d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })
                    + ' · ' + d.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
        const meta  = FT_TYPE_META[ev.type] ?? { icon: 'circle', label: ev.type };
        const color = FT_TYPE_COLOR[ev.type] ?? 'var(--tx3)';
        const ei    = jsq(ev.id);
        const detail = _ftListDetail(ev);
        const clickAction = ev.type === 'friend_gps'
            ? `openFtGpsDetail('${ei}')`
            : `openFtDetail('${ei}')`;

        rows += `<tr class="tl-list-row" onclick="${clickAction}">
            <td class="tl-list-dt">${esc(dt)}</td>
            <td class="tl-list-type"><span class="msi tl-list-icon" style="color:${color}">${meta.icon}</span><span>${esc(meta.label)}</span></td>
            <td class="tl-list-user">${esc(ev.friendName || '—')}</td>
            <td class="tl-list-detail">${detail || '<span class="tl-list-na">—</span>'}</td>
        </tr>`;
    });

    return `<div class="tl-list-wrap">
        <table class="tl-list-table">
            <thead><tr>
                <th>Date / Time</th><th>Type</th><th>User</th><th>Detail</th>
            </tr></thead>
            <tbody>${rows}</tbody>
        </table>
    </div>`;
}

function _ftListDetail(ev) {
    switch (ev.type) {
        case 'friend_online':     return '<span style="color:var(--ok)">Came Online</span>';
        case 'friend_offline':    return '<span style="color:var(--tx3)">Went Offline</span>';
        case 'friend_gps':        return esc(ev.worldName || ev.worldId || 'Unknown World');
        case 'friend_status': {
            const oldCls = statusCssClass(ev.oldValue);
            const newCls = statusCssClass(ev.newValue);
            return `<span class="ft-status-chip ${oldCls}">${esc(ev.oldValue || '?')}</span>`
                 + `<span class="msi" style="font-size:12px;color:var(--tx3);vertical-align:middle;margin:0 4px;">arrow_forward</span>`
                 + `<span class="ft-status-chip ${newCls}">${esc(ev.newValue || '?')}</span>`;
        }
        case 'friend_statusdesc': {
            const v = (ev.newValue || '').slice(0, 80);
            return v ? esc(v) + ((ev.newValue || '').length > 80 ? '…' : '') : '<span class="tl-list-na">(cleared)</span>';
        }
        case 'friend_bio': {
            const v = (ev.newValue || '').slice(0, 80);
            return v ? esc(v) + ((ev.newValue || '').length > 80 ? '…' : '') : '<span class="tl-list-na">(cleared)</span>';
        }
        default: return '';
    }
}
