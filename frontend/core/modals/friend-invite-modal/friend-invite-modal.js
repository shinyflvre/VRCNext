/* === Friend Invite Modal === */
/* === Invite Modal === */
let _invModalUserId      = null;
let _invModalApiMsgs     = []; // 
let _invModalSelected    = -1;
let _invModalTab         = 'direct'; 
let _invModalPhotoFileId = null;
let _invModalPhotoUrl    = null;  
let _invModalDisplayName = '';

function openFriendInviteModal(userId, displayName, initialTab) {
    closeFriendInviteModal();
    _invModalUserId      = userId;
    _invModalApiMsgs     = [];
    _invModalSelected    = -1;
    _invModalTab         = 'direct';
    _invModalPhotoFileId = null;
    _invModalPhotoUrl    = null;
    _invModalDisplayName = displayName || '';
    const _invInitialTab = initialTab || 'direct';

    const thumb      = currentInstanceData?.worldThumb || '';
    const worldName  = currentInstanceData?.worldName || t('profiles.invite.your_instance', 'your instance');
    const hasVrcPlus = Array.isArray(currentVrcUser?.tags) && currentVrcUser.tags.includes('system_supporter');
    const inviteTitle = tf('profiles.invite.to', { name: esc(displayName) }, 'Invite {name} to');

    const el = document.createElement('div');
    el.className = 'modal-overlay';
    el.style.display = 'flex'; // inline display required by _closeTopModal (Escape)
    el.style.zIndex = '10003';
    el.innerHTML = `
        <div class="modal-box inv-single-modal">
            ${renderModalBar(_invModalDisplayName, [modalCloseAction('closeFriendInviteModal()')], { flush: true })}
            <div class="inv-world-banner" style="${thumb ? `background-image:url('${cssUrl(thumb)}')` : ''}">
                <div class="inv-world-fade"></div>
                <div class="inv-world-info">
                    <div style="font-size:calc(11px + var(--fs-off, 0px));color:rgba(255,255,255,.65);margin-bottom:2px;">${inviteTitle}</div>
                    <div class="inv-world-name">${esc(worldName)}</div>
                </div>
            </div>
            <div class="fd-tabs" style="margin:14px 16px 0;flex-shrink:0;">
                <button class="fd-tab active" id="invTab_direct"  onclick="_invModalSetTab('direct')">${t('profiles.invite.tab.direct', 'Directly')}</button>
                <button class="fd-tab"         id="invTab_message" onclick="_invModalSetTab('message')">${t('profiles.invite.tab.message', 'With Message')}</button>
                ${hasVrcPlus ? `<button class="fd-tab" id="invTab_photo" onclick="_invModalSetTab('photo')">${t('profiles.invite.tab.photo', 'With Image')}</button>` : ''}
            </div>
            <div class="inv-single-body">
                <div id="invContent_direct">
                    <div class="fd-info-card" style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx2);" id="invDirectDesc">${t('profiles.invite.direct_description', 'Send a direct invite with no message.')}</div>
                </div>
                <div id="invMsgSection" style="display:none;">
                    <div class="fd-info-card">
                        <div class="fd-group-rep-label" id="invMsgOptLabel"></div>
                        <div id="invMsgList"></div>
                    </div>
                </div>
                <div id="invPhotoSection" style="display:none;">
                    <div class="fd-info-card">
                        <div class="fd-group-rep-label" id="invPhotoLabel">${t('profiles.invite.image_label', 'Image')} <span style="font-weight:400;text-transform:none;letter-spacing:normal;">(${t('common.required', 'required')})</span></div>
                        <div id="invLibraryGrid" style="display:grid;grid-template-columns:repeat(auto-fill,minmax(72px,1fr));gap:6px;max-height:180px;overflow-y:auto;padding:4px 0;"></div>
                    </div>
                </div>
                <button id="invSendBtn" class="vrcn-button vrcn-btn-primary inv-action-full" onclick="_invModalSend()">${t('profiles.invite.send', 'Send Invite')}</button>
            </div>
        </div>`;
    el.addEventListener('click', e => { if (e.target === el) closeFriendInviteModal(); });
    document.body.appendChild(el);
    window._inviteModalEl = el;
    if (_invInitialTab !== 'direct') _invModalSetTab(_invInitialTab);
}

function refreshFriendInviteModalTranslations() {
    const el = window._inviteModalEl;
    if (!el) return;
    const titleEl = el.querySelector('.inv-world-info > div:first-child');
    if (titleEl) titleEl.textContent = tf('profiles.invite.to', { name: _invModalDisplayName }, 'Invite {name} to');
    const directTab = document.getElementById('invTab_direct');
    if (directTab) directTab.textContent = t('profiles.invite.tab.direct', 'Directly');
    const messageTab = document.getElementById('invTab_message');
    if (messageTab) messageTab.textContent = t('profiles.invite.tab.message', 'With Message');
    const photoTab = document.getElementById('invTab_photo');
    if (photoTab) photoTab.textContent = t('profiles.invite.tab.photo', 'With Image');
    const directDesc = document.getElementById('invDirectDesc');
    if (directDesc) directDesc.textContent = t('profiles.invite.direct_description', 'Send a direct invite with no message.');
    const optionalLabel = document.getElementById('invMsgOptLabel');
    if (optionalLabel) optionalLabel.textContent = _invModalTab === 'photo'
        ? t('profiles.invite.optional_message', 'Optional message')
        : t('profiles.invite.tab.message', 'With Message');
    const photoLabel = document.getElementById('invPhotoLabel');
    if (photoLabel) photoLabel.innerHTML = `${t('profiles.invite.image_label', 'Image')} <span style="font-weight:400;text-transform:none;letter-spacing:normal;">(${t('common.required', 'required')})</span>`;
    const sendBtn = document.getElementById('invSendBtn');
    if (sendBtn) sendBtn.textContent = t('profiles.invite.send', 'Send Invite');
    _invModalRenderMsgs();
    if (_invModalTab === 'photo') {
        const cached = (typeof invFilesCache !== 'undefined') ? invFilesCache['gallery'] : null;
        if (cached) _invModalRenderLibrary(cached);
    }
}

function _invModalSetTab(tab) {
    _invModalTab = tab;
    ['direct', 'message', 'photo'].forEach(t => {
        const btn = document.getElementById(`invTab_${t}`);
        if (btn) btn.classList.toggle('active', t === tab);
    });
    const directEl = document.getElementById('invContent_direct');
    const msgSect  = document.getElementById('invMsgSection');
    const optLabel = document.getElementById('invMsgOptLabel');
    const photoSect = document.getElementById('invPhotoSection');
    if (directEl)  directEl.style.display  = tab === 'direct'  ? '' : 'none';
    if (msgSect)   msgSect.style.display   = (tab === 'message' || tab === 'photo') ? '' : 'none';
    if (optLabel)  optLabel.textContent    = tab === 'photo'
        ? t('profiles.invite.optional_message', 'Optional message')
        : t('profiles.invite.tab.message', 'With Message');
    if (photoSect) photoSect.style.display = tab === 'photo'   ? '' : 'none';
    // Load messages on first switch to a tab that needs them
    if ((tab === 'message' || tab === 'photo') && !_invModalApiMsgs.length) {
        sendToCS({ action: 'vrcGetInviteMessages' });
    } else if (tab === 'message' || tab === 'photo') {
        _invModalRenderMsgs();
    }
    // On first switch to photo tab: load library from cache or request
    if (tab === 'photo') {
        const cached = (typeof invFilesCache !== 'undefined') && invFilesCache['gallery'];
        if (cached && cached.length > 0) _invModalRenderLibrary(cached);
        else sendToCS({ action: 'invGetFiles', tag: 'gallery' });
    }
    _invModalUpdateSendBtn();
}

function _invModalRenderMsgs() {
    const list = document.getElementById('invMsgList');
    if (!list) return;
    if (!_invModalApiMsgs.length) {
        list.innerHTML = `<div class="inv-msg-loading"><span class="msi" style="font-size:16px;animation:spin 1s linear infinite;">progress_activity</span> ${t('profiles.invite.loading_messages', 'Loading messages...')}</div>`;
        return;
    }
    list.innerHTML = _invModalApiMsgs.map(m => {
        const i        = m.slot;
        const canEdit  = m.canBeUpdated;
        const cooldown = m.remainingCooldownMinutes || 0;
        const isSelected = _invModalSelected === i;
        return `
        <div class="inv-msg-item${isSelected ? ' selected' : ''}" id="invMsg_${i}" onclick="_invModalSelectMsg(${i})">
            <span class="inv-msg-text" id="invMsgText_${i}">${esc(m.message)}</span>
            ${canEdit
                ? `<button class="inv-msg-edit" onclick="event.stopPropagation();_invModalEditMsg(${i})" title="${esc(t('common.edit', 'Edit'))}"><span class="msi" style="font-size:14px;">edit</span></button>`
                : `<span class="inv-msg-cooldown" title="${esc(tf('profiles.invite.cooldown_title', { count: cooldown }, '{count} min cooldown'))}"><span class="msi" style="font-size:13px;">schedule</span></span>`}
        </div>`;
    }).join('');
}

function handleVrcInviteMessages(msgs) {
    _invModalApiMsgs = (msgs || []).slice().sort((a, b) => a.slot - b.slot);
    _invModalRenderMsgs();
}

function handleVrcInviteMessageUpdateFailed(payload) {
    const itemEl = document.getElementById(`invMsg_${payload.slot}`);
    if (itemEl) { delete itemEl.dataset.editing; _invModalRenderMsgs(); }
    showToast(false, tf('profiles.invite.cooldown_toast', { count: payload.cooldown || 60 }, 'Cooldown: {count} min remaining'));
}

function _invModalSelectMsg(idx) {
    _invModalSelected = _invModalSelected === idx ? -1 : idx;
    document.querySelectorAll('#invMsgList .inv-msg-item').forEach(el => {
        el.classList.toggle('selected', parseInt(el.id.replace('invMsg_', '')) === _invModalSelected);
    });
    _invModalUpdateSendBtn();
}

function _invModalEditMsg(idx) {
    const itemEl = document.getElementById(`invMsg_${idx}`);
    const textEl = document.getElementById(`invMsgText_${idx}`);
    if (!itemEl || !textEl || itemEl.dataset.editing) return;
    itemEl.dataset.editing = '1';
    const cur = (_invModalApiMsgs.find(m => m.slot === idx) || {}).message || '';
    textEl.outerHTML = `<input class="inv-msg-input" id="invMsgText_${idx}" value="${cur.replace(/"/g, '&quot;')}"
        onblur="_invModalSaveMsg(${idx},this)"
        onkeydown="if(event.key==='Enter')this.blur();if(event.key==='Escape'){this.dataset.cancel='1';this.blur();}">`;
    const inp = document.getElementById(`invMsgText_${idx}`);
    if (inp) { inp.focus(); inp.select(); }
    const icon = itemEl.querySelector('.inv-msg-edit .msi');
    if (icon) icon.textContent = 'check';
}

function _invModalSaveMsg(idx, input) {
    const itemEl = document.getElementById(`invMsg_${idx}`);
    if (!itemEl) return;
    delete itemEl.dataset.editing;
    const m = _invModalApiMsgs.find(x => x.slot === idx);
    const newText = input.value.trim();
    if (!input.dataset.cancel && newText && m && newText !== m.message) {
        sendToCS({ action: 'vrcUpdateInviteMessage', slot: idx, message: newText });
        m.message = newText;
        m.canBeUpdated = false;
        m.remainingCooldownMinutes = 60;
    }
    _invModalRenderMsgs();
}

function getInviteUploadTileHtml() {
    return `<div style="width:100%;aspect-ratio:1;border-radius:6px;cursor:pointer;background:var(--bg-input);border:1.5px dashed var(--border);display:flex;align-items:center;justify-content:center;flex-shrink:0;" onmouseover="this.style.borderColor='var(--accent)'" onmouseout="this.style.borderColor='var(--border)'" onclick="_invModalOpenUpload()" title="${esc(t('profiles.invite.upload_new_photo', 'Upload new photo'))}"><span class="msi" style="font-size:22px;color:var(--tx2);pointer-events:none;">add_photo_alternate</span></div>`;
}

function _invModalOpenUpload() {
    openInvUploadModal('photos', file => {
        _invModalRenderLibrary(invFilesCache['gallery'] || []);
        const firstImg = document.querySelector('#invLibraryGrid img');
        if (firstImg) _invModalSelectLibraryPhoto(firstImg, file.fileUrl, file.id);
    });
}

function _invModalOnGalleryLoaded(files) {
    if (!document.getElementById('invLibraryGrid')) return;
    _invModalRenderLibrary(files);
}

function _invModalRenderLibrary(files) {
    const grid = document.getElementById('invLibraryGrid');
    if (!grid) return;
    if (!files || !files.length) { grid.innerHTML = getInviteUploadTileHtml(); return; }
    grid.innerHTML = getInviteUploadTileHtml() + files.map(f => {
        const url = f.fileUrl || '';
        const fid = jsq(f.id || '');
        if (!url) return '';
        return `<img src="${esc(url)}" style="width:100%;aspect-ratio:1;object-fit:cover;border-radius:6px;cursor:pointer;opacity:0.85;transition:opacity .15s;" onmouseover="this.style.opacity=1" onmouseout="this.style.opacity=0.85" onclick="_invModalSelectLibraryPhoto(this,'${jsq(url)}','${fid}')" onerror="this.style.display='none'">`;
    }).join('');
}

function _invModalSelectLibraryPhoto(el, url, fileId) {
    document.querySelectorAll('#invLibraryGrid img').forEach(i => i.style.outline = 'none');
    el.style.outline = '2px solid var(--accent)';
    _invModalPhotoFileId = fileId;
    _invModalPhotoUrl    = url;
    _invModalUpdateSendBtn();
}

function _invModalUpdateSendBtn() {
    const btn = document.getElementById('invSendBtn');
    if (!btn) return;
    btn.disabled = (_invModalTab === 'message' && _invModalSelected < 0)
                || (_invModalTab === 'photo'   && !_invModalPhotoUrl);
}

function _invModalSend() {
    if (!_invModalUserId) return;
    if (_invModalTab === 'direct') {
        sendToCS({ action: 'vrcInviteFriend', userId: _invModalUserId });
    } else if (_invModalTab === 'message') {
        if (_invModalSelected < 0) return;
        sendToCS({ action: 'vrcInviteFriend', userId: _invModalUserId, messageSlot: _invModalSelected });
    } else if (_invModalTab === 'photo') {
        if (!_invModalPhotoUrl) return;
        const p = { action: 'vrcInviteFriendWithPhoto', userId: _invModalUserId, fileUrl: _invModalPhotoUrl };
        if (_invModalSelected >= 0) p.messageSlot = _invModalSelected;
        sendToCS(p);
    }
    closeFriendInviteModal();
}

function closeFriendInviteModal() {
    const el = window._inviteModalEl;
    if (!el) return;
    el.style.opacity = '0';
    el.style.transition = 'opacity .15s';
    setTimeout(() => el.remove(), 150);
    window._inviteModalEl = null;
}
