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

function updateDashSub() {
    const el = document.getElementById('dashSub');
    if (!el) return;
    if (!currentVrcUser?.displayName) {
        el.textContent = t('dashboard.sub.connect_world', 'Connect to VRChat to see your world');
        return;
    }
    const status = currentVrcUser.statusDescription || statusLabel(currentVrcUser.status);
    el.innerHTML = `<div class="myp-status-row" onclick="openStatusModal()">
        <span class="${currentVrcUser.vrcRunning ? 'vrc-status-dot' : 'vrc-status-ring'} ${statusDotClass(currentVrcUser.status)}" style="width:9px;height:9px;flex-shrink:0;"></span>
        <span>${esc(status)}</span>
        <span class="msi" style="font-size:15px;opacity:.45;">edit</span>
    </div>`;
}

function _dashVideoSync() {
    const winVisible = !document.hidden;
    const heroVid = document.getElementById('dashHeroBg')?.querySelector('video');
    if (heroVid) {
        const show = winVisible && document.getElementById('tab0')?.classList.contains('active');
        if (show && heroVid.paused) heroVid.play().catch(() => {});
        else if (!show && !heroVid.paused) heroVid.pause();
    }
    const prevVid = document.getElementById('dashBgPreviewHero')?.querySelector('video');
    if (prevVid) {
        const show = winVisible && document.getElementById('tab9')?.classList.contains('active');
        if (show && prevVid.paused) prevVid.play().catch(() => {});
        else if (!show && !prevVid.paused) prevVid.pause();
    }
}
document.documentElement.addEventListener('tabchange', _dashVideoSync);
document.addEventListener('visibilitychange', _dashVideoSync);

function renderDashBgPreview() {
    const hero = document.getElementById('dashBgPreviewHero');
    if (!hero) return;
    const isVideo = !!dashBgPath && dashBgPath.toLowerCase().endsWith('.mp4');
    const src = dashBgDataUri || (dashBgPath ? 'file:///' + dashBgPath.replace(/\\/g, '/') : '');
    const existingVid = hero.querySelector('video');
    if (isVideo && src) {
        if (existingVid && existingVid.getAttribute('data-src') === src) return;
        if (existingVid) existingVid.remove();
        hero.style.backgroundImage = '';
        const vid = document.createElement('video');
        vid.setAttribute('data-src', src);
        vid.src = src;
        vid.autoplay = true;
        vid.loop = true;
        vid.muted = true;
        vid.playsInline = true;
        hero.insertBefore(vid, hero.firstChild);
    } else {
        if (existingVid) existingVid.remove();
        hero.style.backgroundImage = `url('${src || 'fallback_bg.png'}')`;
    }
    _dashVideoSync();
}

function applyDashHeroBg() {
    const bgEl = document.getElementById('dashHeroBg');
    if (!bgEl) return;
    const isVideoBg = dashBgPath && dashBgPath.toLowerCase().endsWith('.mp4');
    if (isVideoBg) {
        const src = dashBgDataUri || ('file:///' + dashBgPath.replace(/\\/g, '/'));
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
            const fileUri = 'file:///' + dashBgPath.replace(/\\/g, '/');
            bgEl.style.backgroundImage = `url('${fileUri}')`;
        } else {
            bgEl.style.backgroundImage = `url('fallback_bg.png')`;
        }
    }
    _dashVideoSync();
}

const DASH_GREETING_COUNT = 37;
let _dashGreetingIdx = 1 + Math.floor(Math.random() * DASH_GREETING_COUNT);
function _dashRollGreeting() { _dashGreetingIdx = 1 + Math.floor(Math.random() * DASH_GREETING_COUNT); }

function renderDashboard() {
    const _tab0 = document.getElementById('tab0');
    if (_tab0 && !_tab0.classList.contains('active')) return;
    const name = currentVrcUser?.displayName;
    document.getElementById('dashWelcome').innerHTML = name
        ? tf(`dashboard.welcome.greeting.${_dashGreetingIdx}`, { name: `<span style="color:var(--accent)">${esc(name)}</span>` }, 'Welcome back, {name}!')
        : esc(t('dashboard.welcome.default', 'Welcome!'));
    updateDashSub();
    updateDashHeroStats();

    applyDashHeroBg();
    if (currentSpecialTheme === 'auto') applyAutoColor();
    renderDashBgPreview();

    renderDashHeroWidgets();
    renderDashFriendsLocationSmall();
    renderDashFavWorlds();
    renderDashFavAvatars();
    renderDashOwnAvatars();
    renderDashRecentPhotos();
    renderDashRecentlyVisited();
    renderDashPopularWorlds();
    renderDashActiveWorlds();
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

function renderDashboardFriendSections() {
    const _tab0 = document.getElementById('tab0');
    if (_tab0 && !_tab0.classList.contains('active')) return;
    renderDashFriendsLocationSmall();
    renderDashGroupActivityInstancesSmall();
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

function _dashFlocsCardHtml(f) {
        const { worldId } = parseFriendLocation(f.location);
        const cached   = worldId ? dashWorldCache[worldId] : null;
        const thumb    = cached?.thumbnailImageUrl || cached?.imageUrl || '';
        const wname    = esc(cached?.name || (worldId
        ? t('dashboard.friends.location_world', 'In World')
        : t('dashboard.friends.location_private', 'Private Instance')));
        const wid      = (worldId || '').replace(/'/g, "\\'");
        const safeLoc  = (f.location || '').replace(/'/g, "\\'");
        const img      = f.image || '';
        const dotClass = f.presence === 'web' ? 'vrc-status-ring' : 'vrc-status-dot';
        const avatarEl = img
            ? `<img class="dash-flocs-avatar" src="${imgThumb(img, 96)}" loading="lazy" decoding="async" onerror="this.style.display='none'">`
            : `<div class="dash-flocs-avatar dash-flocs-avatar-letter">${esc((f.displayName||'?')[0])}</div>`;
        const worldThumb = thumb
            ? `<img class="dash-flocs-world-thumb" src="${cssUrl(imgThumb(thumb, 96))}" alt="" loading="lazy" onerror="this.style.display='none'">`
            : '';
        const fid = (f.id || '').replace(/'/g, "\'");
    const click = worldId ? `openFriendLocationDetail('${wid}','${safeLoc}')` : `openFriendDetail('${fid}')`;
    return `<div class="dash-flocs-card" data-uid="${esc(f.id || '')}" onclick="${click}">
            ${(typeof nameplateDecoHtml === 'function') ? nameplateDecoHtml(f.nameplateUrl) : ''}
            <div class="dash-flocs-avatar-wrap">
                ${avatarEl}
                ${(typeof iconFrameHtml === 'function') ? iconFrameHtml(f.iconFrameUrl) : ''}
                <span class="${dotClass} ${statusDotClass(f.status)} dash-flocs-dot"></span>
            </div>
            <div class="dash-flocs-info">
                <div class="dash-flocs-name">${esc(f.displayName)}</div>
                <div class="dash-flocs-status">${esc(f.statusDescription || statusLabel(f.status))}</div>
                <div class="dash-flocs-world"><span class="msi">public</span>${wname}</div>
            </div>
            ${worldThumb}
        </div>`;
}

function renderDashFriendsLocationSmall() {
    const el = document.getElementById('dashFriendLocSmallShelf');
    if (!el) return;
    if (!currentVrcUser || !vrcFriendsLoaded) {
        setHtmlIfChanged(el, _dashWorldShelfSkeleton());
        return;
    }
    const inWorld = vrcFriendsData.filter(f => {
        const { worldId } = parseFriendLocation(f.location);
        return worldId && worldId.startsWith('wrld_');
    });
    if (!inWorld.length) {
        setHtmlIfChanged(el, `<div class="empty-msg">${t('dashboard.section.friend_locations_small_empty', 'No friends in worlds right now')}</div>`);
        return;
    }
    setHtmlIfChanged(el, inWorld.slice(0, 24).map(_dashFlocsCardHtml).join(''));
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
        const thumbStyle = inst.worldThumb ? `background-image:url('${cssUrl(imgThumb(inst.worldThumb, 256))}')` : '';
        const wid = (inst.worldId || '').replace(/'/g, "\\'");
        const count = inst.userCount || 0;
        const cap   = inst.capacity  || 0;
        const countStr = cap > 0
            ? tf('dashboard.instances.players_with_capacity', { count, capacity: cap }, '{count}/{capacity} players')
            : tf('dashboard.instances.players', { count }, '{count} players');
        const safeLoc = (inst.location || '').replace(/'/g, "\\'");
        const miAgeGate = (inst.location || '').includes('~ageGate')
            ? `<span class="vrcn-badge" style="background:rgba(255,75,85,.15);color:var(--err);">${esc(t('worlds.instances.age_gated', 'Age Gated'))}</span>` : '';
        const miRegion = ((inst.location || '').match(/~region\(([^)]+)\)/) || [])[1] || '';
        const miRegionBadge = miRegion ? `<span class="vrcn-badge cc-glass-badge">${esc(miRegion.toUpperCase())}</span>` : '';
        const miPct = cap > 0 ? Math.max(0, Math.min(100, count / cap * 100)) : 0;
        const miBar = cap > 0 ? `<div class="cc-capbar"><div class="cc-capbar-fill" style="width:${miPct.toFixed(0)}%"></div></div>` : '';
        return `<div class="vrcn-content-card" onclick="openMyInstanceDetail('${wid}','${safeLoc}')" data-location="${esc(inst.location || '')}">
            <div class="cc-bg" style="${thumbStyle}"></div>
            <div class="cc-scrim"></div>
            <div class="cc-badges-top"><span class="vrcn-badge ${cls}">${esc(typeLabel)}</span>${miRegionBadge}${miAgeGate}</div>
            <div class="cc-content">
                <div class="cc-name">${esc(inst.worldName || inst.worldId || t('dashboard.instances.unknown_world', 'Unknown World'))}</div>
                <div class="cc-bottom-row">
                    <div class="cc-meta"><span class="msi">person</span>${esc(countStr)}</div>
                </div>
                ${miBar}
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
        return;
    }
    _vrcNewsLoading = true;
    sendToCS({ action: 'vrcGetNews' });
}

function onVrcNews(items) {
    _vrcNewsLoading = false;
    _vrcNewsCache   = { items: items || [], ts: Date.now() };
    renderDashHeroWidgets();
}

function _fmtNewsDate(str) {
    if (!str) return '';
    try {
        const d = new Date(str);
        if (isNaN(d.getTime())) return '';
        return d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
    } catch { return ''; }
}


let _newsArticleLink = '';

function openNewsArticle(id, link, title) {
    _newsArticleLink = link || '';
    const modal = document.getElementById('modalNewsArticle');
    if (!modal) {
        if (link) sendToCS({ action: 'openUrl', url: link });
        return;
    }
    const titleEl = document.getElementById('newsArticleTitle');
    const bodyEl = document.getElementById('newsArticleBody');
    if (titleEl) titleEl.textContent = title || '';
    if (bodyEl) bodyEl.innerHTML = `<div class="empty-msg">${t('dashboard.news.loading_article', 'Loading article...')}</div>`;
    modal.style.display = 'flex';
    if (id) sendToCS({ action: 'vrcGetNewsArticle', id });
    else if (bodyEl) bodyEl.innerHTML = `<div class="empty-msg">${t('dashboard.news.error', 'Could not load article')}</div>`;
}

function _newsAbsUrl(u) {
    if (!u) return u;
    if (u.startsWith('//')) return 'https:' + u;
    if (u.startsWith('/')) return 'https://ask.vrchat.com' + u;
    return u;
}

function _newsYtId(url) {
    const m = String(url || '').match(/(?:youtube\.com\/(?:watch\?v=|embed\/|shorts\/)|youtu\.be\/)([\w-]{11})/);
    return m ? m[1] : '';
}

function renderNewsArticle(payload) {
    const bodyEl = document.getElementById('newsArticleBody');
    if (!bodyEl) return;
    if (payload.link) _newsArticleLink = payload.link;
    if (payload.error || !payload.html) {
        bodyEl.innerHTML = `<div class="empty-msg">${t('dashboard.news.error', 'Could not load article')}</div>`;
        return;
    }
    const port = payload.port || (typeof _localHttpPort !== 'undefined' ? _localHttpPort : 0);

    const safe = document.createElement('div');
    safe.innerHTML = payload.html;

    safe.querySelectorAll('script,style,iframe,link,meta').forEach(n => n.remove());

    safe.querySelectorAll('.lazy-video-container,[data-youtube-id],[data-video-id],.youtube-onebox,.lazyYT').forEach(el => {
        let vid = el.getAttribute('data-youtube-id') || el.getAttribute('data-video-id') || '';
        if (!vid) {
            const link = el.querySelector('a[href]');
            if (link) vid = _newsYtId(link.getAttribute('href'));
        }
        if (!vid) return;
        if (port) {
            const wrap = document.createElement('div');
            wrap.className = 'news-video';
            const iframe = document.createElement('iframe');
            iframe.src = `http://localhost:${port}/ytembed?v=${encodeURIComponent(vid)}`;
            iframe.setAttribute('frameborder', '0');
            iframe.setAttribute('allowfullscreen', '');
            iframe.setAttribute('allow', 'accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture; fullscreen');
            wrap.appendChild(iframe);
            el.replaceWith(wrap);
        } else {
            const thumb = document.createElement('div');
            thumb.className = 'news-video news-video-thumb';
            thumb.setAttribute('data-href', 'https://www.youtube.com/watch?v=' + vid);
            thumb.style.backgroundImage = `url('https://i.ytimg.com/vi/${vid}/hqdefault.jpg')`;
            thumb.innerHTML = '<span class="news-video-play msi">play_arrow</span>';
            el.replaceWith(thumb);
        }
    });

    safe.querySelectorAll('*').forEach(node => {
        [...node.attributes].forEach(attr => { if (/^on/i.test(attr.name)) node.removeAttribute(attr.name); });
    });
    safe.querySelectorAll('img').forEach(img => {
        const s = img.getAttribute('src') || img.getAttribute('data-src') || '';
        if (s) img.setAttribute('src', _newsAbsUrl(s));
        img.removeAttribute('srcset');
        img.setAttribute('loading', 'lazy');
    });
    safe.querySelectorAll('a').forEach(a => {
        const h = _newsAbsUrl(a.getAttribute('href') || '');
        a.removeAttribute('href');
        a.removeAttribute('target');
        if (h) a.setAttribute('data-href', h);
    });

    bodyEl.innerHTML = '';
    bodyEl.appendChild(safe);
    bodyEl.scrollTop = 0;
}

function newsArticleLinkClick(e) {
    const a = e.target.closest('[data-href]');
    if (a) { e.preventDefault(); sendToCS({ action: 'openUrl', url: a.getAttribute('data-href') }); }
}


/* === Discovery Section === */

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
    if ((!_dashLayout.hidden.includes('upcoming_events') || _dashLayout.hero.right === 'next_event') && !_dashUpcomingLoading) refreshDashUpcomingEvents();
}, 120 * 60 * 1000);











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
    const photos = files.filter(f => f.type === 'image' && f.url).slice(0, 20);
    if (!photos.length) {
        if (!_dashPhotosRequested) {
            if (_dashLayout.hidden.includes('recent_photos')) return;
            _dashPhotosRequested = true;
            el.innerHTML = _dashWorldShelfSkeleton();
            sendToCS({ action: 'scanLibrary' });
        }
        return;
    }
    const renderPhoto = (f, extraCls) => {
        const thumbUrl  = f.url ? f.url + '?thumb=1' : '';
        const dateMatch = (f.name || '').match(/(\d{4}-\d{2}-\d{2})/);
        const dateStr   = dateMatch ? fmtShortDate(new Date(dateMatch[1] + 'T00:00:00')) : (f.time || '');
        const isHidden  = (typeof hiddenMedia !== 'undefined') && hiddenMedia.has(f.path);
        const pathJs    = jsq(f.path || '');
        return `<div class="dash-photo-item${extraCls ? ' ' + extraCls : ''}${isHidden ? ' dpi-hidden' : ''}" onclick="openPhotoDetail('${pathJs}')" title="${esc(f.name || '')}" data-path="${esc(f.path || '')}" data-url="${esc(f.url || '')}" data-type="image" data-name="${esc(f.name || '')}">
            <div class="dpi-img"${thumbUrl ? ` style="background-image:url('${cssUrl(imgThumb(thumbUrl, 256))}')"` : ''}></div>
            <div class="dpi-date">${esc(dateStr)}</div>
        </div>`;
    };
    el.innerHTML = photos.map(f => renderPhoto(f)).join('');
}

/* === Dashboard — Avatar card (shared by fav + own shelves) === */

function _dashAvatarCard(a) {
    const thumb     = a.thumbnailImageUrl || a.imageUrl || '';
    const aid       = jsq(a.id || '');
    const isActive  = a.id === currentAvatarId;
    const activeBadge = (typeof avatarCurrentBadge === 'function') ? avatarCurrentBadge(isActive) : '';
    return `<div class="dash-av-tile${isActive ? ' dav-active' : ''}" onclick="selectAvatar('${aid}')">
        <div class="dav-img"${thumb ? ` style="background-image:url('${cssUrl(imgThumb(thumb, 256))}')"` : ''}>${activeBadge ? `<div class="dav-badge">${activeBadge}</div>` : ''}</div>
        <div class="dav-name">${esc(a.name || t('avatars.labels.unnamed', 'Unnamed'))}</div>
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
        <div class="cc-bg"${thumb ? ` style="background-image:url('${cssUrl(imgThumb(thumb, 256))}')"` : ''}></div>
        <div class="cc-scrim"></div>
        <div class="cc-content"><div class="cc-name">${esc(w.name || w.id || '?')}</div>${meta}</div>
    </div>`;
}

function _dashWorldShelfSkeleton() {
    return Array.from({ length: 8 }, () => `<div class="vrcn-content-card sk-block" style="pointer-events:none;"></div>`).join('');
}

const WORLDS_CACHE_TTL = 10 * 60 * 1000;
let _popularCache = { worlds: [], ts: 0 };
let _activeCache  = { worlds: [], ts: 0 };
let _recentCache  = { worlds: [], ts: 0 };
let _recentInFlight  = false;
let _popularInFlight = false;
let _activeInFlight  = false;

const _worldsRefreshInterval = setInterval(() => {
    const tab0 = document.getElementById('tab0');
    if (!tab0 || !tab0.classList.contains('active')) return;
    if (!_dashLayout.hidden.includes('popular_worlds')) sendToCS({ action: 'vrcGetPopularWorlds' });
    if (!_dashLayout.hidden.includes('active_worlds')) sendToCS({ action: 'vrcGetActiveWorlds' });
}, WORLDS_CACHE_TTL);

function _dashRankRow(w, idx) {
    const thumb = w.thumbnailImageUrl || w.imageUrl || '';
    const wid   = jsq(w.id || '');
    const occupants = w.occupants ?? w.publicOccupants ?? 0;
    return `<div class="dash-rank-row" onclick="openWorldSearchDetail('${wid}')">
        <span class="drr-rank${idx < 3 ? ' drr-top' : ''}">${idx + 1}</span>
        <div class="drr-thumb"${thumb ? ` style="background-image:url('${cssUrl(imgThumb(thumb, 128))}')"` : ''}></div>
        <div class="drr-info">
            <div class="drr-name">${esc(w.name || w.id || '?')}</div>
            ${occupants > 0 ? `<div class="drr-meta"><span class="msi">person</span>${occupants.toLocaleString()}</div>` : ''}
        </div>
    </div>`;
}

function _dashRankSkeleton() {
    const row = `<div class="dash-rank-row" style="pointer-events:none;">
        <span class="drr-rank">·</span>
        <div class="drr-thumb sk-block"></div>
        <div class="drr-info"><div class="sk-block" style="height:11px;width:70%;border-radius:4px;"></div></div>
    </div>`;
    return Array.from({ length: 10 }, () => row).join('');
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
    if (!worlds.length) { el.innerHTML = _dashWorldShelfSkeleton(); if (!_recentInFlight && !_dashLayout.hidden.includes('recently_visited')) { _recentInFlight = true; sendToCS({ action: 'vrcGetRecentWorlds' }); } return; }
    el.innerHTML = worlds.slice(0, 20).map(w => _dashWorldCard(w, true)).join('');
}

function renderDashPopularWorlds() {
    const el = document.getElementById('dashPopularWorldsShelf');
    if (!el) return;
    if (!currentVrcUser) { el.innerHTML = `<div class="empty-msg">${t('dashboard.worlds.login','Login to see worlds')}</div>`; return; }
    const worlds = _popularCache.worlds;
    if (!worlds.length) { el.innerHTML = _dashRankSkeleton(); if (!_popularInFlight && !_dashLayout.hidden.includes('popular_worlds')) { _popularInFlight = true; sendToCS({ action: 'vrcGetPopularWorlds' }); } return; }
    el.innerHTML = worlds.slice(0, 10).map(_dashRankRow).join('');
}

function renderDashActiveWorlds() {
    const el = document.getElementById('dashActiveWorldsShelf');
    if (!el) return;
    if (!currentVrcUser) { el.innerHTML = `<div class="empty-msg">${t('dashboard.worlds.login','Login to see worlds')}</div>`; return; }
    const worlds = _activeCache.worlds;
    if (!worlds.length) { el.innerHTML = _dashRankSkeleton(); if (!_activeInFlight && !_dashLayout.hidden.includes('active_worlds')) { _activeInFlight = true; sendToCS({ action: 'vrcGetActiveWorlds' }); } return; }
    el.innerHTML = worlds.slice(0, 10).map(_dashRankRow).join('');
}

function onRecentWorlds(worlds) {
    _recentInFlight = false;
    _recentCache = { worlds: worlds || [], ts: Date.now() };
    renderDashRecentlyVisited();
}

function onPopularWorlds(worlds) {
    _popularInFlight = false;
    _popularCache = { worlds: worlds || [], ts: Date.now() };
    renderDashPopularWorlds();
}

function onActiveWorlds(worlds) {
    _activeInFlight = false;
    _activeCache = { worlds: worlds || [], ts: Date.now() };
    renderDashActiveWorlds();
}





/* === Dashboard — Group Activity grid === */



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
    const gBtn = document.getElementById('groupsRefreshBtn');
    if (gBtn && gBtn.disabled) { gBtn.disabled = false; gBtn.querySelector('.msi').textContent = 'refresh'; }
    renderDashGroupActivityInstancesSmall();
    renderDashHeroWidgets();
    if (typeof renderGroupInstancesView === 'function') {
        if (typeof lvKeepScroll === 'function') lvKeepScroll(document.getElementById('groupInstancesGrid'), () => renderGroupInstancesView());
        else renderGroupInstancesView();
    }
}

function renderDashGroupActivityInstancesSmall() {
    const el = document.getElementById('dashGroupActivityShelf');
    if (!el) return;
    if (!currentVrcUser) {
        setHtmlIfChanged(el, `<div class="empty-msg">${t('dashboard.groups.login', 'Login to see your groups')}</div>`);
        return;
    }
    if (_dashGroupInstances === null) {
        setHtmlIfChanged(el, _dashWorldShelfSkeleton());
        loadDashGroupInstances();
        return;
    }
    if (!_dashGroupInstances.length) {
        setHtmlIfChanged(el, `<div class="empty-msg">${t('dashboard.section.group_activity_empty', 'No active group instances right now')}</div>`);
        return;
    }
    setHtmlIfChanged(el, _dashGroupInstances.slice(0, 24).map(inst => {
        const thumb  = inst.worldThumb || '';
        const wname  = esc(inst.worldName || t('dashboard.instances.unknown_world', 'Unknown World'));
        const gname  = esc(inst.groupName || '');
        const loc    = (inst.location || '').replace(/'/g, "\\'");
        const users  = inst.capacity > 0 ? `${inst.userCount}/${inst.capacity}` : (inst.userCount ? String(inst.userCount) : '');
        const isAgeGated = (inst.location || '').includes('~ageGate');
        const iconHtml = inst.groupIcon
            ? `<img class="dash-flocs-avatar" src="${imgThumb(inst.groupIcon, 96)}" onerror="this.style.display='none'">`
            : `<div class="dash-flocs-avatar dash-flocs-avatar-letter"><span class="msi" style="font-size:16px;">group</span></div>`;
        const worldThumb = thumb
            ? `<img class="dash-flocs-world-thumb" src="${cssUrl(imgThumb(thumb, 96))}" alt="" loading="lazy" onerror="this.style.display='none'">`
            : '';
        const ageGateBadge = isAgeGated
            ? `<span class="vrcn-badge" style="font-size:calc(9px + var(--fs-off, 0px));background:rgba(255,75,85,.12);color:var(--err);border:1px solid rgba(255,75,85,.25);padding:1px 5px;flex-shrink:0;">18+</span>`
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
    }).join(''));
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

function _dashTlSkeleton(n = 6) {
    const row = `<div class="dash-act-row" style="pointer-events:none;">
        <span class="sk-block" style="height:14px;width:14px;border-radius:4px;"></span>
        <span class="sk-block" style="height:9px;width:110px;border-radius:3px;"></span>
        <span class="sk-block" style="height:24px;width:24px;border-radius:7px;"></span>
        <span class="sk-block" style="height:9px;width:110px;border-radius:3px;"></span>
        <span class="sk-block" style="height:9px;flex:1;border-radius:3px;"></span>
    </div>`;
    return `<div class="dash-act-list">${Array.from({ length: n }, () => row).join('')}</div>`;
}

function _dashTlDetail(ev, isFriend) {
    if (isFriend) {
        switch (ev.type) {
            case 'friend_gps':        return ev.worldName || ev.worldId || '';
            case 'friend_status':     return '';
            case 'friend_statusdesc': return ev.newValue || t('timeline.value.cleared', '(cleared)');
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
        case 'meet_again':     return ev.worldName || '';
        case 'avatar_switch':  return '';
        case 'notification':   return (typeof tlNotifTypeLabel === 'function') ? tlNotifTypeLabel(ev.notifType) : (ev.notifType || '');
        case 'moderation':     return (typeof tlModTypeLabel === 'function')
            ? tlModTypeLabel(ev.notifType, (typeof tlModIsActive === 'function') ? tlModIsActive(ev) : false)
            : (ev.notifType || '');
        case 'profile': {
            if (ev.notifType === 'status') return '';
            if (ev.notifType === 'statusdesc') return ev.message || t('timeline.value.cleared', '(cleared)');
            if (ev.notifType === 'bio') return ev.message || '';
            return (typeof tlProfileMeta === 'function') ? tlProfileMeta(ev).label : '';
        }
        case 'video_url':      return ev.message || ev.url || '';
        default:               return '';
    }
}

function _dashTlStatusChips(oldV, newV) {
    return `<span class="ft-status-chip ${statusCssClass(oldV)}">${esc(statusLabel(oldV) || '?')}</span>`
        + `<span class="msi" style="font-size:11px;color:var(--tx3);">arrow_forward</span>`
        + `<span class="ft-status-chip ${statusCssClass(newV)}">${esc(statusLabel(newV) || '?')}</span>`;
}

function _dashTlRows(events, isFriend) {
    if (!events.length) {
        return `<div class="empty-msg" style="padding:16px 8px;">${t('dashboard.timeline.empty', 'No events yet')}</div>`;
    }
    const rows = events.slice(0, 10).map(ev => {
        const ei = jsq(ev.id);
        let icon, color, name, click;
        if (isFriend) {
            const meta = (typeof ftTypeMeta === 'function') ? ftTypeMeta(ev.type) : { icon: 'group', label: '' };
            icon  = meta.icon;
            color = ev.type === 'friend_gps'
                ? ((typeof getFtGpsColor === 'function') ? getFtGpsColor(ev) : 'var(--accent)')
                : ((typeof FT_TYPE_COLOR !== 'undefined' && FT_TYPE_COLOR[ev.type]) || 'var(--tx3)');
            name  = ev.friendName || t('timeline.unknown', 'Unknown');
            click = ev.type === 'friend_gps' ? `openFtGpsDetail('${ei}')` : `openFtDetail('${ei}')`;
        } else {
            const meta = (typeof tlTypeMeta === 'function') ? tlTypeMeta(ev.type) : { icon: 'schedule', label: '' };
            icon  = meta.icon;
            color = (typeof getTlEventColor === 'function') ? getTlEventColor(ev) : 'var(--accent)';
            name  = ev.userName || ev.senderName || (currentVrcUser?.displayName ?? '');
            click = `openTlDetail('${ei}')`;
        }
        let detail = _dashTlDetail(ev, isFriend);
        if (!detail) {
            const meta = isFriend
                ? ((typeof ftTypeMeta === 'function') ? ftTypeMeta(ev.type) : null)
                : ((typeof tlTypeMeta === 'function') ? tlTypeMeta(ev.type) : null);
            detail = meta?.label || '';
        }
        const img = isFriend
            ? (ev.friendImage || '')
            : (ev.type === 'notification' ? (ev.senderImage || '') : (ev.userImage || currentVrcUser?.image || ''));
        const av = img
            ? `<span class="dash-act-av" style="background-image:url('${cssUrl(imgThumb(img, 48))}')"></span>`
            : `<span class="dash-act-av dash-act-av-letter">${esc((name || '?')[0].toUpperCase())}</span>`;
        const d = new Date(ev.timestamp);
        const isStatusChange = typeof statusCssClass === 'function' && (
            (isFriend && ev.type === 'friend_status') ||
            (!isFriend && ev.type === 'profile' && ev.notifType === 'status'));
        const detailHtml = isStatusChange
            ? `<span class="dash-act-detail" style="display:flex;align-items:center;gap:5px;overflow:hidden;">${
                isFriend ? _dashTlStatusChips(ev.oldValue, ev.newValue) : _dashTlStatusChips(ev.notifTitle, ev.message)}</span>`
            : `<span class="dash-act-detail">${esc(detail)}</span>`;
        return `<div class="dash-act-row" onclick="${click}">
            <span class="msi dash-act-icon" style="color:${color}">${icon}</span>
            <span class="dash-act-time">${esc(fmtShortDate(d))}<span class="dash-act-clock">${esc(fmtTime(d))}</span></span>
            ${av}
            <span class="dash-act-name">${esc(name)}</span>
            ${detailHtml}
        </div>`;
    }).join('');
    return `<div class="dash-act-list">${rows}</div>`;
}

function renderDashMyRecentTimeline() {
    const el = document.getElementById('dashMyRecentTl');
    if (!el) return;
    if (!currentVrcUser) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.timeline.login', 'Login to see recent activity')}</div>`;
        return;
    }
    const personal = (typeof timelineEvents !== 'undefined') ? timelineEvents : [];
    if (!personal.length && !_dashLayout.hidden.includes('my_recent_activity')) sendToCS({ action: 'getTimeline', offset: 0 });
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
    { id: 'upcoming_events',         nameKey: 'dashboard.section.upcoming_events',         name: 'Upcoming Events' },
    { id: 'group_activity_small',    nameKey: 'dashboard.section.group_activity_small',    name: 'Group Activity (Small)' },
    { id: 'friend_locations_small',  nameKey: 'dashboard.section.friend_locations_small',  name: 'Friends Location (Small)' },
    { id: 'recently_visited',        nameKey: 'dashboard.section.recently_visited',        name: 'Recently Visited' },
    { id: 'recent_photos',           nameKey: 'dashboard.section.recent_photos',           name: 'Recent Photos' },
    { id: 'fav_worlds',              nameKey: 'dashboard.section.fav_worlds',              name: 'Favorite Worlds' },
    { id: 'fav_avatars',             nameKey: 'dashboard.section.fav_avatars',             name: 'Favorite Avatars' },
    { id: 'own_avatars',             nameKey: 'dashboard.section.own_avatars',             name: 'My Avatars' },
    { id: 'popular_worlds',          nameKey: 'dashboard.section.popular_worlds',          name: 'Popular Worlds' },
    { id: 'active_worlds',           nameKey: 'dashboard.section.active_worlds',           name: 'Very Active Worlds' },
    { id: 'my_recent_activity',      nameKey: 'dashboard.section.my_recent_activity',      name: 'My Recent Activity' },
    { id: 'friends_recent_activity', nameKey: 'dashboard.section.friends_recent_activity', name: 'Friends Recent Activity' },
];
const DASH_DEFAULT_ORDER   = DASH_SECTION_META.map(s => s.id);
const DASH_DEFAULT_ROWS = [
    ['my_instances'],
    ['my_recent_activity', 'friends_recent_activity'],
    ['friend_locations_small'],
    ['group_activity_small'],
    ['upcoming_events'],
    ['recent_photos'],
    ['own_avatars', 'fav_avatars'],
    ['popular_worlds', 'active_worlds'],
    ['recently_visited', 'fav_worlds'],
];
const DASH_DEFAULT_VISIBLE = new Set(DASH_DEFAULT_ROWS.flat());

const DASH_HERO_OPTIONS = {
    left:  [
        { id: 'friends_activity', nameKey: 'dashboard.section.friend_activity', name: 'Friends Activity' },
        { id: 'group_activity',   nameKey: 'dashboard.section.group_activity',  name: 'Group Activity' },
        { id: 'pins',             nameKey: 'dashboard.hero.pins',               name: 'Pins' },
    ],
    right: [
        { id: 'next_event',       nameKey: 'dashboard.hero.next_event',          name: 'Next Event' },
        { id: 'vrchat_news',      nameKey: 'dashboard.section.vrchat_news',      name: 'VRChat News' },
        { id: 'friends_activity', nameKey: 'dashboard.section.friend_activity',  name: 'Friends Activity' },
        { id: 'group_activity',   nameKey: 'dashboard.section.group_activity',   name: 'Group Activity' },
        { id: 'pins',             nameKey: 'dashboard.hero.pins',                name: 'Pins' },
    ],
};

let _dashLayout = {
    hero:   { left: 'friends_activity', right: 'group_activity' },
    rows:   DASH_DEFAULT_ROWS.map(r => [...r]),
    order:  [...DASH_DEFAULT_ORDER],
    hidden: DASH_DEFAULT_ORDER.filter(id => !DASH_DEFAULT_VISIBLE.has(id)),
};
let _dashModalLayout = null;
let _dashEditMode = false;

function _dashSyncDerived() {
    _dashLayout.order  = _dashLayout.rows.flat().filter(Boolean);
    _dashLayout.hidden = DASH_DEFAULT_ORDER.filter(id => !_dashLayout.order.includes(id));
}

function loadDashLayout(data) {
    if (!data) { applyDashLayout(); return; }
    const rawHero = data.hero ?? data.dashHero ?? data.DashHero ?? null;
    if (Array.isArray(rawHero)) {
        const okL = DASH_HERO_OPTIONS.left.some(o => o.id === rawHero[0]);
        const okR = DASH_HERO_OPTIONS.right.some(o => o.id === rawHero[1]);
        _dashLayout.hero.left  = okL ? rawHero[0] : (rawHero[0] === '' ? null : _dashLayout.hero.left);
        _dashLayout.hero.right = okR ? rawHero[1] : (rawHero[1] === '' ? null : _dashLayout.hero.right);
    }
    const rawRows = data.rows ?? data.dashRows ?? data.DashRows ?? null;
    if (Array.isArray(rawRows) && rawRows.length) {
        const seen = new Set();
        const rows = [];
        rawRows.forEach(r => {
            const cols = String(r).split('|').slice(0, 2)
                .map(id => (DASH_DEFAULT_ORDER.includes(id) && !seen.has(id)) ? (seen.add(id), id) : null);
            if (cols.length) rows.push(cols);
        });
        _dashLayout.rows = rows.length ? rows : DASH_DEFAULT_ROWS.map(r => [...r]);
    } else {
        const rawOrder  = data.order  ?? data.dashSectionOrder  ?? data.DashSectionOrder  ?? null;
        const rawHidden = data.hidden ?? data.dashSectionHidden ?? data.DashSectionHidden ?? null;
        if (Array.isArray(rawOrder) && rawOrder.length) {
            const hidden = Array.isArray(rawHidden) ? rawHidden : [];
            const visible = rawOrder.filter(id => DASH_DEFAULT_ORDER.includes(id) && !hidden.includes(id));
            if (visible.length) _dashLayout.rows = visible.map(id => [id]);
        }
    }
    applyDashLayout();
}

function applyDashLayout() {
    const container = document.getElementById('dashSectionsContainer');
    if (!container) return;
    _dashSyncDerived();

    container.querySelectorAll('.dash-section-wrap').forEach(w => {
        container.appendChild(w);
        w.toggleAttribute('data-hidden', true);
        w.style.order = '';
        w.querySelector(':scope > .dash-widget-x')?.remove();
    });
    container.querySelectorAll(':scope > .dash-drow, :scope > .dash-edit-bar, :scope > .dash-add-row').forEach(r => r.remove());
    _dashCloseEditMenu();

    if (_dashEditMode) {
        const bar = document.createElement('div');
        bar.className = 'dash-edit-bar';
        bar.innerHTML = `<span class="dash-edit-bar-title"><span class="msi">dashboard_customize</span>${esc(t('dashboard.edit.title', 'Dashboard Layout'))}</span>
            <button class="vrcn-button" onclick="dashEditReset()"><span class="msi" style="font-size:16px;">restart_alt</span>${esc(t('dashboard.edit.reset', 'Reset'))}</button>
            <button class="vrcn-button active" onclick="dashEditDone()"><span class="msi" style="font-size:16px;">check</span>${esc(t('dashboard.edit.done', 'Done'))}</button>`;
        container.appendChild(bar);
    }

    _dashLayout.rows.forEach((cols, rowIdx) => {
        const row = document.createElement('div');
        row.className = 'dash-drow';
        row.dataset.cols = String(cols.length);
        cols.forEach((id, colIdx) => {
            const slot = document.createElement('div');
            slot.className = 'dash-drow-slot';
            const meta = id ? DASH_SECTION_META.find(s => s.id === id) : null;
            const excluded = !!(meta?.windowsOnly && window._isLinuxUi);
            const wrap = (id && !excluded) ? container.querySelector(`.dash-section-wrap[data-section="${id}"]`) : null;
            if (wrap) {
                wrap.toggleAttribute('data-hidden', false);
                slot.appendChild(wrap);
                if (_dashEditMode) {
                    const x = document.createElement('button');
                    x.className = 'dash-widget-x';
                    x.title = t('dashboard.edit.remove_widget', 'Remove widget');
                    x.innerHTML = '<span class="msi">close</span>';
                    x.onclick = ev => { ev.stopPropagation(); dashEditRemoveWidget(rowIdx, colIdx); };
                    wrap.appendChild(x);
                }
            } else if (_dashEditMode) {
                const add = document.createElement('button');
                add.className = 'dash-slot-add';
                add.innerHTML = `<span class="msi">add</span><span>${esc(t('dashboard.edit.add_widget', 'Add Widget'))}</span>`;
                add.onclick = ev => dashEditPickWidget(ev, rowIdx, colIdx);
                slot.appendChild(add);
            }
            row.appendChild(slot);
        });
        if (_dashEditMode) {
            const tools = document.createElement('div');
            tools.className = 'dash-drow-tools';

            const drag = document.createElement('button');
            drag.className = 'dash-drow-btn dash-drow-drag';
            drag.title = t('dashboard.edit.drag_row', 'Drag to reorder');
            drag.innerHTML = '<span class="msi">drag_indicator</span>';
            drag.draggable = true;
            drag.addEventListener('dragstart', ev => {
                _dashDragRow = rowIdx;
                ev.dataTransfer.effectAllowed = 'move';
                try { ev.dataTransfer.setData('text/plain', String(rowIdx)); } catch {}
                row.classList.add('dash-dragging');
                _dashDragY = ev.clientY;
                if (!_dashDragScrollRaf) _dashDragScrollRaf = requestAnimationFrame(_dashDragAutoScroll);
            });
            drag.addEventListener('dragend', () => {
                _dashDragRow = null;
                document.querySelectorAll('.dash-drow').forEach(r =>
                    r.classList.remove('dash-dragging', 'dash-drop-before', 'dash-drop-after'));
            });

            const rx = document.createElement('button');
            rx.className = 'dash-drow-btn dash-drow-x';
            rx.title = t('dashboard.edit.remove_row', 'Remove container');
            rx.innerHTML = '<span class="msi">delete</span>';
            rx.onclick = () => dashEditRemoveRow(rowIdx);

            tools.appendChild(drag);
            tools.appendChild(rx);
            row.appendChild(tools);

            row.addEventListener('dragover', ev => {
                if (_dashDragRow === null || _dashDragRow === rowIdx) return;
                ev.preventDefault();
                ev.dataTransfer.dropEffect = 'move';
                const r = row.getBoundingClientRect();
                const before = ev.clientY < r.top + r.height / 2;
                row.classList.toggle('dash-drop-before', before);
                row.classList.toggle('dash-drop-after', !before);
            });
            row.addEventListener('dragleave', () => {
                row.classList.remove('dash-drop-before', 'dash-drop-after');
            });
            row.addEventListener('drop', ev => {
                ev.preventDefault();
                const from = _dashDragRow;
                _dashDragRow = null;
                row.classList.remove('dash-drop-before', 'dash-drop-after');
                if (from === null || from === rowIdx) return;
                const r = row.getBoundingClientRect();
                const before = ev.clientY < r.top + r.height / 2;
                let to = rowIdx + (before ? 0 : 1);
                const [moved] = _dashLayout.rows.splice(from, 1);
                if (from < to) to--;
                _dashLayout.rows.splice(to, 0, moved);
                applyDashLayout();
                saveSettings();
            });
        }
        container.appendChild(row);
    });

    if (_dashEditMode) {
        const add = document.createElement('button');
        add.className = 'dash-add-row';
        add.innerHTML = `<span class="msi">add</span><span>${esc(t('dashboard.edit.add_container', 'Add Container'))}</span>`;
        add.onclick = ev => dashEditPickContainer(ev);
        container.appendChild(add);
    }

    _dashLayout.order.forEach(id => {
        if (id === 'my_instances') renderMyInstances(_myInstancesData);
        if (id === 'fav_worlds') renderDashFavWorlds();
        if (id === 'fav_avatars') renderDashFavAvatars();
        if (id === 'own_avatars') renderDashOwnAvatars();
        if (id === 'recent_photos') renderDashRecentPhotos();
        if (id === 'recently_visited') renderDashRecentlyVisited();
        if (id === 'popular_worlds') renderDashPopularWorlds();
        if (id === 'active_worlds') renderDashActiveWorlds();
        if (id === 'group_activity_small') renderDashGroupActivityInstancesSmall();
        if (id === 'my_recent_activity') renderDashMyRecentTimeline();
        if (id === 'friends_recent_activity') renderDashFriendsRecentTimeline();
        if (id === 'upcoming_events') { renderDashUpcomingEvents(); if (_dashUpcomingEvents === null && !_dashUpcomingLoading) refreshDashUpcomingEvents(); }
    });
    renderDashHeroWidgets();
}


let _dashDragY = 0;
let _dashDragScrollRaf = 0;
document.addEventListener('dragover', e => { _dashDragY = e.clientY; }, { capture: true, passive: true });

function _dashDragAutoScroll() {
    if (_dashDragRow === null) { _dashDragScrollRaf = 0; return; }
    const c = document.querySelector('.content');
    if (c) {
        const r = c.getBoundingClientRect();
        const zone = 90;
        if (_dashDragY < r.top + zone) {
            c.scrollTop -= Math.ceil((r.top + zone - _dashDragY) / zone * 18);
        } else if (_dashDragY > r.bottom - zone) {
            c.scrollTop += Math.ceil((_dashDragY - (r.bottom - zone)) / zone * 18);
        }
    }
    _dashDragScrollRaf = requestAnimationFrame(_dashDragAutoScroll);
}

function dashHeroScrollDown() {
    const c = document.querySelector('.content');
    const sec = document.getElementById('dashSectionsContainer');
    if (c && sec) c.scrollTo({ top: Math.max(0, sec.offsetTop - 70), behavior: 'smooth' });
}

function _dashHeroLabel(side, id) {
    const o = DASH_HERO_OPTIONS[side].find(x => x.id === id);
    return o ? t(o.nameKey, o.name) : '';
}

function _dashHeroFriendsHtml() {
    if (!currentVrcUser) return `<div class="dash-hw-empty">${esc(t('dashboard.section.login', 'Login to VRChat'))}</div>`;
    const all = (typeof vrcFriendsData !== 'undefined' ? vrcFriendsData : [])
        .filter(f => f.presence === 'game');
    const list = all.slice(0, 60);
    if (!list.length) return `<div class="dash-hw-empty">${esc(t('dashboard.friends.empty', 'No friends online'))}</div>`;
    const more = all.length > 60 ? `<button class="dash-hw-more" onclick="showTab(3)">+${all.length - 60}</button>` : '';
    return `<div class="dash-hw-friends">` + list.map(_dashFlocsCardHtml).join('') + more + `</div>`;
}

function _dashHeroGroupsHtml() {
    if (!currentVrcUser) return `<div class="dash-hw-empty">${esc(t('dashboard.section.login', 'Login to VRChat'))}</div>`;
    if (_dashGroupInstances === null) { loadDashGroupInstances(); return `<div class="dash-hw-empty">${esc(t('dashboard.discovery.loading', 'Loading worlds...'))}</div>`; }
    const list = _dashGroupInstances.slice(0, 60);
    if (!list.length) return `<div class="dash-hw-empty">${esc(t('dashboard.section.group_activity_empty', 'No active group instances right now'))}</div>`;
    return `<div class="dash-hw-friends">` + list.map(inst => {
        const loc = (inst.location || '').replace(/'/g, "\\'");
        const icon = inst.groupIcon || '';
        const av = icon
            ? `<span class="dash-hw-av" style="background-image:url('${cssUrl(imgThumb(icon, 64))}')"></span>`
            : `<span class="dash-hw-av dash-hw-av-letter"><span class="msi" style="font-size:14px;">group</span></span>`;
        const users = inst.capacity > 0 ? `${inst.userCount}/${inst.capacity}` : String(inst.userCount || 0);
        return `<div class="dash-hw-card" onclick="openGroupInstanceDetail('${loc}')">
            ${av}
            <div class="dash-hw-info">
                <div class="dash-hw-name">${esc(inst.groupName || '?')}</div>
                <div class="dash-hw-status">${esc(inst.worldName || '')}</div>
                <div class="dash-hw-world"><span class="msi" style="font-size:11px;">person</span>${esc(users)}</div>
            </div>
        </div>`;
    }).join('') + `</div>`;
}

function _dashHeroPinsHtml() {
    const list = (typeof pinsList === 'function') ? pinsList() : [];
    if (!list.length) return `<div class="dash-hw-empty">${esc(t('pins.empty', 'No pins yet.'))}</div>`;
    return `<div class="dash-hw-friends">` + list.map(pin => {
        const label = pin.name || pin.id;
        const typeLabel = (typeof _pinsTypeLabel === 'function') ? _pinsTypeLabel(pin.type) : '';
        const sub = pin.sub ? `${typeLabel} · ${pin.sub}` : typeLabel;
        const typeIcon = (typeof pinsTypeIcon === 'function') ? pinsTypeIcon(pin.type) : 'push_pin';
        let av;
        if (pin.type === 'feature') {
            av = `<span class="dash-hw-av dash-hw-av-letter"><span class="msi" style="font-size:14px;">${esc(pin.icon || typeIcon)}</span></span>`;
        } else if (pin.image) {
            av = `<span class="dash-hw-av" style="background-image:url('${cssUrl(imgThumb(pin.image, 96))}')"></span>`;
        } else {
            av = `<span class="dash-hw-av dash-hw-av-letter">${esc((label[0] || '?').toUpperCase())}</span>`;
        }
        return `<div class="dash-hw-card" data-pin-type="${esc(pin.type)}" data-pin-id="${esc(pin.id)}" onclick="pinsOpen('${jsq(pin.type)}','${jsq(pin.id)}')">
            ${av}
            <div class="dash-hw-info">
                <div class="dash-hw-name">${esc(label)}</div>
                <div class="dash-hw-world"><span class="msi" style="font-size:11px;">${esc(typeIcon)}</span>${esc(sub)}</div>
            </div>
        </div>`;
    }).join('') + `</div>`;
}

function dashHeroRefreshPins() {
    if (_dashLayout.hero.left === 'pins' || _dashLayout.hero.right === 'pins') renderDashHeroWidgets();
}

function _dashHeroEventHtml() {
    if (!currentVrcUser) return `<div class="dash-hw-empty">${esc(t('dashboard.section.login', 'Login to VRChat'))}</div>`;
    if (_dashUpcomingEvents === null) {
        if (!_dashUpcomingLoading) refreshDashUpcomingEvents();
        return `<div class="dash-hw-empty">${esc(t('dashboard.upcoming.loading', 'Loading events...'))}</div>`;
    }
    const evt = _dashUpcomingEvents[0];
    if (!evt) return `<div class="dash-hw-empty">${esc(t('dashboard.upcoming.empty', 'No upcoming events found'))}</div>`;
    const groupId = jsq(evt.ownerId || evt.groupId || '');
    const eventId = jsq(evt.id || '');
    const title = esc(evt.title || evt.name || t('calendar.untitled_event', 'Untitled Event'));
    const start = new Date(evt.startsAt || evt.startDateTime || evt.startDate || '');
    const hasStart = !isNaN(start.getTime());
    const end = new Date(evt.endsAt || '');
    const timeStr = hasStart ? fmtTime(start) + (!isNaN(end.getTime()) ? ' – ' + fmtTime(end) : '') : '';
    const gid = evt.ownerId || evt.groupId || '';
    const gd = ((typeof myGroups !== 'undefined' ? myGroups : []) || []).find(g => g.id === gid) || {};
    const gname = evt.group?.name || gd.name || '';
    const img = evt.imageUrl || '';
    const desc = esc(evt.description || '');
    const featured = evt.featured === true || (Array.isArray(evt.tags) && evt.tags.some(tag => /featured/i.test(tag)));
    const dateTile = hasStart
        ? `<div class="dash-evt-date ded-overlay"><span class="ded-day">${esc(String(start.getDate()))}</span><span class="ded-mon">${esc(start.toLocaleDateString(getLanguageLocale(), { month: 'short' }))}</span></div>`
        : '';
    return `<div class="dash-evt-feature dash-hw-event${img ? ' has-img' : ''}" onclick="openEventDetail('${groupId}','${eventId}')">
        ${img ? `<img class="dash-evt-img" src="${esc(img)}" alt="" loading="lazy" onerror="this.closest('.dash-evt-feature').classList.remove('has-img')">` : ''}
        ${dateTile}
        <div class="dash-evt-feature-body">
            <div class="dash-evt-title">${title}${featured ? `<span class="dash-evt-featured"><span class="msi">star</span>${esc(t('dashboard.upcoming.featured', 'Featured'))}</span>` : ''}</div>
            ${desc ? `<div class="dash-evt-desc">${desc}</div>` : ''}
            <div class="dash-evt-badge-row">
                ${timeStr ? `<span class="vrcn-badge dash-evt-time-badge"><span class="msi" style="font-size:11px;">schedule</span>${esc(timeStr)}</span>` : ''}
                ${gname ? `<span class="vrcn-badge dash-evt-group-badge"><span class="msi" style="font-size:11px;">group</span>${esc(gname)}</span>` : ''}
            </div>
        </div>
    </div>`;
}

function _dashHeroNewsHtml() {
    if (!_vrcNewsCache.items.length) {
        if (!_vrcNewsLoading) _fetchVrcNews();
        return `<div class="dash-hw-empty">${esc(t('dashboard.news.loading', 'Loading news...'))}</div>`;
    }
    const hero = _vrcNewsCache.items[0];
    return `<div class="dash-news-hero dash-hw-news${hero.img ? ' has-img' : ''}" onclick="openNewsArticle('${jsq(String(hero.id || ''))}','${jsq(hero.link)}','${jsq(hero.title)}')">
        ${hero.img ? `<img class="dash-news-img" src="${esc(hero.img)}" alt="" loading="lazy" onerror="this.closest('.dash-news-hero').classList.remove('has-img')">` : ''}
        <div class="dash-news-hero-body">
            <div class="dash-news-meta">${esc(_fmtNewsDate(hero.pubDate))}</div>
            <div class="dash-news-hero-title">${esc(hero.title)}</div>
            ${hero.excerpt ? `<div class="dash-news-excerpt">${esc(hero.excerpt)}</div>` : ''}
        </div>
    </div>`;
}


function renderDashHeroWidgets() {
    ['left', 'right'].forEach(side => {
        const slot = document.getElementById(side === 'left' ? 'dashHeroLeft' : 'dashHeroRight');
        if (!slot) return;
        const id = _dashLayout.hero[side];
        let body = '';
        if (id === 'friends_activity') body = _dashHeroFriendsHtml();
        else if (id === 'group_activity') body = _dashHeroGroupsHtml();
        else if (id === 'next_event') body = _dashHeroEventHtml();
        else if (id === 'vrchat_news') body = _dashHeroNewsHtml();
        else if (id === 'pins') body = _dashHeroPinsHtml();
        if (!id) {
            slot.innerHTML = _dashEditMode
                ? `<button class="dash-slot-add" onclick="dashHeroPick(event, '${side}')"><span class="msi">add</span><span>${esc(t('dashboard.edit.add_widget', 'Add Widget'))}</span></button>`
                : '';
            return;
        }
        const editBtn = _dashEditMode
            ? `<button class="dash-hw-edit" onclick="dashHeroPick(event, '${side}')"><span class="msi">dashboard_customize</span></button>`
            : '';
        let seeAll = '';
        if (!_dashEditMode && id === 'friends_activity')
            seeAll = `<button class="dash-hw-seeall" onclick="showTab(3);setTimeout(()=>{if(typeof setPeopleFilter==='function')setPeopleFilter('all');if(typeof setAllFriendsStatusFilter==='function')setAllFriendsStatusFilter('ingame');},80)">${esc(t('dashboard.section.see_all', 'SEE ALL →'))}</button>`;
        else if (!_dashEditMode && id === 'group_activity')
            seeAll = `<button class="dash-hw-seeall" onclick="showTab(2);setTimeout(()=>{if(typeof setGroupFilter==='function')setGroupFilter('instances');},80)">${esc(t('dashboard.section.see_all', 'SEE ALL →'))}</button>`;
        slot.innerHTML = `<div class="dash-hw-label">${esc(_dashHeroLabel(side, id))}${editBtn}${seeAll}</div>${body}`;
    });
}

function dashHeroPick(ev, side) {
    const items = DASH_HERO_OPTIONS[side].map(o =>
        `<button class="dash-pick-item" data-id="${esc(o.id)}"><span class="msi">dashboard_customize</span>${esc(t(o.nameKey, o.name))}</button>`).join('')
        + `<button class="dash-pick-item" data-id=""><span class="msi">close</span>${esc(t('dashboard.edit.remove_widget', 'Remove widget'))}</button>`;
    const menu = _dashOpenEditMenu(ev, items);
    menu.querySelectorAll('.dash-pick-item').forEach(b => {
        b.onclick = () => {
            _dashLayout.hero[side] = b.dataset.id || null;
            _dashCloseEditMenu();
            renderDashHeroWidgets();
            saveSettings();
        };
    });
}

let _dashEditMenu = null;
let _dashDragRow = null;

function _dashCloseEditMenu() {
    if (_dashEditMenu) { _dashEditMenu.remove(); _dashEditMenu = null; }
}

function _dashOpenEditMenu(ev, itemsHtml) {
    _dashCloseEditMenu();
    const menu = document.createElement('div');
    menu.className = 'dash-pick-menu';
    menu.innerHTML = itemsHtml;
    document.body.appendChild(menu);
    const r = menu.getBoundingClientRect();
    let x = ev.clientX, y = ev.clientY + 8;
    if (x + r.width  > window.innerWidth  - 8) x = window.innerWidth  - r.width  - 8;
    if (y + r.height > window.innerHeight - 8) y = ev.clientY - r.height - 8;
    menu.style.left = x + 'px';
    menu.style.top  = y + 'px';
    _dashEditMenu = menu;
    setTimeout(() => {
        document.addEventListener('mousedown', function close(e) {
            if (_dashEditMenu && !_dashEditMenu.contains(e.target)) {
                _dashCloseEditMenu();
                document.removeEventListener('mousedown', close, true);
            }
        }, true);
    }, 0);
    return menu;
}

function dashEditPickContainer(ev) {
    const menu = _dashOpenEditMenu(ev, `
        <button class="dash-pick-item" data-cols="1"><span class="msi">remove</span>${esc(t('dashboard.edit.cols1', '1 Column'))}</button>
        <button class="dash-pick-item" data-cols="2"><span class="msi">grid_view</span>${esc(t('dashboard.edit.cols2', '2 Columns'))}</button>`);
    menu.querySelectorAll('.dash-pick-item').forEach(b => {
        b.onclick = () => {
            const n = parseInt(b.dataset.cols, 10) || 1;
            _dashLayout.rows.push(n === 2 ? [null, null] : [null]);
            _dashCloseEditMenu();
            applyDashLayout();
            saveSettings();
        };
    });
}

function dashEditPickWidget(ev, rowIdx, colIdx) {
    const placed = new Set(_dashLayout.rows.flat().filter(Boolean));
    const avail = DASH_SECTION_META.filter(s => !placed.has(s.id) && !(s.windowsOnly && window._isLinuxUi));
    if (!avail.length) {
        _dashOpenEditMenu(ev, `<div class="dash-pick-empty">${esc(t('dashboard.edit.all_placed', 'All widgets are already placed'))}</div>`);
        return;
    }
    const menu = _dashOpenEditMenu(ev, avail.map(s =>
        `<button class="dash-pick-item" data-id="${esc(s.id)}"><span class="msi">dashboard_customize</span>${esc(t(s.nameKey, s.name))}</button>`).join(''));
    menu.querySelectorAll('.dash-pick-item').forEach(b => {
        b.onclick = () => {
            const row = _dashLayout.rows[rowIdx];
            if (row && colIdx < row.length) row[colIdx] = b.dataset.id;
            _dashCloseEditMenu();
            applyDashLayout();
            saveSettings();
        };
    });
}

function dashEditRemoveWidget(rowIdx, colIdx) {
    const row = _dashLayout.rows[rowIdx];
    if (row && colIdx < row.length) row[colIdx] = null;
    applyDashLayout();
    saveSettings();
}

function dashEditRemoveRow(rowIdx) {
    _dashLayout.rows.splice(rowIdx, 1);
    applyDashLayout();
    saveSettings();
}

function dashEditReset() {
    _dashLayout.rows = DASH_DEFAULT_ROWS.map(r => [...r]);
    applyDashLayout();
    saveSettings();
}

function dashEditDone() {
    _dashEditMode = false;
    document.body.classList.remove('dash-edit-mode');
    _dashCloseEditMenu();
    applyDashLayout();
    saveSettings();
}

function openDashLayoutEditor() {
    if (typeof showTab === 'function') showTab(0);
    _dashEditMode = true;
    document.body.classList.add('dash-edit-mode');
    applyDashLayout();
    document.querySelector('.content')?.scrollTo({ top: 0, behavior: 'smooth' });
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
        if (meta.windowsOnly && window._isLinuxUi) return '';
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

// Edge fade on horizontally scrollable shelves
function _dashShelfFade(el) {
    const canL = el.scrollLeft > 4;
    const canR = el.scrollLeft + el.clientWidth < el.scrollWidth - 4;
    el.classList.toggle('shelf-fade-l', canL);
    el.classList.toggle('shelf-fade-r', canR);
}

let _dashShelfFadeRaf = 0;
function _dashShelfFadeAll() {
    if (_dashShelfFadeRaf) return;
    _dashShelfFadeRaf = requestAnimationFrame(() => {
        _dashShelfFadeRaf = 0;
        document.querySelectorAll('.vrcn-dash-fav-shelf').forEach(_dashShelfFade);
    });
}

document.addEventListener('scroll', e => {
    if (e.target instanceof Element && e.target.classList.contains('vrcn-dash-fav-shelf')) _dashShelfFade(e.target);
}, { capture: true, passive: true });
window.addEventListener('resize', _dashShelfFadeAll);
(function () {
    const container = document.getElementById('dashSectionsContainer');
    if (!container || typeof MutationObserver === 'undefined') return;
    new MutationObserver(_dashShelfFadeAll).observe(container, { childList: true, subtree: true });
    _dashShelfFadeAll();
})();

// Edge fade on vertically scrollable hero widget lists.
// The mask sits on each card, not on the scroll container: an ancestor mask
// would isolate the cards and kill their backdrop-filter blur.
function _dashHeroFade(el) {
    const box = el.getBoundingClientRect();
    const zone = Math.min(56, (box.bottom - box.top) / 3);
    const canT = el.scrollTop > 4;
    const canB = el.scrollTop + el.clientHeight < el.scrollHeight - 4;
    let zT = box.top + zone, zB = box.bottom - zone;
    if (canT && canB && zB < zT) { const mid = (box.top + box.bottom) / 2; zT = mid; zB = mid; }
    for (const card of el.children) {
        const r = card.getBoundingClientRect();
        const stops = [];
        if (canT && r.top < zT) {
            stops.push(`transparent ${(box.top - r.top).toFixed(1)}px`, `#000 ${(zT - r.top).toFixed(1)}px`);
        }
        if (canB && r.bottom > zB) {
            stops.push(`#000 ${(zB - r.top).toFixed(1)}px`, `transparent ${(box.bottom - r.top).toFixed(1)}px`);
        }
        if (stops.length) {
            const m = `linear-gradient(to bottom, ${stops.join(', ')})`;
            card.style.webkitMaskImage = m;
            card.style.maskImage = m;
        } else if (card.style.maskImage || card.style.webkitMaskImage) {
            card.style.webkitMaskImage = '';
            card.style.maskImage = '';
        }
    }
}

let _dashHeroFadeRaf = 0;
function _dashHeroFadeAll() {
    if (_dashHeroFadeRaf) return;
    _dashHeroFadeRaf = requestAnimationFrame(() => {
        _dashHeroFadeRaf = 0;
        document.querySelectorAll('.dash-hw-friends').forEach(_dashHeroFade);
    });
}

document.addEventListener('scroll', e => {
    if (e.target instanceof Element && e.target.classList.contains('dash-hw-friends')) _dashHeroFade(e.target);
}, { capture: true, passive: true });
window.addEventListener('resize', _dashHeroFadeAll);
(function () {
    const widgets = document.querySelector('.dash-hero-widgets');
    if (!widgets || typeof MutationObserver === 'undefined') return;
    const ro = typeof ResizeObserver !== 'undefined'
        ? new ResizeObserver(entries => {
            const seen = new Set();
            for (const en of entries) {
                const list = en.target.classList.contains('dash-hw-friends') ? en.target : en.target.parentElement;
                if (list && !seen.has(list)) { seen.add(list); _dashHeroFade(list); }
            }
        })
        : null;
    const rewire = () => {
        if (ro) {
            ro.disconnect();
            widgets.querySelectorAll('.dash-hw-friends').forEach(el => {
                ro.observe(el);
                for (const card of el.children) ro.observe(card);
            });
        }
        _dashHeroFadeAll();
    };
    new MutationObserver(rewire).observe(widgets, { childList: true, subtree: true });
    rewire();
})();

// Drag-to-scroll on shelves
(function () {
    let _shelf = null, _startX = 0, _scrollStart = 0, _dragging = false;

    document.addEventListener('mousedown', e => {
        const shelf = e.target.closest('.vrcn-dash-fav-shelf');
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




function rerenderDashTranslations() {
    updateDashSub();
    renderDashHeroWidgets();
    renderDashFriendsLocationSmall();
    renderDashFavWorlds();
    renderDashFavAvatars();
    renderDashOwnAvatars();
    renderDashRecentPhotos();
    renderDashRecentlyVisited();
    renderDashPopularWorlds();
    renderDashActiveWorlds();
    renderDashGroupActivityInstancesSmall();
    renderDashMyRecentTimeline();
    renderDashFriendsRecentTimeline();
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
    let _glassLast = '';
    let _glassRaf = 0;

    function applyGlass() {
        const onDash = tab0 && tab0.classList.contains('active');
        const t = onDash ? Math.min((content?.scrollTop || 0) / FADE_PX, 1) : 1;
        const target = 1 - t;
        const sig = t.toFixed(3);
        if (onDash === _wasDash && sig === _glassLast) return;
        _glassLast = sig;
        document.body.style.setProperty('--sidebar-glass-t', sig);
        if (_fadeAnim) { _fadeAnim.cancel(); _fadeAnim = null; }
        if (onDash && !_wasDash) {
            vignette.style.opacity = '0';
            _fadeAnim = vignette.animate([{ opacity: 0 }, { opacity: target }], { duration: 800, easing: 'ease-in' });
            _fadeAnim.onfinish = () => { _fadeAnim = null; _glassLast = ''; applyGlass(); };
        } else {
            vignette.style.opacity = target.toFixed(3);
        }
        _wasDash = onDash;
    }

    function onGlassScroll() {
        if (_glassRaf) return;
        _glassRaf = requestAnimationFrame(() => { _glassRaf = 0; applyGlass(); });
    }

    function cleanup() {
        content?.removeEventListener('scroll', onGlassScroll);
        document.documentElement.removeEventListener('themechange', applyGlass);
        document.documentElement.removeEventListener('tabchange', applyGlass);
        document.documentElement.removeEventListener('vrcnext:theme:unload:' + THEME_ID, cleanup);
        document.body.style.removeProperty('--sidebar-glass-t');
        vignette.remove();
    }

    applyGlass();
    content?.addEventListener('scroll', onGlassScroll, { passive: true });
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
    if (typeof myGroupsLoaded !== 'undefined' && !myGroupsLoaded) sendToCS({ action: 'vrcGetMyGroups' });
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
    renderDashHeroWidgets();
}

function renderDashUpcomingEvents() {
    const grid = document.getElementById('dashUpcomingEventsGrid');
    if (!grid) return;

    const emptyState = (icon, msg, btn = '') =>
        `<div style="display:flex;flex-direction:column;align-items:center;justify-content:center;gap:10px;padding:28px 0;color:var(--tx3);font-size:calc(12px + var(--fs-off, 0px));">
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

    const evtFields = (evt) => {
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
        const dayNum    = hasStart ? String(startDate.getDate()) : '';
        const monShort  = hasStart ? startDate.toLocaleDateString(getLanguageLocale(), { month: 'short' }) : '';

        const imgSrc = evt.imageUrl || '';
        const desc = esc(evt.description || '');

        const gid = evt.ownerId || evt.groupId || '';
        const groupData = myGroupsList.find(g => g.id === gid) || {};
        const groupName = evt.group?.name || groupData.name || '';
        const groupIcon = evt.group?.iconUrl || groupData.iconUrl || '';

        return { groupId, eventId, title, featured, whenStr, timeStr, dayNum, monShort, imgSrc, desc, groupName, groupIcon };
    };

    const events = _dashUpcomingEvents.slice(0, 9);

    const dateTile = (f, cls) => f.dayNum
        ? `<div class="dash-evt-date${cls ? ' ' + cls : ''}"><span class="ded-day">${esc(f.dayNum)}</span><span class="ded-mon">${esc(f.monShort)}</span></div>`
        : `<div class="dash-evt-date${cls ? ' ' + cls : ''}"><span class="msi ded-icon">event</span></div>`;

    const feat = evtFields(events[0]);
    const featImg = feat.imgSrc
        ? `<img class="dash-evt-img" src="${feat.imgSrc}" alt="" loading="lazy" onerror="this.closest('.dash-evt-feature').classList.remove('has-img')">`
        : '';
    const featBadge = feat.featured
        ? `<span class="dash-evt-featured"><span class="msi">star</span>${esc(t('dashboard.upcoming.featured', 'Featured'))}</span>`
        : '';
    const featGroupIcon = feat.groupIcon
        ? `<img class="deg-icon" src="${imgThumb(feat.groupIcon, 48)}" loading="lazy" onerror="this.outerHTML='<span class=\\'msi\\' style=\\'font-size:11px;\\'>group</span>'">`
        : `<span class="msi" style="font-size:11px;">group</span>`;
    const featGroup = feat.groupName
        ? `<span class="vrcn-badge dash-evt-group-badge">${featGroupIcon}${esc(feat.groupName)}</span>`
        : '';
    const featTime = feat.timeStr
        ? `<span class="vrcn-badge dash-evt-time-badge"><span class="msi" style="font-size:11px;">schedule</span>${esc(feat.timeStr)}</span>`
        : '';
    const featureHtml = `<div class="dash-evt-feature${feat.imgSrc ? ' has-img' : ''}" onclick="openEventDetail('${feat.groupId}','${feat.eventId}')">
        ${featImg}
        ${dateTile(feat, 'ded-overlay')}
        <div class="dash-evt-feature-body">
            <div class="dash-evt-title">${feat.title}${featBadge}</div>
            ${feat.desc ? `<div class="dash-evt-desc">${feat.desc}</div>` : ''}
            ${(featGroup || featTime) ? `<div class="dash-evt-badge-row">${featTime}${featGroup}</div>` : ''}
        </div>
    </div>`;

    const minis = events.slice(1).map(evt => {
        const f = evtFields(evt);
        return `<div class="dash-evt-mini" onclick="openEventDetail('${f.groupId}','${f.eventId}')">
            ${dateTile(f)}
            <div class="dash-evt-mini-info">
                <div class="dash-evt-mini-title">${f.title}</div>
                <div class="dash-evt-mini-when">
                    ${f.timeStr ? `<span class="msi">schedule</span>${esc(f.timeStr)}` : ''}
                    ${f.groupName ? `<span class="dash-evt-mini-grp">${f.groupIcon ? `<img class="dash-evt-mini-gicon" src="${imgThumb(f.groupIcon, 32)}" loading="lazy" onerror="this.style.display='none'">` : ''}<span class="dash-evt-mini-group">${esc(f.groupName)}</span></span>` : ''}
                </div>
            </div>
        </div>`;
    }).join('');

    grid.innerHTML = `<div class="dash-evt-layout${minis ? '' : ' no-list'}">${featureHtml}${minis ? `<div class="dash-evt-list">${minis}</div>` : ''}</div>`;
}

