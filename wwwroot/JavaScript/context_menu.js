/* === Context Menu Service ===
 * External, self-contained right-click menu with submenu support.
 * Uses event delegation — no modifications to other JS files needed.
 * Entity IDs are extracted from existing onclick attributes via regex.
 */
(function () {
    const menu    = document.createElement('div');
    const submenu = document.createElement('div');
    menu.id    = 'vn-ctx-menu';
    submenu.id = 'vn-ctx-submenu';
    document.body.appendChild(menu);
    document.body.appendChild(submenu);

    let callbacks     = [];
    let confirmState  = null; // { idx, timer }
    let submenuTimer  = null;

    /* ── Dismiss ── */
    document.addEventListener('click', e => {
        if (!menu.contains(e.target) && !submenu.contains(e.target)) hideMenu();
    });
    document.addEventListener('keydown', e => {
        if (e.key === 'Escape') hideMenu();
    });

    /* ── Main listener ── */
    document.addEventListener('contextmenu', e => {
        hideMenu();
        const cfg = getMenuConfig(e);
        if (!cfg) return;
        e.preventDefault();
        showMenu(e.clientX, e.clientY, cfg);
    });

    /* ── Submenu hover persistence ── */
    submenu.addEventListener('mouseenter', () => clearTimeout(submenuTimer));
    submenu.addEventListener('mouseleave', () => {
        submenuTimer = setTimeout(hideSubmenu, 150);
    });

    /* ── Show / Hide ── */
    function showMenu(x, y, items) {
        callbacks = [];
        menu.innerHTML = buildHTML(items);

        menu.style.visibility = 'hidden';
        menu.style.display = 'block';
        const mw = menu.offsetWidth, mh = menu.offsetHeight;
        menu.style.visibility = '';

        const vw = window.innerWidth, vh = window.innerHeight;
        menu.style.left = ((x + mw > vw - 6) ? Math.max(4, x - mw) : x) + 'px';
        menu.style.top  = ((y + mh > vh - 6) ? Math.max(4, y - mh) : y) + 'px';

        // Regular items
        menu.querySelectorAll('.vn-ctx-item[data-idx]:not(.has-sub)').forEach(btn => {
            btn.addEventListener('mouseenter', () => {
                submenuTimer = setTimeout(hideSubmenu, 100);
            });
            btn.addEventListener('click', e => {
                e.stopPropagation();
                const item = callbacks[+btn.dataset.idx];
                if (!item) return;
                if (item.confirm) handleConfirm(btn, item, +btn.dataset.idx);
                else { item.action(); hideMenu(); }
            });
        });

        // Submenu trigger items
        menu.querySelectorAll('.vn-ctx-item.has-sub').forEach(btn => {
            const open = () => {
                clearTimeout(submenuTimer);
                hideSubmenu();
                callbacks[+btn.dataset.idx]?.submenuFn?.(btn);
            };
            btn.addEventListener('mouseenter', open);
            btn.addEventListener('click', e => { e.stopPropagation(); open(); });
            btn.addEventListener('mouseleave', () => {
                submenuTimer = setTimeout(hideSubmenu, 150);
            });
        });
    }

    function hideMenu() {
        hideSubmenu();
        if (confirmState) { clearTimeout(confirmState.timer); confirmState = null; }
        menu.style.display = 'none';
        menu.innerHTML = '';
        callbacks = [];
    }

    function hideSubmenu() {
        clearTimeout(submenuTimer);
        submenu.style.display = 'none';
        submenu.innerHTML = '';
    }

    /* ── Favorites submenu ── */
    function showFavGroupSubmenu(worldId, parentBtn) {
        const groups = (typeof favWorldGroups !== 'undefined') ? favWorldGroups : [];

        if (groups.length === 0) {
            submenu.innerHTML = `<div class="vn-ctx-loading">
                <span class="msi">hourglass_empty</span><span>Loading groups…</span>
            </div>`;
        } else {
            submenu.innerHTML = groups.map(g => {
                const count = (typeof favWorldsData !== 'undefined')
                    ? favWorldsData.filter(fw => fw.favoriteGroup === g.name).length
                    : 0;
                return `<button class="vn-ctx-item"
                    data-fav-name="${g.name}" data-fav-type="${g.type}" data-wid="${worldId}">
                    <span class="msi" style="font-size:14px;">bookmark_border</span>
                    <span class="vn-ctx-label">${esc(g.displayName || g.name)}</span>
                    <span class="vn-ctx-count">${count}</span>
                </button>`;
            }).join('');

            submenu.querySelectorAll('[data-fav-name]').forEach(btn => {
                btn.addEventListener('click', e => {
                    e.stopPropagation();
                    sendToCS({ action: 'vrcAddWorldFavorite', worldId: btn.dataset.wid, groupName: btn.dataset.favName, groupType: btn.dataset.favType, oldFvrtId: '' });
                    hideMenu();
                });
                btn.addEventListener('mouseenter', () => clearTimeout(submenuTimer));
            });
        }

        positionSubmenu(parentBtn);
    }

    function positionSubmenu(parentBtn) {
        const rect = parentBtn.getBoundingClientRect();
        const vw = window.innerWidth, vh = window.innerHeight;
        submenu.style.visibility = 'hidden';
        submenu.style.display = 'block';
        const sw = submenu.offsetWidth, sh = submenu.offsetHeight;
        submenu.style.visibility = '';
        let left = rect.right + 4;
        if (left + sw > vw - 6) left = rect.left - sw - 4;
        let top = rect.top;
        if (top + sh > vh - 6) top = Math.max(4, vh - sh - 6);
        submenu.style.left = left + 'px';
        submenu.style.top  = top  + 'px';
    }

    /* ── Two-step confirm ── */
    function handleConfirm(btn, item, idx) {
        if (confirmState && confirmState.idx === idx) {
            clearTimeout(confirmState.timer);
            confirmState = null;
            item.action();
            hideMenu();
        } else {
            if (confirmState) { clearTimeout(confirmState.timer); resetConfirmBtn(confirmState.idx); }
            btn.classList.add('confirm-pending');
            btn.querySelector('.vn-ctx-label').textContent = 'Confirm?';
            const timer = setTimeout(() => {
                if (confirmState?.idx === idx) { resetConfirmBtn(idx); confirmState = null; }
            }, 3500);
            confirmState = { idx, timer };
        }
    }

    function resetConfirmBtn(idx) {
        const btn = menu.querySelector(`.vn-ctx-item[data-idx="${idx}"]`);
        if (!btn) return;
        btn.classList.remove('confirm-pending');
        btn.querySelector('.vn-ctx-label').textContent = callbacks[idx]?.label || '';
    }

    /* ── HTML builder ── */
    function buildHTML(items) {
        return items.map(item => {
            if (item === 'sep') return '<div class="vn-ctx-sep"></div>';
            const idx = callbacks.length;
            callbacks.push(item);
            const hasSub = !!item.submenuFn;
            const cls = [item.danger ? 'danger' : '', hasSub ? 'has-sub' : ''].filter(Boolean).join(' ');
            const arrow  = hasSub      ? '<span class="msi vn-ctx-arrow">chevron_right</span>' : '';
            const check  = item.checked ? '<span class="msi vn-ctx-check">check</span>' : '';
            const iconEl = item.dotColor
                ? `<span class="vn-ctx-dot" style="background:${item.dotColor}"></span>`
                : `<span class="msi">${item.icon}</span>`;
            return `<button class="vn-ctx-item${cls ? ' ' + cls : ''}" data-idx="${idx}">
                ${iconEl}
                <span class="vn-ctx-label">${item.label}</span>${check}${arrow}
            </button>`;
        }).join('');
    }

    /* ── Entity detection ── */
    function getMenuConfig(e) {
        const el = e.target;

        // Network graph canvas — hit-test against the current graph
        if (el.id === 'netCanvas' && typeof _netGraph !== 'undefined' && _netGraph) {
            const rect = el.getBoundingClientRect();
            const wx = (e.clientX - rect.left  - _netGraph.tx) / _netGraph.scale;
            const wy = (e.clientY - rect.top   - _netGraph.ty) / _netGraph.scale;
            const hit = _netGraph._hitTest(wx, wy);
            if (hit >= 0) {
                const nd = _netGraph.nodes[hit];
                if (nd?.id) return buildFriendItems(nd.id);
            }
            return null; // right-click on empty canvas space — no menu
        }

        if (el.closest('#vrcProfileArea') && (typeof currentVrcUser !== 'undefined') && currentVrcUser) {
            return buildSelfItems();
        }

        const groupCard = el.closest('#myGroupsGrid .s-card');
        if (groupCard) {
            const id = extractId(groupCard, /openGroupDetail\('([^']+)'\)/);
            if (id) return buildGroupItems(id);
        }

        const dashWorld = el.closest('#dashFavWorlds .dash-world-card');
        if (dashWorld) {
            const id = extractId(dashWorld, /openWorldDetail\('([^']+)'\)/);
            if (id) return buildWorldItems(id);
        }

        const worldCard = el.closest('#favWorldsGrid .s-card, #worldSearchArea .s-card');
        if (worldCard) {
            const id = extractId(worldCard, /openWorldSearchDetail\('([^']+)'\)/);
            if (id) return buildWorldItems(id);
        }

        const friendCard = el.closest('.vrc-friend-card, .fd-profile-item, .wd-friend-row, .inst-user-row');
        if (friendCard) {
            const id = extractId(friendCard, /openFriendDetail\('([^']+)'\)/);
            if (id) return buildFriendItems(id);
        }

        const instanceCard = el.closest('#vrcInstanceArea .inst-card');
        if (instanceCard) {
            const wid = (typeof currentInstanceData !== 'undefined') && currentInstanceData?.worldId;
            if (wid && !currentInstanceData.empty && !currentInstanceData.error) return buildWorldItems(wid);
        }

        return null;
    }

    function extractId(el, pattern) {
        return (el.getAttribute('onclick') || '').match(pattern)?.[1] || null;
    }

    /* ── Menu item builders ── */

    function buildGroupItems(id) {
        const g = (typeof myGroups !== 'undefined') && myGroups.find(x => x.id === id);
        const canPost = g && g.canPost === true;
        const items = [
            { icon: 'open_in_new', label: 'Open Details', action: () => openGroupDetail(id) },
            'sep',
        ];
        if (canPost) {
            items.push({ icon: 'edit_note', label: 'Create Post', action: () => openGroupPostModal(id) });
            items.push('sep');
        }
        items.push({ icon: 'logout', label: 'Leave Group', action: () => sendToCS({ action: 'vrcLeaveGroup', groupId: id }), danger: true, confirm: true });
        return items;
    }

    function buildWorldItems(id) {
        const favEntry = (typeof favWorldsData !== 'undefined') && favWorldsData.find(fw => fw.id === id);
        const items = [
            { icon: 'open_in_new', label: 'Open Details', action: () => openWorldSearchDetail(id) },
            'sep',
        ];
        if (favEntry) {
            items.push({ icon: 'star_border', label: 'Remove from Favorites', action: () => removeWorldFavorite(id, favEntry.favoriteId) });
        } else {
            items.push({ icon: 'star', label: 'Add to Favorites', submenuFn: btn => showFavGroupSubmenu(id, btn) });
        }
        return items;
    }

    function buildFriendItems(id) {
        const items = [
            { icon: 'person', label: 'View Profile', action: () => openFriendDetail(id) },
        ];

        const f = (typeof vrcFriendsData !== 'undefined') && vrcFriendsData.find(x => x.id === id);
        if (f) {
            const loc = f.location || '';
            const { instanceType } = parseFriendLocation(loc);
            const isInWorld = loc && loc !== 'offline' && loc !== 'private' && loc !== 'traveling';
            const joinable  = ['public','friends','friends+','hidden','group-public','group-plus','group-members','group'];
            const canJoin          = isInWorld && joinable.includes(instanceType);
            const canRequestInvite = instanceType === 'private';
            const myInInstance     = (typeof currentInstanceData !== 'undefined')
                                  && currentInstanceData && currentInstanceData.location
                                  && !currentInstanceData.empty && !currentInstanceData.error;

            const actionItems = [];
            if (canJoin)          actionItems.push({ icon: 'login',            label: 'Join',                 action: () => friendAction('join', loc, id) });
            if (canRequestInvite) actionItems.push({ icon: 'mail',             label: 'Request Invite',       action: () => friendAction('requestInvite', loc, id) });
            if (myInInstance) {
                actionItems.push(                  { icon: 'send',             label: 'Invite',               action: () => sendToCS({ action: 'vrcInviteFriend', userId: id }) });
                actionItems.push(                  { icon: 'forward_to_inbox', label: 'Invite with Message',  action: () => { openFriendInviteModal(id, f.displayName || id); _invModalToggleMsgs(); } });
            }
            actionItems.push({ icon: 'waving_hand', label: 'Boop!', action: () => sendToCS({ action: 'vrcBoop', userId: id }) });
            if (actionItems.length) { items.push('sep'); actionItems.forEach(i => items.push(i)); }
        }

        if (f) {
            // Favorite / Unfavorite
            const isFav    = Array.isArray(favFriendsData) && favFriendsData.some(x => x.favoriteId === id);
            const favEntry = isFav ? favFriendsData.find(x => x.favoriteId === id) : null;
            items.push('sep');
            items.push(isFav
                ? { icon: 'star_border', label: 'Unfavorite', action: () => sendToCS({ action: 'vrcRemoveFavoriteFriend', userId: id, fvrtId: favEntry?.fvrtId || '' }) }
                : { icon: 'star',        label: 'Favorite',   action: () => sendToCS({ action: 'vrcAddFavoriteFriend',    userId: id }) }
            );

            // Mute / Block / Unfriend
            const isMuted   = Array.isArray(mutedData)   && mutedData.some(x => x.targetUserId === id);
            const isBlocked = Array.isArray(blockedData) && blockedData.some(x => x.targetUserId === id);
            items.push('sep');
            items.push(isMuted
                ? { icon: 'mic',       label: 'Unmute',  action: () => sendToCS({ action: 'vrcUnmute',  userId: id }) }
                : { icon: 'mic_off',   label: 'Mute',    action: () => sendToCS({ action: 'vrcMute',    userId: id }) }
            );
            items.push(isBlocked
                ? { icon: 'lock_open', label: 'Unblock', action: () => sendToCS({ action: 'vrcUnblock', userId: id }) }
                : { icon: 'block',     label: 'Block',   action: () => sendToCS({ action: 'vrcBlock',   userId: id }), danger: true, confirm: true }
            );
            items.push({ icon: 'person_remove', label: 'Unfriend', action: () => sendToCS({ action: 'vrcUnfriend', userId: id }), danger: true, confirm: true });
        } else {
            // Not a friend — offer to add
            items.push('sep');
            items.push({ icon: 'person_add', label: 'Send Friend Request', action: () => sendToCS({ action: 'vrcSendFriendRequest', userId: id }) });
        }
        return items;
    }

    function buildSelfItems() {
        const curStatus = currentVrcUser?.status || 'active';
        const items = [
            { icon: 'manage_accounts', label: 'View Profile', action: () => openMyProfileModal() },
            'sep',
        ];
        STATUS_LIST.forEach(s => {
            items.push({
                dotColor: s.color,
                label:    s.label,
                checked:  curStatus === s.key,
                action:   () => sendToCS({ action: 'vrcUpdateStatus', status: s.key, statusDescription: currentVrcUser?.statusDescription || '' }),
            });
        });
        return items;
    }
}());
