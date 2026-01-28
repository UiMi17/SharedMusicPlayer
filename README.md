# 🛩️ Shared Music Player (Pilot-CoPilot Music Sync Mod)

*Fly in harmony — literally!*

This VTOL VR mod allows you to **sync your in-cockpit MP3 player with your copilot** by:
- Automatically transferring music files from the host to the joining player
- Enabling synchronized playback: **either player** can press the MP3 player's buttons (`Play/Stop`, `Next`, `Prev`) to control music for both

---

## 🚀 How it works

When the copilot enters the cockpit:
1. The mod transfers all `.mp3` files from the host's music folder
2. The mission **pauses until the transfer is complete**, ensuring reliable syncing before takeoff
3. During transfer, the copilot can **cancel the download** by pressing `ESC` or the `B button` on their VR controller
4. After syncing, both players can **play, pause, or switch tracks in sync**

---

## 📂 Installation

1. Install via VTOL VR Mod Loader
2. Place your `.mp3` files in:  
   `VtolVR/RadioMusic/` *(on the host's side)*
3. That's it — copilot will receive the music automatically on mission start

---

## ❗ Requirements

- VTOL VR with Mod Loader installed
- Both players must have the mod installed

---

## 📦 Features

- 🔄 Automatic file transfer from pilot to copilot
- 🔊 Fully synchronized MP3 player
- 🧠 Reliable transfer logic with retries and fallback
- ⚙️ No server or configuration required — uses Steam P2P sockets/VtolVR RPC calls
- ❌ Cancel download option (press `ESC` or `B button` during transfer)
- 📊 Real-time transfer progress display
- 🔍 Smart file deduplication (skips files that already exist with matching hash)

---

## 💬 Feedback / Issues

Feel free to open a [discussion](https://steamcommunity.com/workshop/filedetails/discussion/3499085095/838346164580420605) if you encounter bugs or want to suggest improvements!
