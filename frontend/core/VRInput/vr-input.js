/* === VR Input Mode === */

let vrInputMode = 0;
let vriLoaded   = false;

const VRI_LEGACY = [
    { id: 2,  hl: 'grip',    key: 'vro.keybind.button.grip',       en: 'Grip',       sketch: true },
    { id: 33, hl: 'trigger', key: 'vro.keybind.button.trigger',    en: 'Trigger',    sketch: true },
    { id: 32, hl: 'stick',   key: 'vro.keybind.button.thumbstick', en: 'Thumbstick', sketch: true, short: 'vro.keybind.button.thumbstick_short', shortEn: 'Stick' },
    { id: 7,  hl: 'a',       key: 'vro.keybind.button.ax',         en: 'A / X',      sketch: true },
    { id: 1,  hl: 'b',       key: 'vro.keybind.button.by',         en: 'B / Y',      sketch: true },
];

const VRI_INDEX = [
    { id: 1,  hl: 'a',       key: 'vri.index.a',             en: 'A',               sketch: true },
    { id: 2,  hl: 'b',       key: 'vri.index.b',             en: 'B',               sketch: true },
    { id: 3,  hl: 'trigger', key: 'vri.index.trigger',       en: 'Trigger',         sketch: true },
    { id: 4,  hl: 'grip',    key: 'vri.index.grip',          en: 'Grip',            sketch: true },
    { id: 5,  hl: 'pad',     key: 'vri.index.pad',           en: 'Trackpad',        sketch: true },
    { id: 7,  hl: 'stick',   key: 'vri.index.stick',         en: 'Thumbstick',      sketch: true },
    { id: 6,  hl: 'pad',     key: 'vri.index.pad_touch',     en: 'Trackpad Touch' },
    { id: 15, hl: 'pad',     key: 'vri.index.pad_force',     en: 'Trackpad Force' },
    { id: 8,  hl: 'stick',   key: 'vri.index.stick_touch',   en: 'Thumbstick Touch' },
    { id: 10, hl: 'a',       key: 'vri.index.a_touch',       en: 'A Touch' },
    { id: 11, hl: 'b',       key: 'vri.index.b_touch',       en: 'B Touch' },
    { id: 12, hl: 'trigger', key: 'vri.index.trigger_touch', en: 'Trigger Touch' },
    { id: 14, hl: 'grip',    key: 'vri.index.grip_force',    en: 'Grip Force' },
];

function vriDefs(mode) {
    return (mode ?? vrInputMode) === 1 ? VRI_INDEX : VRI_LEGACY;
}

function vriDef(id, mode) {
    return vriDefs(mode).find(x => x.id === id) || null;
}

function vriName(id, mode) {
    const d = vriDef(id, mode);
    return d ? t(d.key, d.en) : `Button${id}`;
}

function vriShortName(id, mode) {
    const d = vriDef(id, mode);
    if (!d) return `Button${id}`;
    return d.short ? t(d.short, d.shortEn) : t(d.key, d.en);
}

function vriNames(ids, mode) {
    return (ids || []).map(id => vriName(id, mode));
}

function vriBaseImage(mode) {
    return (mode ?? vrInputMode) === 1
        ? 'assets/VR/Index/Controller.png'
        : 'assets/VR/Quest/Controller.png';
}

function vriSketchHtml(idAttr, side, clickExpr) {
    return vriDefs().filter(d => d.sketch).map(d =>
        `<div class="vro-btn" ${idAttr}="${d.id}" data-hl="${d.hl}" data-side="${side}" title="${t(d.key, d.en)}"${clickExpr ? ` onclick="${clickExpr}"` : ''}></div>`
    ).join('');
}

function vriListHtml(idAttr, clickExpr) {
    return vriDefs().map(d =>
        `<div class="vro-btn" ${idAttr}="${d.id}" title="${t(d.key, d.en)}"${clickExpr ? ` onclick="${clickExpr}"` : ''}>${vriShortName(d.id)}</div>`
    ).join('');
}

function vriOptionsHtml(includeNone) {
    const none = includeNone
        ? `<option value="0">${t('frameshot.keybind.none', 'None')}</option>`
        : '';
    return none + vriDefs().map(d => `<option value="${d.id}">${t(d.key, d.en)}</option>`).join('');
}

function vriZoneDefs(hl) {
    return hl ? vriDefs().filter(d => d.hl === hl) : [];
}

function vriZoneIds(hl) {
    return vriZoneDefs(hl).map(d => d.id);
}

function vriDropZoneSiblings(ids, id) {
    const d = vriDef(id);
    if (!d) return ids;
    const zone = vriZoneIds(d.hl).filter(x => x !== id);
    return ids.filter(x => !zone.includes(x));
}

function vriBtnHit(el, ids, idAttr) {
    const list = ids || [];
    if (!el.dataset.hl) {
        const own = parseInt(el.getAttribute(idAttr), 10);
        return list.includes(own) ? own : null;
    }
    const hit = vriZoneIds(el.dataset.hl).find(id => list.includes(id));
    return hit === undefined ? null : hit;
}

function vriMarkBtn(el, ids, idAttr, sideOk) {
    const hit = vriBtnHit(el, ids, idAttr);
    const on  = hit !== null && sideOk !== false;
    const primary = el.dataset.hl ? vriZoneIds(el.dataset.hl)[0] : hit;
    el.classList.toggle('active', on);
    el.classList.toggle('alt', on && hit !== primary);
    return hit;
}

let _vriZoneMenu = null;

function vriCloseZoneMenu() {
    if (!_vriZoneMenu) return;
    _vriZoneMenu.remove();
    _vriZoneMenu = null;
    document.removeEventListener('mousedown', _vriZoneOutside, true);
}

function _vriZoneOutside(e) {
    if (_vriZoneMenu && !_vriZoneMenu.contains(e.target)) vriCloseZoneMenu();
}

function vriZoneClick(el, idAttr, currentIds, onPick) {
    const defs = vriZoneDefs(el.dataset.hl);
    if (defs.length <= 1) {
        onPick(parseInt(el.getAttribute(idAttr), 10));
        return;
    }

    vriCloseZoneMenu();
    const sel = vriBtnHit(el, currentIds, idAttr);

    const menu = document.createElement('div');
    menu.className = 'vro-zone-menu';
    menu.innerHTML = defs.map((d, i) =>
        `<div class="vro-zone-item${i === 0 ? '' : ' misc'}${d.id === sel ? ' sel' : ''}" data-vri-pick="${d.id}">`
        + `<span class="vro-zone-dot"></span><span>${t(d.key, d.en)}</span></div>`
    ).join('');

    document.body.appendChild(menu);
    const r = el.getBoundingClientRect();
    const mr = menu.getBoundingClientRect();
    let left = r.left + r.width / 2 - mr.width / 2;
    let top  = r.bottom + 6;
    if (left < 8) left = 8;
    if (left + mr.width > window.innerWidth - 8) left = window.innerWidth - 8 - mr.width;
    if (top + mr.height > window.innerHeight - 8) top = Math.max(8, r.top - 6 - mr.height);
    menu.style.left = left + 'px';
    menu.style.top  = top + 'px';

    menu.querySelectorAll('[data-vri-pick]').forEach(item => {
        item.addEventListener('click', ev => {
            ev.stopPropagation();
            const id = parseInt(item.dataset.vriPick, 10);
            vriCloseZoneMenu();
            onPick(id);
        });
    });

    _vriZoneMenu = menu;
    setTimeout(() => document.addEventListener('mousedown', _vriZoneOutside, true), 0);
}

function _vriIdAttr(el) {
    if (!el) return 'data-btn-id';
    for (const a of ['data-sf-btn-id', 'data-st-btn-id', 'data-fs-btn-id', 'data-scale-btn-id']) {
        if (el.hasAttribute(a)) return a;
    }
    return 'data-btn-id';
}

function vriApplyMode() {
    const index = vrInputMode === 1;
    vriCloseZoneMenu();

    document.querySelectorAll('.vro-controller-visual').forEach(v => {
        v.classList.toggle('index', index);

        v.querySelectorAll('.vro-controller').forEach(c => {
            const side  = c.classList.contains('vro-controller-left') ? 'left' : 'right';
            const proto = c.querySelector('.vro-btn');
            const attr  = _vriIdAttr(proto);
            const click = proto?.getAttribute('onclick') || '';
            c.querySelectorAll('.vro-btn').forEach(el => el.remove());
            c.insertAdjacentHTML('beforeend', vriSketchHtml(attr, side, click));
        });

        const legacy = v.querySelector('.vro-ctrl-legacy');
        if (legacy) {
            const proto = legacy.querySelector('.vro-btn');
            const attr  = _vriIdAttr(proto);
            const click = proto?.getAttribute('onclick') || '';
            legacy.innerHTML = `<div class="vro-ctrl-list">${vriListHtml(attr, click)}</div>`;
        }
    });

    document.querySelectorAll('select[data-vri-buttons]').forEach(sel => {
        const cur = parseInt(sel.value, 10) || 0;
        sel.innerHTML = vriOptionsHtml(sel.dataset.vriButtons !== 'required');
        sel.value = sel.querySelector(`option[value="${cur}"]`) ? String(cur) : (sel.options[0]?.value ?? '0');
        if (sel._vnRefresh) sel._vnRefresh();
    });

    if (typeof vroApplyInputMode === 'function') vroApplyInputMode();
    if (typeof sfApplyInputMode  === 'function') sfApplyInputMode();
    if (typeof stApplyInputMode  === 'function') stApplyInputMode();
    if (typeof fsApplyInputMode  === 'function') fsApplyInputMode();
}

function vriSetMode(mode) {
    const m = parseInt(mode, 10) === 1 ? 1 : 0;
    if (m === vrInputMode) return;
    vrInputMode = m;
    if (typeof vroSwapKeybindSets === 'function') vroSwapKeybindSets();
    if (typeof sfSwapButtonSets   === 'function') sfSwapButtonSets();
    if (typeof stSwapButtonSets   === 'function') stSwapButtonSets();
    if (typeof fsSwapButtonSets   === 'function') fsSwapButtonSets();
    document.querySelectorAll('select.vro-input-mode').forEach(sel => {
        sel.value = String(m);
        if (sel._vnRefresh) sel._vnRefresh();
    });
    vriApplyMode();
    if (typeof saveSettings === 'function') saveSettings();
}

function vriRefreshModeSelects() {
    document.querySelectorAll('select.vro-input-mode').forEach(sel => {
        sel.innerHTML = `<option value="0">${t('vri.mode.legacy', 'Legacy (Default)')}</option>`
                      + `<option value="1">${t('vri.mode.steamvr', 'SteamVR (Index)')}</option>`;
        sel.value = String(vrInputMode);
        if (sel._vnRefresh) sel._vnRefresh();
    });
}

function vriInit(mode) {
    vrInputMode = parseInt(mode, 10) === 1 ? 1 : 0;
    vriRefreshModeSelects();
    vriApplyMode();
}
