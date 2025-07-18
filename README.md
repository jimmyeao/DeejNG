![example workflow](https://github.com/jimmyeao/DeejNG/actions/workflows/codeql.yml/badge.svg)
# DeejNG

![Screen Recording 2025-07-12 134720 (1)](https://github.com/user-attachments/assets/f6480159-9857-4fb0-8840-5471621184ac)

DeejNG is a modern, extensible audio mixer and controller for Windows, built with WPF (.NET 9), NAudio, and SkiaSharp. It allows real-time control over system and app volumes using physical sliders (e.g. Arduino), complete with VU meters, mute toggles, and persistent target mappings.

## 🚀 Features

- 🎛️ **Physical Slider Control** via serial input
- 🎚️ **Multiple Channels** with per-channel volume and mute
- 🎧 **Supports Applications, System Audio, and Microphones**
- 🔇 **Per-Channel Mute with Visual Feedback**
- 📈 **Smooth VU Meters** with SkiaSharp rendering
- 🔁 **Session Auto-Reconnect & Expiration Handling**
- 💾 **Persistent Settings** including targets, input mode, themes, and more
- 🌓 **Light/Dark Theme Toggle**
- 🛠️ **Start at Boot** and **Start Minimized** options
- 🔊 **Control Unmapped Applications**
- 🎙️ **Input (Microphone) Device Volume Support**
- 🧠 **Smart Session Caching** and optimized session lookup
- 🧰 **Extensive Logging and Self-Healing Timers**

---

## 🧩 How It Works

- Channels (sliders) are represented by `ChannelControl` elements.
- Each slider is mapped to one or more **targets**:
  - System audio
  - Specific applications (by executable name)
  - Input devices (microphones)
  - Unmapped sessions (everything else)
- Volume data is received via serial (USB COM port).
- VU meters are driven by a 25ms dispatcher timer, showing real-time audio levels.
- Targets are assigned via a double-click on a channel, launching a session picker.

---

## 🖱️ Usage Instructions

### 🎚️ Setting Up Sliders

1. Connect your physical slider hardware (e.g. Arduino).
2. Launch DeejNG.
3. Select the correct COM port from the dropdown and click **Connect**.
4. Sliders will auto-generate based on incoming serial data (e.g. `0.5|0.3|...`).

### 🎯 Assigning Targets

- **Double-click a slider** to open the session picker.
- Select from running applications, "System", "Unmapped Applications", or microphones.
- You can select multiple targets per slider. One slider can control multiple apps or a mic.

 <img width="786" height="593" alt="image" src="https://github.com/user-attachments/assets/3f5bce09-0e8b-498c-a69d-1f040545139d" />


### 🔇 Mute / Unmute

- Click the **Mute** button on each channel to toggle audio mute.
- The button will turn red when muted.

### 📊 Show/Hide Meters

- Use the "Show Sliders" checkbox to toggle VU meters.
- Meters update live with peak-hold animation.

### ⚙️ Settings

Settings are saved automatically and include:
- Assigned targets per slider
- Input mode per channel
- Theme preference (light/dark)
- Slider inversion
- Smoothing toggle
- Start on boot
- Start minimized

---


