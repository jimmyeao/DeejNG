// MainWindow.xaml.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;

using System.Runtime;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using DeejNG.Dialogs;
using DeejNG.Services;
using Microsoft.VisualBasic.Logging;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace DeejNG
{
    public partial class MainWindow : Window
    {
        #region Private Fields

        private AudioService _audioService;
        private bool _metersEnabled = true;
        private List<ChannelControl> _channelControls = new();
        private bool _isInitializing = true;
        private bool _isConnected = false;
        private DispatcherTimer _meterTimer;
        private StringBuilder _serialBuffer = new();
        private readonly MMDeviceEnumerator _deviceEnumerator = new();
        private SerialPort _serialPort;
        private MMDevice _audioDevice;
        private DateTime _lastSessionRefresh = DateTime.MinValue;
        private SessionCollection _cachedSessions;
        private Dictionary<string, AudioSessionControl> _sessionLookup = new();
        private List<(AudioSessionControl session, string sessionId, string instanceId)> _sessionIdCache = new();
        private DateTime _lastDeviceRefresh = DateTime.MinValue;

        // Track connection state

        private bool isDarkTheme = false;

        #endregion Private Fields

        #region Public Constructors

        public MainWindow()
        {
            _isInitializing = true;
            InitializeComponent();

            // Hook into the Loaded event to safely access SliderScrollViewer
            this.Loaded += MainWindow_Loaded;

            string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Square150x150Logo.scale-200.ico");

            _audioService = new AudioService();
            LoadAvailablePorts();
            _audioDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            _meterTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _meterTimer.Tick += UpdateMeters;
            _meterTimer.Start();

            MyNotifyIcon.Icon = new System.Drawing.Icon(iconPath);
            CreateNotifyIconContextMenu();
            IconHandler.AddIconToRemovePrograms("DeejNG");
            SetDisplayIcon();
            LoadSettings();
            _isInitializing = false;
        }
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            SliderScrollViewer.Visibility = Visibility.Visible;
        }


        private static void SetDisplayIcon()
        {
            //only run in Release

            try
            {
                // executable file
                var exePath = Environment.ProcessPath;
                if (!System.IO.File.Exists(exePath))
                {
                    return;
                }

                //DisplayIcon == "dfshim.dll,2" => 
                var myUninstallKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                string[]? mySubKeyNames = myUninstallKey?.GetSubKeyNames();
                for (int i = 0; i < mySubKeyNames?.Length; i++)
                {
                    RegistryKey? myKey = myUninstallKey?.OpenSubKey(mySubKeyNames[i], true);
                    // ClickOnce(Publish)
                    // Publish -> Settings -> Options 
                    // Publish Options -> Description -> Product name (is your DisplayName)
                    var displayName = (string?)myKey?.GetValue("DisplayName");
                    if (displayName?.Contains("YourApp") == true)
                    {
                        myKey?.SetValue("DisplayIcon", exePath + ",0");
                        break;
                    }
                }
                DeejNG.Settings.Default.IsFirstRun = false;
                DeejNG.Settings.Default.Save();
            }
            catch { }

        }

        #endregion Public Constructors

        #region Private Properties

        private string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeejNG", "settings.json");

        #endregion Private Properties

        #region Protected Methods

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            try
            {
                if (_serialPort != null)
                {
                    _serialPort.DataReceived -= SerialPort_DataReceived;

                    if (_serialPort.IsOpen)
                    {
                        _serialPort.Close();
                    }

                    _serialPort.Dispose();
                    _serialPort = null;
                }

                _isConnected = false;
                UpdateConnectionStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error while closing serial port: {ex.Message}", "Cleanup Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }


        #endregion Protected Methods

        #region Private Methods
        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                // Only hide the window and show the NotifyIcon when minimized
                this.Hide();
                MyNotifyIcon.Visibility = Visibility.Visible;
            }
            else
            {
                // Ensure the NotifyIcon is hidden when the window is not minimized
                MyNotifyIcon.Visibility = Visibility.Collapsed;
            }

            base.OnStateChanged(e);
        }
        private void RefreshSessionLookup()
        {
            _sessionIdCache.Clear();

            for (int i = 0; i < _cachedSessions.Count; i++)
            {
                var session = _cachedSessions[i];

                try
                {
                    string sessionId = session.GetSessionIdentifier ?? string.Empty;
                    string instanceId = session.GetSessionInstanceIdentifier ?? string.Empty;

                    _sessionIdCache.Add((session, sessionId.ToLower(), instanceId.ToLower()));

                    // Debug: log what we're caching
                   // Debug.WriteLine($"[Session] ID: {sessionId}, Instance: {instanceId}");
                }
                catch
                {
                    // Skip bad sessions
                }
            }
        }


        public List<string> GetCurrentTargets()
        {
            return _channelControls
                .Select(c => c.TargetExecutable?.Trim().ToLowerInvariant())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .ToList();
        }


        private void ComPortSelector_DropDownOpened(object sender, EventArgs e)
        {
            LoadAvailablePorts();  // Re-enumerate COM ports when dropdown is opened
        }

        private void Connect_Click(object sender, RoutedEventArgs e)
        {
            if (ComPortSelector.SelectedItem is string selectedPort)
            {
                InitSerial(selectedPort, 9600);
            }
        }

        private void CreateNotifyIconContextMenu()
        {

            try
            {
                ContextMenu contextMenu = new ContextMenu();

                // Show/Hide Window
                MenuItem showHideMenuItem = new MenuItem();
                showHideMenuItem.Header = "Show/Hide";
                showHideMenuItem.Click += ShowHideMenuItem_Click;

                // Exit
                MenuItem exitMenuItem = new MenuItem();
                exitMenuItem.Header = "Exit";
                exitMenuItem.Click += ExitMenuItem_Click;

                contextMenu.Items.Add(showHideMenuItem);

                contextMenu.Items.Add(new Separator()); // Separator before exit
                contextMenu.Items.Add(exitMenuItem);

                MyNotifyIcon.ContextMenu = contextMenu;
            }
            catch (Exception ex)
            {

            }
        }

        private async void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {

            Application.Current.Shutdown();
        }

        private void GenerateSliders(int count)
        {
            SliderPanel.Children.Clear();
            _channelControls.Clear();

            var savedTargets = LoadSettingsFromDisk()?.Targets ?? new List<string>();

            for (int i = 0; i < count; i++)
            {
                var control = new ChannelControl();

                if (i == 0)
                    control.SetTargetExecutable("system");
                else if (i < savedTargets.Count)
                    control.SetTargetExecutable(savedTargets[i]);

                control.TargetChanged += (_, _) => SaveSettings();

                control.VolumeOrMuteChanged += (target, vol, mute) =>
                {
                    _audioService.ApplyVolumeToTarget(target, vol, mute); // ✅ important
                };

                _channelControls.Add(control);
                SliderPanel.Children.Add(control);
            }


            bool show = ShowSlidersCheckBox.IsChecked ?? true;
            SetMeterVisibilityForAll(show);
        }



        private void HandleSliderData(string data)
        {
            string[] parts = data.Split('|');

            Dispatcher.Invoke(() =>
            {
                if (_channelControls.Count != parts.Length)
                {
                    GenerateSliders(parts.Length);
                }

                for (int i = 0; i < parts.Length; i++)
                {
                    if (float.TryParse(parts[i].Trim(), out float level))
                    {
                        // Normalize the level between 0 and 1
                        level = Math.Clamp(level / 1023f, 0f, 1f);

                        // Invert the level if InvertSliderCheckBox is checked
                        if (InvertSliderCheckBox.IsChecked ?? false)
                        {
                            level = 1f - level;  // Invert the level for physical volume
                        }

                        float currentVolume = _channelControls[i].CurrentVolume;
                        if (Math.Abs(currentVolume - level) >= 0.01f)
                        {
                            _channelControls[i].SmoothAndSetVolume(level);

                            var target = _channelControls[i].TargetExecutable?.Trim();
                            if (!string.IsNullOrEmpty(target))
                            {
                                _audioService.ApplyVolumeToTarget(target, level);
                            }
                        }
                    }
                }
            });
        }


        private void InitSerial(string portName, int baudRate)
        {
            try
            {
                if (_serialPort != null && _serialPort.IsOpen)
                {
                    _serialPort.Close();
                }

                _serialPort = new SerialPort(portName, baudRate);
                _serialPort.DataReceived += SerialPort_DataReceived;
                _serialPort.Open();

                _isConnected = true;
                UpdateConnectionStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open serial port {portName}: {ex.Message}", "Serial Error", MessageBoxButton.OK, MessageBoxImage.Error);
                _isConnected = false;
                UpdateConnectionStatus();
            }
        }

        private void LoadAvailablePorts()
        {
            // Re-enumerate the available COM ports
            var availablePorts = SerialPort.GetPortNames();

            // Populate the ComboBox with the newly enumerated ports
            ComPortSelector.ItemsSource = availablePorts;

            // Ensure we select the first available port or leave it blank if none exist
            if (availablePorts.Length > 0)
                ComPortSelector.SelectedIndex = 0;
            else
                ComPortSelector.SelectedIndex = -1;  // No selection if no ports found
        }

        private void LoadSettings()
        {
            var settings = LoadSettingsFromDisk();
            if (!string.IsNullOrWhiteSpace(settings?.PortName))
            {
                InitSerial(settings.PortName, 9600);
            }
            ApplyTheme(settings?.IsDarkTheme == true ? "Dark" : "Light");
            InvertSliderCheckBox.IsChecked = settings?.IsSliderInverted ?? false;
            ShowSlidersCheckBox.IsChecked = settings?.VuMeters ?? true;

            bool showMeters = settings?.VuMeters ?? true;
            ShowSlidersCheckBox.IsChecked = showMeters;
            SetMeterVisibilityForAll(showMeters);

            foreach (var ctrl in _channelControls)
                ctrl.SetMeterVisibility(showMeters);


        }

        private AppSettings LoadSettingsFromDisk()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize<AppSettings>(json);
                   
                }
            }
            catch { }
            return null;
        }

        private void MainWindow_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                // Hide the window and show the NotifyIcon when minimized
                this.Hide();
                MyNotifyIcon.Visibility = Visibility.Visible;
            }
            else
            {
                // Ensure the NotifyIcon is hidden when the window is not minimized
                MyNotifyIcon.Visibility = Visibility.Collapsed;
            }
        }
        private void MyNotifyIcon_Click(object sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized)
            {
                // Restore the window if it's minimized
                this.Show();
                this.WindowState = WindowState.Normal;
            }
            else
            {
                // Minimize the window if it's currently normal or maximized
                this.WindowState = WindowState.Minimized;
            }
        }
        private void SaveSettings()
        {
            if (_isInitializing) return;
            try
            {
                var settings = new AppSettings
                {
                    PortName = _serialPort?.PortName ?? string.Empty,
                    Targets = _channelControls.Select(c => c.TargetExecutable?.Trim() ?? string.Empty).ToList(),
                    IsDarkTheme = isDarkTheme,
                    IsSliderInverted = InvertSliderCheckBox.IsChecked ?? false,
                    VuMeters = ShowSlidersCheckBox.IsChecked ?? true
                };


                var json = JsonSerializer.Serialize(settings);
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (_serialPort == null || !_serialPort.IsOpen) return;

            try
            {
                string incoming = _serialPort.ReadExisting();
                _serialBuffer.Append(incoming);

                while (true)
                {
                    string buffer = _serialBuffer.ToString();
                    int newLineIndex = buffer.IndexOf('\n');
                    if (newLineIndex == -1) break;

                    string line = buffer.Substring(0, newLineIndex).Trim();
                    _serialBuffer.Remove(0, newLineIndex + 1);

                    Dispatcher.BeginInvoke(() => HandleSliderData(line));
                }
            }
            catch (IOException) { }
            catch (InvalidOperationException) { }
        }

        private void ShowHideMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (this.IsVisible)
            {
                this.Hide();
            }
            else
            {
                this.Show();
                this.WindowState = WindowState.Normal;
            }
        }
        private void SetMeterVisibilityForAll(bool show)
        {
            _metersEnabled = show;

            foreach (var ctrl in _channelControls)
            {
                ctrl.SetMeterVisibility(show);
            }
        }

        private void UpdateConnectionStatus()
        {
            // Update the text block with connection status
            ConnectionStatus.Text = _isConnected ? $"Connected to {_serialPort.PortName}" : "Disconnected";

            // Disable the Connect button if connected
            ConnectButton.IsEnabled = !_isConnected;
        }

        private void UpdateMeters(object? sender, EventArgs e)
        {
            if (!_metersEnabled)
                return;

            if ((DateTime.Now - _lastDeviceRefresh).TotalSeconds > 5)
            {
                _audioDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _lastDeviceRefresh = DateTime.Now;
            }

            if ((DateTime.Now - _lastSessionRefresh).TotalSeconds > 2)
            {
                _cachedSessions = _audioDevice.AudioSessionManager.Sessions;
                _lastSessionRefresh = DateTime.Now;
                RefreshSessionLookup();
            }

            const float visualGain = 1.5f;
            const float systemCalibrationFactor = 2.0f;

            for (int i = 0; i < _channelControls.Count; i++)
            {
                var ctrl = _channelControls[i];
                var target = ctrl.TargetExecutable?.Trim().ToLower();

                if (string.IsNullOrWhiteSpace(target) || target == "system")
                {
                    // Apply system mute
                

                    float systemVolume = _audioDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                    float peak = _audioDevice.AudioMeterInformation.MasterPeakValue;

                    float boosted = ctrl.IsMuted ? 0 : Math.Min(peak * systemVolume * systemCalibrationFactor * visualGain, 1.0f);
                    ctrl.UpdateAudioMeter(boosted);
                    continue;
                }

                var match = _sessionIdCache.FirstOrDefault(tuple =>
                    tuple.sessionId.Contains(target) || tuple.instanceId.Contains(target));

                if (match.session != null)
                {
                    try
                    {
                        // Apply session mute
                        if (match.session.SimpleAudioVolume.Mute != ctrl.IsMuted)
                        {
                            match.session.SimpleAudioVolume.Mute = ctrl.IsMuted;
                        }

                        float peak = match.session.AudioMeterInformation.MasterPeakValue;
                        float sliderVol = ctrl.CurrentVolume;
                        float boosted = ctrl.IsMuted ? 0 : Math.Min(peak * sliderVol * visualGain, 1.0f);
                        ctrl.UpdateAudioMeter(boosted);
                    }
                    catch
                    {
                        ctrl.UpdateAudioMeter(0);
                    }
                }
                else
                {
                    ctrl.UpdateAudioMeter(0);
                }
            }

            Dispatcher.BeginInvoke(() => SliderPanel.InvalidateVisual(), DispatcherPriority.Render);
        }


        private void ShowSlidersCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            foreach (var ctrl in _channelControls)
                ctrl.SetMeterVisibility(true);
            SetMeterVisibilityForAll(true);
            SaveSettings();
        }

        private void ShowSlidersCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            foreach (var ctrl in _channelControls)
                ctrl.SetMeterVisibility(false);
            SetMeterVisibilityForAll(false);
            SaveSettings();
        }



        #endregion Private Methods

        #region Private Classes

        private class AppSettings
        {
            public string? PortName { get; set; }
            public List<string> Targets { get; set; } = new();
            public bool IsDarkTheme { get; set; }
            public bool IsSliderInverted { get; set; }

            public bool VuMeters { get; set; } = true;
        }


        #endregion Private Classes

        private void ApplyTheme(string theme)
        {
            isDarkTheme = theme == "Dark";
            Uri themeUri;
            if (theme == "Dark")
            {
                themeUri = new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Dark.xaml");
                isDarkTheme = true;
            }
            else
            {
                themeUri = new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Light.xaml");
                isDarkTheme = false;
            }

            // Update the theme
            var existingTheme = Application.Current.Resources.MergedDictionaries.FirstOrDefault(d => d.Source == themeUri);
            if (existingTheme == null)
            {
                existingTheme = new ResourceDictionary() { Source = themeUri };
                Application.Current.Resources.MergedDictionaries.Add(existingTheme);
            }

            // Remove the other theme
            var otherThemeUri = isDarkTheme
                ? new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Light.xaml")
                : new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Dark.xaml");

            var currentTheme = Application.Current.Resources.MergedDictionaries.FirstOrDefault(d => d.Source == otherThemeUri);
            if (currentTheme != null)
            {
                Application.Current.Resources.MergedDictionaries.Remove(currentTheme);
            }
        }

        private void InvertSlider_Checked(object sender, RoutedEventArgs e)
        {
            SaveInvertState();
        }

        private void InvertSlider_Unchecked(object sender, RoutedEventArgs e)
        {

            SaveInvertState();
        }
        private void SaveInvertState()
        {
            try
            {
                var settings = LoadSettingsFromDisk() ?? new AppSettings();
                settings.IsSliderInverted = InvertSliderCheckBox.IsChecked ?? false;
                var json = JsonSerializer.Serialize(settings);
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving inversion settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            isDarkTheme = !isDarkTheme;
            string theme = isDarkTheme ? "Dark" : "Light";
            ApplyTheme(theme);
            SaveSettings();

        }
    }
    static class IconHandler
    {
        #region Private Properties

        static string IconPath => Path.Combine(AppContext.BaseDirectory, "icon.ico");

        #endregion Private Properties

        #region Public Methods

        public static void AddIconToRemovePrograms(string productName)
        {
            try
            {
                // Ensure the icon exists
                if (File.Exists(IconPath))
                {
                    // Open the Uninstall registry key
                    var uninstallKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", writable: true);
                    if (uninstallKey != null)
                    {
                        foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                        {
                            using (var subKey = uninstallKey.OpenSubKey(subKeyName, writable: true))
                            {
                                if (subKey == null) continue;

                                // Check the display name of the application
                                var displayName = subKey.GetValue("DisplayName") as string;
                                if (string.Equals(displayName, productName, StringComparison.OrdinalIgnoreCase))
                                {
                                    // Set the DisplayIcon value
                                    subKey.SetValue("DisplayIcon", IconPath);
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Handle errors gracefully
                Console.WriteLine($"Error setting uninstall icon: {ex.Message}");
            }
        }

        #endregion Public Methods
    }
}
