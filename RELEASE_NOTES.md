**2026.41.5**

**Friends Sidebar**
* Favorite friends are in their sub groups now when the sidebar is expanded.

**i18n**

* Added **zh-TW** localization and a **zh-TW** language button.
  By @SoraneYuki

**Linux Improvements**

* Added an NVIDIA driver check and automatically applies `WEBKIT_DISABLE_DMABUF_RENDERER=1` when required.
  By @SharkieWasHere

**VR Overlay**

* Redesigned notifications. Plain sentences instead of badges, for example "Went Online" or "Joined The Black Cat".
* Notification list now scrolls and holds up to 32 entries instead of 4.
* Status dots on portraits in notifications, toasts and the instance list.
* Location tab groups by world now. Shows instance count, the first names and up to three portraits with a counter.
* Tapping a world opens its instance list. Each instance shows its ID and its friends with Join and Invite.
* The world tab icon turns into a back arrow while an instance list is open.
* Added a crossfade between world grid and instance list.
* Removed the status badges in the friends tab. The dots already show it.
* Fixed the world location text being too dark in the friends tab.
* Fixed the world tab dropping frames. Grouping ran up to five times per frame, it is cached now.
* All Join, Invite and Accept buttons share one size.
* notifications follow the head smoothly instead of fixed.

**Kikitan XD**

* Added a **Kikitan** tab to the VR overlay. It appears while Kikitan XD is running.
* Shows live transcription, and the translation below it when translation is on.
* Marks whether a line is partial or final. Gemini streams partial, Groq only sends final.
* Text scales with the mode. Larger when only transcribing, smaller when translating as well.

**Avatar Search**
* improved avatar lookups and endpoint changes

**Networking**

* Switched networking to **HTTP/2**.

**Fixed Bugs**
* Fixed out-of-memory crashes caused by interface messages being duplicated in memory for logging. This unnecessary logging has been disabled.
* Removed an aggressive garbage collection setting that could cause out-of-memory errors during large database searches and imports. The scheduled 10-minute memory cleanup remains unchanged.
* Fixed the VRChat process state being checked twice every 5 seconds.
* Fixed VRCNext refusing to start on systems with a newer .NET version than the version it was built against.
* Significantly reduced disk usage during Media Library scans by improving how photo metadata is read.
* In a test with 758 photos, disk reads dropped from **3.36 GB to 350 MB** and disk operations from **828,316 to 51,307**.
* For a library of around 40,000 photos, estimated scan reads are reduced from roughly **168 GB to 1.3 GB**.
* World IDs, author names and player lists remain unchanged. The new reader was verified against an existing photo library with no differences.
* Some internal improvements and fixes.
* Removed breadcrumbs when declining notifications only show errors.
