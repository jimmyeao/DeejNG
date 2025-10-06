# Quick Troubleshooting Guide

## Issue: UI Still Laggy After Sleep

### Symptoms
- Application slow/unresponsive after waking from sleep
- Need to manually disconnect/reconnect serial port

### Debug Steps
1. **Check Power Events Are Firing**
   ```
   Look for in Debug Output:
   [Power] System suspending
   [Power] System resuming
   [Power] System resumed
   ```
   
   ❌ **If missing:** Power management service not initialized
   - Check ServiceLocator.Configure() is called in App.xaml.cs OnStartup
   - Verify power event subscriptions in MainWindow constructor

2. **Check Audio Device Refresh**
   ```
   Look for in Debug Output:
   [Power] Audio devices refreshed
   ```
   
   ❌ **If missing:** RefreshAudioDevices() not being called
   - Verify OnSystemResumed handler exists
   - Check Dispatcher.BeginInvoke is executing

3. **Check Timer Restart**
   ```
   Look for in Debug Output:
   [Power] Starting serial reconnection after resume
   [Power] Volume application re-enabled
   ```
   
   ❌ **If missing:** Timer coordinator not restarting properly
   - Verify StartMeters() and StartSessionCache() are called
   - Check _allowVolumeApplication flag is re-enabled

### Solutions

**Solution 1: Increase Stabilization Delay**
```csharp
// In PowerManagementService.cs, change:
private const int RESUME_STABILIZATION_DELAY_MS = 2000; // Increase to 3000 or 5000
```

**Solution 2: Force Audio Device Rebuild**
```csharp
// In RefreshAudioDevices(), add:
_deviceEnumerator = new MMDeviceEnumerator(); // Force new enumerator
```

**Solution 3: Clear All COM References**
```csharp
// In OnSystemResuming(), add:
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
```

---

## Issue: Overlay Position Not Persisting

### Symptoms
- Overlay resets to default position after restart
- Position lost after sleep/wake cycle
- Happens specifically on Windows Server 2022

### Debug Steps

1. **Check Position File Exists**
   ```
   Navigate to: %APPDATA%\DeejNG\
   Look for: overlay_position.json
   ```
   
   ❌ **If missing:** Position not being saved
   - Check write permissions on AppData folder
   - Verify OverlayPositionPersistenceService is initialized

2. **Check Position Saves**
   ```
   Look for in Debug Output:
   [Overlay] Position updated via service: X=1450.0, Y=300.5
   [OverlayPersistence] Position queued for save
   [OverlayPersistence] Position saved to disk
   ```
   
   ❌ **If missing:** Position change events not firing
   - Verify OnOverlayPositionChanged is subscribed
   - Check SavePosition() is being called

3. **Check Position Loads**
   ```
   Look for in Debug Output (on startup):
   [Startup] Loaded overlay position from persistence: (1450.0, 300.5)
   [OverlayPersistence] Loaded position: X=1450.0, Y=300.5
   ```
   
   ❌ **If missing:** Position not loading on startup
   - Verify LoadPosition() is called in MainWindow_Loaded
   - Check file permissions (must be readable)

4. **Inspect Position File**
   ```powershell
   # Open in notepad
   notepad %APPDATA%\DeejNG\overlay_position.json
   
   # Should contain:
   {
     "X": 1450.0,
     "Y": 300.5,
     "OperatingSystem": "Win32NT 10.0.20348.0",
     "SavedAt": "2025-01-15T14:23:45.1234567-05:00"
   }
   ```
   
   ❌ **If corrupted:** Delete file and restart app

### Solutions

**Solution 1: Force Synchronous Save on Move**
```csharp
// In OnOverlayPositionChanged, change:
_overlayPositionService?.SavePosition(e.X, e.Y); // Async with debouncing

// To:
Task.Run(async () => await _overlayPositionService.ForceSaveAsync()).Wait(100); // Force immediate
```

**Solution 2: Increase Periodic Save Frequency**
```csharp
// In TimerCoordinator.cs, change:
Interval = TimeSpan.FromSeconds(5) // Change to 2 seconds for more frequent saves
```

**Solution 3: Disable Validation for Server OS**
```csharp
// In OverlayPositionPersistenceService.LoadPosition(), add:
if (position != null)
{
    // Skip validation on Server OS
    if (Environment.OSVersion.Version.Build >= 20000) // Server 2022
    {
        _currentPosition = position;
        return position;
    }
    
    // Normal validation for client OS
    if (!IsPositionValid(position.X, position.Y))
    {
        return GetDefaultPosition();
    }
}
```

**Solution 4: Fallback to Registry Storage**
```csharp
// Alternative storage if file system has issues
// In OverlayPositionPersistenceService, add registry backup:
private void SaveToRegistry(double x, double y)
{
    using (var key = Registry.CurrentUser.CreateSubKey(@"Software\DeejNG"))
    {
        key.SetValue("OverlayX", x);
        key.SetValue("OverlayY", y);
    }
}
```

---

## Issue: Serial Port Not Reconnecting After Sleep

### Symptoms
- Serial port disconnected after wake
- Auto-reconnect not working
- Manual reconnect required

### Debug Steps

1. **Check Disconnect Detection**
   ```
   Look for in Debug Output:
   [Serial] Disconnection detected
   ```
   
   ✅ **If present:** Disconnect properly detected

2. **Check Reconnect Attempts**
   ```
   Look for in Debug Output:
   [Power] Starting serial reconnection after resume
   [SerialReconnect] Attempting to reconnect...
   ```
   
   ❌ **If missing:** Reconnect timer not started
   - Check ShouldAttemptReconnect() returns true
   - Verify _manualDisconnect flag is false

3. **Check Port Availability**
   ```
   Look for in Debug Output:
   [Ports] Found X ports: [COM3, COM4, ...]
   ```
   
   ❌ **If port missing:** Hardware not ready after wake
   - Increase stabilization delay
   - Add retry logic with exponential backoff

### Solutions

**Solution 1: Add Retry Delay After Resume**
```csharp
// In OnSystemResumed, change:
if (!_serialManager.IsConnected && _serialManager.ShouldAttemptReconnect())
{
    // Add delay before starting reconnect
    var reconnectDelay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
    reconnectDelay.Tick += (s, args) =>
    {
        reconnectDelay.Stop();
        _timerCoordinator.StartSerialReconnect();
    };
    reconnectDelay.Start();
}
```

**Solution 2: Reset Serial Manager on Resume**
```csharp
// In OnSystemResumed, add before reconnect:
_serialManager.HandleSerialDisconnection(); // Force clean state
```

**Solution 3: Check USB Power Management**
```
Windows Settings:
1. Device Manager → USB Controllers
2. Properties → Power Management
3. Uncheck "Allow computer to turn off this device to save power"
```

---

## Issue: Memory Leak After Multiple Sleep/Wake Cycles

### Symptoms
- Memory usage grows after each sleep/wake
- Application becomes slower over time

### Debug Steps

1. **Monitor Memory Usage**
   - Task Manager → Performance → Memory
   - Note baseline memory usage
   - Sleep/wake 10 times
   - Check if memory increased significantly

2. **Check COM Object Cleanup**
   ```
   Look for in Debug Output:
   [Cleanup] Released session COM object, final ref count: 0
   ```
   
   ❌ **If ref count > 0:** COM objects not fully released

### Solutions

**Solution 1: Force GC on Resume**
```csharp
// In RefreshAudioDevices, add at end:
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
```

**Solution 2: Clear Event Handlers**
```csharp
// In OnSystemSuspending, add:
CleanupEventHandlers(); // Remove stale handlers before sleep
```

**Solution 3: Rebuild Session Cache**
```csharp
// In OnSystemResumed, add:
_sessionIdCache.Clear();
_registeredHandlers.Clear();
UpdateSessionCache(); // Rebuild from scratch
```

---

## Issue: Overlay Appears on Wrong Monitor

### Symptoms
- Overlay shows on wrong screen after restart
- Multi-monitor setup not handled correctly

### Debug Steps

1. **Check Saved Position**
   ```json
   // In overlay_position.json:
   {
     "X": -1920.0,  // Negative = secondary monitor to left
     "Y": 100.0
   }
   ```

2. **Check Virtual Screen Bounds**
   ```csharp
   // Add debug output in IsPositionValid:
   Debug.WriteLine($"Virtual Screen: {SystemParameters.VirtualScreenLeft}, {SystemParameters.VirtualScreenTop}, {SystemParameters.VirtualScreenWidth}, {SystemParameters.VirtualScreenHeight}");
   ```

### Solutions

**Solution 1: Store Monitor Index**
```csharp
// Modify OverlayPositionData:
public class OverlayPositionData
{
    public double X { get; set; }
    public double Y { get; set; }
    public int MonitorIndex { get; set; } // NEW
    public string OperatingSystem { get; set; }
    public DateTime SavedAt { get; set; }
}
```

**Solution 2: Detect Monitor Changes**
```csharp
// In LoadPosition, add:
if (Screen.AllScreens.Length != savedPosition.MonitorCount)
{
    // Monitor config changed, use default position
    return GetDefaultPosition();
}
```

---

## Emergency Fixes

### Complete Position Reset
```powershell
# Delete position file and restart app
del %APPDATA%\DeejNG\overlay_position.json
```

### Disable Power Management (Temporary)
```csharp
// In MainWindow constructor, comment out:
// _powerManagementService.SystemSuspending += OnSystemSuspending;
// _powerManagementService.SystemResuming += OnSystemResuming;
// _powerManagementService.SystemResumed += OnSystemResumed;
```

### Force Clean State
```csharp
// In OnSystemResumed, add aggressive cleanup:
_audioService?.ForceCleanup();
AudioUtilities.ForceCleanup();
_deviceManager.RefreshCaches();
GC.Collect(2, GCCollectionMode.Forced);
```

---

## Getting Help

### Collect Debug Information
```powershell
# Run app and capture debug output
# File → Save Output As → debug_output.txt
```

### Check System Information
```powershell
# Get Windows version
winver

# Get .NET version
dotnet --version

# Check serial ports
Get-WmiObject Win32_SerialPort | Select Name, DeviceID
```

### Report Issue
Include in bug report:
1. Debug output (debug_output.txt)
2. Position file (%APPDATA%\DeejNG\overlay_position.json)
3. Windows version (winver)
4. Steps to reproduce
5. Expected vs actual behavior

---

## Performance Optimization

### If Position Saves Too Frequent
```csharp
// Increase debounce delay in OverlayPositionPersistenceService:
private const int DEBOUNCE_MS = 300; // Increase to 500 or 1000
```

### If Resume Takes Too Long
```csharp
// Reduce stabilization delay in PowerManagementService:
private const int RESUME_STABILIZATION_DELAY_MS = 2000; // Reduce to 1000
```

### If Memory Usage High
```csharp
// Reduce periodic save frequency in TimerCoordinator:
Interval = TimeSpan.FromSeconds(5) // Increase to 10 or 30
```

---

**All issues should be resolvable with these troubleshooting steps.**
