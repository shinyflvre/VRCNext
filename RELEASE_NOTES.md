# **2026.61.0**

This update focuses on quality of life. Profile previews have been reworked, and user profiles are cleaner and easier to read.

**Avatar Tab**
* Added **Bulk Status** to edit mode in **My Avatars** to make several avatars public or private at once.
* Added **Bulk Tag Add** to edit mode in **My Avatars** to add a tag to several avatars at once.

**Group Tab**
* Added a **Visible to** column to the list view that shows who can see each group on your profile.
* Added **Bulk Visibility** to edit mode to change the visibility of several groups at once.
* Changing a group's visibility now shows a confirmation.

**Own Profile**
* **Current Avatar** and **Representing** are now shown side by side.
* If only one of **Current Avatar** or **Representing** is shown, it now uses the full width.
* **Pronouns** moved below the **Badges** card.
* **Links** and **Languages** are now shown side by side in the **Bio** card.
* The language dropdown now uses the same dropdown as the rest of the app, and the **+** button next to it has the same height.
* Fixed being able to add more than 3 languages.

**Profile Modals**
* Added an **Activity Summary** card with Time Together, Meets, Last Seen, Last Active, Status Mostly and Joined.
* The **Infos** card now only shows Platform, Last Platform, Age Verified and Avatar Cloning.
* Platforms now show as PC, Quest, iOS or Web instead of internal names like `standalonewindows`.
* The **...** button moved next to the other profile buttons and now shows the same options as the right click menu.
* Removed the separate **Unfriend** button. Unfriend is now in the **...** menu.
* Unfriend now asks for confirmation in a dialog.

**Profile Previews**
* Time spent and languages are now shown in a cleaner way.

**Removed**
* Removed the **Trust & Safety** card.
* Removed the **Trust Score**.

**Changes**
* Tags and languages you are editing in profiles, avatars, worlds and groups now use the same colors as the finished badges.
* The **Share** button in profile, world, avatar and group windows now lets you copy either the ID or the link.
* **Kikitan XD**: Translations now fall back to a second AI model if the main one fails.
* Removed the color from **Status Mostly**.

**Fixed Bugs**
* Fixed translation not working in the Kikitan XD live translator.
* Fixed translation of bios and groups with Kikitan XD.
* Fixed the Timeline and Insights switches in profiles sometimes showing nothing selected.
* Fixed unfriending not working for people with an apostrophe in their name.
* Fixed the **Visibility** card in groups not showing your current setting.
* Fixed the selection circles in list view edit mode not being centered in their row.
* Fixed avatars in the Instance list and **People > Instance** not updating when someone changed avatars.
* Fixed profiles sometimes showing outdated avatars after the VRChat API changes.
* **Check for Avatar** now shows the avatar name when known, even if the avatar is not in a public database.
* Avatars that are only known by name can be opened more often.
* Avatars that were not found are now checked again after an hour instead of 30 days.
