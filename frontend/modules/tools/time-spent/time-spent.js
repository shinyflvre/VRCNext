/* === Time Spent - Tab 16 ===
 * Shows time spent in worlds and with persons, derived from instance_join timeline events.
 */

let _tsView = 'worlds';
let _tsData = null;
let _tsLoading = false;
let _tsInited = false;
let _tsWorldQuery = '';
let _tsPersonQuery = '';
let _tsSearchTimer = null;
let _tsWorldPage = 0;
let _tsPersonPage = 0;
let _tsGroupPage = 0;
let _tsGroupQuery = '';
let _tsTotalGroups = 0;
let _tsAllUniqueGroups = 0;
let _tsGlobalGroupSeconds = 0;
let _tsGlobalGroupJoins = 0;
let _tsGlobalTopGroupName = '';
let _tsReqId = 0;
let _tsTotalWorlds = 0;
let _tsTotalPersons = 0;
let _tsAllUniqueWorlds = 0;
let _tsAllUniquePersons = 0;
const TS_PAGE_SIZE = 100;

// Global stats — always reflect full dataset regardless of search/pagination
let _tsGlobalFriendCount = 0;
let _tsGlobalStrangerCount = 0;
let _tsGlobalTopFriendName = '';
let _tsGlobalTopFriendSeconds = 0;
let _tsGlobalTopStrangerName = '';
let _tsGlobalTopStrangerSeconds = 0;
let _tsGlobalTotalWithOthers = 0;
let _tsGlobalTopWorldName = '';
let _tsGlobalTotalVisits = 0;

document.documentElement.addEventListener('tabchange', () => {
    const tab16 = document.getElementById('tab16');
    if (tab16 && tab16.classList.contains('active') && !_tsInited) {
        _tsInited = true;
        tsLoad();
    }
});

function tsShortUnit(key, fallback) {
    return t(`timespent.unit.${key}`, fallback);
}

function tsFmtTime(seconds) {
    if (seconds < 1) return `0${tsShortUnit('second_short', 's')}`;

    const days = Math.floor(seconds / 86400);
    const hours = Math.floor((seconds % 86400) / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);
    const secs = seconds % 60;

    if (days > 0) {
        return `${days}${tsShortUnit('day_short', 'd')} ${hours}${tsShortUnit('hour_short', 'h')} ${minutes}${tsShortUnit('minute_short', 'm')} ${secs}${tsShortUnit('second_short', 's')}`;
    }
    if (hours > 0) {
        return `${hours}${tsShortUnit('hour_short', 'h')} ${minutes}${tsShortUnit('minute_short', 'm')} ${secs}${tsShortUnit('second_short', 's')}`;
    }
    if (minutes > 0) {
        return `${minutes}${tsShortUnit('minute_short', 'm')} ${secs}${tsShortUnit('second_short', 's')}`;
    }
    return `${secs}${tsShortUnit('second_short', 's')}`;
}

function tsFmtTimeDH(seconds) {
    const days = Math.floor(seconds / 86400);
    const hours = Math.floor((seconds % 86400) / 3600);
    if (days > 0) return `${days}${tsShortUnit('day_short', 'd')} ${hours}${tsShortUnit('hour_short', 'h')}`;
    return `${hours}${tsShortUnit('hour_short', 'h')}`;
}

function tsFmtTimeLong(seconds) {
    return tsFmtTime(seconds);
}

function tsFilterSearch(value) {
    const search = value.trim();
    const list = document.getElementById('tsList');
    if (list) list.innerHTML = '<div class="ts-items"><div class="ts-sk-item"></div><div class="ts-sk-item"></div><div class="ts-sk-item"></div><div class="ts-sk-item"></div><div class="ts-sk-item"></div></div>';
    _tsSetPaginator('');
    clearTimeout(_tsSearchTimer);
    _tsSearchTimer = setTimeout(() => {
        if (_tsView === 'worlds')      { _tsWorldQuery = search;  _tsWorldPage = 0; }
        else if (_tsView === 'groups') { _tsGroupQuery = search;  _tsGroupPage = 0; }
        else                           { _tsPersonQuery = search; _tsPersonPage = 0; }
        _tsLoad();
    }, 300);
}

function tsRefresh() {
    _tsData = null;
    _tsWorldPage = 0;
    _tsPersonPage = 0;
    _tsGroupPage = 0;
    _tsWorldQuery = '';
    _tsPersonQuery = '';
    _tsGroupQuery = '';
    clearTimeout(_tsSearchTimer);
    const wi = document.getElementById('tsWorldSearchInput');
    const pi = document.getElementById('tsPersonSearchInput');
    const gi = document.getElementById('tsGroupSearchInput');
    if (wi) wi.value = '';
    if (pi) pi.value = '';
    if (gi) gi.value = '';
    _tsLoad();
}

function tsLoad() {
    _tsLoad();
}

function _tsLoad() {
    _tsLoading = true;

    const icon = document.getElementById('tsRefreshIcon');
    if (icon) icon.classList.add('ts-spin');

    const list = document.getElementById('tsList');
    if (list) {
        list.innerHTML = `<div class="ts-loading"><span class="msi ts-spin" style="font-size:22px;color:var(--accent);">sync</span><span style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('timespent.loading', 'Calculating stats...')}</span></div>`;
    }

    const summary = document.getElementById('tsSummary');
    if (summary && !summary.querySelector('.ts-stat'))
        summary.innerHTML = `<div class="ts-stat-row">${'<div class="ts-sk-stat"></div>'.repeat(4)}</div>`;

    const query = _tsView === 'worlds' ? _tsWorldQuery : _tsView === 'groups' ? _tsGroupQuery : _tsPersonQuery;
    const page  = _tsView === 'worlds' ? _tsWorldPage  : _tsView === 'groups' ? _tsGroupPage  : _tsPersonPage;
    sendToCS({ action: 'vrcGetTimeSpent', view: _tsView, query: query.trim(), page, reqId: ++_tsReqId });
}

function tsOnData(payload) {
    if ((payload.reqId ?? 0) !== _tsReqId) return;
    _tsLoading = false;
    _tsData = payload;
    _tsTotalWorlds       = payload.totalWorlds   ?? 0;
    _tsTotalPersons      = payload.totalPersons  ?? 0;
    _tsAllUniqueWorlds   = payload.allUniqueWorlds  ?? _tsTotalWorlds;
    _tsAllUniquePersons  = payload.allUniquePersons ?? _tsTotalPersons;
    _tsTotalGroups       = payload.totalGroups      ?? 0;
    _tsAllUniqueGroups   = payload.allUniqueGroups  ?? _tsTotalGroups;
    if (payload.globalGroupSeconds !== undefined) {
        _tsGlobalGroupSeconds = payload.globalGroupSeconds ?? 0;
        _tsGlobalGroupJoins   = payload.globalGroupJoins   ?? 0;
        _tsGlobalTopGroupName = payload.globalTopGroupName ?? '';
    }

    // Store global stats — only update when present (backfill SendPage re-sends everything)
    if (payload.globalFriendCount !== undefined) {
        _tsGlobalFriendCount       = payload.globalFriendCount   ?? 0;
        _tsGlobalStrangerCount     = payload.globalStrangerCount ?? 0;
        _tsGlobalTopFriendName     = payload.globalTopFriendName ?? '';
        _tsGlobalTopFriendSeconds  = payload.globalTopFriendSeconds ?? 0;
        _tsGlobalTopStrangerName   = payload.globalTopStrangerName ?? '';
        _tsGlobalTopStrangerSeconds = payload.globalTopStrangerSeconds ?? 0;
        _tsGlobalTotalWithOthers   = payload.globalTotalWithOthers ?? 0;
        _tsGlobalTopWorldName      = payload.globalTopWorldName ?? '';
        _tsGlobalTotalVisits       = payload.globalTotalVisits ?? 0;
    }

    const icon = document.getElementById('tsRefreshIcon');
    if (icon) icon.classList.remove('ts-spin');

    const friendIds = new Set((vrcFriendsData || []).map(friend => friend.id));
    (_tsData.persons || []).forEach(person => {
        person.isFriend = friendIds.has(person.userId);
    });

    tsRender();
}

function tsSetView(view) {
    _tsView = view;
    _tsWorldPage = 0;
    _tsPersonPage = 0;
    _tsGroupPage = 0;
    document.getElementById('tsBtnWorlds')?.classList.toggle('active', view === 'worlds');
    document.getElementById('tsBtnPersons')?.classList.toggle('active', view === 'persons');
    document.getElementById('tsBtnGroups')?.classList.toggle('active', view === 'groups');
    _tsShowSearch();
    _tsLoad();
}

function _tsShowSearch() {
    document.getElementById('tsSearchWorlds')?.style.setProperty('display', _tsView === 'worlds' ? '' : 'none');
    document.getElementById('tsSearchPersons')?.style.setProperty('display', _tsView === 'persons' ? '' : 'none');
    document.getElementById('tsSearchGroups')?.style.setProperty('display', _tsView === 'groups' ? '' : 'none');
}

function tsRender() {
    if (!_tsData) return;

    const tab = document.getElementById('tab16');
    if (!tab || !tab.classList.contains('active')) return;

    if (_tsView === 'worlds') tsRenderWorlds();
    else if (_tsView === 'groups') tsRenderGroups();
    else tsRenderPersons();
}

function tsRenderWorlds() {
    const totalSec = _tsData.totalSeconds || 0;
    const summary = document.getElementById('tsSummary');
    if (!summary) return;

    summary.innerHTML = `
        <div class="ts-stat-row">
            <div class="ts-stat">
                <span class="msi ts-stat-icon">schedule</span>
                <div class="ts-stat-val">${tsFmtTimeDH(totalSec)}</div>
                <div class="ts-stat-label">${t('timespent.summary.total_vrchat_time', 'Total VRChat Time')}</div>
            </div>
            <div class="ts-stat">
                <span class="msi ts-stat-icon">travel_explore</span>
                <div class="ts-stat-val">${_tsAllUniqueWorlds}</div>
                <div class="ts-stat-label">${t('timespent.summary.unique_worlds', 'Unique Worlds')}</div>
            </div>
            <div class="ts-stat">
                <span class="msi ts-stat-icon">favorite</span>
                <div class="ts-stat-val">${_tsGlobalTopWorldName ? esc(_tsGlobalTopWorldName) : '-'}</div>
                <div class="ts-stat-label">${t('timespent.summary.favourite_world', 'Favourite World')}</div>
            </div>
            <div class="ts-stat">
                <span class="msi ts-stat-icon">login</span>
                <div class="ts-stat-val">${_tsGlobalTotalVisits.toLocaleString()}</div>
                <div class="ts-stat-label">${t('timespent.summary.total_joins', 'Total Joins')}</div>
            </div>
        </div>`;

    _tsShowSearch();
    tsRenderWorldItems();
}

function tsRenderWorldItems() {
    const worlds = _tsData?.worlds || [];
    const tsList = document.getElementById('tsList');
    if (!tsList) return;

    if (_tsAllUniqueWorlds === 0) {
        tsList.innerHTML = `<div class="ts-empty"><span class="msi" style="font-size:28px;color:var(--tx2);">travel_explore</span><div>${t('timespent.empty.no_world_data', 'No world data yet.')}</div></div>`;
        _tsSetPaginator('');
        return;
    }

    if (worlds.length === 0) {
        tsList.innerHTML = `<div class="ts-empty"><span class="msi" style="font-size:28px;color:var(--tx3);">search_off</span><div>${t('timespent.empty.no_world_match', 'No worlds match your search.')}</div></div>`;
        _tsSetPaginator('');
        return;
    }

    const totalPages = Math.ceil(_tsTotalWorlds / TS_PAGE_SIZE) || 1;
    const maxSec = _tsData.maxWorldSeconds || worlds[0].seconds || 1;
    const rows = worlds.map((world, i) => {
        const pct = ((world.seconds / maxSec) * 100).toFixed(2);
        const rank = world.rank ?? (_tsWorldPage * TS_PAGE_SIZE + i + 1);
        const thumb = world.worldThumb
            ? `<img class="ts-item-thumb" src="${esc(imgThumb(world.worldThumb, 96))}" onerror="this.style.display='none'">`
            : `<div class="ts-item-thumb ts-thumb-placeholder"><span class="msi" style="font-size:18px;color:var(--tx2);">travel_explore</span></div>`;
        const click = world.worldId ? `onclick="openWorldSearchDetail('${esc(world.worldId)}')" style="cursor:pointer"` : '';
        const visits = tf(`timespent.visit.${world.visits === 1 ? 'one' : 'other'}`, { count: world.visits }, `${world.visits} visit${world.visits === 1 ? '' : 's'}`);

        return `
        <div class="ts-item" ${click}>
            <div class="ts-item-rank">#${rank}</div>
            ${thumb}
            <div class="ts-item-body">
                <div class="ts-item-name">${esc(world.worldName || t('timespent.unknown_world_full', 'Unknown World'))}</div>
                <div class="ts-item-meta">
                    <span class="msi" style="font-size:12px;color:var(--tx2);">login</span>
                    <span>${visits}</span>
                </div>
                <div class="ts-bar-wrap">
                    <div class="ts-bar" style="width:${pct}%"></div>
                </div>
            </div>
            <div class="ts-item-time">${tsFmtTime(world.seconds)}</div>
        </div>`;
    }).join('');

    tsList.innerHTML = `<div class="ts-items">${rows}</div>`;
    _tsSetPaginator(_tsBuildPaginator(_tsWorldPage, totalPages, _tsTotalWorlds, 'tsWorldGoPage'));
}

function tsWorldGoPage(page) {
    const totalPages = Math.ceil(_tsTotalWorlds / TS_PAGE_SIZE) || 1;
    if (page < 0 || page >= totalPages) return;
    _tsWorldPage = page;
    _tsLoad();
    document.getElementById('tsList')?.scrollTo(0, 0);
}

function tsRenderPersons() {
    const summary = document.getElementById('tsSummary');
    if (!summary) return;

    summary.innerHTML = `
        <div class="ts-stat-row">
            <div class="ts-stat">
                <span class="msi ts-stat-icon">group</span>
                <div class="ts-stat-val">${_tsAllUniquePersons}</div>
                <div class="ts-stat-label">${t('timespent.summary.unique_people', 'Unique People')}</div>
            </div>
            <div class="ts-stat">
                <span class="msi ts-stat-icon" style="color:var(--ok);">person</span>
                <div class="ts-stat-val">${_tsGlobalFriendCount}</div>
                <div class="ts-stat-label">${t('timespent.summary.friends', 'Friends')}</div>
            </div>
            <div class="ts-stat">
                <span class="msi ts-stat-icon" style="color:var(--cyan);">person_outline</span>
                <div class="ts-stat-val">${_tsGlobalStrangerCount}</div>
                <div class="ts-stat-label">${t('timespent.summary.others', 'Others')}</div>
            </div>
            <div class="ts-stat">
                <span class="msi ts-stat-icon">schedule</span>
                <div class="ts-stat-val">${tsFmtTimeDH(_tsGlobalTotalWithOthers)}</div>
                <div class="ts-stat-label">${t('timespent.summary.total_social_time', 'Total Social Time')}</div>
            </div>
        </div>
        ${_tsGlobalTopFriendName || _tsGlobalTopStrangerName ? `
        <div class="ts-highlights">
            ${_tsGlobalTopFriendName ? `<div class="ts-highlight ts-hl-friend">
                <span class="msi" style="font-size:13px;">favorite</span>
                <span>${t('timespent.highlight.friend', 'Most time with friend')}: <strong>${esc(_tsGlobalTopFriendName)}</strong> - ${tsFmtTimeLong(_tsGlobalTopFriendSeconds)}</span>
            </div>` : ''}
            ${_tsGlobalTopStrangerName ? `<div class="ts-highlight ts-hl-stranger">
                <span class="msi" style="font-size:13px;">person_add</span>
                <span>${t('timespent.highlight.new_person', 'Most time with someone new')}: <strong>${esc(_tsGlobalTopStrangerName)}</strong> - ${tsFmtTimeLong(_tsGlobalTopStrangerSeconds)}</span>
            </div>` : ''}
        </div>` : ''}`;

    _tsShowSearch();
    tsRenderPersonItems();
}

function tsRenderPersonItems() {
    const persons = _tsData?.persons || [];
    const tsList = document.getElementById('tsList');
    if (!tsList) return;

    if (_tsAllUniquePersons === 0) {
        tsList.innerHTML = `<div class="ts-empty"><span class="msi" style="font-size:28px;color:var(--tx2);">group</span><div>${t('timespent.empty.no_person_data', 'No person data yet.')}</div></div>`;
        _tsSetPaginator('');
        return;
    }

    if (persons.length === 0) {
        tsList.innerHTML = `<div class="ts-empty"><span class="msi" style="font-size:28px;color:var(--tx3);">search_off</span><div>${t('timespent.empty.no_person_match', 'No persons match your search.')}</div></div>`;
        _tsSetPaginator('');
        return;
    }

    const totalPages = Math.ceil(_tsTotalPersons / TS_PAGE_SIZE) || 1;
    const maxSec = _tsData.maxPersonSeconds || persons[0].seconds || 1;
    const rows = persons.map((person, i) => {
        const pct = ((person.seconds / maxSec) * 100).toFixed(2);
        const rank = person.rank ?? (_tsPersonPage * TS_PAGE_SIZE + i + 1);
        const isFriend = person.isFriend;
        const avatar = person.image
            ? `<img class="ts-item-avatar" src="${esc(imgThumb(person.image, 96))}" onerror="this.style.display='none'">`
            : `<div class="ts-item-avatar ts-avatar-placeholder"><span class="msi" style="font-size:16px;color:var(--tx2);">person</span></div>`;
        const badge = isFriend
            ? `<span class="vrcn-badge bdg-friend">${esc(t('timespent.badge.friend', 'Friend'))}</span>`
            : `<span class="vrcn-badge cyan">${esc(t('timespent.badge.new', 'New'))}</span>`;
        const encounters = tf(`timespent.encounter.${person.meets === 1 ? 'one' : 'other'}`, { count: person.meets }, `${person.meets} encounter${person.meets === 1 ? '' : 's'}`);

        return `
        <div class="ts-item" onclick="openFriendDetail('${esc(person.userId)}')" style="cursor:pointer">
            <div class="ts-item-rank">#${rank}</div>
            <div class="ts-avatar-wrap">${avatar}</div>
            <div class="ts-item-body">
                <div class="ts-item-name">${esc(person.displayName || person.userId)} ${badge}</div>
                <div class="ts-item-meta">
                    <span class="msi" style="font-size:12px;color:var(--tx2);">handshake</span>
                    <span>${encounters}</span>
                </div>
                <div class="ts-bar-wrap">
                    <div class="ts-bar ${isFriend ? 'ts-bar-friend' : 'ts-bar-stranger'}" style="width:${pct}%"></div>
                </div>
            </div>
            <div class="ts-item-time">${tsFmtTime(person.seconds)}</div>
        </div>`;
    }).join('');

    tsList.innerHTML = `<div class="ts-items">${rows}</div>`;
    _tsSetPaginator(_tsBuildPaginator(_tsPersonPage, totalPages, _tsTotalPersons, 'tsPersonGoPage'));
}

function tsPersonGoPage(page) {
    const totalPages = Math.ceil(_tsTotalPersons / TS_PAGE_SIZE) || 1;
    if (page < 0 || page >= totalPages) return;
    _tsPersonPage = page;
    _tsLoad();
    document.getElementById('tsList')?.scrollTo(0, 0);
}

function _tsSetPaginator(html) {
    const bar = document.getElementById('tsPaginatorBar');
    if (bar) bar.innerHTML = html;
}

function _tsBuildPaginator(page, totalPages, total, onPageFn) {
    const countHtml = `<span style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx2);padding:0 8px;">${total.toLocaleString()}</span>`;
    return buildPaginator(page, totalPages, onPageFn, countHtml);
}

function rerenderTimeSpentTranslations() {
    if (_tsData) {
        tsRender();
        return;
    }

    const list = document.getElementById('tsList');
    if (!list) return;

    if (_tsLoading) {
        list.innerHTML = `<div class="ts-loading"><span class="msi ts-spin" style="font-size:22px;color:var(--accent);">sync</span><span style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('timespent.loading', 'Calculating stats...')}</span></div>`;
        return;
    }

    list.innerHTML = `<div class="ts-empty"><span class="msi" style="font-size:28px;color:var(--tx2);">schedule</span><div>${t('timespent.empty.no_session', 'No session data yet.')}<br><span style="font-size:calc(11px + var(--fs-off, 0px));">${t('timespent.empty.no_session_hint', 'Join some worlds in VRChat to see stats.')}</span></div></div>`;
}

document.documentElement.addEventListener('languagechange', rerenderTimeSpentTranslations);

function tsRenderGroups() {
    const summary = document.getElementById('tsSummary');
    if (!summary) return;

    summary.innerHTML = `
        <div class="ts-stat-row">
            <div class="ts-stat">
                <span class="msi ts-stat-icon">schedule</span>
                <div class="ts-stat-val">${tsFmtTimeDH(_tsGlobalGroupSeconds)}</div>
                <div class="ts-stat-label">${t('timespent.summary.total_group_time', 'Total Group Time')}</div>
            </div>
            <div class="ts-stat">
                <span class="msi ts-stat-icon">groups</span>
                <div class="ts-stat-val">${_tsAllUniqueGroups}</div>
                <div class="ts-stat-label">${t('timespent.summary.unique_groups', 'Unique Groups')}</div>
            </div>
            <div class="ts-stat">
                <span class="msi ts-stat-icon">favorite</span>
                <div class="ts-stat-val">${_tsGlobalTopGroupName ? esc(_tsGlobalTopGroupName) : '-'}</div>
                <div class="ts-stat-label">${t('timespent.summary.favourite_group', 'Favourite Group')}</div>
            </div>
            <div class="ts-stat">
                <span class="msi ts-stat-icon">login</span>
                <div class="ts-stat-val">${_tsGlobalGroupJoins.toLocaleString()}</div>
                <div class="ts-stat-label">${t('timespent.summary.group_joins', 'Group Joins')}</div>
            </div>
        </div>`;

    _tsShowSearch();
    tsRenderGroupItems();
}

function tsRenderGroupItems() {
    const groups = _tsData?.groups || [];
    const tsList = document.getElementById('tsList');
    if (!tsList) return;

    if (_tsAllUniqueGroups === 0) {
        tsList.innerHTML = `<div class="ts-empty"><span class="msi" style="font-size:28px;color:var(--tx2);">groups</span><div>${t('timespent.empty.no_group_data', 'No group data yet.')}<br><span style="font-size:calc(11px + var(--fs-off, 0px));">${t('timespent.empty.no_group_data_hint', 'Join a group instance to start collecting stats.')}</span></div></div>`;
        _tsSetPaginator('');
        return;
    }

    if (groups.length === 0) {
        tsList.innerHTML = `<div class="ts-empty"><span class="msi" style="font-size:28px;color:var(--tx3);">search_off</span><div>${t('timespent.empty.no_group_match', 'No groups match your search.')}</div></div>`;
        _tsSetPaginator('');
        return;
    }

    const totalPages = Math.ceil(_tsTotalGroups / TS_PAGE_SIZE) || 1;
    const maxSec = _tsData.maxGroupSeconds || groups[0].seconds || 1;
    const rows = groups.map((group, i) => {
        const pct = ((group.seconds / maxSec) * 100).toFixed(2);
        const rank = group.rank ?? (_tsGroupPage * TS_PAGE_SIZE + i + 1);
        const icon = group.iconUrl
            ? `<img class="ts-item-thumb" src="${esc(imgThumb(group.iconUrl, 96))}" onerror="this.style.display='none'">`
            : `<div class="ts-item-thumb ts-thumb-placeholder"><span class="msi" style="font-size:18px;color:var(--tx2);">groups</span></div>`;
        const click = group.groupId ? `onclick="navOpenModal('group','${esc(group.groupId)}','${esc(group.groupName || '')}')" style="cursor:pointer"` : '';
        const joins = tf(`timespent.join.${group.joins === 1 ? 'one' : 'other'}`, { count: group.joins }, `${group.joins} join${group.joins === 1 ? '' : 's'}`);
        const label = group.groupName || group.shortCode || t('timespent.unknown_group', 'Unknown Group');

        return `
        <div class="ts-item" ${click}>
            <div class="ts-item-rank">#${rank}</div>
            ${icon}
            <div class="ts-item-body">
                <div class="ts-item-name">${esc(label)}</div>
                <div class="ts-item-meta">
                    <span class="msi" style="font-size:12px;color:var(--tx2);">login</span>
                    <span>${joins}</span>
                </div>
                <div class="ts-bar-wrap">
                    <div class="ts-bar" style="width:${pct}%"></div>
                </div>
            </div>
            <div class="ts-item-time">${tsFmtTime(group.seconds)}</div>
        </div>`;
    }).join('');

    tsList.innerHTML = `<div class="ts-items">${rows}</div>`;
    _tsSetPaginator(totalPages > 1
        ? buildPaginator(_tsGroupPage, totalPages, 'tsGroupGoPage')
        : '');
}

function tsGroupGoPage(page) {
    if (page < 0) return;
    _tsGroupPage = page;
    _tsLoad();
    document.getElementById('tsList')?.scrollTo(0, 0);
}
