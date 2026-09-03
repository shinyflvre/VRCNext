**2026.48.3**

**Improvements**
- **Create Instance**: group instances can now set a **Minimum Avatar Performance** limit.
- Profile status heatmap is more accurate — statuses are recorded live and backfilled on startup.

**Changes**
- Softer hover colors for several themes, so hover no longer flashes the full accent color.
- **VRCN** theme refreshed (v3): darker cards and inputs, calmer accent, dedicated button/active/badge colors.
- Two new theme colors: **Badge Base** and **UI Input Active BG** (both default to current values, so nothing looks different).
- **Create Instance** modal updated to the v2 vrcn design.
- Calendar **Help Sort** now uses soft candy colors, stable per group.
- Stats are no longer bold.
- Clearer placeholder text on empty tab pages.

**Removed**
- Removed the VRCX, Slates, Rose, Unicorn, Baby and Flipper Zero themes. Active ones fall back to VRCN.

**Fixed Bugs**
- Lots of hardcoded colors replaced with theme colors: controller illustrations, VR Overlay keybind card/dropdowns, profile "Most Visited Worlds" and "Interacted the most with" cards, and hover highlights across taskbar menus, dropdowns, Pins, Smart Search, sidebars and the navigation editor.
- Heatmap no longer marks unobserved periods as **Online** — they're gray **Unknown** and excluded from the "Mostly" summary.