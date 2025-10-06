# OVERLAY NOT SAVING - Quick Fix Reference

## Problem
Overlay position NOT saved when app starts minimized  
**Root cause**: MainWindow_Loaded never fires when window is hidden → subscription never happens → position never saved

## Evidence
```
[OverlayService] About to fire PositionChanged event, subscriber count: 0  ← WRONG!
```

## Solution
Move overlay initialization and subscription from `MainWindow_Loaded` to `MainWindow` constructor, BEFORE window is hidden.

## Changes Made

### MainWindow.xaml.cs - Constructor (~line 175)
**ADDED (before hiding window):**
```csharp
// CRITICAL: Initialize overlay and subscribe to events BEFORE potentially hiding the window
_overlayService.Initialize();
_overlayService.PositionChanged += OnOverlayPositionChanged;  // ✓ Subscribe in constructor
_overlayService.UpdateSettings(_settingsManager.AppSettings);

// NOW it's safe to hide
_isInitializing = false;
if (_settingsManager.AppSettings.StartMinimized)
{
    WindowState = WindowState.Minimized;
    Hide();
}
```

### MainWindow.xaml.cs - MainWindow_Loaded (~line 1305)
**REMOVED (no longer needed):**
```csharp
// ❌ REMOVED - was in MainWindow_Loaded
_overlayService.Initialize();
_overlayService.PositionChanged += OnOverlayPositionChanged;
_overlayService.UpdateSettings(_settingsManager.AppSettings);
```

## How to Test

1. Move overlay to X=500, Y=200
2. Close app
3. Enable "Start Minimized"
4. Restart app
5. **Check debug output:**
   - ✅ "Subscribed to OverlayService.PositionChanged in constructor"
   - ✅ "subscriber count: 1" (NOT 0!)
6. Move overlay to X=600, Y=300
7. **Check debug output:**
   - ✅ "OnOverlayPositionChanged called"
   - ✅ "Position saved to disk"
8. Restart minimized
9. **Overlay should be at (600, 300)** ✓

## Expected Debug Output

### After This Fix:
```
[MainWindow] Initializing overlay service in constructor  ← NEW!
[MainWindow] Subscribed to OverlayService.PositionChanged in constructor  ← NEW!
[OverlayService] About to fire PositionChanged event, subscriber count: 1  ← FIXED!
[MainWindow] OnOverlayPositionChanged called: X=128, Y=1039  ← WORKS!
[Overlay] Position saved to disk (debounced)  ← SAVED!
```

### What You Should NOT See:
❌ "subscriber count: 0" when moving overlay  
❌ Position reverting to default on restart  

## Why This Happened

### WPF Window Lifecycle:
- **Constructor**: Always runs ✓
- **OnSourceInitialized**: Always runs ✓
- **Loaded**: Only runs if window is visible ❌

### The Bug:
```
Constructor → Hide() → Loaded NEVER FIRES → Subscription NEVER HAPPENS → Position NEVER SAVED
```

### The Fix:
```
Constructor → Subscribe → Hide() → Subscription ALREADY HAPPENED → Position SAVED
```

## Files Modified
- `MainWindow.xaml.cs` (2 sections changed)

## Combined with Previous Fixes

This fix solves **saving** the position.  
Previous fixes solved **loading** and **applying** the position.

All three fixes together:
1. **Initialization Guard** (FloatingOverlay) - Prevents feedback loops
2. **Subscription Timing** (MainWindow) - Ensures subscription happens
3. **Position Application** (OnSourceInitialized) - Applies saved position correctly

## If It STILL Doesn't Work

Check debug output when moving overlay:
- If you see "subscriber count: 0" → Subscription still not happening
- If you see "subscriber count: 1" but no save → Debounced timer issue
- If position changes aren't appearing in settings.json → File permission issue

The key diagnostic is "subscriber count". If it's > 0, this fix worked.
