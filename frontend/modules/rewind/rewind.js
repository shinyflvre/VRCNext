// VRCN Rewind - year in review, matched to the app

let _rwState = null;

function rwT(key, fb) { return typeof t === 'function' ? t('rewind.' + key, fb) : fb; }
function rwTf(key, vars, fb) { return typeof tf === 'function' ? tf('rewind.' + key, vars, fb) : fb; }

function _rwNum(n) { return (n || 0).toLocaleString(typeof getLanguageLocale === 'function' ? getLanguageLocale() : undefined); }

function _rwHidden() {
    try { return new Set(JSON.parse(localStorage.getItem('vrcnext_hidden') || '[]')); } catch { return new Set(); }
}

function _rwDur(min) {
    min = Math.round(min || 0);
    if (min < 60) return min + 'm';
    const h = Math.floor(min / 60), m = min % 60;
    return m ? `${h}h ${m}m` : `${h}h`;
}

function _rwFact(icon, label, value) {
    return `<div class="rw-fact-card">
        <span class="msi rw-fact-icon">${icon}</span>
        <div><div class="rw-inline-label">${esc(label)}</div>
        <div class="rw-inline-name">${esc(value)}</div></div>
    </div>`;
}

function _rwMonthName(m) {
    if (!m || m < 1 || m > 12) return '';
    try { return new Date(2026, m - 1, 1).toLocaleDateString(typeof getLanguageLocale === 'function' ? getLanguageLocale() : undefined, { month: 'long' }); }
    catch { return String(m); }
}

function handleRewindData(data) {
    if (!data) return;
    if (!data.hasData) {
        if (!data.auto) showToast(false, rwT('nodata', 'Not enough data yet for a Rewind. Explore more worlds and come back!'));
        return;
    }
    openRewindModal(data);
}

function _rwAvatar(url, name) {
    return url
        ? `<div class="rw-av" style="background-image:url('${cssUrl(url)}')"></div>`
        : `<div class="rw-av rw-av-letter">${esc((name || '?')[0].toUpperCase())}</div>`;
}

function _rwHeader(kicker, title, sub, titleCls) {
    return `<div class="rw-header">
        ${kicker ? `<div class="rw-kicker">${esc(kicker)}</div>` : ''}
        <div class="rw-title ${titleCls || ''}">${esc(title)}</div>
        ${sub ? `<div class="rw-sub">${esc(sub)}</div>` : ''}
    </div>`;
}

function _rwStat(num, label) {
    return `<div class="rw-stat"><div class="rw-stat-num">${num}</div><div class="rw-stat-lbl">${esc(label)}</div></div>`;
}

function _rwBuildPages(d) {
    const pages = [];
    const tt = d.totals || {};
    const hidden = _rwHidden();
    const bfPics = (d.bestFriendPhotos || []).filter(p => !hidden.has(p.path));
    const wPics  = (d.worldPhotos || []).filter(p => !hidden.has(p.path));

    pages.push(`<div class="rw-intro-fg rw-on-media">${_rwHeader('VRCN Rewind ' + d.year,
        rwTf('intro.title', { year: d.year }, `Your VRCN Rewind ${d.year} is ready!`),
        rwT('intro.sub', "Let's look back at your year in VRChat."), 'rw-title-lg')}</div>`);

    pages.push(`${_rwHeader('', rwT('totals.title', 'Your year in numbers'))}
        <div class="rw-content"><div class="rw-stat-grid">
            ${_rwStat(_rwNum(tt.hours), rwT('totals.hours', 'Hours in VRChat'))}
            ${_rwStat(_rwNum(tt.worlds), rwT('totals.worlds', 'Worlds visited'))}
            ${_rwStat(_rwNum(tt.instances), rwT('totals.instances', 'Instances joined'))}
            ${_rwStat(_rwNum(tt.photos), rwT('totals.photos', 'Photos taken'))}
            ${_rwStat(_rwNum(tt.peopleMet), rwT('totals.people', 'People met'))}
            ${_rwStat(_rwNum((d.newFriends || {}).count), rwT('totals.newfriends', 'New friends'))}
        </div></div>`);

    if (d.bestFriend) {
        const bf = d.bestFriend;
        const sw = d.sharedWorld ? `<div class="rw-inline-card">
            ${d.sharedWorld.thumb ? `<div class="rw-mini-thumb" style="background-image:url('${cssUrl(d.sharedWorld.thumb)}')"></div>` : ''}
            <div><div class="rw-inline-label">${esc(rwT('sharedworld.title', 'Your happy place together'))}</div>
            <div class="rw-inline-name">${esc(d.sharedWorld.name)}</div></div>
        </div>` : '';
        pages.push(`<div class="rw-header"><div class="rw-kicker">${esc(rwT('bestfriend.title', 'Your favorite person'))}</div></div>
            ${_rwAvatar(bf.image, bf.name)}
            <div class="rw-title">${esc(bf.name)}</div>
            <div class="rw-line">${esc(rwTf('bestfriend.line', { name: bf.name }, `You and ${bf.name} have been through so many memories together!`))}</div>
            <div class="rw-content"><div class="rw-stat-grid rw-grid-2">
                ${_rwStat(_rwNum(bf.hours) + 'h', rwT('bestfriend.together', 'together'))}
                ${_rwStat(_rwNum(bf.meets), rwT('bestfriend.reunions', 'reunions'))}
            </div></div>
            ${sw}`);
    }

    if (bfPics.length) {
        const imgs = bfPics.slice(0, 16).map(p => `<div class="rw-img" style="background-image:url('${cssUrl(p.url)}')"></div>`).join('');
        pages.push(`${_rwHeader('', rwT('bfphotos.title', 'Moments you shared'))}
            <div class="rw-content rw-scroll"><div class="rw-img-grid-4">${imgs}</div></div>`);
    }

    if (d.newFriends && d.newFriends.count > 0) {
        const list = d.newFriends.list || [];
        const rows = list.map(f => `<div class="ts-item">
            ${f.image ? `<img class="ts-item-avatar" src="${esc(f.image)}" onerror="this.style.visibility='hidden'">` : `<div class="ts-item-avatar ts-avatar-placeholder"></div>`}
            <div class="ts-item-body"><div class="ts-item-name">${esc(f.name)}</div>
            <div class="ts-item-meta">${f.hours ? _rwNum(f.hours) + 'h ' + esc(rwT('bestfriend.together', 'together')) : '&nbsp;'}</div></div>
            ${f.hours ? `<div class="ts-item-time">${_rwNum(f.hours)}h</div>` : ''}
        </div>`).join('');
        pages.push(`<div class="rw-header"><div class="rw-kicker">${esc(rwT('newfriends.title', 'New friends this year'))}</div>
            <div class="rw-title">${esc(rwTf('newfriends.line', { count: _rwNum(d.newFriends.count) }, `You made ${_rwNum(d.newFriends.count)} new friends!`))}</div></div>
            <div class="rw-content rw-scroll">${rows ? `<div class="ts-items">${rows}</div>` : ''}</div>`);
    }

    if (d.topFriends && d.topFriends.length) {
        const rows = d.topFriends.map((f, i) => `<div class="ts-item">
            <div class="ts-item-rank">#${i + 1}</div>
            ${f.image ? `<img class="ts-item-avatar" src="${esc(f.image)}" onerror="this.style.visibility='hidden'">` : `<div class="ts-item-avatar ts-avatar-placeholder"></div>`}
            <div class="ts-item-body"><div class="ts-item-name">${esc(f.name)}</div>
            <div class="ts-item-meta">${_rwNum(f.meets)} ${esc(rwT('bestfriend.reunions', 'reunions'))}</div></div>
            <div class="ts-item-time">${_rwNum(f.hours)}h</div>
        </div>`).join('');
        pages.push(`${_rwHeader('', rwT('topfriends.title', 'Your top 10 friends'))}
            <div class="rw-content rw-scroll"><div class="ts-items">${rows}</div></div>`);
    }

    if (d.topWorlds && d.topWorlds.length) {
        const maxH = Math.max.apply(null, d.topWorlds.map(w => w.hours || 0).concat([1]));
        const rows = d.topWorlds.map((w, i) => {
            const pct = Math.max(3, Math.round((w.hours || 0) / maxH * 100));
            const thumb = w.thumb
                ? `<img class="ts-item-thumb" src="${esc(w.thumb)}" onerror="this.style.visibility='hidden'">`
                : `<div class="ts-item-thumb ts-thumb-placeholder"></div>`;
            return `<div class="ts-item">
                <div class="ts-item-rank">#${i + 1}</div>
                ${thumb}
                <div class="ts-item-body"><div class="ts-item-name">${esc(w.name)}</div>
                <div class="ts-item-meta">${_rwNum(w.visits)} ${esc(rwT('visits', 'visits'))}</div>
                <div class="ts-bar-wrap"><div class="ts-bar" style="width:${pct}%"></div></div></div>
                <div class="ts-item-time">${_rwNum(w.hours)}h</div>
            </div>`;
        }).join('');
        pages.push(`${_rwHeader('', rwT('topworlds.title', 'Your top worlds'))}
            <div class="rw-content rw-scroll"><div class="ts-items">${rows}</div></div>`);
    }

    if (wPics.length) {
        const cells = wPics.slice(0, 16).map(p => `<figure class="rw-figure">
            <div class="rw-img" style="background-image:url('${cssUrl(p.url)}')"></div>
            ${p.world ? `<figcaption class="rw-figcap">${esc(p.world)}</figcaption>` : ''}
        </figure>`).join('');
        pages.push(`${_rwHeader('', rwT('worldphotos.title', 'So many memories in those worlds'))}
            <div class="rw-content rw-scroll"><div class="rw-fig-grid-4">${cells}</div></div>`);
    }

    const fav = d.favoriteAvatar;
    const bm = d.busiestMonth || {};
    const facts = [];
    if (fav && fav.name) facts.push(_rwFact('checkroom', rwT('funfacts.avatar', 'Favorite avatar'), fav.name));
    if (bm.month > 0) facts.push(_rwFact('calendar_month', rwT('funfacts.month', 'Busiest month'), _rwMonthName(bm.month)));
    if (d.longestSessionMin > 0) facts.push(_rwFact('timer', rwT('funfacts.longest_session', 'Longest session'), _rwDur(d.longestSessionMin)));
    if (d.topPhotoWorld) facts.push(_rwFact('photo_camera', rwT('funfacts.top_photo_world', 'Most photographed world'), d.topPhotoWorld));
    if (d.activeDays > 0) facts.push(_rwFact('event_available', rwT('funfacts.active_days', 'Active days'), _rwNum(d.activeDays)));
    if (typeof d.nightOwlHour === 'number' && d.nightOwlHour >= 0) facts.push(_rwFact('bedtime', rwT('funfacts.night_owl', 'Favorite hour'), String(d.nightOwlHour).padStart(2, '0') + ':00'));
    if (d.avatarSwitches > 0) facts.push(_rwFact('styler', rwT('funfacts.avatar_switches', 'Avatar switches'), _rwNum(d.avatarSwitches)));
    if (d.urlsShared > 0) facts.push(_rwFact('link', rwT('funfacts.urls_shared', 'Videos & URLs'), _rwNum(d.urlsShared)));
    if (facts.length) {
        pages.push(`${_rwHeader('', rwT('funfacts.title', 'Fun facts'))}
            <div class="rw-content rw-scroll"><div class="rw-fact-grid">${facts.join('')}</div></div>`);
    }

    if (d.secrets && d.secrets.length) {
        const s = d.secrets[0];
        const more = d.secrets.slice(1).map(x => `<span class="rw-chip">${esc(x.name)} &middot; ${_rwNum(x.hours)}h</span>`).join('');
        pages.push(`<div class="rw-header"><div class="rw-kicker">${esc(rwT('secrets.title', 'Now for some secrets...'))}</div></div>
            ${_rwAvatar(s.image, s.name)}
            <div class="rw-title">${esc(s.name)}</div>
            <div class="rw-line">${esc(rwTf('secrets.line', { name: s.name, meets: _rwNum(s.meets), hours: _rwNum(s.hours) }, `You have seen ${s.name} ${_rwNum(s.meets)} times and spent ${_rwNum(s.hours)}h together, but you are still not friends. Maybe add them?`))}</div>
            ${more ? `<div class="rw-content"><div class="rw-chips">${more}</div></div>` : ''}`);
    }

    pages.push(_rwHeader('VRCN Rewind ' + d.year,
        rwTf('outro.thanks', { name: d.selfName || 'you' }, `Thanks for an amazing year, ${d.selfName || 'you'}!`),
        rwT('outro.sub', 'See you next year.'), 'rw-title-lg'));

    return pages;
}

function openRewindModal(d) {
    closeRewind();
    const pages = _rwBuildPages(d);
    _rwState = { pages, idx: 0, auto: !!d.auto };
    if (d.auto) sendToCS({ action: 'rewindSeen' });

    const hidden = _rwHidden();
    const slides = (d.slideshow || []).filter(s => !hidden.has(s.path)).slice(0, 14);
    const bgHtml = `<div class="rw-bg" id="rwBg">
        ${slides.map((s, i) => `<div class="rw-slide${i === 0 ? ' active' : ''}" style="background-image:url('${cssUrl(s.url)}')"></div>`).join('')}
        <div class="rw-bg-scrim"></div>
    </div>`;

    const ov = document.createElement('div');
    ov.id = 'rwOverlay';
    ov.className = 'modal-overlay';
    ov.innerHTML = `<div class="modal-box wide rw-box">
        ${bgHtml}
        <div class="rw-head">
            <span class="rw-brand">VRCN</span>
            <div class="rw-dots" id="rwDots"></div>
            <button class="vrcn-icon-button" onclick="closeRewind()" title="${esc(t('common.close', 'Close'))}"><span class="msi" style="font-size:18px;">close</span></button>
        </div>
        <div class="rw-body" id="rwBody"></div>
        <div class="rw-foot">
            <button class="vrcn-button" id="rwPrevBtn" onclick="rwPrev()"><span class="msi" style="font-size:16px;">arrow_back</span> ${esc(rwT('prev', 'Back'))}</button>
            <div class="rw-count" id="rwCount"></div>
            <button class="vrcn-button vrcn-btn-primary" id="rwNextBtn" onclick="rwNext()"></button>
        </div>
    </div>`;
    document.body.appendChild(ov);
    ov.addEventListener('mousedown', e => { if (e.target === ov) closeRewind(); });
    _rwStartSlideshow();
    _rwRenderPage();
}

function _rwRenderPage() {
    if (!_rwState) return;
    const body = document.getElementById('rwBody');
    const dots = document.getElementById('rwDots');
    const count = document.getElementById('rwCount');
    const prev = document.getElementById('rwPrevBtn');
    const next = document.getElementById('rwNextBtn');
    if (!body) return;
    const i = _rwState.idx, n = _rwState.pages.length;

    body.innerHTML = `<div class="rw-page">${_rwState.pages[i]}</div>`;
    const bg = document.getElementById('rwBg');
    if (bg) bg.classList.toggle('rw-bg-blur', i !== 0);
    if (dots) dots.innerHTML = _rwState.pages.map((_, k) =>
        `<span class="rw-dot ${k === i ? 'active' : (k < i ? 'done' : '')}"></span>`).join('');
    if (count) count.textContent = `${i + 1} / ${n}`;
    if (prev) prev.style.visibility = i === 0 ? 'hidden' : '';
    if (next) {
        const last = i >= n - 1;
        next.innerHTML = last
            ? `${esc(rwT('finish', 'Finish'))} <span class="msi" style="font-size:16px;">check</span>`
            : `${esc(rwT('next', 'Next'))} <span class="msi" style="font-size:16px;">arrow_forward</span>`;
    }
}

function _rwStartSlideshow() {
    if (_rwState && _rwState.slideTimer) { clearInterval(_rwState.slideTimer); _rwState.slideTimer = null; }
    const ss = document.getElementById('rwBg');
    if (!ss || !_rwState) return;
    const slides = ss.querySelectorAll('.rw-slide');
    if (slides.length < 2) return;
    let si = 0;
    _rwState.slideTimer = setInterval(() => {
        slides[si].classList.remove('active');
        si = (si + 1) % slides.length;
        slides[si].classList.add('active');
    }, 4500);
}

function rwNext() {
    if (!_rwState) return;
    if (_rwState.idx >= _rwState.pages.length - 1) { closeRewind(); return; }
    _rwState.idx++;
    _rwRenderPage();
}

function rwPrev() {
    if (!_rwState || _rwState.idx <= 0) return;
    _rwState.idx--;
    _rwRenderPage();
}

function closeRewind() {
    if (_rwState && _rwState.slideTimer) clearInterval(_rwState.slideTimer);
    const ov = document.getElementById('rwOverlay');
    if (ov) ov.remove();
    _rwState = null;
}

document.addEventListener('keydown', e => {
    if (!_rwState) return;
    if (e.key === 'Escape') closeRewind();
    else if (e.key === 'ArrowRight') rwNext();
    else if (e.key === 'ArrowLeft') rwPrev();
});
