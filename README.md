![Screenshot](https://i.imgur.com/EasCJ7d.png)

# VRCNext

VRCNext is a VRChat Launcher and Management system that aims for simplicity and performance.
With VRCNext you can check the status of your friends, watch their profiles, see in what world they are and ask them to send you an invite or send them one yourself. You can create an instance before even starting VRChat, manage your friends, remove or add new ones. You can search for worlds and groups, change your avatar before even starting VRChat, and set up a Media Library so all of your VRChat images are in one place. Send images directly to Discord using the Media Relay webhook system. See actual instance information such as which players are currently in your instance. Start VRChat directly through VRCNext and configure other apps to launch alongside it like SlimeVR, VRCVideoCacher, and VRCFaceTracking.

---

## Table of Contents

- [FAQ](#faq)
- [Feature Overview](#feature-overview)
- [Linux](#linux)
- [License](#license)
- [Disclaimer](#disclaimer)

---

## FAQ

<details>
<summary><b>Is this a hack client for VRChat?</b></summary>

No. This is not a hack client for VRChat.  
It is simply a launcher that communicates with VRChat's WebSocket and REST API.

The launcher does not modify any game files and does not provide any way to hack or alter the game.

</details>

<details>
<summary><b>Is there any ban risk when I use this?</b></summary>

No. VRCNext follows VRChat's Community Guidelines and Terms of Service.  
Since it is only a launcher with group and friends management plus some additional tools, there should be no ban risk.

It is essentially VRChat.com presented in its own interface.

</details>

<details>
<summary><b>Can I use this without logging into VRChat?</b></summary>

Yes. You can use VRCNext without logging into your VRChat account.  
However you will only be able to use the additional tools and parts of the timeline.

All management systems require a connection to your VRChat account, so they will not work without login.

Login is always optional, but if you want to use all features you will need to log in.

</details>

<details>
<summary><b>Will this save any of my data to external servers?</b></summary>

No. VRCNext does not store any data and does not operate any external servers.

When you log into your VRChat account you communicate directly with VRChat's API.  
There are no servers between VRCNext and VRChat. No storage, no data collection, and full privacy.

</details>

<details>
<summary><b>Does this violate VRChat's Terms of Service?</b></summary>

No. VRCNext exists in a small grey area related to login.

VRChat states that applications should not request login for API access, although VRCX does this as well.  
To respect this guideline, login in VRCNext is completely optional and most features can be used without it.

If you want full functionality you will need to log in. When you do, the communication happens directly with VRChat's API.

The code is open source so everything can be inspected for transparency.

</details>

<details>
<summary><b>Windows shows the application as a virus after installing</b></summary>

This is a false positive.

Windows sometimes flags applications that are not digitally signed. VRCNext is currently not signed.

You can ignore the warning, build the application yourself from the source code, or ask another developer to verify the code.

</details>

<details>
<summary><b>Is there still any way I could get banned or warned?</b></summary>

Theoretical risk always exists, just like with VRCX.

For example if you repeatedly open profiles extremely quickly you could trigger many API requests and receive a 429 rate limit.  
This would temporarily block further interactions for a short time.

The same behavior can happen on the VRChat website. As long as you use the application normally there should be no issues.  
VRCNext also includes internal rate limiting to prevent this from happening.

</details>

VRCNext is not a replacement for VRCX and never will be. It is a hobby project for a good-looking and useful VRChat launcher with practical features. More features may be added in the future!

---

## Feature Overview

### Profile Settings

* Change your online status to red, orange, green, or blue
* Change your status text
* Change your biography
* Change your pronouns
* Change or add your languages
* Change or add your social links (Twitter, Instagram, TikTok, YouTube, Discord, GitHub, and more)
* Change your profile picture / user icon
* Change your profile banner image


### Messenger

Yes, you heard it right. VRCN has a messenger, and the best part is that it uses the VRChat API. More specifically, it uses the InviteMessage and ResponseMessage system of the API.
You can send simple messages such as: "Hey Shiny! Do you want to play VRChat later?"
Keep in mind that VRChat rate-limits message changes. Do not spam messages, as you will be rate limited by VRChat. As an additional safety measure, VRCN also applies its own rate limit.
This is not a full messenger like Discord or WhatsApp. It is intended only for simple messages, such as asking a friend if they want to play together. Use it responsibly.


### Manage Your Friends

* Add notes and read existing notes
* Check which groups they are in and join them
* Add a new friend
* Check their rank, description, and verification status
* Check their last seen status, pronouns, and online status
* Unfriend them if you do not want to stay friends anymore
* See mutual friends you have with someone
* Block or mute users directly from their profile
* View your full blocked and muted users list
* Friend list is split into sections: Favorites, In-Game, Web/Active, and Offline — collapse any section to keep the list tidy
* Search and filter your friend list by name in real time
* Trust rank badge is shown directly on each friend card
* Favorite friends appear pinned at the top in their own section

### Timeline

* See when you first meet someone and when you meet again!
* See when you entered a specific instance or world so you can remember the moment!
* See when you took a picture. Never forget anything!
* Keep track of your personal timeline in VRChat
* **Friends Timeline:** See when your friends come online or offline, change their status, update their bio, or move between worlds in real time

### Multi-Invite

* When you are inside an instance you can click on "Invite" and invite multiple people at once
* Features anti-spam protection and respects VRChat rate limits
* A progress bar shows the status when inviting many friends at once

### Instance Info

* Show all players who are in an instance with you right now
* Open user profiles of players in your current instance
* Add people from the instance you are in as friends

### Stats

* See how much time you have spent with someone overall
* See how much time you have spent in each individual world
* This is stored locally, so time spent before using this feature may not be recorded

### Worlds and People

* Find new people or add them as friends using the People tab
* Find new worlds and see if there is any public instance available to join
* Create your own instance outside of VRChat and invite yourself
* Create group instances (Group Public, Group, Group+) and pick which group to host under
* See all active instances for a world including player counts, region, and type
* See which friends are already in a world and which instance they are in
* View world details: player count, favorites, visits, capacity, author, tags, and description
* Open the author profile directly from the world detail view
* See how much time you spend in a world (BETA)
* See your favorited worlds and organize them into groups (BETA)
* Rename your favorite world groups
* Browse your own uploaded worlds
* Add or move worlds between favorite groups directly from the world detail view

### Groups

* Show the groups you are already a member of and read their descriptions
* Leave any group you are part of
* Search for a group and join it
* See group posts
* See if there is an active group instance
* See the image gallery of a group
* See updates and descriptions
* Create group posts with media attachments
* Create group instances directly from VRCNext

### Avatar

* Change your avatar before you even start VRChat
* See your own uploaded avatars
* Search within your own avatars by name
* See your favorited avatars organized into groups, rename favorite avatar groups
* Search for public avatars via avtrdb.com with pagination
* Browse the Rose Database — a community-curated list of public avatars with category tags
* Platform compatibility badges (PC / Quest) shown on every avatar card
* Your currently active avatar is highlighted with a "Current" badge

### Inventory

* View and manage your VRChat Photos and Gallery
* Upload and manage custom profile Icons (VRC+ required)
* Upload and manage custom Emojis (VRC+ required, up to 18)
* Upload and manage custom Stickers (VRC+ required, up to 18)
* View your in-game Prints collected in VRChat
* Browse your Inventory — props, emojis, and stickers obtained from bundles
* Download or delete individual files
* Animated emojis are labeled with an ANIM badge
* All items are grouped by upload date

### Dashboard

* Show the worlds your friends are in so you can join them quickly
* Show the activity of your friends in real time
* Click a world card to see exactly which friends are there and which instance type they are in
* When friends are spread across multiple instances the dashboard shows the instance count

### Notifications

* See if you have a new friend request and deny or accept it
* See if someone invites you to an instance
* See if someone invites you to a group
* Rich in-app toast cards pop up in real time for friend requests, invites, request invites, invite responses, vote-to-kick, group announcements, group invites, and group join requests
* Accept or deny notifications directly from the toast card without opening the Notifications tab

### Media Library

* See all of your VRChat images sorted by date and time
* Favorite your images so you can find them faster
* Delete images you do not like
* Add custom folders, for example your OBS folder, so your VRChat videos are displayed as well
* See the worlds where you took your images. Enable Meta Data on your VRCCam for this! (BETA)
* See the profiles of people who were in the instance when you took the image
* Blur images that might be sensitive so no one can see them without hovering
* Copy an image URL directly to clipboard
* Filter by folder or by file type (images / videos)
* Infinite scroll — large libraries load progressively so the app stays fast

### Media Relay

* Send images automatically to a Discord channel by using webhooks
* Take a picture and the webhook bot will automatically post it for you
* Add up to 4 webhooks, all configurable, and enable or disable them individually
* Customize the bot name and avatar per webhook

### Chatbox Relay (BETA)

* Show custom text in your chatbox
* Show the song you are listening to on Spotify or iTunes
* Show that you are AFK when VRChat is not focused (Desktop only at the moment)
* Show your computer stats (CPU and RAM usage)
* Show your current local time so people know what time it is for you! (12h/24h system)
* Show how long you have been playing VRChat
* Rotate through multiple custom text lines
* Choose your separator style and update rate
* Suppress the chatbox typing sound effect
* Live preview of the current chatbox message with a character counter

### OSC Tool (BETA)

* Directly control your avatar parameters via OSC
* Automatically detects all available parameters from VRChat
* Toggle or set values for Bool, Int, and Float parameters
* Filter and search through your parameters

### Events Calendar

* Browse upcoming VRChat events in a full monthly calendar view
* Filter by All, Featured, or events you are Following
* Navigate forward and backward through months
* Click any day to see a detailed list of events happening that day
* Open event details including world, time, and description directly from the calendar

### Voice Fight (BETA)

* Voice-triggered soundboard powered by offline VOSK speech recognition — no internet required
* Assign one or more audio files (WAV, MP3, OGG) to any trigger word or phrase
* Randomly picks from multiple files assigned to the same trigger
* Configure a stop word to immediately cut playback
* Per-keyword cooldown prevents accidental spam triggers
* Live microphone VU meter so you can see your input level
* Choose your input microphone and audio output device independently
* Block-list filter strips unwanted filler words from recognition before matching
* Real-time transcription displayed on screen while listening

### YouTube Fix / VRCVideoCacher

* Install and update VRCVideoCacher directly from VRCNext
* Enables YouTube videos to play correctly inside VRChat worlds
* Start and stop the local proxy service without leaving the app

### Space Flight (BETA)

* Change your play space with either the Grip button or control stick, similar to OVR Space Drag
* Lock X, Y, or Z axis for easier Space Flight controls
* Use either left, right, or both hands for this feature
* Adjustable drag multiplier

### Configured Start

* Start VRChat directly from VRCNext
* Start other apps alongside VRChat, for example SlimeVR, VRCFaceTracking, Standable, and others

### Design

* Change the dashboard welcome screen background with a custom image, or choose a random image from your VRChat photo library
* Change the color of the launcher to your favorite color

### Misc

* Activity Log shows everything that happens, including API calls, debugging, and more
* Fast Fetch Cache: frequently used profiles, worlds, and groups are cached locally and shown instantly while refreshing in the background

---

## Linux

# I'm using Arch btw :3

<img width="1913" height="1073" alt="image" src="https://github.com/user-attachments/assets/ff45122a-3fc1-42d0-b3b2-103535884680" />
I am currently working on a Linux port while refactoring the application from WebView2 to Photino.NET. Please give me some time to finish the port.

I am using CachyOS (Arch based) with KDE Plasma, so that is the only environment I can properly test on. I unfortunately do not have the time to test it on every desktop environment or window manager.

However, anyone is welcome to test it and help improve the Linux port.

<details>
<summary><b>Installation</b></summary>

2026.10.8
https://github.com/shinyflvre/VRCNext/releases/tag/v2026.10.8

Other newer builds do not have an linux version as its still being ported. you're stuck on 2026.10.8 for a while :p

Download the latest Linux build and extract the archive. Then run the install script — it will automatically install all required dependencies for your distro and register VRCNext in your application menu.

```bash
chmod +x install_vrcnext.sh
bash install_vrcnext.sh
```

The installer supports **apt** (Debian/Ubuntu), **pacman** (Arch/CachyOS/Manjaro), **dnf** (Fedora), and **zypper** (openSUSE).

</details>

<details>
<summary><b>Dependencies</b></summary>

#### Dependencies installed automatically

| Package | Purpose |
|---|---|
| `webkit2gtk-4.1` | WebKit rendering engine (Photino.NET) |
| `gtk3` | GTK window toolkit |
| `glib2` | Core GLib library |
| `zenity` | Native file dialogs |
| `gst-plugins-base` | GStreamer base plugins |
| `gst-plugins-good` | GStreamer good plugins |
| `gst-libav` | GStreamer FFmpeg/libav codec support |

#### Optional dependency

| Package | Purpose |
|---|---|
| `playerctl` | Now Playing / Play Time in Custom Chatbox (MPRIS2) |

To install `playerctl` on Arch/CachyOS:
```bash
sudo pacman -S playerctl
```

</details>

<details>
<summary><b>Uninstall</b></summary>

```bash
sudo rm -rf /opt/vrcnext && rm -f ~/.local/share/applications/vrcnext.desktop
```

</details>

<details>
<summary><b>Tested On</b></summary>

| Distro | Desktop Environment |
|---|---|
| Arch Linux | KDE Plasma |
| CachyOS (Arch) | KDE Plasma |

</details>

<details>
<summary><b>Feature Status</b></summary>

| Feature | Status |
|---|---|
| Friends Management | ✅ 100% |
| Group Management | ✅ 100% |
| Avatar Management | ✅ 100% |
| All other core features | ✅ 100% |
| Mutual Network | ✅ 100% |
| Time Spent | ✅ 100% |
| Custom Chatbox | 🟡 60% |
| Discord Presence | 🟡 50% *(not tested)* |
| Media Relay | ❌ 0% |
| Space Flight | ❌ 0% |
| OSC Tool | ❌ 0% |
| YouTube Fix | ❌ 0% |
| Voice Fight | ❌ 0% |

</details>

---

## License

VRCNext is licensed under the **VRCNext Non-Commercial Open Source License (VNCOL) 1.0**.

| Permission | Allowed |
|---|---|
| Personal & private use | ✅ |
| Modify the source code | ✅ |
| Distribute copies | ✅ |
| Create forks / derivative works | ✅ |
| Sublicense under the same terms | ✅ |
| Sell the software or any fork | ❌ |
| Use in a commercial product or service | ❌ |
| Place behind a paywall | ❌ |
| Generate revenue from it (directly or indirectly) | ❌ |
| Fork without crediting all contributors | ❌ |

> Forks must visibly credit **all contributors** of the original project (e.g. in the README or a CREDITS file).

See [LICENSE](LICENSE) for the full license text.

---

## Disclaimer

AI Disclaimer: The frontend (the visual interface you see) was partially created with AI assistance. AI helped with parts of the frontend, mainly CSS and JavaScript such as styling and animations, because design is not really my strength. It did not write the whole frontend. The communication between the frontend and backend, as well as the backend itself, was written entirely by a human. 
"and yes i did use DeepL to translate from german to english because english is not my first language, so is this whole descirption translated except this part here :D"

> [!WARNING]
> **VRCNext requires you to log in to VRChat in order to function.**  
> VRCNext does **not** store any sensitive data and is fully open source, so you can verify this yourself in the source code. You can also build the app manually if you do not trust prebuilt `.exe` files.
>
> According to VRChat:
>
> *"You should never ask for or store someone's VRChat credentials. This includes usernames, passwords, login or auth tokens, and/or session data.*
>
> *Since we do not offer OAuth at this time, we know this offers some serious challenges in developing certain applications. Regardless, please do your best to respect this guideline in order to keep users safe."*
>
> This means VRChat asks developers to avoid this wherever possible. However, at this time, applications such as **VRCX** and **VRCNext** require login access in order to function.
>
> By using this application, you acknowledge that **VRChat is not responsible** for any issues related to your account that may result from the use of third-party tools like **VRCX** or **VRCNext**.
>
> While there is no expectation of problems, it is important to note that tools like these exist in a kind of grey area. **Use them at your own risk.**

> [!NOTE]
> **Note for VRChat developers and managers**  
> This application does not violate copyright and does not manipulate the game in any way. It is essentially a compact launcher-style interface for VRChat.com.
>
> The app does not modify VRChat, inject into the game, or alter gameplay or platform behavior. It only communicates with VRChat through the REST API and WebSocket connections needed for its features.
>
> Passwords are not stored on the PC. Email addresses are also not stored. The application only stores the cookies required to keep the user logged in after restarting the app.
>
> No separate server connection is used. The flow is simply:
> `VRCNext -> VRChat REST API / WebSocket`
>
> The application only uses public information and does not place features behind a paywall or any form of monetization.
>
> Use of the application is at the user's own risk.
> In short nothing is saved on any server. Cookies are saved LOCAL on the user machine. i don't get any information about any user :) saftey is important to me!

<img width="218" height="84" alt="image" src="https://github.com/user-attachments/assets/47e584ba-780a-434c-82ff-3004f2e3388a" />
