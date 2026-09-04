/* VRCNext Setup Wizard */

var currentPage = 0;
var totalPages = 10;
var isLoggedIn = false;
var loggedInName = '';
var vrc2faType = 'totp';
var selectedLanguage = 'en';
var _setupTr = {};

var PAGE_VRCPLUS   = 6;
var PAGE_SIDEBAR   = 7;
var PAGE_PREVIEW   = 8;

function t(key, fallback) {
    return _setupTr[key] || fallback || '';
}

function applySetupTranslations() {
    document.querySelectorAll('[data-i18n]').forEach(function(el) {
        var key = el.getAttribute('data-i18n');
        var v = _setupTr[key];
        if (v) el.textContent = v;
    });
    document.querySelectorAll('[data-i18n-html]').forEach(function(el) {
        var key = el.getAttribute('data-i18n-html');
        var v = _setupTr[key];
        if (v) el.innerHTML = v;
    });
    document.querySelectorAll('[data-i18n-placeholder]').forEach(function(el) {
        var key = el.getAttribute('data-i18n-placeholder');
        var v = _setupTr[key];
        if (v) el.placeholder = v;
    });
    // Update nav buttons
    var nextBtn = document.getElementById('btnNext');
    if (nextBtn) {
        if (currentPage === totalPages - 1) {
            nextBtn.innerHTML = '<span class="msi" style="font-size:16px;vertical-align:middle;">check_circle</span> ' + t('setup.nav.finish', 'Finish Setup');
        } else {
            nextBtn.innerHTML = t('setup.nav.next', 'Next') + ' <span class="msi" style="font-size:16px;vertical-align:middle;">arrow_forward</span>';
        }
    }
    // Update 2FA modal
    var tfaMsg = document.getElementById('tfaMsg');
    if (tfaMsg) {
        tfaMsg.textContent = vrc2faType === 'emailotp'
            ? t('setup.tfa.msg_email', 'Enter the 6-digit code sent to your email.')
            : t('setup.tfa.msg_totp', 'Enter the 6-digit code from your authenticator app.');
    }
}

var SETUP_LANGUAGES = [
    { key: 'en',    flag: '\uD83C\uDDFA\uD83C\uDDF8', label: 'English' },
    { key: 'de',    flag: '\uD83C\uDDE9\uD83C\uDDEA', label: 'Deutsch' },
    { key: 'fr',    flag: '\uD83C\uDDEB\uD83C\uDDF7', label: 'Fran\u00E7ais' },
    { key: 'es',    flag: '\uD83C\uDDEA\uD83C\uDDF8', label: 'Espa\u00F1ol' },
    { key: 'ja',    flag: '\uD83C\uDDEF\uD83C\uDDF5', label: '\u65E5\u672C\u8A9E' },
    { key: 'zh-cn', flag: '\uD83C\uDDE8\uD83C\uDDF3', label: '\u7B80\u4F53\u4E2D\u6587' },
];

// Communication helper
function sendToCS(obj) {
    window.external.sendMessage(JSON.stringify(obj));
}

function savePrefs(prefs) {
    sendToCS({ action: 'setupSavePrefs', prefs: prefs });
}

// Language grid
function renderSetupLangGrid() {
    var grid = document.getElementById('setupLangGrid');
    if (!grid) return;
    grid.innerHTML = SETUP_LANGUAGES.map(function(l) {
        return '<button class="lang-chip' + (selectedLanguage === l.key ? ' active' : '') + '" onclick="selectSetupLang(\'' + l.key + '\')">'
            + '<span class="lang-flag">' + l.flag + '</span>'
            + l.label
            + '</button>';
    }).join('');
}

function selectSetupLang(key) {
    selectedLanguage = key;
    renderSetupLangGrid();
    sendToCS({ action: 'loadTranslation', language: key });
}

function vrcPlusDecoPrefs(on) {
    if (on) {
        return {
            enableVrcPlusDecorations: true,
            enableProfileIconFrames: true,
            enableProfileIconFramesOthers: true,
            squareIconFrames: true,
            squareIconFramesOthers: true,
            enableNameplateDecoration: true,
            enableNameplateDecorationOthers: true,
            enableProfileBackgrounds: true,
            enableProfileBackgroundsOthers: true,
            enableProfileThemes: true,
            enableProfileThemesOthers: true,
            profileThemeContrast: true,
            profileThemeContrastOthers: true,
            showDecorationsOnDashboard: true,
            showDecorationsOnDashboardOthers: true,
            enableProfileEffects: true,
            enableProfileEffectsOthers: true,
            transparentProfileCards: false,
            transparentProfileCardsOthers: false,
        };
    }
    return { enableVrcPlusDecorations: false };
}

function onVrcPlusDecoToggle(on) {
    document.querySelectorAll('#decoPillList .deco-pill').forEach(function(el) {
        el.classList.toggle('on', !!on);
    });
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
    var pagesWrap = document.getElementById('setupPages');
    if (pagesWrap) pagesWrap.scrollTop = 0;
    document.getElementById('progressFill').style.width = ((idx + 1) / totalPages * 100) + '%';
    document.getElementById('stepCounter').textContent = 'STEP ' + (idx + 1) + ' OF ' + totalPages;

    var nextBtn = document.getElementById('btnNext');
    var skipBtn = document.getElementById('btnSkip');
    if (idx === totalPages - 1) {
        nextBtn.innerHTML = '<span class="msi" style="font-size:16px;vertical-align:middle;">check_circle</span> ' + t('setup.nav.finish', 'Finish Setup');
        nextBtn.classList.add('finish');
        skipBtn.style.display = 'none';
    } else {
        nextBtn.innerHTML = t('setup.nav.next', 'Next') + ' <span class="msi" style="font-size:16px;vertical-align:middle;">arrow_forward</span>';
        nextBtn.classList.remove('finish');
        skipBtn.style.display = '';
    }
    if (idx === 2 && isLoggedIn) renderLoginSuccess();
    applySetupTranslations();
}

function nextPage() {
    if (currentPage === 0) {
        sendToCS({ action: 'setupSaveLanguage', language: selectedLanguage });
    }
    if (currentPage === 3) {
        var pathVal = document.getElementById('vrcPathInput').value.trim();
        if (pathVal) sendToCS({ action: 'setupSaveVrcPath', path: pathVal });
    }
    if (currentPage === 4) {
        var dirVal = document.getElementById('photoDirInput').value.trim();
        if (dirVal) sendToCS({ action: 'setupSavePhotoDir', path: dirVal });
    }
    if (currentPage === PAGE_VRCPLUS) {
        savePrefs(vrcPlusDecoPrefs(document.getElementById('setupVrcPlusDeco').checked));
    }
    if (currentPage === PAGE_SIDEBAR) {
        savePrefs({
            friendsSidebarLocationOnly: document.getElementById('setupSidebarLocationOnly').checked,
        });
    }
    if (currentPage === PAGE_PREVIEW) {
        savePrefs({
            friendsSidebarPreviewCollapsed: document.getElementById('setupSidebarPreviewCollapsed').checked,
        });
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
            ? t('setup.tfa.msg_email', 'Enter the 6-digit code sent to your email.')
            : t('setup.tfa.msg_totp', 'Enter the 6-digit code from your authenticator app.');
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
        case 'translationData':
            if (p && p.translations) {
                _setupTr = p.translations;
                renderSetupLangGrid();
                applySetupTranslations();
            }
            break;

        case 'windowMaxState':
            document.body.classList.toggle('maximized', !!p);
            break;

        case 'setupState':
            if (p && p.vrcPath) document.getElementById('vrcPathInput').value = p.vrcPath;
            if (p && p.photoDir) document.getElementById('photoDirInput').value = p.photoDir;
            if (p && p.language) {
                selectedLanguage = p.language;
                renderSetupLangGrid();
                sendToCS({ action: 'loadTranslation', language: p.language });
            }
            if (p && p.prefs) {
                var pr = p.prefs;
                var decoOn = pr.enableVrcPlusDecorations !== undefined ? !!pr.enableVrcPlusDecorations : !!pr.enableProfileIconFrames;
                document.getElementById('setupVrcPlusDeco').checked = decoOn;
                onVrcPlusDecoToggle(decoOn);
                document.getElementById('setupSidebarLocationOnly').checked = !!pr.friendsSidebarLocationOnly;
                document.getElementById('setupSidebarPreviewCollapsed').checked = !!pr.friendsSidebarPreviewCollapsed;
            }
            if (p && p.startWithSystem) document.getElementById('setupStartWithWindows').checked = true;
            if (p && p.loggedIn && p.displayName) {
                isLoggedIn = true;
                loggedInName = p.displayName;
                if (currentPage === 2) renderLoginSuccess();
            }
            break;

        case 'setupPhotoDirResult':
            if (p) document.getElementById('photoDirInput').value = p;
            break;

        case 'vrcUser':
            isLoggedIn = true;
            loggedInName = (p && p.displayName) || '';
            document.getElementById('modal2FA').style.display = 'none';
            if (currentPage === 2) renderLoginSuccess();
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
                document.documentElement.style.setProperty('--tb-h', '0px');

                // Page 4: VRChat path — Linux uses steam command, no browse needed
                var vrcInput = document.getElementById('vrcPathInput');
                if (vrcInput) {
                    vrcInput.value = 'steam steam://rungameid/438100';
                    vrcInput.readOnly = true;
                    vrcInput.placeholder = 'steam steam://rungameid/438100';
                }
                var browseBtn = document.getElementById('vrcBrowseBtn');
                if (browseBtn) browseBtn.style.display = 'none';
                var vrcDesc = document.getElementById('vrcPathDesc');
                if (vrcDesc) vrcDesc.innerHTML = 'VRChat is launched through <strong>Steam</strong> on Linux. VRCNext automatically uses the Steam command below &mdash; no manual path selection needed.';
                var vrcHint = document.getElementById('vrcPathHint');
                if (vrcHint) vrcHint.innerHTML = '<span class="msi" style="font-size:13px;vertical-align:middle;color:var(--accent);">info</span> VRChat runs via Proton. Steam handles the launch automatically.';

                [
                    ['welcomeStep10Title', 'setup.page2.step10_title_linux', 'Start with Linux'],
                    ['welcomeStep10Desc',  'setup.page2.step10_desc_linux',  'Launch VRCNext automatically when Linux boots'],
                    ['startupPageTitle',   'setup.page7.title_linux',        'Start with Linux'],
                    ['startupPageDesc',    'setup.page7.desc_linux',         'Should VRCNext launch automatically when Linux starts? It will open minimized so it is ready when you need it.'],
                    ['startupToggleTitle', 'setup.page7.toggle_title_linux', 'Auto-start with Linux'],
                    ['startupToggleSub',   'setup.page7.toggle_sub_linux',   'Opens minimized when Linux boots'],
                ].forEach(function (m) {
                    var el = document.getElementById(m[0]);
                    if (!el) return;
                    el.setAttribute('data-i18n', m[1]);
                    el.textContent = t(m[1], m[2]);
                });
            }
            break;
    }
}

(function () {
    var B = 6;
    var cursorMap = { n: 'n-resize', s: 's-resize', e: 'e-resize', w: 'w-resize', ne: 'ne-resize', nw: 'nw-resize', se: 'se-resize', sw: 'sw-resize' };
    function getDir(x, y) {
        var w = window.innerWidth, h = window.innerHeight;
        var l = x < B, r = x > w - B, t = y < B, b = y > h - B;
        if (t && l) return 'nw'; if (t && r) return 'ne';
        if (b && l) return 'sw'; if (b && r) return 'se';
        if (l) return 'w'; if (r) return 'e';
        if (t) return 'n'; if (b) return 's';
        return null;
    }
    [
        'top:0;left:' + B + 'px;right:' + B + 'px;height:' + B + 'px;cursor:n-resize',
        'top:0;left:0;width:' + B + 'px;height:' + B + 'px;cursor:nw-resize',
        'top:0;right:0;width:' + B + 'px;height:' + B + 'px;cursor:ne-resize',
    ].forEach(function (style) {
        var el = document.createElement('div');
        el.style.cssText = 'position:fixed;' + style + ';z-index:99999;background:transparent;';
        document.body.appendChild(el);
    });

    document.addEventListener('mousemove', function (e) {
        var dir = getDir(e.clientX, e.clientY);
        document.documentElement.style.cursor = dir ? cursorMap[dir] : '';
    });
    document.addEventListener('mousedown', function (e) {
        if (e.button !== 0) return;
        var dir = getDir(e.clientX, e.clientY);
        if (dir) { e.preventDefault(); sendToCS({ action: 'windowResizeStart', direction: dir }); }
    });
})();

// Init
window.external.receiveMessage(rawMsg => { onBackendMessage({ data: JSON.parse(rawMsg) }); });

document.addEventListener('DOMContentLoaded', function() {
    var bar = document.getElementById('titlebar');
    if (bar) {
        bar.addEventListener('mousedown', function(e) {
            if (e.target.closest('.tb-win-btn')) return;
            if (e.clientY < 6) return;
            if (e.button === 0 && e.detail === 1) sendToCS({ action: 'windowDragStart' });
        });
        bar.addEventListener('dblclick', function(e) {
            if (e.target.closest('.tb-win-btn')) return;
            sendToCS({ action: 'windowMaximize' });
        });
    }
    renderSetupLangGrid();
    showPage(0);
    sendToCS({ action: 'setupReady' });
    sendToCS({ action: 'loadTranslation', language: selectedLanguage });
});
