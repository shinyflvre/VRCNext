# **2026.62.4**

**Added**
- Dynamic fork-aware user-agent generation so modified builds identify themselves independently.
- Expanded localization support, including Ukrainian translations and refreshed translation data.
- Linux compatibility improvements across supported VRCNext features.
- Linux YouTube Fix support using Steam's H.264 unlock URI.
- Linux Steam detection and YouTube Fix launch settings with autosave and restore support.
- Dedicated YouTube Fix JavaScript and CSS module assets.
- Linux-specific YouTube Fix instructions and UI.
- Cross-platform build and publishing improvements.

**Changed**
- Preserved Windows VRCVideoCacher behavior while using the Steam H.264 unlock flow on Linux.
- Removed the automatic release publishing workflow.
- Removed the obsolete fork user-agent README notice now that user-agent identity is generated dynamically.

**Fixed Bugs**
- Fixed YouTube Fix visibility and platform filtering on Linux.
- Fixed YouTube Fix launch settings not persisting through the normal settings system.
- Fixed malformed YouTube Fix toggle markup and module loading issues.