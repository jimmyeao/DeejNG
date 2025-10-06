# Overlay Settings Fix - Screen Memory Issue

## Problem
The floating overlay window was not remembering its screen between runs. Position (X, Y) was being saved, but not which monitor it was on.

## Root Cause
The application had **two separate storage systems** running in parallel:
1. **`settings.json`** via `AppSettingsManager` - Had screen device & bounds properties defined but incomplete usage
2. **`overlay_position.json`** via `OverlayPositionPersistenceService` - Separate file that stored position data

When loading at startup, the code loaded from `overlay_position.json` but only copied X and Y coordinates, **not** the screen device information.

## Solution
**Consolidated all overlay settings into the single `settings.json` file** as requested.

### Changes Made

#### 1. **MainWindow.xaml.cs**
- **Removed** `_overlayPositionService` field declaration (line ~81)
- **Removed** service initialization in constructor (line ~103)
- **Removed** `PeriodicPositionSave` timer event registration (line ~138)
- **Removed** `StartPeriodicPositionSave()` call (line ~193)
- **Updated** `MainWindow_Loaded` to use settings from `AppSettings` directly instead of loading from `overlay_position.json`
- **Updated** `OnOverlayPositionChanged` to only save to `settings.json` (removed dual save)
- **Updated** `SaveOverlayPosition` to only save to `settings.json`
- **Updated** `OnSystemSuspending` to save settings before sleep (removed reference to `_overlayPositionService`)
- **Updated** `OnClosed` to save settings on shutdown (removed reference to `_overlayPositionService`)
- **Removed** `_overlayPositionService.Dispose()` call

#### 2. **ServiceLocator.cs**
- **Removed** registration of `OverlayPositionPersistenceService` from the Configure() method

### What Now Happens

#### On Load:
1. `LoadSettingsWithoutSerialConnection()` loads ALL settings from `settings.json` including:
   - `OverlayX`
   - `OverlayY`
   - `OverlayScreenDevice` ✓ (now remembered!)
   - `OverlayScreenBounds` ✓ (now remembered!)
2. `MainWindow_Loaded` uses these settings directly
3. `OverlayService.UpdateSettings()` applies the screen information for multi-monitor support

#### On Save:
1. When overlay position changes, `OnOverlayPositionChanged` is triggered
2. Screen information is captured via `ScreenPositionManager.GetScreenInfo(x, y)`
3. Position AND screen info saved to `AppSettings`
4. Debounced save to `settings.json` via `_timerCoordinator.TriggerPositionSave()`

### Files That Can Be Deleted (Optional)
The following are no longer needed but can remain for backwards compatibility:
- `Core/Services/OverlayPositionPersistenceService.cs` (no longer used)
- Any existing `overlay_position.json` files in user's AppData

### Benefits
✅ **Single source of truth** - All settings in one `settings.json` file  
✅ **Screen memory works** - Monitor device name and bounds now saved and restored  
✅ **Cleaner architecture** - Eliminated redundant persistence layer  
✅ **Easier debugging** - One file to check for all settings  
✅ **Better multi-monitor support** - Full screen context preserved

### Debug Statements
All changes maintain existing debug statements wrapped in `#if DEBUG` compiler directives as requested, so they won't be compiled into release binaries.

## Testing Checklist
- [ ] Move overlay to secondary monitor
- [ ] Close application
- [ ] Reopen application
- [ ] Verify overlay appears on correct monitor
- [ ] Check `settings.json` contains `OverlayScreenDevice` and `OverlayScreenBounds`
- [ ] Test with display configuration changes (unplug/replug monitors)
