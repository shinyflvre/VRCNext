function toggleAttrib(header) {
    header.closest('.attrib-item').classList.toggle('open');
}

function renderFolders(f) {
    const e = document.getElementById('folderList');
    if (!f || !f.length) {
        e.innerHTML = `<div class="folder-empty">${t('settings.watch_folders.empty', 'No folders added')}</div>`;
        return;
    }
    e.innerHTML = f.map((x, i) =>
        `<div class="folder-item" onclick="selectedFolderIdx=${i}" style="${selectedFolderIdx === i ? 'background:var(--bg-hover)' : ''}"><span>${esc(x)}</span><button class="folder-remove" onclick="event.stopPropagation();removeFolderAt(${i})" title="${esc(t('common.remove', 'Remove'))}"><span class="msi" style="font-size:16px;">close</span></button></div>`
    ).join('');
}

function addFolder() {
    sendToCS({ action: 'addFolder' });
}

function removeFolder() {
    if (selectedFolderIdx >= 0 && settings.folders?.[selectedFolderIdx]) {
        settings.folders.splice(selectedFolderIdx, 1);
        selectedFolderIdx = -1;
        renderFolders(settings.folders);
        autoSave();
    }
}

function removeFolderAt(i) {
    if (settings.folders) {
        settings.folders.splice(i, 1);
        selectedFolderIdx = -1;
        renderFolders(settings.folders);
        autoSave();
    }
}

function _renderExeList(listId, list, removeFn) {
    const e = document.getElementById(listId);
    if (!e) return;
    if (!list || !list.length) {
        e.innerHTML = `<div class="folder-empty">${t('common.none', 'None')}</div>`;
        return;
    }
    e.innerHTML = list.map((x, i) =>
        `<div class="exe-item"><span>${esc(x.split(/[\\\\/]/).pop())}</span><button class="exe-remove" onclick="${removeFn}(${i})" title="${esc(t('common.remove', 'Remove'))}"><span class="msi" style="font-size:16px;">close</span></button></div>`
    ).join('');
}

function renderExtraExeDesktop(l) { _renderExeList('extraExeDesktopList', l, 'removeExtraExeDesktop'); }
function renderExtraExeVR(l)      { _renderExeList('extraExeVRList',      l, 'removeExtraExeVR');      }

// legacy — kept so i18n.js re-render calls don't break on old references

function browseExe(t) {
    sendToCS({ action: 'browseExe', target: t });
}

function removeExtraExeDesktop(i) {
    if (settings.extraExeDesktop) {
        settings.extraExeDesktop.splice(i, 1);
        renderExtraExeDesktop(settings.extraExeDesktop);
        autoSave();
    }
}

function removeExtraExeVR(i) {
    if (settings.extraExeVR) {
        settings.extraExeVR.splice(i, 1);
        renderExtraExeVR(settings.extraExeVR);
        autoSave();
    }
}

function saveSettings() {
    const w = [];
    for (let i = 0; i < 4; i++) {
        const nameEl = document.getElementById('whName' + i);
        const urlEl = document.getElementById('whUrl' + i);
        const onEl = document.getElementById('whOn' + i);
        w.push({
            Name: nameEl?.value || '',
            Url: urlEl?.value || '',
            Enabled: onEl?.checked || false
        });
    }
    const _vriReady = typeof vriLoaded !== 'undefined' && vriLoaded;
    const _sfCur = _vriReady && typeof sfReadBtns === 'function' ? sfReadBtns() : {};
    const _stCur = _vriReady && typeof stReadBtns === 'function' ? stReadBtns() : {};
    const _fsCur = _vriReady && typeof fsReadBtns === 'function' ? fsReadBtns() : {};

    const payload = {
        action: 'saveSettings',
        data: {
            botName: document.getElementById('setBotName').value,
            botAvatar: document.getElementById('setBotAvatar').value,
            webhooks: w,
            folders: settings.folders || [],
            relayEnabledFolders: settings.relayEnabledFolders,
            vrcPath: document.getElementById('setVrcPath').value,
            extraExeDesktop: settings.extraExeDesktop || [],
            extraExeVR: settings.extraExeVR || [],
            closeWithVrc:       document.getElementById('setCloseWithVrc')?.checked       ?? false,
            startAlwaysWithVrc: document.getElementById('setStartAlwaysWithVrc')?.checked ?? true,
            extraExe: [], // clear legacy field so migration doesn't re-fire after user deletes apps
            autoStart: false, // legacy kept for JSON compat
            relayAutoStartVR:        document.getElementById('setAutoStartVR')?.checked       ?? false,
            relayAutoStartDesktop:   document.getElementById('setAutoStartDesktop')?.checked  ?? false,
            startWithWindows: document.getElementById('setStartWithWindows').checked,
            minimizeToTray: document.getElementById('setMinimizeToTray').checked,
            trayNotificationsEnabled: document.getElementById('setTrayNotifications')?.checked ?? false,
            notifySound: false, // legacy kept for JSON compat
            notifySoundEnabled: document.getElementById('setNotifySoundEnabled').checked,
            messageSoundEnabled: document.getElementById('setMessageSoundEnabled').checked,
            mediaRelaySoundEnabled: document.getElementById('setMediaRelaySoundEnabled').checked,
            steamOverlaySoundEnabled: document.getElementById('setSteamOverlaySoundEnabled')?.checked ?? true,
            notifySoundFile: document.getElementById('setNotifySoundFile')?.value ?? '',
            messageSoundFile: document.getElementById('setMessageSoundFile')?.value ?? '',
            mediaRelaySoundFile: document.getElementById('setMediaRelaySoundFile')?.value ?? '',
            steamOverlaySoundFile: document.getElementById('setSteamOverlaySoundFile')?.value ?? '',
            notifySoundVolume: Number(document.getElementById('setNotifySoundVolume')?.value ?? 50),
            messageSoundVolume: Number(document.getElementById('setMessageSoundVolume')?.value ?? 50),
            mediaRelaySoundVolume: Number(document.getElementById('setMediaRelaySoundVolume')?.value ?? 50),
            steamOverlaySoundVolume: Number(document.getElementById('setSteamOverlaySoundVolume')?.value ?? 50),
            friendOnlineToastEnabled: document.getElementById('setFriendOnlineToastEnabled')?.checked ?? false,
            friendOnlineToastFavOnly: document.getElementById('setFriendOnlineToastFavOnly')?.checked ?? false,
            friendsSidebarLocationOnly: document.getElementById('setFriendsSidebarLocationOnly')?.checked ?? true,
            friendsSidebarPreviewCollapsed: document.getElementById('setFriendsSidebarPreviewCollapsed')?.checked ?? true,
            friendsSidebarPreviewOpen: document.getElementById('setFriendsSidebarPreviewOpen')?.checked ?? false,
            separateFavoriteFriends: document.getElementById('setSeparateFavoriteFriends')?.checked ?? false,
            peopleAlwaysStats: document.getElementById('setPeopleAlwaysStats')?.checked ?? false,
            commentsOnWorldsEnabled: document.getElementById('setCommentsOnWorlds')?.checked ?? true,
            modernFolderLayout: document.getElementById('setModernFolderLayout')?.checked ?? true,
            navSidebarHoverText: document.getElementById('setNavSidebarHoverText')?.checked ?? true,
            enableVrcPlusDecorations: document.getElementById('setVrcPlusDecorations')?.checked ?? false,
            enableProfileIconFrames: document.getElementById('setEnableIconFrames')?.checked ?? false,
            squareIconFrames: document.getElementById('setSquareIconFrames')?.checked ?? false,
            enableNameplateDecoration: document.getElementById('setEnableNameplateDeco')?.checked ?? false,
            enableProfileEffects: document.getElementById('setEnableProfileEffect')?.checked ?? false,
            enableProfileBackgrounds: document.getElementById('setEnableProfileBg')?.checked ?? false,
            enableProfileThemes: document.getElementById('setEnableProfileThemes')?.checked ?? false,
            profileThemeContrast: document.getElementById('setProfileThemeContrast')?.checked ?? true,
            transparentProfileCards: document.getElementById('setTransparentProfileCards')?.checked ?? false,
            showDecorationsOnDashboard: document.getElementById('setDecoOnDashboard')?.checked ?? false,
            enableProfileIconFramesOthers: document.getElementById('setEnableIconFramesOthers')?.checked ?? false,
            squareIconFramesOthers: document.getElementById('setSquareIconFramesOthers')?.checked ?? false,
            enableNameplateDecorationOthers: document.getElementById('setEnableNameplateDecoOthers')?.checked ?? false,
            enableProfileEffectsOthers: document.getElementById('setEnableProfileEffectOthers')?.checked ?? false,
            enableProfileBackgroundsOthers: document.getElementById('setEnableProfileBgOthers')?.checked ?? false,
            enableProfileThemesOthers: document.getElementById('setEnableProfileThemesOthers')?.checked ?? false,
            profileThemeContrastOthers: document.getElementById('setProfileThemeContrastOthers')?.checked ?? true,
            transparentProfileCardsOthers: document.getElementById('setTransparentProfileCardsOthers')?.checked ?? false,
            showDecorationsOnDashboardOthers: document.getElementById('setDecoOnDashboardOthers')?.checked ?? false,
            language: currentLanguage,
            theme: currentTheme,
            specialTheme: currentSpecialTheme,
            autoColorAccuracy: autoColorAccuracy,
            cursorTheme: currentCursorTheme,
            appFont: currentAppFont,
            customFont: currentCustomFont,
            fontSizeOffset: currentFontSizeOffset,
            taskbarHeight: currentTaskbarHeight,
            activeCustomThemes: [..._activeCustomThemes],
            guiZoom: Math.round(_guiZoom * 100),
            dashBgPath: dashBgPath,
            randomDashBg: document.getElementById('setRandomBg').checked,
            clockEnabled: document.getElementById('setClockEnabled').checked,
            dateEnabled: document.getElementById('setDateEnabled').checked,
            showVrcPlus: document.getElementById('setShowVrcPlus').checked,
            showVrcCredits: document.getElementById('setShowVrcCredits').checked,
            showApiHealth: document.getElementById('setShowApiHealth').checked,
            // Credentials are no longer transported via saveSettings, they live in the Accounts tab.
            sfMultiplier: parseFloat(document.getElementById('sfMultiplier').value) || 1,
            sfLockX: document.getElementById('sfLockX').checked,
            sfLockY: document.getElementById('sfLockY').checked,
            sfLockZ: document.getElementById('sfLockZ').checked,
            sfLeftHand: false,  // legacy
            sfRightHand: true,  // legacy
            sfUseGrip: true,    // legacy
            sfLeftResetBtn:       _sfCur.sfLeftReset,
            sfRightResetBtn:      _sfCur.sfRightReset,
            sfLeftDragBtn:        _sfCur.sfLeftDrag,
            sfRightDragBtn:       _sfCur.sfRightDrag,
            sfLeftGravityBtn:     _sfCur.sfLeftGravity,
            sfRightGravityBtn:    _sfCur.sfRightGravity,
            ...(_vriReady ? { vrInputMode } : {}),
            sfGravity: parseFloat(document.getElementById('sfGravity')?.value) || 9.8,
            stMultiplier:  parseFloat(document.getElementById('stMultiplier')?.value) || 1,
            stSnapDegrees: parseFloat(document.getElementById('stSnapDegrees')?.value) || 0,
            stInvert:      !!document.getElementById('stInvert')?.checked,
            stSmoothing:   parseFloat(document.getElementById('stSmoothing')?.value) || 0,
            stLeftTurnBtn:   _stCur.stLeftTurn,
            stRightTurnBtn:  _stCur.stRightTurn,
            stLeftResetBtn:  _stCur.stLeftReset,
            stRightResetBtn: _stCur.stRightReset,
            stAutoStartVR:   document.getElementById('setStAutoStartVR')?.checked ?? false,
            chatboxAutoStart: false, // legacy
            chatboxAutoStartVR:       document.getElementById('setCbAutoStartVR')?.checked        ?? false,
            chatboxAutoStartDesktop:  document.getElementById('setCbAutoStartDesktop')?.checked   ?? false,
            sfAutoStart: false, // legacy
            sfAutoStartVR:            document.getElementById('setSfAutoStartVR')?.checked        ?? false,
            fsAutoStartVR:            document.getElementById('setFsAutoStartVR')?.checked        ?? false,
            fsLeftButton:             _fsCur.fsLeftButton,
            fsRightButton:            _fsCur.fsRightButton,
            fsActivationRadius:       parseInt(document.getElementById('fsActivationRadius')?.value ?? '15', 10),
            fsLeftRecordButton:       _fsCur.fsLeftRecord,
            fsRightRecordButton:      _fsCur.fsRightRecord,
            fsGifMaxResolution:       parseInt(document.getElementById('fsGifMaxResolution')?.value ?? '512', 10),
            fsGifMaxFps:              parseInt(document.getElementById('fsGifMaxFps')?.value        ?? '10', 10),
            fsUseHmdRotations:        !!document.getElementById('fsUseHmdRotations')?.checked,
            fsLeftVideoButton:        _fsCur.fsLeftVideo,
            fsRightVideoButton:       _fsCur.fsRightVideo,
            fsLeftAcceptButton:       _fsCur.fsLeftAccept,
            fsRightAcceptButton:      _fsCur.fsRightAccept,
            fsVideoDeviceA:           (typeof fsCurrentVideoDeviceA === 'function') ? fsCurrentVideoDeviceA() : (document.getElementById('fsVideoDeviceA')?.value ?? ''),
            fsVideoDeviceB:           (typeof fsCurrentVideoDeviceB === 'function') ? fsCurrentVideoDeviceB() : (document.getElementById('fsVideoDeviceB')?.value ?? ''),
            fsVideoFps:               parseInt(document.getElementById('fsVideoFps')?.value           ?? '30', 10),
            fsVideoQuality:           document.getElementById('fsVideoQuality')?.value                ?? '1080p',
            fsVideoBitrateQuality:    document.getElementById('fsVideoBitrateQuality')?.value         ?? 'medium',
            fsAudioKbps:              parseInt(document.getElementById('fsAudioKbps')?.value          ?? '256', 10),
            ytAutoStartVR:            document.getElementById('setYtAutoStartVR')?.checked        ?? false,
            ytAutoStartDesktop:       document.getElementById('setYtAutoStartDesktop')?.checked   ?? false,
            vfAutoStartVR:            document.getElementById('setVfAutoStartVR')?.checked        ?? false,
            vfAutoStartDesktop:       document.getElementById('setVfAutoStartDesktop')?.checked   ?? false,
            discordPresenceAutoStart: false, // legacy
            dpAutoStartVR:            document.getElementById('setDpAutoStartVR')?.checked        ?? false,
            dpAutoStartDesktop:       document.getElementById('setDpAutoStartDesktop')?.checked   ?? false,
            vroAutoStart: false, // legacy
            vroAutoStartVR:           document.getElementById('setVroAutoStartVR')?.checked       ?? false,
            // Avatar Scaling
            asAutoStartVR:      !!document.getElementById('setAsAutoStartVR')?.checked,
            asAutoStartDesktop: !!document.getElementById('setAsAutoStartDesktop')?.checked,
            asUseSafety:        !!document.getElementById('asUseSafety')?.checked,
            asScale:            parseFloat(document.getElementById('asScaleSlider')?.value) || 1.0,
            asScaleMin:         parseFloat(document.getElementById('asScaleMin')?.value) || 0.5,
            asScaleMax:         parseFloat(document.getElementById('asScaleMax')?.value) || 3.0,
            asSaveScale:        !!document.getElementById('asSaveScale')?.checked,
            asKeyUp:            typeof _asKeyUpCode !== 'undefined' ? _asKeyUpCode : 0,
            asKeyDown:          typeof _asKeyDownCode !== 'undefined' ? _asKeyDownCode : 0,
            asSmoothing:        parseInt(document.getElementById('asSmoothSlider')?.value) || 30,
            vroAttachLeft:   document.getElementById('vroAttachLeft')?.value === 'left',
            vroAttachHand:   document.getElementById('vroAttachPart')?.value === 'hand',
            vroPosX:  parseFloat(document.getElementById('vroPosX')?.value) || 0,
            vroPosY:  parseFloat(document.getElementById('vroPosY')?.value) || 0.07,
            vroPosZ:  parseFloat(document.getElementById('vroPosZ')?.value) || -0.05,
            vroRotX:  parseFloat(document.getElementById('vroRotX')?.value) || -80,
            vroRotY:  parseFloat(document.getElementById('vroRotY')?.value) || 0,
            vroRotZ:  parseFloat(document.getElementById('vroRotZ')?.value) || 0,
            vroWidth: parseFloat(document.getElementById('vroWidth')?.value) || 0.22,
            vroKeybind:        vroComboIds    ?? [],
            vroKeybindHand:    vroComboHand   ?? 0,
            vroKeybindDt:      vroDtIds       ?? [],
            vroKeybindDtHand:  vroDtHand      ?? 0,
            vroKeybindMode:    vroKeybindMode ?? 0,
            vroControlRadius:  parseInt(document.getElementById('vroControlRadius')?.value) || 28,
            vroDynVis:         !!document.getElementById('vroDynVis')?.checked,
            vroFocusRadius:    parseInt(document.getElementById('vroFocusRadius')?.value) || 35,
            vroSeamless:       !!document.getElementById('vroSeamless')?.checked,
            vroToastEnabled:    !!document.getElementById('vroToastEnabled')?.checked,
            vroToastFavOnly:    !!document.getElementById('vroToastFavOnly')?.checked,
            vroToastSize:       parseInt(document.getElementById('vroToastSize')?.value) || 50,
            vroToastOffsetX:    parseFloat(document.getElementById('vroToastOffsetX')?.value) || 0,
            vroToastOffsetY:    parseFloat(document.getElementById('vroToastOffsetY')?.value) || -0.12,
            vroToastOnline:     !!document.getElementById('vroToastOnline')?.checked,
            vroToastTtsOnline:     !!document.getElementById('vroToastOnlineTts')?.checked,
            vroToastTtsOffline:     !!document.getElementById('vroToastOfflineTts')?.checked,
            vroToastTtsGps:     !!document.getElementById('vroToastGpsTts')?.checked,
            vroToastTtsStatus:     !!document.getElementById('vroToastStatusTts')?.checked,
            vroToastTtsStatusDesc:     !!document.getElementById('vroToastStatusDescTts')?.checked,
            vroToastTtsBio:     !!document.getElementById('vroToastBioTts')?.checked,
            vroToastTtsFriendReq:     !!document.getElementById('vroToastFriendReqTts')?.checked,
            vroToastTtsInvite:     !!document.getElementById('vroToastInviteTts')?.checked,
            vroToastTtsGroupInv:     !!document.getElementById('vroToastGroupInvTts')?.checked,
            vroToastTtsJoined:       !!document.getElementById('vroToastJoinedTts')?.checked,
            vroToastJoined:          !!document.getElementById('vroToastJoined')?.checked,
            vroToastTtsLeft:         !!document.getElementById('vroToastLeftTts')?.checked,
            vroToastLeft:            !!document.getElementById('vroToastLeft')?.checked,
            vroToastTtsReqInvite:    !!document.getElementById('vroToastReqInviteTts')?.checked,
            vroToastReqInvite:       !!document.getElementById('vroToastReqInvite')?.checked,
            vroTtsVoice:        document.getElementById('vroTtsVoice')?.value || '',
            vroTtsEngine:       document.getElementById('vroTtsEngine')?.value || 'sapi',
            vroTtsLang:         document.getElementById('vroTtsLang')?.value || '',
            vroTtsGender:       document.getElementById('vroTtsGender')?.value || '',
            vroToastOffline:    !!document.getElementById('vroToastOffline')?.checked,
            vroToastGps:        !!document.getElementById('vroToastGps')?.checked,
            vroToastStatus:     !!document.getElementById('vroToastStatus')?.checked,
            vroToastStatusDesc: !!document.getElementById('vroToastStatusDesc')?.checked,
            vroToastBio:        !!document.getElementById('vroToastBio')?.checked,
            vroToastDuration:   parseInt(document.getElementById('vroToastDuration')?.value) || 8,
            vroToastStack:      parseInt(document.getElementById('vroToastStack')?.value) || 2,
            vroToastFriendReq:  !!document.getElementById('vroToastFriendReq')?.checked,
            vroToastInvite:     !!document.getElementById('vroToastInvite')?.checked,
            vroToastGroupInv:   !!document.getElementById('vroToastGroupInv')?.checked,
            vroWaterEnabled:    !!document.getElementById('vroWaterEnabled')?.checked,
            vroWaterHours:      parseInt(document.getElementById('vroWaterHours')?.value   ?? '1', 10),
            vroWaterMinutes:    parseInt(document.getElementById('vroWaterMinutes')?.value ?? '0', 10),
            vroScaleScrollSensitivity: parseInt(document.getElementById('vroScaleSensitivity')?.value) || 25,
            dpHideJoinBtnJoinMe: document.getElementById('dpHideJoinBtn_joinme')?.checked ?? false,
            dpHideJoinBtnOnline: document.getElementById('dpHideJoinBtn_online')?.checked ?? false,
            dpHideJoinBtnAskMe:  document.getElementById('dpHideJoinBtn_askme')?.checked  ?? false,
            dpHideJoinBtnBusy:   document.getElementById('dpHideJoinBtn_busy')?.checked   ?? false,
            dpHideInstIdJoinMe:  document.getElementById('dpHideInstId_joinme')?.checked ?? false,
            dpHideInstIdOnline:  document.getElementById('dpHideInstId_online')?.checked ?? false,
            dpHideInstIdAskMe:   document.getElementById('dpHideInstId_askme')?.checked  ?? false,
            dpHideInstIdBusy:    document.getElementById('dpHideInstId_busy')?.checked   ?? false,
            dpHideLocJoinMe:     document.getElementById('dpHideLoc_joinme')?.checked    ?? false,
            dpHideLocOnline:     document.getElementById('dpHideLoc_online')?.checked    ?? false,
            dpHideLocAskMe:      document.getElementById('dpHideLoc_askme')?.checked     ?? false,
            dpHideLocBusy:       document.getElementById('dpHideLoc_busy')?.checked      ?? false,
            dpHidePlayersJoinMe: document.getElementById('dpHidePlayers_joinme')?.checked ?? false,
            dpHidePlayersOnline: document.getElementById('dpHidePlayers_online')?.checked ?? false,
            dpHidePlayersAskMe:  document.getElementById('dpHidePlayers_askme')?.checked  ?? false,
            dpHidePlayersBusy:   document.getElementById('dpHidePlayers_busy')?.checked   ?? false,
            searchDebounceMs:        parseInt(document.getElementById('setSearchDebounceMs')?.value) || 500,
            imgCacheLimitGb:         parseInt(document.getElementById('setImgCacheLimit').value) || 5,
            imgCacheOptimizeEnabled: document.getElementById('setImgCacheOptimizeEnabled').checked,
            imgMemoryOptimizeEnabled: document.getElementById('setImgMemoryOptimizeEnabled')?.checked ?? true,
            vrcPlusOptimizeEnabled: document.getElementById('setVrcPlusOptimize')?.checked ?? true,
            ffcEnabled: document.getElementById('setFfcEnabled').checked,
            memoryTrimEnabled: document.getElementById('setMemoryTrimEnabled').checked,
            mediaFixEnabled: document.getElementById('setMediaFixEnabled')?.checked ?? true,
            multiTaskMode: document.getElementById('setMultiTaskMode')?.checked ?? false,
            tilingManager: document.getElementById('setTilingManager')?.checked ?? true,
            dbOptimize: document.getElementById('setDbOptimize').checked,
            dbOptimizeMaxEntries: Math.max(500, Math.min(250000, parseInt(document.getElementById('setDbOptimizeMaxEntries').value) || 500)),
            autoUpdate: document.getElementById('setAutoUpdate').checked,
            sendCrashData: document.getElementById('setSendCrashData').checked,
            restartAfterCrash: document.getElementById('setRestartAfterCrash').checked,
            rememberWindowSize:     document.getElementById('setRememberWindowSize')?.checked     ?? false,
            rememberWindowPosition: document.getElementById('setRememberWindowPosition')?.checked ?? false,
            regBackupEnabled:    document.getElementById('setRegBackupEnabled')?.checked    ?? true,
            regBackupDays:       parseInt(document.getElementById('setRegBackupCycle')?.value    ?? '30'),
            dbAutoBackupEnabled: document.getElementById('setDbAutoBackupEnabled')?.checked  ?? true,
            dbAutoBackupDays:    parseInt(document.getElementById('setDbAutoBackupCycle')?.value ?? '60'),
            textToolsEnabled: document.getElementById('setTextToolsEnabled')?.checked ?? false,
            gpuAcceleration:    document.getElementById('setPerfGpuAccel')?.checked    ?? false,
            linuxGpuAcceleration: document.getElementById('setLinuxGpuAccel')?.checked ?? false,
            gpuShaderCache:     document.getElementById('setPerfShaderCache')?.checked  ?? false,
            v8Heap128:          document.getElementById('setPerfV8Heap')?.checked       ?? false,
            twoRenderProcesses: document.getElementById('setPerfRenderProc')?.checked   ?? false,
            animationsEnabled:  document.getElementById('setPerfAnimations')?.checked   ?? true,
            blurEnabled:        document.getElementById('setPerfBlur')?.checked         ?? true,
            efficiencyMode:     document.getElementById('setPerfEfficiency')?.checked   ?? false,
            avtrdbReportDeleted: document.getElementById('setAvtrdbReport').checked,
            avtrdbSubmitAvatars: document.getElementById('setAvtrdbSubmit').checked,
            avtrIcuReportDeleted: document.getElementById('setAvtrIcuReport').checked,
            avtrIcuSubmitAvatars: document.getElementById('setAvtrIcuSubmit').checked,
            vrcndbSubmitAvatars: document.getElementById('setVrcndbSubmit').checked,
            vrcndbReportDeleted: document.getElementById('setVrcndbReport').checked,
            vrcndbSyncLikes: document.getElementById('setVrcndbSyncLikes').checked,
            vrcndbSyncWears: document.getElementById('setVrcndbSyncWears').checked,
            dashSectionOrder:  (typeof _dashLayout !== 'undefined') ? _dashLayout.order  : [],
            dashSectionHidden: (typeof _dashLayout !== 'undefined') ? _dashLayout.hidden : [],
            dashRows: (typeof _dashLayout !== 'undefined' && Array.isArray(_dashLayout.rows))
                ? _dashLayout.rows.map(r => r.map(id => id || '').join('|'))
                : [],
            dashHero: (typeof _dashLayout !== 'undefined' && _dashLayout.hero)
                ? [_dashLayout.hero.left || '', _dashLayout.hero.right || '']
                : []
        }
    };
    // Sync in-memory flags immediately so sound functions see the updated value without waiting for round-trip
    settings.notifySoundEnabled      = payload.data.notifySoundEnabled;
    settings.messageSoundEnabled     = payload.data.messageSoundEnabled;
    settings.mediaRelaySoundEnabled  = payload.data.mediaRelaySoundEnabled;
    settings.steamOverlaySoundEnabled = payload.data.steamOverlaySoundEnabled;
    settings.notifySoundFile         = payload.data.notifySoundFile;
    settings.messageSoundFile        = payload.data.messageSoundFile;
    settings.mediaRelaySoundFile     = payload.data.mediaRelaySoundFile;
    settings.steamOverlaySoundFile   = payload.data.steamOverlaySoundFile;
    settings.notifySoundVolume       = payload.data.notifySoundVolume;
    settings.messageSoundVolume      = payload.data.messageSoundVolume;
    settings.mediaRelaySoundVolume   = payload.data.mediaRelaySoundVolume;
    settings.steamOverlaySoundVolume = payload.data.steamOverlaySoundVolume;
    settings.friendOnlineToastEnabled = payload.data.friendOnlineToastEnabled;
    settings.friendOnlineToastFavOnly = payload.data.friendOnlineToastFavOnly;
    settings.webhooks = w;
    settings.Webhooks = w;
    const vroTtsDev = (typeof audioDeviceValue === 'function') ? audioDeviceValue('vroTtsDevice') : null;
    if (vroTtsDev) { payload.data.vroTtsDeviceId = vroTtsDev.id; payload.data.vroTtsDeviceName = vroTtsDev.name; }
    const fsOutDev = (typeof fsCurrentOutputDevice === 'function') ? fsCurrentOutputDevice() : null;
    if (fsOutDev) { payload.data.fsOutputDeviceId = fsOutDev.id; payload.data.fsOutputDeviceName = fsOutDev.name; }
    sendToCS(payload);
}

function updateTrayNotifToggle() {
    const hideInTray = document.getElementById('setMinimizeToTray')?.checked ?? false;
    const row = document.getElementById('trayNotifRow');
    const desc = document.getElementById('trayNotifDesc');
    if (row)  row.classList.toggle('disabled', !hideInTray);
    if (desc) desc.classList.toggle('disabled', !hideInTray);
}

function onDbOptimizeChange() {
    const on = document.getElementById('setDbOptimize').checked;
    document.getElementById('setDbOptimizeMaxEntries').disabled = !on;
    document.getElementById('dbOptimizeOffWarning').style.display = on ? 'none' : '';
}

// Autosave: debounced save on any settings change
let _autoSaveTimer = null;
function autoSave() {
    clearTimeout(_autoSaveTimer);
    _autoSaveTimer = setTimeout(() => saveSettings(), 600);
}
// Attach autosave listeners after DOM ready
function initAutoSave() {
    const ids = ['setBotName','setBotAvatar','setVrcPath','setStartWithWindows','setMinimizeToTray','setTrayNotifications',
        'setNotifySoundEnabled','setMessageSoundEnabled','setMediaRelaySoundEnabled','setSteamOverlaySoundEnabled',
        'setFriendsSidebarLocationOnly','setFriendsSidebarPreviewCollapsed','setFriendsSidebarPreviewOpen','setPeopleAlwaysStats',
        'setRandomBg','setClockEnabled','setDateEnabled','setShowVrcPlus','setShowVrcCredits','setShowApiHealth',
        'setAutoStartVR','setAutoStartDesktop',
        'setCloseWithVrc','setStartAlwaysWithVrc',
        'setCbAutoStartVR','setCbAutoStartDesktop',
        'setSfAutoStartVR',
        'setYtAutoStartVR','setYtAutoStartDesktop',
        'setVfAutoStartVR','setVfAutoStartDesktop',
        'setDpAutoStartVR','setDpAutoStartDesktop',
        'setImgCacheEnabled','setImgCacheLimit','setImgCacheOptimizeEnabled','setImgMemoryOptimizeEnabled','setVrcPlusOptimize','setMemoryTrimEnabled','setSendCrashData','setRestartAfterCrash',
        'setPerfGpuAccel','setPerfShaderCache','setPerfV8Heap','setPerfRenderProc',
        'setPerfAnimations','setPerfBlur'];
    ids.forEach(id => {
        const el = document.getElementById(id);
        if (!el) return;
        if (el.type === 'checkbox') el.addEventListener('change', autoSave);
        else if (el.type === 'range') el.addEventListener('input', autoSave);
        else el.addEventListener('input', autoSave);
    });

}

function updateSquareFrameToggle() {
    const pairs = [['setSquareIconFrames', typeof decoSettingSelf === 'function' ? decoSettingSelf('enableProfileIconFrames') : !!(typeof settings !== 'undefined' && settings.enableProfileIconFrames)], ['setSquareIconFramesOthers', typeof decoSettingOthers === 'function' ? decoSettingOthers('enableProfileIconFrames') : !!(typeof settings !== 'undefined' && settings.enableProfileIconFramesOthers)]];
    for (const [id, framesOn] of pairs) {
        const el = document.getElementById(id);
        if (!el) continue;
        el.disabled = !framesOn;
        const item = el.closest('.deco-dual-item') || el.closest('.sf-toggle-row');
        if (item) item.style.opacity = el.disabled ? '.45' : '';
    }
}

const SND_SLOT_IDS = {
    notify:       'setNotifySoundFile',
    message:      'setMessageSoundFile',
    mediaRelay:   'setMediaRelaySoundFile',
    steamOverlay: 'setSteamOverlaySoundFile',
};

function sndPopulateSelects() {
    const lib = typeof SOUND_LIBRARY !== 'undefined' ? SOUND_LIBRARY : [];
    for (const id of Object.values(SND_SLOT_IDS)) {
        const el = document.getElementById(id);
        if (!el) continue;
        const prev = el.value;
        el.innerHTML = '';
        const def = document.createElement('option');
        def.value = '';
        def.textContent = t('settings.sounds.default', 'Default');
        el.appendChild(def);
        for (const file of lib) {
            const o = document.createElement('option');
            o.value = file;
            o.textContent = file.replace(/\.wav$/i, '');
            el.appendChild(o);
        }
        el.value = prev;
        el._vnRefresh?.();
    }
}

function sndOnVolumeInput(slot, el) {
    const val = Number(el.value) || 0;
    const cfg = typeof SOUND_SLOTS !== 'undefined' ? SOUND_SLOTS[slot] : null;
    if (cfg) settings[cfg.volKey] = val;
    const base = el.id.replace(/^set/, '');
    const lbl = document.getElementById(base.charAt(0).toLowerCase() + base.slice(1) + 'Val');
    if (lbl) lbl.textContent = val + '%';
    autoSave();
}

function loadSettingsToUI(s) {
    settings = s;
    // Debug: log webhook data received from C#
    const wh = s.Webhooks || s.webhooks || [];
    console.log('[LOAD] Settings received. Webhooks:', JSON.stringify(wh));
    currentLanguage = normalizeUiLanguage((s.Language || s.language || currentLanguage || 'en').toLowerCase());
    renderLanguageChips();
    requestTranslation(currentLanguage);
    document.getElementById('setBotName').value = s.BotName || s.botName || '';
    document.getElementById('setBotAvatar').value = s.BotAvatarUrl || s.botAvatarUrl || '';
    document.getElementById('setVrcPath').value = s.VrcPath || s.vrcPath || '';
    // setVrcUser and setVrcPass were removed since login now runs through the Accounts tab.
    const _asVR  = document.getElementById('setAutoStartVR');  if (_asVR)  _asVR.checked  = s.RelayAutoStartVR  ?? s.relayAutoStartVR  ?? false;
    const _asDT  = document.getElementById('setAutoStartDesktop'); if (_asDT) _asDT.checked = s.RelayAutoStartDesktop ?? s.relayAutoStartDesktop ?? false;
    document.getElementById('setStartWithWindows').checked = s.StartWithWindows || s.startWithWindows || false;
    document.getElementById('setMinimizeToTray').checked = s.MinimizeToTray ?? s.minimizeToTray ?? false;
    const _trayNotifEl = document.getElementById('setTrayNotifications');
    if (_trayNotifEl) _trayNotifEl.checked = s.TrayNotificationsEnabled ?? s.trayNotificationsEnabled ?? false;
    updateTrayNotifToggle();
    document.getElementById('setNotifySoundEnabled').checked = s.NotifySoundEnabled ?? s.notifySoundEnabled ?? false;
    document.getElementById('setMessageSoundEnabled').checked = s.MessageSoundEnabled ?? s.messageSoundEnabled ?? false;
    document.getElementById('setMediaRelaySoundEnabled').checked = s.MediaRelaySoundEnabled ?? s.mediaRelaySoundEnabled ?? false;
    const _fotEl = document.getElementById('setFriendOnlineToastEnabled');
    if (_fotEl) _fotEl.checked = s.FriendOnlineToastEnabled ?? s.friendOnlineToastEnabled ?? false;
    const _fotFavEl = document.getElementById('setFriendOnlineToastFavOnly');
    if (_fotFavEl) _fotFavEl.checked = s.FriendOnlineToastFavOnly ?? s.friendOnlineToastFavOnly ?? false;
    _fotUpdateFavOnly();
    settings.friendsSidebarLocationOnly = s.FriendsSidebarLocationOnly ?? s.friendsSidebarLocationOnly ?? true;
    const _fslEl = document.getElementById('setFriendsSidebarLocationOnly');
    if (_fslEl) _fslEl.checked = settings.friendsSidebarLocationOnly;
    settings.friendsSidebarPreviewCollapsed = s.FriendsSidebarPreviewCollapsed ?? s.friendsSidebarPreviewCollapsed ?? true;
    const _fspcEl = document.getElementById('setFriendsSidebarPreviewCollapsed');
    if (_fspcEl) _fspcEl.checked = settings.friendsSidebarPreviewCollapsed;
    settings.friendsSidebarPreviewOpen = s.FriendsSidebarPreviewOpen ?? s.friendsSidebarPreviewOpen ?? false;
    const _fspoEl = document.getElementById('setFriendsSidebarPreviewOpen');
    if (_fspoEl) _fspoEl.checked = settings.friendsSidebarPreviewOpen;
    settings.separateFavoriteFriends = s.SeparateFavoriteFriends ?? s.separateFavoriteFriends ?? false;
    const _sffEl = document.getElementById('setSeparateFavoriteFriends');
    if (_sffEl) _sffEl.checked = settings.separateFavoriteFriends;
    if (typeof applyFriendsSidebarFavTabs === 'function') applyFriendsSidebarFavTabs();
    settings.peopleAlwaysStats = s.PeopleAlwaysStats ?? s.peopleAlwaysStats ?? false;
    const _pasEl = document.getElementById('setPeopleAlwaysStats');
    if (_pasEl) _pasEl.checked = settings.peopleAlwaysStats;
    if (typeof applyPeopleAlwaysStats === 'function') applyPeopleAlwaysStats();
    settings.commentsOnWorldsEnabled = s.CommentsOnWorldsEnabled ?? s.commentsOnWorldsEnabled ?? true;
    const _cowEl = document.getElementById('setCommentsOnWorlds');
    if (_cowEl) _cowEl.checked = settings.commentsOnWorldsEnabled;
    if (typeof applyWorldCommentsEnabled === 'function') applyWorldCommentsEnabled();
    settings.modernFolderLayout = s.ModernFolderLayout ?? s.modernFolderLayout ?? true;
    const _mflEl = document.getElementById('setModernFolderLayout');
    if (_mflEl) _mflEl.checked = settings.modernFolderLayout;
    settings.navSidebarHoverText = s.NavSidebarHoverText ?? s.navSidebarHoverText ?? true;
    const _nshtEl = document.getElementById('setNavSidebarHoverText');
    if (_nshtEl) _nshtEl.checked = settings.navSidebarHoverText;
    if (typeof applyNavFolderMode === 'function') applyNavFolderMode();
    settings.enableVrcPlusDecorations = s.EnableVrcPlusDecorations ?? s.enableVrcPlusDecorations ?? false;
    const _vpdEl = document.getElementById('setVrcPlusDecorations');
    if (_vpdEl) _vpdEl.checked = settings.enableVrcPlusDecorations;
    const _vpdRows = document.getElementById('vrcPlusDecoRows');
    if (_vpdRows) _vpdRows.style.display = settings.enableVrcPlusDecorations ? '' : 'none';
    settings.enableProfileIconFrames = s.EnableProfileIconFrames ?? s.enableProfileIconFrames ?? true;
    const _eifEl = document.getElementById('setEnableIconFrames');
    if (_eifEl) _eifEl.checked = settings.enableProfileIconFrames;
    settings.squareIconFrames = s.SquareIconFrames ?? s.squareIconFrames ?? true;
    const _sifEl = document.getElementById('setSquareIconFrames');
    if (_sifEl) _sifEl.checked = settings.squareIconFrames;
    if (typeof updateSquareFrameToggle === 'function') updateSquareFrameToggle();
    settings.enableNameplateDecoration = s.EnableNameplateDecoration ?? s.enableNameplateDecoration ?? true;
    const _endEl = document.getElementById('setEnableNameplateDeco');
    if (_endEl) _endEl.checked = settings.enableNameplateDecoration;
    settings.enableProfileEffects = s.EnableProfileEffects ?? s.enableProfileEffects ?? true;
    const _epeEl = document.getElementById('setEnableProfileEffect');
    if (_epeEl) _epeEl.checked = settings.enableProfileEffects;
    settings.enableProfileBackgrounds = s.EnableProfileBackgrounds ?? s.enableProfileBackgrounds ?? true;
    const _epbEl = document.getElementById('setEnableProfileBg');
    if (_epbEl) _epbEl.checked = settings.enableProfileBackgrounds;
    settings.enableProfileThemes = s.EnableProfileThemes ?? s.enableProfileThemes ?? true;
    const _eptEl = document.getElementById('setEnableProfileThemes');
    if (_eptEl) _eptEl.checked = settings.enableProfileThemes;
    settings.profileThemeContrast = s.ProfileThemeContrast ?? s.profileThemeContrast ?? true;
    const _ptcEl = document.getElementById('setProfileThemeContrast');
    if (_ptcEl) _ptcEl.checked = settings.profileThemeContrast;
    settings.transparentProfileCards = s.TransparentProfileCards ?? s.transparentProfileCards ?? false;
    const _tpcEl = document.getElementById('setTransparentProfileCards');
    if (_tpcEl) _tpcEl.checked = settings.transparentProfileCards;
    settings.showDecorationsOnDashboard = s.ShowDecorationsOnDashboard ?? s.showDecorationsOnDashboard ?? true;
    const _dodEl = document.getElementById('setDecoOnDashboard');
    if (_dodEl) _dodEl.checked = settings.showDecorationsOnDashboard;
    settings.enableProfileIconFramesOthers = s.EnableProfileIconFramesOthers ?? s.enableProfileIconFramesOthers ?? settings.enableProfileIconFrames;
    const _dO0 = document.getElementById('setEnableIconFramesOthers');
    if (_dO0) _dO0.checked = settings.enableProfileIconFramesOthers;
    settings.squareIconFramesOthers = s.SquareIconFramesOthers ?? s.squareIconFramesOthers ?? settings.squareIconFrames;
    const _dO1 = document.getElementById('setSquareIconFramesOthers');
    if (_dO1) _dO1.checked = settings.squareIconFramesOthers;
    settings.enableNameplateDecorationOthers = s.EnableNameplateDecorationOthers ?? s.enableNameplateDecorationOthers ?? settings.enableNameplateDecoration;
    const _dO2 = document.getElementById('setEnableNameplateDecoOthers');
    if (_dO2) _dO2.checked = settings.enableNameplateDecorationOthers;
    settings.enableProfileEffectsOthers = s.EnableProfileEffectsOthers ?? s.enableProfileEffectsOthers ?? settings.enableProfileEffects;
    const _dO3 = document.getElementById('setEnableProfileEffectOthers');
    if (_dO3) _dO3.checked = settings.enableProfileEffectsOthers;
    settings.enableProfileBackgroundsOthers = s.EnableProfileBackgroundsOthers ?? s.enableProfileBackgroundsOthers ?? settings.enableProfileBackgrounds;
    const _dO4 = document.getElementById('setEnableProfileBgOthers');
    if (_dO4) _dO4.checked = settings.enableProfileBackgroundsOthers;
    settings.enableProfileThemesOthers = s.EnableProfileThemesOthers ?? s.enableProfileThemesOthers ?? settings.enableProfileThemes;
    const _dO5 = document.getElementById('setEnableProfileThemesOthers');
    if (_dO5) _dO5.checked = settings.enableProfileThemesOthers;
    settings.profileThemeContrastOthers = s.ProfileThemeContrastOthers ?? s.profileThemeContrastOthers ?? settings.profileThemeContrast;
    const _dO6 = document.getElementById('setProfileThemeContrastOthers');
    if (_dO6) _dO6.checked = settings.profileThemeContrastOthers;
    settings.transparentProfileCardsOthers = s.TransparentProfileCardsOthers ?? s.transparentProfileCardsOthers ?? settings.transparentProfileCards;
    const _dO7 = document.getElementById('setTransparentProfileCardsOthers');
    if (_dO7) _dO7.checked = settings.transparentProfileCardsOthers;
    settings.showDecorationsOnDashboardOthers = s.ShowDecorationsOnDashboardOthers ?? s.showDecorationsOnDashboardOthers ?? settings.showDecorationsOnDashboard;
    const _dO8 = document.getElementById('setDecoOnDashboardOthers');
    if (_dO8) _dO8.checked = settings.showDecorationsOnDashboardOthers;
    if (typeof applyDecorationsSetting === 'function') applyDecorationsSetting();
    settings.folders = s.WatchFolders || s.watchFolders || s.folders || [];
    settings.relayEnabledFolders = s.RelayEnabledFolders ?? s.relayEnabledFolders ?? null;
    settings.extraExe = s.ExtraExe || s.extraExe || [];
    // Migration: if new lists are empty but legacy extraExe has items, pre-populate both lists from it
    const _legacyExe = settings.extraExe;
    settings.extraExeDesktop = (s.ExtraExeDesktop || s.extraExeDesktop || []).length
        ? (s.ExtraExeDesktop || s.extraExeDesktop)
        : (_legacyExe.length ? [..._legacyExe] : []);
    settings.extraExeVR = (s.ExtraExeVR || s.extraExeVR || []).length
        ? (s.ExtraExeVR || s.extraExeVR)
        : (_legacyExe.length ? [..._legacyExe] : []);
    const _cwvEl = document.getElementById('setCloseWithVrc');
    if (_cwvEl) _cwvEl.checked = s.CloseWithVrc ?? s.closeWithVrc ?? false;
    const _sawvEl = document.getElementById('setStartAlwaysWithVrc');
    if (_sawvEl) _sawvEl.checked = s.StartAlwaysWithVrc ?? s.startAlwaysWithVrc ?? true;
    settings.notifySoundEnabled = s.NotifySoundEnabled ?? s.notifySoundEnabled ?? false;
    settings.messageSoundEnabled = s.MessageSoundEnabled ?? s.messageSoundEnabled ?? false;
    settings.mediaRelaySoundEnabled = s.MediaRelaySoundEnabled ?? s.mediaRelaySoundEnabled ?? false;
    settings.steamOverlaySoundEnabled = s.SteamOverlaySoundEnabled ?? s.steamOverlaySoundEnabled ?? true;

    sndPopulateSelects();
    for (const [slot, id] of Object.entries(SND_SLOT_IDS)) {
        const cfg = SOUND_SLOTS[slot];
        const key = cfg.fileKey.charAt(0).toUpperCase() + cfg.fileKey.slice(1);
        const file = s[key] ?? s[cfg.fileKey] ?? '';
        settings[cfg.fileKey] = file;
        const el = document.getElementById(id);
        if (el) { el.value = file; el._vnRefresh?.(); }

        const vKey = cfg.volKey.charAt(0).toUpperCase() + cfg.volKey.slice(1);
        const vol = s[vKey] ?? s[cfg.volKey] ?? 50;
        settings[cfg.volKey] = vol;
        const vEl = document.getElementById(id.replace('File', 'Volume'));
        if (vEl) vEl.value = vol;
        const vLbl = document.getElementById(cfg.volKey + 'Val');
        if (vLbl) vLbl.textContent = vol + '%';
    }
    applySoundSettings();
    settings.friendOnlineToastEnabled = s.FriendOnlineToastEnabled ?? s.friendOnlineToastEnabled ?? false;
    settings.friendOnlineToastFavOnly = s.FriendOnlineToastFavOnly ?? s.friendOnlineToastFavOnly ?? false;
    const _sovEl = document.getElementById('setSteamOverlaySoundEnabled');
    if (_sovEl) _sovEl.checked = settings.steamOverlaySoundEnabled;
    // Restore GUI zoom level
    const savedZoom = s.GuiZoom ?? s.guiZoom ?? 100;
    applyGuiZoom(savedZoom / 100);

    dashBgPath = s.DashBgPath || s.dashBgPath || '';
    if (typeof loadDashLayout === 'function') loadDashLayout({ hero: s.DashHero || s.dashHero, rows: s.DashRows || s.dashRows, order: s.DashSectionOrder || s.dashSectionOrder, hidden: s.DashSectionHidden || s.dashSectionHidden });

    const randomBg = s.RandomDashBg || s.randomDashBg || false;
    document.getElementById('setRandomBg').checked = randomBg;
    document.getElementById('setClockEnabled').checked = s.ClockEnabled ?? s.clockEnabled ?? false;
    document.getElementById('setDateEnabled').checked = s.DateEnabled ?? s.dateEnabled ?? false;
    document.getElementById('setShowVrcPlus').checked = s.ShowVrcPlus ?? s.showVrcPlus ?? true;
    document.getElementById('setShowVrcCredits').checked = s.ShowVrcCredits ?? s.showVrcCredits ?? true;
    document.getElementById('setShowApiHealth').checked = s.ShowApiHealth ?? s.showApiHealth ?? true;
    applyClockSettings();
    if (typeof applyApiHealthSettings === 'function') applyApiHealthSettings();
    if (randomBg) {
        // Request random image from watch folders
        sendToCS({ action: 'vrcRandomDashBg' });
    } else if (dashBgPath) {
        document.getElementById('dashBgName').textContent = dashBgPath.split(/[\\\\/]/).pop();
        sendToCS({ action: 'vrcLoadDashBg', path: dashBgPath });
    }
    settings.webhooks = (s.Webhooks || s.webhooks || []).slice(0, 4);
    settings.Webhooks = settings.webhooks;
    renderWebhookCards(settings.webhooks);
    renderFolders(settings.folders);
    if (typeof renderRelayFolders === 'function') renderRelayFolders();
    renderExtraExeDesktop(settings.extraExeDesktop);
    renderExtraExeVR(settings.extraExeVR);
    updateFolderFilterOptions(settings.folders);
    currentTheme = s.Theme || s.theme || 'vrcn';
    currentSpecialTheme = s.SpecialTheme || s.specialTheme || '';
    autoColorAccuracy = s.AutoColorAccuracy ?? s.autoColorAccuracy ?? 50;
    const accSlider = document.getElementById('setAutoAccuracy');
    if (accSlider) { accSlider.value = autoColorAccuracy; document.getElementById('autoAccuracyVal').textContent = autoColorAccuracy + '%'; }
    const accRow = document.getElementById('autoAccuracyRow');
    if (accRow) accRow.style.display = currentSpecialTheme === 'auto' ? 'flex' : 'none';
    if (THEMES[currentTheme]) applyColors(THEMES[currentTheme].c, THEMES[currentTheme].light ? { on: true, colors: THEMES[currentTheme].cLight } : null);
    else if (!currentTheme.startsWith('custom_')) { currentTheme = 'vrcn'; applyColors(THEMES.vrcn.c); }
    // custom_ themes are applied later when customColors loads
    renderThemeChips();
    renderSpecialThemeChips();
    _localHttpPort = s.LocalHttpPort || s.localHttpPort || 0;
    currentCursorTheme = s.CursorTheme || s.cursorTheme || '';
    applyAppFont(s.AppFont || s.appFont || APP_FONT_DEFAULT);
    applyCustomFont(s.CustomFont || s.customFont || '');
    applyFontSizeOffset(s.FontSizeOffset ?? s.fontSizeOffset ?? 0);
    applyTaskbarHeight(s.TaskbarHeight ?? s.taskbarHeight ?? 42);
    sendToCS({ action: 'getSystemFonts' });
    renderFontGrid();
    sendToCS({ action: 'getCursorFiles' });
    _activeCustomThemes = new Set(s.ActiveCustomThemes || s.activeCustomThemes || []);
    sendToCS({ action: 'getCustomThemes' });

    // Restore chatbox settings
    document.getElementById('cbShowTime').checked = s.CbShowTime ?? s.cbShowTime ?? true;
    document.getElementById('cbShowMedia').checked = s.CbShowMedia ?? s.cbShowMedia ?? true;
    document.getElementById('cbShowPlaytime').checked = s.CbShowPlaytime ?? s.cbShowPlaytime ?? true;
    document.getElementById('cbShowCustom').checked = s.CbShowCustomText ?? s.cbShowCustomText ?? true;
    document.getElementById('cbShowSystemStats').checked = s.CbShowSystemStats ?? s.cbShowSystemStats ?? false;
    document.getElementById('cbShowAfk').checked = s.CbShowAfk ?? s.cbShowAfk ?? false;
    document.getElementById('cbAfkMessage').value = s.CbAfkMessage || s.cbAfkMessage || 'Currently AFK';
    document.getElementById('cbSuppressSound').checked = s.CbSuppressSound ?? s.cbSuppressSound ?? true;
    document.getElementById('cbHideBackground').checked = s.CbHideBackground ?? s.cbHideBackground ?? false;
    const cbTf = s.CbTimeFormat || s.cbTimeFormat || 'hh:mm tt';
    const cbTfEl = document.getElementById('cbTimeFormat');
    if (cbTfEl) cbTfEl.value = cbTf;
    const cbSep = s.CbSeparator || s.cbSeparator || ' | ';
    const cbSepEl = document.getElementById('cbSeparator');
    if (cbSepEl) cbSepEl.value = cbSep;
    const cbTplEl = document.getElementById('cbTemplate');
    if (cbTplEl) cbTplEl.value = s.CbCustomTemplate || s.cbCustomTemplate || '';
    if (typeof cbSyncTemplateUi === 'function') cbSyncTemplateUi();
    const cbInt = s.CbIntervalMs || s.cbIntervalMs || 5000;
    const cbIntEl = document.getElementById('cbInterval');
    if (cbIntEl) cbIntEl.value = String(cbInt);
    document.getElementById('cbShowAfkTime').checked = s.CbShowAfkTime ?? s.cbShowAfkTime ?? true;
    const cbAfkMsEl = document.getElementById('cbAfkMouseSec');
    if (cbAfkMsEl) cbAfkMsEl.value = String(s.CbAfkMouseSeconds ?? s.cbAfkMouseSeconds ?? 10);
    const cbAfkKbEl = document.getElementById('cbAfkKeyboardSec');
    if (cbAfkKbEl) cbAfkKbEl.value = String(s.CbAfkKeyboardSeconds ?? s.cbAfkKeyboardSeconds ?? 10);
    document.getElementById('cbStatCpu').checked = s.CbStatCpu ?? s.cbStatCpu ?? true;
    document.getElementById('cbStatRam').checked = s.CbStatRam ?? s.cbStatRam ?? true;
    document.getElementById('cbStatGpu').checked = s.CbStatGpu ?? s.cbStatGpu ?? false;
    document.getElementById('cbStatVram').checked = s.CbStatVram ?? s.cbStatVram ?? false;
    document.getElementById('cbShowPulse').checked = s.CbShowPulse ?? s.cbShowPulse ?? false;
    const cbHrIdEl = document.getElementById('cbHypeRateId');
    if (cbHrIdEl) cbHrIdEl.value = s.CbHypeRateId || s.cbHypeRateId || '';
    const cbAfHrEl = document.getElementById('cbAfHeartRate');
    if (cbAfHrEl) cbAfHrEl.checked = s.CbAfHeartRate ?? s.cbAfHeartRate ?? false;
    document.getElementById('cbShowWindow').checked = s.CbShowWindow ?? s.cbShowWindow ?? false;
    const cbWinFmtEl = document.getElementById('cbWindowFormat');
    if (cbWinFmtEl) cbWinFmtEl.value = s.CbWindowFormat || s.cbWindowFormat || '';
    document.getElementById('cbShowWeather').checked = s.CbShowWeather ?? s.cbShowWeather ?? false;
    const cbWCityEl = document.getElementById('cbWeatherCity');
    if (cbWCityEl) cbWCityEl.value = s.CbWeatherCity || s.cbWeatherCity || '';
    const cbWUnitEl = document.getElementById('cbWeatherUnit');
    if (cbWUnitEl) cbWUnitEl.value = s.CbWeatherUnit || s.cbWeatherUnit || 'celsius';
    const cbWFmtEl = document.getElementById('cbWeatherFormat');
    if (cbWFmtEl) cbWFmtEl.value = s.CbWeatherFormat || s.cbWeatherFormat || '';
    const cbPulseFmtEl = document.getElementById('cbPulseFormat');
    if (cbPulseFmtEl) cbPulseFmtEl.value = s.CbPulseFormat || s.cbPulseFormat || '\u2665 {bpm} BPM';
    cbApplyLineOrder(s.CbLineOrder || s.cbLineOrder);
    chatboxCustomLines = _cbNormalizeLines(s.CbCustomLines || s.cbCustomLines || []);
    renderChatboxLines();

    if (typeof vriInit === 'function') vriInit(s.VrInputMode ?? s.vrInputMode ?? 0);

    // Restore Space Flight settings
    document.getElementById('sfMultiplier').value = s.SfMultiplier ?? s.sfMultiplier ?? 1;
    document.getElementById('sfMultVal').textContent = (s.SfMultiplier ?? s.sfMultiplier ?? 1) + 'x';
    document.getElementById('sfLockX').checked = s.SfLockX ?? s.sfLockX ?? false;
    document.getElementById('sfLockY').checked = s.SfLockY ?? s.sfLockY ?? false;
    document.getElementById('sfLockZ').checked = s.SfLockZ ?? s.sfLockZ ?? false;
    const _sfLR = document.getElementById('sfLeftReset');
    const _sfRR = document.getElementById('sfRightReset');
    const _sfLD = document.getElementById('sfLeftDrag');
    const _sfRD = document.getElementById('sfRightDrag');
    if (_sfLR) _sfLR.value = String(s.SfLeftResetButton  ?? s.sfLeftResetButton  ?? 32);
    if (_sfRR) _sfRR.value = String(s.SfRightResetButton ?? s.sfRightResetButton ?? 0);
    if (_sfLD) _sfLD.value = String(s.SfLeftDragButton   ?? s.sfLeftDragButton   ?? 0);
    if (_sfRD) _sfRD.value = String(s.SfRightDragButton  ?? s.sfRightDragButton  ?? 32);
    const _sfLG = document.getElementById('sfLeftGravity');
    const _sfRG = document.getElementById('sfRightGravity');
    if (_sfLG) _sfLG.value = String(s.SfLeftGravityButton  ?? s.sfLeftGravityButton  ?? 0);
    if (_sfRG) _sfRG.value = String(s.SfRightGravityButton ?? s.sfRightGravityButton ?? 0);
    const _sfGrav = document.getElementById('sfGravity');
    if (_sfGrav) {
        _sfGrav.value = s.SfGravity ?? s.sfGravity ?? 9.8;
        const _sfGravV = document.getElementById('sfGravityVal');
        if (_sfGravV) _sfGravV.textContent = String(_sfGrav.value);
    }
    if (typeof _sfOtherBtns !== 'undefined') {
        const _sfIdx = (s.VrInputMode ?? s.vrInputMode ?? 0) === 1;
        const _sfLegacySet = {
            sfLeftReset:    s.SfLeftResetButton    ?? s.sfLeftResetButton    ?? 32,
            sfRightReset:   s.SfRightResetButton   ?? s.sfRightResetButton   ?? 0,
            sfLeftDrag:     s.SfLeftDragButton     ?? s.sfLeftDragButton     ?? 0,
            sfRightDrag:    s.SfRightDragButton    ?? s.sfRightDragButton    ?? 32,
            sfLeftGravity:  s.SfLeftGravityButton  ?? s.sfLeftGravityButton  ?? 0,
            sfRightGravity: s.SfRightGravityButton ?? s.sfRightGravityButton ?? 0,
        };
        const _sfIndexSet = {
            sfLeftReset:    s.SfIdxLeftResetButton    ?? s.sfIdxLeftResetButton    ?? 0,
            sfRightReset:   s.SfIdxRightResetButton   ?? s.sfIdxRightResetButton   ?? 0,
            sfLeftDrag:     s.SfIdxLeftDragButton     ?? s.sfIdxLeftDragButton     ?? 0,
            sfRightDrag:    s.SfIdxRightDragButton    ?? s.sfIdxRightDragButton    ?? 0,
            sfLeftGravity:  s.SfIdxLeftGravityButton  ?? s.sfIdxLeftGravityButton  ?? 0,
            sfRightGravity: s.SfIdxRightGravityButton ?? s.sfIdxRightGravityButton ?? 0,
        };
        _sfOtherBtns = _sfIdx ? _sfLegacySet : _sfIndexSet;
        if (_sfIdx && typeof sfWriteBtns === 'function') sfWriteBtns(_sfIndexSet);
    }
    if (typeof sfRenderKeybind === 'function') sfRenderKeybind();

    // Restore Space Turn settings
    const _stMult = document.getElementById('stMultiplier');
    if (_stMult) {
        _stMult.value = s.StMultiplier ?? s.stMultiplier ?? 1;
        const _stMultV = document.getElementById('stMultVal');
        if (_stMultV) _stMultV.textContent = _stMult.value + 'x';
    }
    const _stSnap = document.getElementById('stSnapDegrees');
    if (_stSnap) {
        _stSnap.value = String(s.StSnapDegrees ?? s.stSnapDegrees ?? 0);
        if (_stSnap._vnRefresh) _stSnap._vnRefresh();
    }
    const _stSmo = document.getElementById('stSmoothing');
    if (_stSmo) {
        _stSmo.value = s.StSmoothing ?? s.stSmoothing ?? 0;
        const _stSmoV = document.getElementById('stSmoothVal');
        if (_stSmoV) _stSmoV.textContent = _stSmo.value + '%';
    }
    const _stInv = document.getElementById('stInvert');
    if (_stInv) _stInv.checked = !!(s.StInvert ?? s.stInvert ?? false);
    if (typeof _stOtherBtns !== 'undefined') {
        const _stIdx = (s.VrInputMode ?? s.vrInputMode ?? 0) === 1;
        const _stLegacySet = {
            stLeftTurn:   s.StLeftTurnButton   ?? s.stLeftTurnButton   ?? 2,
            stRightTurn:  s.StRightTurnButton  ?? s.stRightTurnButton  ?? 0,
            stLeftReset:  s.StLeftResetButton  ?? s.stLeftResetButton  ?? 0,
            stRightReset: s.StRightResetButton ?? s.stRightResetButton ?? 0,
        };
        const _stIndexSet = {
            stLeftTurn:   s.StIdxLeftTurnButton   ?? s.stIdxLeftTurnButton   ?? 0,
            stRightTurn:  s.StIdxRightTurnButton  ?? s.stIdxRightTurnButton  ?? 0,
            stLeftReset:  s.StIdxLeftResetButton  ?? s.stIdxLeftResetButton  ?? 0,
            stRightReset: s.StIdxRightResetButton ?? s.stIdxRightResetButton ?? 0,
        };
        _stOtherBtns = _stIdx ? _stLegacySet : _stIndexSet;
        if (typeof stWriteBtns === 'function') stWriteBtns(_stIdx ? _stIndexSet : _stLegacySet);
    }
    if (typeof stRenderKeybind === 'function') stRenderKeybind();

    // Restore VR/Desktop auto-start flags
    const _set = (id, v) => { const el = document.getElementById(id); if (el) el.checked = !!v; };
    _set('setCbAutoStartVR',      s.ChatboxAutoStartVR      ?? s.chatboxAutoStartVR      ?? false);
    _set('setCbAutoStartDesktop', s.ChatboxAutoStartDesktop ?? s.chatboxAutoStartDesktop ?? false);
    _set('setSfAutoStartVR',      s.SfAutoStartVR           ?? s.sfAutoStartVR           ?? false);
    _set('setStAutoStartVR',      s.StAutoStartVR           ?? s.stAutoStartVR           ?? false);
    _set('setFsAutoStartVR',      s.FsAutoStartVR           ?? s.fsAutoStartVR           ?? false);
    const _fsLeftEl  = document.getElementById('fsLeftButton');
    const _fsRightEl = document.getElementById('fsRightButton');
    if (_fsLeftEl)  _fsLeftEl.value  = String(s.FsLeftButton  ?? s.fsLeftButton  ?? 2);
    if (_fsRightEl) _fsRightEl.value = String(s.FsRightButton ?? s.fsRightButton ?? 2);
    const _fsLR = document.getElementById('fsLeftRecord');
    const _fsRR = document.getElementById('fsRightRecord');
    if (_fsLR) _fsLR.value = String(s.FsLeftRecordButton  ?? s.fsLeftRecordButton  ?? 0);
    if (_fsRR) _fsRR.value = String(s.FsRightRecordButton ?? s.fsRightRecordButton ?? 0);
    const _fsGR = document.getElementById('fsGifMaxResolution');
    const _fsGF = document.getElementById('fsGifMaxFps');
    if (_fsGR) _fsGR.value = String(s.FsGifMaxResolution ?? s.fsGifMaxResolution ?? 512);
    if (_fsGF) _fsGF.value = String(s.FsGifMaxFps        ?? s.fsGifMaxFps        ?? 10);
    const _fsUhr = document.getElementById('fsUseHmdRotations');
    if (_fsUhr) _fsUhr.checked = !!(s.FsUseHmdRotations ?? s.fsUseHmdRotations ?? false);
    const _fsLV  = document.getElementById('fsLeftVideo');
    const _fsRV  = document.getElementById('fsRightVideo');
    if (_fsLV) _fsLV.value = String(s.FsLeftVideoButton  ?? s.fsLeftVideoButton  ?? 0);
    if (_fsRV) _fsRV.value = String(s.FsRightVideoButton ?? s.fsRightVideoButton ?? 0);
    const _fsLA  = document.getElementById('fsLeftAccept');
    const _fsRA  = document.getElementById('fsRightAccept');
    if (_fsLA) _fsLA.value = String(s.FsLeftAcceptButton  ?? s.fsLeftAcceptButton  ?? 0);
    if (_fsRA) _fsRA.value = String(s.FsRightAcceptButton ?? s.fsRightAcceptButton ?? 0);
    if (typeof _fsOtherBtns !== 'undefined') {
        const _fsIdx = (s.VrInputMode ?? s.vrInputMode ?? 0) === 1;
        const _fsLegacySet = {
            fsLeftButton:  s.FsLeftButton        ?? s.fsLeftButton        ?? 2,
            fsRightButton: s.FsRightButton       ?? s.fsRightButton       ?? 2,
            fsLeftRecord:  s.FsLeftRecordButton  ?? s.fsLeftRecordButton  ?? 0,
            fsRightRecord: s.FsRightRecordButton ?? s.fsRightRecordButton ?? 0,
            fsLeftVideo:   s.FsLeftVideoButton   ?? s.fsLeftVideoButton   ?? 0,
            fsRightVideo:  s.FsRightVideoButton  ?? s.fsRightVideoButton  ?? 0,
            fsLeftAccept:  s.FsLeftAcceptButton  ?? s.fsLeftAcceptButton  ?? 0,
            fsRightAccept: s.FsRightAcceptButton ?? s.fsRightAcceptButton ?? 0,
        };
        const _fsIndexSet = {
            fsLeftButton:  s.FsIdxLeftButton        ?? s.fsIdxLeftButton        ?? 0,
            fsRightButton: s.FsIdxRightButton       ?? s.fsIdxRightButton       ?? 0,
            fsLeftRecord:  s.FsIdxLeftRecordButton  ?? s.fsIdxLeftRecordButton  ?? 0,
            fsRightRecord: s.FsIdxRightRecordButton ?? s.fsIdxRightRecordButton ?? 0,
            fsLeftVideo:   s.FsIdxLeftVideoButton   ?? s.fsIdxLeftVideoButton   ?? 0,
            fsRightVideo:  s.FsIdxRightVideoButton  ?? s.fsIdxRightVideoButton  ?? 0,
            fsLeftAccept:  s.FsIdxLeftAcceptButton  ?? s.fsIdxLeftAcceptButton  ?? 0,
            fsRightAccept: s.FsIdxRightAcceptButton ?? s.fsIdxRightAcceptButton ?? 0,
        };
        _fsOtherBtns = _fsIdx ? _fsLegacySet : _fsIndexSet;
        if (_fsIdx && typeof fsWriteBtns === 'function') fsWriteBtns(_fsIndexSet);
    }
    if (typeof fsRenderKeybind === 'function') fsRenderKeybind();
    if (typeof _fsSavedAudioA !== 'undefined') _fsSavedAudioA = s.FsVideoDeviceA ?? s.fsVideoDeviceA ?? '';
    if (typeof _fsSavedAudioB !== 'undefined') _fsSavedAudioB = s.FsVideoDeviceB ?? s.fsVideoDeviceB ?? '';
    const _fsVF  = document.getElementById('fsVideoFps');
    const _fsVQ  = document.getElementById('fsVideoQuality');
    const _fsVBQ = document.getElementById('fsVideoBitrateQuality');
    const _fsAK  = document.getElementById('fsAudioKbps');
    if (_fsVF)  _fsVF.value  = String(s.FsVideoFps            ?? s.fsVideoFps            ?? 30);
    if (_fsVQ)  _fsVQ.value  = String(s.FsVideoQuality        ?? s.fsVideoQuality        ?? '1080p');
    if (_fsVBQ) _fsVBQ.value = String(s.FsVideoBitrateQuality ?? s.fsVideoBitrateQuality ?? 'medium');
    if (_fsAK)  _fsAK.value  = String(s.FsAudioKbps           ?? s.fsAudioKbps           ?? 256);
    if (typeof fsApplySavedOutputDevice === 'function') fsApplySavedOutputDevice(s);
    if (typeof fsRequestFfmpegState === 'function') fsRequestFfmpegState();
    const _fsAr = document.getElementById('fsActivationRadius');
    if (_fsAr) {
        _fsAr.value = String(s.FsActivationRadius ?? s.fsActivationRadius ?? 15);
        const _fsArV = document.getElementById('fsActivationRadiusVal');
        if (_fsArV) _fsArV.textContent = `${_fsAr.value} cm`;
    }
    _set('setYtAutoStartVR',      s.YtAutoStartVR           ?? s.ytAutoStartVR           ?? false);
    _set('setYtAutoStartDesktop', s.YtAutoStartDesktop      ?? s.ytAutoStartDesktop      ?? false);
    _set('setVfAutoStartVR',      s.VfAutoStartVR           ?? s.vfAutoStartVR           ?? false);
    _set('setVfAutoStartDesktop', s.VfAutoStartDesktop      ?? s.vfAutoStartDesktop      ?? false);
    _set('setDpAutoStartVR',      s.DpAutoStartVR           ?? s.dpAutoStartVR           ?? false);
    _set('setDpAutoStartDesktop', s.DpAutoStartDesktop      ?? s.dpAutoStartDesktop      ?? false);
    // Restore Discord privacy + join button toggles
    const _dpPv = [
        ['dpHideInstId_joinme', 'DpHideInstIdJoinMe'],  ['dpHideInstId_online', 'DpHideInstIdOnline'],
        ['dpHideInstId_askme',  'DpHideInstIdAskMe'],   ['dpHideInstId_busy',   'DpHideInstIdBusy'],
        ['dpHideLoc_joinme',    'DpHideLocJoinMe'],     ['dpHideLoc_online',    'DpHideLocOnline'],
        ['dpHideLoc_askme',     'DpHideLocAskMe'],      ['dpHideLoc_busy',      'DpHideLocBusy'],
        ['dpHidePlayers_joinme','DpHidePlayersJoinMe'], ['dpHidePlayers_online','DpHidePlayersOnline'],
        ['dpHidePlayers_askme', 'DpHidePlayersAskMe'],  ['dpHidePlayers_busy',  'DpHidePlayersBusy'],
        ['dpHideJoinBtn_joinme','DpHideJoinBtnJoinMe'], ['dpHideJoinBtn_online','DpHideJoinBtnOnline'],
        ['dpHideJoinBtn_askme', 'DpHideJoinBtnAskMe'],  ['dpHideJoinBtn_busy',  'DpHideJoinBtnBusy'],
    ];
    for (const [id, key] of _dpPv) {
        const el = document.getElementById(id);
        if (el) el.checked = s[key] ?? s[key.charAt(0).toLowerCase() + key.slice(1)] ?? false;
    }

    // VR Overlay settings
    vroLoadSettings({
        vroAttachLeft: s.VroAttachLeft ?? s.vroAttachLeft ?? true,
        vroAttachHand: s.VroAttachHand ?? s.vroAttachHand ?? true,
        vroPosX: s.VroPosX ?? s.vroPosX ?? -0.10,
        vroPosY: s.VroPosY ?? s.vroPosY ?? -0.03,
        vroPosZ: s.VroPosZ ?? s.vroPosZ ?? 0.11,
        vroRotX: s.VroRotX ?? s.vroRotX ?? -180,
        vroRotY: s.VroRotY ?? s.vroRotY ?? 46,
        vroRotZ: s.VroRotZ ?? s.vroRotZ ?? 85,
        vroWidth: s.VroWidth ?? s.vroWidth ?? 0.16,
        vroAutoStartVR:      s.VroAutoStartVR      ?? s.vroAutoStartVR      ?? false,
        vroKeybind:       s.VroKeybind       ?? s.vroKeybind       ?? [],
        vroKeybindHand:   s.VroKeybindHand   ?? s.vroKeybindHand   ?? 0,
        vroKeybindDt:     s.VroKeybindDt     ?? s.vroKeybindDt     ?? [],
        vroKeybindDtHand: s.VroKeybindDtHand ?? s.vroKeybindDtHand ?? 0,
        vrInputMode:         s.VrInputMode         ?? s.vrInputMode         ?? 0,
        vroIdxKeybind:       s.VroIdxKeybind       ?? s.vroIdxKeybind       ?? [],
        vroIdxKeybindHand:   s.VroIdxKeybindHand   ?? s.vroIdxKeybindHand   ?? 0,
        vroIdxKeybindDt:     s.VroIdxKeybindDt     ?? s.vroIdxKeybindDt     ?? [],
        vroIdxKeybindDtHand: s.VroIdxKeybindDtHand ?? s.vroIdxKeybindDtHand ?? 0,
        vroKeybindMode:    s.VroKeybindMode   ?? s.vroKeybindMode   ?? 0,
        vroControlRadius:  s.VroControlRadius ?? s.vroControlRadius ?? 16,
        vroDynVis:         s.VroDynVis       ?? s.vroDynVis       ?? false,
        vroFocusRadius:    s.VroFocusRadius  ?? s.vroFocusRadius  ?? 35,
        vroSeamless:       s.VroSeamless     ?? s.vroSeamless     ?? false,
        vroToastEnabled:    s.VroToastEnabled    ?? s.vroToastEnabled    ?? true,
        vroToastFavOnly:    s.VroToastFavOnly    ?? s.vroToastFavOnly    ?? false,
        vroToastSize:       s.VroToastSize       ?? s.vroToastSize       ?? 50,
        vroToastOffsetX:    s.VroToastOffsetX    ?? s.vroToastOffsetX    ?? 0,
        vroToastOffsetY:    s.VroToastOffsetY    ?? s.vroToastOffsetY    ?? -0.12,
        vroToastOnline:     s.VroToastOnline     ?? s.vroToastOnline     ?? true,
        vroToastOffline:    s.VroToastOffline    ?? s.vroToastOffline    ?? true,
        vroToastGps:        s.VroToastGps        ?? s.vroToastGps        ?? true,
        vroToastStatus:     s.VroToastStatus     ?? s.vroToastStatus     ?? true,
        vroToastStatusDesc: s.VroToastStatusDesc ?? s.vroToastStatusDesc ?? true,
        vroToastBio:        s.VroToastBio        ?? s.vroToastBio        ?? true,
        vroToastDuration:   s.VroToastDuration   ?? s.vroToastDuration   ?? 8,
        vroToastStack:      s.VroToastStack      ?? s.vroToastStack      ?? 2,
        vroToastFriendReq:  s.VroToastFriendReq  ?? s.vroToastFriendReq  ?? true,
        vroToastInvite:     s.VroToastInvite     ?? s.vroToastInvite     ?? true,
        vroToastGroupInv:   s.VroToastGroupInv   ?? s.vroToastGroupInv   ?? true,
        vroToastTtsOnline: s.VroToastTtsOnline ?? s.vroToastTtsOnline ?? false,
        vroToastTtsOffline: s.VroToastTtsOffline ?? s.vroToastTtsOffline ?? false,
        vroToastTtsGps: s.VroToastTtsGps ?? s.vroToastTtsGps ?? false,
        vroToastTtsStatus: s.VroToastTtsStatus ?? s.vroToastTtsStatus ?? false,
        vroToastTtsStatusDesc: s.VroToastTtsStatusDesc ?? s.vroToastTtsStatusDesc ?? false,
        vroToastTtsBio: s.VroToastTtsBio ?? s.vroToastTtsBio ?? false,
        vroToastTtsFriendReq: s.VroToastTtsFriendReq ?? s.vroToastTtsFriendReq ?? false,
        vroToastTtsInvite: s.VroToastTtsInvite ?? s.vroToastTtsInvite ?? false,
        vroToastTtsGroupInv: s.VroToastTtsGroupInv ?? s.vroToastTtsGroupInv ?? false,
        vroToastTtsJoined: s.VroToastTtsJoined ?? s.vroToastTtsJoined ?? false,
        vroToastTtsLeft: s.VroToastTtsLeft ?? s.vroToastTtsLeft ?? false,
        vroToastTtsReqInvite: s.VroToastTtsReqInvite ?? s.vroToastTtsReqInvite ?? false,
        vroToastJoined:     s.VroToastJoined     ?? s.vroToastJoined     ?? true,
        vroToastLeft:       s.VroToastLeft       ?? s.vroToastLeft       ?? true,
        vroToastReqInvite:  s.VroToastReqInvite  ?? s.vroToastReqInvite  ?? true,
        vroTtsDeviceId:     s.VroTtsDeviceId     ?? s.vroTtsDeviceId     ?? '',
        vroTtsDeviceName:   s.VroTtsDeviceName   ?? s.vroTtsDeviceName   ?? '',
        vroTtsVoice:        s.VroTtsVoice        ?? s.vroTtsVoice        ?? '',
        vroTtsEngine:       s.VroTtsEngine       ?? s.vroTtsEngine       ?? 'sapi',
        vroTtsLang:         s.VroTtsLang         ?? s.vroTtsLang         ?? '',
        vroTtsGender:       s.VroTtsGender       ?? s.vroTtsGender       ?? '',
        vroWaterEnabled:    s.VroWaterEnabled    ?? s.vroWaterEnabled    ?? false,
        vroWaterHours:      s.VroWaterHours      ?? s.vroWaterHours      ?? 1,
        vroWaterMinutes:    s.VroWaterMinutes    ?? s.vroWaterMinutes    ?? 0,
        vroScaleEnabled:     s.VroScaleEnabled     ?? s.vroScaleEnabled     ?? true,
        vroScaleLeftThumb:   s.VroScaleLeftThumb   ?? s.vroScaleLeftThumb   ?? false,
        vroScaleRightThumb:  s.VroScaleRightThumb  ?? s.vroScaleRightThumb  ?? true,
        vroScaleKeybind:            s.VroScaleKeybind            ?? s.vroScaleKeybind            ?? [],
        vroScaleKeybindHand:        s.VroScaleKeybindHand        ?? s.vroScaleKeybindHand        ?? 0,
        vroIdxScaleKeybind:         s.VroIdxScaleKeybind         ?? s.vroIdxScaleKeybind         ?? [],
        vroIdxScaleKeybindHand:     s.VroIdxScaleKeybindHand     ?? s.vroIdxScaleKeybindHand     ?? 0,
        vroScaleScrollSensitivity:  s.VroScaleScrollSensitivity  ?? s.vroScaleScrollSensitivity  ?? 25,
    });
    // Auto-starts are now triggered by vrcLaunched (see messages.js)

    // Avatar Scaling settings
    if (typeof asLoadSettings === 'function') {
        asLoadSettings({
            autoStartVR:      s.AsAutoStartVR            ?? s.asAutoStartVR            ?? false,
            autoStartDesktop: s.AsAutoStartDesktop       ?? s.asAutoStartDesktop       ?? false,
            useSafety:        s.AsUseSafetySettings      ?? s.asUseSafety              ?? false,
            scale:            s.AsScale                  ?? s.asScale                  ?? 1.0,
            scaleMin:         s.AsScaleMin               ?? s.asScaleMin               ?? 0.5,
            scaleMax:         s.AsScaleMax               ?? s.asScaleMax               ?? 3.0,
            saveScale:        s.AsSaveScaleBetweenWorlds ?? s.asSaveScale              ?? false,
            keyUp:            s.AsKeyUp                  ?? s.asKeyUp                  ?? 0,
            keyDown:          s.AsKeyDown                ?? s.asKeyDown                ?? 0,
            smoothing:        s.AsSmoothing              ?? s.asSmoothing              ?? 30,
        });
    }

    // Image cache settings
    const imgCacheLimitGb         = Math.max(5, Math.min(30, s.ImgCacheLimitGb ?? s.imgCacheLimitGb ?? 5));
    const imgCacheOptimizeEnabled = s.ImgCacheOptimizeEnabled ?? s.imgCacheOptimizeEnabled ?? true;
    const imgMemoryOptimizeEnabled = s.ImgMemoryOptimizeEnabled ?? s.imgMemoryOptimizeEnabled ?? true;
    imgThumbsEnabled = imgMemoryOptimizeEnabled === true;
    const memOptEl = document.getElementById('setImgMemoryOptimizeEnabled');
    if (memOptEl) memOptEl.checked = imgThumbsEnabled;
    vrcPlusOptimizeEnabled = (s.VrcPlusOptimizeEnabled ?? s.vrcPlusOptimizeEnabled ?? true) === true;
    const vpOptEl = document.getElementById('setVrcPlusOptimize');
    if (vpOptEl) vpOptEl.checked = vrcPlusOptimizeEnabled;
    document.getElementById('setImgCacheLimit').value = imgCacheLimitGb;
    document.getElementById('imgCacheLimitVal').textContent = imgCacheLimitGb + ' GB';
    document.getElementById('setImgCacheOptimizeEnabled').checked = imgCacheOptimizeEnabled;
    sendToCS({ action: 'getImgCacheSize' });

    // Fast Fetch Cache
    document.getElementById('setFfcEnabled').checked = s.FfcEnabled ?? s.ffcEnabled ?? true;

    // Avtrdb Support
    document.getElementById('setAvtrdbReport').checked = s.AvtrdbReportDeleted ?? s.avtrdbReportDeleted ?? true;
    document.getElementById('setAvtrdbSubmit').checked = s.AvtrdbSubmitAvatars ?? s.avtrdbSubmitAvatars ?? false;
    document.getElementById('setAvtrIcuReport').checked = s.AvtrIcuReportDeleted ?? s.avtrIcuReportDeleted ?? true;
    document.getElementById('setAvtrIcuSubmit').checked = s.AvtrIcuSubmitAvatars ?? s.avtrIcuSubmitAvatars ?? false;
    document.getElementById('setVrcndbSubmit').checked = s.VrcndbSubmitAvatars ?? s.vrcndbSubmitAvatars ?? false;
    document.getElementById('setVrcndbReport').checked = s.VrcndbReportDeleted ?? s.vrcndbReportDeleted ?? false;
    document.getElementById('setVrcndbSyncLikes').checked = s.VrcndbSyncLikes ?? s.vrcndbSyncLikes ?? true;
    document.getElementById('setVrcndbSyncWears').checked = s.VrcndbSyncWears ?? s.vrcndbSyncWears ?? true;

    // Memory Trim
    document.getElementById('setMemoryTrimEnabled').checked = s.MemoryTrimEnabled ?? s.memoryTrimEnabled ?? true;
    { const _mfEl = document.getElementById('setMediaFixEnabled'); if (_mfEl) _mfEl.checked = s.MediaFixEnabled ?? s.mediaFixEnabled ?? true; }

    const multiTaskMode = s.MultiTaskMode ?? s.multiTaskMode ?? false;
    { const _mtEl = document.getElementById('setMultiTaskMode'); if (_mtEl) _mtEl.checked = multiTaskMode; }
    if (typeof wmSetEnabled === 'function') wmSetEnabled(multiTaskMode);

    const tilingManager = s.TilingManager ?? s.tilingManager ?? true;
    { const _tmEl = document.getElementById('setTilingManager'); if (_tmEl) _tmEl.checked = tilingManager; }
    if (typeof wmSetTiling === 'function') wmSetTiling(tilingManager);
    updateTilingManagerToggle();

    // Database optimization
    const dbOptimize           = s.DbOptimize ?? s.dbOptimize ?? true;
    const dbOptimizeMaxEntries = Math.max(500, Math.min(250000, s.DbOptimizeMaxEntries ?? s.dbOptimizeMaxEntries ?? 500));
    document.getElementById('setDbOptimize').checked            = dbOptimize;
    document.getElementById('setDbOptimizeMaxEntries').value    = dbOptimizeMaxEntries;
    document.getElementById('setDbOptimizeMaxEntries').disabled = !dbOptimize;
    document.getElementById('dbOptimizeEntriesVal').textContent = dbOptimizeMaxEntries;
    document.getElementById('dbOptimizeOffWarning').style.display = dbOptimize ? 'none' : '';

    // Crash Reporting
    document.getElementById('setAutoUpdate').checked          = s.AutoUpdate          ?? s.autoUpdate          ?? true;
    document.getElementById('setSendCrashData').checked      = s.SendCrashData      ?? s.sendCrashData      ?? false;
    document.getElementById('setRestartAfterCrash').checked  = s.RestartAfterCrash  ?? s.restartAfterCrash  ?? true;

    // Window Behavior
    const rwsEl = document.getElementById('setRememberWindowSize');
    if (rwsEl) rwsEl.checked = s.RememberWindowSize ?? s.rememberWindowSize ?? false;
    const rwpEl = document.getElementById('setRememberWindowPosition');
    if (rwpEl) rwpEl.checked = s.RememberWindowPosition ?? s.rememberWindowPosition ?? false;

    // Auto-Backups
    const rbeEl = document.getElementById('setRegBackupEnabled');
    if (rbeEl) rbeEl.checked = s.RegBackupEnabled ?? s.regBackupEnabled ?? true;
    const rbcEl = document.getElementById('setRegBackupCycle');
    if (rbcEl) rbcEl.value = String(s.RegBackupDays ?? s.regBackupDays ?? 30);
    const dbeEl = document.getElementById('setDbAutoBackupEnabled');
    if (dbeEl) dbeEl.checked = s.DbAutoBackupEnabled ?? s.dbAutoBackupEnabled ?? true;
    const dbcEl = document.getElementById('setDbAutoBackupCycle');
    if (dbcEl) dbcEl.value = String(s.DbAutoBackupDays ?? s.dbAutoBackupDays ?? 60);

    // Text Tools
    const textToolsEnabled = s.TextToolsEnabled ?? s.textToolsEnabled ?? false;
    const ttEl = document.getElementById('setTextToolsEnabled');
    if (ttEl) ttEl.checked = textToolsEnabled;
    toggleTextTools(textToolsEnabled, false);


    // Performance
    const _perfSet = (id, val) => { const el = document.getElementById(id); if (el) el.checked = !!val; };
    _perfSet('setPerfGpuAccel',    s.GpuAcceleration    ?? s.gpuAcceleration    ?? true);
    _perfSet('setLinuxGpuAccel',   s.LinuxGpuAcceleration ?? s.linuxGpuAcceleration ?? false);
    _perfSet('setPerfShaderCache', s.GpuShaderCache     ?? s.gpuShaderCache     ?? false);
    _perfSet('setPerfV8Heap',      s.V8Heap128          ?? s.v8Heap128          ?? false);
    _perfSet('setPerfRenderProc',  s.TwoRenderProcesses ?? s.twoRenderProcesses ?? false);
    _perfSet('setPerfEfficiency',  s.EfficiencyMode     ?? s.efficiencyMode     ?? false);
    const animationsEnabled = s.AnimationsEnabled ?? s.animationsEnabled ?? true;
    const blurEnabled       = s.BlurEnabled       ?? s.blurEnabled       ?? true;
    _perfSet('setPerfAnimations', animationsEnabled);
    _perfSet('setPerfBlur',       blurEnabled);
    applyAnimationsSetting(animationsEnabled);
    applyBlurSetting(blurEnabled);
    const perfHint = document.getElementById('perfRestartHint');
    if (perfHint) perfHint.style.display = 'none';
    const linuxPerfHint = document.getElementById('linuxPerfRestartHint');
    if (linuxPerfHint) linuxPerfHint.style.display = 'none';

    // Search debounce speed
    const searchDebounceMs = s.SearchDebounceMs ?? s.searchDebounceMs ?? 500;
    const sdEl = document.getElementById('setSearchDebounceMs');
    if (sdEl) { sdEl.value = String(searchDebounceMs); sdEl._vnRefresh && sdEl._vnRefresh(); }
    if (typeof rebuildSearchDebouncers === 'function') rebuildSearchDebouncers(searchDebounceMs);

    // Sync custom dropdowns to reflect programmatically set values
    document.querySelectorAll('select').forEach(s => s._vnRefresh && s._vnRefresh());

    if (typeof vriLoaded !== 'undefined') vriLoaded = true;

    // Setup autosave listeners after UI is populated
    setTimeout(initAutoSave, 100);
}


function updateImgCacheSizeBar(bytes) {
    const el = document.getElementById('imgCacheSizeBar');
    const label = document.getElementById('imgCacheSizeLabel');
    if (!el || !label) return;
    const limitGb = parseInt(document.getElementById('setImgCacheLimit').value) || 5;
    const limitBytes = limitGb * 1024 * 1024 * 1024;
    const pct = Math.min(100, (bytes / limitBytes) * 100);
    el.style.width = pct + '%';
    const mb = bytes / (1024 * 1024);
    label.textContent = mb >= 1024
        ? (mb / 1024).toFixed(2) + ' GB used'
        : mb.toFixed(1) + ' MB used';
}

function startForceOptimize() {
    const btn = document.getElementById('btnForceOptimize');
    if (btn) btn.disabled = true;
    sendToCS({ action: 'optimizeImgCache' });
}

const VRC_PLUS_DECO_TOGGLES = {
    setEnableIconFrames: 'enableProfileIconFrames',
    setEnableIconFramesOthers: 'enableProfileIconFramesOthers',
    setSquareIconFrames: 'squareIconFrames',
    setSquareIconFramesOthers: 'squareIconFramesOthers',
    setEnableNameplateDeco: 'enableNameplateDecoration',
    setEnableNameplateDecoOthers: 'enableNameplateDecorationOthers',
    setEnableProfileEffect: 'enableProfileEffects',
    setEnableProfileEffectOthers: 'enableProfileEffectsOthers',
    setEnableProfileBg: 'enableProfileBackgrounds',
    setEnableProfileBgOthers: 'enableProfileBackgroundsOthers',
    setEnableProfileThemes: 'enableProfileThemes',
    setEnableProfileThemesOthers: 'enableProfileThemesOthers',
    setProfileThemeContrast: 'profileThemeContrast',
    setProfileThemeContrastOthers: 'profileThemeContrastOthers',
    setTransparentProfileCards: 'transparentProfileCards',
    setTransparentProfileCardsOthers: 'transparentProfileCardsOthers',
    setDecoOnDashboard: 'showDecorationsOnDashboard',
    setDecoOnDashboardOthers: 'showDecorationsOnDashboardOthers',
};

function onVrcPlusDecorationsToggle(on) {
    settings.enableVrcPlusDecorations = !!on;
    const rows = document.getElementById('vrcPlusDecoRows');
    if (rows) rows.style.display = on ? '' : 'none';
    autoSave();
    if (typeof applyDecorationsSetting === 'function') applyDecorationsSetting();
    if (typeof updateSquareFrameToggle === 'function') updateSquareFrameToggle();
}

function onDecoToggle(key, on) {
    settings[key] = !!on;
    autoSave();
    if (typeof applyDecorationsSetting === 'function') applyDecorationsSetting();
    if (typeof updateSquareFrameToggle === 'function') updateSquareFrameToggle();
}

function onVrcPlusOptimizeToggle() {
    vrcPlusOptimizeEnabled = document.getElementById('setVrcPlusOptimize')?.checked === true;
    autoSave();
    const fl = document.getElementById('vrcFriendsList');
    if (fl) fl.__lastHtml = null;
    ['dashFavWorlds', 'dashFriendsFeed', 'dashFriendLocSmallShelf', 'dashGroupActivityGrid', 'dashGroupActivityCards', 'dashGroupActivityShelf'].forEach(id => {
        const el = document.getElementById(id);
        if (el) el.__lastHtml = null;
    });
    if (typeof renderVrcFriends === 'function' && typeof vrcFriendsData !== 'undefined' && vrcFriendsData.length) renderVrcFriends(vrcFriendsData);
    if (typeof renderDashboard === 'function') renderDashboard();
}

function onImgMemoryOptimizeToggle() {
    imgThumbsEnabled = document.getElementById('setImgMemoryOptimizeEnabled')?.checked === true;
    autoSave();
    const fl = document.getElementById('vrcFriendsList');
    if (fl) fl.__lastHtml = null;
    ['dashFavWorlds', 'dashFriendsFeed', 'dashFriendLocSmallShelf', 'dashGroupActivityGrid', 'dashGroupActivityCards', 'dashGroupActivityShelf'].forEach(id => {
        const el = document.getElementById(id);
        if (el) el.__lastHtml = null;
    });
    if (typeof renderVrcFriends === 'function' && typeof vrcFriendsData !== 'undefined' && vrcFriendsData.length) renderVrcFriends(vrcFriendsData);
    if (typeof renderDashboard === 'function') renderDashboard();
}

function handleImgCacheOptimizeProgress(data) {
    const wrap  = document.getElementById('imgOptimizeProgress');
    const bar   = document.getElementById('imgOptimizeBar');
    const label = document.getElementById('imgOptimizeLabel');
    const btn   = document.getElementById('btnForceOptimize');
    if (!wrap || !bar || !label) return;

    // done = -1 signals completion
    if (data.done === -1) {
        wrap.style.display = 'none';
        bar.style.width = '0%';
        if (btn) btn.disabled = false;
        return;
    }

    wrap.style.display = '';
    if (data.total > 0) {
        const pct = Math.round((data.done / data.total) * 100);
        bar.style.width = pct + '%';
        label.textContent = `Optimizing… ${data.done} / ${data.total} (${pct}%)`;
    } else {
        bar.style.width = '0%';
        label.textContent = 'Scanning…';
    }
}


function onPerfSettingChange() {
    autoSave();
    const hint = document.getElementById('perfRestartHint');
    if (hint) hint.style.display = '';
    const linuxHint = document.getElementById('linuxPerfRestartHint');
    if (linuxHint) linuxHint.style.display = '';
}

function onMultiTaskModeChange(el) {
    if (typeof wmSetEnabled === 'function') wmSetEnabled(!!el.checked);
    autoSave();
    updateTilingManagerToggle();
}

function onTilingManagerChange(el) {
    if (typeof wmSetTiling === 'function') wmSetTiling(!!el.checked);
    autoSave();
}

function updateTilingManagerToggle() {
    const enabled = document.getElementById('setMultiTaskMode')?.checked ?? false;
    const row  = document.getElementById('tilingManagerRow');
    const desc = document.getElementById('tilingManagerDesc');
    if (row)  row.classList.toggle('disabled', !enabled);
    if (desc) desc.classList.toggle('disabled', !enabled);
}

function onSearchDebounceMsChange() {
    const ms = parseInt(document.getElementById('setSearchDebounceMs')?.value) || 500;
    if (typeof rebuildSearchDebouncers === 'function') rebuildSearchDebouncers(ms);
    autoSave();
}

function applyAnimationsSetting(enabled) {
    document.documentElement.classList.toggle('no-animations', !enabled);
}

function applyBlurSetting(enabled) {
    document.documentElement.classList.toggle('no-blur', !enabled);
}

// Intercept Web Animations API (.animate()) to respect the no-animations setting.
// CSS rules cannot control el.animate() calls (drag FLIP, ghost-snap, etc.).
(function() {
    const _origAnimate = Element.prototype.animate;
    Element.prototype.animate = function(keyframes, options) {
        if (document.documentElement.classList.contains('no-animations')) {
            if (typeof options === 'number') options = 0;
            else options = Object.assign({}, options || {}, { duration: 0 });
        }
        return _origAnimate.call(this, keyframes, options);
    };
})();

// Text Tools (Debugging).
let _textToolsEnabled = false;

function toggleTextTools(enabled, save = true) {
    _textToolsEnabled = enabled;
    document.documentElement.classList.toggle('text-tools-active', enabled);
    if (save) autoSave();
}

// VRCX Import

let _vrcxPreviewData = null;
let _vrcxLastProgress = null;
let _vrcxLastDone = null;
let _vrcxLastError = null;

function settingsUiLocale() {
    return getLanguageLocale();
}

function formatSettingsNumber(value) {
    return Number(value ?? 0).toLocaleString(settingsUiLocale());
}

function vrcxSelectBtnHtml(selecting = false) {
    return selecting
        ? `<span class="msi" style="font-size:16px;">hourglass_empty</span> ${t('settings.vrcx.selecting', 'Selecting...')}`
        : `<span class="msi" style="font-size:16px;">storage</span> ${t('settings.vrcx.select_db', 'Select VRCX Database')}`;
}

function vrcxStartBtnHtml(retry = false) {
    return retry
        ? `<span class="msi" style="font-size:16px;">upload</span> ${t('settings.vrcx.retry_import', 'Retry Import')}`
        : `<span class="msi" style="font-size:16px;">upload</span> ${t('settings.vrcx.start_import', 'Start Import')}`;
}

function translateVrcxStatus(status) {
    switch (status) {
        case 'Reading database...':
            return t('settings.vrcx.progress.reading_database', 'Reading database...');
        case 'Reading friend data...':
            return t('settings.vrcx.progress.reading_friend_data', 'Reading friend data...');
        case 'Reading timeline events...':
            return t('settings.vrcx.progress.reading_timeline_events', 'Reading timeline events...');
        case 'Reading friend events...':
            return t('settings.vrcx.progress.reading_friend_events', 'Reading friend events...');
        case 'Generating meet events...':
            return t('settings.vrcx.progress.generating_meet_events', 'Generating meet events...');
        case 'Merging into VRCNext...':
            return t('settings.vrcx.progress.merging', 'Merging into VRCNext...');
        case 'Saving timeline...':
            return t('settings.vrcx.progress.saving_timeline', 'Saving timeline...');
        case 'Done':
            return t('common.done', 'Done');
        default:
            return status || '';
    }
}

function renderVrcxPreviewRows(p) {
    const rows = [
        [t('settings.vrcx.preview.worlds_tracked', 'Worlds tracked'), p.worlds],
        [t('settings.vrcx.preview.location_visits', 'Location visits'), p.locations],
        [t('settings.vrcx.preview.friends_time', 'Friends (time)'), p.friendTimes],
        [t('settings.vrcx.preview.gps_events', 'GPS events'), p.gps],
        [t('settings.vrcx.preview.online_offline', 'Online / Offline'), p.onlineOffline],
        [t('settings.vrcx.preview.status_changes', 'Status changes'), p.statuses],
        [t('settings.vrcx.preview.bio_changes', 'Bio changes'), p.bios],
    ];
    document.getElementById('vrcxPreviewRows').innerHTML = rows.map(([label, val]) =>
        `<div style="display:flex;justify-content:space-between;align-items:center;padding:4px 8px;background:var(--bg-input);border-radius:6px;">
            <span style="font-size:calc(12px + var(--fs-off, 0px));opacity:.7;">${esc(label)}</span>
            <span style="font-size:calc(12px + var(--fs-off, 0px));font-weight:600;">${formatSettingsNumber(val)}</span>
        </div>`
    ).join('');
}

function renderVrcxSuccessDetail(p) {
    const el = document.getElementById('vrcxSuccessDetail');
    if (!el) return;
    el.textContent = tf('settings.vrcx.success.summary', {
        worlds: formatSettingsNumber(p.worlds),
        friends: formatSettingsNumber(p.friends),
        joins: formatSettingsNumber(p.timelineJoins),
        friend_events: formatSettingsNumber(p.friendEvents),
        meets: formatSettingsNumber(p.meetEvents),
    }, `${formatSettingsNumber(p.worlds)} worlds, ${formatSettingsNumber(p.friends)} friends, ${formatSettingsNumber(p.timelineJoins)} joins, ${formatSettingsNumber(p.friendEvents)} friend events, ${formatSettingsNumber(p.meetEvents)} meets`);
}

function renderVrcxError(err) {
    const el = document.getElementById('vrcxImportError');
    if (!el) return;
    el.textContent = tf('settings.vrcx.error.message', {
        error: err || t('settings.vrcx.error.unknown', 'Unknown error'),
    }, `Error: ${err || 'Unknown error'}`);
}

function vrcxSelectFile() {
    const btn = document.getElementById('vrcxSelectBtn');
    btn.disabled = true;
    btn.innerHTML = vrcxSelectBtnHtml(true);
    sendToCS({ action: 'importVrcxSelect' });
}

function vrcxReset() {
    _vrcxPreviewData = null;
    _vrcxLastProgress = null;
    _vrcxLastDone = null;
    _vrcxLastError = null;
    document.getElementById('vrcxPreviewBox').style.display = 'none';
    document.getElementById('vrcxProgressWrap').style.display = 'none';
    document.getElementById('vrcxSuccessCard').style.display = 'none';
    document.getElementById('vrcxImportError').style.display = 'none';
    document.getElementById('vrcxActionBtns').style.display = 'flex';
    document.getElementById('vrcxDoneBtn').style.display = 'none';
    const btn = document.getElementById('vrcxSelectBtn');
    btn.style.display = '';
    btn.disabled = false;
    btn.innerHTML = vrcxSelectBtnHtml(false);
    const start = document.getElementById('vrcxStartBtn');
    start.disabled = false;
    start.innerHTML = vrcxStartBtnHtml(false);
}

function vrcxShowPreview(p) {
    _vrcxPreviewData = p;
    _vrcxLastProgress = null;
    _vrcxLastDone = null;
    _vrcxLastError = null;
    document.getElementById('vrcxSelectBtn').style.display = 'none';
    document.getElementById('vrcxFileName').textContent = p.path || 'VRCX.sqlite3';
    renderVrcxPreviewRows(p);
    document.getElementById('vrcxProgressWrap').style.display = 'none';
    document.getElementById('vrcxSuccessCard').style.display = 'none';
    document.getElementById('vrcxImportError').style.display = 'none';
    document.getElementById('vrcxActionBtns').style.display = 'flex';
    document.getElementById('vrcxDoneBtn').style.display = 'none';
    const start = document.getElementById('vrcxStartBtn');
    start.disabled = false;
    start.innerHTML = vrcxStartBtnHtml(false);
    document.getElementById('vrcxPreviewBox').style.display = '';
}

function vrcxStartImport() {
    _vrcxLastDone = null;
    _vrcxLastError = null;
    _vrcxLastProgress = { percent: 5, status: 'Reading database...' };
    document.getElementById('vrcxActionBtns').style.display = 'none';
    document.getElementById('vrcxImportError').style.display = 'none';
    _vrcxSetProgress(5, 'Reading database...');
    document.getElementById('vrcxProgressWrap').style.display = '';
    sendToCS({ action: 'importVrcxStart' });
}

function _vrcxSetProgress(pct, label) {
    document.getElementById('vrcxProgressBar').style.width = pct + '%';
    const progressLabel = document.getElementById('vrcxProgressLabel');
    progressLabel.dataset.rawLabel = label || '';
    progressLabel.textContent = translateVrcxStatus(label || '');
}

function vrcxShowProgress(p) {
    _vrcxLastProgress = p;
    _vrcxSetProgress(p.percent ?? 0, p.status ?? '');
}

function vrcxShowDone(p) {
    _vrcxLastDone = p;
    _vrcxLastError = null;
    _vrcxLastProgress = { percent: 100, status: 'Done' };
    _vrcxSetProgress(100, 'Done');
    setTimeout(() => {
        document.getElementById('vrcxProgressWrap').style.display = 'none';
        document.getElementById('vrcxSuccessDetail').innerHTML =
            `${(p.worlds ?? 0).toLocaleString()} worlds &nbsp;Â·&nbsp; ` +
            `${(p.friends ?? 0).toLocaleString()} friends &nbsp;Â·&nbsp; ` +
            `${(p.timelineJoins ?? 0).toLocaleString()} joins &nbsp;Â·&nbsp; ` +
            `${(p.friendEvents ?? 0).toLocaleString()} friend events &nbsp;Â·&nbsp; ` +
            `${(p.meetEvents ?? 0).toLocaleString()} meets`;
        renderVrcxSuccessDetail(p);
        document.getElementById('vrcxSuccessCard').style.display = '';
        document.getElementById('vrcxDoneBtn').style.display = '';
    }, 400);
}

function vrcxShowError(err) {
    _vrcxLastDone = null;
    _vrcxLastError = err || '';
    _vrcxLastProgress = null;
    document.getElementById('vrcxProgressWrap').style.display = 'none';
    renderVrcxError(err);
    document.getElementById('vrcxImportError').style.display = '';
    document.getElementById('vrcxActionBtns').style.display = 'flex';
    const start = document.getElementById('vrcxStartBtn');
    start.disabled = false;
    start.innerHTML = vrcxStartBtnHtml(true);
}

// === Design Tabs ===

// === Avtrdb Community Support ===

function switchAvtrdbTab(tab, btn) {
    document.getElementById('avtrdbTabSupport').style.display = tab === 'support' ? '' : 'none';
    document.getElementById('avtrdbTabReports').style.display = tab === 'reports' ? '' : 'none';
    btn.closest('.fd-tabs').querySelectorAll('.fd-tab').forEach(t => t.classList.remove('active'));
    btn.classList.add('active');
}

// === VRCNDb ===

function switchVrcndbTab(tab, btn) {
    document.getElementById('vrcndbTabSupport').style.display = tab === 'support' ? '' : 'none';
    document.getElementById('vrcndbTabReports').style.display = tab === 'reports' ? '' : 'none';
    btn.closest('.fd-tabs').querySelectorAll('.fd-tab').forEach(t => t.classList.remove('active'));
    btn.classList.add('active');
}

const _vrcndbReports = [];

function addVrcndbReport(count, enqueued, duplicates, type) {
    _vrcndbReports.push({ ts: Date.now(), count, enqueued: enqueued || 0, duplicates: duplicates || 0, type: type || 'submit' });
    renderVrcndbReports();
}

function renderVrcndbReports() {
    const el = document.getElementById('vrcndbReportsList');
    if (!el) return;
    if (!_vrcndbReports.length) {
        el.innerHTML = `<div class="empty-msg">${esc(t('settings.vrcndb.reports.empty', 'No submissions sent yet this session.'))}</div>`;
        return;
    }
    el.innerHTML = _vrcndbReports.slice().reverse().map(r => {
        const isSubmit = r.type === 'submit';
        const typeLabel = isSubmit
            ? t('settings.vrcndb.reports.type.submitted', 'Submitted')
            : t('settings.vrcndb.reports.type.recheck', 'Re-check');
        const typeColor = isSubmit ? 'var(--ok)' : 'var(--accent)';
        const typeIcon = isSubmit ? 'upload' : 'refresh';
        const time = fmtTimeSeconds(new Date(r.ts || Date.now()));
        const summary = isSubmit
            ? tf('settings.vrcndb.reports.submit_summary', { enqueued: r.enqueued, dupes: r.duplicates }, `${r.enqueued} new, ${r.duplicates} known`)
            : tf('settings.vrcndb.reports.recheck_summary', { count: r.count }, `${r.count} avatar(s)`);
        return `<div style="display:flex;align-items:center;gap:10px;padding:8px 10px;background:var(--bg-input);border-radius:8px;margin-bottom:6px;">
        <span style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);white-space:nowrap;">${esc(time)}</span>
        <span class="vrcn-badge db-vrcndb" style="font-size:calc(10px + var(--fs-off, 0px));flex-shrink:0;">VRCNDb</span>
        <span class="vrcn-badge" style="font-size:calc(10px + var(--fs-off, 0px));color:${typeColor};flex-shrink:0;"><span class="msi" style="font-size:10px;">${typeIcon}</span> ${esc(typeLabel)}</span>
        <span style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx1);flex:1;">${esc(summary)}</span>
    </div>`;
    }).join('');
}

function showVrcndbConsent() {
    if (document.getElementById('vrcndbConsentModal')) return;
    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    overlay.id = 'vrcndbConsentModal';
    overlay.style.zIndex = '10030';
    overlay.innerHTML = `<div class="modal-box">
        <div class="modal-icon" style="background:rgba(45,212,140,.12);color:var(--ok);"><span class="msi" style="font-size:22px;">hub</span></div>
        <div class="modal-title">${esc(t('settings.vrcndb.consent.title', 'Support the VRCNDb Community Database'))}</div>
        <div class="modal-msg" style="word-break:normal;">${esc(t('settings.vrcndb.consent.body', 'When you join instances, VRCNext collects the avatar IDs of public avatars around you and submits them to the VRCNDb community database (db.vrcnext.com). Only the ID is sent, the server verifies each avatar itself, stores only public avatars and downloads the image on its own. This builds the community avatar search.'))}</div>
        <div class="set-desc" style="margin-bottom:14px;">${esc(t('settings.vrcndb.consent.turn_off', 'If you dont want this please turn off these two sliders.'))}</div>
        <div class="sf-toggle-row">
            <span>${esc(t('settings.vrcndb.consent.submit_toggle', 'Submit avatars to VRCNDb'))}</span>
            <label class="toggle"><input type="checkbox" id="vrcndbConsentSubmit" checked><div class="toggle-track"><div class="toggle-knob"></div></div></label>
        </div>
        <div class="sf-toggle-row">
            <span>${esc(t('settings.vrcndb.consent.report_toggle', 'Report deleted avatars to VRCNDb'))}</span>
            <label class="toggle"><input type="checkbox" id="vrcndbConsentReport" checked><div class="toggle-track"><div class="toggle-knob"></div></div></label>
        </div>
        <div class="sf-toggle-row">
            <span>${esc(t('settings.vrcndb.consent.comments_toggle', 'Show Comments'))}</span>
            <label class="toggle"><input type="checkbox" id="vrcndbConsentComments" checked><div class="toggle-track"><div class="toggle-knob"></div></div></label>
        </div>
        <div class="set-desc" style="margin-top:6px;">${esc(t('settings.vrcndb.consent.comments_desc', 'Show comments in world modals. If you do not want to see user comments, disable this.'))}</div>
        <div class="modal-btns" style="margin-top:22px;">
            <button class="vrcn-button-round vrcn-btn-accent" onclick="confirmVrcndbConsent()">${esc(t('settings.vrcndb.consent.confirm', 'Got it'))}</button>
        </div>
    </div>`;
    document.body.appendChild(overlay);
}

function confirmVrcndbConsent() {
    const submit = document.getElementById('vrcndbConsentSubmit')?.checked ?? true;
    const report = document.getElementById('vrcndbConsentReport')?.checked ?? true;
    const comments = document.getElementById('vrcndbConsentComments')?.checked ?? true;
    const sSubmit = document.getElementById('setVrcndbSubmit');
    const sReport = document.getElementById('setVrcndbReport');
    const sComments = document.getElementById('setCommentsOnWorlds');
    if (sSubmit) sSubmit.checked = submit;
    if (sReport) sReport.checked = report;
    if (sComments) sComments.checked = comments;
    if (typeof settings !== 'undefined') settings.commentsOnWorldsEnabled = comments;
    sendToCS({ action: 'saveVrcndbConsent', submit, report, comments });
    if (typeof applyWorldCommentsEnabled === 'function') applyWorldCommentsEnabled();
    const m = document.getElementById('vrcndbConsentModal');
    if (m) m.remove();
}

const _avtrdbReports = [];
let _avtrdbCollecting = 0;
let _avtrdbCollectTimer = null;
let _avtrdbCollectEnd = 0;

function avtrdbCollecting(count) {
    _avtrdbCollecting += count;
    // Reset 60s countdown on each new batch
    _avtrdbCollectEnd = Date.now() + 60000;
    if (!_avtrdbCollectTimer) {
        _avtrdbCollectTimer = setInterval(() => {
            renderAvtrdbReports();
            if (Date.now() >= _avtrdbCollectEnd) {
                clearInterval(_avtrdbCollectTimer);
                _avtrdbCollectTimer = null;
            }
        }, 1000);
    }
    renderAvtrdbReports();
}

function addAvtrdbReport(count, enqueued, invalid, ticket, type, db) {
    _avtrdbReports.push({ ts: Date.now(), count, enqueued, invalid, ticket, type: type || 'deletion', db: db || 'avtrdb' });
    // Clear collecting state
    _avtrdbCollecting = 0;
    _avtrdbCollectEnd = 0;
    if (_avtrdbCollectTimer) { clearInterval(_avtrdbCollectTimer); _avtrdbCollectTimer = null; }
    renderAvtrdbReports();
}

function renderAvtrdbReports() {
    const el = document.getElementById('avtrdbReportsList');
    if (!el) return;

    let html = '';

    // Show collecting banner if active
    if (_avtrdbCollecting > 0 && _avtrdbCollectEnd > Date.now()) {
        const secsLeft = Math.max(0, Math.ceil((_avtrdbCollectEnd - Date.now()) / 1000));
        html += `<div style="display:flex;align-items:center;gap:10px;padding:10px 12px;background:rgba(var(--accent-rgb,100,140,255),.12);border:1px solid rgba(var(--accent-rgb,100,140,255),.25);border-radius:8px;margin-bottom:10px;">
            <span class="msi" style="font-size:18px;color:var(--accent);">hourglass_top</span>
            <div style="flex:1;">
                <div style="font-size:calc(12px + var(--fs-off, 0px));font-weight:600;color:var(--tx1);">${t('settings.avtrdb.reports.collecting_title', 'Collecting Data')}</div>
                <div style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);margin-top:2px;">${tf('settings.avtrdb.reports.collecting_desc', { count: _avtrdbCollecting, seconds: secsLeft }, `${_avtrdbCollecting} deleted avatar(s) queued, sending in ${secsLeft}s`)}</div>
            </div>
        </div>`;
    }

    if (!_avtrdbReports.length && !html) {
        el.innerHTML = `<div class="empty-msg">${t('settings.avtrdb.reports.empty', 'No reports sent yet this session.')}</div>`;
        return;
    }

    html += _avtrdbReports.slice().reverse().map(r => {
        const isDeletion = r.type === 'deletion';
        const typeLabel = isDeletion
            ? t('settings.avtrdb.reports.type.deletion', 'Mark for deletion')
            : t('settings.avtrdb.reports.type.submitted', 'Submitted Avatar');
        const typeColor = isDeletion ? 'var(--err)' : 'var(--ok)';
        const typeIcon = isDeletion ? 'delete' : 'upload';
        const time = fmtTimeSeconds(new Date(r.ts || Date.now()));
        const summaryParts = [tf('settings.avtrdb.reports.enqueued', { count: r.enqueued }, `${r.enqueued} enqueued`)];
        if (r.invalid > 0) {
            summaryParts.push(tf('settings.avtrdb.reports.invalid', { count: r.invalid }, `${r.invalid} invalid`));
        }
        const isIcu = r.db === 'avtricu';
        const dbBadge = isIcu
            ? `<span class="vrcn-badge db-avtricu" style="font-size:calc(10px + var(--fs-off, 0px));flex-shrink:0;">Avtr.icu</span>`
            : `<span class="vrcn-badge db-avtrdb" style="font-size:calc(10px + var(--fs-off, 0px));flex-shrink:0;">Avtrdb</span>`;
        const ticketBtn = (!isIcu && r.ticket)
            ? `<button class="vrcn-button-round" style="font-size:calc(11px + var(--fs-off, 0px));padding:4px 10px;" onclick="sendToCS({action:'openUrl',url:'https://avtrdb.com/check_ticket_status/${esc(r.ticket)}'})">
            <span class="msi" style="font-size:13px;">open_in_new</span> ${t('settings.avtrdb.reports.ticket', 'Ticket')}
        </button>`
            : '';
        return `<div style="display:flex;align-items:center;gap:10px;padding:8px 10px;background:var(--bg-input);border-radius:8px;margin-bottom:6px;">
        <span style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);white-space:nowrap;">${esc(time)}</span>
        ${dbBadge}
        <span class="vrcn-badge" style="font-size:calc(10px + var(--fs-off, 0px));color:${typeColor};flex-shrink:0;"><span class="msi" style="font-size:10px;">${typeIcon}</span> ${esc(typeLabel)}</span>
        <span style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx1);flex:1;">${esc(summaryParts.join(', '))}</span>
        ${ticketBtn}
    </div>`;
    }).join('');

    el.innerHTML = html;
}

function rerenderSettingsTranslations() {
    renderFileList();
    renderAvtrdbReports();

    const selectBtn = document.getElementById('vrcxSelectBtn');
    if (selectBtn && selectBtn.style.display !== 'none') {
        selectBtn.innerHTML = vrcxSelectBtnHtml(selectBtn.disabled);
    }

    const startBtn = document.getElementById('vrcxStartBtn');
    const actionBtns = document.getElementById('vrcxActionBtns');
    if (startBtn && actionBtns && actionBtns.style.display !== 'none') {
        startBtn.innerHTML = vrcxStartBtnHtml(!!_vrcxLastError);
    }

    if (_vrcxPreviewData && document.getElementById('vrcxPreviewBox')?.style.display !== 'none') {
        renderVrcxPreviewRows(_vrcxPreviewData);
    }

    if (_vrcxLastProgress && document.getElementById('vrcxProgressWrap')?.style.display !== 'none') {
        _vrcxSetProgress(_vrcxLastProgress.percent ?? 0, _vrcxLastProgress.status ?? '');
    }

    if (_vrcxLastDone && document.getElementById('vrcxSuccessCard')?.style.display !== 'none') {
        renderVrcxSuccessDetail(_vrcxLastDone);
    }

    if (document.getElementById('vrcxImportError')?.style.display !== 'none') {
        renderVrcxError(_vrcxLastError);
    }
}

document.documentElement.addEventListener('languagechange', rerenderSettingsTranslations);

// === Database Optimization ===

function dbRunAnalysis() {
    const btn = document.getElementById('btnDbAnalyze');
    const optBtn = document.getElementById('btnDbOptimize');
    if (btn) btn.disabled = true;
    if (optBtn) { optBtn.style.display = 'none'; optBtn.disabled = false; }
    document.getElementById('dbAnalysisResults').style.display = 'none';
    const wrap = document.getElementById('dbOptProgressWrap');
    const bar  = document.getElementById('dbOptProgressBar');
    const lbl  = document.getElementById('dbOptProgressLabel');
    if (wrap) wrap.style.display = '';
    if (bar)  bar.style.width = '30%';
    if (lbl)  lbl.textContent = 'Analyzing…';
    sendToCS({ action: 'dbAnalyze' });
}

function dbMemoryUsage() {
    const btn = document.getElementById('btnDbMemory');
    if (btn) btn.disabled = true;
    const optBtn = document.getElementById('btnDbOptimize');
    if (optBtn) optBtn.style.display = 'none';
    document.getElementById('dbAnalysisResults').style.display = 'none';
    const wrap = document.getElementById('dbOptProgressWrap');
    const bar  = document.getElementById('dbOptProgressBar');
    const lbl  = document.getElementById('dbOptProgressLabel');
    if (wrap) wrap.style.display = '';
    if (bar)  bar.style.width = '40%';
    if (lbl)  lbl.textContent = t('settings.db.memory_calculating', 'Calculating table sizes…');
    sendToCS({ action: 'dbMemoryUsage' });
}

function _dbFmtBytes(bytes) {
    bytes = bytes || 0;
    if (bytes >= 1024 * 1024 * 1024) return (bytes / (1024 * 1024 * 1024)).toFixed(2) + ' GB';
    if (bytes >= 1024 * 1024)        return (bytes / (1024 * 1024)).toFixed(2) + ' MB';
    if (bytes >= 1024)               return (bytes / 1024).toFixed(1) + ' KB';
    return bytes + ' B';
}

function handleDbMemoryResult(data) {
    const wrap = document.getElementById('dbOptProgressWrap');
    const bar  = document.getElementById('dbOptProgressBar');
    const lbl  = document.getElementById('dbOptProgressLabel');
    const btn  = document.getElementById('btnDbMemory');
    const res  = document.getElementById('dbAnalysisResults');

    if (bar) bar.style.width = '100%';

    if (data.error) {
        if (lbl) lbl.textContent = 'Error: ' + data.error;
        if (btn) btn.disabled = false;
        return;
    }

    setTimeout(() => {
        if (wrap) wrap.style.display = 'none';
        if (bar)  bar.style.width = '0%';
        if (btn)  btn.disabled = false;

        const tables   = data.tables || [];
        const maxBytes = tables.reduce((m, x) => Math.max(m, x.bytes || 0), 0) || 1;
        const rowsHtml = tables.map(tb => {
            const pct = Math.max(2, Math.round((tb.bytes || 0) / maxBytes * 100));
            return `<div style="display:flex;align-items:center;gap:10px;padding:6px 0;border-bottom:1px solid var(--brd);">
                <div style="flex:1;min-width:0;">
                    <div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx0);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">${esc(tb.label)}</div>
                    <div style="height:4px;border-radius:2px;background:var(--bg-input);margin-top:4px;overflow:hidden;"><div style="height:100%;width:${pct}%;background:var(--accent);"></div></div>
                </div>
                <div style="text-align:right;flex-shrink:0;">
                    <div style="font-size:calc(13px + var(--fs-off, 0px));font-weight:700;color:var(--tx0);">${esc(_dbFmtBytes(tb.bytes))}</div>
                    <div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx3);">${(tb.rows || 0).toLocaleString(settingsUiLocale())} ${esc(t('settings.db.memory_rows', 'rows'))}</div>
                </div>
            </div>`;
        }).join('');

        const liveBytes = data.liveBytes || tables.reduce((s, x) => s + (x.bytes || 0), 0);
        const fileBytes = data.fileBytes || liveBytes;
        const freeBytes = data.freeBytes || 0;
        const overhead  = Math.max(0, fileBytes - freeBytes - liveBytes);

        const extraRow = (label, bytes, color) =>
            `<div style="display:flex;align-items:center;justify-content:space-between;gap:10px;padding:6px 0;border-bottom:1px solid var(--brd);">
                <div style="font-size:calc(12px + var(--fs-off, 0px));color:var(--tx2);">${esc(label)}</div>
                <div style="font-size:calc(13px + var(--fs-off, 0px));font-weight:700;color:${color};">${esc(_dbFmtBytes(bytes))}</div>
            </div>`;

        let extras = '';
        if (overhead  > 1024 * 1024) extras += extraRow(t('settings.db.memory_overhead', 'Indexes & overhead'), overhead, 'var(--tx2)');
        if (freeBytes > 0)           extras += extraRow(t('settings.db.memory_free', 'Free (reclaimable)'), freeBytes, 'var(--tx3)');

        res.innerHTML =
            `<div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:10px;">
                <div style="font-size:calc(12px + var(--fs-off, 0px));font-weight:700;color:var(--tx0);">${esc(t('settings.db.memory_total', 'Total database size'))}</div>
                <div style="font-size:calc(15px + var(--fs-off, 0px));font-weight:700;color:var(--accent);">${esc(_dbFmtBytes(fileBytes))}</div>
            </div>${rowsHtml}${extras}`;
        res.style.display = '';
    }, 200);
}

function dbRunOptimize() {
    const btn = document.getElementById('btnDbOptimize');
    if (btn) btn.disabled = true;
    document.getElementById('btnDbAnalyze').disabled = true;
    document.getElementById('dbAnalysisResults').style.display = 'none';
    const wrap = document.getElementById('dbOptProgressWrap');
    const bar  = document.getElementById('dbOptProgressBar');
    const lbl  = document.getElementById('dbOptProgressLabel');
    if (wrap) wrap.style.display = '';
    if (bar)  bar.style.width = '10%';
    if (lbl)  lbl.textContent = 'Clearing cache data…';
    sendToCS({ action: 'dbOptimize' });
}

function handleDbAnalyzeProgress() {
    const bar = document.getElementById('dbOptProgressBar');
    const lbl = document.getElementById('dbOptProgressLabel');
    if (bar) bar.style.width = '60%';
    if (lbl) lbl.textContent = 'Analyzing…';
}

function handleDbAnalyzeResult(data) {
    const wrap = document.getElementById('dbOptProgressWrap');
    const bar  = document.getElementById('dbOptProgressBar');
    const lbl  = document.getElementById('dbOptProgressLabel');
    const btn  = document.getElementById('btnDbAnalyze');
    const res  = document.getElementById('dbAnalysisResults');

    if (bar) bar.style.width = '100%';

    if (data.error) {
        if (lbl) lbl.textContent = 'Error: ' + data.error;
        if (btn) btn.disabled = false;
        return;
    }

    setTimeout(() => {
        if (wrap) wrap.style.display = 'none';
        if (bar)  bar.style.width = '0%';
        if (btn)  btn.disabled = false;

        // Summary row
        const total    = (data.totalRows     || 0).toLocaleString(settingsUiLocale());
        const friends  = (data.friendRows    || 0).toLocaleString(settingsUiLocale());
        const clean    = (data.cleanableRows || 0).toLocaleString(settingsUiLocale());

        const counts = data.counts || [];
        const gridItems = counts.map(c =>
            `<div style="background:var(--bg-input);border-radius:8px;padding:8px 10px;min-width:0;">
                <div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx3);margin-bottom:2px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">${esc(c.label)}</div>
                <div style="font-size:calc(14px + var(--fs-off, 0px));font-weight:700;color:var(--tx0);">${(c.count||0).toLocaleString(settingsUiLocale())}</div>
            </div>`
        ).join('');

        const fOnline     = (data.friendOnlineCount     || 0).toLocaleString(settingsUiLocale());
        const fOffline    = (data.friendOfflineCount    || 0).toLocaleString(settingsUiLocale());
        const fStatus     = (data.friendStatusCount     || 0).toLocaleString(settingsUiLocale());
        const fStatusDesc = (data.friendStatusDescCount || 0).toLocaleString(settingsUiLocale());
        const fBio        = (data.friendBioCount        || 0).toLocaleString(settingsUiLocale());
        const fAvatar     = (data.friendAvatarCount     || 0).toLocaleString(settingsUiLocale());
        const fTotal      = ((data.friendOnlineCount || 0) + (data.friendOfflineCount || 0) + (data.friendStatusCount || 0) + (data.friendStatusDescCount || 0) + (data.friendBioCount || 0) + (data.friendAvatarCount || 0)).toLocaleString(settingsUiLocale());
        const eNotif    = (data.notificationCount    || 0).toLocaleString(settingsUiLocale());
        const eVideo    = (data.videoUrlCount        || 0).toLocaleString(settingsUiLocale());
        const eAvatar   = (data.avatarSwitchCount    || 0).toLocaleString(settingsUiLocale());
        const epPlayers = (data.instancePlayersCount || 0).toLocaleString(settingsUiLocale());

        res.innerHTML =
            `<div style="display:flex;gap:8px;margin-bottom:12px;flex-wrap:wrap;">
                <div style="background:var(--bg-input);border-radius:8px;padding:8px 12px;flex:1;min-width:120px;">
                    <div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx3);margin-bottom:2px;">Total Users</div>
                    <div style="font-size:calc(15px + var(--fs-off, 0px));font-weight:700;color:var(--tx0);">${total}</div>
                </div>
                <div style="background:var(--bg-input);border-radius:8px;padding:8px 12px;flex:1;min-width:120px;">
                    <div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx3);margin-bottom:2px;">Friends (skipped)</div>
                    <div style="font-size:calc(15px + var(--fs-off, 0px));font-weight:700;color:var(--tx0);">${friends}</div>
                </div>
                <div style="background:var(--bg-input);border-radius:8px;padding:8px 12px;flex:1;min-width:120px;">
                    <div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx3);margin-bottom:2px;">Cleanable Rows</div>
                    <div style="font-size:calc(15px + var(--fs-off, 0px));font-weight:700;color:var(--accent);">${clean}</div>
                </div>
            </div>
            <div style="display:grid;grid-template-columns:repeat(6,1fr);gap:6px;margin-bottom:12px;">
                ${gridItems}
            </div>
            <div style="font-size:calc(11px + var(--fs-off, 0px));font-weight:600;color:var(--tx3);margin-bottom:6px;margin-top:4px;text-transform:uppercase;letter-spacing:.05em;">events</div>
            <div style="display:grid;grid-template-columns:repeat(6,1fr);gap:6px;margin-bottom:12px;">
                <div style="background:var(--bg-input);border-radius:8px;padding:8px 10px;min-width:0;">
                    <div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx3);margin-bottom:2px;">Notifications</div>
                    <div style="font-size:calc(14px + var(--fs-off, 0px));font-weight:700;color:var(--tx0);">${eNotif}</div>
                </div>
                <div style="background:var(--bg-input);border-radius:8px;padding:8px 10px;min-width:0;">
                    <div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx3);margin-bottom:2px;">Video URL</div>
                    <div style="font-size:calc(14px + var(--fs-off, 0px));font-weight:700;color:var(--tx0);">${eVideo}</div>
                </div>
                <div style="background:var(--bg-input);border-radius:8px;padding:8px 10px;min-width:0;">
                    <div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx3);margin-bottom:2px;">Avatars</div>
                    <div style="font-size:calc(14px + var(--fs-off, 0px));font-weight:700;color:var(--tx0);">${eAvatar}</div>
                </div>
            </div>
            <div style="font-size:calc(11px + var(--fs-off, 0px));font-weight:600;color:var(--tx3);margin-bottom:6px;margin-top:4px;text-transform:uppercase;letter-spacing:.05em;">friend_events</div>
            <div style="display:grid;grid-template-columns:repeat(6,1fr);gap:6px;margin-bottom:12px;">
                <div style="background:var(--bg-input);border-radius:8px;padding:8px 10px;min-width:0;">
                    <div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx3);margin-bottom:2px;">Friend Online</div>
                    <div style="font-size:calc(14px + var(--fs-off, 0px));font-weight:700;color:var(--tx0);">${fOnline}</div>
                </div>
                <div style="background:var(--bg-input);border-radius:8px;padding:8px 10px;min-width:0;">
                    <div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx3);margin-bottom:2px;">Friend Offline</div>
                    <div style="font-size:calc(14px + var(--fs-off, 0px));font-weight:700;color:var(--tx0);">${fOffline}</div>
                </div>
                <div style="background:var(--bg-input);border-radius:8px;padding:8px 10px;min-width:0;">
                    <div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx3);margin-bottom:2px;">Friend Status</div>
                    <div style="font-size:calc(14px + var(--fs-off, 0px));font-weight:700;color:var(--tx0);">${fStatus}</div>
                </div>
                <div style="background:var(--bg-input);border-radius:8px;padding:8px 10px;min-width:0;">
                    <div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx3);margin-bottom:2px;">Friend Status Text</div>
                    <div style="font-size:calc(14px + var(--fs-off, 0px));font-weight:700;color:var(--tx0);">${fStatusDesc}</div>
                </div>
                <div style="background:var(--bg-input);border-radius:8px;padding:8px 10px;min-width:0;">
                    <div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx3);margin-bottom:2px;">Friend Bio</div>
                    <div style="font-size:calc(14px + var(--fs-off, 0px));font-weight:700;color:var(--tx0);">${fBio}</div>
                </div>
                <div style="background:var(--bg-input);border-radius:8px;padding:8px 10px;min-width:0;">
                    <div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx3);margin-bottom:2px;">Friend Avatar</div>
                    <div style="font-size:calc(14px + var(--fs-off, 0px));font-weight:700;color:var(--tx0);">${fAvatar}</div>
                </div>
                <div style="background:var(--bg-input);border-radius:8px;padding:8px 10px;min-width:0;grid-column:span 6;">
                    <div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx3);margin-bottom:2px;">Total Deletable Rows</div>
                    <div style="font-size:calc(14px + var(--fs-off, 0px));font-weight:700;color:var(--accent);">${fTotal}</div>
                </div>
            </div>
            <div style="font-size:calc(11px + var(--fs-off, 0px));font-weight:600;color:var(--tx3);margin-bottom:6px;margin-top:4px;text-transform:uppercase;letter-spacing:.05em;">event_players</div>
            <div style="display:grid;grid-template-columns:repeat(6,1fr);gap:6px;margin-bottom:12px;">
                <div style="background:var(--bg-input);border-radius:8px;padding:8px 10px;min-width:0;">
                    <div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx3);margin-bottom:2px;">Instance Players</div>
                    <div style="font-size:calc(14px + var(--fs-off, 0px));font-weight:700;color:var(--tx0);">${epPlayers}</div>
                </div>
                <div style="background:var(--bg-input);border-radius:8px;padding:8px 10px;min-width:0;grid-column:span 5;">
                    <div style="font-size:calc(10px + var(--fs-off, 0px));color:var(--tx3);margin-bottom:2px;">Total Deletable Rows</div>
                    <div style="font-size:calc(14px + var(--fs-off, 0px));font-weight:700;color:var(--accent);">${epPlayers}</div>
                </div>
            </div>`;

        res.style.display = '';

        const optBtn = document.getElementById('btnDbOptimize');
        if (optBtn) optBtn.style.display = '';
    }, 300);
}

function handleDbOptimizeProgress(data) {
    const bar = document.getElementById('dbOptProgressBar');
    const lbl = document.getElementById('dbOptProgressLabel');
    if (data.phase === 'optimize') {
        if (bar) bar.style.width = '40%';
        if (lbl) lbl.textContent = 'Clearing cache data…';
    } else if (data.phase === 'vacuum') {
        if (bar) bar.style.width = '75%';
        if (lbl) lbl.textContent = 'Running VACUUM…';
    }
}

function handleDbOptimizeDone(data) {
    const wrap = document.getElementById('dbOptProgressWrap');
    const bar  = document.getElementById('dbOptProgressBar');
    const lbl  = document.getElementById('dbOptProgressLabel');
    const btn  = document.getElementById('btnDbAnalyze');
    const optBtn = document.getElementById('btnDbOptimize');
    const res  = document.getElementById('dbAnalysisResults');

    if (bar) bar.style.width = '100%';

    setTimeout(() => {
        if (wrap)   wrap.style.display = 'none';
        if (bar)    bar.style.width = '0%';
        if (btn)    btn.disabled = false;
        if (optBtn) { optBtn.style.display = 'none'; optBtn.disabled = false; }

        if (data.error) {
            if (res) { res.innerHTML = `<div style="color:var(--err);font-size:calc(12px + var(--fs-off, 0px));">Error: ${esc(data.error)}</div>`; res.style.display = ''; }
            return;
        }

        const userCleaned  = (data.userCleaned  || 0).toLocaleString(settingsUiLocale());
        const feCleaned    = (data.feCleaned    || 0).toLocaleString(settingsUiLocale());
        const notifCleaned = (data.notifCleaned || 0).toLocaleString(settingsUiLocale());
        const epCleaned    = (data.epCleaned    || 0).toLocaleString(settingsUiLocale());
        if (res) {
            res.innerHTML =
                `<div style="display:flex;align-items:center;gap:10px;padding:10px 12px;background:rgba(var(--ok-rgb,80,200,120),.12);border:1px solid rgba(var(--ok-rgb,80,200,120),.30);border-radius:8px;">
                    <span class="msi" style="font-size:20px;color:var(--ok);">check_circle</span>
                    <div>
                        <div style="font-size:calc(13px + var(--fs-off, 0px));font-weight:600;color:var(--tx1);">Optimization complete</div>
                        <div style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);margin-top:2px;">${userCleaned} user cache rows cleared &nbsp;·&nbsp; ${feCleaned} friend events deleted &nbsp;·&nbsp; ${notifCleaned} notifications deleted &nbsp;·&nbsp; ${epCleaned} instance player rows deleted &nbsp;·&nbsp; VACUUM done</div>
                    </div>
                </div>`;
            res.style.display = '';
        }
    }, 300);
}

function dbCreateBackup() {
    const btn = document.getElementById('btnDbBackup');
    if (btn) { btn.disabled = true; btn.textContent = 'Creating backup…'; }
    sendToCS({ action: 'dbBackup' });
}

function manualDbBackup() {
    const btn = document.getElementById('btnManualDbBackup');
    if (btn) btn.disabled = true;
    sendToCS({ action: 'dbBackup' });
}

function manualRegBackup() {
    const btn = document.getElementById('btnManualRegBackup');
    if (btn) btn.disabled = true;
    sendToCS({ action: 'regBackup' });
}

function _showBackupResult(containerId, label, data) {
    const res = document.getElementById(containerId);
    if (!res) return;
    if (data.error) {
        res.innerHTML = `<div style="color:var(--err);font-size:calc(12px + var(--fs-off, 0px));">${esc(label)} failed: ${esc(data.error)}</div>`;
    } else {
        res.innerHTML =
            `<div style="display:flex;align-items:center;gap:10px;padding:10px 12px;background:rgba(var(--ok-rgb,80,200,120),.12);border:1px solid rgba(var(--ok-rgb,80,200,120),.30);border-radius:8px;">
                <span class="msi" style="font-size:20px;color:var(--ok);">check_circle</span>
                <div>
                    <div style="font-size:calc(13px + var(--fs-off, 0px));font-weight:600;color:var(--tx1);">${esc(label)}</div>
                    <div style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);margin-top:2px;font-family:monospace;">${esc(data.path ?? '')}</div>
                </div>
            </div>`;
    }
    res.style.display = '';
}

function handleRegBackupDone(data) {
    const btn = document.getElementById('btnManualRegBackup');
    if (btn) btn.disabled = false;
    _showBackupResult('manualBackupResult', 'Registry backup created', data);
}

function handleDbBackupDone(data) {
    const btn = document.getElementById('btnDbBackup');
    if (btn) {
        btn.disabled = false;
        btn.innerHTML = '<span class="msi" style="font-size:16px;">backup</span> Create Backup';
    }
    const btn2 = document.getElementById('btnManualDbBackup');
    if (btn2) btn2.disabled = false;

    // DB Optimization card result — replace any existing backup result (max 1)
    const res = document.getElementById('dbAnalysisResults');
    if (res) {
        res.querySelectorAll('[data-backup-result]').forEach(el => el.remove());
        const el = document.createElement('div');
        el.dataset.backupResult = '1';
        if (data.error) {
            el.style.cssText = 'color:var(--err);font-size:calc(12px + var(--fs-off, 0px));margin-top:8px;';
            el.textContent = 'Backup failed: ' + data.error;
        } else {
            el.style.cssText = 'display:flex;align-items:center;gap:10px;padding:10px 12px;background:rgba(var(--ok-rgb,80,200,120),.12);border:1px solid rgba(var(--ok-rgb,80,200,120),.30);border-radius:8px;margin-top:8px;';
            el.innerHTML =
                `<span class="msi" style="font-size:20px;color:var(--ok);">check_circle</span>
                <div>
                    <div style="font-size:calc(13px + var(--fs-off, 0px));font-weight:600;color:var(--tx1);">Backup created</div>
                    <div style="font-size:calc(11px + var(--fs-off, 0px));color:var(--tx3);margin-top:2px;font-family:monospace;">${esc(data.path)}</div>
                </div>`;
        }
        res.prepend(el);
        res.style.display = '';
    }

    // Auto-Backups card result
    _showBackupResult('manualBackupResult', 'Database backup created', data);
}

/* === Settings Search === */
let _settingsSearchActive = false;

const _SETTINGS_SKIP_TEXT = ['set-desc', 'sf-desc', 'setting-desc'];

function _settingsCardHaystacks(el) {
    let labels = '', all = '';
    const walk = (node, inDesc) => {
        for (const child of node.childNodes) {
            if (child.nodeType === 3) {
                all += ' ' + child.nodeValue;
                if (!inDesc) labels += ' ' + child.nodeValue;
                continue;
            }
            if (child.nodeType !== 1) continue;
            const desc = inDesc || _SETTINGS_SKIP_TEXT.some(c => child.classList.contains(c));
            for (const attr of ['placeholder', 'title']) {
                const v = child.getAttribute(attr);
                if (!v) continue;
                all += ' ' + v;
                if (!desc) labels += ' ' + v;
            }
            walk(child, desc);
        }
    };
    walk(el, false);
    const norm = s => s.toLowerCase().replace(/\s+/g, ' ');
    return { labels: norm(labels), all: norm(all) };
}

function _settingsMatches(hay, terms) {
    return terms.every(term => {
        let i = hay.indexOf(term);
        while (i >= 0) {
            if (i === 0 || !/[a-z0-9]/.test(hay[i - 1])) return true;
            i = hay.indexOf(term, i + 1);
        }
        return false;
    });
}

function _settingsActiveSectionId() {
    const btn = document.querySelector('#tab9 .settings-nav-item.active');
    const m = btn && (btn.getAttribute('onclick') || '').match(/switchSettingsSection\('([^']+)'/);
    return m ? m[1] : 'general';
}

function _settingsEmptyEl() {
    let el = document.getElementById('settingsSearchEmpty');
    if (el) return el;
    const content = document.querySelector('#tab9 .settings-content');
    if (!content) return null;
    el = document.createElement('div');
    el.id = 'settingsSearchEmpty';
    el.className = 'settings-search-empty';
    el.style.display = 'none';
    content.appendChild(el);
    return el;
}

function searchSettings() {
    const wrap = document.getElementById('settingsSearchWrap');
    const q = (document.getElementById('settingsSearchInput')?.value || '').toLowerCase().trim();
    if (wrap) wrap.classList.toggle('has-query', !!q);
    const empty = _settingsEmptyEl();

    if (!q) {
        if (!_settingsSearchActive) return;
        _settingsSearchActive = false;
        if (empty) empty.style.display = 'none';
        switchSettingsSection(_settingsActiveSectionId(), null);
        return;
    }

    _settingsSearchActive = true;
    const isLinux = !!window._isLinuxUi;
    const terms = q.split(/\s+/).filter(Boolean);

    const cards = [];
    document.querySelectorAll('#tab9 [data-section]').forEach(el => {
        if (isLinux && el.hasAttribute('data-windows-only')) { el.style.display = 'none'; return; }
        const hay = _settingsCardHaystacks(el);
        cards.push({ el, label: _settingsMatches(hay.labels, terms), all: _settingsMatches(hay.all, terms) });
    });

    const anyLabelHit = cards.some(c => c.label);
    let hits = 0;
    cards.forEach(c => {
        const match = anyLabelHit ? c.label : c.all;
        c.el.style.display = match ? '' : 'none';
        if (match) hits++;
    });
    if (empty) {
        empty.textContent = tf('settings.search.no_results', { query: q }, 'No settings match "{query}"');
        empty.style.display = hits ? 'none' : '';
    }
}

window._dbSettingsSearch = debounceAnim(searchSettings, 150, 'settingsSearchInput');

function clearSettingsSearch() {
    const input = document.getElementById('settingsSearchInput');
    if (input) input.value = '';
    searchSettings();
    input?.focus();
}

document.documentElement.addEventListener('languagechange', () => {
    if (_settingsSearchActive) searchSettings();
});

function switchSettingsSection(id, btn) {
    if (btn && _settingsSearchActive) {
        const input = document.getElementById('settingsSearchInput');
        if (input) input.value = '';
        document.getElementById('settingsSearchWrap')?.classList.remove('has-query');
        const empty = document.getElementById('settingsSearchEmpty');
        if (empty) empty.style.display = 'none';
        _settingsSearchActive = false;
    }
    const _swLinux = !!window._isLinuxUi;
    document.querySelectorAll('#tab9 [data-section]').forEach(el => {
        const show = el.dataset.section === id
            && !(_swLinux && el.hasAttribute('data-windows-only'))
            && !(!_swLinux && el.hasAttribute('data-linux-only'));
        el.style.display = show ? '' : 'none';
    });
    document.querySelectorAll('#tab9 .settings-nav-item').forEach(b => b.classList.remove('active'));
    if (btn) {
        btn.classList.add('active');
    } else {
        const match = document.querySelector(`#tab9 .settings-nav-item[onclick*="'${id}'"]`);
        if (match) match.classList.add('active');
    }
    // Refresh the accounts list whenever the Accounts tab is opened to avoid startup race conditions.
    if (id === 'accounts' && typeof requestAccountsList === 'function') requestAccountsList();
}

function _fotUpdateFavOnly() {
    const enabled = document.getElementById('setFriendOnlineToastEnabled')?.checked ?? false;
    const row = document.getElementById('fotFavRow');
    if (row) row.classList.toggle('disabled', !enabled);
}


document.documentElement.addEventListener('languagechange', sndPopulateSelects);
