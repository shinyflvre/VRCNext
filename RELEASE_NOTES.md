# **2026.60.4**

PLEASE READ!!! - TRACKING BIO IS GONE!
But you can do something to bring it back!
Upvote my canny here: https://feedback.vrchat.com/open-beta/p/api-bring-back-bio-websocket-events
Maybe VRChat staff will consider adding back bio events in their websocket to track users bio changes.

### **Changes**

* **Performance**:
  * Significantly reduced memory usage across VRCNext.
  * Notification sounds now use much less memory and only load when needed.
  * Modals now properly release images and content after being closed.
  * **Image Memory Optimize** now uses smaller thumbnails across more parts of the app.
  * Improved JavaScript memory handling during large friend list updates.
  * Large lists now only render nearby content, improving performance and reducing memory usage.
  * Profile decorations of other people now come from a single cosmetics list instead of one request per decoration. This mainly shortens the cold start, where every unknown icon frame or nameplate used to cost its own request.
  * Added **Reduced Background Usage** under Settings > Performance. When enabled, VRCNext pauses rendering and releases GPU memory while minimized or hidden in the tray. Background features continue running normally. Windows only and requires a restart.

* **Multi-Task Mode**:
  * Added **Open in new Window** under Settings > Multi-Tasking.
  * SHIFT + Left Click can now open Profiles, Groups, Worlds, Avatars, Events and Instances in separate desktop windows.
  * Detached windows share the existing WebView, so they do not create additional browser instances.
  * Windows only and requires a restart.
  * The maximum number of open Multi-Task windows is now 8 instead of 12.

* **VRChat API Update**:
  * VRChat moved profile data to a new endpoint. Profile pictures, biographies, bio links, badges and languages work again.
  * Profile pictures now use VRChat's new icon field. This restores images in the sidebar, dashboard, friends list, group members, ban lists, group logs and user search.
  * Editing your biography, bio links, languages and profile icon now saves to the new endpoint.
  * Languages now use VRChat's own language list instead of profile tags.
  * The Json tab in a user profile now shows both responses, the one from `/users/` and the one from `/profile/`, since VRChat split profile data across two endpoints.
  * Group member lists now show icon frames and nameplate effects for people who are not on your friends list. Friends already had them, since those come from the live friends data.

### **Fixed Bugs**
  * Fixed last login, last activity and bio links staying empty in the People list. VRChat no longer sends these along with the friends list, so they are now filled in from the local profile cache. They also survive a refresh instead of disappearing again right after **Fetch**.
  * Fixed the language column in the People list putting every language on its own line. The column is wider now and the text inside the badges is centered again.
  * Fixed changes to your own biography no longer showing up in the timeline. VRChat moved the biography to a different endpoint, so the detection was comparing a field that is always empty now. It reads the new endpoint instead, without any extra requests.
  * The People tab now follows live friend updates in the **Recently Seen** and **Instance** views as well. Until now only the **All** filter reacted to them, and only on its first page.
  * Live friend updates now pass on everything VRChat actually sends over the WebSocket. Banner type, banner color, join date and friend state were arriving but were thrown away before reaching the interface, so an open profile never followed a live banner change and the join date came from the local cache alone.
  * Fixed the player list in the instance modal jumping back to the start every time a player joined or left. Only the vertical position was kept, the horizontal one was reset.
  * Fixed the horizontal scrollbar of list views not following the mouse and making the table jump while being dragged. The scrollbar and the table kept resetting each other to outdated positions.
  * Fixed bio links missing for players in your instance. VRChat no longer includes them in the player data, so they are now loaded from the profile when someone joins.
  * Fixed saved biographies, bio links and badges being erased whenever you joined an instance with that person, or when loading their profile failed. This left profiles, the People list and live updates without this information.
  * Fixed the **18+** and **Age Verified** badges disappearing from an open profile when that friend traveled or went offline, from the friend preview after a refresh, and from saved profiles when loading a profile failed.
  * Fixed the **Creator** badge missing when reopening a profile shortly after viewing it.
  * Fixed your own badges disappearing from My Profile after every profile refresh, and staying gone when loading your profile failed. Your profile is now loaded once per refresh instead of twice.
  * Fixed the friend hover card showing no banner for friends who use a color banner.
  * Fixed **Optimize Database** erasing the bio links of your friends. Since the VRChat API change they are only kept in the local cache. The bio link count shown before optimizing now matches what actually gets cleaned.
  * Fixed badges not showing when a profile was opened from the local cache.
  * Fixed a deleted biography, bio links or languages briefly reappearing in My Profile right after saving.
  * Fixed the Action Flow conditions **has bio text** and **own bio text** failing although the text matched. Biographies of friends were missing from the friends list, and your own biography was briefly empty after every profile refresh.
  * Fixed Action Flow instance info webhooks showing your avatar picture instead of your profile icon, or no icon at all for a friend whose picture was not cached yet.
  * Fixed an open profile losing its biography and bio links the moment that person changed their status. Live friend updates no longer carry profile data since the VRChat API change, and the empty fields were overwriting what was already on screen.
  * Fixed group instances staying empty in the sidebar and the Groups tab after a restart until the refresh button was pressed. The list was requested before the VRChat session had finished resuming, and the empty result was then treated as loaded.
  * Fixed the friends list taking a long time to appear after a cold start. VRC+ profile decorations were resolved one after another before the list was handed to the interface. The list now appears right away and the decorations fill in shortly after.
  * Fixed scaling and zooming in detached windows.
  * Fixed detached windows briefly showing the desktop while resizing.
  * Fixed detached windows showing the plain browser tooltip instead of the VRCNext tooltip when hovering elements.
  * Fixed the image picker for profile icons and banners opening in the main window when triggered from a detached window.
  * Fixed the content of detached windows shifting for a moment while the main window or another detached window is resized.
  * Detached windows no longer stay blank after a browser process failure. The page is reloaded or the windows are reattached automatically.
  * Fixed detached windows turning black once about ten windows were open or a window was made very wide. The shared drawing surface hit the GPU texture limit, windows are now arranged in rows below that limit.
  * Fixed duplicate **Restart VRCNext** buttons appearing under Performance.
  * Fixed the Change Status modal becoming scrollable when the status dropdown is opened. Dropdown lists inside modals now float above the modal instead of extending its content.
  * Fixed some badges appearing taller than others.