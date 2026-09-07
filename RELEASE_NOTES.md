**2026.50.0**

Smaller, more compact context menus, plus saving for stickers and prints (Taskbar > Tools > VRChat > VRChat Config).

**Smart Search**
Smart Search is now 4x better. Before, you could search for settings, tools, users, groups, events, and other content. Now, you can find every single option inside VRCNext.
Looking for print and sticker saving but can't find it? Now you can.
Looking for a specific option but forgot which tool it belongs to? Smart Search will find it for you.

**Community Suggested**

**Saving Prints and Stickers**
- New **Instance Stickers** section in the VRChat Config modal, next to Instance Prints. Stickers spawned in your instance are saved once as `player_date_stickerId.png` to a folder you choose.

**Responsive Changes**
- All tabs: gap between center content and the sidebars, taskbar and window bottom halved from 28px to 14px.
- Media Library, Worlds, People, Groups, Avatars, Calendar and Inventory: the toolbar stays in one row, with filter buttons scrolling horizontally on narrow windows.
- Scrollable filter rows fade at both edges.
- Filter buttons show their count as a badge, like the People tab:
  - **Inventory**: per category. The count next to the Upload button is gone. Unloaded categories show **X**.
  - **Avatars**: My Avatars, Favorites, Recently Used.
  - **Worlds**: Favorites, Recently Visited, My Worlds.
  - **People**: Favorites, All Friends, Instance, Recently Seen, Blocked, Muted, plus the All, In-Game, Active and Offline filters.
  - **Groups**: filters renamed to **Joined**, **Instances**, **My Groups** and **Moderate**.

**Context Menu**
- Context Menu v2: user, world, instance, group, avatar and media library menus open with an icon toolbar for frequent actions (Favorite, Boop, Join, Set as Home, Represent, Copy, Pin, Hide, Reveal).
- **Share** is now a **Copy** submenu with **Copy ID** and **Copy Link**. Clicking copies the link; hovering shows both.
- Right-click opens the matching menu on the **by username** badge (Group, World and Avatar modals), the **Organizer** (Event modal), and entries in Most Visited Worlds and Interacted The Most With.

**Custom Chatbox**
- Custom text lines can be reordered by drag and drop, like the modules.
- System stats show VRAM as used/total, e.g. **VRAM 3.7/8GB**.

**Timeline**
- List view: user names in the **User** column open the profile, avatar names in the **Detail** column open the avatar.
- Group notifications (announcements, events, invites and other group entries) show the group as a link in the **User** column, and the detail popup has a **View Group** button. New notification events store the sender user or group id directly, older entries resolve the group from the cached icon.

**User & World Modals**
- Timeline, Last Activity and Instance History use the Timeline page list design, with **Show more** opening the Timeline preselected for that user or world.

**Changes**
- People tab: **Blocked** and **Muted** moved into a new **Moderated** filter, shown as sub-filters with their counts.
- People > All Friends > In-Game: friends are grouped with separators.
- Dashboard > Friends Activity: friends are grouped by shared instances
- People > Instance: the instance info is a card at the height of the search box next to it, with the world name, an #id badge, the type badge and the player count on the right.
- Smart Search (Ctrl K) now searches all tools: options, sliders, dropdowns and section headers of every tool page and of the VRChat Config, Launch Options, Message Templates and Log Viewer modals. Selecting a result opens the tool or modal and highlights the option, like Settings results.
- Text now uses ClearType (subpixel antialiasing) instead of grayscale.
- Updated some badge colors.
- Removed the glow from VRC+ and local group badges.

**Bug Fixes**
- Context menus no longer flicker on open.
- Dashboard: Friends Activity and Group Activity keep their scroll position when the list updates instead of jumping back to the top.
- Context menus use the configured badge base color.
- Context menus: icons of red actions like Unfriend or Block stay red on hover instead of turning gray.
- Taskbar: the clock and date are no longer cut off when the menu labels are longer, for example in French. The menu area now grows with its content.
- Kikitan XD: Kawai mode with Local Models keeps the spoken or translated sentence and picks a mood-matching kaomoji. Small local models previously answered with just ^-^.
- Timeline: instance events no longer show a few minutes of Time Spent for players who stayed the whole session. Leave times now come from the VRChat log, so restarting mid-instance no longer creates a bogus second leave. Existing entries are repaired on next start.
- Profile and group icons in compact modals are 64px instead of 61px, for pixel-sharp rendering at 100% scale.
