**2026.48.3**

**Improvements**
- **Create Instance**: Group instances can now set a **Minimum Avatar Performance** limit (No Limit, Good, Medium, Poor).
- Profile status heatmap is much more accurate — statuses are recorded live and backfilled on startup.

**Changes**
- Softer hover colors for the **Miku**, **Flipper Zero**, **Rose**, **Unicorn** and **Baby** themes, so hover states no longer flash in the full accent color.
- The built-in **VRCN** theme has been refreshed (v3): slightly darker cards and inputs, a calmer accent, and dedicated button, active and badge colors.
- New **Badge Base** theme color for badges and tags (defaults to Hover BG, so no visual change).
- New **UI Input Active BG** theme color for active buttons (defaults to accent, so no visual change).
- **Create Instance** modal updated to the v2 vrcn design.
- Calendar **Help Sort** now uses a soft candy color palette with stable per-group colors.
- Stats are no longer bold.
- Clearer placeholder text on empty tab pages.

**Removed**
- Removed the **VRCX**, **Slates**, **Rose**, **Unicorn**, **Baby** and **Flipper Zero** themes. If one of them was active, VRCNext falls back to the VRCN theme.

**Fixed Bugs**
- The controller illustrations in the VR Overlay, FrameShot, Space Flight and Space Turn settings now follow the theme's Text 1 color instead of being plain white.
- The keybind card, the controller view and the dropdowns in the VR Overlay settings now use the theme's Buttons Base color instead of Hover BG.
- The **Most Visited Worlds** and **Interacted the most with** cards in profiles no longer use a hardcoded darkened background. They now use the theme's Input BG and Hover BG.
- Hover highlights across taskbar menus, dropdowns, Pins, Smart Search, sidebars and the navigation editor now use the theme's **Hover BG** instead of hardcoded white.
- Heatmap no longer shows unobserved periods as **Online** — they're now gray **Unknown** and excluded from the "Mostly" summary.