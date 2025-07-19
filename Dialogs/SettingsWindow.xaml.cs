using DeejNG.Classes;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace DeejNG.Dialogs
{
    public partial class SettingsWindow : Window
    {
        private AppSettings _settings;
        private readonly string _settingsPath;
        private MainWindow _mainWindow;

        public SettingsWindow()
        {
            InitializeComponent();

            _settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeejNG", "settings.json");

            _mainWindow = Application.Current.MainWindow as MainWindow;
            _settings = LoadSettings();

            // Initialize controls
            OverlayEnabledCheckBox.IsChecked = _settings.OverlayEnabled;
            OpacitySlider.Value = _settings.OverlayOpacity;
            AutoCloseCheckBox.IsChecked = _settings.OverlayTimeoutSeconds > 0;
            TimeoutSlider.Value = _settings.OverlayTimeoutSeconds;

            // Set text color ComboBox selection
            SetTextColorSelection(_settings.OverlayTextColor);

            // Wire up real-time events
            OpacitySlider.ValueChanged += OpacitySlider_ValueChanged;
            AutoCloseCheckBox.Checked += AutoCloseCheckBox_Changed;
            AutoCloseCheckBox.Unchecked += AutoCloseCheckBox_Changed;
            TimeoutSlider.ValueChanged += TimeoutSlider_ValueChanged;
            OverlayEnabledCheckBox.Checked += OverlayEnabledCheckBox_Changed;
            OverlayEnabledCheckBox.Unchecked += OverlayEnabledCheckBox_Changed;
            TextColorComboBox.SelectionChanged += TextColorComboBox_SelectionChanged;
        }
        public enum TextColorOption
        {
            Auto,
            White,
            Black
        }
        private void SetTextColorSelection(string textColor)
        {
           
            if (Enum.TryParse<TextColorOption>(textColor, true, out var colorOption))
            {
                TextColorComboBox.SelectedIndex = (int)colorOption;
            }
            else
            {
                TextColorComboBox.SelectedIndex = 0; // Default to Auto
            }
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

        private void TextColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settings == null) return;

            _settings.OverlayTextColor = GetTextColorFromSelection();
            ApplySettingsToOverlay();

            Debug.WriteLine($"[Settings] Text color changed to: {_settings.OverlayTextColor}");
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

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_settings == null) return;

            PreserveOverlayPosition();
            _settings.OverlayOpacity = e.NewValue;
            ApplySettingsToOverlay();
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

        private void TimeoutSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_settings == null) return;

            if (AutoCloseCheckBox.IsChecked == true)
            {
                _settings.OverlayTimeoutSeconds = (int)e.NewValue;
                ApplySettingsToOverlay();
            }
        }

        // New method for text color changes


        private void ShowOverlayImmediately()
        {
            if (_mainWindow != null && _settings.OverlayEnabled)
            {
                Debug.WriteLine("[Settings] Showing overlay immediately");
                _mainWindow.ShowVolumeOverlay();

                if (_mainWindow._overlay != null && _mainWindow._channelControls?.Count > 0)
                {
                    var volumes = _mainWindow._channelControls.Select(c => c.CurrentVolume).ToList();
                    var labels = _mainWindow._channelControls.Select(c => _mainWindow.GetChannelLabel(c)).ToList();

                    _mainWindow._overlay.ShowVolumes(volumes, labels);
                    Debug.WriteLine($"[Settings] Overlay shown with {volumes.Count} channels");
                }
            }
        }

        private void HideOverlayImmediately()
        {
            if (_mainWindow?._overlay != null)
            {
                Debug.WriteLine("[Settings] Hiding overlay immediately");
                _mainWindow._overlay.Hide();
            }
        }

        private void PreserveOverlayPosition()
        {
            if (_mainWindow?._overlay != null)
            {
                _settings.OverlayX = Math.Round(_mainWindow._overlay.Left, 1);
                _settings.OverlayY = Math.Round(_mainWindow._overlay.Top, 1);
                Debug.WriteLine($"[Settings] Preserved precise overlay position: ({_settings.OverlayX}, {_settings.OverlayY})");
            }
        }

        private void ApplySettingsToOverlay()
        {
            if (_mainWindow != null)
            {
                PreserveOverlayPosition();
                _mainWindow.UpdateOverlaySettings(_settings);

                if (_mainWindow._overlay != null)
                {
                    _mainWindow._overlay.UpdateSettings(_settings);
                }
            }
        }

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

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            PreserveOverlayPosition();

            _settings.OverlayEnabled = OverlayEnabledCheckBox.IsChecked == true;
            _settings.OverlayOpacity = OpacitySlider.Value;
            _settings.OverlayTimeoutSeconds = AutoCloseCheckBox.IsChecked == true ? (int)TimeoutSlider.Value : AppSettings.OverlayNoTimeout;
            _settings.OverlayTextColor = GetTextColorFromSelection(); // Save text color setting

            try
            {
                string json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });

                var directoryPath = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrEmpty(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                }

                File.WriteAllText(_settingsPath, json);

                Debug.WriteLine($"[Settings] Final save - Text Color: {_settings.OverlayTextColor}");

                if (_mainWindow != null)
                {
                    _mainWindow.UpdateOverlaySettings(_settings);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Failed to save settings: {ex.Message}");
                MessageBox.Show("Failed to save settings.");
            }

            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            var originalSettings = LoadSettings();
            if (_mainWindow != null)
            {
                _mainWindow.UpdateOverlaySettings(originalSettings);

                if (_mainWindow._overlay != null)
                {
                    _mainWindow._overlay.UpdateSettings(originalSettings);
                }
            }

            Close();
        }
    }
}