// Event Snipe — Tab 23

let _snipeSelectedGroupId   = '';
let _snipeSelectedGroupName = '';
let _snipeRunning           = false;

function onSnipeTabOpen() {
    snipePopulateGroups();
    sendToCS({ action: 'vrcSnipeStatus' });
}

// Group picker

function snipePopulateGroups(filter) {
    const list = document.getElementById('snipeGroupList');
    if (!list) return;
    const q = (filter || '').toLowerCase();
    const groups = (typeof myGroups !== 'undefined' ? myGroups : [])
        .filter(g => !q || (g.name||'').toLowerCase().includes(q) || (g.shortCode||'').toLowerCase().includes(q));

    if (groups.length === 0) {
        list.innerHTML = `<div style="padding:12px;color:var(--tx3);font-size:calc(12px + var(--fs-off, 0px));text-align:center;">${esc(t('snipe.group_picker.empty', 'No groups found'))}</div>`;
        return;
    }

    list.innerHTML = groups.map(g => {
        const icon = g.iconUrl
            ? `<img class="fd-group-icon" src="${esc(imgThumb(g.iconUrl, 96))}" onerror="this.style.display='none'">`
            : `<div class="fd-group-icon fd-group-icon-empty"><span class="msi" style="font-size:18px;">group</span></div>`;
        const sel = g.id === _snipeSelectedGroupId;
        return `<div class="fd-group-card${sel ? ' ci-group-selected' : ''}" onclick="snipeSelectGroup('${jsq(g.id)}','${jsq(g.name||'')}','${jsq(g.iconUrl||'')}')">
            ${icon}
            <div class="fd-group-card-info">
                <div class="fd-group-card-name">${esc(g.name||'')}</div>
                <div class="fd-group-card-meta">${esc(g.shortCode||'')}</div>
            </div>
        </div>`;
    }).join('');
}

function snipeSelectGroup(id, name, iconUrl) {
    _snipeSelectedGroupId   = id;
    _snipeSelectedGroupName = name;

    const nameEl = document.getElementById('snipeSelectedGroupName');
    const iconEl = document.getElementById('snipeSelectedGroupIcon');
    const hint   = document.getElementById('snipeNoGroupHint');

    if (nameEl) nameEl.textContent = name || id;
    if (hint)   hint.style.display = 'none';
    if (iconEl) {
        if (iconUrl) {
            iconEl.innerHTML = `<img src="${esc(imgThumb(iconUrl, 96))}" style="width:100%;height:100%;object-fit:cover;border-radius:9px;" onerror="this.parentElement.textContent='${esc(name?.[0]?.toUpperCase()||'?')}'">`;
        } else {
            iconEl.textContent = name?.[0]?.toUpperCase() || '?';
        }
    }

    snipePopulateGroups(document.querySelector('#snipeGroupList')?.previousElementSibling?.querySelector('input')?.value || '');
}


// Start / Stop

function snipeBuildBtnHtml() {
    return _snipeRunning
        ? `<span class="msi" style="font-size:16px;">stop</span> Stop`
        : `<span class="msi" style="font-size:16px;">play_arrow</span> Start`;
}

function snipeSyncStateUi() {
    const dot = document.getElementById('snipeDot');
    const txt = document.getElementById('snipeStatusText');
    const btn = document.getElementById('snipeToggleBtn');
    if (dot) dot.className = _snipeRunning ? 'sf-dot online' : 'sf-dot offline';
    if (txt) txt.textContent = _snipeRunning ? t('snipe.status.active', 'Active') : t('snipe.status.not_running', 'Not running');
    if (btn) btn.innerHTML = snipeBuildBtnHtml();
}

function snipeToggle() {
    if (_snipeRunning) {
        sendToCS({ action: 'vrcStopSnipe' });
        return;
    }
    if (!_snipeSelectedGroupId) {
        showToast(false, t('snipe.toast.no_group', 'Please select a group first'));
        return;
    }
    const worldId     = (document.getElementById('snipeWorldIdInput')?.value || '').trim();
    const autoJoin    = document.getElementById('snipeAutoJoin')?.checked ?? true;
    const minCap      = parseInt(document.getElementById('snipeMinCap')?.value || '0', 10) || 0;
    const accessTypes = ['public','group','groupPlus','groupPublic']
        .filter(v => document.getElementById('snipeAt_' + v)?.checked);
    sendToCS({ action: 'vrcStartSnipe', groupId: _snipeSelectedGroupId, worldId, autoJoin, minCapacity: minCap, accessTypes });
}

// Message handlers

function handleSnipeStatus(p) {
    _snipeRunning = !!p.active;
    snipeSyncStateUi();
    const txt = document.getElementById('snipeStatusText');
    if (_snipeRunning && p.rateLimited && txt) {
        txt.textContent = t('snipe.status.rate_limited', 'Rate limited…');
        txt.style.color = 'var(--warn,orange)';
    } else if (txt) {
        txt.style.color = '';
    }
}

function handleSnipeFound(p) {
    const log = document.getElementById('snipeLog');
    if (!log) return;
    const empty = log.querySelector('[data-snipe-empty]');
    if (empty) empty.remove();

    const loc = p.location || '';
    const row = document.createElement('div');
    row.className = 'fd-group-card';
    row.style.cursor = 'default';
    row.innerHTML = `
        <div style="min-width:0;flex:1;">
            <div class="fd-group-card-name">${esc(p.worldName || p.worldId || '?')}</div>
            <div class="fd-group-card-info">${esc(p.accessType || '')} &middot; ${p.userCount ?? '?'}/${p.capacity ?? '?'}</div>
        </div>
        <button class="vrcn-button vrcn-btn-join" onclick="sendToCS({action:'vrcJoinFriend',location:'${jsq(loc)}'})">
            <span class="msi" style="font-size:14px;">login</span> ${esc(t('common.join','Join'))}
        </button>`;
    log.insertBefore(row, log.firstChild);

    showToast(true, `${t('snipe.toast.found','New instance')}: ${esc(p.worldName || p.worldId || p.instanceId || '?')}`);
}

function handleSnipeJoinResult(p) {
    showToast(p.success,
        p.success
            ? t('snipe.toast.joined', 'Joined!')
            : `${t('snipe.toast.join_failed','Join failed')}: ${esc(p.error || 'unknown')}`
    );
}
