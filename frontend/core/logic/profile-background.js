/* === VRC+ Profile Backgrounds === */

const PROFILE_BG_ASSET_URL = 'https://assets.vrchat.com/www/profile_decorations/profile_backgrounds/';

const PROFILE_BG_FILES = {
    'grid':             'BG_Grid.png',
    'filigree':         'BG_Cascade.png',
    'bit-mountain':     'BG_GRID_BITS.png',
    'approach':         'BG_Approach.png',
    'planet-fall':      'BG_PlanetFall.png',
    'jungle':           'BG_Jungle.png',
    'light-streams':    'BG_LightStream.png',
    'koi':              'Koi_Layer.png',
    'moonlight':        'BG_Cloud.png',
    'machine':          'TechWallLayer.png',
    'topology':         'TOPOLOGY.png',
    'circuit':          'BG_Circuit.png',
    'kawaii-cupcakes':  'BG_Kawaii_cupcakes.png',
    'bokeh':            'BG_Bokeh.png',
    'hologram':         'BG_Shatter.png',
    'flow':             'BG_Flow_Horizontal.png',
    'jelly':            'BG_Jelly.png',
    'cheese':           'BG_Cheese.png',
    'alchemy':          'BG_Alchemy.png',
    'sunset-mountain':  'BG_MountainSun.png',
    'magic-rocks':      'BG_MagicRocks.png',
    'city-stillness':   'BG_FutureCityStillness.png',
    'music':            'BG_Music.png',
    'wolf-running':     'BG_WolfRunning.png',
    'dragon-smoke':     'BG_DragonSmoke.png',
    'night-rain':       'BG_NightRain.png',
    'butterfly-zero':   'BG_ButterFlyZERO.png',
    'i-am-speed':       'void.png',
    'ronin':            'BG_Ronin.png',
    'abduction':        'BG_Abduction.png',
    'cat-dream':        'BG_CatDream.png',
    'virtual-waves':    'BG_VirtualWaves.png',
    'bolt-and-snail':   'BG_BoltNSnail.png',
    'escape':           'BG_Escape.png',
};

function profileBgEnabled() {
    return !!(typeof settings !== 'undefined' && settings.enableProfileBackgrounds);
}

function _profileBgColor(value) {
    const c = String(value || '').trim().replace(/^#/, '');
    return /^[0-9a-f]{6}$/i.test(c) ? '#' + c.toLowerCase() : '';
}

// The backend caches the texture into Caches/ImageCache/VRCPlus like the other VRC+
// decorations and hands back a local URL. The CDN fallback only covers the very first
// load, before the download finished.
function profileBgTextureUrl(user) {
    const cached = String(user?.backgroundTextureUrl || '').trim();
    if (cached) return cached;
    const file = PROFILE_BG_FILES[String(user?.backgroundTextureId || '').trim()];
    return file ? PROFILE_BG_ASSET_URL + file : '';
}

// Returns a CSS value for `background`, or '' when the user has none or the setting
// is off. `scrim` is layered on top so text stays readable over busy textures.
function profileBgCss(user, scrim = 'rgba(15,15,15,.62)', force) {
    if ((!force && !profileBgEnabled()) || !user) return '';

    const type = String(user.backgroundType || '').trim();

    if (type === 'gradient') {
        const top = _profileBgColor(user.backgroundGradientTop);
        const bot = _profileBgColor(user.backgroundGradientBottom);
        if (top && bot) return `linear-gradient(180deg, ${top}, ${bot})`;
        return '';
    }

    if (type === 'texture') {
        const url = profileBgTextureUrl(user);
        if (url) return `linear-gradient(${scrim}, ${scrim}), url("${cssUrl(url)}") top center / cover no-repeat`;
    }

    return '';
}

// Applies (or clears) the background on an element. Kept as one helper so the modal
// and the hover preview cannot drift apart.
function applyProfileBg(el, user, scrim, force) {
    if (!el) return false;
    const css = profileBgCss(user, scrim, force);
    el.style.background = css || '';
    el.classList.toggle('has-profile-bg', !!css);
    return !!css;
}

window.profileBgEnabled    = profileBgEnabled;
window.profileBgCss        = profileBgCss;
window.profileBgTextureUrl = profileBgTextureUrl;
window.applyProfileBg      = applyProfileBg;
