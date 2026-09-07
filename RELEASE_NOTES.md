**2026.50.0**

**Smart Search**
Smart Search is now much better. Before, you could search for settings, tools, users, groups, events and other content. Now you can also find basically every option inside VRCNext.
Looking for print and sticker saving but can't find where it is? Smart Search can find it.
Looking for a specific option but forgot which tool it belongs to? Smart Search can find that too.

**Saving Prints and Stickers**
* New **Instance Stickers** section in the VRChat Config modal, next to Instance Prints. Stickers spawned in your instance are saved once as `player_date_stickerId.png` to a folder you choose.

**Responsive Changes**
* All tabs: the gap between the center content and the sidebars, taskbar and window bottom has been reduced from 28px to 14px.
* Media Library, Worlds, People, Groups, Avatars, Calendar and Inventory: the toolbar now stays in one row, with filter buttons scrolling horizontally on narrow windows.
* Scrollable filter rows now fade at both edges.
* Filter buttons now show their count as a badge, like the People tab:

  * **Inventory**: per category. The count next to the Upload button is gone. Unloaded categories show **X**.
  * **Avatars**: My Avatars, Favorites, Recently Used.
  * **Worlds**: Favorites, Recently Visited, My Worlds.
  * **People**: Favorites, All Friends, Instance, Recently Seen, Blocked, Muted, plus the All, In-Game, Active and Offline filters.
  * **Groups**: filters renamed to **Joined**, **Instances**, **My Groups** and **Moderate**.

**Context Menu**
* Context Menu v2: user, world, instance, group, avatar and media library menus now open with an icon toolbar for common actions like Favorite, Boop, Join, Set as Home, Represent, Copy, Pin, Hide and Reveal.
* **Share** is now a **Copy** submenu with **Copy ID** and **Copy Link**. Clicking Copy copies the link directly, while hovering shows both options.
* Right-click now opens the matching menu on the **by username** badge in Group, World and Avatar modals, the **Organizer** in the Event modal, and entries in Most Visited Worlds and Interacted The Most With.

**Custom Chatbox**
* Custom text lines can now be reordered with drag and drop, just like modules.
* System stats now show VRAM as used/total, for example **VRAM 3.7/8GB**.

**Timeline**
* List view: user names in the **User** column open the profile, and avatar names in the **Detail** column open the avatar.
* Group notifications like announcements, events, invites and other group entries now show the group as a link in the **User** column. The detail popup also has a **View Group** button. New notification events store the sender user or group ID directly, while older entries still resolve the group from the cached icon.

**User & World Modals**
* Timeline, Last Activity and Instance History now use the Timeline page list design, with **Show more** opening the Timeline already filtered for that user or world.

**Changes**
* People tab: **Blocked** and **Muted** moved into a new **Moderated** filter, shown as sub-filters with their own counts.
* People > All Friends > In-Game: friends are now grouped with separators.
* Dashboard > Friends Activity: friends are now grouped by shared instances.
* People > Instance: the instance info is now shown as a compact card next to the search box, with the world name, an #id badge, type badge and player count.
* Smart Search (`Ctrl K`) now searches all tools, including options, sliders, dropdowns and section headers from every tool page and the VRChat Config, Launch Options, Message Templates and Log Viewer modals. Selecting a result opens the tool or modal and highlights the option, just like Settings results.
* Text now uses ClearType subpixel antialiasing instead of grayscale.
* Updated some badge colors.
* Removed the glow from VRC+ and local group badges.

**Bug Fixes**
* Context menus no longer flicker when opening.
* People lists: the Language column now shows every language as a badge with its full name and wraps instead of cutting off after the first one. Instance, All Friends, Favorites and the other lists now use the same badges.
* Dashboard: Friends Activity and Group Activity now keep their scroll position when the list updates instead of jumping back to the top.
* Context menus now use the configured badge base color.
* Context menu icons for red actions like Unfriend or Block now stay red on hover instead of turning gray.
* Taskbar: the clock and date are no longer cut off when menu labels are longer, for example in French. The menu area now grows with its content.
* Kikitan XD: Kawai mode with Local Models now keeps the spoken or translated sentence and adds a mood-matching kaomoji. Small local models previously sometimes answered with just `^-^`.
* Timeline: instance events no longer show a few minutes of Time Spent for players who stayed for the whole session. Leave times now come from the VRChat log, so restarting mid-instance no longer creates a bogus second leave. Existing affected entries are repaired on the next start.
* Profile and group icons in compact modals are now 64px instead of 61px for pixel-sharp rendering at 100% scale.
