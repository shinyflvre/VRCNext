## What's New in 2026.4.0

### Voice Fight

- **Multi-Sound Items** - Each trigger word can now have multiple sounds assigned. When triggered, a random sound from the list is played. Use the **+ Add Sound** button to add more sounds to an existing item.
- **Sentence Triggers** - Trigger words now fire even when said inside a full sentence. Saying "Hello my name is Shiny" will correctly trigger the "Shiny" sound.
- **Multi-Word Triggers** - Trigger phrases can now be multiple words, e.g. "hello kitty" as a trigger phrase.
- **Stop Command** - A dedicated stop word can be configured. Saying it instantly stops any currently playing sound and plays a confirmation sound (`voice/sounds/stop.wav`).
- **Mute Talk** - When enabled, all recognized speech is sent to VRChat via OSC as a chatbox message — perfect for muted players who want to communicate using their voice.
- **Fix: Device Dropdown** - The Input Device dropdown no longer shows "Loading…" on startup. The correct microphone is displayed immediately.

---

## What's New in 2026.3.6

### Groups

- **Create Events** - You can now create events for your groups directly inside VRCNext.
- **Delete Events** - A Delete button has been added to group events so you can remove events you created.
- **Events in Calendar** - Group events are now correctly shown in the Calendar view.
- **Edit Group Description** - You can now edit a group's description from within the group modal.
- **Edit Group Rules** - You can now edit a group's rules from within the group modal.
- **Edit Group Links** - Group links can now be edited directly inside the group modal.
- **Edit Group Languages** - Group languages can now be changed directly inside the group modal.
- **Change Group Icon** - You can now upload and change the group icon from the group modal.
- **Change Group Banner** - You can now upload and change the group banner from the group modal.
- **Change Group Privacy** - You can now change a group's privacy setting to Closed, Invite, Request Invite, or Public.
- **Group Privacy Badges** - Groups now show a badge indicating their privacy state: Closed, Invite, Request Invite, or Public.
- **Languages Shown** - The group modal now displays the languages associated with a group.
- **Links Shown** - The group modal now displays links associated with a group.
- **Live Instances** - Groups now show active instances in the Live tab. This works for both members and non-members, as long as the content is publicly available.
- **Non-Member Content** - You can now view posts, events, and instances for groups you are not a member of, provided the group has public content.
- **My Groups and Search** - The Groups tab has been updated to two sub-sections, "My Groups" and "Search", matching the layout of the Worlds tab for easier navigation.
- **Quick Moderation** - Right-clicking a user in the Group Members tab now gives you the option to Kick or Ban that user.

---

### Profile

- **Change Profile Image** - You can now change your own profile picture directly from within VRCNext.
- **Change Profile Banner** - You can now change your own profile banner directly from within VRCNext.

---

### Timeline

- **Notifications v2 in Timeline** - The Timeline now correctly shows all notification types including Group Announcements, Group Invites, Group Events, Group Posts, and more. Group name and icon are resolved automatically and shown as the notification source.

---

### UI and Code

- **Unified Dropdown Components** - The Events and Posts dropdowns in Groups now use the shared library dropdown component, removing redundant code and keeping the interface consistent.
