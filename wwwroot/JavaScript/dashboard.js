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
                location: f.location
            };
        }
        worlds[worldId].friends.push(f);
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
                    <div class="dash-world-meta"><span class="dash-world-count">${w.friends.length} friend${w.friends.length !== 1 ? 's' : ''} here</span></div>
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
        const thumbStyle = w.thumb ? `background-image:url('${w.thumb}')` : '';
        const wid = (w.worldId || '').replace(/'/g, "\\'");
        const displayName = w.name || w.worldId;
        const { cls: dwInstCls, label: dwInstLabel } = getInstanceBadge(w.instanceType);
        return `<div class="dash-world-card" onclick="openWorldDetail('${wid}')">
            <div class="dash-world-thumb" style="${thumbStyle}"><div class="dash-world-thumb-overlay"></div></div>
            <div class="dash-world-info">
                <div class="dash-world-name">${esc(displayName)}</div>
                <div class="dash-world-friends-row">${friendAvatars}${extra}</div>
                <div class="dash-world-meta"><span class="dash-world-count">${w.friends.length} friend${w.friends.length !== 1 ? 's' : ''} here</span><span class="fd-instance-badge ${dwInstCls}" style="font-size:9px;margin-left:auto;">${dwInstLabel}</span></div>
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
