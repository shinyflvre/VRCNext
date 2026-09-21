# **2026.60.8**

**Changes**
* Changes on the avtrdb/vrcndb/cuteavisearch consent modal.

**Fixed Bugs**
* Fixed avatars in the Instance list and **People > Instance** not updating when someone changed avatars. Avatar changes now update immediately from the game log.
* Fixed profiles sometimes showing outdated avatars after the VRChat API changes. Profiles now only show avatars that can be matched to the current profile picture or detected from the game log.
* **Check for Avatar** now shows the avatar name when known, even if the avatar is not available in a public avatar database.
* Avatars that are only known by name can now be opened more often. VRCNext searches avtrdb, avtr.icu and VRCNDb for the name and only accepts a result whose image is exactly the same file. Nothing is guessed.
* Avatars that were not found are now checked again after an hour instead of after 30 days, so avatars added to a database later show up quickly. **Check for Avatar** always asks the databases again right away.