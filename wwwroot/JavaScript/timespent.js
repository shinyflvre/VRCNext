/* === Time Spent — Tab 16 ===
 * Shows time spent in worlds and with persons, derived from instance_join timeline events.
 */

let _tsView        = 'worlds';  // 'worlds' | 'persons'
let _tsData        = null;      // last payload from backend
let _tsLoading     = false;
let _tsInited      = false;
let _tsWorldQuery  = '';
let _tsPersonQuery = '';

/* ── Tab activation ── */
document.documentElement.addEventListener('tabchange', () => {
    const tab16 = document.getElementById('tab16');
    if (tab16 && tab16.classList.contains('active') && !_tsInited) {
        _tsInited = true;
        tsLoad();
    }
});

function tsRefresh() {
    _tsData = null;
    tsLoad();
}

function tsLoad() {
    if (_tsLoading) return;
    _tsLoading = true;
    const icon = document.getElementById('tsRefreshIcon');
    if (icon) icon.classList.add('ts-spin');
    document.getElementById('tsList').innerHTML = '<div class="ts-loading"><span class="msi ts-spin" style="font-size:22px;color:var(--accent);">sync</span><span style="font-size:12px;color:var(--tx2);">Calculating stats…</span></div>';
    document.getElementById('tsSummary').innerHTML = '';
    sendToCS({ action: 'vrcGetTimeSpent' });
}

/* Called by messages.js when backend responds */
function tsOnData(payload) {
    _tsLoading = false;
    _tsData = payload;
    const icon = document.getElementById('tsRefreshIcon');
    if (icon) icon.classList.remove('ts-spin');

    // Enrich persons: mark those who are current friends
    const friendIds = new Set((vrcFriendsData || []).map(f => f.id));
    (_tsData.persons || []).forEach(p => { p.isFriend = friendIds.has(p.userId); });

    tsRender();
}

function tsSetView(view) {
    _tsView = view;
    document.getElementById('tsBtnWorlds')?.classList.toggle('active', view === 'worlds');
    document.getElementById('tsBtnPersons')?.classList.toggle('active', view === 'persons');
    if (_tsData) tsRender();
    else tsLoad();
}

/* ── Render ── */
function tsRender() {
    if (!_tsData) return;
    const tab = document.getElementById('tab16');
    if (!tab || !tab.classList.contains('active')) return;

    if (_tsView === 'worlds') tsRenderWorlds();
    else                      tsRenderPersons();
}

/* ── Helpers ── */
function tsFmtTime(seconds) {
    if (seconds < 1) return '0s';
    const d = Math.floor(seconds / 86400);
    const h = Math.floor((seconds % 86400) / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    const s = seconds % 60;
    if (d > 0) return `${d}d ${h}h ${m}m ${s}s`;
    if (h > 0) return `${h}h ${m}m ${s}s`;
    if (m > 0) return `${m}m ${s}s`;
    return `${s}s`;
}

function tsFmtTimeDH(seconds) {
    const d = Math.floor(seconds / 86400);
    const h = Math.floor((seconds % 86400) / 3600);
    if (d > 0) return `${d}d ${h}h`;
    return `${h}h`;
}

function tsFmtTimeLong(seconds) {
    return tsFmtTime(seconds);
}

/* ── Worlds ── */
function tsRenderWorlds() {
    const worlds   = _tsData.worlds || [];
    const totalSec = _tsData.totalSeconds || 0;
    const topWorld = worlds[0];

    document.getElementById('tsSummary').innerHTML = `
        <div class="ts-stat-row">
            <div class="ts-stat">
                <span class="msi ts-stat-icon">schedule</span>
                <div class="ts-stat-val">${tsFmtTimeDH(totalSec)}</div>
                <div class="ts-stat-label">Total VRChat Time</div>
            </div>
            <div class="ts-stat">
                <span class="msi ts-stat-icon">travel_explore</span>
                <div class="ts-stat-val">${worlds.length}</div>
                <div class="ts-stat-label">Unique Worlds</div>
            </div>
            <div class="ts-stat">
                <span class="msi ts-stat-icon">star</span>
                <div class="ts-stat-val">${topWorld ? esc(topWorld.worldName || 'Unknown') : '—'}</div>
                <div class="ts-stat-label">Favourite World</div>
            </div>
            <div class="ts-stat">
                <span class="msi ts-stat-icon">login</span>
                <div class="ts-stat-val">${worlds.reduce((s, w) => s + w.visits, 0)}</div>
                <div class="ts-stat-label">Total Joins</div>
            </div>
        </div>
        <div class="search-bar-row ts-search-bar">
            <span class="msi search-ico">search</span>
            <input type="text" class="search-input" placeholder="Filter worlds…"
                   value="${_tsWorldQuery.replace(/"/g, '&quot;')}"
                   oninput="_tsWorldQuery=this.value;tsRenderWorldItems()">
        </div>`;

    tsRenderWorldItems();
}

function tsRenderWorldItems() {
    const worlds = _tsData?.worlds || [];
    const q      = _tsWorldQuery.toLowerCase().trim();
    const list   = q ? worlds.filter(w => (w.worldName || '').toLowerCase().includes(q)) : worlds;

    if (worlds.length === 0) {
        document.getElementById('tsList').innerHTML = '<div class="ts-empty"><span class="msi" style="font-size:28px;color:var(--tx3);">travel_explore</span><div>No world data yet.</div></div>';
        return;
    }
    if (list.length === 0) {
        document.getElementById('tsList').innerHTML = '<div class="ts-empty"><span class="msi" style="font-size:28px;color:var(--tx3);">search_off</span><div>No worlds match your search.</div></div>';
        return;
    }

    const maxSec = list[0].seconds || 1;
    const rows = list.map((w, i) => {
        const pct   = Math.round((w.seconds / maxSec) * 100);
        const rank  = worlds.indexOf(w) + 1;
        const thumb = w.worldThumb
            ? `<img class="ts-item-thumb" src="${esc(w.worldThumb)}" onerror="this.style.display='none'">`
            : `<div class="ts-item-thumb ts-thumb-placeholder"><span class="msi" style="font-size:18px;color:var(--tx3);">travel_explore</span></div>`;
        const wClick = w.worldId ? `onclick="openWorldSearchDetail('${esc(w.worldId)}')" style="cursor:pointer"` : '';
        return `
        <div class="ts-item" ${wClick}>
            <div class="ts-item-rank">#${rank}</div>
            ${thumb}
            <div class="ts-item-body">
                <div class="ts-item-name">${esc(w.worldName || 'Unknown World')}</div>
                <div class="ts-item-meta">
                    <span class="msi" style="font-size:12px;color:var(--tx3);">login</span>
                    <span>${w.visits} visit${w.visits !== 1 ? 's' : ''}</span>
                </div>
                <div class="ts-bar-wrap">
                    <div class="ts-bar" style="width:${pct}%"></div>
                </div>
            </div>
            <div class="ts-item-time">${tsFmtTime(w.seconds)}</div>
        </div>`;
    }).join('');

    document.getElementById('tsList').innerHTML = `<div class="ts-items">${rows}</div>`;
}

/* ── Persons ── */
function tsRenderPersons() {
    const persons         = _tsData.persons || [];
    const friendCount     = persons.filter(p => p.isFriend).length;
    const strangerCount   = persons.length - friendCount;
    const totalWithOthers = persons.reduce((s, p) => s + p.seconds, 0);
    const topFriend       = persons.find(p => p.isFriend);
    const topStranger     = persons.find(p => !p.isFriend);

    document.getElementById('tsSummary').innerHTML = `
        <div class="ts-stat-row">
            <div class="ts-stat">
                <span class="msi ts-stat-icon">group</span>
                <div class="ts-stat-val">${persons.length}</div>
                <div class="ts-stat-label">Unique People</div>
            </div>
            <div class="ts-stat">
                <span class="msi ts-stat-icon" style="color:var(--ok);">person</span>
                <div class="ts-stat-val">${friendCount}</div>
                <div class="ts-stat-label">Friends</div>
            </div>
            <div class="ts-stat">
                <span class="msi ts-stat-icon" style="color:var(--cyan);">person_outline</span>
                <div class="ts-stat-val">${strangerCount}</div>
                <div class="ts-stat-label">Others</div>
            </div>
            <div class="ts-stat">
                <span class="msi ts-stat-icon">schedule</span>
                <div class="ts-stat-val">${tsFmtTimeDH(totalWithOthers)}</div>
                <div class="ts-stat-label">Total Social Time</div>
            </div>
        </div>
        ${topFriend || topStranger ? `
        <div class="ts-highlights">
            ${topFriend ? `<div class="ts-highlight ts-hl-friend">
                <span class="msi" style="font-size:13px;">favorite</span>
                <span>Most time with friend: <strong>${esc(topFriend.displayName)}</strong> — ${tsFmtTimeLong(topFriend.seconds)}</span>
            </div>` : ''}
            ${topStranger ? `<div class="ts-highlight ts-hl-stranger">
                <span class="msi" style="font-size:13px;">person_add</span>
                <span>Most time with someone new: <strong>${esc(topStranger.displayName)}</strong> — ${tsFmtTimeLong(topStranger.seconds)}</span>
            </div>` : ''}
        </div>` : ''}
        <div class="search-bar-row ts-search-bar">
            <span class="msi search-ico">search</span>
            <input type="text" class="search-input" placeholder="Filter persons…"
                   value="${_tsPersonQuery.replace(/"/g, '&quot;')}"
                   oninput="_tsPersonQuery=this.value;tsRenderPersonItems()">
        </div>`;

    tsRenderPersonItems();
}

function tsRenderPersonItems() {
    const persons = _tsData?.persons || [];
    const q       = _tsPersonQuery.toLowerCase().trim();
    const list    = q ? persons.filter(p => (p.displayName || p.userId || '').toLowerCase().includes(q)) : persons;

    if (persons.length === 0) {
        document.getElementById('tsList').innerHTML = '<div class="ts-empty"><span class="msi" style="font-size:28px;color:var(--tx3);">group</span><div>No person data yet.</div></div>';
        return;
    }
    if (list.length === 0) {
        document.getElementById('tsList').innerHTML = '<div class="ts-empty"><span class="msi" style="font-size:28px;color:var(--tx3);">search_off</span><div>No persons match your search.</div></div>';
        return;
    }

    const maxSec = list[0].seconds || 1;
    const rows = list.map((p) => {
        const pct    = Math.round((p.seconds / maxSec) * 100);
        const rank   = persons.indexOf(p) + 1;
        const isFr   = p.isFriend;
        const avatar = p.image
            ? `<img class="ts-item-avatar" src="${esc(p.image)}" onerror="this.style.display='none'">`
            : `<div class="ts-item-avatar ts-avatar-placeholder"><span class="msi" style="font-size:16px;color:var(--tx3);">person</span></div>`;
        const badge = isFr
            ? `<span class="vrcn-badge ok">Friend</span>`
            : `<span class="vrcn-badge cyan">New</span>`;
        return `
        <div class="ts-item" onclick="openFriendDetail('${esc(p.userId)}')" style="cursor:pointer">
            <div class="ts-item-rank">#${rank}</div>
            <div class="ts-avatar-wrap">${avatar}</div>
            <div class="ts-item-body">
                <div class="ts-item-name">${esc(p.displayName || p.userId)} ${badge}</div>
                <div class="ts-item-meta">
                    <span class="msi" style="font-size:12px;color:var(--tx3);">handshake</span>
                    <span>${p.meets} encounter${p.meets !== 1 ? 's' : ''}</span>
                </div>
                <div class="ts-bar-wrap">
                    <div class="ts-bar ${isFr ? 'ts-bar-friend' : 'ts-bar-stranger'}" style="width:${pct}%"></div>
                </div>
            </div>
            <div class="ts-item-time">${tsFmtTime(p.seconds)}</div>
        </div>`;
    }).join('');

    document.getElementById('tsList').innerHTML = `<div class="ts-items">${rows}</div>`;
}
