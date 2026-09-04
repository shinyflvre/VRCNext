/* === VRC+ Profile Themes === */

const PROFILE_THEME_DEFAULTS = { button: '064b5c', icon: '6ae3f9', subtext: 'a9a9a9' };

function profileThemeEnabled() {
    return !!(typeof settings !== 'undefined' && settings.enableProfileThemes);
}

function ptHex(value, fallback = '') {
    const c = String(value || '').trim().replace(/^#/, '').toLowerCase();
    return /^[0-9a-f]{6}$/.test(c) ? '#' + c : fallback;
}

function _ptShade(hex, factor) {
    const c = ptHex(hex);
    if (!c) return '';
    const n = parseInt(c.slice(1), 16);
    const r = Math.round(((n >> 16) & 255) * factor);
    const g = Math.round(((n >> 8) & 255) * factor);
    const b = Math.round((n & 255) * factor);
    return '#' + [r, g, b].map(v => Math.max(0, Math.min(255, v)).toString(16).padStart(2, '0')).join('');
}

function profileThemeContrastEnabled() {
    return !(typeof settings !== 'undefined' && settings.profileThemeContrast === false);
}

function _ptToHex(value) {
    const s = String(value || '').trim();
    if (/^#[0-9a-f]{6}$/i.test(s)) return s.toLowerCase();
    if (/^#[0-9a-f]{3}$/i.test(s)) return '#' + s.slice(1).split('').map(c => c + c).join('').toLowerCase();
    const m = s.match(/^rgba?\(([^)]+)\)$/i);
    if (m) {
        const p = m[1].split(/[\s,/]+/).filter(Boolean).map(Number);
        if (p.length >= 3 && p.slice(0, 3).every(n => isFinite(n))) {
            return '#' + p.slice(0, 3).map(n => Math.max(0, Math.min(255, Math.round(n))).toString(16).padStart(2, '0')).join('');
        }
    }
    return '';
}

function _ptRgb(hex) {
    const n = parseInt(hex.slice(1), 16);
    return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

function _ptLuminance(hex) {
    return _ptRgb(hex).map(v => {
        const s = v / 255;
        return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
    }).reduce((acc, v, i) => acc + v * [0.2126, 0.7152, 0.0722][i], 0);
}

function _ptContrast(a, b) {
    const l1 = _ptLuminance(a), l2 = _ptLuminance(b);
    return (Math.max(l1, l2) + 0.05) / (Math.min(l1, l2) + 0.05);
}

function _ptBlend(hex, target, amount) {
    const a = _ptRgb(hex), b = _ptRgb(target);
    return '#' + a.map((v, i) => Math.round(v + (b[i] - v) * amount).toString(16).padStart(2, '0')).join('');
}

function _ptReadable(color, bg, minRatio) {
    const c = ptHex(color);
    const b = ptHex(bg);
    if (!c || !b || !profileThemeContrastEnabled()) return c || color;
    if (_ptContrast(c, b) >= minRatio) return c;

    const target = _ptLuminance(b) > 0.22 ? '#000000' : '#ffffff';
    for (let i = 1; i <= 20; i++) {
        const mixed = _ptBlend(c, target, i / 20);
        if (_ptContrast(mixed, b) >= minRatio) return mixed;
    }
    return target;
}

function _ptSurface(el, buttonColor) {
    if (buttonColor) return _ptShade(buttonColor, 0.75);
    try {
        const hex = _ptToHex(getComputedStyle(el).getPropertyValue('--bg-card'));
        if (hex) return hex;
    } catch {}
    return '#0f0f0f';
}

function profileThemeColors(user, force) {
    if ((!force && !profileThemeEnabled()) || !user) return null;

    let button  = ptHex(user.themeButtonColor);
    let icon    = ptHex(user.themeIconColor);
    let subtext = ptHex(user.themeSubtextColor);

    if (!button && !icon && !subtext) {
        const th = Array.isArray(user.themes) && user.themeId
            ? user.themes.find(x => x && x.id === user.themeId) : null;
        if (th) {
            button  = ptHex(th.buttonColor);
            icon    = ptHex(th.iconColor);
            subtext = ptHex(th.subtextColor);
        }
    }

    if (!button && !icon && !subtext
        && typeof currentVrcUser !== 'undefined' && currentVrcUser
        && user.id && user.id === currentVrcUser.id) {
        button  = ptHex(currentVrcUser.themeButtonColor);
        icon    = ptHex(currentVrcUser.themeIconColor);
        subtext = ptHex(currentVrcUser.themeSubtextColor);
    }

    if (!button && !icon && !subtext) return null;
    return { button, icon, subtext };
}

const PT_VARS = ['--pt-accent', '--pt-accent-lt', '--pt-bg-card', '--pt-bg-hover',
    '--pt-bg-input', '--pt-bg-btn', '--pt-bg-btn-h', '--pt-brd', '--pt-tx2', '--pt-tx3', '--pt-icon', '--pt-icon-fg'];

function _ptPaint(el, c) {
    if (c.button) {
        el.style.setProperty('--pt-accent', c.button);
        el.style.setProperty('--pt-accent-lt', _ptShade(c.button, 1.25));
        el.style.setProperty('--pt-bg-card', _ptShade(c.button, 0.75));
        el.style.setProperty('--pt-bg-hover', _ptShade(c.button, 0.9));
        el.style.setProperty('--pt-bg-input', _ptShade(c.button, 0.65));
        el.style.setProperty('--pt-bg-btn', _ptShade(c.button, 0.85));
        el.style.setProperty('--pt-bg-btn-h', c.button);
        el.style.setProperty('--pt-brd', _ptShade(_ptShade(c.button, 0.75), 1.10));
    }
    const surface = _ptSurface(el, c.button);
    if (c.subtext) {
        const readable = _ptReadable(c.subtext, surface, 4.5);
        el.style.setProperty('--pt-tx2', readable);
        el.style.setProperty('--pt-tx3', readable);
    }
    if (c.icon) {
        const iconColor = _ptReadable(c.icon, surface, 3);
        el.style.setProperty('--pt-icon', iconColor);
        el.style.setProperty('--pt-icon-fg', _ptLuminance(iconColor) > 0.4 ? '#111111' : '#ffffff');
    }
    el.classList.add('has-profile-theme');
    el.classList.toggle('pt-has-button', !!c.button);
    el.classList.toggle('pt-has-text', !!c.subtext);
}

function applyProfileTheme(el, user, force) {
    if (!el) return false;

    for (const k of PT_VARS) el.style.removeProperty(k);
    el.classList.remove('has-profile-theme', 'pt-has-button', 'pt-has-text');

    const c = profileThemeColors(user, force);
    if (!c) return false;

    _ptPaint(el, c);
    return true;
}

function profileThemeStripes(theme) {
    const parts = [
        ptHex(theme?.buttonColor,  '#' + PROFILE_THEME_DEFAULTS.button),
        ptHex(theme?.iconColor,    '#' + PROFILE_THEME_DEFAULTS.icon),
        ptHex(theme?.subtextColor, '#' + PROFILE_THEME_DEFAULTS.subtext),
    ];
    return `<span class="pt-stripes">${parts.map(c => `<i style="background:${c}"></i>`).join('')}</span>`;
}

window.profileThemeEnabled  = profileThemeEnabled;
window.profileThemeContrastEnabled = profileThemeContrastEnabled;
window.profileThemeColors   = profileThemeColors;
window.applyProfileTheme    = applyProfileTheme;
window.profileThemeStripes  = profileThemeStripes;
window.ptHex                = ptHex;
