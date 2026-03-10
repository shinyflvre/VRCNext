function doSearch(type, loadMore) {
    let query = '', targetEl = '', action = '';
    const sType = type === 'people' ? 'people' : type;
    if (type === 'worlds') { query = document.getElementById('searchWorldsInput').value.trim(); targetEl = 'searchWorldsResults'; action = 'vrcSearchWorlds'; }
    else if (type === 'groups') { query = document.getElementById('searchGroupsInput').value.trim(); targetEl = 'searchGroupsResults'; action = 'vrcSearchGroups'; }
    else if (type === 'people') { query = document.getElementById('searchPeopleInput').value.trim(); targetEl = 'searchPeopleResults'; action = 'vrcSearchUsers'; }
    if (!query) return;

    if (!loadMore) {
        searchState[sType] = { query, offset: 0, results: [], hasMore: false };
        document.getElementById(targetEl).innerHTML = sk(type === 'people' ? 'friend' : 'world', type === 'people' ? 5 : 3);
    } else {
        // Remove load-more button while loading
        const btn = document.getElementById(targetEl).querySelector('.load-more-btn');
        if (btn) btn.textContent = 'Loading...';
    }

    sendToCS({ action, query: searchState[sType].query, offset: searchState[sType].offset });
}

function renderSearchResults(type, results, offset, hasMore) {
    let targetEl = '', sType = '';
    if (type === 'worlds') { targetEl = 'searchWorldsResults'; sType = 'worlds'; }
    else if (type === 'groups') { targetEl = 'searchGroupsResults'; sType = 'groups'; }
    else if (type === 'users') { targetEl = 'searchPeopleResults'; sType = 'people'; }
    const el = document.getElementById(targetEl);
    if (!el) return;

    const state = searchState[sType];
    if (offset === 0) state.results = results;
    else state.results = state.results.concat(results);
    state.offset = state.results.length;
    state.hasMore = hasMore;

    if (state.results.length === 0) { el.innerHTML = '<div class="empty-msg">No results found</div>'; return; }

    let html = '';
    if (type === 'worlds') {
        html = state.results.map(w => renderWorldCard(w)).join('');
    } else if (type === 'groups') {
        html = state.results.map(g => `<div class="s-card" onclick="openGroupDetail('${esc(g.id)}')">
            <div class="s-card-img" style="background-image:url('${cssUrl(g.bannerUrl||g.iconUrl||'')}')"><div class="s-card-icon" style="background-image:url('${cssUrl(g.iconUrl||'')}')"></div></div>
            <div class="s-card-body"><div class="s-card-title">${esc(g.name)}</div><div class="s-card-sub">${esc(g.shortCode)} · <span class="msi" style="font-size:11px;">group</span> ${g.memberCount} members</div></div></div>`).join('');
    } else if (type === 'users') {
        html = state.results.map(u => `<div class="s-card s-card-h" onclick="openFriendDetail('${esc(u.id)}')">
            <div class="s-card-avatar" style="background-image:url('${cssUrl(u.image)}')"></div>
            <div class="s-card-body"><div class="s-card-title">${esc(u.displayName)}</div><div class="s-card-sub"><span class="status-dot-sm st-${u.status}"></span> ${esc(u.statusDescription||u.status)}${u.isFriend?' · <span style="color:var(--ok)">Friend</span>':''}</div></div></div>`).join('');
    }

    // Add "Load More" button if there are more results
    if (state.hasMore) {
        const searchType = sType === 'people' ? 'people' : sType;
        html += `<div style="grid-column:1/-1;text-align:center;padding:12px;"><button class="vrcn-button load-more-btn" onclick="doSearch('${searchType}',true)" style="padding:8px 24px;"><span class="msi" style="font-size:16px;">expand_more</span> Load More</button></div>`;
    }
    el.innerHTML = html;
}

function openUserDetail(userId) {
    // Unified: use same modal as friends
    openFriendDetail(userId);
}

function renderUserDetail(u) {
    // Unified: redirect to friend detail modal
    renderFriendDetail(u);
}
