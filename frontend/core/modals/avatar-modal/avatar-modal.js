/* === Avatar Modal === */
/* === Avatar Detail Modal === */

function fmtAvatarTag(tag) {
    const raw = tag.startsWith('author_tag_') ? tag.slice(11) : tag;
    return raw.replace(/_/g, ' ').replace(/\b\w/g, c => c.toUpperCase());
}
let _avDetailData = null;
let _avEditTags   = [];
let _avGalleryLoaded = false;
let _avGalleryImages = [];

const _avatarDetailCache = {};
const _avRawJsonCache = {};
const _avAnalysisCache = {};

function openAvatarDetail(avatarId) {
    if (typeof navSetCurrent === 'function') navSetCurrent('avatar', avatarId);
    const _avMod = document.getElementById('modalAvatarDetail');
    _avMod.classList.add('av-style-compact');
    _avMod.style.display = 'flex';
    const cached = _avatarDetailCache[avatarId];
    if (cached) {
        renderAvatarDetail(cached);
    } else {
        const c = document.getElementById('avatarDetailContent');
        if (c) c.innerHTML = sk('content-modal-compact');
    }
    sendToCS({ action: 'vrcGetAvatarDetail', avatarId });
}

function closeAvatarDetail(fromNav = false) {
    document.getElementById('modalAvatarDetail').classList.remove('av-style-compact');
    document.getElementById('modalAvatarDetail').style.display = 'none';
    _avDetailData = null;
    if (!fromNav && typeof navClear === 'function') navClear();
}

const _avFieldIds = {
    name:       { view: 'avfNameView',       edit: 'avfNameEdit'       },
    desc:       { view: 'avfDescView',       edit: 'avfDescEdit'       },
    visibility: { view: 'avfVisView',        edit: 'avfVisEdit'        },
    tags:       { view: 'avfTagsView',       edit: 'avfTagsEdit'       },
};
let _avSavingField = '';


/* === Avatar inline edit === */
let _avVisState = 'public';

function editAvField(field) {
    Object.keys(_avFieldIds).forEach(f => {
        if (f === field) return;
        const ids = _avFieldIds[f];
        const v = document.getElementById(ids.view); if (v) v.style.display = '';
        const e = document.getElementById(ids.edit); if (e) e.style.display = 'none';
    });
    const ids = _avFieldIds[field];
    if (!ids) return;
    const v = document.getElementById(ids.view); if (v) v.style.display = 'none';
    const e = document.getElementById(ids.edit); if (e) e.style.display = '';

    if (field === 'name') {
        document.getElementById('avNameInput')?.focus();
    } else if (field === 'desc') {
        document.getElementById('avDescInput')?.focus();
    } else if (field === 'visibility') {
        _avVisState = (_avDetailData?.releaseStatus === 'public') ? 'public' : 'private';
    } else if (field === 'tags') {
        _avEditTags = [...(_avDetailData?.tags || [])];
        avRenderTagChips();
        document.getElementById('avTagInput')?.focus();
    }
}

function cancelAvField(field) {
    const ids = _avFieldIds[field];
    if (!ids) return;
    const v = document.getElementById(ids.view); if (v) v.style.display = '';
    const e = document.getElementById(ids.edit); if (e) e.style.display = 'none';
}


function avVisToggle(state, btn) {
    _avVisState = state;
    document.querySelectorAll('#avVisPublicBtn,#avVisPrivateBtn').forEach(b => b.classList.remove('active'));
    btn.classList.add('active');
}


function avAddTag() {
    const inp = document.getElementById('avTagInput');
    if (!inp) return;
    const val = inp.value.trim();
    if (val && !_avEditTags.includes(val)) {
        _avEditTags.push(val);
        avRenderTagChips();
    }
    inp.value = '';
    inp.focus();
}

function avRemoveTag(idx) {
    _avEditTags.splice(idx, 1);
    avRenderTagChips();
}


function renderAvatarDetail(a) {
    if (a.id && a.rawJson) _avRawJsonCache[a.id] = a.rawJson;
    if (a.id) _avatarDetailCache[a.id] = a;
    _avDetailData = a;
    _avGalleryLoaded = false;
    _avGalleryImages = [];
    if (typeof navUpdateLabel === 'function') navUpdateLabel(a.name || '');
    const c = document.getElementById('avatarDetailContent');
    if (!c) return;

    const thumb = a.thumbnailImageUrl || a.imageUrl || '';
    const isOwn = currentVrcUser && a.authorId === currentVrcUser.id;
    const aid = jsq(a.id || '');
    const _avIsFav = (typeof favAvatarsData !== 'undefined') && favAvatarsData.some(f => f.id === a.id);

    const isPublic = a.releaseStatus === 'public';
    const statusBadge = avatarStatusBadge(isPublic);
    const impostorBadge = a.hasImpostor
        ? `<span class="vrcn-badge" style="background:rgba(138,43,226,.18);color:#b47aff;"><span class="msi" style="font-size:10px;">smart_toy</span> ${t('avatars.labels.impostor', 'Impostor')}</span>`
        : '';

    function fmtDate(iso) {
        if (!iso) return '-';
        const d = new Date(iso);
        if (isNaN(d)) return iso;
        return fmtShortDate(d) + ', ' + fmtTimeSeconds(d);
    }

    const authorHtml = a.authorId
        ? `<span onclick="navOpenModal('friend','${jsq(a.authorId)}','${jsq(a.authorName || '')}')" style="display:inline-flex;align-items:center;padding:1px 8px;border-radius:20px;background:var(--badge-bg);font-size:calc(11px + var(--fs-off, 0px));font-weight:600;color:var(--badge-tx);cursor:pointer;line-height:1.8;">${esc(a.authorName || a.authorId)}</span>`
        : esc(a.authorName || '');

    const metaRows = [
        `<div class="fd-meta-row"><span class="fd-meta-label">${t('avatars.detail.meta.created_at', 'Created At')}</span><span>${fmtDate(a.created_at)}</span></div>`,
        `<div class="fd-meta-row"><span class="fd-meta-label">${t('avatars.detail.meta.updated_at', 'Last Updated')}</span><span>${fmtDate(a.updated_at)}</span></div>`,
        a.version ? `<div class="fd-meta-row"><span class="fd-meta-label">${t('avatars.detail.meta.version', 'Version')}</span><span>v${a.version}</span></div>` : '',
    ].join('');

    const tagsViewHtml = (a.tags && a.tags.length)
        ? `<div class="fd-lang-tags">${a.tags.map(tag => `<span class="vrcn-badge">${esc(fmtAvatarTag(tag))}</span>`).join('')}</div>`
        : `<div class="myp-empty">${t('avatars.detail.empty_tags', 'No tags')}</div>`;

    const _avIdRow = `<div class="fd-badges-row fd-bio-badges-row" style="margin:0 0 10px;">${idBadge(a.id)}</div>`;

    const _descCard = `<div class="fd-info-card">
        <div class="myp-section-header">
            <span class="myp-section-title">${t('avatars.detail.sections.description', 'Description')}</span>
            ${isOwn ? `<button class="myp-edit-btn" onclick="editAvField('desc')"><span class="msi" style="font-size:14px;">edit</span></button>` : ''}
        </div>
        ${_avIdRow}
        <div id="avfDescView">${a.description ? `<div class="fd-bio">${esc(a.description)}</div>` : `<div class="myp-empty">${t('avatars.detail.empty_description', 'No description')}</div>`}</div>
        ${isOwn ? `<div id="avfDescEdit" style="display:none;">
            <textarea id="avDescInput" class="myp-textarea" rows="4" maxlength="2000" placeholder="${esc(t('avatars.detail.description_placeholder', 'Avatar description...'))}">${esc(a.description || '')}</textarea>
            <div class="myp-edit-actions">
                <button class="vrcn-button" onclick="cancelAvField('desc')">${t('common.cancel', 'Cancel')}</button>
                <button class="vrcn-button vrcn-btn-primary" onclick="saveAvField('desc','${aid}')">${t('common.save', 'Save')}</button>
            </div>
        </div>` : ''}
    </div>`;

    const _tagsCard = `<div class="fd-info-card">
        <div class="myp-section-header">
            <span class="myp-section-title">${t('avatars.detail.sections.tags', 'Tags')}</span>
            ${isOwn ? `<button class="myp-edit-btn" onclick="editAvField('tags')"><span class="msi" style="font-size:14px;">edit</span></button>` : ''}
        </div>
        <div id="avfTagsView">${tagsViewHtml}</div>
        ${isOwn ? `<div id="avfTagsEdit" style="display:none;">
            <div id="avTagsChips" class="myp-lang-chips" style="margin-bottom:6px;"></div>
            <div style="display:flex;gap:6px;margin-bottom:8px;">
                <input id="avTagInput" class="vrcn-edit-field" placeholder="${esc(t('avatars.detail.add_tag_placeholder', 'Add tag...'))}" style="flex:1;" onkeydown="if(event.key==='Enter'){event.preventDefault();avAddTag();}">
                <button class="myp-add-lang-btn" onclick="avAddTag()"><span class="msi" style="font-size:15px;">add</span></button>
            </div>
            <div class="myp-edit-actions">
                <button class="vrcn-button" onclick="cancelAvField('tags')">${t('common.cancel', 'Cancel')}</button>
                <button class="vrcn-button vrcn-btn-primary" onclick="saveAvField('tags','${aid}')">${t('common.save', 'Save')}</button>
            </div>
        </div>` : ''}
    </div>`;

    const _mr = (label, valueHtml) =>
        `<div style="display:flex;justify-content:space-between;gap:8px;align-items:center;font-size:calc(11px + var(--fs-off, 0px));"><span style="color:var(--tx2);">${label}</span><span style="color:var(--tx1);text-align:right;display:inline-flex;align-items:center;gap:6px;">${valueHtml}</span></div>`;
    const _perfVal = perf => {
        const ic = avatarPerfIcon(perf, 18);
        return ic ? `${ic}${esc(_avPerfPretty(perf))}` : '<span style="color:var(--tx1);">-</span>';
    };
    const platPerfRows = [
        a.hasPC    ? _mr('PC', _perfVal(a.pcPerf))         : '',
        a.hasQuest ? _mr('Android', _perfVal(a.questPerf)) : '',
        a.hasIos   ? _mr('iOS', _perfVal(a.iosPerf))       : '',
    ].join('');

    const _infosCard = `<div class="fd-info-card">
        <div class="fd-group-rep-label">${t('avatars.detail.sections.infos', 'Infos')}</div>
        <div style="display:grid;gap:6px;margin-bottom:10px;">
            ${_mr(t('avatars.detail.meta.created_at', 'Created'), fmtDate(a.created_at))}
            ${_mr(t('avatars.detail.meta.updated_at', 'Updated'), fmtDate(a.updated_at))}
            ${a.version ? _mr(t('avatars.detail.meta.version', 'Version'), `v${a.version}`) : ''}
            ${platPerfRows}
        </div>
        ${impostorBadge ? `<div style="display:flex;flex-wrap:wrap;gap:5px;margin-bottom:10px;">${impostorBadge}</div>` : ''}
        <div class="myp-section-header">
            <span class="myp-section-title">${t('avatars.detail.sections.visibility', 'Visibility')}</span>
            ${isOwn ? `<button class="myp-edit-btn" onclick="editAvField('visibility')"><span class="msi" style="font-size:14px;">edit</span></button>` : ''}
        </div>
        <div id="avfVisView">${statusBadge}</div>
        ${isOwn ? `<div id="avfVisEdit" style="display:none;">
            <div style="display:flex;gap:6px;margin-bottom:8px;">
                <button id="avVisPublicBtn" class="vrcn-button-round${isPublic ? ' active' : ''}" onclick="avVisToggle('public',this)">
                    <span class="msi" style="font-size:14px;">public</span> ${t('avatars.labels.public', 'Public')}
                </button>
                <button id="avVisPrivateBtn" class="vrcn-button-round${!isPublic ? ' active' : ''}" onclick="avVisToggle('private',this)">
                    <span class="msi" style="font-size:14px;">lock</span> ${t('avatars.labels.private', 'Private')}
                </button>
            </div>
            <div class="myp-edit-actions">
                <button class="vrcn-button" onclick="cancelAvField('visibility')">${t('common.cancel', 'Cancel')}</button>
                <button class="vrcn-button vrcn-btn-primary" onclick="saveAvField('visibility','${aid}')">${t('common.save', 'Save')}</button>
            </div>
        </div>` : ''}
    </div>`;

    const avHeaderActions = renderModalActions([
        { icon: 'checkroom', title: t('avatars.detail.actions.use_avatar', 'Use Avatar'), onclick: `selectAvatar('${aid}');closeAvatarDetail()` },
        { icon: _avIsFav ? 'favorite' : 'favorite_border', iconClass: _avIsFav ? 'fd-action-fav' : '', title: avatarFavoriteActionLabel(_avIsFav), onclick: `openAvFavPicker('${aid}',this)` },
        { icon: 'refresh', iconClass: 'fd-refresh-spin', title: t('common.refresh', 'Refresh'), onclick: `triggerModalRefresh({action:'vrcGetAvatarDetail',avatarId:'${aid}',force:true})` },
        { icon: 'link_2', title: t('common.share', 'Share'), onclick: `navigator.clipboard.writeText('https://vrchat.com/home/avatar/${esc(a.id)}').then(()=>showToast(true,t('common.link_copied','Link copied!')))` },
        isOwn ? { icon: 'delete', title: t('avatars.detail.actions.delete', 'Delete Avatar'), onclick: `confirmDeleteAvatar('${aid}','${jsq(a.name || '')}')`, dangerSolid: true } : null,
        { icon: 'close', title: t('common.close', 'Close'), onclick: `closeAvatarDetail()` },
    ]);
    const _avNameAuthor = `<div id="avfNameView" style="display:flex;align-items:center;gap:6px;">
                        <div class="fd-name">${esc(a.name || t('avatars.detail.unnamed', 'Unnamed Avatar'))}</div>
                        ${isOwn ? `<button class="myp-edit-btn" onclick="editAvField('name')"><span class="msi" style="font-size:14px;">edit</span></button>` : ''}
                    </div>
                    ${isOwn ? `<div id="avfNameEdit" style="display:none;margin-top:6px;">
                        <input id="avNameInput" class="vrcn-edit-field" value="${esc(a.name || '')}" maxlength="64" style="width:100%;">
                        <div class="myp-edit-actions">
                            <button class="vrcn-button" onclick="cancelAvField('name')">${t('common.cancel', 'Cancel')}</button>
                            <button class="vrcn-button vrcn-btn-primary" onclick="saveAvField('name','${aid}')">${t('common.save', 'Save')}</button>
                        </div>
                    </div>` : ''}
                    <div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx2);margin-top:4px;">${t('avatars.detail.by', 'by')} ${authorHtml}</div>`;

    const _avTabsHtml = `<div class="fd-tabs" style="margin-bottom:14px;">
                <button class="fd-tab active" onclick="switchAvTab('info',this)">${t('profiles.tabs.info', 'Info')}</button>
                <button class="fd-tab" onclick="switchAvTab('gallery',this)">${t('avatars.tabs.gallery', 'Gallery')}</button>
                <button class="fd-tab" onclick="switchAvTab('json',this)">Json</button>
            </div>`;

    const _avGalleryTab = `<div id="avTabGallery" style="display:none;">
                ${isOwn ? `<div style="margin-bottom:10px;"><button class="vrcn-button" id="avGalleryUploadBtn" onclick="avUploadGalleryImage('${aid}')"><span class="msi" style="font-size:16px;">upload</span> ${esc(t('avatars.gallery.upload_image', 'Upload Image'))}</button></div>` : ''}
                <div id="avGalleryContent" style="min-height:60px;"></div>
            </div>`;
    const _avJsonTab = `<div id="avTabJson" style="display:none;"><div class="json-viewer">${jsonHighlight((a.id && _avRawJsonCache[a.id]) || {})}</div></div>`;

    c.innerHTML = `${avHeaderActions}<div class="fd-layout">
            <div class="fd-left">
                <div class="fd-left-banner" id="av-banner-slot">${thumb ? `<img src="${thumb}" onerror="this.style.display='none'"><div class="fd-banner-fade"></div>` : ''}${isOwn ? `<button class="fd-banner-edit" onclick="avUploadBannerImage('${aid}')" title="${esc(t('avatars.detail.actions.change_image', 'Change Image'))}"><span class="msi">edit</span></button>` : ''}</div>
                <div class="fd-left-body">
                    <div class="fd-left-id"><div class="fd-left-name-wrap">${_avNameAuthor}</div></div>
                    ${_infosCard}
                    ${_tagsCard}
                </div>
            </div>
            <div class="fd-right"><div class="fd-right-scroll">
                ${_avTabsHtml}
                <div id="avTabInfo"><div class="fd-info-wrap">${_descCard}</div></div>
                ${_avGalleryTab}
                ${_avJsonTab}
            </div></div>
        </div>`;

    const _avModal = document.getElementById('modalAvatarDetail');
    if (_avModal) _avModal.classList.add('av-style-compact');
    _avRenderAnalysis();
}

function onAvatarAnalysis(data) {
    if (!data || !data.avatarId) return;
    const prev = _avAnalysisCache[data.avatarId];
    _avAnalysisCache[data.avatarId] = {
        platforms: Object.assign({}, prev ? prev.platforms : {}, data.platforms || {}),
        pending: !!data.pending,
    };
    if (_avDetailData && _avDetailData.id === data.avatarId) _avRenderAnalysis();
}

function _avPerfMb(v) {
    const n = Number(v);
    return isFinite(n) && v !== null && v !== undefined ? (n / 1048576).toFixed(2) + ' MB' : '-';
}
function _avPerfInt(v) {
    return (v === null || v === undefined || !isFinite(Number(v))) ? '-' : Number(v).toLocaleString();
}
function _avPerfBounds(b) {
    return (Array.isArray(b) && b.length === 3) ? b.map(v => String(Math.round(Number(v) * 100) / 100)).join(' × ') + ' m' : '-';
}
function _avPerfBool(v) {
    if (v === null || v === undefined) return '-';
    return v ? t('avatars.perf.yes', 'Yes') : t('avatars.perf.no', 'No');
}
function _avPerfRow(label, value) {
    return `<div style="display:flex;justify-content:space-between;gap:8px;align-items:center;font-size:calc(11px + var(--fs-off, 0px));"><span style="color:var(--tx2);">${esc(label)}</span><span style="color:var(--tx1);text-align:right;">${value}</span></div>`;
}

const _AV_PERF_GROUPS = [
    ['geometry', 'Geometry', [
        ['totalPolygons', 'triangles', 'Triangles', 'int'], ['totalVertices', 'vertices', 'Vertices', 'int'],
        ['skinnedMeshCount', 'skinned_meshes', 'Skinned meshes', 'int'], ['meshCount', 'basic_meshes', 'Basic meshes', 'int'],
        ['materialSlotsUsed', 'material_slots', 'Material slots', 'int'], ['boneCount', 'bones', 'Bones', 'int'],
        ['blendShapeCount', 'blend_shapes', 'Blend shapes', 'int'], ['bounds', 'bounds', 'Bounds', 'bounds'],
    ]],
    ['dynamics', 'Dynamics and constraints', [
        ['physBoneComponentCount', 'physbone_components', 'PhysBone components', 'int'], ['physBoneTransformCount', 'physbone_transforms', 'PhysBone affected transforms', 'int'],
        ['physBoneColliderCount', 'physbone_colliders', 'PhysBone colliders', 'int'], ['physBoneCollisionCheckCount', 'physbone_checks', 'PhysBone collision checks', 'int'],
        ['contactCount', 'contacts', 'Contacts', 'int'], ['constraintCount', 'constraints', 'Constraints', 'int'], ['constraintDepth', 'constraint_depth', 'Constraint depth', 'int'],
    ]],
    ['components', 'Components and effects', [
        ['animatorCount', 'animators', 'Animators', 'int'], ['particleSystemCount', 'particle_systems', 'Particle systems', 'int'],
        ['totalMaxParticles', 'particles_active', 'Total particles active', 'int'], ['meshParticleMaxPolygons', 'mesh_particle_tris', 'Mesh particle triangles', 'int'],
        ['lightCount', 'lights', 'Lights', 'int'], ['audioSourceCount', 'audio_sources', 'Audio sources', 'int'], ['raycastCount', 'raycasts', 'Raycasts', 'int'],
        ['clothCount', 'cloths', 'Cloths', 'int'], ['totalClothVertices', 'cloth_vertices', 'Cloth vertices', 'int'],
        ['trailRendererCount', 'trail_renderers', 'Trail renderers', 'int'], ['lineRendererCount', 'line_renderers', 'Line renderers', 'int'],
        ['physicsColliders', 'physics_colliders', 'Physics colliders', 'int'], ['physicsRigidbodies', 'physics_rigidbodies', 'Physics rigidbodies', 'int'],
        ['particleTrailsEnabled', 'particle_trails', 'Particle trails enabled', 'bool'], ['particleCollisionEnabled', 'particle_collision', 'Particle collision enabled', 'bool'],
    ]],
];
const _AV_PERF_PLATFORMS = [['standalonewindows', 'PC'], ['android', 'Android'], ['ios', 'iOS']];

const _AV_PERF_RANKS = ['excellent', 'good', 'medium', 'poor'];
const _AV_PERF_LIMITS = {
    pc: {
        totalPolygons: [32000, 70000, 70000, 70000],
        totalTextureUsage: [40, 75, 110, 150].map(mb => mb * 1048576),
        skinnedMeshCount: [1, 2, 8, 16],
        meshCount: [4, 8, 16, 24],
        materialSlotsUsed: [4, 8, 16, 32],
        physBoneComponentCount: [4, 8, 16, 32],
        physBoneTransformCount: [16, 64, 128, 256],
        physBoneColliderCount: [4, 8, 16, 32],
        physBoneCollisionCheckCount: [32, 128, 256, 512],
        contactCount: [8, 16, 24, 32],
        constraintCount: [100, 250, 300, 350],
        constraintDepth: [20, 50, 80, 100],
        animatorCount: [1, 4, 16, 32],
        boneCount: [75, 150, 256, 400],
        lightCount: [0, 0, 0, 1],
        particleSystemCount: [0, 4, 8, 16],
        totalMaxParticles: [0, 300, 1000, 2500],
        meshParticleMaxPolygons: [0, 1000, 2000, 5000],
        particleTrailsEnabled: [false, false, true, true],
        particleCollisionEnabled: [false, false, true, true],
        trailRendererCount: [1, 2, 4, 8],
        lineRendererCount: [1, 2, 4, 8],
        raycastCount: [1, 4, 8, 15],
        clothCount: [0, 1, 1, 1],
        totalClothVertices: [0, 50, 100, 200],
        physicsColliders: [0, 1, 8, 8],
        physicsRigidbodies: [0, 1, 8, 8],
        audioSourceCount: [1, 4, 8, 8],
        bounds: [[2.5, 2.5, 2.5], [4, 4, 4], [5, 6, 5], [5, 6, 5]],
    },
    mobile: {
        totalPolygons: [7500, 10000, 15000, 20000],
        totalTextureUsage: [10, 18, 25, 40].map(mb => mb * 1048576),
        skinnedMeshCount: [1, 1, 2, 2],
        meshCount: [1, 1, 2, 2],
        materialSlotsUsed: [1, 1, 2, 4],
        animatorCount: [1, 1, 1, 2],
        boneCount: [75, 90, 150, 150],
        physBoneComponentCount: [0, 4, 6, 8],
        physBoneTransformCount: [0, 16, 32, 64],
        physBoneColliderCount: [0, 4, 8, 16],
        physBoneCollisionCheckCount: [0, 16, 32, 64],
        contactCount: [2, 4, 8, 16],
        constraintCount: [30, 60, 120, 150],
        constraintDepth: [5, 15, 35, 50],
        particleSystemCount: [0, 0, 0, 2],
        totalMaxParticles: [0, 0, 0, 200],
        meshParticleMaxPolygons: [0, 0, 0, 400],
        particleTrailsEnabled: [false, false, false, true],
        particleCollisionEnabled: [false, false, false, true],
        trailRendererCount: [0, 0, 0, 1],
        lineRendererCount: [0, 0, 0, 1],
        raycastCount: [1, 2, 4, 8],
        bounds: [[2.5, 2.5, 2.5], [4, 4, 4], [5, 6, 5], [5, 6, 5]],
    },
};

function _avPerfRankOf(platform, key, value) {
    const table = _AV_PERF_LIMITS[platform === 'standalonewindows' ? 'pc' : 'mobile'];
    const limits = table && table[key];
    if (!limits || value === null || value === undefined) return '';
    for (let i = 0; i < limits.length; i++) {
        const lim = limits[i];
        if (key === 'bounds') {
            if (Array.isArray(value) && value.length === 3 && value.every((v, j) => Number(v) <= lim[j] + 0.001)) return _AV_PERF_RANKS[i];
        } else if (typeof lim === 'boolean') {
            if (!value || lim) return _AV_PERF_RANKS[i];
        } else if (Number(value) <= lim) {
            return _AV_PERF_RANKS[i];
        }
    }
    return 'verypoor';
}

function _avPerfRowHtml(label, valueHtml) {
    return `<div class="av-perf-row"><span class="av-perf-row-label">${esc(label)}</span><span class="av-perf-row-value">${valueHtml}</span></div>`;
}

function _avPerfCards(platformKey, platformLabel, a, withPlatform) {
    const stats = a.avatarStats || {};
    const rating = a.performanceRating || '';
    const ic = rating && typeof avatarPerfIcon === 'function' ? avatarPerfIcon(rating, 16) : '';
    const ratingHtml = rating ? `<span class="av-perf-rating av-perf-r-${rating.toLowerCase().replace(/[^a-z]/g, '')}">${ic}${esc(typeof _avPerfPretty === 'function' ? _avPerfPretty(rating) : rating)}</span>` : '-';
    const suffix = withPlatform ? ` · ${esc(platformLabel)}` : '';
    let html = `<div class="fd-info-card av-perf-card">
        <div class="fd-group-rep-label">${esc(t('avatars.perf.title', 'Performance'))}${suffix}</div>
        <div class="av-perf-grid">
            ${_avPerfRowHtml(t('avatars.perf.rating', 'Rating'), ratingHtml)}
            ${_avPerfRowHtml(t('avatars.perf.download_size', 'Download size'), esc(_avPerfMb(a.fileSize)))}
            ${_avPerfRowHtml(t('avatars.perf.uncompressed_size', 'Uncompressed size'), esc(_avPerfMb(a.uncompressedSize)))}
            ${_avPerfRowHtml(t('avatars.perf.texture_memory', 'Texture memory'), `<span class="${(r => r ? 'av-perf-r-' + r : '')(_avPerfRankOf(platformKey, 'totalTextureUsage', stats.totalTextureUsage))}">${esc(_avPerfMb(stats.totalTextureUsage))}</span>`)}
        </div>
    </div>`;
    for (const [gk, gLabel, rows] of _AV_PERF_GROUPS) {
        html += `<div class="fd-info-card av-perf-card"><div class="fd-group-rep-label">${esc(t('avatars.perf.' + gk, gLabel))}${suffix}</div><div class="av-perf-grid">`;
        for (const [key, i18nKey, label, kind] of rows) {
            const v = stats[key];
            const out = kind === 'bounds' ? _avPerfBounds(v) : kind === 'bool' ? _avPerfBool(v) : _avPerfInt(v);
            const rank = _avPerfRankOf(platformKey, key, v);
            html += `<div class="av-perf-row"><span class="av-perf-row-label">${esc(t('avatars.perf.' + i18nKey, label))}</span><span class="av-perf-row-value${rank ? ' av-perf-r-' + rank : ''}" title="${rank ? esc(_avPerfPretty(rank)) : ''}">${esc(out)}</span></div>`;
        }
        html += '</div></div>';
    }
    return html;
}

function _avRenderAnalysis() {
    const wrap = document.querySelector('#avTabInfo .fd-info-wrap');
    if (!wrap) return;
    wrap.querySelectorAll('.av-perf-card').forEach(el => el.remove());
    const id = _avDetailData && _avDetailData.id;
    const entry = id ? _avAnalysisCache[id] : null;
    if (!entry) return;
    const present = _AV_PERF_PLATFORMS.filter(([p]) => entry.platforms && entry.platforms[p]);
    let html = '';
    for (const [p, label] of present) html += _avPerfCards(p, label, entry.platforms[p], present.length > 1);
    if (!html && entry.pending) {
        html = `<div class="fd-info-card av-perf-card"><div class="fd-group-rep-label">${esc(t('avatars.perf.title', 'Performance'))}</div><div class="myp-empty">${esc(t('avatars.perf.pending', 'VRChat is still analyzing this upload. Check back in a few minutes.'))}</div></div>`;
    }
    if (html) wrap.insertAdjacentHTML('beforeend', html);
}

function updateAvatarModalFavBtn(avatarId) {
    if (!_avDetailData || _avDetailData.id !== avatarId) return;
    const isFav = (typeof favAvatarsData !== 'undefined') && favAvatarsData.some(f => f.id === avatarId);
    document.querySelectorAll('button[onclick^="openAvFavPicker("]').forEach(btn => {
        const icon = btn.querySelector('.msi');
        if (icon) {
            icon.textContent = isFav ? 'favorite' : 'favorite_border';
            icon.classList.toggle('fd-action-fav', isFav);
        } else {
            btn.textContent = avatarFavoriteActionLabel(isFav);
        }
        btn.title = avatarFavoriteActionLabel(isFav);
    });
}

function saveAvField(field, avatarId) {
    const a = _avDetailData;
    if (!a) return;
    _avSavingField = field;
    const ids = _avFieldIds[field];
    const saveBtn = document.querySelector(`#${ids.edit} .vrcn-btn-primary`);
    if (saveBtn) {
        saveBtn.disabled = true;
        saveBtn.textContent = t('common.saving', 'Saving...');
    }

    const name = field === 'name' ? (document.getElementById('avNameInput')?.value.trim() || '') : (a.name || '');
    const description = field === 'desc' ? (document.getElementById('avDescInput')?.value ?? '') : (a.description || '');
    const releaseStatus = field === 'visibility' ? _avVisState : (a.releaseStatus || 'private');
    const tags = field === 'tags' ? [..._avEditTags] : (a.tags || []);

    sendToCS({ action: 'vrcUpdateAvatar', avatarId, name, description, releaseStatus, tags });
}


function onAvatarUpdateResult(data) {
    const fieldLabels = {
        name: avatarDetailFieldLabel('name'),
        desc: avatarDetailFieldLabel('desc'),
        visibility: avatarDetailFieldLabel('visibility'),
        tags: avatarDetailFieldLabel('tags'),
    };
    if (data.ok) {
        if (_avDetailData) {
            if (data.name != null) _avDetailData.name = data.name;
            if (data.description != null) _avDetailData.description = data.description;
            if (data.releaseStatus != null) _avDetailData.releaseStatus = data.releaseStatus;
            if (data.tags != null) _avDetailData.tags = data.tags;
        }
        const label = fieldLabels[_avSavingField] || avatarDetailFieldLabel('');
        showToast(true, tf('avatars.detail.toast.saved', { field: label }, '{field} saved'));
        renderAvatarDetail(_avDetailData);
        if (avatarFilter === 'own') filterOwnAvatars();
    } else {
        showToast(false, data.error || t('avatars.detail.toast.update_failed', 'Update failed'));
        const ids = _avSavingField ? _avFieldIds[_avSavingField] : null;
        const saveBtn = ids ? document.querySelector(`#${ids.edit} .vrcn-btn-primary`) : null;
        if (saveBtn) {
            saveBtn.disabled = false;
            saveBtn.textContent = t('common.save', 'Save');
        }
    }
}

function switchAvTab(tab, btn) {
    const infoEl = document.getElementById('avTabInfo');
    const jsonEl = document.getElementById('avTabJson');
    const galleryEl = document.getElementById('avTabGallery');
    if (infoEl) infoEl.style.display = tab === 'info' ? '' : 'none';
    if (jsonEl) jsonEl.style.display = tab === 'json' ? '' : 'none';
    if (galleryEl) galleryEl.style.display = tab === 'gallery' ? '' : 'none';
    document.querySelectorAll('#avatarDetailContent .fd-tab').forEach(t => t.classList.remove('active'));
    if (btn) btn.classList.add('active');
    if (tab === 'gallery' && !_avGalleryLoaded) {
        _avGalleryLoaded = true;
        const avId = _avDetailData?.id;
        if (avId) {
            const gc = document.getElementById('avGalleryContent');
            if (gc) gc.innerHTML = '<div class="myp-empty">' + sk('list') + '</div>';
            sendToCS({ action: 'vrcGetAvatarGallery', avatarId: avId });
        }
    }
}

function avUploadGalleryImage(avatarId) {
    openInvUploadModal('avatarGallery', blob => {
        const btn = document.getElementById('avGalleryUploadBtn');
        if (btn) { btn.disabled = true; btn.innerHTML = '<span class="msi" style="font-size:16px;">hourglass_empty</span> Uploading...'; }
        const reader = new FileReader();
        reader.onload = e => sendToCS({ action: 'vrcUploadAvatarGallery', avatarId, data: e.target.result });
        reader.readAsDataURL(blob);
    });
}

function avUploadBannerImage(avatarId) {
    openInvUploadModal('avatarImage', blob => {
        const reader = new FileReader();
        reader.onload = e => sendToCS({ action: 'vrcUploadAvatarImage', avatarId, data: e.target.result });
        reader.readAsDataURL(blob);
    });
}

function buildAvGalleryCard(img, isOwn) {
    const imgUrl = img.url || '';
    const imgAttr = esc(imgUrl);
    const imgJs = jsq(imgUrl);
    const fileId = jsq(img.id || '');
    const defaultFileBase = img.name || 'image';
    const fileName = jsq(defaultFileBase + '.png');
    const sizeStr = formatFileSize(img.sizeBytes || 0);
    const timeStr = invTimeLabel(img.createdAt);
    const nameDisp = esc(img.name || t('inventory.card.unnamed', 'Unnamed'));
    const noPreviewHtml = `<div class=\\'inv-no-preview\\'>${esc(t('inventory.empty.no_preview', 'No Preview'))}</div>`;

    const actions = `<div class="lib-actions">
        <button class="vrcn-lib-button clip" onclick="event.stopPropagation();invDownload('${imgJs}','${fileName}')" title="${esc(t('inventory.actions.download', 'Download'))}"><span class="msi" style="font-size:16px;">download</span></button>
        ${isOwn ? `<button class="vrcn-lib-button del" onclick="event.stopPropagation();avGalleryConfirmDelete('${fileId}')" title="${esc(t('inventory.actions.delete', 'Delete'))}"><span class="msi" style="font-size:16px;">delete</span></button>` : ''}
    </div>`;

    return `<div class="lib-card inv-card">
        ${actions}
        <div class="lib-thumb-wrap" onclick="openLightbox('${imgJs}','image')">
            ${imgUrl ? `<img class="lib-thumb" src="${imgAttr}" loading="lazy" onerror="this.outerHTML='${noPreviewHtml}'">` : `<div class="inv-no-preview">${esc(t('inventory.empty.no_preview', 'No Preview'))}</div>`}
        </div>
        <div class="lib-info">
            <div class="lib-name">${nameDisp}</div>
            <div class="lib-meta"><span>${sizeStr}</span><span>${timeStr}</span></div>
        </div>
    </div>`;
}

function _renderAvGallery() {
    const gc = document.getElementById('avGalleryContent');
    if (!gc) return;
    const isOwn = !!(currentVrcUser && _avDetailData && _avDetailData.authorId === currentVrcUser.id);
    if (!_avGalleryImages.length) {
        gc.innerHTML = `<div class="myp-empty">${t('avatars.gallery.empty', 'No gallery images')}</div>`;
        return;
    }
    const groups = {};
    _avGalleryImages.forEach(img => {
        const key = invGroupDateLabel(img.createdAt);
        if (!groups[key]) groups[key] = [];
        groups[key].push(img);
    });
    let inner = '';
    for (const [groupLabel, items] of Object.entries(groups)) {
        inner += `<div class="lib-date-group">${esc(groupLabel)}</div>`;
        items.forEach(img => { inner += buildAvGalleryCard(img, isOwn); });
    }
    gc.innerHTML = `<div class="inv-grid" style="overflow:visible;flex:unset;padding:0;">${inner}</div>`;
}

function onAvatarGallery(data) {
    if (!data || !_avDetailData || data.avatarId !== _avDetailData.id) return;
    _avGalleryImages = data.images || [];
    _renderAvGallery();
}

function avGalleryConfirmDelete(fileId) {
    const img = _avGalleryImages.find(i => i.id === fileId);
    showInvDeleteModal('file', fileId, null, img?.name || t('inventory.delete.this_item', 'this item'));
}

function onAvGalleryItemDeleted(fileId) {
    _avGalleryImages = _avGalleryImages.filter(i => i.id !== fileId);
    _renderAvGallery();
}

function onAvatarGalleryResult(data) {
    const btn = document.getElementById('avGalleryUploadBtn');
    if (btn) { btn.disabled = false; btn.innerHTML = `<span class="msi" style="font-size:16px;">upload</span> ${esc(t('avatars.gallery.upload_image', 'Upload Image'))}`; }
    if (data.ok) {
        showToast(true, t('avatars.gallery.uploaded', 'Gallery image uploaded!'));
        onAvatarGallery(data);
    } else {
        showToast(false, data.error || t('avatars.gallery.upload_failed', 'Upload failed'));
    }
}

function onAvatarImageResult(data) {
    if (data.ok) {
        showToast(true, t('avatars.detail.toast.image_updated', 'Avatar image updated!'));
        if (data.imageUrl && _avDetailData) {
            _avDetailData.thumbnailImageUrl = data.imageUrl;
            _avDetailData.imageUrl = data.imageUrl;
            renderAvatarDetail(_avDetailData);
        }
    } else {
        showToast(false, data.error || t('avatars.detail.toast.image_update_failed', 'Image update failed'));
    }
}

function avRenderTagChips() {
    const container = document.getElementById('avTagsChips');
    if (!container) return;
    container.innerHTML = _avEditTags.length
        ? _avEditTags.map((tag, i) =>
            `<span class="myp-lang-chip" data-idx="${i}">${esc(tag)}<span class="myp-lang-remove" onclick="avRemoveTag(${i})">&times;</span></span>`
        ).join('')
        : `<div class="myp-empty">${t('avatars.detail.empty_tags', 'No tags')}</div>`;
}

function _avWmState(s) {
    if (s === undefined) return {
        detail:        _avDetailData,
        editTags:      _avEditTags,
        galleryLoaded: _avGalleryLoaded,
        galleryImages: _avGalleryImages,
        savingField:   _avSavingField,
        visState:      _avVisState,
    };
    s = s || {};
    _avDetailData    = s.detail        ?? null;
    _avEditTags      = s.editTags      ?? [];
    _avGalleryLoaded = s.galleryLoaded ?? false;
    _avGalleryImages = s.galleryImages ?? [];
    _avSavingField   = s.savingField   ?? '';
    _avVisState      = s.visState      ?? 'public';
}

function confirmDeleteAvatar(avatarId, avatarName) {
    vrcnConfirmDelete({
        id: 'avatarDeleteModal',
        title: t('avatars.detail.actions.delete', 'Delete Avatar'),
        icon: 'delete',
        message: tf('avatars.detail.delete_confirm', { name: avatarName || avatarId },
            'Delete "{name}"? The avatar is hidden and its files are removed. This cannot be undone.'),
        confirmLabel: t('common.delete', 'Delete'),
        onConfirm: () => sendToCS({ action: 'vrcDeleteAvatar', avatarId }),
    });
}

function onAvatarDeleteResult(data) {
    if (typeof avBulkDeleteConsume === 'function' && avBulkDeleteConsume(!!data.ok)) return;
    if (data.ok) {
        showToast(true, t('avatars.detail.toast.deleted', 'Avatar deleted'));
        closeAvatarDetail();
        if (typeof avatarsData !== 'undefined') avatarsData = avatarsData.filter(a => a.id !== data.avatarId);
        if (typeof avatarFavData !== 'undefined') avatarFavData = avatarFavData.filter(a => a.id !== data.avatarId);
        if (typeof renderAvatarsListView === 'function') renderAvatarsListView();
        else if (typeof filterOwnAvatars === 'function') filterOwnAvatars();
    } else {
        showToast(false, data.error || t('avatars.detail.toast.delete_failed', 'Delete failed'));
    }
}
