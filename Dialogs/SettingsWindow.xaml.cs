using DeejNG.Classes;
using DeejNG.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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

        // Button mappings collection for UI binding
        private ObservableCollection<ButtonMappingViewModel> _buttonMappings = new();

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

            // Initialize general toggles from main window state (hidden controls)
            if (_mainWindow != null)
            {
                SettingInvertSliders.IsChecked = _mainWindow.InvertSliderCheckBox.IsChecked;
                SettingShowMeters.IsChecked = _mainWindow.ShowSlidersCheckBox.IsChecked;
                SettingStartOnBoot.IsChecked = _mainWindow.StartOnBootCheckBox.IsChecked;
                SettingStartMinimized.IsChecked = _mainWindow.StartMinimizedCheckBox.IsChecked;
                SettingDisableSmoothing.IsChecked = _mainWindow.DisableSmoothingCheckBox.IsChecked;

                // Initialize COM port controls from main window
                SettingComPortSelector.ItemsSource = _mainWindow.ComPortSelector.ItemsSource;
                SettingComPortSelector.SelectedItem = _mainWindow.ComPortSelector.SelectedItem;
                
                // Initialize baud rate selector
                SetBaudRateSelection(_settings.BaudRate);
                
                UpdateConnectButtonState();
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

            // Wire COM port selection changes
            SettingComPortSelector.SelectionChanged += SettingComPortSelector_SelectionChanged;

            // Initialize button configuration after window loads
            this.Loaded += SettingsWindow_Loaded;
        }

        private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Load button configuration after all controls are initialized
            LoadButtonConfiguration();
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
        }

        private void SettingComPortSelector_DropDownOpened(object sender, EventArgs e)
        {
            if (_mainWindow != null)
            {
                // Refresh ports in main window and sync to settings
                var selectedPort = SettingComPortSelector.SelectedItem;
                _mainWindow.ComPortSelector.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                
                // Sync the updated list back to settings window
                SettingComPortSelector.ItemsSource = _mainWindow.ComPortSelector.ItemsSource;
                SettingComPortSelector.SelectedItem = selectedPort;
            }
        }

        private void SettingComPortSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_mainWindow != null && SettingComPortSelector.SelectedItem != null)
            {
                _mainWindow.ComPortSelector.SelectedItem = SettingComPortSelector.SelectedItem;
                UpdateConnectButtonState();
            }
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

        private void UpdateConnectButtonState()
        {
            if (_mainWindow != null)
            {
                SettingConnectButton.Content = _mainWindow.ConnectButton.Content;
                SettingConnectButton.IsEnabled = _mainWindow.ConnectButton.IsEnabled;
            }
        }

        private void SettingBaudRateSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settings == null || SettingBaudRateSelector.SelectedItem == null)
                return;

            var selectedItem = SettingBaudRateSelector.SelectedItem as ComboBoxItem;
            if (selectedItem?.Tag != null && int.TryParse(selectedItem.Tag.ToString(), out int baudRate))
            {
                _settings.BaudRate = baudRate;
#if DEBUG
                Debug.WriteLine($"[Settings] Baud rate changed to: {baudRate}");
#endif
            }
        }

        private void SetBaudRateSelection(int baudRate)
        {
            // Find and select the matching baud rate in the ComboBox
            foreach (ComboBoxItem item in SettingBaudRateSelector.Items)
            {
                if (item.Tag != null && int.TryParse(item.Tag.ToString(), out int itemBaudRate))
                {
                    if (itemBaudRate == baudRate)
                    {
                        SettingBaudRateSelector.SelectedItem = item;
                        return;
                    }
                }
            }

            // Default to 9600 if not found
            foreach (ComboBoxItem item in SettingBaudRateSelector.Items)
            {
                if (item.Tag?.ToString() == "9600")
                {
                    SettingBaudRateSelector.SelectedItem = item;
                    return;
                }
            }
        }

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
                // CRITICAL FIX: Load from MainWindow's settings manager which uses the active profile
                if (_mainWindow != null)
                {
                    var settings = _mainWindow.GetCurrentSettings();
                    if (settings != null)
                    {
#if DEBUG
                        Debug.WriteLine($"[SettingsWindow] Loaded settings from active profile - Opacity: {settings.OverlayOpacity}");
#endif
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

            // Save button configuration
            SaveButtonConfiguration();

            try
            {
#if DEBUG
                Debug.WriteLine($"[SettingsWindow] Saving overlay settings - Opacity: {_settings.OverlayOpacity}, Enabled: {_settings.OverlayEnabled}");
#endif

                // Apply the updated settings to the main window and overlay
                // UpdateOverlaySettings will now save to the active profile
                _mainWindow?.UpdateOverlaySettings(_settings);

#if DEBUG
                Debug.WriteLine("[SettingsWindow] Overlay settings saved to profile via UpdateOverlaySettings");
#endif
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

        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            {
                try { DragMove(); } catch { }
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        #region Button Configuration

        /// <summary>
        /// Loads button configuration from settings and initializes UI.
        /// Buttons are now auto-detected (10000/10001 values), so we show all 8 slots for configuration.
        /// </summary>
        private void LoadButtonConfiguration()
        {
            try
            {
                // Always show 8 button slots (max supported)
                // Users can configure ahead of time; only buttons detected from hardware will activate
                LoadButtonMappingSlots();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] LoadButtonConfiguration: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads 8 button mapping slots for configuration.
        /// Buttons are auto-detected from hardware (10000/10001 protocol).
        /// </summary>
        private void LoadButtonMappingSlots()
        {
            _buttonMappings.Clear();

            // Show all 8 button slots (users can configure ahead of time)
            const int maxButtons = 8;

            for (int i = 0; i < maxButtons; i++)
            {
                var existingMapping = _settings?.ButtonMappings?.FirstOrDefault(m => m.ButtonIndex == i);

                var viewModel = new ButtonMappingViewModel
                {
                    ButtonIndex = i,
                    Action = existingMapping?.Action ?? ButtonAction.None,
                    TargetChannelIndex = existingMapping?.TargetChannelIndex ?? -1
                };

                _buttonMappings.Add(viewModel);
            }

            // Set ItemsSource if the control is initialized
            if (ButtonMappingsItemsControl != null)
            {
                ButtonMappingsItemsControl.ItemsSource = _buttonMappings;
            }
        }

        /// <summary>
        /// Handles button action selection changes to enable/disable channel selector.
        /// </summary>
        private void ButtonAction_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // The binding handles this automatically via NeedsTargetChannel property
        }

        /// <summary>
        /// Validates that only numbers can be entered in numeric text boxes.
        /// </summary>
        private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
        {
            Regex regex = new Regex("[^0-9]+");
            e.Handled = regex.IsMatch(e.Text);
        }

        /// <summary>
        /// Saves button configuration to settings.
        /// Only saves button mappings that have actions assigned.
        /// Buttons are auto-detected from hardware (10000/10001 protocol).
        /// </summary>
        private void SaveButtonConfiguration()
        {
            _settings.ButtonMappings = new List<ButtonMapping>();

            // Only save button mappings that have actions configured
            foreach (var viewModel in _buttonMappings.Where(vm => vm.Action != ButtonAction.None))
            {
                _settings.ButtonMappings.Add(new ButtonMapping
                {
                    ButtonIndex = viewModel.ButtonIndex,
                    Action = viewModel.Action,
                    TargetChannelIndex = viewModel.TargetChannelIndex,
                    FriendlyName = $"Button {viewModel.ButtonIndex + 1}"
                });
            }
        }

        #endregion

        #region Button Mapping View Model

        /// <summary>
        /// View model for button mapping UI.
        /// </summary>
        public class ButtonMappingViewModel : INotifyPropertyChanged
        {
            private int _buttonIndex;
            private ButtonAction _action;
            private int _targetChannelIndex;

            public int ButtonIndex
            {
                get => _buttonIndex;
                set
                {
                    _buttonIndex = value;
                    OnPropertyChanged(nameof(ButtonIndex));
                    OnPropertyChanged(nameof(ButtonIndexDisplay));
                }
            }

            public string ButtonIndexDisplay => $"Button {ButtonIndex + 1}";

            public ButtonAction Action
            {
                get => _action;
                set
                {
                    _action = value;
                    OnPropertyChanged(nameof(Action));
                    OnPropertyChanged(nameof(ActionIndex));
                    OnPropertyChanged(nameof(NeedsTargetChannel));
                }
            }

            public int ActionIndex
            {
                get => (int)_action;
                set
                {
                    _action = (ButtonAction)value;
                    OnPropertyChanged(nameof(Action));
                    OnPropertyChanged(nameof(ActionIndex));
                    OnPropertyChanged(nameof(NeedsTargetChannel));
                }
            }

            public int TargetChannelIndex
            {
                get => _targetChannelIndex;
                set
                {
                    _targetChannelIndex = value;
                    OnPropertyChanged(nameof(TargetChannelIndex));
                    OnPropertyChanged(nameof(TargetChannelDisplay));
                }
            }

            public string TargetChannelDisplay
            {
                get => _targetChannelIndex >= 0 ? (_targetChannelIndex + 1).ToString() : "";
                set
                {
                    if (int.TryParse(value, out int channelNum) && channelNum > 0)
                    {
                        TargetChannelIndex = channelNum - 1;
                    }
                    else
                    {
                        TargetChannelIndex = -1;
                    }
                }
            }

            public bool NeedsTargetChannel => _action == ButtonAction.MuteChannel;

            public event PropertyChangedEventHandler? PropertyChanged;

            protected void OnPropertyChanged(string propertyName)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        #endregion
    }
}
