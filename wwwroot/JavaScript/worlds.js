/* === Search (Worlds, Groups, People) === */
/* === World Tab: Favorites / Search filter === */
function setWorldFilter(filter) {
    worldFilter = filter;
    document.getElementById('worldFilterFav').classList.toggle('active', filter === 'favorites');
    document.getElementById('worldFilterSearch').classList.toggle('active', filter === 'search');
    document.getElementById('worldSearchArea').style.display = filter === 'search' ? '' : 'none';
    document.getElementById('worldFavArea').style.display = filter === 'favorites' ? '' : 'none';
    if (filter === 'favorites' && favWorldsData.length === 0) {
        sendToCS({ action: 'vrcGetFavoriteWorlds' });
    }
}

function renderFavWorlds(list) {
    favWorldsData = list || [];
    // Populate world info cache for library badges
    favWorldsData.forEach(w => {
        if (w.id) worldInfoCache[w.id] = { id: w.id, name: w.name, thumbnailImageUrl: w.thumbnailImageUrl || w.imageUrl };
    });
    filterFavWorlds();
}

/* === Shared world card renderer (search + favorites) === */
function renderWorldCard(w) {
    const thumb = w.thumbnailImageUrl || w.imageUrl || '';
    const desc = w.description ? w.description.substring(0, 100) + (w.description.length > 100 ? '...' : '') : '';
    const tags = (w.tags || []).filter(t => t.startsWith('author_tag_')).map(t => t.replace('author_tag_','')).slice(0,4);
    const tagsHtml = tags.length ? `<div class="s-card-tags">${tags.map(t => `<span class="s-tag">${esc(t)}</span>`).join('')}</div>` : '';
    const wid = jsq(w.id);
    const ts = w.worldTimeSeconds || 0;
    const timeBadge = ts > 0 ? `<div class="s-card-time-badge"><span class="msi" style="font-size:11px;">schedule</span> ${formatDuration(ts)}</div>` : '';
    return `<div class="s-card" onclick="openWorldSearchDetail('${wid}')">
        <div class="s-card-img" style="background-image:url('${thumb}')">${timeBadge}</div>
        <div class="s-card-body"><div class="s-card-title">${esc(w.name)}</div>
        <div class="s-card-sub">${esc(w.authorName)} · <span class="msi" style="font-size:11px;">person</span> ${w.occupants} · <span class="msi" style="font-size:11px;">star</span> ${w.favorites}</div>
        ${desc ? `<div class="s-card-desc">${esc(desc)}</div>` : ''}
        ${tagsHtml}</div></div>`;
}

function filterFavWorlds() {
    const q = (document.getElementById('favWorldSearchInput')?.value || '').toLowerCase();
    const filtered = q ? favWorldsData.filter(w => (w.name||'').toLowerCase().includes(q) || (w.authorName||'').toLowerCase().includes(q)) : favWorldsData;
    const el = document.getElementById('favWorldsGrid');
    if (!filtered.length) {
        el.innerHTML = `<div class="empty-msg">${q ? 'No favorites match your search' : 'No favorite worlds found'}</div>`;
        return;
    }
    el.innerHTML = filtered.map(w => renderWorldCard(w)).join('');
}

/* === Detail Modals (shared) === */
function openWorldSearchDetail(id) {
    const el = document.getElementById('detailModalContent');
    el.innerHTML = sk('detail');
    document.getElementById('modalDetail').style.display = 'flex';
    sendToCS({ action: 'vrcGetWorldDetail', worldId: id });
}

function renderWorldSearchDetail(w) {
    const el = document.getElementById('detailModalContent');
    const thumb = w.thumbnailImageUrl || w.imageUrl || '';
    const desc = w.description || '';
    const authorTags = (w.tags || []).filter(t => t.startsWith('author_tag_')).map(t => t.replace('author_tag_', ''));
    const systemTags = (w.tags || []).filter(t => !t.startsWith('author_tag_') && !t.startsWith('system_') && !t.startsWith('admin_'));

    // Tags HTML
    let tagsHtml = '';
    if (authorTags.length || systemTags.length) {
        const allTags = [...authorTags, ...systemTags].slice(0, 12);
        tagsHtml = `<div style="display:flex;flex-wrap:wrap;gap:6px;margin-bottom:14px;">${allTags.map(t => `<span class="s-tag">${esc(t)}</span>`).join('')}</div>`;
    }

    // Active instances HTML
    let instancesHtml = '';
    if (w.instances && w.instances.length > 0) {
        const regionLabels = { us: 'US West', use: 'US East', eu: 'Europe', jp: 'Japan' };
        instancesHtml = `<div class="wd-section-label" style="margin-top:4px;">ACTIVE INSTANCES (${w.instances.length})</div><div class="wd-instances-list">`;
        w.instances.forEach(inst => {
            const { cls: tClass, label: tLabel } = getInstanceBadge(inst.type);
            const rLabel = regionLabels[inst.region] || inst.region.toUpperCase();
            const loc = (inst.location || '').replace(/'/g, "\\'");
            instancesHtml += `<div class="wd-instance-row">
                <div class="wd-instance-info">
                    <span class="fd-instance-badge ${tClass}" style="font-size:10px;">${tLabel}</span>
                    <span style="font-size:11px;color:var(--tx2);">${rLabel}</span>
                    <span style="font-size:11px;color:var(--tx3);display:inline-flex;align-items:center;gap:2px;"><span class="msi" style="font-size:12px;">person</span> ${inst.users}${w.capacity ? '/' + w.capacity : ''}</span>
                </div>
                <button class="btn-f" onclick="sendToCS({action:'vrcJoinFriend',location:'${loc}'});this.disabled=true;this.textContent='Joining...';" style="padding:3px 10px;font-size:10px;"><span class="msi" style="font-size:14px;">login</span> Join</button>
            </div>`;
        });
        instancesHtml += '</div>';
    } else {
        instancesHtml = '<div style="font-size:11px;color:var(--tx3);margin-bottom:14px;">No active public instances</div>';
    }

    // Create instance UI
    const createHtml = `<div class="wd-section-label" style="margin-top:6px;">CREATE INSTANCE</div>
        <div class="wd-create-row">
            <select id="ciType" class="wd-create-select">
                <option value="public">Public</option>
                <option value="friends">Friends</option>
                <option value="hidden">Friends+</option>
                <option value="private">Invite</option>
            </select>
            <select id="ciRegion" class="wd-create-select">
                <option value="eu">Europe</option>
                <option value="us">US West</option>
                <option value="use">US East</option>
                <option value="jp">Japan</option>
            </select>
            <button class="btn-p" id="ciBtn" onclick="createWorldInstance('${esc(w.id)}')" style="padding:6px 14px;font-size:11px;"><span class="msi" style="font-size:14px;">add</span> Create & Join</button>
        </div>`;

    el.innerHTML = `${thumb ? `<div class="fd-banner"><img src="${thumb}" onerror="this.parentElement.style.display='none'"><div class="fd-banner-fade"></div></div>` : ''}
        <div class="fd-content${thumb ? ' fd-has-banner' : ''}" style="padding:20px;">
        <h2 style="margin:0 0 4px;color:var(--tx0);font-size:18px;">${esc(w.name)}</h2>
        <div style="font-size:12px;color:var(--tx3);margin-bottom:12px;">by ${esc(w.authorName)}</div>
        <div class="fd-badges-row">
            <span class="fd-badge"><span class="msi" style="font-size:11px;">person</span> ${w.occupants} Active</span>
            <span class="fd-badge"><span class="msi" style="font-size:11px;">star</span> ${w.favorites}</span>
            <span class="fd-badge"><span class="msi" style="font-size:11px;">visibility</span> ${w.visits}</span>
        </div>
        ${w.worldTimeSeconds > 0 ? `<div class="wd-your-time"><span class="msi" style="font-size:15px;">schedule</span><div><div style="font-size:12px;font-weight:600;color:var(--tx1);">Your Time Spent</div><div style="font-size:11px;color:var(--tx3);">${formatDuration(w.worldTimeSeconds)}${w.worldVisitCount > 0 ? ' · ' + w.worldVisitCount + ' visit' + (w.worldVisitCount > 1 ? 's' : '') : ''}</div></div></div>` : ''}
        ${desc ? `<div style="font-size:12px;color:var(--tx2);margin-bottom:14px;max-height:150px;overflow-y:auto;line-height:1.5;white-space:pre-wrap;">${esc(desc)}</div>` : ''}
        ${tagsHtml}
        <div class="fd-meta" style="margin-bottom:14px;">
            ${w.recommendedCapacity ? `<div class="fd-meta-row"><span class="fd-meta-label">Recommended</span><span>${w.recommendedCapacity} Players</span></div>` : ''}
            <div class="fd-meta-row"><span class="fd-meta-label">Max Capacity</span><span>${w.capacity} Players</span></div>
        </div>
        ${instancesHtml}
        ${createHtml}
        <div style="margin-top:14px;text-align:right;"><button class="modal-btn modal-btn-cancel" onclick="document.getElementById('modalDetail').style.display='none'">Close</button></div>
        </div>`;
}

function createInstance(worldId) {
    const type = document.getElementById('ciType').value;
    const region = document.getElementById('ciRegion').value;
    const btn = document.getElementById('ciBtn');
    if (btn) { btn.disabled = true; btn.innerHTML = '<span class="msi" style="font-size:14px;">hourglass_empty</span> Creating...'; }
    sendToCS({ action: 'vrcCreateInstance', worldId, type, region });
}

function createWorldInstance(worldId) {
    const type = document.getElementById('ciType').value;
    const region = document.getElementById('ciRegion').value;
    const btn = document.getElementById('ciBtn');
    if (btn) { btn.disabled = true; btn.innerHTML = '<span class="msi" style="font-size:14px;">hourglass_empty</span> Creating...'; }
    sendToCS({ action: 'vrcCreateInstance', worldId, type, region });
}

/* === World Detail Modal === */
function openWorldDetail(worldId) {
    if (!worldId) return;
    const m = document.getElementById('modalWorldDetail');
    const c = document.getElementById('worldDetailContent');

    // Find all friends in this world
    const friends = vrcFriendsData.filter(f => {
        const { worldId: wid } = parseFriendLocation(f.location);
        return wid === worldId;
    });

    const cached = dashWorldCache[worldId];
    const worldName = cached?.name || worldId;
    const thumb = cached?.thumbnailImageUrl || cached?.imageUrl || '';

    // Find a location string from any friend in this world (for join)
    const anyLoc = friends.length > 0 ? friends[0].location : '';

    // Detect instance type from location
    let instanceType = 'public';
    if (anyLoc) {
        const parsed = parseFriendLocation(anyLoc);
        instanceType = parsed.instanceType;
    }
    const { cls: instClass, label: instLabel } = getInstanceBadge(instanceType);

    // Build header with banner fade (matching profiles/groups)
    const bannerHtml = thumb
        ? `<div class="fd-banner"><img src="${thumb}" onerror="this.parentElement.style.display='none'"><div class="fd-banner-fade"></div></div>`
        : '';

    // Build friends list
    let friendsHtml = '<div class="wd-friends-label">FRIENDS IN THIS WORLD</div><div class="wd-friends-list">';
    friends.forEach(f => {
        const img = f.image || '';
        const imgTag = img
            ? `<img class="wd-friend-avatar" src="${img}" onerror="this.style.display='none'">`
            : `<div class="wd-friend-avatar" style="display:flex;align-items:center;justify-content:center;font-size:11px;font-weight:700;color:var(--tx3)">${esc((f.displayName||'?')[0])}</div>`;
        const fid = (f.id || '').replace(/'/g, "\\'");
        friendsHtml += `<div class="wd-friend-row" onclick="closeWorldDetail();openFriendDetail('${fid}')">
            ${imgTag}
            <div class="wd-friend-info">
                <div class="wd-friend-name"><span class="vrc-status-dot ${statusDotClass(f.status)}" style="width:7px;height:7px;"></span>${esc(f.displayName)}</div>
                <div class="wd-friend-status">${esc(f.statusDescription || statusLabel(f.status))}</div>
            </div>
        </div>`;
    });
    friendsHtml += '</div>';

    // Actions
    const loc = anyLoc.replace(/'/g, "\\'");
    const canJoin = anyLoc && instanceType !== 'private';
    let actionsHtml = '<div class="fd-actions">';
    if (canJoin) actionsHtml += `<button class="modal-btn modal-btn-cancel" onclick="worldJoinAction('${loc}')">Join World</button>`;
    actionsHtml += `<button class="modal-btn modal-btn-cancel" onclick="closeWorldDetail()">Close</button>`;
    actionsHtml += '</div>';

    c.innerHTML = `${bannerHtml}<div class="fd-content${thumb ? ' fd-has-banner' : ''}" style="padding:16px;">
        <h2 style="margin:0 0 4px;color:var(--tx0);font-size:18px;">${esc(worldName)}</h2>
        <div class="fd-badges-row"><span class="fd-instance-badge ${instClass}">${instLabel}</span></div>
        ${friendsHtml}${actionsHtml}</div>`;
    m.style.display = 'flex';
}

function closeWorldDetail() {
    document.getElementById('modalWorldDetail').style.display = 'none';
}

function worldJoinAction(location) {
    const btns = document.querySelectorAll('#worldDetailContent button');
    btns.forEach(b => b.disabled = true);
    sendToCS({ action: 'vrcJoinFriend', location: location });
}
