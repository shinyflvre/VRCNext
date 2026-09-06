/* === User Modal (Friend / Profile Detail) === */
const _fdRawJsonCache = {};

let _fdGroupsSortMode = 'alpha';
let _fdMutualsSortMode = 'alpha';
let _fdMutualsGroupsSortMode = 'alpha';

function _fdSortGroups(arr, mode) {
    const a = (arr || []).slice();
    if (mode === 'members') {
        a.sort((x, y) => (y.memberCount || 0) - (x.memberCount || 0));
    } else {
        a.sort((x, y) => (x.name || '').localeCompare(y.name || '', undefined, { sensitivity: 'base' }));
    }
    return a;
}

function _fdSortMutuals(arr, mode) {
    const a = (arr || []).slice();
    if (mode === 'favorites') {
        const favIds = new Set((typeof favFriendsData !== 'undefined' ? favFriendsData : []).map(f => f.favoriteId));
        a.sort((x, y) => {
            const fx = favIds.has(x.id) ? 0 : 1;
            const fy = favIds.has(y.id) ? 0 : 1;
            if (fx !== fy) return fx - fy;
            return (x.displayName || '').localeCompare(y.displayName || '', undefined, { sensitivity: 'base' });
        });
    } else {
        a.sort((x, y) => (x.displayName || '').localeCompare(y.displayName || '', undefined, { sensitivity: 'base' }));
    }
    return a;
}

function setFdGroupsSort(v) { _fdGroupsSortMode = v; window._fdGroupsPage = 0; window._fdOwnGroupsPage = 0; filterFdGroups(); filterFdOwnGroups(); }
function setFdMutualsSort(v) { _fdMutualsSortMode = v; window._fdMutualsPage = 0; filterFdMutuals(); }
function setFdMutualsGroupsSort(v) { _fdMutualsGroupsSortMode = v; window._fdMutualsGroupsPage = 0; filterFdMutualsGroups(); }

function setFdMutualsSortValue(v) {
    if (_fdMutualsActivePill === 'groups') setFdMutualsGroupsSort(v);
    else setFdMutualsSort(v);
}

let _fdMutualsActivePill = 'friends';

function _fdMutualsSortOptions(pill) {
    const alpha = `<option value="alpha">${esc(t('profiles.sort.alphabetical', 'Alphabetical'))}</option>`;
    return pill === 'groups'
        ? alpha + `<option value="members">${esc(t('profiles.sort.members', 'Members'))}</option>`
        : alpha + `<option value="favorites">${esc(t('profiles.sort.favorites', 'Favorites'))}</option>`;
}

function _fdMutualsRebuildSort(pill) {
    const sel = document.getElementById('fdMutualsSort');
    if (!sel) return;

    const wrap = sel.closest('.vn-select');
    if (wrap && wrap.parentNode) {
        wrap.parentNode.insertBefore(sel, wrap);
        wrap.remove();
    }
    sel._vnSelect = false;
    sel.style.display = '';
    sel.style.flexShrink = '0';

    sel.innerHTML = _fdMutualsSortOptions(pill);
    sel.value = pill === 'groups' ? _fdMutualsGroupsSortMode : _fdMutualsSortMode;
    if (!sel.value) sel.value = 'alpha';

    if (typeof initVnSelect === 'function') initVnSelect(sel);

    const hasList = !!document.getElementById(pill === 'groups' ? 'fdMutualsGroupsSearch' : 'fdMutualsSearch');
    const newWrap = sel.closest('.vn-select') || sel;
    newWrap.style.display = hasList ? '' : 'none';
}

const _fdBioTransReqs = {};

function fdTranslateBio(btn) {
    const card = btn.closest('.fd-info-card');
    const targetSel = btn.dataset.fdTransTarget || '.fd-bio';
    const bioEl = card?.querySelector(targetSel);
    if (!bioEl) return;

    if (bioEl.dataset.fdBioTranslated === '1' && bioEl.dataset.fdBioOriginal != null) {
        bioEl.textContent = bioEl.dataset.fdBioOriginal;
        bioEl.dataset.fdBioTranslated = '0';
        btn.classList.remove('active');
        const ic = btn.querySelector('.msi'); if (ic) ic.textContent = 'translate';
        return;
    }

    const original = bioEl.dataset.fdBioOriginal != null ? bioEl.dataset.fdBioOriginal : (bioEl.textContent || '');
    if (!original.trim()) return;

    if (!window._kxdApiKeyPresent) {
        if (typeof showToast === 'function') showToast(false, t('profiles.bio.translate_no_key', 'Set your Groq API key in Kikitan XD first.'));
        if (typeof navClear === 'function') navClear();
        if (typeof showTab === 'function') showTab(22);
        return;
    }

    const reqId = 'bt_' + Date.now() + '_' + Math.random().toString(36).slice(2, 8);
    bioEl.dataset.fdBioOriginal = original;
    _fdBioTransReqs[reqId] = { btn, bioEl };
    const ic = btn.querySelector('.msi'); if (ic) ic.textContent = 'progress_activity';
    btn.disabled = true;
    sendToCS({ action: 'kxdTranslateProfileText', reqId, text: original, targetLang: window._kxdProfileTargetLang || 'en' });
}

function handleKxdProfileTranslated(p) {
    const reqId = p && p.reqId;
    const ctx = reqId && _fdBioTransReqs[reqId];
    if (!ctx) return;
    delete _fdBioTransReqs[reqId];
    const { btn, bioEl } = ctx;
    btn.disabled = false;
    const ic = btn.querySelector('.msi'); if (ic) ic.textContent = 'translate';
    if (!p.ok || !p.text) {
        if (typeof showToast === 'function') showToast(false, t('profiles.bio.translate_failed', 'Translation failed.'));
        return;
    }
    if (!bioEl.isConnected) return;
    bioEl.dataset.fdBioTranslated = '1';
    bioEl.textContent = p.text;
    btn.classList.add('active');
}

function fdEditNote() {
    document.getElementById('fdVrcNoteView')?.style.setProperty('display', 'none');
    const edit = document.getElementById('fdVrcNoteEdit');
    if (edit) edit.style.display = '';
    const inp = document.getElementById('fdVrcNoteInput');
    if (inp) { inp.value = currentFriendDetail?.note || ''; inp.focus(); }
}

function fdCancelNote() {
    const view = document.getElementById('fdVrcNoteView');
    if (view) view.style.display = '';
    const edit = document.getElementById('fdVrcNoteEdit');
    if (edit) edit.style.display = 'none';
    const btn = document.getElementById('fdVrcNoteSaveBtn');
    if (btn) btn.disabled = false;
}

function fdSaveNote() {
    const inp = document.getElementById('fdVrcNoteInput');
    if (!inp || !currentFriendDetail) return;
    const btn = document.getElementById('fdVrcNoteSaveBtn');
    if (btn) btn.disabled = true;
    sendToCS({ action: 'vrcUpdateNote', userId: currentFriendDetail.id, note: inp.value });
}

function _fdMemoDefault() {
    const m = (currentFriendDetail?.memo || '').trim();
    return m || (currentFriendDetail?.displayName || '');
}

function fdEditMemo() {
    document.getElementById('fdMemoView')?.style.setProperty('display', 'none');
    const edit = document.getElementById('fdMemoEdit');
    if (edit) edit.style.display = '';
    const inp = document.getElementById('fdMemoInput');
    if (inp) { inp.value = _fdMemoDefault(); inp.focus(); inp.select(); }
}

function fdCancelMemo() {
    const view = document.getElementById('fdMemoView');
    if (view) view.style.display = '';
    const edit = document.getElementById('fdMemoEdit');
    if (edit) edit.style.display = 'none';
}

function fdSaveMemo() {
    const inp = document.getElementById('fdMemoInput');
    if (!inp || !currentFriendDetail) return;
    const val = inp.value.trim();
    sendToCS({ action: 'setUserMemo', userId: currentFriendDetail.id, memo: val });
    currentFriendDetail.memo = val;
    const view = document.getElementById('fdMemoView');
    if (view) view.textContent = _fdMemoDefault();
    fdCancelMemo();
}

function openFriendDetail(userId) {
    if (typeof navSetCurrent === 'function') navSetCurrent('friend', userId);
    const m = document.getElementById('modalFriendDetail');
    const c = document.getElementById('friendDetailContent');
    c.innerHTML = sk('content-modal-compact');
    if (typeof vrcnPlusOnProfileOpened === 'function') vrcnPlusOnProfileOpened(userId);
    m.style.display = 'flex';
    sendToCS({ action: 'vrcGetFriendDetail', userId: userId });
}

let _fdLoadedAvatarKey = '';
let _fdLastAvatarPayload = null;
let _fdLastAvatarUserId = '';

function closeFriendDetail(fromNav = false) {
    if (_fdLiveTimer) { clearInterval(_fdLiveTimer); _fdLiveTimer = null; }
    document.getElementById('modalFriendDetail').style.display = 'none';
    currentFriendDetail = null;
    window._fdAllMutuals = null;
    _fdLoadedAvatarKey = '';
    _fdLastAvatarPayload = null;
    _fdLastAvatarUserId = '';
    if (!fromNav && typeof navClear === 'function') navClear();
}



const ROBOT_AVATAR_ID = 'avtr_c38a1615-5bf5-42b4-84eb-a8b6c37cbd11';
const HIDDEN_AVATAR_FILE_ID = 'file_0e8c4e32-7444-44ea-ade4-313c010d4bae';

function avatarNotInDbLabel() {
    return t('profiles.badges.avatar_not_in_db', 'Avatar not found in any database');
}

function avatarNotInDbLockHtml(unresolved) {
    const icon  = unresolved ? 'hourglass_empty' : 'lock';
    const title = unresolved
        ? t('profiles.badges.avatar_unresolved', 'Unresolved, IDs are rechecked every 10 minutes. If it takes too long, open the profile or use Check for Avatar.')
        : avatarNotInDbLabel();
    return `<span class="msi" title="${esc(title)}" style="font-size:15px;color:var(--tx2);flex-shrink:0;margin-left:auto;">${icon}</span>`;
}
function _applyAvatarSection(payload) {
    const section = document.getElementById('fdAvatarSection');
    if (!section) return;
    if (payload?.userId && currentFriendDetail && payload.userId !== currentFriendDetail.id) return;
    const avId   = payload?.avatarId || '';
    const avName = payload?.avatarName || '';
    if (!avId && !avName) return;
    if (avId === ROBOT_AVATAR_ID) { section.style.display = 'none'; return; }
    const fallbackImg = currentFriendDetail?.currentAvatarImageUrl || '';
    const avImg = payload.avatarImage || (fallbackImg.includes(HIDDEN_AVATAR_FILE_ID) ? '' : fallbackImg);
    const avIcon = avImg
        ? `<img class="fd-group-icon" src="${esc(imgThumb(avImg, 96))}" onerror="this.style.display='none'">`
        : `<div class="fd-group-icon fd-group-icon-empty"><span class="msi" style="font-size:18px;">checkroom</span></div>`;
    const authorHtml = payload.avatarAuthor
        ? `<div class="fd-group-card-meta">${esc(payload.avatarAuthor)}</div>` : '';
    const cardAttrs = avId
        ? `onclick="navOpenModal('avatar','${jsq(avId)}','${jsq(avName)}')"`
        : `style="cursor:default;"`;
    const lockHtml = avId ? '' : avatarNotInDbLockHtml(!!payload.avatarUnresolved);
    section.style.display = '';
    setHtmlIfChanged(section, `<div class="fd-group-rep-label">${t('profiles.badges.current_avatar', 'Current Avatar')}</div>
        <div class="fd-group-card fd-group-rep" ${cardAttrs}>
            ${avIcon}<div class="fd-group-card-info"><div class="fd-group-card-name">${esc(avName || avId)}</div>${authorHtml}</div>${lockHtml}
        </div>`);
}

function handleAvatarByFileId(payload) {
    const forOpenProfile = !payload.userId || (!!currentFriendDetail && payload.userId === currentFriendDetail.id);
    if (!payload.avatarId && !payload.avatarName) {
        if (payload.openModal) showToast(false, t('context_menu.avatar_not_found', 'No public avatar found'));
        return;
    }
    if (forOpenProfile) {
        _fdLastAvatarPayload = payload;
        _applyAvatarSection(payload);
    }
    if (!payload.avatarId) {
        if (payload.openModal) showToast(false, t('context_menu.avatar_not_found', 'No public avatar found'));
        return;
    }
    if (payload.openModal) navOpenModal('avatar', payload.avatarId, payload.avatarName || '');
}

function filterFdGroups() {
    const q = document.getElementById('fdGroupsSearch')?.value.trim().toLowerCase() || '';
    const grid = document.getElementById('fdGroupsGrid');
    if (!grid) return;
    const all = window._fdAllGroupsAll || window._fdAllGroups || [];
    const filtered = q ? all.filter(g => (g.name || '').toLowerCase().includes(q)) : all;
    const otherGroups = _fdSortGroups(filtered, _fdGroupsSortMode);
    const totalPages = Math.ceil(otherGroups.length / MINI_PG_SIZE) || 1;
    if ((window._fdGroupsPage || 0) >= totalPages) window._fdGroupsPage = totalPages - 1;
    const page = window._fdGroupsPage || 0;
    const slice = otherGroups.slice(page * MINI_PG_SIZE, (page + 1) * MINI_PG_SIZE);
    if (slice.length > 0) {
        grid.innerHTML = slice.map(g => {
            const gIcon = g.iconUrl ? `<img class="fd-group-icon" src="${imgThumb(g.iconUrl, 96)}" onerror="this.style.display='none'">` : `<div class="fd-group-icon fd-group-icon-empty"><span class="msi" style="font-size:18px;">group</span></div>`;
            return `<div class="fd-group-card" onclick="navOpenModal('group','${jsq(g.id)}','${jsq(g.name || '')}')">
                ${gIcon}<div class="fd-group-card-info"><div class="fd-group-card-name">${esc(g.name)}</div><div class="fd-group-card-meta">${g.memberCount ? esc(getGroupMemberText(g.memberCount, false)) : ''}</div></div>
            </div>`;
        }).join('');
    } else {
        grid.innerHTML = `<div style="padding:12px;grid-column:1/-1;text-align:center;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('profiles.groups.no_results', 'No results')}</div>`;
    }
    setMiniPaginator('fdGroupsPaginatorBar', buildMiniPaginator(page, totalPages, 'fdGroupsGoPage'));
}

function fdGroupsGoPage(page) {
    if (page < 0) return;
    const q = (document.getElementById('fdGroupsSearch')?.value || '').toLowerCase();
    const all = window._fdAllGroupsAll || window._fdAllGroups || [];
    const filtered = _fdSortGroups(q ? all.filter(g => (g.name||'').toLowerCase().includes(q)) : all, _fdGroupsSortMode);
    const totalPages = Math.ceil(filtered.length / MINI_PG_SIZE) || 1;
    if (page >= totalPages) return;
    window._fdGroupsPage = page;
    filterFdGroups();
}

function filterFdOwnGroups() {
    const grid = document.getElementById('fdOwnGroupsGrid');
    if (!grid) return;
    const all = _fdSortGroups(window._fdAllOwnGroups || [], _fdGroupsSortMode);
    const totalPages = Math.ceil(all.length / MINI_PG_SIZE) || 1;
    if ((window._fdOwnGroupsPage || 0) >= totalPages) window._fdOwnGroupsPage = totalPages - 1;
    const page = window._fdOwnGroupsPage || 0;
    const slice = all.slice(page * MINI_PG_SIZE, (page + 1) * MINI_PG_SIZE);
    if (slice.length > 0) {
        grid.innerHTML = slice.map(g => {
            const gIcon = g.iconUrl ? `<img class="fd-group-icon" src="${imgThumb(g.iconUrl, 96)}" onerror="this.style.display='none'">` : `<div class="fd-group-icon fd-group-icon-empty"><span class="msi" style="font-size:18px;">group</span></div>`;
            return `<div class="fd-group-card" onclick="navOpenModal('group','${jsq(g.id)}','${jsq(g.name || '')}')">
                ${gIcon}<div class="fd-group-card-info"><div class="fd-group-card-name">${esc(g.name)}</div><div class="fd-group-card-meta">${g.memberCount ? esc(getGroupMemberText(g.memberCount, false)) : ''}</div></div>
            </div>`;
        }).join('');
    } else {
        grid.innerHTML = `<div style="padding:12px;grid-column:1/-1;text-align:center;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('profiles.groups.no_results', 'No results')}</div>`;
    }
    setMiniPaginator('fdOwnGroupsPaginatorBar', buildMiniPaginator(page, totalPages, 'fdOwnGroupsGoPage'));
}

function fdOwnGroupsGoPage(page) {
    if (page < 0) return;
    const totalPages = Math.ceil((window._fdAllOwnGroups || []).length / MINI_PG_SIZE) || 1;
    if (page >= totalPages) return;
    window._fdOwnGroupsPage = page;
    filterFdOwnGroups();
}

function filterFdMutualsGroups() {
    const q = document.getElementById('fdMutualsGroupsSearch')?.value.trim().toLowerCase() || '';
    const grid = document.getElementById('fdMutualsGroupsGrid');
    if (!grid) return;
    const all = window._fdAllMutualGroups || [];
    const filtered = _fdSortGroups(q ? all.filter(g => (g.name || '').toLowerCase().includes(q)) : all, _fdMutualsGroupsSortMode);
    const totalPages = Math.ceil(filtered.length / MINI_PG_SIZE) || 1;
    if ((window._fdMutualsGroupsPage || 0) >= totalPages) window._fdMutualsGroupsPage = totalPages - 1;
    const page = window._fdMutualsGroupsPage || 0;
    const slice = filtered.slice(page * MINI_PG_SIZE, (page + 1) * MINI_PG_SIZE);
    if (slice.length > 0) {
        grid.innerHTML = slice.map(g => {
            const icon = g.iconUrl
                ? `<img class="fd-group-icon" src="${esc(imgThumb(g.iconUrl, 96))}" onerror="this.style.display='none'">`
                : `<div class="fd-group-icon fd-group-icon-empty"><span class="msi" style="font-size:18px;">group</span></div>`;
            return `<div class="fd-group-card" style="margin-bottom:0;" onclick="navOpenModal('group','${jsq(g.id)}','${jsq(g.name || '')}')">
                ${icon}<div class="fd-group-card-info">
                    <div class="fd-group-card-name">${esc(g.name)}</div>
                    <div class="fd-group-card-meta">${esc(g.shortCode || '')}${g.discriminator ? '.' + esc(g.discriminator) : ''} &middot; ${esc(getGroupMemberText(g.memberCount))}</div>
                </div>
            </div>`;
        }).join('');
    } else {
        grid.innerHTML = `<div style="padding:12px;grid-column:1/-1;text-align:center;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('profiles.mutuals.groups_no_results', 'No results')}</div>`;
    }
    setMiniPaginator('fdMutualsGroupsPageBar', buildMiniPaginator(page, totalPages, 'fdMutualsGroupsGoPage'));
}

function fdMutualsGroupsGoPage(page) {
    if (page < 0) return;
    const q = (document.getElementById('fdMutualsGroupsSearch')?.value || '').toLowerCase();
    const all = window._fdAllMutualGroups || [];
    const filtered = _fdSortGroups(q ? all.filter(g => (g.name||'').toLowerCase().includes(q)) : all, _fdMutualsGroupsSortMode);
    const totalPages = Math.ceil(filtered.length / MINI_PG_SIZE) || 1;
    if (page >= totalPages) return;
    window._fdMutualsGroupsPage = page;
    filterFdMutualsGroups();
}

function filterFdMutuals() {
    const q = document.getElementById('fdMutualsSearch')?.value.trim().toLowerCase() || '';
    const grid = document.getElementById('fdMutualsGrid');
    if (!grid) return;
    const all = window._fdAllMutuals || [];
    const filtered = _fdSortMutuals(q ? all.filter(m => (m.displayName || '').toLowerCase().includes(q)) : all, _fdMutualsSortMode);
    const totalPages = Math.ceil(filtered.length / MINI_PG_SIZE) || 1;
    if ((window._fdMutualsPage || 0) >= totalPages) window._fdMutualsPage = totalPages - 1;
    const page = window._fdMutualsPage || 0;
    const slice = filtered.slice(page * MINI_PG_SIZE, (page + 1) * MINI_PG_SIZE);
    grid.innerHTML = slice.length
        ? slice.map(mu => {
            const thumbUrl = mu.currentAvatarThumbnailImageUrl || '';
            const opts = thumbUrl ? { attrs: `data-avatar-thumb="${esc(thumbUrl)}"` } : undefined;
            return renderProfileItem(mu, `navOpenModal('friend','${jsq(mu.id)}','${jsq(mu.displayName || '')}')`, opts);
        }).join('')
        : `<div style="padding:12px;grid-column:1/-1;text-align:center;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('profiles.mutuals.no_results', 'No results')}</div>`;
    setMiniPaginator('fdMutualsPageBar', buildMiniPaginator(page, totalPages, 'fdMutualsGoPage'));
}

function fdMutualsGoPage(page) {
    if (page < 0) return;
    const q = (document.getElementById('fdMutualsSearch')?.value || '').toLowerCase();
    const all = window._fdAllMutuals || [];
    const filtered = _fdSortMutuals(q ? all.filter(m => (m.displayName||'').toLowerCase().includes(q)) : all, _fdMutualsSortMode);
    const totalPages = Math.ceil(filtered.length / MINI_PG_SIZE) || 1;
    if (page >= totalPages) return;
    window._fdMutualsPage = page;
    filterFdMutuals();
}

function switchFdTab(tab, btn) {
    const box = document.querySelector('#modalFriendDetail .modal-box');
    const favsEl = document.getElementById('fdTabFavs');
    animateModalBox(box, () => {
        document.getElementById('fdTabInfo').style.display = tab === 'info' ? '' : 'none';
        document.getElementById('fdTabGroups').style.display = tab === 'groups' ? '' : 'none';
        const mutualsEl = document.getElementById('fdTabMutuals');
        if (mutualsEl) mutualsEl.style.display = tab === 'mutuals' ? '' : 'none';
        const contentEl = document.getElementById('fdTabContent');
        if (contentEl) contentEl.style.display = tab === 'content' ? '' : 'none';
        if (favsEl) favsEl.style.display = tab === 'favs' ? '' : 'none';
        const jsonEl = document.getElementById('fdTabJson');
        if (jsonEl) jsonEl.style.display = tab === 'json' ? '' : 'none';
        document.querySelectorAll('.fd-tab').forEach(t => t.classList.remove('active'));
        if (btn) btn.classList.add('active');
    });
    if (tab === 'favs') {
        const uid = favsEl?.dataset.userId;
        if (uid && !favsEl.dataset.loaded) {
            favsEl.dataset.loaded = '1';
            if (!favsEl.querySelector('.fd-content-pills'))
                favsEl.innerHTML = `<div class="empty-msg">${t('profiles.favs.loading', 'Loading favorites...')}</div>`;
            sendToCS({ action: 'vrcGetUserFavWorlds', userId: uid });
        }
    }
}

function renderUserFavWorlds(payload) {
    const el = document.getElementById('fdTabFavs');
    if (!el || el.dataset.userId !== payload.userId) return;
    const groups = payload.groups || [];
    if (!groups.length) {
        el.innerHTML = `<div class="empty-msg">${t('profiles.favs.none', 'No public favorite worlds.')}</div>`;
        return;
    }

    let activePill = 0;
    const existingPill = el.querySelector('.fd-content-pill.active');
    if (existingPill) {
        const idx = [...el.querySelectorAll('.fd-content-pill')].indexOf(existingPill);
        if (idx >= 0) activePill = idx;
    }

    let pillsHtml = `<div class="fd-content-pills">`;
    groups.forEach((g, i) => {
        const label = esc(g.displayName || g.name);
        const count = g.worlds ? g.worlds.length : 0;
        pillsHtml += `<button class="fd-tab fd-content-pill${i === activePill ? ' active' : ''}" onclick="switchFavPill(${i},this)">${label} <span class="vrcn-badge fd-tab-badge">${count}</span></button>`;
    });
    pillsHtml += `</div>`;

    let panelsHtml = '';
    groups.forEach((g, i) => {
        panelsHtml += `<div id="fdFavPanel_${i}" style="${i !== activePill ? 'display:none;' : ''}">`;
        if (g.visibility === 'private') {
            panelsHtml += `<div class="empty-msg">${t('profiles.favs.private', 'This list is private.')}</div>`;
        } else if (!g.worlds || !g.worlds.length) {
            panelsHtml += `<div class="empty-msg">${t('profiles.favs.empty_group', 'Empty.')}</div>`;
        } else {
            panelsHtml += `<div class="vrcn-world-grid-small">`;
            for (const w of g.worlds) {
                const thumb = w.thumbnailImageUrl || '';
                panelsHtml += `<div class="vrcn-world-card-small" onclick="navOpenModal('worldSearch','${jsq(w.id)}','${jsq(w.name || '')}')">
                    <div class="vwcs-bg"${thumb ? ` style="background-image:url('${cssUrl(imgThumb(thumb, 256))}')"` : ''}></div>
                    <div class="vwcs-scrim"></div>
                    <div class="vwcs-info">
                        <div class="vwcs-name">${esc(w.name)}</div>
                        <div class="vwcs-meta"><span class="msi" style="font-size:11px;">person</span>${w.occupants} <span class="msi" style="font-size:11px;">favorite</span>${w.favorites}</div>
                    </div>
                </div>`;
            }
            panelsHtml += `</div>`;
        }
        panelsHtml += `</div>`;
    });

    el.innerHTML = pillsHtml + panelsHtml;
}

function switchFavPill(idx, btn) {
    const el = document.getElementById('fdTabFavs');
    if (!el) return;
    el.querySelectorAll('[id^="fdFavPanel_"]').forEach((p, i) => p.style.display = i === idx ? '' : 'none');
    el.querySelectorAll('.fd-content-pill').forEach(p => p.classList.remove('active'));
    if (btn) btn.classList.add('active');
}

function switchFdContentPill(pill, btn) {
    const worldsEl = document.getElementById('fdContentWorlds');
    const avatarsEl = document.getElementById('fdContentAvatars');
    if (worldsEl) worldsEl.style.display = pill === 'worlds' ? '' : 'none';
    if (avatarsEl) avatarsEl.style.display = pill === 'avatars' ? '' : 'none';
    document.querySelectorAll('.fd-content-pill').forEach(p => p.classList.remove('active'));
    if (btn) btn.classList.add('active');
}

function switchFdMutualsPill(pill, btn) {
    const isFriends = pill === 'friends';
    _fdMutualsActivePill = isFriends ? 'friends' : 'groups';
    const friendsEl = document.getElementById('fdMutualsFriends');
    const groupsEl  = document.getElementById('fdMutualsGroups');
    if (friendsEl) friendsEl.style.display = isFriends ? '' : 'none';
    if (groupsEl)  groupsEl.style.display  = isFriends ? 'none' : '';

    const show = (id, on) => {
        const el = document.getElementById(id);
        if (!el) return;
        (el.closest('.vn-select') || el).style.display = on ? '' : 'none';
    };
    show('fdMutualsSearch',       isFriends);
    show('fdMutualsGroupsSearch', !isFriends);
    show('fdMutualsSearchIco',    !!document.getElementById(isFriends ? 'fdMutualsSearch' : 'fdMutualsGroupsSearch'));
    _fdMutualsRebuildSort(_fdMutualsActivePill);

    document.querySelectorAll('.fd-mutual-pill').forEach(p => p.classList.remove('active'));
    if (btn) btn.classList.add('active');
}

function renderFdUserAvatars(payload) {
    if (!currentFriendDetail || currentFriendDetail.id !== payload.userId) return;
    const avatars = payload.avatars || [];

    const avatarsPill = document.getElementById('fdAvatarsPill');
    if (avatarsPill) avatarsPill.innerHTML = `${t('profiles.content.avatars_pill_label', 'Avatars')} <span class="vrcn-badge fd-tab-badge">${avatars.length}</span>`;

    const worldsCount = Array.isArray(currentFriendDetail?.userWorlds) ? currentFriendDetail.userWorlds.length : 0;
    const contentTab = document.getElementById('fdTabContentBtn');
    if (contentTab) contentTab.innerHTML = `${t('profiles.tabs.content_label', 'Content')} <span class="vrcn-badge fd-tab-badge">${worldsCount + avatars.length}</span>`;

    window._fdAllAvatars = avatars;
    window._fdAvatarsPage = 0;
    updateTrustBar('fdTrustBarSlot', currentFriendDetail, avatars.length);
    renderFdAvatarsPage(0);
}

function renderFdAvatarsPage(page) {
    const grid = document.getElementById('fdAvatarsGrid');
    if (!grid) return;
    const avatars = window._fdAllAvatars || [];
    const totalPages = Math.ceil(avatars.length / MINI_CONTENT_PG_SIZE) || 1;
    if (page >= totalPages) page = totalPages - 1;
    if (page < 0) page = 0;
    window._fdAvatarsPage = page;
    const slice = avatars.slice(page * MINI_CONTENT_PG_SIZE, (page + 1) * MINI_CONTENT_PG_SIZE);
    if (!slice.length) {
        grid.innerHTML = `<div class="empty-msg">${t('profiles.content.no_public_avatars', 'No public avatars found.')}</div>`;
        setMiniPaginator('fdAvatarsPageBar', '');
        return;
    }
    grid.innerHTML = '<div class="vrcn-mini-content-grid">' + slice.map(a => {
        const thumb = a.thumbnailImageUrl || a.imageUrl || '';
        const aid = jsq(a.id || '');
        const aname = jsq(a.name || '');
        const isPublic = a.releaseStatus === 'public';
        const platBadges = _avPlatformBadges(a);
        const pubBadge = `<span class="vrcn-badge" style="${isPublic ? '' : 'background:rgba(255,100,100,.15);color:var(--err);'}">${isPublic ? t('avatars.labels.public','Public') : t('avatars.labels.private','Private')}</span>`;
        return `<div class="vrcn-mini-content" data-avatar-id="${esc(a.id || '')}" onclick="navOpenModal('avatar','${aid}','${aname}')">
            <div class="vrcn-mini-content-thumb" style="background-image:url('${cssUrl(imgThumb(thumb, 128))}')"></div>
            <div class="vrcn-mini-content-info">
                <div class="vrcn-mini-content-name">${esc(a.name || t('avatars.labels.unnamed','Unnamed'))}</div>
                <div class="vrcn-mini-content-meta">${esc(a.authorName || '')}</div>
                <div class="vrcn-mini-content-badges">${platBadges}${pubBadge}</div>
            </div>
        </div>`;
    }).join('') + '</div>';
    setMiniPaginator('fdAvatarsPageBar', buildMiniPaginator(page, totalPages, 'fdAvatarsGoPage'));
    _checkAvatarsExist(slice.map(a => a.id).filter(Boolean));
}

function fdAvatarsGoPage(page) {
    if (page < 0) return;
    const totalPages = Math.ceil((window._fdAllAvatars || []).length / MINI_CONTENT_PG_SIZE) || 1;
    if (page >= totalPages) return;
    window._fdAvatarsPage = page;
    renderFdAvatarsPage(page);
}

function renderFdWorldsPage(page) {
    const grid = document.getElementById('fdWorldsGrid');
    if (!grid) return;
    const worlds = window._fdAllWorlds || [];
    const totalPages = Math.ceil(worlds.length / MINI_CONTENT_PG_SIZE) || 1;
    if (page >= totalPages) page = totalPages - 1;
    if (page < 0) page = 0;
    window._fdWorldsPage = page;
    const slice = worlds.slice(page * MINI_CONTENT_PG_SIZE, (page + 1) * MINI_CONTENT_PG_SIZE);
    if (!slice.length) {
        grid.innerHTML = `<div class="empty-msg">${t('profiles.content.no_public_worlds', 'No public worlds found.')}</div>`;
        setMiniPaginator('fdWorldsPageBar', '');
        return;
    }
    let h = `<div class="vrcn-mini-content-grid">`;
    slice.forEach(w => {
        const thumb = w.thumbnailImageUrl || w.imageUrl || '';
        const wid = jsq(w.id);
        const tags = (w.tags || []).filter(tag => tag.startsWith('author_tag_')).map(tag => tag.replace('author_tag_', '')).slice(0, 2);
        const tagsHtml = tags.map(tag => `<span class="vrcn-badge">${esc(tag)}</span>`).join('');
        h += `<div class="vrcn-mini-content" data-world-id="${esc(w.id || '')}" onclick="navOpenModal('worldSearch','${wid}','${jsq(w.name || '')}')">
            <div class="vrcn-mini-content-thumb" style="background-image:url('${cssUrl(imgThumb(thumb, 128))}')"></div>
            <div class="vrcn-mini-content-info">
                <div class="vrcn-mini-content-name">${esc(w.name || '')}</div>
                <div class="vrcn-mini-content-meta">${esc(w.authorName || '')}<span class="msi">person</span>${w.occupants ?? ''}<span class="msi">favorite</span>${w.favorites ?? ''}</div>
                ${tagsHtml ? `<div class="vrcn-mini-content-badges">${tagsHtml}</div>` : ''}
            </div>
        </div>`;
    });
    h += `</div>`;
    grid.innerHTML = h;
    setMiniPaginator('fdWorldsPageBar', buildMiniPaginator(page, totalPages, 'fdWorldsGoPage'));
}

function fdWorldsGoPage(page) {
    if (page < 0) return;
    const totalPages = Math.ceil((window._fdAllWorlds || []).length / MINI_CONTENT_PG_SIZE) || 1;
    if (page >= totalPages) return;
    window._fdWorldsPage = page;
    renderFdWorldsPage(page);
}

function getLanguages(tags) {
    if (!tags) return [];
    return tags.filter(t => t.startsWith('language_')).map(t => LANG_MAP[t] || t.replace('language_','').toUpperCase());
}

function fdToggleBio(btn) {
    const bio = btn.closest('.fd-info-card')?.querySelector('.fd-bio');
    if (!bio) return;
    const expanded = bio.classList.toggle('expanded');
    btn.querySelector('.msi').textContent = expanded ? 'expand_less' : 'chevron_right';
}

const _fdBannerImgs = {};
function _getFdBannerImg(userId, src) {
    if (!userId || !src) return null;
    if (!_fdBannerImgs[userId]) {
        const img = new Image();
        img.src = src;
        img.onerror = () => { if (img.parentElement) img.parentElement.style.display = 'none'; };
        _fdBannerImgs[userId] = { img, src };
    } else if (_fdBannerImgs[userId].src !== src) {
        _fdBannerImgs[userId].img.src = src;
        _fdBannerImgs[userId].src = src;
    }
    return _fdBannerImgs[userId].img;
}

function renderDiscordChip(discordId) {
    const label = t('profiles.common.discord', 'Discord');
    const svg = `<svg viewBox="0 0 24 24" width="14" height="14" fill="currentColor" style="flex-shrink:0"><path d="${PLATFORM_ICONS['discord'].svg}"/></svg>`;
    return `<button class="fd-bio-link" onclick="sendToCS({action:'openUrl',url:'${jsq('https://discord.com/users/' + discordId)}'})" title="${esc(discordId)}">${svg}<span>${esc(label)}</span></button>`;
}

function renderFriendDetail(d) {
    const _fdPrevTab = document.querySelector('#modalFriendDetail .fd-tab.active')?.dataset.fdtab || '';
    const _fdPrevId  = (typeof currentFriendDetail !== 'undefined' && currentFriendDetail) ? currentFriendDetail.id : '';
    if (d.id && d.rawJson) _fdRawJsonCache[d.id] = d.rawJson;
    currentFriendDetail = d;
    if (typeof vrcnPlusOnProfileOpened === 'function' && d.id) vrcnPlusOnProfileOpened(d.id);
    if (typeof navUpdateLabel === 'function') navUpdateLabel(d.displayName || '');
    window._fdGroupsPage = 0;
    window._fdMutualsPage = 0;
    window._fdMutualsGroupsPage = 0;
    window._fdWorldsPage = 0;
    window._fdAvatarsPage = 0;
    window._fdOwnGroupsPage = 0;
    window._fdAllAvatars = [];
    const c = document.getElementById('friendDetailContent');

    const _fdModal = document.getElementById('modalFriendDetail');
    if (_fdModal) _fdModal.classList.add('fd-style-compact');

    const img = d.image || '';
    const _fdAvatarInner = img
        ? `<img class="fd-avatar" src="${img}" onerror="this.style.display='none'">`
        : `<div class="fd-avatar" style="display:flex;align-items:center;justify-content:center;font-size:calc(20px + var(--fs-off, 0px));font-weight:700;color:var(--tx0)">${esc((d.displayName || '?')[0])}</div>`;
    const _fdFrame = (typeof iconFrameHtml === 'function') ? iconFrameHtml(d.iconFrameUrl, true) : '';
    const imgTag = _fdFrame ? `<div class="icon-frame-wrap">${_fdAvatarInner}${_fdFrame}</div>` : _fdAvatarInner;

    let _worldPartHtml = '';
    let _ownerPartHtml = '';
    if (d.worldName) {
        const { worldId: fdWorldId, ownerId: _fdOwnerId } = parseFriendLocation(d.location);
        const onclick = fdWorldId ? `navOpenModal('worldSearch','${jsq(fdWorldId)}','${jsq(d.worldName || '')}')` : '';
        const _loc = d.location || '';
        const _instId     = _loc.includes(':') ? (_loc.split(':')[1] || '').split('~')[0] : '';
        const _regionRaw  = (_loc.match(/~region\(([^)]+)\)/) || [])[1] || '';
        const _region     = _regionRaw ? getWorldRegionLabel(_regionRaw) : '';
        const _instanceItemHtml = renderInstanceItem({
            thumb:        d.worldThumb || '',
            worldName:    d.worldName,
            instanceType: d.instanceType,
            instanceId:   _instId,
            region:       _region,
            userCount:    d.userCount || 0,
            capacity:     d.worldCapacity || 0,
            ageGate:      d.ageGate || false,
            location:     _loc,
            onclick,
        });
        _worldPartHtml = _instanceItemHtml;
        if (_fdOwnerId && _fdOwnerId.startsWith('usr_')) {
            const _ownerUser = vrcFriendsData.find(f => f.id === _fdOwnerId);
            if (_ownerUser) {
                const _ownerOnclick = `navOpenModal('friend','${jsq(_ownerUser.id)}','${jsq(_ownerUser.displayName || '')}')`;
                _ownerPartHtml = renderProfileItem(_ownerUser, _ownerOnclick, { noWorld: true });
            } else {
                _ownerPartHtml = `<div id="fdOwnerSlot" data-owner-id="${esc(_fdOwnerId)}"><div class="sk-block" style="height:44px;border-radius:8px;"></div></div>`;
                sendToCS({ action: 'vrcGetUserBasic', userId: _fdOwnerId, contextId: d.id });
            }
        }
    } else if (d.location === 'private') {
        _worldPartHtml = `<div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx2);text-align:center;padding:8px 0;">${t('profiles.meta.private_instance', 'Private Instance')}</div>`;
    } else if (d.location === 'traveling') {
        _worldPartHtml = `<div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx2);text-align:center;padding:8px 0;">${t('profiles.meta.traveling', 'Traveling...')}</div>`;
    }

    const bioHtml = d.bio ? `<div class="fd-bio">${esc(d.bio)}</div>` : '';

    const _bioLinkParts = (d.bioLinks || []).map(u => renderBioLink(u));
    if (d.discordId) _bioLinkParts.push(renderDiscordChip(d.discordId));
    const bioLinksHtml = _bioLinkParts.length
        ? `<div class="fd-bio-links">${_bioLinkParts.join('')}</div>`
        : '';

    const avatarId = d.currentAvatarId || '';
    const avatarFileId = d.avatarFileId || '';
    const avatarRowHtml = (avatarId.startsWith('avtr_') || avatarFileId)
        ? `<div id="fdAvatarSection" class="fd-info-card" style="display:none;"></div>`
        : '';

    const lastSeenStr   = d.inSameInstance
        ? t('profiles.last_seen.just_now', 'Just now')
        : (d.lastSeenTracked ? formatLastSeen(null, d.lastSeenTracked) : '');
    const lastActiveStr = d.lastActivity ? formatLastSeen(d.lastActivity, null) : '';
    const isSelf    = currentVrcUser && d.id === currentVrcUser.id;
    const fdMeetCnt      = d.meets || 0;
    const fdFirstMeet    = d.firstMeetDate || '';

    const _mr = (label, valueHtml) =>
        `<div style="display:flex;justify-content:space-between;gap:8px;align-items:baseline;font-size:calc(11px + var(--fs-off, 0px));">
            <span style="color:var(--tx2);">${label}</span>
            <span style="color:var(--tx1);text-align:right;">${valueHtml}</span>
        </div>`;

    const _aboutRows = [
        _mr(t('profiles.meta.platform',       'Platform'),       esc(d.platform || d.lastPlatform || '—')),
        _mr(t('profiles.meta.last_platform',  'Last Platform'),  esc(d.lastPlatform || '—')),
        _mr(t('profiles.meta.joined',         'Joined'),         d.dateJoined ? fmtShortDate(new Date(d.dateJoined + 'T00:00:00')) : '—'),
        _mr(t('profiles.meta.last_seen',      'Last Seen'),      esc(lastSeenStr   || '—')),
        _mr(t('profiles.meta.last_active',    'Last Active'),    esc(lastActiveStr || '—')),
        _mr(t('profiles.meta.age_verified',   'Age Verified'),   d.ageVerified        ? t('common.yes','Yes') : t('common.no','No')),
        _mr(t('profiles.meta.avatar_cloning', 'Avatar Cloning'), d.allowAvatarCopying ? t('common.on','On')   : t('common.off','Off')),
    ];
    if (!isSelf) {
        _aboutRows.push(_mr(t('profiles.meta.meets', 'Meets'),
            fdMeetCnt > 0 ? String(fdMeetCnt) : '—'));
        _aboutRows.push(_mr(t('profiles.meta.time_together', 'Time Together'),
            (d.totalTimeSeconds > 0 || d.inSameInstance)
                ? `<span id="fdTimeTogether">${formatDuration(d.totalTimeSeconds)}</span>`
                : `<span style="color:var(--tx3);">${t('profiles.meta.not_tracked', 'Not tracked yet')}</span>`));
        _aboutRows.push(_mr(t('profiles.meta.status_mostly', 'Status Mostly'),
            `<span id="fdInfoStatusMostly" style="color:var(--tx2);">—</span>`));
    }

    const _aboutRowsHtml = `<div class="fd-group-rep-label">${t('profiles.meta.infos_title', 'Infos')}</div>
        <div style="display:grid;gap:6px;">${_aboutRows.join('')}</div>`;

    const vrcNoteHtml = `<div class="myp-section-header">
            <span class="myp-section-title">${t('profiles.notes.vrc_note', 'VRC Note')}</span>
            <button class="myp-edit-btn" onclick="fdEditNote()"><span class="msi" style="font-size:14px;">edit</span></button>
        </div>
        <div id="fdVrcNoteView">
            ${d.note ? `<div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx1);line-height:1.5;">${esc(d.note)}</div>`
                     : `<div class="myp-empty">${t('profiles.notes.no_note', 'No notes added yet')}</div>`}
        </div>
        <div id="fdVrcNoteEdit" style="display:none;">
            <textarea id="fdVrcNoteInput" class="myp-textarea" rows="3" placeholder="${esc(t('profiles.notes.placeholder', 'Write a note about this user...'))}"></textarea>
            <div class="myp-edit-actions">
                <button class="vrcn-button" onclick="fdCancelNote()">${t('common.cancel', 'Cancel')}</button>
                <button id="fdVrcNoteSaveBtn" class="vrcn-button vrcn-btn-primary" onclick="fdSaveNote()">${t('common.save', 'Save')}</button>
            </div>
        </div>`;

    let actionsHtml = '<div class="fd-actions">';
    const loc = (d.location || '').replace(/'/g, "\\'");
    const uid = (d.id || '').replace(/'/g, "\\'");
    if (d.isFriend) {
        if (d.canJoin) actionsHtml += `<button class="vrcn-button-round vrcn-btn-join" onclick="friendAction('join','${loc}','${uid}')" title="${esc(t('common.join', 'Join'))}"><span class="msi" style="font-size:16px;">login</span></button>`;
        if (d.canRequestInvite) actionsHtml += `<button class="vrcn-button-round" onclick="friendAction('requestInvite','${loc}','${uid}')" title="${esc(t('profiles.actions.request_invite', 'Request Invite'))}"><span class="msi" style="font-size:16px;">outbox</span></button>`;
        const myInInstance = currentInstanceData && currentInstanceData.location && !currentInstanceData.empty && !currentInstanceData.error;
        if (myInInstance) actionsHtml += `<button class="vrcn-button-round" onclick="openFriendInviteModal('${uid}','${esc(d.displayName).replace(/'/g, "\\'")}')" title="${esc(t('instance.actions.invite', 'Invite'))}"><span class="msi" style="font-size:16px;">mail</span></button>`;
        actionsHtml += `<button class="vrcn-button-round${d.isFavorited ? ' active' : ''}" id="fdFavBtn" onclick="toggleFriendFavPicker('${uid}')" title="${d.isFavorited ? t('profiles.actions.unfavorite', 'Unfavorite') : t('profiles.actions.favorite', 'Favorite')}"><span class="msi" style="font-size:16px;">${d.isFavorited ? 'favorite' : 'favorite_border'}</span></button>`;
    } else {
        actionsHtml += `<button class="vrcn-button-round vrcn-btn-primary" id="fdAddFriend" onclick="sendToCS({action:'vrcSendFriendRequest',userId:'${uid}'});this.disabled=true;this.textContent='${esc(t('profiles.actions.request_sent', 'Request Sent'))}';">${t('profiles.actions.add_friend', 'Add Friend')}</button>`;
    }
    if (d.isFriend) actionsHtml += `<button class="vrcn-button-round vrcn-btn-danger" id="fdUnfriend" onclick="confirmUnfriend('${uid}','${esc(d.displayName).replace(/'/g, "\\'")}') " title="${t('profiles.actions.unfriend', 'Unfriend')}"><span class="msi" style="font-size:16px;">person_remove</span></button>`;
    actionsHtml += '</div>';
    const favPickerHtml = d.isFriend
        ? `<div id="fdFavPicker" style="display:none;margin-bottom:14px;">
            <div class="wd-section-label" style="margin-bottom:6px;">ADD TO FAVORITE GROUP</div>
            <div class="ci-group-list" id="fdFavGroupList"><div style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);padding:8px 0;">Loading groups...</div></div>
           </div>` : '';

    let badgesHtml = '<div class="fd-badges-row">';
    const platBadge = getPlatformBadgeHtml(d.platform || d.lastPlatform || '');
    if (platBadge) badgesHtml += platBadge;
    if (d.isFriend) badgesHtml += `<span class="vrcn-badge bdg-friend"><span class="msi" style="font-size:11px;">check_circle</span>${t('profiles.badges.friend', 'Friend')}</span>`;
    badgesHtml += getCreatorBadgeHtml(d);
    if (d.ageVerified) badgesHtml += `<span class="vrcn-badge ok"><span class="msi" style="font-size:11px;">verified</span>${t('profiles.meta.age_verified', 'Age Verified')}</span>`;
    if (d.ageVerificationStatus === '18+') badgesHtml += `<span class="vrcn-badge ok"><span class="msi" style="font-size:11px;">verified</span>18+</span>`;
    const rank = getTrustRank(d.tags || []);
    if (rank) badgesHtml += `<span class="vrcn-badge ${rank.cls}">${esc(rank.label)}</span>`;
    if (d.id) badgesHtml += idBadge(d.id);
    badgesHtml += '</div>';

    const vrcPlusBadge = (d.tags || []).includes('system_supporter') ? `<span class="vrcn-supporter-badge">VRC+</span>` : '';
    const pronounsHtml = d.pronouns ? `<div class="fd-pronouns">${esc(d.pronouns)}</div>` : '';
    const langs = getLanguages(d.tags || []);
    const langsHtml = langs.length ? `<div class="fd-lang-tags">${langs.map(l => `<span class="vrcn-badge">${esc(l)}</span>`).join('')}</div>` : '';

    const allGroups = d.userGroups || [];
    let repG = d.representedGroup;
    if (!repG && allGroups.length > 0) {
        const repFromList = allGroups.find(g => g.isRepresenting);
        if (repFromList) repG = repFromList;
    }

    let repGroupInfoHtml = '';
    let repGroupBadgeHtml = '';
    if (repG && repG.id) {
        const repIcon = repG.iconUrl ? `<img class="fd-group-icon" src="${imgThumb(repG.iconUrl, 96)}" onerror="this.style.display='none'">` : `<div class="fd-group-icon fd-group-icon-empty"><span class="msi" style="font-size:18px;">group</span></div>`;
        repGroupInfoHtml = `<div class="fd-group-rep-label">${t('profiles.badges.representing', 'Representing')}</div><div class="fd-group-card fd-group-rep" onclick="navOpenModal('group','${jsq(repG.id)}','${jsq(repG.name || '')}')">
            ${repIcon}<div class="fd-group-card-info"><div class="fd-group-card-name">${esc(repG.name)}</div><div class="fd-group-card-meta">${esc(repG.shortCode || '')}${repG.discriminator ? '.' + esc(repG.discriminator) : ''} &middot; ${esc(getGroupMemberText(repG.memberCount))}</div></div>
        </div>`;
        // Inline badge shown in the classic-mode status row (next to the status text).
        const _repBadgeIcon = repG.iconUrl
            ? `<img class="fd-rep-group-badge-icon" src="${esc(imgThumb(repG.iconUrl, 64))}" onerror="this.style.display='none'">`
            : `<span class="msi" style="font-size:13px;flex-shrink:0;">group</span>`;
        repGroupBadgeHtml = `<div class="fd-rep-group-badge" onclick="navOpenModal('group','${jsq(repG.id)}','${jsq(repG.name || '')}')">${_repBadgeIcon}<span class="fd-rep-group-badge-name">${esc(repG.name || '')}</span></div>`;
    }

    const vrcBadges = d.badges || [];
    const _isVrcnPlusFd = (typeof vrcnPlusIsKnownPlus === 'function') && vrcnPlusIsKnownPlus(d.id);
    let vrcBadgesRowHtml = '';
    if (vrcBadges.length > 0 || _isVrcnPlusFd) {
        const vrcnPlusBadgeHtml = _isVrcnPlusFd && typeof window.vrcnPlusBadgeHtml === 'function'
            ? window.vrcnPlusBadgeHtml() : '';
        vrcBadgesRowHtml = `<div class="fd-vrc-badges-row">${vrcnPlusBadgeHtml}${vrcBadges.map(b =>
            `<div class="fd-vrc-badge-wrap"` +
                ` data-badge-img="${esc(b.imageUrl)}"` +
                ` data-badge-name="${encodeURIComponent(b.name)}"` +
                ` data-badge-desc="${encodeURIComponent(b.description || '')}">` +
                `<img class="fd-vrc-badge-icon" src="${esc(imgThumb(b.imageUrl, 64))}" alt="${esc(b.name)}" onerror="this.closest('.fd-vrc-badge-wrap').style.display='none'">` +
            `</div>`
        ).join('')}</div>`;
    }

    window._fdRepGroup = (repG && repG.id) ? repG : null;
    window._fdAllGroups = allGroups;
    // Include repG if it's not already in allGroups (VRC API sometimes returns it separately)
    const _repInGroups = repG && allGroups.some(g => g.id === repG.id);
    window._fdAllGroupsAll = (!repG || _repInGroups) ? allGroups : [repG, ...allGroups];
    window._fdAllOwnGroups = allGroups.filter(g => g.ownerId === d.id);

    let groupsContent = '';
    if (window._fdAllGroupsAll.length > 0) {
        groupsContent += `<div class="search-bar-row" style="margin-bottom:6px;">
            <span class="msi search-ico">search</span>
            <input id="fdGroupsSearch" type="text" class="vrcn-input" placeholder="${esc(t('profiles.groups.search_placeholder', 'Search groups by name...'))}" style="background:var(--bg-input);" oninput="_dbFdGroups()">
            <select id="fdGroupsSort" class="vrcn-dropdown" style="flex-shrink:0;" onchange="setFdGroupsSort(this.value)">
                <option value="alpha">${esc(t('profiles.sort.alphabetical', 'Alphabetical'))}</option>
                <option value="members">${esc(t('profiles.sort.members', 'Members'))}</option>
            </select>
        </div>`;
        const ownGroups = window._fdAllOwnGroups || [];
        if (ownGroups.length > 0) {
            groupsContent += `<div class="fd-group-rep-label">${t('profiles.groups.own_groups', 'Own Groups')}</div>`;
            groupsContent += `<div id="fdOwnGroupsGrid" style="display:grid;grid-template-columns:1fr 1fr 1fr;column-gap:6px;"></div>`;
            groupsContent += `<div id="fdOwnGroupsPaginatorBar" class="mini-paginator"></div>`;
        }
        groupsContent += `<div class="fd-group-rep-label" style="margin-top:${ownGroups.length > 0 ? '14' : '0'}px;">${t('profiles.badges.groups', 'Groups')}</div>`;
        groupsContent += `<div id="fdGroupsGrid" style="display:grid;grid-template-columns:1fr 1fr 1fr;column-gap:6px;"></div>`;
        groupsContent += `<div id="fdGroupsPaginatorBar" class="mini-paginator"></div>`;
    }

    if (!groupsContent) groupsContent = `<div style="padding:20px;text-align:center;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('profiles.badges.no_groups', 'No groups')}</div>`;

    const allMutuals = d.mutuals || [];
    const allMutualGroups = d.mutualGroups || [];
    const mutualTotal = allMutuals.length + allMutualGroups.length;
    window._fdAllMutuals = allMutuals;

    if (!isSelf && !d.isFriend && !d.mutualsOptedOut && allMutuals.length > 0 && typeof networkAddNonFriend === 'function') {
        networkAddNonFriend({
            userId:      d.id,
            displayName: d.displayName,
            image:       d.image || '',
            mutualIds:   allMutuals.map(m => m && m.id).filter(Boolean),
        });
    }
    window._fdAllMutualGroups = allMutualGroups;

    let mutualsFriendsHtml = '';
    if (d.mutualsOptedOut) {
        mutualsFriendsHtml = `<div style="padding:24px 16px;text-align:center;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">
            <span class="msi" style="font-size:28px;display:block;margin-bottom:8px;opacity:.5;">visibility_off</span>
            ${t('profiles.mutuals.opted_out', 'This user has disabled Shared Connections.')}
        </div>`;
    } else if (allMutuals.length === 0) {
        mutualsFriendsHtml = `<div style="padding:24px 16px;text-align:center;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">
            <span class="msi" style="font-size:28px;display:block;margin-bottom:8px;opacity:.5;">group_off</span>
            ${t('profiles.mutuals.empty', 'No mutual friends found.')}<br>
            <span style="font-size:calc(10px + var(--fs-off, 0px));margin-top:6px;display:block;line-height:1.5;">
                ${t('profiles.mutuals.empty_hint', 'Requires VRChat\'s "Shared Connections" feature to be active on both accounts.')}
            </span>
        </div>`;
    } else {
        mutualsFriendsHtml = '<div id="fdMutualsGrid" style="display:grid;grid-template-columns:1fr 1fr 1fr;column-gap:6px;"></div>';
        mutualsFriendsHtml += '<div id="fdMutualsPageBar" class="mini-paginator"></div>';
    }

    let mutualsGroupsHtml = '';
    if (allMutualGroups.length === 0) {
        mutualsGroupsHtml = `<div style="padding:24px 16px;text-align:center;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">
            <span class="msi" style="font-size:28px;display:block;margin-bottom:8px;opacity:.5;">group_off</span>
            ${t('profiles.mutuals.no_groups', 'No mutual groups found.')}
        </div>`;
    } else {
        mutualsGroupsHtml = '<div id="fdMutualsGroupsGrid" style="display:grid;grid-template-columns:1fr 1fr 1fr;gap:6px;"></div>';
        mutualsGroupsHtml += '<div id="fdMutualsGroupsPageBar" class="mini-paginator"></div>';
    }

    _fdMutualsActivePill   = 'friends';
    const hasMutualSearch  = !d.mutualsOptedOut && allMutuals.length > 0;
    const hasGroupSearch   = allMutualGroups.length > 0;
    const mutualsSearchBar = `<div class="search-bar-row fd-mutuals-bar" style="margin-bottom:6px;">
            <span class="msi search-ico" id="fdMutualsSearchIco"${hasMutualSearch ? '' : ' style="display:none;"'}>search</span>
            ${hasMutualSearch ? `<input id="fdMutualsSearch" type="text" class="vrcn-input" placeholder="${esc(t('profiles.mutuals.search_placeholder', 'Search users by name...'))}" style="background:var(--bg-input);" oninput="_dbFdMutuals()">` : ''}
            ${hasGroupSearch ? `<input id="fdMutualsGroupsSearch" type="text" class="vrcn-input" placeholder="${esc(t('profiles.mutuals.groups_search_placeholder', 'Search groups by name...'))}" style="background:var(--bg-input);display:none;" oninput="_dbFdMutualsGroups()">` : ''}
            ${(!hasMutualSearch && !hasGroupSearch) ? '<span style="flex:1;"></span>' : ''}
            <div class="fd-content-pills fd-mutuals-pills">
                <button class="fd-tab fd-mutual-pill active" onclick="switchFdMutualsPill('friends',this)">${t('profiles.mutuals.pill_friends_label', 'Friends')} <span class="vrcn-badge fd-tab-badge">${allMutuals.length}</span></button>
                <button class="fd-tab fd-mutual-pill" onclick="switchFdMutualsPill('groups',this)">${t('profiles.mutuals.pill_groups_label', 'Groups')} <span class="vrcn-badge fd-tab-badge">${allMutualGroups.length}</span></button>
            </div>
            ${(hasMutualSearch || hasGroupSearch) ? `<select id="fdMutualsSort" class="vrcn-dropdown" style="flex-shrink:0;${hasMutualSearch ? '' : 'display:none;'}" onchange="setFdMutualsSortValue(this.value)">
                ${_fdMutualsSortOptions('friends')}
            </select>` : ''}
        </div>`;

    const mutualsContent = `
        ${mutualsSearchBar}
        <div id="fdMutualsFriends">${mutualsFriendsHtml}</div>
        <div id="fdMutualsGroups" style="display:none;">${mutualsGroupsHtml}</div>`;

    const miniTlHtml = `<div class="fd-content-pills" style="margin-bottom:10px;">
            <button class="fd-tab fd-mini-tl-pill active" onclick="switchFdMiniTlPill('timeline',this)">${t('nav.timeline', 'Timeline')}</button>
            <button class="fd-tab fd-mini-tl-pill" onclick="switchFdMiniTlPill('activity',this)">${t('profiles.user_activity.title', 'Last Activity')}</button>
        </div>
        <div id="fdMiniTl" style="max-height:160px;overflow-y:auto;"></div>
        <div id="fdUserActivity" style="max-height:160px;overflow-y:auto;display:none;"></div>`;

    const insightsHtml = `<div class="fd-content-pills" style="margin-bottom:10px;">
            <button class="fd-tab fd-insights-pill active" onclick="switchFdInsightsPill('worlds',this)">${t('profiles.insights.most_visited_worlds', 'Most Visited Worlds')}</button>
            <button class="fd-tab fd-insights-pill" onclick="switchFdInsightsPill('persons',this)">${t('profiles.insights.interacted_most', 'Interacted the most with')}</button>
        </div>
        <div id="fdInsightsWorlds" style="max-height:280px;overflow-y:auto;"><div style="padding:4px 0;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('profiles.insights.loading', 'Loading...')}</div></div>
        <div id="fdInsightsPersons" style="max-height:280px;overflow-y:auto;display:none;"></div>`;

    const heatmapHtml = `<div class="fd-hm-header">
            <div class="fd-hm-head-left">
                <button class="vrcn-button" onclick="fdReloadHeatmap()" title="${esc(t('common.refresh', 'Refresh'))}"><span class="msi" id="fdHmRefreshIcon" style="font-size:14px;">refresh</span></button>
                <span class="fd-hm-count" id="fdHmCount">&nbsp;</span>
            </div>
            <div class="fd-hm-head-right">
                <select id="fdHmView" class="vrcn-dropdown" onchange="fdChangeHeatmapView(this.value)">
                    <option value="online" selected>${esc(t('profiles.heatmap.view_online', 'Online'))}</option>
                    <option value="all">${esc(t('profiles.heatmap.view_all_statuses', 'All Statuses'))}</option>
                    <option value="join me">${esc(t('status.join_me', 'Join Me'))}</option>
                    <option value="active">${esc(t('status.online', 'Online'))}</option>
                    <option value="ask me">${esc(t('status.ask_me', 'Ask Me'))}</option>
                    <option value="busy">${esc(t('status.do_not_disturb', 'Do Not Disturb'))}</option>
                </select>
                <select id="fdHmPeriod" class="vrcn-dropdown" onchange="fdChangeHeatmapPeriod(this.value)">
                    <option value="7">${esc(t('profiles.heatmap.last_7', 'Last 7 Days'))}</option>
                    <option value="30" selected>${esc(t('profiles.heatmap.last_30', 'Last 30 Days'))}</option>
                    <option value="90">${esc(t('profiles.heatmap.last_90', 'Last 90 Days'))}</option>
                    <option value="0">${esc(t('profiles.heatmap.all_time', 'All Time'))}</option>
                </select>
            </div>
        </div>
        <div class="fd-hm-stats" id="fdHmStats"></div>
        <div class="fd-hm-grid-wrap" id="fdHmGridWrap"><div style="padding:16px 0;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);text-align:center;">${t('profiles.insights.loading', 'Loading...')}</div></div>
        <div class="fd-hm-status-wrap" id="fdHmStatusWrap" style="display:none;"></div>`;

    const bannerSrc = d.bannerUrl || d.profilePicOverride || d.currentAvatarImageUrl || d.image || '';
    const fdHeaderActions = renderModalActions(_fdBuildTaskbarActions(d));

    const fdLocation = d.location || '';
    const fdIsOffline = (d.status || 'offline') === 'offline';
    const fdIsInGame = !fdIsOffline && !!fdLocation && fdLocation !== 'offline';
    const fdIsWeb = !fdIsOffline && !fdIsInGame && d.state === 'active';
    const fdDotClass = fdIsWeb ? 'vrc-status-ring' : 'vrc-status-dot';
    const fdStatusDotCls = fdIsOffline ? 's-offline' : statusDotClass(d.status);

    const trustCreatorBadge = getCreatorBadgeHtml(d);
    const trustBadgesRow = (rank || trustCreatorBadge)
        ? `<div class="fd-badges-row" style="margin-bottom:0;">${rank ? `<span class="vrcn-badge ${rank.cls}">${esc(rank.label)}</span>` : ''}${trustCreatorBadge}</div>`
        : '';
    const trustSideHtml = `<div class="fd-group-rep-label">${t('profiles.trust.title', 'Trust &amp; Safety')}</div>
        ${trustBadgesRow}
        <div id="fdTrustBarSlot">${getTrustBarHtml(d, 0, false)}</div>`;

    const _fdInstFriends = (_worldPartHtml && d.location && d.location !== 'private' && d.location !== 'traveling')
        ? (typeof getInstanceMembers === 'function' ? getInstanceMembers(d.location) : []).filter(m => m.id !== d.id)
        : [];
    const _instFriendsHtml = _fdInstFriends.length > 0
        ? `<div class="fd-group-rep-label" style="margin-top:10px;">${tf('instance.sections.friends_in_instance', { count: _fdInstFriends.length }, 'FRIENDS IN INSTANCE ({count})')}</div>
           <div class="wd-friends-list" style="display:grid;grid-template-columns:1fr 1fr;max-height:none;">${_fdInstFriends.map(f => renderProfileItem(f, `navOpenModal('friend','${jsq(f.id || '')}','${jsq(f.displayName || '')}')`)).join('')}</div>`
        : '';

    const _currentWorldCard = _worldPartHtml ? `<div class="fd-info-card fd-world-card">
        <div class="fd-group-rep-label">${t('profiles.meta.current_world', 'Current World')}</div>
        ${_worldPartHtml}
        ${_instFriendsHtml}
    </div>` : '';
    const _ownerCard = _ownerPartHtml ? `<div class="fd-info-card fd-owner-card">
        <div class="fd-group-rep-label">${t('instance.owner', 'Instance Owner')}</div>
        ${_ownerPartHtml}
    </div>` : '';
    // Trust badges (PC/Friend/18+/Trusted/User-ID) sit at the top of the
    // Biography card; the id-badge truncates with ellipsis to keep them on one row.
    const _bioBadgesHtml = badgesHtml.replace('<div class="fd-badges-row">', '<div class="fd-badges-row fd-bio-badges-row" style="margin-bottom:10px;">');
    const _bioCardCondition = (d.id || d.bio || bioLinksHtml);
    const _bioTransBtn = (d.bio && window._kxdProfileTranslationEnabled !== false)
        ? `<button class="fd-bio-translate myp-edit-btn" onclick="fdTranslateBio(this)" title="${esc(t('profiles.bio.translate', 'Translate'))}"><span class="msi" style="font-size:14px;">translate</span></button>`
        : '';
    const _bioCard = _bioCardCondition ? `<div class="fd-info-card">
        <div class="fd-group-rep-label">${t('profiles.bio.title', 'Biography')}${d.bio ? `<button class="fd-bio-expand" onclick="fdToggleBio(this)" style="display:none"><span class="msi">chevron_right</span></button>` : ''}${_bioTransBtn}</div>
        ${_bioBadgesHtml}${bioHtml}${bioLinksHtml}
    </div>` : '';
    const _noteCard = `<div class="fd-info-card">${vrcNoteHtml}</div>`;
    const _memoTitle = t('profiles.memo.title_compact', 'Memo');
    const _memoView  = esc((d.memo && d.memo.trim()) ? d.memo : (d.displayName || ''));
    const _memoCard = `<div class="fd-info-card">
        <div class="myp-section-header">
            <span class="myp-section-title">${esc(_memoTitle)}</span>
            <button class="myp-edit-btn" onclick="fdEditMemo()"><span class="msi" style="font-size:14px;">edit</span></button>
        </div>
        <div id="fdMemoView" style="font-size:calc(13px + var(--fs-off, 0px));color:var(--tx1);line-height:1.5;word-break:break-word;">${_memoView}</div>
        <div id="fdMemoEdit" style="display:none;">
            <input id="fdMemoInput" type="text" class="myp-textarea" maxlength="128" placeholder="${esc(t('profiles.memo.placeholder', 'Custom name or note for this user'))}">
            <div class="myp-edit-actions">
                <button class="vrcn-button" onclick="fdCancelMemo()">${t('common.cancel', 'Cancel')}</button>
                <button id="fdMemoSaveBtn" class="vrcn-button vrcn-btn-primary" onclick="fdSaveMemo()">${t('common.save', 'Save')}</button>
            </div>
        </div>
    </div>`;
    const _tlCard = `<div class="fd-info-card">${miniTlHtml}</div>`;
    const _insightsCard = `<div class="fd-info-card">${insightsHtml}</div>`;
    const _heatmapCard = `<div class="fd-info-card">${heatmapHtml}</div>`;
    const _infosCard = `<div class="fd-info-card">${_aboutRowsHtml}</div>`;
    const _trustCard = trustSideHtml ? `<div class="fd-info-card">${trustSideHtml}</div>` : '';
    const _repCard = repGroupInfoHtml ? `<div class="fd-info-card">${repGroupInfoHtml}</div>` : '';
    const _modCard = `<div class="fd-info-card" id="fdModerationCard">${_buildModCardInner(d.id)}</div>`;
    const _fdBadgesCard = vrcBadgesRowHtml ? `<div class="fd-info-card"><div class="fd-group-rep-label">${t('profiles.badges.badges', 'Badges')}</div>${vrcBadgesRowHtml}</div>` : '';
    const _fdLangCard = langsHtml ? `<div class="fd-info-card"><div class="fd-group-rep-label">${t('profiles.my_profile.sections.languages', 'Languages')}</div>${langsHtml}</div>` : '';
    const infoContent = `<div class="fd-info-wrap">
            <div class="fd-info-cols">
                <div class="fd-info-left">
                    ${_currentWorldCard}${avatarRowHtml}${_bioCard}${_noteCard}
                </div>
                <div class="fd-info-right">
                    ${_ownerCard}${_repCard}${_trustCard}${_modCard}
                </div>
            </div>
            ${_tlCard}
            ${_insightsCard}
            ${_heatmapCard}
        </div>`;

    const hasGroups = allGroups.length > 0 || repG;
    const hasMutuals = d.mutuals !== undefined;
    const allUserWorlds = d.userWorlds || [];
    const hasContent = true;
    const hasTabs = hasGroups || hasMutuals || hasContent;
    const groupsTabCount = (window._fdAllGroupsAll || allGroups).length;

    const _tabBadge = (n) => `<span class="vrcn-badge fd-tab-badge">${n}</span>`;
    let tabsHtml = '';
    if (hasTabs) {
        tabsHtml = `<div class="fd-tabs"><button class="fd-tab active" data-fdtab="info" onclick="switchFdTab('info',this)">${t('profiles.tabs.info', 'Info')}</button>`;
        if (hasGroups) tabsHtml += `<button class="fd-tab" data-fdtab="groups" onclick="switchFdTab('groups',this)">${t('profiles.tabs.groups_label', 'Groups')} ${_tabBadge(groupsTabCount)}</button>`;
        if (hasMutuals) tabsHtml += `<button class="fd-tab" data-fdtab="mutuals" onclick="switchFdTab('mutuals',this)">${t('profiles.tabs.mutuals_label', 'Mutuals')} ${_tabBadge(mutualTotal)}</button>`;
        tabsHtml += `<button class="fd-tab" id="fdTabContentBtn" data-fdtab="content" onclick="switchFdTab('content',this)">${t('profiles.tabs.content_label', 'Content')} ${_tabBadge(allUserWorlds.length)}</button>`;
        tabsHtml += `<button class="fd-tab" data-fdtab="favs" onclick="switchFdTab('favs',this)">${t('profiles.tabs.favs', 'Favs.')}</button>`;
        tabsHtml += `<button class="fd-tab" data-fdtab="json" onclick="switchFdTab('json',this)">Json</button>`;
        tabsHtml += `</div>`;
    }

    window._fdAllWorlds = allUserWorlds;
    window._fdWorldsPage = 0;

    const userId = d.id || '';
    const contentHtml = `
        <div class="fd-content-pills">
            <button class="fd-tab fd-content-pill active" id="fdWorldsPill" onclick="switchFdContentPill('worlds',this)">${t('profiles.content.worlds_pill_label', 'Worlds')} <span class="vrcn-badge fd-tab-badge">${allUserWorlds.length}</span></button>
            <button class="fd-tab fd-content-pill" id="fdAvatarsPill" onclick="switchFdContentPill('avatars',this)">${t('profiles.content.avatars_pill_label', 'Avatars')} <span class="vrcn-badge fd-tab-badge">0</span></button>
        </div>
        <div id="fdContentWorlds">
            <div id="fdWorldsGrid"></div>
            <div id="fdWorldsPageBar" class="mini-paginator"></div>
        </div>
        <div id="fdContentAvatars" style="display:none;" data-user-id="${esc(userId)}">
            <div id="fdAvatarsGrid"><div class="empty-msg">${t('profiles.content.loading_avatars', 'Loading avatars...')}</div></div>
            <div id="fdAvatarsPageBar" class="mini-paginator"></div>
        </div>`;

    // Status row & dot — the dot sits on the avatar (#fd-live-dot) and the row
    // shows only the user's status description (label/web suffix come from the
    // avatar dot's color).
    const _fdDotHtml = `<span class="${fdDotClass} ${fdStatusDotCls} fd-left-status-dot" id="fd-live-dot"></span>`;
    const _fdStatusRowCompact = d.statusDescription
        ? `<div class="fd-status-row"><div class="fd-status" id="fd-live-status">${esc(d.statusDescription)}</div></div>`
        : `<div class="fd-status-row" style="display:none;"><div class="fd-status" id="fd-live-status"></div></div>`;

    {
        const bannerSlotHtml = `<div class="fd-left-banner" id="fd-banner-slot">${bannerSrc ? '<div class="fd-banner-fade"></div>' : ''}${(typeof profileEffectHtml === 'function') ? profileEffectHtml(d.profileEffectUrl) : ''}</div>`;
        const _fdLeftHtml = `<div class="fd-left">
            ${bannerSlotHtml}
            <div class="fd-left-body">
                <div class="fd-left-id"><div class="fd-left-avatar-wrap">${imgTag}${_fdDotHtml}</div><div class="fd-left-name-wrap"><div class="fd-name" style="display:flex;align-items:center;gap:6px;flex-wrap:wrap;">${esc(d.displayName)}${vrcPlusBadge}</div>${pronounsHtml}${_fdStatusRowCompact}</div></div>
                ${actionsHtml}${favPickerHtml}
                ${_memoCard}
                ${_fdBadgesCard}
                ${_fdLangCard}
                ${_infosCard}
            </div>
        </div>`;
        const _fdRightHtml = `<div class="fd-right"><div class="fd-right-scroll">${tabsHtml}<div id="fdTabInfo">${infoContent}</div><div id="fdTabGroups" style="display:none;">${groupsContent}</div><div id="fdTabMutuals" style="display:none;">${mutualsContent}</div><div id="fdTabContent" style="display:none;">${contentHtml}</div><div id="fdTabFavs" style="display:none;" data-user-id="${esc(userId)}"></div><div id="fdTabJson" style="display:none;"><div class="json-viewer">${jsonHighlight((d.id && _fdRawJsonCache[d.id]) || {})}</div></div></div></div>`;
        c.innerHTML = `${fdHeaderActions}<div class="fd-layout">${_fdLeftHtml}${_fdRightHtml}</div>`;
    }

    if (bannerSrc) {
        const bannerSlot = document.getElementById('fd-banner-slot');
        const bannerImg = _getFdBannerImg(d.id, bannerSrc);
        if (bannerSlot && bannerImg) bannerSlot.insertBefore(bannerImg, bannerSlot.firstChild);
    }

    // Enhance sort dropdowns into custom vn-select
    ['fdGroupsSort', 'fdMutualsSort', 'fdMutualsGroupsSort', 'fdHmView', 'fdHmPeriod'].forEach(id => {
        const sel = document.getElementById(id);
        if (sel && typeof initVnSelect === 'function') initVnSelect(sel);
    });

    // Populate paginated grids
    filterFdGroups();
    filterFdOwnGroups();
    filterFdMutuals();
    filterFdMutualsGroups();
    renderFdWorldsPage(0);

    if (_fdPrevTab && _fdPrevTab !== 'info' && _fdPrevId === d.id) {
        const _restoreBtn = document.querySelector(`#modalFriendDetail .fd-tab[data-fdtab="${_fdPrevTab}"]`);
        if (_restoreBtn) switchFdTab(_fdPrevTab, _restoreBtn);
    }

    const _avatarKey = (d.id || '') + '|' + (avatarFileId || avatarId);
    if (_fdLastAvatarUserId !== (d.id || '')) {
        _fdLastAvatarUserId = d.id || '';
        _fdLastAvatarPayload = null;
    }
    if (_fdLastAvatarPayload) _applyAvatarSection(_fdLastAvatarPayload);
    if ((avatarFileId || avatarId.startsWith('avtr_') || d.id) && _avatarKey !== _fdLoadedAvatarKey) {
        _fdLoadedAvatarKey = _avatarKey;
        if (avatarFileId) sendToCS({ action: 'vrcLookupAvatarByFileId', fileId: avatarFileId, openModal: false, userId: d.id });
        else if (avatarId && avatarId.startsWith('avtr_')) sendToCS({ action: 'vrcGetAvatarInfo', avatarId, userId: d.id });
        else sendToCS({ action: 'vrcLookupAvatarByFileId', fileId: '', openModal: false, userId: d.id });
    }

    requestAnimationFrame(() => {
        const bio = c.querySelector('.fd-bio');
        const btn = c.querySelector('.fd-bio-expand');
        if (bio && btn && bio.scrollHeight > bio.clientHeight + 2) btn.style.display = '';
    });

    c.querySelectorAll('.fd-group-card-meta').forEach(el => {
        let text = (el.textContent || '').replace(/\s*(?:Â·|·)\s*/g, ' · ').trim();
        text = text.replace(/(\d+)\s+members/gi, (_, count) => tf('worlds.groups.members', { count }, '{count} members'));
        text = text.replace(/\bGroup\b/g, t('groups.common.group', 'Group'));
        el.textContent = text;
    });
    c.querySelectorAll('.s-card-sub').forEach(el => {
        el.innerHTML = el.innerHTML.replace(/Â·/g, '&middot;').replace(/·/g, '&middot;');
    });

    if (userId) sendToCS({ action: 'vrcGetUserAvatars', userId: userId });
    if (userId) { _fdTimelineEvents = []; sendToCS({ action: 'getTimelineForUser', userId }); }
    if (userId) sendToCS({ action: 'getFriendActivityForUser', userId });
    if (userId) sendToCS({ action: 'getProfileInsights', userId });
    if (userId) { _fdHeatmapDays = 30; _fdHeatmapView = 'online'; _fdStatusData = null; sendToCS({ action: 'getUserOnlineHeatmap', userId, days: 30 }); }
    if (userId && !isSelf) sendToCS({ action: 'getUserStatusTime', userId, days: 30 });

    // VRC+ profile background skins the left identity sidebar.
    const _fdSelfBox = document.querySelector('#modalFriendDetail .modal-box');
    if (_fdSelfBox) _fdSelfBox.classList.toggle('deco-self', typeof _decoIsSelf === 'function' && _decoIsSelf(d));
    if (typeof applyProfileBg === 'function') {
        const _bgBox  = document.querySelector('#modalFriendDetail .modal-box');
        const _bgLeft = c.querySelector('.fd-left');
        applyProfileBg(_bgBox,  null);
        applyProfileBg(_bgLeft, d);
    }
    if (typeof applyProfileTheme === 'function') {
        applyProfileTheme(document.querySelector('#modalFriendDetail .modal-box'), d);
    }

    if (_fdLiveTimer) { clearInterval(_fdLiveTimer); _fdLiveTimer = null; }
    if (d.inSameInstance && !(currentVrcUser && d.id === currentVrcUser.id)) {
        let liveSecs = d.totalTimeSeconds;
        _fdLiveTimer = setInterval(() => {
            liveSecs++;
            const el = document.getElementById('fdTimeTogether');
            if (el) el.textContent = formatDuration(liveSecs);
            else { clearInterval(_fdLiveTimer); _fdLiveTimer = null; }
        }, 1000);
    }

}

function patchFriendDetailLive(f) {
    if (!currentFriendDetail || currentFriendDetail.id !== f.id) return;
    const c = document.getElementById('friendDetailContent');
    if (!c) return;

    // displayName
    if (f.displayName) {
        const nameEl = c.querySelector('.fd-name');
        if (nameEl) {
            const plusBadge = nameEl.querySelector('.vrcn-supporter-badge');
            nameEl.textContent = f.displayName;
            if (plusBadge) nameEl.appendChild(plusBadge);
            currentFriendDetail.displayName = f.displayName;
        }
    }

    // avatar image
    if (f.image) {
        const avatarEl = c.querySelector('.fd-avatar');
        if (avatarEl?.tagName === 'IMG') avatarEl.src = f.image;
        currentFriendDetail.image = f.image;
    }

    // bio
    if (f.bio !== undefined) {
        const bioEl = c.querySelector('.fd-bio');
        if (bioEl) bioEl.textContent = f.bio;
        currentFriendDetail.bio = f.bio;
    }

    // pronouns
    if (f.pronouns !== undefined) {
        const prEl = c.querySelector('.fd-pronouns');
        if (prEl) prEl.textContent = f.pronouns;
        currentFriendDetail.pronouns = f.pronouns;
    }

    // bio links
    if (f.bioLinks) {
        const linksEl = c.querySelector('.fd-bio-links');
        if (linksEl) {
            let html = f.bioLinks.map(u => renderBioLink(u)).join('');
            if (currentFriendDetail.discordId) html += renderDiscordChip(currentFriendDetail.discordId);
            linksEl.innerHTML = html;
        }
        currentFriendDetail.bioLinks = f.bioLinks;
    }

    // tags → trust rank badge + language tags
    if (f.tags) {
        const langEl = c.querySelector('.fd-lang-tags');
        if (langEl) {
            const langs = getLanguages(f.tags);
            langEl.innerHTML = langs.map(l => `<span class="vrcn-badge">${esc(l)}</span>`).join('');
        }
        const badgesRow = c.querySelector('.fd-badges-row');
        if (badgesRow) {
            const rank = getTrustRank(f.tags);
            const platBadge = getPlatformBadgeHtml(f.platform || f.lastPlatform || currentFriendDetail.lastPlatform || '');
            const ageVerified = f.ageVerified ?? currentFriendDetail.ageVerified;
            const ageVerificationStatus = f.ageVerificationStatus ?? currentFriendDetail.ageVerificationStatus;
            let html = '';
            if (platBadge) html += platBadge;
            if (currentFriendDetail.isFriend) html += `<span class="vrcn-badge bdg-friend"><span class="msi" style="font-size:11px;">check_circle</span>${t('profiles.badges.friend', 'Friend')}</span>`;
            html += isEconomyCreator(f) ? getCreatorBadgeHtml(f) : getCreatorBadgeHtml(currentFriendDetail);
            if (ageVerified) html += `<span class="vrcn-badge ok"><span class="msi" style="font-size:11px;">verified</span>${t('profiles.meta.age_verified', 'Age Verified')}</span>`;
            if (ageVerificationStatus === '18+') html += `<span class="vrcn-badge ok"><span class="msi" style="font-size:11px;">verified</span>18+</span>`;
            if (rank) html += `<span class="vrcn-badge ${rank.cls}">${esc(rank.label)}</span>`;
            if (f.id) html += idBadge(f.id);
            badgesRow.innerHTML = html;
        }
        currentFriendDetail.tags = f.tags;
    }

    // banner (bannerUrl / profilePicOverride / currentAvatarImageUrl)
    if (f.bannerUrl !== undefined || f.profilePicOverride !== undefined || f.currentAvatarImageUrl !== undefined) {
        if (f.bannerUrl !== undefined) currentFriendDetail.bannerUrl = f.bannerUrl;
        if (f.profilePicOverride !== undefined) currentFriendDetail.profilePicOverride = f.profilePicOverride;
        if (f.currentAvatarImageUrl !== undefined) currentFriendDetail.currentAvatarImageUrl = f.currentAvatarImageUrl;
        const newSrc = currentFriendDetail.bannerUrl || currentFriendDetail.profilePicOverride || currentFriendDetail.currentAvatarImageUrl || '';
        if (newSrc) _getFdBannerImg(f.id, newSrc);
    }

    // VRC badges
    if (f.badges && Array.isArray(f.badges) && f.badges.length > 0) {
        const vrcBadgesRow = c.querySelector('.fd-vrc-badges-row');
        if (vrcBadgesRow) {
            vrcBadgesRow.innerHTML = f.badges.map(b => {
                const imgUrl = b.imageUrl || b.badgeImageUrl || '';
                const name   = b.name || b.badgeName || '';
                const desc   = b.description || b.badgeDescription || '';
                return `<div class="fd-vrc-badge-wrap" data-badge-img="${esc(imgUrl)}" data-badge-name="${encodeURIComponent(name)}" data-badge-desc="${encodeURIComponent(desc)}">
                    <img class="fd-vrc-badge-icon" src="${esc(imgThumb(imgUrl, 64))}" alt="${esc(name)}" onerror="this.closest('.fd-vrc-badge-wrap').style.display='none'">
                </div>`;
            }).join('');
        }
        currentFriendDetail.badges = f.badges;
    }

    // current world + instance owner
    if (f.location !== undefined) {
        const loc          = f.location || '';
        const worldName    = f.worldName || '';
        const worldThumb   = f.worldThumb || '';
        const instanceType = f.instanceType || '';
        const isOfflineOrPrivate = loc === 'offline' || loc === 'private' || loc === '';
        const isTraveling        = loc === 'traveling';

        if (isTraveling) {
            // user is switching — wait for the follow-up push with the new world name
        } else if (isOfflineOrPrivate) {
            c.querySelector('.fd-world-card')?.remove();
            c.querySelector('.fd-owner-card')?.remove();
            currentFriendDetail.location = loc;
            currentFriendDetail.worldName = '';
        } else if (worldName) {
            // world name is known — update both cards
            const { worldId: wid, ownerId: newOwnerId } = parseFriendLocation(loc);
            const instId     = loc.includes(':') ? (loc.split(':')[1] || '').split('~')[0] : '';
            const regionRaw  = (loc.match(/~region\(([^)]+)\)/) || [])[1] || '';
            const region     = regionRaw ? getWorldRegionLabel(regionRaw) : '';
            const onclick    = wid ? `navOpenModal('worldSearch','${jsq(wid)}','${jsq(worldName)}')` : '';

            const instanceItemHtml = renderInstanceItem({
                thumb: worldThumb, worldName, instanceType,
                instanceId: instId, region, userCount: 0, capacity: 0,
                ageGate: loc.includes('~ageGate'), location: loc, onclick,
            });
            const worldInner = `<div class="fd-group-rep-label">${t('profiles.meta.current_world', 'Current World')}</div>${instanceItemHtml}`;

            const existingWorldCard = c.querySelector('.fd-world-card');
            if (existingWorldCard) {
                existingWorldCard.innerHTML = worldInner;
            } else {
                const newCard = document.createElement('div');
                newCard.className = 'fd-info-card fd-world-card';
                newCard.innerHTML = worldInner;
                const topRow  = c.querySelector('.fd-info-top-row');
                const infoWrap = c.querySelector('.fd-info-wrap');
                if (topRow) topRow.insertBefore(newCard, topRow.firstChild);
                else if (infoWrap) infoWrap.insertBefore(newCard, infoWrap.firstChild);
            }

            // owner card
            const existingOwnerCard = c.querySelector('.fd-owner-card');
            if (newOwnerId && newOwnerId.startsWith('usr_')) {
                const ownerUser = vrcFriendsData.find(fu => fu.id === newOwnerId);
                const ownerBody = ownerUser
                    ? renderProfileItem(ownerUser, `navOpenModal('friend','${jsq(ownerUser.id)}','${jsq(ownerUser.displayName || '')}')`, { noWorld: true })
                    : `<div id="fdOwnerSlot" data-owner-id="${esc(newOwnerId)}"><div class="sk-block" style="height:44px;border-radius:8px;"></div></div>`;
                const ownerInner = `<div class="fd-group-rep-label">${t('instance.owner', 'Instance Owner')}</div>${ownerBody}`;
                if (existingOwnerCard) {
                    existingOwnerCard.innerHTML = ownerInner;
                } else {
                    const newOwnerCard = document.createElement('div');
                    newOwnerCard.className = 'fd-info-card fd-owner-card';
                    newOwnerCard.innerHTML = ownerInner;
                    const worldCard = c.querySelector('.fd-world-card');
                    const existingTopRow = c.querySelector('.fd-info-top-row');
                    if (existingTopRow) {
                        existingTopRow.appendChild(newOwnerCard);
                    } else if (worldCard) {
                        // world card is standalone — wrap both into fd-info-top-row
                        const row = document.createElement('div');
                        row.className = 'fd-info-top-row';
                        worldCard.parentNode.insertBefore(row, worldCard);
                        row.appendChild(worldCard);
                        row.appendChild(newOwnerCard);
                    }
                }
                if (!ownerUser) sendToCS({ action: 'vrcGetUserBasic', userId: newOwnerId, contextId: f.id });
            } else if (existingOwnerCard) {
                const ownerParent = existingOwnerCard.parentNode;
                existingOwnerCard.remove();
                // if world card was in a fd-info-top-row, unwrap it so it goes full width
                const topRow = c.querySelector('.fd-info-top-row');
                if (topRow) {
                    const worldCard = topRow.querySelector('.fd-world-card');
                    if (worldCard) topRow.parentNode.insertBefore(worldCard, topRow);
                    topRow.remove();
                }
            }

            currentFriendDetail.location     = loc;
            currentFriendDetail.worldName    = worldName;
            currentFriendDetail.worldThumb   = worldThumb;
            currentFriendDetail.instanceType = instanceType;
        }
        // if worldName is still empty (cache miss on first push) — no-op, wait for second push
    }
}

function renderFdTimeline(userId, events) {
    if (!currentFriendDetail || currentFriendDetail.id !== userId) return;
    _fdTimelineEvents = events || [];
    const _fdTlMore = currentFriendDetail ? `openTimelineWithChip('personal','friends','${jsq(currentFriendDetail.id || '')}','${jsq(currentFriendDetail.displayName || '')}','${jsq(currentFriendDetail.image || '')}','closeFriendDetail')` : '';
    drawMiniTimeline(_fdTimelineEvents, document.getElementById('fdMiniTl'), _fdTlMore);
}

function drawMiniTimeline(events, el, moreOnclick) {
    if (!el) return;

    if (!events || !events.length) {
        el.innerHTML = `<div style="padding:4px 0;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('timeline.empty.initial', 'No events yet')}</div>`;
        return;
    }

    el.innerHTML = tlMiniListHtml(events.map(ev => {
        const meta   = typeof tlTypeMeta === 'function' ? tlTypeMeta(ev.type) : { icon: 'event', label: ev.type };
        const color  = { instance_join:'var(--accent)', photo:'var(--ok)', first_meet:'var(--cyan)', meet_again:'#6554FF', notification:'var(--warn)', avatar_switch:'#FF7043', video_url:'#29B6F6' }[ev.type] || 'var(--tx3)';
        const d      = new Date(ev.timestamp);
        const dt     = `${fmtShortDate(d)} | ${fmtTime(d)}`;
        const ei     = ev.id.replace(/'/g, "\\'");
        const detail = typeof _tlListData === 'function' ? (_tlListData(ev).detail || '') : '';
        return `<tr class="tl-list-row" onclick="openTlDetail('${ei}', true)">
            <td class="tl-list-dt">${esc(dt)}</td>
            <td class="tl-list-type"><span class="msi tl-list-icon" style="color:${color}">${meta.icon}</span><span>${esc(meta.label)}</span></td>
            <td class="tl-list-detail">${detail || (typeof tlListNaHtml === 'function' ? tlListNaHtml() : '')}</td>
        </tr>`;
    }).join(''), moreOnclick || '');
}

function renderFdUserActivity(userId, events) {
    if (!currentFriendDetail || currentFriendDetail.id !== userId) return;
    const el = document.getElementById('fdUserActivity');
    if (!el) return;

    if (!events || !events.length) {
        el.innerHTML = `<div style="padding:4px 0;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('profiles.user_activity.empty', 'No activity recorded yet')}</div>`;
        return;
    }

    _fdUserActivityEvents = events;

    const FT_COLOR = { friend_gps:'var(--accent)', friend_status:'var(--cyan)', friend_statusdesc:'var(--cyan)', friend_online:'var(--ok)', friend_offline:'var(--tx3)', friend_bio:'#6554FF', friend_added:'var(--ok)', friend_removed:'var(--err)' };

    el.innerHTML = tlMiniListHtml(events.map(ev => {
        const meta   = typeof ftTypeMeta === 'function' ? ftTypeMeta(ev.type) : { icon: 'circle', label: ev.type };
        const color  = FT_COLOR[ev.type] || 'var(--tx3)';
        const d      = new Date(ev.timestamp);
        const dt     = `${fmtShortDate(d)} | ${fmtTime(d)}`;
        const ei     = jsq(ev.id);
        const detail = typeof _ftListDetail === 'function' ? (_ftListDetail(ev) || '') : '';
        return `<tr class="tl-list-row" onclick="openFdActivityDetail('${ei}')">
            <td class="tl-list-dt">${esc(dt)}</td>
            <td class="tl-list-type"><span class="msi tl-list-icon" style="color:${color}">${meta.icon}</span><span>${esc(meta.label)}</span></td>
            <td class="tl-list-detail">${detail || (typeof tlListNaHtml === 'function' ? tlListNaHtml() : '')}</td>
        </tr>`;
    }).join(''), `openTimelineWithChip('friends','friends','${jsq(currentFriendDetail.id || '')}','${jsq(currentFriendDetail.displayName || '')}','${jsq(currentFriendDetail.image || '')}','closeFriendDetail')`);
}

function switchFdMiniTlPill(pill, btn) {
    const tl = document.getElementById('fdMiniTl');
    const ua = document.getElementById('fdUserActivity');
    if (tl) tl.style.display = pill === 'timeline'  ? '' : 'none';
    if (ua) ua.style.display = pill === 'activity' ? '' : 'none';
    document.querySelectorAll('.fd-mini-tl-pill').forEach(p => p.classList.remove('active'));
    if (btn) btn.classList.add('active');
}

function switchFdInsightsPill(pill, btn) {
    const w = document.getElementById('fdInsightsWorlds');
    const p = document.getElementById('fdInsightsPersons');
    if (w) w.style.display = pill === 'worlds'  ? '' : 'none';
    if (p) p.style.display = pill === 'persons' ? '' : 'none';
    document.querySelectorAll('.fd-insights-pill').forEach(x => x.classList.remove('active'));
    if (btn) btn.classList.add('active');
}

function renderFdProfileInsights(payload) {
    if (!currentFriendDetail || currentFriendDetail.id !== payload.userId) return;
    renderFdInsightsWorlds(payload.worlds || []);
    renderFdInsightsPersons(payload.persons || []);
}

function renderFdInsightsWorlds(worlds, elId = 'fdInsightsWorlds') {
    const el = document.getElementById(elId);
    if (!el) return;
    if (!worlds.length) {
        el.innerHTML = `<div style="padding:4px 0;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('profiles.insights.no_worlds', 'No world data yet')}</div>`;
        return;
    }
    const maxVisits = worlds[0].visits || 1;
    el.innerHTML = '<div class="ts-items">' + worlds.map((w, i) => {
        const pct = Math.round((w.visits / maxVisits) * 100);
        const thumb = w.worldThumb
            ? `<img class="ts-item-thumb" src="${esc(imgThumb(w.worldThumb, 96))}" onerror="this.style.display='none'">`
            : `<div class="ts-item-thumb ts-thumb-placeholder"><span class="msi" style="font-size:18px;color:var(--tx2);">travel_explore</span></div>`;
        const click = w.worldId ? `onclick="navOpenModal('worldSearch','${jsq(w.worldId)}','${jsq(w.worldName || '')}')" style="cursor:pointer"` : '';
        const visits = tf(`timespent.visit.${w.visits === 1 ? 'one' : 'other'}`, { count: w.visits }, `${w.visits} visit${w.visits === 1 ? '' : 's'}`);
        return `<div class="ts-item" ${w.worldId ? `data-world-id="${esc(w.worldId)}"` : ''} ${click}>
            <div class="ts-item-rank">#${i + 1}</div>
            ${thumb}
            <div class="ts-item-body">
                <div class="ts-item-name">${esc(w.worldName || t('timespent.unknown_world_full', 'Unknown World'))}</div>
                <div class="ts-item-meta"><span class="msi" style="font-size:12px;color:var(--tx2);">login</span><span>${visits}</span></div>
                <div class="ts-bar-wrap"><div class="ts-bar" style="width:${pct}%"></div></div>
            </div>
        </div>`;
    }).join('') + '</div>';
}

function renderFdInsightsPersons(persons, elId = 'fdInsightsPersons') {
    const el = document.getElementById(elId);
    if (!el) return;
    if (!persons.length) {
        el.innerHTML = `<div style="padding:4px 0;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);">${t('profiles.insights.no_persons', 'No interaction data yet')}</div>`;
        return;
    }
    const friendIds = new Set((typeof vrcFriendsData !== 'undefined' ? vrcFriendsData : []).map(f => f.id));
    const maxMeets = persons[0].meets || 1;
    el.innerHTML = '<div class="ts-items">' + persons.map((p, i) => {
        const pct = Math.round((p.meets / maxMeets) * 100);
        const isFriend = friendIds.has(p.userId);
        const avatar = p.image
            ? `<img class="ts-item-avatar" src="${esc(imgThumb(p.image, 96))}" onerror="this.style.display='none'">`
            : `<div class="ts-item-avatar ts-avatar-placeholder"><span class="msi" style="font-size:16px;color:var(--tx2);">person</span></div>`;
        const encounters = tf(`timespent.encounter.${p.meets === 1 ? 'one' : 'other'}`, { count: p.meets }, `${p.meets} encounter${p.meets === 1 ? '' : 's'}`);
        return `<div class="ts-item" data-user-id="${esc(p.userId)}" onclick="navOpenModal('friend','${jsq(p.userId)}','${jsq(p.displayName || '')}')" style="cursor:pointer">
            <div class="ts-item-rank">#${i + 1}</div>
            <div class="ts-avatar-wrap">${avatar}</div>
            <div class="ts-item-body">
                <div class="ts-item-name">${esc(p.displayName || p.userId)}</div>
                <div class="ts-item-meta"><span class="msi" style="font-size:12px;color:var(--tx2);">handshake</span><span>${encounters}</span></div>
                <div class="ts-bar-wrap"><div class="ts-bar ${isFriend ? 'ts-bar-friend' : 'ts-bar-stranger'}" style="width:${pct}%"></div></div>
            </div>
        </div>`;
    }).join('') + '</div>';
}

let _fdHeatmapDays = 30;
let _fdHeatmapView = 'online';
let _fdStatusData = null;

function fdRequestHeatmap() {
    const uid = currentFriendDetail?.id;
    if (!uid) return;
    const icon = document.getElementById('fdHmRefreshIcon');
    if (icon) icon.classList.add('ts-spin');
    const isStatus = _fdHeatmapView !== 'online';
    if (isStatus) {
        const sw = document.getElementById('fdHmStatusWrap');
        if (sw && !sw.querySelector('.fd-hm-grid'))
            sw.innerHTML = `<div style="padding:16px 0;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);text-align:center;">${t('profiles.insights.loading', 'Loading...')}</div>`;
    }
    sendToCS({ action: isStatus ? 'getUserStatusTime' : 'getUserOnlineHeatmap', userId: uid, days: _fdHeatmapDays });
}

function fdChangeHeatmapPeriod(v) {
    _fdHeatmapDays = parseInt(v, 10) || 0;
    _fdStatusData = null;
    fdRequestHeatmap();
}

function fdChangeHeatmapView(v) {
    _fdHeatmapView = v;
    const isOnline = v === 'online';
    const grid = document.getElementById('fdHmGridWrap');
    const status = document.getElementById('fdHmStatusWrap');
    if (grid) grid.style.display = isOnline ? '' : 'none';
    if (status) status.style.display = isOnline ? 'none' : '';
    if (isOnline) { fdRequestHeatmap(); return; }
    const uid = currentFriendDetail?.id;
    if (_fdStatusData && _fdStatusData.userId === uid && _fdStatusData.days === _fdHeatmapDays) {
        renderFdStatusTime(_fdStatusData);
    } else {
        fdRequestHeatmap();
    }
}

function fdReloadHeatmap() {
    if (_fdHeatmapView !== 'online') _fdStatusData = null;
    fdRequestHeatmap();
}

function fdFmtMinutes(mins) {
    const m = Math.round(mins);
    if (m < 60) return `${m}m`;
    const h = Math.floor(m / 60);
    const rem = m % 60;
    return rem > 0 ? `${h}h ${rem}m` : `${h}h`;
}

const FD_HM_IDS = {
    icon:   'fdHmRefreshIcon',
    count:  'fdHmCount',
    stats:  'fdHmStats',
    grid:   'fdHmGridWrap',
    status: 'fdHmStatusWrap',
    mostly: 'fdInfoStatusMostly',
};

function renderFdOnlineHeatmap(payload) {
    if (!currentFriendDetail || currentFriendDetail.id !== payload.userId) return;
    drawOnlineHeatmap(payload, FD_HM_IDS);
}

function drawOnlineHeatmap(payload, ids) {
    const icon = document.getElementById(ids.icon);
    if (icon) icon.classList.remove('ts-spin');

    const buckets = payload.buckets || [];
    const totalMinutes = payload.totalMinutes || 0;

    const countEl = document.getElementById(ids.count);
    if (countEl) countEl.textContent = totalMinutes > 0
        ? tf('profiles.heatmap.total_online', { time: fdFmtMinutes(totalMinutes) }, `${fdFmtMinutes(totalMinutes)} online`)
        : '';

    const fmt = new Intl.DateTimeFormat(typeof getLanguageLocale === 'function' ? getLanguageLocale() : undefined, { weekday: 'short' });
    const dayLabels = Array.from({ length: 7 }, (_, i) => fmt.format(new Date(2024, 0, 8 + i)));

    let max = 0;
    const dayTotals = new Array(7).fill(0);
    const hourTotals = new Array(24).fill(0);
    for (let d = 0; d < 7; d++) {
        for (let h = 0; h < 24; h++) {
            const val = buckets[d * 24 + h] || 0;
            if (val > max) max = val;
            dayTotals[d] += val;
            hourTotals[h] += val;
        }
    }

    const statsEl = document.getElementById(ids.stats);
    if (statsEl) {
        if (totalMinutes <= 0) {
            statsEl.innerHTML = '';
        } else {
            let bestDay = 0;
            for (let d = 1; d < 7; d++) if (dayTotals[d] > dayTotals[bestDay]) bestDay = d;

            const maxHour = Math.max(...hourTotals);
            let peakLabel = '';
            if (maxHour > 0) {
                const threshold = maxHour * 0.7;
                let startH = hourTotals.indexOf(maxHour);
                let endH = startH;
                while (startH > 0 && hourTotals[startH - 1] >= threshold) startH--;
                while (endH < 23 && hourTotals[endH + 1] >= threshold) endH++;
                peakLabel = startH === endH
                    ? `${String(startH).padStart(2, '0')}:00`
                    : `${String(startH).padStart(2, '0')}:00-${String(endH + 1).padStart(2, '0')}:00`;
            }
            statsEl.innerHTML = `<span>${t('profiles.heatmap.most_active_day', 'Most active day')}: <strong>${esc(dayLabels[bestDay])}</strong></span>
                <span>${t('profiles.heatmap.peak_hours', 'Peak hours')}: <strong>${esc(peakLabel)}</strong></span>`;
        }
    }

    const wrap = document.getElementById(ids.grid);
    if (!wrap) return;
    if (totalMinutes <= 0) {
        wrap.innerHTML = `<div style="padding:16px 0;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);text-align:center;">${t('profiles.heatmap.empty', 'No online activity recorded yet')}</div>`;
        return;
    }

    let rowsHtml = '';
    for (let d = 0; d < 7; d++) {
        let cells = '';
        for (let h = 0; h < 24; h++) {
            const val = buckets[d * 24 + h] || 0;
            const intensity = max > 0 ? Math.sqrt(val / max) : 0;
            const title = `${dayLabels[d]} ${String(h).padStart(2, '0')}:00 · ${fdFmtMinutes(val)}`;
            const style = val > 0
                ? `style="background:color-mix(in srgb, var(--hm-online, var(--accent)) ${Math.round(20 + intensity * 80)}%, transparent);"`
                : '';
            cells += `<div class="fd-hm-cell" ${style} title="${esc(title)}"></div>`;
        }
        rowsHtml += `<div class="fd-hm-row"><div class="fd-hm-row-label">${esc(dayLabels[d])}</div><div class="fd-hm-cells">${cells}</div></div>`;
    }

    let axis = '<div class="fd-hm-axis"><div class="fd-hm-axis-spacer"></div><div class="fd-hm-axis-hours">';
    for (let h = 0; h < 24; h++) {
        axis += `<div class="fd-hm-axis-h">${h % 3 === 0 ? String(h).padStart(2, '0') + ':00' : ''}</div>`;
    }
    axis += '</div></div>';

    wrap.innerHTML = `<div class="fd-hm-grid">${rowsHtml}</div>${axis}`;
}

const FD_STATUS_ORDER = ['join me', 'ask me', 'active', 'busy', 'unknown'];

function fdStatusMeta() {
    return {
        'join me': { label: t('status.join_me', 'Join Me'), color: 'var(--status-join)' },
        'active':  { label: t('status.online', 'Online'), color: 'var(--status-online)' },
        'ask me':  { label: t('status.ask_me', 'Ask Me'), color: 'var(--status-ask)' },
        'busy':    { label: t('status.do_not_disturb', 'Do Not Disturb'), color: 'var(--status-busy)' },
        'unknown': { label: t('status.unknown', 'Unknown'), color: 'var(--status-offline)' },
    };
}

function renderFdStatusTime(payload) {
    if (!currentFriendDetail || currentFriendDetail.id !== payload.userId) return;
    _fdStatusData = payload;
    drawStatusHeatmap(payload, FD_HM_IDS, _fdHeatmapView);
}

function drawStatusHeatmap(payload, ids, view) {
    const META = fdStatusMeta();
    const buckets = payload.buckets || {};
    const totals = payload.totals || {};
    const total = payload.totalSeconds || 0;

    const mostlyEl = document.getElementById(ids.mostly);
    if (mostlyEl && payload.days === 30) {
        let topKey = '', topSec = 0;
        for (const k of Object.keys(META)) { if (k === 'unknown') continue; const s = totals[k] || 0; if (s > topSec) { topSec = s; topKey = k; } }
        if (topKey) {
            mostlyEl.textContent = META[topKey].label;
            mostlyEl.style.color = META[topKey].color;
        } else {
            mostlyEl.textContent = '—';
            mostlyEl.style.color = 'var(--tx3)';
        }
    }

    if (view === 'online') return;

    const icon = document.getElementById(ids.icon);
    if (icon) icon.classList.remove('ts-spin');

    const wrap = document.getElementById(ids.status);
    if (!wrap) return;

    const countEl = document.getElementById(ids.count);
    if (countEl) countEl.textContent = total > 0 ? fdFmtMinutes(total / 60) : '';

    const statsEl = document.getElementById(ids.stats);
    if (statsEl) {
        statsEl.innerHTML = FD_STATUS_ORDER.map(k => {
            const secs = totals[k] || 0;
            const pct = total > 0 ? Math.round((secs / total) * 100) : 0;
            return `<span class="fd-st-chip"><span class="fd-st-cdot" style="background:${META[k].color};"></span>${esc(META[k].label)} ${pct}% ${fdFmtMinutes(secs / 60)}</span>`;
        }).join('');
    }

    if (total <= 0) {
        wrap.innerHTML = `<div style="padding:16px 0;font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);text-align:center;">${t('profiles.heatmap.no_status', 'No status data yet')}</div>`;
        return;
    }

    const fmt = new Intl.DateTimeFormat(typeof getLanguageLocale === 'function' ? getLanguageLocale() : undefined, { weekday: 'short' });
    const dayLabels = Array.from({ length: 7 }, (_, i) => fmt.format(new Date(2024, 0, 8 + i)));

    const keys = Object.keys(META);

    const cellVal = (slot) => {
        if (view === 'all') {
            let sum = 0, domKey = '', domVal = 0;
            for (const k of keys) {
                const v = (buckets[k] && buckets[k][slot]) || 0;
                sum += v;
                if (v > domVal) { domVal = v; domKey = k; }
            }
            return { val: sum, color: domKey ? META[domKey].color : 'var(--accent)', dom: domKey };
        }
        const v = (buckets[view] && buckets[view][slot]) || 0;
        return { val: v, color: META[view] ? META[view].color : 'var(--accent)', dom: view };
    };

    let max = 0;
    for (let slot = 0; slot < 168; slot++) { const c = cellVal(slot).val; if (c > max) max = c; }

    let rowsHtml = '';
    for (let d = 0; d < 7; d++) {
        let cells = '';
        for (let h = 0; h < 24; h++) {
            const c = cellVal(d * 24 + h);
            const intensity = max > 0 ? Math.sqrt(c.val / max) : 0;
            const domLabel = c.dom && META[c.dom] ? ` (${META[c.dom].label})` : '';
            const title = `${dayLabels[d]} ${String(h).padStart(2, '0')}:00 · ${fdFmtMinutes(c.val)}${view === 'all' && c.val > 0 ? esc(domLabel) : ''}`;
            const style = c.val > 0
                ? `style="background:color-mix(in srgb, ${c.color} ${Math.round(20 + intensity * 80)}%, transparent);"`
                : '';
            cells += `<div class="fd-hm-cell" ${style} title="${esc(title)}"></div>`;
        }
        rowsHtml += `<div class="fd-hm-row"><div class="fd-hm-row-label">${esc(dayLabels[d])}</div><div class="fd-hm-cells">${cells}</div></div>`;
    }

    let axis = '<div class="fd-hm-axis"><div class="fd-hm-axis-spacer"></div><div class="fd-hm-axis-hours">';
    for (let h = 0; h < 24; h++) {
        axis += `<div class="fd-hm-axis-h">${h % 3 === 0 ? String(h).padStart(2, '0') + ':00' : ''}</div>`;
    }
    axis += '</div></div>';

    wrap.innerHTML = `<div class="fd-hm-grid">${rowsHtml}</div>${axis}`;
}

function friendAction(action, location, userId) {
    const btnContainer = document.querySelector('.fd-actions');
    if (btnContainer) btnContainer.querySelectorAll('button').forEach(b => b.disabled = true);
    if (action === 'join') sendToCS({ action: 'vrcJoinFriend', location: location });
    else if (action === 'invite') sendToCS({ action: 'vrcInviteFriend', userId: userId });
    else if (action === 'requestInvite') sendToCS({ action: 'vrcRequestInvite', userId: userId });
}

function confirmUnfriend(userId, displayName) {
    const btn = document.getElementById('fdUnfriend');
    if (!btn) return;
    if (btn.dataset.confirm) {
        btn.disabled = true;
        btn.innerHTML = '<span class="msi" style="font-size:14px;">hourglass_empty</span>';
        sendToCS({ action: 'vrcUnfriend', userId: userId });
    } else {
        btn.dataset.confirm = '1';
        btn.innerHTML = `<span style="font-size:calc(11px + var(--fs-off, 0px));font-weight:600;">${t('profiles.actions.confirm', 'Confirm?')}</span>`;
        setTimeout(() => {
            if (btn && !btn.disabled) {
                delete btn.dataset.confirm;
                btn.innerHTML = '<span class="msi" style="font-size:16px;">person_remove</span>';
            }
        }, 4000);
    }
}

function toggleFriendFavPicker(userId) {
    const entry = favFriendsData.find(f => f.favoriteId === userId);
    if (entry) {
        const btn = document.getElementById('fdFavBtn');
        if (btn) btn.disabled = true;
        sendToCS({ action: 'vrcRemoveFavoriteFriend', userId, fvrtId: entry.fvrtId });
        return;
    }
    const picker = document.getElementById('fdFavPicker');
    if (!picker) return;
    const open = picker.style.display !== 'none';
    picker.style.display = open ? 'none' : '';
    if (!open) renderFriendFavPicker(userId);
}

function renderFriendFavPicker(userId) {
    const list = document.getElementById('fdFavGroupList');
    if (!list) return;
    if (favFriendGroups.length === 0) {
        list.innerHTML = `<div style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);padding:8px 0;">Loading groups...</div>`;
        sendToCS({ action: 'vrcGetFriendFavGroups' });
        list.dataset.pendingUserId = userId;
        return;
    }
    const currentEntry = favFriendsData.find(f => f.favoriteId === userId);
    const currentGroup = currentEntry?.groupName || '';
    list.innerHTML = favFriendGroups.map(g => {
        const count = favFriendsData.filter(f => f.groupName === g.name).length;
        const cap = g.capacity || 150;
        const isCurrent = g.name === currentGroup;
        const check = isCurrent
            ? `<span class="msi" style="color:var(--accent);font-size:18px;flex-shrink:0;">check_circle</span>`
            : '';
        const gn = jsq(g.name), uid = jsq(userId);
        const oldFvrt = isCurrent ? jsq(currentEntry?.fvrtId || '') : '';
        return `<div class="fd-group-card ci-group-card${isCurrent ? ' ci-group-selected' : ''}"
            onclick="addFriendToFavGroup('${uid}','${gn}','${oldFvrt}',this)" style="cursor:pointer;">
            <div style="flex:1;min-width:0;">
                <div style="display:flex;align-items:center;gap:5px;flex-wrap:wrap;">
                    <span style="font-size:calc(12px + var(--fs-off, 0px));font-weight:600;color:var(--tx0);">${esc(g.displayName || g.name)}</span>
                    ${favGroupBadge(g)}
                </div>
                <div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx2);margin-top:1px;">${count}/${cap} friends</div>
            </div>
            ${check}
        </div>`;
    }).join('');
}

function addFriendToFavGroup(userId, groupName, oldFvrtId, rowEl) {
    document.querySelectorAll('#fdFavGroupList .ci-group-card').forEach(c => {
        c.classList.remove('ci-group-selected');
        const chk = c.querySelector('.msi');
        if (chk && chk.textContent === 'check_circle') chk.remove();
    });
    rowEl.classList.add('ci-group-selected');
    rowEl.insertAdjacentHTML('beforeend', '<span class="msi" style="color:var(--accent);font-size:18px;flex-shrink:0;">check_circle</span>');
    if (oldFvrtId) {
        sendToCS({ action: 'vrcAddFavoriteFriendToGroup', userId, groupName, oldFvrtId });
    } else {
        sendToCS({ action: 'vrcAddFavoriteFriend', userId, groupName });
    }
}

function handleFavFriendToggled(payload) {
    const { userId, fvrtId, isFavorited, groupName } = payload;
    favFriendsData = favFriendsData.filter(f => f.favoriteId !== userId);
    if (isFavorited) favFriendsData.push({ fvrtId, favoriteId: userId, groupName: groupName || 'group_0' });
    const btn = document.getElementById('fdFavBtn');
    if (btn) {
        btn.disabled = false;
        btn.classList.toggle('active', isFavorited);
        btn.title = isFavorited ? t('profiles.actions.unfavorite', 'Unfavorite') : t('profiles.actions.favorite', 'Favorite');
        btn.innerHTML = `<span class="msi" style="font-size:16px;">${isFavorited ? 'favorite' : 'favorite_border'}</span>`;
    }
    const picker = document.getElementById('fdFavPicker');
    if (isFavorited) {
        if (picker && picker.style.display !== 'none') renderFriendFavPicker(userId);
    } else {
        if (picker) picker.style.display = 'none';
    }
    filterFavFriends();
    renderVrcFriends(vrcFriendsData);
    _scheduleBgFavFriendRefresh();
}

function handleFriendFavoriteResult(data) {
    if (data.ok) {
        const entry = favFriendsData.find(f => f.favoriteId === data.userId);
        if (entry) {
            entry.groupName = data.groupName;
            entry.fvrtId   = data.newFvrtId;
        }
        const group = (typeof favFriendGroups !== 'undefined') && favFriendGroups.find(g => g.name === data.groupName);
        const groupLabel = group?.displayName || data.groupName;
        showToast(true, `Moved to ${groupLabel}`);
        const list = document.getElementById('fdFavGroupList');
        if (list && document.getElementById('fdFavPicker')?.style.display !== 'none') renderFriendFavPicker(data.userId);
        filterFavFriends();
        _scheduleBgFavFriendRefresh();
    } else {
        if (data.error) showToast(false, localFavErrorText(data.error));
        const list = document.getElementById('fdFavGroupList');
        if (list) {
            list.innerHTML = `<div style="font-size:calc(11px + var(--fs-off, 0px));color:var(--err,#e55);padding:6px 0;">Failed to move. Try again.</div>`;
            setTimeout(() => { if (document.getElementById('fdFavGroupList')) renderFriendFavPicker(data.userId); }, 1800);
        }
    }
}

function onFriendFavGroupsLoaded(groups) {
    favFriendGroups = groups;
    const list = document.getElementById('fdFavGroupList');
    if (list?.dataset.pendingUserId) {
        const uid = list.dataset.pendingUserId;
        delete list.dataset.pendingUserId;
        renderFriendFavPicker(uid);
    }
}


function _buildModCardInner(userId) {
    const isBlocked     = Array.isArray(blockedData)      && blockedData.some(x => x.targetUserId === userId);
    const isMuted       = Array.isArray(mutedData)        && mutedData.some(x => x.targetUserId === userId);
    const isChatMuted   = Array.isArray(muteChatData)     && muteChatData.some(x => x.targetUserId === userId);
    const isAvatarHid   = Array.isArray(hiddenAvatarData) && hiddenAvatarData.some(x => x.targetUserId === userId);
    const isInteractOff = Array.isArray(interactOffData)  && interactOffData.some(x => x.targetUserId === userId);
    const _row = (label, active, activeKey, activeFb, inactiveKey, inactiveFb) =>
        `<div style="display:flex;justify-content:space-between;gap:8px;align-items:baseline;font-size:calc(11px + var(--fs-off, 0px));">
            <span style="color:var(--tx2);">${label}</span>
            <span style="color:${active ? 'var(--err)' : 'var(--tx1)'};text-align:right;">${active ? t(activeKey, activeFb) : t(inactiveKey, inactiveFb)}</span>
        </div>`;
    return `<div class="fd-group-rep-label">${t('profiles.moderation.title', 'Moderation')}</div>
        <div style="display:grid;gap:6px;">
            ${_row(t('profiles.moderation.status','Status'),       isBlocked,     'profiles.moderation.blocked',     'Blocked',   'profiles.moderation.not_blocked','Not Blocked')}
            ${_row(t('profiles.moderation.voice','Voice'),         isMuted,       'profiles.moderation.muted',       'Muted',     'profiles.moderation.not_muted',  'Not Muted')}
            ${_row(t('profiles.moderation.chat','Chat'),           isChatMuted,   'profiles.moderation.muted',       'Muted',     'profiles.moderation.not_muted',  'Not Muted')}
            ${_row(t('profiles.moderation.avatar','Avatar'),       isAvatarHid,   'profiles.moderation.hidden',      'Hidden',    'profiles.moderation.shown',      'Shown')}
            ${_row(t('profiles.moderation.interactions','Interactions'), isInteractOff, 'profiles.moderation.off', 'Off',       'profiles.moderation.on',         'On')}
        </div>`;
}

function renderFdModerationCard(userId) {
    const card = document.getElementById('fdModerationCard');
    if (card) card.innerHTML = _buildModCardInner(userId);
}

function _fdCopyableThemeColors(d) {
    if (!d || typeof ptHex !== 'function') return null;
    let button  = ptHex(d.themeButtonColor);
    let icon    = ptHex(d.themeIconColor);
    let subtext = ptHex(d.themeSubtextColor);
    if (!button && !icon && !subtext) {
        const th = Array.isArray(d.themes) && d.themeId ? d.themes.find(x => x && x.id === d.themeId) : null;
        if (th) {
            button  = ptHex(th.buttonColor);
            icon    = ptHex(th.iconColor);
            subtext = ptHex(th.subtextColor);
        }
    }
    if (!button && !icon && !subtext) return null;
    return { button, icon, subtext };
}

function copyProfileThemeFromDetail() {
    const d = currentFriendDetail;
    const c = _fdCopyableThemeColors(d);
    if (!c) { showToast(false, t('profiles.theme.no_theme', 'This profile has no theme to copy.')); return; }
    if (typeof openProfileThemeEditor !== 'function') return;
    openProfileThemeEditor('', { button: c.button, icon: c.icon, subtext: c.subtext, name: d.displayName || '' });
}

function _fdBuildTaskbarActions(d) {
    const _fid  = jsq(d.id || '');
    const _mBlk = Array.isArray(blockedData)      && blockedData.some(x => x.targetUserId === d.id);
    const _mMut = Array.isArray(mutedData)        && mutedData.some(x => x.targetUserId === d.id);
    const _mCht = Array.isArray(muteChatData)     && muteChatData.some(x => x.targetUserId === d.id);
    const _mAvt = Array.isArray(hiddenAvatarData) && hiddenAvatarData.some(x => x.targetUserId === d.id);
    const _mInt = Array.isArray(interactOffData)  && interactOffData.some(x => x.targetUserId === d.id);
    const _invG = (typeof myGroups !== 'undefined') ? myGroups.filter(g => g.canInvite === true) : [];
    const _moreItems = [
        d.isFriend ? { icon: 'waving_hand', label: t('context_menu.friend.boop', 'Boop!'), onclick: `openBoopModal('${_fid}','${jsq(d.displayName || _fid)}')` } : null,
        (d.isFriend && _invG.length) ? { icon: 'group_add', label: t('context_menu.friend.invite_group', 'Invite to Group'), submenu: _invG.map(g => ({ icon: 'group', label: g.name || g.id, onclick: `sendToCS({action:'vrcInviteToGroup',groupId:'${jsq(g.id)}',userIds:['${_fid}']});showToast(true,t('context_menu.friend.invite_group_sent','Invite sent!'))` })) } : null,
        { icon: 'shield_person', label: t('context_menu.friend.moderate', 'Moderate'), submenu: [
            { icon: _mBlk ? 'lock_open' : 'block',           label: _mBlk ? t('context_menu.friend.unblock', 'Unblock')                  : t('context_menu.friend.block', 'Block'),                       onclick: `sendToCS({action:'${_mBlk ? 'vrcUnblock' : 'vrcBlock'}',userId:'${_fid}'})` },
            { icon: _mMut ? 'mic' : 'mic_off',               label: _mMut ? t('context_menu.friend.unmute', 'Unmute')                    : t('context_menu.friend.mute', 'Mute'),                         onclick: `sendToCS({action:'${_mMut ? 'vrcUnmute' : 'vrcMute'}',userId:'${_fid}'})` },
            { icon: _mCht ? 'chat' : 'comments_disabled',    label: _mCht ? t('context_menu.friend.unmute_chat', 'Unmute Chat')           : t('context_menu.friend.mute_chat', 'Mute Chat'),               onclick: `sendToCS({action:'${_mCht ? 'vrcUnmuteChat' : 'vrcMuteChat'}',userId:'${_fid}'})` },
            { icon: _mAvt ? 'visibility' : 'visibility_off', label: _mAvt ? t('context_menu.friend.show_avatar', 'Show Avatar')           : t('context_menu.friend.hide_avatar', 'Hide Avatar'),           onclick: `sendToCS({action:'${_mAvt ? 'vrcShowAvatar' : 'vrcHideAvatar'}',userId:'${_fid}'})` },
            { icon: _mInt ? 'touch_app' : 'do_not_touch',    label: _mInt ? t('context_menu.friend.interact_on', 'Turn On Interactions') : t('context_menu.friend.interact_off', 'Turn Off Interactions'), onclick: `sendToCS({action:'${_mInt ? 'vrcInteractOn' : 'vrcInteractOff'}',userId:'${_fid}'})` },
        ] },
        _fdCopyableThemeColors(d) ? { icon: 'palette', label: t('context_menu.copy_theme', 'Copy Theme'), onclick: `copyProfileThemeFromDetail()` } : null,
    ].filter(Boolean);
    const out = [
        { icon: 'refresh', iconClass: _fdRefreshing ? 'fd-action-spin' : '', title: t('common.refresh', 'Refresh'), label: t('common.refresh', 'Refresh'), onclick: `refreshFriendDetailModal('${_fid}')` },
        { icon: 'link_2', title: t('common.share', 'Share'), label: t('common.share_profile', 'Share Profile'), onclick: `navigator.clipboard.writeText('https://vrchat.com/home/user/${esc(d.id)}').then(()=>showToast(true,t('common.link_copied','Link copied!')))` },
    ];
    if (_moreItems.length) out.push({ label: t('common.more', 'More'), dropdown: _moreItems });
    out.push({ icon: 'close', title: t('common.close', 'Close'), label: t('common.close', 'Close'), onclick: `closeFriendDetail()` });
    return out;
}

function refreshFdTaskbarActions() {
    if (!currentFriendDetail || typeof refreshModalActions !== 'function') return;
    const md = document.getElementById('modalFriendDetail');
    if (!md || md.style.display === 'none') return;
    refreshModalActions(_fdBuildTaskbarActions(currentFriendDetail));
}

let _fdRefreshing = false;
let _fdRefreshTimer = null;
function refreshFriendDetailModal(userId) {
    if (!userId) return;
    _fdRefreshing = true;
    if (_fdRefreshTimer) clearTimeout(_fdRefreshTimer);
    _fdRefreshTimer = setTimeout(() => {
        _fdRefreshing = false;
        _fdRefreshTimer = null;
        refreshFdTaskbarActions();
    }, 1500);
    refreshFdTaskbarActions();
    sendToCS({ action: 'vrcGetFriendDetail', userId, force: true });
}

function handleUserBasic(payload) {
    const slot = document.getElementById('fdOwnerSlot');
    if (!slot || slot.dataset.ownerId !== payload.id) return;
    if (!currentFriendDetail || currentFriendDetail.id !== payload.contextId) return;
    const onclick = `navOpenModal('friend','${jsq(payload.id)}','${jsq(payload.displayName || '')}')`;
    slot.outerHTML = renderProfileItem(payload, onclick, { noWorld: true });
}

// Global VRC badge tooltip (position: fixed, escapes modal overflow)
(function () {
    let tip = null;

    function getTip() {
        if (!tip) {
            tip = document.createElement('div');
            tip.className = 'fd-vrc-badge-tooltip-global';
            document.body.appendChild(tip);
        }
        return tip;
    }

    document.addEventListener('mouseover', function (e) {
        const wrap = e.target.closest('.fd-vrc-badge-wrap');
        if (!wrap) return;
        const t = getTip();
        const img  = wrap.dataset.badgeImg  || '';
        const name = decodeURIComponent(wrap.dataset.badgeName || '');
        const desc = decodeURIComponent(wrap.dataset.badgeDesc || '');
        t.innerHTML =
            `<img class="fd-vrc-badge-tip-img" src="${esc(img)}" alt="">` +
            `<div class="fd-vrc-badge-tip-text">` +
                `<div class="fd-vrc-badge-tip-name">${esc(name)}</div>` +
                (desc ? `<div class="fd-vrc-badge-tip-desc">${esc(desc)}</div>` : '') +
            `</div>`;

        t.style.opacity = '0';
        t.style.display = 'flex';
        const tw = t.offsetWidth;
        const th = t.offsetHeight;
        const rect = wrap.getBoundingClientRect();
        const rLeft  = rect.left;
        const rTop   = rect.top;
        const rWidth = rect.width;
        const rBot   = rect.bottom;
        const vw = window.innerWidth;
        let x = rLeft + rWidth / 2 - tw / 2;
        let y = rTop - th - 8;

        x = Math.max(8, Math.min(vw - tw - 8, x));
        if (y < 8) y = rBot + 8;

        t.style.left = x + 'px';
        t.style.top  = y + 'px';
        t.style.opacity = '1';
    });

    document.addEventListener('mouseout', function (e) {
        const wrap = e.target.closest('.fd-vrc-badge-wrap');
        if (!wrap) return;
        if (wrap.contains(e.relatedTarget)) return;
        if (tip) tip.style.opacity = '0';
    });
}());

function _fdWmState(s) {
    if (s === undefined) return {
        detail:            currentFriendDetail,
        loadedAvatarKey:   _fdLoadedAvatarKey,
        lastAvatarPayload: _fdLastAvatarPayload,
        groupsSort:        _fdGroupsSortMode,
        mutualsSort:       _fdMutualsSortMode,
        mutualsGroupsSort: _fdMutualsGroupsSortMode,
        heatmapDays:       _fdHeatmapDays,
        heatmapView:       _fdHeatmapView,
        statusData:        _fdStatusData,
        allGroups:         window._fdAllGroups,
        allGroupsAll:      window._fdAllGroupsAll,
        allOwnGroups:      window._fdAllOwnGroups,
        allMutualGroups:   window._fdAllMutualGroups,
        allMutuals:        window._fdAllMutuals,
        allWorlds:         window._fdAllWorlds,
        allAvatars:        window._fdAllAvatars,
        repGroup:          window._fdRepGroup,
        groupsPage:        window._fdGroupsPage,
        ownGroupsPage:     window._fdOwnGroupsPage,
        mutualsPage:       window._fdMutualsPage,
        mutualsGroupsPage: window._fdMutualsGroupsPage,
        worldsPage:        window._fdWorldsPage,
        avatarsPage:       window._fdAvatarsPage,
    };
    s = s || {};
    currentFriendDetail        = s.detail            ?? null;
    _fdLoadedAvatarKey         = s.loadedAvatarKey   ?? '';
    _fdLastAvatarPayload       = s.lastAvatarPayload ?? null;
    _fdGroupsSortMode          = s.groupsSort        ?? 'alpha';
    _fdMutualsSortMode         = s.mutualsSort       ?? 'alpha';
    _fdMutualsGroupsSortMode   = s.mutualsGroupsSort ?? 'alpha';
    _fdHeatmapDays             = s.heatmapDays       ?? 30;
    _fdHeatmapView             = s.heatmapView       ?? 'online';
    _fdStatusData              = s.statusData        ?? null;
    window._fdAllGroups        = s.allGroups         ?? null;
    window._fdAllGroupsAll     = s.allGroupsAll      ?? null;
    window._fdAllOwnGroups     = s.allOwnGroups      ?? null;
    window._fdAllMutualGroups  = s.allMutualGroups   ?? null;
    window._fdAllMutuals       = s.allMutuals        ?? null;
    window._fdAllWorlds        = s.allWorlds         ?? null;
    window._fdAllAvatars       = s.allAvatars        ?? [];
    window._fdRepGroup         = s.repGroup          ?? null;
    window._fdGroupsPage       = s.groupsPage        ?? 0;
    window._fdOwnGroupsPage    = s.ownGroupsPage     ?? 0;
    window._fdMutualsPage      = s.mutualsPage       ?? 0;
    window._fdMutualsGroupsPage= s.mutualsGroupsPage ?? 0;
    window._fdWorldsPage       = s.worldsPage        ?? 0;
    window._fdAvatarsPage      = s.avatarsPage       ?? 0;
}
