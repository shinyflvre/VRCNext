(function () {
    const _fpCache = {};
    const CACHE_TTL = 10 * 60 * 1000;
    let _fpHideTimer = null;
    let _fpFetchTimer = null;
    let _fpCurrentUid = null;
    let _mouseOverPopup = false;
    let _fpHoverDelayTimer = null;
    let _fpDelayUid = null;
    const OPEN_HOVER_DELAY = 1500;

    function isSidebarCollapsed() {
        return document.getElementById('rsidebar')?.classList.contains('collapsed') ?? false;
    }

    function previewCollapsedEnabled() {
        return (typeof settings === 'undefined') || settings.friendsSidebarPreviewCollapsed !== false;
    }
    function previewOpenEnabled() {
        return (typeof settings !== 'undefined') && settings.friendsSidebarPreviewOpen === true;
    }

    function getPopup() {
        let el = document.getElementById('fpPreview');
        if (!el) {
            el = document.createElement('div');
            el.id = 'fpPreview';
            el.className = 'fp-preview';
            document.body.appendChild(el);
            el.addEventListener('mouseenter', () => {
                _mouseOverPopup = true;
                clearTimeout(_fpHideTimer);
            });
            el.addEventListener('mouseleave', () => {
                _mouseOverPopup = false;
                _fpHideTimer = setTimeout(hidePreview, 150);
            });
        }
        return el;
    }

    function positionPopup(popup, cardEl) {
        const sidebar = document.getElementById('rsidebar');
        if (!sidebar) return;
        const sRect = sidebar.getBoundingClientRect();
        const cRect = cardEl.getBoundingClientRect();
        const popH = popup.offsetHeight || 280;
        let top = cRect.top + cRect.height / 2 - popH / 2;
        top = Math.max(8, Math.min(window.innerHeight - popH - 8, top));
        popup.style.right = (window.innerWidth - sRect.left + 8) + 'px';
        popup.style.top = top + 'px';
    }

    const REGION_LABELS = { us: 'US East', use: 'US East', usw: 'US West', eu: 'EU', jp: 'JP', au: 'AU' };

    function parseRegion(loc) {
        const m = loc.match(/~region\(([^)]+)\)/);
        const code = m ? m[1].toLowerCase() : 'us';
        return REGION_LABELS[code] || code.toUpperCase();
    }

    function buildInstFriendsHtml(f, loc) {
        const all = typeof vrcFriendsData !== 'undefined' ? vrcFriendsData : [];
        const myId = typeof currentVrcUser !== 'undefined' && currentVrcUser ? currentVrcUser.id : null;
        const instFriends = all.filter(x => x.id !== f.id && x.id !== myId && x.location === loc);
        if (!instFriends.length) return '';
        const shown = instFriends.slice(0, 2);
        const extra = instFriends.length - 2;
        let html = '<div class="fp-inst-friends">';
        shown.forEach(fr => {
            if (fr.image) {
                html += `<img class="fp-inst-av" src="${esc(imgThumb(fr.image, 64))}" title="${esc(fr.displayName || '')}">`;
            } else {
                html += `<div class="fp-inst-av fp-inst-av-letter" title="${esc(fr.displayName || '')}">${esc((fr.displayName || '?')[0].toUpperCase())}</div>`;
            }
        });
        if (extra > 0) html += `<span class="fp-inst-more">+${extra}</span>`;
        html += '</div>';
        return html;
    }

    function buildInstanceHtml(f) {
        if (f.presence !== 'game' || !f.location) return '';
        const loc = f.location;

        if (loc === 'private') {
            return `<div class="fd-group-card"><div class="fd-group-card-info"><div class="fd-group-card-name" style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);">${typeof t === 'function' ? t('profiles.meta.private_instance','Private Instance') : 'Private Instance'}</div></div></div>`;
        }

        if (!loc.startsWith('wrld_')) return '';

        const { instanceType } = typeof parseFriendLocation === 'function'
            ? parseFriendLocation(loc) : { instanceType: 'public' };
        const { cls, label } = typeof getInstanceBadge === 'function'
            ? getInstanceBadge(instanceType) : { cls: '', label: instanceType };

        const worldId = loc.split(':')[0];
        const wc = (typeof dashWorldCache !== 'undefined' && dashWorldCache[worldId]) || null;
        const worldName = wc?.name || '';
        const worldThumb = wc?.thumbnailImageUrl || wc?.imageUrl || '';
        const region = parseRegion(loc);

        const safeLoc = loc.replace(/'/g, "\\'");
        const onclick = instanceType !== 'private'
            ? `onclick="sendToCS({action:'vrcJoinFriend',location:'${safeLoc}'});this.closest('.fd-group-card').style.opacity='0.5';"`
            : '';

        const regionHtml = `<span class="vrcn-badge"><span class="msi" style="font-size:10px;">public</span>${esc(region)}</span>`;
        const metaHtml = `<div class="fd-group-card-meta"><span class="vrcn-badge ${cls}">${esc(label)}</span>${regionHtml}</div>`;
        const friendsHtml = buildInstFriendsHtml(f, loc);

        if (worldName) {
            const thumbHtml = worldThumb
                ? `<img class="fd-group-icon" src="${esc(imgThumb(worldThumb, 96))}" onerror="this.style.display='none'">`
                : `<div class="fd-group-icon fd-group-icon-empty"><span class="msi" style="font-size:16px;">travel_explore</span></div>`;
            return `<div class="fd-group-card" ${onclick}>
                ${thumbHtml}
                <div class="fd-group-card-info">
                    <div style="display:flex;align-items:center;justify-content:space-between;gap:6px;min-width:0;">
                        <div class="fd-group-card-name" style="min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${esc(worldName)}</div>
                        ${friendsHtml}
                    </div>
                    ${metaHtml}
                </div>
            </div>`;
        }

        // World not cached — just badge + join
        return `<div class="fd-group-card" ${onclick}>
            <div class="fd-group-icon fd-group-icon-empty"><span class="msi" style="font-size:16px;">travel_explore</span></div>
            <div class="fd-group-card-info">
                <div style="display:flex;align-items:center;justify-content:space-between;gap:6px;min-width:0;">
                    <div class="fd-group-card-name" style="color:var(--tx3);font-size:calc(11px + var(--fs-off, 0px));min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${typeof t === 'function' ? t('profiles.meta.in_game','In Game') : 'In Game'}</div>
                    ${friendsHtml}
                </div>
                ${metaHtml}
            </div>
        </div>`;
    }

    function renderPopup(popup, f, extra) {
        const img = f.image || '';
        const banner = extra?.banner || img;
        const bio = extra?.bio || '';

        const tags = f.tags || [];
        const rank = typeof getTrustRank === 'function' ? getTrustRank(tags) : null;
        const isSupporter = tags.includes('system_supporter');
        const isOff = f.presence === 'offline';
        const isWeb = f.presence === 'web';
        const statusCls = isOff ? 's-offline' : (typeof statusDotClass === 'function' ? statusDotClass(f.status) : '');
        const dotCls = isWeb ? 'vrc-status-ring' : 'vrc-status-dot';
        const statusTxt = f.statusDescription
            || (typeof statusLabel === 'function' ? statusLabel(f.status) : (isOff ? 'Offline' : f.status));

        const langs = tags.filter(tag => tag.startsWith('language_'))
            .map(tag => (typeof LANG_MAP !== 'undefined' && LANG_MAP[tag]) || tag.replace('language_', '').toUpperCase());

        const rankBadge = rank
            ? `<span class="vrcn-badge ${rank.cls}">${esc(rank.label)}</span>`
            : '';
        const ageBadge = (f.ageVerified
            ? `<span class="vrcn-badge ok"><span class="msi" style="font-size:10px;">verified</span>${typeof t === 'function' ? t('profiles.meta.age_verified', 'Age Verified') : 'Age Verified'}</span>`
            : '')
            + (f.ageVerificationStatus === '18+'
            ? `<span class="vrcn-badge ok"><span class="msi" style="font-size:10px;">verified</span>18+</span>`
            : '');
        const platBadge = typeof getPlatformBadgeHtml === 'function' ? getPlatformBadgeHtml(f.platform || '') : '';
        const vrcPlusBadge = isSupporter ? `<span class="vrcn-supporter-badge">VRC+</span>` : '';
        const friendBadge = `<span class="vrcn-badge bdg-friend"><span class="msi" style="font-size:10px;">check_circle</span>${typeof t === 'function' ? t('profiles.badges.friend', 'Friend') : 'Friend'}</span>`;

        const truncBio = bio.length > 60 ? bio.slice(0, 60) + '…' : bio;
        const bioHtml = truncBio ? `<div class="fd-bio">${esc(truncBio)}</div>` : '';

        const langsHtml = langs.length
            ? `<div class="fd-lang-tags">${langs.map(l => `<span class="vrcn-badge">${esc(l)}</span>`).join('')}</div>`
            : '';

        const instanceHtml = buildInstanceHtml(f);
        const statsHtml = buildStatsRow(f.id);
        const avatarInner = img
            ? `<img class="fp-av" src="${esc(imgThumb(img, 96))}" onerror="this.style.display='none'">`
            : `<div class="fp-av fp-av-letter">${esc((f.displayName || '?')[0].toUpperCase())}</div>`;

        popup.innerHTML = `
            <div class="fd-banner">
                ${banner ? `<div class="fp-banner-bg" style="background-image:url('${cssUrl(banner)}')"></div>` : ''}
                <div class="fd-banner-fade"></div>
                ${(typeof profileEffectHtml === 'function') ? profileEffectHtml(f.profileEffectUrl) : ''}
            </div>
            <div class="fp-body">
                <div class="fp-header">
                    <div class="fp-av-wrap">
                        ${avatarInner}
                        ${(typeof iconFrameHtml === 'function') ? iconFrameHtml(f.iconFrameUrl) : ''}
                        <span class="fp-av-dot ${dotCls} ${statusCls}"></span>
                    </div>
                    <div class="fp-header-info">
                        <div class="fp-name-row">
                            <span class="fd-name">${esc(f.displayName)}</span>${vrcPlusBadge}
                        </div>
                        <div class="fp-status-row"><span>${esc(statusTxt)}</span></div>
                        ${statsHtml}
                    </div>
                </div>
                <div class="fp-section">
                    <div class="fd-badges-row">${rankBadge}${friendBadge}${ageBadge}${platBadge}</div>
                    ${instanceHtml}${bioHtml}${langsHtml}
                </div>
            </div>`;

        // The background fields ride along on the preview payload, so the hover card
        // gets the same treatment as the profile modal.
        if (typeof applyProfileBg === 'function') applyProfileBg(popup, extra || f);
        if (typeof applyProfileTheme === 'function') applyProfileTheme(popup, extra || f);
    }

    function buildStatsRow(uid) {
        const st = (typeof window.getPeopleStat === 'function') ? window.getPeopleStat(uid) : null;
        if (typeof window.requestPeopleStats === 'function') window.requestPeopleStats();
        if (!st || ((st.seconds || 0) <= 0 && (st.meets || 0) <= 0)) return '';
        const timeStr = (typeof window.fmtPeopleStatTime === 'function')
            ? window.fmtPeopleStatTime(st.seconds || 0) : '';
        return `<div class="fp-stats-row">
            <span class="msi">schedule</span><span>${esc(timeStr)}</span>
            <span class="fp-stats-dot">&middot;</span>
            <span class="msi">handshake</span><span>${st.meets || 0}</span>
        </div>`;
    }

    function showPreview(uid, cardEl) {
        const f = (typeof vrcFriendsData !== 'undefined' ? vrcFriendsData : []).find(x => x.id === uid);
        if (!f) return;

        clearTimeout(_fpHideTimer);
        _fpCurrentUid = uid;

        const popup = getPopup();
        renderPopup(popup, f, _fpCache[uid] || null);
        popup.dataset.uid = uid;
        popup.classList.add('visible');
        positionPopup(popup, cardEl);

        // Fetch bio/banner immediately if not cached
        const cached = _fpCache[uid];
        if (!cached || Date.now() - cached.ts > CACHE_TTL) {
            clearTimeout(_fpFetchTimer);
            if (typeof sendToCS === 'function') sendToCS({ action: 'vrcGetFriendPreview', userId: uid });
        }
    }

    function hidePreview() {
        if (_mouseOverPopup) return;
        _fpCurrentUid = null;
        clearTimeout(_fpFetchTimer);
        const popup = document.getElementById('fpPreview');
        if (popup) popup.classList.remove('visible');
    }

    window.handleFriendPreview = function (data) {
        if (!data?.id) return;
        _fpCache[data.id] = {
            bio: data.bio || '',
            banner: data.bannerUrl || data.profilePicOverride || '',
            backgroundType:           data.backgroundType || '',
            backgroundTextureId:      data.backgroundTextureId || '',
            backgroundTextureUrl:     data.backgroundTextureUrl || '',
            backgroundGradientTop:    data.backgroundGradientTop || '',
            backgroundGradientBottom: data.backgroundGradientBottom || '',
            ts: Date.now(),
        };
        // Re-render popup if still showing this friend
        if (_fpCurrentUid === data.id) {
            const popup = document.getElementById('fpPreview');
            if (popup?.classList.contains('visible')) {
                const f = (typeof vrcFriendsData !== 'undefined' ? vrcFriendsData : []).find(x => x.id === data.id);
                if (f) {
                    renderPopup(popup, f, _fpCache[data.id]);
                    const card = document.querySelector(`.vrc-friend-card[data-uid="${CSS.escape(data.id)}"]`);
                    if (card) positionPopup(popup, card);
                }
            }
        }
    };

    window._fpOnStatsLoaded = function () {
        if (!_fpCurrentUid) return;
        const popup = document.getElementById('fpPreview');
        if (!popup || !popup.classList.contains('visible')) return;
        const f = (typeof vrcFriendsData !== 'undefined' ? vrcFriendsData : []).find(x => x.id === _fpCurrentUid);
        if (!f) return;
        renderPopup(popup, f, _fpCache[_fpCurrentUid] || null);
        const card = document.querySelector(`.vrc-friend-card[data-uid="${CSS.escape(_fpCurrentUid)}"]`);
        if (card) positionPopup(popup, card);
    };

    // Cache bio/banner whenever a full friend detail is opened. The profile payload
    // carries the VRC+ background too, so it is taken along - dropping it here would
    // silently strip the background from an already cached preview.
    const _origRender = window.renderFriendDetail;
    if (typeof _origRender === 'function') {
        window.renderFriendDetail = function (d) {
            if (d?.id) _fpCache[d.id] = {
                bio: d.bio || '',
                banner: d.bannerUrl || d.profilePicOverride || '',
                backgroundType:           d.backgroundType || '',
                backgroundTextureId:      d.backgroundTextureId || '',
                backgroundTextureUrl:     d.backgroundTextureUrl || '',
                backgroundGradientTop:    d.backgroundGradientTop || '',
                backgroundGradientBottom: d.backgroundGradientBottom || '',
                ts: Date.now(),
            };
            return _origRender.call(this, d);
        };
    }

    function init() {
        const sidebar = document.getElementById('rsidebar');
        if (!sidebar) { setTimeout(init, 500); return; }

        sidebar.addEventListener('mouseover', function (e) {
            if (isSidebarCollapsed()) {
                if (!previewCollapsedEnabled()) return;
                const card = e.target.closest('.vrc-friend-card');
                if (!card) {
                    if (_fpCurrentUid) {
                        clearTimeout(_fpHideTimer);
                        _fpHideTimer = setTimeout(hidePreview, 150);
                    }
                    return;
                }
                const uid = card.dataset.uid;
                if (!uid || _fpCurrentUid === uid) return;
                clearTimeout(_fpHideTimer);
                showPreview(uid, card);
                return;
            }

            // Expanded sidebar: only when hovering the avatar, after a 1.5s delay.
            if (!previewOpenEnabled()) return;
            const avatar = e.target.closest('.vrc-friend-avatar-wrap, .vrc-friend-avatar');
            const card = avatar ? avatar.closest('.vrc-friend-card') : null;
            if (!card) {
                clearTimeout(_fpHoverDelayTimer);
                _fpDelayUid = null;
                return;
            }
            const uid = card.dataset.uid;
            if (!uid || _fpCurrentUid === uid || _fpDelayUid === uid) return;
            clearTimeout(_fpHoverDelayTimer);
            clearTimeout(_fpHideTimer);
            _fpDelayUid = uid;
            _fpHoverDelayTimer = setTimeout(() => { _fpDelayUid = null; showPreview(uid, card); }, OPEN_HOVER_DELAY);
        });

        // When mouse leaves sidebar, cancel any pending hover and start hide timer unless going to popup
        sidebar.addEventListener('mouseleave', function (e) {
            clearTimeout(_fpHoverDelayTimer);
            _fpDelayUid = null;
            if (e.relatedTarget?.closest?.('#fpPreview')) return;
            _fpHideTimer = setTimeout(hidePreview, 150);
        });
    }

    window.fpRenderPreviewInto = function (el, f, extra) { renderPopup(el, f, extra); };

    document.readyState === 'loading'
        ? document.addEventListener('DOMContentLoaded', init)
        : init();
})();
