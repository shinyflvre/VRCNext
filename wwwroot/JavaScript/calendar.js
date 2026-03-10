/* === Calendar Tab === */

let calendarLoaded  = false;
let calendarFilter  = 'all';
let _calEvents      = [];
let _calSelectedDay = null;
let _calYear        = new Date().getFullYear();
let _calMonth       = new Date().getMonth();

(function _calCSS() {
    if (document.getElementById('cal-css')) return;
    const s = document.createElement('style');
    s.id = 'cal-css';
    s.textContent = `
        .cal-day {
            background: var(--bg-card);
            border: 1px solid rgba(255,255,255,.06);
            border-radius: 8px;
            min-height: 100px;
            padding: 8px 6px 6px;
            cursor: pointer;
            transition: background .12s, border-color .12s;
            overflow: hidden;
            box-sizing: border-box;
        }
        .cal-day:hover { background: var(--bg-hover); border-color: color-mix(in srgb,var(--accent) 40%,transparent); }
        .cal-day.cal-today { border-color: color-mix(in srgb,var(--accent) 65%,transparent); background: color-mix(in srgb,var(--accent) 7%,transparent); }
        .cal-day.cal-sel   { border: 2px solid var(--accent); background: color-mix(in srgb,var(--accent) 14%,transparent); }
        .cal-day.cal-empty { background: rgba(255,255,255,.015); cursor: default; pointer-events: none; }
        .cal-day-num { font-size: 12px; font-weight: 600; color: var(--tx2); margin-bottom: 4px; line-height: 1; }
        .cal-day.cal-today .cal-day-num,
        .cal-day.cal-sel   .cal-day-num { color: var(--accent-lt); }
        .cal-chip {
            display: block;
            font-size: 9.5px;
            padding: 2px 5px;
            border-radius: 4px;
            margin-bottom: 2px;
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
            line-height: 1.4;
            cursor: pointer;
        }
        .cal-chip:hover { opacity: .8; }
        .cal-chip-f { background: rgba(245,158,11,.2); color: #f6c265; }
        .cal-chip-g { background: color-mix(in srgb,var(--accent) 20%,transparent); color: var(--accent-lt); }
        .cal-chip-more { display: block; font-size: 9px; color: var(--tx3); padding: 1px 2px; }
        .cal-day-hdr { text-align: center; font-size: 10px; font-weight: 700; color: var(--tx3); padding: 4px 2px 6px; letter-spacing: .6px; text-transform: uppercase; }
        .cal-evlist-card {
            display: flex; gap: 10px; align-items: flex-start;
            padding: 10px 12px; background: var(--bg-card);
            border-radius: 10px; margin-bottom: 8px; cursor: pointer;
            border: 1px solid transparent; transition: border-color .12s, background .12s;
        }
        .cal-evlist-card:hover { border-color: color-mix(in srgb,var(--accent) 40%,transparent); background: var(--bg-hover); }
        .cal-evlist-thumb {
            width: 56px; height: 56px; border-radius: 7px;
            object-fit: cover; flex-shrink: 0; background: var(--bg-hover);
            display: flex; align-items: center; justify-content: center;
        }
        .cal-day-panel {
            margin-top: 14px;
            padding: 14px 0 0;
            border-top: 1px solid rgba(255,255,255,.07);
        }
        .cal-day-panel-hdr {
            font-size: 13px; font-weight: 700; color: var(--tx1); margin-bottom: 10px;
            display: flex; align-items: center; gap: 6px;
        }
    `;
    document.head.appendChild(s);
}());

// ── Init ──────────────────────────────────────────────────────────────────────

function _initCalUI() {
    const tab = document.getElementById('tab17');
    if (!tab || document.getElementById('calInner')) return;
    tab.innerHTML = `<div id="calInner">
        <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:16px;gap:8px;">
            <div style="display:flex;align-items:center;gap:4px;">
                <button class="vrcn-button" onclick="_calNavMonth(-1)"><span class="msi" style="font-size:18px;">chevron_left</span></button>
                <span id="calMonthLabel" style="min-width:140px;text-align:center;font-size:14px;font-weight:700;color:var(--tx0);"></span>
                <button class="vrcn-button" onclick="_calNavMonth(1)"><span class="msi" style="font-size:18px;">chevron_right</span></button>
                <button class="vrcn-button sub-tab-btn cal-filter-btn active" data-filter="all"      onclick="setCalFilter('all')"><span class="msi" style="font-size:14px;">calendar_month</span> All</button>
                <button class="vrcn-button sub-tab-btn cal-filter-btn"        data-filter="featured"  onclick="setCalFilter('featured')"><span class="msi" style="font-size:14px;">star</span> Featured</button>
                <button class="vrcn-button sub-tab-btn cal-filter-btn"        data-filter="following" onclick="setCalFilter('following')"><span class="msi" style="font-size:14px;">notifications_active</span> Following</button>
                <button class="vrcn-button" id="calRefreshBtn" onclick="refreshCalendar()" title="Refresh"><span class="msi" style="font-size:18px;">refresh</span></button>
            </div>
        </div>
        <div id="calGridArea"></div>
        <div id="calDayPanel" style="display:none;"></div>
    </div>`;
    _updateMonthLabel();
}

function _updateMonthLabel() {
    const el = document.getElementById('calMonthLabel');
    if (el) el.textContent = new Date(_calYear, _calMonth, 1)
        .toLocaleString(undefined, { month: 'long', year: 'numeric' });
}

// ── Public ────────────────────────────────────────────────────────────────────

function refreshCalendar() {
    _initCalUI();
    const btn = document.getElementById('calRefreshBtn');
    if (btn) { btn.disabled = true; btn.querySelector('.msi').textContent = 'hourglass_empty'; }
    const ga = document.getElementById('calGridArea');
    if (ga) ga.innerHTML = '<div class="empty-msg" style="padding:40px 0;">Loading events...</div>';
    const dp = document.getElementById('calDayPanel');
    if (dp) dp.style.display = 'none';
    sendToCS({ action: 'vrcGetCalendarEvents', filter: calendarFilter, year: _calYear, month: _calMonth + 1 });
}

function setCalFilter(f) {
    if (calendarFilter === f) return;
    calendarFilter = f;
    document.querySelectorAll('.cal-filter-btn').forEach(b =>
        b.classList.toggle('active', b.dataset.filter === f));
    _calEvents = [];
    _calSelectedDay = null;
    refreshCalendar();
}

function renderCalendarEvents(payload) {
    calendarLoaded = true;
    const btn = document.getElementById('calRefreshBtn');
    if (btn) { btn.disabled = false; btn.querySelector('.msi').textContent = 'refresh'; }

    // Handle all possible response shapes
    let raw = payload;
    if (raw?.events)  raw = raw.events;
    else if (raw?.results) raw = raw.results;
    else if (raw?.data)    raw = raw.data;
    let all = Array.isArray(raw) ? raw : [];

    // "featured" filter: same endpoint as "all", filter client-side
    if (calendarFilter === 'featured') all = all.filter(e => e.featured === true || _isFeatured(e));

    _calEvents = all;
    _calSelectedDay = null;
    const dp = document.getElementById('calDayPanel');
    if (dp) dp.style.display = 'none';
    _buildGrid();
}

// ── Navigation ────────────────────────────────────────────────────────────────

function _calNavMonth(delta) {
    _calMonth += delta;
    if (_calMonth > 11) { _calMonth = 0; _calYear++; }
    if (_calMonth < 0)  { _calMonth = 11; _calYear--; }
    _calSelectedDay = null;
    _calEvents = [];
    _updateMonthLabel();
    _buildGrid();
    const dp = document.getElementById('calDayPanel');
    if (dp) dp.style.display = 'none';
    const btn = document.getElementById('calRefreshBtn');
    if (btn) { btn.disabled = true; btn.querySelector('.msi').textContent = 'hourglass_empty'; }
    sendToCS({ action: 'vrcGetCalendarEvents', filter: calendarFilter, year: _calYear, month: _calMonth + 1 });
}

function _calClickDay(key) {
    _calSelectedDay = (_calSelectedDay === key) ? null : key;
    _buildGrid();
    const dayEvts = _calSelectedDay ? _eventsForDay(_calSelectedDay) : [];
    _buildDayPanel(dayEvts, _calSelectedDay);
}

// ── Helpers ───────────────────────────────────────────────────────────────────

function _eventKey(e) {
    const d = new Date(e.startsAt || e.startDate || '');
    if (isNaN(d)) return null;
    return `${d.getUTCFullYear()}-${String(d.getUTCMonth()+1).padStart(2,'0')}-${String(d.getUTCDate()).padStart(2,'0')}`;
}

function _eventsForDay(key) {
    return _calEvents.filter(e => _eventKey(e) === key);
}

function _isFeatured(e) {
    return Array.isArray(e.tags) && e.tags.some(t => /featured/i.test(t));
}

// ── Grid ──────────────────────────────────────────────────────────────────────

function _buildGrid() {
    const wrap = document.getElementById('calGridArea');
    if (!wrap) return;

    const dayMap = {};
    _calEvents.forEach(e => {
        const k = _eventKey(e);
        if (k) (dayMap[k] = dayMap[k] || []).push(e);
    });

    const today     = new Date();
    const todayKey  = `${today.getUTCFullYear()}-${String(today.getUTCMonth()+1).padStart(2,'0')}-${String(today.getUTCDate()).padStart(2,'0')}`;
    const firstDay  = new Date(_calYear, _calMonth, 1).getDay();
    const daysInMth = new Date(_calYear, _calMonth + 1, 0).getDate();

    const DAY_NAMES = ['SUN','MON','TUE','WED','THU','FRI','SAT'];
    const hdr = DAY_NAMES.map(d => `<div class="cal-day-hdr">${d}</div>`).join('');

    let cells = '';
    for (let i = 0; i < firstDay; i++) cells += `<div class="cal-day cal-empty"></div>`;

    for (let day = 1; day <= daysInMth; day++) {
        const key  = `${_calYear}-${String(_calMonth+1).padStart(2,'0')}-${String(day).padStart(2,'0')}`;
        const evts = dayMap[key] || [];
        const isToday = key === todayKey;
        const isSel   = key === _calSelectedDay;
        let cls = 'cal-day';
        if (isToday) cls += ' cal-today';
        if (isSel)   cls += ' cal-sel';

        const chips = evts.slice(0, 3).map(e => {
            const gid = esc(e.ownerId || '');
            const cid = esc(e.id || '');
            const chipCls = _isFeatured(e) ? 'cal-chip-f' : 'cal-chip-g';
            return `<span class="cal-chip ${chipCls}" onclick="event.stopPropagation();openEventDetail('${gid}','${cid}')" title="${esc(e.title||'')}">${esc(e.title || 'Event')}</span>`;
        }).join('');
        const more = evts.length > 3 ? `<span class="cal-chip-more">+${evts.length-3} more</span>` : '';

        cells += `<div class="${cls}" onclick="_calClickDay('${key}')">
            <div class="cal-day-num">${day}</div>${chips}${more}
        </div>`;
    }

    wrap.innerHTML = `<div style="display:grid;grid-template-columns:repeat(7,1fr);gap:5px;">${hdr}${cells}</div>`;
}

// ── Day Panel (below calendar) ────────────────────────────────────────────────

function _buildDayPanel(events, key) {
    const el = document.getElementById('calDayPanel');
    if (!el) return;

    if (!key || events.length === 0) {
        el.style.display = 'none';
        return;
    }

    const dayLabel = new Date(key + 'T12:00:00Z')
        .toLocaleDateString(undefined, { weekday:'long', month:'long', day:'numeric', year:'numeric' });

    const cards = events
        .sort((a,b) => new Date(a.startsAt||a.startDate||0) - new Date(b.startsAt||b.startDate||0))
        .map(e => {
            const d = new Date(e.startsAt || e.startDate || '');
            const timeStr = !isNaN(d) ? d.toLocaleTimeString(undefined, { hour:'2-digit', minute:'2-digit' }) : '';
            const tags = Array.isArray(e.tags) ? e.tags : [];
            const tagHtml = tags.slice(0,4).map(t => {
                const ft = /featured/i.test(t);
                return `<span class="vrcn-badge${ft ? ' warn' : ''}">${esc(t)}</span>`;
            }).join('');
            const imgHtml = e.imageUrl
                ? `<img class="cal-evlist-thumb" src="${e.imageUrl}" onerror="this.style.display='none'">`
                : `<div class="cal-evlist-thumb"><span class="msi" style="font-size:22px;color:var(--tx3);">event</span></div>`;
            return `<div class="cal-evlist-card" onclick="openEventDetail('${esc(e.ownerId||'')}','${esc(e.id||'')}')">
                ${imgHtml}
                <div style="flex:1;min-width:0;">
                    <div style="font-size:12px;font-weight:600;color:var(--tx0);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;margin-bottom:3px;">${esc(e.title||'Untitled Event')}</div>
                    ${timeStr ? `<div style="font-size:10px;color:var(--tx2);margin-bottom:4px;">${esc(timeStr)}</div>` : ''}
                    <div style="display:flex;flex-wrap:wrap;gap:3px;">${tagHtml}</div>
                </div>
            </div>`;
        }).join('');

    el.innerHTML = `<div class="cal-day-panel">
        <div class="cal-day-panel-hdr">
            <span class="msi" style="font-size:16px;color:var(--accent-lt);">calendar_today</span>${esc(dayLabel)}
            <button class="vrcn-button" onclick="_calClickDay('${key}')" style="margin-left:auto;padding:2px 8px;font-size:11px;">✕</button>
        </div>
        <div style="display:grid;grid-template-columns:repeat(auto-fill,minmax(280px,1fr));gap:8px;">${cards}</div>
    </div>`;
    el.style.display = 'block';
}
