/* === Mutual Network ===
 * Force-directed social graph of your friend circle.
 * Edges = mutual friends. Pure Canvas, no libraries.
 */

let _netGraph        = null;
let _mutualCache     = {};
let _cacheLoadPending = false;
let _externalNonFriends = {};
let _netShowNonFriends = true;
let _netForceRefetch = false;
const NET_NF_STORAGE_KEY = 'vrcnext_net_nonfriends';
const NET_SPRITE_PX = 96;
const NET_SPRITE_PX_HUB = 192;
const NET_HUB_SCALE = 2.1;
const NET_COMM_GAP = 70;
const NET_NF_RGB = { r: 255, g: 138, b: 61, raw: 'rgb(255,138,61)' };

let _netCommunities = false;
let _netCommFilter  = -1;
const NET_COMM_NONE   = '#555555';
const NET_COMM_COLORS = ['#FF6FB5', '#FF2D8F', '#A855F7', '#FF9130', '#3B82F6', '#7CFF3D'];
const NET_COMM_RGB    = NET_COMM_COLORS.map(hex => ({
    raw: hex,
    r: parseInt(hex.slice(1, 3), 16),
    g: parseInt(hex.slice(3, 5), 16),
    b: parseInt(hex.slice(5, 7), 16),
}));

function _netLoadNonFriends() {
    try {
        const raw = localStorage.getItem(NET_NF_STORAGE_KEY);
        if (!raw) return;
        const obj = JSON.parse(raw) || {};
        for (const k in obj) if (!_externalNonFriends[k]) _externalNonFriends[k] = obj[k];
    } catch { /* ignore */ }
}

function _netSaveNonFriends() {
    try { localStorage.setItem(NET_NF_STORAGE_KEY, JSON.stringify(_externalNonFriends)); } catch { /* ignore */ }
}

_netLoadNonFriends();

function networkAddNonFriend(data) {
    if (!data || !data.userId || !Array.isArray(data.mutualIds) || data.mutualIds.length === 0) return;
    _externalNonFriends[data.userId] = {
        id:          data.userId,
        displayName: data.displayName || data.userId,
        image:       data.image || '',
        mutualIds:   data.mutualIds.slice(),
    };
    _netSaveNonFriends();
    if (_netGraph) _netGraph.addNonFriend(_externalNonFriends[data.userId]);
}

function networkToggleNonFriends(btn) {
    _netShowNonFriends = !_netShowNonFriends;
    if (btn) {
        btn.classList.toggle('active', _netShowNonFriends);
        const ic = btn.querySelector('.msi');
        if (ic) ic.textContent = _netShowNonFriends ? 'visibility' : 'visibility_off';
    }
    if (_netGraph) _netGraph._render();
}

function networkToggleCommunities(btn) {
    _netCommunities = !_netCommunities;
    if (btn) btn.classList.toggle('active', _netCommunities);
    if (!_netCommunities) _netCommFilter = -1;
    if (!_netGraph) return;
    if (_netCommunities) {
        _netGraph._scheduleCommunities(0);
    } else {
        _netGraph._renderCommPanel();
        _netGraph._render();
        _netGraph._startSim();
    }
}

function networkFilterCommunity(rank) {
    _netCommFilter = (_netCommFilter === rank) ? -1 : rank;
    if (!_netGraph) return;
    _netGraph._renderCommPanel();
    _netGraph._render();
}

function networkSetMinScore(value) {
    const val = netSetMinScore(parseFloat(value));
    const out = document.getElementById('netMinScoreVal');
    if (out) out.textContent = val.toFixed(2);
    if (_netGraph) _netGraph._scheduleScores(500);
}

function _netNodeHidden(nd) {
    if (!_netShowNonFriends && nd.isNonFriend) return true;
    if (_netCommFilter >= 0 && nd.comm !== _netCommFilter) return true;
    return false;
}

/* ── Community detection (Louvain modularity) ── */
function _netBuildCsr(n, links) {
    const start = new Int32Array(n + 1);
    for (let i = 0; i < links.length; i++) {
        const l = links[i];
        if (l.a === l.b) continue;
        start[l.a + 1]++; start[l.b + 1]++;
    }
    for (let i = 0; i < n; i++) start[i + 1] += start[i];
    const pos = start.slice(0, n);
    const nbr = new Int32Array(start[n]);
    const wgt = new Float64Array(start[n]);
    for (let i = 0; i < links.length; i++) {
        const l = links[i];
        if (l.a === l.b) continue;
        nbr[pos[l.a]] = l.b; wgt[pos[l.a]++] = l.w;
        nbr[pos[l.b]] = l.a; wgt[pos[l.b]++] = l.w;
    }
    return { start, nbr, wgt };
}

function _netLouvainLevel(n, csr, deg, m2) {
    const comm = new Int32Array(n);
    const tot  = new Float64Array(n);
    for (let i = 0; i < n; i++) { comm[i] = i; tot[i] = deg[i]; }
    if (m2 <= 0) return comm;

    const wTo     = new Float64Array(n);
    const touched = new Int32Array(n);

    for (let pass = 0; pass < 12; pass++) {
        let moved = 0;
        for (let i = 0; i < n; i++) {
            const ci = comm[i], ki = deg[i];
            let tCount = 0;
            for (let p = csr.start[i]; p < csr.start[i + 1]; p++) {
                const cj = comm[csr.nbr[p]];
                if (wTo[cj] === 0) touched[tCount++] = cj;
                wTo[cj] += csr.wgt[p];
            }
            tot[ci] -= ki;
            let best = ci, bestGain = wTo[ci] - tot[ci] * ki / m2;
            for (let k = 0; k < tCount; k++) {
                const c = touched[k];
                if (c === ci) continue;
                const gain = wTo[c] - tot[c] * ki / m2;
                if (gain > bestGain) { bestGain = gain; best = c; }
            }
            tot[best] += ki;
            if (best !== ci) { comm[i] = best; moved++; }
            for (let k = 0; k < tCount; k++) wTo[touched[k]] = 0;
        }
        if (!moved) break;
    }
    return comm;
}

function netDetectCommunities(n, edges) {
    const owner = new Int32Array(n);
    for (let i = 0; i < n; i++) owner[i] = i;
    if (n < 2) return owner;

    let size  = n;
    let self  = new Float64Array(n);
    let links = [];
    for (let i = 0; i < edges.length; i++) {
        const e = edges[i];
        if (e.a === e.b) continue;
        const w = e.w === undefined ? 1 : e.w;
        if (w > 0) links.push({ a: e.a, b: e.b, w });
    }
    if (!links.length) return owner;

    for (let level = 0; level < 8; level++) {
        const csr = _netBuildCsr(size, links);
        const deg = new Float64Array(size);
        let m2 = 0;
        for (let i = 0; i < size; i++) {
            let d = 2 * self[i];
            for (let p = csr.start[i]; p < csr.start[i + 1]; p++) d += csr.wgt[p];
            deg[i] = d; m2 += d;
        }

        const comm  = _netLouvainLevel(size, csr, deg, m2);
        const remap = new Int32Array(size).fill(-1);
        let count = 0;
        for (let i = 0; i < size; i++) {
            if (remap[comm[i]] === -1) remap[comm[i]] = count++;
        }
        for (let i = 0; i < size; i++) comm[i] = remap[comm[i]];
        for (let i = 0; i < n; i++) owner[i] = comm[owner[i]];
        if (count === size || count <= 1) break;

        const nSelf = new Float64Array(count);
        for (let i = 0; i < size; i++) nSelf[comm[i]] += self[i];
        const agg = new Map();
        for (let i = 0; i < links.length; i++) {
            const l = links[i];
            const a = comm[l.a], b = comm[l.b];
            if (a === b) { nSelf[a] += l.w; continue; }
            const key = a < b ? a * count + b : b * count + a;
            agg.set(key, (agg.get(key) || 0) + l.w);
        }
        const next = [];
        agg.forEach((w, key) => next.push({ a: Math.floor(key / count), b: key % count, w }));

        links = next; self = nSelf; size = count;
    }
    return owner;
}

function networkProgressText(done, total) {
    return tf('network.progress', { done, total }, `Loading connections: ${done} / ${total}`);
}

function initNetwork() {
    const canvas = document.getElementById('netCanvas');
    if (!canvas) return;
    if (_netGraph) { _netGraph.resize(); return; }
    _netGraph = new MutualGraph(canvas);

    _cacheLoadPending = true;
    sendToCS({ action: 'vrcLoadMutualCache' });
    netSubCacheLoad('groups');
}

function networkCacheLoaded(json) {
    if (!_cacheLoadPending) return;
    _cacheLoadPending = false;
    _netCacheReady = true;
    try { _mutualCache = JSON.parse(json) || {}; } catch { _mutualCache = {}; }
    if (_netGraph) _netGraph.loadFriends();
}

let _netCacheReady = false;

function netEnsureMutualCache() {
    if (_netGraph || _netCacheReady || _cacheLoadPending) return;
    _cacheLoadPending = true;
    sendToCS({ action: 'vrcLoadMutualCache' });
}

function networkAddMutuals(data) {
    if (!data || !data.userId) return;
    _netMutualSets.delete(data.userId);
    if (_netGraph) { _netGraph.onMutualsReceived(data); return; }
    if (!_netCacheReady) { netEnsureMutualCache(); return; }
    _mutualCache[data.userId] = { mutualIds: data.mutualIds || [], optedOut: !!data.optedOut };
    clearTimeout(_netBgSaveTimer);
    _netBgSaveTimer = setTimeout(() => {
        sendToCS({ action: 'vrcSaveMutualCache', cache: JSON.stringify(_mutualCache) });
    }, 2000);
}

let _netBgSaveTimer = null;

function networkCancel() {
    if (_netGraph) _netGraph.cancelLoading();
}

function networkRefresh() {
    if (_netGraph) _netGraph.destroy();
    _netGraph = null;
    initNetwork();
}

function networkReFetch() {
    _mutualCache = {};
    _netForceRefetch = true;
    _netGroupData = {};
    netResetScoreData();
    sendToCS({ action: 'vrcClearMutualCache' });
    sendToCS({ action: 'vrcClearNetworkCache', name: 'groups' });
    networkRefresh();
}

function networkSearch(value) {
    if (_netGraph) _netGraph.setSearch(value);
}

function networkResetView() {
    if (!_netGraph) return;
    _netGraph.fitView();
    _netGraph._render();
}

/* ── Tab activation ── */
document.documentElement.addEventListener('tabchange', () => {
    const tab15 = document.getElementById('tab15');
    if (tab15 && tab15.classList.contains('active')) {
        initNetwork();
        if (_netGraph) _netGraph.reloadImages();
    } else {
        if (_netGraph) { _netGraph._stopSim(); _netGraph.unloadImages(); }
    }
});

/* ════════════════════════════════════════════════════════
   MutualGraph class
   ════════════════════════════════════════════════════════ */
class MutualGraph {
    constructor(canvas) {
        this.canvas  = canvas;
        this.ctx     = canvas.getContext('2d');
        this.nodes   = [];
        this.edges   = [];
        this._edgeSet = new Set();  // fast O(1) duplicate detection
        this.nodeMap = {};

        this.tx = 0; this.ty = 0; this.scale = 1;

        this.dragging = null;
        this.selected = null;
        this.hovered  = null;
        this.searchQuery = '';
        this.searchMatch = null;

        this.fetchQueue  = [];
        this.fetchDone   = 0;
        this.fetchTotal  = 0;
        this.cancelled   = false;
        this._saveTimer  = null;

        this._simRaf        = null;
        this._simRunning    = false;
        this._renderPending = false;
        this._edgesChanged  = true;
        this._edgeCounts    = null;  // cached, only rebuilt when edges change

        this._eg = { normal: [], dimmed: [], highlighted: [], nfNormal: [], nfDimmed: [], nfHighlighted: [] };
        this._egComm   = NET_COMM_RGB.map(() => []);
        this._egCommHi = NET_COMM_RGB.map(() => []);
        this._dotBuckets = new Map();
        this._scaleInv = 1;

        this._commReady = false;
        this._commTimer = null;

        this._bindEvents();
        this._resizeObserver = new ResizeObserver(() => this.resize());
        this._resizeObserver.observe(canvas);
        this.resize();
    }

    /* ── Resize ── */
    resize() {
        const W = this.canvas.offsetWidth;
        const H = this.canvas.offsetHeight;
        if (!W || !H) return;
        if (this.canvas.width === W && this.canvas.height === H) return;
        this.canvas.width  = W;
        this.canvas.height = H;
        this._render();
    }

    /* ── Load friends as nodes ── */
    loadFriends() {
        if (this._friendsLoaded) return;
        const friends = (typeof vrcFriendsData !== 'undefined') ? vrcFriendsData : [];
        if (friends.length === 0) {
            setTimeout(() => { if (_netGraph === this) this.loadFriends(); }, 800);
            return;
        }
        this._friendsLoaded = true;
        this._friendIds = new Set(friends.map(f => f.id));
        const W = this.canvas.width, H = this.canvas.height;
        const cx = W / 2, cy = H / 2;
        const R = Math.min(W, H) * 0.38;

        friends.forEach((f, i) => {
            const angle  = (i / Math.max(friends.length, 1)) * Math.PI * 2;
            const jitter = (Math.random() - 0.5) * R * 0.35;
            this._addNode({
                id:          f.id,
                displayName: f.displayName || f.id,
                image:       f.image || '',
                status:      f.status || 'offline',
                x: cx + (R + jitter) * Math.cos(angle),
                y: cy + (R + jitter) * Math.sin(angle),
            });
        });

        const online  = friends.filter(f => f.status && f.status !== 'offline');
        const offline = friends.filter(f => !f.status || f.status === 'offline');
        this.fetchQueue = [...online, ...offline].map(f => f.id);
        this.fetchTotal = this.fetchQueue.length;
        this.fetchDone  = 0;
        this.cancelled  = false;

        Object.values(_externalNonFriends).forEach(nf => this.addNonFriend(nf));

        if (_netForceRefetch) {
            Object.keys(_externalNonFriends).forEach(id => {
                if (this.nodeMap[id] !== undefined && !this.fetchQueue.includes(id)) this.fetchQueue.push(id);
            });
            _netForceRefetch = false;
            this.fetchTotal = this.fetchQueue.length;
        }

        this._updateProgress();
        this._startSim();
        this._startFetching();
    }

    addNonFriend(nf) {
        if (!this._friendsLoaded || !nf || !Array.isArray(nf.mutualIds)) return;

        if (this._friendIds && this._friendIds.has(nf.id)) {
            if (_externalNonFriends[nf.id]) { delete _externalNonFriends[nf.id]; _netSaveNonFriends(); }
            const fIdx = this.nodeMap[nf.id];
            if (fIdx !== undefined) this.nodes[fIdx].isNonFriend = false;
            return;
        }

        const mutualIdxs = nf.mutualIds
            .map(id => this.nodeMap[id])
            .filter(ix => ix !== undefined);
        if (mutualIdxs.length === 0) return;

        let idx = this.nodeMap[nf.id];
        if (idx === undefined) {
            let ax = 0, ay = 0;
            mutualIdxs.forEach(ix => { ax += this.nodes[ix].x; ay += this.nodes[ix].y; });
            ax /= mutualIdxs.length; ay /= mutualIdxs.length;
            idx = this._addNode({
                id:          nf.id,
                displayName: nf.displayName || nf.id,
                image:       nf.image || '',
                status:      'offline',
                x: ax + (Math.random() - 0.5) * 40,
                y: ay + (Math.random() - 0.5) * 40,
            });
        }
        this.nodes[idx].isNonFriend = true;

        let changed = false;
        mutualIdxs.forEach(bIdx => {
            if (bIdx === idx) return;
            const key = idx < bIdx ? `${idx},${bIdx}` : `${bIdx},${idx}`;
            if (!this._edgeSet.has(key)) {
                this._edgeSet.add(key);
                this.edges.push({ a: idx, b: bIdx, nf: true });
                changed = true;
            }
        });
        if (changed) {
            this._edgesChanged = true;
            if (_netCommunities) this._scheduleCommunities();
        }
        this._startSim(0.12);
    }

    _addNode(opts) {
        const idx  = this.nodes.length;
        const node = {
            id:          opts.id,
            displayName: opts.displayName,
            image:       opts.image || '',
            imgEl:       null,
            status:      opts.status || 'offline',
            x: opts.x || 0,
            y: opts.y || 0,
            vx: 0, vy: 0,
            pinned: false,
            r: 0,
        };
        this.nodes.push(node);
        this.nodeMap[node.id] = idx;

        this._loadNodeImage(node);
        return idx;
    }

    _loadNodeImage(nd) {
        if (!nd.image || nd._imgLoading || this._imagesUnloaded) return;
        nd._imgLoading = true;
        const img = new Image();
        img.src = imgThumb(nd.image, nd.commHub ? 256 : 128);
        img.onload  = () => { nd._imgLoading = false; nd.imgEl = img; this._sprite(nd); nd.imgEl = null; this._scheduleRender(); };
        img.onerror = () => { nd._imgLoading = false; };
    }

    _sprite(nd) {
        const gray = !!nd.isNonFriend;
        const S    = nd.commHub ? NET_SPRITE_PX_HUB : NET_SPRITE_PX;
        if (nd._spr && nd._sprGray === gray && nd._sprPx === S) return nd._spr;
        if (!nd.imgEl) { this._loadNodeImage(nd); return nd._spr || null; }

        const cv = document.createElement('canvas');
        cv.width = S; cv.height = S;
        const c = cv.getContext('2d');
        c.beginPath();
        c.arc(S / 2, S / 2, S / 2, 0, Math.PI * 2);
        c.clip();
        if (gray) c.filter = 'grayscale(1)';
        c.drawImage(nd.imgEl, 0, 0, S, S);

        nd._spr     = cv;
        nd._sprGray = gray;
        nd._sprPx   = S;
        return cv;
    }

    unloadImages() {
        let count = 0;
        this.nodes.forEach(nd => {
            if (nd.imgEl || nd._spr) count++;
            nd.imgEl = null;
            nd._spr  = null;
        });
        this._imagesUnloaded = true;
        if (count > 0 && typeof addLog === 'function')
            addLog(`[Unload] Unloaded ${count} image${count !== 1 ? 's' : ''} from Tab 15 from memory.`, 'info');
    }

    reloadImages() {
        if (!this._imagesUnloaded) return;
        this._imagesUnloaded = false;
        this.nodes.forEach(nd => this._loadNodeImage(nd));
    }

    _nodeColors(nd) {
        const comm = (_netCommunities && this._commReady && nd.comm !== undefined) ? nd.comm : -2;
        const key  = (nd.status || '') + (nd.isNonFriend ? '!' : '') + '|' + comm;
        if (nd._colKey !== key) {
            nd._colKey = key;
            if (comm !== -2) {
                const c = comm >= 0 ? NET_COMM_COLORS[comm % NET_COMM_COLORS.length] : NET_COMM_NONE;
                nd._colDot = c; nd._colRing = c; nd._colFill = c + '44';
            } else {
                nd._colDot  = this._statusColor(nd.status);
                nd._colRing = nd.isNonFriend ? NET_NF_RGB.raw : nd._colDot;
                nd._colFill = nd._colRing + '44';
            }
        }
        return nd;
    }

    _scheduleCommunities(delay) {
        if (delay === 0) {
            clearTimeout(this._commTimer);
            this._commTimer = null;
            this._fitPending = true;
            this._computeCommunities();
            this._render();
            this._startSim();
            return;
        }
        if (this._commTimer) return;
        const loading = this.fetchTotal > 0 && this.fetchDone < this.fetchTotal;
        this._commTimer = setTimeout(() => {
            this._commTimer = null;
            if (!_netCommunities) return;
            this._computeCommunities();
            this._render();
            this._startSim(0.3);
        }, loading ? 5000 : 1200);
    }

    _rescoreEdges() {
        const nodes = this.nodes;
        const links = [];
        const min   = netMinScore();
        const seen  = new Set();

        for (let i = 0; i < this.edges.length; i++) {
            const e = this.edges[i];
            const a = nodes[e.a], b = nodes[e.b];
            if (!a || !b) continue;
            const s = netPairScore(a.id, b.id);
            e.score = s.total;
            seen.add(e.a < e.b ? e.a + ',' + e.b : e.b + ',' + e.a);
            if (s.total >= min) links.push({ a: e.a, b: e.b, w: s.total });
        }

        _netSessionW.forEach((_, key) => {
            const parts = key.split(' ');
            const ia = this.nodeMap[parts[0]], ib = this.nodeMap[parts[1]];
            if (ia === undefined || ib === undefined || ia === ib) return;
            const k = ia < ib ? ia + ',' + ib : ib + ',' + ia;
            if (seen.has(k)) return;
            seen.add(k);
            const s = netPairScore(parts[0], parts[1]);
            if (s.total >= min) links.push({ a: ia, b: ib, w: s.total });
        });

        this._scoreLinks = links;
        this._scoreReady = true;
    }

    _scheduleScores(delay) {
        if (this._scoreTimer) return;
        this._scoreTimer = setTimeout(() => {
            this._scoreTimer = null;
            this._rescoreEdges();
            if (_netCommunities) this._scheduleCommunities(0);
            else this._render();
        }, delay === undefined ? 1500 : delay);
    }

    onSessionDataReady() { this._scheduleScores(300); }
    onGroupDataReady()   { this._scheduleScores(2500); }

    requestScoreData() {
        this._sessionIds = this.nodes.map(nd => nd.id);
        if (this._sessionIds.length < 2) return;
        sendToCS({ action: 'vrcGetNetworkSessions', ids: this._sessionIds });
        netStartGroupFetch(this._sessionIds);
    }

    _computeCommunities() {
        const n = this.nodes.length;
        if (!n) return;

        if (!this._scoreReady) this._rescoreEdges();
        const owner = netDetectCommunities(n, this._scoreLinks && this._scoreLinks.length ? this._scoreLinks : this.edges);
        const size    = new Map();
        const members = new Map();
        for (let i = 0; i < n; i++) {
            const l = owner[i];
            size.set(l, (size.get(l) || 0) + 1);
            let m = members.get(l);
            if (!m) { m = []; members.set(l, m); }
            m.push(i);
        }

        const big = Array.from(size.keys())
            .filter(l => size.get(l) > 1)
            .sort((a, b) => (size.get(b) - size.get(a)) || (a - b));
        const k = big.length;

        const prev  = this._commOf;
        const slots = new Array(k).fill(-1);
        const taken = new Set();
        if (prev) {
            for (let idx = 0; idx < k; idx++) {
                const votes = new Map();
                members.get(big[idx]).forEach(i => {
                    const r = prev.get(this.nodes[i].id);
                    if (r === undefined || r < 0 || r >= k || taken.has(r)) return;
                    votes.set(r, (votes.get(r) || 0) + 1);
                });
                let best = -1, bestV = 0;
                votes.forEach((v, r) => { if (v > bestV) { bestV = v; best = r; } });
                if (best >= 0) { slots[idx] = best; taken.add(best); }
            }
        }
        let free = 0;
        for (let idx = 0; idx < k; idx++) {
            if (slots[idx] >= 0) continue;
            while (taken.has(free)) free++;
            slots[idx] = free;
            taken.add(free);
        }

        const rank    = new Map();
        const bySlot  = new Array(k).fill(-1);
        for (let idx = 0; idx < k; idx++) { rank.set(big[idx], slots[idx]); bySlot[slots[idx]] = big[idx]; }

        const commOf = new Map();
        for (let i = 0; i < n; i++) {
            const r = rank.has(owner[i]) ? rank.get(owner[i]) : -1;
            this.nodes[i].comm = r;
            commOf.set(this.nodes[i].id, r);
        }
        this._commOf = commOf;

        for (let i = 0; i < this.edges.length; i++) {
            const e = this.edges[i];
            e.comm = (owner[e.a] === owner[e.b] && rank.has(owner[e.a])) ? rank.get(owner[e.a]) : -1;
        }

        const inDeg = new Int32Array(n);
        for (let i = 0; i < this.edges.length; i++) {
            const e = this.edges[i];
            if (e.comm >= 0) { inDeg[e.a]++; inDeg[e.b]++; }
        }
        const hubs = new Int32Array(k).fill(-1);
        for (let i = 0; i < n; i++) {
            const c = this.nodes[i].comm;
            this.nodes[i].commHub = false;
            if (c < 0 || c >= k) continue;
            if (hubs[c] === -1 || inDeg[i] > inDeg[hubs[c]]) hubs[c] = i;
        }
        for (let c = 0; c < k; c++) if (hubs[c] >= 0) this.nodes[hubs[c]].commHub = true;

        const list = [];
        for (let c = 0; c < k; c++) {
            if (hubs[c] < 0) continue;
            list.push({
                rank:  c,
                name:  this.nodes[hubs[c]].displayName || '',
                count: size.get(bySlot[c]) || 0,
                color: NET_COMM_COLORS[c % NET_COMM_COLORS.length],
            });
        }
        this._commList = list;
        if (_netCommFilter >= list.length) _netCommFilter = -1;

        const radii = new Array(k);
        for (let c = 0; c < k; c++) radii[c] = 36 + 26 * Math.sqrt(Math.max(2, size.get(bySlot[c]) || 2));

        this._commHubs    = hubs;
        this._commAnchors = this._buildAnchors(radii);
        this._commReady   = true;
        this._edgesChanged = true;
        this._renderCommPanel();
    }

    _renderCommPanel() {
        const el = document.getElementById('netCommPanel');
        if (!el) return;
        const list = this._commList || [];
        if (!_netCommunities || !this._commReady || !list.length) {
            el.style.display = 'none';
            return;
        }

        if (el.dataset.built !== '1') {
            const min = netMinScore();
            el.innerHTML =
                  `<div class="net-comm-head"><span>${esc(t('network.communities', 'Communities'))}</span>`
                + `<span id="netCommCount"></span></div>`
                + `<div class="net-comm-list" id="netCommList"></div>`
                + `<div class="net-comm-score" title="${esc(t('network.min_score_help', 'Connections weaker than this are ignored when building communities'))}">`
                + `<div class="net-comm-score-row"><span>${esc(t('network.min_score', 'Min. connection score'))}</span>`
                + `<span id="netMinScoreVal">${min.toFixed(2)}</span></div>`
                + `<input type="range" id="netMinScore" min="0" max="0.5" step="0.01" value="${min}" oninput="networkSetMinScore(this.value)"></div>`
                + `<div class="net-comm-hint"><span class="net-comm-hint-dots"><i></i><i></i></span>`
                + `<span>${esc(t('network.comm_hint', 'Bigger dot = more mutual friends'))}</span></div>`;
            el.dataset.built = '1';
        }

        const only = t('network.comm_only', 'Show only this community');
        const all  = t('network.comm_all', 'Show all communities');
        const rows = list.map(c => {
            const on = _netCommFilter === c.rank;
            return `<button type="button" class="net-comm-row${on ? ' active' : ''}" onclick="networkFilterCommunity(${c.rank})" title="${esc(on ? all : only)}">`
                 + `<span class="net-comm-dot" style="background:${c.color};"></span>`
                 + `<span class="net-comm-name">${esc(c.name)}</span>`
                 + `<span class="net-comm-count">${c.count}</span></button>`;
        }).join('');

        const cnt = document.getElementById('netCommCount');
        if (cnt) cnt.textContent = list.length;
        const box = document.getElementById('netCommList');
        if (box) box.innerHTML = rows;
        el.style.display = 'flex';
    }

    _updateScoreCard() {
        const el = document.getElementById('netScoreCard');
        if (!el) return;
        const a = this.selected, b = this.hovered;
        if (a === null || b === null || a === b || !this.nodes[a] || !this.nodes[b]) {
            el.style.display = 'none';
            return;
        }

        const na = this.nodes[a], nb = this.nodes[b];
        const s  = netPairScore(na.id, nb.id);
        const row = (label, value) => `<div class="net-score-row"><span>${esc(label)}</span><span>${value.toFixed(2)}</span></div>`;

        el.innerHTML = `<div class="net-score-pair">${esc(na.displayName)} &harr; ${esc(nb.displayName)}</div>`
                     + `<div class="net-score-total"><span>${esc(t('network.score_total', 'Connection Score'))}</span><span>${s.total.toFixed(2)}</span></div>`
                     + row(t('network.score_mutual', 'Mutual Friends'), s.mutual)
                     + row(t('network.score_instances', 'Shared Instances'), s.instance)
                     + row(t('network.score_groups', 'Shared Groups'), s.group);
        el.style.display = 'flex';
    }

    _buildAnchors(radii) {
        const W = this.canvas.width, H = this.canvas.height;
        const cx = W / 2, cy = H / 2;
        const k  = radii.length;
        const anchors = [];
        this._commRadii = radii.slice();
        if (k <= 0) { this._worldRadius = Math.max(W, H); return anchors; }
        if (k === 1) {
            anchors.push({ x: cx, y: cy });
            this._worldRadius = radii[0] + NET_COMM_GAP + Math.max(W, H) * 0.2;
            return anchors;
        }

        const order = radii.map((_, i) => i).sort((a, b) => (radii[b] - radii[a]) || (a - b));
        anchors.length = k;

        const suffix = new Float64Array(k + 1);
        for (let s = k - 1; s >= 0; s--) suffix[s] = suffix[s + 1] + radii[order[s]] * radii[order[s]];

        let idx = 0;
        let R = radii[order[0]] + NET_COMM_GAP;
        while (idx < k) {
            const areaR = Math.sqrt(suffix[idx]);
            if (areaR > R) R = areaR;

            const ring = [];
            let used = 0, maxR = 0;
            while (idx < k) {
                const i    = order[idx];
                const half = Math.asin(Math.min(1, (radii[i] + NET_COMM_GAP / 2) / R));
                if (ring.length && used + 2 * half > Math.PI * 2) break;
                ring.push({ i, half });
                used += 2 * half;
                if (radii[i] > maxR) maxR = radii[i];
                idx++;
            }

            const slack = Math.max(0, Math.PI * 2 - used) / ring.length;
            let acc = -Math.PI / 2;
            for (let s = 0; s < ring.length; s++) {
                const it = ring[s];
                const ang = acc + it.half + slack / 2;
                acc += 2 * it.half + slack;
                anchors[it.i] = { x: cx + R * Math.cos(ang), y: cy + R * Math.sin(ang) };
            }

            if (idx < k) R += maxR + radii[order[idx]] + NET_COMM_GAP;
        }

        let world = 0;
        for (let i = 0; i < k; i++) {
            const reach = Math.hypot(anchors[i].x - cx, anchors[i].y - cy) + radii[i];
            if (reach > world) world = reach;
        }
        this._worldRadius = world + NET_COMM_GAP + Math.max(W, H) * 0.2;
        return anchors;
    }

    _confineToCommunities() {
        const anchors = this._commAnchors, radii = this._commRadii;
        const nodes = this.nodes, n = nodes.length;
        const cx = this.canvas.width / 2, cy = this.canvas.height / 2;
        const world = this._worldRadius || Math.max(this.canvas.width, this.canvas.height) * 2;

        for (let i = 0; i < n; i++) {
            const nd = nodes[i], c = nd.comm;
            let ax = cx, ay = cy, lim = world;

            if (anchors && radii && c >= 0 && c < anchors.length && anchors[c]) {
                ax = anchors[c].x; ay = anchors[c].y;
                lim = Math.max(radii[c] - (nd.r || 5), 10);
            }

            const dx = nd.x - ax, dy = nd.y - ay;
            const d  = Math.sqrt(dx * dx + dy * dy);
            if (d <= lim || d === 0 || !isFinite(d)) continue;

            const f = lim / d;
            nd.x = ax + dx * f;
            nd.y = ay + dy * f;
            nd.vx = 0; nd.vy = 0;
        }
    }

    /* ── Fetching mutuals (with cache) ── */
    _startFetching() {
        const toFetch = [];
        this.fetchQueue.forEach(uid => {
            if (_mutualCache[uid] !== undefined) {
                this._applyMutuals(uid, _mutualCache[uid].mutualIds, _mutualCache[uid].optedOut);
                this.fetchDone++;
            } else {
                toFetch.push(uid);
            }
        });
        this.fetchQueue = toFetch;

        this._updateProgress();
        if (this.fetchQueue.length === 0) {
            this._hideProgress();
            this.requestScoreData();
            this._startSim();
            return;
        }
        this._startSim();
        this._fetchBatch();
    }

    _fetchBatch() {
        if (this.cancelled || this.fetchQueue.length === 0) {
            this._hideProgress();
            return;
        }
        const batch = this.fetchQueue.splice(0, 3);
        batch.forEach(uid => {
            if (typeof sendToCS === 'function')
                sendToCS({ action: 'vrcGetMutualsForNetwork', userId: uid });
        });
        setTimeout(() => this._fetchBatch(), 350);
    }

    _applyMutuals(userId, mutualIds, optedOut) {
        const aIdx = this.nodeMap[userId];
        if (aIdx === undefined || optedOut || !Array.isArray(mutualIds)) return;
        let changed = false;
        mutualIds.forEach(bid => {
            const bIdx = this.nodeMap[bid];
            if (bIdx !== undefined && aIdx !== bIdx) {
                // Canonical key — smaller index first
                const key = aIdx < bIdx ? `${aIdx},${bIdx}` : `${bIdx},${aIdx}`;
                if (!this._edgeSet.has(key)) {
                    this._edgeSet.add(key);
                    this.edges.push({ a: aIdx, b: bIdx });
                    changed = true;
                }
            }
        });
        if (changed) {
            this._edgesChanged = true;
            if (_netCommunities) this._scheduleCommunities();
        }
    }

    onMutualsReceived(data) {
        if (_externalNonFriends[data.userId]) {
            _externalNonFriends[data.userId].mutualIds = data.optedOut ? [] : (data.mutualIds || []);
            _netSaveNonFriends();
            this.fetchDone++;
            this.addNonFriend(_externalNonFriends[data.userId]);
            this._updateProgress();
            this._startSim(0.12);
            if (this.fetchDone >= this.fetchTotal) { this._hideProgress(); this.requestScoreData(); if (_netCommunities) this._scheduleCommunities(0); }
            return;
        }

        _mutualCache[data.userId] = { mutualIds: data.mutualIds || [], optedOut: !!data.optedOut };
        clearTimeout(this._saveTimer);
        this._saveTimer = setTimeout(() => {
            sendToCS({ action: 'vrcSaveMutualCache', cache: JSON.stringify(_mutualCache) });
        }, 1500);

        this.fetchDone++;
        this._applyMutuals(data.userId, data.mutualIds, data.optedOut);
        this._updateProgress();
        this._startSim(0.12);  // no-op if already running; restarts if settled
        if (this.fetchDone >= this.fetchTotal) { this._hideProgress(); this.requestScoreData(); if (_netCommunities) this._scheduleCommunities(0); }
    }

    cancelLoading() {
        this.cancelled = true;
        clearTimeout(this._saveTimer);
        this._hideProgress();
        // Keep sim running so the partial graph settles naturally
    }

    /* ── Progress ── */
    _updateProgress() {
        const bar  = document.getElementById('netProgress');
        const text = document.getElementById('netProgressText');
        if (!bar) return;
        if (this.fetchTotal === 0) { bar.style.display = 'none'; return; }
        bar.style.display = 'flex';
        if (text) text.textContent = networkProgressText(this.fetchDone, this.fetchTotal);
    }
    _hideProgress() {
        const bar = document.getElementById('netProgress');
        if (bar) bar.style.display = 'none';
    }

    /* ── Async simulation loop — runs via rAF, never blocks the main thread ── */
    _startSim(heat) {
        const h = heat === undefined ? 1 : heat;
        this._temp = Math.max(this._temp || 0, h);
        if (this._simRunning) return;
        this._settled = false;
        this._simRunning = true;
        this._simRaf = requestAnimationFrame(() => this._simTick());
    }

    _stopSim() {
        this._simRunning = false;
        if (this._simRaf) { cancelAnimationFrame(this._simRaf); this._simRaf = null; }
    }

    _simTick() {
        if (!this._simRunning) return;
        const tab15 = document.getElementById('tab15');
        if (!tab15 || !tab15.classList.contains('active') || document.hidden) {
            this._stopSim();
            return;
        }

        const n = this.nodes.length;
        const steps = n > 900 ? 2 : n > 400 ? 4 : 8;
        this._settled = false;
        for (let i = 0; i < steps; i++) {
            this._simulate();
            if (this._settled) break;
        }

        this._sanitizePositions();
        this._render();

        if (this._settled) {
            this._simRunning = false;
            this._simRaf = null;
            if (this._fitPending) { this._fitPending = false; this.fitView(); this._render(); }
        } else {
            this._simRaf = requestAnimationFrame(() => this._simTick());
        }
    }

    _sanitizePositions() {
        const W = this.canvas.width || 1000, H = this.canvas.height || 1000;
        const lim = Math.max(W, H) * 30;
        const nodes = this.nodes;
        let broken = 0;
        for (let i = 0; i < nodes.length; i++) {
            const nd = nodes[i];
            if (isFinite(nd.x) && isFinite(nd.y) && Math.abs(nd.x) < lim && Math.abs(nd.y) < lim) continue;
            nd.x = W / 2 + (Math.random() - 0.5) * W * 0.4;
            nd.y = H / 2 + (Math.random() - 0.5) * H * 0.4;
            nd.vx = 0; nd.vy = 0;
            broken++;
        }
        if (!broken) return;

        const anchors = this._commAnchors;
        if (anchors && this._commRadii && this._commRadii.length === anchors.length) {
            for (let c = 0; c < anchors.length; c++) {
                const a = anchors[c];
                if (a && isFinite(a.x) && isFinite(a.y) && Math.abs(a.x) < lim && Math.abs(a.y) < lim) continue;
                this._commAnchors = this._buildAnchors(this._commRadii);
                break;
            }
        }
        this._temp = Math.min(this._temp || 0, 0.3);
    }

    _simulate() {
        const nodes = this.nodes;
        const n = nodes.length;
        if (n < 2) return;

        const W = this.canvas.width, H = this.canvas.height;
        const cx = W / 2, cy = H / 2;

        // Recompute edge counts only when edges have changed
        if (this._edgesChanged || !this._edgeCounts || this._edgeCounts.length !== n) {
            const counts = new Array(n).fill(0);
            this.edges.forEach(e => { counts[e.a]++; counts[e.b]++; });
            nodes.forEach((nd, i) => {
                const base = Math.min(12, 5 + counts[i] * 0.3);
                nd.r = (_netCommunities && nd.commHub) ? base * NET_HUB_SCALE : base;
            });
            this._edgeCounts = counts;
            this._edgesChanged = false;
        }

        // Reuse force arrays to reduce GC pressure
        if (!this._fx || this._fx.length !== n) {
            this._fx = new Float64Array(n);
            this._fy = new Float64Array(n);
            this._px = new Float64Array(n);
            this._py = new Float64Array(n);
        }
        const fx = this._fx; fx.fill(0);
        const fy = this._fy; fy.fill(0);
        const px = this._px, py = this._py;
        for (let i = 0; i < n; i++) { px[i] = nodes[i].x; py[i] = nodes[i].y; }

        const anchors = this._commAnchors;
        const hubs    = this._commHubs;
        const commOn  = _netCommunities && this._commReady && anchors && anchors.length > 0;

        let cm = null;
        if (commOn) {
            if (!this._cm || this._cm.length !== n) this._cm = new Int32Array(n);
            cm = this._cm;
            for (let i = 0; i < n; i++) { const c = nodes[i].comm; cm[i] = c === undefined ? -1 : c; }
        }

        const K_REP = 1900, MIN_D = 25, REP_IN = 0.6;
        for (let i = 0; i < n; i++) {
            const xi = px[i], yi = py[i];
            const ci = cm ? cm[i] : -1;
            let ax = 0, ay = 0;
            for (let j = i + 1; j < n; j++) {
                const dx = xi - px[j];
                const dy = yi - py[j];
                const d  = Math.max(Math.sqrt(dx * dx + dy * dy), MIN_D);
                const f  = (ci >= 0 && cm[j] === ci ? K_REP * REP_IN : K_REP) / (d * d * d);
                const ox = f * dx, oy = f * dy;
                ax += ox;  ay += oy;
                fx[j] -= ox;  fy[j] -= oy;
            }
            fx[i] += ax;  fy[i] += ay;
        }

        const K_SPRING = 0.018, REST = 240, CROSS = 0.08;
        const edges = this.edges, m = edges.length;
        for (let k = 0; k < m; k++) {
            const e = edges[k], ia = e.a, ib = e.b;
            const dx = px[ib] - px[ia], dy = py[ib] - py[ia];
            const d  = Math.sqrt(dx * dx + dy * dy) || 1;
            let s    = (d - REST) * K_SPRING / d;
            if (commOn && !(e.comm >= 0)) s *= CROSS;
            const ox = s * dx, oy = s * dy;
            fx[ia] += ox;  fy[ia] += oy;
            fx[ib] -= ox;  fy[ib] -= oy;
        }

        const K_GRAV = 0.004, K_COMM = 0.030, K_HUB = 0.090;
        for (let i = 0; i < n; i++) {
            const c = commOn ? nodes[i].comm : -1;
            const a = (c >= 0 && c < anchors.length) ? anchors[c] : null;
            if (!a) {
                fx[i] += (cx - px[i]) * K_GRAV;
                fy[i] += (cy - py[i]) * K_GRAV;
                continue;
            }
            const hub = hubs && c < hubs.length ? hubs[c] : -1;
            if (hub === i || hub < 0) {
                fx[i] += (a.x - px[i]) * K_HUB;
                fy[i] += (a.y - py[i]) * K_HUB;
            } else {
                fx[i] += (px[hub] - px[i]) * K_COMM;
                fy[i] += (py[hub] - py[i]) * K_COMM;
            }
        }

        const DAMP = 0.75, MAX_V = 6, TEMP_DECAY = 0.997, TEMP_MIN = 0.02;
        const temp = this._temp === undefined ? 1 : this._temp;
        const cap  = MAX_V * temp;
        let maxV = 0;
        for (let i = 0; i < n; i++) {
            const nd = nodes[i];
            if (nd.pinned) continue;
            const vx = Math.max(-cap, Math.min(cap, nd.vx * DAMP + fx[i]));
            const vy = Math.max(-cap, Math.min(cap, nd.vy * DAMP + fy[i]));
            nd.vx = vx;
            nd.vy = vy;
            nd.x += vx;
            nd.y += vy;
            const av = Math.abs(vx) > Math.abs(vy) ? Math.abs(vx) : Math.abs(vy);
            if (av > maxV) maxV = av;
        }
        this._temp = temp * TEMP_DECAY;

        if (commOn) this._confineToCommunities();
        if (maxV < 0.12 || this._temp < TEMP_MIN) this._settled = true;
    }

    /* ── Search ── */
    setSearch(value) {
        const q = (value || '').trim().toLowerCase();
        this.searchQuery = q;
        if (!q) { this.searchMatch = null; this._render(); return; }

        let best = -1, bestStarts = false;
        for (let i = 0; i < this.nodes.length; i++) {
            if (_netNodeHidden(this.nodes[i])) continue;
            const name = (this.nodes[i].displayName || '').toLowerCase();
            if (!name.includes(q)) continue;
            const starts = name.startsWith(q);
            if (best === -1 || (starts && !bestStarts)) { best = i; bestStarts = starts; }
            if (starts) break;
        }
        this.searchMatch = best >= 0 ? best : null;
        if (this.searchMatch !== null) this._centerOn(this.searchMatch);
        this._render();
    }

    _clearSearch() {
        this.searchQuery = '';
        this.searchMatch = null;
        const inp = document.getElementById('netSearchInput');
        if (inp) inp.value = '';
    }

    fitView() {
        const nodes = this.nodes;
        const W = this.canvas.width, H = this.canvas.height;
        if (!W || !H) return;

        let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity, any = false;
        for (let i = 0; i < nodes.length; i++) {
            const nd = nodes[i];
            if (_netNodeHidden(nd) || !isFinite(nd.x) || !isFinite(nd.y)) continue;
            const r = (nd.r || 5) + 10;
            if (nd.x - r < minX) minX = nd.x - r;
            if (nd.x + r > maxX) maxX = nd.x + r;
            if (nd.y - r < minY) minY = nd.y - r;
            if (nd.y + r > maxY) maxY = nd.y + r;
            any = true;
        }
        if (!any) return;

        const pad = 24;
        const sx = (W - pad * 2) / Math.max(1, maxX - minX);
        const sy = (H - pad * 2) / Math.max(1, maxY - minY);
        this.scale = Math.min(2, Math.max(0.04, Math.min(sx, sy)));
        this.tx = W / 2 - ((minX + maxX) / 2) * this.scale;
        this.ty = H / 2 - ((minY + maxY) / 2) * this.scale;
    }

    _centerOn(idx) {
        const nd = this.nodes[idx];
        if (!nd) return;
        const W = this.canvas.width, H = this.canvas.height;
        this.tx = W / 2 - nd.x * this.scale;
        this.ty = H / 2 - nd.y * this.scale;
    }

    /* ── Render ── */

    // Deferred render — coalesces multiple calls within the same frame into one
    _scheduleRender() {
        if (this._renderPending) return;
        this._renderPending = true;
        requestAnimationFrame(() => { this._renderPending = false; this._render(); });
    }

    _render() {
        const ctx = this.ctx;
        const W = this.canvas.width, H = this.canvas.height;
        if (!W || !H) return;
        ctx.clearRect(0, 0, W, H);

        ctx.save();
        ctx.translate(this.tx, this.ty);
        ctx.scale(this.scale, this.scale);

        // Visible viewport in world-space for culling (with margin for rings/labels)
        const margin = 30;
        const vx0 = -this.tx / this.scale - margin;
        const vy0 = -this.ty / this.scale - margin;
        const vx1 = vx0 + W / this.scale + margin * 2;
        const vy1 = vy0 + H / this.scale + margin * 2;

        const searching = this.searchMatch !== null && this.searchMatch !== undefined;
        const sel = searching ? null : this.selected;
        const scaleInv = 1 / this.scale;
        this._scaleInv = scaleInv;

        const nodes = this.nodes, edges = this.edges;
        const nEdges = edges.length;

        let activeSet = null;
        if (searching) {
            activeSet = new Set([this.searchMatch]);
        } else if (sel !== null) {
            activeSet = new Set([sel]);
            for (let i = 0; i < nEdges; i++) {
                const e = edges[i];
                if (e.a === sel) activeSet.add(e.b);
                if (e.b === sel) activeSet.add(e.a);
            }
        }

        const ac = this._getAccentRgb();
        const nf = this._getNonFriendRgb();
        const g = this._eg;
        g.normal.length = 0; g.dimmed.length = 0; g.highlighted.length = 0;
        g.nfNormal.length = 0; g.nfDimmed.length = 0; g.nfHighlighted.length = 0;

        const commOn = _netCommunities && this._commReady;
        const egc = this._egComm, egcHi = this._egCommHi;
        if (commOn) for (let i = 0; i < egc.length; i++) { egc[i].length = 0; egcHi[i].length = 0; }
        const emx = (vx1 - vx0) * 0.5, emy = (vy1 - vy0) * 0.5;
        const ex0 = vx0 - emx, ex1 = vx1 + emx;
        const ey0 = vy0 - emy, ey1 = vy1 + emy;

        for (let i = 0; i < nEdges; i++) {
            const e = edges[i];
            if (!_netShowNonFriends && e.nf) continue;
            if (_netCommFilter >= 0 && e.comm !== _netCommFilter) continue;
            const a = nodes[e.a], b = nodes[e.b];
            if (a.x < vx0 && b.x < vx0) continue;
            if (a.x > vx1 && b.x > vx1) continue;
            if (a.y < vy0 && b.y < vy0) continue;
            if (a.y > vy1 && b.y > vy1) continue;
            const aIn = a.x >= ex0 && a.x <= ex1 && a.y >= ey0 && a.y <= ey1;
            const bIn = b.x >= ex0 && b.x <= ex1 && b.y >= ey0 && b.y <= ey1;
            if (!aIn && !bIn) continue;

            const inComm = commOn && e.comm >= 0;
            if (sel !== null && (e.a === sel || e.b === sel)) {
                if (inComm) egcHi[e.comm % egcHi.length].push(e);
                else        (e.nf ? g.nfHighlighted : g.highlighted).push(e);
            }
            else if (activeSet !== null) (e.nf ? g.nfDimmed : g.dimmed).push(e);
            else if (inComm)             egc[e.comm % egc.length].push(e);
            else                         (e.nf ? g.nfNormal : g.normal).push(e);
        }

        const strokeBatch = (arr, col, alpha, w) => {
            const len = arr.length;
            if (!len) return;
            ctx.beginPath();
            ctx.strokeStyle = `rgba(${col.r},${col.g},${col.b},${alpha})`;
            ctx.lineWidth   = w * scaleInv;
            for (let i = 0; i < len; i++) {
                const e = arr[i];
                const a = nodes[e.a], b = nodes[e.b];
                ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y);
            }
            ctx.stroke();
        };
        strokeBatch(g.normal,        ac, 0.5,  1);
        strokeBatch(g.dimmed,        ac, 0.08, 1);
        strokeBatch(g.highlighted,   ac, 0.95, 2.5);
        strokeBatch(g.nfNormal,      nf, 0.75, 1);
        strokeBatch(g.nfDimmed,      nf, 0.12, 1);
        strokeBatch(g.nfHighlighted, nf, 0.95, 2.6);
        if (commOn) {
            for (let i = 0; i < egc.length; i++) strokeBatch(egc[i], NET_COMM_RGB[i], 0.65, 1);
            for (let i = 0; i < egcHi.length; i++) strokeBatch(egcHi[i], NET_COMM_RGB[i], 0.95, 2.5);
        }
        const lod = this.scale < 0.35 ? 0 : this.scale < 0.65 ? 1 : 2;

        const nCount = nodes.length;

        if (lod === 0) {
            const byColor = this._dotBuckets;
            byColor.forEach(b => { b.nds.length = 0; });
            for (let i = 0; i < nCount; i++) {
                const nd = nodes[i];
                if (_netNodeHidden(nd)) continue;
                const r = nd.r || 5;
                if (nd.x + r < vx0 || nd.x - r > vx1 || nd.y + r < vy0 || nd.y - r > vy1) continue;
                const inActive = activeSet === null || activeSet.has(i);
                const sc  = this._nodeColors(nd)._colDot;
                const key = inActive ? sc : sc + '_dim';
                let b = byColor.get(key);
                if (!b) { b = { color: sc, alpha: inActive ? 1 : 0.12, nds: [] }; byColor.set(key, b); }
                b.nds.push(nd);
            }
            byColor.forEach(({ color, alpha, nds }) => {
                const len = nds.length;
                if (!len) return;
                ctx.globalAlpha = alpha;
                ctx.fillStyle = color;
                ctx.beginPath();
                for (let i = 0; i < len; i++) {
                    const nd = nds[i];
                    const rr = Math.max((nd.r || 5) * 0.65, 2);
                    ctx.moveTo(nd.x + rr, nd.y); ctx.arc(nd.x, nd.y, rr, 0, Math.PI * 2);
                }
                ctx.fill();
            });
            ctx.globalAlpha = 1;
        } else {
            for (let i = 0; i < nCount; i++) {
                const nd = nodes[i];
                if (_netNodeHidden(nd)) continue;
                const r = nd.r || 5;
                if (nd.x + r < vx0 || nd.x - r > vx1 || nd.y + r < vy0 || nd.y - r > vy1) continue;
                const inActive = activeSet === null || activeSet.has(i);
                ctx.globalAlpha = inActive ? 1 : 0.12;
                this._drawNode(ctx, nd, i, lod);
            }
            ctx.globalAlpha = 1;
        }
        if (lod > 0) {
            if (activeSet !== null) {
                activeSet.forEach(i => {
                    if (!this.nodes[i]) return;
                    if (_netNodeHidden(this.nodes[i])) return;
                    this._drawLabel(ctx, this.nodes[i], i === sel);
                });
            } else if (this.hovered !== null && this.nodes[this.hovered]) {
                this._drawLabel(ctx, this.nodes[this.hovered], false);
            }
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

    _getNonFriendRgb() {
        return NET_NF_RGB;
    }

    _statusColor(status) {
        if (typeof STATUS_LIST !== 'undefined') {
            const s = STATUS_LIST.find(x => x.key === status);
            if (s) return s.color;
        }
        const map = { active: '#2DD48C', 'join me': '#2196F3', 'ask me': '#FF9800', busy: '#F44336' };
        return map[status] || '#555';
    }

    _drawNode(ctx, nd, idx, lod = 2) {
        const r        = nd.r || 5;
        const x        = nd.x, y = nd.y;
        const sel      = this.selected === idx || this.searchMatch === idx;
        const hov      = this.hovered  === idx;
        const col      = this._nodeColors(nd);
        const scaleInv = this._scaleInv;

        if (sel || hov) {
            ctx.beginPath();
            ctx.arc(x, y, r + 4, 0, Math.PI * 2);
            ctx.strokeStyle = sel ? 'rgba(80,180,255,0.9)' : 'rgba(80,180,255,0.45)';
            ctx.lineWidth = 2 * scaleInv;
            ctx.stroke();
        }

        ctx.beginPath();
        ctx.arc(x, y, r + 1.5, 0, Math.PI * 2);
        ctx.strokeStyle = col._colRing;
        ctx.lineWidth = 2 * scaleInv;
        ctx.stroke();

        const spr = lod < 2 ? null : this._sprite(nd);
        if (!spr) {
            ctx.beginPath();
            ctx.arc(x, y, r, 0, Math.PI * 2);
            ctx.fillStyle = col._colFill;
            ctx.fill();
            return;
        }

        ctx.drawImage(spr, x - r, y - r, r * 2, r * 2);
    }

    _drawLabel(ctx, nd, isSelected) {
        const commOn = _netCommunities && this._commReady && nd.comm >= 0;
        const ac     = commOn ? NET_COMM_RGB[nd.comm % NET_COMM_RGB.length] : this._getAccentRgb();
        const fs     = isSelected ? 9 : 8;
        const px     = 4, py = 2;
        const nodeR  = nd.r || 5;
        const yBadge = nd.y + nodeR + 5;

        ctx.save();
        ctx.font         = `${isSelected ? '700' : '600'} ${fs}px sans-serif`;
        ctx.textAlign    = 'center';
        ctx.textBaseline = 'top';

        const tw = ctx.measureText(nd.displayName).width;
        const bw = tw + px * 2;
        const bh = fs  + py * 2;

        ctx.fillStyle = `rgba(${ac.r},${ac.g},${ac.b},0.22)`;
        ctx.beginPath();
        if (ctx.roundRect) ctx.roundRect(nd.x - bw / 2, yBadge, bw, bh, 4);
        else               ctx.rect(nd.x - bw / 2, yBadge, bw, bh);
        ctx.fill();

        ctx.fillStyle = ac.raw;
        ctx.fillText(nd.displayName, nd.x, yBadge + py);

        ctx.restore();
    }

    /* ── Events ── */
    _bindEvents() {
        this._handlers = {
            wheel:      e  => this._onWheel(e),
            mousedown:  e  => this._onMouseDown(e),
            mousemove:  e  => this._onMouseMove(e),
            mouseup:    () => { this.dragging = null; this.canvas.classList.remove('dragging'); },
            mouseleave: () => { this.dragging = null; this.hovered = null; this._updateScoreCard(); this._scheduleRender(); },
            click:      e  => this._onClick(e),
        };
        const c = this.canvas;
        c.addEventListener('wheel',      this._handlers.wheel,      { passive: false });
        c.addEventListener('mousedown',  this._handlers.mousedown);
        c.addEventListener('mousemove',  this._handlers.mousemove);
        c.addEventListener('mouseup',    this._handlers.mouseup);
        c.addEventListener('mouseleave', this._handlers.mouseleave);
        c.addEventListener('click',      this._handlers.click);
    }

    destroy() {
        this._stopSim();
        clearTimeout(this._commTimer);
        this._commTimer = null;
        clearTimeout(this._scoreTimer);
        this._scoreTimer = null;
        netStopGroupFetch();
        this.cancelLoading();
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
    }

    _canvasToWorld(cx, cy) {
        return { x: (cx - this.tx) / this.scale, y: (cy - this.ty) / this.scale };
    }

    _hitTest(wx, wy) {
        for (let i = this.nodes.length - 1; i >= 0; i--) {
            const nd = this.nodes[i];
            if (_netNodeHidden(nd)) continue;
            const r  = (nd.r || 5) + 5;
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
        const newScale = Math.min(4, Math.max(0.04, this.scale * factor));
        this.tx = mx - (mx - this.tx) * (newScale / this.scale);
        this.ty = my - (my - this.ty) * (newScale / this.scale);
        this.scale = newScale;
        this._scheduleRender();
    }

    _onMouseDown(e) {
        if (e.button !== 0) return;
        const rect = this.canvas.getBoundingClientRect();
        const mx = e.clientX - rect.left, my = e.clientY - rect.top;
        const { x: wx, y: wy } = this._canvasToWorld(mx, my);
        const hit = this._hitTest(wx, wy);
        this._dragMoved = false;
        if (hit >= 0) {
            this.dragging = { type: 'node', idx: hit, ox: wx - this.nodes[hit].x, oy: wy - this.nodes[hit].y, sx: mx, sy: my };
            this.nodes[hit].pinned = true;
        } else {
            this.dragging = { type: 'pan', ox: mx - this.tx, oy: my - this.ty, sx: mx, sy: my };
            this.canvas.classList.add('dragging');
        }
    }

    _onMouseMove(e) {
        const rect = this.canvas.getBoundingClientRect();
        const mx = e.clientX - rect.left, my = e.clientY - rect.top;
        const { x: wx, y: wy } = this._canvasToWorld(mx, my);

        if (this.dragging) {
            if (Math.abs(mx - this.dragging.sx) + Math.abs(my - this.dragging.sy) > 3) this._dragMoved = true;
            if (this.dragging.type === 'node') {
                const nd = this.nodes[this.dragging.idx];
                nd.x = wx - this.dragging.ox;
                nd.y = wy - this.dragging.oy;
                nd.vx = nd.vy = 0;
            } else {
                this.tx = mx - this.dragging.ox;
                this.ty = my - this.dragging.oy;
            }
            this._scheduleRender();
            return;
        }
        const hit = this.scale >= 0.35 ? this._hitTest(wx, wy) : -1;
        const newHov = hit >= 0 ? hit : null;
        if (newHov !== this.hovered) {
            this.hovered = newHov;
            this._updateScoreCard();
            this._scheduleRender();
        }
    }

    _onClick(e) {
        if (this._dragMoved) { this._dragMoved = false; return; }
        const rect = this.canvas.getBoundingClientRect();
        const { x: wx, y: wy } = this._canvasToWorld(e.clientX - rect.left, e.clientY - rect.top);
        const hit = this._hitTest(wx, wy);
        if (hit >= 0 && (this.searchQuery || this.searchMatch !== null)) this._clearSearch();
        this.selected = (hit >= 0 && hit !== this.selected) ? hit : null;
        this._updateScoreCard();
        this._render();
    }
}

function rerenderNetworkTranslations() {
    if (!_netGraph) return;
    const panel = document.getElementById('netCommPanel');
    if (panel) panel.dataset.built = '';
    _netGraph._updateProgress();
    _netGraph._renderCommPanel();
    _netGraph._updateScoreCard();
}

document.documentElement.addEventListener('languagechange', rerenderNetworkTranslations);
