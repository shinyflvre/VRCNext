/* === Instance Chart === */

let _icData = [];
let _icDayKey = '';

const IC_COLORS = ['var(--accent)', 'var(--ok)', 'var(--cyan)', 'var(--warn)', '#AB47BC', '#42A5F5', '#26A69A', '#EC407A'];

function _icHash(s) {
    let h = 0;
    s = String(s || '');
    for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) | 0;
    return Math.abs(h);
}

function _icSelectedDate() {
    if (typeof tlDateFilter !== 'undefined' && tlDateFilter) return new Date(tlDateFilter + 'T00:00:00');
    const d = new Date();
    d.setHours(0, 0, 0, 0);
    return d;
}

function loadInstanceChart() {
    const d = _icSelectedDate();
    _icDayKey = _dpFmt(d.getFullYear(), d.getMonth(), d.getDate());
    const wrap = document.getElementById('tlInstanceChart');
    if (wrap && !wrap.querySelector('.tl-ic-list')) wrap.innerHTML = `<div class="empty-msg">${t('timeline.instance_chart.loading', 'Loading instances...')}</div>`;
    sendToCS({ action: 'getInstanceChart', date: _icDayKey });
}

function renderInstanceChart(payload) {
    if (!payload || payload.date !== _icDayKey) return;
    _icData = payload.events || [];
    _icRender();
}

function icNavDay(delta) {
    const d = _icSelectedDate();
    d.setDate(d.getDate() + delta);
    applyTlDateFilter(_dpFmt(d.getFullYear(), d.getMonth(), d.getDate()));
}

function _icRender() {
    const wrap = document.getElementById('tlInstanceChart');
    if (!wrap) return;

    const d = _icSelectedDate();
    const dayStart = new Date(d.getFullYear(), d.getMonth(), d.getDate(), 0, 0, 0, 0).getTime();
    const dayMs = 86400000;
    const now = Date.now();
    const isToday = _icDayKey === _todayDateStr();

    const bars = (_icData || []).map((e, i) => {
        const joinMs = Date.parse(e.timestamp);
        let leaveMs = e.leftAt ? Date.parse(e.leftAt) : now;
        if (!leaveMs || isNaN(leaveMs) || leaveMs < joinMs) leaveMs = isToday ? now : joinMs;
        const cj = Math.max(joinMs, dayStart);
        const cl = Math.min(leaveMs, dayStart + dayMs);
        const left = ((cj - dayStart) / dayMs) * 100;
        const width = Math.max(0.5, ((cl - cj) / dayMs) * 100);
        const color = IC_COLORS[_icHash(e.worldId || e.worldName) % IC_COLORS.length];
        return { e, i, left, width, color, joinMs, leaveMs, dur: Math.max(0, leaveMs - joinMs), visMs: Math.max(0, cl - cj) };
    });

    if (!bars.length) {
        wrap.innerHTML = `<div class="empty-msg">${t('timeline.instance_chart.empty', 'No instances visited on this day.')}</div>`;
        return;
    }

    const totalSec = Math.floor(bars.reduce((a, b) => a + b.visMs, 0) / 1000);
    const summary = `<div class="tl-ic-summary">${tf('timeline.instance_chart.summary', { count: bars.length, time: formatDuration(totalSec) }, '{count} instances · {time}')}</div>`;

    const axis = [0, 3, 6, 9, 12, 15, 18, 21, 24].map(h =>
        `<div class="tl-ic-tick" style="left:${(h / 24) * 100}%"><span>${esc(fmtTime(new Date(dayStart + h * 3600000)))}</span></div>`).join('');

    const track = bars.map(b =>
        `<div class="tl-ic-bar" style="left:${b.left}%;width:${b.width}%;background:${b.color};" title="${esc(b.e.worldName || b.e.worldId)} · ${esc(fmtTime(new Date(b.joinMs)))}" onclick="icOpenDetail(${b.i})"></div>`).join('');

    const list = bars.map(b => {
        const thumb = b.e.worldThumb ? `style="background-image:url('${cssUrl(imgThumb(b.e.worldThumb, 96))}')"` : '';
        const lt = b.e.leftAt ? esc(fmtTime(new Date(b.leaveMs))) : `<span class="tl-ic-live">${esc(t('timeline.instance_chart.ongoing', 'now'))}</span>`;
        const pc = (b.e.players || []).length;
        return `<div class="tl-ic-card" onclick="icOpenDetail(${b.i})">
            <div class="tl-ic-card-thumb" ${thumb}></div>
            <div class="tl-ic-card-main">
                <div class="tl-ic-card-name">${esc(b.e.worldName || b.e.worldId || t('timeline.instance_chart.unknown_world', 'Unknown World'))}</div>
                <div class="tl-ic-card-meta">${esc(fmtTime(new Date(b.joinMs)))} – ${lt} · ${esc(formatDuration(Math.floor(b.dur / 1000)))} · ${tf('timeline.instance_chart.player_count', { count: pc }, '{count} players')}</div>
                <div class="tl-ic-card-bar-wrap"><div class="tl-ic-card-bar" style="left:${b.left}%;width:${b.width}%;background:${b.color};"></div></div>
            </div>
            <span class="msi tl-ic-card-arrow">chevron_right</span>
        </div>`;
    }).join('');

    wrap.innerHTML = summary
        + `<div class="tl-ic-axis">${axis}</div>`
        + `<div class="tl-ic-track">${track}</div>`
        + `<div class="tl-ic-list">${list}</div>`;
}

function icOpenDetail(idx) {
    const e = (_icData || [])[idx];
    if (e && typeof openTlDetail === 'function') openTlDetail(e.id);
}
