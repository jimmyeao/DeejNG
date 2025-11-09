using DeejNG.Classes;
using DeejNG.Models;
using DeejNG.Core.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeejNG.Services
{
    /// <summary>
    /// FIXED VERSION: Enhanced for Server 2022 / Windows 10 compatibility
    /// Key fixes:
    /// 1. Multiple fallback paths for settings storage
    /// 2. Write permission testing before using paths
    /// 3. Better error logging for diagnostics
    /// 4. Explicit sync/flush for file operations
    /// </summary>
    public class AppSettingsManager
    {
        private readonly object _settingsLock = new object();
        private DateTime _lastSettingsSave = DateTime.MinValue;

        private string _cachedSettingsPath = null;

        /// <summary>
        /// Gets the settings file path with fallback options for compatibility with Server OS
        /// </summary>
        private string SettingsPath
        {
            get
            {
                if (_cachedSettingsPath != null)
                    return _cachedSettingsPath;

                // Try LocalApplicationData first (standard location)
                string primaryPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeejNG",
                    "settings.json");

#if DEBUG
                Debug.WriteLine($"[Settings] Primary path: {primaryPath}");
#endif

                // Check if primary path is writable
                if (IsPathWritable(Path.GetDirectoryName(primaryPath)))
                {
                    _cachedSettingsPath = primaryPath;
#if DEBUG
                    Debug.WriteLine($"[Settings] Using primary path (writable)");
#endif
                    return _cachedSettingsPath;
                }

                // Fallback 1: ApplicationData (Roaming)
                string fallback1 = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DeejNG",
                    "settings.json");

#if DEBUG
                Debug.WriteLine($"[Settings] Primary path not writable, trying fallback 1: {fallback1}");
#endif

                if (IsPathWritable(Path.GetDirectoryName(fallback1)))
                {
                    _cachedSettingsPath = fallback1;
#if DEBUG
                    Debug.WriteLine($"[Settings] Using fallback 1 (Roaming - writable)");
#endif
                    return _cachedSettingsPath;
                }

                // Fallback 2: Current user's Documents folder
                string fallback2 = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "DeejNG",
                    "settings.json");

#if DEBUG
                Debug.WriteLine($"[Settings] Fallback 1 not writable, trying fallback 2: {fallback2}");
#endif

                if (IsPathWritable(Path.GetDirectoryName(fallback2)))
                {
                    _cachedSettingsPath = fallback2;
#if DEBUG
                    Debug.WriteLine($"[Settings] Using fallback 2 (Documents - writable)");
#endif
                    return _cachedSettingsPath;
                }

                // Last resort: Application directory (might need admin rights but better than nothing)
                string fallback3 = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "settings.json");

#if DEBUG
                Debug.WriteLine($"[Settings] Using last resort fallback 3: {fallback3}");
#endif

                _cachedSettingsPath = fallback3;
                return _cachedSettingsPath;
            }
        }

        /// <summary>
        /// Checks if a directory path is writable
        /// </summary>
        private bool IsPathWritable(string directoryPath)
        {
            try
            {
                if (string.IsNullOrEmpty(directoryPath))
                    return false;

                // Try to create directory if it doesn't exist
                if (!Directory.Exists(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                }

                // Test write access
                string testFile = Path.Combine(directoryPath, $".write_test_{Guid.NewGuid()}.tmp");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                return true;
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[Settings] Path not writable ({directoryPath}): {ex.Message}");
#endif
                return false;
            }
        }

        // Rename to avoid conflict with DeejNG.Settings class
        public AppSettings AppSettings { get; set; } = new AppSettings();

        public event Action<AppSettings> SettingsChanged;

        /// <summary>
        /// Gets the settings file path (exposed for ProfileManager)
        /// </summary>
        public string GetSettingsPath() => SettingsPath;

        public AppSettings LoadSettingsFromDisk()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
#if DEBUG
                    Debug.WriteLine($"[Settings] Loading from: {SettingsPath}");
#endif

                    AppSettings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

#if DEBUG
                    Debug.WriteLine($"[Settings] Loaded from disk - OverlayEnabled: {AppSettings.OverlayEnabled}");
                    Debug.WriteLine($"[Settings] Loaded from disk - OverlayPosition: ({AppSettings.OverlayX}, {AppSettings.OverlayY})");
                    Debug.WriteLine($"[Settings] Loaded from disk - OverlayScreen: {AppSettings.OverlayScreenDevice}");
                    Debug.WriteLine($"[Settings] Loaded from disk - OverlayOpacity: {AppSettings.OverlayOpacity}");
                    Debug.WriteLine($"[Settings] Loaded from disk - OverlayTimeoutSeconds: {AppSettings.OverlayTimeoutSeconds}");
#endif

                    // Validate and correct overlay position for current display configuration
                    ValidateAndCorrectOverlayPosition();

                    return AppSettings;
                }
                else
                {
#if DEBUG
                    Debug.WriteLine($"[Settings] Settings file does not exist: {SettingsPath}");
#endif
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[Settings] Error loading settings: {ex.Message}");
                Debug.WriteLine($"[Settings] Stack trace: {ex.StackTrace}");
#endif
            }

            AppSettings = new AppSettings();
#if DEBUG
            Debug.WriteLine("[Settings] Using default AppSettings");
#endif
            return AppSettings;
        }

        public void SaveSettings(AppSettings newSettings)
        {
            lock (_settingsLock)
            {
                try
                {
                    AppSettings = newSettings;

                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true
                    };
                    var json = JsonSerializer.Serialize(AppSettings, options);

                    // Ensure directory exists
                    var dir = Path.GetDirectoryName(SettingsPath);
                    if (!Directory.Exists(dir) && dir != null)
                    {
                        Directory.CreateDirectory(dir);
#if DEBUG
                        Debug.WriteLine($"[Settings] Created directory: {dir}");
#endif
                    }

                    // FIX: Write with explicit flush for Server 2022 compatibility
                    using (var fileStream = new FileStream(SettingsPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(fileStream))
                    {
                        writer.Write(json);
                        writer.Flush();
                        fileStream.Flush(true); // Force OS-level flush
                    }

                    // FIX: Verify the write actually happened
                    if (File.Exists(SettingsPath))
                    {
                        var verifyContent = File.ReadAllText(SettingsPath);
                        if (verifyContent == json)
                        {
                            _lastSettingsSave = DateTime.Now;
#if DEBUG
                            Debug.WriteLine($"[Settings] ✓ SAVED and VERIFIED to {SettingsPath}");
                            Debug.WriteLine($"[Settings]   Sliders: {AppSettings.SliderTargets?.Count ?? 0}");
                            Debug.WriteLine($"[Settings]   Overlay position: ({AppSettings.OverlayX}, {AppSettings.OverlayY})");
                            Debug.WriteLine($"[Settings]   Overlay screen: {AppSettings.OverlayScreenDevice}");
                            Debug.WriteLine($"[Settings]   Overlay bounds: {AppSettings.OverlayScreenBounds}");
#endif
                            SettingsChanged?.Invoke(AppSettings);
                        }
                        else
                        {
#if DEBUG
                            Debug.WriteLine($"[Settings] ✗ VERIFICATION FAILED - written content doesn't match!");
#endif
                        }
                    }
                    else
                    {
#if DEBUG
                        Debug.WriteLine($"[Settings] ✗ File doesn't exist after write!");
#endif
                    }
                }
                catch (Exception ex)
                {
#if DEBUG
                    Debug.WriteLine($"[ERROR] ✗ FAILED to save settings: {ex.Message}");
                    Debug.WriteLine($"[ERROR] Stack trace: {ex.StackTrace}");
                    Debug.WriteLine($"[ERROR] Settings path: {SettingsPath}");
#endif
                }
            }
        }

        public void SaveSettingsAsync(AppSettings newSettings)
        {
            Task.Run(() => SaveSettings(newSettings));
        }

        public string LoadSavedPortName()
        {
            // Use the in-memory AppSettings instead of reloading from disk
            if (!string.IsNullOrWhiteSpace(AppSettings?.PortName))
            {
#if DEBUG
                Debug.WriteLine($"[Settings] Using cached port name: {AppSettings.PortName}");
#endif
                return AppSettings.PortName;
            }

#if DEBUG
            Debug.WriteLine("[Settings] No saved port name found");
#endif
            return string.Empty;
        }

        public void SaveInvertState(bool isInverted)
        {
            try
            {
                // Use existing in-memory settings instead of reloading from disk
                AppSettings.IsSliderInverted = isInverted;
                SaveSettings(AppSettings);
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[ERROR] Error saving inversion settings: {ex.Message}");
#endif
            }
        }

        /// <summary>
        /// Updates overlay position and screen information in settings
        /// </summary>
        public void UpdateOverlayPosition(double x, double y)
        {
            try
            {
                AppSettings.OverlayX = x;
                AppSettings.OverlayY = y;

                // Capture screen information for multi-monitor support
                var screenInfo = ScreenPositionManager.GetScreenInfo(x, y);
                AppSettings.OverlayScreenDevice = screenInfo.DeviceName;
                AppSettings.OverlayScreenBounds = screenInfo.Bounds;

#if DEBUG
                Debug.WriteLine($"[Settings] Updated overlay position: ({x}, {y})");
                Debug.WriteLine($"[Settings] Screen: {screenInfo.DeviceName} - {screenInfo.Bounds}");
#endif

                // Save will be triggered by the debounced timer in MainWindow
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[ERROR] Error updating overlay position: {ex.Message}");
#endif
            }
        }

        /// <summary>
        /// Validates and corrects overlay position for current display configuration
        /// NOTE: This only corrects the position IN MEMORY for display purposes.
        /// It does NOT save to disk - the user's saved position is the source of truth.
        /// </summary>
        private void ValidateAndCorrectOverlayPosition()
        {
            try
            {
                // If screen device info is missing but we have a position, capture it now
                if (string.IsNullOrEmpty(AppSettings.OverlayScreenDevice) &&
                    (AppSettings.OverlayX != 0 || AppSettings.OverlayY != 0))
                {
#if DEBUG
                    Debug.WriteLine($"[Settings] Screen device info missing, capturing for position ({AppSettings.OverlayX}, {AppSettings.OverlayY})");
#endif
                    var screenInfo = ScreenPositionManager.GetScreenInfo(AppSettings.OverlayX, AppSettings.OverlayY);
                    AppSettings.OverlayScreenDevice = screenInfo.DeviceName;
                    AppSettings.OverlayScreenBounds = screenInfo.Bounds;

                    // IMPORTANT: Don't save here! Just capture for next time user moves overlay
#if DEBUG
                    Debug.WriteLine($"[Settings] Captured screen info (not saved): Device={screenInfo.DeviceName}, Bounds={screenInfo.Bounds}");
#endif
                    return;
                }

                double correctedX, correctedY;
                bool needsCorrection = ScreenPositionManager.ValidateAndCorrectPosition(AppSettings, out correctedX, out correctedY);

                if (needsCorrection)
                {
#if DEBUG
                    Debug.WriteLine($"[Settings] Display configuration may have changed:");
                    Debug.WriteLine($"[Settings]   Saved: ({AppSettings.OverlayX}, {AppSettings.OverlayY})");
                    Debug.WriteLine($"[Settings]   Corrected (for display): ({correctedX}, {correctedY})");
#endif

                    // Update position IN MEMORY ONLY for display purposes
                    AppSettings.OverlayX = correctedX;
                    AppSettings.OverlayY = correctedY;

                    // Update screen info for corrected position
                    var screenInfo = ScreenPositionManager.GetScreenInfo(correctedX, correctedY);
                    AppSettings.OverlayScreenDevice = screenInfo.DeviceName;
                    AppSettings.OverlayScreenBounds = screenInfo.Bounds;

                    // CRITICAL FIX: DO NOT SAVE! Let the overlay be shown at corrected position.
                    // If user moves it, THEN it will be saved.
#if DEBUG
                    Debug.WriteLine($"[Settings] Position corrected in memory only (not saved to disk)");
#endif
                }
                else
                {
#if DEBUG
                    Debug.WriteLine($"[Settings] Overlay position validated successfully: ({AppSettings.OverlayX}, {AppSettings.OverlayY})");
#endif
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[ERROR] Error validating overlay position: {ex.Message}");
#endif
            }
        }

        public bool IsPositionValid(double x, double y)
        {
            // Use virtual screen bounds to support multi-monitor setups
            var virtualLeft = System.Windows.SystemParameters.VirtualScreenLeft;
            var virtualTop = System.Windows.SystemParameters.VirtualScreenTop;
            var virtualWidth = System.Windows.SystemParameters.VirtualScreenWidth;
            var virtualHeight = System.Windows.SystemParameters.VirtualScreenHeight;

            var virtualRight = virtualLeft + virtualWidth;
            var virtualBottom = virtualTop + virtualHeight;

            // Allow position anywhere within virtual screen bounds (with small margin)
            bool isValid = x >= virtualLeft - 100 &&
                           x <= virtualRight - 100 &&
                           y >= virtualTop - 50 &&
                           y <= virtualBottom - 50;

#if DEBUG
            Debug.WriteLine($"[Position] Checking position ({x}, {y}) against virtual bounds ({virtualLeft}, {virtualTop}) to ({virtualRight}, {virtualBottom}) - Valid: {isValid}");
#endif

            return isValid;
        }

        public void ValidateOverlayPosition()
        {
#if DEBUG
            Debug.WriteLine($"[Settings] Initial overlay position from file: ({AppSettings.OverlayX}, {AppSettings.OverlayY})");
#endif

            // Use the new validation that handles multi-monitor scenarios
            ValidateAndCorrectOverlayPosition();

            if (AppSettings.OverlayOpacity <= 0 || AppSettings.OverlayOpacity > 1)
            {
                AppSettings.OverlayOpacity = 0.85;
            }

#if DEBUG
            Debug.WriteLine($"[Settings] Final overlay settings - Enabled: {AppSettings.OverlayEnabled}, Position: ({AppSettings.OverlayX}, {AppSettings.OverlayY}), Opacity: {AppSettings.OverlayOpacity}, Timeout: {AppSettings.OverlayTimeoutSeconds}");
#endif
        }

        public AppSettings CreateSettingsFromUI(
            string portName,
            List<List<AudioTarget>> sliderTargets,
            bool isDarkTheme,
            bool isSliderInverted,
            bool vuMeters,
            bool startOnBoot,
            bool startMinimized,
            bool disableSmoothing)
        {
            return new AppSettings
            {
                PortName = portName,
                SliderTargets = sliderTargets,
                IsDarkTheme = isDarkTheme,
                IsSliderInverted = isSliderInverted,
                VuMeters = vuMeters,
                StartOnBoot = startOnBoot,
                StartMinimized = startMinimized,
                DisableSmoothing = disableSmoothing,

                // Preserve overlay settings from current settings
                OverlayEnabled = AppSettings.OverlayEnabled,
                OverlayOpacity = AppSettings.OverlayOpacity,
                OverlayTimeoutSeconds = AppSettings.OverlayTimeoutSeconds,
                OverlayX = AppSettings.OverlayX,
                OverlayY = AppSettings.OverlayY,
                OverlayTextColor = AppSettings.OverlayTextColor,
                OverlayScreenDevice = AppSettings.OverlayScreenDevice,
                OverlayScreenBounds = AppSettings.OverlayScreenBounds,

                // Preserve button settings from current settings
                NumberOfButtons = AppSettings.NumberOfButtons,
                ButtonMappings = AppSettings.ButtonMappings,

                // Persist the selected theme with the profile settings
                SelectedTheme = AppSettings.SelectedTheme
            };
        }

        /// <summary>
        /// Diagnostic method to help troubleshoot settings issues
        /// </summary>
        public string GetDiagnosticInfo()
        {
            var info = $"Settings Path: {SettingsPath}\n";
            info += $"Path Exists: {File.Exists(SettingsPath)}\n";
            info += $"Path Writable: {IsPathWritable(Path.GetDirectoryName(SettingsPath))}\n";
            info += $"LocalAppData: {Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}\n";
            info += $"AppData (Roaming): {Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}\n";
            info += $"Documents: {Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}\n";
            info += $"Current Settings:\n";
            info += $"  Overlay Position: ({AppSettings.OverlayX}, {AppSettings.OverlayY})\n";
            info += $"  Overlay Screen: {AppSettings.OverlayScreenDevice}\n";
            info += $"  Overlay Bounds: {AppSettings.OverlayScreenBounds}\n";
            info += $"OS Version: {Environment.OSVersion}\n";
            info += $"Is 64-bit: {Environment.Is64BitOperatingSystem}\n";
            return info;
        }
    }
}