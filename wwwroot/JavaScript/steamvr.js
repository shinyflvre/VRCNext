/* === Space Flight === */
function sfConnect() {
    if (sfConnected) {
        sendToCS({ action: 'sfDisconnect' });
    } else {
        sendToCS({ action: 'sfConnect' });
        sfSendConfig();
    }
}
function sfReset() { sendToCS({ action: 'sfReset' }); }
function sfSendConfig() {
    sendToCS({
        action: 'sfConfig',
        dragMultiplier: parseFloat(document.getElementById('sfMultiplier').value) || 1,
        lockX: document.getElementById('sfLockX').checked,
        lockY: document.getElementById('sfLockY').checked,
        lockZ: document.getElementById('sfLockZ').checked,
        leftHand: document.getElementById('sfLeftHand').checked,
        rightHand: document.getElementById('sfRightHand').checked,
        useGrip: document.getElementById('sfUseGrip').checked
    });
}
let _sfAutoTimer = null;
function sfAutoSave() {
    sfSendConfig();
    clearTimeout(_sfAutoTimer);
    _sfAutoTimer = setTimeout(() => saveSettings(), 600);
}
function handleSfUpdate(d) {
    sfConnected = d.connected;
    const dot = document.getElementById('sfDot');
    const txt = document.getElementById('sfStatusText');
    const btn = document.getElementById('sfConnBtn');
    const badge = document.getElementById('badgeSteamVR');
    if (d.connected) {
        dot.classList.remove('offline'); dot.classList.add('online');
        txt.textContent = d.dragging ? 'Dragging...' : 'Connected to SteamVR';
        txt.style.color = d.dragging ? 'var(--warn)' : 'var(--ok)';
        btn.innerHTML = '<span class="msi" style="font-size:16px;">link_off</span> Disconnect';
        if (badge) { badge.classList.remove('offline'); badge.classList.add('online'); }
    } else {
        dot.classList.remove('online'); dot.classList.add('offline');
        txt.textContent = d.error || 'Not connected';
        txt.style.color = d.error ? 'var(--err)' : 'var(--tx3)';
        btn.innerHTML = '<span class="msi" style="font-size:16px;">link</span> Connect';
        if (badge) { badge.classList.remove('online'); badge.classList.add('offline'); }
    }
    document.getElementById('sfOffX').textContent = (d.offsetX ?? 0).toFixed(3);
    document.getElementById('sfOffY').textContent = (d.offsetY ?? 0).toFixed(3);
    document.getElementById('sfOffZ').textContent = (d.offsetZ ?? 0).toFixed(3);
    const lc = document.getElementById('sfCtrlL');
    const rc = document.getElementById('sfCtrlR');
    if (lc) lc.classList.toggle('detected', !!d.leftController);
    if (rc) rc.classList.toggle('detected', !!d.rightController);
}
