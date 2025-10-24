# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

DeejNG is a modern Windows audio mixer and controller built with WPF (.NET 9), NAudio, and SkiaSharp. It enables real-time control of system and application volumes using physical slider hardware (Arduino) via serial communication, featuring VU meters, mute controls, persistent target mappings, and a configurable transparent overlay.

**Companion Hardware:** https://github.com/omriharel/deej

## Development Commands

### Build & Run
```powershell
# Build the project
dotnet build DeejNG.sln

# Run the application
dotnet run --project DeejNG.csproj

# Build for release
dotnet build DeejNG.sln -c Release

# Publish single-file executable
dotnet publish DeejNG.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

### Testing
This project does not currently have automated tests. Test manually by:
1. Connecting Arduino hardware with physical sliders
2. Verifying serial communication (9600 baud, format: `0.5|0.3|0.8|...`)
3. Testing volume control, mute, VU meters, and overlay functionality

## Architecture

### Core Application Structure

**Entry Point:** `App.xaml.cs`
- Initializes `ServiceLocator` on startup for dependency injection
- Disposes services on exit

**Main Window:** `MainWindow.xaml.cs`
- Central hub connecting all subsystems
- Manages channel controls (sliders), serial communication, audio services, and overlay
- Uses multiple manager classes to separate concerns

### Manager Classes (Services Layer)

These managers handle specific subsystem responsibilities:

1. **ProfileManager** (`Services/ProfileManager.cs`)
   - Manages multiple user profiles (e.g., "Gaming", "Streaming", "Default")
   - Each profile contains complete application settings including COM port
   - Handles profile creation, switching, renaming, and deletion
   - **Migration:** Automatically migrates legacy settings.json to "Default" profile on first run
   - Persists to `profiles.json` with active profile tracking
   - **Critical:** `GenerateSliders()` must load from `_profileManager.GetActiveProfileSettings()`, NOT from disk

2. **SerialConnectionManager** (`Services/SerialConnectionManager.cs`)
   - Manages COM port connection lifecycle
   - Implements watchdog for automatic reconnection
   - Parses slider data format: `0.5|0.3|0.8|...` (pipe-delimited 0.0-1.0 values)
   - Handles manual disconnect vs. automatic reconnect scenarios

3. **AppSettingsManager** (`Services/AppSettingsManager.cs`)
   - Works with ProfileManager to persist settings
   - **Note:** No longer saves directly to settings.json; profiles handle persistence
   - **Critical:** Uses fallback path strategy for Windows Server compatibility
   - Multi-monitor overlay position persistence with screen device tracking
   - Validates overlay positions across display configuration changes
   - Exposes `GetSettingsPath()` for ProfileManager to determine profiles.json location

4. **DeviceCacheManager** (`Services/DeviceCacheManager.cs`)
   - Caches audio input/output devices for performance
   - Manages NAudio `MMDevice` references
   - Applies volume/mute to input (microphones) and output devices

5. **TimerCoordinator** (`Services/TimerCoordinator.cs`)
   - Centralizes all `DispatcherTimer` management
   - Timers: VU meters (25ms), session cache (1s), cleanup (5min), serial reconnect (5s), watchdog (1s), position save (500ms debounce)
   - Prevents timer leaks and ensures coordinated startup/shutdown

6. **AudioService** (`Classes/AudioService.cs`)
   - Core audio control via NAudio
   - Session caching with LRU eviction
   - Applies volume/mute to targets: specific apps, system, unmapped apps, current focused app
   - Handles expired sessions and process matching

### Service Locator Pattern

`Core/Configuration/ServiceLocator.cs` provides manual DI:
```csharp
ServiceLocator.Configure();  // Register services
var service = ServiceLocator.Get<IOverlayService>();
ServiceLocator.Dispose();    // Cleanup
```

Registered services:
- `IOverlayService` → `OverlayService`
- `ISystemIntegrationService` → `SystemIntegrationService`
- `IPowerManagementService` → `PowerManagementService`

### Audio Target Types

**AudioTarget Model** (`Models/AudioTarget.cs`):
- `Name`: Process name or special keyword
- `IsInputDevice`: Microphone/input device flag
- `IsOutputDevice`: Speaker/output device flag

**Special Targets:**
- `"system"`: Master system volume
- `"unmapped"`: All applications not assigned to any slider
- `"current"`: Currently focused window's application (uses `AudioUtilities.GetCurrentFocusTarget()`)

### Channel Controls

**ChannelControl** (`Dialogs/ChannelControl.xaml.cs`):
- Represents a single slider channel with VU meter, mute button, and target label
- Supports multiple targets per channel (one slider controls multiple apps)
- Events: `VolumeOrMuteChanged`, `TargetChanged`, `SessionDisconnected`
- Double-click opens target picker dialog

**Target Assignment:**
- Double-click slider → `SessionPickerDialog` or `MultiTargetPickerDialog`
- Shows running apps, system, unmapped, input/output devices
- Multi-select support for controlling multiple targets per slider

### Overlay System

**FloatingOverlay** (`Views/FloatingOverlay.xaml`):
- Transparent, topmost, draggable window showing volume levels
- Multi-monitor support with position persistence
- Configurable timeout (auto-hide) or always-on mode
- Text color auto-detection based on background brightness

**Position Management:**
- `OverlayService` (`Core/Services/OverlayService.cs`): Core overlay logic
- `ScreenPositionManager` (`Core/Helpers/ScreenPositionManager.cs`): Multi-monitor validation
- Saves screen device name + bounds to detect monitor removal/rearrangement

### Power Management

**PowerManagementService** (`Core/Services/PowerManagementService.cs`):
- Monitors system suspend/resume events via `SystemEvents`
- **Suspend:** Saves settings, stops timers, hides overlay
- **Resume:** Refreshes audio devices, restarts timers, reconnects serial
- **Important:** Uses background priority for dispatcher operations to prevent focus stealing

### Event Handling Pattern

Audio session events use a decoupled handler to avoid COM reference issues:

```csharp
public class DecoupledAudioSessionEventsHandler : IAudioSessionEventsHandler
{
    private readonly MainWindow _mainWindow;
    private readonly string _targetName;

    public void OnSessionDisconnected(AudioSessionDisconnectReason reason)
    {
        // Notify MainWindow without holding COM references
        _mainWindow.HandleSessionDisconnected(_targetName);
    }
}
```

Handlers registered in `SyncMuteStates()` and cleaned up when targets change.

## Important Implementation Details

### Profile System Architecture
**File:** `profiles.json` stored in same directory as legacy settings.json
- Each profile contains: name, settings (AppSettings), created/modified timestamps
- Active profile name tracked in ProfileCollection
- **Migration on first run:** Loads legacy settings.json → creates "Default" profile → backs up old file
- **Critical Bug Prevention:** Always load settings from `_profileManager.GetActiveProfileSettings()`, NEVER from `_settingsManager.LoadSettingsFromDisk()`
- Profile switching flow:
  1. Save current profile settings (`SaveSettings()`)
  2. Switch active profile (`_profileManager.SwitchToProfile()`)
  3. Reload UI from new profile (`LoadSettingsWithoutSerialConnection()`)
  4. Regenerate sliders with new targets (`GenerateSliders()`)

**UI Components:**
- `InputDialog` - Material Design themed input for profile names (respects light/dark theme)
- `ConfirmationDialog` - Material Design themed confirmations (ShowYesNo, ShowYesNoCancel, ShowOK)
- Profile selector in MainWindow with New/Rename/Delete buttons

### Serial Data Processing
- Hardware sends slider values as `0.0-1.023` (10-bit ADC mapped to 0-1023)
- Deadzone: Values ≤10 → 0, values ≥1013 → 1023
- Invert slider option: `level = 1.0 - level`
- Smoothing can be disabled for immediate response

### Initialization Sequence
1. App starts, `ServiceLocator.Configure()` registers services
2. `MainWindow` constructor:
   - Initializes managers (settings, serial, device, timer, overlay)
   - Loads settings (without auto-connecting serial)
   - Generates sliders from saved settings
   - Subscribes to power management events
   - Starts timers (meters, session cache, watchdog, cleanup)
   - **Critical:** `_allowVolumeApplication = false` until first serial data received
3. First serial data triggers:
   - `_allowVolumeApplication = true`
   - `SyncMuteStates()` to sync with Windows audio state
   - Meter timer starts
4. MainWindow fully operational

### Volume Application Flow
```
Serial data → HandleSliderData()
          → ChannelControl.SmoothAndSetVolume()
          → VolumeOrMuteChanged event
          → ApplyVolumeToTargets()
          → AudioService.ApplyVolumeToTarget() or DeviceCacheManager methods
```

### Mute State Synchronization
`SyncMuteStates()` (called on first data, after resume):
1. Gets all audio sessions from `AudioSessionManager`
2. Registers `DecoupledAudioSessionEventsHandler` for each mapped target
3. Reads current mute state from Windows and applies to channel controls
4. Ensures bidirectional sync (hardware ↔ Windows)

### Session Caching Strategy
- **Session ID Cache** (`_sessionIdCache` in MainWindow): Maps session IDs to `AudioSessionControl` for meter updates
- **AudioService Cache** (`_sessionCache`): Groups sessions by process name, LRU eviction, stale session removal
- Refresh intervals: Session cache (1s), device cache (5s), forced cleanup (5min)

### Settings Persistence
**New Architecture (Post-Profile System):**
- **Primary:** `profiles.json` - Contains all profiles and active profile name
- **Legacy:** `settings.json` - Only used for migration, then backed up to `settings.json.backup`

**File Location (with fallback):**
1. `%LocalAppData%\DeejNG\profiles.json` (preferred)
2. `%AppData%\DeejNG\profiles.json` (roaming fallback)
3. `%UserProfile%\Documents\DeejNG\profiles.json` (documents fallback)
4. `<app directory>\profiles.json` (last resort)

**Each Profile Contains:**
- Profile name (e.g., "Gaming", "Streaming", "Default")
- Slider targets per channel
- Theme (light/dark)
- Slider inversion, smoothing, VU meters
- Start on boot, start minimized
- Overlay: enabled, position (X/Y), screen device, bounds, opacity, timeout, text color
- **COM port name** (different COM ports per profile)

**Important:** Settings save uses explicit flush (`FileStream.Flush(true)`) for Windows Server compatibility.

## Common Tasks

### Adding a New Manager/Service
1. Create interface in `Core/Interfaces/`
2. Implement in `Core/Services/` or `Services/`
3. Register in `ServiceLocator.Configure()`
4. Inject via `ServiceLocator.Get<T>()` in MainWindow

### Adding a New Timer
Use `TimerCoordinator`:
```csharp
_timerCoordinator.InitializeTimers(); // Create
_timerCoordinator.MyNewTimer += MyHandler; // Subscribe
_timerCoordinator.StartMyTimer(); // Start
_timerCoordinator.StopMyTimer(); // Stop
```

### Modifying Audio Target Behavior
- For new target types: Update `ApplyVolumeToTargets()` in MainWindow
- For audio control logic: Modify `AudioService` methods
- For special device handling: Extend `DeviceCacheManager`

### Debugging Serial Issues
- Check `[Serial]` debug output for connection status
- Watchdog logs `[SerialWatchdog]` every 1s if no data received
- Verify data format: `float|float|float|...` (pipe-delimited)
- Manual disconnect prevents auto-reconnect (check `_manualDisconnect` flag)

### Working with Overlay
- Position changes trigger debounced save (500ms) via `TimerCoordinator`
- Multi-monitor: Screen device + bounds saved to detect configuration changes
- `ScreenPositionManager.ValidateAndCorrectPosition()` adjusts position if monitor removed
- Overlay focus prevention: Uses `DispatcherPriority.Background` and checks `Window.IsActive`

## Key Dependencies

- **NAudio (2.2.1)**: Core audio control, `MMDevice`, `AudioSessionManager`
- **SkiaSharp (2.88.9)**: VU meter rendering with smooth animations
- **MaterialDesign**: UI theme framework
- **System.IO.Ports (9.0.7)**: Serial communication

## Known Considerations

- **COM Threading:** NAudio objects are apartment-threaded; use `Dispatcher.Invoke()` for UI updates
- **Session Expiration:** Audio sessions can expire; handlers must be resilient
- **Serial Reconnect:** Automatic unless user clicks "Disconnect" (sets `_manualDisconnect`)
- **Overlay Focus Stealing:** Mitigated by checking window state before showing overlay
- **Windows Server Compatibility:** Settings path fallback handles restricted write permissions
- **Multi-Monitor:** Overlay position validated on startup; corrects if monitor configuration changed

## Troubleshooting

**No audio control:**
- Check `_allowVolumeApplication` flag (should be true after first serial data)
- Verify `SyncMuteStates()` completed successfully
- Check session cache: `[AudioService] Cache refreshed: X sessions in Y apps`

**Overlay not persisting position:**
- Verify settings save with `[Settings] ✓ SAVED and VERIFIED` log
- Check multi-monitor setup: Screen device and bounds should be saved
- Validate `OverlayService.PositionChanged` event fires

**Serial connection issues:**
- Check available ports: `[Ports] Found N ports: [...]`
- Watchdog logs indicate data flow status
- Manual disconnect prevents reconnect until user re-enables

**Settings not saving (Server OS):**
- Check `[Settings] Path not writable` logs
- Fallback path should be selected automatically
- Run `GetDiagnosticInfo()` for detailed path analysis
