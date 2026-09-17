let _mnetGraph   = null;
let _mnetPending = false;

const MNET_SPRITE_PX  = 96;
const MNET_RING_GAP   = 34;
const MNET_WORLD_RGB  = { r: 45, g: 212, b: 140, raw: '#2DD48C' };
const MNET_NF_RGB     = { r: 255, g: 138, b: 61, raw: 'rgb(255,138,61)' };

function initMeetNetwork() {
    const canvas = document.getElementById('mnetCanvas');
    if (!canvas) return;
    if (_mnetGraph) { _mnetGraph.resize(); return; }
    _mnetGraph = new MeetGraph(canvas);
    _mnetRequestData();
}

function _mnetRequestData() {
    if (_mnetPending) return;
    _mnetPending = true;
    const prog = document.getElementById('mnetProgress');
    if (prog) prog.style.display = 'flex';
    const empty = document.getElementById('mnetEmpty');
    if (empty) empty.style.display = 'none';
    sendToCS({ action: 'getMeetNetwork' });
}

function meetNetworkDataLoaded(payload) {
    _mnetPending = false;
    const prog = document.getElementById('mnetProgress');
    if (prog) prog.style.display = 'none';
    if (!_mnetGraph) return;
    const people = (payload && payload.people) || [];
    const empty = document.getElementById('mnetEmpty');
    if (empty) empty.style.display = people.length ? 'none' : 'block';
    _mnetGraph.setPeople(people);
}

function meetNetworkWorldsLoaded(payload) {
    if (!_mnetGraph || !payload || !payload.userId) return;
    _mnetGraph.onWorldsReceived(payload.userId, payload.worlds || []);
}

function meetNetworkRefresh() {
    if (_mnetGraph) { _mnetGraph.destroy(); _mnetGraph = null; }
    _mnetPending = false;
    const inp = document.getElementById('mnetSearchInput');
    if (inp) inp.value = '';
    initMeetNetwork();
}

function meetNetworkSearch(value) {
    if (_mnetGraph) _mnetGraph.setSearch(value);
}

function meetNetworkResetView() {
    if (!_mnetGraph) return;
    _mnetGraph.fitView();
    _mnetGraph.render();
}

document.documentElement.addEventListener('tabchange', () => {
    const tab29 = document.getElementById('tab29');
    if (tab29 && tab29.classList.contains('active')) {
        initMeetNetwork();
        if (_mnetGraph) _mnetGraph.reloadImages();
    } else if (_mnetGraph) {
        _mnetGraph.unloadImages();
    }
});

class MeetGraph {
    constructor(canvas) {
        this.canvas = canvas;
        this.ctx    = canvas.getContext('2d');
        this.nodes  = [];
        this.worlds = [];
        this.nodeMap = {};
        this._worldCache = {};

        this.tx = 0; this.ty = 0; this.scale = 1;
        this.dragging = null;
        this.selected = null;
        this.hovered  = null;
        this.hoveredWorld = null;
        this.searchTimer = null;

        this._anim = 1;
        this._animRaf = null;
        this._renderPending = false;
        this._friendIds = new Set();

        this._bindEvents();
        this._resizeObserver = new ResizeObserver(() => this.resize());
        this._resizeObserver.observe(canvas);
        this.resize();
    }

    resize() {
        const W = this.canvas.offsetWidth;
        const H = this.canvas.offsetHeight;
        if (!W || !H) return;
        if (this.canvas.width === W && this.canvas.height === H) return;
        this.canvas.width  = W;
        this.canvas.height = H;
        this.render();
    }

    setPeople(people) {
        const friends = (typeof vrcFriendsData !== 'undefined') ? vrcFriendsData : [];
        this._friendIds = new Set(friends.map(f => f.id));
        this.nodes = [];
        this.nodeMap = {};
        this.worlds = [];
        this.selected = null;
        this.hovered = null;
        this.hoveredWorld = null;
        this._updateInfoCard();

        const maxM = people.length ? Math.max(1, people[0].meets) : 1;
        people.forEach((p, i) => {
            const node = {
                id:          p.userId,
                displayName: p.displayName || p.userId,
                image:       p.image || '',
                meets:       p.meets || 0,
                isFriend:    this._friendIds.has(p.userId),
                x: 0, y: 0,
                r: 7 + 15 * Math.sqrt((p.meets || 0) / maxM),
                imgEl: null,
            };
            this.nodes.push(node);
            this.nodeMap[node.id] = i;
            this._loadImage(node);
        });

        this._layoutPeople();
        this.fitView();
        this.render();
    }

    _loadImage(nd) {
        if (!nd.image || nd._imgLoading || this._imagesUnloaded) return;
        nd._imgLoading = true;
        const img = new Image();
        img.src = imgThumb(nd.image, 128);
        img.onload  = () => { nd._imgLoading = false; nd.imgEl = img; this._sprite(nd); nd.imgEl = null; this._scheduleRender(); };
        img.onerror = () => { nd._imgLoading = false; };
    }

    unloadImages() {
        let count = 0;
        const drop = nd => {
            if (nd.imgEl || nd._spr) count++;
            nd.imgEl = null;
            nd._spr  = null;
        };
        this.nodes.forEach(drop);
        this.worlds.forEach(drop);
        this._imagesUnloaded = true;
        if (count > 0 && typeof addLog === 'function')
            addLog(`[Unload] Unloaded ${count} image${count !== 1 ? 's' : ''} from Tab 29 from memory.`, 'info');
    }

    reloadImages() {
        if (!this._imagesUnloaded) return;
        this._imagesUnloaded = false;
        this.nodes.forEach(nd => { if (!nd._spr) this._loadImage(nd); });
        this.worlds.forEach(w => { if (!w._spr) this._loadImage(w); });
    }

    _sprite(nd) {
        if (nd._spr) return nd._spr;
        if (!nd.imgEl) { this._loadImage(nd); return null; }
        const S  = MNET_SPRITE_PX;
        const cv = document.createElement('canvas');
        cv.width = S; cv.height = S;
        const c = cv.getContext('2d');
        c.beginPath();
        c.arc(S / 2, S / 2, S / 2, 0, Math.PI * 2);
        c.clip();
        const iw = nd.imgEl.naturalWidth || S, ih = nd.imgEl.naturalHeight || S;
        const side = Math.min(iw, ih);
        c.drawImage(nd.imgEl, (iw - side) / 2, (ih - side) / 2, side, side, 0, 0, S, S);
        nd._spr = cv;
        return cv;
    }

    _layoutPeople() {
        const n = this.nodes.length;
        if (!n) return;
        this.nodes[0].x = 0;
        this.nodes[0].y = 0;
        let idx = 1;
        let R = this.nodes[0].r + MNET_RING_GAP;
        while (idx < n) {
            const ringMaxR = this.nodes[idx].r;
            R += ringMaxR;
            const spacing  = ringMaxR * 2 + MNET_RING_GAP;
            const capacity = Math.max(1, Math.floor((2 * Math.PI * R) / spacing));
            const count    = Math.min(capacity, n - idx);
            const rot      = R * 0.37;
            for (let k = 0; k < count; k++, idx++) {
                const ang = (k / count) * Math.PI * 2 + rot;
                this.nodes[idx].x = R * Math.cos(ang);
                this.nodes[idx].y = R * Math.sin(ang);
            }
            R += ringMaxR + MNET_RING_GAP;
        }
    }

    _layoutWorlds(sel, worlds) {
        const out = [];
        if (!worlds.length) return out;
        const maxC = Math.max(1, worlds[0].meets);
        worlds.forEach(w => {
            out.push({
                id:    w.worldId,
                name:  w.worldName || w.worldId,
                image: w.worldThumb || '',
                meets: w.meets || 0,
                r: 9 + 17 * Math.sqrt((w.meets || 0) / maxC),
                dx: 0, dy: 0,
                imgEl: null,
            });
        });

        const person = this.nodes[sel];
        let idx = 0;
        let R = person.r + out[0].r + 70;
        while (idx < out.length) {
            const ringMaxR = out[idx].r;
            const spacing  = ringMaxR * 2 + 42;
            const capacity = Math.max(1, Math.floor((2 * Math.PI * R) / spacing));
            const count    = Math.min(capacity, out.length - idx);
            for (let k = 0; k < count; k++, idx++) {
                const ang = (k / count) * Math.PI * 2 - Math.PI / 2;
                out[idx].dx = R * Math.cos(ang);
                out[idx].dy = R * Math.sin(ang);
            }
            R += ringMaxR * 2 + 46;
        }
        out.forEach(w => this._loadImage(w));
        return out;
    }

    select(idx) {
        if (idx === null || idx === undefined || !this.nodes[idx]) {
            this.selected = null;
            this.worlds = [];
            this._updateInfoCard();
            this.render();
            return;
        }
        if (this.selected === idx) return;
        this.selected = idx;
        this.worlds = [];
        const uid = this.nodes[idx].id;
        const cached = this._worldCache[uid];
        if (cached) {
            this.worlds = this._layoutWorlds(idx, cached);
            this._startAnim();
        } else {
            sendToCS({ action: 'getMeetNetworkWorlds', userId: uid });
        }
        this._updateInfoCard();
        this._centerOn(idx);
        this.render();
    }

    onWorldsReceived(userId, worlds) {
        this._worldCache[userId] = worlds;
        if (this.selected === null || !this.nodes[this.selected] || this.nodes[this.selected].id !== userId) return;
        this.worlds = this._layoutWorlds(this.selected, worlds);
        this._updateInfoCard();
        this._startAnim();
    }

    _startAnim() {
        this._anim = 0;
        const t0 = performance.now();
        const step = now => {
            this._anim = Math.min(1, (now - t0) / 260);
            this.render();
            if (this._anim < 1) this._animRaf = requestAnimationFrame(step);
            else this._animRaf = null;
        };
        if (this._animRaf) cancelAnimationFrame(this._animRaf);
        this._animRaf = requestAnimationFrame(step);
    }

    _updateInfoCard() {
        const el = document.getElementById('mnetInfoCard');
        if (!el) return;
        const sel = this.selected !== null ? this.nodes[this.selected] : null;
        if (!sel) { el.style.display = 'none'; return; }
        const rows = [
            `<div class="mnet-info-name">${esc(sel.displayName)}</div>`,
            `<div class="mnet-info-row"><span>${esc(t('meetnet.card_meets', 'Total Meets'))}</span><span>${sel.meets}</span></div>`,
        ];
        const cached = this._worldCache[sel.id];
        if (cached) {
            rows.push(`<div class="mnet-info-row"><span>${esc(t('meetnet.card_worlds', 'Worlds'))}</span><span>${cached.length}</span></div>`);
            if (cached.length) {
                const top = cached[0];
                rows.push(`<div class="mnet-info-row"><span>${esc(t('meetnet.card_top_world', 'Most met in'))}</span><span>${esc(top.worldName || top.worldId)}</span></div>`);
            }
        }
        el.innerHTML = rows.join('');
        el.style.display = 'flex';
    }

    setSearch(value) {
        clearTimeout(this.searchTimer);
        this.searchTimer = setTimeout(() => {
            const q = (value || '').trim().toLowerCase();
            if (!q) return;
            let best = -1, bestStarts = false;
            for (let i = 0; i < this.nodes.length; i++) {
                const name = (this.nodes[i].displayName || '').toLowerCase();
                if (!name.includes(q)) continue;
                const starts = name.startsWith(q);
                if (best === -1 || (starts && !bestStarts)) { best = i; bestStarts = starts; }
                if (starts) break;
            }
            if (best >= 0) this.select(best);
        }, 300);
    }

    fitView() {
        const W = this.canvas.width, H = this.canvas.height;
        if (!W || !H || !this.nodes.length) return;
        let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
        for (const nd of this.nodes) {
            const r = nd.r + 10;
            if (nd.x - r < minX) minX = nd.x - r;
            if (nd.x + r > maxX) maxX = nd.x + r;
            if (nd.y - r < minY) minY = nd.y - r;
            if (nd.y + r > maxY) maxY = nd.y + r;
        }
        const pad = 30;
        const sx = (W - pad * 2) / Math.max(1, maxX - minX);
        const sy = (H - pad * 2) / Math.max(1, maxY - minY);
        this.scale = Math.min(1.6, Math.max(0.05, Math.min(sx, sy)));
        this.tx = W / 2 - ((minX + maxX) / 2) * this.scale;
        this.ty = H / 2 - ((minY + maxY) / 2) * this.scale;
    }

    _centerOn(idx) {
        const nd = this.nodes[idx];
        if (!nd) return;
        this.scale = Math.max(this.scale, 0.7);
        this.tx = this.canvas.width  / 2 - nd.x * this.scale;
        this.ty = this.canvas.height / 2 - nd.y * this.scale;
    }

    _scheduleRender() {
        if (this._renderPending) return;
        this._renderPending = true;
        requestAnimationFrame(() => { this._renderPending = false; this.render(); });
    }

    render() {
        const ctx = this.ctx;
        const W = this.canvas.width, H = this.canvas.height;
        if (!W || !H) return;
        ctx.clearRect(0, 0, W, H);
        ctx.save();
        ctx.translate(this.tx, this.ty);
        ctx.scale(this.scale, this.scale);

        const scaleInv = 1 / this.scale;
        this._scaleInv = scaleInv;
        const sel = this.selected;
        const selNode = sel !== null ? this.nodes[sel] : null;
        const ease = 1 - Math.pow(1 - this._anim, 3);
        const margin = 40;
        const vx0 = -this.tx / this.scale - margin;
        const vy0 = -this.ty / this.scale - margin;
        const vx1 = vx0 + W / this.scale + margin * 2;
        const vy1 = vy0 + H / this.scale + margin * 2;

        if (sel === null) {
            for (let i = 0; i < this.nodes.length; i++) {
                const nd = this.nodes[i];
                if (nd.x + nd.r < vx0 || nd.x - nd.r > vx1 || nd.y + nd.r < vy0 || nd.y - nd.r > vy1) continue;
                this._drawPerson(ctx, nd, i === this.hovered);
            }
        }

        if (selNode) {
            const wc = MNET_WORLD_RGB;
            for (const w of this.worlds) {
                const wx = selNode.x + w.dx * ease;
                const wy = selNode.y + w.dy * ease;
                ctx.beginPath();
                ctx.strokeStyle = `rgba(${wc.r},${wc.g},${wc.b},${0.55 * ease})`;
                ctx.lineWidth = Math.min(6, 1 + w.meets * 0.15) * scaleInv;
                ctx.moveTo(selNode.x, selNode.y);
                ctx.lineTo(wx, wy);
                ctx.stroke();
            }
            for (const w of this.worlds) {
                const wx = selNode.x + w.dx * ease;
                const wy = selNode.y + w.dy * ease;
                if (wx + w.r < vx0 || wx - w.r > vx1 || wy + w.r < vy0 || wy - w.r > vy1) continue;
                ctx.globalAlpha = ease;
                this._drawWorld(ctx, w, wx, wy, w === this.hoveredWorld);
                ctx.globalAlpha = 1;
            }
            this._drawPerson(ctx, selNode, true, true);
            this._drawLabel(ctx, selNode.displayName, selNode.x, selNode.y + selNode.r + 6, true, null);
            if (ease > 0.5) {
                for (const w of this.worlds) {
                    const wx = selNode.x + w.dx * ease;
                    const wy = selNode.y + w.dy * ease;
                    if (wx + w.r < vx0 || wx - w.r > vx1 || wy + w.r < vy0 || wy - w.r > vy1) continue;
                    this._drawLabel(ctx, `${w.name} (${w.meets})`, wx, wy + w.r + 5, w === this.hoveredWorld, MNET_WORLD_RGB);
                }
            }
        } else if (this.hovered !== null && this.nodes[this.hovered]) {
            const nd = this.nodes[this.hovered];
            this._drawLabel(ctx, `${nd.displayName} (${nd.meets})`, nd.x, nd.y + nd.r + 6, false, null);
        }

        ctx.restore();
    }

    _getAccentRgb() {
        if (this._accentCache) return this._accentCache;
        const raw = getComputedStyle(document.documentElement).getPropertyValue('--accent').trim() || '#5682f4';
        const m = raw.match(/^#?([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i);
        this._accentCache = m
            ? { raw, r: parseInt(m[1], 16), g: parseInt(m[2], 16), b: parseInt(m[3], 16) }
            : { raw: '#5682f4', r: 86, g: 130, b: 244 };
        return this._accentCache;
    }

    _drawPerson(ctx, nd, hov, isSel) {
        const scaleInv = this._scaleInv || 1;
        const ring = nd.isFriend ? this._getAccentRgb().raw : MNET_NF_RGB.raw;

        if (hov || isSel) {
            ctx.beginPath();
            ctx.arc(nd.x, nd.y, nd.r + 4, 0, Math.PI * 2);
            ctx.strokeStyle = isSel ? 'rgba(80,180,255,0.9)' : 'rgba(80,180,255,0.45)';
            ctx.lineWidth = 2 * scaleInv;
            ctx.stroke();
        }

        ctx.beginPath();
        ctx.arc(nd.x, nd.y, nd.r + 1.5, 0, Math.PI * 2);
        ctx.strokeStyle = ring;
        ctx.lineWidth = 2 * scaleInv;
        ctx.stroke();

        const spr = this.scale >= 0.3 ? this._sprite(nd) : null;
        if (spr) {
            ctx.drawImage(spr, nd.x - nd.r, nd.y - nd.r, nd.r * 2, nd.r * 2);
        } else {
            ctx.beginPath();
            ctx.arc(nd.x, nd.y, nd.r, 0, Math.PI * 2);
            ctx.fillStyle = ring + '44';
            ctx.fill();
        }
    }

    _drawWorld(ctx, w, x, y, hov) {
        const scaleInv = this._scaleInv || 1;
        if (hov) {
            ctx.beginPath();
            ctx.arc(x, y, w.r + 4, 0, Math.PI * 2);
            ctx.strokeStyle = 'rgba(80,180,255,0.45)';
            ctx.lineWidth = 2 * scaleInv;
            ctx.stroke();
        }
        ctx.beginPath();
        ctx.arc(x, y, w.r + 1.5, 0, Math.PI * 2);
        ctx.strokeStyle = MNET_WORLD_RGB.raw;
        ctx.lineWidth = 2 * scaleInv;
        ctx.stroke();

        const spr = this._sprite(w);
        if (spr) {
            ctx.drawImage(spr, x - w.r, y - w.r, w.r * 2, w.r * 2);
        } else {
            ctx.beginPath();
            ctx.arc(x, y, w.r, 0, Math.PI * 2);
            ctx.fillStyle = MNET_WORLD_RGB.raw + '44';
            ctx.fill();
        }
    }

    _drawLabel(ctx, text, x, yTop, strong, colOverride) {
        const ac = colOverride || this._getAccentRgb();
        const fs = strong ? 9 : 8;
        const px = 4, py = 2;
        ctx.save();
        ctx.font = `${strong ? '700' : '600'} ${fs}px sans-serif`;
        ctx.textAlign = 'center';
        ctx.textBaseline = 'top';
        const tw = ctx.measureText(text).width;
        const bw = tw + px * 2;
        const bh = fs + py * 2;
        ctx.fillStyle = `rgba(${ac.r},${ac.g},${ac.b},0.22)`;
        ctx.beginPath();
        if (ctx.roundRect) ctx.roundRect(x - bw / 2, yTop, bw, bh, 4);
        else               ctx.rect(x - bw / 2, yTop, bw, bh);
        ctx.fill();
        ctx.fillStyle = ac.raw;
        ctx.fillText(text, x, yTop + py);
        ctx.restore();
    }

    /* ── Events ── */
    _bindEvents() {
        this._handlers = {
            wheel:      e  => this._onWheel(e),
            mousedown:  e  => this._onMouseDown(e),
            mousemove:  e  => this._onMouseMove(e),
            mouseup:    () => { this.dragging = null; this.canvas.classList.remove('dragging'); },
            mouseleave: () => { this.dragging = null; this.hovered = null; this.hoveredWorld = null; this._scheduleRender(); },
            click:      e  => this._onClick(e),
        };
        const c = this.canvas;
        c.addEventListener('wheel',      this._handlers.wheel, { passive: false });
        c.addEventListener('mousedown',  this._handlers.mousedown);
        c.addEventListener('mousemove',  this._handlers.mousemove);
        c.addEventListener('mouseup',    this._handlers.mouseup);
        c.addEventListener('mouseleave', this._handlers.mouseleave);
        c.addEventListener('click',      this._handlers.click);
    }

    destroy() {
        clearTimeout(this.searchTimer);
        if (this._animRaf) cancelAnimationFrame(this._animRaf);
        this._resizeObserver?.disconnect();
        if (this._handlers) {
            const c = this.canvas;
            c.removeEventListener('wheel',      this._handlers.wheel);
            c.removeEventListener('mousedown',  this._handlers.mousedown);
            c.removeEventListener('mousemove',  this._handlers.mousemove);
            c.removeEventListener('mouseup',    this._handlers.mouseup);
            c.removeEventListener('mouseleave', this._handlers.mouseleave);
            c.removeEventListener('click',      this._handlers.click);
        }
        const card = document.getElementById('mnetInfoCard');
        if (card) card.style.display = 'none';
    }

    _canvasToWorld(cx, cy) {
        return { x: (cx - this.tx) / this.scale, y: (cy - this.ty) / this.scale };
    }

    _hitTestWorld(wx, wy) {
        if (this.selected === null || this._anim < 1) return -1;
        const selNode = this.nodes[this.selected];
        if (!selNode) return -1;
        for (let i = this.worlds.length - 1; i >= 0; i--) {
            const w = this.worlds[i];
            const r = w.r + 5;
            const dx = selNode.x + w.dx - wx, dy = selNode.y + w.dy - wy;
            if (dx * dx + dy * dy <= r * r) return i;
        }
        return -1;
    }

    _hitTest(wx, wy) {
        for (let i = this.nodes.length - 1; i >= 0; i--) {
            const nd = this.nodes[i];
            const r  = nd.r + 5;
            const dx = nd.x - wx, dy = nd.y - wy;
            if (dx * dx + dy * dy <= r * r) return i;
        }
        return -1;
    }

    _onWheel(e) {
        e.preventDefault();
        const rect = this.canvas.getBoundingClientRect();
        const mx = e.clientX - rect.left, my = e.clientY - rect.top;
        const factor   = e.deltaY < 0 ? 1.12 : 1 / 1.12;
        const newScale = Math.min(4, Math.max(0.05, this.scale * factor));
        this.tx = mx - (mx - this.tx) * (newScale / this.scale);
        this.ty = my - (my - this.ty) * (newScale / this.scale);
        this.scale = newScale;
        this._scheduleRender();
    }

    _onMouseDown(e) {
        if (e.button !== 0) return;
        const rect = this.canvas.getBoundingClientRect();
        this.dragging = { ox: (e.clientX - rect.left) - this.tx, oy: (e.clientY - rect.top) - this.ty };
        this._dragMoved = false;
        this.canvas.classList.add('dragging');
    }

    _onMouseMove(e) {
        const rect = this.canvas.getBoundingClientRect();
        const mx = e.clientX - rect.left, my = e.clientY - rect.top;

        if (this.dragging) {
            const nx = mx - this.dragging.ox, ny = my - this.dragging.oy;
            if (Math.abs(nx - this.tx) + Math.abs(ny - this.ty) > 2) this._dragMoved = true;
            this.tx = nx;
            this.ty = ny;
            this._scheduleRender();
            return;
        }

        const { x: wx, y: wy } = this._canvasToWorld(mx, my);
        const wHit = this._hitTestWorld(wx, wy);
        const newHovWorld = wHit >= 0 ? this.worlds[wHit] : null;
        let hit = wHit >= 0 ? -1 : (this.scale >= 0.25 ? this._hitTest(wx, wy) : -1);
        if (this.selected !== null && hit !== this.selected) hit = -1;
        const newHov = hit >= 0 ? hit : null;
        if (newHov !== this.hovered || newHovWorld !== this.hoveredWorld) {
            this.hovered = newHov;
            this.hoveredWorld = newHovWorld;
            this._scheduleRender();
        }
    }

    _onClick(e) {
        if (this._dragMoved) { this._dragMoved = false; return; }
        const rect = this.canvas.getBoundingClientRect();
        const { x: wx, y: wy } = this._canvasToWorld(e.clientX - rect.left, e.clientY - rect.top);
        if (this._hitTestWorld(wx, wy) >= 0) return;
        if (this.selected !== null) { this.select(null); return; }
        const hit = this._hitTest(wx, wy);
        if (hit >= 0) this.select(hit);
        else this.select(null);
    }
}

function rerenderMeetNetTranslations() {
    if (_mnetGraph) _mnetGraph._updateInfoCard();
}

document.documentElement.addEventListener('languagechange', rerenderMeetNetTranslations);
