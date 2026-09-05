/* === Boop Emoji Modal === */

const BOOP_DEFAULT_EMOJIS = [
    { name: 'Angry',          file: 'Angry.webp' },
    { name: 'Blushing',       file: 'Blush.webp' },
    { name: 'Crying',         file: 'Crying.webp' },
    { name: 'Frown',          file: 'Frown.webp' },
    { name: 'Hand Wave',      file: 'Handwave.webp' },
    { name: 'Hang Ten',       file: 'Summer_Hangten.webp' },
    { name: 'In Love',        file: 'Inlove.webp' },
    { name: 'Jack O Lantern', file: 'Fall_Jackolantern.webp' },
    { name: 'Kiss',           file: 'Kiss.webp' },
    { name: 'Laugh',          file: 'Laugh.webp' },
    { name: 'Skull',          file: 'Fall_Skull.webp' },
    { name: 'Smile',          file: 'Smile.webp' },
    { name: 'Spooky Ghost',   file: 'Fall_Ghost.webp' },
    { name: 'Stoic',          file: 'Stoic.webp' },
    { name: 'Sunglasses',     file: 'Sunglasses.webp' },
    { name: 'Thinking',       file: 'Thinking.webp' },
    { name: 'Thumbs Down',    file: 'Dislike.webp' },
    { name: 'Thumbs Up',      file: 'Like.webp' },
    { name: 'Tongue Out',     file: 'Tongue.webp' },
    { name: 'Wow',            file: 'Wow.webp' },
    { name: 'Arrow Point',    file: 'Accessibility_Arrow.webp' },
    { name: "Can't see",      file: 'Accessibility_Blind.webp' },
    { name: 'Hourglass',      file: 'Accessibility_Hourglass.webp' },
    { name: 'Keyboard',       file: 'Accessibility_Keyboard.webp' },
    { name: 'No Headphones',  file: 'Accessibility_Deafened.webp' },
    { name: 'No Mic',         file: 'Accessibility_Muted.webp' },
    { name: 'Portal',         file: 'Accessibility_Portal.webp' },
    { name: 'Shush',          file: 'Accessibility_Shush.webp' },
    { name: 'Bats',           file: 'Fall_Bat.webp' },
    { name: 'Cloud',          file: 'Cloud.webp' },
    { name: 'Fire',           file: 'Fire.webp' },
    { name: 'Snow Fall',      file: 'Winter_Snowflake.webp' },
    { name: 'Snowball',       file: 'Snowball_-_emoji_animation_type.gif' },
    { name: 'Splash',         file: 'Summer_Splash.webp' },
    { name: 'Web',            file: 'Fall_Web.webp' },
    { name: 'Beer',           file: 'Beer.webp' },
    { name: 'Candy',          file: 'Fall_Candy.webp' },
    { name: 'Candy Cane',     file: 'Winter_Candycane.webp' },
    { name: 'Candy Corn',     file: 'Fall_CandyCorn.webp' },
    { name: 'Champagne',      file: 'Winter_Champagneclink.webp' },
    { name: 'Drink',          file: 'Summer_Coconut_Drink.webp' },
    { name: 'Gingerbread',    file: 'Winter_Gingerbreadman.webp' },
    { name: 'Ice Cream',      file: 'Summer_Icecream.webp' },
    { name: 'Pineapple',      file: 'Summer_Pineapple.webp' },
    { name: 'Pizza',          file: 'Pizza.webp' },
    { name: 'Tomato',         file: 'Tomato.webp' },
    { name: 'Beachball',      file: 'Summer_Beachball.webp' },
    { name: 'Coal',           file: 'Winter_Coal.webp' },
    { name: 'Confetti',       file: 'Winter_ConfettiPopper.webp' },
    { name: 'Gift',           file: 'Gift.webp' },
    { name: 'Gifts',          file: 'Winter_Gifts.webp' },
    { name: 'Life Ring',      file: 'Lifering.webp' },
    { name: 'Mistletoe',      file: 'Winter_Mistletoe.webp' },
    { name: 'Money',          file: 'Money.webp' },
    { name: 'Neon Shades',    file: 'Summer_Neonshades.webp' },
    { name: 'Sun Lotion',     file: 'Summer_Sunlotion.webp' },
    { name: 'Boo',            file: 'Fall_BOO.webp' },
    { name: 'Broken Heart',   file: 'Brokenheart.webp' },
    { name: 'Exclamation',    file: 'Exclaim.webp' },
    { name: 'Go',             file: 'Go.webp' },
    { name: 'Heart',          file: 'Love.webp' },
    { name: 'Music Note',     file: 'Music.webp' },
    { name: 'Question',       file: 'Question.webp' },
    { name: 'Stop',           file: 'Stop.webp' },
    { name: 'Zzz',            file: 'ZZZZ.webp' },
].map(e => ({
    id: `default_${e.name.replace(/ /g, '_').toLowerCase()}`,
    name: e.name,
    previewUrl: `assets/Emojis/Default/${e.file}`,
}));

const _boopDefaultById = new Map(BOOP_DEFAULT_EMOJIS.map(e => [e.id, e]));

let _boopContext      = null; // { userId, displayName }
let _boopSelectedId    = '';
let _boopTab           = 'default';
let _boopCustomEmojis  = [];
let _boopCustomState   = 'idle'; // idle | loading | loaded | empty

function boopEmojiName(emojiId) {
    if (!emojiId) return '';
    const known = _boopDefaultById.get(emojiId);
    if (known) return known.name;
    const custom = _boopCustomEmojis.find(e => e.id === emojiId);
    if (custom) return t('boop.custom_emoji', 'Custom Emoji');
    return emojiId.replace(/^default_/, '').replace(/_/g, ' ');
}

function boopEmojiPreviewUrl(emojiId) {
    if (!emojiId) return '';
    const known = _boopDefaultById.get(emojiId);
    if (known) return known.previewUrl;
    const custom = _boopCustomEmojis.find(e => e.id === emojiId);
    return custom ? custom.previewUrl : '';
}

function boopHasVrcPlus() {
    return Array.isArray(currentVrcUser?.tags) && currentVrcUser.tags.includes('system_supporter');
}

function openBoopModal(userId, displayName) {
    if (!userId) return;
    _boopContext     = { userId, displayName: displayName || '' };
    _boopSelectedId  = '';
    _boopTab         = 'default';
    _boopCustomState = 'idle';

    let overlay = document.getElementById('boopModalOverlay');
    if (overlay) overlay.remove();

    overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    overlay.id = 'boopModalOverlay';
    overlay.style.zIndex = '10003';
    overlay.style.display = 'flex'; // inline display required by _closeTopModal (Escape)
    overlay.innerHTML = `
        <div class="modal-box wide narrow">
            ${renderModalBar(t('boop.title', 'Send a Boop'), [modalCloseAction('closeBoopModal()')])}
            <div class="modal-card">
            <div class="modal-msg" style="margin:0 0 14px;">${esc(tf('boop.target', { name: _boopContext.displayName }, `To: ${_boopContext.displayName}`))}</div>
            <div class="fd-tabs">
                <button class="fd-tab active" id="boopTabDefault" onclick="switchBoopTab('default')">${esc(t('boop.tab_default', 'Default Emojis'))}</button>
                ${boopHasVrcPlus() ? `<button class="fd-tab" id="boopTabCustom" onclick="switchBoopTab('custom')">${esc(t('boop.tab_custom', 'Custom'))}</button>` : ''}
            </div>
            <div id="boopGrid" style="max-height:44vh;overflow-y:auto;display:grid;grid-template-columns:repeat(auto-fill,minmax(84px,1fr));gap:6px;padding:6px;background:var(--bg-input);border-radius:8px;"></div>
            <div style="display:flex;align-items:center;gap:8px;margin-top:10px;min-height:32px;">
                <span id="boopHint" style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);flex:1;">${esc(t('boop.no_selection_hint', 'No emoji selected. The default boop will be sent.'))}</span>
                <span id="boopSelectedName" style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx2);font-weight:600;flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;display:none;"></span>
                <button class="vrcn-button" id="boopClearBtn" onclick="selectBoopEmoji('')" style="display:none;">${esc(t('boop.clear', 'Clear'))}</button>
                <button class="vrcn-button" id="boopRefreshBtn" onclick="refreshBoopCustomEmojis()" title="${esc(t('common.refresh', 'Refresh'))}" style="display:none;"><span class="msi">refresh</span></button>
            </div>
            </div>
            <div class="modal-foot"><div class="modal-foot-spacer"></div>
                <button class="vrcn-button" id="boopSendBtn" onclick="sendBoopFromModal()">${esc(t('boop.send', 'Send Boop'))}</button>
            </div>
        </div>`;
    document.body.appendChild(overlay);
    overlay.addEventListener('click', e => { if (e.target === overlay) closeBoopModal(); });

    switchBoopTab('default');
}

function closeBoopModal() {
    const overlay = document.getElementById('boopModalOverlay');
    if (overlay) overlay.remove();
    _boopContext    = null;
    _boopSelectedId = '';
}

function switchBoopTab(tab) {
    if (!_boopContext) return;
    if (tab === 'custom' && !boopHasVrcPlus()) return;
    _boopTab = tab;

    const dBtn = document.getElementById('boopTabDefault');
    const cBtn = document.getElementById('boopTabCustom');
    if (dBtn) dBtn.classList.toggle('active', tab === 'default');
    if (cBtn) cBtn.classList.toggle('active', tab === 'custom');

    const refreshBtn = document.getElementById('boopRefreshBtn');
    if (refreshBtn) refreshBtn.style.display = tab === 'custom' ? '' : 'none';

    if (tab === 'custom' && _boopCustomState === 'idle') refreshBoopCustomEmojis();
    else _renderBoopGrid();
}

function refreshBoopCustomEmojis() {
    if (!boopHasVrcPlus()) return;
    _boopCustomState = 'loading';
    _renderBoopGrid();
    sendToCS({ action: 'invGetFiles', tag: 'emoji' });
}

function _renderBoopGrid() {
    const grid = document.getElementById('boopGrid');
    if (!grid) return;

    const msg = html => `<div style="grid-column:1/-1;text-align:center;padding:24px;font-size:calc(11px + var(--fs-off, 0px));color:var(--tx2);">${html}</div>`;

    if (_boopTab === 'custom') {
        if (_boopCustomState === 'loading') { grid.innerHTML = msg(esc(t('boop.loading_emojis', 'Loading emojis...'))); return; }
        if (!_boopCustomEmojis.length)      { grid.innerHTML = msg(esc(t('boop.no_custom', 'No custom emojis found.'))); return; }
        grid.innerHTML = _boopCustomEmojis.map(e => _boopTile(e.id, e.previewUrl, t('boop.custom_emoji', 'Custom Emoji'), true)).join('');
        return;
    }

    grid.innerHTML = BOOP_DEFAULT_EMOJIS.map(e => _boopTile(e.id, e.previewUrl, e.name, false)).join('');
}

function _boopTile(id, url, label, imageOnly) {
    const sel = _boopSelectedId === id;
    const border = sel ? '1px solid var(--accent)' : '1px solid var(--brd)';
    const bg     = sel ? 'var(--bg-hover)' : 'transparent';
    return `
        <button type="button" title="${esc(label)}" onclick="selectBoopEmoji('${jsq(id)}')"
            style="position:relative;display:flex;flex-direction:column;align-items:center;gap:4px;padding:6px;border:${border};background:${bg};border-radius:10px;cursor:pointer;transition:background .12s,border-color .12s;font-family:inherit;">
            ${sel ? `<span class="msi" style="position:absolute;top:2px;right:2px;font-size:14px;color:var(--accent);">check_circle</span>` : ''}
            <img src="${esc(url)}" alt="" loading="lazy" style="width:52px;height:52px;object-fit:contain;" onerror="this.style.visibility='hidden'">
            ${imageOnly ? '' : `<span style="font-size:calc(10px + var(--fs-off, 0px));color:${sel ? 'var(--tx1)' : 'var(--tx2)'};max-width:100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${esc(label)}</span>`}
        </button>`;
}

function selectBoopEmoji(id) {
    _boopSelectedId = (_boopSelectedId === id) ? '' : id;
    _renderBoopGrid();

    const nameEl  = document.getElementById('boopSelectedName');
    const clearEl = document.getElementById('boopClearBtn');
    const hintEl  = document.getElementById('boopHint');
    if (nameEl) {
        nameEl.textContent   = _boopSelectedId ? boopEmojiName(_boopSelectedId) : '';
        nameEl.style.display = _boopSelectedId ? '' : 'none';
    }
    if (clearEl) clearEl.style.display = _boopSelectedId ? '' : 'none';
    if (hintEl)  hintEl.style.display  = _boopSelectedId ? 'none' : '';
}

function sendBoopFromModal() {
    if (!_boopContext) return;
    const { userId } = _boopContext;
    const emojiId = _boopSelectedId;

    if (typeof msgrRegisterBoopSent === 'function') msgrRegisterBoopSent(userId, emojiId);
    sendToCS({ action: 'vrcBoop', userId, emojiId });
    closeBoopModal();
}

// Called from messages.js when invFiles arrives
function onBoopModalFilesLoaded(files, tag) {
    if (tag !== 'emoji') return;
    _boopCustomEmojis = (files || [])
        .filter(f => f && f.id && f.fileUrl)
        .map(f => ({ id: f.id, previewUrl: f.fileUrl }));
    _boopCustomState = _boopCustomEmojis.length ? 'loaded' : 'empty';
    if (document.getElementById('boopModalOverlay') && _boopTab === 'custom') _renderBoopGrid();
}
