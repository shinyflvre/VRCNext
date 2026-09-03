**2026.48.3**

**Improvements**
- Six new built-in themes: **Copper**, **Nature**, **Spaceout**, **Flipper Nano**, **Fluffy** and **Ender**, all with matching VR overlay palettes.
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
- With **Enable Profile Themes** on, the Join/Favorite buttons and the Info/Groups/Mutuals tabs in profiles are tinted with the person's VRC+ theme again instead of showing the app theme colors.
- Taskbar hover highlights are now 50% transparent so they no longer cover active icons.
- Lots of hardcoded colors replaced with theme colors: controller illustrations, VR Overlay keybind card/dropdowns, profile "Most Visited Worlds" and "Interacted the most with" cards, and hover highlights across taskbar menus, dropdowns, Pins, Smart Search, sidebars and the navigation editor.
- Heatmap no longer marks unobserved periods as **Online** — they're gray **Unknown** and excluded from the "Mostly" summary.