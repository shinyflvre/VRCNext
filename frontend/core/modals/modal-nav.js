let _navStack        = [];
let _navIdx          = -1;
let _navCurrentEntry = null;
let _navBackdropEl   = null;
let _mnActions       = [];

function _mnActionHtml(a, asText) {
    if (a.dropdown) {
        const items = a.dropdown.filter(Boolean).map(_tbDropdownItem).join('');
        if (asText) {
            return `<div class="tb-modal-action-wrap"><button class="tb-modal-action" onclick="_tbToggleDropdown(this)">${esc(a.label || a.title || '')}<span class="msi tb-modal-dd-caret">expand_more</span></button><div class="tb-modal-dropdown">${items}</div></div>`;
        }
        return `<div class="tb-modal-action-wrap"><button class="btn-notif fd-action-btn" title="${esc(a.title || a.label || '')}" onclick="_tbToggleDropdown(this)"><span class="msi" style="font-size:20px;">${esc(a.icon || 'more_horiz')}</span></button><div class="tb-modal-dropdown">${items}</div></div>`;
    }
    if (asText) {
        return `<button class="tb-modal-action${a.danger ? ' tb-modal-action-danger' : ''}"${a.disabled ? ' disabled' : ''} onclick="${a.onclick}">${esc(a.label || a.title || '')}</button>`;
    }
    const dangerCls = a.dangerSolid ? ' fd-action-danger fd-action-danger-solid' : (a.danger ? ' fd-action-danger' : '');
    return `<button class="btn-notif fd-action-btn${dangerCls}"${a.disabled ? ' disabled' : ''} title="${esc(a.title || a.label || '')}" onclick="${a.onclick}"><span class="msi${a.iconClass ? ' ' + esc(a.iconClass) : ''}" style="font-size:20px;">${esc(a.icon)}</span></button>`;
}

function _mnActionsHtml(actions, asText) {
    return (actions || []).filter(Boolean).map(a => _mnActionHtml(a, asText)).join(asText ? '<div class="tb-sep"></div>' : '');
}

function _mnActiveBar() {
    const bars = document.querySelectorAll('.fd-modal-bar');
    for (const b of bars) if (b.offsetParent !== null) return b;
    return null;
}

function renderModalActions(actions) {
    actions = (actions || []).filter(Boolean);
    _mnActions = actions;
    return `<div class="fd-modal-bar"><div class="fd-modal-bar-crumbs">${_mnCrumbsHtml()}</div><div class="fd-modal-bar-actions">${_mnActionsHtml(actions, false)}</div></div>`;
}

function renderModalBar(title, actions, opts) {
    opts = opts || {};
    const label = String(title == null ? '' : title);
    const cls   = 'fd-modal-bar' + (opts.flush ? ' fd-modal-bar-flush' : '');
    const idAttr = opts.titleId ? ` id="${_esc(opts.titleId)}"` : '';
    return `<div class="${cls}"><div class="fd-modal-bar-crumbs"><span class="fd-modal-bar-title"${idAttr} title="${_esc(label)}">${_esc(label)}</span></div><div class="fd-modal-bar-actions">${opts.extra || ''}${_mnActionsHtml(actions, false)}</div></div>`;
}

const _MODAL_SHARE_KINDS = {
    friend: { path: 'user',   id: 'Copy User ID',   link: 'Copy User Link',   idDone: 'User ID copied to clipboard',   linkDone: 'Profile link copied to clipboard' },
    world:  { path: 'world',  id: 'Copy World ID',  link: 'Copy World Link',  idDone: 'World ID copied to clipboard',  linkDone: 'World link copied to clipboard' },
    avatar: { path: 'avatar', id: 'Copy Avatar ID', link: 'Copy Avatar Link', idDone: 'Avatar ID copied to clipboard', linkDone: 'Avatar link copied to clipboard' },
    group:  { path: 'group',  id: 'Copy Group ID',  link: 'Copy Group Link',  idDone: 'Group ID copied to clipboard',  linkDone: 'Group link copied to clipboard' },
};

function modalShareAction(kind, id, label) {
    const k = _MODAL_SHARE_KINDS[kind];
    const url = `https://vrchat.com/home/${k.path}/${id}`;
    const copy = (text, key, fb) => `navigator.clipboard.writeText('${jsq(text)}').then(()=>showToast(true,t('context_menu.${kind}.${key}','${jsq(fb)}')))`;
    return {
        icon: 'link_2',
        title: t('common.share', 'Share'),
        label: label || t('common.share', 'Share'),
        dropdown: [
            { icon: 'id_card', label: t(`context_menu.${kind}.copy_id`, k.id), onclick: copy(id, 'id_copied', k.idDone) },
            { icon: 'link_2', label: t(`context_menu.${kind}.copy_link`, k.link), onclick: copy(url, 'share_copied', k.linkDone) },
        ],
    };
}

function modalCloseAction(onclick) {
    return { icon: 'close', title: typeof t === 'function' ? t('common.close', 'Close') : 'Close', onclick };
}

function refreshModalActions(actions) {
    actions = (actions || []).filter(Boolean);
    _mnActions = actions;
    const el = _mnActiveBar()?.querySelector('.fd-modal-bar-actions');
    if (el) el.innerHTML = _mnActionsHtml(actions, false);
}


let _modalRefreshTimer = null;
function triggerModalRefresh(action) {
    document.documentElement.classList.add('modal-refreshing');
    if (_modalRefreshTimer) clearTimeout(_modalRefreshTimer);
    _modalRefreshTimer = setTimeout(() => {
        _modalRefreshTimer = null;
        document.documentElement.classList.remove('modal-refreshing');
    }, 1200);
    if (action) sendToCS(action);
}

function _tbDropdownItem(o) {
    if (o.submenu) {
        const sub = o.submenu.filter(Boolean).map(_tbDropdownItem).join('');
        return `<div class="tb-modal-dd-sub-wrap"><button class="tb-modal-dd-item">${o.icon ? `<span class="msi">${esc(o.icon)}</span>` : ''}<span>${esc(o.label || '')}</span><span class="msi tb-modal-dd-arrow">chevron_right</span></button><div class="tb-modal-dropdown tb-modal-dd-sub">${sub}</div></div>`;
    }
    return `<button class="tb-modal-dd-item${o.active ? ' active' : ''}"${o.disabled ? ' disabled' : ''} onclick="${o.disabled ? '' : '_tbCloseDropdowns();' + o.onclick}">${o.icon ? `<span class="msi">${esc(o.icon)}</span>` : ''}<span>${esc(o.label || '')}</span></button>`;
}

function _tbToggleDropdown(btn) {
    const wrap = btn.closest('.tb-modal-action-wrap');
    if (!wrap) return;
    const wasOpen = wrap.classList.contains('open');
    _tbCloseDropdowns();
    if (!wasOpen) {
        wrap.classList.add('open');
        setTimeout(() => document.addEventListener('click', _tbDropdownOutside), 0);
    }
}

let _tbSubTimer = null;

function _tbOpenSubWrap(wrap) {
    const st = window.safeTriangle;
    const cur = document.querySelector('.tb-modal-dd-sub-wrap.open');
    if (cur && cur !== wrap && !cur.contains(wrap) && st && st.isProtected()) return;

    if (_tbSubTimer) { clearTimeout(_tbSubTimer); _tbSubTimer = null; }
    if (wrap.classList.contains('open')) return;
    document.querySelectorAll('.tb-modal-dd-sub-wrap.open').forEach(w => {
        if (w !== wrap && !w.contains(wrap)) w.classList.remove('open');
    });
    wrap.classList.add('open');
    if (st) st.register(wrap.querySelector('.tb-modal-dd-sub'), wrap);
}

function _tbScheduleSubClose() {
    const st = window.safeTriangle;
    const open = document.querySelector('.tb-modal-dd-sub-wrap.open');
    if (!open) return;
    if (_tbSubTimer) clearTimeout(_tbSubTimer);
    if (st && st.isProtected()) {
        _tbSubTimer = setTimeout(_tbScheduleSubClose, 40);
        return;
    }
    _tbSubTimer = setTimeout(() => {
        document.querySelectorAll('.tb-modal-dd-sub-wrap.open').forEach(w => w.classList.remove('open'));
        if (st) st.reset();
    }, st ? st.cfg.closeDelay : 200);
}

document.addEventListener('mouseover', e => {
    if (!e.target.closest) return;
    if (!document.querySelector('.tb-modal-action-wrap.open')) return;
    const wrap = e.target.closest('.tb-modal-dd-sub-wrap');
    if (wrap) { _tbOpenSubWrap(wrap); return; }
    if (e.target.closest('.tb-modal-dropdown')) _tbScheduleSubClose();
});

function _tbCloseDropdowns() {
    document.querySelectorAll('.tb-modal-action-wrap.open, .tb-modal-dd-sub-wrap.open').forEach(w => w.classList.remove('open'));
    if (window.safeTriangle) window.safeTriangle.reset();
    document.removeEventListener('click', _tbDropdownOutside);
}

function _tbDropdownOutside(e) {
    if (!e.target.closest('.tb-modal-action-wrap')) _tbCloseDropdowns();
}

function _navSyncTaskbar() {
    const bc = _mnActiveBar()?.querySelector('.fd-modal-bar-crumbs');
    if (bc) bc.innerHTML = _mnCrumbsHtml();
}

function _mnCrumbsHtml() {
    let entries, curIdx;
    if (_navIdx >= 0 && _navStack.length > 0) { entries = _navStack.slice(0, _navIdx + 1); curIdx = entries.length - 1; }
    else if (_navCurrentEntry && _navCurrentEntry.id) { entries = [_navCurrentEntry]; curIdx = 0; }
    else return '';
    const start = Math.max(0, entries.length - 5);
    let html = '';
    if (start > 0) html += `<button class="tb-crumb" onclick="navGoTo(0)">···</button><span class="tb-crumb-sep">›</span>`;
    html += entries.slice(start).map((e, j) => {
        const idx = start + j;
        const name = e.label || _navTypeLabel(e.type);
        const short = _trunc(name, 14);
        if (idx === curIdx) return `<span class="tb-crumb-current" title="${_esc(name)}">${_esc(short)}</span>`;
        return `<button class="tb-crumb" title="${_esc(name)}" onclick="navGoTo(${idx})">${_esc(short)}</button>`;
    }).join('<span class="tb-crumb-sep">›</span>');
    return html;
}

function navSetCurrent(type, id, id2) {
    _navCurrentEntry = { type, id: id || '', id2: id2 || '', label: '' };
    _navSyncTaskbar();
}

function navOpenModal(type, id, label, id2) {
    if (!id) return;

    const _stackedTl = document.getElementById('modalDetail2');
    if (_stackedTl && _stackedTl.style.display === 'flex') _stackedTl.style.display = 'none';

    _navCaptureTab(_navLiveEntry());

    if (_navIdx === -1 && _navCurrentEntry && _navCurrentEntry.id) {
        _navStack.push({ ..._navCurrentEntry });
        _navIdx = 0;
    }

    const cur = _navIdx >= 0 ? _navStack[_navIdx] : null;
    if (cur && cur.type === type && cur.id === id && cur.id2 === (id2 || '')) return;

    const leavingEntry = cur ? { ...cur } : null;

    _navStack = _navStack.slice(0, _navIdx + 1);
    _navStack.push({ type, id, label: label || '', id2: id2 || '' });
    _navIdx = _navStack.length - 1;

    if (_navStack.length >= 2) {
        _navShowBackdrop();
        document.documentElement.classList.add('modal-nav-active');
    }

    if (leavingEntry && _navSameOverlay(leavingEntry.type, type)) {
        // Same overlay (e.g. friend→friend): animate box out first, then reopen
        _navAnimateLeaveThen(leavingEntry, () => {
            _navDoOpen(type, id, id2);
            _navRender();
        });
    } else {
        // Different overlays: animate leave async, open new immediately
        if (leavingEntry) _navAnimateLeave(leavingEntry);
        _navDoOpen(type, id, id2);
        _navRender();
    }
}

function navGoTo(idx) {
    if (idx < 0 || idx >= _navStack.length || idx === _navIdx) return;

    _navCaptureTab(_navStack[_navIdx]);

    const leavingEntry = _navStack[_navIdx] ? { ..._navStack[_navIdx] } : null;
    const targetEntry  = { ..._navStack[idx] };

    _navIdx = idx;
    _navCurrentEntry = { ...targetEntry };
    _navRender();

    if (leavingEntry && _navSameOverlay(leavingEntry.type, targetEntry.type)) {
        _navAnimateLeaveThen(leavingEntry, () => {
            _navDoOpen(targetEntry.type, targetEntry.id, targetEntry.id2);
            _navRestoreTab(targetEntry);
        });
    } else {
        if (leavingEntry) _navAnimateLeave(leavingEntry);
        _navDoOpen(targetEntry.type, targetEntry.id, targetEntry.id2);
        _navRestoreTab(targetEntry);
    }
}

function navClear() {
    _navStack        = [];
    _navIdx          = -1;
    _navCurrentEntry = null;
    _navTabPending   = null;
    document.documentElement.classList.remove('modal-nav-instant');
    document.documentElement.classList.remove('modal-nav-active');
    _navHideBackdrop();
    _navRender();
}

const _MODAL_RELEASE_TARGETS = [
    ['modalFriendDetail', 'friendDetailContent'],
    ['modalWorldDetail',  'worldDetailContent'],
    ['modalDetail',       'detailModalContent'],
    ['modalDetail2',      'detailModalContent2'],
    ['modalAvatarDetail', 'avatarDetailContent'],
    ['modalInstanceInfo', 'instanceInfoContent'],
    ['modalMyInstance',   'myInstanceContent'],
    ['modalFtGpsDetail',  'ftGpsDetailContent'],
    ['modalMyProfile',    'myProfileContent'],
];

function releaseModalContent(overlayId, contentId) {
    const ov = document.getElementById(overlayId);
    if (!ov || ov.style.display !== 'none') return false;
    ov.querySelectorAll('img').forEach(img => { img.src = PLACEHOLDER; });
    ov.querySelectorAll('video').forEach(v => { try { v.pause(); } catch {} v.removeAttribute('src'); try { v.load(); } catch {} });
    ov.querySelectorAll('[style*="background"]').forEach(el => { el.style.backgroundImage = ''; });
    const c = document.getElementById(contentId);
    if (c) c.innerHTML = '';
    return true;
}

function releaseClosedModals() {
    for (const [ov, c] of _MODAL_RELEASE_TARGETS) releaseModalContent(ov, c);
    [typeof _fdBannerImgs !== 'undefined' ? _fdBannerImgs : null,
     typeof _worldBannerImgs !== 'undefined' ? _worldBannerImgs : null,
     typeof _miBannerImgs !== 'undefined' ? _miBannerImgs : null].forEach(cache => {
        if (!cache) return;
        for (const k of Object.keys(cache)) delete cache[k];
    });
}

function navUpdateLabel(label) {
    if (_navCurrentEntry) _navCurrentEntry.label = label;
    if (_navIdx >= 0 && _navStack[_navIdx]) _navStack[_navIdx].label = label;
    _navSyncTaskbar();
}

function _navDoOpen(type, id, id2) {
    switch (type) {
        case 'friend':      openFriendDetail(id);           break;
        case 'world':       openWorldDetail(id);            break;
        case 'worldSearch': openWorldSearchDetail(id);      break;
        case 'avatar':      openAvatarDetail(id);           break;
        case 'group':       openGroupDetail(id);            break;
        case 'event':       openEventDetail(id, id2);       break;
        case 'instance':    if (typeof _reopenCachedInstance === 'function') _reopenCachedInstance(id); break;
        case 'myprofile':   if (typeof openMyProfileModal === 'function') openMyProfileModal(); break;
        case 'photo':       if (typeof openPhotoDetail === 'function') openPhotoDetail(id); break;
        case 'tlEvent':     if (typeof openTlDetail === 'function') openTlDetail(id); break;
        case 'ftEvent':     if (typeof openFtDetail === 'function') openFtDetail(id); break;
        case 'ftGps':       if (typeof openFtGpsDetail === 'function') openFtGpsDetail(id); break;
    }
}

function _navOverlayIdForType(type) {
    switch (type) {
        case 'friend':      return 'modalFriendDetail';
        case 'world':       return 'modalWorldDetail';
        case 'worldSearch': return 'modalDetail';
        case 'avatar':      return 'modalAvatarDetail';
        case 'group':       return 'modalDetail';
        case 'event':       return 'modalDetail';
        case 'instance':    return 'modalMyInstance';
        case 'myprofile':   return 'modalMyProfile';
        case 'photo':       return 'photoDetailModal';
        case 'tlEvent':     return 'modalDetail';
        case 'ftEvent':     return 'modalDetail';
        case 'ftGps':       return 'modalFtGpsDetail';
        default:            return null;
    }
}

function _navSameOverlay(typeA, typeB) {
    return _navOverlayIdForType(typeA) === _navOverlayIdForType(typeB);
}

function _navTabKey(btn) {
    if (!btn) return '';
    if (btn.dataset && btn.dataset.fdtab) return btn.dataset.fdtab;
    const m = /\(\s*'([^']+)'/.exec(btn.getAttribute('onclick') || '');
    return m ? m[1] : '';
}

const _NAV_TABS = {
    friend: {
        active: () => _navTabKey(document.querySelector('#modalFriendDetail .fd-tab.active')),
        btn:    tab => document.querySelector(`#modalFriendDetail .fd-tab[data-fdtab="${tab}"]`),
        curId:  () => (typeof currentFriendDetail !== 'undefined' && currentFriendDetail) ? (currentFriendDetail.id || '') : '',
    },
    group: {
        active: () => _navTabKey(document.querySelector('#detailModalContent .fd-tab.active[onclick^="switchGdTab("]')),
        btn:    tab => document.querySelector(`#detailModalContent .fd-tab[onclick^="switchGdTab('${tab}'"]`),
        curId:  () => (window._currentGroupDetail && window._currentGroupDetail.id) || '',
    },
    worldSearch: {
        active: () => _navTabKey(document.querySelector('#detailModalContent .fd-tab.active[onclick^="switchWdTab("]')),
        btn:    tab => document.querySelector(`#detailModalContent .fd-tab[onclick^="switchWdTab('${tab}'"]`),
        curId:  () => (typeof _wdCurrentWorldId !== 'undefined' && _wdCurrentWorldId) ? _wdCurrentWorldId : '',
    },
    avatar: {
        active: () => _navTabKey(document.querySelector('#avatarDetailContent .fd-tab.active[onclick^="switchAvTab("]')),
        btn:    tab => document.querySelector(`#avatarDetailContent .fd-tab[onclick^="switchAvTab('${tab}'"]`),
        curId:  () => (typeof _avDetailData !== 'undefined' && _avDetailData) ? (_avDetailData.id || '') : '',
    },
    myprofile: {
        active: () => _navTabKey(document.querySelector('#mypBox .fd-tab.active[onclick^="switchMypTab("]')),
        btn:    tab => document.querySelector(`#mypBox .fd-tab[onclick^="switchMypTab('${tab}'"]`),
    },
};

function _navLiveEntry() {
    return (_navIdx >= 0 && _navStack[_navIdx]) ? _navStack[_navIdx] : _navCurrentEntry;
}

function _navCaptureTab(entry) {
    if (!entry) return;
    const d = _NAV_TABS[entry.type];
    if (!d) return;
    const tab = d.active();
    if (tab) entry.tab = tab;
}

let _navTabPending = null;
const _NAV_TAB_MAX_FRAMES = 120;

function _navRestoreTab(entry) {
    _navTabPending = null;
    if (!entry || !entry.tab) return;
    const d = _NAV_TABS[entry.type];
    if (!d) return;

    const token = { tab: entry.tab, id: entry.id || '', frames: 0 };
    const tryApply = () => {
        if (d.curId) {
            const cur = d.curId();
            if (!cur || (token.id && cur !== token.id)) return false;
        }
        const btn = d.btn(token.tab);
        if (!btn) return false;
        if (d.active() !== token.tab) btn.click();
        return true;
    };

    if (tryApply()) return;
    _navTabPending = token;
    const tick = () => {
        if (_navTabPending !== token) return;
        if (token.frames++ > _NAV_TAB_MAX_FRAMES) { _navTabPending = null; return; }
        if (tryApply()) { _navTabPending = null; return; }
        requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
}

function _navBoxForEntry(entry) {
    if (!entry) return null;
    const ovId = _navOverlayIdForType(entry.type);
    if (!ovId) return null;
    const ov = document.getElementById(ovId);
    return ov ? ov.querySelector('.modal-box') : null;
}

// Animate box out, then call onDone (used when same overlay is reused).
// Cuts in at 80ms — box is mostly faded by then — so enter starts almost in parallel.
function _navAnimateLeaveThen(entry, onDone) {
    const box = _navBoxForEntry(entry);
    if (!box) {
        _navCloseForEntry(entry);
        onDone();
        return;
    }
    box.classList.remove('nav-leaving');
    void box.offsetWidth;
    box.classList.add('nav-leaving');
    setTimeout(() => {
        box.classList.remove('nav-leaving');
        _navCloseForEntry(entry);
        onDone();
    }, 80);
}

// Animate box out, close overlay after animation (used when switching overlays)
function _navAnimateLeave(entry) {
    const box = _navBoxForEntry(entry);
    if (!box) {
        _navCloseForEntry(entry);
        return;
    }
    box.classList.remove('nav-leaving');
    void box.offsetWidth;
    box.classList.add('nav-leaving');
    setTimeout(() => {
        box.classList.remove('nav-leaving');
        _navCloseForEntry(entry);
    }, 110);
}

function _navShowBackdrop() {
    if (!_navBackdropEl) {
        _navBackdropEl = document.createElement('div');
        _navBackdropEl.id = 'modalNavBackdrop';
        document.body.appendChild(_navBackdropEl);
    }
    _navBackdropEl.style.display = 'block';
}

function _navHideBackdrop() {
    if (_navBackdropEl) _navBackdropEl.style.display = 'none';
}


function _navCloseForEntry(entry) {
    if (!entry) return;
    switch (entry.type) {
        case 'friend':
            if (typeof closeFriendDetail === 'function') closeFriendDetail(true);
            break;
        case 'world':
            if (typeof closeWorldDetail === 'function') closeWorldDetail(true);
            break;
        case 'worldSearch':
            if (typeof closeWorldSearchDetail === 'function') closeWorldSearchDetail(true);
            break;
        case 'avatar':
            if (typeof closeAvatarDetail === 'function') closeAvatarDetail(true);
            break;
        case 'group':
        case 'event': {
            const md = document.getElementById('modalDetail');
            if (md) md.style.display = 'none';
            break;
        }
        case 'instance': {
            const mi = document.getElementById('modalMyInstance');
            if (mi) mi.style.display = 'none';
            break;
        }
        case 'myprofile': {
            const mp = document.getElementById('modalMyProfile');
            if (mp) mp.style.display = 'none';
            break;
        }
        case 'photo':
            if (typeof closePhotoDetail === 'function') closePhotoDetail(true);
            break;
        case 'tlEvent':
        case 'ftEvent':
            if (typeof closeTlDetail === 'function') closeTlDetail(true);
            break;
        case 'ftGps':
            if (typeof closeFtGpsDetail === 'function') closeFtGpsDetail(true);
            break;
    }
}

const _NAV_SHELLS = [
    { overlay: 'modalFriendDetail', bar: 'fd_navBar', p: 'fd' },
    { overlay: 'modalWorldDetail',  bar: 'wd_navBar', p: 'wd' },
    { overlay: 'modalDetail',       bar: 'dt_navBar', p: 'dt' },
    { overlay: 'modalAvatarDetail', bar: 'av_navBar', p: 'av' },
    { overlay: 'modalMyInstance',   bar: 'mi_navBar', p: 'mi' },
];

const _NAV_SLOTS = 5;

function _navRender() {
    for (const s of _NAV_SHELLS) {
        const bar = document.getElementById(s.bar);
        if (bar) bar.style.display = 'none';
    }
    _navSyncTaskbar();
}

function _trunc(s, max = 12) {
    s = String(s || '');
    return s.length > max ? s.slice(0, max) + '…' : s;
}

function _esc(s) {
    return String(s || '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}

function _navTypeLabel(type) {
    const labels = {
        friend:      typeof t === 'function' ? t('nav.modal.friend',      'Profile') : 'Profile',
        world:       typeof t === 'function' ? t('nav.modal.world',       'World')   : 'World',
        worldSearch: typeof t === 'function' ? t('nav.modal.world',       'World')   : 'World',
        avatar:      typeof t === 'function' ? t('nav.modal.avatar',      'Avatar')  : 'Avatar',
        group:       typeof t === 'function' ? t('nav.modal.group',       'Group')   : 'Group',
        event:       typeof t === 'function' ? t('nav.modal.event',       'Event')   : 'Event',
        instance:    typeof t === 'function' ? t('nav.modal.instance',    'Instance'): 'Instance',
        myprofile:   typeof t === 'function' ? t('nav.modal.friend',      'Profile') : 'Profile',
        photo:       typeof t === 'function' ? t('timeline.photo',        'Photo')   : 'Photo',
        tlEvent:     typeof t === 'function' ? t('nav.timeline',          'Timeline'): 'Timeline',
        ftEvent:     typeof t === 'function' ? t('nav.timeline',          'Timeline'): 'Timeline',
        ftGps:       typeof t === 'function' ? t('nav.modal.instance',    'Instance'): 'Instance',
    };
    return labels[type] || type;
}

function jsonHighlight(obj) {
    const json = JSON.stringify(obj, null, 2);
    const re = /("(\\u[a-zA-Z0-9]{4}|\\[^u]|[^\\"])*"(\s*:)?|\b(true|false|null)\b|-?\d+(?:\.\d*)?(?:[eE][+\-]?\d+)?)/g;
    let result = '';
    let last = 0;
    let m;
    while ((m = re.exec(json)) !== null) {
        const pre = json.slice(last, m.index).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
        result += pre;
        const match = m[0];
        const safe = match.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
        let cls = 'json-num';
        if (/^"/.test(match))       cls = /:$/.test(match) ? 'json-key' : 'json-str';
        else if (/true|false/.test(match)) cls = 'json-bool';
        else if (/null/.test(match))       cls = 'json-null';
        result += `<span class="${cls}">${safe}</span>`;
        last = m.index + match.length;
    }
    result += json.slice(last).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
    return result;
}

function vnConfirmModal(opts) {
    opts = opts || {};
    const id = 'vnConfirmModal';
    document.getElementById(id)?.remove();

    const danger = opts.danger !== false;
    const o = document.createElement('div');
    o.className = 'modal-overlay';
    o.style.display = 'flex';
    o.id = id;
    o.style.zIndex = '10003';
    o.onclick = e => { if (e.target === o) o.remove(); };
    o.innerHTML = `<div class="modal-box">
        ${renderModalBar(opts.title || '', [modalCloseAction(`document.getElementById('${id}').remove()`)])}
        <div class="modal-icon${danger ? ' danger' : ''}" style="margin-top:20px;"><span class="msi" style="font-size:22px;">${esc(opts.icon || 'delete')}</span></div>
        <div class="modal-msg">${opts.message || ''}</div>
        <div class="modal-btns">
            <button class="vrcn-button-round${danger ? ' vrcn-btn-danger' : ''}" id="${id}Ok">${esc(opts.confirmLabel || t('common.remove', 'Remove'))}</button>
        </div></div>`;
    document.body.appendChild(o);

    document.getElementById(id + 'Ok').onclick = () => {
        o.remove();
        if (typeof opts.onConfirm === 'function') opts.onConfirm();
    };
}

function vnPromptModal(opts) {
    opts = opts || {};
    const id = 'vnPromptModal';
    document.getElementById(id)?.remove();

    const o = document.createElement('div');
    o.className = 'modal-overlay';
    o.style.display = 'flex';
    o.id = id;
    o.style.zIndex = '10003';
    o.onclick = e => { if (e.target === o) o.remove(); };
    o.innerHTML = `<div class="modal-box">
        ${renderModalBar(opts.title || '', [modalCloseAction(`document.getElementById('${id}').remove()`)])}
        <div class="modal-msg" style="margin:20px 0 8px;word-break:normal;">${opts.message || ''}</div>
        <input id="${id}Input" class="vrcn-edit-field" style="width:100%;box-sizing:border-box;margin-bottom:20px;"
               maxlength="${parseInt(opts.maxLength, 10) || 128}"
               placeholder="${esc(opts.placeholder || '')}" value="${esc(opts.value || '')}">
        <div class="modal-btns">
            <button class="vrcn-button-round" id="${id}Ok">${esc(opts.confirmLabel || t('common.save', 'Save'))}</button>
        </div></div>`;
    document.body.appendChild(o);

    const input = document.getElementById(id + 'Input');
    const submit = () => {
        const v = (input.value || '').trim();
        if (!v) return;
        o.remove();
        if (typeof opts.onConfirm === 'function') opts.onConfirm(v);
    };
    document.getElementById(id + 'Ok').onclick = submit;
    input.onkeydown = e => { if (e.key === 'Enter') { e.preventDefault(); submit(); } };
    setTimeout(() => { input.focus(); input.select(); }, 50);
}

function vrcnConfirmDelete(opts) {
    const id = opts.id || 'vrcnConfirmDeleteModal';
    document.getElementById(id)?.remove();
    const o = document.createElement('div');
    o.className = 'modal-overlay';
    o.style.display = 'flex';
    o.id = id;
    o.style.zIndex = '10003';
    o.onclick = e => { if (e.target === o) o.remove(); };
    o.innerHTML = `<div class="modal-box">
        ${renderModalBar(opts.title, [modalCloseAction(`document.getElementById('${id}').remove()`)])}
        <div class="modal-icon danger" style="margin-top:20px;"><span class="msi" style="font-size:22px;">${_esc(opts.icon || 'delete')}</span></div>
        <div class="modal-msg">${_esc(opts.message)}</div>
        ${opts.listHtml ? `<div class="gr-bulk-list">${opts.listHtml}</div>` : ''}
        <div class="modal-btns">
            <button class="vrcn-button-round" id="${id}Cancel">${_esc(typeof t === 'function' ? t('common.cancel', 'Cancel') : 'Cancel')}</button>
            <button class="vrcn-button-round vrcn-btn-danger" id="${id}Ok">${_esc(opts.confirmLabel || 'Delete')}</button>
        </div></div>`;
    document.body.appendChild(o);
    document.getElementById(id + 'Cancel').onclick = () => o.remove();
    document.getElementById(id + 'Ok').onclick = () => { o.remove(); opts.onConfirm && opts.onConfirm(); };
}
