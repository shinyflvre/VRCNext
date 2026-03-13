/* === Dashboard === */

let _dashOnlineCount = 0;
let _dashOnlineCountLastFetch = 0;

function updateDashSub() {
    const name = currentVrcUser?.displayName;
    const status = name
        ? (currentVrcUser.statusDescription || statusLabel(currentVrcUser.status))
        : 'Connect to VRChat to see your world';
    const suffix = _dashOnlineCount > 0 ? ` | ${_dashOnlineCount.toLocaleString()} playing worldwide` : '';
    document.getElementById('dashSub').textContent = status + suffix;
}

function renderDashboard() {
    const name = currentVrcUser?.displayName;
    document.getElementById('dashWelcome').textContent = name ? `Welcome, ${name}!` : 'Welcome!';
    updateDashSub();

    const bgEl = document.getElementById('dashHeroBg');
    if (dashBgDataUri) {
        bgEl.style.backgroundImage = `url('${dashBgDataUri}')`;
    } else if (dashBgPath) {
        const fileUri = 'file:///' + dashBgPath.replace(/\\/g, '/');
        bgEl.style.backgroundImage = `url('${fileUri}')`;
    } else {
        bgEl.style.backgroundImage = `url('fallback_bg.png')`;
    }
    const fadeEl = document.querySelector('.dash-hero-fade');
    if (fadeEl) {
        const op = dashOpacity / 100;
        fadeEl.style.background = `linear-gradient(to bottom, rgba(0,0,0,${op * 0.4}) 0%, var(--bg-base) 100%)`;
    }
    if (currentSpecialTheme === 'auto') applyAutoColor();

    renderDashWorlds();
    renderDashFriendsFeed();
    const now = Date.now();
    if (now - _dashOnlineCountLastFetch >= 10 * 60 * 1000) {
        _dashOnlineCountLastFetch = now;
        sendToCS({ action: 'vrcGetOnlineCount' });
    }
}

function requestWorldResolution() {
    if (!vrcFriendsData.length) return;
    const worldIds = new Set();
    vrcFriendsData.forEach(f => {
        const { worldId } = parseFriendLocation(f.location);
        if (worldId && worldId.startsWith('wrld_') && !dashWorldCache[worldId]) worldIds.add(worldId);
    });
    if (worldIds.size > 0) {
        sendToCS({ action: 'vrcResolveWorlds', worldIds: Array.from(worldIds) });
    }
}

function renderDashWorlds() {
    const el = document.getElementById('dashFavWorlds');
    if (!currentVrcUser || !vrcFriendsLoaded) {
        el.innerHTML = sk('world', 3);
        return;
    }
    if (!vrcFriendsData.length) {
        el.innerHTML = '<div class="empty-msg">No friends in worlds right now</div>';
        return;
    }

    // Group friends by worldId parsed from location
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
        el.innerHTML = '<div class="empty-msg">No friends in worlds right now</div>';
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
                    ? `<img class="dash-world-friend-av" src="${img}" title="${esc(f.displayName)}" onerror="this.style.display='none'">`
                    : `<div class="dash-world-friend-av" title="${esc(f.displayName)}" style="display:flex;align-items:center;justify-content:center;font-size:9px;font-weight:700;color:var(--tx3)">${esc((f.displayName||'?')[0])}</div>`;
            }).join('');
            const extra = w.friends.length > 5 ? `<span class="dash-world-extra">+${w.friends.length - 5}</span>` : '';
            return `<div class="dash-world-card">
                <div class="dash-world-thumb"><div class="dash-world-thumb-overlay"></div></div>
                <div class="dash-world-info">
                    <div class="dash-world-name" style="color:var(--tx3)">Loading world...</div>
                    <div class="dash-world-friends-row">${friendAvatars}${extra}</div>
                    <div class="dash-world-meta"><span class="dash-world-count">${w.friends.length} friend${w.friends.length !== 1 ? 's' : ''} in world</span></div>
                </div>
            </div>`;
        }).join('');
        return;
    }

    el.innerHTML = worldList.map(w => {
        const friendAvatars = w.friends.slice(0, 5).map(f => {
            const img = f.image || '';
            return img
                ? `<img class="dash-world-friend-av" src="${img}" title="${esc(f.displayName)}" onerror="this.style.display='none'">`
                : `<div class="dash-world-friend-av" title="${esc(f.displayName)}" style="display:flex;align-items:center;justify-content:center;font-size:9px;font-weight:700;color:var(--tx3)">${esc((f.displayName||'?')[0])}</div>`;
        }).join('');
        const extra = w.friends.length > 5 ? `<span class="dash-world-extra">+${w.friends.length - 5}</span>` : '';
        const thumbStyle = w.thumb ? `background-image:url('${cssUrl(w.thumb)}')` : '';
        const wid = (w.worldId || '').replace(/'/g, "\\'");
        const displayName = w.name || w.worldId;
        const instCount = w.instances ? w.instances.size : 1;
        const countLabel = instCount > 1
            ? `${w.friends.length} friend${w.friends.length !== 1 ? 's' : ''} in world`
            : `${w.friends.length} friend${w.friends.length !== 1 ? 's' : ''} here`;
        const { cls: dwInstCls, label: dwInstLabel } = getInstanceBadge(w.instanceType);
        const instBadge = instCount > 1
            ? `<span style="font-size:9px;color:var(--tx3);margin-left:auto;">${instCount} instances</span>`
            : `<span class="vrcn-badge ${dwInstCls}" style="margin-left:auto;">${dwInstLabel}</span>`;
        return `<div class="dash-world-card" onclick="openWorldDetail('${wid}')">
            <div class="dash-world-thumb" style="${thumbStyle}"><div class="dash-world-thumb-overlay"></div></div>
            <div class="dash-world-info">
                <div class="dash-world-name">${esc(displayName)}</div>
                <div class="dash-world-friends-row">${friendAvatars}${extra}</div>
                <div class="dash-world-meta"><span class="dash-world-count">${countLabel}</span>${instBadge}</div>
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
        el.innerHTML = '<div class="empty-msg">No friends online</div>';
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
        const loc = f.presence === 'web' ? 'Web / Mobile' : (isPrivate ? 'Private Instance' : (cached?.name || 'In World'));
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

    if (!_myInstancesData.length) {
        label.style.display = 'none';
        grid.style.display  = 'none';
        return;
    }

    label.style.display = '';
    grid.style.display  = '';

    grid.innerHTML = _myInstancesData.map(inst => {
        const { cls, label: typeLabel } = getInstanceBadge(inst.instanceType);
        const thumbStyle = inst.worldThumb ? `background-image:url('${cssUrl(inst.worldThumb)}')` : '';
        const wid = (inst.worldId || '').replace(/'/g, "\\'");
        const count = inst.userCount || 0;
        const cap   = inst.capacity  || 0;
        const countStr = cap > 0 ? `${count}/${cap} players` : `${count} players`;
        const safeLoc = (inst.location || '').replace(/'/g, "\\'");
        return `<div class="dash-world-card" onclick="openMyInstanceDetail('${wid}','${safeLoc}')" data-location="${esc(inst.location || '')}">
            <div class="dash-world-thumb" style="${thumbStyle}"><div class="dash-world-thumb-overlay"></div></div>
            <div class="dash-world-info">
                <div class="dash-world-name">${esc(inst.worldName || inst.worldId || 'Unknown World')}</div>
                <div class="dash-world-friends-row"></div>
                <div class="dash-world-meta">
                    <span class="dash-world-count">${esc(countStr)}</span>
                    <span class="vrcn-badge ${cls}" style="margin-left:auto;">${esc(typeLabel)}</span>
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
    showToast(true, 'Instance removed.');
}

function closeMyInstanceDetail() {
    document.getElementById('modalMyInstance').style.display = 'none';
}

function openMyInstanceDetail(worldId, location) {
    const inst = _myInstancesData.find(i => i.location === location) || _myInstancesData.find(i => i.worldId === worldId);
    if (!inst) return;

    const m  = document.getElementById('modalMyInstance');
    const c  = document.getElementById('myInstanceContent');
    const thumb = inst.worldThumb || '';
    const { cls, label: typeLabel } = getInstanceBadge(inst.instanceType);
    const instNum = (inst.location || '').match(/:(\d+)/)?.[1] || '';
    const canJoin = inst.instanceType !== 'private' && inst.instanceType !== 'invite_plus';

    // Friends in this instance (match by instance number)
    const instFriends = (typeof vrcFriendsData !== 'undefined')
        ? vrcFriendsData.filter(f => f.location && f.location.match(/:(\d+)/)?.[1] === instNum)
        : [];

    const bannerHtml = thumb
        ? `<div class="fd-banner"><img src="${thumb}" onerror="this.parentElement.style.display='none'"><div class="fd-banner-fade"></div></div>`
        : '';

    const copyBadge = instNum
        ? `<span class="vrcn-id-clip" onclick="copyInstanceLink('${jsq(inst.location)}')"><span class="msi" style="font-size:12px;">content_copy</span>#${esc(instNum)}</span>`
        : '';

    let friendsHtml = '<div class="wd-friends-label">FRIENDS IN THIS INSTANCE</div><div class="wd-friends-list">';
    if (instFriends.length > 0) {
        instFriends.forEach(f => {
            friendsHtml += renderProfileItem(f, `closeMyInstanceDetail();openFriendDetail('${jsq(f.id || '')}')`);
        });
    } else {
        friendsHtml += `<div class="vrcn-profile-item" style="pointer-events:none;opacity:0.55;">
            <div class="fd-profile-item-avatar" style="display:flex;align-items:center;justify-content:center;"><span class="msi" style="font-size:20px;color:var(--tx3);">person</span></div>
            <div class="fd-profile-item-info">
                <div class="fd-profile-item-name">No friends here yet!</div>
                <div class="fd-profile-item-status">Invite friends to this instance!</div>
            </div>
        </div>`;
    }
    friendsHtml += '</div>';

    const mloc = jsq(inst.location || '');
    const mwn  = jsq(inst.worldName || '');
    const mwt  = jsq(inst.worldThumb || '');
    const mit  = jsq(inst.instanceType || '');
    const loc  = (inst.location || '').replace(/'/g, "\\'");

    c.innerHTML = `${bannerHtml}<div class="fd-content${thumb ? ' fd-has-banner' : ''}" style="padding:16px;">
        <h2 style="margin:0 0 4px;color:var(--tx0);font-size:18px;">${esc(inst.worldName || inst.worldId || 'Unknown World')}</h2>
        <div style="display:flex;justify-content:flex-end;gap:6px;margin-bottom:4px;">
            <button class="vrcn-button-round" title="Invite Friends" onclick="closeMyInstanceDetail();openInviteModalForLocation('${mloc}','${mwn}','${mwt}','${mit}')"><span class="msi" style="font-size:16px;">person_add</span></button>
            <button class="vrcn-button-round vrcn-btn-danger" title="Remove Instance" onclick="removeMyInstance('${loc}')"><span class="msi" style="font-size:16px;">delete</span></button>
        </div>
        <div class="fd-badges-row"><span class="vrcn-badge ${cls}">${typeLabel}</span>${copyBadge}</div>
        ${friendsHtml}
        <div class="fd-actions">
            ${canJoin ? `<button class="vrcn-button-round vrcn-btn-join" onclick="closeMyInstanceDetail();sendToCS({action:'vrcJoinFriend',location:'${loc}'})">Join World</button>` : ''}
            <button class="vrcn-button-round" onclick="closeMyInstanceDetail();openWorldSearchDetail('${jsq(worldId)}')">Open World</button>
            <button class="vrcn-button-round" style="margin-left:auto;" onclick="closeMyInstanceDetail()">Close</button>
        </div>
    </div>`;
    m.style.display = 'flex';
}

/* === Discovery Section === */
let _discTab  = 'discovery';
let _discPage = 0;
const DISC_PER_PAGE   = 8;
const DISC_CACHE_TTL  = 10 * 60 * 1000;
let _popularCache = { worlds: [], ts: 0 };
let _activeCache  = { worlds: [], ts: 0 };

setInterval(() => {
    sendToCS({ action: 'vrcGetPopularWorlds' });
    sendToCS({ action: 'vrcGetActiveWorlds' });
}, DISC_CACHE_TTL);

function setDiscoveryTab(tab) {
    _discTab  = tab;
    _discPage = 0;
    document.querySelectorAll('.disc-tab').forEach(btn => {
        btn.classList.toggle('active', btn.dataset.tab === tab);
    });
    if (tab === 'popular') _fetchPopularWorlds();
    else if (tab === 'active') _fetchActiveWorlds();
    else renderDiscoverySection();
}

function _fetchPopularWorlds() {
    if (Date.now() - _popularCache.ts < DISC_CACHE_TTL && _popularCache.worlds.length) {
        renderDiscoverySection();
        return;
    }
    document.getElementById('dashDiscoveryGrid').innerHTML = '<div class="empty-msg">Loading worlds...</div>';
    sendToCS({ action: 'vrcGetPopularWorlds' });
}

function _fetchActiveWorlds() {
    if (Date.now() - _activeCache.ts < DISC_CACHE_TTL && _activeCache.worlds.length) {
        renderDiscoverySection();
        return;
    }
    document.getElementById('dashDiscoveryGrid').innerHTML = '<div class="empty-msg">Loading worlds...</div>';
    sendToCS({ action: 'vrcGetActiveWorlds' });
}

function onPopularWorlds(worlds) {
    _popularCache = { worlds: worlds || [], ts: Date.now() };
    if (_discTab === 'popular') renderDiscoverySection();
}

function onActiveWorlds(worlds) {
    _activeCache = { worlds: worlds || [], ts: Date.now() };
    if (_discTab === 'active') renderDiscoverySection();
}

function discPageChange(dir) {
    const cache = _discTab === 'popular' ? _popularCache : _activeCache;
    const maxPage = Math.max(0, Math.ceil(cache.worlds.length / DISC_PER_PAGE) - 1);
    _discPage = Math.max(0, Math.min(maxPage, _discPage + dir));
    renderDiscoverySection();
}

function renderDiscoverySection() {
    const grid       = document.getElementById('dashDiscoveryGrid');
    const pagination = document.getElementById('discPagination');
    if (!grid) return;

    if (_discTab === 'discovery') {
        if (pagination) pagination.style.display = 'none';
        renderDiscovery();
        return;
    }

    const cache = _discTab === 'popular' ? _popularCache : _activeCache;
    if (!cache.worlds.length) {
        grid.innerHTML = '<div class="empty-msg">Loading worlds...</div>';
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
        const playingStr = occupants > 0 ? `${occupants.toLocaleString()} playing` : '';
        return `<div class="dash-world-card" onclick="openWorldSearchDetail('${wid}')">
            <div class="dash-world-thumb" style="${thumbStyle}"><div class="dash-world-thumb-overlay"></div></div>
            <div class="dash-world-info">
                <div class="dash-world-name">${name}</div>
                ${playingStr ? `<div class="dash-world-meta"><span class="dash-world-count">${playingStr}</span></div>` : ''}
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

const DISCOVERY_URL = 'https://vrcn.shinyflvres.com/Dashboard_Discovery.json';

const DISCOVERY_TAG_COLORS = {
    'Hangout':    { bg: '#3b82f6', tx: '#fff' },
    'Photography':{ bg: '#8b5cf6', tx: '#fff' },
    'Game':       { bg: '#ef4444', tx: '#fff' },
    'Events':     { bg: '#f59e0b', tx: '#000' },
    'Abstract':   { bg: '#6366f1', tx: '#fff' },
    'Lovely':     { bg: '#ec4899', tx: '#fff' },
    'Cozy':       { bg: '#f97316', tx: '#fff' },
    'Open World': { bg: '#22c55e', tx: '#fff' },
    'Home':       { bg: '#14b8a6', tx: '#fff' },
    'Outdoor':    { bg: '#84cc16', tx: '#000' },
    'Indoor':     { bg: '#a78bfa', tx: '#fff' },
    'Brainrot':   { bg: '#f43f5e', tx: '#fff' },
};

let discoveryWorlds = [];

function fetchDiscovery() {
    sendToCS({ action: 'fetchDiscoveryFeed', url: DISCOVERY_URL });
}

function onDiscoveryFeed(json) {
    try {
        const data = JSON.parse(json);
        if (!Array.isArray(data) || data.length === 0) return;
        discoveryWorlds = data.slice(0, 8);
        const unresolvedIds = discoveryWorlds
            .map(w => w.WorldID || w.worldId || '')
            .filter(id => id.startsWith('wrld_') && !dashWorldCache[id]);
        if (unresolvedIds.length > 0) {
            sendToCS({ action: 'vrcResolveWorlds', worldIds: unresolvedIds });
            setTimeout(() => {
                const stillUnresolved = discoveryWorlds.some(w => {
                    const id = w.WorldID || w.worldId || '';
                    return id.startsWith('wrld_') && !dashWorldCache[id];
                });
                if (stillUnresolved && _discTab === 'discovery') renderDiscovery();
            }, 8000);
        } else if (_discTab === 'discovery') {
            renderDiscovery();
        }
    } catch (_) {}
}

function renderDiscovery() {
    const grid = document.getElementById('dashDiscoveryGrid');
    if (!grid) return;

    if (!discoveryWorlds.length) {
        grid.innerHTML = '<div class="empty-msg">No discovery worlds available</div>';
        return;
    }

    grid.innerHTML = discoveryWorlds.map(w => {
        const wid  = (w.WorldID || w.worldId || '').trim();
        const desc = w.Description || w.description || '';
        const tags = Array.isArray(w.Tags || w.tags) ? (w.Tags || w.tags) : [];
        const cached = dashWorldCache[wid];
        const name  = cached?.name || wid;
        const thumb = cached?.thumbnailImageUrl || cached?.imageUrl || '';
        const thumbStyle = thumb ? `background-image:url('${cssUrl(thumb)}')` : '';
        const safeWid = wid.replace(/'/g, "\\'");

        const tagHtml = tags.map(t => {
            const col = DISCOVERY_TAG_COLORS[t] || { bg: 'var(--bg3)', tx: 'var(--tx2)' };
            return `<span class="vrcn-badge" style="background:${col.bg};color:${col.tx}">${esc(t)}</span>`;
        }).join('');

        return `<div class="dash-world-card disc-card" onclick="openWorldDetail('${safeWid}')">
            <div class="dash-world-thumb" style="${thumbStyle}"><div class="dash-world-thumb-overlay"></div></div>
            <div class="dash-world-info">
                <div class="dash-world-name">${esc(name)}</div>
                ${desc ? `<div class="disc-desc">${esc(desc)}</div>` : ''}
                ${tagHtml ? `<div class="disc-tags-row">${tagHtml}</div>` : ''}
            </div>
        </div>`;
    }).join('');
}
