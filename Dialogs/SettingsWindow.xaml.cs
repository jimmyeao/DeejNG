using DeejNG.Classes;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace DeejNG.Dialogs
{
    public partial class SettingsWindow : Window
    {
        private AppSettings _settings;
        private readonly string _settingsPath;

        public SettingsWindow()
        {
            InitializeComponent();

            _settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeejNG", "settings.json");

            _settings = LoadSettings();

            OverlayEnabledCheckBox.IsChecked = _settings.OverlayEnabled;
            OpacitySlider.Value = _settings.OverlayOpacity;
            AutoCloseCheckBox.IsChecked = _settings.OverlayTimeoutSeconds > 0;
            TimeoutSlider.Value = _settings.OverlayTimeoutSeconds;
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
            _settings.OverlayEnabled = OverlayEnabledCheckBox.IsChecked == true;
            _settings.OverlayOpacity = OpacitySlider.Value;

            _settings.OverlayTimeoutSeconds = AutoCloseCheckBox.IsChecked == true ? (int)TimeoutSlider.Value : 0;

            try
            {
                string json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
                Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
                File.WriteAllText(_settingsPath, json);
            }
            catch
            {
                MessageBox.Show("Failed to save settings.");
            }

            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
