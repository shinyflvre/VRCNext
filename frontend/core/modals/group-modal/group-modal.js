/* === Group Modal === */
function setGroupVisibility(groupId, visibility) {
    sendToCS({ action: 'vrcSetGroupVisibility', groupId, visibility });
    // Optimistic UI update
    ['visible','friends','hidden'].forEach(v => {
        const btn = document.getElementById('ggrpVis_' + v);
        if (!btn) return;
        const isActive = v === visibility;
        const icons = { visible: 'public', friends: 'people', hidden: 'visibility_off' };
        btn.classList.toggle('vrcn-btn-primary', isActive);
        btn.querySelector('.msi').textContent = isActive ? 'check_circle' : icons[v];
    });
    // Update myGroups cache so context menu reflects new value
    if (typeof myGroups !== 'undefined') {
        const entry = myGroups.find(g => g.id === groupId);
        if (entry) entry.visibility = visibility;
    }
}

const _groupDetailCache = {};
const _gdRawJsonCache = {};

function openGroupDetail(groupId) {
    if (typeof navSetCurrent === 'function') navSetCurrent('group', groupId);
    const _gpMb = document.querySelector('#modalDetail .modal-box');
    if (_gpMb) _gpMb.classList.remove('narrow');
    const _gpMod = document.getElementById('modalDetail');
    _gpMod.classList.remove('wd-style-compact', 'tl-style-compact');
    _gpMod.classList.add('gd-style-compact');
    _gpMod.style.display = 'flex';
    const cached = _groupDetailCache[groupId];
    if (cached) {
        renderGroupDetail(cached);
    } else {
        const mg = (typeof myGroups !== 'undefined') && myGroups.find(x => x.id === groupId);
        if (mg) {
            renderGroupDetail({ ...mg, isJoined: true,
                posts: [], groupEvents: [], groupInstances: [], galleryImages: [], groupMembers: [], roles: [],
                languages: [], links: [], rules: '', ownerId: '', ownerDisplayName: '' });
        } else {
            document.getElementById('detailModalContent').innerHTML = sk('content-modal-compact');
        }
    }
    sendToCS({ action: 'vrcGetGroup', groupId });
}

function closeGroupDetail(fromNav = false) {
    document.getElementById('modalDetail').classList.remove('gd-style-compact');
    document.getElementById('modalDetail').style.display = 'none';
    if (!fromNav && typeof navClear === 'function') navClear();
}

function confirmLeaveGroup(groupId, groupName) {
    const old = document.getElementById('leaveGroupModal');
    if (old) old.remove();
    const o = document.createElement('div');
    o.className = 'modal-overlay';
    o.style.display = 'flex'; // inline display required by _closeTopModal (Escape)
    o.id = 'leaveGroupModal';
    o.style.zIndex = '10003';
    o.onclick = e => { if (e.target === o) o.remove(); };
    o.innerHTML = `<div class="modal-box">
        ${renderModalBar(t('groups.leave_confirm.title', 'Leave Group'), [modalCloseAction("document.getElementById('leaveGroupModal').remove()")])}
        <div class="modal-icon danger" style="margin-top:20px;"><span class="msi" style="font-size:22px;">logout</span></div>
        <div class="modal-msg">${tf('groups.leave_confirm.message', { name: esc(groupName || '') }, 'Leave {name}?')}</div>
        <div class="modal-btns">
            <button class="vrcn-button-round vrcn-btn-danger" onclick="document.getElementById('leaveGroupModal').remove();sendToCS({action:'vrcLeaveGroup',groupId:'${jsq(groupId)}'});closeGroupDetail();">${t('groups.actions.leave_group', 'Leave Group')}</button>
        </div></div>`;
    document.body.appendChild(o);
}

function _gdMoreDropdown(g, gidJs) {
    const items = [];
    if (g.isJoined) {
        items.push({ icon: 'shield_person', label: t('context_menu.group.represent', 'Represent this group'), onclick: `sendToCS({action:'vrcRepresentGroup',groupId:'${gidJs}'})`, disabled: g.isRepresenting === true });
        items.push({ icon: 'visibility', label: t('context_menu.group.visibility', 'Visibility'), submenu: [
            { icon: 'public',         label: t('groups.visibility.visible', 'Visible for Everyone'), active: (g.visibility || 'visible') === 'visible', onclick: `setGroupVisibility('${gidJs}','visible')` },
            { icon: 'people',         label: t('groups.visibility.friends', 'Visible for Friends'),  active: (g.visibility || 'visible') === 'friends', onclick: `setGroupVisibility('${gidJs}','friends')` },
            { icon: 'visibility_off', label: t('groups.visibility.hidden',  'Visible for None'),     active: (g.visibility || 'visible') === 'hidden',  onclick: `setGroupVisibility('${gidJs}','hidden')` },
        ] });
    }
    if ((g.roles || []).length) {
        items.push({ icon: 'shield', label: t('groups.roles.show', 'Show Roles'), onclick: `openGroupRolesModal('${gidJs}')` });
    }
    return items.length ? { label: t('common.more', 'More'), dropdown: items } : null;
}

function renderGroupDetail(g) {
    const _gdPrevBtn = document.querySelector('#detailModalContent .fd-tab.active[onclick^="switchGdTab("]');
    const _gdPrevTab = _gdPrevBtn ? ((/switchGdTab\('([^']+)'/.exec(_gdPrevBtn.getAttribute('onclick') || '') || [])[1] || '') : '';
    const _gdPrevId  = (window._currentGroupDetail && window._currentGroupDetail.id) || '';
    if (g.id && g.rawJson) _gdRawJsonCache[g.id] = g.rawJson;
    if (g.id) _groupDetailCache[g.id] = g;
    window._currentGroupDetailFull = g;
    window._currentGroupDetail = { id: g.id, canKick: g.canKick === true, canBan: g.canBan === true, canManageRoles: g.canManageRoles === true, canAssignRoles: g.canAssignRoles === true, languages: g.languages || [], links: g.links || [], joinState: g.joinState || '', roles: g.roles || [] };
    window._gdBannedLoaded = false;
    window._gdLogsLoaded   = false;
    window._gdLogRows      = [];
    window._gdLogsOffset   = 0;
    if (typeof navUpdateLabel === 'function') navUpdateLabel(g.name || '');
    window._gdMemberRoleIds = {};
    const el = document.getElementById('detailModalContent');
    const canEdit = g.canEdit === true;
    const isGroupOwner = !!(g.ownerId && typeof currentVrcUser !== 'undefined' && currentVrcUser && g.ownerId === currentVrcUser.id);
    const gidJs  = jsq(g.id);
    const banner = g.bannerUrl || g.iconUrl || 'fallback_cover.png';

    // Header
    const iconEditBtn = canEdit ? `<button class="myp-edit-btn" style="position:absolute;bottom:-4px;right:-4px;padding:2px;min-width:0;width:18px;height:18px;display:flex;align-items:center;justify-content:center;" onclick="openImagePicker('group-icon','${gidJs}')" title="${esc(t('groups.images.change_icon', 'Change icon'))}"><span class="msi" style="font-size:11px;">edit</span></button>` : '';
    const iconHtml = g.iconUrl
        ? `<div style="position:relative;display:inline-block;flex-shrink:0;"><img class="fd-avatar" src="${g.iconUrl}" onerror="this.style.display='none'">${iconEditBtn}</div>`
        : (canEdit ? `<div style="position:relative;display:inline-block;flex-shrink:0;"><div class="fd-avatar" style="display:flex;align-items:center;justify-content:center;font-size:calc(20px + var(--fs-off, 0px));font-weight:700;color:var(--tx3);">${esc((g.name||'?')[0])}</div>${iconEditBtn}</div>` : '');
    const membersPart = esc(getGroupMembersText(g.memberCount || 0)) + (g.onlineMemberCount > 0 ? ` &middot; ${g.onlineMemberCount} ${esc(t('status.online', 'Online'))}` : '');
    const headerMeta = [g.shortCode ? esc(g.shortCode) : '', membersPart].filter(Boolean).join(' &middot; ');
    const ownerLabel = g.ownerDisplayName || '';
    const ownerHtml = (g.ownerId && ownerLabel)
        ? `<div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);margin-top:2px;margin-bottom:4px;">${t('worlds.meta.by', 'by')} <span onclick="navOpenModal('friend','${jsq(g.ownerId)}','${jsq(ownerLabel)}')" style="display:inline-flex;align-items:center;padding:1px 8px;border-radius:20px;background:var(--badge-bg);font-size:calc(11px + var(--fs-off, 0px));font-weight:600;color:var(--tx1);cursor:pointer;line-height:1.8;">${esc(ownerLabel)}</span></div>`
        : '';

    // Header action cluster (top-right) — replaces the old bottom button bar.
    const canPost   = g.canPost === true;
    const canEvent  = g.canEvent === true;
    const canInvite = g.canInvite === true;
    const headerActions = renderModalActions([
        g.isJoined
            ? { icon: 'logout', title: t('groups.actions.leave_group', 'Leave Group'), onclick: `confirmLeaveGroup('${gidJs}','${jsq(g.name || '')}')`, danger: true }
            : { icon: 'group_add', title: t('groups.actions.join_group', 'Join Group'), onclick: `sendToCS({action:'vrcJoinGroup',groupId:'${gidJs}'});closeGroupDetail();` },
        (g.isJoined && canInvite) ? { icon: 'person_add', title: t('groups.actions.invite', 'Invite'), onclick: `openGroupInviteModal('${gidJs}')` } : null,
        (g.isJoined && canPost)   ? { icon: 'post_add',   title: t('groups.actions.post', 'Post'),     onclick: `openGroupPostModal('${gidJs}')` } : null,
        (g.isJoined && canEvent)  ? { icon: 'event',      title: t('groups.actions.events', 'Events'), onclick: `openGroupEventModal('${gidJs}')` } : null,
        _gdMoreDropdown(g, gidJs),
        isGroupOwner ? { icon: 'delete', title: t('groups.actions.delete_group', 'Delete Group'), onclick: `confirmDeleteGroup('${gidJs}','${jsq(g.name || '')}')`, dangerSolid: true } : null,
        { icon: 'refresh', iconClass: 'fd-refresh-spin', title: t('common.refresh', 'Refresh'), onclick: `triggerModalRefresh({action:'vrcGetGroup',groupId:'${gidJs}',force:true})` },
        { icon: 'link_2', title: t('common.share', 'Share'), onclick: `navigator.clipboard.writeText('https://vrchat.com/home/group/${esc(g.id)}').then(()=>showToast(true,t('common.link_copied','Link copied!')))` },
        { icon: 'close', title: t('common.close', 'Close'), onclick: `closeGroupDetail()` },
    ]);

    // Tab: Info
    const gid_e = esc(g.id);
    const grpLangs = (g.languages || []);
    const grpLinks = (g.links || []).filter(Boolean);
    const grpLangsViewHtml = grpLangs.length
        ? `<div class="fd-lang-tags">${grpLangs.map(l => `<span class="vrcn-badge">${esc(LANG_MAP['language_'+l] || l.toUpperCase())}</span>`).join('')}</div>`
        : `<div class="myp-empty">${t('profiles.my_profile.empty.no_languages', 'No languages set')}</div>`;
    const grpLinksViewHtml = grpLinks.length
        ? `<div class="fd-bio-links">${grpLinks.map(url => renderBioLink(url)).join('')}</div>`
        : `<div class="myp-empty">${t('profiles.my_profile.empty.no_links', 'No links added')}</div>`;
    const infoTab = `
        <div class="fd-info-cols">
            <div class="fd-info-left">
                <div class="fd-info-card">
                    <div class="myp-section-header">
                        <span class="myp-section-title">${t('groups.sections.description', 'Description')}</span>
                        <div style="display:flex;align-items:center;gap:4px;margin-left:auto;">
                            ${g.description && window._kxdProfileTranslationEnabled !== false ? `<button class="myp-edit-btn" onclick="fdTranslateBio(this)" title="${esc(t('profiles.bio.translate', 'Translate'))}"><span class="msi" style="font-size:14px;">translate</span></button>` : ''}
                            ${canEdit ? `<button class="myp-edit-btn" onclick="editGroupField('desc')"><span class="msi" style="font-size:14px;">edit</span></button>` : ''}
                        </div>
                    </div>
                    <div id="gdescDescView">
                        ${g.description ? `<div class="fd-bio">${esc(g.description)}</div>` : `<div class="myp-empty">${t('groups.empty.no_description', 'No description')}</div>`}
                    </div>
                    ${canEdit ? `<div id="gdescDescEdit" style="display:none;">
                        <textarea id="gdescDescInput" class="myp-textarea" rows="4" maxlength="2000" placeholder="${esc(t('groups.placeholders.description', 'Group description...'))}">${esc(g.description||'')}</textarea>
                        <div class="myp-edit-actions">
                            <button class="vrcn-button" onclick="cancelGroupField('desc')">${t('common.cancel', 'Cancel')}</button>
                            <button class="vrcn-button vrcn-btn-primary" onclick="saveGroupField('desc','${gid_e}')">${t('common.save', 'Save')}</button>
                        </div>
                    </div>` : ''}
                </div>
                ${(() => {
                    const lp = (g.posts || [])[0];
                    if (!lp) return '';
                    const lpDate = lp.createdAt ? fmtShortDate(new Date(lp.createdAt)) : '';
                    const lpText = lp.text || '';
                    const lpPreview = lpText.length > 80 ? lpText.slice(0, 80) + '...' : lpText;
                    const lpImg = lp.imageUrl ? `<img src="${lp.imageUrl}" style="width:60px;align-self:stretch;object-fit:cover;border-radius:6px;flex-shrink:0;" onerror="this.style.display='none'">` : '';
                    return `<div class="fd-info-card" style="cursor:pointer;" onclick="_switchGdTabByKey('posts')">
                        <div class="fd-group-rep-label">${t('groups.sections.last_post', 'Last Post')}</div>
                        <div style="display:flex;gap:10px;align-items:flex-start;">
                            <div style="flex:1;min-width:0;">
                                <div style="font-size:calc(12px + var(--fs-off, 0px));font-weight:600;color:var(--tx0);margin-bottom:2px;">${esc(lp.title || 'Untitled')}</div>
                                ${lpDate ? `<div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx3);margin-bottom:4px;">${lpDate}</div>` : ''}
                                ${lpPreview ? `<div style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx2);line-height:1.4;">${esc(lpPreview)}</div>` : ''}
                            </div>
                            ${lpImg}
                        </div>
                    </div>`;
                })()}
                ${(() => {
                    const le = (g.groupEvents || [])[0];
                    if (!le) return '';
                    const leStart = le.startsAt ? new Date(le.startsAt) : null;
                    const leDateStr = leStart && !isNaN(leStart) ? fmtLongDate(leStart) : '';
                    const leTimeStr = leStart && !isNaN(leStart) ? fmtTime(leStart) : '';
                    const leImg = le.imageUrl ? `<img src="${le.imageUrl}" style="width:60px;align-self:stretch;object-fit:cover;border-radius:6px;flex-shrink:0;" onerror="this.style.display='none'">` : '';
                    const leText = le.description || '';
                    const lePrev = leText.length > 80 ? leText.slice(0, 80) + '...' : leText;
                    return `<div class="fd-info-card" style="cursor:pointer;" onclick="_switchGdTabByKey('events')">
                        <div class="fd-group-rep-label">${t('groups.sections.last_event', 'Last Event')}</div>
                        <div style="display:flex;gap:10px;align-items:flex-start;">
                            <div style="flex:1;min-width:0;">
                                <div style="font-size:calc(12px + var(--fs-off, 0px));font-weight:600;color:var(--tx0);margin-bottom:2px;">${esc(le.title || 'Untitled Event')}</div>
                                <div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx3);margin-bottom:4px;">${leDateStr}${leTimeStr ? ' · ' + leTimeStr : ''}</div>
                                ${lePrev ? `<div style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx2);line-height:1.4;">${esc(lePrev)}</div>` : ''}
                            </div>
                            ${leImg}
                        </div>
                    </div>`;
                })()}
                <div class="fd-info-card">
                    <div class="myp-section-header">
                        <span class="myp-section-title">${t('groups.sections.rules', 'Rules')}</span>
                        <div style="display:flex;align-items:center;gap:4px;margin-left:auto;">
                            ${g.rules && window._kxdProfileTranslationEnabled !== false ? `<button class="myp-edit-btn" onclick="fdTranslateBio(this)" title="${esc(t('profiles.bio.translate', 'Translate'))}"><span class="msi" style="font-size:14px;">translate</span></button>` : ''}
                            ${canEdit ? `<button class="myp-edit-btn" onclick="editGroupField('rules')"><span class="msi" style="font-size:14px;">edit</span></button>` : ''}
                        </div>
                    </div>
                    <div id="gdescRulesView">
                        ${g.rules ? `<div class="fd-bio">${esc(g.rules)}</div>` : `<div class="myp-empty">${t('groups.empty.no_rules', 'No rules set')}</div>`}
                    </div>
                    ${canEdit ? `<div id="gdescRulesEdit" style="display:none;">
                        <textarea id="gdescRulesInput" class="myp-textarea" rows="5" maxlength="2000" placeholder="${esc(t('groups.placeholders.rules', 'Group rules...'))}">${esc(g.rules||'')}</textarea>
                        <div class="myp-edit-actions">
                            <button class="vrcn-button" onclick="cancelGroupField('rules')">${t('common.cancel', 'Cancel')}</button>
                            <button class="vrcn-button vrcn-btn-primary" onclick="saveGroupField('rules','${gid_e}')">${t('common.save', 'Save')}</button>
                        </div>
                    </div>` : ''}
                </div>
            </div>
            <div class="fd-info-right">
                ${(() => {
                    const insts = g.groupInstances || [];
                    if (!insts.length) return '';
                    const instsHtml = insts.slice(0, 3).map(inst => {
                        const thumb = inst.worldThumb ? `<img style="width:36px;height:36px;border-radius:6px;object-fit:cover;flex-shrink:0;" src="${inst.worldThumb}" onerror="this.style.display='none'">` : '';
                        const users = inst.userCount > 0 ? (inst.capacity > 0 ? `${inst.userCount}/${inst.capacity}` : `${inst.userCount}`) : '';
                        return `<div style="display:flex;align-items:center;gap:8px;padding:2px 0;">
                            ${thumb}
                            <div style="flex:1;min-width:0;">
                                <div style="font-size:calc(11px + var(--fs-off, 0px));font-weight:600;color:var(--tx0);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">${esc(inst.worldName || t('dashboard.instances.unknown_world', 'Unknown World'))}</div>
                                ${users ? `<div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx3);">${users}</div>` : ''}
                            </div>
                        </div>`;
                    }).join('');
                    return `<div class="fd-info-card" style="cursor:pointer;" onclick="_switchGdTabByKey('instances')">
                        <div class="fd-group-rep-label">${t('groups.sections.instances', 'Instances')}</div>
                        <div style="display:grid;gap:4px;">${instsHtml}</div>
                    </div>`;
                })()}
                <div class="fd-info-card">
                    <div class="fd-group-rep-label">${t('groups.sections.group_info', 'Group Info')}</div>
                    <div style="display:grid;gap:6px;">
                        ${g.joinedAt ? `<div style="display:flex;justify-content:space-between;gap:8px;align-items:baseline;font-size:calc(11px + var(--fs-off, 0px));"><span style="color:var(--tx3);">${t('groups.info.joined_at','Joined Group')}</span><span style="color:var(--tx1);text-align:right;">${fmtShortDate(new Date(g.joinedAt))}</span></div>` : ''}
                        <div style="display:flex;justify-content:space-between;gap:8px;align-items:baseline;font-size:calc(11px + var(--fs-off, 0px));"><span style="color:var(--tx3);">${t('groups.info.created_at','Created')}</span><span style="color:var(--tx1);text-align:right;">${g.createdAt ? fmtShortDate(new Date(g.createdAt)) : '—'}</span></div>
                        ${g.isJoined ? `<div style="display:flex;justify-content:space-between;gap:8px;align-items:baseline;font-size:calc(11px + var(--fs-off, 0px));"><span style="color:var(--tx3);">${t('groups.info.representing','Representing')}</span><span style="color:var(--tx1);text-align:right;">${g.isRepresenting ? t('common.yes','Yes') : t('common.no','No')}</span></div>` : ''}
                        <div style="display:flex;justify-content:space-between;gap:8px;align-items:baseline;font-size:calc(11px + var(--fs-off, 0px));"><span style="color:var(--tx3);">${t('groups.info.member_count','Members')}</span><span style="color:var(--tx1);text-align:right;">${g.memberCount ?? '—'}</span></div>
                        <div style="display:flex;justify-content:space-between;gap:8px;align-items:baseline;font-size:calc(11px + var(--fs-off, 0px));"><span style="color:var(--tx3);">${t('groups.info.verified','Verified')}</span><span style="color:var(--tx1);text-align:right;">${g.isVerified ? t('common.yes','Yes') : t('common.no','No')}</span></div>
                    </div>
                </div>
                <div class="fd-info-card">
                    <div class="myp-section-header">
                        <span class="myp-section-title">${t('groups.sections.links', 'Links')}</span>
                        ${canEdit ? `<button class="myp-edit-btn" onclick="editGroupField('links')"><span class="msi" style="font-size:14px;">edit</span></button>` : ''}
                    </div>
                    <div id="ggrpLinksView">${grpLinksViewHtml}</div>
                    ${canEdit ? `<div id="ggrpLinksEdit" style="display:none;">
                        <div id="ggrpLinksInputs"></div>
                        <div class="myp-edit-actions">
                            <button class="vrcn-button" onclick="cancelGroupField('links')">${t('common.cancel', 'Cancel')}</button>
                            <button class="vrcn-button vrcn-btn-primary" onclick="saveGroupField('links','${gid_e}')">${t('common.save', 'Save')}</button>
                        </div>
                    </div>` : ''}
                </div>
                <div class="fd-info-card">
                    <div class="myp-section-header">
                        <span class="myp-section-title">${t('groups.sections.languages', 'Languages')}</span>
                        ${canEdit ? `<button class="myp-edit-btn" onclick="editGroupField('langs')"><span class="msi" style="font-size:14px;">edit</span></button>` : ''}
                    </div>
                    <div id="ggrpLangsView">${grpLangsViewHtml}</div>
                    ${canEdit ? `<div id="ggrpLangsEdit" style="display:none;">
                        <div id="ggrpLangsChips" class="myp-lang-chips"></div>
                        <div class="myp-lang-add-row">
                            <select id="ggrpLangSelect" class="myp-lang-select"><option value="">${t('profiles.my_profile.add_language', 'Add language...')}</option></select>
                            <button class="myp-add-lang-btn" onclick="addGrpLanguage()"><span class="msi" style="font-size:15px;">add</span></button>
                        </div>
                        <div class="myp-edit-actions">
                            <button class="vrcn-button" onclick="cancelGroupField('langs')">${t('common.cancel', 'Cancel')}</button>
                            <button class="vrcn-button vrcn-btn-primary" onclick="saveGroupField('langs','${gid_e}')">${t('common.save', 'Save')}</button>
                        </div>
                    </div>` : ''}
                </div>
                <div class="fd-info-card">
                    <div class="myp-section-header">
                        <span class="myp-section-title">${t('groups.sections.open_members', 'Open to new Members')}</span>
                        ${canEdit ? `<button class="myp-edit-btn" onclick="editGroupField('joinState')"><span class="msi" style="font-size:14px;">edit</span></button>` : ''}
                    </div>
                    <div id="ggrpJoinStateView">
                        ${g.joinState ? joinStateBadge(g.joinState) : `<div class="myp-empty">${t('groups.empty.not_set', 'Not set')}</div>`}
                    </div>
                    ${canEdit ? `<div id="ggrpJoinStateEdit" style="display:none;">
                        <select id="ggrpJoinStateSelect" class="myp-lang-select" style="width:100%;margin-bottom:6px;">
                            <option value="open"    ${g.joinState==='open'    ? 'selected' : ''}>${t('groups.join_state.open', 'Open')}</option>
                            <option value="closed"  ${g.joinState==='closed'  ? 'selected' : ''}>${t('groups.join_state.closed', 'Closed')}</option>
                            <option value="invite"  ${g.joinState==='invite'  ? 'selected' : ''}>${t('groups.join_state.invite_only', 'Invite Only')}</option>
                            <option value="request" ${g.joinState==='request' ? 'selected' : ''}>${t('groups.join_state.request_invite', 'Request Invite')}</option>
                        </select>
                        <div class="myp-edit-actions">
                            <button class="vrcn-button" onclick="cancelGroupField('joinState')">${t('common.cancel', 'Cancel')}</button>
                            <button class="vrcn-button vrcn-btn-primary" onclick="saveGroupField('joinState','${gid_e}')">${t('common.save', 'Save')}</button>
                        </div>
                    </div>` : ''}
                </div>
            </div>
        </div>
        ${g.isJoined ? `<div class="fd-info-card" style="margin-top:10px;">
            <div class="myp-section-header">
                <span class="myp-section-title">${t('groups.visibility.title', 'Visibility')}</span>
            </div>
            <div id="ggrpVisibilityBtns" style="display:flex;gap:6px;flex-wrap:wrap;">
                ${[
                    { val: 'visible', icon: 'public',         key: 'groups.visibility.visible', fb: 'Visible for Everyone' },
                    { val: 'friends', icon: 'people',         key: 'groups.visibility.friends', fb: 'Visible for Friends'  },
                    { val: 'hidden',  icon: 'visibility_off', key: 'groups.visibility.hidden',  fb: 'Visible for None'     },
                ].map(opt => {
                    const active = (g.visibility || 'visible') === opt.val;
                    return `<button class="vrcn-button${active ? ' vrcn-btn-primary' : ''}" id="ggrpVis_${opt.val}" onclick="setGroupVisibility('${gid_e}','${opt.val}')" style="flex:1;justify-content:center;min-width:0;font-size:calc(11px + var(--fs-off, 0px));">
                        <span class="msi" style="font-size:13px;">${active ? 'check_circle' : opt.icon}</span>
                        ${esc(t(opt.key, opt.fb))}
                    </button>`;
                }).join('')}
            </div>
        </div>` : ''}`;

    // Tab: Posts
    const posts = g.posts || [];
    let postsTab = '';
    if (posts.length === 0) {
        postsTab = renderGroupEmptyMessage('groups.empty.no_posts', 'No posts');
    } else {
        posts.forEach((p, i) => {
            const date = p.createdAt ? fmtShortDate(new Date(p.createdAt)) : '';
            const imgHtml = p.imageUrl ? `<img src="${p.imageUrl}" style="width:120px;align-self:stretch;object-fit:cover;border-radius:6px;flex-shrink:0;" onerror="this.style.display='none'">` : '';
            const fullText = p.text || '';
            const isLong = fullText.length > 120;
            const preview = isLong ? fullText.slice(0, 120) + '...' : fullText;
            const pid = esc(p.id || ''), gid = esc(g.id || '');
            const editBtn = (canPost && p.id)
                ? `<button class="gd-post-edit" onclick="openGroupPostEditModal('${gid}','${pid}')" title="${esc(t('groups.posts.edit_title', 'Edit post'))}"><span class="msi">edit</span></button>`
                : '';
            const delBtn = (canPost && p.id)
                ? `<button class="gd-post-del" onclick="deleteGroupPost('${gid}','${pid}',this)" title="${esc(t('groups.posts.delete_title', 'Delete post'))}"><span class="msi">delete</span></button>`
                : '';
            postsTab += `<div class="fd-group-card" data-post-id="${pid}" style="display:flex;align-items:flex-start;gap:12px;cursor:default;padding:12px;">
                <div style="flex:1;min-width:0;">
                    <div style="display:flex;align-items:center;gap:6px;margin-bottom:2px;">
                        <div style="font-size:calc(13px + var(--fs-off, 0px));font-weight:600;color:var(--tx0);flex:1;">${esc(p.title || 'Untitled')}</div>
                        ${editBtn}${delBtn}
                    </div>
                    <div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx3);margin-bottom:6px;">${date}${p.visibility ? ' · ' + esc(p.visibility) : ''}</div>
                    <div class="gd-post-text" id="gpost${i}" data-full="${esc(fullText).replace(/"/g,'&quot;')}" data-preview="${esc(preview).replace(/"/g,'&quot;')}" style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx2);line-height:1.4;">${esc(preview)}</div>
                    ${isLong ? `<div style="margin-top:4px;"><span class="gd-expand" data-expanded="0" onclick="toggleGPost(${i})">${t('groups.posts.show_more', 'Show more')}</span></div>` : ''}
                </div>
                ${imgHtml}
            </div>`;
        });
    }

    // Tab: Events
    const events = g.groupEvents || [];
    let eventsTab = '';
    if (events.length === 0) {
        eventsTab = renderGroupEmptyMessage('groups.empty.no_events', 'No events');
    } else {
        events.forEach(e => {
            const startD = e.startsAt ? new Date(e.startsAt) : null;
            const endD   = e.endsAt   ? new Date(e.endsAt)   : null;
            const endDiffDay = endD && !isNaN(endD) && startD &&
                (endD.getFullYear() !== startD.getFullYear() || endD.getMonth() !== startD.getMonth() || endD.getDate() !== startD.getDate());
            const timeStr = startD && !isNaN(startD)
                ? fmtTime(startD) + (endD && !isNaN(endD) ? ' – ' + (endDiffDay ? fmtLongDate(endD) + ', ' : '') + fmtTime(endD) : '')
                : '';
            const dateStr = startD && !isNaN(startD) ? fmtLongDate(startD) : '';
            const imgHtml = e.imageUrl ? `<img src="${e.imageUrl}" style="width:100%;max-height:120px;object-fit:cover;border-radius:6px;margin-bottom:8px;" onerror="this.style.display='none'">` : '';
            const badge = e.accessType ? `<span style="font-size:calc(9px + var(--fs-off, 0px));padding:1px 6px;border-radius:4px;background:color-mix(in srgb,var(--accent) 12%,transparent);color:var(--accent-lt);border:1px solid color-mix(in srgb,var(--accent) 35%,transparent);margin-left:6px;">${esc(e.accessType)}</span>` : '';
            const gid = jsq(e.ownerId || g.id || '');
            const cid = jsq(e.id || '');
            const delEvtBtn = (canEvent && e.id)
                ? `<button class="gd-post-del" onclick="event.stopPropagation();deleteGroupEvent('${jsq(g.id)}','${cid}',this)" title="${esc(t('groups.events.delete_title', 'Delete event'))}"><span class="msi">delete</span></button>`
                : '';
            eventsTab += `<div class="fd-group-card" data-event-id="${cid}" style="display:block;cursor:pointer;padding:12px;" onclick="navOpenModal('event','${gid}','${jsq(e.title || '')}','${cid}')">
                ${imgHtml}
                <div style="display:flex;align-items:center;justify-content:space-between;gap:6px;">
                    <div style="font-size:calc(13px + var(--fs-off, 0px));font-weight:600;color:var(--tx0);">${esc(e.title || 'Untitled Event')}${badge}</div>
                    ${delEvtBtn}
                </div>
                <div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx3);margin:2px 0 4px;">${dateStr}${timeStr ? ' · ' + timeStr : ''}</div>
                ${e.description ? `<div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx2);line-height:1.4;">${esc(e.description)}</div>` : ''}
            </div>`;
        });
    }

    // Tab: Instances
    const instances = g.groupInstances || [];
    let instancesTab = '';
    if (instances.length === 0) {
        instancesTab = `<div style="padding:20px;text-align:center;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('groups.empty.no_active_instances', 'No active instances')}</div>`;
    } else {
        const cards = instances.map(inst => {
            const { instanceType, instanceId: parsedInstId } = parseFriendLocation(inst.location || '');
            const regionMatch = (inst.instanceId || '').match(/region\(([^)]+)\)/);
            const region = regionMatch ? regionMatch[1] : '';
            return renderInstanceItem({
                thumb:        inst.worldThumb || '',
                worldTitle:   inst.worldName || t('dashboard.instances.unknown_world', 'Unknown World'),
                instanceType: instanceType || 'group',
                instanceId:   inst.instanceId || '',
                region:       getWorldRegionLabel(region),
                userCount:    inst.userCount || 0,
                capacity:     inst.capacity  || 0,
                location:     inst.location  || '',
                friends:      getInstanceMembers(inst.location || ''),
                ageGate:      (inst.instanceId || '').includes('~ageGate'),
            });
        });
        instancesTab = `<div style="display:flex;flex-direction:column;gap:6px;padding:4px 0;">${cards.join('')}</div>`;
    }

    // Tab: Gallery
    const gallery = g.galleryImages || [];
    let galleryTab = '';
    if (gallery.length === 0) {
        galleryTab = renderGroupEmptyMessage('groups.empty.no_gallery_images', 'No gallery images');
    } else {
        galleryTab = '<div class="gd-gallery-grid">';
        gallery.forEach(img => {
            if (img.imageUrl) galleryTab += `<img class="gd-gallery-img" src="${img.imageUrl}" onclick="openLightbox('${jsq(img.imageUrl)}')" onerror="this.style.display='none'">`;
        });
        galleryTab += '</div>';
    }

    // Tab: Members (paginated)
    const members = g.groupMembers || [];
    let membersTab = `<div class="search-bar-row" style="margin-bottom:6px;">
        <span class="msi search-ico">search</span>
        <input id="gdMembersSearch" type="text" class="vrcn-input" placeholder="${esc(t('groups.members.search_placeholder', 'Search users by name... hit enter'))}" style="background:var(--bg-input);" onkeydown="if(event.key==='Enter')searchGroupMembers()">
    </div>`;
    membersTab += '<div id="gdMembersList" style="display:grid;grid-template-columns:1fr 1fr 1fr;column-gap:6px;">';
    if (members.length === 0) {
        membersTab += renderGroupEmptyMessage('groups.empty.no_members', 'No members');
    } else {
        members.forEach(m => { membersTab += renderGroupMemberCard(m); });
    }
    membersTab += '</div>';
    membersTab += `<div id="gdMembersLoadMore" style="text-align:center;padding:12px;">` +
        (members.length >= 50
            ? `<button class="vrcn-button" onclick="loadMoreGroupMembers()">${t('groups.members.load_more', 'Load More Members')}</button>`
            : (members.length > 0 ? `<div style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);">${t('groups.members.all_loaded', 'All members loaded')}</div>` : '')) +
        `</div>`;
    // Store group id + offset for pagination
    window._gdMembersGroupId = g.id;
    window._gdMembersOffset = members.length;
    window._gdMembersSearchActive = false;

    // Tabs
    const tabs = [
        { key: 'info', label: t('groups.tabs.info', 'Info') },
        { key: 'posts', label: t('groups.tabs.posts', 'Posts') },
        { key: 'events', label: t('groups.tabs.events', 'Events') },
        { key: 'instances', label: t('groups.tabs.live', 'Live') },
        { key: 'gallery', label: t('groups.tabs.gallery', 'Gallery') },
        { key: 'members', label: t('groups.tabs.members', 'Members') },
    ];
    if (g.canManageRoles) tabs.push({ key: 'roles', label: t('groups.tabs.roles', 'Roles') });
    if (g.canBan)         tabs.push({ key: 'banned', label: t('groups.tabs.banned', 'Banned') });
    if (g.canViewAudit)   tabs.push({ key: 'logs', label: t('groups.tabs.logs', 'Logs') });
    tabs.push({ key: 'json', label: 'Json' });
    const tabsHtml = `<div class="fd-tabs gd-tabs">${tabs.map((t,i) => `<button class="fd-tab${i===0?' active':''}" onclick="switchGdTab('${t.key}',this)">${t.label}</button>`).join('')}</div>`;

    const rolesTab   = g.canManageRoles ? _buildRolesTab(g) : '';
    const bannedTab  = g.canBan ? _buildBannedTab() : '';

    const gdTabsContent = `<div id="gdTabInfo">${infoTab}</div>
        <div id="gdTabPosts" style="display:none;">${postsTab}</div>
        <div id="gdTabEvents" style="display:none;">${eventsTab}</div>
        <div id="gdTabInstances" style="display:none;">${instancesTab}</div>
        <div id="gdTabGallery" style="display:none;">${galleryTab}</div>
        <div id="gdTabMembers" style="display:none;">${membersTab}</div>
        ${g.canManageRoles ? `<div id="gdTabRoles" style="display:none;">${rolesTab}</div>` : ''}
        ${g.canBan ? `<div id="gdTabBanned" style="display:none;">${bannedTab}</div>` : ''}
        ${g.canViewAudit ? `<div id="gdTabLogs" style="display:none;">${_buildLogsTab()}</div>` : ''}
        <div id="gdTabJson" style="display:none;"><div class="json-viewer">${jsonHighlight((g.id && _gdRawJsonCache[g.id]) || {})}</div></div>`;

    {
        const gdLeftHtml = `<div class="fd-left">
            <div class="fd-left-banner">${banner ? `<img src="${banner}" onerror="this.src='fallback_cover.png'"><div class="fd-banner-fade"></div>` : ''}${canEdit ? `<button class="fd-banner-edit" onclick="openImagePicker('group-banner','${gidJs}')" title="${esc(t('groups.images.change_banner', 'Change banner'))}"><span class="msi">edit</span></button>` : ''}</div>
            <div class="fd-left-body">
                <div class="fd-left-id">${iconHtml}<div class="fd-left-name-wrap"><div class="fd-name">${esc(g.name)}</div>${ownerHtml}<div class="fd-status">${headerMeta}</div></div></div>
            </div>
        </div>`;
        el.innerHTML = `${headerActions}<div class="fd-layout">${gdLeftHtml}<div class="fd-right"><div class="fd-right-scroll">${tabsHtml}${gdTabsContent}</div></div></div>`;
    }

    const _gdModal = document.getElementById('modalDetail');
    if (_gdModal) {
        _gdModal.classList.remove('wd-style-compact', 'tl-style-compact');
        _gdModal.classList.add('gd-style-compact');
    }
    applyGroupDetailTranslations(g);
    _gdCompactReflow(g);

    if (_gdPrevTab && _gdPrevTab !== 'info' && _gdPrevId === g.id) {
        const _gdRestoreBtn = el.querySelector(`.fd-tab[onclick^="switchGdTab('${_gdPrevTab}'"]`);
        if (_gdRestoreBtn) switchGdTab(_gdPrevTab, _gdRestoreBtn);
    }
}

function _gdCompactReflow(g) {
    const el = document.getElementById('detailModalContent');
    if (!el) return;
    const leftBody  = el.querySelector('.fd-left-body');
    const infoRight = el.querySelector('#gdTabInfo .fd-info-right');
    if (leftBody && infoRight) {
        [...infoRight.children].forEach(card => {
            const oc = card.getAttribute('onclick') || '';
            if (!oc.includes("_switchGdTabByKey('instances')")) leftBody.appendChild(card);
        });
        if (!infoRight.children.length) {
            const cols = el.querySelector('#gdTabInfo .fd-info-cols');
            if (cols) cols.style.gridTemplateColumns = '1fr';
            infoRight.remove();
        }
    }
    const descCard = el.querySelector('#gdTabInfo .fd-info-left > .fd-info-card');
    if (descCard && !descCard.querySelector('.fd-bio-badges-row')) {
        const idRow = document.createElement('div');
        idRow.className = 'fd-badges-row fd-bio-badges-row';
        idRow.style.margin = '0 0 10px';
        idRow.innerHTML = idBadge(g.id);
        const header = descCard.querySelector('.myp-section-header');
        if (header) header.after(idRow); else descCard.prepend(idRow);
    }
}

function renderGroupEmptyMessage(key, fallback) {
    return `<div style="padding:20px;text-align:center;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t(key, fallback)}</div>`;
}

function setGroupEditActionLabels(panelId) {
    const panel = document.getElementById(panelId);
    if (!panel) return;
    const buttons = panel.querySelectorAll('.myp-edit-actions button');
    if (buttons[0]) buttons[0].textContent = t('common.cancel', 'Cancel');
    if (buttons[1]) buttons[1].textContent = t('common.save', 'Save');
}

function applyGroupDetailTranslations(g) {
    const detail = document.getElementById('detailModalContent');
    if (!detail) return;

    const statusEl = detail.querySelector('.fd-status');
    if (statusEl) {
        const parts = [];
        if (g.shortCode) parts.push(g.shortCode);
        parts.push(getGroupMembersText(g.memberCount || 0) + (g.onlineMemberCount > 0 ? ` · ${g.onlineMemberCount} ${t('status.online', 'Online')}` : ''));
        statusEl.textContent = parts.join(' - ');
    }

    const tabKeys = [
        ['info', 'groups.tabs.info', 'Info'],
        ['posts', 'groups.tabs.posts', 'Posts'],
        ['events', 'groups.tabs.events', 'Events'],
        ['instances', 'groups.tabs.live', 'Live'],
        ['gallery', 'groups.tabs.gallery', 'Gallery'],
        ['members', 'groups.tabs.members', 'Members'],
        ['roles', 'groups.tabs.roles', 'Roles'],
        ['banned', 'groups.tabs.banned', 'Banned'],
    ];
    tabKeys.forEach(([key, i18nKey, fallback]) => {
        const btn = detail.querySelector(`.gd-tabs .fd-tab[onclick*="switchGdTab('${key}'"]`);
        if (btn) btn.textContent = t(i18nKey, fallback);
    });

    const titles = detail.querySelectorAll('#gdTabInfo .myp-section-title');
    if (titles[0]) titles[0].textContent = t('groups.sections.description', 'Description');
    if (titles[1]) titles[1].textContent = t('groups.sections.rules', 'Rules');
    if (titles[2]) titles[2].textContent = t('groups.sections.links', 'Links');
    if (titles[3]) titles[3].textContent = t('groups.sections.languages', 'Languages');
    if (titles[4]) titles[4].textContent = t('groups.sections.open_members', 'Open to new Members');

    if (!g.description) {
        const descView = document.getElementById('gdescDescView');
        if (descView) descView.innerHTML = `<div class="myp-empty">${t('groups.empty.no_description', 'No description')}</div>`;
    }
    if (!(g.links || []).filter(Boolean).length) {
        const linksView = document.getElementById('ggrpLinksView');
        if (linksView) linksView.innerHTML = `<div class="myp-empty">${t('profiles.my_profile.empty.no_links', 'No links added')}</div>`;
    }
    if (!(g.languages || []).length) {
        const langsView = document.getElementById('ggrpLangsView');
        if (langsView) langsView.innerHTML = `<div class="myp-empty">${t('profiles.my_profile.empty.no_languages', 'No languages set')}</div>`;
    }
    if (!g.rules) {
        const rulesView = document.getElementById('gdescRulesView');
        if (rulesView) rulesView.innerHTML = `<div class="myp-empty">${t('groups.empty.no_rules', 'No rules set')}</div>`;
    }
    if (!g.joinState) {
        const joinStateView = document.getElementById('ggrpJoinStateView');
        if (joinStateView) joinStateView.innerHTML = `<div class="myp-empty">${t('groups.empty.not_set', 'Not set')}</div>`;
    }

    const descInput = document.getElementById('gdescDescInput');
    if (descInput) descInput.placeholder = t('groups.placeholders.description', 'Group description...');
    const rulesInput = document.getElementById('gdescRulesInput');
    if (rulesInput) rulesInput.placeholder = t('groups.placeholders.rules', 'Group rules...');
    setGroupEditActionLabels('gdescDescEdit');
    setGroupEditActionLabels('ggrpLinksEdit');
    setGroupEditActionLabels('ggrpLangsEdit');
    setGroupEditActionLabels('gdescRulesEdit');
    setGroupEditActionLabels('ggrpJoinStateEdit');

    const joinStateSelect = document.getElementById('ggrpJoinStateSelect');
    if (joinStateSelect) {
        Array.from(joinStateSelect.options).forEach(opt => {
            if (opt.value === 'open') opt.textContent = t('groups.join_state.open', 'Open');
            if (opt.value === 'closed') opt.textContent = t('groups.join_state.closed', 'Closed');
            if (opt.value === 'invite') opt.textContent = t('groups.join_state.invite_only', 'Invite Only');
            if (opt.value === 'request') opt.textContent = t('groups.join_state.request_invite', 'Request Invite');
        });
    }

    const invBtn = detail.querySelector(`button[onclick*="openGroupInviteModal("]`);
    if (invBtn) invBtn.title = t('groups.actions.invite', 'Invite');
    const postBtn = detail.querySelector(`button[onclick*="openGroupPostModal("]`);
    if (postBtn) postBtn.title = t('groups.actions.post', 'Post');
    const eventBtn = detail.querySelector(`button[onclick*="openGroupEventModal("]`);
    if (eventBtn) eventBtn.title = t('groups.actions.events', 'Events');
    const leaveBtn = detail.querySelector(`button[onclick*="confirmLeaveGroup"]`);
    if (leaveBtn) leaveBtn.title = t('groups.actions.leave_group', 'Leave Group');
    const joinBtn = detail.querySelector(`button[onclick*="vrcJoinGroup"]`);
    if (joinBtn) joinBtn.title = t('groups.actions.join_group', 'Join Group');

    const postsTab = document.getElementById('gdTabPosts');
    if (postsTab) {
        if (!(g.posts || []).length) postsTab.innerHTML = renderGroupEmptyMessage('groups.empty.no_posts', 'No posts');
        postsTab.querySelectorAll('.gd-post-del').forEach(btn => btn.title = t('groups.posts.delete_title', 'Delete post'));
        postsTab.querySelectorAll('.gd-expand').forEach(link => {
            if (!link.dataset.expanded) link.dataset.expanded = '0';
            link.textContent = link.dataset.expanded === '1'
                ? t('groups.posts.show_less', 'Show less')
                : t('groups.posts.show_more', 'Show more');
        });
    }

    const eventsTab = document.getElementById('gdTabEvents');
    if (eventsTab) {
        if (!(g.groupEvents || []).length) eventsTab.innerHTML = renderGroupEmptyMessage('groups.empty.no_events', 'No events');
        eventsTab.querySelectorAll('.gd-post-del').forEach(btn => btn.title = t('groups.events.delete_title', 'Delete event'));
    }

    const instancesTab = document.getElementById('gdTabInstances');
    if (instancesTab) {
        if (!(g.groupInstances || []).length) {
            instancesTab.innerHTML = renderGroupEmptyMessage('groups.empty.no_active_instances', 'No active instances');
        } else {
            instancesTab.querySelectorAll('.fd-group-card-name').forEach(el => {
                if (el.textContent.trim() === 'Unknown World') el.textContent = t('dashboard.instances.unknown_world', 'Unknown World');
            });
            instancesTab.querySelectorAll('.vrcn-button-round').forEach(btn => {
                btn.innerHTML = `<span class="msi" style="font-size:14px;">login</span>${t('common.join', 'Join')}`;
            });
        }
    }

    const galleryTab = document.getElementById('gdTabGallery');
    if (galleryTab && !(g.galleryImages || []).length) galleryTab.innerHTML = renderGroupEmptyMessage('groups.empty.no_gallery_images', 'No gallery images');

    const membersSearch = document.getElementById('gdMembersSearch');
    if (membersSearch) membersSearch.placeholder = t('groups.members.search_placeholder', 'Search users by name... hit enter');
    const membersList = document.getElementById('gdMembersList');
    if (membersList && !(g.groupMembers || []).length) membersList.innerHTML = renderGroupEmptyMessage('groups.empty.no_members', 'No members');
    const loadMore = document.getElementById('gdMembersLoadMore');
    if (loadMore) {
        if ((g.groupMembers || []).length >= 50) {
            const btn = loadMore.querySelector('button');
            if (btn) btn.textContent = t('groups.members.load_more', 'Load More Members');
        } else if ((g.groupMembers || []).length > 0) {
            loadMore.innerHTML = `<div style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);">${t('groups.members.all_loaded', 'All members loaded')}</div>`;
        }
    }
}

function renderGroupMemberCard(m) {
    if (m.id && m.roleIds) {
        if (!window._gdMemberRoleIds) window._gdMemberRoleIds = {};
        window._gdMemberRoleIds[m.id] = m.roleIds;
    }
    const thumbUrl = m.currentAvatarThumbnailImageUrl || '';
    const opts = thumbUrl ? { attrs: `data-avatar-thumb="${esc(thumbUrl)}"` } : undefined;
    return renderProfileItem(m, `navOpenModal('friend','${jsq(m.id || '')}','${jsq(m.displayName || '')}')`, opts);
}

const _grpFieldIds = {
    desc:      { view: 'gdescDescView',       edit: 'gdescDescEdit'       },
    rules:     { view: 'gdescRulesView',      edit: 'gdescRulesEdit'      },
    links:     { view: 'ggrpLinksView',       edit: 'ggrpLinksEdit'       },
    langs:     { view: 'ggrpLangsView',       edit: 'ggrpLangsEdit'       },
    joinState: { view: 'ggrpJoinStateView',   edit: 'ggrpJoinStateEdit'   },
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
    const saveBtn = document.querySelector(`#${ids.edit} .vrcn-btn-primary`);
    if (saveBtn) saveBtn.disabled = true;

    if (field === 'desc') {
        sendToCS({ action: 'vrcUpdateGroup', groupId, description: document.getElementById('gdescDescInput')?.value ?? '' });
    } else if (field === 'rules') {
        sendToCS({ action: 'vrcUpdateGroup', groupId, rules: document.getElementById('gdescRulesInput')?.value ?? '' });
    } else if (field === 'links') {
        const inputs = document.querySelectorAll('#ggrpLinksInputs .vrcn-edit-field');
        const links = Array.from(inputs).map(i => i.value.trim()).filter(Boolean);
        sendToCS({ action: 'vrcUpdateGroup', groupId, links });
    } else if (field === 'langs') {
        const chips = document.querySelectorAll('#ggrpLangsChips [data-lang]');
        const languages = Array.from(chips).map(c => c.dataset.lang);
        sendToCS({ action: 'vrcUpdateGroup', groupId, languages });
    } else if (field === 'joinState') {
        const val = document.getElementById('ggrpJoinStateSelect')?.value;
        if (val) sendToCS({ action: 'vrcUpdateGroup', groupId, joinState: val });
    }
}

function _renderGrpLinksInputs() {
    const container = document.getElementById('ggrpLinksInputs');
    if (!container) return;
    const links = (window._currentGroupDetail?.links || []).filter(Boolean);
    container.innerHTML = [0, 1, 2].map(i =>
        `<div class="myp-link-row">
            <span class="myp-link-num">${i + 1}</span>
            <input type="url" class="vrcn-edit-field" placeholder="https://..." value="${esc(links[i]||'')}" maxlength="512" style="flex:1;">
        </div>`
    ).join('');
}

function _renderGrpLangsEdit() {
    const selected = (window._currentGroupDetail?.languages || []);
    _renderGrpLangChips(selected, document.getElementById('ggrpLangsChips'));
    const sel = document.getElementById('ggrpLangSelect');
    if (!sel) return;
    sel.innerHTML = `<option value="">${t('profiles.my_profile.add_language', 'Add language...')}</option>`;
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
    if (!window._gdMembersGroupId || window._gdMembersSearchActive) return;
    const btn = document.querySelector('#gdMembersLoadMore button');
    if (btn) { btn.textContent = t('common.loading', 'Loading...'); btn.disabled = true; }
    sendToCS({ action: 'vrcGetGroupMembers', groupId: window._gdMembersGroupId, offset: window._gdMembersOffset || 0 });
}

function searchGroupMembers() {
    if (!window._gdMembersGroupId) return;
    const q = document.getElementById('gdMembersSearch')?.value.trim() || '';
    if (!q) {
        // Empty search → reset to normal paginated view
        window._gdMembersSearchActive = false;
        window._gdMembersOffset = 0;
        const list = document.getElementById('gdMembersList');
        if (list) list.innerHTML = renderGroupEmptyMessage('common.loading', 'Loading...');
        const lm = document.getElementById('gdMembersLoadMore');
        if (lm) lm.innerHTML = '';
        sendToCS({ action: 'vrcGetGroupMembers', groupId: window._gdMembersGroupId, offset: 0 });
        return;
    }
    window._gdMembersSearchActive = true;
    const list = document.getElementById('gdMembersList');
    if (list) list.innerHTML = renderGroupEmptyMessage('groups.members.searching', 'Searching...');
    const lm = document.getElementById('gdMembersLoadMore');
    if (lm) lm.innerHTML = '';
    sendToCS({ action: 'vrcSearchGroupMembers', groupId: window._gdMembersGroupId, query: q });
}

function switchGdTab(tab, btn) {
    const box = document.querySelector('#modalDetail .modal-box');
    animateModalBox(box, () => {
        ['Info','Posts','Events','Instances','Gallery','Members','Roles','Banned','Logs','Json'].forEach(t => {
            const el = document.getElementById('gdTab' + t);
            if (el) el.style.display = t.toLowerCase() === tab ? '' : 'none';
        });
        btn.closest('.fd-tabs').querySelectorAll('.fd-tab').forEach(t => t.classList.remove('active'));
        btn.classList.add('active');
    });
    if (tab === 'banned' && !window._gdBannedLoaded) loadGroupBans();
    if (tab === 'logs'   && !window._gdLogsLoaded)   loadGroupLogs(true);
}

function _switchGdTabByKey(key) {
    const btn = document.querySelector(`.gd-tabs .fd-tab[onclick*="'${key}'"]`);
    if (btn) switchGdTab(key, btn);
}

function toggleGPost(i) {
    const el = document.getElementById('gpost' + i);
    const link = el?.parentElement?.querySelector('.gd-expand');
    if (!el || !link) return;
    const expanded = link.dataset.expanded === '1';
    if (!expanded) {
        el.textContent = el.dataset.full;
        link.dataset.expanded = '1';
        link.textContent = t('groups.posts.show_less', 'Show less');
    } else {
        el.textContent = el.dataset.preview;
        link.dataset.expanded = '0';
        link.textContent = t('groups.posts.show_more', 'Show more');
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
let _groupPostEditPostId = null;

function renderGroupImageUploadTile(onclickHandler) {
    return `<div style="width:100%;aspect-ratio:1;border-radius:6px;cursor:pointer;background:var(--bg-input);border:1.5px dashed var(--border);display:flex;align-items:center;justify-content:center;flex-shrink:0;" onmouseover="this.style.borderColor='var(--accent)'" onmouseout="this.style.borderColor='var(--border)'" onclick="${onclickHandler}()" title="${esc(t('groups.common.upload_new_photo', 'Upload new photo'))}"><span class="msi" style="font-size:22px;color:var(--tx3);pointer-events:none;">add_photo_alternate</span></div>`;
}

function openGroupPostEditModal(groupId, postId) {
    const g = window._currentGroupDetailFull;
    if (!g) return;
    const post = (g.posts || []).find(p => p.id === postId);
    if (!post) return;
    openGroupPostModal(groupId, post);
}

function openGroupPostModal(groupId, editPost = null) {
    _groupPostGroupId = groupId;
    _groupPostImageBase64 = null;
    _groupPostSelectedFileId = null;
    _groupPostEditPostId = editPost ? (editPost.id || null) : null;

    const isEdit = !!_groupPostEditPostId;
    const modalTitle    = isEdit ? t('groups.posts.modal.title_edit',    'Edit Group Post')   : t('groups.posts.modal.title',    'Create Group Post');
    const modalAriaLabel = isEdit ? t('groups.posts.modal.aria_label_edit','Edit Group Post')  : t('groups.posts.modal.aria_label','Create Group Post');
    const submitLabel   = isEdit ? t('groups.posts.submit_edit', 'Save')                      : t('groups.posts.submit', 'Post');

    let overlay = document.getElementById('groupPostOverlay');
    if (!overlay) {
        overlay = document.createElement('div');
        overlay.id = 'groupPostOverlay';
        overlay.style.cssText = 'position:fixed;top:var(--tb-h);left:0;right:0;bottom:0;z-index:10002;background:rgba(0,0,0,.55);display:flex;align-items:center;justify-content:center;';
        document.body.appendChild(overlay);
    }
    overlay.innerHTML = `
    <div class="gp-modal" role="dialog" aria-label="${esc(modalAriaLabel)}">
        ${renderModalBar(modalTitle, [modalCloseAction('closeGroupPostModal()')])}
        <div class="gp-modal-body">
            <label class="gp-label">${t('groups.posts.fields.title', 'Title')}</label>
            <input id="gpTitle" class="vrcn-edit-field" type="text" placeholder="${esc(t('groups.posts.fields.title_placeholder', 'Post title...'))}" maxlength="200" style="width:100%;">
            <label class="gp-label" style="margin-top:12px;">${t('groups.posts.fields.content', 'Content')}</label>
            <textarea id="gpText" class="gp-textarea" placeholder="${esc(t('groups.posts.fields.content_placeholder', "What's on your mind?"))}" rows="5" maxlength="2000"></textarea>
            <div style="display:flex;gap:12px;margin-top:12px;flex-wrap:wrap;">
                <div style="flex:1;min-width:130px;">
                    <label class="gp-label">${t('groups.posts.fields.visibility', 'Visibility')}</label>
                    <select id="gpVisibility" class="wd-create-select" style="width:100%">
                        <option value="group">${t('groups.common.group_only', 'Group only')}</option>
                        <option value="public">${t('groups.common.public', 'Public')}</option>
                    </select>
                </div>
                <div style="flex:1;min-width:130px;">
                    <label class="gp-label">${t('groups.posts.fields.notification', 'Notification')}</label>
                    <select id="gpNotify" class="wd-create-select" style="width:100%">
                        <option value="0">${t('groups.common.no_notification', 'No notification')}</option>
                        <option value="1">${t('groups.common.send_notification', 'Send notification')}</option>
                    </select>
                </div>
            </div>
            <label class="gp-label" style="margin-top:12px;">${t('groups.posts.fields.image', 'Image')} <span style="color:var(--tx3);font-weight:400;">(${t('groups.common.optional', 'optional')})</span></label>
            <div id="gpImgGrid" style="display:grid;grid-template-columns:repeat(auto-fill,minmax(72px,1fr));gap:6px;max-height:180px;overflow-y:auto;padding:4px 0;"></div>
            <div id="gpError" style="display:none;margin-top:8px;padding:8px 10px;background:rgba(255,80,80,.12);border-radius:8px;color:var(--err);font-size:calc(12px + var(--fs-off, 0px));"></div>
        </div>
        <div class="gp-modal-footer">
            <button class="vrcn-button-round vrcn-btn-join" id="gpSubmitBtn" onclick="submitGroupPost()"><span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">send</span>${esc(submitLabel)}</button>
        </div>
    </div>`;
    initAllVnSelects();
    overlay.style.display = 'flex';
    if (isEdit && editPost) {
        const titleEl = document.getElementById('gpTitle');
        const textEl  = document.getElementById('gpText');
        const visEl   = document.getElementById('gpVisibility');
        if (titleEl) titleEl.value = editPost.title || '';
        if (textEl)  textEl.value  = editPost.text  || '';
        if (visEl)   visEl.value   = editPost.visibility || 'group';
    }
    setTimeout(() => document.getElementById('gpTitle')?.focus(), 50);
    const _gpCached = (typeof invFilesCache !== 'undefined') && invFilesCache['gallery'];
    if (_gpCached && _gpCached.length > 0) renderGpImgGrid(_gpCached);
    else sendToCS({ action: 'invGetFiles', tag: 'gallery' });
}

function renderGpImgGrid(files) {
    const grid = document.getElementById('gpImgGrid');
    if (!grid) return;
    const plusTile = renderGroupImageUploadTile('gpOpenUpload');
    if (!files || !files.length) { grid.innerHTML = plusTile; return; }
    grid.innerHTML = plusTile + files.map(f => {
        const url = f.fileUrl || '';
        if (!url) return '';
        const fid = jsq(f.id || '');
        const fname = esc(f.name || f.id || '');
        return `<img src="${esc(url)}" title="${fname}" style="width:100%;aspect-ratio:1;object-fit:cover;border-radius:6px;cursor:pointer;opacity:0.85;transition:opacity .15s;" onmouseover="this.style.opacity=1" onmouseout="this.style.opacity=0.85" onclick="gpSelectLibraryPhoto('${fid}','${jsq(url)}')" onerror="this.style.display='none'">`;
    }).join('');
}

function gpOpenUpload() {
    openInvUploadModal('photos', file => {
        _groupPostSelectedFileId = file.id;
        _groupPostImageBase64 = null;
        renderGpImgGrid(invFilesCache['gallery'] || []);
        const first = document.querySelector('#gpImgGrid img');
        if (first) { document.querySelectorAll('#gpImgGrid img').forEach(el => el.style.outline = 'none'); first.style.outline = '2px solid var(--accent)'; }
    });
}

function gpSelectLibraryPhoto(fileId, url) {
    _groupPostSelectedFileId = fileId;
    _groupPostImageBase64 = null;
    document.querySelectorAll('#gpImgGrid img').forEach(el => el.style.outline = 'none');
    event.target.style.outline = '2px solid var(--accent)';
}

function onGroupPostGalleryLoaded(files) {
    if (!document.getElementById('gpImgGrid')) return;
    renderGpImgGrid(files);
}

function closeGroupPostModal() {
    const overlay = document.getElementById('groupPostOverlay');
    if (overlay) overlay.style.display = 'none';
    _groupPostGroupId = null;
    _groupPostImageBase64 = null;
    _groupPostSelectedFileId = null;
    _groupPostEditPostId = null;
}


function submitGroupPost() {
    if (!_groupPostGroupId) return;
    const title = document.getElementById('gpTitle')?.value.trim() || '';
    const text = document.getElementById('gpText')?.value.trim() || '';
    const visibility = document.getElementById('gpVisibility')?.value || 'group';
    const sendNotification = document.getElementById('gpNotify')?.value === '1';
    const errEl = document.getElementById('gpError');

    if (!title) { if (errEl) { errEl.textContent = t('groups.posts.validation.title_required', 'Title is required.'); errEl.style.display = ''; } return; }
    if (!text) { if (errEl) { errEl.textContent = t('groups.posts.validation.content_required', 'Content is required.'); errEl.style.display = ''; } return; }
    if (errEl) errEl.style.display = 'none';

    const btn = document.getElementById('gpSubmitBtn');
    if (btn) { btn.disabled = true; btn.innerHTML = `<span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">hourglass_empty</span>${t('groups.posts.submitting', 'Posting...')}`; }

    if (_groupPostEditPostId) {
        const editPayload = { action: 'vrcUpdateGroupPost', groupId: _groupPostGroupId, postId: _groupPostEditPostId, title, text, visibility };
        if (_groupPostSelectedFileId) editPayload.imageFileId = _groupPostSelectedFileId;
        else if (_groupPostImageBase64) editPayload.imageBase64 = _groupPostImageBase64;
        sendToCS(editPayload);
    } else {
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
    }
    closeGroupPostModal();
}

/* === Group Event Date-Time Picker === */
let _gevDpTarget = null; // 'start' | 'end'
let _gevDpYear = 0, _gevDpMonth = 0;
let _gevDpSelDate = ''; // YYYY-MM-DD
let _gevDpHour = 12;   // 0-23 (internal always 24h)
let _gevDpMin  = 0;
let _gevDp24h  = true; // initialized to Windows 24h on first open

function _gevDpDateLocale() {
    return getLanguageLocale();
}

function _gevDpAmLabel() {
    return t('groups.date_picker.am', 'AM');
}

function _gevDpPmLabel() {
    return t('groups.date_picker.pm', 'PM');
}

function _gevDpWeekdaysHtml() {
    const fmt = new Intl.DateTimeFormat(_gevDpDateLocale(), { weekday: 'short' });
    return Array.from({ length: 7 }, (_, index) => fmt.format(new Date(2024, 0, 8 + index)))
        .map(label => `<div class="tl-dp-wd">${esc(label)}</div>`)
        .join('');
}

function _gevDpMonthLabel(year, month) {
    return new Intl.DateTimeFormat(_gevDpDateLocale(), { month: 'long', year: 'numeric' }).format(new Date(year, month, 1));
}

function _ensureGevDp() {
    const existing = document.getElementById('gevDatePicker');
    if (existing) {
        const weekdays = existing.querySelector('.tl-dp-weekdays');
        if (weekdays) weekdays.innerHTML = _gevDpWeekdaysHtml();
        const nowBtn = existing.querySelector('[data-gev-now]');
        if (nowBtn) nowBtn.textContent = t('groups.date_picker.now', 'Now');
        const okBtn = existing.querySelector('[data-gev-confirm]');
        if (okBtn) okBtn.textContent = t('groups.date_picker.confirm', 'OK');
        return;
    }
    const el = document.createElement('div');
    el.id = 'gevDatePicker';
    el.className = 'tl-date-picker';
    el.style.cssText = 'display:none;width:268px;z-index:10100;';
    el.innerHTML = `
        <div class="tl-dp-header">
            <button class="tl-dp-nav" onclick="gevDpNavMonth(-1)"><span class="msi" style="font-size:16px;">chevron_left</span></button>
            <span id="gevDpMonthLabel" class="tl-dp-month-label"></span>
            <button class="tl-dp-nav" onclick="gevDpNavMonth(1)"><span class="msi" style="font-size:16px;">chevron_right</span></button>
        </div>
        <div class="tl-dp-weekdays">${_gevDpWeekdaysHtml()}</div>
        <div id="gevDpDaysGrid" class="tl-dp-days"></div>
        <div style="display:flex;align-items:center;gap:6px;margin-top:10px;padding-top:10px;border-top:1px solid var(--brd);">
            <span class="msi" style="font-size:15px;color:var(--tx3);">schedule</span>
            <input id="gevDpHourInput" class="gev-dp-time-input" type="number" min="1" max="12" oninput="gevDpTimeChanged()">
            <span style="color:var(--tx2);font-weight:700;font-size:calc(15px + var(--fs-off, 0px));">:</span>
            <input id="gevDpMinInput" class="gev-dp-time-input" type="number" min="0" max="59" oninput="gevDpTimeChanged()">
            <button id="gevDpAmPmBtn" class="gev-dp-ampm-btn" onclick="gevDpToggleAmPm()">${t('groups.date_picker.am', 'AM')}</button>
            <button id="gevDp24hBtn" class="gev-dp-24h-btn" onclick="gevDpToggle24h()">24h</button>
        </div>
        <div class="tl-dp-footer">
            <button class="vrcn-button-round" style="flex:1;justify-content:center;" onclick="gevDpNow()" data-gev-now>${t('groups.date_picker.now', 'Now')}</button>
            <button class="vrcn-button-round vrcn-btn-join" style="flex:1;justify-content:center;" onclick="gevDpConfirm()" data-gev-confirm>${t('groups.date_picker.confirm', 'OK')}</button>
        </div>`;
    document.body.appendChild(el);
}

function _gevDpFmtDate(year, month, day) {
    const d = new Date(year, month, day);
    return d.getFullYear() + '-' + String(d.getMonth()+1).padStart(2,'0') + '-' + String(d.getDate()).padStart(2,'0');
}


function openGevDp(target) {
    _ensureGevDp();
    _gevDpTarget = target;
    _gevDp24h = _dtIs24Hour;
    const existing = document.getElementById(target === 'start' ? 'gevStart' : 'gevEnd')?.value || '';
    if (existing) {
        const [datePart, timePart] = existing.split('T');
        _gevDpSelDate = datePart;
        if (timePart) { const [h, m] = timePart.split(':').map(Number); _gevDpHour = h||0; _gevDpMin = m||0; }
        const base = new Date(_gevDpSelDate + 'T00:00:00');
        _gevDpYear = base.getFullYear(); _gevDpMonth = base.getMonth();
    } else {
        const now = new Date();
        _gevDpSelDate = ''; _gevDpHour = now.getHours(); _gevDpMin = 0;
        _gevDpYear = now.getFullYear(); _gevDpMonth = now.getMonth();
    }
    _gevDpSyncTimeInputs();
    renderGevDpCalendar();
    const picker = document.getElementById('gevDatePicker');
    picker.style.display = '';
    const trigEl = document.getElementById(target === 'start' ? 'gevStartDisplay' : 'gevEndDisplay');
    if (trigEl) {
        const rect = trigEl.getBoundingClientRect();
        const ph = picker.offsetHeight || 360;
        const top = rect.bottom + 6 + ph > window.innerHeight ? rect.top - ph - 6 : rect.bottom + 6;
        picker.style.top  = Math.max(6, top) + 'px';
        picker.style.left = Math.min(rect.left, window.innerWidth - 276) + 'px';
    }
    setTimeout(() => document.addEventListener('click', _gevDpOutside), 0);
}

function _gevDpOutside(e) {
    const picker = document.getElementById('gevDatePicker');
    if (!picker) return;
    // Detached target = calendar grid was re-rendered (day click) — not an outside click
    if (!e.target.isConnected) return;
    const trigEl = document.getElementById(_gevDpTarget === 'start' ? 'gevStartDisplay' : 'gevEndDisplay');
    if (!picker.contains(e.target) && (!trigEl || !trigEl.contains(e.target))) {
        picker.style.display = 'none';
        document.removeEventListener('click', _gevDpOutside);
    }
}

function gevDpNavMonth(dir) {
    _gevDpMonth += dir;
    if (_gevDpMonth < 0)  { _gevDpMonth = 11; _gevDpYear--; }
    if (_gevDpMonth > 11) { _gevDpMonth = 0;  _gevDpYear++; }
    renderGevDpCalendar();
}

function gevDpSelectDate(ds) { _gevDpSelDate = ds; renderGevDpCalendar(); }

function gevDpToggle24h() {
    gevDpTimeChanged();
    _gevDp24h = !_gevDp24h;
    _gevDpSyncTimeInputs();
}

function _gevDpFormatDisplay(dateStr, hour, min, use24) {
    const d = new Date(dateStr + 'T00:00:00');
    d.setHours(hour, min, 0, 0);
    return new Intl.DateTimeFormat(use24 ? _gevDpDateLocale() : _gevDpTimeLocale(), {
        weekday: 'short',
        month: 'short',
        day: 'numeric',
        hour: 'numeric',
        minute: '2-digit',
        hour12: !use24,
    }).format(d);
}

function _gevDpSyncTimeInputs() {
    const hInput  = document.getElementById('gevDpHourInput');
    const mInput  = document.getElementById('gevDpMinInput');
    const ampmBtn = document.getElementById('gevDpAmPmBtn');
    const h24Btn  = document.getElementById('gevDp24hBtn');
    if (!hInput) return;
    if (_gevDp24h) {
        hInput.min = '0';
        hInput.max = '23';
        hInput.value = String(_gevDpHour).padStart(2, '0');
        if (ampmBtn) ampmBtn.style.display = 'none';
        if (h24Btn) h24Btn.classList.add('active');
    } else {
        hInput.min = '1';
        hInput.max = '12';
        let h12 = _gevDpHour % 12;
        if (h12 === 0) h12 = 12;
        hInput.value = h12;
        if (ampmBtn) {
            ampmBtn.textContent = _gevDpHour < 12 ? _gevDpAmLabel() : _gevDpPmLabel();
            ampmBtn.style.display = '';
        }
        if (h24Btn) h24Btn.classList.remove('active');
    }
    if (mInput) mInput.value = String(_gevDpMin).padStart(2, '0');
}

function renderGevDpCalendar() {
    const label = document.getElementById('gevDpMonthLabel');
    const grid = document.getElementById('gevDpDaysGrid');
    if (!label || !grid) return;
    label.textContent = _gevDpMonthLabel(_gevDpYear, _gevDpMonth);
    const today = new Date();
    const todayStr = _gevDpFmtDate(today.getFullYear(), today.getMonth(), today.getDate());
    const firstDow = new Date(_gevDpYear, _gevDpMonth, 1).getDay();
    const firstDowMon = (firstDow + 6) % 7;
    const daysInMonth = new Date(_gevDpYear, _gevDpMonth + 1, 0).getDate();
    const daysInPrev = new Date(_gevDpYear, _gevDpMonth, 0).getDate();
    let html = '';
    for (let i = firstDowMon - 1; i >= 0; i--) {
        const d = daysInPrev - i;
        const ds = _gevDpFmtDate(_gevDpYear, _gevDpMonth - 1, d);
        html += `<button class="tl-dp-day other-month${ds === _gevDpSelDate ? ' selected' : ''}" onclick="gevDpSelectDate('${ds}')">${d}</button>`;
    }
    for (let d = 1; d <= daysInMonth; d++) {
        const ds = _gevDpFmtDate(_gevDpYear, _gevDpMonth, d);
        const cls = (ds === todayStr ? ' today' : '') + (ds === _gevDpSelDate ? ' selected' : '');
        html += `<button class="tl-dp-day${cls}" onclick="gevDpSelectDate('${ds}')">${d}</button>`;
    }
    const used = firstDowMon + daysInMonth;
    const remaining = used % 7 === 0 ? 0 : 7 - (used % 7);
    for (let d = 1; d <= remaining; d++) {
        const ds = _gevDpFmtDate(_gevDpYear, _gevDpMonth + 1, d);
        html += `<button class="tl-dp-day other-month${ds === _gevDpSelDate ? ' selected' : ''}" onclick="gevDpSelectDate('${ds}')">${d}</button>`;
    }
    grid.innerHTML = html;
}

function gevDpTimeChanged() {
    const hInput = document.getElementById('gevDpHourInput');
    const mInput = document.getElementById('gevDpMinInput');
    if (!hInput) return;
    let h = parseInt(hInput.value) || 0;
    const m = Math.max(0, Math.min(59, parseInt(mInput.value) || 0));
    if (_gevDp24h) {
        _gevDpHour = Math.max(0, Math.min(23, h));
    } else {
        h = Math.max(1, Math.min(12, h));
        const isAm = document.getElementById('gevDpAmPmBtn')?.textContent === _gevDpAmLabel();
        _gevDpHour = (h === 12 ? 0 : h) + (isAm ? 0 : 12);
    }
    _gevDpMin = m;
}

function gevDpToggleAmPm() {
    const btn = document.getElementById('gevDpAmPmBtn');
    if (!btn) return;
    if (_gevDpHour < 12) {
        _gevDpHour += 12;
        btn.textContent = _gevDpPmLabel();
    } else {
        _gevDpHour -= 12;
        btn.textContent = _gevDpAmLabel();
    }
}

function gevDpNow() {
    const now = new Date();
    _gevDpSelDate = _gevDpFmtDate(now.getFullYear(), now.getMonth(), now.getDate());
    _gevDpHour = now.getHours(); _gevDpMin = now.getMinutes();
    _gevDpYear = now.getFullYear(); _gevDpMonth = now.getMonth();
    _gevDpSyncTimeInputs();
    renderGevDpCalendar();
}

function gevDpConfirm() {
    if (!_gevDpSelDate) return;
    gevDpTimeChanged();
    const value     = `${_gevDpSelDate}T${String(_gevDpHour).padStart(2,'0')}:${String(_gevDpMin).padStart(2,'0')}`;
    const hiddenId  = _gevDpTarget === 'start' ? 'gevStart' : 'gevEnd';
    const displayId = _gevDpTarget === 'start' ? 'gevStartDisplay' : 'gevEndDisplay';
    const hidden  = document.getElementById(hiddenId);
    const display = document.getElementById(displayId);
    if (hidden)  hidden.value = value;
    if (display) display.textContent = _gevDpFormatDisplay(_gevDpSelDate, _gevDpHour, _gevDpMin, _gevDp24h);
    document.getElementById('gevDatePicker').style.display = 'none';
    document.removeEventListener('click', _gevDpOutside);
}

function _gevDpSetDisplay(target, dtLocalStr) {
    if (!dtLocalStr) return;
    const [datePart, timePart] = dtLocalStr.split('T');
    const [h, m] = (timePart || '00:00').split(':').map(Number);
    document.getElementById(target === 'start' ? 'gevStart' : 'gevEnd').value = dtLocalStr;
    const display = document.getElementById(target === 'start' ? 'gevStartDisplay' : 'gevEndDisplay');
    if (display) display.textContent = _gevDpFormatDisplay(datePart, h, m, _gevDp24h);
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
        overlay.style.cssText = 'position:fixed;top:var(--tb-h);left:0;right:0;bottom:0;z-index:10002;background:rgba(0,0,0,.55);display:flex;align-items:center;justify-content:center;';
        document.body.appendChild(overlay);
    }
    overlay.innerHTML = `
    <div class="gp-modal" role="dialog" aria-label="${esc(t('groups.events.modal.aria_label', 'Create Group Event'))}" style="max-height:calc(100vh - var(--tb-h) - 32px);overflow-y:auto;">
        ${renderModalBar(t('groups.events.modal.title', 'Create Group Event'), [modalCloseAction('closeGroupEventModal()')])}
        <div class="gp-modal-body">
            <label class="gp-label">${t('groups.events.fields.name', 'Event Name')}</label>
            <input id="gevName" class="vrcn-edit-field" type="text" placeholder="${esc(t('groups.events.fields.name_placeholder', 'Event name...'))}" maxlength="64" style="width:100%;">

            <label class="gp-label" style="margin-top:12px;">${t('groups.events.fields.description', 'Description')}</label>
            <textarea id="gevDesc" class="gp-textarea" placeholder="${esc(t('groups.events.fields.description_placeholder', "What's happening?"))}" rows="4" maxlength="2000"></textarea>

            <div style="display:flex;gap:12px;margin-top:12px;flex-wrap:wrap;">
                <div style="flex:1;min-width:160px;">
                    <label class="gp-label">${t('groups.events.fields.start', 'Start')}</label>
                    <div class="gp-input gev-dt-trigger" id="gevStartDisplay" onclick="openGevDp('start')"></div>
                    <input type="hidden" id="gevStart">
                </div>
                <div style="flex:1;min-width:160px;">
                    <label class="gp-label">${t('groups.events.fields.end', 'End')}</label>
                    <div class="gp-input gev-dt-trigger" id="gevEndDisplay" onclick="openGevDp('end')"></div>
                    <input type="hidden" id="gevEnd">
                </div>
            </div>

            <div style="display:flex;gap:12px;margin-top:12px;flex-wrap:wrap;">
                <div style="flex:1;min-width:130px;">
                    <label class="gp-label">${t('groups.events.fields.category', 'Category')}</label>
                    <select id="gevCategory" class="wd-create-select" style="width:100%">
                        <option value="hangout">${t('groups.events.categories.hangout', 'Hangout')}</option>
                        <option value="gaming">${t('groups.events.categories.gaming', 'Gaming')}</option>
                        <option value="music">${t('groups.events.categories.music', 'Music')}</option>
                        <option value="dance">${t('groups.events.categories.dance', 'Dance')}</option>
                        <option value="performance">${t('groups.events.categories.performance', 'Performance')}</option>
                        <option value="arts">${t('groups.events.categories.arts', 'Arts')}</option>
                        <option value="education">${t('groups.events.categories.education', 'Education')}</option>
                        <option value="exploration">${t('groups.events.categories.exploration', 'Exploration')}</option>
                        <option value="film_media">${t('groups.events.categories.film_media', 'Film & Media')}</option>
                        <option value="roleplaying">${t('groups.events.categories.roleplaying', 'Roleplaying')}</option>
                        <option value="wellness">${t('groups.events.categories.wellness', 'Wellness')}</option>
                        <option value="avatars">${t('groups.events.categories.avatars', 'Avatars')}</option>
                        <option value="other">${t('groups.events.categories.other', 'Other')}</option>
                    </select>
                </div>
                <div style="flex:1;min-width:130px;">
                    <label class="gp-label">${t('groups.events.fields.access_type', 'Access Type')}</label>
                    <select id="gevAccess" class="wd-create-select" style="width:100%">
                        <option value="group">${t('groups.common.group_only', 'Group only')}</option>
                        <option value="public">${t('groups.common.public', 'Public')}</option>
                    </select>
                </div>
            </div>

            <div style="margin-top:12px;">
                <label class="gp-label">${t('groups.events.fields.notification', 'Notification')}</label>
                <select id="gevNotify" class="wd-create-select" style="width:100%">
                    <option value="0">${t('groups.common.no_notification', 'No notification')}</option>
                    <option value="1">${t('groups.common.send_notification', 'Send notification')}</option>
                </select>
            </div>

            <label class="gp-label" style="margin-top:12px;">${t('groups.events.fields.image', 'Image')} <span style="color:var(--tx3);font-weight:400;">(${t('groups.common.optional', 'optional')})</span></label>
            <div id="gevImgGrid" style="display:grid;grid-template-columns:repeat(auto-fill,minmax(72px,1fr));gap:6px;max-height:180px;overflow-y:auto;padding:4px 0;"></div>

            <div id="gevError" style="display:none;margin-top:8px;padding:8px 10px;background:rgba(255,80,80,.12);border-radius:8px;color:var(--err);font-size:calc(12px + var(--fs-off, 0px));"></div>
        </div>
        <div class="gp-modal-footer">
            <button class="vrcn-button-round vrcn-btn-join" id="gevSubmitBtn" onclick="submitGroupEvent()"><span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">event</span>${t('groups.events.submit', 'Create Event')}</button>
        </div>
    </div>`;
    initAllVnSelects();
    _gevDpSetDisplay('start', defaultStart);
    _gevDpSetDisplay('end', defaultEnd);
    overlay.style.display = 'flex';
    setTimeout(() => document.getElementById('gevName')?.focus(), 50);
    const _gevCached = (typeof invFilesCache !== 'undefined') && invFilesCache['gallery'];
    if (_gevCached && _gevCached.length > 0) renderGevImgGrid(_gevCached);
    else sendToCS({ action: 'invGetFiles', tag: 'gallery' });
}

function renderGevImgGrid(files) {
    const grid = document.getElementById('gevImgGrid');
    if (!grid) return;
    const plusTile = renderGroupImageUploadTile('gevOpenUpload');
    if (!files || !files.length) { grid.innerHTML = plusTile; return; }
    grid.innerHTML = plusTile + files.map(f => {
        const url = f.fileUrl || '';
        if (!url) return '';
        const fid = jsq(f.id || '');
        const fname = esc(f.name || f.id || '');
        return `<img src="${esc(url)}" title="${fname}" style="width:100%;aspect-ratio:1;object-fit:cover;border-radius:6px;cursor:pointer;opacity:0.85;transition:opacity .15s;" onmouseover="this.style.opacity=1" onmouseout="this.style.opacity=0.85" onclick="gevSelectLibraryPhoto('${fid}','${jsq(url)}')" onerror="this.style.display='none'">`;
    }).join('');
}

function gevOpenUpload() {
    openInvUploadModal('photos', file => {
        _groupEventSelectedFileId = file.id;
        _groupEventImageBase64 = null;
        renderGevImgGrid(invFilesCache['gallery'] || []);
        const first = document.querySelector('#gevImgGrid img');
        if (first) { document.querySelectorAll('#gevImgGrid img').forEach(el => el.style.outline = 'none'); first.style.outline = '2px solid var(--accent)'; }
    });
}

function gevSelectLibraryPhoto(fileId, url) {
    _groupEventSelectedFileId = fileId;
    _groupEventImageBase64 = null;
    document.querySelectorAll('#gevImgGrid img').forEach(el => el.style.outline = 'none');
    event.target.style.outline = '2px solid var(--accent)';
}

function onGroupEventGalleryLoaded(files) {
    if (!document.getElementById('gevImgGrid')) return;
    renderGevImgGrid(files);
}

function closeGroupEventModal() {
    const overlay = document.getElementById('groupEventOverlay');
    if (overlay) overlay.style.display = 'none';
    _groupEventGroupId = null;
    _groupEventImageBase64 = null;
    _groupEventSelectedFileId = null;
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

    if (!title) { if (errEl) { errEl.textContent = t('groups.events.validation.name_required', 'Event name is required.'); errEl.style.display = ''; } return; }
    if (!description) { if (errEl) { errEl.textContent = t('groups.events.validation.description_required', 'Description is required.'); errEl.style.display = ''; } return; }
    if (!startVal) { if (errEl) { errEl.textContent = t('groups.events.validation.start_required', 'Start date/time is required.'); errEl.style.display = ''; } return; }
    if (!endVal) { if (errEl) { errEl.textContent = t('groups.events.validation.end_required', 'End date/time is required.'); errEl.style.display = ''; } return; }
    if (new Date(endVal) <= new Date(startVal)) { if (errEl) { errEl.textContent = t('groups.events.validation.end_after_start', 'End must be after start.'); errEl.style.display = ''; } return; }
    if (errEl) errEl.style.display = 'none';

    const btn = document.getElementById('gevSubmitBtn');
    if (btn) { btn.disabled = true; btn.innerHTML = `<span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">hourglass_empty</span>${t('groups.events.submitting', 'Creating...')}`; }

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

/* ============================================================
   GROUP ROLES
   ============================================================ */

const ROLE_PERM_DEFS = [
    { key: 'group-members-manage',               label: 'Manage Group Member Data',            desc: 'View, filter, sort, and edit data about all members.' },
    { key: 'group-data-manage',                  label: 'Manage Group Data',                   desc: 'Edit group details (name, description, joinState, etc).' },
    { key: 'group-audit-view',                   label: 'View Audit Log',                      desc: 'View the full group audit log.' },
    { key: 'group-roles-manage',                 label: 'Manage Group Roles',                  desc: 'Create, modify, and delete roles.' },
    { key: 'group-default-role-manage',          label: 'Manage Default Role',                 desc: 'Manage permissions for the default (Everyone) role.' },
    { key: 'group-roles-assign',                 label: 'Assign Group Roles',                  desc: 'Assign/unassign roles to users. Requires "Manage Group Member Data".' },
    { key: 'group-bans-manage',                  label: 'Manage Group Bans',                   desc: 'Ban/unban users and view all banned users. Requires "Manage Group Member Data".' },
    { key: 'group-members-remove',               label: 'Remove Group Members',                desc: 'Remove someone from the group. Requires "Manage Group Member Data".' },
    { key: 'group-members-viewall',              label: 'View All Members',                    desc: 'View all members in the group, not just friends.' },
    { key: 'group-announcement-manage',          label: 'Manage Group Announcement',           desc: 'Set/clear group announcement and send it as a notification.' },
    { key: 'group-calendar-manage',              label: 'Manage Group Calendar',               desc: 'Create, modify, and publish calendar entries.' },
    { key: 'group-galleries-manage',             label: 'Manage Group Galleries',              desc: 'Create, reorder, edit, and delete group galleries.' },
    { key: 'group-invites-manage',               label: 'Manage Group Invites',                desc: 'Create/cancel invites, accept/decline/block join requests.' },
    { key: 'group-instance-moderate',            label: 'Moderate Group Instances',            desc: 'Moderate within a group instance.' },
    { key: 'group-instance-manage',              label: 'Manage Group Instances',              desc: 'Rename or close a group instance.' },
    { key: 'group-instance-queue-priority',      label: 'Group Instance Queue Priority',       desc: 'Priority for group instance queues.' },
    { key: 'group-instance-age-gated-create',    label: 'Create Age Gated Instances',          desc: 'Create instances requiring age verification (18+).' },
    { key: 'group-instance-public-create',       label: 'Create Public Group Instances',       desc: 'Create instances open to all, member or not.' },
    { key: 'group-instance-plus-create',         label: 'Create Group+ Instances',             desc: 'Create instances that friends of attendees can also join.' },
    { key: 'group-instance-open-create',         label: 'Create Open Group Instances',         desc: 'Create open group instances.' },
    { key: 'group-instance-restricted-create',   label: 'Create Role-Restricted Instances',    desc: 'Create instances restricted to specific roles.' },
    { key: 'group-instance-plus-portal',         label: 'Portal to Group+ Instances',          desc: 'Open locked portals to Group+ instances.' },
    { key: 'group-instance-plus-portal-unlocked',label: 'Unlocked Portal to Group+ Instances', desc: 'Open unlocked portals to Group+ instances.' },
    { key: 'group-instance-join',                label: 'Join Group Instances',                desc: 'Join group instances.' },
];

function getRolePermissionLabel(def) {
    return t(`groups.roles.permissions.${def.key}.label`, def.label);
}

function openGroupRolesModal(groupId) {
    const g = (_groupDetailCache && _groupDetailCache[groupId])
        || window._currentGroupDetailFull
        || window._currentGroupDetail
        || {};
    const roles = Array.isArray(g.roles) ? g.roles : [];

    document.getElementById('groupRolesOverlay')?.remove();
    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    overlay.id = 'groupRolesOverlay';
    overlay.style.zIndex = '10004';
    overlay.style.display = 'flex';
    overlay.addEventListener('click', e => { if (e.target === overlay) closeGroupRolesModal(); });

    const myRoleIds = new Set(Array.isArray(g.myRoleIds) ? g.myRoleIds : []);
    const gid = g.id || groupId || '';
    const body = roles.length
        ? roles.map((r, i) => _buildRoleViewCard(r, i, myRoleIds.has(r.id), gid)).join('')
        : `<div class="myp-empty">${t('groups.roles.empty', 'No roles found')}</div>`;

    overlay.innerHTML = `<div class="gp-modal" style="width:460px;max-width:92vw;max-height:80vh;display:flex;flex-direction:column;">
        ${renderModalBar(t('groups.tabs.roles', 'Roles'), [modalCloseAction('closeGroupRolesModal()')])}
        <div class="gp-modal-body" style="flex:1;overflow-y:auto;">${body}</div>
    </div>`;
    document.body.appendChild(overlay);
}

function closeGroupRolesModal() {
    document.getElementById('groupRolesOverlay')?.remove();
}

function _buildRoleViewCard(role, idx, isMine, groupId) {
    const rid   = 'grv' + idx;
    const badge = (isMine ? `<span class="vrcn-badge ok" style="margin-right:6px;">${t('groups.roles.yours', 'Your role')}</span>` : '')
        + (role.isManagementRole ? `<span class="vrcn-badge" style="margin-right:6px;">${t('groups.roles.system', 'System')}</span>` : '');
    const perms = role.permissions || [];
    const known = new Set(ROLE_PERM_DEFS.map(p => p.key));

    let permsHtml;
    if (perms.includes('*')) {
        permsHtml = `<div style="padding:8px 0;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx2);">${t('groups.roles.full_access', 'This role has full access (all permissions).')}</div>`;
    } else if (!perms.length) {
        permsHtml = `<div style="padding:8px 0;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('groups.roles.no_permissions', 'This role has no permissions.')}</div>`;
    } else {
        const rows = ROLE_PERM_DEFS.filter(p => perms.includes(p.key)).map(p =>
            `<div class="gd-perm-row"><div class="gd-perm-info"><div class="gd-perm-label">${esc(getRolePermissionLabel(p))}</div><div class="gd-perm-desc">${esc(getRolePermissionDescription(p))}</div></div></div>`
        );
        perms.filter(k => !known.has(k)).forEach(k =>
            rows.push(`<div class="gd-perm-row"><div class="gd-perm-info"><div class="gd-perm-label">${esc(k)}</div></div></div>`)
        );
        permsHtml = rows.join('');
    }

    return `<div class="gd-role-card" id="gdrole-${rid}">
        <div class="gd-role-header" onclick="toggleGroupRoleView('${rid}','${jsq(role.id || '')}','${jsq(groupId || '')}')">
            <div style="flex:1;min-width:0;">
                <div class="gd-role-name">${badge}${esc(role.name || '')}</div>
                <div class="gd-role-meta">${esc(getRoleMetaText(role))}</div>
            </div>
            <span class="msi gd-role-chevron" style="font-size:18px;color:var(--tx3);transition:transform .2s;">expand_more</span>
        </div>
        <div class="gd-role-body" id="gdrole-body-${rid}" style="display:none;">
            ${role.description ? `<div class="gd-role-section"><div class="gd-role-section-title">${t('groups.sections.description', 'Description')}</div><div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx2);white-space:pre-wrap;">${esc(role.description)}</div></div>` : ''}
            <div class="gd-role-section">
                <div class="gd-role-section-title">${t('groups.roles.permissions', 'Permissions')}</div>
                ${permsHtml}
            </div>
            <div class="gd-role-section">
                <div class="gd-role-section-title">${t('groups.roles.tabs.members', 'Members')}</div>
                <div id="grv-members-${esc(role.id || '')}"><div style="padding:8px 0;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('common.loading', 'Loading...')}</div></div>
            </div>
        </div>
    </div>`;
}

function toggleGroupRoleView(rid, roleId, groupId) {
    toggleGdRoleExpand(rid);
    const list = document.getElementById('grv-members-' + roleId);
    if (!list || list.dataset.loaded || !groupId || !roleId) return;
    list.dataset.loaded = '1';
    sendToCS({ action: 'vrcGetGroupRoleMembers', groupId, roleId });
}

function getRolePermissionDescription(def) {
    return t(`groups.roles.permissions.${def.key}.desc`, def.desc);
}

function getRoleMetaText(role) {
    const parts = [];
    if ((role.permissions || []).includes('*')) {
        parts.push(`* (${t('groups.roles.meta.all', 'all')})`);
    } else {
        const count = (role.permissions || []).length;
        parts.push(`${count} ${t(count === 1 ? 'groups.roles.meta.permission_one' : 'groups.roles.meta.permission_other', count === 1 ? 'permission' : 'permissions')}`);
    }
    if (role.isAddedOnJoin) parts.push(t('groups.roles.meta.auto_join', 'Auto-join'));
    if (role.isSelfAssignable) parts.push(t('groups.roles.meta.self_assign', 'Self-assign'));
    return parts.join(' - ');
}

function _buildRoleEditor(role, groupId, canManage, isSystemRole) {
    const rid = esc(role.id);
    const gid = jsq(groupId);
    const isOwner = (role.permissions || []).includes('*');
    const dis = (canManage && !isOwner) ? '' : 'disabled';
    const generalHtml = `
    <div class="gd-role-section">
        <div class="gd-role-section-title">${t('groups.roles.general', 'General')}</div>
        <div class="sf-row" style="margin-bottom:8px;">
            <label style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx2);min-width:50px;">${t('groups.roles.name', 'Name')}</label>
            <input id="grole-name-${rid}" class="vrcn-edit-field" value="${esc(role.name)}" maxlength="64" style="flex:1;" ${dis}>
        </div>
        <div class="sf-toggle-row"><span>${t('groups.roles.assign_on_join', 'Assign On Join')}</span>
            <label class="toggle"><input type="checkbox" id="grole-join-${rid}" ${role.isAddedOnJoin ? 'checked' : ''} ${dis}><div class="toggle-track"><div class="toggle-knob"></div></div></label></div>
        <div class="sf-toggle-row"><span>${t('groups.roles.self_assignable', 'Self Assignable')}</span>
            <label class="toggle"><input type="checkbox" id="grole-self-${rid}" ${role.isSelfAssignable ? 'checked' : ''} ${dis}><div class="toggle-track"><div class="toggle-knob"></div></div></label></div>
        <div class="sf-toggle-row"><span>${t('groups.roles.require_2fa', 'Require Two Factor Authentication')}</span>
            <label class="toggle"><input type="checkbox" id="grole-tfa-${rid}" ${role.requiresTwoFactor ? 'checked' : ''} ${dis}><div class="toggle-track"><div class="toggle-knob"></div></div></label></div>
    </div>`;
    const hasWildcard = (role.permissions || []).includes('*');
    const permsHtml = `
    <div class="gd-role-section">
        <div class="gd-role-section-title">${t('groups.roles.permissions', 'Permissions')}</div>
        ${hasWildcard
            ? `<div style="padding:8px 0;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx2);">${t('groups.roles.full_access', 'This role has full access (all permissions).')}</div>`
            : ROLE_PERM_DEFS.map(p => {
                const checked = (role.permissions || []).includes(p.key);
                return `<div class="gd-perm-row">
                    <div class="gd-perm-info"><div class="gd-perm-label">${getRolePermissionLabel(p)}</div><div class="gd-perm-desc">${getRolePermissionDescription(p)}</div></div>
                    <label class="toggle" style="flex-shrink:0;margin-left:12px;"><input type="checkbox" data-perm-key="${p.key}" ${checked ? 'checked' : ''} ${dis}><div class="toggle-track"><div class="toggle-knob"></div></div></label>
                </div>`;
            }).join('')}
    </div>`;
    const canSave = canManage && !isOwner;
    const actionBtns = canSave ? `
    <div style="display:flex;gap:8px;margin-top:14px;justify-content:space-between;">
        ${!isSystemRole ? `<button class="vrcn-button-round vrcn-btn-danger" style="font-size:calc(11px + var(--fs-off, 0px));" onclick="deleteGroupRole('${jsq(role.id)}','${gid}')"><span class="msi" style="font-size:14px;">delete</span> ${t('groups.roles.delete', 'Delete')}</button>` : '<div></div>'}
        <button class="vrcn-button-round vrcn-btn-join" style="font-size:calc(11px + var(--fs-off, 0px));" onclick="saveGroupRole('${jsq(role.id)}','${gid}')"><span class="msi" style="font-size:14px;">save</span> ${t('groups.roles.save_changes', 'Save Changes')}</button>
    </div>` : '';
    return generalHtml + permsHtml + actionBtns;
}

function _buildRoleCard(role, groupId, canManage) {
    const rid = esc(role.id);
    const badge = role.isManagementRole ? `<span class="vrcn-badge" style="margin-right:6px;">${t('groups.roles.system', 'System')}</span>` : '';
    return `<div class="gd-role-card" id="gdrole-${rid}">
        <div class="gd-role-header" onclick="toggleGdRoleExpand('${rid}')">
            <div style="flex:1;"><div class="gd-role-name">${badge}${esc(role.name)}</div><div class="gd-role-meta">${getRoleMetaText(role)}</div></div>
            <span class="msi gd-role-chevron" style="font-size:18px;color:var(--tx3);transition:transform .2s;">expand_more</span>
        </div>
        <div class="gd-role-body" id="gdrole-body-${rid}" style="display:none;">
            <div class="fd-tabs gd-tabs" style="margin:10px 14px 0;">
                <button class="fd-tab active" onclick="switchGdRoleTab('${rid}','settings',this)">${t('groups.roles.tabs.settings', 'Settings')}</button>
                <button class="fd-tab" onclick="switchGdRoleTab('${rid}','members',this)">${t('groups.roles.tabs.members', 'Members')}</button>
            </div>
            <div id="gdrole-settings-${rid}">
                ${_buildRoleEditor(role, groupId, canManage, role.isManagementRole)}
            </div>
            <div id="gdrole-members-${rid}" style="display:none;">
                <div id="gdrole-members-list-${rid}" style="padding:8px 0;">
                    <div style="padding:16px;text-align:center;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('groups.roles.click_load_members', 'Click to load members...')}</div>
                </div>
            </div>
        </div>
    </div>`;
}

function _buildCreateRoleForm(groupId) {
    const gid = jsq(groupId);
    return `<div id="gdRoleCreateForm" style="display:none;margin-bottom:10px;">
        <div class="gd-role-card" style="border:1.5px dashed var(--accent);">
            <div style="padding:12px 14px 0;"><div class="gd-role-section-title">${t('groups.roles.new_role', 'New Role')}</div>
            <div class="sf-row" style="margin-bottom:8px;">
                <label style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx2);min-width:50px;">${t('groups.roles.name', 'Name')}</label>
                <input id="gdNewRoleName" class="vrcn-edit-field" placeholder="${esc(t('groups.roles.name_placeholder', 'Role name...'))}" maxlength="64" style="flex:1;">
            </div>
            <div class="sf-toggle-row"><span>${t('groups.roles.assign_on_join', 'Assign On Join')}</span>
                <label class="toggle"><input type="checkbox" id="gdNewRoleJoin"><div class="toggle-track"><div class="toggle-knob"></div></div></label></div>
            <div class="sf-toggle-row"><span>${t('groups.roles.self_assignable', 'Self Assignable')}</span>
                <label class="toggle"><input type="checkbox" id="gdNewRoleSelf"><div class="toggle-track"><div class="toggle-knob"></div></div></label></div>
            <div class="sf-toggle-row"><span>${t('groups.roles.require_2fa', 'Require Two Factor Authentication')}</span>
                <label class="toggle"><input type="checkbox" id="gdNewRoleTfa"><div class="toggle-track"><div class="toggle-knob"></div></div></label></div>
            <div class="gd-role-section-title" style="margin-top:14px;">${t('groups.roles.permissions', 'Permissions')}</div>
            ${ROLE_PERM_DEFS.map(p => `<div class="gd-perm-row">
                <div class="gd-perm-info"><div class="gd-perm-label">${getRolePermissionLabel(p)}</div><div class="gd-perm-desc">${getRolePermissionDescription(p)}</div></div>
                <label class="toggle" style="flex-shrink:0;margin-left:12px;"><input type="checkbox" class="gdNewRolePerm" data-perm-key="${p.key}"><div class="toggle-track"><div class="toggle-knob"></div></div></label>
            </div>`).join('')}
            <div style="display:flex;gap:8px;margin-top:14px;padding-bottom:14px;">
                <button class="vrcn-button-round" style="font-size:calc(11px + var(--fs-off, 0px));" onclick="closeCreateRoleForm()">${t('common.cancel', 'Cancel')}</button>
                <button class="vrcn-button-round vrcn-btn-join" style="font-size:calc(11px + var(--fs-off, 0px));margin-left:auto;" onclick="submitCreateRole('${gid}')"><span class="msi" style="font-size:14px;">add</span> ${t('groups.roles.create_role', 'Create Role')}</button>
            </div></div>
        </div>
    </div>`;
}

function _buildRolesTab(g) {
    const canManage = g.canManageRoles === true;
    const roles = g.roles || [];
    const createBtn = `<button class="vrcn-button-round" style="margin-bottom:10px;" onclick="openCreateRoleForm()"><span class="msi" style="font-size:14px;">add</span> ${t('groups.roles.create_role', 'Create Role')}</button>`;
    const createForm = _buildCreateRoleForm(g.id);
    const roleCards = roles.length
        ? roles.map(r => _buildRoleCard(r, g.id, canManage)).join('')
        : `<div style="padding:20px;text-align:center;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('groups.roles.empty', 'No roles found')}</div>`;
    return `${createBtn}${createForm}<div id="gdRolesList">${roleCards}</div>`;
}

function toggleGdRoleExpand(roleId) {
    const body    = document.getElementById('gdrole-body-' + roleId);
    const chevron = document.querySelector('#gdrole-' + roleId + ' .gd-role-chevron');
    if (!body) return;
    const open = body.style.display !== 'none';
    body.style.display = open ? 'none' : '';
    if (chevron) chevron.style.transform = open ? '' : 'rotate(180deg)';
}

function switchGdRoleTab(roleId, tab, btn) {
    document.getElementById('gdrole-settings-' + roleId).style.display = tab === 'settings' ? '' : 'none';
    document.getElementById('gdrole-members-'  + roleId).style.display = tab === 'members'  ? '' : 'none';
    btn.closest('.fd-tabs').querySelectorAll('.fd-tab').forEach(b => b.classList.toggle('active', b === btn));
    if (tab === 'members') {
        const listEl = document.getElementById('gdrole-members-list-' + roleId);
        if (listEl && !listEl.dataset.loaded) {
            listEl.dataset.loaded = '1';
            listEl.innerHTML = renderGroupEmptyMessage('common.loading', 'Loading...');
            const g = window._currentGroupDetail;
            if (g) sendToCS({ action: 'vrcGetGroupRoleMembers', groupId: g.id, roleId });
        }
    }
}

function onGroupRoleMembers(data) {
    const targets = [
        document.getElementById('gdrole-members-list-' + data.roleId),
        document.getElementById('grv-members-' + data.roleId),
    ].filter(Boolean);
    if (!targets.length) return;

    const members = (data.members || []).filter(m => Array.isArray(m.roleIds) && m.roleIds.includes(data.roleId));

    const html = members.length === 0
        ? renderGroupEmptyMessage('groups.roles.no_members', 'No members with this role.')
        : members.map(m => renderGroupMemberCard(m)).join('');
    targets.forEach(el => { el.innerHTML = html; });
}

function openCreateRoleForm() {
    const form = document.getElementById('gdRoleCreateForm');
    if (form) { form.style.display = ''; form.querySelector('input')?.focus(); }
}

function closeCreateRoleForm() {
    const form = document.getElementById('gdRoleCreateForm');
    if (form) form.style.display = 'none';
}

function submitCreateRole(groupId) {
    const name = document.getElementById('gdNewRoleName')?.value.trim() || '';
    if (!name) { showToast(false, t('groups.roles.validation.name_required', 'Role name is required')); return; }
    const perms = Array.from(document.querySelectorAll('.gdNewRolePerm:checked')).map(el => el.dataset.permKey);
    sendToCS({
        action: 'vrcCreateGroupRole', groupId, name, description: '',
        permissions: perms,
        isAddedOnJoin:    document.getElementById('gdNewRoleJoin')?.checked || false,
        isSelfAssignable: document.getElementById('gdNewRoleSelf')?.checked || false,
        requiresTwoFactor:document.getElementById('gdNewRoleTfa')?.checked  || false,
    });
}

function saveGroupRole(roleId, groupId) {
    const name = document.getElementById('grole-name-' + roleId)?.value.trim() || '';
    if (!name) { showToast(false, t('groups.roles.validation.name_required', 'Role name is required')); return; }
    const perms = Array.from(document.querySelectorAll(`#gdrole-body-${roleId} [data-perm-key]:checked`)).map(el => el.dataset.permKey);
    sendToCS({
        action: 'vrcUpdateGroupRole', groupId, roleId, name, permissions: perms,
        isAddedOnJoin:    document.getElementById('grole-join-' + roleId)?.checked || false,
        isSelfAssignable: document.getElementById('grole-self-' + roleId)?.checked || false,
        requiresTwoFactor:document.getElementById('grole-tfa-'  + roleId)?.checked || false,
    });
}

function deleteGroupRole(roleId, groupId) {
    const card = document.getElementById('gdrole-' + roleId);
    if (card) { card.style.opacity = '0.4'; card.style.pointerEvents = 'none'; }
    sendToCS({ action: 'vrcDeleteGroupRole', groupId, roleId });
}

function onGroupRoleResult(payload) {
    if (!payload.success) {
        const msg = {
            create: t('groups.roles.result.create_failed', 'Failed to create role'),
            update: t('groups.roles.result.update_failed', 'Failed to save role'),
            delete: t('groups.roles.result.delete_failed', 'Failed to delete role'),
        }[payload.action] || t('groups.roles.result.action_failed', 'Role action failed');
        showToast(false, msg);
        if (payload.action === 'delete') {
            const card = document.getElementById('gdrole-' + payload.roleId);
            if (card) { card.style.opacity = ''; card.style.pointerEvents = ''; }
        }
        return;
    }
    if (payload.action === 'create') {
        showToast(true, t('groups.roles.result.created', 'Role created'));
        closeCreateRoleForm();
        if (payload.role && window._currentGroupDetail) {
            window._currentGroupDetail.roles = [...(window._currentGroupDetail.roles || []), payload.role];
            if (window._currentGroupDetailFull) {
                window._currentGroupDetailFull.roles = [...(window._currentGroupDetailFull.roles || []), payload.role];
            }
            const list = document.getElementById('gdRolesList');
            if (list) list.insertAdjacentHTML('beforeend', _buildRoleCard(payload.role, payload.groupId, true));
        }
    } else if (payload.action === 'update') {
        showToast(true, t('groups.roles.result.saved', 'Role saved'));
        const updatedRole = {
            id: payload.roleId,
            name: document.getElementById('grole-name-' + payload.roleId)?.value.trim() || '',
            permissions: Array.from(document.querySelectorAll(`#gdrole-body-${payload.roleId} [data-perm-key]:checked`)).map(el => el.dataset.permKey),
            isAddedOnJoin: document.getElementById('grole-join-' + payload.roleId)?.checked || false,
            isSelfAssignable: document.getElementById('grole-self-' + payload.roleId)?.checked || false,
            requiresTwoFactor: document.getElementById('grole-tfa-' + payload.roleId)?.checked || false,
        };
        [window._currentGroupDetail, window._currentGroupDetailFull].forEach(group => {
            if (!group?.roles) return;
            group.roles = group.roles.map(role => role.id === payload.roleId ? { ...role, ...updatedRole } : role);
        });
    } else if (payload.action === 'delete') {
        showToast(true, t('groups.roles.result.deleted', 'Role deleted'));
        if (window._currentGroupDetail)
            window._currentGroupDetail.roles = (window._currentGroupDetail.roles || []).filter(r => r.id !== payload.roleId);
        if (window._currentGroupDetailFull)
            window._currentGroupDetailFull.roles = (window._currentGroupDetailFull.roles || []).filter(r => r.id !== payload.roleId);
        document.getElementById('gdrole-' + payload.roleId)?.remove();
    }
}

/* ============================================================
   GROUP BANNED MEMBERS
   ============================================================ */

function _buildBannedTab() {
    return `<div id="gdBannedList">${renderGroupEmptyMessage('common.loading', 'Loading...')}</div>`;
}

// Audit Logs (group-audit-view)

function _buildLogsTab() {
    return `<div id="gdLogsList">${renderGroupEmptyMessage('common.loading', 'Loading...')}</div>
        <div id="gdLogsLoadMore" style="text-align:center;padding:12px;"></div>`;
}

function loadGroupLogs(reset) {
    const gid = window._currentGroupDetail?.id;
    if (!gid) return;
    window._gdLogsLoaded = true;
    if (reset) {
        window._gdLogRows   = [];
        window._gdLogsOffset = 0;
    }
    sendToCS({ action: 'vrcGetGroupLogs', groupId: gid, offset: window._gdLogsOffset || 0 });
}

function loadMoreGroupLogs() {
    const more = document.getElementById('gdLogsLoadMore');
    if (more) more.innerHTML = `<div style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);">${esc(t('common.loading', 'Loading...'))}</div>`;
    loadGroupLogs(false);
}

const _GD_LOG_VERB_PAST = {
    create: 'Created',   update: 'Updated',   delete: 'Deleted',
    remove: 'Removed',   add: 'Added',        edit: 'Edited',
    join: 'Joined',      leave: 'Left',       post: 'Posted',
    assign: 'Assigned',  unassign: 'Unassigned',
    kick: 'Kicked',      ban: 'Banned',       unban: 'Unbanned',
    invite: 'Invited',   accept: 'Accepted',  reject: 'Rejected',
};

// Nouns that read badly straight from the API.
const _GD_LOG_NOUN_ALIAS = { calendarEvent: 'Event' };

function _gdLogTitle(s) {
    return String(s || '')
        .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
        .replace(/\b\w/g, c => c.toUpperCase());
}

// "group.calendarEvent.create" -> "Event Created", "group.member.join" -> "Join".
// The leading group/member segments are noise inside a group's own log and only eat
// column width. The trailing verb is only put in past tense when a noun survives,
// which is what keeps bare actions short.
function _gdLogEventLabel(eventType) {
    const raw = String(eventType || '');
    const key = 'groups.logs.event.' + raw.replace(/\./g, '_');
    const translated = t(key, '');
    if (translated && translated !== key) return translated;

    let parts = raw.split('.').filter(Boolean);
    while (parts.length && (parts[0] === 'group' || parts[0] === 'member')) parts.shift();
    if (!parts.length) return _gdLogTitle(raw.split('.').pop());

    const verb  = parts.pop();
    const nouns = parts.map(p => _GD_LOG_NOUN_ALIAS[p] || _gdLogTitle(p));

    if (!nouns.length) return _gdLogTitle(verb);
    return [...nouns, _GD_LOG_VERB_PAST[verb] || _gdLogTitle(verb)].join(' ');
}

function _gdLogEventColor(eventType) {
    const e = String(eventType || '');
    if (/ban|delete|remove|kick/i.test(e)) return 'var(--err)';
    if (/create|add|join/i.test(e))        return 'var(--ok)';
    if (/update|edit|role/i.test(e))       return 'var(--warn)';
    return 'var(--tx3)';
}

function renderGroupLogs(payload) {
    const list = document.getElementById('gdLogsList');
    const more = document.getElementById('gdLogsLoadMore');
    if (!list) return;

    if (payload?.error) {
        list.innerHTML = renderGroupEmptyMessage('groups.logs.failed', 'Failed to load audit logs.');
        if (more) more.innerHTML = '';
        return;
    }

    const rows = Array.isArray(payload?.logs) ? payload.logs : [];
    window._gdLogRows = (window._gdLogRows || []).concat(rows);
    window._gdLogsOffset = (payload?.offset || 0) + rows.length;

    const all = window._gdLogRows;
    if (!all.length) {
        list.innerHTML = renderGroupEmptyMessage('groups.logs.empty', 'No audit log entries.');
        if (more) more.innerHTML = '';
        return;
    }

    const body = all.map(ev => {
        const dateStr = tlFormatShortDate(ev.created_at);
        const timeStr = tlFormatTime(ev.created_at);
        const dt      = (dateStr || timeStr) ? `${dateStr} | ${timeStr}` : '';
        const name    = ev.actorDisplayName || ev.actorId || '';
        const uid     = ev.actorId || '';

        // tl-av, not tl-list-av: the latter is the stacked variant with a negative
        // margin and bleeds into the neighbouring column.
        const av = ev.actorImage
            ? `<div class="tl-av" style="width:26px;height:26px;background-image:url('${cssUrl(imgThumb(ev.actorImage, 64))}')" title="${esc(name)}"></div>`
            : `<div class="tl-av tl-av-letter" style="width:26px;height:26px;display:flex;align-items:center;justify-content:center;font-size:calc(10px + var(--fs-off, 0px));" title="${esc(name)}">${esc((name || '?')[0].toUpperCase())}</div>`;

        // Same click + context-menu contract as group member cards.
        const userAttrs = uid
            ? ` data-gdlog-user="${esc(uid)}" data-gdlog-name="${esc(name)}" style="cursor:pointer;" onclick="navOpenModal('friend','${jsq(uid)}','${jsq(name)}')"`
            : '';

        return `<tr class="tl-list-row">
            <td class="tl-list-dt">${esc(dt)}</td>
            <td class="tl-list-type"><span class="msi tl-list-icon" style="color:${_gdLogEventColor(ev.eventType)}">history</span><span>${esc(_gdLogEventLabel(ev.eventType))}</span></td>
            <td style="width:34px;padding:4px 8px;"><span${userAttrs}>${av}</span></td>
            <td class="tl-list-user"><span${userAttrs}>${esc(name)}</span></td>
            <td class="tl-list-detail">${esc(ev.description || '')}</td>
        </tr>`;
    }).join('');

    list.innerHTML = `<div class="tl-list-wrap">
        <table class="tl-list-table">
            <colgroup><col style="width:155px"><col style="width:135px"><col style="width:80px"><col style="width:130px"><col></colgroup>
            <thead><tr>
                <th>${esc(t('timeline.list.header.date_time', 'Date / Time'))}</th>
                <th>${esc(t('timeline.list.header.type', 'Type'))}</th>
                <th>${esc(t('timeline.list.header.profile', 'Profile'))}</th>
                <th>${esc(t('timeline.list.header.user', 'User'))}</th>
                <th>${esc(t('timeline.list.header.detail', 'Detail'))}</th>
            </tr></thead>
            <tbody>${body}</tbody>
        </table>
    </div>`;

    if (more) {
        more.innerHTML = payload?.hasNext
            ? `<button class="vrcn-button" onclick="loadMoreGroupLogs()">${esc(t('groups.logs.load_more', 'Load More Logs'))}</button>`
            : `<div style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);">${esc(t('groups.logs.all_loaded', 'All log entries loaded'))}</div>`;
    }
}

function loadGroupBans() {
    if (!window._currentGroupDetail?.id) return;
    window._gdBannedLoaded = true;
    sendToCS({ action: 'vrcGetGroupBans', groupId: window._currentGroupDetail.id });
}

function renderGroupBans(groupId, bans) {
    const list = document.getElementById('gdBannedList');
    if (!list) return;
    if (!bans || bans.length === 0) {
        list.innerHTML = renderGroupEmptyMessage('groups.empty.no_banned_members', 'No banned members');
        return;
    }
    list.innerHTML = bans.map(b => renderProfileItem(b, `navOpenModal('friend','${jsq(b.id || '')}','${jsq(b.displayName || '')}')`)).join('');
}

/* === Group Invite (reuses modalInvite / inviteBox) === */
let _grpInvGroupId = null;

function openGroupInviteModal(groupId) {
    _grpInvGroupId = groupId;
    const m = document.getElementById('modalInvite');
    if (!m) return;
    _inviteSelected = new Set();
    _inviteSending = false;
    _inviteFilter = '';
    _inviteProgressState = null;
    _inviteOverride = null;
    _renderGroupInviteBox();
    m.style.display = 'flex';
}

function _renderGroupInviteBox() {
    const box = document.getElementById('inviteBox');
    if (!box) return;
    const gd = window._currentGroupDetailFull || window._currentGroupDetail || {};
    const groupName = gd.name || '';
    const groupIcon = gd.iconUrl || '';
    const bannerUrl = gd.bannerUrl || '';
    const bannerBg = bannerUrl || groupIcon;
    box.innerHTML = `
        ${renderModalBar(groupName, [modalCloseAction('closeInviteModal();_grpInvGroupId=null;')], { flush: true })}
        <div class="inv-world-banner" style="background-image:url('${esc(bannerBg)}')">
            <div class="inv-world-fade"></div>
            <div class="inv-world-info">
                <div class="inv-world-name">${esc(groupName)}</div>
                <div style="font-size:calc(10px + var(--fs-off, 0px));color:rgba(255,255,255,.65);margin-top:3px;">${esc(t('groups.invite.subtitle', 'Invite to this group'))}</div>
            </div>
        </div>
        <div class="inv-search-wrap">
            <span class="msi inv-search-icon">search</span>
            <input type="text" id="inviteSearch" class="inv-search-input" placeholder="${esc(t('invite.multi.search_placeholder', 'Search friends...'))}" oninput="_dbFilterInvite()">
        </div>
        <div id="inviteList" class="inv-list"></div>
        <div class="inv-footer">
            <span id="inviteSelCount" class="inv-sel-count"></span>
            <button id="inviteSendBtn" class="vrcn-button" onclick="_sendGroupInvites()" disabled>${esc(t('groups.actions.invite', 'Invite'))}</button>
        </div>
        <div id="inviteProgress" class="inv-progress-wrap" style="display:none;">
            <div class="inv-progress-track"><div id="inviteProgressBar" class="inv-progress-bar"></div></div>
            <div id="inviteProgressText" class="inv-progress-text"></div>
        </div>`;
    const search = document.getElementById('inviteSearch');
    if (search) search.value = _inviteFilter;
    renderInviteList(_inviteFilter);
}

function _sendGroupInvites() {
    const ids = Array.from(_inviteSelected);
    if (!ids.length || _inviteSending || !_grpInvGroupId) return;
    _inviteSending = true;
    const btn = document.getElementById('inviteSendBtn');
    if (btn) btn.disabled = true;
    const prog = document.getElementById('inviteProgress');
    if (prog) prog.style.display = '';
    _applyInviteProgress(0, ids.length, 0, 0);
    sendToCS({ action: 'vrcInviteToGroup', groupId: _grpInvGroupId, userIds: ids });
}

function handleGroupInviteProgress(payload) {
    _applyInviteProgress(payload.done, payload.total, payload.success, payload.fail);
    if (payload.error) showToast(false, payload.error);
    if (payload.done >= payload.total) {
        _inviteSending = false;
        _grpInvGroupId = null;
        setTimeout(() => {
            const count = _inviteSelected.size;
            const btn = document.getElementById('inviteSendBtn');
            if (btn) { btn.disabled = count === 0; }
        }, 1500);
    }
}

function _gdWmState(s) {
    if (s === undefined) return {
        detail:         window._currentGroupDetail,
        detailFull:     window._currentGroupDetailFull,
        membersGroupId: window._gdMembersGroupId,
        membersOffset:  window._gdMembersOffset,
        membersSearch:  window._gdMembersSearchActive,
        memberRoleIds:  window._gdMemberRoleIds,
        bannedLoaded:   window._gdBannedLoaded,
        logsLoaded:     window._gdLogsLoaded,
        logsOffset:     window._gdLogsOffset,
        logRows:        window._gdLogRows,
    };
    s = s || {};
    window._currentGroupDetail     = s.detail         ?? null;
    window._currentGroupDetailFull = s.detailFull     ?? null;
    window._gdMembersGroupId       = s.membersGroupId ?? null;
    window._gdMembersOffset        = s.membersOffset  ?? 0;
    window._gdMembersSearchActive  = s.membersSearch  ?? false;
    window._gdMemberRoleIds        = s.memberRoleIds  ?? null;
    window._gdBannedLoaded         = s.bannedLoaded   ?? false;
    window._gdLogsLoaded           = s.logsLoaded     ?? false;
    window._gdLogsOffset           = s.logsOffset     ?? 0;
    window._gdLogRows              = s.logRows        ?? null;
}

function confirmDeleteGroup(groupId, groupName) {
    vrcnConfirmDelete({
        id: 'groupDeleteModal',
        title: t('groups.actions.delete_group', 'Delete Group'),
        icon: 'delete',
        message: tf('groups.actions.delete_confirm', { name: groupName || groupId },
            'Delete the group "{name}"? Every member loses access and this cannot be undone.'),
        confirmLabel: t('common.delete', 'Delete'),
        onConfirm: () => sendToCS({ action: 'vrcDeleteGroup', groupId }),
    });
}

function onGroupDeleteResult(data) {
    if (typeof groupBulkDeleteConsume === 'function' && groupBulkDeleteConsume(!!data.ok)) return;
    if (data.ok) {
        showToast(true, t('groups.actions.toast.deleted', 'Group deleted'));
        closeGroupDetail();
        if (typeof myGroups !== 'undefined') myGroups = myGroups.filter(g => g.id !== data.groupId);
        if (typeof filterMyGroups === 'function') filterMyGroups();
        if (typeof loadMyGroups === 'function') loadMyGroups();
    } else {
        showToast(false, data.error || t('groups.actions.toast.delete_failed', 'Delete failed'));
    }
}
