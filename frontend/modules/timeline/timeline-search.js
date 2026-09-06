let _tlSearchChip = null;
let _tlSearchNavIdx = -1;

const TL_SEARCH_SOURCES = [
    {
        key: 'friends',
        labelKey: 'search.section.friends',
        label: 'Friends',
        getData: () => (typeof vrcFriendsData !== 'undefined' && Array.isArray(vrcFriendsData)) ? vrcFriendsData : [],
        match: (f, q) => (f.displayName || '').toLowerCase().includes(q)
                      || (f.username || f.userName || '').toLowerCase().includes(q),
        getId:   f => f.id || '',
        getName: f => f.displayName || '?',
        getSub:  f => (typeof statusLabel === 'function')
            ? (f.statusDescription || statusLabel(f.status))
            : (f.statusDescription || f.status || ''),
        getImg:  f => f.image || '',
        renderAvatar: f => {
            const presence = f.presence === 'web' ? 'web' : (!f.presence || f.presence === 'offline' ? 'offline' : 'online');
            const statusCls = presence === 'offline' ? 's-offline'
                : (typeof statusDotClass === 'function' ? statusDotClass(f.status) : 's-offline');
            const dotCls = presence === 'web' ? 'vrc-status-ring' : 'vrc-status-dot';
            const img = f.image
                ? `<img class="ss-item-img" src="${esc(imgThumb(f.image, 64))}" onerror="this.style.visibility='hidden'">`
                : `<div class="ss-item-img-placeholder">${esc((f.displayName || '?')[0].toUpperCase())}</div>`;
            return `<div class="vrc-friend-avatar-wrap">${img}<span class="${dotCls} ${statusCls} vrc-friend-status-badge"></span></div>`;
        },
    },
    {
        key: 'worlds',
        labelKey: 'search.remote.worlds',
        label: 'Worlds',
        getData: () => {
            const seen = {};
            const out = [];
            const push = arr => (Array.isArray(arr) ? arr : []).forEach(w => {
                if (!w || !w.id || seen[w.id]) return;
                seen[w.id] = 1;
                out.push(w);
            });
            push(typeof _visitedWorldsData !== 'undefined' ? _visitedWorldsData : []);
            push(typeof favWorldsData !== 'undefined' ? favWorldsData : []);
            push(typeof dashWorldCache !== 'undefined' ? Object.values(dashWorldCache) : []);
            return out;
        },
        match: (w, q) => (w.name || '').toLowerCase().includes(q),
        getId:   w => w.id || '',
        getName: w => w.name || '?',
        getSub:  w => w.authorName || '',
        getImg:  w => w.thumbnailImageUrl || w.imageUrl || '',
        renderAvatar: w => {
            const src = w.thumbnailImageUrl || w.imageUrl || '';
            return src
                ? `<img class="ss-item-img" src="${esc(imgThumb(src, 64))}" onerror="this.style.visibility='hidden'">`
                : `<div class="ss-item-img-placeholder"><span class="msi" style="font-size:14px;">public</span></div>`;
        },
    },
];

function tlRunSearchFilter() {
    if (typeof tlMode === 'undefined') return;
    if (tlMode === 'gamelog') renderGameLog();
    else if (tlMode === 'friends') filterFriendTimeline();
    else filterTimeline();
}

function _tlSearchEnabled() {
    return typeof tlMode !== 'undefined' && (tlMode === 'personal' || tlMode === 'friends');
}

function _tlSearchSource(key) {
    return TL_SEARCH_SOURCES.find(s => s.key === key) || null;
}

function _tlSearchHideDropdown() {
    const d = document.getElementById('tlSearchDropdown');
    if (d) { d.classList.remove('ss-visible'); d.innerHTML = ''; }
    _tlSearchNavIdx = -1;
}

function _tlSearchRenderSuggestions() {
    const d = document.getElementById('tlSearchDropdown');
    if (!d) return;
    const q = (document.getElementById('tlSearchInput')?.value || '').trim().toLowerCase();
    if (_tlSearchChip || !_tlSearchEnabled() || q.length < 1) { _tlSearchHideDropdown(); return; }

    let html = '';
    TL_SEARCH_SOURCES.forEach(src => {
        const hits = src.getData()
            .filter(x => src.match(x, q))
            .sort((a, b) => {
                const an = src.getName(a).toLowerCase(), bn = src.getName(b).toLowerCase();
                const ap = an.startsWith(q) ? 0 : 1, bp = bn.startsWith(q) ? 0 : 1;
                return ap !== bp ? ap - bp : an.localeCompare(bn);
            })
            .slice(0, 5);
        if (!hits.length) return;
        html += `<div class="ss-section-hdr">${esc(t(src.labelKey, src.label))}</div>`;
        html += hits.map(x => {
            const sub = src.getSub(x);
            return `<div class="ss-item" onmousedown="event.preventDefault();tlSearchPick('${jsq(src.key)}','${jsq(src.getId(x))}')">
                ${src.renderAvatar(x)}
                <div class="ss-item-info">
                    <div class="ss-item-name">${esc(src.getName(x))}</div>
                    ${sub ? `<div class="ss-item-sub">${esc(sub)}</div>` : ''}
                </div>
            </div>`;
        }).join('');
    });

    if (!html) { _tlSearchHideDropdown(); return; }
    d.innerHTML = html;
    d.classList.add('ss-visible');
    _tlSearchNavIdx = -1;
}

function _tlSearchRenderChip() {
    const chip = document.getElementById('tlSearchChip');
    const wrap = document.getElementById('tlSearchWrap');
    if (!chip || !wrap) return;
    if (!_tlSearchChip) {
        chip.style.display = 'none';
        chip.innerHTML = '';
        wrap.classList.remove('tl-has-chip');
        return;
    }
    const c = _tlSearchChip;
    const av = c.image
        ? `<img class="tl-search-chip-img" src="${esc(imgThumb(c.image, 48))}" onerror="this.style.display='none'">`
        : `<span class="msi tl-search-chip-icon">${c.sourceKey === 'worlds' ? 'public' : 'person'}</span>`;
    chip.innerHTML = `${av}<span class="tl-search-chip-name">${esc(c.name)}</span>
        <button type="button" class="tl-search-chip-x" onclick="tlSearchClearChip()" title="${esc(t('common.clear', 'Clear'))}"><span class="msi">close</span></button>`;
    chip.style.display = '';
    wrap.classList.add('tl-has-chip');
}

function tlSearchPick(sourceKey, id) {
    const src = _tlSearchSource(sourceKey);
    if (!src) return;
    const item = src.getData().find(x => src.getId(x) === id);
    if (!item) return;
    const name = src.getName(item);
    const input = document.getElementById('tlSearchInput');
    if (input) input.value = name;
    _tlSearchChip = { sourceKey, id, name, image: src.getImg(item) };
    _tlSearchRenderChip();
    _tlSearchHideDropdown();
    tlRunSearchFilter();
}

function tlSearchSetChip(sourceKey, id, name, image) {
    const input = document.getElementById('tlSearchInput');
    if (input) input.value = name || '';
    _tlSearchChip = { sourceKey, id: id || '', name: name || '?', image: image || '' };
    _tlSearchRenderChip();
    _tlSearchHideDropdown();
    tlRunSearchFilter();
}

function openTimelineWithChip(mode, sourceKey, id, name, image, closeFn) {
    const srcEl = window._tlMoreEl || null;
    window._tlMoreEl = null;
    const windowed = !!(srcEl && typeof _wmOwned === 'function' && _wmOwned(srcEl));
    if (closeFn && !windowed && typeof window[closeFn] === 'function') { try { window[closeFn](); } catch (e) { console.warn('openTimelineWithChip close failed', e); } }
    if (typeof showTab === 'function') showTab(12);
    if (typeof setTlMode === 'function') setTlMode(mode);
    if (mode === 'personal' && typeof setTlFilter === 'function') setTlFilter('all');
    if (mode === 'friends' && typeof setFtFilter === 'function') setFtFilter('all');
    tlSearchSetChip(sourceKey, id, name, image);
}

function tlSearchClearChip() {
    _tlSearchChip = null;
    const input = document.getElementById('tlSearchInput');
    if (input) input.value = '';
    _tlSearchRenderChip();
    _tlSearchHideDropdown();
    input?.focus();
    tlRunSearchFilter();
}

function tlSearchInputChanged() {
    _tlSearchRenderSuggestions();
    tlRunSearchFilter();
}

function tlSearchKeydown(ev) {
    const d = document.getElementById('tlSearchDropdown');
    const items = d ? [...d.querySelectorAll('.ss-item')] : [];
    if (ev.key === 'Escape') {
        if (items.length) { ev.preventDefault(); _tlSearchHideDropdown(); }
        return;
    }
    if (ev.key === 'Backspace' && _tlSearchChip) { ev.preventDefault(); tlSearchClearChip(); return; }
    if (!items.length) return;
    if (ev.key === 'ArrowDown' || ev.key === 'ArrowUp') {
        ev.preventDefault();
        _tlSearchNavIdx += ev.key === 'ArrowDown' ? 1 : -1;
        if (_tlSearchNavIdx < 0) _tlSearchNavIdx = items.length - 1;
        if (_tlSearchNavIdx >= items.length) _tlSearchNavIdx = 0;
        items.forEach((el, i) => el.classList.toggle('ss-focused', i === _tlSearchNavIdx));
        items[_tlSearchNavIdx].scrollIntoView({ block: 'nearest' });
    } else if (ev.key === 'Enter' && _tlSearchNavIdx >= 0) {
        ev.preventDefault();
        items[_tlSearchNavIdx].dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));
    }
}

document.addEventListener('mousedown', e => {
    const wrap = document.getElementById('tlSearchWrap');
    if (wrap && !wrap.contains(e.target)) _tlSearchHideDropdown();
});
