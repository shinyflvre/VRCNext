# VRCNext 2026.10.3

## Live Own Profile Updates (WebSocket)

- Your **status** (Online / Join Me / Ask Me / Busy) now updates in real time in the sidebar as soon as you change it in VRChat
- Your **status description** updates live without needing to restart the app
- Your **bio**, **pronouns**, **languages**, and **bio links** all sync immediately when edited
- Your **profile icon** and **avatar thumbnail** update live as well
- Previously, none of these would update until the app was restarted — now all own profile changes are reflected instantly via the official VRChat WebSocket (`user-update` event)

---

## Discord Rich Presence

- New **Discord Rich Presence** feature (Tab 19) — shows your current VRChat world and instance state directly in Discord
- Displays your VRChat **status color** (Online / Join Me / Ask Me / Busy) as a small status dot on your Discord profile
- Shows the **world thumbnail** as the large image and elapsed time since joining the instance
- Optional **Join Instance** button in Discord that links directly to your current instance
- **Privacy settings** per status — independently hide instance ID, location, player count, and the join button for each of your four VRChat statuses
- Ask Me and Busy statuses default to hiding sensitive info out of the box
- Can be toggled on/off at any time from within the app
