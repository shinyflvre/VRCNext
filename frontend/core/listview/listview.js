const LV_PAGE_SIZES = [10, 15, 20, 25, 50, 100];

function lvPageSize(key) {
    try {
        const v = parseInt(localStorage.getItem('vrcn_lv_size_' + key), 10);
        return LV_PAGE_SIZES.includes(v) ? v : 25;
    } catch { return 25; }
}

function lvSetPageSize(key, value, onChange) {
    const n = parseInt(value, 10);
    if (!LV_PAGE_SIZES.includes(n)) return;
    try { localStorage.setItem('vrcn_lv_size_' + key, String(n)); } catch {}
    if (typeof onChange === 'function') onChange();
}

function lvPageSizeSelectHtml(key, onChangeFn) {
    const cur = lvPageSize(key);
    const opts = LV_PAGE_SIZES.map(n => `<option value="${n}"${n === cur ? ' selected' : ''}>${n}</option>`).join('');
    return `<select class="vrcn-dropdown tl-page-size" onchange="${onChangeFn}(this.value)" title="${esc(t('timeline.page_size', 'Entries per page'))}">${opts}</select>`;
}

function lvPaginator(key, page, totalPages, onPageFn, total, onSizeFn) {
    const countHtml = `<span style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx2);padding:0 8px;">${total.toLocaleString()} total</span>`;
    const bar = buildPaginator(page, totalPages, onPageFn, countHtml);
    return lvPageSizeSelectHtml(key, onSizeFn) + (bar || countHtml);
}

function lvViewMode(key) {
    return localStorage.getItem('vrcn_lv_view_' + key) === 'list' ? 'list' : 'grid';
}

function lvSetViewMode(key, mode) {
    localStorage.setItem('vrcn_lv_view_' + key, mode === 'list' ? 'list' : 'grid');
}

function lvReady() {
    return typeof tlTableHtml === 'function' && typeof tlTableRow === 'function';
}


function lvKeepScroll(startEl, render) {
    const saved = [];
    let n = startEl instanceof Element ? startEl : null;
    while (n && n !== document.body) {
        if (n.scrollTop > 0 || n.scrollLeft > 0) saved.push([n, n.scrollTop, n.scrollLeft]);
        n = n.parentElement;
    }
    const inner = [];
    if (startEl instanceof Element) {
        const seen = {};
        startEl.querySelectorAll('*').forEach(d => {
            const cls = typeof d.className === 'string' ? d.className : '';
            const idx = seen[cls] = (seen[cls] ?? -1) + 1;
            if (cls && (d.scrollTop > 0 || d.scrollLeft > 0))
                inner.push([cls, idx, d.scrollTop, d.scrollLeft]);
        });
    }
    render();
    saved.forEach(([el, top, left]) => { el.scrollTop = top; el.scrollLeft = left; });
    if (startEl instanceof Element && inner.length) {
        const seen = {};
        const byKey = {};
        startEl.querySelectorAll('*').forEach(d => {
            const cls = typeof d.className === 'string' ? d.className : '';
            const idx = seen[cls] = (seen[cls] ?? -1) + 1;
            if (cls) byKey[cls + '|' + idx] = d;
        });
        inner.forEach(([cls, idx, top, left]) => {
            const d = byKey[cls + '|' + idx];
            if (d) { d.scrollTop = top; d.scrollLeft = left; }
        });
    }
}

function lvSort(list, listId, valueFn) {
    const { field, dir } = tlTableSortField(listId);
    const mul = dir === 'asc' ? 1 : -1;
    return [...list].sort((a, b) => {
        const va = valueFn(a, field), vb = valueFn(b, field);
        if (typeof va === 'number' && typeof vb === 'number') return (va - vb) * mul;
        return String(va ?? '').localeCompare(String(vb ?? '')) * mul;
    });
}

function lvDuration(seconds) {
    const s = Number(seconds) || 0;
    const d = Math.floor(s / 86400);
    const h = Math.floor((s % 86400) / 3600);
    const m = Math.floor((s % 3600) / 60);
    const ds = t('timespent.unit.day_short', 'd');
    const hs = t('timespent.unit.hour_short', 'h');
    const ms = t('timespent.unit.minute_short', 'm');
    const parts = [];
    if (d > 0) parts.push(`${d}${ds}`);
    if (h > 0) parts.push(`${h}${hs}`);
    if (m > 0 && d === 0) parts.push(`${m}${ms}`);
    return parts.length ? parts.join(' ') : '';
}


function lvDateTime(v) {
    if (!v) return '';
    const d = new Date(v);
    return isNaN(d) ? '' : `${fmtShortDate(d)} | ${fmtTime(d)}`;
}

function lvIcon(url, name, round) {
    const cls = 'lv-icon' + (round ? ' lv-icon-round' : '');
    return url
        ? `<span class="${cls}" style="background-image:url('${cssUrl(imgThumb(url, 96))}')"></span>`
        : `<span class="${cls} lv-icon-letter">${esc((name || '?')[0].toUpperCase())}</span>`;
}

function lvScrollBox(target) {
    let el = target instanceof Element ? target : null;
    while (el && el !== document.body) {
        if (el.scrollWidth > el.clientWidth + 1) {
            const ox = getComputedStyle(el).overflowX;
            if (ox === 'auto' || ox === 'scroll') return el;
        }
        el = el.parentElement;
    }
    return null;
}

document.addEventListener('wheel', e => {
    if (!e.altKey || e.ctrlKey) return;
    const box = lvScrollBox(e.target);
    if (!box) return;
    e.preventDefault();
    box.scrollLeft += (e.deltaY || e.deltaX);
}, { passive: false });

const _lvEditConfigs = {};
let _lvEditBound = false;

function lvEditRegister(key, cfg) {
    _lvEditConfigs[key] = cfg;
    if (_lvEditBound) return;
    _lvEditBound = true;
    document.addEventListener('click', e => {
        for (const k in _lvEditConfigs) {
            const c = _lvEditConfigs[k];
            if (!c.isActive()) continue;
            const row = e.target.closest(`tr.tl-list-row[${c.attr}]`);
            if (!row) continue;
            e.preventDefault();
            e.stopPropagation();
            const id = row.getAttribute(c.attr);
            c.toggle(id);
            lvEditDecorateRow(row, c.isSelected(id));
            if (c.onChange) c.onChange();
            return;
        }
    }, true);
}

function lvEditCheckHtml(selected) {
    return selected
        ? '<span class="msi lv-row-check-on">check_circle</span>'
        : '<span class="msi lv-row-check-off">radio_button_unchecked</span>';
}

function lvEditDecorateRow(row, selected) {
    const cell = row.querySelector('td');
    if (cell) {
        let chk = cell.querySelector('.lv-row-check');
        if (!chk) {
            cell.insertAdjacentHTML('afterbegin', '<span class="lv-row-check"></span>');
            chk = cell.querySelector('.lv-row-check');
        }
        chk.innerHTML = lvEditCheckHtml(selected);
    }
    row.classList.toggle('lv-row-selected', selected);
}

function lvEditDecorateList(rootEl, key) {
    const c = _lvEditConfigs[key];
    if (!rootEl || !c) return;
    const on = c.isActive();
    rootEl.querySelectorAll(`tr.tl-list-row[${c.attr}]`).forEach(row => {
        if (!on) {
            row.querySelector('.lv-row-check')?.remove();
            row.classList.remove('lv-row-selected');
            return;
        }
        lvEditDecorateRow(row, c.isSelected(row.getAttribute(c.attr)));
    });
    rootEl.classList.toggle('lv-edit', on);
}
