/* === Avatars Tab === */
function refreshAvatars() {
    if (!currentVrcUser) {
        document.getElementById('avatarGrid').innerHTML = '<div class="empty-msg">Login to VRChat to see your avatars</div>';
        return;
    }
    document.getElementById('avatarGrid').innerHTML = sk('avatar', 6);
    sendToCS({ action: 'vrcGetAvatars', filter: avatarFilter });
}

function setAvatarFilter(filter) {
    avatarFilter = filter;
    document.querySelectorAll('.avatar-filter-btn').forEach(b => b.classList.remove('active'));
    const btnId = filter === 'own' ? 'avatarFilterOwn' : 'avatarFilterFav';
    document.getElementById(btnId).classList.add('active');
    // Always fetch when switching, same as clicking Refresh
    refreshAvatars();
}

function renderAvatarGrid() {
    const el = document.getElementById('avatarGrid');
    const list = avatarFilter === 'own' ? avatarsData : avatarFavData;

    if (!currentVrcUser) {
        el.innerHTML = '<div class="empty-msg">Login to VRChat to see your avatars</div>';
        return;
    }
    if (list.length === 0) {
        const msgs = { own: 'No avatars found', favorites: 'No favorite avatars' };
        el.innerHTML = `<div class="empty-msg">${msgs[avatarFilter] || 'No avatars'}</div>`;
        return;
    }

    document.getElementById('avatarCount').textContent = `${list.length} avatar${list.length !== 1 ? 's' : ''}`;

    let html = list.map(a => {
        const thumb = a.thumbnailImageUrl || a.imageUrl || '';
        const isActive = a.id === currentAvatarId;
        const isPublic = a.releaseStatus === 'public';
        const badge = isPublic
            ? '<span class="av-badge public"><span class="msi" style="font-size:10px;">public</span> Public</span>'
            : '<span class="av-badge private"><span class="msi" style="font-size:10px;">lock</span> Private</span>';
        const activeBadge = isActive ? '<span class="av-badge current">Current</span>' : '';
        const aid = (a.id || '').replace(/'/g, "\\'");
        const thumbStyle = thumb ? `background-image:url('${thumb}')` : '';
        return `<div class="av-card ${isActive ? 'av-active' : ''}" onclick="selectAvatar('${aid}')">
            <div class="av-thumb" style="${thumbStyle}">
                <div class="av-thumb-overlay"></div>
                <div class="av-badges">${activeBadge}${badge}</div>
            </div>
            <div class="av-info">
                <div class="av-name">${esc(a.name || 'Unnamed')}</div>
                <div class="av-author">${esc(a.authorName || '')}</div>
            </div>
        </div>`;
    }).join('');

    el.innerHTML = html;
}

function selectAvatar(avatarId) {
    if (!avatarId || avatarId === currentAvatarId) return;
    // Show loading state on the clicked card
    document.querySelectorAll('.av-card').forEach(c => {
        c.style.pointerEvents = 'none';
        c.style.opacity = '0.6';
    });
    sendToCS({ action: 'vrcSelectAvatar', avatarId: avatarId });
}
