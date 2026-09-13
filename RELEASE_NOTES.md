# **2026.50.2**

### **Changes**

* **Performance**:
  * Significantly reduced memory usage across VRCNext.
  * Notification sounds now use much less memory and only load when needed.
  * Modals now properly release images and content after being closed.
  * **Image Memory Optimize** now uses smaller thumbnails across more parts of the app.
  * Large lists now only render nearby content, improving performance and reducing memory usage.
  * Improved JavaScript memory handling during large friend list updates.
  * Added **Reduced Background Usage** under Settings > Performance. When enabled, VRCNext pauses rendering and releases GPU memory while minimized or hidden in the tray. Background features continue running normally. Windows only and requires a restart.

* **Multi-Task Mode**:
  * Added **Open in new Window** under Settings > Multi-Tasking.
  * SHIFT + Left Click can now open Profiles, Groups, Worlds, Avatars, Events and Instances in separate desktop windows.
  * Detached windows share the existing WebView, so they do not create additional browser instances.
  * Windows only and requires a restart.
  * The maximum number of open Multi-Task windows is now 8 instead of 12.

### **Fixed Bugs**
  * Fixed scaling and zooming in detached windows.
  * Fixed detached windows briefly showing the desktop while resizing.
  * Fixed detached windows showing the plain browser tooltip instead of the VRCNext tooltip when hovering elements.
  * Fixed the image picker for profile icons and banners opening in the main window when triggered from a detached window.
  * Fixed the content of detached windows shifting for a moment while the main window or another detached window is resized.
  * Detached windows no longer stay blank after a browser process failure. The page is reloaded or the windows are reattached automatically.
  * Fixed detached windows turning black once about ten windows were open or a window was made very wide. The shared drawing surface hit the GPU texture limit, windows are now arranged in rows below that limit.
  * Fixed duplicate **Restart VRCNext** buttons appearing under Performance.
  * Fixed some badges appearing taller than others.
