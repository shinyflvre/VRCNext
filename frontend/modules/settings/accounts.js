/* Multi-Account Manager — uses the universal vrcn-user-item classes from items.css. */

let _accountsState = { activeAccountId: '', accounts: [] };

function requestAccountsList() {
    sendToCS({ action: 'listAccounts' });
}

function renderAccountsList(payload) {
    _accountsState = payload || { activeAccountId: '', accounts: [] };
    if (typeof renderTbAccountsMenu === 'function') renderTbAccountsMenu(_accountsState);
    const list = document.getElementById('accountsList');
    if (!list) return;
    const accounts = _accountsState.accounts || [];
    const activeId = _accountsState.activeAccountId || '';
    if (accounts.length === 0) {
        list.innerHTML = `<div class="set-desc">${t('settings.accounts.empty', 'No accounts yet. Click "Add Account" to log in.')}</div>`;
        return;
    }
    const lbl = {
        primary:    t('settings.accounts.badge.primary',    'PRIMARY'),
        active:     t('settings.accounts.badge.active',     'ACTIVE'),
        loggedOut:  t('settings.accounts.badge.logged_out', 'LOGGED OUT'),
        profile:    t('settings.accounts.badge.profile',    'GAME PROFILE {n}'),
        switchBtn:  t('settings.accounts.btn.switch',       'Switch'),
        disconnect: t('settings.accounts.btn.disconnect',   'Disconnect'),
        signin:     t('settings.accounts.btn.signin',       'Sign In'),
        removeBtn:  t('settings.accounts.btn.remove',       'Remove'),
    };
    list.innerHTML = accounts.map(a => {
        const isActive  = a.isActive || a.accountId === activeId;
        const avatar    = a.avatarImageUrl || '';
        const name      = (a.displayName || a.username || '(unnamed)').replace(/</g, '&lt;');
        const userTag   = a.username ? '@' + a.username.replace(/</g, '&lt;') : '';
        const badges    = [];
        if (a.isPrimary) badges.push(`<span class="vrcn-badge accent" style="margin-left:6px;">${lbl.primary}</span>`);
        if (isActive)    badges.push(`<span class="vrcn-badge ok" style="margin-left:6px;">${lbl.active}</span>`);
        if (!a.hasSession && !isActive) badges.push(`<span class="vrcn-badge warn" style="margin-left:6px;">${lbl.loggedOut}</span>`);
        if (!a.isPrimary && a.profileIndex > 0) badges.push(`<span class="vrcn-badge hidden" style="margin-left:6px;">${lbl.profile.replace('{n}', a.profileIndex)}</span>`);

        const buttons = [];
        if (!isActive) {
            buttons.push(`<button class="vrcn-button" onclick="switchToAccount('${a.accountId}')"><span class="msi" style="font-size:14px;">swap_horiz</span> <span>${lbl.switchBtn}</span></button>`);
        } else if (a.hasSession) {
            buttons.push(`<button class="vrcn-button" onclick="logoutFromAccount('${a.accountId}')"><span class="msi" style="font-size:14px;">logout</span> <span>${lbl.disconnect}</span></button>`);
        } else {
            buttons.push(`<button class="vrcn-button" onclick="signInToAccount('${a.accountId}')"><span class="msi" style="font-size:14px;">login</span> <span>${lbl.signin}</span></button>`);
        }
        if (!a.isPrimary) {
            buttons.push(`<button class="vrcn-button" onclick="confirmRemoveAccount('${a.accountId}')"><span class="msi" style="font-size:14px;">delete</span> <span>${lbl.removeBtn}</span></button>`);
        }

        return `
            <div class="vrcn-user-item" style="cursor:default;">
                ${avatar ? `<img class="vrcn-user-item-avatar" src="${imgThumb(avatar, 96)}" alt="">` : `<div class="vrcn-user-item-avatar vrcn-user-item-avatar-letter"></div>`}
                <div class="vrcn-user-item-info">
                    <div style="display:flex;align-items:center;gap:6px;font-size:calc(13px + var(--fs-off, 0px));color:var(--tx0);">${name}${badges.join('')}</div>
                    <div class="vrcn-user-item-status">${userTag}</div>
                </div>
                <div style="display:flex;gap:6px;flex-shrink:0;">${buttons.join('')}</div>
            </div>`;
    }).join('');
}

function showAddAccountForm() {
    const form = document.getElementById('addAccountForm');
    if (form) form.style.display = '';
    const u = document.getElementById('addAccUser'); if (u) { u.value = ''; u.focus(); }
    const p = document.getElementById('addAccPass'); if (p) p.value = '';
    const s = document.getElementById('addAccountStatus'); if (s) s.textContent = '';
}

function cancelAddAccount() {
    sendToCS({ action: 'addAccountCancel' });
    hideAddAccountForm();
}

function hideAddAccountForm() {
    const form = document.getElementById('addAccountForm');
    if (form) form.style.display = 'none';
    const s = document.getElementById('addAccountStatus'); if (s) s.textContent = '';
}

function submitAddAccount() {
    const u = (document.getElementById('addAccUser') || {}).value || '';
    const p = (document.getElementById('addAccPass') || {}).value || '';
    const s = document.getElementById('addAccountStatus');
    if (!u || !p) {
        if (s) s.textContent = t('settings.accounts.enter_credentials', 'Enter username and password');
        return;
    }
    if (s) s.textContent = t('settings.accounts.connecting', 'Connecting...');
    sendToCS({ action: 'addAccount', username: u, password: p });
}

function switchToAccount(accountId) {
    if (!accountId) return;
    const acc = (_accountsState.accounts || []).find(a => a.accountId === accountId);
    if (!acc) return;
    const username = acc.displayName || acc.username || acc.userId || '';
    showSwitchAccountModal(username, accountId);
}

function showSwitchAccountModal(username, accountId) {
    let modal = document.getElementById('modalSwitchAccount');
    if (!modal) {
        modal = document.createElement('div');
        modal.id = 'modalSwitchAccount';
        modal.className = 'modal-overlay';
        modal.style.display = 'none';
        modal.addEventListener('click', e => { if (e.target === modal) modal.style.display = 'none'; });
        modal.innerHTML = `
            <div class="modal-box" style="max-width:460px;">
                <div class="fd-modal-bar">
                    <div class="fd-modal-bar-crumbs"><span class="fd-modal-bar-title" data-i18n="settings.accounts.switch_title">Switch Account</span></div>
                    <div class="fd-modal-bar-actions"><button class="btn-notif fd-action-btn" title="Close" data-i18n-title="common.close" onclick="document.getElementById('modalSwitchAccount').style.display='none'"><span class="msi" style="font-size:20px;">close</span></button></div>
                </div>
                <div class="modal-icon" style="background:rgba(56,132,255,.1);color:var(--accent);margin-top:20px;"><span class="msi" style="font-size:22px;">swap_horiz</span></div>
                <div class="modal-msg" id="modalSwitchAccountMsg" style="word-break:normal;overflow-wrap:break-word;line-height:1.5;"></div>
                <div class="modal-btns">
                    <button class="vrcn-button-round vrcn-btn-join" id="modalSwitchAccountConfirm" data-i18n="settings.accounts.switch_title">Switch Account</button>
                </div>
            </div>`;
        document.body.appendChild(modal);
    }
    // Translate freshly-injected modal nodes for any data-i18n attrs.
    if (typeof applyTranslations === 'function') applyTranslations(modal);
    const safe = (username || '').replace(/</g, '&lt;');
    const tpl = t('settings.accounts.switch_confirm',
        'You want to switch the account to <b>{name}</b>?<br><br>Switching Account will cause the app to restart to load everything properly.');
    document.getElementById('modalSwitchAccountMsg').innerHTML = tpl.replace('{name}', safe);
    document.getElementById('modalSwitchAccountConfirm').onclick = () => {
        document.getElementById('modalSwitchAccount').style.display = 'none';
        if (typeof showLoadingOverlay === 'function') showLoadingOverlay(t('settings.accounts.switching', 'Switching account, restarting...'));
        sendToCS({ action: 'switchAccount', accountId });
    };
    modal.style.display = 'flex';
}

function logoutFromAccount(accountId) {
    if (!accountId) return;
    sendToCS({ action: 'logoutAccount', accountId });
}

function signInToAccount(accountId) {
    if (!accountId) return;
    const acc = (_accountsState.accounts || []).find(a => a.accountId === accountId);
    if (!acc) return;
    const username = acc.username || acc.displayName || '';
    showReconnectModal(username);
}

function showReconnectModal(username) {
    let modal = document.getElementById('modalReconnect');
    if (!modal) {
        modal = document.createElement('div');
        modal.id = 'modalReconnect';
        modal.className = 'modal-overlay';
        modal.style.display = 'none';
        modal.addEventListener('click', e => { if (e.target === modal) modal.style.display = 'none'; });
        modal.innerHTML = `
            <div class="modal-box" style="max-width:420px;">
                <div class="modal-icon" style="background:rgba(56,132,255,.1);color:var(--accent);"><span class="msi" style="font-size:22px;">login</span></div>
                <div class="modal-title" data-i18n="settings.accounts.signin_title">Sign In</div>
                <div class="modal-msg" id="modalReconnectMsg" style="word-break:normal;line-height:1.5;"></div>
                <input type="password" id="modalReconnectPass" class="vrcn-edit-field" placeholder="VRChat password" data-i18n-placeholder="settings.api.password" style="margin:10px 0;width:100%;">
                <div id="modalReconnectError" style="font-size:calc(11px + var(--fs-off, 0px));color:var(--err);min-height:14px;margin-bottom:6px;"></div>
                <div class="modal-btns">
                    <button class="vrcn-button-round" onclick="document.getElementById('modalReconnect').style.display='none'" data-i18n="common.cancel">Cancel</button>
                    <button class="vrcn-button-round vrcn-btn-join" id="modalReconnectConfirm" data-i18n="settings.accounts.btn.signin">Sign In</button>
                </div>
            </div>`;
        document.body.appendChild(modal);
    }
    if (typeof applyTranslations === 'function') applyTranslations(modal);
    const safe = (username || '').replace(/</g, '&lt;');
    const tpl = t('settings.accounts.signin_prompt', 'Sign in as <b>{name}</b>');
    document.getElementById('modalReconnectMsg').innerHTML = tpl.replace('{name}', safe);
    const passEl = document.getElementById('modalReconnectPass');
    passEl.value = '';
    document.getElementById('modalReconnectError').textContent = '';
    const submit = () => {
        const p = passEl.value || '';
        const errEl = document.getElementById('modalReconnectError');
        if (!p) {
            if (errEl) errEl.textContent = t('settings.accounts.signin_enter_password', 'Enter password');
            return;
        }
        document.getElementById('modalReconnect').style.display = 'none';
        sendToCS({ action: 'vrcLogin', username, password: p });
    };
    document.getElementById('modalReconnectConfirm').onclick = submit;
    passEl.onkeydown = e => { if (e.key === 'Enter') submit(); };
    modal.style.display = 'flex';
    setTimeout(() => passEl.focus(), 100);
}

function confirmRemoveAccount(accountId) {
    if (!accountId) return;
    const acc  = (_accountsState.accounts || []).find(a => a.accountId === accountId);
    const name = acc ? (acc.displayName || acc.username || '(unnamed)') : accountId;
    vnConfirmModal({
        title: t('settings.accounts.remove_title', 'Remove Account'),
        icon: 'delete',
        message: tf('settings.accounts.remove_confirm', { name: esc(name) },
                    'Remove {name} from the list? Its database file stays on disk and can be re-linked by adding the same account again.'),
        confirmLabel: t('settings.accounts.btn.remove', 'Remove'),
        onConfirm: () => sendToCS({ action: 'removeAccount', accountId }),
    });
}
