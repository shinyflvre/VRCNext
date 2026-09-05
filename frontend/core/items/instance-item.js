function _selfInstanceMember() {
    const self = (typeof currentVrcUser !== 'undefined') ? currentVrcUser : null;
    if (!self || !self.id) return null;
    let loc = self.location || '';
    if ((!loc || !loc.includes(':')) && typeof currentInstanceData !== 'undefined' && currentInstanceData && currentInstanceData.location) {
        loc = currentInstanceData.location;
    }
    if (!loc || !loc.includes(':')) return null;
    return { id: self.id, displayName: self.displayName, image: self.image, status: self.status, statusDescription: self.statusDescription, location: loc, presence: 'game' };
}

function getInstanceMembers(location) {
    const base = (location || '').split('~')[0];
    if (!base || !base.includes(':')) return [];
    const out = [];
    const self = _selfInstanceMember();
    if (self && self.location.split('~')[0] === base) out.push(self);
    if (typeof vrcFriendsData !== 'undefined') {
        vrcFriendsData.forEach(f => {
            if (self && f.id === self.id) return;
            if ((f.location || '').split('~')[0] === base) out.push(f);
        });
    }
    return out;
}

/**
 * Universal instance item card.
 * Used by: World Modal → Active Instances, Friend Profile → Current World.
 *
 * Layout A — world modal:
 *   row 1: owner name (group or player) — only if available
 *   row 2: [badge] [region] [count]  →→→  [friend avatars] [Join]
 *
 * Layout B — profile:
 *   [thumb] | name
 *           | [badge] [count]
 *
 * @param {object}  opts
 * @param {string}  opts.thumb        - World thumbnail URL (Layout B only)
 * @param {string}  [opts.worldName]  - World name → triggers Layout B
 * @param {string}  opts.instanceType - e.g. 'public', 'friends+', 'group'
 * @param {string}  [opts.owner]      - Instance owner display name (group or player)
 * @param {string}  [opts.region]     - Region label (e.g. 'EU', 'US East')
 * @param {number}  [opts.userCount]  - Current player count
 * @param {number}  [opts.capacity]   - Max player count
 * @param {Array}   [opts.friends]    - [{image, displayName}] for friend strip
 * @param {string}  [opts.location]   - Instance location string (enables Join button)
 * @param {string}  [opts.onclick]    - Inline JS onclick string (makes card clickable)
 * @returns {string} HTML string
 */
function renderInstanceItem(opts) {
    const { thumb, worldName, worldTitle, instanceType, instanceId, owner, ownerGroup, ownerId, region, userCount, capacity, friends, location, onclick, ageGate, languageRatio } = opts;

    const { cls, label } = getInstanceBadge(instanceType);
    const joinLabel = t('common.join', 'Join');
    const joiningLabel = t('common.joining', 'Joining...');
    const thumbStyle = thumb ? `background-image:url('${cssUrl(imgThumb(thumb, 128))}')` : '';

    const regionHtml = region
        ? `<span class="vrcn-badge"><span class="msi" style="font-size:10px;">language</span>${esc(region)}</span>`
        : '';

    const countHtml = userCount > 0
        ? `<span class="vrcn-badge"><span class="msi" style="font-size:10px;">person</span>${userCount}${capacity > 0 ? '/' + capacity : ''}</span>`
        : '';

    // Top 2 languages from languageRatio, shown as compact badges
    let langHtml = '';
    if (languageRatio && typeof languageRatio === 'object') {
        const sorted = Object.entries(languageRatio).sort((a, b) => b[1] - a[1]).slice(0, 2);
        langHtml = sorted.map(([lang, pct]) =>
            `<span class="vrcn-badge" style="font-size:calc(10px + var(--fs-off, 0px));">${esc(lang.toUpperCase())} ${Math.round(pct * 100)}%</span>`
        ).join('');
    }

    const ageGateLabel = t('worlds.instances.age_gated', 'Age Gated');
    const badgeHtml = `<span class="vrcn-badge ${cls}">${esc(label)}</span>${ageGate ? `<span class="vrcn-badge" style="background:rgba(255,75,85,.15);color:var(--err);">${esc(ageGateLabel)}</span>` : ''}`;
    const thumbEl   = `<div class="inst-item-thumb" style="${thumbStyle}"></div>`;
    const clickAttr = onclick ? ` onclick="${onclick}" style="cursor:pointer;"` : '';

    if (worldName) {
        // Layout B — profile: stacked name + meta
        const bInstNum = instanceId ? esc(instanceId.split('~')[0]) : '';
        let bIdBadge = '';
        if (bInstNum) {
            bIdBadge = location
                ? `<span class="vrcn-badge" style="cursor:pointer;" title="${esc(t('timeline.actions.copy_instance_link', 'Copy Instance Link'))}" onclick="event.stopPropagation();copyInstanceLink('${location.replace(/'/g, "\\'")}')"><span class="msi" style="font-size:10px;">content_copy</span>#${bInstNum}</span>`
                : `<span class="vrcn-badge">#${bInstNum}</span>`;
        }
        const regionMeta = regionHtml ? regionHtml : '';
        return `<div class="inst-item"${clickAttr}>${thumbEl}<div class="inst-item-body"><div class="inst-item-name">${esc(worldName)}</div><div class="inst-item-meta">${badgeHtml}${bIdBadge}${countHtml}${regionMeta}</div></div></div>`;
    }

    // Layout A — world modal: instance modal card style
    const instNum = instanceId ? instanceId.split('~')[0] : '';

    let ownerBadge = '';
    if (owner && ownerGroup && ownerId) {
        const safeId    = ownerId.replace(/'/g, "\\'");
        const safeOwner = owner.replace(/'/g, "\\'");
        ownerBadge = `<span class="inst-owner-group-badge" onclick="event.stopPropagation();navOpenModal('group','${safeId}','${safeOwner}')">${esc(owner)}<span class="inst-owner-group-sep">·</span>${esc(ownerGroup)}</span>`;
    } else if (owner && ownerGroup) {
        ownerBadge = `<span class="vrcn-badge">${esc(owner)}<span style="opacity:.5;margin:0 3px;">·</span>${esc(ownerGroup)}</span>`;
    } else if (owner) {
        ownerBadge = `<span class="vrcn-badge">${esc(owner)}</span>`;
    }

    let friendsHtml = '';
    if (friends && friends.length > 0) {
        const items = friends.map(f =>
            renderProfileItem(f, `navOpenModal('friend','${jsq(f.id || '')}','${jsq(f.displayName || '')}')`)
        ).join('');
        friendsHtml = `<div class="mi-friends-list">${items}</div>`;
    }

    let joinHtml = '';
    if (location && instanceType !== 'private') {
        const loc = location.replace(/'/g, "\\'");
        joinHtml = `<button class="vrcn-button-round vrcn-btn-join" title="${esc(joinLabel)}" onclick="sendToCS({action:'vrcJoinFriend',location:'${loc}'});this.disabled=true;"><span class="msi" style="font-size:14px;">login</span></button>`;
    }

    let instIdBadge = '';
    if (instNum) {
        instIdBadge = location
            ? `<span class="vrcn-badge" style="cursor:pointer;" title="${esc(typeof t === 'function' ? t('timeline.actions.copy_instance_link', 'Copy Instance Link') : 'Copy Instance Link')}" onclick="event.stopPropagation();copyInstanceLink('${location.replace(/'/g, "\\'")}')"><span class="msi" style="font-size:10px;">content_copy</span>#${esc(instNum)}</span>`
            : `<span class="vrcn-badge">#${esc(instNum)}</span>`;
    }

    const hasSep = friendsHtml ? ' inst-item-card-hdr-sep' : '';
    const thumbHtml = thumbStyle ? `<div class="inst-item-thumb" style="${thumbStyle}"></div>` : '';

    // Title row: "23/80 · World Name · Instance Type · #ID (GroupShortCode)"
    const titleParts = [];
    if (userCount > 0) titleParts.push(`<span class="vrcn-badge"><span class="msi" style="font-size:10px;">person</span>${userCount}${capacity > 0 ? '/' + capacity : ''}</span>`);
    if (worldTitle) titleParts.push(`<span style="color:var(--tx0);font-weight:700;">${esc(worldTitle)}</span>`);
    titleParts.push(`<span style="color:var(--tx3);">·</span><span>${esc(label)}</span>`);
    if (ownerGroup) titleParts.push(`<span style="color:var(--tx2);">(${esc(ownerGroup)})</span>`);
    const titleHtml = `<div class="inst-item-card-title">${titleParts.join(' ')}</div>`;

    return `<div class="inst-item-card" data-iid="${esc(instanceId || '')}"${clickAttr}>
        <div class="inst-item-card-hdr${hasSep}">
            ${thumbHtml}
            <div class="inst-item-card-body">
                ${titleHtml}
                <div style="display:flex;align-items:center;gap:6px;flex-wrap:wrap;">
                    ${badgeHtml}${instIdBadge}${ownerBadge}${regionHtml}${langHtml}
                    <div class="inst-item-right">${joinHtml}</div>
                </div>
            </div>
        </div>
        ${friendsHtml}
    </div>`;
}
