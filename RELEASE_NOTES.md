# **2026.62.4**

**Added**
- Dynamic fork-aware user-agent generation so modified builds identify themselves independently.
- Expanded localization support, including Ukrainian translations and refreshed translation data.
- Linux compatibility for OSC, Media Relay, notifications, tray behavior, and Discord Rich Presence.
- Linux SpaceDrag/SpaceTurn support, avatar scaling, launch options, and VR/Desktop auto-start handling.
- Linux YouTube Fix support using Steam's H.264 unlock URI.
- Linux Steam detection and YouTube Fix launch settings with autosave and restore support.
- Dedicated YouTube Fix JavaScript and CSS module assets, Linux instructions, and platform-specific UI.
- Cross-platform installer and publishing improvements, including Windows PowerShell installation support.

**Changed**
- Preserved Windows VRCVideoCacher behavior while using the Steam H.264 unlock flow on Linux.
- Updated notification hints and tray icon handling across Windows and Linux.
- Deprecated the custom Chatbox “Hide Chatbox Background” option.
- Moved the Windows tray helper into the Helpers organization.
- Improved Edge TTS handling.
- Removed the automatic release publishing workflow.
- Removed the obsolete fork user-agent README notice now that user-agent identity is generated dynamically.

**Fixed Bugs**
- Fixed YouTube Fix visibility and platform filtering on Linux.
- Fixed YouTube Fix launch settings not persisting through the normal settings system.
- Fixed malformed YouTube Fix toggle markup and module loading issues.
- Fixed Linux notification delivery for custom VRCNext images.
- Fixed Linux installer errors and cross-platform installer behavior.
- Fixed Windows tray icon and notification-related issues.
- Fixed assorted staged Linux changes and edge cases from the October stability updates.