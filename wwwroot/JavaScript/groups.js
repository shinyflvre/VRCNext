/* === My Groups === */
function loadMyGroups() {
    sendToCS({ action: 'vrcGetMyGroups' });
}

function renderMyGroups(list) {
    myGroups = list || [];
    myGroupsLoaded = true;
    const el = document.getElementById('myGroupsGrid');
    const label = document.getElementById('myGroupsLabel');
    if (myGroups.length === 0) { el.innerHTML = ''; label.style.display = 'none'; return; }
    label.style.display = '';
    el.innerHTML = myGroups.map(g => `<div class="s-card" onclick="openGroupDetail('${esc(g.id)}')">
        <div class="s-card-img" style="background-image:url('${g.bannerUrl||g.iconUrl||''}')"><div class="s-card-icon" style="background-image:url('${g.iconUrl||''}')"></div></div>
        <div class="s-card-body"><div class="s-card-title">${esc(g.name)}</div><div class="s-card-sub">${esc(g.shortCode)} · <span class="msi" style="font-size:11px;">group</span> ${g.memberCount}</div></div></div>`).join('');
}

function openGroupDetail(groupId) {
    const el = document.getElementById('detailModalContent');
    el.innerHTML = sk('detail');
    document.getElementById('modalDetail').style.display = 'flex';
    sendToCS({ action: 'vrcGetGroup', groupId });
}

function renderGroupDetail(g) {
    const el = document.getElementById('detailModalContent');
    const banner = g.bannerUrl || g.iconUrl || '';
    const bannerHtml = banner ? `<div class="fd-banner"><img src="${banner}" onerror="this.parentElement.style.display='none'"><div class="fd-banner-fade"></div></div>` : '';

    // Header
    const iconHtml = g.iconUrl ? `<img class="fd-avatar" src="${g.iconUrl}" onerror="this.style.display='none'">` : '';
    const headerHtml = `<div class="fd-content${banner ? ' fd-has-banner' : ''}"><div class="fd-header">${iconHtml}<div><div class="fd-name">${esc(g.name)}</div><div class="fd-status">${esc(g.shortCode)} · ${g.memberCount} members · ${esc(g.privacy)}</div></div></div>`;

    // Actions - moved to bottom bar
    const leaveJoinBtn = g.isJoined
        ? `<button class="fd-btn fd-btn-danger" onclick="sendToCS({action:'vrcLeaveGroup',groupId:'${esc(g.id)}'});document.getElementById('modalDetail').style.display='none';"><span class="msi" style="font-size:16px;">logout</span>Leave Group</button>`
        : `<button class="fd-btn fd-btn-join" onclick="sendToCS({action:'vrcJoinGroup',groupId:'${esc(g.id)}'});document.getElementById('modalDetail').style.display='none';"><span class="msi" style="font-size:16px;">group_add</span>Join Group</button>`;

    // Tab: Info
    const descHtml = g.description ? `<div class="fd-section-label">Description</div><div class="fd-bio">${esc(g.description)}</div>` : '';
    const rulesHtml = g.rules ? `<div class="fd-section-label">Rules</div><div style="font-size:11px;color:var(--tx3);margin-bottom:10px;padding:8px;background:var(--bg-input);border-radius:8px;max-height:120px;overflow-y:auto;">${esc(g.rules)}</div>` : '';
    const infoTab = `${descHtml}${rulesHtml}`;

    // Tab: Posts
    const posts = g.posts || [];
    let postsTab = '';
    if (posts.length === 0) {
        postsTab = '<div style="padding:20px;text-align:center;font-size:12px;color:var(--tx3);">No posts</div>';
    } else {
        posts.forEach((p, i) => {
            const date = p.createdAt ? new Date(p.createdAt).toLocaleDateString() : '';
            const imgHtml = p.imageUrl ? `<img src="${p.imageUrl}" style="width:100%;border-radius:6px;margin-top:8px;" onerror="this.style.display='none'">` : '';
            const fullText = p.text || '';
            const isLong = fullText.length > 120;
            const preview = isLong ? fullText.slice(0, 120) + '...' : fullText;
            postsTab += `<div class="fd-group-card" style="display:block;cursor:default;padding:12px;">
                <div style="font-size:13px;font-weight:600;color:var(--tx0);">${esc(p.title || 'Untitled')}</div>
                <div style="font-size:10px;color:var(--tx3);margin:2px 0 6px;">${date}${p.visibility ? ' · ' + esc(p.visibility) : ''}</div>
                <div class="gd-post-text" id="gpost${i}" data-full="${esc(fullText).replace(/"/g,'&quot;')}" data-preview="${esc(preview).replace(/"/g,'&quot;')}" style="font-size:12px;color:var(--tx2);line-height:1.4;">${esc(preview)}</div>
                ${isLong ? `<div style="margin-top:4px;"><span class="gd-expand" onclick="toggleGPost(${i})">Show more</span></div>` : ''}
                ${imgHtml}
            </div>`;
        });
    }

    // Tab: Events
    const events = g.groupEvents || [];
    let eventsTab = '';
    if (events.length === 0) {
        eventsTab = '<div style="padding:20px;text-align:center;font-size:12px;color:var(--tx3);">No events</div>';
    } else {
        events.forEach(e => {
            const start = e.startDate ? new Date(e.startDate).toLocaleString() : '';
            const end = e.endDate ? new Date(e.endDate).toLocaleString() : '';
            eventsTab += `<div class="fd-group-card" style="display:block;cursor:default;padding:12px;">
                <div style="font-size:13px;font-weight:600;color:var(--tx0);">${esc(e.title || 'Untitled Event')}</div>
                <div style="font-size:10px;color:var(--tx3);margin:2px 0 4px;">${start}${end ? ' → ' + end : ''}</div>
                ${e.description ? `<div style="font-size:12px;color:var(--tx2);line-height:1.4;">${esc(e.description)}</div>` : ''}
                ${e.location ? `<div style="font-size:11px;color:var(--tx3);margin-top:4px;"><span class="msi" style="font-size:12px;vertical-align:middle;">location_on</span> ${esc(e.location)}</div>` : ''}
            </div>`;
        });
    }

    // Tab: Instances
    const instances = g.groupInstances || [];
    let instancesTab = '';
    if (instances.length === 0) {
        instancesTab = '<div style="padding:20px;text-align:center;font-size:12px;color:var(--tx3);">No active instances</div>';
    } else {
        instances.forEach(inst => {
            const thumbHtml = inst.worldThumb ? `<img style="width:48px;height:48px;border-radius:8px;object-fit:cover;flex-shrink:0;" src="${inst.worldThumb}" onerror="this.style.display='none'">` : '';
            const users = inst.userCount > 0 ? (inst.capacity > 0 ? `${inst.userCount}/${inst.capacity}` : inst.userCount + ' users') : '';
            const loc = (inst.location || '').replace(/'/g, "\\'");
            instancesTab += `<div class="fd-group-card" onclick="sendToCS({action:'vrcJoinFriend',location:'${loc}'})">
                ${thumbHtml}<div class="fd-group-card-info"><div class="fd-group-card-name">${esc(inst.worldName || 'Unknown World')}</div><div class="fd-group-card-meta">${users}</div></div>
                <button class="fd-btn fd-btn-join" style="padding:4px 10px;font-size:11px;" onclick="event.stopPropagation();sendToCS({action:'vrcJoinFriend',location:'${loc}'})"><span class="msi" style="font-size:14px;">login</span>Join</button>
            </div>`;
        });
    }

    // Tab: Gallery
    const gallery = g.galleryImages || [];
    let galleryTab = '';
    if (gallery.length === 0) {
        galleryTab = '<div style="padding:20px;text-align:center;font-size:12px;color:var(--tx3);">No gallery images</div>';
    } else {
        galleryTab = '<div class="gd-gallery-grid">';
        gallery.forEach(img => {
            if (img.imageUrl) galleryTab += `<img class="gd-gallery-img" src="${img.imageUrl}" onclick="window.open('${img.imageUrl}','_blank')" onerror="this.style.display='none'">`;
        });
        galleryTab += '</div>';
    }

    // Tab: Members (paginated)
    const members = g.groupMembers || [];
    let membersTab = '';
    if (members.length === 0) {
        membersTab = '<div style="padding:20px;text-align:center;font-size:12px;color:var(--tx3);">No members</div>';
    } else {
        membersTab = '<div id="gdMembersList">';
        members.forEach(m => {
            membersTab += renderGroupMemberCard(m);
        });
        membersTab += '</div>';
        if (members.length >= 50) {
            membersTab += `<div id="gdMembersLoadMore" style="text-align:center;padding:12px;"><button class="btn-f" onclick="loadMoreGroupMembers()">Load More Members</button></div>`;
        }
    }
    // Store group id + offset for pagination
    window._gdMembersGroupId = g.id;
    window._gdMembersOffset = members.length;

    // Tabs
    const tabs = [
        { key: 'info', label: 'Info' },
        { key: 'posts', label: 'Posts' },
        { key: 'events', label: 'Events' },
        { key: 'instances', label: 'Live' },
        { key: 'gallery', label: 'Gallery' },
        { key: 'members', label: 'Members' },
    ];
    const tabsHtml = `<div class="fd-tabs gd-tabs">${tabs.map((t,i) => `<button class="fd-tab${i===0?' active':''}" onclick="switchGdTab('${t.key}',this)">${t.label}</button>`).join('')}</div>`;

    el.innerHTML = `${bannerHtml}${headerHtml}${tabsHtml}
        <div id="gdTabInfo">${infoTab}</div>
        <div id="gdTabPosts" style="display:none;">${postsTab}</div>
        <div id="gdTabEvents" style="display:none;">${eventsTab}</div>
        <div id="gdTabInstances" style="display:none;">${instancesTab}</div>
        <div id="gdTabGallery" style="display:none;">${galleryTab}</div>
        <div id="gdTabMembers" style="display:none;">${membersTab}</div>
        <div style="margin-top:10px;display:flex;justify-content:space-between;align-items:center;">${leaveJoinBtn}<button class="modal-btn modal-btn-cancel" onclick="document.getElementById('modalDetail').style.display='none'">Close</button></div>
    </div>`;
}

function renderGroupMemberCard(m) {
    const mImg = m.image ? `<img class="vrc-friend-avatar" src="${m.image}" onerror="this.style.display='none'">` : `<div class="vrc-friend-avatar" style="display:flex;align-items:center;justify-content:center;font-size:12px;font-weight:700;color:var(--tx3)">${esc((m.displayName || '?')[0])}</div>`;
    const mId = (m.id || '').replace(/'/g, "\\'");
    const joined = m.joinedAt ? new Date(m.joinedAt).toLocaleDateString() : '';
    return `<div class="vrc-friend-card" onclick="document.getElementById('modalDetail').style.display='none';openFriendDetail('${mId}')">${mImg}<div class="vrc-friend-info"><div class="vrc-friend-name">${esc(m.displayName)}</div><div class="vrc-friend-loc">${joined ? 'Joined ' + joined : ''}</div></div></div>`;
}

function loadMoreGroupMembers() {
    if (!window._gdMembersGroupId) return;
    const btn = document.querySelector('#gdMembersLoadMore button');
    if (btn) { btn.textContent = 'Loading...'; btn.disabled = true; }
    sendToCS({ action: 'vrcGetGroupMembers', groupId: window._gdMembersGroupId, offset: window._gdMembersOffset || 0 });
}

function switchGdTab(tab, btn) {
    ['Info','Posts','Events','Instances','Gallery','Members'].forEach(t => {
        const el = document.getElementById('gdTab' + t);
        if (el) el.style.display = t.toLowerCase() === tab ? '' : 'none';
    });
    btn.closest('.fd-tabs').querySelectorAll('.fd-tab').forEach(t => t.classList.remove('active'));
    btn.classList.add('active');
}

function toggleGPost(i) {
    const el = document.getElementById('gpost' + i);
    const link = el?.parentElement?.querySelector('.gd-expand');
    if (!el || !link) return;
    if (link.textContent === 'Show more') {
        el.textContent = el.dataset.full;
        link.textContent = 'Show less';
    } else {
        el.textContent = el.dataset.preview;
        link.textContent = 'Show more';
    }
}
