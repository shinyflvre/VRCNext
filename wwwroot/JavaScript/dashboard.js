/* === Dashboard === */
function parseFriendLocation(loc) {
    if (!loc || loc === 'private' || loc === 'offline' || loc === 'traveling') return { worldId: '', instanceType: loc || 'private' };
    var worldId = loc.includes(':') ? loc.split(':')[0] : loc;
    var instanceType = 'public';
    if (loc.includes('~private(')) instanceType = 'private';
    else if (loc.includes('~friends+(')) instanceType = 'friends+';
    else if (loc.includes('~friends(')) instanceType = 'friends';
    else if (loc.includes('~hidden(')) instanceType = 'hidden';
    else if (loc.includes('~group(')) {
        var gatMatch = loc.match(/groupAccessType\(([^)]+)\)/);
        var gat = gatMatch ? gatMatch[1].toLowerCase() : '';
        if (gat === 'public') instanceType = 'group-public';
        else if (gat === 'plus') instanceType = 'group-plus';
        else if (gat === 'members') instanceType = 'group-members';
        else instanceType = 'group';
    }
    return { worldId, instanceType };
}

function getInstanceBadge(instanceType) {
    const t = instanceType || 'public';
    // Map API instance types to VRChat UI names
    const labels = { 'public':'Public', 'friends':'Friends', 'friends+':'Friends+', 'hidden':'Friends+',
                     'private':'Invite', 'group':'Group', 'group-public':'Group Public',
                     'group-plus':'Group+', 'group-members':'Group' };
    const label = labels[t] || t.charAt(0).toUpperCase() + t.slice(1);
    let cls = 'public';
    if (t === 'friends' || t === 'friends+' || t === 'hidden') cls = 'friends';
    else if (t === 'private') cls = 'private';
    else if (t.startsWith('group')) cls = 'group';
    return { cls, label };
}

function renderDashboard() {
    const name = currentVrcUser?.displayName;
    document.getElementById('dashWelcome').textContent = name ? `Welcome, ${name}!` : 'Welcome!';
    document.getElementById('dashSub').textContent = name
        ? (currentVrcUser.statusDescription || statusLabel(currentVrcUser.status))
        : 'Connect to VRChat to see your world';

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

    renderDashWorlds();
    renderDashFriendsFeed();
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
            : `<span class="fd-instance-badge ${dwInstCls}" style="font-size:9px;margin-left:auto;">${dwInstLabel}</span>`;
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

/* === Discovery Section === */
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
            // Wait for vrcWorldsResolved before rendering to avoid flashing raw IDs.
            // Fallback render after 8s in case the API call fails or user is not logged in.
            setTimeout(() => {
                const stillUnresolved = discoveryWorlds.some(w => {
                    const id = w.WorldID || w.worldId || '';
                    return id.startsWith('wrld_') && !dashWorldCache[id];
                });
                if (stillUnresolved) renderDiscovery();
            }, 8000);
        } else {
            renderDiscovery();
        }
    } catch (_) {}
}

function renderDiscovery() {
    const label = document.getElementById('dashDiscoveryLabel');
    const grid  = document.getElementById('dashDiscoveryGrid');
    if (!label || !grid) return;

    if (!discoveryWorlds.length) {
        label.style.display = 'none';
        grid.style.display  = 'none';
        return;
    }

    label.style.display = '';
    grid.style.display  = '';

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
            return `<span class="disc-tag" style="background:${col.bg};color:${col.tx}">${esc(t)}</span>`;
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
