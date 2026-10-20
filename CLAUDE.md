# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

DeejNG is a modern audio mixer and controller for Windows built with WPF (.NET 9), NAudio, and SkiaSharp. It provides real-time control over system and application volumes using physical sliders connected via serial (e.g., Arduino), featuring VU meters, mute controls, and persistent configuration with profile support.

**Key Technologies:**
- .NET 9 / WPF (Windows-only)
- NAudio for audio session management
- SkiaSharp for VU meter rendering
- Serial port communication (System.IO.Ports)

## Build & Run Commands

**Build the project:**
```powershell
dotnet build DeejNG.sln
```

**Build for Release:**
```powershell
dotnet build DeejNG.sln -c Release
```

**Run the application:**
```powershell
dotnet run --project DeejNG.csproj
```

**Clean build artifacts:**
```powershell
dotnet clean
```

Note: This is a Windows-only WPF application targeting .NET 9. Requires Windows with audio devices and optionally Arduino hardware for physical slider control.

## Architecture Overview

### Core Architectural Pattern

The application uses a **manager-based architecture** with manual dependency injection via `ServiceLocator` (Core/Configuration/ServiceLocator.cs). Key managers coordinate different aspects:

1. **SerialConnectionManager** - Handles USB/COM port communication with hardware sliders
2. **AppSettingsManager** - Persistent settings with fallback paths for Server OS compatibility
3. **ProfileManager** - Multiple user profiles for different scenarios (Gaming, Streaming, etc.)
4. **DeviceCacheManager** - Caches audio device information to reduce COM calls
5. **TimerCoordinator** - Coordinates all application timers (VU meters, overlays, etc.)

### Key Components

**MainWindow (MainWindow.xaml.cs)**
- Central hub coordinating all managers and services
- Owns the collection of `ChannelControl` instances (sliders)
- Manages dispatcher timers for VU meters (25ms refresh)
- Handles audio session discovery and volume application

**ChannelControl (Dialogs/ChannelControl.xaml.cs)**
- Individual slider UI component with SkiaSharp-rendered VU meter
- Can control multiple AudioTargets (apps, devices, system audio)
- Features: mute toggle, input/output device switching, smoothing
- Uses pre-calculated segment colors for performance

**AudioService (Classes/AudioService.cs)**
- Wraps NAudio API for audio session management
- Implements session caching (5-second refresh, max 15 cached apps)
- Handles "unmapped applications" - controls all apps not assigned to sliders
- Distinguishes system processes from user applications

**OverlayService (Core/Services/OverlayService.cs)**
- Manages floating overlay window (FloatingOverlay) showing volume levels
- Configurable timeout, opacity, text color, position
- Position persistence via OverlayPositionPersistenceService

### Directory Structure

```
DeejNG/
├── MainWindow.xaml(.cs)     # Main application window
├── App.xaml(.cs)            # Application entry point
├── Classes/                 # Core utilities
│   ├── AppSettings.cs       # Settings model
│   ├── AudioService.cs      # Audio session management
│   └── AudioUtilities.cs    # Audio helper functions
├── Models/                  # Data models
│   ├── AudioTarget.cs       # Represents controllable audio target
│   ├── Profile.cs           # User profile with settings
│   ├── ThemeOption.cs       # Theme metadata
│   ├── ButtonAction.cs      # Button action enum
│   ├── ButtonMapping.cs     # Button configuration model
│   └── ButtonIndicatorViewModel.cs  # Button UI state
├── Services/                # Manager classes
│   ├── AppSettingsManager.cs       # Settings persistence
│   ├── ProfileManager.cs           # Profile management
│   ├── SerialConnectionManager.cs  # Serial communication
│   ├── DeviceCacheManager.cs       # Audio device caching
│   ├── TimerCoordinator.cs         # Timer lifecycle
│   └── ButtonActionHandler.cs      # Button action execution
├── Dialogs/                 # UI components and dialogs
│   ├── ChannelControl.xaml(.cs)    # Slider component
│   ├── SessionPickerDialog.xaml    # App picker
│   ├── SettingsWindow.xaml         # Settings UI
│   └── ...
├── Views/                   # Additional windows
│   └── FloatingOverlay.xaml(.cs)   # Transparent overlay
├── Core/
│   ├── Configuration/       # ServiceLocator for DI
│   ├── Interfaces/          # Service interfaces
│   ├── Services/            # Overlay, power management services
│   └── Helpers/             # Screen position utilities
├── Infrastructure/
│   └── System/              # System integration (startup, registry)
└── Themes/                  # XAML theme files
```

### Audio Target Types

An `AudioTarget` can represent:
1. **System Audio** - Master volume control
2. **Application** - Specific app by executable name (e.g., "Spotify.exe")
3. **Input Device** - Microphone/recording device
4. **Output Device** - Speakers/headphones
5. **Unmapped Applications** - All apps not assigned to any slider

Each slider (ChannelControl) can control multiple targets simultaneously.

### Serial Protocol

Hardware sends slider values as pipe-delimited floats (0.0-1.0):
```
0.5|0.3|0.8|1.0|0.0
```
Number of values determines slider count. SerialConnectionManager parses this and raises DataReceived events.

### Button System

**Physical Button Detection:**
Buttons are auto-detected from serial data. Hardware sends special values to indicate button states:
- `10000.0` = Button released (not pressed)
- `10001.0` = Button pressed

Buttons can be mixed with sliders in the same serial message:
```
0.5|0.3|10001.0|0.8|10000.0
```
This represents: 2 sliders, 1 pressed button, 1 slider, 1 released button.

**Button Types & Behavior:**

The system distinguishes between two button types based on their action, even though all physical buttons are momentary:

1. **Momentary Buttons** - UI indicator shows press state only while physically held
   - MediaNext (Next Track)
   - MediaPrevious (Previous Track)
   - MediaStop (Stop Playback)

2. **Latched Buttons** - UI indicator shows toggled/persistent state
   - MediaPlayPause - Toggles between playing/paused state with each press
   - MuteChannel - Shows actual mute state of the target channel
   - GlobalMute - Shows mute state (lit when any channel is muted)

**Button Actions (Models/ButtonAction.cs):**
- `None` - No action assigned
- `MediaPlayPause` - Toggle play/pause (sends Windows media key)
- `MediaNext` - Next track (sends Windows media key)
- `MediaPrevious` - Previous track (sends Windows media key)
- `MediaStop` - Stop playback (sends Windows media key)
- `MuteChannel` - Toggle mute for specific channel (requires TargetChannelIndex)
- `GlobalMute` - Toggle mute for all channels
- `ToggleInputOutput` - Reserved for future use
- `SendKeys` - Send a keyboard shortcut (`KeyCombo`, e.g. "Ctrl+Shift+M", sequences comma-separated)
- `RunCommand` - Run a command line via `cmd.exe /c` (`Command`)

**Button Mapping (Models/ButtonMapping.cs):**
`ButtonMapping` derives from `ButtonActionBinding` (Action, TargetChannelIndex, KeyCombo, Command).
The inherited fields are the short-press action; `LongPress` is a second binding; `HoldLayer`
lists slider overrides that are active while the button is held:
```csharp
{
    ButtonIndex = 0,                    // 0-based button index
    Action = ButtonAction.MediaPlayPause,
    TargetChannelIndex = -1,            // -1 for global, 0+ for specific channel
    FriendlyName = "Play/Pause",
    LongPress = { Action = ButtonAction.SendKeys, KeyCombo = "Ctrl+Shift+M" },
    HoldLayer = [ { ChannelIndex = 2, Targets = [ { Name = "discord.exe" } ] } ]
}
```

**Gestures (Services/ButtonGestureController.cs):**
- Only a short action: fires immediately on press (old behavior)
- Short + long, no layer: long fires once `AppSettings.LongPressThresholdMs` is reached while held, otherwise short fires on release
- With a hold layer: layer is active while held; if a layer slider moved, nothing fires on release, otherwise long/short is decided on release
- With a double press (`DoublePress`, window `AppSettings.DoublePressWindowMs`): a short tap is held back for the window; a second press inside it fires the double action immediately
- Slider pickup: after any routing switch (layer on/off) a slider value is ignored until it moves more than `PickupThreshold` from where it was, so neither target jumps

**Button UI Indicators:**
Buttons are displayed in the UI as indicator chips showing:
- Button number (BTN 1, BTN 2, etc.)
- Action icon (▶, ⏭, ⏮, ⏹, 🔇)
- Action text (Play/Pause, Next, Previous, etc.)
- Visual state (highlighted when pressed/active)

Indicators use WPF DataTriggers bound to `ButtonIndicatorViewModel.IsPressed`:
- True = AccentBrush background (highlighted)
- False = Default background

**State Tracking (MainWindow.xaml.cs):**
- `_buttonIndicators` - ObservableCollection of ButtonIndicatorViewModel for UI binding
- `_playPauseState` - Tracks play/pause toggle state (bool)
- Mute state - Queried from ChannelControl.IsMuted

**Button Event Flow:**
1. Serial data received → `SerialConnectionManager` detects button value (10000/10001)
2. `ButtonStateChanged` event raised with (buttonIndex, isPressed)
3. `MainWindow.HandleButtonPress()` processes the event:
   - For non-latched indicators: Update indicator.IsPressed = isPressed
   - Passes the event to `ButtonGestureController.OnButtonChanged()`, which calls back `ExecuteButtonBinding()`
   - For latched buttons: Update indicator to show actual state after action
4. `ButtonActionHandler.ExecuteAction()` performs the action (media keys, mute toggle, keys, command)
5. UI indicator updates automatically via INotifyPropertyChanged

**Implementation Details:**
- ButtonIndicatorsList.ItemsSource set only once during initialization to prevent binding issues
- All button state updates marshalled to UI thread via Dispatcher.BeginInvoke
- Media keys sent via Win32 keybd_event API (VK_MEDIA_PLAY_PAUSE, etc.)
- Mute actions directly call ChannelControl.SetMuted()
- Button indicators auto-hide when no button mappings configured

**Important Notes:**
- Physical buttons are always momentary (hardware has no latching mechanism)
- "Latched" refers to the UI indicator behavior, not the physical button
- Play/Pause state is tracked in software since Windows doesn't report media state
- All button events fire on both press AND release for UI feedback
- Plain buttons execute on press; buttons with long press or hold layer decide on release (or at the long-press threshold)
- `HandleSliderData` asks `ButtonGestureController.RouteSliderValue()` where each value goes (normal targets, layer targets, or ignored during pickup)

### VU Meter Implementation

- SkiaSharp-based rendering with 20 segments (green → yellow → red)
- 25ms refresh rate via DispatcherTimer
- Peak hold with decay animation
- Smoothing factor: 0.3 for gradual level changes
- Optimized with cached SKPaint objects and pre-calculated colors

### Session Management & Caching

**AudioService session cache:**
- Refreshes every 5 seconds
- Caches up to 15 most recently accessed apps
- Key: application name (case-insensitive)
- Value: List of SessionInfo with process IDs

**Performance optimizations:**
- Device caching to reduce NAudio COM calls
- Reusable collections (_tempTargetList, _tempStringSet)
- Throttled unmapped application updates (100ms)
- Smart session refresh only when needed

### Settings Persistence

Settings stored in JSON format with multiple fallback paths for Server OS compatibility:
1. `%LocalAppData%\DeejNG\settings.json` (preferred)
2. `%AppData%\DeejNG\settings.json` (fallback)
3. Application directory (last resort)

Profiles stored in `profiles.json` in the same directory. All saves are throttled to prevent excessive disk I/O.

### Theme System

Themes are XAML ResourceDictionary files in Themes/ directory:
- DarkTheme.xaml, LightTheme.xaml (default pair)
- Additional themes: Arctic, Cyberpunk, Dracula, Forest, Nord, Ocean, Fluent, Mideej

Switched dynamically via App.xaml.cs resource merging.

## Common Development Patterns

### Adding a New Manager Service

1. Create interface in `Core/Interfaces/` (e.g., INewService.cs)
2. Implement in `Core/Services/` or `Services/`
3. Register in `ServiceLocator.Configure()` or inject via constructor in MainWindow
4. For MainWindow-level managers, add as readonly field and initialize in constructor

### Modifying Audio Session Logic

- All session enumeration goes through `AudioService`
- Use `GetAllSessions()` for app discovery
- Use cached sessions when possible (check `_sessionCache`)
- System processes (PID 0, 4, 8) are filtered out
- Always handle disposed sessions (NAudio throws COMException)

### Working with Profiles

- Profiles are managed by `ProfileManager`
- Each profile contains a complete `AppSettings` snapshot
- Switching profiles triggers `ProfileChanged` event
- MainWindow subscribes to reload all settings

### Dispatcher Timer Best Practices

- All timers coordinated through `TimerCoordinator`
- VU meter timer runs at 25ms for smooth animation
- Device cache refresh is less frequent (varies by usage)
- Always check `_isClosing` before timer operations

### Serial Communication Changes

- SerialConnectionManager handles reconnection logic
- Watchdog monitors for silent connections (5s threshold)
- DataReceived event fires with complete lines (not partial)
- Leftover buffer prevents message truncation

### Working with Buttons

**Adding a New Button Action:**
1. Add enum value to `Models/ButtonAction.cs`
2. Implement action logic in `Services/ButtonActionHandler.ExecuteAction()`
3. Update `MainWindow.HandleButtonPress()` to categorize as momentary or latched
4. Add icon mapping in `MainWindow.GetButtonActionIcon()`
5. Add text mapping in `MainWindow.GetButtonActionText()`
6. Add tooltip mapping in `MainWindow.GetButtonActionTooltip()`

**Button State Update Rules:**
- **Momentary buttons**: Update `indicator.IsPressed` immediately on press/release
- **Latched buttons**: Update `indicator.IsPressed` AFTER action executes with actual state
- **Never update indicators directly** - always use Dispatcher.BeginInvoke for thread safety
- For new latched buttons, query actual state (like mute) rather than tracking separate state

**Button Indicator Lifecycle:**
1. `ConfigureButtonLayout()` called when sliders are generated or settings change
2. `UpdateButtonIndicators()` clears and rebuilds _buttonIndicators collection
3. ItemsSource set only on first initialization (checked via null check)
4. ObservableCollection automatically notifies UI of changes
5. Panel visibility managed automatically (visible when mappings exist)

## Important Implementation Notes

### Audio Session Lifetime

NAudio audio sessions can expire or become invalid. Always:
- Wrap session access in try-catch for COMException
- Check session state before accessing properties
- Re-enumerate sessions periodically (not on every operation)
- Use session instance IDs for stable identification

### WPF Threading

- Audio operations happen on background threads
- UI updates require `Dispatcher.Invoke` or `Dispatcher.BeginInvoke`
- ChannelControl updates are dispatcher-marshalled
- Settings saves are async to prevent UI blocking

### Memory Management

- Dispose NAudio objects (MMDeviceEnumerator, AudioSessionControl) properly
- ServiceLocator.Dispose() called on shutdown
- Unregister event handlers to prevent memory leaks
- TimerCoordinator stops all timers on disposal

### Performance Considerations

- Session enumeration is expensive - cache results
- VU meter rendering is GPU-accelerated (SkiaSharp)
- Smoothing can be disabled per-channel for responsiveness
- Device queries are throttled to reduce COM overhead

## Known Constraints & Design Decisions

1. **Windows-only** - Uses WPF and NAudio (WASAPI)
2. **Single instance** - No multi-instance support
3. **Serial dependency** - Requires hardware for full functionality (but can run without)
4. **Manual DI** - Uses ServiceLocator pattern instead of full DI container
5. **File-based persistence** - No database, uses JSON files
6. **25ms timer resolution** - Balance between smoothness and CPU usage
7. **Session cache TTL** - 5 seconds to balance freshness vs performance

## Debugging Tips

- `#if DEBUG` blocks throughout codebase provide verbose logging
- Check Debug output for serial communication issues
- AppSettingsManager logs fallback path selection
- Session cache hits tracked in `_sessionCacheHitCount`
- Timer coordinator logs timer lifecycle events
- Button events logged with `[Button]` prefix showing state changes and indicator updates
- Button action execution logged in `ButtonActionHandler` with action type
- Momentary vs latched button categorization logged in HandleButtonPress
