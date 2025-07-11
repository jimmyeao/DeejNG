# COM Port Selection Bug Fix

## Bug Description
**Issue**: The COM port selection doesn't persist between application restarts. When the user selects COM4 and restarts the application, it defaults back to COM1 (the first available port) instead of remembering the previously selected COM4.

## Root Cause
The issue was caused by incorrect timing in the initialization sequence:

1. `LoadAvailablePorts()` was called before the saved settings were loaded
2. At the time the ComboBox was populated, `_lastConnectedPort` was still empty
3. This caused the ComboBox to default to the first available port (COM1)
4. Later when settings were loaded, the ComboBox selection had already been set

## Solution
The fix involves two changes to `MainWindow.xaml.cs`:

### 1. Added LoadSavedPortName() Method
```csharp
private void LoadSavedPortName()
{
    try
    {
        var settings = LoadSettingsFromDisk();
        if (!string.IsNullOrWhiteSpace(settings?.PortName))
        {
            _lastConnectedPort = settings.PortName;
            Debug.WriteLine($"[Settings] Loaded saved port name: {_lastConnectedPort}");
        }
        else
        {
            Debug.WriteLine("[Settings] No saved port name found");
        }
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"[ERROR] Failed to load saved port name: {ex.Message}");
    }
}
```

### 2. Updated Constructor Initialization Order
```csharp
// OLD ORDER (buggy):
LoadAvailablePorts();  // This ran first, when _lastConnectedPort was empty

// NEW ORDER (fixed):
LoadSavedPortName();   // Load the saved port name first
LoadAvailablePorts();  // Then populate the ComboBox with the correct selection
```

### 3. Enhanced Debug Logging
Added better debug logging to `LoadAvailablePorts()` to help verify the fix:
```csharp
Debug.WriteLine($"[Ports] Current selection: '{currentSelection}', Last connected port: '{_lastConnectedPort}'");
```

## How It Works Now
1. `LoadSavedPortName()` loads the saved COM port from settings into `_lastConnectedPort`
2. `LoadAvailablePorts()` populates the ComboBox and finds the saved port in `_lastConnectedPort`
3. The ComboBox correctly selects the saved port (e.g., COM4) instead of defaulting to the first port

## Testing
To verify the fix:
1. Start the application and select your preferred COM port (e.g., COM4)
2. Verify the connection works
3. Close the application completely
4. Restart the application
5. The ComboBox should now show your previously selected COM port (COM4) instead of COM1

## Debug Output
With the fix, you should see debug output like:
```
[Settings] Loaded saved port name: COM4
[Ports] Found 4 ports: [COM1, COM3, COM4, COM7]
[Ports] Current selection: '', Last connected port: 'COM4'
[Ports] Selected saved port: COM4
```

Instead of the previous buggy output:
```
[Ports] Found 4 ports: [COM1, COM3, COM4, COM7]
[Ports] Current selection: '', Last connected port: ''
[Ports] Selected first available port: COM1
```

This fix ensures that user preferences are properly persisted and restored, improving the user experience by eliminating the need to reselect the COM port every time the application starts.
