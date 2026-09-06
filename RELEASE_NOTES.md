**2026.48.6**

This update makes context menus smaller and more compact, and adds saving for stickers and prints (Taskbar > Tools > VRChat > VRChat Config).

**Saving Prints and Stickers**
- New **Instance Stickers** section in the VRChat Config modal, next to Instance Prints. Stickers spawned in your instance are saved once as `player_date_stickerId.png` to a folder you choose, so moderators can trace who placed what.

**Responsive Changes**
- All tabs: the gap between the center content and the sidebars, the taskbar and the window bottom is halved from 28px to 14px.
- Media Library, Worlds, People, Groups, Avatars, Calendar and Inventory: the toolbar now stays in one row. On narrow windows the filter buttons scroll horizontally.
- Scrollable filter rows now fade at both edges to show there's more to scroll.
- Filter buttons now show their count as a badge, like the People tab:
  - **Inventory**: per category. The item count next to the Upload button is gone. Categories that aren't loaded yet show **X**.
  - **Avatars**: My Avatars, Favorites, Recently Used.
  - **Worlds**: Favorites, Recently Visited, My Worlds.
  - **People**: Favorites, All Friends, Instance, Recently Seen, Blocked, Muted, plus the All, In-Game, Active and Offline filters under All Friends.
  - **Groups**: filters renamed to **Joined**, **Instances**, **My Groups** and **Moderate**.

**Context Menu**
- Context Menu v2: user, world, instance, group, avatar and media library menus now open with an icon toolbar for frequent actions (Favorite, Boop, Join, Set as Home, Represent, Copy, Pin, Hide, Reveal).
- **Share** is now a **Copy** submenu with **Copy ID** and **Copy Link**. Clicking **Copy** copies the link right away, while hovering still shows both options.
- Right-click now opens the matching menu on the **by username** badge (Group, World and Avatar modals), the **Organizer** (Event modal), and entries in Most Visited Worlds and Interacted The Most With.

**Custom Chatbox**
- Custom text lines can be reordered by drag and drop, like the modules.
- System stats show VRAM as used/total, e.g. **VRAM 3.7/8GB**.

**User & World Modals**
- Timeline, Last Activity and Instance History now use the Timeline page list design, with a **Show more** link that opens the Timeline with the user or world preselected in the search.

**Changes**
- Text rendering: all text now uses ClearType (subpixel antialiasing) instead of grayscale.
- Updated some badge colors.
- Removed the glow effect from VRC+ and local group badges.

**Bug Fixes**
- Context menus no longer flicker on open.
- Context menus now use the configured badge base color.
- Timeline: instance events no longer show a few minutes of Time Spent for players who stayed the whole session. Leave times now come from the VRChat log instead of the app clock, so restarting VRCNext mid-instance no longer creates a bogus second leave. Existing entries are repaired automatically on next start.