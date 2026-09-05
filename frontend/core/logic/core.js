// Global date/time format (received from Windows system via C#)
let _dtShortPattern = 'dd.MM.yyyy'; // e.g. "dd.MM.yyyy", "M/d/yyyy", "yyyy-MM-dd"
let _dtIs24Hour = true;

function applyDateTimeFormat(payload) {
    if (payload?.shortDatePattern) _dtShortPattern = payload.shortDatePattern;
    if (typeof payload?.is24Hour === 'boolean') _dtIs24Hour = payload.is24Hour;
}

// Apply pattern tokens in safe order (longest first to avoid partial replacement)
function _applyDatePat(dt, pattern) {
    const d = dt.getDate();
    const m = dt.getMonth() + 1;
    const y = dt.getFullYear();
    return pattern
        .replace(/yyyy/g, String(y))
        .replace(/yy/g, String(y).slice(-2))
        .replace(/MM/g, String(m).padStart(2, '0'))
        .replace(/M/g, String(m))
        .replace(/dd/g, String(d).padStart(2, '0'))
        .replace(/d/g, String(d));
}

function fmtShortDate(d) {
    const dt = d instanceof Date ? d : new Date(d);
    if (!dt || isNaN(dt)) return '';
    return _applyDatePat(dt, _dtShortPattern);
}

// Long date: day-padded + full month name in app language + year, order from pattern
function fmtLongDate(d) {
    const dt = d instanceof Date ? d : new Date(d);
    if (!dt || isNaN(dt)) return '';
    const monthName = dt.toLocaleDateString(getLanguageLocale(), { month: 'long' });
    const day = String(dt.getDate()).padStart(2, '0');
    const year = dt.getFullYear();
    const pat = _dtShortPattern.toLowerCase().replace(/[^dmy]/g, '');
    const dIdx = pat.indexOf('d');
    const mIdx = pat.indexOf('m');
    const yIdx = pat.indexOf('y');
    if (yIdx < dIdx && yIdx < mIdx) return `${year}, ${monthName} ${day}`;
    if (mIdx < dIdx) return `${monthName} ${day}, ${year}`;
    return `${day}. ${monthName} ${year}`;
}

function fmtTime(d) {
    const dt = d instanceof Date ? d : new Date(d);
    if (!dt || isNaN(dt)) return '';
    const h = dt.getHours();
    const min = String(dt.getMinutes()).padStart(2, '0');
    if (_dtIs24Hour) return String(h).padStart(2, '0') + ':' + min;
    const meridiem = h >= 12 ? 'PM' : 'AM';
    return String(h % 12 || 12).padStart(2, '0') + ':' + min + ' ' + meridiem;
}

function fmtTimeSeconds(d) {
    const dt = d instanceof Date ? d : new Date(d);
    if (!dt || isNaN(dt)) return '';
    const sec = String(dt.getSeconds()).padStart(2, '0');
    if (_dtIs24Hour) {
        return fmtTime(dt) + ':' + sec;
    }
    const h = dt.getHours();
    const min = String(dt.getMinutes()).padStart(2, '0');
    const meridiem = h >= 12 ? 'PM' : 'AM';
    return String(h % 12 || 12).padStart(2, '0') + ':' + min + ':' + sec + ' ' + meridiem;
}

let relayOn = false, settings = { webhooks: [{}, {}, {}, {}], folders: [], extraExe: [] }, postedFiles = [], selectedFolderIdx = -1;
let favorites = new Set(), showFavOnly = false, libraryFiles = [];
let photoRatings = new Map();
const MEDIA_TAG_CATALOG = ['funny', 'romantic', 'lovely', 'sad', 'extreme', 'meme', 'funky', 'dancing',
                           'friends', 'group', 'relationship', 'sports', 'activities', 'games', 'sleeping', 'misc'];
let mediaTags = new Map();
let mediaUserTags = new Map();
let _prevTab = 0;
let _lazyUnloadDelay = 0; // Lazy Unload Timer. not tested yet
let _lazyUnloadTimer = null;
let hiddenMedia = new Set();
try { hiddenMedia = new Set(JSON.parse(localStorage.getItem('vrcnext_hidden') || '[]')); } catch {}
const thumbCache = {};
let currentTheme = 'vrcn', currentSpecialTheme = '', autoColorAccuracy = 50, notifyAudio = null, messageAudio = null, mediaRelayAudio = null, steamOverlayAudio = null, waterAudio = null, currentVrcUser = null;
let customThemes = []; // user-saved themes from auto color

function decoMasterOn() {
    return !!(typeof settings !== 'undefined' && settings.enableVrcPlusDecorations);
}
function decoSettingSelf(key) {
    return decoMasterOn() && !!settings[key];
}
function decoSettingOthers(key) {
    if (!decoMasterOn()) return false;
    const v = settings[key + 'Others'];
    return (v === undefined || v === null) ? !!settings[key] : !!v;
}
function _decoIsSelf(u) {
    if (!u) return false;
    const id = typeof u === 'string' ? u : (u.id || u.userId || '');
    return !!(id && typeof currentVrcUser !== 'undefined' && currentVrcUser && currentVrcUser.id === id);
}
function decoSelfCls(u) {
    return _decoIsSelf(u) ? ' deco-self' : '';
}
function applyDecorationsSetting() {
    const cls = document.documentElement.classList;
    const scopes = [['frames', 'enableProfileIconFrames'], ['square', 'squareIconFrames'], ['nameplate', 'enableNameplateDecoration'], ['effect', 'enableProfileEffects'], ['dash', 'showDecorationsOnDashboard'], ['glass', 'transparentProfileCards']];
    for (const [n, k] of scopes) {
        cls.toggle('deco-self-' + n, decoSettingSelf(k));
        cls.toggle('deco-others-' + n, decoSettingOthers(k));
    }
    const any = k => decoSettingSelf(k) || decoSettingOthers(k);
    const frames = any('enableProfileIconFrames');
    cls.toggle('icon-frames-on', frames);
    cls.toggle('icon-frames-square', frames && any('squareIconFrames'));
    cls.toggle('nameplate-deco-on', any('enableNameplateDecoration'));
    cls.toggle('profile-effect-on', any('enableProfileEffects'));
    cls.toggle('deco-dashboard-on', any('showDecorationsOnDashboard'));
    cls.toggle('profile-cards-transparent', any('transparentProfileCards'));
}
function iconFrameHtml(frameUrl, animated) {
    if (!frameUrl) return '';
    const src = (animated || !vrcPlusOptimizeEnabled) ? frameUrl : _thumbUrl(frameUrl, 96);
    return `<img class="user-frame-deco" src="${src}" loading="lazy" decoding="async" alt="" aria-hidden="true">`;
}
function nameplateDecoHtml(url, animated) {
    if (!url) return '';
    const src = (animated || !vrcPlusOptimizeEnabled) ? url : _thumbUrl(url, 256);
    return `<img class="nameplate-deco" src="${src}" loading="lazy" decoding="async" alt="" aria-hidden="true">`;
}
function profileEffectHtml(url) {
    if (!url) return '';
    return `<img class="profile-effect-deco" src="${url}" loading="lazy" decoding="async" alt="" aria-hidden="true">`;
}
let currentPlayBtnTheme = '';
let currentCursorTheme = '';
let currentAppFont = 'google-sans';
let currentCustomFont = '';
let currentFontSizeOffset = 0;
let currentTaskbarHeight = 42;
let _systemFonts = [];
let _localHttpPort = 0;
let _cursorFiles = [];
let _customThemes = [];
let _activeCustomThemes = new Set();
let sidebarCollapsed = localStorage.getItem('vrcnext_sidebar') !== '0';
let rsidebarCollapsed = localStorage.getItem('vrcnext_rsidebar') !== '0';
// Apply saved sidebar state immediately on load
(function() {
    const sidebar = document.getElementById('sidebarEl');
    if (sidebar && sidebarCollapsed) {
        sidebar.classList.add('collapsed');
        const icon = document.getElementById('sbIcon');
        if (icon) icon.textContent = 'chevron_right';
    }
    const rs = document.getElementById('rsidebar');
    if (rs && rsidebarCollapsed) {
        rs.classList.add('collapsed');
        const rsIcon = document.getElementById('rsIcon');
        if (rsIcon) rsIcon.textContent = 'chevron_left';
    }
})();
// GUI zoom — Ctrl+Wheel persisted to settings, applied as native browser zoom by the host
let _zoomSaveTimer = null;
let _guiZoom = 1;
function applyGuiZoom(z) {
    _guiZoom = z;
    sendToCS({ action: 'setGuiZoom', zoom: Math.round(z * 100) });
    const _lbl = document.getElementById('tbZoomLabel');
    if (_lbl) _lbl.textContent = Math.round(z * 100) + '%';
}
function _stepGuiZoom(dir) {
    const z = Math.min(2, Math.max(0.5, _guiZoom + dir * 0.05));
    applyGuiZoom(z);
    clearTimeout(_zoomSaveTimer);
    _zoomSaveTimer = setTimeout(() => { try { autoSave(); } catch {} }, 800);
}
document.addEventListener('wheel', e => {
    if (!e.ctrlKey) return;
    e.preventDefault();
    _stepGuiZoom(e.deltaY < 0 ? 1 : -1);
}, { passive: false });
// Ctrl+0 resets zoom, Ctrl+Plus / Ctrl+Minus step it — handled here so the host stays in sync
document.addEventListener('keydown', e => {
    if (!e.ctrlKey) return;
    if (e.key === '0') { e.preventDefault(); applyGuiZoom(1); try { autoSave(); } catch {} }
    else if (e.key === '+' || e.key === '=') { e.preventDefault(); _stepGuiZoom(1); }
    else if (e.key === '-') { e.preventDefault(); _stepGuiZoom(-1); }
});

let dashBgPath = '', dashBgDataUri = '', dashBgSample = '';
let dashWorldCache = {};
let dashGroupCache = {};
let vrcFriendsLoaded = false;
const _fscDefaults = { samelocation: false, favorites: false, ingame: false, web: false, offline: true, groupinstances: false };
let friendSectionCollapsed = (() => {
    try { return Object.assign({}, _fscDefaults, JSON.parse(localStorage.getItem('friendSectionCollapsed') || '{}')); }
    catch { return { ..._fscDefaults }; }
})();
let friendsSidebarTab = (() => {
    try { const v = localStorage.getItem('friendsSidebarTab'); return (v === 'groups' || v === 'favorites') ? v : 'friends'; }
    catch { return 'friends'; }
})();
let avatarsData = [], avatarFavData = [], avatarFilter = 'own', avatarsLoaded = false, currentAvatarId = '';
let avatarInfoCache = {}; // avtr_XXX -> { id, name, thumbnailImageUrl }
let avatarSearchResults = [], avatarSearchPage = 0, avatarSearchQuery = '', avatarSearchHasMore = false, avatarSearchDb = 'avtrdb';
let favAvatarsData = [], favAvatarGroups = [], favAvatarGroupFilter = '';
let notifications = [], notifPanelOpen = false, myGroups = [], myGroupsLoaded = false, myRepresentedGroup = null;
let currentInstanceData = null;
// Pagination state for search
let searchState = {
    worlds: { query: '', offset: 0, results: [], hasMore: false },
    groups: { query: '', offset: 0, results: [], hasMore: false },
    people: { query: '', offset: 0, results: [], hasMore: false },
};
let currentFriendDetail = null;
let _fdLiveTimer = null;
// World info cache for library badges
let worldInfoCache = {};
let pendingDeletePath = null;
// World Tab: Favorites / Search filter
let worldFilter = 'favorites';
let favWorldsData = [];
let favWorldGroups = [];
let favWorldGroupFilter = '';
// People Tab: Favorites / Search / Blocked / Muted filter
let peopleFilter = 'favorites';
let favFriendsData = []; // [{ fvrtId, favoriteId }]
let blockedData = null; // null = not yet loaded
let mutedData = null;
let hiddenAvatarData = [];
let interactOffData = [];
let muteChatData = [];
// People Tab pagination state
let _allFriendsStatusFilter = 'all';
let _peopleAllPage = 0;
let _peopleBlockedPage = 0;
let _peopleMutedPage = 0;
const PEOPLE_PAGE_SIZE = 100; //MAX PG PP
// VRChat API
let vrc2faType = 'totp';
let vrcFriendsData = [];
let selectedStatus = 'active';
const STATUS_LIST = [
    {
        key: 'active',
        labelKey: 'status.online',
        label: 'Online',
        color: '#2DD48C',
        descKey: 'profiles.status.option.online_desc',
        desc: 'You appear online'
    },
    {
        key: 'join me',
        labelKey: 'status.join_me',
        label: 'Join Me',
        color: '#3783FF',
        descKey: 'profiles.status.option.join_me_desc',
        desc: 'Others can easily join you'
    },
    {
        key: 'ask me',
        labelKey: 'status.ask_me',
        label: 'Ask Me',
        color: '#FF8D26',
        descKey: 'profiles.status.option.ask_me_desc',
        desc: 'Others should ask before joining'
    },
    {
        key: 'busy',
        labelKey: 'status.do_not_disturb',
        label: 'Do Not Disturb',
        color: '#FF2D2D',
        descKey: 'profiles.status.option.busy_desc',
        desc: 'You appear busy'
    }
];
// Language tag codes to readable display names
const LANG_MAP = {
    language_eng: 'English', language_kor: '한국어', language_rus: 'Русский',
    language_spa: 'Español', language_por: 'Português', language_zho: '中文',
    language_deu: 'Deutsch', language_jpn: '日本語', language_fra: 'Français',
    language_swe: 'Svenska', language_nld: 'Nederlands', language_tur: 'Türkçe',
    language_ara: 'العربية', language_pol: 'Polski', language_dan: 'Dansk',
    language_nor: 'Norsk', language_fin: 'Suomi', language_ces: 'Čeština',
    language_hun: 'Magyar', language_ron: 'Română', language_tha: 'ไทย',
    language_vie: 'Tiếng Việt', language_ukr: 'Українська', language_ase: 'ASL',
    language_bfi: 'BSL', language_dse: 'DGS', language_fsl: 'LSF',
    language_kvk: 'KSL',
};
// Language tag → flag emoji (for instance info modal)
const LANG_FLAG = {
    language_eng: '🇺🇸', language_kor: '🇰🇷', language_rus: '🇷🇺',
    language_spa: '🇪🇸', language_por: '🇧🇷', language_zho: '🇨🇳',
    language_deu: '🇩🇪', language_jpn: '🇯🇵', language_fra: '🇫🇷',
    language_swe: '🇸🇪', language_nld: '🇳🇱', language_tur: '🇹🇷',
    language_ara: '🇸🇦', language_pol: '🇵🇱', language_dan: '🇩🇰',
    language_nor: '🇳🇴', language_fin: '🇫🇮', language_ces: '🇨🇿',
    language_hun: '🇭🇺', language_ron: '🇷🇴', language_tha: '🇹🇭',
    language_vie: '🇻🇳', language_ukr: '🇺🇦',
};
// Platform SVG icon paths (Simple Icons, CC0)
const PLATFORM_ICONS = {
    'twitter':   { svg: 'M18.244 2.25h3.308l-7.227 8.26 8.502 11.24H16.17l-4.714-6.231-5.401 6.231H2.748l7.73-8.835L1.254 2.25H8.08l4.26 5.632zm-1.161 17.52h1.833L7.084 4.126H5.117z' },
    'instagram': { svg: 'M12 2.163c3.204 0 3.584.012 4.85.07 3.252.148 4.771 1.691 4.919 4.919.058 1.265.069 1.645.069 4.849 0 3.205-.012 3.584-.069 4.849-.149 3.225-1.664 4.771-4.919 4.919-1.266.058-1.644.07-4.85.07-3.204 0-3.584-.012-4.849-.07-3.26-.149-4.771-1.699-4.919-4.92-.058-1.265-.07-1.644-.07-4.849 0-3.204.013-3.583.07-4.849.149-3.227 1.664-4.771 4.919-4.919 1.266-.057 1.645-.069 4.849-.069zm0-2.163c-3.259 0-3.667.014-4.947.072-4.358.2-6.78 2.618-6.98 6.98-.059 1.281-.073 1.689-.073 4.948 0 3.259.014 3.668.072 4.948.2 4.358 2.618 6.78 6.98 6.98 1.281.058 1.689.072 4.948.072 3.259 0 3.668-.014 4.948-.072 4.354-.2 6.782-2.618 6.979-6.98.059-1.28.073-1.689.073-4.948 0-3.259-.014-3.667-.072-4.947-.196-4.354-2.617-6.78-6.979-6.98-1.281-.059-1.69-.073-4.949-.073zm0 5.838c-3.403 0-6.162 2.759-6.162 6.162s2.759 6.163 6.162 6.163 6.162-2.759 6.162-6.163c0-3.403-2.759-6.162-6.162-6.162zm0 10.162c-2.209 0-4-1.79-4-4 0-2.209 1.791-4 4-4s4 1.791 4 4c0 2.21-1.791 4-4 4zm6.406-11.845c-.796 0-1.441.645-1.441 1.44s.645 1.44 1.441 1.44c.795 0 1.439-.645 1.439-1.44s-.644-1.44-1.439-1.44z' },
    'tiktok':    { svg: 'M19.59 6.69a4.83 4.83 0 01-3.77-4.25V2h-3.45v13.67a2.89 2.89 0 01-2.88 2.5 2.89 2.89 0 01-2.89-2.89 2.89 2.89 0 012.89-2.89c.28 0 .54.04.79.1V9.01a6.33 6.33 0 00-.79-.05 6.34 6.34 0 00-6.34 6.34 6.34 6.34 0 006.34 6.34 6.34 6.34 0 006.33-6.34V8.93a8.19 8.19 0 004.77 1.54V7.02a4.85 4.85 0 01-1-.33z' },
    'youtube':   { svg: 'M23.495 6.205a3.007 3.007 0 00-2.088-2.088c-1.87-.501-9.396-.501-9.396-.501s-7.507-.01-9.396.501A3.007 3.007 0 00.527 6.205a31.247 31.247 0 00-.522 5.805 31.247 31.247 0 00.522 5.783 3.007 3.007 0 002.088 2.088c1.868.502 9.396.502 9.396.502s7.506 0 9.396-.502a3.007 3.007 0 002.088-2.088 31.247 31.247 0 00.5-5.783 31.247 31.247 0 00-.5-5.805zM9.609 15.601V8.408l6.264 3.602z' },
    'discord':   { svg: 'M20.317 4.492c-1.53-.69-3.17-1.2-4.885-1.49a.075.075 0 00-.079.036c-.21.369-.444.85-.608 1.23a18.566 18.566 0 00-5.487 0 12.36 12.36 0 00-.617-1.23A.077.077 0 008.562 3c-1.714.29-3.354.8-4.885 1.491a.07.07 0 00-.032.027C.533 9.093-.32 13.555.099 17.961a.08.08 0 00.031.055 20.03 20.03 0 005.993 2.98.078.078 0 00.084-.026 13.83 13.83 0 001.226-1.963.074.074 0 00-.041-.104 13.175 13.175 0 01-1.872-.878.075.075 0 01-.008-.125c.126-.093.252-.19.372-.287a.075.075 0 01.078-.01c3.927 1.764 8.18 1.764 12.061 0a.075.075 0 01.079.009c.12.098.245.195.372.288a.075.075 0 01-.006.125c-.598.344-1.22.635-1.873.877a.075.075 0 00-.041.105c.36.687.772 1.341 1.225 1.962a.077.077 0 00.084.028 19.963 19.963 0 006.002-2.981.076.076 0 00.032-.054c.5-5.094-.838-9.52-3.549-13.442a.06.06 0 00-.031-.028zM8.02 15.278c-1.182 0-2.157-1.069-2.157-2.38 0-1.312.956-2.38 2.157-2.38 1.21 0 2.176 1.077 2.157 2.38 0 1.312-.956 2.38-2.157 2.38zm7.975 0c-1.183 0-2.157-1.069-2.157-2.38 0-1.312.955-2.38 2.157-2.38 1.21 0 2.176 1.077 2.157 2.38 0 1.312-.946 2.38-2.157 2.38z' },
    'github':    { svg: 'M12 .297c-6.63 0-12 5.373-12 12 0 5.303 3.438 9.8 8.205 11.385.6.113.82-.258.82-.577 0-.285-.01-1.04-.015-2.04-3.338.724-4.042-1.61-4.042-1.61C4.422 18.07 3.633 17.7 3.633 17.7c-1.087-.744.084-.729.084-.729 1.205.084 1.838 1.236 1.838 1.236 1.07 1.835 2.809 1.305 3.495.998.108-.776.417-1.305.76-1.605-2.665-.3-5.466-1.332-5.466-5.93 0-1.31.465-2.38 1.235-3.22-.135-.303-.54-1.523.105-3.176 0 0 1.005-.322 3.3 1.23.96-.267 1.98-.399 3-.405 1.02.006 2.04.138 3 .405 2.28-1.552 3.285-1.23 3.285-1.23.645 1.653.24 2.873.12 3.176.765.84 1.23 1.91 1.23 3.22 0 4.61-2.805 5.625-5.475 5.92.42.36.81 1.096.81 2.22 0 1.606-.015 2.896-.015 3.286 0 .315.21.69.825.57C20.565 22.092 24 17.592 24 12.297c0-6.627-5.373-12-12-12' },
    'facebook':  { svg: 'M24 12.073c0-6.627-5.373-12-12-12s-12 5.373-12 12c0 5.99 4.388 10.954 10.125 11.854v-8.385H7.078v-3.47h3.047V9.43c0-3.007 1.792-4.669 4.533-4.669 1.312 0 2.686.235 2.686.235v2.953H15.83c-1.491 0-1.956.925-1.956 1.874v2.25h3.328l-.532 3.47h-2.796v8.385C19.612 23.027 24 18.062 24 12.073z' },
    'twitch':    { svg: 'M11.571 4.714h1.715v5.143H11.57zm4.715 0H18v5.143h-1.714zM6 0L1.714 4.286v15.428h5.143V24l4.286-4.286h3.428L22.286 12V0zm14.571 11.143l-3.428 3.428h-3.429l-3 3v-3H6.857V1.714h13.714z' },
    'bluesky':   { svg: 'M12 10.8c-1.087-2.114-4.046-6.053-6.798-7.995C2.566.944 1.561 1.266.902 1.565.139 1.908 0 3.08 0 3.768c0 .69.378 5.65.624 6.479.815 2.736 3.713 3.66 6.383 3.364.136-.02.275-.04.415-.056-.138.022-.276.04-.415.056-3.912.58-7.387 2.005-2.83 7.078 5.013 5.19 6.87-1.113 7.823-4.308.953 3.195 2.05 9.271 7.733 4.308 4.267-4.308 1.172-6.498-2.74-7.078a8.741 8.741 0 01-.415-.056c.14.017.279.036.415.056 2.67.297 5.568-.628 6.383-3.364.246-.828.624-5.79.624-6.478 0-.69-.139-1.861-.902-2.206-.659-.298-1.664-.62-4.3 1.24C16.046 4.748 13.087 8.687 12 10.8z' },
    'pixiv':     { svg: 'M4.935 0A4.924 4.924 0 000 4.935v14.13A4.924 4.924 0 004.935 24H19.06A4.924 4.924 0 0024 19.065V4.935A4.924 4.924 0 0019.065 0zm7.81 4.547c2.181 0 4.058.676 5.399 1.847a6.117 6.117 0 012.116 4.43c.01 1.619-.554 3.048-1.607 4.17-1.066 1.148-2.59 1.806-4.484 2.003-.39.04-.784.06-1.179.06-.896 0-1.755-.092-2.478-.245v3.488H8.kr V5.276c1.058-.488 2.354-.729 3.748-.729zm.042 1.97c-.584 0-1.145.05-1.66.15v7.422c.466.09 1.01.135 1.61.135.34 0 .683-.016 1.019-.053 1.508-.167 2.606-.689 3.26-1.493.623-.765.939-1.76.93-2.875-.008-1.193-.38-2.212-1.109-2.875-.745-.68-1.828-1.022-3.05-1.011z' },
    'kofi':      { svg: 'M23.881 8.948c-.773-4.085-4.859-4.593-4.859-4.593H.723c-.604 0-.679.798-.679.798s-.082 7.324-.022 11.822c.164 4.641 3.568 4.816 3.568 4.816s14.898.043 19.858.048c4.854-.016 4.854-4.853 4.854-4.853s.742-7.897-4.441-7.038zm-5.222 5.701c-.928.084-1.538-.773-1.538-.773l-1.104 1.03 1.588-8.688 1.537-.16-1.523 8.591zm-9.356-.71l-.904-5.088 1.521-.158.904 5.085-1.521.161zm4.82 0l-.904-5.088 1.52-.158.904 5.085-1.52.161z' },
    'patreon':   { svg: 'M22.957 7.21c-.004-3.064-2.391-5.576-5.191-6.482-3.466-1.125-8.064-.47-11.09 1.99C4.61 4.529 3.22 7.167 3.043 9.967c-.227 3.583.988 7.012 4.28 8.257 2.1.784 4.363.361 6.213-.62 1.625-.862 2.857-2.275 3.429-3.987.521-1.554.53-3.276.538-4.887l.002-.024c.002-1.097.467-2.833 1.604-3.394 1.166-.578 2.853.145 2.848 1.898z' },
    'booth':     { svg: 'M5.217 0A5.217 5.217 0 000 5.217v13.566A5.217 5.217 0 005.217 24h13.566A5.217 5.217 0 0024 18.783V5.217A5.217 5.217 0 0018.783 0zm5.235 5.4h3.096c1.386 0 2.27.25 2.654.752.383.5.362 1.26-.063 2.28a4.1 4.1 0 01-1.265 1.698c-.584.453-1.317.68-2.2.68H11.26zm-3.52 0h1.67l-2.42 13.2H4.51zm3.52 6.52h1.6c1.076 0 1.821.22 2.235.66.415.44.47 1.1.164 1.98a3.97 3.97 0 01-1.35 1.84c-.62.48-1.42.72-2.4.72h-1.62z' },
    'vrchat':    { svg: 'M12 0C5.373 0 0 5.373 0 12s5.373 12 12 12 12-5.373 12-12S18.627 0 12 0zm5.894 8.221l-1.97 9.28a.327.327 0 01-.618.04L12.95 12.7l-3.4 4.84a.327.327 0 01-.618-.04l-1.97-9.28a.327.327 0 01.321-.39h.87a.327.327 0 01.318.25l1.28 5.34 2.7-3.9a.327.327 0 01.538 0l2.7 3.9 1.28-5.34a.327.327 0 01.318-.25h.87a.327.327 0 01.317.39z' },
};

/**
 * Returns an HTML badge for an instance owner or group.
 * Can auto-resolve owner name from vrcFriendsData / dashGroupCache when only ownerId is given.
 * @param {string} ownerId    - 'grp_xxx', 'usr_xxx', or ''
 * @param {string} [ownerName]  - Display name (auto-resolved if empty)
 * @param {string} [ownerGroup] - Group shortCode (auto-resolved if empty)
 * @param {string} [closeModal] - Optional JS to close the parent modal before navigating
 * @returns {string} HTML string (empty if no owner)
 */
function getOwnerBadgeHtml(ownerId, ownerName, ownerGroup, closeModal) {
    if (!ownerId) return '';
    // Auto-resolve from caches
    if (!ownerName && ownerId.startsWith('usr_')) {
        const f = vrcFriendsData.find(f => f.id === ownerId);
        ownerName = f?.displayName || '';
    }
    if (!ownerName && ownerId.startsWith('grp_')) {
        const g = dashGroupCache[ownerId];
        ownerName = g?.name || '';
        if (!ownerGroup) ownerGroup = g?.shortCode || '';
    }
    if (!ownerName) return '';
    const close = closeModal ? closeModal + ';' : '';
    if (ownerId.startsWith('grp_'))
        return `<span class="inst-owner-group-badge" onclick="event.stopPropagation();${close}openGroupDetail('${jsq(ownerId)}')">${esc(ownerName)}${ownerGroup ? `<span class="inst-owner-group-sep">\u00b7</span>${esc(ownerGroup)}` : ''}</span>`;
    if (ownerId.startsWith('usr_'))
        return `<span class="vrcn-badge" style="cursor:pointer;" onclick="event.stopPropagation();${close}openFriendDetail('${jsq(ownerId)}')" title="${t('instance.owner', 'Instance Owner')}"><span class="msi" style="font-size:11px;">person</span>${esc(ownerName)}</span>`;
    return '';
}

/**
 * Returns an HTML badge for a VRChat platform string.
 * @param {string} platform - 'standalonewindows', 'android', 'web', or ''
 * @returns {string} HTML string (empty if unknown/empty)
 */
function getPlatformBadgeHtml(platform) {
    if (platform === 'standalonewindows') return `<span class="vrcn-badge platform-pc" title="${t('instance.platform.pc', 'PC')}"><span class="msi" style="font-size:11px;">computer</span>${t('instance.platform.pc', 'PC')}</span>`;
    if (platform === 'android')           return `<span class="vrcn-badge platform-quest" title="${t('instance.platform.quest', 'Quest')}"><span class="msi" style="font-size:11px;">view_in_ar</span>${t('instance.platform.quest', 'Quest')}</span>`;
    if (platform === 'web')               return `<span class="vrcn-badge platform-web" title="${t('instance.platform.web', 'Web')}"><span class="msi" style="font-size:11px;">language</span>${t('instance.platform.web', 'Web')}</span>`;
    return '';
}

const VRC_CREATOR_BADGE_ID = 'bdg_98b3f9e6-ab5e-4133-96c1-70cb06c07500';

function isEconomyCreator(u) {
    if (!u) return false;
    if (u.isEconomyCreator === true) return true;
    return Array.isArray(u.badges)
        && u.badges.some(b => b && (b.id === VRC_CREATOR_BADGE_ID || b.badgeId === VRC_CREATOR_BADGE_ID));
}

function getCreatorBadgeHtml(u) {
    if (!isEconomyCreator(u)) return '';
    const label = t('profiles.badges.creator', 'Creator');
    return `<span class="vrcn-badge" style="background:rgba(128,106,252,.18);color:#806afc;" title="${label}"><span class="msi" style="font-size:11px;">verified</span>${label}</span>`;
}

const TRUST_RANK_MAX = 4;
const TRUST_BADGE_TARGET = 4;
const TRUST_YEAR_TARGET = 3;
const TRUST_YEAR_WEIGHT = 3;
const TRUST_GROUP_TARGET = 20;
const TRUST_GROUP_JOIN_WEIGHT = 0.8;

function getTrustRankLevel(tags) {
    if (!Array.isArray(tags)) return 0;
    if (tags.includes('system_trust_legend') || tags.includes('system_trust_veteran')) return 4;
    if (tags.includes('system_trust_trusted')) return 3;
    if (tags.includes('system_trust_known')) return 2;
    if (tags.includes('system_trust_basic')) return 1;
    return 0;
}

function getTrustCriteria(u, avatarCount) {
    const tags = Array.isArray(u?.tags) ? u.tags : [];
    const worlds = Array.isArray(u?.userWorlds) ? u.userWorlds.length : 0;
    const avatars = Number(avatarCount) > 0 ? Number(avatarCount) : 0;
    const raw = u?.dateJoined || u?.date_joined || '';
    const joined = raw ? new Date(raw.length === 10 ? raw + 'T00:00:00' : raw) : null;
    const years = (joined && !isNaN(joined.getTime()))
        ? (Date.now() - joined.getTime()) / (365.25 * 24 * 60 * 60 * 1000) : 0;
    const rankLevel = getTrustRankLevel(tags);
    const rankInfo = (typeof getTrustRank === 'function' && tags.length) ? getTrustRank(tags) : null;
    const badgeCount = Array.isArray(u?.badges) ? u.badges.length : 0;
    const groupCount = Array.isArray(u?.userGroups) ? u.userGroups.length : 0;
    const rep = u?.representedGroup;
    const representing = !!(rep && (rep.id || rep.groupId || rep.name));
    return [
        { score: rankLevel / TRUST_RANK_MAX,
          label: t('profiles.trust.criteria.trusted', 'Trusted User'),
          detail: rankInfo ? rankInfo.label : t('profiles.trust.visitor', 'Visitor') },
        { score: (u?.ageVerified === true || u?.ageVerificationStatus === '18+') ? 1 : 0,
          label: t('profiles.meta.age_verified', 'Age Verified') },
        { score: Math.max(Math.min(years / TRUST_YEAR_TARGET, 1), 0),
          weight: TRUST_YEAR_WEIGHT,
          label: t('profiles.trust.criteria.years', '3+ years on VRChat'),
          detail: Math.min(Math.max(Math.floor(years), 0), TRUST_YEAR_TARGET) + ' / ' + TRUST_YEAR_TARGET },
        { score: tags.includes('system_supporter') ? 1 : 0,
          label: t('profiles.trust.criteria.supporter', 'VRC+ Supporter') },
        { score: Math.min(badgeCount / TRUST_BADGE_TARGET, 1),
          label: t('profiles.trust.criteria.badges', '4+ badges'),
          detail: Math.min(badgeCount, TRUST_BADGE_TARGET) + ' / ' + TRUST_BADGE_TARGET },
        { score: (u?.bio && String(u.bio).trim()) ? 1 : 0,
          label: t('profiles.trust.criteria.bio', 'Has a bio') },
        { score: (worlds + avatars >= 1) ? 1 : 0,
          label: t('profiles.trust.criteria.content', 'Uploaded content') },
        { score: Math.min(groupCount / TRUST_GROUP_TARGET, 1) * TRUST_GROUP_JOIN_WEIGHT
               + (representing ? 1 - TRUST_GROUP_JOIN_WEIGHT : 0),
          label: t('profiles.trust.criteria.groups', 'Joined a few groups') },
    ];
}

function getTrustScorePct(crit) {
    const total = crit.reduce((s, c) => s + (c.weight || 1), 0);
    if (!total) return 0;
    return Math.round(crit.reduce((s, c) => s + c.score * (c.weight || 1), 0) / total * 100);
}

function _trustPctColor() {
    return 'var(--bdg-rank-trusted)';
}

function _trustDescription(pct) {
    if (pct >= 100) return t('profiles.trust.description', 'This user has a trusted user standing within the community.');
    if (pct >= 80)  return t('profiles.trust.desc.high', 'This user has a highly trusted standing within the community.');
    if (pct >= 60)  return t('profiles.trust.desc.good', 'This user has a good standing within the community.');
    if (pct >= 40)  return t('profiles.trust.desc.some', 'This user has some established trust within the community.');
    if (pct >= 20)  return t('profiles.trust.desc.low',  'This user has a low level of established trust within the community.');
    return t('profiles.trust.desc.none', 'This user has no established trust within the community yet.');
}

function _trustCritRows(crit) {
    return crit.map(c => {
        const full = c.score >= 1;
        const partial = !full && c.score > 0;
        const icon = full ? 'check_circle' : partial ? 'radio_button_checked' : 'radio_button_unchecked';
        const cls = full ? ' met' : partial ? ' partial' : '';
        const detail = c.detail ? `<span class="fd-trust-crit-detail">${esc(c.detail)}</span>` : '';
        return `<div class="fd-trust-crit${cls}">
            <span class="msi">${icon}</span>${esc(c.label)}${detail}
        </div>`;
    }).join('');
}

function _animateTrustPct(el, target) {
    if (!el) return;
    const from = parseInt(el.textContent, 10) || 0;
    if (from === target) { el.textContent = target + '%'; return; }
    if (el._trustRaf) cancelAnimationFrame(el._trustRaf);
    const start = performance.now();
    const step = now => {
        const k = Math.min((now - start) / 420, 1);
        const eased = 1 - Math.pow(1 - k, 3);
        el.textContent = Math.round(from + (target - from) * eased) + '%';
        if (k < 1) el._trustRaf = requestAnimationFrame(step);
        else el._trustRaf = 0;
    };
    el._trustRaf = requestAnimationFrame(step);
}

let _trustBarSeq = 0;
const _trustBarTimers = {};

function _fillTrustBar(id, pct, color, tries) {
    requestAnimationFrame(() => {
        const wrap = document.getElementById(id);
        if (!wrap) {
            if ((tries || 0) < 40) _fillTrustBar(id, pct, color, (tries || 0) + 1);
            return;
        }
        _paintTrustBar(wrap, pct, color);
    });
}

function _paintTrustBar(wrap, pct, color) {
    wrap.classList.remove('fd-trust-pending');
    const fill = wrap.querySelector('.fd-trust-bar-fill');
    if (fill) { fill.style.background = color; fill.style.width = pct + '%'; }
    const pctEl = wrap.querySelector('.fd-trust-pct');
    if (pctEl) { pctEl.style.color = color; _animateTrustPct(pctEl, pct); }
    const descEl = wrap.querySelector('.fd-trust-desc');
    if (descEl) descEl.textContent = _trustDescription(pct);
}

function getTrustBarHtml(u, avatarCount, ready) {
    const crit = getTrustCriteria(u, avatarCount);
    const pct = getTrustScorePct(crit);
    const color = _trustPctColor(pct);
    const id = 'trustBar' + (++_trustBarSeq);
    if (ready) _fillTrustBar(id, pct, color);
    else _trustBarTimers[id] = setTimeout(() => { delete _trustBarTimers[id]; _fillTrustBar(id, pct, color); }, 6000);
    return `<div class="fd-trust-bar-wrap${ready ? '' : ' fd-trust-pending'}" id="${id}">
        <button type="button" class="fd-trust-bar-head" onclick="toggleTrustCrits(this)">
            <span>${esc(t('profiles.trust.score', 'Trust Score'))}</span>
            <span class="fd-trust-pct" style="color:${color};">0%</span>
            <span class="msi fd-trust-chevron">expand_more</span>
        </button>
        <div class="fd-trust-bar"><div class="fd-trust-bar-fill" style="width:0%;background:${color};"></div></div>
        <p class="fd-trust-desc">${esc(_trustDescription(pct))}</p>
        <div class="fd-trust-crits">${_trustCritRows(crit)}</div>
    </div>`;
}

function toggleTrustCrits(el) {
    el.closest('.fd-trust-bar-wrap')?.classList.toggle('open');
}

function updateTrustBar(slotId, u, avatarCount) {
    const slot = document.getElementById(slotId);
    if (!slot) return;
    const wrap = slot.querySelector('.fd-trust-bar-wrap');
    if (!wrap) { slot.innerHTML = getTrustBarHtml(u, avatarCount, true); return; }
    if (_trustBarTimers[wrap.id]) { clearTimeout(_trustBarTimers[wrap.id]); delete _trustBarTimers[wrap.id]; }
    const crit = getTrustCriteria(u, avatarCount);
    const crits = wrap.querySelector('.fd-trust-crits');
    if (crits) crits.innerHTML = _trustCritRows(crit);
    _paintTrustBar(wrap, getTrustScorePct(crit), _trustPctColor(getTrustScorePct(crit)));
}
// Space Flight
let sfConnected = false;
// Space Turn
let stConnected = false;
// FrameShot
let fsConnected = false;
// Custom Chatbox OSC
let chatboxEnabled = false;
let chatboxCustomLines = [];
let chatboxLineOrder = ['time', 'media', 'stats', 'custom'];
// OSC Tool
let oscParams = {};
let oscConnected = false;
// Timeline
let timelineEvents = [];
let tlFilter = 'all';
let tlMode = 'personal';
let friendTimelineEvents = [];
let ftFilter = 'all';
// Inventory
let activeInvTab = 'photos';
let invFilesCache = {}; // tag → file[]
let invPrintsCache = [];
let invInventoryCache = [];

// Skeleton shimmer helpers. sk(type, count) adds .sk-block CSS class to any element.
function sk(type, n = 1) {
    const t = {
        world:   () => `<div class="vrcn-content-card" style="pointer-events:none;"><div class="cc-bg sk-block"></div><div class="cc-scrim"></div><div class="cc-content"><div class="sk-block" style="height:16px;width:65%;border-radius:4px;margin-bottom:7px;"></div><div class="sk-block" style="height:10px;width:40%;border-radius:4px;"></div></div></div>`,
        feed:    () => `<div class="dash-feed-card"><div class="dash-feed-avatar sk-block"></div><div class="dash-feed-info"><div class="sk-block" style="height:11px;width:75%;border-radius:4px;margin-bottom:5px;"></div><div class="sk-block" style="height:9px;width:50%;border-radius:4px;"></div></div></div>`,
        friend:  () => `<div class="vrc-friend-card"><div class="vrc-friend-avatar sk-block"></div><div class="vrc-friend-info"><div class="sk-block" style="height:11px;width:70%;border-radius:4px;margin-bottom:5px;"></div><div class="sk-block" style="height:9px;width:45%;border-radius:4px;"></div></div></div>`,
        avatar:  () => `<div class="vrcn-content-card av-card" style="pointer-events:none;"><div class="cc-bg sk-block"></div><div class="cc-scrim"></div><div class="cc-content"><div class="sk-block" style="height:14px;width:70%;border-radius:4px;margin-bottom:6px;"></div><div class="sk-block" style="height:10px;width:45%;border-radius:4px;"></div></div></div>`,
        detail:  () => `<div style="padding:4px 0"><div class="sk-block" style="height:180px;border-radius:10px;margin-bottom:20px;"></div><div class="sk-block" style="height:20px;width:60%;border-radius:6px;margin-bottom:10px;"></div><div class="sk-block" style="height:13px;width:40%;border-radius:4px;margin-bottom:20px;"></div><div class="sk-block" style="height:11px;border-radius:4px;margin-bottom:8px;"></div><div class="sk-block" style="height:11px;width:85%;border-radius:4px;margin-bottom:8px;"></div><div class="sk-block" style="height:11px;width:65%;border-radius:4px;"></div></div>`,
        'timeline': () => { const tlRow = (side) => side === 'left' ? `<div class="tl-row"><div class="tl-card-side tl-side-left"><div class="sk-block" style="width:100%;max-width:340px;height:85px;border-radius:10px;"></div></div><div class="tl-center-col"><div class="sk-block" style="width:12px;height:12px;border-radius:50%;flex-shrink:0;"></div></div><div class="tl-card-side tl-side-right"></div></div>` : `<div class="tl-row"><div class="tl-card-side tl-side-left"></div><div class="tl-center-col"><div class="sk-block" style="width:12px;height:12px;border-radius:50%;flex-shrink:0;"></div></div><div class="tl-card-side tl-side-right"><div class="sk-block" style="width:100%;max-width:340px;height:85px;border-radius:10px;"></div></div></div>`; return `<div class="tl-wrap"><div class="tl-date-sep"><div class="sk-block" style="height:10px;width:80px;border-radius:20px;"></div></div>${tlRow('left')}${tlRow('right')}${tlRow('left')}${tlRow('right')}${tlRow('left')}</div>`; },
        'timeline-list': () => { const tlSkRow = (ws) => `<tr class="tl-list-row"><td><div class="sk-block" style="height:11px;width:${ws[0]}%;border-radius:3px;"></div></td><td><div class="sk-block" style="height:11px;width:${ws[1]}%;border-radius:3px;"></div></td><td><div class="sk-block" style="height:20px;width:20px;border-radius:50%;"></div></td><td><div class="sk-block" style="height:11px;width:${ws[2]}%;border-radius:3px;"></div></td><td><div class="sk-block" style="height:11px;width:${ws[3]}%;border-radius:3px;"></div></td></tr>`; return `<div class="tl-list-wrap"><table class="tl-list-table"><colgroup><col style="width:155px"><col style="width:185px"><col style="width:42px"><col style="width:156px"><col></colgroup><thead><tr><th><div class="sk-block" style="height:8px;width:65px;border-radius:3px;"></div></th><th><div class="sk-block" style="height:8px;width:38px;border-radius:3px;"></div></th><th></th><th><div class="sk-block" style="height:8px;width:42px;border-radius:3px;"></div></th><th><div class="sk-block" style="height:8px;width:50px;border-radius:3px;"></div></th></tr></thead><tbody>${tlSkRow([70,65,80,55])}${tlSkRow([80,75,60,70])}${tlSkRow([65,80,75,45])}${tlSkRow([75,60,85,60])}${tlSkRow([85,70,55,75])}${tlSkRow([60,85,70,50])}${tlSkRow([78,65,80,65])}${tlSkRow([68,75,65,40])}</tbody></table></div>`; },
        'content-modal-compact': () => `<div style="display:flex;height:100%;"><div style="width:302px;flex-shrink:0;display:flex;flex-direction:column;border-right:1px solid var(--brd);overflow:hidden;"><div class="sk-block" style="width:100%;height:165px;flex-shrink:0;border-radius:0;"></div><div style="padding:0 16px 16px;margin-top:-58px;display:flex;flex-direction:column;gap:10px;"><div style="display:flex;gap:12px;align-items:flex-start;"><div class="sk-block" style="width:61px;height:61px;border-radius:14px;flex-shrink:0;"></div><div style="flex:1;min-width:0;padding-top:26px;"><div class="sk-block" style="height:14px;width:75%;border-radius:4px;margin-bottom:6px;"></div><div class="sk-block" style="height:10px;width:50%;border-radius:3px;"></div></div></div><div class="sk-block" style="height:10px;width:40%;border-radius:3px;"></div><div class="sk-block" style="height:32px;border-radius:7px;"></div><div style="display:grid;grid-template-columns:1fr 1fr 1fr;gap:6px;"><div class="sk-block" style="height:28px;border-radius:6px;"></div><div class="sk-block" style="height:28px;border-radius:6px;"></div><div class="sk-block" style="height:28px;border-radius:6px;"></div></div></div></div><div style="flex:1;min-width:0;padding:18px;overflow:hidden;"><div class="sk-block" style="height:32px;border-radius:7px;margin-bottom:18px;"></div><div class="sk-block" style="height:11px;border-radius:4px;margin-bottom:7px;"></div><div class="sk-block" style="height:11px;width:85%;border-radius:4px;margin-bottom:7px;"></div><div class="sk-block" style="height:11px;width:70%;border-radius:4px;margin-bottom:7px;"></div><div class="sk-block" style="height:11px;width:90%;border-radius:4px;margin-bottom:7px;"></div><div class="sk-block" style="height:11px;width:60%;border-radius:4px;margin-bottom:20px;"></div><div class="sk-block" style="height:9px;width:28%;border-radius:3px;margin-bottom:12px;"></div><div style="display:grid;grid-template-columns:1fr 1fr 1fr;gap:14px 8px;margin-bottom:20px;"><div><div class="sk-block" style="height:9px;width:70%;border-radius:3px;margin-bottom:6px;"></div><div class="sk-block" style="height:12px;width:85%;border-radius:4px;"></div></div><div><div class="sk-block" style="height:9px;width:55%;border-radius:3px;margin-bottom:6px;"></div><div class="sk-block" style="height:12px;width:65%;border-radius:4px;"></div></div><div><div class="sk-block" style="height:9px;width:65%;border-radius:3px;margin-bottom:6px;"></div><div class="sk-block" style="height:12px;width:30%;border-radius:4px;"></div></div><div><div class="sk-block" style="height:9px;width:60%;border-radius:3px;margin-bottom:6px;"></div><div class="sk-block" style="height:12px;width:25%;border-radius:4px;"></div></div><div><div class="sk-block" style="height:9px;width:45%;border-radius:3px;margin-bottom:6px;"></div><div class="sk-block" style="height:12px;width:30%;border-radius:4px;"></div></div><div><div class="sk-block" style="height:9px;width:75%;border-radius:3px;margin-bottom:6px;"></div><div class="sk-block" style="height:12px;width:70%;border-radius:4px;"></div></div></div></div></div>`
    };
    const fn = t[type]; return fn ? Array.from({length: n}, fn).join('') : '';
}


function emptyStateHtml(icon, title, desc) {
    return `<div class="empty-msg"><div class="empty-msg-icon"><span class="msi">${icon}</span></div><div class="empty-msg-title">${title}</div>${desc ? `<div class="empty-msg-desc">${desc}</div>` : ''}</div>`;
}

const THEMES = {
    vrcn:      { label: 'VRCN',      dot: '#4C4C66', c: { 'bg-base': '#0A0A0A', 'bg-side': '#0A0A0A', 'bg-taskbar': '#0A0A0A', 'bg-card': '#0F0F0F', 'bg-hover': '#1C1C1F', 'bg-input': '#121212', 'tab-card-bg': '#0D0D0D', 'ui-input-bg': '#161618', 'ui-input-hover-bg': '#1C1C1F', 'ui-input-active-bg': '#1F2024', 'badge-bg': '#2B2C30', 'accent': '#4C4C66', 'accent-lt': '#9797B1', 'cyan': '#8CA5FF', 'ok': '#2DD48C', 'warn': '#FFBA37', 'err': '#FF4B55', 'tx0': '#EBEBFF', 'tx1': '#EBEBFF', 'tx2': '#B7B7C3', 'tx3': '#FFFFFF', 'brd': '#1C1C1F', 'brd-lt': '#1C1C1F', 'bdg-user-pc': '#989DAF', 'bdg-user-quest': '#989DAF', 'bdg-user-web': '#989DAF', 'bdg-user-friend': '#2DD48C', 'bdg-rank-visitor': '#CCCCCC', 'bdg-rank-new': '#1778FF', 'bdg-rank-user': '#2BCF5C', 'bdg-rank-known': '#FF7B42', 'bdg-rank-trusted': '#8143E6' } },
    blood:     { label: 'Blood',     dot: '#DF2A4E', c: { 'bg-base': '#0B0611', 'bg-side': '#10091A', 'bg-taskbar': '#10091A', 'bg-card': '#190F26', 'bg-hover': '#251936', 'bg-input': '#1C1229', 'tab-card-bg': '#170D24', 'accent': '#DF2A4E', 'accent-lt': '#E16B82', 'cyan': '#DC7A56', 'ok': '#2DD48C', 'warn': '#FFBA37', 'err': '#FF4B55', 'tx0': '#F2EFF5', 'tx1': '#D2CCDB', 'tx2': '#D2CCDB', 'tx3': '#D2CCDB', 'brd': '#291B3C', 'brd-lt': '#38284D' } },
    halloween: { label: 'Halloween', dot: '#DF462A', c: { 'bg-base': '#0B091A', 'bg-side': '#0B091A', 'bg-taskbar': '#0B091A', 'bg-card': '#110F26', 'bg-hover': '#1B1936', 'bg-input': '#141229', 'tab-card-bg': '#0F0D24', 'accent': '#DF462A', 'accent-lt': '#E17D6B', 'cyan': '#DCA956', 'ok': '#2DD48C', 'warn': '#FFBA37', 'err': '#FF4B55', 'tx0': '#F0EFF5', 'tx1': '#F0EFF5', 'tx2': '#F0EFF5', 'tx3': '#F0EFF5', 'brd': '#1E1B3C', 'brd-lt': '#2B284D' } },
    miku:      { label: 'Miku',      dot: '#66B4D2', c: { 'bg-base': '#080D14', 'bg-side': '#080D14', 'bg-taskbar': '#080D14', 'bg-card': '#080D14', 'bg-hover': '#17262C', 'bg-input': '#0B1017', 'tab-card-bg': '#060B12', 'accent': '#66B4D2', 'accent-lt': '#66B4D2', 'cyan': '#66B4D2', 'ok': '#2DD48C', 'warn': '#FFBA37', 'err': '#FF4B55', 'tx0': '#FFFFFF', 'tx1': '#FFFFFF', 'tx2': '#FFFFFF', 'tx3': '#FFFFFF', 'brd': '#13223F', 'brd-lt': '#13223F' } },
    vrchat:    { label: 'VRChat',    dot: '#0B748E', c: { 'bg-base': '#0E1013', 'bg-side': '#0E1013', 'bg-taskbar': '#0E1013', 'bg-card': '#181B1F', 'bg-hover': '#042E39', 'bg-input': '#1B1E22', 'tab-card-bg': '#16191D', 'accent': '#0B748E', 'accent-lt': '#53C0D5', 'cyan': '#53C0D5', 'ok': '#18A86A', 'warn': '#D4860A', 'err': '#D93040', 'tx0': '#FFFFFF', 'tx1': '#FFFFFF', 'tx2': '#FFFFFF', 'tx3': '#FFFFFF', 'brd': '#042E39', 'brd-lt': '#BEC8DA' } },
    copper:     { label: 'Copper',    dot: '#D08A4F', c: { 'bg-base': '#0B0B0C', 'bg-side': '#101012', 'bg-taskbar': '#101012', 'bg-card': '#151517', 'bg-hover': '#232326', 'bg-input': '#111113', 'tab-card-bg': '#131315', 'ui-input-bg': '#1C1C1F', 'ui-input-hover-bg': '#262629', 'ui-input-active-bg': '#B8703C', 'badge-bg': '#24282E', 'accent': '#D08A4F', 'accent-lt': '#E6AC78', 'cyan': '#8FB4D9', 'ok': '#46C88C', 'warn': '#E0A43C', 'err': '#E05555', 'tx0': '#F4F1EE', 'tx1': '#E4DFDA', 'tx2': '#ABA49D', 'tx3': '#7C7670', 'brd': '#232326', 'brd-lt': '#303034', 'bdg-user-pc': '#8FB4D9', 'bdg-user-quest': '#46C88C', 'bdg-user-web': '#E0A43C', 'bdg-user-friend': '#46C88C', 'bdg-rank-visitor': '#CCCCCC', 'bdg-rank-new': '#1778FF', 'bdg-rank-user': '#2BCF5C', 'bdg-rank-known': '#FF7B42', 'bdg-rank-trusted': '#8143E6' } },
    nature:     { label: 'Nature',      dot: '#8DBF63', c: { 'bg-base': '#0B0C0A', 'bg-side': '#0F110D', 'bg-taskbar': '#0F110D', 'bg-card': '#151714', 'bg-hover': '#232620', 'bg-input': '#111310', 'tab-card-bg': '#131512', 'ui-input-bg': '#1B1E19', 'ui-input-hover-bg': '#262A23', 'ui-input-active-bg': '#4F8A3A', 'badge-bg': '#2A2620', 'accent': '#8DBF63', 'accent-lt': '#B4D98F', 'cyan': '#D8926A', 'ok': '#6ECB86', 'warn': '#D9A441', 'err': '#E05C5C', 'tx0': '#F2F4EE', 'tx1': '#E0E4DA', 'tx2': '#A6AC9E', 'tx3': '#767C6F', 'brd': '#232620', 'brd-lt': '#313529', 'bdg-user-pc': '#86B8D8', 'bdg-user-quest': '#8DBF63', 'bdg-user-web': '#D8926A', 'bdg-user-friend': '#6ECB86', 'bdg-rank-visitor': '#CCCCCC', 'bdg-rank-new': '#1778FF', 'bdg-rank-user': '#2BCF5C', 'bdg-rank-known': '#FF7B42', 'bdg-rank-trusted': '#8143E6' } },
    flippernano: { label: 'Flipper Nano', dot: '#FF896F', c: { 'bg-base': '#E5E8F6', 'bg-side': '#EEF1FF', 'bg-taskbar': '#EEF1FF', 'bg-card': '#E7EAF8', 'bg-hover': '#FF896F', 'bg-input': '#EAEDFB', 'tab-card-bg': '#E0E3F3', 'ui-input-bg': '#D7DAEB', 'ui-input-hover-bg': '#FF896F', 'ui-input-active-bg': '#FF896F', 'badge-bg': '#FFBAAB', 'accent': '#FF896F', 'accent-lt': '#FF896F', 'cyan': '#FF896F', 'ok': '#2BFF00', 'warn': '#FF7455', 'err': '#FF2E00', 'tx0': '#FFFFFF', 'tx1': '#FFFFFF', 'tx2': '#FFFFFF', 'tx3': '#FFFFFF', 'brd': '#D3D6E6', 'brd-lt': '#D3D6E6', 'bdg-user-pc': '#64AAFF', 'bdg-user-quest': '#38DC78', 'bdg-user-web': '#FFA726', 'bdg-user-friend': '#2DD48C', 'bdg-rank-visitor': '#CCCCCC', 'bdg-rank-new': '#1778FF', 'bdg-rank-user': '#2BCF5C', 'bdg-rank-known': '#FF7B42', 'bdg-rank-trusted': '#8143E6' }, light: true, cLight: { 'bg-base': '#E5E8F6', 'tx0': '#000000', 'tx1': '#494949', 'tx2': '#494949', 'tx3': '#494949', 'accent': '#FF896F' } },
    spaceout:   { label: 'Spaceout',     dot: '#FF9F60', c: { 'bg-base': '#05040C', 'bg-side': '#05040C', 'bg-taskbar': '#05040C', 'bg-card': '#0A0714', 'bg-hover': '#191327', 'bg-input': '#110D1B', 'tab-card-bg': '#110D1B', 'ui-input-bg': '#110D1B', 'ui-input-hover-bg': '#191327', 'ui-input-active-bg': '#FF9F60', 'badge-bg': '#4D455F', 'accent': '#FF9F60', 'accent-lt': '#9797B1', 'cyan': '#8CA5FF', 'ok': '#2DD48C', 'warn': '#FFBA37', 'err': '#FF4B55', 'tx0': '#EBEBFF', 'tx1': '#EBEBFF', 'tx2': '#B7B7C3', 'tx3': '#FFFFFF', 'brd': '#1C162C', 'brd-lt': '#1C162C', 'bdg-user-pc': '#FF9F60', 'bdg-user-quest': '#FF9F60', 'bdg-user-web': '#FF9F60', 'bdg-user-friend': '#FF9F60', 'bdg-rank-visitor': '#CCCCCC', 'bdg-rank-new': '#1778FF', 'bdg-rank-user': '#2BCF5C', 'bdg-rank-known': '#FF7B42', 'bdg-rank-trusted': '#8143E6' } },
    fluffy:     { label: 'Fluffy',       dot: '#DFBFFF', c: { 'bg-base': '#EEF1FF', 'bg-side': '#EEF1FF', 'bg-taskbar': '#EEF1FF', 'bg-card': '#FAE8FF', 'bg-hover': '#FFCCE9', 'bg-input': '#FBF1FF', 'tab-card-bg': '#FBF1FF', 'ui-input-bg': '#EEDCF5', 'ui-input-hover-bg': '#FFCCE9', 'ui-input-active-bg': '#F8C5E2', 'badge-bg': '#FFD5EE', 'accent': '#DFBFFF', 'accent-lt': '#D1A8FF', 'cyan': '#DCAFFF', 'ok': '#2BFF00', 'warn': '#FF7455', 'err': '#FF2E00', 'tx0': '#FFFFFF', 'tx1': '#FFFFFF', 'tx2': '#FFFFFF', 'tx3': '#FFFFFF', 'brd': '#E5D3E6', 'brd-lt': '#D3D6E6', 'bdg-user-pc': '#64AAFF', 'bdg-user-quest': '#38DC78', 'bdg-user-web': '#FFA726', 'bdg-user-friend': '#2DD48C', 'bdg-rank-visitor': '#CCCCCC', 'bdg-rank-new': '#1778FF', 'bdg-rank-user': '#2BCF5C', 'bdg-rank-known': '#FF7B42', 'bdg-rank-trusted': '#8143E6' }, light: true, cLight: { 'bg-base': '#EEF1FF', 'tx0': '#0B050E', 'tx1': '#49414E', 'tx2': '#3D3547', 'tx3': '#3B3441', 'accent': '#DFBFFF' } },
    ender:      { label: 'Ender',        dot: '#CC60FF', c: { 'bg-base': '#05040C', 'bg-side': '#05040C', 'bg-taskbar': '#05040C', 'bg-card': '#0A0714', 'bg-hover': '#191327', 'bg-input': '#110D1B', 'tab-card-bg': '#151120', 'ui-input-bg': '#110D1B', 'ui-input-hover-bg': '#191327', 'ui-input-active-bg': '#B960FF', 'badge-bg': '#4D3666', 'accent': '#CC60FF', 'accent-lt': '#9797B1', 'cyan': '#8CA5FF', 'ok': '#2DD48C', 'warn': '#FFBA37', 'err': '#FF4B55', 'tx0': '#EBEBFF', 'tx1': '#EBEBFF', 'tx2': '#B7B7C3', 'tx3': '#FFFFFF', 'brd': '#1C162C', 'brd-lt': '#1C162C', 'bdg-user-pc': '#E7B5FF', 'bdg-user-quest': '#E7B5FF', 'bdg-user-web': '#E7B5FF', 'bdg-user-friend': '#E7B5FF', 'bdg-rank-visitor': '#CCCCCC', 'bdg-rank-new': '#1778FF', 'bdg-rank-user': '#2BCF5C', 'bdg-rank-known': '#FF7B42', 'bdg-rank-trusted': '#8143E6' } },
};

const _LIGHT_VARS = ['bg-base', 'tx0', 'tx1', 'tx2', 'tx3', 'accent'];
let _activeLightOn = false;
let _activeLightColors = {};
let _activePrimaryColors = {};

function applyColors(c, light) {
    if (!c) return;
    for (const k of Object.keys(_activePrimaryColors)) {
        if (!(k in c)) document.documentElement.style.removeProperty('--' + k);
    }
    _activePrimaryColors = { ...c };
    _activeLightOn = !!(light && light.on);
    _activeLightColors = (light && light.colors) ? { ...light.colors } : {};
    for (const [k, v] of Object.entries(c)) document.documentElement.style.setProperty('--' + k, v);
    if (c['bg-card']) document.documentElement.style.setProperty('--bg-btn', c['bg-card']);
    if (c['bg-hover']) document.documentElement.style.setProperty('--bg-btn-h', c['bg-hover']);
    if (!c['tab-card-bg'] && c['bg-input']) document.documentElement.style.setProperty('--tab-card-bg', c['bg-input']);
    _applyLightBase();
    const logoEl = document.getElementById('logoIcon');
    if (logoEl && logoEl._repaintLogo) logoEl._repaintLogo();
    document.documentElement.dispatchEvent(new Event('themechange'));

    let overlayColors = c;
    if (_activeLightOn) {
        overlayColors = { ...c };
        for (const k of _LIGHT_VARS) if (_activeLightColors[k]) overlayColors[k] = _activeLightColors[k];
    }
    try { sendToCS({ action: 'overlayThemeColors', colors: overlayColors }); } catch {}
}

function _lerpHex(a, b, t) {
    const pa = [parseInt(a.slice(1, 3), 16), parseInt(a.slice(3, 5), 16), parseInt(a.slice(5, 7), 16)];
    const pb = [parseInt(b.slice(1, 3), 16), parseInt(b.slice(3, 5), 16), parseInt(b.slice(5, 7), 16)];
    return '#' + pa.map((x, i) => Math.round(x + (pb[i] - x) * t).toString(16).padStart(2, '0')).join('').toUpperCase();
}

function _lightScrollT() {
    const tab0 = document.getElementById('tab0');
    if (!tab0 || !tab0.classList.contains('active')) return 1;
    const content = document.querySelector('.content');
    return Math.min((content?.scrollTop || 0) / 140, 1);
}

const _LIGHT_SCOPE_IDS = ['tab0', 'taskbar', 'sidebarEl', 'rsidebar'];
let _lightSig = '';

function _lightEls() {
    const out = [];
    for (const id of _LIGHT_SCOPE_IDS) { const el = document.getElementById(id); if (el) out.push(el); }
    return out;
}

const _LIGHT_ONBG_PREFIX = '--dash-onbg-';

function _lightVarName(el, key) {
    return (el.id === 'tab0' && key !== 'bg-base') ? _LIGHT_ONBG_PREFIX + key : '--' + key;
}

function _lightClearVars(el) {
    for (const k of _LIGHT_VARS) {
        el.style.removeProperty('--' + k);
        el.style.removeProperty(_LIGHT_ONBG_PREFIX + k);
    }
}

function _applyLightBase() {
    const rs = document.documentElement.style;
    const drops = document.querySelectorAll('#taskbar .tb-dropdown');
    _lightSig = '';
    if (!_activeLightOn) {
        for (const el of _lightEls()) _lightClearVars(el);
        for (const d of drops) _lightClearVars(d);
        return;
    }
    for (const k of _LIGHT_VARS) {
        const lite = _activeLightColors[k];
        if (!lite) continue;
        rs.setProperty('--' + k, lite);
        for (const d of drops) d.style.setProperty('--' + k, lite);
    }
    _applyLightInterp();
}

function _applyLightInterp() {
    if (!_activeLightOn) return;
    const els  = _lightEls();
    const tab0 = document.getElementById('tab0');
    const onDash = tab0 && tab0.classList.contains('active');
    if (!onDash) {
        if (_lightSig === 'off') return;
        _lightSig = 'off';
        for (const el of els) _lightClearVars(el);
        return;
    }
    const lSide = document.getElementById('sidebarEl');
    const rSide = document.getElementById('rsidebar');
    const lCol = !lSide || lSide.classList.contains('collapsed');
    const rCol = !rSide || rSide.classList.contains('collapsed');
    const faded = [tab0];
    if (lSide && lCol) faded.push(lSide);
    if (rSide && rCol) faded.push(rSide);
    const tbEl = document.getElementById('taskbar');
    if (tbEl && lCol && rCol) faded.push(tbEl);
    const tq  = Math.round(_lightScrollT() * 40);
    const sig = tq + '|' + faded.map(e => e.id).join(',');
    if (sig === _lightSig) return;
    _lightSig = sig;
    for (const el of els) _lightClearVars(el);
    const t = tq / 40;
    for (const k of _LIGHT_VARS) {
        const prim = _activePrimaryColors[k];
        if (!prim) continue;
        const lite = _activeLightColors[k] || prim;
        const val = _lerpHex(prim, lite, t);
        for (const el of faded) el.style.setProperty(_lightVarName(el, k), val);
    }
    const logoEl = document.getElementById('logoIcon');
    if (logoEl && logoEl._repaintLogo) logoEl._repaintLogo();
}

let _lightRaf = 0;
document.addEventListener('scroll', function (e) {
    if (!_activeLightOn || !(e.target instanceof Element) || !e.target.classList.contains('content')) return;
    if (_lightRaf) return;
    _lightRaf = requestAnimationFrame(function () { _lightRaf = 0; _applyLightInterp(); });
}, { passive: true, capture: true });
document.documentElement.addEventListener('tabchange', function () {
    if (_activeLightOn) _applyLightInterp();
});

// Theme Editor.

const _TE_GROUPS = [
    { title: 'Main Colors', vars: [
        ['bg-base', 'Base BG'], ['bg-side', 'Sidebar BG'], ['bg-taskbar', 'Taskbar BG'],
    ]},
    { title: 'Accent Colors', vars: [
        ['bg-card', 'Card BG'], ['bg-hover', 'Hover BG'], ['bg-input', 'Input BG'], ['tab-card-bg', 'Tab Card BG'],
    ]},
    { title: 'Buttons', vars: [
        ['ui-input-bg', 'Buttons Base'], ['ui-input-hover-bg', 'Buttons Hover'], ['ui-input-active-bg', 'Buttons Active'], ['badge-bg', 'Badge Base'],
        ['accent', 'Accent'], ['accent-lt', 'Accent Light'], ['cyan', 'Highlight'],
    ]},
    { title: 'Border', vars: [
        ['brd', 'Border'], ['brd-lt', 'Border Light'],
    ]},
    { title: 'Text', vars: [
        ['tx0', 'Text 0'], ['tx1', 'Text 1'], ['tx2', 'Text 2'], ['tx3', 'Text 3'],
    ]},
    { title: 'Status', vars: [
        ['ok', 'Success'], ['err', 'Error'], ['warn', 'Warning'],
    ]},
    { title: 'Users', vars: [
        ['bdg-user-pc', 'PC Badge'], ['bdg-user-quest', 'Quest Badge'],
        ['bdg-user-web', 'Web Badge'], ['bdg-user-friend', 'Friend Badge'],
    ]},
    { title: 'Trusted Ranks', vars: [
        ['bdg-rank-visitor', 'Visitor Badge'], ['bdg-rank-new', 'New User Badge'], ['bdg-rank-user', 'User Badge'],
        ['bdg-rank-known', 'Known Badge'], ['bdg-rank-trusted', 'Trusted Badge'],
    ]},
];
const _TE_VARS = _TE_GROUPS.flatMap(g => g.vars);

let _teColors = {}, _teOrigColors = {}, _teSaved = false;
let _teLightOn = false, _teLightColors = {}, _teOrigLightOn = false, _teOrigLightColors = {};

function _teGetColor(v) {
    return (v.indexOf('lt:') === 0) ? _teLightColors[v.slice(3)] : _teColors[v];
}

function _teApply() {
    applyColors(_teColors, _teLightOn ? { on: true, colors: _teLightColors } : null);
}

function teToggleLight(on) {
    _teLightOn = on;
    _teRenderRows();
    _teApply();
}

function _teCssToHex(raw) {
    const s = (raw || '').trim();
    if (/^#[0-9A-Fa-f]{6}$/.test(s)) return s.toUpperCase();
    const m = s.match(/rgba?\((\d+),\s*(\d+),\s*(\d+)/);
    if (m) return '#' + [m[1],m[2],m[3]].map(n => parseInt(n).toString(16).padStart(2,'0')).join('').toUpperCase();
    return '#000000';
}

function openThemeEditor() {
    _teSaved = false;
    const style = getComputedStyle(document.documentElement);
    _teColors = {}; _teOrigColors = {};
    for (const [v] of _TE_VARS) {
        const hex = _activePrimaryColors[v] ? _teCssToHex(_activePrimaryColors[v]) : _teCssToHex(style.getPropertyValue('--' + v));
        _teColors[v] = _teOrigColors[v] = hex;
    }
    _teLightOn = _activeLightOn;
    _teLightColors = {};
    for (const k of _LIGHT_VARS) _teLightColors[k] = _activeLightColors[k] || _teColors[k];
    _teOrigLightOn = _activeLightOn;
    _teOrigLightColors = { ..._teLightColors };
    _teRenderRows();
    const panel = document.getElementById('themeEditorPanel');
    if (panel) panel.style.display = 'flex';
    document.getElementById('teThemeName').value = '';
    _tePickerInit();
}

function closeThemeEditor() {
    _tePickerClose();
    document.getElementById('themeEditorPanel').style.display = 'none';
    if (!_teSaved) applyColors(_teOrigColors, _teOrigLightOn ? { on: true, colors: _teOrigLightColors } : null);
}

function _teRenderRows() {
    const container = document.getElementById('teColorRows');
    if (!container) return;
    container.innerHTML = '';

    const toggleCard = document.createElement('div');
    toggleCard.className = 'te-section te-section-toggle';
    toggleCard.innerHTML =
        `<span class="te-section-title">Light Theme</span>` +
        `<label class="toggle"><input type="checkbox" id="teLightToggle" ${_teLightOn ? 'checked' : ''}>` +
        `<div class="toggle-track"><div class="toggle-knob"></div></div></label>`;
    toggleCard.querySelector('#teLightToggle').addEventListener('change', function () {
        teToggleLight(this.checked);
    });
    container.appendChild(toggleCard);

    for (const group of _TE_GROUPS) {
        const section = document.createElement('div');
        section.className = 'te-section';
        const header = document.createElement('div');
        header.className = 'te-section-title';
        header.textContent = group.title;
        section.appendChild(header);
        const grid = document.createElement('div');
        grid.className = 'te-grid';
        section.appendChild(grid);

        for (const [v, label] of group.vars) {
            const hex = _teColors[v];
            const showLight = _teLightOn && _LIGHT_VARS.includes(v);
            const lightHex = _teLightColors[v] || hex;
            const row = document.createElement('div');
            row.className = 'te-row';
            const lightSwatch = showLight
                ? `<div class="te-swatch te-swatch-light" id="teSwatch_lt:${v}" data-var="lt:${v}" title="Light color (top of dashboard)" style="background:${lightHex};"></div>`
                : '';
            row.innerHTML =
                `<span class="te-label" title="${label}">${label}</span>` +
                lightSwatch +
                `<div class="te-swatch" id="teSwatch_${v}" data-var="${v}" style="background:${hex};"></div>` +
                `<input type="text" class="vrcn-input te-hex" id="teHex_${v}" value="${hex}" maxlength="7" oninput="teSetColorFromHex('${v}',this.value)">`;
            row.querySelector(`#teSwatch_${v}`).addEventListener('click', function(e) {
                e.stopPropagation();
                _tePickerOpen(v, this);
            });
            if (showLight) {
                row.querySelector(`[data-var="lt:${v}"]`).addEventListener('click', function(e) {
                    e.stopPropagation();
                    _tePickerOpen('lt:' + v, this);
                });
            }
            grid.appendChild(row);
        }
        container.appendChild(section);
    }
}

function _teApplyColor(v, hex) {
    hex = hex.toUpperCase();
    if (v.indexOf('lt:') === 0) _teLightColors[v.slice(3)] = hex;
    else _teColors[v] = hex;
    const swatch = document.getElementById('teSwatch_' + v);
    const hexEl  = document.getElementById('teHex_' + v);
    if (swatch) swatch.style.background = hex;
    if (hexEl)  hexEl.value = hex;
    _teApply();
}

function teSetColorFromHex(v, raw) {
    if (!/^#[0-9A-Fa-f]{6}$/.test(raw)) return;
    _teApplyColor(v, raw);
    // Sync open picker if it's for this var
    if (_tePickerState.varName === v) { _tePickerSyncFromHex(raw.toUpperCase()); }
}

function teSaveTheme() {
    const name = (document.getElementById('teThemeName')?.value.trim()) || 'My Theme';
    const key = 'custom_' + Date.now();
    const theme = { key, label: name, dot: _teColors['accent'] || '#888888', c: { ..._teColors } };
    if (_teLightOn) {
        theme.light = true;
        theme.cLight = {};
        for (const k of _LIGHT_VARS) theme.cLight[k] = _teLightColors[k] || _teColors[k];
    }
    customThemes.push(theme);
    saveCustomColors();
    selectCustomTheme(key);
    renderThemeChips();
    saveSettings();
    _teSaved = true;
    closeThemeEditor();
}

// Custom Color Picker.

const _tePickerState = { varName: '', h: 0, s: 1, v: 1, draggingSV: false, draggingHue: false, inited: false, onPick: null, anchor: null };

function _teHsvToHex(h, s, v) {
    const f = n => { const k = (n + h / 60) % 6; return v - v * s * Math.max(0, Math.min(k, 4 - k, 1)); };
    return '#' + [f(5),f(3),f(1)].map(c => Math.round(c*255).toString(16).padStart(2,'0')).join('').toUpperCase();
}

function _teHexToHsv(hex) {
    const r = parseInt(hex.slice(1,3),16)/255, g = parseInt(hex.slice(3,5),16)/255, b = parseInt(hex.slice(5,7),16)/255;
    const max = Math.max(r,g,b), min = Math.min(r,g,b), d = max - min;
    let h = 0;
    if (d) {
        if (max===r) h = ((g-b)/d+6)%6;
        else if (max===g) h = (b-r)/d+2;
        else h = (r-g)/d+4;
        h *= 60;
    }
    return { h, s: max ? d/max : 0, v: max };
}

function _tePickerInit() {
    if (_tePickerState.inited) return;
    _tePickerState.inited = true;
    const svC  = document.getElementById('tePickerSV');
    const hueC = document.getElementById('tePickerHue');
    const picker = document.getElementById('teColorPicker');

    const svPos = e => {
        const r = svC.getBoundingClientRect();
        _tePickerState.s = Math.max(0, Math.min(1, (e.clientX - r.left) / r.width));
        _tePickerState.v = Math.max(0, Math.min(1, 1 - (e.clientY - r.top) / r.height));
        _tePickerCommit();
    };
    const huePos = e => {
        const r = hueC.getBoundingClientRect();
        _tePickerState.h = Math.max(0, Math.min(359.99, ((e.clientX - r.left) / r.width) * 360));
        _tePickerCommit();
    };

    svC.addEventListener('mousedown',  e => { _tePickerState.draggingSV  = true; svPos(e);  e.preventDefault(); });
    hueC.addEventListener('mousedown', e => { _tePickerState.draggingHue = true; huePos(e); e.preventDefault(); });
    document.addEventListener('mousemove', e => {
        if (_tePickerState.draggingSV)  svPos(e);
        if (_tePickerState.draggingHue) huePos(e);
    });
    document.addEventListener('mouseup', () => { _tePickerState.draggingSV = false; _tePickerState.draggingHue = false; });
    document.addEventListener('mousedown', e => {
        if (!picker.contains(e.target) && !e.target.dataset.var) _tePickerClose();
    });
}

function _tePickerOpen(varName, anchorEl) {
    if (_tePickerState.varName === varName && document.getElementById('teColorPicker').style.display !== 'none') {
        _tePickerClose(); return;
    }
    const hex = _teGetColor(varName) || '#888888';
    const hsv = _teHexToHsv(hex);
    Object.assign(_tePickerState, { varName, onPick: null, anchor: null, h: hsv.h, s: hsv.s, v: hsv.v });

    const picker  = document.getElementById('teColorPicker');
    const panel   = document.getElementById('themeEditorPanel');
    const pRect   = panel.getBoundingClientRect();
    picker.style.display = 'block';
    const pW = picker.offsetWidth || 220;
    const left = pRect.left - pW - 8;
    const aRect = anchorEl.getBoundingClientRect();
    const top = Math.min(Math.max(8, aRect.top - 10), window.innerHeight - (picker.offsetHeight || 300) - 8);
    picker.style.left = Math.max(8, left) + 'px';
    picker.style.top  = top + 'px';

    document.getElementById('tePickerHex').value = hex;
    document.getElementById('tePickerPreview').style.background = hex;
    _tePickerDraw();
}

function _tePickerClose() {
    document.getElementById('teColorPicker').style.display = 'none';
    _tePickerState.varName = '';
    _tePickerState.onPick = null;
    _tePickerState.anchor = null;
}

function tePickerOpenGeneric(hex, anchorEl, onPick) {
    const picker = document.getElementById('teColorPicker');
    if (_tePickerState.anchor === anchorEl && picker.style.display !== 'none') {
        _tePickerClose(); return;
    }
    _tePickerInit();
    const clean = /^#[0-9A-Fa-f]{6}$/.test(hex || '') ? hex.toUpperCase() : '#888888';
    const hsv = _teHexToHsv(clean);
    Object.assign(_tePickerState, { varName: '', onPick, anchor: anchorEl, h: hsv.h, s: hsv.s, v: hsv.v });
    picker.style.display = 'block';
    const aRect = anchorEl.getBoundingClientRect();
    const pW = picker.offsetWidth || 220, pH = picker.offsetHeight || 240;
    let left = aRect.left - pW - 10;
    if (left < 8) left = Math.min(aRect.right + 10, window.innerWidth - pW - 8);
    const top = Math.min(Math.max(8, aRect.top - 10), window.innerHeight - pH - 8);
    picker.style.left = Math.max(8, left) + 'px';
    picker.style.top  = top + 'px';
    document.getElementById('tePickerHex').value = clean;
    document.getElementById('tePickerPreview').style.background = clean;
    _tePickerDraw();
}

function _tePickerDraw() {
    _tePickerDrawSV();
    _tePickerDrawHue();
}

function _tePickerDrawSV() {
    const c = document.getElementById('tePickerSV');
    const ctx = c.getContext('2d');
    const W = c.width, H = c.height;
    ctx.fillStyle = _teHsvToHex(_tePickerState.h, 1, 1);
    ctx.fillRect(0, 0, W, H);
    const wg = ctx.createLinearGradient(0,0,W,0);
    wg.addColorStop(0, 'rgba(255,255,255,1)'); wg.addColorStop(1, 'rgba(255,255,255,0)');
    ctx.fillStyle = wg; ctx.fillRect(0,0,W,H);
    const bg = ctx.createLinearGradient(0,0,0,H);
    bg.addColorStop(0, 'rgba(0,0,0,0)'); bg.addColorStop(1, 'rgba(0,0,0,1)');
    ctx.fillStyle = bg; ctx.fillRect(0,0,W,H);
    // cursor
    const cx = _tePickerState.s * W, cy = (1 - _tePickerState.v) * H;
    ctx.beginPath(); ctx.arc(cx, cy, 6, 0, Math.PI*2);
    ctx.strokeStyle = '#fff'; ctx.lineWidth = 2; ctx.stroke();
    ctx.beginPath(); ctx.arc(cx, cy, 6, 0, Math.PI*2);
    ctx.strokeStyle = 'rgba(0,0,0,.4)'; ctx.lineWidth = 1; ctx.stroke();
}

function _tePickerDrawHue() {
    const c = document.getElementById('tePickerHue');
    const ctx = c.getContext('2d');
    const W = c.width, H = c.height;
    const g = ctx.createLinearGradient(0,0,W,0);
    for (let i = 0; i <= 12; i++) g.addColorStop(i/12, `hsl(${i*30},100%,50%)`);
    ctx.fillStyle = g; ctx.fillRect(0,0,W,H);
    // cursor
    const cx = (_tePickerState.h / 360) * W;
    ctx.strokeStyle = '#fff'; ctx.lineWidth = 2;
    ctx.strokeRect(cx-5, 0, 10, H);
    ctx.strokeStyle = 'rgba(0,0,0,.4)'; ctx.lineWidth = 1;
    ctx.strokeRect(cx-5, 0, 10, H);
}

function _tePickerCommit() {
    const hex = _teHsvToHex(_tePickerState.h, _tePickerState.s, _tePickerState.v);
    document.getElementById('tePickerHex').value = hex;
    document.getElementById('tePickerPreview').style.background = hex;
    _tePickerDraw();
    _tePickerApplyResult(hex);
}

function _tePickerApplyResult(hex) {
    if (_tePickerState.onPick) {
        try { _tePickerState.onPick(hex); } catch (e) { console.error('[picker]', e); }
        return;
    }
    _teApplyColor(_tePickerState.varName, hex);
}

function tePickerHexInput(raw) {
    if (!/^#[0-9A-Fa-f]{6}$/.test(raw)) return;
    _tePickerSyncFromHex(raw.toUpperCase());
    _tePickerApplyResult(raw.toUpperCase());
}

function _tePickerSyncFromHex(hex) {
    const hsv = _teHexToHsv(hex);
    Object.assign(_tePickerState, hsv);
    document.getElementById('tePickerPreview').style.background = hex;
    _tePickerDraw();
}

function tePickerEyedropper() {
    if (typeof sendToCS === 'function') { sendToCS({ action: 'screenPickColor' }); return; }
    _tePickerEyedropperFallback();
}

async function _tePickerEyedropperFallback() {
    if (!window.EyeDropper) return;
    try {
        const result = await new EyeDropper().open();
        _tePickerApplyHexResult(result.sRGBHex.toUpperCase());
    } catch {}
}

function _tePickerApplyHexResult(hex) {
    if (document.getElementById('teColorPicker').style.display === 'none') return;
    document.getElementById('tePickerHex').value = hex;
    _tePickerSyncFromHex(hex);
    _tePickerApplyResult(hex);
}

window.onScreenPickResult = function (payload) {
    if (!payload) return;
    if (payload.unsupported) { _tePickerEyedropperFallback(); return; }
    if (payload.cancelled || !payload.hex) return;
    _tePickerApplyHexResult(String(payload.hex).toUpperCase());
};

function getThemeLabel(key, fallback) {
    return t(`theme.${key}`, fallback);
}

function getPageTitle(i) {
    return [
        t('page.dashboard', 'Dashboard'),
        t('page.worlds', 'Worlds'),
        t('page.groups', 'Groups'),
        t('page.people', 'People'),
        t('page.avatars', 'Avatars'),
        t('page.custom_chatbox', 'Custom Chatbox'),
        t('page.media_relay', 'Media Relay'),
        t('page.media_library', 'Media Library'),
        t('page.activity_log', 'Activity Log'),
        t('page.settings', 'Settings'),
        t('page.space_flight', 'Space Flight'),
        t('page.osc_tool', 'OSC Tool'),
        t('page.timeline', 'Timeline'),
        t('page.inventory', 'Inventory'),
        t('page.youtube_fix', 'YouTube Fix'),
        t('page.mutual_network', 'Mutual Network'),
        t('page.time_spent', 'Time Spent'),
        t('page.calendar', 'Calendar'),
        t('page.voice_fight', 'Voice Fight'),
        t('page.discord_presence', 'Discord Presence'),
        t('page.vr_overlay', 'VR Overlay'),
        t('page.permini', 'Permini'),
        t('page.kikitan_xd', 'Kikitan XD'),
        t('page.event_snipe', 'Event Snipe'),
        t('page.avatar_scaling', 'Avatar Scaling'),
        t('page.action_flow', 'Action Flow'),
        t('page.frame_shot', 'FrameShot'),
        t('page.status_schedule', 'Status Schedule'),
        t('page.space_turn', 'Space Turn'),
        t('page.meet_network', 'Meet Network'),
    ][i] ?? '';
}

function updateCurrentPageTitle() {
    const activeTab = document.querySelector('.tab.active');
    if (!activeTab) return;
    const match = activeTab.id.match(/^tab(\d+)$/);
    if (!match) return;
    const pageTitle = document.getElementById('pageTitle');
    if (!pageTitle) return;
    const next = getPageTitle(parseInt(match[1], 10));
    if (pageTitle.textContent === next) return;
    const noAnim = document.documentElement.classList.contains('no-animations');
    const oldW = pageTitle.offsetWidth;
    pageTitle.textContent = next;
    pageTitle.classList.remove('tb-title-anim');
    void pageTitle.offsetWidth;
    pageTitle.classList.add('tb-title-anim');
    if (noAnim || !oldW) return;
    const newW = pageTitle.offsetWidth;
    if (newW === oldW) return;
    pageTitle.style.width = oldW + 'px';
    void pageTitle.offsetWidth;
    pageTitle.style.transition = 'width 0.18s ease';
    pageTitle.style.width = newW + 'px';
    const done = () => {
        pageTitle.style.transition = '';
        pageTitle.style.width = '';
        pageTitle.removeEventListener('transitionend', done);
    };
    pageTitle.addEventListener('transitionend', done);
}

const THEME_SKELETON_KEYS = ['bg-base', 'bg-side', 'bg-taskbar', 'bg-card', 'bg-input', 'accent', 'tx0', 'tx2', 'brd'];

function _themePreviewColors(theme) {
    const c = { ...(theme?.c || {}) };
    if (theme?.light && theme.cLight) {
        for (const k of _LIGHT_VARS) if (theme.cLight[k]) c[k] = theme.cLight[k];
    }
    return c;
}

function _liveThemeColors() {
    const style = getComputedStyle(document.documentElement);
    const c = {};
    THEME_SKELETON_KEYS.forEach(k => { c[k] = style.getPropertyValue('--' + k).trim(); });
    return c;
}

function themeSkeleton(c, accentOverride) {
    const v = k => esc(c[k] || '#000');
    const accent = accentOverride || v('accent');
    return `<span class="theme-skeleton" style="background:${v('bg-base')};border-color:${v('brd')}">`
        + `<span class="tsk-side" style="background:${v('bg-side')};border-color:${v('brd')}">`
            + `<span class="tsk-badge" style="background:${accent}"></span>`
            + `<span class="tsk-line w85" style="background:${v('tx2')}"></span>`
            + `<span class="tsk-line w70" style="background:${v('tx2')}"></span>`
            + `<span class="tsk-line w45" style="background:${v('tx2')}"></span>`
        + `</span>`
        + `<span class="tsk-main">`
            + `<span class="tsk-bar" style="background:${esc(c['bg-taskbar'] || c['bg-side'] || '#000')};border-color:${v('brd')}"></span>`
            + `<span class="tsk-body">`
                + `<span class="tsk-card" style="background:${v('bg-card')}">`
                    + `<span class="tsk-line w70" style="background:${v('tx0')}"></span>`
                    + `<span class="tsk-line w45" style="background:${v('tx2')}"></span>`
                    + `<span class="tsk-pill" style="background:${accent}"></span>`
                + `</span>`
                + `<span class="tsk-card" style="background:${v('bg-input')}">`
                    + `<span class="tsk-line w85" style="background:${v('tx2')}"></span>`
                    + `<span class="tsk-line w45" style="background:${v('tx2')}"></span>`
                + `</span>`
            + `</span>`
        + `</span>`
        + `</span>`;
}

function renderThemeChips() {
    const builtIn = Object.entries(THEMES).map(([k, th]) =>
        `<button class="theme-option${currentTheme === k ? ' active' : ''}" onclick="selectTheme('${k}')">`
        + themeSkeleton(_themePreviewColors(th))
        + `<span class="theme-option-name">${esc(getThemeLabel(k, th.label))}</span>`
        + `</button>`
    ).join('');
    const removeLabel = t('common.remove', 'Remove');
    const custom = customThemes.map(th =>
        `<button class="theme-option theme-chip-custom${currentTheme === th.key ? ' active' : ''}" data-ckey="${th.key}" onclick="selectCustomTheme('${th.key}')">`
        + themeSkeleton(_themePreviewColors(th))
        + `<span class="theme-option-name theme-chip-label">${esc(th.label)}</span>`
        + `<span class="theme-chip-del" onclick="event.stopPropagation();deleteCustomTheme('${th.key}')" title="${esc(removeLabel)}">×</span>`
        + `</button>`
    ).join('');
    const addBtn = currentSpecialTheme === 'auto'
        ? `<button class="theme-option theme-chip-add" onclick="addCustomThemeFromAuto()">`
          + themeSkeleton(_liveThemeColors())
          + `<span class="theme-option-name">${esc(t('settings.design.add_plus', 'Add +'))}</span>`
          + `</button>`
        : '';

    const themeGrid = document.getElementById('themeGrid');
    if (themeGrid) themeGrid.innerHTML = builtIn;

    const customGrid = document.getElementById('customThemeGrid');
    const customSection = document.getElementById('customThemeSection');
    const showCustom = customThemes.length > 0 || !!addBtn;
    if (customSection) customSection.style.display = showCustom ? 'block' : 'none';

    // Backward-compatible fallback: if the new grid doesn't exist, append custom chips into themeGrid.
    if (customGrid) customGrid.innerHTML = custom + addBtn;
    else if (themeGrid) themeGrid.innerHTML = builtIn + custom + addBtn;
}

const APP_FONTS = [
    { key: 'google-sans', label: 'Google Sans', stack: "'Google Sans', sans-serif" },
    { key: 'inter',       label: 'Inter',       stack: "'Inter', sans-serif" },
    { key: 'roboto',      label: 'Roboto',      stack: "'Roboto', sans-serif" },
    { key: 'open-sans',   label: 'Open Sans',   stack: "'Open Sans', sans-serif" },
    { key: 'lato',        label: 'Lato',        stack: "'Lato', sans-serif" },
    { key: 'montserrat',  label: 'Montserrat',  stack: "'Montserrat', sans-serif" },
    { key: 'poppins',     label: 'Poppins',     stack: "'Poppins', sans-serif" },
    { key: 'nunito',      label: 'Nunito',      stack: "'Nunito', sans-serif" },
    { key: 'rubik',       label: 'Rubik',       stack: "'Rubik', sans-serif" },
    { key: 'manrope',     label: 'Manrope',     stack: "'Manrope', sans-serif" },
    { key: 'quicksand',   label: 'Quicksand',   stack: "'Quicksand', sans-serif" },
];
const APP_FONT_DEFAULT = 'google-sans';

function getAppFontStack(key) {
    const f = APP_FONTS.find(x => x.key === key);
    return (f || APP_FONTS[0]).stack;
}

function applyAppFont(key) {
    currentAppFont = APP_FONTS.some(f => f.key === key) ? key : APP_FONT_DEFAULT;
    _pushFontStack();
}

function _pushFontStack() {
    const stack = currentCustomFont
        ? `"${currentCustomFont.replace(/"/g, '')}", ${getAppFontStack(currentAppFont)}`
        : getAppFontStack(currentAppFont);
    document.documentElement.style.setProperty('--font-app', stack);
}

function applyCustomFont(name) {
    currentCustomFont = name || '';
    _pushFontStack();
    renderCustomFontOptions();
}

function applyFontSizeOffset(px) {
    const n = Math.max(-5, Math.min(5, parseInt(px, 10) || 0));
    currentFontSizeOffset = n;
    document.documentElement.style.setProperty('--fs-off', n + 'px');
    const slider = document.getElementById('setFontSize');
    if (slider && String(slider.value) !== String(n)) slider.value = n;
    const label = document.getElementById('fontSizeVal');
    if (label) label.textContent = (n > 0 ? '+' : '') + n + ' px';
}

function setFontSizeOffset(px) {
    applyFontSizeOffset(px);
    autoSave();
}

function applyTaskbarHeight(px) {
    const n = Math.max(36, Math.min(48, parseInt(px, 10) || 42));
    currentTaskbarHeight = n;
    document.documentElement.style.setProperty('--tb-h', n + 'px');
    const slider = document.getElementById('setTaskbarHeight');
    if (slider && String(slider.value) !== String(n)) slider.value = n;
    const label = document.getElementById('taskbarHeightVal');
    if (label) label.textContent = n + ' px';
}

function setTaskbarHeight(px) {
    applyTaskbarHeight(px);
    autoSave();
}

function setCustomFont(name) {
    applyCustomFont(name);
    autoSave();
}

function renderCustomFontOptions() {
    const sel = document.getElementById('setCustomFont');
    if (!sel) return;
    const none = t('settings.design.fonts.custom_none', 'None');
    let html = `<option value="">${esc(none)}</option>`;
    for (const f of _systemFonts) {
        html += `<option value="${esc(f)}" data-vn-font="${esc(f)}">${esc(f)}</option>`;
    }
    sel.innerHTML = html;
    sel.value = _systemFonts.includes(currentCustomFont) ? currentCustomFont : '';
    if (sel._vnRefresh) sel._vnRefresh();
}

function loadSystemFonts(list) {
    _systemFonts = Array.isArray(list) ? list : [];
    renderCustomFontOptions();
}

let _fontPreviewObserver = null;

function renderFontGrid() {
    const grid = document.getElementById('fontGrid');
    if (!grid) return;
    grid.innerHTML = APP_FONTS.map(f =>
        `<button class="font-option${currentAppFont === f.key ? ' active' : ''}" data-font="${f.key}" data-stack="${esc(f.stack)}" onclick="selectAppFont('${f.key}')">`
        + `<span class="font-preview">Aa</span>`
        + `<span class="font-option-name">${esc(f.label)}</span>`
        + `</button>`
    ).join('');

    if (!_fontPreviewObserver) {
        _fontPreviewObserver = new IntersectionObserver(entries => {
            entries.forEach(e => {
                if (!e.isIntersecting) return;
                const btn = e.target;
                const prev = btn.querySelector('.font-preview');
                if (prev) prev.style.fontFamily = btn.dataset.stack;
                _fontPreviewObserver.unobserve(btn);
            });
        }, { rootMargin: '80px' });
    }
    grid.querySelectorAll('.font-option').forEach(el => _fontPreviewObserver.observe(el));
}

function selectAppFont(key) {
    currentCustomFont = '';
    applyAppFont(key);
    renderCustomFontOptions();
    document.querySelectorAll('#fontGrid .font-option').forEach(el => {
        el.classList.toggle('active', el.dataset.font === currentAppFont);
    });
    autoSave();
}

function selectTheme(n) {
    if (!THEMES[n]) n = 'vrcn';
    currentTheme = n;
    if (currentSpecialTheme === 'auto') applyAutoColor();
    else applyColors(THEMES[n].c, THEMES[n].light ? { on: true, colors: THEMES[n].cLight } : null);
    renderThemeChips();
    renderSpecialThemeChips();
    autoSave();
}

function selectCustomTheme(key) {
    const t = customThemes.find(x => x.key === key);
    if (!t) return;
    currentTheme = key;
    currentSpecialTheme = '';
    applyColors(t.c, t.light ? { on: true, colors: t.cLight } : null);
    renderThemeChips();
    renderSpecialThemeChips();
    const row = document.getElementById('autoAccuracyRow');
    if (row) row.style.display = 'none';
    autoSave();
}

function deleteCustomTheme(key) {
    customThemes = customThemes.filter(t => t.key !== key);
    if (currentTheme === key) { currentTheme = 'vrcn'; applyColors(THEMES.vrcn.c); }
    renderThemeChips();
    saveCustomColors();
}

function addCustomThemeFromAuto() {
    const style = getComputedStyle(document.documentElement);
    const colorKeys = ['bg-base','bg-side','bg-taskbar','bg-card','bg-hover','bg-input','ui-input-bg','ui-input-hover-bg','ui-input-active-bg','badge-bg','accent','accent-lt','cyan','ok','warn','err','tx0','tx1','tx2','tx3','brd','brd-lt'];
    const c = {};
    colorKeys.forEach(k => { c[k] = style.getPropertyValue('--' + k).trim(); });
    const dot = c['accent'] || '#3884FF';
    const key = 'custom_' + Date.now();
    customThemes.push({ key, label: t('theme.custom', 'Custom'), dot, c });
    renderThemeChips();
    // Enter inline name-edit mode on the new chip
    const chip = document.querySelector(`.theme-chip-custom[data-ckey="${key}"]`);
    if (!chip) return;
    const labelEl = chip.querySelector('.theme-chip-label');
    if (!labelEl) return;
    // Disable the chip's onclick so Space/Enter can't trigger selectCustomTheme while editing
    chip.onclick = null;
    const input = document.createElement('input');
    input.type = 'text';
    input.value = t('theme.custom', 'Custom');
    input.className = 'theme-chip-name-input';
    input.onclick = e => e.stopPropagation();
    let saved = false;
    const finalize = () => {
        if (saved) return; saved = true;
        const name = input.value.trim() || 'Custom';
        const theme = customThemes.find(t => t.key === key);
        if (theme) theme.label = name;
        renderThemeChips();
        saveCustomColors();
    };
    input.addEventListener('blur', finalize);
    input.addEventListener('keydown', e => {
        if (e.key === 'Enter') { e.preventDefault(); input.blur(); }
        if (e.key === 'Escape') { input.value = 'Custom'; input.blur(); }
    });
    labelEl.replaceWith(input);
    input.select();
    input.focus();
}

function saveCustomColors() {
    sendToCS({ action: 'saveCustomColors', themes: customThemes });
}

function loadCustomThemes(data) {
    customThemes = Array.isArray(data?.themes) ? data.themes : [];
    renderThemeChips();
    // If the saved currentTheme is a custom key, apply it now that we have the data
    if (currentTheme && currentTheme.startsWith('custom_')) {
        const t = customThemes.find(x => x.key === currentTheme);
        if (t) applyColors(t.c, t.light ? { on: true, colors: t.cLight } : null);
        else { currentTheme = 'vrcn'; applyColors(THEMES.vrcn.c); renderThemeChips(); }
    }
}

// Auto Color.
function _rgbToHsl(r, g, b) {
    r /= 255; g /= 255; b /= 255;
    const max = Math.max(r, g, b), min = Math.min(r, g, b);
    let h = 0, s = 0, l = (max + min) / 2;
    if (max !== min) {
        const d = max - min;
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        switch (max) {
            case r: h = ((g - b) / d + (g < b ? 6 : 0)) / 6; break;
            case g: h = ((b - r) / d + 2) / 6; break;
            case b: h = ((r - g) / d + 4) / 6; break;
        }
    }
    return [h * 360, s * 100, l * 100];
}

function _hslToHex(h, s, l) {
    h = ((h % 360) + 360) % 360; s = Math.max(0, Math.min(100, s)); l = Math.max(0, Math.min(100, l));
    h /= 360; s /= 100; l /= 100;
    let r, g, b;
    if (s === 0) { r = g = b = l; } else {
        const q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        const f = (t) => { t = ((t % 1) + 1) % 1; if (t < 1/6) return p + (q-p)*6*t; if (t < 1/2) return q; if (t < 2/3) return p + (q-p)*(2/3-t)*6; return p; };
        r = f(h + 1/3); g = f(h); b = f(h - 1/3);
    }
    return '#' + [r, g, b].map(x => Math.round(x * 255).toString(16).padStart(2, '0')).join('');
}

function _buildAutoTheme(bgHue, accentHue, accentLit, imgSat) {
    // accuracy 0-100, default 50 = current behavior
    // Below 50: only saturation drops (lightness stays) → muted/grey, clearly different
    // Above 50: lightness rises + saturation rises → vivid, color-accurate
    const t = autoColorAccuracy / 100;
    const lMult = t < 0.5 ? 1.0 : 1.0 + (t - 0.5) * 1.6; // 0%→1.0, 50%→1.0, 100%→1.8
    const sMult = 0.2 + t * 1.6;                            // 0%→0.2, 50%→1.0, 100%→1.8
    const bs = Math.max(14, Math.min(95, Math.round(Math.max(55, Math.min(82, imgSat * 1.25)) * sMult)));
    const al = Math.max(52, Math.min(65, accentLit));
    return {
        'bg-base':   _hslToHex(bgHue, bs*0.92, 4.5  * lMult),
        'bg-side':   _hslToHex(bgHue, bs*0.88, 7.0  * lMult),
        'bg-taskbar': _hslToHex(bgHue, bs*0.88, 7.0  * lMult),
        'bg-card':   _hslToHex(bgHue, bs*0.78, 10.5 * lMult),
        'bg-hover':  _hslToHex(bgHue, bs*0.68, 15.5 * lMult),
        'bg-input':  _hslToHex(bgHue, bs*0.78, 10.5 * lMult), 'tab-card-bg': _hslToHex(bgHue, bs*0.78, 10.5 * lMult),
        'accent':    _hslToHex(accentHue, 74, al),
        'accent-lt': _hslToHex(accentHue, 66, al + 13),
        'cyan':      _hslToHex((accentHue + 28) % 360, 66, 60),
        'ok':        '#2DD48C',
        'warn':      '#FFBA37',
        'err':       '#FF4B55',
        'tx0':       _hslToHex(bgHue, 22, 95),
        'tx1':       _hslToHex(bgHue, 18, 83),
        'tx2':       _hslToHex(bgHue, 20, 54),
        'tx3':       _hslToHex(bgHue, 22, 32),
        'brd':       _hslToHex(bgHue, bs*0.68, 17 * lMult),
        'brd-lt':    _hslToHex(bgHue, bs*0.58, 23 * lMult),
    };
}

function setAutoColorAccuracy(val) {
    autoColorAccuracy = parseInt(val) || 50;
    if (currentSpecialTheme === 'auto') applyAutoColor();
}

function applyAutoColor() {
    const url = (typeof dashBgSample !== 'undefined' && dashBgSample)
        || (typeof dashBgDataUri !== 'undefined' && dashBgDataUri) || '';
    if (!url) {
        if (typeof dashBgPath !== 'undefined' && dashBgPath) sendToCS({ action: 'vrcLoadDashBg', path: dashBgPath });
        return;
    }

    const img = new Image();
    img.crossOrigin = 'anonymous';
    img.onload = () => {
        try {
            const SIZE = 80;
            const cv = document.createElement('canvas');
            cv.width = cv.height = SIZE;
            const ctx = cv.getContext('2d');
            ctx.drawImage(img, 0, 0, SIZE, SIZE);
            const data = ctx.getImageData(0, 0, SIZE, SIZE).data;

            const buckets = new Array(36).fill(0);
            let bestSat = 0, accentHue = 210, accentLit = 55;
            let colorful = 0, satSum = 0;

            for (let i = 0; i < data.length; i += 12) {
                const [h, s, l] = _rgbToHsl(data[i], data[i+1], data[i+2]);
                // Lower thresholds: capture dark-but-colored pixels (e.g. deep ocean blues)
                if (s < 5 || l < 3 || l > 93) continue;
                colorful++;
                satSum += s;
                buckets[Math.floor(h / 10) % 36]++;
                // Accent: most vibrant pixel with visible brightness
                if (s > bestSat && l > 18 && l < 75) { bestSat = s; accentHue = h; accentLit = l; }
            }

            if (colorful < 20) { applyColors(_buildAutoTheme(210, 210, 55, 55)); return; }

            const avgSat = satSum / colorful;

            // Smooth hue histogram (±1 bucket) and find peak
            let domBucket = 0, domCount = 0;
            for (let i = 0; i < 36; i++) {
                const c = buckets[(i+35)%36] + buckets[i]*2 + buckets[(i+1)%36];
                if (c > domCount) { domCount = c; domBucket = i; }
            }
            const domHue = domBucket * 10 + 5;
            if (bestSat < 15) { accentHue = domHue; accentLit = 55; }

            applyColors(_buildAutoTheme(domHue, accentHue, accentLit, avgSat));
        } catch (e) { console.warn('[AutoColor]', e); }
    };
    img.src = url;
}

function renderSpecialThemeChips() {
    const el = document.getElementById('specialThemeGrid');
    if (!el) return;
    const live = _liveThemeColors();
    const chips = [
        { key: '',     label: t('settings.design.special.standard', 'Standard'), accent: null },
        { key: 'auto', label: t('settings.design.special.auto_color', 'Auto Color'),
          accent: 'conic-gradient(red,yellow,lime,cyan,blue,magenta,red)' },
    ];
    el.innerHTML = chips.map(ch =>
        `<button class="theme-option${currentSpecialTheme === ch.key ? ' active' : ''}" onclick="applySpecialTheme('${ch.key}')">`
        + themeSkeleton(live, ch.accent)
        + `<span class="theme-option-name">${esc(ch.label)}</span>`
        + `</button>`
    ).join('');
}

function applySpecialTheme(n) {
    currentSpecialTheme = n;
    if (n === 'auto') applyAutoColor();
    else {
        const ct = customThemes.find(t => t.key === currentTheme);
        if (!ct && !THEMES[currentTheme]) currentTheme = 'vrcn';
        const bt = THEMES[currentTheme];
        const lightSrc = bt?.light ? bt : (ct?.light ? ct : null);
        applyColors(bt?.c ?? ct?.c, lightSrc ? { on: true, colors: lightSrc.cLight } : null);
    }
    renderSpecialThemeChips();
    renderThemeChips(); // show/hide Add + button
    const row = document.getElementById('autoAccuracyRow');
    if (row) row.style.display = n === 'auto' ? 'flex' : 'none';
    autoSave();
}


function renderCursorThemeChips(files) {
    if (files) _cursorFiles = files;
    const el = document.getElementById('cursorThemeGrid');
    if (!el) return;
    const all = [{ key: '', label: t('common.standard', 'Standard'), url: null }, ..._cursorFiles.map(f => ({ key: f, label: f.replace(/\.[^.]+$/, ''), url: _localHttpPort ? `http://localhost:${_localHttpPort}/cursor/${encodeURIComponent(f)}` : null }))];
    el.innerHTML = all.map(t =>
        `<button class="theme-chip${currentCursorTheme === t.key ? ' active' : ''}" onclick="applyCursorTheme('${t.key}')">${t.url ? `<img src="${t.url}" style="width:18px;height:18px;object-fit:contain;image-rendering:pixelated;margin-right:4px;vertical-align:middle;">` : `<span class="theme-dot" style="background:var(--tx3)"></span>`}${t.label}</button>`
    ).join('');
}

function applyCursorTheme(key) {
    currentCursorTheme = key;
    if (key && _localHttpPort) {
        document.documentElement.style.setProperty('--cursor-url', `url('http://localhost:${_localHttpPort}/cursor/${encodeURIComponent(key)}') 0 0, auto`);
        document.documentElement.classList.add('cursor-custom');
    } else {
        document.documentElement.classList.remove('cursor-custom');
        document.documentElement.style.removeProperty('--cursor-url');
    }
    renderCursorThemeChips();
    autoSave();
}

function renderCustomThemesList() {
    const el = document.getElementById('customThemesList');
    if (!el) return;
    if (!_customThemes.length) {
        el.innerHTML = `<div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx3);padding:8px 0;" data-i18n="settings.design.themes.empty">No themes found. Drop a folder with CSS files into the custom-themes folder.</div>`;
        return;
    }
    el.innerHTML = _customThemes.map(th => {
        const on = _activeCustomThemes.has(th.id);
        const meta = [th.author ? `by ${esc(th.author)}` : '', th.version ? `v${esc(th.version)}` : ''].filter(Boolean).join(' · ');
        return `<div class="sf-toggle-row" style="background:var(--bg-input);border-radius:8px;padding:10px 14px;">
            <div>
                <div style="font-size:calc(13px + var(--fs-off, 0px));font-weight:600;color:var(--tx1);">${esc(th.name)}</div>
                ${meta ? `<div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx3);margin-top:2px;">${meta}</div>` : ''}
            </div>
            <label class="toggle"><input type="checkbox" ${on ? 'checked' : ''} onchange="toggleCustomTheme('${esc(th.id)}',this.checked)"><div class="toggle-track"><div class="toggle-knob"></div></div></label>
        </div>`;
    }).join('');
}

function _ctHref(th, file) {
    const route = th.builtIn ? 'builtinthemes' : 'customthemes';
    return `http://localhost:${_localHttpPort}/${route}/${encodeURIComponent(th.name)}/${encodeURIComponent(file)}`;
}

function _ctInjectTheme(th) {
    (th.cssFiles || []).forEach(f => {
        const id = 'ct_' + th.id + '_' + f.replace(/[^a-z0-9]/gi, '_');
        if (document.getElementById(id)) return;
        const link = document.createElement('link');
        link.rel = 'stylesheet'; link.id = id;
        link.href = _ctHref(th, f);
        document.head.appendChild(link);
    });
    (th.jsFiles || []).forEach(f => {
        const id = 'ct_js_' + th.id + '_' + f.replace(/[^a-z0-9]/gi, '_');
        if (document.getElementById(id)) return;
        const script = document.createElement('script');
        script.id = id;
        script.src = _ctHref(th, f);
        document.head.appendChild(script);
    });
}

function _ctRemoveTheme(th) {
    document.documentElement.dispatchEvent(new CustomEvent('vrcnext:theme:unload:' + th.id));
    (th.cssFiles || []).forEach(f => document.getElementById('ct_' + th.id + '_' + f.replace(/[^a-z0-9]/gi, '_'))?.remove());
    (th.jsFiles  || []).forEach(f => document.getElementById('ct_js_' + th.id + '_' + f.replace(/[^a-z0-9]/gi, '_'))?.remove());
}

function toggleCustomTheme(id, enabled) {
    const theme = _customThemes.find(t => t.id === id);
    if (enabled) {
        _activeCustomThemes.add(id);
        if (theme) _ctInjectTheme(theme);
    } else {
        _activeCustomThemes.delete(id);
        if (theme) _ctRemoveTheme(theme);
    }
    autoSave();
}

function applyCustomThemesFromSettings(activeIds) {
    _activeCustomThemes = new Set(activeIds || []);
    _customThemes.forEach(th => {
        if (_activeCustomThemes.has(th.id)) _ctInjectTheme(th);
        else _ctRemoveTheme(th);
    });
    renderCustomThemesList();
}

function tryLoadLogo() {
    const i = new Image();
    i.onload = () => {
        const el = document.getElementById('logoIcon');
        el.textContent = '';
        el.style.background = 'transparent';
        const canvas = document.createElement('canvas');
        const dpr = window.devicePixelRatio || 1;
        canvas.width = 34 * dpr; canvas.height = 34 * dpr;
        canvas.style.cssText = 'width:100%;height:100%;';
        el.appendChild(canvas);
        el._repaintLogo = () => {
            const accent = getComputedStyle(document.documentElement).getPropertyValue('--accent').trim() || '#3884FF';
            const ctx = canvas.getContext('2d');
            ctx.imageSmoothingEnabled = true;
            ctx.imageSmoothingQuality = 'high';
            ctx.clearRect(0, 0, canvas.width, canvas.height);
            ctx.fillStyle = accent;
            ctx.fillRect(0, 0, canvas.width, canvas.height);
            ctx.globalCompositeOperation = 'destination-in';
            ctx.drawImage(i, 0, 0, canvas.width, canvas.height);
            ctx.globalCompositeOperation = 'source-over';
        };
        el._repaintLogo();
    };
    i.src = 'logo.png';
}

const SOUND_LIBRARY = [
    'Amogus.wav', 'Bing.wav', 'Clicker.wav', 'Cryo.wav', 'Food Finished.wav',
    'Fooley.wav', 'Insert Disk.wav', 'Kalimba Bell.wav', 'Karambola.wav', 'Ladida.wav',
    'Plug.wav', 'Soft Lace.wav', 'Solana.wav', 'Sweep.wav', 'Waum.wav',
];

const SOUND_SLOTS = {
    notify:       { def: 'Notification.wav', fileKey: 'notifySoundFile',       volKey: 'notifySoundVolume' },
    message:      { def: 'Message.wav',      fileKey: 'messageSoundFile',      volKey: 'messageSoundVolume' },
    mediaRelay:   { def: 'MediaRelay.wav',   fileKey: 'mediaRelaySoundFile',   volKey: 'mediaRelaySoundVolume' },
    steamOverlay: { def: 'SteamOverlay.wav', fileKey: 'steamOverlaySoundFile', volKey: 'steamOverlaySoundVolume' },
};

function soundFileUrl(file, defaultFile) {
    if (!file) return 'sounds/notifications/' + encodeURIComponent(defaultFile);
    return 'sounds/notifications/Notificationsv2/' + encodeURIComponent(file);
}

function soundSlotVolume(slot) {
    const raw = settings?.[SOUND_SLOTS[slot].volKey];
    const n = Number.isFinite(raw) ? raw : 50;
    return Math.min(100, Math.max(0, n)) / 100;
}

function _initAudio(path, volume) {
    const a = new Audio(path);
    a.volume = typeof volume === 'number' ? volume : 0.5;
    a._ready = false;
    a.addEventListener('canplaythrough', () => { a._ready = true; }, { once: true });
    a.addEventListener('error', () => { a._ready = false; });
    a.load();
    return a;
}

function _initSlotAudio(slot) {
    const cfg = SOUND_SLOTS[slot];
    return _initAudio(soundFileUrl(settings?.[cfg.fileKey], cfg.def), soundSlotVolume(slot));
}

function tryInitNotifySound() {
    notifyAudio = _initSlotAudio('notify');
    messageAudio = _initSlotAudio('message');
    mediaRelayAudio = _initSlotAudio('mediaRelay');
    steamOverlayAudio = _initSlotAudio('steamOverlay');
    waterAudio = _initAudio('sounds/notifications/water.wav');
}

function applySoundSettings() {
    notifyAudio = _initSlotAudio('notify');
    messageAudio = _initSlotAudio('message');
    mediaRelayAudio = _initSlotAudio('mediaRelay');
    steamOverlayAudio = _initSlotAudio('steamOverlay');
}

let _sndPreviewAudio = null;

function previewSound(slot, file) {
    const cfg = SOUND_SLOTS[slot];
    if (!cfg) return;
    if (_sndPreviewAudio) { try { _sndPreviewAudio.pause(); } catch {} }
    _sndPreviewAudio = new Audio(soundFileUrl(file, cfg.def));
    _sndPreviewAudio.volume = soundSlotVolume(slot);
    _sndPreviewAudio.play().catch(() => {});
}

function playNotificationSound() {
    if (notifyAudio?._ready && settings.notifySoundEnabled) {
        notifyAudio.volume = soundSlotVolume('notify');
        notifyAudio.currentTime = 0;
        notifyAudio.play().catch(() => {});
    }
}

function playMessageSound() {
    if (messageAudio?._ready && settings.messageSoundEnabled) {
        messageAudio.volume = soundSlotVolume('message');
        messageAudio.currentTime = 0;
        messageAudio.play().catch(() => {});
    }
}

function playMediaRelaySound() {
    if (mediaRelayAudio?._ready && settings.mediaRelaySoundEnabled) {
        mediaRelayAudio.volume = soundSlotVolume('mediaRelay');
        mediaRelayAudio.currentTime = 0;
        mediaRelayAudio.play().catch(() => {});
    }
}

function playSteamOverlaySound() {
    if (steamOverlayAudio?._ready && settings.steamOverlaySoundEnabled) {
        steamOverlayAudio.volume = soundSlotVolume('steamOverlay');
        steamOverlayAudio.currentTime = 0;
        steamOverlayAudio.play().catch(() => {});
    }
}

function sndPreviewNotify(file)       { previewSound('notify', file); }
function sndPreviewMessage(file)      { previewSound('message', file); }
function sndPreviewMediaRelay(file)   { previewSound('mediaRelay', file); }
function sndPreviewSteamOverlay(file) { previewSound('steamOverlay', file); }


let _clockEnabled = false;
let _dateEnabled = false;
let _showVrcPlus = true;
let _showVrcCredits = true;
let _hasVrcPlus = false;
let _hasVrcCredits = false;

function applyClockSettings() {
    const enableEl  = document.getElementById('setClockEnabled');
    const dateEl    = document.getElementById('setDateEnabled');
    const vrcpEl    = document.getElementById('setShowVrcPlus');
    const creditsEl = document.getElementById('setShowVrcCredits');
    if (enableEl)  _clockEnabled   = enableEl.checked;
    if (dateEl)    _dateEnabled    = dateEl.checked;
    if (vrcpEl)    _showVrcPlus    = vrcpEl.checked;
    if (creditsEl) _showVrcCredits = creditsEl.checked;
    const anyClock = _clockEnabled || _dateEnabled;
    const clockEl = document.getElementById('tbClock');
    if (clockEl) clockEl.style.display = anyClock ? '' : 'none';
    const sepEl = document.getElementById('tbClockSep');
    if (sepEl) sepEl.style.display = anyClock ? '' : 'none';
    const timeEl = document.getElementById('clock');
    if (timeEl) timeEl.style.display = _clockEnabled ? '' : 'none';
    const dEl = document.getElementById('clockDate');
    if (dEl) dEl.style.display = _dateEnabled ? '' : 'none';
    applyTbBadgeVisibility();
    updateClock();
}

function applyTbBadgeVisibility() {
    const vp = document.getElementById('badgeVrcPlus');
    if (vp) vp.style.display = (_showVrcPlus && _hasVrcPlus) ? '' : 'none';
    const bc = document.getElementById('badgeVrcCredits');
    if (bc) bc.style.display = (_showVrcCredits && _hasVrcCredits) ? '' : 'none';
}

function updateClock() {
    if (!_clockEnabled && !_dateEnabled) return;
    const n = new Date();
    const timeEl = document.getElementById('clock');
    const dateEl = document.getElementById('clockDate');
    if (timeEl && _clockEnabled) timeEl.textContent = fmtTime(n);
    if (dateEl && _dateEnabled) dateEl.textContent = fmtShortDate(n);
}

function toggleNavGroup(id) {
    const group = document.getElementById(id);
    if (!group) return;
    if (typeof navIsModernFolders === 'function' && navIsModernFolders()) {
        openNavFolderPopout(id, group.querySelector('.nav-group-btn'));
        return;
    }
    group.classList.toggle('collapsed');
    localStorage.setItem('vrcnext_navgroup_' + id, group.classList.contains('collapsed') ? '1' : '0');
}

const _LAZY_PH = 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7';

function _unloadTabImages(tabEl) {
    let count = 0;
    tabEl.querySelectorAll('img').forEach(img => {
        const s = img.src;
        if (!s || s === _LAZY_PH || img.dataset.lazySrc || img.classList.contains('lazy-keep')) return;
        if (!s.startsWith('http://localhost:')) return;
        img.dataset.lazySrc = s;
        img.src = _LAZY_PH;
        count++;
    });
    tabEl.querySelectorAll('[style*="background-image"]').forEach(el => {
        const bg = el.style.backgroundImage;
        if (!bg || bg === 'none' || el.dataset.lazyBg || el.classList.contains('lazy-keep')) return;
        if (bg.indexOf('http://localhost:') === -1) return;
        el.dataset.lazyBg = bg;
        el.style.backgroundImage = `url('${_LAZY_PH}')`;
        count++;
    });
    if (count > 0) {
        const tabIdx = Array.from(document.querySelectorAll('.tab')).indexOf(tabEl);
        const label = tabIdx >= 0 ? `Tab ${tabIdx}` : (tabEl.id || 'modal');
        addLog(`[Unload] Unloaded ${count} image${count !== 1 ? 's' : ''} from ${label} from memory.`, 'info');
    }
}

function _reloadTabImages(tabEl) {
    tabEl.querySelectorAll('img[data-lazy-src]').forEach(img => {
        img.src = img.dataset.lazySrc;
        delete img.dataset.lazySrc;
    });
    tabEl.querySelectorAll('[data-lazy-bg]').forEach(el => {
        el.style.backgroundImage = el.dataset.lazyBg;
        delete el.dataset.lazyBg;
    });
}


function showTab(i) {
    clearTimeout(_lazyUnloadTimer);

    const _tabs = document.querySelectorAll('.tab');
    const _prevTabEl = (_prevTab >= 0 && _prevTab !== i) ? _tabs[_prevTab] : null;
    const _nextTabEl = _tabs[i];

    if (_nextTabEl) _reloadTabImages(_nextTabEl);

    if (_prevTab === 7 && i !== 7) destroyLibrary();
    _prevTab = i;
    document.querySelectorAll('.tab').forEach((t, j) => t.classList.toggle('active', j === i));
    const contentEl = document.querySelector('.content');
    if (contentEl) {
        contentEl.classList.toggle('tab1-active', i === 1);
        contentEl.classList.toggle('tab2-active', i === 2);
        contentEl.classList.toggle('tab3-active', i === 3);
        contentEl.classList.toggle('tab4-active', i === 4);
        contentEl.classList.toggle('tab7-active', i === 7);
        contentEl.classList.toggle('tab12-active', i === 12);
        contentEl.classList.toggle('tab16-active', i === 16);
    }
    // Clear active from all nav-btns (including sub-items and group headers)
    document.querySelectorAll('#sidebarEl .nav-btn').forEach(b => b.classList.remove('active'));
    document.querySelectorAll('.nav-group').forEach(g => g.classList.remove('has-active'));
    // Find and activate the correct button by matching tab index
    const allBtns = document.querySelectorAll('#sidebarEl .nav-btn[onclick]');
    allBtns.forEach(b => {
        const match = b.getAttribute('onclick')?.match(/showTab\((\d+)\)/);
        if (match && parseInt(match[1]) === i) {
            b.classList.add('active');
            // If it's a sub-item, mark parent group and auto-expand
            const parentGroup = b.closest('.nav-group');
            if (parentGroup) {
                parentGroup.classList.add('has-active');
                parentGroup.classList.remove('collapsed');
            }
        }
    });
    updateCurrentPageTitle();
    if (i === 0) { if (typeof _dashRollGreeting === 'function') _dashRollGreeting(); renderDashboard(); }
    if (i === 1 && favWorldsData.length === 0) sendToCS({ action: 'vrcGetFavoriteWorlds' });
    if (i === 2 && !myGroupsLoaded) loadMyGroups();
    if (i === 2 && typeof _myGroupsDirty !== 'undefined' && _myGroupsDirty && myGroupsLoaded) filterMyGroups();
    if (i === 2 && typeof _groupInstDirty !== 'undefined' && _groupInstDirty && typeof _dashGroupInstances !== 'undefined' && _dashGroupInstances !== null) renderGroupInstancesView();
    if (i === 23) { if (!myGroupsLoaded) loadMyGroups(); if (typeof onSnipeTabOpen === 'function') onSnipeTabOpen(); }
    if (i === 3 && favFriendsData.length === 0) sendToCS({ action: 'vrcGetFavoriteFriends' });
    if (i === 3 && typeof _favFriendsDirty !== 'undefined' && _favFriendsDirty && favFriendsData.length > 0) filterFavFriends();
    if (i === 4) { if (!avatarsLoaded) refreshAvatars(); }
    if (i === 7) { if (!libraryFiles.length) refreshLibrary(); else filterLibrary(); }
    if (i === 9) {
        renderLanguageChips();
        renderThemeChips();
        if (currentTheme === 'custom') renderColorInputs();
    }
    if (i === 8) flushActivityLog();
    if (i === 12) refreshTimeline();
    if (i === 13) switchInvTab(activeInvTab);
    if (i === 14) sendToCS({ action: 'vcCheck' });
    if (i === 17 && !calendarLoaded) refreshCalendar();
    if (i === 18) vfOnTabOpen();
    if (i === 21) onPerminiTabOpen();
    if (i === 22) { kxdInitLangSelects(); kxdOnTabOpen(); }
    if (i === 25) { if (typeof afOnTabOpen === 'function') afOnTabOpen(); }
    if (i === 27) { if (typeof onStatusScheduleTabOpen === 'function') onStatusScheduleTabOpen(); }
    if (i === 26) { if (typeof fsEnsureDeviceLists === 'function') fsEnsureDeviceLists(); }
    if (i === 5) { sendToCS({ action: 'hypeRateGetState' }); sendToCS({ action: 'weatherGetState' }); }
    if (typeof oscConnected !== 'undefined' && oscConnected) sendToCS({ action: 'oscSetTabVisible', visible: i === 11 });

    if (_prevTabEl) {
        if (_lazyUnloadDelay === 0) {
            _unloadTabImages(_prevTabEl);
        } else {
            _lazyUnloadTimer = setTimeout(() => _unloadTabImages(_prevTabEl), _lazyUnloadDelay);
        }
    }

    document.documentElement.dispatchEvent(new Event('tabchange'));
}

function toggleRelay() {
    sendToCS({ action: relayOn ? 'stopRelay' : 'startRelay' });
}

function relayToggleBtnHtml(running) {
    return running
        ? `<span class="msi" style="font-size:16px;">stop</span> ${t('common.stop', 'Stop')}`
        : `<span class="msi" style="font-size:16px;">play_arrow</span> ${t('common.start', 'Start')}`;
}

function relayStatusLabel(running) {
    return running
        ? t('relay.status.running', 'Running')
        : t('relay.status.not_running', 'Not running');
}

function setRelayState(r, s) {
    relayOn = r;
    const b = document.getElementById('btnRelay');
    const dot = document.getElementById('relayDot');
    const txt = document.getElementById('relayStatusText');
    const bd = document.getElementById('badgeRelay');
    if (r) {
        if (b) { b.className = 'vrcn-button'; b.innerHTML = relayToggleBtnHtml(true); }
        if (dot) dot.className = 'sf-dot online';
        if (txt) txt.textContent = relayStatusLabel(true);
        if (bd) bd.classList.add('tb-active');
        document.getElementById('statStreams').textContent = s || '0';
    } else {
        if (b) { b.className = 'vrcn-button'; b.innerHTML = relayToggleBtnHtml(false); }
        if (dot) dot.className = 'sf-dot offline';
        if (txt) txt.textContent = relayStatusLabel(false);
        if (bd) bd.classList.remove('tb-active');
        document.getElementById('statStreams').textContent = '0';
    }
}

const _httpCounts = { 200: 0, 429: 0, 404: 0, 403: 0, 400: 0 };
const _httpStatIds = { 200: 'statHttp200', 429: 'statHttp429', 404: 'statHttp404', 403: 'statHttp403', 400: 'statHttp400' };

function _setLogStat(id, value) {
    const el = document.getElementById(id);
    if (!el) return;
    const v = el.querySelector('.log-stat-v');
    if (!v) return;
    v.textContent = value;
    v.classList.toggle('zero', !value);
}

function _updateHttpBadge(code) {
    _setLogStat(_httpStatIds[code], _httpCounts[code]);
}

let _cdnCount = 0;
function _updateCdnBadge() {
    _setLogStat('statCdn', _cdnCount);
}

let _avtrdbGetCount = 0;
let _avtrdbQryCount = 0;
let _avtrdbSubCount = 0;
function _updateAvtrdbStats() {
    _setLogStat('statAvtrdbGet', _avtrdbGetCount);
    _setLogStat('statAvtrdbQry', _avtrdbQryCount);
    _setLogStat('statAvtrdbSub', _avtrdbSubCount);
}

const _sessionStart = Date.now();
let _totalGetCount = 0;

function _updateAvgBadges() {
    const hours = Math.max((Date.now() - _sessionStart) / 3_600_000, 1 / 3600);
    _setLogStat('statAget', Math.round(_totalGetCount / hours));
    _setLogStat('statAcdn', Math.round(_cdnCount / hours));
}

let _logShowFull = false;
let _logSearch   = '';

let _logLines = [];
let _logDomDirty = false;

function _applyLogFilter() {
    const a = document.getElementById('logArea');
    if (!a) return;
    const q = _logSearch.toLowerCase();
    if (!q) {
        for (const el of a.children) el.style.display = '';
        a.classList.toggle('log-tail', !_logShowFull);
        return;
    }
    a.classList.remove('log-tail');
    const all = Array.from(a.querySelectorAll('.li-f'));
    const matched = all.filter(el => el.textContent.toLowerCase().includes(q));
    const showFrom = _logShowFull ? 0 : Math.max(0, matched.length - 100);
    for (const el of all) el.style.display = 'none';
    for (let i = showFrom; i < matched.length; i++) matched[i].style.display = '';
}

function flushActivityLog() {
    const a = document.getElementById('logArea');
    if (!a) return;
    if (_logDomDirty) {
        _logDomDirty = false;
        a.innerHTML = _logLines.join('');
        _applyLogFilter();
        a.scrollTop = a.scrollHeight;
    }
}

function toggleLogShowFull() {
    _logShowFull = !_logShowFull;
    const btn = document.getElementById('logShowFullBtn');
    if (btn) {
        const ic = btn.querySelector('.logShowFullIcon'); if (ic) ic.textContent = _logShowFull ? 'unfold_less' : 'unfold_more';
        const tx = btn.querySelector('.logShowFullText'); if (tx) tx.textContent = _logShowFull ? 'Show Last 100' : 'Show Full';
    }
    _applyLogFilter();
    if (!_logShowFull) { const a = document.getElementById('logArea'); if (a) a.scrollTop = a.scrollHeight; }
}

function onLogSearch(val) {
    _logSearch = val.trim();
    _applyLogFilter();
}

function addLog(m, c) {
    const a = document.getElementById('logArea');
    if (!a) return;
    const ts = fmtTimeSeconds(new Date());

    // Strip emoji
    m = m.replace(/[\p{Emoji_Presentation}\p{Extended_Pictographic}]/gu, '').trim();

    // Multi-line messages: each line becomes its own row
    if (m.includes('\n')) {
        m.split('\n').forEach(line => addLog(line, c));
        return;
    }

    // Suppress pending REST requests — only show the response line (with → NNN)
    if (/\[REST\] (GET|POST|PUT|DELETE|PATCH) /.test(m) && !/→/.test(m)) return;

    // Track avtrdb requests
    if (/\[AVTRDB\] GET/.test(m)) { _avtrdbGetCount++; _updateAvtrdbStats(); }
    else if (/\[AVTRDB\] QRY/.test(m)) { _avtrdbQryCount++; _updateAvtrdbStats(); }
    else if (/\[AVTRDB\] SUB/.test(m)) { _avtrdbSubCount++; _updateAvtrdbStats(); }

    // Track CDN image downloads
    if (m.startsWith('CDN ') || m.startsWith('CDN -')) {
        _cdnCount++;
        _updateCdnBadge();
        _updateAvgBadges();
    }

    // Track HTTP status codes
    let httpLevel = null, statusCode = null;
    const statusMatch = m.match(/→ (\d{3})/);
    if (statusMatch) {
        statusCode = statusMatch[1];
        const code = +statusCode;
        if (code in _httpCounts) { _httpCounts[code]++; _updateHttpBadge(code); }
        if (code === 200) httpLevel = 'ok';
        else if (code === 429) httpLevel = 'warn';
        else if (code >= 400) httpLevel = 'err';
        if (/\[REST\].*GET /.test(m)) { _totalGetCount++; _updateAvgBadges(); }
    }

    // Bracket-prefix → level label + color class
    const _prefixMap = {
        'LOG': ['LOG', 'info'], 'VRC': ['VRC', 'vrc'], 'VRCHAT': ['VRC', 'vrc'],
        'LOAD': ['LOAD', 'ok'], 'LOAD ERROR': ['ERR', 'err'],
        'STARTUP': ['START', 'info'], 'GROUPS': ['GRPS', 'ok'],
        'INSTANCE': ['INST', 'warn'], 'RELAY': ['RELY', 'ok'],
        'CHATBOX': ['CHAT', 'info'], 'SF': ['SF', 'info'], 'WS': ['WS', 'info'],
    };
    const _colorParamMap = { ok: ['OK', 'ok'], warn: ['WARN', 'warn'], err: ['ERR', 'err'], sec: ['SEC', 'info'], accent: ['VRC', 'vrc'], cmd: ['CMD', 'info'] };

    let level = 'INFO', levelCls = 'info', msgBody = m;

    const pm = m.match(/^\[([A-Z][A-Z0-9 _-]*)\]\s*/);
    if (pm && _prefixMap[pm[1]]) {
        [level, levelCls] = _prefixMap[pm[1]];
        msgBody = m.slice(pm[0].length);
    } else if (/^VRChat:/.test(m))       { level = 'VRC';  levelCls = 'vrc'; }
      else if (/Instance:/.test(m))       { level = 'INST'; levelCls = 'warn'; }
      else if (/^Relay|^Posted/.test(m)) { level = 'RELY'; levelCls = 'ok'; }
      else if (_colorParamMap[c])         { [level, levelCls] = _colorParamMap[c]; }

    if (httpLevel) levelCls = httpLevel;
    if (statusCode) msgBody = msgBody.replace(/ → \d{3}.*$/, '');

    const rowHtml = `<div class="li-f"><span class="li-ts">${ts}</span><span class="li-level ${levelCls}">${esc(level)}</span><span class="li-msg">${esc(msgBody)}</span>${statusCode ? `<span class="li-status ${levelCls}">${statusCode}</span>` : ''}</div>`;
    _logLines.push(rowHtml);
    if (_logLines.length > 500) _logLines.shift();

    const tab8 = document.getElementById('tab8');
    if (!tab8 || !tab8.classList.contains('active')) { _logDomDirty = true; return; }

    const atBottom = a.scrollHeight - a.scrollTop - a.clientHeight < 40;
    a.insertAdjacentHTML('beforeend', rowHtml);
    while (a.childElementCount > 500) a.removeChild(a.firstChild);
    if (_logSearch) _applyLogFilter();
    else a.classList.toggle('log-tail', !_logShowFull);
    if (atBottom) a.scrollTop = a.scrollHeight;
}

// VRCVideoCacher
let _vcLastState = null;

function toggleVc() {
    const running = document.getElementById('vcDot')?.classList.contains('online');
    sendToCS({ action: running ? 'vcStop' : 'vcStart' });
}
function vcInstall() {
    document.getElementById('btnVcInstall').disabled = true;
    sendToCS({ action: 'vcInstall' });
}
function handleVcState(d) {
    _vcLastState = d;
    const bdYt = document.getElementById('badgeYt');
    if (bdYt) bdYt.classList.toggle('tb-active', !!d.running);
    const running    = !!d.running;
    const installed  = !!d.installed;
    const dot        = document.getElementById('vcDot');
    const txt        = document.getElementById('vcStatusText');
    const btn        = document.getElementById('btnVc');
    const installBtn = document.getElementById('btnVcInstall');
    const progWrap   = document.getElementById('vcProgressWrap');
    const progBar    = document.getElementById('vcProgressBar');
    const progLbl    = document.getElementById('vcProgressLabel');
    const verLbl     = document.getElementById('vcVersionLabel');

    if (d.downloading) {
        if (progWrap) progWrap.style.display = '';
        if (progBar)  progBar.style.width = (d.progress || 0) + '%';
        if (progLbl)  progLbl.textContent  = tf('youtube_fix.progress.downloading_percent', { percent: d.progress || 0 }, `Downloading... ${d.progress || 0}%`);
        if (installBtn) installBtn.disabled = true;
        return;
    }
    if (progWrap) progWrap.style.display = 'none';
    if (installBtn) installBtn.disabled = false;

    if (d.error) {
        if (txt) { txt.textContent = tf('youtube_fix.status.error', { error: d.error }, `Error: ${d.error}`); txt.style.color = 'var(--err)'; }
        return;
    }
    if (txt) txt.style.color = '';

    if (btn) {
        btn.disabled = !installed;
        btn.innerHTML = running
            ? `<span class="msi" style="font-size:16px;">stop</span> ${t('common.stop', 'Stop')}`
            : `<span class="msi" style="font-size:16px;">play_arrow</span> ${t('common.start', 'Start')}`;
    }
    if (dot) dot.className = 'sf-dot ' + (running ? 'online' : 'offline');
    if (txt) txt.textContent = running
        ? t('youtube_fix.status.running', 'Running')
        : (installed ? t('youtube_fix.status.not_running', 'Not running') : t('youtube_fix.status.not_installed', 'Not installed'));
    if (verLbl) verLbl.textContent = installed ? t('youtube_fix.version.installed', 'Installed') : '';
}

function rerenderVcTranslations() {
    if (_vcLastState) {
        handleVcState(_vcLastState);
        return;
    }

    const txt = document.getElementById('vcStatusText');
    if (txt) txt.textContent = t('youtube_fix.status.not_running', 'Not running');

    const btn = document.getElementById('btnVc');
    if (btn && !btn.disabled) {
        const running = document.getElementById('vcDot')?.classList.contains('online');
        btn.innerHTML = running
            ? `<span class="msi" style="font-size:16px;">stop</span> ${t('common.stop', 'Stop')}`
            : `<span class="msi" style="font-size:16px;">play_arrow</span> ${t('common.start', 'Start')}`;
    }
}

document.documentElement.addEventListener('languagechange', rerenderVcTranslations);

function clearLog() {
    const si = document.getElementById('logSearchInput');
    if (!si || !si.value) { _logSearch = ''; if (si) si.value = ''; }
    _logShowFull = false;
    const btn = document.getElementById('logShowFullBtn');
    if (btn) {
        const ic = btn.querySelector('.logShowFullIcon'); if (ic) ic.textContent = 'unfold_more';
        const tx = btn.querySelector('.logShowFullText'); if (tx) tx.textContent = 'Show Full';
    }
    const a = document.getElementById('logArea');
    if (a) a.innerHTML = '';
    _logLines = [];
    _logDomDirty = false;
}

function copyLog() {
    const a = document.getElementById('logArea');
    if (!a) return;
    const text = Array.from(a.querySelectorAll('.li-f')).map(l => l.textContent).join('\n');
    navigator.clipboard.writeText(text).then(() => showToast(true, t('activity.copy_done', 'Log copied to clipboard')));
}

function rerenderRelayTranslations() {
    const currentStreams = document.getElementById('statStreams')?.textContent || '0';
    setRelayState(relayOn, currentStreams);

    const wrap = document.getElementById('ffcProgressWrap');
    const bar = document.getElementById('ffcProgressBar');
    const lbl = document.getElementById('ffcProgressLabel');
    if (wrap && wrap.style.display !== 'none' && lbl) {
        const progress = Number.parseInt((bar?.style.width || '0').replace('%', ''), 10) || 0;
        lbl.textContent = translateFfcProgressLabel(lbl.dataset.rawLabel || '', progress);
    }
}

document.documentElement.addEventListener('languagechange', rerenderRelayTranslations);


function playVRChat() {
    sendToCS({ action: 'playVRChat' });
}

// Communication
function sendToCS(m) {
    window.external.sendMessage(JSON.stringify(m));
}

// Loading overlay used during account switch and restart.
function showLoadingOverlay(text) {
    let el = document.getElementById('vrcnLoadingOverlay');
    if (!el) {
        el = document.createElement('div');
        el.id = 'vrcnLoadingOverlay';
        el.style.cssText = 'position:fixed;inset:0;background:rgba(0,0,0,.55);z-index:99999;display:flex;align-items:center;justify-content:center;color:#fff;font-size:calc(14px + var(--fs-off, 0px));backdrop-filter:blur(4px);';
        el.innerHTML = '<div style="padding:18px 26px;background:var(--bg-input);border-radius:12px;color:var(--tx1);display:flex;align-items:center;gap:10px;"><span class="msi" style="font-size:20px;">sync</span><span id="vrcnLoadingOverlayText"></span></div>';
        document.body.appendChild(el);
    }
    const t = document.getElementById('vrcnLoadingOverlayText');
    if (t) t.textContent = text || 'Loading...';
    el.style.display = 'flex';
}
function hideLoadingOverlay() {
    const el = document.getElementById('vrcnLoadingOverlay');
    if (el) el.style.display = 'none';
}

function execConsoleCommand(cmd) {
    cmd = (cmd || '').trim();
    if (!cmd) return;
    const input = document.getElementById('consoleInput');
    if (input) input.value = '';
    if (cmd.toLowerCase() === '/blyat') { if (typeof runBlyat === 'function') runBlyat(); return; }
    if (cmd.toLowerCase() === '/rewind') { addLog('> ' + cmd, 'cmd'); sendToCS({ action: 'getRewind' }); return; }
    if (cmd.toLowerCase() === '/changelog') { addLog('> ' + cmd, 'cmd'); openChangelogModal(); return; }
    if (cmd.toLowerCase() === '/init-modal') {
        addLog('> ' + cmd, 'cmd');
        const ex = document.getElementById('vrcndbConsentModal');
        if (ex) ex.remove();
        if (typeof showVrcndbConsent === 'function') { showVrcndbConsent(); addLog('Reopened the VRCNDb consent modal.', 'info'); }
        else addLog('Consent modal is not available.', 'warn');
        return;
    }
    addLog('> ' + cmd, 'cmd');
    sendToCS({ action: 'consoleCommand', cmd });
}

function audioSelectionFromSaved(id, name) {
    if (id) return { mode: 'endpoint', id, name: name || '' };
    if (name) return { mode: 'legacy', id: '', name };
    return { mode: 'default', id: '', name: '' };
}

function audioSelectionFromSelect(sel) {
    if (!sel || sel.options.length === 0) return { mode: 'default', id: '', name: '' };
    const v = sel.value || '';
    if (!v) return { mode: 'default', id: '', name: '' };
    if (v.startsWith('legacy:')) return { mode: 'legacy', id: '', name: v.slice(7) };
    const opt = sel.options[sel.selectedIndex];
    return { mode: 'endpoint', id: v, name: opt?.dataset.name || '' };
}

function audioFillDeviceSelect(sel, devices, selection) {
    if (!sel) return;
    const list = Array.isArray(devices) ? devices : [];
    const s = selection || {};
    let html = `<option value="">${esc(t('audio.system_default', 'System default'))}</option>`;
    for (const d of list) html += `<option value="${esc(d.id)}" data-name="${esc(d.name)}">${esc(d.name)}</option>`;
    let wanted = '';
    if (s.mode === 'endpoint' && s.id) {
        wanted = s.id;
        if (!list.some(d => d.id === s.id)) {
            html += `<option value="${esc(s.id)}" data-name="${esc(s.name || '')}">${esc((s.name || s.id) + ' ' + t('audio.unavailable', '(Unavailable)'))}</option>`;
        }
    } else if (s.mode === 'legacy' && s.name) {
        wanted = 'legacy:' + s.name;
        html += `<option value="${esc(wanted)}" data-name="${esc(s.name)}">${esc(s.name + ' ' + t('audio.unresolved', '(Unresolved)'))}</option>`;
    }
    sel.innerHTML = html;
    sel.value = wanted;
    sel.dataset.audioReady = '1';
    if (sel._vnRefresh) sel._vnRefresh();
}

function audioPrefillDeviceSelect(sel, selection) {
    if (!sel || sel.dataset.audioReady === '1') return;
    const s = selection || {};
    let html = `<option value="">${esc(t('audio.system_default', 'System default'))}</option>`;
    let wanted = '';
    if (s.mode === 'endpoint' && s.id) {
        wanted = s.id;
        html += `<option value="${esc(s.id)}" data-name="${esc(s.name || '')}">${esc(s.name || s.id)}</option>`;
    } else if (s.mode === 'legacy' && s.name) {
        wanted = 'legacy:' + s.name;
        html += `<option value="${esc(wanted)}" data-name="${esc(s.name)}">${esc(s.name)}</option>`;
    }
    sel.innerHTML = html;
    sel.value = wanted;
    if (sel._vnRefresh) sel._vnRefresh();
}

function audioDeviceValue(selId) {
    const sel = document.getElementById(selId);
    if (!sel || sel.options.length === 0) return null;
    const opt = sel.options[sel.selectedIndex];
    return { id: sel.value || '', name: opt?.dataset.name || '' };
}

const _escMap = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };
function esc(s) {
    return String(s || '').replace(/[&<>"']/g, ch => _escMap[ch]);
}

function jsq(s) {
    return String(s || '').replace(/\\/g, '\\\\').replace(/'/g, "\\'").replace(/\r/g, '\\r').replace(/\n/g, '\\n');
}

function cssUrl(s) {
    return (s || '').replace(/'/g, '%27').replace(/\)/g, '%29');
}

let imgThumbsEnabled = true;
let vrcPlusOptimizeEnabled = true;

function _thumbUrl(url, size) {
    if (!url || url.indexOf('/imgcache/') === -1 || url.indexOf('thumb=') !== -1) return url;
    return url + (url.indexOf('?') >= 0 ? '&' : '?') + 'thumb=' + size;
}

function imgThumb(url, size = 64) {
    if (!imgThumbsEnabled) return url;
    return _thumbUrl(url, size);
}

function imgOriginal(url) {
    if (!url || url.indexOf('thumb=') === -1) return url;
    return url.replace(/([?&])thumb=\d+(&|$)/, (m, p1, p2) => p2 === '&' ? p1 : '').replace(/[?&]$/, '');
}

function setHtmlIfChanged(el, h) {
    if (!el) return false;
    if (el.__lastHtml === h) return false;
    el.__lastHtml = h;
    el.innerHTML = h;
    return true;
}

function isLocalFavGroup(g) {
    return !!(g && (g.local || (typeof g.type === 'string' && g.type.startsWith('local'))));
}

function favGroupBadge(g) {
    if (isLocalFavGroup(g)) return `<span class="vrcn-local-badge">${t('favorites.local_badge', 'Local')}</span>`;
    const vrcPlusTypes = ['vrcPlusWorld', 'vrcPlusAvatar'];
    if (g && vrcPlusTypes.includes(g.type)) return `<span class="vrcn-supporter-badge">VRC+</span>`;
    return '';
}

function localFavErrorText(code) {
    const map = {
        group_limit:   t('favorites.err_group_limit', 'Local group limit reached (10)'),
        item_limit:    t('favorites.err_item_limit', 'This group is full (100)'),
        empty_name:    t('favorites.err_empty_name', 'Enter a group name'),
    };
    return map[code] || t('favorites.err_generic', 'Could not update local favorites');
}

function onLocalGroupResult(data) {
    if (!data) return;
    if (data.ok) {
        if (data.action === 'create') showToast(true, tf('favorites.local_group_created', { name: data.displayName || '' }, 'Created local group "{name}"'));
        else if (data.action === 'delete') showToast(true, t('favorites.local_group_deleted', 'Local group deleted'));
        return;
    }
    showToast(false, localFavErrorText(data.error));
}

function copyIdBadge(el, id) {
    navigator.clipboard.writeText(id).catch(() => {});
    const orig = el.innerHTML;
    el.innerHTML = '<span class="msi" style="font-size:12px;">check</span>Copied!';
    setTimeout(() => { el.innerHTML = orig; }, 1500);
}

function idBadge(id) {
    const safe = jsq(id);
    return `<span class="vrcn-id-clip" title="${esc(id)}" onclick="copyIdBadge(this,'${safe}')"><span class="msi" style="font-size:12px;">link</span><span class="vrcn-id-text">${esc(id)}</span></span>`;
}

// Location / instance type helpers (global).

function parseFriendLocation(loc) {
    if (!loc || loc === 'private' || loc === 'offline' || loc === 'traveling') return { worldId: '', instanceType: loc || 'private', ownerId: '' };
    var worldId = loc.includes(':') ? loc.split(':')[0] : loc;
    var instanceType = 'public';
    var ownerId = '';
    if (loc.includes('~private(')) instanceType = loc.includes('~canRequestInvite') ? 'invite_plus' : 'private';
    else if (loc.includes('~friends+(')) instanceType = 'friends+';
    else if (loc.includes('~friends(')) instanceType = 'friends';
    else if (loc.includes('~hidden(')) instanceType = 'hidden';
    else if (loc.includes('~group(')) {
        var gatMatch = loc.match(/groupAccessType\(([^)]+)\)/);
        var gat = gatMatch ? gatMatch[1].toLowerCase() : '';
        if (gat === 'public') instanceType = 'group-public';
        else if (gat === 'plus') instanceType = 'group-plus';
        else if (gat === 'members') instanceType = 'group-members';
        else instanceType = 'group';
    }
    // Extract owner ID from location: ~friends(usr_xxx), ~group(grp_xxx), ~hidden(usr_xxx), ~private(usr_xxx)
    var ownerMatch = loc.match(/~(?:friends\+?|hidden|private|group)\(([^)]+)\)/);
    if (ownerMatch) ownerId = ownerMatch[1];
    return { worldId, instanceType, ownerId };
}

function getInstanceBadge(instanceType) {
    const type = instanceType || 'public';
    const labels = {
        'public': t('instance.badge.public', 'Public'),
        'friends': t('instance.badge.friends', 'Friends'),
        'friends+': t('instance.badge.friends_plus', 'Friends+'),
        'hidden': t('instance.badge.friends_plus', 'Friends+'),
        'invite_plus': t('instance.badge.invite_plus', 'Invite+'),
        'private': t('instance.badge.invite', 'Invite'),
        'group': t('instance.badge.group', 'Group'),
        'group-public': t('instance.badge.group_public', 'Group Public'),
        'group-plus': t('instance.badge.group_plus', 'Group+'),
        'group-members': t('instance.badge.group', 'Group')
    };
    const label = labels[type] || type.charAt(0).toUpperCase() + type.slice(1);
    let cls = 'public';
    if (type === 'friends' || type === 'friends+' || type === 'hidden') cls = 'friends';
    else if (type === 'invite_plus' || type === 'private') cls = 'private';
    else if (type.startsWith('group')) cls = 'group';
    return { cls, label };
}

function parseInstanceRegion(loc) {
    const m = String(loc || '').match(/~region\(([^)]+)\)/);
    return m ? m[1].toLowerCase() : '';
}

function getRegionShortLabel(code) {
    const key = String(code || '').toLowerCase();
    const labels = {
        eu:  t('regions.short.eu',  'EU'),
        us:  t('regions.short.us',  'USW'),
        usw: t('regions.short.usw', 'USW'),
        use: t('regions.short.use', 'USE'),
        jp:  t('regions.short.jp',  'JP'),
        au:  t('regions.short.au',  'AU'),
    };
    return labels[key] || key.toUpperCase();
}

function regionBadgeHtml(loc) {
    const code = parseInstanceRegion(loc);
    if (!code) return '';
    return `<span class="vrcn-badge"><span class="msi" style="font-size:10px;">language</span>${esc(getRegionShortLabel(code))}</span>`;
}

// Custom Dropdown
function initVnSelect(el) {
    if (!el || el._vnSelect) return;
    el._vnSelect = true;

    // Build wrapper, copying the select's class for layout (flex etc.) and inline style
    const wrap = document.createElement('div');
    wrap.className = (el.className ? el.className + ' ' : '') + 'vn-select';
    if (el.style.cssText) wrap.style.cssText = el.style.cssText;

    // Trigger (the visible "button")
    const trigger = document.createElement('div');
    trigger.className = 'vn-select-trigger';
    const triggerDot = document.createElement('span');
    triggerDot.className = 'sf-dot';
    triggerDot.style.display = 'none';
    const label = document.createElement('span');
    label.className = 'vn-select-label';
    const arrow = document.createElement('span');
    arrow.className = 'msi vn-select-arrow';
    arrow.textContent = 'expand_more';
    trigger.append(triggerDot, label, arrow);

    const panel = document.createElement('div');
    panel.className = 'vn-select-panel';

    wrap.append(trigger, panel);
    el.parentNode.insertBefore(wrap, el);
    el.style.display = 'none';
    wrap.appendChild(el); 

    function isVrcPlus(value, text) {
        return /vrcplus/i.test(value) || /vrc\+/i.test(text);
    }

    function cleanText(text) {
        return text.replace(/\s*\[VRC\+\]/gi, '').trim();
    }

    function splitCount(text) {
        const m = text.match(/^(.*?)\s+(\d+\/\d+)$/);
        return m ? { name: m[1], count: m[2] } : { name: text, count: '' };
    }

    function makeDot(state) {
        const d = document.createElement('span');
        d.className = 'sf-dot ' + state;
        return d;
    }

    function buildPanel() {
        panel.innerHTML = '';
        for (let i = 0; i < el.options.length; i++) {
            const opt = el.options[i];
            const item = document.createElement('div');
            item.className = 'vn-select-option' + (i === el.selectedIndex ? ' vn-active' : '');

            if (opt.dataset && opt.dataset.vnDot) item.appendChild(makeDot(opt.dataset.vnDot));

            const { name, count } = splitCount(cleanText(opt.text));
            const span = document.createElement('span');
            span.className = 'vn-select-label';
            span.textContent = name;
            if (opt.dataset && opt.dataset.vnFont) span.style.fontFamily = opt.dataset.vnFont;
            item.appendChild(span);
            item.title = name;

            if (count) {
                const countEl = document.createElement('span');
                countEl.textContent = count;
                countEl.style.cssText = 'font-size:calc(10px + var(--fs-off, 0px));color:var(--tx3);flex-shrink:0;margin-left:auto;';
                item.appendChild(countEl);
            }

            if (isVrcPlus(opt.value, opt.text)) {
                const badge = document.createElement('span');
                badge.className = 'vrcn-supporter-badge';
                badge.textContent = 'VRC+';
                item.appendChild(badge);
            } else if (/^local/i.test(opt.value)) {
                const badge = document.createElement('span');
                badge.className = 'vrcn-local-badge';
                badge.textContent = t('favorites.local_badge', 'Local');
                item.appendChild(badge);
            }

            item.addEventListener('click', () => {
                el.selectedIndex = i;
                el.dispatchEvent(new Event('change', { bubbles: true }));
                syncLabel();
                close();
            });

            if (el.dataset.vnHover) {
                item.addEventListener('mouseenter', () => {
                    clearTimeout(_vnHoverTimer);
                    const fn = window[el.dataset.vnHover];
                    if (typeof fn !== 'function') return;
                    _vnHoverTimer = setTimeout(() => fn(opt.value), 450);
                });
                item.addEventListener('mouseleave', () => clearTimeout(_vnHoverTimer));
            }

            panel.appendChild(item);
        }
    }

    function syncLabel() {
        const opt = el.options[el.selectedIndex];
        label.textContent = opt ? splitCount(cleanText(opt.text)).name : '';
        label.style.fontFamily = (opt && opt.dataset && opt.dataset.vnFont) || '';
        const dotState = opt && opt.dataset && opt.dataset.vnDot;
        if (dotState) { triggerDot.className = 'sf-dot ' + dotState; triggerDot.style.display = ''; }
        else          { triggerDot.style.display = 'none'; }
        panel.querySelectorAll('.vn-select-option').forEach((item, i) => {
            item.classList.toggle('vn-active', i === el.selectedIndex);
        });
    }

    function open() {
        buildPanel();
        syncLabel();
        wrap.classList.add('vn-open');
        // Flip above if near bottom of viewport
        const rect = wrap.getBoundingClientRect();
        const below = rect.bottom + 270 < window.innerHeight;
        panel.style.top    = below ? 'calc(100% + 4px)' : 'auto';
        panel.style.bottom = below ? 'auto' : 'calc(100% + 4px)';
        // Flip right-aligned if panel would extend past viewport right edge
        panel.style.left = '0'; panel.style.right = 'auto';
        const panelRect = panel.getBoundingClientRect();
        if (panelRect.right > window.innerWidth - 8) {
            panel.style.left = 'auto'; panel.style.right = '0';
        }
        setTimeout(() => document.addEventListener('click', onOutside, { once: true }), 0);
    }

    function close() { wrap.classList.remove('vn-open'); }

    function onOutside(e) {
        if (wrap.contains(e.target)) document.addEventListener('click', onOutside, { once: true });
        else close();
    }

    trigger.addEventListener('click', e => {
        e.stopPropagation();
        wrap.classList.contains('vn-open') ? close() : open();
    });

    // Expose refresh for callers that update options programmatically
    el._vnRefresh = () => { buildPanel(); syncLabel(); };

    // Initial render
    buildPanel();
    syncLabel();
}

let _vnHoverTimer = null;

function initAllVnSelects() {
    document.querySelectorAll('select:not([data-no-vn])').forEach(initVnSelect);
}

// === Force FFC All ===
function forceFfcAll() {
    const btn = document.getElementById('btnForceFfc');
    if (btn) btn.disabled = true;
    sendToCS({ action: 'forceFfcAll' });
}

function translateFfcProgressLabel(label, progress) {
    if (!label) {
        return tf('settings.debug.ffc_progress.caching_percent', { percent: progress ?? 0 }, `Caching... ${progress ?? 0}%`);
    }

    switch (label) {
        case 'Caching avatars...':
            return t('settings.debug.ffc_progress.caching_avatars', 'Caching avatars...');
        case 'Caching groups...':
            return t('settings.debug.ffc_progress.caching_groups', 'Caching groups...');
        case 'Caching worlds...':
            return t('settings.debug.ffc_progress.caching_worlds', 'Caching worlds...');
        default:
            break;
    }

    const profilesMatch = label.match(/^Caching profiles\.\.\. \((\d+)\/(\d+)\)$/);
    if (profilesMatch) {
        return tf(
            'settings.debug.ffc_progress.caching_profiles',
            { current: profilesMatch[1], total: profilesMatch[2] },
            `Caching profiles... (${profilesMatch[1]}/${profilesMatch[2]})`
        );
    }

    return label;
}

function handleFfcProgress(d) {
    const wrap = document.getElementById('ffcProgressWrap');
    const bar  = document.getElementById('ffcProgressBar');
    const lbl  = document.getElementById('ffcProgressLabel');
    const btn  = document.getElementById('btnForceFfc');
    if (d.done) {
        if (wrap) wrap.style.display = 'none';
        if (btn)  btn.disabled = false;
        return;
    }
    if (wrap) wrap.style.display = '';
    if (bar)  bar.style.width  = (d.progress || 0) + '%';
    if (lbl) {
        lbl.dataset.rawLabel = d.label || '';
        lbl.textContent = translateFfcProgressLabel(d.label, d.progress || 0);
    }
    if (btn)  btn.disabled = true;
}

function vrcnToggleCollapse(headerEl) {
    headerEl.closest('.vrcn-panel-card').classList.toggle('collapsed');
}

function animateModalBox(boxEl, doSwitch) {
    if (!boxEl) { doSwitch(); return; }
    const h0 = boxEl.offsetHeight;
    doSwitch();
    const h1 = boxEl.scrollHeight;
    if (Math.abs(h1 - h0) < 2) return;
    boxEl.style.overflow = 'hidden';
    boxEl.style.height = h0 + 'px';
    void boxEl.offsetHeight;
    boxEl.style.transition = 'height 0.2s ease';
    boxEl.style.height = h1 + 'px';
    boxEl.addEventListener('transitionend', () => {
        boxEl.style.height = '';
        boxEl.style.overflow = '';
        boxEl.style.transition = '';
    }, { once: true });
}


