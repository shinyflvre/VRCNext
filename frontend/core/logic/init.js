/* === Init === */
document.addEventListener('contextmenu', e => e.preventDefault());

/* === Grid Size Toggle === */
function setGridSize(tab, size) {
    localStorage.setItem('vrcn_gridSize_' + tab, size);
    const compact = size === 'compact';
    const gridIds = {
        worlds:  ['favWorldsGrid', 'worldDiscoverGrid', 'worldMineGrid', 'worldRecentGrid', 'searchWorldsResults'],
        groups:  ['myGroupsGrid', 'searchGroupsResults', 'groupInstancesGrid'],
        avatars: ['avatarGrid', 'favAvatarsGrid', 'avatarRecentGrid', 'roseDbGrid', 'avatarSearchGrid'],
    };
    (gridIds[tab] || []).forEach(id => {
        const el = document.getElementById(id);
        if (el) el.classList.toggle('grid-compact', compact);
    });
    const btnPrefix = { worlds: 'world', groups: 'group', avatars: 'avatar' };
    const pfx = btnPrefix[tab] || tab;
    const largeBtn = document.getElementById(pfx + 'GridLarge');
    const smallBtn = document.getElementById(pfx + 'GridSmall');
    const isList = typeof lvViewMode === 'function' && lvViewMode(tab) === 'list';
    if (largeBtn) largeBtn.classList.toggle('active', !compact && !isList);
    if (smallBtn) smallBtn.classList.toggle('active', compact && !isList);
}

// Restore nav group collapsed states (default: collapsed)
(function() {
    document.querySelectorAll('.nav-group[id]').forEach(group => {
        const saved = localStorage.getItem('vrcnext_navgroup_' + group.id);
        const isCollapsed = saved === null ? true : saved === '1';
        group.classList.toggle('collapsed', isCollapsed);
    });
}());

initAllVnSelects();
(function() {
    ['worlds', 'groups', 'avatars'].forEach(tab => {
        const saved = localStorage.getItem('vrcn_gridSize_' + tab) || 'default';
        setGridSize(tab, saved);
    });
    if (typeof _worldsSyncViewBtns  === 'function') _worldsSyncViewBtns();
    if (typeof _groupsSyncViewBtns  === 'function') _groupsSyncViewBtns();
    if (typeof _avatarsSyncViewBtns === 'function') _avatarsSyncViewBtns();
}());
(function() {
    const el = document.getElementById('avatarSearchDbDrop');
    if (el) { const w = el._vnSelect ? el.parentNode : el; w.style.display = 'none'; }
}());
renderWebhookCards([{}, {}, {}, {}]);
renderLanguageChips();
renderThemeChips();
renderFontGrid();
applyFontSizeOffset(currentFontSizeOffset);
renderDashboard();
tryLoadLogo();
tryInitNotifySound();
renderChatboxLines();

/* === Borderless window: drag & double-click maximize === */
const winDrag = document.getElementById('winDrag');
if (winDrag) {
    winDrag.addEventListener('mousedown', e => {
        // Only drag from the topbar background, not buttons/badges/search
        if (e.target.closest('.win-controls, .btn-notif, .mini-badge, .ss-wrap, button, input')) return;
        // Skip SC_MOVE on the 2nd click of a double-click so dblclick event can fire
        if (e.button === 0 && e.detail === 1) sendToCS({ action: 'windowDragStart' });
    });
    winDrag.addEventListener('dblclick', e => {
        if (e.target.closest('.win-controls, .btn-notif, .mini-badge, .ss-wrap, button, input')) return;
        sendToCS({ action: 'windowMaximize' });
    });
}

// Also allow dragging from the left sidebar logo and right sidebar header
['.logo', '.rsidebar-header'].forEach(sel => {
    const el = document.querySelector(sel);
    if (!el) return;
    el.addEventListener('mousedown', e => {
        if (e.target.closest('button')) return;
        if (e.button === 0 && e.detail === 1) sendToCS({ action: 'windowDragStart' });
    });
    el.addEventListener('dblclick', e => {
        if (e.target.closest('button')) return;
        sendToCS({ action: 'windowMaximize' });
    });
});

/* === Borderless window: edge resize === */
(function () {
    const B = 6;
    const cursorMap = { n: 'n-resize', s: 's-resize', e: 'e-resize', w: 'w-resize', ne: 'ne-resize', nw: 'nw-resize', se: 'se-resize', sw: 'sw-resize' };
    function getDir(x, y) {
        const w = window.innerWidth, h = window.innerHeight;
        const l = x < B, r = x > w - B, t = y < B, b = y > h - B;
        if (t && l) return 'nw'; if (t && r) return 'ne';
        if (b && l) return 'sw'; if (b && r) return 'se';
        if (l) return 'w'; if (r) return 'e';
        if (t) return 'n'; if (b) return 's';
        return null;
    }
    // Transparent overlays for top resize zones so the cursor shows correctly
    // even over taskbar elements that have cursor:default in CSS.
    // They sit above the taskbar (z-index > 100) but still bubble mousedown to document.
    [
        { style: `top:0;left:${B}px;right:${B}px;height:${B}px;cursor:n-resize` },
        { style: `top:0;left:0;width:${B}px;height:${B}px;cursor:nw-resize` },
        { style: `top:0;right:0;width:${B}px;height:${B}px;cursor:ne-resize` },
    ].forEach(({ style }) => {
        const el = document.createElement('div');
        el.style.cssText = `position:fixed;${style};z-index:99999;background:transparent;`;
        document.body.appendChild(el);
    });

    document.addEventListener('mousemove', e => {
        if (window._isLinuxUi) return;
        const dir = getDir(e.clientX, e.clientY);
        document.documentElement.style.cursor = dir ? cursorMap[dir] : '';
    });
    document.addEventListener('mousedown', e => {
        if (window._isLinuxUi) return;
        if (e.button !== 0) return;
        const dir = getDir(e.clientX, e.clientY);
        if (dir) { e.preventDefault(); sendToCS({ action: 'windowResizeStart', direction: dir }); }
    });
})();

setInterval(updateClock, 1000);
updateClock();

// Silently pre-load timeline data so friend-detail previews work before Tab 12 is visited
sendToCS({ action: 'getTimeline' });

/* === Topbar scroll fade === */
(function () {
    const content = document.querySelector('.content');
    if (!winDrag || !content) return;
    const FADE_PX = 140;
    const tab0 = document.getElementById('tab0');
    const taskbar = document.getElementById('taskbar');
    let _tbRgb = null;
    let _tbLast = '';
    let _tbRaf = 0;
    function applyTopbarBg() {
        if (!_tbRgb) {
            const hex = getComputedStyle(document.documentElement).getPropertyValue('--bg-base').trim();
            _tbRgb = [parseInt(hex.slice(1, 3), 16), parseInt(hex.slice(3, 5), 16), parseInt(hex.slice(5, 7), 16)];
        }
        const [r, g, b] = _tbRgb;
        let bg;
        if (!tab0 || !tab0.classList.contains('active')) {
            bg = `rgb(${r},${g},${b})`;
        } else {
            const t = Math.min(content.scrollTop / FADE_PX, 1);
            bg = `rgba(${r},${g},${b},${t.toFixed(2)})`;
        }
        if (bg === _tbLast) return;
        _tbLast = bg;
        winDrag.style.background = bg;
        if (taskbar) taskbar.style.background = bg;
    }
    function onTopbarScroll() {
        if (_tbRaf) return;
        _tbRaf = requestAnimationFrame(() => { _tbRaf = 0; applyTopbarBg(); });
    }
    applyTopbarBg();
    content.addEventListener('scroll', onTopbarScroll, { passive: true });
    document.documentElement.addEventListener('themechange', () => { _tbRgb = null; _tbLast = ''; applyTopbarBg(); });
    document.documentElement.addEventListener('tabchange', applyTopbarBg);
}());

/* === Topbar compact badges === */
(function () {
    const topbar = document.getElementById('winDrag');
    if (!topbar) return;
    // Use topbar.offsetWidth (stable — unaffected by compact toggle) to avoid
    // the feedback loop where compact shrinks badges → gap widens → compact
    // removed → badges grow → gap shrinks → compact added → infinite oscillation.
    function checkCompact() {
        const w = topbar.offsetWidth;
        const isCompact = topbar.classList.contains('compact');
        if (!isCompact && w < 620) topbar.classList.add('compact');
        else if (isCompact && w > 700) topbar.classList.remove('compact');
    }
    const _compactObserver = new ResizeObserver(checkCompact);
    _compactObserver.observe(topbar);
    checkCompact();
}());

/* === Library top-fade === */
(function () {
    const tab7 = document.getElementById('tab7');
    function applyLibFade() {
        const fade    = document.getElementById('libTopFade');
        const libWrap = document.querySelector('.lib-wrap');
        if (!fade || !tab7) return;
        const visible = tab7.classList.contains('active') && (libWrap?.scrollTop ?? 0) > 60;
        fade.classList.toggle('visible', visible);
    }
    // .lib-wrap is the scrolling container after the flex-column layout change
    document.querySelector('.lib-wrap')?.addEventListener('scroll', applyLibFade, { passive: true });
    document.documentElement.addEventListener('tabchange', applyLibFade);
}());
