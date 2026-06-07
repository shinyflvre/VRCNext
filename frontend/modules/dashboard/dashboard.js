/* === Dashboard === */

let _dashOnlineCount = 0;
let _dashOnlineCountLastFetch = 0;
function updateDashHeroStats() {
    const statsEl = document.getElementById('dashHeroStats');
    if (!statsEl) return;
    const hasUser = !!currentVrcUser;
    statsEl.style.display = hasUser ? 'flex' : 'none';
    if (!hasUser) return;
    const onlineEl  = document.getElementById('dashStatOnline');
    const friendsEl = document.getElementById('dashStatFriends');
    const webEl     = document.getElementById('dashStatSession');
    if (onlineEl) onlineEl.textContent = _dashOnlineCount > 0 ? _dashOnlineCount.toLocaleString() : '—';
    if (typeof vrcFriendsData !== 'undefined') {
        const inGame = vrcFriendsData.filter(f => f.presence === 'game').length;
        const onWeb  = vrcFriendsData.filter(f => f.presence === 'web').length;
        if (friendsEl) friendsEl.textContent = String(inGame);
        if (webEl)     webEl.textContent     = String(onWeb);
    }
}

function getDashFriendCountLabel(count, keyBase) {
    return count === 1
        ? tf(`${keyBase}.one`, { count }, '{count} friend')
        : tf(`${keyBase}.other`, { count }, '{count} friends');
}

function updateDashSub() {
    const name = currentVrcUser?.displayName;
    const status = name
        ? (currentVrcUser.statusDescription || statusLabel(currentVrcUser.status))
        : t('dashboard.sub.connect_world', 'Connect to VRChat to see your world');

    document.getElementById('dashSub').textContent = status;
}

function _dashBgSrc(p) {
    if (location.protocol === 'file:') return 'file:///' + p.replace(/\\/g, '/');
    return `http://localhost:${_localHttpPort}/dashbg?file=${encodeURIComponent(p)}`;
}

function renderDashboard() {
    const name = currentVrcUser?.displayName;
    document.getElementById('dashWelcome').innerHTML = name
        ? tf('dashboard.welcome.named', { name: `<span style="color:var(--accent)">${esc(name)}</span>` }, 'Welcome, {name}!')
        : esc(t('dashboard.welcome.default', 'Welcome!'));
    updateDashSub();
    updateDashHeroStats();

    const bgEl = document.getElementById('dashHeroBg');
    const isVideoBg = dashBgPath && dashBgPath.toLowerCase().endsWith('.mp4');
    if (isVideoBg) {
        const src = dashBgDataUri || _dashBgSrc(dashBgPath);
        const existingVid = bgEl.querySelector('video');
        if (existingVid && existingVid.getAttribute('data-src') === src) {
            // same source — keep the video playing, don't recreate it
        } else {
            if (existingVid) existingVid.remove();
            bgEl.style.backgroundImage = '';
            const vid = document.createElement('video');
            vid.setAttribute('data-src', src);
            vid.src = src;
            vid.autoplay = true;
            vid.loop = true;
            vid.muted = true;
            vid.playsInline = true;
            vid.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;object-fit:cover;';
            bgEl.appendChild(vid);
        }
    } else {
        const existingVid = bgEl.querySelector('video');
        if (existingVid) existingVid.remove();
        if (dashBgDataUri) {
            bgEl.style.backgroundImage = `url('${dashBgDataUri}')`;
        } else if (dashBgPath) {
            const fileUri = _dashBgSrc(dashBgPath);
            bgEl.style.backgroundImage = `url('${fileUri}')`;
        } else {
            bgEl.style.backgroundImage = `url('fallback_bg.png')`;
        }
    }
    if (currentSpecialTheme === 'auto') applyAutoColor();

    renderDashWorlds();
    renderDashFriendsFeed();
    renderDashFriendsLocationSmall();
    renderDashFavWorlds();
    renderDashFavAvatars();
    renderDashOwnAvatars();
    renderDashRecentPhotos();
    renderDashRecentlyVisited();
    renderDashPopularWorlds();
    renderDashActiveWorlds();
    renderDashGroupActivity();
    renderDashGroupActivityInstances();
    renderDashGroupActivityInstancesSmall();
    renderDashRecentTimeline();
    if (currentVrcUser && _dashUpcomingEvents === null && !_dashUpcomingLoading && !_dashLayout.hidden.includes('upcoming_events')) {
        refreshDashUpcomingEvents();
    }
    const now = Date.now();
    if (now - _dashOnlineCountLastFetch >= 10 * 60 * 1000) {
        _dashOnlineCountLastFetch = now;
        sendToCS({ action: 'vrcGetOnlineCount' });
    }
}

function requestWorldResolution() {
    if (!vrcFriendsData.length) return;
    const worldIds = new Set();
    const groupIds = new Set();
    vrcFriendsData.forEach(f => {
        const { worldId, ownerId } = parseFriendLocation(f.location);
        if (worldId && worldId.startsWith('wrld_') && !dashWorldCache[worldId]) worldIds.add(worldId);
        if (ownerId && ownerId.startsWith('grp_') && !dashGroupCache[ownerId]) groupIds.add(ownerId);
    });
    if (worldIds.size > 0) {
        sendToCS({ action: 'vrcResolveWorlds', worldIds: Array.from(worldIds) });
    }
    if (groupIds.size > 0) {
        sendToCS({ action: 'vrcResolveGroups', groupIds: Array.from(groupIds) });
    }
}

function renderDashWorlds() {
    const el = document.getElementById('dashFavWorlds');
    if (!currentVrcUser || !vrcFriendsLoaded) {
        el.innerHTML = sk('world', 3);
        return;
    }
    if (!vrcFriendsData.length) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.worlds.empty', 'No friends in worlds right now')}</div>`;
        return;
    }

    // Group friends by worldId
    const worlds = {};
    vrcFriendsData.forEach(f => {
        const { worldId, instanceType } = parseFriendLocation(f.location);
        if (!worldId || !worldId.startsWith('wrld_')) return;

        if (!worlds[worldId]) {
            const cached = dashWorldCache[worldId];
            worlds[worldId] = {
                worldId: worldId,
                name: cached?.name || null,
                thumb: cached?.thumbnailImageUrl || cached?.imageUrl || '',
                instanceType: instanceType,
                friends: [],
                location: f.location,
                instances: new Set()
            };
        }
        worlds[worldId].friends.push(f);
        worlds[worldId].instances.add(f.location);
    });

    const worldList = Object.values(worlds);
    if (!worldList.length) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.worlds.empty', 'No friends in worlds right now')}</div>`;
        return;
    }

    // Check if we have unresolved worlds
    const unresolved = worldList.filter(w => !w.name);
    if (unresolved.length > 0 && Object.keys(dashWorldCache).length === 0) {
        // Show placeholder while resolving
        el.innerHTML = worldList.map(w => {
            const friendAvatars = w.friends.slice(0, 5).map(f => {
                const img = f.image || '';
                return img
                    ? `<img class="cc-friend-av" src="${img}" title="${esc(f.displayName)}" onerror="this.style.display='none'">`
                    : `<div class="cc-friend-av" title="${esc(f.displayName)}" style="display:flex;align-items:center;justify-content:center;font-size:9px;font-weight:700;color:var(--tx3)">${esc((f.displayName||'?')[0])}</div>`;
            }).join('');
            const extra = w.friends.length > 5 ? `<span class="cc-extra">+${w.friends.length - 5}</span>` : '';
            const friendCountLabel = getDashFriendCountLabel(w.friends.length, 'dashboard.worlds.count_world');
            return `<div class="vrcn-content-card">
                <div class="cc-bg"></div>
                <div class="cc-scrim"></div>
                <div class="cc-content">
                    <div class="cc-name" style="color:var(--tx3)">${t('dashboard.worlds.loading_world', 'Loading world...')}</div>
                    <div class="cc-friends-row">${friendAvatars}${extra}</div>
                    <div class="cc-bottom-row">
                        <div class="cc-meta"><span class="msi">person</span>${friendCountLabel}</div>
                    </div>
                </div>
            </div>`;
        }).join('');
        return;
    }

    el.innerHTML = worldList.map(w => {
        const friendAvatars = w.friends.slice(0, 5).map(f => {
            const img = f.image || '';
            return img
                ? `<img class="cc-friend-av" src="${img}" title="${esc(f.displayName)}" onerror="this.style.display='none'">`
                : `<div class="cc-friend-av" title="${esc(f.displayName)}" style="display:flex;align-items:center;justify-content:center;font-size:9px;font-weight:700;color:var(--tx3)">${esc((f.displayName||'?')[0])}</div>`;
        }).join('');
        const extra = w.friends.length > 5 ? `<span class="cc-extra">+${w.friends.length - 5}</span>` : '';
        const thumbStyle = w.thumb ? `background-image:url('${cssUrl(w.thumb)}')` : '';
        const wid = (w.worldId || '').replace(/'/g, "\\'");
        const safeLoc = (w.location || '').replace(/'/g, "\\'");
        const displayName = w.name || w.worldId;
        const instCount = w.instances ? w.instances.size : 1;
        const countLabel = instCount > 1
            ? getDashFriendCountLabel(w.friends.length, 'dashboard.worlds.count_world')
            : getDashFriendCountLabel(w.friends.length, 'dashboard.worlds.count_here');
        const { cls: dwInstCls, label: dwInstLabel } = getInstanceBadge(w.instanceType);
        const dwAgeGate = (w.location || '').includes('~ageGate')
            ? `<span class="vrcn-badge" style="background:rgba(255,75,85,.15);color:var(--err);">${esc(t('worlds.instances.age_gated', 'Age Gated'))}</span>` : '';
        return `<div class="vrcn-content-card" onclick="openFriendLocationDetail('${wid}','${safeLoc}')">
            <div class="cc-bg" style="${thumbStyle}"></div>
            <div class="cc-scrim"></div>
            <div class="cc-badges-top"><span class="vrcn-badge ${dwInstCls}">${dwInstLabel}</span>${dwAgeGate}</div>
            <div class="cc-content">
                <div class="cc-name">${esc(displayName)}</div>
                <div class="cc-friends-row">${friendAvatars}${extra}</div>
                <div class="cc-bottom-row">
                    <div class="cc-meta"><span class="msi">person</span>${countLabel}</div>
                    ${instCount > 1 ? `<span style="font-size:9px;color:rgba(255,255,255,.4);">${tf('dashboard.worlds.instances', { count: instCount }, '{count} instances')}</span>` : ''}
                </div>
            </div>
        </div>`;
    }).join('');
}

function renderDashFriendsFeed() {
    const el = document.getElementById('dashFriendsFeed');
    if (!currentVrcUser || !vrcFriendsLoaded) {
        el.innerHTML = sk('feed', 8);
        return;
    }
    if (!vrcFriendsData.length) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.friends.empty', 'No friends online')}</div>`;
        return;
    }
    const activeFriends = vrcFriendsData.filter(f => f.presence !== 'offline');
    el.innerHTML = activeFriends.slice(0, 12).map(f => {
        const img = f.image || '';
        const imgTag = img
            ? `<img class="dash-feed-avatar" src="${img}" onerror="this.style.display='none'">`
            : `<div class="dash-feed-avatar" style="display:flex;align-items:center;justify-content:center;font-size:12px;font-weight:700;color:var(--tx3)">${esc((f.displayName||'?')[0])}</div>`;
        const { worldId } = parseFriendLocation(f.location);
        const cached = worldId ? dashWorldCache[worldId] : null;
        const isPrivate = !f.location || f.location === 'private';
        const loc = f.presence === 'web'
            ? t('dashboard.friends.location_web', 'Web / Mobile')
            : (isPrivate ? t('dashboard.friends.location_private', 'Private Instance') : (cached?.name || t('dashboard.friends.location_world', 'In World')));
        const fid = (f.id || '').replace(/'/g, "\\'");
        const dotClass = f.presence === 'web' ? 'vrc-status-ring' : 'vrc-status-dot';
        return `<div class="dash-feed-card" onclick="openFriendDetail('${fid}')">
            ${imgTag}
            <div class="dash-feed-info">
                <div class="dash-feed-name"><span class="${dotClass} ${statusDotClass(f.status)}" style="width:7px;height:7px;"></span>${esc(f.displayName)}</div>
                <div class="dash-feed-status">${esc(f.statusDescription || statusLabel(f.status))}</div>
                <div class="dash-feed-loc">${esc(loc)}</div>
            </div>
        </div>`;
    }).join('');
}

function browseDashBg() {
    sendToCS({ action: 'browseDashBg' });
}

/* === Dashboard — Friends Location (Small) shelf === */

function openFriendLocationDetail(worldId, location) {
    const cached = (worldId && typeof dashWorldCache !== 'undefined') ? (dashWorldCache[worldId] || {}) : {};
    const { instanceType, ownerId } = (typeof parseFriendLocation === 'function') ? parseFriendLocation(location) : { instanceType: 'public', ownerId: '' };
    openInstanceDetailFromData({
        worldId,
        location,
        worldName:  cached.name || worldId || '',
        worldThumb: cached.thumbnailImageUrl || cached.imageUrl || '',
        instanceType,
        ownerId:    ownerId || '',
        ownerName:  '',
        ownerGroup: '',
        allInstances: true,
    });
}

function renderDashFriendsLocationSmall() {
    const el = document.getElementById('dashFriendLocSmallShelf');
    if (!el) return;
    if (!currentVrcUser || !vrcFriendsLoaded) {
        el.innerHTML = _dashWorldShelfSkeleton();
        return;
    }
    const inWorld = vrcFriendsData.filter(f => {
        const { worldId } = parseFriendLocation(f.location);
        return worldId && worldId.startsWith('wrld_');
    });
    if (!inWorld.length) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.section.friend_locations_small_empty', 'No friends in worlds right now')}</div>`;
        return;
    }
    el.innerHTML = inWorld.slice(0, 24).map(f => {
        const { worldId } = parseFriendLocation(f.location);
        const cached   = worldId ? dashWorldCache[worldId] : null;
        const thumb    = cached?.thumbnailImageUrl || cached?.imageUrl || '';
        const wname    = esc(cached?.name || t('dashboard.friends.location_world', 'In World'));
        const wid      = (worldId || '').replace(/'/g, "\\'");
        const safeLoc  = (f.location || '').replace(/'/g, "\\'");
        const img      = f.image || '';
        const dotClass = f.presence === 'web' ? 'vrc-status-ring' : 'vrc-status-dot';
        const avatarEl = img
            ? `<img class="dash-flocs-avatar" src="${img}" onerror="this.style.display='none'">`
            : `<div class="dash-flocs-avatar dash-flocs-avatar-letter">${esc((f.displayName||'?')[0])}</div>`;
        const worldThumb = thumb
            ? `<img class="dash-flocs-world-thumb" src="${cssUrl(thumb)}" alt="" loading="lazy" onerror="this.style.display='none'">`
            : '';
        return `<div class="dash-flocs-card" onclick="openFriendLocationDetail('${wid}','${safeLoc}')">
            <div class="dash-flocs-avatar-wrap">
                ${avatarEl}
                <span class="${dotClass} ${statusDotClass(f.status)} dash-flocs-dot"></span>
            </div>
            <div class="dash-flocs-info">
                <div class="dash-flocs-name">${esc(f.displayName)}</div>
                <div class="dash-flocs-status">${esc(f.statusDescription || statusLabel(f.status))}</div>
                <div class="dash-flocs-world"><span class="msi">public</span>${wname}</div>
            </div>
            ${worldThumb}
        </div>`;
    }).join('');
}

/* === My Instances === */
let _myInstancesData = [];

function loadMyInstances() {
    sendToCS({ action: 'vrcGetMyInstances' });
}

function refreshMyInstances() {
    const btn = document.getElementById('miRefreshBtn');
    if (btn) btn.classList.add('spinning');
    sendToCS({ action: 'vrcGetMyInstances' });
    // Spinner stops when renderMyInstances is called (response arrives)
}

function renderMyInstances(instances) {
    _myInstancesData = instances || [];
    const label = document.getElementById('dashMyInstancesLabel');
    const grid  = document.getElementById('dashMyInstances');
    const btn   = document.getElementById('miRefreshBtn');
    if (btn) btn.classList.remove('spinning');
    if (!label || !grid) return;

    // CSS class controls wrap visibility; data-hidden (layout) takes priority via !important
    const wrap = label.closest('.dash-section-wrap');
    if (wrap) wrap.classList.toggle('mi-has-instances', !!_myInstancesData.length);

    if (!_myInstancesData.length) {
        label.style.display = 'none';
        grid.style.display  = 'none';
        return;
    }
    // If layout has hidden this section, don't render content
    if (wrap?.hasAttribute('data-hidden')) return;

    label.style.display = '';
    grid.style.display  = '';

    grid.innerHTML = _myInstancesData.map(inst => {
        const { cls, label: typeLabel } = getInstanceBadge(inst.instanceType);
        const thumbStyle = inst.worldThumb ? `background-image:url('${cssUrl(inst.worldThumb)}')` : '';
        const wid = (inst.worldId || '').replace(/'/g, "\\'");
        const count = inst.userCount || 0;
        const cap   = inst.capacity  || 0;
        const countStr = cap > 0
            ? tf('dashboard.instances.players_with_capacity', { count, capacity: cap }, '{count}/{capacity} players')
            : tf('dashboard.instances.players', { count }, '{count} players');
        const safeLoc = (inst.location || '').replace(/'/g, "\\'");
        const miAgeGate = (inst.location || '').includes('~ageGate')
            ? `<span class="vrcn-badge" style="background:rgba(255,75,85,.15);color:var(--err);">${esc(t('worlds.instances.age_gated', 'Age Gated'))}</span>` : '';
        return `<div class="vrcn-content-card" onclick="openMyInstanceDetail('${wid}','${safeLoc}')" data-location="${esc(inst.location || '')}">
            <div class="cc-bg" style="${thumbStyle}"></div>
            <div class="cc-scrim"></div>
            <div class="cc-badges-top"><span class="vrcn-badge ${cls}">${esc(typeLabel)}</span>${miAgeGate}</div>
            <div class="cc-content">
                <div class="cc-name">${esc(inst.worldName || inst.worldId || t('dashboard.instances.unknown_world', 'Unknown World'))}</div>
                <div class="cc-bottom-row">
                    <div class="cc-meta"><span class="msi">person</span>${esc(countStr)}</div>
                </div>
            </div>
        </div>`;
    }).join('');
}

function removeMyInstance(location) {
    sendToCS({ action: 'vrcRemoveMyInstance', location });
    _myInstancesData = _myInstancesData.filter(i => i.location !== location);
    renderMyInstances(_myInstancesData);
    closeMyInstanceDetail();
    showToast(true, t('dashboard.instances.removed', 'Instance removed.'));
}

/* === VRChat News Widget === */

const _VRC_NEWS_TTL = 30 * 60 * 1000;
let _vrcNewsCache   = { items: [], ts: 0 };
let _vrcNewsLoading = false;

function _fetchVrcNews() {
    if (_vrcNewsLoading) return;
    if (Date.now() - _vrcNewsCache.ts < _VRC_NEWS_TTL && _vrcNewsCache.items.length) {
        renderDashVrcNews();
        return;
    }
    _vrcNewsLoading = true;
    renderDashVrcNews();
    sendToCS({ action: 'vrcGetNews' });
}

function onVrcNews(items) {
    _vrcNewsLoading = false;
    _vrcNewsCache   = { items: items || [], ts: Date.now() };
    renderDashVrcNews();
}

function _fmtNewsDate(str) {
    if (!str) return '';
    try {
        const d = new Date(str);
        if (isNaN(d.getTime())) return '';
        return d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
    } catch { return ''; }
}

function renderDashVrcNews() {
    const el = document.getElementById('dashVrcNewsGrid');
    if (!el) return;
    if (_vrcNewsLoading && !_vrcNewsCache.items.length) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.news.loading', 'Loading news...')}</div>`;
        return;
    }
    if (!_vrcNewsCache.items.length) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.news.empty', 'No news available')}</div>`;
        return;
    }
    el.innerHTML = _vrcNewsCache.items.map(item => `
        <div class="dash-news-card" onclick="sendToCS({action:'openUrl',url:'${jsq(item.link)}'})">
            ${item.img ? `<div class="dash-news-img-wrap"><img class="dash-news-img" src="${esc(item.img)}" alt="" loading="lazy" onerror="this.parentElement.style.display='none'"></div>` : ''}
            <div class="dash-news-body">
                <div class="dash-news-title">${esc(item.title)}</div>
                ${item.excerpt ? `<div class="dash-news-excerpt">${esc(item.excerpt)}</div>` : ''}
                <div class="dash-news-meta">${esc(_fmtNewsDate(item.pubDate))}</div>
            </div>
        </div>
    `).join('');
}

function refreshDashVrcNews() {
    const btn = document.getElementById('dashVrcNewsRefreshBtn');
    if (btn) btn.classList.add('spinning');
    _vrcNewsCache = { items: [], ts: 0 };
    _fetchVrcNews();
    setTimeout(() => { if (btn) btn.classList.remove('spinning'); }, 1500);
}

/* === Discovery Section === */
let _discTab  = 'popular';
let _discPage = 0;
const DISC_PER_PAGE   = 8;
const DISC_CACHE_TTL  = 10 * 60 * 1000;
let _popularCache = { worlds: [], ts: 0 };
let _activeCache  = { worlds: [], ts: 0 };
let _recentCache  = { worlds: [], ts: 0 };
let _recentInFlight  = false;
let _popularInFlight = false;
let _activeInFlight  = false;

// Refresh every 10 minutes — only when Dashboard tab is active and at least one consumer is visible
const _discRefreshInterval = setInterval(() => {
    const tab0 = document.getElementById('tab0');
    if (!tab0 || !tab0.classList.contains('active')) return;
    const popHidden = _dashLayout.hidden.includes('discovery') && _dashLayout.hidden.includes('popular_worlds');
    const actHidden = _dashLayout.hidden.includes('discovery') && _dashLayout.hidden.includes('active_worlds');
    if (!popHidden) sendToCS({ action: 'vrcGetPopularWorlds' });
    if (!actHidden) sendToCS({ action: 'vrcGetActiveWorlds' });
}, DISC_CACHE_TTL);

// Group Activity: refresh every 10 min
const _groupInstRefreshInterval = setInterval(() => {
    if (!window._groupInstInFlight) {
        window._groupInstInFlight = true;
        sendToCS({ action: 'vrcGetDashGroupInstances' });
    }
}, 10 * 60 * 1000);

// Recently Visited, Fav Worlds, Fav Avatars: refresh every 60 min
const _dashSlow60Interval = setInterval(() => {
    const tab0 = document.getElementById('tab0');
    if (!tab0 || !tab0.classList.contains('active')) return;
    if (!_dashLayout.hidden.includes('recently_visited')) sendToCS({ action: 'vrcGetRecentWorlds' });
    if (!_dashLayout.hidden.includes('fav_worlds')) sendToCS({ action: 'vrcGetFavoriteWorlds' });
    if (!_dashLayout.hidden.includes('fav_avatars')) sendToCS({ action: 'vrcGetAvatars', filter: 'favorites' });
}, 60 * 60 * 1000);

// Own Avatars, Upcoming Events: refresh every 120 min
const _dashSlow120Interval = setInterval(() => {
    const tab0 = document.getElementById('tab0');
    if (!tab0 || !tab0.classList.contains('active')) return;
    if (!_dashLayout.hidden.includes('own_avatars')) sendToCS({ action: 'vrcGetAvatars', filter: 'own' });
    if (!_dashLayout.hidden.includes('upcoming_events') && !_dashUpcomingLoading) refreshDashUpcomingEvents();
}, 120 * 60 * 1000);

function fetchWorldTabs() {
    if (!(_dashLayout.hidden.includes('discovery') && _dashLayout.hidden.includes('popular_worlds')))
        sendToCS({ action: 'vrcGetPopularWorlds' });
    if (!(_dashLayout.hidden.includes('discovery') && _dashLayout.hidden.includes('active_worlds')))
        sendToCS({ action: 'vrcGetActiveWorlds' });
}

function setDiscoveryTab(tab) {
    _discTab  = tab;
    _discPage = 0;
    document.querySelectorAll('.disc-tab').forEach(btn => {
        btn.classList.toggle('active', btn.dataset.tab === tab);
    });
    if (tab === 'popular') _fetchPopularWorlds();
    else if (tab === 'active') _fetchActiveWorlds();
    else if (tab === 'recent') _fetchRecentWorlds();
}

function _fetchPopularWorlds() {
    if (Date.now() - _popularCache.ts < DISC_CACHE_TTL && _popularCache.worlds.length) {
        renderDiscoverySection();
        return;
    }
    document.getElementById('dashDiscoveryGrid').innerHTML = `<div class="empty-msg">${t('dashboard.discovery.loading', 'Loading worlds...')}</div>`;
    sendToCS({ action: 'vrcGetPopularWorlds' });
}

function _fetchActiveWorlds() {
    if (Date.now() - _activeCache.ts < DISC_CACHE_TTL && _activeCache.worlds.length) {
        renderDiscoverySection();
        return;
    }
    document.getElementById('dashDiscoveryGrid').innerHTML = `<div class="empty-msg">${t('dashboard.discovery.loading', 'Loading worlds...')}</div>`;
    sendToCS({ action: 'vrcGetActiveWorlds' });
}

function _fetchRecentWorlds() {
    if (Date.now() - _recentCache.ts < DISC_CACHE_TTL && _recentCache.worlds.length) {
        renderDiscoverySection();
        return;
    }
    document.getElementById('dashDiscoveryGrid').innerHTML = `<div class="empty-msg">${t('dashboard.discovery.loading', 'Loading worlds...')}</div>`;
    sendToCS({ action: 'vrcGetRecentWorlds' });
}

function onRecentWorlds(worlds) {
    _recentInFlight = false;
    _recentCache = { worlds: worlds || [], ts: Date.now() };
    if (_discTab === 'recent') renderDiscoverySection();
    renderDashRecentlyVisited();
}

function onPopularWorlds(worlds) {
    _popularInFlight = false;
    _popularCache = { worlds: worlds || [], ts: Date.now() };
    if (_discTab === 'popular') renderDiscoverySection();
    renderDashPopularWorlds();
}

function onActiveWorlds(worlds) {
    _activeInFlight = false;
    _activeCache = { worlds: worlds || [], ts: Date.now() };
    if (_discTab === 'active') renderDiscoverySection();
    renderDashActiveWorlds();
}

function discPageChange(dir) {
    const cache = _discTab === 'popular' ? _popularCache : _discTab === 'active' ? _activeCache : _recentCache;
    const maxPage = Math.max(0, Math.ceil(cache.worlds.length / DISC_PER_PAGE) - 1);
    _discPage = Math.max(0, Math.min(maxPage, _discPage + dir));
    renderDiscoverySection();
}

function renderDiscoverySection() {
    const grid       = document.getElementById('dashDiscoveryGrid');
    const pagination = document.getElementById('discPagination');
    if (!grid) return;

    const cache = _discTab === 'popular' ? _popularCache : _discTab === 'active' ? _activeCache : _recentCache;
    if (!cache.worlds.length) {
        grid.innerHTML = `<div class="empty-msg">${t('dashboard.discovery.loading', 'Loading worlds...')}</div>`;
        if (pagination) pagination.style.display = 'none';
        return;
    }

    const totalPages = Math.max(1, Math.ceil(cache.worlds.length / DISC_PER_PAGE));
    _discPage = Math.min(_discPage, totalPages - 1);
    const page = cache.worlds.slice(_discPage * DISC_PER_PAGE, (_discPage + 1) * DISC_PER_PAGE);

    grid.innerHTML = page.map(w => {
        const name  = esc(w.name || w.id || '');
        const thumb = w.thumbnailImageUrl || w.imageUrl || '';
        const thumbStyle = thumb ? `background-image:url('${cssUrl(thumb)}')` : '';
        const wid = (w.id || '').replace(/'/g, "\\'");
        const occupants = w.occupants ?? w.publicOccupants ?? 0;
        const playingStr = occupants > 0
            ? tf('dashboard.discovery.playing', { count: occupants.toLocaleString() }, '{count} playing')
            : '';
        return `<div class="vrcn-content-card" onclick="openWorldSearchDetail('${wid}')">
            <div class="cc-bg" style="${thumbStyle}"></div>
            <div class="cc-scrim"></div>
            <div class="cc-content">
                <div class="cc-name">${name}</div>
                ${playingStr ? `<div class="cc-bottom-row"><div class="cc-meta"><span class="msi">person</span>${playingStr}</div></div>` : ''}
            </div>
        </div>`;
    }).join('');

    if (pagination) {
        pagination.style.display = totalPages > 1 ? 'flex' : 'none';
        const lbl = document.getElementById('discPageLabel');
        if (lbl) lbl.textContent = `${_discPage + 1} / ${totalPages}`;
        const prev = document.getElementById('discPrevBtn');
        const next = document.getElementById('discNextBtn');
        if (prev) prev.disabled = _discPage === 0;
        if (next) next.disabled = _discPage >= totalPages - 1;
    }
}

/* === Dashboard — Favorite Worlds shelf === */

function refreshDashFavWorlds() {
    const btn = document.getElementById('dashFavWorldsRefreshBtn');
    if (btn) btn.classList.add('spinning');
    sendToCS({ action: 'vrcGetFavoriteWorlds' });
}

function renderDashFavWorlds() {
    const el = document.getElementById('dashFavWorldsShelf');
    if (!el) return;
    const _btn = document.getElementById('dashFavWorldsRefreshBtn');
    if (_btn) _btn.classList.remove('spinning');
    if (!currentVrcUser) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.favworlds.login', 'Login to see favorite worlds')}</div>`;
        return;
    }
    const worlds = (typeof favWorldsData !== 'undefined') ? favWorldsData : [];
    const loaded = (typeof _favWorldsLoaded !== 'undefined') ? _favWorldsLoaded : false;
    if (!worlds.length && !loaded) {
        if (_dashLayout.hidden.includes('fav_worlds')) return;
        el.innerHTML = _dashWorldShelfSkeleton();
        sendToCS({ action: 'vrcGetFavoriteWorlds' });
        return;
    }
    if (!worlds.length) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.favworlds.empty', 'No favorite worlds yet')}</div>`;
        return;
    }
    el.innerHTML = worlds.slice(0, 20).map(_dashWorldCard).join('');
}

/* === Dashboard — Favorite Avatars shelf === */

let _dashFavAvatarsRequested = false;

function refreshDashFavAvatars() {
    const btn = document.getElementById('dashFavAvatarsRefreshBtn');
    if (btn) btn.classList.add('spinning');
    sendToCS({ action: 'vrcGetAvatars', filter: 'favorites' });
}

function renderDashFavAvatars() {
    const el = document.getElementById('dashFavAvatarsShelf');
    if (!el) return;
    const _btn = document.getElementById('dashFavAvatarsRefreshBtn');
    if (_btn) _btn.classList.remove('spinning');
    if (!currentVrcUser) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.section.login', 'Login to VRChat')}</div>`;
        return;
    }
    const avatars = (typeof favAvatarsData !== 'undefined') ? favAvatarsData : [];
    if (!avatars.length && !_dashFavAvatarsRequested) {
        if (_dashLayout.hidden.includes('fav_avatars')) return;
        _dashFavAvatarsRequested = true;
        el.innerHTML = _dashWorldShelfSkeleton();
        sendToCS({ action: 'vrcGetAvatars', filter: 'favorites' });
        return;
    }
    if (!avatars.length) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.favavaatars.empty', 'No favorite avatars yet')}</div>`;
        return;
    }
    el.innerHTML = avatars.slice(0, 20).map(_dashAvatarCard).join('');
}

/* === Dashboard — Own Avatars shelf === */

let _dashOwnAvatarsRequested = false;

function refreshDashOwnAvatars() {
    const btn = document.getElementById('dashOwnAvatarsRefreshBtn');
    if (btn) btn.classList.add('spinning');
    sendToCS({ action: 'vrcGetAvatars', filter: 'own' });
}

function renderDashOwnAvatars() {
    const el = document.getElementById('dashOwnAvatarsShelf');
    if (!el) return;
    const _btn = document.getElementById('dashOwnAvatarsRefreshBtn');
    if (_btn) _btn.classList.remove('spinning');
    if (!currentVrcUser) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.section.login', 'Login to VRChat')}</div>`;
        return;
    }
    const avatars = (typeof avatarsData !== 'undefined') ? avatarsData : [];
    const loaded  = (typeof avatarsLoaded !== 'undefined') ? avatarsLoaded : false;
    if (!avatars.length && !loaded && !_dashOwnAvatarsRequested) {
        if (_dashLayout.hidden.includes('own_avatars')) return;
        _dashOwnAvatarsRequested = true;
        el.innerHTML = _dashWorldShelfSkeleton();
        sendToCS({ action: 'vrcGetAvatars', filter: 'own' });
        return;
    }
    if (!avatars.length) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.ownavatars.empty', 'No avatars found')}</div>`;
        return;
    }
    el.innerHTML = avatars.slice(0, 20).map(_dashAvatarCard).join('');
}

/* === Dashboard — Recent Photos shelf === */

let _dashPhotosRequested = false;

function renderDashRecentPhotos() {
    const el = document.getElementById('dashRecentPhotosShelf');
    if (!el) return;
    if (!currentVrcUser) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.section.login', 'Login to VRChat')}</div>`;
        return;
    }
    const files = (typeof libraryFiles !== 'undefined') ? libraryFiles : [];
    const photos = files.filter(f => f.type === 'image' && f.url).slice(0, 32);
    if (!photos.length) {
        if (!_dashPhotosRequested) {
            if (_dashLayout.hidden.includes('recent_photos')) return;
            _dashPhotosRequested = true;
            el.innerHTML = _dashWorldShelfSkeleton();
            sendToCS({ action: 'scanLibrary' });
        }
        return;
    }
    el.innerHTML = photos.map(f => {
        const thumbUrl  = f.url ? f.url + '?thumb=1' : '';
        const dateMatch = (f.name || '').match(/(\d{4}-\d{2}-\d{2})/);
        const dateStr   = dateMatch ? fmtShortDate(new Date(dateMatch[1] + 'T00:00:00')) : (f.time || '');
        const isHidden  = (typeof hiddenMedia !== 'undefined') && hiddenMedia.has(f.path);
        const pathJs    = jsq(f.path || '');
        return `<div class="dash-photo-item${isHidden ? ' dpi-hidden' : ''}" onclick="openPhotoDetail('${pathJs}')" title="${esc(f.name || '')}" data-path="${esc(f.path || '')}" data-url="${esc(f.url || '')}" data-type="image" data-name="${esc(f.name || '')}">
            <div class="dpi-img"${thumbUrl ? ` style="background-image:url('${cssUrl(thumbUrl)}')"` : ''}></div>
            <div class="dpi-date">${esc(dateStr)}</div>
        </div>`;
    }).join('');
}

/* === Dashboard — Avatar card (shared by fav + own shelves) === */

function _dashAvatarCard(a) {
    const thumb     = a.thumbnailImageUrl || a.imageUrl || '';
    const aid       = jsq(a.id || '');
    const isActive  = a.id === currentAvatarId;
    const activeBadge = (typeof avatarCurrentBadge === 'function') ? avatarCurrentBadge(isActive) : '';
    return `<div class="vrcn-content-card av-card${isActive ? ' av-active' : ''}" onclick="selectAvatar('${aid}')">
        <div class="cc-bg"${thumb ? ` style="background-image:url('${cssUrl(thumb)}')"` : ''}></div>
        <div class="cc-scrim"></div>
        <div class="cc-badges-top">${activeBadge}</div>
        <div class="cc-content"><div class="cc-name">${esc(a.name || t('avatars.labels.unnamed', 'Unnamed'))}</div></div>
    </div>`;
}

/* === Dashboard — World shelves (Recently Visited / Popular / Active) === */

function _dashWorldCard(w, showCount = true) {
    const thumb     = w.thumbnailImageUrl || w.imageUrl || '';
    const wid       = jsq(w.id || '');
    const occupants = w.occupants ?? w.publicOccupants ?? 0;
    const meta      = showCount && occupants > 0
        ? `<div class="cc-bottom-row"><div class="cc-meta"><span class="msi">person</span>${occupants.toLocaleString()}</div></div>`
        : '';
    return `<div class="vrcn-content-card" onclick="openWorldSearchDetail('${wid}')">
        <div class="cc-bg"${thumb ? ` style="background-image:url('${cssUrl(thumb)}')"` : ''}></div>
        <div class="cc-scrim"></div>
        <div class="cc-content"><div class="cc-name">${esc(w.name || w.id || '?')}</div>${meta}</div>
    </div>`;
}

function _dashWorldShelfSkeleton() {
    return Array.from({ length: 8 }, () => `<div class="vrcn-content-card sk-block" style="pointer-events:none;"></div>`).join('');
}

function refreshDashRecentlyVisited() {
    const btn = document.getElementById('dashRecentlyVisitedRefreshBtn');
    if (btn) btn.classList.add('spinning');
    _recentInFlight = true;
    sendToCS({ action: 'vrcGetRecentWorlds' });
}

function renderDashRecentlyVisited() {
    const el = document.getElementById('dashRecentlyVisitedShelf');
    if (!el) return;
    const _btn = document.getElementById('dashRecentlyVisitedRefreshBtn');
    if (_btn) _btn.classList.remove('spinning');
    if (!currentVrcUser) { el.innerHTML = `<div class="empty-msg">${t('dashboard.worlds.login','Login to see worlds')}</div>`; return; }
    const worlds = _recentCache.worlds;
    if (!worlds.length) { el.innerHTML = _dashWorldShelfSkeleton(); if (!_recentInFlight && !(_dashLayout.hidden.includes('recently_visited') && _dashLayout.hidden.includes('discovery'))) { _recentInFlight = true; sendToCS({ action: 'vrcGetRecentWorlds' }); } return; }
    el.innerHTML = worlds.slice(0, 20).map(w => _dashWorldCard(w, false)).join('');
}

function renderDashPopularWorlds() {
    const el = document.getElementById('dashPopularWorldsShelf');
    if (!el) return;
    if (!currentVrcUser) { el.innerHTML = `<div class="empty-msg">${t('dashboard.worlds.login','Login to see worlds')}</div>`; return; }
    const worlds = _popularCache.worlds;
    if (!worlds.length) { el.innerHTML = _dashWorldShelfSkeleton(); if (!_popularInFlight && !(_dashLayout.hidden.includes('popular_worlds') && _dashLayout.hidden.includes('discovery'))) { _popularInFlight = true; sendToCS({ action: 'vrcGetPopularWorlds' }); } return; }
    el.innerHTML = worlds.slice(0, 20).map(_dashWorldCard).join('');
}

function renderDashActiveWorlds() {
    const el = document.getElementById('dashActiveWorldsShelf');
    if (!el) return;
    if (!currentVrcUser) { el.innerHTML = `<div class="empty-msg">${t('dashboard.worlds.login','Login to see worlds')}</div>`; return; }
    const worlds = _activeCache.worlds;
    if (!worlds.length) { el.innerHTML = _dashWorldShelfSkeleton(); if (!_activeInFlight && !(_dashLayout.hidden.includes('active_worlds') && _dashLayout.hidden.includes('discovery'))) { _activeInFlight = true; sendToCS({ action: 'vrcGetActiveWorlds' }); } return; }
    el.innerHTML = worlds.slice(0, 20).map(_dashWorldCard).join('');
}

/* === Dashboard — Group Activity grid === */

function _dashGroupSkeleton() {
    const card = `<div class="dash-group-card" style="pointer-events:none;">
        <div class="dash-group-icon sk-block"></div>
        <div class="dash-group-info">
            <div class="sk-block" style="height:11px;width:70%;border-radius:4px;margin-bottom:5px;"></div>
            <div class="sk-block" style="height:9px;width:45%;border-radius:4px;"></div>
        </div>
    </div>`;
    return Array.from({ length: 4 }, () => card).join('');
}

function renderDashGroupActivity() {
    const el = document.getElementById('dashGroupActivityGrid');
    if (!el) return;
    if (!currentVrcUser) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.groups.login', 'Login to see your groups')}</div>`;
        return;
    }
    const groups = (typeof myGroups !== 'undefined') ? myGroups : [];
    const loaded = (typeof myGroupsLoaded !== 'undefined') ? myGroupsLoaded : false;
    if (!groups.length && !loaded) {
        if (_dashLayout.hidden.includes('groups')) return;
        el.innerHTML = _dashGroupSkeleton();
        sendToCS({ action: 'vrcGetMyGroups' });
        return;
    }
    if (!groups.length) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.groups.empty', 'No groups joined yet')}</div>`;
        return;
    }
    el.innerHTML = groups.slice(0, 8).map(g => {
        const gid  = jsq(g.id || '');
        const icon = g.iconUrl || '';
        const cnt  = g.memberCount || 0;
        const iconHtml = icon
            ? `<img src="${icon}" onerror="this.parentElement.innerHTML='<span class=msi>group</span>'">`
            : `<span class="msi">group</span>`;
        const metaHtml = cnt > 0
            ? `<span class="msi">person</span>${esc(cnt.toLocaleString())}`
            : (g.shortCode ? esc(g.shortCode) : '');
        return `<div class="dash-group-card" onclick="openGroupDetail('${gid}')">
            <div class="dash-group-icon">${iconHtml}</div>
            <div class="dash-group-info">
                <div class="dash-group-name">${esc(g.name || '?')}</div>
                <div class="dash-group-meta">${metaHtml}</div>
            </div>
        </div>`;
    }).join('');
}

/* === Dashboard — Group Activity Instances === */

let _dashGroupInstances = null;

function loadDashGroupInstances() {
    if (window._groupInstInFlight) return;
    window._groupInstInFlight = true;
    sendToCS({ action: 'vrcGetDashGroupInstances' });
}

function refreshDashGroupInstances() {
    const btn1 = document.getElementById('dashGroupActivityRefreshBtn');
    const btn2 = document.getElementById('dashGroupActivitySmallRefreshBtn');
    if (btn1) btn1.classList.add('spinning');
    if (btn2) btn2.classList.add('spinning');
    window._groupInstInFlight = false;
    loadDashGroupInstances();
}

function onDashGroupInstances(instances) {
    window._groupInstInFlight = false;
    _dashGroupInstances = instances || [];
    const btn1 = document.getElementById('dashGroupActivityRefreshBtn');
    const btn2 = document.getElementById('dashGroupActivitySmallRefreshBtn');
    if (btn1) btn1.classList.remove('spinning');
    if (btn2) btn2.classList.remove('spinning');
    renderDashGroupActivityInstances();
    renderDashGroupActivityInstancesSmall();
}

function _ensureDashGroupInstancesLoaded() {
    if (_dashGroupInstances === null) {
        loadDashGroupInstances();
        return false;
    }
    return true;
}

function renderDashGroupActivityInstances() {
    const el = document.getElementById('dashGroupActivityCards');
    if (!el) return;
    if (!currentVrcUser) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.groups.login', 'Login to see your groups')}</div>`;
        return;
    }
    if (_dashGroupInstances === null) {
        el.innerHTML = sk('world', 3);
        loadDashGroupInstances();
        return;
    }
    if (!_dashGroupInstances.length) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.section.group_activity_empty', 'No active group instances right now')}</div>`;
        return;
    }
    el.innerHTML = _dashGroupInstances.slice(0, 24).map(inst => {
        const thumb    = inst.worldThumb || '';
        const wname    = inst.worldName || t('dashboard.instances.unknown_world', 'Unknown World');
        const gname    = inst.groupName || '';
        const loc      = (inst.location || '').replace(/'/g, "\\'");
        const users    = inst.capacity > 0 ? `${inst.userCount}/${inst.capacity}` : String(inst.userCount || 0);
        const thumbStyle = thumb ? `background-image:url('${cssUrl(thumb)}')` : '';
        const { instanceType } = (typeof parseFriendLocation === 'function') ? parseFriendLocation(inst.location || '') : { instanceType: 'group' };
        const { cls: instCls, label: instLabel } = (typeof getInstanceBadge === 'function') ? getInstanceBadge(instanceType) : { cls: '', label: '' };
        const gaAgeGate = (inst.location || '').includes('~ageGate')
            ? `<span class="vrcn-badge" style="background:rgba(255,75,85,.15);color:var(--err);">${esc(t('worlds.instances.age_gated', 'Age Gated'))}</span>` : '';
        const groupAvatar = inst.groupIcon
            ? `<img class="cc-friend-av" src="${inst.groupIcon}" title="${esc(gname)}" onerror="this.style.display='none'">`
            : `<div class="cc-friend-av" title="${esc(gname)}" style="display:flex;align-items:center;justify-content:center;"><span class="msi" style="font-size:10px;color:var(--tx3)">group</span></div>`;
        return `<div class="vrcn-content-card" onclick="openGroupInstanceDetail('${loc}')">
            <div class="cc-bg" style="${thumbStyle}"></div>
            <div class="cc-scrim"></div>
            ${(instLabel || gaAgeGate) ? `<div class="cc-badges-top">${instLabel ? `<span class="vrcn-badge ${instCls}">${instLabel}</span>` : ''}${gaAgeGate}</div>` : ''}
            <div class="cc-content">
                <div class="cc-name">${esc(wname)}</div>
                <div class="cc-friends-row">${groupAvatar}<span class="cc-extra" style="background:transparent;color:rgba(255,255,255,.85);padding-left:4px;">${esc(gname)}</span></div>
                <div class="cc-bottom-row">
                    <div class="cc-meta"><span class="msi">person</span>${esc(users)}</div>
                </div>
            </div>
        </div>`;
    }).join('');
}

function renderDashGroupActivityInstancesSmall() {
    const el = document.getElementById('dashGroupActivityShelf');
    if (!el) return;
    if (!currentVrcUser) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.groups.login', 'Login to see your groups')}</div>`;
        return;
    }
    if (_dashGroupInstances === null) {
        el.innerHTML = _dashWorldShelfSkeleton();
        loadDashGroupInstances();
        return;
    }
    if (!_dashGroupInstances.length) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.section.group_activity_empty', 'No active group instances right now')}</div>`;
        return;
    }
    el.innerHTML = _dashGroupInstances.slice(0, 24).map(inst => {
        const thumb  = inst.worldThumb || '';
        const wname  = esc(inst.worldName || t('dashboard.instances.unknown_world', 'Unknown World'));
        const gname  = esc(inst.groupName || '');
        const loc    = (inst.location || '').replace(/'/g, "\\'");
        const users  = inst.capacity > 0 ? `${inst.userCount}/${inst.capacity}` : (inst.userCount ? String(inst.userCount) : '');
        const isAgeGated = (inst.location || '').includes('~ageGate');
        const iconHtml = inst.groupIcon
            ? `<img class="dash-flocs-avatar" src="${inst.groupIcon}" onerror="this.style.display='none'">`
            : `<div class="dash-flocs-avatar dash-flocs-avatar-letter"><span class="msi" style="font-size:16px;">group</span></div>`;
        const worldThumb = thumb
            ? `<img class="dash-flocs-world-thumb" src="${cssUrl(thumb)}" alt="" loading="lazy" onerror="this.style.display='none'">`
            : '';
        const ageGateBadge = isAgeGated
            ? `<span class="vrcn-badge" style="font-size:9px;background:rgba(255,75,85,.12);color:var(--err);border:1px solid rgba(255,75,85,.25);padding:1px 5px;flex-shrink:0;">18+</span>`
            : '';
        return `<div class="dash-flocs-card" onclick="openGroupInstanceDetail('${loc}')">
            <div class="dash-flocs-avatar-wrap">
                ${iconHtml}
            </div>
            <div class="dash-flocs-info">
                <div class="dash-flocs-name">${gname}</div>
                <div class="dash-flocs-status">${wname}</div>
                <div class="dash-flocs-world">${users ? `<span class="msi">person</span>${esc(users)}` : ''}${ageGateBadge}</div>
            </div>
            ${worldThumb}
        </div>`;
    }).join('');
}

function openGroupInstanceDetail(location) {
    if (!location) return;
    const { worldId, instanceType, ownerId } = (typeof parseFriendLocation === 'function')
        ? parseFriendLocation(location)
        : { worldId: '', instanceType: 'group', ownerId: '' };
    const cached = (worldId && typeof dashWorldCache !== 'undefined') ? (dashWorldCache[worldId] || {}) : {};
    const inst = (_dashGroupInstances || []).find(x => x.location === location);
    openInstanceDetailFromData({
        worldId,
        location,
        worldName:  cached.name || inst?.worldName || worldId || '',
        worldThumb: cached.thumbnailImageUrl || cached.imageUrl || inst?.worldThumb || '',
        instanceType,
        ownerId:    ownerId || '',
        ownerName:  '',
        ownerGroup: inst?.groupName || '',
    });
}

/* === Dashboard — Recent Activity Timeline === */

function _dashTlSkeleton(n = 5) {
    const row = `<tr style="pointer-events:none;">
        <td><span class="sk-block" style="height:9px;width:80px;border-radius:3px;display:inline-block;"></span></td>
        <td><span class="sk-block" style="height:9px;width:70px;border-radius:3px;display:inline-block;"></span></td>
        <td><span class="sk-block" style="height:9px;width:50px;border-radius:3px;display:inline-block;"></span></td>
        <td><span class="sk-block" style="height:9px;width:100px;border-radius:3px;display:inline-block;"></span></td>
    </tr>`;
    return `<div class="tl-list-wrap"><table class="tl-list-table"><tbody>${Array.from({ length: n }, () => row).join('')}</tbody></table></div>`;
}

function _dashTlRelative(ts) {
    const diff = Date.now() - new Date(ts).getTime();
    const m = Math.floor(diff / 60000);
    if (m < 1)  return t('common.time.just_now', 'just now');
    if (m < 60) return tf('common.time.min_ago',  { m }, '{m}m ago');
    const h = Math.floor(m / 60);
    if (h < 24) return tf('common.time.hour_ago', { h }, '{h}h ago');
    const d = Math.floor(h / 24);
    return tf('common.time.day_ago', { d }, '{d}d ago');
}

function _dashTlDetail(ev, isFriend) {
    if (isFriend) {
        switch (ev.type) {
            case 'friend_gps':        return ev.worldName || ev.worldId || '';
            case 'friend_status':     return ev.newValue || '';
            case 'friend_statusdesc': return ev.newValue || '';
            case 'friend_bio':        return ev.newValue || '';
            case 'friend_online':
            case 'friend_offline':
            case 'friend_added':
            case 'friend_removed':    return '';
            default:                  return '';
        }
    }
    switch (ev.type) {
        case 'instance_join':  return ev.worldName || ev.worldId || '';
        case 'photo':          return ev.worldName || (ev.photoPath ? ev.photoPath.split(/[\\/]/).pop() : '') || '';
        case 'first_meet':
        case 'meet_again':     return ev.userName || '';
        case 'avatar_switch':  return ev.avatarName || '';
        case 'notification':   return ev.notifType || '';
        case 'video_url':      return ev.url || '';
        default:               return '';
    }
}

function _dashTlRows(events, isFriend) {
    if (!events.length) {
        return `<div class="empty-msg" style="padding:16px 8px;">${t('dashboard.timeline.empty', 'No events yet')}</div>`;
    }
    const sliced = events.slice(0, 8);
    if (isFriend) {
        return typeof buildFriendListHtml === 'function'
            ? buildFriendListHtml(sliced)
            : `<div class="empty-msg">${t('dashboard.timeline.empty', 'No events yet')}</div>`;
    }
    return typeof buildPersonalListHtml === 'function'
        ? buildPersonalListHtml(sliced)
        : `<div class="empty-msg">${t('dashboard.timeline.empty', 'No events yet')}</div>`;
}

function renderDashMyRecentTimeline() {
    const el = document.getElementById('dashMyRecentTl');
    if (!el) return;
    if (!currentVrcUser) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.timeline.login', 'Login to see recent activity')}</div>`;
        return;
    }
    const personal = (typeof timelineEvents !== 'undefined') ? timelineEvents : [];
    if (!personal.length && !_dashLayout.hidden.includes('my_recent_activity')) sendToCS({ action: 'vrcGetTimeline', offset: 0 });
    el.innerHTML = personal.length ? _dashTlRows(personal, false) : _dashTlSkeleton();
}

function renderDashFriendsRecentTimeline() {
    const el = document.getElementById('dashFriendsRecentTl');
    if (!el) return;
    if (!currentVrcUser) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.timeline.login', 'Login to see recent activity')}</div>`;
        return;
    }
    const friends = (typeof friendTimelineEvents !== 'undefined') ? friendTimelineEvents : [];
    if (!friends.length && !_dashLayout.hidden.includes('friends_recent_activity')) sendToCS({ action: 'getFriendTimeline', type: '' });
    el.innerHTML = friends.length ? _dashTlRows(friends, true) : _dashTlSkeleton();
}

function renderDashRecentTimeline() {
    renderDashMyRecentTimeline();
    renderDashFriendsRecentTimeline();
}

/* === Dashboard Layout System === */

const DASH_SECTION_META = [
    { id: 'my_instances',            nameKey: 'dashboard.section.my_instances',            name: 'Your Instances' },
    { id: 'vrchat_news',             nameKey: 'dashboard.section.vrchat_news',             name: 'VRChat News' },
    { id: 'upcoming_events',         nameKey: 'dashboard.section.upcoming_events',         name: 'Upcoming Events' },
    { id: 'group_activity_small',    nameKey: 'dashboard.section.group_activity_small',    name: 'Group Activity (Small)' },
    { id: 'friend_locations_small',  nameKey: 'dashboard.section.friend_locations_small',  name: 'Friends Location (Small)' },
    { id: 'recently_visited',        nameKey: 'dashboard.section.recently_visited',        name: 'Recently Visited' },
    { id: 'recent_photos',           nameKey: 'dashboard.section.recent_photos',           name: 'Recent Photos' },
    { id: 'quick_controls',          nameKey: 'dashboard.section.quick_controls',          name: 'Quick Controls' },
    { id: 'friend_locations',        nameKey: 'dashboard.section.friend_locations',        name: 'Friends Locations' },
    { id: 'discovery',               nameKey: 'dashboard.section.discovery',               name: 'Discover Worlds' },
    { id: 'friend_activity',         nameKey: 'dashboard.section.friend_activity',         name: 'Friends Activity' },
    { id: 'fav_worlds',              nameKey: 'dashboard.section.fav_worlds',              name: 'Favorite Worlds' },
    { id: 'fav_avatars',             nameKey: 'dashboard.section.fav_avatars',             name: 'Favorite Avatars' },
    { id: 'own_avatars',             nameKey: 'dashboard.section.own_avatars',             name: 'My Avatars' },
    { id: 'groups',                  nameKey: 'dashboard.section.your_groups',             name: 'Your Groups' },
    { id: 'group_activity',          nameKey: 'dashboard.section.group_activity',          name: 'Group Activity' },
    { id: 'popular_worlds',          nameKey: 'dashboard.section.popular_worlds',          name: 'Popular Worlds' },
    { id: 'active_worlds',           nameKey: 'dashboard.section.active_worlds',           name: 'Very Active Worlds' },
    { id: 'my_recent_activity',      nameKey: 'dashboard.section.my_recent_activity',      name: 'My Recent Activity' },
    { id: 'friends_recent_activity', nameKey: 'dashboard.section.friends_recent_activity', name: 'Friends Recent Activity' },
];
const DASH_DEFAULT_ORDER   = DASH_SECTION_META.map(s => s.id);
const DASH_DEFAULT_VISIBLE = new Set(['my_instances', 'vrchat_news', 'upcoming_events', 'group_activity_small', 'friend_locations_small', 'recently_visited', 'recent_photos']);

let _dashLayout = {
    order:  [...DASH_DEFAULT_ORDER],
    hidden: DASH_DEFAULT_ORDER.filter(id => !DASH_DEFAULT_VISIBLE.has(id)),
};
let _dashModalLayout = null;

function loadDashLayout(data) {
    if (!data) { applyDashLayout(); return; }
    const rawOrder  = data.order  ?? data.dashSectionOrder  ?? data.DashSectionOrder  ?? null;
    const rawHidden = data.hidden ?? data.dashSectionHidden ?? data.DashSectionHidden ?? null;
    if (Array.isArray(rawOrder) && rawOrder.length) {
        const known   = rawOrder.filter(id => DASH_DEFAULT_ORDER.includes(id));
        const missing = DASH_DEFAULT_ORDER.filter(id => !known.includes(id));
        _dashLayout.order = [...known, ...missing];
    }
    if (Array.isArray(rawHidden)) {
        _dashLayout.hidden = rawHidden.filter(id => DASH_DEFAULT_ORDER.includes(id));
    }
    applyDashLayout();
}

function applyDashLayout() {
    const container = document.getElementById('dashSectionsContainer');
    if (!container) return;
    // CSS flex order — no DOM moves, no scroll jumps
    _dashLayout.order.forEach((id, idx) => {
        const wrap = container.querySelector(`.dash-section-wrap[data-section="${id}"]`);
        if (!wrap) return;
        wrap.style.order = idx;
        const hidden = _dashLayout.hidden.includes(id);
        wrap.toggleAttribute('data-hidden', hidden);
        if (!hidden && id === 'my_instances') renderMyInstances(_myInstancesData);
        if (!hidden && id === 'quick_controls') renderDashQuickControls();
        if (!hidden && id === 'fav_worlds') renderDashFavWorlds();
        if (!hidden && id === 'fav_avatars') renderDashFavAvatars();
        if (!hidden && id === 'own_avatars') renderDashOwnAvatars();
        if (!hidden && id === 'recent_photos') renderDashRecentPhotos();
        if (!hidden && id === 'recently_visited') renderDashRecentlyVisited();
        if (!hidden && id === 'popular_worlds') renderDashPopularWorlds();
        if (!hidden && id === 'active_worlds') renderDashActiveWorlds();
        if (!hidden && id === 'discovery') { renderDiscoverySection(); if (!_popularInFlight && (!_popularCache.worlds.length || Date.now() - _popularCache.ts > DISC_CACHE_TTL)) _fetchPopularWorlds(); }
        if (!hidden && id === 'groups') renderDashGroupActivity();
        if (!hidden && id === 'group_activity') renderDashGroupActivityInstances();
        if (!hidden && id === 'group_activity_small') renderDashGroupActivityInstancesSmall();
        if (!hidden && id === 'my_recent_activity') renderDashMyRecentTimeline();
        if (!hidden && id === 'friends_recent_activity') renderDashFriendsRecentTimeline();
        if (!hidden && id === 'upcoming_events') { renderDashUpcomingEvents(); if (_dashUpcomingEvents === null && !_dashUpcomingLoading) refreshDashUpcomingEvents(); }
        if (!hidden && id === 'vrchat_news') { renderDashVrcNews(); if (!_vrcNewsCache.items.length && !_vrcNewsLoading) _fetchVrcNews(); }
    });
}

function openDashLayoutEditor() {
    _dashModalLayout = { order: [..._dashLayout.order], hidden: [..._dashLayout.hidden] };
    _renderDashLayoutList();
    document.getElementById('dashLayoutModal').style.display = 'flex';
}

function closeDashLayoutEditor() {
    document.getElementById('dashLayoutModal').style.display = 'none';
    _dashModalLayout = null;
}

function _renderDashLayoutList() {
    const list = document.getElementById('dashLayoutList');
    if (!list || !_dashModalLayout) return;
    list.innerHTML = _dashModalLayout.order.map((id) => {
        const meta   = DASH_SECTION_META.find(s => s.id === id) || { nameKey: id, name: id };
        const label  = t(meta.nameKey, meta.name);
        const hidden = _dashModalLayout.hidden.includes(id);
        const sid    = jsq(id);
        return `<div class="ne-row${hidden ? ' dli-hidden' : ''}" data-id="${esc(id)}">
            <span class="ne-handle msi">drag_indicator</span>
            <span class="ne-label">${esc(label)}</span>
            <span class="ne-spacer"></span>
            <button class="ne-btn" onclick="dashModalToggle('${sid}')">
                <span class="msi">${hidden ? 'visibility_off' : 'visibility'}</span>
            </button>
        </div>`;
    }).join('');
    _dashInitDrag(list);
}

function _dashInitDrag(list) {
    const ANIM_MS = 200;
    const EASE    = 'cubic-bezier(.2,.7,.3,1)';
    let drag = null;

    function snap() {
        const map = new Map();
        list.querySelectorAll('.ne-row[data-id]').forEach(el => {
            map.set(el, el.getBoundingClientRect().top);
        });
        return map;
    }

    function flip(prev) {
        list.querySelectorAll('.ne-row[data-id]').forEach(el => {
            if (!prev.has(el)) return;
            const dy = prev.get(el) - el.getBoundingClientRect().top;
            if (!dy) return;
            el.animate(
                [{ transform: `translateY(${dy}px)` }, { transform: 'translateY(0)' }],
                { duration: ANIM_MS, easing: EASE }
            );
        });
    }

    function resolveTarget(clientY, dragEl) {
        let best = null;
        list.querySelectorAll('.ne-row[data-id]').forEach(el => {
            if (el === dragEl) return;
            const rect = el.getBoundingClientRect();
            const mid  = rect.top + rect.height / 2;
            if (clientY < mid && !best) best = { target: el, before: true };
            else if (clientY >= mid)    best = { target: el, before: false };
        });
        return best;
    }

    function onDown(e) {
        if (e.button !== 0) return;
        const handle = e.target.closest('.ne-handle');
        if (!handle) return;
        const dragEl = handle.closest('.ne-row[data-id]');
        if (!dragEl) return;
        e.preventDefault();

        const rect = dragEl.getBoundingClientRect();
        const ghost = dragEl.cloneNode(true);
        Object.assign(ghost.style, {
            position:     'fixed',
            top:          rect.top + 'px',
            left:         rect.left + 'px',
            width:        rect.width + 'px',
            pointerEvents:'none',
            zIndex:       '10020',
            opacity:      '0.92',
            boxShadow:    '0 14px 40px rgba(0,0,0,.55)',
            borderRadius: '8px',
            background:   'var(--bg-card)',
            transform:    'scale(1.01)',
        });
        document.body.appendChild(ghost);
        dragEl.classList.add('ne-dragging');

        drag = {
            dragEl, ghost,
            offsetX: e.clientX - rect.left,
            offsetY: e.clientY - rect.top,
            lastKey: null,
        };

        handle.setPointerCapture?.(e.pointerId);
        window.addEventListener('pointermove', onMove);
        window.addEventListener('pointerup',   onUp);
        window.addEventListener('pointercancel', onUp);
        document.body.style.cursor = 'grabbing';
    }

    function onMove(e) {
        if (!drag) return;
        drag.ghost.style.top  = (e.clientY - drag.offsetY) + 'px';
        drag.ghost.style.left = (e.clientX - drag.offsetX) + 'px';

        const drop = resolveTarget(e.clientY, drag.dragEl);
        const key  = drop ? `${drop.before}:${drop.target.dataset.id}` : 'none';
        if (key === drag.lastKey) return;
        drag.lastKey = key;

        const prev = snap();
        if (drop) {
            if (drop.before) list.insertBefore(drag.dragEl, drop.target);
            else             list.insertBefore(drag.dragEl, drop.target.nextSibling);
        }
        flip(prev);
    }

    function onUp() {
        if (!drag) return;
        window.removeEventListener('pointermove', onMove);
        window.removeEventListener('pointerup',   onUp);
        window.removeEventListener('pointercancel', onUp);
        document.body.style.cursor = '';

        const { dragEl, ghost } = drag;
        drag = null;

        const finalRect = dragEl.getBoundingClientRect();
        const ghostRect = ghost.getBoundingClientRect();
        const dx = finalRect.left - ghostRect.left;
        const dy = finalRect.top  - ghostRect.top;

        ghost.animate(
            [
                { transform: 'translate(0,0) scale(1.01)', opacity: 0.92 },
                { transform: `translate(${dx}px,${dy}px) scale(1)`, opacity: 1 },
            ],
            { duration: ANIM_MS, easing: EASE, fill: 'forwards' }
        ).onfinish = () => {
            ghost.remove();
            dragEl.classList.remove('ne-dragging');
            if (_dashModalLayout) {
                _dashModalLayout.order = [...list.querySelectorAll('.ne-row[data-id]')]
                    .map(el => el.dataset.id);
            }
        };
    }

    list.addEventListener('pointerdown', onDown);
}

function dashModalToggle(id) {
    if (!_dashModalLayout) return;
    const i = _dashModalLayout.hidden.indexOf(id);
    if (i === -1) _dashModalLayout.hidden.push(id);
    else _dashModalLayout.hidden.splice(i, 1);
    _renderDashLayoutList();
}

function dashLayoutReset() {
    if (!_dashModalLayout) return;
    _dashModalLayout.order  = [...DASH_DEFAULT_ORDER];
    _dashModalLayout.hidden = DASH_DEFAULT_ORDER.filter(id => !DASH_DEFAULT_VISIBLE.has(id));
    _renderDashLayoutList();
}

function saveDashLayoutFromModal() {
    if (!_dashModalLayout) return;
    _dashLayout = { order: [..._dashModalLayout.order], hidden: [..._dashModalLayout.hidden] };
    closeDashLayoutEditor();
    applyDashLayout();
    saveSettings();
}

// Drag-to-scroll on shelves
(function () {
    let _shelf = null, _startX = 0, _scrollStart = 0, _dragging = false;

    document.addEventListener('mousedown', e => {
        const shelf = e.target.closest('.vrcn-dash-fav-shelf, .dash-photo-grid');
        if (!shelf) return;
        _shelf       = shelf;
        _startX      = e.clientX;
        _scrollStart = shelf.scrollLeft;
        _dragging    = false;
        shelf.style.cursor = 'grabbing';
        e.preventDefault();
    });

    document.addEventListener('mousemove', e => {
        if (!_shelf) return;
        const dx = e.clientX - _startX;
        if (!_dragging && Math.abs(dx) > 4) _dragging = true;
        if (_dragging) _shelf.scrollLeft = _scrollStart - dx;
    });

    document.addEventListener('mouseup', () => {
        if (!_shelf) return;
        _shelf.style.cursor = '';
        if (_dragging) {
            _shelf.addEventListener('click', ev => ev.stopPropagation(), { capture: true, once: true });
        }
        _shelf = null;
        _dragging = false;
    });
})();

/* === Quick Controls Widget === */
const _DASH_QC = [
    { id: 'chatbox', icon: 'chat',          labelKey: 'nav.custom_chatbox',   defaultLabel: 'Chatbox',      isOn: () => typeof chatboxEnabled !== 'undefined' && chatboxEnabled,   toggle: () => toggleChatbox() },
    { id: 'relay',   icon: 'cell_tower',    labelKey: 'nav.media_relay',      defaultLabel: 'Media Relay',  isOn: () => typeof relayOn !== 'undefined' && relayOn,                 toggle: () => toggleRelay() },
    { id: 'vf',      icon: 'mic',           labelKey: 'nav.voice_fight',      defaultLabel: 'Voice Fight',  isOn: () => typeof vfRunning !== 'undefined' && vfRunning,             toggle: () => { if (!vfRunning) vfOnTabOpen(); vfConnect(); } },
    { id: 'dp',      icon: 'sensors',       labelKey: 'nav.discord_presence', defaultLabel: 'Discord',      isOn: () => typeof _dpRunning !== 'undefined' && _dpRunning,           toggle: () => dpToggle() },
    { id: 'yt',      icon: 'smart_display', labelKey: 'nav.youtube_fix',      defaultLabel: 'YouTube Fix',  isOn: () => typeof _vcLastState !== 'undefined' && !!_vcLastState?.running, toggle: () => toggleVc() },
    { id: 'sf',      icon: 'rocket_launch', labelKey: 'nav.space_flight',     defaultLabel: 'Space Flight', isOn: () => typeof sfConnected !== 'undefined' && sfConnected,         toggle: () => sfConnect() },
    { id: 'fs',      icon: 'photo_camera',  labelKey: 'nav.frame_shot',       defaultLabel: 'FrameShot',    isOn: () => typeof fsConnected !== 'undefined' && fsConnected,         toggle: () => fsConnect() },
];

function dashQcToggle(id) {
    const f = _DASH_QC.find(x => x.id === id);
    if (f) f.toggle();
}

function renderDashQuickControls() {
    const grid = document.getElementById('dashQcGrid');
    if (!grid) return;
    grid.innerHTML = _DASH_QC.map(f => {
        const on  = f.isOn();
        const lbl = t(f.labelKey, f.defaultLabel);
        const sid = jsq(f.id);
        return `<button class="dash-qc-card${on ? ' dqc-on' : ''}" data-qc="${esc(f.id)}" onclick="dashQcToggle('${sid}')">
            <span class="msi dash-qc-icon">${esc(f.icon)}</span>
            <span class="dash-qc-label">${esc(lbl)}</span>
        </button>`;
    }).join('');
}

function updateDashQuickControls() {
    const grid = document.getElementById('dashQcGrid');
    if (!grid) return;
    _DASH_QC.forEach(f => {
        const card = grid.querySelector(`.dash-qc-card[data-qc="${CSS.escape(f.id)}"]`);
        if (card) card.classList.toggle('dqc-on', f.isOn());
    });
}

function rerenderDashTranslations() {
    renderDashWorlds();
    renderDashFriendsFeed();
    renderDashFriendsLocationSmall();
    renderDashFavWorlds();
    renderDashFavAvatars();
    renderDashOwnAvatars();
    renderDashRecentPhotos();
    renderDashRecentlyVisited();
    renderDashPopularWorlds();
    renderDashActiveWorlds();
    renderDashGroupActivity();
    renderDashGroupActivityInstances();
    renderDashGroupActivityInstancesSmall();
    renderDashMyRecentTimeline();
    renderDashFriendsRecentTimeline();
    renderDashQuickControls();
    renderDashUpcomingEvents();
    if (_dashModalLayout) _renderDashLayoutList();
}
document.documentElement.addEventListener('languagechange', rerenderDashTranslations);

/* === Dashboard Overlay — Glass Vignette === */
(function () {
    const THEME_ID = 'Dashboard Theme';
    const content = document.querySelector('.content');
    const tab0 = document.getElementById('tab0');
    const FADE_PX = 140;

    const vignette = document.createElement('div');
    vignette.id = 'dash-vignette';
    Object.assign(vignette.style, {
        position: 'fixed',
        inset: '0',
        pointerEvents: 'none',
        background: [
            'linear-gradient(to right,  rgba(0,0,0,0.80), transparent 280px)',
            'linear-gradient(to left,   rgba(0,0,0,0.80), transparent 280px)',
            'linear-gradient(to bottom, rgba(0,0,0,0.75), transparent 200px)',
        ].join(','),
    });
    document.body.appendChild(vignette);

    let _wasDash = false;
    let _fadeAnim = null;

    function applyGlass() {
        const onDash = tab0 && tab0.classList.contains('active');
        const t = onDash ? Math.min((content?.scrollTop || 0) / FADE_PX, 1) : 1;
        const target = 1 - t;
        document.body.style.setProperty('--sidebar-glass-t', t.toFixed(3));
        if (_fadeAnim) { _fadeAnim.cancel(); _fadeAnim = null; }
        if (onDash && !_wasDash) {
            vignette.style.opacity = '0';
            _fadeAnim = vignette.animate([{ opacity: 0 }, { opacity: target }], { duration: 800, easing: 'ease-in' });
            _fadeAnim.onfinish = () => { _fadeAnim = null; applyGlass(); };
        } else {
            vignette.style.opacity = target.toFixed(3);
        }
        _wasDash = onDash;
    }

    function cleanup() {
        content?.removeEventListener('scroll', applyGlass);
        document.documentElement.removeEventListener('themechange', applyGlass);
        document.documentElement.removeEventListener('tabchange', applyGlass);
        document.documentElement.removeEventListener('vrcnext:theme:unload:' + THEME_ID, cleanup);
        document.body.style.removeProperty('--sidebar-glass-t');
        vignette.remove();
    }

    applyGlass();
    content?.addEventListener('scroll', applyGlass, { passive: true });
    document.documentElement.addEventListener('themechange', applyGlass);
    document.documentElement.addEventListener('tabchange', applyGlass);
    document.documentElement.addEventListener('vrcnext:theme:unload:' + THEME_ID, cleanup);
}());

/* === Upcoming Events Widget === */

let _dashUpcomingEvents = null;
let _dashUpcomingLoading = false;

function refreshDashUpcomingEvents() {
    if (_dashUpcomingLoading || !currentVrcUser) return;
    _dashUpcomingLoading = true;
    _calDashRawEvents = [];
    _calDashPending = 2;
    renderDashUpcomingEvents();
    const now = new Date();
    const nxt = new Date(now.getFullYear(), now.getMonth() + 1, 1);
    sendToCS({ action: 'vrcGetCalendarEvents', filter: 'all', year: now.getFullYear(), month: now.getMonth() + 1 });
    sendToCS({ action: 'vrcGetCalendarEvents', filter: 'all', year: nxt.getFullYear(), month: nxt.getMonth() + 1 });
}

function refreshDashUpcomingEventsManual() {
    const btn = document.getElementById('dashUpcomingEventsRefreshBtn');
    if (btn) btn.classList.add('spinning');
    refreshDashUpcomingEvents();
}

function onCalendarEventsForDash(allEvents) {
    _dashUpcomingLoading = false;
    const _btn = document.getElementById('dashUpcomingEventsRefreshBtn');
    if (_btn) _btn.classList.remove('spinning');
    const now = new Date();
    const seen = new Set();
    _dashUpcomingEvents = (Array.isArray(allEvents) ? allEvents : [])
        .filter(e => { if (seen.has(e.id)) return false; seen.add(e.id); return true; })
        .filter(e => new Date(e.startsAt || e.startDate || 0) >= now)
        .sort((a, b) => new Date(a.startsAt || a.startDate || 0) - new Date(b.startsAt || b.startDate || 0));
    renderDashUpcomingEvents();
}

function renderDashUpcomingEvents() {
    const grid = document.getElementById('dashUpcomingEventsGrid');
    if (!grid) return;

    const emptyState = (icon, msg, btn = '') =>
        `<div style="display:flex;flex-direction:column;align-items:center;justify-content:center;gap:10px;padding:28px 0;color:var(--tx3);font-size:12px;">
            <span class="msi" style="font-size:26px;">${icon}</span>${esc(msg)}${btn}
        </div>`;

    if (_dashUpcomingLoading) {
        grid.innerHTML = emptyState('event', t('dashboard.upcoming.loading', 'Loading events...'));
        return;
    }
    if (_dashUpcomingEvents === null) {
        grid.innerHTML = emptyState('event_upcoming', '',
            `<button class="vrcn-button" onclick="refreshDashUpcomingEvents()">${esc(t('dashboard.upcoming.load', 'Load Events'))}</button>`);
        return;
    }
    if (_dashUpcomingEvents.length === 0) {
        grid.innerHTML = emptyState('event_busy', t('dashboard.upcoming.empty', 'No upcoming events found'));
        return;
    }

    const myGroupsList = (typeof myGroups !== 'undefined') ? myGroups : [];

    const cards = _dashUpcomingEvents.slice(0, 4).map(evt => {
        const groupId = jsq(evt.ownerId || evt.groupId || '');
        const eventId = jsq(evt.id || '');
        const title   = esc(evt.title || evt.name || t('calendar.untitled_event', 'Untitled Event'));
        const featured = evt.featured === true || (Array.isArray(evt.tags) && evt.tags.some(tag => /featured/i.test(tag)));

        const startDate = new Date(evt.startsAt || evt.startDateTime || evt.startDate || '');
        const endDate   = new Date(evt.endsAt || '');
        const hasStart  = !isNaN(startDate.getTime());
        const hasEnd    = !isNaN(endDate.getTime());
        const dateStr   = hasStart ? startDate.toLocaleDateString(getLanguageLocale(), { day: 'numeric', month: 'short', year: 'numeric' }) : '';
        const timeStr   = hasStart ? fmtTime(startDate) + (hasEnd ? ' – ' + fmtTime(endDate) : '') : '';
        const whenStr   = [dateStr, timeStr].filter(Boolean).join(' · ');

        const imgSrc = evt.imageUrl || '';
        const imgHtml = imgSrc
            ? `<img class="dash-evt-img" src="${imgSrc}" alt="" loading="lazy" onerror="this.style.display='none'">`
            : `<div class="dash-evt-img dash-evt-img-placeholder"><span class="msi">event</span></div>`;

        const desc = esc(evt.description || '');
        const featuredBadge = featured
            ? `<span class="dash-evt-featured"><span class="msi">star</span>${esc(t('dashboard.upcoming.featured', 'Featured'))}</span>`
            : '';

        const gid = evt.ownerId || evt.groupId || '';
        const groupData = myGroupsList.find(g => g.id === gid) || {};
        const groupName = evt.group?.name || groupData.name || '';
        const groupMeta = groupName
            ? `<div class="dash-evt-group"><span class="msi">group</span>${esc(groupName)}</div>`
            : '';

        return `<div class="dash-evt-card" onclick="openEventDetail('${groupId}','${eventId}')">
            ${imgHtml}
            <div class="dash-evt-body">
                <div class="dash-evt-title">${title}${featuredBadge}</div>
                ${whenStr ? `<div class="dash-evt-when"><span class="msi">calendar_today</span>${esc(whenStr)}</div>` : ''}
                ${desc ? `<div class="dash-evt-desc">${desc}</div>` : ''}
                ${groupMeta}
            </div>
        </div>`;
    }).join('');

    grid.innerHTML = `<div class="dash-evt-grid">${cards}</div>`;
}

