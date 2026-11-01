let currentLanguage = 'en';
let i18nBundle = {};

const UI_LANGUAGES = {
    en: { label: 'English', flag: '\uD83C\uDDFA\uD83C\uDDF8', locale: 'en-US' },
    de: { label: 'Deutsch', flag: '\uD83C\uDDE9\uD83C\uDDEA', locale: 'de-DE' },
    es: { label: 'Espa\u00F1ol', flag: '\uD83C\uDDEA\uD83C\uDDF8', locale: 'es-ES' },
    fr: { label: 'Fran\u00E7ais', flag: '\uD83C\uDDEB\uD83C\uDDF7', locale: 'fr-FR' },
    ja: { label: '\u65E5\u672C\u8A9E', flag: '\uD83C\uDDEF\uD83C\uDDF5', locale: 'ja-JP' },
    'zh-cn': { label: '\u7B80\u4F53\u4E2D\u6587', flag: '\uD83C\uDDE8\uD83C\uDDF3', locale: 'zh-CN' },
    'zh-tw': { label: '\u7E41\u9AD4\u4E2D\u6587(\u53F0\u7063)', flag: '\uD83C\uDDF9\uD83C\uDDFC', locale: 'zh-TW' },
    ru: { label: '\u0420\u0443\u0441\u0441\u043A\u0438\u0439', flag: '\uD83C\uDDF7\uD83C\uDDFA', locale: 'ru-RU' },
    ko: { label: '\uD55C\uAD6D\uC5B4', flag: '\uD83C\uDDF0\uD83C\uDDF7', locale: 'ko-KR' },
    uk: { label: '\u0423\u043A\u0440\u0430\u0457\u043D\u0441\u044C\u043A\u0430', flag: '\uD83C\uDDFA\uD83C\uDDE6', locale: 'uk-UA' }
};

function normalizeUiLanguage(language) {
    const normalized = String(language || '').trim().toLowerCase();
    return UI_LANGUAGES[normalized] ? normalized : 'en';
}

function t(key, fallback = '') {
    return i18nBundle[key] ?? fallback;
}

function tf(key, vars = {}, fallback = '') {
    return String(t(key, fallback)).replace(/\{(\w+)\}/g, (_, name) => {
        return vars[name] ?? `{${name}}`;
    });
}

function getLanguageMeta(language) {
    return UI_LANGUAGES[normalizeUiLanguage(language)];
}

function getLanguageLocale(language = currentLanguage) {
    return getLanguageMeta(language)?.locale || 'en-US';
}

function requestTranslation(language = currentLanguage) {
    sendToCS({ action: 'loadTranslation', language: normalizeUiLanguage(language) });
}

function handleTranslationData(payload) {
    currentLanguage = normalizeUiLanguage(payload?.language || currentLanguage);
    i18nBundle = payload?.translations || {};
    document.documentElement.lang = currentLanguage;
    applyTranslations();
}

let _i18nApplying = false;

function applyTranslations(root = document) {
    applyTranslationAttributes(root);
    if (root !== document || _i18nApplying) return;
    _i18nApplying = true;
    try { applyTranslationRerenders(); }
    finally { _i18nApplying = false; }
    document.documentElement.dispatchEvent(new CustomEvent('languagechange', { detail: { language: currentLanguage } }));
}

function applyTranslationAttributes(root) {
    root.querySelectorAll('[data-i18n]').forEach(el => {
        const value = t(el.dataset.i18n);
        if (value) el.textContent = value;
    });
    root.querySelectorAll('[data-i18n-html]').forEach(el => {
        const value = t(el.dataset.i18nHtml);
        if (value) el.innerHTML = value;
    });
    root.querySelectorAll('[data-i18n-placeholder]').forEach(el => {
        const value = t(el.dataset.i18nPlaceholder);
        if (value) el.setAttribute('placeholder', value);
    });
    root.querySelectorAll('[data-i18n-title]').forEach(el => {
        const value = t(el.dataset.i18nTitle);
        if (value) el.setAttribute('title', value);
    });
}

function applyTranslationRerenders() {
    renderLanguageChips();
    if (typeof renderThemeChips === 'function') renderThemeChips();
    if (typeof renderSpecialThemeChips === 'function') renderSpecialThemeChips();
    if (typeof renderCursorThemeChips === 'function') renderCursorThemeChips();
    if (typeof renderFontGrid === 'function') renderFontGrid();
    document.querySelectorAll('select').forEach(el => el._vnRefresh && el._vnRefresh());
    if (typeof renderFolders === 'function' && settings?.folders) renderFolders(settings.folders);
    if (typeof renderExtraExeDesktop === 'function' && settings?.extraExeDesktop) renderExtraExeDesktop(settings.extraExeDesktop);
    if (typeof renderExtraExeVR === 'function' && settings?.extraExeVR) renderExtraExeVR(settings.extraExeVR);
    if (typeof renderWebhookCards === 'function') renderWebhookCards(settings?.webhooks || settings?.Webhooks || []);
    if (typeof updateCurrentPageTitle === 'function') updateCurrentPageTitle();
    if (typeof updateClock === 'function') updateClock();
    if (typeof updateFavWorldGroupHeader === 'function') updateFavWorldGroupHeader();
    if (typeof renderFavWorlds === 'function'
        && typeof _favWorldsLoaded !== 'undefined'
        && _favWorldsLoaded
        && typeof favWorldsData !== 'undefined'
        && typeof favWorldGroups !== 'undefined') {
        renderFavWorlds({ worlds: favWorldsData, groups: favWorldGroups });
    }
    if (typeof renderNotifications === 'function' && typeof notifications !== 'undefined') renderNotifications(notifications);
    if (typeof renderCurrentInstance === 'function' && typeof currentInstanceData !== 'undefined' && currentInstanceData) renderCurrentInstance(currentInstanceData);
    if (typeof update2FAMessage === 'function' && document.getElementById('modal2FA')?.style.display !== 'none') update2FAMessage();
    if (typeof renderMyProfileContent === 'function' && document.getElementById('modalMyProfile')?.style.display !== 'none') renderMyProfileContent();
    if (typeof renderVrcFriends === 'function' && typeof vrcFriendsLoaded !== 'undefined' && vrcFriendsLoaded) renderVrcFriends(vrcFriendsData);
    if (typeof currentFriendDetail !== 'undefined' && currentFriendDetail && typeof renderFriendDetail === 'function') renderFriendDetail(currentFriendDetail);
    if (typeof filterFavFriends === 'function' && typeof favFriendsData !== 'undefined') filterFavFriends();
    if (typeof renderModList === 'function' && typeof blockedData !== 'undefined' && blockedData !== null) renderModList('blockedList', blockedData, 'block');
    if (typeof renderModList === 'function' && typeof mutedData !== 'undefined' && mutedData !== null) renderModList('mutedList', mutedData, 'mute');
    if (typeof openStatusModal === 'function' && document.getElementById('modalStatus')?.style.display !== 'none' && currentVrcUser) openStatusModal();
    if (typeof refreshFriendInviteModalTranslations === 'function' && window._inviteModalEl) refreshFriendInviteModalTranslations();
    if (typeof refreshImagePickerTranslations === 'function' && document.getElementById('imagePickerOverlay')) refreshImagePickerTranslations();
    if (typeof renderWorldSearchDetail === 'function'
        && typeof _wdCurrentWorldId !== 'undefined'
        && _wdCurrentWorldId
        && typeof worldInfoCache !== 'undefined'
        && worldInfoCache[_wdCurrentWorldId]
        && document.getElementById('modalDetail')?.style.display !== 'none') {
        renderWorldSearchDetail(worldInfoCache[_wdCurrentWorldId]);
    }
}

function renderLanguageChips() {
    const el = document.getElementById('languageGrid');
    if (!el) return;
    el.innerHTML = Object.entries(UI_LANGUAGES).map(([key, meta]) =>
        `<button class="theme-chip${currentLanguage === key ? ' active' : ''}" onclick="selectLanguage('${key}')"><span class="theme-flag" aria-hidden="true">${meta.flag}</span>${t(`language.${key}`, meta.label)}</button>`
    ).join('');
}

function selectLanguage(language) {
    const nextLanguage = normalizeUiLanguage(language);
    if (nextLanguage === currentLanguage) return;
    currentLanguage = nextLanguage;
    renderLanguageChips();
    requestTranslation(nextLanguage);
    autoSave();
    const hint = document.getElementById('langRestartHint');
    if (hint) hint.style.display = '';
}
