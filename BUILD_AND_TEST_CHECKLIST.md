# Build and Test Checklist

## Pre-Build Checklist

### ✓ Required NuGet Packages
The solution requires the following NuGet packages (should already be installed):
- [ ] System.Windows.Forms (for Screen class)
- [ ] NAudio (already used for audio)
- [ ] MaterialDesignThemes (already used for UI)

### ✓ Namespace Verifications

Ensure these usings are in place:

**ScreenPositionManager.cs**:
```csharp
using DeejNG.Classes;
using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Forms;  // For Screen class
```

**OverlayPositionPersistenceService.cs**:
```csharp
using DeejNG.Core.Helpers;  // For ScreenPositionManager
using System.Windows.Forms;  // For Screen class
```

**MainWindow.xaml.cs**:
```csharp
using DeejNG.Core.Helpers;  // For ScreenPositionManager
```

## Build Instructions

### 1. Clean Solution
```powershell
dotnet clean
```

### 2. Restore NuGet Packages
```powershell
dotnet restore
```

### 3. Build Debug Configuration
```powershell
dotnet build --configuration Debug
```

### 4. Build Release Configuration
```powershell
dotnet build --configuration Release
```

## Expected Compiler Warnings/Errors

### ✓ Should Compile Successfully
All new code follows existing patterns and uses types already in the project.

### Potential Issues to Watch For

1. **Missing System.Windows.Forms reference**:
   - Error: `The type or namespace name 'Screen' could not be found`
   - Solution: Add reference to System.Windows.Forms (should already be there for NotifyIcon)

2. **Ambiguous Screen reference**:
   - Warning: `'Screen' is an ambiguous reference`
   - Solution: Already fully qualified as `System.Windows.Forms.Screen` in code

## Testing Checklist

### Unit Testing
- [ ] Test `ScreenPositionManager.GetScreenInfo()` returns valid screen info
- [ ] Test `ScreenPositionManager.ValidateAndCorrectPosition()` handles all scenarios
- [ ] Test position saving includes screen information
- [ ] Test position loading validates screen configuration

### Integration Testing

**Test 1: Basic Save/Load**
- [ ] Move overlay to a position
- [ ] Close app
- [ ] Reopen app
- [ ] Verify overlay appears at same position

**Test 2: Single Monitor - No Issues**
- [ ] Position overlay
- [ ] Restart app (same display config)
- [ ] Verify position restored exactly

**Test 3: Laptop Undocking Scenario**
- [ ] Connect laptop to external monitor
- [ ] Move overlay to external monitor
- [ ] Close app
- [ ] Unplug external monitor
- [ ] Reopen app on laptop only
- [ ] Verify overlay appears on laptop screen (proportionally positioned)

**Test 4: Docking Scenario**
- [ ] Position overlay on laptop screen
- [ ] Close app
- [ ] Dock laptop (add external monitors)
- [ ] Reopen app
- [ ] Verify overlay appears on appropriate screen

**Test 5: Resolution Change**
- [ ] Position overlay
- [ ] Close app
- [ ] Change display resolution
- [ ] Reopen app
- [ ] Verify overlay position adjusted proportionally

**Test 6: Monitor Rearrangement**
- [ ] Position overlay on secondary monitor
- [ ] Close app
- [ ] Rearrange monitors in Windows Display Settings
- [ ] Reopen app
- [ ] Verify overlay follows its screen to new position

**Test 7: First Run (No Saved Position)**
- [ ] Delete `%APPDATA%\DeejNG\overlay_position.json`
- [ ] Start app
- [ ] Verify overlay appears at default position

**Test 8: Corrupted Settings**
- [ ] Manually corrupt `overlay_position.json` with invalid JSON
- [ ] Start app
- [ ] Verify app starts successfully with default position

### Debug Output Verification

In Debug builds, verify the following output appears in Visual Studio Output window:

**On App Start**:
```
[ScreenPositionManager] Screen Count: 2
[ScreenPositionManager] Virtual Screen: 0,0 3840x1080
[OverlayPersistence] Loaded position: X=2560, Y=400, Screen=\\.\DISPLAY2
[ScreenPositionManager] Screen found with matching bounds, position is valid
```

**On Position Change**:
```
[MainWindow] SaveOverlayPosition called: X=2560, Y=400
[MainWindow] Saved screen info: Device=\\.\DISPLAY2, Bounds=1920,0,1920,1080
[OverlayPersistence] Position saved to disk
```

**On Display Config Change**:
```
[ScreenPositionManager] Validating position: (2560, 400)
[ScreenPositionManager] Screen '\\.\DISPLAY2' not found, moving to primary
[ScreenPositionManager] Position moved to primary screen: (640, 400)
```

### Release Build Verification

In Release builds, verify:
- [ ] No debug output in console/Output window
- [ ] Application runs without performance issues
- [ ] File size not significantly increased
- [ ] All features work as expected

## Verification Commands

### Check for DEBUG Statements in Release Build
```powershell
# After building Release, check that DEBUG code was removed
$releaseDll = "bin\Release\net9.0-windows\DeejNG.dll"
$strings = [System.Reflection.Assembly]::LoadFile((Resolve-Path $releaseDll))
# No "[ScreenPositionManager]" strings should be in release build
```

### Test File Locations
```powershell
# Verify position file location
$positionFile = "$env:APPDATA\DeejNG\overlay_position.json"
Test-Path $positionFile  # Should exist after first position save
Get-Content $positionFile  # Should show JSON with ScreenDeviceName and ScreenBounds
```

### Sample Valid overlay_position.json
```json
{
  "X": 2560.0,
  "Y": 400.0,
  "ScreenDeviceName": "\\\\.\\DISPLAY2",
  "ScreenBounds": "1920,0,1920,1080",
  "OperatingSystem": "Win32NT 10.0.22631.0",
  "SavedAt": "2025-10-06T14:30:00"
}
```

## Known Limitations

1. **Screen DeviceName May Change**:
   - If monitors are unplugged and replugged in different order
   - System handles this by validating bounds as fallback

2. **Virtual Multi-Desktop Not Supported**:
   - Windows Virtual Desktops treated as single screen
   - Position persists per physical screen configuration

3. **DPI Scaling Not Considered**:
   - Position in pixels, not DPI-independent units
   - Usually not an issue as Windows handles DPI per-monitor

## Success Criteria

✓ Solution compiles without errors in both Debug and Release
✓ No new compiler warnings introduced
✓ All manual tests pass
✓ Debug output present in Debug builds
✓ No debug output in Release builds
✓ Position persists correctly in single-monitor setup
✓ Position adjusts intelligently when displays change
✓ No regressions in existing functionality
✓ Documentation complete and accurate

## Troubleshooting

### Issue: "Screen class not found"
**Solution**: Ensure `System.Windows.Forms` reference exists in project file

### Issue: Position not saving
**Check**:
1. `%APPDATA%\DeejNG` folder has write permissions
2. Debug output shows save attempts
3. JSON file is being created/updated

### Issue: Position always resets to default
**Check**:
1. JSON file exists and is valid
2. ScreenDeviceName matches current display
3. Bounds validation not rejecting position

### Issue: Overlay appears off-screen
**Check**:
1. Virtual screen bounds in debug output
2. Saved position values
3. Screen validation logic triggered correctly

## Post-Implementation Notes

Add any issues encountered during testing here:

- [ ] Issue 1: _________________
  - Resolution: _________________

- [ ] Issue 2: _________________
  - Resolution: _________________

## Sign-Off

- [ ] Code reviewed
- [ ] All tests pass
- [ ] Documentation complete
- [ ] Ready for commit

**Implemented by**: Claude
**Date**: 2025-10-06
**Reviewed by**: ___________
**Date**: ___________
