/* === VRChat API === */
function vrcQuickLogin() {
    const u = document.getElementById('vrcQuickUser').value, p = document.getElementById('vrcQuickPass').value;
    if (!u || !p) return;
    document.getElementById('vrcQuickError').textContent = 'Connecting...';
    sendToCS({ action: 'vrcLogin', username: u, password: p });
}

function vrcLoginFromSettings() {
    const u = document.getElementById('setVrcUser').value, p = document.getElementById('setVrcPass').value;
    if (!u || !p) {
        document.getElementById('vrcLoginStatus').textContent = 'Enter username and password';
        return;
    }
    document.getElementById('vrcLoginStatus').textContent = 'Connecting...';
    sendToCS({ action: 'vrcLogin', username: u, password: p });
}

function show2FAModal(type) {
    vrc2faType = type;
    const m = document.getElementById('modal2FA');
    m.style.display = 'flex';
    document.getElementById('modal2FACode').value = '';
    document.getElementById('modal2FAError').textContent = '';
    const msg = type === 'emailotp' ? 'Enter the 6-digit code sent to your email.' : 'Enter the 6-digit code from your authenticator app.';
    document.getElementById('modal2FAMsg').textContent = msg;
    setTimeout(() => document.getElementById('modal2FACode').focus(), 100);
}

function modal2FASubmit() {
    const c = document.getElementById('modal2FACode').value.trim();
    if (!c || c.length < 6) {
        document.getElementById('modal2FAError').textContent = 'Enter the full 6-digit code';
        return;
    }
    document.getElementById('modal2FAError').textContent = 'Verifying...';
    sendToCS({ action: 'vrc2FA', code: c, type: vrc2faType });
}

function vrcLogout() {
    sendToCS({ action: 'vrcLogout' });
    document.getElementById('btnVrcLogin').style.display = '';
    document.getElementById('btnVrcLogout').style.display = 'none';
    document.getElementById('vrcLoginStatus').textContent = 'Disconnected';
    currentVrcUser = null;
}

function vrcRefresh() {
    sendToCS({ action: 'vrcRefreshFriends' });
    requestInstanceInfo();
    refreshNotifications();
}

function closeDetailModal() { document.getElementById('modalDetail').style.display = 'none'; }

function statusDotClass(s) {
    if (!s) return 's-offline';
    const sl = s.toLowerCase();
    if (sl === 'active' || sl === 'online') return 's-active';
    if (sl === 'join me') return 's-join';
    if (sl === 'ask me' || sl === 'look me') return 's-ask';
    if (sl === 'busy' || sl === 'do not disturb') return 's-busy';
    return 's-offline';
}

function statusLabel(s) {
    if (!s) return 'Offline';
    const sl = s.toLowerCase();
    const m = { 'active': 'Online', 'online': 'Online', 'join me': 'Join Me', 'ask me': 'Ask Me', 'look me': 'Ask Me', 'busy': 'Do Not Disturb', 'do not disturb': 'Do Not Disturb', 'offline': 'Offline' };
    return m[sl] || s;
}

function renderVrcProfile(u) {
    const a = document.getElementById('vrcProfileArea');
    if (!u) { a.innerHTML = ''; currentVrcUser = null; return; }
    currentVrcUser = u;
    // If My Profile modal is open, refresh it immediately
    const _myp = document.getElementById('modalMyProfile');
    if (_myp && _myp.style.display !== 'none') renderMyProfileContent();
    const img = u.image || '';
    const imgTag = img
        ? `<img class="vrc-avatar" src="${img}" onerror="this.style.display='none'">`
        : `<div class="vrc-avatar" style="display:flex;align-items:center;justify-content:center;font-size:13px;font-weight:700;color:var(--tx3)">${esc((u.displayName || '?')[0])}</div>`;
    a.innerHTML = `<div class="vrc-profile" onclick="openMyProfileModal()">${imgTag}<div class="vrc-profile-info"><div class="vrc-profile-name">${esc(u.displayName)}</div><div class="vrc-profile-status"><span class="vrc-status-dot ${statusDotClass(u.status)}"></span>${statusLabel(u.status)}${u.statusDescription ? ' — ' + esc(u.statusDescription) : ''}</div></div><span class="msi" style="font-size:16px;color:var(--tx3);flex-shrink:0;">manage_accounts</span></div>`;
}

// My Profile Modal
function openMyProfileModal() {
    if (!currentVrcUser) return;
    const m = document.getElementById('modalMyProfile');
    if (!m) return;
    renderMyProfileContent();
    m.style.display = 'flex';
}

function closeMyProfile() {
    const m = document.getElementById('modalMyProfile');
    if (m) m.style.display = 'none';
}

function renderMyProfileContent() {
    const u = currentVrcUser;
    const box = document.getElementById('mypBox');
    if (!u || !box) return;

    const bannerSrc = u.profilePicOverride || u.currentAvatarImageUrl || u.image || '';
    const bannerHtml = bannerSrc
        ? `<div class="fd-banner"><img src="${esc(bannerSrc)}" onerror="this.parentElement.style.display='none'"><div class="fd-banner-fade"></div></div>`
        : '';

    const imgTag = u.image
        ? `<img class="myp-avatar" src="${esc(u.image)}" onerror="this.outerHTML='<div class=\\'myp-avatar myp-avatar-fb\\'>${esc((u.displayName||'?')[0])}</div>'">`
        : `<div class="myp-avatar myp-avatar-fb">${esc((u.displayName||'?')[0])}</div>`;

    const langTags = (u.tags||[]).filter(t => t.startsWith('language_'));
    const langsViewHtml = langTags.length
        ? `<div class="fd-lang-tags">${langTags.map(t => `<span class="fd-lang-tag">${esc(LANG_MAP[t]||t.replace('language_','').toUpperCase())}</span>`).join('')}</div>`
        : `<div class="myp-empty">No languages set</div>`;

    const bioLinksViewHtml = (u.bioLinks||[]).length
        ? `<div class="fd-bio-links">${(u.bioLinks).map(bl => renderBioLink(bl)).join('')}</div>`
        : `<div class="myp-empty">No links added</div>`;

    box.innerHTML = `
        ${bannerHtml}
        <div class="fd-content${bannerSrc ? ' fd-has-banner' : ''}">
            <div class="myp-header">
                ${imgTag}
                <div class="myp-header-info">
                    <div class="myp-name">${esc(u.displayName)}</div>
                    <div class="myp-status-row" onclick="openStatusModal()">
                        <span class="vrc-status-dot ${statusDotClass(u.status)}" style="width:7px;height:7px;flex-shrink:0;"></span>
                        <span>${statusLabel(u.status)}${u.statusDescription ? ' — ' + esc(u.statusDescription) : ''}</span>
                        <span class="msi" style="font-size:13px;opacity:.45;margin-left:2px;">edit</span>
                    </div>
                </div>
            </div>

            <div class="myp-section">
                <div class="myp-section-header">
                    <span class="myp-section-title">Pronouns</span>
                    <button class="myp-edit-btn" onclick="editMyField('pronouns')"><span class="msi" style="font-size:14px;">edit</span></button>
                </div>
                <div id="mypPronounsView">
                    ${u.pronouns ? `<div style="font-size:13px;color:var(--tx1);">${esc(u.pronouns)}</div>` : '<div class="myp-empty">No pronouns set</div>'}
                </div>
                <div id="mypPronounsEdit" style="display:none;">
                    <input type="text" id="mypPronounsInput" class="myp-text-input" placeholder="z.B. he/him, she/her, they/them..." maxlength="32" value="${esc(u.pronouns||'')}">
                    <div class="myp-edit-actions">
                        <button class="myp-cancel-btn" onclick="cancelMyField('pronouns')">Cancel</button>
                        <button class="myp-save-btn" onclick="saveMyField('pronouns')">Save</button>
                    </div>
                </div>
            </div>

            <div class="myp-section">
                <div class="myp-section-header">
                    <span class="myp-section-title">Bio</span>
                    <button class="myp-edit-btn" onclick="editMyField('bio')"><span class="msi" style="font-size:14px;">edit</span></button>
                </div>
                <div id="mypBioView">
                    ${u.bio ? `<div class="fd-bio">${esc(u.bio)}</div>` : '<div class="myp-empty">No bio written yet</div>'}
                </div>
                <div id="mypBioEdit" style="display:none;">
                    <textarea id="mypBioInput" class="myp-textarea" rows="4" maxlength="512" placeholder="Write your bio...">${esc(u.bio||'')}</textarea>
                    <div class="myp-char-count"><span id="mypBioCount">${(u.bio||'').length}</span>/512</div>
                    <div class="myp-edit-actions">
                        <button class="myp-cancel-btn" onclick="cancelMyField('bio')">Cancel</button>
                        <button class="myp-save-btn" onclick="saveMyField('bio')">Save</button>
                    </div>
                </div>
            </div>

            <div class="myp-section">
                <div class="myp-section-header">
                    <span class="myp-section-title">Links</span>
                    <button class="myp-edit-btn" onclick="editMyField('links')"><span class="msi" style="font-size:14px;">edit</span></button>
                </div>
                <div id="mypLinksView">${bioLinksViewHtml}</div>
                <div id="mypLinksEdit" style="display:none;">
                    <div id="mypLinksInputs"></div>
                    <div class="myp-edit-actions">
                        <button class="myp-cancel-btn" onclick="cancelMyField('links')">Cancel</button>
                        <button class="myp-save-btn" onclick="saveMyField('links')">Save</button>
                    </div>
                </div>
            </div>

            <div class="myp-section">
                <div class="myp-section-header">
                    <span class="myp-section-title">Languages</span>
                    <button class="myp-edit-btn" onclick="editMyField('languages')"><span class="msi" style="font-size:14px;">edit</span></button>
                </div>
                <div id="mypLangsView">${langsViewHtml}</div>
                <div id="mypLangsEdit" style="display:none;">
                    <div id="mypLangsChips" class="myp-lang-chips"></div>
                    <div class="myp-lang-add-row">
                        <select id="mypLangSelect" class="myp-lang-select"><option value="">Add language...</option></select>
                        <button class="myp-add-lang-btn" onclick="addMyLanguage()"><span class="msi" style="font-size:15px;">add</span></button>
                    </div>
                    <div class="myp-edit-actions">
                        <button class="myp-cancel-btn" onclick="cancelMyField('languages')">Cancel</button>
                        <button class="myp-save-btn" onclick="saveMyField('languages')">Save</button>
                    </div>
                </div>
            </div>

            <div style="text-align:right;padding-top:12px;">
                <button class="modal-btn modal-btn-cancel" onclick="closeMyProfile()">Close</button>
            </div>
        </div>`;

    const bioInput = document.getElementById('mypBioInput');
    if (bioInput) bioInput.oninput = () => {
        const cnt = document.getElementById('mypBioCount');
        if (cnt) cnt.textContent = bioInput.value.length;
    };
}

function editMyField(field) {
    const VIEWS = { pronouns: 'mypPronounsView', bio: 'mypBioView', links: 'mypLinksView', languages: 'mypLangsView' };
    const EDITS = { pronouns: 'mypPronounsEdit', bio: 'mypBioEdit', links: 'mypLinksEdit', languages: 'mypLangsEdit' };
    // Close other open edit panels
    Object.keys(VIEWS).forEach(f => {
        if (f !== field) {
            const v = document.getElementById(VIEWS[f]); if (v) v.style.display = '';
            const e = document.getElementById(EDITS[f]); if (e) e.style.display = 'none';
        }
    });
    const viewEl = document.getElementById(VIEWS[field]);
    const editEl = document.getElementById(EDITS[field]);
    if (viewEl) viewEl.style.display = 'none';
    if (editEl) editEl.style.display = '';

    if (field === 'pronouns') {
        const inp = document.getElementById('mypPronounsInput');
        if (inp) { inp.value = currentVrcUser.pronouns || ''; inp.focus(); }
    } else if (field === 'bio') {
        const inp = document.getElementById('mypBioInput');
        if (inp) { inp.focus(); const cnt = document.getElementById('mypBioCount'); if (cnt) cnt.textContent = inp.value.length; }
    } else if (field === 'links') {
        _renderMyLinksInputs();
    } else if (field === 'languages') {
        _renderMyLangsEdit();
    }
}

function cancelMyField(field) {
    const VIEWS = { pronouns: 'mypPronounsView', bio: 'mypBioView', links: 'mypLinksView', languages: 'mypLangsView' };
    const EDITS = { pronouns: 'mypPronounsEdit', bio: 'mypBioEdit', links: 'mypLinksEdit', languages: 'mypLangsEdit' };
    const v = document.getElementById(VIEWS[field]); if (v) v.style.display = '';
    const e = document.getElementById(EDITS[field]); if (e) e.style.display = 'none';
}

function saveMyField(field) {
    const u = currentVrcUser;
    if (!u) return;
    const EDITS = { pronouns: 'mypPronounsEdit', bio: 'mypBioEdit', links: 'mypLinksEdit', languages: 'mypLangsEdit' };
    const saveBtn = document.querySelector(`#${EDITS[field]} .myp-save-btn`);
    if (saveBtn) saveBtn.disabled = true;

    if (field === 'pronouns') {
        const pronouns = document.getElementById('mypPronounsInput')?.value ?? '';
        sendToCS({ action: 'vrcUpdateProfile', pronouns });
    } else if (field === 'bio') {
        const bio = document.getElementById('mypBioInput')?.value ?? '';
        sendToCS({ action: 'vrcUpdateProfile', bio });
    } else if (field === 'links') {
        const inputs = document.querySelectorAll('#mypLinksInputs .myp-link-input');
        const bioLinks = Array.from(inputs).map(i => i.value.trim()).filter(Boolean).slice(0, 3);
        sendToCS({ action: 'vrcUpdateProfile', bioLinks });
    } else if (field === 'languages') {
        const chips = document.querySelectorAll('#mypLangsChips [data-lang]');
        const selectedLangs = Array.from(chips).map(c => c.dataset.lang);
        const nonLangTags = (u.tags||[]).filter(t => !t.startsWith('language_'));
        sendToCS({ action: 'vrcUpdateProfile', tags: [...nonLangTags, ...selectedLangs] });
    }
}

function _renderMyLinksInputs() {
    const container = document.getElementById('mypLinksInputs');
    if (!container) return;
    const links = currentVrcUser.bioLinks || [];
    container.innerHTML = [0, 1, 2].map(i =>
        `<div class="myp-link-row">
            <span class="myp-link-num">${i + 1}</span>
            <input type="url" class="myp-link-input" placeholder="https://..." value="${esc(links[i]||'')}" maxlength="512">
        </div>`
    ).join('');
}

function _renderMyLangsEdit() {
    const selectedLangs = (currentVrcUser.tags||[]).filter(t => t.startsWith('language_'));
    _renderMyLangChips(selectedLangs, document.getElementById('mypLangsChips'));
    const sel = document.getElementById('mypLangSelect');
    if (!sel) return;
    sel.innerHTML = '<option value="">Add language...</option>';
    Object.entries(LANG_MAP).forEach(([key, name]) => {
        if (!selectedLangs.includes(key))
            sel.insertAdjacentHTML('beforeend', `<option value="${key}">${esc(name)}</option>`);
    });
}

function _renderMyLangChips(langs, el) {
    if (!el) return;
    el.innerHTML = langs.map(tag =>
        `<span class="myp-lang-chip" data-lang="${tag}">${esc(LANG_MAP[tag]||tag.replace('language_','').toUpperCase())}<button class="myp-lang-remove" onclick="removeMyLanguage('${tag}')"><span class="msi" style="font-size:11px;">close</span></button></span>`
    ).join('');
}

function addMyLanguage() {
    const sel = document.getElementById('mypLangSelect');
    const key = sel?.value;
    if (!key) return;
    const chips = Array.from(document.querySelectorAll('#mypLangsChips [data-lang]')).map(c => c.dataset.lang);
    if (chips.includes(key)) return;
    chips.push(key);
    _renderMyLangChips(chips, document.getElementById('mypLangsChips'));
    const opt = sel.querySelector(`option[value="${key}"]`);
    if (opt) opt.remove();
    sel.value = '';
}

function removeMyLanguage(tag) {
    const chips = Array.from(document.querySelectorAll('#mypLangsChips [data-lang]')).map(c => c.dataset.lang).filter(t => t !== tag);
    _renderMyLangChips(chips, document.getElementById('mypLangsChips'));
    const sel = document.getElementById('mypLangSelect');
    if (sel) sel.insertAdjacentHTML('beforeend', `<option value="${tag}">${esc(LANG_MAP[tag]||tag.replace('language_','').toUpperCase())}</option>`);
}

function showMypToast(success, msg) {
    const box = document.getElementById('mypBox');
    if (!box) return;
    const ex = box.querySelector('.myp-toast');
    if (ex) ex.remove();
    const t = document.createElement('div');
    t.className = 'myp-toast ' + (success ? 'ok' : 'err');
    t.textContent = msg;
    box.appendChild(t);
    setTimeout(() => { if (t.parentNode) t.remove(); }, 3000);
}

function renderVrcFriends(friends, counts) {
    const el = document.getElementById('vrcFriendsList');
    const lp = document.getElementById('vrcLoginPrompt');
    if (lp) lp.style.display = 'none';
    vrcFriendsData = friends || [];

    // Show/hide search bar
    const searchBar = document.getElementById('vrcFriendSearch');
    if (searchBar) searchBar.style.display = vrcFriendsData.length > 0 ? '' : 'none';

    if (!friends || !friends.length) {
        el.innerHTML = '<div class="vrc-section-label">ONLINE — 0</div><div style="padding:16px;text-align:center;font-size:12px;color:var(--tx3);">No friends online</div>';
        return;
    }

    const gameFriends = friends.filter(f => f.presence === 'game');
    const webFriends = friends.filter(f => f.presence === 'web');
    const offlineFriends = friends.filter(f => f.presence === 'offline');

    const gc = counts ? counts.game : gameFriends.length;
    const wc = counts ? counts.web : webFriends.length;
    const oc = counts ? counts.offline : offlineFriends.length;

    let h = '';

    function renderCard(f, presenceType) {
        const img = f.image || '';
        const imgTag = img
            ? `<img class="vrc-friend-avatar" src="${img}" onerror="this.style.display='none'">`
            : `<div class="vrc-friend-avatar" style="display:flex;align-items:center;justify-content:center;font-size:12px;font-weight:700;color:var(--tx3)">${esc((f.displayName || '?')[0])}</div>`;
        const isPrivate = !f.location || f.location === 'private';
        const isOffline = f.location === 'offline' || f.presence === 'offline';
        const loc = isOffline ? 'Offline' : (presenceType === 'web' ? 'Web / Mobile' : (isPrivate ? 'Private Instance' : 'In World'));
        const fid = (f.id || '').replace(/'/g, "\\'");
        const dotClass = presenceType === 'web' ? 'vrc-status-ring' : 'vrc-status-dot';
        const statusCls = presenceType === 'offline' ? 's-offline' : statusDotClass(f.status);
        return `<div class="vrc-friend-card" onclick="openFriendDetail('${fid}')">${imgTag}<div class="vrc-friend-info"><div class="vrc-friend-name"><span class="${dotClass} ${statusCls}" style="width:6px;height:6px;"></span>${esc(f.displayName)}</div><div class="vrc-friend-loc">${esc(f.statusDescription || statusLabel(f.status))} · ${esc(loc)}</div></div></div>`;
    }

    if (gameFriends.length > 0) {
        h += `<div class="vrc-section-label">IN-GAME — ${gc}</div>`;
        gameFriends.forEach(f => { h += renderCard(f, 'game'); });
    }

    if (webFriends.length > 0) {
        h += `<div class="vrc-section-label">WEB / ACTIVE — ${wc}</div>`;
        webFriends.forEach(f => { h += renderCard(f, 'web'); });
    }

    if (offlineFriends.length > 0) {
        h += `<div class="vrc-section-label vrc-offline-toggle" onclick="toggleOfflineFriends()" style="cursor:pointer;">OFFLINE — ${oc} <span class="msi" style="font-size:14px;vertical-align:middle;" id="offlineChevron">expand_more</span></div>`;
        h += `<div id="offlineFriendsSection" style="display:none;">`;
        offlineFriends.forEach(f => { h += renderCard(f, 'offline'); });
        h += `</div>`;
    }

    el.innerHTML = h;

    // Re-apply active search filter so live updates don't reset the search bar
    filterFriendsList();

    // Refresh instance info whenever friends update, keeps "who's in your instance" current
    requestInstanceInfo();
}

function toggleOfflineFriends() {
    const sec = document.getElementById('offlineFriendsSection');
    const chev = document.getElementById('offlineChevron');
    if (!sec) return;
    if (sec.style.display === 'none') {
        sec.style.display = '';
        if (chev) chev.textContent = 'expand_less';
    } else {
        sec.style.display = 'none';
        if (chev) chev.textContent = 'expand_more';
    }
}

function filterFriendsList() {
    const q = (document.getElementById('vrcFriendSearchInput')?.value || '').toLowerCase().trim();
    const cards = document.querySelectorAll('#vrcFriendsList .vrc-friend-card');
    const sections = document.querySelectorAll('#vrcFriendsList .vrc-section-label');

    if (!q) {
        // Reset: show all, restore offline collapsed state
        cards.forEach(c => c.style.display = '');
        sections.forEach(s => s.style.display = '');
        const offSec = document.getElementById('offlineFriendsSection');
        if (offSec) offSec.style.display = 'none';
        const chev = document.getElementById('offlineChevron');
        if (chev) chev.textContent = 'expand_more';
        return;
    }

    // Show offline section while searching
    const offSec = document.getElementById('offlineFriendsSection');
    if (offSec) offSec.style.display = '';

    cards.forEach(c => {
        const name = (c.querySelector('.vrc-friend-name')?.textContent || '').toLowerCase();
        c.style.display = name.includes(q) ? '' : 'none';
    });

    // Hide section labels if all their cards are hidden
    sections.forEach(s => {
        let next = s.nextElementSibling;
        // For offline, the next is the wrapper div
        if (next && next.id === 'offlineFriendsSection') next = next;
        let hasVisible = false;
        let sibling = s.nextElementSibling;
        while (sibling && !sibling.classList.contains('vrc-section-label')) {
            if (sibling.classList.contains('vrc-friend-card') && sibling.style.display !== 'none') hasVisible = true;
            // Check inside offline wrapper
            if (sibling.id === 'offlineFriendsSection') {
                sibling.querySelectorAll('.vrc-friend-card').forEach(c => {
                    if (c.style.display !== 'none') hasVisible = true;
                });
            }
            sibling = sibling.nextElementSibling;
        }
        s.style.display = hasVisible ? '' : 'none';
    });
}

function openStatusModal() {
    if (!currentVrcUser) return;
    selectedStatus = currentVrcUser.status || 'active';
    const m = document.getElementById('modalStatus');
    const opts = document.getElementById('statusOptions');
    opts.innerHTML = STATUS_LIST.map(s =>
        `<div class="status-option${selectedStatus === s.key ? ' selected' : ''}" onclick="selectStatusOption('${s.key}')"><div class="status-option-dot" style="background:${s.color}"></div><div><div class="status-option-label">${s.label}</div><div class="status-option-desc">${s.desc}</div></div></div>`
    ).join('');
    const inp = document.getElementById('statusDescInput');
    inp.value = currentVrcUser.statusDescription || '';
    document.getElementById('statusDescCount').textContent = (inp.value.length) + '/64';
    inp.oninput = () => {
        document.getElementById('statusDescCount').textContent = inp.value.length + '/64';
    };
    m.style.display = 'flex';
    setTimeout(() => inp.focus(), 100);
}

function selectStatusOption(key) {
    selectedStatus = key;
    document.querySelectorAll('.status-option').forEach(el => {
        el.classList.toggle('selected', el.querySelector('.status-option-label').textContent === STATUS_LIST.find(s => s.key === key)?.label);
    });
}

function submitStatusChange() {
    const desc = document.getElementById('statusDescInput').value.trim();
    sendToCS({ action: 'vrcUpdateStatus', status: selectedStatus, statusDescription: desc });
    document.getElementById('modalStatus').style.display = 'none';
}

// Profile helpers
function formatDuration(totalSec) {
    if (totalSec < 60) return `${totalSec}s`;
    const h = Math.floor(totalSec / 3600);
    const m = Math.floor((totalSec % 3600) / 60);
    const s = totalSec % 60;
    if (h >= 24) { const d = Math.floor(h/24); const rh = h%24; return rh > 0 ? `${d}d ${rh}h` : `${d}d`; }
    if (h > 0) return `${h}h ${m}m ${s}s`;
    return `${m}m ${s}s`;
}

function formatLastSeen(apiLastLogin, localLastSeen) {
    let best = null;
    if (apiLastLogin) {
        const d = new Date(apiLastLogin);
        if (!isNaN(d)) best = d;
    }
    if (localLastSeen) {
        const d = new Date(localLastSeen);
        if (!isNaN(d) && (!best || d > best)) best = d;
    }
    if (!best) return '';
    const now = new Date();
    const diff = now - best;
    if (diff < 60000) return 'Just now';
    if (diff < 3600000) return `${Math.floor(diff/60000)}m ago`;
    if (diff < 86400000) return `${Math.floor(diff/3600000)}h ago`;
    if (diff < 604800000) return `${Math.floor(diff/86400000)}d ago`;
    return best.toLocaleDateString();
}

let _noteSaveTimer = null;
function debounceSaveNote(userId) {
    const saved = document.getElementById('fdNoteSaved');
    if (saved) { saved.textContent = ''; }
    clearTimeout(_noteSaveTimer);
    _noteSaveTimer = setTimeout(() => saveUserNote(userId), 1200);
}

function saveUserNote(userId) {
    const input = document.getElementById('fdNoteInput');
    const saved = document.getElementById('fdNoteSaved');
    if (!input) return;
    const note = input.value;
    sendToCS({ action: 'vrcUpdateNote', userId, note });
    if (saved) { saved.textContent = 'Saving...'; saved.style.color = 'var(--tx3)'; }
}

function openFriendDetail(userId) {
    const m = document.getElementById('modalFriendDetail');
    const c = document.getElementById('friendDetailContent');
    c.innerHTML = sk('profile');
    m.style.display = 'flex';
    sendToCS({ action: 'vrcGetFriendDetail', userId: userId });
    // Refresh timeline so world names are resolved and instance players are current
    sendToCS({ action: 'getTimeline' });
}

function closeFriendDetail() {
    if (_fdLiveTimer) { clearInterval(_fdLiveTimer); _fdLiveTimer = null; }
    document.getElementById('modalFriendDetail').style.display = 'none';
    currentFriendDetail = null;
}

function switchFdTab(tab, btn) {
    document.getElementById('fdTabInfo').style.display = tab === 'info' ? '' : 'none';
    document.getElementById('fdTabGroups').style.display = tab === 'groups' ? '' : 'none';
    const mutualsEl = document.getElementById('fdTabMutuals');
    if (mutualsEl) mutualsEl.style.display = tab === 'mutuals' ? '' : 'none';
    document.querySelectorAll('.fd-tab').forEach(t => t.classList.remove('active'));
    if (btn) btn.classList.add('active');
}

// Trust rank from tags (offset by 1 in API naming)
function getTrustRank(tags) {
    if (!tags || !tags.length) return null;
    // Order matters: check highest first
    if (tags.includes('system_trust_legend')) return { label: 'Trusted User', color: '#8143E6' };
    if (tags.includes('system_trust_veteran')) return { label: 'Trusted User', color: '#8143E6' };
    if (tags.includes('system_trust_trusted')) return { label: 'Known User', color: '#FF7B42' };
    if (tags.includes('system_trust_known'))   return { label: 'User', color: '#2BCF5C' };
    if (tags.includes('system_trust_basic'))   return { label: 'New User', color: '#1778FF' };
    return { label: 'Visitor', color: '#CCCCCC' };
}

function getLanguages(tags) {
    if (!tags) return [];
    return tags.filter(t => t.startsWith('language_')).map(t => LANG_MAP[t] || t.replace('language_','').toUpperCase());
}

function getPlatformInfo(hostname) {
    const h = hostname.replace('www.', '');
    if (h.includes('twitter.com') || h.includes('x.com'))          return { key: 'twitter',   label: 'Twitter/X' };
    if (h.includes('instagram.com'))                                 return { key: 'instagram', label: 'Instagram' };
    if (h.includes('tiktok.com'))                                    return { key: 'tiktok',    label: 'TikTok' };
    if (h.includes('youtube.com') || h.includes('youtu.be'))        return { key: 'youtube',   label: 'YouTube' };
    if (h.includes('discord.gg') || h.includes('discord.com'))      return { key: 'discord',   label: 'Discord' };
    if (h.includes('github.com'))                                    return { key: 'github',    label: 'GitHub' };
    if (h.includes('facebook.com') || h.includes('fb.com'))         return { key: 'facebook',  label: 'Facebook' };
    if (h.includes('twitch.tv'))                                     return { key: 'twitch',    label: 'Twitch' };
    if (h.includes('bsky.app'))                                      return { key: 'bluesky',   label: 'Bluesky' };
    if (h.includes('pixiv.net'))                                     return { key: 'pixiv',     label: 'Pixiv' };
    if (h.includes('ko-fi.com'))                                     return { key: 'kofi',      label: 'Ko-fi' };
    if (h.includes('patreon.com'))                                   return { key: 'patreon',   label: 'Patreon' };
    if (h.includes('booth.pm'))                                      return { key: 'booth',     label: 'Booth' };
    if (h.includes('vrchat.com') || h.includes('vrc.group'))        return { key: 'vrchat',    label: 'VRChat' };
    return { key: null, label: h };
}

// Bio link to SVG brand icon and label
function renderBioLink(url) {
    let platformSvg = '';
    let label = 'Link';
    try {
        const h = new URL(url).hostname;
        const info = getPlatformInfo(h);
        label = info.label;
        if (info.key && PLATFORM_ICONS[info.key]) {
            platformSvg = `<svg viewBox="0 0 24 24" width="14" height="14" fill="currentColor" style="flex-shrink:0"><path d="${PLATFORM_ICONS[info.key].svg}"/></svg>`;
        } else {
            platformSvg = `<span class="msi" style="font-size:14px;">link</span>`;
        }
    } catch {
        label = 'Link';
        platformSvg = `<span class="msi" style="font-size:14px;">link</span>`;
    }
    const safeUrl = esc(url);
    const safeUrlJs = jsq(url);
    return `<button class="fd-bio-link" onclick="sendToCS({action:'openUrl',url:'${safeUrlJs}'})" title="${safeUrl}">${platformSvg}<span>${esc(label)}</span></button>`;
}

// Timeline preview list for friend detail modal
function buildFdTimelinePreview(userId) {
    // Always return a placeholder so updateFdTlPreview() can find the element later
    if (!Array.isArray(timelineEvents) || !userId) return '<div id="fdTlPreview"></div>';

    let evs = timelineEvents.filter(ev => {
        if (ev.type === 'first_meet' || ev.type === 'meet_again')
            return ev.userId === userId;
        if (ev.type === 'photo')
            return (ev.players || []).some(p => p.userId === userId);
        return false;
    });
    // Show section only if we've ever seen this person (first_meet OR meet_again)
    if (!evs.some(ev => ev.type === 'first_meet' || ev.type === 'meet_again'))
        return '<div id="fdTlPreview"></div>';

    evs.sort((a, b) => (b.timestamp || '').localeCompare(a.timestamp || ''));  // newest first
    if (evs.length > 10) evs = evs.slice(0, 10);

    const items = evs.map(ev => {
        const meta  = TL_TYPE_META[ev.type]  || { icon: 'event', label: ev.type };
        const color = TL_TYPE_COLOR[ev.type] || 'var(--tx3)';
        const d    = ev.timestamp ? new Date(ev.timestamp) : null;
        const dateStr = d ? d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' }) : '';
        const timeStr = d ? d.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' }) : '';

        let imgSrc = '';
        if (ev.type === 'photo' && ev.photoUrl) {
            imgSrc = ev.photoUrl;
        } else {
            // For all event types (including first_meet / meet_again): show world thumbnail
            imgSrc = ev.worldThumb || '';
        }

        const imgHtml = imgSrc
            ? `<img class="fdtl-avatar" src="${esc(imgSrc)}" onerror="this.style.display='none'">`
            : `<div class="fdtl-avatar fdtl-avatar-empty"><span class="msi" style="font-size:16px;">${meta.icon}</span></div>`;

        const loc = ev.type === 'photo'
            ? (ev.photoPath ? ev.photoPath.split(/[\\/]/).pop() : 'Photo')
            : (ev.worldName || '');

        return `<div class="fdtl-item">
            ${imgHtml}
            <div class="fdtl-item-info">
                <div class="fdtl-badge" style="background:${color}22;color:${color};"><span class="msi" style="font-size:9px;vertical-align:middle;">${meta.icon}</span> ${esc(meta.label)}</div>
                ${loc ? `<div class="fdtl-item-loc">${esc(loc)}</div>` : ''}
                <div class="fdtl-item-date">${esc(dateStr)}${timeStr ? ' · ' + timeStr : ''}</div>
            </div>
        </div>`;
    });

    return `<div id="fdTlPreview" class="fd-tl-section">
        <div class="fd-meta-label" style="margin-bottom:8px;">Timeline</div>
        <div class="fdtl-list">${items.join('')}</div>
    </div>`;
}

// Called by renderTimeline / handleTimelineEvent if the friend detail modal is open
function updateFdTlPreview() {
    const el = document.getElementById('fdTlPreview');
    if (!el || !currentFriendDetail) return;
    el.outerHTML = buildFdTimelinePreview(currentFriendDetail.id || '');
}

function renderFriendDetail(d) {
    currentFriendDetail = d;
    const c = document.getElementById('friendDetailContent');
    const img = d.image || '';
    const imgTag = img
        ? `<img class="fd-avatar" src="${img}" onerror="this.style.display='none'">`
        : `<div class="fd-avatar" style="display:flex;align-items:center;justify-content:center;font-size:20px;font-weight:700;color:var(--tx3)">${esc((d.displayName || '?')[0])}</div>`;

    let worldHtml = '';
    if (d.worldName) {
        const thumbTag = d.worldThumb ? `<img class="fd-world-thumb" src="${d.worldThumb}" onerror="this.style.display='none'">` : '';
        const { cls: instClass, label: instLabel } = getInstanceBadge(d.instanceType);
        const users = d.userCount > 0 ? (d.worldCapacity > 0 ? `${d.userCount}/${d.worldCapacity}` : d.userCount + ' users') : '';
        const { worldId: fdWorldId } = parseFriendLocation(d.location);
        const clickAttr = fdWorldId ? ` onclick="openWorldSearchDetail('${esc(fdWorldId)}')" style="cursor:pointer;"` : '';
        worldHtml = `<div class="fd-world-card"${clickAttr}>${thumbTag}<div class="fd-world-info"><div class="fd-world-name">${esc(d.worldName)}</div><div class="fd-world-meta">${users ? esc(users) : ''}</div><span class="fd-instance-badge ${instClass}">${instLabel}</span></div></div>`;
    } else if (d.location === 'private') {
        worldHtml = `<div style="padding:12px;background:var(--bg-input);border-radius:10px;margin-bottom:14px;font-size:12px;color:var(--tx3);text-align:center;">Private Instance</div>`;
    } else if (d.location === 'traveling') {
        worldHtml = `<div style="padding:12px;background:var(--bg-input);border-radius:10px;margin-bottom:14px;font-size:12px;color:var(--tx3);text-align:center;">Traveling...</div>`;
    } else if (d.location === 'offline') {
        worldHtml = `<div style="padding:12px;background:var(--bg-input);border-radius:10px;margin-bottom:14px;font-size:12px;color:var(--tx3);text-align:center;">Offline</div>`;
    }

    const bioHtml = d.bio ? `<div class="fd-bio">${esc(d.bio)}</div>` : '';

    // Bio links (profile links from VRChat API)
    let bioLinksHtml = '';
    if (d.bioLinks && d.bioLinks.length) {
        bioLinksHtml = `<div class="fd-bio-links">${d.bioLinks.map(u => renderBioLink(u)).join('')}</div>`;
    }

    let metaHtml = '';
    if (d.lastPlatform) metaHtml += `<div class="fd-meta-row"><span class="fd-meta-label">Platform</span><span>${esc(d.lastPlatform)}</span></div>`;
    if (d.dateJoined) metaHtml += `<div class="fd-meta-row"><span class="fd-meta-label">Joined</span><span>${esc(d.dateJoined)}</span></div>`;
    const lastSeenStr = formatLastSeen(d.lastLogin, d.lastSeenTracked);
    if (lastSeenStr) metaHtml += `<div class="fd-meta-row"><span class="fd-meta-label">Last Seen</span><span>${esc(lastSeenStr)}</span></div>`;
    if (d.totalTimeSeconds > 0 || d.inSameInstance) {
        metaHtml += `<div class="fd-meta-row"><span class="fd-meta-label">Time Together</span><span id="fdTimeTogether">${formatDuration(d.totalTimeSeconds)}</span></div>`;
    } else {
        metaHtml += `<div class="fd-meta-row"><span class="fd-meta-label">Time Together</span><span style="color:var(--tx3)">Not tracked yet</span></div>`;
    }

    const noteVal = (d.userNote || '').replace(/"/g, '&quot;').replace(/</g, '&lt;');
    const noteHtml = `<div class="fd-note-section">
        <div class="fd-meta-label" style="margin-bottom:6px;display:flex;align-items:center;gap:6px;"><span class="msi" style="font-size:14px;">edit_note</span>Note</div>
        <textarea id="fdNoteInput" class="fd-note-input" placeholder="Write a note about this user..." rows="2" oninput="debounceSaveNote('${(d.id||'').replace(/'/g,"\\'")}')">${noteVal}</textarea>
        <div class="fd-note-actions"><span id="fdNoteSaved" class="fd-note-saved"></span></div>
    </div>`;

    if (d.note) metaHtml += `<div class="fd-meta-row"><span class="fd-meta-label">VRC Note</span><span style="color:var(--tx3);font-style:italic">${esc(d.note)}</span></div>`;

    // Actions
    let actionsHtml = '<div class="fd-actions">';
    const loc = (d.location || '').replace(/'/g, "\\'");
    const uid = (d.id || '').replace(/'/g, "\\'");
    if (d.isFriend) {
        if (d.canJoin) actionsHtml += `<button class="fd-btn fd-btn-join" onclick="friendAction('join','${loc}','${uid}')">Join</button>`;
        if (d.canRequestInvite) actionsHtml += `<button class="fd-btn" onclick="friendAction('requestInvite','${loc}','${uid}')">Request Invite</button>`;
        const myInInstance = currentInstanceData && currentInstanceData.location && !currentInstanceData.empty && !currentInstanceData.error;
        if (myInInstance) actionsHtml += `<button class="fd-btn" onclick="friendAction('invite','${loc}','${uid}')">Invite Here</button>`;
        actionsHtml += `<button class="fd-btn fd-btn-danger" id="fdUnfriend" onclick="confirmUnfriend('${uid}','${esc(d.displayName).replace(/'/g, "\\'")}')">Unfriend</button>`;
        const favFid = (d.favFriendId || '').replace(/'/g, "\\'");
        actionsHtml += `<button class="fd-btn fd-btn-fav${d.isFavorited ? ' active' : ''}" id="fdFavBtn" onclick="toggleFavFriend('${uid}','${favFid}',this)"><span class="msi" style="font-size:16px;">${d.isFavorited ? 'star' : 'star_outline'}</span>${d.isFavorited ? 'Unfavorite' : 'Favorite'}</button>`;
    } else {
        actionsHtml += `<button class="fd-btn fd-btn-accent" id="fdAddFriend" onclick="sendToCS({action:'vrcSendFriendRequest',userId:'${uid}'});this.disabled=true;this.textContent='Request Sent';">Add Friend</button>`;
    }
    actionsHtml += '</div>';

    // Badges
    let badgesHtml = '<div class="fd-badges-row">';
    if (d.isFriend) badgesHtml += `<span class="fd-badge fd-badge-friend"><span class="msi" style="font-size:11px;">check_circle</span>Friend</span>`;
    if (d.ageVerified) badgesHtml += `<span class="fd-badge fd-badge-verified"><span class="msi" style="font-size:11px;">verified</span>18+</span>`;
    const rank = getTrustRank(d.tags || []);
    if (rank) badgesHtml += `<span class="fd-badge" style="background:${rank.color}22;color:${rank.color}">${esc(rank.label)}</span>`;
    const mutualCount = (d.mutuals || []).length;
    if (mutualCount > 0) badgesHtml += `<span class="fd-badge fd-badge-mutual"><span class="msi" style="font-size:11px;">group</span>${mutualCount} Mutual${mutualCount !== 1 ? 's' : ''}</span>`;
    badgesHtml += '</div>';

    const pronounsHtml = d.pronouns ? `<div class="fd-pronouns">${esc(d.pronouns)}</div>` : '';
    const langs = getLanguages(d.tags || []);
    const langsHtml = langs.length ? `<div class="fd-lang-tags">${langs.map(l => `<span class="fd-lang-tag">${esc(l)}</span>`).join('')}</div>` : '';

    // Groups data
    const allGroups = d.userGroups || [];
    let repG = d.representedGroup;
    // Fallback: find representing group from userGroups list
    if (!repG && allGroups.length > 0) {
        const repFromList = allGroups.find(g => g.isRepresenting);
        if (repFromList) repG = repFromList;
    }

    // Represented group card for Info tab (above bio)
    let repGroupInfoHtml = '';
    if (repG && repG.id) {
        const repIcon = repG.iconUrl ? `<img class="fd-group-icon" src="${repG.iconUrl}" onerror="this.style.display='none'">` : `<div class="fd-group-icon fd-group-icon-empty"><span class="msi" style="font-size:18px;">group</span></div>`;
        repGroupInfoHtml = `<div class="fd-group-rep-label">Representing</div><div class="fd-group-card fd-group-rep" onclick="closeFriendDetail();openGroupDetail('${esc(repG.id)}')">
            ${repIcon}<div class="fd-group-card-info"><div class="fd-group-card-name">${esc(repG.name)}</div><div class="fd-group-card-meta">${esc(repG.shortCode || '')}${repG.discriminator ? '.' + esc(repG.discriminator) : ''} · ${repG.memberCount ? repG.memberCount + ' members' : 'Group'}</div></div>
        </div>`;
    }

    // Groups tab content
    let groupsContent = '';

    if (repG && repG.id) {
        const repIcon = repG.iconUrl ? `<img class="fd-group-icon" src="${repG.iconUrl}" onerror="this.style.display='none'">` : `<div class="fd-group-icon fd-group-icon-empty"><span class="msi" style="font-size:18px;">group</span></div>`;
        groupsContent += `<div class="fd-group-rep-label">Representing</div>
        <div class="fd-group-card fd-group-rep" onclick="closeFriendDetail();openGroupDetail('${esc(repG.id)}')">
            ${repIcon}<div class="fd-group-card-info"><div class="fd-group-card-name">${esc(repG.name)}</div><div class="fd-group-card-meta">${esc(repG.shortCode || '')}${repG.discriminator ? '.' + esc(repG.discriminator) : ''} · ${repG.memberCount ? repG.memberCount + ' members' : ''}</div></div>
        </div>`;
    }

    if (allGroups.length > 0) {
        const otherGroups = repG ? allGroups.filter(g => g.id !== repG.id) : allGroups;
        if (otherGroups.length > 0) {
            groupsContent += `<div class="fd-group-rep-label" style="margin-top:${repG && repG.id ? '14' : '0'}px;">Groups</div>`;
            otherGroups.forEach(g => {
                const gIcon = g.iconUrl ? `<img class="fd-group-icon" src="${g.iconUrl}" onerror="this.style.display='none'">` : `<div class="fd-group-icon fd-group-icon-empty"><span class="msi" style="font-size:18px;">group</span></div>`;
                groupsContent += `<div class="fd-group-card" onclick="closeFriendDetail();openGroupDetail('${esc(g.id)}')">
                    ${gIcon}<div class="fd-group-card-info"><div class="fd-group-card-name">${esc(g.name)}</div><div class="fd-group-card-meta">${g.memberCount ? g.memberCount + ' members' : ''}</div></div>
                </div>`;
            });
        }
    }

    if (!groupsContent) groupsContent = '<div style="padding:20px;text-align:center;font-size:12px;color:var(--tx3);">No groups</div>';

    // Mutuals tab content
    const allMutuals = d.mutuals || [];
    let mutualsContent = '';
    if (d.mutualsOptedOut) {
        mutualsContent = `<div style="padding:24px 16px;text-align:center;font-size:12px;color:var(--tx3);">
            <span class="msi" style="font-size:28px;display:block;margin-bottom:8px;opacity:.5;">visibility_off</span>
            This user has disabled Shared Connections.
        </div>`;
    } else if (allMutuals.length === 0) {
        mutualsContent = `<div style="padding:24px 16px;text-align:center;font-size:12px;color:var(--tx3);">
            <span class="msi" style="font-size:28px;display:block;margin-bottom:8px;opacity:.5;">group_off</span>
            No mutual friends found.<br>
            <span style="font-size:10px;margin-top:6px;display:block;line-height:1.5;">
                Requires VRChat's "Shared Connections" feature to be active on both accounts.
            </span>
        </div>`;
    } else {
        allMutuals.forEach(mu => {
            const muImg = mu.image
                ? `<img class="fd-mutual-avatar" src="${esc(mu.image)}" onerror="this.outerHTML='<div class=\\'fd-mutual-avatar\\' style=\\'display:flex;align-items:center;justify-content:center;font-size:14px;font-weight:700;color:var(--tx3)\\'>${esc((mu.displayName||'?')[0])}</div>'">`
                : `<div class="fd-mutual-avatar" style="display:flex;align-items:center;justify-content:center;font-size:14px;font-weight:700;color:var(--tx3)">${esc((mu.displayName||'?')[0])}</div>`;
            const muUid = jsq(mu.id || '');
            // Cross-reference vrcFriendsData because the mutuals endpoint doesn't expose location reliably
            const _muFriend = vrcFriendsData.find(f => f.id === mu.id);
            const _muPresence = _muFriend ? _muFriend.presence : (mu.presence || 'offline');
            const muIndicator = _muPresence === 'web' ? 'vrc-status-ring' : 'vrc-status-dot';
            mutualsContent += `<div class="fd-mutual-card" onclick="closeFriendDetail();openFriendDetail('${muUid}')">
                ${muImg}<div class="fd-mutual-info"><div class="fd-mutual-name">${esc(mu.displayName)}</div><div class="fd-mutual-status"><span class="${muIndicator} ${statusDotClass(mu.status)}" style="width:6px;height:6px;flex-shrink:0;"></span>${statusLabel(mu.status)}${mu.statusDescription ? ' — ' + esc(mu.statusDescription) : ''}</div></div>
            </div>`;
        });
    }

    // Info tab content
    const tlPreviewHtml = buildFdTimelinePreview(d.id || '');
    const infoContent = `${repGroupInfoHtml}${bioHtml}${bioLinksHtml}${langsHtml}${worldHtml}${metaHtml ? '<div style="margin-bottom:14px;">' + metaHtml + '</div>' : ''}${noteHtml}${tlPreviewHtml}`;

    // Banner
    const bannerSrc = d.profilePicOverride || d.currentAvatarImageUrl || d.image || '';
    const bannerHtml = bannerSrc
        ? `<div class="fd-banner"><img src="${bannerSrc}" onerror="this.parentElement.style.display='none'"><div class="fd-banner-fade"></div></div>`
        : '';

    // Presence
    const fdLocation = d.location || '';
    // "private" / "traveling" = in-game with hidden or transitioning instance, not web.
    // d.state === 'active' is the reliable web indicator (null = in-game, 'active' = web/mobile).
    const fdIsInGame = fdLocation && fdLocation !== 'offline' && fdLocation !== '' && d.state !== 'active';
    const fdIsWeb = !fdIsInGame && (d.state === 'active' || (d.platform || '').toLowerCase() === 'web') && d.status !== 'offline';
    const fdDotClass = fdIsWeb ? 'vrc-status-ring' : 'vrc-status-dot';

    const hasGroups = allGroups.length > 0 || repG;
    const hasMutuals = d.mutuals !== undefined;
    const hasTabs = hasGroups || hasMutuals;

    let tabsHtml = '';
    if (hasTabs) {
        tabsHtml = `<div class="fd-tabs"><button class="fd-tab active" onclick="switchFdTab('info',this)">Info</button>`;
        if (hasGroups) tabsHtml += `<button class="fd-tab" onclick="switchFdTab('groups',this)">Groups${allGroups.length ? ' (' + allGroups.length + ')' : ''}</button>`;
        if (hasMutuals) tabsHtml += `<button class="fd-tab" onclick="switchFdTab('mutuals',this)">Mutuals${allMutuals.length ? ' (' + allMutuals.length + ')' : ''}</button>`;
        tabsHtml += `</div>`;
    }

    c.innerHTML = `${bannerHtml}<div class="fd-content${bannerSrc ? ' fd-has-banner' : ''}"><div class="fd-header">${imgTag}<div><div class="fd-name">${esc(d.displayName)}</div>${pronounsHtml}<div class="fd-status"><span class="${fdDotClass} ${statusDotClass(d.status)}" style="width:8px;height:8px;"></span>${statusLabel(d.status)}${fdIsWeb ? ' (Web)' : ''}${d.statusDescription ? ' — ' + esc(d.statusDescription) : ''}</div></div></div>${badgesHtml}${actionsHtml}${tabsHtml}<div id="fdTabInfo">${infoContent}</div><div id="fdTabGroups" style="display:none;">${groupsContent}</div><div id="fdTabMutuals" style="display:none;">${mutualsContent}</div><div style="margin-top:10px;text-align:right;"><button class="modal-btn modal-btn-cancel" onclick="closeFriendDetail()">Close</button></div></div>`;

    // Live ticker - only when friend is confirmed in same instance
    if (_fdLiveTimer) { clearInterval(_fdLiveTimer); _fdLiveTimer = null; }
    if (d.inSameInstance) {
        let liveSecs = d.totalTimeSeconds;
        _fdLiveTimer = setInterval(() => {
            liveSecs++;
            const el = document.getElementById('fdTimeTogether');
            if (el) el.textContent = formatDuration(liveSecs);
            else { clearInterval(_fdLiveTimer); _fdLiveTimer = null; }
        }, 1000);
    }
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
        btn.textContent = 'Removing...';
        sendToCS({ action: 'vrcUnfriend', userId: userId });
    } else {
        btn.dataset.confirm = '1';
        btn.textContent = 'Confirm?';
        setTimeout(() => {
            if (btn && !btn.disabled) {
                delete btn.dataset.confirm;
                btn.textContent = 'Unfriend';
            }
        }, 4000);
    }
}

function showFriendActionToast(success, message) {
    const c = document.getElementById('friendDetailContent');
    const existing = c.querySelector('.fd-toast');
    if (existing) existing.remove();
    const t = document.createElement('div');
    t.className = 'fd-toast ' + (success ? 'success' : 'error');
    t.textContent = message;
    c.appendChild(t);
    setTimeout(() => t.remove(), 3000);
    const btnContainer = c.querySelector('.fd-actions');
    if (btnContainer) btnContainer.querySelectorAll('button').forEach(b => b.disabled = false);
}

// Favorite Friends

function toggleFavFriend(userId, fvrtId, btn) {
    const isFav = btn.classList.contains('active');
    btn.disabled = true;
    if (isFav) {
        sendToCS({ action: 'vrcRemoveFavoriteFriend', userId, fvrtId });
    } else {
        sendToCS({ action: 'vrcAddFavoriteFriend', userId });
    }
}

function handleFavFriendToggled(payload) {
    const { userId, fvrtId, isFavorited } = payload;
    // Update in-memory list
    favFriendsData = favFriendsData.filter(f => f.favoriteId !== userId);
    if (isFavorited) favFriendsData.push({ fvrtId, favoriteId: userId });
    // Update button if profile is open
    const btn = document.getElementById('fdFavBtn');
    if (btn) {
        btn.disabled = false;
        btn.classList.toggle('active', isFavorited);
        btn.innerHTML = `<span class="msi" style="font-size:16px;">${isFavorited ? 'star' : 'star_outline'}</span>${isFavorited ? 'Unfavorite' : 'Favorite'}`;
    }
    // Refresh favorites grid if visible
    filterFavFriends();
}

// People Tab: Favorites / Search

function setPeopleFilter(filter) {
    peopleFilter = filter;
    document.getElementById('peopleFilterFav').classList.toggle('active', filter === 'favorites');
    document.getElementById('peopleFilterSearch').classList.toggle('active', filter === 'search');
    document.getElementById('peopleFavArea').style.display    = filter === 'favorites' ? '' : 'none';
    document.getElementById('peopleSearchArea').style.display = filter === 'search'    ? '' : 'none';
    if (filter === 'favorites' && favFriendsData.length === 0)
        sendToCS({ action: 'vrcGetFavoriteFriends' });
}

function renderFavFriends(list) {
    favFriendsData = Array.isArray(list) ? list : [];
    filterFavFriends();
}

function filterFavFriends() {
    const el = document.getElementById('favFriendsGrid');
    if (!el) return;
    const q = (document.getElementById('favFriendSearchInput')?.value || '').toLowerCase();
    const favIds = new Set(favFriendsData.map(f => f.favoriteId));
    let friends = vrcFriendsData.filter(f => favIds.has(f.id));
    if (q) friends = friends.filter(f => (f.displayName || '').toLowerCase().includes(q));
    if (!friends.length) {
        el.innerHTML = `<div class="empty-msg">${q ? 'No favorites match your search' : 'No favorite friends yet'}</div>`;
        return;
    }
    el.innerHTML = friends.map(f => {
        const img = f.image ? `<div class="fav-friend-av" style="background-image:url('${f.image}')"></div>`
                            : `<div class="fav-friend-av fav-friend-av-letter">${esc((f.displayName || '?')[0].toUpperCase())}</div>`;
        const dotCls = f.presence === 'web' ? 'vrc-status-ring' : 'vrc-status-dot';
        const uid = jsq(f.id);
        return `<div class="fav-friend-card" onclick="openFriendDetail('${uid}')">
            ${img}
            <div class="fav-friend-info">
                <div class="fav-friend-name">${esc(f.displayName)}</div>
                <div class="fav-friend-status"><span class="${dotCls} ${statusDotClass(f.status)}" style="width:6px;height:6px;flex-shrink:0;"></span>${statusLabel(f.status)}${f.statusDescription ? ' — ' + esc(f.statusDescription) : ''}</div>
            </div>
        </div>`;
    }).join('');
}
