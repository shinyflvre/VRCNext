# VRCNext 2026.10.0 / 2026.6.5

This is a very large update and a major refactor of the CSS, JavaScript, and general frontend.
Redundant CSS classes have been removed. VRCNext has been ported from WebView2 to Photino.NET,
opening the door for future Linux and macOS support. While Linux is not fully working yet, most
base elements do function. There are still plenty of fixes needed before Linux and macOS are fully
supported — any help is appreciated.

---

## Photino.NET Migration

> **You may need to install .NET 9.** The Velo Installer should notify you, but if not please install it manually.
> We previously used .NET 10 and downgraded for Photino.NET compatibility.

- VRCNext previously used WebView2 and is now migrated to Photino.NET
- This migration opens the door for future platform support such as Linux and macOS
- First tests were already done on Arch Linux and CachyOS with KDE Plasma
- The application base runs well on Linux but some features still need fixes and adjustments
- Not everything works on Linux yet, but the groundwork is there

### Linux Build

- If you want to try it early you can already build a Linux version yourself
- Use the `linux_build.bat` file included in the project or run the publish command manually
- The compiled Linux build will appear in the `publish` folder

---

## VRCX Import

- You can now import your existing VRCX database into VRCNext
- Most logs will be migrated into the VRCNext Timeline system
- Imported timeline data includes: First Meets, Meet Again events, Bio Changes, Status Changes, Online/Offline events, Instance joins, and Instance information
- Some data may be missing because VRCNext does not implement every VRCX feature
- If you rely heavily on specific VRCX data it may still be better to continue using VRCX for now

---

## Timeline

- Added proper pagination for Timeline and List views — limited to 100 items per page to reduce memory usage
- Pagination controls are now always visible, no longer requires scrolling to the bottom
- The Timeline now shows how many times you have met someone
- Added two new personal Timeline tabs:
  - **Avatars** — shows avatars you have used based on timeline events
  - **URLs** — shows media URLs played in worlds (YouTube, SoundCloud, and other detected sources)
- Instance links can now be copied directly from the Timeline

---

## Dashboard

- The Dashboard now shows **Your Instances** — all of your currently open instances
- You can invite friends directly from an instance card or close an instance
- You can create instances without needing to be inside VRChat first — prepare an instance and invite friends before starting the game
- The Dashboard now shows a live global player count showing how many people are currently playing VRChat

---

## Media Library

- Added Folder View to the Media Library for easier navigation through your photo collection

---

## Instance Links

- Instance links can now be copied from the **Instances** tab
- Instance links can also be copied from the **Location** field in the Friends tab
- Makes it easier to save instance links or re-invite yourself to Invite or Invite Plus instances

---

## Context Menu

- Right-clicking your own instance now shows **Invite Friends** and **Close Instance** options

---

## Avatars

- You can now edit your own uploaded avatars directly from the Avatars tab:
  - Change avatar name
  - Edit the description
  - Edit tags
  - Toggle between Public and Private visibility

---

## VRChat API

- Reduced unnecessary GET requests across several areas
- Removed redundant API calls where the same data was requested multiple times
- `GetLocation` calls are no longer made when VRChat is not running
- The LogWatcher now handles location detection while VRChat is active
- World images are now cached for 14 days since they rarely change

---

## Performance

- Friend loading is now faster with 5 parallel workers in `GetOnlineFriendsAsync` and `GetOfflineFriendsAsync`
- Added in-flight request deduplication for `GetUserAsync` — identical concurrent requests are no longer executed multiple times
- World information is now enriched on demand through WebSocket events instead of bulk fetching at startup

---

## Profile

- The Profile modal now shows the meet count next to **Time Spent Together**

---

## Changes

- Added the missing **Invite Plus** instance type to the Create Instance dialog
- Inventory uploads now accept `.jpg` and `.jpeg` files (Icons, Banners, Stickers, etc.)
- Added image compression for Inventory uploads when the file is too large
- Added auto-resize for images that exceed the upload size limit

---

## Fixes

- Fixed profile banner and profile picture uploads not working after the Photino.NET migration
- Fixed profile images of players inside an instance randomly disappearing
- Fixed multiple REST API calls being triggered on a single player status change
- Fixed instance info images being wiped when someone joined or left the active instance
- Fixed the notification card closing when deleting or declining a message
- Fixed being unable to unfavorite an avatar in the **My Avatars** section
- Fixed favorite world groups incorrectly showing XX/25 instead of the correct XX/100 limit
- Fixed the Timeline showing no results when switching dates while a search query was active
- Fixed the Timeline not updating results when switching filters or clearing the search query
- Fixed the file size limit in Inventory — all files are now correctly set to 8 MB
