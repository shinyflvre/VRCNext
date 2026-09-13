const WM_MAX = 12;
const WM_Z_BASE = 9000;
const WM_MIN_W = 560;
const WM_MIN_H = 360;
const WM_NARROW_W = 940;
const WM_DIRS = ['n', 's', 'e', 'w', 'ne', 'nw', 'se', 'sw'];

const WM_TYPES = {
    friend: {
        overlay: 'modalFriendDetail', content: 'friendDetailContent',
        open: 'openFriendDetail', close: 'closeFriendDetail',
        render: 'renderFriendDetail', state: '_fdWmState',
        icon: 'person', label: () => _wmT('nav.modal.friend', 'Profile'),
    },
    myprofile: {
        overlay: 'modalMyProfile', content: 'myProfileContent',
        open: 'openMyProfileModal', close: 'closeMyProfile',
        state: '_mypWmState', noId: true,
        icon: 'manage_accounts', label: () => _wmT('nav.modal.friend', 'Profile'),
    },
    group: {
        overlay: 'modalDetail', content: 'detailModalContent',
        open: 'openGroupDetail', close: 'closeGroupDetail',
        render: 'renderGroupDetail', state: '_gdWmState',
        icon: 'group', label: () => _wmT('nav.modal.group', 'Group'),
    },
    worldSearch: {
        overlay: 'modalDetail', content: 'detailModalContent',
        open: 'openWorldSearchDetail', close: 'closeWorldSearchDetail',
        render: 'renderWorldSearchDetail', state: '_wdWmState',
        icon: 'public', label: () => _wmT('nav.modal.world', 'World'),
    },
    world: {
        overlay: 'modalWorldDetail', content: 'worldDetailContent',
        open: 'openWorldDetail', close: 'closeWorldDetail',
        state: '_wdWmState',
        icon: 'public', label: () => _wmT('nav.modal.world', 'World'),
    },
    avatar: {
        overlay: 'modalAvatarDetail', content: 'avatarDetailContent',
        open: 'openAvatarDetail', close: 'closeAvatarDetail',
        render: 'renderAvatarDetail', state: '_avWmState',
        icon: 'checkroom', label: () => _wmT('nav.modal.avatar', 'Avatar'),
    },
    event: {
        overlay: 'modalDetail', content: 'detailModalContent',
        open: 'openEventDetail', close: 'closeEventDetail',
        render: 'renderEventDetail', pairId: true,
        icon: 'event', label: () => _wmT('nav.modal.event', 'Event'),
    },
    instance: {
        overlay: 'modalMyInstance', content: 'myInstanceContent',
        open: '_reopenCachedInstance', close: 'closeMyInstanceDetail',
        state: '_miWmState',
        icon: 'sensors', label: () => _wmT('nav.modal.instance', 'Instance'),
    },
};

let _wmWindows   = [];
let _wmFocused   = null;
let _wmScope     = null;
let _wmSeq       = 0;
let _wmZTop      = WM_Z_BASE;
let _wmShift     = false;
let _wmInternal  = false;
let _wmEnabled   = false;
let _wmSurfaced  = false;
const _wmSurfaceWindows = {};
const _wmSurfaceDocs = [];
const _wmPortals = [];
const _wmTemplates = {};

function wmEnabled() {
    return _wmEnabled;
}

function wmSetEnabled(on) {
    on = !!on;
    if (on === _wmEnabled) return;
    _wmEnabled = on;
    if (!on) wmCloseAll();
}

function wmCloseAll() {
    _wmScope = null;
    _wmFocused = null;
    [..._wmWindows].forEach(_wmDestroy);
    _wmWindows = [];
    _wmZTop = WM_Z_BASE;
    _wmSyncDock();
}

function _wmT(key, fallback) {
    return typeof t === 'function' ? t(key, fallback) : fallback;
}

function _wmEscAttr(s) {
    return String(s == null ? '' : s)
        .replace(/&/g, '&amp;').replace(/</g, '&lt;')
        .replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

function _wmIdSel(id) {
    return '[id="' + String(id).replace(/(["\\])/g, '\\$1') + '"]';
}

function _wmTrunc(s, max) {
    s = String(s || '');
    return s.length > max ? s.slice(0, max) + '…' : s;
}

const _wmRawGetById = Document.prototype.getElementById;
const _wmDocQS      = Document.prototype.querySelector;
const _wmDocQSA     = Document.prototype.querySelectorAll;
const _wmRawQS      = Element.prototype.querySelector;
const _wmRawQSA     = Element.prototype.querySelectorAll;

function _wmScopeRoot() {
    if (_wmScope) return _wmScope.el;
    if (_wmFocused && !_wmFocused.minimized) return _wmFocused.el;
    return null;
}

function _wmOwned(el) {
    return !!(el && el.closest && el.closest('.wm-window'));
}

function _wmOutsideWindows(sel, native) {
    const all = _wmDocQSA.call(document, sel);
    for (const el of all) if (!_wmOwned(el)) return el;
    return native;
}

let _wmLookupDepth = 0;

document.getElementById = function (id) {
    if (_wmLookupDepth) return _wmRawGetById.call(document, id);
    _wmLookupDepth++;
    try {
        const root = _wmScopeRoot();
        const sel = _wmIdSel(id);
        if (root) {
            const scoped = _wmRawQS.call(root, sel);
            if (scoped) return scoped;
        }
        const native = _wmRawGetById.call(document, id);
        if (native == null && _wmPortals.length) return _wmPortalQuery(sel, false);
        if (!_wmOwned(native)) return native;
        return root ? null : _wmOutsideWindows(sel, native);
    } finally {
        _wmLookupDepth--;
    }
};

document.querySelector = function (sel) {
    if (_wmLookupDepth) return _wmDocQS.call(document, sel);
    _wmLookupDepth++;
    try {
        const root = _wmScopeRoot();
        if (root) {
            let scoped = null;
            try { scoped = _wmRawQS.call(root, sel); } catch (e) { scoped = null; }
            if (scoped) return scoped;
        }
        let native = null;
        try { native = _wmDocQS.call(document, sel); } catch (e) { return null; }
        if (native == null && _wmPortals.length) return _wmPortalQuery(sel, false);
        if (!_wmOwned(native)) return native;
        return root ? null : _wmOutsideWindows(sel, native);
    } finally {
        _wmLookupDepth--;
    }
};

document.querySelectorAll = function (sel) {
    if (_wmLookupDepth) return _wmDocQSA.call(document, sel);
    _wmLookupDepth++;
    try {
        const root = _wmScopeRoot();
        if (root) {
            let scoped = null;
            try { scoped = _wmRawQSA.call(root, sel); } catch (e) { scoped = null; }
            if (scoped && scoped.length) return scoped;
        }
        const native = _wmDocQSA.call(document, sel);
        if (root && native.length && [...native].every(_wmOwned)) return _wmDocQSA.call(document, ':not(*)');
        return native;
    } finally {
        _wmLookupDepth--;
    }
};

function _wmStateFn(type) {
    const d = WM_TYPES[type];
    const fn = d && d.state ? window[d.state] : null;
    return typeof fn === 'function' ? fn : null;
}

function _wmSaveState(win) {
    const fn = _wmStateFn(win.type);
    if (fn) win.state = fn();
}

function _wmLoadState(win) {
    const fn = _wmStateFn(win.type);
    if (fn) fn(win.state || null);
}

function _wmRunIn(win, fn) {
    const prevScope = _wmScope;
    const prevFocus = _wmFocused;
    const swap = prevFocus !== win;
    if (swap) {
        if (prevFocus) _wmSaveState(prevFocus);
        _wmLoadState(win);
    }
    _wmScope = win;
    _wmFocused = win;
    try {
        return fn();
    } finally {
        _wmSaveState(win);
        _wmScope = prevScope;
        _wmFocused = prevFocus;
        if (swap && prevFocus) _wmLoadState(prevFocus);
    }
}

function _wmTemplate(type) {
    const d = WM_TYPES[type];
    if (!_wmTemplates[d.overlay]) {
        const src = _wmRawGetById.call(document, d.overlay);
        if (!src) return null;
        const tpl = src.cloneNode(true);
        tpl.removeAttribute('onclick');
        tpl.removeAttribute('style');
        tpl.classList.remove('fd-style-compact', 'wd-style-compact', 'gd-style-compact', 'av-style-compact', 'tl-style-compact');
        const body = _wmRawQS.call(tpl, _wmIdSel(d.content));
        if (body) body.innerHTML = '';
        _wmTemplates[d.overlay] = tpl;
    }
    const clone = _wmTemplates[d.overlay].cloneNode(true);
    clone.style.display = 'flex';
    return clone;
}

function _wmApplyChrome(win) {
    if (!win.el) return;
    const bar = _wmRawQS.call(win.el, '.fd-modal-bar');
    if (!bar) return;

    const actions = _wmRawQS.call(bar, '.fd-modal-bar-actions');
    if (actions && !_wmRawQS.call(actions, '.wm-btn-min')) {
        const btn = document.createElement('button');
        btn.className = 'btn-notif fd-action-btn wm-btn-min';
        btn.title = _wmT('common.minimize', 'Minimize');
        btn.innerHTML = `<span class="msi" style="font-size:20px;">remove</span>`;
        btn.addEventListener('click', e => { e.stopPropagation(); wmMinimize(win); });
        actions.insertBefore(btn, actions.firstChild);
    }
    _wmRenderCrumbs(win, bar);
}

function _wmRenderCrumbs(win, bar) {
    bar = bar || (win.el && _wmRawQS.call(win.el, '.fd-modal-bar'));
    const host = bar && _wmRawQS.call(bar, '.fd-modal-bar-crumbs');
    if (!host) return;

    const entries = win.stack.slice(0, win.idx + 1);
    const sig = win.idx + '|' + entries.map(e => e.type + ':' + e.id + ':' + e.id2 + ':' + e.label).join('|');
    if (host.dataset.wmCrumbs === sig) return;

    const start = Math.max(0, entries.length - 5);
    let html = '';
    if (start > 0) html += `<button class="tb-crumb" data-wm-go="0">···</button><span class="tb-crumb-sep">›</span>`;
    html += entries.slice(start).map((e, j) => {
        const idx = start + j;
        const name = e.label || WM_TYPES[e.type].label();
        const short = _wmTrunc(name, 14);
        if (idx === entries.length - 1) return `<span class="tb-crumb-current" title="${_wmEscAttr(name)}">${_wmEscAttr(short)}</span>`;
        return `<button class="tb-crumb" data-wm-go="${idx}" title="${_wmEscAttr(name)}">${_wmEscAttr(short)}</button>`;
    }).join('<span class="tb-crumb-sep">›</span>');
    host.innerHTML = html;
    host.dataset.wmCrumbs = sig;

    _wmRawQSA.call(host, '[data-wm-go]').forEach(b => {
        b.addEventListener('click', e => {
            e.stopPropagation();
            wmNavGoTo(win, parseInt(b.getAttribute('data-wm-go'), 10));
        });
    });
}

const WM_TILE_MS = 220;
const WM_ARROW_DIRS = { ArrowLeft: 'left', ArrowRight: 'right', ArrowUp: 'up', ArrowDown: 'down' };

let _wmTiling     = true;
let _wmHeldArrows = [];

function wmSetTiling(on) {
    _wmTiling = !!on;
}

function wmTilingEnabled() {
    return _wmTiling;
}

function wmIsEnabled() {
    return _wmEnabled;
}

function wmSyncKeybindHelp() {
    const rows = _wmDocQSA.call(document, '.kb-tiling');
    const show = _wmEnabled && _wmTiling;
    for (let i = 0; i < rows.length; i++) rows[i].style.display = show ? '' : 'none';
}

function _wmIsTypingTarget(el) {
    if (!el || !el.tagName) return false;
    const tag = el.tagName.toUpperCase();
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return true;
    return !!el.isContentEditable;
}

function _wmHoldArrow(dir) {
    _wmHeldArrows = _wmHeldArrows.filter(d => d !== dir);
    _wmHeldArrows.push(dir);
}

function _wmReleaseArrow(dir) {
    _wmHeldArrows = _wmHeldArrows.filter(d => d !== dir);
}

function _wmChordDir() {
    let h = null, v = null;
    for (let i = _wmHeldArrows.length - 1; i >= 0; i--) {
        const d = _wmHeldArrows[i];
        if (!h && (d === 'left' || d === 'right')) h = d;
        if (!v && (d === 'up'   || d === 'down'))  v = d;
    }
    if (h && v) return v + '-' + h;
    return h || v;
}

function _wmTileRect(dir, box) {
    const halfW  = Math.max(WM_MIN_W, Math.round(box.w / 2));
    const halfH  = Math.max(WM_MIN_H, Math.round(box.h / 2));
    const fullW  = Math.max(WM_MIN_W, box.w);
    const fullH  = Math.max(WM_MIN_H, box.h);
    const right  = Math.max(0, box.w - halfW);
    const bottom = Math.max(0, box.h - halfH);
    switch (dir) {
        case 'left':       return { x: 0,     y: 0,      w: halfW, h: fullH };
        case 'right':      return { x: right, y: 0,      w: halfW, h: fullH };
        case 'up':         return { x: 0,     y: 0,      w: fullW, h: halfH };
        case 'down':       return { x: 0,     y: bottom, w: fullW, h: halfH };
        case 'up-left':    return { x: 0,     y: 0,      w: halfW, h: halfH };
        case 'up-right':   return { x: right, y: 0,      w: halfW, h: halfH };
        case 'down-left':  return { x: 0,     y: bottom, w: halfW, h: halfH };
        case 'down-right': return { x: right, y: bottom, w: halfW, h: halfH };
        case 'max':        return { x: 0,     y: 0,      w: fullW, h: fullH };
    }
    return null;
}

function _wmTileAnimate(win) {
    if (!win.el) return;
    win.el.classList.add('wm-tiling');
    if (win.tileAnim) clearTimeout(win.tileAnim);
    win.tileAnim = setTimeout(() => {
        win.tileAnim = null;
        if (win.el) win.el.classList.remove('wm-tiling');
    }, WM_TILE_MS + 80);
}

function _wmTileAnimStop(win) {
    if (!win) return;
    if (win.tileAnim) clearTimeout(win.tileAnim);
    win.tileAnim = null;
    if (win.el) win.el.classList.remove('wm-tiling');
}

function wmTile(win, dir, animate) {
    if (!win || !win.el || win.minimized || win.surfaced) return false;
    const box = _wmLayerBox();
    if (!box) return false;
    const rect = _wmTileRect(dir, box);
    if (!rect) return false;

    win.userPlaced = true;
    win.tile = dir;
    if (animate === false) _wmTileAnimStop(win);
    else _wmTileAnimate(win);
    _wmApplySize(win, rect.w, rect.h);
    _wmMoveTo(win, rect.x, rect.y);
    return true;
}


function _wmMoveTo(win, x, y) {
    win.x = Math.round(x);
    win.y = Math.round(y);
    win.el.style.left = win.x + 'px';
    win.el.style.top  = win.y + 'px';
}

function _wmSizeOf(win) {
    return {
        w: win.w || win.el.offsetWidth,
        h: win.h || win.el.offsetHeight,
    };
}

function _wmClampInto(win, box) {
    if (win.surfaced) return;
    box = box || _wmLayerBox();
    if (!box || !win.el) return;
    let { w, h } = _wmSizeOf(win);
    if (w > box.w || h > box.h) {
        _wmApplySize(win, Math.max(WM_MIN_W, Math.min(w, box.w)), Math.max(WM_MIN_H, Math.min(h, box.h)));
        w = win.w;
        h = win.h;
    }
    _wmMoveTo(win, Math.max(0, Math.min(win.x, box.w - w)), Math.max(0, Math.min(win.y, box.h - h)));
}

function _wmCenter(win) {
    const box = _wmLayerBox();
    if (!box || !win.el) return;
    const { w, h } = _wmSizeOf(win);
    const off = (win.cascade || 0) * 26;
    _wmMoveTo(win, (box.w - w) / 2 + off, (box.h - h) / 2 + off);
    _wmClampInto(win, box);
}

function _wmRecenter(win) {
    if (win && win.el && !win.userPlaced && !win.surfaced) _wmCenter(win);
}

function _wmCreate(type) {
    const layer = _wmRawGetById.call(document, 'wmLayer');
    if (!layer) return null;
    const body = _wmTemplate(type);
    if (!body) return null;

    const el = document.createElement('div');
    el.className = 'wm-window';
    el.appendChild(body);

    el.style.left = '0px';
    el.style.top  = '0px';
    if (_wmSurfaceWanted()) el.style.visibility = 'hidden';

    const win = {
        id: ++_wmSeq, type, el, body,
        entityId: '', entityId2: '', label: '',
        stack: [], idx: -1,
        minimized: false, state: null,
        x: 0, y: 0, w: 0, h: 0,
        cascade: _wmWindows.length % 6, userPlaced: false,
    };
    el._wmWin = win;

    WM_DIRS.forEach(dir => {
        const g = document.createElement('div');
        g.className = 'wm-resize wm-resize-' + dir;
        g.addEventListener('pointerdown', e => _wmResizeStart(win, dir, e));
        el.appendChild(g);
    });

    el.addEventListener('pointerdown', e => _wmDragStart(win, e), true);

    win.observer = new MutationObserver(() => _wmApplyChrome(win));
    win.observer.observe(el, { childList: true, subtree: true });

    if (typeof ResizeObserver === 'function') {
        win.sizeObserver = new ResizeObserver(() => _wmRecenter(win));
        win.sizeObserver.observe(el);
    }

    layer.appendChild(el);
    _wmWindows.push(win);
    return win;
}

function _wmDestroy(win) {
    if (win.surfaceTimer) { clearTimeout(win.surfaceTimer); win.surfaceTimer = null; }
    win.surfaceDeferred = false;
    if (win.observer) { win.observer.disconnect(); win.observer = null; }
    if (win.rebindObserver) { win.rebindObserver.disconnect(); win.rebindObserver = null; }
    if (win.sizeObserver) { win.sizeObserver.disconnect(); win.sizeObserver = null; }
    if (win.el && win.el.parentNode) win.el.parentNode.removeChild(win.el);
    win.el = null;
    win.body = null;
    win.state = null;
}

function wmClose(win) {
    if (!win) return;
    const i = _wmWindows.indexOf(win);
    if (i >= 0) _wmWindows.splice(i, 1);
    if (_wmFocused === win) _wmFocused = null;
    if (_wmScope === win) _wmScope = null;
    _wmSurfaceRelease(win);
    _wmDestroy(win);
    _wmSyncDock();
}

function wmMinimize(win) {
    if (!win || win.minimized || !win.el) return;
    if (_wmFocused === win) { _wmSaveState(win); _wmFocused = null; }
    win.minimized = true;
    win.el.classList.add('wm-minimized');
    if (win.surfaceId) sendToCS({ action: 'wmSurfaceVisible', wmId: win.id, visible: false });
    _wmSyncDock();
}

function wmRestore(win) {
    if (!win || !win.el) return;
    win.minimized = false;
    win.el.classList.remove('wm-minimized');
    if (win.surfaceId) sendToCS({ action: 'wmSurfaceVisible', wmId: win.id, visible: true });
    else if (win.tile) wmTile(win, win.tile, false);
    else _wmClampInto(win);
    _wmSyncDock();
    wmFocus(win);
}

function wmFocus(win) {
    if (!win || win.minimized || !win.el) return;
    win.el.style.zIndex = String(++_wmZTop);
    const i = _wmWindows.indexOf(win);
    if (i >= 0) { _wmWindows.splice(i, 1); _wmWindows.push(win); }
    if (_wmFocused === win) return;
    if (_wmFocused) _wmSaveState(_wmFocused);
    _wmFocused = win;
    _wmLoadState(win);
    if (win.surfaced) sendToCS({ action: 'wmSurfaceActivate', wmId: win.id });
}

let _wmDrag = null;
let _wmResize = null;

function _wmLayerBox() {
    const layer = _wmRawGetById.call(document, 'wmLayer');
    if (!layer) return null;
    return { el: layer, w: layer.clientWidth, h: layer.clientHeight, top: layer.getBoundingClientRect().top };
}

function _wmApplySize(win, w, h) {
    win.w = Math.round(w);
    win.h = Math.round(h);
    win.el.classList.add('wm-sized');
    win.el.classList.toggle('wm-narrow', win.w < WM_NARROW_W);
    win.el.style.width  = win.w + 'px';
    win.el.style.height = win.h + 'px';
}

function _wmResizeStart(win, dir, e) {
    if (e.button !== 0) return;
    e.preventDefault();
    e.stopPropagation();
    wmFocus(win);
    if (!win.w || !win.h) _wmApplySize(win, win.el.offsetWidth, win.el.offsetHeight);
    _wmResize = { win, dir, sx: e.clientX, sy: e.clientY, x: win.x, y: win.y, w: win.w, h: win.h };
    _wmTileAnimStop(win);
    win.el.classList.add('wm-resizing');
}

function _wmResizeMove(e) {
    if (!_wmResize) return;
    const r = _wmResize, win = r.win;
    if (!win.el) return;
    const box = _wmLayerBox();
    if (!box) return;

    win.userPlaced = true;
    win.tile = null;
    const dx = e.clientX - r.sx;
    const dy = e.clientY - r.sy;
    let x = r.x, y = r.y, w = r.w, h = r.h;

    if (r.dir.indexOf('e') >= 0) w = Math.max(WM_MIN_W, r.w + dx);
    if (r.dir.indexOf('s') >= 0) h = Math.max(WM_MIN_H, r.h + dy);
    if (r.dir.indexOf('w') >= 0) { w = Math.max(WM_MIN_W, r.w - dx); x = r.x + (r.w - w); }
    if (r.dir.indexOf('n') >= 0) { h = Math.max(WM_MIN_H, r.h - dy); y = r.y + (r.h - h); }

    x = Math.max(0, x);
    y = Math.max(0, y);
    w = Math.max(WM_MIN_W, Math.min(w, box.w - x));
    h = Math.max(WM_MIN_H, Math.min(h, box.h - y));

    win.x = x;
    win.y = y;
    win.el.style.left = x + 'px';
    win.el.style.top  = y + 'px';
    _wmApplySize(win, w, h);
}

function _wmResizeEnd() {
    if (!_wmResize) return;
    _wmResize.win.el?.classList.remove('wm-resizing');
    _wmResize = null;
}

function _wmDragStart(win, e) {
    wmFocus(win);
    if (e.button !== 0) return;
    if (!e.target.closest || !e.target.closest('.fd-modal-bar')) return;
    if (e.target.closest('button, input, a, select, textarea, .tb-crumb, .vn-select')) return;
    if (win.surfaced) {
        e.preventDefault();
        sendToCS({ action: 'wmSurfaceDrag', wmId: win.id });
        return;
    }

    const layer = _wmRawGetById.call(document, 'wmLayer');
    _wmDrag = {
        win,
        dx: e.clientX - win.x,
        dy: e.clientY - win.y - (layer ? layer.getBoundingClientRect().top : 0),
    };
    _wmTileAnimStop(win);
    win.el.classList.add('wm-dragging');
    e.preventDefault();
}

function _wmDragMove(e) {
    if (!_wmDrag) return;
    const win = _wmDrag.win;
    if (!win.el) return;
    const box = _wmLayerBox();
    if (!box) return;
    const { w, h } = _wmSizeOf(win);
    const maxX = Math.max(0, box.w - w);
    const maxY = Math.max(0, box.h - h);
    win.userPlaced = true;
    win.tile = null;
    _wmMoveTo(win,
        Math.min(maxX, Math.max(0, e.clientX - _wmDrag.dx)),
        Math.min(maxY, Math.max(0, e.clientY - box.top - _wmDrag.dy)));
}

function _wmDragEnd() {
    if (!_wmDrag) return;
    _wmDrag.win.el?.classList.remove('wm-dragging');
    _wmDrag = null;
}

document.addEventListener('pointermove', e => { _wmDragMove(e); _wmResizeMove(e); });
document.addEventListener('pointerup', () => { _wmDragEnd(); _wmResizeEnd(); });
document.addEventListener('pointercancel', () => { _wmDragEnd(); _wmResizeEnd(); });

let _wmReflowPending = false;

function wmReflow() {
    if (_wmReflowPending) return;
    _wmReflowPending = true;
    requestAnimationFrame(() => {
        _wmReflowPending = false;
        const box = _wmLayerBox();
        if (!box) return;
        _wmWindows.forEach(win => {
            if (!win.el) return;
            if (win.tile) wmTile(win, win.tile, false);
            else _wmClampInto(win, box);
        });
    });
}

window.addEventListener('resize', wmReflow);

function _wmWatchLayer() {
    if (typeof ResizeObserver !== 'function') return;
    const layer = _wmRawGetById.call(document, 'wmLayer');
    if (!layer) { document.addEventListener('DOMContentLoaded', _wmWatchLayer, { once: true }); return; }
    new ResizeObserver(wmReflow).observe(layer);
}
_wmWatchLayer();

function _wmSyncDock() {
    const dock = _wmRawGetById.call(document, 'wmDock');
    if (!dock) return;
    const mins = _wmWindows.filter(w => w.minimized);
    dock.classList.toggle('wm-dock-shown', mins.length > 0);
    dock.innerHTML = mins.map(w => {
        const d = WM_TYPES[w.type];
        const name = w.label || d.label();
        return `<button class="wm-dock-item" data-wm-id="${w.id}" title="${_wmEscAttr(name)}">
            <span class="msi">${_wmEscAttr(d.icon)}</span>
            <span class="wm-dock-label">${_wmEscAttr(_wmTrunc(name, 22))}</span>
            <span class="wm-dock-close" data-wm-close="1"><span class="msi">close</span></span>
        </button>`;
    }).join('');
    _wmRawQSA.call(dock, '.wm-dock-item').forEach(btn => {
        btn.addEventListener('click', e => {
            const win = _wmWindows.find(w => String(w.id) === btn.getAttribute('data-wm-id'));
            if (!win) return;
            if (e.target.closest('[data-wm-close]')) { e.stopPropagation(); wmClose(win); return; }
            wmRestore(win);
        });
    });
}

function _wmMount(win, type, id, id2) {
    if (win.type !== type) {
        if (WM_TYPES[win.type].overlay !== WM_TYPES[type].overlay) {
            const body = _wmTemplate(type);
            if (!body) return false;
            win.el.replaceChild(body, win.body);
            win.body = body;
        }
        win.type = type;
        win.state = null;
    }
    win.entityId  = id  || '';
    win.entityId2 = id2 || '';

    const d = WM_TYPES[type];
    const openFn = window[d.open];
    if (typeof openFn !== 'function') return false;

    _wmRunIn(win, () => {
        _wmInternal = true;
        try {
            if (d.pairId)    openFn(id, id2);
            else if (d.noId) openFn();
            else             openFn(id);
        } finally {
            _wmInternal = false;
        }
    });
    return true;
}

function wmNavPush(win, type, id, label, id2) {
    if (!win || !win.el || !WM_TYPES[type]) return false;
    win.stack = win.stack.slice(0, win.idx + 1);
    win.stack.push({ type, id: id || '', id2: id2 || '', label: label || '' });
    win.idx = win.stack.length - 1;
    win.label = label || '';
    if (!_wmMount(win, type, id, id2)) return false;
    _wmApplyChrome(win);
    _wmSyncDock();
    return true;
}

function wmNavGoTo(win, idx) {
    if (!win || idx < 0 || idx >= win.stack.length || idx === win.idx) return;
    win.idx = idx;
    const e = win.stack[idx];
    win.label = e.label || '';
    _wmMount(win, e.type, e.id, e.id2);
    _wmApplyChrome(win);
    _wmSyncDock();
}

function wmSetLabel(win, label) {
    if (!win || !label || win.label === label) return;
    win.label = label;
    if (win.stack[win.idx]) win.stack[win.idx].label = label;
    _wmRenderCrumbs(win);
    _wmSyncDock();
    if (win.surfaceId) sendToCS({ action: 'wmSurfaceTitle', wmId: win.id, title: label });
}

function wmOpen(type, id, label, id2) {
    if (!_wmEnabled || !WM_TYPES[type]) return false;
    const origin = _wmCurrent();

    const existing = _wmFindByEntity(type, id, id2);
    if (existing) {
        if (existing.minimized) wmRestore(existing);
        else wmFocus(existing);
        const name = existing.label || label || WM_TYPES[type].label();
        if (typeof showToast === 'function') {
            showToast(false, typeof tf === 'function'
                ? tf('wm.already_open', { name }, name + ' is already open')
                : name + ' is already open');
        }
        return true;
    }

    if (_wmWindows.length >= WM_MAX) {
        if (typeof showToast === 'function') {
            showToast(false, _wmT('wm.limit_reached', 'Maximum of 12 windows reached'));
        }
        return true;
    }
    const win = _wmCreate(type);
    if (!win) return false;
    wmFocus(win);
    if (!wmNavPush(win, type, id, label, id2)) { wmClose(win); return false; }
    _wmCenter(win);
    if (_wmSurfaceWanted()) {
        win.surfaceOrigin = origin;
        if (WM_TYPES[type].render) {
            win.surfaceDeferred = true;
            win.el.style.visibility = 'hidden';
            win.surfaceTimer = setTimeout(() => {
                if (!win.surfaceDeferred || !win.el) return;
                win.surfaceDeferred = false;
                _wmSurfaceRequest(win, origin);
            }, 3000);
        } else {
            _wmSurfaceRequest(win, origin);
        }
    }
    return true;
}

function _wmCurrent() {
    if (_wmScope) return _wmScope;
    return (_wmFocused && !_wmFocused.minimized && _wmFocused.el) ? _wmFocused : null;
}

function _wmFindByEntity(type, id, id2) {
    const hit = w => w && w.el && w.type === type &&
        w.entityId === (id || '') && (!id2 || w.entityId2 === id2);
    if (hit(_wmScope)) return _wmScope;
    if (hit(_wmFocused)) return _wmFocused;
    return _wmWindows.find(hit) || null;
}

document.addEventListener('pointerdown', e => {
    _wmShift = e.shiftKey;
    if (!e.target.closest || e.target.closest('.wm-window')) return;
    if (_wmFocused && !e.target.closest('#wmDock')) {
        _wmSaveState(_wmFocused);
        _wmFocused = null;
    }
}, true);

document.addEventListener('keydown', e => {
    _wmShift = e.shiftKey;
    if (e.key === 'Escape' && _wmCurrent()) {
        e.stopPropagation();
        wmClose(_wmCurrent());
        return;
    }

    if (!_wmEnabled || !_wmTiling) return;
    if (!e.shiftKey || e.ctrlKey || e.altKey || e.metaKey) return;
    if (_wmIsTypingTarget(e.target)) return;
    const tileWin = _wmCurrent();
    if (!tileWin) return;

    let done = true;
    const arrow = WM_ARROW_DIRS[e.key];
    if (arrow) {
        _wmHoldArrow(arrow);
        done = wmTile(tileWin, _wmChordDir());
    } else if (e.key === 'M' || e.key === 'm') {
        wmMinimize(tileWin);
    } else if (e.key === 'F' || e.key === 'f') {
        wmTile(tileWin, 'max');
    } else {
        done = false;
    }
    if (done) { e.preventDefault(); e.stopPropagation(); }
}, true);

document.addEventListener('keyup', e => {
    _wmShift = e.shiftKey;
    const arrow = WM_ARROW_DIRS[e.key];
    if (arrow) _wmReleaseArrow(arrow);
    else if (e.key === 'Shift') _wmHeldArrows = [];
}, true);

window.addEventListener('blur', () => { _wmHeldArrows = []; });

document.addEventListener('focusin', e => {
    const host = e.target.closest ? e.target.closest('.wm-window') : null;
    if (host && host._wmWin) wmFocus(host._wmWin);
}, true);

function _wmRouteOpen(type, id, id2, label) {
    if (_wmInternal) return false;
    if (_wmShift && _wmEnabled) {
        _wmShift = false;
        return wmOpen(type, id, label, id2);
    }
    const cur = _wmCurrent();
    if (cur) return wmNavPush(cur, type, id, label, id2);
    return false;
}

function _wmWrapOpen(type) {
    const d = WM_TYPES[type];
    const orig = window[d.open];
    if (typeof orig !== 'function') return;
    window[d.open] = function (...args) {
        const id  = d.noId ? '' : (args[0] || '');
        const id2 = d.pairId ? (args[1] || '') : '';
        if ((d.noId || id) && _wmRouteOpen(type, id, id2, '')) return;
        return orig.apply(this, args);
    };
}

function _wmWrapClose(type) {
    const d = WM_TYPES[type];
    const orig = window[d.close];
    if (typeof orig !== 'function') return;
    window[d.close] = function (...args) {
        const cur = _wmCurrent();
        if (!_wmInternal && cur && cur.type === type) { wmClose(cur); return; }
        return orig.apply(this, args);
    };
}

function _wmWrapRender(type) {
    const d = WM_TYPES[type];
    const orig = window[d.render];
    if (typeof orig !== 'function') return;
    window[d.render] = function (payload, ...rest) {
        if (!_wmWindows.length || !payload || !payload.id) return orig.call(this, payload, ...rest);
        const win = d.pairId
            ? _wmFindByEntity(type, payload.ownerId || payload.groupId || '', payload.id)
            : _wmFindByEntity(type, payload.id, '');
        if (!win) return orig.call(this, payload, ...rest);
        return _wmRunIn(win, () => {
            const r = orig.call(this, payload, ...rest);
            wmSetLabel(win, payload.displayName || payload.name || payload.title || '');
            _wmApplyChrome(win);
            _wmRecenter(win);
            if (win.surfaceDeferred) {
                win.surfaceDeferred = false;
                clearTimeout(win.surfaceTimer);
                _wmSurfaceRequest(win, win.surfaceOrigin);
            }
            return r;
        });
    };
}

function _wmWrapNav() {
    const origOpen = window.navOpenModal;
    if (typeof origOpen === 'function') {
        window.navOpenModal = function (type, id, label, id2) {
            if (id && WM_TYPES[type] && _wmRouteOpen(type, id, id2, label)) return;
            return origOpen.call(this, type, id, label, id2);
        };
    }

    const origLabel = window.navUpdateLabel;
    if (typeof origLabel === 'function') {
        window.navUpdateLabel = function (label) {
            const cur = _wmCurrent();
            if (cur) { wmSetLabel(cur, label); return; }
            return origLabel.call(this, label);
        };
    }

    const origSet = window.navSetCurrent;
    if (typeof origSet === 'function') {
        window.navSetCurrent = function (...args) {
            if (_wmCurrent()) return;
            return origSet.apply(this, args);
        };
    }

    const origClear = window.navClear;
    if (typeof origClear === 'function') {
        window.navClear = function (...args) {
            if (_wmCurrent()) return;
            return origClear.apply(this, args);
        };
    }
}

(function _wmInit() {
    Object.keys(WM_TYPES).forEach(type => {
        _wmWrapOpen(type);
        _wmWrapClose(type);
        if (WM_TYPES[type].render) _wmWrapRender(type);
    });
    _wmWrapNav();
})();

function wmSetSurfaced(on) {
    _wmSurfaced = !!on;
}

function wmSurfacedAvailable() {
    return !!(window.photino && window.photino.isHost === false && typeof window.photino.surfaceWindow === 'function');
}

function _wmSurfaceWanted() {
    return _wmSurfaced && wmSurfacedAvailable() && typeof sendToCS === 'function';
}

function _wmSurfaceRequest(win, origin) {
    const { w, h } = _wmSizeOf(win);
    win.surfacePending = true;
    win.el.style.visibility = 'hidden';
    sendToCS({
        action: 'wmSurfaceOpen',
        wmId: win.id,
        nearWmId: origin && origin.surfaceId ? origin.id : 0,
        title: win.label || WM_TYPES[win.type].label(),
        width: Math.max(WM_MIN_W, Math.round(w || 0)),
        height: Math.max(WM_MIN_H, Math.round(h || 0)),
    });
}

function wmOnSurfaceCreated(payload) {
    const wmId = payload && payload.wmId;
    const surfaceId = (payload && payload.surfaceId) || 0;
    const win = _wmWindows.find(w => w.id === wmId);
    if (!win || !win.el) {
        if (surfaceId) sendToCS({ action: 'wmSurfaceClose', wmId });
        return;
    }
    win.surfacePending = false;
    if (!surfaceId) { win.el.style.visibility = ''; return; }
    win.surfaceId = surfaceId;
    const sw = _wmSurfaceWindows[surfaceId];
    if (sw) _wmAdopt(win, sw);
}

function wmOnSurfaceClosed(payload) {
    const win = _wmWindows.find(w => w.id === (payload && payload.wmId));
    if (!win) return;
    win.surfaceClosedByNative = true;
    wmClose(win);
}

function _wmSurfaceRelease(win) {
    if (win.surfaced) _wmUnportalAll(win);
    if (win.surfaceId && !win.surfaceClosedByNative) sendToCS({ action: 'wmSurfaceClose', wmId: win.id });
    win.surfaceId = 0;
    win.surfaced = false;
    win.surfaceWin = null;
}

function _wmMirrorRoot() {
    const src = document.documentElement;
    for (const doc of _wmSurfaceDocs) {
        const dst = doc.documentElement;
        if (!dst) continue;
        for (const a of Array.from(dst.attributes)) if (!src.hasAttribute(a.name)) dst.removeAttribute(a.name);
        for (const a of Array.from(src.attributes)) if (dst.getAttribute(a.name) !== a.value) dst.setAttribute(a.name, a.value);
        if (doc.body && doc.body.className !== document.body.className) doc.body.className = document.body.className;
    }
}

let _wmRootObserver = null;

function _wmPrepareSurfaceDoc(doc) {
    if (_wmSurfaceDocs.includes(doc)) return;
    _wmSurfaceDocs.push(doc);
    const head = doc.head;
    head.innerHTML = '';
    const meta = doc.createElement('meta');
    meta.setAttribute('charset', 'utf-8');
    head.appendChild(meta);
    _wmDocQSA.call(document, 'head link[rel="stylesheet"], head style').forEach(n => head.appendChild(doc.importNode(n, true)));
    doc.body.setAttribute('style', 'margin:0;overflow:hidden;background:var(--bg,#111);');
    _wmMirrorRoot();
    if (!_wmRootObserver) {
        _wmRootObserver = new MutationObserver(_wmMirrorRoot);
        _wmRootObserver.observe(document.documentElement, { attributes: true });
        _wmRootObserver.observe(document.body, { attributes: true, attributeFilter: ['class'] });
    }
    if (typeof window.VrcnCtxAttach === 'function') window.VrcnCtxAttach(doc);
    if (typeof vnTooltipAttach === 'function') vnTooltipAttach(doc);
    _wmShareGlobals(doc.defaultView);
    _wmBindSurfaceDoc(doc);
}

function _wmShareGlobals(sw) {
    if (!sw) return;
    for (const k of Object.getOwnPropertyNames(window)) {
        if (k in sw) continue;
        let v;
        try { v = window[k]; } catch (e) { continue; }
        if (typeof v === 'function') { try { sw[k] = v; } catch (e) { } }
    }
}

function _wmRebindInline(root, shallow) {
    if (!root || root.nodeType !== 1) return;
    const nodes = shallow ? [root] : [root, ..._wmRawQSA.call(root, '*')];
    for (const el of nodes) {
        if (!el.attributes) continue;
        for (const a of Array.from(el.attributes)) {
            if (!/^on[a-z]+$/i.test(a.name)) continue;
            const code = a.value;
            const type = a.name.slice(2).toLowerCase();
            const bound = el.__wmBound || (el.__wmBound = {});
            if (bound[type] && bound[type].code === code) { el[a.name] = null; continue; }
            let fn;
            try { fn = new Function('event', code); } catch (e) { continue; }
            if (bound[type]) el.removeEventListener(type, bound[type].listener);
            const listener = function (ev) { return fn.call(this, ev); };
            bound[type] = { code, listener };
            el[a.name] = null;
            el.addEventListener(type, listener);
        }
    }
}


function _wmOverlayHidden(el) {
    if (el.style.display === 'none') return true;
    if (el.style.display) return false;
    try { return el.ownerDocument.defaultView.getComputedStyle(el).display === 'none'; } catch (e) { return false; }
}

function _wmMaybePortal(el) {
    if (!el || el.nodeType !== 1 || el.__wmPortal) return;
    if (el.parentNode !== document.body || !(el.classList.contains('modal-overlay') || el.hasAttribute('data-wm-portal'))) return;
    if (_wmOverlayHidden(el)) return;
    const cur = _wmCurrent();
    if (!cur || !cur.surfaced || !cur.surfaceWin) return;
    _wmPortalOverlay(el, cur);
}

function _wmPortalOverlay(el, win) {
    let doc;
    try { doc = win.surfaceWin.document; } catch (e) { return; }
    if (!doc || !doc.body) return;
    const placeholder = document.createComment('wm-portal');
    el.parentNode.insertBefore(placeholder, el);
    doc.body.appendChild(doc.adoptNode(el));
    _wmRebindInline(el, false);
    const observer = new MutationObserver(muts => {
        for (const m of muts) {
            if (m.type === 'childList') {
                m.addedNodes.forEach(n => _wmRebindInline(n, false));
            } else if (m.type === 'attributes') {
                if (/^on[a-z]+$/i.test(m.attributeName)) _wmRebindInline(m.target, true);
                else if (m.target === el && (m.attributeName === 'style' || m.attributeName === 'class') && _wmOverlayHidden(el)) _wmUnportal(el);
            }
        }
    });
    observer.observe(el, { childList: true, subtree: true, attributes: true });
    el.__wmPortal = { placeholder, win, observer };
    _wmPortals.push(el);
}

function _wmUnportal(el) {
    const p = el.__wmPortal;
    if (!p) return;
    p.observer.disconnect();
    delete el.__wmPortal;
    const i = _wmPortals.indexOf(el);
    if (i >= 0) _wmPortals.splice(i, 1);
    if (!el.parentNode) { if (p.placeholder.parentNode) p.placeholder.remove(); return; }
    const moved = document.adoptNode(el);
    if (p.placeholder.parentNode) { p.placeholder.parentNode.insertBefore(moved, p.placeholder); p.placeholder.remove(); }
    else document.body.appendChild(moved);
}

function _wmUnportalAll(win) {
    [..._wmPortals].forEach(el => { if (!win || (el.__wmPortal && el.__wmPortal.win === win)) _wmUnportal(el); });
}

function _wmPortalQuery(sel, all) {
    const out = [];
    for (const el of _wmPortals) {
        try {
            if (el.matches(sel)) { if (!all) return el; out.push(el); }
            const inner = all ? _wmRawQSA.call(el, sel) : _wmRawQS.call(el, sel);
            if (!all) { if (inner) return inner; }
            else out.push(...inner);
        } catch (e) { }
    }
    return all ? out : null;
}

function _wmWatchOverlays() {
    if (!document.body) { document.addEventListener('DOMContentLoaded', _wmWatchOverlays, { once: true }); return; }
    const obs = new MutationObserver(muts => {
        if (!_wmWindows.some(w => w.surfaced)) return;
        for (const m of muts) {
            if (m.type === 'childList') m.addedNodes.forEach(_wmMaybePortal);
            else _wmMaybePortal(m.target);
        }
    });
    obs.observe(document.body, { childList: true, attributes: true, attributeFilter: ['style', 'class'], subtree: true });
}
_wmWatchOverlays();

function _wmWatchInline(win) {
    if (win.rebindObserver) return;
    win.rebindObserver = new MutationObserver(muts => {
        for (const m of muts) {
            if (m.type === 'childList') {
                m.addedNodes.forEach(n => _wmRebindInline(n, false));
            } else if (m.type === 'attributes' && /^on[a-z]+$/i.test(m.attributeName) && m.target.getAttribute(m.attributeName) != null) {
                _wmRebindInline(m.target, true);
            }
        }
    });
    win.rebindObserver.observe(win.el, { childList: true, subtree: true, attributes: true });
}

function _wmHostOf(target) {
    const el = target && target.closest ? target : (target && target.parentElement) || null;
    const host = el && el.closest ? el.closest('.wm-window') : null;
    return host && host._wmWin ? host._wmWin : null;
}

function _wmBindSurfaceDoc(doc) {
    doc.addEventListener('wheel', e => {
        if (!e.ctrlKey) return;
        e.preventDefault();
        if (typeof _stepGuiZoom === 'function') _stepGuiZoom(e.deltaY < 0 ? 1 : -1);
    }, { passive: false });
    doc.addEventListener('keydown', e => {
        if (!e.ctrlKey) return;
        if (e.key === '0') { e.preventDefault(); if (typeof applyGuiZoom === 'function') applyGuiZoom(1); try { autoSave(); } catch {} }
        else if (e.key === '+' || e.key === '=') { e.preventDefault(); if (typeof _stepGuiZoom === 'function') _stepGuiZoom(1); }
        else if (e.key === '-') { e.preventDefault(); if (typeof _stepGuiZoom === 'function') _stepGuiZoom(-1); }
    });
    doc.addEventListener('pointerdown', e => {
        _wmShift = e.shiftKey;
        const win = _wmHostOf(e.target);
        if (win) wmFocus(win);
    }, true);
    doc.addEventListener('keydown', e => {
        _wmShift = e.shiftKey;
        if (e.key === 'Escape') {
            const portal = _wmPortals.filter(p => p.ownerDocument === doc && !_wmOverlayHidden(p)).pop();
            if (portal) {
                e.stopPropagation();
                const closeBtn = _wmRawQS.call(portal, '.launch-close');
                if (closeBtn) closeBtn.click();
                else portal.style.display = 'none';
                return;
            }
            const win = _wmHostOf(e.target) || _wmHostOf(doc.activeElement) || _wmWindows.find(w => w.surfaceWin === doc.defaultView);
            if (win && !_wmIsTypingTarget(e.target)) { e.stopPropagation(); wmClose(win); }
        }
    }, true);
    new MutationObserver(muts => {
        for (const m of muts) m.removedNodes.forEach(n => { if (n.__wmPortal && !n.parentNode) _wmUnportal(n); });
    }).observe(doc.body, { childList: true });
    doc.addEventListener('keyup', e => { _wmShift = e.shiftKey; }, true);
    doc.addEventListener('focusin', e => {
        const win = _wmHostOf(e.target);
        if (win) wmFocus(win);
    }, true);
}

function _wmAdopt(win, sw) {
    if (!win.el || win.surfaced) return;
    let doc;
    try { doc = sw.document; } catch (e) { return; }
    if (!doc || !doc.body) return;
    _wmPrepareSurfaceDoc(doc);
    doc.body.appendChild(doc.adoptNode(win.el));
    _wmRebindInline(win.el, false);
    _wmWatchInline(win);
    win.surfaced = true;
    win.surfacePending = false;
    win.surfaceWin = sw;
    win.userPlaced = true;
    win.tile = null;
    win.el.classList.add('wm-surfaced', 'wm-sized');
    win.el.classList.remove('wm-minimized');
    win.el.style.visibility = '';
    win.el.style.left = '0px';
    win.el.style.top = '0px';
    win.el.style.width = '100%';
    win.el.style.height = '100%';
    const narrow = () => { if (win.el) win.el.classList.toggle('wm-narrow', sw.innerWidth < WM_NARROW_W); };
    narrow();
    sw.addEventListener('resize', narrow);
    wmFocus(win);
    _wmRevealSurface(win, sw);
}

function _wmStylesReady(doc) {
    const links = Array.from(_wmRawQSA.call(doc.head, 'link[rel="stylesheet"]'));
    const pending = links.filter(l => !l.sheet).map(l => new Promise(res => {
        l.addEventListener('load', res, { once: true });
        l.addEventListener('error', res, { once: true });
    }));
    const fonts = doc.fonts && doc.fonts.ready ? doc.fonts.ready.catch(() => {}) : Promise.resolve();
    const timeout = new Promise(res => setTimeout(res, 2000));
    return Promise.race([Promise.all([...pending, fonts]), timeout]);
}

function _wmRevealSurface(win, sw) {
    _wmStylesReady(sw.document).then(() => new Promise(res => sw.requestAnimationFrame(() => sw.requestAnimationFrame(res)))).then(() => {
        if (!win.el || !win.surfaceId || win.minimized) return;
        sendToCS({ action: 'wmSurfaceVisible', wmId: win.id, visible: true });
    });
}

window.addEventListener('photinosurface', e => {
    const d = e.detail || {};
    if (d.type === 'ready') {
        _wmSurfaceWindows[d.id] = d.window;
        const win = _wmWindows.find(w => w.surfaceId === d.id && !w.surfaced);
        if (win) _wmAdopt(win, d.window);
    } else if (d.type === 'removed') {
        delete _wmSurfaceWindows[d.id];
    }
});

function _wmParseColor(value) {
    const v = String(value || '').trim();
    if (/^#[0-9a-f]{3}$/i.test(v) || /^#[0-9a-f]{6}$/i.test(v)) return v;
    const m = v.match(/^rgba?\((\d+)\s*,\s*(\d+)\s*,\s*(\d+)/i);
    if (!m) return '';
    return '#' + [m[1], m[2], m[3]].map(n => Math.max(0, Math.min(255, +n)).toString(16).padStart(2, '0')).join('');
}

let _wmLastWindowBackground = '';

function _wmSyncWindowBackground() {
    if (!wmSurfacedAvailable() || typeof sendToCS !== 'function') return;
    let color = '';
    try { color = _wmParseColor(getComputedStyle(document.body).backgroundColor); } catch (e) { }
    if (!color || color === '#000000') {
        try { color = _wmParseColor(getComputedStyle(document.documentElement).getPropertyValue('--bg')) || color; } catch (e) { }
    }
    if (!color) return;
    try { window.top.document.documentElement.style.background = color; } catch (e) { }
    if (color === _wmLastWindowBackground) return;
    _wmLastWindowBackground = color;
    sendToCS({ action: 'windowBackground', color });
}

document.documentElement.addEventListener('themechange', _wmSyncWindowBackground);
window.addEventListener('load', () => setTimeout(_wmSyncWindowBackground, 500));
