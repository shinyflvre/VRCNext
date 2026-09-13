# **2026.50.2**

### **Changes**
* **Performance**:
  * Notification sounds are now stored as OGG instead of WAV (16 MB down to under 1 MB) and are no longer preloaded at startup. Each sound is loaded the first time it plays, which frees about 10 MB of memory for every user.
  * Modals (Profile, World, Group, Avatar, Instance, Timeline, My Profile) now release their images and content when they are closed instead of keeping them in memory for the whole session.
  * With **Image Memory Optimize** enabled, small avatars, icons, banners in popups and screenshot previews in Timeline and Rewind now use thumbnails instead of full size images. This affects the friends sidebar, taskbar, smart search, pins, group posts and galleries, profile decorations, event icons and the Mutual and Meet Network graphs.
  * Cards and rows in long lists (worlds, groups, avatars, people, timeline, media library, notifications) are only laid out while they are near the visible area, which reduces memory and layout work for long lists.
  * The JavaScript engine's short lived memory area is now capped at 2 MB per half space instead of 16 MB, which keeps the renderer process smaller during heavy friend list updates. Requires a restart.
  * Added the setting **Reduced Background Usage** under Settings > Performance (off by default, Windows only). When enabled, VRCNext pauses rendering and releases GPU memory while the window is minimized or hidden in the tray. Notifications, the friends list and all background features keep running at full speed and rendering resumes instantly when the window is shown again.

* **Multi-Task Mode**: Added the setting **Open in new Window** under Settings > Multi-Tasking. When enabled, modals opened in Multi-Task mode with SHIFT + Left Mouse (Profile, Group, World, Avatar, Event, Instance) appear as separate desktop windows instead of floating windows inside the app. These windows share the app's single browser instance, so no additional WebView is loaded. Windows only, takes effect after a restart. The setting is not shown on Linux and macOS.

### **Fixed Bugs**
* **Multi-Task Mode**: With **Open in new Window** enabled, Ctrl + scroll, Ctrl + Plus / Minus / 0 and Taskbar > View > Scale now scale the main window and every detached window correctly. Previously the content shrank into itself or was cut off after changing the scale. The shortcuts also work while the mouse is over a detached window.
* **Multi-Task Mode**: With **Open in new Window** enabled, resizing the main window or a detached window no longer shows the desktop through the window for a moment. The app background color fills the window while the new frame is being rendered.
* **Settings**: Fixed two **Restart VRCNext** buttons appearing under Performance after changing a performance setting. Only the button for the current platform is shown now.
* **Badges**: All badges now have a fixed height that no longer depends on their text or icon. This fixes the instance name badge, the group owner badge and the language badges on instance cards being taller than the other badges.