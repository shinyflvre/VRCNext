/* === VRChat API === */
function vrcQuickLogin() {
    const u = document.getElementById('vrcQuickUser').value, p = document.getElementById('vrcQuickPass').value;
    if (!u || !p) return;
    document.getElementById('vrcQuickError').textContent = t('profiles.login.connecting', 'Connecting...');
    sendToCS({ action: 'vrcLogin', username: u, password: p });
}

function vrcLogout() {
    sendToCS({ action: 'vrcLogout' });
    currentVrcUser = null;
}



function statusDotClass(s) {
    if (!s) return 's-offline';
    const sl = s.toLowerCase();
    if (sl === 'active' || sl === 'online') return 's-active';
    if (sl === 'join me') return 's-join';
    if (sl === 'ask me' || sl === 'look me') return 's-ask';
    if (sl === 'busy' || sl === 'do not disturb') return 's-busy';
    return 's-offline';
}

function statusLabel(s) {
    if (!s) return t('status.offline', 'Offline');
    const sl = s.toLowerCase();
    const m = {
        'active': t('status.online', 'Online'),
        'online': t('status.online', 'Online'),
        'join me': t('status.join_me', 'Join Me'),
        'ask me': t('status.ask_me', 'Ask Me'),
        'look me': t('status.ask_me', 'Ask Me'),
        'busy': t('status.do_not_disturb', 'Do Not Disturb'),
        'do not disturb': t('status.do_not_disturb', 'Do Not Disturb'),
        'offline': t('status.offline', 'Offline')
    };
    return m[sl] || s;
}

function getFriendLocationLabel(presenceType, location) {
    const isPrivate = !location || location === 'private';
    const isOffline = location === 'offline' || presenceType === 'offline';
    if (isOffline) return t('status.offline', 'Offline');
    if (presenceType === 'web') return t('profiles.friends.location.web', 'Web / Mobile');
    if (isPrivate) return t('profiles.friends.location.private', 'Private Instance');
    const { worldId } = parseFriendLocation(location);
    const cached = worldId && (typeof dashWorldCache !== 'undefined') ? dashWorldCache[worldId] : null;
    return cached?.name || t('profiles.friends.location.world', 'In World');
}

function getFriendSectionLabel(section, count) {
    const map = {
        favorites: ['profiles.friends.sections.favorites', 'FAVORITES - {count}'],
        ingame: ['profiles.friends.sections.in_game', 'IN-GAME - {count}'],
        web: ['profiles.friends.sections.web', 'WEB / ACTIVE - {count}'],
        offline: ['profiles.friends.sections.offline', 'OFFLINE - {count}'],
        onlineZero: ['profiles.friends.sections.online_zero', 'ONLINE - {count}']
    };
    const entry = map[section];
    if (!entry) return `${count}`;
    return tf(entry[0], { count }, entry[1]);
}



function getStatusText(status, description) {
    return `${statusLabel(status)}${description ? ' - ' + esc(description) : ''}`;
}

function getGroupMemberText(memberCount, fallbackToGroup = true) {
    if (memberCount) return tf('worlds.groups.members', { count: memberCount }, '{count} members');
    return fallbackToGroup ? t('groups.common.group', 'Group') : '';
}

// Profile helpers
function formatDuration(totalSec) {
    if (totalSec < 1) return '0s';
    const d = Math.floor(totalSec / 86400);
    const h = Math.floor((totalSec % 86400) / 3600);
    const m = Math.floor((totalSec % 3600) / 60);
    const s = totalSec % 60;
    if (d > 0) return `${d}d ${h}h ${m}m ${s}s`;
    if (h > 0) return `${h}h ${m}m ${s}s`;
    if (m > 0) return `${m}m ${s}s`;
    return `${s}s`;
}

function formatLastSeen(apiLastLogin, localLastSeen) {
    let best = null;
    if (apiLastLogin) {
        const d = new Date(apiLastLogin);
        if (!isNaN(d)) best = d;
    }
    if (localLastSeen) {
        const d = new Date(localLastSeen);
        if (!isNaN(d) && (!best || d > best)) best = d;
    }
    if (!best) return '';
    const now = new Date();
    const diff = now - best;
    if (diff < 60000) return t('profiles.last_seen.just_now', 'Just now');
    if (diff < 3600000) return tf('profiles.last_seen.minutes_ago', { count: Math.floor(diff / 60000) }, '{count}m ago');
    if (diff < 86400000) return tf('profiles.last_seen.hours_ago', { count: Math.floor(diff / 3600000) }, '{count}h ago');
    if (diff < 604800000) return tf('profiles.last_seen.days_ago', { count: Math.floor(diff / 86400000) }, '{count}d ago');
    return fmtShortDate(best);
}


// Trust rank from tags (offset by 1 in API naming)
function getTrustRank(tags) {
    if (!tags || !Array.isArray(tags) || tags.length === 0) return null;
    // Order matters: check highest first
    if (tags.includes('system_trust_legend')) return { label: t('profiles.trust.trusted', 'Trusted User'), color: 'var(--bdg-rank-trusted)', cls: 'rank-trusted' };
    if (tags.includes('system_trust_veteran')) return { label: t('profiles.trust.trusted', 'Trusted User'), color: 'var(--bdg-rank-trusted)', cls: 'rank-trusted' };
    if (tags.includes('system_trust_trusted')) return { label: t('profiles.trust.known', 'Known User'), color: 'var(--bdg-rank-known)', cls: 'rank-known' };
    if (tags.includes('system_trust_known'))   return { label: t('profiles.trust.user', 'User'), color: 'var(--bdg-rank-user)', cls: 'rank-user' };
    if (tags.includes('system_trust_basic'))   return { label: t('profiles.trust.new', 'New User'), color: 'var(--bdg-rank-new)', cls: 'rank-new' };
    return { label: t('profiles.trust.visitor', 'Visitor'), color: 'var(--bdg-rank-visitor)', cls: 'rank-visitor' };
}


function getPlatformInfo(hostname) {
    const h = hostname.replace('www.', '');
    if (h.includes('twitter.com') || h.includes('x.com'))          return { key: 'twitter',   label: 'Twitter/X' };
    if (h.includes('instagram.com'))                                 return { key: 'instagram', label: 'Instagram' };
    if (h.includes('tiktok.com'))                                    return { key: 'tiktok',    label: 'TikTok' };
    if (h.includes('youtube.com') || h.includes('youtu.be'))        return { key: 'youtube',   label: 'YouTube' };
    if (h.includes('discord.gg') || h.includes('discord.com'))      return { key: 'discord',   label: 'Discord' };
    if (h.includes('github.com'))                                    return { key: 'github',    label: 'GitHub' };
    if (h.includes('facebook.com') || h.includes('fb.com'))         return { key: 'facebook',  label: 'Facebook' };
    if (h.includes('twitch.tv'))                                     return { key: 'twitch',    label: 'Twitch' };
    if (h.includes('bsky.app'))                                      return { key: 'bluesky',   label: 'Bluesky' };
    if (h.includes('pixiv.net'))                                     return { key: 'pixiv',     label: 'Pixiv' };
    if (h.includes('ko-fi.com'))                                     return { key: 'kofi',      label: 'Ko-fi' };
    if (h.includes('patreon.com'))                                   return { key: 'patreon',   label: 'Patreon' };
    if (h.includes('booth.pm'))                                      return { key: 'booth',     label: 'Booth' };
    if (h.includes('vrchat.com') || h.includes('vrc.group'))        return { key: 'vrchat',    label: 'VRChat' };
    return { key: null, label: h };
}

// Bio link to SVG brand icon and label
function renderBioLink(url) {
    let platformSvg = '';
    let label = t('profiles.common.link', 'Link');
    try {
        const h = new URL(url).hostname;
        const info = getPlatformInfo(h);
        label = info.label;
        if (info.key && PLATFORM_ICONS[info.key]) {
            platformSvg = `<svg viewBox="0 0 24 24" width="14" height="14" fill="currentColor" style="flex-shrink:0"><path d="${PLATFORM_ICONS[info.key].svg}"/></svg>`;
        } else {
            platformSvg = `<span class="msi" style="font-size:14px;">link</span>`;
        }
    } catch {
        label = t('profiles.common.link', 'Link');
        platformSvg = `<span class="msi" style="font-size:14px;">link</span>`;
    }
    const safeUrl = esc(url);
    const safeUrlJs = jsq(url);
    return `<button class="fd-bio-link" onclick="sendToCS({action:'openUrl',url:'${safeUrlJs}'})" title="${safeUrl}">${platformSvg}<span>${esc(label)}</span></button>`;
}



// People Tab: Favorites / Search / Blocked / Muted


// === People Stats Overlay (time spent + meets bar on every user item) ===
window._peopleStatsMode = false;
let _peopleStatsMap = {};
let _peopleStatsMax = 1;
let _peopleStatsLoaded = false;

const PEOPLE_STATS_AREAS = ['peopleFavArea', 'peopleAllArea', 'peopleRecentArea', 'peopleSearchArea', 'peopleBlockedArea', 'peopleMutedArea'];

function _peopleStatsFmtTime(seconds) {
    const d = Math.floor(seconds / 86400);
    const h = Math.floor((seconds % 86400) / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    const ds = t('timespent.unit.day_short', 'd');
    const hs = t('timespent.unit.hour_short', 'h');
    const ms = t('timespent.unit.minute_short', 'm');
    if (d > 0) return `${d}${ds}`;
    if (h > 0) return `${h}${hs}`;
    return `${m}${ms}`;
}

function buildPeopleStatsRow(userId) {
    if (!_peopleStatsLoaded) {
        return `<div class="vrcn-user-item-stats vrcn-user-item-stats-pending"><div class="vrcn-user-item-stats-bar-wrap"><div class="vrcn-user-item-stats-bar" style="width:0%"></div></div></div>`;
    }
    const st = _peopleStatsMap[userId];
    const sec = st ? st.seconds : 0;
    const meets = st ? st.meets : 0;
    if (sec <= 0) {
        return `<div class="vrcn-user-item-stats vrcn-user-item-stats-empty"><div class="vrcn-user-item-stats-meta"><span class="msi">schedule</span><span>${t('profiles.people.stats.no_data', 'No data')}</span></div><div class="vrcn-user-item-stats-bar-wrap"><div class="vrcn-user-item-stats-bar" style="width:0%"></div></div></div>`;
    }
    const maxSec = _peopleStatsMax > 0 ? _peopleStatsMax : 1;
    const pct = Math.max(3, Math.round((sec / maxSec) * 100));
    return `<div class="vrcn-user-item-stats">
        <div class="vrcn-user-item-stats-meta">
            <span class="msi">schedule</span><span>${_peopleStatsFmtTime(sec)}</span>
            <span class="vrcn-user-item-stats-dot">&middot;</span>
            <span class="msi">handshake</span><span>${meets}</span>
        </div>
        <div class="vrcn-user-item-stats-bar-wrap"><div class="vrcn-user-item-stats-bar" style="width:${pct}%"></div></div>
    </div>`;
}

function applyPeopleStatsBars() {
    PEOPLE_STATS_AREAS.forEach(cid => {
        const c = document.getElementById(cid);
        if (!c) return;
        c.querySelectorAll('.vrcn-user-item[data-uid]').forEach(item => {
            const existing = item.querySelector('.vrcn-user-item-stats');
            if (existing) existing.remove();
            if (!window._peopleStatsMode) return;
            const info = item.querySelector('.vrcn-user-item-info');
            if (info) info.insertAdjacentHTML('afterend', buildPeopleStatsRow(item.getAttribute('data-uid')));
        });
    });
}

function togglePeopleStats() {
    window._peopleStatsMode = !window._peopleStatsMode;
    const btn = document.getElementById('peopleStatsBtn');
    if (btn) btn.classList.toggle('active', window._peopleStatsMode);
    if (window._peopleStatsMode && !_peopleStatsLoaded) sendToCS({ action: 'vrcGetPeopleStats' });
    applyPeopleStatsBars();
}

function applyPeopleAlwaysStats() {
    const always = (typeof settings !== 'undefined' && settings) ? !!settings.peopleAlwaysStats : false;
    const btn = document.getElementById('peopleStatsBtn');
    if (btn) btn.style.display = always ? 'none' : '';
    if (always) {
        window._peopleStatsMode = true;
        if (btn) btn.classList.add('active');
        if (!_peopleStatsLoaded) sendToCS({ action: 'vrcGetPeopleStats' });
    }
    applyPeopleStatsBars();
}

function onPeopleStatsData(payload) {
    const list = (payload && payload.stats) || [];
    _peopleStatsMap = {};
    let max = 1;
    list.forEach(s => {
        _peopleStatsMap[s.userId] = { seconds: s.seconds || 0, meets: s.meets || 0 };
        if ((s.seconds || 0) > max) max = s.seconds;
    });
    _peopleStatsMax = max;
    _peopleStatsLoaded = true;
    _plStatsPending = false;
    if (window._peopleStatsMode) applyPeopleStatsBars();
    if (typeof window._fpOnStatsLoaded === 'function') window._fpOnStatsLoaded();
    _plLiveRerender();
}

function _plGridId() {
    return {
        favorites: 'favFriendsGrid', all: 'allFriendsGrid', recentseen: 'recentSeenGrid',
        search: 'searchPeopleResults', blocked: 'blockedList', muted: 'mutedList',
        instance: 'instancePlayersGrid',
    }[peopleFilter] || '';
}

function _plKeepScroll(gridId, render) {
    const el = document.getElementById(gridId);
    if (!el || !_peopleListMode() || typeof lvKeepScroll !== 'function') { render(); return; }
    lvKeepScroll(el, render);
}

function _plLiveRerender() {
    const tab = document.getElementById('tab3');
    if (!tab || !tab.classList.contains('active')) return;
    if (peopleFilter === 'instance') { renderInstancePlayers(); return; }
    if (!_peopleListMode()) return;
    lvKeepScroll(document.getElementById(_plGridId()), () => renderPeopleListView());
}

function _plEnsureStats() {
    if (_peopleStatsLoaded || _plStatsPending) return;
    _plStatsPending = true;
    sendToCS({ action: 'vrcGetPeopleStats' });
}

let _plStatsPending = false;

window.getPeopleStat = function (userId) { return _peopleStatsMap[userId] || null; };
window.requestPeopleStats = function () { if (!_peopleStatsLoaded) sendToCS({ action: 'vrcGetPeopleStats' }); };
window.fmtPeopleStatTime = function (seconds) { return _peopleStatsFmtTime(seconds); };

function setPeopleFilter(filter) {
    if (_favFriendEditMode) exitFriendEditMode();
    _peopleAllPage = 0; _peopleBlockedPage = 0; _peopleMutedPage = 0; _peopleFavPage = 0;
    peopleFilter = filter;
    _peopleRecentPage = 0;
    const viewBtns = document.getElementById('peopleViewBtns');
    if (viewBtns) viewBtns.style.display = filter === 'instance' ? 'none' : 'flex';
    ['peopleFavPaginatorBar', 'peopleAllPaginatorBar', 'peopleRecentPaginatorBar', 'peopleBlockedPaginatorBar', 'peopleMutedPaginatorBar']
        .forEach(id => setPaginator(id, ''));
    document.getElementById('peopleFilterFav').classList.toggle('active', filter === 'favorites');
    document.getElementById('peopleFilterAll').classList.toggle('active', filter === 'all');
    document.getElementById('peopleFilterInstance')?.classList.toggle('active', filter === 'instance');
    document.getElementById('peopleFilterRecent').classList.toggle('active', filter === 'recentseen');
    document.getElementById('peopleFilterSearch').classList.toggle('active', filter === 'search');
    document.getElementById('peopleFilterBlocked').classList.toggle('active', filter === 'blocked');
    document.getElementById('peopleFilterMuted').classList.toggle('active', filter === 'muted');
    document.getElementById('peopleFavArea').style.display     = filter === 'favorites'  ? '' : 'none';
    document.getElementById('peopleAllArea').style.display     = filter === 'all'        ? '' : 'none';
    const instArea = document.getElementById('peopleInstanceArea');
    if (instArea) instArea.style.display = filter === 'instance' ? '' : 'none';
    document.getElementById('peopleRecentArea').style.display  = filter === 'recentseen' ? '' : 'none';
    document.getElementById('peopleSearchArea').style.display  = filter === 'search'     ? '' : 'none';
    document.getElementById('peopleBlockedArea').style.display = filter === 'blocked'    ? '' : 'none';
    document.getElementById('peopleMutedArea').style.display   = filter === 'muted'      ? '' : 'none';
    ['peopleAllPaginatorBar', 'peopleBlockedPaginatorBar', 'peopleMutedPaginatorBar'].forEach(id => {
        const bar = document.getElementById(id);
        if (bar) bar.innerHTML = '';
    });
    const editBtn = document.getElementById('favFriendEditModeBtn');
    if (editBtn) editBtn.style.display = (filter === 'favorites' || filter === 'all') ? '' : 'none';
    _pplUpdateCounts();
    refreshPeopleTab();
}

const _PL_PROFILE_FACTS = ['dateJoined', 'pronouns', 'lastLogin', 'lastActivity', 'bioLinks', 'lastSeen'];

let _plFactsRerender = null;

function applyFriendFacts(u) {
    if (!u || !u.id || typeof vrcFriendsData === 'undefined') return;
    const f = vrcFriendsData.find(x => x.id === u.id);
    if (!f) return;
    let changed = false;
    ['dateJoined', 'pronouns'].forEach(k => {
        if (u[k] && f[k] !== u[k]) { f[k] = u[k]; changed = true; }
    });
    ['mutualFriends', 'mutualGroups'].forEach(k => {
        if (typeof u[k] === 'number' && f[k] !== u[k]) { f[k] = u[k]; changed = true; }
    });
    if (!changed || _plFactsRerender) return;
    _plFactsRerender = setTimeout(() => {
        _plFactsRerender = null;
        _plLiveRerender();
    }, 600);
}

function patchFriendProfileFacts(u) {
    if (!u || !u.id || typeof vrcFriendsData === 'undefined') return;
    const f = vrcFriendsData.find(x => x.id === u.id);
    if (!f) return;
    let changed = false;
    if (Array.isArray(u.mutuals) && !u.mutualsOptedOut && f.mutualFriends !== u.mutuals.length) {
        f.mutualFriends = u.mutuals.length;
        changed = true;
    }
    if (Array.isArray(u.mutualGroups) && f.mutualGroups !== u.mutualGroups.length) {
        f.mutualGroups = u.mutualGroups.length;
        changed = true;
    }
    _PL_PROFILE_FACTS.forEach(k => {
        const v = u[k];
        const hasValue = Array.isArray(v) ? v.length > 0 : !!v;
        if (!hasValue) return;
        if (JSON.stringify(f[k]) === JSON.stringify(v)) return;
        f[k] = v;
        changed = true;
    });
    if (!changed) return;
    _plLiveRerender();
}

function refreshPeopleTab(force) {
    if (force) { _peopleStatsLoaded = false; _plStatsPending = false; _plEnsureStats(); }
    if (typeof applyPeopleAlwaysStats === 'function') applyPeopleAlwaysStats();
    if (peopleFilter === 'favorites') {
        filterFavFriends();
        sendToCS({ action: 'vrcGetFavoriteFriends' });
    }
    if (peopleFilter === 'all') {
        if (force) sendToCS({ action: 'vrcRefreshFriends' });
        else filterAllFriends();
    }
    if (peopleFilter === 'instance') { renderInstancePlayers(); instancePlayersSyncTicker(); }
    if (peopleFilter === 'recentseen') {
        filterRecentSeen();
        sendToCS({ action: 'vrcGetRecentSeen' });
    }
    if (peopleFilter === 'blocked') {
        if (typeof blockedData !== 'undefined' && blockedData) renderModList('blockedList', blockedData, 'block');
        sendToCS({ action: 'vrcGetBlocked' });
    }
    if (peopleFilter === 'muted') {
        if (typeof mutedData !== 'undefined' && mutedData) renderModList('mutedList', mutedData, 'mute');
        sendToCS({ action: 'vrcGetMuted' });
    }
    if (peopleFilter === 'search') renderPeopleListView();
}

let _recentSeenData = [];
let _peopleRecentPage = 0;
let _pplFavLoaded = false;
let _pplRecentLoaded = false;

function _pplUpdateCounts() {
    const inst = (typeof currentInstanceData !== 'undefined') ? currentInstanceData : null;
    let instUsers = null;
    if (inst && !inst.error) instUsers = (inst.empty || typeof _ipUsers !== 'function') ? [] : _ipUsers().users;
    const counts = {
        peopleFilterFavCount: _pplFavLoaded ? favFriendsData : null,
        peopleFilterAllCount: (typeof vrcFriendsLoaded !== 'undefined' && vrcFriendsLoaded) ? vrcFriendsData : null,
        peopleFilterInstanceCount: instUsers,
        peopleFilterRecentCount: _pplRecentLoaded ? _recentSeenData : null,
        peopleFilterBlockedCount: (typeof blockedData !== 'undefined') ? blockedData : null,
        peopleFilterMutedCount: (typeof mutedData !== 'undefined') ? mutedData : null
    };
    Object.keys(counts).forEach(id => {
        const el = document.getElementById(id);
        if (el) el.textContent = Array.isArray(counts[id]) ? String(counts[id].length) : 'X';
    });
}

function renderRecentSeen(players) {
    _recentSeenData = Array.isArray(players) ? players : [];
    _pplRecentLoaded = true;
    _pplUpdateCounts();
    filterRecentSeen();
}

function peopleRecentGoPage(page) {
    if (page < 0) return;
    _peopleRecentPage = page;
    filterRecentSeen();
    document.getElementById('recentSeenGrid')?.scrollTo(0, 0);
}

function _plMergeFriend(p) {
    const f = vrcFriendsData.find(x => x.id === p.id);
    if (!f) return p;
    return { ...f, image: f.image || p.image, displayName: f.displayName || p.displayName };
}

function filterRecentSeen() {
    const el = document.getElementById('recentSeenGrid');
    if (!el) return;
    const q = (document.getElementById('recentSeenSearchInput')?.value || '').toLowerCase();
    const list = q
        ? _recentSeenData.filter(p => (p.displayName || '').toLowerCase().includes(q) || (p.id || '').toLowerCase().includes(q))
        : _recentSeenData;
    const listMode = _peopleListMode();
    el.classList.toggle('search-grid', !listMode);
    if (!list.length) {
        el.innerHTML = q ? emptyStateHtml('search', t('profiles.people.no_results', 'No results')) : emptyStateHtml('history', t('profiles.people.recent_seen.empty', 'No recently seen players'));
        plSetPaginator('peopleRecentPaginatorBar', '');
        return;
    }
    if (listMode) {
        const sorted = _plSort(list.map(_plMergeFriend));
        const totalPages = Math.ceil(sorted.length / peopleListPageSize) || 1;
        if (_peopleRecentPage >= totalPages) _peopleRecentPage = totalPages - 1;
        if (_peopleRecentPage < 0) _peopleRecentPage = 0;
        const page = _peopleRecentPage;
        el.innerHTML = buildPeopleListHtml(sorted.slice(page * peopleListPageSize, (page + 1) * peopleListPageSize));
        plSetPaginator('peopleRecentPaginatorBar', plPaginator(page, totalPages, 'peopleRecentGoPage', sorted.length));
        return;
    }
    plSetPaginator('peopleRecentPaginatorBar', '');
    el.innerHTML = list.map(p => renderUserItem(p, `openFriendDetail('${jsq(p.id)}')`)).join('');
}

function _allFriendCategory(f) {
    const pres = f.presence || '';
    if (!pres || pres === 'offline') return 'offline';
    if (pres === 'web') return 'active';
    return 'ingame';
}

function setAllFriendsStatusFilter(filter) {
    _allFriendsStatusFilter = filter;
    _peopleAllPage = 0;
    ['all', 'ingame', 'active', 'offline'].forEach(k => {
        const btn = document.getElementById('allFriendFilter' + k.charAt(0).toUpperCase() + k.slice(1));
        if (btn) btn.classList.toggle('active', k === filter);
    });
    filterAllFriends();
}

let peopleViewMode = localStorage.getItem('vrcn_peopleView') === 'list' ? 'list' : 'grid';

const PL_PAGE_SIZES = [10, 15, 20, 25, 50, 100];
const PL_PAGE_SIZE_KEY = 'vrcn_people_page_size';
let peopleListPageSize = (() => {
    try {
        const v = parseInt(localStorage.getItem(PL_PAGE_SIZE_KEY), 10);
        return PL_PAGE_SIZES.includes(v) ? v : 25;
    } catch { return 25; }
})();

function plPageSizeSelectHtml() {
    const opts = PL_PAGE_SIZES.map(n => `<option value="${n}"${n === peopleListPageSize ? ' selected' : ''}>${n}</option>`).join('');
    return `<select class="vrcn-dropdown tl-page-size" onchange="setPeopleListPageSize(this.value)" title="${esc(t('timeline.page_size', 'Entries per page'))}">${opts}</select>`;
}

function setPeopleListPageSize(value) {
    const n = parseInt(value, 10);
    if (!PL_PAGE_SIZES.includes(n) || n === peopleListPageSize) return;
    peopleListPageSize = n;
    try { localStorage.setItem(PL_PAGE_SIZE_KEY, String(n)); } catch {}
    _peopleAllPage = 0;
    _peopleFavPage = 0;
    _peopleRecentPage = 0;
    _peopleBlockedPage = 0;
    _peopleMutedPage = 0;
    renderPeopleListView();
}

function plPaginator(page, totalPages, onPageFn, total) {
    const bar = buildPaginator(page, totalPages, onPageFn,
        `<span style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx2);padding:0 8px;">${total.toLocaleString()} total</span>`);
    return plPageSizeSelectHtml() + (bar || `<span style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx2);padding:0 8px;">${total.toLocaleString()} total</span>`);
}

document.getElementById('peopleViewGrid')?.classList.toggle('active', peopleViewMode === 'grid');
document.getElementById('peopleViewList')?.classList.toggle('active', peopleViewMode === 'list');

function setPeopleViewMode(mode) {
    peopleViewMode = mode === 'list' ? 'list' : 'grid';
    localStorage.setItem('vrcn_peopleView', peopleViewMode);
    document.getElementById('peopleViewGrid')?.classList.toggle('active', peopleViewMode === 'grid');
    document.getElementById('peopleViewList')?.classList.toggle('active', peopleViewMode === 'list');
    _peopleAllPage = 0; _peopleFavPage = 0; _peopleRecentPage = 0;
    _peopleBlockedPage = 0; _peopleMutedPage = 0;
    renderPeopleListView();
}

function _plLangs(tags) {
    if (!Array.isArray(tags)) return [];
    return tags.filter(x => typeof x === 'string' && x.startsWith('language_'));
}

function _plLangCell(f) {
    const langs = _plLangs(f.tags);
    if (!langs.length) return '';
    return langs.slice(0, 3).map(k => {
        const name = (typeof LANG_MAP !== 'undefined' && LANG_MAP[k]) ? LANG_MAP[k] : k.replace('language_', '').toUpperCase();
        const flag = (typeof LANG_FLAG !== 'undefined' && LANG_FLAG[k]) ? LANG_FLAG[k] : '';
        return `<span class="pl-lang" title="${esc(name)}">${flag ? esc(flag) : esc(name)}</span>`;
    }).join('');
}

function _plBioLinksCell(f) {
    const links = Array.isArray(f.bioLinks) ? f.bioLinks.filter(Boolean) : [];
    if (!links.length) return '';
    return links.slice(0, 4).map(l => {
        let host = '';
        try { host = new URL(l).hostname; } catch { host = l; }
        const info = (typeof getPlatformInfo === 'function') ? getPlatformInfo(host) : null;
        const icon = info && typeof PLATFORM_ICONS !== 'undefined' && PLATFORM_ICONS[info.key]
            ? `<svg viewBox="0 0 24 24" class="pl-bio-ico"><path d="${PLATFORM_ICONS[info.key].svg}"/></svg>`
            : `<span class="msi pl-bio-ico">link</span>`;
        return `<span class="pl-bio-link" title="${esc(l)}" onclick="event.stopPropagation();sendToCS({action:'openUrl',url:'${jsq(l)}'})">${icon}</span>`;
    }).join('');
}

function _plTimeSpent(seconds) {
    const d = Math.floor(seconds / 86400);
    const h = Math.floor((seconds % 86400) / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    const ds = t('timespent.unit.day_short', 'd');
    const hs = t('timespent.unit.hour_short', 'h');
    const ms = t('timespent.unit.minute_short', 'm');
    const parts = [];
    if (d > 0) parts.push(`${d}${ds}`);
    if (h > 0) parts.push(`${h}${hs}`);
    if (m > 0 && d === 0) parts.push(`${m}${ms}`);
    return parts.length ? parts.join(' ') : `0${ms}`;
}

function _plDate(v) {
    if (!v) return '';
    const d = new Date(v);
    return isNaN(d) ? '' : fmtShortDate(d);
}

function _plDateTime(v) {
    if (!v) return '';
    const d = new Date(v);
    return isNaN(d) ? '' : `${fmtShortDate(d)} | ${fmtTime(d)}`;
}

function _plSortValue(f, field) {
    const st = _peopleStatsMap[f.id];
    switch (field) {
        case 'name':      return (f.displayName || '').toLowerCase();
        case 'rank':      return _PL_RANK_ORDER.indexOf((getTrustRank(f.tags || [])?.cls) || '');
        case 'status':    return _allFriendCategory(f) + '|' + (f.status || '');
        case 'language':  return (_plLangs(f.tags)[0] || '');
        case 'biolinks':  return (Array.isArray(f.bioLinks) ? f.bioLinks.filter(Boolean).length : 0);
        case 'pronouns':  return (f.pronouns || '').toLowerCase();
        case 'mutualfriends': return f.mutualFriends || 0;
        case 'mutualgroups':  return f.mutualGroups || 0;
        case 'meets':     return st ? (st.meets || 0) : 0;
        case 'timespent': return st ? (st.seconds || 0) : 0;
        case 'joined':    return f.dateJoined || '';
        case 'lastlogin': return f.lastLogin || '';
        case 'lastseen':  return f.lastSeen || '';
        case 'profile':   return (f.displayName || '').toLowerCase();
        default:          return (f.displayName || '').toLowerCase();
    }
}

const _PL_RANK_ORDER = ['rank-visitor', 'rank-new', 'rank-user', 'rank-known', 'rank-trusted'];

function _peopleListMode() {
    return peopleViewMode === 'list' && typeof tlTableHtml === 'function' && typeof tlTableRow === 'function';
}

function _plSort(list) {
    const { field, dir } = tlTableSortField('friendsList');
    const mul = dir === 'asc' ? 1 : -1;
    return [...list].sort((a, b) => {
        const va = _plSortValue(a, field), vb = _plSortValue(b, field);
        if (typeof va === 'number' && typeof vb === 'number') return (va - vb) * mul;
        return String(va).localeCompare(String(vb)) * mul;
    });
}

function buildPeopleListHtml(friends, staticHeader) {
    _plEnsureStats();
    let rows = '';
    friends.forEach(f => {
        const uid = jsq(f.id || '');
        const img = f.image || '';
        const av = img
            ? `<span class="pl-av" style="background-image:url('${cssUrl(imgThumb(img, 64))}')"></span>`
            : `<span class="pl-av pl-av-letter">${esc((f.displayName || '?')[0].toUpperCase())}</span>`;
        const rank = getTrustRank(f.tags || []);
        const st = _peopleStatsMap[f.id];
        const cat = f.presence ? _allFriendCategory(f) : '';
        const dotKind = f.presence === 'web' ? 'vrc-status-ring' : 'vrc-status-dot';
        const dotCls = (f.pendingOffline || cat === 'offline') ? 's-offline' : statusDotClass(f.status);
        const statusTxt = f.pendingOffline
            ? t('profiles.friends.pending_offline', 'Pending offline...')
            : f.traveling
                ? t('profiles.friends.traveling', 'Traveling...')
                : (f.statusDescription || statusLabel(f.status));
        const statusCell = f.status
            ? `<span class="${dotKind} ${dotCls}"></span><span class="pl-status-txt">${esc(statusTxt)}</span>`
            : '';

        rows += tlTableRow('friendsList', ` data-uid="${esc(f.id || '')}" onclick="openFriendDetail('${uid}')"`, {
            profile:   `<td class="pl-profile">${av}</td>`,
            name:      `<td class="pl-name">${esc(f.displayName || '')}</td>`,
            rank:      `<td>${rank ? `<span class="vrcn-badge ${rank.cls}">${esc(rank.label)}</span>` : ''}</td>`,
            status:    `<td class="pl-status">${statusCell}</td>`,
            language:  `<td class="pl-langs">${_plLangCell(f)}</td>`,
            biolinks:  `<td class="pl-bios">${_plBioLinksCell(f)}</td>`,
            pronouns:  `<td class="lv-sub">${esc(f.pronouns || '')}</td>`,
            mutualfriends: `<td class="pl-num">${f.mutualFriends ? esc(String(f.mutualFriends)) : ''}</td>`,
            mutualgroups:  `<td class="pl-num">${f.mutualGroups ? esc(String(f.mutualGroups)) : ''}</td>`,
            meets:     `<td class="pl-num">${st && st.meets ? esc(String(st.meets)) : ''}</td>`,
            timespent: `<td class="pl-num">${st && st.seconds > 0 ? esc(_plTimeSpent(st.seconds)) : ''}</td>`,
            joined:    `<td class="pl-date">${esc(_plDate(f.dateJoined))}</td>`,
            lastlogin: `<td class="pl-date">${esc(_plDateTime(f.lastLogin))}</td>`,
            lastseen:  `<td class="pl-date">${esc(_plDateTime(f.lastSeen))}</td>`,
        });
    });
    return tlTableHtml('friendsList', rows, staticHeader);
}

const _PL_BAR_FILTER = {
    peopleFavPaginatorBar:     'favorites',
    peopleAllPaginatorBar:     'all',
    peopleRecentPaginatorBar:  'recentseen',
    peopleBlockedPaginatorBar: 'blocked',
    peopleMutedPaginatorBar:   'muted',
};

function plSetPaginator(id, html) {
    setPaginator(id, _PL_BAR_FILTER[id] === peopleFilter ? html : '');
}

function renderPeopleListView() {
    if (peopleFilter === 'instance')        renderInstancePlayers();
    else if (peopleFilter === 'favorites')  filterFavFriends();
    else if (peopleFilter === 'recentseen') filterRecentSeen();
    else if (peopleFilter === 'blocked')    filterModList('block');
    else if (peopleFilter === 'muted')      filterModList('mute');
    else if (peopleFilter === 'search') {
        const st = searchState?.people;
        if (st && st.results && st.results.length) renderSearchResults('users', st.results, 0, st.hasMore);
    }
    else filterAllFriends();
}

let _peopleFavPage = 0;
function peopleFavGoPage(page) {
    if (page < 0) return;
    _peopleFavPage = page;
    filterFavFriends();
    document.getElementById('favFriendsGrid')?.scrollTo(0, 0);
}

const ALL_FRIENDS_LIVE_MS = 400;
let _allFriendsLiveTimer = null;

function filterAllFriendsIfLive() {
    _pplUpdateCounts();
    const tab = document.getElementById('tab3');
    if (!tab || !tab.classList.contains('active')) return;
    if (peopleFilter !== 'all') return;
    if (_peopleAllPage !== 0) return;
    if ((document.getElementById('allFriendSearchInput')?.value || '').trim()) return;
    if (_allFriendsLiveTimer) return;
    _allFriendsLiveTimer = setTimeout(() => {
        _allFriendsLiveTimer = null;
        const t3 = document.getElementById('tab3');
        if (!t3 || !t3.classList.contains('active')) return;
        if (peopleFilter !== 'all' || _peopleAllPage !== 0) return;
        if ((document.getElementById('allFriendSearchInput')?.value || '').trim()) return;
        _plKeepScroll('allFriendsGrid', filterAllFriends);
    }, ALL_FRIENDS_LIVE_MS);
}

function _updateAllFriendsFilterCounts(list) {
    const counts = { all: list.length, ingame: 0, active: 0, offline: 0 };
    for (const f of list) { const c = _allFriendCategory(f); if (counts[c] !== undefined) counts[c]++; }
    for (const k of Object.keys(counts)) {
        const el = document.getElementById('allFriendFilter' + k.charAt(0).toUpperCase() + k.slice(1) + 'Count');
        if (el) el.textContent = counts[k].toLocaleString();
    }
}

function filterAllFriends() {
    _pplUpdateCounts();
    const el = document.getElementById('allFriendsGrid');
    if (!el) return;
    const q = (document.getElementById('allFriendSearchInput')?.value || '').toLowerCase();
    let all = q
        ? vrcFriendsData.filter(f =>
            (f.displayName || '').toLowerCase().includes(q) ||
            (f.username || f.userName || '').toLowerCase().includes(q) ||
            (f.id || '').toLowerCase().includes(q))
        : [...vrcFriendsData];
    _updateAllFriendsFilterCounts(all);
    if (_allFriendsStatusFilter !== 'all') all = all.filter(f => _allFriendCategory(f) === _allFriendsStatusFilter);
    const listMode = _peopleListMode();
    all = listMode ? _plSort(all) : all.sort((a, b) => (a.displayName || '').localeCompare(b.displayName || ''));
    const pageSize = listMode ? peopleListPageSize : PEOPLE_PAGE_SIZE;
    const totalPages = Math.ceil(all.length / pageSize) || 1;
    if (_peopleAllPage >= totalPages) _peopleAllPage = totalPages - 1;
    if (_peopleAllPage < 0) _peopleAllPage = 0;
    const page = _peopleAllPage;
    const slice = all.slice(page * pageSize, (page + 1) * pageSize);
    if (!all.length) {
        el.innerHTML = q ? emptyStateHtml('search', t('profiles.people.no_results', 'No results')) : emptyStateHtml('person', t('profiles.people.no_friends', 'No friends yet'));
        plSetPaginator('peopleAllPaginatorBar', '');
        return;
    }
    el.classList.toggle('search-grid', !listMode);
    el.innerHTML = listMode
        ? buildPeopleListHtml(slice)
        : slice.map(f => renderPeopleFriendCard(f)).join('');
    if (listMode) lvEditDecorateList(el, 'people');
    plSetPaginator('peopleAllPaginatorBar', listMode
        ? plPaginator(page, totalPages, 'peopleAllGoPage', all.length)
        : buildPaginator(page, totalPages, 'peopleAllGoPage',
            `<span style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx2);padding:0 8px;">${all.length.toLocaleString()} total</span>`));
}

function peopleAllGoPage(page) {
    if (page < 0) return;
    const q = (document.getElementById('allFriendSearchInput')?.value || '').toLowerCase();
    let all = q
        ? vrcFriendsData.filter(f =>
            (f.displayName || '').toLowerCase().includes(q) ||
            (f.username || f.userName || '').toLowerCase().includes(q) ||
            (f.id || '').toLowerCase().includes(q))
        : [...vrcFriendsData];
    if (_allFriendsStatusFilter !== 'all') all = all.filter(f => _allFriendCategory(f) === _allFriendsStatusFilter);
    const totalPages = Math.ceil(all.length / (_peopleListMode() ? peopleListPageSize : PEOPLE_PAGE_SIZE)) || 1;
    if (page >= totalPages) return;
    _peopleAllPage = page;
    filterAllFriends();
    document.getElementById('allFriendsGrid')?.scrollTo(0, 0);
}

function filterModList(type) {
    const isBlock = type === 'block';
    renderModList(isBlock ? 'blockedList' : 'mutedList', isBlock ? (blockedData || []) : (mutedData || []), type);
}

function _mlValue(e, field) {
    switch (field) {
        case 'userid': return (e.targetUserId || '').toLowerCase();
        default:       return (e.targetDisplayName || e.targetUserId || '').toLowerCase();
    }
}

function buildModListHtml(entries, actionType) {
    const isBlock = actionType === 'block';
    const btnLabel = isBlock ? t('profiles.people.unblock', 'Unblock') : t('profiles.people.unmute', 'Unmute');
    let rows = '';
    entries.forEach(entry => {
        const uid = jsq(entry.targetUserId || '');
        const displayName = entry.targetDisplayName || entry.targetUserId || '?';
        const friend = vrcFriendsData.find(f => f.id === entry.targetUserId);
        const img = entry.image || (friend && friend.image) || '';
        const av = img
            ? `<span class="pl-av" style="background-image:url('${cssUrl(imgThumb(img, 64))}')"></span>`
            : `<span class="pl-av pl-av-letter">${esc(displayName[0].toUpperCase())}</span>`;
        rows += tlTableRow('modList', ` onclick="openFriendDetail('${uid}')"`, {
            profile: `<td class="pl-profile">${av}</td>`,
            name:    `<td class="pl-name">${esc(displayName)}</td>`,
            userid:  `<td class="lv-sub">${esc(entry.targetUserId || '')}</td>`,
            action:  `<td><button class="vrcn-button-round vrcn-btn-danger" onclick="event.stopPropagation();doUnmod('${uid}','${actionType}')">${esc(btnLabel)}</button></td>`,
        });
    });
    return tlTableHtml('modList', rows);
}

function renderModList(containerId, list, actionType) {
    _pplUpdateCounts();
    const el = document.getElementById(containerId);
    if (!el) return;
    const isBlock = actionType === 'block';
    const searchId = isBlock ? 'blockedSearch' : 'mutedSearch';
    const paginatorBarId = isBlock ? 'peopleBlockedPaginatorBar' : 'peopleMutedPaginatorBar';
    const goPageFn = isBlock ? 'peopleBlockedGoPage' : 'peopleMutedGoPage';
    const query = (document.getElementById(searchId)?.value || '').toLowerCase().trim();
    const all = (query
        ? (list || []).filter(e =>
            (e.targetDisplayName || '').toLowerCase().includes(query) ||
            (e.targetUserId || '').toLowerCase().includes(query))
        : (list || []));
    const listMode = _peopleListMode();
    el.classList.toggle('search-grid', !listMode);
    const pageSize = listMode ? peopleListPageSize : PEOPLE_PAGE_SIZE;
    const sorted = listMode ? lvSort(all, 'modList', _mlValue) : all;
    const totalPages = Math.ceil(sorted.length / pageSize) || 1;
    if (isBlock) {
        if (_peopleBlockedPage >= totalPages) _peopleBlockedPage = totalPages - 1;
        if (_peopleBlockedPage < 0) _peopleBlockedPage = 0;
    } else {
        if (_peopleMutedPage >= totalPages) _peopleMutedPage = totalPages - 1;
        if (_peopleMutedPage < 0) _peopleMutedPage = 0;
    }
    const page = isBlock ? _peopleBlockedPage : _peopleMutedPage;
    if (!sorted.length) {
        el.innerHTML = query
            ? emptyStateHtml('search', t('profiles.people.no_results', 'No results'))
            : (isBlock
                ? emptyStateHtml('block', t('profiles.people.no_blocked', 'No blocked users'))
                : emptyStateHtml('volume_off', t('profiles.people.no_muted', 'No muted users')));
        plSetPaginator(paginatorBarId, '');
        return;
    }
    const slice = sorted.slice(page * pageSize, (page + 1) * pageSize);
    if (listMode) {
        el.innerHTML = buildModListHtml(slice, actionType);
        plSetPaginator(paginatorBarId, plPaginator(page, totalPages, goPageFn, sorted.length));
        return;
    }
    const btnLabel = isBlock ? t('profiles.people.unblock', 'Unblock') : t('profiles.people.unmute', 'Unmute');
    const btnClass = 'vrcn-button-round vrcn-btn-danger';
    el.innerHTML = slice.map(entry => {
        const uid = jsq(entry.targetUserId || '');
        const displayName = entry.targetDisplayName || entry.targetUserId || '?';
        const friend = vrcFriendsData.find(f => f.id === entry.targetUserId);
        const user = { id: entry.targetUserId, displayName, image: entry.image || (friend && friend.image) || '' };
        const trailing = `<button class="${btnClass}" style="margin-left:auto;flex-shrink:0;" onclick="event.stopPropagation();doUnmod('${uid}','${actionType}')">${btnLabel}</button>`;
        return renderUserItem(user, `openFriendDetail('${uid}')`, { trailing });
    }).join('');
    plSetPaginator(paginatorBarId, buildPaginator(page, totalPages, goPageFn,
        `<span style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx2);padding:0 8px;">${all.length.toLocaleString()} total</span>`));
}

function peopleBlockedGoPage(page) {
    if (page < 0) return;
    _peopleBlockedPage = page;
    renderModList('blockedList', blockedData || [], 'block');
    document.getElementById('blockedList')?.scrollTo(0, 0);
}

function peopleMutedGoPage(page) {
    if (page < 0) return;
    _peopleMutedPage = page;
    renderModList('mutedList', mutedData || [], 'mute');
    document.getElementById('mutedList')?.scrollTo(0, 0);
}

function doUnmod(userId, type) {
    sendToCS({ action: type === 'block' ? 'vrcUnblock' : 'vrcUnmute', userId });
}


// === Favorite Friends (Group-Aware) ===
let favFriendGroups = [];
let favFriendGroupFilter = '';
let _favFriendEditMode = false;
let _favFriendEditSelected = new Set();
let _favFriendRefreshTimer = null;

function _scheduleBgFavFriendRefresh() {
    clearTimeout(_favFriendRefreshTimer);
    _favFriendRefreshTimer = setTimeout(() => sendToCS({ action: 'vrcGetFavoriteFriends' }), 2000);
}


function _ffGroupOptionLabel(g) {
    const count = favFriendsData.filter(f => f.groupName === g.name).length;
    const cap = g.capacity || 150;
    return `${esc(g.displayName || g.name)} ${count}/${cap}`;
}

function renderFavFriends(payload) {
    const refreshBtn = document.getElementById('peopleRefreshBtn');
    if (refreshBtn) { refreshBtn.disabled = false; const ico = refreshBtn.querySelector('.msi'); if (ico) ico.textContent = 'refresh'; }
    const friends = payload?.friends || (Array.isArray(payload) ? payload : []);
    const groups  = payload?.groups  || [];
    favFriendsData  = friends;
    favFriendGroups = groups;
    _pplFavLoaded = true;
    _pplUpdateCounts();
    const sel = document.getElementById('favFriendGroupFilter');
    if (sel) {
        const prev = favFriendGroupFilter;
        sel.innerHTML = `<option value="">${t('friends.favorites.group.all', 'All Favorites')}</option>` +
            groups.map(g => `<option value="${esc(g.name)}">${_ffGroupOptionLabel(g)}</option>`).join('');
        favFriendGroupFilter = groups.some(g => g.name === prev) ? prev : '';
        sel.value = favFriendGroupFilter;
    }
    updateFavFriendGroupHeader();
    filterFavFriends();
    const list = document.getElementById('fdFavGroupList');
    if (list?.dataset.pendingUserId) {
        const uid = list.dataset.pendingUserId;
        delete list.dataset.pendingUserId;
        if (typeof renderFriendFavPicker === 'function') renderFriendFavPicker(uid);
    }
}

function setFavFriendGroup(val) {
    favFriendGroupFilter = val;
    cancelEditFriendGroupName();
    updateFavFriendGroupHeader();
    filterFavFriends();
}

function updateFavFriendGroupHeader() {
    const header = document.getElementById('favFriendGroupHeader');
    const delBtn = document.getElementById('favFriendGroupDeleteBtn');
    if (!header) return;
    if (!favFriendGroupFilter) {
        if (delBtn) delBtn.style.display = 'none';
    } else {
        const g = favFriendGroups.find(x => x.name === favFriendGroupFilter);
        if (delBtn) delBtn.style.display = isLocalFavGroup(g) ? '' : 'none';
    }
    const anyVisible = [delBtn].some(el => el && el.style.display !== 'none');
    header.style.display = anyVisible ? 'flex' : 'none';
}

function _ffGroupVisDropdown(groupName, currentVis) {
    const opts = [
        { value: 'public',  key: 'friends.favorites.visibility.public',  label: 'Visible for everyone' },
        { value: 'friends', key: 'friends.favorites.visibility.friends', label: 'Visible for friends' },
        { value: 'private', key: 'friends.favorites.visibility.private', label: 'Visible only to you' },
    ];
    const optsHtml = opts.map(o =>
        `<option value="${o.value}"${o.value === currentVis ? ' selected' : ''}>${esc(t(o.key, o.label))}</option>`
    ).join('');
    return `<select class="vrcn-dropdown" style="min-width:160px;" onchange="saveFavFriendGroupVisibility(this.value,'${jsq(groupName)}')">${optsHtml}</select>`;
}

function saveFavFriendGroupVisibility(visibility, groupName) {
    const name = groupName || favFriendGroupFilter;
    const g = favFriendGroups.find(x => x.name === name);
    if (!g) return;
    sendToCS({ action: 'vrcUpdateFavoriteFriendGroup', groupName: g.name, displayName: g.displayName || g.name, visibility });
}


function cancelEditFriendGroupName() {
    updateFavFriendGroupHeader();
    const row = document.getElementById('favFriendGroupRenameRow');
    if (row) row.style.display = 'none';
    const saveBtn = document.querySelector('#favFriendGroupRenameRow .vrcn-btn-primary');
    if (saveBtn) { saveBtn.disabled = false; saveBtn.textContent = 'Save'; }
}

function saveFriendGroupName() {
    const g = favFriendGroups.find(x => x.name === favFriendGroupFilter);
    if (!g) return;
    const input = document.getElementById('favFriendGroupNameInput');
    const newName = (input?.value || '').trim();
    if (!newName) return;
    const saveBtn = document.querySelector('#favFriendGroupRenameRow .vrcn-btn-primary');
    if (saveBtn) { saveBtn.disabled = true; saveBtn.textContent = 'Saving...'; }
    sendToCS({ action: 'vrcUpdateFavoriteFriendGroup', groupName: g.name, displayName: newName });
}

function onFriendFavoriteGroupUpdated(data) {
    if (!data.ok) { if (_favFriendEditMode) filterFavFriends(); else cancelEditFriendGroupName(); return; }
    const g = favFriendGroups.find(x => x.name === data.groupName);
    if (g) {
        if (data.displayName) g.displayName = data.displayName;
        if (data.visibility)  g.visibility  = data.visibility;
    }
    const sel = document.getElementById('favFriendGroupFilter');
    if (sel) {
        const opt = [...sel.options].find(o => o.value === data.groupName);
        if (opt && g) opt.textContent = _ffGroupOptionLabel(g);
    }
    cancelEditFriendGroupName();
    updateFavFriendGroupHeader();
    filterFavFriends();
}

function _ffGroupHeaderHtml(g, count, first) {
    const isLocal = isLocalFavGroup(g);
    const cap = g.capacity || 150;
    const visHtml = (!isLocal && _favFriendEditMode)
        ? _ffGroupVisDropdown(g.name, g.visibility)
        : '';
    return `<div class="fav-group-header${first ? ' fav-group-header-first' : ''}">
        ${_ffGroupTitleHtml(g)}
        ${favGroupBadge(g)}
        <span class="fav-group-count">${count}/${cap}</span>
        ${visHtml}
    </div>`;
}

function _ffGroupTitleHtml(g) {
    const disp = g.displayName || g.name;
    if (!_favFriendEditMode) return `<span class="topbar-title">${esc(disp)}</span>`;
    return `<span class="fav-group-name-edit">
        <input class="vrcn-edit-field fav-group-name-input" maxlength="64" value="${esc(disp)}" data-group="${esc(g.name)}" data-orig="${esc(disp)}" oninput="ffOnGroupNameInput(this)" onclick="event.stopPropagation()">
        <span class="fav-group-name-actions" style="display:none;">
            <button class="vrcn-button vrcn-btn-primary" onclick="ffSaveGroupName(this)">${t('common.save', 'Save')}</button>
            <button class="vrcn-button" onclick="ffCancelGroupName(this)">${t('common.cancel', 'Cancel')}</button>
        </span>
    </span>`;
}

function ffOnGroupNameInput(inp) {
    const actions = inp.closest('.fav-group-name-edit')?.querySelector('.fav-group-name-actions');
    if (!actions) return;
    const v = inp.value.trim();
    actions.style.display = (v && v !== inp.dataset.orig) ? 'inline-flex' : 'none';
}

function ffSaveGroupName(btn) {
    const inp = btn.closest('.fav-group-name-edit')?.querySelector('.fav-group-name-input');
    if (!inp) return;
    const newName = inp.value.trim();
    if (!newName || newName === inp.dataset.orig) return;
    btn.disabled = true;
    sendToCS({ action: 'vrcUpdateFavoriteFriendGroup', groupName: inp.dataset.group, displayName: newName });
}

function ffCancelGroupName(btn) {
    const wrap = btn.closest('.fav-group-name-edit');
    const inp = wrap?.querySelector('.fav-group-name-input');
    if (inp) inp.value = inp.dataset.orig;
    const actions = wrap?.querySelector('.fav-group-name-actions');
    if (actions) actions.style.display = 'none';
}

function renderPeopleFriendCard(f) {
    const uid = jsq(f.id);
    if (!_favFriendEditMode) return renderUserItem(f, `openFriendDetail('${uid}')`);
    const isSel = _favFriendEditSelected.has(f.id);
    const checkIcon = isSel
        ? `<span class="msi" style="font-size:22px;color:var(--accent);">check_circle</span>`
        : `<span class="msi" style="font-size:22px;color:rgba(255,255,255,0.7);">radio_button_unchecked</span>`;
    return renderUserItem(f, `toggleFriendEditSelect('${uid}',this)`, {
        trailing: `<div style="margin-left:auto;flex-shrink:0;">${checkIcon}</div>`,
    });
}

function renderFavFriendCard(f) {
    const uid = jsq(f.id);
    if (_favFriendEditMode) {
        const isSel = _favFriendEditSelected.has(f.id);
        const checkIcon = isSel
            ? `<span class="msi" style="font-size:22px;color:var(--accent);">check_circle</span>`
            : `<span class="msi" style="font-size:22px;color:rgba(255,255,255,0.7);">radio_button_unchecked</span>`;
        return renderUserItem(f, `toggleFriendEditSelect('${uid}',this)`, {
            trailing: `<div style="margin-left:auto;flex-shrink:0;">${checkIcon}</div>`,
        });
    }
    return renderUserItem(f, `openFriendDetail('${uid}')`);
}

let _favFriendsDirty = false;
function filterFavFriendsIfVisible() {
    const tab = document.getElementById('tab3');
    if (tab && tab.classList.contains('active')) _plKeepScroll('favFriendsGrid', filterFavFriends);
    else _favFriendsDirty = true;
}

function filterFavFriends() {
    _pplUpdateCounts();
    _favFriendsDirty = false;
    const el = document.getElementById('favFriendsGrid');
    if (!el) return;
    const q = (document.getElementById('favFriendSearchInput')?.value || '').toLowerCase();
    const favMap = new Map(favFriendsData.map(f => [f.favoriteId, f]));
    let friends = vrcFriendsData.filter(f => favMap.has(f.id));
    if (favFriendGroupFilter) friends = friends.filter(f => favMap.get(f.id)?.groupName === favFriendGroupFilter);
    if (q) friends = friends.filter(f => (f.displayName || '').toLowerCase().includes(q));
    const favListMode = _peopleListMode();
    el.classList.toggle('search-grid', !favListMode);
    if (!friends.length) {
        el.innerHTML = q || favFriendGroupFilter ? emptyStateHtml('search', t('friends.favorites.no_match', 'No favorites match your filter')) : emptyStateHtml('favorite', t('friends.favorites.empty', 'No favorite friends yet'));
        plSetPaginator('peopleFavPaginatorBar', '');
        if (_favFriendEditMode) updateFriendEditBar();
        return;
    }
    if (favListMode) {
        const sorted = _plSort(friends);
        const totalPages = Math.ceil(sorted.length / peopleListPageSize) || 1;
        if (_peopleFavPage >= totalPages) _peopleFavPage = totalPages - 1;
        if (_peopleFavPage < 0) _peopleFavPage = 0;
        const slice = sorted.slice(_peopleFavPage * peopleListPageSize, (_peopleFavPage + 1) * peopleListPageSize);
        el.innerHTML = buildPeopleListHtml(slice);
        lvEditDecorateList(el, 'people');
        plSetPaginator('peopleFavPaginatorBar', plPaginator(_peopleFavPage, totalPages, 'peopleFavGoPage', sorted.length));
        return;
    }
    plSetPaginator('peopleFavPaginatorBar', '');
    if (!favFriendGroupFilter && favFriendGroups.length > 1) {
        let html = '', first = true;
        favFriendGroups.forEach(g => {
            const gFriends = friends.filter(f => favMap.get(f.id)?.groupName === g.name);
            if (!gFriends.length && !_favFriendEditMode) return;
            html += _ffGroupHeaderHtml(g, gFriends.length, first);
            html += gFriends.map(f => renderFavFriendCard(f)).join('');
            first = false;
        });
        el.innerHTML = html || emptyStateHtml('favorite', t('friends.favorites.empty', 'No favorite friends yet'));
        el.querySelectorAll('select.vrcn-dropdown').forEach(initVnSelect);
    } else {
        const selected = favFriendGroupFilter
            ? favFriendGroups.find(x => x.name === favFriendGroupFilter)
            : null;
        const head = selected ? _ffGroupHeaderHtml(selected, friends.length, true) : '';
        el.innerHTML = head + friends.map(f => renderFavFriendCard(f)).join('');
        if (head) el.querySelectorAll('select.vrcn-dropdown').forEach(initVnSelect);
    }
    if (_favFriendEditMode) updateFriendEditBar();
}

// === Friend Edit Mode ===

function toggleFriendEditMode() {
    if (_favFriendEditMode) { exitFriendEditMode(); return; }
    _favFriendEditMode = true;
    _favFriendEditSelected = new Set();
    const btn = document.getElementById('favFriendEditModeBtn');
    if (btn) { btn.innerHTML = `<span class="msi" style="font-size:16px;">check</span> <span>${t('friends.edit.done', 'Done')}</span>`; btn.classList.add('active'); }
    const bar = document.getElementById('favFriendEditBar');
    if (bar) bar.style.display = 'flex';
    _friendEditSyncActions();
    _friendEditRerender();
    updateFavFriendGroupHeader();
    updateFriendEditBar();
}

function _friendEditSyncActions() {
    const isAll = _friendEditIsAllFilter();
    const show = (id, on) => {
        const el = document.getElementById(id);
        if (el) el.style.display = on ? '' : 'none';
    };
    show('favFriendEditAddFavWrap', isAll);
    show('favFriendEditMoveWrap', !isAll);
    show('favFriendEditRemoveBtn', !isAll);
    show('friendCreateLocalWrap', !isAll);
}

function exitFriendEditMode() {
    const wasAll = _friendEditIsAllFilter();
    _favFriendEditMode = false;
    _favFriendEditSelected = new Set();
    const btn = document.getElementById('favFriendEditModeBtn');
    if (btn) { btn.innerHTML = `<span class="msi" style="font-size:16px;">edit</span> <span>${t('friends.edit.button', 'Edit')}</span>`; btn.classList.remove('active'); }
    const bar = document.getElementById('favFriendEditBar');
    if (bar) bar.style.display = 'none';
    ['favFriendEditMovePicker', 'favFriendEditAddFavPicker'].forEach(id => {
        const picker = document.getElementById(id);
        if (picker) { picker.style.display = 'none'; picker.innerHTML = ''; }
    });
    friendCancelCreateLocalGroup();
    if (wasAll) filterAllFriends();
    else filterFavFriends();
    updateFavFriendGroupHeader();
}

/* === Local Groups (Friends) === */
function friendLocalGroupCount() {
    return (typeof favFriendGroups !== 'undefined' ? favFriendGroups : []).filter(isLocalFavGroup).length;
}

function friendShowCreateLocalGroup(btn) {
    const panel = document.getElementById('friendCreateLocalPanel');
    if (!panel) return;
    if (panel.style.display === 'block') { friendCancelCreateLocalGroup(); return; }
    if (friendLocalGroupCount() >= 100) { showToast(false, localFavErrorText('group_limit')); return; }
    panel.style.display = 'block';
    const input = document.getElementById('friendCreateLocalInput');
    if (input) { input.value = ''; input.focus(); }
    setTimeout(() => {
        const close = (e) => {
            if (!panel.contains(e.target) && !(btn && btn.contains(e.target))) {
                panel.style.display = 'none';
                document.removeEventListener('click', close);
            }
        };
        document.addEventListener('click', close);
    }, 0);
}

function friendCancelCreateLocalGroup() {
    const panel = document.getElementById('friendCreateLocalPanel');
    if (panel) panel.style.display = 'none';
}

function friendSaveLocalGroup() {
    const input = document.getElementById('friendCreateLocalInput');
    const name = (input?.value || '').trim();
    if (!name) { showToast(false, localFavErrorText('empty_name')); return; }
    sendToCS({ action: 'vrcCreateLocalGroup', kind: 'friend', displayName: name });
    friendCancelCreateLocalGroup();
}

function deleteCurrentLocalFriendGroup(btn) {
    const g = favFriendGroups.find(x => x.name === favFriendGroupFilter);
    if (!g || !isLocalFavGroup(g)) return;
    if (btn && btn.dataset.confirm !== '1') {
        btn.dataset.confirm = '1';
        btn.classList.add('myp-edit-btn-danger');
        btn.title = t('favorites.delete_local_group_confirm', 'Click again to delete');
        clearTimeout(btn._confirmTimer);
        btn._confirmTimer = setTimeout(() => {
            btn.dataset.confirm = '0';
            btn.classList.remove('myp-edit-btn-danger');
            btn.title = t('favorites.delete_local_group', 'Delete local group');
        }, 3000);
        return;
    }
    if (btn) { btn.dataset.confirm = '0'; btn.classList.remove('myp-edit-btn-danger'); clearTimeout(btn._confirmTimer); }
    sendToCS({ action: 'vrcDeleteLocalGroup', kind: 'friend', groupName: g.name });
    favFriendGroupFilter = '';
}

function toggleFriendEditSelect(id, el) {
    if (_favFriendEditSelected.has(id)) {
        _favFriendEditSelected.delete(id);
        const msi = el?.querySelector('[style*="margin-left:auto"] .msi');
        if (msi) { msi.textContent = 'radio_button_unchecked'; msi.style.color = 'rgba(255,255,255,0.7)'; }
    } else {
        _favFriendEditSelected.add(id);
        const msi = el?.querySelector('[style*="margin-left:auto"] .msi');
        if (msi) { msi.textContent = 'check_circle'; msi.style.color = 'var(--accent)'; }
    }
    updateFriendEditBar();
}

function friendEditSelectAll() {
    const friends = _friendEditVisibleList();
    const allSel = friends.length > 0 && friends.every(f => _favFriendEditSelected.has(f.id));
    if (allSel) friends.forEach(f => _favFriendEditSelected.delete(f.id));
    else friends.forEach(f => _favFriendEditSelected.add(f.id));
    _friendEditRerender();
    updateFriendEditBar();
}

function updateFriendEditBar() {
    const count = _favFriendEditSelected.size;
    const countEl = document.getElementById('favFriendEditCount');
    if (countEl) countEl.textContent = t('friends.edit.selected', '{count} selected').replace('{count}', count);
    const selectAllBtn = document.getElementById('favFriendEditSelectAllBtn');
    if (selectAllBtn) {
        const friends = _friendEditVisibleList();
        const allSel = friends.length > 0 && friends.every(f => _favFriendEditSelected.has(f.id));
        selectAllBtn.textContent = allSel ? t('friends.edit.deselect_all', 'Deselect All') : t('friends.edit.select_all', 'Select All');
    }
    document.querySelectorAll('#favFriendEditBar .wd-edit-action').forEach(b => b.disabled = count === 0);
}

function _friendEditIsAllFilter() {
    return peopleFilter === 'all';
}

function _friendEditVisibleList() {
    if (_friendEditIsAllFilter()) {
        const q = (document.getElementById('allFriendSearchInput')?.value || '').toLowerCase();
        let all = q
            ? vrcFriendsData.filter(f =>
                (f.displayName || '').toLowerCase().includes(q) ||
                (f.username || f.userName || '').toLowerCase().includes(q) ||
                (f.id || '').toLowerCase().includes(q))
            : [...vrcFriendsData];
        if (_allFriendsStatusFilter !== 'all') all = all.filter(f => _allFriendCategory(f) === _allFriendsStatusFilter);
        return all;
    }
    const q = (document.getElementById('favFriendSearchInput')?.value || '').toLowerCase();
    const favMap = new Map(favFriendsData.map(f => [f.favoriteId, f]));
    let friends = vrcFriendsData.filter(f => favMap.has(f.id));
    if (favFriendGroupFilter) friends = friends.filter(f => favMap.get(f.id)?.groupName === favFriendGroupFilter);
    if (q) friends = friends.filter(f => (f.displayName || '').toLowerCase().includes(q));
    return friends;
}

function _friendEditRerender() {
    if (_friendEditIsAllFilter()) filterAllFriends();
    else filterFavFriends();
}

function friendEditShowAddFavMenu(btn) {
    if (_favFriendEditSelected.size === 0) return;
    const picker = document.getElementById('favFriendEditAddFavPicker');
    if (!picker) return;
    if (picker.style.display === 'block') { picker.style.display = 'none'; picker.innerHTML = ''; return; }
    picker.innerHTML = favFriendGroups.map(g => {
        const count = favFriendsData.filter(f => f.groupName === g.name).length;
        const gn = jsq(g.name);
        return `<div class="vn-select-option" onclick="friendEditAddToFavorites('${gn}')">
            <span class="msi" style="font-size:14px;flex-shrink:0;">favorite</span>
            <span style="flex:1;">${esc(g.displayName || g.name)}</span>
            ${favGroupBadge(g)}
            <span style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx2);flex-shrink:0;">${count}</span>
        </div>`;
    }).join('') || `<div class="vn-select-option" style="pointer-events:none;color:var(--tx3);">${esc(t('friends.favorites.no_groups', 'No favorite groups'))}</div>`;
    picker.style.display = 'block';
    setTimeout(() => {
        const close = (e) => {
            if (!picker.contains(e.target) && e.target !== btn) {
                picker.style.display = 'none';
                picker.innerHTML = '';
                document.removeEventListener('click', close);
            }
        };
        document.addEventListener('click', close);
    }, 0);
}

function friendEditAddToFavorites(groupName) {
    if (_favFriendEditSelected.size === 0) return;
    const picker = document.getElementById('favFriendEditAddFavPicker');
    if (picker) { picker.style.display = 'none'; picker.innerHTML = ''; }
    const ids = [..._favFriendEditSelected];
    let added = 0;
    ids.forEach(userId => {
        if (favFriendsData.some(f => f.favoriteId === userId)) return;
        sendToCS({ action: 'vrcAddFavoriteFriend', userId, groupName });
        added++;
    });
    exitFriendEditMode();
    if (typeof showToast === 'function') {
        showToast(added > 0, tf('friends.edit.add_to_favorites_done', { count: added }, 'Added {count} to favorites'));
    }
}

function friendEditShowMoveMenu(btn) {
    if (_favFriendEditSelected.size === 0) return;
    const picker = document.getElementById('favFriendEditMovePicker');
    if (!picker) return;
    if (picker.style.display === 'block') { picker.style.display = 'none'; picker.innerHTML = ''; return; }
    picker.innerHTML = favFriendGroups.map(g => {
        const count = favFriendsData.filter(f => f.groupName === g.name).length;
        const gn = jsq(g.name);
        return `<div class="vn-select-option" onclick="friendEditMoveSelected('${gn}')">
            <span class="msi" style="font-size:14px;flex-shrink:0;">folder</span>
            <span style="flex:1;">${esc(g.displayName || g.name)}</span>
            ${favGroupBadge(g)}
            <span style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx2);flex-shrink:0;">${count}</span>
        </div>`;
    }).join('');
    picker.style.display = 'block';
    setTimeout(() => {
        const close = (e) => {
            if (!picker.contains(e.target) && e.target !== btn) {
                picker.style.display = 'none';
                picker.innerHTML = '';
                document.removeEventListener('click', close);
            }
        };
        document.addEventListener('click', close);
    }, 0);
}

function friendEditMoveSelected(groupName) {
    if (_favFriendEditSelected.size === 0) return;
    const picker = document.getElementById('favFriendEditMovePicker');
    if (picker) { picker.style.display = 'none'; picker.innerHTML = ''; }
    const toMove = [..._favFriendEditSelected];
    toMove.forEach(userId => {
        const entry = favFriendsData.find(f => f.favoriteId === userId);
        if (entry && entry.groupName !== groupName) {
            sendToCS({ action: 'vrcAddFavoriteFriendToGroup', userId, groupName, oldFvrtId: entry.fvrtId });
        }
    });
    exitFriendEditMode();
}

function friendEditUnfriendSelected() {
    const ids = [..._favFriendEditSelected];
    if (!ids.length) return;
    const names = ids
        .map(id => (vrcFriendsData.find(f => f.id === id)?.displayName) || id)
        .slice(0, 6);
    const more = ids.length - names.length;
    const listHtml = names.map(n => `<div>${esc(n)}</div>`).join('')
        + (more > 0 ? `<div>${esc(tf('friends.edit.bulk_unfriend_more', { count: more }, '+{count} more'))}</div>` : '');

    document.getElementById('friendBulkUnfriendModal')?.remove();
    const o = document.createElement('div');
    o.className = 'modal-overlay';
    o.style.display = 'flex';
    o.id = 'friendBulkUnfriendModal';
    o.style.zIndex = '10003';
    o.onclick = e => { if (e.target === o) o.remove(); };
    o.innerHTML = `<div class="modal-box">
        ${renderModalBar(t('friends.edit.bulk_unfriend', 'Bulk Unfriend'), [modalCloseAction("document.getElementById('friendBulkUnfriendModal').remove()")])}
        <div class="modal-icon danger" style="margin-top:20px;"><span class="msi" style="font-size:22px;">person_remove</span></div>
        <div class="modal-msg">${tf('friends.edit.bulk_unfriend_confirm', { count: ids.length }, 'Unfriend {count} people? This cannot be undone.')}</div>
        <div class="gr-bulk-list">${listHtml}</div>
        <div class="modal-btns">
            <button class="vrcn-button-round" onclick="document.getElementById('friendBulkUnfriendModal').remove()">${esc(t('common.cancel', 'Cancel'))}</button>
            <button class="vrcn-button-round vrcn-btn-danger" onclick="friendEditUnfriendConfirmed()">${esc(t('friends.edit.bulk_unfriend', 'Bulk Unfriend'))}</button>
        </div></div>`;
    document.body.appendChild(o);
}

let _friendBulkUnfriendPending = 0;

function friendEditUnfriendConfirmed() {
    document.getElementById('friendBulkUnfriendModal')?.remove();
    const ids = [..._favFriendEditSelected];
    if (!ids.length) return;
    _friendBulkUnfriendPending = ids.length;
    ids.forEach(userId => sendToCS({ action: 'vrcUnfriend', userId }));
    exitFriendEditMode();
}

function friendBulkUnfriendConsume() {
    if (_friendBulkUnfriendPending <= 0) return false;
    const total = _friendBulkUnfriendPending;
    _friendBulkUnfriendPending--;
    if (_friendBulkUnfriendPending === 0) {
        if (typeof showToast === 'function') {
            showToast(true, tf('friends.edit.bulk_unfriend_done', { count: total }, 'Unfriended {count} people'));
        }
        sendToCS({ action: 'vrcRefreshFriends' });
    }
    return true;
}

function friendEditRemoveSelected() {
    if (_favFriendEditSelected.size === 0) return;
    const toRemove = [..._favFriendEditSelected];
    toRemove.forEach(userId => {
        const entry = favFriendsData.find(f => f.favoriteId === userId);
        if (entry) sendToCS({ action: 'vrcRemoveFavoriteFriend', userId, fvrtId: entry.fvrtId });
    });
    exitFriendEditMode();
}

lvEditRegister('people', {
    attr: 'data-uid',
    isActive: () => _favFriendEditMode,
    isSelected: id => _favFriendEditSelected.has(id),
    toggle: id => { if (_favFriendEditSelected.has(id)) _favFriendEditSelected.delete(id); else _favFriendEditSelected.add(id); },
    onChange: () => updateFriendEditBar(),
});
