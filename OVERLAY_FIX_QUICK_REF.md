# Quick Reference - Overlay Position Fix

## Problem
Overlay position NOT retained when app starts minimized (worked fine when starting normally)

## Root Cause
The `_hasAppliedInitialPosition` flag was being set in `UpdateSettings()` BEFORE `OnSourceInitialized()` could apply the initial position, causing the position to be lost.

## Solution
Let `OnSourceInitialized()` be the ONLY place that sets the `_hasAppliedInitialPosition` flag.

## Changes Made

### 1. FloatingOverlay.xaml.cs - Line ~211
**REMOVED THIS LINE:**
```csharp
_hasAppliedInitialPosition = true; // ❌ REMOVED - was preventing OnSourceInitialized from working
```

**REPLACED WITH:**
```csharp
// DON'T set _hasAppliedInitialPosition here - let OnSourceInitialized handle it ✓
```

### 2. OverlayService.cs - Lines ~215-223
**REMOVED THIS CODE BLOCK:**
```csharp
// ❌ REMOVED - was conflicting with OnSourceInitialized
_overlay.Loaded += (s, e) =>
{
    _overlay.Left = X;
    _overlay.Top = Y;
};
```

**REPLACED WITH:**
```csharp
// Position is applied in FloatingOverlay.OnSourceInitialized - don't apply it here
// to avoid timing conflicts that cause position to be lost ✓
```

### 3. OverlayService.cs - Lines ~159-170
**ADDED SAFETY CHECK:**
```csharp
if (_overlay.IsLoaded)  // ✓ ADDED - only apply to fully loaded overlay
{
    _overlay.Left = X;
    _overlay.Top = Y;
}
```

## How to Test

1. **Move overlay** to a specific position (e.g., X=500, Y=200)
2. **Close app**
3. **Enable "Start Minimized"** in settings
4. **Start app**
5. **Verify** overlay appears at X=500, Y=200 ✓

## Expected Debug Output

When starting minimized, you should see:
```
[Overlay] Constructor: Stored initial position X=686, Y=1
[OverlayService] Skipping position application - overlay not loaded yet
[Overlay] OnSourceInitialized: Applying initial position X=686, Y=1
[Overlay] OnSourceInitialized: Position applied successfully. Window: Left=686, Top=1
```

**Key phrase to look for:** "Position applied successfully" in OnSourceInitialized

## Files Modified
- `Views/FloatingOverlay.xaml.cs` (1 section removed, 1 enhanced)
- `Core/Services/OverlayService.cs` (1 section removed, 1 section modified)

## Architecture Benefits
✓ **Single responsibility**: OnSourceInitialized handles initial position  
✓ **No timing conflicts**: Clear separation of initialization vs. runtime updates  
✓ **Debug-wrapped**: All debug code wrapped in `#if DEBUG` directives  
✓ **Maintainable**: Simple, clean logic that's easy to understand  

## If It Still Doesn't Work
Check the debug output for:
- "Skipping position application (already applied: True..." ← Should NOT see this anymore!
- "Position applied successfully" ← MUST see this in OnSourceInitialized

If you see the "already applied: True" message, the flag is still being set somewhere else.
