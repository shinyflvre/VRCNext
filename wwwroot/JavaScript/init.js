/* === Init === */
renderWebhookCards([{}, {}, {}, {}]);
renderThemeChips();
renderDashboard();
tryLoadLogo();
tryInitNotifySound();
renderChatboxLines();

/* === Borderless window: drag & double-click maximize === */
const winDrag = document.getElementById('winDrag');
if (winDrag) {
    winDrag.addEventListener('mousedown', e => {
        // Only drag from the topbar background, not buttons/badges
        if (e.target.closest('.win-controls, .btn-notif, .mini-badge, button')) return;
        if (e.button === 0) sendToCS({ action: 'windowDragStart' });
    });
    winDrag.addEventListener('dblclick', e => {
        if (e.target.closest('.win-controls, .btn-notif, .mini-badge, button')) return;
        sendToCS({ action: 'windowMaximize' });
    });
}

setInterval(updateClock, 1000);
updateClock();

// Silently pre-load timeline data so friend-detail previews work before Tab 12 is visited
sendToCS({ action: 'getTimeline' });
