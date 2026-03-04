/* === My Groups === */
function loadMyGroups() {
    sendToCS({ action: 'vrcGetMyGroups' });
}

function refreshGroups() {
    const btn = document.getElementById('groupsRefreshBtn');
    if (btn) { btn.disabled = true; btn.querySelector('.msi').textContent = 'hourglass_empty'; }
    sendToCS({ action: 'vrcGetMyGroups' });
}

function renderMyGroups(list) {
    const btn = document.getElementById('groupsRefreshBtn');
    if (btn) { btn.disabled = false; btn.querySelector('.msi').textContent = 'refresh'; }
    myGroups = list || [];
    myGroupsLoaded = true;
    const el = document.getElementById('myGroupsGrid');
    const label = document.getElementById('myGroupsLabel');
    if (myGroups.length === 0) { el.innerHTML = ''; label.style.display = 'none'; return; }
    label.style.display = '';
    el.innerHTML = myGroups.map(g => `<div class="s-card" onclick="openGroupDetail('${esc(g.id)}')">
        <div class="s-card-img" style="background-image:url('${cssUrl(g.bannerUrl||g.iconUrl||'')}')"><div class="s-card-icon" style="background-image:url('${cssUrl(g.iconUrl||'')}')"></div></div>
        <div class="s-card-body"><div class="s-card-title">${esc(g.name)}</div><div class="s-card-sub">${esc(g.shortCode)} · <span class="msi" style="font-size:11px;">group</span> ${g.memberCount}</div></div></div>`).join('');
}

function openGroupDetail(groupId) {
    const el = document.getElementById('detailModalContent');
    el.innerHTML = sk('detail');
    document.getElementById('modalDetail').style.display = 'flex';
    sendToCS({ action: 'vrcGetGroup', groupId });
}

function renderGroupDetail(g) {
    window._currentGroupDetail = { id: g.id, canKick: g.canKick === true, canBan: g.canBan === true, languages: g.languages || [], links: g.links || [] };
    const el = document.getElementById('detailModalContent');
    const banner = g.bannerUrl || g.iconUrl || '';
    const bannerHtml = banner ? `<div class="fd-banner"><img src="${banner}" onerror="this.parentElement.style.display='none'"><div class="fd-banner-fade"></div></div>` : '';

    // Header
    const iconHtml = g.iconUrl ? `<img class="fd-avatar" src="${g.iconUrl}" onerror="this.style.display='none'">` : '';
    const headerHtml = `<div class="fd-content${banner ? ' fd-has-banner' : ''}"><div class="fd-header">${iconHtml}<div><div class="fd-name">${esc(g.name)}</div><div class="fd-status">${esc(g.shortCode)} · ${g.memberCount} members · ${esc(g.privacy)}</div></div></div>`;

    // Actions - moved to bottom bar
    const canPost  = g.canPost === true;
    const canEvent = g.canEvent === true;
    const createPostBtn = (g.isJoined && canPost)
        ? `<button class="fd-btn fd-btn-join" onclick="openGroupPostModal('${esc(g.id)}')"><span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">edit</span>Post</button>`
        : '';
    const createEventBtn = (g.isJoined && canEvent)
        ? `<button class="fd-btn fd-btn-join" onclick="openGroupEventModal('${esc(g.id)}')"><span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">event</span>Events</button>`
        : '';
    const leaveJoinBtn = g.isJoined
        ? `<button class="fd-btn fd-btn-danger" onclick="sendToCS({action:'vrcLeaveGroup',groupId:'${esc(g.id)}'});document.getElementById('modalDetail').style.display='none';"><span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">logout</span>Leave Group</button>`
        : `<button class="fd-btn fd-btn-join" onclick="sendToCS({action:'vrcJoinGroup',groupId:'${esc(g.id)}'});document.getElementById('modalDetail').style.display='none';"><span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">group_add</span>Join Group</button>`;

    // Tab: Info
    const canEdit = g.canEdit === true;
    const gid_e = esc(g.id);
    const grpLangs = (g.languages || []);
    const grpLinks = (g.links || []).filter(Boolean);
    const grpLangsViewHtml = grpLangs.length
        ? `<div class="fd-lang-tags">${grpLangs.map(l => `<span class="fd-lang-tag">${esc(LANG_MAP['language_'+l] || l.toUpperCase())}</span>`).join('')}</div>`
        : `<div class="myp-empty">No languages set</div>`;
    const grpLinksViewHtml = grpLinks.length
        ? `<div class="fd-bio-links">${grpLinks.map(url => renderBioLink(url)).join('')}</div>`
        : `<div class="myp-empty">No links added</div>`;
    const infoTab = `
        <div class="myp-section">
            <div class="myp-section-header">
                <span class="myp-section-title">Description</span>
                ${canEdit ? `<button class="myp-edit-btn" onclick="editGroupField('desc')"><span class="msi" style="font-size:14px;">edit</span></button>` : ''}
            </div>
            <div id="gdescDescView">
                ${g.description ? `<div class="fd-bio">${esc(g.description)}</div>` : '<div class="myp-empty">No description</div>'}
            </div>
            ${canEdit ? `<div id="gdescDescEdit" style="display:none;">
                <textarea id="gdescDescInput" class="myp-textarea" rows="4" maxlength="2000" placeholder="Group description...">${esc(g.description||'')}</textarea>
                <div class="myp-edit-actions">
                    <button class="myp-cancel-btn" onclick="cancelGroupField('desc')">Cancel</button>
                    <button class="myp-save-btn" onclick="saveGroupField('desc','${gid_e}')">Save</button>
                </div>
            </div>` : ''}
        </div>
        <div class="myp-section">
            <div class="myp-section-header">
                <span class="myp-section-title">Links</span>
                ${canEdit ? `<button class="myp-edit-btn" onclick="editGroupField('links')"><span class="msi" style="font-size:14px;">edit</span></button>` : ''}
            </div>
            <div id="ggrpLinksView">${grpLinksViewHtml}</div>
            ${canEdit ? `<div id="ggrpLinksEdit" style="display:none;">
                <div id="ggrpLinksInputs"></div>
                <div class="myp-edit-actions">
                    <button class="myp-cancel-btn" onclick="cancelGroupField('links')">Cancel</button>
                    <button class="myp-save-btn" onclick="saveGroupField('links','${gid_e}')">Save</button>
                </div>
            </div>` : ''}
        </div>
        <div class="myp-section">
            <div class="myp-section-header">
                <span class="myp-section-title">Languages</span>
                ${canEdit ? `<button class="myp-edit-btn" onclick="editGroupField('langs')"><span class="msi" style="font-size:14px;">edit</span></button>` : ''}
            </div>
            <div id="ggrpLangsView">${grpLangsViewHtml}</div>
            ${canEdit ? `<div id="ggrpLangsEdit" style="display:none;">
                <div id="ggrpLangsChips" class="myp-lang-chips"></div>
                <div class="myp-lang-add-row">
                    <select id="ggrpLangSelect" class="myp-lang-select"><option value="">Add language...</option></select>
                    <button class="myp-add-lang-btn" onclick="addGrpLanguage()"><span class="msi" style="font-size:15px;">add</span></button>
                </div>
                <div class="myp-edit-actions">
                    <button class="myp-cancel-btn" onclick="cancelGroupField('langs')">Cancel</button>
                    <button class="myp-save-btn" onclick="saveGroupField('langs','${gid_e}')">Save</button>
                </div>
            </div>` : ''}
        </div>
        <div class="myp-section">
            <div class="myp-section-header">
                <span class="myp-section-title">Rules</span>
                ${canEdit ? `<button class="myp-edit-btn" onclick="editGroupField('rules')"><span class="msi" style="font-size:14px;">edit</span></button>` : ''}
            </div>
            <div id="gdescRulesView">
                ${g.rules ? `<div style="font-size:11px;color:var(--tx3);padding:8px;background:var(--bg-input);border-radius:8px;max-height:120px;overflow-y:auto;white-space:pre-wrap;">${esc(g.rules)}</div>` : '<div class="myp-empty">No rules set</div>'}
            </div>
            ${canEdit ? `<div id="gdescRulesEdit" style="display:none;">
                <textarea id="gdescRulesInput" class="myp-textarea" rows="5" maxlength="2000" placeholder="Group rules...">${esc(g.rules||'')}</textarea>
                <div class="myp-edit-actions">
                    <button class="myp-cancel-btn" onclick="cancelGroupField('rules')">Cancel</button>
                    <button class="myp-save-btn" onclick="saveGroupField('rules','${gid_e}')">Save</button>
                </div>
            </div>` : ''}
        </div>`;

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
            const pid = esc(p.id || ''), gid = esc(g.id || '');
            const delBtn = (canPost && p.id)
                ? `<button class="gd-post-del" onclick="deleteGroupPost('${gid}','${pid}',this)" title="Delete post"><span class="msi">delete</span></button>`
                : '';
            postsTab += `<div class="fd-group-card" data-post-id="${pid}" style="display:block;cursor:default;padding:12px;">
                <div style="display:flex;align-items:center;gap:6px;margin-bottom:2px;">
                    <div style="font-size:13px;font-weight:600;color:var(--tx0);flex:1;">${esc(p.title || 'Untitled')}</div>
                    ${delBtn}
                </div>
                <div style="font-size:10px;color:var(--tx3);margin-bottom:6px;">${date}${p.visibility ? ' · ' + esc(p.visibility) : ''}</div>
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
            const startD = e.startsAt ? new Date(e.startsAt) : null;
            const endD   = e.endsAt   ? new Date(e.endsAt)   : null;
            const timeStr = startD && !isNaN(startD)
                ? startD.toLocaleTimeString(undefined, { hour:'2-digit', minute:'2-digit' }) +
                  (endD && !isNaN(endD) ? ' – ' + endD.toLocaleTimeString(undefined, { hour:'2-digit', minute:'2-digit' }) : '')
                : '';
            const dateStr = startD && !isNaN(startD)
                ? startD.toLocaleDateString(undefined, { weekday:'long', month:'long', day:'numeric' })
                : '';
            const imgHtml = e.imageUrl ? `<img src="${e.imageUrl}" style="width:100%;max-height:120px;object-fit:cover;border-radius:6px;margin-bottom:8px;" onerror="this.style.display='none'">` : '';
            const badge = e.accessType ? `<span style="font-size:9px;padding:1px 6px;border-radius:4px;background:color-mix(in srgb,var(--accent) 12%,transparent);color:var(--accent-lt);border:1px solid color-mix(in srgb,var(--accent) 35%,transparent);margin-left:6px;">${esc(e.accessType)}</span>` : '';
            const gid = esc(e.ownerId || g.id || '');
            const cid = esc(e.id || '');
            const delEvtBtn = (canEvent && e.id)
                ? `<button class="gd-post-del" onclick="event.stopPropagation();deleteGroupEvent('${esc(g.id)}','${cid}',this)" title="Delete event"><span class="msi">delete</span></button>`
                : '';
            eventsTab += `<div class="fd-group-card" data-event-id="${cid}" style="display:block;cursor:pointer;padding:12px;" onclick="openEventDetail('${gid}','${cid}')">
                ${imgHtml}
                <div style="display:flex;align-items:center;justify-content:space-between;gap:6px;">
                    <div style="font-size:13px;font-weight:600;color:var(--tx0);">${esc(e.title || 'Untitled Event')}${badge}</div>
                    ${delEvtBtn}
                </div>
                <div style="font-size:10px;color:var(--tx3);margin:2px 0 4px;">${dateStr}${timeStr ? ' · ' + timeStr : ''}</div>
                ${e.description ? `<div style="font-size:12px;color:var(--tx2);line-height:1.4;">${esc(e.description)}</div>` : ''}
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
            if (img.imageUrl) galleryTab += `<img class="gd-gallery-img" src="${img.imageUrl}" onclick="openLightbox('${jsq(img.imageUrl)}')" onerror="this.style.display='none'">`;
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
        <div style="margin-top:10px;display:flex;justify-content:space-between;align-items:center;"><div style="display:flex;gap:8px;">${createPostBtn}${createEventBtn}${leaveJoinBtn}</div><button class="fd-btn" onclick="document.getElementById('modalDetail').style.display='none'">Close</button></div>
    </div>`;
}

function renderGroupMemberCard(m) {
    return renderProfileItem(m, `closeDetailModal();openFriendDetail('${jsq(m.id || '')}')`);
}

const _grpFieldIds = {
    desc:  { view: 'gdescDescView',  edit: 'gdescDescEdit'  },
    rules: { view: 'gdescRulesView', edit: 'gdescRulesEdit' },
    links: { view: 'ggrpLinksView',  edit: 'ggrpLinksEdit'  },
    langs: { view: 'ggrpLangsView',  edit: 'ggrpLangsEdit'  },
};

function editGroupField(field) {
    Object.keys(_grpFieldIds).forEach(f => {
        if (f === field) return;
        const ids = _grpFieldIds[f];
        const v = document.getElementById(ids.view); if (v) v.style.display = '';
        const e = document.getElementById(ids.edit); if (e) e.style.display = 'none';
    });
    const ids = _grpFieldIds[field];
    if (!ids) return;
    document.getElementById(ids.view).style.display = 'none';
    document.getElementById(ids.edit).style.display = '';
    if (field === 'desc')  document.getElementById('gdescDescInput')?.focus();
    if (field === 'rules') document.getElementById('gdescRulesInput')?.focus();
    if (field === 'links') _renderGrpLinksInputs();
    if (field === 'langs') _renderGrpLangsEdit();
}

function cancelGroupField(field) {
    const ids = _grpFieldIds[field];
    if (!ids) return;
    document.getElementById(ids.view).style.display = '';
    document.getElementById(ids.edit).style.display = 'none';
}

function saveGroupField(field, groupId) {
    const ids = _grpFieldIds[field];
    const saveBtn = document.querySelector(`#${ids.edit} .myp-save-btn`);
    if (saveBtn) saveBtn.disabled = true;

    if (field === 'desc') {
        sendToCS({ action: 'vrcUpdateGroup', groupId, description: document.getElementById('gdescDescInput')?.value ?? '' });
    } else if (field === 'rules') {
        sendToCS({ action: 'vrcUpdateGroup', groupId, rules: document.getElementById('gdescRulesInput')?.value ?? '' });
    } else if (field === 'links') {
        const inputs = document.querySelectorAll('#ggrpLinksInputs .myp-link-input');
        const links = Array.from(inputs).map(i => i.value.trim()).filter(Boolean);
        sendToCS({ action: 'vrcUpdateGroup', groupId, links });
    } else if (field === 'langs') {
        const chips = document.querySelectorAll('#ggrpLangsChips [data-lang]');
        const languages = Array.from(chips).map(c => c.dataset.lang);
        sendToCS({ action: 'vrcUpdateGroup', groupId, languages });
    }
}

function _renderGrpLinksInputs() {
    const container = document.getElementById('ggrpLinksInputs');
    if (!container) return;
    const links = (window._currentGroupDetail?.links || []).filter(Boolean);
    container.innerHTML = [0, 1, 2].map(i =>
        `<div class="myp-link-row">
            <span class="myp-link-num">${i + 1}</span>
            <input type="url" class="myp-link-input" placeholder="https://..." value="${esc(links[i]||'')}" maxlength="512">
        </div>`
    ).join('');
}

function _renderGrpLangsEdit() {
    const selected = (window._currentGroupDetail?.languages || []);
    _renderGrpLangChips(selected, document.getElementById('ggrpLangsChips'));
    const sel = document.getElementById('ggrpLangSelect');
    if (!sel) return;
    sel.innerHTML = '<option value="">Add language...</option>';
    Object.entries(LANG_MAP).forEach(([key, name]) => {
        const code = key.replace('language_', '');
        if (!selected.includes(code))
            sel.insertAdjacentHTML('beforeend', `<option value="${code}">${esc(name)}</option>`);
    });
}

function _renderGrpLangChips(langs, el) {
    if (!el) return;
    el.innerHTML = langs.map(code =>
        `<span class="myp-lang-chip" data-lang="${code}">${esc(LANG_MAP['language_'+code] || code.toUpperCase())}<button class="myp-lang-remove" onclick="removeGrpLanguage('${code}')"><span class="msi" style="font-size:11px;">close</span></button></span>`
    ).join('');
}

function addGrpLanguage() {
    const sel = document.getElementById('ggrpLangSelect');
    const code = sel?.value;
    if (!code) return;
    const chips = Array.from(document.querySelectorAll('#ggrpLangsChips [data-lang]')).map(c => c.dataset.lang);
    if (chips.includes(code)) return;
    chips.push(code);
    _renderGrpLangChips(chips, document.getElementById('ggrpLangsChips'));
    const opt = sel.querySelector(`option[value="${code}"]`);
    if (opt) opt.remove();
    sel.value = '';
}

function removeGrpLanguage(code) {
    const chips = Array.from(document.querySelectorAll('#ggrpLangsChips [data-lang]')).map(c => c.dataset.lang).filter(c => c !== code);
    _renderGrpLangChips(chips, document.getElementById('ggrpLangsChips'));
    const sel = document.getElementById('ggrpLangSelect');
    if (sel) sel.insertAdjacentHTML('beforeend', `<option value="${code}">${esc(LANG_MAP['language_'+code] || code.toUpperCase())}</option>`);
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

function deleteGroupPost(groupId, postId, btn) {
    btn.disabled = true;
    btn.querySelector('.msi').textContent = 'hourglass_empty';
    const card = btn.closest('.fd-group-card');
    if (card) { card.style.opacity = '.4'; card.style.pointerEvents = 'none'; }
    sendToCS({ action: 'vrcDeleteGroupPost', groupId, postId });
}

function deleteGroupEvent(groupId, eventId, btn) {
    btn.disabled = true;
    btn.querySelector('.msi').textContent = 'hourglass_empty';
    const card = btn.closest('.fd-group-card');
    if (card) { card.style.opacity = '.4'; card.style.pointerEvents = 'none'; }
    sendToCS({ action: 'vrcDeleteGroupEvent', groupId, eventId });
}

/* === Group Post Modal === */
let _groupPostGroupId = null;
let _groupPostImageBase64 = null;
let _groupPostSelectedFileId = null; // file_xxx from library picker

function openGroupPostModal(groupId) {
    _groupPostGroupId = groupId;
    _groupPostImageBase64 = null;
    _groupPostSelectedFileId = null;

    let overlay = document.getElementById('groupPostOverlay');
    if (!overlay) {
        overlay = document.createElement('div');
        overlay.id = 'groupPostOverlay';
        overlay.style.cssText = 'position:fixed;inset:0;z-index:10002;background:rgba(0,0,0,.55);display:flex;align-items:center;justify-content:center;';
        document.body.appendChild(overlay);
    }
    overlay.innerHTML = `
    <div class="gp-modal" role="dialog" aria-label="Create Group Post">
        <div class="gp-modal-header">
            <span class="msi" style="font-size:20px;color:var(--accent);">edit</span>
            <span>Create Group Post</span>
            <button class="fd-btn" onclick="closeGroupPostModal()" style="padding:4px 8px;" title="Close"><span class="msi" style="font-size:18px;">close</span></button>
        </div>
        <div class="gp-modal-body">
            <label class="gp-label">Title</label>
            <input id="gpTitle" class="gp-input" type="text" placeholder="Post title..." maxlength="200">
            <label class="gp-label" style="margin-top:12px;">Content</label>
            <textarea id="gpText" class="gp-textarea" placeholder="What's on your mind?" rows="5" maxlength="2000"></textarea>
            <div style="display:flex;gap:12px;margin-top:12px;flex-wrap:wrap;">
                <div style="flex:1;min-width:130px;">
                    <label class="gp-label">Visibility</label>
                    <select id="gpVisibility" class="gp-select">
                        <option value="group">Group only</option>
                        <option value="public">Public</option>
                    </select>
                </div>
                <div style="flex:1;min-width:130px;">
                    <label class="gp-label">Notification</label>
                    <select id="gpNotify" class="gp-select">
                        <option value="0">No notification</option>
                        <option value="1">Send notification</option>
                    </select>
                </div>
            </div>
            <label class="gp-label" style="margin-top:12px;">Image <span style="color:var(--tx3);font-weight:400;">(optional)</span></label>
            <div style="display:flex;gap:6px;margin-bottom:8px;">
                <button class="fd-btn active" id="gpSrcUploadBtn" onclick="gpSetImgSource('upload')" style="flex:1;font-size:11px;"><span class="msi" style="font-size:14px;vertical-align:middle;">upload_file</span> Upload</button>
                <button class="fd-btn" id="gpSrcLibraryBtn" onclick="gpSetImgSource('library')" style="flex:1;font-size:11px;"><span class="msi" style="font-size:14px;vertical-align:middle;">photo_library</span> From Library</button>
            </div>
            <div id="gpUploadArea">
                <div class="gp-img-area" id="gpImgArea" onclick="document.getElementById('gpFileInput').click()">
                    <span class="msi" style="font-size:28px;color:var(--tx3);">image</span>
                    <span id="gpImgLabel" style="font-size:11px;color:var(--tx3);">Click to select image</span>
                    <input id="gpFileInput" type="file" accept="image/png,image/jpeg,image/gif,image/webp" style="display:none;" onchange="onGroupPostImageSelected(event)">
                </div>
                <img id="gpImgPreview" style="display:none;width:100%;max-height:180px;object-fit:contain;border-radius:8px;margin-top:8px;">
            </div>
            <div id="gpLibraryArea" style="display:none;">
                <div id="gpLibraryGrid" style="display:grid;grid-template-columns:repeat(auto-fill,minmax(72px,1fr));gap:6px;max-height:180px;overflow-y:auto;padding:4px 0;">
                    <div style="grid-column:1/-1;text-align:center;padding:20px;font-size:11px;color:var(--tx3);">Loading photos...</div>
                </div>
            </div>
            <div id="gpError" style="display:none;margin-top:8px;padding:8px 10px;background:rgba(255,80,80,.12);border-radius:8px;color:var(--err);font-size:12px;"></div>
        </div>
        <div class="gp-modal-footer">
            <button class="fd-btn" onclick="closeGroupPostModal()">Cancel</button>
            <button class="fd-btn fd-btn-join" id="gpSubmitBtn" onclick="submitGroupPost()"><span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">send</span>Post</button>
        </div>
    </div>`;
    overlay.style.display = 'flex';
    setTimeout(() => document.getElementById('gpTitle')?.focus(), 50);
}

function gpSetImgSource(src) {
    const uploadArea = document.getElementById('gpUploadArea');
    const libraryArea = document.getElementById('gpLibraryArea');
    const uploadBtn = document.getElementById('gpSrcUploadBtn');
    const libraryBtn = document.getElementById('gpSrcLibraryBtn');
    if (!uploadArea || !libraryArea) return;
    if (src === 'library') {
        uploadArea.style.display = 'none';
        libraryArea.style.display = '';
        uploadBtn?.classList.remove('active');
        libraryBtn?.classList.add('active');
        _groupPostImageBase64 = null;
        // Render cached photos or request fresh
        const cached = invFilesCache['gallery'];
        if (cached && cached.length > 0) {
            renderGpLibraryPhotos(cached);
        } else {
            sendToCS({ action: 'invGetFiles', tag: 'gallery' });
        }
    } else {
        uploadArea.style.display = '';
        libraryArea.style.display = 'none';
        uploadBtn?.classList.add('active');
        libraryBtn?.classList.remove('active');
        _groupPostSelectedFileId = null;
    }
}

function renderGpLibraryPhotos(files) {
    const grid = document.getElementById('gpLibraryGrid');
    if (!grid) return;
    if (!files || files.length === 0) {
        grid.innerHTML = '<div style="grid-column:1/-1;text-align:center;padding:20px;font-size:11px;color:var(--tx3);">No photos in library.<br>Upload photos via the Inventory tab.</div>';
        return;
    }
    grid.innerHTML = files.map(f => {
        const url = f.fileUrl || '';
        const fid = jsq(f.id || '');
        const fname = esc(f.name || f.id || '');
        const fnameJs = jsq(f.name || f.id || '');
        return `<img src="${esc(url)}" title="${fname}" style="width:100%;aspect-ratio:1;object-fit:cover;border-radius:6px;cursor:pointer;opacity:0.85;transition:opacity .15s;" onmouseover="this.style.opacity=1" onmouseout="this.style.opacity=0.85" onclick="gpSelectLibraryPhoto('${fid}','${jsq(url)}','${fnameJs}')" onerror="this.parentElement?.remove()">`;
    }).join('');
}

function gpSelectLibraryPhoto(fileId, url, name) {
    _groupPostSelectedFileId = fileId;
    _groupPostImageBase64 = null;
    document.querySelectorAll('#gpLibraryGrid img').forEach(el => el.style.outline = 'none');
    event.target.style.outline = '2px solid var(--accent)';
}

// Called from messages.js when gallery photos load, to refresh picker if open
function onGroupPostGalleryLoaded(files) {
    const libraryArea = document.getElementById('gpLibraryArea');
    if (!libraryArea || libraryArea.style.display === 'none') return;
    renderGpLibraryPhotos(files);
}

function closeGroupPostModal() {
    const overlay = document.getElementById('groupPostOverlay');
    if (overlay) overlay.style.display = 'none';
    _groupPostGroupId = null;
    _groupPostImageBase64 = null;
    _groupPostSelectedFileId = null;
}

function onGroupPostImageSelected(event) {
    const file = event.target.files?.[0];
    if (!file) return;
    if (file.size > 10 * 1024 * 1024) {
        document.getElementById('gpError').textContent = 'Image too large (max 10 MB, max 2048×2048 px)';
        document.getElementById('gpError').style.display = '';
        return;
    }
    document.getElementById('gpError').style.display = 'none';
    const reader = new FileReader();
    reader.onload = e => {
        _groupPostImageBase64 = e.target.result;
        _groupPostSelectedFileId = null;
        const preview = document.getElementById('gpImgPreview');
        if (preview) { preview.src = _groupPostImageBase64; preview.style.display = ''; }
        const label = document.getElementById('gpImgLabel');
        if (label) label.textContent = file.name;
    };
    reader.readAsDataURL(file);
}

function submitGroupPost() {
    if (!_groupPostGroupId) return;
    const title = document.getElementById('gpTitle')?.value.trim() || '';
    const text = document.getElementById('gpText')?.value.trim() || '';
    const visibility = document.getElementById('gpVisibility')?.value || 'group';
    const sendNotification = document.getElementById('gpNotify')?.value === '1';
    const errEl = document.getElementById('gpError');

    if (!title) { if (errEl) { errEl.textContent = 'Title is required.'; errEl.style.display = ''; } return; }
    if (!text) { if (errEl) { errEl.textContent = 'Content is required.'; errEl.style.display = ''; } return; }
    if (errEl) errEl.style.display = 'none';

    const btn = document.getElementById('gpSubmitBtn');
    if (btn) { btn.disabled = true; btn.innerHTML = '<span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">hourglass_empty</span>Posting...'; }

    const payload = {
        action: 'vrcCreateGroupPost',
        groupId: _groupPostGroupId,
        title,
        text,
        visibility,
        sendNotification,
    };
    if (_groupPostSelectedFileId) payload.imageFileId = _groupPostSelectedFileId;
    else if (_groupPostImageBase64) payload.imageBase64 = _groupPostImageBase64;

    sendToCS(payload);
    closeGroupPostModal();
}

/* === Group Event Modal === */
let _groupEventGroupId = null;
let _groupEventImageBase64 = null;
let _groupEventSelectedFileId = null;

function openGroupEventModal(groupId) {
    _groupEventGroupId = groupId;
    _groupEventImageBase64 = null;
    _groupEventSelectedFileId = null;

    // Default start: now + 1h, rounded to next full hour
    const now = new Date();
    now.setMinutes(0, 0, 0);
    now.setHours(now.getHours() + 1);
    const pad = n => String(n).padStart(2, '0');
    const localDT = v => `${v.getFullYear()}-${pad(v.getMonth()+1)}-${pad(v.getDate())}T${pad(v.getHours())}:${pad(v.getMinutes())}`;
    const defaultStart = localDT(now);
    const endD = new Date(now); endD.setHours(endD.getHours() + 1);
    const defaultEnd = localDT(endD);

    let overlay = document.getElementById('groupEventOverlay');
    if (!overlay) {
        overlay = document.createElement('div');
        overlay.id = 'groupEventOverlay';
        overlay.style.cssText = 'position:fixed;inset:0;z-index:10002;background:rgba(0,0,0,.55);display:flex;align-items:center;justify-content:center;';
        document.body.appendChild(overlay);
    }
    overlay.innerHTML = `
    <div class="gp-modal" role="dialog" aria-label="Create Group Event" style="max-height:calc(100vh - 32px);overflow-y:auto;">
        <div class="gp-modal-header">
            <span class="msi" style="font-size:20px;color:var(--accent);">event</span>
            <span>Create Group Event</span>
            <button class="fd-btn" onclick="closeGroupEventModal()" style="padding:4px 8px;" title="Close"><span class="msi" style="font-size:18px;">close</span></button>
        </div>
        <div class="gp-modal-body">
            <label class="gp-label">Event Name</label>
            <input id="gevName" class="gp-input" type="text" placeholder="Event name..." maxlength="64">

            <label class="gp-label" style="margin-top:12px;">Description</label>
            <textarea id="gevDesc" class="gp-textarea" placeholder="What's happening?" rows="4" maxlength="2000"></textarea>

            <div style="display:flex;gap:12px;margin-top:12px;flex-wrap:wrap;">
                <div style="flex:1;min-width:160px;">
                    <label class="gp-label">Start</label>
                    <input id="gevStart" class="gp-input" type="datetime-local" value="${defaultStart}">
                </div>
                <div style="flex:1;min-width:160px;">
                    <label class="gp-label">End</label>
                    <input id="gevEnd" class="gp-input" type="datetime-local" value="${defaultEnd}">
                </div>
            </div>

            <div style="display:flex;gap:12px;margin-top:12px;flex-wrap:wrap;">
                <div style="flex:1;min-width:130px;">
                    <label class="gp-label">Category</label>
                    <select id="gevCategory" class="gp-select">
                        <option value="hangout">Hangout</option>
                        <option value="gaming">Gaming</option>
                        <option value="music">Music</option>
                        <option value="dance">Dance</option>
                        <option value="performance">Performance</option>
                        <option value="arts">Arts</option>
                        <option value="education">Education</option>
                        <option value="exploration">Exploration</option>
                        <option value="film_media">Film & Media</option>
                        <option value="roleplaying">Roleplaying</option>
                        <option value="wellness">Wellness</option>
                        <option value="avatars">Avatars</option>
                        <option value="other">Other</option>
                    </select>
                </div>
                <div style="flex:1;min-width:130px;">
                    <label class="gp-label">Access Type</label>
                    <select id="gevAccess" class="gp-select">
                        <option value="group">Group only</option>
                        <option value="public">Public</option>
                    </select>
                </div>
            </div>

            <div style="margin-top:12px;">
                <label class="gp-label">Notification</label>
                <select id="gevNotify" class="gp-select">
                    <option value="0">No notification</option>
                    <option value="1">Send notification</option>
                </select>
            </div>

            <label class="gp-label" style="margin-top:12px;">Image <span style="color:var(--tx3);font-weight:400;">(optional)</span></label>
            <div style="display:flex;gap:6px;margin-bottom:8px;">
                <button class="fd-btn active" id="gevSrcUploadBtn" onclick="gevSetImgSource('upload')" style="flex:1;font-size:11px;"><span class="msi" style="font-size:14px;vertical-align:middle;">upload_file</span> Upload</button>
                <button class="fd-btn" id="gevSrcLibraryBtn" onclick="gevSetImgSource('library')" style="flex:1;font-size:11px;"><span class="msi" style="font-size:14px;vertical-align:middle;">photo_library</span> From Library</button>
            </div>
            <div id="gevUploadArea">
                <div class="gp-img-area" id="gevImgArea" onclick="document.getElementById('gevFileInput').click()">
                    <span class="msi" style="font-size:28px;color:var(--tx3);">image</span>
                    <span id="gevImgLabel" style="font-size:11px;color:var(--tx3);">Click to select image</span>
                    <input id="gevFileInput" type="file" accept="image/png,image/jpeg,image/gif,image/webp" style="display:none;" onchange="onGroupEventImageSelected(event)">
                </div>
                <img id="gevImgPreview" style="display:none;width:100%;max-height:160px;object-fit:contain;border-radius:8px;margin-top:8px;">
            </div>
            <div id="gevLibraryArea" style="display:none;">
                <div id="gevLibraryGrid" style="display:grid;grid-template-columns:repeat(auto-fill,minmax(72px,1fr));gap:6px;max-height:180px;overflow-y:auto;padding:4px 0;">
                    <div style="grid-column:1/-1;text-align:center;padding:20px;font-size:11px;color:var(--tx3);">Loading photos...</div>
                </div>
            </div>

            <div id="gevError" style="display:none;margin-top:8px;padding:8px 10px;background:rgba(255,80,80,.12);border-radius:8px;color:var(--err);font-size:12px;"></div>
        </div>
        <div class="gp-modal-footer">
            <button class="fd-btn" onclick="closeGroupEventModal()">Cancel</button>
            <button class="fd-btn fd-btn-join" id="gevSubmitBtn" onclick="submitGroupEvent()"><span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">event</span>Create Event</button>
        </div>
    </div>`;
    overlay.style.display = 'flex';
    setTimeout(() => document.getElementById('gevName')?.focus(), 50);
}

function gevSetImgSource(src) {
    const uploadArea = document.getElementById('gevUploadArea');
    const libraryArea = document.getElementById('gevLibraryArea');
    const uploadBtn = document.getElementById('gevSrcUploadBtn');
    const libraryBtn = document.getElementById('gevSrcLibraryBtn');
    if (!uploadArea || !libraryArea) return;
    if (src === 'library') {
        uploadArea.style.display = 'none';
        libraryArea.style.display = '';
        uploadBtn?.classList.remove('active');
        libraryBtn?.classList.add('active');
        _groupEventImageBase64 = null;
        const cached = invFilesCache['gallery'];
        if (cached && cached.length > 0) {
            gevRenderLibraryPhotos(cached);
        } else {
            sendToCS({ action: 'invGetFiles', tag: 'gallery' });
        }
    } else {
        uploadArea.style.display = '';
        libraryArea.style.display = 'none';
        uploadBtn?.classList.add('active');
        libraryBtn?.classList.remove('active');
        _groupEventSelectedFileId = null;
    }
}

function gevRenderLibraryPhotos(files) {
    const grid = document.getElementById('gevLibraryGrid');
    if (!grid) return;
    if (!files || files.length === 0) {
        grid.innerHTML = '<div style="grid-column:1/-1;text-align:center;padding:20px;font-size:11px;color:var(--tx3);">No photos in library.</div>';
        return;
    }
    grid.innerHTML = files.map(f => {
        const url = f.fileUrl || '';
        const fid = jsq(f.id || '');
        const fname = esc(f.name || f.id || '');
        return `<img src="${esc(url)}" title="${fname}" style="width:100%;aspect-ratio:1;object-fit:cover;border-radius:6px;cursor:pointer;opacity:0.85;transition:opacity .15s;" onmouseover="this.style.opacity=1" onmouseout="this.style.opacity=0.85" onclick="gevSelectLibraryPhoto('${fid}','${jsq(url)}')" onerror="this.parentElement?.remove()">`;
    }).join('');
}

function gevSelectLibraryPhoto(fileId, url) {
    _groupEventSelectedFileId = fileId;
    _groupEventImageBase64 = null;
    document.querySelectorAll('#gevLibraryGrid img').forEach(el => el.style.outline = 'none');
    event.target.style.outline = '2px solid var(--accent)';
}

// Called from messages.js when gallery photos load, refresh picker if open
function onGroupEventGalleryLoaded(files) {
    const libraryArea = document.getElementById('gevLibraryArea');
    if (!libraryArea || libraryArea.style.display === 'none') return;
    gevRenderLibraryPhotos(files);
}

function closeGroupEventModal() {
    const overlay = document.getElementById('groupEventOverlay');
    if (overlay) overlay.style.display = 'none';
    _groupEventGroupId = null;
    _groupEventImageBase64 = null;
    _groupEventSelectedFileId = null;
}

function onGroupEventImageSelected(event) {
    const file = event.target.files?.[0];
    if (!file) return;
    if (file.size > 10 * 1024 * 1024) {
        const err = document.getElementById('gevError');
        if (err) { err.textContent = 'Image too large (max 10 MB)'; err.style.display = ''; }
        return;
    }
    const err = document.getElementById('gevError');
    if (err) err.style.display = 'none';
    const reader = new FileReader();
    reader.onload = e => {
        _groupEventImageBase64 = e.target.result;
        _groupEventSelectedFileId = null;
        const preview = document.getElementById('gevImgPreview');
        if (preview) { preview.src = _groupEventImageBase64; preview.style.display = ''; }
        const label = document.getElementById('gevImgLabel');
        if (label) label.textContent = file.name;
    };
    reader.readAsDataURL(file);
}

function submitGroupEvent() {
    if (!_groupEventGroupId) return;
    const title = document.getElementById('gevName')?.value.trim() || '';
    const description = document.getElementById('gevDesc')?.value.trim() || '';
    const startVal = document.getElementById('gevStart')?.value || '';
    const endVal = document.getElementById('gevEnd')?.value || '';
    const category = document.getElementById('gevCategory')?.value || 'other';
    const accessType = document.getElementById('gevAccess')?.value || 'group';
    const sendCreationNotification = document.getElementById('gevNotify')?.value === '1';
    const errEl = document.getElementById('gevError');

    if (!title) { if (errEl) { errEl.textContent = 'Event name is required.'; errEl.style.display = ''; } return; }
    if (!description) { if (errEl) { errEl.textContent = 'Description is required.'; errEl.style.display = ''; } return; }
    if (!startVal) { if (errEl) { errEl.textContent = 'Start date/time is required.'; errEl.style.display = ''; } return; }
    if (!endVal) { if (errEl) { errEl.textContent = 'End date/time is required.'; errEl.style.display = ''; } return; }
    if (new Date(endVal) <= new Date(startVal)) { if (errEl) { errEl.textContent = 'End must be after start.'; errEl.style.display = ''; } return; }
    if (errEl) errEl.style.display = 'none';

    const btn = document.getElementById('gevSubmitBtn');
    if (btn) { btn.disabled = true; btn.innerHTML = '<span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">hourglass_empty</span>Creating...'; }

    const payload = {
        action: 'vrcCreateGroupEvent',
        groupId: _groupEventGroupId,
        title,
        description,
        startsAt: new Date(startVal).toISOString(),
        endsAt: new Date(endVal).toISOString(),
        category,
        accessType,
        sendCreationNotification,
    };
    if (_groupEventSelectedFileId) payload.imageFileId = _groupEventSelectedFileId;
    else if (_groupEventImageBase64) payload.imageBase64 = _groupEventImageBase64;

    sendToCS(payload);
    closeGroupEventModal();
}
