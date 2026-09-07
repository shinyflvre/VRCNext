/* === Calendar Tab === */

let calendarLoaded = false;
let calendarFilter = 'all';
let calendarView = 'calendar';
let calendarGroupColors = false;
let _calEvents = [];
let _calSelectedDay = null;
let _calYear = new Date().getFullYear();
let _calMonth = new Date().getMonth();
let _calWeekStart = null;
let _calLoading = false;
let _calLoadedKey = '';
let _calWeekScrollKey = '';
let _calWeekScrollTop = 0;
let _calDpYear = new Date().getFullYear();
let _calDpMonth = new Date().getMonth();
let _calEventDays = new Set();
var _calDashPending = 0;
var _calDashRawEvents = [];

const CAL_MAX_LANES = 2;
const CAL_HOUR_H = 44;


function _calDateLocale() {
    return getLanguageLocale();
}

function _calStartOfWeek(date) {
    const start = new Date(date.getFullYear(), date.getMonth(), date.getDate());
    start.setDate(start.getDate() - ((start.getDay() + 6) % 7));
    return start;
}

function _calWeekDays() {
    if (!_calWeekStart) _calWeekStart = _calStartOfWeek(new Date());
    return Array.from({ length: 7 }, (_, idx) =>
        new Date(_calWeekStart.getFullYear(), _calWeekStart.getMonth(), _calWeekStart.getDate() + idx));
}

function _calFetchYM() {
    if (calendarView === 'week') {
        if (!_calWeekStart) _calWeekStart = _calStartOfWeek(new Date());
        return { year: _calWeekStart.getFullYear(), month: _calWeekStart.getMonth() + 1 };
    }
    return { year: _calYear, month: _calMonth + 1 };
}

function _calRequestEvents() {
    const ym = _calFetchYM();
    _calLoadedKey = `${calendarFilter}|${ym.year}-${ym.month}`;
    _calLoading = true;
    _syncCalView();
    sendToCS({ action: 'vrcGetCalendarEvents', filter: calendarFilter, year: ym.year, month: ym.month });
}

function _calEnsureEvents() {
    const ym = _calFetchYM();
    if (`${calendarFilter}|${ym.year}-${ym.month}` === _calLoadedKey) {
        _syncCalView();
        return;
    }
    _calEvents = [];
    _calRequestEvents();
}

function _renderCalUI() {
    const tab = document.getElementById('tab17');
    if (!tab) return;

    const refreshTitle = esc(t('calendar.refresh_title', 'Refresh calendar'));
    const refreshIcon = _calLoading ? 'hourglass_empty' : 'refresh';
    const refreshDisabled = _calLoading ? ' disabled' : '';

    tab.innerHTML = `<div id="calInner">
        <div class="tab-toolbar" style="display:flex;align-items:center;justify-content:space-between;gap:8px;">
            <div class="tt-scroll" style="--tt-gap:4px;">
                <button class="vrcn-button tl-datebtn" onclick="_calNav(-1)"><span class="msi" style="font-size:16px;">chevron_left</span></button>
                <button class="vrcn-button tl-datebtn cal-date-btn" id="calDateBtn" onclick="toggleCalDatePicker()" title="${esc(t('calendar.date.jump_title', 'Jump to date'))}">
                    <span class="msi" style="font-size:15px;">calendar_today</span>
                    <span id="calMonthLabel" style="color:var(--accent-lt);font-size:calc(11px + var(--fs-off, 0px));min-width:104px;text-align:center;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;"></span>
                </button>
                <button class="vrcn-button tl-datebtn" onclick="_calNav(1)"><span class="msi" style="font-size:16px;">chevron_right</span></button>
                <button class="vrcn-button sub-tab-btn cal-filter-btn${calendarFilter === 'all' ? ' active' : ''}" data-filter="all" onclick="setCalFilter('all')"><span class="msi" style="font-size:14px;">calendar_month</span> ${esc(t('calendar.filters.all', 'All'))}</button>
                <button class="vrcn-button sub-tab-btn cal-filter-btn${calendarFilter === 'featured' ? ' active' : ''}" data-filter="featured" onclick="setCalFilter('featured')"><span class="msi" style="font-size:14px;">star</span> ${esc(t('calendar.filters.featured', 'Featured'))}</button>
                <button class="vrcn-button sub-tab-btn cal-filter-btn${calendarFilter === 'following' ? ' active' : ''}" data-filter="following" onclick="setCalFilter('following')"><span class="msi" style="font-size:14px;">notifications_active</span> ${esc(t('calendar.filters.following', 'Following'))}</button>
                <button class="vrcn-button" id="calRefreshBtn" onclick="refreshCalendar()" title="${refreshTitle}"${refreshDisabled}><span class="msi" style="font-size:18px;">${refreshIcon}</span></button>
            </div>
            <div style="display:flex;gap:4px;align-items:center;flex-shrink:0;">
                <button class="vrcn-button sub-tab-btn cal-view-btn${calendarGroupColors ? ' active' : ''}" onclick="toggleCalGroupColors()" title="${esc(t('calendar.help_sort', 'Help Sort'))}"><span class="msi" style="font-size:16px;">palette</span></button>
                <button class="vrcn-button sub-tab-btn cal-view-btn${calendarView === 'calendar' ? ' active' : ''}" data-view="calendar" onclick="setCalView('calendar')" title="${esc(t('calendar.view.calendar', 'Calendar View'))}"><span class="msi" style="font-size:16px;">calendar_month</span></button>
                <button class="vrcn-button sub-tab-btn cal-view-btn${calendarView === 'week' ? ' active' : ''}" data-view="week" onclick="setCalView('week')" title="${esc(t('calendar.view.week', 'Week View'))}"><span class="msi" style="font-size:16px;">today</span></button>
            </div>
        </div>
        <div id="calGridArea"></div>
        <div id="calDayPanel" style="display:none;"></div>
    </div>`;

    _updateMonthLabel();

    if (document.getElementById('calDatePicker')?.style.display === '') {
        document.getElementById('calDateBtn')?.classList.add('dp-active');
    }
}

function _syncCalView() {
    if (!document.getElementById('tab17')) return;
    const wkEl = document.getElementById('calWeekScroll');
    if (wkEl) {
        _calWeekScrollKey = wkEl.dataset.week || '';
        _calWeekScrollTop = wkEl.scrollTop;
    }
    _renderCalUI();
    if (_calLoading) {
        const gridArea = document.getElementById('calGridArea');
        if (gridArea) {
            gridArea.innerHTML = `<div class="empty-msg" style="padding:40px 0;">${esc(t('calendar.loading', 'Loading events...'))}</div>`;
        }
        return;
    }
    if (calendarView === 'week') _buildWeekView();
    else _buildGrid();
    const dayEvents = _calSelectedDay ? _eventsForDay(_calSelectedDay) : [];
    _buildDayPanel(dayEvents, _calSelectedDay);
    _calSizeView();
}

function _initCalUI() {
    const tab = document.getElementById('tab17');
    if (!tab || document.getElementById('calInner')) return;
    _renderCalUI();
}

function _updateMonthLabel() {
    const el = document.getElementById('calMonthLabel');
    if (!el) return;
    if (calendarView === 'week') {
        const days = _calWeekDays();
        el.textContent = `${fmtShortDate(days[0])} - ${fmtShortDate(days[6])}`;
        return;
    }
    el.textContent = new Date(_calYear, _calMonth, 1).toLocaleDateString(_calDateLocale(), { month: 'long', year: 'numeric' });
}

function _calDpEl() {
    let el = document.getElementById('calDatePicker');
    if (!el) {
        el = document.createElement('div');
        el.id = 'calDatePicker';
        el.className = 'tl-date-picker';
        el.style.display = 'none';
        document.body.appendChild(el);
    }
    return el;
}

function toggleCalDatePicker() {
    const picker = _calDpEl();
    const btn = document.getElementById('calDateBtn');
    if (!btn) return;
    if (picker.style.display !== 'none') { _calDpClose(); return; }

    const base = calendarView === 'week' ? _calWeekDays()[0] : new Date(_calYear, _calMonth, 1);
    _calDpYear = base.getFullYear();
    _calDpMonth = base.getMonth();
    _calDpRender();

    picker.style.display = '';
    btn.classList.add('dp-active');
    const rect = btn.getBoundingClientRect();
    const ph = picker.offsetHeight || 290;
    const top = rect.bottom + 6 + ph > window.innerHeight ? rect.top - ph - 6 : rect.bottom + 6;
    picker.style.top = `${Math.max(6, top)}px`;
    picker.style.left = `${Math.max(6, Math.min(rect.left, window.innerWidth - 268))}px`;

    setTimeout(() => document.addEventListener('click', _calDpOutside), 0);
}

function _calDpClose() {
    const picker = document.getElementById('calDatePicker');
    if (picker) picker.style.display = 'none';
    document.getElementById('calDateBtn')?.classList.remove('dp-active');
    document.removeEventListener('click', _calDpOutside);
}

function _calDpOutside(e) {
    const picker = document.getElementById('calDatePicker');
    const btn = document.getElementById('calDateBtn');
    if (!picker) return;
    if (picker.contains(e.target) || (btn && (e.target === btn || btn.contains(e.target)))) return;
    _calDpClose();
}

function _calDpRender() {
    const picker = document.getElementById('calDatePicker');
    if (!picker) return;

    const wd = ['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun']
        .map((day, idx) => `<div class="tl-dp-wd">${esc(t(`timeline.datepicker.weekday.${day}`, ['Mo', 'Tu', 'We', 'Th', 'Fr', 'Sa', 'Su'][idx]))}</div>`)
        .join('');

    picker.innerHTML = `<div class="tl-dp-header">
            <button class="tl-dp-nav" onclick="_calDpNavMonth(-1)"><span class="msi" style="font-size:16px;">chevron_left</span></button>
            <span id="calDpMonthLabel" class="tl-dp-month-label"></span>
            <button class="tl-dp-nav" onclick="_calDpNavMonth(1)"><span class="msi" style="font-size:16px;">chevron_right</span></button>
        </div>
        <div class="tl-dp-weekdays">${wd}</div>
        <div id="calDpDaysGrid" class="tl-dp-days"></div>
        <div class="tl-dp-footer">
            <button class="vrcn-button" style="flex:1;justify-content:center;font-size:calc(11px + var(--fs-off, 0px));" onclick="calDpToday()">${esc(t('timeline.datepicker.today', 'Today'))}</button>
        </div>`;

    _calDpRenderDays();
}

function _calDpRenderDays() {
    const label = document.getElementById('calDpMonthLabel');
    const grid = document.getElementById('calDpDaysGrid');
    if (!label || !grid) return;

    label.textContent = new Date(_calDpYear, _calDpMonth, 1).toLocaleDateString(_calDateLocale(), { month: 'long', year: 'numeric' });

    const todayKey = _calDayKey(new Date());
    const marked = new Set();
    if (calendarView === 'week') _calWeekDays().forEach(d => marked.add(_calDayKey(d)));
    else if (_calSelectedDay) marked.add(_calSelectedDay);

    const firstDowMon = (new Date(_calDpYear, _calDpMonth, 1).getDay() + 6) % 7;
    const daysInMonth = new Date(_calDpYear, _calDpMonth + 1, 0).getDate();
    const daysInPrevMo = new Date(_calDpYear, _calDpMonth, 0).getDate();

    const cell = (year, month, day, other) => {
        const key = _calDayKey(new Date(year, month, day));
        const cls = (other ? ' other-month' : '') + (key === todayKey ? ' today' : '') + (marked.has(key) ? ' selected' : '');
        const dot = _calEventDays.has(key) ? '<span class="dp-dots"><span class="dp-dot dp-dot-p"></span></span>' : '';
        return `<button class="tl-dp-day${cls}" onclick="calDpSelectDate('${key}')">${day}${dot}</button>`;
    };

    let days = '';
    for (let i = firstDowMon - 1; i >= 0; i--) days += cell(_calDpYear, _calDpMonth - 1, daysInPrevMo - i, true);
    for (let d = 1; d <= daysInMonth; d++) days += cell(_calDpYear, _calDpMonth, d, false);
    const used = firstDowMon + daysInMonth;
    const remaining = used % 7 === 0 ? 0 : 7 - (used % 7);
    for (let d = 1; d <= remaining; d++) days += cell(_calDpYear, _calDpMonth + 1, d, true);

    grid.innerHTML = days;
}

function _calDpNavMonth(dir) {
    _calDpMonth += dir;
    if (_calDpMonth < 0) { _calDpMonth = 11; _calDpYear--; }
    if (_calDpMonth > 11) { _calDpMonth = 0; _calDpYear++; }
    _calDpRenderDays();
}

function calDpSelectDate(key) {
    _calDpClose();
    const date = new Date(`${key}T00:00:00`);
    if (isNaN(date)) return;
    if (calendarView === 'week') {
        _calWeekStart = _calStartOfWeek(date);
        _calYear = _calWeekStart.getFullYear();
        _calMonth = _calWeekStart.getMonth();
    } else {
        _calYear = date.getFullYear();
        _calMonth = date.getMonth();
    }
    _calSelectedDay = null;
    _calEnsureEvents();
}

function calDpToday() {
    calDpSelectDate(_calDayKey(new Date()));
}

function refreshCalendar() {
    _initCalUI();
    _calRequestEvents();
}

function setCalFilter(filter) {
    if (calendarFilter === filter) return;
    calendarFilter = filter;
    _calEvents = [];
    _calEventDays = new Set();
    _calSelectedDay = null;
    refreshCalendar();
}

function setCalView(view) {
    if (calendarView === view) return;
    calendarView = view;
    _calSelectedDay = null;
    if (view === 'week') {
        _calWeekStart = _calStartOfWeek(new Date());
        _calYear = _calWeekStart.getFullYear();
        _calMonth = _calWeekStart.getMonth();
    }
    _calEnsureEvents();
}

function renderCalendarEvents(payload) {
    let raw = payload;
    if (raw?.events) raw = raw.events;
    else if (raw?.results) raw = raw.results;
    else if (raw?.data) raw = raw.data;
    let all = Array.isArray(raw) ? raw : [];

    // Dashboard-only fetch: accumulate but don't touch calendar state or UI
    if (_calDashPending > 0) {
        _calDashRawEvents = _calDashRawEvents.concat(all);
        _calDashPending--;
        if (_calDashPending <= 0 && typeof onCalendarEventsForDash === 'function') {
            onCalendarEventsForDash(_calDashRawEvents);
        }
        return;
    }

    // Normal calendar flow
    calendarLoaded = true;
    _calLoading = false;

    if (calendarFilter === 'featured') {
        all = all.filter(e => e.featured === true || _isFeatured(e));
    }

    _calEvents = all;
    all.forEach(evt => _eventDayKeys(evt).forEach(key => _calEventDays.add(key)));
    _calSelectedDay = null;
    _syncCalView();
    if (document.getElementById('calDatePicker')?.style.display === '') _calDpRenderDays();
}

function _calNav(delta) {
    if (calendarView === 'week') _calNavWeek(delta);
    else _calNavMonth(delta);
}

function _calNavMonth(delta) {
    _calMonth += delta;
    if (_calMonth > 11) {
        _calMonth = 0;
        _calYear++;
    }
    if (_calMonth < 0) {
        _calMonth = 11;
        _calYear--;
    }
    _calSelectedDay = null;
    _calEvents = [];
    _calRequestEvents();
}

function _calNavWeek(delta) {
    if (!_calWeekStart) _calWeekStart = _calStartOfWeek(new Date());
    _calWeekStart = new Date(_calWeekStart.getFullYear(), _calWeekStart.getMonth(), _calWeekStart.getDate() + delta * 7);
    _calYear = _calWeekStart.getFullYear();
    _calMonth = _calWeekStart.getMonth();
    _calSelectedDay = null;
    _calEnsureEvents();
}

function _calClickDay(key) {
    _calSelectedDay = _calSelectedDay === key ? null : key;
    if (calendarView === 'week') _buildWeekView();
    else _buildGrid();
    const dayEvents = _calSelectedDay ? _eventsForDay(_calSelectedDay) : [];
    _buildDayPanel(dayEvents, _calSelectedDay);
    _calSizeView();
}

function _calDayKey(date) {
    return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
}


function _eventDayKeys(evt) {
    const start = new Date(evt.startsAt || evt.startDate || '');
    if (isNaN(start)) return [];
    let end = new Date(evt.endsAt || evt.endDate || '');
    if (isNaN(end) || end < start) end = start;
    const keys = [];
    const cur  = new Date(start.getFullYear(), start.getMonth(), start.getDate());
    const last = new Date(end.getFullYear(), end.getMonth(), end.getDate());
    let guard = 0;
    while (cur <= last && guard++ < 366) {
        keys.push(_calDayKey(cur));
        cur.setDate(cur.getDate() + 1);
    }
    return keys;
}

function _eventsForDay(key) {
    return _calEvents.filter(evt => _eventDayKeys(evt).includes(key));
}

function _isFeatured(evt) {
    return Array.isArray(evt.tags) && evt.tags.some(tag => /featured/i.test(tag));
}

const CAL_GROUP_PALETTE = [
    { bg: 'rgba(200,166,219,.32)', fg: '#c8a6db' },
    { bg: 'rgba(255,181,209,.32)', fg: '#ffb5d1' },
    { bg: 'rgba(255,147,186,.32)', fg: '#ff93ba' },
    { bg: 'rgba(129,194,255,.32)', fg: '#81c2ff' },
    { bg: 'rgba(108,162,248,.32)', fg: '#6ca2f8' },
    { bg: 'rgba(255,200,255,.32)', fg: '#ffc8ff' },
    { bg: 'rgba(223,178,255,.32)', fg: '#dfb2ff' },
    { bg: 'rgba(159,170,255,.32)', fg: '#9faaff' },
    { bg: 'rgba(181,156,255,.32)', fg: '#b59cff' },
    { bg: 'rgba(163,192,255,.32)', fg: '#a3c0ff' },
    { bg: 'rgba(95,193,219,.32)', fg: '#5fc1db' },
    { bg: 'rgba(255,190,194,.32)', fg: '#ffbec2' },
    { bg: 'rgba(195,167,240,.32)', fg: '#c3a7f0' },
    { bg: 'rgba(135,180,243,.32)', fg: '#87b4f3' },
];

function _calGroupColor(evt) {
    const seed = String(evt.ownerId || evt.groupId || evt.title || '');
    let hash = 2166136261;
    for (let i = 0; i < seed.length; i++) {
        hash ^= seed.charCodeAt(i);
        hash = Math.imul(hash, 16777619);
    }
    return CAL_GROUP_PALETTE[Math.abs(hash) % CAL_GROUP_PALETTE.length];
}

function _calGroupStyle(evt) {
    if (!calendarGroupColors) return '';
    const color = _calGroupColor(evt);
    return `background:${color.bg};color:${color.fg};`;
}

function toggleCalGroupColors() {
    calendarGroupColors = !calendarGroupColors;
    _syncCalView();
}

function _calMoreLabel(count) {
    if (count === 1) return t('calendar.more_events_one', '1 more event');
    return tf('calendar.more_events', { count }, '{count} more events');
}

function _buildGrid() {
    const wrap = document.getElementById('calGridArea');
    if (!wrap) return;

    const DAY = 86400000;
    const today = new Date();
    const todayKey = _calDayKey(today);
    const firstDay = new Date(_calYear, _calMonth, 1).getDay();
    const firstDayMon = (firstDay + 6) % 7;
    const daysInMonth = new Date(_calYear, _calMonth + 1, 0).getDate();
    const weeks = Math.ceil((firstDayMon + daysInMonth) / 7);
    const totalCells = weeks * 7;
    const gridStart = new Date(_calYear, _calMonth, 1 - firstDayMon);
    gridStart.setHours(0, 0, 0, 0);

    const gIndex = d => Math.round((new Date(d.getFullYear(), d.getMonth(), d.getDate()) - gridStart) / DAY);

    const segs = [];
    _calEvents.forEach(evt => {
        const start = new Date(evt.startsAt || evt.startDate || '');
        if (isNaN(start)) return;
        let end = new Date(evt.endsAt || evt.endDate || '');
        if (isNaN(end) || end < start) end = start;
        const rawS = gIndex(start), rawE = gIndex(end);
        if (rawE < 0 || rawS > totalCells - 1) return;
        segs.push({ evt, gS: Math.max(0, rawS), gE: Math.min(totalCells - 1, rawE), trueStart: rawS >= 0, trueEnd: rawE <= totalCells - 1 });
    });

    segs.sort((a, b) => a.gS - b.gS || (b.gE - b.gS) - (a.gE - a.gS));
    const laneEnd = [];
    segs.forEach(seg => {
        let lane = 0;
        while (lane < laneEnd.length && laneEnd[lane] >= seg.gS) lane++;
        seg.lane = lane;
        laneEnd[lane] = seg.gE;
    });

    // 2024-01-08 was a Monday, matching the Monday-first grid above. Built in local
    // time on purpose: Date.UTC() would put midnight UTC on the previous day for
    // anyone west of UTC, shifting every header label back by one.
    const hdr = Array.from({ length: 7 }, (_, idx) => {
        const label = new Date(2024, 0, 8 + idx).toLocaleDateString(_calDateLocale(), { weekday: 'short' });
        return `<div class="cal-day-hdr">${esc(label.toUpperCase())}</div>`;
    }).join('');

    let weeksHtml = '';
    for (let w = 0; w < weeks; w++) {
        const wStart = w * 7, wEnd = w * 7 + 6;
        const weekSegs = segs.filter(seg => seg.gE >= wStart && seg.gS <= wEnd);
        let lanes = 0;
        weekSegs.forEach(seg => { lanes = Math.max(lanes, seg.lane + 1); });
        const shownLanes = Math.min(lanes, CAL_MAX_LANES);

        const hidden = weekSegs.filter(seg => seg.lane >= CAL_MAX_LANES);
        const overflow = Array.from({ length: 7 }, (_, c) => {
            const idx = wStart + c;
            return hidden.filter(seg => seg.gS <= idx && seg.gE >= idx).length;
        });
        const hasMore = overflow.some(n => n > 0);

        let dayCells = '';
        for (let c = 0; c < 7; c++) {
            const cellDate = new Date(gridStart.getTime() + (wStart + c) * DAY);
            const inMonth = cellDate.getMonth() === _calMonth && cellDate.getFullYear() === _calYear;
            const key = _calDayKey(cellDate);
            let cls = 'cal-day';
            if (!inMonth) cls += ' cal-out';
            if (key === todayKey) cls += ' cal-today';
            if (key === _calSelectedDay) cls += ' cal-sel';
            dayCells += `<div class="${cls}" style="grid-column:${c + 1};grid-row:1/-1;" onclick="_calClickDay('${key}')"><div class="cal-day-num">${cellDate.getDate()}</div></div>`;
        }

        let bars = '';
        weekSegs.forEach(seg => {
            if (seg.lane >= CAL_MAX_LANES) return;
            const colStart = Math.max(seg.gS, wStart) - wStart + 1;
            const colEnd = Math.min(seg.gE, wEnd) - wStart + 1;
            const openLeft = !(seg.gS >= wStart && seg.trueStart);
            const openRight = !(seg.gE <= wEnd && seg.trueEnd);
            const showLabel = seg.gS >= wStart;
            const evt = seg.evt;
            const barCls = _isFeatured(evt) ? 'cal-bar-f' : 'cal-bar-g';
            const edge = (openLeft ? ' cal-bar-openl' : '') + (openRight ? ' cal-bar-openr' : '');
            const title = evt.title || t('calendar.event_fallback', 'Event');
            bars += `<div class="cal-bar ${barCls}${edge}" data-pin-event-id="${esc(evt.id || '')}" data-pin-event-owner="${esc(evt.ownerId || '')}" data-pin-event-name="${esc(evt.title || '')}" data-pin-event-image="${esc(evt.imageUrl || '')}" style="grid-column:${colStart}/${colEnd + 1};grid-row:${seg.lane + 2};${_calGroupStyle(evt)}" onclick="event.stopPropagation();openEventDetail('${esc(evt.ownerId || '')}','${esc(evt.id || '')}')" title="${esc(title)}">${showLabel ? esc(title) : ''}</div>`;
        });

        let moreChips = '';
        if (hasMore) {
            overflow.forEach((count, c) => {
                if (count <= 0) return;
                const cellDate = new Date(gridStart.getTime() + (wStart + c) * DAY);
                const key = _calDayKey(cellDate);
                moreChips += `<div class="cal-more" style="grid-column:${c + 1};grid-row:${shownLanes + 2};" onclick="event.stopPropagation();_calClickDay('${key}')">${esc(_calMoreLabel(count))}</div>`;
            });
        }

        const rows = `26px ${shownLanes > 0 ? `repeat(${shownLanes}, 22px) ` : ''}${hasMore ? '18px ' : ''}1fr`;
        weeksHtml += `<div class="cal-week" style="grid-template-rows:${rows};">${dayCells}${bars}${moreChips}</div>`;
    }

    wrap.innerHTML = `<div class="cal-mo" id="calMonthCard">
        <div class="cal-hdr-row">${hdr}</div>
        <div class="cal-month">${weeksHtml}</div>
    </div>`;

    _calSizeView();
}

function _calLayoutDayColumn(items) {
    items.sort((a, b) => a.sMin - b.sMin || b.eMin - a.eMin);
    const placed = [];
    let cluster = [];
    let clusterEnd = -1;

    const flush = () => {
        if (!cluster.length) return;
        const colEnd = [];
        cluster.forEach(item => {
            let col = 0;
            while (col < colEnd.length && colEnd[col] > item.sMin) col++;
            item.col = col;
            colEnd[col] = item.eMin;
        });
        cluster.forEach(item => { item.cols = colEnd.length; });
        placed.push(...cluster);
        cluster = [];
        clusterEnd = -1;
    };

    items.forEach(item => {
        if (cluster.length && item.sMin >= clusterEnd) flush();
        cluster.push(item);
        clusterEnd = Math.max(clusterEnd, item.eMin);
    });
    flush();
    return placed;
}

function _calSizeView() {
    const el = document.getElementById('calWeekScroll') || document.getElementById('calMonthCard');
    if (!el || !el.offsetParent) return;
    const panel = document.getElementById('calDayPanel');
    const reserve = panel && panel.style.display !== 'none'
        ? Math.min(panel.offsetHeight + 14, Math.round(window.innerHeight * 0.35))
        : 0;
    const top = el.getBoundingClientRect().top;
    el.style.height = `${Math.max(320, window.innerHeight - top - 28 - reserve)}px`;
}

function _calEventAttrs(evt) {
    return `data-pin-event-id="${esc(evt.id || '')}" data-pin-event-owner="${esc(evt.ownerId || '')}" data-pin-event-name="${esc(evt.title || '')}" data-pin-event-image="${esc(evt.imageUrl || '')}" onclick="event.stopPropagation();openEventDetail('${esc(evt.ownerId || '')}','${esc(evt.id || '')}')"`;
}

function _buildWeekView() {
    const wrap = document.getElementById('calGridArea');
    if (!wrap) return;

    const DAY = 86400000;
    const days = _calWeekDays();
    const weekStart = days[0];
    const weekKey = _calDayKey(weekStart);
    const todayKey = _calDayKey(new Date());

    const liveScroller = document.getElementById('calWeekScroll');
    const keepScroll = liveScroller && liveScroller.dataset.week === weekKey
        ? liveScroller.scrollTop
        : (_calWeekScrollKey === weekKey ? _calWeekScrollTop : null);
    const dIndex = d => Math.round((new Date(d.getFullYear(), d.getMonth(), d.getDate()) - weekStart) / DAY);

    const allDay = [];
    const timed = Array.from({ length: 7 }, () => []);

    _calEvents.forEach(evt => {
        const start = new Date(evt.startsAt || evt.startDate || '');
        if (isNaN(start)) return;
        let end = new Date(evt.endsAt || evt.endDate || '');
        if (isNaN(end) || end <= start) end = new Date(start.getTime() + 3600000);

        const sIdx = dIndex(start);
        const eIdx = dIndex(end);
        if (eIdx < 0 || sIdx > 6) return;

        // An event ending at 00:00 belongs to the day it started on, not the next one.
        const endsAtMidnight = end.getHours() === 0 && end.getMinutes() === 0;
        const sameDay = eIdx === sIdx || (eIdx === sIdx + 1 && endsAtMidnight);

        if (!sameDay) {
            allDay.push({
                evt,
                gS: Math.max(0, sIdx),
                gE: Math.min(6, eIdx),
                openL: sIdx < 0,
                openR: eIdx > 6,
                showLabel: sIdx >= 0,
            });
            return;
        }

        const sMin = start.getHours() * 60 + start.getMinutes();
        const rawEnd = eIdx === sIdx ? end.getHours() * 60 + end.getMinutes() : 24 * 60;
        const eMin = Math.min(24 * 60, Math.max(rawEnd, sMin + 15));
        timed[sIdx].push({ evt, start, end, sMin, eMin });
    });

    allDay.sort((a, b) => a.gS - b.gS || (b.gE - b.gS) - (a.gE - a.gS));
    const laneEnd = [];
    allDay.forEach(seg => {
        let lane = 0;
        while (lane < laneEnd.length && laneEnd[lane] >= seg.gS) lane++;
        seg.lane = lane;
        laneEnd[lane] = seg.gE;
    });

    const hdrCells = days.map(date => {
        const key = _calDayKey(date);
        const dow = date.toLocaleDateString(_calDateLocale(), { weekday: 'short' });
        let cls = 'cal-wk-hdr-cell';
        if (key === todayKey) cls += ' cal-wk-today';
        if (key === _calSelectedDay) cls += ' cal-wk-sel';
        return `<div class="${cls}" onclick="_calClickDay('${key}')">
            <span class="cal-wk-dow">${esc(dow.toUpperCase())}</span>
            <span class="cal-wk-dnum">${date.getDate()}</span>
        </div>`;
    }).join('');

    let allDayHtml = '';
    if (allDay.length) {
        const bars = allDay.map(seg => {
            const barCls = _isFeatured(seg.evt) ? 'cal-bar-f' : 'cal-bar-g';
            const edge = (seg.openL ? ' cal-bar-openl' : '') + (seg.openR ? ' cal-bar-openr' : '');
            const title = seg.evt.title || t('calendar.event_fallback', 'Event');
            return `<div class="cal-bar ${barCls}${edge}" style="grid-column:${seg.gS + 1}/${seg.gE + 2};grid-row:${seg.lane + 1};${_calGroupStyle(seg.evt)}" ${_calEventAttrs(seg.evt)} title="${esc(title)}">${seg.showLabel ? esc(title) : ''}</div>`;
        }).join('');
        const laneCount = laneEnd.length;
        allDayHtml = `<div class="cal-wk-allday">
            <div class="cal-wk-gutter-lbl">${esc(t('calendar.week.all_day', 'All day'))}</div>
            <div class="cal-wk-allday-grid" style="grid-template-rows:repeat(${laneCount}, 22px);">${bars}</div>
        </div>`;
    }

    const gutter = Array.from({ length: 24 }, (_, h) =>
        `<div class="cal-wk-hour">${esc(fmtTime(new Date(2024, 0, 1, h, 0)))}</div>`).join('');

    const cols = days.map((date, idx) => {
        const key = _calDayKey(date);
        let cls = 'cal-wk-col';
        if (key === todayKey) cls += ' cal-wk-today';
        if (key === _calSelectedDay) cls += ' cal-wk-sel';

        const blocks = _calLayoutDayColumn(timed[idx]).map(item => {
            const top = (item.sMin / 60) * CAL_HOUR_H;
            const height = Math.max(20, ((item.eMin - item.sMin) / 60) * CAL_HOUR_H - 2);
            const width = 100 / item.cols;
            const left = width * item.col;
            const evtCls = _isFeatured(item.evt) ? 'cal-wk-evt-f' : 'cal-wk-evt-g';
            const title = item.evt.title || t('calendar.untitled_event', 'Untitled Event');
            const timeStr = `${fmtTime(item.start)} - ${fmtTime(item.end)}`;
            return `<div class="cal-wk-evt ${evtCls}" style="top:${top.toFixed(2)}px;height:${height.toFixed(2)}px;left:calc(${left.toFixed(3)}% + 2px);width:calc(${width.toFixed(3)}% - 4px);${_calGroupStyle(item.evt)}" ${_calEventAttrs(item.evt)} title="${esc(`${title} | ${timeStr}`)}">
                <div class="cal-wk-evt-title">${esc(title)}</div>
                <div class="cal-wk-evt-time">${esc(timeStr)}</div>
            </div>`;
        }).join('');

        const now = new Date();
        const nowLine = key === todayKey
            ? `<div class="cal-wk-now" style="top:${(((now.getHours() * 60 + now.getMinutes()) / 60) * CAL_HOUR_H).toFixed(2)}px;"></div>`
            : '';

        return `<div class="${cls}" onclick="_calClickDay('${key}')">${blocks}${nowLine}</div>`;
    }).join('');

    wrap.innerHTML = `<div class="cal-wk" id="calWeekScroll" data-week="${weekKey}">
        <div class="cal-wk-hdr">
            <div class="cal-wk-gutter-sp"></div>
            ${hdrCells}
        </div>
        ${allDayHtml}
        <div class="cal-wk-body">
            <div class="cal-wk-gutter">${gutter}</div>
            <div class="cal-wk-cols" style="height:${24 * CAL_HOUR_H}px;">${cols}</div>
        </div>
    </div>`;

    _calSizeView();

    const scroller = document.getElementById('calWeekScroll');
    const grid = scroller?.querySelector('.cal-wk-body');
    const hdrEl = scroller?.querySelector('.cal-wk-hdr');
    if (scroller && grid && hdrEl) {
        if (keepScroll !== null) {
            scroller.scrollTop = keepScroll;
        } else {
            let firstMin = 8 * 60;
            timed.forEach(list => list.forEach(item => { firstMin = Math.min(firstMin, item.sMin); }));
            const offset = Math.max(0, (firstMin / 60) * CAL_HOUR_H - CAL_HOUR_H / 2);
            scroller.scrollTop = Math.max(0, grid.offsetTop - hdrEl.offsetHeight + offset);
        }
    }
}

function _buildDayPanel(events, key) {
    const el = document.getElementById('calDayPanel');
    if (!el) return;

    if (!key || events.length === 0) {
        el.style.display = 'none';
        return;
    }

    const dayLabel = fmtLongDate(new Date(`${key}T12:00:00Z`));

    const cards = events
        .sort((a, b) => new Date(a.startsAt || a.startDate || 0) - new Date(b.startsAt || b.startDate || 0))
        .map(evt => {
            const date = new Date(evt.startsAt || evt.startDate || '');
            const timeStr = !isNaN(date) ? fmtTime(date) : '';
            const tags = Array.isArray(evt.tags) ? evt.tags : [];
            const tagHtml = tags.slice(0, 4).map(tag => {
                const featured = /featured/i.test(tag);
                return `<span class="vrcn-badge${featured ? ' warn' : ''}">${esc(tag)}</span>`;
            }).join('');
            const imgHtml = evt.imageUrl
                ? `<img class="cal-evlist-thumb" src="${imgThumb(evt.imageUrl, 128)}" onerror="this.style.display='none'">`
                : `<div class="cal-evlist-thumb"><span class="msi" style="font-size:22px;color:var(--tx2);">event</span></div>`;

            return `<div class="cal-evlist-card" data-pin-event-id="${esc(evt.id || '')}" data-pin-event-owner="${esc(evt.ownerId || '')}" data-pin-event-name="${esc(evt.title || '')}" data-pin-event-image="${esc(evt.imageUrl || '')}" onclick="openEventDetail('${esc(evt.ownerId || '')}','${esc(evt.id || '')}')">
                ${imgHtml}
                <div style="flex:1;min-width:0;">
                    <div style="font-size:calc(12px + var(--fs-off, 0px));font-weight:600;color:var(--tx0);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;margin-bottom:3px;">${esc(evt.title || t('calendar.untitled_event', 'Untitled Event'))}</div>
                    ${timeStr ? `<div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx2);margin-bottom:4px;">${esc(timeStr)}</div>` : ''}
                    <div style="display:flex;flex-wrap:wrap;gap:3px;">${tagHtml}</div>
                </div>
            </div>`;
        }).join('');

    el.innerHTML = `<div class="cal-day-panel">
        <div class="cal-day-panel-hdr">
            <span class="msi" style="font-size:16px;color:var(--accent-lt);">calendar_today</span>${esc(dayLabel)}
            <button class="vrcn-button" onclick="_calClickDay('${key}')" style="margin-left:auto;padding:2px 8px;font-size:calc(11px + var(--fs-off, 0px));" title="${esc(t('common.close', 'Close'))}"><span class="msi" style="font-size:14px;">close</span></button>
        </div>
        <div style="display:grid;grid-template-columns:repeat(auto-fill,minmax(280px,1fr));gap:8px;">${cards}</div>
    </div>`;
    el.style.display = 'block';
}

function rerenderCalendarTranslations() {
    if (!document.getElementById('calInner')) return;
    _syncCalView();
    if (document.getElementById('calDatePicker')?.style.display === '') _calDpRender();
}

document.documentElement.addEventListener('languagechange', rerenderCalendarTranslations);
document.documentElement.addEventListener('tabchange', () => { _calDpClose(); _calSizeView(); });
window.addEventListener('resize', _calSizeView);
