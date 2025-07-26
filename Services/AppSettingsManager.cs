using DeejNG.Classes;
using DeejNG.Models;
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
        private readonly object _settingsLock = new object();
        private DateTime _lastSettingsSave = DateTime.MinValue;
        private string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeejNG", "settings.json");

        public AppSettings Settings { get; private set; } = new AppSettings();

        public event Action<AppSettings> SettingsChanged;

        public AppSettings LoadSettingsFromDisk()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    Debug.WriteLine($"[Settings] Loading from: {SettingsPath}");

                    Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

                    Debug.WriteLine($"[Settings] Loaded from disk - OverlayEnabled: {Settings.OverlayEnabled}");
                    Debug.WriteLine($"[Settings] Loaded from disk - OverlayPosition: ({Settings.OverlayX}, {Settings.OverlayY})");
                    Debug.WriteLine($"[Settings] Loaded from disk - OverlayOpacity: {Settings.OverlayOpacity}");
                    Debug.WriteLine($"[Settings] Loaded from disk - OverlayTimeoutSeconds: {Settings.OverlayTimeoutSeconds}");

                    return Settings;
                }
                else
                {
                    Debug.WriteLine($"[Settings] Settings file does not exist: {SettingsPath}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] Error loading settings: {ex.Message}");
            }

            Settings = new AppSettings();
            Debug.WriteLine("[Settings] Using default AppSettings");
            return Settings;
        }

        public void SaveSettings(AppSettings newSettings)
        {
            // Prevent too frequent saves
            if ((DateTime.Now - _lastSettingsSave).TotalMilliseconds < 500)
            {
                Debug.WriteLine("[Settings] Skipping save - too frequent");
                return;
            }

            lock (_settingsLock)
            {
                try
                {
                    Settings = newSettings;

                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true
                    };
                    var json = JsonSerializer.Serialize(Settings, options);

                    // Ensure directory exists
                    var dir = Path.GetDirectoryName(SettingsPath);
                    if (!Directory.Exists(dir) && dir != null)
                    {
                        Directory.CreateDirectory(dir);
                    }

                    File.WriteAllText(SettingsPath, json);
                    _lastSettingsSave = DateTime.Now;

                    Debug.WriteLine($"[Settings] Saved successfully with {Settings.SliderTargets?.Count ?? 0} slider configurations and overlay settings");

                    SettingsChanged?.Invoke(Settings);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ERROR] Failed to save settings: {ex.Message}");
                }
            }
        }

        public void SaveSettingsAsync(AppSettings newSettings)
        {
            Task.Run(() => SaveSettings(newSettings));
        }

        public string LoadSavedPortName()
        {
            try
            {
                var settings = LoadSettingsFromDisk();
                if (!string.IsNullOrWhiteSpace(settings?.PortName))
                {
                    Debug.WriteLine($"[Settings] Loaded saved port name: {settings.PortName}");
                    return settings.PortName;
                }
                else
                {
                    Debug.WriteLine("[Settings] No saved port name found");
                    return string.Empty;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Failed to load saved port name: {ex.Message}");
                return string.Empty;
            }
        }

        public void SaveInvertState(bool isInverted)
        {
            try
            {
                var settings = LoadSettingsFromDisk() ?? new AppSettings();
                settings.IsSliderInverted = isInverted;
                SaveSettings(settings);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Error saving inversion settings: {ex.Message}");
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

            Debug.WriteLine($"[Position] Checking position ({x}, {y}) against virtual bounds ({virtualLeft}, {virtualTop}) to ({virtualRight}, {virtualBottom}) - Valid: {isValid}");

            return isValid;
        }

        public void ValidateOverlayPosition()
        {
            Debug.WriteLine($"[Settings] Initial overlay position from file: ({Settings.OverlayX}, {Settings.OverlayY})");

            if (!IsPositionValid(Settings.OverlayX, Settings.OverlayY))
            {
                Debug.WriteLine($"[Settings] Position ({Settings.OverlayX}, {Settings.OverlayY}) is outside virtual screen bounds, resetting to default");
                Settings.OverlayX = 100;
                Settings.OverlayY = 100;
            }
            else
            {
                Debug.WriteLine($"[Settings] Position ({Settings.OverlayX}, {Settings.OverlayY}) is valid for multi-monitor setup");
            }

            if (Settings.OverlayOpacity <= 0 || Settings.OverlayOpacity > 1)
            {
                Settings.OverlayOpacity = 0.85;
            }

            Debug.WriteLine($"[Settings] Final overlay settings - Enabled: {Settings.OverlayEnabled}, Position: ({Settings.OverlayX}, {Settings.OverlayY}), Opacity: {Settings.OverlayOpacity}, Timeout: {Settings.OverlayTimeoutSeconds}");
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
                OverlayEnabled = Settings.OverlayEnabled,
                OverlayOpacity = Settings.OverlayOpacity,
                OverlayTimeoutSeconds = Settings.OverlayTimeoutSeconds,
                OverlayX = Settings.OverlayX,
                OverlayY = Settings.OverlayY,
                OverlayTextColor = Settings.OverlayTextColor
            };
        }
    }
}