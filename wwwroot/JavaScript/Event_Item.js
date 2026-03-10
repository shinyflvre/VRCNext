/* === Calendar Event Detail Modal === */

function openEventDetail(groupId, calendarId) {
    if (!groupId || !calendarId) return;
    const el = document.getElementById('detailModalContent');
    el.innerHTML = sk('detail');
    document.getElementById('modalDetail').style.display = 'flex';
    sendToCS({ action: 'vrcGetCalendarEvent', groupId, calendarId });
}

function renderEventDetail(ev) {
    const el = document.getElementById('detailModalContent');
    if (!ev || !ev.id) {
        el.innerHTML = '<div style="padding:40px;text-align:center;color:var(--tx3);">Event not found</div>';
        return;
    }

    const bannerHtml = ev.imageUrl
        ? `<div class="fd-banner"><img src="${ev.imageUrl}" onerror="this.parentElement.style.display='none'"><div class="fd-banner-fade"></div></div>`
        : '';

    const start = ev.startsAt ? new Date(ev.startsAt) : null;
    const end   = ev.endsAt   ? new Date(ev.endsAt)   : null;
    const dateLine = start && !isNaN(start)
        ? start.toLocaleDateString(undefined, { weekday:'long', year:'numeric', month:'long', day:'numeric' })
        : '';
    const timeLine = start && !isNaN(start)
        ? start.toLocaleTimeString(undefined, { hour:'2-digit', minute:'2-digit' }) +
          (end && !isNaN(end) ? ' – ' + end.toLocaleTimeString(undefined, { hour:'2-digit', minute:'2-digit' }) : '')
        : '';

    const tags = Array.isArray(ev.tags) ? ev.tags : [];
    const tagsHtml = tags.map(t => {
        const isFeat = /featured/i.test(t);
        return `<span class="vrcn-badge${isFeat ? ' warn' : ''}">${esc(t)}</span>`;
    }).join('');

    const { cls: _accCls } = getInstanceBadge((ev.accessType || '').toLowerCase());
    const accessBadge = ev.accessType
        ? `<span class="vrcn-badge ${_accCls}">${esc(ev.accessType)}</span>`
        : '';

    const groupHtml = ev.group
        ? `<div class="fd-section-label" style="margin-top:12px;">Organizer</div>
           <div style="display:flex;align-items:center;gap:8px;cursor:pointer;padding:6px 0;" onclick="openGroupDetail('${esc(ev.group.id||ev.ownerId||'')}')">
               ${ev.group.iconUrl ? `<img src="${ev.group.iconUrl}" style="width:28px;height:28px;border-radius:6px;object-fit:cover;">` : ''}
               <span style="font-size:13px;color:var(--tx1);">${esc(ev.group.name||'')}</span>
               <span class="msi" style="font-size:14px;color:var(--tx3);">chevron_right</span>
           </div>`
        : '';

    const isFollowing = ev.userInterest?.isFollowing === true;
    const groupId     = esc(ev.ownerId || '');
    const calendarId  = esc(ev.id || '');
    const followBtnId = `evFollowBtn_${ev.id}`;

    el.innerHTML = `
        ${bannerHtml}
        <div class="fd-content${bannerHtml ? ' fd-has-banner' : ''}">
            <div class="fd-header" style="flex-direction:column;align-items:flex-start;gap:6px;">
                <div class="fd-name" style="font-size:18px;">${esc(ev.title || 'Untitled Event')}</div>
                ${dateLine ? `<div style="font-size:12px;color:var(--tx2);display:flex;align-items:center;gap:4px;"><span class="msi" style="font-size:14px;">calendar_today</span>${esc(dateLine)}</div>` : ''}
                ${timeLine ? `<div style="font-size:12px;color:var(--tx2);display:flex;align-items:center;gap:4px;"><span class="msi" style="font-size:14px;">schedule</span>${esc(timeLine)}</div>` : ''}
                <div style="display:flex;flex-wrap:wrap;gap:4px;margin-top:2px;">${accessBadge}${tagsHtml}</div>
            </div>
            ${groupHtml}
            ${ev.description ? `<div class="fd-section-label" style="margin-top:12px;">About</div><div class="fd-bio">${esc(ev.description)}</div>` : ''}
            <div style="display:flex;justify-content:space-between;align-items:center;margin-top:16px;">
                <button class="vrcn-button-round vrcn-btn-join" id="${followBtnId}" onclick="toggleFollowEvent('${groupId}','${calendarId}',${isFollowing},this)"><span class="msi">${isFollowing ? 'notifications_off' : 'notifications_active'}</span><span class="ev-follow-lbl">${isFollowing ? 'Unfollow' : 'Follow'}</span></button>
                <button class="vrcn-button-round" onclick="document.getElementById('modalDetail').style.display='none'">Close</button>
            </div>
        </div>`;
}

function toggleFollowEvent(groupId, calendarId, isCurrentlyFollowing, btn) {
    const follow = !isCurrentlyFollowing;
    sendToCS({ action: 'vrcFollowEvent', groupId, calendarId, follow });
    if (btn) {
        btn.querySelector('.msi').textContent        = follow ? 'notifications_off' : 'notifications_active';
        btn.querySelector('.ev-follow-lbl').textContent = follow ? 'Unfollow' : 'Follow';
        btn.onclick = () => toggleFollowEvent(groupId, calendarId, follow, btn);
    }
}
