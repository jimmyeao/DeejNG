# Overlay Position Retention Fix - Minimized Startup Issue

## Date: October 6, 2025

## Problem Statement
When the app started minimized, the overlay position was NOT retained between runs, even though settings were being saved correctly. Starting the app normally worked fine.

## Root Cause Analysis

The issue was a **timing conflict** in position application during window initialization:

### The Problematic Sequence:
1. App starts minimized → MainWindow is hidden
2. Settings loaded with saved position (e.g., X=686, Y=1)
3. `OverlayService.UpdateSettings()` called with settings
4. Later, overlay creation triggered
5. `FloatingOverlay` constructor stores initial position as `_initialX`/`_initialY`
6. **IMMEDIATELY after creation**, `OverlayService` calls `_overlay.UpdateSettings()`
7. `UpdateSettings()` calls `SetPrecisePosition()` and **sets `_hasAppliedInitialPosition = true`**
8. `OnSourceInitialized` fires, but **skips position application because flag is already true!**
9. Multiple conflicting position applications caused position to be lost

### The Key Problem:
The `_hasAppliedInitialPosition` flag was being set in the wrong place - in `UpdateSettings()` instead of in `OnSourceInitialized()`. This prevented the reliable initialization-time position application from working.

## The Solution

### Changes Made:

#### 1. **FloatingOverlay.xaml.cs** - Fixed flag management
**Removed** the line that set `_hasAppliedInitialPosition = true` in `UpdateSettings()`:
```csharp
// OLD (WRONG):
SetPrecisePosition(settings.OverlayX, settings.OverlayY);
_hasAppliedInitialPosition = true; // ❌ This prevented OnSourceInitialized from working

// NEW (CORRECT):
SetPrecisePosition(settings.OverlayX, settings.OverlayY);
// DON'T set _hasAppliedInitialPosition here - let OnSourceInitialized handle it ✓
```

The flag should **ONLY** be set in `OnSourceInitialized`, after successfully applying the initial position there.

#### 2. **OverlayService.cs** - Removed conflicting position application
**Removed** the `Loaded` event handler that was applying position redundantly:
```csharp
// OLD (CAUSED CONFLICTS):
_overlay.Loaded += (s, e) =>
{
    _overlay.Left = X;
    _overlay.Top = Y; // ❌ This conflicted with OnSourceInitialized
};

// NEW (LET OnSourceInitialized HANDLE IT):
// Position is applied in FloatingOverlay.OnSourceInitialized - don't apply it here
// to avoid timing conflicts that cause position to be lost ✓
```

#### 3. **OverlayService.cs** - Added safety check in UpdateSettings
**Added** a check to only apply position to an already-loaded overlay:
```csharp
if (_overlay != null)
{
    _overlay.UpdateSettings(settings);
    
    // CRITICAL: Only apply position if window is fully loaded
    if (_overlay.IsLoaded)
    {
        _overlay.Left = X;
        _overlay.Top = Y; // ✓ Safe to apply now
    }
    else
    {
        // Skip - will be applied in OnSourceInitialized
    }
}
```

#### 4. **Enhanced Debug Logging**
Added comprehensive logging to track position application:
- Shows when position is applied in `OnSourceInitialized`
- Shows when application is skipped (with reason)
- Shows actual window coordinates after application

## How This Fixes the Problem

### New Sequence (Minimized Startup):
1. App starts minimized
2. Settings loaded (X=686, Y=1)
3. `OverlayService.UpdateSettings()` called - stores X/Y in service properties
4. Overlay created later with `settings` containing X=686, Y=1
5. Constructor stores `_initialX=686`, `_initialY=1` ✓
6. `UpdateSettings()` called on new overlay - applies position but **does NOT set flag** ✓
7. `OnSourceInitialized` fires - **applies position** and **NOW sets flag** ✓
8. Position is correctly retained! ✓

### Key Improvements:
- **Single Source of Truth**: `OnSourceInitialized` is the ONLY place that sets `_hasAppliedInitialPosition`
- **No Timing Conflicts**: Other methods don't interfere with initial position application
- **Clear Separation**: Initial position (OnSourceInitialized) vs. runtime updates (UpdateSettings on loaded overlay)

## Testing Instructions

### Test 1: Minimized Startup
1. Move overlay to a specific position (e.g., X=500, Y=300)
2. Close the app
3. Check "Start Minimized" in settings
4. Start the app
5. **Expected**: Overlay appears at X=500, Y=300 ✓

### Test 2: Normal Startup
1. Move overlay to a different position (e.g., X=700, Y=100)
2. Close the app
3. Uncheck "Start Minimized"
4. Start the app
5. **Expected**: Overlay appears at X=700, Y=100 ✓

### Test 3: Multi-Monitor
1. Move overlay to secondary monitor
2. Close the app
3. Start app (minimized or normal)
4. **Expected**: Overlay appears on correct monitor at saved position ✓

### Test 4: Monitor Configuration Change
1. Save overlay position on 2-monitor setup
2. Disconnect secondary monitor
3. Start app
4. **Expected**: Overlay repositioned to primary monitor (validated safely) ✓

## Debug Log Output

### Successful Position Application (Minimized Start):
```
[Settings] Loaded from disk - OverlayPosition: (686, 1)
[OverlayService] Overlay not created yet - position will be applied when created: (686, 1)
[OverlayService] Creating new overlay instance at position (686, 1)
[Overlay] Constructor: Stored initial position X=686, Y=1
[OverlayService] Creating standalone overlay (MainWindow hidden/minimized)
[Overlay] UpdateSettings: Applying position X=686, Y=1
[OverlayService] Skipping position application - overlay not loaded yet (will be applied in OnSourceInitialized)
[Overlay] OnSourceInitialized: Applying initial position X=686, Y=1
[Overlay] OnSourceInitialized: Position applied successfully. Window: Left=686, Top=1
[Overlay] Applied WS_EX_NOACTIVATE
```

### Key Indicators of Success:
- ✓ "Stored initial position X=686, Y=1"
- ✓ "Skipping position application - overlay not loaded yet"
- ✓ "Position applied successfully. Window: Left=686, Top=1"

## Architecture Notes

### Compiler Directives
All debug statements are wrapped in `#if DEBUG` directives, ensuring:
- **Development**: Full diagnostic logging available
- **Release**: No debug overhead or log clutter

### Single Responsibility Principle
- **FloatingOverlay.OnSourceInitialized**: Handles initial window position setup
- **FloatingOverlay.UpdateSettings**: Handles runtime setting changes (but not initial setup)
- **OverlayService.UpdateSettings**: Coordinates settings between service and overlay window

### Separation of Concerns
- **Position Storage**: AppSettings (saved to disk)
- **Position Validation**: ScreenPositionManager (multi-monitor handling)
- **Position Application**: FloatingOverlay (window-level control)
- **Position Coordination**: OverlayService (service-level orchestration)

## Files Modified

1. `Views/FloatingOverlay.xaml.cs`
   - Removed `_hasAppliedInitialPosition = true` from `UpdateSettings()`
   - Enhanced debug logging in `OnSourceInitialized()`

2. `Core/Services/OverlayService.cs`
   - Removed redundant `Loaded` event handler
   - Added `IsLoaded` check before applying position in `UpdateSettings()`
   - Enhanced debug logging throughout

## Verification Checklist

- [x] Position retained when starting minimized
- [x] Position retained when starting normally
- [x] No timing conflicts during initialization
- [x] Debug logging shows clear sequence of events
- [x] Multi-monitor support still works
- [x] All debug code wrapped in `#if DEBUG` directives
- [x] Code follows single responsibility principle
- [x] Clear separation between initialization and runtime updates

## Summary

**The fix was simple but critical**: Let `OnSourceInitialized` be the sole authority for applying the initial window position. Don't let other methods interfere by setting the `_hasAppliedInitialPosition` flag prematurely or by applying position before the window is fully initialized.

This ensures reliable position retention regardless of whether the app starts minimized or normally, and maintains clean architectural separation between initialization-time and runtime position management.
