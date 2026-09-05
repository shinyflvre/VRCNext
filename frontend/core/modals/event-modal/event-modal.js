/* === Calendar Event Detail Modal === */

function openEventDetail(groupId, calendarId) {
    if (!groupId || !calendarId) return;
    if (typeof navSetCurrent === 'function') navSetCurrent('event', groupId, calendarId);
    const el = document.getElementById('detailModalContent');
    el.innerHTML = sk('detail');
    const _evMb = document.querySelector('#modalDetail .modal-box');
    if (_evMb) _evMb.classList.remove('narrow');
    document.getElementById('modalDetail').classList.remove('wd-style-compact', 'gd-style-compact', 'tl-style-compact');
    document.getElementById('modalDetail').style.display = 'flex';
    sendToCS({ action: 'vrcGetCalendarEvent', groupId, calendarId });
}

function closeEventDetail(fromNav = false) {
    document.getElementById('modalDetail').style.display = 'none';
    if (!fromNav && typeof navClear === 'function') navClear();
}

function renderEventDetail(ev) {
    const el = document.getElementById('detailModalContent');
    if (!ev || !ev.id) {
        el.innerHTML = `<div style="padding:40px;text-align:center;color:var(--tx3);">${t('calendar.detail.not_found', 'Event not found')}</div>`;
        return;
    }

    const bannerSrc = ev.imageUrl || 'fallback_cover.png';
    const bannerHtml = `<div class="fd-banner"><img src="${bannerSrc}" onerror="this.src='fallback_cover.png'"><div class="fd-banner-fade"></div></div>`;

    const start = ev.startsAt ? new Date(ev.startsAt) : null;
    const end = ev.endsAt ? new Date(ev.endsAt) : null;
    const dateLine = start && !isNaN(start) ? fmtLongDate(start) : '';
    const endDiffDay = end && !isNaN(end) && start &&
        (end.getFullYear() !== start.getFullYear() || end.getMonth() !== start.getMonth() || end.getDate() !== start.getDate());
    const timeLine = start && !isNaN(start)
        ? fmtTime(start) + (end && !isNaN(end) ? ' – ' + (endDiffDay ? fmtLongDate(end) + ', ' : '') + fmtTime(end) : '')
        : '';

    const tags = Array.isArray(ev.tags) ? ev.tags : [];
    const tagsHtml = tags.map(tag => {
        const isFeatured = /featured/i.test(tag);
        return `<span class="vrcn-badge${isFeatured ? ' warn' : ''}">${esc(tag)}</span>`;
    }).join('');

    const { cls: accessCls } = getInstanceBadge((ev.accessType || '').toLowerCase());
    const accessBadge = ev.accessType
        ? `<span class="vrcn-badge ${accessCls}">${esc(ev.accessType)}</span>`
        : '';

    // Resolve group info: prefer ev.group, fall back to local myGroups cache
    const myGroupsList = (typeof myGroups !== 'undefined') ? myGroups : [];
    const gid = ev.ownerId || ev.groupId || '';
    const groupCache = myGroupsList.find(g => g.id === gid) || {};
    const groupName = ev.group?.name || groupCache.name || '';
    const groupIconUrl = ev.group?.iconUrl || groupCache.iconUrl || '';
    const groupOpenId = jsq(ev.group?.id || gid);

    const groupTopHtml = groupName
        ? `<div style="display:flex;align-items:center;gap:6px;margin-bottom:2px;">
               ${groupIconUrl ? `<img src="${groupIconUrl}" style="width:16px;height:16px;border-radius:3px;object-fit:cover;flex-shrink:0;">` : `<span class="msi" style="font-size:14px;color:var(--tx2);">group</span>`}
               <span style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx0);overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${esc(groupName)}</span>
           </div>`
        : '';

    const groupHtml = groupName
        ? `<div class="fd-section-label" style="margin-top:12px;">${t('calendar.detail.organizer', 'Organizer')}</div>
           <div style="display:flex;align-items:center;gap:8px;cursor:pointer;padding:6px 0;" onclick="navOpenModal('group','${groupOpenId}','${jsq(groupName || '')}')">
               ${groupIconUrl ? `<img src="${groupIconUrl}" style="width:28px;height:28px;border-radius:6px;object-fit:cover;">` : ''}
               <span style="font-size:calc(13px + var(--fs-off, 0px));color:var(--tx0);">${esc(groupName)}</span>
               <span class="msi" style="font-size:14px;color:var(--tx2);">chevron_right</span>
           </div>`
        : '';

    const isFollowing = ev.userInterest?.isFollowing === true;
    const groupId = esc(gid);
    const calendarId = esc(ev.id || '');
    const followBtnId = `evFollowBtn_${ev.id}`;
    const followLabel = isFollowing
        ? t('calendar.detail.unfollow', 'Unfollow')
        : t('calendar.detail.follow', 'Follow');

    const evHeaderActions = renderModalActions([
        { icon: isFollowing ? 'notifications_off' : 'notifications_active', title: followLabel, onclick: `toggleFollowEvent('${groupId}','${calendarId}',${isFollowing},this)` },
        gid ? { icon: 'group', title: t('calendar.detail.open_group', 'Open Group'), onclick: `navOpenModal('group','${groupOpenId}','${jsq(groupName || '')}')` } : null,
        { icon: 'close', title: t('common.close', 'Close'), onclick: `closeEventDetail()` },
    ]);

    el.innerHTML = `${evHeaderActions}
        ${bannerHtml}
        <div class="fd-content${bannerHtml ? ' fd-has-banner' : ''}">
            <div class="fd-header" style="flex-direction:column;align-items:flex-start;gap:6px;">
                ${groupTopHtml}
                <div class="fd-name" style="font-size:calc(18px + var(--fs-off, 0px));">${esc(ev.title || t('calendar.untitled_event', 'Untitled Event'))}</div>
                ${dateLine ? `<div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx2);display:flex;align-items:center;gap:4px;"><span class="msi" style="font-size:14px;">calendar_today</span>${esc(dateLine)}</div>` : ''}
                ${timeLine ? `<div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx2);display:flex;align-items:center;gap:4px;"><span class="msi" style="font-size:14px;">schedule</span>${esc(timeLine)}</div>` : ''}
                <div style="display:flex;flex-wrap:wrap;gap:4px;margin-top:2px;">${accessBadge}${tagsHtml}</div>
            </div>
            ${groupHtml}
            ${ev.description ? `<div class="fd-section-label" style="margin-top:12px;">${t('calendar.detail.about', 'About')}</div><div class="fd-bio">${esc(ev.description)}</div>` : ''}
        </div>`;
}

function toggleFollowEvent(groupId, calendarId, isCurrentlyFollowing, btn) {
    const follow = !isCurrentlyFollowing;
    sendToCS({ action: 'vrcFollowEvent', groupId, calendarId, follow });
    if (btn) {
        const label = follow
            ? t('calendar.detail.unfollow', 'Unfollow')
            : t('calendar.detail.follow', 'Follow');
        const ic = btn.querySelector('.msi');
        if (ic) ic.textContent = follow ? 'notifications_off' : 'notifications_active';
        const lbl = btn.querySelector('.ev-follow-lbl');
        if (lbl) lbl.textContent = label;
        else if (!ic) btn.textContent = label;
        btn.title = label;
        btn.onclick = () => toggleFollowEvent(groupId, calendarId, follow, btn);
    }
}
