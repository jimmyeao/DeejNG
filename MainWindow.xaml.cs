// MainWindow.xaml.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using Microsoft.Win32;

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
using NAudio.CoreAudioApi.Interfaces;
using DeejNG.Classes;

namespace DeejNG
{
    public partial class MainWindow : Window
    {
        #region Private Fields
        private readonly Dictionary<string, float> _lastInputVolume = new(StringComparer.OrdinalIgnoreCase);

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
        private Dictionary<int, string> _processNameCache = new();
        private bool _disableSmoothing = false;
        private DateTime _lastDeviceRefresh = DateTime.MinValue;
        private bool _hasSyncedMuteStates = false;
        private AppSettings _appSettings = new();
        private AudioEndpointVolume _systemVolume;
        private Dictionary<string, MMDevice> _inputDeviceMap = new();



        // Track connection state

        private bool isDarkTheme = false;

        #endregion Private Fields

        #region Public Constructors

        public MainWindow()
        {
            _isInitializing = true;

            InitializeComponent();
            Loaded += MainWindow_Loaded;

            string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Square150x150Logo.scale-200.ico");

            _audioService = new AudioService();
            BuildInputDeviceCache();
            LoadAvailablePorts();
            _audioDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _systemVolume = _audioDevice.AudioEndpointVolume;
            _systemVolume.OnVolumeNotification += AudioEndpointVolume_OnVolumeNotification;


            _meterTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(10)
            };
            _meterTimer.Tick += UpdateMeters;
            _audioDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            StartSessionCacheUpdater(); // ← call here

            MyNotifyIcon.Icon = new System.Drawing.Icon(iconPath);
            CreateNotifyIconContextMenu();
            IconHandler.AddIconToRemovePrograms("DeejNG");
            SetDisplayIcon();
            LoadSettings();

            _isInitializing = false;
            if (_appSettings.StartMinimized)
            {
                WindowState = WindowState.Minimized;
                Hide();
                MyNotifyIcon.Visibility = Visibility.Visible;
            }

   
        }
        private void DisableSmoothingCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            _disableSmoothing = true;
            SaveSettings();
        }

        private void DisableSmoothingCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            _disableSmoothing = false;
            SaveSettings();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            SliderScrollViewer.Visibility = Visibility.Visible;
            StartOnBootCheckBox.IsChecked = _appSettings.StartOnBoot;
        }
        private void BuildInputDeviceCache()
        {
            try
            {
                _inputDeviceMap.Clear();
                var devices = new MMDeviceEnumerator()
                    .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

                foreach (var d in devices)
                {
                    var key = d.FriendlyName.Trim().ToLowerInvariant();
                    if (!_inputDeviceMap.ContainsKey(key))
                    {
                        _inputDeviceMap[key] = d;
                    }
                }

                Debug.WriteLine($"[Init] Cached {_inputDeviceMap.Count} input devices.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Init] Failed to build input device cache: {ex.Message}");
            }
        }

        private void AudioEndpointVolume_OnVolumeNotification(AudioVolumeNotificationData data)
        {
            Dispatcher.Invoke(() =>
            {
                var systemControl = _channelControls.FirstOrDefault(c =>
                    string.Equals(c.TargetExecutable, "system", StringComparison.OrdinalIgnoreCase));

                if (systemControl != null)
                {
                  
                    systemControl.SetMuted(data.Muted);
                }
                else
                {
                   
                }
            });
        }
        private void StartOnBootCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            EnableStartup();
            _appSettings.StartOnBoot = true;
            SaveSettings();
        }
        private void StartSessionCacheUpdater()
        {
            var sessionTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };

            sessionTimer.Tick += (_, _) =>
            {
                try
                {
                    var sessions = new MMDeviceEnumerator()
                        .GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                        .AudioSessionManager.Sessions;

                    var targets = GetCurrentTargets(); // <- your method that gets all active targets

                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        try
                        {
                            string sid = session.GetSessionIdentifier?.ToLowerInvariant() ?? "";
                            string iid = session.GetSessionInstanceIdentifier?.ToLowerInvariant() ?? "";

                            if (!_sessionIdCache.Any(t => t.sessionId == sid && t.instanceId == iid))
                            {
                                foreach (var target in targets)
                                {
                                    if (sid.Contains(target) || iid.Contains(target))
                                    {
                                        _sessionIdCache.Add((session, sid, iid));

                                       

                                        break;
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                           

                        }
                    }
                }
                catch (Exception ex)
                {
                   

                }
            };

            sessionTimer.Start();
        }



        private void StartOnBootCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            //check the value in _settings
            
            DisableStartup();
            _appSettings.StartOnBoot = false;
            SaveSettings();
        }



        private void EnableStartup()
        {
            string appName = "DeejNG";

            try
            {
                string shortcutPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft", "Windows", "Start Menu", "Programs",
                    "Jimmy White", // ← Publisher name
                    "DeejNG",      // ← Product name
                    "DeejNG.appref-ms");

                if (File.Exists(shortcutPath))
                {
                    using RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                    key?.SetValue(appName, $"\"{shortcutPath}\"", RegistryValueKind.String);
                }
                else
                {
                    MessageBox.Show($"Startup shortcut not found:\n{shortcutPath}", "Startup Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to set startup key: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        private void DisableStartup()
        {
            string appName = "DeejNG";
            try
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                key?.DeleteValue(appName, false);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to delete startup key: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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

            var savedSettings = LoadSettingsFromDisk();
            var savedTargets = savedSettings?.Targets ?? new List<string>();

            _isInitializing = true; // ensure this is explicitly set at start

            for (int i = 0; i < count; i++)
            {
                var control = new ChannelControl();

                string target = (i == 0) ? "system" : (i < savedTargets.Count ? savedTargets[i] : "");
                control.SetTargetExecutable(target);
                if (i < savedSettings.InputModes.Count)
                    control.IsInputMode = savedSettings.InputModes[i];

                control.SetMuted(false);
                control.SetVolume(0.5f);

                control.TargetChanged += (_, _) => SaveSettings();
                control.VolumeOrMuteChanged += (t, vol, mute) =>
                {
                    if (_isInitializing) return;

                    if (control.IsInputMode)
                    {
                        if (!_inputDeviceMap.TryGetValue(t.ToLowerInvariant(), out var mic))
                        {
                            mic = new MMDeviceEnumerator()
                                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                                .FirstOrDefault(d => d.FriendlyName.Equals(t, StringComparison.OrdinalIgnoreCase));

                            if (mic != null)
                                _inputDeviceMap[t.ToLowerInvariant()] = mic;
                        }

                        if (mic != null)
                        {
                            try
                            {
                                mic.AudioEndpointVolume.Mute = mute || vol <= 0.01f;
                                mic.AudioEndpointVolume.MasterVolumeLevelScalar = vol;
                                Debug.WriteLine($"[MicVolume] Applied mute={mute} + vol={vol:F2} to {mic.FriendlyName}");
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[MicVolume] Error applying mute: {ex.Message}");
                            }
                        }
                    }
                    else
                    {
                        _audioService.ApplyVolumeToTarget(t, vol, mute);
                    }
                };



                _channelControls.Add(control);
                SliderPanel.Children.Add(control);
            }

            SetMeterVisibilityForAll(ShowSlidersCheckBox.IsChecked ?? true);

            Dispatcher.InvokeAsync(async () =>
            {
                SyncMuteStates();
                _meterTimer.Start();
                _isInitializing = false; // ✅ Set this **last**, after the UI has settled
            });

        }
        private void SyncMuteStates()
        {
            var audioDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = audioDevice.AudioSessionManager.Sessions;

            foreach (var ctrl in _channelControls)
            {
                string target = ctrl.TargetExecutable?.Trim().ToLower();
                bool isMuted = false;

                if (string.IsNullOrEmpty(target))
                {
                    ctrl.SetMuted(false);
                    continue;
                }

                if (target == "system")
                {
                    isMuted = audioDevice.AudioEndpointVolume.Mute;
                    ctrl.SetMuted(isMuted);
                    _audioService.ApplyMuteStateToTarget(target, isMuted);


                    // 🔔 Subscribe to system volume notifications
                    //audioDevice.AudioEndpointVolume.OnVolumeNotification += (data) =>
                    //{
                    //    Dispatcher.Invoke(() =>
                    //    {
                    //        ctrl.SetMuted(data.Muted);
                    //    });
                    //};

                    continue;
                }

                var matchedSession = Enumerable.Range(0, sessions.Count)
                    .Select(i => sessions[i])
                    .FirstOrDefault(s =>
                    {
                        try
                        {
                            var sessionId = s.GetSessionIdentifier?.ToLower() ?? "";
                            var instanceId = s.GetSessionInstanceIdentifier?.ToLower() ?? "";
                            return sessionId.Contains(target) || instanceId.Contains(target);
                        }
                        catch { return false; }
                    });

                if (matchedSession != null)
                {
                    isMuted = matchedSession.SimpleAudioVolume.Mute;

                    // ✅ Register proper IAudioSessionEvents handler
                    try
                    {
                        matchedSession.RegisterEventClient(new AudioSessionEventsHandler(ctrl));


                    }
                    catch (Exception ex)
                    {
                       

                    }

                    ctrl.SetMuted(isMuted);
                    _audioService.ApplyVolumeToTarget(target, ctrl.CurrentVolume, isMuted);
                }
            }
        }


        private void HandleSliderData(string data)
        {
            if (_isInitializing) return;

            string[] parts = data.Split('|');

            Dispatcher.Invoke(() =>
            {
                if (_channelControls.Count != parts.Length)
                {
                    GenerateSliders(parts.Length);
                }

                for (int i = 0; i < parts.Length; i++)
                {
                    if (!float.TryParse(parts[i].Trim(), out float level)) continue;

                    level = Math.Clamp(level / 1023f, 0f, 1f);
                    if (InvertSliderCheckBox.IsChecked ?? false)
                        level = 1f - level;

                    var ctrl = _channelControls[i];
                    var target = ctrl.TargetExecutable?.Trim();

                    if (string.IsNullOrWhiteSpace(target)) continue;

                    float currentVolume = ctrl.CurrentVolume;
                    if (Math.Abs(currentVolume - level) < 0.01f) continue;

                    ctrl.SmoothAndSetVolume(level, suppressEvent: _isInitializing, disableSmoothing: _disableSmoothing);

                    // Don't apply audio if we're still initializing
                    if (_isInitializing) continue;

                    if (ctrl.IsInputMode)
                    {
                        if (!_inputDeviceMap.TryGetValue(target, out var mic))
                        {
                            mic = new MMDeviceEnumerator()
                                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                                .FirstOrDefault(d => d.FriendlyName.Equals(target, StringComparison.OrdinalIgnoreCase));

                            if (mic != null)
                            {
                                _inputDeviceMap[target] = mic;
                                Debug.WriteLine($"[MicVolume] Cached mic: {mic.FriendlyName}");
                            }
                            else
                            {
                                Debug.WriteLine($"[MicVolume] Mic not found for '{target}'");
                            }
                        }

                        if (mic != null)
                        {
                            try
                            {
                                float previous = _lastInputVolume.TryGetValue(target, out var lastVol) ? lastVol : -1f;

                                if (Math.Abs(previous - level) > 0.01f)
                                {
                                    mic.AudioEndpointVolume.Mute = ctrl.IsMuted || level <= 0.01f;

                                    mic.AudioEndpointVolume.MasterVolumeLevelScalar = level;
                                    _lastInputVolume[target] = level;

                                  //  Debug.WriteLine($"[MicVolume] Set input volume to {level:F2} for {mic.FriendlyName}");
                                }
                            }
                            catch (Exception ex)
                            {
                              //  Debug.WriteLine($"[MicVolume] Error: {ex.Message}");
                            }
                        }
                    }
                    else
                    {
                        // ✅ Output mode — apply session volume
                        _audioService.ApplyVolumeToTarget(target, level, ctrl.IsMuted);
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
            DisableSmoothingCheckBox.IsChecked = settings?.DisableSmoothing ?? false;

            // ✅ Unsubscribe events temporarily
            StartOnBootCheckBox.Checked -= StartOnBootCheckBox_Checked;
            StartOnBootCheckBox.Unchecked -= StartOnBootCheckBox_Unchecked;

            bool isInStartup = IsStartupEnabled();
            _appSettings.StartOnBoot = isInStartup;
            StartOnBootCheckBox.IsChecked = isInStartup;

            // ✅ Re-subscribe after setting the value
            StartOnBootCheckBox.Checked += StartOnBootCheckBox_Checked;
            StartOnBootCheckBox.Unchecked += StartOnBootCheckBox_Unchecked;

            StartMinimizedCheckBox.IsChecked = settings?.StartMinimized ?? false;
            StartMinimizedCheckBox.Checked += StartMinimizedCheckBox_Checked;
            foreach (var ctrl in _channelControls)
                ctrl.SetMeterVisibility(showMeters);
        }

        private bool IsStartupEnabled()
        {
            const string appName = "DeejNG";
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            var value = key?.GetValue(appName) as string;
            return !string.IsNullOrEmpty(value);
        }

        private AppSettings LoadSettingsFromDisk()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    _appSettings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                    return _appSettings; // ✅ return the same reference
                }
            }
            catch { }

            _appSettings = new AppSettings();
            return _appSettings;
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
                this.Show();
                this.WindowState = WindowState.Normal;

                // ✅ Force WPF to recalculate layout now that we're visible
                this.InvalidateMeasure();
                this.UpdateLayout();
            }
            else
            {
                this.WindowState = WindowState.Minimized;
                this.Hide();
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
                    VuMeters = ShowSlidersCheckBox.IsChecked ?? true,
                    StartOnBoot = StartOnBootCheckBox.IsChecked ?? false,
                    StartMinimized = StartMinimizedCheckBox.IsChecked ?? false,
                    InputModes = _channelControls.Select(c => c.IsInputMode).ToList(),

                    DisableSmoothing = DisableSmoothingCheckBox.IsChecked ?? false

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
            if (!_metersEnabled) return;

            const float visualGain = 1.5f;
            const float systemCalibrationFactor = 2.0f;

            // Only refresh audio device every 5 seconds to save CPU
            if ((DateTime.Now - _lastDeviceRefresh).TotalSeconds > 5)
            {
                _audioDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _lastDeviceRefresh = DateTime.Now;
            }

            var sessions = _audioDevice.AudioSessionManager.Sessions;

            foreach (var ctrl in _channelControls)
            {
                var target = ctrl.TargetExecutable?.Trim().ToLower();
                if (string.IsNullOrWhiteSpace(target))
                {
                    ctrl.UpdateAudioMeter(0);
                    continue;
                }

                if (target == "system")
                {
                    float peak = _audioDevice.AudioMeterInformation.MasterPeakValue;
                    float systemVol = _audioDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                    float boosted = ctrl.IsMuted ? 0 : Math.Min(peak * systemVol * systemCalibrationFactor * visualGain, 1.0f);
                    ctrl.UpdateAudioMeter(boosted);
                    continue;
                }

                AudioSessionControl? matchingSession = null;

                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    try
                    {
                        string sid = s.GetSessionIdentifier?.ToLowerInvariant() ?? "";
                        string iid = s.GetSessionInstanceIdentifier?.ToLowerInvariant() ?? "";
                        int pid = (int)s.GetProcessID;

                        if (!_processNameCache.TryGetValue(pid, out string procName))
                        {
                            try
                            {
                                procName = Process.GetProcessById(pid).ProcessName.ToLowerInvariant();
                                _processNameCache[pid] = procName;
                            }
                            catch
                            {
                                procName = "";
                                _processNameCache[pid] = procName;
                            }
                        }



                        var sidFile = Path.GetFileNameWithoutExtension(sid);
                        var iidFile = Path.GetFileNameWithoutExtension(iid);

                        if (sidFile == target || iidFile == target || procName == target ||
                            sid.Contains(target) || iid.Contains(target))
                        {
                            matchingSession = s;
                            break;
                        }
                    }
                    catch { }
                }

                if (matchingSession != null)
                {
                    try
                    {
                        float peak = matchingSession.AudioMeterInformation.MasterPeakValue;
                        float boosted = ctrl.IsMuted ? 0 : Math.Min(peak * ctrl.CurrentVolume * visualGain, 1.0f);
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

            Dispatcher.InvokeAsync(() => SliderPanel.InvalidateVisual(), DispatcherPriority.Render);
        }




        private void StartMinimizedCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _appSettings.StartMinimized = true;
            SaveSettings();
        }

        private void StartMinimizedCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _appSettings.StartMinimized = false;
            SaveSettings();
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
            public List<bool> MuteStates { get; set; } = new();
            public bool IsDarkTheme { get; set; }
            public bool IsSliderInverted { get; set; }
            public bool VuMeters { get; set; } = true;
            public bool StartOnBoot { get; set; }
            public bool StartMinimized { get; set; } = false;
            public bool DisableSmoothing { get; set; }
            // 👇 ADD THIS:
            public List<bool> InputModes { get; set; } = new();
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
