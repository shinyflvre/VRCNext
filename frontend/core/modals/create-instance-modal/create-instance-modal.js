/* === Create Instance Modal === */
function onCiTypeChange() {
    const type = document.getElementById('ciTypeValue')?.value || '';
    const groupRow = document.getElementById('ciGroupRow');
    if (!groupRow) return;
    const isGroup = type === 'group';
    groupRow.style.display = isGroup ? '' : 'none';
    const box = document.getElementById('createInstanceContent');
    if (box) box.style.width = isGroup ? '840px' : '506px';
    const subPills = document.getElementById('ciGroupSubPillsWrap');
    if (subPills) subPills.style.display = isGroup ? '' : 'none';
    const groupOpts = document.getElementById('ciGroupOptions');
    if (groupOpts) groupOpts.style.display = isGroup ? '' : 'none';
    const hidden = document.getElementById('ciGroupId');
    if (hidden) hidden.value = '';
    document.querySelectorAll('.ci-group-card').forEach(c => c.classList.remove('ci-group-selected'));
    if (isGroup) {
        if (!myGroupsLoaded) {
            renderCiGroupPicker(null);
            loadMyGroups();
        } else {
            renderCiGroupPicker(myGroups);
        }
    }
}

function renderCiGroupPicker(groups) {
    const el = document.getElementById('ciGroupList');
    if (!el) return;
    if (groups === null) {
        el.innerHTML = `<div style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);padding:6px 0;">${t('worlds.groups.loading', 'Loading groups...')}</div>`;
        return;
    }
    const validGroups = groups.filter(g => g.canCreateInstance !== false);
    if (!validGroups.length) {
        el.innerHTML = `<div style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);padding:6px 0;">${t('worlds.groups.none_create_rights', 'No groups with instance creation rights')}</div>`;
        return;
    }
    el.innerHTML = validGroups.map(g => {
        const icon = g.iconUrl
            ? `<img class="fd-group-icon" src="${esc(imgThumb(g.iconUrl, 96))}" onerror="this.style.display='none'">`
            : `<div class="fd-group-icon fd-group-icon-empty"><span class="msi" style="font-size:18px;">group</span></div>`;
        return `<div class="fd-group-card ci-group-card" data-gid="${esc(g.id)}" onclick="ciSelectGroup('${esc(g.id)}',this)">
            ${icon}
            <div class="fd-group-card-info">
                <div class="fd-group-card-name">${esc(g.name)}</div>
                <div class="fd-group-card-meta">${esc(g.shortCode || '')} &middot; ${tf('worlds.groups.members', { count: g.memberCount || 0 }, '{count} members')}</div>
            </div>
        </div>`;
    }).join('');
}

function ciSelectGroup(groupId, el) {
    document.getElementById('ciGroupId').value = groupId;
    document.querySelectorAll('.ci-group-card').forEach(c => c.classList.remove('ci-group-selected'));
    el.classList.add('ci-group-selected');
}

function createInstance(worldId) {
    // Legacy wrapper - open the create instance modal if world is set
    if (worldId && _ciWorld?.id !== worldId) _ciWorld = { id: worldId, name: worldId, thumb: '' };
    openCreateInstanceModal();
}

let _ciWorldId = '';
let _ciWorld = null;

const CI_PICK_LIMIT = 3;
let _ciLanguages = [];
let _ciVibes = [];
let _ciOptions = null;

function onInstanceCreateOptions(payload) {
    const categories = payload?.categories || [];
    const vibes = payload?.vibes || [];
    _ciOptions = (categories.length || vibes.length) ? { categories, vibes } : null;
    _ciRenderCategory();
    _ciRenderVibes();
}

function _ciRenderCategory() {
    const wrap = document.getElementById('ciCategoryWrap');
    if (!wrap) return;
    const current = document.getElementById('ciCategory')?.value || '';
    const rows = _ciOptions?.categories || [];
    wrap.innerHTML = `<select id="ciCategory" class="wd-create-select" style="width:100%;">
        <option value="">${esc(t('worlds.instances.category_none', 'None'))}</option>
        ${rows.map(c => `<option value="${esc(c.id)}">${esc(instanceCategoryLabel(c.name))}</option>`).join('')}
    </select>`;
    const sel = document.getElementById('ciCategory');
    sel.value = current;
    initVnSelect(sel);
}

function _ciChipsHtml(items, removeFn) {
    return items.map(item =>
        `<span class="myp-lang-chip">${esc(item.label)}<button class="myp-lang-remove" onclick="${removeFn}('${jsq(item.value)}')"><span class="msi" style="font-size:11px;">close</span></button></span>`
    ).join('');
}

function _ciFillPicker(selectId, rowId, placeholder, options, count) {
    const sel = document.getElementById(selectId);
    const row = document.getElementById(rowId);
    if (!sel) return;
    sel.innerHTML = `<option value="">${esc(placeholder)}</option>` +
        options.map(o => `<option value="${esc(o.value)}">${esc(o.label)}</option>`).join('');
    sel.value = '';
    if (!sel._vnSelect) initVnSelect(sel);
    sel._vnRefresh?.();
    if (row) row.style.display = count >= CI_PICK_LIMIT ? 'none' : '';
}

function _ciLanguageLabel(code) {
    return LANG_MAP['language_' + code] || code.toUpperCase();
}

function _ciRenderLanguages() {
    const chips = document.getElementById('ciLangChips');
    if (!chips) return;
    chips.innerHTML = _ciChipsHtml(_ciLanguages.map(value => ({ value, label: _ciLanguageLabel(value) })), 'ciRemoveLanguage');
    chips.style.display = _ciLanguages.length ? '' : 'none';
    const options = Object.keys(LANG_MAP)
        .map(key => key.replace('language_', ''))
        .filter(code => !_ciLanguages.includes(code))
        .map(code => ({ value: code, label: _ciLanguageLabel(code) }))
        .sort((a, b) => a.label.localeCompare(b.label));
    _ciFillPicker('ciLangSelect', 'ciLangAddRow', t('worlds.instances.add_language', 'Add language...'), options, _ciLanguages.length);
}

function ciAddLanguage() {
    const code = document.getElementById('ciLangSelect')?.value;
    if (!code || _ciLanguages.includes(code) || _ciLanguages.length >= CI_PICK_LIMIT) return;
    _ciLanguages.push(code);
    _ciRenderLanguages();
}

function ciRemoveLanguage(code) {
    _ciLanguages = _ciLanguages.filter(c => c !== code);
    _ciRenderLanguages();
}

function _ciVibeLabel(id) {
    const vibe = (_ciOptions?.vibes || []).find(v => v.id === id);
    return vibe ? instanceVibeLabel(vibe.title) : id;
}

function _ciRenderVibes() {
    const chips = document.getElementById('ciVibeChips');
    if (!chips) return;
    chips.innerHTML = _ciChipsHtml(_ciVibes.map(value => ({ value, label: _ciVibeLabel(value) })), 'ciRemoveVibe');
    chips.style.display = _ciVibes.length ? '' : 'none';
    const options = (_ciOptions?.vibes || [])
        .filter(v => !_ciVibes.includes(v.id))
        .map(v => ({ value: v.id, label: instanceVibeLabel(v.title) }));
    _ciFillPicker('ciVibeSelect', 'ciVibeAddRow', t('worlds.instances.add_vibe', 'Add vibe...'), options, _ciVibes.length);
}

function ciAddVibe() {
    const id = document.getElementById('ciVibeSelect')?.value;
    if (!id || _ciVibes.includes(id) || _ciVibes.length >= CI_PICK_LIMIT) return;
    _ciVibes.push(id);
    _ciRenderVibes();
}

function ciRemoveVibe(id) {
    _ciVibes = _ciVibes.filter(v => v !== id);
    _ciRenderVibes();
}

function _ciCollectExtras() {
    return {
        description: (document.getElementById('ciDescription')?.value || '').trim(),
        categoryId: document.getElementById('ciCategory')?.value || '',
        vibeIds: _ciVibes.slice(),
        languages: _ciLanguages.slice(),
    };
}

function openCreateInstanceModal() {
    if (!_ciWorld) return;
    _ciWorldId = _ciWorld.id;
    const worldName = _ciWorld.name;
    const thumb = _ciWorld.thumb || '';

    const el = document.getElementById('createInstanceContent');
    if (!el) return;

    el.innerHTML = `
        ${renderModalBar(t('worlds.instances.create_title', 'Create Instance'), [modalCloseAction('closeCreateInstanceModal()')])}
        <div class="modal-card">
            <div class="ci-world-row">
                ${thumb ? `<img class="ci-world-thumb" src="${esc(imgThumb(thumb, 128))}" onerror="this.style.display='none'">` : ''}
                <div class="ci-world-name" title="${esc(worldName)}">${esc(worldName)}</div>
            </div>

            <div class="ci-cols">
            <div class="ci-col-main">
            <div class="sf-section-label" style="margin-top:0;">${t('timeline.detail.instance_type', 'Instance Type')}</div>
            <div class="fd-tabs" id="ciTypePills" style="margin-bottom:0;">
                <button class="fd-tab active" data-type="private" onclick="setCiType('private',this)">${t('worlds.instances.types.invite', 'Invite')}</button>
                <button class="fd-tab" data-type="invite_plus" onclick="setCiType('invite_plus',this)">${t('worlds.instances.types.invite_plus', 'Invite+')}</button>
                <button class="fd-tab" data-type="friends" onclick="setCiType('friends',this)">${t('worlds.instances.types.friends', 'Friends')}</button>
                <button class="fd-tab" data-type="hidden" onclick="setCiType('hidden',this)">${t('worlds.instances.types.friends_plus', 'Friends+')}</button>
                <button class="fd-tab" data-type="group" onclick="setCiType('group',this)">${t('worlds.instances.types.group_root', 'Group')}</button>
                <button class="fd-tab" data-type="public" onclick="setCiType('public',this)">${t('worlds.instances.types.public', 'Public')}</button>
            </div>

            <div id="ciGroupSubPillsWrap" style="margin-top:6px;">
                <div class="fd-tabs" id="ciGroupSubPills" style="margin-bottom:0;">
                    <button class="fd-tab active" data-type="group_members" onclick="setCiGroupType('group_members',this)">${t('worlds.instances.types.group', 'Members')}</button>
                    <button class="fd-tab" data-type="group_plus" onclick="setCiGroupType('group_plus',this)">${t('worlds.instances.types.group_plus', 'Group+')}</button>
                    <button class="fd-tab" data-type="group_public" onclick="setCiGroupType('group_public',this)">${t('worlds.instances.types.group_public', 'Group Public')}</button>
                </div>
            </div>

            <div id="ciGroupOptions">
                <div class="ci-toggle-row" id="ciQueueRow">
                    <span>${t('worlds.instances.queue', 'Queue')}</span>
                    <label class="ci-toggle"><input type="checkbox" id="ciQueueEnabled"><span class="ci-toggle-slider"></span></label>
                </div>
                <div class="ci-toggle-row" id="ciAgeGateRow">
                    <span>${t('worlds.instances.age_gate', 'Age Gate')}</span>
                    <label class="ci-toggle"><input type="checkbox" id="ciAgeGateEnabled"><span class="ci-toggle-slider"></span></label>
                </div>
                <div class="sf-section-label">${t('worlds.instances.min_perf', 'Minimum Avatar Performance')}</div>
                <div class="ci-opt-row" id="ciMinPerfRow">
                    <button type="button" class="vrcn-button active" onclick="ciSetMinPerf('', this)">${t('worlds.instances.min_perf_none', 'No Limit')}</button>
                    <button type="button" class="vrcn-button" onclick="ciSetMinPerf('Good', this)"><img src="assets/Avatars/good.png" alt=""> ${t('avatars.perf.good', 'Good')}</button>
                    <button type="button" class="vrcn-button" onclick="ciSetMinPerf('Medium', this)"><img src="assets/Avatars/medium.png" alt=""> ${t('avatars.perf.medium', 'Medium')}</button>
                    <button type="button" class="vrcn-button" onclick="ciSetMinPerf('Poor', this)"><img src="assets/Avatars/poor.png" alt=""> ${t('avatars.perf.poor', 'Poor')}</button>
                </div>
                <input type="hidden" id="ciMinPerf" value="">
            </div>

            <div style="margin-top:14px;display:flex;gap:10px;align-items:flex-end;">
                <div style="flex:1;min-width:0;">
                    <div class="sf-section-label" style="margin-top:0;">${t('worlds.instances.instance_name', 'Instance Name')} <span class="vrcn-supporter-badge" style="vertical-align:middle;margin-left:4px;">VRC+</span></div>
                    <input type="text" id="ciInstanceName" class="ci-instance-name-input" placeholder="${t('worlds.instances.instance_name_placeholder', 'Optional name...')}" maxlength="64">
                </div>
                <div style="flex-shrink:0;">
                    <div class="sf-section-label" style="margin-top:0;">${t('worlds.instances.region', 'Region')}</div>
                    <select id="ciRegion" class="wd-create-select">
                        <option value="eu">${getWorldRegionLabel('eu')}</option>
                        <option value="us">${getWorldRegionLabel('us')}</option>
                        <option value="use">${getWorldRegionLabel('use')}</option>
                        <option value="jp">${getWorldRegionLabel('jp')}</option>
                    </select>
                </div>
            </div>

            <div class="sf-section-label">${t('worlds.instances.description', 'Description')}</div>
            <textarea id="ciDescription" class="vrcn-edit-field" rows="2" maxlength="256" placeholder="${esc(t('worlds.instances.description_placeholder', 'Optional description...'))}" style="width:100%;height:auto;padding:8px 10px;font-family:inherit;resize:vertical;"></textarea>

            <div class="sf-section-label">${t('worlds.instances.category', 'Category')}</div>
            <div id="ciCategoryWrap" style="display:flex;"></div>

            <div class="sf-section-label">${t('worlds.instances.languages', 'Languages')} <span style="font-weight:400;text-transform:none;letter-spacing:normal;">(${t('worlds.instances.up_to_three', 'up to 3')})</span></div>
            <div id="ciLangChips" class="myp-lang-chips"></div>
            <div class="myp-lang-add-row" id="ciLangAddRow">
                <select id="ciLangSelect" class="myp-lang-select"></select>
                <button class="myp-add-lang-btn" onclick="ciAddLanguage()"><span class="msi" style="font-size:15px;">add</span></button>
            </div>

            <div class="sf-section-label">${t('worlds.instances.vibes', 'Vibes')} <span style="font-weight:400;text-transform:none;letter-spacing:normal;">(${t('worlds.instances.up_to_three', 'up to 3')})</span></div>
            <div id="ciVibeChips" class="myp-lang-chips"></div>
            <div class="myp-lang-add-row" id="ciVibeAddRow">
                <select id="ciVibeSelect" class="myp-lang-select"></select>
                <button class="myp-add-lang-btn" onclick="ciAddVibe()"><span class="msi" style="font-size:15px;">add</span></button>
            </div>
            </div>

            <div id="ciGroupRow" class="ci-col-group">
                <div class="sf-section-label" style="margin-top:0;">${t('worlds.instances.select_group', 'Select Group')}</div>
                <input type="hidden" id="ciGroupId" value="">
                <div class="ci-group-list" id="ciGroupList"></div>
            </div>
            </div>

            <input type="hidden" id="ciTypeValue" value="private">
            <input type="hidden" id="ciGroupTypeValue" value="group_members">
        </div>

        <div class="modal-foot">
            <div class="modal-foot-spacer"></div>
            <button class="vrcn-button" id="ciCreateBtn" onclick="doCreateInstance(false)">
                <span class="msi" style="font-size:16px;">add_circle_outline</span> ${t('worlds.instances.create_only', 'Create Instance')}
            </button>
            <button class="vrcn-button vrcn-btn-primary" id="ciCreateJoinBtn" onclick="doCreateInstance(true)">
                <span class="msi" style="font-size:16px;">play_circle</span> ${t('worlds.instances.create_join', 'Create &amp; Join')}
            </button>
        </div>`;

    initVnSelect(document.getElementById('ciRegion'));
    _ciLanguages = [];
    _ciVibes = [];
    _ciRenderCategory();
    _ciRenderLanguages();
    _ciRenderVibes();
    if (!_ciOptions) sendToCS({ action: 'vrcGetInstanceCreateOptions' });
    onCiTypeChange();
    document.getElementById('modalCreateInstance').style.display = 'flex';
}

function closeCreateInstanceModal() {
    document.getElementById('modalCreateInstance').style.display = 'none';
}

function setCiType(type, btn) {
    document.querySelectorAll('#ciTypePills .fd-tab').forEach(b => b.classList.remove('active'));
    if (btn) btn.classList.add('active');
    const hidden = document.getElementById('ciTypeValue');
    if (hidden) hidden.value = type;
    onCiTypeChange();
}

function setCiGroupType(type, btn) {
    document.querySelectorAll('#ciGroupSubPills .fd-tab').forEach(b => b.classList.remove('active'));
    if (btn) btn.classList.add('active');
    const hidden = document.getElementById('ciGroupTypeValue');
    if (hidden) hidden.value = type;
}

function ciSetMinPerf(value, btn) {
    document.querySelectorAll('#ciMinPerfRow .vrcn-button').forEach(b => b.classList.remove('active'));
    if (btn) btn.classList.add('active');
    const hidden = document.getElementById('ciMinPerf');
    if (hidden) hidden.value = value;
}

function doCreateInstance(andJoin) {
    const worldId = _ciWorldId;
    const type = document.getElementById('ciTypeValue')?.value || 'public';
    const region = document.getElementById('ciRegion')?.value || 'eu';
    const instanceName = (document.getElementById('ciInstanceName')?.value || '').trim();
    const queueEnabled = document.getElementById('ciQueueEnabled')?.checked ?? false;
    const ageGateEnabled = document.getElementById('ciAgeGateEnabled')?.checked ?? false;
    const minAvatarPerf = document.getElementById('ciMinPerf')?.value || '';

    const createBtn = document.getElementById('ciCreateBtn');
    const joinBtn = document.getElementById('ciCreateJoinBtn');
    const loadHtml = `<span class="msi" style="font-size:14px;">hourglass_empty</span> ${t('worlds.instances.creating', 'Creating...')}`;
    if (createBtn) { createBtn.disabled = true; createBtn.innerHTML = loadHtml; }
    if (joinBtn) { joinBtn.disabled = true; joinBtn.innerHTML = loadHtml; }

    if (type === 'group') {
        const groupSubType = document.getElementById('ciGroupTypeValue')?.value || 'group_members';
        const groupId = document.getElementById('ciGroupId')?.value || '';
        if (!groupId) {
            if (createBtn) { createBtn.disabled = false; createBtn.innerHTML = `<span class="msi" style="font-size:14px;">add_circle_outline</span> ${t('worlds.instances.create_only', 'Create Instance')}`; }
            if (joinBtn) { joinBtn.disabled = false; joinBtn.innerHTML = `<span class="msi" style="font-size:14px;">play_circle</span> ${t('worlds.instances.create_join', 'Create & Join')}`; }
            return;
        }
        const accessType = groupSubType === 'group_public' ? 'public' : groupSubType === 'group_plus' ? 'plus' : 'members';
        sendToCS({ action: 'vrcCreateGroupInstance', worldId, groupId, groupAccessType: accessType, region, instanceName, queueEnabled, ageGateEnabled, minAvatarPerf, andJoin, ..._ciCollectExtras() });
    } else {
        sendToCS({ action: 'vrcCreateInstance', worldId, type, region, instanceName: _hasVrcPlus ? instanceName : '', andJoin, ..._ciCollectExtras() });
    }
}

let _pendingCreateInstance = null;

function createWorldInstance(worldId) {
    if (!worldId) return;
    const cached = (typeof dashWorldCache !== 'undefined' && dashWorldCache[worldId]) || {};
    if (cached.name) {
        _ciWorld = { id: worldId, name: cached.name, thumb: cached.thumbnailImageUrl || cached.imageUrl || '' };
        openCreateInstanceModal();
    } else {
        // World not in cache yet - fetch it, then open the modal
        _pendingCreateInstance = worldId;
        sendToCS({ action: 'vrcResolveWorlds', worldIds: [worldId] });
    }
}

function onCreateInstanceWorldResolved(dict) {
    if (!_pendingCreateInstance) return;
    const worldId = _pendingCreateInstance;
    _pendingCreateInstance = null;
    const w = dict[worldId];
    _ciWorld = { id: worldId, name: w?.name || worldId, thumb: w?.thumbnailImageUrl || w?.imageUrl || '' };
    openCreateInstanceModal();
}

