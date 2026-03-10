/* === Init === */
document.addEventListener('contextmenu', e => e.preventDefault());

// Restore nav group collapsed states (default: collapsed)
(function() {
    document.querySelectorAll('.nav-group[id]').forEach(group => {
        const saved = localStorage.getItem('vrcnext_navgroup_' + group.id);
        const isCollapsed = saved === null ? true : saved === '1';
        group.classList.toggle('collapsed', isCollapsed);
    });
}());

initAllVnSelects();
renderWebhookCards([{}, {}, {}, {}]);
renderThemeChips();
renderDashboard();
fetchDiscovery();
tryLoadLogo();
tryInitNotifySound();
renderChatboxLines();

/* === Borderless window: drag & double-click maximize === */
const winDrag = document.getElementById('winDrag');
if (winDrag) {
    winDrag.addEventListener('mousedown', e => {
        // Only drag from the topbar background, not buttons/badges
        if (e.target.closest('.win-controls, .btn-notif, .mini-badge, button')) return;
        // Skip SC_MOVE on the 2nd click of a double-click so dblclick event can fire
        if (e.button === 0 && e.detail === 1) sendToCS({ action: 'windowDragStart' });
    });
    winDrag.addEventListener('dblclick', e => {
        if (e.target.closest('.win-controls, .btn-notif, .mini-badge, button')) return;
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
    document.addEventListener('mousemove', e => {
        const dir = getDir(e.clientX, e.clientY);
        document.documentElement.style.cursor = dir ? cursorMap[dir] : '';
    });
    document.addEventListener('mousedown', e => {
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
    function applyTopbarBg() {
        const hex = getComputedStyle(document.documentElement).getPropertyValue('--bg-base').trim();
        const r = parseInt(hex.slice(1, 3), 16);
        const g = parseInt(hex.slice(3, 5), 16);
        const b = parseInt(hex.slice(5, 7), 16);
        if (!tab0 || !tab0.classList.contains('active')) {
            winDrag.style.background = `rgb(${r},${g},${b})`;
            return;
        }
        const t = Math.min(content.scrollTop / FADE_PX, 1);
        const a1 = (0.78 + 0.22 * t).toFixed(2);
        const a2 = t.toFixed(2);
        winDrag.style.background = `linear-gradient(to bottom, rgba(${r},${g},${b},${a1}), rgba(${r},${g},${b},${a2}))`;
    }
    applyTopbarBg();
    content.addEventListener('scroll', applyTopbarBg, { passive: true });
    document.documentElement.addEventListener('themechange', applyTopbarBg);
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
    const content = document.querySelector('.content');
    if (!content) return;
    const tab7 = document.getElementById('tab7');
    function applyLibFade() {
        const fade = document.getElementById('libTopFade');
        if (!fade || !tab7) return;
        const visible = tab7.classList.contains('active') && content.scrollTop > 160;
        fade.classList.toggle('visible', visible);
    }
    content.addEventListener('scroll', applyLibFade, { passive: true });
    document.documentElement.addEventListener('tabchange', applyLibFade);
}());
