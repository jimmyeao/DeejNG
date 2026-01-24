![example workflow](https://github.com/jimmyeao/DeejNG/actions/workflows/codeql.yml/badge.svg)

<p align="center">
  <img src="logo.png" alt="DeejNG Logo" width="200"/>
</p>

# DeejNG - We've had a UI Update!

<img width="772" height="561" alt="image" src="https://github.com/user-attachments/assets/988c1148-2e28-4be7-b430-7425d8209647" />


We now support buttons! You can use our fork of Deej to add button support here https://github.com/jimmyeao/ButtonDeej


DeejNG is a modern, extensible audio mixer and controller for Windows, built with WPF (.NET 9), NAudio, and SkiaSharp. It allows real-time control over system and app volumes using physical sliders (e.g. Arduino), complete with VU meters, mute toggles, and persistent target mappings. This is meant as a companion app to the hardware, the code for which can be found here https://github.com/omriharel/deej

##  New!
Configurable transpartent overaly with adjustable time out
<img width="654" height="166" alt="image" src="https://github.com/user-attachments/assets/2ab966f9-1fac-45ad-8978-177e7e76a214" />


Profiles - configure multiple profiles for different scenarios

Add applicaiotn manually by name

## 🚀 Features

- 🎛️ **Physical Slider Control** via serial input
- **Button Support** for toggle or momentary press buttons
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
  - Current application (by focused window)
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


<img width="800" height="683" alt="image" src="https://github.com/user-attachments/assets/bf9d0c45-b2f3-4423-b351-0df545778777" />



### 🔇 Mute / Unmute

- Click the **Mute** button on each channel to toggle audio mute.
- The button will turn red when muted.

### 📊 Show/Hide Meters

- Use the "Show Sliders" checkbox to toggle VU meters.
- Meters update live with peak-hold animation.

### ⚙️ Settings
<img width="900" height="769" alt="image" src="https://github.com/user-attachments/assets/52eb9539-72e8-4003-b7e3-9db4d1fbc586" />


Settings are saved automatically and include:
- Assigned targets per slider
- Input mode per channel
- Theme preference (light/dark)
- Slider inversion
- Smoothing toggle
- Start on boot
- Start minimized
- 
### ⚙️ Button Settings

<img width="800" height="901" alt="image" src="https://github.com/user-attachments/assets/c73522f1-d8ef-4250-9f9e-4c96fc578ff4" />

Configure Buttons for Media Control or Mute


---


