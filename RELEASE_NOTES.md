## What's New in 2026.5.2

### API & Performance

- **Reduced API Calls** - Significantly reduced the number of REST requests VRCNext makes to the VRChat API, keeping usage well within VRChat's guidelines.
- **Profile Caching** - User profiles are now cached properly. Re-opening the same profile no longer triggers redundant API calls.

### Topbar

- **V Credits Badge** - A new badge next to the VRC+ indicator displays your current VRChat credit balance.
- **Improved Responsiveness** - The topbar now adapts more reliably to smaller window sizes, ensuring badges and labels are always displayed correctly without flickering.

### Profile Card

- **Status Fix** - Fixed a bug where the profile card could display an incorrect status color and label for offline users.

### Messenger

- **Refreshed Layout** - The message input, character counter, and send button have been reorganized for a cleaner, more cohesive look.
- **Boop Support** - Sending or receiving a boop now shows a dedicated event bubble inside the conversation. Boops are stored in chat history and appear correctly whether the messenger is open or not.
- **Boops Moved Out of Notifications** - Boops no longer appear in the notification panel or as toast cards. They are shown exclusively in the messenger.
- **Send Cooldown** - After sending a message, a 45-second cooldown is enforced. The input placeholder counts down in real time and the cooldown persists even if the messenger is closed and reopened, preventing API spam.
