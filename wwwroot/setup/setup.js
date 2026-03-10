/* VRCNext Setup Wizard */

var currentPage = 0;
var totalPages = 6;
var isLoggedIn = false;
var loggedInName = '';
var vrc2faType = 'totp';

// Communication helper
function sendToCS(obj) {
    window.external.sendMessage(JSON.stringify(obj));
}

// Page navigation
function showPage(idx) {
    currentPage = idx;
    var pages = document.querySelectorAll('.setup-page');
    for (var i = 0; i < pages.length; i++) pages[i].classList.remove('active');
    var page = document.querySelector('[data-page="' + idx + '"]');
    if (page) {
        void page.offsetWidth; // force reflow for animation
        page.classList.add('active');
    }
    document.getElementById('progressFill').style.width = ((idx + 1) / totalPages * 100) + '%';
    document.getElementById('stepCounter').textContent = 'STEP ' + (idx + 1) + ' OF ' + totalPages;

    var nextBtn = document.getElementById('btnNext');
    var skipBtn = document.getElementById('btnSkip');
    if (idx === totalPages - 1) {
        nextBtn.innerHTML = '<span class="msi" style="font-size:16px;vertical-align:middle;">check_circle</span> Finish Setup';
        nextBtn.classList.add('finish');
        skipBtn.style.display = 'none';
    } else {
        nextBtn.innerHTML = 'Next <span class="msi" style="font-size:16px;vertical-align:middle;">arrow_forward</span>';
        nextBtn.classList.remove('finish');
        skipBtn.style.display = '';
    }
    if (idx === 1 && isLoggedIn) renderLoginSuccess();
}

function nextPage() {
    if (currentPage === 2) {
        var pathVal = document.getElementById('vrcPathInput').value.trim();
        if (pathVal) sendToCS({ action: 'setupSaveVrcPath', path: pathVal });
    }
    if (currentPage === 3) {
        var dirVal = document.getElementById('photoDirInput').value.trim();
        if (dirVal) sendToCS({ action: 'setupSavePhotoDir', path: dirVal });
    }
    if (currentPage >= totalPages - 1) {
        var startWithWin = document.getElementById('setupStartWithWindows').checked;
        sendToCS({ action: 'setupSaveStartWithWindows', enabled: startWithWin });
        sendToCS({ action: 'setupDone' });
        return;
    }
    showPage(currentPage + 1);
}

function skipSetup() {
    sendToCS({ action: 'setupDone' });
}

// VRChat login
function doLogin() {
    var u = document.getElementById('loginUser').value.trim();
    var p = document.getElementById('loginPass').value.trim();
    if (!u || !p) {
        document.getElementById('loginStatus').textContent = 'Please enter username and password';
        return;
    }
    document.getElementById('loginStatus').textContent = 'Connecting...';
    document.getElementById('loginBtn').disabled = true;
    sendToCS({ action: 'vrcLogin', username: u, password: p });
}

function renderLoginSuccess() {
    document.getElementById('loginBox').innerHTML =
        '<div class="login-success">' +
            '<span class="msi">check_circle</span>' +
            '<div>Connected as <strong>' + esc(loggedInName) + '</strong></div>' +
        '</div>';
}

// Two-factor authentication
function show2FA(type) {
    vrc2faType = type;
    document.getElementById('modal2FA').style.display = 'flex';
    document.getElementById('tfaCode').value = '';
    document.getElementById('tfaError').textContent = '';
    document.getElementById('tfaMsg').textContent =
        type === 'emailotp'
            ? 'Enter the 6-digit code sent to your email.'
            : 'Enter the 6-digit code from your authenticator app.';
}

function submit2FA() {
    var code = document.getElementById('tfaCode').value.trim();
    if (code.length < 4) { document.getElementById('tfaError').textContent = 'Enter a valid code'; return; }
    sendToCS({ action: 'vrc2FA', code: code, type: vrc2faType });
}

// File and folder browsing
function browseVrcPath() {
    sendToCS({ action: 'browseExe', target: 'vrchat' });
}

function browsePhotoDir() {
    sendToCS({ action: 'setupBrowsePhotoDir' });
}

// Window controls
function winMin()   { sendToCS({ action: 'windowMinimize' }); }
function winMax()   { sendToCS({ action: 'windowMaximize' }); }
function winClose() { sendToCS({ action: 'windowClose' }); }

function esc(s) {
    var d = document.createElement('div');
    d.textContent = s;
    return d.innerHTML;
}

// Message handler (C# to JS)
function onBackendMessage(e) {
    var d = e.data;
    if (!d || !d.type) return;
    var type = d.type, p = d.payload;

    switch (type) {
        case 'setupState':
            if (p && p.vrcPath) document.getElementById('vrcPathInput').value = p.vrcPath;
            if (p && p.photoDir) document.getElementById('photoDirInput').value = p.photoDir;
            if (p && p.loggedIn && p.displayName) {
                isLoggedIn = true;
                loggedInName = p.displayName;
                if (currentPage === 1) renderLoginSuccess();
            }
            break;

        case 'setupPhotoDirResult':
            if (p) document.getElementById('photoDirInput').value = p;
            break;

        case 'vrcUser':
            isLoggedIn = true;
            loggedInName = (p && p.displayName) || '';
            document.getElementById('modal2FA').style.display = 'none';
            if (currentPage === 1) renderLoginSuccess();
            break;

        case 'vrcNeeds2FA':
            show2FA((p && p.type) || 'totp');
            document.getElementById('loginBtn').disabled = false;
            document.getElementById('loginStatus').textContent = '';
            break;

        case 'vrcLoginError':
            document.getElementById('loginBtn').disabled = false;
            document.getElementById('loginStatus').textContent = (p && p.error) || 'Login failed';
            document.getElementById('tfaError').textContent = (p && p.error) || '';
            break;

        case 'exeAdded':
            if (p && p.target === 'vrchat' && p.path) {
                document.getElementById('vrcPathInput').value = p.path;
            }
            break;

        case 'vrcPrefillLogin':
            if (p && p.username) {
                var uEl = document.getElementById('loginUser');
                var pEl = document.getElementById('loginPass');
                if (uEl && !uEl.value) uEl.value = p.username;
                if (pEl && !pEl.value) pEl.value = p.password || '';
            }
            break;
        case 'setPlatform':
            if (p && p.isLinux) {
                document.querySelectorAll('[data-windows-only]').forEach(function(el) { el.style.display = 'none'; });
            }
            break;
    }
}

// Init
window.external.receiveMessage(rawMsg => { onBackendMessage({ data: JSON.parse(rawMsg) }); });

document.addEventListener('DOMContentLoaded', function() {
    var bar = document.getElementById('titlebar');
    if (bar) {
        bar.addEventListener('mousedown', function(e) {
            if (e.target.closest('.win-btn')) return;
            if (e.button === 0) sendToCS({ action: 'windowDragStart' });
        });
    }
    showPage(0);
    sendToCS({ action: 'setupReady' });
});
