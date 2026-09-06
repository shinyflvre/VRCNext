/* === SmartSearch — local cache-only search across friends, worlds, groups, avatars, events === */

const SmartSearch = (() => {
    let _myWorlds = [];
    let _calEventsCache = [];
    let _settingsIndex = null;
    let _modulesIndex  = null;
    let _inited = false;

    function _patchRenderMyWorlds() {
        if (typeof renderMyWorlds !== 'function') return;
        const _orig = renderMyWorlds;
        window.renderMyWorlds = function(worlds) {
            _myWorlds = Array.isArray(worlds) ? worlds : [];
            return _orig.call(this, worlds);
        };
    }

    function _buildSettingsIndex() {
        const tab = document.getElementById('tab9');
        if (!tab) { _settingsIndex = []; return; }
        _settingsIndex = [];
        tab.querySelectorAll('.sf-toggle-row').forEach(row => {
            // Label: first [data-i18n] span that isn't a material icon
            const labelEl = [...row.querySelectorAll('[data-i18n]')]
                .find(el => !el.classList.contains('msi'));
            const label = labelEl?.textContent.trim();
            if (!label) return;

            const input = row.querySelector('input[type="checkbox"], input[type="range"], select');
            if (!input) return;

            // Section: card header text (second span in header, skip .msi icon span)
            const card = row.closest('.vrcn-panel-card, .vrcn-panel-card-pair');
            const sectionEl = card && [...card.querySelectorAll('.vrcn-panel-card-header [data-i18n]')]
                .find(el => !el.classList.contains('msi'));
            const section = sectionEl?.textContent.trim() || '';

            const type = input.type === 'checkbox' ? 'toggle'
                : input.type === 'range' ? 'slider'
                : input.tagName === 'SELECT' ? 'select' : 'input';

            const navSectionEl = row.closest('[data-section]');
            const sectionId = navSectionEl?.dataset.section || '';

            _settingsIndex.push({ row, input, label, section, type, sectionId });
        });
    }

    function _buildModulesIndex() {
        _modulesIndex = [];
        document.querySelectorAll('.sidebar .nav-btn[onclick]').forEach(btn => {
            const onclickVal = btn.getAttribute('onclick') || '';
            const tabMatch = onclickVal.match(/showTab\((\d+)\)/);
            if (!tabMatch) return;
            const tabIndex = parseInt(tabMatch[1], 10);

            const label = btn.querySelector('.nl')?.textContent.trim();
            if (!label) return;

            const icon = btn.querySelector('.ni.msi')?.textContent.trim() || 'apps';
            const isSub = btn.classList.contains('nav-sub');

            // Parent group label for sub-items (e.g. "Tools")
            let group = '';
            if (isSub) {
                const groupBtn = btn.closest('.nav-group')?.querySelector('.nav-group-btn .nl');
                group = groupBtn?.textContent.trim() || '';
            }

            _modulesIndex.push({ btn, tabIndex, label, icon, group });
        });
    }

    // ---- Tools index (tool pages + VRChat modals) ----

    let _toolsIndex = null;

    const TOOL_MODAL_OVERLAYS = {
        openVrcConfigModal:        'modalVrcConfig',
        openVrcLaunchOptionsModal: 'modalVrcLaunchOptions',
        openMessageTemplatesModal: 'modalMessageTemplates',
        openLogViewerModal:        'modalLogViewer',
    };
    const TOOL_CONTROL_SEL = 'input[type="checkbox"], input[type="range"], select, input[type="text"], input[type="number"], input[type="password"], textarea';
    const TOOL_STOP_SEL = '.vrcn-panel-card, .modal-card, .modal-box, .tab';

    function _ssText(el) {
        if (!el) return '';
        const clone = el.cloneNode(true);
        clone.querySelectorAll('.msi, select, input, textarea, button, .toggle').forEach(n => n.remove());
        return clone.textContent.replace(/\s+/g, ' ').trim();
    }

    function _ssHeaderText(hdr) {
        const lab = [...hdr.querySelectorAll('[data-i18n]')].find(e => !e.classList.contains('msi') && !e.closest('select, button'));
        return _ssText(lab) || _ssText(hdr);
    }

    function _ssLabelIn(row) {
        const i18nEl = [...row.querySelectorAll('[data-i18n]')].find(el =>
            !el.classList.contains('msi') && el.tagName !== 'OPTION' && !el.closest('.toggle, button, select'));
        if (i18nEl) return _ssText(i18nEl);
        const textEl = [...row.querySelectorAll('span, div, label')].find(n =>
            !n.closest('.toggle, button, select') && !n.querySelector(TOOL_CONTROL_SEL) && /[A-Za-zÀ-￿]{3,}/.test(_ssText(n)));
        return _ssText(textEl);
    }

    function _ssCtrlType(ctrl) {
        if (!ctrl) return 'section';
        if (ctrl.tagName === 'SELECT') return 'select';
        if (ctrl.type === 'checkbox') return 'toggle';
        if (ctrl.type === 'range') return 'slider';
        return 'input';
    }

    function _ssCardSection(el) {
        const card = el.closest('.vrcn-panel-card');
        if (!card) return '';
        const hdr = card.querySelector('.vrcn-panel-card-header');
        return (hdr && !hdr.contains(el)) ? _ssHeaderText(hdr) : '';
    }

    function _ssSectionOf(el, container) {
        const body = el.closest('.cb-ord-body');
        if (body) {
            const head = body.parentElement?.querySelector('.cb-ord-row');
            const name = head ? _ssLabelIn(head) : '';
            if (name) return name;
        }
        const cardSection = _ssCardSection(el);
        if (cardSection) return cardSection;
        let node = el;
        while (node && node !== container && !node.matches('.vrcn-panel-card')) {
            let sib = node.previousElementSibling;
            while (sib) {
                if (sib.classList.contains('sf-section-label')) return _ssText(sib);
                if (sib.classList.contains('vrcn-panel-card-header')) return _ssHeaderText(sib);
                sib = sib.previousElementSibling;
            }
            node = node.parentElement;
        }
        return '';
    }

    function _buildToolsIndex() {
        _toolsIndex = [];
        const seen = new Set();
        const toolsLabel = (typeof t === 'function') ? t('nav.tools', 'Tools') : 'Tools';
        const linux = !!window._isLinuxUi;

        const add = (ctx, label, type, targets, section, desc, path) => {
            label = (label || '').trim();
            if (!label) return;
            const key = `${ctx.key}|${label.toLowerCase()}|${(section || '').toLowerCase()}`;
            if (seen.has(key)) return;
            seen.add(key);
            _toolsIndex.push({
                ctx, label, type, targets, section: section || '',
                desc: (desc || '').toLowerCase(),
                path: path || [ctx.group, ctx.label, section].filter(Boolean).join(' › '),
            });
        };

        const scan = (ctx, container) => {
            const covered = new Set();
            const cover = (els) => els.forEach(x => {
                covered.add(x);
                x.querySelectorAll(TOOL_CONTROL_SEL).forEach(c => covered.add(c));
            });

            container.querySelectorAll('.vrcn-panel-card-header').forEach(hdr => {
                add(ctx, _ssHeaderText(hdr), 'section', [hdr.closest('.vrcn-panel-card') || hdr], '', '');
            });

            container.querySelectorAll('.sf-toggle-row').forEach(row => {
                const ctrl = row.querySelector(TOOL_CONTROL_SEL);
                if (!ctrl) return;
                cover([row]);
                const next = row.nextElementSibling;
                const desc = _ssText(row.querySelector('.sf-desc, .set-desc'))
                    || ((next && next.matches('.set-desc, .sf-desc')) ? _ssText(next) : '');
                add(ctx, _ssLabelIn(row), _ssCtrlType(ctrl), [row], _ssSectionOf(row, container), desc);
            });

            container.querySelectorAll('label[data-i18n], .sf-section-label[data-i18n]').forEach(lab => {
                if (lab.closest('.sf-toggle-row') || lab.classList.contains('toggle') || lab.classList.contains('msi')) return;
                const label = _ssText(lab);
                if (!label) return;
                const next = lab.nextElementSibling;
                const parent = lab.parentElement;
                let ctrl = null, targets = [lab];
                if (parent && !parent.matches(TOOL_STOP_SEL) && parent !== container
                    && parent.querySelectorAll('label[data-i18n], .sf-section-label').length === 1) {
                    ctrl = parent.querySelector(TOOL_CONTROL_SEL);
                    if (ctrl) targets = [parent];
                }
                if (!ctrl && next && !next.matches('label, .sf-section-label, .vrcn-panel-card-header')) {
                    ctrl = next.matches(TOOL_CONTROL_SEL) ? next : next.querySelector(TOOL_CONTROL_SEL);
                    if (ctrl) targets = [lab, next];
                }
                if (ctrl) cover(targets);
                const desc = (next && next.matches('.set-desc, .sf-desc')) ? _ssText(next) : '';
                add(ctx, label, _ssCtrlType(ctrl), targets, ctrl ? _ssSectionOf(lab, container) : _ssCardSection(lab), desc);
            });

            container.querySelectorAll(TOOL_CONTROL_SEL).forEach(ctrl => {
                if (covered.has(ctrl) || ctrl.closest('.sf-toggle-row')) return;
                let row = (ctrl.closest('label.toggle') || ctrl).parentElement;
                for (let i = 0; i < 3 && row && row !== container && !row.matches(TOOL_STOP_SEL); i++, row = row.parentElement) {
                    if (covered.has(row)) return;
                    const label = _ssLabelIn(row);
                    if (!label) continue;
                    cover([row]);
                    add(ctx, label, _ssCtrlType(ctrl), [row], _ssSectionOf(row, container), _ssText(row.querySelector('.sf-desc, .set-desc')));
                    return;
                }
            });
        };

        if (typeof NAV_ITEMS_DEF !== 'undefined' && typeof NAV_DEFAULT_LAYOUT !== 'undefined') {
            const folder = NAV_DEFAULT_LAYOUT.find(e => e.type === 'folder' && e.id === 'folder-tools');
            for (const key of (folder ? folder.items : [])) {
                const def = NAV_ITEMS_DEF[key];
                if (!def || (def.windowsOnly && linux)) continue;
                const container = document.getElementById('tab' + def.tab);
                if (!container || !container.children.length) continue;
                const label = (typeof t === 'function') ? t(def.i18n, def.label) : def.label;
                const tab = def.tab;
                scan({ key, label, group: '', icon: def.icon, activate: () => { if (typeof showTab === 'function') showTab(tab); } }, container);
            }
        }

        document.querySelectorAll('#tbMenuTools .tb-dd-item[onclick]').forEach(btn => {
            const m = (btn.getAttribute('onclick') || '').match(/^\s*(\w+)\(\)/);
            const fn = m && m[1];
            const overlayId = fn && TOOL_MODAL_OVERLAYS[fn];
            if (!overlayId) return;
            const label = _ssText(btn);
            const icon = btn.querySelector('.msi')?.textContent.trim() || 'tune';
            const groupBtn = btn.closest('.tb-dd-submenu')?.querySelector(':scope > .tb-dd-item');
            const group = groupBtn ? _ssText(groupBtn) : '';
            const ctx = { key: overlayId, label, group, icon, activate: () => { if (typeof window[fn] === 'function') window[fn](); } };
            add(ctx, label, 'modal', [], '', '', [toolsLabel, group].filter(Boolean).join(' › '));
            const container = document.getElementById(overlayId);
            if (container && container.children.length) scan(ctx, container);
        });
    }

    function _revealToolTargets(targets) {
        targets.forEach(el => {
            const block = el.closest('.cb-ord-body')?.closest('.cb-ord-block');
            if (block) block.classList.add('cb-open');
        });
        let els = targets.filter(el => el.offsetParent !== null);
        if (!els.length && targets.length) {
            const card = targets[0].closest('.vrcn-panel-card, .modal-card');
            if (card && card.offsetParent !== null) els = [card];
        }
        if (!els.length) return;
        els[0].scrollIntoView({ behavior: 'smooth', block: 'center' });
        els.forEach(el => {
            if (el.matches(TOOL_CONTROL_SEL)) return;
            const cls = el.matches('.vrcn-panel-card, .modal-card') ? 'ss-tool-card-highlight' : 'ss-setting-highlight';
            el.classList.remove(cls);
            void el.offsetWidth;
            el.classList.add(cls);
            setTimeout(() => el.classList.remove(cls), 1400);
        });
    }

    function _patchCalEvents() {
        if (typeof renderCalendarEvents !== 'function') return;
        const _orig = renderCalendarEvents;
        window.renderCalendarEvents = function(payload) {
            // Mirror the normalization calendar.js does
            let raw = payload;
            if (raw?.events) raw = raw.events;
            else if (raw?.results) raw = raw.results;
            else if (raw?.data) raw = raw.data;
            const all = Array.isArray(raw) ? raw : [];
            if (all.length > 0) _calEventsCache = all;
            return _orig.call(this, payload);
        };
    }

    // ---- Search sections ----

    const SECTIONS = [
        {
            key: 'modules',
            labelKey: 'search.section.modules',
            label: 'Navigation',
            getData: () => {
                if (_modulesIndex === null) _buildModulesIndex();
                return _modulesIndex || [];
            },
            match: (item, q) =>
                item.label.toLowerCase().includes(q) ||
                item.group.toLowerCase().includes(q),
            getImg: () => ({ src: '', circle: false }),
            getName: (item) => item.label,
            getSub: (item) => item.group,
            renderAvatar: (item) => {
                const wrap = document.createElement('div');
                wrap.className = 'ss-item-img-placeholder';
                wrap.style.borderRadius = '8px';
                const span = document.createElement('span');
                span.className = 'msi';
                span.style.cssText = 'font-size:16px;color:var(--accent);';
                span.textContent = item.icon;
                wrap.appendChild(span);
                return wrap;
            },
            onOpen: (item) => {
                if (typeof showTab === 'function') showTab(item.tabIndex);
            },
        },
        {
            key: 'friends',
            labelKey: 'search.section.friends',
            label: 'Friends',
            getData: () => typeof vrcFriendsData !== 'undefined' ? vrcFriendsData : [],
            match: (item, q) =>
                (item.displayName || '').toLowerCase().includes(q) ||
                (item.username || item.userName || '').toLowerCase().includes(q) ||
                (item.id || '').toLowerCase().includes(q),
            getImg: (item) => ({ src: item.image || '', circle: false }),
            getName: (item) => item.displayName || '?',
            getSub: (item) => (typeof statusLabel === 'function')
                ? (item.statusDescription || statusLabel(item.status))
                : (item.statusDescription || item.status || ''),
            renderAvatar: (item) => {
                const presenceType = item.presence === 'web' ? 'web'
                    : (!item.presence || item.presence === 'offline' ? 'offline' : 'online');
                const statusCls = presenceType === 'offline' ? 's-offline'
                    : (typeof statusDotClass === 'function' ? statusDotClass(item.status) : 's-offline');
                const badgeDotCls = presenceType === 'web' ? 'vrc-status-ring' : 'vrc-status-dot';

                const wrap = document.createElement('div');
                wrap.className = 'vrc-friend-avatar-wrap';

                if (item.image) {
                    const img = document.createElement('img');
                    img.className = 'vrc-friend-avatar';
                    img.src = item.image;
                    img.onerror = function() {
                        const ph = document.createElement('div');
                        ph.className = 'vrc-friend-avatar';
                        ph.style.cssText = 'display:flex;align-items:center;justify-content:center;font-size:calc(12px + var(--fs-off, 0px));font-weight:700;color:var(--tx0)';
                        ph.textContent = (item.displayName || '?')[0].toUpperCase();
                        this.parentNode.replaceChild(ph, this);
                    };
                    wrap.appendChild(img);
                } else {
                    const ph = document.createElement('div');
                    ph.className = 'vrc-friend-avatar';
                    ph.style.cssText = 'display:flex;align-items:center;justify-content:center;font-size:calc(12px + var(--fs-off, 0px));font-weight:700;color:var(--tx0)';
                    ph.textContent = (item.displayName || '?')[0].toUpperCase();
                    wrap.appendChild(ph);
                }

                const badge = document.createElement('span');
                badge.className = `vrc-friend-status-badge ${badgeDotCls} ${statusCls}`;
                wrap.appendChild(badge);
                return wrap;
            },
            onOpen: (item) => { if (typeof openFriendDetail === 'function') openFriendDetail(item.id); },
        },
        {
            key: 'favWorlds',
            labelKey: 'search.section.fav_worlds',
            label: 'Favorite Worlds',
            getData: () => typeof favWorldsData !== 'undefined' ? favWorldsData : [],
            match: (item, q) => (item.name || '').toLowerCase().includes(q),
            getImg: (item) => ({ src: item.thumbnailImageUrl || item.imageUrl || '', circle: false }),
            getName: (item) => item.name || '?',
            getSub: () => '',
            onOpen: (item) => { if (typeof openWorldSearchDetail === 'function') openWorldSearchDetail(item.id); },
        },
        {
            key: 'myWorlds',
            labelKey: 'search.section.my_worlds',
            label: 'My Worlds',
            getData: () => _myWorlds,
            match: (item, q) => (item.name || '').toLowerCase().includes(q),
            getImg: (item) => ({ src: item.thumbnailImageUrl || item.imageUrl || '', circle: false }),
            getName: (item) => item.name || '?',
            getSub: () => '',
            onOpen: (item) => { if (typeof openWorldSearchDetail === 'function') openWorldSearchDetail(item.id); },
        },
        {
            key: 'myGroups',
            labelKey: 'search.section.my_groups',
            label: 'My Groups',
            getData: () => typeof myGroups !== 'undefined' ? myGroups : [],
            match: (item, q) => (item.name || '').toLowerCase().includes(q) || (item.shortCode || '').toLowerCase().includes(q),
            getImg: (item) => ({ src: item.iconUrl || '', circle: false }),
            getName: (item) => item.name || '?',
            getSub: (item) => item.shortCode || '',
            onOpen: (item) => { if (typeof openGroupDetail === 'function') openGroupDetail(item.id); },
        },
        {
            key: 'events',
            labelKey: 'search.section.events',
            label: 'Events',
            getData: () => _calEventsCache,
            match: (item, q) => (item.title || '').toLowerCase().includes(q),
            getImg: (item) => ({ src: item.imageUrl || '', circle: false }),
            getName: (item) => item.title || 'Untitled Event',
            getSub: () => '',
            onOpen: (item) => { if (typeof openEventDetail === 'function') openEventDetail(item.ownerId || '', item.id || ''); },
        },
        {
            key: 'myAvatars',
            labelKey: 'search.section.my_avatars',
            label: 'My Avatars',
            getData: () => typeof avatarsData !== 'undefined' ? avatarsData : [],
            match: (item, q) => (item.name || '').toLowerCase().includes(q),
            getImg: (item) => ({ src: item.thumbnailImageUrl || item.imageUrl || '', circle: false }),
            getName: (item) => item.name || '?',
            getSub: () => '',
            onOpen: (item) => { if (typeof openAvatarDetail === 'function') openAvatarDetail(item.id); },
        },
        {
            key: 'favAvatars',
            labelKey: 'search.section.fav_avatars',
            label: 'Favorited Avatars',
            getData: () => typeof favAvatarsData !== 'undefined' ? favAvatarsData : [],
            match: (item, q) => (item.name || '').toLowerCase().includes(q),
            getImg: (item) => ({ src: item.thumbnailImageUrl || item.imageUrl || '', circle: false }),
            getName: (item) => item.name || '?',
            getSub: (item) => item.favoriteGroup || '',
            onOpen: (item) => { if (typeof openAvatarDetail === 'function') openAvatarDetail(item.id); },
        },
        {
            key: 'settings',
            labelKey: 'search.section.settings',
            label: 'Settings',
            getData: () => {
                if (_settingsIndex === null) _buildSettingsIndex();
                return _settingsIndex || [];
            },
            match: (item, q) =>
                item.label.toLowerCase().includes(q) ||
                item.section.toLowerCase().includes(q),
            getImg: () => ({ src: '', circle: false }),
            getName: (item) => item.label,
            getSub: (item) => item.section,
            renderAvatar: (item) => {
                const iconMap = { toggle: 'toggle_on', slider: 'tune', select: 'arrow_drop_down_circle', input: 'edit' };
                const icon = iconMap[item.type] || 'settings';
                const wrap = document.createElement('div');
                wrap.className = 'ss-item-img-placeholder';
                wrap.style.cssText = 'border-radius:8px;';
                const span = document.createElement('span');
                span.className = 'msi';
                span.style.cssText = 'font-size:16px;color:var(--accent);';
                span.textContent = icon;
                wrap.appendChild(span);
                return wrap;
            },
            onOpen: (item) => {
                if (typeof showTab === 'function') showTab(9);
                if (item.sectionId && typeof switchSettingsSection === 'function') {
                    switchSettingsSection(item.sectionId);
                }
                setTimeout(() => {
                    item.row.scrollIntoView({ behavior: 'smooth', block: 'center' });
                    item.row.classList.add('ss-setting-highlight');
                    setTimeout(() => item.row.classList.remove('ss-setting-highlight'), 1400);
                }, 220);
            },
        },
        {
            key: 'tools',
            labelKey: 'nav.tools',
            label: 'Tools',
            max: 8,
            getData: () => {
                if (_toolsIndex === null) {
                    _buildToolsIndex();
                    if (!_toolsIndex.length) { _toolsIndex = null; return []; }
                }
                return _toolsIndex || [];
            },
            match: (item, q) =>
                item.label.toLowerCase().includes(q) ||
                item.path.toLowerCase().includes(q) ||
                item.desc.includes(q),
            rank: (item, q) =>
                item.label.toLowerCase().includes(q) ? 0 :
                item.path.toLowerCase().includes(q) ? 1 : 2,
            getImg: () => ({ src: '', circle: false }),
            getName: (item) => item.label,
            getSub: (item) => item.path,
            renderAvatar: (item) => {
                const iconMap = { toggle: 'toggle_on', slider: 'tune', select: 'arrow_drop_down_circle', input: 'edit' };
                const icon = iconMap[item.type] || item.ctx.icon || 'build';
                const wrap = document.createElement('div');
                wrap.className = 'ss-item-img-placeholder';
                wrap.style.cssText = 'border-radius:8px;';
                const span = document.createElement('span');
                span.className = 'msi';
                span.style.cssText = 'font-size:16px;color:var(--accent);';
                span.textContent = icon;
                wrap.appendChild(span);
                return wrap;
            },
            onOpen: (item) => {
                item.ctx.activate();
                if (!item.targets.length) return;
                setTimeout(() => _revealToolTargets(item.targets), 220);
            },
        },
    ];

    const MAX_PER_SECTION = 5;

    function _query(raw) {
        const q = raw.trim().toLowerCase();
        if (!q) return [];
        const out = [];
        for (const section of SECTIONS) {
            let hits = section.getData().filter(item => section.match(item, q));
            if (section.rank) hits.sort((a, b) => section.rank(a, q) - section.rank(b, q));
            hits = hits.slice(0, section.max || MAX_PER_SECTION);
            if (hits.length) out.push({ section, hits });
        }
        return out;
    }

    const REMOTE_SEARCH_TARGETS = [
        { key: 'worlds', labelKey: 'search.remote.worlds', label: 'Worlds' },
        { key: 'groups', labelKey: 'search.remote.groups', label: 'Groups' },
        { key: 'users', labelKey: 'search.remote.users', label: 'Users' },
        { key: 'avatars', labelKey: 'search.remote.avatars', label: 'Avatars' },
        { key: 'timeline_personal', labelKey: 'search.remote.timeline_personal', label: 'Personal Timeline' },
        { key: 'timeline_friends', labelKey: 'search.remote.timeline_friends', label: 'Friends Timeline' },
    ];

    function _setSearchInputValue(id, query) {
        const input = document.getElementById(id);
        if (input) input.value = query;
        return input;
    }

    function _openRemoteSearchTarget(key, query) {
        const q = query.trim();
        if (!q) return;

        if (key === 'worlds') {
            if (typeof showTab === 'function') showTab(1);
            if (typeof setWorldFilter === 'function') setWorldFilter('search');
            const input = _setSearchInputValue('searchWorldsInput', q);
            if (typeof doSearch === 'function') doSearch('worlds');
            setTimeout(() => input?.focus(), 80);
        } else if (key === 'groups') {
            if (typeof showTab === 'function') showTab(2);
            if (typeof setGroupFilter === 'function') setGroupFilter('search');
            const input = _setSearchInputValue('searchGroupsInput', q);
            if (typeof doSearch === 'function') doSearch('groups');
            setTimeout(() => input?.focus(), 80);
        } else if (key === 'users') {
            if (typeof showTab === 'function') showTab(3);
            if (typeof setPeopleFilter === 'function') setPeopleFilter('search');
            const input = _setSearchInputValue('searchPeopleInput', q);
            if (typeof doSearch === 'function') doSearch('people');
            setTimeout(() => input?.focus(), 80);
        } else if (key === 'avatars') {
            if (typeof showTab === 'function') showTab(4);
            if (typeof setAvatarFilter === 'function') setAvatarFilter('search');
            const input = _setSearchInputValue('avatarSearchInput', q);
            if (typeof doAvatarSearch === 'function') doAvatarSearch();
            setTimeout(() => input?.focus(), 80);
        } else if (key === 'timeline_personal' || key === 'timeline_friends') {
            if (typeof showTab === 'function') showTab(12);
            if (typeof setTlMode === 'function') setTlMode(key === 'timeline_friends' ? 'friends' : 'personal');
            if (typeof tlSearchClearChip === 'function') tlSearchClearChip();
            const input = _setSearchInputValue('tlSearchInput', q);
            if (typeof tlRunSearchFilter === 'function') tlRunSearchFilter();
            setTimeout(() => input?.focus(), 80);
        }

        _close();
    }

    function _renderRemoteSearchActions(query) {
        const wrap = document.createElement('div');
        wrap.className = 'ss-search-in';

        const label = document.createElement('span');
        label.className = 'ss-search-in-label';
        label.textContent = (typeof t === 'function') ? t('search.remote.label', 'Search in:') : 'Search in:';
        wrap.appendChild(label);

        const actions = document.createElement('div');
        actions.className = 'ss-search-in-actions';

        for (const target of REMOTE_SEARCH_TARGETS) {
            const btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'vrcn-button';
            btn.textContent = (typeof t === 'function') ? t(target.labelKey, target.label) : target.label;
            btn.addEventListener('mousedown', (e) => {
                e.preventDefault();
                e.stopPropagation();
            });
            btn.addEventListener('click', (e) => {
                e.preventDefault();
                e.stopPropagation();
                _openRemoteSearchTarget(target.key, query);
            });
            actions.appendChild(btn);
        }

        wrap.appendChild(actions);
        return wrap;
    }

    function _isSettingsToggle(section, item) {
        return section.key === 'settings'
            && item?.type === 'toggle'
            && item.input?.type === 'checkbox';
    }

    function _isSettingsToggleDisabled(item) {
        return !!(item?.input?.disabled || item?.row?.classList.contains('disabled'));
    }

    function _syncSettingsToggle(toggle, item) {
        const checked = !!item.input.checked;
        const disabled = _isSettingsToggleDisabled(item);
        toggle.classList.toggle('is-on', checked);
        toggle.classList.toggle('ss-item-toggle-disabled', disabled);
        toggle.setAttribute('aria-checked', checked ? 'true' : 'false');
        toggle.setAttribute('aria-disabled', disabled ? 'true' : 'false');
    }

    function _syncRenderedSettingsToggles(input) {
        if (!_dropdown) return;
        _dropdown.querySelectorAll('.ss-item-toggle').forEach(toggle => {
            if (input && toggle._ssSettingsInput !== input) return;
            _syncSettingsToggle(toggle, toggle._ssSettingsItem);
        });
    }

    function _toggleSettingFromSearch(item) {
        if (_isSettingsToggleDisabled(item)) return;
        item.input.checked = !item.input.checked;
        item.input.dispatchEvent(new Event('input', { bubbles: true }));
        item.input.dispatchEvent(new Event('change', { bubbles: true }));
        _syncRenderedSettingsToggles();
    }

    function _renderSettingsToggle(item, label) {
        const toggle = document.createElement('button');
        toggle.type = 'button';
        toggle.className = 'ss-item-toggle';
        toggle.setAttribute('role', 'switch');
        toggle.setAttribute('aria-label', label);
        toggle._ssSettingsItem = item;
        toggle._ssSettingsInput = item.input;

        const track = document.createElement('span');
        track.className = 'ss-item-toggle-track';
        const knob = document.createElement('span');
        knob.className = 'ss-item-toggle-knob';
        track.appendChild(knob);
        toggle.appendChild(track);

        toggle.addEventListener('mousedown', (e) => {
            e.preventDefault();
            e.stopPropagation();
        });
        toggle.addEventListener('click', (e) => {
            e.preventDefault();
            e.stopPropagation();
            _toggleSettingFromSearch(item);
        });

        _syncSettingsToggle(toggle, item);
        return toggle;
    }

    // ---- DOM ----
    let _wrap, _badge, _modal, _input, _dropdown;
    let _open = false;
    let _debouncedRender = null;
    let _focusedIdx = -1;
    let _focusedChipIdx = -1;

    function _getNavItems() { return _dropdown ? [..._dropdown.querySelectorAll('.ss-item')] : []; }
    function _getNavChips() { return _dropdown ? [..._dropdown.querySelectorAll('.ss-search-in-actions .vrcn-button')] : []; }

    function _setFocusedItem(idx) {
        _getNavItems().forEach(el => el.classList.remove('ss-item-focused'));
        _getNavChips().forEach(el => el.classList.remove('ss-chip-focused'));
        _focusedChipIdx = -1;
        _focusedIdx = idx;
        const items = _getNavItems();
        if (idx >= 0 && idx < items.length) {
            items[idx].classList.add('ss-item-focused');
            items[idx].scrollIntoView({ block: 'nearest' });
        }
    }

    function _setFocusedChip(idx) {
        _getNavItems().forEach(el => el.classList.remove('ss-item-focused'));
        _getNavChips().forEach(el => el.classList.remove('ss-chip-focused'));
        _focusedIdx = -1;
        _focusedChipIdx = idx;
        const chips = _getNavChips();
        if (idx >= 0 && idx < chips.length) chips[idx].classList.add('ss-chip-focused');
    }

    function _clearNavFocus() { _setFocusedItem(-1); }

    // Build a result row entirely via DOM — avoids any innerHTML/onerror injection issues
    function _renderItem(section, item) {
        const name = section.getName(item);
        const sub = section.getSub(item);
        const imgInfo = section.getImg(item);
        const circleClass = imgInfo.circle ? ' ss-img-circle' : '';

        const row = document.createElement('div');
        row.className = 'ss-item';

        // Image / placeholder — use custom renderer for friends (status badge)
        if (section.renderAvatar) {
            row.appendChild(section.renderAvatar(item));
        } else if (imgInfo.src) {
            const img = document.createElement('img');
            img.className = 'ss-item-img' + circleClass;
            img.src = imgInfo.src;
            img.onerror = function() {
                const ph = document.createElement('div');
                ph.className = 'ss-item-img-placeholder' + circleClass;
                ph.textContent = (name[0] || '?').toUpperCase();
                this.parentNode.replaceChild(ph, this);
            };
            row.appendChild(img);
        } else {
            const ph = document.createElement('div');
            ph.className = 'ss-item-img-placeholder' + circleClass;
            ph.textContent = (name[0] || '?').toUpperCase();
            row.appendChild(ph);
        }


        // Text
        const info = document.createElement('div');
        info.className = 'ss-item-info';
        const nameEl = document.createElement('div');
        nameEl.className = 'ss-item-name';
        nameEl.textContent = name;
        info.appendChild(nameEl);
        if (sub) {
            const subEl = document.createElement('div');
            subEl.className = 'ss-item-sub';
            subEl.textContent = sub;
            info.appendChild(subEl);
        }
        row.appendChild(info);

        if (_isSettingsToggle(section, item)) {
            row.appendChild(_renderSettingsToggle(item, name));
        }

        row._ssActivate = () => { section.onOpen(item); _close(); };

        row.addEventListener('mousedown', (e) => {
            e.preventDefault();
            row._ssActivate();
        });

        return row;
    }

    function _renderResults(results) {
        _focusedIdx = -1;
        _focusedChipIdx = -1;
        _dropdown.innerHTML = '';
        const query = _input.value.trim();
        if (query) {
            _dropdown.appendChild(_renderRemoteSearchActions(query));
        }
        if (results.length === 0) {
            const el = document.createElement('div');
            el.className = 'ss-empty';
            el.textContent = (typeof t === 'function') ? t('search.no_results', 'No results') : 'No results';
            _dropdown.appendChild(el);
        } else {
            for (const { section, hits } of results) {
                const hdr = document.createElement('div');
                hdr.className = 'ss-section-hdr';
                hdr.textContent = (typeof t === 'function') ? t(section.labelKey, section.label) : section.label;
                _dropdown.appendChild(hdr);
                for (const item of hits) _dropdown.appendChild(_renderItem(section, item));
            }
        }
        _showDropdown();
    }

    function _showDropdown() {
        _dropdown.classList.add('ss-visible');
    }

    function _hideDropdown() {
        _dropdown.classList.remove('ss-visible');
        _dropdown.innerHTML = '';
    }

    function _showHint() {
        const hint = document.createElement('div');
        hint.className = 'ss-hint';
        hint.innerHTML = '<span class="msi">search</span>';
        hint.appendChild(document.createTextNode((typeof t === 'function') ? t('search.hint', 'Search friends, worlds, groups, avatars…') : 'Search friends, worlds, groups, avatars…'));
        _dropdown.innerHTML = '';
        _dropdown.appendChild(hint);
        _showDropdown();
    }

    const MODAL_CLOSE_HANDLERS = {
        modalFriendDetail: () => typeof closeFriendDetail === 'function' && closeFriendDetail(true),
        modalWorldDetail: () => typeof closeWorldDetail === 'function' && closeWorldDetail(true),
        modalDetail: () => typeof closeWorldSearchDetail === 'function' && closeWorldSearchDetail(true),
        modalAvatarDetail: () => typeof closeAvatarDetail === 'function' && closeAvatarDetail(true),
        modalInstanceInfo: () => typeof closeInstanceInfoModal === 'function' && closeInstanceInfoModal(),
        modalMyInstance: () => typeof closeMyInstanceDetail === 'function' && closeMyInstanceDetail(true),
        modalFtGpsDetail: () => typeof closeFtGpsDetail === 'function' && closeFtGpsDetail(),
        modalMyProfile: () => typeof closeMyProfile === 'function' && closeMyProfile(),
        modalInvite: () => typeof closeInviteModal === 'function' && closeInviteModal(),
        modalCreateInstance: () => typeof closeCreateInstanceModal === 'function' && closeCreateInstanceModal(),
        modalFriendPicker: () => typeof closeFriendPicker === 'function' && closeFriendPicker(),
        dashLayoutModal: () => typeof closeDashLayoutEditor === 'function' && closeDashLayoutEditor(),
        navEditorOverlay: () => typeof closeNavEditor === 'function' && closeNavEditor(),
        invUploadModal: () => typeof closeInvUploadModal === 'function' && closeInvUploadModal(),
        invDeleteModal: () => typeof closeInvDeleteModal === 'function' && closeInvDeleteModal(),
        deleteModal: () => typeof closeDeleteModal === 'function' && closeDeleteModal(),
        imagePickerOverlay: () => typeof closeImagePicker === 'function' && closeImagePicker(),
        groupPostOverlay: () => typeof closeGroupPostModal === 'function' && closeGroupPostModal(),
        groupCreateOverlay: () => typeof closeCreateGroupModal === 'function' && closeCreateGroupModal(),
        groupEventOverlay: () => typeof closeGroupEventModal === 'function' && closeGroupEventModal(),
    };

    function _isVisibleOverlay(el) {
        return !!el && el.isConnected && getComputedStyle(el).display !== 'none';
    }

    function _closeModalOverlay(el) {
        if (!_isVisibleOverlay(el) || el === _modal) return false;

        if (el === window._inviteModalEl && typeof closeFriendInviteModal === 'function') {
            closeFriendInviteModal();
            return true;
        }
        if (el === window._launchModalEl && typeof closeLaunchModal === 'function') {
            closeLaunchModal();
            return true;
        }
        if (el === window._avatarWearModalEl && typeof closeAvatarWearModal === 'function') {
            closeAvatarWearModal();
            return true;
        }

        const closeHandler = MODAL_CLOSE_HANDLERS[el.id];
        if (closeHandler) {
            closeHandler();
        } else {
            el.style.display = 'none';
        }
        if (_isVisibleOverlay(el)) el.style.display = 'none';
        return true;
    }

    function _closeOpenModalsBeforeOpen() {
        const overlays = [
            ...document.querySelectorAll('.modal-overlay'),
            ...document.querySelectorAll('#imagePickerOverlay, #groupPostOverlay, #groupEventOverlay'),
        ];
        let closedAny = false;

        for (const overlay of new Set(overlays)) {
            closedAny = _closeModalOverlay(overlay) || closedAny;
        }

        if (closedAny && typeof navClear === 'function') navClear();
    }

    function _open_ui() {
        _closeOpenModalsBeforeOpen();
        if (_open) {
            setTimeout(() => _input?.focus(), 0);
            return;
        }
        _open = true;
        _badge.classList.add('tb-active');
        _modal.classList.add('ss-modal-open');
        if (!myGroupsLoaded && typeof loadMyGroups === 'function') loadMyGroups();
        _input.value = '';
        setTimeout(() => _input.focus(), 40);
    }

    function _close() {
        if (!_open) return;
        _open = false;
        _badge.classList.remove('tb-active');
        _modal.classList.remove('ss-modal-open');
        _hideDropdown();
        _input.value = '';
        _hideDebAnim('ssInput');
    }

    function _onInput() {
        const q = _input.value.trim();
        if (!q) { _hideDebAnim('ssInput'); _hideDropdown(); _showHint(); return; }
        _debouncedRender();
    }

    function init() {
        if (_inited) return;
        _wrap     = document.getElementById('ssWrap');
        _badge    = document.getElementById('ssBadge');
        _modal    = document.getElementById('ssModal');
        _input    = document.getElementById('ssInput');
        _dropdown = document.getElementById('ssDropdown');

        if (!_wrap || !_badge || !_modal || !_input || !_dropdown) return;
        _inited = true;

        _debouncedRender = debounceAnim(
            () => _renderResults(_query(_input.value.trim())),
            typeof DEBOUNCE_SEARCH_MS !== 'undefined' ? DEBOUNCE_SEARCH_MS : 500,
            'ssInput'
        );

        _patchRenderMyWorlds();
        _patchCalEvents();

        _badge.addEventListener('click', _open_ui);

        _input.addEventListener('input', _onInput);
        _input.addEventListener('focus', () => {
            if (!_input.value.trim()) _showHint();
            else _showDropdown();
        });
        _input.addEventListener('keydown', (e) => {
            if (e.key === 'Escape') { e.preventDefault(); _close(); return; }
            if (!_dropdown.classList.contains('ss-visible')) return;
            const items = _getNavItems();
            const chips = _getNavChips();
            if (e.key === 'ArrowDown') {
                e.preventDefault();
                _setFocusedItem(Math.min(_focusedIdx + 1, items.length - 1));
            } else if (e.key === 'ArrowUp') {
                e.preventDefault();
                if (_focusedIdx > 0) _setFocusedItem(_focusedIdx - 1);
                else _clearNavFocus();
            } else if (e.key === 'ArrowRight') {
                e.preventDefault();
                if (chips.length) _setFocusedChip(Math.min(Math.max(_focusedChipIdx, -1) + 1, chips.length - 1));
            } else if (e.key === 'ArrowLeft') {
                e.preventDefault();
                if (chips.length) _setFocusedChip(Math.max((_focusedChipIdx < 0 ? chips.length : _focusedChipIdx) - 1, 0));
            } else if (e.key === 'Enter') {
                e.preventDefault();
                if (_focusedChipIdx >= 0 && chips[_focusedChipIdx]) {
                    chips[_focusedChipIdx].click();
                } else if (_focusedIdx >= 0 && items[_focusedIdx]?._ssActivate) {
                    items[_focusedIdx]._ssActivate();
                }
            }
        });

        document.getElementById('ssCloseBtn')?.addEventListener('click', (e) => {
            e.stopPropagation();
            _close();
        });

        document.addEventListener('mousedown', (e) => {
            if (_open && !e.target.closest('.ss-modal-box')) _close();
        });
    }

    function rebuildDebouncer() {
        if (!_inited) return;
        _debouncedRender = debounceAnim(
            () => _renderResults(_query(_input.value.trim())),
            typeof DEBOUNCE_SEARCH_MS !== 'undefined' ? DEBOUNCE_SEARCH_MS : 500,
            'ssInput'
        );
    }

    return { init, open: _open_ui, close: _close, rebuildDebouncer };
})();

window.SmartSearch = SmartSearch;

if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', () => SmartSearch.init());
} else {
    SmartSearch.init();
}
