using DeejNG.Classes;
using NAudio.CoreAudioApi;
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

        #endregion Private Fields

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
            ExponentialVolumeFactorSlider.Value = _settings.ExponentialVolumeFactor;

            HidVendorIdTextBox.Text = $"{_settings.HidVendorId}";
            HidProductIdTextBox.Text = $"{_settings.HidProductId}";

            AudioDevice1FriendlyNameTextBox.Text = _settings.AudioDeviceOneFriendlyName;
            AudioDevice1IdTextBox.Text = _settings.AudioDeviceOneId;
            MicrophoneDevice1FriendlyNameTextBox.Text = _settings.MicrophoneDeviceOneFriendlyName;
            MicrophoneDevice1IdTextBox.Text = _settings.MicrophoneDeviceOneId;

            AudioDevice2FriendlyNameTextBox.Text = _settings.AudioDeviceTwoFriendlyName;
            AudioDevice2IdTextBox.Text = _settings.AudioDeviceTwoId;
            MicrophoneDevice2FriendlyNameTextBox.Text = _settings.MicrophoneDeviceTwoFriendlyName;
            MicrophoneDevice2IdTextBox.Text = _settings.MicrophoneDeviceTwoId;

            AudioDevice3FriendlyNameTextBox.Text = _settings.AudioDeviceThreeFriendlyName;
            AudioDevice3IdTextBox.Text = _settings.AudioDeviceThreeId;
            MicrophoneDevice3FriendlyNameTextBox.Text = _settings.MicrophoneDeviceThreeFriendlyName;
            MicrophoneDevice3IdTextBox.Text = _settings.MicrophoneDeviceThreeId;

            SetTextColorSelection(_settings.OverlayTextColor); // Set ComboBox for text color

            // Initialize general toggles from main window state (hidden controls)
            if (_mainWindow != null)
            {
                SettingInvertSliders.IsChecked = _mainWindow.InvertSliderCheckBox.IsChecked;
                SettingShowMeters.IsChecked = _mainWindow.ShowSlidersCheckBox.IsChecked;
                SettingStartOnBoot.IsChecked = _mainWindow.StartOnBootCheckBox.IsChecked;
                SettingStartMinimized.IsChecked = _mainWindow.StartMinimizedCheckBox.IsChecked;
                SettingDisableSmoothing.IsChecked = _mainWindow.DisableSmoothingCheckBox.IsChecked;
                SettingUseExponentialVolume.IsChecked = _mainWindow.UseExponentialVolumeCheckBox.IsChecked;
                ExponentialVolumeFactorSlider.Value = _mainWindow.ExponentialVolumeFactorSlider.Value;


                UpdateConnectButtonState();

                // Initialize baud rate from settings
                InitializeBaudRateSelection();

                // Initialize exclusion list from settings
                InitializeExcludedAppsList();
            }

            // Wire up real-time control events
            OpacitySlider.ValueChanged += OpacitySlider_ValueChanged;
            AutoCloseCheckBox.Checked += AutoCloseCheckBox_Changed;
            AutoCloseCheckBox.Unchecked += AutoCloseCheckBox_Changed;
            TimeoutSlider.ValueChanged += TimeoutSlider_ValueChanged;
            OverlayEnabledCheckBox.Checked += OverlayEnabledCheckBox_Changed;
            OverlayEnabledCheckBox.Unchecked += OverlayEnabledCheckBox_Changed;
            TextColorComboBox.SelectionChanged += TextColorComboBox_SelectionChanged;

            // Wire general checkbox events -> forward to main window controls to reuse logic and saving
            SettingInvertSliders.Checked += ForwardGeneralCheckbox;
            SettingInvertSliders.Unchecked += ForwardGeneralCheckbox;
            SettingShowMeters.Checked += ForwardGeneralCheckbox;
            SettingShowMeters.Unchecked += ForwardGeneralCheckbox;
            SettingStartOnBoot.Checked += ForwardGeneralCheckbox;
            SettingStartOnBoot.Unchecked += ForwardGeneralCheckbox;
            SettingStartMinimized.Checked += ForwardGeneralCheckbox;
            SettingStartMinimized.Unchecked += ForwardGeneralCheckbox;
            SettingDisableSmoothing.Checked += ForwardGeneralCheckbox;
            SettingDisableSmoothing.Unchecked += ForwardGeneralCheckbox;
            SettingUseExponentialVolume.Checked += ForwardGeneralCheckbox;
            SettingUseExponentialVolume.Unchecked += ForwardGeneralCheckbox;
            ExponentialVolumeFactorSlider.ValueChanged += ForwardGeneralSlider;

            // Baud rate changes are persisted on Save; no live wiring needed here
        }

        #endregion Public Constructors

        #region Public Enums

        // Used to map text color ComboBox options
        public enum TextColorOption
        {
            Auto,
            White,
            Black
        }

        #endregion Public Enums

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

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// Opens the button configuration dialog.
        /// </summary>
        private void ConfigureButtons_Click(object sender, RoutedEventArgs e)
        {
            var buttonDialog = new ButtonSettingsDialog(_settings)
            {
                Owner = this
            };

            if (buttonDialog.ShowDialog() == true)
            {
                // Button settings are saved directly to _settings by the dialog
                // No additional action needed here

            }
        }

        private void ForwardGeneralCheckbox(object? sender, RoutedEventArgs e)
        {
            if (_mainWindow == null) return;

            if (sender == SettingInvertSliders)
                _mainWindow.InvertSliderCheckBox.IsChecked = SettingInvertSliders.IsChecked;
            else if (sender == SettingShowMeters)
                _mainWindow.ShowSlidersCheckBox.IsChecked = SettingShowMeters.IsChecked;
            else if (sender == SettingStartOnBoot)
                _mainWindow.StartOnBootCheckBox.IsChecked = SettingStartOnBoot.IsChecked;
            else if (sender == SettingStartMinimized)
                _mainWindow.StartMinimizedCheckBox.IsChecked = SettingStartMinimized.IsChecked;
            else if (sender == SettingDisableSmoothing)
                _mainWindow.DisableSmoothingCheckBox.IsChecked = SettingDisableSmoothing.IsChecked;
            else if (sender == SettingUseExponentialVolume)
                _mainWindow.UseExponentialVolumeCheckBox.IsChecked = SettingUseExponentialVolume.IsChecked;
        }

        private void ForwardGeneralSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_mainWindow == null) return;

            if (sender == ExponentialVolumeFactorSlider)
                _mainWindow.ExponentialVolumeFactorSlider.Value = ExponentialVolumeFactorSlider.Value;
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
        /// Helper to select the baud rate option matching AppSettings.BaudRate, defaulting to 9600 if not set.
        /// </summary>
        private void InitializeBaudRateSelection()
        {
            int baud = 9600;
        }

        /// <summary>
        /// Loads settings from disk. Returns defaults if file is missing or corrupt.
        /// </summary>
        private AppSettings LoadSettings()
        {
            try
            {
                // CRITICAL FIX: Load from MainWindow's settings manager which uses the active profile
                if (_mainWindow != null)
                {
                    var settings = _mainWindow.GetCurrentSettings();
                    if (settings != null)
                    {
                        return settings;
                    }
                }

                // Fallback: Check if the old settings file exists on disk (legacy)
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
        /// Auto-shows the overlay so users can see the effect of opacity changes in real-time.
        /// </summary>
        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_settings == null) return;

            PreserveOverlayPosition();
            _settings.OverlayOpacity = e.NewValue;
            ApplySettingsToOverlay();

            // Auto-show overlay when opacity is being adjusted so user can see the effect
            if (_settings.OverlayEnabled)
            {
                ShowOverlayImmediately();
            }
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

            // Save excluded apps list
            _settings.ExcludedFromUnmapped = ExcludedAppsListBox.Items.Cast<string>().ToList();

            if (int.TryParse(HidVendorIdTextBox.Text, out int vid))
            {
                _settings.HidVendorId = vid;
            }

            if (int.TryParse(HidProductIdTextBox.Text, out int pid))
            {
                _settings.HidProductId = pid;
            }

            _settings.AudioDeviceOneFriendlyName = AudioDevice1FriendlyNameTextBox.Text;
            _settings.AudioDeviceOneId = AudioDevice1IdTextBox.Text;
            _settings.MicrophoneDeviceOneFriendlyName = MicrophoneDevice1FriendlyNameTextBox.Text;
            _settings.MicrophoneDeviceOneId = MicrophoneDevice1IdTextBox.Text;

            _settings.AudioDeviceTwoFriendlyName = AudioDevice2FriendlyNameTextBox.Text;
            _settings.AudioDeviceTwoId = AudioDevice2IdTextBox.Text;
            _settings.MicrophoneDeviceTwoFriendlyName = MicrophoneDevice2FriendlyNameTextBox.Text;
            _settings.MicrophoneDeviceTwoId = MicrophoneDevice2IdTextBox.Text;

            _settings.AudioDeviceThreeFriendlyName = AudioDevice3FriendlyNameTextBox.Text;
            _settings.AudioDeviceThreeId = AudioDevice3IdTextBox.Text;
            _settings.MicrophoneDeviceThreeFriendlyName = MicrophoneDevice3FriendlyNameTextBox.Text;
            _settings.MicrophoneDeviceThreeId = MicrophoneDevice3IdTextBox.Text;

            try
            {
                // Apply the updated settings to the main window and overlay
                // UpdateOverlaySettings will now save to the active profile
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

        private void SettingComPortSelector_DropDownOpened(object sender, EventArgs e)
        {
            // Not needed anymore
        }

        private void SettingComPortSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // not needed anymore
        }

        private void SettingConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_mainWindow != null)
            {
                // Forward the click to the main window's connect button
                _mainWindow.ConnectButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                UpdateConnectButtonState();
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

        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            {
                try { DragMove(); } catch { }
            }
        }

        private void UpdateConnectButtonState()
        {
            if (_mainWindow != null)
            {
                // not needed anymore ?
            }
        }

        /// <summary>
        /// Initializes the excluded apps list from settings.
        /// </summary>
        private void InitializeExcludedAppsList()
        {
            ExcludedAppsListBox.Items.Clear();
            if (_settings?.ExcludedFromUnmapped != null)
            {
                foreach (var app in _settings.ExcludedFromUnmapped)
                {
                    ExcludedAppsListBox.Items.Add(app);
                }
            }
        }

        /// <summary>
        /// Populates the running apps combo box when the dropdown is opened.
        /// </summary>
        private void RunningAppsComboBox_DropDownOpened(object sender, EventArgs e)
        {
            try
            {
                RunningAppsComboBox.Items.Clear();

                // Get running audio sessions using the existing helper
                var sessions = AudioSessionManagerHelper.GetSessionNames(new List<string>(), string.Empty);

                // Filter to only real applications (exclude System and Unmapped entries)
                var appNames = sessions
                    .Where(s => s.Id != "system" && s.Id != "unmapped")
                    .Select(s => s.FriendlyName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name)
                    .ToList();

                foreach (var appName in appNames)
                {
                    // Don't show apps already in the exclusion list
                    if (!ExcludedAppsListBox.Items.Contains(appName))
                    {
                        RunningAppsComboBox.Items.Add(appName);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] Error populating running apps: {ex.Message}");
            }
        }

        /// <summary>
        /// Adds the selected app to the exclusion list.
        /// </summary>
        private void AddExcludedApp_Click(object sender, RoutedEventArgs e)
        {
            if (RunningAppsComboBox.SelectedItem is string selectedApp && !string.IsNullOrWhiteSpace(selectedApp))
            {
                // Don't add duplicates
                if (!ExcludedAppsListBox.Items.Contains(selectedApp))
                {
                    ExcludedAppsListBox.Items.Add(selectedApp);
                    RunningAppsComboBox.Items.Remove(selectedApp);
                    RunningAppsComboBox.SelectedItem = null;
                }
            }
        }

        /// <summary>
        /// Removes the selected app from the exclusion list.
        /// </summary>
        private void RemoveExcludedApp_Click(object sender, RoutedEventArgs e)
        {
            if (ExcludedAppsListBox.SelectedItem is string selectedApp)
            {
                ExcludedAppsListBox.Items.Remove(selectedApp);
            }
        }

        private void OnSetNewHidID(object sender, RoutedEventArgs e)
        {
            int newVid = int.TryParse(HidVendorIdTextBox.Text, out newVid) ? newVid : -1;
            int newPid = int.TryParse(HidProductIdTextBox.Text, out newPid) ? newPid : -1;

            int oldVid = _settings.HidVendorId;
            int oldPid = _settings.HidProductId;

            if (newVid > 0 && newPid > 0)
            {
                _mainWindow.ConnectWithNewVidAndPid(newVid, newPid);
            }
        }

        private void LogAudioDevicesButton_Click(object sender, EventArgs e)
        {
            LoadAudioOutputDevices();
            LoadMicrophoneDevices();
        }

        private void LoadAudioOutputDevices()
        {
            AvailableAudioDevicesListBox.ItemsSource = GetDeviceItems(DataFlow.Render);
        }

        private void LoadMicrophoneDevices()
        {
            AvailableMicrophoneDevicesListBox.ItemsSource = GetDeviceItems(DataFlow.Capture);
        }

        private List<AudioDeviceListItem> GetDeviceItems(DataFlow flow)
        {
            var items = new List<AudioDeviceListItem>();

            try
            {
                using var enumerator = new MMDeviceEnumerator();

                MMDevice? defaultDevice = null;

                try
                {
                    defaultDevice = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                }
                catch
                {
                    // No default device for this flow is okay
                }

                var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);

                foreach (var device in devices)
                {
                    bool isDefault = defaultDevice != null && device.ID == defaultDevice.ID;

                    items.Add(new AudioDeviceListItem
                    {
                        FriendlyName = isDefault
                            ? $"{device.FriendlyName} (Default)"
                            : device.FriendlyName,
                        Id = device.ID
                    });
                }
            }
            catch
            {
            }

            return items;
        }

        private void CopyAudioDeviceField_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button button && button.Tag is string text && !string.IsNullOrWhiteSpace(text))
                {
                    Clipboard.SetText(text);
                }
            }
            catch
            {
            }
        }

        #endregion Private Methods

        #region Private Models
        private sealed class AudioDeviceListItem
        {
            public string FriendlyName { get; set; } = string.Empty;
            public string Id { get; set; } = string.Empty;
        }

        #endregion
    }
}