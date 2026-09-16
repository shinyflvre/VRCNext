var _helpPanelOpen = false;
var _helpDismiss = null;

var _apiHealthState = null;
var _apiHealthPanelOpen = false;
var _apiHealthDismiss = null;

function _apiHealthLevel(indicator) {
    if (!indicator || indicator === 'none') return 'ok';
    if (indicator === 'minor' || indicator === 'maintenance') return 'warn';
    return 'err';
}

function _apiHealthCompLevel(status) {
    if (status === 'operational') return 'ok';
    if (status === 'degraded_performance' || status === 'under_maintenance') return 'warn';
    return 'err';
}

function _apiHealthEnabled() {
    var el = document.getElementById('setShowApiHealth');
    return el ? el.checked : true;
}

function _applyApiHealthVisibility() {
    var btn = document.getElementById('tbApiHealth');
    if (btn) btn.style.display = (_apiHealthEnabled() && _apiHealthState) ? '' : 'none';
}

function applyApiHealthSettings() {
    _applyApiHealthVisibility();
    if (_apiHealthEnabled() && !_apiHealthState) sendToCS({ action: 'getApiHealth' });
    if (!_apiHealthEnabled() && _apiHealthPanelOpen) toggleApiHealthPanel();
}

function onApiHealth(payload) {
    if (!payload) return;
    _apiHealthState = payload;
    var dot = document.getElementById('tbApiHealthDot');
    var btn = document.getElementById('tbApiHealth');
    if (dot) {
        var lvl = _apiHealthLevel(payload.indicator);
        dot.className = 'msi tbah-icon ' + lvl;
        dot.textContent = lvl === 'ok' ? 'database' : 'database_off';
    }
    if (btn) btn.title = payload.description || t('apihealth.title', 'VRChat API Status');
    _applyApiHealthVisibility();
}

function _apiHealthPanelEl() {
    var panel = document.getElementById('apiHealthPanel');
    if (panel) return panel;
    panel = document.createElement('div');
    panel.id = 'apiHealthPanel';
    panel.style.display = 'none';
    document.body.appendChild(panel);
    return panel;
}

function toggleApiHealthPanel() {
    var panel = _apiHealthPanelEl();
    _apiHealthPanelOpen = !_apiHealthPanelOpen;
    if (!_apiHealthPanelOpen) {
        if (_apiHealthDismiss) { document.removeEventListener('click', _apiHealthDismiss); _apiHealthDismiss = null; }
        panel.style.display = 'none';
        return;
    }
    var btn = document.getElementById('tbApiHealth');
    if (btn) {
        var r = btn.getBoundingClientRect();
        panel.style.top   = (r.bottom + 6) + 'px';
        panel.style.right = Math.max(8, window.innerWidth - r.right) + 'px';
    }
    _renderApiHealthPanel(null);
    panel.style.display = '';
    sendToCS({ action: 'getApiHealthDetail' });
    setTimeout(function () {
        _apiHealthDismiss = function (ev) {
            var p = document.getElementById('apiHealthPanel');
            var b = document.getElementById('tbApiHealth');
            if (p && !p.contains(ev.target) && (!b || !b.contains(ev.target))) toggleApiHealthPanel();
        };
        document.addEventListener('click', _apiHealthDismiss);
    }, 0);
}

function onApiHealthDetail(payload) {
    if (_apiHealthPanelOpen) _renderApiHealthPanel(payload || {});
}

function _renderApiHealthPanel(detail) {
    var panel = _apiHealthPanelEl();
    var st  = detail || _apiHealthState || {};
    var lvl = _apiHealthLevel(st.indicator);
    var h = '<div class="tbah-head"><span class="tbah-dot ' + lvl + '"></span><span>' + esc(st.description || t('apihealth.title', 'VRChat API Status')) + '</span></div>';

    if (!detail) {
        h += '<div class="tbah-loading">' + esc(t('apihealth.loading', 'Loading details...')) + '</div>';
    } else {
        var graphs = _tbahGraphBlock('visits', t('apihealth.online_users', 'Online Users'),
            detail.onlineUsers >= 0 ? Number(detail.onlineUsers).toLocaleString() : '', detail.visits)
            + _tbahGraphBlock('latency', t('apihealth.latency', 'API Latency'),
            detail.latencyMs >= 0 ? Math.round(detail.latencyMs) + ' ms' : '', detail.latency)
            + _tbahGraphBlock('requests', t('apihealth.requests', 'API Requests'), '', detail.requests)
            + _tbahGraphBlock('errors', t('apihealth.error_rate', 'API Error Rate'), _tbahErrorPct(detail.errors), detail.errors);
        if (graphs) h += '<div class="tbah-cols">' + graphs + '</div>';
        var incs = detail.incidents || [];
        if (incs.length) {
            h += '<div class="tbah-section">' + esc(t('apihealth.incidents', 'Active Incidents')) + '</div>';
            incs.forEach(function (i) {
                h += '<div class="tbah-row"><span class="tbah-dot err"></span><span class="tbah-row-name">' + esc(i.name) + '</span></div>';
            });
        }
        var groupCells = '';
        (detail.componentGroups || []).forEach(function (g) {
            var comps = g.components || [];
            if (!comps.length) return;
            groupCells += '<div class="tbah-group"><div class="tbah-section">' + esc(g.name || t('apihealth.components', 'Services')) + '</div>';
            comps.forEach(function (c) {
                groupCells += '<div class="tbah-row" title="' + esc(c.status) + '"><span class="tbah-dot ' + _apiHealthCompLevel(c.status) + '"></span><span class="tbah-row-name">' + esc(c.name) + '</span></div>';
            });
            groupCells += '</div>';
        });
        if (groupCells) h += '<div class="tbah-cols">' + groupCells + '</div>';
    }
    h += '<button class="vrcn-button tbah-open" onclick="sendToCS({action:\'openUrl\',url:\'https://status.vrchat.com\'})"><span class="msi" style="font-size:14px;">open_in_new</span> <span>' + esc(t('apihealth.open_page', 'Open Status Page')) + '</span></button>';
    panel.innerHTML = h;

    if (detail) {
        var ac = _tbahAccent();
        _tbahDrawGraph(panel.querySelector('[data-tbah-graph="visits"]'), detail.visits, { color: ac, fill: ac + '40', zeroBase: true });
        _tbahDrawGraph(panel.querySelector('[data-tbah-graph="latency"]'), detail.latency, { color: ac });
        _tbahDrawGraph(panel.querySelector('[data-tbah-graph="requests"]'), detail.requests, { color: ac });
        _tbahDrawGraph(panel.querySelector('[data-tbah-graph="errors"]'), detail.errors, { color: ac, fill: ac + '40', zeroBase: true });
    }
}

function _tbahErrorPct(series) {
    if (!Array.isArray(series) || !series.length) return '';
    var v = (series[series.length - 1][1] || 0) * 100;
    if (v === 0) return '0%';
    if (v < 0.001) return v.toFixed(5) + '%';
    if (v < 0.1)   return v.toFixed(4) + '%';
    return v.toFixed(2) + '%';
}

function _tbahTime(ts) {
    try {
        var d = new Date(ts * 1000);
        return typeof fmtTime === 'function' ? fmtTime(d) : d.toTimeString().slice(0, 5);
    } catch { return ''; }
}

function _tbahGraphBlock(key, label, valueText, series) {
    var hasGraph = Array.isArray(series) && series.length >= 2;
    if (!hasGraph && !valueText) return '';
    var h = '<div class="tbah-graph-block"><div class="tbah-stat"><span>' + esc(label) + '</span><span>' + esc(valueText) + '</span></div>';
    if (hasGraph) {
        var first = series[0][0], last = series[series.length - 1][0];
        h += '<canvas class="tbah-graph" data-tbah-graph="' + key + '" width="180" height="54"></canvas>';
        h += '<div class="tbah-graph-axis"><span>' + esc(_tbahTime(first)) + '</span><span>' + esc(_tbahTime((first + last) / 2)) + '</span><span>' + esc(_tbahTime(last)) + '</span></div>';
    }
    return h + '</div>';
}

function _tbahAccent() {
    var raw = getComputedStyle(document.documentElement).getPropertyValue('--accent').trim() || '#5682f4';
    return /^#[0-9a-f]{6}$/i.test(raw) ? raw : '#5682f4';
}

function _tbahDrawGraph(canvas, series, opts) {
    if (!canvas) return;
    if (!Array.isArray(series) || series.length < 2) { canvas.style.display = 'none'; return; }
    var ctx = canvas.getContext('2d');
    var W = canvas.width, H = canvas.height;
    ctx.clearRect(0, 0, W, H);
    var min = Infinity, max = -Infinity;
    for (var i = 0; i < series.length; i++) {
        var v = series[i][1];
        if (v < min) min = v;
        if (v > max) max = v;
    }
    if (opts.zeroBase) min = 0;
    var span = (max - min) || 1;
    var pad = 3;
    var px = function (i) { return pad + (i / (series.length - 1)) * (W - pad * 2); };
    var py = function (v) { return H - pad - ((v - min) / span) * (H - pad * 2); };
    var tracePath = function () {
        ctx.beginPath();
        for (var i = 0; i < series.length; i++) {
            if (i) ctx.lineTo(px(i), py(series[i][1]));
            else   ctx.moveTo(px(i), py(series[i][1]));
        }
    };
    if (opts.fill) {
        tracePath();
        ctx.lineTo(px(series.length - 1), H - pad);
        ctx.lineTo(px(0), H - pad);
        ctx.closePath();
        ctx.fillStyle = opts.fill;
        ctx.fill();
    }
    tracePath();
    ctx.strokeStyle = opts.color;
    ctx.lineWidth = 1;
    ctx.stroke();
}

// Updates the App-dropdown user header with the active user's icon and display name.
function updateTbAppUserHeader() {
    var av = document.getElementById('tbAppUserAvatar');
    var nm = document.getElementById('tbAppUserName');
    if (!av || !nm) return;
    var u = (typeof currentVrcUser !== 'undefined') ? currentVrcUser : null;
    if (u) {
        av.style.backgroundImage = u.image ? "url('" + imgThumb(u.image, 64).replace(/'/g, "\\'") + "')" : '';
        nm.textContent = u.displayName || '';
    } else {
        av.style.backgroundImage = '';
        nm.textContent = '';
    }
}

// Fills the App > Switch Accounts submenu from the accounts list.
function renderTbAccountsMenu(state) {
    var drop = document.getElementById('tbAccountsSubDrop');
    if (!drop) return;
    var st       = state || (typeof _accountsState !== 'undefined' ? _accountsState : null) || {};
    var accounts = st.accounts || [];
    var activeId = st.activeAccountId || '';
    var manage   = '<button class="tb-dd-item" onclick="tbCloseMenus();openAccountsSection()"><span class="msi">manage_accounts</span><span>'
                 + esc(t('tb.app.manage_accounts', 'Manage Accounts')) + '</span></button>';

    if (!accounts.length) {
        drop.innerHTML = '<div class="tb-dd-acc-empty">' + esc(t('tb.app.no_accounts', 'No accounts yet')) + '</div>'
                       + '<div class="tb-dd-sep"></div>' + manage;
        return;
    }

    drop.innerHTML = accounts.map(function (a) {
        var isActive = a.isActive || a.accountId === activeId;
        var name     = a.displayName || a.username || '(unnamed)';
        var img      = a.avatarImageUrl || '';
        var av       = img
            ? '<span class="tb-dd-acc-av" style="background-image:url(\'' + cssUrl(imgThumb(img, 64)) + '\')"></span>'
            : '<span class="tb-dd-acc-av tb-dd-acc-av-letter">' + esc((name[0] || '?').toUpperCase()) + '</span>';
        var mark = isActive ? '<span class="msi tb-dd-acc-check">check_circle</span>' : '';
        var cls  = 'tb-dd-item tb-dd-acc' + (isActive ? ' tb-dd-acc-active' : '');
        var click = isActive ? '' : ' onclick="tbCloseMenus();switchToAccount(\'' + jsq(a.accountId) + '\')"';
        return '<button class="' + cls + '"' + click + ' data-keep-modal>'
             + av + '<span class="tb-dd-acc-name">' + esc(name) + '</span>' + mark + '</button>';
    }).join('') + '<div class="tb-dd-sep"></div>' + manage;
}

document.documentElement.addEventListener('languagechange', function () { renderTbAccountsMenu(); });

// Opens the Settings tab and switches to the Accounts section.
function openAccountsSection() {
    if (typeof showTab === 'function') showTab(9);
    if (typeof switchSettingsSection === 'function') switchSettingsSection('accounts', null);
}

function toggleHelpPanel() {
    _helpPanelOpen = !_helpPanelOpen;
    var panel = document.getElementById('helpPanel');
    if (_helpPanelOpen) {
        var titleEl = document.getElementById('pageTitle');
        if (titleEl) {
            var r = titleEl.getBoundingClientRect();
            panel.style.left = Math.max(8, r.left) + 'px';
        }
        var activeTab = document.querySelector('.tab.active');
        var tabIndex = activeTab ? (parseInt(activeTab.id.replace('tab', '')) || 0) : 0;
        document.getElementById('helpPanelTitle').textContent = titleEl ? titleEl.textContent : '';
        document.getElementById('helpPanelText').textContent = t('page.help.' + tabIndex, '');
        panel.style.display = '';
        requestAnimationFrame(function() { panel.classList.add('panel-open'); });
        setTimeout(function() {
            _helpDismiss = function(ev) {
                var p = document.getElementById('helpPanel');
                var tl = document.getElementById('pageTitle');
                if (!p.contains(ev.target) && ev.target !== tl) toggleHelpPanel();
            };
            document.addEventListener('click', _helpDismiss);
        }, 0);
    } else {
        if (_helpDismiss) { document.removeEventListener('click', _helpDismiss); _helpDismiss = null; }
        panel.classList.remove('panel-open');
        setTimeout(function() { if (!_helpPanelOpen) panel.style.display = 'none'; }, 90);
    }
}

function tbZoomStep(dir) {
    var z = Math.round((_guiZoom + dir * 0.1) * 10) / 10;
    z = Math.min(2, Math.max(0.5, z));
    applyGuiZoom(z);
    try { autoSave(); } catch {}
}

function tbToggleTools() {
    var group  = document.getElementById('tbToolsGroup');
    var menu   = document.getElementById('tbMenuItems');
    var btn    = document.getElementById('tbToolsToggle');
    var open   = group.classList.contains('tb-expanded');
    group.classList.toggle('tb-expanded', !open);
    menu.classList.toggle('tb-collapsed', !open);
    btn.classList.toggle('tb-active', !open);
}

(function () {
    var _open = null;
    var _openSubDrop = null;
    var _subCloseTimer = null;

    function cancelSubClose() {
        if (_subCloseTimer) { clearTimeout(_subCloseTimer); _subCloseTimer = null; }
    }

    function scheduleSubClose() {
        cancelSubClose();
        var st = window.safeTriangle;
        if (st && _openSubDrop && st.isProtected()) {
            _subCloseTimer = setTimeout(scheduleSubClose, 40);
            return;
        }
        _subCloseTimer = setTimeout(closeSubDrop, st ? st.cfg.closeDelay : 200);
    }

    function closeSubDrop() {
        cancelSubClose();
        if (window.safeTriangle) window.safeTriangle.reset();
        if (_openSubDrop) { _openSubDrop.style.display = 'none'; _openSubDrop = null; }
    }

    function closeMenus() {
        closeSubDrop();
        if (_open) { _open.classList.remove('open'); _open = null; }
    }
    window.tbCloseMenus = closeMenus;

    function activateMenu(item) {
        if (_open && _open !== item) _open.classList.remove('open');
        var drop = item.querySelector('.tb-dropdown');
        if (drop) {
            var r = item.getBoundingClientRect();
            drop.style.top  = r.bottom + 'px';
            drop.style.left = r.left + 'px';
        }
        item.classList.add('open');
        _open = item;
    }

    document.querySelectorAll('.tb-menu-item').forEach(function (item) {
        item.addEventListener('mousedown', function (e) {
            // Click came from inside the dropdown — let it through untouched
            if (e.target.closest('.tb-dropdown')) return;
            e.stopPropagation();
            if (item.classList.contains('open')) { closeMenus(); } else { activateMenu(item); }
        });
        item.addEventListener('mouseenter', function () {
            if (_open && _open !== item) activateMenu(item);
        });
    });

    document.querySelectorAll('.tb-dd-submenu').forEach(function(sub) {
        var drop = sub.querySelector('.tb-dd-sub-drop');
        if (!drop) return;
        sub.addEventListener('mouseenter', function() {
            if (_openSubDrop && _openSubDrop !== drop && window.safeTriangle
                && window.safeTriangle.isProtected()) return;
            cancelSubClose();
            if (_openSubDrop && _openSubDrop !== drop) closeSubDrop();
            var r = sub.getBoundingClientRect();
            var vw = window.innerWidth;
            var vh = window.innerHeight;
            drop.style.visibility = 'hidden';
            drop.style.display = 'block';
            var sw = drop.offsetWidth;
            var sh = drop.offsetHeight;
            drop.style.visibility = '';
            var left = r.right - 1;
            if (left + sw > vw - 4) left = r.left - sw + 1;
            var top = r.top;
            if (top + sh > vh - 4) top = Math.max(4, vh - sh - 4);
            drop.style.left = Math.max(4, left) + 'px';
            drop.style.top = top + 'px';
            _openSubDrop = drop;
            if (window.safeTriangle) window.safeTriangle.register(drop, sub);
        });
        sub.addEventListener('mouseleave', scheduleSubClose);
        drop.addEventListener('mouseenter', cancelSubClose);
        drop.addEventListener('mouseleave', scheduleSubClose);
    });

    // Close menu after a dropdown item is clicked (fires after onclick)
    document.querySelectorAll('.tb-dd-item').forEach(function (ddItem) {
        ddItem.addEventListener('click', function () {
            closeMenus();
            if (!ddItem.hasAttribute('data-keep-modal')) {
                document.querySelectorAll('.modal-overlay').forEach(function (ov) {
                    if (ov.style.display && ov.style.display !== 'none') ov.style.display = 'none';
                });
                if (typeof navClear === 'function') navClear();
            }
        });
    });

    // Close on click anywhere outside a menu
    document.addEventListener('mousedown', function (e) {
        if (!e.target.closest('.tb-menu-item')) closeMenus();
    });

    // Drag: taskbar background (excluding interactive elements)
    var bar = document.getElementById('taskbar');
    if (bar) {
        bar.addEventListener('mousedown', function (e) {
            if (e.button !== 0 || e.detail !== 1) return;
            if (e.target.closest('.tb-menu-item,.tb-sidebar-btn,.tb-win-btn,.mini-badge,.ss-wrap,.topbar-title,button,input')) return;
            if (e.clientY < 6) return; // top resize zone — let resize handler take over
            sendToCS({ action: 'windowDragStart' });
        });
        bar.addEventListener('dblclick', function (e) {
            if (e.target.closest('.tb-menu-item,.tb-sidebar-btn,.tb-win-btn,.mini-badge,.ss-wrap,.topbar-title,button,input')) return;
            sendToCS({ action: 'windowMaximize' });
        });
    }
}());
