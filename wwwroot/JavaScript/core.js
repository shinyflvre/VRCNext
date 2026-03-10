let relayOn = false, settings = { webhooks: [{}, {}, {}, {}], folders: [], extraExe: [] }, postedFiles = [], selectedFolderIdx = -1;
let favorites = new Set(), showFavOnly = false, libraryFiles = [];
let hiddenMedia = new Set();
try { hiddenMedia = new Set(JSON.parse(localStorage.getItem('vrcnext_hidden') || '[]')); } catch {}
const thumbCache = {};
let currentTheme = 'midnight', notifyAudio = null, currentVrcUser = null;
let sidebarCollapsed = localStorage.getItem('vrcnext_sidebar') === '1';
// Apply saved sidebar state immediately on load
(function() {
    const sidebar = document.getElementById('sidebarEl');
    if (sidebar && sidebarCollapsed) {
        sidebar.classList.add('collapsed');
        const icon = document.getElementById('sbIcon');
        if (icon) icon.textContent = 'menu';
    }
})();
let dashBgPath = '', dashBgDataUri = '', dashOpacity = 40;
let dashWorldCache = {};
let vrcFriendsLoaded = false;
const _fscDefaults = { favorites: false, ingame: false, web: false, offline: true };
let friendSectionCollapsed = (() => {
    try { return Object.assign({}, _fscDefaults, JSON.parse(localStorage.getItem('friendSectionCollapsed') || '{}')); }
    catch { return { ..._fscDefaults }; }
})();
let avatarsData = [], avatarFavData = [], avatarFilter = 'own', avatarsLoaded = false, currentAvatarId = '';
let avatarSearchResults = [], avatarSearchPage = 0, avatarSearchQuery = '', avatarSearchHasMore = false;
let favAvatarsData = [], favAvatarGroups = [], favAvatarGroupFilter = '';
let notifications = [], notifPanelOpen = false, myGroups = [], myGroupsLoaded = false;
let currentInstanceData = null;
// Pagination state for search
let searchState = {
    worlds: { query: '', offset: 0, results: [], hasMore: false },
    groups: { query: '', offset: 0, results: [], hasMore: false },
    people: { query: '', offset: 0, results: [], hasMore: false },
};
let currentFriendDetail = null;
let _fdLiveTimer = null;
/* === World info cache for library badges === */
let worldInfoCache = {};
let pendingDeletePath = null;
/* === World Tab: Favorites / Search filter === */
let worldFilter = 'favorites';
let favWorldsData = [];
let favWorldGroups = [];
let favWorldGroupFilter = '';
/* === People Tab: Favorites / Search / Blocked / Muted filter === */
let peopleFilter = 'favorites';
let favFriendsData = []; // [{ fvrtId, favoriteId }]
let blockedData = null; // null = not yet loaded
let mutedData = null;
/* === VRChat API === */
let vrc2faType = 'totp';
let vrcFriendsData = [];
let selectedStatus = 'active';
const STATUS_LIST = [
    { key: 'active', label: 'Online', color: '#2DD48C', desc: 'You appear online' },
    { key: 'join me', label: 'Join Me', color: '#42A5F5', desc: 'Others can easily join you' },
    { key: 'ask me', label: 'Ask Me', color: '#FFA726', desc: 'Others should ask before joining' },
    { key: 'busy', label: 'Do Not Disturb', color: '#EF5350', desc: 'You appear busy' }
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
/* === Space Flight === */
let sfConnected = false;
/* === Custom Chatbox OSC === */
let chatboxEnabled = false;
let chatboxCustomLines = [];
/* === OSC Tool === */
let oscParams = {};
let oscConnected = false;
/* === Timeline === */
let timelineEvents = [];
let tlFilter = 'all';
let tlMode = 'personal';
let friendTimelineEvents = [];
let ftFilter = 'all';
/* === Inventory === */
let activeInvTab = 'photos';
let invFilesCache = {}; // tag → file[]
let invPrintsCache = [];
let invInventoryCache = [];

// Skeleton shimmer helpers. sk(type, count) adds .sk-block CSS class to any element.
function sk(type, n = 1) {
    const t = {
        world:   () => `<div class="dash-world-card"><div class="dash-world-thumb sk-block"></div><div class="dash-world-info"><div class="sk-block" style="height:14px;width:65%;border-radius:4px;margin-bottom:8px;"></div><div class="sk-block" style="height:10px;width:40%;border-radius:4px;"></div></div></div>`,
        feed:    () => `<div class="dash-feed-card"><div class="dash-feed-avatar sk-block"></div><div class="dash-feed-info"><div class="sk-block" style="height:11px;width:75%;border-radius:4px;margin-bottom:5px;"></div><div class="sk-block" style="height:9px;width:50%;border-radius:4px;"></div></div></div>`,
        friend:  () => `<div class="vrc-friend-card"><div class="vrc-friend-avatar sk-block"></div><div class="vrc-friend-info"><div class="sk-block" style="height:11px;width:70%;border-radius:4px;margin-bottom:5px;"></div><div class="sk-block" style="height:9px;width:45%;border-radius:4px;"></div></div></div>`,
        avatar:  () => `<div class="av-card"><div class="av-thumb sk-block"></div><div class="av-info"><div class="sk-block" style="height:13px;width:70%;border-radius:4px;margin-bottom:6px;"></div><div class="sk-block" style="height:10px;width:45%;border-radius:4px;"></div></div></div>`,
        detail:  () => `<div style="padding:4px 0"><div class="sk-block" style="height:180px;border-radius:10px;margin-bottom:20px;"></div><div class="sk-block" style="height:20px;width:60%;border-radius:6px;margin-bottom:10px;"></div><div class="sk-block" style="height:13px;width:40%;border-radius:4px;margin-bottom:20px;"></div><div class="sk-block" style="height:11px;border-radius:4px;margin-bottom:8px;"></div><div class="sk-block" style="height:11px;width:85%;border-radius:4px;margin-bottom:8px;"></div><div class="sk-block" style="height:11px;width:65%;border-radius:4px;"></div></div>`,
        profile: () => `<div style="padding:4px 0"><div style="display:flex;gap:16px;align-items:center;margin-bottom:20px;"><div class="sk-block" style="width:72px;height:72px;border-radius:14px;flex-shrink:0;"></div><div style="flex:1;min-width:0;"><div class="sk-block" style="height:18px;width:60%;border-radius:5px;margin-bottom:8px;"></div><div class="sk-block" style="height:12px;width:40%;border-radius:4px;margin-bottom:6px;"></div><div class="sk-block" style="height:12px;width:55%;border-radius:4px;"></div></div></div><div class="sk-block" style="height:34px;border-radius:8px;margin-bottom:18px;"></div><div class="sk-block" style="height:11px;border-radius:4px;margin-bottom:7px;"></div><div class="sk-block" style="height:11px;width:80%;border-radius:4px;margin-bottom:7px;"></div><div class="sk-block" style="height:11px;width:60%;border-radius:4px;"></div></div>`
    };
    const fn = t[type]; return fn ? Array.from({length: n}, fn).join('') : '';
}

function toggleSidebar() {
    sidebarCollapsed = !sidebarCollapsed;
    localStorage.setItem('vrcnext_sidebar', sidebarCollapsed ? '1' : '0');
    const sidebar = document.getElementById('sidebarEl');
    document.getElementById('sbIcon').textContent = sidebarCollapsed ? 'menu' : 'menu_open';
    if (sidebarCollapsed) {
        sidebar.classList.add('collapsing');
        setTimeout(() => {
            sidebar.classList.remove('collapsing');
            sidebar.classList.add('collapsed');
        }, 230);
    } else {
        sidebar.classList.remove('collapsed');
    }
}

const THEMES = {
    midnight: { label: 'Midnight', dot: '#3884FF', c: { 'bg-base': '#080C15', 'bg-side': '#0B101C', 'bg-card': '#0F1628', 'bg-hover': '#141E37', 'bg-input': '#0D1225', 'accent': '#3884FF', 'accent-lt': '#64A0FF', 'cyan': '#00D2EB', 'ok': '#2DD48C', 'warn': '#FFBA37', 'err': '#FF4B55', 'tx0': '#F0F5FF', 'tx1': '#DCE4F5', 'tx2': '#788CAF', 'tx3': '#41506E', 'brd': '#1C2841', 'brd-lt': '#263755' } },
    ocean: { label: 'Ocean', dot: '#0EA5E9', c: { 'bg-base': '#041220', 'bg-side': '#061828', 'bg-card': '#082233', 'bg-hover': '#0C2E44', 'bg-input': '#030E1A', 'accent': '#0EA5E9', 'accent-lt': '#38BDF8', 'cyan': '#22D3EE', 'ok': '#34D399', 'warn': '#FBBF24', 'err': '#F87171', 'tx0': '#F0F9FF', 'tx1': '#BAE6FD', 'tx2': '#7DD3FC', 'tx3': '#3B7EA1', 'brd': '#164E63', 'brd-lt': '#1E6B8A' } },
    emerald: { label: 'Emerald', dot: '#10B981', c: { 'bg-base': '#05100B', 'bg-side': '#081810', 'bg-card': '#0C2018', 'bg-hover': '#12301F', 'bg-input': '#040D08', 'accent': '#10B981', 'accent-lt': '#34D399', 'cyan': '#2DD4BF', 'ok': '#4ADE80', 'warn': '#FCD34D', 'err': '#FB7185', 'tx0': '#F0FDF4', 'tx1': '#BBF7D0', 'tx2': '#6EE7B7', 'tx3': '#3D7A5A', 'brd': '#1A4034', 'brd-lt': '#245544' } },
    sunset: { label: 'Sunset', dot: '#F97316', c: { 'bg-base': '#150A08', 'bg-side': '#1C100C', 'bg-card': '#251814', 'bg-hover': '#33201A', 'bg-input': '#120806', 'accent': '#F97316', 'accent-lt': '#FB923C', 'cyan': '#FBBF24', 'ok': '#4ADE80', 'warn': '#FDE047', 'err': '#EF4444', 'tx0': '#FFF7ED', 'tx1': '#FED7AA', 'tx2': '#FDBA74', 'tx3': '#9A6340', 'brd': '#3D2516', 'brd-lt': '#552F1E' } },
    rose: { label: 'Rose', dot: '#F43F5E', c: { 'bg-base': '#140810', 'bg-side': '#1A0C16', 'bg-card': '#22101E', 'bg-hover': '#311828', 'bg-input': '#100610', 'accent': '#F43F5E', 'accent-lt': '#FB7185', 'cyan': '#F472B6', 'ok': '#4ADE80', 'warn': '#FCD34D', 'err': '#FF6B6B', 'tx0': '#FFF1F2', 'tx1': '#FECDD3', 'tx2': '#FDA4AF', 'tx3': '#9A4058', 'brd': '#3D1526', 'brd-lt': '#551A34' } },
    lavender: { label: 'Lavender', dot: '#A78BFA', c: { 'bg-base': '#0C0A18', 'bg-side': '#100E20', 'bg-card': '#16132A', 'bg-hover': '#1E1A3A', 'bg-input': '#090814', 'accent': '#A78BFA', 'accent-lt': '#C4B5FD', 'cyan': '#818CF8', 'ok': '#4ADE80', 'warn': '#FCD34D', 'err': '#FB7185', 'tx0': '#F5F3FF', 'tx1': '#DDD6FE', 'tx2': '#A78BFA', 'tx3': '#6D5BA0', 'brd': '#2E2556', 'brd-lt': '#3D3370' } },
    vrchat:   { label: 'VRChat',   dot: '#1461FF', c: { 'bg-base': '#05071A', 'bg-side': '#080C24', 'bg-card': '#0D1230', 'bg-hover': '#141A3F', 'bg-input': '#070B20', 'accent': '#1461FF', 'accent-lt': '#4D87FF', 'cyan': '#00C8FF', 'ok': '#2DD48C', 'warn': '#FFBA37', 'err': '#FF4B55', 'tx0': '#FFFFFF', 'tx1': '#C8D5FF', 'tx2': '#6B7DB8', 'tx3': '#3A4880', 'brd': '#1A2454', 'brd-lt': '#243070' } },
    day:      { label: 'Day',      dot: '#3884FF', c: { 'bg-base': '#F2F4F8', 'bg-side': '#E6E9F2', 'bg-card': '#FFFFFF', 'bg-hover': '#E8EDF8', 'bg-input': '#F5F7FC', 'accent': '#3884FF', 'accent-lt': '#64A0FF', 'cyan': '#00A8C8', 'ok': '#18A86A', 'warn': '#D4860A', 'err': '#D93040', 'tx0': '#0A0E1E', 'tx1': '#1A2440', 'tx2': '#4A5878', 'tx3': '#8090B0', 'brd': '#D0D8E8', 'brd-lt': '#BEC8DA' } },
    night:    { label: 'Night',    dot: '#0A84FF', c: { 'bg-base': '#0E0F12', 'bg-side': '#141519', 'bg-card': '#1A1B20', 'bg-hover': '#22242C', 'bg-input': '#111318', 'accent': '#0A84FF', 'accent-lt': '#3D9EFF', 'cyan': '#5AC8FA', 'ok': '#30D158', 'warn': '#FFD60A', 'err': '#FF453A', 'tx0': '#F4F4F6', 'tx1': '#C8CACD', 'tx2': '#6E737D', 'tx3': '#3D4249', 'brd': '#272930', 'brd-lt': '#33363F' } },
    iris:     { label: 'Iris',     dot: '#6674F0', c: { 'bg-base': '#07091C', 'bg-side': '#0B0E26', 'bg-card': '#101430', 'bg-hover': '#181C42', 'bg-input': '#090C20', 'accent': '#6674F0', 'accent-lt': '#8A95F5', 'cyan': '#94B8FF', 'ok': '#4ADE80', 'warn': '#FCD34D', 'err': '#FC8181', 'tx0': '#EDF0FF', 'tx1': '#C0CAFF', 'tx2': '#7080C0', 'tx3': '#3C4880', 'brd': '#1E2452', 'brd-lt': '#2A326E' } },
    glacier:  { label: 'Glacier',  dot: '#7AA8E0', c: { 'bg-base': '#111620', 'bg-side': '#171C2A', 'bg-card': '#1D2335', 'bg-hover': '#242B42', 'bg-input': '#141820', 'accent': '#7AA8E0', 'accent-lt': '#98BEE8', 'cyan': '#88CCD8', 'ok': '#68C89A', 'warn': '#D8C068', 'err': '#D88080', 'tx0': '#D8E4F2', 'tx1': '#9EB0C8', 'tx2': '#5A6E88', 'tx3': '#364054', 'brd': '#242C3E', 'brd-lt': '#303C52' } },
    petal:    { label: 'Petal',    dot: '#E890B0', c: { 'bg-base': '#130C10', 'bg-side': '#1C1018', 'bg-card': '#241620', 'bg-hover': '#302030', 'bg-input': '#100A0E', 'accent': '#E890B0', 'accent-lt': '#F0A8C4', 'cyan': '#D8A0D0', 'ok': '#68C888', 'warn': '#F0C848', 'err': '#F07878', 'tx0': '#FFF0F5', 'tx1': '#ECC8D8', 'tx2': '#B07890', 'tx3': '#744860', 'brd': '#361E2C', 'brd-lt': '#4A283C' } },
    void:        { label: 'Void',        dot: '#8060C8', c: { 'bg-base': '#060408', 'bg-side': '#090610', 'bg-card': '#0E0A18', 'bg-hover': '#140F22', 'bg-input': '#07050C', 'accent': '#8060C8', 'accent-lt': '#A080E0', 'cyan': '#6060D8', 'ok': '#3AD480', 'warn': '#E8B840', 'err': '#F06060', 'tx0': '#F0ECF8', 'tx1': '#C8B8E8', 'tx2': '#7060A0', 'tx3': '#40305C', 'brd': '#18102A', 'brd-lt': '#221640' } },
    dusk:        { label: 'Dusk',        dot: '#C4944C', c: { 'bg-base': '#080816', 'bg-side': '#0C0C1E', 'bg-card': '#121228', 'bg-hover': '#1A1A36', 'bg-input': '#080814', 'accent': '#C4944C', 'accent-lt': '#D8AE70', 'cyan': '#9880C8', 'ok': '#64C878', 'warn': '#E8C040', 'err': '#E86868', 'tx0': '#F8F4EC', 'tx1': '#E0C8A0', 'tx2': '#907060', 'tx3': '#50404C', 'brd': '#1E1C32', 'brd-lt': '#2C2844' } },
    ultraviolet: { label: 'Ultraviolet', dot: '#7B4FCC', c: { 'bg-base': '#08060E', 'bg-side': '#0C091A', 'bg-card': '#120E24', 'bg-hover': '#1A1430', 'bg-input': '#090710', 'accent': '#7B4FCC', 'accent-lt': '#9B70E0', 'cyan': '#6080D8', 'ok': '#50C880', 'warn': '#E8B040', 'err': '#E06080', 'tx0': '#F0ECFC', 'tx1': '#C8B8E8', 'tx2': '#806898', 'tx3': '#483860', 'brd': '#1E1640', 'brd-lt': '#2C2058' } },
    plum:        { label: 'Plum',        dot: '#9878C0', c: { 'bg-base': '#1C1830', 'bg-side': '#241E3A', 'bg-card': '#2E2844', 'bg-hover': '#3A3252', 'bg-input': '#181428', 'accent': '#9878C0', 'accent-lt': '#B09ED4', 'cyan': '#8898C8', 'ok': '#6CC890', 'warn': '#D4BC60', 'err': '#D07880', 'tx0': '#EDE8F8', 'tx1': '#C8BFD4', 'tx2': '#8878A0', 'tx3': '#504868', 'brd': '#3A3052', 'brd-lt': '#4A3E60' } },
    lilac:       { label: 'Lilac',       dot: '#9A50D8', c: { 'bg-base': '#F0E8FC', 'bg-side': '#E8DEFA', 'bg-card': '#FAFBFE', 'bg-hover': '#EDE4FB', 'bg-input': '#F5F0FD', 'accent': '#9A50D8', 'accent-lt': '#B878F0', 'cyan': '#7878E0', 'ok': '#28A870', 'warn': '#B87A10', 'err': '#C83040', 'tx0': '#1A0C28', 'tx1': '#280E3C', 'tx2': '#5A3878', 'tx3': '#9878B0', 'brd': '#D8C8F0', 'brd-lt': '#CAB8E8' } },
    prism:       { label: 'Prism',       dot: '#8878F0', c: { 'bg-base': '#080C18', 'bg-side': '#0C1020', 'bg-card': '#101628', 'bg-hover': '#182030', 'bg-input': '#080C16', 'accent': '#8878F0', 'accent-lt': '#A898F8', 'cyan': '#60C0F8', 'ok': '#48D890', 'warn': '#F0C848', 'err': '#F06090', 'tx0': '#F0EEFF', 'tx1': '#C0C0F8', 'tx2': '#6870C0', 'tx3': '#383870', 'brd': '#182040', 'brd-lt': '#202850' } },
    periwinkle:  { label: 'Periwinkle',  dot: '#7A9AD8', c: { 'bg-base': '#0A0C14', 'bg-side': '#0E1020', 'bg-card': '#141828', 'bg-hover': '#1C2234', 'bg-input': '#0C0E18', 'accent': '#7A9AD8', 'accent-lt': '#A0B8E8', 'cyan': '#88C0D8', 'ok': '#58C890', 'warn': '#D4C060', 'err': '#D06880', 'tx0': '#E8F0FC', 'tx1': '#B8C8E8', 'tx2': '#5870A0', 'tx3': '#304068', 'brd': '#1C2440', 'brd-lt': '#283450' } },
};

function applyColors(c) {
    if (!c) return;
    for (const [k, v] of Object.entries(c)) document.documentElement.style.setProperty('--' + k, v);
    if (c['bg-card']) document.documentElement.style.setProperty('--bg-btn', c['bg-card']);
    if (c['bg-hover']) document.documentElement.style.setProperty('--bg-btn-h', c['bg-hover']);
    const logoEl = document.getElementById('logoIcon');
    if (logoEl && logoEl._repaintLogo) logoEl._repaintLogo();
    document.documentElement.dispatchEvent(new Event('themechange'));
}

function renderThemeChips() {
    document.getElementById('themeGrid').innerHTML = Object.entries(THEMES).map(([k, t]) =>
        `<button class="theme-chip${currentTheme === k ? ' active' : ''}" onclick="selectTheme('${k}')"><span class="theme-dot" style="background:${t.dot}"></span>${t.label}</button>`
    ).join('');
}

function selectTheme(n) {
    currentTheme = n;
    applyColors(THEMES[n].c);
    renderThemeChips();
    autoSave();
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
    i.src = 'Logo.png';
}

function tryInitNotifySound() {
    const a = new Audio('notify.ogg');
    a.volume = 0.5;
    a.addEventListener('canplaythrough', () => { notifyAudio = a; }, { once: true });
    a.addEventListener('error', () => { notifyAudio = null; });
    a.load();
}

function playNotifySound() {
    if (notifyAudio && settings.notifySound) {
        notifyAudio.currentTime = 0;
        notifyAudio.play().catch(() => {});
    }
}

function updateClock() {
    const n = new Date();
    document.getElementById('clock').textContent = n.toLocaleTimeString('en-GB', { hour12: false });
    document.getElementById('clockDate').textContent = n.toLocaleDateString('en-US', { weekday: 'long', month: 'short', day: 'numeric' });
}

function toggleNavGroup(id) {
    const group = document.getElementById(id);
    if (!group) return;
    group.classList.toggle('collapsed');
    localStorage.setItem('vrcnext_navgroup_' + id, group.classList.contains('collapsed') ? '1' : '0');
}

function showTab(i) {
    document.querySelectorAll('.tab').forEach((t, j) => t.classList.toggle('active', j === i));
    // Clear active from all nav-btns (including sub-items and group headers)
    document.querySelectorAll('.nav-btn').forEach(b => b.classList.remove('active'));
    document.querySelectorAll('.nav-group').forEach(g => g.classList.remove('has-active'));
    // Find and activate the correct button by matching tab index
    const allBtns = document.querySelectorAll('.nav-btn[onclick]');
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
    document.getElementById('pageTitle').textContent = ['Dashboard','Worlds','Groups','People','Avatars','Custom Chatbox','Media Relay','Media Library','Activity Log','Settings','Space Flight','OSC Tool','Timeline','Inventory','YouTube Fix','Mutual Network','Time Spent','Calendar','Voice Fight'][i] ?? '';
    if (i === 0) renderDashboard();
    if (i === 1 && favWorldsData.length === 0) sendToCS({ action: 'vrcGetFavoriteWorlds' });
    if (i === 2 && !myGroupsLoaded) loadMyGroups();
    if (i === 3 && favFriendsData.length === 0) sendToCS({ action: 'vrcGetFavoriteFriends' });
    if (i === 4) { if (!avatarsLoaded) refreshAvatars(); }
    if (i === 7) refreshLibrary();
    if (i === 9) {
        renderThemeChips();
        if (currentTheme === 'custom') renderColorInputs();
    }
    if (i === 12) refreshTimeline();
    if (i === 13) switchInvTab(activeInvTab);
    if (i === 14) sendToCS({ action: 'vcCheck' });
    if (i === 17 && !calendarLoaded) refreshCalendar();
    if (i === 18) vfOnTabOpen();
    document.documentElement.dispatchEvent(new Event('tabchange'));
}

function toggleRelay() {
    sendToCS({ action: relayOn ? 'stopRelay' : 'startRelay' });
}

function setRelayState(r, s) {
    relayOn = r;
    const b = document.getElementById('btnRelay');
    const dot = document.getElementById('relayDot');
    const txt = document.getElementById('relayStatusText');
    const bd = document.getElementById('badgeRelay');
    if (r) {
        if (b) { b.className = 'vrcn-button'; b.innerHTML = '<span class="msi" style="font-size:16px;">stop</span> Stop'; }
        if (dot) dot.className = 'sf-dot online';
        if (txt) txt.textContent = 'Running';
        bd.className = 'mini-badge online';
        document.getElementById('statStreams').textContent = s || '0';
    } else {
        if (b) { b.className = 'vrcn-button'; b.innerHTML = '<span class="msi" style="font-size:16px;">play_arrow</span> Start'; }
        if (dot) dot.className = 'sf-dot offline';
        if (txt) txt.textContent = 'Not running';
        bd.className = 'mini-badge offline';
        document.getElementById('statStreams').textContent = '0';
    }
}

function addLog(m, c) {
    const a = document.getElementById('logArea');
    if (!a) return;
    const t = new Date().toLocaleTimeString('en-GB', { hour12: false });

    // Strip emoji characters
    m = m.replace(/[\p{Emoji_Presentation}\p{Extended_Pictographic}]/gu, '').trim();

    // Bracket-prefix to CSS class map
    const _prefixMap = {
        'LOG':        'log-msg-log',
        'VRC':        'log-msg-vrc',
        'VRCHAT':     'log-msg-vrc',
        'LOAD':       'log-msg-load',
        'LOAD ERROR': 'log-msg-err',
        'STARTUP':    'log-msg-startup',
        'GROUPS':     'log-msg-groups',
        'INSTANCE':   'log-msg-instance',
        'RELAY':      'log-msg-relay',
        'CHATBOX':    'log-msg-chatbox',
        'SF':         'log-msg-sf',
        'WS':         'log-msg-ws',
    };
    // Content-pattern fallback map (for messages without brackets)
    const _contentMap = [
        [/^VRChat:/,   'log-msg-vrc'],
        [/Instance:/,  'log-msg-instance'],
        [/^Relay/,     'log-msg-relay'],
        [/^Posted|^\s+Posted|^\s+Error '/, 'log-msg-relay'],
    ];
    // Color-param fallback
    const _colorMap = { ok: 'log-msg-ok', warn: 'log-msg-warn', err: 'log-msg-err', sec: 'log-msg-sec', accent: 'log-msg-accent' };

    let cl;
    const pm = m.match(/^\[([A-Z][A-Z0-9 _-]*)\]/);
    if (pm) cl = _prefixMap[pm[1]];
    if (!cl) { for (const [re, cls] of _contentMap) { if (re.test(m)) { cl = cls; break; } } }
    if (!cl) cl = _colorMap[c] || 'log-msg-default';

    const l = document.createElement('div');
    l.className = 'log-line';
    l.innerHTML = `<span class="log-time">${t}  </span><span class="${cl}">${esc(m)}</span>`;
    a.appendChild(l);
    a.scrollTop = a.scrollHeight;
}

// VRCVideoCacher
function toggleVc() {
    const running = document.getElementById('vcDot')?.classList.contains('online');
    sendToCS({ action: running ? 'vcStop' : 'vcStart' });
}
function vcInstall() {
    document.getElementById('btnVcInstall').disabled = true;
    sendToCS({ action: 'vcInstall' });
}
function handleVcState(d) {
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
        if (progLbl)  progLbl.textContent  = `Downloading... ${d.progress || 0}%`;
        if (installBtn) installBtn.disabled = true;
        return;
    }
    if (progWrap) progWrap.style.display = 'none';
    if (installBtn) installBtn.disabled = false;

    if (d.error) {
        if (txt) { txt.textContent = 'Error: ' + d.error; txt.style.color = 'var(--err)'; }
        return;
    }
    if (txt) txt.style.color = '';

    if (btn) {
        btn.disabled = !installed;
        btn.innerHTML = running
            ? '<span class="msi" style="font-size:16px;">stop</span> Stop'
            : '<span class="msi" style="font-size:16px;">play_arrow</span> Start';
    }
    if (dot) dot.className = 'sf-dot ' + (running ? 'online' : 'offline');
    if (txt) txt.textContent = running ? 'Running' : (installed ? 'Not running' : 'Not installed');
    if (verLbl) verLbl.textContent = installed ? 'Installed' : '';
}

function clearLog() {
    const a = document.getElementById('logArea');
    if (a) a.innerHTML = '';
}

function exportLog() {
    const a = document.getElementById('logArea');
    if (!a) return;
    const lines = Array.from(a.querySelectorAll('.log-line')).map(l => l.textContent);
    const blob = new Blob([lines.join('\n')], { type: 'text/plain' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `vrcnext-log-${new Date().toISOString().slice(0, 19).replace(/:/g, '-')}.txt`;
    link.click();
    URL.revokeObjectURL(url);
}

function playVRChat() {
    sendToCS({ action: 'playVRChat' });
}

/* === Communication === */
function sendToCS(m) {
    window.external.sendMessage(JSON.stringify(m));
}

function esc(s) {
    const d = document.createElement('div');
    d.textContent = s || '';
    return d.innerHTML;
}

function jsq(s) {
    return String(s || '').replace(/\\/g, '\\\\').replace(/'/g, "\\'").replace(/\r/g, '\\r').replace(/\n/g, '\\n');
}

function cssUrl(s) {
    return (s || '').replace(/'/g, '%27').replace(/\)/g, '%29');
}

function copyIdBadge(el, id) {
    navigator.clipboard.writeText(id).catch(() => {});
    const orig = el.innerHTML;
    el.innerHTML = '<span class="msi" style="font-size:12px;">check</span>Copied!';
    setTimeout(() => { el.innerHTML = orig; }, 1500);
}

function idBadge(id) {
    const safe = jsq(id);
    return `<span class="vrcn-id-clip" onclick="copyIdBadge(this,'${safe}')"><span class="msi" style="font-size:12px;">link</span>${esc(id)}</span>`;
}

// ── Location / instance type helpers (global) ──────────────────────────────

function parseFriendLocation(loc) {
    if (!loc || loc === 'private' || loc === 'offline' || loc === 'traveling') return { worldId: '', instanceType: loc || 'private' };
    var worldId = loc.includes(':') ? loc.split(':')[0] : loc;
    var instanceType = 'public';
    if (loc.includes('~private(')) instanceType = 'private';
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
    return { worldId, instanceType };
}

function getInstanceBadge(instanceType) {
    const t = instanceType || 'public';
    const labels = { 'public':'Public', 'friends':'Friends', 'friends+':'Friends+', 'hidden':'Friends+',
                     'invite_plus':'Invite+', 'private':'Invite', 'group':'Group',
                     'group-public':'Group Public', 'group-plus':'Group+', 'group-members':'Group' };
    const label = labels[t] || t.charAt(0).toUpperCase() + t.slice(1);
    let cls = 'public';
    if (t === 'friends' || t === 'friends+' || t === 'hidden') cls = 'friends';
    else if (t === 'invite_plus' || t === 'private') cls = 'private';
    else if (t.startsWith('group')) cls = 'group';
    return { cls, label };
}

/* ── Custom Dropdown (vn-select) ─────────────────────────────────────────── */
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
    const label = document.createElement('span');
    label.className = 'vn-select-label';
    const arrow = document.createElement('span');
    arrow.className = 'msi vn-select-arrow';
    arrow.textContent = 'expand_more';
    trigger.append(label, arrow);

    // Panel (the open list)
    const panel = document.createElement('div');
    panel.className = 'vn-select-panel';

    wrap.append(trigger, panel);
    el.parentNode.insertBefore(wrap, el);
    el.style.display = 'none';
    wrap.appendChild(el); // keep hidden select inside for form compat

    function isVrcPlus(value, text) {
        return /vrcplus/i.test(value) || /vrc\+/i.test(text);
    }

    function cleanText(text) {
        return text.replace(/\s*\[VRC\+\]/gi, '').trim();
    }

    function buildPanel() {
        panel.innerHTML = '';
        for (let i = 0; i < el.options.length; i++) {
            const opt = el.options[i];
            const item = document.createElement('div');
            item.className = 'vn-select-option' + (i === el.selectedIndex ? ' vn-active' : '');

            const span = document.createElement('span');
            span.textContent = cleanText(opt.text);
            item.appendChild(span);

            if (isVrcPlus(opt.value, opt.text)) {
                const badge = document.createElement('span');
                badge.className = 'vrcn-badge warn';
                badge.textContent = 'VRC+';
                item.appendChild(badge);
            }

            item.addEventListener('click', () => {
                el.selectedIndex = i;
                el.dispatchEvent(new Event('change', { bubbles: true }));
                syncLabel();
                close();
            });
            panel.appendChild(item);
        }
    }

    function syncLabel() {
        const opt = el.options[el.selectedIndex];
        label.textContent = opt ? cleanText(opt.text) : '';
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

function initAllVnSelects() {
    document.querySelectorAll('select:not([data-no-vn])').forEach(initVnSelect);
}

// === Force FFC All ===
function forceFfcAll() {
    const btn = document.getElementById('btnForceFfc');
    if (btn) btn.disabled = true;
    sendToCS({ action: 'forceFfcAll' });
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
    if (lbl)  lbl.textContent  = d.label || ('Caching... ' + (d.progress || 0) + '%');
    if (btn)  btn.disabled = true;
}

