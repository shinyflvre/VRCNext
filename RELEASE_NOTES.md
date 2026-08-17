**2026.42.3**
* Temporarily disabled the **"VRCN has crashed"** modal.
* The current watchdog is too aggressive and can count a simple **taskkill** as a crash, so the crash handler has been disabled for now.
* Crash logs are still generated when an actual crash occurs.

**2026.42.2**
* Added an option to use the SteamVR Overlay without movement blocking.
* HOTFIX - Fixed Voice Fight having no entries.
* HOTFIX - Fixed being unable to add new sound files to Voice Fight.

**2026.42.1**
* HOTFIX - Fixed the Pin system showing the world ID instead of the world name.
* HOTFIX - Fixed usernames displaying incorrectly in the Pin system.
* HOTFIX - Fixed incorrect names, images, and icons in the Pin system.


**2026.42.0**
**Smart Search**
* Added **Friends** and **Personal Timeline** buttons to Smart Search, allowing you to quickly search the timeline for a keyword without opening the Timeline first.

**Profile Previews**
* Updated the profile preview design to v2.
* Fixed time spent and meet counts showing different values than the profile and the Time Spent tab.

**Notifications**
* Notifications have now a volume slider so you can choose how loud the notification is.
* Added new notification dropdowns with 15 sounds to all four notification types.

**User Profiles**
* Added a **Creator** badge for users who sell content or participate in the VRChat Creator Economy.
* Added a **Trusted Score** showing how established and trustworthy a user appears to be.
* The score considers account age, uploaded worlds or avatars, VRC+ support, biography, trust rank, badges, and Creator Economy participation.
* Added a trust description inside **Trust & Safety** based on the user's score.

**Action Flow**
* Added "left player name (string)" action flows.
* Fixed an bug where the joined playername returned an user id instead of username.

**Time Spent**
* Updated the Time Spent tab design to v2.

**Dashboard**
* Completely redesigned the Dashboard with the new **VRCN v2** style.
* Added customizable hero widgets for **Friends/Group Activity**, **Next Event**, and **VRChat News**.
* Added a new **Pins** hero widget.
* Reworked **Edit Dashboard**. Widgets can now be added, removed, and reordered directly on the Dashboard.
* Added support for **2 widgets side by side**.
* Redesigned and improved most Dashboard widgets.
* Removed several outdated or redundant widgets.

**VR Overlay**
* Fixed major FPS drops caused by images being resized every frame. Images are now scaled once and reused, keeping the overlay smooth regardless of source resolution.
* This applies across the entire overlay, including world thumbnails, friend avatars, notifications, the Friends tab, your avatar, music album art and its blurred background, and notification toasts.
* Further improved overlay rendering by reducing unnecessary CPU work.

**Groups**
* Added a new **Group Instances** tab showing active instances from all your groups.

**Timeline**
* The search bar now suggests friends and worlds while typing. Selecting one creates a badge that filters the timeline to that friend or world.

**Performance**
* Improved memory cleanup to reduce VRCNext's RAM usage.
* **Memory Trim** is now enabled by default and runs every 15 minutes.
* The VR helper now only runs when needed, saving around **100 MB of RAM** when VR features are not being used.
* Improved loading performance in the **Time Spent** tab by around **150%**.

**Modals**
* Profile, World, Group, and Avatar modals now always use the **Compact** layout.
* Modal actions and breadcrumb history now always appear in the top bar.
* Removed the old taskbar navigation mode and its related settings.
* Added outlines to cards.

**Removed**
* Removed the **Navigation** tab from Settings.
* Removed the **Classic** modal design for Profile, World, Group, and Avatar modals.
* Removed the **Direct Modal Search** option, as it is now always enabled.

**Fixes**
* Fixed the horizontal scroll position in **People > Instance** resetting during player list updates.
* Fixed **See All** on the **Friends Recent Activity** widget not opening **Timeline > Friends**.
* **See All** on the **Group Activity** widget now opens **Groups > Group Instances**.
* Fixed Dashboard timeline events not updating live and requiring a manual refresh.
* Fixed activity widgets showing internal names such as `group.announcement` instead of proper labels. Status changes now also show the old and new status again.
* Fixed missing right-click context menus on **Friends Activity** and **Group Activity** hero widgets.
* Fixed the World modal header image disappearing or becoming corrupted after opening a timeline event from inside the modal.
* Fixed Dashboard timeline events not using localization keys.
* Fixed timeline events not showing status dots.
* Fixed avatar author lookups always sending at least one unnecessary request to AVTRDB. Pagination now follows the API's `has_more` flag, cutting requests in half for authors with only one page of avatars.
* Fixed the **AVTRDB/GET** Activity Log counter always staying at 0 even while avatar searches were running.
* Added missing `instance.announcement` support to the notification system and Timeline.
* Fixed audio devices changing after restarting VRCNext, Windows, or reconnecting devices.
* Audio devices are now saved using their stable Windows device ID so the correct device is restored reliably.
* Missing devices now show as **(Unavailable)** without overwriting the saved selection or using another device.
* Existing audio settings are migrated automatically where possible.
* Fixed FrameShot audio inputs losing their selection while temporarily unavailable.
* Fixed inaccuracies in the **Time Spent** tab that could show incorrect overall playtime.
* Fixed inaccurate person counts, meet counts, and time-spent values in the **Time Spent** tab.
* Fixed an issue on time spent tab causing the users and worlds to not be filtered correctly.
* Fixed an issue where time spent tab showed wrong orders or spent-bars.
* Fixed meet counts being one too low in **Rewind** and inconsistent in **Profile Insights**.

**Internal Changes**
Mostly cleanup to improve maintainability and reduce some of the structural chaos created over time.
* Removed unused methods left over, JavaScript functions, old CSS classes left over from the V1 design.
* Removed **VRChat.API**, as VRCNext no longer uses it.
* Updated **NAudio** from 2.2.1 to 3.0.0.