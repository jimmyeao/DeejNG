using DeejNG.Classes;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace DeejNG.Dialogs
{
    /// <summary>
    /// In-app settings dialog content hosted by MaterialDesign DialogHost.
    /// Mirrors SettingsWindow but as a UserControl.
    /// </summary>
    public partial class SettingsDialog : UserControl
    {
        #region Private Fields

        private readonly string _settingsPath;
        private MainWindow _mainWindow;
        private AppSettings _settings;

        #endregion Private Fields

        #region Public Constructors

        public SettingsDialog()
        {
            InitializeComponent();

            _settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeejNG", "settings.json");

            _mainWindow = Application.Current.MainWindow as MainWindow;
            _settings = LoadSettings();

            OverlayEnabledCheckBox.IsChecked = _settings.OverlayEnabled;
            OpacitySlider.Value = _settings.OverlayOpacity;
            AutoCloseCheckBox.IsChecked = _settings.OverlayTimeoutSeconds > 0;
            TimeoutSlider.Value = _settings.OverlayTimeoutSeconds;
            SetTextColorSelection(_settings.OverlayTextColor);

            OpacitySlider.ValueChanged += OpacitySlider_ValueChanged;
            AutoCloseCheckBox.Checked += AutoCloseCheckBox_Changed;
            AutoCloseCheckBox.Unchecked += AutoCloseCheckBox_Changed;
            TimeoutSlider.ValueChanged += TimeoutSlider_ValueChanged;
            OverlayEnabledCheckBox.Checked += OverlayEnabledCheckBox_Changed;
            OverlayEnabledCheckBox.Unchecked += OverlayEnabledCheckBox_Changed;
            TextColorComboBox.SelectionChanged += TextColorComboBox_SelectionChanged;
        }

        #endregion Public Constructors

        #region Private Methods

        private void ApplySettingsToOverlay()
        {
            _mainWindow?.UpdateOverlaySettings(_settings);
        }

        private void AutoCloseCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;
            bool isEnabled = AutoCloseCheckBox.IsChecked == true;
            _settings.OverlayTimeoutSeconds = isEnabled ? (int)TimeoutSlider.Value : AppSettings.OverlayNoTimeout;
            ApplySettingsToOverlay();
            if (!isEnabled && _settings.OverlayEnabled)
            {
                ShowOverlayImmediately();
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            var originalSettings = LoadSettings();
            _mainWindow?.UpdateOverlaySettings(originalSettings);
            // Close: if hosted in a dialog framework, caller should close; here we just do nothing.
        }

        private string GetTextColorFromSelection()
        {
            return TextColorComboBox.SelectedIndex switch
            {
                0 => "Auto",
                1 => "White",
                2 => "Black",
                _ => "Auto"
            };
        }

        private void HideOverlayImmediately()
        {
            if (_mainWindow != null)
            {
                Debug.WriteLine("[Settings] Hiding overlay immediately");
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

        private AppSettings LoadSettings()
        {
            try
            {
                if (_mainWindow != null)
                {
                    var settings = _mainWindow.GetCurrentSettings();
                    if (settings != null)
                    {
                        return settings;
                    }
                }

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

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_settings == null) return;
            _settings.OverlayOpacity = e.NewValue;
            ApplySettingsToOverlay();
        }

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

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _settings.OverlayEnabled = OverlayEnabledCheckBox.IsChecked == true;
            _settings.OverlayOpacity = OpacitySlider.Value;
            _settings.OverlayTimeoutSeconds = AutoCloseCheckBox.IsChecked == true ? (int)TimeoutSlider.Value : AppSettings.OverlayNoTimeout;
            _settings.OverlayTextColor = GetTextColorFromSelection();
            try
            {
                _mainWindow?.UpdateOverlaySettings(_settings);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Failed to save settings: {ex.Message}");
                MessageBox.Show("Failed to save settings.");
            }
            // Close handled by caller
        }

        private void SetTextColorSelection(string textColor)
        {
            if (Enum.TryParse<SettingsWindow.TextColorOption>(textColor, true, out var colorOption))
            {
                TextColorComboBox.SelectedIndex = (int)colorOption;
            }
            else
            {
                TextColorComboBox.SelectedIndex = 0;
            }
        }

        private void ShowOverlayImmediately()
        {
            if (_mainWindow != null && _settings.OverlayEnabled)
            {
                _mainWindow.ShowVolumeOverlay();
            }
        }

        private void TextColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settings == null) return;
            _settings.OverlayTextColor = GetTextColorFromSelection();
            ApplySettingsToOverlay();
        }

        private void TimeoutSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_settings == null) return;
            if (AutoCloseCheckBox.IsChecked == true)
            {
                _settings.OverlayTimeoutSeconds = (int)e.NewValue;
                ApplySettingsToOverlay();
            }
        }

        #endregion Private Methods
    }
}
