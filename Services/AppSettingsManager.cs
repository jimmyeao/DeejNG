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
 
    public class AppSettingsManager
    {
        #region Private Fields

        private readonly object _settingsLock = new object();
        private string _cachedSettingsPath = null;
        private DateTime _lastSettingsSave = DateTime.MinValue;

        #endregion Private Fields

        #region Public Events

        public event Action<AppSettings> SettingsChanged;

        #endregion Public Events

        #region Public Properties

        // Rename to avoid conflict with DeejNG.Settings class
        public AppSettings AppSettings { get; set; } = new AppSettings();

        #endregion Public Properties

        #region Private Properties

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



                // Check if primary path is writable
                if (IsPathWritable(Path.GetDirectoryName(primaryPath)))
                {
                    _cachedSettingsPath = primaryPath;

                    return _cachedSettingsPath;
                }

                // Fallback 1: ApplicationData (Roaming)
                string fallback1 = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DeejNG",
                    "settings.json");



                if (IsPathWritable(Path.GetDirectoryName(fallback1)))
                {
                    _cachedSettingsPath = fallback1;

                    return _cachedSettingsPath;
                }

                // Fallback 2: Current user's Documents folder
                string fallback2 = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "DeejNG",
                    "settings.json");



                if (IsPathWritable(Path.GetDirectoryName(fallback2)))
                {
                    _cachedSettingsPath = fallback2;

                    return _cachedSettingsPath;
                }

                // Last resort: Application directory (might need admin rights but better than nothing)
                string fallback3 = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "settings.json");



                _cachedSettingsPath = fallback3;
                return _cachedSettingsPath;
            }
        }

        #endregion Private Properties

        #region Public Methods

        public AppSettings CreateSettingsFromUI(
                    string portName,
                    List<List<AudioTarget>> sliderTargets,
                    bool isDarkTheme,
                    bool isSliderInverted,
                    bool vuMeters,
                    bool startOnBoot,
                    bool startMinimized,
                    bool disableSmoothing,
                    bool exponentialVolume,
                    float exponentialVolumeFactor,
                    int baudRate)
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
                UseExponentialVolume = exponentialVolume,
                ExponentialVolumeFactor = exponentialVolumeFactor,
                BaudRate = baudRate,

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
                SelectedTheme = AppSettings.SelectedTheme,

                // Preserve excluded apps for unmapped applications
                ExcludedFromUnmapped = AppSettings.ExcludedFromUnmapped ?? new List<string>()
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

        /// <summary>
        /// Gets the settings file path (exposed for ProfileManager)
        /// </summary>
        public string GetSettingsPath() => SettingsPath;

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



            return isValid;
        }

        public string LoadSavedPortName()
        {
            // Use the in-memory AppSettings instead of reloading from disk
            if (!string.IsNullOrWhiteSpace(AppSettings?.PortName))
            {

                return AppSettings.PortName;
            }


            return string.Empty;
        }

        public AppSettings LoadSettingsFromDisk()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);


                    AppSettings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();



                    // Validate and correct overlay position for current display configuration
                    ValidateAndCorrectOverlayPosition();

                    return AppSettings;
                }
                else
                {

                }
            }
            catch (Exception ex)
            {

            }

            AppSettings = new AppSettings();

            return AppSettings;
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

            }
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

                            SettingsChanged?.Invoke(AppSettings);
                        }
                        else
                        {

                        }
                    }
                    else
                    {

                    }
                }
                catch (Exception ex)
                {

                }
            }
        }

        public void SaveSettingsAsync(AppSettings newSettings)
        {
            Task.Run(() => SaveSettings(newSettings));
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



                // Save will be triggered by the debounced timer in MainWindow
            }
            catch (Exception ex)
            {

            }
        }

        public void ValidateOverlayPosition()
        {


            // Use the new validation that handles multi-monitor scenarios
            ValidateAndCorrectOverlayPosition();

            if (AppSettings.OverlayOpacity <= 0 || AppSettings.OverlayOpacity > 1)
            {
                AppSettings.OverlayOpacity = 0.85;
            }


        }

        #endregion Public Methods

        #region Private Methods

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

                return false;
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

                    var screenInfo = ScreenPositionManager.GetScreenInfo(AppSettings.OverlayX, AppSettings.OverlayY);
                    AppSettings.OverlayScreenDevice = screenInfo.DeviceName;
                    AppSettings.OverlayScreenBounds = screenInfo.Bounds;

                    // IMPORTANT: Don't save here! Just capture for next time user moves overlay

                    return;
                }

                double correctedX, correctedY;
                bool needsCorrection = ScreenPositionManager.ValidateAndCorrectPosition(AppSettings, out correctedX, out correctedY);

                if (needsCorrection)
                {


                    // Update position IN MEMORY ONLY for display purposes
                    AppSettings.OverlayX = correctedX;
                    AppSettings.OverlayY = correctedY;

                    // Update screen info for corrected position
                    var screenInfo = ScreenPositionManager.GetScreenInfo(correctedX, correctedY);
                    AppSettings.OverlayScreenDevice = screenInfo.DeviceName;
                    AppSettings.OverlayScreenBounds = screenInfo.Bounds;

                    // CRITICAL FIX: DO NOT SAVE! Let the overlay be shown at corrected position.
                    // If user moves it, THEN it will be saved.

                }
                else
                {

                }
            }
            catch (Exception ex)
            {

            }
        }

        #endregion Private Methods
    }
}