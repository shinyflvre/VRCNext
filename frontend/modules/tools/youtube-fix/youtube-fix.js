// YouTube Fix
// Fixes YouTube video errors in VRChat by unlocking H.264 codec on Linux or running VRCVideoCacher on Windows

let vcState = 'stopped'; // stopped, running, installing, error
let vcVersion = '';
let vcAutoStartVR = false;
let vcAutoStartDesktop = false;

// Detect platform from URL parameter
let isLinux = new URLSearchParams(location.search).get('vrcnPlatform') === 'linux';

// Initialize on page load
function initYoutubeFix() {
    requestVcState();
    loadYoutubeFixSettings();
    updateUIForPlatform();
}

// Update UI elements based on platform
function updateUIForPlatform() {
    if (isLinux) {
        // Hide Windows-specific UI on Linux
        const installCard = document.getElementById('vcInstallCard');
        if (installCard) {
            installCard.style.display = 'none';
        }
        
        // Show Linux-specific UI
        const linuxCard = document.getElementById('vcLinuxCard');
        if (linuxCard) {
            linuxCard.style.display = 'block';
        }
    } else {
        // Show Windows-specific UI on Windows
        const installCard = document.getElementById('vcInstallCard');
        if (installCard) {
            installCard.style.display = 'block';
        }
        
        // Hide Linux-specific UI
        const linuxCard = document.getElementById('vcLinuxCard');
        if (linuxCard) {
            linuxCard.style.display = 'none';
        }
    }
}

// Request current VC state from backend
function requestVcState() {
    sendToCS({ action: 'vcCheck' });
}

// Handle VC state update from backend
function onVcStateUpdate(state) {
    vcState = state.state || 'stopped';
    vcVersion = state.version || '';
    
    updateVcUI();
}

// Handle VC data update from backend
function onVcData(data) {
    if (data.autoStartVR !== undefined) {
        vcAutoStartVR = data.autoStartVR;
        const el = document.getElementById('setYtAutoStartVR');
        if (el) el.checked = vcAutoStartVR;
    }
    if (data.autoStartDesktop !== undefined) {
        vcAutoStartDesktop = data.autoStartDesktop;
        const el = document.getElementById('setYtAutoStartDesktop');
        if (el) el.checked = vcAutoStartDesktop;
    }
}

// Load YouTube Fix settings from localStorage
function loadYoutubeFixSettings() {
    try {
        const saved = localStorage.getItem('youtubeFixSettings');
        if (saved) {
            const settings = JSON.parse(saved);
            vcAutoStartVR = settings.autoStartVR || false;
            vcAutoStartDesktop = settings.autoStartDesktop || false;
            
            const vrEl = document.getElementById('setYtAutoStartVR');
            if (vrEl) vrEl.checked = vcAutoStartVR;
            
            const deskEl = document.getElementById('setYtAutoStartDesktop');
            if (deskEl) deskEl.checked = vcAutoStartDesktop;
        }
    } catch (e) {
        console.error('Failed to load YouTube Fix settings:', e);
    }
}

// Toggle VC start/stop
function toggleVc() {
    if (vcState === 'running') {
        stopVc();
    } else if (vcState === 'stopped' || vcState === 'error') {
        startVc();
    }
}

// Start VC process
function startVc() {
    sendToCS({ action: 'startVc' });
    updateVcUI();
}

// Stop VCs process
function stopVc() {
    sendToCS({ action: 'stopVc' });
    updateVcUI();
}

// Install/Update VRCVideoCacher
function vcInstall() {
    const btn = document.getElementById('btnVcInstall');
    if (btn) btn.disabled = true;
    
    sendToCS({ action: 'vcInstall' });
    
    vcState = 'installing';
    updateVcUI();
}

// Handle VC install progress
function onVcProgress(progress) {
    const progressWrap = document.getElementById('vcProgressWrap');
    const progressBar = document.getElementById('vcProgressBar');
    const progressLabel = document.getElementById('vcProgressLabel');
    
    if (!progressWrap || !progressBar) return;
    
    if (progress.total > 0) {
        progressWrap.style.display = 'block';
        const percent = (progress.downloaded / progress.total) * 100;
        progressBar.style.width = percent + '%';
        
        if (progressLabel) {
            if (progress.downloaded === progress.total) {
                progressLabel.textContent = t('youtube_fix.progress.extracting', 'Extracting...');
            } else {
                const mb = (progress.downloaded / 1024 / 1024).toFixed(1);
                const totalMb = (progress.total / 1024 / 1024).toFixed(1);
                progressLabel.textContent = `${t('youtube_fix.progress.downloading', 'Downloading...')} (${mb}MB / ${totalMb}MB)`;
            }
        }
    } else {
        progressWrap.style.display = 'none';
    }
}

// Handle VC install completion
function onVcInstallComplete(success, version) {
    const btn = document.getElementById('btnVcInstall');
    if (btn) btn.disabled = false;
    
    if (success) {
        vcVersion = version || '';
        vcState = 'stopped';
    } else {
        vcState = 'error';
    }
    
    const progressWrap = document.getElementById('vcProgressWrap');
    if (progressWrap) progressWrap.style.display = 'none';
    
    updateVcUI();
    requestVcState();
}

// Update UI based on state
function updateVcUI() {
    const dot = document.getElementById('vcDot');
    const statusText = document.getElementById('vcStatusText');
    const btn = document.getElementById('btnVc');
    const installBtn = document.getElementById('btnVcInstall');
    const versionLabel = document.getElementById('vcVersionLabel');
    
    if (!dot || !statusText || !btn) return;
    
    // Update status dot and text
    dot.className = 'sf-dot ' + (vcState === 'running' ? 'online' : vcState === 'installing' ? 'ask' : 'offline');
    
    let statusKey = 'youtube_fix.status.not_running';
    if (vcState === 'running') {
        statusKey = 'youtube_fix.status.running';
    } else if (vcState === 'installing') {
        statusKey = 'youtube_fix.status.installing';
    } else if (vcState === 'error') {
        statusKey = 'youtube_fix.status.error';
    }
    statusText.setAttribute('data-i18n', statusKey);
    statusText.textContent = t(statusKey);
    
    // Update button state
    btn.disabled = vcState === 'installing';
    if (vcState === 'running') {
        btn.innerHTML = '<span class="msi" style="font-size:16px;">stop</span> <span data-i18n="common.stop">Stop</span>';
    } else {
        btn.innerHTML = '<span class="msi" style="font-size:16px;">play_arrow</span> <span data-i18n="common.start">Start</span>';
    }
    
    // Update install button state
    if (installBtn) {
        installBtn.disabled = vcState === 'running' || vcState === 'installing';
    }
    
    // Update version label
    if (versionLabel) {
        if (vcVersion) {
            versionLabel.textContent = `${t('youtube_fix.version', 'Version')}: ${vcVersion}`;
        } else {
            versionLabel.textContent = '';
        }
    }
}

// Auto-save settings
function autoSave() {
    const vrEl = document.getElementById('setYtAutoStartVR');
    const deskEl = document.getElementById('setYtAutoStartDesktop');
    
    if (vrEl) vcAutoStartVR = vrEl.checked;
    if (deskEl) vcAutoStartDesktop = deskEl.checked;
    
    const settings = {
        autoStartVR: vcAutoStartVR,
        autoStartDesktop: vcAutoStartDesktop
    };
    
    try {
        localStorage.setItem('youtubeFixSettings', JSON.stringify(settings));
    } catch (e) {
        console.error('Failed to save YouTube Fix settings:', e);
    }
    
    sendToCS({
        action: 'setYoutubeFixSettings',
        autoStartVR: vcAutoStartVR,
        autoStartDesktop: vcAutoStartDesktop
    });
}

// Initialize when module loads
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initYoutubeFix);
} else {
    initYoutubeFix();
}
