/* === Join State === */
function getJoinStateLabel(js) {
    const map = {
        open: ['groups.join_state.open', 'Open'],
        closed: ['groups.join_state.closed', 'Closed'],
        invite: ['groups.join_state.invite_only', 'Invite Only'],
        request: ['groups.join_state.request_invite', 'Request Invite'],
    };
    const entry = map[js];
    return entry ? t(entry[0], entry[1]) : (js || '?');
}

function getGroupMembersText(count) {
    return tf('worlds.groups.members', { count }, '{count} members');
}

function joinStateBadge(js) {
    const map = {
        open:    { label: getJoinStateLabel('open'), cls: 'public'  },
        closed:  { label: getJoinStateLabel('closed'), cls: 'private' },
        invite:  { label: getJoinStateLabel('invite'), cls: 'friends' },
        request: { label: getJoinStateLabel('request'), cls: 'group'   },
    };
    const m = map[js] || { label: getJoinStateLabel(js), cls: 'hidden' };
    return `<span class="vrcn-badge ${m.cls}">${esc(m.label)}</span>`;
}

/* === My Groups === */

function _renderGroupListCard(g) {
    const metaParts = [];
    if (g.shortCode) metaParts.push(esc(g.shortCode));
    metaParts.push(`<span class="msi" style="font-size:12px;">group</span> ${esc(getGroupMembersText(g.memberCount || 0))}`);
    const iconHtml = g.iconUrl ? `<div class="cc-group-icon" style="background-image:url('${cssUrl(imgThumb(g.iconUrl, 64))}')"></div>` : '';
    if (_groupEditMode) {
        const isSelected = _groupEditSelected.has(g.id);
        const checkIcon = isSelected
            ? `<span class="msi" style="font-size:22px;color:var(--accent);">check_circle</span>`
            : `<span class="msi" style="font-size:22px;color:rgba(255,255,255,0.7);">radio_button_unchecked</span>`;
        return `<div class="vrcn-content-card" data-gid="${esc(g.id)}" onclick="toggleGroupEditSelect('${jsq(g.id)}',this)" style="user-select:none;">
            <div class="cc-bg"><img src="${imgThumb(g.bannerUrl, 256)||'fallback_cover.png'}" loading="lazy" decoding="async" onerror="this.src='fallback_cover.png'" style="position:absolute;inset:0;width:100%;height:100%;object-fit:cover;"></div>
            <div class="cc-scrim"></div>
            <div class="wd-edit-check">${checkIcon}</div>
            <div class="cc-content">
                <div class="cc-name">${esc(g.name)}</div>
                <div class="cc-bottom-row">
                    <div class="cc-meta">${iconHtml}${metaParts.join(' · ')}</div>
                </div>
            </div>
            ${isSelected ? '<div class="wd-edit-sel-border"></div>' : ''}</div>`;
    }
    return `<div class="vrcn-content-card" onclick="openGroupDetail('${esc(g.id)}')">
        <div class="cc-bg"><img src="${imgThumb(g.bannerUrl, 256)||'fallback_cover.png'}" loading="lazy" decoding="async" onerror="this.src='fallback_cover.png'" style="position:absolute;inset:0;width:100%;height:100%;object-fit:cover;"></div>
        <div class="cc-scrim"></div>
        <div class="cc-content">
            <div class="cc-name">${esc(g.name)}</div>
            <div class="cc-bottom-row">
                <div class="cc-meta">${iconHtml}${metaParts.join(' · ')}</div>
            </div>
        </div>
    </div>`;
}

let _groupTab = 'joined';
let _groupEditMode = false;
let _groupEditSelected = new Set();

function toggleGroupEditMode() {
    if (_groupEditMode) { exitGroupEditMode(); return; }
    _groupEditMode = true;
    _groupEditSelected = new Set();
    const btn = document.getElementById('groupEditModeBtn');
    if (btn) { btn.innerHTML = `<span class="msi" style="font-size:16px;">check</span> <span>${t('groups.edit.done', 'Done')}</span>`; btn.classList.add('active'); }
    const bar = document.getElementById('groupEditBar');
    if (bar) bar.style.display = 'flex';
    filterMyGroups();
    updateGroupEditBar();
}

function exitGroupEditMode() {
    _groupEditMode = false;
    _groupEditSelected = new Set();
    const btn = document.getElementById('groupEditModeBtn');
    if (btn) { btn.innerHTML = `<span class="msi" style="font-size:16px;">edit</span> <span>${t('groups.edit.button', 'Edit')}</span>`; btn.classList.remove('active'); }
    const bar = document.getElementById('groupEditBar');
    if (bar) bar.style.display = 'none';
    filterMyGroups();
}

function toggleGroupEditSelect(id, el) {
    if (_groupEditSelected.has(id)) {
        _groupEditSelected.delete(id);
        const chk = el?.querySelector('.wd-edit-check .msi');
        if (chk) { chk.textContent = 'radio_button_unchecked'; chk.style.color = 'rgba(255,255,255,0.7)'; }
        el?.querySelector('.wd-edit-sel-border')?.remove();
    } else {
        _groupEditSelected.add(id);
        const chk = el?.querySelector('.wd-edit-check .msi');
        if (chk) { chk.textContent = 'check_circle'; chk.style.color = 'var(--accent)'; }
        if (el && !el.querySelector('.wd-edit-sel-border')) {
            el.insertAdjacentHTML('beforeend', '<div class="wd-edit-sel-border"></div>');
        }
    }
    updateGroupEditBar();
}

function _groupEditVisibleList() {
    const q = (document.getElementById('filterGroupsInput')?.value || '').toLowerCase();
    const base = _groupTabList();
    return q
        ? base.filter(g => (g.name || '').toLowerCase().includes(q) || (g.shortCode || '').toLowerCase().includes(q))
        : base;
}

function groupEditSelectAll() {
    const list = _groupEditVisibleList();
    const allSel = list.length > 0 && list.every(g => _groupEditSelected.has(g.id));
    if (allSel) list.forEach(g => _groupEditSelected.delete(g.id));
    else list.forEach(g => _groupEditSelected.add(g.id));
    filterMyGroups();
    updateGroupEditBar();
}

function updateGroupEditBar() {
    const count = _groupEditSelected.size;
    const countEl = document.getElementById('groupEditCount');
    if (countEl) countEl.textContent = tf('groups.edit.selected', { count }, '{count} selected');
    const selectAllBtn = document.getElementById('groupEditSelectAllBtn');
    if (selectAllBtn) {
        const list = _groupEditVisibleList();
        const allSel = list.length > 0 && list.every(g => _groupEditSelected.has(g.id));
        selectAllBtn.textContent = allSel ? t('groups.edit.deselect_all', 'Deselect All') : t('groups.edit.select_all', 'Select All');
    }
    document.querySelectorAll('.gr-edit-action').forEach(b => b.disabled = count === 0);
    _groupEditSyncButtons();
}

function groupEditLeaveSelected() {
    const ids = [..._groupEditSelected];
    if (!ids.length) return;
    const names = ids
        .map(id => (myGroups.find(g => g.id === id)?.name) || id)
        .slice(0, 6);
    const more = ids.length - names.length;
    const listHtml = names.map(n => `<div>${esc(n)}</div>`).join('')
        + (more > 0 ? `<div>${esc(tf('groups.edit.bulk_leave_more', { count: more }, '+{count} more'))}</div>` : '');

    const old = document.getElementById('groupBulkLeaveModal');
    if (old) old.remove();
    const o = document.createElement('div');
    o.className = 'modal-overlay';
    o.style.display = 'flex';
    o.id = 'groupBulkLeaveModal';
    o.style.zIndex = '10003';
    o.onclick = e => { if (e.target === o) o.remove(); };
    o.innerHTML = `<div class="modal-box">
        ${renderModalBar(t('groups.edit.bulk_leave', 'Bulk Leave'), [modalCloseAction("document.getElementById('groupBulkLeaveModal').remove()")])}
        <div class="modal-icon danger" style="margin-top:20px;"><span class="msi" style="font-size:22px;">logout</span></div>
        <div class="modal-msg">${tf('groups.edit.bulk_leave_confirm', { count: ids.length }, 'Leave {count} groups? This cannot be undone.')}</div>
        <div class="gr-bulk-list">${listHtml}</div>
        <div class="modal-btns">
            <button class="vrcn-button-round" onclick="document.getElementById('groupBulkLeaveModal').remove()">${esc(t('common.cancel', 'Cancel'))}</button>
            <button class="vrcn-button-round vrcn-btn-danger" onclick="groupEditLeaveConfirmed()">${esc(t('groups.edit.bulk_leave', 'Bulk Leave'))}</button>
        </div></div>`;
    document.body.appendChild(o);
}

let _groupBulkLeavePending = 0;
let _groupBulkLeaveOk = 0;

function groupEditLeaveConfirmed() {
    document.getElementById('groupBulkLeaveModal')?.remove();
    const ids = [..._groupEditSelected];
    if (!ids.length) return;
    _groupBulkLeavePending = ids.length;
    _groupBulkLeaveOk = 0;
    ids.forEach(id => sendToCS({ action: 'vrcLeaveGroup', groupId: id }));
    const leaving = new Set(ids);
    myGroups = myGroups.filter(g => !leaving.has(g.id));
    exitGroupEditMode();
}

function groupBulkLeaveConsume(success) {
    if (_groupBulkLeavePending <= 0) return false;
    _groupBulkLeavePending--;
    if (success) _groupBulkLeaveOk++;
    if (_groupBulkLeavePending === 0) {
        const done = _groupBulkLeaveOk;
        if (typeof showToast === 'function') {
            showToast(done > 0, tf('groups.edit.bulk_leave_done', { count: done }, 'Left {count} groups'));
        }
        loadMyGroups();
    }
    return true;
}

const _GROUP_TAB_BTNS = { joined: 'groupFilterJoined', mine: 'groupFilterMine', mod: 'groupFilterMod', search: 'groupFilterSearch', instances: 'groupFilterInstances' };

function setGroupFilter(filter) {
    if (!_GROUP_TAB_BTNS[filter]) filter = 'joined';
    if (_groupEditMode) exitGroupEditMode();
    const editBtn = document.getElementById('groupEditModeBtn');
    if (editBtn) editBtn.style.display = (filter === 'search' || filter === 'instances') ? 'none' : '';
    for (const [key, id] of Object.entries(_GROUP_TAB_BTNS)) {
        document.getElementById(id)?.classList.toggle('active', key === filter);
    }
    const isSearch = filter === 'search';
    const isInstances = filter === 'instances';
    document.getElementById('groupMineArea').style.display      = (isSearch || isInstances) ? 'none' : '';
    document.getElementById('groupSearchArea').style.display    = isSearch ? '' : 'none';
    document.getElementById('groupInstancesArea').style.display = isInstances ? '' : 'none';
    if (isInstances) {
        _groupTab = filter;
        const glBtn2 = document.getElementById('groupViewList');
        if (glBtn2) glBtn2.style.display = '';
        _groupsSyncViewBtns();
        renderGroupInstancesView();
        if (typeof _dashGroupInstances !== 'undefined' && _dashGroupInstances === null && typeof loadDashGroupInstances === 'function') loadDashGroupInstances();
        return;
    }
    const glBtn = document.getElementById('groupViewList');
    if (glBtn) glBtn.style.display = '';
    if (isSearch) {
        renderGroupsListView();
        document.getElementById('searchGroupsInput')?.focus();
        return;
    }
    _groupTab = filter;
    filterMyGroups();
    if (!myGroupsLoaded) loadMyGroups();
}

function _groupIsOwn(g) {
    const meId = (typeof currentVrcUser !== 'undefined' && currentVrcUser) ? currentVrcUser.id : '';
    return !!(meId && g.ownerId === meId);
}

function _groupCanModerate(g) {
    return !!(g.canKick || g.canBan || g.canEdit || g.canManageRoles || g.canAssignRoles
           || g.canViewAudit || g.canModInstance || g.canManageMembers || g.canPost || g.canEvent);
}

function _groupTabList() {
    if (_groupTab === 'mine') return myGroups.filter(_groupIsOwn);
    if (_groupTab === 'mod')  return myGroups.filter(g => !_groupIsOwn(g) && _groupCanModerate(g));
    return myGroups;
}

function _groupEmptyMsg() {
    if (_groupTab === 'mine') return t('groups.mine.empty_owned', 'You have not created any groups');
    if (_groupTab === 'mod')  return t('groups.mine.empty_moderate', 'No groups where you have moderation rights');
    return t('groups.mine.empty_joined', 'No groups joined');
}

document.documentElement.addEventListener('languagechange', () => {
    if (typeof myGroupsLoaded !== 'undefined' && myGroupsLoaded && typeof myGroups !== 'undefined' && Array.isArray(myGroups)) {
        filterMyGroups();
    }
    if (document.getElementById('gdTabInfo') && window._currentGroupDetailFull) {
        renderGroupDetail(window._currentGroupDetailFull);
    }
});

let _groupsPage = 0;

function setGroupsViewMode(mode) {
    lvSetViewMode('groups', mode);
    _groupsPage = 0; _groupInstPage = 0;
    _groupsSyncViewBtns();
    renderGroupsListView();
}

function renderGroupsListView() {
    if (document.getElementById('groupFilterSearch')?.classList.contains('active')) {
        const st = searchState?.groups;
        if (st && st.results && st.results.length) renderSearchResults('groups', st.results, 0, st.hasMore);
        return;
    }
    if (_groupTab === 'instances') { renderGroupInstancesView(); return; }
    filterMyGroups();
}

function _groupsSyncViewBtns() {
    const isList = lvViewMode('groups') === 'list';
    document.getElementById('groupViewList')?.classList.toggle('active', isList);
    if (isList) {
        document.getElementById('groupGridLarge')?.classList.remove('active');
        document.getElementById('groupGridSmall')?.classList.remove('active');
    } else {
        const compact = localStorage.getItem('vrcn_gridSize_groups') === 'compact';
        document.getElementById('groupGridLarge')?.classList.toggle('active', !compact);
        document.getElementById('groupGridSmall')?.classList.toggle('active', compact);
    }
}

function setGroupsListPageSize(v) { lvSetPageSize('groups', v, () => { _groupsPage = 0; _groupInstPage = 0; renderGroupsListView(); }); }
function groupsGoPage(p) { if (p < 0) return; _groupsPage = p; filterMyGroups(); document.getElementById('myGroupsGrid')?.scrollTo(0, 0); }

function _glValue(g, field) {
    switch (field) {
        case 'name':    return (g.name || '').toLowerCase();
        case 'short':   return (g.shortCode || '').toLowerCase();
        case 'members': return g.memberCount || 0;
        case 'joined':  return Date.parse(g.joinedAt || '') || 0;
        case 'created': return Date.parse(g.createdAt || '') || 0;
        default:        return (g.name || '').toLowerCase();
    }
}

function buildGroupsListHtml(groups, staticHeader) {
    let rows = '';
    groups.forEach(g => {
        const gid = jsq(g.id || '');
        rows += tlTableRow('groupsList', ` data-gid="${esc(g.id || '')}" onclick="openGroupDetail('${gid}')"`, {
            icon:    `<td>${lvIcon(g.iconUrl, g.name, true)}</td>`,
            name:    `<td class="lv-name">${esc(g.name || '')}</td>`,
            short:   `<td class="lv-sub">${esc(g.shortCode || '')}</td>`,
            members: `<td class="lv-num">${esc((g.memberCount || 0).toLocaleString())}</td>`,
            joined:  `<td class="lv-sub">${esc(_glDate(g.joinedAt))}</td>`,
            created: `<td class="lv-sub">${esc(_glDate(g.createdAt))}</td>`,
        });
    });
    return `<div class="lv-scroll">${tlTableHtml('groupsList', rows, staticHeader)}</div>`;
}

let _myGroupsDirty = false;
function filterMyGroups() {
    const tab = document.getElementById('tab2');
    if (tab && !tab.classList.contains('active')) { _myGroupsDirty = true; return; }
    _myGroupsDirty = false;
    const q = (document.getElementById('filterGroupsInput')?.value || '').toLowerCase();
    const el = document.getElementById('myGroupsGrid');
    if (!el) return;
    const base = _groupTabList();
    const filtered = q
        ? base.filter(g => (g.name||'').toLowerCase().includes(q) || (g.shortCode||'').toLowerCase().includes(q))
        : base;
    if (!filtered.length) {
        el.classList.add('search-grid');
        el.innerHTML = q ? emptyStateHtml('search', esc(t('groups.mine.empty_match', 'No groups match'))) : emptyStateHtml('groups', esc(_groupEmptyMsg()));
        setPaginator('groupsPaginatorBar', '');
        return;
    }
    if (lvViewMode('groups') === 'list' && lvReady()) {
        const sorted = lvSort(filtered, 'groupsList', _glValue);
        const size = lvPageSize('groups');
        const totalPages = Math.ceil(sorted.length / size) || 1;
        if (_groupsPage >= totalPages) _groupsPage = totalPages - 1;
        if (_groupsPage < 0) _groupsPage = 0;
        el.classList.remove('search-grid');
        el.innerHTML = buildGroupsListHtml(sorted.slice(_groupsPage * size, (_groupsPage + 1) * size));
        lvEditDecorateList(el, 'groups');
        setPaginator('groupsPaginatorBar', lvPaginator('groups', _groupsPage, totalPages, 'groupsGoPage', sorted.length, 'setGroupsListPageSize'));
        return;
    }
    setPaginator('groupsPaginatorBar', '');
    el.classList.add('search-grid');
    el.innerHTML = filtered.map(_renderGroupListCard).join('');
}

function loadMyGroups() {
    sendToCS({ action: 'vrcGetMyGroups' });
}


function _groupInstValue(inst, field) {
    switch (field) {
        case 'group':   return (inst.groupName || '').toLowerCase();
        case 'world':   return (inst.worldName || '').toLowerCase();
        case 'type':    return (typeof parseFriendLocation === 'function' ? parseFriendLocation(inst.location || '').instanceType : '');
        case 'players': return inst.userCount || 0;
        default:        return inst.userCount || 0;
    }
}

let _groupInstPage = 0;
let _groupInstDirty = false;

function groupInstGoPage(p) { if (p < 0) return; _groupInstPage = p; renderGroupInstancesView(); document.getElementById('groupInstancesGrid')?.scrollTo(0, 0); }

function buildGroupInstancesListHtml(list) {
    let rows = '';
    list.forEach(inst => {
        const loc = jsq(inst.location || '');
        const { cls, label } = getInstanceBadge(parseFriendLocation(inst.location || '').instanceType);
        const users = inst.capacity > 0 ? `${inst.userCount}/${inst.capacity}` : String(inst.userCount || 0);
        rows += tlTableRow('groupInstList', ` onclick="openGroupInstanceDetail('${loc}')"`, {
            icon:    `<td>${lvIcon(inst.groupIcon, inst.groupName, true)}</td>`,
            group:   `<td class="lv-name">${esc(inst.groupName || '?')}</td>`,
            world:   `<td class="lv-name">${esc(inst.worldName || '')}</td>`,
            type:    `<td>${label ? `<span class="vrcn-badge ${cls}">${esc(label)}</span>` : ''}</td>`,
            players: `<td class="lv-num">${esc(users)}</td>`,
        });
    });
    return `<div class="lv-scroll">${tlTableHtml('groupInstList', rows)}</div>`;
}

function _groupInstCardHtml(inst) {
    const loc = jsq(inst.location || '');
    const thumbStyle = inst.worldThumb ? `background-image:url('${cssUrl(imgThumb(inst.worldThumb, 256))}')` : '';
    const { instanceType } = parseFriendLocation(inst.location || '');
    const { cls, label } = getInstanceBadge(instanceType);
    const region = ((inst.location || '').match(/~region\(([^)]+)\)/) || [])[1] || '';
    const regionBadge = region ? `<span class="vrcn-badge" style="background:rgba(0,0,0,.45);color:rgba(255,255,255,.85);">${esc(region.toUpperCase())}</span>` : '';
    const ageGate = (inst.location || '').includes('~ageGate')
        ? `<span class="vrcn-badge" style="background:rgba(255,75,85,.15);color:var(--err);">${esc(t('worlds.instances.age_gated', 'Age Gated'))}</span>` : '';
    const groupAvatar = inst.groupIcon
        ? `<img class="cc-friend-av" src="${imgThumb(inst.groupIcon, 64)}" title="${esc(inst.groupName || '')}" onerror="this.style.display='none'">`
        : `<div class="cc-friend-av" style="display:flex;align-items:center;justify-content:center;"><span class="msi" style="font-size:10px;color:var(--tx2)">group</span></div>`;
    const users = inst.capacity > 0 ? `${inst.userCount}/${inst.capacity}` : String(inst.userCount || 0);
    return `<div class="vrcn-content-card" onclick="openGroupInstanceDetail('${loc}')">
        <div class="cc-bg" style="${thumbStyle}"></div>
        <div class="cc-scrim"></div>
        <div class="cc-badges-top">${label ? `<span class="vrcn-badge ${cls}">${esc(label)}</span>` : ''}${regionBadge}${ageGate}</div>
        <div class="cc-content">
            <div class="cc-name">${esc(inst.worldName || '?')}</div>
            <div class="cc-friends-row">${groupAvatar}<span class="cc-extra" style="background:transparent;color:rgba(255,255,255,.85);padding-left:4px;">${esc(inst.groupName || '')}</span></div>
            <div class="cc-bottom-row">
                <div class="cc-meta"><span class="msi">person</span>${esc(users)}</div>
            </div>
        </div>
    </div>`;
}

function renderGroupInstancesView() {
    const tab = document.getElementById('tab2');
    if (tab && !tab.classList.contains('active')) { _groupInstDirty = true; return; }
    _groupInstDirty = false;
    const el = document.getElementById('groupInstancesGrid');
    if (!el) return;
    const raw = (typeof _dashGroupInstances !== 'undefined') ? _dashGroupInstances : null;
    const q = (document.getElementById('filterGroupInstancesInput')?.value || '').toLowerCase();
    const data = (raw && q)
        ? raw.filter(i => (i.groupName || '').toLowerCase().includes(q) || (i.worldName || '').toLowerCase().includes(q))
        : raw;
    if (data === null) {
        el.classList.add('search-grid');
        el.innerHTML = `<div class="empty-msg">${t('dashboard.discovery.loading', 'Loading worlds...')}</div>`;
        setPaginator('groupInstancesPaginatorBar', '');
        return;
    }
    if (!data.length) {
        el.classList.add('search-grid');
        el.innerHTML = emptyStateHtml('groups', t('dashboard.section.group_activity_empty', 'No active group instances right now'));
        setPaginator('groupInstancesPaginatorBar', '');
        return;
    }
    if (lvViewMode('groups') === 'list' && lvReady()) {
        const sorted = lvSort(data, 'groupInstList', _groupInstValue);
        const size = lvPageSize('groups');
        const totalPages = Math.ceil(sorted.length / size) || 1;
        if (_groupInstPage >= totalPages) _groupInstPage = totalPages - 1;
        if (_groupInstPage < 0) _groupInstPage = 0;
        el.classList.remove('search-grid');
        el.innerHTML = buildGroupInstancesListHtml(sorted.slice(_groupInstPage * size, (_groupInstPage + 1) * size));
        setPaginator('groupInstancesPaginatorBar', lvPaginator('groups', _groupInstPage, totalPages, 'groupInstGoPage', sorted.length, 'setGroupsListPageSize'));
        return;
    }
    setPaginator('groupInstancesPaginatorBar', '');
    el.classList.add('search-grid');
    el.innerHTML = data.map(_groupInstCardHtml).join('');
}

function refreshGroups() {
    const btn = document.getElementById('groupsRefreshBtn');
    if (btn) { btn.disabled = true; btn.querySelector('.msi').textContent = 'hourglass_empty'; }
    if (_groupTab === 'instances') {
        window._groupInstInFlight = true;
        sendToCS({ action: 'vrcGetDashGroupInstances' });
        return;
    }
    sendToCS({ action: 'vrcGetMyGroups', force: true });
}

function renderMyGroups(list) {
    const btn = document.getElementById('groupsRefreshBtn');
    if (btn) { btn.disabled = false; btn.querySelector('.msi').textContent = 'refresh'; }
    myGroups = list || [];
    myGroupsLoaded = true;
    lvKeepScroll(document.getElementById('myGroupsGrid'), () => filterMyGroups());
    if (typeof renderDashUpcomingEvents === 'function') renderDashUpcomingEvents();
}


_groupsSyncViewBtns();

lvEditRegister('groups', {
    attr: 'data-gid',
    isActive: () => _groupEditMode,
    isSelected: id => _groupEditSelected.has(id),
    toggle: id => { if (_groupEditSelected.has(id)) _groupEditSelected.delete(id); else _groupEditSelected.add(id); },
    onChange: () => updateGroupEditBar(),
});

function _groupEditSyncButtons() {
    const btn = document.getElementById('groupEditDeleteBtn');
    if (btn) btn.style.display = _groupTab === 'mine' ? '' : 'none';
}

let _groupBulkDeletePending = 0;
let _groupBulkDeleteOk = 0;

function groupEditDeleteSelected() {
    const ids = [..._groupEditSelected].filter(id => {
        const g = myGroups.find(x => x.id === id);
        return g && _groupIsOwn(g);
    });
    if (!ids.length) {
        showToast(false, t('groups.edit.bulk_delete_none', 'Only groups you own can be deleted'));
        return;
    }
    const names = ids.map(id => (myGroups.find(g => g.id === id)?.name) || id).slice(0, 6);
    const more = ids.length - names.length;
    const listHtml = names.map(n => `<div>${esc(n)}</div>`).join('')
        + (more > 0 ? `<div>${esc(tf('groups.edit.bulk_delete_more', { count: more }, '+{count} more'))}</div>` : '');

    vrcnConfirmDelete({
        id: 'groupBulkDeleteModal',
        title: t('groups.edit.bulk_delete', 'Bulk Delete'),
        icon: 'delete',
        message: tf('groups.edit.bulk_delete_confirm', { count: ids.length },
            'Delete {count} groups? Every member loses access and this cannot be undone.'),
        listHtml,
        confirmLabel: t('groups.edit.bulk_delete', 'Bulk Delete'),
        onConfirm: () => {
            _groupBulkDeletePending = ids.length;
            _groupBulkDeleteOk = 0;
            ids.forEach(groupId => sendToCS({ action: 'vrcDeleteGroup', groupId }));
            const gone = new Set(ids);
            myGroups = myGroups.filter(g => !gone.has(g.id));
            exitGroupEditMode();
        },
    });
}

function groupBulkDeleteConsume(success) {
    if (_groupBulkDeletePending <= 0) return false;
    _groupBulkDeletePending--;
    if (success) _groupBulkDeleteOk++;
    if (_groupBulkDeletePending === 0) {
        showToast(_groupBulkDeleteOk > 0, tf('groups.edit.bulk_delete_done', { count: _groupBulkDeleteOk }, 'Deleted {count} groups'));
        if (typeof loadMyGroups === 'function') loadMyGroups();
    }
    return true;
}

function _glDate(value) {
    if (!value) return '';
    const raw = /^\d{4}-\d{2}-\d{2}$/.test(value) ? value + 'T00:00:00' : value;
    const d = new Date(raw);
    return isNaN(d) ? String(value) : fmtShortDate(d);
}

/* === Create Group === */
let _cgIconFileId = null, _cgBannerFileId = null;

function openCreateGroupModal() {
    _cgIconFileId = null;
    _cgBannerFileId = null;
    let overlay = document.getElementById('groupCreateOverlay');
    if (!overlay) {
        overlay = document.createElement('div');
        overlay.id = 'groupCreateOverlay';
        overlay.className = 'modal-overlay';
        overlay.style.display = 'none';
        overlay.onclick = (e) => { if (e.target === overlay) closeCreateGroupModal(); };
        document.body.appendChild(overlay);
    }
    const optional = `<span class="cg-optional">(${t('groups.common.optional', 'optional')})</span>`;
    overlay.innerHTML = `
    <div class="modal-box cg-box" role="dialog" aria-label="${esc(t('groups.create.title', 'Create Group'))}">
        ${renderModalBar(t('groups.create.title', 'Create Group'), [modalCloseAction('closeCreateGroupModal()')])}
        <div class="modal-card">
            <div class="modal-msg" style="margin:0 0 18px;">${t('groups.create.desc', 'Fill in the details on the right. The preview shows how your group will look.')}</div>
            <div class="cg-layout">
                <div class="cg-preview">
                    <div class="cg-prev-banner" id="cgPrevBanner" onclick="cgPickImage('banner')" title="${esc(t('groups.create.fields.banner', 'Banner'))}">
                        <div class="cg-prev-banner-hint"><span class="msi">add_photo_alternate</span><span>${t('groups.create.choose_banner', 'Choose banner')}</span></div>
                        <button class="cg-prev-clear" id="cgClear_banner" style="display:none;" onclick="event.stopPropagation();cgClearImage('banner')" title="${esc(t('common.remove', 'Remove'))}"><span class="msi">close</span></button>
                        <div class="fd-banner-fade"></div>
                    </div>
                    <div class="cg-prev-body">
                        <div class="cg-prev-id">
                            <div class="cg-prev-icon" id="cgPrevIcon" onclick="cgPickImage('icon')" title="${esc(t('groups.create.fields.icon', 'Icon'))}">
                                <span class="msi cg-prev-icon-hint">add_photo_alternate</span>
                                <button class="cg-prev-clear" id="cgClear_icon" style="display:none;" onclick="event.stopPropagation();cgClearImage('icon')" title="${esc(t('common.remove', 'Remove'))}"><span class="msi">close</span></button>
                            </div>
                            <div class="cg-prev-name-wrap">
                                <div class="fd-name cg-prev-name" id="cgPrevName">${esc(t('groups.create.preview.name', 'Group Name'))}</div>
                                <div class="cg-prev-code" id="cgPrevCode">ABC.0000</div>
                            </div>
                        </div>
                        <div class="cg-prev-chips">
                            <span class="cg-chip" id="cgPrevJoin"></span>
                            <span class="cg-chip" id="cgPrevPrivacy"></span>
                            <span class="cg-chip"><span class="msi">group</span>1</span>
                        </div>
                        <div class="cg-prev-desc" id="cgPrevDesc">${esc(t('groups.create.preview.desc', 'Your description will appear here.'))}</div>
                    </div>
                </div>
                <div class="cg-form">
                    <div class="vrc-cfg-grid">
                        <div class="vrc-cfg-field" style="grid-column:1 / -1;">
                            <div class="sf-section-label">${t('groups.create.fields.name', 'Name')}</div>
                            <input id="cgName" class="vrcn-edit-field" type="text" maxlength="64" placeholder="${esc(t('groups.create.fields.name_placeholder', 'Group name (3 to 64 characters)'))}" oninput="cgSyncPreview()">
                        </div>
                        <div class="vrc-cfg-field">
                            <div class="sf-section-label">${t('groups.create.fields.short_code', 'Short Code')}</div>
                            <input id="cgShort" class="vrcn-edit-field" type="text" maxlength="6" placeholder="${esc(t('groups.create.fields.short_code_placeholder', '3 to 6 characters'))}" style="text-transform:uppercase;" oninput="cgSyncPreview()">
                        </div>
                        <div class="vrc-cfg-field">
                            <div class="sf-section-label">${t('groups.create.fields.role_template', 'Role Template')}</div>
                            <select id="cgTemplate" class="vrcn-dropdown">
                                <option value="default">${t('groups.create.template.default', 'Default')}</option>
                                <option value="managedFree">${t('groups.create.template.managed_free', 'Managed (free join)')}</option>
                                <option value="managedRequest">${t('groups.create.template.managed_request', 'Managed (request)')}</option>
                                <option value="managedInvite">${t('groups.create.template.managed_invite', 'Managed (invite)')}</option>
                            </select>
                        </div>
                        <div class="vrc-cfg-field">
                            <div class="sf-section-label">${t('groups.create.fields.join_state', 'Join State')}</div>
                            <select id="cgJoin" class="vrcn-dropdown" onchange="cgSyncPreview()">
                                <option value="open">${t('groups.create.join.open', 'Open')}</option>
                                <option value="request">${t('groups.create.join.request', 'Request to join')}</option>
                                <option value="invite">${t('groups.create.join.invite', 'Invite only')}</option>
                                <option value="closed">${t('groups.create.join.closed', 'Closed')}</option>
                            </select>
                        </div>
                        <div class="vrc-cfg-field">
                            <div class="sf-section-label">${t('groups.create.fields.privacy', 'Privacy')}</div>
                            <select id="cgPrivacy" class="vrcn-dropdown" onchange="cgSyncPreview()">
                                <option value="default">${t('groups.create.privacy.default', 'Public')}</option>
                                <option value="private">${t('groups.create.privacy.private', 'Private')}</option>
                            </select>
                        </div>
                        <div class="vrc-cfg-field" style="grid-column:1 / -1;">
                            <div class="sf-section-label">${t('groups.create.fields.description', 'Description')} ${optional}</div>
                            <textarea id="cgDesc" class="vrcn-edit-field cg-textarea" rows="4" maxlength="250" placeholder="${esc(t('groups.create.fields.description_placeholder', 'What is this group about?'))}" oninput="cgSyncPreview()"></textarea>
                            <div class="set-desc cg-counter"><span id="cgDescCount">0</span>/250</div>
                        </div>
                        <div class="vrc-cfg-field">
                            <div class="sf-section-label">${t('groups.create.fields.icon', 'Icon')} ${optional}</div>
                            <div class="vrc-cfg-path">
                                <input type="text" id="cgIconName" class="vrcn-edit-field" readonly placeholder="${esc(t('groups.create.no_image', 'No image selected'))}">
                                <button class="vrcn-button" onclick="cgPickImage('icon')" title="${esc(t('common.browse', 'Browse'))}"><span class="msi" style="font-size:16px;">folder_open</span></button>
                            </div>
                        </div>
                        <div class="vrc-cfg-field">
                            <div class="sf-section-label">${t('groups.create.fields.banner', 'Banner')} ${optional}</div>
                            <div class="vrc-cfg-path">
                                <input type="text" id="cgBannerName" class="vrcn-edit-field" readonly placeholder="${esc(t('groups.create.no_image', 'No image selected'))}">
                                <button class="vrcn-button" onclick="cgPickImage('banner')" title="${esc(t('common.browse', 'Browse'))}"><span class="msi" style="font-size:16px;">folder_open</span></button>
                            </div>
                        </div>
                    </div>
                    <div id="cgError" class="cg-error" style="display:none;"></div>
                </div>
            </div>
        </div>
        <div class="modal-foot">
            <button class="vrcn-button" id="cgSubmitBtn" onclick="submitCreateGroup()"><span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">group_add</span>${esc(t('groups.create.submit', 'Create'))}</button>
            <span style="flex:1;"></span>
            <button class="vrcn-button" onclick="closeCreateGroupModal()">${t('common.cancel', 'Cancel')}</button>
        </div>
    </div>`;
    initAllVnSelects();
    cgSyncPreview();
    overlay.style.display = 'flex';
    setTimeout(() => document.getElementById('cgName')?.focus(), 50);
}

function cgSyncPreview() {
    const name = document.getElementById('cgName')?.value.trim() || '';
    const code = (document.getElementById('cgShort')?.value.trim() || '').toUpperCase();
    const desc = document.getElementById('cgDesc')?.value.trim() || '';
    const join = document.getElementById('cgJoin')?.value || 'open';
    const privacy = document.getElementById('cgPrivacy')?.value || 'default';
    const nameEl = document.getElementById('cgPrevName');
    if (nameEl) { nameEl.textContent = name || t('groups.create.preview.name', 'Group Name'); nameEl.classList.toggle('cg-placeholder', !name); }
    const codeEl = document.getElementById('cgPrevCode');
    if (codeEl) { codeEl.textContent = (code || 'ABC') + '.0000'; codeEl.classList.toggle('cg-placeholder', !code); }
    const descEl = document.getElementById('cgPrevDesc');
    if (descEl) { descEl.textContent = desc || t('groups.create.preview.desc', 'Your description will appear here.'); descEl.classList.toggle('cg-placeholder', !desc); }
    const cnt = document.getElementById('cgDescCount');
    if (cnt) cnt.textContent = String(document.getElementById('cgDesc')?.value.length || 0);
    const joinIcon = { open: 'lock_open', request: 'how_to_reg', invite: 'mail', closed: 'lock' }[join] || 'lock_open';
    const joinLabel = { open: t('groups.create.join.open', 'Open'), request: t('groups.create.join.request', 'Request to join'), invite: t('groups.create.join.invite', 'Invite only'), closed: t('groups.create.join.closed', 'Closed') }[join];
    const joinEl = document.getElementById('cgPrevJoin');
    if (joinEl) joinEl.innerHTML = `<span class="msi">${joinIcon}</span>${esc(joinLabel)}`;
    const privEl = document.getElementById('cgPrevPrivacy');
    if (privEl) privEl.innerHTML = `<span class="msi">${privacy === 'private' ? 'visibility_off' : 'public'}</span>${esc(privacy === 'private' ? t('groups.create.privacy.private', 'Private') : t('groups.create.privacy.default', 'Public'))}`;
}

function closeCreateGroupModal() {
    const overlay = document.getElementById('groupCreateOverlay');
    if (overlay) overlay.style.display = 'none';
    _cgIconFileId = null;
    _cgBannerFileId = null;
}

function cgPickImage(kind) {
    openImagePicker(kind === 'icon' ? 'create-group-icon' : 'create-group-banner');
}

function cgClearImage(kind) {
    if (kind === 'icon') _cgIconFileId = null; else _cgBannerFileId = null;
    _cgApplyImage(kind, null);
}

function _cgApplyImage(kind, url) {
    const box = document.getElementById(kind === 'icon' ? 'cgPrevIcon' : 'cgPrevBanner');
    if (box) { box.style.backgroundImage = url ? `url("${url}")` : ''; box.classList.toggle('has-img', !!url); }
    const clr = document.getElementById('cgClear_' + kind);
    if (clr) clr.style.display = url ? '' : 'none';
    const nameEl = document.getElementById(kind === 'icon' ? 'cgIconName' : 'cgBannerName');
    if (nameEl) nameEl.value = url ? t('groups.create.image_selected', 'Image selected') : '';
}

function cgApplyPickedImage(kind, fileId, url) {
    if (!document.getElementById('groupCreateOverlay') || !fileId) return;
    if (kind === 'icon') _cgIconFileId = fileId; else _cgBannerFileId = fileId;
    _cgApplyImage(kind, url);
}

function _cgSetError(msg) {
    const errEl = document.getElementById('cgError');
    if (!errEl) return;
    if (msg) { errEl.textContent = msg; errEl.style.display = ''; } else errEl.style.display = 'none';
}

function _cgResetSubmit() {
    const btn = document.getElementById('cgSubmitBtn');
    if (btn) { btn.disabled = false; btn.innerHTML = `<span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">group_add</span>${esc(t('groups.create.submit', 'Create'))}`; }
}

function submitCreateGroup() {
    const name = document.getElementById('cgName')?.value.trim() || '';
    const shortCode = (document.getElementById('cgShort')?.value.trim() || '').toUpperCase();
    const description = document.getElementById('cgDesc')?.value.trim() || '';
    const joinState = document.getElementById('cgJoin')?.value || 'open';
    const privacy = document.getElementById('cgPrivacy')?.value || 'default';
    const roleTemplate = document.getElementById('cgTemplate')?.value || 'default';

    if (name.length < 3 || name.length > 64) { _cgSetError(t('groups.create.validation.name', 'Name must be 3 to 64 characters.')); return; }
    if (shortCode.length < 3 || shortCode.length > 6) { _cgSetError(t('groups.create.validation.short_code', 'Short code must be 3 to 6 characters.')); return; }
    _cgSetError('');

    const btn = document.getElementById('cgSubmitBtn');
    if (btn) { btn.disabled = true; btn.innerHTML = `<span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">hourglass_empty</span>${esc(t('groups.create.submitting', 'Creating...'))}`; }

    const payload = { action: 'vrcCreateGroup', name, shortCode, description, joinState, privacy, roleTemplate };
    if (_cgIconFileId) payload.iconId = _cgIconFileId;
    if (_cgBannerFileId) payload.bannerId = _cgBannerFileId;
    sendToCS(payload);
}

function onGroupCreateResult(data) {
    if (data.ok) {
        showToast(true, t('groups.create.toast.created', 'Group created'));
        closeCreateGroupModal();
        loadMyGroups();
    } else {
        _cgResetSubmit();
        const msg = data.vrcPlusRequired
            ? t('groups.create.toast.vrcplus_required', 'Creating groups requires a VRChat+ subscription.')
            : (data.error || t('groups.create.toast.failed', 'Could not create group'));
        _cgSetError(msg);
        showToast(false, msg);
    }
}
