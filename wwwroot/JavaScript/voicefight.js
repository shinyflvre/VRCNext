// Voice Fight - voice-triggered soundboard using VOSK
let vfRunning = false;
let vfItems = [];
let _vfWordTimers = {};

function vfOnTabOpen() {
    sendToCS({ action: 'vfGetDevices' });
    sendToCS({ action: 'vfGetItems' });
}

// ── State ──────────────────────────────────────────────────────────────────

function handleVfState(p) {
    vfRunning = p.running;
    const dot = document.getElementById('vfDot');
    const txt = document.getElementById('vfStatusText');
    const btn = document.getElementById('vfConnBtn');
    if (!dot) return;
    if (vfRunning) {
        dot.className = 'sf-dot online';
        txt.textContent = 'Listening…';
        btn.innerHTML = '<span class="msi" style="font-size:16px;">stop</span> Stop';
    } else {
        dot.className = 'sf-dot offline';
        txt.textContent = 'Not running';
        btn.innerHTML = '<span class="msi" style="font-size:16px;">play_arrow</span> Start';
        updateVfMeter(0);
    }
}

function vfConnect() {
    if (vfRunning) {
        sendToCS({ action: 'vfStop' });
    } else {
        const sel = document.getElementById('vfDeviceSelect');
        const deviceIndex = sel ? parseInt(sel.value) : 0;
        sendToCS({ action: 'vfStart', deviceIndex });
    }
}

// ── Devices ────────────────────────────────────────────────────────────────

function populateVfDevices(p) {
    const sel = document.getElementById('vfDeviceSelect');
    if (!sel) return;
    sel.innerHTML = '';
    const devices = p.devices || [];
    if (devices.length === 0) {
        sel.innerHTML = '<option value="0">No microphone found</option>';
        if (sel._vnRefresh) sel._vnRefresh();
        return;
    }
    const targetIndex = Math.min(p.savedIndex ?? 0, devices.length - 1);
    devices.forEach((name, i) => {
        const opt = document.createElement('option');
        opt.value = String(i);
        opt.textContent = name;
        sel.appendChild(opt);
    });
    sel.selectedIndex = targetIndex;
    if (sel._vnRefresh) sel._vnRefresh();
    const stopInput = document.getElementById('vfStopWordInput');
    if (stopInput && p.stopWord != null) stopInput.value = p.stopWord;
    const muteTalkChk = document.getElementById('vfMuteTalkToggle');
    if (muteTalkChk && p.muteTalk != null) muteTalkChk.checked = !!p.muteTalk;
}

function vfSetMuteTalk(enabled) {
    sendToCS({ action: 'vfSetMuteTalk', enabled: !!enabled });
}

function vfSetInputDevice(val) {
    const idx = parseInt(val) || 0;
    sendToCS({ action: 'vfSetInputDevice', deviceIndex: idx });
}

// ── Meter ──────────────────────────────────────────────────────────────────

function updateVfMeter(level) {
    const bar = document.getElementById('vfMeterBar');
    if (!bar) return;
    const pct = Math.round(Math.min(1, Math.max(0, level)) * 100);
    bar.style.width = pct + '%';
    bar.style.background = pct > 80 ? 'var(--err)' : pct > 50 ? 'var(--warn)' : 'var(--ok)';
}

// ── Items ──────────────────────────────────────────────────────────────────

function renderVfItems(items) {
    vfItems = items || [];
    const el = document.getElementById('vfItems');
    if (!el) return;
    if (vfItems.length === 0) {
        el.innerHTML = '<div class="empty-msg">No sounds added yet.</div>';
        return;
    }
    el.innerHTML = vfItems.map((item, i) => buildVfItemHtml(item, i)).join('');
}

function buildVfItemHtml(item, i) {
    const word = esc(item.word || '');
    const files = item.files || [];
    const filesHtml = files.map((f, si) => buildVfSoundHtml(f, i, si)).join('');
    return `<div class="vf-item" data-idx="${i}">
  <div class="vf-item-header">
    <input class="vf-word-input" placeholder="Trigger word…" value="${word}"
      oninput="vfWordChanged(${i}, this.value)"
      onblur="vfSetWord(${i}, this.value)">
    <button class="vf-btn-icon vf-btn-del" onclick="vfDeleteItem(${i})" title="Remove item"><span class="msi">delete</span></button>
  </div>
  <div class="vf-sounds" data-item="${i}">${filesHtml || '<div class="vf-no-sounds">No sounds yet</div>'}</div>
  <button class="vf-add-btn" onclick="vfAddSoundToItem(${i})"><span class="msi">add</span> Add Sound</button>
</div>`;
}

function buildVfSoundHtml(file, i, si) {
    const name = esc(file.fileName || file.filePath?.split(/[\\/]/).pop() || 'Unknown');
    const dur = formatVfTime(file.durationMs || 0);
    const vol = Math.round(file.volumePercent ?? 100);
    return `<div class="vf-sound" data-item-idx="${i}" data-sound-idx="${si}">
  <div class="vf-sound-top">
    <div class="vf-item-info">
      <span class="vf-filename">${name}</span>
      <span class="vf-length">LENGTH: ${dur}</span>
    </div>
    <div class="vf-item-actions">
      <button class="vf-btn-icon" onclick="vfPlaySound(${i},${si})" title="Test play"><span class="msi">play_arrow</span></button>
      <button class="vf-btn-icon" onclick="vfStopSound()" title="Stop playback"><span class="msi">stop</span></button>
      <button class="vf-btn-icon vf-btn-del" onclick="vfDeleteSound(${i},${si})" title="Remove"><span class="msi">close</span></button>
    </div>
  </div>
  <div class="vf-vol-row">
    <span class="vf-vol-label">VOL</span>
    <input type="range" class="vf-vol-slider" min="0" max="100" value="${vol}"
      oninput="this.nextElementSibling.textContent=this.value+'%';vfSetVolume(${i},${si},this.value)">
    <span class="vf-vol-val">${vol}%</span>
  </div>
</div>`;
}

function vfOnItemAdded(p) {
    vfItems.push(p);
    const el = document.getElementById('vfItems');
    if (!el) return;
    if (vfItems.length === 1) el.innerHTML = '';
    const div = document.createElement('div');
    div.innerHTML = buildVfItemHtml(p, vfItems.length - 1);
    el.appendChild(div.firstElementChild);
}

function vfOnSoundAdded(p) {
    const item = vfItems[p.itemIndex];
    if (!item) return;
    if (!item.files) item.files = [];
    item.files.push(p);
    const soundsEl = document.querySelector(`.vf-sounds[data-item="${p.itemIndex}"]`);
    if (!soundsEl) return;
    const noSounds = soundsEl.querySelector('.vf-no-sounds');
    if (noSounds) noSounds.remove();
    const div = document.createElement('div');
    div.innerHTML = buildVfSoundHtml(p, p.itemIndex, p.soundIndex);
    soundsEl.appendChild(div.firstElementChild);
}

function vfAddSound() {
    sendToCS({ action: 'vfAddSound' });
}

function vfAddSoundToItem(i) {
    sendToCS({ action: 'vfAddSoundToItem', itemIndex: i });
}

function vfDeleteItem(i) {
    sendToCS({ action: 'vfDeleteItem', index: i });
}

function vfDeleteSound(i, si) {
    sendToCS({ action: 'vfDeleteSound', itemIndex: i, soundIndex: si });
}

function vfPlaySound(i, si) {
    sendToCS({ action: 'vfPlaySound', itemIndex: i, soundIndex: si });
}

function vfWordChanged(i, word) {
    clearTimeout(_vfWordTimers[i]);
    _vfWordTimers[i] = setTimeout(() => vfSetWord(i, word), 600);
}

function vfSetWord(i, word) {
    clearTimeout(_vfWordTimers[i]);
    if (vfItems[i]) vfItems[i].word = word;
    sendToCS({ action: 'vfSetWord', index: i, word });
}

function vfSetVolume(i, si, vol) {
    if (vfItems[i]?.files?.[si]) vfItems[i].files[si].volumePercent = parseFloat(vol);
    sendToCS({ action: 'vfSetVolume', itemIndex: i, soundIndex: si, volume: parseFloat(vol) });
}

// ── Stop Command ──────────────────────────────────────────────────────────

let _vfStopWordTimer = null;

function vfStopWordChanged(word) {
    clearTimeout(_vfStopWordTimer);
    _vfStopWordTimer = setTimeout(() => vfSetStopWord(word), 600);
}

function vfSetStopWord(word) {
    clearTimeout(_vfStopWordTimer);
    sendToCS({ action: 'vfSetStopWord', word });
}

function vfStopSound() {
    sendToCS({ action: 'vfStopSound' });
}

// ── Recognized text display ───────────────────────────────────────────────

let _vfClearTimer = null;

function vfOnRecognized(text, isPartial) {
    const el = document.getElementById('vfRecognizedText');
    if (!el) return;
    el.textContent = text;
    el.classList.toggle('vf-recognized-partial', !!isPartial);
    el.classList.toggle('vf-recognized-final', !isPartial);
    if (!isPartial) {
        clearTimeout(_vfClearTimer);
        _vfClearTimer = setTimeout(() => {
            if (el) {
                el.classList.remove('vf-recognized-final');
                el.textContent = '—';
            }
        }, 3000);
    }
}

// ── Keyword flash ──────────────────────────────────────────────────────────

function vfOnKeyword(word) {
    const items = document.querySelectorAll('.vf-item');
    items.forEach(el => {
        const idx = parseInt(el.dataset.idx);
        const item = vfItems[idx];
        if (item && (item.word || '').toLowerCase().trim() === word.toLowerCase().trim()) {
            el.classList.add('vf-item-flash');
            setTimeout(() => el.classList.remove('vf-item-flash'), 600);
        }
    });
}

// ── Helpers ────────────────────────────────────────────────────────────────

function formatVfTime(ms) {
    if (!ms || ms <= 0) return '00:00';
    const s = Math.round(ms / 1000);
    const m = Math.floor(s / 60);
    const sec = s % 60;
    return String(m).padStart(2, '0') + ':' + String(sec).padStart(2, '0');
}
