# **2026.50.2**

### **Changes** (Windows Only)
* **Performance**: Added the setting **Reduced Background Usage** under Settings > Performance (off by default). When enabled, VRCNext pauses rendering and releases GPU memory while the window is minimized or hidden in the tray. Notifications, the friends list and all background features keep running at full speed and rendering resumes instantly when the window is shown again.

* **Multi-Task Mode**: Added the setting **Open in new Window** under Settings > Multi-Tasking. When enabled, modals opened in Multi-Task mode with SHIFT + Left Mouse (Profile, Group, World, Avatar, Event, Instance) appear as separate desktop windows instead of floating windows inside the app. These windows share the app's single browser instance, so no additional WebView is loaded. Windows only, takes effect after a restart. The setting is not shown on Linux and macOS.

### **Fixed Bugs**
* **Settings**: Fixed two **Restart VRCNext** buttons appearing under Performance after changing a performance setting. Only the button for the current platform is shown now.
* **Badges**: All badges now have a fixed height that no longer depends on their text or icon. This fixes the instance name badge, the group owner badge and the language badges on instance cards being taller than the other badges.