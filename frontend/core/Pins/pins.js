/* === Pins === */

const PINS_MAX = 10;
const _PINS_KEY = 'vrcnext_pins_v1';

let _pins = [];

function _pinsLoad() {
    try {
        const raw = localStorage.getItem(_PINS_KEY);
        const arr = raw ? JSON.parse(raw) : [];
        _pins = Array.isArray(arr) ? arr.filter(p => p && p.type && p.id).slice(0, PINS_MAX) : [];
    } catch { _pins = []; }
}

function _pinsSave() {
    try { localStorage.setItem(_PINS_KEY, JSON.stringify(_pins)); } catch { }
}


function pinsHas(type, id) {
    return _pins.some(p => p.type === type && p.id === id);
}

function pinsList() { _pinsRefresh(); return _pins.slice(); }

// Data lookup

// Pulls name/image from the caches SmartSearch already reads, so a pin created from a
// context menu that only knows an id still gets a label and a thumbnail.
function _pinsVar(name) {
    try { return eval(`typeof ${name} !== 'undefined' ? ${name} : undefined`); } catch { return undefined; }
}

function _pinsSources(type) {
    const S = {
        user:   ['vrcFriendsData', 'searchState.people.results', 'currentInstanceData.users'],
        world:  ['favWorldsData', '_visitedWorldsData', '_myWorldsData', 'dashWorldCache',
                 'searchState.worlds.results', '_popularCache.worlds', '_activeCache.worlds', '_recentCache.worlds'],
        group:  ['myGroups', 'searchState.groups.results'],
        avatar: ['avatarsData', 'favAvatarsData', 'avatarFavData', 'hiddenAvatarData',
                 '_recentAvatarsData', 'avatarSearchResults'],
    }[type] || [];
    const out = [];
    for (const path of S) {
        const root = _pinsVar(path.split('.')[0]);
        if (root === undefined) continue;
        let v = root;
        for (const part of path.split('.').slice(1)) {
            if (v == null) break;
            v = v[part];
        }
        if (v) out.push(v);
    }
    return out;
}

function _pinsFind(type, id) {
    for (const src of _pinsSources(type)) {
        const arr = Array.isArray(src) ? src : (typeof src === 'object' ? Object.values(src) : []);
        const hit = arr.find(x => x && (x.id === id || x.vrc_id === id));
        if (hit) return hit;
    }
    return null;
}

function _pinsResolve(type, id) {
    let m = _pinsFind(type, id);

    if (type === 'user') {
        if (!m) {
            const cur = _pinsVar('currentFriendDetail');
            if (cur && cur.id === id) m = cur;
        }
        if (!m) {
            const me = _pinsVar('currentVrcUser');
            if (me && me.id === id) m = me;
        }
        if (!m) return null;
        return { name: m.displayName || '', image: m.image || m.currentAvatarThumbnailImageUrl || '' };
    }
    if (type === 'world') {
        if (!m) {
            const d = _pinsVar('_currentWorldDetail');
            if (d && d.id === id) m = d;
        }
        if (!m) return null;
        return { name: m.name || '', image: m.thumbnailImageUrl || m.imageUrl || '' };
    }
    if (type === 'group') {
        if (!m) {
            const d = _pinsVar('_currentGroupDetailFull') || _pinsVar('currentGroupDetail');
            if (d && d.id === id) m = d;
        }
        if (!m) return null;
        return { name: m.name || '', image: m.iconUrl || m.bannerUrl || '', sub: m.shortCode || '' };
    }
    if (type === 'avatar') {
        if (!m) return null;
        return { name: m.name || '', image: m.thumbnailImageUrl || m.imageUrl || m.image_url || '' };
    }
    return null;
}

const _PINS_TYPE_ICON = {
    user: 'person', world: 'public', avatar: 'checkroom',
    group: 'groups', event: 'event', feature: 'widgets',
};

function pinsTypeIcon(type) {
    return _PINS_TYPE_ICON[type] || 'push_pin';
}

function _pinsTypeLabel(type) {
    switch (type) {
        case 'user':    return t('pins.type.user', 'Profile');
        case 'world':   return t('pins.type.world', 'World');
        case 'avatar':  return t('pins.type.avatar', 'Avatar');
        case 'group':   return t('pins.type.group', 'Group');
        case 'event':   return t('pins.type.event', 'Event');
        case 'feature': return t('pins.type.feature', 'Feature');
        default:        return '';
    }
}

// Mutations

function pinsAdd(entry) {
    if (!entry || !entry.type || !entry.id) return false;
    if (pinsHas(entry.type, entry.id)) return false;

    if (_pins.length >= PINS_MAX) {
        if (typeof showToast === 'function')
            showToast(false, tf('pins.toast.limit', { max: PINS_MAX }, `Pin limit reached (${PINS_MAX})`));
        return false;
    }

    const resolved = _pinsResolve(entry.type, entry.id) || {};
    _pins.push({
        type:    entry.type,
        id:      entry.id,
        // Left empty on purpose when unknown - the id is only a display fallback, and an
        // empty name is what marks the pin as still needing a lookup.
        name:    resolved.name || entry.name || '',
        image:   resolved.image || entry.image || '',
        sub:     resolved.sub || entry.sub || '',
        ownerId: entry.ownerId || '',
        tab:     typeof entry.tab === 'number' ? entry.tab : null,
        icon:    entry.icon || '',
    });
    _pinsSave();
    _pinsBackfill();
    _pinsRenderMenu();
    if (typeof showToast === 'function') showToast(true, t('pins.toast.added', 'Pinned'));
    return true;
}

// Profiles pinned from search or a group log are not in any local cache, so ask the
// backend for the display name and avatar and patch the pin once it answers.
function _pinsRefresh() {
    let changed = false;
    _pins.forEach(p => {
        if (p.name === p.id) { p.name = ''; changed = true; }
        const r = _pinsResolve(p.type, p.id);
        if (!r) return;
        if (r.name && r.name !== p.name)   { p.name = r.name;   changed = true; }
        if (r.image && r.image !== p.image) { p.image = r.image; changed = true; }
        if (r.sub && r.sub !== p.sub)       { p.sub = r.sub;     changed = true; }
    });
    if (changed) _pinsSave();
    return changed;
}

function _pinsBackfill() {
    _pinsRefresh();
    if (typeof sendToCS !== 'function') return;
    _pins.forEach(p => {
        if (p.type !== 'user') return;
        if (_pinsResolve('user', p.id)) return;
        sendToCS({ action: 'vrcGetUserBasic', userId: p.id, contextId: 'pin' });
    });
}

function pinsOnUserBasic(payload) {
    if (!payload || payload.contextId !== 'pin') return;
    const pin = _pins.find(p => p.type === 'user' && p.id === payload.id);
    if (!pin) return;
    if (!payload.displayName && !payload.image) return;
    if (payload.displayName) pin.name = payload.displayName;
    if (payload.image) pin.image = payload.image;
    _pinsSave();
    _pinsRenderMenu();
}

function pinsRemove(type, id) {
    const before = _pins.length;
    _pins = _pins.filter(p => !(p.type === type && p.id === id));
    if (_pins.length === before) return false;
    _pinsSave();
    _pinsRenderMenu();
    if (typeof showToast === 'function') showToast(true, t('pins.toast.removed', 'Pin removed'));
    return true;
}

function pinsOpen(type, id) {
    const pin = _pins.find(p => p.type === type && p.id === id);
    if (!pin) return;
    _pinsCloseMenu();

    switch (pin.type) {
        case 'user':
            if (typeof openFriendDetail === 'function') openFriendDetail(pin.id);
            break;
        case 'world':
            if (typeof openWorldSearchDetail === 'function') openWorldSearchDetail(pin.id);
            break;
        case 'group':
            if (typeof openGroupDetail === 'function') openGroupDetail(pin.id);
            break;
        case 'avatar':
            if (typeof openAvatarDetail === 'function') openAvatarDetail(pin.id);
            break;
        case 'event':
            if (typeof openEventDetail === 'function') openEventDetail(pin.ownerId || '', pin.id);
            break;
        case 'feature':
            if (typeof showTab === 'function' && typeof pin.tab === 'number') showTab(pin.tab);
            break;
    }
}

// Context menu contract

// Returns the pin/unpin entry for a context menu, or null when the type is unknown.
function pinsContextItem(type, id, data) {
    if (!type || !id) return null;
    if (pinsHas(type, id)) {
        return {
            icon: 'push_pin',
            label: t('pins.remove', 'Remove pin'),
            action: () => pinsRemove(type, id),
        };
    }
    return {
        icon: 'push_pin',
        label: t('pins.add', 'Add to pins'),
        action: () => pinsAdd({ type, id, ...(data || {}) }),
    };
}

// Menu

// Open/close and positioning are handled by the shared .tb-menu-item wiring in
// taskbar.js; this only needs to close the menu after a pin was activated.
function _pinsCloseMenu() {
    document.getElementById('tbMenuPins')?.classList.remove('open');
}

// Built via DOM rather than innerHTML so pinned names cannot inject markup.
function _pinsRenderMenu() {
    _pinsRefresh();
    if (typeof dashHeroRefreshPins === 'function') dashHeroRefreshPins();
    const drop = document.getElementById('pinsDropdown');
    if (!drop) return;
    drop.innerHTML = '';

    if (!_pins.length) {
        const empty = document.createElement('div');
        empty.className = 'pins-empty';
        empty.textContent = t('pins.empty', 'No pins yet.');
        drop.appendChild(empty);
        return;
    }

    _pins.forEach(pin => {
        const row = document.createElement('div');
        row.className = 'pins-item ss-item';
        row.dataset.pinType = pin.type;
        row.dataset.pinId   = pin.id;
        const label = pin.name || pin.id;

        if (pin.type === 'feature') {
            const ph = document.createElement('div');
            ph.className = 'ss-item-img-placeholder';
            ph.style.borderRadius = '8px';
            const span = document.createElement('span');
            span.className = 'msi';
            span.style.cssText = 'font-size:16px;color:var(--accent);';
            span.textContent = pin.icon || _PINS_TYPE_ICON.feature;
            ph.appendChild(span);
            row.appendChild(ph);
        } else if (pin.image) {
            const img = document.createElement('img');
            img.className = 'ss-item-img';
            img.src = pin.image;
            img.onerror = function () {
                const ph = document.createElement('div');
                ph.className = 'ss-item-img-placeholder';
                ph.textContent = (label[0] || '?').toUpperCase();
                this.parentNode.replaceChild(ph, this);
            };
            row.appendChild(img);
        } else {
            const ph = document.createElement('div');
            ph.className = 'ss-item-img-placeholder';
            ph.textContent = (label[0] || '?').toUpperCase();
            row.appendChild(ph);
        }

        const info = document.createElement('div');
        info.className = 'ss-item-info';
        const nameEl = document.createElement('div');
        nameEl.className = 'ss-item-name';
        nameEl.textContent = label;
        info.appendChild(nameEl);
        const subEl = document.createElement('div');
        subEl.className = 'ss-item-sub';
        subEl.textContent = pin.sub ? `${_pinsTypeLabel(pin.type)} · ${pin.sub}` : _pinsTypeLabel(pin.type);
        info.appendChild(subEl);
        row.appendChild(info);

        const badge = document.createElement('span');
        badge.className = 'msi pins-item-type';
        badge.textContent = _PINS_TYPE_ICON[pin.type] || 'push_pin';
        row.appendChild(badge);

        row.addEventListener('mousedown', e => {
            if (e.button !== 0) return;
            e.preventDefault();
            pinsOpen(pin.type, pin.id);
        });

        drop.appendChild(row);
    });
}

function pinsInit() {
    _pinsLoad();
    _pinsBackfill();
    _pinsRenderMenu();
}

window.pinsContextItem  = pinsContextItem;
window.pinsOpen         = pinsOpen;
window.pinsAdd          = pinsAdd;
window.pinsRemove       = pinsRemove;
window.pinsHas          = pinsHas;
window.pinsList         = pinsList;
window.pinsTypeIcon     = pinsTypeIcon;
window.pinsInit         = pinsInit;
window.pinsOnUserBasic  = pinsOnUserBasic;

// Self-initialise: this file loads after init.js, so init.js cannot call pinsInit.
pinsInit();

// Rows are built with textContent, so the [data-i18n] sweep in applyTranslations()
// cannot reach them. The translation bundle also arrives asynchronously after this
// file first renders, which is why the initial pass still shows English fallbacks.
document.documentElement.addEventListener('languagechange', () => _pinsRenderMenu());
