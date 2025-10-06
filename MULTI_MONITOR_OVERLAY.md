# Multi-Monitor Overlay Position Persistence

## Overview

This implementation provides robust overlay position persistence across multi-monitor setups, handling display configuration changes such as:
- Plugging/unplugging monitors
- Laptop docking/undocking
- Display resolution changes
- Monitor rearrangement

## Architecture

### Core Components

1. **ScreenPositionManager** (`Core/Helpers/ScreenPositionManager.cs`)
   - Manages screen information and position validation
   - Handles display configuration changes
   - Provides proportional position adjustment when screens change

2. **OverlayPositionPersistenceService** (`Core/Services/OverlayPositionPersistenceService.cs`)
   - Persists overlay position with screen metadata
   - Validates and corrects position on load
   - Debounces saves to prevent excessive disk writes

3. **OverlayPositionData** (Model in `OverlayPositionPersistenceService.cs`)
   ```csharp
   public class OverlayPositionData
   {
       public double X { get; set; }
       public double Y { get; set; }
       public string ScreenDeviceName { get; set; }  // NEW: Screen identifier
       public string ScreenBounds { get; set; }       // NEW: "Left,Top,Width,Height"
       public string OperatingSystem { get; set; }
       public DateTime SavedAt { get; set; }
   }
   ```

4. **AppSettings** (`Classes/AppSettings.cs`)
   - Already contains `OverlayScreenDevice` and `OverlayScreenBounds` properties
   - Now properly populated and used for validation

## How It Works

### Saving Position

When the overlay is moved:

1. **ScreenPositionManager.GetScreenInfo(x, y)** determines:
   - Which screen the overlay is on (`DeviceName`)
   - The screen's bounds (`Left,Top,Width,Height`)

2. **Position is saved with screen metadata**:
   ```csharp
   {
       "X": 2560.0,
       "Y": 400.0,
       "ScreenDeviceName": "\\\\.\\DISPLAY2",
       "ScreenBounds": "1920,0,1920,1080",
       "OperatingSystem": "Win32NT 10.0.22631.0",
       "SavedAt": "2025-10-06T14:30:00"
   }
   ```

### Loading Position

When the app starts:

1. **Load saved position** from JSON file

2. **Validate screen configuration**:
   
   **Case 1: Screen exists with same bounds**
   - Position is valid ✓
   - Use saved position as-is

   **Case 2: Screen exists but bounds changed**
   - Calculate relative position within old screen
   - Apply proportionally to new screen bounds
   ```csharp
   relativeX = (savedX - oldLeft) / oldWidth
   relativeY = (savedY - oldTop) / oldHeight
   
   correctedX = newLeft + (relativeX * newWidth)
   correctedY = newTop + (relativeY * newHeight)
   ```

   **Case 3: Screen no longer exists**
   - Calculate relative position within old screen
   - Move to primary screen at same relative position
   - Clamp to visible area

   **Case 4: No screen info saved**
   - Validate against virtual screen bounds
   - Correct if outside visible area

### Example Scenarios

#### Scenario 1: Laptop Undocking

**Before** (2 monitors):
- Primary: 1920x1080 at (0, 0)
- Secondary: 1920x1080 at (1920, 0)
- Overlay at (2560, 400) on secondary

**After** (1 monitor):
- Primary: 1920x1080 at (0, 0)
- Secondary: MISSING

**Result**:
- Relative position calculated: 33% from left, 37% from top
- Applied to primary: (640, 400)

#### Scenario 2: Display Resolution Change

**Before**:
- Monitor: 1920x1080 at (0, 0)
- Overlay at (1600, 400)

**After**:
- Monitor: 2560x1440 at (0, 0)

**Result**:
- Relative position: 83% from left, 37% from top
- Applied to new resolution: (2133, 533)

#### Scenario 3: Docking Station

**Before** (laptop only):
- Primary: 1920x1080 at (0, 0)
- Overlay at (1600, 400)

**After** (docked with 3 monitors):
- Primary: 1920x1080 at (1920, 0)
- Left: 1920x1080 at (0, 0)
- Right: 1920x1080 at (3840, 0)

**Result**:
- Original screen still exists at different position
- Bounds changed from (0,0,1920,1080) to (1920,0,1920,1080)
- Position adjusted: (3520, 400)

## Technical Details

### Screen Identification

Screens are identified by `DeviceName` (e.g., `\\\\.\\DISPLAY1`, `\\\\.\\DISPLAY2`)

This identifier:
- ✓ Persists across reboots
- ✓ Unique per physical monitor
- ✗ May change when monitors are unplugged/replugged in different order

### Bounds Format

Screen bounds are stored as: `"Left,Top,Width,Height"`

Example: `"1920,0,1920,1080"` means:
- Screen starts at X=1920, Y=0
- Screen is 1920 pixels wide, 1080 pixels tall

### Position Correction Algorithm

```csharp
if (screen exists)
{
    if (bounds same)
        return savedPosition; // No change needed
    else
        return proportionalPosition; // Adjust for new bounds
}
else // screen missing
{
    return proportionalPositionOnPrimary; // Move to primary screen
}
```

### Debouncing Strategy

**Position saves are debounced** to prevent excessive writes:
- **300ms debounce** after each position change
- **5-second forced save** if position is "dirty"
- **Immediate save** on app shutdown

## Debugging

All debug output is wrapped in `#if DEBUG` compiler directives:

```csharp
#if DEBUG
    Debug.WriteLine($"[ScreenPositionManager] Position corrected: ({x}, {y}) -> ({newX}, {newY})");
    Debug.WriteLine(ScreenPositionManager.GetScreenDiagnostics());
#endif
```

### Debug Output Examples

**Screen Diagnostics**:
```
Screen Count: 2
Virtual Screen: 0,0 3840x1080
Screen 0: \\.\DISPLAY1 - {X=0,Y=0,Width=1920,Height=1080} (Primary)
Screen 1: \\.\DISPLAY2 - {X=1920,Y=0,Width=1920,Height=1080}
```

**Position Correction**:
```
[ScreenPositionManager] Validating position: (2560, 400)
[ScreenPositionManager] Saved screen device: \\.\DISPLAY2
[ScreenPositionManager] Screen no longer exists, moving to primary
[ScreenPositionManager] Position moved to primary screen: (640, 400)
```

## File Locations

- **Position file**: `%APPDATA%\DeejNG\overlay_position.json`
- **Settings file**: `%APPDATA%\DeejNG\appsettings.json`

Both files contain position and screen information for redundancy.

## Error Handling

The system gracefully handles:
- Missing/corrupt position files → Use defaults
- Invalid screen references → Move to primary
- Out-of-bounds positions → Clamp to visible area
- Permission errors → Continue with in-memory state

## Future Enhancements

Potential improvements:
1. **Screen fingerprinting** - Use EDID data for more reliable screen identification
2. **Multi-profile support** - Different positions for different display configs
3. **Visual feedback** - Show overlay briefly on app start to confirm position
4. **Position presets** - Quick access to common positions (corners, edges, center)
