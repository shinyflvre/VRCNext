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
    const { thumb, worldName, instanceType, instanceId, owner, ownerGroup, ownerId, region, userCount, capacity, friends, location, onclick } = opts;

    const { cls, label } = getInstanceBadge(instanceType);
    const thumbStyle = thumb ? `background-image:url('${cssUrl(thumb)}')` : '';

    const regionHtml = region
        ? `<span class="inst-item-region">${esc(region)}</span>`
        : '';

    const countHtml = userCount > 0
        ? `<span class="inst-item-count"><span class="msi" style="font-size:12px;">person</span>${userCount}${capacity > 0 ? '/' + capacity : ''}</span>`
        : '';

    const badgeHtml = `<span class="vrcn-badge ${cls}">${esc(label)}</span>`;
    const thumbEl   = `<div class="inst-item-thumb" style="${thumbStyle}"></div>`;
    const clickAttr = onclick ? ` onclick="${onclick}" style="cursor:pointer;"` : '';

    if (worldName) {
        // Layout B — profile: stacked name + meta
        return `<div class="inst-item"${clickAttr}>${thumbEl}<div class="inst-item-body"><div class="inst-item-name">${esc(worldName)}</div><div class="inst-item-meta">${badgeHtml}${countHtml}</div></div></div>`;
    }

    // Layout A — world modal: two rows
    let friendsHtml = '';
    if (friends && friends.length > 0) {
        const MAX_AV = 3;
        const avatars = friends.slice(0, MAX_AV).map(f => {
            const img = f.image || '';
            return img
                ? `<img class="inst-av-sm" src="${esc(img)}" title="${esc(f.displayName)}" onerror="this.style.display='none'">`
                : `<div class="inst-av-sm inst-av-sm-letter" title="${esc(f.displayName)}">${esc((f.displayName || '?')[0])}</div>`;
        }).join('');
        const extra = friends.length > MAX_AV ? `<span class="inst-av-extra">+${friends.length - MAX_AV}</span>` : '';
        friendsHtml = `<div class="inst-friends-strip">${avatars}${extra}</div>`;
    }

    let joinHtml = '';
    if (location && instanceType !== 'private') {
        const loc = location.replace(/'/g, "\\'");
        joinHtml = `<button class="vrcn-button inst-item-join" onclick="sendToCS({action:'vrcJoinFriend',location:'${loc}'});this.disabled=true;this.textContent='Joining...';" ><span class="msi" style="font-size:14px;">login</span> Join</button>`;
    }

    const rightHtml = (friendsHtml || joinHtml)
        ? `<div class="inst-item-right">${friendsHtml}${joinHtml}</div>`
        : '';

    // Row 1: group badge (clickable, themed) or player name or "#12345" (public fallback)
    const instNum = instanceId ? '#' + instanceId.split('~')[0] : '';
    let ownerRow = '';
    if (owner && ownerGroup && ownerId) {
        // Group instance: themed clickable badge showing "Full Name · ShortCode"
        const safeId = ownerId.replace(/'/g, "\\'");
        ownerRow = `<div class="inst-item-owner"><span class="inst-owner-group-badge" onclick="event.stopPropagation();openGroupDetail('${safeId}')">${esc(owner)}<span class="inst-owner-group-sep">·</span>${esc(ownerGroup)}</span></div>`;
    } else if (owner && ownerGroup) {
        // Fallback: no ownerId, plain text
        ownerRow = `<div class="inst-item-owner">${esc(owner)}<span class="inst-item-owner-sep">·</span><span class="inst-item-owner-tag">${esc(ownerGroup)}</span></div>`;
    } else if (owner) {
        // Player-owned instance
        ownerRow = `<div class="inst-item-owner">${esc(owner)}</div>`;
    } else if (instNum) {
        // Public: no resolved owner, show instance number
        ownerRow = `<div class="inst-item-owner"><span class="inst-item-owner-num">${instNum}</span></div>`;
    }

    return `<div class="inst-item"${clickAttr}><div class="inst-item-body">${ownerRow}<div class="inst-item-row">${badgeHtml}${regionHtml}${countHtml}${rightHtml}</div></div></div>`;
}
