# **2026.50.2**

### **Changes**
* **Multi-Task Mode**: Added the setting **Open in new Window** under Settings > Multi-Tasking. When enabled, modals opened in Multi-Task mode with SHIFT + Left Mouse (Profile, Group, World, Avatar, Event, Instance) appear as separate desktop windows instead of floating windows inside the app. These windows share the app's single browser instance, so no additional WebView is loaded. Windows only, takes effect after a restart.

### **Fixed Bugs**
* **Badges**: All badges now have a fixed height that no longer depends on their text or icon. This fixes the instance name badge, the group owner badge and the language badges on instance cards being taller than the other badges.