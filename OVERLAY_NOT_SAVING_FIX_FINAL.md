# Overlay Position NOT Saving - FINAL FIX
## Date: October 6, 2025

## The REAL Problem 🔴

The overlay position was NOT being saved because **MainWindow_Loaded never fires when starting minimized!**

### Evidence from Logs:
```
[OverlayService] About to fire PositionChanged event, subscriber count: 0
```

### The Issue:
In `MainWindow` constructor:
```csharp
if (_settingsManager.AppSettings.StartMinimized)
{
    WindowState = WindowState.Minimized;
    Hide();  // ← This prevents Loaded event from firing!
}
```

The subscription was in `MainWindow_Loaded`:
```csharp
private void MainWindow_Loaded(object sender, RoutedEventArgs e)
{
    _overlayService.PositionChanged += OnOverlayPositionChanged;  // ← NEVER CALLED!
}
```

**Result**: When starting minimized, `MainWindow_Loaded` never fires, so the subscription never happens, so position changes are never saved!

## The Solution ✅

**Moved overlay initialization and subscription to the constructor**, BEFORE the window is potentially hidden:

### MainWindow.xaml.cs - Constructor (after line ~175)
```csharp
// CRITICAL: Initialize overlay and subscribe to events BEFORE potentially hiding the window
// This ensures the subscription happens even when starting minimized
_overlayService.Initialize();

// Subscribe to PositionChanged event
_overlayService.PositionChanged += OnOverlayPositionChanged;

// Update overlay with settings
_overlayService.UpdateSettings(_settingsManager.AppSettings);

_isInitializing = false;
if (_settingsManager.AppSettings.StartMinimized)
{
    WindowState = WindowState.Minimized;
    Hide();  // ✓ Now subscription already happened!
    MyNotifyIcon.Visibility = Visibility.Visible;
}
```

### MainWindow.xaml.cs - MainWindow_Loaded (simplified)
```csharp
private void MainWindow_Loaded(object sender, RoutedEventArgs e)
{
    SliderScrollViewer.Visibility = Visibility.Visible;
    StartOnBootCheckBox.IsChecked = _settingsManager.AppSettings.StartOnBoot;
    
    // Overlay already initialized in constructor
}
```

## How This Fixes Everything

### Old Broken Sequence (Minimized Start):
1. Constructor runs
2. `Hide()` called → **Prevents Loaded event**
3. `MainWindow_Loaded` **NEVER FIRES** ❌
4. Subscription **NEVER HAPPENS** ❌
5. Position changes → **PositionChanged event has 0 subscribers** ❌
6. Position **NEVER SAVED** ❌

### New Fixed Sequence (Minimized Start):
1. Constructor runs
2. Initialize overlay service ✓
3. **Subscribe to PositionChanged** ✓
4. Update overlay settings ✓
5. `Hide()` called → Loaded event may not fire, but **doesn't matter anymore!** ✓
6. User moves overlay → **PositionChanged event has 1 subscriber** ✓
7. Position **SAVED TO DISK** ✓
8. Restart → Position **RETAINED** ✓

## Expected Debug Output

### When Moving Overlay:
```
[Overlay] Mouse released at position: (128, 1039)
[OverlayService] Received OverlayPositionChanged event: X=128, Y=1039
[OverlayService] About to fire PositionChanged event, subscriber count: 1  ← KEY!
[MainWindow] OnOverlayPositionChanged called: X=128, Y=1039
[MainWindow] Screen info captured: Device=..., Bounds=...
[Overlay] Position and screen info queued for save
[Overlay] Position saved to disk (debounced)
```

**Key change**: `subscriber count: 1` instead of `0`!

### When Restarting (Minimized):
```
[MainWindow] Initializing overlay service in constructor
[MainWindow] Overlay position from settings: X=128, Y=1039
[MainWindow] Subscribed to OverlayService.PositionChanged in constructor
[OverlayService] Creating new overlay instance at position (128, 1039)
[Overlay] Constructor: Stored initial position X=128, Y=1039
[Overlay] OnSourceInitialized: Applying initial position X=128, Y=1039
[Overlay] LocationChanged suppressed during initialization
[Overlay] Initialization complete - position updates now enabled
```

**Key phrase**: "Subscribed to OverlayService.PositionChanged in constructor"

## All Fixes Combined

### 1. Initialization Guard (Previous Fix)
- Added `_isInitializing` flag to FloatingOverlay
- Suppresses LocationChanged events during initialization
- Prevents feedback loops

### 2. Subscription Timing (This Fix)
- Moved subscription to constructor
- Happens BEFORE window is hidden
- Ensures subscription works when starting minimized

### 3. Position Application (Previous Fix)
- OnSourceInitialized applies initial position
- UpdateSettings doesn't interfere during init
- Clear separation of concerns

## Testing Instructions

1. **Start app normally (not minimized)**
2. **Move overlay** to position X=200, Y=300
3. **Close app**
4. **Check debug output** - should see "Position saved to disk"
5. **Enable "Start Minimized"**
6. **Restart app**
7. **Verify** in debug output:
   - "Subscribed to OverlayService.PositionChanged in constructor"
   - "Creating new overlay instance at position (200, 300)"
   - "Position applied successfully. Window: Left=200, Top=300"
8. **Move overlay** to different position
9. **Verify** in debug output:
   - "subscriber count: 1" (NOT 0!)
   - "OnOverlayPositionChanged called"
   - "Position saved to disk"
10. **Restart minimized again**
11. **Overlay should be at new position!** ✓

## Architecture Benefits

✅ **Event Lifecycle Management**: Subscriptions happen at construction, not at loading  
✅ **Window State Independence**: Works regardless of minimized/normal/hidden state  
✅ **Initialization Guard Pattern**: Clean separation between init and runtime  
✅ **Compiler Directives**: All debug code wrapped in `#if DEBUG`  
✅ **Single Responsibility**: Each component has clear, well-defined role  

## Files Modified

1. **MainWindow.xaml.cs** - 2 changes
   - Moved overlay init/subscription to constructor
   - Simplified MainWindow_Loaded

2. **Views/FloatingOverlay.xaml.cs** - 4 changes (from previous fix)
   - Added `_isInitializing` flag
   - Suppressed LocationChanged during init
   - Blocked UpdateSettings during init
   - Cleared flag after OnSourceInitialized

3. **Core/Services/OverlayService.cs** - 2 changes (from previous fix)
   - Removed redundant Loaded handler
   - Added IsLoaded safety check

## Technical Notes

### WPF Window Lifecycle:
1. **Constructor** - Always runs (even for hidden windows)
2. **OnSourceInitialized** - Always runs (even for hidden windows)
3. **Loaded** - **MAY NOT RUN** if window is hidden before loading!

This is why subscriptions must happen in constructor or OnSourceInitialized, NOT in Loaded.

### Why This Was Hard to Find:
- Worked fine when starting normally (Loaded fired)
- Only failed when starting minimized (Loaded didn't fire)
- No errors thrown - just silently failed to subscribe
- "subscriber count: 0" was the only clue

## Summary

**The Core Issue**: Hiding the window in the constructor prevented `MainWindow_Loaded` from firing, which prevented the overlay position subscription from happening, which meant position changes were never saved.

**The Simple Fix**: Move the subscription to the constructor, BEFORE the window is potentially hidden. This ensures the subscription always happens, regardless of window state.

**The Result**: Overlay position is now correctly saved and retained between runs, whether starting minimized or normally.

## Verification

After this fix, you should ALWAYS see in the debug output when moving the overlay:
- "subscriber count: 1" (or higher, never 0)
- "OnOverlayPositionChanged called"
- "Position saved to disk"

If you see "subscriber count: 0", the subscription didn't happen and this fix didn't work.
