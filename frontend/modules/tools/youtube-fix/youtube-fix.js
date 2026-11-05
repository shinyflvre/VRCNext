// YouTube Fix module helpers.
// State updates and backend actions remain centralized in core.js.

(function () {
    function updatePlatformUi() {
        const linux = document.documentElement.classList.contains('vrcn-linux');
        const steamOption = document.getElementById('ytSteamLaunchOption');
        if (steamOption) steamOption.style.display = linux ? 'flex' : 'none';
    }

    function persistLaunchSettings() {
        if (typeof autoSave === 'function') autoSave();
    }

    window.initYoutubeFix = function () {
        updatePlatformUi();
        const steamOption = document.getElementById('setYtAutoStartSteam');
        if (steamOption) steamOption.addEventListener('change', persistLaunchSettings);
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', window.initYoutubeFix);
    } else {
        window.initYoutubeFix();
    }
})();
