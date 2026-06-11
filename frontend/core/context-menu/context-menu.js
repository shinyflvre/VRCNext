/* === Context Menu Service ===
 * External, self-contained right-click menu with submenu support.
 * Uses event delegation; no modifications to other JS files needed.
 * Entity IDs are extracted from existing onclick attributes via regex.
 */
(function () {
    const menu = document.createElement('div');
    const submenu = document.createElement('div');
    menu.id = 'vn-ctx-menu';
    submenu.id = 'vn-ctx-submenu';
    document.body.appendChild(menu);
    document.body.appendChild(submenu);

    let callbacks = [];
    let confirmState = null; // { idx, timer }
    let submenuTimer = null;

    function cm(key, fallback = '') {
        return typeof t === 'function' ? t(`context_menu.${key}`, fallback) : fallback;
    }

    function copyWithToast(text, toastKey, fallback) {
        navigator.clipboard.writeText(text);
        showToast(true, cm(toastKey, fallback));
    }

    /* Dismiss */
    document.addEventListener('click', e => {
        if (!menu.contains(e.target) && !submenu.contains(e.target)) hideMenu();
    });
    document.addEventListener('keydown', e => {
        if (e.key === 'Escape') hideMenu();
    });

    /* VRC clipboard link detection */
    function detectVrcClipboard(text) {
        text = (text || '').trim();
        let m;
        if ((m = text.match(/vrchat\.com\/home\/launch\?worldId=(wrld_[\w-]+)&instanceId=(\S+)/i))) return { type: 'instance', id: m[1], instanceId: m[2] };
        if ((m = text.match(/vrchat\.com\/home\/avatar\/(avtr_[\w-]+)/i)))       return { type: 'avatar', id: m[1] };
        if ((m = text.match(/vrchat\.com\/home\/world\/(wrld_[\w-]+)/i)))        return { type: 'world',  id: m[1] };
        if ((m = text.match(/vrchat\.com\/home\/group\/(grp_[\w-]+)/i)))         return { type: 'group',  id: m[1] };
        if ((m = text.match(/vrchat\.com\/home\/user\/(usr_[\w-]+)/i)))          return { type: 'user',   id: m[1] };
        if ((m = text.match(/vrchat:\/\/launch.*[?&]worldId=(wrld_[\w-]+)/i)))  return { type: 'world',  id: m[1] };
        if ((m = text.match(/^(avtr_[\w-]+)$/i)))  return { type: 'avatar', id: m[1], bare: true };
        if ((m = text.match(/^(wrld_[\w-]+)$/i)))  return { type: 'world',  id: m[1], bare: true };
        if ((m = text.match(/^(grp_[\w-]+)$/i)))   return { type: 'group',  id: m[1], bare: true };
        if ((m = text.match(/^(usr_[\w-]+)$/i)))   return { type: 'user',   id: m[1], bare: true };
        return null;
    }
    const VRC_CTX_META = {
        avatar:   { icon: 'checkroom',      labelKey: 'ctx.open_avatar_link',   fallback: 'Open Avatar Link',   bareKey: 'ctx.open_avatar_id',  bareFallback: 'Open Avatar ID'  },
        world:    { icon: 'travel_explore', labelKey: 'ctx.open_world_link',    fallback: 'Open World Link',    bareKey: 'ctx.open_world_id',   bareFallback: 'Open World ID'   },
        group:    { icon: 'group',          labelKey: 'ctx.open_group_link',    fallback: 'Open Group Link',    bareKey: 'ctx.open_group_id',   bareFallback: 'Open Group ID'   },
        user:     { icon: 'person',         labelKey: 'ctx.open_profile_link',  fallback: 'Open Profile Link',  bareKey: 'ctx.open_profile_id', bareFallback: 'Open Profile ID' },
        instance: { icon: 'meeting_room',   labelKey: 'ctx.open_instance_link', fallback: 'Open Instance Link' },
    };

    function getVrcContextLabel(vrcData) {
        const meta = VRC_CTX_META[vrcData?.type];
        if (!meta) return '';
        const key = vrcData.bare && meta.bareKey ? meta.bareKey : meta.labelKey;
        const fallback = vrcData.bare && meta.bareFallback ? meta.bareFallback : meta.fallback;
        return typeof t === 'function' ? t(key, fallback) : fallback;
    }

    function openVrcContextTarget(vrcData) {
        if (!vrcData) return false;
        if      (vrcData.type === 'avatar')   navOpenModal('avatar',       vrcData.id, '');
        else if (vrcData.type === 'world')    navOpenModal('worldSearch',  vrcData.id, '');
        else if (vrcData.type === 'group')    navOpenModal('group',        vrcData.id, '');
        else if (vrcData.type === 'user')     navOpenModal('friend',       vrcData.id, '');
        else if (vrcData.type === 'instance') sendToCS({ action: 'vrcGetInstanceDetail', location: vrcData.id + ':' + vrcData.instanceId });
        else return false;
        return true;
    }

    window.VrcnDirectAccess = {
        detect: detectVrcClipboard,
        getLabel: getVrcContextLabel,
        open: openVrcContextTarget,
        openFromText(text) {
            const vrcData = detectVrcClipboard(text);
            return openVrcContextTarget(vrcData) ? vrcData : null;
        },
    };

    window.VrcnShowContextMenu = (x, y, items) => {
        if (items && items.length) showMenu(x, y, items);
    };
    window.VrcnHideContextMenu = () => hideMenu();

    /* Main listener */
    document.addEventListener('contextmenu', async e => {
        e.preventDefault();
        hideMenu();
        const sel = (typeof _textToolsEnabled !== 'undefined' && _textToolsEnabled)
            ? (window.getSelection()?.toString().trim() ?? '')
            : '';
        const copyItem = sel
            ? { icon: 'content_copy', label: cm('copy', 'Copy'), action: () => navigator.clipboard.writeText(sel).catch(() => {}) }
            : null;

        const clipText = await navigator.clipboard.readText().catch(() => '');
        const tgt = e.target;
        const isEditable = tgt && (tgt.tagName === 'INPUT' || tgt.tagName === 'TEXTAREA' || tgt.isContentEditable);
        const pasteItem = (isEditable && clipText)
            ? { icon: 'content_paste', label: cm('paste', 'Paste'), action: () => { tgt.focus(); document.execCommand('insertText', false, clipText); } }
            : null;

        const editItems = [copyItem, pasteItem].filter(Boolean);

        let cfg = getMenuConfig(e);
        if (editItems.length && cfg) cfg = [...editItems, 'sep', ...cfg];
        else if (editItems.length && !cfg) cfg = editItems;
        const vrcData = detectVrcClipboard(clipText);
        if (vrcData) {
            const meta = VRC_CTX_META[vrcData.type];
            const vrcItem = {
                icon: meta.icon,
                label: getVrcContextLabel(vrcData),
                action: () => openVrcContextTarget(vrcData)
            };
            cfg = cfg ? [vrcItem, 'sep', ...cfg] : [vrcItem];
        }

        if (!cfg) return;
        showMenu(e.clientX, e.clientY, cfg);
    });

    /* Submenu hover persistence */
    submenu.addEventListener('mouseenter', () => clearTimeout(submenuTimer));
    submenu.addEventListener('mouseleave', () => {
        submenuTimer = setTimeout(hideSubmenu, 150);
    });

    /* Show / Hide */
    function showMenu(x, y, items) {
        callbacks = [];
        menu.innerHTML = buildHTML(items);

        menu.style.visibility = 'hidden';
        menu.style.display = 'block';
        const mw = menu.offsetWidth;
        const mh = menu.offsetHeight;
        menu.style.visibility = '';

        const z = (typeof _guiZoom !== 'undefined' ? _guiZoom : 1);
        const vw = window.innerWidth / z;
        const vh = window.innerHeight / z;
        const lx = x / z;
        const ly = y / z;
        menu.style.left = ((lx + mw > vw - 6) ? Math.max(4, lx - mw) : lx) + 'px';
        menu.style.top = ((ly + mh > vh - 6) ? Math.max(4, ly - mh) : ly) + 'px';

        menu.querySelectorAll('.vn-ctx-item[data-idx]:not(.has-sub)').forEach(btn => {
            btn.addEventListener('mouseenter', () => {
                submenuTimer = setTimeout(hideSubmenu, 100);
            });
            btn.addEventListener('click', e => {
                e.stopPropagation();
                const item = callbacks[+btn.dataset.idx];
                if (!item) return;
                if (item.confirm) handleConfirm(btn, item, +btn.dataset.idx);
                else {
                    item.action();
                    hideMenu();
                }
            });
        });

        menu.querySelectorAll('.vn-ctx-item.has-sub').forEach(btn => {
            const open = () => {
                clearTimeout(submenuTimer);
                hideSubmenu();
                callbacks[+btn.dataset.idx]?.submenuFn?.(btn);
            };
            btn.addEventListener('mouseenter', open);
            btn.addEventListener('click', e => {
                e.stopPropagation();
                open();
            });
            btn.addEventListener('mouseleave', () => {
                submenuTimer = setTimeout(hideSubmenu, 150);
            });
        });
    }

    function hideMenu() {
        hideSubmenu();
        if (confirmState) {
            clearTimeout(confirmState.timer);
            confirmState = null;
        }
        menu.style.display = 'none';
        menu.innerHTML = '';
        callbacks = [];
    }

    function hideSubmenu() {
        clearTimeout(submenuTimer);
        submenu.style.display = 'none';
        submenu.innerHTML = '';
    }

    /* Favorites submenu */
    function showFavGroupSubmenu(worldId, parentBtn) {
        const groups = (typeof favWorldGroups !== 'undefined') ? favWorldGroups : [];

        if (groups.length === 0) {
            submenu.innerHTML = `<div class="vn-ctx-loading">
                <span class="msi">hourglass_empty</span><span>${esc(cm('loading_groups', 'Loading groups...'))}</span>
            </div>`;
            positionSubmenu(parentBtn);
            sendToCS({ action: 'vrcGetFavoriteWorlds' });
            let attempts = 0;
            const retry = setInterval(() => {
                const g = (typeof favWorldGroups !== 'undefined') ? favWorldGroups : [];
                if (g.length > 0 || ++attempts > 15) {
                    clearInterval(retry);
                    if (g.length > 0 && submenu.style.display !== 'none') showFavGroupSubmenu(worldId, parentBtn);
                }
            }, 300);
            return;
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
                    sendToCS({
                        action: 'vrcAddWorldFavorite',
                        worldId: btn.dataset.wid,
                        groupName: btn.dataset.favName,
                        groupType: btn.dataset.favType,
                        oldFvrtId: ''
                    });
                    hideMenu();
                });
                btn.addEventListener('mouseenter', () => clearTimeout(submenuTimer));
            });
        }

        positionSubmenu(parentBtn);
    }

    function showMoveToGroupSubmenu(worldId, favEntry, parentBtn) {
        const groups = (typeof favWorldGroups !== 'undefined') ? favWorldGroups : [];
        submenu.innerHTML = groups.map(g => {
            const isCurrent = g.name === favEntry.favoriteGroup;
            const count = (typeof favWorldsData !== 'undefined')
                ? favWorldsData.filter(fw => fw.favoriteGroup === g.name).length
                : 0;
            const iconEl = isCurrent
                ? `<span class="msi" style="font-size:14px;color:var(--accent);">check_circle</span>`
                : `<span class="msi" style="font-size:14px;">drive_file_move</span>`;
            return `<button class="vn-ctx-item${isCurrent ? ' ci-group-selected' : ''}"
                data-move-name="${esc(g.name)}" data-move-type="${esc(g.type)}" data-wid="${esc(worldId)}" data-old-fvrt="${esc(favEntry.favoriteId)}" data-is-current="${isCurrent}">
                ${iconEl}
                <span class="vn-ctx-label">${esc(g.displayName || g.name)}</span>
                <span class="vn-ctx-count">${count}</span>
            </button>`;
        }).join('');
        submenu.querySelectorAll('[data-move-name]').forEach(btn => {
            btn.addEventListener('click', e => {
                e.stopPropagation();
                if (btn.dataset.isCurrent === 'true') { hideMenu(); return; }
                sendToCS({
                    action: 'vrcAddWorldFavorite',
                    worldId: btn.dataset.wid,
                    groupName: btn.dataset.moveName,
                    groupType: btn.dataset.moveType,
                    oldFvrtId: btn.dataset.oldFvrt
                });
                hideMenu();
            });
            btn.addEventListener('mouseenter', () => clearTimeout(submenuTimer));
        });
        positionSubmenu(parentBtn);
    }

    function showFavFriendGroupSubmenu(userId, parentBtn) {
        const groups = (typeof favFriendGroups !== 'undefined') ? favFriendGroups : [];
        if (groups.length === 0) {
            submenu.innerHTML = `<div class="vn-ctx-loading">
                <span class="msi">hourglass_empty</span><span>${esc(cm('loading_groups', 'Loading groups...'))}</span>
            </div>`;
            positionSubmenu(parentBtn);
            sendToCS({ action: 'vrcGetFavoriteFriends' });
            let attempts = 0;
            const retry = setInterval(() => {
                const g = (typeof favFriendGroups !== 'undefined') ? favFriendGroups : [];
                if (g.length > 0 || ++attempts > 15) {
                    clearInterval(retry);
                    if (g.length > 0 && submenu.style.display !== 'none') showFavFriendGroupSubmenu(userId, parentBtn);
                }
            }, 300);
            return;
        }
        submenu.innerHTML = groups.map(g => {
            const count = (typeof favFriendsData !== 'undefined')
                ? favFriendsData.filter(f => f.groupName === g.name).length : 0;
            return `<button class="vn-ctx-item" data-ff-name="${esc(g.name)}" data-ff-uid="${esc(userId)}">
                <span class="msi" style="font-size:14px;">bookmark_border</span>
                <span class="vn-ctx-label">${esc(g.displayName || g.name)}</span>
                <span class="vn-ctx-count">${count}</span>
            </button>`;
        }).join('');
        submenu.querySelectorAll('[data-ff-name]').forEach(btn => {
            btn.addEventListener('click', e => {
                e.stopPropagation();
                sendToCS({ action: 'vrcAddFavoriteFriend', userId: btn.dataset.ffUid, groupName: btn.dataset.ffName });
                hideMenu();
            });
            btn.addEventListener('mouseenter', () => clearTimeout(submenuTimer));
        });
        positionSubmenu(parentBtn);
    }

    function showFavFriendMoveSubmenu(userId, favEntry, parentBtn) {
        const groups = (typeof favFriendGroups !== 'undefined') ? favFriendGroups : [];
        submenu.innerHTML = groups.map(g => {
            const isCurrent = g.name === favEntry?.groupName;
            const count = (typeof favFriendsData !== 'undefined')
                ? favFriendsData.filter(f => f.groupName === g.name).length : 0;
            const iconEl = isCurrent
                ? `<span class="msi" style="font-size:14px;color:var(--accent);">check_circle</span>`
                : `<span class="msi" style="font-size:14px;">drive_file_move</span>`;
            return `<button class="vn-ctx-item${isCurrent ? ' ci-group-selected' : ''}"
                data-ffmv-name="${esc(g.name)}" data-ffmv-uid="${esc(userId)}" data-ffmv-old="${esc(favEntry?.fvrtId || '')}" data-ffmv-current="${isCurrent}">
                ${iconEl}
                <span class="vn-ctx-label">${esc(g.displayName || g.name)}</span>
                <span class="vn-ctx-count">${count}</span>
            </button>`;
        }).join('');
        submenu.querySelectorAll('[data-ffmv-name]').forEach(btn => {
            btn.addEventListener('click', e => {
                e.stopPropagation();
                if (btn.dataset.ffmvCurrent === 'true') { hideMenu(); return; }
                sendToCS({ action: 'vrcAddFavoriteFriendToGroup', userId: btn.dataset.ffmvUid, groupName: btn.dataset.ffmvName, oldFvrtId: btn.dataset.ffmvOld });
                hideMenu();
            });
            btn.addEventListener('mouseenter', () => clearTimeout(submenuTimer));
        });
        positionSubmenu(parentBtn);
    }

    function showFriendEditModeGroupSubmenu(parentBtn) {
        const groups = (typeof favFriendGroups !== 'undefined') ? favFriendGroups : [];
        submenu.innerHTML = groups.map(g => {
            const count = (typeof favFriendsData !== 'undefined')
                ? favFriendsData.filter(f => f.groupName === g.name).length : 0;
            return `<button class="vn-ctx-item"
                data-ff-edit-move-name="${esc(g.name)}">
                <span class="msi" style="font-size:14px;">folder</span>
                <span class="vn-ctx-label">${esc(g.displayName || g.name)}</span>
                <span class="vn-ctx-count">${count}</span>
            </button>`;
        }).join('');
        submenu.querySelectorAll('[data-ff-edit-move-name]').forEach(btn => {
            btn.addEventListener('click', e => {
                e.stopPropagation();
                if (typeof friendEditMoveSelected === 'function') friendEditMoveSelected(btn.dataset.ffEditMoveName);
                hideMenu();
            });
            btn.addEventListener('mouseenter', () => clearTimeout(submenuTimer));
        });
        positionSubmenu(parentBtn);
    }

    function positionSubmenu(parentBtn) {
        const rect = parentBtn.getBoundingClientRect();
        const z = (typeof _guiZoom !== 'undefined' ? _guiZoom : 1);
        const vw = window.innerWidth / z;
        const vh = window.innerHeight / z;
        submenu.style.visibility = 'hidden';
        submenu.style.display = 'block';
        const sw = submenu.offsetWidth;
        const sh = submenu.offsetHeight;
        submenu.style.visibility = '';
        let left = rect.right / z + 4;
        if (left + sw > vw - 6) left = rect.left / z - sw - 4;
        let top = rect.top / z;
        if (top + sh > vh - 6) top = Math.max(4, vh - sh - 6);
        submenu.style.left = left + 'px';
        submenu.style.top = top + 'px';
    }

    /* Two-step confirm */
    function handleConfirm(btn, item, idx) {
        if (confirmState && confirmState.idx === idx) {
            clearTimeout(confirmState.timer);
            confirmState = null;
            item.action();
            hideMenu();
        } else {
            if (confirmState) {
                clearTimeout(confirmState.timer);
                resetConfirmBtn(confirmState.idx);
            }
            btn.classList.add('confirm-pending');
            btn.querySelector('.vn-ctx-label').textContent = cm('confirm', 'Confirm?');
            const timer = setTimeout(() => {
                if (confirmState?.idx === idx) {
                    resetConfirmBtn(idx);
                    confirmState = null;
                }
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

    /* HTML builder */
    function buildHTML(items) {
        return items.map(item => {
            if (item === 'sep') return '<div class="vn-ctx-sep"></div>';
            const idx = callbacks.length;
            callbacks.push(item);
            const hasSub = !!item.submenuFn;
            const cls = [item.danger ? 'danger' : '', hasSub ? 'has-sub' : ''].filter(Boolean).join(' ');
            const arrow = hasSub ? '<span class="msi vn-ctx-arrow">chevron_right</span>' : '';
            const check = item.checked ? '<span class="msi vn-ctx-check">check</span>' : '';
            const iconEl = item.dotColor
                ? `<span class="vn-ctx-dot" style="background:${item.dotColor}"></span>`
                : `<span class="msi">${item.icon}</span>`;
            return `<button class="vn-ctx-item${cls ? ' ' + cls : ''}" data-idx="${idx}">
                ${iconEl}
                <span class="vn-ctx-label">${esc(item.label)}</span>${check}${arrow}
            </button>`;
        }).join('');
    }

    /* Entity detection */
    function getMenuConfig(e) {
        const el = e.target;

        if (el.closest('.af-blockly-host') && typeof window.afBuildBlockContextMenu === 'function') {
            return window.afBuildBlockContextMenu(el);
        }

        if (el.id === 'netCanvas' && typeof _netGraph !== 'undefined' && _netGraph) {
            const rect = el.getBoundingClientRect();
            const wx = (e.clientX - rect.left - _netGraph.tx) / _netGraph.scale;
            const wy = (e.clientY - rect.top - _netGraph.ty) / _netGraph.scale;
            const hit = _netGraph._hitTest(wx, wy);
            if (hit >= 0) {
                const nd = _netGraph.nodes[hit];
                if (nd?.id) return buildFriendItems(nd.id);
            }
            return null;
        }

        if (el.closest('.nav-btn[data-nav="dashboard"]')) {
            return [
                { icon: 'dashboard_customize', label: cm('dash_layout', 'Edit Dashboard'), action: () => openDashLayoutEditor() },
                { icon: 'tune', label: cm('nav_edit', 'Edit Navigation'), action: () => openNavEditor() },
            ];
        }

        if (el.closest('#vrcProfileArea') && (typeof currentVrcUser !== 'undefined') && currentVrcUser) {
            return buildSelfItems();
        }

        if (el.closest('#modalFriendDetail, #modalMyProfile, #modalDetail, #modalWorldDetail, #modalAvatarDetail')) {
            // .fd-banner = classic profile banner / other modals
            // .fd-left-banner = compact profile banner inside the left sidebar
            const bannerDiv = el.closest('.fd-banner, .fd-left-banner');
            if (bannerDiv) {
                const img = bannerDiv.querySelector('img');
                if (img?.src) return buildModalImageItems(img.src);
            }
            if (el.tagName === 'IMG' && el.classList.contains('fd-avatar') && el.src) {
                return buildModalImageItems(el.src);
            }
        }

        // Photo detail modal — custom subset (no Hide)
        const photoPane = el.closest('#photoDetailModal .photo-detail-img-pane');
        if (photoPane) {
            const path = photoPane.dataset.path || '';
            const url  = photoPane.dataset.url  || '';
            const type = photoPane.dataset.type || 'image';
            const name = photoPane.dataset.name || '';
            if (path) return buildPhotoDetailItems(path, url, type, name);
        }

        const libCard = el.closest('.lib-card, .dash-photo-item');
        if (libCard) {
            const path = libCard.dataset.path || '';
            const url = libCard.dataset.url || '';
            const type = libCard.dataset.type || 'image';
            const name = libCard.dataset.name || '';
            if (path) return buildLibCardItems(path, url, type, name);
        }

        const groupCard = el.closest('#myGroupsGrid .vrcn-content-card, #dashGroupActivityGrid .dash-group-card, #searchGroupsResults .vrcn-content-card, .fd-group-card');
        if (groupCard) {
            const id = extractGroupId(groupCard);
            if (id) return buildGroupItems(id);
        }

        const myInstCard = el.closest('#dashMyInstances .vrcn-content-card');
        if (myInstCard) {
            const loc = myInstCard.dataset.location;
            if (loc) return buildMyInstanceItems(loc);
        }

        const dashWorld = el.closest('#dashFavWorlds .vrcn-content-card, #dashDiscoveryGrid .vrcn-content-card, #dashFavWorldsShelf .vrcn-content-card, #dashRecentlyVisitedShelf .vrcn-content-card, #dashPopularWorldsShelf .vrcn-content-card, #dashActiveWorldsShelf .vrcn-content-card, #dashFriendLocSmallShelf .dash-flocs-card');
        if (dashWorld) {
            const id = extractWorldId(dashWorld)
                || extractId(dashWorld, /openFriendLocationDetail\('([^']+)'/);
            if (id) return buildWorldItems(id);
        }

        const worldCard = el.closest('#favWorldsGrid .vrcn-content-card, #worldSearchArea .vrcn-content-card, #worldMineGrid .vrcn-content-card, #fdContentWorlds .vrcn-content-card, #fdTabFavs .vrcn-world-card-small');
        if (worldCard) {
            const id = extractWorldId(worldCard);
            if (id) return buildWorldItems(id);
        }

        const miniWorld = el.closest('#fdContentWorlds .vrcn-mini-content');
        if (miniWorld) {
            const id = miniWorld.dataset.worldId;
            if (id) return buildWorldItems(id);
        }

        const avatarCard = el.closest('.av-card');
        if (avatarCard) {
            const id = (avatarCard.getAttribute('onclick') || '').match(/selectAvatar\('([^']+)'\)/)?.[1] || avatarCard.dataset.avid;
            if (id) return buildAvatarItems(id);
        }

        const miniAvatar = el.closest('#fdContentAvatars .vrcn-mini-content');
        if (miniAvatar) {
            const id = miniAvatar.dataset.avatarId;
            if (id) return buildAvatarItems(id);
        }

        const bannedCard = el.closest('#gdTabBanned .vrcn-user-item');
        if (bannedCard && window._currentGroupDetail?.canBan) {
            const id = extractFriendId(bannedCard);
            if (id) {
                return [
                    {
                        icon: 'lock_open',
                        label: cm('group.unban_member', 'Unban Member'),
                        action: () => sendToCS({ action: 'vrcUnbanGroupMember', groupId: window._currentGroupDetail.id, userId: id })
                    },
                    'sep',
                    ...buildFriendItems(id)
                ];
            }
        }

        const memberCard = el.closest('#gdTabMembers .vrcn-user-item, #gdTabRoles .vrcn-user-item');
        if (memberCard && window._currentGroupDetail) {
            const id = extractFriendId(memberCard);
            if (id) {
                const memberRoleIds = (window._gdMemberRoleIds && window._gdMemberRoleIds[id]) || [];
                const avatarThumb = memberCard.dataset.avatarThumb || '';
                return buildGroupMemberItems(id, window._currentGroupDetail, memberRoleIds, avatarThumb);
            }
        }

        const friendCard = el.closest('.vrc-friend-card, .vrcn-user-item, .inst-user-row, .iim-user-item, .dash-feed-card');
        if (friendCard) {
            const id = extractFriendId(friendCard);
            if (id) {
                const items = buildFriendItems(id, friendCard);
                const isMutualCard = !!friendCard.closest('#fdMutualsGrid');
                const avatarThumb = isMutualCard ? (friendCard.dataset.avatarThumb || '') : '';
                const fileId = avatarThumb.match(/file_[a-f0-9-]{36}/i)?.[0] || '';
                const checkAction = fileId
                    ? () => sendToCS({ action: 'vrcLookupAvatarByFileId', fileId, userId: id, openModal: true })
                    : () => ctxCheckAvatar(id);
                items.unshift('sep');
                items.unshift({ icon: 'checkroom', label: cm('check_for_avatar', 'Check for Avatar'), action: checkAction });
                return items;
            }
        }

        const instanceCard = el.closest('#vrcInstanceArea .inst-card');
        if (instanceCard) {
            const wid = (typeof currentInstanceData !== 'undefined') && currentInstanceData?.worldId;
            if (wid && !currentInstanceData.empty && !currentInstanceData.error) return buildWorldItems(wid);
        }

        if (el.closest('#sidebarEl')) {
            return [{ icon: 'tune', label: cm('nav_edit', 'Edit Navigation'), action: () => openNavEditor() }];
        }

        return null;
    }

    function extractId(el, pattern) {
        return (el.getAttribute('onclick') || '').match(pattern)?.[1] || null;
    }

    function extractFriendId(el) {
        const onclick = el.getAttribute('onclick') || '';
        return onclick.match(/openFriendDetail\('([^']+)'\)/)?.[1]
            || onclick.match(/navOpenModal\('friend','([^']+)'/)?.[1]
            || el.dataset.uid
            || null;
    }

    function extractWorldId(el) {
        const onclick = el.getAttribute('onclick') || '';
        return onclick.match(/openWorld(?:Search)?Detail\('([^']+)'\)/)?.[1]
            || onclick.match(/navOpenModal\('world[^']*','([^']+)'/)?.[1]
            || el.dataset.wid
            || null;
    }

    function extractGroupId(el) {
        const onclick = el.getAttribute('onclick') || '';
        return onclick.match(/openGroupDetail\('([^']+)'\)/)?.[1]
            || onclick.match(/navOpenModal\('group','([^']+)'/)?.[1]
            || null;
    }

    /* Menu item builders */
    function buildGroupItems(id) {
        const g = (typeof myGroups !== 'undefined') && myGroups.find(x => x.id === id);
        const isJoined = !!g;

        if (!isJoined) {
            return [
                { icon: 'open_in_new', label: cm('group.open_details', 'Open Details'), action: () => navOpenModal('group', id, '') },
                { icon: 'share', label: cm('group.share', 'Share Group'), action: () => copyWithToast('https://vrchat.com/home/group/' + id, 'group.share_copied', 'Group link copied to clipboard') },
                'sep',
                { icon: 'group_add', label: cm('group.join', 'Join Group'), action: () => sendToCS({ action: 'vrcJoinGroup', groupId: id }) },
            ];
        }

        const canPost = g.canPost === true;
        const canEvent = g.canEvent === true;
        const isRep = g.isRepresenting === true;
        const curVis = g.visibility || 'visible';
        const items = [
            { icon: 'open_in_new', label: cm('group.open_details', 'Open Details'), action: () => navOpenModal('group', id, g.name || '') },
            { icon: 'share', label: cm('group.share', 'Share Group'), action: () => copyWithToast('https://vrchat.com/home/group/' + id, 'group.share_copied', 'Group link copied to clipboard') },
            'sep',
        ];
        if (canPost) items.push({ icon: 'edit_note', label: cm('group.post', 'Post'), action: () => openGroupPostModal(id) });
        if (canEvent) items.push({ icon: 'event', label: cm('group.events', 'Events'), action: () => openGroupEventModal(id) });
        if (canPost || canEvent) items.push('sep');
        items.push({ icon: 'shield_person', label: cm('group.represent', 'Represent this group'), action: () => sendToCS({ action: 'vrcRepresentGroup', groupId: id }), disabled: isRep });
        items.push({ icon: 'visibility', label: cm('group.visibility', 'Visibility'), submenuFn: btn => showGroupVisibilitySubmenu(id, curVis, btn) });
        items.push('sep');
        items.push({ icon: 'logout', label: cm('group.leave', 'Leave Group'), action: () => sendToCS({ action: 'vrcLeaveGroup', groupId: id }), danger: true, confirm: true });
        return items;
    }

    function showGroupVisibilitySubmenu(groupId, currentVis, parentBtn) {
        const opts = [
            { val: 'visible', icon: 'public',         key: 'groups.visibility.visible', fb: 'Visible for Everyone' },
            { val: 'friends', icon: 'people',          key: 'groups.visibility.friends', fb: 'Visible for Friends'  },
            { val: 'hidden',  icon: 'visibility_off',  key: 'groups.visibility.hidden',  fb: 'Visible for None'     },
        ];
        submenu.innerHTML = opts.map(o => {
            const active = currentVis === o.val;
            return `<button class="vn-ctx-item" data-vis="${esc(o.val)}" data-gid="${esc(groupId)}">
                <span class="msi" style="font-size:14px;">${o.icon}</span>
                <span class="vn-ctx-label">${esc(typeof t === 'function' ? t(o.key, o.fb) : o.fb)}</span>
                ${active ? '<span class="msi vn-ctx-check">check</span>' : ''}
            </button>`;
        }).join('');
        submenu.querySelectorAll('[data-vis]').forEach(btn => {
            btn.addEventListener('click', e => {
                e.stopPropagation();
                if (typeof setGroupVisibility === 'function') setGroupVisibility(btn.dataset.gid, btn.dataset.vis);
                hideMenu();
            });
            btn.addEventListener('mouseenter', () => clearTimeout(submenuTimer));
        });
        positionSubmenu(parentBtn);
    }

    function showGroupInviteForUserSubmenu(userId, groups, parentBtn) {
        submenu.innerHTML = groups.map(g => `
            <button class="vn-ctx-item" data-gid="${esc(g.id)}" data-uid="${esc(userId)}">
                <span class="msi" style="font-size:14px;">group</span>
                <span class="vn-ctx-label">${esc(g.name || g.id)}</span>
            </button>`).join('');
        submenu.querySelectorAll('[data-gid]').forEach(btn => {
            btn.addEventListener('click', e => {
                e.stopPropagation();
                const gid = btn.dataset.gid;
                const uid = btn.dataset.uid;
                sendToCS({ action: 'vrcInviteToGroup', groupId: gid, userIds: [uid] });
                showToast(true, cm('friend.invite_group_sent', 'Invite sent!'));
                hideMenu();
            });
            btn.addEventListener('mouseenter', () => clearTimeout(submenuTimer));
        });
        positionSubmenu(parentBtn);
    }

    if (!window._friendAlertCache) window._friendAlertCache = {};
    const _friendAlertCache = window._friendAlertCache;

    function showToastAlertSubmenu(userId, parentBtn) {
        const currentLevel = _friendAlertCache[userId] ?? 0;
        const opts = [
            { icon: 'notifications_active', label: cm('friend.toast_always', 'Always notify'), level: 1  },
            { icon: 'notifications_off',    label: cm('friend.toast_never',  'Never notify'),  level: -1 },
            { icon: 'notifications',        label: cm('friend.toast_default', 'Default'),       level: 0  },
        ];

        function renderActive(level) {
            submenu.querySelectorAll('[data-tidx]').forEach(btn => {
                const active = parseInt(btn.dataset.level) === level;
                btn.classList.toggle('active', active);
                const existing = btn.querySelector('.fot-chk');
                if (existing) existing.remove();
                if (active) {
                    const chk = document.createElement('span');
                    chk.className = 'msi fot-chk';
                    chk.style.cssText = 'font-size:13px;margin-left:auto;opacity:.7;';
                    chk.textContent = 'check';
                    btn.appendChild(chk);
                }
            });
        }

        submenu.innerHTML = opts.map((o, i) => `
            <button class="vn-ctx-item${o.level === currentLevel ? ' active' : ''}" data-tidx="${i}" data-level="${o.level}">
                <span class="msi" style="font-size:14px;">${o.icon}</span>
                <span class="vn-ctx-label">${esc(o.label)}</span>
                ${o.level === currentLevel ? '<span class="msi fot-chk" style="font-size:13px;margin-left:auto;opacity:.7;">check</span>' : ''}
            </button>`).join('');
        submenu.querySelectorAll('[data-tidx]').forEach(btn => {
            btn.addEventListener('click', e => {
                e.stopPropagation();
                const level = parseInt(btn.dataset.level);
                _friendAlertCache[userId] = level;
                sendToCS({ action: 'vrcSetFriendAlert', userId, level });
                hideMenu();
            });
            btn.addEventListener('mouseenter', () => clearTimeout(submenuTimer));
        });

        window._pendingFotSubmenuUpdate = { userId, fn: renderActive };
        sendToCS({ action: 'vrcGetFriendAlert', userId });
        positionSubmenu(parentBtn);
    }

    function showModerateSubmenu(userId, parentBtn) {
        const isBlocked    = Array.isArray(blockedData)      && blockedData.some(x => x.targetUserId === userId);
        const isMuted      = Array.isArray(mutedData)        && mutedData.some(x => x.targetUserId === userId);
        const isChatMuted  = Array.isArray(muteChatData)     && muteChatData.some(x => x.targetUserId === userId);
        const isAvatarHid  = Array.isArray(hiddenAvatarData) && hiddenAvatarData.some(x => x.targetUserId === userId);
        const isInteractOff= Array.isArray(interactOffData)  && interactOffData.some(x => x.targetUserId === userId);
        const opts = [
            {
                icon: isBlocked ? 'lock_open' : 'block',
                label: isBlocked ? cm('friend.unblock', 'Unblock') : cm('friend.block', 'Block'),
                danger: !isBlocked,
                action: () => sendToCS({ action: isBlocked ? 'vrcUnblock' : 'vrcBlock', userId }),
            },
            {
                icon: isMuted ? 'mic' : 'mic_off',
                label: isMuted ? cm('friend.unmute', 'Unmute') : cm('friend.mute', 'Mute'),
                action: () => sendToCS({ action: isMuted ? 'vrcUnmute' : 'vrcMute', userId }),
            },
            {
                icon: isChatMuted ? 'chat' : 'comments_disabled',
                label: isChatMuted ? cm('friend.unmute_chat', 'Unmute Chat') : cm('friend.mute_chat', 'Mute Chat'),
                action: () => sendToCS({ action: isChatMuted ? 'vrcUnmuteChat' : 'vrcMuteChat', userId }),
            },
            {
                icon: isAvatarHid ? 'visibility' : 'visibility_off',
                label: isAvatarHid ? cm('friend.show_avatar', 'Show Avatar') : cm('friend.hide_avatar', 'Hide Avatar'),
                action: () => sendToCS({ action: isAvatarHid ? 'vrcShowAvatar' : 'vrcHideAvatar', userId }),
            },
            {
                icon: isInteractOff ? 'touch_app' : 'do_not_touch',
                label: isInteractOff ? cm('friend.interact_on', 'Turn On Interactions') : cm('friend.interact_off', 'Turn Off Interactions'),
                action: () => sendToCS({ action: isInteractOff ? 'vrcInteractOn' : 'vrcInteractOff', userId }),
            },
        ];
        submenu.innerHTML = opts.map((o, i) => `
            <button class="vn-ctx-item${o.danger ? ' danger' : ''}" data-midx="${i}">
                <span class="msi" style="font-size:14px;">${o.icon}</span>
                <span class="vn-ctx-label">${esc(o.label)}</span>
            </button>`).join('');
        submenu.querySelectorAll('[data-midx]').forEach((btn, i) => {
            btn.addEventListener('click', e => { e.stopPropagation(); opts[i].action(); hideMenu(); });
            btn.addEventListener('mouseenter', () => clearTimeout(submenuTimer));
        });
        positionSubmenu(parentBtn);
    }

    function showFriendInviteSubmenu(userId, displayName, hasVrcPlus, parentBtn) {
        const opts = [
            { icon: 'send',              key: 'friend.invite',         fb: 'Invite',              action: () => sendToCS({ action: 'vrcInviteFriend', userId }) },
            { icon: 'forward_to_inbox',  key: 'friend.invite_message', fb: 'Invite with Message', action: () => openFriendInviteModal(userId, displayName, 'message') },
        ];
        if (hasVrcPlus) opts.push({ icon: 'add_photo_alternate', key: 'friend.invite_image', fb: 'Invite with Image', action: () => openFriendInviteModal(userId, displayName, 'photo') });
        submenu.innerHTML = opts.map(o => `
            <button class="vn-ctx-item" data-opt="${esc(o.key)}">
                <span class="msi" style="font-size:14px;">${o.icon}</span>
                <span class="vn-ctx-label">${esc(cm(o.key, o.fb))}</span>
            </button>`).join('');
        submenu.querySelectorAll('[data-opt]').forEach((btn, i) => {
            btn.addEventListener('click', e => { e.stopPropagation(); opts[i].action(); hideMenu(); });
            btn.addEventListener('mouseenter', () => clearTimeout(submenuTimer));
        });
        positionSubmenu(parentBtn);
    }

    function buildGroupMemberItems(userId, grpCtx, memberRoleIds = [], avatarThumb = '') {
        const fileId = avatarThumb.match(/file_[a-f0-9-]{36}/i)?.[0] || '';
        const modItems = [];
        if (grpCtx.canKick) {
            modItems.push({
                icon: 'person_remove',
                label: cm('group.kick_member', 'Kick from group'),
                danger: true,
                confirm: true,
                action: () => sendToCS({ action: 'vrcKickGroupMember', groupId: grpCtx.id, userId })
            });
        }
        if (grpCtx.canBan) {
            modItems.push({
                icon: 'block',
                label: cm('group.ban_member', 'Ban from group'),
                danger: true,
                confirm: true,
                action: () => sendToCS({ action: 'vrcBanGroupMember', groupId: grpCtx.id, userId })
            });
        }
        if (grpCtx.canAssignRoles) {
            const assignable = (grpCtx.roles || []).filter(r => !(r.permissions || []).includes('*'));
            if (assignable.length > 0) {
                modItems.push({
                    icon: 'badge',
                    label: cm('group.assign_role', 'Assign Role'),
                    submenuFn: btn => showRoleAssignSubmenu(userId, grpCtx, memberRoleIds, btn)
                });
            }
        }
        const friendItems = buildFriendItems(userId);
        let items;
        if (modItems.length > 0) items = [...modItems, 'sep', ...friendItems];
        else items = friendItems;
        if (fileId) {
            items.unshift('sep');
            items.unshift({ icon: 'checkroom', label: cm('check_for_avatar', 'Check for Avatar'), action: () => sendToCS({ action: 'vrcLookupAvatarByFileId', fileId, userId, openModal: true }) });
        }
        return items;
    }

    function showRoleAssignSubmenu(userId, grpCtx, memberRoleIds, parentBtn) {
        const roles = (grpCtx.roles || []).filter(r => !(r.permissions || []).includes('*'));
        submenu.innerHTML = roles.map(r => {
            const hasRole = memberRoleIds.includes(r.id);
            return `<button class="vn-ctx-item" data-role-id="${esc(r.id)}" data-group-id="${esc(grpCtx.id)}" data-user-id="${esc(userId)}" data-has-role="${hasRole}">
                <span class="msi" style="font-size:14px;color:${hasRole ? 'var(--ok, #4caf50)' : 'inherit'};">${hasRole ? 'check_circle' : 'badge'}</span>
                <span class="vn-ctx-label">${esc(r.name)}</span>
            </button>`;
        }).join('');
        submenu.querySelectorAll('[data-role-id]').forEach(btn => {
            btn.addEventListener('click', e => {
                e.stopPropagation();
                const action = btn.dataset.hasRole === 'true' ? 'vrcRemoveGroupMemberRole' : 'vrcAddGroupMemberRole';
                sendToCS({ action, groupId: btn.dataset.groupId, userId: btn.dataset.userId, roleId: btn.dataset.roleId });
                hideMenu();
            });
            btn.addEventListener('mouseenter', () => clearTimeout(submenuTimer));
        });
        positionSubmenu(parentBtn);
    }

    function showAvFavGroupSubmenu(avatarId, parentBtn) {
        const groups = (typeof favAvatarGroups !== 'undefined') ? favAvatarGroups : [];
        if (groups.length === 0) {
            submenu.innerHTML = `<div class="vn-ctx-loading"><span class="msi">hourglass_empty</span><span>${esc(cm('loading_groups', 'Loading groups...'))}</span></div>`;
            positionSubmenu(parentBtn);
            sendToCS({ action: 'vrcGetAvatars', filter: 'favorites' });
            let attempts = 0;
            const retry = setInterval(() => {
                const g = (typeof favAvatarGroups !== 'undefined') ? favAvatarGroups : [];
                if (g.length > 0 || ++attempts > 15) {
                    clearInterval(retry);
                    if (g.length > 0 && submenu.style.display !== 'none') showAvFavGroupSubmenu(avatarId, parentBtn);
                }
            }, 300);
            return;
        } else {
            submenu.innerHTML = groups.map(g => {
                const count = (typeof favAvatarsData !== 'undefined') ? favAvatarsData.filter(a => a.favoriteGroup === g.name).length : 0;
                const isVrcPlus = g.name !== 'avatars1';
                return `<button class="vn-ctx-item" data-av-fav-name="${esc(g.name)}" data-av-fav-type="${esc(g.type)}" data-avid="${esc(avatarId)}">
                    <span class="msi" style="font-size:14px;">bookmark_border</span>
                    <span class="vn-ctx-label">${esc(g.displayName || g.name)}</span>
                    ${isVrcPlus ? '<span class="vrcn-supporter-badge">VRC+</span>' : ''}
                    <span class="vn-ctx-count">${count}</span>
                </button>`;
            }).join('');
            submenu.querySelectorAll('[data-av-fav-name]').forEach(btn => {
                btn.addEventListener('click', e => {
                    e.stopPropagation();
                    sendToCS({ action: 'vrcAddAvatarFavorite', avatarId: btn.dataset.avid, groupName: btn.dataset.avFavName, groupType: btn.dataset.avFavType, oldFvrtId: '' });
                    hideMenu();
                });
                btn.addEventListener('mouseenter', () => clearTimeout(submenuTimer));
            });
        }
        positionSubmenu(parentBtn);
    }

    function showAvMoveToGroupSubmenu(avatarId, favEntry, parentBtn) {
        const groups = (typeof favAvatarGroups !== 'undefined') ? favAvatarGroups : [];
        submenu.innerHTML = groups.map(g => {
            const isCurrent = g.name === favEntry.favoriteGroup;
            const count = (typeof favAvatarsData !== 'undefined') ? favAvatarsData.filter(a => a.favoriteGroup === g.name).length : 0;
            const iconEl = isCurrent
                ? `<span class="msi" style="font-size:14px;color:var(--accent);">check_circle</span>`
                : `<span class="msi" style="font-size:14px;">drive_file_move</span>`;
            const isVrcPlus = g.name !== 'avatars1';
            return `<button class="vn-ctx-item${isCurrent ? ' ci-group-selected' : ''}"
                data-av-move-name="${esc(g.name)}" data-av-move-type="${esc(g.type)}" data-avid="${esc(avatarId)}" data-old-fvrt="${esc(favEntry.favoriteId)}" data-is-current="${isCurrent}">
                ${iconEl}
                <span class="vn-ctx-label">${esc(g.displayName || g.name)}</span>
                ${isVrcPlus ? '<span class="vrcn-supporter-badge">VRC+</span>' : ''}
                <span class="vn-ctx-count">${count}</span>
            </button>`;
        }).join('');
        submenu.querySelectorAll('[data-av-move-name]').forEach(btn => {
            btn.addEventListener('click', e => {
                e.stopPropagation();
                if (btn.dataset.isCurrent === 'true') { hideMenu(); return; }
                sendToCS({ action: 'vrcAddAvatarFavorite', avatarId: btn.dataset.avid, groupName: btn.dataset.avMoveName, groupType: btn.dataset.avMoveType, oldFvrtId: btn.dataset.oldFvrt });
                hideMenu();
            });
            btn.addEventListener('mouseenter', () => clearTimeout(submenuTimer));
        });
        positionSubmenu(parentBtn);
    }

    function showAvEditModeGroupSubmenu(parentBtn) {
        const groups = (typeof favAvatarGroups !== 'undefined') ? favAvatarGroups : [];
        submenu.innerHTML = groups.map(g => {
            const count = (typeof favAvatarsData !== 'undefined') ? favAvatarsData.filter(a => a.favoriteGroup === g.name).length : 0;
            const isVrcPlus = g.name !== 'avatars1';
            return `<button class="vn-ctx-item" data-av-edit-move-name="${esc(g.name)}" data-av-edit-move-type="${esc(g.type)}">
                <span class="msi" style="font-size:14px;">folder</span>
                <span class="vn-ctx-label">${esc(g.displayName || g.name)}</span>
                ${isVrcPlus ? '<span class="vrcn-supporter-badge">VRC+</span>' : ''}
                <span class="vn-ctx-count">${count}</span>
            </button>`;
        }).join('');
        submenu.querySelectorAll('[data-av-edit-move-name]').forEach(btn => {
            btn.addEventListener('click', e => {
                e.stopPropagation();
                avEditMoveSelected(btn.dataset.avEditMoveName, btn.dataset.avEditMoveType);
                hideMenu();
            });
            btn.addEventListener('mouseenter', () => clearTimeout(submenuTimer));
        });
        positionSubmenu(parentBtn);
    }

    function showAvLocalMoveToGroupSubmenu(avatarId, parentBtn) {
        const groups = (typeof _localFavAvatarGroups !== 'undefined') ? _localFavAvatarGroups : [];
        const currentGroupIds = (typeof _localFavAvatarsData !== 'undefined')
            ? _localFavAvatarsData.filter(a => a.id === avatarId).map(a => a.groupId)
            : [];

        function renderLocalGroupSubmenu() {
            const g = (typeof _localFavAvatarGroups !== 'undefined') ? _localFavAvatarGroups : [];
            const cIds = (typeof _localFavAvatarsData !== 'undefined')
                ? _localFavAvatarsData.filter(a => a.id === avatarId).map(a => a.groupId)
                : [];
            submenu.innerHTML = g.map(gr => {
                const isInGroup = cIds.includes(gr.id);
                const iconEl = isInGroup
                    ? `<span class="msi" style="font-size:14px;color:var(--accent);">check_circle</span>`
                    : `<span class="msi" style="font-size:14px;">folder</span>`;
                return `<button class="vn-ctx-item${isInGroup ? ' ci-group-selected' : ''}"
                    data-local-group-id="${gr.id}" data-avid="${esc(avatarId)}" data-is-in-group="${isInGroup}">
                    ${iconEl}
                    <span class="vn-ctx-label">${esc(gr.name)}</span>
                </button>`;
            }).join('') + `<button class="vn-ctx-item" data-local-create-group="true" data-avid="${esc(avatarId)}">
                <span class="msi" style="font-size:14px;">add</span>
                <span class="vn-ctx-label">${cm('avatar.create_local_group', 'Create New Group...')}</span>
            </button>`;
            submenu.querySelectorAll('[data-local-group-id]').forEach(btn => {
                btn.addEventListener('click', e => {
                    e.stopPropagation();
                    const groupId = parseInt(btn.dataset.localGroupId);
                    const isInGroup = btn.dataset.isInGroup === 'true';
                    if (isInGroup) {
                        sendToCS({ action: 'localFavRemoveItem', groupId, itemId: btn.dataset.avid, itemType: 'avatar' });
                    } else {
                        sendToCS({ action: 'localFavAddItem', groupId, itemId: btn.dataset.avid, itemType: 'avatar' });
                    }
                    hideMenu();
                });
                btn.addEventListener('mouseenter', () => clearTimeout(submenuTimer));
            });
            submenu.querySelectorAll('[data-local-create-group]').forEach(btn => {
                btn.addEventListener('click', e => {
                    e.stopPropagation();
                    hideMenu();
                    if (typeof showAvatarLocalFavCreateGroupDialog === 'function') {
                        showAvatarLocalFavCreateGroupDialog(btn.dataset.avid);
                    }
                });
                btn.addEventListener('mouseenter', () => clearTimeout(submenuTimer));
            });
            positionSubmenu(parentBtn);
        }

        // If groups not loaded yet, fetch them first
        if (groups.length === 0) {
            submenu.innerHTML = `<div class="vn-ctx-item" style="opacity:0.5;">${cm('common.loading', 'Loading...')}</div>`;
            positionSubmenu(parentBtn);
            const originalHandler = window.handleAvatarLocalFavGroups;
            window.handleAvatarLocalFavGroups = function(payload) {
                if (payload && payload.favType === 'avatar') {
                    _localFavAvatarGroups = payload.groups || [];
                    if (typeof renderAvatarLocalFavItems === 'function') {
                        renderAvatarLocalFavItems({ groups: payload.groups, entries: undefined });
                    }
                }
                window.handleAvatarLocalFavGroups = originalHandler;
                renderLocalGroupSubmenu();
            };
            sendToCS({ action: 'localFavGetGroups', favType: 'avatar' });
        } else {
            renderLocalGroupSubmenu();
        }
    }

    function showAvLocalEditModeGroupSubmenu(parentBtn) {
        const groups = (typeof _localFavAvatarGroups !== 'undefined') ? _localFavAvatarGroups : [];

        function renderLocalEditGroupSubmenu() {
            const g = (typeof _localFavAvatarGroups !== 'undefined') ? _localFavAvatarGroups : [];
            submenu.innerHTML = g.map(gr => {
                return `<button class="vn-ctx-item" data-local-edit-group-id="${gr.id}">
                    <span class="msi" style="font-size:14px;">folder</span>
                    <span class="vn-ctx-label">${esc(gr.name)}</span>
                </button>`;
            }).join('') + `<button class="vn-ctx-item" data-local-edit-create-group="true">
                <span class="msi" style="font-size:14px;">add</span>
                <span class="vn-ctx-label">${cm('avatar.create_local_group', 'Create New Group...')}</span>
            </button>`;
            submenu.querySelectorAll('[data-local-edit-group-id]').forEach(btn => {
                btn.addEventListener('click', e => {
                    e.stopPropagation();
                    const groupId = parseInt(btn.dataset.localEditGroupId);
                    if (typeof avLocalEditMoveSelected === 'function') {
                        avLocalEditMoveSelected(groupId);
                    }
                    hideMenu();
                });
                btn.addEventListener('mouseenter', () => clearTimeout(submenuTimer));
            });
            submenu.querySelectorAll('[data-local-edit-create-group]').forEach(btn => {
                btn.addEventListener('click', e => {
                    e.stopPropagation();
                    hideMenu();
                    if (typeof showAvatarLocalFavCreateGroupDialog === 'function') {
                        showAvatarLocalFavCreateGroupDialog();
                    }
                });
                btn.addEventListener('mouseenter', () => clearTimeout(submenuTimer));
            });
            positionSubmenu(parentBtn);
        }

        // If groups not loaded yet, fetch them first
        if (groups.length === 0) {
            submenu.innerHTML = `<div class="vn-ctx-item" style="opacity:0.5;">${cm('common.loading', 'Loading...')}</div>`;
            positionSubmenu(parentBtn);
            const originalHandler = window.handleAvatarLocalFavGroups;
            window.handleAvatarLocalFavGroups = function(payload) {
                if (payload && payload.favType === 'avatar') {
                    _localFavAvatarGroups = payload.groups || [];
                    if (typeof renderAvatarLocalFavItems === 'function') {
                        renderAvatarLocalFavItems({ groups: payload.groups, entries: undefined });
                    }
                }
                window.handleAvatarLocalFavGroups = originalHandler;
                renderLocalEditGroupSubmenu();
            };
            sendToCS({ action: 'localFavGetGroups', favType: 'avatar' });
        } else {
            renderLocalEditGroupSubmenu();
        }
    }

    function buildAvatarItems(id) {
        // Edit mode: auto-select right-clicked avatar and show only batch-action items
        if (typeof _avEditMode !== 'undefined' && _avEditMode) {
            if (!_avEditSelected.has(id)) {
                _avEditSelected.add(id);
                if (typeof filterFavAvatars === 'function') filterFavAvatars();
                else if (typeof updateAvEditBar === 'function') updateAvEditBar();
            }
            return [
                { icon: 'drive_file_move', label: cm('avatar.move_to_category', 'Move to Category'), submenuFn: btn => showAvEditModeGroupSubmenu(btn) },
                { icon: 'folder', label: cm('avatar.move_to_local_group', 'Move to Local Group'), submenuFn: btn => showAvLocalEditModeGroupSubmenu(btn) },
                { icon: 'star_border', label: cm('avatar.remove_favorites', 'Remove from Favorites'), action: () => avEditRemoveSelected(), danger: true, confirm: true },
            ];
        }

        const favEntry = (typeof favAvatarsData !== 'undefined') && favAvatarsData.find(a => a.id === id);
        const items = [
            { icon: 'info', label: cm('avatar.show', 'Show Avatar'), action: () => navOpenModal('avatar', id, '') },
            { icon: 'share', label: cm('avatar.share', 'Share Avatar'), action: () => copyWithToast('https://vrchat.com/home/avatar/' + id, 'avatar.share_copied', 'Avatar link copied to clipboard') },
            { icon: 'checkroom', label: cm('avatar.use', 'Use Avatar'), action: () => sendToCS({ action: 'vrcSelectAvatar', avatarId: id }) },
            'sep',
            { icon: 'style', label: cm('avatar.similar', 'Similar Avatars'), action: () => { showTab(4); setAvatarFilter('search'); setTimeout(() => { const inp = document.getElementById('avatarSearchInput'); if (inp) { inp.value = 'similar: ' + id; doAvatarSearch(); } }, 100); } },
            'sep',
        ];
        if (favEntry) {
            items.push({ icon: 'star_border', label: cm('avatar.remove_favorites', 'Remove from Favorites'), action: () => removeAvatarFavorite(id, favEntry.favoriteId) });
            items.push({ icon: 'drive_file_move', label: cm('avatar.move_to_category', 'Move to Category'), submenuFn: btn => showAvMoveToGroupSubmenu(id, favEntry, btn) });
        } else {
            items.push({ icon: 'star', label: cm('avatar.add_favorites', 'Add to Favorites'), submenuFn: btn => showAvFavGroupSubmenu(id, btn) });
        }
        items.push({ icon: 'folder', label: cm('avatar.move_to_local_group', 'Move to Local Group'), submenuFn: btn => showAvLocalMoveToGroupSubmenu(id, btn) });
        return items;
    }

    function buildMyInstanceItems(loc) {
        const inst = (typeof _myInstancesData !== 'undefined') && _myInstancesData.find(i => i.location === loc);
        const worldId = inst?.worldId || '';
        const wn = inst?.worldName || '';
        const wt = inst?.worldThumb || '';
        const it = inst?.instanceType || '';
        const favEntry = (typeof favWorldsData !== 'undefined') && favWorldsData.find(fw => fw.id === worldId);
        const items = [];
        if (loc) {
            items.push({ icon: 'person_add', label: cm('instance.invite_friends', 'Invite Friends'), action: () => openInviteModalForLocation(loc, wn, wt, it) });
            items.push({ icon: 'close', label: cm('instance.close', 'Close Instance'), action: () => removeMyInstance(loc), danger: true, confirm: true });
            items.push('sep');
        }
        items.push({ icon: 'open_in_new', label: cm('world.open_details', 'Open Details'), action: () => navOpenModal('worldSearch', worldId, wn) });
        items.push({ icon: 'add_circle_outline', label: cm('world.create_instance', 'Create Instance'), action: () => createWorldInstance(worldId) });
        items.push({ icon: 'share', label: cm('world.share', 'Share World'), action: () => copyWithToast('https://vrchat.com/home/world/' + worldId, 'world.share_copied', 'World link copied to clipboard') });
        items.push({ icon: 'home', label: cm('world.set_home', 'Set as Home'), action: () => sendToCS({ action: 'vrcSetHomeWorld', worldId }), confirm: true });
        items.push('sep');
        if (favEntry) {
            items.push({ icon: 'star_border', label: cm('world.remove_favorites', 'Remove from Favorites'), action: () => removeWorldFavorite(worldId, favEntry.favoriteId) });
            const otherGroups = (typeof favWorldGroups !== 'undefined') ? favWorldGroups.filter(g => g.name !== favEntry.favoriteGroup) : [];
            if (otherGroups.length > 0) {
                items.push({ icon: 'drive_file_move', label: cm('world.move_to_category', 'Move to Category'), submenuFn: btn => showMoveToGroupSubmenu(worldId, favEntry, btn) });
            }
        } else {
            items.push({ icon: 'star', label: cm('world.add_favorites', 'Add to Favorites'), submenuFn: btn => showFavGroupSubmenu(worldId, btn) });
        }
        return items;
    }

    function showEditModeGroupSubmenu(parentBtn) {
        const groups = (typeof favWorldGroups !== 'undefined') ? favWorldGroups : [];
        submenu.innerHTML = groups.map(g => {
            const count = (typeof favWorldsData !== 'undefined')
                ? favWorldsData.filter(fw => fw.favoriteGroup === g.name).length
                : 0;
            const isVrcPlus = g.type === 'vrcPlusWorld';
            return `<button class="vn-ctx-item"
                data-edit-move-name="${esc(g.name)}" data-edit-move-type="${esc(g.type)}">
                <span class="msi" style="font-size:14px;">folder</span>
                <span class="vn-ctx-label">${esc(g.displayName || g.name)}</span>
                ${isVrcPlus ? '<span class="vrcn-supporter-badge">VRC+</span>' : ''}
                <span class="vn-ctx-count">${count}</span>
            </button>`;
        }).join('');
        submenu.querySelectorAll('[data-edit-move-name]').forEach(btn => {
            btn.addEventListener('click', e => {
                e.stopPropagation();
                worldEditMoveSelected(btn.dataset.editMoveName, btn.dataset.editMoveType);
                hideMenu();
            });
            btn.addEventListener('mouseenter', () => clearTimeout(submenuTimer));
        });
        positionSubmenu(parentBtn);
    }

    function buildWorldItems(id) {
        // Edit mode: auto-select right-clicked world and show only batch-action items
        if (typeof _worldEditMode !== 'undefined' && _worldEditMode) {
            if (!_worldEditSelected.has(id)) {
                _worldEditSelected.add(id);
                if (typeof filterFavWorlds === 'function') filterFavWorlds();
                else if (typeof updateWorldEditBar === 'function') updateWorldEditBar();
            }
            return [
                { icon: 'drive_file_move', label: cm('world.move_to_category', 'Move to Category'), submenuFn: btn => showEditModeGroupSubmenu(btn) },
                { icon: 'star_border', label: cm('world.remove_favorites', 'Remove from Favorites'), action: () => worldEditRemoveSelected(), danger: true, confirm: true },
            ];
        }

        const favEntry = (typeof favWorldsData !== 'undefined') && favWorldsData.find(fw => fw.id === id);
        const items = [
            { icon: 'open_in_new', label: cm('world.open_details', 'Open Details'), action: () => navOpenModal('worldSearch', id, '') },
            { icon: 'add_circle_outline', label: cm('world.create_instance', 'Create Instance'), action: () => createWorldInstance(id) },
            { icon: 'share', label: cm('world.share', 'Share World'), action: () => copyWithToast('https://vrchat.com/home/world/' + id, 'world.share_copied', 'World link copied to clipboard') },
            { icon: 'home', label: cm('world.set_home', 'Set as Home'), action: () => sendToCS({ action: 'vrcSetHomeWorld', worldId: id }), confirm: true },
            'sep',
        ];
        if (favEntry) {
            items.push({ icon: 'star_border', label: cm('world.remove_favorites', 'Remove from Favorites'), action: () => removeWorldFavorite(id, favEntry.favoriteId) });
            const otherGroups = (typeof favWorldGroups !== 'undefined') ? favWorldGroups.filter(g => g.name !== favEntry.favoriteGroup) : [];
            if (otherGroups.length > 0) {
                items.push({ icon: 'drive_file_move', label: cm('world.move_to_category', 'Move to Category'), submenuFn: btn => showMoveToGroupSubmenu(id, favEntry, btn) });
            }
        } else {
            items.push({ icon: 'star', label: cm('world.add_favorites', 'Add to Favorites'), submenuFn: btn => showFavGroupSubmenu(id, btn) });
        }
        return items;
    }

    function buildFriendItems(id, sourceEl) {
        if (typeof _favFriendEditMode !== 'undefined' && _favFriendEditMode) {
            if (!_favFriendEditSelected.has(id)) {
                _favFriendEditSelected.add(id);
                if (typeof filterFavFriends === 'function') filterFavFriends();
                else if (typeof updateFriendEditBar === 'function') updateFriendEditBar();
            }
            return [
                { icon: 'drive_file_move', label: cm('friend.move_group', 'Move to Group'), submenuFn: btn => showFriendEditModeGroupSubmenu(btn) },
                { icon: 'star_border', label: cm('friend.remove_favorites', 'Remove from Favorites'), action: () => friendEditRemoveSelected(), danger: true, confirm: true },
            ];
        }

        const f = (typeof vrcFriendsData !== 'undefined') && vrcFriendsData.find(x => x.id === id);
        const items = [
            { icon: 'person', label: cm('friend.view_profile', 'View Profile'), action: () => navOpenModal('friend', id, f?.displayName || '') },
            { icon: 'share', label: cm('friend.share_profile', 'Share Profile'), action: () => copyWithToast('https://vrchat.com/home/user/' + id, 'friend.share_copied', 'Profile link copied to clipboard') },
        ];
        if (f) {
            const loc = f.location || '';
            const { instanceType } = parseFriendLocation(loc);
            const isInWorld = loc && loc !== 'offline' && loc !== 'private' && loc !== 'traveling';
            const joinable = ['public', 'friends', 'friends+', 'hidden', 'group-public', 'group-plus', 'group-members', 'group'];
            const canJoin = isInWorld && joinable.includes(instanceType);
            const canRequestInvite = instanceType === 'private' || instanceType === 'invite_plus';
            const myInInstance = (typeof currentInstanceData !== 'undefined')
                && currentInstanceData && currentInstanceData.location
                && !currentInstanceData.empty && !currentInstanceData.error;

            const actionItems = [];
            if (canJoin) actionItems.push({ icon: 'login', label: cm('friend.join', 'Join'), action: () => friendAction('join', loc, id) });
            if (canRequestInvite) actionItems.push({ icon: 'mail', label: cm('friend.request_invite', 'Request Invite'), action: () => friendAction('requestInvite', loc, id) });
            if (myInInstance) {
                const hasVrcPlus = Array.isArray(currentVrcUser?.tags) && currentVrcUser.tags.includes('system_supporter');
                actionItems.push({ icon: 'send', label: cm('friend.invite', 'Invite'), submenuFn: btn => showFriendInviteSubmenu(id, f.displayName || id, hasVrcPlus, btn) });
            }
            const invitableGroups = (typeof myGroups !== 'undefined') ? myGroups.filter(g => g.canInvite === true) : [];
            if (invitableGroups.length > 0) {
                actionItems.push({ icon: 'group_add', label: cm('friend.invite_group', 'Invite to Group'), submenuFn: btn => showGroupInviteForUserSubmenu(id, invitableGroups, btn) });
            }
            actionItems.push({ icon: 'waving_hand', label: cm('friend.boop', 'Boop!'), action: () => { if (typeof msgrRegisterBoopSent === 'function') msgrRegisterBoopSent(id); sendToCS({ action: 'vrcBoop', userId: id }); } });
            actionItems.push({ icon: 'chat', label: cm('friend.messenger', 'Messenger'), action: () => openMessenger(id, f.displayName || id, f.image || '', f.status || '', f.statusDescription || '') });
            if (actionItems.length) {
                items.push('sep');
                actionItems.forEach(i => items.push(i));
            }
        }

        if (f) {
            const isFav = Array.isArray(favFriendsData) && favFriendsData.some(x => x.favoriteId === id);
            const favEntry = isFav ? favFriendsData.find(x => x.favoriteId === id) : null;
            const onFavTab = !!sourceEl?.closest('#favFriendsGrid');
            items.push('sep');
            if (isFav) {
                items.push({ icon: 'star_border', label: cm('friend.unfavorite', 'Unfavorite'), action: () => sendToCS({ action: 'vrcRemoveFavoriteFriend', userId: id, fvrtId: favEntry?.fvrtId || '' }) });
                if (onFavTab) {
                    const otherGroups = (typeof favFriendGroups !== 'undefined') ? favFriendGroups.filter(g => g.name !== favEntry?.groupName) : [];
                    if (otherGroups.length > 0) {
                        items.push({ icon: 'drive_file_move', label: cm('friend.move_group', 'Move to Group'), submenuFn: btn => showFavFriendMoveSubmenu(id, favEntry, btn) });
                    }
                }
            } else {
                items.push({ icon: 'star', label: cm('friend.favorite', 'Add to Favorites'), submenuFn: btn => showFavFriendGroupSubmenu(id, btn) });
            }

            items.push('sep');
            if (settings?.friendOnlineToastEnabled) items.push({ icon: 'notifications', label: cm('friend.toast', 'Toast'), submenuFn: btn => showToastAlertSubmenu(id, btn) });
            items.push({ icon: 'shield_person', label: cm('friend.moderate', 'Moderate'), submenuFn: btn => showModerateSubmenu(id, btn) });
            items.push({ icon: 'person_remove', label: cm('friend.unfriend', 'Unfriend'), action: () => sendToCS({ action: 'vrcUnfriend', userId: id }), danger: true, confirm: true });
        } else {
            items.push('sep');
            items.push({ icon: 'person_add', label: cm('friend.send_request', 'Send Friend Request'), action: () => sendToCS({ action: 'vrcSendFriendRequest', userId: id }) });
            items.push('sep');
            items.push({ icon: 'shield_person', label: cm('friend.moderate', 'Moderate'), submenuFn: btn => showModerateSubmenu(id, btn) });
        }
        return items;
    }

    function buildLibCardItems(path, url, type, name) {
        const isFav = (typeof favorites !== 'undefined') && favorites.has(path);
        const isHidden = (typeof hiddenMedia !== 'undefined') && hiddenMedia.has(path);
        const items = [
            { icon: 'content_copy', label: cm('library.copy', 'Copy to Clipboard'), action: () => copyToClipboard(url, path, type) },
        ];
        if (type === 'image' || type === 'gif' || type === 'video') {
            items.push({ icon: 'wallpaper',  label: cm('library.set_background',  'Set as Background'),        action: () => setLibItemAsDashBg(path, url) });
        }
        if (type === 'image' || type === 'gif') {
            items.push({ icon: 'desktop_windows', label: cm('library.set_wallpaper', 'Set as Desktop Background'), action: () => sendToCS({ action: 'setDesktopBackground', path }) });
        }
        items.push({ icon: 'folder_open', label: cm('library.reveal_in_explorer', 'Reveal in Explorer'), action: () => sendToCS({ action: 'revealInExplorer', path }) });
        if (typeof relayOn !== 'undefined' && relayOn) {
            items.push({ icon: 'send', label: cm('library.send_to_webhook', 'Send to Webhook'), action: () => sendToCS({ action: 'manualPost', filePath: path }) });
        }
        items.push('sep');
        items.push(isFav
            ? { icon: 'star_border', label: cm('library.remove_favorite', 'Remove Favorite'), action: () => toggleFavorite(path) }
            : { icon: 'star', label: cm('library.favorite', 'Favorite'), action: () => toggleFavorite(path) }
        );
        items.push(isHidden
            ? { icon: 'visibility', label: cm('library.unhide', 'Unhide'), action: () => toggleHidden(path) }
            : { icon: 'visibility_off', label: cm('library.hide', 'Hide'), action: () => toggleHidden(path) }
        );
        items.push('sep');
        items.push({ icon: 'delete', label: cm('library.delete', 'Delete'), danger: true, action: () => showDeleteModal(path, name) });
        return items;
    }

    function buildPhotoDetailItems(path, url, type, name) {
        const isFav = (typeof favorites !== 'undefined') && favorites.has(path);
        const items = [
            { icon: 'content_copy', label: cm('library.copy', 'Copy to Clipboard'), action: () => copyToClipboard(url, path, type) },
        ];
        if (type === 'image' || type === 'gif' || type === 'video') {
            items.push({ icon: 'wallpaper',  label: cm('library.set_background',  'Set as Background'),        action: () => setLibItemAsDashBg(path, url) });
        }
        if (type === 'image' || type === 'gif') {
            items.push({ icon: 'desktop_windows', label: cm('library.set_wallpaper', 'Set as Desktop Background'), action: () => sendToCS({ action: 'setDesktopBackground', path }) });
        }
        items.push({ icon: 'folder_open', label: cm('library.reveal_in_explorer', 'Reveal in Explorer'), action: () => sendToCS({ action: 'revealInExplorer', path }) });
        items.push('sep');
        items.push(isFav
            ? { icon: 'star_border', label: cm('library.remove_favorite', 'Remove Favorite'), action: () => toggleFavorite(path) }
            : { icon: 'star',        label: cm('library.favorite',        'Favorite'),        action: () => toggleFavorite(path) }
        );
        items.push('sep');
        items.push({ icon: 'delete', label: cm('library.delete', 'Delete'), danger: true, action: () => showDeleteModal(path, name) });
        return items;
    }

    function buildModalImageItems(src) {
        return [
            { icon: 'download',     label: cm('image.download', 'Download Image'), action: () => sendToCS({ action: 'invDownload', url: src, fileName: 'image.png' }) },
            { icon: 'open_in_full', label: cm('image.inspect',  'Inspect Image'),  action: () => openLightbox(src, 'image') },
        ];
    }

    function buildSelfItems() {
        const curStatus = currentVrcUser?.status || 'active';
        const items = [
            { icon: 'manage_accounts', label: cm('friend.view_profile', 'View Profile'), action: () => openMyProfileModal() },
            'sep',
        ];
        STATUS_LIST.forEach(s => {
            items.push({
                dotColor: s.color,
                label: t(s.labelKey || '', s.label),
                checked: curStatus === s.key,
                action: () => sendToCS({ action: 'vrcUpdateStatus', status: s.key, statusDescription: currentVrcUser?.statusDescription || '' }),
            });
        });
        return items;
    }
}());
