// Kikitan XD — Live STT + Translation via Groq, output to VRChat Chatbox OSC

let kxdRunning = false;
let _kxdDevicesPayload = null;
let _kxdSavedTtsVoice = '';
let kxdNoiseGatePct = 10;
let kxdLastMeterLevel = 0;
let _kxdPendingConnect = false;
let _kxdBlockWords = [];
let _kxdBlockSentences = [];

const KXD_SOURCE_LANGS = [
    { code: 'auto', name: 'Auto Detect' },
    { code: 'en', name: 'English' },
    { code: 'ja', name: 'Japanese' },
    { code: 'zh', name: 'Chinese' },
    { code: 'ko', name: 'Korean' },
    { code: 'de', name: 'German' },
    { code: 'fr', name: 'French' },
    { code: 'es', name: 'Spanish' },
    { code: 'pt', name: 'Portuguese' },
    { code: 'ru', name: 'Russian' },
    { code: 'ar', name: 'Arabic' },
    { code: 'it', name: 'Italian' },
    { code: 'nl', name: 'Dutch' },
    { code: 'pl', name: 'Polish' },
    { code: 'sv', name: 'Swedish' },
    { code: 'tr', name: 'Turkish' },
    { code: 'id', name: 'Indonesian' },
    { code: 'fi', name: 'Finnish' },
    { code: 'no', name: 'Norwegian' },
    { code: 'cs', name: 'Czech' },
    { code: 'hu', name: 'Hungarian' },
    { code: 'ro', name: 'Romanian' },
    { code: 'uk', name: 'Ukrainian' },
];

const KXD_TARGET_LANGS = [
    { code: 'en', name: 'English' },
    { code: 'ja', name: 'Japanese' },
    { code: 'zh', name: 'Chinese' },
    { code: 'ko', name: 'Korean' },
    { code: 'de', name: 'German' },
    { code: 'fr', name: 'French' },
    { code: 'es', name: 'Spanish' },
    { code: 'pt', name: 'Portuguese' },
    { code: 'ru', name: 'Russian' },
    { code: 'ar', name: 'Arabic' },
    { code: 'it', name: 'Italian' },
    { code: 'nl', name: 'Dutch' },
    { code: 'pl', name: 'Polish' },
    { code: 'sv', name: 'Swedish' },
    { code: 'tr', name: 'Turkish' },
    { code: 'id', name: 'Indonesian' },
    { code: 'fi', name: 'Finnish' },
    { code: 'no', name: 'Norwegian' },
    { code: 'cs', name: 'Czech' },
    { code: 'hu', name: 'Hungarian' },
    { code: 'ro', name: 'Romanian' },
    { code: 'uk', name: 'Ukrainian' },
    { code: 'th', name: 'Thai' },
    { code: 'vi', name: 'Vietnamese' },
    { code: 'hi', name: 'Hindi' },
];

function kxdOnTabOpen() {
    sendToCS({ action: 'kxdGetDevices' });
}

function kxdBuildConnButtonHtml() {
    return kxdRunning
        ? `<span class="msi" style="font-size:16px;">stop</span> Stop`
        : `<span class="msi" style="font-size:16px;">play_arrow</span> Start`;
}

function kxdSyncStateUi() {
    const dot = document.getElementById('kxdDot');
    const txt = document.getElementById('kxdStatusText');
    const btn = document.getElementById('kxdConnBtn');
    if (dot) dot.className = kxdRunning ? 'sf-dot online' : 'sf-dot offline';
    if (txt) txt.textContent = kxdRunning ? t('kikitan.status.listening', 'Listening...') : t('kikitan.status.not_running', 'Not running');
    if (btn) btn.innerHTML = kxdBuildConnButtonHtml();
}

function handleKxdState(p) {
    kxdRunning = !!p.running;
    kxdSyncStateUi();
    if (!kxdRunning) updateKxdMeter(0);
    const bdKxd = document.getElementById('badgeKxd');
    if (bdKxd) bdKxd.classList.toggle('tb-active', kxdRunning);
}

function kxdConnect() {
    if (kxdRunning) {
        sendToCS({ action: 'kxdStop' });
        return;
    }

    if (!_kxdDevicesPayload) {
        _kxdPendingConnect = true;
        sendToCS({ action: 'kxdGetDevices' });
        return;
    }

    const dev = audioDeviceValue('kxdDeviceSelect');
    const apiKey = (document.getElementById('kxdApiKey')?.value || _kxdDevicesPayload.apiKey || '').trim();
    const srcSel = document.getElementById('kxdSourceLang');
    const tgtSel = document.getElementById('kxdTargetLang');
    const sourceLang = srcSel?.value || _kxdDevicesPayload.sourceLang || 'auto';
    const targetLang = tgtSel?.value || _kxdDevicesPayload.targetLang || 'en';
    const translateEnabled = document.getElementById('kxdTranslateToggle')?.checked ?? !!_kxdDevicesPayload.translateEnabled;
    const oscEnabled = document.getElementById('kxdOscToggle')?.checked ?? !!_kxdDevicesPayload.oscEnabled;
    const partialOsc = document.getElementById('kxdPartialToggle')?.checked ?? !!_kxdDevicesPayload.partialOsc;
    const disableNonSpeech = document.getElementById('kxdNonSpeechToggle')?.checked ?? (_kxdDevicesPayload.disableNonSpeech !== false);
    const chatboxNotify = document.getElementById('kxdNotifyToggle')?.checked ?? (_kxdDevicesPayload.chatboxNotify !== false);
    const model = document.getElementById('kxdModel')?.value || _kxdDevicesPayload.model || 'groq';
    const googleApiKey = (document.getElementById('kxdGoogleApiKey')?.value || _kxdDevicesPayload.googleApiKey || '').trim();

    if (model === 'google') {
        if (!googleApiKey) {
            showToast(false, t('kikitan.google_api_key.missing', 'Please enter your Google API key.'));
            return;
        }
    } else if (model !== 'local' && !apiKey) {
        showToast(false, t('kikitan.api_key.missing', 'Please enter your Groq API key.'));
        return;
    }

    const personality = document.getElementById('kxdPersonality')?.value || _kxdDevicesPayload.personality || 'raw';
    const startMsg = { action: 'kxdStart', apiKey, googleApiKey, model, sourceLang, targetLang, translateEnabled, oscEnabled, partialOsc, disableNonSpeech, chatboxNotify, noiseGatePct: kxdNoiseGatePct, personality, blockWords: _kxdBlockWords, blockSentences: _kxdBlockSentences };
    if (dev) { startMsg.deviceId = dev.id; startMsg.deviceName = dev.name; }
    sendToCS(startMsg);
}

function ttsBuildCascade(prefix, voices, savedVoice, defaultLabel) {
    const isEdge = document.getElementById(prefix + 'TtsEngine')?.value === 'edge';
    document.querySelectorAll('.' + prefix + '-edge-only').forEach(el => {
        el.style.display = isEdge ? '' : 'none';
    });

    const langSel   = document.getElementById(prefix + 'TtsLang');
    const genderSel = document.getElementById(prefix + 'TtsGender');
    const voiceSel  = document.getElementById(prefix + 'TtsVoice');
    if (!voiceSel) return;

    if (!isEdge) {
        let html = `<option value="">${esc(defaultLabel)}</option>`;
        (voices || []).forEach(v => { html += `<option value="${esc(v.id)}">${esc(v.label || v.id)}</option>`; });
        voiceSel.innerHTML = html;
        voiceSel.value = (voices || []).some(v => v.id === savedVoice) ? savedVoice : '';
        if (voiceSel._vnRefresh) voiceSel._vnRefresh();
        return;
    }

    const saved = (voices || []).find(v => v.id === savedVoice);

    if (langSel) {
        const seen = new Map();
        (voices || []).forEach(v => { if (!seen.has(v.locale)) seen.set(v.locale, v.localeName || v.locale); });
        const wanted = langSel.dataset.pick || saved?.locale || 'en-US';
        const locales = [...seen.entries()];
        langSel.innerHTML = locales.map(([code, name]) =>
            `<option value="${esc(code)}">${esc(name)}</option>`).join('');
        langSel.value = seen.has(wanted) ? wanted : (locales[0]?.[0] || '');
        langSel.dataset.pick = langSel.value;
        if (langSel._vnRefresh) langSel._vnRefresh();
    }

    const locale = langSel?.value || '';
    const inLocale = (voices || []).filter(v => v.locale === locale);

    if (genderSel) {
        const genders = [...new Set(inLocale.map(v => v.gender).filter(Boolean))];
        const wanted = genderSel.dataset.pick || saved?.gender || 'Female';
        genderSel.innerHTML = genders.map(g =>
            `<option value="${esc(g)}">${esc(t('tts.gender.' + g.toLowerCase(), g))}</option>`).join('');
        genderSel.value = genders.includes(wanted) ? wanted : (genders[0] || '');
        genderSel.dataset.pick = genderSel.value;
        if (genderSel._vnRefresh) genderSel._vnRefresh();
    }

    const gender = genderSel?.value || '';
    const finalList = inLocale.filter(v => !gender || v.gender === gender);
    voiceSel.innerHTML = finalList.map(v =>
        `<option value="${esc(v.id)}">${esc(v.label || v.id)}</option>`).join('');
    voiceSel.value = finalList.some(v => v.id === savedVoice) ? savedVoice : (finalList[0]?.id || '');
    if (voiceSel._vnRefresh) voiceSel._vnRefresh();
}

function kxdTtsHandleDevices(p) {
    if (p.target !== 'kxd') return;
    if (p.devices) kxdTtsFillDevices(p.devices, audioSelectionFromSelect(document.getElementById('kxdTtsDevice')));
    kxdTtsFillVoices(p.voices, _kxdSavedTtsVoice);
}

function kxdTtsEngineChanged() {
    const eng = document.getElementById('kxdTtsEngine')?.value || 'sapi';
    const voiceSel = document.getElementById('kxdTtsVoice');
    if (voiceSel) {
        voiceSel.innerHTML = `<option value="">${esc(t('tts.loading', 'Loading voices...'))}</option>`;
        if (voiceSel._vnRefresh) voiceSel._vnRefresh();
    }
    sendToCS({ action: 'getTtsDevices', target: 'kxd', engine: eng });
    kxdSaveSettings();
}

function kxdTtsPreviewVoice(voice) {
    if (!voice) return;
    const dev = audioDeviceValue('kxdTtsDevice') || { id: '', name: '' };
    sendToCS({
        action: 'ttsPreview',
        text: t('tts.preview_line', 'This is how this voice sounds.'),
        engine: document.getElementById('kxdTtsEngine')?.value || 'sapi',
        voice,
        deviceId: dev.id,
        deviceName: dev.name,
        rate: parseInt(document.getElementById('kxdTtsRate')?.value ?? '0', 10),
    });
}

let _kxdTtsVoices = [];

function kxdTtsFillVoices(voices, saved) {
    _kxdTtsVoices = voices || [];
    ttsBuildCascade('kxd', _kxdTtsVoices, saved, t('kikitan.tts.voice_default', 'Default voice'));
}

function kxdTtsFilterChanged() {
    const lang = document.getElementById('kxdTtsLang');
    const gen  = document.getElementById('kxdTtsGender');
    if (lang) lang.dataset.pick = lang.value;
    if (gen)  gen.dataset.pick  = gen.value;
    ttsBuildCascade('kxd', _kxdTtsVoices, '', t('kikitan.tts.voice_default', 'Default voice'));
    kxdSaveSettings();
}

function kxdTtsFillDevices(devices, selection) {
    audioFillDeviceSelect(document.getElementById('kxdTtsDevice'), devices, selection);
}

function kxdTtsTest() {
    const dev = audioDeviceValue('kxdTtsDevice') || { id: '', name: '' };
    sendToCS({
        action: 'ttsTest',
        text: t('kikitan.tts.test_line', 'VRCNext text to speech is working.'),
        engine: document.getElementById('kxdTtsEngine')?.value || 'sapi',
        voice: document.getElementById('kxdTtsVoice')?.value || '',
        deviceId: dev.id,
        deviceName: dev.name,
        rate: parseInt(document.getElementById('kxdTtsRate')?.value ?? '0', 10),
        volume: 100,
    });
}

function populateKxdDevices(p) {
    _kxdDevicesPayload = p;
    if (_kxdPendingConnect) {
        _kxdPendingConnect = false;
        kxdConnect();
        return;
    }

    audioFillDeviceSelect(document.getElementById('kxdDeviceSelect'), p.devices, p.input);

    const ttsToggle = document.getElementById('kxdTtsToggle');
    if (ttsToggle) ttsToggle.checked = !!p.ttsEnabled;

    _kxdSavedTtsVoice = p.ttsVoice || '';
    const ttsEngSel = document.getElementById('kxdTtsEngine');
    if (ttsEngSel) {
        ttsEngSel.value = p.ttsEngine || 'sapi';
        if (ttsEngSel._vnRefresh) ttsEngSel._vnRefresh();
    }
    kxdTtsFillDevices(p.ttsDevices, p.tts);
    kxdTtsFillVoices((p.ttsVoices || []).map(v => ({ id: v, label: v })), _kxdSavedTtsVoice);
    if ((p.ttsEngine || 'sapi') === 'edge') {
        sendToCS({ action: 'getTtsDevices', target: 'kxd', engine: 'edge' });
    }

    const ttsRateEl = document.getElementById('kxdTtsRate');
    if (ttsRateEl) {
        ttsRateEl.value = String(p.ttsRate ?? 0);
        const rv = document.getElementById('kxdTtsRateVal');
        if (rv) rv.textContent = String(p.ttsRate ?? 0);
    }

    const apiKeyEl = document.getElementById('kxdApiKey');
    if (apiKeyEl && p.apiKey) apiKeyEl.value = p.apiKey;

    const googleApiKeyEl = document.getElementById('kxdGoogleApiKey');
    if (googleApiKeyEl && p.googleApiKey) googleApiKeyEl.value = p.googleApiKey;

    const modelSel = document.getElementById('kxdModel');
    if (modelSel) {
        modelSel.value = p.model || 'groq';
        if (modelSel._vnRefresh) modelSel._vnRefresh();
    }
    kxdApplyModelVisibility();
    if (_kxdLocalState) kxdRenderLocalState(_kxdLocalState);

    const srcSel = document.getElementById('kxdSourceLang');
    if (srcSel) {
        if (!srcSel.dataset.built) {
            srcSel.innerHTML = buildKxdLangOptions(KXD_SOURCE_LANGS, p.sourceLang || 'auto');
            srcSel.dataset.built = '1';
        } else if (p.sourceLang) {
            srcSel.value = p.sourceLang;
        }
        if (srcSel._vnRefresh) srcSel._vnRefresh();
    }

    const tgtSel = document.getElementById('kxdTargetLang');
    if (tgtSel) {
        if (!tgtSel.dataset.built) {
            tgtSel.innerHTML = buildKxdLangOptions(KXD_TARGET_LANGS, p.targetLang || 'en');
            tgtSel.dataset.built = '1';
        } else if (p.targetLang) {
            tgtSel.value = p.targetLang;
        }
        if (tgtSel._vnRefresh) tgtSel._vnRefresh();
    }

    const profTgtSel = document.getElementById('kxdProfileTargetLang');
    if (profTgtSel) {
        if (!profTgtSel.dataset.built) {
            profTgtSel.innerHTML = buildKxdLangOptions(KXD_TARGET_LANGS, p.profileTargetLang || 'en');
            profTgtSel.dataset.built = '1';
        } else if (p.profileTargetLang) {
            profTgtSel.value = p.profileTargetLang;
        }
        if (profTgtSel._vnRefresh) profTgtSel._vnRefresh();
    }

    const profTransToggle = document.getElementById('kxdProfileTransToggle');
    if (profTransToggle && p.profileTranslationEnabled != null) profTransToggle.checked = !!p.profileTranslationEnabled;

    const persSel = document.getElementById('kxdPersonality');
    if (persSel && p.personality) {
        persSel.value = p.personality;
        if (persSel._vnRefresh) persSel._vnRefresh();
    }

    _kxdBlockWords = Array.isArray(p.blockWords) ? p.blockWords : [];
    _kxdBlockSentences = Array.isArray(p.blockSentences) ? p.blockSentences : [];
    kxdRenderBlockChips('word');
    kxdRenderBlockChips('sentence');

    window._kxdProfileTranslationEnabled = p.profileTranslationEnabled !== false;
    window._kxdProfileTargetLang = p.profileTargetLang || 'en';
    window._kxdApiKeyPresent = !!(p.apiKey && p.apiKey.length > 0);

    const transToggle = document.getElementById('kxdTranslateToggle');
    if (transToggle && p.translateEnabled != null) transToggle.checked = !!p.translateEnabled;

    const oscToggle = document.getElementById('kxdOscToggle');
    if (oscToggle && p.oscEnabled != null) oscToggle.checked = !!p.oscEnabled;
    const partialToggle = document.getElementById('kxdPartialToggle');
    if (partialToggle && p.partialOsc != null) partialToggle.checked = !!p.partialOsc;

    const nonSpeechToggle = document.getElementById('kxdNonSpeechToggle');
    if (nonSpeechToggle) nonSpeechToggle.checked = p.disableNonSpeech !== false;

    const notifyToggle = document.getElementById('kxdNotifyToggle');
    if (notifyToggle) notifyToggle.checked = p.chatboxNotify !== false;

    if (p.noiseGatePct != null) {
        kxdNoiseGatePct = p.noiseGatePct;
        const slider = document.getElementById('kxdGateSlider');
        if (slider) slider.value = kxdNoiseGatePct;
        const label = document.getElementById('kxdGateVal');
        if (label) label.textContent = kxdNoiseGatePct + '%';
        updateKxdMeter(kxdLastMeterLevel);
    }

    const sileroToggle = document.getElementById('kxdSileroToggle');
    if (sileroToggle) sileroToggle.checked = p.localVadEnabled !== false;

    kxdUpdateTranslateVisibility();
}

function updateKxdMeter(level) {
    kxdLastMeterLevel = level;
    const bar = document.getElementById('kxdMeterBar');
    if (!bar) return;
    const pct = Math.round(Math.min(1, Math.max(0, level)) * 100);
    bar.style.width = pct + '%';
    bar.style.background = pct < kxdNoiseGatePct
        ? 'var(--tx3)'
        : pct > 80 ? 'var(--err)' : pct > 50 ? 'var(--warn)' : 'var(--ok)';
    const mark = document.getElementById('kxdGateMark');
    if (mark) mark.style.left = kxdNoiseGatePct + '%';
}

function kxdSetNoiseGate(val) {
    kxdNoiseGatePct = parseInt(val, 10) || 0;
    const label = document.getElementById('kxdGateVal');
    if (label) label.textContent = kxdNoiseGatePct + '%';
    updateKxdMeter(kxdLastMeterLevel); // refresh gate mark position
    kxdSaveSettings();
}

function handleKxdRecognized(p) {
    const el = document.getElementById('kxdSourceText');
    if (!el) return;
    el.textContent = p.text || '';
}

function handleKxdTranslated(p) {
    const el = document.getElementById('kxdOutputText');
    if (!el) return;
    el.textContent = p.text || '';
}

function kxdUpdateTranslateVisibility() {
    const toggle = document.getElementById('kxdTranslateToggle');
    const outputCard = document.getElementById('kxdOutputCard');
    if (!toggle || !outputCard) return;
    outputCard.style.display = toggle.checked ? '' : 'none';
}

function kxdSaveSettings() {
    const apiKey = (document.getElementById('kxdApiKey')?.value || '').trim();
    const googleApiKey = (document.getElementById('kxdGoogleApiKey')?.value || '').trim();
    const model = document.getElementById('kxdModel')?.value || 'groq';
    const srcSel = document.getElementById('kxdSourceLang');
    const tgtSel = document.getElementById('kxdTargetLang');
    const sourceLang = srcSel ? srcSel.value : 'auto';
    const targetLang = tgtSel ? tgtSel.value : 'en';
    const translateEnabled = !!(document.getElementById('kxdTranslateToggle')?.checked);
    const oscEnabled = !!(document.getElementById('kxdOscToggle')?.checked);
    const partialOsc = !!(document.getElementById('kxdPartialToggle')?.checked);
    const disableNonSpeech = !!(document.getElementById('kxdNonSpeechToggle')?.checked);
    const chatboxNotify = !!(document.getElementById('kxdNotifyToggle')?.checked);
    const profileTransToggle = document.getElementById('kxdProfileTransToggle');
    const profileTargetSel = document.getElementById('kxdProfileTargetLang');
    const profileTranslationEnabled = profileTransToggle ? !!profileTransToggle.checked : (window._kxdProfileTranslationEnabled !== false);
    const profileTargetLang = profileTargetSel?.value || window._kxdProfileTargetLang || 'en';
    window._kxdProfileTranslationEnabled = profileTranslationEnabled;
    window._kxdProfileTargetLang = profileTargetLang;
    window._kxdApiKeyPresent = !!apiKey;
    const personality = document.getElementById('kxdPersonality')?.value || 'raw';
    const ttsEnabled = !!(document.getElementById('kxdTtsToggle')?.checked);
    const ttsVoice = document.getElementById('kxdTtsVoice')?.value || '';
    const ttsEngine = document.getElementById('kxdTtsEngine')?.value || 'sapi';
    const ttsRate = parseInt(document.getElementById('kxdTtsRate')?.value ?? '0', 10);
    const payload = { action: 'kxdSaveSettings', apiKey, googleApiKey, model, sourceLang, targetLang, translateEnabled, oscEnabled, partialOsc, disableNonSpeech, chatboxNotify, noiseGatePct: kxdNoiseGatePct, profileTranslationEnabled, profileTargetLang, personality, blockWords: _kxdBlockWords, blockSentences: _kxdBlockSentences, ttsEnabled, ttsVoice, ttsEngine, ttsRate: isNaN(ttsRate) ? 0 : ttsRate };
    const dev = audioDeviceValue('kxdDeviceSelect');
    if (dev) { payload.deviceId = dev.id; payload.deviceName = dev.name; }
    const ttsDev = audioDeviceValue('kxdTtsDevice');
    if (ttsDev) { payload.ttsDeviceId = ttsDev.id; payload.ttsDeviceName = ttsDev.name; }
    sendToCS(payload);
}


let _kxdLocalState = null;

function kxdApplyModelVisibility() {
    const isLocal = (document.getElementById('kxdModel')?.value || 'groq') === 'local';
    const sec = document.getElementById('kxdLocalSection');
    if (sec) sec.style.display = isLocal ? '' : 'none';
    const sileroRow = document.getElementById('kxdSileroRow');
    if (sileroRow) sileroRow.style.display = isLocal ? '' : 'none';
    if (isLocal && !_kxdLocalState) sendToCS({ action: 'kxdLocalGetState' });
}

function kxdFmtBytes(n) {
    if (!n || n < 0) return '';
    if (n < 1024 * 1024) return (n / 1024).toFixed(0) + ' KB';
    if (n < 1024 * 1024 * 1024) return (n / (1024 * 1024)).toFixed(0) + ' MB';
    return (n / (1024 * 1024 * 1024)).toFixed(1) + ' GB';
}

function kxdLocalRow(item, selectable, selectedId, group) {
    const active = selectable && item.installed && item.id === selectedId;
    const clickable = selectable && item.installed && !item.busy;

    const btn = item.busy
        ? `<button class="vrcn-button" onclick="event.stopPropagation();kxdLocalCancel('${esc(item.id)}')" style="border-color:var(--warn);padding:2px 8px;font-size:calc(11px + var(--fs-off, 0px));"><span class="msi" style="font-size:13px;">close</span></button>`
        : item.installed
            ? `<button class="vrcn-button" onclick="event.stopPropagation();kxdLocalUninstall('${esc(item.id)}')" style="border-color:var(--err);padding:2px 8px;font-size:calc(11px + var(--fs-off, 0px));"><span class="msi" style="font-size:13px;">delete</span></button>`
            : `<button class="vrcn-button" onclick="event.stopPropagation();kxdLocalDownload('${esc(item.id)}')" style="padding:2px 8px;font-size:calc(11px + var(--fs-off, 0px));"><span class="msi" style="font-size:13px;">download</span></button>`;

    const size = item.installed ? kxdFmtBytes(item.sizeOnDisk) : kxdFmtBytes(item.approxBytes);

    let warn = '';
    if ((item.id === 'rt-whisper-vulkan' || item.id === 'rt-llama-vulkan')
        && _kxdLocalState && _kxdLocalState.vulkanAvailable === false) {
        warn = `<span style="color:var(--warn);">${esc(t('kikitan.local.vulkan_missing', 'No Vulkan driver found'))}</span>`;
    }

    const border = active ? 'var(--accent)' : 'var(--brd)';
    const nameColor = active ? 'var(--accent)' : 'var(--tx1)';

    return `
    <div class="kxd-local-row" ${clickable ? `onclick="kxdPickModel('${esc(group)}','${esc(item.id)}')"` : ''}
         style="display:flex;align-items:center;gap:8px;padding:5px 9px;margin-bottom:4px;border:1px solid ${border};border-radius:7px;${clickable ? 'cursor:pointer;' : ''}${item.installed ? '' : 'opacity:.72;'}">
        <div style="flex:1;min-width:0;">
            <div style="font-size:calc(12px + var(--fs-off, 0px));color:${nameColor};white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">
                ${esc(item.name)} <span style="color:var(--tx1);">${esc(size)}</span>
            </div>
            <div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx2);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;" title="${esc(item.detail)}">${warn || esc(item.detail)}</div>
        </div>
        ${btn}
        <div class="inv-progress-wrap" id="kxdProgWrap-${esc(item.id)}" style="display:${item.busy ? '' : 'none'};padding:0;width:78px;flex-shrink:0;">
            <div class="inv-progress-track"><div class="inv-progress-bar" id="kxdProgBar-${esc(item.id)}"></div></div>
            <div class="inv-progress-text" id="kxdProgText-${esc(item.id)}" style="margin-top:2px;font-size:calc(9px + var(--fs-off, 0px));"></div>
        </div>
    </div>`;
}

function kxdPickModel(group, id) {
    if (!_kxdDevicesPayload) _kxdDevicesPayload = {};
    if (group === 'stt') _kxdDevicesPayload.localSttModel = id;
    if (group === 'llm') _kxdDevicesPayload.localLlmModel = id;
    if (_kxdLocalState) kxdRenderLocalState(_kxdLocalState);
    kxdSaveLocalSelection();
}

function kxdRenderLocalState(state) {
    if (!state) return;
    _kxdLocalState = state;

    const p = _kxdDevicesPayload || {};
    const sttSel = p.localSttModel || 'stt-small';
    const llmSel = p.localLlmModel || 'llm-qwen25-3b';

    const by = k => (state.items || []).filter(i => i.kind === k);

    const sttEl = document.getElementById('kxdLocalStt');
    if (sttEl) sttEl.innerHTML = by('stt').map(i => kxdLocalRow(i, true, sttSel, 'stt')).join('');

    const vadEl = document.getElementById('kxdLocalVad');
    if (vadEl) vadEl.innerHTML = by('vad').map(i => kxdLocalRow(i, false, '', '')).join('');

    const llmEl = document.getElementById('kxdLocalLlm');
    if (llmEl) llmEl.innerHTML = by('llm').map(i => kxdLocalRow(i, true, llmSel, 'llm')).join('');

    const rtEl = document.getElementById('kxdLocalRuntime');
    if (rtEl) rtEl.innerHTML = by('runtime').map(i => kxdLocalRow(i, false, '', '')).join('');

    const rootEl = document.getElementById('kxdLocalRoot');
    if (rootEl) rootEl.textContent = state.root || '';

    const gpuEl = document.getElementById('kxdLocalGpu');
    if (gpuEl && p.localUseGpu !== undefined) gpuEl.checked = !!p.localUseGpu;

    if (typeof applyTranslations === 'function') applyTranslations(document);
}

function kxdOnLocalProgress(p) {
    if (!p || !p.id) return;
    const wrap = document.getElementById('kxdProgWrap-' + p.id);
    const bar = document.getElementById('kxdProgBar-' + p.id);
    const txt = document.getElementById('kxdProgText-' + p.id);
    if (wrap) wrap.style.display = '';
    if (bar) bar.style.width = (p.pct || 0) + '%';
    if (txt) txt.textContent = `${p.pct || 0}%`;
}

function kxdOnLocalFinished(p) {
    if (!p || !p.id) return;
    const wrap = document.getElementById('kxdProgWrap-' + p.id);
    if (wrap) wrap.style.display = 'none';
    if (!p.ok && p.error && p.error !== 'cancelled') {
        if (typeof showToast === 'function') showToast(false, p.error);
    }
}

function kxdLocalDownload(id) { sendToCS({ action: 'kxdLocalDownload', id }); }
function kxdLocalCancel(id)   { sendToCS({ action: 'kxdLocalCancel', id }); }

function kxdLocalUninstall(id) {
    const item = (_kxdLocalState?.items || []).find(i => i.id === id);
    const name = item ? item.name : id;
    vnConfirmModal({
        title: t('kikitan.local.uninstall_title', 'Remove Model'),
        icon: 'delete',
        message: tf('kikitan.local.uninstall_confirm', { name: esc(name) }, 'Remove {name} from your disk?'),
        confirmLabel: t('kikitan.local.uninstall', 'Uninstall'),
        onConfirm: () => sendToCS({ action: 'kxdLocalUninstall', id }),
    });
}

function kxdSaveLocalSelection() {
    const p = _kxdDevicesPayload || (_kxdDevicesPayload = {});
    const useGpu = !!document.getElementById('kxdLocalGpu')?.checked;
    p.localUseGpu = useGpu;
    const vadEnabled = document.getElementById('kxdSileroToggle')?.checked !== false;
    p.localVadEnabled = vadEnabled;
    sendToCS({
        action: 'kxdLocalSaveSelection',
        sttModel: p.localSttModel,
        llmModel: p.localLlmModel,
        vadEnabled,
        useGpu,
    });
}

function kxdRenderBlockChips(kind) {
    const isWord = kind === 'word';
    const list = isWord ? _kxdBlockWords : _kxdBlockSentences;
    const containerId = isWord ? 'kxdBlockWordChips' : 'kxdBlockSentenceChips';
    const inputId = isWord ? 'kxdBlockWordInput' : 'kxdBlockSentenceInput';
    const el = document.getElementById(containerId);
    if (!el) return;
    const chips = list.map(w =>
        `<span class="vf-block-chip">${esc(w)}<button class="vf-block-chip-remove" onclick='kxdBlockRemove(${JSON.stringify(kind)}, ${JSON.stringify(w)})' title="${esc(t('common.remove', 'Remove'))}"><span class="msi" style="font-size:11px;">close</span></button></span>`
    ).join('');
    const placeholder = list.length === 0
        ? (isWord
            ? t('kikitan.blocked.word_placeholder', 'Type a word and press Enter...')
            : t('kikitan.blocked.sentence_placeholder', 'Type a sentence and press Enter...'))
        : '';
    el.innerHTML = chips + `<input id="${inputId}" class="vf-block-inline-input" placeholder="${esc(placeholder)}" onkeydown="kxdBlockInputKey(event, '${kind}')">`;
}

function kxdBlockInputKey(e, kind) {
    const isWord = kind === 'word';
    const list = isWord ? _kxdBlockWords : _kxdBlockSentences;
    const inputId = isWord ? 'kxdBlockWordInput' : 'kxdBlockSentenceInput';
    if (e.key === 'Enter') {
        e.preventDefault();
        kxdBlockAdd(kind);
    } else if (e.key === 'Backspace' && e.target.value === '' && list.length > 0) {
        e.preventDefault();
        list.pop();
        kxdRenderBlockChips(kind);
        document.getElementById(inputId)?.focus();
        kxdSaveSettings();
    }
}

function kxdBlockAdd(kind) {
    const isWord = kind === 'word';
    const list = isWord ? _kxdBlockWords : _kxdBlockSentences;
    const inputId = isWord ? 'kxdBlockWordInput' : 'kxdBlockSentenceInput';
    const inp = document.getElementById(inputId);
    if (!inp) return;
    const val = inp.value.trim();
    inp.value = '';
    if (!val || list.some(w => w.toLowerCase() === val.toLowerCase())) return;
    list.push(val);
    kxdRenderBlockChips(kind);
    document.getElementById(inputId)?.focus();
    kxdSaveSettings();
}

function kxdBlockRemove(kind, val) {
    if (kind === 'word') _kxdBlockWords = _kxdBlockWords.filter(w => w !== val);
    else _kxdBlockSentences = _kxdBlockSentences.filter(w => w !== val);
    kxdRenderBlockChips(kind);
    document.getElementById(kind === 'word' ? 'kxdBlockWordInput' : 'kxdBlockSentenceInput')?.focus();
    kxdSaveSettings();
}

function buildKxdLangOptions(langs, selectedCode) {
    return langs.map(l =>
        `<option value="${esc(l.code)}"${l.code === selectedCode ? ' selected' : ''}>${esc(l.name)}</option>`
    ).join('');
}

function kxdInitLangSelects() {
    const srcSel = document.getElementById('kxdSourceLang');
    if (srcSel && !srcSel.dataset.built) {
        srcSel.innerHTML = buildKxdLangOptions(KXD_SOURCE_LANGS, 'auto');
        srcSel.dataset.built = '1';
        if (srcSel._vnRefresh) srcSel._vnRefresh();
    }
    const tgtSel = document.getElementById('kxdTargetLang');
    if (tgtSel && !tgtSel.dataset.built) {
        tgtSel.innerHTML = buildKxdLangOptions(KXD_TARGET_LANGS, 'en');
        tgtSel.dataset.built = '1';
        if (tgtSel._vnRefresh) tgtSel._vnRefresh();
    }
}
