# **2026.62.0**

**Improvements**
* The **Favs.** tab in profiles now shows the number of favorite worlds.
* The **Instance** modal, **My Instance** modal, **People** > **Instance** and the instance card in profiles now show the minimum avatar performance of an instance.
* The **World** modal now also lists group instances of your groups in that world.
* The **My Instance** modal is now wider and taller.

**Changes**
* VRCNext now uses less memory while running in the background.
* **Timeline** entries are now loaded from the database when needed instead of being kept in memory.
* Photo ratings in the **Media Library** are now read faster and with less memory.
* **Submit avatars to VRCNDb** now uses far less memory while VRCNext is running.
* **Fix NPSMSvc** in **Windows Fixes** now uses less memory.
* Saving and loading caches now uses less memory.
* Player profiles of people in your instance now use less memory.
* World information now uses less memory.
* Notifications now use less memory.
* The database now frees unused memory in the background.

**Fixed Bugs**
* Fixed photo ratings changed outside VRCNext sometimes not being picked up for the whole **Media Library**.
* Fixed **Current Avatar** in your own profile and the **Avatars** tab showing an old avatar after restarting VRCNext.
* Fixed switching back to your last saved avatar not being picked up by VRCNext.
* Fixed the description field in **Create Group** having its text stuck to the top edge.
* Fixed a double gap between the region and player count badges in the **My Instance** modal.
* Fixed the instance card in the friends sidebar not showing the **Public** badge.
* Fixed the player count sometimes missing on the **Current World** card in profiles.