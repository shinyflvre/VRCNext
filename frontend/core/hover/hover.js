(function () {
    const tip = document.createElement('div');
    tip.id = 'vnTip';
    tip.className = 'vn-tooltip';
    document.body.appendChild(tip);

    let _active = false;

    function show(text, rect, placement) {
        tip.textContent = '';
        const inner = document.createElement('span');
        inner.className = 'vn-tooltip-inner';
        inner.textContent = text;
        tip.appendChild(inner);
        tip.classList.add('vn-tooltip-visible');
        _active = true;
        position(rect, placement);
    }

    function hide() {
        tip.classList.remove('vn-tooltip-visible');
        _active = false;
    }

    function position(rect, placement) {
        tip.style.left = '-9999px';
        tip.style.top  = '-9999px';
        requestAnimationFrame(() => {
            const tw = tip.offsetWidth;
            const th = tip.offsetHeight;
            const rLeft   = rect.left;
            const rTop    = rect.top;
            const rWidth  = rect.width;
            const rHeight = rect.height;
            const rRight  = rect.right;
            const rBottom = rect.bottom;
            const vw = window.innerWidth;
            const vh = window.innerHeight;
            let left, top;
            if (placement === 'right') {
                left = rRight + 7;
                top  = rTop + rHeight / 2 - th / 2;
                if (left + tw > vw - 8) left = rLeft - tw - 7;
                top  = Math.max(8, Math.min(top, vh - th - 8));
                left = Math.max(8, Math.min(left, vw - tw - 8));
            } else {
                left = rLeft + rWidth / 2 - tw / 2;
                top  = rBottom + 7;
                if (top + th > vh - 8) top = rTop - th - 7;
                left = Math.max(8, Math.min(left, vw - tw - 8));
            }
            tip.style.left = left + 'px';
            tip.style.top  = top + 'px';
        });
    }

    function navHoverEnabled() {
        return (typeof settings === 'undefined') || settings.navSidebarHoverText !== false;
    }

    document.addEventListener('mouseover', e => {
        const el = e.target.closest('[data-tooltip], [title]');
        if (!el) {
            const navBtn = e.target.closest('#navEl .nav-btn');
            if (navBtn && navHoverEnabled() && typeof sidebarCollapsed !== 'undefined' && sidebarCollapsed) {
                const label = navBtn.querySelector('.nl')?.textContent?.trim();
                if (label) { show(label, navBtn.getBoundingClientRect(), 'right'); return; }
            }
            if (_active) hide();
            return;
        }

        let text = el.dataset.tooltip;

        if (!text) {
            text = el.getAttribute('title');
            if (!text) return;
            // Remove title so the native browser tooltip never appears.
            // Store it for restoration on mouseleave.
            el._vnTitle = text;
            el.removeAttribute('title');
            el.dataset.tooltip = text;
        }

        show(text, el.getBoundingClientRect());
    });

    document.addEventListener('mouseleave', e => {
        const el = e.target;
        if (el._vnTitle) {
            el.setAttribute('title', el._vnTitle);
            delete el.dataset.tooltip;
            delete el._vnTitle;
        }
        hide();
    }, true);

    window.vnTooltip = { show, hide };
})();
