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
│   └── ThemeOption.cs       # Theme metadata
├── Services/                # Manager classes
│   ├── AppSettingsManager.cs       # Settings persistence
│   ├── ProfileManager.cs           # Profile management
│   ├── SerialConnectionManager.cs  # Serial communication
│   ├── DeviceCacheManager.cs       # Audio device caching
│   └── TimerCoordinator.cs         # Timer lifecycle
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
