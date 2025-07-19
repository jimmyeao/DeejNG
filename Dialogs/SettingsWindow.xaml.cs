// Updated SettingsWindow.xaml.cs with position preservation
using DeejNG.Classes;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

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

            // Wire up real-time events
            OpacitySlider.ValueChanged += OpacitySlider_ValueChanged;
            AutoCloseCheckBox.Checked += AutoCloseCheckBox_Changed;
            AutoCloseCheckBox.Unchecked += AutoCloseCheckBox_Changed;
            TimeoutSlider.ValueChanged += TimeoutSlider_ValueChanged;
            OverlayEnabledCheckBox.Checked += OverlayEnabledCheckBox_Changed;
            OverlayEnabledCheckBox.Unchecked += OverlayEnabledCheckBox_Changed;
        }

        private void OverlayEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;

            _settings.OverlayEnabled = OverlayEnabledCheckBox.IsChecked == true;
            ApplySettingsToOverlay();
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_settings == null) return;

            // Preserve current overlay position before applying opacity
            PreserveOverlayPosition();

            _settings.OverlayOpacity = e.NewValue;
            ApplySettingsToOverlay();
        }

        private void AutoCloseCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;

            bool isEnabled = AutoCloseCheckBox.IsChecked == true;
            _settings.OverlayTimeoutSeconds = isEnabled ? (int)TimeoutSlider.Value : 0;
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

        private void PreserveOverlayPosition()
        {
            // Get current overlay position if it exists
            if (_mainWindow?._overlay != null)
            {
                _settings.OverlayX = _mainWindow._overlay.Left;
                _settings.OverlayY = _mainWindow._overlay.Top;
                Debug.WriteLine($"[Settings] Preserved overlay position: ({_settings.OverlayX}, {_settings.OverlayY})");
            }
        }

        private void ApplySettingsToOverlay()
        {
            if (_mainWindow != null)
            {
                // Preserve position before updating
                PreserveOverlayPosition();

                // Update main window settings
                _mainWindow.UpdateOverlaySettings(_settings);

                // If overlay exists, update it directly with preserved position
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
            catch { }

            return new AppSettings();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            // Preserve current position before final save
            PreserveOverlayPosition();

            // Update final settings
            _settings.OverlayEnabled = OverlayEnabledCheckBox.IsChecked == true;
            _settings.OverlayOpacity = OpacitySlider.Value;
            _settings.OverlayTimeoutSeconds = AutoCloseCheckBox.IsChecked == true ? (int)TimeoutSlider.Value : 0;

            try
            {
                string json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
                Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
                File.WriteAllText(_settingsPath, json);

                Debug.WriteLine($"[Settings] Final save - Position: ({_settings.OverlayX}, {_settings.OverlayY}), Opacity: {_settings.OverlayOpacity}");

                // Update MainWindow's _appSettings for future saves
                if (_mainWindow != null)
                {
                    _mainWindow.UpdateOverlaySettings(_settings);
                }
            }
            catch
            {
                MessageBox.Show("Failed to save settings.");
            }

            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            // Reload original settings to undo any real-time changes
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