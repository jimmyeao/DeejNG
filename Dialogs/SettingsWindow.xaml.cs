using DeejNG.Classes;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace DeejNG.Dialogs
{
    /// <summary>
    /// Interaction logic for the Settings window.
    /// Allows configuration of overlay behavior, timeout, opacity, and text color.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        #region Private Fields

        // Path to where settings will be stored on disk
        private readonly string _settingsPath;

        // Reference to the main application window
        private MainWindow _mainWindow;

        // Current instance of the settings object
        private AppSettings _settings;

        #endregion

        #region Public Constructors

        /// <summary>
        /// Initializes the settings window, loads saved settings from disk,
        /// and applies them to UI controls.
        /// </summary>
        public SettingsWindow()
        {
            InitializeComponent();

            // Build the path to the settings file in the user's AppData folder
            _settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeejNG", "settings.json");

            _mainWindow = Application.Current.MainWindow as MainWindow;
            _settings = LoadSettings();

            // Initialize UI controls with loaded values
            OverlayEnabledCheckBox.IsChecked = _settings.OverlayEnabled;
            OpacitySlider.Value = _settings.OverlayOpacity;
            AutoCloseCheckBox.IsChecked = _settings.OverlayTimeoutSeconds > 0;
            TimeoutSlider.Value = _settings.OverlayTimeoutSeconds;

            SetTextColorSelection(_settings.OverlayTextColor); // Set ComboBox for text color

            // Wire up real-time control events
            OpacitySlider.ValueChanged += OpacitySlider_ValueChanged;
            AutoCloseCheckBox.Checked += AutoCloseCheckBox_Changed;
            AutoCloseCheckBox.Unchecked += AutoCloseCheckBox_Changed;
            TimeoutSlider.ValueChanged += TimeoutSlider_ValueChanged;
            OverlayEnabledCheckBox.Checked += OverlayEnabledCheckBox_Changed;
            OverlayEnabledCheckBox.Unchecked += OverlayEnabledCheckBox_Changed;
            TextColorComboBox.SelectionChanged += TextColorComboBox_SelectionChanged;
        }

        #endregion

        #region Public Enums

        // Used to map text color ComboBox options
        public enum TextColorOption
        {
            Auto,
            White,
            Black
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Applies the current settings to the overlay.
        /// Ensures overlay updates even if already shown.
        /// </summary>
        private void ApplySettingsToOverlay()
        {
            if (_mainWindow != null)
            {
                _mainWindow.UpdateOverlaySettings(_settings);
            }
        }

        /// <summary>
        /// Handles changes to the Auto Close checkbox state.
        /// Updates the overlay timeout setting based on the user's choice,
        /// applies the change, and optionally shows the overlay immediately if auto-close was turned off.
        /// </summary>
        private void AutoCloseCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            // If settings are not initialized, exit early
            if (_settings == null) return;

            // Determine whether auto-close is enabled (checked)
            bool isEnabled = AutoCloseCheckBox.IsChecked == true;

            // If enabled, set timeout to current slider value; otherwise, use the constant for 'no timeout'
            _settings.OverlayTimeoutSeconds = isEnabled ? (int)TimeoutSlider.Value : AppSettings.OverlayNoTimeout;

            // Apply the updated setting to the overlay immediately
            ApplySettingsToOverlay();

            // If auto-close was disabled AND overlay is enabled, show the overlay immediately
            if (!isEnabled && _settings.OverlayEnabled)
            {
                ShowOverlayImmediately();
            }
        }



        /// <summary>
        /// Handles the Cancel button click in the settings window.
        /// Reloads the original settings from disk, applies them to the main window and overlay,
        /// and then closes the settings window without saving any changes made by the user.
        /// </summary>
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            // Reload the original saved settings from disk
            var originalSettings = LoadSettings();

            if (_mainWindow != null)
            {
                // Apply original settings to the main window and overlay
                _mainWindow.UpdateOverlaySettings(originalSettings);
            }

            // Close the settings window without saving changes
            Close();
        }


        /// <summary>
        /// Retrieves the selected overlay text color as a string based on the ComboBox selection index.
        /// </summary>
        /// <returns>
        /// "Auto", "White", or "Black" depending on the selected index.
        /// Defaults to "Auto" if the index is out of range.
        /// </returns>
        private string GetTextColorFromSelection()
        {
            return TextColorComboBox.SelectedIndex switch
            {
                0 => "Auto",   // Index 0 corresponds to automatic color selection
                1 => "White",  // Index 1 explicitly sets text color to white
                2 => "Black",  // Index 2 explicitly sets text color to black
                _ => "Auto"    // Fallback in case of unexpected index value
            };
        }


        /// <summary>
        /// Immediately hides the overlay (e.g. when overlay is disabled).
        /// </summary>
        private void HideOverlayImmediately()
        {
            if (_mainWindow != null)
            {
                Debug.WriteLine("[Settings] Hiding overlay immediately");
                // Temporarily disable overlay and apply settings
                var tempSettings = new AppSettings
                {
                    OverlayEnabled = false,
                    OverlayOpacity = _settings.OverlayOpacity,
                    OverlayTimeoutSeconds = _settings.OverlayTimeoutSeconds,
                    OverlayTextColor = _settings.OverlayTextColor,
                    OverlayX = _settings.OverlayX,
                    OverlayY = _settings.OverlayY
                };
                _mainWindow.UpdateOverlaySettings(tempSettings);
            }
        }

        /// <summary>
        /// Loads settings from disk. Returns defaults if file is missing or corrupt.
        /// </summary>
        private AppSettings LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    string json = File.ReadAllText(_settingsPath);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Failed to load settings: {ex.Message}");
                MessageBox.Show("Settings could not be loaded. Using default settings.", "Settings Error",
                               MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return new AppSettings();
        }

        /// <summary>
        /// Applies changes to the overlay opacity slider.
        /// </summary>
        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_settings == null) return;

            PreserveOverlayPosition();
            _settings.OverlayOpacity = e.NewValue;
            ApplySettingsToOverlay();
        }

        /// <summary>
        /// Handles toggle of the overlay enabled checkbox.
        /// Shows or hides the overlay immediately based on state.
        /// </summary>
        private void OverlayEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;

            _settings.OverlayEnabled = OverlayEnabledCheckBox.IsChecked == true;
            ApplySettingsToOverlay();

            if (_settings.OverlayEnabled)
            {
                ShowOverlayImmediately();
            }
            else
            {
                HideOverlayImmediately();
            }
        }

        /// <summary>
        /// Saves the last known overlay position for consistency across sessions.
        /// </summary>
        private void PreserveOverlayPosition()
        {
            // Position is now managed by the overlay service automatically
            // No need to manually preserve position here
            Debug.WriteLine("[Settings] Position preservation handled by overlay service");
        }


        /// <summary>
        /// Handles the Save button click in the settings window.
        /// Applies the user's settings, writes them to disk, and updates the overlay in the main window.
        /// </summary>
        private void Save_Click(object sender, RoutedEventArgs e)
        {
            // Save the current position of the overlay (if it's open)
            PreserveOverlayPosition();

            // Update settings object with values from UI controls
            _settings.OverlayEnabled = OverlayEnabledCheckBox.IsChecked == true;
            _settings.OverlayOpacity = OpacitySlider.Value;
            _settings.OverlayTimeoutSeconds = AutoCloseCheckBox.IsChecked == true
                ? (int)TimeoutSlider.Value            // Use timeout from slider if auto-close is enabled
                : AppSettings.OverlayNoTimeout;       // Use a constant to indicate "no timeout"

            // Save selected overlay text color (e.g., Auto, White, Black)
            _settings.OverlayTextColor = GetTextColorFromSelection();

            try
            {
                // Serialize the settings object to a formatted JSON string
                string json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });

                // Ensure the settings directory exists
                var directoryPath = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrEmpty(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                }

                // Write the settings JSON to disk
                File.WriteAllText(_settingsPath, json);
                Debug.WriteLine($"[Settings] Final save - Text Color: {_settings.OverlayTextColor}");

                // Apply the updated settings to the main window and overlay
                _mainWindow?.UpdateOverlaySettings(_settings);
            }
            catch (Exception ex)
            {
                // Log and show error if saving to disk fails
                Debug.WriteLine($"[ERROR] Failed to save settings: {ex.Message}");
                MessageBox.Show("Failed to save settings.");
            }

            // Close the settings window
            Close();
        }

        /// <summary>
        /// Sets the selected index of the TextColorComboBox based on the provided text color string.
        /// Falls back to "Auto" if the input is invalid or not recognized.
        /// </summary>
        /// <param name="textColor">The string representation of the text color (e.g., "Auto", "White", "Black").</param>
        private void SetTextColorSelection(string textColor)
        {
            // Attempt to parse the input string into a valid enum value of type TextColorOption (case-insensitive)
            if (Enum.TryParse<TextColorOption>(textColor, true, out var colorOption))
            {
                // If parsing succeeds, set the ComboBox index based on the enum value
                TextColorComboBox.SelectedIndex = (int)colorOption;
            }
            else
            {
                // If parsing fails, default to "Auto" (index 0)
                TextColorComboBox.SelectedIndex = 0;
            }
        }


        /// <summary>
        /// Immediately displays the overlay with current volume levels and channel labels.
        /// Only runs if the overlay is enabled in settings and the main window is available.
        /// </summary>
        private void ShowOverlayImmediately()
        {
            // Check that the main window exists and the overlay feature is enabled
            if (_mainWindow != null && _settings.OverlayEnabled)
            {
                Debug.WriteLine("[Settings] Showing overlay immediately");
                
                // Instruct the main window to display the overlay
                _mainWindow.ShowVolumeOverlay();
                
                Debug.WriteLine($"[Settings] Overlay shown via service");
            }
        }


        /// <summary>
        /// Updates overlay preview in real-time when text color selection changes.
        /// </summary>
        private void TextColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settings == null) return;

            _settings.OverlayTextColor = GetTextColorFromSelection();
            ApplySettingsToOverlay();

            Debug.WriteLine($"[Settings] Text color changed to: {_settings.OverlayTextColor}");
        }

        /// <summary>
        /// Updates overlay timeout in real-time if auto-close is enabled.
        /// </summary>
        private void TimeoutSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_settings == null) return;

            if (AutoCloseCheckBox.IsChecked == true)
            {
                _settings.OverlayTimeoutSeconds = (int)e.NewValue;
                ApplySettingsToOverlay();
            }
        }

        #endregion
    }
}
